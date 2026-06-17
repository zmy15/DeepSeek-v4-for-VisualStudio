using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// OCR 引擎类型枚举。
    /// </summary>
    public enum OcrEngineType
    {
        /// <summary>Windows 10+ 内置 OCR 引擎（默认，无需额外依赖）</summary>
        WindowsBuiltIn,

        /// <summary>MCP 服务器 OCR — 通过 MCP 协议调用远程/本地 OCR 服务，优先使用</summary>
        McpServer,
    }

    /// <summary>
    /// 图像 OCR 服务，支持多种 OCR 引擎。
    /// 
    /// 引擎说明：
    /// - WindowsBuiltIn: Windows 10+ 内置 OCR，零依赖，准确率一般
    /// - McpServer: MCP 远程 OCR，需要配置 MCP 服务器
    /// 
    /// 使用方式：
    /// 1. 设置 OcrService.CurrentEngine 选择引擎
    /// 2. 调用 ExtractTextFromImageAsync 执行 OCR
    /// </summary>
    public static class OcrService
    {
        #region Constants

        /// <summary>支持的图像文件扩展名</summary>
        public static readonly string[] SupportedImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif", ".webp" };

        /// <summary>最大图像文件大小（字节），超过此大小不进行 OCR</summary>
        private const long MaxImageFileSizeBytes = 10 * 1024 * 1024; // 10MB

        #endregion

        #region Configuration

        /// <summary>当前使用的 OCR 引擎类型</summary>
        public static OcrEngineType CurrentEngine { get; set; } = OcrEngineType.WindowsBuiltIn;

        /// <summary>插件安装根目录（用于默认模型路径）</summary>
        public static string? PluginRootPath { get; set; }

        /// <summary>MCP 管理器引用（用于 MCP 服务器 OCR）</summary>
        public static McpManagerService? McpManager { get; set; }

        /// <summary>
        /// 确保原生 DLL 能被 .NET Framework 运行时找到。
        /// 由于 VS 扩展宿主是 devenv.exe（不在我们的输出目录），
        /// 需要手动将插件目录及其原生 DLL 子目录加入 Windows DLL 搜索路径。
        /// </summary>
        public static void EnsureNativeDllSearchPath()
        {
            try
            {
                string? searchPath = PluginRootPath
                    ?? Path.GetDirectoryName(typeof(OcrService).Assembly.Location);

                if (string.IsNullOrWhiteSpace(searchPath) || !Directory.Exists(searchPath))
                    return;

                // 将插件根目录加入搜索路径
                SetDllDirectory(searchPath);
                Logger.Info($"[OCR] 原生 DLL 搜索路径: {searchPath}");

                // ── 预加载 dll\x64 子目录中的原生 DLL ──
                // 这些 DLL 由 NuGet 包（PaddleInference/OpenCvSharp）部署到 dll\x64\，
                // 但它们不在 SetDllDirectory 的搜索路径中，因此需要手动 LoadLibrary。
                string dllX64Dir = Path.Combine(searchPath, "dll", "x64");
                if (Directory.Exists(dllX64Dir))
                {
                    // OpenCvSharp
                    PreloadDll(dllX64Dir, "OpenCvSharpExtern.dll");
                    // PaddleInference（按依赖顺序：先加载被依赖的 DLL）
                    PreloadDll(dllX64Dir, "libiomp5md.dll");
                    PreloadDll(dllX64Dir, "mklml.dll");
                    PreloadDll(dllX64Dir, "mkldnn.dll");
                    PreloadDll(dllX64Dir, "onnxruntime_providers_shared.dll");
                    PreloadDll(dllX64Dir, "onnxruntime.dll");
                    PreloadDll(dllX64Dir, "paddle2onnx.dll");
                    PreloadDll(dllX64Dir, "common.dll");
                    PreloadDll(dllX64Dir, "paddle_inference_c.dll");
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[OCR] 设置 DLL 搜索路径失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从指定目录预加载原生 DLL（如文件存在）。
        /// 预加载后，Windows 会将此模块标记为已加载，后续 DllImport/PInvoke 可直接使用。
        /// </summary>
        private static void PreloadDll(string directory, string dllName)
        {
            string dllPath = Path.Combine(directory, dllName);
            if (File.Exists(dllPath))
            {
                LoadLibrary(dllPath);
                Logger.Info($"[OCR] 预加载原生 DLL: {dllName}");
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        #endregion

        #region Public Methods

        /// <summary>
        /// 检查文件扩展名是否为受支持的图像格式。
        /// </summary>
        public static bool IsImageFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string ext = Path.GetExtension(filePath);
            foreach (var supported in SupportedImageExtensions)
            {
                if (string.Equals(ext, supported, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 从图像文件中提取文字。
        /// 根据 CurrentEngine 设置自动选择 OCR 引擎。
        /// </summary>
        /// <param name="imagePath">图像文件路径</param>
        /// <returns>OCR 识别出的文本；失败时返回 null。</returns>
        public static async Task<string?> ExtractTextFromImageAsync(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                Logger.Info($"[OCR] 跳过: 文件不存在或路径为空 - {imagePath ?? "(null)"}");
                return null;
            }

            var fileInfo = new FileInfo(imagePath);
            if (fileInfo.Length > MaxImageFileSizeBytes)
            {
                Logger.Info($"[OCR] 跳过: 图像文件过大 ({FormatFileSize(fileInfo.Length)}) - {Path.GetFileName(imagePath)}");
                return null;
            }

            Logger.Info($"[OCR] 开始识别: {Path.GetFileName(imagePath)} ({FormatFileSize(fileInfo.Length)}), 引擎={CurrentEngine}");

            try
            {
                // ── 优先级 1: MCP 服务器 OCR（如果可用）──
                string? mcpResult = await TryMcpOcrAsync(imagePath);
                if (!string.IsNullOrWhiteSpace(mcpResult))
                {
                    Logger.Info($"[OCR] ✅ MCP 服务器 OCR 成功: {mcpResult!.Length} 字符");
                    return mcpResult;
                }

                // ── 优先级 2: 用户选择的引擎 ──
                string? result = CurrentEngine switch
                {
                    _ => await WindowsEngineWrapper.ExtractTextAsync(imagePath),
                };

                if (!string.IsNullOrWhiteSpace(result))
                {
                    Logger.Info($"[OCR] ✅ 最终结果: {result!.Length} 字符 (引擎={CurrentEngine})");
                    return result;
                }

                // ── 优先级 3: 回退到 Windows 内置 OCR ──
                if (CurrentEngine != OcrEngineType.WindowsBuiltIn)
                {
                    Logger.Info("[OCR] 所选引擎未返回结果，尝试回退到 Windows 内置 OCR...");
                    try
                    {
                        var fallbackResult = await WindowsEngineWrapper.ExtractTextAsync(imagePath);
                        if (!string.IsNullOrWhiteSpace(fallbackResult))
                        {
                            Logger.Info($"[OCR] ✅ 回退成功: {fallbackResult!.Length} 字符 (Windows 内置)");
                            return fallbackResult;
                        }
                        Logger.Info("[OCR] 回退也未提取到文字");
                    }
                    catch (Exception fbEx)
                    {
                        Logger.Info($"[OCR] 回退失败: {fbEx.Message}");
                    }
                }

                Logger.Info($"[OCR] ⚠️ 未提取到任何文字 (引擎={CurrentEngine})");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[OCR] ❌ 识别异常 (引擎={CurrentEngine}): {ex.GetType().Name} - {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 尝试通过 MCP 服务器进行 OCR（优先级最高）。
        /// 自动查找 MCP 工具列表中包含 "ocr" 或 "recognize" 的工具，
        /// 读取图像文件为 base64 后调用 MCP 工具。
        /// 
        /// 失败场景（均静默回退到本地 OCR）：
        /// - MCP 管理器未配置或未就绪
        /// - 未找到 OCR 相关工具
        /// - MCP 服务器返回错误（Token 无效、配额耗尽、网络异常等）
        /// </summary>
        private static async Task<string?> TryMcpOcrAsync(string imagePath)
        {
            if (McpManager == null) return null;

            try
            {
                // 查找 OCR 相关工具
                var allTools = McpManager.AllTools;
                if (allTools.Count == 0) return null;

                // 按优先级搜索 OCR 工具名
                var ocrToolNames = new[] { "ocr", "recognize_text", "paddle_ocr", "ocr_image", "image_to_text", "read_text" };
                var ocrTool = allTools.FirstOrDefault(t =>
                    ocrToolNames.Any(name => t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0));

                if (ocrTool == null)
                {
                    Logger.Info("[OCR-MCP] 未找到 OCR 工具，跳过 MCP OCR");
                    return null;
                }

                Logger.Info($"[OCR-MCP] 找到 OCR 工具: {ocrTool.Name}，尝试调用...");

                // 读取图像文件并转为 base64
                // RAG-SOURCE: file-read 读取图像文件（OCR base64 转换）
                byte[] imageBytes = File.ReadAllBytes(imagePath);
                string base64Image = Convert.ToBase64String(imageBytes);

                // 根据工具的 inputSchema 智能匹配参数名
                var arguments = BuildOcrToolArguments(ocrTool, imagePath, base64Image);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await McpManager.CallToolAsync(ocrTool.Name,
                    JsonSerializer.Serialize(arguments), cts.Token);

                if (string.IsNullOrWhiteSpace(result))
                {
                    Logger.Info("[OCR-MCP] MCP OCR 返回空结果");
                    return null;
                }

                // 检测错误响应（Token 无效、配额耗尽等）
                if (result.Contains("invalid", StringComparison.OrdinalIgnoreCase) &&
                    (result.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                     result.Contains("api key", StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Info($"[OCR-MCP] MCP OCR Token 无效: {TruncateForLog(result)}");
                    return null; // 静默回退到本地 OCR
                }

                if (result.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("exceeded", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("额度", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info($"[OCR-MCP] MCP OCR 配额耗尽: {TruncateForLog(result)}");
                    return null; // 静默回退到本地 OCR
                }

                if (result.StartsWith("❌") || result.StartsWith("错误"))
                {
                    Logger.Info($"[OCR-MCP] MCP OCR 返回错误: {TruncateForLog(result)}");
                    return null;
                }

                return result;
            }
            catch (McpException mcpEx)
            {
                Logger.Info($"[OCR-MCP] MCP 调用异常（回退到本地）: {mcpEx.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Info($"[OCR-MCP] MCP OCR 失败（回退到本地）: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 根据 MCP 工具的 inputSchema 智能构建 OCR 调用参数。
        /// 不同 MCP OCR 服务器使用的参数名不同：
        /// - PaddleOCR/FastMCP → input_data, output_mode, file_type
        /// - Claude MCP 风格   → image, image_base64, mime_type
        /// - 其他              → file_path 或 file_url
        /// </summary>
        private static Dictionary<string, object> BuildOcrToolArguments(
            Models.McpTool tool, string imagePath, string base64Image)
        {
            var args = new Dictionary<string, object>();
            var props = tool.InputSchema?.Properties;
            if (props == null || props.Count == 0)
            {
                args["input_data"] = base64Image;
                return args;
            }

            foreach (var propName in props.Keys)
            {
                var lower = propName.ToLowerInvariant();
                if (lower is "input_data" or "input" or "data")
                    args[propName] = base64Image;
                else if (lower is "image" or "image_base64" or "base64" or "image_data")
                    args[propName] = base64Image;
                else if (lower is "image_path" or "file_path" or "path")
                    args[propName] = imagePath;
                else if (lower is "file_url" or "url" or "image_url")
                    args[propName] = imagePath;
                else if (lower is "output_mode" or "mode" or "format")
                    args[propName] = "simple";
                else if (lower is "file_type" or "type")
                    args[propName] = "image";
                else if (lower is "mime_type" or "mimetype")
                    args[propName] = GetImageMimeType(imagePath);
            }

            Logger.Info($"[OCR-MCP] 参数匹配: schema=[{string.Join(",", props.Keys)}], args=[{string.Join(",", args.Keys)}]");
            return args;
        }

        /// <summary>
        /// 根据文件扩展名推断 MIME 类型。
        /// </summary>
        private static string GetImageMimeType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".tiff" or ".tif" => "image/tiff",
                ".webp" => "image/webp",
                _ => "image/png",
            };
        }

        /// <summary>
        /// 截断日志文本以防止过长。
        /// </summary>
        private static string TruncateForLog(string text, int maxLen = 150)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "...";
        }

        /// <summary>
        /// 获取当前引擎的可用状态描述。
        /// </summary>
        public static string GetEngineStatus()
        {
            return CurrentEngine switch
            {
                OcrEngineType.WindowsBuiltIn => WindowsEngineWrapper.GetStatus(),
                _ => "未知引擎",
            };
        }

        /// <summary>
        /// 检查当前引擎是否就绪可用。
        /// </summary>
        public static bool IsEngineReady()
        {
            return CurrentEngine switch
            {
                OcrEngineType.WindowsBuiltIn => WindowsEngineWrapper.IsAvailable(),
                _ => false,
            };
        }

        /// <summary>
        /// 重置所有引擎的内部状态（用于设置热切换）。
        /// 清除缓存的引擎实例和环境检查结果，下次调用时重新检测。
        /// </summary>
        public static void ResetAllEngines()
        {
            Logger.Info("[OCR] 重置所有引擎状态（热切换设置）...");
            PaddleEngineWrapper.Reset();
            Logger.Info("[OCR] 引擎状态已重置，下次 OCR 将使用新配置重新初始化");
        }

        #endregion

        #region Private Helpers

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }


        #endregion

        #region ── Windows Built-in OCR Engine Wrapper ──

        /// <summary>
        /// Windows 10+ 内置 OCR 引擎封装。
        /// 使用 Windows.Media.Ocr API，零额外依赖。
        /// </summary>
        private static class WindowsEngineWrapper
        {
            public static string GetStatus() => "✅ Windows 内置 OCR 已就绪";

            public static bool IsAvailable()
            {
                try
                {
                    var engine = OcrEngine.TryCreateFromUserProfileLanguages();
                    bool available = engine != null;
                    Logger.Info($"[OCR-Windows] 可用性检查: {(available ? "✅ 可用" : "❌ 不可用（用户语言不支持 OCR）")}");
                    return available;
                }
                catch (Exception ex)
                {
                    Logger.Info($"[OCR-Windows] 可用性检查异常: {ex.Message}");
                    return false;
                }
            }

            public static async Task<string?> ExtractTextAsync(string imagePath)
            {
                Logger.Info($"[OCR-Windows] 开始识别: {Path.GetFileName(imagePath)}");
                try
                {
                    var storageFile = await StorageFile.GetFileFromPathAsync(imagePath);
                    using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                    if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Gray8)
                    {
                        Logger.Info("[OCR-Windows] 转换像素格式 → Gray8");
                        softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Gray8);
                    }

                    var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (ocrEngine == null)
                    {
                        Logger.Info("[OCR-Windows] ❌ 无法创建 OcrEngine（用户语言配置可能不支持 OCR）");
                        return null;
                    }

                    Logger.Info($"[OCR-Windows] 引擎语言: {ocrEngine.RecognizerLanguage?.DisplayName ?? "未知"}");
                    var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
                    string text = ocrResult?.Text?.Trim() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Logger.Info($"[OCR-Windows] ✅ 成功: {text.Length} 字符 ← {Path.GetFileName(imagePath)}");
                        return text;
                    }

                    Logger.Info($"[OCR-Windows] ⚠️ 未提取到文字 ← {Path.GetFileName(imagePath)}");
                    return null;
                }
                catch (TypeLoadException)
                {
                    Logger.Info("[OCR-Windows] ❌ WinRT 类型不可用（非 Windows 10+ 环境）");
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Info($"[OCR-Windows] ❌ 失败: {ex.GetType().Name} - {ex.Message}");
                    return null;
                }
            }
        }

        #endregion
    }
}
