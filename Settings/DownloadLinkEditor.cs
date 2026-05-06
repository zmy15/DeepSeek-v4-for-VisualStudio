using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 可点击的超链接编辑器，用于 VS Options 属性网格。
    /// 在属性网格中显示 "🔗 点击下载" 文本，点击 "..." 按钮后在浏览器中打开 URL。
    /// 
    /// 使用方式：
    /// [Editor(typeof(DownloadLinkEditor), typeof(UITypeEditor))]
    /// public string DownloadLink => "https://...";
    /// </summary>
    public class DownloadLinkEditor : UITypeEditor
    {
        /// <summary>
        /// 使用模态编辑风格，在属性值右侧显示 "..." 浏览按钮。
        /// </summary>
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)
            => UITypeEditorEditStyle.Modal;

        /// <summary>
        /// 点击浏览按钮时，在默认浏览器中打开 URL。
        /// 返回值本身不变，仅触发下载操作。
        /// </summary>
        public override object? EditValue(
            ITypeDescriptorContext? context,
            IServiceProvider provider,
            object? value)
        {
            if (value is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    // 浏览器打开失败时静默忽略，URL 仍显示在属性值中供用户手动复制
                    System.Diagnostics.Debug.WriteLine(
                        $"[DownloadLinkEditor] 无法打开浏览器: {ex.Message}");
                }
            }

            // 返回值不变 — 该属性仅用于触发下载操作
            return value;
        }

        /// <summary>
        /// 在属性网格的值列中绘制提示文字 "🔗 点击下载"。
        /// </summary>
        public override bool GetPaintValueSupported(ITypeDescriptorContext? context) => true;

        public override void PaintValue(PaintValueEventArgs e)
        {
            if (e.Graphics == null) return;

            // 绘制蓝色链接样式文本
            using var font = new Font(
                e.Graphics.ClipBounds.IsEmpty
                    ? "Segoe UI"
                    : SystemFonts.DefaultFont.FontFamily.Name,
                8.5f);
            using var brush = new SolidBrush(Color.FromArgb(0, 102, 204));

            var format = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
            };

            e.Graphics.DrawString("🔗 点击下载", font, brush, e.Bounds, format);
        }
    }
}