using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// 反思模块：
/// 它不是定时写总结，而是在“任务结束、连续失败、被卡住、看到重要异常”这些时刻，
/// 从记忆里抽取最近经历，交给 LLM 提炼成可复用的行动规则。
/// </summary>
public class ReflectionModule : MonoBehaviour
{
    [Header("反思触发")]
    [Min(0f)] public float reflectionCooldownSeconds = 90f;
    [Min(1)] public int repeatedFailureThreshold = 2;
    [Min(1)] public int defaultReflectionMemoryCount = 8;
    public bool autoReflectOnMissionCompletion = true;

    private MemoryModule memoryModule;
    private LLMInterface llmInterface;
    private float lastReflectionTime = -999f;
    private int consecutiveActionFailures;

    private void Start()
    {
        EnsureDependencies();
    }

    private void EnsureDependencies()
    {
        memoryModule = GetComponent<MemoryModule>();
        if (llmInterface == null)
        {
            llmInterface = FindObjectOfType<LLMInterface>();
        }
    }

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

    public string GetActionGuidance(
        string missionText,
        string missionId,
        string slotId,
        string stepText,
        string targetRef,
        int maxInsights = 2)
    {
        EnsureDependencies();
        if (memoryModule == null) return "无稳定反思策略";

        MemoryQuery query = new MemoryQuery
        {
            freeText = BuildCombinedText(missionText, stepText, targetRef),
            missionId = missionId,
            slotId = slotId,
            stepLabel = stepText,
            targetRef = targetRef,
            maxCount = maxInsights,
            preferProceduralHints = true
        };

        return BuildInsightSummary(memoryModule.GetRelevantInsights(query, maxInsights), "无稳定反思策略");
    }

    /// <summary>
    /// 真正执行一次反思：
    /// 输入是“为什么现在要复盘”，输出是几条以后还能拿来用的 insight。
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

    private static string BuildInsightSummary(List<ReflectionInsight> insights, string fallback)
    {
        if (insights == null || insights.Count == 0) return fallback;

        StringBuilder builder = new StringBuilder("反思策略:");
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

        return builder.ToString();
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
