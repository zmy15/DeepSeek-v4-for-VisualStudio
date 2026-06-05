using DeepSeek_v4_for_VisualStudio.Services.Agents;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 服务注册扩展 — 将所有服务注册到 DI 容器。
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册所有 DeepSeek Chat 服务。
        /// </summary>
        public static IServiceCollection AddDeepSeekServices(this IServiceCollection services)
        {
            // ── 核心 API 服务（延迟初始化 API Key） ──
            services.AddSingleton<IDeepSeekApiService>(sp =>
            {
                var options = Settings.DeepSeekOptionsPage.Instance;
                var apiKey = options?.ApiKey ?? "";
                var model = options?.SelectedModel ?? "deepseek-v4-pro";
                var service = new DeepSeekApiService(apiKey, model);

                // 配置思考模式
                if (options != null)
                {
                    service.ConfigureThinking(
                        options.IsThinkingEnabled,
                        options.ReasoningEffort);
                }

                // ── 注入前缀缓存管理器（v1.1.9 前缀缓存优化）──
                var prefixCache = sp.GetService<IPrefixCacheManager>() as PrefixCacheManager;
                if (prefixCache != null)
                {
                    service.PrefixCache = prefixCache;
                }

                return service;
            });

            // ── 文件解析服务（适配器包装静态类） ──
            services.AddSingleton<IFileParserService, FileParserServiceAdapter>();

            // ── 编辑补丁服务 ──
            services.AddSingleton<IEditPatchService>(sp =>
            {
                var apiService = sp.GetRequiredService<IDeepSeekApiService>();
                return new EditPatchService((DeepSeekApiService)apiService);
            });

            // ── 上下文管理 ──
            services.AddSingleton<IConversationContextManager, ConversationContextManager>();
            services.AddSingleton<IPrefixCacheManager, PrefixCacheManager>();
            services.AddSingleton<IContextCompressorService>(sp =>
            {
                // ContextCompressorService 可选 LLM 摘要器（通过 DeepSeekApiService）
                var apiService = sp.GetRequiredService<IDeepSeekApiService>();
                return new ContextCompressorService(
                    summarizer: async (text, ct) =>
                    {
                        var messages = new List<Models.ChatApiMessage>
                        {
                            new() { Role = "user", Content = text }
                        };
                        return await apiService.CompleteAsync(messages, ct);
                    });
            });

            // ── Skill 服务 ──
            services.AddSingleton<ISkillService, SkillService>();

            // ── 搜索与 RAG ──
            services.AddSingleton<IWebSearchService, WebSearchService>();
            services.AddSingleton<IRagService, RagService>();

            // ── 记忆服务 ──
            services.AddSingleton<IMemoryService, MemoryService>();

            // ── MCP 服务 ──
            services.AddSingleton<IMcpManagerService, McpManagerService>();

            // ── 构建服务 ──
            services.AddSingleton<IBuildService, BuildService>();

            // ── 内置工具服务 ──
            services.AddSingleton<IBuiltInToolService>(sp =>
            {
                var mcpManager = sp.GetService<IMcpManagerService>() as McpManagerService;
                var webSearch = sp.GetService<IWebSearchService>() as WebSearchService;
                var buildService = sp.GetService<IBuildService>();
                var memoryService = sp.GetService<IMemoryService>();
                return new BuiltInToolService(mcpManager, webSearch, buildService, memoryService);
            });

            // ── 持久化服务（适配器包装静态类） ──
            services.AddSingleton<IChatPersistenceService, ChatPersistenceServiceAdapter>();

            // ── Agent 调度器 ──
            services.AddSingleton<IAgentDispatcher>(sp =>
            {
                var apiService = sp.GetRequiredService<IDeepSeekApiService>() as DeepSeekApiService;
                var builtInTools = sp.GetService<IBuiltInToolService>() as BuiltInToolService;
                var mcpManager = sp.GetService<IMcpManagerService>() as McpManagerService;

                var dispatcher = new AgentDispatcher(apiService!, builtInTools);
                dispatcher.UpdateMcpManager(mcpManager);
                return dispatcher;
            });

            return services;
        }
    }
}
