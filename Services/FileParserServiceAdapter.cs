using DeepSeek_v4_for_VisualStudio.Models;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// IFileParserService 适配器 — 将静态 FileParserService 包装为可注入的实例。
    /// 在 FileParserService 完全转为实例类后，此适配器可移除。
    /// </summary>
    public class FileParserServiceAdapter : IFileParserService
    {
        public bool IsSupportedFormat(string filePath)
            => FileParserService.IsSupportedFormat(filePath);

        public string GetFileFilter()
            => FileParserService.GetFileFilter();

        public Task<FileParseResult> ParseFileAsync(string filePath)
            => FileParserService.ParseFileAsync(filePath);
    }
}
