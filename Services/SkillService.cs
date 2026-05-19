using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Skill 发现、加载、管理服务。
    /// 
    /// 支持的 Skill 目录位置：
    ///   - 项目级: .github/skills/、.agents/skills/、.claude/skills/
    ///   - 用户级: %USERPROFILE%\.copilot\skills\、%USERPROFILE%\.agents\skills\、%USERPROFILE%\.claude\skills\
    ///   - 内置级: 扩展安装目录下的 BuiltInSkills/
    /// 
    /// SKILL.md 格式：
    ///   ---
    ///   name: skill-name
    ///   description: 'What and when to use.'
    ///   argument-hint: 'optional hint'
    ///   user-invocable: true
    ///   disable-model-invocation: false
    ///   ---
    ///   # Skill Title
    ///   ... markdown body ...
    /// </summary>
    public class SkillService : ISkillService
    {
        #region Singleton

        private static readonly Lazy<SkillService> _instance = new(() => new SkillService());
        public static SkillService Instance => _instance.Value;

        private SkillService() { }

        #endregion

        #region Constants

        /// <summary>Skill 定义文件名</summary>
        private const string SkillFileName = "SKILL.md";

        /// <summary>技能总结持久化文件路径</summary>
        private static readonly string SkillsSummaryFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "skills_summary.json");

        /// <summary>持久化 JSON 序列化选项</summary>
        private static readonly System.Text.Json.JsonSerializerOptions SummaryJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

        /// <summary>YAML 前置元数据正则</summary>
        private static readonly Regex YamlFrontMatterRegex = new(
            @"^---\s*\n(.*?)\n---\s*\n(.*)",
            RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>YAML 简单键值对解析（name: value）</summary>
        private static readonly Regex YamlKeyValueRegex = new(
            @"^(\w[\w-]*)\s*:\s*(.+?)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        #endregion

        #region Fields

        private SkillDiscoveryResult? _cachedResult;
        private string? _lastSolutionPath;
        private int _lastSkillCount = -1;
        private string? _cachedSkillSummary;
        private readonly object _lock = new();

        #endregion

        #region Events

        /// <summary>
        /// 技能列表发生变化时触发（新增/移除技能）。
        /// 参数: 新技能总数, 技能总结文本。
        /// </summary>
        public event Action<int, string>? SkillsChanged;

        #endregion

        #region Discovery

        /// <summary>
        /// 扫描所有位置，发现可用的 Skill。
        /// </summary>
        /// <param name="solutionPath">当前解决方案路径（用于项目级技能发现）</param>
        /// <param name="forceRefresh">是否强制刷新缓存</param>
        public async Task<SkillDiscoveryResult> DiscoverSkillsAsync(
            string? solutionPath = null, bool forceRefresh = false)
        {
            lock (_lock)
            {
                if (!forceRefresh && _cachedResult != null && _lastSolutionPath == solutionPath)
                    return _cachedResult;
            }

            var result = new SkillDiscoveryResult();

            try
            {
                // 1. 项目级技能发现
                if (!string.IsNullOrEmpty(solutionPath))
                {
                    var solutionDir = FindSolutionRoot(solutionPath);
                    if (solutionDir != null)
                    {
                        var projectSkills = await DiscoverInDirectoryAsync(
                            solutionDir, SkillSource.Project);
                        result.Skills.AddRange(projectSkills);
                    }
                }

                // 2. 用户级技能发现
                var userSkills = await DiscoverUserSkillsAsync();
                result.Skills.AddRange(userSkills);

                // 3. 内置技能发现
                var builtInSkills = await DiscoverBuiltInSkillsAsync();
                result.Skills.AddRange(builtInSkills);

                // 按名称去重（项目级 > 用户级 > 内置级）
                result.Skills = DeduplicateSkills(result.Skills);
            }
            catch (Exception ex)
            {
                Logger.Error($"[SkillService] 技能发现失败: {ex.Message}");
            }

            // ── 技能变更检测：数量变化时生成总结、持久化、触发事件 ──
            int newCount = result.TotalCount;
            int oldCount;
            bool skillsChanged = false;
            lock (_lock)
            {
                oldCount = _lastSkillCount;
                if (_lastSkillCount >= 0 && newCount != _lastSkillCount)
                {
                    skillsChanged = true;
                    Logger.Info($"[SkillService] 🔍 检测到技能数量变化: {_lastSkillCount} → {newCount}");
                    Logger.Info($"[SkillService]   项目级: {result.ProjectSkillCount}, 用户级: {result.UserSkillCount}, 内置: {result.TotalCount - result.ProjectSkillCount - result.UserSkillCount}");
                }
                else if (_lastSkillCount < 0)
                {
                    Logger.Info($"[SkillService] 🔍 首次技能发现: 共 {newCount} 个技能 (项目: {result.ProjectSkillCount}, 用户: {result.UserSkillCount})");
                }
                _lastSkillCount = newCount;
                _cachedResult = result;
                _lastSolutionPath = solutionPath;
            }

            // ── 技能总结：仅在数量变化或本地文件不存在时重新生成 ──
            bool fileExists = File.Exists(SkillsSummaryFilePath);
            bool needsRegeneration = skillsChanged || !fileExists;

            if (needsRegeneration)
            {
                Logger.Info($"[SkillService] 📝 需要生成技能总结 (数量变化={skillsChanged}, 文件存在={fileExists})");

                // ── 生成技能总结 ──
                _cachedSkillSummary = GenerateSkillsSummary(result);
                Logger.Info($"[SkillService] 📝 已生成技能总结 ({_cachedSkillSummary.Length} 字符)");

                // ── 持久化到本地文件 ──
                SaveSkillsSummaryToDisk(newCount, _cachedSkillSummary);

                if (skillsChanged)
                {
                    Logger.Info($"[SkillService] 📢 触发 SkillsChanged 事件 (count={newCount})");
                    SkillsChanged?.Invoke(newCount, _cachedSkillSummary);
                }
            }
            else
            {
                Logger.Info($"[SkillService] ⏭️ 技能数量未变化 ({newCount}) 且持久化文件已存在，跳过总结生成");
            }

            return result;
        }

        /// <summary>
        /// 在指定目录下搜索所有 Skill 定义。
        /// </summary>
        private async Task<List<SkillDefinition>> DiscoverInDirectoryAsync(
            string rootDirectory, SkillSource source)
        {
            var skills = new List<SkillDefinition>();

            // 支持的技能子目录名
            var skillDirNames = new[] { ".github", ".agents", ".claude" };

            foreach (var dirName in skillDirNames)
            {
                var skillsRoot = Path.Combine(rootDirectory, dirName, "skills");
                if (Directory.Exists(skillsRoot))
                {
                    var discovered = await DiscoverInSkillsRootAsync(skillsRoot, source);
                    skills.AddRange(discovered);
                }
            }

            return skills;
        }

        /// <summary>
        /// 在 skills/ 根目录下扫描所有子目录查找 SKILL.md。
        /// </summary>
        private Task<List<SkillDefinition>> DiscoverInSkillsRootAsync(
            string skillsRoot, SkillSource source)
        {
            var skills = new List<SkillDefinition>();

            try
            {
                foreach (var skillDir in Directory.GetDirectories(skillsRoot))
                {
                    var skillFile = Path.Combine(skillDir, SkillFileName);
                    if (File.Exists(skillFile))
                    {
                        var skill = ParseSkillFile(skillFile, source);
                        if (skill != null)
                        {
                            skill.RootDirectory = skillDir;
                            skill.ResourceFiles = DiscoverResourceFiles(skillDir);
                            skills.Add(skill);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SkillService] 扫描技能目录失败 '{skillsRoot}': {ex.Message}");
            }

            return Task.FromResult(skills);
        }

        /// <summary>
        /// 发现用户级技能。
        /// </summary>
        private async Task<List<SkillDefinition>> DiscoverUserSkillsAsync()
        {
            var skills = new List<SkillDefinition>();
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var userSkillRoots = new[]
            {
                Path.Combine(userProfile, ".copilot", "skills"),
                Path.Combine(userProfile, ".agents", "skills"),
                Path.Combine(userProfile, ".claude", "skills"),
            };

            foreach (var root in userSkillRoots)
            {
                if (Directory.Exists(root))
                {
                    var discovered = await DiscoverInSkillsRootAsync(root, SkillSource.User);
                    skills.AddRange(discovered);
                }
            }

            return skills;
        }

        /// <summary>
        /// <summary>
        /// 发现内置技能（随扩展发布 + 硬编码内置技能）。
        /// </summary>
        private async Task<List<SkillDefinition>> DiscoverBuiltInSkillsAsync()
        {
            var skills = new List<SkillDefinition>();

            try
            {
                // 内置技能位于扩展安装目录的 BuiltInSkills 子目录
                var extensionPath = Path.GetDirectoryName(
                    typeof(SkillService).Assembly.Location);
                if (extensionPath != null)
                {
                    var builtInRoot = Path.Combine(extensionPath, "BuiltInSkills");
                    if (Directory.Exists(builtInRoot))
                    {
                        var discovered = await DiscoverInSkillsRootAsync(builtInRoot, SkillSource.BuiltIn);
                        skills.AddRange(discovered);
                    }
                }

                // ── 硬编码内置技能：代码审查 (code-review) ──
                var codeReviewSkill = ParseSkillContent(
                    AiPrompts.BuiltInSkill_CodeReview,
                    "<builtin>/code-review/SKILL.md",
                    SkillSource.BuiltIn);
                if (codeReviewSkill != null)
                {
                    skills.Add(codeReviewSkill);
                    Logger.Info("[SkillService] 已加载内置技能: code-review");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SkillService] 内置技能发现失败: {ex.Message}");
            }

            return skills;
        }

        #endregion

        #region Parsing

        /// <summary>
        /// 解析 SKILL.md 文件。
        /// </summary>
        public SkillDefinition? ParseSkillFile(string filePath, SkillSource source)
        {
            try
            {
                var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                return ParseSkillContent(content, filePath, source);
            }
            catch (Exception ex)
            {
                Logger.Error($"[SkillService] 解析技能文件失败 '{filePath}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析 Skill 文本内容。
        /// </summary>
        public SkillDefinition? ParseSkillContent(string content, string filePath, SkillSource source)
        {
            try
            {
                var skill = new SkillDefinition
                {
                    FilePath = filePath,
                    Source = source
                };

                var match = YamlFrontMatterRegex.Match(content);
                if (match.Success)
                {
                    var yamlBlock = match.Groups[1].Value;
                    var body = match.Groups[2].Value.Trim();

                    ParseYamlFrontMatter(yamlBlock, skill);
                    skill.Body = body;
                }
                else
                {
                    // 无 YAML 前置元数据，整个文件作为 body
                    skill.Body = content.Trim();
                    // 尝试从文件名推断名称
                    var dirName = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dirName))
                        skill.Name = Path.GetFileName(dirName).ToLowerInvariant();
                }

                // 验证必填字段
                if (string.IsNullOrWhiteSpace(skill.Name))
                {
                    Logger.Warn($"[SkillService] 技能文件缺少名称: {filePath}");
                    return null;
                }

                return skill;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SkillService] 解析技能内容失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析 YAML 前置元数据。
        /// </summary>
        private void ParseYamlFrontMatter(string yaml, SkillDefinition skill)
        {
            var matches = YamlKeyValueRegex.Matches(yaml);
            foreach (Match match in matches)
            {
                var key = match.Groups[1].Value.Trim();
                var value = match.Groups[2].Value.Trim();

                // 去除引号
                value = value.Trim('\'', '"');

                switch (key)
                {
                    case "name":
                        skill.Name = value;
                        break;
                    case "description":
                        skill.Description = value;
                        break;
                    case "argument-hint":
                        skill.ArgumentHint = value;
                        break;
                    case "user-invocable":
                        skill.UserInvocable = ParseBool(value, true);
                        break;
                    case "disable-model-invocation":
                        skill.DisableModelInvocation = ParseBool(value, false);
                        break;
                }
            }
        }

        private static bool ParseBool(string value, bool defaultValue)
        {
            if (bool.TryParse(value, out var result))
                return result;
            return defaultValue;
        }

        #endregion

        #region Resources

        /// <summary>
        /// 发现技能目录下的资源文件。
        /// </summary>
        private List<string> DiscoverResourceFiles(string skillDirectory)
        {
            var resources = new List<string>();

            try
            {
                // 只扫描一级子目录和直接文件（scripts/, references/, assets/）
                foreach (var dir in Directory.GetDirectories(skillDirectory))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName == "scripts" || dirName == "references" || dirName == "assets")
                    {
                        foreach (var file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                        {
                            resources.Add(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SkillService] 扫描资源文件失败 '{skillDirectory}': {ex.Message}");
            }

            return resources;
        }

        /// <summary>
        /// 根据相对路径读取技能资源文件内容。
        /// </summary>
        public string? ReadSkillResource(SkillDefinition skill, string relativePath)
        {
            try
            {
                if (skill.RootDirectory == null) return null;

                // 安全检查：防止路径遍历攻击
                var fullPath = Path.GetFullPath(
                    Path.Combine(skill.RootDirectory, relativePath));
                if (!fullPath.StartsWith(skill.RootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[SkillService] 拒绝路径遍历: {relativePath}");
                    return null;
                }

                if (File.Exists(fullPath))
                    return File.ReadAllText(fullPath, System.Text.Encoding.UTF8);

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SkillService] 读取资源失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 查找项目根目录。
        /// 支持 .sln 项目、CMake（Open Folder）等无 .sln 的项目类型。
        /// 
        /// 判定逻辑：
        ///   1. 如果 path 是文件（如 .sln），取其父目录作为起点
        ///   2. 优先向上查找包含 .sln 的目录（传统解决方案）
        ///   3. 如果找不到 .sln，认为是文件夹项目，返回传入的目录路径
        ///      （不再继续向上遍历，避免匹配到无关的父级 .sln）
        /// 
        /// 判断是否是有效项目根目录的启发式标记（满足任一即视为根目录）：
        ///   - 包含 .git 子目录
        ///   - 包含 CMakeLists.txt
        ///   - 包含 package.json / Cargo.toml / Makefile / build.gradle 等
        ///   - 传入的就是目录路径（不是文件）
        /// </summary>
        private string? FindSolutionRoot(string path)
        {
            try
            {
                // 如果 path 是文件（如 .sln），取其目录
                if (File.Exists(path))
                    path = Path.GetDirectoryName(path) ?? path;

                // —— 向上查找包含 .sln 文件的目录（传统解决方案）——
                var current = path;
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.GetFiles(current, "*.sln").Length > 0)
                        return current;

                    // 检测是否是有效的非 .sln 项目根目录，避免继续向上
                    if (IsProjectRootDirectory(current))
                        return current;

                    var parent = Directory.GetParent(current);
                    if (parent == null) break;
                    current = parent.FullName;
                }

                // 找不到 .sln 且没有项目根标记 → 文件夹项目，返回传入路径
                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 判断目录是否是一个有效的项目根目录（无 .sln 场景）。
        /// 用于阻止继续向上遍历到无关目录。
        /// </summary>
        private static bool IsProjectRootDirectory(string dir)
        {
            try
            {
                // .git 目录是通用的项目根目录标识
                if (Directory.Exists(Path.Combine(dir, ".git")))
                    return true;

                // 常见构建系统/包管理器的根标识文件
                string[] rootMarkers = {
                    "CMakeLists.txt", "Makefile", "GNUmakefile",
                    "package.json", "Cargo.toml", "go.mod",
                    "build.gradle", "build.gradle.kts", "pom.xml",
                    "meson.build", "BUILD.bazel", "WORKSPACE",
                    ".editorconfig", ".gitignore"
                };

                foreach (var marker in rootMarkers)
                {
                    if (File.Exists(Path.Combine(dir, marker)))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按优先级去重：项目级 > 用户级 > 内置级。
        /// </summary>
        private List<SkillDefinition> DeduplicateSkills(List<SkillDefinition> skills)
        {
            // Source 优先级：Project(0) > User(1) > BuiltIn(2)
            var priorityMap = new Dictionary<SkillSource, int>
            {
                [SkillSource.Project] = 0,
                [SkillSource.User] = 1,
                [SkillSource.BuiltIn] = 2,
            };

            return skills
                .GroupBy(s => s.Name.ToLowerInvariant())
                .Select(g => g.OrderBy(s => priorityMap.TryGetValue(s.Source, out var priority) ? priority : 99).First())
                .ToList();
        }

        /// <summary>
        /// 根据名称查找技能。
        /// </summary>
        public SkillDefinition? FindSkill(string name, SkillDiscoveryResult? discoveryResult = null)
        {
            var result = discoveryResult ?? _cachedResult;
            if (result == null) return null;

            return result.Skills.Find(
                s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region AI Context Generation

        /// <summary>
        /// 生成所有可自动加载技能的发现文本（注入 System Prompt）。
        /// 约 100 tokens/skill，适合始终包含在上下文中。
        /// </summary>
        public string GenerateSkillsDiscoveryContext(SkillDiscoveryResult? discoveryResult = null)
        {
            var result = discoveryResult ?? _cachedResult;
            if (result == null || result.AutoLoadableSkills.Count == 0)
                return string.Empty;

            var lines = new List<string>
            {
                "<available_skills>",
                "以下技能可在需要时加载完整指令。技能描述中的关键词触发自动加载。",
                string.Empty,
            };

            foreach (var skill in result.AutoLoadableSkills)
            {
                lines.Add(skill.GetDiscoveryText());
                lines.Add(string.Empty);
            }

            lines.Add("</available_skills>");

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 生成用户可调用技能的列表（用于斜杠命令显示）。
        /// </summary>
        public string GenerateUserInvocableSkillsList(SkillDiscoveryResult? discoveryResult = null)
        {
            var result = discoveryResult ?? _cachedResult;
            if (result == null || result.UserInvocableSkills.Count == 0)
                return string.Empty;

            var L = LocalizationService.Instance;
            var lines = new List<string> { string.Format(L["skills.availableHeader"], "/") };

            foreach (var skill in result.UserInvocableSkills)
            {
                var hint = skill.ArgumentHint != null ? $" [{skill.ArgumentHint}]" : "";
                lines.Add($"- `/{skill.Name}{hint}` — {skill.Description}");
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 生成所有技能的紧凑总结（用于 AI 路由决策）。
        /// 每行一个技能：名称 | 描述 | 触发关键词
        /// </summary>
        public string GenerateSkillsSummary(SkillDiscoveryResult? discoveryResult = null)
        {
            var result = discoveryResult ?? _cachedResult;
            if (result == null || result.TotalCount == 0)
            {
                Logger.Info("[SkillService] 📝 GenerateSkillsSummary: 无可用技能");
                return LocalizationService.Instance["skills.noneAvailable"];
            }

            Logger.Info($"[SkillService] 📝 开始生成技能总结: 共 {result.TotalCount} 个技能 (项目: {result.ProjectSkillCount}, 用户: {result.UserSkillCount}, 内置: {result.TotalCount - result.ProjectSkillCount - result.UserSkillCount})");

            var lines = new List<string>
            {
                $"共 {result.TotalCount} 个技能可用：",
                string.Empty,
            };

            for (int i = 0; i < result.Skills.Count; i++)
            {
                var skill = result.Skills[i];
                var sourceLabel = skill.Source switch
                {
                    SkillSource.BuiltIn => "[内置]",
                    SkillSource.User => "[用户]",
                    SkillSource.Project => "[项目]",
                    _ => ""
                };
                var invocableLabel = skill.UserInvocable ? "" : " (仅AI自动加载)";
                var descPreview = skill.Description.Length > 80
                    ? skill.Description.Substring(0, 80) + "..."
                    : skill.Description;
                lines.Add($"{i + 1}. **{skill.Name}** {sourceLabel}{invocableLabel}");
                lines.Add($"   {skill.Description}");
                lines.Add(string.Empty);

                Logger.Info($"[SkillService] 📝   [{i + 1}/{result.TotalCount}] {skill.Name} {sourceLabel} — {descPreview}");
            }

            string summary = string.Join("\n", lines);
            Logger.Info($"[SkillService] 📝 技能总结生成完成: {summary.Length} 字符");
            return summary;
        }

        /// <summary>
        /// 获取缓存的技能总结（用于路由决策）。
        /// 优先从内存缓存读取，若为空则尝试从本地磁盘加载。
        /// </summary>
        public string? GetSkillsSummary()
        {
            lock (_lock)
            {
                if (_cachedSkillSummary != null)
                    return _cachedSkillSummary;
            }

            // ── 冷启动：尝试从磁盘恢复 ──
            string? diskSummary = LoadSkillsSummaryFromDisk();
            if (diskSummary != null)
                return diskSummary;

            return null;
        }

        /// <summary>
        /// 从本地磁盘加载缓存的技能总结（用于 VS 重启后恢复）。
        /// </summary>
        /// <returns>成功加载返回总结文本，否则返回 null</returns>
        public string? LoadSkillsSummaryFromDisk()
        {
            try
            {
                if (!File.Exists(SkillsSummaryFilePath))
                {
                    Logger.Info($"[SkillService] 💾 技能总结文件不存在，跳过加载: {SkillsSummaryFilePath}");
                    return null;
                }

                string json = File.ReadAllText(SkillsSummaryFilePath, System.Text.Encoding.UTF8);
                var record = System.Text.Json.JsonSerializer.Deserialize<SkillsSummaryRecord>(json, SummaryJsonOptions);

                if (record == null || string.IsNullOrWhiteSpace(record.Summary))
                {
                    Logger.Warn("[SkillService] 💾 技能总结文件内容为空或格式无效");
                    return null;
                }

                lock (_lock)
                {
                    _lastSkillCount = record.SkillCount;
                    _cachedSkillSummary = record.Summary;
                }

                var age = DateTime.Now - record.GeneratedAt;
                Logger.Info($"[SkillService] 💾 从磁盘加载技能总结成功: {record.SkillCount} 个技能, 生成于 {age.TotalMinutes:F1} 分钟前");
                Logger.Info($"[SkillService] 💾 总结长度: {record.Summary.Length} 字符, 文件: {SkillsSummaryFilePath}");
                return record.Summary;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SkillService] 💾 加载技能总结文件失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 将技能总结持久化到本地磁盘。
        /// </summary>
        private void SaveSkillsSummaryToDisk(int skillCount, string summary)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SkillsSummaryFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var record = new SkillsSummaryRecord
                {
                    SkillCount = skillCount,
                    GeneratedAt = DateTime.Now,
                    Summary = summary,
                    ProjectSkillCount = _cachedResult?.ProjectSkillCount ?? 0,
                    UserSkillCount = _cachedResult?.UserSkillCount ?? 0,
                };

                string json = System.Text.Json.JsonSerializer.Serialize(record, SummaryJsonOptions);
                File.WriteAllText(SkillsSummaryFilePath, json, System.Text.Encoding.UTF8);

                Logger.Info($"[SkillService] 💾 技能总结已持久化到磁盘: {SkillsSummaryFilePath}");
                Logger.Info($"[SkillService] 💾   技能数量: {skillCount} (项目: {record.ProjectSkillCount}, 用户: {record.UserSkillCount})");
                Logger.Info($"[SkillService] 💾   文件大小: {json.Length} 字节, 总结长度: {summary.Length} 字符");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SkillService] 💾 持久化技能总结失败: {ex.Message}");
            }
        }

        #endregion

        #region Persistence Model

        /// <summary>
        /// 技能总结持久化记录（序列化为 JSON 存储到本地磁盘）。
        /// </summary>
        private class SkillsSummaryRecord
        {
            [System.Text.Json.Serialization.JsonPropertyName("skillCount")]
            public int SkillCount { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("generatedAt")]
            public DateTime GeneratedAt { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("summary")]
            public string Summary { get; set; } = string.Empty;

            [System.Text.Json.Serialization.JsonPropertyName("projectSkillCount")]
            public int ProjectSkillCount { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("userSkillCount")]
            public int UserSkillCount { get; set; }
        }

        #endregion
    }
}
