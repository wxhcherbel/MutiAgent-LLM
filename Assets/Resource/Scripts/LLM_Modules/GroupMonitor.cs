// GroupMonitor.cs
// 组级裁判通信与任务监控器，仅挂载于组长 GameObject（由 PlanningModule 初始化）。
//
// 职责分区：
//   [通信区]     向裁判层广播本队完成事件；轮询裁判层并将事件写入本队白板
//   [监控区]     定时检查本队完成状态（规则 → LLM 两阶段判断）
//   [紧急事件区] 持有 MADCoordinator，接收 IncidentReport 和 MemberOpinion 并转交
// ═══════════════════════════════════════════════════════════════════════════
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class GroupMonitor : MonoBehaviour
{
    // ─── 数据结构区 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 白板中 RefereeNotice 条目使用的固定约束 ID，供 QueryEntries 过滤。
    /// 不与规划约束 ID 冲突（以双下划线开头）。
    /// </summary>
    public const string RefereeConstraintId = "__ref__";

    // ─── 私有字段 ─────────────────────────────────────────────────────────────

    private GroupDef           myGroup;
    private string             groupId;
    private string             leaderId;
    private string[]           otherLeaderIds;
    private LLMInterface       llmInterface;
    private CommunicationModule _commModule;

    private bool missionBroadcasted;

    /// <summary>
    /// 记忆模块（组长自身的，在 Initialize 时从同 GameObject 获取）。
    /// 任务完成时调用 RememberMissionOutcome() 写入最终结果记忆。
    /// </summary>
    private MemoryModule _memoryModule;

    /// <summary>
    /// 反思模块（组长自身的，在 Initialize 时从同 GameObject 获取）。
    /// 任务完成时触发 L1 反思（MissionCompleted）；可选启动 L3 阶段性抽象反思。
    /// </summary>
    private ReflectionModule _reflectionModule;

    // ─── MAD 辩论协调器 ───────────────────────────────────────────────────────

    private MADCoordinator _madCoordinator;

    // ─────────────────────────────────────────────────────────────────────────
    // 初始化（由 PlanningModule 调用）
    // ─────────────────────────────────────────────────────────────────────────

    public void Initialize(GroupDef group, string gId, string lId,
                           string[] otherLeaders, LLMInterface llm)
    {
        myGroup        = group;
        groupId        = gId;
        leaderId       = lId;
        otherLeaderIds = otherLeaders ?? Array.Empty<string>();
        llmInterface   = llm;
        _commModule    = GetComponent<CommunicationModule>();

        // 记忆和反思模块（组长侧，用于任务完成时的经验沉淀与反思触发）
        _memoryModule    = GetComponent<MemoryModule>();
        _reflectionModule = GetComponent<ReflectionModule>();

        // 向裁判层注册（使组长可收到 push 事件，轮询另由 PollRefereeLoop 保证）
        if (RefereeManager.Instance != null)
            RefereeManager.Instance.RegisterAgent(leaderId, OnRefereeEventPush);

        // MAD 辩论协调器（组长侧，用于协调两轮辩论并执行决策）
        // 传入 _memoryModule：仲裁决策完成后写入 AgentMemoryKind.Decision，同时自动提炼 Policy
        var planning = GetComponent<PlanningModule>();
        _madCoordinator = new MADCoordinator(
            this, myGroup, groupId, leaderId, llmInterface, _commModule, planning, _memoryModule);

        StartCoroutine(MonitorLoop());
        StartCoroutine(PollRefereeLoop());

        Debug.Log($"[GroupMonitor] {leaderId} 初始化完成，监控组 {groupId}，" +
                  $"其他队组长={string.Join(",", otherLeaderIds)}");
    }

    private void OnDestroy()
    {
        if (RefereeManager.Instance != null)
            RefereeManager.Instance.UnregisterAgent(leaderId);
    }

    // ─── 通信区（裁判层广播/接收）────────────────────────────────────────────

    /// <summary>
    /// 广播本队任务完成事件到裁判层（由监控区 EvaluateMissionCompletion 调用）。
    /// visibleTo 为其他队组长，避免自回环。
    /// </summary>
    private void BroadcastMissionComplete(MissionCompletePayload payload)
    {
        if (RefereeManager.Instance == null) return;

        RefereeManager.Instance.BroadcastEvent(new RefereeEvent
        {
            eventType    = RefereeEventType.MissionResult,
            constraintId = null,
            payload      = JsonUtility.ToJson(payload),
            visibleTo    = otherLeaderIds,
        });
        Debug.Log($"[GroupMonitor] {leaderId} 广播任务完成: {payload.missionDesc}");
    }

    /// <summary>
    /// 裁判层 push 回调（RegisterAgent 注册，BroadcastEvent 触发）。
    /// 直接写入白板，与 PollRefereeLoop 的写入逻辑共享 WriteRefereeNotice。
    /// </summary>
    private void OnRefereeEventPush(RefereeEvent evt)
    {
        // push 与 poll 均可触发写入；AcknowledgeEvent 确保不重复处理
        WriteRefereeNotice(evt);
    }

    /// <summary>
    /// 轮询裁判层未读事件（每 3s），将可见事件写入本队白板（RefereeNotice 类型），
    /// 供本队所有成员 ADM 在下次 CheckWhiteboardAndGetContext() 时读取。
    /// </summary>
    private IEnumerator PollRefereeLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(3f);
            if (RefereeManager.Instance == null) continue;

            var events = RefereeManager.Instance.PollEvents(leaderId);
            foreach (var evt in events)
                WriteRefereeNotice(evt);
        }
    }

    /// <summary>
    /// 将单条裁判事件写入本队白板，并 Acknowledge 避免重复写入。
    /// </summary>
    private void WriteRefereeNotice(RefereeEvent evt)
    {
        if (SharedWhiteboard.Instance == null) return;

        var src = JsonUtility.FromJson<MissionCompletePayload>(evt.payload);
        var notice = new RefereeNoticeData
        {
            fromGroupId = src?.groupId ?? "unknown",
            eventType   = evt.eventType.ToString(),
            summary     = $"Team {src?.groupId} completed: {src?.missionDesc}",
            receivedAt  = Time.time,
        };

        SharedWhiteboard.Instance.WriteEntry(groupId, new WhiteboardEntry
        {
            agentId      = leaderId,
            constraintId = RefereeConstraintId,
            entryType    = WhiteboardEntryType.RefereeNotice,
            status       = 1,
            progress     = JsonUtility.ToJson(notice),
        });

        RefereeManager.Instance.AcknowledgeEvent(leaderId, evt.eventId);
        Debug.Log($"[GroupMonitor] {leaderId} 收到裁判通知写入白板: {notice.summary}");
    }

    // ─── 监控区（任务完成判断）──────────────────────────────────────────────

    /// <summary>
    /// 主监控循环（每 5s 检查一次）。
    /// Phase 1 通过后进入 Phase 2，两阶段均通过才广播裁判事件。
    /// missionBroadcasted 防止重复广播。
    /// </summary>
    private IEnumerator MonitorLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(5f);

            if (missionBroadcasted) yield break;

            if (!CheckAllStepsDone()) continue;

            Debug.Log($"[GroupMonitor] {leaderId} 规则判断：所有成员已完成步骤，进入 LLM 评估");
            yield return StartCoroutine(EvaluateMissionCompletion());
        }
    }

    /// <summary>
    /// Phase 1：规则判断。
    /// 条件：本组所有 memberIds 在白板中均有 DoneSignal（status=1）。
    /// </summary>
    private bool CheckAllStepsDone()
    {
        if (SharedWhiteboard.Instance == null || myGroup?.memberIds == null) return false;

        var entries = SharedWhiteboard.Instance.QueryEntries(groupId);
        return myGroup.memberIds.All(memberId =>
            entries.Any(e =>
                e.agentId   == memberId &&
                e.entryType == WhiteboardEntryType.DoneSignal &&
                e.status    == 1));
    }

    /// <summary>
    /// Phase 2：LLM 判断。
    /// 收集所有成员 DoneSignal.progress → 构建 prompt → 调用 LLM →
    /// 解析 {"completed":bool,"reason":"..."} → 若 true 则广播。
    /// </summary>
    private IEnumerator EvaluateMissionCompletion()
    {
        if (llmInterface == null) yield break;

        var entries       = SharedWhiteboard.Instance.QueryEntries(groupId);
        string summary    = BuildMemberSummary(entries);
        string prompt     = BuildEvalPrompt(summary);

        string llmResult  = null;
        yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 200));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"[GroupMonitor] {leaderId} LLM 评估返回空，跳过本轮");
            yield break;
        }

        Debug.Log($"[GroupMonitor] {leaderId} LLM 评估结果: {llmResult}");

        bool completed = false;
        string reason  = string.Empty;
        try
        {
            // 提取 JSON 对象（兼容 ```json ... ``` 包裹）
            var match = System.Text.RegularExpressions.Regex.Match(
                llmResult, @"```(?:json)?\s*([\s\S]*?)```");
            string json = match.Success ? match.Groups[1].Value.Trim() : llmResult.Trim();

            var obj = JsonUtility.FromJson<EvalResult>(json);
            if (obj != null)
            {
                completed = obj.completed;
                reason    = obj.reason ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GroupMonitor] {leaderId} LLM 评估 JSON 解析失败: {ex.Message}");
            yield break;
        }

        if (!completed)
        {
            Debug.Log($"[GroupMonitor] {leaderId} LLM 评估：任务未完成。原因: {reason}");
            yield break;
        }

        Debug.Log($"[GroupMonitor] {leaderId} LLM 评估：任务完成！原因: {reason}");
        missionBroadcasted = true;

        var payload = new MissionCompletePayload
        {
            groupId         = groupId,
            missionDesc     = myGroup?.mission ?? string.Empty,
            memberSummaries = summary.Split('\n'),
            missionSuccess  = true,
        };
        BroadcastMissionComplete(payload);

        // 将任务完成结果写入记忆（importance=0.94，确保跨任务检索时优先召回）
        string missionOutcomeSummary = $"任务完成: {myGroup?.mission ?? "未知"}。{reason}";
        _memoryModule?.RememberMissionOutcome(
            missionId: groupId,
            slotId: leaderId,
            summary: missionOutcomeSummary,
            success: true,
            targetRef: myGroup?.mission ?? string.Empty);

        // 触发 L1 任务完成反思（沉淀成功策略，供下一任务规划参考）
        if (_reflectionModule != null)
        {
            StartCoroutine(_reflectionModule.TriggerReflection(
                new ReflectionRequest
                {
                    reason = ReflectionTriggerReason.MissionCompleted,
                    missionId = groupId,
                    missionText = myGroup?.mission ?? string.Empty,
                    slotId = leaderId,
                    stepText = string.Empty,
                    targetRef = string.Empty,
                    summary = missionOutcomeSummary,
                    maxSourceMemories = 8,
                    force = false  // 受冷却时间约束，避免高频任务时连续触发反思
                }));

            // 可选触发 L3 阶段性抽象反思（由 enableL3AbstractReflection 控制）
            StartCoroutine(_reflectionModule.TriggerL3Reflection(null));
        }
    }

    /// <summary>
    /// 从白板中收集所有成员的 DoneSignal.progress，拼接为多行摘要字符串。
    /// </summary>
    private string BuildMemberSummary(List<WhiteboardEntry> entries)
    {
        return string.Join("\n", entries
            .Where(e => e.entryType == WhiteboardEntryType.DoneSignal && e.status == 1)
            .Select(e => $"- {e.agentId}: {e.progress}"));
    }

    /// <summary>
    /// 构建 LLM 任务完成度评估 prompt。
    /// LLM 需回答 {"completed": true/false, "reason": "一句话原因"}
    /// </summary>
    private string BuildEvalPrompt(string memberSummary)
    {
        return
            "你是任务完成度评估器。仅输出 JSON，不要解释。\n\n" +
            $"本队任务：{myGroup?.mission ?? "未知"}\n\n" +
            $"成员完成情况：\n{memberSummary}\n\n" +
            "问题：本队是否已成功完成任务？\n" +
            "回答格式：{\"completed\": true/false, \"reason\": \"一句话原因\"}";
    }

    // ─── 内部序列化辅助类 ─────────────────────────────────────────────────────

    [Serializable]
    private class EvalResult
    {
        public bool   completed;
        public string reason;
    }

    // ─── 紧急事件区（MAD 辩论入口）──────────────────────────────────────────

    /// <summary>
    /// 接收成员上报的 IncidentReport（由 CommunicationModule 路由）或 MADGateway 本地直调。
    /// 转交给 MADCoordinator 处理（含去重、两轮辩论、仲裁决策）。
    /// </summary>
    public void HandleIncident(IncidentReport report)
    {
        _madCoordinator?.HandleIncident(report);
    }

    /// <summary>
    /// 接收成员回传的辩论意见（由 CommunicationModule 路由 IncidentOpinion 消息调用）。
    /// </summary>
    public void OnOpinionReceived(MemberOpinion opinion)
    {
        _madCoordinator?.AddOpinion(opinion);
    }
}

// ─── 跨文件共享数据类（供 GroupMonitor 和 ActionDecisionModule 使用）──────────

/// <summary>
/// 广播到裁判层的任务完成事件 payload，供对方队组长反序列化。
/// </summary>
[Serializable]
public class MissionCompletePayload
{
    public string   groupId;
    public string   missionDesc;
    public string[] memberSummaries;
    public bool     missionSuccess;
}

/// <summary>
/// 组长从裁判层收到事件后写入本队白板的通知数据（RefereeNotice.progress）。
/// 队员 ADM 的 CheckWhiteboardAndGetContext() 读取并传给 LLM。
/// </summary>
[Serializable]
public class RefereeNoticeData
{
    public string fromGroupId;
    public string eventType;
    public string summary;
    public float  receivedAt;
}
