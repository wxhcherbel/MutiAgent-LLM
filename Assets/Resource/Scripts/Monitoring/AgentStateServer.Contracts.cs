using System;

/// <summary>
/// 单个智能体的仪表板快照。
/// </summary>
[Serializable]
public class AgentStateSnapshot
{
    /// <summary>智能体 ID。</summary>
    public string agentId;

    /// <summary>平台类型文本。</summary>
    public string type;

    /// <summary>角色文本。</summary>
    public string role;

    /// <summary>队伍编号。</summary>
    public int teamId;

    /// <summary>世界坐标数组，格式为 [x, y, z]。</summary>
    public float[] position;

    /// <summary>当前电量。</summary>
    public float battery;

    /// <summary>ADM 状态文本。</summary>
    public string admStatus;

    /// <summary>Planning 状态文本。</summary>
    public string planState;

    /// <summary>当前动作类型文本。</summary>
    public string currentAction;

    /// <summary>当前动作目标文本。</summary>
    public string currentTarget;

    /// <summary>当前任务描述。</summary>
    public string missionDesc;

    /// <summary>当前步骤文本。</summary>
    public string currentStep;

    /// <summary>最近感知事件摘要。</summary>
    public string[] recentEvents;

    /// <summary>附近友方智能体 ID 列表。</summary>
    public string[] nearbyAgentIds;

    /// <summary>附近敌方智能体 ID 列表。</summary>
    public string[] enemyAgentIds;

    /// <summary>感知半径。</summary>
    public float perceptionRange;

    /// <summary>快照采样时间。</summary>
    public float timestamp;

    // ── ctx 字段（ActionExecutionContext 全部字段）────────────────────────────

    /// <summary>当前任务 ID。</summary>
    public string msnId;

    /// <summary>当前步骤 ID。</summary>
    public string stepId;

    /// <summary>当前步骤文本。</summary>
    public string stepText;

    /// <summary>步骤关联的结构化约束完整快照列表。</summary>
    public StructuredConstraintSnapshot[] stepConstraints;

    /// <summary>ctx 内的 ADM 状态。</summary>
    public string ctxStatus;

    /// <summary>当前滚动迭代次数。</summary>
    public int iterationCount;

    /// <summary>当前位置名称。</summary>
    public string currentLocationName;

    /// <summary>已执行动作摘要列表。</summary>
    public string[] executedActions;

    /// <summary>完整动作队列（type 名列表）。</summary>
    public string[] actionQueue;

    /// <summary>当前执行的动作下标。</summary>
    public int currentActionIdx;

    /// <summary>是否处于滚动规划模式。</summary>
    public bool isRollingMode;

    /// <summary>是否为破坏型 agent（PersonalitySystem.IsAdversarial）。</summary>
    public bool isAdversarial;
}

/// <summary>
/// 仪表板使用的地图元数据。
/// </summary>
[Serializable]
public class MapMetadata
{
    /// <summary>地图原点 X 坐标。</summary>
    public float originX;

    /// <summary>地图原点 Z 坐标。</summary>
    public float originZ;

    /// <summary>网格单元边长。</summary>
    public float cellSize;

    /// <summary>网格宽度。</summary>
    public int gridWidth;

    /// <summary>网格长度。</summary>
    public int gridLength;

    /// <summary>地图要素点位列表。</summary>
    public FeaturePoint[] features;
}

/// <summary>
/// 仪表板展示的地图要素抽样点。
/// </summary>
[Serializable]
public class FeaturePoint
{
    /// <summary>要素名称。</summary>
    public string name;

    /// <summary>要素类型文本。</summary>
    public string kind;

    /// <summary>要素中心点 X 坐标。</summary>
    public float x;

    /// <summary>要素中心点 Z 坐标。</summary>
    public float z;

    /// <summary>要素估计半径。</summary>
    public float radius;
}

/// <summary>
/// 通信日志条目。
/// </summary>
[Serializable]
public class MessageLogEntry
{
    /// <summary>发送者 ID。</summary>
    public string sender;

    /// <summary>接收者 ID。</summary>
    public string receiver;

    /// <summary>消息类型文本。</summary>
    public string type;

    /// <summary>消息时间戳。</summary>
    public float timestamp;

    /// <summary>消息内容摘要。</summary>
    public string content;
}

/// <summary>
/// LLM 调用日志条目。
/// </summary>
[Serializable]
public class LlmLogEntry
{
    /// <summary>发起该调用的智能体 ID。</summary>
    public string agentId;

    /// <summary>日志时间字符串。</summary>
    public string timestamp;

    /// <summary>日志类型。</summary>
    public string type;

    /// <summary>模型名。</summary>
    public string model;

    /// <summary>温度参数。</summary>
    public float temperature;

    /// <summary>最大 token 设置。</summary>
    public int maxTokens;

    /// <summary>日志内容摘要。</summary>
    public string content;

    /// <summary>调用阶段标签（如 "LLM#1"、"ADM_Roll_iter2"）。</summary>
    public string tag;
}

/// <summary>
/// StructuredConstraint 的仪表板快照（字段与原始类一一对应）。
/// </summary>
[Serializable]
public class StructuredConstraintSnapshot
{
    // 通用
    public string constraintId;
    public string cType;
    public string channel;
    public int    groupScope;
    // C1
    public string subject;
    public string targetObject;
    public bool   exclusive;
    // C2
    public string   condition;
    public string[] syncWith;
    // C3
    public int    sign;
    public string watchAgent;
    public string reactTo;
}

/// <summary>
/// 白板条目快照（用于仪表板展示）。
/// </summary>
[Serializable]
public class WhiteboardEntrySnapshot
{
    public string groupId;
    public string agentId;
    public string constraintId;
    public string entryType;
    public string progress;
    public int status;
    public float timestamp;
}

/// <summary>
/// 白板写入历史记录（用于仪表板时间线）。
/// </summary>
[Serializable]
public class WhiteboardWriteRecord
{
    public float timestamp;
    public string groupId;
    public string agentId;
    public string constraintId;
    public string entryType;
    public string progress;
}

/// <summary>
/// 白板快照（条目 + 写入历史）。
/// </summary>
[Serializable]
public class WhiteboardSnapshot
{
    public WhiteboardEntrySnapshot[] entries;
    public WhiteboardWriteRecord[] history;
}

// ── MAD 辩论快照（与当前 MADCoordinator / MADContracts 对齐）────────────────

/// <summary>
/// 单个成员意见的仪表板快照（对应 MemberOpinion）。
/// </summary>
[Serializable]
public class MadMemberOpinionSnapshot
{
    public string agentId;
    public int    round;           // 1 = 独立提案；2 = 参考汇总后修正
    public string recommendation;  // 成员建议
    public float  confidence;      // 0-1
    public string thought;         // LLM 推理过程 JSON 字符串，可能为空
}

/// <summary>
/// 单条 AgentDirective 的仪表板快照。
/// </summary>
[Serializable]
public class MadDirectiveSnapshot
{
    public string agentId;
    public string instruction;
    public string targetModule;   // "planning" | "adm"
    public string payload;        // JSON 字符串
}

/// <summary>
/// 仲裁决策的仪表板快照（对应 IncidentDecision）。
/// </summary>
[Serializable]
public class MadDecisionSnapshot
{
    public string summary;
    public MadDirectiveSnapshot[] directives;
    public string thought;
}

/// <summary>
/// 单个 MAD 事件及其辩论过程的完整快照（由 MADCoordinator 通过 AgentStateServer.PushMadIncident 推送）。
/// </summary>
[Serializable]
public class MadIncidentSnapshot
{
    public string incidentId;
    public string incidentType;
    public bool   isCritical;
    public string reporterId;
    public string groupId;
    public string description;
    public string context;
    public string status;          // "Debating" | "Resolved"
    public float  startedAt;
    public float  resolvedAt;
    public MadMemberOpinionSnapshot[] opinions;  // 两轮意见合并，按时序排列
    public MadDecisionSnapshot        decision;  // null 直到 Resolved
}

// ── 运动事件 ─────────────────────────────────────────────────────────────────

/// <summary>
/// DoMoveTo 关键事件条目，供仪表板在地图气泡中可视化。
/// </summary>
[Serializable]
public class MotionEventDto
{
    /// <summary>发起该事件的智能体 ID。</summary>
    public string agentId;
    /// <summary>事件类型："move_start" | "waypoint_timeout" | "obstacle_replan" | "arrive"</summary>
    public string eventType;
    /// <summary>含 emoji 前缀的用户可读描述。</summary>
    public string message;
    /// <summary>Time.time 游戏时间戳（秒）。</summary>
    public float  timestamp;
}

// ── 记忆 / 反思洞察快照 ───────────────────────────────────────────────────────

/// <summary>
/// 单条记忆的仪表板展示快照（detail 截断到 500 字，其余字段完整保留）。
/// </summary>
[Serializable]
public class MemorySnapshot
{
    public string   id;
    public string   kind;               // AgentMemoryKind.ToString()
    public string   summary;
    public string   detail;             // 最多 500 字
    public string   status;             // AgentMemoryStatus.ToString()
    public float    importance;
    public float    confidence;
    public float    strengthScore;
    public bool     isProceduralHint;
    public int      reflectionDepth;    // 0=L1原始, 1=L2推断, 2=L3抽象
    public string   sourceModule;
    public string   missionId;
    public string   targetRef;
    public string   outcome;
    public string[] tags;
    public long     createdAtUnix;      // DateTimeOffset.ToUnixTimeSeconds()
    public long     lastAccessedAtUnix;
    public int      accessCount;
}

/// <summary>
/// 反思洞察的仪表板展示快照。
/// </summary>
[Serializable]
public class ReflectionInsightSnapshot
{
    public string   id;
    public int      insightDepth;       // 1=L2, 2=L3
    public string   title;
    public string   summary;
    public string   applyWhen;
    public string   suggestedAdjustment;
    public float    confidence;
    public string   missionId;
    public string   targetRef;
    public string[] tags;
    public long     createdAtUnix;
    public long     expiresAtUnix;
    public float    remainingSeconds;   // 服务端预算（秒），前端直接展示
}

/// <summary>
/// 单个智能体的完整记忆 + 反思洞察快照载体。
/// </summary>
[Serializable]
public class AgentMemoryPayload
{
    public string                      agentId;
    public int                         totalMemoryCount; // 实际总条数（含未传输部分）
    public MemorySnapshot[]            memories;         // 最多 30 条（程序性优先）
    public ReflectionInsightSnapshot[] insights;         // 全部有效洞察
}

// ── 涌现模块快照 ──────────────────────────────────────────────────────────────

/// <summary>
/// 单个 Agent 的驱动力快照条目。
/// </summary>
[Serializable]
public class DriveEntry
{
    public string name;
    public float  strength;
}

/// <summary>
/// 协作招募候选者快照。
/// </summary>
[Serializable]
public class AcceptorEntry
{
    public string agentId;
    public float  battery;
    public string location;
}

/// <summary>
/// 单个 Agent 的涌现模块状态快照（每 0.5s 由 AgentStateServer 采集）。
/// </summary>
[Serializable]
public class EmergenceSnapshot
{
    /// <summary>所属 Agent ID。</summary>
    public string agentId;

    /// <summary>是否正在执行 EvaluateAndTrigger 协程（LLM 决策阶段）。</summary>
    public bool isEvaluating;

    /// <summary>是否处于邀请收集窗口（等待 ColabAccept 中）。</summary>
    public bool collectingAcceptors;

    /// <summary>当前候选接受者列表（仅收集窗口内有效）。</summary>
    public AcceptorEntry[] pendingAcceptors;

    /// <summary>本次计算出的各驱动力强度（按强度降序）。</summary>
    public DriveEntry[] drives;

    /// <summary>最强驱动力名称。</summary>
    public string topDrive;

    /// <summary>最强驱动力强度。</summary>
    public float topDriveStrength;

    /// <summary>最近一次 LLM 生成的任务目标。</summary>
    public string lastGoal;

    /// <summary>最近一次 LLM 生成的 thought 独白。</summary>
    public string lastThought;

    /// <summary>最近一次涌现是否请求协作（已废弃，恒为 false；保留字段避免前端解析报错）。</summary>
    public bool lastNeedsHelp;

    /// <summary>距下次评估的剩余秒数（-1=非 Idle 状态不计时）。</summary>
    public float secsUntilNextEval;

    // ── 涌现重设计新增字段 ─────────────────────────────────────────────────────

    /// <summary>该 agent 是否为破坏型（PersonalitySystem.IsAdversarial）。破坏型出现红色徽章。</summary>
    public bool isAdversarial;

    /// <summary>是否正在执行自主涌现独立任务（PlanningModule.IsRunningSolo）。</summary>
    public bool isRunningSolo;

    /// <summary>
    /// 是否正在执行感知触发协作评估（EvaluateCollabTrigger 运行中）。
    /// 由 isEvaluating &amp;&amp; isRunningSolo 联合推断：Solo 执行中被感知触发评估。
    /// </summary>
    public bool inCollabSetup;
}

/// <summary>
/// 全局持久化规律库快照（所有 agent 的 Policy 记忆 + ReflectionInsight 去重合并）。
/// </summary>
[Serializable]
public class PersistentMemoryPayload
{
    public int                         policyCount;   // Policy 记忆总数
    public int                         insightCount;  // Insight 总数
    public MemorySnapshot[]            policies;      // 全部 Policy 记忆（按 strengthScore 降序）
    public ReflectionInsightSnapshot[] insights;      // 全部有效 Insight（按 insightDepth+createdAt 降序）
    public string                      saveFilePath;  // 持久化文件路径（供前端展示）
}
