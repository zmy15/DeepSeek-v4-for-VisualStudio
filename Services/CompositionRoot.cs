using Microsoft.Extensions.DependencyInjection;
using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// DI 组合根 — 全局 IServiceProvider 访问点。
    /// 在 DeepSeek_v4_for_VisualStudioPackage.InitializeAsync 中初始化。
    /// </summary>
    public static class CompositionRoot
    {
        private static readonly object _lock = new();
        private static IServiceProvider? _serviceProvider;

        /// <summary>
        /// 全局 DI 容器。在 Build() 调用后可用。
        /// </summary>
        public static IServiceProvider Services
        {
            get
            {
                if (_serviceProvider == null)
                    throw new InvalidOperationException(
                        "CompositionRoot 尚未初始化。请确保在 Package.InitializeAsync 中调用了 CompositionRoot.Build()。");
                return _serviceProvider;
            }
        }

        /// <summary>
        /// 是否已初始化。
        /// </summary>
        public static bool IsBuilt => _serviceProvider != null;

        /// <summary>
        /// 构建 DI 容器。
        /// </summary>
        public static void Build()
        {
            lock (_lock)
            {
                if (_serviceProvider != null)
                    return; // 已构建

                var services = new ServiceCollection();
                services.AddDeepSeekServices();
                _serviceProvider = services.BuildServiceProvider();
            }
        }

        /// <summary>
        /// 获取指定类型的服务实例。
        /// </summary>
        public static T GetService<T>() where T : notnull
            => Services.GetRequiredService<T>();

        /// <summary>
        /// 尝试获取指定类型的服务实例。
        /// </summary>
        public static T? GetServiceOrDefault<T>() where T : class
            => Services.GetService<T>();
    }
}
