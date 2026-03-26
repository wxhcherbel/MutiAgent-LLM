using System;

/// <summary>
/// 触发反思的原因类型。
/// </summary>
[Serializable]
public enum ReflectionTriggerReason
{
    Manual = 0,
    MissionCompleted = 1,
    RepeatedFailure = 2,
    Blocked = 3,
    ImportantObservation = 4,
    CoordinationIssue = 5
}

/// <summary>
/// 单次反思请求。
/// </summary>
[Serializable]
public class ReflectionRequest
{
    /// <summary>触发本次反思的原因。</summary>
    public ReflectionTriggerReason reason;

    /// <summary>关联任务 ID。</summary>
    public string missionId;

    /// <summary>关联任务文本。</summary>
    public string missionText;

    /// <summary>关联槽位 ID。</summary>
    public string slotId;

    /// <summary>关联步骤文本。</summary>
    public string stepText;

    /// <summary>关联目标引用。</summary>
    public string targetRef;

    /// <summary>本次反思的事件摘要。</summary>
    public string summary;

    /// <summary>最多抽取多少条源记忆。</summary>
    public int maxSourceMemories = 8;

    /// <summary>是否忽略冷却时间强制执行反思。</summary>
    public bool force;
}
