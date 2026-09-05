using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Utils;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 跨实例设置迁移（问题 2 修复）。
    ///
    /// 背景：VS 每个实例的 DialogPage 设置存放在各自的 privateregistry.bin 中，
    /// VS2022 的配置不会自动出现在 VS2026（新 hive 完全独立）。
    ///
    /// 策略（两阶段拆分）：
    ///   阶段一 ProbeBestSourceAsync —— 纯 IO（目录枚举 + RegLoadAppKey 只读挂载），
    ///     不触碰 DialogPage，可在后台线程执行；带自排除（跳过本实例活动 hive）
    ///     与单 hive 超时兜底。
    ///   阶段二 ApplyProbedValues —— 把探得值回填到目标 OptionsPage 并走正常
    ///     SaveSettingsToStorage 持久化；涉及 DialogPage，需在 UI 线程调用。
    /// </summary>
    internal static class SettingsMigration
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegLoadAppKey(string fileName, out IntPtr hKey, uint samDesired, uint options, uint reserved);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        private const uint KEY_READ = 0x20019;

        /// <summary>单个 hive 探测的超时上限。RegLoadAppKey 无内建超时，
        /// 对异常锁定的 hive 需主动放弃以防拖住加载链路。</summary>
        private const int PerHiveTimeoutMs = 3000;

        /// <summary>探测结果：来源 hive 目录名 + 解码后的设置键值。</summary>
        public sealed class MigrationProbeResult
        {
            public string SourceHive { get; init; } = string.Empty;
            public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 阶段一：枚举同机其他实例的 bin 并读取 DeepSeekOptionsPage 集合（纯 IO，可后台线程调用）。
        /// 返回最佳来源（含 ApiKey 的最新实例优先）；无有效来源返回 null。
        /// </summary>
        /// <param name="excludeHiveName">当前实例自身 hive 目录名（如 "18.0_xxxExp"）；
        /// 命中则跳过，避免对本实例正在使用的活动注册表做 RegLoadAppKey。null 表示不排除。</param>
        /// <param name="baseDirOverride">实例根目录覆盖（仅测试用；生产环境使用默认 %LOCALAPPDATA% 路径）。</param>
        public static async Task<MigrationProbeResult?> ProbeBestSourceAsync(
            string? excludeHiveName, string? baseDirOverride = null)
        {
            try
            {
                var baseDir = baseDirOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "VisualStudio");

                foreach (var bin in EnumerateCandidateBins(baseDir, excludeHiveName))
                {
                    DiagnosticLog.Write($"[Settings] 迁移探测: {bin}");
                    var values = await WithTimeoutAsync(() => TryReadValues(bin), PerHiveTimeoutMs);
                    if (values == null || values.Count == 0)
                    {
                        Logger.Info("[Settings] 迁移探测: 未找到有效 DeepSeekOptionsPage 集合");
                        continue;
                    }

                    return new MigrationProbeResult
                    {
                        SourceHive = Path.GetFileName(Path.GetDirectoryName(bin)) ?? string.Empty,
                        Values = values,
                    };
                }
                Logger.Info("[Settings] 迁移结束：无可迁移来源");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[Settings] 迁移探测失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 枚举候选 privateregistry.bin（按最后写入时间倒序）：
        /// 排除 Exp 后缀 hive（正式实例优先）与当前实例自身 hive（自排除活动注册表）。
        /// </summary>
        internal static IEnumerable<string> EnumerateCandidateBins(string baseDir, string? excludeHiveName)
        {
            return Directory.GetDirectories(baseDir)
                .Where(d => !d.EndsWith("Exp", StringComparison.OrdinalIgnoreCase)) // 正式实例优先
                .Where(d => excludeHiveName == null ||
                            !string.Equals(Path.GetFileName(d), excludeHiveName, StringComparison.OrdinalIgnoreCase)) // 自排除
                .Select(d => Path.Combine(d, "privateregistry.bin"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTime);
        }

        /// <summary>
        /// 判定是否存在任意候选来源（区别于"探测失败/被锁"的瞬时失败）。
        /// P1-5a：用于区分"确无来源"（definitive ⇒ 可固化一次性迁移标志）与
        /// "暂不可用/超时"（transient ⇒ 不得固化，下次启动应重试）。
        /// 目录枚举失败视为"不确定"，返回 false，避免据此固化标志导致永不再迁移。
        /// </summary>
        public static bool HasNoCandidateSource(string? excludeHiveName, string? baseDirOverride = null)
        {
            try
            {
                var baseDir = baseDirOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "VisualStudio");
                if (!Directory.Exists(baseDir))
                    return true; // 根目录不存在 ⇒ 确无来源
                return !EnumerateCandidateBins(baseDir, excludeHiveName).Any();
            }
            catch
            {
                return false; // 枚举失败视为"不确定"，不得据此固化标志
            }
        }

        /// <summary>阶段二（需在 UI 线程调用）：将探得值按属性名回填到目标并持久化。返回是否发生迁移。</summary>
        public static bool ApplyProbedValues(DeepSeekOptionsPage target, MigrationProbeResult probed)
        {
            try
            {
                int applied = Apply(target, probed.Values);
                if (applied > 0)
                {
                    target.SaveSettingsToStorage();
                    DiagnosticLog.Write($"[Settings] 已从 {probed.SourceHive} 迁移 {applied} 项设置");
                    return true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[Settings] 迁移应用失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>带超时的受控执行：超时后放弃等待（后台任务自然消亡），返回 default。</summary>
        internal static async Task<T?> WithTimeoutAsync<T>(Func<T> func, int timeoutMs)
        {
            Task<T?> readTask = Task.Run(() => (T?)func());
            Task completed = await Task.WhenAny(readTask, Task.Delay(timeoutMs));
            if (completed != readTask)
            {
                DiagnosticLog.Write($"[Settings] 迁移探测超时({timeoutMs}ms)");
                return default;
            }
            return readTask.Result;
        }



        /// <summary>解码 SettingsManager 存储编码：&lt;flag&gt;*&lt;Type&gt;*&lt;value&gt;。</summary>
        private static string DecodeStoredValue(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var m = Regex.Match(raw, @"^(\d+)\*([^*]+)\*(.*)$", RegexOptions.Singleline);
            return m.Success ? m.Groups[3].Value : raw;
        }
        private static Dictionary<string, string>? TryReadValues(string binPath)
        {
            IntPtr hKey = IntPtr.Zero;
            try
            {
                int err = RegLoadAppKey(binPath, out hKey, KEY_READ, 0, 0);
                if (err != 0 || hKey == IntPtr.Zero)
                {
                    DiagnosticLog.Write($"[Settings] RegLoadAppKey 失败: win32err={err}");
                    return null;
                }

                // SafeRegistryHandle 拥有句柄，负责最终释放（不再单独 RegCloseKey）
                using var root = RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(hKey, true));
                var page = FindKeyRecursive(root, "DeepSeekOptionsPage", maxDepth: 10);
                if (page == null) return null;

                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (page.GetValueNames().Length == 0) return null;
                foreach (var name in page.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (page.GetValue(name) is string s && !string.IsNullOrEmpty(s))
                        dict[name] = DecodeStoredValue(s);   // 剥离 0*Type* 前缀；DPAPI 密文同用户可解
                }
                return dict.ContainsKey("ApiKey") ? dict : null; // 必须含 Key 才视为有效来源
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[Settings] 读取 {binPath} 失败: {ex.Message}");
                return null;
            }
        }

        private static RegistryKey? FindKeyRecursive(RegistryKey root, string nameHint, int maxDepth)
        {
            if (maxDepth < 0) return null;
            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.IndexOf(nameHint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var k = root.OpenSubKey(sub);
                    if (k != null && k.GetValueNames().Length > 0) return k;
                    k?.Dispose();
                }
            }
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) continue;
                var found = FindKeyRecursive(k, nameHint, maxDepth - 1);
                if (found != null) return found;
            }
            return null;
        }

        private static int Apply(DeepSeekOptionsPage target, Dictionary<string, string> values)
        {
            int applied = 0;
            var props = typeof(DeepSeekOptionsPage).GetProperties()
                .Where(p => p.CanRead && p.CanWrite);

            foreach (var p in props)
            {
                if (!values.TryGetValue(p.Name, out var raw)) continue;
                try
                {
                    if (p.PropertyType == typeof(string))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        // 迁移源值可能为 DPAPI 密文（dpapi1:<base64>），需先解密
                        var plain = ApiKeyProtection.Unprotect(raw);
                        p.SetValue(target, plain);
                        applied++;
                    }
                    else if (p.PropertyType == typeof(bool))
                    {
                        p.SetValue(target, bool.Parse(raw));
                        applied++;
                    }
                    else if (p.PropertyType == typeof(int))
                    {
                        p.SetValue(target, int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture));
                        applied++;
                    }
                    else if (p.PropertyType == typeof(double))
                    {
                        p.SetValue(target, double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture));
                        applied++;
                    }
                }
                catch { /* 单项失败跳过 */ }
            }
            return applied;
        }
    }
}
