// Enums/IncidentEnums.cs
// MAD 白板协同与紧急情况处理枚举定义。

/// <summary>
/// 紧急事件类型，覆盖所有已知场景的 4 种分类。
/// 每种类型对应清晰的响应策略，无需按具体情况列举子类型。
/// </summary>
public enum IncidentType
{
    /// <summary>Agent 无法继续执行任何任务：没电/硬件故障/被消灭/软件崩溃。响应策略：任务重新分配。</summary>
    AgentUnavailable,

    /// <summary>Agent 能继续工作但需要帮助：卡住/能力不匹配/通信降级。响应策略：派遣援助或缩减任务范围。</summary>
    AgentImpaired,

    /// <summary>计划假设不再成立：目标移动/资源被抢/环境变化。响应策略：触发（部分）重规划。</summary>
    PlanInvalid,

    /// <summary>团队整体能力不足：多人掉线后工作量失衡/时间压力。响应策略：优先级重排或跨组请援。</summary>
    CapacityShortfall,
}

/// <summary>
/// 紧急事件严重程度（自动判定，无需 LLM）。
/// </summary>
public enum IncidentSeverity
{
    /// <summary>需立即中断当前动作并参与辩论（AgentUnavailable / CapacityShortfall）。</summary>
    Critical,

    /// <summary>完成当前 action batch 后在下一窗口参与辩论。</summary>
    High,

    /// <summary>继续执行，在下一批次间隙参与辩论。</summary>
    Medium,

    /// <summary>不触发辩论，由 leader 单方决策处理。</summary>
    Low,
}

/// <summary>
/// 紧急事件处理状态。
/// </summary>
public enum IncidentStatus
{
    /// <summary>已上报，等待 leader 处理。</summary>
    Open,

    /// <summary>正在辩论中。</summary>
    Debating,

    /// <summary>辩论完成，决策已执行。</summary>
    Resolved,

    /// <summary>事件被放弃处理（如重复事件、系统关闭）。</summary>
    Abandoned,
}

/// <summary>
/// MAD 辩论中的角色分工。
/// </summary>
public enum DebateRole
{
    /// <summary>提出具体应对方案的 agent（受影响 agent / leader）。</summary>
    Proposer,

    /// <summary>批评或质疑提案的 agent（第 1 轮，非受影响成员）。</summary>
    Critic,

    /// <summary>对已有提案进行投票的 agent（第 2+ 轮）。</summary>
    Voter,

    /// <summary>在轮数上限后做最终仲裁决策的 leader。</summary>
    Arbiter,
}
