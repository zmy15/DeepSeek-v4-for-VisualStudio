using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.BuiltInTools;

/// <summary>
/// 前缀契约防碰撞回归测试（三轮评审 B2）：
/// read_file 小文件快速通道此前原样返回文件内容——内容以 "Error: " 开头的文件
/// 会被 Classify / BaseAgent 连续错误检测误判为工具失败，累计后提前终止工具循环。
/// 修复后：契约前缀开头的内容用 &lt;file&gt; 信封包裹，正常内容保持原样。
/// </summary>
public class ReadFileContractMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public ReadFileContractMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rf_marker_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static async Task<string> ReadAsync(string filePath)
    {
        var tool = new ReadFileTool(new ConcurrentDictionary<string, FileReadCacheEntry>());
        var args = new Dictionary<string, JsonElement>();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { filePath }));
        foreach (var prop in doc.RootElement.EnumerateObject())
            args[prop.Name] = prop.Value.Clone();
        return await tool.ExecuteAsync(args, null);
    }

    [Theory]
    [InlineData("Error: something went wrong in this log file")]
    [InlineData("Timeout: operation exceeded 30s")]
    [InlineData("[BLOCKED] dangerous command was intercepted")]
    public async Task SmallFile_StartsWithContractMarker_IsWrapped_NotMisclassified(string content)
    {
        var file = Path.Combine(_tempDir, $"marker_{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, content);

        var result = await ReadAsync(file);

        // 修复核心：结果不得以契约前缀开头（否则被记为工具失败）
        result.Should().StartWith("<file ", "契约前缀开头的内容必须用 <file> 信封包裹以防误判");
        result.Should().Contain(content, "包裹不得破坏原始内容");
        ToolExecutionOutcome.Classify(result).Should().Be(ToolResultKind.Success,
            "读取成功的结果不得被分类为失败（否则累计触发 consecutive_errors 终止循环）");
    }

    [Fact]
    public async Task SmallFile_NormalContent_ReturnedRaw()
    {
        var file = Path.Combine(_tempDir, $"normal_{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "just ordinary content\r\nsecond line");

        var result = await ReadAsync(file);

        // 快速通道语义保持：普通内容原样返回（不引入信封开销）
        result.Should().Be("just ordinary content\r\nsecond line");
        ToolExecutionOutcome.Classify(result).Should().Be(ToolResultKind.Success);
    }

    [Fact]
    public void StartsWithContractMarker_Semantics()
    {
        ToolExecutionOutcome.StartsWithContractMarker(null).Should().BeFalse();
        ToolExecutionOutcome.StartsWithContractMarker("").Should().BeFalse();
        ToolExecutionOutcome.StartsWithContractMarker("Error: x").Should().BeTrue();
        ToolExecutionOutcome.StartsWithContractMarker("Timeout: x").Should().BeTrue();
        ToolExecutionOutcome.StartsWithContractMarker("[BLOCKED] x").Should().BeTrue();
        // 仅整串前缀匹配；内容中间出现不算（Classify 同语义）
        ToolExecutionOutcome.StartsWithContractMarker("some text Error: mid").Should().BeFalse();
        ToolExecutionOutcome.StartsWithContractMarker("Errors: near-miss").Should().BeFalse();
    }
}
