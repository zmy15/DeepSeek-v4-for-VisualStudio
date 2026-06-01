using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 构建服务实现 — 封装 VS SDK 构建交互。
    /// 
    /// 支持：
    /// - MSBuild .sln 项目（通过 IVsSolutionBuildManager）
    /// - CMake / Open Folder 项目（通过命令行 cmake --build）
    /// - 构建错误收集（Task List + Output Window）
    /// </summary>
    public class BuildService : IBuildService
    {
        /// <summary>
        /// 执行解决方案构建。自动检测项目类型并选择合适的构建方式。
        /// </summary>
        public async Task<string> BuildAsync(string? solutionPath, CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // ── 解析工作区根目录 ──
            string? workspaceDir = NormalizeToDirectory(solutionPath);

            // ── 检测是否为 CMake / Open Folder 项目（用于日志）──
            bool isCmakeProject = IsCmakeProject(workspaceDir);

            Logger.Info($"[BuildService] 开始构建 (CMake={isCmakeProject}, workspace={workspaceDir ?? "(null)"})");

            // ── CMake / Open Folder 项目：使用命令行 cmake --build ──
            // IVsSolutionBuildManager 和 DTE SolutionBuild.Build 对 CMake 项目不兼容，
            // 会分别产生 E_POINTER 和 "pSlnCfg 为 null" 的虚假 WARN 日志。
            // 改用命令行 cmake --build 直接构建，更可靠且能获取完整编译输出。
            if (isCmakeProject)
            {
                if (workspaceDir == null)
                {
                    Logger.Warn("[BuildService] CMake 项目未检测到工作区目录");
                    return LocalizationService.Instance["build.noBuildSystem"];
                }
                return await BuildCmakeWithCommandLineAsync(workspaceDir, ct);
            }

            // ── 方案1：IVsSolutionBuildManager（VS 2022 支持 .sln 项目）──
            // dwDefQueryResults 已修正为 VSSBQR_OUTOFDATE_QUERY_YES，
            // SBF_OPERATION_FORCE_UPDATE 确保过期项目也被构建。
            string? slnResult = await TryBuildWithSolutionManagerAsync(ct);
            if (slnResult != null)
                return slnResult; // 成功启动或明确失败
            // slnResult 为 null → 需要回退

            // ── 方案2：DTE.SolutionBuild / ExecuteCommand（回退方案）──
            string? dteResult = await TryBuildWithDteAsync(ct);
            if (dteResult != null)
                return dteResult;

            return LocalizationService.Instance["build.noBuildSystem"];
        }

        #region Build Methods

        /// <summary>
        /// CMake/Open Folder 项目专用构建路径。
        /// 优先使用命令行 cmake --build 直接构建（更可靠，能获取完整编译输出）；
        /// 若未找到 cmake 可执行文件，则回退到 DTE ExecuteCommand（依赖 VS 内置 CMake 集成）。
        /// 
        /// 命令行构建流程：
        /// 1. 从 CMakePresets.json 或默认路径查找构建目录
        /// 2. 执行 cmake --build &lt;buildDir&gt; --config &lt;config&gt;
        /// 3. 捕获 stdout/stderr 并解析错误行
        /// 4. 返回结构化构建结果
        /// </summary>
        private static async Task<string> BuildCmakeWithCommandLineAsync(
            string workspaceDir, CancellationToken ct)
        {
            Logger.Info($"[BuildService] CMake 命令行构建, workspace={workspaceDir}");

            // ── 1. 查找构建目录 ──
            string? buildDir = FindCmakeBuildDirectory(workspaceDir);
            if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir))
            {
                Logger.Warn($"[BuildService] 未找到 CMake 构建目录: {buildDir ?? "(null)"}");
                return LocalizationService.Instance["build.cmake.noBuildDir"];
            }

            // ── 2. 确定构建配置 ──
            string config = FindCmakeBuildConfig(workspaceDir);

            // ── 3. 查找 cmake 可执行文件；找不到则回退到 DTE ──
            string? cmakePath = FindCmakeExecutable();
            if (string.IsNullOrEmpty(cmakePath))
            {
                Logger.Warn("[BuildService] 未找到 cmake 可执行文件，回退到 DTE ExecuteCommand");
                return await BuildCmakeViaDteFallbackAsync(ct);
            }

            // ── 3.5 查找 vcvars64.bat 以初始化 MSVC 编译器环境 ──
            // 直接调用 cmake --build 时，cl.exe 缺少 INCLUDE/LIB 等环境变量，
            // 导致找不到 <cstdint> 等标准头文件。通过 vcvars 初始化环境解决。
            string? vcvarsPath = FindVcvarsBat();

            string args = $"--build \"{buildDir}\" --config {config}";
            Logger.Info($"[BuildService] 执行: {cmakePath} {args}" + (vcvarsPath != null ? $" (通过 vcvars: {vcvarsPath})" : ""));

            try
            {
                ProcessStartInfo psi;

                if (vcvarsPath != null)
                {
                    // ── 方案 A：通过 cmd.exe /c 先初始化 MSVC 环境再构建 ──
                    // 使用 `call vcvars64.bat >nul 2>&1 && cmake --build ...`
                    // 这样 cmake 继承 vcvars 设置的环境变量（INCLUDE、LIB、PATH 等）
                    string cmdArgs = $"/c \"call \"{vcvarsPath}\" >nul 2>&1 && \"{cmakePath}\" {args}\"";
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = cmdArgs,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = workspaceDir,
                    };
                }
                else
                {
                    // ── 方案 B：未找到 vcvars，直接调用 cmake（可能失败）──
                    Logger.Warn("[BuildService] 未找到 vcvars64.bat，直接调用 cmake（可能缺少 MSVC 环境）");
                    psi = new ProcessStartInfo
                    {
                        FileName = cmakePath,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = workspaceDir,
                    };
                }

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Logger.Warn("[BuildService] 无法启动 cmake 进程");
                    return LocalizationService.Instance["build.cmake.launchFailed"];
                }

                // ── 异步读取输出（避免死锁）──
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                // ── 等待进程完成（带超时）──
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(5));

                await Task.Run(() =>
                {
                    process.WaitForExit();
                }, cts.Token);

                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                int exitCode = process.ExitCode;
                Logger.Info($"[BuildService] cmake 退出码: {exitCode}");

                // ── 4. 格式化结果 ──
                return FormatCmakeBuildResult(exitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[BuildService] CMake 构建已被取消");
                return LocalizationService.Instance["build.cancelled"];
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] CMake 命令行构建异常: {ex.Message}");
                return string.Format(LocalizationService.Instance["build.cmake.failed"], ex.Message);
            }
        }

        /// <summary>
        /// 事件驱动的构建触发 + 等待。
        /// 先按需触发构建（仅一次），再通过 IVsUpdateSolutionEvents 事件驱动等待
        /// （.sln 项目快速响应），超时后回退到 UI 线程轮询 BuildState
        /// （CMake 项目兼容）。所有 COM 访问均保证在 UI 线程。
        /// </summary>
        /// <param name="needStart">true=调用 ExecuteCommand 触发构建；false=构建已在运行，仅等待</param>
        private static async Task<bool> TriggerAndWaitForBuildViaEventsAsync(
            EnvDTE.DTE dte,
            EnvDTE.SolutionBuild solutionBuild,
            bool needStart,
            CancellationToken ct,
            TimeSpan timeout)
        {
            // ── 启动构建（仅一次，在事件订阅之前）──
            if (needStart)
            {
                Logger.Info("[BuildService] 正在通过 ExecuteCommand 构建解决方案 (CMake 项目)...");
                dte.ExecuteCommand("Build.BuildSolution");
            }
            else
            {
                Logger.Info("[BuildService] 构建已在运行中，等待完成...");
            }

            // ── 策略 1：事件驱动（IVsUpdateSolutionEvents，适用于 .sln 项目）──
            var buildManager = (IVsSolutionBuildManager?)ServiceProvider.GlobalProvider
                .GetService(typeof(SVsSolutionBuildManager));

            if (buildManager != null)
            {
                var sink = new BuildEventsSink();
                buildManager.AdviseUpdateSolutionEvents(sink, out uint cookie);
                try
                {
                    // 事件检测用较短超时（30s）：.sln 项目事件通常秒级触发；
                    // CMake 项目不触发 IVsUpdateSolutionEvents，30s 后回退轮询
                    var eventTimeout = TimeSpan.FromSeconds(30);
                    if (eventTimeout > timeout) eventTimeout = timeout;

                    bool completed = await sink.WaitForCompletionAsync(ct, eventTimeout);
                    if (completed) return true;

                    Logger.Info("[BuildService] 事件未在 30s 内触发（可能是 CMake 项目），回退到轮询...");
                }
                finally
                {
                    try { buildManager.UnadviseUpdateSolutionEvents(cookie); } catch { }
                    sink.Dispose();
                }
            }

            // ── 策略 2：UI 线程异步轮询 BuildState（CMake 兼容）──
            // 注意：构建已在上方启动，此处只轮询等待，不重复 ExecuteCommand
            return await PollDteBuildCompletionAsync(solutionBuild, ct, timeout);
        }

        /// <summary>
        /// 从 DTE SolutionBuild 状态构建结果摘要。
        /// </summary>
        private static string BuildResultFromDte(EnvDTE.SolutionBuild solutionBuild)
        {
            int lastBuildInfo = solutionBuild.LastBuildInfo;
            if (lastBuildInfo == 0)
            {
                Logger.Info("[BuildService] ✅ 构建成功");
                return LocalizationService.Instance["build.dteSuccess"];
            }

            return FormatBuildErrorResult();
        }

        /// <summary>
        /// 使用 IVsSolutionBuildManager 构建。
        /// VS 2022 中同时支持 .sln 和 CMake/Open Folder 项目。
        /// 返回 null 表示需要回退到其他方式。
        /// </summary>
        private static async Task<string?> TryBuildWithSolutionManagerAsync(CancellationToken ct)
        {
            var buildManager = (IVsSolutionBuildManager?)ServiceProvider.GlobalProvider
                .GetService(typeof(SVsSolutionBuildManager));

            if (buildManager == null)
            {
                Logger.Warn("[BuildService] 无法获取 IVsSolutionBuildManager");
                return null; // 回退
            }

            // ── 检查构建管理器是否正忙 ──
            buildManager.QueryBuildManagerBusy(out int isBusy);
            if (isBusy != 0)
            {
                Logger.Warn("[BuildService] 构建管理器正忙");
                return LocalizationService.Instance["build.managerBusy"];
            }

            var buildEventsSink = new BuildEventsSink();
            uint buildCookie = 0;
            bool advised = false;

            try
            {
                buildManager.AdviseUpdateSolutionEvents(buildEventsSink, out buildCookie);
                advised = true;

                Logger.Info("[BuildService] 正在构建解决方案 (IVsSolutionBuildManager)…");

                // ── 修正参数：dwDefQueryResults 必须为有效枚举值，0 会导致构建取消 ──
                // SBF_OPERATION_FORCE_UPDATE: 强制更新过期项目
                // SBF_OPERATION_BUILD: 执行构建操作
                // VSSBQR_OUTOFDATE_QUERY_YES: 对过期项目执行构建
                // fSuppressUI = 0: 允许显示 UI（错误列表等）
                int hr = buildManager.StartSimpleUpdateSolutionConfiguration(
                    (uint)(VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_FORCE_UPDATE |
                           VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD),
                    (uint)VSSOLNBUILDQUERYRESULTS.VSSBQR_OUTOFDATE_QUERY_YES,
                    0);

                if (ErrorHandler.Failed(hr))
                {
                    // ── API 调用失败但构建事件可能已触发（CMake 项目常见）──
                    // 检查事件是否已收到构建完成通知，若是则直接返回事件结果
                    if (buildEventsSink.HasCompleted)
                    {
                        Logger.Info($"[BuildService] StartSimpleUpdateSolutionConfiguration 返回 0x{hr:X8}，但构建事件已完成，使用事件结果");
                        return BuildResultFromEvents(buildEventsSink);
                    }

                    Logger.Warn($"[BuildService] StartSimpleUpdateSolutionConfiguration 失败: HRESULT=0x{hr:X8} ({GetHResultDescription(hr)})");
                    return null; // 回退到 DTE 方式
                }

                Logger.Info($"[BuildService] StartSimpleUpdateSolutionConfiguration 成功 (HRESULT=0x{hr:X8})，等待构建完成...");

                // 等待构建完成（最长 5 分钟）
                bool completed = await buildEventsSink.WaitForCompletionAsync(ct, TimeSpan.FromMinutes(5));

                if (!completed)
                {
                    Logger.Warn("[BuildService] ⚠️ 构建超时（5 分钟）");
                    return LocalizationService.Instance["build.timeout"];
                }

                return BuildResultFromEvents(buildEventsSink);
            }
            finally
            {
                if (advised && buildCookie != 0)
                {
                    try { buildManager.UnadviseUpdateSolutionEvents(buildCookie); } catch { }
                }
                buildEventsSink.Dispose();
            }
        }

        /// <summary>
        /// 获取常见 HRESULT 的可读描述，辅助诊断。
        /// </summary>
        private static string GetHResultDescription(int hr)
        {
            return (uint)hr switch
            {
                0x80004003u => "E_POINTER（空指针/无效参数）",
                0x80004005u => "E_FAIL（一般失败）",
                0x8007000Eu => "E_OUTOFMEMORY（内存不足）",
                0x80004001u => "E_NOTIMPL（未实现）",
                _ => "未知错误"
            };
        }

        /// <summary>
        /// 使用 DTE 构建（回退方案）。
        /// 优先使用 ExecuteCommand 以兼容 CMake/Open Folder 项目；
        /// 回退到 SolutionBuild.Build 处理传统 .sln 项目。
        /// </summary>
        private static async Task<string?> TryBuildWithDteAsync(CancellationToken ct)
        {
            try
            {
                var dte = (EnvDTE.DTE?)ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    Logger.Warn("[BuildService] 无法获取 DTE");
                    return null;
                }

                var solutionBuild = dte.Solution?.SolutionBuild;
                if (solutionBuild == null)
                {
                    Logger.Warn("[BuildService] DTE.SolutionBuild 不可用");
                    return null;
                }

                Logger.Info("[BuildService] 正在构建解决方案 (DTE)…");

                // ── 等待 DTE 就绪：上一步构建刚完成时，DTE 内部状态可能尚未复位 ──
                const int maxWaitRetries = 5;
                for (int retry = 0; retry < maxWaitRetries; retry++)
                {
                    if (solutionBuild.BuildState != EnvDTE.vsBuildState.vsBuildStateInProgress)
                        break;
                    Logger.Info($"[BuildService] DTE 构建忙，等待就绪 ({retry + 1}/{maxWaitRetries})…");
                    await Task.Delay(1000, ct);
                }

                if (solutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
                {
                    return LocalizationService.Instance["build.alreadyInProgress"];
                }

                // 启动构建
                bool buildSuccess = false;

                // ── 方案 A：SolutionBuild.Build（同步等待，必须后台线程执行避免阻塞 UI）──
                bool? resultA = null;
                try
                {
                    resultA = await Task.Run(() =>
                    {
                        solutionBuild.Build(true); // true = WaitForBuildToFinish
                        return (bool?)(solutionBuild.LastBuildInfo == 0);
                    }, ct);
                }
                catch (Exception exBuild)
                {
                    Logger.Warn($"[BuildService] DTE SolutionBuild.Build 异常: {exBuild.Message}");
                }

                if (resultA.HasValue)
                {
                    buildSuccess = resultA.Value;
                }
                else
                {
                    // ── 方案 B：ExecuteCommand + 事件驱动/轮询等待（UI 线程安全）──
                    try
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                        bool completed = await TriggerAndWaitForBuildViaEventsAsync(
                            dte, solutionBuild, needStart: true, ct, TimeSpan.FromMinutes(5));
                        if (!completed)
                        {
                            Logger.Warn("[BuildService] ⚠️ DTE 构建等待超时");
                            return LocalizationService.Instance["build.timeout"];
                        }
                        buildSuccess = solutionBuild.LastBuildInfo == 0;
                    }
                    catch (Exception exCmd)
                    {
                        Logger.Warn($"[BuildService] DTE ExecuteCommand 异常: {exCmd.Message}");
                        return string.Format(LocalizationService.Instance["build.dteFailed"], exCmd.Message);
                    }
                }

                if (buildSuccess)
                {
                    Logger.Info("[BuildService] ✅ DTE 构建成功");
                    return LocalizationService.Instance["build.dteSuccess"];
                }

                return FormatBuildErrorResult();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] DTE 构建异常: {ex.Message}");
                return string.Format(LocalizationService.Instance["build.dteFailed"], ex.Message);
            }
        }

        #endregion

        #region Result Helpers

        private static string? BuildResultFromEvents(BuildEventsSink sink)
        {
            sink.GetBuildResult(out int succeeded, out int failed, out int cancelled);

            // ── 0 个项目被构建：CMake / Open Folder 项目可能不被 IVsSolutionBuildManager 支持 ──
            if (succeeded == 0 && failed == 0 && cancelled == 0)
            {
                Logger.Info("[BuildService] ⚠️ IVsSolutionBuildManager 未构建任何项目（可能是 CMake/Open Folder），回退到 DTE");
                return null!; // 返回 null 触发回退到 DTE
            }

            if (failed == 0 && cancelled == 0)
            {
                Logger.Info($"[BuildService] ✅ 构建成功 ({succeeded} 个项目)");
                return string.Format(LocalizationService.Instance["build.projectsPassed"], succeeded);
            }

            if (cancelled != 0)
            {
                Logger.Info("[BuildService] ⚠️ 构建已取消");
                return LocalizationService.Instance["build.cancelled"];
            }

            var fullResult = new StringBuilder();
            fullResult.AppendLine(string.Format(LocalizationService.Instance["build.projectsFailed"], failed));
            fullResult.Append(FormatBuildErrorResult());
            string resultStr = fullResult.ToString();
            Logger.Info($"[BuildService] {resultStr.Truncate(500)}");
            return resultStr;
        }

        /// <summary>
        /// 收集构建错误并格式化为统一的错误摘要。
        /// 供 BuildResultFromDte、BuildResultFromEvents 和 TryBuildWithDteAsync 复用。
        /// </summary>
        private static string FormatBuildErrorResult()
        {
            string errors = CollectBuildErrors();
            var result = new StringBuilder();
            result.AppendLine(LocalizationService.Instance["build.completedWithErrors"]);
            if (!string.IsNullOrEmpty(errors))
            {
                result.AppendLine();
                result.AppendLine("## " + LocalizationService.Instance["build.errorDetails"]);
                result.Append(errors);
            }
            return result.ToString();
        }

        #endregion

        #region Error Collection

        /// <summary>
        /// 从 VS Error List 收集编译错误详情。
        /// 按文件分组，每个错误包含文件名、行号、错误描述。
        /// 公开为 internal static，供 BuiltInToolService.get_errors 复用。
        /// </summary>
        internal static string CollectBuildErrors()
        {
            var sb = new StringBuilder();

            try
            {
                // ── 方案一：SVsErrorList → IVsTaskList（VS SDK Interop 原生接口）──
                // 关键：必须使用 SVsErrorList（错误列表），而非 SVsTaskList（任务列表）！
                // 根据 MSDN："The SVsErrorList service also provides IVsTaskList."
                // SVsTaskList 返回的是用户任务列表（// TODO 等），不包含编译错误。
                // 编译错误由构建系统注册到 SVsErrorList 中。
                // 详见：https://learn.microsoft.com/dotnet/api/microsoft.visualstudio.shell.interop.ivserrorlist
                var taskList = (IVsTaskList?)ServiceProvider.GlobalProvider
                    .GetService(typeof(SVsErrorList));
                if (taskList != null)
                {
                    taskList.EnumTaskItems(out IVsEnumTaskItems? enumTasks);
                    if (enumTasks != null)
                    {
                        var errorsByFile = new Dictionary<string, List<string>>(
                            StringComparer.OrdinalIgnoreCase);

                        IVsTaskItem[] items = new IVsTaskItem[1];
                        uint[] fetched = new uint[1];

                        while (enumTasks.Next(1, items, fetched) == VSConstants.S_OK && fetched[0] == 1)
                        {
                            try
                            {
                                var item = items[0];

                                // ── 使用 IVsTaskItem 基接口获取信息（VS 2022 SDK）──
                                string? fileName = null;
                                int line = 0;
                                int column = 0;
                                string? text = null;
                                VSTASKCATEGORY cat = VSTASKCATEGORY.CAT_MISC;
                                VSTASKPRIORITY priority = VSTASKPRIORITY.TP_NORMAL;

                                // IVsTaskItem3 / IVsTaskItem2 均继承自 IVsTaskItem，
                                // Category/Document/Line/Column/get_Priority/get_Text 定义在基接口上
                                var catArr = new VSTASKCATEGORY[1];
                                item.Category(catArr);
                                cat = catArr[0];

                                var priArr = new VSTASKPRIORITY[1];
                                item.get_Priority(priArr);
                                priority = priArr[0];

                                item.Document(out fileName);
                                item.Line(out line);
                                item.Column(out column);
                                item.get_Text(out text);

                                // ── 过滤：只收集编译错误（非警告/消息）──
                                if (cat != VSTASKCATEGORY.CAT_BUILDCOMPILE)
                                    continue;
                                if (priority != VSTASKPRIORITY.TP_HIGH)
                                    continue;
                                if (string.IsNullOrWhiteSpace(text))
                                    continue;

                                string headingKey = !string.IsNullOrWhiteSpace(fileName)
                                    ? fileName : LocalizationService.Instance["build.unknownFile"];

                                string desc = line > 0
                                    ? string.Format(LocalizationService.Instance["build.errorAtLine"], line, text)
                                    : $"- {text}";

                                if (!errorsByFile.ContainsKey(headingKey))
                                    errorsByFile[headingKey] = new List<string>();
                                errorsByFile[headingKey].Add(desc);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[BuildService] 跳过无法读取的 Task Item: {ex.Message}");
                            }
                        }

                        if (errorsByFile.Count > 0)
                        {
                            Logger.Info($"[BuildService] SVsErrorList 收集到 {errorsByFile.Sum(k => k.Value.Count)} 个编译错误，分布在 {errorsByFile.Count} 个文件");
                            foreach (var kvp in errorsByFile)
                            {
                                sb.AppendLine($"### {kvp.Key}");
                                foreach (var desc in kvp.Value)
                                    sb.AppendLine(desc);
                                sb.AppendLine();
                            }
                            return sb.ToString();
                        }
                        else
                        {
                            Logger.Info("[BuildService] SVsErrorList 中未找到编译错误条目，回退到 Output Window");
                        }
                    }
                }
                else
                {
                    Logger.Info("[BuildService] SVsErrorList 服务不可用（SVsErrorList 未实现 IVsTaskList），回退到 Output Window");
                }

                // ── 方案二：DTE Output Window 回退 ──
                TryCollectFromOutputWindow(sb);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] 错误收集失败: {ex.Message}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 从 VS Output Window 的"生成"窗格中提取编译错误行。
        /// 必须在 UI 线程上调用（访问 DTE COM 对象）。
        /// 优先匹配 GUID，回退到按名称匹配。
        /// </summary>
        private static void TryCollectFromOutputWindow(StringBuilder sb)
        {
            // ── 确保在 UI 线程上访问 DTE COM 对象 ──
            // 跨线程访问 DTE 会导致 FatalExecutionEngineError / ExecutionEngineException
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                try
                {
                    var dte = (EnvDTE.DTE?)ServiceProvider.GlobalProvider
                        .GetService(typeof(EnvDTE.DTE));
                    if (dte == null) return;

                    EnvDTE.Window window = dte.Windows.Item(EnvDTE.Constants.vsWindowKindOutput);
                    EnvDTE.OutputWindow outputWin = (EnvDTE.OutputWindow)window.Object;

                    // ── 查找"生成"输出窗格 ──
                    // VS 2022 中 Build 窗格的 GUID 为 {1BD8A850-02D1-11D1-BEE7-00A0C913D83C}
                    // 部分版本/场景下 GUID 可能不同，回退到按名称匹配
                    EnvDTE.OutputWindowPane? buildPane = FindBuildPane(outputWin);

                    if (buildPane == null) return;

                    // ── 安全读取窗格文本 ──
                    EnvDTE.TextDocument textDoc = buildPane.TextDocument;
                    var sel = textDoc.Selection;
                    sel.SelectAll();
                    string output = sel.Text ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(output)) return;

                    var errorLines = output
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(line =>
                            line.Contains("error", StringComparison.OrdinalIgnoreCase)
                            && !line.Contains("0 Error", StringComparison.OrdinalIgnoreCase)
                            && !line.Contains("0 错误", StringComparison.OrdinalIgnoreCase))
                        .Take(30)
                        .Select(line => line.Trim())
                        .ToList();

                    if (errorLines.Count > 0)
                    {
                        sb.AppendLine("### 构建输出 (Output Window)");
                        foreach (var line in errorLines)
                            sb.AppendLine($"- {line}");
                        sb.AppendLine();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[TryCollectFromOutputWindow] Output 窗口读取失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 在 Output Window 中查找"生成"窗格。
        /// 优先按 GUID 匹配，回退到按名称匹配（兼容不同 VS 版本/语言）。
        /// </summary>
        private static EnvDTE.OutputWindowPane? FindBuildPane(EnvDTE.OutputWindow outputWin)
        {
            // ── 已知的 Build 窗格 GUID ──
            string[] buildPaneGuids = {
                "{1BD8A850-02D1-11D1-BEE7-00A0C913D83C}",  // VS 生成窗格标准 GUID
                "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}",  // 备选（部分版本）
            };

            foreach (EnvDTE.OutputWindowPane pane in outputWin.OutputWindowPanes)
            {
                try
                {
                    string paneGuid = pane.Guid;
                    foreach (var guid in buildPaneGuids)
                    {
                        if (string.Equals(paneGuid, guid, StringComparison.OrdinalIgnoreCase))
                            return pane;
                    }
                }
                catch
                {
                    // 某些窗格可能不支持读取 Guid，忽略
                }
            }

            // ── 回退：按名称匹配（中英文）──
            string[] buildPaneNames = { "Build", "生成", "build", "生成输出" };
            foreach (EnvDTE.OutputWindowPane pane in outputWin.OutputWindowPanes)
            {
                try
                {
                    string name = pane.Name;
                    foreach (var candidate in buildPaneNames)
                    {
                        if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                            return pane;
                    }
                }
                catch { }
            }

            return null;
        }

        #endregion

        #region Error List Methods

        /// <summary>
        /// 获取错误列表中用户当前选中的错误项信息。
        /// 通过 IVsTaskList2 (SVsErrorList) 接口获取 VS Error List 窗口中用户高亮的条目。
        /// 仅在 UI 线程调用。
        /// </summary>
        public async Task<List<ErrorListItem>> GetSelectedErrorsAsync(CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var results = new List<ErrorListItem>();

            try
            {
                var errorList = ServiceProvider.GlobalProvider
                    .GetService(typeof(SVsErrorList)) as IVsTaskList2;
                if (errorList == null)
                {
                    Logger.Info("[BuildService] SVsErrorList 服务不可用，无法获取选中错误项");
                    return results;
                }

                errorList.EnumSelectedItems(out IVsEnumTaskItems? enumSelected);
                if (enumSelected == null) return results;

                results = CollectTaskItemsFromEnum(enumSelected);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] 获取选中错误项失败: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// 从 IVsEnumTaskItems 枚举器中收集所有错误项的结构化信息。
        /// 同时支持 IVsTaskItem（基础）和 IVsTaskItem2/3（扩展）接口，提取错误码、项目名等扩展字段。
        /// </summary>
        private static List<ErrorListItem> CollectTaskItemsFromEnum(IVsEnumTaskItems enumerator)
        {
            var results = new List<ErrorListItem>();
            IVsTaskItem[] items = new IVsTaskItem[1];
            uint[] fetched = new uint[1];

            while (enumerator.Next(1, items, fetched) == VSConstants.S_OK && fetched[0] == 1)
            {
                try
                {
                    var item = items[0];
                    var entry = ExtractErrorListItem(item);
                    if (entry != null)
                        results.Add(entry);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[BuildService] 跳过无法读取的选中 Task Item: {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// 从 IVsTaskItem 中提取结构化的错误项信息。
        /// 尝试将 IVsTaskItem 转换为 IVsTaskItem2/3 以获取更丰富的元数据（错误码、项目名、子类别等）。
        /// </summary>
        private static ErrorListItem? ExtractErrorListItem(IVsTaskItem item)
        {
            string? text = null;
            item.get_Text(out text);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var entry = new ErrorListItem { Description = text };

            // ── 基本信息（IVsTaskItem 基接口）──
            item.Document(out string? fileName);
            entry.FileName = fileName;

            item.Line(out int line);
            entry.Line = line;

            item.Column(out int column);
            entry.Column = column;

            // 类别
            var catArr = new VSTASKCATEGORY[1];
            item.Category(catArr);
            entry.Category = catArr[0] switch
            {
                VSTASKCATEGORY.CAT_BUILDCOMPILE => "error",
                VSTASKCATEGORY.CAT_USER => "user",
                _ => "info"
            };

            // 优先级
            var priArr = new VSTASKPRIORITY[1];
            item.get_Priority(priArr);
            entry.Priority = priArr[0] switch
            {
                VSTASKPRIORITY.TP_HIGH => "high",
                VSTASKPRIORITY.TP_NORMAL => "normal",
                VSTASKPRIORITY.TP_LOW => "low",
                _ => "normal"
            };

            // ── 扩展信息（IVsTaskItem2）──
            if (item is IVsTaskItem2 item2)
            {
                // 自定义列值 — 尝试获取错误码和项目名
                // VS Error List 默认列：Severity(0), Code(1), Description(2), Project(3), File(4), Line(5)
                // get_CustomColumnText 签名: (ref Guid guidFormat, uint iColumn, out string pbstrText)
                Guid defaultFormat = Guid.Empty;

                try
                {
                    item2.get_CustomColumnText(ref defaultFormat, 1, out string? errorCode);
                    if (!string.IsNullOrWhiteSpace(errorCode))
                        entry.ErrorCode = errorCode;
                }
                catch
                {
                    // 某些 IVsTaskItem2 实现可能不支持 get_CustomColumnText
                }

                try
                {
                    item2.get_CustomColumnText(ref defaultFormat, 3, out string? project);
                    if (!string.IsNullOrWhiteSpace(project))
                        entry.Project = project;
                }
                catch { }
            }

            // ── 扩展信息（IVsTaskItem3）──
            if (item is IVsTaskItem3 item3)
            {
                // 尝试获取更精确的列值
                // GetColumnValue 签名: (int iColumn, out uint ptvfFlags, out uint pvtfColourFlags, out object pvarValue, out string pbstrCanonical)
                try
                {
                    if (string.IsNullOrEmpty(entry.ErrorCode))
                    {
                        item3.GetColumnValue(1, out uint _, out uint _, out object varValue, out string _);
                        if (varValue is string code && !string.IsNullOrWhiteSpace(code))
                            entry.ErrorCode = code;
                    }
                }
                catch { }

                try
                {
                    if (string.IsNullOrEmpty(entry.Project))
                    {
                        item3.GetColumnValue(3, out uint _, out uint _, out object varValue, out string _);
                        if (varValue is string proj && !string.IsNullOrWhiteSpace(proj))
                            entry.Project = proj;
                    }
                }
                catch { }
            }

            return entry;
        }

        #endregion

        #region DTE Build Wait Helper

        /// <summary>
        /// UI 线程异步轮询 DTE SolutionBuild.BuildState，等待构建完成。
        /// 作为事件驱动机制的回退方案。
        /// 
        /// 关键：使用 await Task.Delay + SwitchToMainThreadAsync 替代 Thread.Sleep，
        /// 保证每次 COM 属性读取都在 UI 线程，且不阻塞线程池。
        /// </summary>
        private static async Task<bool> PollDteBuildCompletionAsync(
            EnvDTE.SolutionBuild solutionBuild, CancellationToken ct, TimeSpan timeout)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                while (true)
                {
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);

                    var state = solutionBuild.BuildState;

                    // 构建已完成（Done 或任何非 NotStarted/InProgress 状态）
                    if (state != EnvDTE.vsBuildState.vsBuildStateInProgress
                        && state != EnvDTE.vsBuildState.vsBuildStateNotStarted)
                    {
                        Logger.Info($"[BuildService] 轮询检测到构建完成, BuildState={state}, LastBuildInfo={solutionBuild.LastBuildInfo}");
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("[BuildService] DTE 构建等待超时或取消");
                return false;
            }
        }

        #endregion

        #region CMake CLI Build Helpers

        /// <summary>
        /// 查找 CMake 构建目录。
        /// 优先级：
        /// 1. CMakePresets.json 中 configurePresets[0].binaryDir
        /// 2. 默认目录 out/build/&lt;config&gt;（VS 默认）
        /// 3. 默认目录 build/&lt;config&gt;（CLI 默认）
        /// </summary>
        private static string? FindCmakeBuildDirectory(string workspaceDir)
        {
            // ── 方案1：从 CMakePresets.json 读取 ──
            string presetsPath = Path.Combine(workspaceDir, "CMakePresets.json");
            if (File.Exists(presetsPath))
            {
                string? fromPreset = ParseCmakePresetsBinaryDir(presetsPath, workspaceDir);
                if (!string.IsNullOrEmpty(fromPreset) && Directory.Exists(fromPreset))
                {
                    Logger.Info($"[BuildService] 从 CMakePresets.json 获取构建目录: {fromPreset}");
                    return fromPreset;
                }
            }

            // ── 方案2：检查常用默认目录 ──
            string[] candidateDirs =
            {
                Path.Combine(workspaceDir, "out", "build"),
                Path.Combine(workspaceDir, "build"),
            };

            // 在候选目录下查找包含 CMakeCache.txt 的子目录
            foreach (var candidate in candidateDirs)
            {
                if (!Directory.Exists(candidate)) continue;

                // 先尝试 candidate 自身
                if (File.Exists(Path.Combine(candidate, "CMakeCache.txt")))
                    return candidate;

                // 再尝试子目录（如 out/build/x64-Debug）
                try
                {
                    foreach (var subDir in Directory.GetDirectories(candidate))
                    {
                        if (File.Exists(Path.Combine(subDir, "CMakeCache.txt")))
                            return subDir;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// 从 CMakePresets.json 解析 binaryDir。
        /// 支持 ${sourceDir} 和 ${presetName} 宏展开。
        /// </summary>
        private static string? ParseCmakePresetsBinaryDir(string presetsPath, string workspaceDir)
        {
            try
            {
                string jsonText = File.ReadAllText(presetsPath);
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                // 优先读取 configurePresets
                if (root.TryGetProperty("configurePresets", out var configurePresets)
                    && configurePresets.ValueKind == JsonValueKind.Array
                    && configurePresets.GetArrayLength() > 0)
                {
                    var firstPreset = configurePresets[0];
                    if (firstPreset.TryGetProperty("binaryDir", out var binaryDirProp)
                        && binaryDirProp.ValueKind == JsonValueKind.String)
                    {
                        string binaryDir = binaryDirProp.GetString() ?? string.Empty;
                        string presetName = string.Empty;
                        if (firstPreset.TryGetProperty("name", out var nameProp))
                            presetName = nameProp.GetString() ?? string.Empty;

                        // 展开宏：${sourceDir} → workspaceDir, ${presetName} → presetName
                        binaryDir = binaryDir
                            .Replace("${sourceDir}", workspaceDir)
                            .Replace("${presetName}", presetName);

                        // 处理相对路径
                        if (!Path.IsPathRooted(binaryDir))
                            binaryDir = Path.GetFullPath(Path.Combine(workspaceDir, binaryDir));

                        return binaryDir;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] 解析 CMakePresets.json 失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 确定 CMake 构建配置（Debug / Release 等）。
        /// 从 CMakePresets.json 的 buildPresets 读取，或从 CMakeCache.txt 解析，
        /// 默认返回 Debug。
        /// </summary>
        private static string FindCmakeBuildConfig(string workspaceDir)
        {
            // ── 从 CMakePresets.json 读取 buildPresets[0].configuration ──
            string presetsPath = Path.Combine(workspaceDir, "CMakePresets.json");
            if (File.Exists(presetsPath))
            {
                try
                {
                    string jsonText = File.ReadAllText(presetsPath);
                    using var doc = JsonDocument.Parse(jsonText);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("buildPresets", out var buildPresets)
                        && buildPresets.ValueKind == JsonValueKind.Array
                        && buildPresets.GetArrayLength() > 0)
                    {
                        var firstBuildPreset = buildPresets[0];
                        if (firstBuildPreset.TryGetProperty("configuration", out var configProp)
                            && configProp.ValueKind == JsonValueKind.String)
                        {
                            string config = configProp.GetString() ?? "Debug";
                            Logger.Info($"[BuildService] 从 CMakePresets.json 获取构建配置: {config}");
                            return config;
                        }
                    }
                }
                catch { }
            }

            // ── 默认 Debug ──
            return "Debug";
        }

        /// <summary>
        /// 查找 cmake 可执行文件路径。
        /// 优先级：
        /// 1. PATH 环境变量中的 cmake
        /// 2. VS 自带的 CMake（通过统一 VS 路径发现）
        /// 3. vswhere 查询 VS 安装目录下的 CMake
        /// 4. 常见安装路径（使用环境变量，不硬编码盘符）
        /// </summary>
        private static string? FindCmakeExecutable()
        {
            // ── 1. 尝试 PATH 中的 cmake ──
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmake",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(2000);
                    if (process.ExitCode == 0)
                        return "cmake";
                }
            }
            catch
            {
                // cmake 不在 PATH 中
            }

            // ── 2. 通过统一 VS 安装路径发现 CMake ──
            string? vsPath = GetVisualStudioInstallPath();
            if (vsPath != null)
            {
                string cmakePath = Path.Combine(vsPath,
                    "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe");
                if (File.Exists(cmakePath))
                {
                    Logger.Info($"[BuildService] 找到 VS 自带 CMake: {cmakePath}");
                    return cmakePath;
                }
            }

            // ── 3. 常见安装路径（使用环境变量，支持任意盘符）──
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] commonPaths =
            {
                Path.Combine(progFiles, "CMake", "bin", "cmake.exe"),
                Path.Combine(progFilesX86, "CMake", "bin", "cmake.exe"),
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        /// <summary>
        /// 获取 Visual Studio 安装路径（统一入口）。
        /// 通过 SVsShell 服务获取当前 VS 实例的安装根目录。
        /// 必须在 UI 线程调用。
        /// </summary>
        private static string? GetVisualStudioInstallPath()
        {
            try
            {
                // ── UI 线程守卫 ──
                ThreadHelper.ThrowIfNotOnUIThread();

                if (ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) is IVsShell vsShell)
                {
                    if (ErrorHandler.Succeeded(vsShell.GetProperty(
                        (int)__VSSPROPID.VSSPROPID_InstallDirectory, out object installDirObj)))
                    {
                        string path = (installDirObj as string) ?? installDirObj.ToString();
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            // 末尾可能带 \，统一 TrimEnd
                            path = path.TrimEnd('\\');

                            // VSSPROPID_InstallDirectory 返回的是 <VS>\Common7\IDE，
                            // 而上层逻辑（FindVcvarsBat / FindCmakeExecutable）需要 VS 根目录。
                            // 上溯两级：IDE → Common7 → VS Root
                            string? ideDir = path;
                            if (ideDir.EndsWith("IDE", StringComparison.OrdinalIgnoreCase))
                            {
                                string? common7Dir = Path.GetDirectoryName(ideDir);
                                if (common7Dir != null)
                                {
                                    string? vsRoot = Path.GetDirectoryName(common7Dir);
                                    if (vsRoot != null && Directory.Exists(vsRoot))
                                        path = vsRoot;
                                }
                            }

                            Logger.Info($"[BuildService] SVsShell 获取 VS 安装路径: {path}");
                            return path;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] SVsShell 获取 VS 安装路径失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 查找 MSVC 环境初始化脚本 vcvars64.bat。
        /// 用于在命令行 CMake 构建前初始化编译器环境（INCLUDE、LIB、PATH 等），
        /// 解决直接调用 cmake --build 时找不到标准头文件的问题。
        /// </summary>
        private static string? FindVcvarsBat()
        {
            string? vsPath = GetVisualStudioInstallPath();
            if (vsPath != null)
            {
                string vcvarsPath = Path.Combine(vsPath, "VC", "Auxiliary", "Build", "vcvars64.bat");
                if (File.Exists(vcvarsPath))
                {
                    Logger.Info($"[BuildService] 找到 vcvars64.bat: {vcvarsPath}");
                    return vcvarsPath;
                }
            }

            return null;
        }

        /// <summary>
        /// 将 cmake --build 的输出格式化为结构化构建结果。
        /// </summary>
        private static string FormatCmakeBuildResult(int exitCode, string stdout, string stderr)
        {
            var sb = new StringBuilder();

            if (exitCode == 0)
            {
                sb.AppendLine(LocalizationService.Instance["build.cmake.success"]);
            }
            else
            {
                sb.AppendLine(string.Format(
                    LocalizationService.Instance["build.cmake.failedWithCode"], exitCode));
            }

            // ── 提取错误/警告行 ──
            string combined = stdout + "\n" + stderr;
            var errorLines = combined
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                    line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("warning", StringComparison.OrdinalIgnoreCase))
                .Take(50)
                .Select(line => line.Trim())
                .ToList();

            if (errorLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### " + LocalizationService.Instance["build.errorDetails"]);
                foreach (var line in errorLines)
                    sb.AppendLine($"- {line}");
            }
            else if (exitCode != 0)
            {
                // 没有明显的错误行时，显示最后部分输出
                var allLines = combined
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var tailLines = allLines
                    .Skip(Math.Max(0, allLines.Length - 20))
                    .Select(line => line.Trim())
                    .ToList();

                if (tailLines.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("### " + LocalizationService.Instance["build.cmake.outputTail"]);
                    foreach (var line in tailLines)
                        sb.AppendLine($"- {line}");
                }
            }

            string result = sb.ToString().TrimEnd();
            Logger.Info($"[BuildService] CMake 构建结果: {result.Truncate(500)}");
            return result;
        }

        /// <summary>
        /// CMake DTE 回退构建路径。
        /// 当命令行 cmake 不可用时，通过 DTE ExecuteCommand("Build.BuildSolution")
        /// 使用 VS 内置的 CMake 集成进行构建。
        /// 
        /// 等待策略（UI 线程安全）：
        /// 1. 优先：通过 IVsUpdateSolutionEvents（BuildEventsSink）事件驱动等待
        /// 2. 回退：UI 线程异步轮询 DTE SolutionBuild.BuildState
        /// </summary>
        private static async Task<string> BuildCmakeViaDteFallbackAsync(CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            EnvDTE.DTE? dte = null;

            try
            {
                dte = (EnvDTE.DTE?)ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    Logger.Warn("[BuildService] DTE 回退：无法获取 DTE");
                    return LocalizationService.Instance["build.cmake.notFound"];
                }

                var solutionBuild = dte.Solution?.SolutionBuild;
                if (solutionBuild == null)
                {
                    Logger.Warn("[BuildService] DTE 回退：SolutionBuild 不可用");
                    return LocalizationService.Instance["build.cmake.notFound"];
                }

                Logger.Info("[BuildService] DTE 回退：通过 ExecuteCommand 构建 (CMake)…");

                bool alreadyBuilding = solutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress;

                bool buildCompleted = await TriggerAndWaitForBuildViaEventsAsync(
                    dte, solutionBuild, needStart: !alreadyBuilding, ct, TimeSpan.FromMinutes(5));

                if (!buildCompleted)
                {
                    Logger.Warn("[BuildService] DTE 回退：构建等待超时（5 分钟）");
                    return LocalizationService.Instance["build.timeout"];
                }

                return BuildResultFromDte(solutionBuild);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[BuildService] DTE 回退：构建已被取消");
                try { dte?.ExecuteCommand("Build.Cancel"); } catch { }
                return LocalizationService.Instance["build.cancelled"];
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] DTE 回退构建异常: {ex.Message}");
                return string.Format(LocalizationService.Instance["build.dteFailed"], ex.Message);
            }
        }

        #endregion

        #region Project Detection

        /// <summary>
        /// 检测是否为 CMake / Open Folder 项目。
        /// 判定依据：工作区目录中存在 CMakeLists.txt 或 CMakePresets.json，
        /// 且没有 .sln 文件。
        /// </summary>
        private static bool IsCmakeProject(string? workspaceDir)
        {
            if (string.IsNullOrEmpty(workspaceDir) || !Directory.Exists(workspaceDir))
                return false;

            try
            {
                bool hasCmakeFile = File.Exists(Path.Combine(workspaceDir, "CMakeLists.txt"))
                    || File.Exists(Path.Combine(workspaceDir, "CMakePresets.json"));

                if (!hasCmakeFile) return false;

                // 确认没有 .sln 文件（纯 CMake 项目）
                bool hasSln = Directory.GetFiles(workspaceDir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0;
                return !hasSln;
            }
            catch
            {
                return false;
            }
        }

        private static string? NormalizeToDirectory(string? solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath))
                return null;

            try
            {
                if (File.Exists(solutionPath))
                    return Path.GetDirectoryName(solutionPath);
                if (Directory.Exists(solutionPath))
                    return solutionPath;
            }
            catch { }

            return null;
        }

        #endregion

        #region BuildEventsSink

        /// <summary>
        /// 构建事件接收器，实现 IVsUpdateSolutionEvents2 以监听构建开始/完成/取消/项目配置。
        /// 所有回调方法必须快速返回，避免阻塞事件流。
        /// </summary>
        private sealed class BuildEventsSink : IVsUpdateSolutionEvents2, IDisposable
        {
            private readonly TaskCompletionSource<bool> _tcs = new();
            private CancellationTokenRegistration _ctRegistration;
            private int _succeeded;
            private int _failed;
            private int _cancelled;
            private int _projectCount;
            private int _projectSucceeded;
            private int _projectFailed;
            private bool _disposed;
            private bool _completed;
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

            /// <summary>
            /// 构建事件是否已收到完成通知（UpdateSolution_Done 已触发）。
            /// 用于 API 调用返回错误但构建实际已完成的场景（CMake 项目常见）。
            /// </summary>
            public bool HasCompleted => Volatile.Read(ref _completed);

            public async Task<bool> WaitForCompletionAsync(CancellationToken ct, TimeSpan timeout)
            {
                _ctRegistration = ct.Register(() => _tcs.TrySetCanceled());
                try
                {
                    var completedTask = await Task.WhenAny(_tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
                    if (completedTask == _tcs.Task)
                    {
                        await completedTask.ConfigureAwait(false);
                        return true;
                    }
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            public void GetBuildResult(out int succeeded, out int failed, out int cancelled)
            {
                succeeded = _succeeded;
                failed = _failed;
                cancelled = _cancelled;
            }

            // ═══════════════════════════════════════════════════════════════
            // IVsUpdateSolutionEvents
            // ═══════════════════════════════════════════════════════════════

            public int UpdateSolution_Begin(ref int pfCancelUpdate)
            {
                Logger.Info($"[BuildEvents] 🔨 UpdateSolution_Begin (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                pfCancelUpdate = 0;
                return VSConstants.S_OK;
            }

            public int UpdateSolution_StartUpdate(ref int pfCancelUpdate)
            {
                Logger.Info($"[BuildEvents] 🔨 UpdateSolution_StartUpdate (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                pfCancelUpdate = 0;
                return VSConstants.S_OK;
            }

            public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
            {
                Volatile.Write(ref _completed, true);
                Logger.Info($"[BuildEvents] 🏁 UpdateSolution_Done: Succeeded={fSucceeded}, Modified={fModified}, Cancelled={fCancelCommand}, Projects={_projectCount} (ok={_projectSucceeded}, fail={_projectFailed}), Elapsed={_sw.Elapsed.TotalSeconds:F1}s");
                if (fCancelCommand != 0) _cancelled = 1;
                else if (fSucceeded != 0) _succeeded = 1;
                else _failed = 1;
                _tcs.TrySetResult(true);
                return VSConstants.S_OK;
            }

            public int UpdateSolution_StartUpdateProjectCfg(
                ref int pfCancel, IVsHierarchy pHierProj, IVsCfg pCfgProj,
                IVsCfg pCfgSln, uint dwProjectCfgOfInterest, uint dwCopyFlags, int fCancel)
            {
                Interlocked.Increment(ref _projectCount);
                Logger.Info($"[BuildEvents] 📦 StartUpdateProjectCfg #{_projectCount} (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                return VSConstants.S_OK;
            }

            public int UpdateSolution_Cancel()
            {
                Logger.Warn($"[BuildEvents] ⚠️ UpdateSolution_Cancel (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                _cancelled = 1;
                _tcs.TrySetResult(true);
                return VSConstants.S_OK;
            }

            public int OnActiveProjectCfgChange(IVsHierarchy pHierarchy)
            {
                Logger.Info($"[BuildEvents] 🔄 OnActiveProjectCfgChange (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                return VSConstants.S_OK;
            }

            // ═══════════════════════════════════════════════════════════════
            // IVsUpdateSolutionEvents2 — 增强事件（VS 2010+）
            // ═══════════════════════════════════════════════════════════════

            public int UpdateSolution_Begin2(ref int pfCancelUpdate)
            {
                Logger.Info($"[BuildEvents] 🔨 UpdateSolution_Begin2 (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                pfCancelUpdate = 0;
                return VSConstants.S_OK;
            }

            public int UpdateSolution_StartUpdate2(ref int pfCancelUpdate)
            {
                Logger.Info($"[BuildEvents] 🔨 UpdateSolution_StartUpdate2 (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                pfCancelUpdate = 0;
                return VSConstants.S_OK;
            }

            public int UpdateSolution_Done2(int fSucceeded, int fModified, int fCancelCommand, string? pszUpdatedProjects)
            {
                Logger.Info($"[BuildEvents] 🏁 UpdateSolution_Done2: Succeeded={fSucceeded}, Modified={fModified}, Cancelled={fCancelCommand}, UpdatedProjects={pszUpdatedProjects ?? "(none)"}, Elapsed={_sw.Elapsed.TotalSeconds:F1}s");
                // Done2 在 Done 之前触发，不重复设置 _tcs
                return VSConstants.S_OK;
            }

            public int UpdateSolution_StartUpdateProjectCfg2(
                ref int pfCancel, IVsHierarchy pHierProj, IVsCfg pCfgProj,
                IVsCfg pCfgSln, uint dwProjectCfgOfInterest, uint dwCopyFlags, int fCancel)
            {
                Logger.Info($"[BuildEvents] 📦 StartUpdateProjectCfg2 #{_projectCount} (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                return VSConstants.S_OK;
            }

            public int UpdateSolution_ProjectUpdateDone(
                int fSucceeded, int fModified, int fCancelCommand, string? pszProject)
            {
                if (fSucceeded != 0)
                    Interlocked.Increment(ref _projectSucceeded);
                else if (fCancelCommand == 0)
                    Interlocked.Increment(ref _projectFailed);
                Logger.Info($"[BuildEvents] 📦 ProjectUpdateDone: Succeeded={fSucceeded}, Project={pszProject ?? "(unknown)"}, Accumulated(ok={_projectSucceeded}, fail={_projectFailed})");
                return VSConstants.S_OK;
            }

            public int OnActiveProjectCfgChange2(IVsHierarchy pHierarchy, string? pszActiveConfig)
            {
                Logger.Info($"[BuildEvents] 🔄 OnActiveProjectCfgChange2: Config={pszActiveConfig ?? "(none)"}");
                return VSConstants.S_OK;
            }

            // ═══════════════════════════════════════════════════════════════
            // IVsUpdateSolutionEvents2 — 项目配置级事件（VS 2010+）
            // ═══════════════════════════════════════════════════════════════

            public int UpdateProjectCfg_Begin(
                IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln,
                uint dwAction, ref int pfCancel)
            {
                Logger.Info($"[BuildEvents] 📦 UpdateProjectCfg_Begin: Action={dwAction} (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                return VSConstants.S_OK;
            }

            public int UpdateProjectCfg_Done(
                IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln,
                uint dwAction, int fSuccess, int fCancel)
            {
                Logger.Info($"[BuildEvents] 📦 UpdateProjectCfg_Done: Action={dwAction}, Success={fSuccess}, Cancel={fCancel} (elapsed={_sw.Elapsed.TotalSeconds:F1}s)");
                return VSConstants.S_OK;
            }

            // ═══════════════════════════════════════════════════════════════
            // IDisposable
            // ═══════════════════════════════════════════════════════════════

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _ctRegistration.Dispose();
                _tcs.TrySetResult(false);
            }
        }

        #endregion
    }
}
