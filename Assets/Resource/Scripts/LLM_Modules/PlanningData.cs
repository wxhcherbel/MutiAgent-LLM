// LLM_Modules/PlanningData.cs
// 规划层共享数据结构：新4阶段协商协议 + 旧系统兼容类型
using System;
using UnityEngine;

// ─────────────────────────────────────────────────────────────
// 一、新4阶段协商协议数据结构
// ─────────────────────────────────────────────────────────────

/// <summary>LLM#1 解析自然语言任务的输出。协调者用于分组。</summary>
[Serializable]
public class ParsedMission
{
    public string msnId;        // 任务唯一ID，系统生成，格式 "msn_yyyyMMdd_N"
    public string relType;      // 关系类型："Cooperation"/"Competition"/"Adversarial"/"Mixed"
    public int groupCnt;        // 组数；Cooperation=1，其余>=2
    public string[] groupMsns;  // 各组任务描述，下标对应组序号
    public float timeLimit;     // 任务时间限制（秒），0=无限制
}

/// <summary>协调者广播给全体的分组通知，接收方在 groups 中找自己的条目。</summary>
[Serializable]
public class GroupBootstrapPayload
{
    public string msnId;        // 任务ID
    public string relType;      // 关系类型
    public GroupDef[] groups;   // 全部分组定义
}

/// <summary>一个组的定义。</summary>
[Serializable]
public class GroupDef
{
    public string groupId;      // 组ID，如 "g0"/"g1"
    public string mission;      // 本组任务描述
    public string leaderId;     // 组长 AgentID
    public string[] memberIds;  // 全部成员 AgentID（含组长）
}

/// <summary>组长广播给组内成员的计划槽列表，成员从中选择一个。</summary>
[Serializable]
public class SlotBroadcastPayload
{
    public string msnId;        // 任务ID
    public string groupId;      // 组ID
    public string leaderId;     // 组长 AgentID，成员发 SlotSelect 时的收件人
    public PlanSlot[] slots;    // 可选计划槽，数量=本组成员数
}

/// <summary>步骤级协同约束条目，包含触发时机和约束内容。</summary>
[Serializable]
public class StepConstraint
{
    public string trigger;      // 约束生效时机（描述性，供人阅读）
    public string slotRef;      // 约束涉及的另一个槽 ID，如 "s1"（可为空）
    public string constraint;   // 约束内容
}

/// <summary>一个计划槽，代表组内一个成员需要承担的子任务。</summary>
[Serializable]
public class PlanSlot
{
    public string slotId;       // 槽ID，在本组内唯一，如 "s0"/"s1"
    public string role;         // 对应角色，如 "Scout"/"Defender"
    public string desc;         // 任务描述，成员和 LLM#3 读此字段判断是否适合
    public string doneCond;     // 完成条件
    public StepConstraint[] coordinationConstraints; // 步骤级协同约束列表，空数组=无约束
}

/// <summary>成员发给组长的槽选择。</summary>
[Serializable]
public class SlotSelectPayload
{
    public string msnId;        // 任务ID
    public string agentId;      // 发送方 AgentID
    public string slotId;       // 选择的槽ID
    public string reason;       // 选择理由，组长冲突处理时参考
}

/// <summary>组长发给每个成员的最终确认（一对一），adjusted=true 时表示发生了冲突调整。</summary>
[Serializable]
public class SlotConfirmPayload
{
    public string msnId;        // 任务ID
    public string agentId;      // 接收方 AgentID
    public PlanSlot slot;       // 最终分配的槽（可能与选择不同）
    public bool adjusted;       // 是否经过冲突调整
    public string adjReason;    // adjusted=true 时说明原因
}

/// <summary>组长广播给组内全员，通知所有人开始 LLM#4 步骤拆解。</summary>
[Serializable]
public class StartExecPayload
{
    public string msnId;        // 任务ID
    public string groupId;      // 组ID
}

/// <summary>LLM#4 输出的单个步骤，ActionDecisionModule 每次读 text 字段。</summary>
[Serializable]
public class PlanStep
{
    public string stepId;         // 步骤ID，格式 "step_N"，N 从 1 开始
    public string text;           // 步骤指令，格式"动作+目标(+参数)"
    public string doneCond;       // 本步骤完成条件
    public string[] constraints;  // 绑定到本步骤的协同约束列表（由 RunLLM4 后处理填入）
}

/// <summary>智能体本地保存的完整计划，由 SlotConfirm + LLM#4 共同填充。</summary>
[Serializable]
public class AgentPlan
{
    public string msnId;        // 任务ID
    public string slotId;       // 对应 PlanSlot.slotId
    public string role;         // 分配角色
    public string desc;         // 计划描述（PlanSlot.desc 原文，LLM#4 输入）
    public PlanStep[] steps;    // 步骤列表，LLM#4 生成后填入
    public int curIdx;          // 当前执行步骤索引，初始 0，CompleteCurrentStep 时递增
}

// ─── ADM 执行层数据结构 ────────────────────────────────────────────

/// <summary>原子动作，ADM 的最小可执行单元。</summary>
[Serializable]
public class AtomicAction
{
    public string actionId;              // 唯一ID，格式 "aa_N"
    public AtomicActionType type;        // 动作类型（枚举在 CoreEnums.cs）
    public string targetName;            // 目标地名（MoveTo/PatrolAround/Observe 用）
    public string targetAgentId;         // 目标智能体ID（FormationHold 用）
    public float  duration;              // 持续秒数（Wait/Observe 用；-1=条件触发结束）
    public string broadcastContent;      // 广播文本（Broadcast 用）
    public string actionParams;          // LLM填写：具体参数，如"高度50米"、"环绕半径30米"、"速度慢速"
    public string spatialHint;           // LLM填写：方向/空间修饰，如"从东侧接近"、"绕行西路"
}

/// <summary>ADM 当前步骤的完整执行上下文。</summary>
[Serializable]
public class ActionExecutionContext
{
    public string   msnId;
    public string   stepId;
    public string   stepText;
    public string[] coordinationConstraints;  // 本步骤的协同约束列表
    public RoleType role;

    public AtomicAction[] actionQueue;
    public int            currentActionIdx;
    public ADMStatus      status;

    public string   currentLocationName;
    //public string   originalGoalName;
    public string[] remainingWaypoints;
    public string[] recentEvents;        // 最近3条感知事件，时间倒序
}

/// <summary>黑板条目：智能体向队友广播的状态快照。</summary>
[Serializable]
public class AgentContextUpdate
{
    public string   agentId;
    public string   locationName;
    public string   currentAction;   // AtomicActionType 枚举名
    public string   currentTarget;
    public string   role;

    public string[] plannedTargets;  // 计划访问的目标地名序列
    public string[] recentEvents;    // 最近3条事件摘要

    public float    timestamp;
}
