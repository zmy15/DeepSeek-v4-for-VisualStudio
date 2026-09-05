using DeepSeek_v4_for_VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Connected.CredentialStorage;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class VisualStudioApiKeyStoreTests
{
    [Fact]
    public void SetAndTryGet_RoundTripsCredential()
    {
        var store = new VisualStudioApiKeyStore(new FakeCredentialStorageService());

        store.Set(ApiKeyKind.DeepSeek, "sk-test").Should().BeTrue();
        store.TryGet(ApiKeyKind.DeepSeek, out string value).Should().BeTrue();

        value.Should().Be("sk-test");
    }

    [Fact]
    public void Clear_RemovesCredential()
    {
        var store = new VisualStudioApiKeyStore(new FakeCredentialStorageService());
        store.Set(ApiKeyKind.Baidu, "baidu-test");

        store.Clear(ApiKeyKind.Baidu).Should().BeTrue();
        store.TryGet(ApiKeyKind.Baidu, out string value).Should().BeFalse();
        value.Should().BeEmpty();
    }

    [Fact]
    public void DifferentApiKeyKinds_UseIndependentCredentials()
    {
        var store = new VisualStudioApiKeyStore(new FakeCredentialStorageService());

        store.Set(ApiKeyKind.DeepSeek, "deepseek-key");
        store.Set(ApiKeyKind.Baidu, "baidu-key");
        store.Set(ApiKeyKind.Bing, "bing-key");

        store.TryGet(ApiKeyKind.DeepSeek, out string deepSeek).Should().BeTrue();
        store.TryGet(ApiKeyKind.Baidu, out string baidu).Should().BeTrue();
        store.TryGet(ApiKeyKind.Bing, out string bing).Should().BeTrue();

        deepSeek.Should().Be("deepseek-key");
        baidu.Should().Be("baidu-key");
        bing.Should().Be("bing-key");
    }

    [Fact]
    public void TryGet_NonEmptyTokenValue_DoesNotRequireRefresh()
    {
        var service = new FakeCredentialStorageService
        {
            RefreshTokenValueResult = false,
        };
        var store = new VisualStudioApiKeyStore(service);
        store.Set(ApiKeyKind.DeepSeek, "sk-already-cached");

        service.RefreshTokenValueResult = false;

        store.TryGet(ApiKeyKind.DeepSeek, out string value).Should().BeTrue();
        value.Should().Be("sk-already-cached");
    }

    [Fact]
    public void Set_ReadBackMismatch_ReturnsFalse()
    {
        var service = new FakeCredentialStorageService
        {
            PersistReadBack = false,
        };
        var store = new VisualStudioApiKeyStore(service);

        store.Set(ApiKeyKind.DeepSeek, "sk-write-only").Should().BeFalse();
    }

    private sealed class FakeCredentialStorageService : IVsCredentialStorageService
    {
        private readonly Dictionary<string, string> _credentials = new(StringComparer.OrdinalIgnoreCase);

        public bool RefreshTokenValueResult { get; set; } = true;

        public bool PersistReadBack { get; set; } = true;

        public IVsCredential Add(IVsCredentialKey key, string credentialValue)
        {
            _credentials[GetId(key)] = credentialValue;
            return new FakeCredential(key, credentialValue, RefreshTokenValueResult);
        }

        public IVsCredentialKey CreateCredentialKey(
            string featureName,
            string resource,
            string userName,
            string type)
            => new FakeCredentialKey(featureName, resource, userName, type);

        public bool Remove(IVsCredentialKey key)
            => _credentials.Remove(GetId(key));

        public IVsCredential Retrieve(IVsCredentialKey key)
            => PersistReadBack && _credentials.TryGetValue(GetId(key), out string value)
                ? new FakeCredential(key, value, RefreshTokenValueResult)
                : null!;

        public IEnumerable<IVsCredential> RetrieveAll(string featureName)
            => Enumerable.Empty<IVsCredential>();

        private static string GetId(IVsCredentialKey key)
            => $"{key.FeatureName}|{key.Resource}|{key.UserName}|{key.Type}";
    }

    private sealed class FakeCredentialKey : IVsCredentialKey
    {
        public FakeCredentialKey(
            string featureName,
            string resource,
            string userName,
            string type)
        {
            FeatureName = featureName;
            Resource = resource;
            UserName = userName;
            Type = type;
        }

        public string FeatureName { get; }
        public string Resource { get; }
        public string UserName { get; }
        public string Type { get; }
    }

    private sealed class FakeCredential : IVsCredential
    {
        private readonly string _tokenValue;
        private readonly bool _refreshTokenValueResult;

        public FakeCredential(IVsCredentialKey key, string tokenValue, bool refreshTokenValueResult)
        {
            FeatureName = key.FeatureName;
            Resource = key.Resource;
            UserName = key.UserName;
            Type = key.Type;
            _tokenValue = tokenValue;
            _refreshTokenValueResult = refreshTokenValueResult;
        }

        public string FeatureName { get; }
        public string Resource { get; }
        public string UserName { get; }
        public string Type { get; }
        public string TokenValue => _tokenValue;

        public string GetProperty(string name) => string.Empty;

        public bool RefreshTokenValue() => _refreshTokenValueResult;

        public bool SetProperty(string name, string value) => true;

        public void SetTokenValue(string value)
        {
        }
    }
}
