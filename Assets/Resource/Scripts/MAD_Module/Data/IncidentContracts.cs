// MAD_Module/Data/IncidentContracts.cs
// 紧急情况处理与 MAD 辩论协议的核心数据契约。
using System;
using UnityEngine;

// ── 核心事件报告 ─────────────────────────────────────────────────────────────

/// <summary>
/// 紧急事件报告：统一所有 4 种事件类型的单一结构体。
/// 由任意 agent 检测到紧急情况后创建，发送给 leader 的 GroupMonitor 处理。
/// </summary>
[Serializable]
public class IncidentReport
{
    /// <summary>唯一事件 ID，格式 "inc_001"，由 GroupMonitor 生成。</summary>
    public string incidentId;

    /// <summary>事件分类（4 种类型之一）。</summary>
    public IncidentType incidentType;

    /// <summary>严重程度（由 GroupMonitor 按规则自动判定，无需 LLM）。</summary>
    public IncidentSeverity severity;

    /// <summary>上报者 agentId。</summary>
    public string reporterId;

    /// <summary>所属团队 ID。</summary>
    public string groupId;

    /// <summary>直接受影响的 agent（PlanInvalid/CapacityShortfall 时可为空）。</summary>
    public string affectedAgentId;

    /// <summary>受影响的任务步骤 ID（可为空）。</summary>
    public string affectedTaskId;

    /// <summary>自然语言描述，供 LLM 理解具体情况。</summary>
    public string description;

    /// <summary>当前处理状态。</summary>
    public IncidentStatus status;

    /// <summary>上报时间（Time.time）。</summary>
    public float reportedAt;

    /// <summary>解决时间（0 = 未解决）。</summary>
    public float resolvedAt;

    /// <summary>最终决策的自然语言摘要（辩论结束后写入）。</summary>
    public string finalResolutionSummary;
}

// ── 辩论记录 ─────────────────────────────────────────────────────────────────

/// <summary>
/// 辩论条目：单个 agent 在一轮辩论中的发言记录。
/// 走 CommunicationModule 路由，不进白板（由 DebateCoordinator 汇总）。
/// </summary>
[Serializable]
public class DebateEntry
{
    /// <summary>条目唯一 ID，格式 "dbt_{incidentId}_r{round}_{agentId}"。</summary>
    public string entryId;

    /// <summary>所属事件 ID（反向引用）。</summary>
    public string incidentId;

    /// <summary>发言 agent 的 ID。</summary>
    public string authorId;

    /// <summary>辩论轮次（1-based）。</summary>
    public int debateRound;

    /// <summary>发言角色。</summary>
    public DebateRole role;

    /// <summary>提案或批评内容（自然语言，2-3 句具体可执行方案）。</summary>
    public string content;

    /// <summary>LLM 自评置信度（0.0-1.0）。</summary>
    public float confidence;

    /// <summary>投票支持的 entryId（Voter 角色用；Proposer/Critic 时为空）。</summary>
    public string voteFor;

    /// <summary>创建时间（Time.time）。</summary>
    public float createdAt;
}

// ── 白板共识条目 ─────────────────────────────────────────────────────────────

/// <summary>
/// 辩论共识条目：辩论结束后 leader 写入白板的最终决策。
/// constraintId 格式："__dbt_{incidentId}__"，staleSeconds 建议 120f。
/// 各 agent 在下一个参与窗口读取并按 assignedAgentId 执行决策。
/// </summary>
[Serializable]
public class DebateConsensusEntry
{
    /// <summary>所属事件 ID。</summary>
    public string incidentId;

    /// <summary>最终决策（自然语言，具体可执行）。</summary>
    public string resolution;

    /// <summary>负责执行决策的 agentId（可为空，表示全员知晓但不指定执行方）。</summary>
    public string assignedAgentId;

    /// <summary>此决策是否修改了任务范围（触发重规划时为 true）。</summary>
    public bool missionScopeChanged;

    /// <summary>决策时间（Time.time）。</summary>
    public float decidedAt;
}

// ── 通信 Payload ─────────────────────────────────────────────────────────────

/// <summary>
/// Leader 向组员发送的辩论角色分配消息（MessageType.DebateProposal）。
/// 包含角色、当前轮次及已有提案摘要，供 agent LLM 生成回复。
/// </summary>
[Serializable]
public class DebateRoleAssignment
{
    /// <summary>所属事件 ID。</summary>
    public string incidentId;

    /// <summary>Leader agentId（agent 将 DebateUpdate 回复给此 ID）。</summary>
    public string leaderId;

    /// <summary>完整事件报告（供 agent LLM 理解上下文）。</summary>
    public IncidentReport report;

    /// <summary>本轮分配给该 agent 的角色。</summary>
    public DebateRole role;

    /// <summary>当前辩论轮次（1-based）。</summary>
    public int debateRound;

    /// <summary>已有提案的文本摘要（第 2+ 轮时非空，供 agent 参考）。</summary>
    public string existingEntriesSummary;
}
