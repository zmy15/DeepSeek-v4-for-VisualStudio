using DeepSeek_v4_for_VisualStudio.Services;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Runtime.InteropServices; // Added for Dispid attribute

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

        [Category("OCR Settings")]
        [DisplayName("OCR Engine")]
        [Description("选择图像 OCR 引擎:\n" +
                     "  • Windows Built-in — 系统内置，无需配置，准确率一般\n" +
                     "  • Tesseract.NET — 经典开源引擎，中文准确率 ≥92%\n" +
                     "    需下载语言包 chi_sim.traineddata (~15MB)\n" +
                     "  • PaddleOCR-Sharp — 深度学习引擎，中文准确率 ≥95%\n" +
                     "    需下载推理模型 det/rec/cls (~200MB)")]
        [TypeConverter(typeof(OcrEngineConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string OcrEngine { get; set; } = "Windows Built-in";

        [Category("OCR Settings")]
        [DisplayName("Tesseract Language Data Path")]
        [Description("Tesseract 语言包目录（需包含 chi_sim.traineddata 文件）。\n" +
                     "留空则使用默认路径: {插件安装目录}\\tessdata")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string TesseractDataPath { get; set; } = string.Empty;

        [Category("OCR Settings")]
        [DisplayName("📥 Download Tesseract Language Pack")]
        [Description("点击右侧 \"...\" 按钮，在浏览器中打开 Tesseract 中文语言包下载页面。\n" +
                     "下载 chi_sim.traineddata 后放入上方配置的 tessdata 目录即可使用。\n" +
                     "下载地址: https://github.com/tesseract-ocr/tessdata_best")]
        [Editor(typeof(DownloadLinkEditor), typeof(UITypeEditor))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string TesseractDownloadLink => "https://github.com/tesseract-ocr/tessdata_best";

        [Category("OCR Settings")]
        [DisplayName("PaddleOCR Model Path")]
        [Description("PaddleOCR 推理模型根目录（需包含 det / rec 两个子目录，cls 可选）。\n" +
                     "留空则使用默认路径: {插件安装目录}\\inference")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string PaddleOcrModelPath { get; set; } = string.Empty;

        [Category("OCR Settings")]
        [DisplayName("📥 Download PaddleOCR Inference Models")]
        [Description("点击右侧 \"...\" 按钮，在浏览器中打开 PaddleOCR 模型下载页面。\n" +
                     "下载 det/rec 推理模型后解压放入上方配置的目录即可使用。\n" +
                     "检测模型: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_det_infer.tar\n" +
                     "识别模型: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_rec_infer.tar\n" +
                     "字典文件: https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/utils/ppocr_keys_v1.txt")]
        [Editor(typeof(DownloadLinkEditor), typeof(UITypeEditor))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string PaddleOcrDownloadLink => "https://github.com/PaddlePaddle/PaddleOCR/blob/main/doc/doc_ch/models_list.md";
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
    /// OCR 引擎下拉选项。
    /// </summary>
    internal class OcrEngineConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "Windows Built-in", "Tesseract.NET", "PaddleOCR-Sharp" });
    }
}
