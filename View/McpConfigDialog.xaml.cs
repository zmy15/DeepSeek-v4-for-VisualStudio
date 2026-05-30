using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// MCP 服务器配置对话框。
    /// 支持从 Claude Desktop / VS Code / 自定义 JSON 格式导入，
    /// 以及对已配置服务器的增删改查和连接测试。
    /// </summary>
    public partial class McpConfigDialog : Window
    {
        private ObservableCollection<McpServerConfig> _servers;
        private readonly Action<List<McpServerConfig>> _onSaved;

        /// <summary>
        /// 构造函数。
        /// </summary>
        /// <param name="currentServers">当前已配置的服务器列表</param>
        /// <param name="onSaved">保存后的回调，用于触发 MCP 重连</param>
        public McpConfigDialog(List<McpServerConfig> currentServers, Action<List<McpServerConfig>> onSaved)
        {
            InitializeComponent();

            // 应用本地化字符串
            ApplyLocalization();

            // 订阅语言变更事件以动态刷新
            LocalizationService.Instance.LanguageChanged += (_, _) => ApplyLocalization();

            _servers = new ObservableCollection<McpServerConfig>(
                currentServers?.Select(s => CloneForDisplay(s)) ?? new List<McpServerConfig>());

            ServersItemsControl.ItemsSource = _servers;
            _onSaved = onSaved;

            // 加载现有配置到 JSON 文本框（Claude Desktop 格式预览）
            if (_servers.Count > 0)
            {
                try
                {
                    JsonPasteTextBox.Text = McpConfigParser.SerializeToClaudeFormat(
                        _servers.Select(NormalizeForSave).ToList());
                }
                catch { }
            }
        }

        /// <summary>
        /// 应用当前语言的本地化字符串到对话框所有 UI 元素。
        /// </summary>
        private void ApplyLocalization()
        {
            var L = LocalizationService.Instance;

            // 窗口标题
            Title = L["mcp.title"];

            // 标题标签
            TitleLabel.Text = "⚙️ " + L["mcp.title"];

            // JSON 粘贴区域
            PasteJsonLabel.Text = "📋 " + L["mcp.pasteJson"];

            // 按钮
            ParseJsonButton.Content = L["mcp.detectAndAdd"];
            ClearJsonButton.Content = L["general.delete"];
            AddServerButton.Content = "+ " + L["mcp.addServer"];
            SaveButton.Content = L["mcp.save"];
            CancelButton.Content = L["general.cancel"];

            // 服务器列表
            ServersLabel.Text = "📡 " + L["mcp.configuredServers"];

            // 字段标签 (在 DataTemplate 中，无法通过 x:Name 访问，使用固定英文标签)
            // "Command:", "Args:", "Environment:" 在 XAML 中已设为英文默认值
        }

        /// <summary>
        /// 克隆配置并确保环境变量在 UI 的 Environment 文本框中可见。
        /// Env 字典 → Environment 扁平字符串转换。
        /// </summary>
        private static McpServerConfig CloneForDisplay(McpServerConfig src)
        {
            var clone = new McpServerConfig
            {
                Name = src.Name,
                Command = src.Command,
                Args = src.Args,
                Transport = src.Transport,
                Url = src.Url,
                Enabled = src.Enabled,
                Env = src.Env != null ? new Dictionary<string, string>(src.Env) : null,
            };

            // 将 Env 字典展开为 Environment 字符串（用于 UI 文本框显示）
            var envParts = new List<string>();

            // 先添加原有扁平字符串部分
            if (!string.IsNullOrWhiteSpace(src.Environment))
                envParts.Add(src.Environment);

            // 再追加 Env 字典内容（仅添加不在扁平字符串中的键）
            if (src.Env != null)
            {
                foreach (var kvp in src.Env)
                {
                    // 避免重复
                    if (!string.IsNullOrWhiteSpace(src.Environment) &&
                        src.Environment.IndexOf(kvp.Key + "=", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    envParts.Add($"{kvp.Key}={kvp.Value}");
                }
            }

            clone.Environment = string.Join(";", envParts);
            return clone;
        }

        /// <summary>
        /// 保存前规范化：将 Environment 字符串同步到 Env 字典。
        /// </summary>
        private static McpServerConfig NormalizeForSave(McpServerConfig src)
        {
            // 将 Environment 字符串解析合并到 Env 字典
            var envDict = src.Env != null ? new Dictionary<string, string>(src.Env) : new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(src.Environment))
            {
                foreach (var pair in src.Environment.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var eqIndex = pair.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var key = pair.Substring(0, eqIndex).Trim();
                        var value = pair.Substring(eqIndex + 1).Trim();
                        if (!string.IsNullOrEmpty(key))
                            envDict[key] = value;
                    }
                }
            }

            return new McpServerConfig
            {
                Name = src.Name,
                Command = src.Command,
                Args = src.Args,
                Transport = src.Transport,
                Url = src.Url,
                Enabled = src.Enabled,
                Env = envDict.Count > 0 ? envDict : null,
                Environment = string.Empty, // 清空扁平格式，统一用 Env 字典
            };
        }

        #region Event Handlers

        private void ParseJsonButton_Click(object sender, RoutedEventArgs e)
        {
            var json = JsonPasteTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                ParseStatusLabel.Text = LocalizationService.Instance["mcp.status.pasteJsonHint"];
                ParseStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78));
                return;
            }

            try
            {
                var parsed = McpConfigParser.Parse(json);
                if (parsed.Count == 0)
                {
                    ParseStatusLabel.Text = "⚠️ " + LocalizationService.Instance["mcp.status.noServersDetected"];
                    ParseStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78));
                    return;
                }

                // 合并：更新同名服务器，添加新服务器
                int added = 0, updated = 0;
                foreach (var server in parsed)
                {
                    var existing = _servers.FirstOrDefault(s =>
                        s.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.Command = server.Command;
                        existing.Args = server.Args;
                        existing.Transport = server.Transport;
                        existing.Url = server.Url;
                        // 合并 env：将新解析的 Env 也显示到 Environment 文本框
                        existing.Env = server.Env;
                        existing.Environment = CloneForDisplay(server).Environment;
                        updated++;
                    }
                    else
                    {
                        // 确保 env 字典在 UI 的 Environment 文本框中可见
                        _servers.Add(CloneForDisplay(server));
                        added++;
                    }
                }

                ParseStatusLabel.Text = string.Format(LocalizationService.Instance["mcp.status.detectSuccess"], $"新增 {added} 个, 更新 {updated} 个");
                ParseStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0));

                // 更新 JSON 预览
                JsonPasteTextBox.Text = McpConfigParser.SerializeToClaudeFormat(_servers.ToList());
            }
            catch (Exception ex)
            {
                ParseStatusLabel.Text = string.Format("❌ {0}: {1}", LocalizationService.Instance["mcp.status.parseFailed"], ex.Message);
                ParseStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));
            }
        }

        private void ClearJsonButton_Click(object sender, RoutedEventArgs e)
        {
            JsonPasteTextBox.Text = string.Empty;
            ParseStatusLabel.Text = string.Empty;
        }

        private void AddServerButton_Click(object sender, RoutedEventArgs e)
        {
            var newServer = new McpServerConfig
            {
                Name = LocalizationService.Instance["mcp.newServerDefaultName"],
                Command = "npx",
                Args = "-y @anthropic/mcp-filesystem C:\\",
                Enabled = true,
                Transport = "stdio",
            };

            _servers.Add(newServer);

            // 滚动到新增项
            ServersItemsControl.UpdateLayout();
        }

        #pragma warning disable VSTHRD100 // async void 用于 WPF 按钮事件
        private async void TestServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not McpServerConfig config) return;

            // 显示进度条和状态
            btn.IsEnabled = false;
            btn.Content = "⏳";
            TestStatusBorder.Visibility = Visibility.Visible;
            TestProgressBar.IsIndeterminate = true;
            TestStatusText.Text = $"正在连接 {config.Name}...";

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

                // 根据传输类型选择客户端
                bool isHttp = config.Transport?.ToLowerInvariant() == "http";
                using var client = isHttp
                    ? (IMcpClient)new McpHttpClient(config)
                    : new McpStdioClient(config);

                // 进度回调（ConnectAsync 在后台线程执行，用 Dispatcher 桥接 UI 更新）
                Action<string> progress = msg =>
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TestStatusText.Text = $"⏳ {msg}";
                    }));
                };

                await client.ConnectAsync(cts.Token, progress);

                if (client.IsConnected && client.Tools.Count > 0)
                {
                    var toolNames = string.Join(", ", client.Tools.Select(t => t.Name));
                    TestProgressBar.IsIndeterminate = false;
                    TestProgressBar.Value = 100;
                    TestStatusText.Text = $"✅ {config.Name} 连接成功！\n" +
                                          $"   服务器: {client.ServerInfo?.ServerInfo?.Name ?? config.Name}\n" +
                                          $"   版本: {client.ServerInfo?.ServerInfo?.Version ?? "未知"}\n" +
                                          $"   工具 ({client.Tools.Count}): {toolNames}";
                    TestStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0));
                    btn.Content = "✅";
                    btn.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0));
                    Logger.Info($"[MCP Config] '{config.Name}' 测试成功: {client.Tools.Count} 工具");
                }
                else
                {
                    TestProgressBar.IsIndeterminate = false;
                    TestProgressBar.Value = 100;
                    TestStatusText.Text = $"⚠️ {config.Name} 连接成功但未提供任何工具。\n" +
                                          $"   请检查服务器配置是否正确。";
                    TestStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78));
                    btn.Content = "⚠️";
                    btn.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78));
                }
            }
            catch (OperationCanceledException)
            {
                TestProgressBar.IsIndeterminate = false;
                TestStatusText.Text = $"⏱️ {config.Name} 连接超时（20秒）。\n" +
                                      $"   请检查网络连接或服务器地址是否正确。";
                TestStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78));
                btn.Content = "⏱️";
                btn.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78));
            }
            catch (McpException ex)
            {
                TestProgressBar.IsIndeterminate = false;
                TestStatusText.Text = $"❌ {config.Name} MCP 协议错误:\n{ex.Message}";
                TestStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));
                btn.Content = "❌";
                btn.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));
                Logger.Error($"[MCP Config] '{config.Name}' MCP 错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                bool isHttp = config.Transport?.ToLowerInvariant() == "http";
                string errorHint = isHttp
                    ? $"• 服务器地址是否正确（当前: {config.Url}）\n• 网络连接是否正常\n• 服务器是否在线"
                    : $"• 命令是否正确（当前: {config.Command}）\n• 是否已安装所需运行时\n• 网络/Token 是否有效";

                TestProgressBar.IsIndeterminate = false;
                TestStatusText.Text = $"❌ {config.Name} 连接失败:\n{ex.Message}\n\n" +
                                      $"请检查:\n{errorHint}";
                TestStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));
                btn.Content = "❌";
                btn.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));
                Logger.Error($"[MCP Config] '{config.Name}' 测试失败: {ex.Message}", ex);
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private void DeleteServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is McpServerConfig config)
            {
                var result = MessageBox.Show(
                    string.Format(LocalizationService.Instance["mcp.confirmDeleteMessage"], config.Name),
                    LocalizationService.Instance["mcp.confirmDeleteTitle"],
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _servers.Remove(config);
                }
            }
        }

        private void SaveAndReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            // 规范化：将 UI 的 Environment 字符串合并到 Env 字典
            var servers = _servers.Select(NormalizeForSave).ToList();

            // 触发保存回调
            _onSaved?.Invoke(servers);

            Logger.Info($"[MCP Config] 保存 {servers.Count} 个 MCP 服务器配置");
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion
    }
}
