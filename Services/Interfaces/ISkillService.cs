using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Skill 发现、加载、管理服务接口。
    /// </summary>
    public interface ISkillService
    {
        /// <summary>技能列表变化事件</summary>
        event Action<int, string>? SkillsChanged;

        /// <summary>扫描所有位置，发现可用的 Skill</summary>
        Task<SkillDiscoveryResult> DiscoverSkillsAsync(string? solutionPath = null, bool forceRefresh = false);

        /// <summary>解析单个 Skill 文件</summary>
        SkillDefinition? ParseSkillFile(string filePath, SkillSource source);

        /// <summary>解析 Skill 内容字符串</summary>
        SkillDefinition? ParseSkillContent(string content, string filePath, SkillSource source);

        /// <summary>读取 Skill 关联的资源文件</summary>
        string? ReadSkillResource(SkillDefinition skill, string relativePath);

        /// <summary>按名称查找 Skill</summary>
        SkillDefinition? FindSkill(string name, SkillDiscoveryResult? discoveryResult = null);

        /// <summary>生成 Skill 发现上下文（供注入 system prompt）</summary>
        string GenerateSkillsDiscoveryContext(SkillDiscoveryResult? discoveryResult = null);

        /// <summary>生成用户可调用的 Skill 列表</summary>
        string GenerateUserInvocableSkillsList(SkillDiscoveryResult? discoveryResult = null);

        /// <summary>生成 Skill 总结文本</summary>
        string GenerateSkillsSummary(SkillDiscoveryResult? discoveryResult = null);

        /// <summary>获取当前缓存的 Skill 总结</summary>
        string? GetSkillsSummary();

        /// <summary>从磁盘加载持久化的 Skill 总结</summary>
        string? LoadSkillsSummaryFromDisk();
    }
}
