using System;
using System.Collections.Generic;

/// <summary>
/// ADM 最小可执行动作单元。
/// </summary>
[Serializable]
public class AtomicAction
{
    /// <summary>动作唯一 ID。</summary>
    public string actionId;

    /// <summary>动作类型枚举。</summary>
    public AtomicActionType type;

    /// <summary>动作的地图目标名称。</summary>
    public string targetName;

    /// <summary>动作关联的目标智能体 ID。</summary>
    public string targetAgentId;

    /// <summary>动作持续时间，单位秒。</summary>
    public float duration;

    /// <summary>动作的细节参数文本。</summary>
    public string actionParams;

    /// <summary>动作的空间偏好或路径提示文本。</summary>
    public string spatialHint;
}

/// <summary>
/// 滚动规划单次 LLM 调用的返回结构。
/// </summary>
[Serializable]
public class RollingPlanResult
{
    /// <summary>LLM 对当前局势的推理过程（ReAct Thought，1-2句）。</summary>
    public string thought;

    /// <summary>LLM 判断当前步骤是否已完成。</summary>
    public bool isDone;

    /// <summary>完成或未完成的原因说明。</summary>
    public string doneReason;

    /// <summary>下一批待执行原子动作（isDone=true 时可为空数组，最多3个）。</summary>
    public AtomicAction[] nextActions;
}

/// <summary>
/// ADM 在当前步骤内维护的执行上下文。
/// </summary>
[Serializable]
public class ActionExecutionContext
{
    /// <summary>所属任务 ID。</summary>
    public string msnId;

    /// <summary>当前步骤 ID。</summary>
    public string stepId;

    /// <summary>当前步骤文本。</summary>
    public string stepText;

    /// <summary>当前步骤关联的结构化约束对象列表（已从 ID 解析）。</summary>
    public StructuredConstraint[] stepConstraints;

    /// <summary>当前智能体在该步骤中的角色。</summary>
    public RoleType role;

    /// <summary>已经生成的原子动作队列（当前批次）。</summary>
    public AtomicAction[] actionQueue;

    /// <summary>当前执行到的动作下标。</summary>
    public int currentActionIdx;

    /// <summary>ADM 当前状态。</summary>
    public ADMStatus status;

    /// <summary>步骤开始时解析出的当前位置名称。</summary>
    public string currentLocationName;

    /// <summary>最近记录的感知事件摘要。</summary>
    public string[] recentEvents;

    // ── 滚动规划字段 ────────────────────────────────────────────────

    /// <summary>已执行动作摘要列表，供 LLM 判断步骤进度。</summary>
    public List<string> executedActionsSummary;

    /// <summary>当前滚动迭代次数（防死锁计数）。</summary>
    public int iterationCount;

    /// <summary>
    /// 是否处于滚动模式。
    /// true = CompleteCurrentAction 批次完成时设 BatchDone，由 RunRollingLoop 处理下一轮；
    /// false = 原有一次性模式，动作队列耗尽时直接设 Done。
    /// </summary>
    public bool isRollingMode;
}
