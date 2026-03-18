using UnityEngine;

/// <summary>
/// 智能体平台类型。
/// </summary>
public enum AgentType
{
    Quadcopter,
    WheeledRobot,
}

/// <summary>
/// 小节点类型。
/// </summary>
public enum SmallNodeType
{
    Unknown = 0,
    Tree = 1,
    Pedestrian = 2,
    Vehicle = 3,
    ResourcePoint = 4,
    TemporaryObstacle = 5,
    Agent = 6,
    Custom = 99
}

[System.Serializable]
public class SmallNodeData
{
    public string NodeId;
    public SmallNodeType NodeType;
    public Vector3 WorldPosition;
    public bool IsDynamic;
    public bool BlocksMovement;
    public float FirstSeenTime;
    public float LastSeenTime;
    public float Confidence;
    public int SeenCount;
    public string SourceAgentId;
    public string DisplayName;
    public GameObject SceneObject;
}

[System.Serializable]
public class SmallNodePerceptionSnapshot
{
    public string AgentId;
    public float QueryTime;
    public Vector3 AgentPosition;
    public float QueryRadius;
    public System.Collections.Generic.List<SmallNodeData> NearbyStaticNodes = new();
    public System.Collections.Generic.List<SmallNodeData> NearbyDynamicNodes = new();
    public System.Collections.Generic.List<SmallNodeData> NearbyResourceNodes = new();
}

[System.Serializable]
public class CampusFeaturePerceptionData
{
    public string FeatureUid;
    public string FeatureName;
    public string FeatureKind;
    public bool BlocksMovement;
    public Vector2Int AnchorGridCell;
    public Vector3 AnchorWorldPosition;
    public Vector3 ApproxCenterWorldPosition;
    public int ObservedCellCount;
    public System.Collections.Generic.List<Vector2Int> ObservedSampleCells = new();
    public float FirstSeenTime;
    public float LastSeenTime;
    public int SeenCount;
    public float Confidence;
    public string SourceAgentId;
}

public enum AgentStatus
{
    Idle,
    Moving,
    Thinking,
    ExecutingTask,
    Charging,
    Error
}

public enum TeamRelationshipType
{
    Cooperation = 0,
    Competition = 1,
    Adversarial = 2,
    Mixed = 3
}

public enum NavigationPolicy
{
    Auto = 0,
    PreferLocal = 1,
    PreferGlobalAStar = 2
}

public enum RoleType
{
    Supporter,
    Scout,
    Assault,
    Defender,
    Transporter
}

public enum CommunicationMode
{
    Centralized,
    Decentralized,
    Hybrid
}

public enum CommunicationScope
{
    DirectAgent = 0,
    Team = 1,
    Judge = 2,
    Public = 3
}

public enum AgentRelation
{
    Self = 0,
    Ally = 1,
    Enemy = 2,
    Unknown = 3
}




public enum AtomicActionType
{
    MoveTo,         // 移动到目标节点
    PatrolAround,   // 绕目标节点巡逻
    Observe,        // 原地观察，激活感知
    Wait,           // 等待（时长或条件）
    FormationHold,  // 保持编队位置跟随目标智能体
    Broadcast,      // 广播消息到组内黑板
    Evade,          // 机动规避
}

public enum ADMStatus
{
    Idle,           // 空闲，等待 StartStep
    Interpreting,   // LLM-A 调用中
    Negotiating,    // 广播 plannedTargets，等待 0.5s 协商窗口
    Running,        // 正在逐步执行 actionQueue
    Interrupted,    // 被感知事件或黑板更新打断
    Replanning,     // LLM-B 调用中
    Done,           // 所有动作完成
    Failed,         // 出错
}

public enum MessageType
{
    // ─── 保留项 ──────────────────────────────────────────────
    Heartbeat,
    StatusUpdate,
    TaskAnnouncement,   // IntelligentAgent 仍引用
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
    RoleAssignment,     // IntelligentAgent 仍引用

    // ─── 新增：4阶段协商协议 ──────────────────────────────────
    GroupBootstrap,     // 协调者→全体：分组通知，payload=GroupBootstrapPayload
    SlotBroadcast,      // 组长→组内：计划槽列表，payload=SlotBroadcastPayload
    SlotSelect,         // 组员→组长：槽选择，payload=SlotSelectPayload
    SlotConfirm,        // 组长→组员（一对一）：槽确认，payload=SlotConfirmPayload
    StartExecution,     // 组长→组内：开始步骤拆解，payload=StartExecPayload
    BoardUpdate,        // 智能体→队内：黑板状态更新，payload=AgentContextUpdate
}

/// <summary>规划状态机枚举。</summary>
public enum PlanningState
{
    Idle      = 0,  // 空闲，等待任务
    Parsing   = 1,  // 协调者：LLM#1 解析任务中
    Grouping  = 2,  // 协调者：分组并广播中；成员：等待分组通知
    SlotGen   = 3,  // 组长：LLM#2 生成计划槽中
    SlotPick  = 4,  // 组员：LLM#3 选槽 + 等待组长确认
    StepGen   = 5,  // 全员：LLM#4 拆解步骤中
    Active    = 6,  // 执行中，步骤被 ActionDecisionModule 消费
    Done      = 7,  // 全部步骤完成
    Failed    = 8   // 超时或出错
}

public enum PrimitiveActionType
{
    Idle,
    MoveTo,
    Stop,
    TakeOff,
    Land,
    Hover,
    AdjustAltitude,
    RotateTo,
    PickUp,
    Drop,
    LookAt,
    Scan,
    Wait,
    Align,
    Follow,
    Orbit,
    TransmitMessage
}

/// <summary>智能体通信消息（含完整路由字段）。</summary>
[System.Serializable]
public class AgentMessage
{
    public string SenderID;
    public string ReceiverID;
    public string TargetAgentId;
    public string TargetTeamId;
    public string SenderTeamId;
    public MessageType Type;
    public int Priority;
    public float Timestamp;
    public string Content;
    public CommunicationScope Scope;
    public string ScenarioId;
    public string MissionId;
    public string PayloadType;
    public bool Reliable;
}

[System.Serializable]
public class ActionCommand
{
    public PrimitiveActionType ActionType;
    public Vector3 TargetPosition;
    public Quaternion TargetRotation;
    public GameObject TargetObject;
    public string Parameters;

    public ActionCommand(
        PrimitiveActionType actionType = PrimitiveActionType.Idle,
        Vector3 targetPosition = default,
        Quaternion targetRotation = default,
        GameObject targetObject = null,
        string parameters = null)
    {
        ActionType = actionType;
        TargetPosition = targetPosition;
        TargetRotation = targetRotation;
        TargetObject = targetObject;
        Parameters = parameters;
    }
}
