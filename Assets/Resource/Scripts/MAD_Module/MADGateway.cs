// MAD_Module/MADGateway.cs
// 每个 Agent GameObject 挂一个 MADGateway 组件。
//
// 职责：
//   Raise(IncidentReport)        — 将事件路由给组长 GroupMonitor（本地直调或消息发送）
//   OnIncidentQuery(query)       — 收到 leader 查询 → 立即 StartCoroutine 响应（不等 ADM 轮次）
//   OnIncidentResolved(decision) — 收到最终决策 → MADDecisionForwarder 路由到对应模块执行
//   OnIncidentAnnounced(report)  — 收到 Critical 事件广播 → 触发 onCriticalInterrupt 回调
//
// 设计原则：辩论响应必须即时触发，不依赖 ADM Rolling Loop 的执行窗口。
// leader 的 WaitForOpinions 有超时（10s/20s），若成员等到 Step 11.5 才响应极可能已超时。
// ════════════════════════════════════════════════════════════════════════════
using UnityEngine;
using System;
using System.Collections;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

public class MADGateway : MonoBehaviour
{
    // ─── 外部依赖（Start() 时从同 GameObject 获取）──────────────────────────

    private AgentProperties      _props;
    private CommunicationModule  _comm;
    private PlanningModule       _planning;
    private LLMInterface         _llm;
    private MemoryModule         _memory;

    // ─── ADM 中断回调（由 ActionDecisionModule.Start() 注入）────────────────────

    private Action _onCriticalInterrupt;

    // ─── MAD 决策转发器（Start() 中从同 GameObject 获取）──────────────────────

    private MADDecisionForwarder _forwarder;

    // ─── 成员侧辩论状态 ──────────────────────────────────────────────────────

    /// <summary>
    /// 正在响应期间到达的下一个查询（暂存，响应完成后立即处理）。
    /// 同一 incidentId 的 Round2 查询会在 Round1 响应结束后立即处理。
    /// </summary>
    private IncidentQuery _pendingQuery;

    /// <summary>当前是否正在执行辩论响应协程（防止并发 LLM 调用）。</summary>
    private bool _isResponding;

    // ─────────────────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        _props     = GetComponent<IntelligentAgent>()?.Properties;
        _comm      = GetComponent<CommunicationModule>();
        _planning  = GetComponent<PlanningModule>();
        _llm       = FindObjectOfType<LLMInterface>();
        _memory    = GetComponent<MemoryModule>();
        _forwarder = GetComponent<MADDecisionForwarder>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 依赖注入（由 ActionDecisionModule.Start() 调用）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ActionDecisionModule 在自身 Start() 中调用，注入 ADM 中断回调。
    /// 必须在 Rolling Loop 开始前调用。
    /// 决策执行已由 MADDecisionForwarder 统一处理，此处只保留 Critical 中断回调。
    /// </summary>
    public void SetAdmCallbacks(Action onCriticalInterrupt)
    {
        _onCriticalInterrupt = onCriticalInterrupt;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 触发侧：发起辩论
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发起事件上报：路由给组长 GroupMonitor（本地直调或通过 CommunicationModule 发送）。
    /// </summary>
    public void Raise(IncidentReport report)
    {
        if (report == null) return;

        // 确保 reporterId 已填写
        if (string.IsNullOrWhiteSpace(report.reporterId))
            report.reporterId = _props?.AgentID ?? string.Empty;

        Debug.Log($"[MADGateway] {_props?.AgentID} 发起事件: " +
                  $"{report.incidentType} (critical={report.isCritical})");

        string leaderId = _planning?.GetLeaderId();

        // 自身是组长（或尚未分配组长）→ 本地直调
        if (string.IsNullOrWhiteSpace(leaderId) ||
            string.Equals(leaderId, _props?.AgentID, StringComparison.OrdinalIgnoreCase))
        {
            var gm = GetComponent<GroupMonitor>();
            if (gm != null)
            {
                gm.HandleIncident(report);
                return;
            }
            Debug.LogWarning($"[MADGateway] {_props?.AgentID} 自身为组长但未挂载 GroupMonitor，尝试广播");
        }

        // 非组长 → 通过通信模块发给 leader
        string target = string.IsNullOrWhiteSpace(leaderId) ? "All" : leaderId;
        _comm?.SendStructuredMessage(target, MessageType.IncidentReport, report);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 成员侧：接收查询与响应
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 收到 leader 发来的 IncidentQuery，立即触发响应协程。
    /// 不依赖 ADM Rolling Loop——leader 的等待超时为 10-20s，必须即时响应。
    /// 若当前已在响应中，暂存查询，待本次响应完成后立即处理。
    /// </summary>
    public void OnIncidentQuery(IncidentQuery query)
    {
        if (query == null) return;
        Debug.Log($"[MADGateway] {_props?.AgentID} 收到 Round{query.round} 查询: {query.incidentId}");

        if (_isResponding)
        {
            // 响应中（如 Round1 LLM 还未返回）：暂存，完成后立即处理
            _pendingQuery = query;
            return;
        }

        StartCoroutine(DoRespond(query));
    }

    /// <summary>
    /// 辩论响应主协程：构建提示词 → LLM → 解析意见 → 回传 leader → 存记忆。
    /// 结束后检查是否有暂存查询并继续处理。
    /// </summary>
    private IEnumerator DoRespond(IncidentQuery query)
    {
        _isResponding = true;

        if (_llm == null)
        {
            Debug.LogWarning($"[MADGateway] {_props?.AgentID} 无 LLMInterface，跳过辩论响应");
        }
        else
        {
            // 从规划模块获取当前步骤描述（作为"当前任务"注入提示词，反映实时执行状态）
            string currentTask = _planning?.GetCurrentStep()?.description ?? "无";
            string role        = _props?.Role ?? "通用";

            string prompt = MADPrompt.BuildMemberPrompt(query, _props?.AgentID ?? "unknown", role, currentTask);

            string llmResult = null;
            yield return StartCoroutine(_llm.SendRequest(prompt, r => llmResult = r, maxTokens: 400));

            MemberOpinion opinion = ParseOpinion(query, llmResult);
            if (opinion != null)
            {
                // 回传给 leader
                _comm?.SendStructuredMessage(query.leaderId, MessageType.IncidentOpinion, opinion);
                Debug.Log($"[MADGateway] {_props?.AgentID} 发出 Round{query.round} 意见: {opinion.recommendation}");

                // 写入记忆：置信度"低"的意见不值得沉淀，跳过
                // thought.suggestion 由 MemoryModule.TryExtractPolicyFromDetail 自动提炼为 Policy 记忆
                TryStoreOpinionMemory(query, opinion);
            }
        }

        _isResponding = false;

        // 响应完成后立即处理期间到达的下一个查询（通常是 Round2）
        if (_pendingQuery != null)
        {
            var next = _pendingQuery;
            _pendingQuery = null;
            StartCoroutine(DoRespond(next));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 成员侧：接收 leader 广播
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 收到 IncidentAnnounce（Critical 广播）：若 isCritical 则触发 ADM 中断回调。
    /// </summary>
    public void OnIncidentAnnounced(IncidentReport report)
    {
        if (report == null) return;
        if (report.isCritical)
        {
            Debug.Log($"[MADGateway] {_props?.AgentID} Critical 事件，触发 ADM 中断: {report.incidentId}");
            _onCriticalInterrupt?.Invoke();
        }
    }

    /// <summary>
    /// 收到最终决策（IncidentResolved）：从 directives 中找出本 agent 的指令，
    /// 交由 MADDecisionForwarder 路由到对应模块执行。
    /// </summary>
    public void OnIncidentResolved(IncidentDecision decision)
    {
        if (decision == null) return;
        string myId = _props?.AgentID;
        Debug.Log($"[MADGateway] {myId} 收到决策 {decision.incidentId}: {decision.summary}");

        // 找出属于本 agent 的 directive（一次决策中每个 agent 至多一条）
        AgentDirective directive = null;
        if (decision.directives != null)
        {
            foreach (var d in decision.directives)
            {
                if (string.Equals(d.agentId, myId, StringComparison.OrdinalIgnoreCase))
                {
                    directive = d;
                    break;
                }
            }
        }

        if (directive == null)
        {
            Debug.Log($"[MADGateway] {myId} 本次决策无针对本 agent 的指令，无需执行");
            return;
        }

        // 转发给 MADDecisionForwarder 路由执行
        if (_forwarder != null)
            _forwarder.Forward(directive);
        else
            Debug.LogWarning($"[MADGateway] {myId} 未找到 MADDecisionForwarder，无法执行决策指令");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有辅助
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析 LLM JSON 输出为 MemberOpinion（含 thought）。
    /// 使用 Newtonsoft.Json + ThoughtJsonConverter 将嵌套 thought 对象读为字符串。
    /// 失败时返回 null（调用方跳过本轮响应，不发送无效意见）。
    /// </summary>
    private MemberOpinion ParseOpinion(IncidentQuery query, string llmResult)
    {
        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"[MADGateway] {_props?.AgentID} LLM 返回空，跳过意见回传");
            return null;
        }

        try
        {
            var raw = JsonConvert.DeserializeObject<RawOpinion>(ExtractJson(llmResult));
            if (raw == null) return null;

            return new MemberOpinion
            {
                incidentId     = query.incidentId,
                agentId        = _props?.AgentID ?? string.Empty,
                round          = query.round,
                recommendation = raw.recommendation ?? string.Empty,
                confidence     = Mathf.Clamp01(raw.confidence),
                thought        = raw.thought        ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MADGateway] {_props?.AgentID} 意见解析失败: {ex.Message}");
            return null;
        }
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

    /// <summary>
    /// 决定是否将成员意见写入记忆，并执行写入。
    /// 置信度"低"时跳过：不确定的意见无参考价值，不应污染记忆库。
    /// thought.suggestion 由 MemoryModule.TryExtractPolicyFromDetail 自动提炼为 Policy 记忆。
    /// </summary>
    private void TryStoreOpinionMemory(IncidentQuery query, MemberOpinion opinion)
    {
        if (_memory == null || string.IsNullOrWhiteSpace(opinion.thought)) return;

        bool isLowConfidence = opinion.thought.Contains("\"confidence\": \"低\"") ||
                               opinion.thought.Contains("\"confidence\":\"低\"");
        if (isLowConfidence) return;

        string missionId = _planning?.GetCurrentMissionId() ?? string.Empty;
        string slotId    = _planning?.GetCurrentSlotId()    ?? string.Empty;
        string summary   = $"MAD辩论意见 [{query.incidentId} Round{query.round}]: {opinion.recommendation}";
        string detail    = $"{summary}\n[推理] {opinion.thought}";

        _memory.Remember(
            AgentMemoryKind.Decision,
            summary,
            detail,
            importance: 0.65f,
            confidence: opinion.confidence,
            sourceModule: "MADGateway",
            missionId: missionId,
            slotId: slotId,
            targetRef: query.incidentId,
            tags: new[] { "mad_debate", "member_opinion" });

        Debug.Log($"[MADGateway] {_props?.AgentID} 辩论意见已写入记忆: {summary}");
    }

    // LLM 输出的原始 JSON 结构（元数据由 ParseOpinion 补充）
    // thought 字段为嵌套 JSON 对象，ThoughtJsonConverter 将其序列化为字符串存入 string 字段
    private class RawOpinion
    {
        public string recommendation;
        public float  confidence;
        [JsonConverter(typeof(ThoughtJsonConverter))]
        public string thought;
    }
}
