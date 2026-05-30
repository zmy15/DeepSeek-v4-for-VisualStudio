using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 单个 MCP 服务器的 stdio 客户端。
    /// 通过启动子进程（如 npx、python、node 等）并与其通过 stdin/stdout 进行 JSON-RPC 2.0 通信。
    /// 
    /// 协议流程：
    /// 1. 启动进程
    /// 2. 发送 Initialize 请求
    /// 3. 发送 Initialized 通知
    /// 4. 发送 tools/list 获取工具列表
    /// 5. 保持连接，随时发送 tools/call
    /// </summary>
    public class McpStdioClient : IMcpClient
    {
        private readonly McpServerConfig _config;
        private Process? _process;
        private int _nextRequestId;
        private bool _initialized;
        private readonly object _lock = new();
        private readonly Dictionary<int, TaskCompletionSource<JsonRpcResponse>> _pendingRequests = new();

        // 服务端信息
        public InitializeResult? ServerInfo { get; private set; }
        public List<McpTool> Tools { get; private set; } = new();
        public bool IsConnected { get; private set; }
        public string ServerName => _config.Name;
        public string Transport => "stdio";

        public McpStdioClient(McpServerConfig config)
        {
            _config = config;
        }

        #region 连接与初始化

        /// <summary>
        /// 启动 MCP 服务器进程并完成初始化握手。
        /// </summary>
        /// <param name="progress">可选的进度回调，用于 UI 报告当前阶段</param>
        public async Task ConnectAsync(CancellationToken cancellationToken = default, Action<string>? progress = null)
        {
            if (IsConnected) return;

            var resolvedArgs = _config.GetResolvedArgs();
            var command = _config.Command;

            Logger.Info($"[MCP] 启动服务器: {command} {resolvedArgs}");
            progress?.Invoke(string.Format(LocalizationService.Instance["mcp.startingProcess"], command));

            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = resolvedArgs,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // ── 修复 PATH ──
            psi.Environment["PATH"] = GetFullEnvironmentPath();

            // 设置 MCP 服务器的业务环境变量
            var envVars = _config.GetResolvedEnvVars();
            foreach (var kvp in envVars)
            {
                if (!string.IsNullOrEmpty(kvp.Key))
                    psi.Environment[kvp.Key] = kvp.Value;
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // ── 尝试启动进程 ──
            try
            {
                _process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                string? found = HardSearchExecutable(command);
                if (found != null)
                {
                    Logger.Info($"[MCP] 硬搜索找到: {command} → {found}");
                    psi.FileName = found;
                    _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _process.Start();
                }
                else
                {
                    throw new McpException(
                        string.Format(LocalizationService.Instance["mcp.commandNotFound"], command));
                }
            }

            // ── 立即检查进程是否已崩溃 ──
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (_process.HasExited)
            {
                string stderr = "";
                try { stderr = _process.StandardError.ReadToEnd(); } catch { }
                int exitCode = _process.ExitCode;
                string errorOutput = string.IsNullOrWhiteSpace(stderr) ? "(no output)" : TruncateForDisplay(stderr, 300);
                throw new McpException(
                    string.Format(LocalizationService.Instance["mcp.processExitedEarly"],
                        command, exitCode, command, resolvedArgs, errorOutput));
            }

            _process.Exited += OnProcessExited;

            // ── 用 OutputDataReceived 事件异步读取 stdout/stderr（.NET Framework 安全模式）──
            _process.OutputDataReceived += OnStdoutReceived;
            _process.ErrorDataReceived += OnStderrReceived;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // 等待进程初始化完成
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);

            // ── 再次检查进程状态 ──
            if (_process.HasExited)
            {
                throw new McpException(
                    string.Format(LocalizationService.Instance["mcp.processExitedDuringInit"],
                        command, _process.ExitCode));
            }

            // ── Initialize 握手 ──
            progress?.Invoke(LocalizationService.Instance["mcp.handshaking"]);
            var initParams = new InitializeParams
            {
                ProtocolVersion = "2025-11-25",
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

            var initResponse = await SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);

            // ── 初始化后检查进程是否还活着 ──
            if (_process.HasExited)
            {
                throw new McpException(
                    string.Format(LocalizationService.Instance["mcp.processExitedDuringHandshake"],
                        _process.ExitCode, command));
            }

            if (initResponse.Error != null)
            {
                throw new McpException($"Initialize 失败: {initResponse.Error.Message} (code={initResponse.Error.Code})");
            }

            ServerInfo = DeserializeResult<InitializeResult>(initResponse);
            Logger.Info($"[MCP] 服务器已初始化: {ServerInfo?.ServerInfo.Name} v{ServerInfo?.ServerInfo.Version}");

            // ── Initialized 通知 ──
            SendNotification("notifications/initialized", null);
            _initialized = true;
            IsConnected = true;

            // ── 列举工具 ──
            if (ServerInfo?.Capabilities?.Tools != null)
            {
                progress?.Invoke(LocalizationService.Instance["mcp.fetchingTools"]);
                await RefreshToolsAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 刷新工具列表
        /// </summary>
        public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
        {
            if (!_initialized || _process == null)
                throw new McpException("MCP 客户端未初始化");

            var response = await SendRequestAsync("tools/list", null, cancellationToken);
            if (response.Error != null)
            {
                Logger.Error($"[MCP] tools/list 失败: {response.Error.Message}");
                return;
            }

            var result = DeserializeResult<ToolsListResult>(response);
            Tools = result?.Tools ?? new List<McpTool>();
            Logger.Info($"[MCP] 发现 {Tools.Count} 个工具: {string.Join(", ", Tools.Select(t => t.Name))}");
        }

        #endregion

        #region 工具调用

        /// <summary>
        /// 调用 MCP 工具
        /// </summary>
        public async Task<ToolCallResult> CallToolAsync(string toolName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default)
        {
            if (!_initialized || _process == null)
                throw new McpException("MCP 客户端未初始化");

            Logger.Info($"[MCP] 调用工具: {toolName} (参数 {arguments.Count} 项)");

            var toolParams = new ToolCallParams
            {
                Name = toolName,
                Arguments = arguments
            };

            var response = await SendRequestAsync("tools/call", toolParams, cancellationToken);

            if (response.Error != null)
            {
                Logger.Error($"[MCP] tools/call 失败: {response.Error.Message}");
                return new ToolCallResult
                {
                    IsError = true,
                    Content = new List<ToolContentItem>
                    {
                        new ToolContentItem { Type = "text", Text = string.Format(LocalizationService.Instance["mcp.toolError"], response.Error.Message) }
                    }
                };
            }

            var result = DeserializeResult<ToolCallResult>(response);
            return result ?? new ToolCallResult { IsError = true, Content = new List<ToolContentItem> { new ToolContentItem { Type = "text", Text = LocalizationService.Instance["mcp.emptyResponse"] } } };
        }

        #endregion

        #region JSON-RPC 通信

        /// <summary>
        /// 发送 JSON-RPC 请求并等待响应（带 15 秒硬超时）。
        /// </summary>
        private async Task<JsonRpcResponse> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            var request = new JsonRpcRequest
            {
                Id = id,
                Method = method
            };

            if (@params != null)
            {
                var paramsJson = JsonSerializer.Serialize(@params);
                request.Params = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(paramsJson);
            }

            var tcs = new TaskCompletionSource<JsonRpcResponse>();
            lock (_lock) { _pendingRequests[id] = tcs; }

            var requestJson = JsonSerializer.Serialize(request);
            Logger.Info($"[MCP] → 发送 #{id}: {method}");
            await SendLineAsync(requestJson);

            // ── 双重超时保护 ──
            using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hardCts.Token);
            linkedCts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

            try
            {
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None));
                if (completed != tcs.Task)
                {
                    lock (_lock) { _pendingRequests.Remove(id); }
                    throw new McpException(
                        $"MCP 请求超时 (15s): {method} (id={id})。\n" +
                        $"{LocalizationService.Instance["mcp.serverProcessStatus"]}: {(_process?.HasExited == true ? string.Format(LocalizationService.Instance["mcp.processStatusExited"], _process.ExitCode) : LocalizationService.Instance["mcp.processStatusRunning"])}");
                }
                return await tcs.Task;
            }
            finally
            {
                lock (_lock) { _pendingRequests.Remove(id); }
            }
        }

        /// <summary>
        /// 发送 JSON-RPC 通知（无 id，无需响应）
        /// </summary>
        private void SendNotification(string method, object? @params)
        {
            var notification = new JsonRpcNotification
            {
                Method = method
            };

            if (@params != null)
            {
                var paramsJson = JsonSerializer.Serialize(@params);
                notification.Params = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(paramsJson);
            }

            var json = JsonSerializer.Serialize(notification);
            _ = SendLineAsync(json);
        }

        /// <summary>
        /// 向进程 stdin 写入一行 JSON
        /// </summary>
        private async Task SendLineAsync(string json)
        {
            if (_process?.StandardInput == null)
                throw new McpException(LocalizationService.Instance["mcp.stdinUnavailable"]);

            await _process.StandardInput.WriteLineAsync(json);
            await _process.StandardInput.FlushAsync();
        }

        #endregion

        #region 进程 stdout/stderr 事件处理（.NET Framework 安全模式）

        /// <summary>
        /// stdout 数据接收事件（由 BeginOutputReadLine 触发，线程池回调）。
        /// 解析 JSON-RPC 响应并匹配到等待中的请求。
        /// 注意：不要在事件处理器中做阻塞操作。
        /// </summary>
        private void OnStdoutReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var line = e.Data;
            Logger.Info($"[MCP stdout] ← {line}");

            try
            {
                var response = JsonSerializer.Deserialize<JsonRpcResponse>(line);
                if (response != null && response.Id > 0)
                {
                    TaskCompletionSource<JsonRpcResponse>? tcs;
                    lock (_lock)
                    {
                        _pendingRequests.TryGetValue(response.Id, out tcs);
                    }
                    if (tcs != null)
                    {
                        Logger.Info($"[MCP] ✓ 匹配响应 #{response.Id}");
                        tcs.TrySetResult(response);
                    }
                    else
                    {
                        Logger.Info($"[MCP] ⚠ 未找到等待者 #{response.Id}, pending: [{string.Join(",", _pendingRequests.Keys)}]");
                    }
                }
                else if (response != null && response.Error != null)
                {
                    Logger.Info($"[MCP] ⚠ 收到错误响应: {response.Error.Message}");
                }
            }
            catch (JsonException ex)
            {
                Logger.Info($"[MCP stdout] 非 JSON (忽略): {line.Substring(0, Math.Min(100, line.Length))}... {ex.Message}");
            }
        }

        /// <summary>
        /// stderr 数据接收事件（日志输出）。
        /// </summary>
        private void OnStderrReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Logger.Info($"[MCP stderr] {e.Data}");
            }
        }

        #endregion

        #region 进程管理

        private void OnProcessExited(object? sender, EventArgs e)
        {
            IsConnected = false;
            _initialized = false;

            // 停止异步读取
            try
            {
                _process!.OutputDataReceived -= OnStdoutReceived;
                _process.ErrorDataReceived -= OnStderrReceived;
                _process.CancelOutputRead();
                _process.CancelErrorRead();
            }
            catch { }

            Logger.Info($"[MCP] 服务器进程已退出: {_config.Name} (exit code: {_process?.ExitCode})");

            // 取消所有等待中的请求
            lock (_lock)
            {
                foreach (var kvp in _pendingRequests)
                {
                    kvp.Value.TrySetException(new McpException("MCP 服务器进程已退出"));
                }
                _pendingRequests.Clear();
            }
        }

        #endregion

        #region 工具方法

        /// <summary>
        /// 获取完整的 PATH 环境变量。
        /// devenv.exe 继承的 PATH 可能缺失用户终端中配置的路径
        /// （如 %USERPROFILE%\.cargo\bin、%APPDATA%\npm 等），
        /// 因此合并 系统PATH + 注册表中的用户PATH + 当前进程PATH，
        /// 注入到子进程环境变量中，让 CreateProcess 自行查找命令。
        /// </summary>
        private static string GetFullEnvironmentPath()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 系统级 PATH
            try
            {
                var sysPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
                if (!string.IsNullOrEmpty(sysPath))
                    foreach (var p in sysPath.Split(';')) paths.Add(p.Trim());
            }
            catch { }

            // 用户级 PATH（从注册表直接读，不受 devenv.exe 继承影响）
            try
            {
                using var userKey = Registry.CurrentUser.OpenSubKey("Environment");
                var userPath = userKey?.GetValue("PATH") as string;
                if (!string.IsNullOrEmpty(userPath))
                    foreach (var p in userPath.Split(';')) paths.Add(p.Trim());
            }
            catch { }

            // 当前进程 PATH（兜底）
            try
            {
                var procPath = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(procPath))
                    foreach (var p in procPath.Split(';')) paths.Add(p.Trim());
            }
            catch { }

            // 展开 %USERPROFILE% 等变量，去重合并
            var merged = string.Join(";", paths.Where(p => !string.IsNullOrEmpty(p)));
            merged = Environment.ExpandEnvironmentVariables(merged);
            return merged;
        }

        /// <summary>
        /// 硬搜索可执行文件路径（PATH 注入失败时的兜底方案）。
        /// 探测用户终端中常见的工具安装目录。
        /// </summary>
        private static string? HardSearchExecutable(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            var searchNames = new List<string> { command };
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                searchNames.Add(command + ".exe");
                searchNames.Add(command + ".cmd");
                searchNames.Add(command + ".bat");
            }

            string userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            string appData = Environment.GetEnvironmentVariable("APPDATA") ?? "";
            string programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? "";
            string programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "";

            var searchDirs = new List<string>();

            // ── 标准安装位置 ──
            AddIfExists(searchDirs, Path.Combine(userProfile, ".cargo", "bin"));           // Rust/uv
            AddIfExists(searchDirs, Path.Combine(localAppData, "Programs", "uv"));         // uv installer
            AddIfExists(searchDirs, Path.Combine(appData, "npm"));                          // npx/node
            AddIfExists(searchDirs, Path.Combine(programFiles, "nodejs"));                  // Node.js MSI
            AddIfExists(searchDirs, Path.Combine(programFilesX86, "nodejs"));

            // ── Python Scripts 目录 ──
            var localPython = Path.Combine(appData, "Python");
            if (Directory.Exists(localPython))
            {
                try
                {
                    foreach (var sub in Directory.GetDirectories(localPython))
                    {
                        AddIfExists(searchDirs, Path.Combine(sub, "Scripts"));
                    }
                }
                catch { }
            }
            var roamingPython = Path.Combine(userProfile, "AppData", "Roaming", "Python");
            if (Directory.Exists(roamingPython))
            {
                try
                {
                    foreach (var sub in Directory.GetDirectories(roamingPython))
                    {
                        AddIfExists(searchDirs, Path.Combine(sub, "Scripts"));
                    }
                }
                catch { }
            }

            // ── pipx 安装 ──
            var pipxDir = Path.Combine(localAppData, "pipx");
            if (Directory.Exists(pipxDir))
            {
                try
                {
                    foreach (var sub in Directory.GetDirectories(pipxDir))
                        searchDirs.Add(sub);
                }
                catch { }
            }

            // ── 常见全局工具目录 ──
            AddIfExists(searchDirs, Path.Combine(userProfile, ".local", "bin"));
            AddIfExists(searchDirs, Path.Combine(userProfile, "scoop", "shims"));
            AddIfExists(searchDirs, Path.Combine(userProfile, "chocolatey", "bin"));

            // 遍历搜索
            foreach (var dir in searchDirs)
            {
                foreach (var name in searchNames)
                {
                    var fullPath = Path.Combine(dir, name);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }

            return null;
        }

        private static void AddIfExists(List<string> list, string path)
        {
            if (Directory.Exists(path))
                list.Add(path);
        }

        private static string TruncateForDisplay(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "...";
        }

        private static T? DeserializeResult<T>(JsonRpcResponse response)
        {
            if (response.Result == null) return default;
            var json = response.Result.Value.GetRawText();
            return JsonSerializer.Deserialize<T>(json);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            IsConnected = false;
            _initialized = false;

            if (_process != null)
            {
                _process.Exited -= OnProcessExited;

                // 停止异步读取
                try
                {
                    _process.OutputDataReceived -= OnStdoutReceived;
                    _process.ErrorDataReceived -= OnStderrReceived;
                    _process.CancelOutputRead();
                    _process.CancelErrorRead();
                }
                catch { }

                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"[MCP] 进程清理异常: {ex.Message}");
                }

                _process.Dispose();
                _process = null;
            }

            lock (_lock)
            {
                foreach (var kvp in _pendingRequests)
                {
                    kvp.Value.TrySetCanceled();
                }
                _pendingRequests.Clear();
            }
        }

        #endregion
    }

    /// <summary>
    /// MCP 协议异常
    /// </summary>
    public class McpException : Exception
    {
        public McpException(string message) : base(message) { }
        public McpException(string message, Exception innerException) : base(message, innerException) { }
    }
}
