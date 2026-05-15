using DeepSeek_v4_for_VisualStudio.Models;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 文件解析服务接口 — 支持解析常见文档格式的文本内容。
    /// </summary>
    public interface IFileParserService
    {
        /// <summary>检查文件扩展名是否受支持</summary>
        bool IsSupportedFormat(string filePath);

        /// <summary>获取受支持的文件扩展名列表（用于文件对话框过滤器）</summary>
        string GetFileFilter();

        /// <summary>解析单个文件，提取其文本内容</summary>
        Task<FileParseResult> ParseFileAsync(string filePath);
    }
}
