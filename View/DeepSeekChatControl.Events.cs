using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// UI 事件处理器：输入框键盘、按钮点击、下拉框选择、WebView2 事件等。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Event Handlers - Input

        /// <summary>
        /// 输入框键盘事件：Enter 直接发送消息，Ctrl+Enter 插入换行，Ctrl+V 粘贴图片。
        /// Ctrl+V 在隧道阶段拦截，优先于 TextBox 内部命令绑定，确保图片粘贴可靠触发。
        /// </summary>
        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ── Ctrl+V: 优先检查剪贴板图片，在隧道阶段拦截 ──
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Logger.Info("PreviewKeyDown 检测到 Ctrl+V，检查剪贴板...");
                bool hasImage = TryPasteClipboardImage();
                if (hasImage)
                {
                    Logger.Info("Ctrl+V 已作为图片粘贴处理，拦截事件。");
                    e.Handled = true; // 拦截，阻止 TextBox 的默认文本粘贴
                    return;
                }
                Logger.Info("Ctrl+V 剪贴板无图片，交由 TextBox 默认文本粘贴处理。");
                // 无图片时放行，让 TextBox 默认行为处理文本粘贴
                return;
            }

            // ── Agent 弹出框键盘导航 ──
            if (AgentSuggestionPopup.IsOpen)
            {
                if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    NavigateAgentSuggestion(1);
                    return;
                }
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    NavigateAgentSuggestion(-1);
                    return;
                }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    e.Handled = true;
                    AcceptAgentSuggestion();
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    AgentSuggestionPopup.IsOpen = false;
                    return;
                }
            }

            // ── Skill 弹出框键盘导航 ──
            if (SkillSuggestionPopup.IsOpen)
            {
                if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    NavigateSkillSuggestion(1);
                    return;
                }
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    NavigateSkillSuggestion(-1);
                    return;
                }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    e.Handled = true;
                    AcceptSkillSuggestion();
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    SkillSuggestionPopup.IsOpen = false;
                    return;
                }
            }

            // ── 编辑模式下 ESC 取消编辑 ──
            if (e.Key == Key.Escape && _pendingEditMsgIndex >= 0)
            {
                e.Handled = true;
                _ = HandleEditCancelAsync(_pendingEditMsgIndex);
                return;
            }

            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Enter: 插入换行
                    e.Handled = false;
                    return;
                }

                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Shift+Enter: 插入换行
                    e.Handled = false;
                    return;
                }

                // 如果弹出框打开，Enter 优先选择技能/Agent
                if (SkillSuggestionPopup.IsOpen)
                {
                    e.Handled = true;
                    AcceptSkillSuggestion();
                    return;
                }
                if (AgentSuggestionPopup.IsOpen)
                {
                    e.Handled = true;
                    AcceptAgentSuggestion();
                    return;
                }

                // 普通 Enter: 发送消息
                e.Handled = true;
                SendMessage();
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            // 如果弹出框打开，先关闭
            if (SkillSuggestionPopup.IsOpen)
            {
                SkillSuggestionPopup.IsOpen = false;
            }
            if (AgentSuggestionPopup.IsOpen)
            {
                AgentSuggestionPopup.IsOpen = false;
            }
            SendMessage();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopGeneration();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearConversation();
        }

        /// <summary>
        /// 文件上传按钮点击：打开文件选择对话框，将选中文件添加到附件列表。
        /// </summary>
        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要上传的文件",
                Filter = FileParserService.GetFileFilter(),
                Multiselect = true,
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (string filePath in dlg.FileNames)
                {
                    if (!_attachedFilePaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                    {
                        if (FileParserService.IsSupportedFormat(filePath))
                        {
                            _attachedFilePaths.Add(filePath);
                        }
                        else
                        {
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.fileFormatUnsupported"], System.IO.Path.GetExtension(filePath));
                        }
                    }
                }
                RefreshAttachedFilesUI();
            }
        }

        /// <summary>
        /// 移除单个已上传文件。
        /// </summary>
        private void RemoveAttachedFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string fileName)
            {
                // 根据文件名找到对应路径并移除
                var pathToRemove = _attachedFilePaths.FirstOrDefault(
                    p => string.Equals(System.IO.Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
                if (pathToRemove != null)
                {
                    _attachedFilePaths.Remove(pathToRemove);
                    RefreshAttachedFilesUI();
                }
            }
        }

        /// <summary>
        /// 刷新附件文件标签 UI。
        /// </summary>
        private void RefreshAttachedFilesUI()
        {
            AttachedFilesControl.ItemsSource = null;
            AttachedFilesControl.ItemsSource = _attachedFilePaths
                .Select(p => System.IO.Path.GetFileName(p))
                .ToList();
        }

        /// <summary>
        /// 清空已上传的文件列表。
        /// </summary>
        private void ClearAttachedFiles()
        {
            _attachedFilePaths.Clear();
            RefreshAttachedFilesUI();
        }

        /// <summary>
        /// ＋ 按钮点击：弹出上下文菜单 Popup。
        /// </summary>
        private void AddContextButton_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = !AddContextPopup.IsOpen;
        }

        /// <summary>
        /// 添加上下文菜单 - 活动文档：将当前编辑器中的活动文档内容添加到对话上下文。
        /// </summary>
        private async void AddActiveDocument_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                var doc = dte?.ActiveDocument;
                if (doc == null)
                {
                    StatusLabel.Text = "⚠️ 没有打开的活动文档";
                    return;
                }

                string filePath = doc.FullName;
                string language = doc.Language;

                // 检查是否已添加
                if (_attachedFilePaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                {
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.context.fileAlreadyAttached"], System.IO.Path.GetFileName(filePath));
                    return;
                }

                // 活动文档直接添加文件路径（FileParserService 会读取内容）
                if (FileParserService.IsSupportedFormat(filePath))
                {
                    _attachedFilePaths.Add(filePath);
                    RefreshAttachedFilesUI();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.context.fileAttached"], System.IO.Path.GetFileName(filePath), language);
                    Logger.Info($"[AddContext] 活动文档已添加: {filePath}, 语言={language}");
                }
                else
                {
                    // 对于不支持的格式，将文件内容保存为临时 .txt 文件后添加
                    var textDoc = (EnvDTE.TextDocument)doc.Object("TextDocument");
                    string content = textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);

                    string tempDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DeepSeekVS", "temp", "context");
                    System.IO.Directory.CreateDirectory(tempDir);
                    string tempPath = System.IO.Path.Combine(tempDir,
                        $"doc_{System.IO.Path.GetFileNameWithoutExtension(filePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    System.IO.File.WriteAllText(tempPath, content);

                    _attachedFilePaths.Add(tempPath);
                    RefreshAttachedFilesUI();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.context.fileAttached"], System.IO.Path.GetFileName(filePath), language);
                    Logger.Info($"[AddContext] 活动文档已保存为临时文件并添加: {tempPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"添加活动文档失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.addDocFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 添加上下文菜单 - 项目文件：打开文件对话框，选择项目中的文件。
        /// </summary>
        private void AddProjectFiles_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要添加到上下文的项目文件",
                Filter = FileParserService.GetFileFilter(),
                Multiselect = true,
            };

            if (dlg.ShowDialog() == true)
            {
                int addedCount = 0;
                foreach (string filePath in dlg.FileNames)
                {
                    if (!_attachedFilePaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                    {
                        if (FileParserService.IsSupportedFormat(filePath))
                        {
                            _attachedFilePaths.Add(filePath);
                            addedCount++;
                        }
                        else
                        {
                            Logger.Info($"[AddContext] 不支持的文件格式，跳过: {filePath}");
                        }
                    }
                }
                RefreshAttachedFilesUI();
                StatusLabel.Text = addedCount > 0
                    ? $"📁 已添加 {addedCount} 个文件到上下文"
                    : "⚠️ 未添加新文件（已存在或格式不支持）";
                Logger.Info($"[AddContext] 项目文件已添加: {addedCount} 个");
            }
        }

        /// <summary>
        /// 添加上下文菜单 - 项目全部文件：扫描解决方案中所有项目的源代码文件并添加到上下文。
        /// </summary>
        private async void AddAllProjectFiles_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                if (dte?.Solution == null || !dte.Solution.IsOpen)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.search.noSolution"];
                    return;
                }

                StatusLabel.Text = LocalizationService.Instance["status.search.scanningFiles"];
                int addedCount = 0;
                int skippedCount = 0;
                var sourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".cs", ".vb", ".cpp", ".c", ".h", ".hpp", ".fs", ".fsx",
                    ".xaml", ".xml", ".json", ".config", ".csproj", ".vbproj",
                    ".py", ".js", ".ts", ".jsx", ".tsx", ".css", ".scss", ".less",
                    ".html", ".htm", ".razor", ".cshtml", ".vbhtml",
                    ".sql", ".md", ".txt", ".yml", ".yaml", ".ps1", ".psm1",
                    ".go", ".rs", ".java", ".kt", ".swift", ".proto",
                };

                // 遍历解决方案中的所有项目
                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    try
                    {
                        var (projAdded, projSkipped) = await AddProjectItemsRecursiveAsync(project.ProjectItems, sourceExtensions);
                        addedCount += projAdded;
                        skippedCount += projSkipped;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[AddContext] 扫描项目 {project.Name} 时出错: {ex.Message}");
                    }
                }

                RefreshAttachedFilesUI();
                StatusLabel.Text = addedCount > 0
                    ? $"📦 已添加 {addedCount} 个项目文件到上下文" + (skippedCount > 0 ? $" (跳过 {skippedCount} 个)" : "")
                    : "⚠️ 未找到可添加的源代码文件";
                Logger.Info($"[AddContext] 项目全部文件已添加: {addedCount} 个, 跳过: {skippedCount} 个");
            }
            catch (Exception ex)
            {
                Logger.Error($"添加项目全部文件失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.addAllFilesFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 递归遍历项目项，收集源代码文件。
        /// 返回 (addedCount, skippedCount)。
        /// </summary>
        private async Task<(int added, int skipped)> AddProjectItemsRecursiveAsync(
            EnvDTE.ProjectItems projectItems,
            HashSet<string> sourceExtensions)
        {
            int addedCount = 0;
            int skippedCount = 0;
            if (projectItems == null) return (addedCount, skippedCount);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            foreach (EnvDTE.ProjectItem item in projectItems)
            {
                try
                {
                    // 递归处理子项（文件夹）
                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        var (childAdded, childSkipped) = await AddProjectItemsRecursiveAsync(item.ProjectItems, sourceExtensions);
                        addedCount += childAdded;
                        skippedCount += childSkipped;
                    }

                    // 检查文件
                    string? filePath = null;
                    try { filePath = item.FileNames[0]; } catch { /* 某些项没有文件名 */ }

                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                    {
                        string ext = System.IO.Path.GetExtension(filePath);
                        if (sourceExtensions.Contains(ext) || FileParserService.IsSupportedFormat(filePath!))
                        {
                            if (!_attachedFilePaths.Contains(filePath!, StringComparer.OrdinalIgnoreCase))
                            {
                                _attachedFilePaths.Add(filePath!);
                                addedCount++;
                            }
                            else
                            {
                                skippedCount++;
                            }
                        }
                    }
                }
                catch
                {
                    // 跳过无法访问的项
                }
            }

            return (addedCount, skippedCount);
        }

        /// <summary>
        /// 添加上下文菜单 - 选中代码块：将编辑器选中的代码保存为临时文件并添加到上下文。
        /// </summary>
        private async void AddSelectedCode_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                var doc = dte?.ActiveDocument;
                if (doc == null)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.noActiveDoc"];
                    return;
                }

                var textDoc = (EnvDTE.TextDocument)doc.Object("TextDocument");
                var selection = textDoc.Selection as EnvDTE.TextSelection;
                if (selection == null || selection.IsEmpty)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.noSelection"];
                    return;
                }

                string selectedCode = selection.Text;
                if (string.IsNullOrWhiteSpace(selectedCode))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.selectionEmpty"];
                    return;
                }

                string language = doc.Language ?? "text";
                string fileExt = GetFileExtensionForLanguage(language);

                // 保存选中代码到临时文件
                string tempDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekVS", "temp", "context");
                System.IO.Directory.CreateDirectory(tempDir);
                string tempPath = System.IO.Path.Combine(tempDir,
                    $"selection_{DateTime.Now:yyyyMMdd_HHmmss}.{fileExt}");
                System.IO.File.WriteAllText(tempPath, selectedCode);

                _attachedFilePaths.Add(tempPath);
                RefreshAttachedFilesUI();
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.context.selectionAttached"], selectedCode.Length, language);
                Logger.Info($"[AddContext] 选中代码块已添加: {tempPath}, 长度={selectedCode.Length}, 语言={language}");
            }
            catch (Exception ex)
            {
                Logger.Error($"添加选中代码块失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.addSelectionFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 添加上下文菜单 - 调试输出：捕获输出窗口中的调试信息，
        /// 并自动提取报错中引用的文件一并加入上下文。
        /// </summary>
        private async void AddDebugOutput_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.noDebugEnv"];
                    return;
                }

                // 尝试从输出窗口获取调试输出
                string debugOutput = await CaptureDebugOutputAsync(dte);

                if (string.IsNullOrWhiteSpace(debugOutput))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.noDebugOutput"];
                    return;
                }

                // 保存调试输出到临时文件
                string tempDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekVS", "temp", "context");
                System.IO.Directory.CreateDirectory(tempDir);
                string tempPath = System.IO.Path.Combine(tempDir,
                    $"debug_output_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                System.IO.File.WriteAllText(tempPath, debugOutput);

                _attachedFilePaths.Add(tempPath);
                RefreshAttachedFilesUI();

                // ── 从调试输出中提取报错引用的文件路径并添加上下文 ──
                int referencedFileCount = await ExtractAndAddReferencedFilesAsync(debugOutput);

                StatusLabel.Text = referencedFileCount > 0
                    ? $"🐛 已添加调试输出 ({debugOutput.Length} 字符) + {referencedFileCount} 个关联文件"
                    : $"🐛 已添加调试输出 ({debugOutput.Length} 字符)";
                Logger.Info($"[AddContext] 调试输出已添加: {tempPath}, 长度={debugOutput.Length}, 关联文件={referencedFileCount} 个");
            }
            catch (Exception ex)
            {
                Logger.Error($"添加调试输出失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.addDebugFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 从调试/构建输出中提取报错引用的文件路径，并添加到附件列表。
        /// 支持 MSBuild 格式 (file.cs(line,col): error) 和通用路径格式。
        /// </summary>
        private async Task<int> ExtractAndAddReferencedFilesAsync(string output)
        {
            int addedCount = 0;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                try
                {
                    // 模式 1: MSBuild/编译器格式  path\to\file.ext(12,34): error CS0001: ...
                    var msbuildPattern = new System.Text.RegularExpressions.Regex(
                        @"([A-Za-z]:[^(\r\n]+?\.\w+)\s*\(\d+",
                        System.Text.RegularExpressions.RegexOptions.Multiline);

                    foreach (System.Text.RegularExpressions.Match match in msbuildPattern.Matches(output))
                    {
                        string candidate = match.Groups[1].Value.Trim();
                        if (System.IO.File.Exists(candidate) && seenPaths.Add(candidate))
                        {
                            Logger.Info($"[AddContext] 从调试输出提取文件: {candidate}");
                        }
                    }

                    // 模式 2: 绝对路径格式 (带盘符的常见扩展名)
                    var absPathPattern = new System.Text.RegularExpressions.Regex(
                        @"([A-Za-z]:[^\s""']+?\.(cs|vb|cpp|c|h|hpp|fs|py|js|ts|jsx|tsx|xaml|xml|json|config|csproj|vbproj|sql|md|txt|yml|yaml))\b",
                        System.Text.RegularExpressions.RegexOptions.Multiline);

                    foreach (System.Text.RegularExpressions.Match match in absPathPattern.Matches(output))
                    {
                        string candidate = match.Groups[1].Value.Trim();
                        if (System.IO.File.Exists(candidate) && seenPaths.Add(candidate))
                        {
                            Logger.Info($"[AddContext] 从调试输出提取文件: {candidate}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AddContext] 提取关联文件失败: {ex.Message}");
                }
            });

            // 添加提取到的文件
            foreach (string filePath in seenPaths)
            {
                if (!_attachedFilePaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                {
                    if (FileParserService.IsSupportedFormat(filePath))
                    {
                        _attachedFilePaths.Add(filePath);
                        addedCount++;
                    }
                    else
                    {
                        Logger.Info($"[AddContext] 跳过不支持的关联文件格式: {filePath}");
                    }
                }
            }

            if (addedCount > 0)
            {
                RefreshAttachedFilesUI();
            }

            return addedCount;
        }

        /// <summary>
        /// 从 Visual Studio 输出窗口捕获调试输出。
        /// 尝试读取"调试"窗格的内容，如果不可用则读取"生成"窗格。
        /// </summary>
        private async Task<string> CaptureDebugOutputAsync(EnvDTE80.DTE2 dte)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // 通过 DTE2 的 ToolWindows.OutputWindow 获取输出窗格列表
                var panes = dte.ToolWindows.OutputWindow.OutputWindowPanes;
                var debugPaneNames = new[] { "调试", "Debug", "生成", "Build" };

                foreach (string paneName in debugPaneNames)
                {
                    try
                    {
                        var pane = panes.Item(paneName);
                        if (pane != null)
                        {
                            var textDoc = pane.TextDocument;
                            if (textDoc != null)
                            {
                                var content = textDoc.StartPoint.CreateEditPoint()
                                    .GetText(textDoc.EndPoint);
                                if (!string.IsNullOrWhiteSpace(content))
                                {
                                    Logger.Info($"[AddContext] 从输出窗格 \"{paneName}\" 获取内容，长度={content.Length}");
                                    return content;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 窗格不存在则跳过
                    }
                }

                // 遍历所有窗格查找非空内容
                foreach (EnvDTE.OutputWindowPane pane in panes)
                {
                    try
                    {
                        var textDoc = pane.TextDocument;
                        if (textDoc != null)
                        {
                            var content = textDoc.StartPoint.CreateEditPoint()
                                .GetText(textDoc.EndPoint);
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                Logger.Info($"[AddContext] 从输出窗格 \"{pane.Name}\" 获取内容，长度={content.Length}");
                                return content;
                            }
                        }
                    }
                    catch
                    {
                        // 跳过无法访问的窗格
                    }
                }

                Logger.Info("[AddContext] 所有输出窗格均为空");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Error($"捕获调试输出失败: {ex.Message}", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// 根据语言名称获取对应的文件扩展名。
        /// </summary>
        private static string GetFileExtensionForLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return "txt";

            return language.ToLowerInvariant() switch
            {
                "csharp" or "c#" => "cs",
                "vb" or "vb.net" or "basic" => "vb",
                "c/c++" or "c++" or "cpp" => "cpp",
                "c" => "c",
                "f#" or "fsharp" => "fs",
                "python" => "py",
                "javascript" or "js" => "js",
                "typescript" or "ts" => "ts",
                "html" or "htmlx" => "html",
                "css" => "css",
                "xml" or "xaml" => "xml",
                "json" => "json",
                "sql" => "sql",
                "java" => "java",
                "go" => "go",
                "rust" => "rs",
                "powershell" or "ps1" => "ps1",
                "shell" or "bash" => "sh",
                "markdown" => "md",
                "yaml" or "yml" => "yml",
                _ => "txt"
            };
        }

        #endregion

        #region Event Handlers - Skill Suggestions

        /// <summary>
        /// 输入框文本变更：检测 / 触发 Skill 自动补全、检测 @ 触发 Agent 路由自动补全。
        /// </summary>
        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var text = InputTextBox.Text;

                // ── 占位提示文字显隐 ──
                UpdatePlaceholderVisibility();

                // ── @ 路由前缀检测（优先） ──
                if (!string.IsNullOrEmpty(text) && text.StartsWith("@"))
                {
                    // 关闭 Skill 弹出框（互斥）
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;

                    // @ 后包含空格 → 用户正在输入参数，关闭弹出框
                    if (text.Contains(' '))
                    {
                        if (AgentSuggestionPopup.IsOpen)
                            AgentSuggestionPopup.IsOpen = false;
                        return;
                    }

                    // 提取 @ 后的文本用于过滤
                    var agentFilterText = text.Length > 1 ? text.Substring(1).ToLowerInvariant() : string.Empty;
                    UpdateAgentSuggestions(agentFilterText);
                    return;
                }

                // 关闭 Agent 弹出框（非 @ 开头）
                if (AgentSuggestionPopup.IsOpen)
                    AgentSuggestionPopup.IsOpen = false;

                // ── / 斜杠命令检测 ──
                // 不以 / 开头 → 关闭弹出框
                if (string.IsNullOrEmpty(text) || !text.StartsWith("/"))
                {
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;
                    return;
                }

                // 命令已包含空格 → 用户正在输入参数，关闭弹出框
                if (text.Contains(' '))
                {
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;
                    return;
                }

                // 提取 / 后的文本用于过滤
                var filterText = text.Length > 1 ? text.Substring(1).ToLowerInvariant() : string.Empty;
                UpdateSkillSuggestions(filterText);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Skill] 文本变更处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新 Skill 建议列表。
        /// </summary>
        private void UpdateSkillSuggestions(string filterText)
        {
            try
            {
                if (_skillDiscoveryResult == null)
                {
                    // 后台异步加载技能缓存，不阻塞 UI 线程。
                    // InitializeSkills() 已在启动时触发加载，此处为兜底路径。
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[Skill] 后台技能发现失败: {ex.Message}");
                        }
                    });
                    // 首次无缓存时关闭弹出框，下次输入时将命中缓存
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;
                    return;
                }

                var allSkills = _skillDiscoveryResult?.Skills ?? new List<SkillDefinition>();

                // 添加内置元命令
                var L = LocalizationService.Instance;
                var metaCommands = new List<SkillSuggestionItem>
                {
                    new SkillSuggestionItem
                    {
                        Name = "help",
                        Description = L["popup.skillDesc.help"],
                        Source = L["popup.skillSource.builtin"],
                        IsMeta = true,
                    },
                    new SkillSuggestionItem
                    {
                        Name = "create-skill",
                        Description = L["popup.skillDesc.createSkill"],
                        Source = L["popup.skillSource.builtin"],
                        IsMeta = true,
                    },
                    new SkillSuggestionItem
                    {
                        Name = "refresh-skills",
                        Description = L["popup.skillDesc.refreshSkills"],
                        Source = L["popup.skillSource.builtin"],
                        IsMeta = true,
                    },
                };

                // 添加用户/项目技能
                var skillItems = allSkills
                    .Select(s => new SkillSuggestionItem
                    {
                        Name = s.Name,
                        Description = s.Description,
                        Source = s.Source switch
                        {
                            SkillSource.Project => L["popup.skillSource.project"],
                            SkillSource.User => L["popup.skillSource.user"],
                            SkillSource.BuiltIn => L["popup.skillSource.package"],
                            _ => "❓"
                        },
                        IsMeta = false,
                        SkillDefinition = s,
                    })
                    .ToList();

                var allItems = metaCommands.Concat(skillItems).ToList();

                // 按过滤文本筛选
                if (!string.IsNullOrEmpty(filterText))
                {
                    var parts = filterText.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    var commandPart = parts.Length > 0 ? parts[0] : filterText;

                    allItems = allItems
                        .Where(item => item.Name.StartsWith(commandPart, StringComparison.OrdinalIgnoreCase)
                                       || item.Description.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }

                // 更新 ListBox
                SkillSuggestionListBox.ItemsSource = allItems;

                if (allItems.Count > 0)
                {
                    SkillSuggestionListBox.SelectedIndex = 0;
                    SkillSuggestionPopup.IsOpen = true;

                    // 动态调整弹出框宽度
                    var maxNameLen = allItems.Max(item => item.Name.Length);
                    // 不显式设置宽度，让 WPF 自动布局
                }
                else
                {
                    SkillSuggestionPopup.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Skill] 更新建议列表失败: {ex.Message}");
                SkillSuggestionPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// 导航 Skill 建议列表（上下键）。
        /// </summary>
        private void NavigateSkillSuggestion(int direction)
        {
            if (!SkillSuggestionPopup.IsOpen || SkillSuggestionListBox.Items.Count == 0)
                return;

            var newIndex = SkillSuggestionListBox.SelectedIndex + direction;
            if (newIndex < 0)
                newIndex = SkillSuggestionListBox.Items.Count - 1;
            else if (newIndex >= SkillSuggestionListBox.Items.Count)
                newIndex = 0;

            SkillSuggestionListBox.SelectedIndex = newIndex;
            SkillSuggestionListBox.ScrollIntoView(SkillSuggestionListBox.SelectedItem);
        }

        /// <summary>
        /// 接受当前选中的 Skill 建议（Enter/Tab 键）。
        /// </summary>
        private void AcceptSkillSuggestion()
        {
            if (!SkillSuggestionPopup.IsOpen || SkillSuggestionListBox.SelectedItem == null)
                return;

            if (SkillSuggestionListBox.SelectedItem is SkillSuggestionItem item)
            {
                // 替换输入框文本为 /skill-name
                var skillName = item.Name;

                // ── 日志：记录用户从弹出框选择了技能 ──
                if (item.IsMeta)
                    Logger.Info($"[Skill] 用户从弹出框选择元命令: /{skillName}");
                else
                    Logger.Info($"[Skill] 用户从弹出框选择技能: /{skillName} | 描述: {item.Description} | 来源: {item.Source}");

                // 保留 / 后的其他参数（如果用户在 /skill-name 后输入了额外文本）
                var currentText = InputTextBox.Text;
                var spaceIndex = currentText.IndexOf(' ');
                var extraArgs = spaceIndex > 0 ? currentText.Substring(spaceIndex) : string.Empty;

                if (item.IsMeta)
                {
                    // 元命令直接执行
                    InputTextBox.Text = $"/{skillName}{extraArgs}";
                }
                else
                {
                    // 技能命令：格式为 /skill-name [description hint]
                    var hint = item.SkillDefinition?.ArgumentHint;
                    InputTextBox.Text = $"/{skillName}{extraArgs}";
                }

                // 将光标移到末尾
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                InputTextBox.Focus();
            }

            SkillSuggestionPopup.IsOpen = false;
        }

        /// <summary>
        /// ListBox 选中项变更。
        /// </summary>
        private void SkillSuggestionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 预留：选中项变更时的处理
        }

        /// <summary>
        /// ListBox 键盘事件：Enter/Tab 接受选择，Escape 关闭弹出框。
        /// 解决鼠标点击选中后按 Enter 无反应的问题。
        /// </summary>
        private void SkillSuggestionListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                AcceptSkillSuggestion();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SkillSuggestionPopup.IsOpen = false;
                InputTextBox.Focus();
                return;
            }
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // 让 ListBox 默认处理导航
                e.Handled = false;
            }
        }

        /// <summary>
        /// ListBox 双击选中技能。
        /// </summary>
        private void SkillSuggestionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            AcceptSkillSuggestion();
        }

        #endregion

        #region Event Handlers - Agent Suggestions

        /// <summary>
        /// 更新 Agent 路由建议列表。
        /// 根据 @ 后的输入文本过滤可用的 Agent。
        /// </summary>
        private void UpdateAgentSuggestions(string filterText)
        {
            try
            {
                var L = LocalizationService.Instance;
                var agents = new List<AgentSuggestionItem>
                {
                    new AgentSuggestionItem
                    {
                        Name = "ask",
                        Icon = "💬",
                        Description = L["popup.agentDesc.ask"],
                        ArgumentHint = L["popup.agentHint.ask"],
                        AgentType = AgentType.Ask,
                    },
                    new AgentSuggestionItem
                    {
                        Name = "explore",
                        Icon = "🔍",
                        Description = L["popup.agentDesc.explore"],
                        ArgumentHint = L["popup.agentHint.explore"],
                        AgentType = AgentType.Explore,
                    },
                    new AgentSuggestionItem
                    {
                        Name = "plan",
                        Icon = "📋",
                        Description = L["popup.agentDesc.plan"],
                        ArgumentHint = L["popup.agentHint.plan"],
                        AgentType = AgentType.Plan,
                    },
                    new AgentSuggestionItem
                    {
                        Name = "edit",
                        Icon = "🔨",
                        Description = L["popup.agentDesc.edit"],
                        ArgumentHint = L["popup.agentHint.edit"],
                        AgentType = AgentType.Edit,
                    },
                    new AgentSuggestionItem
                    {
                        Name = "build",
                        Icon = "🔧",
                        Description = L["popup.agentDesc.build"],
                        ArgumentHint = L["popup.agentHint.build"],
                        AgentType = AgentType.Build,
                    },
                };

                // 按过滤文本筛选
                if (!string.IsNullOrEmpty(filterText))
                {
                    agents = agents
                        .Where(a => a.Name.StartsWith(filterText, StringComparison.OrdinalIgnoreCase)
                                    || a.Description.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }

                // 更新 ListBox
                AgentSuggestionListBox.ItemsSource = agents;

                if (agents.Count > 0)
                {
                    AgentSuggestionListBox.SelectedIndex = 0;
                    AgentSuggestionPopup.IsOpen = true;
                }
                else
                {
                    AgentSuggestionPopup.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Agent] 更新建议列表失败: {ex.Message}");
                AgentSuggestionPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// 导航 Agent 建议列表（上下键）。
        /// </summary>
        private void NavigateAgentSuggestion(int direction)
        {
            if (!AgentSuggestionPopup.IsOpen || AgentSuggestionListBox.Items.Count == 0)
                return;

            var newIndex = AgentSuggestionListBox.SelectedIndex + direction;
            if (newIndex < 0)
                newIndex = AgentSuggestionListBox.Items.Count - 1;
            else if (newIndex >= AgentSuggestionListBox.Items.Count)
                newIndex = 0;

            AgentSuggestionListBox.SelectedIndex = newIndex;
            AgentSuggestionListBox.ScrollIntoView(AgentSuggestionListBox.SelectedItem);
        }

        /// <summary>
        /// 接受当前选中的 Agent 建议（Enter/Tab 键）。
        /// 将输入框文本替换为 @agent-name 格式。
        /// </summary>
        private void AcceptAgentSuggestion()
        {
            if (!AgentSuggestionPopup.IsOpen || AgentSuggestionListBox.SelectedItem == null)
                return;

            if (AgentSuggestionListBox.SelectedItem is AgentSuggestionItem item)
            {
                var agentName = item.Name;

                Logger.Info($"[Agent] 用户从弹出框选择 Agent: @{agentName} ({item.Description})");

                // 设置输入框为 @agent-name 后加空格，方便用户继续输入
                InputTextBox.Text = $"@{agentName} ";
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                InputTextBox.Focus();
            }

            AgentSuggestionPopup.IsOpen = false;
        }

        /// <summary>
        /// Agent ListBox 键盘事件：Enter/Tab 接受选择，Escape 关闭弹出框。
        /// </summary>
        private void AgentSuggestionListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                AcceptAgentSuggestion();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                AgentSuggestionPopup.IsOpen = false;
                InputTextBox.Focus();
                return;
            }
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // 让 ListBox 默认处理导航
                e.Handled = false;
            }
        }

        /// <summary>
        /// Agent ListBox 双击选中 Agent。
        /// </summary>
        private void AgentSuggestionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            AcceptAgentSuggestion();
        }

        /// <summary>
        /// Agent ListBox 选中项变更。
        /// </summary>
        private void AgentSuggestionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 预留：选中项变更时的处理
        }

        #endregion

        #region Event Handlers - LostFocus & Placeholder

        /// <summary>
        /// 输入框获得焦点时隐藏占位提示文字。
        /// </summary>
        private void InputTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdatePlaceholderVisibility();
        }

        /// <summary>
        /// 输入框失去焦点时关闭弹出框（延迟关闭，允许点击 ListBox 项）。
        /// </summary>
        private async void InputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 延迟关闭，确保 ListBox 的 MouseDoubleClick 能先触发
            await System.Threading.Tasks.Task.Delay(200);
            if (SkillSuggestionPopup.IsOpen && !SkillSuggestionListBox.IsKeyboardFocusWithin)
            {
                SkillSuggestionPopup.IsOpen = false;
            }
            if (AgentSuggestionPopup.IsOpen && !AgentSuggestionListBox.IsKeyboardFocusWithin)
            {
                AgentSuggestionPopup.IsOpen = false;
            }
            UpdatePlaceholderVisibility();
        }

        /// <summary>
        /// 更新输入框占位提示文字的可见性。
        /// 规则：输入框为空 且 无焦点 → 显示；否则隐藏。
        /// </summary>
        private void UpdatePlaceholderVisibility()
        {
            if (InputPlaceholder == null) return;
            bool isEmpty = string.IsNullOrEmpty(InputTextBox.Text);
            bool isFocused = InputTextBox.IsFocused;
            InputPlaceholder.Visibility = (isEmpty && !isFocused) ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Event Handlers - Session & Model

        private void SessionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionComboBox.SelectedItem is ChatSession session && session != _activeSession)
            {
                SwitchToSession(session);
            }
        }

        private void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
        {
            // ── 删除前确认 ──
            string confirmMsg = LocalizationService.Instance["chat.confirmDeleteConversation"];
            if (string.IsNullOrEmpty(confirmMsg))
                confirmMsg = LocalizationService.Instance["status.deleteConfirm"];

            var result = System.Windows.MessageBox.Show(
                confirmMsg,
                LocalizationService.Instance["chat.deleteConversation"] ?? "删除对话",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                DeleteCurrentSession();
            }
        }

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            CreateNewChat();
        }

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_apiService != null && ModelComboBox.SelectedItem is string model)
            {
                _apiService.UpdateModel(model);
                Logger.Info($"模型切换为: {model}");
            }
        }

        private void ApprovalModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ApprovalModeComboBox.SelectedValue is Models.ApprovalMode mode)
            {
                // 持久化到设置
                if (_options != null)
                {
                    _options.ApprovalMode = mode switch
                    {
                        Models.ApprovalMode.BlockAll => "BlockAll",
                        Models.ApprovalMode.AllowAll => "AllowAll",
                        _ => "SmartBlock",
                    };
                    // 立即写入存储，确保重启 VS 后仍然生效
                    try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                }
                Logger.Info($"审批模式切换为: {mode}");
            }
        }

        private void ThinkingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_apiService != null)
            {
                bool enabled = ThinkingCheckBox.IsChecked == true;
                string effort = EffortComboBox.SelectedItem as string ?? "high";
                _apiService.ConfigureThinking(enabled, effort);
                Logger.Info($"思考模式: {(enabled ? "启用" : "禁用")}, 强度: {effort}");
            }
        }

        // TODO: RagCheckBox 暂时注释掉，功能未完善
        /*
        private void RagCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = RagCheckBox.IsChecked == true;
            if (_ragService != null)
                _ragService.IsEnabled = enabled;
            Logger.Info($"代码索引: {(enabled ? "启用" : "禁用")}");
        }
        */

        private void EffortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_apiService != null && EffortComboBox.SelectedItem is string effort)
            {
                bool enabled = ThinkingCheckBox.IsChecked == true;
                _apiService.ConfigureThinking(enabled, effort);
                // 持久化到设置
                if (_options != null)
                {
                    _options.ReasoningEffort = effort;
                    // 立即写入存储，确保重启 VS 后仍然生效
                    try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                }
                Logger.Info($"推理强度切换为: {effort}");
            }
        }

        #endregion

        #region Event Handlers - Web Search

        /// <summary>
        /// 联网搜索开关按钮点击：切换开启/关闭，联动下拉框可见性。
        /// </summary>
        private void WebSearchToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 切换状态
                if (_webSearchEngine == "Off")
                {
                    // 使用 ComboBox 当前选择的搜索引擎，而非硬编码 DuckDuckGo
                    string? selected = WebSearchEngineComboBox.SelectedItem as string;
                    string newEngine = selected switch
                    {
                        string s when s.Contains("百度") || s.Contains("Baidu") => "Baidu",
                        _ => "DuckDuckGo"
                    };
                    _webSearchEngine = newEngine;
                    // 同步 ComboBox 选中项到当前引擎（确保 UI 一致）
                    int idx = newEngine switch
                    {
                        "Baidu" => 0,
                        "DuckDuckGo" => 1,
                        _ => 1
                    };
                    WebSearchEngineComboBox.SelectedIndex = idx;
                }
                else
                {
                    _webSearchEngine = "Off";
                }

                Logger.Info($"联网搜索状态切换为: {_webSearchEngine}");
                UpdateWebSearchToggleAppearance();

                if (_webSearchEngine != "Off")
                {
                    ApplyWebSearchConfig();
                }

                // 提示百度未配置 Key 的情况
                if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.search.baiduKeyRequired"];
                }
                else
                {
                    StatusLabel.Text = string.Empty;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSearchToggleButton_Click 异常: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// MCP 配置按钮点击：打开 MCP 服务器配置对话框。
        /// </summary>
        private void McpConfigButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 从文件加载当前配置
                var currentConfigs = McpConfigStore.Load();

                var dialog = new McpConfigDialog(currentConfigs, savedServers =>
                {
                    // 保存到文件
                    McpConfigStore.Save(savedServers);
                    Logger.Info($"[MCP Config] 已保存 {savedServers.Count} 个 MCP 服务器配置");
                });

                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();

                // 重新初始化 MCP 连接
                InitializeMcp();

                // 刷新 MCP 按钮状态
                UpdateMcpButtonAppearance();
            }
            catch (Exception ex)
            {
                Logger.Error($"McpConfigButton_Click 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 更新 MCP 配置按钮的外观，反映连接状态。
        /// </summary>
        private void UpdateMcpButtonAppearance()
        {
            try
            {
                var L = LocalizationService.Instance;
                if (_mcpManager == null || _mcpManager.AllTools.Count == 0)
                {
                    McpConfigButton.ToolTip = L["input.mcpNotConnected"];
                    McpConfigButton.Foreground = new SolidColorBrush(
                        Color.FromRgb(0x88, 0x88, 0x88));
                }
                else
                {
                    int toolCount = _mcpManager.AllTools.Count;
                    McpConfigButton.ToolTip = L.Format("input.mcpConnected", toolCount);
                    McpConfigButton.Foreground = new SolidColorBrush(
                        Color.FromRgb(0x4E, 0xC9, 0xB0));
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[MCP] 更新按钮外观失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据当前搜索引擎更新切换按钮的外观和 ToolTip。
        /// </summary>
        private void UpdateWebSearchToggleAppearance()
        {
            bool isOn = _webSearchEngine != "Off";
            var L = LocalizationService.Instance;
            if (isOn)
            {
                WebSearchToggleButton.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x6C, 0xAF, 0xD9));
                WebSearchToggleButton.ToolTip = L["input.webSearchOn"];
            }
            else
            {
                WebSearchToggleButton.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x88, 0x88, 0x88));
                WebSearchToggleButton.ToolTip = L["input.webSearchOff"];
            }

            // 下拉框可见性：开启时显示，关闭时隐藏
            WebSearchEngineComboBox.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        }


        /// <summary>
        /// 联网搜索引擎选择变更事件。仅在搜索已开启时生效，搜索关闭时仅记录偏好。
        /// </summary>
        private void WebSearchEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (WebSearchEngineComboBox.SelectedIndex < 0) return;

                // 搜索关闭时，ComboBox 仅作为偏好存储，不切换引擎
                if (_webSearchEngine == "Off") return;

                string? selected = WebSearchEngineComboBox.SelectedItem as string;
                string newEngine = selected switch
                {
                    string s when s.Contains("百度") || s.Contains("Baidu") => "Baidu",
                    _ => "DuckDuckGo"
                };

                if (_webSearchEngine == newEngine) return; // 避免循环触发

                _webSearchEngine = newEngine;
                Logger.Info($"联网搜索引擎切换为: {_webSearchEngine}");
                UpdateWebSearchToggleAppearance();
                ApplyWebSearchConfig();

                if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.search.baiduKeyRequired"];
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSearchEngineComboBox_SelectionChanged 异常: {ex.Message}", ex);
            }
        }

        #endregion

        #region Event Handlers - WebView2

        /// <summary>
        /// WebView2 新窗口请求事件：拦截 target='_blank' 链接，在系统默认浏览器中打开。
        /// 使搜索结果卡片中的 URL 可以点击跳转。
        /// </summary>
        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true; // 阻止 WebView2 内部打开
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri,
                    UseShellExecute = true,
                });
                Logger.Info($"在外部浏览器打开: {e.Uri}");
            }
            catch (Exception ex)
            {
                Logger.Error($"打开外部浏览器失败 ({e.Uri}): {ex.Message}", ex);
            }
        }

        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(message)) return;

                // ── 页面就绪信号：WebView2 DOM + JS 全部加载完成 ──
                if (message == "__pageReady__")
                {
                    _pageReady = true;
                    Logger.Info("[Render] WebView2 页面就绪信号收到");
                    return;
                }

                var obj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message);
                if (obj.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? string.Empty;

                    if (type == "applyCode")
                    {
                        string code = obj.TryGetProperty("code", out var codeProp)
                            ? codeProp.GetString() ?? string.Empty : string.Empty;
                        ApplyCodeToActiveDocument(code);
                    }
                    else if (type == "retryMessage")
                    {
                        int msgIndex = obj.TryGetProperty("messageIndex", out var retryIdxProp)
                            ? retryIdxProp.GetInt32() : -1;
                        if (msgIndex >= 0)
                            _ = RetryMessageAsync(msgIndex);
                    }
                    else if (type == "editMessage")
                    {
                        int msgIndex = obj.TryGetProperty("messageIndex", out var editIdxProp)
                            ? editIdxProp.GetInt32() : -1;
                        if (msgIndex >= 0)
                            _ = EditMessageAsync(msgIndex);
                    }
                    else if (type == "editMessageConfirm")
                    {
                        int msgIndex = obj.TryGetProperty("messageIndex", out var editConfirmIdxProp)
                            ? editConfirmIdxProp.GetInt32() : -1;
                        string newText = obj.TryGetProperty("text", out var editTextProp)
                            ? editTextProp.GetString() ?? string.Empty : string.Empty;
                        if (msgIndex >= 0 && !string.IsNullOrEmpty(newText))
                            _ = HandleEditConfirmAsync(msgIndex, newText);
                    }
                    else if (type == "editMessageCancel")
                    {
                        int msgIndex = obj.TryGetProperty("messageIndex", out var editCancelIdxProp)
                            ? editCancelIdxProp.GetInt32() : -1;
                        if (msgIndex >= 0)
                            _ = HandleEditCancelAsync(msgIndex);
                    }
                    else if (type == "navigateBranch")
                    {
                        string nodeId = obj.TryGetProperty("nodeId", out var nodeIdProp)
                            ? nodeIdProp.GetString() ?? string.Empty : string.Empty;
                        int direction = obj.TryGetProperty("direction", out var dirProp)
                            ? dirProp.GetInt32() : 0;
                        if (!string.IsNullOrEmpty(nodeId) && direction != 0)
                            _ = NavigateBranchAsync(nodeId, direction);
                    }
                    else if (type == "navigateVersion")
                    {
                        // ── 旧版兼容：转换为 navigateBranch ──
                        int msgIndex = obj.TryGetProperty("messageIndex", out var navIdxProp)
                            ? navIdxProp.GetInt32() : -1;
                        int direction = obj.TryGetProperty("direction", out var dirPropOld)
                            ? dirPropOld.GetInt32() : 0;
                        if (msgIndex >= 0 && direction != 0)
                        {
                            // 旧版按消息索引导航 → 转为按 nodeId 导航
                            var node = GetConvNodeByMessageIndex(msgIndex);
                            if (node != null)
                                _ = NavigateBranchAsync(node.Id, direction);
                        }
                    }
                    else if (type == "agentApprove")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var reqIdProp)
                            ? reqIdProp.GetString() ?? string.Empty : string.Empty;
                        bool approved = obj.TryGetProperty("approved", out var apprProp)
                            && apprProp.GetBoolean();
                        var permAgent = _agentFactory?.FindAgentWithPendingPermission(requestId) ?? _activeAgent;
                        permAgent?.RespondToPermission(requestId, approved);

                        // 移除权限 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        "var p=document.getElementById('agent-permission');if(p)p.remove();");
                                }
                                catch { }
                            }
                        });
                    }
                    // ── 终端命令审批 ──
                    else if (type == "terminalApprove")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var termReqIdProp)
                            ? termReqIdProp.GetString() ?? string.Empty : string.Empty;
                        var termPermAgent = _agentFactory?.FindAgentWithPendingPermission(requestId) ?? _activeAgent;
                        termPermAgent?.RespondToPermission(requestId, true);

                        // 移除审批 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        "var p=document.getElementById('terminal-approval');if(p)p.remove();");
                                    StatusLabel.Text = LocalizationService.Instance["status.terminalApproved"];
                                }
                                catch { }
                            }
                        });
                    }
                    else if (type == "terminalSkip")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var termSkipReqIdProp)
                            ? termSkipReqIdProp.GetString() ?? string.Empty : string.Empty;
                        var skipPermAgent = _agentFactory?.FindAgentWithPendingPermission(requestId) ?? _activeAgent;
                        skipPermAgent?.RespondToPermission(requestId, false);

                        // 移除审批 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        "var p=document.getElementById('terminal-approval');if(p)p.remove();");
                                    StatusLabel.Text = LocalizationService.Instance["status.terminalSkipped"];
                                }
                                catch { }
                            }
                        });
                    }
                    // ── VisualStudio_askQuestions 回答 ──
                    else if (type == "answerQuestions")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var qReqIdProp)
                            ? qReqIdProp.GetString() ?? string.Empty : string.Empty;
                        string answers = obj.TryGetProperty("answers", out var ansProp)
                            ? ansProp.GetString() ?? "{}" : "{}";
                        var questionAgent = _agentFactory?.FindAgentWithPendingQuestion(requestId) ?? _activeAgent;
                        questionAgent?.RespondToQuestions(requestId, answers);

                        // 移除问题 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        "var p=document.getElementById('agent-questions');if(p)p.remove();");
                                    StatusLabel.Text = LocalizationService.Instance["status.ready"];
                                }
                                catch { }
                            }
                        });
                    }
                    // ── 诊断消息（来自 JS try-catch / DOM 检查）──
                    else if (type == "diagnostic")
                    {
                        string msg = obj.TryGetProperty("msg", out var diagProp)
                            ? diagProp.GetString() ?? string.Empty : string.Empty;
                        Logger.Info($"[WebMessage] JS diagnostic: {msg}");
                    }
                    else if (type == "fileDeleteConfirm")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var delReqIdProp)
                            ? delReqIdProp.GetString() ?? string.Empty : string.Empty;
                        bool confirmed = obj.TryGetProperty("confirmed", out var confProp)
                            && confProp.GetBoolean();

                        // 按 RequestId 精确查找待处理的权限请求（支持并发权限场景）
                        var filePermAgent = _agentFactory?.FindAgentWithPendingPermission(requestId);
                        var pendingPermission = filePermAgent?.TryGetPendingPermission(requestId);
                        List<string> filePaths = pendingPermission?.FilePaths ?? new List<string>();

                        if (confirmed && filePaths.Count > 0)
                        {
                            await AgentFactory.DeleteFilesViaEnvDTEAsync(filePaths);
                        }

                        // 完成权限响应（解除 Agent 等待）
                        (filePermAgent ?? _activeAgent)?.RespondToPermission(requestId, confirmed);

                        // 移除文件删除确认 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        "var p=document.getElementById('file-delete-confirm');if(p)p.remove();");
                                    StatusLabel.Text = confirmed
                                        ? $"✅ 已删除 {filePaths.Count} 个文件"
                                        : "❌ 已取消删除";
                                }
                                catch { }
                            }
                        });
                    }
                    else if (type == "executeHandoff")
                    {
                        string targetAgent = obj.TryGetProperty("targetAgent", out var targetProp)
                            ? targetProp.GetString() ?? "Edit" : "Edit";
                        string label = obj.TryGetProperty("label", out var labelProp)
                            ? labelProp.GetString() ?? LocalizationService.Instance["plan.handoff.label"] : LocalizationService.Instance["plan.handoff.label"];
                        _ = ExecuteAgentHandoffAsync(targetAgent, label);
                    }
                    // ── 关闭任务面板：清除持久化的 PlanJson，防止重启后重新显示 ──
                    else if (type == "dismissTaskPanel")
                    {
                        string planId = obj.TryGetProperty("planId", out var planIdProp)
                            ? planIdProp.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrEmpty(planId))
                        {
                            DismissTaskPanel(planId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebMessage 处理异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Applies the AI-generated code to the active document.
        /// Uses TerminalWindowHelper for proper TextBuffer manipulation.
        /// </summary>
        private void ApplyCodeToActiveDocument(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                string? error = await TerminalWindowHelper.ApplyCodeToActiveDocumentAsync(code);
                if (error != null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"⚠️ {error}";
                    Logger.Warn($"[ApplyCode] 写入失败: {error}");
                }
            });
        }

        #endregion

        #region Event Handlers - Diff Global Bar

        /// <summary>
        /// 「接受全部」按钮点击：确认所有活跃会话中的变更，丢弃所有待处理 diff。
        /// </summary>
        private void AcceptAllDiffButton_Click(object sender, RoutedEventArgs e)
        {
            EditorDiffMarkerService.Instance.AcceptAllChanges();
            RefreshDiffGlobalBar();
            StatusLabel.Text = LocalizationService.Instance["diff.global.allAccepted"];
        }

        /// <summary>
        /// 「撤销全部」按钮点击：撤销所有活跃会话并丢弃所有待处理 diff。
        /// </summary>
        private void UndoAllDiffButton_Click(object sender, RoutedEventArgs e)
        {
            EditorDiffMarkerService.Instance.UndoAllChanges();
            RefreshDiffGlobalBar();
            StatusLabel.Text = LocalizationService.Instance["diff.global.allUndone"];
        }

        /// <summary>
        /// 刷新全局 diff 控制栏的可见性和内容。
        /// 当有活跃会话或待处理 diff 时显示，否则隐藏。
        /// </summary>
        public void RefreshDiffGlobalBar()
        {
            // ── WPF 控件访问必须在 UI 线程 ──
            if (!Dispatcher.CheckAccess())
            {
#pragma warning disable VSTHRD001 // 此处使用 BeginInvoke 是安全的 fire-and-forget 模式
                _ = Dispatcher.BeginInvoke(new Action(RefreshDiffGlobalBar));
#pragma warning restore VSTHRD001
                return;
            }

            var L = LocalizationService.Instance;
            int activeCount = EditorDiffMarkerService.Instance.GetActiveCount();
            int pendingCount = EditorDiffMarkerService.Instance.GetPendingCount();
            int totalCount = activeCount + pendingCount;

            if (totalCount > 0)
            {
                DiffGlobalBar.Visibility = Visibility.Visible;
                DiffGlobalLabel.Text = string.Format(L["diff.global.filesChanged"], totalCount);
                DiffGlobalDetail.Text = activeCount > 0 && pendingCount > 0
                    ? string.Format(L["diff.global.bothStatus"], activeCount, pendingCount)
                    : activeCount > 0
                        ? string.Format(L["diff.global.activeOnly"], activeCount)
                        : string.Format(L["diff.global.pendingOnly"], pendingCount);
            }
            else
            {
                DiffGlobalBar.Visibility = Visibility.Collapsed;
            }
        }

        #endregion
    }
}
