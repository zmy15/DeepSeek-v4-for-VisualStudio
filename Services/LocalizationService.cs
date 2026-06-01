using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 国际化/本地化服务。
    /// 根据用户系统语言或手动设置，加载对应的语言资源文件，
    /// 支持界面文字、提示词、输出信息的多语言切换。
    /// 用户可通过 工具→选项→DeepSeek Chat 自定义语言偏好。
    /// </summary>
    public sealed class LocalizationService
    {
        #region Singleton

        private static readonly Lazy<LocalizationService> _instance =
            new(() => new LocalizationService());

        public static LocalizationService Instance => _instance.Value;

        private LocalizationService()
        {
            // 自动加载默认语言，确保在测试环境或未显式调用 Initialize() 时也能正常工作
            Reload();
        }

        #endregion

        #region Constants

        /// <summary>
        /// 支持的语言列表。
        /// </summary>
        public static readonly IReadOnlyList<LanguageInfo> SupportedLanguages = new[]
        {
            new LanguageInfo("zh-CN", "中文（简体）", "Chinese (Simplified)"),
            new LanguageInfo("en",    "English",        "English"),
        };

        /// <summary>
        /// 默认语言（中文）。
        /// </summary>
        private const string DefaultLanguage = "zh-CN";

        /// <summary>
        /// 用户自定义语言文件扩展名。
        /// </summary>
        private const string CustomFileExtension = ".user.json";

        #endregion

        #region Fields

        private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
        private string _currentLanguage = DefaultLanguage;
        private readonly object _lock = new();

        #endregion

        #region Properties

        /// <summary>
        /// 当前使用的语言代码（如 "zh-CN", "en"）。
        /// </summary>
        public string CurrentLanguage
        {
            get => _currentLanguage;
            private set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    Reload();
                    LanguageChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// 语言变更事件，供 UI 组件订阅以刷新显示。
        /// </summary>
        public event EventHandler? LanguageChanged;

        #endregion

        #region Initialization

        /// <summary>
        /// 初始化本地化服务。自动检测系统语言，并加载对应资源。
        /// 应在 VS 包初始化时调用。
        /// </summary>
        /// <param name="userLanguageOverride">
        /// 用户手动选择的语言代码。为 null 或 "auto" 时自动检测系统语言。
        /// </param>
        public void Initialize(string? userLanguageOverride = null)
        {
            string detectedLanguage = DetectSystemLanguage();

            string targetLanguage;
            if (string.IsNullOrEmpty(userLanguageOverride) ||
                string.Equals(userLanguageOverride, "auto", StringComparison.OrdinalIgnoreCase))
            {
                targetLanguage = detectedLanguage;
            }
            else
            {
                targetLanguage = NormalizeLanguageCode(userLanguageOverride);
            }

            CurrentLanguage = targetLanguage;

            System.Diagnostics.Debug.WriteLine(
                $"[I18n] Initialized: system={detectedLanguage}, target={targetLanguage}, loaded={_strings.Count} keys");
        }

        /// <summary>
        /// 切换语言并重新加载资源。
        /// </summary>
        public void SetLanguage(string languageCode)
        {
            CurrentLanguage = NormalizeLanguageCode(languageCode);
        }

        /// <summary>
        /// 重新加载当前语言的资源文件。
        /// </summary>
        public void Reload()
        {
            lock (_lock)
            {
                _strings.Clear();

                // 1. 加载默认语言（中文）作为回退
                LoadJsonFile(DefaultLanguage, isFallback: true);

                // 2. 如果当前语言不是默认语言，叠加当前语言
                if (!string.Equals(CurrentLanguage, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    LoadJsonFile(CurrentLanguage, isFallback: false);
                }

                // 3. 加载用户自定义覆盖文件
                LoadJsonFile(CurrentLanguage + CustomFileExtension, isFallback: false, optional: true);
            }
        }

        #endregion

        #region String Access

        /// <summary>
        /// 获取指定键的本地化字符串。
        /// </summary>
        /// <param name="key">字符串键。</param>
        /// <returns>本地化后的字符串；如果键不存在，返回键本身（用方括号包裹）。</returns>
        public string this[string key]
        {
            get
            {
                if (key == null) return string.Empty;
                lock (_lock)
                {
                    if (_strings.TryGetValue(key, out var value))
                        return value;
                }
                return $"[{key}]";
            }
        }

        /// <summary>
        /// 获取格式化后的本地化字符串。
        /// </summary>
        /// <param name="key">字符串键。</param>
        /// <param name="args">格式化参数。</param>
        public string Format(string key, params object?[] args)
        {
            var template = this[key];
            if (args == null || args.Length == 0) return template;
            try { return string.Format(template, args); }
            catch (FormatException) { return template; }
        }

        /// <summary>
        /// 尝试获取本地化字符串。如果键不存在，返回 false。
        /// </summary>
        public bool TryGet(string key, out string value)
        {
            lock (_lock)
            {
                return _strings.TryGetValue(key, out value!);
            }
        }

        /// <summary>
        /// 获取所有已加载的字符串键（用于调试）。
        /// </summary>
        public IReadOnlyCollection<string> Keys
        {
            get { lock (_lock) return _strings.Keys; }
        }

        #endregion

        #region Language Detection

        /// <summary>
        /// 检测用户系统 UI 语言，映射到支持的语言代码。
        /// </summary>
        private static string DetectSystemLanguage()
        {
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                string name = culture.Name.ToLowerInvariant();

                // 中文系列 → zh-CN
                if (name.StartsWith("zh"))
                    return "zh-CN";

                // 其他语言暂时返回英文
                return "en";
            }
            catch
            {
                return DefaultLanguage;
            }
        }

        /// <summary>
        /// 标准化语言代码：确保映射到支持的语言。
        /// </summary>
        private static string NormalizeLanguageCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return DefaultLanguage;

            code = code.Trim().ToLowerInvariant();

            // 中文系列 → zh-CN
            if (code.StartsWith("zh"))
                return "zh-CN";

            // 英文 → en
            if (code.StartsWith("en"))
                return "en";

            // 尝试精确匹配
            foreach (var lang in SupportedLanguages)
            {
                if (string.Equals(lang.Code, code, StringComparison.OrdinalIgnoreCase))
                    return lang.Code;
            }

            return DefaultLanguage;
        }

        #endregion

        #region File Loading

        /// <summary>
        /// 从 JSON 文件加载字符串资源。
        /// 如果文件不存在，尝试从嵌入资源加载（默认语言）。
        /// </summary>
        private void LoadJsonFile(string languageCode, bool isFallback, bool optional = false)
        {
            string? json = null;
            string filePath = GetResourceFilePath(languageCode);
            if (!File.Exists(filePath))
            {
                // ── 回退：尝试从嵌入资源加载默认语言 ──
                if (isFallback && string.Equals(languageCode, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    json = LoadFromEmbeddedResource(languageCode);
                }

                if (json == null)
                {
                    if (!optional && !isFallback)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[I18n] Warning: Resource file not found: {filePath}");
                    }
                    return;
                }
            }
            else
            {
                try
                {
                    json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[I18n] Error reading {filePath}: {ex.Message}");
                    return;
                }
            }

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (dict != null)
                {
                    foreach (var kvp in dict)
                    {
                        // isFallback: 只在键不存在时添加
                        if (isFallback && _strings.ContainsKey(kvp.Key))
                            continue;

                        _strings[kvp.Key] = kvp.Value;
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[I18n] Loaded {dict.Count} keys from: {languageCode}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[I18n] Error loading {filePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// 从嵌入资源加载语言 JSON（用于文件系统不可用时的回退，如 CI/测试环境）。
        /// </summary>
        private static string? LoadFromEmbeddedResource(string languageCode)
        {
            try
            {
                var assembly = typeof(LocalizationService).Assembly;
                // 嵌入资源名称: <RootNamespace>.Resources.Locales.<languageCode>.json
                string resourceName = $"{assembly.GetName().Name}.Resources.Locales.{languageCode}.json";
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[I18n] Embedded resource not found: {resourceName}");
                    return null;
                }
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[I18n] Error loading embedded resource: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取资源文件的绝对路径。
        /// 按优先级搜索多个位置：程序集目录 → 向上遍历查找 → 工作目录。
        /// </summary>
        private static string GetResourceFilePath(string languageCode)
        {
            string relativePath = Path.Combine("Resources", "Locales", $"{languageCode}.json");

            // 1. 尝试程序集所在目录（VS 扩展运行时 / 本地调试）
            try
            {
                var assemblyLocation = typeof(LocalizationService).Assembly.Location;
                var assemblyDir = Path.GetDirectoryName(assemblyLocation);
                if (assemblyDir != null)
                {
                    string filePath = Path.Combine(assemblyDir, relativePath);
                    if (File.Exists(filePath))
                        return filePath;

                    // 2. 从程序集目录向上查找（最多 5 级），适配测试项目等不同输出目录结构
                    string? searchDir = assemblyDir;
                    for (int i = 0; i < 5 && searchDir != null; i++)
                    {
                        string candidate = Path.Combine(searchDir, relativePath);
                        if (File.Exists(candidate))
                            return candidate;
                        searchDir = Path.GetDirectoryName(searchDir);
                    }
                }
            }
            catch
            {
                // 回退到后续方法
            }

            // 3. 尝试当前工作目录
            string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
            if (File.Exists(cwdPath))
                return cwdPath;

            // 4. 最终回退：返回程序集目录下的相对路径（即使文件不存在，让调用方处理）
            try
            {
                var assemblyDir = Path.GetDirectoryName(typeof(LocalizationService).Assembly.Location);
                if (assemblyDir != null)
                    return Path.Combine(assemblyDir, relativePath);
            }
            catch { }

            return cwdPath;
        }

        #endregion
    }

    /// <summary>
    /// 支持的语言信息。
    /// </summary>
    public sealed class LanguageInfo
    {
        public string Code { get; }
        public string NativeName { get; }
        public string EnglishName { get; }

        public LanguageInfo(string code, string nativeName, string englishName)
        {
            Code = code;
            NativeName = nativeName;
            EnglishName = englishName;
        }

        public override string ToString() => $"{NativeName} ({EnglishName})";
    }
}
