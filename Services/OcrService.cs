using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.IO;
using System.Linq;
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

        /// <summary>Tesseract.NET — 经典开源 OCR，中文准确率 ≥92%</summary>
        Tesseract,

        /// <summary>PaddleOCR-Sharp — 深度学习 OCR，中文准确率 ≥95%</summary>
        PaddleOCR,
    }

    /// <summary>
    /// 图像 OCR 服务，支持多种 OCR 引擎。
    /// 
    /// 引擎说明：
    /// - WindowsBuiltIn: Windows 10+ 内置 OCR，零依赖，准确率一般
    /// - Tesseract.NET:  需安装 NuGet 包 Tesseract + 语言包 chi_sim.traineddata (~15MB)
    /// - PaddleOCR-Sharp: 需安装 NuGet 包 PaddleOCRSharp + 推理模型 (~200MB)
    /// 
    /// 使用方式：
    /// 1. 设置 OcrService.CurrentEngine 选择引擎
    /// 2. 设置 OcrService.TesseractDataPath 和 OcrService.PaddleOcrModelPath 指定模型路径
    /// 3. 调用 ExtractTextFromImageAsync 执行 OCR
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

        /// <summary>Tesseract 语言包目录路径（包含 chi_sim.traineddata）</summary>
        public static string? TesseractDataPath { get; set; }

        /// <summary>PaddleOCR 推理模型目录路径</summary>
        public static string? PaddleOcrModelPath { get; set; }

        /// <summary>插件安装根目录（用于默认模型路径）</summary>
        public static string? PluginRootPath { get; set; }

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

                // ── 预加载 x64 子目录中的原生 DLL ──
                // Tesseract 原生 DLL 可能被部署到 x64\ 子目录。
                string x64Dir = Path.Combine(searchPath, "x64");
                if (Directory.Exists(x64Dir))
                {
                    PreloadDll(x64Dir, "leptonica-1.82.0.dll");
                    PreloadDll(x64Dir, "tesseract50.dll");
                }

                // ── 兜底：也尝试从根目录加载 Tesseract DLL（如果它们被直接部署到根目录）──
                PreloadDll(searchPath, "leptonica-1.82.0.dll");
                PreloadDll(searchPath, "tesseract50.dll");
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
                string? result = CurrentEngine switch
                {
                    OcrEngineType.Tesseract => await TesseractEngineWrapper.ExtractTextAsync(imagePath),
                    OcrEngineType.PaddleOCR => await PaddleEngineWrapper.ExtractTextAsync(imagePath),
                    _ => await WindowsEngineWrapper.ExtractTextAsync(imagePath),
                };

                if (!string.IsNullOrWhiteSpace(result))
                {
                    Logger.Info($"[OCR] ✅ 最终结果: {result.Length} 字符 (引擎={CurrentEngine})");
                    return result;
                }

                // 如果所选引擎失败，尝试回退到 Windows 内置 OCR
                if (CurrentEngine != OcrEngineType.WindowsBuiltIn)
                {
                    Logger.Info("[OCR] 所选引擎未返回结果，尝试回退到 Windows 内置 OCR...");
                    try
                    {
                        var fallbackResult = await WindowsEngineWrapper.ExtractTextAsync(imagePath);
                        if (!string.IsNullOrWhiteSpace(fallbackResult))
                        {
                            Logger.Info($"[OCR] ✅ 回退成功: {fallbackResult.Length} 字符 (Windows 内置)");
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
        /// 获取当前引擎的可用状态描述。
        /// </summary>
        public static string GetEngineStatus()
        {
            return CurrentEngine switch
            {
                OcrEngineType.WindowsBuiltIn => WindowsEngineWrapper.GetStatus(),
                OcrEngineType.Tesseract => TesseractEngineWrapper.GetStatus(),
                OcrEngineType.PaddleOCR => PaddleEngineWrapper.GetStatus(),
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
                OcrEngineType.Tesseract => TesseractEngineWrapper.IsAvailable(),
                OcrEngineType.PaddleOCR => PaddleEngineWrapper.IsAvailable(),
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
            TesseractEngineWrapper.Reset();
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

        /// <summary>
        /// 获取 Tesseract 语言包默认路径。
        /// </summary>
        internal static string GetDefaultTessdataPath()
        {
            if (!string.IsNullOrWhiteSpace(TesseractDataPath))
                return TesseractDataPath;
            if (!string.IsNullOrWhiteSpace(PluginRootPath))
                return Path.Combine(PluginRootPath, "tessdata");
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
        }

        /// <summary>
        /// 获取 PaddleOCR 模型默认路径。
        /// </summary>
        internal static string GetDefaultPaddleModelPath()
        {
            if (!string.IsNullOrWhiteSpace(PaddleOcrModelPath))
                return PaddleOcrModelPath;
            if (!string.IsNullOrWhiteSpace(PluginRootPath))
                return Path.Combine(PluginRootPath, "inference");
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inference");
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

        #region ── Tesseract.NET Engine Wrapper ──

        /// <summary>
        /// Tesseract.NET OCR 引擎封装。
        /// 需要 NuGet 包: Tesseract (>=5.2.0)
        /// 需要语言包: chi_sim.traineddata 放入 tessdata 目录
        /// 
        /// ⚠️ 设计要点：_cachedEngine 使用 object? 而非 Tesseract.TesseractEngine?，
        /// 避免 CLR 在访问此类时立即尝试加载 Tesseract 程序集（防止找不到原生 DLL 时崩溃）。
        /// </summary>
        private static class TesseractEngineWrapper
        {
            private static readonly object _lock = new();
            private static bool _initialized;
            private static bool _available;
            private static string? _statusMessage;
            /// <summary>缓存的引擎实例（object 类型避免强类型触发程序集加载）</summary>
            private static object? _cachedEngine;
            private static readonly SemaphoreSlim _engineSemaphore = new(1, 1);

            public static string GetStatus()
            {
                EnsureChecked();
                return _statusMessage ?? "Tesseract: 状态未知";
            }

            public static bool IsAvailable()
            {
                EnsureChecked();
                return _available;
            }

            /// <summary>
            /// 重置引擎状态（用于设置热切换）。
            /// 清除缓存的引擎实例和环境检查结果。
            /// </summary>
            public static void Reset()
            {
                lock (_lock)
                {
                    Logger.Info("[OCR-Tesseract] 重置缓存状态...");
                    (_cachedEngine as IDisposable)?.Dispose();
                    _cachedEngine = null;
                    _initialized = false;
                    _available = false;
                    _statusMessage = null;
                }
            }

            private static void EnsureChecked()
            {
                if (_initialized) return;
                lock (_lock)
                {
                    if (_initialized) return;
                    _initialized = true;

                    Logger.Info("[OCR-Tesseract] 开始环境检查...");
                    try
                    {
                        // 验证 Tesseract 程序集可加载
                        _ = typeof(Tesseract.TesseractEngine);
                        Logger.Info("[OCR-Tesseract] Tesseract 程序集已加载");

                        string tessdataPath = GetDefaultTessdataPath();
                        Logger.Info($"[OCR-Tesseract] 语言包搜索路径: {tessdataPath}");

                        string chiSimFile = Path.Combine(tessdataPath, "chi_sim.traineddata");

                        if (!Directory.Exists(tessdataPath))
                        {
                            _available = false;
                            _statusMessage = $"❌ Tesseract 语言包目录不存在: {tessdataPath}\n请下载 chi_sim.traineddata 放入该目录。\n下载: https://github.com/tesseract-ocr/tessdata_best";
                            Logger.Info($"[OCR-Tesseract] {_statusMessage}");
                            return;
                        }

                        Logger.Info($"[OCR-Tesseract] 语言包目录存在，扫描内容: {string.Join(", ", Directory.GetFiles(tessdataPath).Select(Path.GetFileName))}");

                        if (!File.Exists(chiSimFile))
                        {
                            _available = false;
                            _statusMessage = $"❌ 找不到中文语言包: {chiSimFile}\n请从 https://github.com/tesseract-ocr/tessdata_best 下载 chi_sim.traineddata";
                            Logger.Info($"[OCR-Tesseract] {_statusMessage}");
                            return;
                        }

                        var chiSimInfo = new FileInfo(chiSimFile);
                        Logger.Info($"[OCR-Tesseract] 找到 chi_sim.traineddata ({FormatFileSize(chiSimInfo.Length)})");

                        _available = true;
                        _statusMessage = $"✅ Tesseract.NET 已就绪 (语言包: {tessdataPath})";
                        Logger.Info($"[OCR-Tesseract] {_statusMessage}");
                    }
                    catch (Exception ex)
                    {
                        _available = false;
                        _statusMessage = $"❌ Tesseract 初始化失败: {ex.Message}";
                        Logger.Error($"[OCR-Tesseract] 环境检查异常: {ex.GetType().Name} - {ex.Message}", ex);
                    }
                }
            }

            public static async Task<string?> ExtractTextAsync(string imagePath)
            {
                Logger.Info($"[OCR-Tesseract] 开始提取文字: {Path.GetFileName(imagePath)}");
                EnsureChecked();
                if (!_available)
                {
                    Logger.Info($"[OCR-Tesseract] 引擎不可用，跳过: {_statusMessage}");
                    return null;
                }

                await _engineSemaphore.WaitAsync();
                try
                {
                    return await Task.Run(() =>
                    {
                        try
                        {
                            string tessdataPath = GetDefaultTessdataPath();

                            // 复用引擎实例（TesseractEngine 创建开销大）
                            var engine = _cachedEngine as Tesseract.TesseractEngine;
                            if (engine == null || engine.IsDisposed)
                            {
                                Logger.Info("[OCR-Tesseract] 创建新引擎实例...");
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                (engine as IDisposable)?.Dispose();
                                engine = new Tesseract.TesseractEngine(
                                    tessdataPath, "chi_sim", Tesseract.EngineMode.Default);
                                _cachedEngine = engine;

                                // 优化设置
                                engine.SetVariable("preserve_interword_spaces", "1");
                                sw.Stop();
                                Logger.Info($"[OCR-Tesseract] 引擎实例化完成，耗时 {sw.ElapsedMilliseconds}ms");
                            }

                            Logger.Info($"[OCR-Tesseract] 正在识别: {Path.GetFileName(imagePath)}");
                            var sw2 = System.Diagnostics.Stopwatch.StartNew();
                            using var img = Tesseract.Pix.LoadFromFile(imagePath);
                            using var page = engine.Process(img);
                            string text = page.GetText()?.Trim() ?? string.Empty;
                            sw2.Stop();

                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                Logger.Info($"[OCR-Tesseract] ✅ 识别成功: {text.Length} 字符，耗时 {sw2.ElapsedMilliseconds}ms ← {Path.GetFileName(imagePath)}");
                                return text;
                            }

                            Logger.Info($"[OCR-Tesseract] ⚠️ 识别完成但无文字，耗时 {sw2.ElapsedMilliseconds}ms ← {Path.GetFileName(imagePath)}");
                            return null;
                        }
                        catch (Exception ex)
                        {
                            string innerMsg = ex.InnerException != null
                                ? $" | Inner: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}"
                                : "";
                            Logger.Error($"[OCR-Tesseract] ❌ 识别失败: {ex.GetType().Name} - {ex.Message}{innerMsg}", ex);
                            // 引擎可能损坏，下次重新创建
                            (_cachedEngine as IDisposable)?.Dispose();
                            _cachedEngine = null;
                            return null;
                        }
                    });
                }
                finally
                {
                    _engineSemaphore.Release();
                }
            }
        }

        #endregion

        #region ── PaddleOCR-Sharp Engine Wrapper (Sdcb.PaddleSharp) ──

        /// <summary>
        /// PaddleOCR 引擎封装，基于 Sdcb.PaddleSharp 系列包。
        /// 需要 NuGet 包: Sdcb.PaddleOCR, Sdcb.PaddleInference.runtime.win64.mkl
        /// 需要推理模型: det + rec 子目录放入 inference 目录
        /// 
        /// GPU 加速: 需替换 runtime 包为 cuda 版本（如 win64.cuda118_cudnn86_tr85）
        /// 
        /// ⚠️ 设计要点：_cachedEngine 使用 object? 避免 CLR 预加载程序集。
        /// </summary>
        private static class PaddleEngineWrapper
        {
            private static readonly object _lock = new();
            private static bool _initialized;
            private static bool _available;
            private static string? _statusMessage;
            /// <summary>缓存的检测器实例（Sdcb.PaddleOCR.PaddleOcrDetector）</summary>
            private static object? _cachedDetector;
            /// <summary>缓存的识别器实例（Sdcb.PaddleOCR.PaddleOcrRecognizer）</summary>
            private static object? _cachedRecognizer;
            private static string? _cachedModelPath; // 用于检测模型路径是否变更
            private static readonly SemaphoreSlim _engineSemaphore = new(1, 1);

            /// <summary>是否检测到 CUDA GPU（仅用于日志提示，GPU 需对应 runtime 包）</summary>
            private static bool? _cudaAvailable;

            /// <summary>
            /// 检测 CUDA 是否可用（通过 nvidia-smi）。
            /// 注意：即使检测到 CUDA，仍需安装 GPU 版 runtime 包才能启用 GPU 加速。
            /// 当前使用的 MKL 版 runtime 仅支持 CPU 推理。
            /// </summary>
            private static bool IsCudaAvailable()
            {
                if (_cudaAvailable.HasValue)
                    return _cudaAvailable.Value;

                try
                {
                    using var proc = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "nvidia-smi",
                            Arguments = "-L",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        },
                    };
                    proc.Start();
                    proc.WaitForExit(5000);
                    _cudaAvailable = proc.ExitCode == 0;
                    Logger.Info($"[OCR-Paddle] CUDA 检测 (nvidia-smi): 退出码={proc.ExitCode}, 可用={_cudaAvailable}");
                }
                catch (Exception ex)
                {
                    _cudaAvailable = false;
                    Logger.Info($"[OCR-Paddle] CUDA 检测失败: {ex.Message}");
                }

                return _cudaAvailable.Value;
            }

            public static string GetStatus()
            {
                EnsureChecked();
                return _statusMessage ?? "PaddleOCR: 状态未知";
            }

            public static bool IsAvailable()
            {
                EnsureChecked();
                return _available;
            }

            /// <summary>
            /// 重置引擎状态（用于设置热切换）。
            /// 清除缓存的引擎实例、CUDA 检测结果和环境检查结果。
            /// </summary>
            public static void Reset()
            {
                lock (_lock)
                {
                    Logger.Info("[OCR-Paddle] 重置缓存状态...");
                    (_cachedDetector as IDisposable)?.Dispose();
                    (_cachedRecognizer as IDisposable)?.Dispose();
                    _cachedDetector = null;
                    _cachedRecognizer = null;
                    _cachedModelPath = null;
                    _cudaAvailable = null;
                    _initialized = false;
                    _available = false;
                    _statusMessage = null;
                }
            }

            private static void EnsureChecked()
            {
                if (_initialized) return;
                lock (_lock)
                {
                    if (_initialized) return;
                    _initialized = true;

                    Logger.Info("[OCR-Paddle] 开始环境检查...");
                    try
                    {
                        // 验证 Sdcb.PaddleOCR 程序集可加载
                        _ = typeof(Sdcb.PaddleOCR.PaddleOcrAll);
                        Logger.Info("[OCR-Paddle] Sdcb.PaddleOCR 程序集已加载");

                        string modelPath = GetDefaultPaddleModelPath();
                        Logger.Info($"[OCR-Paddle] 模型搜索路径: {modelPath}");

                        if (!Directory.Exists(modelPath))
                        {
                            _available = false;
                            _statusMessage = $"❌ PaddleOCR 模型目录不存在: {modelPath}\n请从 PaddleOCR 官方下载推理模型。\n检测: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_det_infer.tar\n识别: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_rec_infer.tar\n字典: https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/utils/ppocr_keys_v1.txt";
                            Logger.Info($"[OCR-Paddle] {_statusMessage}");
                            return;
                        }

                        Logger.Info($"[OCR-Paddle] 模型目录存在，扫描子目录: {string.Join(", ", Directory.GetDirectories(modelPath).Select(d => Path.GetFileName(d)))}");

                        // 检查必要模型（det + rec）
                        bool foundDet = Directory.GetDirectories(modelPath, "*det*", SearchOption.TopDirectoryOnly).Length > 0;
                        bool foundRec = Directory.GetDirectories(modelPath, "*rec*", SearchOption.TopDirectoryOnly).Length > 0;
                        bool foundCls = Directory.GetDirectories(modelPath, "*cls*", SearchOption.TopDirectoryOnly).Length > 0;

                        Logger.Info($"[OCR-Paddle] 模型检测: det={foundDet}, rec={foundRec}, cls={foundCls} (cls 可选)");

                        if (!foundDet || !foundRec)
                        {
                            _available = false;
                            _statusMessage = $"❌ PaddleOCR 推理模型不完整: {modelPath}\n至少需要 det 和 rec 两个模型目录。\n检测: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_det_infer.tar\n识别: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_rec_infer.tar\n字典: https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/utils/ppocr_keys_v1.txt";
                            Logger.Info($"[OCR-Paddle] {_statusMessage}");
                            return;
                        }

                        // 检查字典文件
                        var keyFiles = Directory.GetFiles(modelPath, "*.txt", SearchOption.AllDirectories);
                        bool hasDict = keyFiles.Length > 0;
                        Logger.Info($"[OCR-Paddle] 字典文件: {(hasDict ? keyFiles[0] : "未找到")}");

                        // CUDA 检测（仅用于日志）
                        bool cudaDetected = IsCudaAvailable();
                        string gpuNote = cudaDetected
                            ? " (检测到 NVIDIA GPU，安装 GPU 版 runtime 包可启用加速)"
                            : "";

                        _available = true;
                        _statusMessage = $"✅ PaddleOCR 已就绪 (CPU/MKL{gpuNote}, 模型: {modelPath})";
                        Logger.Info($"[OCR-Paddle] {_statusMessage}");
                    }
                    catch (Exception ex)
                    {
                        _available = false;
                        _statusMessage = $"❌ PaddleOCR 初始化失败: {ex.Message}";
                        Logger.Error($"[OCR-Paddle] 环境检查异常: {ex.GetType().Name} - {ex.Message}", ex);
                    }
                }
            }

            /// <summary>
            /// 验证 PaddleOCR 模型目录中包含必需的模型文件。
            /// 若缺少关键文件，抛出 FileNotFoundException 并给出清晰的排查指引。
            /// </summary>
            /// <param name="modelDir">模型目录路径</param>
            /// <param name="dirLabel">目录用途标签（如"检测(det)"）</param>
            private static void ValidateModelDirectory(string modelDir, string dirLabel)
            {
                if (!Directory.Exists(modelDir))
                {
                    throw new FileNotFoundException(
                        $"❌ PaddleOCR {dirLabel} 模型目录不存在: {modelDir}\n" +
                        "请确认 PaddleOcrModelPath 设置正确，且已下载推理模型。\n" +
                        "下载: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_det_infer.tar");
                }

                var allFiles = Directory.GetFiles(modelDir);
                var pdmodelFiles = allFiles.Where(f => f.EndsWith(".pdmodel", StringComparison.OrdinalIgnoreCase)).ToArray();
                var pdiparamsFiles = allFiles.Where(f => f.EndsWith(".pdiparams", StringComparison.OrdinalIgnoreCase)).ToArray();
                bool hasParamsFile = pdiparamsFiles.Length > 0;

                // 列出目录内容供诊断
                string fileList = allFiles.Length > 0
                    ? string.Join("\n  ", allFiles.Select(Path.GetFileName))
                    : "(目录为空)";
                Logger.Info($"[OCR-Paddle] {dirLabel} 模型目录内容 ({allFiles.Length} 文件):\n  {fileList}");

                // 检查必需文件
                if (pdmodelFiles.Length == 0)
                {
                    throw new FileNotFoundException(
                        $"❌ PaddleOCR {dirLabel} 模型目录中缺少 .pdmodel 文件: {modelDir}\n" +
                        $"目录中的文件 ({allFiles.Length}):\n  {fileList}\n\n" +
                        "请下载 PaddleOCR 推理模型（.pdmodel + .pdiparams）放入此目录。\n" +
                        "检测: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_det_infer.tar\n" +
                        "识别: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_rec_infer.tar\n" +
                        "字典: https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/utils/ppocr_keys_v1.txt");
                }

                if (!hasParamsFile)
                {
                    throw new FileNotFoundException(
                        $"❌ PaddleOCR {dirLabel} 模型目录中缺少 .pdiparams 文件: {modelDir}\n" +
                        $"目录中的文件 ({allFiles.Length}):\n  {fileList}\n\n" +
                        "请下载 PaddleOCR 推理模型（.pdmodel + .pdiparams）放入此目录。\n" +
                        "检测: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_det_infer.tar\n" +
                        "识别: https://paddleocr.bj.bcebos.com/PP-OCRv4/chinese/ch_PP-OCRv4_rec_infer.tar\n" +
                        "字典: https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/utils/ppocr_keys_v1.txt");
                }

                Logger.Info($"[OCR-Paddle] {dirLabel} 模型验证通过: {pdmodelFiles.Length} pdmodel + {pdiparamsFiles.Length} pdiparams");
            }

            public static async Task<string?> ExtractTextAsync(string imagePath)
            {
                Logger.Info($"[OCR-Paddle] 开始提取文字: {Path.GetFileName(imagePath)}");
                EnsureChecked();
                if (!_available)
                {
                    Logger.Info($"[OCR-Paddle] 引擎不可用，跳过: {_statusMessage}");
                    return null;
                }

                await _engineSemaphore.WaitAsync();
                try
                {
                    return await Task.Run(() =>
                    {
                        try
                        {
                            string modelPath = GetDefaultPaddleModelPath();

                            // ── 装载图像并预处理 ──
                            using var srcMat = OpenCvSharp.Cv2.ImRead(imagePath, OpenCvSharp.ImreadModes.Color);
                            if (srcMat == null || srcMat.Empty())
                            {
                                Logger.Info($"[OCR-Paddle] ❌ 无法加载图像: {imagePath}");
                                return null;
                            }

                            // 灰度化 + 对比度增强（提升 OCR 识别率）
                            using var grayMat = new OpenCvSharp.Mat();
                            OpenCvSharp.Cv2.CvtColor(srcMat, grayMat, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
                            Logger.Info($"[OCR-Paddle] 图像预处理: {srcMat.Width}x{srcMat.Height} → 灰度化");

                            // ── 创建或复用检测器 + 识别器（分离模型目录） ──
                            var detector = _cachedDetector as Sdcb.PaddleOCR.PaddleOcrDetector;
                            var recognizer = _cachedRecognizer as Sdcb.PaddleOCR.PaddleOcrRecognizer;
                            bool needRecreate = detector == null || recognizer == null || _cachedModelPath != modelPath;

                            if (needRecreate)
                            {
                                Logger.Info("[OCR-Paddle] 创建新引擎实例（可能需要 3-5 秒）...");
                                var sw = System.Diagnostics.Stopwatch.StartNew();

                                // 清理旧实例
                                (detector as IDisposable)?.Dispose();
                                (recognizer as IDisposable)?.Dispose();

                                // 智能匹配 det/rec 子目录
                                var detDirs = Directory.GetDirectories(modelPath, "*det*");
                                var recDirs = Directory.GetDirectories(modelPath, "*rec*");
                                string detDir = detDirs.Length > 0 ? detDirs[0] : modelPath;
                                string recDir = recDirs.Length > 0 ? recDirs[0] : modelPath;
                                Logger.Info($"[OCR-Paddle] detDir={detDir}, recDir={recDir}");

                                // 验证模型文件完整性（帮助诊断 "programPath not found" 错误）
                                ValidateModelDirectory(detDir, "检测(det)");
                                ValidateModelDirectory(recDir, "识别(rec)");

                                // 查找字典文件
                                var keyFiles = Directory.GetFiles(modelPath, "*.txt", SearchOption.AllDirectories);
                                string labelPath = keyFiles.Length > 0
                                    ? keyFiles[0]
                                    : Path.Combine(modelPath, "ppocr_keys_v1.txt");
                                Logger.Info($"[OCR-Paddle] labelPath={labelPath}");

                                detector = new Sdcb.PaddleOCR.PaddleOcrDetector(detDir);
                                recognizer = new Sdcb.PaddleOCR.PaddleOcrRecognizer(recDir, labelPath);
                                _cachedDetector = detector;
                                _cachedRecognizer = recognizer;
                                _cachedModelPath = modelPath;
                                sw.Stop();
                                Logger.Info($"[OCR-Paddle] 引擎实例化完成，耗时 {sw.ElapsedMilliseconds}ms");
                            }

                            // ── 执行识别 ──
                            Logger.Info($"[OCR-Paddle] 正在识别: {Path.GetFileName(imagePath)}");
                            var sw2 = System.Diagnostics.Stopwatch.StartNew();

                            // 步骤 1: 文字检测 → 获得文本框
                            var rects = detector.Run(grayMat);
                            if (rects == null || rects.Length == 0)
                            {
                                Logger.Info($"[OCR-Paddle] ⚠️ 未检测到文字区域，耗时 {sw2.ElapsedMilliseconds}ms");
                                return null;
                            }
                            Logger.Info($"[OCR-Paddle] 检测到 {rects.Length} 个文字区域");

                            // 步骤 2: 逐区域识别
                            var sb = new System.Text.StringBuilder();
                            int recogCount = 0;
                            foreach (var rect in rects)
                            {
                                try
                                {
                                    // 裁剪并保证边界有效
                                    int x = Math.Max(0, rect.X);
                                    int y = Math.Max(0, rect.Y);
                                    int w = Math.Min(rect.Width, grayMat.Width - x);
                                    int h = Math.Min(rect.Height, grayMat.Height - y);
                                    if (w <= 0 || h <= 0) continue;

                                    using var crop = new OpenCvSharp.Mat(grayMat, new OpenCvSharp.Rect(x, y, w, h));
                                    var recResult = recognizer.Run(crop);
                                    if (recResult != null && !string.IsNullOrWhiteSpace(recResult.Text))
                                    {
                                        if (sb.Length > 0) sb.AppendLine();
                                        sb.Append(recResult.Text);
                                        recogCount++;
                                    }
                                }
                                catch (Exception regionEx)
                                {
                                    Logger.Info($"[OCR-Paddle] 识别区域失败: {regionEx.Message}");
                                }
                            }

                            sw2.Stop();
                            string text = sb.ToString().Trim();

                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                Logger.Info($"[OCR-Paddle] ✅ 识别成功: {text.Length} 字符 ({recogCount}/{rects.Length} 区域), 耗时 {sw2.ElapsedMilliseconds}ms ← {Path.GetFileName(imagePath)}");
                                return text;
                            }

                            Logger.Info($"[OCR-Paddle] ⚠️ 识别完成但无文字 ({rects.Length} 区域)，耗时 {sw2.ElapsedMilliseconds}ms ← {Path.GetFileName(imagePath)}");
                            return null;
                        }
                        catch (Exception ex)
                        {
                            // 记录完整的内部异常链
                            string innerMsg = ex.InnerException != null
                                ? $" | Inner: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}"
                                : "";
                            Logger.Error($"[OCR-Paddle] ❌ 识别失败: {ex.GetType().Name} - {ex.Message}{innerMsg}", ex);
                            // 引擎可能损坏，下次重新创建
                            (_cachedDetector as IDisposable)?.Dispose();
                            (_cachedRecognizer as IDisposable)?.Dispose();
                            _cachedDetector = null;
                            _cachedRecognizer = null;
                            _cachedModelPath = null;
                            return null;
                        }
                    });
                }
                finally
                {
                    _engineSemaphore.Release();
                }
            }
        }

        #endregion
    }
}
