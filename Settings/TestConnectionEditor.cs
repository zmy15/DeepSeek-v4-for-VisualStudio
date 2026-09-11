using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// “测试连接”的属性网格编辑器。复用 Resolver 的端点/密钥选择规则，
    /// 因此官方端点测官方 Key，自定义端点测 CustomApiKey。
    /// </summary>
    public class TestConnectionEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)
            => UITypeEditorEditStyle.Modal;

        public override object? EditValue(
            ITypeDescriptorContext? context,
            IServiceProvider provider,
            object? value)
        {
            if (context?.Instance is not DeepSeekOptionsPage page)
                return value;

            using var dialog = new TestConnectionDialog(DeepSeekEndpointResolver.Resolve(page));
            dialog.ShowDialog();
            return value;
        }
    }

    /// <summary>校验当前生效端点的轻量对话框，按钮点击时调用 ValidateApiKeyAsync。</summary>
    internal sealed class TestConnectionDialog : Form
    {
        private readonly DeepSeekEndpointConfig _config;
        private readonly Label _endpointValueLabel;
        private readonly Label _modelValueLabel;
        private readonly Button _testButton;
        private readonly Label _statusLabel;

        public TestConnectionDialog(DeepSeekEndpointConfig config)
        {
            _config = config;
            var l = LocalizationService.Instance;

            Text = l["settings.testConnection.title"];
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(548, 236);
            Font = new Font("Segoe UI", 9f);

            var endpointLabel = new Label
            {
                Text = l["settings.testConnection.endpoint"],
                Location = new Point(12, 16),
                AutoSize = true,
            };
            _endpointValueLabel = new Label
            {
                Text = config.BaseUrl ?? DeepSeekApiService.DefaultBaseUrl,
                Location = new Point(150, 16),
                AutoSize = true,
            };

            var modelLabel = new Label
            {
                Text = l["settings.testConnection.model"],
                Location = new Point(12, 46),
                AutoSize = true,
            };
            _modelValueLabel = new Label
            {
                Text = config.Model,
                Location = new Point(150, 46),
                AutoSize = true,
            };

            _testButton = new Button
            {
                Text = l["settings.testConnection.test"],
                Location = new Point(12, 80),
                Size = new Size(180, 26),
            };
            _testButton.Click += async (_, _) => await TestAsync();

            _statusLabel = new Label
            {
                Text = string.Empty,
                Location = new Point(12, 118),
                Size = new Size(524, 72),
                AutoEllipsis = false,
                TextAlign = ContentAlignment.TopLeft,
                ForeColor = SystemColors.GrayText,
            };

            var closeButton = new Button
            {
                Text = l["settings.testConnection.close"],
                DialogResult = DialogResult.OK,
                Location = new Point(452, 200),
                Size = new Size(84, 26),
            };
            AcceptButton = closeButton;
            CancelButton = closeButton;

            Controls.AddRange(new Control[]
            {
                endpointLabel, _endpointValueLabel,
                modelLabel, _modelValueLabel,
                _testButton, _statusLabel, closeButton,
            });

            if (string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                _testButton.Enabled = false;
                _statusLabel.Text = l["settings.testConnection.missingKey"];
                _statusLabel.ForeColor = Color.Firebrick;
            }
        }

        private async Task TestAsync()
        {
            var l = LocalizationService.Instance;
            _testButton.Enabled = false;
            _statusLabel.Text = l["settings.testConnection.testing"];
            _statusLabel.ForeColor = SystemColors.GrayText;

            try
            {
                using var apiService = new DeepSeekApiService(
                    _config.ApiKey,
                    _config.Model,
                    baseUrl: _config.BaseUrl,
                    isVision: _config.IsVision,
                    isCustom: _config.IsCustom);
                string? error = await apiService.ValidateApiKeyAsync();

                if (string.IsNullOrWhiteSpace(error))
                {
                    _statusLabel.Text = l["settings.testConnection.success"];
                    _statusLabel.ForeColor = Color.FromArgb(0, 128, 96);
                }
                else
                {
                    _statusLabel.Text = string.Format(l["settings.testConnection.failure"], error);
                    _statusLabel.ForeColor = Color.Firebrick;
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = string.Format(l["settings.testConnection.failure"], ex.Message);
                _statusLabel.ForeColor = Color.Firebrick;
            }
            finally
            {
                _testButton.Enabled = true;
            }
        }
    }
}
