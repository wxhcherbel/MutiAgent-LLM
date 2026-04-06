/// <summary>
/// 通信消息路由范围。
/// </summary>
public enum CommunicationScope
{
    DirectAgent = 0,
    Team = 1,
    Judge = 2,
    Public = 3
}

/// <summary>
/// 通信消息类型。
/// </summary>
public enum MessageType
{
    Heartbeat,
    StatusUpdate,
    TaskAnnouncement,
    TaskUpdate,
    TaskCompletion,
    TaskAbort,
    SystemAlert,
    ResourceRequest,
    ResourceOffer,
    EnvironmentAlert,
    ObstacleWarning,
    Synchronization,
    RequestHelp,
    HelpRequest,
    Response,
    RoleAssignment,

    GroupBootstrap,
    SlotBroadcast,
    SlotSelect,
    SlotConfirm,
    StartExecution,

    // ── 紧急情况 & MAD 辩论协议 ──────────────────────────────────────────
    /// <summary>任意 agent 上报紧急事件给 leader（GroupMonitor 处理）。</summary>
    IncidentReport,

    /// <summary>Leader 宣布活跃事件，广播给全组并写白板 IncidentAnnounce。</summary>
    IncidentAnnounce,

    /// <summary>Leader 给每位成员分配辩论角色，触发其 LLM 辩论调用。</summary>
    DebateProposal,

    /// <summary>成员将辩论结果（DebateEntry）回传给 leader。</summary>
    DebateUpdate,

    /// <summary>Leader 广播最终共识（DebateConsensusEntry），全员执行决策。</summary>
    DebateResolved,
}
