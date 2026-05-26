using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 构建服务接口 — 为 Agent 提供解决方案构建能力。
    /// 实现类负责与 VS SDK (IVsSolutionBuildManager / DTE) 交互。
    /// </summary>
    public interface IBuildService
    {
        /// <summary>
        /// 执行解决方案构建。
        /// </summary>
        /// <param name="solutionPath">解决方案路径或工作区根目录（.sln 文件、CMakeLists.txt 所在目录等）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>构建结果摘要（成功/失败 + 错误详情）</returns>
        Task<string> BuildAsync(string? solutionPath, CancellationToken ct);

        /// <summary>
        /// 获取错误列表中用户当前选中的错误项信息。
        /// 通过 IVsTaskList2 (SVsErrorList) 接口获取 VS Error List 窗口中用户高亮的条目。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>
        /// 选中错误项的结构化信息列表。
        /// 如果没有选中任何项，返回空列表。
        /// </returns>
        Task<List<ErrorListItem>> GetSelectedErrorsAsync(CancellationToken ct);
    }

    /// <summary>
    /// 错误列表项的结构化信息 — 从 VS Error List 窗口提取的标准字段。
    /// </summary>
    public class ErrorListItem
    {
        /// <summary>错误描述文本（如 "CS0103: 当前上下文中不存在名称 'foo'"）</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>文件绝对路径</summary>
        public string? FileName { get; set; }

        /// <summary>行号（1-based，0 表示未知）</summary>
        public int Line { get; set; }

        /// <summary>列号（1-based，0 表示未知）</summary>
        public int Column { get; set; }

        /// <summary>错误代码（如 "CS0103"、"MSB4018"）</summary>
        public string? ErrorCode { get; set; }

        /// <summary>所属项目名称</summary>
        public string? Project { get; set; }

        /// <summary>类别（error 或 warning）</summary>
        public string Category { get; set; } = "error";

        /// <summary>优先级（high / normal / low）</summary>
        public string Priority { get; set; } = "normal";

        /// <summary>子类别（如 "Compiler"、"Build"）</summary>
        public string? SubCategory { get; set; }
    }
}
