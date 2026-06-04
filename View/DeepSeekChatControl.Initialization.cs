using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Events;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 初始化相关方法：API/上下文/搜索/OCR/MCP/Skills 服务初始化。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Private Methods - Initialization

        private void InitializeApiService()
        {
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
                return;

            _apiService?.Dispose();
            _apiService = new DeepSeekApiService(_options.ApiKey, _options.SelectedModel);
            _apiService.ConfigureThinking(_options.IsThinkingEnabled, _options.ReasoningEffort);

            // ── 初始化/重建 Agent 调度器（ApiService 重建时必须同步重建）──
            if (_agentDispatcher != null)
            {
                _agentDispatcher.PermissionRequested -= OnAgentPermissionRequested;
                _agentDispatcher.QuestionsRequested -= OnAgentQuestionsRequested;
                _agentDispatcher.Dispose();
            }

            // ── 创建内置工具服务 ──
            var buildService = new BuildService();
            _memoryService = new MemoryService();
            _builtInToolService = new BuiltInToolService(_mcpManager, _webSearchService, buildService, _memoryService);

            _agentDispatcher = new AgentDispatcher(_apiService, _builtInToolService, _mcpManager);
            _agentDispatcher.ContextManager = _contextManager;
            _agentDispatcher.PermissionRequested += OnAgentPermissionRequested;
            _agentDispatcher.QuestionsRequested += OnAgentQuestionsRequested;
            Logger.Info("Agent 调度器初始化成功（多 Agent 模式：Ask / Plan / Explore / Edit）");

            // 初始化 Agent 模式徽章（默认隐藏 Ask 模式）
            UpdateAgentModeBadge();

            // ── 初始化余额查询定时器（每分钟刷新一次）──
            StartBalanceTimer();

            Logger.Info("API 服务初始化成功");
        }

        /// <summary>
        /// 初始化 RAG 服务和上下文压缩服务。
        /// 在 API 服务就绪后调用。
        /// </summary>
        private void InitializeContextServices()
        {
            if (_options == null) return;

            // ── 初始化上下文压缩服务 ──
            if (_options.EnableAutoCompression)
            {
                var compressionConfig = new CompressionConfig
                {
                    CompressionThreshold = _options.CompressionThreshold / 100.0,
                    PreserveRecentTurns = _options.PreserveRecentTurns,
                    AutoCompressEnabled = _options.EnableAutoCompression,
                };

                // 当 API 服务可用时，使用 LLM 摘要；否则使用本地规则提取
                if (_apiService != null)
                {
                    _compressorService = new ContextCompressorService(
                        async (text, ct) =>
                        {
                            try
                            {
                                var messages = new List<ChatApiMessage>
                                {
                                    new ChatApiMessage { Role = "user", Content = text }
                                };
                                return await _apiService.CompleteAsync(messages, ct);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[ContextCompressor] LLM 摘要失败，回退到本地提取: {ex.Message}");
                                return string.Empty; // 返回空将触发回退
                            }
                        },
                        compressionConfig);
                }
                else
                {
                    _compressorService = new ContextCompressorService(null, compressionConfig);
                }

                _contextManager.SetCompressor(_compressorService);
                _contextManager.TokenBudget = _options.TokenBudget;

                Logger.Info($"[ContextServices] 上下文压缩已启用: " +
                    $"预算={_options.TokenBudget:N0}, 阈值={_options.CompressionThreshold}%, " +
                    $"保留轮次={_options.PreserveRecentTurns}");
            }
            else
            {
                _compressorService = null;
                _contextManager.SetCompressor(null);
                _contextManager.TokenBudget = _options.TokenBudget;
                _contextManager.AutoTrimTurns = _options.PreserveRecentTurns;

                Logger.Info($"[ContextServices] 上下文压缩已禁用，使用旧版截断: " +
                    $"预算={_options.TokenBudget:N0}, 保留轮次={_options.PreserveRecentTurns}");
            }

            // ── 初始化 RAG 服务 ──
            if (_options.EnableRag)
            {
                _ragService = new RagService { IsEnabled = true };
                Logger.Info("[ContextServices] RAG 服务已初始化（等待提供者注册）");
            }
            else
            {
                _ragService?.DeactivateProvider();
                _ragService = null;
            }
        }

        /// <summary>
        /// 初始化联网搜索服务。从选项页读取默认搜索引擎。
        /// </summary>
        private void InitializeWebSearchService()
        {
            _webSearchService?.Dispose();
            _webSearchService = new WebSearchService();

            // 从选项页读取默认搜索引擎，同步到 ComboBox
            string optionsProvider = _options?.SearchProvider ?? "DuckDuckGo";
            string resolvedEngine = optionsProvider switch
            {
                "Baidu" => "Baidu",
                _ => "DuckDuckGo"
            };

            // 同步 ComboBox 选中项
            int idx = resolvedEngine switch
            {
                "Baidu" => 0,
                "DuckDuckGo" => 1,
                _ => 1
            };
            WebSearchEngineComboBox.SelectedIndex = idx;

            // 注意：_webSearchEngine 仍为 "Off"，用户需要点击 🌐 按钮开启
            // 但搜索引擎已预选为选项页中配置的值

            ApplyWebSearchConfig();
            Logger.Info($"联网搜索服务初始化成功 (默认引擎: {resolvedEngine})");
        }

        /// <summary>
        /// 初始化 OCR 服务，从选项页读取用户选择的引擎和模型路径。
        /// 此方法内部捕获所有异常，确保 OCR 初始化失败不会影响聊天核心功能。
        /// </summary>
        private void InitializeOcrService()
        {
            try
            {
                if (_options == null)
                {
                    Logger.Info("[OCR] 跳过初始化: _options 为 null");
                    return;
                }

                Logger.Info($"[OCR] 开始初始化，用户选择引擎: {_options.OcrEngine}");

                // 设置 OCR 引擎类型（PaddleOCR-Sharp 已移除）
                OcrService.CurrentEngine = OcrEngineType.WindowsBuiltIn;
                Logger.Info($"[OCR] 引擎类型已设置: {OcrService.CurrentEngine}");

                // 检查引擎状态
                string status = OcrService.GetEngineStatus();
                bool ready = OcrService.IsEngineReady();
                Logger.Info($"[OCR] 引擎状态: {status}, 就绪={ready}");
            }
            catch (Exception ex)
            {
                // ⚠️ 关键：OCR 初始化失败绝不能影响聊天核心功能
                Logger.Error($"[OCR] ❌ 初始化失败（已降级，不影响聊天）: {ex.GetType().Name} - {ex.Message}", ex);
                OcrService.CurrentEngine = OcrEngineType.WindowsBuiltIn;
            }
        }

        /// <summary>
        /// 设置变更事件回调（用户点击 Options 对话框的"确定"/"应用"时触发）。
        /// 热重载 OCR、Web 搜索、模型等配置，无需重启聊天窗口。
        /// </summary>
        private void OnOcrSettingsChanged()
        {
            Logger.Info("[Settings] 检测到设置变更，热重载配置...");
            try
            {
                // 刷新 _options 引用（DialogPage 属性已由 VS 自动更新）
                if (_package != null)
                    _options = _package.Options;

                // ── OCR 热重载 ──
                OcrService.ResetAllEngines();
                InitializeOcrService();
                Logger.Info($"[Settings] OCR 热切换完成 → {OcrService.CurrentEngine}");

                // ── Web 搜索热重载 ──
                string optionsProvider = _options?.SearchProvider ?? "DuckDuckGo";
                string resolvedEngine = optionsProvider switch
                {
                    "Baidu" => "Baidu",
                    _ => "DuckDuckGo"
                };

                int idx = resolvedEngine switch
                {
                    "Baidu" => 0,
                    "DuckDuckGo" => 1,
                    _ => 1
                };
                WebSearchEngineComboBox.SelectedIndex = idx;

                // 如果联网搜索当前是开启状态，同步引擎并应用配置
                if (_webSearchEngine != "Off")
                {
                    if (_webSearchEngine != resolvedEngine)
                    {
                        _webSearchEngine = resolvedEngine;
                        Logger.Info($"[Settings] 搜索引擎热切换为: {_webSearchEngine}");
                    }

                    ApplyWebSearchConfig();
                    UpdateWebSearchToggleAppearance();

                    if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                    {
                        StatusLabel.Text = LocalizationService.Instance["status.search.baiduKeyRequired"];
                    }
                    else
                    {
                        StatusLabel.Text = $"设置已更新 (搜索引擎: {_webSearchEngine})";
                    }
                }
                else
                {
                    StatusLabel.Text = $"设置已更新 (默认引擎: {resolvedEngine})";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Settings] 设置热切换失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 后台异步初始化 MCP 服务器连接。
        /// 失败不影响核心聊天功能。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 用于 fire-and-forget 初始化
        private async void InitializeMcp()
        {
            try
            {
                // 从独立的配置文件加载 MCP 服务器配置
                var mcpConfigs = McpConfigStore.Load();
                var enabledConfigs = mcpConfigs.Where(c => c.Enabled).ToList();

                if (enabledConfigs.Count == 0)
                {
                    Logger.Info("[MCP] 没有启用的 MCP 服务器，跳过初始化。点击 🔌 按钮配置。");
                    return;
                }

                // 清理旧的 MCP 管理器
                _mcpManager?.Dispose();
                _mcpManager = new McpManagerService();
                OcrService.McpManager = _mcpManager; // 注入 OCR 服务

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _mcpManager.InitializeAsync(enabledConfigs, cts.Token);

                // ── 将 MCP 管理器注入到 Agent 调度器 ──
                _agentDispatcher?.UpdateMcpManager(_mcpManager);

                var toolCount = _mcpManager.AllTools.Count;
                if (toolCount > 0)
                {
                    Logger.Info($"[MCP] MCP 初始化完成，共 {toolCount} 个工具可用");
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.mcpConnected"], toolCount);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MCP] MCP 初始化失败: {ex.Message}", ex);
            }
            finally
            {
                UpdateMcpButtonAppearance();
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
        /// 后台发现并加载 Skill。
        /// 扫描项目目录和用户目录下的 SKILL.md 文件。
        /// 失败不影响核心聊天功能。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 用于 fire-and-forget 初始化
        private async void InitializeSkills()
        {
            try
            {
                _skillService = SkillService.Instance;
                _skillDiscoveryResult = await _skillService.DiscoverSkillsAsync(_solutionPath);

                if (_skillDiscoveryResult.TotalCount > 0)
                {
                    Logger.Info($"[Skill] Skill 发现完成: 共 {_skillDiscoveryResult.TotalCount} 个技能 " +
                        $"(项目: {_skillDiscoveryResult.ProjectSkillCount}, " +
                        $"用户: {_skillDiscoveryResult.UserSkillCount})");

                    var invocableCount = _skillDiscoveryResult.UserInvocableSkills.Count;
                    if (invocableCount > 0)
                    {
                        var names = string.Join(", ", _skillDiscoveryResult.UserInvocableSkills.ConvertAll(s => s.Name));
                        Logger.Info($"[Skill] 可调用技能: {names}");
                    }
                    // 内置示例技能已随扩展发布，在 BuiltInSkills 目录下
                }
                else
                {
                    Logger.Info("[Skill] 未发现任何 Skill 定义。可在 .github/skills/<name>/SKILL.md 或 ~/.copilot/skills/<name>/SKILL.md 中创建。");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] Skill 初始化失败: {ex.Message}", ex);
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
        /// 同时遵循用户在 ComboBox 中选择的搜索引擎偏好。
        /// 用于支持用户在 工具→选项 中修改 API Key 后无需重启即可生效。
        /// </summary>
        private void ApplyWebSearchConfig()
        {
            if (_webSearchService == null) return;

            switch (_webSearchEngine)
            {
                case "Baidu":
                    if (_options != null && !string.IsNullOrWhiteSpace(_options.BaiduApiKey))
                    {
                        _webSearchService.ConfigureBaiduSearch(_options.BaiduApiKey);
                        Logger.Info("联网搜索热重载: 百度千帆 (API Key 已配置)");
                    }
                    else
                    {
                        _webSearchService.ConfigureBaiduSearch(null!);
                        Logger.Info("联网搜索热重载: DuckDuckGo (百度 API Key 未配置)");
                    }
                    break;

                default:
                    _webSearchService.ConfigureBaiduSearch(null!);
                    Logger.Info($"联网搜索热重载: DuckDuckGo (用户选择 {_webSearchEngine})");
                    break;
            }
        }

        /// <summary>
        /// 后台异步校验所有已配置的 API Key 是否有效。
        /// 启动时调用，校验结果通过 StatusLabel 提示用户。
        /// </summary>
        private async Task ValidateAllApiKeysAsync()
        {
            // ── 校验 DeepSeek API Key ──
            if (_apiService != null)
            {
                string? deepSeekError = await _apiService.ValidateApiKeyAsync();
                if (deepSeekError != null)
                {
                    Logger.Error($"DeepSeek API Key 校验失败: {deepSeekError}");
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = LocalizationService.Instance["status.apiKeyInvalid"];
                }
                else
                {
                    Logger.Info("DeepSeek API Key 校验通过");
                }
            }

            // ── 校验百度 API Key ──
            if (_webSearchService != null && _webSearchEngine == "Baidu" &&
                _options != null && !string.IsNullOrWhiteSpace(_options.BaiduApiKey))
            {
                string? baiduError = await _webSearchService.ValidateBaiduApiKeyAsync();
                if (baiduError != null)
                {
                    Logger.Error($"百度 API Key 校验失败: {baiduError}");
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = LocalizationService.Instance["status.baiduApiKeyInvalid"];
                }
                else
                {
                    Logger.Info("百度 API Key 校验通过");
                }
            }

            // ── 校验 OCR 引擎状态（PaddleOCR 已移除，仅检查 Windows 内置 OCR）──
            {
                bool ocrReady = OcrService.IsEngineReady();
                string ocrStatus = OcrService.GetEngineStatus();
                Logger.Info($"OCR 引擎状态: {ocrStatus}");

                if (!ocrReady)
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = LocalizationService.Instance["status.ocrUnavailable"];
                }
            }
        }

        /// <summary>
        /// 解析当前工作区路径。支持以下场景：
        /// 1. 传统 .sln 解决方案 → 使用 .sln 文件路径作为标识
        /// 2. 文件夹项目（CMake / Open Folder）→ 使用工作区根目录路径作为标识
        /// 3. 未打开任何项目 → null，使用 _unsaved.json 兜底存储
        /// 
        /// 解析顺序：
        ///   a) IVsSolution.GetSolutionInfo —— 适用于 .sln 项目，返回 .sln 文件路径
        ///   b) IVsWorkspaceService          —— 适用于所有 Open Folder 项目，返回工作区根目录
        ///   c) DTE                          —— 终极回退，兼容极少数边界情况
        /// </summary>
        private async Task ResolveSolutionPathAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // 第一步：.sln 项目 (仅返回 .sln 文件路径，文件夹项目此处返回 null)
                _solutionPath = GetSolutionPathFromIVsSolution();

                // 第二步：终极 DTE 回退（GetSolutionPathFromIVsSolution 已同时覆盖 .sln 和 Open Folder）
                if (string.IsNullOrEmpty(_solutionPath))
                {
                    var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                    _solutionPath = GetSolutionPathFromDTE(dte);
                }

                if (!string.IsNullOrEmpty(_solutionPath))
                    Logger.Info($"检测到项目路径: {_solutionPath}");
                else
                    Logger.Info("未检测到已打开的项目，使用默认存储 (_unsaved.json)");
            }
            catch (Exception ex)
            {
                Logger.Error("解析项目路径失败", ex);
                _solutionPath = null;
            }
        }

        /// <summary>
        /// 通过 IVsSolution.GetSolutionInfo 获取项目路径（首选方案）。
        /// 
        /// 这是 VS SDK 中获取解决方案/工作区路径的官方接口：
        ///   - GetSolutionInfo(out dir, out file, out opts)
        ///   - 对 .sln 项目，file 为非空 .sln 路径
        ///   - 对文件夹项目 (Open Folder/CMake)，file 为空，dir 为工作区根目录
        /// 
        /// 参考: https://learn.microsoft.com/zh-cn/dotnet/api/microsoft.visualstudio.shell.interop.ivssolution.getsolutioninfo
        /// </summary>
        /// <summary>
        /// 通过 IVsSolution.GetSolutionInfo 获取项目路径。
        /// - .sln 项目：返回 .sln 文件路径
        /// - Open Folder 项目（CMake 等无 .sln）：返回工作区根目录（solutionDir）
        /// 在 VS 2019+ 中，GetSolutionInfo 对 Open Folder 会在 solutionDir 中返回工作区根目录。
        /// </summary>
        private static string? GetSolutionPathFromIVsSolution()
        {
            try
            {
                var vsSolution = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                if (vsSolution == null)
                    return null;

                int hr = vsSolution.GetSolutionInfo(out string solutionDir, out string solutionFile, out string _);
                Logger.Info($"[Workspace] IVsSolution.GetSolutionInfo → HR=0x{hr:X8}, dir=[{solutionDir ?? "(null)"}], file=[{solutionFile ?? "(null)"}]");

                if (hr != VSConstants.S_OK)
                {
                    Logger.Warn($"[Workspace] IVsSolution.GetSolutionInfo 返回非 S_OK: 0x{hr:X8}");
                    return null;
                }

                // .sln 项目优先返回 .sln 文件路径
                if (!string.IsNullOrWhiteSpace(solutionFile))
                {
                    Logger.Info($"[Workspace] ✅ IVsSolution → .sln 项目: {solutionFile}");
                    return solutionFile;
                }

                // Open Folder 项目：solutionFile 为空，回退到 solutionDir
                if (!string.IsNullOrWhiteSpace(solutionDir))
                {
                    string dir = solutionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    Logger.Info($"[Workspace] ✅ IVsSolution → Open Folder 项目: {dir}");
                    return dir;
                }

                Logger.Info("[Workspace] IVsSolution 未发现项目路径");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Workspace] IVsSolution.GetSolutionInfo 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 通过 DTE 接口获取项目路径（回退方案，当 IVsSolution 不可用时）。
        /// 顺序：Solution.FullName → Solution.Properties("Path") → 首个项目父目录
        /// </summary>
        private static string? GetSolutionPathFromDTE(EnvDTE.DTE? dte)
        {
            if (dte?.Solution == null || !dte.Solution.IsOpen)
                return null;

            var solution = dte.Solution;

            // ── A) Solution.FullName（.sln 文件路径）──
            try
            {
                string? fullName = solution.FullName;
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    Logger.Info($"[Workspace] DTE Solution.FullName: {fullName}");
                    return fullName;
                }
            }
            catch { }

            // ── B) Solution.Properties("Path") ──
            try
            {
                var pathProp = solution.Properties?.Item("Path");
                if (pathProp?.Value is string folderPath && !string.IsNullOrWhiteSpace(folderPath))
                {
                    Logger.Info($"[Workspace] DTE Solution.Properties(\"Path\"): {folderPath}");
                    return folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
            catch { }

            // ── C) 首个项目的父目录 ──
            try
            {
                var projects = solution.Projects;
                if (projects != null && projects.Count > 0)
                {
                    foreach (EnvDTE.Project project in projects)
                    {
                        try
                        {
                            string? fullName = project?.FullName;
                            if (!string.IsNullOrWhiteSpace(fullName))
                            {
                                string? dir = Path.GetDirectoryName(fullName);
                                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                                {
                                    Logger.Info($"[Workspace] DTE Project.FullName 推断: {dir}");
                                    return dir;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 订阅 Microsoft.VisualStudio.Shell.Events.SolutionEvents（推荐 VSSDK 方式）。
        /// OnAfterOpenSolution / OnAfterCloseSolution 用于 .sln 项目；
        /// OnAfterOpenFolder / OnAfterCloseFolder 用于 Open Folder / CMake 项目。
        /// 这与 DTE SolutionEvents 不同——后者仅在 .sln 打开时触发。
        /// </summary>
        private async Task WireSolutionEventsAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // .sln 项目
                SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;
                SolutionEvents.OnAfterCloseSolution += OnAfterCloseSolution;

                // Open Folder / CMake 项目
                SolutionEvents.OnAfterOpenFolder += OnAfterOpenFolder;
                SolutionEvents.OnAfterCloseFolder += OnAfterCloseFolder;

                Logger.Info("[会话] SolutionEvents 监听已注册（覆盖 .sln 和 Open Folder / CMake）");
            }
            catch (Exception ex)
            {
                Logger.Error("[会话] 注册解决方案事件失败", ex);
            }
        }

        // ── .sln 事件处理 ──
        private void OnAfterOpenSolution(object sender, OpenSolutionEventArgs e) => OnSolutionOpened();
        private void OnAfterCloseSolution(object sender, EventArgs e) => OnSolutionClosed();

        // ── Open Folder / CMake 事件处理 ──
        private void OnAfterOpenFolder(object sender, FolderEventArgs e)
        {
            Logger.Info($"[会话] OnAfterOpenFolder: {e.FolderPath}");
            OnSolutionOpened();
        }
        private void OnAfterCloseFolder(object sender, FolderEventArgs e)
        {
            Logger.Info($"[会话] OnAfterCloseFolder: {e.FolderPath}");
            OnSolutionClosed();
        }

        /// <summary>
        /// 用户打开解决方案时：保存当前对话，加载新解决方案的对话记录。
        /// </summary>
        private void OnSolutionOpened()
        {
            Logger.Info("[会话] 检测到解决方案已打开，准备切换对话存储");

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // 先保存当前对话
                    SaveCurrentSession();

                    // 解析新路径并重载
                    await ResolveSolutionPathAsync();

                    // ── 重置浏览器状态，切换解决方案时强制全量刷新 ──
                    _browserInitialized = false;

                    await LoadAndShowAsync();

                    Logger.Info($"[会话] 对话已切换到新解决方案: {_solutionPath ?? "(无)"}");
                }
                catch (Exception ex)
                {
                    Logger.Error("[会话] 切换解决方案时出错", ex);
                }
            });
        }

        /// <summary>
        /// 用户关闭解决方案时：保存当前对话，切换到无解决方案状态。
        /// 复用 LoadAndShowAsync 以避免每次关闭都创建新的空会话。
        /// </summary>
        private void OnSolutionClosed()
        {
            Logger.Info("[会话] 检测到解决方案已关闭，保存并清空对话");

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // 保存当前解决方案的对话
                    SaveCurrentSession();

                    // 切换到无解决方案状态
                    _solutionPath = null;

                    // ── 重置浏览器状态，切换时强制全量刷新 ──
                    _browserInitialized = false;

                    await LoadAndShowAsync();

                    Logger.Info("[会话] 对话已清空（解决方案已关闭）");
                }
                catch (Exception ex)
                {
                    Logger.Error("[会话] 关闭解决方案时出错", ex);
                }
            });
        }

        /// <summary>
        /// 触发代码索引：在后台线程执行，不阻塞 UI。
        private async Task LoadAndShowAsync()
        {
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            // 加载所有会话
            _sessionsContainer = ChatPersistenceService.LoadSessions(_solutionPath);

            // 确定活跃会话
            if (!string.IsNullOrEmpty(_sessionsContainer.ActiveSessionId))
            {
                _activeSession = _sessionsContainer.Sessions
                    .FirstOrDefault(s => s.Id == _sessionsContainer.ActiveSessionId);
            }
            _activeSession ??= _sessionsContainer.Sessions.FirstOrDefault();

            // 如果没有会话，创建默认会话
            if (_activeSession == null)
            {
                _activeSession = CreateNewSessionInternal();
                _sessionsContainer.Sessions.Add(_activeSession);
                _sessionsContainer.ActiveSessionId = _activeSession.Id;
            }

            // 加载活跃会话的消息
            _messages.Clear();
            _contextManager.Clear();

            bool hasData = _activeSession.ApiHistory.Count > 0
                        || !string.IsNullOrWhiteSpace(_activeSession.TreeDataJson);

            if (hasData)
            {
                Logger.Info($"[Render] LoadConversation: 从会话 '{_activeSession.Title}' 加载数据 "
                    + $"(apiHistory={_activeSession.ApiHistory.Count}, "
                    + $"hasTree={!string.IsNullOrWhiteSpace(_activeSession.TreeDataJson)})");

                // ── 从 TreeData 恢复树状结构（UI 展示用）──
                if (!string.IsNullOrWhiteSpace(_activeSession.TreeDataJson))
                {
                    try
                    {
                        var treeData = System.Text.Json.JsonSerializer.Deserialize<TreePersistenceData>(
                            _activeSession.TreeDataJson,
                            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                        if (treeData != null)
                        {
                            _tree = ConversationTree.Deserialize(treeData);
                            SyncMessagesFromTree();
                            Logger.Info($"[Tree] LoadConversation 从 TreeData 恢复 (节点数: {treeData.Nodes.Count})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Tree] LoadConversation TreeData 反序列化失败: {ex.Message}");
                    }
                }

                // ── 从 ApiHistory 恢复完整上下文（权威数据源，含 tool_calls/reasoning/system）──
                if (_activeSession.ApiHistory.Count > 0)
                {
                    try
                    {
                        _contextManager.RestoreFullContext(_activeSession.ApiHistory);
                        Logger.Info($"[Context] 从 ApiHistory 恢复上下文成功 ({_activeSession.ApiHistory.Count} 条消息, "
                            + $"turnCount={_contextManager.TurnCount}, estimatedTokens={_contextManager.EstimatedTokens})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Context] 从 ApiHistory 恢复上下文失败，回退到树重建: {ex.Message}", ex);
                        // 回退：从树节点重建上下文（不含 tool_calls，但 user/assistant 可用）
                        RebuildContextFromTree();
                        StatusLabel.Text = LocalizationService.Instance["status.contextRestoreFailed"];
                    }
                }
                else
                {
                    // ApiHistory 为空时，从树节点重建上下文（简易对话无 tool_calls 场景）
                    RebuildContextFromTree();
                    Logger.Info("[Context] ApiHistory 为空，从树节点重建上下文");
                }
            }

            // 没有消息则显示欢迎语
            if (_messages.Count == 0)
            {
                bool hasApiKey = _options != null && !string.IsNullOrEmpty(_options.ApiKey);
                string welcomeContent = hasApiKey ? WelcomeMessage : ApiKeyMissingMessage;

                var welcomeMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = welcomeContent,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(welcomeMsg);
                Logger.Info(hasApiKey ? "[Render] 添加欢迎语" : "[Render] 添加欢迎语 + API密钥缺失警告");
            }

            // 填充会话下拉框
            PopulateSessionComboBox();

            // ── 从会话恢复累计 Cache 统计 ──
            RestoreCacheStatsFromSession();

            // ── WebView2 只能初始化一次，切换解决方案时只刷新页面内容 ──
            if (_webViewInitialized)
            {
                Logger.Info("[Render] WebView2 已初始化，直接刷新页面内容");
                RebuildMessagesHtml();
                UpdateBrowser();
                // ── 重建持久化的任务面板 ──
                _ = RebuildPanelsWhenPageReadyAsync();
            }
            else
            {
                // ── 抑制 CoreWebView2InitializationCompleted 中的 UpdateBrowser ──
                // 事件在 EnsureCoreWebView2Async 期间同步触发，但其 RunAsync 延迟执行
                // 会覆盖此处的显式 UpdateBrowser，导致面板丢失。
                _suppressWebViewUpdate = true;
                bool initSuccess = await InitializeWebViewAsync();
                _webViewInitialized = initSuccess;

                if (initSuccess)
                {
                    // ── 显式构建 HTML 并全量加载到 WebView ──
                    // 使用 session 数据重建消息 HTML，重置初始化标志强制 NavigateToString
                    // 全量替换路径（而非增量追加），避免与事件处理器中的空白页面重复。
                    RebuildMessagesHtml();
                    _browserInitialized = false;
                    UpdateBrowser();
                    // ── 重建持久化的任务面板 ──
                    _ = RebuildPanelsWhenPageReadyAsync();
                }
                else
                {
                    // ── 初始化失败：显示错误状态并确保抑制标志被重置 ──
                    Logger.Error("[Render] WebView2 初始化失败，聊天窗口将以降级模式运行");
                }
                // ── 无论成功与否，重置抑制标志 ──
                _suppressWebViewUpdate = false;
            }
        }

        /// <summary>
        /// 初始化 WebView2 环境。返回 true 表示初始化成功。
        /// 包含重试逻辑：首次失败后，使用默认用户数据文件夹路径重试一次。
        /// ReSharper 等第三方扩展可能通过环境变量或注册表设置影响 WebView2 运行时发现，
        /// 显式指定 browserExecutableFolder 可提高初始化成功率。
        /// </summary>
        private async Task<bool> InitializeWebViewAsync()
        {
            string userDataFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekVS", "WebView2");

            // ── 尝试1：使用自定义用户数据文件夹 ──
            try
            {
                Logger.Info($"[Render] 开始初始化 WebView2 CoreWebView2 环境 (userDataFolder={userDataFolder})");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await ChatWebView.EnsureCoreWebView2Async(env);
                Logger.Info("[Render] CoreWebView2 环境初始化成功");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Render] WebView2 初始化尝试1失败: {ex.GetType().Name}: {ex.Message}");
                Logger.Warn($"[Render] 堆栈: {ex.StackTrace}");

                // ── 尝试2：使用默认用户数据文件夹 + 默认运行时发现 ──
                try
                {
                    Logger.Info("[Render] 重试 WebView2 初始化 (尝试2, 默认参数)...");
                    // 传入空字符串等效于默认临时文件夹
                    var env = await CoreWebView2Environment.CreateAsync();
                    await ChatWebView.EnsureCoreWebView2Async(env);
                    Logger.Info("[Render] CoreWebView2 环境初始化成功 (尝试2)");
                    return true;
                }
                catch (Exception ex2)
                {
                    Logger.Error($"[Render] WebView2 初始化尝试2也失败: {ex2.GetType().Name}: {ex2.Message}");
                }
            }

            // ── 所有尝试都失败 ──
            var i18nMsg = LocalizationService.Instance["status.webView2Failed"];
            if (i18nMsg.StartsWith("[")) // key not found, use hardcoded fallback
                i18nMsg = "WebView2 initialization failed. Please verify the Evergreen WebView2 Runtime is installed.";
            StatusLabel.Text = i18nMsg;
            return false;
        }

        #endregion
    }
}
