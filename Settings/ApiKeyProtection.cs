using System;
using System.Security.Cryptography;
using System.Text;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 使用 Windows DPAPI 对 API Key 做用户级加密，避免设置存储中直接出现明文。
    /// 旧版本已保存的明文值会自动识别并兼容，等待下次保存时升级为密文。
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
                Logger.Warn($"[Settings] API Key 解密失败，保留已存储值: {ex.GetType().Name}");
                return value;
            }
        }
    }
}
