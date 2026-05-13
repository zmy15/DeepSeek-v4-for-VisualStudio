using System;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// Agent 路由建议列表项（用于 @ 命令自动补全弹出框）。
    /// 与 SkillSuggestionItem 类似，但针对 Agent 路由场景定制。
    /// </summary>
    public class AgentSuggestionItem
    {
        /// <summary>Agent 名称（如 "ask", "edit", "plan", "explore"）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>显示图标（emoji）</summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>简短描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>参数提示（如 "输入问题"）</summary>
        public string ArgumentHint { get; set; } = string.Empty;

        /// <summary>对应的 AgentType 枚举值</summary>
        public AgentType AgentType { get; set; } = AgentType.Ask;

        /// <summary>用于 ListBox 显示的格式化前缀文本</summary>
        public string DisplayPrefix => $"@{Name}";

        /// <summary>详细提示文本</summary>
        public string TooltipText => string.IsNullOrEmpty(Description)
            ? $"@{Name}"
            : $"@{Name}\n{Description}\n{ArgumentHint}";
    }
}
