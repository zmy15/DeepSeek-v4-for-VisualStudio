using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    #region COM Interop 类型（本地定义，因 SDK NuGet 版本未包含这些接口）

    /// <summary>__VSOBSEARCHFLAGS2 枚举</summary>
    [Flags]
    internal enum __VSOBSEARCHFLAGS2 : uint
    {
        VSOBSF_NONE = 0x0000,
        VSOBSF_ENTIRESOLUTION = 0x0001,
        VSOBSF_PARTIALMATCH = 0x0002,
        VSOBSF_ENTIRESYSTEM = 0x0004,
        VSOBSF_CURRENTPROJECTONLY = 0x0008,
    }

    /// <summary>VsSymbolPath 结构体（COM 布局）</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct VsSymbolPath
    {
        [MarshalAs(UnmanagedType.BStr)]
        public string pszFullName;
        [MarshalAs(UnmanagedType.BStr)]
        public string pszDisplayName;
        [MarshalAs(UnmanagedType.BStr)]
        public string pszProjectName;
        [MarshalAs(UnmanagedType.BStr)]
        public string pszFileName;
        public uint dwLineNumber;
        public uint dwColumnNumber;
        public uint dwType;
        public uint dwAccess;
        public uint dwReserved;
    }

    /// <summary>IVsEnumSymbolPaths 接口</summary>
    [ComImport]
    [Guid("8AA2DB61-2653-4AB1-A281-43335B3B34CF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVsEnumSymbolPaths
    {
        [PreserveSig]
        int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] VsSymbolPath[] rgelt, out uint pceltFetched);

        [PreserveSig]
        int Skip(uint celt);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IVsEnumSymbolPaths ppEnum);
    }

    /// <summary>IVsFindSymbol 接口</summary>
    [ComImport]
    [Guid("B70E02C4-0F4A-4B31-99B6-DFEE252EB28F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVsFindSymbol
    {
        [PreserveSig]
        int FindSymbol([MarshalAs(UnmanagedType.BStr)] string szSymbol, out IVsEnumSymbolPaths ppSymbols);
    }

    /// <summary>IVsFindSymbol2 接口</summary>
    [ComImport]
    [Guid("6F47EAA8-0F43-4CD4-80AB-6CB84B33FF0B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVsFindSymbol2
    {
        [PreserveSig]
        int FindSymbol([MarshalAs(UnmanagedType.BStr)] string szSymbol, out IVsEnumSymbolPaths ppSymbols);

        [PreserveSig]
        int FindSymbol2([MarshalAs(UnmanagedType.BStr)] string szSymbol, __VSOBSEARCHFLAGS2 dwFlags, out IVsEnumSymbolPaths ppSymbols);
    }

    #endregion

    /// <summary>
    /// symbol_search 工具 — 使用 VS 对象浏览器按名称搜索符号（类、方法、属性、命名空间等）。
    /// 基于 IVsFindSymbol2 COM 接口，可在整个解决方案或当前项目中搜索。
    /// </summary>
    public class SymbolSearchTool : BuiltInToolBase
    {
        public override string Name => "symbol_search";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "symbol_search",
                    Description = L["tool.symbolSearch.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.symbolSearch.param.query"]
                            },
                            searchEntireSolution = new
                            {
                                type = "boolean",
                                description = LocalizationService.Instance["tool.symbolSearch.param.searchEntireSolution"]
                            },
                            maxResults = new
                            {
                                type = "integer",
                                description = LocalizationService.Instance["tool.symbolSearch.param.maxResults"]
                            }
                        },
                        required = new[] { "query" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string ssQuery = GetStringArg(args, "query");
            return string.IsNullOrEmpty(ssQuery)
                ? LocalizationService.Instance["tool.symbolSearch.searching"]
                : LocalizationService.Instance.Format("tool.symbolSearch.searchingQuery", TruncateText(ssQuery, 60));
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;

            var ssFirstLine = toolResult.Split('\n')[0];
            var ssMatch = System.Text.RegularExpressions.Regex.Match(
                ssFirstLine, @"(?:找到|Found|found)\s*(\d+)\s*个?\s*(?:符号|symbols?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (ssMatch.Success)
                return $"✅ {ssMatch.Value.Trim()}";

            var ssLines = toolResult.Split('\n');
            int ssCount = ssLines.Length;
            return LocalizationService.Instance.Format("tool.symbolSearch.foundSymbols", ssCount);
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string query = GetStringArg(args, "query");
            if (string.IsNullOrEmpty(query))
                return LocalizationService.Instance["tool.symbolSearch.missingQuery"];

            bool searchEntireSolution = GetBoolArg(args, "searchEntireSolution", true);
            int maxResults = GetIntArg(args, "maxResults", 30);

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken);

                // ── 方式 1: Package.GetGlobalService（UI 线程同步调用，最可靠）──
                object? objSearch = null;
                try { objSearch = Package.GetGlobalService(typeof(SVsObjectSearch)); } catch { }

                if (objSearch != null)
                {
                    // ── 尝试 IVsFindSymbol2 ──
                    var findSym2 = objSearch as IVsFindSymbol2;
                    if (findSym2 != null)
                    {
                        var flags = __VSOBSEARCHFLAGS2.VSOBSF_PARTIALMATCH;
                        if (searchEntireSolution)
                            flags |= __VSOBSEARCHFLAGS2.VSOBSF_ENTIRESOLUTION;
                        int hr = findSym2.FindSymbol2(query, flags, out var enumSyms);
                        if (hr == VSConstants.S_OK && enumSyms != null)
                            return FormatEnumResults(query, maxResults, enumSyms);
                    }

                    // ── 回退: IVsFindSymbol ──
                    var findSym = objSearch as IVsFindSymbol;
                    if (findSym != null)
                    {
                        int hr = findSym.FindSymbol(query, out var enumSyms);
                        if (hr == VSConstants.S_OK && enumSyms != null)
                            return FormatEnumResults(query, maxResults, enumSyms);
                    }
                }

                // ── 方式 2: 文件内容回退 — 在源码中搜索符号定义 ──
                Logger.Info("[symbol_search] VS Object Search not available, falling back to file-content symbol search");
                return FallbackSymbolSearch(query, maxResults, workspaceRoot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ── 异常时也回退到文件搜索 ──
                try { return FallbackSymbolSearch(query, maxResults, workspaceRoot); }
                catch { return LocalizationService.Instance.Format("tool.symbolSearch.failed", ex.Message); }
            }
        }

        /// <summary>
        /// 格式化 IVsEnumSymbolPaths 的枚举结果为 Markdown 表格。
        /// </summary>
        private static string FormatEnumResults(string query, int maxResults, IVsEnumSymbolPaths enumSymbols)
        {
            var results = new List<(string FullName, string DisplayName, string ProjectName, string FileName, int LineNumber)>();

            uint fetched;
            var pathArray = new VsSymbolPath[1];

            while (results.Count < maxResults)
            {
                int hr = enumSymbols.Next(1, pathArray, out fetched);
                if (hr != VSConstants.S_OK || fetched == 0)
                    break;

                var path = pathArray[0];
                results.Add((
                    path.pszFullName ?? "",
                    path.pszDisplayName ?? "",
                    path.pszProjectName ?? "",
                    path.pszFileName ?? "",
                    (int)path.dwLineNumber
                ));
            }

            return FormatOutput(query, results);
        }

        /// <summary>
        /// 文件内容回退：在源码文件中用正则匹配符号定义。
        /// 当 VS Object Search COM 服务不可用时使用此方案。
        /// </summary>
        private static string FallbackSymbolSearch(string query, int maxResults, string? workspaceRoot)
        {
            string searchRoot = NormalizeWorkspaceRoot(workspaceRoot) ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(searchRoot))
                return LocalizationService.Instance.Format("tool.fileSearch.workspaceNotExist", searchRoot);

            var results = new List<(string FullName, string DisplayName, string ProjectName, string FileName, int LineNumber)>();

            // ── 只搜索源代码文件 ──
            var sourceExts = new[] { ".cs", ".vb", ".cpp", ".h", ".hpp", ".c", ".fs", ".fsx",
                ".py", ".js", ".ts", ".jsx", ".tsx", ".java", ".go", ".rs", ".swift", ".kt" };

            // ── 符号定义正则（C# 优先，通用兜底）──
            // 匹配: class/interface/struct/enum/record Name, method/property/field definitions
            var symbolPatterns = new[]
            {
                // C# 类型定义
                $@"(class|interface|struct|enum|record|delegate)\s+{Regex.Escape(query)}\b",
                // C# 方法/属性/字段（带类型前缀）
                $@"\b{Regex.Escape(query)}\s*[\(\<]",
                // 通用：行首/空格后紧跟符号名
                $@"\b{Regex.Escape(query)}\b",
            };

            try
            {
                // ── 枚举源代码文件（限制数量）──
                var files = new List<string>();
                foreach (var ext in sourceExts)
                {
                    if (files.Count >= 1000) break;
                    try
                    {
                        files.AddRange(Directory.EnumerateFiles(searchRoot, "*" + ext, SearchOption.AllDirectories).Take(500));
                    }
                    catch { }
                }

                foreach (var file in files)
                {
                    if (results.Count >= maxResults) break;

                    // 跳过排除目录
                    string dir = Path.GetDirectoryName(file) ?? "";
                    if (dir.Contains("\\bin\\") || dir.Contains("\\obj\\") || dir.Contains("\\.git\\")
                        || dir.Contains("\\node_modules\\") || dir.Contains("\\.vs\\")
                        || dir.Contains("\\packages\\") || dir.Contains("\\Debug\\") || dir.Contains("\\Release\\"))
                        continue;

                    try
                    {
                        var lines = File.ReadAllLines(file);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (results.Count >= maxResults) break;
                            var line = lines[i];

                            foreach (var pattern in symbolPatterns)
                            {
                                if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                                {
                                    string relativePath = file;
                                    if (file.StartsWith(searchRoot, StringComparison.OrdinalIgnoreCase))
                                        relativePath = file.Substring(searchRoot.Length).TrimStart('\\', '/');

                                    results.Add((
                                        relativePath,
                                        line.Trim().Truncate(80),
                                        "",
                                        relativePath,
                                        i + 1
                                    ));
                                    break; // 每行只匹配一次
                                }
                            }
                        }
                    }
                    catch { /* 跳过无法读取的文件 */ }
                }
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.symbolSearch.failed", ex.Message);
            }

            return FormatOutput(query, results);
        }

        /// <summary>
        /// 将结果列表格式化为统一的 Markdown 表格输出。
        /// </summary>
        private static string FormatOutput(string query, List<(string FullName, string DisplayName, string ProjectName, string FileName, int LineNumber)> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine(LocalizationService.Instance.Format(
                "tool.symbolSearch.resultHeader", query, results.Count));
            sb.AppendLine();

            if (results.Count == 0)
            {
                sb.AppendLine(LocalizationService.Instance["tool.symbolSearch.noMatches"]);
            }
            else
            {
                sb.AppendLine("| # | 符号全名 | 显示名 | 项目 | 文件 | 行 |");
                sb.AppendLine("|---|----------|--------|------|------|----|");
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    sb.AppendLine($"| {i + 1} | `{EscapePipe(r.FullName)}` | `{EscapePipe(r.DisplayName)}` | `{EscapePipe(r.ProjectName)}` | `{EscapePipe(r.FileName)}` | {r.LineNumber} |");
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 转义 Markdown 表格中的管道符。
        /// </summary>
        private static string EscapePipe(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("|", "\\|");
        }
    }
}
