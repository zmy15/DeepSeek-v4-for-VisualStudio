using DeepSeek_v4_for_VisualStudio.Services;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel;
using System.Drawing.Design;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// DeepSeek 选项页，对标共享项目 OptionPageGridGeneral。
    /// 通过 Tools → Options → DeepSeek Chat 访问。
    /// </summary>
    public class DeepSeekOptionsPage : DialogPage
    {
        /// <summary>
        /// 当用户在 Options 对话框中点击"确定"或"应用"时触发。
        /// 订阅此事件可实现设置热切换，无需重启聊天窗口。
        /// </summary>
        public static event Action? SettingsChanged;

        /// <summary>
        /// 全局实例引用，在 Package 初始化时设置，方便静态工具类读取设置。
        /// </summary>
        public static DeepSeekOptionsPage? Instance { get; set; }

        /// <summary>
        /// VS 在用户应用设置更改时调用此方法。
        /// 我们在此触发 SettingsChanged 事件以通知订阅者刷新配置。
        /// </summary>
        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply)
            {
                SettingsChanged?.Invoke();
            }
        }
        [Category("API Settings")]
        [DisplayName("API Key")]
        [Description("DeepSeek API 密钥，从 https://platform.deepseek.com/api_keys 获取")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ApiKey { get; set; } = string.Empty;

        [Category("API Settings")]
        [DisplayName("System Prompt")]
        [Description("系统提示词，定义 AI 助手的行为角色")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string SystemPrompt { get; set; } = AiPrompts.DefaultSystemPrompt;

        [Category("Model Settings")]
        [DisplayName("Selected Model")]
        [Description("使用的 DeepSeek 模型")]
        [TypeConverter(typeof(ModelListConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string SelectedModel { get; set; } = "deepseek-v4-pro";

        [Category("Model Settings")]
        [DisplayName("Enable Deep Thinking")]
        [Description("启用深度思考模式 (Reasoning)")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public bool IsThinkingEnabled { get; set; } = true;

        [Category("Model Settings")]
        [DisplayName("Reasoning Effort")]
        [Description("推理强度: high 或 max")]
        [TypeConverter(typeof(ReasoningEffortConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ReasoningEffort { get; set; } = "high";

        [Category("Web Search")]
        [DisplayName("Enable Web Search")]
        [Description("启用联网搜索功能。启用后可在聊天窗口中使用联网搜索开关。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableWebSearch { get; set; } = true;

        [Category("Web Search")]
        [DisplayName("Search Provider")]
        [Description("选择搜索引擎: Baidu (百度千帆, 需 API Key, 每月1500次免费) 或 DuckDuckGo (完全免费)")]
        [TypeConverter(typeof(SearchProviderConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string SearchProvider { get; set; } = "DuckDuckGo";

        [Category("Web Search")]
        [DisplayName("Baidu Qianfan API Key")]
        [Description("百度千帆 AppBuilder API Key。获取地址: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                     "⚠️ 计费提醒: 每月免费 1500 次（约每天 50 次），超出后按量后付费。\n" +
                     "免费额度耗尽后会自动切换至 DuckDuckGo。\n" +
                     "开通后付费: https://console.bce.baidu.com/ai_apaas/resource\n" +
                     "计费详情: https://cloud.baidu.com/doc/qianfan/s/Mmh4sv6ec")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string BaiduApiKey { get; set; } = string.Empty;

        [Category("Editor")]
        [DisplayName("Show Diff Markers In Editor")]
        [Description("AI 代码写入后，在编辑器内显示红绿行标记预览（绿色=新增行，红色=已删除行），" +
                     "并提供确认/撤销按钮。关闭后变更直接生效不预览。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool ShowDiffMarkersInEditor { get; set; } = true;

        [Category("OCR Settings")]
        [DisplayName("OCR Engine")]
        [Description("选择图像 OCR 引擎:\n" +
                     "  • Windows Built-in — 系统内置，无需配置，准确率一般\n" +
                     "  • 通过 MCP 协议使用远程 OCR 服务")]
        [TypeConverter(typeof(OcrEngineConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string OcrEngine { get; set; } = "Windows Built-in";

        // ═══════════════════════════════════════════════
        //  代码补全（幽灵文本）设置
        // ═══════════════════════════════════════════════

        [Category("Copilot")]
        [DisplayName("启用代码补全")]
        [Description("在编辑器中启用内联代码补全（幽灵文本）。" +
                     "启用后，DeepSeek 将在你输入时提供代码补全建议。" +
                     "按 Tab 接受，按 Escape 取消。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool CopilotEnabled { get; set; } = false;

        [Category("Copilot")]
        [DisplayName("补全延迟 (毫秒)")]
        [Description("停止输入后等待多少毫秒再请求补全建议。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int CopilotSuggestionInterval { get; set; } = 800;

        [Category("Copilot")]
        [DisplayName("接受后继续补全")]
        [Description("启用后，接受一条补全会立即触发新的预测。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool CopilotNextEditSuggestions { get; set; } = true;

        [Category("Copilot")]
        [DisplayName("补全专用模型")]
        [Description("可选：为代码补全指定不同的模型。留空则使用默认模型。" +
                     "建议使用速度较快的模型（如 deepseek-v4-flash）。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string CopilotModel { get; set; } = string.Empty;

        // ═══════════════════════════════════════════════
        //  上下文管理设置（DeepSeek V4 1M 上下文窗口）
        // ═══════════════════════════════════════════════

        [Category("Context Management")]
        [DisplayName("Token 预算上限")]
        [Description("DeepSeek V4 拥有 1M Token 上下文窗口。\n" +
                     "此设置控制发送给 API 的最大 Token 数（预留 100K 给模型输出）。\n" +
                     "默认 900,000。减小此值可降低 API 费用，增大可容纳更多上下文。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int TokenBudget { get; set; } = 900_000;

        [Category("Context Management")]
        [DisplayName("启用自动压缩")]
        [Description("当上下文接近 Token 预算时，自动将早期对话压缩为摘要，\n" +
                     "而非直接删除旧消息。关闭后回退到旧的截断行为。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableAutoCompression { get; set; } = true;

        [Category("Context Management")]
        [DisplayName("压缩触发阈值 (%)")]
        [Description("上下文使用率达到此百分比时触发自动压缩。\n" +
                     "默认 85%，即 900K 预算中约 765K tokens 时触发。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int CompressionThreshold { get; set; } = 85;

        [Category("Context Management")]
        [DisplayName("保留最近轮次")]
        [Description("压缩时保留最近 N 轮完整对话不被压缩。\n" +
                     "默认 3 轮。增大可保留更多即时上下文。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int PreserveRecentTurns { get; set; } = 3;

        [Category("Context Management")]
        [DisplayName("启用 RAG")]
        [Description("启用检索增强生成（RAG），在对话前自动从知识库检索相关文档。\n" +
                     "需要配置 RAG 提供者（如本地向量数据库）。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableRag { get; set; } = false;

        [Category("Context Management")]
        [DisplayName("RAG 检索数量")]
        [Description("每次查询从知识库检索的最大文档数。默认 5。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int RagTopK { get; set; } = 5;

        [Category("Context Management")]
        [DisplayName("上下文统计指示器")]
        [Description("在状态栏显示当前 Token 使用量（已用/预算）。")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool ShowContextStats { get; set; } = true;

        // ═══════════════════════════════════════════════
        //  国际化 (i18n) 设置
        // ═══════════════════════════════════════════════

        [Category("Language / 语言")]
        [DisplayName("界面语言 / Language")]
        [Description("选择显示语言。选择「自动」则跟随系统语言。\n" +
                     "Select display language. 'Auto' follows system language.")]
        [TypeConverter(typeof(LanguageConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string Language { get; set; } = "auto";
    }

    /// <summary>
    /// 模型列表下拉选项。
    /// </summary>
    internal class ModelListConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "deepseek-v4-pro", "deepseek-v4-flash" });
    }

    /// <summary>
    /// 推理强度下拉选项。
    /// </summary>
    internal class ReasoningEffortConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "high", "max" });
    }

    /// <summary>
    /// 搜索提供商下拉选项。
    /// </summary>
    internal class SearchProviderConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "Baidu", "DuckDuckGo" });
    }

    /// <summary>
    /// OCR 引擎下拉选项（PaddleOCR-Sharp 已移除以减小包体，仍可通过 MCP 使用远程 OCR）。
    /// </summary>
    internal class OcrEngineConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "Windows Built-in" });
    }

    /// <summary>
    /// 语言选择下拉选项。
    /// </summary>
    internal class LanguageConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "auto", "zh-CN", "en" });
    }
}
