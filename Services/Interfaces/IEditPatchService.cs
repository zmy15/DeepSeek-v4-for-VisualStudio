using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑补丁服务接口 — 支持 apply_patch / insert_edit_into_file / create_file 三种编辑方式。
    /// </summary>
    public interface IEditPatchService
    {
        /// <summary>从 AI 输出中解析所有 Patch 操作</summary>
        List<PatchOperation> ParsePatches(string aiOutput);

        /// <summary>从 AI 输出中解析所有 insert_edit_into_file 操作</summary>
        List<InsertEditOperation> ParseInsertEdits(string aiOutput);

        /// <summary>从 AI 输出中检测编辑操作类型</summary>
        EditOperationType DetectOperationType(string aiOutput);

        /// <summary>4 级字符串匹配：在文件内容中定位目标区域</summary>
        int MatchWithFallback(string fileContent, string searchText, out MatchLevel matchLevel);
    }
}
