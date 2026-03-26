// LLM_Modules/ActionDecisionModule.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

public class ActionDecisionModule : MonoBehaviour
{
    // ─── 外部依赖 ─────────────────────────────────────────────────
    private PlanningModule planningModule;
    private LLMInterface llmInterface;
    private AgentProperties agentProperties;
    private AgentDynamicState agentState;
    private CommunicationModule commModule;
    private CampusGrid2D campusGrid;

    // ─── ADM 状态 ─────────────────────────────────────────────────
    private ActionExecutionContext ctx;
    private ADMStatus status = ADMStatus.Idle;

    // ─── 感知事件队列 ─────────────────────────────────────────────
    private readonly Queue<(string desc, string location)> pendingPerceptionEvents = new();

    // ─── 协程句柄 ─────────────────────────────────────────────────
    private Coroutine activeCoroutine;

    // ─── 重规划计数（防死锁）────────────────────────────────────
    private int replanCount;
    private const int MaxReplanCount = 5;

    // ─── 滚动规划常量 ─────────────────────────────────────────────
    private const int MaxIterations = 10;

    // ─── JSON 提取正则 ────────────────────────────────────────────
    private static readonly Regex JsonBlockRe = new Regex(@"```(?:json)?\s*([\s\S]*?)```");

    // ─────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────

    private void Start()
    {
        planningModule = GetComponent<PlanningModule>();
        llmInterface = FindObjectOfType<LLMInterface>();
        commModule = GetComponent<CommunicationModule>();
        campusGrid = FindObjectOfType<CampusGrid2D>();

        IntelligentAgent agent = GetComponent<IntelligentAgent>();
        if (agent != null)
        {
            agentProperties = agent.Properties;
            agentState = agent.CurrentState;
        }
    }

    private void Update()
    {
        //TODO: 感知事件检测；
    }

    // ─────────────────────────────────────────────────────────────
    // 公共接口
    // ─────────────────────────────────────────────────────────────

    /// <summary>由 PlanningModule 在 Active 状态下调用，传入当前步骤。</summary>
    public void StartStep(PlanStep step)
    {
        if (step == null) return;
        if (activeCoroutine != null) { StopCoroutine(activeCoroutine); activeCoroutine = null; }

        // 解析步骤关联的结构化约束（C1/C2/C3 由 LLM#4 分配到步骤）
        StructuredConstraint[] stepConstraints = Array.Empty<StructuredConstraint>();
        if (planningModule != null)
        {
            var list = new List<StructuredConstraint>();
            // 加载步骤显式绑定的约束（C1/C2/C3）
            if (step.constraintIds != null)
                foreach (var id in step.constraintIds)
                {
                    var c = planningModule.GetConstraint(id);
                    if (c != null) list.Add(c);
                }
            stepConstraints = list.ToArray();
        }

        ctx = new ActionExecutionContext
        {
            msnId                   = planningModule?.GetCurrentMissionId() ?? string.Empty,
            stepId                  = step.stepId,
            stepText                = step.text,
            stepConstraints         = stepConstraints,
            role                    = agentProperties != null ? agentProperties.Role : RoleType.Scout,
            actionQueue             = null,
            currentActionIdx        = 0,
            status                  = ADMStatus.Idle,
            currentLocationName     = ResolveCurrentLocationName(),
            recentEvents            = Array.Empty<string>(),
            executedActionsSummary  = new List<string>(),
            iterationCount          = 0,
            isRollingMode           = true,
        };

        replanCount = 0;
        SetStatus(ADMStatus.Idle);
        activeCoroutine = StartCoroutine(RunRollingLoop(step));
    }

    /// <summary>由感知模块调用，记录事件，稍后在 Running 状态统一处理。</summary>
    public void OnPerceptionEvent(string eventDescription, string locationName)
    {
        if (string.IsNullOrWhiteSpace(eventDescription)) return;
        pendingPerceptionEvents.Enqueue((eventDescription, locationName ?? string.Empty));
        Debug.Log($"[ADM] {agentProperties?.AgentID} 收到感知事件: {eventDescription}");
    }

    /// <summary>执行层查询当前应执行的原子动作。</summary>
    public AtomicAction GetCurrentAction()
    {
        if (ctx?.actionQueue == null || ctx.currentActionIdx >= ctx.actionQueue.Length) return null;
        return ctx.actionQueue[ctx.currentActionIdx];
    }

    /// <summary>
    /// 执行层通知当前动作已完成，ADM 推进队列。
    /// 滚动模式下：批次耗尽时设 BatchDone，由 RunRollingLoop 处理下一轮。
    /// 非滚动模式下：队列耗尽时直接 Done 并通知 PlanningModule。
    /// </summary>
    public void CompleteCurrentAction()
    {
        if (ctx?.actionQueue == null) return;
        ctx.currentActionIdx++;

        if (ctx.currentActionIdx >= ctx.actionQueue.Length)
        {
            if (ctx.isRollingMode)
            {
                SetStatus(ADMStatus.BatchDone);
                Debug.Log($"[ADM] {agentProperties?.AgentID} 当前批次动作完成，等待下一轮滚动规划");
            }
            else
            {
                SetStatus(ADMStatus.Done);
                planningModule?.CompleteCurrentStep();
                Debug.Log($"[ADM] {agentProperties?.AgentID} 当前步骤动作全部完成，通知 PlanningModule");
                if (agentState != null) agentState.Status = AgentStatus.Idle;
            }
            return;
        }

        Debug.Log($"[ADM] {agentProperties?.AgentID} 切换到动作 {ctx.currentActionIdx + 1}/{ctx.actionQueue.Length}");
    }

    /// <summary>当前 ADM 是否空闲（可接受新步骤）。</summary>
    public bool IsIdle() =>
        status == ADMStatus.Idle || status == ADMStatus.Done || status == ADMStatus.Failed;

    /// <summary>返回当前 ADM 状态（供外部监控使用）。</summary>
    public ADMStatus GetStatus() => status;

    /// <summary>向后兼容：由 IntelligentAgent.MakeDecisionCoroutine 调用的协程入口。</summary>
    public IEnumerator DecideNextAction()
    {
        SyncCampusGridReference();
        commModule?.ProcessMessages();

        if (planningModule == null || !planningModule.HasActiveMission()) yield break;

        PlanStep currentStep = planningModule.GetCurrentStep();
        if (currentStep == null) yield break;

        if (ctx != null && ctx.stepId == currentStep.stepId &&
            status != ADMStatus.Idle && status != ADMStatus.Done && status != ADMStatus.Failed)
        {
            yield return null;
            yield break;
        }

        if (status == ADMStatus.Idle || status == ADMStatus.Done || status == ADMStatus.Failed)
            StartStep(currentStep);

        yield return null;
    }

    // ─────────────────────────────────────────────────────────────
    // 滚动规划主循环
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 滚动规划主循环协程。
    /// 每轮：检查 C3 → 读白板/裁判 → LLM 判断是否完成 + 生成下 1-3 个动作 → 执行 → 循环。
    /// </summary>
    private IEnumerator RunRollingLoop(PlanStep step)
    {
        SetStatus(ADMStatus.Interpreting);

        while (ctx.iterationCount < MaxIterations)
        {
            // ── 1. 读取白板上下文 ───────────────────────────────────
            string whiteboardCtx = ReadWhiteboardContext();

            // ── 2. 检查 C3 等待条件 ─────────────────────────────────
            AtomicAction waitAction = CheckC3WaitConditions();
            if (waitAction != null)
            {
                ctx.actionQueue = new[] { waitAction };
                ctx.currentActionIdx = 0;
                SetStatus(ADMStatus.Running);
                yield return new WaitUntil(() => status == ADMStatus.BatchDone || status == ADMStatus.Failed);
                if (status == ADMStatus.Failed) yield break;
                // Wait 完成后重新进入本轮循环检查（不增加迭代计数）
                SetStatus(ADMStatus.Interpreting);
                continue;
            }

            // ── 3. 读取裁判事件 ─────────────────────────────────────
            string refereeCtx = ReadRefereeContext();

            // ── 4. 获取地图信息 ─────────────────────────────────────
            string relativeMap = campusGrid != null
                ? MapTopologySerializer.GetAgentRelativeMap(campusGrid, agentState.Position)
                : "(地图不可用)";

            // ── 5. 构建滚动规划提示词 ────────────────────────────────
            string prompt = BuildRollingPrompt(step, whiteboardCtx, refereeCtx, relativeMap);

            // ── 6. 调用 LLM ──────────────────────────────────────────
            string llmResult = null;
            yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 900));

            if (string.IsNullOrWhiteSpace(llmResult))
            {
                Debug.LogError($"[ADM] {agentProperties?.AgentID} 滚动规划第{ctx.iterationCount + 1}轮 LLM 返回空");
                SetStatus(ADMStatus.Failed);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }
            Debug.Log($"[ADM] {agentProperties?.AgentID} 滚动规划第{ctx.iterationCount + 1}轮 LLM 回复: {llmResult}");

            // ── 7. 解析 JSON ─────────────────────────────────────────
            RollingPlanResult planResult = null;
            try
            {
                planResult = JsonConvert.DeserializeObject<RollingPlanResult>(ExtractJson(llmResult));
            }
            catch (Exception e)
            {
                Debug.LogError($"[ADM] 滚动规划 JSON 解析失败: {e.Message}");
                SetStatus(ADMStatus.Failed);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }

            if (planResult == null)
            {
                Debug.LogError($"[ADM] 滚动规划解析结果为 null");
                SetStatus(ADMStatus.Failed);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }

            // ── 8. 处理 isDone = true ────────────────────────────────
            if (planResult.isDone)
            {
                Debug.Log($"[ADM] {agentProperties?.AgentID} 步骤完成: {planResult.doneReason}");

                // 写 DoneSignal 到白板（C2 约束）
                WriteWhiteboardDoneSignals();

                // 等待 C2 同步（若有 syncWith）
                yield return StartCoroutine(WaitForC2Sync());

                planningModule?.CompleteCurrentStep();
                SetStatus(ADMStatus.Done);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }

            // ── 9. nextActions 为空的保护 ──────────────────────────
            if (planResult.nextActions == null || planResult.nextActions.Length == 0)
            {
                Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM 返回 isDone=false 但 nextActions 为空，强制完成步骤");
                planningModule?.CompleteCurrentStep();
                SetStatus(ADMStatus.Done);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }

            // ── 10. 执行当前批次动作 ─────────────────────────────────
            ctx.actionQueue = planResult.nextActions;
            ctx.currentActionIdx = 0;

            // 写 C3 sign=-1 互斥占位（将 MoveTo 目标写入白板，供其他 Agent 避开）
            WriteC3MutexClaims(planResult.nextActions);

            // 写 IntentAnnounce 到白板（可选，通知队友意图）
            WriteWhiteboardIntentSignals();

            SetStatus(ADMStatus.Running);
            yield return new WaitUntil(() => status == ADMStatus.BatchDone || status == ADMStatus.Failed);

            if (status == ADMStatus.Failed) yield break;

            // ── 11. 更新执行历史，进入下一迭代 ──────────────────────
            UpdateHistory(planResult.nextActions);
            ctx.iterationCount++;
            SetStatus(ADMStatus.Interpreting);
        }

        // 超出最大迭代次数，强制完成
        Debug.LogWarning($"[ADM] {agentProperties?.AgentID} 滚动规划超出最大迭代次数 {MaxIterations}，强制完成步骤");
        planningModule?.CompleteCurrentStep();
        SetStatus(ADMStatus.Done);
        if (agentState != null) agentState.Status = AgentStatus.Idle;
    }

    // ─────────────────────────────────────────────────────────────
    // 滚动规划提示词构建
    // ─────────────────────────────────────────────────────────────

    private string BuildRollingPrompt(PlanStep step, string whiteboardCtx, string refereeCtx, string relativeMap)
    {
        // 已执行历史
        string historyBlock = ctx.executedActionsSummary != null && ctx.executedActionsSummary.Count > 0
            ? string.Join("\n", ctx.executedActionsSummary.ConvertAll((s) => "  • " + s))
            : "  （本步骤尚无已执行动作）";

        // 感知快照
        string perception = BuildPerceptionSnapshot();
        string perceptionBlock = string.IsNullOrWhiteSpace(perception) ? "无感知数据" : perception;

        // 协同约束摘要
        string constraintBlock = BuildConstraintSummary();

        return
            "你是无人机战术执行规划器，负责滚动式地为当前步骤生成下一批原子动作，并判断步骤是否已完成。\n\n" +
            "═══ 步骤目标 ═══\n" +
            $"步骤文本：{step.text}\n" +
            $"完成条件（doneCond）：{(string.IsNullOrWhiteSpace(step.doneCond) ? "未指定" : step.doneCond)}\n\n" +
            "═══ 已执行历史 ═══\n" +
            historyBlock + "\n\n" +
            "═══ 当前状态 ═══\n" +
            $"当前角色：{ctx.role}\n" +
            $"当前位置：{ctx.currentLocationName}\n\n" +
            "═══ 环境感知 ═══\n" +
            perceptionBlock + "\n\n" +
            "═══ 周边地图（以本机为中心，半径300m） ═══\n" +
            relativeMap + "\n\n" +
            "═══ 白板状态（组内协同） ═══\n" +
            (string.IsNullOrWhiteSpace(whiteboardCtx) ? "（无白板数据）" : whiteboardCtx) + "\n" +
            "（⚠ 标记的目标已被其他 Agent 占用，请在本轮 nextActions 中避免前往）\n\n" +
            "═══ 裁判事件 ═══\n" +
            (string.IsNullOrWhiteSpace(refereeCtx) ? "（无裁判事件）" : refereeCtx) + "\n\n" +
            "═══ 协同约束 ═══\n" +
            (string.IsNullOrWhiteSpace(constraintBlock) ? "无约束（本步骤独立执行）" : constraintBlock) + "\n\n" +
            "═══ 原子动作类型说明 ═══\n" +
            "• MoveTo：前往指定地点。targetName=目的地（地图中存在），spatialHint=路径偏好，actionParams=飞行参数\n" +
            "• PatrolAround：在目标地点周围环绕巡逻。targetName=巡逻中心，actionParams=必填，duration=-1=一圈后结束\n" +
            "• Observe：对目标区域定点观察。targetName=观察目标，actionParams=必填（时长等），duration=-1=条件触发结束\n" +
            "• Evade：规避障碍物或调整高度。actionParams=必填\n" +
            "• Wait：原地悬停等待。actionParams=必填等待条件（如\"等待20秒\"\"等待队友到达\"）\n" +
            "• FormationHold：与指定队友保持相对位置协同移动。targetAgentId=队友ID，actionParams=必填相对偏移\n\n" +
            "═══ 规划流程（按顺序思考） ═══\n" +
            "1. 先看已执行历史，判断步骤是否已完成（isDone=true）。\n" +
            "2. 若未完成，结合当前位置、感知、白板状态，生成下一批 1-3 个动作推进步骤。\n" +
            "3. 协同约束非空时，动作必须遵守约束要求（如等待信号、保持间距等）。\n\n" +
            "═══ 输出格式（JSON 对象，非数组） ═══\n" +
            "{\n" +
            "  \"isDone\": true/false,\n" +
            "  \"doneReason\": \"说明为何步骤完成或未完成\",\n" +
            "  \"nextActions\": [\n" +
            "    {\"actionId\":\"aa_N\",\"type\":\"MoveTo\",\"targetName\":\"地点名\",\"targetAgentId\":\"\",\"duration\":0,\"actionParams\":\"参数\",\"spatialHint\":\"\"}\n" +
            "  ]\n" +
            "}\n\n" +
            "规则：\n" +
            "1. isDone=true 时 nextActions 可为空数组 []。\n" +
            "2. isDone=false 时 nextActions 必须有 1-3 个动作。\n" +
            "3. targetName 必须是地图中存在的地点名称，禁止编造。\n" +
            "4. 每个动作必须包含全部字段：actionId / type / targetName / targetAgentId / duration / actionParams / spatialHint。\n";
    }

    private string BuildConstraintSummary()
    {
        if (ctx.stepConstraints == null || ctx.stepConstraints.Length == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var c in ctx.stepConstraints)
        {
            sb.Append($"[{c.constraintId}] {c.cType} (channel={c.channel})");
            switch (c.cType)
            {
                case "C1":
                case "Assignment":
                    sb.AppendLine($"\n  分配: subject={c.subject}, target={c.targetObject}, exclusive={c.exclusive}");
                    break;
                case "C2":
                case "Completion":
                    string sync = c.syncWith?.Length > 0 ? $", syncWith=[{string.Join(",", c.syncWith)}]" : "";
                    sb.AppendLine($"\n  完成条件: {c.condition}{sync}");
                    break;
                case "C3":
                case "Coupling":
                    if (c.sign == 1)
                        sb.AppendLine($"\n  耦合(单向等待): 我等 watchAgent={c.watchAgent} 写 ReadySignal 后才行动");
                    else if (c.sign == -1)
                        sb.AppendLine($"\n  耦合(动态互斥): 与其他 Agent 动态争夺目标，先到先得，" +
                                      $"白板中已占目标见上方白板状态区");
                    break;
                default:
                    sb.AppendLine();
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ─────────────────────────────────────────────────────────────
    // 约束处理辅助方法
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 检查 C3 约束的等待条件：
    ///   sign=+1：等待 watchAgent 写入 ReadySignal（单向前置依赖）
    ///   sign=-1：动态互斥，处理完全移至 Prompt 层（ReadWhiteboardContext）和写入层（WriteC3MutexClaims），此处不再拦截。
    /// 返回 Wait AtomicAction 表示需要等待；返回 null 表示可以继续。
    /// </summary>
    private AtomicAction CheckC3WaitConditions()
    {
        if (ctx.stepConstraints == null) return null;
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId) || SharedWhiteboard.Instance == null) return null;
        string myId = agentProperties?.AgentID ?? string.Empty;

        foreach (var c in ctx.stepConstraints)
        {
            if (c.cType != "C3" && c.cType != "Coupling") continue;

            if (c.sign == 1)
            {
                // 单向前置等待：我等 watchAgent 写 ReadySignal
                if (string.IsNullOrWhiteSpace(c.watchAgent)) continue;
                bool watchReady = SharedWhiteboard.Instance.HasSignal(
                    groupId, c.constraintId, c.watchAgent, WhiteboardEntryType.ReadySignal);
                if (!watchReady)
                {
                    Debug.Log($"[ADM] {myId} C3+1 约束 {c.constraintId} 等待 {c.watchAgent} 就绪");
                    return new AtomicAction
                    {
                        actionId      = "aa_wait_c3_plus",
                        type          = AtomicActionType.Wait,
                        targetName    = string.Empty,
                        targetAgentId = c.watchAgent,
                        duration      = 5f,
                        actionParams  = $"等待 {c.watchAgent} 就绪（约束 {c.constraintId}）",
                        spatialHint   = string.Empty,
                    };
                }
            }
            // sign=-1：不在此处拦截，由 ReadWhiteboardContext 将已占目标注入 Prompt，让 LLM 主动避开
        }
        return null;
    }

    /// <summary>读取白板上与本步骤 channel=whiteboard 约束相关的条目，格式化为字符串。
    /// C3 sign=-1 约束额外追加其他 agent 的已占目标，供 LLM 主动避开。</summary>
    private string ReadWhiteboardContext()
    {
        if (SharedWhiteboard.Instance == null || ctx.stepConstraints == null) return string.Empty;
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId)) return string.Empty;
        string myId = agentProperties?.AgentID ?? string.Empty;

        var sb = new StringBuilder();
        foreach (var c in ctx.stepConstraints)
        {
            if (c.channel != "whiteboard") continue;
            var entries = SharedWhiteboard.Instance.QueryEntries(groupId, c.constraintId);
            foreach (var e in entries)
                sb.AppendLine($"  [{e.constraintId}] agent={e.agentId}, type={e.entryType}, status={e.status}, progress={e.progress}");
        }

        // 收集 C3 sign=-1 约束中其他 agent 的已占目标，注入 Prompt 让 LLM 主动避开
        foreach (var c in ctx.stepConstraints)
        {
            if ((c.cType != "C3" && c.cType != "Coupling") || c.sign != -1) continue;
            var entries = SharedWhiteboard.Instance.QueryEntries(groupId, c.constraintId);
            foreach (var e in entries)
            {
                if (e.agentId == myId) continue;
                if (e.entryType != WhiteboardEntryType.IntentAnnounce) continue;
                if (string.IsNullOrEmpty(e.progress)) continue;
                sb.AppendLine($"  ⚠ [{e.agentId}] 已占用目标：{e.progress}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>轮询裁判事件中与本步骤约束相关的条目，格式化为字符串并标记已读。</summary>
    private string ReadRefereeContext()
    {
        if (RefereeManager.Instance == null || ctx.stepConstraints == null) return string.Empty;
        string agentId = agentProperties?.AgentID;
        if (string.IsNullOrWhiteSpace(agentId)) return string.Empty;

        var sb = new StringBuilder();
        foreach (var c in ctx.stepConstraints)
        {
            var events = RefereeManager.Instance.PollEvents(agentId, c.constraintId);
            foreach (var evt in events)
            {
                sb.AppendLine($"  [{evt.constraintId}] {evt.eventType}: {evt.payload}");
                RefereeManager.Instance.AcknowledgeEvent(agentId, evt.eventId);
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 步骤完成后处理白板信号：
    ///   C2：写 DoneSignal（触发完成同步等待）
    ///   C3 sign=-1：清除 IntentAnnounce（释放动态互斥锁，让等待的 agent 可以进入）
    /// </summary>
    private void WriteWhiteboardDoneSignals()
    {
        if (SharedWhiteboard.Instance == null || ctx.stepConstraints == null) return;
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId)) return;
        string agentId = agentProperties?.AgentID ?? string.Empty;

        foreach (var c in ctx.stepConstraints)
        {
            if (c.cType == "C2" || c.cType == "Completion")
            {
                // C2：写 DoneSignal
                SharedWhiteboard.Instance.WriteEntry(groupId, new WhiteboardEntry
                {
                    agentId      = agentId,
                    constraintId = c.constraintId,
                    entryType    = WhiteboardEntryType.DoneSignal,
                    status       = 1,
                    progress     = "步骤完成",
                });
                Debug.Log($"[ADM] {agentId} 写入 DoneSignal: constraintId={c.constraintId}");
            }
            else if ((c.cType == "C3" || c.cType == "Coupling") && c.sign == -1)
            {
                // C3 sign=-1：释放互斥锁（清除 IntentAnnounce，让等待方得以进入）
                SharedWhiteboard.Instance.ClearEntry(groupId, agentId, c.constraintId);
                Debug.Log($"[ADM] {agentId} 释放互斥锁: constraintId={c.constraintId}");
            }
        }
    }

    /// <summary>为 channel=whiteboard 约束写入 IntentAnnounce（执行批次动作前调用）。</summary>
    private void WriteWhiteboardIntentSignals()
    {
        if (SharedWhiteboard.Instance == null || ctx.stepConstraints == null) return;
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId)) return;
        string agentId = agentProperties?.AgentID ?? string.Empty;

        foreach (var c in ctx.stepConstraints)
        {
            if (c.channel != "whiteboard") continue;
            SharedWhiteboard.Instance.WriteEntry(groupId, new WhiteboardEntry
            {
                agentId      = agentId,
                constraintId = c.constraintId,
                entryType    = WhiteboardEntryType.IntentAnnounce,
                status       = 0,
                progress     = $"准备执行第{ctx.iterationCount + 1}轮批次动作",
            });
        }
    }

    /// <summary>
    /// LLM 返回 nextActions 后，将 MoveTo 动作的 targetName 写入白板 IntentAnnounce.progress，
    /// 供同组其他 Agent 在下一轮 ReadWhiteboardContext 时读取，主动避开已占目标。
    /// 仅处理本步骤中 C3 sign=-1 的约束。
    /// </summary>
    private void WriteC3MutexClaims(AtomicAction[] actions)
    {
        if (SharedWhiteboard.Instance == null || ctx.stepConstraints == null || actions == null) return;
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId)) return;
        string myId = agentProperties?.AgentID ?? string.Empty;

        foreach (var c in ctx.stepConstraints)
        {
            if ((c.cType != "C3" && c.cType != "Coupling") || c.sign != -1) continue;
            foreach (var action in actions)
            {
                if (action.type != AtomicActionType.MoveTo) continue;
                if (string.IsNullOrEmpty(action.targetName)) continue;
                SharedWhiteboard.Instance.WriteEntry(groupId, new WhiteboardEntry
                {
                    agentId      = myId,
                    constraintId = c.constraintId,
                    entryType    = WhiteboardEntryType.IntentAnnounce,
                    progress     = action.targetName,
                    status       = 1,
                });
                Debug.Log($"[ADM] {myId} C3-1 占位: constraintId={c.constraintId}, target={action.targetName}");
            }
        }
    }

    /// <summary>
    /// 等待 C2 约束中所有相关 Agent 写入 DoneSignal（超时 30s 后继续）。
    /// syncWith 非空时等待列表中指定的 agentId；
    /// syncWith 为空时默认等待组内所有其他成员（对称同步语义）。
    /// </summary>
    private IEnumerator WaitForC2Sync()
    {
        if (SharedWhiteboard.Instance == null || ctx.stepConstraints == null) yield break;
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId)) yield break;

        const float syncTimeout = 30f;
        string selfId = agentProperties?.AgentID ?? string.Empty;

        foreach (var c in ctx.stepConstraints)
        {
            if (c.cType != "C2" && c.cType != "Completion") continue;

            // 确定等待目标：syncWith 非空则用指定列表，否则等组内所有其他成员
            string[] waitTargets;
            if (c.syncWith != null && c.syncWith.Length > 0)
            {
                waitTargets = c.syncWith;
            }
            else
            {
                // syncWith 为空（LLM#1 不知道 agentId）→ 等组内所有其他成员
                var groupMembers = GetGroupMemberIds();
                waitTargets = System.Array.FindAll(groupMembers,
                    id => !string.Equals(id, selfId, StringComparison.OrdinalIgnoreCase));
            }

            if (waitTargets.Length == 0) continue;

            float startTime = Time.time;
            Debug.Log($"[ADM] {selfId} 等待 C2 同步完成: constraintId={c.constraintId}," +
                      $" 等待=[{string.Join(",", waitTargets)}]");

            bool allDone = false;
            while (!allDone && Time.time - startTime < syncTimeout)
            {
                allDone = true;
                foreach (var syncAgent in waitTargets)
                {
                    if (!SharedWhiteboard.Instance.HasSignal(
                            groupId, c.constraintId, syncAgent,
                            WhiteboardEntryType.DoneSignal, syncTimeout))
                    {
                        allDone = false;
                        break;
                    }
                }
                if (!allDone) yield return new WaitForSeconds(0.5f);
            }

            if (!allDone)
                Debug.LogWarning($"[ADM] {selfId} C2 同步超时 (constraintId={c.constraintId})，继续执行");
            else
                Debug.Log($"[ADM] {selfId} C2 同步完成 (constraintId={c.constraintId})");
        }
    }

    /// <summary>获取本 Agent 所在组的所有成员 ID（用于 C2 默认对称等待）。</summary>
    private string[] GetGroupMemberIds()
    {
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId) || CommunicationManager.Instance == null)
            return Array.Empty<string>();

        // 遍历所有 agent，找到同组成员
        string[] allIds = CommunicationManager.Instance.GetAllAgentIds();
        var result = new List<string>();
        foreach (var id in allIds)
        {
            CommunicationModule mod = CommunicationManager.Instance.GetAgentModule(id);
            if (mod == null) continue;
            IntelligentAgent agent = mod.GetComponent<IntelligentAgent>();
            if (agent?.Properties == null) continue;
            // TeamID 对应组序号（在 OnGroupBootstrap 中由 props.TeamID = groupIndex 设置）
            string agentGroup = $"g{agent.Properties.TeamID}";
            if (agentGroup == groupId)
                result.Add(id);
        }
        return result.ToArray();
    }

    /// <summary>将本批次已执行的动作追加到 executedActionsSummary，供下一轮 LLM 判断进度。</summary>
    private void UpdateHistory(AtomicAction[] actions)
    {
        if (actions == null || ctx.executedActionsSummary == null) return;
        foreach (var a in actions)
        {
            string target = string.IsNullOrWhiteSpace(a.targetName) ? "无目标" : a.targetName;
            string line = $"[迭代{ctx.iterationCount + 1}] {a.type}({target})";
            if (!string.IsNullOrWhiteSpace(a.actionParams))
                line += $" - {a.actionParams}";
            ctx.executedActionsSummary.Add(line);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 感知快照
    // ─────────────────────────────────────────────────────────────

    private string BuildPerceptionSnapshot()
    {
        if (agentState == null) return string.Empty;

        Vector3 myPos = agentState.Position;
        const float maxDist = 50f;

        var sb = new StringBuilder();
        bool hasData = false;

        var nodes = agentState.DetectedSmallNodes;
        if (nodes != null && nodes.Count > 0)
        {
            List<string> nearbyNodeLines = new List<string>();
            foreach (var node in nodes)
            {
                if (Vector3.Distance(myPos, node.WorldPosition) > maxDist) continue;

                string dir = GetCompassDir8(myPos, node.WorldPosition);
                int dist = Mathf.RoundToInt(Vector3.Distance(myPos, node.WorldPosition) / 5f) * 5;
                string label = NodeTypeDisplayName(node.NodeType);
                string extra = node.IsDynamic ? "动态" : "静态";
                if (node.NodeType == SmallNodeType.TemporaryObstacle)
                    extra += "，需要规避";
                nearbyNodeLines.Add($"  - {label}({dir}约{dist}m，{extra})");
            }

            if (nearbyNodeLines.Count > 0)
            {
                sb.AppendLine("附近障碍（已感知）：");
                foreach (string line in nearbyNodeLines)
                    sb.AppendLine(line);
                hasData = true;
            }
        }

        var nearbyAgents = agentState.NearbyAgents;
        if (nearbyAgents != null && nearbyAgents.Count > 0)
        {
            sb.AppendLine("附近队友（本地感知）：");
            foreach (var kv in nearbyAgents)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                sb.AppendLine($"  - {kv.Key}");
                hasData = true;
            }
        }

        return hasData ? sb.ToString().TrimEnd() : string.Empty;
    }

    private static string GetCompassDir8(Vector3 from, Vector3 to)
    {
        float angle = Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        string[] dirs = { "正北", "东北", "正东", "东南", "正南", "西南", "正西", "西北" };
        return dirs[Mathf.RoundToInt(angle / 45f) % 8];
    }

    private static string NodeTypeDisplayName(SmallNodeType t) => t switch
    {
        SmallNodeType.Tree => "树木",
        SmallNodeType.Pedestrian => "行人",
        SmallNodeType.Vehicle => "车辆",
        SmallNodeType.ResourcePoint => "资源点",
        SmallNodeType.TemporaryObstacle => "临时障碍",
        SmallNodeType.Agent => "智能体",
        _ => "未知障碍"
    };

    // ─────────────────────────────────────────────────────────────
    // 工具方法
    // ─────────────────────────────────────────────────────────────

    private void SetStatus(ADMStatus s)
    {
        status = s;
        if (ctx != null) ctx.status = s;
        Debug.Log($"[ADM] {agentProperties?.AgentID} → {s}");
    }

    /// <summary>
    /// 统一消费待处理感知事件，并在 Running 状态下触发一次 LLM-B 重规划。
    /// </summary>
    private void HandlePendingPerceptionEvents()
    {
        //TODO
    }

    private string ResolveCurrentLocationName()
    {
        if (agentState == null || campusGrid == null) return "未知位置";
        Vector3 pos = agentState.Position;
        if (campusGrid.TryGetCellFeatureInfoByWorld(pos, out _, out _, out string name, out _, out _)
            && !string.IsNullOrWhiteSpace(name))
            return name;
        return $"({pos.x:F0},{pos.z:F0})";
    }

    private void SyncCampusGridReference()
    {
        if (campusGrid == null) campusGrid = FindObjectOfType<CampusGrid2D>();
        IntelligentAgent agent = GetComponent<IntelligentAgent>();
        if (agentState != null && agent != null)
            agentState.CampusGrid = agent.CampusGrid2D ?? campusGrid;
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        Match m = JsonBlockRe.Match(raw);
        if (m.Success) return m.Groups[1].Value.Trim();
        int start = raw.IndexOf('{');
        int startArr = raw.IndexOf('[');
        if (startArr >= 0 && (start < 0 || startArr < start)) start = startArr;
        if (start >= 0)
        {
            char open = raw[start];
            char close = open == '[' ? ']' : '}';
            int end = raw.LastIndexOf(close);
            if (end > start) return raw.Substring(start, end - start + 1);
        }
        return raw.Trim();
    }

    // ─────────────────────────────────────────────────────────────
    // 仪表板查询接口
    // ─────────────────────────────────────────────────────────────

    public string[] GetRecentEvents() => ctx?.recentEvents ?? Array.Empty<string>();
}
