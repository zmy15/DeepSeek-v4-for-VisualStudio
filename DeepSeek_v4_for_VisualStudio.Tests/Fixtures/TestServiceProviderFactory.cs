using DeepSeek_v4_for_VisualStudio.Services.Agents;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace DeepSeek_v4_for_VisualStudio.Tests.Fixtures
{
    /// <summary>
    /// 测试专用 DI 容器 fixture — 为单元测试提供 Mock 友好的服务提供器。
    /// </summary>
    public static class TestServiceProviderFactory
    {
        /// <summary>
        /// 创建测试用 IServiceProvider，使用 Mock 服务替代真实 API/外部依赖。
        /// </summary>
        public static IServiceProvider Create(Mock<IDeepSeekApiService>? mockApiService = null)
        {
            var services = new ServiceCollection();

            // ── Mock API 服务 ──
            if (mockApiService != null)
            {
                services.AddSingleton(mockApiService.Object);
            }
            else
            {
                var defaultMock = new Mock<IDeepSeekApiService>();
                services.AddSingleton(defaultMock.Object);
            }

            // ── 核心服务（真实实现，便于单元测试业务逻辑） ──
            services.AddSingleton<IFileParserService, FileParserServiceAdapter>();
            services.AddSingleton<IConversationContextManager, ConversationContextManager>();
            services.AddSingleton<IContextCompressorService, ContextCompressorService>();
            services.AddSingleton<ISkillService>(sp => new SkillServiceTestProxy());
            services.AddSingleton<IChatPersistenceService, ChatPersistenceServiceAdapter>();

            // ── 可测试的服务 ──
            services.AddSingleton<IEditPatchService>(sp =>
            {
                var apiService = sp.GetRequiredService<IDeepSeekApiService>();
                return new EditPatchService((DeepSeekApiService?)apiService 
                    ?? new DeepSeekApiService("test-key"));
            });

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// SkillService 测试代理 — 允许在测试中直接构造而无需访问真实文件系统。
        /// </summary>
        private class SkillServiceTestProxy : ISkillService
        {
            // 注意：SkillService 作为单例且构造函数为 private，此处需要反射或改为 internal
            // 实际测试中直接使用 SkillService.Instance 或通过 InternalsVisibleTo
            public event System.Action<int, string>? SkillsChanged;

            public async System.Threading.Tasks.Task<SkillDiscoveryResult> DiscoverSkillsAsync(
                string? solutionPath = null, bool forceRefresh = false)
            {
                await System.Threading.Tasks.Task.CompletedTask;
                return new SkillDiscoveryResult();
            }

            public SkillDefinition? ParseSkillFile(string filePath, SkillSource source)
                => null;

            public SkillDefinition? ParseSkillContent(string content, string filePath, SkillSource source)
                => null;

            public string? ReadSkillResource(SkillDefinition skill, string relativePath)
                => null;

            public SkillDefinition? FindSkill(string name, SkillDiscoveryResult? discoveryResult = null)
                => null;

            public string GenerateSkillsDiscoveryContext(SkillDiscoveryResult? discoveryResult = null)
                => string.Empty;

            public string GenerateUserInvocableSkillsList(SkillDiscoveryResult? discoveryResult = null)
                => string.Empty;

            public string GenerateSkillsSummary(SkillDiscoveryResult? discoveryResult = null)
                => string.Empty;

            public string? GetSkillsSummary()
                => null;

            public string? LoadSkillsSummaryFromDisk()
                => null;
        }
    }
}
