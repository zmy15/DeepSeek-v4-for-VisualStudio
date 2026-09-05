using System;

namespace DeepSeek_v4_for_VisualStudio.Services.Tools
{
    /// <summary>
    /// 工具超时分档策略（P2，序号 22）。
    ///
    ///  生效范围说明：交互式/审批类工具（run_in_terminal、编辑族、build_solution、
    /// read_file/list_dir/file_search/grep_search/git、runSubagent、askQuestions）
    /// 由 BaseAgent.IsInteractiveTool 直接跳过超时 —— 本策略仅对非豁免工具生效，
    /// 因此分档集中在：诊断/符号查询、网页抓取、记忆操作与其余默认工具。
    /// </summary>
    public static class ToolTimeoutPolicy
    {
        /// <summary>记忆文件操作</summary>
        public static readonly TimeSpan Memory = TimeSpan.FromSeconds(10);

        /// <summary>错误列表 / 符号查询（依赖 VS 索引）</summary>
        public static readonly TimeSpan Diagnostics = TimeSpan.FromSeconds(20);

        /// <summary>网络抓取</summary>
        public static readonly TimeSpan WebFetch = TimeSpan.FromSeconds(45);

        /// <summary>默认（含 MCP 外部工具）</summary>
        public static readonly TimeSpan Default = TimeSpan.FromSeconds(60);

        public static TimeSpan GetTimeout(string toolName)
        {
            return toolName switch
            {
                "memory" => Memory,
                "get_errors" => Diagnostics,
                "symbol_search" => Diagnostics,
                "fetch_webpage" => WebFetch,
                _ => Default,
            };
        }
    }
}
