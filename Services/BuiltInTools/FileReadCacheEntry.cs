using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// 文件读取缓存条目，包含完整文件内容、已读行范围和上次读取轮次。
    /// 
    /// 缓存策略：
    /// - FullContent 始终存储完整文件内容（无行号前缀），用于磁盘变更检测。
    /// - ReadRanges 追踪 AI 已被服务的行范围，避免同一范围重复读取，但允许不同范围的合法读取。
    /// - 轮数过期后清空 ReadRanges，允许全部重读。
    /// </summary>
    public struct FileReadCacheEntry
    {
        /// <summary>完整文件原始内容（无行号前缀），用于磁盘变更比较和缓存服务</summary>
        public string FullContent;

        /// <summary>已读行范围列表（1-based，闭区间）。新请求范围若完全被已有范围覆盖则拦截，否则放行并追加。</summary>
        public List<(int Start, int End)> ReadRanges;

        /// <summary>上次读取时的 API 请求轮次号（0 表示未在工具循环中读取）</summary>
        public int LastReadRound;

        /// <summary>
        /// 判断给定行范围是否已被已有范围完全覆盖。
        /// </summary>
        public readonly bool IsRangeCovered(int reqStart, int reqEnd)
        {
            if (ReadRanges == null || ReadRanges.Count == 0)
                return false;
            foreach (var (start, end) in ReadRanges)
            {
                if (start <= reqStart && end >= reqEnd)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 将新行范围合并到已读范围列表中（简单追加，不做区间合并）。
        /// </summary>
        public void AddRange(int start, int end)
        {
            ReadRanges ??= new List<(int, int)>();
            ReadRanges.Add((start, end));
        }
    }
}
