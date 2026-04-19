// MAD_Module/MADGateway.cs
// 每个 Agent GameObject 挂一个 MADGateway 组件，实现 IMADGateway 接口。
//
// 职责：
//   Raise()                      — 将 DebateRequest 转换为 IncidentReport，路由给组长 GroupMonitor
//   HasPendingDebateRole()        — 委托给内部 DebateParticipant（成员侧辩论状态）
//   ParticipateInActiveDebates()  — 委托给内部 DebateParticipant（成员侧 LLM 辩论调用）
//   OnIncidentAnnounced / AssignDebateRole / OnDebateResolved / OnCriticalIncident
//                                — 委托给内部 DebateParticipant（CommunicationModule 路由调用）
//
// 使用方（ActionDecisionModule / IntelligentAgent / PlanningModule）
// 只需在 Start() 中 GetComponent<MADGateway>()，然后调用 IMADGateway 接口，
// 不直接持有或构造 DebateParticipant。
// ════════════════════════════════════════════════════════════════════════════
using UnityEngine;
using System;
using System.Collections;

public class MADGateway : MonoBehaviour, IMADGateway
{
    // ─── 外部依赖（Start() 时从同 GameObject 获取）──────────────────────────

    private AgentProperties      _props;
    private CommunicationModule  _comm;
    private PlanningModule       _planning;

    // ─── 内部成员侧辩论参与模块 ──────────────────────────────────────────────

    /// <summary>
    /// 成员侧辩论参与：接收角色分配、LLM 生成提案、发 DebateUpdate。
    /// 由 MADGateway 在 Start() 中构造，需在 ADM 调用 SetAdmCallbacks() 后才能处理 Critical 中断。
    /// </summary>
    private DebateParticipant _participant;

    // ─── ADM 回调（由 ActionDecisionModule.Start() 注入）─────────────────────

    private Func<bool>                    _isAdmRunning;
    private Action                        _onCriticalInterrupt;
    private Action<DebateConsensusEntry>  _onConsensusReceived;

    // ─── 初始化标志 ────────────────────────────────────────────────────────

    private bool _participantReady;

    // ─── 指标收集（默认空实现）──────────────────────────────────────────────

    private readonly IMADMetricsCollector _metrics = new NullMetricsCollector();

    // ─────────────────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        _props   = GetComponent<IntelligentAgent>()?.Properties;
        _comm    = GetComponent<CommunicationModule>();
        _planning = GetComponent<PlanningModule>();

        // DebateParticipant 构造器需要 ADM 回调；
        // 若 ADM 已在 Start() 中调用 SetAdmCallbacks()，此处回调已就绪（顺序不定）。
        // 若未调用，在 SetAdmCallbacks() 中延迟构造。
        TryBuildParticipant();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 依赖注入（由 ActionDecisionModule.Start() 调用）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ActionDecisionModule 在自身 Start() 中调用，注入 ADM 状态回调。
    /// 必须在 Rolling Loop 开始前调用，否则 DebateParticipant 无法处理 Critical 中断。
    /// </summary>
    public void SetAdmCallbacks(
        Func<bool>                   isAdmRunning,
        Action                       onCriticalInterrupt,
        Action<DebateConsensusEntry> onConsensusReceived)
    {
        _isAdmRunning        = isAdmRunning;
        _onCriticalInterrupt = onCriticalInterrupt;
        _onConsensusReceived = onConsensusReceived;
        TryBuildParticipant();
    }

    /// <summary>尝试构造 DebateParticipant（需依赖就绪后才能成功）。</summary>
    private void TryBuildParticipant()
    {
        if (_participantReady) return;

        var llm = FindObjectOfType<LLMInterface>();
        if (llm == null || _comm == null || _props == null) return;
        if (_isAdmRunning == null) return; // ADM 回调尚未注入，等待 SetAdmCallbacks

        _participant = new DebateParticipant(
            owner:               this,
            agentProps:          _props,
            comm:                _comm,
            llm:                 llm,
            isAdmRunning:        _isAdmRunning,
            onCriticalInterrupt: _onCriticalInterrupt,
            onConsensusReceived: _onConsensusReceived);

        _participantReady = true;
        Debug.Log($"[MADGateway] {_props.AgentID} DebateParticipant 已就绪");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IMADGateway 实现
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发起辩论请求：将 DebateRequest 转换为 IncidentReport 并路由给组长 GroupMonitor。
    /// 若当前 Agent 自身即组长，直接本地调用；否则通过 CommunicationModule 发消息。
    /// </summary>
    public void Raise(DebateRequest request)
    {
        if (request == null) return;

        // 构建 IncidentReport（GroupMonitor 会覆盖 incidentId / severity / reportedAt）
        var report = new IncidentReport
        {
            reporterId      = _props?.AgentID ?? string.Empty,
            affectedAgentId = request.affectedAgentId ?? string.Empty,
            affectedTaskId  = request.affectedTaskId  ?? string.Empty,
            incidentType    = request.incidentType,
            description     = string.IsNullOrWhiteSpace(request.context)
                              ? request.topic
                              : $"[{request.topic}] {request.context}",
        };

        Debug.Log($"[MADGateway] {_props?.AgentID} 发起辩论: {request.incidentType} - {request.topic}");
        _metrics.OnDebateStarted(report.reporterId + "_pending", request.incidentType, DebateLayer.FullLLM);

        string leaderId = _planning?.GetLeaderId();

        // 若自身是组长，直接调用本机 GroupMonitor
        if (string.IsNullOrWhiteSpace(leaderId) ||
            string.Equals(leaderId, _props?.AgentID, StringComparison.OrdinalIgnoreCase))
        {
            var gm = GetComponent<GroupMonitor>();
            if (gm != null)
            {
                gm.HandleIncidentReport(report);
                return;
            }
            // 降级：自身是组长但 GroupMonitor 未挂载（配置错误），尝试广播
            Debug.LogWarning($"[MADGateway] {_props?.AgentID} 自身是组长但未找到 GroupMonitor，尝试广播 IncidentReport");
        }

        // 非组长或自身是组长但本地处理失败：发给组长
        string target = string.IsNullOrWhiteSpace(leaderId) ? "All" : leaderId;
        _comm?.SendStructuredMessage(target, MessageType.IncidentReport, report);
    }

    /// <inheritdoc/>
    public bool HasPendingDebateRole()
    {
        if (!_participantReady) TryBuildParticipant();
        return _participant?.HasPendingDebateRole() ?? false;
    }

    /// <inheritdoc/>
    public IEnumerator ParticipateInActiveDebates()
    {
        if (!_participantReady) TryBuildParticipant();
        if (_participant == null) yield break;
        yield return StartCoroutine(_participant.ParticipateInActiveDebates());
    }

    /// <inheritdoc/>
    public void OnIncidentAnnounced(IncidentReport report)
    {
        if (!_participantReady) TryBuildParticipant();
        _participant?.OnIncidentAnnounced(report);
    }

    /// <inheritdoc/>
    public void AssignDebateRole(DebateRoleAssignment assignment)
    {
        if (!_participantReady) TryBuildParticipant();
        _participant?.AssignDebateRole(assignment);
    }

    /// <inheritdoc/>
    public void OnDebateResolved(DebateConsensusEntry consensus)
    {
        if (!_participantReady) TryBuildParticipant();
        _participant?.OnDebateResolved(consensus);
    }

    /// <inheritdoc/>
    public void OnCriticalIncident(IncidentReport report)
    {
        if (!_participantReady) TryBuildParticipant();
        _participant?.OnCriticalIncident(report);
    }
}
