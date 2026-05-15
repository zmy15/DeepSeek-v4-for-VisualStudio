using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 对话持久化服务接口 — 按项目保存/加载多轮对话会话。
    /// </summary>
    public interface IChatPersistenceService
    {
        string GetStoragePath(string? solutionPath);
        SessionsContainer LoadSessions(string? solutionPath);
        void SaveSessions(string? solutionPath, SessionsContainer container);
        void DeleteAllSessions(string? solutionPath);
    }
}
