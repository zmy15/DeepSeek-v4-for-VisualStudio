using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 代码差异比较服务 — 优先使用 VS SDK IDifferenceService，回退简单逐行比较。
    /// </summary>
    public static class CodeDiffService
    {
        #region Fields

        /// <summary>VS SDK 差异服务是否已尝试解析。</summary>
        private static bool _vsSdkProbed;
        /// <summary>VS SDK IDifferenceService 是否可用。</summary>
        private static bool _vsSdkAvailable;

        #endregion

        #region Public Methods

        /// <summary>
        /// 计算两段文本的行级差异。优先使用 VS SDK IDifferenceService，
        /// 不可用时回退到简单逐行比较。
        /// </summary>
        /// <param name="oldText">原始文本</param>
        /// <param name="newText">新文本</param>
        /// <returns>差异行列表</returns>
        public static List<DiffLine> ComputeDiff(string oldText, string newText)
        {
            var oldLines = SplitLines(oldText);
            var newLines = SplitLines(newText);

            // 尝试使用 VS SDK 差异服务
            if (TryGetVsDiffService(out var diffService))
            {
                try
                {
                    return ComputeDiffUsingVsSdk(diffService, oldLines, newLines);
                }
                catch
                {
                    // 回退到简单比较
                }
            }

            return ComputeDiffSimple(oldLines, newLines);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 尝试获取 VS SDK IDifferenceService。使用反射避免单元测试环境中因程序集缺失而崩溃。
        /// </summary>
        private static bool TryGetVsDiffService(out object? diffService)
        {
            diffService = null;
            if (_vsSdkProbed) return _vsSdkAvailable;

            _vsSdkProbed = true;
            try
            {
                return TryResolveVsDiffService(out diffService);
            }
            catch (System.IO.FileNotFoundException) { return false; }
            catch (System.Reflection.ReflectionTypeLoadException) { return false; }
            catch (TypeLoadException) { return false; }
            catch
            {
                _vsSdkAvailable = false;
                return false;
            }
        }

        /// <summary>
        /// 实际解析 VS SDK IDifferenceService（可能因程序集缺失而抛出 FileNotFoundException）。
        /// </summary>
        private static bool TryResolveVsDiffService(out object? diffService)
        {
            diffService = null;

            ThreadHelper.ThrowIfNotOnUIThread();
            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            if (componentModel == null) return false;

            var exportProvider = componentModel.DefaultExportProvider;
            var diffServiceType = Type.GetType(
                "Microsoft.VisualStudio.Text.Differencing.IDifferenceService, " +
                "Microsoft.VisualStudio.Text.Data, Version=17.0.0.0, " +
                "Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
            if (diffServiceType == null) return false;

            // 通过反射调用 GetExport<T>().Value
            var exportProviderType = exportProvider.GetType();
            var getExportMethod = exportProviderType.GetMethod("GetExport", Type.EmptyTypes)?
                .MakeGenericMethod(diffServiceType);
            if (getExportMethod == null) return false;

            var export = getExportMethod.Invoke(exportProvider, null);
            if (export == null) return false;

            var valueProp = export.GetType().GetProperty("Value");
            diffService = valueProp?.GetValue(export);
            _vsSdkAvailable = diffService != null;
            return _vsSdkAvailable;
        }

        /// <summary>
        /// 通过反射调用 VS SDK IDifferenceService.DifferenceSequences 计算行级差异。
        /// </summary>
        private static List<DiffLine> ComputeDiffUsingVsSdk(
            object diffService, List<string> oldLines, List<string> newLines)
        {
            var result = new List<DiffLine>();

            // 反射调用: diffService.DifferenceSequences<string>(oldLines, newLines)
            var method = diffService.GetType().GetMethod("DifferenceSequences");
            if (method == null) return ComputeDiffSimple(oldLines, newLines);

            var genericMethod = method.MakeGenericMethod(typeof(string));
            var diffCollection = genericMethod.Invoke(diffService, new object[] { oldLines, newLines });
            if (diffCollection == null) return ComputeDiffSimple(oldLines, newLines);

            // 获取 diffCollection.Differences (IList<Difference>)
            var differencesProp = diffCollection.GetType().GetProperty("Differences");
            var differences = differencesProp?.GetValue(diffCollection) as System.Collections.IList;
            if (differences == null) return ComputeDiffSimple(oldLines, newLines);

            int oldIdx = 0;
            int newIdx = 0;

            foreach (var diff in differences)
            {
                var diffType = diff.GetType();
                // diff.DifferenceType
                var diffTypeProp = diffType.GetProperty("DifferenceType");
                var leftProp = diffType.GetProperty("Left");
                var rightProp = diffType.GetProperty("Right");

                if (diffTypeProp == null || leftProp == null || rightProp == null) continue;

                // Left/Right 是 Microsoft.VisualStudio.Text.Span
                var leftSpan = leftProp.GetValue(diff);
                var rightSpan = rightProp.GetValue(diff);
                var leftStart = (int)leftSpan.GetType().GetProperty("Start")!.GetValue(leftSpan)!;
                var leftEnd = (int)leftSpan.GetType().GetProperty("End")!.GetValue(leftSpan)!;
                var rightStart = (int)rightSpan.GetType().GetProperty("Start")!.GetValue(rightSpan)!;
                var rightEnd = (int)rightSpan.GetType().GetProperty("End")!.GetValue(rightSpan)!;
                // DifferenceType 是 enum，转为 int 比较: 0=Add, 1=Remove, 2=Change
                var dt = (int)diffTypeProp.GetValue(diff)!;

                // 输出差异之前的未变行
                while (oldIdx < leftStart && newIdx < rightStart && oldIdx < oldLines.Count && newIdx < newLines.Count)
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Unchanged,
                        OldLineNumber = oldIdx + 1,
                        NewLineNumber = newIdx + 1,
                        Content = oldLines[oldIdx]
                    });
                    oldIdx++;
                    newIdx++;
                }

                // 0=Add, 1=Remove, 2=Change (对应于 DifferenceType enum)
                switch (dt)
                {
                    case 1: // Remove
                        for (int i = leftStart; i < leftEnd && i < oldLines.Count; i++)
                        {
                            result.Add(new DiffLine
                            {
                                Type = DiffLineType.Deleted,
                                OldLineNumber = i + 1,
                                NewLineNumber = null,
                                Content = oldLines[i]
                            });
                        }
                        oldIdx = leftEnd;
                        break;

                    case 0: // Add
                        for (int i = rightStart; i < rightEnd && i < newLines.Count; i++)
                        {
                            result.Add(new DiffLine
                            {
                                Type = DiffLineType.Added,
                                OldLineNumber = null,
                                NewLineNumber = i + 1,
                                Content = newLines[i]
                            });
                        }
                        newIdx = rightEnd;
                        break;

                    case 2: // Change
                        for (int i = leftStart; i < leftEnd && i < oldLines.Count; i++)
                        {
                            result.Add(new DiffLine
                            {
                                Type = DiffLineType.Deleted,
                                OldLineNumber = i + 1,
                                NewLineNumber = null,
                                Content = oldLines[i]
                            });
                        }
                        for (int i = rightStart; i < rightEnd && i < newLines.Count; i++)
                        {
                            result.Add(new DiffLine
                            {
                                Type = DiffLineType.Added,
                                OldLineNumber = null,
                                NewLineNumber = i + 1,
                                Content = newLines[i]
                            });
                        }
                        oldIdx = leftEnd;
                        newIdx = rightEnd;
                        break;
                }
            }

            // 输出剩余未变行
            while (oldIdx < oldLines.Count && newIdx < newLines.Count)
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Unchanged,
                    OldLineNumber = oldIdx + 1,
                    NewLineNumber = newIdx + 1,
                    Content = oldLines[oldIdx]
                });
                oldIdx++;
                newIdx++;
            }

            // 剩余删除行
            while (oldIdx < oldLines.Count)
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Deleted,
                    OldLineNumber = oldIdx + 1,
                    NewLineNumber = null,
                    Content = oldLines[oldIdx]
                });
                oldIdx++;
            }

            // 剩余新增行
            while (newIdx < newLines.Count)
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Added,
                    OldLineNumber = null,
                    NewLineNumber = newIdx + 1,
                    Content = newLines[newIdx]
                });
                newIdx++;
            }

            return result;
        }

        /// <summary>
        /// 简单逐行比较（无需 VS SDK 的兜底算法）。
        /// 使用行匹配 + 贪心合并来处理插入/删除场景。
        /// </summary>
        private static List<DiffLine> ComputeDiffSimple(List<string> oldLines, List<string> newLines)
        {
            var result = new List<DiffLine>();

            // 使用前向匹配：找到 old/new 之间的共同前缀和共同后缀
            int oldCount = oldLines.Count;
            int newCount = newLines.Count;

            int prefixMatch = 0;
            while (prefixMatch < oldCount && prefixMatch < newCount &&
                   oldLines[prefixMatch] == newLines[prefixMatch])
                prefixMatch++;

            int suffixMatch = 0;
            while (suffixMatch < oldCount - prefixMatch &&
                   suffixMatch < newCount - prefixMatch &&
                   oldLines[oldCount - 1 - suffixMatch] == newLines[newCount - 1 - suffixMatch])
                suffixMatch++;

            // 输出共同前缀
            for (int i = 0; i < prefixMatch; i++)
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Unchanged,
                    OldLineNumber = i + 1,
                    NewLineNumber = i + 1,
                    Content = oldLines[i]
                });
            }

            // 中间差异区域
            int oldMidStart = prefixMatch;
            int oldMidEnd = oldCount - suffixMatch;
            int newMidStart = prefixMatch;
            int newMidEnd = newCount - suffixMatch;

            // 在中间区域用 LCS 匹配
            var oldMid = oldLines.GetRange(oldMidStart, oldMidEnd - oldMidStart);
            var newMid = newLines.GetRange(newMidStart, newMidEnd - newMidStart);
            var lcsIndices = ComputeCompactLcs(oldMid, newMid);

            int oi = 0, ni = 0;
            foreach (var (oa, na) in lcsIndices)
            {
                // 输出 LCS 匹配前的删除行
                while (oi < oa)
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Deleted,
                        OldLineNumber = oldMidStart + oi + 1,
                        NewLineNumber = null,
                        Content = oldMid[oi]
                    });
                    oi++;
                }
                // 输出 LCS 匹配前的新增行
                while (ni < na)
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Added,
                        OldLineNumber = null,
                        NewLineNumber = newMidStart + ni + 1,
                        Content = newMid[ni]
                    });
                    ni++;
                }
                // 输出匹配行
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Unchanged,
                    OldLineNumber = oldMidStart + oi + 1,
                    NewLineNumber = newMidStart + ni + 1,
                    Content = oldMid[oi]
                });
                oi++;
                ni++;
            }

            // 剩余删除行
            while (oi < oldMid.Count)
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Deleted,
                    OldLineNumber = oldMidStart + oi + 1,
                    NewLineNumber = null,
                    Content = oldMid[oi]
                });
                oi++;
            }
            // 剩余新增行
            while (ni < newMid.Count)
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Added,
                    OldLineNumber = null,
                    NewLineNumber = newMidStart + ni + 1,
                    Content = newMid[ni]
                });
                ni++;
            }

            // 输出共同后缀
            for (int i = 0; i < suffixMatch; i++)
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Unchanged,
                    OldLineNumber = oldMidEnd + i + 1,
                    NewLineNumber = newMidEnd + i + 1,
                    Content = oldLines[oldMidEnd + i]
                });
            }

            return result;
        }

        /// <summary>
        /// 紧凑 LCS 计算：只对中间差异区域使用 DP，用滚动数组减小内存。
        /// 返回 LCS 中每对匹配的 (oldIndex, newIndex)。
        /// </summary>
        private static List<(int oldIdx, int newIdx)> ComputeCompactLcs(
            List<string> a, List<string> b)
        {
            if (a.Count == 0 || b.Count == 0)
                return new List<(int, int)>();

            int m = a.Count, n = b.Count;

            // 使用两行滚动数组的 DP（O(min(m,n)) 空间）
            int[] prev = new int[n + 1];
            int[] curr = new int[n + 1];

            // 存储回溯信息：dpPrev[i,j] → 上一个最优状态
            var back = new byte[m + 1, n + 1]; // 0=diag, 1=up, 2=left

            for (int i = 1; i <= m; i++)
            {
                curr[0] = 0;
                for (int j = 1; j <= n; j++)
                {
                    if (a[i - 1] == b[j - 1])
                    {
                        curr[j] = prev[j - 1] + 1;
                        back[i, j] = 0; // diagonal
                    }
                    else if (prev[j] >= curr[j - 1])
                    {
                        curr[j] = prev[j];
                        back[i, j] = 1; // up
                    }
                    else
                    {
                        curr[j] = curr[j - 1];
                        back[i, j] = 2; // left
                    }
                }
                var tmp = prev;
                prev = curr;
                curr = tmp;
            }

            // 回溯构造 LCS 匹配对
            var result = new List<(int, int)>();
            int x = m, y = n;
            while (x > 0 && y > 0)
            {
                switch (back[x, y])
                {
                    case 0: // diagonal
                        x--; y--;
                        result.Add((x, y));
                        break;
                    case 1: // up
                        x--;
                        break;
                    case 2: // left
                        y--;
                        break;
                }
            }
            result.Reverse();
            return result;
        }

        private static List<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new List<string>();

            var lines = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    lines.Add(sb.ToString());
                    sb.Clear();
                }
                else if (text[i] == '\n')
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(text[i]);
                }
            }
            lines.Add(sb.ToString());
            return lines;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// diff 行类型。
    /// </summary>
    public enum DiffLineType
    {
        Unchanged,
        Added,
        Deleted
    }

    /// <summary>
    /// 表示一行 diff 结果。
    /// </summary>
    public class DiffLine
    {
        public DiffLineType Type { get; set; }
        public int? OldLineNumber { get; set; }
        public int? NewLineNumber { get; set; }
        public string Content { get; set; } = string.Empty;
    }

    #endregion
}
