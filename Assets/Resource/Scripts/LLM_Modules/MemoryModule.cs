using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// 记忆类型：
/// 把“看到了什么、打算做什么、做完结果如何、反思出了什么规则”分开存，
/// 后面规划和执行就能按用途取，而不是只拿到一串混在一起的文本。
/// </summary>
[Serializable]
public enum AgentMemoryKind
{
    Observation = 0,
    Goal = 1,
    Plan = 2,
    Decision = 3,
    Outcome = 4,
    Coordination = 5,
    Reflection = 6,
    Policy = 7,
    WorldState = 8,
    Relationship = 9
}

[Serializable]
public enum AgentMemoryStatus
{
    Active = 0,
    Completed = 1,
    Failed = 2,
    Archived = 3
}

/// <summary>
/// 单条记忆：
/// 这里不再只是存一句描述，而是连任务、槽位、步骤、目标引用、标签、关联对象一起存。
/// 这样以后做“按任务/按目标/按失败原因”检索时就有结构可用。
/// </summary>
[Serializable]
public class Memory
{
    public string id;
    public AgentMemoryKind kind;
    public string summary;
    public string detail;
    public DateTime createdAt;
    public DateTime lastAccessedAt;
    public float importance;
    public float confidence;
    public AgentMemoryStatus status;
    public string sourceModule;
    public string missionId;
    public string slotId;
    public string stepLabel;
    public string targetRef;
    public string outcome;
    public List<string> tags = new List<string>();
    public List<string> relatedAgentIds = new List<string>();
    public List<string> relatedEntityRefs = new List<string>();
    public List<string> derivedFromMemoryIds = new List<string>();
    public int accessCount;
    public bool isProceduralHint;
}

[Serializable]
public class MemoryQuery
{
    public string freeText;
    public string missionId;
    public string slotId;
    public string stepLabel;
    public string targetRef;
    public AgentMemoryKind[] kinds;
    public List<string> tags = new List<string>();
    public List<string> relatedAgentIds = new List<string>();
    public List<string> relatedEntityRefs = new List<string>();
    public int maxCount = 5;
    public bool preferProceduralHints;
}

[Serializable]
public class PlanningMemoryContextRequest
{
    public string missionText;
    public string missionId;
    public string teamObjective;
    public string roleName;
    public string slotId;
    public string slotLabel;
    public string slotTarget;
    public string[] viaTargets;
    public int maxMemories = 4;
    public int maxInsights = 2;
}

[Serializable]
public class ActionMemoryContextRequest
{
    public string missionText;
    public string missionId;
    public string slotId;
    public string stepText;
    public string targetRef;
    public string stepIntentSummary;
    public string coordinationSummary;
    public int maxMemories = 3;
    public int maxInsights = 2;
}

[Serializable]
public class ReflectionInsight
{
    public string id;
    public string title;
    public string summary;
    public string applyWhen;
    public string suggestedAdjustment;
    public string missionId;
    public string slotId;
    public string targetRef;
    public DateTime createdAt;
    public DateTime expiresAt;
    public float confidence;
    public List<string> tags = new List<string>();
    public List<string> sourceMemoryIds = new List<string>();
}

public class MemoryModule : MonoBehaviour
{
    [Header("记忆容量")]
    [Min(32)] public int maxMemoryCount = 512;
    [Min(8)] public int maxReflectionInsightCount = 64;
    [Min(1f)] public float freshnessWindowHours = 72f;

    public List<Memory> memories = new List<Memory>();
    public List<ReflectionInsight> reflectionInsights = new List<ReflectionInsight>();

    private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{Nd}_]+", RegexOptions.Compiled);

    private void Awake()
    {
        PruneExpiredInsights();
        TrimMemoryCapacity();
    }

    /// <summary>
    /// 统一入库入口：
    /// 所有记忆都会先在这里补齐默认值、去掉空字段、做容量裁剪，
    /// 保证后面的检索逻辑面对的是稳定结构，而不是半成品数据。
    /// </summary>
    public Memory StoreMemory(Memory memory)
    {
        if (memory == null) return null;

        memory.id = string.IsNullOrWhiteSpace(memory.id) ? Guid.NewGuid().ToString("N") : memory.id.Trim();
        memory.summary = CleanText(memory.summary);
        memory.detail = CleanText(memory.detail);
        memory.targetRef = CleanText(memory.targetRef);
        memory.missionId = CleanText(memory.missionId);
        memory.slotId = CleanText(memory.slotId);
        memory.stepLabel = CleanText(memory.stepLabel);
        memory.outcome = CleanText(memory.outcome);
        memory.sourceModule = string.IsNullOrWhiteSpace(memory.sourceModule) ? "Unknown" : memory.sourceModule.Trim();
        memory.createdAt = memory.createdAt == default ? DateTime.UtcNow : memory.createdAt;
        memory.lastAccessedAt = memory.lastAccessedAt == default ? memory.createdAt : memory.lastAccessedAt;
        memory.importance = Mathf.Clamp01(memory.importance <= 0f ? 0.5f : memory.importance);
        memory.confidence = Mathf.Clamp01(memory.confidence <= 0f ? 0.5f : memory.confidence);
        memory.status = memory.status;
        memory.tags = NormalizeList(memory.tags);
        memory.relatedAgentIds = NormalizeList(memory.relatedAgentIds);
        memory.relatedEntityRefs = NormalizeList(memory.relatedEntityRefs);
        memory.derivedFromMemoryIds = NormalizeList(memory.derivedFromMemoryIds);

        memories.Add(memory);
        TrimMemoryCapacity();
        return memory;
    }

    public Memory Remember(
        AgentMemoryKind kind,
        string summary,
        string detail = "",
        float importance = 0.5f,
        float confidence = 0.7f,
        AgentMemoryStatus status = AgentMemoryStatus.Active,
        string sourceModule = "Unknown",
        string missionId = "",
        string slotId = "",
        string stepLabel = "",
        string targetRef = "",
        string outcome = "",
        IEnumerable<string> tags = null,
        IEnumerable<string> relatedAgentIds = null,
        IEnumerable<string> relatedEntityRefs = null,
        IEnumerable<string> derivedFromMemoryIds = null,
        bool isProceduralHint = false)
    {
        Memory memory = new Memory
        {
            kind = kind,
            summary = summary,
            detail = detail,
            importance = importance,
            confidence = confidence,
            status = status,
            sourceModule = sourceModule,
            missionId = missionId,
            slotId = slotId,
            stepLabel = stepLabel,
            targetRef = targetRef,
            outcome = outcome,
            tags = tags != null ? new List<string>(tags) : new List<string>(),
            relatedAgentIds = relatedAgentIds != null ? new List<string>(relatedAgentIds) : new List<string>(),
            relatedEntityRefs = relatedEntityRefs != null ? new List<string>(relatedEntityRefs) : new List<string>(),
            derivedFromMemoryIds = derivedFromMemoryIds != null ? new List<string>(derivedFromMemoryIds) : new List<string>(),
            isProceduralHint = isProceduralHint
        };

        return StoreMemory(memory);
    }

    public Memory RememberMissionAssignment(MissionAssignment mission, RoleType role, MissionTaskSlot slot, CommunicationMode commMode)
    {
        string target = slot != null ? slot.target : string.Empty;
        string via = slot != null && slot.viaTargets != null && slot.viaTargets.Length > 0
            ? string.Join("|", slot.viaTargets.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray())
            : "none";

        return Remember(
            AgentMemoryKind.Goal,
            $"接受任务并进入岗位 {role}",
            $"mission={mission?.missionDescription}; role={role}; commMode={commMode}; slot={slot?.slotLabel}; target={target}; via={via}",
            importance: 0.92f,
            confidence: 0.9f,
            status: AgentMemoryStatus.Active,
            sourceModule: "PlanningModule",
            missionId: mission != null ? mission.missionId : string.Empty,
            slotId: slot != null ? slot.slotId : string.Empty,
            targetRef: target,
            tags: BuildTagSet(role.ToString(), slot != null ? slot.slotLabel : string.Empty, target, via));
    }

    public Memory RememberPlanSnapshot(
        string missionId,
        string slotId,
        string stepLabel,
        string planSummary,
        string targetRef,
        IEnumerable<string> tags = null)
    {
        return Remember(
            AgentMemoryKind.Plan,
            $"生成执行计划: {stepLabel}",
            planSummary,
            importance: 0.72f,
            confidence: 0.75f,
            status: AgentMemoryStatus.Active,
            sourceModule: "PlanningModule",
            missionId: missionId,
            slotId: slotId,
            stepLabel: stepLabel,
            targetRef: targetRef,
            tags: tags);
    }

    public Memory RememberProgress(
        string missionId,
        string slotId,
        string stepLabel,
        string summary,
        string targetRef = "",
        float importance = 0.68f)
    {
        return Remember(
            AgentMemoryKind.Outcome,
            summary,
            summary,
            importance: importance,
            confidence: 0.8f,
            status: AgentMemoryStatus.Completed,
            sourceModule: "PlanningModule",
            missionId: missionId,
            slotId: slotId,
            stepLabel: stepLabel,
            targetRef: targetRef,
            outcome: "progress");
    }

    public Memory RememberActionExecution(
        string missionId,
        string slotId,
        string stepLabel,
        string actionType,
        string targetRef,
        bool success,
        string detail)
    {
        return Remember(
            AgentMemoryKind.Decision,
            $"执行动作 {actionType} {(success ? "成功" : "失败")}",
            detail,
            importance: success ? 0.62f : 0.86f,
            confidence: success ? 0.8f : 0.92f,
            status: success ? AgentMemoryStatus.Completed : AgentMemoryStatus.Failed,
            sourceModule: "ActionDecisionModule",
            missionId: missionId,
            slotId: slotId,
            stepLabel: stepLabel,
            targetRef: targetRef,
            outcome: success ? "success" : "failure",
            tags: BuildTagSet(actionType, success ? "success" : "failure"),
            relatedEntityRefs: BuildTagSet(targetRef));
    }

    public Memory RememberObservation(
        string missionId,
        string slotId,
        string stepLabel,
        string summary,
        string detail,
        string targetRef,
        IEnumerable<string> entityRefs = null)
    {
        return Remember(
            AgentMemoryKind.Observation,
            summary,
            detail,
            importance: 0.75f,
            confidence: 0.7f,
            status: AgentMemoryStatus.Active,
            sourceModule: "PerceptionModule",
            missionId: missionId,
            slotId: slotId,
            stepLabel: stepLabel,
            targetRef: targetRef,
            relatedEntityRefs: entityRefs,
            tags: BuildTagSet(targetRef, stepLabel));
    }

    public Memory RememberMissionOutcome(string missionId, string slotId, string summary, bool success, string targetRef = "")
    {
        return Remember(
            AgentMemoryKind.Outcome,
            summary,
            summary,
            importance: success ? 0.94f : 0.96f,
            confidence: 0.9f,
            status: success ? AgentMemoryStatus.Completed : AgentMemoryStatus.Failed,
            sourceModule: "PlanningModule",
            missionId: missionId,
            slotId: slotId,
            targetRef: targetRef,
            outcome: success ? "mission_success" : "mission_failure");
    }

    /// <summary>
    /// 统一检索入口：
    /// 不靠硬编码地点词表，而是按“任务ID、槽位、步骤、目标、标签、自由文本”综合打分。
    /// </summary>
    public List<Memory> Recall(MemoryQuery query)
    {
        if (query == null) query = new MemoryQuery();
        PruneExpiredInsights();

        IEnumerable<Memory> candidates = memories.Where(m => m != null);
        if (query.kinds != null && query.kinds.Length > 0)
        {
            HashSet<AgentMemoryKind> kindSet = new HashSet<AgentMemoryKind>(query.kinds);
            candidates = candidates.Where(m => kindSet.Contains(m.kind));
        }

        List<Memory> ranked = candidates
            .Select(m => new { Memory = m, Score = ScoreMemory(m, query) })
            .Where(x => x.Score > 0.01f)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Memory.importance)
            .ThenByDescending(x => x.Memory.createdAt)
            .Take(Mathf.Max(1, query.maxCount))
            .Select(x => x.Memory)
            .ToList();

        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < ranked.Count; i++)
        {
            ranked[i].accessCount++;
            ranked[i].lastAccessedAt = now;
        }

        return ranked;
    }

    public void RegisterReflectionInsight(ReflectionInsight insight)
    {
        if (insight == null) return;

        insight.id = string.IsNullOrWhiteSpace(insight.id) ? Guid.NewGuid().ToString("N") : insight.id.Trim();
        insight.title = CleanText(insight.title);
        insight.summary = CleanText(insight.summary);
        insight.applyWhen = CleanText(insight.applyWhen);
        insight.suggestedAdjustment = CleanText(insight.suggestedAdjustment);
        insight.missionId = CleanText(insight.missionId);
        insight.slotId = CleanText(insight.slotId);
        insight.targetRef = CleanText(insight.targetRef);
        insight.createdAt = insight.createdAt == default ? DateTime.UtcNow : insight.createdAt;
        insight.expiresAt = insight.expiresAt == default ? insight.createdAt.AddHours(12) : insight.expiresAt;
        insight.confidence = Mathf.Clamp01(insight.confidence <= 0f ? 0.7f : insight.confidence);
        insight.tags = NormalizeList(insight.tags);
        insight.sourceMemoryIds = NormalizeList(insight.sourceMemoryIds);

        reflectionInsights.RemoveAll(r => r != null && r.id == insight.id);
        reflectionInsights.Add(insight);
        TrimInsightCapacity();

        Remember(
            AgentMemoryKind.Reflection,
            string.IsNullOrWhiteSpace(insight.title) ? insight.summary : insight.title,
            $"summary={insight.summary}; when={insight.applyWhen}; adjustment={insight.suggestedAdjustment}",
            importance: 0.9f,
            confidence: insight.confidence,
            status: AgentMemoryStatus.Active,
            sourceModule: "ReflectionModule",
            missionId: insight.missionId,
            slotId: insight.slotId,
            targetRef: insight.targetRef,
            derivedFromMemoryIds: insight.sourceMemoryIds,
            tags: insight.tags,
            isProceduralHint: true);
    }

    public List<ReflectionInsight> GetRelevantInsights(MemoryQuery query, int maxCount = 2)
    {
        if (query == null) query = new MemoryQuery();
        PruneExpiredInsights();

        return reflectionInsights
            .Where(r => r != null)
            .Select(r => new { Insight = r, Score = ScoreInsight(r, query) })
            .Where(x => x.Score > 0.01f)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Insight.confidence)
            .ThenByDescending(x => x.Insight.createdAt)
            .Take(Mathf.Max(1, maxCount))
            .Select(x => x.Insight)
            .ToList();
    }

    /// <summary>
    /// 给 Planning 用的历史上下文：
    /// 重点返回过去类似任务的目标、结果和可复用规则，帮助它不是只看当前一句任务文本。
    /// </summary>
    public string BuildPlanningContext(PlanningMemoryContextRequest request)
    {
        if (request == null) return "无稳定历史经验";

        MemoryQuery query = new MemoryQuery
        {
            freeText = BuildCombinedText(
                request.missionText,
                request.teamObjective,
                request.roleName,
                request.slotLabel,
                request.slotTarget,
                request.viaTargets != null ? string.Join(" ", request.viaTargets) : string.Empty),
            missionId = request.missionId,
            slotId = request.slotId,
            targetRef = request.slotTarget,
            tags = BuildTagSet(request.roleName, request.slotLabel, request.slotTarget),
            kinds = new[]
            {
                AgentMemoryKind.Goal,
                AgentMemoryKind.Plan,
                AgentMemoryKind.Outcome,
                AgentMemoryKind.Reflection,
                AgentMemoryKind.Policy
            },
            maxCount = Mathf.Max(1, request.maxMemories),
            preferProceduralHints = true
        };

        List<Memory> relevantMemories = Recall(query);
        List<ReflectionInsight> insights = GetRelevantInsights(query, Mathf.Max(1, request.maxInsights));
        return BuildContextSummary(relevantMemories, insights, "无稳定历史经验");
    }

    /// <summary>
    /// 给 Action 用的历史上下文：
    /// 重点返回当前步骤附近最相关的动作经验和反思提示，帮助执行层少犯重复错误。
    /// </summary>
    public string BuildActionContext(ActionMemoryContextRequest request)
    {
        if (request == null) return "无相关历史经验";

        MemoryQuery query = new MemoryQuery
        {
            freeText = BuildCombinedText(
                request.missionText,
                request.stepText,
                request.targetRef,
                request.stepIntentSummary,
                request.coordinationSummary),
            missionId = request.missionId,
            slotId = request.slotId,
            stepLabel = request.stepText,
            targetRef = request.targetRef,
            tags = BuildTagSet(request.stepText, request.targetRef),
            kinds = new[]
            {
                AgentMemoryKind.Decision,
                AgentMemoryKind.Observation,
                AgentMemoryKind.Outcome,
                AgentMemoryKind.Reflection,
                AgentMemoryKind.Policy
            },
            maxCount = Mathf.Max(1, request.maxMemories),
            preferProceduralHints = true
        };

        List<Memory> relevantMemories = Recall(query);
        List<ReflectionInsight> insights = GetRelevantInsights(query, Mathf.Max(1, request.maxInsights));
        return BuildContextSummary(relevantMemories, insights, "无相关历史经验");
    }

    public string BuildReflectionInput(MemoryQuery query, int maxMemories = 8)
    {
        if (query == null) query = new MemoryQuery();
        query.maxCount = Mathf.Max(1, maxMemories);

        List<Memory> relevant = Recall(query);
        if (relevant.Count == 0) return string.Empty;

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < relevant.Count; i++)
        {
            Memory memory = relevant[i];
            builder.Append("- [")
                .Append(memory.kind)
                .Append("] ")
                .Append(memory.summary);

            if (!string.IsNullOrWhiteSpace(memory.targetRef))
            {
                builder.Append(" | target=").Append(memory.targetRef);
            }

            if (!string.IsNullOrWhiteSpace(memory.outcome))
            {
                builder.Append(" | outcome=").Append(memory.outcome);
            }

            if (!string.IsNullOrWhiteSpace(memory.detail))
            {
                builder.Append(" | detail=").Append(memory.detail);
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    // 兼容旧调用：现在不再生成关键词，而是直接按结构化字段入记忆。
    public void AddMemory(string description, string type, float importance = 0.5f)
    {
        AgentMemoryKind kind = ParseLegacyKind(type);
        Remember(
            kind,
            description,
            description,
            importance: importance,
            confidence: 0.7f,
            sourceModule: "Legacy");
    }

    // 兼容旧调用：对外仍返回 Memory 列表，但内部已改成结构化检索。
    public List<Memory> RetrieveRelevantMemories(string query, int maxCount = 5)
    {
        return Recall(new MemoryQuery
        {
            freeText = query,
            maxCount = maxCount,
            preferProceduralHints = true
        });
    }

    private float ScoreMemory(Memory memory, MemoryQuery query)
    {
        if (memory == null) return 0f;

        float score = memory.importance * 4f + memory.confidence * 2f;
        if (query.preferProceduralHints && memory.isProceduralHint) score += 1.5f;

        if (!string.IsNullOrWhiteSpace(query.missionId) &&
            string.Equals(memory.missionId, query.missionId, StringComparison.OrdinalIgnoreCase))
        {
            score += 3.5f;
        }

        if (!string.IsNullOrWhiteSpace(query.slotId) &&
            string.Equals(memory.slotId, query.slotId, StringComparison.OrdinalIgnoreCase))
        {
            score += 2.5f;
        }

        if (!string.IsNullOrWhiteSpace(query.stepLabel) &&
            string.Equals(memory.stepLabel, query.stepLabel, StringComparison.OrdinalIgnoreCase))
        {
            score += 2f;
        }

        if (!string.IsNullOrWhiteSpace(query.targetRef))
        {
            if (string.Equals(memory.targetRef, query.targetRef, StringComparison.OrdinalIgnoreCase))
            {
                score += 3f;
            }
            else if (memory.relatedEntityRefs.Any(r => string.Equals(r, query.targetRef, StringComparison.OrdinalIgnoreCase)))
            {
                score += 2f;
            }
        }

        score += OverlapScore(query.tags, memory.tags, 0.9f);
        score += OverlapScore(query.relatedAgentIds, memory.relatedAgentIds, 1.0f);
        score += OverlapScore(query.relatedEntityRefs, memory.relatedEntityRefs, 1.0f);
        score += ComputeTextSimilarity(query.freeText, BuildSearchableText(memory));

        double ageHours = Math.Max(0d, (DateTime.UtcNow - memory.createdAt).TotalHours);
        float freshness = 1f / (1f + (float)(ageHours / Mathf.Max(1f, freshnessWindowHours)));
        score += freshness * 2f;

        if (memory.status == AgentMemoryStatus.Failed) score += 0.3f;
        return score;
    }

    private float ScoreInsight(ReflectionInsight insight, MemoryQuery query)
    {
        if (insight == null) return 0f;

        float score = insight.confidence * 4f;
        if (!string.IsNullOrWhiteSpace(query.missionId) &&
            string.Equals(insight.missionId, query.missionId, StringComparison.OrdinalIgnoreCase))
        {
            score += 3f;
        }

        if (!string.IsNullOrWhiteSpace(query.slotId) &&
            string.Equals(insight.slotId, query.slotId, StringComparison.OrdinalIgnoreCase))
        {
            score += 2f;
        }

        if (!string.IsNullOrWhiteSpace(query.targetRef) &&
            string.Equals(insight.targetRef, query.targetRef, StringComparison.OrdinalIgnoreCase))
        {
            score += 2.5f;
        }

        score += OverlapScore(query.tags, insight.tags, 0.8f);
        score += ComputeTextSimilarity(
            query.freeText,
            BuildCombinedText(insight.title, insight.summary, insight.applyWhen, insight.suggestedAdjustment));

        double ageHours = Math.Max(0d, (DateTime.UtcNow - insight.createdAt).TotalHours);
        score += 1f / (1f + (float)(ageHours / 24f));
        return score;
    }

    private string BuildContextSummary(List<Memory> relevantMemories, List<ReflectionInsight> insights, string fallback)
    {
        if ((relevantMemories == null || relevantMemories.Count == 0) &&
            (insights == null || insights.Count == 0))
        {
            return fallback;
        }

        StringBuilder builder = new StringBuilder();
        if (relevantMemories != null && relevantMemories.Count > 0)
        {
            builder.Append("经验:");
            for (int i = 0; i < relevantMemories.Count; i++)
            {
                Memory memory = relevantMemories[i];
                builder.Append("\n- [")
                    .Append(memory.kind)
                    .Append("] ")
                    .Append(memory.summary);
            }
        }

        if (insights != null && insights.Count > 0)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append("反思提示:");
            for (int i = 0; i < insights.Count; i++)
            {
                ReflectionInsight insight = insights[i];
                builder.Append("\n- ")
                    .Append(string.IsNullOrWhiteSpace(insight.summary) ? insight.title : insight.summary);

                if (!string.IsNullOrWhiteSpace(insight.suggestedAdjustment))
                {
                    builder.Append(" | 建议=").Append(insight.suggestedAdjustment);
                }
            }
        }

        return builder.ToString();
    }

    private void TrimMemoryCapacity()
    {
        if (memories.Count <= maxMemoryCount) return;

        memories = memories
            .OrderByDescending(m => m != null && m.isProceduralHint ? 1 : 0)
            .ThenByDescending(m => m != null ? m.importance : 0f)
            .ThenByDescending(m => m != null ? m.lastAccessedAt : DateTime.MinValue)
            .ThenByDescending(m => m != null ? m.createdAt : DateTime.MinValue)
            .Take(maxMemoryCount)
            .ToList();
    }

    private void TrimInsightCapacity()
    {
        PruneExpiredInsights();
        if (reflectionInsights.Count <= maxReflectionInsightCount) return;

        reflectionInsights = reflectionInsights
            .Where(i => i != null)
            .OrderByDescending(i => i.confidence)
            .ThenByDescending(i => i.createdAt)
            .Take(maxReflectionInsightCount)
            .ToList();
    }

    private void PruneExpiredInsights()
    {
        DateTime now = DateTime.UtcNow;
        reflectionInsights.RemoveAll(i => i == null || (i.expiresAt != default && i.expiresAt <= now));
    }

    private static AgentMemoryKind ParseLegacyKind(string type)
    {
        if (Enum.TryParse(type, true, out AgentMemoryKind parsed))
        {
            return parsed;
        }

        return AgentMemoryKind.Observation;
    }

    private static List<string> NormalizeList(IEnumerable<string> input)
    {
        return input == null
            ? new List<string>()
            : input
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static List<string> BuildTagSet(params string[] values)
    {
        return NormalizeList(values);
    }

    private static string CleanText(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
    }

    private static string BuildCombinedText(params string[] values)
    {
        return string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray());
    }

    private static string BuildSearchableText(Memory memory)
    {
        if (memory == null) return string.Empty;

        return BuildCombinedText(
            memory.summary,
            memory.detail,
            memory.targetRef,
            memory.outcome,
            memory.missionId,
            memory.slotId,
            memory.stepLabel,
            memory.sourceModule,
            string.Join(" ", memory.tags),
            string.Join(" ", memory.relatedAgentIds),
            string.Join(" ", memory.relatedEntityRefs));
    }

    private static float OverlapScore(List<string> queryValues, List<string> memoryValues, float weight)
    {
        if (queryValues == null || memoryValues == null || queryValues.Count == 0 || memoryValues.Count == 0)
        {
            return 0f;
        }

        HashSet<string> memorySet = new HashSet<string>(memoryValues, StringComparer.OrdinalIgnoreCase);
        float score = 0f;
        for (int i = 0; i < queryValues.Count; i++)
        {
            if (memorySet.Contains(queryValues[i]))
            {
                score += weight;
            }
        }

        return score;
    }

    private static float ComputeTextSimilarity(string queryText, string searchableText)
    {
        if (string.IsNullOrWhiteSpace(queryText) || string.IsNullOrWhiteSpace(searchableText)) return 0f;

        string searchable = searchableText.ToLowerInvariant();
        List<string> tokens = ExtractTokens(queryText);
        if (tokens.Count == 0) return 0f;

        float score = 0f;
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrWhiteSpace(token)) continue;

            if (searchable.Contains(token.ToLowerInvariant()))
            {
                score += token.Length >= 4 ? 0.9f : 0.45f;
            }
        }

        return score;
    }

    private static List<string> ExtractTokens(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new List<string>();

        MatchCollection matches = TokenRegex.Matches(input.ToLowerInvariant());
        List<string> tokens = new List<string>(matches.Count);
        for (int i = 0; i < matches.Count; i++)
        {
            string token = matches[i].Value.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }

        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
