using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 对话持久化服务 — 按项目保存/加载聊天记录。
    /// 文件存储在 %LocalAppData%\DeepSeekVS\conversations\ 下，
    /// 以解决方案路径的哈希值作为文件名，实现每个项目独立的对话历史。
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

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(solutionPath));
            var hash = Convert.ToHexString(hashBytes)[..16];
            return Path.Combine(BaseDir, $"proj_{hash}.json");
        }

        /// <summary>
        /// 保存消息列表到文件。
        /// </summary>
        public static async Task SaveAsync(string? solutionPath, IReadOnlyList<ChatMessage> messages)
        {
            if (messages == null) return;

            var filePath = GetStoragePath(solutionPath);

            try
            {
                var dto = new ConversationDto
                {
                    SolutionPath = solutionPath ?? "(unsaved)",
                    LastSaved = DateTime.Now,
                    Messages = messages.ToList(),
                };

                var json = JsonSerializer.Serialize(dto, JsonOptions);
                await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
                Logger.Info($"对话已保存 ({messages.Count} 条消息) → {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Logger.Error("保存对话失败", ex);
            }
        }

        /// <summary>
        /// 同步保存（用于 Dispose 等不能使用 async 的场景）。
        /// </summary>
        public static void Save(string? solutionPath, IReadOnlyList<ChatMessage> messages)
        {
            if (messages == null) return;

            var filePath = GetStoragePath(solutionPath);

            try
            {
                var dto = new ConversationDto
                {
                    SolutionPath = solutionPath ?? "(unsaved)",
                    LastSaved = DateTime.Now,
                    Messages = messages.ToList(),
                };

                var json = JsonSerializer.Serialize(dto, JsonOptions);
                File.WriteAllText(filePath, json, Encoding.UTF8);
                Logger.Info($"对话已保存 ({messages.Count} 条消息) → {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Logger.Error("保存对话失败", ex);
            }
        }

        /// <summary>
        /// 从文件加载消息列表。如果文件不存在或损坏，返回 null。
        /// </summary>
        public static List<ChatMessage>? Load(string? solutionPath)
        {
            var filePath = GetStoragePath(solutionPath);

            try
            {
                if (!File.Exists(filePath))
                {
                    Logger.Info($"对话文件不存在: {Path.GetFileName(filePath)}");
                    return null;
                }

                var json = File.ReadAllText(filePath, Encoding.UTF8);
                var dto = JsonSerializer.Deserialize<ConversationDto>(json, JsonOptions);

                if (dto?.Messages == null || dto.Messages.Count == 0)
                {
                    Logger.Info($"对话文件为空: {Path.GetFileName(filePath)}");
                    return null;
                }

                // 确保加载的消息都不是 Streaming 状态
                foreach (var msg in dto.Messages)
                {
                    msg.IsStreaming = false;
                }

                Logger.Info($"对话已加载 ({dto.Messages.Count} 条消息) ← {Path.GetFileName(filePath)}");
                return dto.Messages;
            }
            catch (Exception ex)
            {
                Logger.Error($"加载对话失败: {Path.GetFileName(filePath)}", ex);
                return null;
            }
        }

        /// <summary>
        /// 删除指定项目的对话文件。
        /// </summary>
        public static void Delete(string? solutionPath)
        {
            var filePath = GetStoragePath(solutionPath);
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Logger.Info($"对话文件已删除: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("删除对话文件失败", ex);
            }
        }

        // ─── 内部 DTO ───

        private class ConversationDto
        {
            public string SolutionPath { get; set; } = string.Empty;
            public DateTime LastSaved { get; set; }
            public List<ChatMessage> Messages { get; set; } = new();
        }
    }
}
