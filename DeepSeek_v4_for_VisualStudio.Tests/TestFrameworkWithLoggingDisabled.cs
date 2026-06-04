using DeepSeek_v4_for_VisualStudio.Utils;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace DeepSeek_v4_for_VisualStudio.Tests;

/// <summary>
/// 自定义 XunitTestFramework，在测试程序集加载时关闭 Logger 的文件输出，
/// 避免单元测试日志污染生产日志文件 extension-*.log。
/// </summary>
public class TestFrameworkWithLoggingDisabled : XunitTestFramework
{
    public TestFrameworkWithLoggingDisabled(IMessageSink messageSink)
        : base(messageSink)
    {
        // 在最早时机（测试框架初始化时）关闭文件日志
        Logger.EnableFileLogging = false;
        messageSink.OnMessage(new DiagnosticMessage(
            "[DeepSeek Tests] 已禁用 Logger 文件输出，避免污染生产日志"));
    }
}
