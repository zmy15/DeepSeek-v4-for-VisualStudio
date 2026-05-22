using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ========================================================================
    // Skill (SKILL.md) 模型定义
    // 参考: VS Code Copilot Agent Skills 规范
    // ========================================================================

    /// <summary>
    /// SKILL.md 文件解析后的完整技能定义。
    /// </summary>
    public class SkillDefinition
    {
        /// <summary>技能名称（1-64字符，小写字母+数字+连字符）</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 技能描述（最多1024字符）。
        /// 这是 AI 发现技能的关键字段，应包含触发关键词。
        /// 模式："Use when: ..." 或 "Use for: ..."
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>斜杠命令参数提示</summary>
        [JsonPropertyName("argument-hint")]
        public string? ArgumentHint { get; set; }

        /// <summary>是否在聊天中显示为斜杠命令（默认 true）</summary>
        [JsonPropertyName("user-invocable")]
        public bool UserInvocable { get; set; } = true;

        /// <summary>是否禁用 AI 自动加载（默认 false，即允许自动加载）</summary>
        [JsonPropertyName("disable-model-invocation")]
        public bool DisableModelInvocation { get; set; } = false;

        /// <summary>SKILL.md 正文（Markdown 格式的指令和流程）</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>SKILL.md 文件的完整路径</summary>
        [JsonIgnore]
        public string FilePath { get; set; } = string.Empty;

        /// <summary>技能根目录（包含 scripts/ references/ assets/ 等资源）</summary>
        [JsonIgnore]
        public string? RootDirectory { get; set; }

        /// <summary>技能来源类型</summary>
        [JsonIgnore]
        public SkillSource Source { get; set; } = SkillSource.Project;

        /// <summary>资源文件列表</summary>
        [JsonIgnore]
        public List<string> ResourceFiles { get; set; } = new();

        /// <summary>
        /// 获取用于 AI 发现的简短描述（name + description，约100 tokens）。
        /// </summary>
        public string GetDiscoveryText()
        {
            return $"## {Name}\n{Description}";
        }

        /// <summary>
        /// 获取完整技能指令（用于注入 AI 上下文）。
        /// </summary>
        public string GetFullInstructions()
        {
            var lines = new List<string>
            {
                $"<skill name=\"{Name}\">",
                $"<description>{Description}</description>",
                string.Empty,
                Body,
                string.Empty,
                "</skill>"
            };

            if (ResourceFiles.Count > 0)
            {
                lines.Add("<resources>");
                foreach (var res in ResourceFiles)
                    lines.Add($"- {res}");
                lines.Add("</resources>");
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 获取精简指令（仅 name + description + body 前 2000 字符）。
        /// 安全截断，不会在 Unicode 代理对（如 emoji）中间切断。
        /// </summary>
        public string GetCompactInstructions(int maxBodyChars = 2000)
        {
            string body;
            // RAG-MARK: no-truncate — 不再截断技能指令正文，完整加载
            // RAG-SOURCE: skill-definition 技能定义正文
            body = Body;

            return $"<skill name=\"{Name}\">\n<description>{Description}</description>\n\n{body}\n</skill>";
        }
    }

    /// <summary>
    /// 技能来源类型
    /// </summary>
    public enum SkillSource
    {
        /// <summary>项目级：.github/skills/、.agents/skills/、.claude/skills/</summary>
        Project,
        /// <summary>用户级：~/.copilot/skills/、~/.agents/skills/、~/.claude/skills/</summary>
        User,
        /// <summary>内置技能（随扩展发布）</summary>
        BuiltIn
    }

    /// <summary>
    /// 技能发现结果汇总
    /// </summary>
    public class SkillDiscoveryResult
    {
        /// <summary>发现的技能总数</summary>
        public int TotalCount => Skills.Count;

        /// <summary>所有发现的技能</summary>
        public List<SkillDefinition> Skills { get; set; } = new();

        /// <summary>项目级技能数量</summary>
        public int ProjectSkillCount => Skills.FindAll(s => s.Source == SkillSource.Project).Count;

        /// <summary>用户级技能数量</summary>
        public int UserSkillCount => Skills.FindAll(s => s.Source == SkillSource.User).Count;

        /// <summary>用户可调用的技能（斜杠命令）</summary>
        public List<SkillDefinition> UserInvocableSkills =>
            Skills.FindAll(s => s.UserInvocable);

        /// <summary>AI 可自动加载的技能</summary>
        public List<SkillDefinition> AutoLoadableSkills =>
            Skills.FindAll(s => !s.DisableModelInvocation);
    }

    /// <summary>
    /// AI 技能路由判断结果。
    /// </summary>
    public class SkillRoutingResult
    {
        /// <summary>匹配到的技能名称，null 表示无需调用技能</summary>
        [JsonPropertyName("skill")]
        public string? Skill { get; set; }

        /// <summary>匹配置信度：high / medium / low</summary>
        [JsonPropertyName("confidence")]
        public string? Confidence { get; set; }

        /// <summary>简短的匹配理由</summary>
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        /// <summary>是否匹配到技能</summary>
        [JsonIgnore]
        public bool HasSkill => !string.IsNullOrWhiteSpace(Skill)
            && !string.Equals(Skill, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Skill, "null", StringComparison.OrdinalIgnoreCase);
    }
}
