// MAD_Module/MADMetrics.cs
// MAD 辩论指标收集接口 + 空实现（NullObject 模式）。
// 未来可替换为写入 JSON 日志或仪表板推送的实现。

/// <summary>
/// MAD 辩论过程指标收集接口。
/// MADGateway 持有一个 IMADMetricsCollector 实例（默认为 NullMetricsCollector）。
/// </summary>
public interface IMADMetricsCollector
{
    void OnDebateStarted(string debateId, IncidentType type, DebateLayer layer);
    void OnDebateResolved(string debateId, int rounds, int totalTokens, float elapsedMs);
    void OnLayerDowngraded(string debateId, DebateLayer from, DebateLayer to, string reason);
    void OnParticipantDropped(string debateId, string agentId);
    void OnReplanTriggered(string debateId, string agentId);
}

/// <summary>
/// 空指标收集器（默认实现）：所有方法均为空操作，不产生任何副作用。
/// </summary>
public class NullMetricsCollector : IMADMetricsCollector
{
    public void OnDebateStarted(string debateId, IncidentType type, DebateLayer layer) { }
    public void OnDebateResolved(string debateId, int rounds, int totalTokens, float elapsedMs) { }
    public void OnLayerDowngraded(string debateId, DebateLayer from, DebateLayer to, string reason) { }
    public void OnParticipantDropped(string debateId, string agentId) { }
    public void OnReplanTriggered(string debateId, string agentId) { }
}
