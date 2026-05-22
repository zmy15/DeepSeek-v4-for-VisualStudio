using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// RAG 数据源标记属性。
    /// 用于标记方法或代码位置为 RAG（检索增强生成）可索引的数据源。
    /// 后续可通过反射扫描所有标记位置，自动将代码读入向量数据库。
    /// 
    /// 使用约定：
    /// - 方法级：[RagSource("file-read", "读取用户附加的文件内容")]
    /// - 代码行级：// RAG-SOURCE: file-read 读取 workspace 文件内容
    /// - 无截断标记：// RAG-MARK: no-truncate — 此位置已移除截断逻辑，完整传递内容
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public class RagSourceAttribute : Attribute
    {
        /// <summary>数据源类别（如 file-read, code-read, web-fetch, terminal-output, search-result）</summary>
        public string Category { get; }

        /// <summary>数据源描述</summary>
        public string Description { get; }

        public RagSourceAttribute(string category, string description)
        {
            Category = category;
            Description = description;
        }
    }

    /// <summary>
    /// RAG（检索增强生成）提供者接口。
    /// 预留接口，允许接入外部知识库或向量数据库。
    /// 实现此接口即可将检索结果注入到对话上下文中。
    /// </summary>
    public interface IRagProvider
    {
        /// <summary>提供者名称（用于日志和选项选择）</summary>
        string ProviderName { get; }

        /// <summary>提供者描述</summary>
        string Description { get; }

        /// <summary>是否已初始化并可正常使用</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 初始化提供者（如连接向量数据库、加载索引等）。
        /// </summary>
        /// <param name="config">JSON 格式的配置字符串</param>
        /// <returns>是否初始化成功</returns>
        Task<bool> InitializeAsync(string config);

        /// <summary>
        /// 根据用户查询检索相关文档片段。
        /// </summary>
        /// <param name="query">用户查询文本</param>
        /// <param name="topK">返回的最大结果数</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>检索到的文档列表</returns>
        Task<List<RagSearchResult>> SearchAsync(
            string query,
            int topK = 5,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 向知识库添加文档。
        /// </summary>
        /// <param name="documents">要添加的文档列表</param>
        Task AddDocumentsAsync(IEnumerable<RagDocument> documents);

        /// <summary>
        /// 从知识库移除文档。
        /// </summary>
        /// <param name="documentIds">要移除的文档 ID 列表</param>
        Task RemoveDocumentsAsync(IEnumerable<string> documentIds);

        /// <summary>
        /// 获取知识库统计信息。
        /// </summary>
        Task<RagStats> GetStatsAsync();
    }

    /// <summary>
    /// RAG 检索结果。
    /// </summary>
    public class RagSearchResult
    {
        /// <summary>文档 ID</summary>
        public string DocumentId { get; set; } = string.Empty;

        /// <summary>文档标题或文件名</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>检索到的文本片段</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>相关性分数 (0.0 ~ 1.0)</summary>
        public double RelevanceScore { get; set; }

        /// <summary>来源信息（如文件路径、URL 等）</summary>
        public string? Source { get; set; }

        /// <summary>额外元数据</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// RAG 知识库文档。
    /// </summary>
    public class RagDocument
    {
        /// <summary>文档唯一 ID</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>文档标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>文档正文内容</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>文档来源路径</summary>
        public string? SourcePath { get; set; }

        /// <summary>文档类型（如 "code", "documentation", "api_spec"）</summary>
        public string DocumentType { get; set; } = "general";

        /// <summary>添加时间</summary>
        public DateTime AddedAt { get; set; } = DateTime.Now;

        /// <summary>额外元数据</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// RAG 知识库统计信息。
    /// </summary>
    public class RagStats
    {
        /// <summary>文档总数</summary>
        public int TotalDocuments { get; set; }

        /// <summary>总 Token 数（估算）</summary>
        public int TotalTokens { get; set; }

        /// <summary>最后更新时间</summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>提供者名称</summary>
        public string ProviderName { get; set; } = string.Empty;
    }

    /// <summary>
    /// RAG 上下文注入格式。
    /// 用于将检索结果格式化为可注入对话的上下文字符串。
    /// </summary>
    public static class RagContextFormatter
    {
        /// <summary>
        /// 将 RAG 检索结果格式化为 AI 可读的上下文字符串。
        /// </summary>
        public static string FormatForContext(List<RagSearchResult> results)
        {
            if (results == null || results.Count == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[RAG 检索结果] 以下是从知识库中检索到的相关文档片段：");
            sb.AppendLine();

            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.AppendLine($"📄 [{i + 1}] {r.Title} (相关度: {r.RelevanceScore:P0})");
                if (!string.IsNullOrEmpty(r.Source))
                    sb.AppendLine($"   来源: {r.Source}");
                sb.AppendLine($"   {r.Content}");
                sb.AppendLine();
            }

            sb.AppendLine("[/RAG 检索结果]");
            return sb.ToString();
        }
    }
}
