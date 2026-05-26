using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 代码内容合法性检测器。
    /// 防止 AI 将源文件内容替换为自然语言描述、TODO 注释或文档摘要，
    /// 确保写入磁盘的源文件包含实际可编译的代码。
    /// </summary>
    public static class CodeContentValidator
    {
        /// <summary>
        /// 语言检测规则：扩展名 → 必须在内容中匹配的关键词/模式列表。
        /// 内容需匹配至少一个模式才被视为"可能是代码"。
        /// </summary>
        private static readonly Dictionary<string, Regex[]> LanguagePatterns = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── C/C++ (.c, .cpp, .cc, .cxx, .h, .hpp, .hxx, .inl) ──
            ["c"] = CppPatterns,
            ["cpp"] = CppPatterns,
            ["cc"] = CppPatterns,
            ["cxx"] = CppPatterns,
            ["h"] = CppPatterns,
            ["hpp"] = CppPatterns,
            ["hxx"] = CppPatterns,
            ["inl"] = CppPatterns,

            // ── C# (.cs) ──
            ["cs"] = new[]
            {
                new Regex(@"\b(using|namespace|class|struct|record|interface|enum)\s", RegexOptions.Compiled),
                new Regex(@"\b(void|int|string|bool|double|float|long|var|async|await|return|yield)\b", RegexOptions.Compiled),
                new Regex(@"^\s*\[", RegexOptions.Compiled | RegexOptions.Multiline), // attribute
            },

            // ── Python (.py, .pyw, .pyx) ──
            ["py"] = PyPatterns,
            ["pyw"] = PyPatterns,
            ["pyx"] = PyPatterns,

            // ── JavaScript / TypeScript (.js, .ts, .jsx, .tsx, .mjs, .cjs, .mts, .cts) ──
            ["js"] = JsPatterns,
            ["ts"] = JsPatterns,
            ["jsx"] = JsPatterns,
            ["tsx"] = JsPatterns,
            ["mjs"] = JsPatterns,
            ["cjs"] = JsPatterns,
            ["mts"] = JsPatterns,
            ["cts"] = JsPatterns,

            // ── Java (.java) ──
            ["java"] = new[]
            {
                new Regex(@"\b(package|import|class|interface|enum|@Override|@Entity)\b", RegexOptions.Compiled),
                new Regex(@"\b(public|private|protected|static|final|abstract|synchronized)\b", RegexOptions.Compiled),
                new Regex(@"\b(void|int|String|boolean|long|double|float)\b", RegexOptions.Compiled),
            },

            // ── Go (.go) ──
            ["go"] = new[]
            {
                new Regex(@"\b(package|func|import)\s", RegexOptions.Compiled),
                new Regex(@"\b(type|struct|interface|map|chan)\b", RegexOptions.Compiled),
                new Regex(@"\b(var|const|return|if|for|go|defer)\b", RegexOptions.Compiled),
            },

            // ── Rust (.rs) ──
            ["rs"] = new[]
            {
                new Regex(@"\b(fn|struct|impl|use|mod|enum|trait)\b", RegexOptions.Compiled),
                new Regex(@"\b(let|mut|const|pub|unsafe|async|await)\b", RegexOptions.Compiled),
                new Regex(@"#!?\[", RegexOptions.Compiled), // attributes
            },

            // ── CMake (CMakeLists.txt, .cmake) ──
            ["cmake"] = CmakePatterns,

            // ── MSBuild / XML (.csproj, .vcxproj, .vbproj, .sln, .props, .targets, .xml, .xaml, .config) ──
            ["csproj"] = XmlPatterns,
            ["vcxproj"] = XmlPatterns,
            ["vbproj"] = XmlPatterns,
            ["sln"] = new[]
            {
                new Regex(@"Microsoft Visual Studio Solution File", RegexOptions.Compiled),
                new Regex(@"Project\(\""", RegexOptions.Compiled),
            },
            ["props"] = XmlPatterns,
            ["targets"] = XmlPatterns,
            ["xml"] = XmlPatterns,
            ["xaml"] = XmlPatterns,
            ["config"] = XmlPatterns,

            // ── JSON (.json) ──
            ["json"] = new[]
            {
                new Regex(@"^\s*[\[{]", RegexOptions.Compiled | RegexOptions.Multiline),
            },

            // ── YAML (.yml, .yaml) ──
            ["yml"] = YamlPatterns,
            ["yaml"] = YamlPatterns,

            // ── Shell (.sh, .bash, .zsh, .ps1, .psm1, .psd1) ──
            ["sh"] = ShellPatterns,
            ["bash"] = ShellPatterns,
            ["zsh"] = ShellPatterns,
            ["ps1"] = ShellPatterns,
            ["psm1"] = ShellPatterns,
            ["psd1"] = ShellPatterns,

            // ── Makefile ──
            ["makefile"] = MakefilePatterns,

            // ── SQL (.sql) ──
            ["sql"] = new[]
            {
                new Regex(@"\b(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|FROM|WHERE|JOIN)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            },

            // ── Ruby (.rb) ──
            ["rb"] = new[]
            {
                new Regex(@"\b(def|class|module|require|include|attr_)\b", RegexOptions.Compiled),
                new Regex(@"\b(do|end|if|unless|yield)\b", RegexOptions.Compiled),
            },

            // ── Swift (.swift) ──
            ["swift"] = new[]
            {
                new Regex(@"\b(import|class|struct|enum|protocol|extension|func|var|let)\b", RegexOptions.Compiled),
                new Regex(@"\b(override|init|guard|defer|throws|async|await)\b", RegexOptions.Compiled),
            },

            // ── Kotlin (.kt, .kts) ──
            ["kt"] = new[]
            {
                new Regex(@"\b(package|import|class|object|interface|fun|val|var)\b", RegexOptions.Compiled),
                new Regex(@"\b(override|data|sealed|suspend|companion)\b", RegexOptions.Compiled),
            },
            ["kts"] = new[]
            {
                new Regex(@"\b(package|import|class|object|interface|fun|val|var)\b", RegexOptions.Compiled),
            },
        };

        // ── Shared patterns ──
        private static readonly Regex[] CppPatterns = new[]
        {
            new Regex(@"#\s*include", RegexOptions.Compiled),
            new Regex(@"#\s*define|#\s*ifdef|#\s*ifndef|#\s*if\b|#\s*endif|#\s*pragma", RegexOptions.Compiled),
            new Regex(@"\b(namespace|class|struct|enum\s+(class\s+)?|template|typename)\b", RegexOptions.Compiled),
            new Regex(@"\b(void|int|char|bool|double|float|long|short|auto|const|constexpr|size_t)\b", RegexOptions.Compiled),
            new Regex(@"\b(return|if|for|while|switch|case|break|continue|try|catch|throw)\b", RegexOptions.Compiled),
            new Regex(@"::", RegexOptions.Compiled),
        };

        private static readonly Regex[] PyPatterns = new[]
        {
            new Regex(@"\b(def|class|import|from|async\s+def)\b", RegexOptions.Compiled),
            new Regex(@"\b(return|yield|if\s+__name__|with|raise|try|except|finally|lambda)\b", RegexOptions.Compiled),
            new Regex(@"^\s*@\w+", RegexOptions.Compiled | RegexOptions.Multiline), // decorator
        };

        private static readonly Regex[] JsPatterns = new[]
        {
            new Regex(@"\b(function|const|let|var|import|export|class|interface|type|enum)\b", RegexOptions.Compiled),
            new Regex(@"\b(return|if|for|while|switch|try|catch|throw|async|await|yield)\b", RegexOptions.Compiled),
            new Regex(@"=>", RegexOptions.Compiled),
        };

        private static readonly Regex[] CmakePatterns = new[]
        {
            new Regex(@"\bcmake_minimum_required\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bproject\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(add_executable|add_library|target_|set\s*\(|find_package|include_directories|file\s*\()", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(if|else|endif|foreach|endforeach)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        private static readonly Regex[] XmlPatterns = new[]
        {
            new Regex(@"<\?xml", RegexOptions.Compiled),
            new Regex(@"<Project\b", RegexOptions.Compiled),
            new Regex(@"<[A-Za-z_]\w*[\s/>]", RegexOptions.Compiled),
        };

        private static readonly Regex[] YamlPatterns = new[]
        {
            new Regex(@"^\s*[\w-]+\s*:", RegexOptions.Compiled | RegexOptions.Multiline),
            new Regex(@"^\s*-\s+", RegexOptions.Compiled | RegexOptions.Multiline),
        };

        private static readonly Regex[] ShellPatterns = new[]
        {
            new Regex(@"^#!", RegexOptions.Compiled | RegexOptions.Multiline),
            new Regex(@"\b(echo|export|source|cd|mkdir|rm|cp|mv|chmod|grep|sed|awk|git|docker)\b", RegexOptions.Compiled),
            new Regex(@"\b(if|then|else|elif|fi|for|while|do|done|case|esac)\b", RegexOptions.Compiled),
        };

        private static readonly Regex[] MakefilePatterns = new[]
        {
            new Regex(@"^[a-zA-Z_][\w./-]*\s*:", RegexOptions.Compiled | RegexOptions.Multiline),
            new Regex(@"^\t", RegexOptions.Compiled | RegexOptions.Multiline), // recipe tab
        };

        /// <summary>
        /// 不检查的文件扩展名（文档/配置/纯文本等，任何内容都可接受）。
        /// </summary>
        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".txt", ".rst", ".csv", ".tsv",
            ".htm", ".html", ".css", ".scss", ".less",
            ".ini", ".cfg", ".conf", ".toml", ".lock",
            ".gitignore", ".gitattributes", ".editorconfig",
            ".dockerignore", ".env",
            ".bat", ".cmd", // Windows batch
        };

        /// <summary>
        /// 检测文件内容是否像是真实的源代码（而非自然语言描述/文档摘要）。
        /// </summary>
        /// <param name="filePath">文件路径（用于通过扩展名判断语言）。</param>
        /// <param name="content">要写入的文件内容。</param>
        /// <returns>true 表示内容看起来是合法源代码；false 表示可能为描述性文本。</returns>
        public static bool IsProbablySourceCode(string filePath, string content)
        {
            if (string.IsNullOrEmpty(content))
                return true; // 空文件允许（如 __init__.py）

            string ext = Path.GetExtension(filePath)?.TrimStart('.') ?? string.Empty;

            // ── 特殊文件名处理 ──
            string fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                ext = "cmake";
            else if (string.Equals(fileName, "Makefile", StringComparison.OrdinalIgnoreCase))
                ext = "makefile";
            else if (string.Equals(fileName, "Dockerfile", StringComparison.OrdinalIgnoreCase))
                return true; // Dockerfile 格式多样，跳过检查

            // ── 跳过不需要检查的扩展名 ──
            if (SkippedExtensions.Contains("." + ext) || SkippedExtensions.Contains(fileName))
                return true;

            // ── 没有注册的语言 → 跳过检查（不阻塞未知类型）──
            if (!LanguagePatterns.TryGetValue(ext, out Regex[]? patterns) || patterns.Length == 0)
                return true;

            // ── 逐模式匹配 ──
            foreach (var regex in patterns)
            {
                if (regex.IsMatch(content))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 获取对人类友好的语言描述。
        /// </summary>
        public static string GetLanguageDescription(string filePath)
        {
            string ext = Path.GetExtension(filePath)?.TrimStart('.') ?? "";
            string fileName = Path.GetFileName(filePath);

            if (string.Equals(fileName, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                return "CMake";
            if (string.Equals(fileName, "Makefile", StringComparison.OrdinalIgnoreCase))
                return "Makefile";

            return ext.ToLowerInvariant() switch
            {
                "c" => "C",
                "cpp" or "cc" or "cxx" => "C++",
                "h" or "hpp" or "hxx" or "inl" => "C/C++ Header",
                "cs" => "C#",
                "py" or "pyw" or "pyx" => "Python",
                "js" or "mjs" or "cjs" => "JavaScript",
                "ts" or "mts" or "cts" => "TypeScript",
                "jsx" or "tsx" => "React (JSX/TSX)",
                "java" => "Java",
                "go" => "Go",
                "rs" => "Rust",
                "swift" => "Swift",
                "kt" or "kts" => "Kotlin",
                "rb" => "Ruby",
                "sql" => "SQL",
                "csproj" or "vcxproj" or "vbproj" or "props" or "targets" => "MSBuild",
                "sln" => "Solution",
                "xml" or "xaml" or "config" => "XML",
                "json" => "JSON",
                "yml" or "yaml" => "YAML",
                "sh" or "bash" or "zsh" => "Shell Script",
                "ps1" or "psm1" or "psd1" => "PowerShell",
                "cmake" => "CMake",
                _ => ext,
            };
        }
    }
}
