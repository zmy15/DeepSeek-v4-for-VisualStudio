using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// P2-1 回归测试：同秒启动的两个备份会话必须各自持有私有目录，
/// 不得共享目录后以 overwrite:true 互踩对方的备份文件。
/// 修复前：目录名仅含秒级时间戳，同秒会话共用同一目录。
/// </summary>
public class BackupServiceSessionCollisionTests : IDisposable
{
    private readonly string _backupRoot;

    public BackupServiceSessionCollisionTests()
    {
        _backupRoot = Path.Combine(Path.GetTempPath(), $"bs_collision_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_backupRoot);
        BackupService.BaseDirOverride = _backupRoot;
        BackupService.EndSession();
    }

    public void Dispose()
    {
        BackupService.EndSession();
        BackupService.BaseDirOverride = null;
        try { if (Directory.Exists(_backupRoot)) Directory.Delete(_backupRoot, true); } catch { }
    }

    private string CreateSourceFile(string name, string content)
    {
        var dir = Path.Combine(_backupRoot, "sources");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void BeginSession_SecondSessionInSameSecond_GetsDistinctDirectory()
    {
        // 生产场景：并发的两个编辑会话（如两个 Agent 同时落盘）在同一秒内先后 BeginSession。
        // 第一个会话的目录里已有备份文件（未 EndSession，目录非空且存在），
        // 第二个会话必须获得不同的目录，否则 File.Copy(overwrite:true) 互踩备份。
        BackupService.BeginSession();
        var first = BackupService.CurrentSessionDir;
        first.Should().NotBeNull();

        // 让第一个会话目录"非空"（放置一个文件，模拟已有备份）
        Directory.CreateDirectory(first!);
        File.WriteAllText(Path.Combine(first!, "keep.txt"), "occupied");

        // 模拟并发：直接再次调用 BeginSession 是 no-op（单例语义），
        // 因此直接用反射重置 _currentSessionDir 模拟"另一个并发会话"，
        // 磁盘上第一个会话目录仍在 → 第二个 BeginSession 必须撞名并加后缀。
        var field = typeof(BackupService).GetField("_currentSessionDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull();
        field!.SetValue(null, null);

        BackupService.BeginSession();
        var second = BackupService.CurrentSessionDir;

        second.Should().NotBeNull();
        second!.Should().NotBe(first, "同秒会话必须获得互不相同的目录（旧目录已有备份，不得共享互踩）");

        // 旧会话的备份文件必须完好无损
        File.Exists(Path.Combine(first!, "keep.txt")).Should().BeTrue("旧会话的备份不得被新会话破坏");
    }

    [Fact]
    public void BeginSession_PreexistingTimestampDir_GetsSuffixedDirectory()
    {
        // 直接占位"下一个会话将命名到的目录"
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var occupied = Path.Combine(_backupRoot, timestamp);
        Directory.CreateDirectory(occupied);

        BackupService.BeginSession();
        var session = BackupService.CurrentSessionDir;

        session.Should().NotBeNull();
        session!.Should().NotBe(occupied, "被占位的时间戳目录不得复用（避免 overwrite 互踩）");
        // 后缀形式：timestamp_2 / timestamp_3 …
        session.Should().Match(s => s.Contains($"{timestamp}_") || !s.EndsWith(timestamp),
            "撞名时应追加序号后缀");
    }

    [Fact]
    public void CreateBackup_ConcurrentEndSession_DoesNotThrowNre()
    {
        // P2-1 TOCTOU：CreateBackup 锁外读 _currentSessionDir! 与 EndSession 置空竞态。
        // 修复前：Path.Combine(null, …) 抛 NullReferenceException → CreateBackup 返回 null。
        // 修复后：会话目录读取全部在锁内，EndSession 后自动重建会话。
        var file = CreateSourceFile("race.txt", "RACE-CONTENT");

        var results = new List<string?>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var racer = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                results.Add(BackupService.CreateBackup(file));
            }
        });

        var stopper = Task.Run(async () =>
        {
            for (int i = 0; i < 200 && !cts.IsCancellationRequested; i++)
            {
                BackupService.EndSession();
                await Task.Delay(1);
            }
        });

        Task.WaitAll(racer, stopper);

        // 竞态窗口内每次 CreateBackup 都必须成功返回备份路径（修复前会随机返回 null）
        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => r != null, "TOCTOU 修复后并发 EndSession 不得使 CreateBackup 失败");
    }
}
