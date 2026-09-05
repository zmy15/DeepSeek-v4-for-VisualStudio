using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// capture_window 工具 — 捕获指定 Windows 窗口的截图并保存为 PNG，
    /// 供视觉模型（deepseek-v4-flash-vision-exp）直接读取分析。
    ///
    /// 结果文本末尾附带 [CAPTURE_IMAGE]...[/CAPTURE_IMAGE] 块，里面是截图 PNG 的本地路径。
    /// BaseAgent 工具循环会解析该块，剥离出纯文本，并在视觉模型激活时把截图转成
    /// data URI 作为 image_url 直传给模型；非视觉模型则只收到纯文本（保存路径 + 尺寸）。
    /// </summary>
    public class CaptureWindowTool : BuiltInToolBase
    {
        /// <summary>结果文本中图片块的起始标记。</summary>
        public const string CaptureImageBlockStart = "[CAPTURE_IMAGE]";

        /// <summary>结果文本中图片块的结束标记。</summary>
        public const string CaptureImageBlockEnd = "[/CAPTURE_IMAGE]";

        /// <summary>PrintWindow 渲染完整内容标志（兼容 DWM/Chrome/Electron 合成窗口）。</summary>
        private const uint PrintWindowRenderFullContent = 0x2;

        /// <summary>截图长边像素上限，防止超大截图撑爆视觉模型请求。</summary>
        private const int DefaultMaxLongEdgePx = 2048;

        /// <summary>SW_RESTORE：还原（可能最小化的）窗口。</summary>
        private const int SwRestore = 9;

        /// <summary>SW_SHOWNOACTIVATE：显示窗口但不激活。</summary>
        private const int SwShowNoActivate = 4;

        /// <summary>WS_POPUP：无边框顶层窗口。</summary>
        private const int WsPopup = unchecked((int)0x80000000);

        /// <summary>WS_EX_TOOLWINDOW：不出现在任务栏。</summary>
        private const int WsExToolWindow = 0x00000080;

        /// <summary>WS_EX_NOACTIVATE：显示时不抢焦点。</summary>
        private const int WsExNoActivate = 0x08000000;

        // ── DWM 缩略图属性标志 ──
        private const int DwmTnpRectDestination = 0x00000001;
        private const int DwmTnpOpacity = 0x00000004;
        private const int DwmTnpVisible = 0x00000008;
        private const int DwmTnpSourceClientAreaOnly = 0x00000010;

        /// <summary>截图默认保存目录（系统临时目录下）。</summary>
        private static readonly string CaptureTempDir =
            Path.Combine(Path.GetTempPath(), "DeepSeekVS_Captures");

        public override string Name => "capture_window";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "capture_window",
                    Description = L["tool.capture_window.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            window_title = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.captureWindow.param.windowTitle"]
                            },
                            max_width = new
                            {
                                type = "integer",
                                description = LocalizationService.Instance["tool.captureWindow.param.maxWidth"]
                            },
                            capture_method = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.captureWindow.param.captureMethod"]
                            },
                            save_path = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.captureWindow.param.savePath"]
                            }
                        }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string title = GetStringArg(args, "window_title");
            return string.IsNullOrWhiteSpace(title)
                ? LocalizationService.Instance["tool.captureWindow.capturing"]
                : LocalizationService.Instance.Format("tool.captureWindow.capturingTitle", TruncateText(title, 60));
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;
            return LocalizationService.Instance["tool.captureWindow.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string windowTitle = GetStringArg(args, "window_title");
            int maxWidth = GetIntArg(args, "max_width", 0);
            string captureMethod = GetStringArg(args, "capture_method");
            string savePathArg = GetStringArg(args, "save_path");

            IntPtr hWnd = IntPtr.Zero;
            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                hWnd = GetForegroundWindow();
            }
            else
            {
                hWnd = FindWindowByTitle(windowTitle);
                if (hWnd == IntPtr.Zero)
                {
                    string visible = ListVisibleWindowTitles();
                    string noMatch = LocalizationService.Instance.Format("tool.captureWindow.noWindowFound", TruncateText(windowTitle, 80));
                    return string.IsNullOrEmpty(visible) ? noMatch : noMatch + "\n" + visible;
                }
            }

            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
                return LocalizationService.Instance["tool.captureWindow.noForegroundWindow"];

            try
            {
                string savePath;
                if (!string.IsNullOrWhiteSpace(savePathArg))
                {
                    savePath = Path.GetFullPath(savePathArg);
                    string? dir = Path.GetDirectoryName(savePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                }
                else
                {
                    Directory.CreateDirectory(CaptureTempDir);
                    savePath = Path.Combine(CaptureTempDir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                }

                var (capturedTitle, width, height, methodUsed) =
                    await CaptureToFileAsync(hWnd, savePath, maxWidth, captureMethod, CancellationToken);

                var sb = new StringBuilder();
                sb.AppendLine(LocalizationService.Instance.Format(
                    "tool.captureWindow.captured",
                    string.IsNullOrEmpty(capturedTitle) ? "（无标题）" : capturedTitle,
                    width,
                    height));
                sb.AppendLine($"- {LocalizationService.Instance["tool.captureWindow.savePath"]}: {savePath}");
                sb.AppendLine($"- {LocalizationService.Instance["tool.captureWindow.method"]}: {methodUsed}");
                if (methodUsed == "screen")
                    sb.AppendLine(LocalizationService.Instance["tool.captureWindow.screenNote"]);
                else if (methodUsed == "thumbnail")
                    sb.AppendLine(LocalizationService.Instance["tool.captureWindow.thumbnailNote"]);
                else if (methodUsed == "wgc")
                    sb.AppendLine(LocalizationService.Instance["tool.captureWindow.wgcNote"]);
                sb.AppendLine();
                sb.AppendLine(CaptureImageBlockStart);
                sb.AppendLine(savePath);
                sb.AppendLine(CaptureImageBlockEnd);
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                Logger.Error($"[capture_window] 截图失败: {ex.Message}", ex);
                return LocalizationService.Instance.Format("tool.captureWindow.failed", ex.Message);
            }
        }

        /// <summary>
        /// 解析工具结果文本中的图片块，返回剥离块后的纯文本与图片 data URI 列表。
        /// 块内每行既可以是一个 data URI（可直接使用），也可以是一个 PNG 文件路径（会被读取并转为 data URI）。
        /// </summary>
        public static (string CleanText, List<string> ImageDataUris) ParseImageBlock(string raw)
        {
            var uris = new List<string>();
            if (string.IsNullOrEmpty(raw))
                return (raw ?? string.Empty, uris);

            int start = raw.IndexOf(CaptureImageBlockStart, StringComparison.Ordinal);
            if (start < 0)
                return (raw, uris);

            int contentStart = start + CaptureImageBlockStart.Length;
            int end = raw.IndexOf(CaptureImageBlockEnd, contentStart, StringComparison.Ordinal);
            if (end < 0)
                return (raw, uris);

            string cleanText = raw.Remove(start, (end + CaptureImageBlockEnd.Length) - start).TrimEnd();
            string block = raw.Substring(contentStart, end - contentStart);

            foreach (string line in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = line.Trim();
                if (item.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    uris.Add(item);
                    continue;
                }

                try
                {
                    if (File.Exists(item))
                    {
                        byte[] bytes = File.ReadAllBytes(item);
                        uris.Add("data:image/png;base64," + Convert.ToBase64String(bytes));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[capture_window] 读取截图为 data URI 失败: {item} - {ex.Message}");
                }
            }

            return (cleanText, uris);
        }

        #region Window Capture (Win32)

        private static string GetWindowTitle(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private const int DwmwaExtendedFrameBounds = 9;

        private static IntPtr FindWindowByTitle(string titleSubstring)
        {
            var candidates = new List<IntPtr>();
            EnumWindows((hWnd, lParam) =>
            {
                string title = GetWindowTitle(hWnd);
                if (title.Length > 0
                    && title.IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    candidates.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);

            // 命中多个窗口时选真实边界最大的（主窗口），避免选中标题栏/通知/小弹窗。
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            foreach (IntPtr h in candidates)
            {
                long area = GetWindowArea(h);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = h;
                }
            }
            return best;
        }

        /// <summary>
        /// 取窗口真实边界面积（优先 DWM 扩展边界，最小化窗口也会返回还原尺寸）；
        /// 失败时回退 GetWindowRect，用于按面积挑选主窗口。
        /// </summary>
        private static long GetWindowArea(IntPtr hWnd)
        {
            if (DwmGetWindowAttribute(hWnd, DwmwaExtendedFrameBounds, out RECT r, Marshal.SizeOf(typeof(RECT))) == 0)
            {
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > 0) return area;
            }
            if (GetWindowRect(hWnd, out RECT r2))
                return (long)(r2.Right - r2.Left) * (r2.Bottom - r2.Top);
            return -1;
        }

        private static string ListVisibleWindowTitles()
        {
            var titles = new List<string>();
            EnumWindows((hWnd, lParam) =>
            {
                if (titles.Count >= 20) return false;
                if (!IsWindowVisible(hWnd)) return true;
                string t = GetWindowTitle(hWnd);
                if (t.Length > 0) titles.Add(t);
                return true;
            }, IntPtr.Zero);

            if (titles.Count == 0) return string.Empty;
            string hint = LocalizationService.Instance["tool.captureWindow.visibleWindowsHint"];
            return hint + "\n" + string.Join("\n", titles.Select(t => $"  - {t}"));
        }

        private static async Task<(string Title, int Width, int Height, string MethodUsed)> CaptureToFileAsync(
            IntPtr hWnd, string savePath, int maxWidth, string captureMethod, CancellationToken ct)
        {
            string title = GetWindowTitle(hWnd);
            string method = NormalizeCaptureMethod(captureMethod);

            var (bitmap, methodUsed) = await CaptureBitmapAsync(hWnd, method, ct).ConfigureAwait(false);
            using (var src = bitmap)
            {
                ScaleAndSave(src, savePath, maxWidth, out int width, out int height);
                return (title, width, height, methodUsed);
            }
        }

        /// <summary>等比缩放（受 maxWidth 与默认长边上限约束）并保存为 PNG。</summary>
        private static void ScaleAndSave(Bitmap src, string savePath, int maxWidth, out int width, out int height)
        {
            int srcW = src.Width;
            int srcH = src.Height;

            Bitmap? final = src;
            bool ownsFinal = false;
            try
            {
                int dstW = srcW;
                int dstH = srcH;
                // 等比例缩放：同时受 maxWidth（如指定）与默认长边上限约束。
                double scale = 1.0;
                if (maxWidth > 0 && srcW > maxWidth)
                    scale = Math.Min(scale, (double)maxWidth / srcW);
                int longEdge = Math.Max(srcW, srcH);
                if (longEdge > DefaultMaxLongEdgePx)
                    scale = Math.Min(scale, (double)DefaultMaxLongEdgePx / longEdge);

                if (scale < 1.0)
                {
                    dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                    dstH = Math.Max(1, (int)Math.Round(srcH * scale));
                    final = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
                    ownsFinal = true;
                    using (var g = Graphics.FromImage(final))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(src, 0, 0, dstW, dstH);
                    }
                }

                width = dstW;
                height = dstH;
                final.Save(savePath, ImageFormat.Png);
            }
            finally
            {
                if (ownsFinal)
                    final?.Dispose();
            }
        }

        /// <summary>
        /// 规范化 capture_method 参数：auto / printwindow / screen。
        /// </summary>
        private static string NormalizeCaptureMethod(string? method)
        {
            switch (method?.Trim().ToLowerInvariant())
            {
                case "wgc":
                case "windowsgraphicscapture":
                case "graphics_capture":
                case "graphicscapture":
                    return "wgc";
                case "printwindow":
                case "print_window":
                case "paint":
                    return "printwindow";
                case "thumbnail":
                case "dwm":
                case "dwmthumbnail":
                case "dwm_thumbnail":
                    return "thumbnail";
                case "screen":
                case "copyfromscreen":
                case "copy_from_screen":
                case "framebuffer":
                    return "screen";
                default:
                    return "auto";
            }
        }

        /// <summary>
        /// 按指定方式生成窗口的原始尺寸位图，并回传实际使用的方式。
        /// wgc 走官方 Windows Graphics Capture；其余走传统 Win32 通道。
        /// </summary>
        private static async Task<(Bitmap Bitmap, string MethodUsed)> CaptureBitmapAsync(
            IntPtr hWnd, string method, CancellationToken ct)
        {
            switch (method)
            {
                case "wgc":
                    if (!GraphicsCaptureService.IsWindowCaptureSupported())
                        throw new InvalidOperationException(
                            "当前环境不支持 WGC 窗口捕获（IGraphicsCaptureItemInterop 不可用，常见于远程桌面/虚拟机会话）");
                    return (await CaptureWgcWithTimeoutAsync(hWnd, ct).ConfigureAwait(false), "wgc");

                case "printwindow":
                case "thumbnail":
                case "screen":
                {
                    var bmp = CaptureBitmap(hWnd, method, out string used);
                    return (bmp, used);
                }

                default: // auto：官方 WGC 优先（仅在该环境支持时），否则直接走传统通道（含后台 DWM 缩略图）
                    if (GraphicsCaptureService.IsWindowCaptureSupported())
                    {
                        try
                        {
                            var wgcBmp = await CaptureWgcWithTimeoutAsync(hWnd, ct).ConfigureAwait(false);
                            if (!IsMostlyBlank(wgcBmp))
                                return (wgcBmp, "wgc");
                            wgcBmp.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[capture_window] WGC 捕获失败，回退传统通道: {ex.Message}");
                        }
                    }

                    var legacyBmp = CaptureBitmap(hWnd, "auto", out string legacyUsed);
                    return (legacyBmp, legacyUsed);
            }
        }

        /// <summary>带硬超时地执行 WGC 捕获，确保任何同步阻塞都不会卡死工具循环。</summary>
        private static async Task<Bitmap> CaptureWgcWithTimeoutAsync(IntPtr hWnd, CancellationToken ct)
        {
            var captureTask = GraphicsCaptureService.CaptureWindowAsync(hWnd, ct);
            var timeoutTask = Task.Delay(5000, ct);
            var done = await Task.WhenAny(captureTask, timeoutTask).ConfigureAwait(false);
            if (done == timeoutTask)
                throw new TimeoutException("WGC 捕获整体超时（5s）");
            return await captureTask.ConfigureAwait(false);
        }

        /// <summary>
        /// 按指定方式生成窗口的原始尺寸位图，并回传实际使用的方式。
        /// </summary>
        private static Bitmap CaptureBitmap(IntPtr hWnd, string method, out string methodUsed)
        {
            if (!GetWindowRect(hWnd, out RECT rect))
                throw new InvalidOperationException("无法获取窗口区域 (GetWindowRect 失败)");

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException($"窗口尺寸无效 ({w}x{h})");

            // screen：直接读取 GPU 合成后的真实屏幕帧（窗口需可见）。
            if (method == "screen")
            {
                methodUsed = "screen";
                return CaptureViaScreen(hWnd, rect, w, h);
            }

            // thumbnail：DWM 缩略图投影，可捕获后台/被遮挡的 GPU 窗口。
            if (method == "thumbnail")
            {
                methodUsed = "thumbnail";
                return CaptureViaDwmThumbnail(hWnd, w, h);
            }

            // printwindow / auto：先尝试 PrintWindow（最快，可捕获多数后台窗口）。
            var bmp = CaptureViaPrintWindow(hWnd, w, h);
            if (!IsMostlyBlank(bmp) || method == "printwindow")
            {
                methodUsed = "printwindow";
                return bmp;
            }
            bmp.Dispose();

            // auto：PrintWindow 黑屏 → 优先 DWM 缩略图（后台 GPU 窗口），失败再回退屏幕帧。
            try
            {
                var thumb = CaptureViaDwmThumbnail(hWnd, w, h);
                if (!IsMostlyBlank(thumb))
                {
                    methodUsed = "thumbnail";
                    return thumb;
                }
                thumb.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[capture_window] DWM 缩略图捕获失败，回退屏幕帧: {ex.Message}");
            }

            methodUsed = "screen";
            return CaptureViaScreen(hWnd, rect, w, h);
        }

        /// <summary>
        /// PrintWindow 捕获：依次尝试 渲染完整内容 → 仅客户区 → 经典 GDI，取第一个非空结果。
        /// </summary>
        private static Bitmap CaptureViaPrintWindow(IntPtr hWnd, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    foreach (uint flags in new uint[] { PrintWindowRenderFullContent, 0x1, 0x0 })
                    {
                        PrintWindow(hWnd, hdc, flags);
                        if (!IsMostlyBlank(bmp))
                            break;
                    }
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            return bmp;
        }

        /// <summary>
        /// 屏幕帧捕获：读取 GPU 合成后的真实桌面帧。先把窗口还原/置前，最大化成功概率。
        /// 注意：只能捕获窗口在屏幕上的可见区域，被遮挡部分会显示遮挡窗口。
        /// </summary>
        private static Bitmap CaptureViaScreen(IntPtr hWnd, RECT rect, int w, int h)
        {
            ActivateWindow(hWnd);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        /// <summary>
        /// 尝试还原并置前目标窗口（屏幕帧捕获需要窗口可见）。尽力而为，不抛异常。
        /// </summary>
        private static void ActivateWindow(IntPtr hWnd)
        {
            try
            {
                ShowWindow(hWnd, SwRestore);
                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);
            }
            catch
            {
                // 置前失败不影响后续屏幕帧捕获，忽略。
            }

            Thread.Sleep(250); // 给窗口合成/重绘留出时间
        }

        /// <summary>
        /// DWM 缩略图捕获：桌面合成器把目标窗口（即便后台/被遮挡/最小化）投影到宿主窗口，
        /// 再从宿主窗口读回像素。这是捕获后台 GPU 窗口内容的关键通道。
        /// </summary>
        private static Bitmap CaptureViaDwmThumbnail(IntPtr hWnd, int w, int h)
        {
            IntPtr host = CreateThumbnailHost(w, h);
            if (host == IntPtr.Zero)
                throw new InvalidOperationException("无法创建 DWM 缩略图宿主窗口");

            IntPtr thumb = IntPtr.Zero;
            try
            {
                ShowWindow(host, SwShowNoActivate);

                int hr = DwmRegisterThumbnail(host, hWnd, out thumb);
                if (hr != 0 || thumb == IntPtr.Zero)
                    throw new InvalidOperationException($"DwmRegisterThumbnail 失败 (HRESULT=0x{hr:X8})");

                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DwmTnpVisible | DwmTnpRectDestination | DwmTnpOpacity | DwmTnpSourceClientAreaOnly,
                    rcDestination = new RECT { Left = 0, Top = 0, Right = w, Bottom = h },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false,
                };
                hr = DwmUpdateThumbnailProperties(thumb, ref props);
                if (hr != 0)
                    throw new InvalidOperationException($"DwmUpdateThumbnailProperties 失败 (HRESULT=0x{hr:X8})");

                // 等待 DWM 合成首帧缩略图
                Thread.Sleep(400);

                // 方式 A：从宿主窗口读回合成后的缩略图（无需上屏像素级别抓取）
                var bmp = ReadBackWindow(host, w, h);
                if (bmp != null && !IsMostlyBlank(bmp))
                    return bmp;
                bmp?.Dispose();

                // 方式 B：宿主窗口屏幕帧回退（缩略图已投影到宿主窗口可见区域）
                return CaptureTopLeftRegion(w, h);
            }
            finally
            {
                if (thumb != IntPtr.Zero)
                    DwmUnregisterThumbnail(thumb);
                if (host != IntPtr.Zero)
                    DestroyWindow(host);
            }
        }

        /// <summary>
        /// 创建承载 DWM 缩略图的宿主窗口（系统内置 STATIC 类，免注册窗口类）。
        /// </summary>
        private static IntPtr CreateThumbnailHost(int w, int h)
        {
            IntPtr hInstance = GetModuleHandle(null);
            return CreateWindowEx(
                WsExToolWindow | WsExNoActivate,
                "STATIC", string.Empty,
                WsPopup,
                0, 0, w, h,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        }

        /// <summary>
        /// 通过 PrintWindow(PW_RENDERFULLCONTENT) 从指定窗口读回位图。
        /// </summary>
        private static Bitmap? ReadBackWindow(IntPtr hWnd, int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    PrintWindow(hWnd, hdc, PrintWindowRenderFullContent);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            return bmp;
        }

        /// <summary>
        /// 抓取屏幕左上角 (0,0) 起的 w×h 区域（宿主窗口被创建在此处）。
        /// </summary>
        private static Bitmap CaptureTopLeftRegion(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        /// <summary>
        /// 采样判断位图是否"近乎全黑/全透明"，用于识别 PrintWindow 渲染失败的黑屏。
        /// </summary>
        private static bool IsMostlyBlank(Bitmap bmp)
        {
            const int grid = 12;
            int dark = 0;
            int total = 0;

            for (int y = 0; y < grid; y++)
            {
                for (int x = 0; x < grid; x++)
                {
                    int px = Math.Min(bmp.Width - 1, x * bmp.Width / Math.Max(1, grid - 1));
                    int py = Math.Min(bmp.Height - 1, y * bmp.Height / Math.Max(1, grid - 1));
                    Color c = bmp.GetPixel(px, py);
                    total++;
                    if (c.A < 16 || (c.R < 28 && c.G < 28 && c.B < 28))
                        dark++;
                }
            }

            // 超过 97% 的采样点为近黑/全透明即视为空白。
            return total > 0 && dark * 100 >= total * 97;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fSourceClientAreaOnly;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint nFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr thumbnailId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumbnailId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnailId, ref DWM_THUMBNAIL_PROPERTIES properties);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int cbAttribute);

        #endregion
    }
}
