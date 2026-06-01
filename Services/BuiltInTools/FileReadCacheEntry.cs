using System;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// 文件读取缓存条目，包含文件内容和上次读取时的轮次号。
    /// 用于支持基于轮数的缓存过期策略：经过一定轮数后允许 AI 重新读取文件以刷新上下文。
    /// </summary>
    public struct FileReadCacheEntry
    {
        /// <summary>文件内容</summary>
        public string Content;

        /// <summary>上次读取时的 API 请求轮次号（0 表示未在工具循环中读取）</summary>
        public int LastReadRound;
    }
}
