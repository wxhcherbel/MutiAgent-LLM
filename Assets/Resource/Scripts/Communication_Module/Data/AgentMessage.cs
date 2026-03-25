/// <summary>
/// 智能体之间传递的统一消息载体。
/// </summary>
[System.Serializable]
public class AgentMessage
{
    /// <summary>发送方智能体 ID。</summary>
    public string SenderID;

    /// <summary>兼容旧接口保留的接收方字段。</summary>
    public string ReceiverID;

    /// <summary>显式点对点目标智能体 ID。</summary>
    public string TargetAgentId;

    /// <summary>显式目标队伍 ID，团队广播时使用。</summary>
    public string TargetTeamId;

    /// <summary>发送方所属队伍 ID。</summary>
    public string SenderTeamId;

    /// <summary>消息语义类型。</summary>
    public MessageType Type;

    /// <summary>消息优先级，值越大越重要。</summary>
    public int Priority;

    /// <summary>消息发送时间戳。</summary>
    public float Timestamp;

    /// <summary>消息正文，通常是纯文本或 JSON 字符串。</summary>
    public string Content;

    /// <summary>消息的路由范围，例如单播、队伍广播或全局广播。</summary>
    public CommunicationScope Scope;

    /// <summary>场景或演训实例 ID，用于跨场景隔离。</summary>
    public string ScenarioId;

    /// <summary>所属任务 ID，用于筛掉跨任务消息。</summary>
    public string MissionId;

    /// <summary>Content 中载荷对象的类型名，便于调试与反序列化检查。</summary>
    public string PayloadType;

    /// <summary>是否强制可靠投递，true 时可绕过距离衰减等限制。</summary>
    public bool Reliable;
}
