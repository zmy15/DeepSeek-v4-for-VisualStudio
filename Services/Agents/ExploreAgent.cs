using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Explore Agent — 只读代码库探索子代理。
    /// 
    /// 职责：
    /// - 使用多种搜索策略（glob、grep、语义搜索）在代码库中查找信息
    /// - 回答关于代码结构、依赖关系、实现模式的问题
    /// - 被 Plan Agent 作为子代理并行调用
    /// - 绝不修改任何文件
    /// 
    /// 参考: VS Code Copilot Chat Explore Agent
    /// </summary>
    public class ExploreAgent : BaseAgent
    {
        public ExploreAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Explore) { }

        #region Agent Definition

        /// <summary>
        /// Explore Agent 默认只读工具集。
        /// 这些工具只能检查工作区，绝不能修改文件。
        /// </summary>
        public static readonly string[] DefaultReadTools = new[]
        {
            "search",           // 语义搜索
            "file_search",      // glob 文件搜索
            "grep_search",      // 文本/正则搜索
            "read_file",        // 读取文件内容
            "list_dir",         // 列出目录内容
            "get_errors",       // 获取编译错误
            "get_changed_files",// 获取变更文件
            "fetch_webpage",    // 获取网页内容
            "github_repo",      // GitHub 仓库搜索
        };

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Explore,
                Name = "Explore",
                Description = "快速只读代码库探索和问答子代理。" +
                    "优先使用而非手动链接多个搜索和文件读取操作，避免污染主对话。" +
                    "支持并行调用。指定详细程度: quick, medium, 或 thorough。",
                ArgumentHint = "描述要搜索的内容和期望的详细程度 (quick/medium/thorough)",
                UserInvocable = false,
                AllowedTools = new List<string>(DefaultReadTools),
                SubAgents = new List<AgentType>(),
                Handoffs = new List<AgentHandoff>(),
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return CommonSystemPromptPrefix + "\n" +
                "你当前处于 **Explore 模式**——专精于快速代码库分析和高效回答问题。\n\n" +
                "## 核心原则\n" +
                "- 你只能读取代码，绝不能修改、创建或删除任何文件。\n" +
                "- 你的输出是分析报告，包含文件路径、关键符号、可复用的模式。\n" +
                "- 优先使用绝对文件路径引用（如 `F:\\VSCode\\project\\src\\Models\\User.cs`）。\n" +
                "- ⚠️ 所有路径必须使用 Windows 绝对路径格式，不要使用 Linux 风格路径（如 /usr/src）。\n\n" +
                "## 网页链接处理\n" +
                "- 如果用户提供了 URL 链接，你必须使用 fetch_webpage 工具来获取网页内容\n" +
                "- 获取后检查内容中是否有其他相关链接，设置 maxDepth 参数递归抓取直到收集了所有需要的信息\n\n" +
                "## 搜索策略\n" +
                "- **从宽到窄**: 先用 list_dir 了解目录结构，再用 file_search 或 grep_search 缩小范围\n" +
                "- **并行优先**: 同时发起多个独立的搜索和读取操作（不依赖彼此结果的可并行）\n" +
                "- **适时停止**: 一旦获取足够上下文就停止，不做穷举式扫描\n" +
                "- **根目录优先**: 始终从工作区根目录开始探索，不要猜测路径\n\n" +
                "## 输出格式\n" +
                "直接以消息形式报告发现。包含：\n" +
                "- 相关文件及其绝对路径链接\n" +
                "- 可复用的具体函数、类型或模式\n" +
                "- 可作为实现模板的类似已有功能\n" +
                "- 对所提问题的明确回答，而不是全面概述\n\n" +
                "## 详细程度\n" +
                "- **quick**: 只搜索最明显的匹配，返回最相关的结果\n" +
                "- **medium**: 做适度的多维度搜索，确保覆盖主要相关区域\n" +
                "- **thorough**: 深度搜索，确保不遗漏任何相关信息";
        }

        #endregion

        #region Execute

        /// <summary>
        /// Explore Agent 执行入口。
        /// 接收搜索任务描述，返回代码库分析结果。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            AddLog("INFO", $"Explore Agent 开始执行: \"{userMessage.Truncate(100)}\"");

            var result = new AgentResult
            {
                AgentType = AgentType.Explore,
                Success = true,
            };

            try
            {
                var ct = context.CancellationToken;

                // ── 构建消息列表 ──
                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = Definition.SystemPrompt },
                    new ChatApiMessage { Role = "user", Content = BuildExplorePrompt(userMessage, context) }
                };

                // ── 解析工作区根目录 ──
                // 对 .sln 文件取目录；对文件夹项目保持目录原样。
                // 注意：不能无条件调用 Path.GetDirectoryName()，否则文件夹项目
                // 的路径会被错误解析为父目录（如 "F:\Proj" → "F:\"）。
                string workspaceRoot = context.SolutionPath ?? string.Empty;
                if (!string.IsNullOrEmpty(workspaceRoot))
                {
                    try
                    {
                        if (File.Exists(workspaceRoot))
                            workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
                        // 目录路径保持原样，由 BuiltInToolService.NormalizeWorkspaceRoot 最终处理
                    }
                    catch { }
                }

                AddLog("INFO", $"Explore prompt 已构建 ({messages.Last().Content?.Length ?? 0} 字符), workspaceRoot={workspaceRoot}");

                // ── 使用工具调用循环 ──
                string thinkingContent = string.Empty;
                string fullContent = string.Empty;

                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    maxTokens: 4096,
                    onThinking: (thinking) =>
                    {
                        thinkingContent += thinking;
                    },
                    onContent: (content) =>
                    {
                        fullContent += content;
                    },
                    onToolCall: (toolNames) =>
                    {
                        AddLog("INFO", $"🔧 调用工具: {toolNames}");
                    });

                // ── 如果工具调用后有思考内容，附加到结果中 ──
                if (!string.IsNullOrEmpty(thinkingContent))
                {
                    result.Content = $"<details><summary>💭 思考过程</summary>\n\n{thinkingContent}\n\n</details>\n\n{aiResponse}";
                }
                else
                {
                    result.Content = aiResponse;
                }

                result.Logs.AddRange(_logs);

                AddLog("INFO", $"Explore Agent 完成 ({aiResponse.Length} 字符)");
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "探索已取消";
                AddLog("WARN", "探索已取消");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", $"探索失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 构建 Explore prompt，包含 workspace 上下文和搜索指导。
        /// </summary>
        private static string BuildExplorePrompt(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 探索任务");
            sb.AppendLine(userMessage);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                // ── 提取工作区根目录（处理 .sln 文件 vs 文件夹项目）──
                string workspaceRoot = context.SolutionPath;
                bool isSlnFile = false;
                try
                {
                    if (System.IO.File.Exists(workspaceRoot))
                    {
                        workspaceRoot = System.IO.Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
                        isSlnFile = true;
                    }
                }
                catch { }

                sb.AppendLine($"## 工作区");
                sb.AppendLine($"工作区根目录: `{workspaceRoot}`");
                if (isSlnFile)
                    sb.AppendLine($"解决方案文件: {context.SolutionPath}");
                else
                    sb.AppendLine($"项目类型: 文件夹项目（无 .sln 文件，如 CMake / Open Folder）");
                sb.AppendLine();
                sb.AppendLine("> ⚠️ 所有路径必须使用 Windows 绝对路径格式（如 `F:\\project\\src\\file.cs`）。");
                sb.AppendLine("> 不要使用 Linux 风格路径（如 `/home/user/...`）。");
                sb.AppendLine("> 使用 `list_dir` 从工作区根目录开始探索，用 `file_search` 和 `grep_search` 定位文件。");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(context.FileContext))
            {
                // 截断过长的文件上下文（Explore 只需要概览）
                string truncated = context.FileContext.Length > 3000
                    ? context.FileContext.Substring(0, 3000) + "\n... (已截断)"
                    : context.FileContext;
                sb.AppendLine("## 附加文件上下文");
                sb.AppendLine(truncated);
                sb.AppendLine();
            }

            if (context.ActivePlan != null)
            {
                sb.AppendLine("## 当前计划信息");
                sb.AppendLine($"{LocalizationService.Instance["explore.task"]}: {context.ActivePlan.Title}");
                sb.AppendLine($"当前步骤: {context.ActivePlan.CurrentStepIndex}/{context.ActivePlan.Steps.Count}");
                sb.AppendLine();
            }

            sb.AppendLine("## 搜索指令");
            sb.AppendLine("1. 使用多种搜索策略找到相关文件和代码");
            sb.AppendLine("2. 报告关键文件路径、函数、类、模式");
            sb.AppendLine("3. 识别可作为实现模板的类似已有功能");
            sb.AppendLine("4. 指出潜在的依赖关系和注意事项");
            sb.AppendLine();
            sb.AppendLine("请输出你的发现。");

            return sb.ToString();
        }

        #endregion

        #region Solution Discovery

        /// <summary>
        /// 可自动发现的源代码文件扩展名集合。
        /// </summary>
        private static readonly HashSet<string> SourceFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".vb", ".cpp", ".c", ".h", ".hpp", ".fs", ".fsx",
            ".xaml", ".xml", ".json", ".config", ".csproj", ".vbproj",
            ".py", ".js", ".ts", ".jsx", ".tsx", ".css", ".scss", ".less",
            ".html", ".htm", ".razor", ".cshtml", ".vbhtml",
            ".sql", ".md", ".txt", ".yml", ".yaml", ".ps1", ".psm1",
            ".go", ".rs", ".java", ".kt", ".swift", ".proto",
        };

        /// <summary>
        /// 自动发现解决方案中的源代码文件。
        /// 所有 DTE COM 操作集中在一次主线程切换中完成，避免多次切换导致 UI 卡顿。
        /// </summary>
        public async Task<List<string>> DiscoverSolutionFilesAsync(
            string solutionPath, int maxFiles = 200)
        {
            var discoveredFiles = new List<string>();

            try
            {
                // ── 所有 DTE 操作集中在一次 SwitchToMainThreadAsync 中完成 ──
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package
                    .GetGlobalService(typeof(EnvDTE.DTE));
                if (dte?.Solution == null || !dte.Solution.IsOpen)
                {
                    AddLog("INFO", "[Discover] 当前没有打开的解决方案，跳过文件发现");
                    return discoveredFiles;
                }

                AddLog("INFO", $"[Discover] 开始扫描解决方案: {solutionPath}");

                // 遍历解决方案中的所有项目（同步，在主线程上）
                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    if (discoveredFiles.Count >= maxFiles) break;

                    try
                    {
                        CollectProjectFilesRecursive(
                            project.ProjectItems, discoveredFiles, maxFiles);
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", $"[Discover] 扫描项目 {project.Name} 时出错: {ex.Message}");
                    }
                }

                AddLog("INFO", $"[Discover] 文件发现完成: {discoveredFiles.Count} 个文件");
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"[Discover] 文件发现失败: {ex.Message}");
            }

            // ── 回退：CMake / Open Folder 项目中 DTE ProjectItems 可能为空 ──
            // 使用目录扫描作为兜底方案，确保非 .sln 项目也能发现文件
            if (discoveredFiles.Count == 0 && !string.IsNullOrEmpty(solutionPath))
            {
                try
                {
                    string workspaceDir = solutionPath;
                    if (System.IO.File.Exists(workspaceDir))
                        workspaceDir = System.IO.Path.GetDirectoryName(workspaceDir) ?? workspaceDir;

                    if (System.IO.Directory.Exists(workspaceDir))
                    {
                        AddLog("INFO", $"[Discover] DTE 未发现文件，回退到目录扫描: {workspaceDir}");

                        var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "bin", "obj", ".git", ".vs", "node_modules", "packages",
                            "Debug", "Release", "out", ".github",
                        };

                        await Task.Run(() =>
                        {
                            try
                            {
                                var allFiles = System.IO.Directory.GetFiles(workspaceDir, "*.*", System.IO.SearchOption.AllDirectories);
                                foreach (var file in allFiles)
                                {
                                    if (discoveredFiles.Count >= maxFiles) break;

                                    string dir = System.IO.Path.GetDirectoryName(file) ?? "";
                                    bool excluded = false;
                                    foreach (var excludeDir in excludeDirs)
                                    {
                                        if (dir.IndexOf(excludeDir, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            excluded = true;
                                            break;
                                        }
                                    }
                                    if (excluded) continue;

                                    string ext = System.IO.Path.GetExtension(file);
                                    if (SourceFileExtensions.Contains(ext))
                                        discoveredFiles.Add(file);
                                }
                            }
                            catch (Exception ex)
                            {
                                AddLog("WARN", $"[Discover] 目录扫描回退失败: {ex.Message}");
                            }
                        });

                        AddLog("INFO", $"[Discover] 目录扫描回退完成: {discoveredFiles.Count} 个文件");
                    }
                }
                catch (Exception ex)
                {
                    AddLog("WARN", $"[Discover] 目录扫描回退异常: {ex.Message}");
                }
            }

            return discoveredFiles;
        }

        /// <summary>
        /// 同步递归遍历项目项，收集源代码文件路径。
        /// 必须在主线程上调用（访问 EnvDTE COM 对象）。
        /// </summary>
        private static void CollectProjectFilesRecursive(
            EnvDTE.ProjectItems projectItems,
            List<string> discoveredFiles,
            int maxFiles)
        {
            if (projectItems == null || discoveredFiles.Count >= maxFiles)
                return;

            foreach (EnvDTE.ProjectItem item in projectItems)
            {
                if (discoveredFiles.Count >= maxFiles) return;

                try
                {
                    // 递归处理子文件夹
                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        CollectProjectFilesRecursive(
                            item.ProjectItems, discoveredFiles, maxFiles);
                    }

                    // 获取文件路径
                    string? filePath = null;
                    try { filePath = item.FileNames[0]; } catch { }

                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        string ext = Path.GetExtension(filePath);
                        bool isSourceFile = SourceFileExtensions.Contains(ext);

                        if (isSourceFile && !discoveredFiles.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                        {
                            discoveredFiles.Add(filePath);
                        }
                    }
                }
                catch
                {
                    // 跳过无法访问的项
                }
            }
        }

        /// <summary>
        /// 使用 AI 根据用户查询和上下文智能生成多个搜索关键词。
        /// AI 能理解语义关联，生成原始查询中没有但高度相关的关键词。
        /// 例如："修复认证逻辑" → ["Authentication", "Login", "Token", "JWT", "Auth", "Identity"]
        /// </summary>
        /// <param name="userQuery">用户的原始问题/需求描述。</param>
        /// <param name="context">可选的附加上下文（如文件内容摘要、当前计划信息等）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>AI 生成的关键词集合，失败时返回空集合。</returns>
        private async Task<HashSet<string>> GenerateSearchKeywordsViaAiAsync(
            string userQuery, string? context, CancellationToken ct)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // ── 构建 AI prompt ──
                var userPrompt = new StringBuilder();
                userPrompt.AppendLine("根据以下用户查询，生成用于代码库搜索的相关关键词。");
                userPrompt.AppendLine();
                userPrompt.AppendLine($"用户查询: {userQuery}");
                if (!string.IsNullOrWhiteSpace(context))
                {
                    // 限制上下文长度
                    string ctx = context.Length > 2000 ? context.Substring(0, 2000) + "..." : context;
                    userPrompt.AppendLine($"上下文: {ctx}");
                }
                userPrompt.AppendLine();
                userPrompt.AppendLine("## 要求");
                userPrompt.AppendLine("1. 理解查询的真实意图，生成语义相关的关键词");
                userPrompt.AppendLine("2. 包含技术术语、类名、方法名、模块名、命名空间");
                userPrompt.AppendLine("3. 考虑常见的命名约定（驼峰、帕斯卡、下划线）");
                userPrompt.AppendLine("4. 包含同义词和相关概念（如 auth → login, authentication, token, jwt）");
                userPrompt.AppendLine("5. 每个关键词 2-40 个字符，生成 5-15 个关键词");
                userPrompt.AppendLine("6. 只返回关键词，每行一个，不要编号、解释或 Markdown 格式");
                userPrompt.AppendLine();
                userPrompt.AppendLine("关键词:");

                string systemPrompt = "你是一个代码库搜索专家。你的唯一任务是根据用户查询生成精准的代码搜索关键词。只返回关键词列表，每行一个。";

                string aiResponse = await CallAiShortAsync(systemPrompt, userPrompt.ToString(), ct, maxTokens: 256);

                // ── 解析 AI 返回的关键词 ──
                if (!string.IsNullOrWhiteSpace(aiResponse))
                {
                    foreach (var line in aiResponse.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        // 清理每行：去掉编号前缀（1. 2. - * 等）、首尾空白
                        string cleaned = line.Trim()
                            .TrimStart('-', '*', '•', '·', '>', ' ')
                            .Trim();

                        // 去掉数字编号前缀（如 "1. Authentication" → "Authentication"）
                        int dotIdx = cleaned.IndexOf('.');
                        if (dotIdx > 0 && dotIdx < 5 && cleaned.Substring(0, dotIdx).All(char.IsDigit))
                            cleaned = cleaned.Substring(dotIdx + 1).Trim();

                        // 跳过无用行
                        if (cleaned.Length < 2 || cleaned.Length > 60) continue;
                        if (cleaned.All(char.IsDigit)) continue;
                        if (cleaned.Contains(' ') && cleaned.Length > 40) continue; // 太长的短语

                        keywords.Add(cleaned);
                    }

                    if (keywords.Count > 0)
                        AddLog("INFO", $"[Discover] AI 生成 {keywords.Count} 个关键词: [{string.Join(", ", keywords.Take(10))}]"
                            + (keywords.Count > 10 ? $" ... 等 {keywords.Count} 个" : ""));
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Discover] AI 关键词生成失败 ({ex.Message})，回退到规则提取");
            }

            return keywords;
        }

        /// <summary>
        /// 智能文件发现：根据用户查询模糊搜索代码库中的相关文件。
        /// 
        /// 关键词生成策略（优先级从高到低）：
        /// 1. AI 语义理解 → 根据查询+上下文生成语义相关关键词（如"认证"→Auth/Token/JWT）
        /// 2. 规则提取 → 分词+去停用词+驼峰拆分（兜底）
        /// 
        /// 三阶段搜索策略：
        /// 1. 文件名匹配 — 提取查询关键词，匹配文件名
        /// 2. 内容搜索   — grep 搜索源码文件中是否包含关键词
        /// 3. 相关性排序 — 综合文件名和内容命中数加权排序
        /// 
        /// 适用场景：用户提出模糊需求（如"修改 UserService 类"、
        /// "重构认证逻辑"）时，自动定位可能相关的文件。
        /// </summary>
        /// <param name="solutionPath">解决方案路径。</param>
        /// <param name="userQuery">用户的原始问题/需求描述。</param>
        /// <param name="maxFiles">最多返回的文件数量，默认 30。</param>
        /// <param name="additionalContext">可选的附加上下文（文件内容、计划信息等），用于 AI 关键词生成。</param>
        /// <returns>按相关性降序排列的文件路径列表。</returns>
        public async Task<List<string>> DiscoverRelevantFilesAsync(
            string solutionPath, string userQuery, int maxFiles = 30, string? additionalContext = null)
        {
            var relevantFiles = new List<string>();

            if (string.IsNullOrWhiteSpace(userQuery))
            {
                // 无查询时回退到全量发现
                return await DiscoverSolutionFilesAsync(solutionPath);
            }

            try
            {
                AddLog("INFO", $"[Discover] 智能文件发现开始: \"{userQuery.Truncate(100)}\"");

                // ── 阶段 0: 提取搜索关键词（AI 优先，规则兜底）──
                var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 先尝试 AI 生成关键词
                CancellationToken ct = CancellationToken.None; // 公共 API 不强制要求 token
                var aiKeywords = await GenerateSearchKeywordsViaAiAsync(userQuery, additionalContext, ct);
                if (aiKeywords.Count > 0)
                {
                    keywords.UnionWith(aiKeywords);
                }

                // 再补充规则提取的关键词（作为兜底和补充）
                var ruleKeywords = ExtractKeywordsFromQuery(userQuery);
                keywords.UnionWith(ruleKeywords);

                if (keywords.Count == 0)
                {
                    AddLog("INFO", "[Discover] 未提取到有效关键词，回退到全量发现");
                    return await DiscoverSolutionFilesAsync(solutionPath);
                }

                AddLog("INFO", $"[Discover] 合并关键词: {keywords.Count} 个 "
                    + $"(AI: {aiKeywords.Count}, 规则: {ruleKeywords.Count}) "
                    + $"[{string.Join(", ", keywords.Take(8))}]");

                // ── 阶段 1: 收集候选文件 ──
                var candidateFiles = await DiscoverSolutionFilesAsync(solutionPath, maxFiles: 200);
                if (candidateFiles.Count == 0)
                {
                    AddLog("INFO", "[Discover] 未找到候选文件");
                    return relevantFiles;
                }

                // ── 阶段 2: 对每个候选文件评分 ──
                var scoredFiles = new List<(string FilePath, int Score)>();

                await Task.Run(() =>
                {
                    foreach (var filePath in candidateFiles)
                    {
                        string fileName = Path.GetFileName(filePath);
                        int score = ScoreFileRelevance(filePath, fileName, keywords);
                        if (score > 0)
                        {
                            scoredFiles.Add((filePath, score));
                        }
                    }
                });

                // ── 阶段 3: 按相关性降序排列，取前 maxFiles 个 ──
                relevantFiles = scoredFiles
                    .OrderByDescending(f => f.Score)
                    .Take(maxFiles)
                    .Select(f => f.FilePath)
                    .ToList();

                AddLog("INFO", $"[Discover] 智能文件发现完成: {relevantFiles.Count} 个相关文件 "
                    + $"(候选: {candidateFiles.Count}, 评分>0: {scoredFiles.Count})");
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"[Discover] 智能文件发现失败: {ex.Message}");
                // 回退到全量发现
                relevantFiles = await DiscoverSolutionFilesAsync(solutionPath);
            }

            return relevantFiles;
        }

        /// <summary>
        /// 从用户查询中提取有意义的搜索关键词。
        /// 过滤常见停用词和中文语气词。
        /// </summary>
        private static HashSet<string> ExtractKeywordsFromQuery(string userQuery)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 中文/英文停用词
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "a", "an", "is", "are", "was", "were", "be", "been",
                "have", "has", "had", "do", "does", "did", "will", "would",
                "can", "could", "should", "may", "might", "shall", "must",
                "to", "of", "in", "for", "on", "with", "at", "by", "from",
                "and", "or", "not", "but", "if", "then", "else", "when",
                "this", "that", "these", "those", "it", "its", "my", "our",
                "i", "you", "he", "she", "we", "they", "me", "him", "her",
                "请", "帮我", "帮忙", "一下", "一个", "这个", "那个", "哪些",
                "什么", "怎么", "如何", "为什么", "能不能", "可以", "需要",
                "修改", "重构", "优化", "添加", "删除", "实现", "创建",
                "代码", "文件", "功能", "逻辑", "需求", "问题",
            };

            // 按常见分隔符拆分
            var tokens = userQuery.Split(
                new[] { ' ', '\t', '\n', '\r', '，', '。', '！', '？', '、', '：', '；',
                        '(', ')', '[', ']', '{', '}', '<', '>', '"', '\'', '`', '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                string cleaned = token.Trim().TrimEnd('.', ',', ':', ';', '!', '?');

                // 跳过停用词、过短、纯数字、纯标点
                if (cleaned.Length < 2) continue;
                if (stopWords.Contains(cleaned)) continue;
                if (cleaned.All(char.IsDigit)) continue;
                if (cleaned.All(c => !char.IsLetterOrDigit(c))) continue;

                // 驼峰/帕斯卡命名拆分：UserService → User, Service
                if (cleaned.Length > 2 && char.IsUpper(cleaned[0]))
                {
                    keywords.Add(cleaned); // 保留原始形式

                    // 拆分驼峰
                    var parts = SplitCamelCase(cleaned);
                    foreach (var part in parts)
                    {
                        if (part.Length >= 2 && !stopWords.Contains(part))
                            keywords.Add(part);
                    }
                }
                else
                {
                    keywords.Add(cleaned);
                }
            }

            return keywords;
        }

        /// <summary>
        /// 拆分驼峰/帕斯卡命名（如 "UserService" → {"User", "Service"}）。
        /// </summary>
        private static List<string> SplitCamelCase(string name)
        {
            var parts = new List<string>();
            int start = 0;
            for (int i = 1; i < name.Length; i++)
            {
                if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                {
                    parts.Add(name.Substring(start, i - start));
                    start = i;
                }
            }
            if (start < name.Length)
                parts.Add(name.Substring(start));
            return parts;
        }

        /// <summary>
        /// 对单个文件计算与查询关键词的相关性得分。
        /// 评分规则：
        /// - 文件名完全匹配关键词: +10 分/词
        /// - 文件名部分匹配关键词: +5 分/词
        /// - 文件内容包含关键词: +3 分/行
        /// - 内容命中行数越多分数越高（有上限）
        /// </summary>
        private static int ScoreFileRelevance(string filePath, string fileName, HashSet<string> keywords)
        {
            int score = 0;

            // ── 文件名匹配 ──
            string fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);
            foreach (var kw in keywords)
            {
                // 完全匹配文件名
                if (fileNameNoExt.Equals(kw, StringComparison.OrdinalIgnoreCase))
                {
                    score += 10;
                }
                // 文件名包含关键词
                else if (fileNameNoExt.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 5;
                }
            }

            // ── 文件内容搜索（grep） ──
            try
            {
                // 只读取文本文件进行内容搜索
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                bool isTextFile = ext is ".cs" or ".vb" or ".cpp" or ".c" or ".h" or ".hpp"
                    or ".fs" or ".fsx" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx"
                    or ".java" or ".go" or ".rs" or ".swift" or ".kt" or ".php"
                    or ".xml" or ".json" or ".yaml" or ".yml" or ".md" or ".txt"
                    or ".xaml" or ".csproj" or ".vbproj" or ".config" or ".sql"
                    or ".css" or ".html" or ".htm" or ".razor" or ".cshtml";

                if (!isTextFile) return score;

                // 读取文件内容并在行级别搜索
                var lines = File.ReadAllLines(filePath);
                const int maxLineScore = 30; // 内容搜索最高30分
                int lineHitCount = 0;

                foreach (var line in lines)
                {
                    foreach (var kw in keywords)
                    {
                        if (line.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            lineHitCount++;
                            break; // 每行最多计1次
                        }
                    }
                }

                score += Math.Min(lineHitCount * 3, maxLineScore);
            }
            catch
            {
                // 无法读取的文件跳过内容搜索
            }

            return score;
        }

        /// <summary>
        /// 检测用户消息中是否引用了具体文件。
        /// 如果用户消息中包含文件路径或文件名+扩展名模式，
        /// 则认为用户已指明文件，无需自动附加解决方案全部代码。
        /// </summary>
        /// <param name="userText">用户输入的原始文本。</param>
        /// <returns>true 表示用户已指明文件。</returns>
        public static bool UserMessageReferencesFiles(string? userText)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return false;

            // 模式 1: 包含常见源代码文件扩展名（如 .cs、.py 等）
            var codeFilePattern = new Regex(
                @"\.(cs|vb|cpp|c|h|hpp|fs|py|js|ts|jsx|tsx|java|go|rs|swift|kt|php|rb|lua|sql|xml|json|yaml|yml|md|css|html|xaml|csproj|vbproj|sln|config|razor|cshtml)\b",
                RegexOptions.IgnoreCase);
            if (codeFilePattern.IsMatch(userText))
                return true;

            // 模式 2: 包含 Windows 绝对路径（盘符 + 反斜杠）
            if (Regex.IsMatch(userText, @"[A-Za-z]:\\"))
                return true;

            // 模式 3: 包含 Unix 绝对路径或相对路径模式
            if (Regex.IsMatch(userText, @"(?:^|\s)[./~].*[/\\]"))
                return true;

            // 模式 4: 包含文件名+扩展名组合（如 "Program.cs"、"app.py"）
            var fileNamePattern = new Regex(
                @"\b\w+\.(cs|vb|cpp|c|h|hpp|fs|py|js|ts|jsx|tsx|java|go|rs|swift|kt|php|rb|lua|sql|xml|json|yaml|yml|md|css|html|xaml)\b",
                RegexOptions.IgnoreCase);
            if (fileNamePattern.IsMatch(userText))
                return true;

            return false;
        }

        #endregion
    }
}
