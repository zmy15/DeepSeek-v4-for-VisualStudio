using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 记忆服务 — 管理 AI 持久化记忆的 CRUD 操作。
    /// 
    /// 文件存储层级：
    ///   %LocalAppData%\DeepSeekVS\memories\
    ///     ├── user\              ← User 作用域（跨解决方案）
    ///     │   └── {filename}.md
    ///     ├── session\{id}\      ← Session 作用域（当前对话）
    ///     │   └── {filename}.md
    ///     └── repo\{hash}\       ← Repo 作用域（当前解决方案）
    ///         └── {filename}.md
    /// 
    /// 并发安全：按文件路径粒度加锁。
    /// </summary>
    public class MemoryService : IMemoryService
    {
        private static readonly string BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "memories");

        /// <summary>按规范化的文件路径进行细粒度锁定</summary>
        private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>防止路径穿越的正则：只允许字母数字、中文、下划线、连字符、点号、斜杠</summary>
        private static readonly Regex SafePathRegex = new(
            @"^[\w\u4e00-\u9fff\-\.,;:!@#$%^&()+=\[\]{}'~` ]+(/[\w\u4e00-\u9fff\-\.,;:!@#$%^&()+=\[\]{}'~` ]+)*\.?[\w]*$",
            RegexOptions.Compiled);

        #region Path Resolution

        /// <summary>
        /// 根据作用域和上下文解析文件系统上的实际目录路径。
        /// </summary>
        private static string GetScopeDir(MemoryScope scope, string? sessionId, string? solutionPath)
        {
            return scope switch
            {
                MemoryScope.User => Path.Combine(BaseDir, "user"),
                MemoryScope.Session => Path.Combine(BaseDir, "session", SanitizeSessionId(sessionId)),
                MemoryScope.Repo => Path.Combine(BaseDir, "repo", ComputeSolutionHash(solutionPath)),
                _ => throw new ArgumentOutOfRangeException(nameof(scope))
            };
        }

        /// <summary>
        /// 将相对路径解析为文件系统上的绝对路径。
        /// 包含路径穿越防护。
        /// </summary>
        private static string ResolveFilePath(MemoryScope scope, string relativePath, string? sessionId, string? solutionPath)
        {
            var scopeDir = GetScopeDir(scope, sessionId, solutionPath);

            // 规范化路径：统一使用反斜杠（Windows），移除开头的 /
            var normalized = relativePath.Replace('/', '\\').TrimStart('\\').Trim();

            if (string.IsNullOrEmpty(normalized))
                return scopeDir;

            var resolved = Path.GetFullPath(Path.Combine(scopeDir, normalized));

            // ── 路径穿越防护 ──
            var normalizedScope = Path.GetFullPath(scopeDir).TrimEnd(Path.DirectorySeparatorChar);
            if (!resolved.StartsWith(normalizedScope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(resolved, normalizedScope, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"路径穿越被拒绝: '{relativePath}' 不在作用域 '{scope}' 内。");
            }

            return resolved;
        }

        private static string ComputeSolutionHash(string? solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                return "_unsaved";

            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(solutionPath));
                return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
            }
        }

        private static string SanitizeSessionId(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return "_default";

            // 只保留安全字符
            var safe = new string(sessionId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            return string.IsNullOrEmpty(safe) ? "_default" : safe;
        }

        #endregion

        #region Public API

        /// <inheritdoc />
        public async Task<MemoryViewResult> ViewAsync(
            MemoryScope scope, string path, string? sessionId = null,
            string? solutionPath = null, int? startLine = null, int? endLine = null)
        {
            var resolved = ResolveFilePath(scope, path, sessionId, solutionPath);

            if (Directory.Exists(resolved))
            {
                return ListDirectory(resolved, scope, path);
            }

            if (File.Exists(resolved))
            {
                return await Task.Run(() => ReadFile(resolved, scope, path, startLine, endLine));
            }

            // ── 如果是作用域根目录且不存在 → 返回空目录列表（首次使用时目录尚未创建）──
            if (string.IsNullOrEmpty(path) || resolved.Equals(
                GetScopeDir(scope, sessionId, solutionPath), StringComparison.OrdinalIgnoreCase))
            {
                var scopeDir = GetScopeDir(scope, sessionId, solutionPath);
                return ListDirectory(scopeDir, scope, path);
            }

            throw new FileNotFoundException($"记忆路径不存在: '{path}' (作用域: {scope})");
        }

        /// <inheritdoc />
        public Task<string> CreateAsync(
            MemoryScope scope, string path, string content,
            string? sessionId = null, string? solutionPath = null)
        {
            var resolved = ResolveFilePath(scope, path, sessionId, solutionPath);

            if (File.Exists(resolved))
                throw new InvalidOperationException($"记忆文件已存在: '{path}'。请使用 str_replace 或先 delete 再 create。");

            if (Directory.Exists(resolved))
                throw new InvalidOperationException($"路径已是目录: '{path}'");

            return Task.Run(() =>
            {
                var lockObj = GetLock(resolved);
                lock (lockObj)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                    File.WriteAllText(resolved, content, Encoding.UTF8);
                }
                Logger.Info($"[Memory] 创建: {scope}/{path} ({content.Length} 字符)");
                return $"记忆文件已创建: {scope}/{path}";
            });
        }

        /// <inheritdoc />
        public Task<string> StrReplaceAsync(
            MemoryScope scope, string path, string oldStr, string newStr,
            string? sessionId = null, string? solutionPath = null)
        {
            var resolved = ResolveFilePath(scope, path, sessionId, solutionPath);

            if (!File.Exists(resolved))
                throw new FileNotFoundException($"记忆文件不存在: '{path}' (作用域: {scope})");

            return Task.Run(() =>
            {
                var lockObj = GetLock(resolved);
                lock (lockObj)
                {
                    var content = File.ReadAllText(resolved, Encoding.UTF8);

                    var count = CountOccurrences(content, oldStr);
                    if (count == 0)
                        throw new InvalidOperationException(
                            $"oldStr 在文件中未找到，无法替换。文件: {scope}/{path}");
                    if (count > 1)
                        throw new InvalidOperationException(
                            $"oldStr 在文件中出现了 {count} 次，不唯一无法精确替换。请提供更多上下文使匹配唯一。文件: {scope}/{path}");

                    var newContent = content.Replace(oldStr, newStr);
                    File.WriteAllText(resolved, newContent, Encoding.UTF8);
                }
                Logger.Info($"[Memory] 替换: {scope}/{path}");
                return $"记忆文件已更新: {scope}/{path}";
            });
        }

        /// <inheritdoc />
        public Task<string> InsertAsync(
            MemoryScope scope, string path, int lineNumber, string text,
            string? sessionId = null, string? solutionPath = null)
        {
            var resolved = ResolveFilePath(scope, path, sessionId, solutionPath);

            if (!File.Exists(resolved))
                throw new FileNotFoundException($"记忆文件不存在: '{path}' (作用域: {scope})");

            return Task.Run(() =>
            {
                var lockObj = GetLock(resolved);
                lock (lockObj)
                {
                    var lines = File.ReadAllLines(resolved, Encoding.UTF8).ToList();
                    var idx = Math.Max(0, Math.Min(lineNumber, lines.Count));
                    lines.Insert(idx, text);
                    File.WriteAllLines(resolved, lines, Encoding.UTF8);
                }
                Logger.Info($"[Memory] 插入行 {lineNumber}: {scope}/{path}");
                return $"已在 {scope}/{path} 的第 {lineNumber} 行插入文本";
            });
        }

        /// <inheritdoc />
        public Task<string> DeleteAsync(
            MemoryScope scope, string path,
            string? sessionId = null, string? solutionPath = null)
        {
            var resolved = ResolveFilePath(scope, path, sessionId, solutionPath);

            if (Directory.Exists(resolved))
            {
                return Task.Run(() =>
                {
                    Directory.Delete(resolved, recursive: true);
                    Logger.Info($"[Memory] 删除目录: {scope}/{path}");
                    return $"记忆目录已删除: {scope}/{path}";
                });
            }

            if (File.Exists(resolved))
            {
                return Task.Run(() =>
                {
                    var lockObj = GetLock(resolved);
                    lock (lockObj)
                    {
                        File.Delete(resolved);
                    }
                    Logger.Info($"[Memory] 删除文件: {scope}/{path}");
                    return $"记忆文件已删除: {scope}/{path}";
                });
            }

            throw new FileNotFoundException($"记忆路径不存在: '{path}' (作用域: {scope})");
        }

        /// <inheritdoc />
        public Task<string> RenameAsync(
            MemoryScope scope, string oldPath, string newPath,
            string? sessionId = null, string? solutionPath = null)
        {
            var resolvedOld = ResolveFilePath(scope, oldPath, sessionId, solutionPath);
            var resolvedNew = ResolveFilePath(scope, newPath, sessionId, solutionPath);

            if (!File.Exists(resolvedOld) && !Directory.Exists(resolvedOld))
                throw new FileNotFoundException($"源路径不存在: '{oldPath}' (作用域: {scope})");

            if (File.Exists(resolvedNew) || Directory.Exists(resolvedNew))
                throw new InvalidOperationException($"目标路径已存在: '{newPath}'");

            return Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolvedNew)!);

                if (Directory.Exists(resolvedOld))
                {
                    Directory.Move(resolvedOld, resolvedNew);
                }
                else
                {
                    var lockObj = GetLock(resolvedOld);
                    lock (lockObj)
                    {
                        File.Move(resolvedOld, resolvedNew);
                    }
                }
                Logger.Info($"[Memory] 重命名: {scope}/{oldPath} → {scope}/{newPath}");
                return $"记忆已重命名: {scope}/{oldPath} → {scope}/{newPath}";
            });
        }

        /// <inheritdoc />
        public string GetMemoryPreviews(MemoryScope scope, string? sessionId = null, string? solutionPath = null)
        {
            var scopeDir = GetScopeDir(scope, sessionId, solutionPath);
            if (!Directory.Exists(scopeDir))
                return string.Empty;

            var mdFiles = Directory.GetFiles(scopeDir, "*.md", SearchOption.AllDirectories);
            if (mdFiles.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var file in mdFiles.OrderBy(f => f))
            {
                try
                {
                    var relativePath = GetRelativePath(scopeDir, file).Replace('\\', '/');
                    var content = File.ReadAllText(file, Encoding.UTF8);
                    // 取前 200 字符作为预览，去掉换行
                    var preview = content.Length > 200
                        ? content.Substring(0, 200).Replace("\r", " ").Replace("\n", " ") + "..."
                        : content.Replace("\r", " ").Replace("\n", " ");
                    sb.AppendLine($"- **{relativePath}**: {preview}");
                }
                catch { /* skip unreadable files */ }
            }

            return sb.ToString();
        }

        /// <inheritdoc />
        public string GetMemoryContext(MemoryScope scope, string? sessionId = null, string? solutionPath = null)
        {
            var scopeDir = GetScopeDir(scope, sessionId, solutionPath);
            if (!Directory.Exists(scopeDir))
                return string.Empty;

            var mdFiles = Directory.GetFiles(scopeDir, "*.md", SearchOption.AllDirectories);
            if (mdFiles.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine($"## {GetScopeLabel(scope)}");
            sb.AppendLine();

            foreach (var file in mdFiles.OrderBy(f => f))
            {
                try
                {
                    var relativePath = GetRelativePath(scopeDir, file).Replace('\\', '/');
                    var content = File.ReadAllText(file, Encoding.UTF8);
                    // 限制每个文件最多 3000 字符防止撑爆上下文
                    var truncated = content.Length > 3000
                        ? content.Substring(0, 3000) + $"\n\n[... 文件过长，已截断，共 {content.Length} 字符 ...]"
                        : content;

                    sb.AppendLine($"### {relativePath}");
                    sb.AppendLine(truncated);
                    sb.AppendLine();
                }
                catch { /* skip unreadable files */ }
            }

            return sb.ToString();
        }

        #endregion

        #region Private Helpers

        private static object GetLock(string filePath)
        {
            return FileLocks.GetOrAdd(Path.GetFullPath(filePath), _ => new object());
        }

        private static MemoryViewResult ListDirectory(string resolvedDir, MemoryScope scope, string relativePath)
        {
            var entries = new List<MemoryEntry>();

            // ── 目录不存在时返回空列表（首次使用该作用域时目录尚未创建）──
            if (!Directory.Exists(resolvedDir))
            {
                return new MemoryViewResult
                {
                    Path = string.IsNullOrEmpty(relativePath) ? "/" : relativePath,
                    IsDirectoryListing = true,
                    Entries = entries,
                };
            }

            // 列出目录
            foreach (var dir in Directory.GetDirectories(resolvedDir))
            {
                entries.Add(new MemoryEntry
                {
                    Name = Path.GetFileName(dir) + "/",
                    IsDirectory = true,
                    LastModified = Directory.GetLastWriteTime(dir)
                });
            }

            // 列出 .md 文件
            foreach (var file in Directory.GetFiles(resolvedDir, "*.md"))
            {
                var fi = new FileInfo(file);
                entries.Add(new MemoryEntry
                {
                    Name = fi.Name,
                    IsDirectory = false,
                    SizeBytes = fi.Length,
                    LastModified = fi.LastWriteTime
                });
            }

            return new MemoryViewResult
            {
                Path = relativePath,
                IsDirectoryListing = true,
                Entries = entries.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name).ToList(),
                Scope = scope
            };
        }

        private static MemoryViewResult ReadFile(
            string resolvedFile, MemoryScope scope, string relativePath,
            int? startLine, int? endLine)
        {
            var lines = File.ReadAllLines(resolvedFile, Encoding.UTF8);
            var totalLines = lines.Length;

            var startIdx = Math.Max(0, (startLine ?? 1) - 1);
            var endIdx = Math.Min(totalLines - 1, (endLine ?? totalLines) - 1);

            if (startIdx > endIdx)
                (startIdx, endIdx) = (endIdx, startIdx);

            var selectedLines = lines.Skip(startIdx).Take(endIdx - startIdx + 1);
            var content = string.Join("\n", selectedLines);

            return new MemoryViewResult
            {
                Path = relativePath,
                Content = content,
                IsDirectoryListing = false,
                ViewStartLine = startIdx + 1,
                ViewEndLine = endIdx + 1,
                TotalLines = totalLines,
                Scope = scope
            };
        }

        /// <summary>
        /// 统计 oldStr 在 content 中的出现次数（区分大小写）。
        /// </summary>
        private static int CountOccurrences(string content, string oldStr)
        {
            if (string.IsNullOrEmpty(oldStr))
                return 0;

            int count = 0;
            int idx = 0;
            while ((idx = content.IndexOf(oldStr, idx, StringComparison.Ordinal)) != -1)
            {
                count++;
                idx += oldStr.Length;
            }
            return count;
        }

        private static string GetScopeLabel(MemoryScope scope) => scope switch
        {
            MemoryScope.User => "用户记忆",
            MemoryScope.Session => "会话记忆",
            MemoryScope.Repo => "仓库记忆",
            _ => "记忆"
        };

        /// <summary>
        /// .NET Framework 兼容的 GetRelativePath 实现。
        /// </summary>
        private static string GetRelativePath(string relativeTo, string path)
        {
            var baseUri = new Uri(relativeTo.EndsWith("\\") ? relativeTo : relativeTo + "\\");
            var fullUri = new Uri(path);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString());
        }

        #endregion
    }
}
