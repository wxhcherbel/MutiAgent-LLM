using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
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

    [Header("3D检索评分权重（三者之和建议为 1.0）")]
    /// <summary>时效性维度权重。指数衰减：e^(-t/freshnessWindowHours)，λ=1/freshnessWindowHours。</summary>
    [Range(0f, 2f)] public float recencyWeight    = 0.35f;
    /// <summary>重要性维度权重。直接使用 memory.importance，已归一化 [0,1]。</summary>
    [Range(0f, 2f)] public float importanceWeight = 0.40f;
    /// <summary>相关性维度权重。结构字段精确匹配 + BagOfWords，归一化到 [0,1]。</summary>
    [Range(0f, 2f)] public float relevanceWeight  = 0.25f;

    [Header("重要性累积触发反思")]
    /// <summary>
    /// 重要性累积阈值：每次存入记忆时，将 importance×10 累加到累积器。
    /// 累积器达到此阈值时触发 OnImportanceThresholdReached 事件，通知 ReflectionModule 执行 L2 反思。
    /// 值越小，反思触发越频繁（token 消耗越高）；值越大，反思越稀疏（经验沉淀越慢）。
    /// 默认 150 对应约 15 条 importance=1.0 的高权重记忆进入后才触发一次。
    /// </summary>
    [Min(50f)] public float reflectionImportanceThreshold = 150f;

    [Header("持久化")]
    /// <summary>是否在每次写入 Policy/Reflection 记忆后自动保存到磁盘。关闭后可手动调用 SaveMemories()。</summary>
    public bool autoSaveOnStore = true;
    /// <summary>是否在初始化时自动从磁盘加载历史 Policy/Reflection。</summary>
    public bool autoLoadOnAwake = true;

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

    /// <summary>
    /// 当前 agent 的人格系统引用，由 IntelligentAgent.InitializeAgent() 通过
    /// SetPersonalitySystem() 注入。为 null 时 InjectPersonality 直接返回，不影响正常流程。
    /// </summary>
    private PersonalitySystem _personalitySystem;

    /// <summary>AgentID，仅用于日志，持久化文件不再按 AgentID 分文件。由 SetAgentId() 注入。</summary>
    private string _agentId = "unknown";

    /// <summary>从 thought JSON 中提取 suggestion 字段内容。</summary>
    private static readonly Regex PolicyRegex = new Regex(@"""suggestion""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
    /// <summary>从 thought JSON 中提取 confidence 字段等级（高/中/低）。</summary>
    private static readonly Regex ConfidenceRegex = new Regex(@"""confidence""\s*:\s*""(高|中|低)""", RegexOptions.Compiled);

    private void Awake()
    {
        // AgentID 由 IntelligentAgent.InitializeAgent() 延迟注入，Awake 阶段跳过加载；
        // 加载将在 SetAgentId() 中触发（确保文件名正确）。
        PruneExpiredInsights();
        TrimMemoryCapacity();
    }

    private void OnApplicationQuit()
    {
        SaveMemories();
    }

    private void OnDisable()
    {
        // OnDisable 比 OnApplicationQuit 更可靠（Editor 停止播放时必定触发）
        SaveMemories();
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

        // 注入人格维度标注并修正重要度（需要在 Add 之前，确保入库的记忆已含人格信息）
        InjectPersonality(memory);

        memories.Add(memory);
        TrimMemoryCapacity();

        // 累积重要性：超过阈值时通知 ReflectionModule 触发 L2 跨事件反思
        // 注意：此时 memory.importance 已经过人格乘数修正，累积的是修正后的权重
        AccumulateImportance(memory.importance);

        // 从 thought 结构化文本中提取【建议】，独立存为 Policy 程序性提示
        // isProceduralHint 作守卫，避免提取出的 Policy 记忆再次触发自身
        if (!memory.isProceduralHint)
            TryExtractPolicyFromDetail(memory);

        // 只有 Policy 类型记忆才触发持久化（情景记忆不写磁盘）
        if (autoSaveOnStore && memory.kind == AgentMemoryKind.Policy)
            SaveMemories();

        return memory;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 人格系统接口
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 注入人格系统引用。由 IntelligentAgent.InitializeAgent() 在初始化阶段调用。
    /// 注入后，每次 StoreMemory 都会自动调用 InjectPersonality，
    /// 无需在各调用方单独处理人格相关逻辑。
    /// </summary>
    /// <param name="ps">当前 agent 的 PersonalitySystem，为 null 时不修改现有引用。</param>
    public void SetPersonalitySystem(PersonalitySystem ps)
    {
        if (ps == null) return;
        _personalitySystem = ps;
    }

    /// <summary>
    /// 注入 AgentID，用于持久化文件命名。
    /// 由 IntelligentAgent.InitializeAgent() 在初始化阶段调用（与 SetPersonalitySystem 同期）。
    /// 注入后若 autoLoadOnAwake=false 也可在此处手动触发加载。
    /// </summary>
    public void SetAgentId(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;
        _agentId = agentId;
        // Awake 时 AgentID 尚未注入，在此补充执行一次加载
        if (autoLoadOnAwake)
            LoadMemories();

        // 定期保存作为安全网（OnApplicationQuit 在 Editor 中不可靠）
        if (autoSaveOnStore && !IsInvoking(nameof(PeriodicSave)))
            InvokeRepeating(nameof(PeriodicSave), 60f, 60f);
    }

    private void PeriodicSave()
    {
        if (memories.Any(m => m.kind == AgentMemoryKind.Policy))
            SaveMemories();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 持久化（仅保存 Policy + ReflectionInsight）
    // ═══════════════════════════════════════════════════════════════════════════
    //
    // 设计原则：
    //   · 情景记忆（Goal/Plan/Decision/Outcome/Observation）绑定具体 run 和角色，
    //     跨 run 不具参考价值，每次启动重建即可，不写磁盘。
    //   · Policy 记忆是从 thought.suggestion 提炼的通用决策原则（"当[结构条件]时应[策略]"），
    //     与具体 AgentID 或角色无关，跨 run 有效。
    //   · ReflectionInsight 是跨事件的抽象洞察，同样与角色无关，值得持久化。
    //   · 使用单一全局文件（不按 AgentID 分文件），所有 agent 共享同一个规律库。

    /// <summary>
    /// 持久化文件路径：项目根目录下的 SaveData/MemoryModule/shared_policies.json。
    /// Editor 下 Application.dataPath = &lt;项目&gt;/Assets，故 "../SaveData" 即项目根。
    /// Build 下位于可执行文件同级目录的 SaveData 文件夹。
    /// </summary>
    public static string SaveFilePath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SaveData", "MemoryModule", "shared_policies.json"));

    /// <summary>
    /// 将 Policy 记忆和 ReflectionInsight 保存到全局共享文件。
    /// 情景记忆（非 Policy 类型）不写磁盘。
    /// </summary>
    public void SaveMemories()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SaveFilePath));
            var policies = memories.Where(m => m.kind == AgentMemoryKind.Policy).ToList();
            var data = new MemorySaveData { policies = policies, reflectionInsights = reflectionInsights };
            File.WriteAllText(SaveFilePath, JsonConvert.SerializeObject(data, Formatting.None));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MemoryModule] 保存失败: {e.Message}");
        }
    }

    /// <summary>
    /// 从全局共享文件加载 Policy 记忆和 ReflectionInsight，合并到当前列表（按 id 去重）。
    /// </summary>
    public void LoadMemories()
    {
        if (!File.Exists(SaveFilePath)) return;
        try
        {
            var data = JsonConvert.DeserializeObject<MemorySaveData>(File.ReadAllText(SaveFilePath));
            if (data?.policies != null)
            {
                var existingIds = new HashSet<string>(memories.Select(m => m.id));
                foreach (var m in data.policies)
                    if (!string.IsNullOrEmpty(m.id) && !existingIds.Contains(m.id))
                        memories.Add(m);
            }
            if (data?.reflectionInsights != null)
            {
                var existingIds = new HashSet<string>(reflectionInsights.Select(i => i.id));
                foreach (var ins in data.reflectionInsights)
                    if (!string.IsNullOrEmpty(ins.id) && !existingIds.Contains(ins.id))
                        reflectionInsights.Add(ins);
            }
            int policyCount = memories.Count(m => m.kind == AgentMemoryKind.Policy);
            Debug.Log($"[MemoryModule] ({_agentId}) 加载规律库: Policy={policyCount} 条，Insight={reflectionInsights.Count} 条");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MemoryModule] 加载失败: {e.Message}");
        }
    }

    [Serializable]
    private class MemorySaveData
    {
        public List<Memory> policies;
        public List<ReflectionInsight> reflectionInsights;
    }

    /// <summary>
    /// 在记忆入库前，将人格系统的维度标注和重要度修正写入记忆对象。
    /// 由 StoreMemory 在 Add() 之前调用，保证入库记忆已含完整人格信息。
    ///
    /// 步骤：
    ///   ① 调用 GetImportanceModifier 获取人格乘数，修正 memory.importance
    ///      （例如高神经质 agent 的 Decision 类记忆 importance×1.2）
    ///   ② 调用 GetPersonalityTag 获取人格标注字符串，写入 memory.personalityContext
    ///      （例如 "[高尽责性-0.85]"，供 ReflectionModule 分析人格-结果关联）
    ///
    /// 若 _personalitySystem 为 null（人格系统未挂载），此方法直接返回，不影响正常流程。
    /// </summary>
    private void InjectPersonality(Memory m)
    {
        if (_personalitySystem == null) return;

        // ① 根据人格调整 importance 权重
        //    GetImportanceModifier 根据记忆类型和人格维度返回乘数（1.0f = 不修改）
        //    Clamp01 保证修正后的值不超出 [0, 1] 范围
        float modifier = _personalitySystem.GetImportanceModifier(m.kind);
        if (!Mathf.Approximately(modifier, 1.0f))
            m.importance = Mathf.Clamp01(m.importance * modifier);

        // ② 记录人格标注，供 ReflectionModule 做人格-场景-结果关联分析
        //    只有存在突出维度（>0.7）时 GetPersonalityTag 才会返回非空字符串
        string tag = _personalitySystem.GetPersonalityTag();
        if (!string.IsNullOrWhiteSpace(tag))
            m.personalityContext = tag;
    }

    /// <summary>
    /// 从已存入记忆的 detail 字段中解析结构化 thought 的【建议】和【置信】标签。
    /// 若【建议】非空且【置信】不为"低"，则单独存入一条 Policy 类型的程序性提示记忆，
    /// 使其在后续检索中可被优先召回，而不是埋在长推理文本里。
    /// 【置信】缺失时默认按"中"处理（confidence=0.70）。
    /// </summary>
    private void TryExtractPolicyFromDetail(Memory source)
    {
        if (string.IsNullOrWhiteSpace(source.detail)) return;

        Debug.Log($"[MemoryModule] ({_agentId}) TryExtractPolicy: detail片段={source.detail.Substring(0, Mathf.Min(100, source.detail.Length))}, regex匹配={PolicyRegex.IsMatch(source.detail)}");

        Match policyMatch = PolicyRegex.Match(source.detail);
        if (!policyMatch.Success) return;

        string suggestion = policyMatch.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(suggestion) || suggestion == "留空" || suggestion == "否则留空") return;

        // 根据【置信】等级决定存储置信度，低置信度建议直接丢弃
        float confidence = 0.70f;
        Match confMatch = ConfidenceRegex.Match(source.detail);
        if (confMatch.Success)
        {
            switch (confMatch.Groups[1].Value)
            {
                case "高": confidence = 0.85f; break;
                case "中": confidence = 0.70f; break;
                case "低": return;
            }
        }

        // 去重：若已有 Policy 记忆与新建议 Jaccard 相似度 >= 0.6，视为重复。
        // 仅强化已有记录的 strengthScore，不新建，避免规律库膨胀。
        const float policyDupThreshold = 0.6f;
        Memory duplicate = null;
        float bestSim = 0f;
        foreach (var m in memories)
        {
            if (m.kind != AgentMemoryKind.Policy) continue;
            float sim = JaccardSimilarity(suggestion, m.summary);
            if (sim >= policyDupThreshold && sim > bestSim) { bestSim = sim; duplicate = m; }
        }
        if (duplicate != null)
        {
            duplicate.strengthScore = Mathf.Min(1f, duplicate.strengthScore + 0.05f);
            duplicate.accessCount++;
            duplicate.lastAccessedAt = DateTime.UtcNow;
            return;
        }

        Remember(
            AgentMemoryKind.Policy,
            suggestion,
            $"[来源] {source.summary}",
            importance: 0.75f,
            confidence: confidence,
            isProceduralHint: true,
            missionId: source.missionId,
            slotId: source.slotId,
            targetRef: source.targetRef,
            sourceModule: source.sourceModule,
            tags: new[] { "inline_policy", "procedural" });
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

    /// <summary>记录 LLM#1 对任务约束结构的解析推理（importance=0.70）。
    /// 由 PlanningModule 在 LLM#1 解析完成后调用。
    /// thought 包含 LLM 判断约束类型的关键依据，供后续同类任务规划参考。
    /// </summary>
    public Memory RememberConstraintAnalysis(
        string missionId,
        string missionDesc,
        string relType,
        int constraintCount,
        string thought)
    {
        string summary = $"任务解析: {relType}, {constraintCount}个约束";
        string detail = string.IsNullOrWhiteSpace(thought)
            ? summary
            : $"{summary}\n[推理] {thought}";
        return Remember(
            AgentMemoryKind.Plan,
            summary,
            detail,
            importance: 0.70f,
            confidence: 0.75f,
            status: AgentMemoryStatus.Completed,
            sourceModule: "PlanningModule",
            missionId: missionId,
            targetRef: missionDesc,
            tags: new[] { relType, "constraint_analysis" });
    }

    /// <summary>记录 LLM#2 生成槽位的设计推理（importance=0.68）。
    /// 由 PlanningModule 组长在 LLM#2 槽位生成完成后调用。
    /// thought 包含角色设计的依据，供后续同类任务参考角色分工模式。
    /// </summary>
    public Memory RememberSlotDesign(
        string missionId,
        string groupMission,
        string slotsSummary,
        string thought)
    {
        string summary = $"槽位设计: {slotsSummary}";
        string detail = string.IsNullOrWhiteSpace(thought)
            ? summary
            : $"{summary}\n[推理] {thought}";
        return Remember(
            AgentMemoryKind.Plan,
            summary,
            detail,
            importance: 0.68f,
            confidence: 0.75f,
            status: AgentMemoryStatus.Completed,
            sourceModule: "PlanningModule",
            missionId: missionId,
            targetRef: groupMission,
            tags: new[] { "slot_design" });
    }

    /// <summary>记录 LLM#3 选槽决策的推理（importance=0.65）。
    /// 由 PlanningModule 全员在 LLM#3 选槽完成后调用。
    /// thought 包含选择该槽的理由（电量、位置、槽可用性等），
    /// 供后续同角色任务中角色自我匹配提供经验参考。
    /// </summary>
    public Memory RememberSlotSelection(
        string missionId,
        string slotId,
        string role,
        string doneCond,
        string thought)
    {
        string summary = $"选择槽位 {slotId}({role}): {doneCond}";
        string detail = string.IsNullOrWhiteSpace(thought)
            ? summary
            : $"{summary}\n[推理] {thought}";
        // Plan 而非 Decision：选槽是规划阶段的决策，不应被 ADM 的 BuildActionContext 检索到
        return Remember(
            AgentMemoryKind.Plan,
            summary,
            detail,
            importance: 0.65f,
            confidence: 0.80f,
            status: AgentMemoryStatus.Completed,
            sourceModule: "PlanningModule",
            missionId: missionId,
            slotId: slotId,
            targetRef: doneCond,
            tags: new[] { role, "slot_selection" });
    }

    /// <summary>记录本 Agent 在指定步骤生成执行计划的快照（importance=0.72）。
    /// 由 PlanningModule 在 LLM#4 步骤生成完毕后调用。
    /// thought 包含步骤拆解的推理，供后续检索时为相同角色/目标的计划阶段提供历史经验。
    /// </summary>
    public Memory RememberPlanSnapshot(
        string missionId,
        string slotId,
        string stepLabel,
        string planSummary,
        string targetRef,
        string thought = "",
        IEnumerable<string> tags = null)
    {
        string detail = string.IsNullOrWhiteSpace(thought)
            ? planSummary
            : $"{planSummary}\n[推理] {thought}";
        return Remember(
            AgentMemoryKind.Plan,
            $"生成执行计划: {stepLabel}",
            detail,
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

        // 去重：用 summary + applyWhen 组合文本做 Jaccard，>= 0.5 视为重复。
        // 重复时延长有效期并更新置信度，不新建，避免反思库膨胀。
        const float insightDupThreshold = 0.5f;
        string newText = $"{insight.summary} {insight.applyWhen}";
        ReflectionInsight dupInsight = null;
        float bestInsightSim = 0f;
        foreach (var r in reflectionInsights)
        {
            float sim = JaccardSimilarity(newText, $"{r.summary} {r.applyWhen}");
            if (sim >= insightDupThreshold && sim > bestInsightSim) { bestInsightSim = sim; dupInsight = r; }
        }
        if (dupInsight != null)
        {
            if (insight.confidence > dupInsight.confidence)
                dupInsight.confidence = insight.confidence;
            // 延长有效期到 insight 的过期时间（取较晚者）
            if (insight.expiresAt != DateTime.MinValue && insight.expiresAt > dupInsight.expiresAt)
                dupInsight.expiresAt = insight.expiresAt;
            if (autoSaveOnStore) SaveMemories();
            return;
        }

        reflectionInsights.Add(insight);
        TrimInsightCapacity();

        if (autoSaveOnStore)
            SaveMemories();

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

    /// <summary>
    /// 仅检索与指定标签匹配的 Policy 记忆（已提炼的可复用规律），供 LLM#1/2/3 轻量注入。
    /// 不返回情景记忆，避免无关历史经验污染规划阶段各 LLM 的 prompt。
    /// tags 应与存储时的阶段标签一致，如 "constraint_analysis"/"slot_design"/"slot_selection"。
    /// 无相关 Policy 时返回空字符串，调用方跳过注入即可。
    /// </summary>
    public string BuildPoliciesContext(string freeText, string[] tags, int maxCount = 3)
    {
        if (memories == null || memories.Count == 0) return string.Empty;

        MemoryQuery query = new MemoryQuery
        {
            freeText = freeText ?? string.Empty,
            kinds = new[] { AgentMemoryKind.Policy },
            tags = tags != null ? new List<string>(tags) : new List<string>(),
            maxCount = Mathf.Max(1, maxCount),
            preferProceduralHints = true
        };

        List<Memory> policies = Recall(query);
        if (policies == null || policies.Count == 0) return string.Empty;

        return BuildContextSummary(policies, new List<ReflectionInsight>(), string.Empty);
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

    /// <summary>
    /// 三维检索评分（3D Retrieval Score）：
    ///   score = recencyWeight    × e^(-t/freshnessWindowHours)   — 时效性，指数衰减
    ///         + importanceWeight × memory.importance              — 重要性，已归一化 [0,1]
    ///         + relevanceWeight  × ComputeNormalizedRelevance()   — 相关性，归一化到 [0,1]
    /// 结果由 Recall() 按降序排列后取 top-K。
    /// </summary>
    private float ComputeRetrievalScore(Memory memory, MemoryQuery query)
    {
        // 维度 1：时效性 — 指数衰减 e^(-t/freshnessWindowHours)，λ = 1/freshnessWindowHours
        double ageHours = Math.Max(0d, (DateTime.UtcNow - memory.createdAt).TotalHours);
        float recency = (float)Math.Exp(-ageHours / Math.Max(1.0, freshnessWindowHours));

        // 维度 2：重要性 — memory.importance 已在 StoreMemory 中 Clamp01，无需再处理
        float importance = memory.importance;

        // 维度 3：相关性 — 结构字段精确匹配 + BagOfWords，归一化到 [0,1]
        float relevance = ComputeNormalizedRelevance(memory, query);

        return recencyWeight * recency
             + importanceWeight * importance
             + relevanceWeight  * relevance;
    }

    /// <summary>
    /// 计算记忆与查询的相关性，归一化到 [0,1]。
    /// 子项满分分布：proceduralHint=0.15，missionId=0.30，slotId=0.20，stepLabel=0.15，
    /// targetRef=0.25，entityRef=0.15，overlap≤0.30，textSim≤0.30，failed=0.05，总分母=1.85。
    /// </summary>
    private float ComputeNormalizedRelevance(Memory memory, MemoryQuery query)
    {
        const float MaxScore = 1.85f;
        float raw = 0f;

        if (query.preferProceduralHints && memory.isProceduralHint) raw += 0.15f;

        if (!string.IsNullOrWhiteSpace(query.missionId) &&
            string.Equals(memory.missionId, query.missionId, StringComparison.OrdinalIgnoreCase))
            raw += 0.30f;

        if (!string.IsNullOrWhiteSpace(query.slotId) &&
            string.Equals(memory.slotId, query.slotId, StringComparison.OrdinalIgnoreCase))
            raw += 0.20f;

        if (!string.IsNullOrWhiteSpace(query.stepLabel) &&
            string.Equals(memory.stepLabel, query.stepLabel, StringComparison.OrdinalIgnoreCase))
            raw += 0.15f;

        if (!string.IsNullOrWhiteSpace(query.targetRef))
        {
            if (string.Equals(memory.targetRef, query.targetRef, StringComparison.OrdinalIgnoreCase))
                raw += 0.25f;
            else if (memory.relatedEntityRefs.Any(r =>
                     string.Equals(r, query.targetRef, StringComparison.OrdinalIgnoreCase)))
                raw += 0.15f;
        }

        // 标签 / 相关智能体 / 相关实体重叠（最多贡献 0.30）
        float overlap = OverlapScore(query.tags, memory.tags, 0.10f)
                      + OverlapScore(query.relatedAgentIds, memory.relatedAgentIds, 0.10f)
                      + OverlapScore(query.relatedEntityRefs, memory.relatedEntityRefs, 0.10f);
        raw += Mathf.Min(0.30f, overlap);

        // BagOfWords 文本相似度（原始值无上限，缩放 ×0.15 后截断到 0.30）
        float textSim = ComputeTextSimilarity(query.freeText, BuildSearchableText(memory));
        raw += Mathf.Min(0.30f, textSim * 0.15f);

        // 失败记忆轻微加分，保证错误经验不因重要性不够高而被埋没
        if (memory.status == AgentMemoryStatus.Failed) raw += 0.05f;

        // 人格偏好角色加权（可选）：
        //   若 query.preferredRoles 非空，检查记忆的 tags 中是否包含偏好角色名。
        //   每命中一个偏好角色加 0.10f，上限 0.20f（最多两个角色贡献加分）。
        //   目的：让历史上担任人格偏好角色的记忆在检索时优先被召回，
        //   例如高尽责性 agent 查询时，Perimeter/Guard 相关的历史记忆得分更高。
        if (query.preferredRoles != null && query.preferredRoles.Count > 0)
        {
            float roleBonus = 0f;
            foreach (string role in query.preferredRoles)
            {
                if (memory.tags.Any(t => string.Equals(t, role, StringComparison.OrdinalIgnoreCase)))
                    roleBonus += 0.10f;
            }
            raw += Mathf.Min(0.20f, roleBonus);
        }

        return Mathf.Clamp01(raw / MaxScore);
    }

    private float ScoreMemory(Memory memory, MemoryQuery query)
    {
        if (memory == null) return 0f;
        return ComputeRetrievalScore(memory, query);
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
        score += (float)Math.Exp(-ageHours / 24.0);  // 指数衰减，半衰期约 17h（ln2×24）
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

    /// <summary>
    /// Jaccard 相似度：|A∩B| / |A∪B|，返回 [0,1]。
    /// 用于去重判断，比 ComputeTextSimilarity 更适合两段任意文本的对比（有归一化）。
    /// </summary>
    private static float JaccardSimilarity(string textA, string textB)
    {
        if (string.IsNullOrWhiteSpace(textA) || string.IsNullOrWhiteSpace(textB)) return 0f;
        var setA = new HashSet<string>(ExtractTokens(textA), StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(ExtractTokens(textB), StringComparer.OrdinalIgnoreCase);
        if (setA.Count == 0 || setB.Count == 0) return 0f;
        int intersection = 0;
        foreach (var t in setA) if (setB.Contains(t)) intersection++;
        int union = setA.Count + setB.Count - intersection;
        return union > 0 ? (float)intersection / union : 0f;
    }

    // ─── 巡逻空闲度追踪 ─────────────────────────────────────────────

    private readonly Dictionary<string, DateTime> _patrolTimestamps = new();

    /// <summary>DoPatrol 完成时由 AME 调用，记录区域最后巡逻时间。</summary>
    public void RecordPatrolEvent(string areaName, DateTime time)
    {
        if (!string.IsNullOrWhiteSpace(areaName))
            _patrolTimestamps[areaName] = time;
    }

    /// <summary>返回空闲度最高（最久未巡逻）的区域名；无记录时返回 string.Empty。
    /// DoPatrol 在 targetName 为空时调用，自动选区。</summary>
    public string GetHighestIdlenessArea()
    {
        if (_patrolTimestamps.Count == 0) return string.Empty;
        return _patrolTimestamps
            .OrderByDescending(kv => (DateTime.Now - kv.Value).TotalSeconds)
            .First().Key;
    }

    /// <summary>生成区域空闲度文本（供需要时使用）。</summary>
    public string BuildPatrolIdlenessContext(int topN = 6)
    {
        if (_patrolTimestamps.Count == 0) return "（无历史巡逻记录）";
        var now = DateTime.Now;
        var lines = _patrolTimestamps
            .OrderByDescending(kv => (now - kv.Value).TotalSeconds)
            .Take(topN)
            .Select(kv =>
            {
                int mins = (int)(now - kv.Value).TotalMinutes;
                string ago = mins == 0 ? "刚巡逻过" : $"{mins}分钟前";
                return $"  • {kv.Key}：{ago}";
            });
        return string.Join("\n", lines);
    }
}
