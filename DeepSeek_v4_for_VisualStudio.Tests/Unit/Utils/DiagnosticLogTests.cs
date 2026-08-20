using System.Reflection;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Utils;

public class DiagnosticLogTests
{
    [Fact]
    public void Write_DeletesExpiredDiagnosticLogsAndKeepsOtherFiles()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeepSeekVS-DiagnosticLogTests-{Guid.NewGuid():N}");
        var originalDirectory = GetFieldValue<string>("LogDirectory")!;
        var originalLastCleanupDate = GetFieldValue<DateTime>("_lastCleanupDate");
        var originalDirectoryEnsured = GetFieldValue<bool>("_directoryEnsured");

        try
        {
            Directory.CreateDirectory(tempDirectory);
            SetFieldValue("LogDirectory", tempDirectory);
            SetFieldValue("_lastCleanupDate", DateTime.MinValue);
            SetFieldValue("_directoryEnsured", false);

            var expiredDiagnosticLog = Path.Combine(tempDirectory, "diagnostic-old.log");
            var freshDiagnosticLog = Path.Combine(tempDirectory, "diagnostic-fresh.log");
            var expiredExtensionLog = Path.Combine(tempDirectory, "extension-old.log");
            var expiredDiagnosticText = Path.Combine(tempDirectory, "diagnostic-old.txt");

            File.WriteAllText(expiredDiagnosticLog, "old");
            File.WriteAllText(freshDiagnosticLog, "fresh");
            File.WriteAllText(expiredExtensionLog, "old");
            File.WriteAllText(expiredDiagnosticText, "old");

            var expiredTime = DateTime.Today.AddDays(-15);
            File.SetLastWriteTime(expiredDiagnosticLog, expiredTime);
            File.SetLastWriteTime(expiredExtensionLog, expiredTime);
            File.SetLastWriteTime(expiredDiagnosticText, expiredTime);

            DiagnosticLog.Write("cleanup test");

            File.Exists(expiredDiagnosticLog).Should().BeFalse();
            File.Exists(freshDiagnosticLog).Should().BeTrue();
            File.Exists(expiredExtensionLog).Should().BeTrue();
            File.Exists(expiredDiagnosticText).Should().BeTrue();
            File.Exists(Path.Combine(tempDirectory, $"diagnostic-{DateTime.Now:yyyy-MM-dd}.log"))
                .Should().BeTrue();
        }
        finally
        {
            SetFieldValue("LogDirectory", originalDirectory);
            SetFieldValue("_lastCleanupDate", originalLastCleanupDate);
            SetFieldValue("_directoryEnsured", originalDirectoryEnsured);

            try { Directory.Delete(tempDirectory, true); }
            catch { /* 测试临时目录清理失败不影响断言 */ }
        }
    }

    private static T GetFieldValue<T>(string name)
    {
        var field = typeof(DiagnosticLog).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        field.Should().NotBeNull($"DiagnosticLog should define the private field {name}");
        return (T)field!.GetValue(null)!;
    }

    private static void SetFieldValue<T>(string name, T value)
    {
        var field = typeof(DiagnosticLog).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        field.Should().NotBeNull($"DiagnosticLog should define the private field {name}");
        field!.SetValue(null, value);
    }
}
