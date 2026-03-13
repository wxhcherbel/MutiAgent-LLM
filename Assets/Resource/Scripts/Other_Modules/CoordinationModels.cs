using System;      // 提供 [Serializable]、基础时间/字符串/数组类型等通用能力。
using UnityEngine; // 提供 Vector3、MonoBehaviour 生态下常用的数学与序列化类型。

// -----------------------------------------------------------------------------
// 第一部分：步骤语义、路径策略、协同约束。
// 这一组类型是 PlanningModule 和 ActionDecisionModule 之间最核心的共享协议。
// 它们回答的是：
// 1) 这一步想干什么；
// 2) 路怎么走；
// 3) 和队友怎么配合。
// -----------------------------------------------------------------------------

/// <summary>
/// 当前步骤的高层语义意图。
/// 它回答“这一步本质上想完成什么”，而不是“底层飞控怎么执行”。
/// </summary>
public enum StepIntentType
{
    Unknown = 0,     // 未知意图；通常表示上游没有给出可靠结构化结果。
    Navigate = 1,    // 导航到某个地点、对象、坐标、区域入口或队友附近。
    Observe = 2,     // 观察、扫描、侦察、确认或持续监视某个目标。
    Communicate = 3, // 广播、汇报、同步、请求帮助或传回状态。
    Interact = 4,    // 与对象交互，例如拾取、投放、装卸、修复、操作装置。
    Follow = 5,      // 跟随某个队友或动态对象推进。
    Support = 6,     // 为队友提供支援、掩护、协助。
    Escort = 7       // 对目标或队友进行护送、伴随通行。
}

/// <summary>
/// 步骤级高度策略。
/// 它是“怎么走”的一部分，不是最终的连续高度值。
/// </summary>
public enum RouteAltitudeMode
{
    Default = 0,        // 使用系统默认高度策略，由系统结合环境和任务决定。
    KeepCurrent = 1,    // 尽量维持当前高度，减少不必要的高度切换。
    Low = 2,            // 优先低空层推进。
    Medium = 3,         // 优先中空层推进。
    High = 4,           // 优先高空层推进。
    HighThenDescend = 5 // 先高空穿越，再在末段下降接近目标。
}

/// <summary>
/// 障碍清距偏好。
/// 它告诉系统局部规划器应该更激进还是更保守。
/// </summary>
public enum RouteClearancePreference
{
    Low = 0,    // 允许更贴近障碍，效率优先。
    Medium = 1, // 默认清距。
    High = 2    // 明显拉大与障碍、车辆、人群的距离。
}

/// <summary>
/// 当路径推进受阻时的默认处理策略。
/// 这是上层战术语义，不是底层运动控制。
/// </summary>
public enum BlockedPolicyType
{
    Replan = 0,          // 直接请求重规划。
    Wait = 1,            // 原地等待，观察局面变化。
    ReportAndReplan = 2, // 先上报，再重规划。
    HoldPosition = 3,    // 保持当前位置，不主动推进。
    RequestSupport = 4   // 请求队友协助、让行或调整协同策略。
}

/// <summary>
/// 队伍级协同模式。
/// 它只描述“协同关系是什么”，不描述具体动作细节。
/// </summary>
public enum TeamCoordinationMode
{
    Independent = 0,   // 独立执行；本智能体按自己的局部计划推进。
    LooseSync = 1,     // 松耦合同步；大体保持节奏一致即可。
    TightSync = 2,     // 紧耦合同步；通常意味着需要等待同步点或 barrier。
    LeaderFollower = 3,// 领航/跟随模式。
    CorridorReserve = 4// 对瓶颈、走廊、通道进行预留和让行。
}

// -----------------------------------------------------------------------------
// 第二部分：最小团队执行状态。
// 这一层不负责任务拆解，而是负责把“当前队伍已经知道什么、占了什么、完成到哪一步”
// 以最小共享状态暴露给 PlanningModule 和 ActionDecisionModule。
// -----------------------------------------------------------------------------

/// <summary>
/// 离散高度层。
/// 这是动作层在离散网格里表达高度偏好的基础枚举。
/// </summary>
public enum GridAltitudeLayer
{
    Ground = 0, // 地面或最低层。
    Low = 1,    // 低空层。
    Medium = 2, // 中空层。
    High = 3    // 高空层。
}

/// <summary>
/// 队伍共享占用声明。
/// 竞争、对抗、混合任务里，最常见的问题是“己方两人重复抢同一目标”。
/// 这个对象只回答：谁当前声明占用/负责了什么。
/// </summary>
[Serializable]
public class TargetClaim
{
    public string claimKind;            // 占用类型，例如 resource / checkpoint / target / corridor。
    public string claimKey;             // 被占用对象的稳定键。
    public string ownerAgentId;         // 当前占用该对象的智能体 ID。
    public string slotId;               // 若这次占用来自具体槽位，这里记录槽位 ID。
    public long expiresAtUnixSeconds;   // 占用失效时间；0 表示本轮任务内持续有效。
}

/// <summary>
/// 队伍共享事实。
/// 它保存“队伍已经确认知道的事”，例如敌机位置、资源状态、伤员位置、目标已失效等。
/// </summary>
[Serializable]
public class SharedFact
{
    public string factType;              // 事实类型，例如 enemy_seen / resource_claimed / victim_found / target_blocked。
    public string subjectId;             // 事实主体的稳定 ID；没有时可留空。
    public string subjectText;           // 面向日志和 prompt 的主体文本。
    public string subjectKind;           // 主体类型，例如 Agent / SmallNode / Feature / Area / Resource。
    public GridCellCandidate lastKnownCell; // 若主体能落到网格，这里保存最近格。
    public Vector3 worldPosition;        // 若主体有世界坐标，这里保存最近世界坐标。
    public float confidence;             // 该事实当前置信度。
    public string sourceAgentId;         // 最先或最近上报该事实的智能体。
    public string notes;                 // 调试说明。
}

/// <summary>
/// 团队执行状态。
/// 这是当前主路径最小共享状态，不承担长期记忆或自治目标语义。
/// </summary>
[Serializable]
public class TeamExecutionState
{
    public string missionId;              // 当前团队任务 ID。
    public string currentPhaseId;         // 当前正在推进的阶段 ID。
    public string[] releasedSlotIds;      // 已被协调者放行执行的槽位集合。
    public string[] completedSlotIds;     // 已确认完成的槽位集合。
    public string[] readyAgentIds;        // 已到位/已就绪的智能体集合。
    public TargetClaim[] claims;          // 当前仍有效的目标/资源/通道声明。
    public SharedFact[] sharedFacts;      // 当前队伍共享事实。
}

// -----------------------------------------------------------------------------
// 第三部分：结构化目标与步骤级桥接层。
// 这一层仍然很重要，因为系统还需要在“自然语言目标”和“离散候选/世界对象”之间做稳定传递。
// -----------------------------------------------------------------------------

/// <summary>
/// 结构化目标的抽象类型。
/// 它描述“这个目标在世界里是什么”，而不是地图里的具体名字。
/// </summary>
public enum StructuredTargetMode
{
    Unknown = 0,         // 未知目标类型。
    Entity = 1,          // 单个实体，例如某栋楼、某个队友、某个小节点。
    Area = 2,            // 一片区域。
    Collection = 3,      // 一组目标。
    Agent = 4,           // 明确是智能体目标。
    Self = 5,            // 当前自己。
    WorldPoint = 6,      // 世界点或显式坐标点。
    DynamicSelector = 7  // 动态筛选目标，例如“最近的行人”。
}

/// <summary>
/// 结构化目标的数量语义。
/// 它回答“这是单体、全集还是子集”。
/// </summary>
public enum StructuredTargetCardinality
{
    Unspecified = 0, // 未明确数量语义。
    One = 1,         // 单个目标。
    All = 2,         // 全部目标。
    Subset = 3       // 部分目标。
}

/// <summary>
/// 结构化目标引用。
/// 它是跨模块传递目标语义时的正式对象。
/// </summary>
[Serializable]
public class StructuredTargetReference
{
    public StructuredTargetMode mode;               // 目标形态，例如实体、区域、集合或动态筛选。
    public StructuredTargetCardinality cardinality; // 单体、全集或子集。
    public string rawText;                          // 原始自然语言描述；方便回溯上游到底说了什么。
    public string executableQuery;                  // 兼容旧 grounding 链保留的查询锚点；新主路径不要求 LLM 填写。
    public string entityId;                         // 兼容旧 grounding 链保留的稳定实体 ID；新主路径不要求 LLM 填写。
    public string entityClass;                      // 兼容旧执行链保留的实体类别。
    public string displayName;                      // 面向提示词与日志的人类可读名称。
    public string selectorText;                     // 集合或动态筛选语义，例如“所有 building”“最近的行人”。
    public string collectionKey;                    // 兼容旧集合 grounding 保留的集合键。
    public string[] memberEntityIds;                // 兼容旧集合覆盖链保留的成员 ID 列表。
    public string areaHint;                         // 兼容旧区域 grounding 保留的区域提示。
    public string relation;                         // 关系词，例如“南侧”“附近”“最近”。
    public string anchorText;                       // 兼容旧执行链保留的接近锚点。
    public int anchorBiasX;                         // 兼容旧执行链保留的平面偏置 X。
    public int anchorBiasZ;                         // 兼容旧执行链保留的平面偏置 Z。
    public bool isDynamic;                          // 兼容旧动态目标链保留的标记。
    public string notes;                            // 调试说明或兼容注释。
}

/// <summary>
/// 单个步骤的结构化语义意图。
/// 这是旧兼容执行链和新简化 `PlanStepDefinition` 之间的重要桥接层。
/// </summary>
[Serializable]
public class StepIntentDefinition
{
    public string stepText;                              // 原始步骤文本。
    public StepIntentType intentType;                    // 结构化意图类型。
    public string primaryTarget;                         // 兼容旧链路保留的主目标字符串。
    public StructuredTargetReference primaryTargetRef;   // 主目标的结构化引用。
    public string[] orderedViaTargets;                   // 兼容旧链路保留的经过点字符串列表。
    public StructuredTargetReference[] orderedViaTargetRefs; // 经过点的结构化目标引用列表。
    public string[] requestedTeammateIds;                // 本步骤想要协作、等待或呼叫的队友列表。
    public string observationFocus;                      // 若包含观察语义，这里写观察重点。
    public string communicationGoal;                     // 若包含通信语义，这里写通信目标。
    public string finalBehavior;                         // 到达后的终端行为，例如 observe / orbit / report / hold。
    public string completionCondition;                   // 本步骤怎样算完成。
}

/// <summary>
/// 单个步骤的路径策略。
/// 它只回答“怎么去”，不回答“去哪里”。
/// </summary>
[Serializable]
public class RoutePolicyDefinition
{
    public RouteAltitudeMode altitudeMode;         // 高度偏好。
    public RouteClearancePreference clearance;     // 清距偏好。
    public SmallNodeType[] avoidNodeTypes;         // 需要额外规避的小节点类型。
    public string[] avoidFeatureNames;             // 需要绕开的地点或区域名称。
    public bool allowGlobalAStar;                  // 是否允许系统做全局 A* 粗路径。
    public bool allowLocalDetour;                  // 是否允许局部避障时临时绕行。
    public BlockedPolicyType blockedPolicy;        // 连续受阻时的默认上层策略。
}

/// <summary>
/// 队伍级协同指令。
/// 它描述本步骤或本角色与队友之间的同步、让行、跟随或共享目标关系。
/// </summary>
[Serializable]
public class TeamCoordinationDirective
{
    public TeamCoordinationMode coordinationMode;   // 当前采用的协同模式。
    public string leaderAgentId;                    // 若是领航/跟随模式，这里记录领航者。
    public string sharedTarget;                     // 兼容旧链路保留的共享目标字符串。
    public StructuredTargetReference sharedTargetRef; // 共享目标的正式结构化引用。
    public string corridorReservationKey;           // 通道/瓶颈资源的预留键。
    public string[] yieldToAgentIds;                // 需要主动让行的队友列表。
    public string[] syncPointTargets;               // 需要在这些点附近做同步的锚点。
    public string formationSlot;                    // 编队槽位，例如 left/right/rear。
}

// -----------------------------------------------------------------------------
// 第四部分：当前主路径的数据模型。
// 这部分最贴近我们已经对齐的“简化版多智能体协同任务 + 两次LLM决策”方案。
// -----------------------------------------------------------------------------

/// <summary>
/// 第一阶段团队语义里的角色需求。
/// 它只描述“要什么角色、多少个、职责是什么”，不手写每个智能体的完整动作。
/// </summary>
[Serializable]
public class MissionSemanticRoleRequirement
{
    public string role;                // 角色名，例如 Scout / Supporter / Transporter。
    public int count;                  // 建议人数。
    public string responsibility;      // 角色职责摘要。
    public string targetText;          // 该角色共同面向的自然语言目标。
    public string targetKindHint;      // 目标弱提示，例如 Entity / Area / Collection / WorldPoint。
    public string[] viaTargets;        // 常见中间检查点。
    public string completionCondition; // 该角色怎样算完成职责。
    public string[] phaseIds;          // 该角色参与的阶段。
}

/// <summary>
/// 第一阶段团队语义里的阶段模板。
/// 这里只保留阶段 ID、目标与依赖关系；具体展开由代码完成。
/// </summary>
[Serializable]
public class MissionSemanticPhaseTemplate
{
    public string phaseId;     // 阶段 ID。
    public string objective;   // 阶段目标。
    public string[] dependsOn; // 前置阶段 ID 列表。
}

/// <summary>
/// 第一阶段团队语义里的协同规则。
/// 只表达触发条件和协同效果，不提前展开到底层动作。
/// </summary>
[Serializable]
public class MissionSemanticCoordinationRule
{
    public string ruleType;       // 规则类型，例如 BroadcastOnEvent / Barrier / Reservation。
    public string trigger;        // 触发事件或触发条件。
    public string effect;         // 触发后的团队行为。
    public string sharedTarget;   // 这条规则关联的共享目标。
    public string[] participants; // 规则参与者；可写角色或 agentId。
    public string notes;          // 调试说明。
}

/// <summary>
/// 第一阶段团队语义骨架。
/// 这是 PlanningModule 第一阶段期望 LLM 输出的核心对象。
/// </summary>
[Serializable]
public class MissionSemanticResponse
{
    public string missionType;                                  // 任务类型。
    public string relationshipType;                             // 队伍关系类型，例如 Cooperation / Competition / Adversarial / Mixed。
    public string teamObjective;                                // 全队一句话总目标。
    public string successCondition;                             // 团队成功条件。
    public string failureCondition;                             // 团队失败条件。
    public MissionSemanticRoleRequirement[] roleRequirements;   // 角色需求列表。
    public MissionSemanticPhaseTemplate[] phaseTemplates;       // 阶段模板列表。
    public MissionSemanticCoordinationRule[] coordinationRules; // 协同规则列表。
}

/// <summary>
/// 简化后的本地步骤定义。
/// 它是当前主路径里 PlanningModule 交给 ActionDecisionModule 的最小语义输入。
/// </summary>
[Serializable]
public class PlanStepDefinition
{
    public string stepId;              // 步骤唯一 ID。
    public string text;                // 当前步骤自然语言描述。
    public string targetText;          // 当前步骤的自然语言目标。
    public string targetKindHint;      // 目标弱提示。
    public string relationHint;        // 模糊关系提示，例如南侧、附近、外围。
    public string[] viaTargets;        // 中间检查点。
    public string completionCondition; // 本步完成条件。
}

/// <summary>
/// 单个离散网格格子候选。
/// 单独封装是为了让两次 LLM 的输入输出结构更稳定。
/// </summary>
[Serializable]
public class GridCellCandidate
{
    public int x; // 网格 X。
    public int z; // 网格 Z。
}

/// <summary>
/// 大节点、区域或集合成员候选。
/// 当前主路径里，大节点通常以“边界格集合”进入第一次 LLM。
/// </summary>
[Serializable]
public class FeatureTargetCandidate
{
    public string candidateId;            // 候选 ID，例如 feature:building_01。
    public string displayName;            // 人类可读名称。
    public string sourceKind;             // 候选来源类型，例如 Feature / Area / CollectionMember。
    public GridCellCandidate[] candidateCells; // 候选可执行格集合；大节点通常填外圈格，区域通常填区域格。
    public string notes;                  // 调试说明。

    public GridCellCandidate[] boundaryCells // 兼容旧代码保留；语义等同于 candidateCells。
    {
        get => candidateCells;
        set => candidateCells = value;
    }
}

/// <summary>
/// 小节点、队友、显式坐标等对象候选。
/// 这类候选通常只有一个最近可执行格。
/// </summary>
[Serializable]
public class ObjectTargetCandidate
{
    public string candidateId;          // 候选 ID，例如 node:car_01 / agent:uav_02 / grid:12_34。
    public string displayName;          // 展示名称。
    public string sourceKind;           // 候选来源类型，例如 SmallNode / Agent / Grid / World。
    public Vector3 worldPosition;       // 世界坐标。
    public GridCellCandidate nearestCell; // 最近可执行格。
    public string notes;                // 调试说明。
}

/// <summary>
/// 第一次 LLM 的输入候选包。
/// 系统只负责给候选，不负责替 LLM 选最终格子。
/// </summary>
[Serializable]
public class TargetCandidateBundle
{
    public string queryText;                     // 当前步骤原文。
    public string targetText;                    // 当前目标原文。
    public string candidateType;                 // 当前候选主类型摘要。
    public FeatureTargetCandidate[] featureCandidates; // 大节点、区域、集合成员候选。
    public ObjectTargetCandidate[] objectCandidates;   // 小节点、队友、显式坐标候选。
    public string notes;                         // 调试说明。
}

/// <summary>
/// 第一次 LLM 的目标选择结果。
/// 它只回答“选哪个候选、最终格是哪一格、要不要带中间格”。
/// </summary>
[Serializable]
public class TargetSelectionResponse
{
    public string selectedCandidateId;        // 选中的候选 ID。
    public GridCellCandidate goalCell;        // 最终目标格。
    public GridCellCandidate[] intermediateCells; // 可选中间格。
    public string reason;                     // 选择原因。
}

/// <summary>
/// 任务中的具体岗位槽位。
/// 它仍然从属于同一个团队任务，不是独立任务。
/// </summary>
[Serializable]
public class MissionTaskSlot
{
    public string slotId;                         // 槽位唯一 ID。
    public string slotLabel;                      // 槽位显示名。
    public RoleType roleType;                     // 该槽位需要的角色。
    public AgentType requiredAgentType;           // 该槽位偏好的平台类型。
    public string targetText;                     // 当前岗位最终要作用的自然语言目标。
    public StructuredTargetReference targetRef;   // 主目标的正式结构化引用。
    public string[] viaTargets;                   // 中间检查点字符串列表。
    public string syncGroup;                      // 同步组 ID。
    public string[] dependsOnSlotIds;             // 前置槽位依赖。
    public string localCompletionCondition;       // 当前岗位的本地完成条件。
    public string notes;                          // 调试说明。

    public StructuredTargetReference[] viaTargetRefs; // 兼容旧执行链保留的经过点结构化引用。
    public RouteAltitudeMode altitudeMode;            // 兼容旧执行链保留的高度偏好。
    public string finalBehavior;                      // 兼容旧执行链保留的终端行为。

    public string target // 兼容旧代码保留；语义等同于 targetText。
    {
        get => targetText;
        set => targetText = value;
    }

    public string completionCondition // 兼容旧代码保留；语义等同于 localCompletionCondition。
    {
        get => localCompletionCondition;
        set => localCompletionCondition = value;
    }
}

// -----------------------------------------------------------------------------
// 第五部分：任务广播与协同通信载荷。
// 这些结构是协调者与普通智能体之间交换“任务、角色、进度”的正式消息体。
// -----------------------------------------------------------------------------

/// <summary>
/// 任务公告消息载荷。
/// 协调者向全队广播任务时使用。
/// </summary>
[Serializable]
public class TaskAnnouncementPayload
{
    public MissionAssignment mission;                     // 团队任务主体。
    public TeamCoordinationDirective[] missionDirectives; // 整个任务级别的协同约束。
    public string briefing;                               // 面向全队的简短说明。
}

/// <summary>
/// 角色偏好上报消息载荷。
/// 普通智能体向协调者上报自己更适合哪些角色。
/// </summary>
[Serializable]
public class RolePreferencePayload
{
    public string missionId;         // 当前响应的任务 ID。
    public string agentId;           // 上报者 AgentID。
    public RoleType[] preferences;   // 角色偏好列表；按优先级排序。
    public AgentType agentType;      // 平台类型。
    public RoleType currentRole;     // 当前已有角色。
    public float maxSpeed;           // 最大速度。
    public float perceptionRange;    // 感知范围。
    public string capabilitySummary; // 简短能力摘要。
}

/// <summary>
/// 角色裁决消息载荷。
/// 协调者把最终分配结果发回给各个智能体时使用。
/// </summary>
[Serializable]
public class RoleDecisionPayload
{
    public string missionId;                       // 当前任务 ID。
    public string agentId;                         // 被分配角色的目标智能体 ID。
    public RoleType assignedRole;                  // 最终角色。
    public MissionTaskSlot assignedSlot;           // 最终岗位槽位。
    public string assignmentReason;                // 分配理由。
    public TeamCoordinationDirective directive;    // 兼容旧链路保留的单条协同指令；默认可取 directives[0]。
    public TeamCoordinationDirective[] directives; // 当前智能体需要遵守的完整协同指令集合。
}

/// <summary>
/// 角色接受回执消息载荷。
/// 智能体接受角色并生成本地计划后回发给协调者。
/// </summary>
[Serializable]
public class RoleAcceptancePayload
{
    public string missionId;         // 当前任务 ID。
    public string agentId;           // 发送确认的智能体 ID。
    public RoleType acceptedRole;    // 接受的角色。
    public string acceptedSlotId;    // 接受的槽位 ID。
    public AgentType agentType;      // 平台类型。
    public string reasoning;         // 接受理由。
    public string capabilitySummary; // 能力摘要。
}

/// <summary>
/// 任务进度消息载荷。
/// 智能体完成步骤、任务或遇到阻塞时上报当前推进状态。
/// </summary>
[Serializable]
public class TaskProgressPayload
{
    public string missionId;          // 当前任务 ID。
    public string missionDescription; // 当前任务描述。
    public string agentId;            // 上报者 AgentID。
    public RoleType role;             // 上报者当前角色。
    public string slotId;             // 上报者当前岗位槽位 ID。
    public string completedStep;      // 刚刚完成的步骤文本。
    public string nextStep;           // 下一步文本。
    public int completedStepIndex;    // 已完成步骤索引。
    public int totalStepCount;        // 总步骤数。
    public string status;             // 状态，例如 step_completed / mission_completed / blocked。
    public string coordinationNote;   // 协同相关补充说明。
}
