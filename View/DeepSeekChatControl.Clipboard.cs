using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 剪贴板图片粘贴：检测、保存、DIB 解码等。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Clipboard Constants

        /// <summary>
        /// 存储剪贴板图片的临时目录（%LocalAppData%\DeepSeekVS\temp\clipboard\）。
        /// </summary>
        private static readonly string ClipboardTempDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "temp", "clipboard");

        #endregion

        #region Clipboard Methods

        /// <summary>
        /// 判断剪贴板中是否包含可粘贴的图像。
        /// 检查多种剪贴板格式：WPF Image、DIB、Bitmap、PNG 流等。
        /// </summary>
        private static bool CanPasteImage()
        {
            try
            {
                // 方式 1: WPF 原生 ContainsImage（检测 BitmapSource 可转换格式）
                if (System.Windows.Clipboard.ContainsImage())
                {
                    Logger.Info("剪贴板检测: ContainsImage() = true");
                    return true;
                }

                // 方式 2: 检查 DIB (Device Independent Bitmap) 格式
                if (System.Windows.Clipboard.ContainsData(System.Windows.DataFormats.Dib))
                {
                    Logger.Info("剪贴板检测: ContainsData(Dib) = true");
                    return true;
                }

                // 方式 3: 检查 Bitmap 格式
                if (System.Windows.Clipboard.ContainsData(System.Windows.DataFormats.Bitmap))
                {
                    Logger.Info("剪贴板检测: ContainsData(Bitmap) = true");
                    return true;
                }

                // 方式 4: 检查 PNG 流格式（部分浏览器使用）
                if (System.Windows.Clipboard.ContainsData("PNG"))
                {
                    Logger.Info("剪贴板检测: ContainsData(PNG) = true");
                    return true;
                }

                // 方式 5: 检查 FileDrop 中的图片文件（从资源管理器复制图片文件时）
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var files = System.Windows.Clipboard.GetFileDropList();
                    foreach (string file in files)
                    {
                        if (OcrService.IsImageFile(file))
                        {
                            Logger.Info($"剪贴板检测: FileDrop 包含图片 - {System.IO.Path.GetFileName(file)}");
                            return true;
                        }
                    }
                }

                Logger.Info("剪贴板检测: 未发现任何图片格式");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"剪贴板图片检测异常: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// 尝试从剪贴板粘贴图片（由 PreviewKeyDown 的 Ctrl+V 调用）。
        /// 返回 true 表示成功处理了图片粘贴。
        /// </summary>
        private bool TryPasteClipboardImage()
        {
            if (!CanPasteImage())
            {
                Logger.Info("TryPasteClipboardImage: 剪贴板无图片，跳过。");
                return false;
            }

            try
            {
                Logger.Info("TryPasteClipboardImage: 开始处理剪贴板图片...");
                string? tempFilePath = SaveClipboardImageToTempFile();
                if (tempFilePath == null)
                {
                    Logger.Error("TryPasteClipboardImage: SaveClipboardImageToTempFile 返回 null");
                    return false;
                }

                if (!_attachedFilePaths.Contains(tempFilePath, StringComparer.OrdinalIgnoreCase))
                {
                    _attachedFilePaths.Add(tempFilePath);
                    RefreshAttachedFilesUI();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.clipboardImagePasted"], System.IO.Path.GetFileName(tempFilePath));
                    Logger.Info($"TryPasteClipboardImage: 图片已添加为附件 - {tempFilePath}");
                }
                else
                {
                    Logger.Info($"TryPasteClipboardImage: 图片已存在于附件列表 - {tempFilePath}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"TryPasteClipboardImage 失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.clipboardPasteFailed"], ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 执行剪贴板图片粘贴（CommandBinding 回调，作为后备路径）。
        /// 将剪贴板中的图像保存为临时 PNG 文件，并添加到附件列表中。
        /// 仅在剪贴板包含图像时处理；文本粘贴由 TextBox 默认行为处理。
        /// </summary>
        private void ExecutePasteImage(ExecutedRoutedEventArgs e)
        {
            Logger.Info("ExecutePasteImage: CommandBinding 回调触发");

            // 仅处理图像粘贴，文本粘贴交给 TextBox 默认行为
            if (!System.Windows.Clipboard.ContainsImage())
            {
                Logger.Info("ExecutePasteImage: ContainsImage() = false，跳过。");
                return;
            }

            try
            {
                Logger.Info("ExecutePasteImage: 开始保存剪贴板图片...");
                string? tempFilePath = SaveClipboardImageToTempFile();
                if (tempFilePath == null)
                {
                    Logger.Error("ExecutePasteImage: SaveClipboardImageToTempFile 返回 null");
                    return;
                }

                if (!_attachedFilePaths.Contains(tempFilePath, StringComparer.OrdinalIgnoreCase))
                {
                    _attachedFilePaths.Add(tempFilePath);
                    RefreshAttachedFilesUI();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.clipboardImagePasted"], System.IO.Path.GetFileName(tempFilePath));
                    Logger.Info($"ExecutePasteImage: 图片已添加为附件 - {tempFilePath}");
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"ExecutePasteImage 失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.clipboardPasteFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 将剪贴板中的图像保存为临时 PNG 文件。
        /// 支持多种剪贴板格式：WPF Image、DIB、Bitmap、PNG 流、FileDrop。
        /// 文件名格式：clipboard_yyyyMMdd_HHmmss_fff.png。
        /// </summary>
        /// <returns>临时文件的完整路径，失败返回 null。</returns>
        private static string? SaveClipboardImageToTempFile()
        {
            try
            {
                BitmapSource? bitmap = null;

                // ── 方式 1: WPF 原生 GetImage ──
                try
                {
                    bitmap = System.Windows.Clipboard.GetImage();
                    if (bitmap != null)
                    {
                        Logger.Info($"SaveClipboardImage: 通过 GetImage() 获取，尺寸={bitmap.PixelWidth}x{bitmap.PixelHeight}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"SaveClipboardImage: GetImage() 失败 - {ex.Message}");
                }

                // ── 方式 2: 从 DIB 格式解码 ──
                if (bitmap == null)
                {
                    try
                    {
                        if (System.Windows.Clipboard.ContainsData(System.Windows.DataFormats.Dib))
                        {
                            var dibData = System.Windows.Clipboard.GetData(System.Windows.DataFormats.Dib);
                            bitmap = ConvertDibToBitmapSource(dibData);
                            if (bitmap != null)
                                Logger.Info($"SaveClipboardImage: 通过 DIB 解码，尺寸={bitmap.PixelWidth}x{bitmap.PixelHeight}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"SaveClipboardImage: DIB 解码失败 - {ex.Message}");
                    }
                }

                // ── 方式 3: 从 PNG 流格式解码 ──
                if (bitmap == null)
                {
                    try
                    {
                        if (System.Windows.Clipboard.ContainsData("PNG"))
                        {
                            var pngData = System.Windows.Clipboard.GetData("PNG");
                            if (pngData is System.IO.MemoryStream pngStream)
                            {
                                var decoder = new PngBitmapDecoder(
                                    pngStream,
                                    BitmapCreateOptions.PreservePixelFormat,
                                    BitmapCacheOption.OnLoad);
                                bitmap = decoder.Frames[0];
                                Logger.Info($"SaveClipboardImage: 通过 PNG 流解码，尺寸={bitmap.PixelWidth}x{bitmap.PixelHeight}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"SaveClipboardImage: PNG 流解码失败 - {ex.Message}");
                    }
                }

                // ── 方式 4: 从 FileDrop 加载首个图片文件 ──
                if (bitmap == null)
                {
                    try
                    {
                        if (System.Windows.Clipboard.ContainsFileDropList())
                        {
                            var files = System.Windows.Clipboard.GetFileDropList();
                            foreach (string file in files)
                            {
                                if (OcrService.IsImageFile(file))
                                {
                                    Logger.Info($"SaveClipboardImage: 从 FileDrop 加载 - {System.IO.Path.GetFileName(file)}");
                                    var decoder = new PngBitmapDecoder(
                                        new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read),
                                        BitmapCreateOptions.PreservePixelFormat,
                                        BitmapCacheOption.OnLoad);
                                    bitmap = decoder.Frames[0];
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"SaveClipboardImage: FileDrop 加载失败 - {ex.Message}");
                    }
                }

                if (bitmap == null)
                {
                    Logger.Error("SaveClipboardImage: 所有方式均无法从剪贴板获取图像");
                    return null;
                }

                // ── 确保目录存在 ──
                try
                {
                    System.IO.Directory.CreateDirectory(ClipboardTempDir);
                }
                catch (Exception ex)
                {
                    Logger.Error($"SaveClipboardImage: 创建临时目录失败 - {ex.Message}", ex);
                    return null;
                }

                string fileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                string filePath = System.IO.Path.Combine(ClipboardTempDir, fileName);

                Logger.Info($"SaveClipboardImage: 正在保存 → {filePath}");

                // 使用 PngBitmapEncoder 保存为 PNG（保持最佳质量，适合 OCR）
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write);
                encoder.Save(stream);

                long fileSize = new System.IO.FileInfo(filePath).Length;
                Logger.Info($"SaveClipboardImage: 保存成功，大小={FormatFileSize(fileSize)} ← {fileName}");

                return filePath;
            }
            catch (Exception ex)
            {
                Logger.Error($"SaveClipboardImage: 保存失败 - {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 将 DIB (Device Independent Bitmap) 原始数据转换为 BitmapSource。
        /// DIB 格式 = BITMAPINFOHEADER + 像素数据。
        /// </summary>
        private static BitmapSource? ConvertDibToBitmapSource(object? dibData)
        {
            if (dibData == null) return null;

            try
            {
                byte[] dibBytes;
                if (dibData is byte[] bytes)
                    dibBytes = bytes;
                else if (dibData is System.IO.MemoryStream ms)
                    dibBytes = ms.ToArray();
                else
                {
                    Logger.Info($"ConvertDibToBitmapSource: 不支持的 DIB 数据类型 - {dibData.GetType().Name}");
                    return null;
                }

                if (dibBytes.Length < 40)
                {
                    Logger.Info($"ConvertDibToBitmapSource: DIB 数据过短 ({dibBytes.Length} bytes)");
                    return null;
                }

                // 解析 BITMAPINFOHEADER
                int width = BitConverter.ToInt32(dibBytes, 4);
                int height = Math.Abs(BitConverter.ToInt32(dibBytes, 8)); // 高度可能为负（top-down）
                short bitsPerPixel = BitConverter.ToInt16(dibBytes, 14);
                int compression = BitConverter.ToInt32(dibBytes, 16);
                int imageSize = BitConverter.ToInt32(dibBytes, 20);

                Logger.Info($"ConvertDibToBitmapSource: DIB header - {width}x{height}, {bitsPerPixel}bpp, compression={compression}");

                // 计算像素数据偏移
                int headerSize = BitConverter.ToInt32(dibBytes, 0);
                int pixelOffset = headerSize;
                int stride = ((width * bitsPerPixel + 31) / 32) * 4;

                System.Windows.Media.PixelFormat format;
                if (bitsPerPixel == 32)
                    format = System.Windows.Media.PixelFormats.Bgra32;
                else if (bitsPerPixel == 24)
                    format = System.Windows.Media.PixelFormats.Bgr24;
                else
                {
                    Logger.Info($"ConvertDibToBitmapSource: 不支持的位深度 {bitsPerPixel}");
                    return null;
                }

                int expectedSize = pixelOffset + stride * height;
                if (dibBytes.Length < expectedSize)
                {
                    Logger.Info($"ConvertDibToBitmapSource: DIB 数据不足 (实际={dibBytes.Length}, 期望={expectedSize})");
                    return null;
                }

                // 提取像素数据
                byte[] pixelData = new byte[stride * height];
                Array.Copy(dibBytes, pixelOffset, pixelData, 0, pixelData.Length);

                // DIB 是 bottom-up，需要翻转
                if (BitConverter.ToInt32(dibBytes, 8) > 0)
                {
                    // 正高度 = bottom-up，需要翻转行
                    byte[] flippedData = new byte[pixelData.Length];
                    for (int y = 0; y < height; y++)
                    {
                        int srcRow = (height - 1 - y) * stride;
                        int dstRow = y * stride;
                        Array.Copy(pixelData, srcRow, flippedData, dstRow, stride);
                    }
                    pixelData = flippedData;
                }

                return BitmapSource.Create(
                    width, height, 96, 96, format, null, pixelData, stride);
            }
            catch (Exception ex)
            {
                Logger.Error($"ConvertDibToBitmapSource 失败: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 格式化文件大小为可读字符串。
        /// </summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        #endregion
    }
}
