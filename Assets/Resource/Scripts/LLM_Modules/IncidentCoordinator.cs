// LLM_Modules/IncidentCoordinator.cs
// MAD（Multi-Agent Debate）辩论协调器，由 GroupMonitor 实例化并持有。
// 非 MonoBehaviour；协程由 GroupMonitor 启动，可正常使用 Unity yield 指令。
//
// 辩论协议：
//   Phase 0  — 上报：创建 IncidentReport，写白板 IncidentAnnounce，广播通知
//   Phase 1  — 独立提案（Round 1）：受影响 agent + leader = Proposer，其余 = Critic
//   Phase 2+ — 交叉批评/投票（Round 2-N）：全员切换为 Voter，可改变立场
//   收敛判断  — 投票多数 OR 置信集中 → 直接定案；达轮数上限 → Arbiter 仲裁
//   Phase 4  — 写 DebateConsensus 到白板 + 广播 DebateResolved
// ═══════════════════════════════════════════════════════════════════════════
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class IncidentCoordinator
{
    // ─── 外部依赖 ─────────────────────────────────────────────────────────────

    private readonly GroupMonitor      _owner;
    private readonly GroupDef          _group;
    private readonly string            _groupId;
    private readonly string            _leaderId;
    private readonly LLMInterface      _llm;
    private readonly CommunicationModule _commModule;

    // ─── 状态 ─────────────────────────────────────────────────────────────────

    public IncidentReport ActiveIncident { get; private set; }

    /// <summary>所有已收到的辩论条目（跨轮次）。</summary>
    private readonly List<DebateEntry> _allEntries = new List<DebateEntry>();

    // ─── 白板 constraintId 命名规范 ──────────────────────────────────────────

    private string AnnounceConstraintId(string incidentId) => $"__inc_{incidentId}__";
    private string ConsensusConstraintId(string incidentId) => $"__dbt_{incidentId}__";

    // ─── 超时/轮次常量 ───────────────────────────────────────────────────────

    private static int GetMaxRounds(IncidentSeverity sev) => sev switch
    {
        IncidentSeverity.Critical => 2,
        IncidentSeverity.High     => 3,
        _                         => 4,   // Medium
    };

    private static float GetRoundTimeout(IncidentSeverity sev) =>
        sev == IncidentSeverity.Critical ? 12f : 18f;

    // ─────────────────────────────────────────────────────────────────────────
    // 构造器
    // ─────────────────────────────────────────────────────────────────────────

    public IncidentCoordinator(
        GroupMonitor owner,
        GroupDef group,
        string groupId,
        string leaderId,
        LLMInterface llm,
        CommunicationModule commModule)
    {
        _owner       = owner;
        _group       = group;
        _groupId     = groupId;
        _leaderId    = leaderId;
        _llm         = llm;
        _commModule  = commModule;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 外部接口（GroupMonitor 调用）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 返回当前辩论的完整快照（供仪表板 AgentStateServer 调用）。
    /// </summary>
    public IncidentDebateSnapshot GetSnapshot()
    {
        if (ActiveIncident == null) return null;

        var entrySnaps = new DebateEntrySnapshot[_allEntries.Count];
        for (int i = 0; i < _allEntries.Count; i++)
        {
            var e = _allEntries[i];
            entrySnaps[i] = new DebateEntrySnapshot
            {
                entryId     = e.entryId,
                authorId    = e.authorId,
                debateRound = e.debateRound,
                role        = e.role.ToString(),
                content     = e.content,
                confidence  = e.confidence,
                voteFor     = e.voteFor,
                createdAt   = e.createdAt,
            };
        }

        return new IncidentDebateSnapshot
        {
            incidentId             = ActiveIncident.incidentId,
            incidentType           = ActiveIncident.incidentType.ToString(),
            severity               = ActiveIncident.severity.ToString(),
            status                 = ActiveIncident.status.ToString(),
            reporterId             = ActiveIncident.reporterId,
            groupId                = ActiveIncident.groupId,
            affectedAgentId        = ActiveIncident.affectedAgentId,
            affectedTaskId         = ActiveIncident.affectedTaskId,
            description            = ActiveIncident.description,
            reportedAt             = ActiveIncident.reportedAt,
            resolvedAt             = ActiveIncident.resolvedAt,
            finalResolutionSummary = ActiveIncident.finalResolutionSummary,
            entries                = entrySnaps,
        };
    }

    /// <summary>GroupMonitor 收到 DebateUpdate 消息后调用，线程安全（Unity 单线程）。</summary>
    public void AddDebateEntry(DebateEntry entry)
    {
        if (entry == null || entry.incidentId != ActiveIncident?.incidentId) return;
        _allEntries.Add(entry);
        Debug.Log($"[IncidentCoordinator] 收到辩论条目: {entry.entryId} ({entry.role}, conf={entry.confidence:F2})");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 主辩论协程（由 GroupMonitor.StartCoroutine 启动）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 完整辩论流程协程：从 Phase 0 到 Phase 4。
    /// </summary>
    public IEnumerator RunDebate(IncidentReport report)
    {
        ActiveIncident = report;
        report.status  = IncidentStatus.Debating;

        // ── Phase 0: 宣告事件 ─────────────────────────────────────────────
        WriteIncidentAnnounce(report);
        BroadcastToGroup(MessageType.IncidentAnnounce, report);

        Debug.Log($"[IncidentCoordinator] 启动辩论 {report.incidentId}: " +
                  $"{report.incidentType}/{report.severity}，成员={_group.memberIds?.Length ?? 0}");

        int   maxRounds    = GetMaxRounds(report.severity);
        float roundTimeout = GetRoundTimeout(report.severity);
        int   memberCount  = _group.memberIds?.Length ?? 1;

        for (int round = 1; round <= maxRounds; round++)
        {
            // ── Phase 1/2: 分配角色并发送 DebateProposal ──────────────────
            BroadcastRoleAssignments(report, round);

            // ── 等待响应（带超时）──────────────────────────────────────────
            yield return _owner.StartCoroutine(
                WaitForRoundResponses(report.incidentId, memberCount, round, roundTimeout));

            int received = _allEntries.Count(e => e.incidentId == report.incidentId && e.debateRound == round);
            Debug.Log($"[IncidentCoordinator] Round {round} 结束，收到 {received}/{memberCount} 条回复");

            // ── 收敛判断（最后一轮前检查）────────────────────────────────
            if (round < maxRounds && IsConverged(report.incidentId, memberCount))
            {
                yield return _owner.StartCoroutine(FinalizeFromEntries(report));
                yield break;
            }
        }

        // ── Phase 3: 轮数耗尽 → Arbiter 仲裁（1 次 LLM 调用）─────────────
        yield return _owner.StartCoroutine(ArbitrateFinal(report));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辩论协议私有方法
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>等待本轮所有成员回复，或超时后继续。</summary>
    private IEnumerator WaitForRoundResponses(
        string incidentId, int expected, int round, float timeoutSeconds)
    {
        float deadline = Time.time + timeoutSeconds;
        while (Time.time < deadline)
        {
            int got = _allEntries.Count(e => e.incidentId == incidentId && e.debateRound == round);
            if (got >= expected) yield break;
            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>
    /// 向每位成员发送角色分配消息。
    /// Round 1: 受影响 agent + leader → Proposer；其余 → Critic。
    /// Round 2+: 全员 → Voter。
    /// </summary>
    private void BroadcastRoleAssignments(IncidentReport report, int round)
    {
        if (_group.memberIds == null) return;

        string existingSummary = BuildExistingEntriesSummary(report.incidentId);

        foreach (string memberId in _group.memberIds)
        {
            DebateRole role;
            if (round == 1)
            {
                bool isAffected = memberId == report.affectedAgentId || memberId == _leaderId;
                role = isAffected ? DebateRole.Proposer : DebateRole.Critic;
            }
            else
            {
                role = DebateRole.Voter;
            }

            var assignment = new DebateRoleAssignment
            {
                incidentId             = report.incidentId,
                leaderId               = _leaderId,
                report                 = report,
                role                   = role,
                debateRound            = round,
                existingEntriesSummary = existingSummary,
            };

            _commModule?.SendStructuredMessage(memberId, MessageType.DebateProposal, assignment);
        }

        Debug.Log($"[IncidentCoordinator] Round {round} 角色分配广播完成");
    }

    /// <summary>
    /// 收敛判断：满足任一条件即收敛。
    /// 1. 投票多数：>= ceil(N/2) 支持同一提案
    /// 2. 置信集中：最高提案均值 > 0.7，第二名 < 0.5
    /// </summary>
    private bool IsConverged(string incidentId, int memberCount)
    {
        var entries = _allEntries.Where(e => e.incidentId == incidentId).ToList();
        if (entries.Count == 0) return false;

        // 条件 1：投票多数
        var votes = entries.Where(e => !string.IsNullOrWhiteSpace(e.voteFor))
                           .GroupBy(e => e.voteFor)
                           .OrderByDescending(g => g.Count())
                           .FirstOrDefault();
        if (votes != null && votes.Count() >= Mathf.CeilToInt(memberCount / 2f))
        {
            Debug.Log($"[IncidentCoordinator] 收敛：投票多数 ({votes.Count()}/{memberCount})");
            return true;
        }

        // 条件 2：置信集中（仅 Proposer 条目参与）
        var proposals = entries.Where(e => e.role == DebateRole.Proposer || e.role == DebateRole.Arbiter)
                               .OrderByDescending(e => e.confidence)
                               .ToList();
        if (proposals.Count >= 2)
        {
            float first  = proposals[0].confidence;
            float second = proposals[1].confidence;
            if (first > 0.7f && second < 0.5f)
            {
                Debug.Log($"[IncidentCoordinator] 收敛：置信集中 (max={first:F2}, 2nd={second:F2})");
                return true;
            }
        }
        else if (proposals.Count == 1 && proposals[0].confidence > 0.8f)
        {
            Debug.Log($"[IncidentCoordinator] 收敛：单一高置信提案 ({proposals[0].confidence:F2})");
            return true;
        }

        return false;
    }

    /// <summary>从已有条目中选出最佳提案并写入最终决策。</summary>
    private IEnumerator FinalizeFromEntries(IncidentReport report)
    {
        // 先找得票最多的提案
        var best = FindBestEntry(report.incidentId);
        string resolution = best?.content ?? "基于辩论共识：维持当前计划，继续观察";
        string assignedId = ExtractAssignedAgentFromText(resolution);

        yield return null; // 允许调度器处理一帧

        WriteAndBroadcastConsensus(report, resolution, assignedId);
    }

    /// <summary>Arbiter 仲裁：leader 进行最后一次 LLM 调用决定最终方案。</summary>
    private IEnumerator ArbitrateFinal(IncidentReport report)
    {
        string prompt  = BuildArbiterPrompt(report);
        string llmResult = null;

        yield return _owner.StartCoroutine(_llm.SendRequest(
            new LLMRequestOptions
            {
                prompt         = prompt,
                maxTokens      = 400,
                enableJsonMode = true,
                callTag        = $"Arbiter_{report.incidentId}",
                agentId        = _leaderId,
            },
            r => llmResult = r));

        string resolution = "仲裁：维持现有计划，等待后续评估";
        string assignedId = string.Empty;

        if (!string.IsNullOrWhiteSpace(llmResult))
        {
            try
            {
                var parsed = JsonUtility.FromJson<ArbiterResult>(llmResult.Trim());
                if (parsed != null)
                {
                    resolution = parsed.resolution ?? resolution;
                    assignedId = parsed.assignedAgentId ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IncidentCoordinator] 仲裁结果解析失败: {ex.Message}，使用默认决策");
            }
        }

        WriteAndBroadcastConsensus(report, resolution, assignedId);
    }

    /// <summary>写入白板 DebateConsensus 并广播 DebateResolved，通知 GroupMonitor 清理。</summary>
    private void WriteAndBroadcastConsensus(IncidentReport report, string resolution, string assignedAgentId)
    {
        var consensus = new DebateConsensusEntry
        {
            incidentId          = report.incidentId,
            resolution          = resolution,
            assignedAgentId     = assignedAgentId,
            missionScopeChanged = report.incidentType == IncidentType.PlanInvalid,
            decidedAt           = Time.time,
        };

        report.status                = IncidentStatus.Resolved;
        report.resolvedAt            = Time.time;
        report.finalResolutionSummary = resolution;

        WriteDebateConsensus(consensus);
        BroadcastToGroup(MessageType.DebateResolved, consensus);
        _owner.OnCoordinatorFinished(report.incidentId);

        Debug.Log($"[IncidentCoordinator] {report.incidentId} 已决策: {resolution}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 白板写入
    // ─────────────────────────────────────────────────────────────────────────

    private void WriteIncidentAnnounce(IncidentReport report)
    {
        if (SharedWhiteboard.Instance == null) return;
        SharedWhiteboard.Instance.WriteEntry(_groupId, new WhiteboardEntry
        {
            agentId      = _leaderId,
            constraintId = AnnounceConstraintId(report.incidentId),
            entryType    = WhiteboardEntryType.IncidentAnnounce,
            status       = 1,
            progress     = JsonUtility.ToJson(report),
        });
    }

    private void WriteDebateConsensus(DebateConsensusEntry consensus)
    {
        if (SharedWhiteboard.Instance == null) return;
        SharedWhiteboard.Instance.WriteEntry(_groupId, new WhiteboardEntry
        {
            agentId      = _leaderId,
            constraintId = ConsensusConstraintId(consensus.incidentId),
            entryType    = WhiteboardEntryType.DebateConsensus,
            status       = 1,
            progress     = JsonUtility.ToJson(consensus),
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 通信广播
    // ─────────────────────────────────────────────────────────────────────────

    private void BroadcastToGroup<T>(MessageType msgType, T payload)
    {
        if (_commModule == null || _group.memberIds == null) return;
        foreach (string memberId in _group.memberIds)
            _commModule.SendStructuredMessage(memberId, msgType, payload);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助方法
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>从所有条目中找出得票最多（或置信最高）的提案条目。</summary>
    private DebateEntry FindBestEntry(string incidentId)
    {
        var entries = _allEntries.Where(e => e.incidentId == incidentId).ToList();
        if (entries.Count == 0) return null;

        // 优先：得票最多
        var byVotes = entries.Where(e => !string.IsNullOrWhiteSpace(e.voteFor))
                             .GroupBy(e => e.voteFor)
                             .OrderByDescending(g => g.Count())
                             .FirstOrDefault();
        if (byVotes != null)
        {
            string bestId = byVotes.Key;
            var target = entries.FirstOrDefault(e => e.entryId == bestId);
            if (target != null) return target;
        }

        // 次选：置信最高的 Proposer
        return entries.Where(e => e.role == DebateRole.Proposer)
                      .OrderByDescending(e => e.confidence)
                      .FirstOrDefault()
               ?? entries.OrderByDescending(e => e.confidence).FirstOrDefault();
    }

    /// <summary>将已有辩论条目格式化为文本摘要，供 Round 2+ LLM 参考。</summary>
    private string BuildExistingEntriesSummary(string incidentId)
    {
        var entries = _allEntries.Where(e => e.incidentId == incidentId).ToList();
        if (entries.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLine($"[{e.entryId}] {e.authorId}（{e.role}, Round {e.debateRound}, conf={e.confidence:F2}）:");
            sb.AppendLine($"  {e.content}");
            if (!string.IsNullOrWhiteSpace(e.voteFor))
                sb.AppendLine($"  → 支持: {e.voteFor}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 从决策文本中提取被指派 agent 的 ID（简单关键词匹配）。
    /// 如果文本中包含已知成员 ID，则提取第一个。
    /// </summary>
    private string ExtractAssignedAgentFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _group.memberIds == null)
            return string.Empty;
        foreach (string memberId in _group.memberIds)
            if (text.Contains(memberId)) return memberId;
        return string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Prompt 构建
    // ─────────────────────────────────────────────────────────────────────────

    private string BuildArbiterPrompt(IncidentReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是多智能体任务仲裁器，负责基于辩论记录做出最终决策。仅输出 JSON，不解释。");
        sb.AppendLine();
        sb.AppendLine("=== 紧急事件 ===");
        sb.AppendLine($"类型：{report.incidentType}");
        sb.AppendLine($"严重程度：{report.severity}");
        sb.AppendLine($"受影响 Agent：{report.affectedAgentId ?? "无"}");
        sb.AppendLine($"受影响任务：{report.affectedTaskId ?? "无"}");
        sb.AppendLine($"描述：{report.description}");
        sb.AppendLine();

        var entries = _allEntries.Where(e => e.incidentId == report.incidentId).ToList();
        if (entries.Count > 0)
        {
            sb.AppendLine("=== 辩论记录 ===");
            sb.AppendLine(BuildExistingEntriesSummary(report.incidentId));
            sb.AppendLine();
        }

        sb.AppendLine("=== 可用成员 ===");
        if (_group.memberIds != null)
            sb.AppendLine(string.Join(", ", _group.memberIds));
        sb.AppendLine();

        sb.AppendLine("=== 输出格式（JSON） ===");
        sb.AppendLine("{");
        sb.AppendLine("  \"resolution\": \"具体可执行的最终决策（2-3句，包含执行主体和步骤）\",");
        sb.AppendLine("  \"assignedAgentId\": \"执行主体的 agentId（若无特定执行方则填空字符串）\"");
        sb.AppendLine("}");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 内部序列化辅助类
    // ─────────────────────────────────────────────────────────────────────────

    [Serializable]
    private class ArbiterResult
    {
        public string resolution;
        public string assignedAgentId;
    }
}
