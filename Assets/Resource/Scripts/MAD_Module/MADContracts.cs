// MAD_Module/MADContracts.cs
// MAD（Multi-Agent Debate）模块的核心数据契约。
// 其他模块通过 DebateRequest 发起辩论，不直接操作 IncidentReport 等内部类型。
using System;
using UnityEngine;

// ── 辩论请求（外部入口契约）─────────────────────────────────────────────────

/// <summary>
/// 辩论发起请求，由任意模块创建并传给 IMADGateway.Raise()。
/// MADGateway 负责将其转换为 IncidentReport 路由给 leader 的 GroupMonitor。
/// </summary>
[Serializable]
public class DebateRequest
{
    /// <summary>发起方 agentId（通常为检测到问题的 agent 自身）。</summary>
    public string initiatorId;

    /// <summary>直接受影响的 agentId（可与 initiatorId 相同，如电量耗尽自报）。</summary>
    public string affectedAgentId;

    /// <summary>受影响的任务/步骤 ID（可为空，用于 slot 冲突或跨步骤事件）。</summary>
    public string affectedTaskId;

    /// <summary>事件类型（AgentUnavailable / AgentImpaired / PlanInvalid / CapacityShortfall）。</summary>
    public IncidentType incidentType;

    /// <summary>简短主题，供日志和白板摘要使用（1 行）。</summary>
    public string topic;

    /// <summary>自然语言详细描述，供 LLM 理解具体情况（2-5 句）。</summary>
    public string context;

    /// <summary>建议辩论轮数（GroupMonitor 可根据 severity 覆盖）。默认 2。</summary>
    public int estimatedRounds;
}

// ── 结构化论据（辩论 prompt 增强）───────────────────────────────────────────

/// <summary>
/// 可附加在 DebateParticipant prompt 中的量化论据，帮助 LLM 输出更有依据的提案。
/// 由 DebateParticipant 在构建 prompt 前可选注入，空字段将被跳过。
/// </summary>
[Serializable]
public class StructuredArgument
{
    /// <summary>当前电量百分比（0-100）。</summary>
    public float batteryLevel;

    /// <summary>到任务目标的直线距离（Unity 单位）。</summary>
    public float distanceToTarget;

    /// <summary>任务优先级权重（来自 PlanSlot.priority，如有）。</summary>
    public float taskPriorityWeight;

    /// <summary>历史成功率（来自 MemoryModule，0-1）。</summary>
    public float historicalSuccessRate;

    /// <summary>剩余步骤数（来自 PlanStep 列表长度）。</summary>
    public int remainingStepsCount;

    /// <summary>提议的应对动作（自然语言，一句话）。</summary>
    public string proposedAction;
}

// ── 辩论层级（三层降级策略）─────────────────────────────────────────────────

/// <summary>
/// 辩论资源层级，由 ResourceBudget.SelectLayer() 根据电量/紧急度/复杂度选择。
/// 当前始终返回 FullLLM，Rule/JsonSummary 为预留扩展。
/// </summary>
public enum DebateLayer
{
    /// <summary>确定性规则仲裁（0 次 LLM 调用，最快）。</summary>
    Rule,

    /// <summary>收集结构化 JSON 论据后 1 次 LLM 综合（中等成本）。</summary>
    JsonSummary,

    /// <summary>完整多轮 MAD（Proposer/Critic/Voter 角色，最高质量）。</summary>
    FullLLM
}
