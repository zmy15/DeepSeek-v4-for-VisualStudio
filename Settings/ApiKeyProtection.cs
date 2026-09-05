using System;
using System.Security.Cryptography;
using System.Text;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 旧版 API Key 的 DPAPI 加密/解密逻辑。当前主路径是 Visual Studio 官方
    /// Credential Storage；本类型用于读取旧值迁移，以及凭据服务不可用时的降级保存。
    /// </summary>
    internal static class ApiKeyProtection
    {
        private const string Prefix = "dpapi1:";
        private const DataProtectionScope Scope = DataProtectionScope.CurrentUser;

        public static bool IsProtected(string? value)
            => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

        public static string Protect(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || IsProtected(value))
            {
                return value;
            }

            byte[] plainBytes = Encoding.UTF8.GetBytes(value);
            byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, Scope);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }

        public static string Unprotect(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !IsProtected(value))
            {
                return value;
            }

            try
            {
                byte[] protectedBytes = Convert.FromBase64String(value.Substring(Prefix.Length));
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, Scope);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                // ── 修复：解密失败时绝不能把密文（"dpapi1:..."）当作 API Key 返回 ──
                // DPAPI 以 CurrentUser 为作用域，若密钥在其它用户上下文（如管理员权限、
                // 不同 Windows 用户、域账户迁移）或凭据变更后被加密，解密会抛
                // CryptographicException。旧实现直接返回密文，导致 HttpClient 把
                // "dpapi1:..." 当作 Bearer Token 发送，服务端返回 401。
                // 这里返回空字符串，让上层走"未配置 API Key"分支并提示用户重新填写，
                // 而不是静默发送一个必然 401 的垃圾令牌。
                Logger.Error($"[Settings] API Key 解密失败，按未配置处理（请重新填写）: {ex.GetType().Name}: {ex.Message}", ex);
                return string.Empty;
            }
        }
    }
}
