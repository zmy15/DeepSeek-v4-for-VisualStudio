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
                var rawArgs = parts.Length > 1 ? parts[1] : null;
                var (parsedName, options) = ParseCreateSkillArgs(rawArgs);
                Logger.Info($"[Skill] 调用元命令: /create-skill (名称: {parsedName ?? "(无)"}, alwaysInject: {options.AlwaysInject}, 用户输入: \"{userText}\")");
                await CreateSkillAsync(parsedName, options);
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
                    StatusLabel.Text = string.Format(LocalizationService.Instance["skills.loaded"], skill.Name);

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
                    StatusLabel.Text = string.Format(LocalizationService.Instance["skills.unknownCommand"], commandName);

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

                var L = LocalizationService.Instance;
                var sb = new StringBuilder();
                sb.AppendLine(L["skills.help.title"]);
                sb.AppendLine();
                sb.AppendLine(L["skills.help.builtinCommands"]);
                sb.AppendLine("| 命令 | 说明 |");
                sb.AppendLine("|------|------|");
                sb.AppendLine($"| `/help` | {L["skills.help.cmdHelp"]} |");
                sb.AppendLine($"| `/create-skill <名称>` | {L["skills.help.cmdCreateSkill"]} |");
                sb.AppendLine($"| `/refresh-skills` | {L["skills.help.cmdRefresh"]} |");
                sb.AppendLine();

                var allSkills = _skillDiscoveryResult?.Skills ?? new List<SkillDefinition>();
                if (allSkills.Count == 0)
                {
                    sb.AppendLine(L["skills.help.customSkills"]);
                    sb.AppendLine(L["skills.help.noSkills"]);
                }
                else
                {
                    sb.AppendLine(L["skills.help.customSkills"]);
                    sb.AppendLine("| 命令 | 来源 | 类型 | 说明 |");
                    sb.AppendLine("|------|------|------|------|");
                    foreach (var skill in allSkills)
                    {
                        var sourceLabel = skill.Source switch
                        {
                            SkillSource.Project => L["popup.skillSource.project"],
                            SkillSource.User => L["popup.skillSource.user"],
                            SkillSource.BuiltIn => L["popup.skillSource.package"],
                            _ => "❓"
                        };
                        var typeLabel = skill.UserInvocable ? L["skills.help.typeInvocable"] : L["skills.help.typeAuto"];
                        var desc = TruncateText(skill.Description, 60);
                        sb.AppendLine($"| `/{skill.Name}` | {sourceLabel} | {typeLabel} | {desc} |");
                    }
                    sb.AppendLine();
                    sb.AppendLine(L["skills.help.createMore"]);
                }

                sb.AppendLine();
                sb.AppendLine(L["skills.help.whenCalled"]);
                sb.AppendLine(L["skills.help.whenCalledTable"]);

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
                StatusLabel.Text = string.Format(L["skills.countAvailable"], allSkills.Count);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 显示技能帮助失败: {ex.Message}");
                StatusLabel.Text = LocalizationService.Instance["status.skillHelpFailed"];
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

                var L = LocalizationService.Instance;
                var count = _skillDiscoveryResult?.TotalCount ?? 0;
                StatusLabel.Text = L.Format("status.skillRefreshed", count);
                Logger.Info($"[Skill] 手动刷新完成: {count} 个技能");

                var sb = new StringBuilder();
                sb.AppendLine(L.Format("skills.refreshed", count));
                if (_skillDiscoveryResult != null && _skillDiscoveryResult.UserInvocableSkills.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(L["skills.availableCommands"]);
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
                StatusLabel.Text = LocalizationService.Instance["status.skillRefreshFailed"];
            }
        }

        /// <summary>
        /// 创建新的自定义 Skill 模板。
        /// </summary>
        private async Task CreateSkillAsync(string? skillName, SkillCreateOptions options = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                string? targetDir = null;
                string locationLabel = string.Empty;

                // ── 根据 --level 选项确定目标目录 ──
                if (options.Level == SkillSource.User)
                {
                    targetDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".vs", "deepseek", "skills");
                    locationLabel = targetDir;
                }
                else if (options.Level == SkillSource.Project)
                {
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
                            locationLabel = targetDir;
                        }
                    }

                    if (targetDir == null)
                    {
                        // 无解决方案时回退到用户目录
                        targetDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".vs", "deepseek", "skills");
                        locationLabel = targetDir;
                    }
                }
                else
                {
                    // ── 自动检测（默认行为）──
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
                            locationLabel = targetDir;
                        }
                        else
                        {
                            targetDir = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                ".vs", "deepseek", "skills");
                            locationLabel = targetDir;
                        }
                    }
                    else
                    {
                        targetDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".vs", "deepseek", "skills");
                        locationLabel = targetDir;
                    }
                }

                var L = LocalizationService.Instance;

                if (string.IsNullOrWhiteSpace(skillName))
                {
                    var promptMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = L.Format("skills.create.promptName", locationLabel),
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(promptMsg);
                    AddMessagesHtml("assistant", promptMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = L["skills.enterName"];
                    return;
                }

                if (!Regex.IsMatch(skillName, @"^[a-z0-9][a-z0-9-]{0,63}$"))
                {
                    var errorMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = L.Format("skills.create.invalidName", skillName),
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(errorMsg);
                    AddMessagesHtml("assistant", errorMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = L.Format("skills.invalidName", skillName);
                    return;
                }

                var skillDir = Path.Combine(targetDir!, skillName);
                if (Directory.Exists(skillDir))
                {
                    var existsMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = L.Format("skills.create.alreadyExists", skillName, skillDir),
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(existsMsg);
                    AddMessagesHtml("assistant", existsMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = L.Format("status.skillExists", skillName);
                    return;
                }

                Directory.CreateDirectory(skillDir);

                var skillContent = L.Format("skills.template.content", skillName, FormatSkillTitle(skillName));
                // 如果指定了 --always-inject，在 YAML front matter 中插入
                if (options.AlwaysInject)
                {
                    skillContent = skillContent.Replace(
                        "user-invocable: true",
                        "user-invocable: true\nalways-inject: true");
                }

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

                var alwaysInjectLine = options.AlwaysInject
                    ? L["skills.create.success.alwaysInject"] + "\n"
                    : "";
                var successMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = L.Format("skills.create.success", skillName, skillDir, alwaysInjectLine, skillFilePath),
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(successMsg);
                AddMessagesHtml("assistant", successMsg.Content);
                UpdateBrowser();
                StatusLabel.Text = L.Format("status.skillCreated", skillName);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 创建技能失败: {ex.Message}");
                StatusLabel.Text = LocalizationService.Instance["skills.createFailed"];
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

                if (string.IsNullOrEmpty(skillsSummary) || _activeAgent == null)
                    return null;

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

                StatusLabel.Text = LocalizationService.Instance["skills.matching"];
                Logger.Info($"[SkillRoute] 开始技能路由判断 (用户输入 {fullUserContent.Length} 字符)");

                string? routingResponse = null;
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    routingResponse = await _activeAgent.CallAiWithMessagesAsync(routingMessages, cts.Token, responseFormat: "json_object");
                    routingResponse = routingResponse?.Trim();
                }
                catch (OperationCanceledException)
                {
                    Logger.Warn("[SkillRoute] 路由判断超时，跳过技能匹配");
                    StatusLabel.Text = LocalizationService.Instance["status.thinking"];
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[SkillRoute] 路由判断失败: {ex.Message}");
                    StatusLabel.Text = LocalizationService.Instance["status.thinking"];
                    return null;
                }

                if (string.IsNullOrEmpty(routingResponse))
                {
                    Logger.Info("[SkillRoute] 路由判断返回空，跳过技能匹配");
                    StatusLabel.Text = LocalizationService.Instance["status.thinking"];
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
                    StatusLabel.Text = LocalizationService.Instance["status.thinking"];
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
                    StatusLabel.Text = LocalizationService.Instance["status.thinking"];
                    return null;
                }

                // 始终注入的技能已在系统提示中完整加载，无需重复注入
                if (matchedSkill.AlwaysInject)
                {
                    Logger.Info($"[SkillRoute] 技能 '{skillName}' 已标记为始终注入，跳过重复加载");
                    StatusLabel.Text = LocalizationService.Instance["status.thinking"];
                    return null;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = string.Format(LocalizationService.Instance["skills.autoLoaded"], skillName);

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

        #region Create Skill Options

        /// <summary>
        /// /create-skill 命令的选项。
        /// </summary>
        private struct SkillCreateOptions
        {
            /// <summary>是否每次对话都注入完整指令</summary>
            public bool AlwaysInject;

            /// <summary>技能级别（null=自动，Project=项目级，User=用户级）</summary>
            public SkillSource? Level;
        }

        /// <summary>
        /// 解析 /create-skill 的参数，分离技能名称和选项。
        /// 支持: /create-skill name --always-inject --level project|user
        /// </summary>
        private static (string? skillName, SkillCreateOptions options) ParseCreateSkillArgs(string? rawArgs)
        {
            var options = new SkillCreateOptions();
            if (string.IsNullOrWhiteSpace(rawArgs))
                return (null, options);

            var parts = rawArgs!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string? skillName = null;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.StartsWith("--", StringComparison.Ordinal))
                {
                    switch (part.ToLowerInvariant())
                    {
                        case "--always-inject":
                            options.AlwaysInject = true;
                            break;
                        case "--level":
                            if (i + 1 < parts.Length)
                            {
                                var levelArg = parts[++i].ToLowerInvariant();
                                options.Level = levelArg switch
                                {
                                    "project" => SkillSource.Project,
                                    "user" => SkillSource.User,
                                    _ => null
                                };
                            }
                            break;
                    }
                }
                else if (skillName == null)
                {
                    skillName = part;
                }
            }

            return (skillName, options);
        }

        #endregion
    }
}
