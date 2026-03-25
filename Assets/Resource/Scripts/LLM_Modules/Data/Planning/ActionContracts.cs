using System;

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

    /// <summary>当前步骤绑定的协同约束数组。</summary>
    public string[] coordinationConstraints;

    /// <summary>当前智能体在该步骤中的角色。</summary>
    public RoleType role;

    /// <summary>已经生成的原子动作队列。</summary>
    public AtomicAction[] actionQueue;

    /// <summary>当前执行到的动作下标。</summary>
    public int currentActionIdx;

    /// <summary>ADM 当前状态。</summary>
    public ADMStatus status;

    /// <summary>步骤开始时解析出的当前位置名称。</summary>
    public string currentLocationName;

    /// <summary>最近记录的感知事件摘要。</summary>
    public string[] recentEvents;
}
