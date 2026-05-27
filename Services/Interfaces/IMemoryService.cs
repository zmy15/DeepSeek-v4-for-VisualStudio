using DeepSeek_v4_for_VisualStudio.Models;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 记忆服务接口 — 管理 AI 持久化记忆的 CRUD 操作。
    /// 
    /// 三个作用域：
    /// - User:   跨所有工作区/会话的持久记忆
    /// - Session: 当前对话范围的临时记忆
    /// - Repo:    当前解决方案范围的记忆
    /// 
    /// 存储位置：%LocalAppData%\DeepSeekVS\memories\{scope}\
    /// </summary>
    public interface IMemoryService
    {
        /// <summary>
        /// 查看记忆文件内容或列出目录。
        /// 如果 path 指向文件 → 返回文件内容（可选行范围）。
        /// 如果 path 指向目录或为空 → 返回目录条目列表。
        /// </summary>
        Task<MemoryViewResult> ViewAsync(
            MemoryScope scope,
            string path,
            string? sessionId = null,
            string? solutionPath = null,
            int? startLine = null,
            int? endLine = null);

        /// <summary>
        /// 创建新的记忆文件。如果文件已存在则失败。
        /// </summary>
        Task<string> CreateAsync(
            MemoryScope scope,
            string path,
            string content,
            string? sessionId = null,
            string? solutionPath = null);

        /// <summary>
        /// 精确替换文件中的字符串。oldStr 必须在文件中恰好出现一次。
        /// </summary>
        Task<string> StrReplaceAsync(
            MemoryScope scope,
            string path,
            string oldStr,
            string newStr,
            string? sessionId = null,
            string? solutionPath = null);

        /// <summary>
        /// 在指定行号插入文本。lineNumber 为 0 时插入到文件开头。
        /// </summary>
        Task<string> InsertAsync(
            MemoryScope scope,
            string path,
            int lineNumber,
            string text,
            string? sessionId = null,
            string? solutionPath = null);

        /// <summary>
        /// 删除记忆文件或递归删除目录。
        /// </summary>
        Task<string> DeleteAsync(
            MemoryScope scope,
            string path,
            string? sessionId = null,
            string? solutionPath = null);

        /// <summary>
        /// 重命名/移动记忆文件或目录。
        /// </summary>
        Task<string> RenameAsync(
            MemoryScope scope,
            string oldPath,
            string newPath,
            string? sessionId = null,
            string? solutionPath = null);

        /// <summary>
        /// 获取指定作用域的所有记忆文件内容摘要（用于注入 system prompt）。
        /// 返回格式：每个文件一行 "[文件名]: 前100字符预览"
        /// </summary>
        string GetMemoryPreviews(
            MemoryScope scope,
            string? sessionId = null,
            string? solutionPath = null);

        /// <summary>
        /// 获取指定作用域的所有记忆文件完整内容合并文本（用于注入 system prompt）。
        /// </summary>
        string GetMemoryContext(
            MemoryScope scope,
            string? sessionId = null,
            string? solutionPath = null);
    }
}
