using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 技能系统：斜杠命令解析、技能帮助、缓存刷新、技能创建、AI 路由匹配。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Skill System

        /// <summary>
        /// 处理斜杠命令。如果用户输入以 / 开头，尝试匹配已注册的技能。
        /// 匹配成功时返回技能的完整指令文本，匹配失败时显示错误并返回 null。
        /// 非斜杠命令（不以 / 开头）返回 string.Empty 表示正常发送。
        /// </summary>
        private async Task<string?> ResolveSlashCommandAsync(string userText)
        {
            if (string.IsNullOrEmpty(userText) || !userText.StartsWith("/"))
                return string.Empty;

            var parts = userText.Substring(1).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;

            var commandName = parts[0].ToLowerInvariant();

            // ── 内置命令：/help — 列出所有可用技能 ──
            if (commandName == "help")
            {
                Logger.Info($"[Skill] 调用元命令: /help (用户输入: \"{userText}\")");
                await ShowSkillsHelpAsync();
                return null;
            }

            // ── 刷新技能缓存 ──
            if (commandName == "refresh-skills")
            {
                Logger.Info($"[Skill] 调用元命令: /refresh-skills (用户输入: \"{userText}\")");
                await RefreshSkillsAsync();
                return null;
            }

            // ── 创建技能 ──
            if (commandName == "create-skill")
            {
                var skillNameArg = parts.Length > 1 ? parts[1] : null;
                Logger.Info($"[Skill] 调用元命令: /create-skill (参数: {skillNameArg ?? "(无)"}, 用户输入: \"{userText}\")");
                await CreateSkillAsync(skillNameArg);
                return null;
            }

            // ── 查找匹配的技能 ──
            try
            {
                if (_skillDiscoveryResult == null)
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);

                var skill = SkillService.Instance.FindSkill(commandName, _skillDiscoveryResult);
                if (skill != null)
                {
                    Logger.Info($"[Skill] ═══ 用户显式调用技能 ═══");
                    Logger.Info($"[Skill]   技能名称: {skill.Name}");
                    Logger.Info($"[Skill]   调用方式: 斜杠命令 /{commandName}");
                    Logger.Info($"[Skill]   用户输入: \"{userText}\"");
                    Logger.Info($"[Skill]   技能来源: {skill.Source}");
                    Logger.Info($"[Skill]   技能描述: {skill.Description}");
                    Logger.Info($"[Skill]   文件路径: {skill.FilePath}");
                    Logger.Info($"[Skill]   资源文件数: {skill.ResourceFiles.Count}");
                    Logger.Info($"[Skill] ══════════════════════════");

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"🎯 已加载技能: {skill.Name}";

                    var instructions = skill.GetFullInstructions();
                    return $"用户通过 /{commandName} 调用了技能 \"{skill.Name}\"。请按以下技能指令执行：\n\n{instructions}";
                }
                else
                {
                    Logger.Warn($"[Skill] 未知斜杠命令: /{commandName}");

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var availableSkills = _skillDiscoveryResult?.UserInvocableSkills ?? new List<SkillDefinition>();
                    var skillListStr = availableSkills.Count > 0
                        ? string.Join("\n", availableSkills.ConvertAll(s => $"  • `/{s.Name}` — {s.Description}"))
                        : "  （暂无可用技能）";

                    var errorMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"⚠️ **未知命令**: `/{commandName}`\n\n" +
                                  $"可用的技能命令：\n{skillListStr}\n\n" +
                                  $"输入 `/help` 查看完整帮助。",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(errorMsg);
                    AddMessagesHtml("assistant", errorMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = $"⚠️ 未知命令: /{commandName}";

                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 斜杠命令处理失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 显示所有可用技能的帮助信息。
        /// </summary>
        private async Task ShowSkillsHelpAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                if (_skillDiscoveryResult == null)
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);

                var sb = new StringBuilder();
                sb.AppendLine("## 📋 Skill 系统");
                sb.AppendLine();
                sb.AppendLine("### 🛠️ 内置命令");
                sb.AppendLine("| 命令 | 说明 |");
                sb.AppendLine("|------|------|");
                sb.AppendLine("| `/help` | 显示此帮助信息 |");
                sb.AppendLine("| `/create-skill <名称>` | 创建新的自定义 Skill 模板 |");
                sb.AppendLine("| `/refresh-skills` | 强制刷新技能缓存 |");
                sb.AppendLine();

                var allSkills = _skillDiscoveryResult?.Skills ?? new List<SkillDefinition>();
                if (allSkills.Count == 0)
                {
                    sb.AppendLine("### 📦 自定义技能");
                    sb.AppendLine("暂无可用技能。");
                    sb.AppendLine();
                    sb.AppendLine("**快速创建技能：**");
                    sb.AppendLine("输入 `/create-skill 技能名` 一键生成模板。");
                    sb.AppendLine();
                    sb.AppendLine("**手动创建：**");
                    sb.AppendLine("1. 在项目根目录创建 `.github/skills/<技能名>/SKILL.md`");
                    sb.AppendLine("2. 或在用户目录创建 `~/.copilot/skills/<技能名>/SKILL.md`");
                    sb.AppendLine();
                    sb.AppendLine("**SKILL.md 格式：**");
                    sb.AppendLine("```yaml");
                    sb.AppendLine("---");
                    sb.AppendLine("name: my-skill");
                    sb.AppendLine("description: '技能描述（含触发关键词）。Use when: ...'");
                    sb.AppendLine("argument-hint: '[可选参数]'");
                    sb.AppendLine("user-invocable: true");
                    sb.AppendLine("---");
                    sb.AppendLine("# 技能标题");
                    sb.AppendLine("## 何时使用");
                    sb.AppendLine("- 场景一");
                    sb.AppendLine("## 流程");
                    sb.AppendLine("1. 步骤一");
                    sb.AppendLine("2. 步骤二");
                    sb.AppendLine("```");
                }
                else
                {
                    sb.AppendLine("### 📦 自定义技能");
                    sb.AppendLine("| 命令 | 来源 | 类型 | 说明 |");
                    sb.AppendLine("|------|------|------|------|");
                    foreach (var skill in allSkills)
                    {
                        var sourceLabel = skill.Source switch
                        {
                            SkillSource.Project => "📁 项目",
                            SkillSource.User => "👤 用户",
                            SkillSource.BuiltIn => "📦 内置",
                            _ => "❓"
                        };
                        var typeLabel = skill.UserInvocable ? "✅ 可调用" : "🤖 自动";
                        var desc = TruncateText(skill.Description, 60);
                        sb.AppendLine($"| `/{skill.Name}` | {sourceLabel} | {typeLabel} | {desc} |");
                    }
                    sb.AppendLine();
                    sb.AppendLine($"💡 输入 `/create-skill 新技能名` 创建更多技能。");
                }

                sb.AppendLine();
                sb.AppendLine("### ⏱️ 技能何时被调用？");
                sb.AppendLine("| 方式 | 触发条件 | 说明 |");
                sb.AppendLine("|------|----------|------|");
                sb.AppendLine("| 🗣️ **用户显式调用** | 输入 `/技能名` | 用户主动通过斜杠命令调用 |");
                sb.AppendLine("| 🧠 **AI 语义匹配** | AI 分析用户意图 | AI 自动识别匹配的技能并加载 |");
                sb.AppendLine("| 📍 **上下文推断** | 多轮对话积累 | AI 根据对话历史主动建议技能 |");

                var helpMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = sb.ToString(),
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(helpMsg);
                AddMessagesHtml("assistant", helpMsg.Content);
                UpdateBrowser();
                StatusLabel.Text = $"共 {allSkills.Count} 个技能可用";
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 显示技能帮助失败: {ex.Message}");
                StatusLabel.Text = "显示技能帮助失败";
            }
        }

        /// <summary>
        /// 强制刷新技能缓存。
        /// </summary>
        private async Task RefreshSkillsAsync()
        {
            try
            {
                _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath, forceRefresh: true);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var count = _skillDiscoveryResult?.TotalCount ?? 0;
                StatusLabel.Text = $"✅ 技能已刷新: 共 {count} 个技能";
                Logger.Info($"[Skill] 手动刷新完成: {count} 个技能");

                var sb = new StringBuilder();
                sb.AppendLine($"✅ **技能已刷新**: 共发现 {count} 个技能");
                if (_skillDiscoveryResult != null && _skillDiscoveryResult.UserInvocableSkills.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("可用命令：");
                    foreach (var s in _skillDiscoveryResult.UserInvocableSkills)
                        sb.AppendLine($"- `/{s.Name}` — {s.Description}");
                }

                var msg = new ChatMessage
                {
                    Role = "assistant",
                    Content = sb.ToString(),
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(msg);
                AddMessagesHtml("assistant", msg.Content);
                UpdateBrowser();
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 刷新技能失败: {ex.Message}");
                StatusLabel.Text = "刷新技能失败";
            }
        }

        /// <summary>
        /// 创建新的自定义 Skill 模板。
        /// </summary>
        private async Task CreateSkillAsync(string? skillName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                string? targetDir = null;
                string locationLabel;

                if (!string.IsNullOrEmpty(_solutionPath))
                {
                    var solutionDir = Path.GetDirectoryName(_solutionPath);
                    var current = solutionDir;
                    while (!string.IsNullOrEmpty(current))
                    {
                        if (Directory.GetFiles(current, "*.sln").Length > 0)
                        {
                            solutionDir = current;
                            break;
                        }
                        var parent = Directory.GetParent(current);
                        if (parent == null) break;
                        current = parent.FullName;
                    }

                    if (solutionDir != null)
                    {
                        targetDir = Path.Combine(solutionDir, ".github", "skills");
                        locationLabel = $"项目目录: {targetDir}";
                    }
                    else
                    {
                        targetDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".copilot", "skills");
                        locationLabel = $"用户目录: {targetDir}";
                    }
                }
                else
                {
                    targetDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".copilot", "skills");
                    locationLabel = $"用户目录: {targetDir}";
                }

                if (string.IsNullOrWhiteSpace(skillName))
                {
                    var promptMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = "## 🛠️ 创建新技能\n\n" +
                                  "请告诉我新技能的名称（小写字母+数字+连字符），例如：\n" +
                                  "```\n/create-skill my-test-helper\n```\n\n" +
                                  $"技能将创建在: `{locationLabel}`\n\n" +
                                  "技能名称规范：\n" +
                                  "- 1-64 个字符\n" +
                                  "- 仅限小写字母、数字和连字符 (-)\n" +
                                  "- 示例: `code-review`, `api-doc-gen`, `deploy-check`",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(promptMsg);
                    AddMessagesHtml("assistant", promptMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = "请输入技能名称";
                    return;
                }

                if (!Regex.IsMatch(skillName, @"^[a-z0-9][a-z0-9-]{0,63}$"))
                {
                    var errorMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"⚠️ **无效的技能名称**: `{skillName}`\n\n" +
                                  "技能名称必须：\n" +
                                  "- 1-64 个字符\n" +
                                  "- 仅限小写字母、数字和连字符 (-)\n" +
                                  "- 以字母或数字开头\n\n" +
                                  "有效示例: `code-review`, `api-doc-gen`, `deploy-check`",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(errorMsg);
                    AddMessagesHtml("assistant", errorMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = $"⚠️ 无效的技能名称: {skillName}";
                    return;
                }

                var skillDir = Path.Combine(targetDir!, skillName);
                if (Directory.Exists(skillDir))
                {
                    var existsMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"⚠️ 技能 `{skillName}` 已存在于 `{skillDir}`。\n\n" +
                                  $"输入 `/refresh-skills` 刷新后即可使用。",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(existsMsg);
                    AddMessagesHtml("assistant", existsMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = $"技能已存在: {skillName}";
                    return;
                }

                Directory.CreateDirectory(skillDir);

                var skillContent = $@"---
name: {skillName}
description: '[请填写技能描述，包含触发关键词。Use when: ...]'
argument-hint: '[可选参数提示]'
user-invocable: true
---

# {FormatSkillTitle(skillName)}

## 何时使用
- [描述触发此技能的用户场景]
- [例如：用户请求 XXX 操作时]

## 流程
1. [步骤一：描述第一步操作]
2. [步骤二：描述第二步操作]
3. [步骤三：描述第三步操作]

## 输出格式
- [描述期望的输出格式]
- [例如：使用 Markdown 表格、代码块等]

## 注意事项
- [列出需要注意的边界条件、限制等]
";

                var skillFilePath = Path.Combine(skillDir, "SKILL.md");
                File.WriteAllText(skillFilePath, skillContent, Encoding.UTF8);

                Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
                File.WriteAllText(Path.Combine(skillDir, "scripts", ".gitkeep"), string.Empty);
                Directory.CreateDirectory(Path.Combine(skillDir, "references"));
                File.WriteAllText(Path.Combine(skillDir, "references", ".gitkeep"), string.Empty);
                Directory.CreateDirectory(Path.Combine(skillDir, "assets"));
                File.WriteAllText(Path.Combine(skillDir, "assets", ".gitkeep"), string.Empty);

                Logger.Info($"[Skill] 创建技能 '{skillName}' 于: {skillDir}");

                _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath, forceRefresh: true);

                var successMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = $"## ✅ 技能创建成功!\n\n" +
                              $"**技能名称**: `{skillName}`\n" +
                              $"**位置**: `{skillDir}`\n\n" +
                              $"### 文件结构\n" +
                              $"```\n" +
                              $"{skillName}/\n" +
                              $"├── SKILL.md          ← 技能定义（请编辑 description）\n" +
                              $"├── scripts/          ← 可执行脚本\n" +
                              $"├── references/       ← 参考文档\n" +
                              $"└── assets/           ← 模板资源\n" +
                              $"```\n\n" +
                              $"### 下一步\n" +
                              $"1. ✏️ 编辑 `SKILL.md`，填写 **description**（这是 AI 发现技能的关键）\n" +
                              $"2. 📝 完善「何时使用」和「流程」部分\n" +
                              $"3. 🔄 输入 `/refresh-skills` 刷新缓存后即可使用\n\n" +
                              $"现在输入 `/{skillName}` 即可调用（模板内容需先完善）。",
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(successMsg);
                AddMessagesHtml("assistant", successMsg.Content);
                UpdateBrowser();
                StatusLabel.Text = $"✅ 技能已创建: {skillName}";
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 创建技能失败: {ex.Message}");
                StatusLabel.Text = "创建技能失败";
            }
        }

        /// <summary>
        /// 将技能名称格式化为标题（如 "my-test-skill" → "My Test Skill"）。
        /// </summary>
        private static string FormatSkillTitle(string skillName)
        {
            if (string.IsNullOrEmpty(skillName)) return skillName;
            var parts = skillName.Split('-');
            return string.Join(" ", Array.ConvertAll(parts,
                p => p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1) : p));
        }

        /// <summary>
        /// AI 技能路由：根据用户问题和可用技能总结，判断是否应自动调用某个技能。
        /// 发送轻量级 AI 查询，解析返回的 JSON 判断结果。
        /// </summary>
        private async Task<string?> RouteSkillAsync(string fullUserContent)
        {
            try
            {
                string? skillsSummary = SkillService.Instance.GetSkillsSummary();
                if (string.IsNullOrEmpty(skillsSummary))
                {
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);
                    skillsSummary = SkillService.Instance.GetSkillsSummary();
                }

                if (string.IsNullOrEmpty(skillsSummary) || _apiService == null)
                    return null;

                // RAG-MARK: no-truncate — 不再截断用户内容，完整传递给技能路由判断
                // RAG-SOURCE: user-message 用户消息内容（用于技能路由）
                string truncatedContent = fullUserContent;

                string routingUserPrompt = string.Format(
                    AiPrompts.SkillRoutingUserPrompt,
                    skillsSummary,
                    truncatedContent);

                var routingMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = AiPrompts.SkillRoutingSystemPrompt },
                    new ChatApiMessage { Role = "user", Content = routingUserPrompt }
                };

                StatusLabel.Text = "AI 正在匹配技能…";
                Logger.Info($"[SkillRoute] 开始技能路由判断 (用户输入 {fullUserContent.Length} 字符)");

                string? routingResponse = null;
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    routingResponse = await _apiService.CompleteAsync(routingMessages, cts.Token);
                    routingResponse = routingResponse?.Trim();
                }
                catch (OperationCanceledException)
                {
                    Logger.Warn("[SkillRoute] 路由判断超时，跳过技能匹配");
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[SkillRoute] 路由判断失败: {ex.Message}");
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }

                if (string.IsNullOrEmpty(routingResponse))
                {
                    Logger.Info("[SkillRoute] 路由判断返回空，跳过技能匹配");
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }

                SkillRoutingResult? routingResult = null;
                try
                {
                    string cleanJson = routingResponse;
                    if (cleanJson.StartsWith("```"))
                    {
                        int startIdx = cleanJson.IndexOf('\n');
                        int endIdx = cleanJson.LastIndexOf("```");
                        if (startIdx >= 0 && endIdx > startIdx)
                            cleanJson = cleanJson.Substring(startIdx + 1, endIdx - startIdx - 1).Trim();
                    }
                    routingResult = JsonSerializer.Deserialize<SkillRoutingResult>(cleanJson);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[SkillRoute] 解析路由结果 JSON 失败: {ex.Message}, 原始响应: {routingResponse}");
                    return null;
                }

                if (routingResult == null || !routingResult.HasSkill)
                {
                    Logger.Info($"[SkillRoute] AI 判断无需调用技能" +
                        (routingResult?.Reason != null ? $": {routingResult.Reason}" : ""));
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }

                string skillName = routingResult.Skill!;
                Logger.Info($"[SkillRoute] ═══ AI 自动匹配技能 ═══");
                Logger.Info($"[SkillRoute]   技能名称: {skillName}");
                Logger.Info($"[SkillRoute]   置信度: {routingResult.Confidence}");
                Logger.Info($"[SkillRoute]   匹配理由: {routingResult.Reason}");
                Logger.Info($"[SkillRoute] ══════════════════════════");

                if (_skillDiscoveryResult == null)
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);

                var matchedSkill = SkillService.Instance.FindSkill(skillName, _skillDiscoveryResult);
                if (matchedSkill == null)
                {
                    Logger.Warn($"[SkillRoute] AI 返回的技能 '{skillName}' 在可用列表中未找到");
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = $"🎯 AI 自动加载技能: {skillName}";

                var instructions = matchedSkill.GetFullInstructions();
                Logger.Info($"[SkillRoute] 技能指令长度: {instructions.Length} 字符");

                return $"AI 自动匹配到技能 \"{matchedSkill.Name}\"（置信度: {routingResult.Confidence}，理由: {routingResult.Reason}）。请按以下技能指令执行：\n\n{instructions}";
            }
            catch (Exception ex)
            {
                Logger.Error($"[SkillRoute] 技能路由异常: {ex.Message}");
                return null;
            }
        }


        #endregion
    }
}
