// MAD_Module/MADMetrics.cs
// MAD 辩论指标收集接口 + 空实现（NullObject 模式）。
// 未来可替换为写入 JSON 日志或仪表板推送的实现。

/// <summary>
/// MAD 辩论过程指标收集接口。
/// MADGateway 持有一个 IMADMetrics 实例（默认为 NullMADMetrics）。
/// </summary>
public interface IMADMetrics
{
    /// <summary>辩论启动时记录（incidentId、触发模块、层级）。</summary>
    void RecordDebateStart(string incidentId, string triggerSource, DebateLayer layer);

    /// <summary>辩论完成时记录（incidentId、耗时秒数、是否收敛）。</summary>
    void RecordDebateEnd(string incidentId, float durationSeconds, bool converged);

    /// <summary>单轮辩论条目提交时记录（用于统计 LLM 调用次数）。</summary>
    void RecordDebateEntry(string incidentId, int round, string agentId);
}

/// <summary>
/// 空指标收集器（默认实现）：所有方法均为空操作，不产生任何副作用。
/// 当不需要指标采集时使用，避免 null 检查。
/// </summary>
public class NullMADMetrics : IMADMetrics
{
    public void RecordDebateStart(string incidentId, string triggerSource, DebateLayer layer) { }
    public void RecordDebateEnd(string incidentId, float durationSeconds, bool converged) { }
    public void RecordDebateEntry(string incidentId, int round, string agentId) { }
}
