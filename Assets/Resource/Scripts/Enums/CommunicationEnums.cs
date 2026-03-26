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
}
