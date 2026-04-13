// LLM_Modules/ActionDecisionModule.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>
    /// 单步骤最大 LLM 调用轮次。每轮生成 1-3 个原子动作，共可产出 20-60 个动作。
    /// 超限说明步骤目标存在根本性问题（导航死路/目标不可达），设为 Failed 而非强制 Done。
    /// </summary>
    private const int MaxIterations = 30;

    // ─── nextActions 空重试计数 ───────────────────────────────────
    /// <summary>
    /// LLM 返回 isDone=false 但 nextActions 为空时的连续重试次数。
    /// 最多重试 2 次后设为 Failed，防止无限循环。每次步骤开始时归零。
    /// </summary>
    private int _emptyActionRetries = 0;
    private const int MaxEmptyActionRetries = 2;

    // ─── JSON 提取正则 ────────────────────────────────────────────
    private static readonly Regex JsonBlockRe = new Regex(@"```(?:json)?\s*([\s\S]*?)```");

    // ─── 辩论参与模块 ─────────────────────────────────────────────
    /// <summary>
    /// 个体辩论参与模块，封装全部 MAD 个体侧逻辑。
    /// 在 Start() 中初始化，Rolling Loop Step 11.5 处通过此实例触发辩论参与协程。
    /// 群组级协调（广播角色、收敛判断、仲裁）由 IncidentCoordinator 负责，与本模块无关。
    /// </summary>
    private DebateParticipant _debateParticipant;

    // ─── 记忆与反思模块 ───────────────────────────────────────────
    /// <summary>
    /// 记忆模块（同 GameObject），负责存储动作执行记录并为 LLM 提供历史上下文。
    /// BuildRollingPrompt 调用 BuildActionContext() 注入记忆块；
    /// UpdateHistory 调用 RememberActionExecution() 记录每批次动作结果。
    /// </summary>
    private MemoryModule _memoryModule;

    /// <summary>
    /// 反思模块（同 GameObject），在动作批次完成或失败后接收通知。
    /// 连续失败时触发 L1 反思，感知到重要事件时触发 ImportantObservation 反思。
    /// </summary>
    private ReflectionModule _reflectionModule;

    /// <summary>
    /// 当前 agent 的人格系统引用，在 Start() 中通过 GetComponent 获取。
    /// 用于在 BuildRollingPrompt 中注入"行动风格"段，
    /// 让 LLM 在每次 rolling 决策时感知自身的个性特征（谨慎/系统/协作等）。
    /// 为 null 时跳过注入，不影响正常决策流程。
    /// </summary>
    private PersonalitySystem _personalitySystem;

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

        // 初始化记忆与反思模块（同 GameObject，失败时降级为无记忆模式）
        _memoryModule = GetComponent<MemoryModule>();
        _reflectionModule = GetComponent<ReflectionModule>();

        // 获取人格系统（挂在同一 agent GameObject 上，Inspector 中配置人格档案）
        _personalitySystem = GetComponent<PersonalitySystem>();

        // 初始化个体辩论参与模块，注入所需依赖和回调
        _debateParticipant = new DebateParticipant(
            owner:               this,
            agentProps:          agentProperties,
            comm:                commModule,
            llm:                 llmInterface,
            isAdmRunning:        () => status == ADMStatus.Running,
            onCriticalInterrupt: () => SetStatus(ADMStatus.BatchDone),
            onConsensusReceived: consensus =>
            {
                if (consensus.missionScopeChanged && planningModule != null)
                {
                    Debug.Log($"[ADM] {agentProperties?.AgentID} 任务范围已变更，触发重规划");
                    planningModule.RequestReplan($"DebateResolved: {consensus.resolution}");
                }
            });
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
            slotId                  = planningModule?.GetCurrentSlotId() ?? string.Empty,
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
        _emptyActionRetries   = 0;
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

    /// <summary>仪表板查询当前执行上下文快照（只读）。</summary>
    public ActionExecutionContext GetCtxSnapshot() => ctx;

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

    // ─────────────────────────────────────────────────────────────
    // 辩论协议公共接口（由 CommunicationModule 转发调用）
    // 具体逻辑已迁移至 DebateParticipant，此处仅做委托转发。
    // ─────────────────────────────────────────────────────────────

    /// <summary>收到 IncidentAnnounce 消息，转发给辩论参与模块处理。</summary>
    public void OnIncidentAnnounced(IncidentReport report)  => _debateParticipant?.OnIncidentAnnounced(report);

    /// <summary>收到 DebateProposal 角色分配消息，转发给辩论参与模块入队。</summary>
    public void AssignDebateRole(DebateRoleAssignment assignment) => _debateParticipant?.AssignDebateRole(assignment);

    /// <summary>收到 DebateResolved 共识消息，转发给辩论参与模块，由其触发重规划回调。</summary>
    public void OnDebateResolved(DebateConsensusEntry consensus)  => _debateParticipant?.OnDebateResolved(consensus);

    /// <summary>Critical 事件快速路径，转发给辩论参与模块，由其通过回调中断当前 batch。</summary>
    public void OnCriticalIncident(IncidentReport report)        => _debateParticipant?.OnCriticalIncident(report);

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
            // ── 0. 获取 C3-1 互斥锁（串行化：读白板 + LLM + 写占位三阶段） ──
            var mutexCids = GetC3MutexConstraintIds();
            if (mutexCids.Count > 0)
                yield return StartCoroutine(AcquireC3MutexLocks(mutexCids));

            // ── 1. 读取白板上下文 & 检查 C3 等待条件 ─────────────────
            var (waitAction, whiteboardCtx, occupiedNodes) = CheckWhiteboardAndGetContext();
            if (waitAction != null)
            {
                ReleaseC3MutexLocks(mutexCids);
                ctx.actionQueue = new[] { waitAction };
                ctx.currentActionIdx = 0;
                SetStatus(ADMStatus.Running);
                yield return new WaitUntil(() => status == ADMStatus.BatchDone || status == ADMStatus.Failed);
                if (status == ADMStatus.Failed) yield break;
                // Wait 完成后重新进入本轮循环检查（不增加迭代计数）
                SetStatus(ADMStatus.Interpreting);
                continue;
            }

            // ── 2.5 刷新当前位置名称 ──────────────────────────────────
            ctx.currentLocationName = ResolveCurrentLocationName();

            // ── 3. 获取地图信息 ─────────────────────────────────────
            Vector3? stepTargetWorldPos = null;
            if (campusGrid != null && !string.IsNullOrWhiteSpace(step.targetName))
            {
                if (campusGrid.TryResolveFeatureSpatialProfile(
                        step.targetName,
                        agentState?.Position ?? Vector3.zero,
                        out FeatureSpatialProfile targetProfile))
                {
                    stepTargetWorldPos = targetProfile.centroidWorld;
                }
                else
                {
                    Debug.LogWarning($"[ADM] 无法解析步骤目标 '{step.targetName}'，地图将不标注目标趋向");
                }
            }

            string relativeMap = campusGrid != null
                ? MapTopologySerializer.GetAgentRelativeMap(campusGrid, agentState.Position, stepTargetWorldPos)
                : "(地图不可用)";

            // ── 5. 构建滚动规划提示词 ────────────────────────────────
            // maxWaypoints=1：每轮只给下一跳，避免 LLM 提前规划多步产生锚定
            List<string> suggestedWaypoints = ComputeTopoWaypointChain(step, maxWaypoints: 1);
            string prompt = BuildRollingPrompt(step, whiteboardCtx, relativeMap, suggestedWaypoints, occupiedNodes, stepTargetWorldPos);

            // ── 6. 调用 LLM（锁持有中）──────────────────────────────
            string llmResult = null;
            yield return StartCoroutine(llmInterface.SendRequest(
                new LLMRequestOptions
                {
                    prompt         = prompt,
                    maxTokens      = 900,
                    enableJsonMode = true,
                    callTag        = $"ADM_Roll_iter{ctx.iterationCount + 1}",
                    agentId        = agentProperties?.AgentID
                },
                r => llmResult = r));

            if (string.IsNullOrWhiteSpace(llmResult))
            {
                Debug.LogError($"[ADM] {agentProperties?.AgentID} 滚动规划第{ctx.iterationCount + 1}轮 LLM 返回空");
                ReleaseC3MutexLocks(mutexCids);
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
                ReleaseC3MutexLocks(mutexCids);
                SetStatus(ADMStatus.Failed);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }

            // 输出思维链（可追溯性）
            if (!string.IsNullOrWhiteSpace(planResult?.thought))
                Debug.Log($"[ADM] {agentProperties?.AgentID} [Thought] {planResult.thought}");

            if (planResult == null)
            {
                Debug.LogError($"[ADM] 滚动规划解析结果为 null");
                ReleaseC3MutexLocks(mutexCids);
                SetStatus(ADMStatus.Failed);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }

            // ── 8. 处理 isDone = true ────────────────────────────────
            if (planResult.isDone)
            {
                Debug.Log($"[ADM] {agentProperties?.AgentID} 步骤完成: {planResult.doneReason}");
                ReleaseC3MutexLocks(mutexCids);

                // 写 DoneSignal 到白板（C2 约束）
                WriteWhiteboardDoneSignals();

                // 等待 C2 同步（若有 syncWith）
                yield return StartCoroutine(WaitForC2Sync());

                // 步骤完成：记录进展记忆（供下一任务规划阶段参考）
                _memoryModule?.RememberProgress(
                    missionId: ctx.msnId,
                    slotId: ctx.stepId,
                    stepLabel: step.text,
                    summary: $"步骤完成: {step.text}。{planResult.doneReason}",
                    targetRef: step.targetName);

                // 重置连续失败计数（步骤成功视为清算点）
                _reflectionModule?.NotifyActionOutcome(
                    missionId: ctx.msnId,
                    missionText: ctx.stepText,
                    slotId: ctx.slotId,
                    stepText: step.text,
                    targetRef: step.targetName,
                    success: true,
                    summary: planResult.doneReason);

                planningModule?.CompleteCurrentStep();
                SetStatus(ADMStatus.Done);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }

            // ── 9. nextActions 为空的保护 ──────────────────────────
            // LLM 返回 isDone=false 但没有给出动作，说明 LLM 当前轮次判断混乱。
            // 最多重试 MaxEmptyActionRetries 次（重新走完整 LLM 流程）；
            // 超限后设 Failed，由 PlanningModule 决定是否重规划，而非错误地标记步骤完成。
            if (planResult.nextActions == null || planResult.nextActions.Length == 0)
            {
                ReleaseC3MutexLocks(mutexCids);
                _emptyActionRetries++;
                if (_emptyActionRetries <= MaxEmptyActionRetries)
                {
                    Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM 返回 isDone=false 但 nextActions 为空，" +
                                     $"重试 ({_emptyActionRetries}/{MaxEmptyActionRetries})");
                    // 不增加 iterationCount，直接重试本轮 LLM 调用
                    continue;
                }
                Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM 连续 {MaxEmptyActionRetries} 次返回空动作，步骤设为 Failed");
                SetStatus(ADMStatus.Failed);
                if (agentState != null) agentState.Status = AgentStatus.Idle;
                yield break;
            }
            // 成功生成动作，重置重试计数
            _emptyActionRetries = 0;

            // ── 10. 执行当前批次动作 ─────────────────────────────────
            ctx.actionQueue = planResult.nextActions;
            ctx.currentActionIdx = 0;

            // 写 C3 sign=-1 互斥占位（将 MoveTo 目标写入白板，供其他 Agent 避开）
            WriteC3MutexClaims(planResult.nextActions);

            // 写完占位后立即释放锁，后来者进入时已能看到占位；动作执行不在锁内
            ReleaseC3MutexLocks(mutexCids);

            // 写 IntentAnnounce 到白板（可选，通知队友意图）
            WriteWhiteboardIntentSignals();

            SetStatus(ADMStatus.Running);
            yield return new WaitUntil(() => status == ADMStatus.BatchDone || status == ADMStatus.Failed);

            if (status == ADMStatus.Failed) yield break;

            // ── 11. 更新执行历史，进入下一迭代 ──────────────────────
            UpdateHistory(planResult.nextActions);

            // ── 11.5 辩论参与窗口 ──────────────────────────────────
            // 非 Critical 情况：action batch 自然结束后才参与，不打断当前 action
            // 每次最多 1 个 LLM 调用，不延误超过 1 次 rolling 迭代
            if (_debateParticipant != null && _debateParticipant.HasPendingDebateRole())
                yield return StartCoroutine(_debateParticipant.ParticipateInActiveDebates());

            ctx.iterationCount++;
            SetStatus(ADMStatus.Interpreting);
        }

        // 超出最大迭代次数：步骤目标可能不可达，设为 Failed 让 PlanningModule 处理重规划
        // 不强制 Done，避免掩盖真实的未完成状态
        Debug.LogError($"[ADM] {agentProperties?.AgentID} 滚动规划超出最大迭代次数 {MaxIterations}，步骤设为 Failed（目标可能不可达）");
        SetStatus(ADMStatus.Failed);
        if (agentState != null) agentState.Status = AgentStatus.Idle;
    }

    // ─────────────────────────────────────────────────────────────
    // 滚动规划提示词构建
    // ─────────────────────────────────────────────────────────────

    private string BuildRollingPrompt(PlanStep step, string whiteboardCtx, string relativeMap,
                                       List<string> suggestedWaypoints = null,
                                       List<string> occupiedNodes = null,
                                       Vector3? stepTargetWorldPos = null)
    {
        string historyBlock = ctx.executedActionsSummary != null && ctx.executedActionsSummary.Count > 0
            ? string.Join("\n", ctx.executedActionsSummary.ConvertAll(s => "  • " + s))
            : "  （本步骤尚无已执行动作）";

        string constraintBlock = BuildConstraintSummary();

        // 目标方向感知：让 LLM 知道目标在哪个方向、多远，而非只知道"未到达"
        bool isAtTarget = IsNearTarget(step.targetName, ctx.currentLocationName);
        string targetSpatialHint = string.Empty;
        if (!string.IsNullOrWhiteSpace(step.targetName) && agentState != null)
        {
            if (isAtTarget)
            {
                targetSpatialHint = "（已在目标附近）";
            }
            else if (stepTargetWorldPos.HasValue)
            {
                string dir = GetCompassDir8(agentState.Position, stepTargetWorldPos.Value);
                int distM = Mathf.RoundToInt(Vector3.Distance(agentState.Position, stepTargetWorldPos.Value));
                targetSpatialHint = $"（目标方向：{dir}，直线距离约 {distM}m）";
            }
        }

        // thought 模板：带标签的结构化推理，强制 LLM 逐步思考
        // 末尾两个标签补充主观维度：【置信】记录当前决策的把握程度，【建议】提炼可复用的任务处理规律
        string thoughtGuide = ctx.iterationCount >= 3
            ? "【情境】我在哪/要去哪/走了几步 → 【轨迹分析】历史中是否出现重复节点、整体是否在朝目标方向推进 → 【完成判断】三项核对结果 → 【选择理由】下一节点是否为历史外的新路径、方向是否朝目标 → 【置信】对此决策的把握（高/中/低）及原因 → 【建议】若有值得记录的规律输出\"当[场景]时应[策略]\"，否则留空"
            : "【完成判断】三项核对结果 → 【下一步】选择意图及依据 → 【置信】对此决策的把握（高/中/低）及原因 → 【建议】若有值得记录的规律输出\"当[场景]时应[策略]\"，否则留空";

        // 从 MemoryModule 检索当前步骤相关的历史经验和反思洞察
        // 失败时降级为空字符串（不影响决策流程，只是少了历史参考）
        string memoryContext = string.Empty;
        if (_memoryModule != null)
        {
            memoryContext = _memoryModule.BuildActionContext(new ActionMemoryContextRequest
            {
                missionText = ctx.stepText,
                missionId = ctx.msnId,
                slotId = ctx.slotId,
                stepText = step.text,
                targetRef = step.targetName,
                stepIntentSummary = step.text,
                maxMemories = 3,
                maxInsights = 2
            });
        }

        // 构建行动风格段（人格系统注入）：
        //   GetDecisionStyleHint 根据大五维度生成风格描述（如"谨慎行事，优先规避风险"）。
        //   非空时作为独立段落插入 prompt，位于"步骤目标"之后、"历史经验"之前，
        //   让 LLM 在阅读步骤目标后立即感知自身风格，再结合历史经验做出决策。
        //   期望效果：高神经质 agent 在面对不确定情况时倾向先 Observe 而不是直接 MoveTo。
        string styleHint = _personalitySystem?.GetDecisionStyleHint() ?? string.Empty;
        string styleSection = string.IsNullOrWhiteSpace(styleHint)
            ? string.Empty
            : $"## 行动风格\n{styleHint}\n\n";

        return
            "你是无人机战术执行规划器。每轮决策前，先理解自己的情境（我在哪、要去哪、走了多远、方向对不对），" +
            "再基于历史行为评估当前路径是否有效，最后给出有明确依据的下一步动作。\n\n" +

            "## 步骤目标\n" +
            $"步骤：{step.text}\n" +
            $"导航目标：{(string.IsNullOrWhiteSpace(step.targetName) ? "无" : step.targetName)}\n" +
            $"完成条件：{(string.IsNullOrWhiteSpace(step.doneCond) ? "未指定" : step.doneCond)}\n\n" +

            styleSection +

            "## 历史经验与反思规则\n" +
            (string.IsNullOrWhiteSpace(memoryContext) ? "（首次执行，暂无历史经验）" : memoryContext) + "\n\n" +

            "## 已执行历史\n" +
            historyBlock + "\n\n" +

            BuildSelfDiagnosisBlock(isAtTarget) +

            "## 当前状态\n" +
            $"角色：{ctx.role} | 当前位置：{ctx.currentLocationName}\n" +
            $"导航目标：{(string.IsNullOrWhiteSpace(step.targetName) ? "无" : step.targetName)}{targetSpatialHint}\n" +
            $"当前位置是否已到达目标：{(string.IsNullOrWhiteSpace(step.targetName) || IsNearTarget(step.targetName, ctx.currentLocationName) ? "是" : "否（仍需移动）")}\n\n" +

            "## 周边地图（半径300m）\n" +
            relativeMap + "\n\n" +

            BuildTopoWaypointBlock(suggestedWaypoints, occupiedNodes) +

            "## 导航规则\n" +
            "地图仅覆盖当前位置300m内地物。\n" +
            "• 目标在范围内（标注\"在本地图范围内\"）：targetName 直接填目标名。\n" +
            "• 目标超出范围（标注\"超出范围\"）：targetName 只能填地图内已列出的中间节点，每轮只规划到下一个节点。\n" +
            "• 选择中间节点时，请按以下优先级推理：\n" +
            "  ① A* 建议节点可用且方向朝目标 → 首选\n" +
            "  ② 历史中未出现过、方向朝目标的节点 → 次选\n" +
            "  ③ 若以上都无，选方向最接近目标的节点，并在 thought 中说明原因\n" +
            "  每次选择必须有方向依据，不要随机选。\n" +
            "• targetName 禁止编造或使用范围外地名。\n" +
            "• 当\"已到达目标：是\"时，不要再 MoveTo 同一目标，应立即执行下一步动作（Observe/巡逻/Signal 等）。\n\n" +

            "## 白板状态（组内协同）\n" +
            (string.IsNullOrWhiteSpace(whiteboardCtx) ? "（无白板数据）" : whiteboardCtx) + "\n\n" +

            "## 协同约束\n" +
            (string.IsNullOrWhiteSpace(constraintBlock) ? "无约束（本步骤独立执行）" : constraintBlock) + "\n\n" +

            "## 可用动作\n" +
            "• MoveTo：前往地图内静态地点，targetName=地点名，spatialHint=路径偏好，actionParams=飞行参数\n" +
            "• Wait：原地悬停等待，duration=等待秒数，actionParams=等待条件说明\n" +
            "• Observe：定点激活传感器感知环境，duration=观测时长（秒），actionParams=观察说明\n" +
            "• Track：跟踪动态移动实体，targetAgentId=目标智能体ID，duration=跟踪时长，actionParams=相对方向（前/后/左/右）\n" +
            "• Signal：向队友广播结构化信息，targetAgentId=接收方ID（\"all\"=全体），actionParams=消息内容\n" +
            "• Get：在当前位置获取物资或触发交互，targetName=目标名称，duration=交互等待时长（秒）\n" +
            "• Put：在当前位置放下物资或完成交付，targetName=交付对象名称，duration=交互等待时长（秒）\n" +
            "• Land：降落至地面，targetName=降落区域（可选）\n" +
            "• Takeoff：从地面起飞至悬停高度\n\n" +

            "## 步骤完成判断（重要）\n" +
            "在输出前，请依次核对以下三项，全部满足才可 isDone=true：\n" +
            "① 若步骤有 targetName，当前位置必须已到达该目标（见\"当前位置是否已到达目标\"）。\n" +
            "② 若步骤有 doneCond（完成条件），执行历史中必须有满足该条件的动作记录。\n" +
            "③ 若存在 C3 约束（信号等待/互斥），必须已满足信号前置条件（白板中已出现对应 ReadySignal 或 IntentAnnounce）。\n" +
            "   【C2 约束不在此判断】C2 同步（多机完成同步）由系统在 isDone=true 后自动写入并等待，你只需判断本机的步骤目标是否达成，不要因为等 C2 同步而推迟 isDone=true。\n" +
            "任意一项不满足，isDone 必须为 false，并继续规划下一步动作。\n\n" +

            "## 输出（JSON 对象，非数组）\n" +
            "{\n" +
            $"  \"thought\": \"{thoughtGuide}\",\n" +
            "  \"isDone\": true/false,\n" +
            "  \"doneReason\": \"完成或未完成的具体原因（引用当前位置/历史/条件对比）\",\n" +
            "  \"nextActions\": [\n" +
            "    {\"actionId\":\"aa_1\",\"type\":\"MoveTo\",\"targetName\":\"地点名\",\"targetAgentId\":\"\",\"duration\":0,\"actionParams\":\"\",\"spatialHint\":\"\"}\n" +
            "  ]\n" +
            "}\n" +
            "1. thought 必填，按上方标签格式逐步推理（不影响执行逻辑）。\n" +
            "2. isDone=true 时 nextActions 填 []；isDone=false 时必须提供 1-3 个动作。\n" +
            "3. 每个动作必须包含全部字段：actionId / type / targetName / targetAgentId / duration / actionParams / spatialHint。\n" +
            "4. 若存在 C3 约束，生成的动作必须遵守其信号/互斥要求（如等待 ReadySignal、避开已占节点）；C2 约束无需在动作层面等待。\n";
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
                    {
                        string selfId = agentProperties?.AgentID ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(c.watchAgent) &&
                            string.Equals(selfId, c.watchAgent, StringComparison.OrdinalIgnoreCase))
                            sb.AppendLine($"\n  耦合(C3+1 生产侧): 本步骤完成后由我写 ReadySignal,释放其他成员");
                        else
                            sb.AppendLine($"\n  耦合(C3+1 等待侧): 本步骤执行前需等待 watchAgent={c.watchAgent} 写 ReadySignal");
                    }
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
    // 导航自我诊断 & 到达判定
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 当迭代次数 >= 3 时，向 LLM 提供情境问题，引导它主动诊断循环并决策，
    /// 而不是通过硬性规则（tabu）约束选择。
    /// </summary>
    private string BuildSelfDiagnosisBlock(bool isAtTarget = false)
    {
        if (ctx == null || ctx.iterationCount < 3) return string.Empty;
        if (isAtTarget) return string.Empty;
        return "## 导航自我诊断（请在 thought 中完成）\n" +
               $"你已执行了 {ctx.iterationCount} 轮迭代，当前仍未到达目标。请在 thought 中主动分析：\n" +
               "- 回顾执行历史，移动轨迹是否出现了重复节点或循环模式？\n" +
               "- 当前位置整体上是否在向目标方向靠近，还是在原地打转？\n" +
               "- 下一步应往哪个方向移动？历史中有没有未尝试过的节点或路径？\n" +
               "请基于分析结果给出 nextActions，而非重复之前的选择。\n\n";
    }

    /// <summary>
    /// 模糊到达判定：currentLoc 等于或包含 targetName 时视为已到达。
    /// 解决 "近艺术中心" != "艺术中心" 导致永远判定未到达的问题。
    /// </summary>
    private static bool IsNearTarget(string targetName, string currentLoc)
    {
        if (string.IsNullOrWhiteSpace(targetName)) return true;
        if (targetName == currentLoc) return true;
        if (!string.IsNullOrWhiteSpace(currentLoc) && currentLoc.Contains(targetName)) return true;
        return false;
    }

    private List<string> _topoWaypointCache       = new List<string>();
    private Vector3      _topoWaypointCachePos     = Vector3.positiveInfinity;
    private string       _topoWaypointCacheTarget  = string.Empty;
    private const float  TopoWaypointCacheRange    = 20f;

    private List<string> ComputeTopoWaypointChain(PlanStep step, int maxWaypoints = 3)
    {
        if (campusGrid == null || string.IsNullOrWhiteSpace(step?.targetName))
            return _topoWaypointCache;

        Vector3 agentPos = agentState?.Position ?? Vector3.zero;
        if (step.targetName == _topoWaypointCacheTarget &&
            Vector3.Distance(agentPos, _topoWaypointCachePos) < TopoWaypointCacheRange &&
            _topoWaypointCache.Count > 0)
            return _topoWaypointCache;

        _topoWaypointCache.Clear();
        _topoWaypointCachePos   = agentPos;
        _topoWaypointCacheTarget = step.targetName;

        Vector2Int agentCell = campusGrid.WorldToGrid(agentPos);
        if (!campusGrid.TryGetFeatureApproachCells(step.targetName, agentPos,
                out Vector2Int[] approachArr, maxCount: 1)
            || approachArr.Length == 0)
            return _topoWaypointCache;
        Vector2Int goalCell = approachArr[0];

        List<Vector2Int> path = campusGrid.FindPathAStar(agentCell, goalCell);
        if (path == null || path.Count == 0) return _topoWaypointCache;

        string currentLoc = ctx?.currentLocationName ?? string.Empty;
        string lastAdded  = string.Empty;
        foreach (Vector2Int cell in path)
        {
            if (campusGrid.cellFeatureNameGrid == null) break;
            string feat = campusGrid.cellFeatureNameGrid[cell.x, cell.y];
            if (string.IsNullOrWhiteSpace(feat) || feat == currentLoc || feat == lastAdded) continue;
            _topoWaypointCache.Add(feat);
            lastAdded = feat;
            if (feat == step.targetName || _topoWaypointCache.Count >= maxWaypoints) break;
        }
        return _topoWaypointCache;
    }

    private string BuildTopoWaypointBlock(List<string> waypoints, List<string> occupiedNodes = null)
    {
        if (waypoints == null || waypoints.Count == 0) return string.Empty;

        // 标注每个节点的占用状态（供 LLM 自主判断是否采纳）
        var occupiedSet = occupiedNodes != null
            ? new HashSet<string>(occupiedNodes, StringComparer.OrdinalIgnoreCase)
            : null;

        var annotated = new System.Text.StringBuilder();
        foreach (string wp in waypoints)
        {
            bool isOccupied = occupiedSet != null && occupiedSet.Contains(wp);
            annotated.Append(isOccupied ? $"{wp}（⚠ 白板显示已被他人占用）" : $"{wp}（可用）");
        }

        return "## A* 下一跳参考（静态地图，仅供参考）\n" +
               $"  {annotated}\n" +
               "请在 thought 中评估此建议：该节点是否曾在历史中频繁出现？移动至此是否更接近目标方向？若不合理，请从地图中选取更优节点并说明理由。\n\n";
    }

    // ─────────────────────────────────────────────────────────────
    // 约束处理辅助方法
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 单次遍历本步骤约束，完成两件事：
    ///
    /// ── Phase 1: C3 sign=+1 等待拦截 ──────────────────────────────────────
    /// C3 sign=+1 是单向前置依赖：同一 constraintId 同时绑定到等待侧和生产侧。
    ///   - 等待侧（myId != watchAgent）：执行本步骤前必须等 watchAgent 写入 ReadySignal。
    ///   - 生产侧（myId == watchAgent）：本步骤完成后由 WriteWhiteboardDoneSignals 写 ReadySignal，无需此处拦截。
    /// 若等待条件未满足，提前返回 Wait AtomicAction（whiteboardCtx 为空，调用方跳过 LLM）。
    /// 提前返回可避免 Phase 2 白板查询的无效开销。
    ///
    /// ── Phase 2: 构建 JSON 白板上下文 ──────────────────────────────────────
    /// 遍历所有 channel=whiteboard 约束，按语义分三组序列化为紧凑 JSON：
    ///   "occupied" : C3 sign=-1 互斥约束中，其他 agent 的 IntentAnnounce.progress（已占目标名）。
    ///                LLM 应在本轮 nextActions 中避开这些目标（先到先得互斥）。
    ///                自身条目不收录（对 LLM 无用）。
    ///   "signals"  : DoneSignal（type="done"）和 ReadySignal（type="ready"）。
    ///                告知 LLM 哪些依赖步骤已完成或就绪。
    ///   "intents"  : 非 C3-1 约束下其他 agent 的 IntentAnnounce / StatusUpdate。
    ///                提供队友当前执行意图，供 LLM 协同决策参考。
    /// 空字段不输出；三组均空时返回 string.Empty（调用方显示"无白板数据"）。
    /// </summary>
    private (AtomicAction waitAction, string whiteboardCtx, List<string> occupiedNodes) CheckWhiteboardAndGetContext()
    {
        if (SharedWhiteboard.Instance == null || ctx.stepConstraints == null)
            return (null, string.Empty, null);
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId)) return (null, string.Empty, null);
        string myId = agentProperties?.AgentID ?? string.Empty;

        // ── Phase 1: C3 sign=+1 等待拦截 ──────────────────────────────────────────
        // 注：此处只处理等待侧逻辑；生产侧（myId == watchAgent）在步骤完成时由
        //     WriteWhiteboardDoneSignals() 写 ReadySignal，不在此处操作。
        foreach (var c in ctx.stepConstraints)
        {
            if (c.cType != "C3" && c.cType != "Coupling") continue;
            if (c.sign != 1) continue;
            if (string.IsNullOrWhiteSpace(c.watchAgent))
            {
                // watchAgent 在运行时回填阶段（BuildRuntimeConstraintsForAgent）应被填充；
                // 若为空说明规划数据异常，跳过本约束以免误拦截。
                Debug.LogWarning($"[ADM] {myId} C3+1 constraint {c.constraintId} missing runtime watchAgent, skip wait");
                continue;
            }
            if (string.Equals(myId, c.watchAgent, StringComparison.OrdinalIgnoreCase))
                continue; // 本机是生产侧，无需等待

            // 查询 watchAgent 是否已写入 ReadySignal
            bool watchReady = SharedWhiteboard.Instance.HasSignal(
                groupId, c.constraintId, c.watchAgent, WhiteboardEntryType.ReadySignal);
            if (!watchReady)
            {
                Debug.Log($"[ADM] {myId} C3+1 约束 {c.constraintId} 等待 {c.watchAgent} 就绪");
                // 返回 Wait 动作；调用方检测到 waitAction != null 后跳过 LLM，直接执行等待
                return (new AtomicAction
                {
                    actionId      = "aa_wait_c3_plus",
                    type          = AtomicActionType.Wait,
                    targetName    = string.Empty,
                    targetAgentId = c.watchAgent,
                    duration      = 5f,
                    actionParams  = $"等待 {c.watchAgent} 就绪（约束 {c.constraintId}）",
                    spatialHint   = string.Empty,
                }, string.Empty, null);
            }
        }

        // ── Phase 2: 构建 JSON 白板上下文 ──────────────────────────────────────────
        // 先收集所有 C3 sign=-1 约束 ID，用于后续分类（互斥 vs 普通意图）
        var mutexConstraintIds = new HashSet<string>();
        foreach (var c in ctx.stepConstraints)
            if ((c.cType == "C3" || c.cType == "Coupling") && c.sign == -1)
                mutexConstraintIds.Add(c.constraintId);

        var occupied = new List<string>();                      // 已被他人占用的目标名
        var signals  = new List<Dictionary<string, string>>();  // DoneSignal / ReadySignal
        var intents  = new List<Dictionary<string, string>>();  // 非互斥 IntentAnnounce / StatusUpdate

        foreach (var c in ctx.stepConstraints)
        {
            if (c.channel != "whiteboard") continue;
            // isMutex=true 时：该约束是 C3 sign=-1 互斥约束，IntentAnnounce 代表"已占目标"
            bool isMutex = mutexConstraintIds.Contains(c.constraintId);

            foreach (var e in SharedWhiteboard.Instance.QueryEntries(groupId, c.constraintId))
            {
                switch (e.entryType)
                {
                    case WhiteboardEntryType.DoneSignal:
                        // 步骤完成信号，由 WriteWhiteboardDoneSignals 写入（C2 约束）
                        signals.Add(new Dictionary<string, string>
                            { ["agent"] = e.agentId, ["cid"] = c.constraintId, ["type"] = "done" });
                        break;

                    case WhiteboardEntryType.ReadySignal:
                        // 就绪信号，由 WriteWhiteboardDoneSignals 写入（C3 sign=+1 生产侧）
                        signals.Add(new Dictionary<string, string>
                            { ["agent"] = e.agentId, ["cid"] = c.constraintId, ["type"] = "ready" });
                        break;

                    case WhiteboardEntryType.IntentAnnounce:
                        if (isMutex)
                        {
                            // C3 sign=-1：IntentAnnounce.progress = 该 agent 已锁定的目标名。
                            // 仅收录他人条目；自身条目对本机 LLM 决策无意义，丢弃。
                            if (e.agentId != myId && !string.IsNullOrEmpty(e.progress))
                                occupied.Add(e.progress);
                        }
                        else
                        {
                            // 非互斥约束：记录为普通意图宣告供 LLM 参考
                            var entry = new Dictionary<string, string>
                                { ["agent"] = e.agentId, ["cid"] = c.constraintId };
                            if (!string.IsNullOrEmpty(e.progress)) entry["progress"] = e.progress;
                            intents.Add(entry);
                        }
                        break;

                    case WhiteboardEntryType.StatusUpdate:
                        // 通用状态更新；progress 为空则无实质内容，跳过
                        if (!string.IsNullOrEmpty(e.progress))
                            intents.Add(new Dictionary<string, string>
                                { ["agent"] = e.agentId, ["cid"] = c.constraintId, ["progress"] = e.progress });
                        break;
                }
            }
        }

        // ── Phase 3: 读取裁判通知（跨队情报，组长写入，队员读取）─────────────────
        var refereeEntries = SharedWhiteboard.Instance.QueryEntries(
            groupId, GroupMonitor.RefereeConstraintId);

        var refereeList = new List<Dictionary<string, string>>();
        foreach (var e in refereeEntries)
        {
            if (e.entryType != WhiteboardEntryType.RefereeNotice) continue;
            var notice = JsonUtility.FromJson<RefereeNoticeData>(e.progress);
            if (notice == null) continue;
            refereeList.Add(new Dictionary<string, string>
            {
                ["from"]    = notice.fromGroupId,
                ["event"]   = notice.eventType,
                ["summary"] = notice.summary,
                ["at"]      = notice.receivedAt.ToString("F1"),
            });
        }

        if (occupied.Count == 0 && signals.Count == 0 && intents.Count == 0 && refereeList.Count == 0)
            return (null, string.Empty, null);

        // 按重要性排序字段（occupied 最关键，放最前）；空字段不序列化
        var result = new Dictionary<string, object>();
        if (occupied.Count > 0)    result["occupied"] = occupied;
        if (signals.Count > 0)    result["signals"]  = signals;
        if (intents.Count > 0)    result["intents"]  = intents;
        if (refereeList.Count > 0) result["referee"] = refereeList;

        // occupied 单独返回，供 BuildTopoWaypointBlock 交叉标注 A* 节点的占用状态
        return (null, JsonConvert.SerializeObject(result), occupied.Count > 0 ? occupied : null);
    }

/// <summary>Handle whiteboard writes after the current step completes.</summary>
    private void WriteWhiteboardDoneSignals()
    {
        if (SharedWhiteboard.Instance == null || ctx.stepConstraints == null) return;
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId)) return;
        string agentId = agentProperties?.AgentID ?? string.Empty;

        var currentStep = planningModule?.GetCurrentStep();
        string roleTag  = ctx.role.ToString();
        string doneCond = currentStep != null
            ? (string.IsNullOrWhiteSpace(currentStep.doneCond) ? currentStep.text : currentStep.doneCond)
            : ctx.stepText;

        foreach (var c in ctx.stepConstraints)
        {
            if (c.cType == "C2" || c.cType == "Completion")
            {
                // C2：写 DoneSignal，progress 格式 "[角色] 完成条件" 供 GroupMonitor 收集
                SharedWhiteboard.Instance.WriteEntry(groupId, new WhiteboardEntry
                {
                    agentId      = agentId,
                    constraintId = c.constraintId,
                    entryType    = WhiteboardEntryType.DoneSignal,
                    status       = 1,
                    progress     = $"[{roleTag}] {doneCond}",
                });
                Debug.Log($"[ADM] {agentId} 写入 DoneSignal: constraintId={c.constraintId}");
            }
            else if ((c.cType == "C3" || c.cType == "Coupling") &&
                     c.sign == 1 &&
                     !string.IsNullOrWhiteSpace(c.watchAgent) &&
                     string.Equals(agentId, c.watchAgent, StringComparison.OrdinalIgnoreCase))
            {
                // C3 sign=+1 producer side: write ReadySignal when this step completes.
                SharedWhiteboard.Instance.WriteEntry(groupId, new WhiteboardEntry
                {
                    agentId      = agentId,
                    constraintId = c.constraintId,
                    entryType    = WhiteboardEntryType.ReadySignal,
                    status       = 1,
                    progress     = "step_complete_ready",
                });
                Debug.Log($"[ADM] {agentId} 写入 ReadySignal: constraintId={c.constraintId}");
            }
            else if ((c.cType == "C3" || c.cType == "Coupling") && c.sign == -1)
            {
                // C3 sign=-1：释放互斥锁（清除 IntentAnnounce，让等待方得以进入）
                SharedWhiteboard.Instance.ClearEntry(groupId, agentId, c.constraintId);
                Debug.Log($"[ADM] {agentId} 释放互斥锁: constraintId={c.constraintId}");
            }
        }
    }

    /// <summary>为 channel=whiteboard 约束写入 IntentAnnounce（执行批次动作前调用）。
    /// 注意：C3 sign=-1 互斥约束由 WriteC3MutexClaims 专门处理（progress=targetName），此处跳过，避免覆盖。</summary>
    private void WriteWhiteboardIntentSignals()
    {
        if (SharedWhiteboard.Instance == null || ctx.stepConstraints == null) return;
        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId)) return;
        string agentId = agentProperties?.AgentID ?? string.Empty;

        foreach (var c in ctx.stepConstraints)
        {
            if (c.channel != "whiteboard") continue;
            // C3 sign=-1 互斥约束由 WriteC3MutexClaims 写 progress=targetName，跳过，防止覆盖
            if ((c.cType == "C3" || c.cType == "Coupling") && c.sign == -1) continue;
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
        string myId = agentProperties?.AgentID ?? "?";

        // ── 前置守卫诊断 ─────────────────────────────────────────────
        if (SharedWhiteboard.Instance == null)
        {
            Debug.LogWarning($"[ADM][WriteC3MutexClaims] {myId} 跳过：SharedWhiteboard.Instance == null");
            return;
        }
        if (ctx.stepConstraints == null)
        {
            Debug.LogWarning($"[ADM][WriteC3MutexClaims] {myId} 跳过：ctx.stepConstraints == null");
            return;
        }
        if (actions == null)
        {
            Debug.LogWarning($"[ADM][WriteC3MutexClaims] {myId} 跳过：actions == null");
            return;
        }

        string groupId = planningModule?.GetGroupId();
        if (string.IsNullOrWhiteSpace(groupId))
        {
            Debug.LogWarning($"[ADM][WriteC3MutexClaims] {myId} 跳过：groupId 为空（planningModule={planningModule != null})");
            return;
        }

        // ── 约束遍历诊断 ─────────────────────────────────────────────
        Debug.Log($"[ADM][WriteC3MutexClaims] {myId} 开始 | stepConstraints={ctx.stepConstraints.Length}" +
                  $" | actions={actions.Length} | groupId={groupId}");

        foreach (var c in ctx.stepConstraints)
        {
            bool isC3  = c.cType == "C3" || c.cType == "Coupling";
            bool isMux = c.sign == -1;
            Debug.Log($"[ADM][WriteC3MutexClaims] {myId} 约束 {c.constraintId}: cType={c.cType} isC3={isC3} sign={c.sign} isMux={isMux}");

            // 修复：原来只判断 "C3"，漏掉了 "Coupling"
            if (!isC3 || !isMux) continue;

            // ── 动作遍历诊断 ─────────────────────────────────────────
            foreach (var action in actions)
            {
                Debug.Log($"[ADM][WriteC3MutexClaims] {myId}   action: type={action.type} targetName='{action.targetName}'");

                if (action.type != AtomicActionType.MoveTo)
                {
                    Debug.Log($"[ADM][WriteC3MutexClaims] {myId}   跳过（非 MoveTo）");
                    continue;
                }
                if (string.IsNullOrEmpty(action.targetName))
                {
                    Debug.LogWarning($"[ADM][WriteC3MutexClaims] {myId}   跳过（targetName 为空）");
                    continue;
                }

                SharedWhiteboard.Instance.WriteEntry(groupId, new WhiteboardEntry
                {
                    agentId      = myId,
                    constraintId = c.constraintId,
                    entryType    = WhiteboardEntryType.IntentAnnounce,
                    progress     = action.targetName,
                    status       = 1,
                });
                Debug.Log($"[ADM][WriteC3MutexClaims] {myId} ✓ 写入占位: group={groupId} cid={c.constraintId} target={action.targetName}");
            }
        }
    }

    /// <summary>返回当前步骤中所有 C3 sign=-1 互斥约束的 constraintId 列表（已去重）。</summary>
    private List<string> GetC3MutexConstraintIds()
    {
        var ids = new List<string>();
        if (ctx.stepConstraints == null) return ids;
        foreach (var c in ctx.stepConstraints)
        {
            if ((c.cType == "C3" || c.cType == "Coupling") && c.sign == -1)
                ids.Add(c.constraintId);
        }
        return ids;
    }

    /// <summary>
    /// 协程：尝试获取本步骤所有 C3-1 互斥锁，全部获取后才返回。
    /// 若部分锁被占用则全部释放并 0.1s 后重试（all-or-nothing，避免死锁）。
    /// </summary>
    private IEnumerator AcquireC3MutexLocks(List<string> constraintIds)
    {
        if (constraintIds == null || constraintIds.Count == 0) yield break;
        string myId = agentProperties?.AgentID ?? string.Empty;

        float timeout = 60f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            bool allAcquired = true;
            foreach (var cid in constraintIds)
            {
                if (!SharedWhiteboard.Instance.TryAcquireMutexLock(cid, myId))
                {
                    allAcquired = false;
                    break;
                }
            }

            if (allAcquired)
            {
                Debug.Log($"[ADM] {myId} 已获取所有 C3-1 互斥锁");
                yield break;
            }

            // 部分失败：全部释放，等待后重试
            foreach (var cid in constraintIds)
                SharedWhiteboard.Instance.ReleaseMutexLock(cid, myId);

            yield return new WaitForSeconds(0.1f);
            elapsed += 0.1f;
        }

        Debug.LogError($"[ADM] {myId} 获取 C3-1 互斥锁超时！强制继续（可能冲突）");
    }

    /// <summary>释放本步骤所有 C3-1 互斥锁。</summary>
    private void ReleaseC3MutexLocks(List<string> constraintIds)
    {
        if (constraintIds == null) return;
        string myId = agentProperties?.AgentID ?? string.Empty;
        foreach (var cid in constraintIds)
            SharedWhiteboard.Instance.ReleaseMutexLock(cid, myId);
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
                waitTargets = Array.FindAll(
                    c.syncWith.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    id => !string.IsNullOrWhiteSpace(id) &&
                          !string.Equals(id, selfId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // syncWith 为空（LLM#1 不知道 agentId）→ 等组内所有其他成员
                Debug.LogWarning($"[ADM] {selfId} C2 constraint {c.constraintId} missing syncWith, fallback to group peers");
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

    /// <summary>
    /// 将本批次已执行的动作追加到 executedActionsSummary（供下一轮 LLM 判断进度）。
    /// 批次成功执行（status=BatchDone）时调用，不在 Failed 分支调用。
    /// 注：成功原子动作不写入长期记忆——当前格式仅为"执行成功"，不携带超出执行确认的有效信息，
    /// 会稀释检索结果中的失败/反思信号。进度追踪由 executedActionsSummary 承担；
    /// 任务级成功由 RememberMissionOutcome 记录；失败由 NotifyActionOutcome 路径记录。
    /// </summary>
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

        if (campusGrid.TryGetCellFeatureInfoByWorld(pos, out _, out string uid, out string name, out _, out _))
        {
            // 优先用 runtimeAlias（与地图显示一致）
            if (!string.IsNullOrWhiteSpace(uid)
                && campusGrid.featureSpatialProfileByUid != null
                && campusGrid.featureSpatialProfileByUid.TryGetValue(uid, out var profile)
                && !string.IsNullOrWhiteSpace(profile.runtimeAlias))
                return profile.runtimeAlias;

            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        // 当前格无特征（开阔地/地图边界外）：搜索半径5格内最近有名字的特征
        string nearest = campusGrid.TryGetNearestFeatureNameByWorld(pos, searchRadius: 5);
        return !string.IsNullOrWhiteSpace(nearest) ? $"近{nearest}" : $"({pos.x:F0},{pos.z:F0})";
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
