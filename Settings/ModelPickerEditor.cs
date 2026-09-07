using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// "自定义模型名称"的取模型列表编辑器（参考 CC Switch 的供应商模型抓取）。
    /// 属性网格中显示 "..." 按钮，弹出对话框：端点 + 密钥 → GET /models → 双击选择。
    /// </summary>
    public class ModelPickerEditor : UITypeEditor
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

            using var dialog = new ModelPickerDialog(
                page.ApiBaseUrl,
                ApiKeyProtection.Unprotect(page.ApiKey));
            if (dialog.ShowDialog() == DialogResult.OK &&
                !string.IsNullOrWhiteSpace(dialog.SelectedModel))
            {
                return dialog.SelectedModel;
            }

            return value;
        }
    }

    /// <summary>模型抓取对话框：GET /models + 列表选择。</summary>
    internal sealed class ModelPickerDialog : Form
    {
        private readonly TextBox _baseUrlBox;
        private readonly TextBox _apiKeyBox;
        private readonly ListBox _modelList;
        private readonly Button _fetchButton;
        private readonly Label _statusLabel;

        public string? SelectedModel => _modelList.SelectedItem as string;

        public ModelPickerDialog(string baseUrl, string apiKey)
        {
            var l = LocalizationService.Instance;
            Text = l["settings.modelPicker.title"];
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 368);
            Font = new Font("Segoe UI", 9f);

            var urlLabel = new Label
            {
                Text = l["settings.modelPicker.baseUrl"],
                Location = new Point(12, 12),
                AutoSize = true,
            };
            _baseUrlBox = new TextBox { Location = new Point(12, 32), Width = 436 };
            _baseUrlBox.Text = string.IsNullOrWhiteSpace(baseUrl)
                ? DeepSeekApiService.DefaultBaseUrl
                : baseUrl;

            var keyLabel = new Label
            {
                Text = l["settings.modelPicker.apiKey"],
                Location = new Point(12, 62),
                AutoSize = true,
            };
            _apiKeyBox = new TextBox
            {
                Location = new Point(12, 82),
                Width = 436,
                UseSystemPasswordChar = true,
            };
            _apiKeyBox.Text = apiKey ?? string.Empty;

            _fetchButton = new Button
            {
                Text = l["settings.modelPicker.fetch"],
                Location = new Point(12, 112),
                Size = new Size(160, 26),
            };
            _fetchButton.Click += async (_, _) => await FetchAsync();

            _statusLabel = new Label
            {
                Location = new Point(182, 118),
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            };

            var listLabel = new Label
            {
                Text = l["settings.modelPicker.select"],
                Location = new Point(12, 148),
                AutoSize = true,
            };
            _modelList = new ListBox
            {
                Location = new Point(12, 168),
                Size = new Size(436, 148),
            };
            _modelList.DoubleClick += (_, _) => DialogResult = DialogResult.OK;

            var okButton = new Button
            {
                Text = l["settings.modelPicker.ok"],
                DialogResult = DialogResult.OK,
                Location = new Point(273, 326),
                Size = new Size(84, 26),
            };
            var cancelButton = new Button
            {
                Text = l["settings.modelPicker.cancel"],
                DialogResult = DialogResult.Cancel,
                Location = new Point(364, 326),
                Size = new Size(84, 26),
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.AddRange(new Control[]
            {
                urlLabel, _baseUrlBox, keyLabel, _apiKeyBox,
                _fetchButton, _statusLabel, listLabel, _modelList,
                okButton, cancelButton,
            });
        }

        private async Task FetchAsync()
        {
            var l = LocalizationService.Instance;
            _fetchButton.Enabled = false;
            _modelList.Items.Clear();
            _statusLabel.Text = l["settings.modelPicker.fetching"];
            _statusLabel.ForeColor = SystemColors.GrayText;
            try
            {
                var models = await ModelFetchService.FetchModelsAsync(
                    _baseUrlBox.Text.Trim(), _apiKeyBox.Text.Trim());
                foreach (var model in models)
                    _modelList.Items.Add(model);
                _statusLabel.Text = string.Format(l["settings.modelPicker.count"], models.Count);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = ex.Message;
                _statusLabel.ForeColor = Color.Firebrick;
            }
            finally
            {
                _fetchButton.Enabled = true;
            }
        }
    }
}
