using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 流式工具调用增量累积器。
    /// 用于将 DeepSeek 流式返回的 tool_calls 增量片段合并为完整的工具调用。
    /// </summary>
    internal class ToolCallAccumulator
    {
        public string Id { get; set; } = string.Empty;
        public string? Type { get; set; }
        public string? FunctionName { get; set; }
        public StringBuilder ArgumentsBuilder { get; } = new StringBuilder();
    }
}
