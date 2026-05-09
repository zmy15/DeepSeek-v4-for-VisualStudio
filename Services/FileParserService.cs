using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 文件解析服务，支持解析常见文档格式的文本内容。
    /// 支持的格式: .txt, .c, .py, .cs, .cpp, .h, .java, .js, .ts,
    /// .html, .css, .xml, .json, .yaml, .yml, .md, .sql, .doc, .docx, .xls, .xlsx,
    /// .pdf, .png, .jpg, .jpeg, .bmp, .gif, .tiff, .tif
    /// </summary>
    public static class FileParserService
    {
        #region Constants

        /// <summary>
        /// 可解析的文本文件扩展名集合（直接以纯文本读取）。
        /// </summary>
        private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".c", ".py", ".cs", ".cpp", ".cc", ".cxx", ".h", ".hpp",
            ".java", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css",
            ".xml", ".json", ".yaml", ".yml", ".md", ".sql", ".php", ".rb",
            ".go", ".rs", ".swift", ".kt", ".scala", ".lua", ".pl", ".sh",
            ".bat", ".ps1", ".ini", ".cfg", ".conf", ".log", ".csv", ".tsv",
            ".r", ".m", ".mm", ".f", ".f90", ".vb", ".vbproj", ".csproj",
            ".sln", ".xaml", ".axml", ".gradle", ".dockerfile", ".gitignore",
            ".editorconfig", ".env", ".toml", ".nim", ".zig", ".v", ".sv",
            ".proto", ".cmake", ".makefile", ".rst",
        };

        /// <summary>
        /// Word 文档扩展名集合。
        /// </summary>
        private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx",
        };

        /// <summary>
        /// Excel 文档扩展名集合。
        /// </summary>
        private static readonly HashSet<string> ExcelExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".xls", ".xlsx",
        };

        /// <summary>
        /// PDF 文档扩展名集合。
        /// </summary>
        private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
        };

        /// <summary>
        /// 支持 OCR 的图像文件扩展名集合。
        /// </summary>
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif", ".webp",
        };

        /// <summary>单文件最大解析大小（字节），超过则截断并提示。</summary>
        private const long MaxFileSizeBytes = 2 * 1024 * 1024; // 2MB

        /// <summary>PDF 文件最大解析大小（字节），超过则截断并提示。</summary>
        private const long MaxPdfFileSizeBytes = 20 * 1024 * 1024; // 20MB

        /// <summary>解析后文本最大长度（字符），超过则截断并提示。</summary>
        private const int MaxParsedChars = 200000;

        #endregion

        #region Public Methods

        /// <summary>
        /// 检查文件扩展名是否受支持。
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>true 表示可以解析</returns>
        public static bool IsSupportedFormat(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string ext = Path.GetExtension(filePath);
            return TextFileExtensions.Contains(ext)
                || WordExtensions.Contains(ext)
                || ExcelExtensions.Contains(ext)
                || PdfExtensions.Contains(ext)
                || ImageExtensions.Contains(ext);
        }

        /// <summary>
        /// 获取受支持的文件扩展名列表（用于文件对话框过滤器）。
        /// </summary>
        /// <returns>文件对话框过滤器字符串</returns>
        public static string GetFileFilter()
        {
            return "所有支持的文件|*.txt;*.c;*.py;*.cs;*.cpp;*.h;*.java;*.js;*.ts;*.html;*.css;*.xml;*.json;*.yaml;*.yml;*.md;*.sql;*.doc;*.docx;*.xls;*.xlsx;*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|" +
                   "文本文件|*.txt;*.c;*.py;*.cs;*.cpp;*.h;*.java;*.js;*.ts;*.html;*.css;*.xml;*.json;*.yaml;*.yml;*.md;*.sql;*.csv|" +
                   "Word 文档|*.doc;*.docx|" +
                   "Excel 文档|*.xls;*.xlsx|" +
                   "PDF 文档|*.pdf|" +
                   "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|" +
                   "所有文件|*.*";
        }

        /// <summary>
        /// 解析单个文件，提取其文本内容。
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>解析结果，包含文件名、扩展名和文本内容</returns>
        public static async Task<FileParseResult> ParseFileAsync(string filePath)
        {
            var result = new FileParseResult
            {
                FileName = Path.GetFileName(filePath),
                FileExtension = Path.GetExtension(filePath),
                FilePath = filePath,
            };

            try
            {
                // 检查文件是否存在
                if (!File.Exists(filePath))
                {
                    result.Error = $"文件不存在: {filePath}";
                    return result;
                }

                // 检查文件大小（PDF 单独设置更大的限制）
                var fileInfo = new FileInfo(filePath);
                string ext = result.FileExtension.ToLowerInvariant();
                long maxSize = PdfExtensions.Contains(ext) ? MaxPdfFileSizeBytes : MaxFileSizeBytes;
                if (fileInfo.Length > maxSize)
                {
                    result.Truncated = true;
                    result.TruncationNote = $"⚠️ 文件过大 ({FormatFileSize(fileInfo.Length)})，仅解析前 {FormatFileSize(maxSize)}。";
                }

                // 文本文件直接以 UTF-8 读取
                if (TextFileExtensions.Contains(ext))
                {
                    result.Content = await ReadTextFileAsync(filePath);
                }
                // Word 文档
                else if (WordExtensions.Contains(ext))
                {
                    result.Content = await ParseWordDocumentAsync(filePath);
                }
                // Excel 文档
                else if (ExcelExtensions.Contains(ext))
                {
                    result.Content = await Task.Run(() => ParseExcelDocument(filePath));
                }
                // PDF 文档
                else if (PdfExtensions.Contains(ext))
                {
                    result.Content = await Task.Run(() => ParsePdfDocument(filePath));
                }
                // 图像文件（OCR）
                else if (ImageExtensions.Contains(ext))
                {
                    result.Content = await ParseImageFileAsync(filePath);
                }
                else
                {
                    result.Error = $"不支持的文件格式: {ext}";
                    return result;
                }

                // 截断过长的内容
                if (result.Content != null && result.Content.Length > MaxParsedChars)
                {
                    result.Content = result.Content.Substring(0, MaxParsedChars);
                    result.Truncated = true;
                    result.TruncationNote = (result.TruncationNote != null ? result.TruncationNote + " " : "")
                        + $"⚠️ 内容过长，已截断至 {MaxParsedChars} 字符。";
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"文件解析失败: {filePath}", ex);
                result.Error = $"解析失败: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 批量解析多个文件。
        /// </summary>
        /// <param name="filePaths">文件路径列表</param>
        /// <returns>解析结果列表</returns>
        public static async Task<List<FileParseResult>> ParseFilesAsync(IEnumerable<string> filePaths)
        {
            var results = new List<FileParseResult>();
            foreach (var path in filePaths)
            {
                var result = await ParseFileAsync(path);
                results.Add(result);
            }
            return results;
        }

        /// <summary>
        /// 将解析结果格式化为可发送给 AI 的上下文字符串。
        /// </summary>
        /// <param name="results">解析结果列表</param>
        /// <returns>格式化的上下文字符串</returns>
        public static string FormatParseResultsForContext(List<FileParseResult> results)
        {
            if (results == null || results.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("=== 用户上传的文件 ===");
            sb.AppendLine();

            foreach (var r in results)
            {
                if (!r.Success)
                {
                    sb.AppendLine($"📄 {r.FileName} - ❌ 解析失败: {r.Error}");
                    sb.AppendLine();
                    continue;
                }

                string lang = GetLanguageFromExtension(r.FileExtension);
                sb.AppendLine($"📄 {r.FileName}");

                if (r.Truncated && !string.IsNullOrEmpty(r.TruncationNote))
                {
                    sb.AppendLine(r.TruncationNote);
                }

                sb.AppendLine("```" + lang);
                sb.AppendLine(r.Content);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            sb.AppendLine("=== 文件结束 ===");
            return sb.ToString();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 以 UTF-8 读取文本文件，遇到非 UTF-8 编码时回退到系统默认编码。
        /// </summary>
        private static async Task<string> ReadTextFileAsync(string filePath)
        {
            try
            {
                using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync();
            }
            catch (DecoderFallbackException)
            {
                // UTF-8 解码失败，回退到系统默认编码
                using var reader = new StreamReader(filePath, Encoding.Default);
                return await reader.ReadToEndAsync();
            }
        }

        /// <summary>
        /// 解析 Word 文档（.doc 和 .docx）。
        /// </summary>
        private static async Task<string> ParseWordDocumentAsync(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".docx")
                return await ParseDocxAsync(filePath);
            else
                return ParseDoc(filePath);
        }

        /// <summary>
        /// 解析 .docx 文件（OOXML 格式），支持提取图片并 OCR 插入对应位置。
        /// </summary>
        private static async Task<string> ParseDocxAsync(string filePath)
        {
            var sb = new StringBuilder();
            string? tempOcrDir = null;

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var doc = new XWPFDocument(stream);

                // 为 OCR 临时文件创建目录
                tempOcrDir = Path.Combine(Path.GetTempPath(), "DeepSeek_DocxOCR_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempOcrDir);

                foreach (var paragraph in doc.Paragraphs)
                {
                    var paraSb = new StringBuilder();

                    foreach (var run in paragraph.Runs)
                    {
                        // ── 检查 run 中是否包含嵌入图片 ──
                        try
                        {
                            var embeddedPictures = run.GetEmbeddedPictures();
                            if (embeddedPictures != null && embeddedPictures.Count > 0)
                            {
                                foreach (var picture in embeddedPictures)
                                {
                                    var picData = picture?.GetPictureData();
                                    if (picData?.Data != null)
                                    {
                                        string? ocrResult = await OcrPictureDataAsync(
                                            picData, tempOcrDir);

                                        if (!string.IsNullOrWhiteSpace(ocrResult))
                                        {
                                            paraSb.AppendLine();
                                            paraSb.AppendLine("[📷 图片OCR内容]");
                                            paraSb.AppendLine(ocrResult);
                                            paraSb.AppendLine("[/📷 图片OCR内容]");
                                            paraSb.AppendLine();
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Info($"[Docx-OCR] 检查嵌入图片时出错: {ex.Message}");
                        }

                        // ── 获取 run 中的文本 ──
                        string? runText = run.GetText(0);
                        if (!string.IsNullOrEmpty(runText))
                        {
                            paraSb.Append(runText);
                        }
                    }

                    string paraText = paraSb.ToString().Trim();
                    if (!string.IsNullOrEmpty(paraText))
                        sb.AppendLine(paraText);
                }

                // 也提取表格内容
                foreach (var table in doc.Tables)
                {
                    sb.AppendLine();
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.GetTableCells())
                        {
                            string? cellText = cell.GetText()?.Trim();
                            if (!string.IsNullOrEmpty(cellText))
                                sb.Append(cellText + "\t");
                        }
                        sb.AppendLine();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"解析 .docx 文件失败: {filePath}", ex);
                return $"[解析失败: {ex.Message}]";
            }
            finally
            {
                // 清理 OCR 临时目录
                if (tempOcrDir != null && Directory.Exists(tempOcrDir))
                {
                    try { Directory.Delete(tempOcrDir, true); }
                    catch (Exception ex) { Logger.Info($"[Docx-OCR] 清理临时目录失败: {ex.Message}"); }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将 NPOI 图片数据保存为临时文件并进行 OCR。
        /// </summary>
        private static async Task<string?> OcrPictureDataAsync(
            NPOI.XWPF.UserModel.XWPFPictureData pictureData, string tempDir)
        {
            try
            {
                string ext = pictureData.SuggestFileExtension() ?? "png";
                if (!ext.StartsWith(".")) ext = "." + ext;

                string tempPath = Path.Combine(tempDir, $"ocr_{Guid.NewGuid():N}{ext}");
                File.WriteAllBytes(tempPath, pictureData.Data);

                Logger.Info($"[Docx-OCR] 开始识别嵌入图片 ({pictureData.Data.Length} bytes)...");
                string? ocrText = await OcrService.ExtractTextFromImageAsync(tempPath);

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    Logger.Info($"[Docx-OCR] ✅ 成功: {ocrText!.Length} 字符");
                    return ocrText;
                }
                else
                {
                    Logger.Info("[Docx-OCR] ⚠️ 未提取到文字");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[Docx-OCR] ❌ 图片 OCR 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析 .doc 文件（旧版二进制格式）。
        /// NPOI 2.7+ 已移除 HWPF 模块，使用备用文本提取方案。
        /// </summary>
        private static string ParseDoc(string filePath)
        {
            try
            {
                // 尝试从二进制 .doc 文件中提取可读文本
                return ExtractTextFromBinaryDoc(filePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"解析 .doc 文件失败: {filePath}", ex);
                return $"[解析失败: {ex.Message}。建议将 .doc 转换为 .docx 格式后重试。]";
            }
        }

        /// <summary>
        /// 从旧版 .doc 二进制文件中提取可读文本。
        /// .doc 使用 OLE2 (Compound File Binary) 格式存储，文本主要在 WordDocument 流中。
        /// 此方法使用简易的字符串提取，可能无法提取全部内容。
        /// </summary>
        private static string ExtractTextFromBinaryDoc(string filePath)
        {
            var sb = new StringBuilder();
            byte[] bytes = File.ReadAllBytes(filePath);

            // 尝试以多种编码提取可读文本段落
            var encodings = new[] { Encoding.Unicode, Encoding.UTF8, Encoding.Default };

            foreach (var enc in encodings)
            {
                try
                {
                    string text = enc.GetString(bytes);
                    // 提取连续的可打印字符段落
                    var matches = System.Text.RegularExpressions.Regex.Matches(
                        text, @"[\u4e00-\u9fff\u3000-\u303f\uff00-\uffefa-zA-Z0-9\s,.;:!?()（）、。，；：！？""''\-\[\]{}]{20,}");

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        string trimmed = match.Value.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed) && !sb.ToString().Contains(trimmed))
                        {
                            sb.AppendLine(trimmed);
                        }
                    }

                    if (sb.Length > 100)
                        break; // 已经提取到足够文本
                }
                catch
                {
                    // 尝试下一种编码
                }
            }

            string result = sb.ToString().Trim();
            if (string.IsNullOrEmpty(result))
            {
                return "[此 .doc 文件使用了旧版格式，无法直接解析文本内容。建议使用 Microsoft Word 将其另存为 .docx 格式后重试。]";
            }

            return "[注意: 此文件为旧版 .doc 格式，文本提取可能不完整。建议转换为 .docx 格式以获得最佳效果。]\n\n" + result;
        }

        /// <summary>
        /// 解析 Excel 文档（.xls 和 .xlsx）。
        /// </summary>
        private static string ParseExcelDocument(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".xlsx")
                return ParseXlsx(filePath);
            else
                return ParseXls(filePath);
        }

        /// <summary>
        /// 解析 .xlsx 文件（OOXML 格式）。
        /// </summary>
        private static string ParseXlsx(string filePath)
        {
            var sb = new StringBuilder();
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var workbook = new XSSFWorkbook(stream);

                for (int sheetIdx = 0; sheetIdx < workbook.NumberOfSheets; sheetIdx++)
                {
                    var sheet = workbook.GetSheetAt(sheetIdx);
                    sb.AppendLine($"--- 工作表: {sheet.SheetName} ---");

                    for (int rowIdx = sheet.FirstRowNum; rowIdx <= sheet.LastRowNum; rowIdx++)
                    {
                        var row = sheet.GetRow(rowIdx);
                        if (row == null) continue;

                        for (int colIdx = row.FirstCellNum; colIdx < row.LastCellNum; colIdx++)
                        {
                            var cell = row.GetCell(colIdx);
                            if (cell != null)
                            {
                                string cellValue = GetCellValueAsString(cell);
                                if (!string.IsNullOrEmpty(cellValue))
                                    sb.Append(cellValue + "\t");
                            }
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"解析 .xlsx 文件失败: {filePath}", ex);
                return $"[解析失败: {ex.Message}]";
            }

            return sb.ToString();
        }

        /// <summary>
        /// 解析 .xls 文件（旧版二进制格式）。
        /// </summary>
        private static string ParseXls(string filePath)
        {
            var sb = new StringBuilder();
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var workbook = new HSSFWorkbook(stream);

                for (int sheetIdx = 0; sheetIdx < workbook.NumberOfSheets; sheetIdx++)
                {
                    var sheet = workbook.GetSheetAt(sheetIdx);
                    sb.AppendLine($"--- 工作表: {sheet.SheetName} ---");

                    for (int rowIdx = sheet.FirstRowNum; rowIdx <= sheet.LastRowNum; rowIdx++)
                    {
                        var row = sheet.GetRow(rowIdx);
                        if (row == null) continue;

                        for (int colIdx = row.FirstCellNum; colIdx < row.LastCellNum; colIdx++)
                        {
                            var cell = row.GetCell(colIdx);
                            if (cell != null)
                            {
                                string cellValue = GetCellValueAsString(cell);
                                if (!string.IsNullOrEmpty(cellValue))
                                    sb.Append(cellValue + "\t");
                            }
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"解析 .xls 文件失败: {filePath}", ex);
                return $"[解析失败: {ex.Message}]";
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将 Excel 单元格值转换为字符串。
        /// </summary>
        private static string GetCellValueAsString(NPOI.SS.UserModel.ICell cell)
        {
            if (cell == null) return string.Empty;

            switch (cell.CellType)
            {
                case CellType.String:
                    return cell.StringCellValue?.Trim() ?? string.Empty;

                case CellType.Numeric:
                    // 判断是否为日期格式
                    if (DateUtil.IsCellDateFormatted(cell))
                    {
                        try { return cell.DateCellValue?.ToString("yyyy-MM-dd") ?? string.Empty; }
                        catch { return cell.NumericCellValue.ToString(); }
                    }
                    // 去除不必要的 .0 后缀（整数显示为整数）
                    double num = cell.NumericCellValue;
                    if (num == Math.Floor(num) && !double.IsNaN(num) && !double.IsInfinity(num))
                        return ((long)num).ToString();
                    return num.ToString();

                case CellType.Boolean:
                    return cell.BooleanCellValue ? "TRUE" : "FALSE";

                case CellType.Formula:
                    try
                    {
                        // 尝试获取公式计算后的值
                        return cell.StringCellValue?.Trim()
                            ?? cell.NumericCellValue.ToString()
                            ?? string.Empty;
                    }
                    catch
                    {
                        return cell.ToString() ?? string.Empty;
                    }

                case CellType.Blank:
                    return string.Empty;

                default:
                    return cell.ToString()?.Trim() ?? string.Empty;
            }
        }

        /// <summary>
        /// 根据文件扩展名获取 Markdown 代码块语言标识。
        /// </summary>
        private static string GetLanguageFromExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return string.Empty;

            return extension.ToLowerInvariant() switch
            {
                ".py" => "python",
                ".cs" => "csharp",
                ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" => "cpp",
                ".java" => "java",
                ".js" or ".jsx" => "javascript",
                ".ts" or ".tsx" => "typescript",
                ".html" or ".htm" => "html",
                ".css" => "css",
                ".xml" or ".xaml" or ".axml" => "xml",
                ".json" => "json",
                ".yaml" or ".yml" => "yaml",
                ".md" => "markdown",
                ".sql" => "sql",
                ".php" => "php",
                ".rb" => "ruby",
                ".go" => "go",
                ".rs" => "rust",
                ".swift" => "swift",
                ".kt" => "kotlin",
                ".scala" => "scala",
                ".lua" => "lua",
                ".sh" or ".bash" => "bash",
                ".ps1" => "powershell",
                ".bat" => "batch",
                ".ini" or ".cfg" or ".conf" or ".editorconfig" => "ini",
                ".r" => "r",
                ".dockerfile" => "dockerfile",
                ".cmake" => "cmake",
                ".proto" => "protobuf",
                _ => string.Empty,
            };
        }

        /// <summary>
        /// 解析 PDF 文档，使用 PdfPig 提取全部页面文本。
        /// </summary>
        private static string ParsePdfDocument(string filePath)
        {
            var sb = new StringBuilder();
            try
            {
                using var pdf = PdfDocument.Open(filePath);
                int totalPages = pdf.NumberOfPages;
                int pageCount = 0;

                foreach (var page in pdf.GetPages())
                {
                    pageCount++;
                    string pageText = page.Text?.Trim();

                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        if (totalPages > 1)
                            sb.AppendLine($"--- 第 {pageCount}/{totalPages} 页 ---");
                        sb.AppendLine(pageText);
                        sb.AppendLine();
                    }
                }

                Logger.Info($"PDF 解析完成: {pageCount} 页, {sb.Length} 个字符 ← {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Logger.Error($"解析 PDF 文件失败: {filePath}", ex);
                return $"[PDF 解析失败: {ex.Message}]";
            }

            return sb.ToString();
        }

        /// <summary>
        /// 解析图像文件，使用 OCR 提取文字。
        /// </summary>
        private static async Task<string?> ParseImageFileAsync(string filePath)
        {
            try
            {
                Logger.Info($"开始图像 OCR ({OcrService.CurrentEngine}): {Path.GetFileName(filePath)}");

                string? ocrText = await OcrService.ExtractTextFromImageAsync(filePath);

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    return ocrText;
                }

                // OCR 未成功，返回引擎相关的提示信息
                string engineHint = OcrService.CurrentEngine switch
                {
                    OcrEngineType.PaddleOCR => "3. PaddleOCR 引擎初始化失败（工具 → 选项 → DeepSeek Chat → OCR Settings）",
                    _ => "3. 系统未安装中文/英文 OCR 语言包（设置 → 语言 → 添加语言）",
                };

                return $"[图像文件: {Path.GetFileName(filePath)}]\n" +
                       $"[OCR 引擎: {OcrService.CurrentEngine}]\n" +
                       "[未能从此图像中提取文字。可能原因：\n" +
                       "1. 图像中不含清晰的文字内容\n" +
                       "2. 文字过小、模糊或与背景对比度不足\n" +
                       $"{engineHint}]";
            }
            catch (Exception ex)
            {
                Logger.Error($"图像 OCR 失败: {filePath}", ex);
                return $"[图像解析失败: {ex.Message}]";
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
