using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

/// <summary>
/// 跨实例设置迁移两阶段 API 的单元测试（空白实例启动卡死修复配套）。
/// 阶段一 ProbeBestSourceAsync：纯 IO 探测（自排除 / Exp 过滤 / 超时）；
/// 阶段二 ApplyProbedValues：DialogPage 回填（依赖 VS 存储，此处不覆盖保存路径）。
/// 所有用例使用临时目录，不触碰真实实例 hive。
/// </summary>
public class SettingsMigrationTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsMigrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"dsmig_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    #region EnumerateCandidateBins

    private string MakeHive(string name, bool withBin = true, DateTime? lastWrite = null)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        if (withBin)
        {
            // 内容为非法 hive：RegLoadAppKey 必然失败，用于验证"失败被优雅跳过"
            File.WriteAllText(Path.Combine(dir, "privateregistry.bin"), "not-a-registry-hive");
            if (lastWrite.HasValue) File.SetLastWriteTime(Path.Combine(dir, "privateregistry.bin"), lastWrite.Value);
        }
        return dir;
    }

    [Fact]
    public void Enumerate_ExcludesExpHives_AndMissingBins()
    {
        MakeHive("17.0_normal");
        MakeHive("17.0_xxxExp");          // Exp 后缀 → 排除
        Directory.CreateDirectory(Path.Combine(_tempRoot, "18.0_nobin")); // 无 bin → 排除

        var result = SettingsMigration.EnumerateCandidateBins(_tempRoot, excludeHiveName: null).ToList();

        result.Should().ContainSingle();
        Path.GetFileName(Path.GetDirectoryName(result[0])).Should().Be("17.0_normal");
    }

    [Fact]
    public void Enumerate_ExcludesOwnHiveByName()
    {
        MakeHive("17.0_normal");
        MakeHive("18.0_ownhive");

        var result = SettingsMigration.EnumerateCandidateBins(_tempRoot, excludeHiveName: "18.0_ownhive").ToList();

        result.Should().ContainSingle();
        Path.GetFileName(Path.GetDirectoryName(result[0])).Should().Be("17.0_normal");
    }

    [Fact]
    public void Enumerate_OrdersByLastWriteTime_Descending()
    {
        var older = MakeHive("17.0_older", lastWrite: DateTime.Now.AddHours(-2));
        var newer = MakeHive("16.0_newer", lastWrite: DateTime.Now.AddHours(-1));

        var result = SettingsMigration.EnumerateCandidateBins(_tempRoot, excludeHiveName: null).ToList();

        result.Should().HaveCount(2);
        result[0].Should().Be(Path.Combine(newer, "privateregistry.bin"));
        result[1].Should().Be(Path.Combine(older, "privateregistry.bin"));
    }

    #endregion

    #region WithTimeoutAsync

    [Fact]
    public async Task WithTimeout_FastFunc_ReturnsValue()
    {
        var result = await SettingsMigration.WithTimeoutAsync(() => "ok", timeoutMs: 2000);
        result.Should().Be("ok");
    }

    [Fact]
    public async Task WithTimeout_SlowFunc_AbandonsAndReturnsDefault()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await SettingsMigration.WithTimeoutAsync(
            () => { Thread.Sleep(5000); return "late"; },
            timeoutMs: 200);

        sw.ElapsedMilliseconds.Should().BeLessThan(3000, "应在超时预算附近放弃等待，而非等慢任务完成");
        result.Should().BeNull();
    }

    #endregion

    #region ProbeBestSourceAsync

    [Fact]
    public async Task Probe_NonexistentBaseDir_ReturnsNullWithoutThrow()
    {
        var ghostDir = Path.Combine(_tempRoot, "does-not-exist");

        var result = await SettingsMigration.ProbeBestSourceAsync(excludeHiveName: null, baseDirOverride: ghostDir);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Probe_AllCandidatesExcluded_ReturnsNullQuickly()
    {
        MakeHive("17.0_a");
        MakeHive("16.0_b");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await SettingsMigration.ProbeBestSourceAsync(excludeHiveName: "17.0_a", baseDirOverride: _tempRoot);

        // 16.0_b 未被排除但 bin 为非法 hive → 读取失败被跳过；整体优雅返回 null
        result.Should().BeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    [Fact]
    public async Task Probe_InvalidHiveContent_GracefullySkipsAndReturnsNull()
    {
        MakeHive("17.0_badcontent");

        var result = await SettingsMigration.ProbeBestSourceAsync(excludeHiveName: null, baseDirOverride: _tempRoot);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Probe_EmptyBaseDir_ReturnsNull()
    {
        var emptyDir = Path.Combine(_tempRoot, "empty-root");
        Directory.CreateDirectory(emptyDir);

        var result = await SettingsMigration.ProbeBestSourceAsync(excludeHiveName: null, baseDirOverride: emptyDir);

        result.Should().BeNull();
    }

    #endregion

    #region HasNoCandidateSource (P1-5a)

    [Fact]
    public void HasNoCandidateSource_EmptyBaseDir_ReturnsTrue()
    {
        var emptyDir = Path.Combine(_tempRoot, "empty-src");
        Directory.CreateDirectory(emptyDir);

        var result = SettingsMigration.HasNoCandidateSource(excludeHiveName: null, baseDirOverride: emptyDir);

        result.Should().BeTrue("空目录 = 确无来源，可固化一次性迁移标志");
    }

    [Fact]
    public void HasNoCandidateSource_MissingBaseDir_ReturnsTrue()
    {
        var ghostDir = Path.Combine(_tempRoot, "missing-src");

        var result = SettingsMigration.HasNoCandidateSource(excludeHiveName: null, baseDirOverride: ghostDir);

        result.Should().BeTrue("根目录不存在 = 确无来源");
    }

    [Fact]
    public void HasNoCandidateSource_OnlyExpHives_ReturnsTrue()
    {
        MakeHive("17.0_xxxExp"); // Exp 后缀被枚举逻辑排除

        var result = SettingsMigration.HasNoCandidateSource(excludeHiveName: null, baseDirOverride: _tempRoot);

        result.Should().BeTrue("只有 Exp hive = 无正式候选来源");
    }

    [Fact]
    public void HasNoCandidateSource_OnlyOwnHiveExcluded_ReturnsTrue()
    {
        MakeHive("18.0_ownhive");

        var result = SettingsMigration.HasNoCandidateSource(excludeHiveName: "18.0_ownhive", baseDirOverride: _tempRoot);

        result.Should().BeTrue("唯一候选被自排除 = 确无来源");
    }

    [Fact]
    public void HasNoCandidateSource_WithCandidate_ReturnsFalse()
    {
        MakeHive("17.0_normal");

        var result = SettingsMigration.HasNoCandidateSource(excludeHiveName: null, baseDirOverride: _tempRoot);

        result.Should().BeFalse("存在候选来源 = 不得判定'确无来源'（避免误固化标志导致永不再迁移）");
    }

    #endregion

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }
}
