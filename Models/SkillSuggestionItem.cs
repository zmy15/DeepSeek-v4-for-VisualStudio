using System;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// Skill 建议列表项（用于 / 斜杠命令自动补全弹出框）。
    /// </summary>
    public class SkillSuggestionItem
    {
        /// <summary>技能/命令名称（如 "code-review"）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>简短描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>来源标签（如 " 项目"）</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>是否为元命令（help, refresh-skills 等内置命令）</summary>
        public bool IsMeta { get; set; } = false;

        /// <summary>对应的完整 Skill 定义（仅技能有，元命令为 null）</summary>
        public SkillDefinition? SkillDefinition { get; set; }

        /// <summary>用于 ListBox 显示的格式化文本</summary>
        public string DisplayText => $"/{Name}  {Source}";

        /// <summary>详细提示文本</summary>
        public string TooltipText => string.IsNullOrEmpty(Description)
            ? $"/{Name}"
            : $"/{Name}\n{Description}\n来源: {Source}";
    }
}
