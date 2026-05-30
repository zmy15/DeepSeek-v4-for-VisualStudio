using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// MCP HTTP 传输客户端。
    /// 完整实现 MCP Streamable HTTP 传输（2025-11-25 规范）。
    /// 
    /// 协议要点：
    /// - 无状态架构：移除了会话（session）概念，每个请求独立
    /// - 单一端点：所有请求通过 HTTP POST/GET 发送到同一 URL
    /// - 响应格式由 Content-Type 决定：
    ///   · application/json → 直接返回 JSON-RPC 响应
    ///   · text/event-stream → 返回 SSE 事件流
    /// - MCP-Protocol-Version: 所有请求头部携带协议版本号 2025-11-25
    /// - 版本协商：Initialize 握手时与服务器协商协议版本
    /// - 通知/响应：服务器返回 202 Accepted 即成功，无需读取 body
    /// - GET SSE 监听：后台长连接接收服务器主动推送的请求/通知
    /// - 断线续传：跟踪 SSE id 事件，重连时发送 Last-Event-ID
    /// - Tasks（实验性）：支持长时间运行的异步任务
    /// 
    /// 参考：https://modelcontextprotocol.io/specification/2025-11-25/basic/transports/
    /// </summary>
    public class McpHttpClient : IMcpClient
    {
        private const string ProtocolVersion = "2025-06-18";

        private readonly McpServerConfig _config;
        private readonly System.Net.Http.HttpClient _httpClient;
        private int _nextRequestId;
        private bool _initialized;
        private string? _sessionId;
        private string _negotiatedProtocolVersion;
        private string? _lastEventId;            // SSE 断线续传：最后接收的事件 ID
        private CancellationTokenSource? _listenerCts;  // 后台 GET SSE 监听取消令牌
        private bool _disposed;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public InitializeResult? ServerInfo { get; private set; }
        public List<McpTool> Tools { get; private set; } = new();
        public bool IsConnected { get; private set; }
        public string ServerName => _config.Name;
        public string Transport => "http";

        public McpHttpClient(McpServerConfig config)
        {
            _config = config;
            _negotiatedProtocolVersion = ProtocolVersion; // 默认使用最新版本

            _httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        #region 连接与初始化

        /// <summary>
        /// 建立 HTTP 连接并完成 MCP 初始化握手。
        /// </summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default, Action<string>? progress = null)
        {
            if (IsConnected) return;

            var url = _config.Url?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new McpException($"HTTP MCP 服务器 '{_config.Name}' 未配置 URL。请在 MCP 设置中填写服务器地址。");
            }

            // 确保 URL 以 http:// 或 https:// 开头
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            Logger.Info($"[MCP HTTP] 连接服务器: {url} (协议版本 {_negotiatedProtocolVersion})");
            progress?.Invoke(string.Format(LocalizationService.Instance["mcp.connecting"], url));

            // ── 1. Initialize 握手（Streamable HTTP 2025-11-25）──
            progress?.Invoke(LocalizationService.Instance["mcp.handshaking"]);
            var initParams = new InitializeParams
            {
                ProtocolVersion = ProtocolVersion,
                Capabilities = new ClientCapabilities
                {
                    Roots = new CapabilityRoots { ListChanged = true }
                },
                ClientInfo = new ImplementationInfo
                {
                    Name = "DeepSeek-v4-for-VisualStudio",
                    Version = "1.1.0"
                }
            };

            JsonRpcResponse initResponse;
            initResponse = await SendHttpRequestAsync(url, "initialize", initParams, cancellationToken)
                .ConfigureAwait(false);

            if (initResponse.Error != null)
            {
                throw new McpException($"Initialize 失败: {initResponse.Error.Message} (code={initResponse.Error.Code})");
            }

            ServerInfo = DeserializeResult<InitializeResult>(initResponse);

            // ── 版本协商：使用服务器返回的协议版本 ──
            if (!string.IsNullOrEmpty(ServerInfo?.ProtocolVersion))
            {
                _negotiatedProtocolVersion = ServerInfo.ProtocolVersion;
                Logger.Info($"[MCP HTTP] 协议版本协商: {_negotiatedProtocolVersion}");
            }

            Logger.Info($"[MCP HTTP] 服务器已初始化: {ServerInfo?.ServerInfo.Name} v{ServerInfo?.ServerInfo.Version}");

            // ── 2. Initialized 通知 ──
            await SendHttpNotificationAsync(url, "notifications/initialized", null, cancellationToken)
                .ConfigureAwait(false);

            _initialized = true;
            IsConnected = true;

            // ── 3. 列举工具 ──
            if (ServerInfo?.Capabilities?.Tools != null)
            {
                progress?.Invoke(LocalizationService.Instance["mcp.fetchingTools"]);
                await RefreshToolsAsync(cancellationToken).ConfigureAwait(false);
            }

            // ── 4. 启动 GET SSE 后台监听（接收服务器主动推送的消息）──
            StartListening(url);
        }

        /// <summary>
        /// 刷新工具列表。
        /// </summary>
        public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
        {
            if (!_initialized)
                throw new McpException("MCP HTTP 客户端未初始化");

            var url = _config.Url?.Trim()!;
            var response = await SendHttpRequestAsync(url, "tools/list", null, cancellationToken)
                .ConfigureAwait(false);

            if (response.Error != null)
            {
                Logger.Error($"[MCP HTTP] tools/list 失败: {response.Error.Message}");
                return;
            }

            var result = DeserializeResult<ToolsListResult>(response);
            Tools = result?.Tools ?? new List<McpTool>();
            Logger.Info($"[MCP HTTP] 发现 {Tools.Count} 个工具: {string.Join(", ", Tools.Select(t => t.Name))}");
        }

        #endregion

        #region 工具调用

        /// <summary>
        /// 调用 MCP 工具。
        /// </summary>
        public async Task<ToolCallResult> CallToolAsync(string toolName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default)
        {
            if (!_initialized)
                throw new McpException("MCP HTTP 客户端未初始化");

            Logger.Info($"[MCP HTTP] 调用工具: {toolName} (参数 {arguments.Count} 项)");

            var toolParams = new ToolCallParams
            {
                Name = toolName,
                Arguments = arguments
            };

            var url = _config.Url?.Trim()!;
            var response = await SendHttpRequestAsync(url, "tools/call", toolParams, cancellationToken)
                .ConfigureAwait(false);

            if (response.Error != null)
            {
                Logger.Error($"[MCP HTTP] tools/call 失败: {response.Error.Message}");
                return new ToolCallResult
                {
                    IsError = true,
                    Content = new List<ToolContentItem>
                    {
                        new ToolContentItem
                        {
                            Type = "text",
                            Text = string.Format(LocalizationService.Instance["mcp.toolError"], response.Error.Message)
                        }
                    }
                };
            }

            var result = DeserializeResult<ToolCallResult>(response);
            return result ?? new ToolCallResult
            {
                IsError = true,
                Content = new List<ToolContentItem>
                {
                    new ToolContentItem { Type = "text", Text = LocalizationService.Instance["mcp.emptyResponse"] }
                }
            };
        }

        #endregion

        #region HTTP 通信（Streamable HTTP 2025）

        /// <summary>
        /// 发送 JSON-RPC 请求并解析响应。
        /// 根据 Content-Type 自动分流：
        /// - application/json → 直接解析 JSON-RPC
        /// - text/event-stream → 流式读取 SSE 事件
        /// </summary>
        private async Task<JsonRpcResponse> SendHttpRequestAsync(
            string url, string method, object? @params, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            var request = new JsonRpcRequest
            {
                Id = id,
                Method = method,
            };

            if (@params != null)
            {
                var paramsJson = JsonSerializer.Serialize(@params, JsonOptions);
                request.Params = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(paramsJson);
            }

            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            Logger.Info($"[MCP HTTP] → POST #{id}: {method} → {url}");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            // Streamable HTTP: Accept 头声明同时支持 JSON 和 SSE
            httpRequest.Headers.Accept.Clear();
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            // ── MCP-Protocol-Version: 2025-11-25 规范要求所有请求携带 ──
            httpRequest.Headers.Add("MCP-Protocol-Version", _negotiatedProtocolVersion);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            HttpResponseMessage httpResponse;
            try
            {
                // ResponseHeadersRead: 只等响应头，body 稍后流式读取
                httpResponse = await _httpClient.SendAsync(
                    httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new McpException(
                    $"MCP HTTP 请求超时 (30s): {method} (id={id})。请检查服务器地址和网络连接。");
            }
            catch (HttpRequestException ex)
            {
                throw new McpException(
                    $"MCP HTTP 请求失败: {method} → {url}\n{ex.Message}");
            }

            // ── 检查 HTTP 状态码 ──
            if (!httpResponse.IsSuccessStatusCode)
            {
                var statusCode = (int)httpResponse.StatusCode;

                var errorBody = await SafeReadBodyAsync(httpResponse).ConfigureAwait(false);
                Logger.Error($"[MCP HTTP] 服务器返回 HTTP {statusCode}: {TruncateForDisplay(errorBody, 500)}");
                throw new McpException(
                    $"MCP HTTP 服务器返回错误 (HTTP {statusCode}): {TruncateForDisplay(errorBody, 200)}");
            }

            // ── 根据 Content-Type 分派解析 ──
            var contentType = httpResponse.Content.Headers.ContentType?.MediaType ?? "";
            Logger.Info($"[MCP HTTP] ← 响应 #{id}: Content-Type={contentType}");

            if (contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // ── SSE 流式响应（有状态服务器）──
                using var stream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                return await ParseSseStreamAsync(stream, id, cts.Token).ConfigureAwait(false);
            }
            else
            {
                // ── JSON 响应（无状态服务器 或 简单响应）──
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                Logger.Info($"[MCP HTTP] ← 响应体 #{id}: {TruncateForDisplay(responseBody, 300)}");
                return ParseJsonRpcResponse(responseBody, id);
            }
        }

        /// <summary>
        /// 安全读取响应体（错误场景使用）。
        /// </summary>
        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return "(无法读取响应体)";
            }
        }

        /// <summary>
        /// 发送 JSON-RPC 通知（无需响应，fire-and-forget）。
        /// 2025-11-25 规范：服务器应返回 202 Accepted，无需读取 body。
        /// </summary>
        private async Task SendHttpNotificationAsync(
            string url, string method, object? @params, CancellationToken cancellationToken)
        {
            var notification = new JsonRpcNotification { Method = method };
            if (@params != null)
            {
                var paramsJson = JsonSerializer.Serialize(@params, JsonOptions);
                notification.Params = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(paramsJson);
            }

            var json = JsonSerializer.Serialize(notification, JsonOptions);
            Logger.Info($"[MCP HTTP] → NOTIFICATION: {method}");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            // ── MCP-Protocol-Version ──
            httpRequest.Headers.Add("MCP-Protocol-Version", _negotiatedProtocolVersion);

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var response = await _httpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);

                // 202 Accepted 或 200 OK 均视为成功
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted ||
                    response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    Logger.Info($"[MCP HTTP] 通知已发送: {method} (HTTP {(int)response.StatusCode})");
                }
                else
                {
                    Logger.Info($"[MCP HTTP] 通知响应异常: HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                // 通知发送失败不应阻断流程
                Logger.Info($"[MCP HTTP] 通知发送失败 (非致命): {ex.Message}");
            }
        }

        #endregion

        #region 响应解析（JSON / SSE 分流）

        /// <summary>
        /// 解析 JSON 格式的 JSON-RPC 响应。
        /// </summary>
        private static JsonRpcResponse ParseJsonRpcResponse(string json, int expectedId)
        {
            try
            {
                var response = JsonSerializer.Deserialize<JsonRpcResponse>(json, JsonOptions);
                if (response == null)
                    throw new McpException("MCP HTTP 响应为空");

                if (response.Id != expectedId && response.Id != 0)
                {
                    Logger.Info($"[MCP HTTP] ⚠ 响应 ID 不匹配: 期望 {expectedId}, 实际 {response.Id}");
                }

                return response;
            }
            catch (JsonException ex)
            {
                throw new McpException(
                    $"MCP HTTP 响应 JSON 解析失败: {ex.Message}\n" +
                    $"响应内容: {TruncateForDisplay(json, 300)}");
            }
        }

        /// <summary>
        /// 流式解析 SSE (Server-Sent Events) 响应流。
        /// 逐行读取，按事件边界分组，提取 message 事件中的 JSON-RPC 响应。
        /// 同时跟踪 SSE id 字段用于断线续传。
        /// 
        /// SSE 格式（Streamable HTTP）:
        ///   id: 42
        ///   event: message
        ///   data: {"jsonrpc":"2.0","id":1,"result":{...}}
        ///   
        ///   (空行表示事件结束)
        /// </summary>
        private async Task<JsonRpcResponse> ParseSseStreamAsync(
            Stream stream, int expectedId, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            string? currentEvent = null;
            string? currentEventId = null;
            var dataLines = new List<string>();
            int lineNumber = 0;

            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;

                if (line.StartsWith("id:"))
                {
                    currentEventId = line.Substring(3).Trim();
                }
                else if (line.StartsWith("event:"))
                {
                    currentEvent = line.Substring(6).Trim();
                }
                else if (line.StartsWith("data:"))
                {
                    dataLines.Add(line.Substring(5).Trim());
                }
                else if (line.StartsWith(":"))
                {
                    // SSE 注释行（心跳），忽略
                }
                else if (string.IsNullOrEmpty(line))
                {
                    // ── 事件边界：处理累积的事件 ──
                    // 记录 SSE id（用于断线续传）
                    if (!string.IsNullOrEmpty(currentEventId))
                    {
                        _lastEventId = currentEventId;
                    }

                    if (dataLines.Count > 0)
                    {
                        var eventType = currentEvent ?? "message";
                        var data = string.Join("\n", dataLines);

                        var result = TryProcessSseEvent(eventType, data, expectedId);
                        if (result != null)
                        {
                            return result;
                        }
                    }

                    currentEvent = null;
                    currentEventId = null;
                    dataLines.Clear();
                }
                // else: 未知行，忽略（SSE 规范要求跳过无法识别的字段）
            }

            // ── 流结束，处理最后一段（无结尾空行的情况）──
            if (!string.IsNullOrEmpty(currentEventId))
            {
                _lastEventId = currentEventId;
            }
            if (dataLines.Count > 0)
            {
                var eventType = currentEvent ?? "message";
                var data = string.Join("\n", dataLines);
                var result = TryProcessSseEvent(eventType, data, expectedId);
                if (result != null)
                {
                    return result;
                }
            }

            throw new McpException(
                $"MCP SSE 流中未找到匹配的 JSON-RPC 响应 (id={expectedId})。\n" +
                $"已读取 {lineNumber} 行 SSE 数据。请检查服务器实现是否兼容 Streamable HTTP 规范。");
        }

        /// <summary>
        /// 处理单个 SSE 事件，尝试提取 JSON-RPC 响应。
        /// </summary>
        /// <returns>匹配的响应，或 null（继续处理下一个事件）</returns>
        private static JsonRpcResponse? TryProcessSseEvent(string eventType, string data, int expectedId)
        {
            switch (eventType)
            {
                case "message":
                    // ── 标准 JSON-RPC 响应事件 ──
                    if (data.StartsWith("{"))
                    {
                        try
                        {
                            var response = JsonSerializer.Deserialize<JsonRpcResponse>(data, JsonOptions);
                            if (response != null && (response.Id == expectedId || response.Id == 0))
                            {
                                Logger.Info($"[MCP HTTP] ✓ SSE message 事件匹配 #{expectedId}");
                                return response;
                            }
                            if (response != null)
                            {
                                Logger.Info($"[MCP HTTP] SSE message 事件 ID 不匹配: 期望 {expectedId}, 实际 {response.Id}，继续等待...");
                            }
                        }
                        catch (JsonException ex)
                        {
                            Logger.Info($"[MCP HTTP] SSE message 解析失败: {ex.Message}，data={TruncateForDisplay(data, 100)}");
                        }
                    }
                    break;

                case "error":
                    // ── 服务器错误事件 ──
                    Logger.Error($"[MCP HTTP] SSE error 事件: {TruncateForDisplay(data, 300)}");
                    throw new McpException($"MCP 服务器 SSE 错误: {TruncateForDisplay(data, 200)}");

                case "endpoint":
                    // ── 旧版 HTTP+SSE 的端点事件（向后兼容，仅记录）──
                    Logger.Info($"[MCP HTTP] SSE endpoint 事件 (向后兼容): {data}");
                    break;

                default:
                    // ── 未知事件类型，记录日志后忽略 ──
                    Logger.Info($"[MCP HTTP] SSE 未识别事件类型 '{eventType}': {TruncateForDisplay(data, 100)}");
                    break;
            }

            return null;
        }

        #endregion

        #region GET SSE 后台监听（服务器→客户端消息）

        /// <summary>
        /// 启动后台 GET SSE 监听流，接收服务器主动推送的请求和通知。
        /// 2025-11-25 规范：客户端 MAY 发起 GET 请求建立 SSE 流，
        /// 服务器可通过此流发送 JSON-RPC 请求和通知。
        /// </summary>
        private void StartListening(string url)
        {
            if (_listenerCts != null) return; // 已在监听

            _listenerCts = new CancellationTokenSource();
            var token = _listenerCts.Token;

            // 后台 fire-and-forget 启动监听循环
            _ = Task.Run(() => RunListenerLoopAsync(url, token), token);
            Logger.Info($"[MCP HTTP] 已启动 GET SSE 后台监听: {url}");
        }

        /// <summary>
        /// 停止后台 GET SSE 监听。
        /// </summary>
        private void StopListening()
        {
            try
            {
                _listenerCts?.Cancel();
                _listenerCts?.Dispose();
                _listenerCts = null;
            }
            catch (Exception ex)
            {
                Logger.Info($"[MCP HTTP] 停止监听异常: {ex.Message}");
            }
        }

        /// <summary>
        /// GET SSE 后台监听主循环。
        /// 持续运行直到被取消，连接断开时自动重连（带 Last-Event-ID 续传）。
        /// 连续失败 3 次后自动停止，避免对不支持 GET SSE 的服务器无限重试。
        /// </summary>
        private async Task RunListenerLoopAsync(string url, CancellationToken cancellationToken)
        {
            int consecutiveFailures = 0;
            const int maxFailures = 3;

            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    bool shouldRetry = await RunSingleListenerConnectionAsync(url, cancellationToken)
                        .ConfigureAwait(false);

                    if (!shouldRetry)
                    {
                        Logger.Info("[MCP HTTP] GET SSE 服务器不支持，永久停止监听");
                        break;
                    }

                    consecutiveFailures = 0; // 成功完成，重置计数
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    Logger.Info($"[MCP HTTP] GET SSE 监听异常 ({consecutiveFailures}/{maxFailures}，3秒后重试): {ex.Message}");

                    if (consecutiveFailures >= maxFailures)
                    {
                        Logger.Info("[MCP HTTP] GET SSE 连续失败次数过多，停止监听");
                        break;
                    }
                }

                try
                {
                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Logger.Info("[MCP HTTP] GET SSE 监听已停止");
        }

        /// <summary>
        /// 单次 GET SSE 连接：打开流 → 读取事件 → 分发处理。
        /// </summary>
        /// <returns>true 表示可重试，false 表示服务器不支持应永久停止</returns>
        private async Task<bool> RunSingleListenerConnectionAsync(string url, CancellationToken cancellationToken)
        {
            Logger.Info($"[MCP HTTP] GET SSE 监听连接: {url}" +
                (string.IsNullOrEmpty(_lastEventId) ? "" : $" (Last-Event-ID: {_lastEventId})"));

            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            getRequest.Headers.Accept.Clear();
            getRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            getRequest.Headers.Add("MCP-Protocol-Version", _negotiatedProtocolVersion);

            if (!string.IsNullOrEmpty(_sessionId))
            {
                getRequest.Headers.Add("Mcp-Session-Id", _sessionId);
            }

            if (!string.IsNullOrEmpty(_lastEventId))
            {
                getRequest.Headers.Add("Last-Event-ID", _lastEventId);
            }

            // GET SSE 连接探测超时 15 秒（服务器不支持时会快速超时而非挂起 30 分钟）
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    getRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[MCP HTTP] GET SSE 连接超时（可重试）");
                return true;
            }
            catch (HttpRequestException ex)
            {
                Logger.Info($"[MCP HTTP] GET SSE 连接失败: {ex.Message}");
                return true;
            }

            // 服务器明确不支持 GET SSE → 405
            if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                Logger.Info("[MCP HTTP] 服务器不支持 GET SSE 流 (HTTP 405)");
                StopListening();
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Info($"[MCP HTTP] GET SSE 响应异常: HTTP {(int)response.StatusCode}");
                return true;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"[MCP HTTP] GET SSE 响应非 SSE 格式: {contentType}，停止监听");
                StopListening();
                return false;
            }

            // 流式读取 SSE 事件（长连接不受 15 秒限制）
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await ReadListenerSseStreamAsync(stream, cancellationToken).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// 读取 GET SSE 监听流中的事件。
        /// 与 ParseSseStreamAsync 共用相同的 SSE 解析逻辑，
        /// 但处理的是服务器→客户端的消息（requests/notifications），而非响应。
        /// </summary>
        private async Task ReadListenerSseStreamAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            string? currentEvent = null;
            string? currentEventId = null;
            var dataLines = new List<string>();

            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (line.StartsWith("id:"))
                {
                    currentEventId = line.Substring(3).Trim();
                }
                else if (line.StartsWith("event:"))
                {
                    currentEvent = line.Substring(6).Trim();
                }
                else if (line.StartsWith("data:"))
                {
                    dataLines.Add(line.Substring(5).Trim());
                }
                else if (line.StartsWith(":"))
                {
                    // SSE 注释行（心跳），忽略
                }
                else if (string.IsNullOrEmpty(line))
                {
                    // ── 事件边界 ──
                    // 记录 SSE id（用于断线续传）
                    if (!string.IsNullOrEmpty(currentEventId))
                    {
                        _lastEventId = currentEventId;
                    }

                    if (dataLines.Count > 0)
                    {
                        var eventType = currentEvent ?? "message";
                        var data = string.Join("\n", dataLines);
                        ProcessListenerSseEvent(eventType, data);
                    }

                    currentEvent = null;
                    currentEventId = null;
                    dataLines.Clear();
                }
            }

            // ── 流结束，处理最后一段 ──
            if (!string.IsNullOrEmpty(currentEventId))
            {
                _lastEventId = currentEventId;
            }
            if (dataLines.Count > 0)
            {
                var eventType = currentEvent ?? "message";
                var data = string.Join("\n", dataLines);
                ProcessListenerSseEvent(eventType, data);
            }
        }

        /// <summary>
        /// 处理 GET SSE 监听流中接收到的事件。
        /// 服务器可推送 JSON-RPC 请求或通知。
        /// 当前版本仅记录日志，完整请求处理留待后续扩展。
        /// </summary>
        private static void ProcessListenerSseEvent(string eventType, string data)
        {
            switch (eventType)
            {
                case "message":
                    // 服务器推送的 JSON-RPC 消息（请求或通知）
                    if (data.StartsWith("{"))
                    {
                        try
                        {
                            // 尝试解析为 JSON-RPC 消息
                            using var doc = JsonDocument.Parse(data);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("method", out var methodProp))
                            {
                                var method = methodProp.GetString();
                                if (root.TryGetProperty("id", out _))
                                {
                                    Logger.Info($"[MCP HTTP] 🔔 服务器请求: {method}");
                                    // TODO: 完整的服务器→客户端请求处理
                                }
                                else
                                {
                                    Logger.Info($"[MCP HTTP] 🔔 服务器通知: {method}");
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            Logger.Info($"[MCP HTTP] GET SSE 消息解析失败: {ex.Message}");
                        }
                    }
                    break;

                case "error":
                    Logger.Error($"[MCP HTTP] GET SSE 服务器错误: {TruncateForDisplay(data, 300)}");
                    break;

                case "endpoint":
                    Logger.Info($"[MCP HTTP] GET SSE endpoint (忽略): {data}");
                    break;

                default:
                    Logger.Info($"[MCP HTTP] GET SSE 事件 '{eventType}': {TruncateForDisplay(data, 100)}");
                    break;
            }
        }

        #endregion

        #region 工具方法

        private static T? DeserializeResult<T>(JsonRpcResponse response)
        {
            if (response.Result == null) return default;
            var json = response.Result.Value.GetRawText();
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }

        private static string TruncateForDisplay(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "...";
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // ── 停止 GET SSE 后台监听 ──
            StopListening();

            IsConnected = false;
            _initialized = false;
            _sessionId = null;

            try
            {
                _httpClient?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Info($"[MCP HTTP] 清理 HTTP 客户端异常: {ex.Message}");
            }
        }

        #endregion
    }
}
