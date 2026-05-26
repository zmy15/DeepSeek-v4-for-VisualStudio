using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑补丁服务 — 对外保持 IEditPatchService 接口，内部委托给新工具类。
    /// 所有实际实现已迁移到 EditTools/ 目录下的独立工具类。
    /// </summary>
    public class EditPatchService : IEditPatchService
    {
        private readonly DeepSeekApiService _apiService;

        public EditPatchService(DeepSeekApiService apiService)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        }

        #region IEditPatchService — 委托给新工具

        /// <inheritdoc/>
        public List<PatchOperation> ParsePatches(string aiOutput)
            => ApplyPatchTool.ParsePatches(aiOutput);

        /// <inheritdoc/>
        public List<InsertEditOperation> ParseInsertEdits(string aiOutput)
            => InsertEditTool.ParseInsertEdits(aiOutput);

        /// <inheritdoc/>
        public EditOperationType DetectOperationType(string aiOutput)
        {
            if (string.IsNullOrWhiteSpace(aiOutput))
                return EditOperationType.CreateFile;
            if (System.Text.RegularExpressions.Regex.IsMatch(aiOutput,
                @"\*\*\*\s*Begin\s*Patch", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return EditOperationType.ApplyPatch;
            if (System.Text.RegularExpressions.Regex.IsMatch(aiOutput,
                @"```(?:insert_edit_into_file|edit)\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return EditOperationType.InsertEditIntoFile;
            if (aiOutput.Contains("...existing code..."))
                return EditOperationType.InsertEditIntoFile;
            return EditOperationType.CreateFile;
        }

        /// <inheritdoc/>
        public int MatchWithFallback(string fileContent, string searchText, out MatchLevel matchLevel)
            => EditStringMatcher.MatchWithFallback(fileContent, searchText, out matchLevel);

        #endregion

        #region Static Utilities

        /// <summary>
        /// 检查文件在编辑后是否引入了新的编译/诊断错误。
        /// </summary>
        public static async Task<List<string>> CheckNewDiagnosticsAsync(string filePath)
        {
            var diagnostics = new List<string>();
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var errorList = (IVsTaskList?)Package.GetGlobalService(typeof(SVsTaskList));
                if (errorList == null) return diagnostics;
                errorList.EnumTaskItems(out IVsEnumTaskItems? enumTasks);
                if (enumTasks == null) return diagnostics;
                IVsTaskItem[] items = new IVsTaskItem[1];
                uint[] fetched = new uint[1];
                while (enumTasks.Next(1, items, fetched) == VSConstants.S_OK && fetched[0] == 1)
                {
                    try
                    {
                        var item = items[0];
                        if (item is not IVsTaskItem2 item2) continue;
                        var catArray = new VSTASKCATEGORY[1];
                        item2.Category(catArray);
                        if (catArray[0] != VSTASKCATEGORY.CAT_BUILDCOMPILE) continue;
                        var priorityArray = new VSTASKPRIORITY[1];
                        item2.get_Priority(priorityArray);
                        if (priorityArray[0] != VSTASKPRIORITY.TP_HIGH) continue;
                        item2.Document(out string fileName);
                        if (!string.IsNullOrEmpty(fileName) && string.Equals(fileName, filePath, StringComparison.OrdinalIgnoreCase))
                        {
                            item2.Line(out int line);
                            item2.Column(out int column);
                            item2.get_Text(out string text);
                            diagnostics.Add($"行 {line}: {text}");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Logger.Warn($"[EditPatchService] 诊断检查失败: {ex.Message}"); }
            return diagnostics;
        }

        /// <summary>
        /// 解析文件路径（支持相对路径和绝对路径），含路径穿越防护。
        /// </summary>
        public static string ResolvePath(string filePath, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;
            string resolved;
            if (Path.IsPathRooted(filePath)) { resolved = Path.GetFullPath(filePath); }
            else if (!string.IsNullOrEmpty(workspaceRoot)) { resolved = Path.GetFullPath(Path.Combine(workspaceRoot, filePath.Replace('/', '\\'))); }
            else { return filePath; }
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string nw = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string nr = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!nr.StartsWith(nw + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(nr, nw, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[EditPatch] 路径穿越检测: {resolved} 不在工作区 {workspaceRoot} 内");
                    return filePath;
                }
            }
            return resolved;
        }

        #endregion
    }
}
