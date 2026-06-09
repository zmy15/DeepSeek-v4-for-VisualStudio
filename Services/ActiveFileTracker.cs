using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 活跃文件追踪器接口 — 供 BuiltInToolService 和 ConversationContextManager 共享。
    /// </summary>
    public interface IActiveFileTracker
    {
        /// <summary>记录一次文件读取访问</summary>
        void ObserveRead(string filePath, string toolName, int currentTurn);

        /// <summary>记录一次文件写入/编辑访问</summary>
        void ObserveWrite(string filePath, string toolName, int currentTurn);

        /// <summary>获取 Working Set 摘要块（用于注入 System Prompt 动态块）</summary>
        string GetActiveFileSummary(string workspaceRoot);

        /// <summary>获取 Top-K 最近访问的文件路径</summary>
        List<string> GetTopPaths(string workspaceRoot, int limit = 24);

        /// <summary>清除所有追踪数据</summary>
        void Clear();
    }

    /// <summary>
    /// 活跃文件追踪器 — 会话级单例，追踪工具读取/写入过的文件。
    /// 生成 Working Set 摘要注入 System Prompt，帮助 AI 感知当前上下文。
    /// 
    /// 设计参考 CodeWhale working_set.rs。
    /// </summary>
    public class ActiveFileTracker : IActiveFileTracker
    {
        private readonly ActiveFileContext _context = new();

        /// <inheritdoc />
        public void ObserveRead(string filePath, string toolName, int currentTurn)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            _context.ObserveFileAccess(filePath, toolName, currentTurn, isWrite: false);
        }

        /// <inheritdoc />
        public void ObserveWrite(string filePath, string toolName, int currentTurn)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            _context.ObserveFileAccess(filePath, toolName, currentTurn, isWrite: true);
        }

        /// <inheritdoc />
        public string GetActiveFileSummary(string workspaceRoot)
        {
            return _context.GetSummaryBlock(workspaceRoot);
        }

        /// <inheritdoc />
        public List<string> GetTopPaths(string workspaceRoot, int limit = 24)
        {
            return _context.GetTopPaths(workspaceRoot, limit);
        }

        /// <inheritdoc />
        public void Clear()
        {
            _context.Clear();
        }
    }
}
