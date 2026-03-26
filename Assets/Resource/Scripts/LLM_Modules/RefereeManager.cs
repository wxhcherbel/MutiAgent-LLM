// RefereeManager.cs
using UnityEngine;
using System;
using System.Collections.Generic;

// ── 数据类型区 ────────────────────────────────────────────────────────────────

/// <summary>
/// 裁判事件类型枚举，描述物理世界客观事实。
/// </summary>
public enum RefereeEventType
{
    ResourceClaimed,   // 某 Agent 占领/认领某资源
    ResourceReleased,  // 某 Agent 释放某资源
    AgentEliminated,   // 某 Agent 被淘汰或离线
    ConditionMet,      // 某客观条件已满足（如目标区域清空）
    MissionResult,     // 任务整体结果（胜/负/平）
}

/// <summary>
/// 单条裁判事件，由裁判逻辑广播，Agent 被动接收。
/// </summary>
[Serializable]
public class RefereeEvent
{
    /// <summary>事件唯一 ID（由 RefereeManager 在广播时自动生成）。</summary>
    public string eventId;

    /// <summary>事件类型。</summary>
    public RefereeEventType eventType;

    /// <summary>关联的约束 ID（可为空，表示全局事件）。</summary>
    public string constraintId;

    /// <summary>事件携带的额外信息（自然语言描述或 JSON 字符串）。</summary>
    public string payload;

    /// <summary>
    /// 可见的 Agent ID 列表。
    /// null 或空 = 向所有已注册 Agent 广播；否则仅投递给列表中的 Agent。
    /// </summary>
    public string[] visibleTo;

    /// <summary>事件触发时间（Time.time），由 BroadcastEvent 自动设置。</summary>
    public float timestamp;
}

// ── RefereeManager MonoBehaviour ─────────────────────────────────────────────

/// <summary>
/// 裁判管理器：全局单例 MonoBehaviour，负责广播物理世界客观事实给相关 Agent。
///
/// 使用方式：
///   - Agent 启动时调用 RegisterAgent 注册回调（被动推送）。
///   - ADM 也可在每次滚动规划迭代前调用 PollEvents 主动轮询。
///   - 裁判/物理检测逻辑调用 BroadcastEvent 宣布事实。
///   - 初期可由开发者在 Inspector 或脚本中手动调用 BroadcastEvent 进行测试。
/// </summary>
public class RefereeManager : MonoBehaviour
{
    // ── 单例 ───────────────────────────────────────────────────────────────
    public static RefereeManager Instance { get; private set; }

    // ── 注册接口区内部存储 ─────────────────────────────────────────────────
    // agentId → 事件回调
    private readonly Dictionary<string, Action<RefereeEvent>> _handlers
        = new Dictionary<string, Action<RefereeEvent>>();

    // ── 查询接口区内部存储 ─────────────────────────────────────────────────
    // 所有广播过的事件（含已读标记）
    private readonly List<RefereeEvent> _events = new List<RefereeEvent>();

    // agentId → 该 Agent 已读的 eventId 集合
    private readonly Dictionary<string, HashSet<string>> _acknowledged
        = new Dictionary<string, HashSet<string>>();

    private int _eventCounter;

    // ── Unity 生命周期 ─────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ── 注册接口区（Agent 启动时调用）─────────────────────────────────────

    /// <summary>
    /// 注册 Agent 的事件回调。每次 BroadcastEvent 后，裁判会主动推送给已注册 Agent。
    /// </summary>
    /// <param name="agentId">注册的 Agent ID。</param>
    /// <param name="handler">事件处理回调（在 Unity 主线程调用）。</param>
    public void RegisterAgent(string agentId, Action<RefereeEvent> handler)
    {
        if (string.IsNullOrWhiteSpace(agentId) || handler == null) return;
        _handlers[agentId] = handler;
        if (!_acknowledged.ContainsKey(agentId))
            _acknowledged[agentId] = new HashSet<string>();
        Debug.Log($"[RefereeManager] Agent {agentId} 已注册");
    }

    /// <summary>注销 Agent 回调（Agent 销毁或任务结束时调用）。</summary>
    public void UnregisterAgent(string agentId)
    {
        _handlers.Remove(agentId);
        Debug.Log($"[RefereeManager] Agent {agentId} 已注销");
    }

    // ── 广播接口区（裁判逻辑调用）────────────────────────────────────────

    /// <summary>
    /// 广播裁判事件。
    /// 按 visibleTo 过滤，仅投递给可见 Agent 的已注册回调。
    /// eventId 和 timestamp 由本方法自动赋值。
    /// </summary>
    /// <param name="evt">裁判事件（不需要预填 eventId / timestamp）。</param>
    public void BroadcastEvent(RefereeEvent evt)
    {
        if (evt == null) return;

        evt.eventId = $"ref_{++_eventCounter}";
        evt.timestamp = Time.time;
        _events.Add(evt);

        foreach (var kv in _handlers)
        {
            if (!IsVisibleTo(evt, kv.Key)) continue;
            try { kv.Value?.Invoke(evt); }
            catch (Exception ex)
            {
                Debug.LogError($"[RefereeManager] Agent {kv.Key} 事件处理异常: {ex.Message}");
            }
        }

        Debug.Log($"[RefereeManager] 广播事件 {evt.eventId}: {evt.eventType}" +
                  (string.IsNullOrWhiteSpace(evt.constraintId) ? "" : $", constraintId={evt.constraintId}"));
    }

    // ── 查询接口区（ADM 轮询用）──────────────────────────────────────────

    /// <summary>
    /// 轮询指定 Agent 可见且与 constraintId 匹配的未读事件列表。
    /// </summary>
    /// <param name="agentId">查询的 Agent ID。</param>
    /// <param name="constraintId">约束 ID 过滤（null 表示不过滤）。</param>
    /// <param name="since">只返回 timestamp 大于该值的事件（0 表示全部）。</param>
    /// <returns>未读且符合条件的事件列表。</returns>
    public List<RefereeEvent> PollEvents(string agentId, string constraintId = null, float since = 0f)
    {
        var result = new List<RefereeEvent>();
        if (string.IsNullOrWhiteSpace(agentId)) return result;

        if (!_acknowledged.TryGetValue(agentId, out var acked))
        {
            acked = new HashSet<string>();
            _acknowledged[agentId] = acked;
        }

        foreach (var evt in _events)
        {
            if (evt.timestamp <= since) continue;
            if (!IsVisibleTo(evt, agentId)) continue;
            if (constraintId != null && evt.constraintId != constraintId) continue;
            if (acked.Contains(evt.eventId)) continue;
            result.Add(evt);
        }
        return result;
    }

    /// <summary>
    /// 标记事件为已读，避免 PollEvents 重复返回同一事件。
    /// </summary>
    public void AcknowledgeEvent(string agentId, string eventId)
    {
        if (!_acknowledged.TryGetValue(agentId, out var acked))
        {
            acked = new HashSet<string>();
            _acknowledged[agentId] = acked;
        }
        acked.Add(eventId);
    }

    // ── 清理接口区 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 清除超过指定时间的历史事件，防止内存无限增长。
    /// 建议在任务结束或定时清理时调用。
    /// </summary>
    /// <param name="olderThanSeconds">保留最近多少秒内的事件（默认 60s）。</param>
    public void ClearEvents(float olderThanSeconds = 60f)
    {
        float cutoff = Time.time - olderThanSeconds;
        int removed = _events.RemoveAll(e => e.timestamp < cutoff);
        if (removed > 0)
            Debug.Log($"[RefereeManager] 清理 {removed} 条过期事件（{olderThanSeconds}s 之前）");
    }

    // ── 私有工具方法 ────────────────────────────────────────────────────────

    private bool IsVisibleTo(RefereeEvent evt, string agentId)
    {
        if (evt.visibleTo == null || evt.visibleTo.Length == 0) return true;
        foreach (var id in evt.visibleTo)
        {
            if (string.Equals(id, agentId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
