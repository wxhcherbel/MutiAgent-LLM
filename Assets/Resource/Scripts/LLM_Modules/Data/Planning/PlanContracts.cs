using System;

/// <summary>
/// 步骤级协同约束。
/// </summary>
[Serializable]
public class StepConstraint
{
    /// <summary>约束生效的触发时机。</summary>
    public string trigger;

    /// <summary>关联的其他槽位 ID，可为空。</summary>
    public string slotRef;

    /// <summary>约束的自然语言内容。</summary>
    public string constraint;
}

/// <summary>
/// 组内单个成员要承担的计划槽。
/// </summary>
[Serializable]
public class PlanSlot
{
    /// <summary>槽位唯一 ID。</summary>
    public string slotId;

    /// <summary>该槽位对应的角色名。</summary>
    public string role;

    /// <summary>该槽位的职责描述。</summary>
    public string desc;

    /// <summary>该槽位的完成条件。</summary>
    public string doneCond;

    /// <summary>该槽位关联的步骤级协同约束。</summary>
    public StepConstraint[] coordinationConstraints;
}

/// <summary>
/// LLM#4 拆解出的单个步骤。
/// </summary>
[Serializable]
public class PlanStep
{
    /// <summary>步骤唯一 ID，例如 step_1。</summary>
    public string stepId;

    /// <summary>供 ADM 继续解释的步骤文本。</summary>
    public string text;

    /// <summary>该步骤的完成条件。</summary>
    public string doneCond;

    /// <summary>已经挂载到该步骤上的协同约束文本数组。</summary>
    public string[] constraints;
}

/// <summary>
/// 智能体本地持有的完整计划实例。
/// </summary>
[Serializable]
public class AgentPlan
{
    /// <summary>所属任务 ID。</summary>
    public string msnId;

    /// <summary>当前计划对应的槽位 ID。</summary>
    public string slotId;

    /// <summary>当前智能体在该计划中扮演的角色名。</summary>
    public string role;

    /// <summary>槽位的原始职责描述。</summary>
    public string desc;

    /// <summary>该计划拆解出的全部步骤。</summary>
    public PlanStep[] steps;

    /// <summary>当前执行到的步骤索引。</summary>
    public int curIdx;
}
