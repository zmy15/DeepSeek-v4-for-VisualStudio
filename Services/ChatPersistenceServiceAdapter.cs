using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// IChatPersistenceService 适配器 — 将静态 ChatPersistenceService 包装为可注入的实例。
    /// 在 ChatPersistenceService 完全转为实例类后，此适配器可移除。
    /// </summary>
    public class ChatPersistenceServiceAdapter : IChatPersistenceService
    {
        public string GetStoragePath(string? solutionPath)
            => ChatPersistenceService.GetStoragePath(solutionPath);

        public SessionsContainer LoadSessions(string? solutionPath)
            => ChatPersistenceService.LoadSessions(solutionPath);

        public void SaveSessions(string? solutionPath, SessionsContainer container)
            => ChatPersistenceService.SaveSessions(solutionPath, container);

        public void DeleteAllSessions(string? solutionPath)
            => ChatPersistenceService.DeleteAllSessions(solutionPath);
    }
}
