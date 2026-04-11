using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public class MemoryModule : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Inspector 配置
    // ═══════════════════════════════════════════════════════════════════════════

    [Header("记忆容量")]
    /// <summary>记忆列表最大条数。超出时按重要度+访问时间裁剪，程序性提示优先保留。</summary>
    [Min(32)] public int maxMemoryCount = 512;
    /// <summary>反思洞察最大条数。超出时按置信度+创建时间裁剪。</summary>
    [Min(8)] public int maxReflectionInsightCount = 64;
    /// <summary>
    /// 新鲜度衰减窗口（小时）。
    /// 记忆的年龄超过此值后新鲜度分会趋近于 0，但不会被强制删除。
    /// </summary>
    [Min(1f)] public float freshnessWindowHours = 72f;

    [Header("重要性累积触发反思")]
    /// <summary>
    /// 重要性累积阈值：每次存入记忆时，将 importance×10 累加到累积器。
    /// 累积器达到此阈值时触发 OnImportanceThresholdReached 事件，通知 ReflectionModule 执行 L2 反思。
    /// 值越小，反思触发越频繁（token 消耗越高）；值越大，反思越稀疏（经验沉淀越慢）。
    /// 默认 150 对应约 15 条 importance=1.0 的高权重记忆进入后才触发一次。
    /// </summary>
    [Min(50f)] public float reflectionImportanceThreshold = 150f;

    // ═══════════════════════════════════════════════════════════════════════════
    // 公共数据（Inspector 可见，方便调试）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>当前存储的所有结构化记忆。</summary>
    public List<Memory> memories = new List<Memory>();
    /// <summary>当前有效的反思洞察（已过期的在每次 Recall/RegisterInsight 时自动清理）。</summary>
    public List<ReflectionInsight> reflectionInsights = new List<ReflectionInsight>();

    // ═══════════════════════════════════════════════════════════════════════════
    // 事件（供外部模块订阅）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 当重要性累积器达到 reflectionImportanceThreshold 时触发。
    /// ReflectionModule 订阅此事件以执行 L2 跨事件反思（独立于失败/阻塞触发的 L1 反思）。
    /// 每次触发后累积器归零，下一周期重新累积。
    /// </summary>
    public event Action OnImportanceThresholdReached;

    // ═══════════════════════════════════════════════════════════════════════════
    // 私有字段
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 重要性累积器：每次 StoreMemory 时 += importance×10。
    /// 达到 reflectionImportanceThreshold 后触发事件并归零。
    /// 不需要持久化，重启后从 0 重新积累即可。
    /// </summary>
    private float _importanceAccumulator = 0f;

    /// <summary>词袋文本相似度使用的 token 提取正则。</summary>
    private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{Nd}_]+", RegexOptions.Compiled);

    private void Awake()
    {
        PruneExpiredInsights();
        TrimMemoryCapacity();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 接口函数：记忆写入
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 统一入库入口（所有其他 Remember* 方法的最终调用点）：
    /// 补齐字段默认值 → 入库 → 裁剪容量 → 累积重要性（可能触发 L2 反思事件）。
    /// 保证后续检索逻辑面对的是结构稳定的数据，而不是半成品。
    /// </summary>
    public Memory StoreMemory(Memory memory)
    {
        if (memory == null) return null;

        // ── 字段规范化 ──────────────────────────────────────────────
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
        memory.tags = NormalizeList(memory.tags);
        memory.relatedAgentIds = NormalizeList(memory.relatedAgentIds);
        memory.relatedEntityRefs = NormalizeList(memory.relatedEntityRefs);
        memory.derivedFromMemoryIds = NormalizeList(memory.derivedFromMemoryIds);

        // 初始化遗忘曲线强度：以重要度作为起始值，高重要度记忆本身就有更强的初始稳固性
        if (memory.strengthScore <= 0f)
            memory.strengthScore = memory.importance;

        memories.Add(memory);
        TrimMemoryCapacity();

        // 累积重要性：超过阈值时通知 ReflectionModule 触发 L2 跨事件反思
        AccumulateImportance(memory.importance);

        return memory;
    }

    /// <summary>
    /// 构建并存储一条结构化记忆（统一工厂方法，推荐优先使用此方法而非直接 new Memory）。
    /// reflectionDepth 标记知识层次：0=原始事实，1=L2反思推断，2=L3抽象洞察。
    /// </summary>
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
        bool isProceduralHint = false,
        int reflectionDepth = 0)
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
            isProceduralHint = isProceduralHint,
            reflectionDepth = reflectionDepth
        };

        return StoreMemory(memory);
    }

    /// <summary>记录本 Agent 在指定步骤生成执行计划的快照（importance=0.72）。
    /// 由 PlanningModule 在 LLM#4 步骤生成完毕后调用。
    /// 后续检索时可为相同角色/目标的计划阶段提供历史经验。
    /// </summary>
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

    /// <summary>记录某一步骤的阶段性进展（importance 默认 0.68）。
    /// 由 PlanningModule.CompleteCurrentStep() 或 ADM 在步骤完成时调用。
    /// </summary>
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

    /// <summary>记录单次原子动作的执行结果（成功 importance=0.62；失败 importance=0.86）。
    /// 失败记忆的高重要度保证它在后续检索时被优先召回，避免重蹈覆辙。
    /// 由 ActionDecisionModule 在每批次动作执行完成后调用。
    /// </summary>
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

    /// <summary>记录感知模块捕获到的环境观测（importance=0.75）。
    /// 由 PerceptionModule 在 OnPerceptionEvent 回调中调用。
    /// entityRefs 用于传入本次观测涉及的实体引用列表，便于后续按目标检索。
    /// </summary>
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

    /// <summary>记录整个任务（mission）的最终结果（成功 importance=0.94，失败 importance=0.96）。
    /// 由 GroupMonitor.EvaluateMissionCompletion() 在判断任务完成后调用。
    /// 这是任务执行全周期最高权重的记忆节点，importance 极高保证未来跨任务检索时必定被召回。
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════════════════
    // 接口函数：记忆检索
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 统一检索入口：
    /// 不靠硬编码词表，而是按”任务ID、槽位、步骤、目标、标签、自由文本”综合打分，
    /// 返回 Top-K 最相关记忆。每次命中都会强化该记忆的 strengthScore（Ebbinghaus 效应）。
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
            // Ebbinghaus 强化：被访问的记忆强度 +0.1，最高 1.0
            // 使热点记忆在 TrimMemoryCapacity 时得到优先保留
            StrengthenMemory(ranked[i]);
        }

        return ranked;
    }

    /// <summary>
    /// 存入一条反思洞察（由 ReflectionModule 在 LLM 反思完成后调用）：
    /// 同步写入 reflectionInsights 列表（供 GetRelevantInsights 专项检索），
    /// 并作为 kind=Reflection、isProceduralHint=true 的记忆写入 memories 列表（供 Recall 通用检索）。
    /// insight.insightDepth 决定写入的 Memory.reflectionDepth（1=L2，2=L3）。
    /// </summary>
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

        // 将反思洞察同步写入 Memory 列表，供 Recall 检索
        // reflectionDepth 由 insight.insightDepth 决定（1=L2单次反思，2=L3抽象洞察）
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
            isProceduralHint: true,
            reflectionDepth: insight.insightDepth);
    }

    /// <summary>
    /// 检索与 query 最相关的反思洞察（专项索引，比 Recall 过滤 kind=Reflection 更高效）。
    /// 自动清理过期洞察。由 BuildPlanningContext / BuildActionContext 内部调用，
    /// 也可由 ActionDecisionModule 直接调用以获取操作规则提示。
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════════════════
    // 辅助函数：容量管理
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 裁剪记忆列表到最大容量：
    /// 优先保留程序性提示 → 次按 strengthScore（Ebbinghaus强度）降序 → 再按重要度 → 最后按访问时间。
    /// strengthScore 纳入排序保证"频繁被检索的热点记忆不因年龄老化而被淘汰"。
    /// </summary>
    private void TrimMemoryCapacity()
    {
        if (memories.Count <= maxMemoryCount) return;

        memories = memories
            .OrderByDescending(m => m != null && m.isProceduralHint ? 1 : 0)
            .ThenByDescending(m => m != null ? m.strengthScore : 0f)
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

    // ═══════════════════════════════════════════════════════════════════════════
    // 辅助函数：重要性累积与遗忘曲线
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 重要性累积器更新：每次 StoreMemory 后调用。
    /// importance×10 累加到累积器，超过阈值时触发 OnImportanceThresholdReached 事件，
    /// 通知 ReflectionModule 执行 L2 跨事件反思，然后累积器归零。
    /// 使用 importance×10 而非原始值，是为了让高重要度事件（如动作失败）对触发反思贡献更大。
    /// </summary>
    private void AccumulateImportance(float importance)
    {
        _importanceAccumulator += importance * 10f;
        if (_importanceAccumulator >= reflectionImportanceThreshold)
        {
            _importanceAccumulator = 0f;
            OnImportanceThresholdReached?.Invoke();
        }
    }

    /// <summary>
    /// Ebbinghaus 遗忘曲线强化：每次记忆被 Recall 命中时调用。
    /// strengthScore +0.1（上限 1.0），模拟"重复激活使记忆更稳固"的效果。
    /// TrimMemoryCapacity 时 strengthScore 高的记忆优先保留。
    /// </summary>
    private static void StrengthenMemory(Memory memory)
    {
        if (memory == null) return;
        memory.strengthScore = Mathf.Min(1f, memory.strengthScore + 0.1f);
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
