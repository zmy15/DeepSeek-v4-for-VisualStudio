using System.Runtime.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Models;

/// <summary>
/// 聊天消息实体
/// </summary>
[DataContract]
internal class ChatMessage
{
    /// <summary>角色: "user" | "assistant" | "system"</summary>
    [DataMember]
    public string? Role { get; set; }

    /// <summary>消息内容</summary>
    [DataMember]
    public string? Content { get; set; }

    /// <summary>时间戳</summary>
    [DataMember]
    public DateTime Timestamp { get; set; }

    /// <summary>是否正在生成中（流式输出动画）</summary>
    [DataMember]
    public bool IsStreaming { get; set; }

    /// <summary>关联的代码块列表</summary>
    [DataMember]
    public List<CodeBlockInfo>? CodeBlocks { get; set; }
}

/// <summary>
/// 代码块信息
/// </summary>
[DataContract]
internal class CodeBlockInfo
{
    [DataMember]
    public string? Language { get; set; }

    [DataMember]
    public string? Code { get; set; }
}
