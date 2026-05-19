using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 对话持久化服务 — 按项目保存/加载多轮对话会话。
    /// 文件存储在 %LocalAppData%\DeepSeekVS\conversations\ 下，
    /// 以解决方案路径的哈希值作为文件名，每个文件包含该项目的所有会话。
    /// </summary>
    internal static class ChatPersistenceService
    {
        private static readonly string BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "conversations");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>
        /// 根据解决方案路径计算持久化文件路径。
        /// 使用 SHA256 哈希（取前16位）避免路径中的非法字符。
        /// </summary>
        public static string GetStoragePath(string? solutionPath)
        {
            Directory.CreateDirectory(BaseDir);

            if (string.IsNullOrWhiteSpace(solutionPath))
                return Path.Combine(BaseDir, "_unsaved.json");

            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(solutionPath));
                var hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
                return Path.Combine(BaseDir, $"proj_{hash}.json");
            }
        }

        #region Public Methods - Session Level

        /// <summary>
        /// 加载指定解决方案的所有会话。如果文件不存在或为空，返回空容器。
        /// 自动兼容旧版单对话格式（自动迁移到多会话格式）。
        /// </summary>
        public static SessionsContainer LoadSessions(string? solutionPath)
        {
            var filePath = GetStoragePath(solutionPath);
            bool isUnsaved = string.IsNullOrWhiteSpace(solutionPath);

            try
            {
                if (!File.Exists(filePath))
                {
                    Logger.Info($"会话文件不存在，创建空容器: {Path.GetFileName(filePath)}");
                    return new SessionsContainer { SolutionPath = solutionPath ?? "(unsaved)" };
                }

                var json = File.ReadAllText(filePath, Encoding.UTF8);

                var container = JsonSerializer.Deserialize<SessionsContainer>(json, JsonOptions);
                if (container != null)
                {
                    // ── 清理 _unsaved.json 中从未使用过的冗余空会话 ──
                    int originalCount = container.Sessions.Count;
                    if (isUnsaved && originalCount > 1)
                    {
                        container.Sessions = container.Sessions
                            .Where(s => !IsUnusedEmptySession(s))
                            .ToList();

                        if (container.Sessions.Count == 0)
                        {
                            container.Sessions.Add(new ChatSession
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                Title = LocalizationService.Instance["session.new"],
                                CreatedAt = DateTime.Now,
                                LastActiveAt = DateTime.Now,
                            });
                        }

                        if (container.Sessions.All(s => s.Id != container.ActiveSessionId))
                        {
                            container.ActiveSessionId = container.Sessions[0].Id;
                        }

                        int removed = originalCount - container.Sessions.Count;
                        if (removed > 0)
                            Logger.Info($"[清理] 已移除 {removed} 个冗余空会话 ← {Path.GetFileName(filePath)}");
                    }

                    Logger.Info($"已加载 {container.Sessions.Count} 个会话 ← {Path.GetFileName(filePath)}");
                    return container;
                }

                return new SessionsContainer { SolutionPath = solutionPath ?? "(unsaved)" };
            }
            catch (Exception ex)
            {
                Logger.Error($"加载会话失败: {Path.GetFileName(filePath)}", ex);
                return new SessionsContainer { SolutionPath = solutionPath ?? "(unsaved)" };
            }
        }

        /// <summary>
        /// 保存所有会话到文件。
        /// </summary>
        public static void SaveSessions(string? solutionPath, SessionsContainer container)
        {
            if (container == null) return;

            var filePath = GetStoragePath(solutionPath);
            container.LastSaved = DateTime.Now;

            try
            {
                var json = JsonSerializer.Serialize(container, JsonOptions);
                File.WriteAllText(filePath, json, Encoding.UTF8);
                Logger.Info($"会话已保存 ({container.Sessions.Count} 个会话) → {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Logger.Error(LocalizationService.Instance["session.saveFailed"], ex);
            }
        }

        /// <summary>
        /// 删除指定会话文件（整个项目的所有会话）。
        /// </summary>
        public static void DeleteAllSessions(string? solutionPath)
        {
            var filePath = GetStoragePath(solutionPath);
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Logger.Info($"会话文件已删除: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(LocalizationService.Instance["session.deleteFailed"], ex);
            }
        }

        /// <summary>
        /// 判断会话是否为从未使用过的空会话（无 TreeData、无 ApiHistory、标题为"新对话"）。
        /// 用于 _unsaved.json 冗余清理。
        /// </summary>
        private static bool IsUnusedEmptySession(ChatSession session)
        {
            if (session == null) return true;

            // 有 TreeData → 使用过
            if (!string.IsNullOrWhiteSpace(session.TreeDataJson)) return false;

            // 有 API 历史记录 → 使用过
            if (session.ApiHistory.Count > 0) return false;

            // 标题被修改过 → 使用过
            if (!string.IsNullOrEmpty(session.Title) && session.Title != LocalizationService.Instance["session.new"]) return false;

            // 无 TreeData、无 ApiHistory、标题为默认 → 未使用
            return true;
        }

        #endregion
    }
}
