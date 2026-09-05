using Microsoft.VisualStudio.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio
{
    /// <summary>
    /// VisualStudio.Extensibility in-proc 扩展宿主。
    /// 与既有 AsyncPackage 共存于同一 VSIX（ExtensionType="VSSDK+VisualStudio.Extensibility"）。
    ///
    /// 说明（2026-09-01）：本壳承载 VS2026 新版设置界面的非敏感 SettingCategory
    /// （DeepSeekUnifiedSettings，"DeepSeek Chat 设置"）。旧选项页保持可见，
    /// 用于配置保存在 Visual Studio Credential Storage 的 API Key。
    /// </summary>
    [VisualStudioContribution]
    internal class DeepSeekExtension : Extension
    {
        public override ExtensionConfiguration? ExtensionConfiguration => new()
        {
            RequiresInProcessHosting = true,
        };

        public DeepSeekExtension()
        {
            DiagnosticLog.Write("[VSEXT] DeepSeekExtension ctor");
        }

        protected override void InitializeServices(IServiceCollection services)
        {
            DiagnosticLog.Write("[VSEXT] InitializeServices enter");
            base.InitializeServices(services);
            services.AddSettingsObservers();
            DiagnosticLog.Write("[VSEXT] InitializeServices exit (AddSettingsObservers done)");
        }
    }
}
