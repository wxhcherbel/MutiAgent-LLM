// MAD_Module/MADCoordinator.cs + AgentPlanRegistry
// 组长侧 MAD 辩论协调器（纯 C# 类，非 MonoBehaviour）。
// 由 GroupMonitor 持有并通过 StartCoroutine 驱动协程。
//
// 流程：
//   HandleIncident → 去重 → 赋 incidentId → RunDebate 协程
//   RunDebate:
//     1) isCritical → 广播 IncidentAnnounce（中断成员 ADM）
//     2) Round 1：广播 IncidentQuery(round=1)  → 等待成员回复（10s/20s 超时）
//     3) Round 2：广播 IncidentQuery(round=2, round1Summary=...)  → 等待成员回复
//     4) 仲裁：LLM 读两轮摘要 → 输出 IncidentDecision
//     5) 执行：统一转为 AgentDirective，逐人单播 IncidentResolved，由 MADDecisionForwarder 路由执行
// ═══════════════════════════════════════════════════════════════════════════
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 全局 Agent 规划模块注册表（静态，进程内全局共享）。
/// 每个 PlanningModule 在 Start() 中调用 Register 注册自身；
/// MADDecisionForwarder 在处理任务继承（insert_steps + fromAgentId）时通过此注册表
/// 查询源 agent 的剩余步骤，避免跨 GameObject 直接引用。
/// </summary>
public static class AgentPlanRegistry
{
    private static readonly Dictionary<string, PlanningModule> _registry
        = new Dictionary<string, PlanningModule>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 注册 agent 的 PlanningModule。由 PlanningModule.Start() 调用。
    /// </summary>
    public static void Register(string agentId, PlanningModule pm)
    {
        if (string.IsNullOrWhiteSpace(agentId) || pm == null) return;
        _registry[agentId] = pm;
    }

    /// <summary>
    /// 获取指定 agent 的剩余步骤（当前步骤及其后的所有步骤）。
    /// 若 agent 未注册或无活跃计划，返回 null。
    /// 由 MADDecisionForwarder.ResolveSteps 调用。
    /// </summary>
    public static PlanStep[] GetRemainingSteps(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        return _registry.TryGetValue(agentId, out var pm) ? pm.GetRemainingSteps() : null;
    }
}

public class MADCoordinator
{
    // ─── 依赖 ────────────────────────────────────────────────────────────────

    private readonly MonoBehaviour     _owner;     // GroupMonitor，用于 StartCoroutine
    private readonly GroupDef          _group;
    private readonly string            _groupId;
    private readonly string            _leaderId;
    private readonly LLMInterface      _llm;
    private readonly CommunicationModule _comm;
    private readonly PlanningModule    _planning;
    private readonly MemoryModule      _memory;

    // ─── 辩论状态 ────────────────────────────────────────────────────────────

    /// <summary>活跃辩论的成员意见收集箱（key = incidentId）。</summary>
    private readonly Dictionary<string, List<MemberOpinion>> _opinions
        = new Dictionary<string, List<MemberOpinion>>();

    // ─── 去重 ────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, float> _recentKeys
        = new Dictionary<string, float>();
    private const float DedupeWindow = 30f;

    // ─── 计数器 ──────────────────────────────────────────────────────────────

    private static int _counter = 0;

    // ─────────────────────────────────────────────────────────────────────────
    // 构造
    // ─────────────────────────────────────────────────────────────────────────

    public MADCoordinator(
        MonoBehaviour    owner,
        GroupDef         group,
        string           groupId,
        string           leaderId,
        LLMInterface     llm,
        CommunicationModule comm,
        PlanningModule   planning,
        MemoryModule     memory = null)
    {
        _owner    = owner;
        _group    = group;
        _groupId  = groupId;
        _leaderId = leaderId;
        _llm      = llm;
        _comm     = comm;
        _planning = planning;
        _memory   = memory;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 公开接口
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 由 GroupMonitor 收到 IncidentReport 后调用，启动辩论协程。
    /// 自动去重：30s 内相同 incidentType+reporterId 的重复上报静默丢弃。
    /// </summary>
    public void HandleIncident(IncidentReport report)
    {
        if (report == null) return;

        // 去重：同类型同上报者在短时间内不重复开启辩论
        string dedupeKey = $"{report.incidentType}_{report.reporterId}";
        if (_recentKeys.TryGetValue(dedupeKey, out float lastTime) &&
            Time.time - lastTime < DedupeWindow)
        {
            Debug.Log($"[MADCoordinator] {_leaderId} 去重跳过: {dedupeKey}");
            return;
        }
        _recentKeys[dedupeKey] = Time.time;

        // 赋予全局唯一 incidentId（格式 inc_001）
        report.incidentId = $"inc_{++_counter:D3}";
        _opinions[report.incidentId] = new List<MemberOpinion>();

        Debug.Log($"[MADCoordinator] {_leaderId} 开始辩论 {report.incidentId}: " +
                  $"{report.incidentType} (critical={report.isCritical})");

        _owner.StartCoroutine(RunDebate(report));
    }

    /// <summary>
    /// 由 CommunicationModule 路由：成员回传 MemberOpinion 时调用。
    /// </summary>
    public void AddOpinion(MemberOpinion opinion)
    {
        if (opinion == null) return;
        if (!_opinions.TryGetValue(opinion.incidentId, out var list))
        {
            Debug.LogWarning($"[MADCoordinator] {_leaderId} 收到孤立意见: " +
                             $"{opinion.incidentId} from {opinion.agentId}");
            return;
        }
        list.Add(opinion);
        Debug.Log($"[MADCoordinator] {_leaderId} 收到 Round{opinion.round} 意见: " +
                  $"{opinion.agentId} - {opinion.recommendation}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辩论协程
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator RunDebate(IncidentReport report)
    {
        float timeout    = report.isCritical ? 10f : 20f;
        int memberCount  = _group?.memberIds?.Length ?? 0;

        // ── isCritical：立即广播，通知成员中断 ADM ──────────────────────────────
        if (report.isCritical)
        {
            _comm?.SendStructuredMessage("All", MessageType.IncidentAnnounce, report);
            Debug.Log($"[MADCoordinator] {_leaderId} 广播 Critical IncidentAnnounce: {report.incidentId}");
        }

        // ── Round 1：独立提案（成员不知道他人意见）──────────────────────────────
        var query1 = new IncidentQuery
        {
            incidentId   = report.incidentId,
            leaderId     = _leaderId,
            description  = report.description,
            context      = report.context,
            round        = 1,
            round1Summary = string.Empty,
        };
        _comm?.SendStructuredMessage("All", MessageType.IncidentQuery, query1);
        Debug.Log($"[MADCoordinator] {_leaderId} 发出 Round 1 查询: {report.incidentId}");

        yield return _owner.StartCoroutine(
            WaitForOpinions(report.incidentId, round: 1, memberCount, timeout));

        string round1Summary = BuildOpinionSummary(report.incidentId, round: 1);
        Debug.Log($"[MADCoordinator] {_leaderId} Round1 摘要:\n{round1Summary}");

        // ── Round 2：参考他人意见后修正（真正的"辩论"）──────────────────────────
        var query2 = new IncidentQuery
        {
            incidentId    = report.incidentId,
            leaderId      = _leaderId,
            description   = report.description,
            context       = report.context,
            round         = 2,
            round1Summary = round1Summary,
        };
        _comm?.SendStructuredMessage("All", MessageType.IncidentQuery, query2);
        Debug.Log($"[MADCoordinator] {_leaderId} 发出 Round 2 查询: {report.incidentId}");

        yield return _owner.StartCoroutine(
            WaitForOpinions(report.incidentId, round: 2, memberCount, timeout));

        string round2Summary = BuildOpinionSummary(report.incidentId, round: 2);
        Debug.Log($"[MADCoordinator] {_leaderId} Round2 摘要:\n{round2Summary}");

        // ── 仲裁：Leader LLM 综合两轮意见，输出可执行决策 ──────────────────────
        string membersStatus = BuildMembersStatus();
        string arbiterPrompt = MADPrompt.BuildArbiterPrompt(
            report.description, report.context,
            round1Summary, round2Summary, membersStatus);

        string llmResult = null;
        if (_llm != null)
            yield return _owner.StartCoroutine(
                _llm.SendRequest(arbiterPrompt, r => llmResult = r, maxTokens: 400));

        IncidentDecision decision = ParseDecision(report.incidentId, llmResult);
        Debug.Log($"[MADCoordinator] {_leaderId} 仲裁决策: {decision.summary}");

        // ── 执行决策 ──────────────────────────────────────────────────────────
        ApplyDecision(decision, report);

        // ── 清理 ──────────────────────────────────────────────────────────────
        _opinions.Remove(report.incidentId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 等待成员回复（带超时）
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator WaitForOpinions(
        string incidentId, int round, int memberCount, float timeout)
    {
        float elapsed = 0f;
        const float pollInterval = 0.5f;

        while (elapsed < timeout)
        {
            int received = CountOpinions(incidentId, round);
            // 所有成员都回复后提前结束等待
            if (memberCount > 0 && received >= memberCount) yield break;
            yield return new WaitForSeconds(pollInterval);
            elapsed += pollInterval;
        }

        Debug.LogWarning($"[MADCoordinator] {_leaderId} Round {round} 超时 ({timeout}s)，" +
                         $"已收到 {CountOpinions(incidentId, round)}/{memberCount} 个意见");
    }

    private int CountOpinions(string incidentId, int round)
    {
        if (!_opinions.TryGetValue(incidentId, out var list)) return 0;
        return list.Count(o => o.round == round);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助：摘要构建
    // ─────────────────────────────────────────────────────────────────────────

    private string BuildOpinionSummary(string incidentId, int round)
    {
        if (!_opinions.TryGetValue(incidentId, out var list)) return "（无意见）";

        var roundOpinions = list.Where(o => o.round == round).ToList();
        if (roundOpinions.Count == 0) return "（无成员回复）";

        return string.Join("\n", roundOpinions.Select(o =>
            $"- {o.agentId}（置信度 {o.confidence:F1}）: {o.recommendation}"));
    }

    /// <summary>
    /// 构建成员状态字符串（供仲裁 LLM 参考）。
    /// 从白板中读取各成员最新状态，若无白板数据则仅列出 ID。
    /// </summary>
    private string BuildMembersStatus()
    {
        if (_group?.memberIds == null || _group.memberIds.Length == 0)
            return "（成员列表为空）";

        var parts = new List<string>();
        var entries = SharedWhiteboard.Instance?.QueryEntries(_groupId);

        foreach (string memberId in _group.memberIds)
        {
            string status = "状态未知";
            if (entries != null)
            {
                // 取该成员最新的进度条目作为状态描述
                var latest = entries
                    .Where(e => e.agentId == memberId && !string.IsNullOrWhiteSpace(e.progress))
                    .OrderByDescending(e => e.status)
                    .FirstOrDefault();
                if (latest != null)
                    status = latest.progress;
            }
            parts.Add($"{memberId}({status})");
        }

        return string.Join(", ", parts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助：LLM 输出解析
    // ─────────────────────────────────────────────────────────────────────────

    private IncidentDecision ParseDecision(string incidentId, string llmResult)
    {
        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"[MADCoordinator] {_leaderId} 仲裁 LLM 返回空，使用降级决策");
            return FallbackDecision(incidentId, "LLM 无回复");
        }

        try
        {
            // 使用 Newtonsoft.Json + ThoughtJsonConverter 正确处理嵌套 thought 对象
            var decision = JsonConvert.DeserializeObject<IncidentDecision>(ExtractJson(llmResult));
            if (decision != null)
            {
                decision.incidentId = incidentId;
                return decision;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MADCoordinator] {_leaderId} 仲裁决策解析失败: {ex.Message}");
        }

        return FallbackDecision(incidentId, "JSON 解析失败");
    }

    /// <summary>从 LLM 原始输出中提取 JSON 字符串（兼容 ```json ... ``` 包裹）。</summary>
    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var codeBlock = Regex.Match(raw, @"```(?:json)?\s*([\s\S]*?)```");
        if (codeBlock.Success) return codeBlock.Groups[1].Value.Trim();
        int start = raw.IndexOf('{');
        if (start >= 0)
        {
            int end = raw.LastIndexOf('}');
            if (end > start) return raw.Substring(start, end - start + 1);
        }
        return raw.Trim();
    }

    private static IncidentDecision FallbackDecision(string incidentId, string reason)
        => new IncidentDecision
        {
            incidentId     = incidentId,
            summary        = $"降级决策：{reason}",
            requiresReplan = false,
            replanHint     = string.Empty,
            directives     = Array.Empty<AgentDirective>(),
        };

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助：执行决策
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplyDecision(IncidentDecision decision, IncidentReport report)
    {
        Debug.Log($"[MADCoordinator] {_leaderId} 执行决策 {decision.incidentId}: summary={decision.summary}");

        // ── 输出 thought（可追溯性）──────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(decision.thought))
            Debug.Log($"[MADCoordinator] {_leaderId} [Thought] {decision.thought}");

        // ── 兼容：将 requiresReplan=true 转换为统一的 directive 格式 ─────────────
        // requiresReplan 是 LLM 输出的高层标志，此处转换为 targetModule="planning" 的
        // request_replan 指令，之后所有路径统一走 MADDecisionForwarder，无需额外分支。
        if (decision.requiresReplan && _group?.memberIds != null)
        {
            string hint = string.IsNullOrWhiteSpace(decision.replanHint)
                ? $"MAD决策：{decision.summary}"
                : decision.replanHint;

            string replanPayload = $"{{\"operation\":\"request_replan\",\"replanHint\":{JsonConvert.SerializeObject(hint)}}}";

            var replanDirectives = _group.memberIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => new AgentDirective
                {
                    agentId      = id,
                    instruction  = hint,
                    targetModule = "planning",
                    payload      = replanPayload,
                })
                .ToArray();

            // 合并到 directives（LLM 此时通常给出空 directives）
            decision.directives = (decision.directives ?? Array.Empty<AgentDirective>())
                .Concat(replanDirectives)
                .ToArray();

            Debug.Log($"[MADCoordinator] {_leaderId} requiresReplan=true → 已为 " +
                      $"{replanDirectives.Length} 个成员生成 request_replan 指令");
        }

        // ── 单播各 agent 的 directive ──────────────────────────────────────────
        if (decision.directives != null)
        {
            foreach (var directive in decision.directives)
            {
                if (string.IsNullOrWhiteSpace(directive.agentId)) continue;
                // 发送完整决策对象；成员端 MADGateway.OnIncidentResolved 会筛选自身 directive
                _comm?.SendStructuredMessage(
                    directive.agentId, MessageType.IncidentResolved, decision);
                Debug.Log($"[MADCoordinator] {_leaderId} → {directive.agentId} " +
                          $"[{directive.targetModule}/{ExtractOperation(directive.payload)}]: " +
                          $"{directive.instruction}");
            }
        }

        // ── 广播完整决策供全组知晓（白板更新、监控端采集等）──────────────────────
        _comm?.SendStructuredMessage("All", MessageType.IncidentResolved, decision);

        // ── 写入记忆：MAD 仲裁决策属于高价值协调经验，始终存储 ─────────────────
        // thought.suggestion 由 MemoryModule.TryExtractPolicyFromDetail 自动提炼为 Policy 记忆
        if (_memory != null)
        {
            string missionId = _planning?.GetCurrentMissionId() ?? string.Empty;
            string slotId    = _planning?.GetCurrentSlotId()    ?? string.Empty;
            string summary   = $"MAD仲裁决策 [{decision.incidentId}|{report.incidentType}]: {decision.summary}";
            string detail    = string.IsNullOrWhiteSpace(decision.thought)
                ? summary
                : $"{summary}\n[推理] {decision.thought}";

            _memory.Remember(
                AgentMemoryKind.Decision,
                summary,
                detail,
                importance: 0.82f,  // 高于成员意见（0.65f），协调决策更具参考价值
                confidence: 0.80f,
                sourceModule: "MADCoordinator",
                missionId: missionId,
                slotId: slotId,
                targetRef: decision.incidentId,
                tags: new[] { "mad_decision", "arbiter", report.incidentType });

            Debug.Log($"[MADCoordinator] {_leaderId} 仲裁决策已写入记忆: {summary}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有辅助
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 payload JSON 字符串中提取 operation 字段值，用于日志输出。
    /// 解析失败时返回空字符串，不影响执行流程。
    /// </summary>
    private static string ExtractOperation(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return string.Empty;
        try
        {
            var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(payload);
            return obj != null && obj.TryGetValue("operation", out var op) ? op?.ToString() ?? "" : "";
        }
        catch { return string.Empty; }
    }
}
