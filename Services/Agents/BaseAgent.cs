using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// 所有 Agent 的抽象基类。
    /// 提供：AI 调用、日志、权限请求、文件解析等共享能力。
    /// </summary>
    public abstract class BaseAgent : IDisposable
    {
        protected readonly DeepSeekApiService _apiService;
        protected readonly List<AgentLogEntry> _logs = new();

        /// <summary>Agent 元数据定义</summary>
        public AgentDefinition Definition { get; protected set; }

        /// <summary>当前执行上下文</summary>
        public AgentContext? Context { get; set; }

        /// <summary>内置工具服务引用（由 AgentDispatcher 注入）</summary>
        public BuiltInToolService? BuiltInTools { get; set; }

        /// <summary>MCP 管理器引用（由 AgentDispatcher 注入，用于执行 MCP 工具）</summary>
        public McpManagerService? McpManager { get; set; }

        /// <summary>日志事件</summary>
        public event Action<AgentLogEntry>? LogEntryAdded;

        /// <summary>权限请求事件</summary>
        public event Action<AgentPermissionRequest>? PermissionRequested;

        /// <summary>文件变更实时通知事件（编辑阶段逐文件推送）</summary>
        public event Action<AgentFileChangeEventArgs>? FileChangeNotified;

        /// <summary>当前待确认的权限请求</summary>
        public AgentPermissionRequest? PendingPermission { get; protected set; }

        protected BaseAgent(DeepSeekApiService apiService, AgentType agentType)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
            Definition = CreateDefinition(agentType);
        }

        #region Abstract

        /// <summary>
        /// 子类实现：定义 Agent 的元数据（名称、描述、工具、系统提示词等）。
        /// </summary>
        protected abstract AgentDefinition CreateDefinition(AgentType agentType);

        /// <summary>
        /// 子类实现：Agent 核心执行逻辑。
        /// </summary>
        /// <param name="userMessage">用户消息</param>
        /// <param name="context">执行上下文</param>
        /// <returns>执行结果</returns>
        public abstract Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context);

        #endregion

        #region Shared AI Call Methods

        /// <summary>
        /// 调用 AI 进行简短回答（用于分类、路由判断等）。
        /// 公开给 AgentDispatcher 使用。
        /// </summary>
        public async Task<string> CallAiShortAsync(string systemPrompt, string userPrompt, CancellationToken ct, int maxTokens = 512)
        {
            var messages = new List<ChatApiMessage>
            {
                new ChatApiMessage { Role = "system", Content = systemPrompt },
                new ChatApiMessage { Role = "user", Content = userPrompt }
            };

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (!chunk.StartsWith("[THINKING]") && !chunk.StartsWith("[TOOL_CALL]"))
                    sb.Append(chunk);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 调用 AI 进行长回答（用于代码生成、分析等）。
        /// </summary>
        protected async Task<string> CallAiLongAsync(string systemPrompt, string userPrompt, CancellationToken ct, int maxTokens = 4096)
        {
            var messages = new List<ChatApiMessage>
            {
                new ChatApiMessage { Role = "system", Content = systemPrompt },
                new ChatApiMessage { Role = "user", Content = userPrompt }
            };

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (!chunk.StartsWith("[THINKING]") && !chunk.StartsWith("[TOOL_CALL]"))
                    sb.Append(chunk);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 带对话历史的 AI 调用。
        /// </summary>
        protected async Task<string> CallAiWithHistoryAsync(List<ChatApiMessage> history, CancellationToken ct, int maxTokens = 4096)
        {
            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(history, null, ct))
            {
                if (!chunk.StartsWith("[THINKING]") && !chunk.StartsWith("[TOOL_CALL]"))
                    sb.Append(chunk);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 使用 ConversationContextManager 构建的消息列表调用 AI。
        /// 正确处理 reasoning_content 回传规则。
        /// </summary>
        protected async Task<string> CallAiWithContextAsync(ConversationContextManager ctxManager, CancellationToken ct, int maxTokens = 4096)
        {
            var messages = ctxManager.BuildApiMessages();
            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (!chunk.StartsWith("[THINKING]") && !chunk.StartsWith("[TOOL_CALL]"))
                    sb.Append(chunk);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 带工具调用的 AI 对话循环（支持多轮工具调用）。
        /// 
        /// 这是 Agent 执行工具增强型任务的核心方法。
        /// 与主聊天流程 (DeepSeekChatControl.Messaging.cs) 中的工具调用循环一致。
        /// </summary>
        /// <param name="messages">消息列表（system + 历史 + user）</param>
        /// <param name="workspaceRoot">工作区根目录，用于内置工具（如 file_search, list_dir）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="maxTokens">最大 token 数</param>
        /// <param name="maxToolRounds">【已废弃】使用智能循环检测替代。保留参数兼容性，但不再使用。</param>
        /// <param name="onThinking">思考内容回调（用于 UI 实时更新）</param>
        /// <param name="onContent">内容回调（用于 UI 实时更新）</param>
        /// <param name="onToolCall">工具调用回调（用于 UI 通知）</param>
        /// <returns>AI 最终生成的文本内容</returns>
        protected async Task<string> CallAiWithToolLoopAsync(
            List<ChatApiMessage> messages,
            string? workspaceRoot,
            CancellationToken ct,
            int maxTokens = 4096,
            int maxToolRounds = 30,
            Action<string>? onThinking = null,
            Action<string>? onContent = null,
            Action<string>? onToolCall = null)
        {
            var reasoningBuilder = new StringBuilder();
            var contentBuilder = new StringBuilder();
            var toolCallAccumulator = new Dictionary<int, Models.ToolCallAccumulator>();

            // ── 循环检测状态 ──
            var callSignatureHistory = new List<string>();
            int consecutiveErrorRounds = 0;
            const int maxRepeatedSameCall = 3;
            const int maxConsecutiveErrors = 5;
            const int safetyLimit = 200;
            bool loopDetected = false;

            int round = 0;
            while (!loopDetected)
            {
                round++;
                if (round > safetyLimit)
                {
                    Logger.Warn($"[Agent:{Definition.Name}] 达到安全上限 {safetyLimit} 轮，强制结束");
                    contentBuilder.Append("\n\n> ⚠️ 工具调用已达安全上限，分析可能不完整。");
                    break;
                }

                toolCallAccumulator.Clear();
                reasoningBuilder.Clear();
                contentBuilder.Clear();

                // ── 获取工具定义 ──
                List<ToolDefinition>? toolDefs = null;
                if (BuiltInTools != null || McpManager != null)
                {
                    toolDefs = new List<ToolDefinition>();

                    // 内置工具
                    if (BuiltInTools != null)
                    {
                        var builtInDefs = BuiltInTools.GetFilteredToolDefinitions(Definition.AllowedTools);
                        toolDefs.AddRange(builtInDefs);
                    }

                    // MCP 外部工具
                    if (McpManager != null && McpManager.AllTools.Count > 0)
                    {
                        var mcpDefs = McpManager.GetFilteredToolDefinitions(Definition.AllowedTools);
                        toolDefs.AddRange(mcpDefs);
                    }

                    Logger.Info($"[Agent:{Definition.Name}] 本轮携带 {toolDefs.Count} 个工具定义");
                }

                // ── 流式调用 AI ──
                await foreach (var chunk in _apiService.ChatStreamAsync(messages, toolDefs, ct))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuilder.Append(thinking);
                        onThinking?.Invoke(thinking);
                    }
                    else if (chunk.StartsWith("[TOOL_CALL]"))
                    {
                        var tcJson = chunk.Substring(11);
                        try
                        {
                            var deltas = JsonSerializer.Deserialize<List<ToolCallDelta>>(tcJson);
                            if (deltas != null)
                            {
                                foreach (var delta in deltas)
                                {
                                    if (!toolCallAccumulator.ContainsKey(delta.Index))
                                        toolCallAccumulator[delta.Index] = new Models.ToolCallAccumulator();
                                    var acc = toolCallAccumulator[delta.Index];
                                    if (!string.IsNullOrEmpty(delta.Id)) acc.Id = delta.Id!;
                                    if (!string.IsNullOrEmpty(delta.Type)) acc.Type = delta.Type;
                                    if (delta.Function != null)
                                    {
                                        if (!string.IsNullOrEmpty(delta.Function.Name)) acc.FunctionName = delta.Function.Name;
                                        if (!string.IsNullOrEmpty(delta.Function.Arguments)) acc.ArgumentsBuilder.Append(delta.Function.Arguments);
                                    }
                                }
                            }
                        }
                        catch (JsonException) { }

                        var toolNames = toolCallAccumulator.Values
                            .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                            .Select(a => a.FunctionName!);
                        string toolSummary = string.Join(", ", toolNames);
                        onToolCall?.Invoke(toolSummary);
                    }
                    else
                    {
                        contentBuilder.Append(chunk);
                        onContent?.Invoke(chunk);
                    }
                }

                // ── 处理工具调用 ──
                if (toolCallAccumulator.Count > 0)
                {
                    var toolCalls = toolCallAccumulator.Values
                        .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                        .Select(a => new ToolCall
                        {
                            Id = a.Id,
                            Type = a.Type ?? "function",
                            Function = new ToolCallFunction
                            {
                                Name = a.FunctionName!,
                                Arguments = a.ArgumentsBuilder.ToString()
                            }
                        }).ToList();

                    if (toolCalls.Count == 0) break;

                    Logger.Info($"[Agent:{Definition.Name}] 检测到 {toolCalls.Count} 个工具调用: {string.Join(", ", toolCalls.Select(t => t.Function.Name))}");

                    // ── 添加 assistant 消息（含工具调用）──
                    messages.Add(new ChatApiMessage
                    {
                        Role = "assistant",
                        Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
                        ReasoningContent = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
                        ToolCalls = toolCalls
                    });

                    // ── 执行每个工具并添加 tool 结果消息 ──
                    foreach (var tc in toolCalls)
                    {
                        string toolResult;
                        try
                        {
                            toolResult = await ExecuteToolAsync(tc.Function.Name, tc.Function.Arguments, workspaceRoot, ct);
                        }
                        catch (Exception ex)
                        {
                            toolResult = $"❌ 工具执行异常: {ex.Message}";
                            Logger.Error($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 执行异常: {ex.Message}", ex);
                        }

                        messages.Add(new ChatApiMessage
                        {
                            Role = "tool",
                            Content = toolResult,
                            ToolCallId = tc.Id,
                            Name = tc.Function.Name
                        });

                        Logger.Info($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 返回: {(toolResult.Length > 200 ? toolResult.Substring(0, 200) + "..." : toolResult)}");
                    }

                    // ── 循环检测 ──
                    // 收集本轮签名
                    var roundSignatures = new List<string>();
                    foreach (var tc in toolCalls)
                    {
                        string sig = tc.Function.Name + "|" +
                            (tc.Function.Arguments.Length > 200
                                ? tc.Function.Arguments.Substring(0, 200)
                                : tc.Function.Arguments);
                        callSignatureHistory.Add(sig);
                        roundSignatures.Add(sig);
                    }

                    // 检测同一调用重复
                    foreach (var sig in roundSignatures)
                    {
                        int repeatCount = callSignatureHistory.Count(s => s == sig);
                        if (repeatCount >= maxRepeatedSameCall)
                        {
                            loopDetected = true;
                            string toolName = sig.Split('|')[0];
                            Logger.Warn($"[Agent:{Definition.Name}] 🔄 检测到循环调用: {toolName} 已重复 {repeatCount} 次");
                            contentBuilder.Append($"\n\n> ⚠️ 检测到 `{toolName}` 重复调用 {repeatCount} 次，已自动终止循环。");
                            break;
                        }
                    }

                    // 保留最近 30 条签名
                    while (callSignatureHistory.Count > 30)
                        callSignatureHistory.RemoveAt(0);

                    // 检测连续错误：检查本轮 tool 消息是否全部以 ❌ 开头
                    if (!loopDetected)
                    {
                        int toolMsgStart = messages.Count - toolCalls.Count;
                        bool allErrors = toolCalls.Count > 0;
                        for (int i = toolMsgStart; i < messages.Count && allErrors; i++)
                        {
                            if (messages[i].Role == "tool" && !(messages[i].Content ?? "").StartsWith("❌"))
                                allErrors = false;
                        }

                        if (allErrors)
                            consecutiveErrorRounds++;
                        else
                            consecutiveErrorRounds = 0;

                        if (consecutiveErrorRounds >= maxConsecutiveErrors)
                        {
                            loopDetected = true;
                            Logger.Warn($"[Agent:{Definition.Name}] 🔄 连续 {consecutiveErrorRounds} 轮工具调用全部返回错误，强制结束");
                            contentBuilder.Append($"\n\n> ⚠️ 连续 {consecutiveErrorRounds} 轮工具调用均失败，已自动终止。");
                        }
                    }

                    // ── 继续下一轮 ──
                    continue;
                }

                // ── 无工具调用，结束循环 ──
                break;
            }

            return contentBuilder.ToString().Trim();
        }

        /// <summary>
        /// 执行单个工具调用（优先内置工具，其次 MCP 工具）。
        /// </summary>
        private async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, string? workspaceRoot, CancellationToken ct)
        {
            // ── 1. 内置工具 ──
            if (BuiltInTools != null && BuiltInToolService.IsBuiltInTool(toolName))
            {
                string? result = await BuiltInTools.ExecuteBuiltInToolAsync(toolName, argumentsJson, workspaceRoot);
                if (result != null)
                    return result;
            }

            // ── 2. MCP 工具 ──
            if (McpManager != null)
            {
                try
                {
                    return await McpManager.CallToolAsync(toolName, argumentsJson, ct);
                }
                catch (Exception ex)
                {
                    return $"❌ MCP 工具调用失败 ({toolName}): {ex.Message}";
                }
            }

            return $"❌ 未知工具: {toolName}";
        }

        #endregion

        #region Shared Utility Methods

        /// <summary>
        /// 从 AI 返回结果中提取 JSON（可能被 markdown 代码块包裹）。
        /// </summary>
        protected static string ExtractJsonFromMarkdown(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            int jsonStart = text.IndexOf('{');
            int jsonEnd = text.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                return text.Substring(jsonStart, jsonEnd - jsonStart + 1);

            return text.Trim();
        }

        /// <summary>
        /// 从 AI 返回结果中解析文件变更（```file: 格式）。
        /// </summary>
        protected static List<FileChangeSummary> ParseCodeChangesFromResult(string aiResult)
        {
            var changes = new List<FileChangeSummary>();
            if (string.IsNullOrWhiteSpace(aiResult)) return changes;

            var regex = new System.Text.RegularExpressions.Regex(
                @"```file:\s*(?<path>[^\r\n]+)[\r\n]+(?<content>.*?)```",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var matches = regex.Matches(aiResult);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string filePath = match.Groups["path"].Value.Trim();
                string newContent = match.Groups["content"].Value;
                if (string.IsNullOrWhiteSpace(filePath)) continue;

                int newLines = CountLines(newContent);
                int linesAdded = newLines;
                int linesRemoved = 0;

                if (System.IO.File.Exists(filePath))
                {
                    try
                    {
                        string oldContent = System.IO.File.ReadAllText(filePath);
                        int oldLines = CountLines(oldContent);
                        linesAdded = Math.Max(0, newLines - oldLines);
                        linesRemoved = Math.Max(0, oldLines - newLines);
                    }
                    catch { }
                }

                changes.Add(new FileChangeSummary
                {
                    FilePath = filePath,
                    NewContent = newContent,
                    LinesAdded = linesAdded,
                    LinesRemoved = linesRemoved,
                    BriefDescription = System.IO.Path.GetFileName(filePath)
                        + (System.IO.File.Exists(filePath) ? " (修改)" : " (新建)"),
                });
            }

            return changes;
        }

        /// <summary>
        /// 从 AI 返回结果中解析待删除的文件（delete: 格式）。
        /// 支持格式:
        ///   delete: path/to/file.cs
        ///   delete_file: path/to/file.cs
        ///   ```delete: path/to/file.cs```
        /// </summary>
        protected static List<string> ParseFileDeletionsFromResult(string aiResult)
        {
            var deletions = new List<string>();
            if (string.IsNullOrWhiteSpace(aiResult)) return deletions;

            // 严格匹配：仅匹配行首的 delete: 或 delete_file: 格式
            // 要求路径包含文件扩展名（如 .cs/.cpp），排除代码块内的 delete 关键字误匹配
            var regex = new System.Text.RegularExpressions.Regex(
                @"(?<=^|\n)\s*(?:delete|delete_file)\s*:\s*(?<path>[^\r\n`]+?\.[a-zA-Z0-9]+)\b",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var matches = regex.Matches(aiResult);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string filePath = match.Groups["path"].Value.Trim();
                // 过滤：路径不能以 ``` 结尾（排除代码块标记）
                if (string.IsNullOrWhiteSpace(filePath) || filePath.EndsWith("`"))
                    continue;

                // 过滤：路径不能在代码块内部
                int matchPos = match.Index;
                string textBefore = aiResult.Substring(0, Math.Min(matchPos, aiResult.Length));
                int openFences = CountSubstring(textBefore, "```");
                if (openFences % 2 != 0)
                    continue;

                deletions.Add(filePath);
            }

            return deletions;
        }

        /// <summary>
        /// 统计子串出现次数。
        /// </summary>
        private static int CountSubstring(string text, string substring)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(substring, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += substring.Length;
            }
            return count;
        }

        /// <summary>
        /// 计算文本行数。
        /// </summary>
        protected static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') count++;
            return count;
        }

        /// <summary>
        /// 解析 AI 返回的文件路径（支持相对路径和 Unix 风格路径）。
        /// </summary>
        protected static string ResolveFilePath(string filePath, string? solutionPath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;
            if (System.IO.Path.IsPathRooted(filePath) && System.IO.File.Exists(System.IO.Path.GetDirectoryName(filePath)))
                return filePath;

            if (System.IO.Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(solutionPath))
            {
                string relativePart = filePath.TrimStart('/').Replace('/', '\\');
                string candidate = System.IO.Path.Combine(solutionPath, relativePart);
                if (System.IO.File.Exists(candidate))
                {
                    Logger.Info($"[PathResolve] AI路径 {filePath} → {candidate}");
                    return candidate;
                }

                string fileName = System.IO.Path.GetFileName(filePath);
                if (!string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        var matches = System.IO.Directory.GetFiles(solutionPath, fileName,
                            System.IO.SearchOption.AllDirectories);
                        if (matches.Length > 0)
                        {
                            Logger.Info($"[PathResolve] AI路径 {filePath} → {matches[0]} (方案目录搜索)");
                            return matches[0];
                        }
                    }
                    catch { }
                }

                string fallbackDir = System.IO.Path.GetDirectoryName(candidate) ?? solutionPath ?? string.Empty;
                Logger.Warn($"[PathResolve] AI路径无法匹配已有文件，将创建: {candidate}");
                return candidate;
            }
            else if (!string.IsNullOrEmpty(solutionPath))
            {
                string candidate = System.IO.Path.Combine(solutionPath, filePath.Replace('/', '\\'));
                Logger.Info($"[PathResolve] 相对路径 {filePath} → {candidate}");
                return candidate;
            }

            return filePath;
        }

        #endregion

        #region Permission

        /// <summary>
        /// 请求用户许可执行某个操作。
        /// </summary>
        public async Task<bool> RequestPermissionAsync(string title, string command, string actionType = "command")
        {
            var request = new AgentPermissionRequest
            {
                Title = title,
                Command = command,
                ActionType = actionType,
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            PendingPermission = request;
            PermissionRequested?.Invoke(request);
            AddLog("INFO", $"等待用户许可: {title}");

            bool approved = await request.ResponseTcs.Task;
            PendingPermission = null;
            AddLog("INFO", $"权限请求结果: {(approved ? "✅ 允许" : "❌ 拒绝")} → {title}");
            return approved;
        }

        /// <summary>
        /// 响应权限请求。
        /// </summary>
        public void RespondToPermission(string requestId, bool approved)
        {
            if (PendingPermission?.RequestId == requestId)
                PendingPermission.ResponseTcs?.TrySetResult(approved);
        }

        /// <summary>
        /// 请求用户确认文件删除操作。
        /// 会中断当前执行流，在 WebView 中渲染确认按钮，等待用户响应。
        /// </summary>
        /// <param name="filePaths">待删除的文件绝对路径列表</param>
        /// <param name="reason">删除原因说明</param>
        /// <returns>true 表示用户确认删除，false 表示取消</returns>
        public async Task<bool> RequestFileDeleteConfirmationAsync(List<string> filePaths, string reason = "")
        {
            if (filePaths == null || filePaths.Count == 0)
                return false;

            var fileNames = filePaths.Select(p => System.IO.Path.GetFileName(p)).ToList();
            string title = filePaths.Count == 1
                ? $"删除文件: {fileNames[0]}"
                : $"删除 {filePaths.Count} 个文件";
            string command = !string.IsNullOrEmpty(reason) ? reason : string.Join("\n", filePaths);

            var request = new AgentPermissionRequest
            {
                Title = title,
                Command = command,
                ActionType = "file_delete",
                FilePaths = new List<string>(filePaths),
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            PendingPermission = request;
            PermissionRequested?.Invoke(request);
            AddLog("INFO", $"等待用户确认删除: {title}");

            bool approved = await request.ResponseTcs.Task;
            PendingPermission = null;
            AddLog("INFO", $"文件删除确认结果: {(approved ? "✅ 确认删除" : "❌ 取消")} → {title}");
            return approved;
        }

        /// <summary>
        /// 通知文件变更（用于实时推送到 WebView）。
        /// </summary>
        /// <param name="planId">关联的计划 ID</param>
        /// <param name="changeType">变更类型: modify, create, delete</param>
        /// <param name="filePath">文件绝对路径</param>
        /// <param name="detail">变更详情</param>
        protected void NotifyFileChange(string planId, string changeType, string filePath, string detail)
        {
            try
            {
                FileChangeNotified?.Invoke(new AgentFileChangeEventArgs
                {
                    PlanId = planId,
                    ChangeType = changeType,
                    FilePath = filePath,
                    Detail = detail,
                });
            }
            catch { }
        }

        #endregion

        #region Event Helpers for Derived Classes

        /// <summary>
        /// 供派生类触发 LogEntryAdded 事件（不写日志文件，仅转发到订阅者）。
        /// </summary>
        protected void RaiseLogEntryAdded(AgentLogEntry entry)
        {
            try { LogEntryAdded?.Invoke(entry); } catch { }
        }

        #endregion

        #region Logging

        protected void AddLog(string level, string message)
        {
            var entry = new AgentLogEntry { Level = level, Message = message };
            _logs.Add(entry);
            try { LogEntryAdded?.Invoke(entry); } catch { }

            if (level == "ERROR") Logger.Error($"[{Definition.Name}] {message}");
            else if (level == "WARN") Logger.Warn($"[{Definition.Name}] {message}");
            else Logger.Info($"[{Definition.Name}] {message}");
        }

        public IReadOnlyList<AgentLogEntry> GetLogs() => _logs.AsReadOnly();

        #endregion

        #region IDisposable

        public virtual void Dispose()
        {
            _logs.Clear();
        }

        #endregion
    }
}
