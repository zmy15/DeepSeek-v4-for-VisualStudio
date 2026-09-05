using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Connected.CredentialStorage;
using System;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    internal enum ApiKeyKind
    {
        DeepSeek,
        Baidu,
        Bing,
    }

    internal interface IApiKeyStore
    {
        bool TryGet(ApiKeyKind kind, out string value);
        bool Set(ApiKeyKind kind, string value);
        bool Clear(ApiKeyKind kind);
    }

    /// <summary>
    /// Stores API keys in Visual Studio's official credential storage (the IDE keychain)
    /// instead of the ro/exportable DialogPage settings store.
    /// </summary>
    internal sealed class VisualStudioApiKeyStore : IApiKeyStore
    {
        private const string FeatureName = "DeepSeek Chat";

        private readonly IVsCredentialStorageService _service;

        internal VisualStudioApiKeyStore(IVsCredentialStorageService service)
        {
            _service = service;
        }

        internal static IApiKeyStore? Current { get; set; }

        internal static bool IsAvailable => Current != null;

        internal static async Task<VisualStudioApiKeyStore?> CreateAsync(IAsyncServiceProvider provider)
        {
            try
            {
                var service = await provider.GetServiceAsync(typeof(SVsCredentialStorageService))
                    as IVsCredentialStorageService;
                if (service == null)
                {
                    DiagnosticLog.Write("[ApiKeyStore] SVsCredentialStorageService unavailable; DPAPI fallback remains active");
                    return null;
                }

                DiagnosticLog.Write("[ApiKeyStore] Visual Studio credential storage acquired");
                return new VisualStudioApiKeyStore(service);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[ApiKeyStore] credential storage initialization failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public bool TryGet(ApiKeyKind kind, out string value)
        {
            value = string.Empty;
            try
            {
                var credential = _service.Retrieve(CreateKey(kind));
                if (credential == null)
                {
                    return false;
                }

                // TokenValue is the last known value. RefreshTokenValue is only required
                // when that snapshot is empty, as documented by IVsCredential.
                if (string.IsNullOrWhiteSpace(credential.TokenValue) &&
                    !credential.RefreshTokenValue())
                {
                    return false;
                }

                value = credential.TokenValue ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[ApiKeyStore] read {kind} failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public bool Set(ApiKeyKind kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Clear(kind);
            }

            try
            {
                _service.Add(CreateKey(kind), value);

                // Never erase the legacy DPAPI backup unless the keychain value can be
                // read back and matches exactly. This guards against transient storage
                // initialization failures during the first startup after migration.
                if (!TryGet(kind, out string storedValue) ||
                    !string.Equals(storedValue, value, StringComparison.Ordinal))
                {
                    DiagnosticLog.Write($"[ApiKeyStore] write {kind} failed readback verification");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[ApiKeyStore] write {kind} failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public bool Clear(ApiKeyKind kind)
        {
            try
            {
                var key = CreateKey(kind);
                if (_service.Retrieve(key) == null)
                {
                    return true;
                }

                return _service.Remove(key) && _service.Retrieve(key) == null;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[ApiKeyStore] clear {kind} failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private IVsCredentialKey CreateKey(ApiKeyKind kind) => kind switch
        {
            ApiKeyKind.Baidu => _service.CreateCredentialKey(
                FeatureName, "https://qianfan.baidubce.com", "ApiKey", "Bearer"),
            ApiKeyKind.Bing => _service.CreateCredentialKey(
                FeatureName, "https://api.bing.microsoft.com", "ApiKey", "SubscriptionKey"),
            _ => _service.CreateCredentialKey(
                FeatureName, "https://api.deepseek.com", "ApiKey", "Bearer"),
        };
    }
}
