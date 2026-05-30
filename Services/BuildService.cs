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

            // ── 检测是否为 CMake / Open Folder 项目（用于日志）──
            bool isCmakeProject = IsCmakeProject(workspaceDir);

            Logger.Info($"[BuildService] 开始构建 (CMake={isCmakeProject}, workspace={workspaceDir ?? "(null)"})");

            // ── 方案1：IVsSolutionBuildManager（VS 2022 支持 .sln 和 CMake/Open Folder）──
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

        /// <summary>
        /// 异步启动构建 — 通过 DTE ExecuteCommand 触发构建后立即返回。
        /// 不等待构建完成，构建结果会输出到 VS 输出窗口和错误列表。
        /// 适用于 CMake/Open Folder 项目（IVsSolutionBuildManager 和 DTE SolutionBuild.Build 均不兼容）。
        /// </summary>
        public async Task<string> StartBuildAsync(string? solutionPath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string? workspaceDir = NormalizeToDirectory(solutionPath);
            bool isCmakeProject = IsCmakeProject(workspaceDir);

            Logger.Info($"[BuildService] 异步启动构建 (CMake={isCmakeProject}, workspace={workspaceDir ?? "(null)"})");

            try
            {
                var dte = (EnvDTE.DTE?)ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    Logger.Warn("[BuildService] 无法获取 DTE，异步构建启动失败");
                    return "❌ 无法连接到 Visual Studio DTE，请确认 VS 已打开解决方案/文件夹。";
                }

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                {
                    Logger.Warn("[BuildService] 未打开解决方案/文件夹，异步构建启动失败");
                    return "❌ 当前未打开任何解决方案或文件夹，请先在 VS 中打开项目。";
                }

                // ── 检查是否已有构建在进行中 ──
                var solutionBuild = dte.Solution?.SolutionBuild;
                if (solutionBuild != null && solutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
                {
                    Logger.Info("[BuildService] 构建已在后台运行中");
                    return "🔨 构建已在后台运行中。请等待构建完成后使用 get_errors 检查编译错误。";
                }

                // ── 通过 ExecuteCommand 启动构建（兼容 CMake/MSBuild 等所有项目类型）──
                Logger.Info("[BuildService] 通过 DTE ExecuteCommand 异步启动构建...");
                dte.ExecuteCommand("Build.BuildSolution");

                string projectType = isCmakeProject ? "CMake" : "MSBuild";
                return $"🔨 构建已启动（{projectType} 项目）。\n\n" +
                       $"构建正在后台运行，输出将显示在 VS 输出窗口和错误列表中。\n" +
                       $"请在构建完成后使用 get_errors 工具检查编译错误。\n" +
                       $"💡 提示：CMake 项目构建通常需要 1-5 分钟，大型项目可能更长。";
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] 异步构建启动异常: {ex.Message}");
                return $"❌ 构建启动失败: {ex.Message}";
            }
        }

        #region Build Methods

        /// <summary>
        /// CMake/Open Folder 项目专用构建路径。
        /// 跳过 IVsSolutionBuildManager 和 DTE SolutionBuild.Build（两者对 CMake 项目均不兼容），
        /// 直接使用 DTE ExecuteCommand("Build.BuildSolution") 触发构建并等待完成。
        /// </summary>
        private static async Task<string> BuildWithExecuteCommandAsync(CancellationToken ct)
        {
            try
            {
                var dte = (EnvDTE.DTE?)ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    Logger.Warn("[BuildService] 无法获取 DTE");
                    return LocalizationService.Instance["build.noBuildSystem"];
                }

                var solutionBuild = dte.Solution?.SolutionBuild;
                if (solutionBuild == null)
                {
                    Logger.Warn("[BuildService] DTE.SolutionBuild 不可用");
                    return LocalizationService.Instance["build.noBuildSystem"];
                }

                // ── 等待 DTE 就绪 ──
                if (solutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
                {
                    Logger.Info("[BuildService] 构建已在运行中，等待完成...");
                    if (!WaitForDteBuildCompletion(solutionBuild, TimeSpan.FromMinutes(5)))
                    {
                        return LocalizationService.Instance["build.timeout"];
                    }
                    return BuildResultFromDte(solutionBuild);
                }

                // ── 启动构建 ──
                Logger.Info("[BuildService] 正在通过 ExecuteCommand 构建解决方案 (CMake 项目)...");
                dte.ExecuteCommand("Build.BuildSolution");

                // ── 等待构建完成 ──
                if (!WaitForDteBuildCompletion(solutionBuild, TimeSpan.FromMinutes(5)))
                {
                    Logger.Warn("[BuildService] ⚠️ 构建等待超时（5 分钟）");
                    return LocalizationService.Instance["build.timeout"];
                }

                return BuildResultFromDte(solutionBuild);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildService] CMake 构建异常: {ex.Message}");
                return string.Format(LocalizationService.Instance["build.dteFailed"], ex.Message);
            }
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
                bool buildSuccess = await Task.Run(() =>
                {
                    // ── 方案 A：SolutionBuild.Build（等待构建完成，兼容 CMake/Open Folder）──
                    try
                    {
                        solutionBuild.Build(true); // true = WaitForBuildToFinish
                        return solutionBuild.LastBuildInfo == 0; // 0 errors = success
                    }
                    catch (Exception exBuild)
                    {
                        Logger.Warn($"[BuildService] DTE SolutionBuild.Build 异常: {exBuild.Message}");

                        // ── 方案 B：ExecuteCommand 回退（部分项目类型不支持 Build 方法）──
                        try
                        {
                            dte.ExecuteCommand("Build.BuildSolution");
                            // ExecuteCommand 是异步的，必须等待构建实际完成
                            if (!WaitForDteBuildCompletion(solutionBuild, TimeSpan.FromMinutes(5)))
                            {
                                Logger.Warn("[BuildService] ⚠️ DTE 构建等待超时");
                                return false;
                            }
                            return solutionBuild.LastBuildInfo == 0;
                        }
                        catch (Exception exCmd)
                        {
                            Logger.Warn($"[BuildService] DTE ExecuteCommand 异常: {exCmd.Message}");
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
        /// 等待 DTE 构建完成（ExecuteCommand 是异步的，必须轮询 BuildState）。
        /// 超时返回 false。
        /// </summary>
        private static bool WaitForDteBuildCompletion(EnvDTE.SolutionBuild solutionBuild, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int pollIntervalMs = 500;

            // ── 先等待构建开始（BuildState 变为 InProgress）──
            while (solutionBuild.BuildState != EnvDTE.vsBuildState.vsBuildStateInProgress)
            {
                if (sw.Elapsed >= timeout)
                {
                    Logger.Warn("[BuildService] ⏱️ 等待 DTE 构建启动超时");
                    return false;
                }
                System.Threading.Thread.Sleep(pollIntervalMs);
            }

            Logger.Info("[BuildService] DTE 构建已启动，等待完成...");

            // ── 等待构建完成（BuildState 不再是 InProgress）──
            while (solutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
            {
                if (sw.Elapsed >= timeout)
                {
                    Logger.Warn("[BuildService] ⏱️ DTE 构建执行超时");
                    return false;
                }
                System.Threading.Thread.Sleep(pollIntervalMs);
            }

            Logger.Info($"[BuildService] DTE 构建完成，耗时 {sw.Elapsed.TotalSeconds:F1}s, LastBuildInfo={solutionBuild.LastBuildInfo}");
            return true;
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
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

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
