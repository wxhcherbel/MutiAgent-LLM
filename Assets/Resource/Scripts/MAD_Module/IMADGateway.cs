// MAD_Module/IMADGateway.cs
// MAD 模块对外的统一接口。
// 其他模块（IntelligentAgent / ActionDecisionModule / PlanningModule）
// 只需持有 IMADGateway 引用，不感知内部的 DebateParticipant / IncidentCoordinator。
using System.Collections;

/// <summary>
/// MAD（Multi-Agent Debate）网关接口。
/// 每个 Agent GameObject 挂一个 MADGateway : MonoBehaviour 实现此接口。
///
/// 用法速查：
/// <code>
/// // 发起辩论（任意模块）
/// _madGateway.Raise(new DebateRequest { incidentType=..., context="..." });
///
/// // Rolling Loop Step 11.5（ActionDecisionModule）
/// if (_madGateway.HasPendingDebateRole())
///     yield return StartCoroutine(_madGateway.ParticipateInActiveDebates());
/// </code>
/// </summary>
public interface IMADGateway
{
    /// <summary>
    /// 发起辩论请求。
    /// 内部自动完成：DebateRequest → IncidentReport → 路由给 leader GroupMonitor。
    /// 若自身即 leader，直接本地调用 GroupMonitor.HandleIncidentReport()。
    /// </summary>
    /// <param name="request">辩论请求，描述触发场景和受影响对象。</param>
    void Raise(DebateRequest request);

    /// <summary>
    /// 检查当前 Agent 是否有待处理的辩论角色（由 leader 通过 DebateProposal 消息分配）。
    /// 在 Rolling Loop Step 11.5 处轮询，决定是否进入辩论参与协程。
    /// </summary>
    bool HasPendingDebateRole();

    /// <summary>
    /// 辩论参与协程：消费一条待处理角色分配，调 LLM 生成提案，发 DebateUpdate 给 leader。
    /// 每次最多一个 LLM 调用（不阻塞超过一次 rolling 迭代）。
    /// 由 ActionDecisionModule 在辩论窗口（Step 11.5）中 yield return StartCoroutine() 调用。
    /// </summary>
    IEnumerator ParticipateInActiveDebates();

    /// <summary>
    /// 收到 IncidentAnnounce 消息时由 CommunicationModule 路由调用。
    /// Critical 事件触发快速路径（立即中断当前 action batch）。
    /// </summary>
    void OnIncidentAnnounced(IncidentReport report);

    /// <summary>
    /// 收到 DebateProposal 角色分配消息时由 CommunicationModule 路由调用。
    /// 将分配加入待处理队列，等下一个辩论窗口消费。
    /// </summary>
    void AssignDebateRole(DebateRoleAssignment assignment);

    /// <summary>
    /// 收到 DebateResolved 共识消息时由 CommunicationModule 路由调用。
    /// missionScopeChanged=true 时触发 PlanningModule.RequestReplan()。
    /// </summary>
    void OnDebateResolved(DebateConsensusEntry consensus);

    /// <summary>
    /// Critical 事件快速路径（由 OnIncidentAnnounced 内部调用，或外部直接调用）。
    /// 若 ADM 正在 Running，立即中断当前 action batch。
    /// </summary>
    void OnCriticalIncident(IncidentReport report);
}
