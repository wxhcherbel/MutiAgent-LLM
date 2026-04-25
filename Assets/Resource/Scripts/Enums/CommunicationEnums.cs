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
    /// <summary>任意 Agent 上报紧急事件给 leader（MADCoordinator 处理）。</summary>
    IncidentReport,

    /// <summary>Leader 广播紧急事件通知，isCritical=true 时中断成员 ADM。</summary>
    IncidentAnnounce,

    /// <summary>Leader → 成员：发起辩论查询（round=1 独立提案，round=2 参考修正）。</summary>
    IncidentQuery,

    /// <summary>成员 → Leader：回传 LLM 生成的辩论意见（MemberOpinion）。</summary>
    IncidentOpinion,

    /// <summary>Leader → 全组：广播最终决策（IncidentDecision），含逐人指令。</summary>
    IncidentResolved,

    // ── 自主涌现协作招募协议 ──────────────────────────────────────────────
    /// <summary>涌现发起方广播协作邀请，Content 为任务目标文本，SenderID 为发起者。</summary>
    ColabInvite,

    /// <summary>接受邀请的 Idle agent 回传，SenderID 为接受者，Content 为发起者 ID。</summary>
    ColabAccept,

    /// <summary>发起方向接受者发送最终角色+约束分配。</summary>
    ColabStart,
}
