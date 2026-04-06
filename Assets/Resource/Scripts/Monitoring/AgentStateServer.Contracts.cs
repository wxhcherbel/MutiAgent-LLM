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

// ── MAD 辩论快照 ─────────────────────────────────────────────────────────────

/// <summary>
/// 单个辩论条目的仪表板快照（隐藏 incidentId 冗余字段以减小传输量）。
/// </summary>
[Serializable]
public class DebateEntrySnapshot
{
    public string entryId;
    public string authorId;
    public int    debateRound;
    public string role;
    public string content;
    public float  confidence;
    public string voteFor;
    public float  createdAt;
}

/// <summary>
/// 单个紧急事件及其辩论记录的完整快照（由 IncidentCoordinator 暴露）。
/// </summary>
[Serializable]
public class IncidentDebateSnapshot
{
    public string incidentId;
    public string incidentType;
    public string severity;
    public string status;
    public string reporterId;
    public string groupId;
    public string affectedAgentId;
    public string affectedTaskId;
    public string description;
    public float  reportedAt;
    public float  resolvedAt;
    public string finalResolutionSummary;
    public DebateEntrySnapshot[] entries;
}
