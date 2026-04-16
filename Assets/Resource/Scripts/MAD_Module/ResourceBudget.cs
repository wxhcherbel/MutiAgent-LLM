// MAD_Module/ResourceBudget.cs
// 三层辩论降级策略（当前预留，始终返回 FullLLM）。
// 当需要根据电量/紧急度/复杂度动态选择辩论成本时，在此添加规则。

/// <summary>
/// 辩论资源预算模型，决定本次辩论使用哪一层策略。
/// 由 MADGateway 在 Raise() 中创建并调用 SelectLayer()。
/// </summary>
public class ResourceBudget
{
    // ── 各层成本参考值（文档记录，当前未实际使用）────────────────────────────

    /// <summary>FullLLM 每轮 token 成本估算。</summary>
    public float tokenCostPerRound    = 800f;

    /// <summary>FullLLM 每轮 API 延迟估算（秒）。</summary>
    public float apiLatencyPerRound   = 2.0f;

    /// <summary>FullLLM 每轮电量消耗估算（%）。</summary>
    public float batteryDrainPerRound = 0.5f;

    // ── 切换阈值（预留，当前未激活）────────────────────────────────────────

    /// <summary>低于此电量时降级为 Rule 层（未激活）。</summary>
    public float minBatteryForFullDebate = 30f;

    /// <summary>紧急度超过此值时降级为 Rule 层（未激活）。</summary>
    public float maxUrgencyForFullDebate = 0.9f;

    /// <summary>复杂度 ≤ 此值时允许 JsonSummary 层（未激活）。</summary>
    public int   maxComplexityForSummary = 2;

    // ── 层级选择（入口方法）─────────────────────────────────────────────────

    /// <summary>
    /// 根据当前资源状态选择辩论层级。
    /// 当前实现始终返回 FullLLM，三层降级逻辑预留待后续激活。
    /// </summary>
    /// <param name="battery">发起方当前电量（0-100）。</param>
    /// <param name="urgency">紧急度（0-1，Critical=1.0）。</param>
    /// <param name="conflictComplexity">冲突参与方数量（影响 token 消耗）。</param>
    /// <returns>本次应使用的辩论层级。</returns>
    public DebateLayer SelectLayer(float battery, float urgency, int conflictComplexity)
    {
        // TODO：激活分层逻辑时取消注释以下代码
        // if (battery < minBatteryForFullDebate || urgency >= maxUrgencyForFullDebate)
        //     return DebateLayer.Rule;
        // if (conflictComplexity <= maxComplexityForSummary && battery < 50f)
        //     return DebateLayer.JsonSummary;
        return DebateLayer.FullLLM;
    }
}
