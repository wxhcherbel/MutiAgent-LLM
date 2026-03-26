using System;
using System.Collections.Generic;

/// <summary>
/// 记忆类型。
/// </summary>
[Serializable]
public enum AgentMemoryKind
{
    Observation = 0,
    Goal = 1,
    Plan = 2,
    Decision = 3,
    Outcome = 4,
    Coordination = 5,
    Reflection = 6,
    Policy = 7,
    WorldState = 8,
    Relationship = 9
}

/// <summary>
/// 记忆生命周期状态。
/// </summary>
[Serializable]
public enum AgentMemoryStatus
{
    Active = 0,
    Completed = 1,
    Failed = 2,
    Archived = 3
}

/// <summary>
/// 单条结构化记忆。
/// </summary>
[Serializable]
public class Memory
{
    /// <summary>记忆唯一 ID。</summary>
    public string id;

    /// <summary>记忆的语义类型。</summary>
    public AgentMemoryKind kind;

    /// <summary>记忆摘要。</summary>
    public string summary;

    /// <summary>记忆详情。</summary>
    public string detail;

    /// <summary>记忆创建时间。</summary>
    public DateTime createdAt;

    /// <summary>最近一次被访问的时间。</summary>
    public DateTime lastAccessedAt;

    /// <summary>记忆重要度。</summary>
    public float importance;

    /// <summary>记忆可信度。</summary>
    public float confidence;

    /// <summary>记忆当前状态。</summary>
    public AgentMemoryStatus status;

    /// <summary>产出该记忆的模块名。</summary>
    public string sourceModule;

    /// <summary>关联任务 ID。</summary>
    public string missionId;

    /// <summary>关联槽位 ID。</summary>
    public string slotId;

    /// <summary>关联步骤标识。</summary>
    public string stepLabel;

    /// <summary>关联目标引用。</summary>
    public string targetRef;

    /// <summary>结果描述。</summary>
    public string outcome;

    /// <summary>标签集合。</summary>
    public List<string> tags = new List<string>();

    /// <summary>关联的其他智能体 ID 列表。</summary>
    public List<string> relatedAgentIds = new List<string>();

    /// <summary>关联的实体引用列表。</summary>
    public List<string> relatedEntityRefs = new List<string>();

    /// <summary>从哪些旧记忆衍生而来。</summary>
    public List<string> derivedFromMemoryIds = new List<string>();

    /// <summary>访问次数。</summary>
    public int accessCount;

    /// <summary>是否属于可直接复用的程序性提示。</summary>
    public bool isProceduralHint;
}

/// <summary>
/// 记忆检索条件。
/// </summary>
[Serializable]
public class MemoryQuery
{
    /// <summary>自由文本检索词。</summary>
    public string freeText;

    /// <summary>按任务 ID 过滤。</summary>
    public string missionId;

    /// <summary>按槽位 ID 过滤。</summary>
    public string slotId;

    /// <summary>按步骤标识过滤。</summary>
    public string stepLabel;

    /// <summary>按目标引用过滤。</summary>
    public string targetRef;

    /// <summary>限定允许返回的记忆类型集合。</summary>
    public AgentMemoryKind[] kinds;

    /// <summary>要求匹配的标签集合。</summary>
    public List<string> tags = new List<string>();

    /// <summary>要求匹配的相关智能体集合。</summary>
    public List<string> relatedAgentIds = new List<string>();

    /// <summary>要求匹配的相关实体集合。</summary>
    public List<string> relatedEntityRefs = new List<string>();

    /// <summary>最多返回多少条记忆。</summary>
    public int maxCount = 5;

    /// <summary>是否优先返回程序性提示类记忆。</summary>
    public bool preferProceduralHints;
}

/// <summary>
/// 规划阶段向记忆模块请求上下文时使用的参数。
/// </summary>
[Serializable]
public class PlanningMemoryContextRequest
{
    /// <summary>原始任务文本。</summary>
    public string missionText;

    /// <summary>任务 ID。</summary>
    public string missionId;

    /// <summary>团队层面的总目标。</summary>
    public string teamObjective;

    /// <summary>当前角色名称。</summary>
    public string roleName;

    /// <summary>当前槽位 ID。</summary>
    public string slotId;

    /// <summary>当前槽位标签或名称。</summary>
    public string slotLabel;

    /// <summary>当前槽位主要目标。</summary>
    public string slotTarget;

    /// <summary>规划中涉及的中继目标列表。</summary>
    public string[] viaTargets;

    /// <summary>最多取回多少条记忆。</summary>
    public int maxMemories = 4;

    /// <summary>最多取回多少条反思 insight。</summary>
    public int maxInsights = 2;
}

/// <summary>
/// 动作阶段向记忆模块请求上下文时使用的参数。
/// </summary>
[Serializable]
public class ActionMemoryContextRequest
{
    /// <summary>原始任务文本。</summary>
    public string missionText;

    /// <summary>任务 ID。</summary>
    public string missionId;

    /// <summary>槽位 ID。</summary>
    public string slotId;

    /// <summary>当前步骤文本。</summary>
    public string stepText;

    /// <summary>当前动作指向的目标引用。</summary>
    public string targetRef;

    /// <summary>当前步骤意图摘要。</summary>
    public string stepIntentSummary;

    /// <summary>协同约束摘要。</summary>
    public string coordinationSummary;

    /// <summary>最多取回多少条记忆。</summary>
    public int maxMemories = 3;

    /// <summary>最多取回多少条反思 insight。</summary>
    public int maxInsights = 2;
}

/// <summary>
/// 反思模块沉淀出的可复用 insight。
/// </summary>
[Serializable]
public class ReflectionInsight
{
    /// <summary>insight 唯一 ID。</summary>
    public string id;

    /// <summary>insight 标题。</summary>
    public string title;

    /// <summary>insight 核心摘要。</summary>
    public string summary;

    /// <summary>适用场景说明。</summary>
    public string applyWhen;

    /// <summary>建议的动作调整策略。</summary>
    public string suggestedAdjustment;

    /// <summary>关联任务 ID。</summary>
    public string missionId;

    /// <summary>关联槽位 ID。</summary>
    public string slotId;

    /// <summary>关联目标引用。</summary>
    public string targetRef;

    /// <summary>创建时间。</summary>
    public DateTime createdAt;

    /// <summary>失效时间。</summary>
    public DateTime expiresAt;

    /// <summary>可信度。</summary>
    public float confidence;

    /// <summary>标签集合。</summary>
    public List<string> tags = new List<string>();

    /// <summary>支撑该 insight 的来源记忆 ID 列表。</summary>
    public List<string> sourceMemoryIds = new List<string>();
}
