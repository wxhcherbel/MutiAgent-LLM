// MAD_Module/DebateParticipant.cs
// 个体智能体的 MAD（Multi-Agent Debate）参与模块。
//
// 职责分层说明：
//   DebateCoordinator — 群组级协调：广播角色、收集投票、收敛判断、最终仲裁
//   DebateParticipant — 个体级参与：接收角色分配、调 LLM 生成提案、发回辩论条目
//
// 非 MonoBehaviour；由 MADGateway（MonoBehaviour）持有并在 Start() 中初始化。
// 需要协程时通过构造器传入的 owner（MonoBehaviour）启动。
// ═══════════════════════════════════════════════════════════════════════════════
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

/// <summary>
/// 封装单个 Agent 在 MAD 辩论中的全部参与行为，包括：
/// <list type="bullet">
///   <item>接收 IncidentAnnounce / DebateProposal / DebateResolved 消息</item>
///   <item>维护待处理辩论角色队列</item>
///   <item>在 Rolling Loop 的辩论窗口（Step 11.5）中调 LLM 生成并发送辩论条目</item>
/// </list>
/// </summary>
public class DebateParticipant
{
    // ─── 依赖（由 MADGateway 在初始化时注入）────────────────────────────────

    private readonly MonoBehaviour _owner;
    private readonly AgentProperties _agentProps;
    private readonly CommunicationModule _comm;
    private readonly LLMInterface _llm;

    // ─── 回调（与 ActionDecisionModule 解耦的接口）──────────────────────────

    private readonly Func<bool> _isAdmRunning;
    private readonly Action _onCriticalInterrupt;
    private readonly Action<DebateConsensusEntry> _onConsensusReceived;

    // ─── 内部状态 ────────────────────────────────────────────────────────────

    private readonly Dictionary<string, PendingDebateInfo> _pendingDebateRoles
        = new Dictionary<string, PendingDebateInfo>();

    private DebateConsensusEntry _latestConsensus;

    // ─── JSON 提取正则 ───────────────────────────────────────────────────────

    private static readonly Regex JsonBlockRe =
        new Regex(@"```(?:json)?\s*([\s\S]*?)```");

    // ─── 内部数据类 ──────────────────────────────────────────────────────────

    private class PendingDebateInfo
    {
        public DebateRoleAssignment Assignment;
        public bool Processed;
    }

    [Serializable]
    private class DebateEntryRaw
    {
        public string content;
        public float confidence;
        public string voteFor;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 构造器
    // ─────────────────────────────────────────────────────────────────────────

    public DebateParticipant(
        MonoBehaviour owner,
        AgentProperties agentProps,
        CommunicationModule comm,
        LLMInterface llm,
        Func<bool> isAdmRunning,
        Action onCriticalInterrupt,
        Action<DebateConsensusEntry> onConsensusReceived)
    {
        _owner                = owner;
        _agentProps           = agentProps;
        _comm                 = comm;
        _llm                  = llm;
        _isAdmRunning         = isAdmRunning;
        _onCriticalInterrupt  = onCriticalInterrupt;
        _onConsensusReceived  = onConsensusReceived;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 公共接口（由 CommunicationModule 路由消息后调用）
    // ─────────────────────────────────────────────────────────────────────────

    public void OnIncidentAnnounced(IncidentReport report)
    {
        if (report == null) return;
        Debug.Log($"[Debate] {_agentProps?.AgentID} 收到事件宣告: " +
                  $"{report.incidentId} ({report.incidentType}/{report.severity})");

        if (report.severity == IncidentSeverity.Critical)
            OnCriticalIncident(report);
    }

    public void AssignDebateRole(DebateRoleAssignment assignment)
    {
        if (assignment == null || string.IsNullOrWhiteSpace(assignment.incidentId)) return;

        if (!_pendingDebateRoles.ContainsKey(assignment.incidentId))
        {
            _pendingDebateRoles[assignment.incidentId] = new PendingDebateInfo
            {
                Assignment = assignment,
                Processed  = false,
            };
            Debug.Log($"[Debate] {_agentProps?.AgentID} 收到辩论角色: " +
                      $"{assignment.role} (incident={assignment.incidentId}, round={assignment.debateRound})");
        }
    }

    public void OnDebateResolved(DebateConsensusEntry consensus)
    {
        if (consensus == null) return;

        _latestConsensus = consensus;
        Debug.Log($"[Debate] {_agentProps?.AgentID} 收到辩论共识 {consensus.incidentId}: " +
                  $"assigned={consensus.assignedAgentId}, resolution={consensus.resolution}");

        _onConsensusReceived?.Invoke(consensus);
    }

    public void OnCriticalIncident(IncidentReport report)
    {
        if (report == null || report.severity != IncidentSeverity.Critical) return;

        if (_isAdmRunning != null && _isAdmRunning())
        {
            Debug.LogWarning($"[Debate] {_agentProps?.AgentID} Critical 事件 {report.incidentId}，" +
                             $"通知 ADM 中断当前 action batch");
            _onCriticalInterrupt?.Invoke();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rolling Loop 集成接口（由 ActionDecisionModule.RunRollingLoop Step 11.5 调用）
    // ─────────────────────────────────────────────────────────────────────────

    public bool HasPendingDebateRole() => _pendingDebateRoles.Count > 0;

    public IEnumerator ParticipateInActiveDebates()
    {
        PendingDebateInfo pending = null;
        string pendingKey = null;
        foreach (var kvp in _pendingDebateRoles)
        {
            if (!kvp.Value.Processed)
            {
                pending    = kvp.Value;
                pendingKey = kvp.Key;
                break;
            }
        }
        if (pending == null) yield break;

        pending.Processed = true;
        var assignment = pending.Assignment;

        Debug.Log($"[Debate] {_agentProps?.AgentID} 参与辩论 {assignment.incidentId} " +
                  $"as {assignment.role} (round={assignment.debateRound})");

        string prompt = DebatePromptBuilder.BuildMemberPrompt(assignment, _agentProps);

        string llmResult = null;
        yield return _owner.StartCoroutine(_llm.SendRequest(
            new LLMRequestOptions
            {
                prompt         = prompt,
                maxTokens      = 350,
                enableJsonMode = true,
                callTag        = $"Debate_{assignment.incidentId}_r{assignment.debateRound}",
                agentId        = _agentProps?.AgentID,
            },
            r => llmResult = r));

        _pendingDebateRoles.Remove(pendingKey);

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"[Debate] {_agentProps?.AgentID} 辩论 LLM 返回空，本轮跳过");
            yield break;
        }

        DebateEntry entry = ParseDebateEntry(assignment, llmResult);
        if (entry != null && _comm != null)
        {
            _comm.SendStructuredMessage(assignment.leaderId, MessageType.DebateUpdate, entry);
            Debug.Log($"[Debate] {_agentProps?.AgentID} 辩论回复已发送: " +
                      $"{entry.entryId} (conf={entry.confidence:F2})");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有：LLM 结果解析
    // ─────────────────────────────────────────────────────────────────────────

    private DebateEntry ParseDebateEntry(DebateRoleAssignment assignment, string llmResult)
    {
        try
        {
            string json   = ExtractJson(llmResult);
            var    parsed = JsonConvert.DeserializeObject<DebateEntryRaw>(json);
            if (parsed == null) return null;

            string entryId = $"dbt_{assignment.incidentId}_r{assignment.debateRound}_{_agentProps?.AgentID}";

            return new DebateEntry
            {
                entryId     = entryId,
                incidentId  = assignment.incidentId,
                authorId    = _agentProps?.AgentID ?? string.Empty,
                debateRound = assignment.debateRound,
                role        = assignment.role,
                content     = parsed.content     ?? string.Empty,
                confidence  = Mathf.Clamp01(parsed.confidence),
                voteFor     = parsed.voteFor     ?? string.Empty,
                createdAt   = Time.time,
            };
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Debate] {_agentProps?.AgentID} 辩论条目解析失败: {e.Message}");
            return null;
        }
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var m = JsonBlockRe.Match(raw);
        if (m.Success) return m.Groups[1].Value.Trim();

        int start    = raw.IndexOf('{');
        int startArr = raw.IndexOf('[');
        if (startArr >= 0 && (start < 0 || startArr < start)) start = startArr;
        if (start >= 0)
        {
            char open  = raw[start];
            char close = open == '[' ? ']' : '}';
            int  end   = raw.LastIndexOf(close);
            if (end > start) return raw.Substring(start, end - start + 1);
        }

        return raw.Trim();
    }
}
