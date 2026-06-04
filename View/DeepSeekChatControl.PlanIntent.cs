using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 计划上下文意图检测：当存在待处理计划时，识别用户输入的执行/修改意图。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Plan Contextual Intent Detection

        /// <summary>
        /// 上下文感知意图覆盖：当存在待处理计划（_pendingHandoff）时，
        /// 识别用户输入的执行/修改意图，覆盖 AI 路由结果。
        /// 
        /// 场景：
        /// - "开始执行" / "执行计划" → 直接路由到 Edit Agent 执行计划（效果等同点击按钮）
        /// - "修改第三步..." / "换个方案" → 路由到 Plan Agent 重新规划
        /// </summary>
        private AgentRoutingResult OverrideRoutingForPlanContext(string userText, AgentRoutingResult originalRouting)
        {
            // 用户显式指定 Agent（如 @plan / @edit），尊重用户意图，不覆盖
            if (originalRouting.IsExplicit)
                return originalRouting;

            // 没有待处理计划时，尝试从持久化的 HandoffJson 恢复
            if (_pendingHandoff == null || !_pendingHandoff.ShowContinueOn)
            {
                _pendingHandoff = TryRestorePendingHandoffFromMessages();
            }

            // 仍未找到待处理计划，不覆盖路由
            if (_pendingHandoff == null || !_pendingHandoff.ShowContinueOn)
                return originalRouting;

            // ── 执行意图：直接开始执行计划 ──
            if (IsPlanExecutionIntent(userText))
            {
                Logger.Info($"[PlanContext] 检测到执行意图，路由到 Edit Agent: \"{userText.Truncate(50)}\"");
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Edit,
                    Confidence = "high",
                    Reason = "检测到执行意图，且存在待处理计划",
                    NeedsPlanning = false,
                    IsExplicit = true,
                };
            }

            // ── 修改计划意图：重新规划 ──
            if (IsPlanModificationIntent(userText))
            {
                Logger.Info($"[PlanContext] 检测到修改计划意图，路由到 Plan Agent: \"{userText.Truncate(50)}\"");
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Plan,
                    Confidence = "high",
                    Reason = "检测到修改计划意图，重新规划",
                    NeedsPlanning = true,
                    IsExplicit = true,
                };
            }

            // ── 问答意图：用户可能在询问计划相关问题 ──
            if (IsQuestionAboutPlan(userText))
            {
                Logger.Info($"[PlanContext] 检测到计划相关提问，路由到 Ask Agent: \"{userText.Truncate(50)}\"");
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Ask,
                    Confidence = "high",
                    Reason = "存在待处理计划，用户消息为计划相关提问",
                    NeedsPlanning = false,
                    IsExplicit = true,
                };
            }

            // ── 原路由为 Edit 但置信度不高，且无明确修改关键词 → 保守路由到 Ask ──
            // 场景：AI 路由失败时启发式可能误判"实现"等中性别关键词为 Edit，
            // 存在待处理计划时应保守处理，避免直接执行修改
            if (originalRouting.TargetAgent == AgentType.Edit
                && (originalRouting.Confidence == "medium" || originalRouting.Confidence == "low")
                && !HasExplicitEditIntent(userText))
            {
                Logger.Info($"[PlanContext] Edit 路由低置信度且无明确修改意图，改为 Ask: \"{userText.Truncate(50)}\"");
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Ask,
                    Confidence = "medium",
                    Reason = "存在待处理计划，低置信度 Edit 路由保守回退到 Ask",
                    NeedsPlanning = false,
                    IsExplicit = true,
                };
            }

            // ── 原路由为 Ask 且置信度不高时，检查是否在讨论计划 ──
            if (originalRouting.TargetAgent == AgentType.Ask
                && (originalRouting.Confidence == "medium" || originalRouting.Confidence == "low"))
            {
                var planDiscussionKeywords = new[]
                {
                    "步骤", "第", "step", "计划", "方案", "plan",
                    "不满意", "调整", "修改", "改成", "换成", "换个",
                    "第三步", "第二步", "第一步", "第四步", "第五步",
                };
                if (planDiscussionKeywords.Any(k =>
                    userText.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Info($"[PlanContext] Ask 路由低置信度 + 计划关键词，改为 Plan Agent: \"{userText.Truncate(50)}\"");
                    return new AgentRoutingResult
                    {
                        TargetAgent = AgentType.Plan,
                        Confidence = "medium",
                        Reason = "存在待处理计划，用户消息涉及计划讨论",
                        NeedsPlanning = true,
                        IsExplicit = true,
                    };
                }
            }

            return originalRouting;
        }

        /// <summary>
        /// 判断用户输入是否为"执行计划"意图。
        /// </summary>
        private static bool IsPlanExecutionIntent(string userText)
        {
            // 精确匹配：开始执行、执行计划 等
            var exactMatches = new[]
            {
                "开始执行", "执行计划", "开始实现", "确认执行",
                "开始吧", "执行吧", "开始实施", "开始干活",
                "go", "execute", "run it", "start",
            };

            if (exactMatches.Any(k => string.Equals(userText.Trim(), k, StringComparison.OrdinalIgnoreCase)))
                return true;

            // 短文本包含执行关键词
            if (userText.Length <= 10)
            {
                var shortExecutionKeywords = new[]
                {
                    "执行", "开始", "实现", "实施", "启动",
                    "execute", "start", "run", "go ahead", "do it",
                };
                if (shortExecutionKeywords.Any(k =>
                    userText.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 判断用户输入是否为"修改计划"意图。
        /// </summary>
        private static bool IsPlanModificationIntent(string userText)
        {
            var modifyKeywords = new[]
            {
                "修改计划", "调整方案", "改一下计划", "重新规划",
                "换个方案", "不满意", "修改第", "调整第",
                "改第", "步骤.*改", "方案.*调整",
                "改成", "换成", "前面.*改",
            };

            return modifyKeywords.Any(k =>
            {
                if (k.Contains(".*"))
                {
                    // 简单模式匹配：检查两个部分是否都存在
                    var parts = k.Split(new[] { ".*" }, StringSplitOptions.None);
                    return parts.All(p =>
                        userText.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                return userText.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        /// <summary>
        /// 判断用户输入是否是在询问计划相关问题（非修改、非执行）。
        /// 例如："第三步是什么意思"、"这个方案可行吗"、"为什么需要这样做"
        /// </summary>
        private static bool IsQuestionAboutPlan(string userText)
        {
            // 疑问句式（排除 URL 中的 ?，仅匹配中文问号或句末/空格后的英文问号）
            var questionPatterns = new[]
            {
                "是什么", "为什么", "怎么做", "如何", "怎么样",
                "可行吗", "可以吗", "对吗", "好不好",
                "什么意思", "能否", "是否", "能不能",
                "what", "why", "how", "explain",
                "？",  // 中文问号不会出现在 URL 中
            };

            // 计划相关上下文词（结合疑问才算计划提问）
            var planContextWords = new[]
            {
                "步骤", "第", "计划", "方案", "step",
                "这个", "这样", "那种",
            };
            // 注意: "plan" 从上下文词中移除，避免匹配 @plan 显式路由前缀

            bool hasQuestion = questionPatterns.Any(p =>
                userText.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

            // 英文问号单独处理：排除 URL 中的 ?（避免 ?envType=… 等被误判）
            if (!hasQuestion)
            {
                int qmIdx = userText.IndexOf('?');
                if (qmIdx >= 0)
                {
                    // 问号前面的字符不是 URL 分隔符（/ = & - _ .）时才算真正的疑问
                    char prev = qmIdx > 0 ? userText[qmIdx - 1] : ' ';
                    if (!IsUrlSeparatorChar(prev))
                        hasQuestion = true;
                }
            }

            bool hasPlanContext = planContextWords.Any(p =>
                userText.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

            return hasQuestion && hasPlanContext;
        }

        /// <summary>
        /// 判断字符是否为 URL 中常见的分隔符（用于排除 URL 中的 ? 被误判为问句）。
        /// </summary>
        private static bool IsUrlSeparatorChar(char c)
        {
            return c == '/' || c == '=' || c == '&' || c == '-' || c == '_' || c == '.'
                || c == '?' || c == '#' || c == ':'
                || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9');
        }

        /// <summary>
        /// 判断用户输入是否包含明确的代码修改意图（如"修复"/"添加"/"删除"等强动作词）。
        /// 用于区分"讨论计划"和"执行修改"。
        /// </summary>
        private static bool HasExplicitEditIntent(string userText)
        {
            var strongEditKeywords = new[]
            {
                "修复", "fix", "添加", "add", "删除", "delete", "remove",
                "创建", "create", "修改", "change", "update",
                "重构", "refactor", "实现", "implement",
                "写一个", "编写", "write", "生成", "generate",
                "改一下", "修改一下",
            };

            return strongEditKeywords.Any(k =>
                userText.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 从消息列表的 HandoffJson 中恢复 _pendingHandoff（用于会话切换后的上下文感知路由）。
        /// </summary>
        private AgentHandoff? TryRestorePendingHandoffFromMessages()
        {
            lock (_lock)
            {
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    var msg = _messages[i];
                    if (!string.IsNullOrEmpty(msg.HandoffJson))
                    {
                        try
                        {
                            var handoff = JsonSerializer.Deserialize<AgentHandoff>(
                                msg.HandoffJson,
                                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                            if (handoff != null && handoff.ShowContinueOn)
                            {
                                Logger.Info("[PlanContext] 从 HandoffJson 恢复 _pendingHandoff");
                                return handoff;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[PlanContext] 反序列化 HandoffJson 失败: {ex.Message}");
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 从消息列表的 PlanJson 中恢复 ActivePlan 到 AgentDispatcher。
        /// 用于"开始执行"文本输入直接路由到 Edit 时，确保 EditAgent 按步骤执行而非单步回退。
        /// </summary>
        private void RestoreActivePlanIfNeeded(AgentContext context)
        {
            if (_agentDispatcher == null) return;

            // 已有 ActivePlan，无需恢复
            if (_agentDispatcher.ActivePlan != null && _agentDispatcher.ActivePlan.Steps.Count > 0)
                return;

            lock (_lock)
            {
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    var msg = _messages[i];
                    if (!string.IsNullOrEmpty(msg.PlanJson))
                    {
                        try
                        {
                            var plan = JsonSerializer.Deserialize<AgentTaskPlan>(
                                msg.PlanJson,
                                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                            if (plan != null && plan.Steps.Count > 0 && !plan.IsCompleted && !plan.IsCancelled)
                            {
                                _agentDispatcher.ActivePlan = plan;
                                context.ActivePlan = plan;
                                context.IsPlanningMode = true;

                                // 重建 PlanFilePath
                                string baseDir = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "DeepSeekVS", "plans");
                                string subDir;
                                if (!string.IsNullOrEmpty(_solutionPath))
                                {
                                    using (var sha256 = SHA256.Create())
                                    {
                                        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(_solutionPath));
                                        var hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
                                        subDir = Path.Combine(baseDir, $"proj_{hash}");
                                    }
                                }
                                else
                                {
                                    subDir = Path.Combine(baseDir, "_unsaved");
                                }
                                context.PlanFilePath = Path.Combine(subDir, "plan.md");

                                Logger.Info($"[PlanContext] 从 PlanJson 恢复 ActivePlan: {plan.Steps.Count} 个步骤 (PlanId={plan.PlanId})");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[PlanContext] 反序列化 PlanJson 失败: {ex.Message}");
                        }
                    }
                }
            }
        }

        #endregion
    }
}
