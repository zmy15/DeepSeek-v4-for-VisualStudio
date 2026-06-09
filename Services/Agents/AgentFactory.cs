using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Agent 工厂 — 负责 Agent 实例的创建、缓存和依赖注入。
    /// 替代原 AgentDispatcher 的创建/注入职责，不负责路由和 Handoff 编排。
    /// 
    /// 所有 Agent 以 AskAgent 为统一入口，
    /// Agent 在执行过程中通过 request_handoff 工具自行决定是否移交。
    /// </summary>
    public class AgentFactory : IDisposable
    {
        private readonly DeepSeekApiService _apiService;
        private BuiltInToolService? _builtInToolService;
        private McpManagerService? _mcpManager;
        private readonly IMemoryService? _memoryService;

        // ── Agent 实例（懒加载） ──
        private AskAgent? _askAgent;
        private ExploreAgent? _exploreAgent;
        private PlanAgent? _planAgent;
        private EditAgent? _editAgent;
        private BuildAgent? _buildAgent;

        // ── 属性 ──
        public AskAgent AskAgent
        {
            get
            {
                if (_askAgent == null)
                {
                    _askAgent = new AskAgent(_apiService);
                    InjectServices(_askAgent);
                }
                // 🔑 确保 ExploreAgent 已注入（属性 getter 可能被 UI 初始化直接调用，绕过 GetAgent）
                WireExploreAgent(_askAgent);
                return _askAgent;
            }
        }

        public ExploreAgent ExploreAgent
        {
            get
            {
                if (_exploreAgent == null)
                {
                    _exploreAgent = new ExploreAgent(_apiService);
                    InjectServices(_exploreAgent);
                }
                return _exploreAgent;
            }
        }

        public PlanAgent PlanAgent
        {
            get
            {
                if (_planAgent == null)
                {
                    _planAgent = new PlanAgent(_apiService);
                    InjectServices(_planAgent);
                }
                // 🔑 确保 ExploreAgent 与 AgentFactory 实例同步
                WireExploreAgent(_planAgent);
                return _planAgent;
            }
        }

        public EditAgent EditAgent
        {
            get
            {
                if (_editAgent == null)
                {
                    _editAgent = new EditAgent(_apiService);
                    InjectServices(_editAgent);
                }
                // 🔑 确保 ExploreAgent 与 AgentFactory 实例同步
                WireExploreAgent(_editAgent);
                return _editAgent;
            }
        }

        public BuildAgent BuildAgent
        {
            get
            {
                if (_buildAgent == null)
                {
                    _buildAgent = new BuildAgent(_apiService);
                    InjectServices(_buildAgent);
                }
                // 🔑 确保 ExploreAgent 与 AgentFactory 实例同步
                WireExploreAgent(_buildAgent);
                return _buildAgent;
            }
        }

        /// <summary>当前活跃计划（由 UI 层管理）</summary>
        public AgentTaskPlan? ActivePlan { get; set; }

        /// <summary>会话上下文管理器引用</summary>
        public ConversationContextManager? ContextManager { get; set; }

        public AgentFactory(DeepSeekApiService apiService,
            BuiltInToolService? builtInToolService = null,
            McpManagerService? mcpManager = null,
            IMemoryService? memoryService = null)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
            _builtInToolService = builtInToolService;
            _mcpManager = mcpManager;
            _memoryService = memoryService;
        }

        /// <summary>
        /// 更新 MCP 管理器引用并注入到所有已创建的 Agent。
        /// </summary>
        public void UpdateMcpManager(McpManagerService mcpManager)
        {
            _mcpManager = mcpManager;
            if (_askAgent != null) _askAgent.McpManager = mcpManager;
            if (_exploreAgent != null) _exploreAgent.McpManager = mcpManager;
            if (_planAgent != null) _planAgent.McpManager = mcpManager;
            if (_editAgent != null) _editAgent.McpManager = mcpManager;
            if (_buildAgent != null) _buildAgent.McpManager = mcpManager;
            if (mcpManager != null)
            {
                Logger.Info($"[AgentFactory] MCP 管理器已注入 (工具数: {mcpManager.AllTools.Count})");
            }
            else
            {
                Logger.Info("[AgentFactory] MCP 管理器已清除 (null)");
            }
        }

        /// <summary>
        /// 根据 AgentType 获取对应的 Agent 实例。
        /// </summary>
        public BaseAgent GetAgent(AgentType type)
        {
            BaseAgent agent = type switch
            {
                AgentType.Ask => AskAgent,
                AgentType.Explore => ExploreAgent,
                AgentType.Plan => PlanAgent,
                AgentType.Edit => EditAgent,
                AgentType.Build => BuildAgent,
                _ => AskAgent,
            };

            // ── 注入 ExploreAgent 引用并设置日志转发（统一单点，避免双重订阅）──
            // AskAgent / BuildAgent：简单属性 + WireExploreLogs
            WireExploreAgent(agent);

            // EditAgent / PlanAgent：属性 setter 内部调用 RegisterExploreAgent（已含日志转发）
            // 注意：RegisterExploreAgent 自带 -= 防重，但 InjectServices 已移除此逻辑，
            // 确保此处是唯一注入点。
            if (agent is EditAgent editAgent && editAgent.ExploreAgent == null)
                editAgent.ExploreAgent = ExploreAgent;
            if (agent is PlanAgent planAgent)
                planAgent.ExploreAgent = ExploreAgent;

            // ── 通用回退：确保 BaseAgent.ExploreAgent 已注入并设置日志转发 ──
            if (agent.ExploreAgent == null)
            {
                agent.ExploreAgent = ExploreAgent;
                agent.WireExploreLogs();
            }

            return agent;
        }

        /// <summary>
        /// 注入共享服务到 Agent（不包含 ExploreAgent，由 GetAgent 统一管理）。
        /// </summary>
        private void InjectServices(BaseAgent agent)
        {
            if (agent.BuiltInTools == null && _builtInToolService != null)
                agent.BuiltInTools = _builtInToolService;
            if (agent.McpManager == null && _mcpManager != null)
                agent.McpManager = _mcpManager;
            if (agent.MemoryService == null && _memoryService != null)
                agent.MemoryService = _memoryService;
        }

        /// <summary>
        /// 确保 Agent 有 ExploreAgent 引用并设置日志转发。
        /// </summary>
        private void WireExploreAgent(BaseAgent agent)
        {
            if (agent is AskAgent askAgent && askAgent.ExploreAgent == null)
            {
                askAgent.ExploreAgent = ExploreAgent;
                askAgent.WireExploreLogs();
            }
            if (agent is BuildAgent buildAgent && buildAgent.ExploreAgent == null)
            {
                buildAgent.ExploreAgent = ExploreAgent;
                buildAgent.WireExploreLogs();
            }
        }

        public void Dispose()
        {
            _askAgent?.Dispose();
            _exploreAgent?.Dispose();
            _planAgent?.Dispose();
            _editAgent?.Dispose();
            _buildAgent?.Dispose();
        }

        /// <summary>
        /// 通过 EnvDTE 项目系统删除文件。
        /// 从 AgentDispatcher 搬过来，保持为静态工具方法。
        /// </summary>
        public static async System.Threading.Tasks.Task DeleteFilesViaEnvDTEAsync(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0) return;

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                .SwitchToMainThreadAsync();

            var dte = (EnvDTE.DTE?)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                .GetService(typeof(EnvDTE.DTE));
            if (dte == null || dte.Solution == null || !dte.Solution.IsOpen)
            {
                foreach (string fp in filePaths)
                {
                    try { if (System.IO.File.Exists(fp)) System.IO.File.Delete(fp); }
                    catch (Exception ex) { Logger.Warn($"[AgentFactory] 磁盘删除失败: {fp} - {ex.Message}"); }
                }
                Logger.Warn("[AgentFactory] DTE 不可用，仅执行磁盘文件删除");
                return;
            }

            foreach (string filePath in filePaths)
            {
                try
                {
                    EnvDTE.ProjectItem? item = FindProjectItemByPath(dte, filePath);
                    if (item != null)
                    {
                        item.Delete();
                        Logger.Info($"[AgentFactory] ✅ 已通过 EnvDTE 从项目中删除: {filePath}");
                    }
                    else
                    {
                        if (System.IO.File.Exists(filePath))
                        {
                            System.IO.File.Delete(filePath);
                            Logger.Info($"[AgentFactory] ✅ 已从磁盘删除: {filePath}");
                        }
                        else
                        {
                            Logger.Warn($"[AgentFactory] 文件不存在，跳过: {filePath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AgentFactory] 删除文件失败: {filePath} - {ex.Message}");
                }
            }
        }

        private static EnvDTE.ProjectItem? FindProjectItemByPath(EnvDTE.DTE dte, string filePath)
        {
            try
            {
                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    var item = FindInProjectItems(project.ProjectItems, filePath);
                    if (item != null) return item;
                }
            }
            catch { }
            return null;
        }

        private static EnvDTE.ProjectItem? FindInProjectItems(EnvDTE.ProjectItems items, string filePath)
        {
            if (items == null) return null;
            foreach (EnvDTE.ProjectItem item in items)
            {
                try
                {
                    if (item.FileCount > 0 && string.Equals(item.FileNames[0], filePath, StringComparison.OrdinalIgnoreCase))
                        return item;
                }
                catch { }
                if (item.ProjectItems != null)
                {
                    var found = FindInProjectItems(item.ProjectItems, filePath);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// 在所有已创建的 Agent 实例中查找拥有指定 requestId 的待处理权限请求。
        /// </summary>
        public BaseAgent? FindAgentWithPendingPermission(string requestId)
        {
            var agents = new BaseAgent?[] { _askAgent, _exploreAgent, _planAgent, _editAgent, _buildAgent };
            foreach (var agent in agents)
            {
                if (agent?.TryGetPendingPermission(requestId) != null)
                    return agent;
            }
            return null;
        }

        /// <summary>
        /// 在所有已创建的 Agent 实例中查找拥有指定 requestId 的待处理提问请求。
        /// </summary>
        public BaseAgent? FindAgentWithPendingQuestion(string requestId)
        {
            var agents = new BaseAgent?[] { _askAgent, _exploreAgent, _planAgent, _editAgent, _buildAgent };
            foreach (var agent in agents)
            {
                if (agent?.TryGetPendingQuestion(requestId) != null)
                    return agent;
            }
            return null;
        }
    }
}
