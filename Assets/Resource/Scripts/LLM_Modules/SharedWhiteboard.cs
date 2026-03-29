// SharedWhiteboard.cs
using UnityEngine;
using System;
using System.Collections.Generic;

// ── 数据类型区 ────────────────────────────────────────────────────────────────

/// <summary>
/// 白板条目类型枚举。
/// </summary>
public enum WhiteboardEntryType
{
    IntentAnnounce, // 宣告意图（准备执行某约束关联的动作）
    ReadySignal,    // 就绪信号（C3 前置等待条件已满足）
    DoneSignal,     // 完成信号（本 Agent 完成约束关联的步骤，用于 C2 跨 Agent 同步）
    StatusUpdate,   // 通用状态更新
    RefereeNotice,  // 跨队裁判通知（组长从裁判层写入，供本队队员读取）
}

/// <summary>
/// 白板条目，代表单个 Agent 在某约束维度上的最新状态。
/// 每个 (agentId, constraintId) 组合对应逻辑上唯一的条目，写入时覆盖旧值。
/// </summary>
[Serializable]
public class WhiteboardEntry
{
    /// <summary>写入该条目的 Agent ID。</summary>
    public string agentId;

    /// <summary>写入时间（Time.time），用于超时过滤。</summary>
    public float timestamp;

    /// <summary>关联的结构化约束 ID（对应 StructuredConstraint.constraintId）。</summary>
    public string constraintId;

    /// <summary>条目类型。</summary>
    public WhiteboardEntryType entryType;

    /// <summary>就绪/完成状态（0=未就绪, 1=就绪/完成）。</summary>
    public int status;

    /// <summary>进度描述（可选，供 LLM 读取或调试日志输出）。</summary>
    public string progress;
}

// ── SharedWhiteboard MonoBehaviour ───────────────────────────────────────────

/// <summary>
/// 组内共享白板：全局单例 MonoBehaviour，各 Agent 通过它发布和读取协同状态。
///
/// 写操作：每个 Agent 只写自己 agentId 的条目，不同 Agent 互不覆盖。
///         同一 (agentId, constraintId) 覆盖最新值（last-write-wins）。
/// 读操作：支持按 (groupId, constraintId, agentId) 过滤，并自动剔除超时条目。
/// 线程安全：Unity 单线程执行，无需显式加锁。
/// </summary>
public class SharedWhiteboard : MonoBehaviour
{
    // ── 单例 ───────────────────────────────────────────────────────────────
    public static SharedWhiteboard Instance { get; private set; }

    // ── Inspector 可调参数 ──────────────────────────────────────────────────
    [Tooltip("白板条目超时阈值（秒）。超时条目在 QueryEntries/HasSignal 时被自动过滤。")]
    [SerializeField] private float defaultStaleSeconds = 10f;

    // ── 内部存储区 ─────────────────────────────────────────────────────────
    // key = groupId → 该组所有 Agent 写入的条目列表
    // 逻辑唯一键：(agentId + constraintId)；写入时按此键覆盖
    private readonly Dictionary<string, List<WhiteboardEntry>> _store
        = new Dictionary<string, List<WhiteboardEntry>>();

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

    // ── 写接口区 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 写入（或覆盖）一条白板条目。
    /// 同一 (agentId, constraintId) 组合视为同一逻辑槽，last-write-wins。
    /// timestamp 由本方法自动设置为 Time.time。
    /// </summary>
    /// <param name="groupId">所属组 ID（例如 "g0"）。</param>
    /// <param name="entry">要写入的条目（agentId 和 constraintId 必须非空）。</param>
    public void WriteEntry(string groupId, WhiteboardEntry entry)
    {
        if (string.IsNullOrWhiteSpace(groupId) || entry == null) return;

        entry.timestamp = Time.time;

        if (!_store.TryGetValue(groupId, out var list))
        {
            list = new List<WhiteboardEntry>();
            _store[groupId] = list;
        }

        // 查找同 (agentId, constraintId) 的已有条目并替换
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].agentId == entry.agentId &&
                list[i].constraintId == entry.constraintId)
            {
                list[i] = entry;
                return;
            }
        }

        list.Add(entry);
    }

    // ── 读接口区 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 查询指定组内的白板条目，支持按约束 ID 和 Agent ID 过滤，并自动过滤超时条目。
    /// </summary>
    /// <param name="groupId">组 ID（必填）。</param>
    /// <param name="constraintId">约束 ID 过滤（null 表示不过滤）。</param>
    /// <param name="agentId">Agent ID 过滤（null 表示不过滤）。</param>
    /// <param name="staleSeconds">超时阈值（秒）。传 -1 使用 defaultStaleSeconds。</param>
    /// <returns>符合条件的条目列表（浅拷贝，不会修改内部存储）。</returns>
    public List<WhiteboardEntry> QueryEntries(
        string groupId,
        string constraintId = null,
        string agentId = null,
        float staleSeconds = -1f)
    {
        float threshold = staleSeconds < 0f ? defaultStaleSeconds : staleSeconds;
        float now = Time.time;
        var result = new List<WhiteboardEntry>();

        if (!_store.TryGetValue(groupId, out var list)) return result;

        foreach (var e in list)
        {
            if (now - e.timestamp > threshold) continue;
            if (constraintId != null && e.constraintId != constraintId) continue;
            if (agentId != null && e.agentId != agentId) continue;
            result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// 快速检查某 Agent 是否在指定约束上写入了指定类型的信号（且未超时）。
    /// </summary>
    /// <param name="groupId">组 ID。</param>
    /// <param name="constraintId">约束 ID。</param>
    /// <param name="agentId">目标 Agent ID。</param>
    /// <param name="entryType">信号类型。</param>
    /// <param name="staleSeconds">超时阈值（秒）。传 -1 使用 defaultStaleSeconds。</param>
    public bool HasSignal(
        string groupId,
        string constraintId,
        string agentId,
        WhiteboardEntryType entryType,
        float staleSeconds = -1f)
    {
        float threshold = staleSeconds < 0f ? defaultStaleSeconds : staleSeconds;
        float now = Time.time;

        if (!_store.TryGetValue(groupId, out var list)) return false;

        foreach (var e in list)
        {
            if (e.agentId == agentId &&
                e.constraintId == constraintId &&
                e.entryType == entryType &&
                now - e.timestamp <= threshold)
                return true;
        }
        return false;
    }

    // ── 清理接口区 ──────────────────────────────────────────────────────────

    /// <summary>清空指定组的所有白板条目（任务结束时调用）。</summary>
    public void ClearGroup(string groupId)
    {
        _store.Remove(groupId);
        Debug.Log($"[SharedWhiteboard] 已清空组 {groupId} 的全部条目");
    }

    /// <summary>删除指定组内特定 Agent 的特定约束条目。</summary>
    public void ClearEntry(string groupId, string agentId, string constraintId)
    {
        if (!_store.TryGetValue(groupId, out var list)) return;
        int removed = list.RemoveAll(e => e.agentId == agentId && e.constraintId == constraintId);
        if (removed > 0)
            Debug.Log($"[SharedWhiteboard] 清除条目: group={groupId}, agent={agentId}, constraint={constraintId}");
    }
}
