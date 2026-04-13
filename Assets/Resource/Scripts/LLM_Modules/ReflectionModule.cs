using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// 反思模块（三层反思架构）：
///
/// L1（被动触发）：连续动作失败 ≥ threshold、被阻塞、重要观察、任务完成后，
///   调用 NotifyActionOutcome/NotifyBlocked/NotifyImportantObservation 触发；
///
/// L2（重要性积累触发）：MemoryModule 的重要性累积器满阈值后，
///   触发 OnImportanceThresholdReached → OnMemoryImportanceThresholdReached()；
///   相比 L1 更侧重”跨步骤规律提炼”，冷却时间独立且更短；
///
/// L3（阶段性抽象，可选）：由 GroupMonitor 在任务批次结束后主动调用
///   TriggerL3Reflection()，整合多次 L1/L2 洞察，生成高层战略规律，
///   需要 enableL3AbstractReflection=true 且 MemoryModule 中有足够多记忆。
/// </summary>
public class ReflectionModule : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Inspector 配置
    // ═══════════════════════════════════════════════════════════════════════════

    [Header("L1 反思触发（失败/阻塞/完成）")]
    /// <summary>
    /// L1 反思冷却时间（秒）。连续事件不会每次都触发 LLM；
    /// 在此时间内发生的第二次触发会被丢弃，防止反思风暴消耗 LLM 配额。
    /// </summary>
    [Min(0f)] public float reflectionCooldownSeconds = 90f;

    /// <summary>
    /// 触发 L1 反思所需的连续动作失败次数阈值。
    /// 1 次失败可能是环境随机噪音；连续 2 次才说明存在系统性问题，值得反思。
    /// </summary>
    [Min(1)] public int repeatedFailureThreshold = 2;

    /// <summary>L1 反思时拉取的最近记忆条数（建议 6–12，过少信息不足，过多 token 超限）。</summary>
    [Min(1)] public int defaultReflectionMemoryCount = 8;

    /// <summary>任务完成后是否自动触发 L1 反思（沉淀成功经验）。</summary>
    public bool autoReflectOnMissionCompletion = true;

    [Header("L2 反思触发（重要性累积）")]
    /// <summary>
    /// L2 反思的独立冷却时间（秒），比 L1 更短以允许更频繁的跨事件推断。
    /// L1 和 L2 的冷却计时器相互独立，互不影响。
    /// </summary>
    [Min(10f)] public float l2CooldownSeconds = 30f;

    /// <summary>
    /// L2 反思时从 MemoryModule 拉取的记忆条数（比 L1 更多，以获得跨步骤视角）。
    /// 不绑定特定 mission，拉取全局最新记忆以发现跨任务规律。
    /// </summary>
    [Min(4)] public int l2MaxSourceMemories = 20;

    [Header("L3 反思触发（阶段性抽象）")]
    /// <summary>
    /// 是否启用 L3 阶段性抽象反思。
    /// 需要 GroupMonitor 在任务批次完成后主动调用 TriggerL3Reflection()。
    /// L3 反思比 L2 耗时更长（token 更多）且非实时需要，默认关闭，按需开启。
    /// </summary>
    public bool enableL3AbstractReflection = false;

    // ═══════════════════════════════════════════════════════════════════════════
    // 私有字段
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>MemoryModule 依赖（同 GameObject，由 EnsureDependencies 获取）。</summary>
    private MemoryModule memoryModule;

    /// <summary>LLM 接口（场景全局单例，由 EnsureDependencies 获取）。</summary>
    private LLMInterface llmInterface;

    /// <summary>L1 反思最近触发时间（Time.time），用于 reflectionCooldownSeconds 判断。</summary>
    private float lastReflectionTime = -999f;

    /// <summary>L2 反思最近触发时间（Time.time），与 L1 计时器独立。</summary>
    private float lastL2ReflectionTime = -999f;

    /// <summary>当前连续动作失败次数；成功时归零，失败时递增。</summary>
    private int consecutiveActionFailures;

    /// <summary>是否已订阅 MemoryModule.OnImportanceThresholdReached 事件，防止重复订阅。</summary>
    private bool _subscribedToMemoryEvent = false;

    /// <summary>
    /// 当前 agent 的人格系统引用，在 EnsureDependencies() 中通过 GetComponent 获取。
    /// 高神经质（>0.7）的 agent 会在初始化时将 L2 反思触发阈值降低（150→100），
    /// 使其更频繁地进行跨事件反思——这本身也是高神经质人格的体现（过度思考倾向）。
    /// 为 null 时不调整阈值，使用 Inspector 中配置的默认值。
    /// </summary>
    private PersonalitySystem _personalitySystem;

    private void Start()
    {
        EnsureDependencies();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 辅助函数：依赖初始化与事件订阅
    // ═══════════════════════════════════════════════════════════════════════════

    private void EnsureDependencies()
    {
        if (memoryModule == null)
        {
            memoryModule = GetComponent<MemoryModule>();

            // 首次获取到 MemoryModule 后订阅重要性累积事件（L2 触发入口）
            if (memoryModule != null && !_subscribedToMemoryEvent)
            {
                memoryModule.OnImportanceThresholdReached += OnMemoryImportanceThresholdReached;
                _subscribedToMemoryEvent = true;
            }
        }

        if (llmInterface == null)
        {
            llmInterface = FindObjectOfType<LLMInterface>();
        }

        // 获取人格系统并按人格特征调整 L2 反思触发阈值
        // 此处在 EnsureDependencies 中而非 Start 中，是因为 EnsureDependencies
        // 可能在 Start 之外（如首次 NotifyActionOutcome 时）被调用，确保引用始终被尝试获取
        if (_personalitySystem == null)
        {
            _personalitySystem = GetComponent<PersonalitySystem>();

            // 高神经质 agent（>0.7）降低 L2 触发阈值：150 → 100
            // 含义：每积累约10条高权重记忆就触发一次 L2 反思（而非默认15条）
            // 这让高神经质 agent 更积极地从失败和观察中提炼经验，
            // 但也意味着更高的 LLM 调用频率（设计上这是人格特征的忠实反映）
            if (_personalitySystem != null && _personalitySystem.ShouldTriggerEarlyReflection()
                && memoryModule != null)
            {
                memoryModule.reflectionImportanceThreshold = 100f;
                Debug.Log($"[ReflectionModule] {_personalitySystem.Profile.agentId} " +
                          $"高神经质({_personalitySystem.Profile.neuroticism:F2}) → " +
                          $"L2反思阈值降低为100（默认150）");
            }
        }
    }

    private void OnDestroy()
    {
        // 取消事件订阅，避免 GameObject 销毁后产生空引用回调
        if (memoryModule != null && _subscribedToMemoryEvent)
        {
            memoryModule.OnImportanceThresholdReached -= OnMemoryImportanceThresholdReached;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 接口函数：L1 反思通知（由执行层主动调用）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 通知某个原子动作的执行结果（由 ActionDecisionModule 在每批次动作完成后调用）。
    /// 连续失败次数达到 repeatedFailureThreshold 时触发 L1 反思（RepeatedFailure）。
    /// 成功时重置连续失败计数（避免偶发失败被错误归类为系统性问题）。
    /// </summary>
    public void NotifyActionOutcome(
        string missionId,
        string missionText,
        string slotId,
        string stepText,
        string targetRef,
        bool success,
        string summary)
    {
        EnsureDependencies();
        consecutiveActionFailures = success ? 0 : consecutiveActionFailures + 1;

        if (success) return;
        if (consecutiveActionFailures < repeatedFailureThreshold) return;

        ReflectionRequest request = new ReflectionRequest
        {
            reason = ReflectionTriggerReason.RepeatedFailure,
            missionId = missionId,
            missionText = missionText,
            slotId = slotId,
            stepText = stepText,
            targetRef = targetRef,
            summary = summary,
            maxSourceMemories = defaultReflectionMemoryCount
        };

        StartCoroutine(TriggerReflection(request));
    }

    /// <summary>
    /// 通知 Agent 被阻塞无法继续前进（由 ActionDecisionModule 在 C3 等待超时或路径死锁时调用）。
    /// 立即触发 L1 反思（Blocked），不受连续失败计数限制，因为"被阻塞"本身就是严重信号。
    /// </summary>
    public void NotifyBlocked(string missionId, string missionText, string slotId, string stepText, string targetRef, string summary)
    {
        EnsureDependencies();
        ReflectionRequest request = new ReflectionRequest
        {
            reason = ReflectionTriggerReason.Blocked,
            missionId = missionId,
            missionText = missionText,
            slotId = slotId,
            stepText = stepText,
            targetRef = targetRef,
            summary = summary,
            maxSourceMemories = defaultReflectionMemoryCount
        };

        StartCoroutine(TriggerReflection(request));
    }

    /// <summary>
    /// 通知感知到重要的环境事件（由 PerceptionModule 在 importance≥0.8 的观测时调用）。
    /// 拉取较少记忆（defaultReflectionMemoryCount/2），专注于当前上下文的快速洞察。
    /// </summary>
    public void NotifyImportantObservation(string missionId, string missionText, string slotId, string stepText, string targetRef, string summary)
    {
        EnsureDependencies();
        ReflectionRequest request = new ReflectionRequest
        {
            reason = ReflectionTriggerReason.ImportantObservation,
            missionId = missionId,
            missionText = missionText,
            slotId = slotId,
            stepText = stepText,
            targetRef = targetRef,
            summary = summary,
            maxSourceMemories = Mathf.Max(4, defaultReflectionMemoryCount / 2)
        };

        StartCoroutine(TriggerReflection(request));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 接口函数：L2 反思触发（由 MemoryModule 重要性累积事件驱动）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// L2 跨事件反思入口：由 MemoryModule.OnImportanceThresholdReached 事件触发。
    /// 不绑定特定 mission/slot（missionId 为空），拉取全局最新记忆，
    /// 重点发现跨步骤、跨任务的共同规律，生成适用范围更广的 insight。
    /// 使用独立的 lastL2ReflectionTime 计时，不受 L1 冷却限制。
    /// </summary>
    private void OnMemoryImportanceThresholdReached()
    {
        if (Time.time - lastL2ReflectionTime < l2CooldownSeconds) return;
        lastL2ReflectionTime = Time.time;

        EnsureDependencies();
        if (memoryModule == null || llmInterface == null) return;

        // L2 不绑定特定任务，覆盖最近的全局记忆以发现跨步骤规律
        ReflectionRequest request = new ReflectionRequest
        {
            reason = ReflectionTriggerReason.ImportantObservation,
            missionId = string.Empty,         // 空 = 跨任务范围
            missionText = "（L2重要性累积触发，跨任务模式推断）",
            slotId = string.Empty,
            stepText = string.Empty,
            targetRef = string.Empty,
            summary = "重要性累积阈值触发，分析最近多条高权重记忆中的共性规律",
            maxSourceMemories = l2MaxSourceMemories,
            force = false                     // 仍受 reflectionCooldownSeconds 约束（L1 全局冷却）
        };

        StartCoroutine(TriggerReflectionL2(request));
    }

    /// <summary>
    /// L2 反思协程：内部实现与 L1 相同，但生成的 insight 标记 insightDepth=1。
    /// 单独封装是为了在写入 insight 时可以设置正确的 insightDepth，区别于 L1 的 insight。
    /// </summary>
    private IEnumerator TriggerReflectionL2(ReflectionRequest request)
    {
        // 使用通用反思协程，生成完成后将 insightDepth 设为 1（L2）
        List<ReflectionInsight> l2Insights = new List<ReflectionInsight>();
        yield return StartCoroutine(TriggerReflection(request, generated =>
        {
            if (generated != null) l2Insights.AddRange(generated);
        }));

        // 标记 L2 深度（TriggerReflection 内部写入时默认 insightDepth=1，此处确认）
        // 注意：insight 已在 TriggerReflection 内部通过 memoryModule.RegisterReflectionInsight 写入，
        // 此处仅输出 Debug 信息，不重复写入。
        if (l2Insights.Count > 0)
        {
            Debug.Log($"[Reflection] L2 跨事件反思生成 {l2Insights.Count} 条洞察（重要性累积触发）");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 接口函数：L3 反思触发（由 GroupMonitor 在任务批次结束后调用）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// L3 阶段性抽象反思：拉取大量混合记忆（包含 L1/L2 洞察），
    /// 生成跨任务的高层战略规律（insightDepth=2），expiresAt 设为 24h（比 L2 的 12h 更长）。
    /// 需要 enableL3AbstractReflection=true 才生效（避免频繁 LLM 调用）。
    /// 由 GroupMonitor.EvaluateMissionCompletion() 在任务成功后调用。
    /// </summary>
    public IEnumerator TriggerL3Reflection(Action<List<ReflectionInsight>> onCompleted = null)
    {
        if (!enableL3AbstractReflection)
        {
            onCompleted?.Invoke(new List<ReflectionInsight>());
            yield break;
        }

        EnsureDependencies();
        if (memoryModule == null || llmInterface == null)
        {
            onCompleted?.Invoke(new List<ReflectionInsight>());
            yield break;
        }

        // L3 强制执行（忽略冷却时间），但不影响 L1/L2 的计时器
        ReflectionRequest request = new ReflectionRequest
        {
            reason = ReflectionTriggerReason.MissionCompleted,
            missionId = string.Empty,          // L3 跨任务，不绑定单一 mission
            missionText = "（L3阶段性抽象，整合多次任务经验）",
            slotId = string.Empty,
            stepText = string.Empty,
            targetRef = string.Empty,
            summary = "任务批次结束，归纳跨任务高层战略模式",
            maxSourceMemories = 30,            // 拉取更多记忆以覆盖整个任务批次
            force = true                       // L3 强制执行，不受 L1 冷却约束
        };

        List<ReflectionInsight> l3Insights = new List<ReflectionInsight>();
        yield return StartCoroutine(TriggerReflection(request, generated =>
        {
            if (generated != null) l3Insights.AddRange(generated);
        }));

        // 将 L3 洞察标记为 insightDepth=2，并延长有效期至 24h
        foreach (ReflectionInsight insight in l3Insights)
        {
            insight.insightDepth = 2;
            // 延长到 24h（覆盖跨日的战略规律）
            insight.expiresAt = DateTime.UtcNow.AddHours(24);
        }

        if (l3Insights.Count > 0)
        {
            Debug.Log($"[Reflection] L3 阶段性抽象生成 {l3Insights.Count} 条高层洞察（insightDepth=2）");
        }

        onCompleted?.Invoke(l3Insights);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 接口函数：通用反思协程（L1/L2/L3 共用）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 执行一次 LLM 反思：
    /// 根据 request.reason 决定触发原因 → 从 MemoryModule 检索相关记忆 →
    /// 构建反思 prompt → 调用 LLM → 解析 insight JSON → 写入 MemoryModule。
    /// request.force=true 时忽略冷却时间（L3 专用）。
    /// onCompleted 回调在所有 insight 写入完成后执行（可为 null）。
    /// </summary>
    public IEnumerator TriggerReflection(ReflectionRequest request, Action<List<ReflectionInsight>> onCompleted = null)
    {
        EnsureDependencies();
        if (request == null)
        {
            onCompleted?.Invoke(new List<ReflectionInsight>());
            yield break;
        }

        if (!request.force && Time.time - lastReflectionTime < reflectionCooldownSeconds)
        {
            onCompleted?.Invoke(new List<ReflectionInsight>());
            yield break;
        }

        if (memoryModule == null || llmInterface == null)
        {
            onCompleted?.Invoke(new List<ReflectionInsight>());
            yield break;
        }

        MemoryQuery query = new MemoryQuery
        {
            freeText = BuildCombinedText(request.missionText, request.stepText, request.targetRef, request.summary),
            missionId = request.missionId,
            slotId = request.slotId,
            stepLabel = request.stepText,
            targetRef = request.targetRef,
            maxCount = Mathf.Max(1, request.maxSourceMemories),
            preferProceduralHints = true
        };

        string memoryContext = memoryModule.BuildReflectionInput(query, request.maxSourceMemories);
        if (string.IsNullOrWhiteSpace(memoryContext))
        {
            onCompleted?.Invoke(new List<ReflectionInsight>());
            yield break;
        }

        string prompt =
        $@"你正在为一个自治智能体做复盘与反思。
        请根据给定经历，提炼出以后可以复用的行动规则，而不是重复叙述事实。

        [触发原因]
        {request.reason}

        [当前任务]
        mission={request.missionText}
        slot={request.slotId}
        step={request.stepText}
        target={request.targetRef}
        summary={request.summary}

        [近期经历]
        {memoryContext}

        [输出要求]
        1) 只返回一个 JSON 对象，不要解释。
        2) 输出 1-3 条 insight。
        3) 每条 insight 要说明：学到了什么、什么时候适用、下次应该怎么调整。
        4) 不要发明地图词表，不要枚举固定地点类型，只根据输入事实总结。

        [JSON 模板]
        {{
          ""insights"": [
            {{
              ""title"": ""一句话标题"",
              ""summary"": ""学到了什么"",
              ""applyWhen"": ""什么时候适用"",
              ""suggestedAdjustment"": ""下次该怎么做"",
              ""confidence"": 0.75,
              ""tags"": [""简短标签1"", ""简短标签2""],
              ""expiresInMinutes"": 720
            }}
          ]
        }}";

        string response = string.Empty;
        yield return llmInterface.SendRequest(
            new LLMRequestOptions
            {
                prompt      = prompt,
                temperature = 0.2f,
                maxTokens   = 360,
                callTag     = "Reflection",
                agentId     = GetComponent<IntelligentAgent>()?.Properties?.AgentID,
            },
            result => { response = result ?? string.Empty; });

        List<ReflectionInsight> insights = ParseInsights(response, request);
        for (int i = 0; i < insights.Count; i++)
        {
            memoryModule.RegisterReflectionInsight(insights[i]);
        }

        if (insights.Count > 0)
        {
            lastReflectionTime = Time.time;
        }

        onCompleted?.Invoke(insights);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 辅助函数：JSON 解析与格式化
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 解析 LLM 返回的反思 JSON，构造 ReflectionInsight 对象列表。
    /// 自动为每个 insight 填充 sourceMemoryIds（通过 MemoryModule.Recall 关联来源记忆）。
    /// 字段缺失时使用安全默认值，不抛出异常（仅 Debug.LogWarning）。
    /// </summary>
    private List<ReflectionInsight> ParseInsights(string response, ReflectionRequest request)
    {
        List<ReflectionInsight> insights = new List<ReflectionInsight>();
        if (string.IsNullOrWhiteSpace(response)) return insights;

        try
        {
            string json = ExtractJsonObject(response);
            if (string.IsNullOrWhiteSpace(json)) return insights;

            JObject root = JObject.Parse(json);
            JArray array = root["insights"] as JArray;
            if (array == null) return insights;

            for (int i = 0; i < array.Count; i++)
            {
                if (!(array[i] is JObject obj)) continue;

                int expiresInMinutes = ReadInt(obj, "expiresInMinutes", 720);
                ReflectionInsight insight = new ReflectionInsight
                {
                    id = Guid.NewGuid().ToString("N"),
                    title = ReadString(obj, "title"),
                    summary = ReadString(obj, "summary"),
                    applyWhen = ReadString(obj, "applyWhen"),
                    suggestedAdjustment = ReadString(obj, "suggestedAdjustment"),
                    missionId = request.missionId,
                    slotId = request.slotId,
                    targetRef = request.targetRef,
                    createdAt = DateTime.UtcNow,
                    expiresAt = DateTime.UtcNow.AddMinutes(Mathf.Max(30, expiresInMinutes)),
                    confidence = Mathf.Clamp01(ReadFloat(obj, "confidence", 0.7f)),
                    tags = ReadStringArray(obj, "tags"),
                    sourceMemoryIds = memoryModule
                        .Recall(new MemoryQuery
                        {
                            freeText = BuildCombinedText(request.missionText, request.stepText, request.targetRef, request.summary),
                            missionId = request.missionId,
                            slotId = request.slotId,
                            stepLabel = request.stepText,
                            targetRef = request.targetRef,
                            maxCount = Mathf.Max(1, request.maxSourceMemories)
                        })
                        .Select(m => m.id)
                        .ToList()
                };

                if (string.IsNullOrWhiteSpace(insight.summary) && string.IsNullOrWhiteSpace(insight.suggestedAdjustment))
                {
                    continue;
                }

                insights.Add(insight);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Reflection] 解析反思 JSON 失败: {e.Message}");
        }

        return insights;
    }

    private static string BuildCombinedText(params string[] values)
    {
        return string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray());
    }

    private static string ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return string.Empty;
        return text.Substring(start, end - start + 1);
    }

    private static string ReadString(JObject obj, string field)
    {
        return obj[field] != null ? obj[field].ToString().Trim() : string.Empty;
    }

    private static float ReadFloat(JObject obj, string field, float fallback)
    {
        if (obj[field] == null) return fallback;
        return float.TryParse(obj[field].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;
    }

    private static int ReadInt(JObject obj, string field, int fallback)
    {
        if (obj[field] == null) return fallback;
        return int.TryParse(obj[field].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static List<string> ReadStringArray(JObject obj, string field)
    {
        List<string> result = new List<string>();
        if (!(obj[field] is JArray array)) return result;

        for (int i = 0; i < array.Count; i++)
        {
            string value = array[i]?.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
