using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 构建服务实现 — 封装 VS SDK 构建交互。
    /// 
    /// 支持：
    /// - MSBuild .sln 项目（通过 IVsSolutionBuildManager）
    /// - CMake / Open Folder 项目（回退到 DTE.SolutionBuild）
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

            // ── 检测是否为 CMake / Open Folder 项目（用于日志和回退策略）──
            bool isCmakeProject = IsCmakeProject(workspaceDir);

            Logger.Info($"[BuildService] 开始构建 (CMake={isCmakeProject}, workspace={workspaceDir ?? "(null)"})");

            // ── 方案1：IVsSolutionBuildManager（VS 2022 支持 .sln 和 CMake/Open Folder）──
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

            // 检查构建管理器是否正忙
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

                int hr = buildManager.StartSimpleUpdateSolutionConfiguration(
                    (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD,
                    0, 0);

                if (hr < 0)
                {
                    Logger.Warn($"[BuildService] StartSimpleUpdateSolutionConfiguration 失败: 0x{hr:X8}");
                    return null; // 回退到 DTE 方式
                }

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

                // 检查是否已有构建在进行
                if (solutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
                {
                    return LocalizationService.Instance["build.alreadyInProgress"];
                }

                // 启动构建（DTE 方式是同步阻塞的，用 Task.Run 包裹）
                bool buildSuccess = await Task.Run(() =>
                {
                    // ── 方案 A：ExecuteCommand（兼容 CMake/Open Folder）──
                    try
                    {
                        dte.ExecuteCommand("Build.BuildSolution");
                        return solutionBuild.LastBuildInfo == 0; // 0 errors = success
                    }
                    catch (Exception exCmd)
                    {
                        Logger.Warn($"[BuildService] DTE ExecuteCommand 异常: {exCmd.Message}");

                        // ── 方案 B：SolutionBuild.Build 回退（传统 .sln 项目）──
                        try
                        {
                            solutionBuild.Build(true); // true = WaitForBuildToFinish
                            return solutionBuild.LastBuildInfo == 0;
                        }
                        catch (Exception exBuild)
                        {
                            Logger.Warn($"[BuildService] DTE SolutionBuild.Build 异常: {exBuild.Message}");
                            return false;
                        }
                    }
                }, ct);

                if (buildSuccess)
                {
                    Logger.Info("[BuildService] ✅ DTE 构建成功");
                    return LocalizationService.Instance["build.dteSuccess"];
                }

                // 收集错误
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
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] DTE 构建异常: {ex.Message}");
                return string.Format(LocalizationService.Instance["build.dteFailed"], ex.Message);
            }
        }

        #endregion

        #region Result Helpers

        private static string BuildResultFromEvents(BuildEventsSink sink)
        {
            sink.GetBuildResult(out int succeeded, out int failed, out int cancelled);

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

            string errors = CollectBuildErrors();
            var result = new StringBuilder();
            result.AppendLine(string.Format(LocalizationService.Instance["build.projectsFailed"], failed));
            if (!string.IsNullOrEmpty(errors))
            {
                result.AppendLine();
                result.AppendLine("## " + LocalizationService.Instance["build.errorDetails"]);
                result.Append(errors);
            }

            string fullResult = result.ToString();
            Logger.Info($"[BuildService] {fullResult.Truncate(500)}");
            return fullResult;
        }

        #endregion

        #region Error Collection

        /// <summary>
        /// 从 VS Task List 收集编译错误详情。
        /// 按文件分组，每个错误包含文件名、行号、错误描述。
        /// 公开为 internal static，供 BuiltInToolService.get_errors 复用。
        /// </summary>
        internal static string CollectBuildErrors()
        {
            var sb = new StringBuilder();

            try
            {
                // ── 方案一：IVsTaskList（VS SDK Interop 原生接口）──
                var taskList = (IVsTaskList?)ServiceProvider.GlobalProvider
                    .GetService(typeof(SVsTaskList));
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
                            Logger.Info($"[BuildService] IVsTaskList 收集到 {errorsByFile.Sum(k => k.Value.Count)} 个编译错误，分布在 {errorsByFile.Count} 个文件");
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
                            Logger.Info("[BuildService] IVsTaskList 中未找到编译错误条目，回退到 Output Window");
                        }
                    }
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

        private static void TryCollectFromOutputWindow(StringBuilder sb)
        {
            try
            {
                var dte = (EnvDTE.DTE?)ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dte == null) return;

                EnvDTE.Window window = dte.Windows.Item(EnvDTE.Constants.vsWindowKindOutput);
                EnvDTE.OutputWindow outputWin = (EnvDTE.OutputWindow)window.Object;

                const string buildPaneGuid = "{1BD8A850-02D1-11D1-BEE7-00A0C913D83C}";
                EnvDTE.OutputWindowPane? buildPane = null;
                foreach (EnvDTE.OutputWindowPane pane in outputWin.OutputWindowPanes)
                {
                    if (pane.Guid == buildPaneGuid)
                    {
                        buildPane = pane;
                        break;
                    }
                }

                if (buildPane == null) return;

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
                Logger.Warn($"[BuildService] Output 窗口读取失败: {ex.Message}");
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
        /// 构建事件接收器，实现 IVsUpdateSolutionEvents 以监听构建开始/完成/取消。
        /// </summary>
        private sealed class BuildEventsSink : IVsUpdateSolutionEvents, IDisposable
        {
            private readonly TaskCompletionSource<bool> _tcs = new();
            private CancellationTokenRegistration _ctRegistration;
            private int _succeeded;
            private int _failed;
            private int _cancelled;
            private bool _disposed;

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

            public int UpdateSolution_Begin(ref int pfCancelUpdate) { pfCancelUpdate = 0; return VSConstants.S_OK; }
            public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) { pfCancelUpdate = 0; return VSConstants.S_OK; }

            public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
            {
                if (fCancelCommand != 0) _cancelled = 1;
                else if (fSucceeded != 0) _succeeded = 1;
                else _failed = 1;
                _tcs.TrySetResult(true);
                return VSConstants.S_OK;
            }

            public int UpdateSolution_StartUpdateProjectCfg(
                ref int pfCancel, IVsHierarchy pHierProj, IVsCfg pCfgProj,
                IVsCfg pCfgSln, uint dwProjectCfgOfInterest, uint dwCopyFlags, int fCancel)
                => VSConstants.S_OK;

            public int UpdateSolution_Cancel()
            {
                _cancelled = 1;
                _tcs.TrySetResult(true);
                return VSConstants.S_OK;
            }

            public int OnActiveProjectCfgChange(IVsHierarchy pHierarchy) => VSConstants.S_OK;

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
