using System;
using UnityEngine;

/// <summary>
/// 当前 step 的语义意图类型。
/// 该枚举用于告诉系统“这一步本质上想完成什么”，而不是直接描述底层执行动作。
/// </summary>
public enum StepIntentType
{
    Unknown = 0,     // 未知意图，通常表示 LLM 未给出可靠结构化结果
    Navigate = 1,    // 前往某个地点、坐标、节点或队友
    Observe = 2,     // 观察、扫描、定位、确认目标
    Communicate = 3, // 汇报、同步、请求帮助、广播状态
    Interact = 4,    // 拾取、投放、交互、操作装置
    Follow = 5,      // 跟随某个队友或动态目标
    Support = 6,     // 支援、掩护、协助其他智能体
    Escort = 7       // 护送、伴随移动、协同通行
}

/// <summary>
/// 粗路径或最终接近阶段的接近侧偏好。
/// 该枚举不直接生成几何路径，而是作为系统全局/局部规划的代价偏好输入。
/// </summary>
public enum RouteApproachSide
{
    Any = 0,         // 不限制接近侧
    North = 1,       // 尽量从目标北侧接近
    South = 2,       // 尽量从目标南侧接近
    East = 3,        // 尽量从目标东侧接近
    West = 4,        // 尽量从目标西侧接近
    LeftOfPath = 5,  // 尽量位于粗路径左侧通过
    RightOfPath = 6  // 尽量位于粗路径右侧通过
}

/// <summary>
/// 高度策略。
/// 用于告诉系统路径/控制层当前 step 更偏好什么样的飞行高度风格。
/// </summary>
public enum RouteAltitudeMode
{
    Default = 0,         // 使用系统默认高度策略
    KeepCurrent = 1,     // 尽量维持当前高度
    Low = 2,             // 偏低高度通过
    Medium = 3,          // 中等高度通过
    High = 4,            // 偏高高度通过
    HighThenDescend = 5  // 先高后低，接近目标时再下降
}

/// <summary>
/// 障碍清距偏好。
/// 系统局部规划器可根据该偏好调整障碍代价权重和轨迹安全边际。
/// </summary>
public enum RouteClearancePreference
{
    Low = 0,     // 允许更贴近障碍，通过效率优先
    Medium = 1,  // 默认安全边际
    High = 2     // 明显拉大与障碍/人群/车辆的距离
}

/// <summary>
/// 受阻后的默认处理策略。
/// 该枚举用于说明当系统局部规划连续失败时，上层应采取什么战术动作。
/// </summary>
public enum BlockedPolicyType
{
    Replan = 0,          // 直接请求系统重新规划
    Wait = 1,            // 原地等待，观察局面变化
    ReportAndReplan = 2, // 先上报协调者/队友，再重规划
    HoldPosition = 3,    // 保持当前位置，不主动推进
    RequestSupport = 4   // 请求队友协助或调整协同路径
}

/// <summary>
/// 队伍级协同模式。
/// 用于描述当前 step 更接近独立行动、松耦合协同，还是紧耦合同步。
/// </summary>
public enum TeamCoordinationMode
{
    Independent = 0,    // 独立执行，本智能体按自身路径推进
    LooseSync = 1,      // 松耦合同步，只需大致保持进度一致
    TightSync = 2,      // 紧耦合同步，需要等待队友或共享节奏
    LeaderFollower = 3, // 领航/跟随模式
    CorridorReserve = 4 // 需要对通行走廊进行预留和让行
}

/// <summary>
/// 单个 step 的结构化意图。
/// 该结构由 LLM 或规划模块生成，描述“这一步到底想做什么、目标是谁、是否要求经过点”。
/// </summary>
[Serializable]
public class StepIntentDefinition
{
    public string stepText;                 // 原始 step 文本，便于调试和回溯
    public StepIntentType intentType;       // 结构化语义意图类型
    public string primaryTarget;            // 主目标，可为 building:名称 / feature:名称 / world(...) / 节点名 / AgentID
    public string[] orderedViaTargets;      // 有序经过点列表，系统会按顺序分段规划粗路径
    public string[] avoidTargets;           // 需要规避的地点/区域名称列表
    public string[] preferTargets;          // 需要优先靠近或优先穿越的地点/区域名称列表
    public string[] requestedTeammateIds;   // 本 step 希望协同或等待的队友 AgentID 列表
    public string observationFocus;         // 若该 step 包含观察语义，希望优先观察的对象或内容
    public string communicationGoal;        // 若该 step 包含通信语义，希望发送/同步的核心信息
    public string finalBehavior;            // 到达目标后的终端行为，例如 observe/orbit/hover/report
    public string completionCondition;      // 本 step 的完成条件说明，例如“到达building_2并完成一次扫描”
    public string notes;                    // LLM 给出的补充说明，供日志与调试使用
}

/// <summary>
/// 单个 step 的结构化路径策略。
/// 该结构描述“怎么去”而不是“去哪里”，供系统路径规划和局部规划器使用。
/// </summary>
[Serializable]
public class RoutePolicyDefinition
{
    public RouteApproachSide approachSide;            // 希望从目标哪一侧接近
    public RouteAltitudeMode altitudeMode;            // 高度偏好或高度切换策略
    public RouteClearancePreference clearance;        // 对静态/动态障碍的安全边际偏好
    public SmallNodeType[] avoidNodeTypes;            // 需要额外规避的小节点类型，例如 Vehicle/Pedestrian/TemporaryObstacle
    public string[] avoidFeatureNames;                // 需要提升代价或绕开的地点/区域名称
    public string[] preferFeatureNames;               // 需要优先贴近或优先经过的地点/区域名称
    public bool keepTargetVisible;                    // 局部接近时是否优先保持目标在视野中
    public bool preferOpenSpace;                      // 是否更偏好开阔空间而非贴边/贴建筑通过
    public bool allowGlobalAStar;                     // 是否允许系统为该 step 生成全局 A* 粗路径
    public bool allowLocalDetour;                     // 是否允许局部规划为避障而临时绕行
    public bool slowNearTarget;                       // 接近目标末段是否主动降速
    public bool holdForTeammates;                     // 是否需要为队友让行或等待协同通过
    public BlockedPolicyType blockedPolicy;           // 连续受阻时的上层处理策略
    public int maxTeammatesInCorridor;                // 希望同一走廊内同时存在的队友上限，0 表示不限制
    public string notes;                              // 路径策略补充说明，供日志与调试使用
}

/// <summary>
/// 单个角色或单个 step 的协同指令。
/// 该结构给出队伍层面的同步、让行、领航/跟随、走廊预留等约束。
/// </summary>
[Serializable]
public class TeamCoordinationDirective
{
    public TeamCoordinationMode coordinationMode; // 当前 step 或当前角色采用的协同模式
    public string leaderAgentId;                  // 若是跟随模式，指出领航智能体 AgentID
    public string sharedTarget;                   // 多智能体共享关注的目标或汇合点
    public string corridorReservationKey;         // 共享走廊/瓶颈的预留键，用于协同让行
    public string[] yieldToAgentIds;              // 本智能体需要主动让行的队友 AgentID 列表
    public string[] syncPointTargets;             // 需要在这些地点/目标附近做同步的锚点列表
    public string formationSlot;                  // 编队槽位或编队侧，例如 left/right/rear
    public string notes;                          // 协同说明，供调试和通信日志使用
}

/// <summary>
/// 任务中的“具体子任务槽位”。
/// 与 MissionRole 不同，MissionRole 表示能力类别；MissionTaskSlot 表示这次任务里一个可被分配给单个智能体的具体岗位。
/// 例如同样都是 Scout，也可以拆成 EastScout 和 WestScout 两个不同槽位。
/// </summary>
[Serializable]
public class MissionTaskSlot
{
    public string slotId;                          // 槽位唯一 ID，例如 slot_east_scout_1
    public string slotLabel;                       // 槽位显示名称，例如 EastScout / WestScout
    public RoleType roleType;                      // 该槽位需要的角色类别
    public AgentType requiredAgentType;            // 该槽位偏好的平台类型
    public string target;                          // 槽位主要目标，例如 building:building_2
    public string[] viaTargets;                    // 槽位经过点列表，允许分段粗路径
    public RouteApproachSide approachSide;         // 该槽位要求的接近侧
    public RouteAltitudeMode altitudeMode;         // 该槽位偏好的高度策略
    public string syncGroup;                       // 与其他槽位共享的同步组，例如 building_2_recon
    public string[] dependsOnSlotIds;              // 依赖槽位列表；非空时表示需要等待这些前置槽位完成后再放行
    public string finalBehavior;                   // 到达目标后的终端行为，例如 observe/orbit/report
    public string completionCondition;             // 槽位完成条件，供协调者做聚合判断
    public string notes;                           // 槽位说明，便于日志和调试
}

/// <summary>
/// 任务公告消息载荷。
/// 协调者向全队广播任务时使用该结构，避免直接传裸字符串。
/// </summary>
[Serializable]
public class TaskAnnouncementPayload
{
    public MissionAssignment mission;                    // 结构化任务分配主体
    public TeamCoordinationDirective[] missionDirectives; // 整个任务级别的协同指令
    public string briefing;                              // 面向全队的简要任务说明
}

/// <summary>
/// 角色偏好上报消息载荷。
/// 普通智能体向协调者声明自身更适合哪些角色、具备哪些关键能力时使用。
/// </summary>
[Serializable]
public class RolePreferencePayload
{
    public string missionId;           // 当前响应的任务 ID
    public string agentId;             // 上报偏好的智能体 ID
    public RoleType[] preferences;     // 角色偏好列表，按优先级从高到低排列
    public AgentType agentType;        // 智能体平台类型，例如 Quadcopter / WheeledRobot
    public RoleType currentRole;       // 智能体当前已有角色，便于协调者做兼容分配
    public float maxSpeed;             // 最大速度，用于协调者衡量机动能力
    public float perceptionRange;      // 感知范围，用于协调者衡量侦查能力
    public string capabilitySummary;   // 简短能力摘要，供调试和日志使用
}

/// <summary>
/// 角色裁决消息载荷。
/// 协调者在收齐偏好后，将最终角色和附加协同约束发回给各个智能体。
/// </summary>
[Serializable]
public class RoleDecisionPayload
{
    public string missionId;                        // 当前任务 ID
    public string agentId;                          // 被分配角色的目标智能体 ID
    public RoleType assignedRole;                   // 协调者最终裁定的角色
    public MissionTaskSlot assignedSlot;            // 协调者分配给该智能体的具体子任务槽位
    public string assignmentReason;                 // 角色分配理由，便于调试和解释
    public TeamCoordinationDirective directive;     // 兼容旧链路保留的单条协同指令，默认取 directives[0]
    public TeamCoordinationDirective[] directives;  // 当前智能体需要遵守的完整协同指令集合
}

/// <summary>
/// 角色接受回执消息载荷。
/// 智能体在接受最终角色并生成本地计划后，向协调者回发确认。
/// </summary>
[Serializable]
public class RoleAcceptancePayload
{
    public string missionId;          // 当前任务 ID
    public string agentId;            // 发送确认的智能体 ID
    public RoleType acceptedRole;     // 智能体接受的角色
    public string acceptedSlotId;     // 智能体接受的具体子任务槽位 ID
    public AgentType agentType;       // 智能体平台类型
    public string reasoning;          // 智能体接受该角色的简单理由
    public string capabilitySummary;  // 智能体能力摘要，便于协调者留档
}

/// <summary>
/// 任务进度消息载荷。
/// 智能体完成 step、任务或发生阻塞时，用该结构上报当前推进情况。
/// </summary>
[Serializable]
public class TaskProgressPayload
{
    public string missionId;            // 当前任务 ID
    public string missionDescription;   // 当前任务描述，便于日志直接阅读
    public string agentId;              // 上报进度的智能体 ID
    public RoleType role;               // 上报者在任务中的角色
    public string slotId;               // 上报者当前执行的具体子任务槽位 ID
    public string completedStep;        // 刚刚完成的 step 文本；任务完成时可为空
    public string nextStep;             // 下一步即将执行的 step 文本；任务结束时可为 none
    public int completedStepIndex;      // 已完成 step 的索引
    public int totalStepCount;          // 任务总 step 数量
    public string status;               // 进度状态，例如 step_completed / mission_completed / blocked
    public string coordinationNote;     // 与多智能体协同相关的补充说明
}
