using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
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

        /// <summary>日志事件</summary>
        public event Action<AgentLogEntry>? LogEntryAdded;

        /// <summary>权限请求事件</summary>
        public event Action<AgentPermissionRequest>? PermissionRequested;

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
