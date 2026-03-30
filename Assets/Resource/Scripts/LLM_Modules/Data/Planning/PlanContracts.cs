using System;

/// <summary>
/// 结构化协同约束，涵盖三种约束类型（C1~C3）。
/// </summary>
[Serializable]
public class StructuredConstraint
{
    // ── 通用字段 ─────────────────────────────────────────────────
    /// <summary>约束唯一 ID，例如 c1_assign_scout。</summary>
    public string constraintId;

    /// <summary>约束类型：C1=Assignment, C2=Completion, C3=Coupling。</summary>
    public string cType;

    /// <summary>协调通道：whiteboard | referee | direct。</summary>
    public string channel;

    /// <summary>
    /// 约束作用域：>=0=仅对指定组序号可见（C1/C2/C3 组内约束）。
    /// LLM#1 生成时填对应 groupIndex（单组任务填 0）。
    /// GroupBootstrap 分发时仅将 groupScope==本组序号 的约束发给该组。
    /// </summary>
    public int groupScope;

    // ── C1 Assignment ─────────────────────────────────────────────
    /// <summary>[C1] 被分配的执行主体 AgentID。</summary>
    public string subject;

    /// <summary>[C1] 被分配的目标对象名称。</summary>
    public string targetObject;

    /// <summary>[C1] 是否独占（true=只有 subject 可访问 targetObject）。</summary>
    public bool exclusive;

    // ── C2 Completion ─────────────────────────────────────────────
    /// <summary>[C2] 步骤完成条件描述。</summary>
    public string condition;

    /// <summary>
    /// [C2] 需要等待其写入 DoneSignal 的其他 Agent ID 列表。
    /// 例如 ["drone_B","drone_C"] 表示等这两个 Agent 也完成后才继续。
    /// LLM#1 阶段不知道具体 agentId 时填 []，表示不等待（仅自身完成即可）。
    /// </summary>
    // Phase 1: abstract slot/role refs from LLM2. Phase 2: rewritten to runtime agentIds.
    public string[] syncWith;

    // ── C3 Coupling ───────────────────────────────────────────────
    /// <summary>
    /// [C3] 耦合符号，定义等待语义：
    ///   +1 = 单向前置等待：我等 watchAgent 写入 ReadySignal 后才行动（非对称依赖）。
    ///        例：无人机 B 必须等无人机 A 到达掩护位后才起飞。
    ///   -1 = 动态互斥：与其他 agent 不能同时前往同一目标，先到先得。
    ///        ADM 每轮从白板读取该约束下其他 agent 的 IntentAnnounce.progress（已占目标名），
    ///        注入 Prompt 让 LLM 主动避开；LLM 决定目标后写 IntentAnnounce.progress 占位；
    ///        步骤完成时 ClearEntry 释放占位。
    /// </summary>
    public int sign;

    /// <summary>[C3] 被监视的智能体 ID（等待其 ReadySignal / 互斥的对端 agentId；不知道时填 ''）。</summary>
    // Phase 1: abstract slot/role ref from LLM2. Phase 2: rewritten to runtime agentId.
    public string watchAgent;

    /// <summary>
    /// [C3] 触发本约束的事件类型。
    ///   sign=+1 填 "ReadySignal"；sign=-1 填 "IntentAnnounce"（互斥占位信号）。
    /// </summary>
    public string reactTo;

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

    /// <summary>该槽位关联的结构化约束 ID 列表（引用 ParsedMission.constraints）。</summary>
    public string[] constraintIds;
}

[Serializable]
public class LLM2SlotPlanResult
{
    public PlanSlot[] slots;
    public StructuredConstraint[] constraints;
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

    /// <summary>
    /// LLM#4 从 text 中提取的空间目标名称（例如"南区"）。
    /// 无明确空间目标的步骤填 ""。
    /// </summary>
    public string targetName;

    /// <summary>该步骤的完成条件。</summary>
    public string doneCond;

    /// <summary>该步骤关联的结构化约束 ID 列表（从 ParsedMission.constraints 中引用）。</summary>
    public string[] constraintIds;
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
