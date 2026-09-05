using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// Dangerous command kind, used by run_in_terminal interception.
    /// </summary>
    public enum DangerousCommandKind
    {
        None = 0,
        SystemDestruction,
        Shutdown,
        CriticalDelete,
        AccountTampering,
        CredentialTheft,
        RemoteCodeExecution,
        DisableSecurity,
        RegistryTampering,
        PythonInlineDanger,
    }

    /// <summary>
    /// run_in_terminal 工具 — 在终端中运行命令。
    ///  编译/构建命令会被拦截，提示使用 build_solution 工具。
    /// </summary>
    public class RunInTerminalTool : BuiltInToolBase
    {
        /// <summary>同步模式最大等待时间（防止进程僵死导致 Agent 永久卡住）</summary>
        private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 当前调用 Agent 类型（由 BaseAgent 在执行前设置，用于运行时权限校验）。
        /// AskAgent / ExploreAgent 只能执行不修改文件的终端命令。
        /// P1-7：用 AsyncLocal 隔离并发 Agent，避免"类级静态可写"被同时运行的
        /// 其他 Agent 覆盖，导致只读判定被静默绕过或反向误拦。
        /// </summary>
        private static readonly System.Threading.AsyncLocal<AgentType?> CurrentAgentTypeAsyncLocal = new();
        public static AgentType? CurrentAgentType
        {
            get => CurrentAgentTypeAsyncLocal.Value;
            set => CurrentAgentTypeAsyncLocal.Value = value;
        }

        /// <summary>是否为只读 Agent（Ask/Explore）——禁止终端修改文件。</summary>
        private static bool IsReadOnlyAgent => CurrentAgentType is AgentType.Ask or AgentType.Explore;

        /// <summary>检测到的 Python 运行环境信息。</summary>
        internal sealed class PythonEnvironment
        {
            public string Executable { get; set; } = "";
            public string Version { get; set; } = "";
        }

        /// <summary>Python 运行环境缓存（只探测一次，避免每次命令都启动子进程）。</summary>
        private static readonly Lazy<PythonEnvironment?> PythonEnvironmentLazy =
            new(DetectPythonEnvironment, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// 拼接敏感词片段，避免编译产物中出现可直接被杀软识别的静态特征签名。
        /// </summary>
        private static string JoinParts(params string[] parts) => string.Concat(parts);

        private static readonly string CredentialToolPattern =
            @"\b(?:" + JoinParts("mimi", "katz") + @"|" + JoinParts("secret", "s", "dump") + @"|"
            + JoinParts("crack", "mapex", "ec") + @"|" + JoinParts("pw", "dump") + @"|"
            + JoinParts("kerb", "eroast") + @"|" + JoinParts("hash", "cat") + @"|"
            + JoinParts("ch", "ntpw") + @")\b";

        private static readonly string CredentialDumpPattern =
            @"\b" + JoinParts("pro", "cdump") + @"\b[^\r\n;|]{0,80}\b" + JoinParts("lsa", "ss") + @"\b|"
            + JoinParts("com", "svcs.dll");

        /// <summary>
        /// 危险命令拦截规则（有序，先匹配的分类优先生效）。
        /// </summary>
        private static readonly (System.Text.RegularExpressions.Regex Pattern, DangerousCommandKind Kind)[] DangerousCommandPatterns =
        {
            // ── 关机 / 重启 / 注销 ──
            (new System.Text.RegularExpressions.Regex(
                @"\bshutdown\b(?=[^\r\n;|]{0,80}(?:/s|/r|/p|/sg|/rs|-s|-r|-p)\b)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.Shutdown),
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:stop-computer|restart-computer|logoff)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.Shutdown),

            // ── 磁盘 / 引导区破坏 ──
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:format|format\.com)\b[^\r\n;|]{0,80}[A-Za-z]:",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.SystemDestruction),
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:diskpart|bcdedit|bootrec|sdelete)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.SystemDestruction),

            // ── 删除系统 / 关键目录 ──
            (new System.Text.RegularExpressions.Regex(
                @"(?<![\w\\/])(?:remove-item|erase|rmdir|rd|rm|del(?:\.exe)?)\b[^\r\n;|]{0,200}\b(?:C:\\windows(?:\\(?:system32|syswow64))?|C:\\users\b|C:\\program\s+files\b|C:\\programdata\b|C:\\windows\.old\b|C:\\\$recycle\.bin\b|%systemroot%\b|%windir%\b|\$env:systemroot\b|\$env:windir\b|C:\\pagefile\.sys\b|C:\\hiberfil\.sys\b)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.CriticalDelete),
            (new System.Text.RegularExpressions.Regex(
                @"(?<![\w\\/])(?:remove-item|rmdir|rd|rm)\b[^\r\n;|]{0,120}(?:-recurse|-force|/s|/q)\b[^\r\n;|]{0,160}\b[A-Za-z]:\s*(?:\\?|;|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.CriticalDelete),
            (new System.Text.RegularExpressions.Regex(
                @"\brm\b[^\r\n;|]{0,80}-rf\b[^\r\n;|]{0,120}(?:\s|['""])?/",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.CriticalDelete),

            // ── 账户 / 系统服务 ──
            (new System.Text.RegularExpressions.Regex(
                @"(?<![\w\\/])net\b\s+(?:user|localgroup)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.AccountTampering),
            (new System.Text.RegularExpressions.Regex(
                @"(?<![\w\\/])sc\b\s+delete\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.AccountTampering),

            // ── 凭据窃取 ──
            (new System.Text.RegularExpressions.Regex(
                CredentialToolPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.CredentialTheft),
            (new System.Text.RegularExpressions.Regex(
                CredentialDumpPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.CredentialTheft),
            (new System.Text.RegularExpressions.Regex(
                @"\breg\b\s+(?:save|restore)\b[^\r\n;|]{0,120}\bHKLM\\(?:SAM|SYSTEM|SECURITY)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.CredentialTheft),

            // ── 下载并执行远程代码 / 混淆执行 ──
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:iex|invoke-expression)\b(?=[^\r\n;|]{0,200}\b(?:downloadstring|downloadfile|net\.webclient|http|https|certutil|bitsadmin|start-bits)\b)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:iwr|invoke-webrequest|curl|wget|invoke-restmethod)\b[^\r\n;|]{0,200}\|\s*(?:iex|invoke-expression|powershell|pwsh|python|py)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\bcertutil\b[^\r\n;|]{0,60}-urlcache\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:bitsadmin|start-bitstransfer)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\bmshta\b[^\r\n;|]{0,120}(?:http|javascript)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\bregsvr32\b[^\r\n;|]{0,120}(?:http|https|scrobj|fromscript)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\brun dll32\b[^\r\n;|]{0,120}(?:javascript|http|https)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:cscript|wscript)\b[^\r\n;|]{0,120}(?:\.sct\b|http|https)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\bwmic\b[^\r\n;|]{0,120}\bprocess\b[^\r\n;|]{0,60}\bcall\b[^\r\n;|]{0,60}\bcreate\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:invoke-wmimethod|invoke-cimethod)\b[^\r\n;|]{0,120}\bwin32_process\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"(?<!\w)-(?:enc|encodedcommand)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),
            (new System.Text.RegularExpressions.Regex(
                @"\bschtasks\b[^\r\n;|]{0,160}\b(?:/create|/change)\b[^\r\n;|]{0,300}(?:https?://|/ru\s+system|/RL\s+high)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RemoteCodeExecution),

            // ── 关闭安全防护 ──
            (new System.Text.RegularExpressions.Regex(
                @"\bset-mppreference\b(?=[^\r\n;|]{0,200}(?:disablerealtimemonitoring|disableioavprotection|disablescriptscanning|disablebehaviormonitoring|disableservice))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.DisableSecurity),
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:set-netfirewallprofile|netsh\s+advfirewall\s+set)\b(?=[^\r\n;|]{0,200}\b(?:state\s+off|enabled\s+false)\b)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.DisableSecurity),
            (new System.Text.RegularExpressions.Regex(
                @"\bnetsh\b[^\r\n;|]{0,100}\bfirewall\b[^\r\n;|]{0,100}\bopmode\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.DisableSecurity),
            (new System.Text.RegularExpressions.Regex(
                @"\b(?:stop-service|sc\s+stop)\b[^\r\n;|]{0,100}\b(?:windefend|wscsvc|mpssvc|wuauserv|securityhealthservice|securitycenter)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.DisableSecurity),
            (new System.Text.RegularExpressions.Regex(
                @"\bset-executionpolicy\b(?=[^\r\n;|]{0,200}\blocalmachine\b)[^\r\n;|]{0,200}\b(?:bypass|unrestricted)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.DisableSecurity),

            // ── 注册表篡改 ──
            (new System.Text.RegularExpressions.Regex(
                @"(?<![\w\\/])reg\b\s+delete\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RegistryTampering),
            (new System.Text.RegularExpressions.Regex(
                @"(?<![\w\\/])reg\b\s+(?:add|import|load|restore)\b[^\r\n;|]{0,80}\bHKLM\\",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RegistryTampering),
            (new System.Text.RegularExpressions.Regex(
                @"\bregedit\b[^\r\n;|]{0,40}/s\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                DangerousCommandKind.RegistryTampering),
        };

        /// <summary>
        /// 探测本机可用的 Python 解释器（python / python3 / py 启动器）。
        /// </summary>
        internal static PythonEnvironment? DetectPythonEnvironment()
        {
            foreach (string candidate in new[] { "python", "python3", "py" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "-V",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var process = Process.Start(psi);
                    if (process == null) continue;

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { }
                        try { Task.WaitAll(stdoutTask, stderrTask); } catch { }
                        continue;
                    }

                    string version =
                        (stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty) +
                        (stderrTask.IsCompleted ? stderrTask.Result : string.Empty);
                    version = version.Trim();
                    if (version.StartsWith("Python ", StringComparison.OrdinalIgnoreCase) && version.Length > 7)
                        return new PythonEnvironment { Executable = candidate, Version = version };
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 判断命令是否为 Python 相关命令（python / python3 / pythonw / py / pip）。
        /// 避免把 pytest 等相似前缀误判为 Python。
        /// </summary>
        internal static bool IsPythonCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            string trimmed = command.Trim().TrimStart('&', ' ', '\t');
            foreach (string exe in new[] { "python3", "pythonw3", "pythonw", "python", "pip3", "pip", "py" })
            {
                if (!trimmed.StartsWith(exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.Length == exe.Length) return true;

                char next = trimmed[exe.Length];
                if (next != ' ' && next != '\t' && next != '.') continue;
                if (next != '.') return true;

                // 仅当后缀是 .exe 且后面是空白/结束时才认为是 python.exe
                string rest = trimmed.Substring(exe.Length + 1);
                if (!rest.StartsWith("exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (rest.Length == 3 || rest[3] == ' ' || rest[3] == '\t') return true;
            }
            return false;
        }

        /// <summary>
        /// 拦截危险命令，返回其危险分类；安全命令返回 None。
        /// </summary>
        internal static DangerousCommandKind DetectDangerousCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return DangerousCommandKind.None;

            var inlineDanger = DetectPythonInlineDanger(command);
            if (inlineDanger != DangerousCommandKind.None) return inlineDanger;

            foreach (var (pattern, kind) in DangerousCommandPatterns)
            {
                if (pattern.IsMatch(command)) return kind;
            }
            return DangerousCommandKind.None;
        }

        /// <summary>
        /// 只读 Agent（Ask/Explore）的终端限制：检测命令是否会修改文件。
        /// 覆盖文件写入/创建/删除/移动/复制/重命名命令、输出重定向、交互式编辑器、
        /// git 写操作、sed -i 原地编辑，以及 curl/wget 下载写文件。
        /// </summary>
        internal static bool DetectFileEditingCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            string c = command.Trim();

            // ── 输出重定向写入文件（`>` 覆盖 / `>>` 追加）──
            // PowerShell 中裸 `>` 是写文件重定向；比较运算用 -gt/-lt，无歧义。
            if (c.Contains(">>"))
                return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(
                c, @"(?<![<>=+\-])\s>(?!=)\s*\S"))
                return true;

            // ── 文件修改类命令（匹配命令起始或 | ; ( & 之后）──
            const string fileVerbs =
                // 写内容 / 导出
                @"set-content|add-content|clear-content|out-file|tee-object|tee|"
                + @"export-csv|export-clixml|"
                // 创建 / 删除 / 移动 / 复制 / 重命名（含别名与 cmd 内建）
                + @"new-item|touch|remove-item|rmdir|move-item|copy-item|rename-item|"
                + @"rm|mv|move|cp|copy|ren|rename|rd|del|erase|xcopy|robocopy|"
                // 交互式编辑器
                + @"notepad\+\+|notepad|gvim|vim|vi|nano|code";

            if (System.Text.RegularExpressions.Regex.IsMatch(
                c, @"(?ix)(?:^|[\|\;\(]|&\s*)\s*(?:" + fileVerbs + @")\b"))
                return true;

            // ── git 写操作（与 git 工具的只读白名单一致：status/diff/log/show 放行）──
            if (System.Text.RegularExpressions.Regex.IsMatch(
                c, @"(?ix)\bgit\s+(?:add|commit|push|pull|checkout|switch|restore|reset|stash|merge|rebase|rm|mv|clean|apply|am|branch|tag|cherry-pick)\b"))
                return true;

            // ── sed -i 原地编辑文件 ──
            if (System.Text.RegularExpressions.Regex.IsMatch(
                c, @"(?ix)\bsed\b[^\r\n;|]{0,80}(?:-i\b|--in-place\b)"))
                return true;

            // ── curl/wget 下载写入本地文件（-o/-O/--output/-OutFile）──
            if (System.Text.RegularExpressions.Regex.IsMatch(
                c, @"(?ix)\b(?:curl|wget|iwr|invoke-webrequest)\b[^\r\n;|]{0,120}(?:-outfile\b|--output\b|\s-o\b|-O\b)"))
                return true;

            return false;
        }

        /// <summary>生成只读 Agent 文件修改命令拦截结果文本。</summary>
        internal static string FormatFileEditBlocked(string command)
        {
            return LocalizationService.Instance.Format("tool.runTerminal.fileEditBlocked", command);
        }

        /// <summary>
        /// 检测 python -c / py -c 等内联代码中的危险调用（os.system、shutil.rmtree、subprocess 等）。
        /// </summary>
        private static DangerousCommandKind DetectPythonInlineDanger(string command)
        {
            if (!IsPythonCommand(command)) return DangerousCommandKind.None;

            var quoted = System.Text.RegularExpressions.Regex.Match(
                command, @"-c\s+([""'])(?<code>.*?)\1",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            string code = quoted.Success ? quoted.Groups["code"].Value : string.Empty;
            if (string.IsNullOrEmpty(code))
            {
                var unquoted = System.Text.RegularExpressions.Regex.Match(
                    command, @"-\s?c\s+(?<code>[^\r\n;|]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                code = unquoted.Success ? unquoted.Groups["code"].Value : string.Empty;
            }
            if (string.IsNullOrWhiteSpace(code)) return DangerousCommandKind.None;

            string[] dangerousPythonPatterns =
            {
                @"\bos\.(?:system|popen|remove|unlink|removedirs|rmdir|execl|execv|spawn|kill)\b",
                @"\bfrom\s+os\s+import\s+(?:system|popen|remove|unlink|removedirs|rmdir)\b",
                @"\bshutil\.rmtree\b",
                @"\bfrom\s+shutil\s+import\s+rmtree\b",
                @"\bsubprocess\b",
                @"\bctypes\b",
                @"\bwinreg\b[^\r\n;]{0,80}\b(?:Delete|Save)\w*\b",
                @"\bsocket\b",
                @"\b(?:paramiko|impacket|scapy|pymetasploit|pwn)\b",
                @"\b\.unlink\s*\(|\b\.rmdir\s*\(",
                @"(?:urlretrieve|requests\s*\.\s*get|urllib)[^\r\n;]{0,160}(?:exec|eval|os\.system|subprocess)",
                @"\b(?:base64|zlib)\b[^\r\n;]{0,200}\bexec\s*\(",
            };

            foreach (string pattern in dangerousPythonPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(code, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
                {
                    return DangerousCommandKind.PythonInlineDanger;
                }
            }
            return DangerousCommandKind.None;
        }

        /// <summary>
        /// 生成危险命令拦截结果文本。
        /// </summary>
        private static string FormatDangerBlocked(string command, DangerousCommandKind kind)
        {
            string reasonKey = kind switch
            {
                DangerousCommandKind.SystemDestruction => "tool.runTerminal.danger.systemDestruction",
                DangerousCommandKind.Shutdown => "tool.runTerminal.danger.shutdown",
                DangerousCommandKind.CriticalDelete => "tool.runTerminal.danger.criticalDelete",
                DangerousCommandKind.AccountTampering => "tool.runTerminal.danger.accountTampering",
                DangerousCommandKind.CredentialTheft => "tool.runTerminal.danger.credentialTheft",
                DangerousCommandKind.RemoteCodeExecution => "tool.runTerminal.danger.remoteCodeExecution",
                DangerousCommandKind.DisableSecurity => "tool.runTerminal.danger.disableSecurity",
                DangerousCommandKind.RegistryTampering => "tool.runTerminal.danger.registryTampering",
                DangerousCommandKind.PythonInlineDanger => "tool.runTerminal.danger.pythonInlineDanger",
                _ => "tool.runTerminal.danger.generic",
            };
            return LocalizationService.Instance.Format(
                "tool.runTerminal.dangerBlocked", command, LocalizationService.Instance[reasonKey]);
        }

        public override string Name => "run_in_terminal";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "run_in_terminal",
                    Description = L["tool.run_in_terminal.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            command = new { type = "string", description = LocalizationService.Instance["tool.runInTerminal.param.command"] },
                            explanation = new { type = "string", description = LocalizationService.Instance["tool.runInTerminal.param.explanation"] },
                            purpose = new { type = "string", description = LocalizationService.Instance["tool.runInTerminal.param.purpose"] },
                            mode = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.runInTerminal.param.mode"],
                                @enum = new[] { "sync", "async" }
                            }
                        },
                        required = new[] { "command", "explanation" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string cmd = GetStringArg(args, "command");
            string expl = GetStringArg(args, "explanation");
            if (!string.IsNullOrEmpty(expl))
                return LocalizationService.Instance.Format("tool.runTerminal.displayText", TruncateText(expl, 80));
            else if (!string.IsNullOrEmpty(cmd))
                return LocalizationService.Instance.Format("tool.runTerminal.displayTextWithCmd", TruncateText(cmd, 60));
            return LocalizationService.Instance["tool.runTerminal.defaultDisplayText"];
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ") || toolResult.StartsWith("[BLOCKED] ")) return toolResult;
            if (toolResult.Contains("exit code: 0") || toolResult.Contains("ExitCode: 0"))
                return LocalizationService.Instance["tool.runTerminal.success"];
            return LocalizationService.Instance["tool.runTerminal.executed"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string command = GetStringArg(args, "command");
            string mode = GetStringArg(args, "mode");

            if (string.IsNullOrEmpty(command))
                return LocalizationService.Instance["tool.runTerminal.missingCommand"];

            if (IsBuildCommand(command))
            {
                return LocalizationService.Instance["tool.runTerminal.buildBlocked"] + "\n\n" +
                    LocalizationService.Instance.Format("tool.runInTerminal.buildBlockedExtra", command);
            }

            // ── 危险命令拦截（原始命令，覆盖 PowerShell/CMD 与 python -c 内联代码）──
            var rawDangerKind = DetectDangerousCommand(command);
            if (rawDangerKind != DangerousCommandKind.None)
            {
                Logger.Warn($"[RunInTerminal] [BLOCKED] 危险命令被拦截 ({rawDangerKind}): {command.Truncate(150)}");
                return FormatDangerBlocked(command, rawDangerKind);
            }

            // ── 只读 Agent 限制：Ask/Explore 不得通过终端修改文件（原始命令）──
            if (IsReadOnlyAgent && DetectFileEditingCommand(command))
            {
                Logger.Warn($"[RunInTerminal] [BLOCKED] 只读 Agent 的文件修改命令被拦截 ({CurrentAgentType}): {command.Truncate(150)}");
                return FormatFileEditBlocked(command);
            }

            // ── Unix 风格命令检测与修正（安全网：即使 AI prompt 已要求 PowerShell，仍有概率输出 Unix 命令）──
            string? unixWarning = DetectUnixStyleCommand(command);

            // ── cmake --build 自动包装 vcvars64.bat ──
            // build_solution 内部用 cmd /c "call vcvars64.bat >nul 2>&1 && cmake --build ..." 初始化 MSVC 环境。
            // AI 通过 run_in_terminal 直接调 cmake --build 时缺少该环境 → 找不到 <cstdint> 等标准头文件。
            // 此处自动检测并注入 vcvars 初始化，确保终端 cmake --build 与 build_solution 行为一致。
            bool isCmakeBuild = command.IndexOf("cmake --build", StringComparison.OrdinalIgnoreCase) >= 0;
            string? vcvarsPath = null;
            string? vcvarsWarning = null;
            if (isCmakeBuild)
            {
                // ── 复用 BuildService.FindVcvarsBat()（需要 UI 线程访问 SVsShell）──
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                vcvarsPath = BuildService.FindVcvarsBat();
                if (vcvarsPath == null)
                    vcvarsWarning = LocalizationService.Instance["tool.runInTerminal.vcvarsNotFound"] + "\n\n";
            }

            // ── 检测并剥离 AI 不必要的 cmd /c "..." 2>&1 包装 ──
            // run_in_terminal 已并发捕获 stdout 和 stderr，AI 有时会额外包装
            // cmd /c "..." 2>&1 试图合并 stderr，但这会导致：(1) NormalizeUnixToPowerShell
            // 正则误改 /c 为 \c 破坏 cmd 开关；(2) PowerShell -Command 内嵌双引号解析错误。
            // 示例：cmd /c "F:\a.exe 2>&1" → 直接执行 F:\a.exe（stderr 已单独捕获）
            string? unwrappedCommand = TryUnwrapCmdC(command);
            if (unwrappedCommand != null)
            {
                Logger.Info($"[RunInTerminal] 剥离冗余 cmd /c 包装: {command.Truncate(100)} → {unwrappedCommand.Truncate(100)}");
                command = unwrappedCommand;
            }

            command = NormalizeUnixToPowerShell(command);

            // ── 危险命令二次检查（修正后的命令，覆盖 && / curl 等被转换后的形态）──
            var normalizedDangerKind = DetectDangerousCommand(command);
            if (normalizedDangerKind != DangerousCommandKind.None)
            {
                Logger.Warn($"[RunInTerminal] [BLOCKED] 危险命令被拦截（修正后） ({normalizedDangerKind}): {command.Truncate(150)}");
                return FormatDangerBlocked(command, normalizedDangerKind);
            }

            // ── 只读 Agent 限制（修正后的命令，覆盖 cmd /c 剥离与 Unix→PowerShell 转换结果）──
            if (IsReadOnlyAgent && DetectFileEditingCommand(command))
            {
                Logger.Warn($"[RunInTerminal] [BLOCKED] 只读 Agent 的文件修改命令被拦截（修正后）({CurrentAgentType}): {command.Truncate(150)}");
                return FormatFileEditBlocked(command);
            }

            // ── Python 环境提示（python / py / pip 命令附加可用性信息）──
            string? pythonHint = null;
            bool useUtf8Output = false;
            if (IsPythonCommand(command))
            {
                useUtf8Output = true;
                var pythonEnv = PythonEnvironmentLazy.Value;
                if (pythonEnv != null)
                    pythonHint = LocalizationService.Instance.Format(
                        "tool.runTerminal.pythonDetected", pythonEnv.Executable, pythonEnv.Version);
                else
                    pythonHint = LocalizationService.Instance["tool.runTerminal.pythonNotFound"];
            }

            // 如果命令被修正过，构建警告前缀（附加到输出开头提醒 AI 下次注意）
            string warningPrefix = "";
            if (unixWarning != null)
                warningPrefix = unixWarning + "\n修正后的命令: " + command + "\n\n";
            if (vcvarsWarning != null)
                warningPrefix += vcvarsWarning;
            if (pythonHint != null)
                warningPrefix += pythonHint + "\n\n";

            bool isAsync = string.Equals(mode, "async", StringComparison.OrdinalIgnoreCase);

            try
            {
                ProcessStartInfo psi;

                // ── cmake --build 专用路径：通过 cmd.exe + vcvars64.bat 初始化 MSVC 环境 ──
                // 与 BuildService.BuildCmakeWithCommandLineAsync 行为对齐
                if (isCmakeBuild && vcvarsPath != null)
                {
                    string cmdArgs = $"/c \"call \"{vcvarsPath}\" >nul 2>&1 && {command}\"";
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = cmdArgs,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = useUtf8Output ? Encoding.UTF8 : null,
                        StandardErrorEncoding = useUtf8Output ? Encoding.UTF8 : null,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = workspaceRoot ?? Directory.GetCurrentDirectory(),
                    };
                }
                else if (command.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase)
                         || command.StartsWith("cmd.exe ", StringComparison.OrdinalIgnoreCase))
                {
                    // ── 命令本身以 cmd /c 开头：直接通过 cmd.exe 执行，避免 PowerShell 嵌套引号问题 ──
                    // 提取 cmd 的参数部分（去掉 "cmd " 或 "cmd.exe " 前缀）
                    string cmdArgs = command.Substring(command.IndexOf(' ') + 1).Trim();
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = cmdArgs,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = useUtf8Output ? Encoding.UTF8 : null,
                        StandardErrorEncoding = useUtf8Output ? Encoding.UTF8 : null,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = workspaceRoot ?? Directory.GetCurrentDirectory(),
                    };
                }
                else
                {
                    // ── 普通命令：通过 PowerShell 执行，需转义内嵌双引号 ──
                    // 将命令中的 " 转义为 PowerShell 可识别的 `" 或使用单引号
                    string escapedCommand = EscapeForPowerShell(command);
                    psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -Command \"{escapedCommand}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = useUtf8Output ? Encoding.UTF8 : null,
                        StandardErrorEncoding = useUtf8Output ? Encoding.UTF8 : null,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Directory.GetCurrentDirectory(),
                    };
                }

                var process = Process.Start(psi);
                if (process == null)
                    return LocalizationService.Instance["tool.runTerminal.cannotStart"];

                if (isAsync)
                {
                    string pid = process.Id.ToString();
                    // ── P2：异步模式必须排空输出管道，否则子进程悬挂 ──
                    // RedirectStandardOutput/Error = true 但无人读取时，子进程输出超过
                    // OS 管道缓冲（~4KB）即阻塞在 write 上、永不退出 →
                    // WaitForExit 悬挂 + 进程泄漏。BeginOutputReadLine/BeginErrorReadLine
                    // 持续排空管道（回调丢弃输出），保证子进程能自然退出。
                    // 注：async 输出的获取入口是 get_terminal_output 工具（当前为占位提示），
                    // 此处仅做排水，不缓冲内容。
                    process.OutputDataReceived += (_, _) => { };
                    process.ErrorDataReceived += (_, _) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // 不等待进程退出，直接返回。进程由 OS 管理，VS 退出时自动清理。
                    // 注意：不能 using/dispose process，因为 fire-and-forget 任务还需要它。
                    _ = Task.Run(() =>
                    {
                        try { process.WaitForExit(); }
                        catch { }
                        finally { process.Dispose(); }
                    });
                    return warningPrefix + LocalizationService.Instance.Format("tool.runTerminal.started", pid, command);
                }
                else
                {
                    // ── 并发读取 stdout 和 stderr，防止管道缓冲区满导致死锁 ──
                    // 经典问题：若先读 stdout 再读 stderr，当 stderr 缓冲区先满时，
                    // 进程会阻塞等待 stderr 被读取，但 stdout 永远等不到进程退出 → 死锁。
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var readTask = Task.WhenAll(stdoutTask, stderrTask);

                    // ── 超时保护：防止进程僵死（如后台进程未正确关闭管道）──
                    var timeoutTask = Task.Delay(SyncTimeout);
                    var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

                    if (completed == timeoutTask)
                    {
                        // 超时：强制杀死进程并返回部分输出
                        try { process.Kill(); } catch { }
                        string partialStdout = stdoutTask.IsCompleted ? stdoutTask.Result : "(超时截断)";
                        string partialStderr = stderrTask.IsCompleted ? stderrTask.Result : "(超时截断)";
                        process.Dispose();

                        var timeoutSb = new StringBuilder();
                        if (!string.IsNullOrEmpty(warningPrefix))
                            timeoutSb.Append(warningPrefix);
                        timeoutSb.AppendLine(LocalizationService.Instance.Format("tool.runInTerminal.timeout", SyncTimeout.TotalMinutes));
                        timeoutSb.AppendLine(LocalizationService.Instance.Format("tool.runInTerminal.commandLabel", command));
                        if (!string.IsNullOrWhiteSpace(partialStdout))
                            timeoutSb.AppendLine(partialStdout);
                        if (!string.IsNullOrWhiteSpace(partialStderr))
                        {
                            timeoutSb.AppendLine("--- STDERR ---");
                            timeoutSb.AppendLine(partialStderr);
                        }
                        return timeoutSb.ToString().TrimEnd();
                    }

                    // 正常完成
                    string stdout = stdoutTask.Result;
                    string stderr = stderrTask.Result;

                    // 流已关闭，进程应已退出；WaitForExit 确保退出码可用
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    int exitCode = process.ExitCode;
                    process.Dispose();

                    var sb = new StringBuilder();
                    if (!string.IsNullOrEmpty(warningPrefix))
                        sb.Append(warningPrefix);
                    sb.AppendLine($"终端输出 (退出码: {exitCode}):");
                    if (!string.IsNullOrWhiteSpace(stdout))
                        sb.AppendLine(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        sb.AppendLine("--- STDERR ---");
                        sb.AppendLine(stderr);
                    }
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.runTerminal.failed", ex.Message);
            }
        }

        /// <summary>
        /// 检测命令是否为编译/构建命令。
        /// </summary>
        private static bool IsBuildCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            string normalized = command.Trim();
            if (normalized.StartsWith("&"))
                normalized = normalized.Substring(1).Trim();

            if (normalized.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("dotnet msbuild", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("dotnet publish", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("dotnet restore", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("dotnet pack", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("msbuild", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("MSBuild.exe", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("cl.exe", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(" link.exe", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("cl ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("\"cl\"", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.StartsWith("gcc ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("g++ ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("clang", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(" gcc ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(" g++ ", StringComparison.OrdinalIgnoreCase))
                return true;

            //  cmake --build 不拦截：build_solution 对 CMake 项目底层用的就是 cmake --build，
            // 且 build_solution 不支持 --target 参数，拦截会导致 AI 无法构建测试目标等非默认 target。
            // 其余原生构建工具（make/ninja）仍拦截，引导使用 build_solution。
            if (normalized.StartsWith("make ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("ninja", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(" make ", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("cargo build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("go build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("npm run build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("yarn build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("pnpm build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("gradle build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("gradlew build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("mvn ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("mvnw ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("pip install", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("nuget restore", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// 检测命令中是否包含 Unix/Linux 风格的语法，返回警告信息。
        /// 如果命令是有效的 PowerShell，返回 null。
        /// </summary>
        private static string? DetectUnixStyleCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            var issues = new List<string>();

            // ── 检测 `&&` 命令链（应使用 `;`）──
            if (command.Contains("&&"))
                issues.Add("使用了 `&&` 连接命令（应用 `;` 替代）");

            // ── 检测 Unix 命令 ──
            var unixCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["grep "] = "Select-String",
                ["| grep"] = "| Select-String",
                ["cat "] = "Get-Content",
                ["rm -rf"] = "Remove-Item -Recurse -Force",
                ["rm -r"] = "Remove-Item -Recurse",
                ["rm "] = "Remove-Item",
                ["ls -la"] = "Get-ChildItem -Force",
                ["ls -l"] = "Get-ChildItem",
                ["ls "] = "Get-ChildItem",
                ["chmod "] = "(不支持 chmod，Windows 使用 icacls 或 attrib)",
                ["sed "] = "(不支持 sed，使用 -replace 运算符或 Select-String)",
                ["awk "] = "(不支持 awk，使用 Select-String 或 ForEach-Object)",
                ["touch "] = "New-Item",
                ["which "] = "Get-Command",
                ["cp -r"] = "Copy-Item -Recurse",
                ["cp "] = "Copy-Item",
                ["mv "] = "Move-Item",
                ["mkdir -p"] = "New-Item -ItemType Directory -Force",
                ["mkdir "] = "New-Item -ItemType Directory",
                ["wget "] = "Invoke-WebRequest",
                ["curl "] = "Invoke-WebRequest",
                ["tail -f"] = "Get-Content -Wait -Tail",
                ["tail "] = "Get-Content -Tail",
                ["head "] = "Get-Content -Head",
                ["./"] = "应使用 `.\\` 运行脚本",
                ["export "] = "应使用 `$env:` 设置环境变量",
            };

            foreach (var kvp in unixCommands)
            {
                if (command.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    issues.Add($"检测到 Unix 命令 `{kvp.Key.Trim()}` → 应使用 `{kvp.Value}`");
                    break; // 只报告第一个问题，避免消息过长
                }
            }

            if (issues.Count == 0) return null;

            return LocalizationService.Instance["tool.runTerminal.unixWarning"] + "\n" + string.Join("\n", issues);
        }

        /// <summary>
        /// 将常见 Unix 风格命令修正为 Windows PowerShell 等价命令。
        /// 此方法是安全网——AI 应通过 system prompt 直接输出 PowerShell 命令。
        /// </summary>
        private static string NormalizeUnixToPowerShell(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return command;

            string result = command;

            // `&&` → `;`（PowerShell 不支持 && 命令链）
            result = result.Replace("&&", ";");

            // 常见 Unix 命令替换（整词匹配，避免误替换变量名等）
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // 注意：顺序很重要！长匹配必须放在短匹配前面
                ["rm -rf"] = "Remove-Item -Recurse -Force",
                ["rm -r"] = "Remove-Item -Recurse",
                ["cp -r"] = "Copy-Item -Recurse",
                ["cp -R"] = "Copy-Item -Recurse",
                ["mkdir -p"] = "New-Item -ItemType Directory -Force",
                ["ls -la"] = "Get-ChildItem -Force",
                ["ls -l"] = "Get-ChildItem",
                ["tail -f"] = "Get-Content -Wait -Tail",
                ["tail "] = "Get-Content -Tail ",
                ["head "] = "Get-Content -Head ",
            };

            foreach (var kvp in replacements)
            {
                result = ReplaceCommandWord(result, kvp.Key, kvp.Value);
            }

            // 简单命令映射（整词）
            var simpleReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["grep"] = "Select-String",
                ["cat"] = "Get-Content",
                ["chmod"] = "icacls",
                ["touch"] = "New-Item",
                ["which"] = "Get-Command",
                ["wget"] = "Invoke-WebRequest",
                ["curl"] = "Invoke-WebRequest",
            };

            foreach (var kvp in simpleReplacements)
            {
                result = ReplaceCommandWord(result, kvp.Key + " ", kvp.Value + " ");
            }

            // 路径分隔符 `/` → `\`（仅对已知路径模式，避免破坏 URL 等）
            // 匹配类似 ./path/to/file 或 /absolute/path 的模式
            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"(?<![a-zA-Z])(\./)([^\s;|]+)", @".\$2");
            //  至少匹配 2 层路径（如 /usr/bin），避免误改 cmd /c、cmd /k 等 Windows 开关
            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"(?<![a-zA-Z:\)\(])(/[a-zA-Z0-9_\-\.]+){2,}", m =>
                    m.Value.Replace('/', '\\'));

            // `./script` → `.\script`
            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"(?<![a-zA-Z\\])\./([^\s;|]+)", @".\$1");

            return result;
        }

        /// <summary>
        /// 在命令字符串中替换命令词（仅当出现在命令起始位置或管道/分隔符后时替换）。
        /// 避免替换文件路径或参数中包含的关键词。
        /// </summary>
        private static string ReplaceCommandWord(string command, string oldWord, string newWord)
        {
            if (!command.Contains(oldWord)) return command;

            // 只在命令起始位置或 `|`、`;` 后替换
            var pattern = $@"(^|\||;\s*){System.Text.RegularExpressions.Regex.Escape(oldWord)}";
            return System.Text.RegularExpressions.Regex.Replace(
                command, pattern, $"$1{newWord}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 尝试剥离 AI 不必要的 cmd /c "..." 2>&1 包装。
        /// run_in_terminal 已并发捕获 stdout 和 stderr，无需通过 cmd /c + 2>&1 合并。
        /// 如果命令是纯 cmd /c "某程序" 2>&1 模式（只是为了合并 stderr），
        /// 返回内部命令；否则返回 null。
        /// </summary>
        /// <remarks>
        /// 仅剥离「仅合并 stderr，无其他 cmd 特性」的包装。
        /// 如果 cmd /c 内部使用了 || / && / set 等 cmd 特性，保留原命令。
        /// </remarks>
        private static string? TryUnwrapCmdC(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            string trimmed = command.Trim();

            // 匹配: cmd /c "..." 或 cmd /c "... 2>&1" 或 cmd.exe /c "..." 2>&1
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"^cmd(?:\.exe)?\s+/c\s+""([^""]+)""(\s*2>&1)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!match.Success) return null;

            string innerCommand = match.Groups[1].Value.Trim();

            // 安全检查：如果内部命令使用了 cmd 特性（||, &&, set, if, for, %VAR%），保留
            if (System.Text.RegularExpressions.Regex.IsMatch(innerCommand,
                @"\|\||&&|(?<!\%)set\s+|%[a-zA-Z_][a-zA-Z0-9_]*%|if\s+exist|for\s+%|call\s+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return null;
            }

            return innerCommand;
        }

        /// <summary>
        /// 将命令字符串中的双引号转义为 PowerShell 可安全解析的形式。
        /// 在 PowerShell -Command 参数中，内嵌双引号需要特殊处理：
        /// - 将 " 替换为 `"（反引号+双引号），PowerShell 会将其识别为转义双引号
        /// - 或者改用单引号包裹（但如果命令本身含单引号则不宜）
        /// </summary>
        private static string EscapeForPowerShell(string command)
        {
            if (string.IsNullOrEmpty(command)) return command;
            if (!command.Contains("\"")) return command;

            // PowerShell 中，用 \"\" 或 `" 转义双引号
            // 使用 \" 替换 "（在 -Command 的引号字符串中，\" 被识别为转义引号）
            return command.Replace("\"", "\\\"");
        }
    }
}
