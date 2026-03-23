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
    private PlanningModule    planningModule;
    private LLMInterface      llmInterface;
    private AgentProperties   agentProperties;
    private AgentDynamicState agentState;
    private CommunicationModule commModule;
    private CampusGrid2D       campusGrid;

    // ─── ADM 状态 ─────────────────────────────────────────────────
    private ActionExecutionContext ctx;
    private ADMStatus              status = ADMStatus.Idle;

    // ─── 黑板（队友状态缓存）──────────────────────────────────────
    private readonly Dictionary<string, AgentContextUpdate> blackboard = new();

    // ─── 协商窗口 ─────────────────────────────────────────────────
    private float              negotiationWindowEnd;

    // ─── 感知事件队列 ─────────────────────────────────────────────
    private readonly Queue<(string desc, string location)> pendingPerceptionEvents = new();

    // ─── 协程句柄 ─────────────────────────────────────────────────
    private Coroutine activeCoroutine;

    // ─── 重规划计数（防死锁）────────────────────────────────────
    private int replanCount;
    private const int MaxReplanCount = 5;

    // ─── JSON 提取正则 ────────────────────────────────────────────
    private static readonly Regex JsonBlockRe = new Regex(@"```(?:json)?\s*([\s\S]*?)```");

    // ─────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────

    private void Start()
    {
        planningModule = GetComponent<PlanningModule>();
        llmInterface   = FindObjectOfType<LLMInterface>();
        commModule     = GetComponent<CommunicationModule>();
        campusGrid     = FindObjectOfType<CampusGrid2D>();

        IntelligentAgent agent = GetComponent<IntelligentAgent>();
        if (agent != null)
        {
            agentProperties = agent.Properties;
            agentState      = agent.CurrentState;
        }
    }

    private void Update()
    {
        if (status == ADMStatus.Running && pendingPerceptionEvents.Count > 0)
            HandlePendingPerceptionEvents();

        if (status == ADMStatus.Negotiating && Time.time >= negotiationWindowEnd)
            CheckConflictsAndMaybeReplan();
    }

    // ─────────────────────────────────────────────────────────────
    // 公共接口
    // ─────────────────────────────────────────────────────────────

    /// <summary>由 PlanningModule 在 Active 状态下调用，传入当前步骤。</summary>
    public void StartStep(PlanStep step)
    {
        if (step == null) return;
        if (activeCoroutine != null) { StopCoroutine(activeCoroutine); activeCoroutine = null; }

        // RunLLMA() 依赖的上下文都在这里一次性准备好：
        // - role / currentLocationName / coordinationConstraint 会直接进入 prompt
        // - originalGoalName 当前实现直接等于 step.text，不是单独抽取出来的纯地名
        //   因此后面拿它去构造任务子图时，可能出现“整句步骤文本无法定位节点”的情况。
        ctx = new ActionExecutionContext
        {
            msnId                    = planningModule?.currentMission?.missionId ?? string.Empty,
            stepId                   = step.stepId,
            stepText                 = step.text,
            coordinationConstraints  = step.constraints ?? Array.Empty<string>(),
            role                     = agentProperties != null ? agentProperties.Role : RoleType.Scout,
            actionQueue            = null,
            currentActionIdx       = 0,
            status                 = ADMStatus.Idle,
            currentLocationName    = ResolveCurrentLocationName(),
            //originalGoalName       = step.text,
            remainingWaypoints     = Array.Empty<string>(),
            recentEvents           = Array.Empty<string>(),
        };

        replanCount = 0;
        SetStatus(ADMStatus.Idle);
        activeCoroutine = StartCoroutine(RunLLMA(step));
    }

    /// <summary>由感知模块调用，触发可能的打断与重规划。</summary>
    public void OnPerceptionEvent(string eventDescription, string locationName)
    {
        if (string.IsNullOrWhiteSpace(eventDescription)) return;
        pendingPerceptionEvents.Enqueue((eventDescription, locationName ?? string.Empty));
    }

    /// <summary>由 CommunicationModule 在收到 BoardUpdate 消息时调用。</summary>
    public void OnBoardUpdate(AgentContextUpdate update)
    {
        if (update == null || string.IsNullOrWhiteSpace(update.agentId)) return;
        if (string.Equals(update.agentId, agentProperties?.AgentID, StringComparison.OrdinalIgnoreCase))
            return;   // 忽略自己广播的回声

        blackboard[update.agentId] = update;
        Debug.Log($"[ADM] {agentProperties?.AgentID} 收到黑板更新 from {update.agentId}");

        // 只在 Running 状态响应（Negotiating/Replanning 状态已在处理冲突，不重复触发）
        if (status != ADMStatus.Running || ctx?.actionQueue == null) return;

        // ③ 全量扫描（修复"只看 incoming"的缺陷）
        string myId       = agentProperties?.AgentID ?? string.Empty;
        var    ownTargets = BuildOwnTargetSet();
        bool   hasConflict = false;

        foreach (var kv in blackboard)
        {
            if (string.Equals(kv.Key, myId, StringComparison.OrdinalIgnoreCase)) continue;
            if (CountConflicts(kv.Value.plannedTargets, ownTargets) > 0)
            {
                hasConflict = true;
                break;
            }
        }

        if (!hasConflict) return;

        // ④ 打开协商窗口走选举，而非直接调 RunLLMB
        // BUG-H4: 进入 Negotiating 前停止旧协程，避免并发
        if (activeCoroutine != null) { StopCoroutine(activeCoroutine); activeCoroutine = null; }
        negotiationWindowEnd = Time.time + GetNegotiationWindow();
        SetStatus(ADMStatus.Negotiating);
        // Update() 检测到 Negotiating && 窗口到期 → 调 CheckConflictsAndMaybeReplan
    }

    /// <summary>执行层查询当前应执行的原子动作。</summary>
    public AtomicAction GetCurrentAction()
    {
        if (ctx?.actionQueue == null || ctx.currentActionIdx >= ctx.actionQueue.Length) return null;
        return ctx.actionQueue[ctx.currentActionIdx];
    }

    /// <summary>执行层通知当前动作已完成，ADM 推进队列。</summary>
    public void CompleteCurrentAction()
    {
        if (ctx?.actionQueue == null) return;
        ctx.currentActionIdx++;

        if (ctx.currentActionIdx >= ctx.actionQueue.Length)
        {
            SetStatus(ADMStatus.Done);
            planningModule?.CompleteCurrentStep();
            Debug.Log($"[ADM] {agentProperties?.AgentID} 所有动作完成，通知 PlanningModule");
            // BUG-04 修复：步骤完成后将智能体物理状态归 Idle，
            // 确保 IntelligentAgent.ShouldMakeDecision() 能检测到并启动下一步
            if (agentState != null) agentState.Status = AgentStatus.Idle;
        }
        else
        {
            BroadcastContextUpdate();
        }
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

        // 同一步骤已在处理中，不重复启动
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
    // LLM-A：步骤 → 原子动作序列
    // ─────────────────────────────────────────────────────────────

    private IEnumerator RunLLMA(PlanStep step)
    {
        // 这个协程的输入是规划层给出的当前步骤，目标是把 step 解释成原子动作数组，
        // 最终写入 ctx.actionQueue，供执行层逐条消费。
        SetStatus(ADMStatus.Interpreting);

        // 这里只是切换 ADM 运行状态，同时把同一个状态同步到 ctx.status；
        // 它不会推进动作，也不会自动开始执行。

        // 给 LLM 的相对地图（以本机为中心，带罗盘方位）
        string relativeMap = campusGrid != null
            ? MapTopologySerializer.GetAgentRelativeMap(campusGrid, agentState.Position)
            : "(地图不可用)";

        string prompt = BuildLLMAPrompt(step, relativeMap);

        string llmResult = null;

        // 这里的 yield return StartCoroutine(...) 表示：
        // 当前协程会暂停，等待 SendRequest() 完成网络请求并通过回调写入 llmResult，
        // 然后再继续往下执行。
        yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 900));

        // 如果 LLM 连可用文本都没返回，这一步直接失败。
        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM-A 返回空");
            SetStatus(ADMStatus.Failed);
            if (agentState != null) agentState.Status = AgentStatus.Idle;  // CRIT-2
            yield break;
        }
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-A 原始回复: {llmResult}");

        // 先用 ExtractJson() 从”可能夹杂说明文字或 ```json 代码块”的回复里提取 JSON 主体，
        // 再反序列化成 AtomicAction[]。
        AtomicAction[] actions = null;
        try { actions = JsonConvert.DeserializeObject<AtomicAction[]>(ExtractJson(llmResult)); }
        catch (Exception e) { Debug.LogError($"[ADM] LLM-A JSON解析失败: {e.Message}"); SetStatus(ADMStatus.Failed); if (agentState != null) agentState.Status = AgentStatus.Idle; yield break; }

        // 能解析但结果为空，也视为无效输出。
        if (actions == null || actions.Length == 0)
        {
            Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM-A 解析结果为空数组");
            SetStatus(ADMStatus.Failed);
            if (agentState != null) agentState.Status = AgentStatus.Idle;  // CRIT-2
            yield break;
        }

        // 到这里说明“步骤 -> 原子动作数组”转换成功。
        // 后续执行层会从 ctx.actionQueue[currentActionIdx] 开始逐条取动作。
        ctx.actionQueue      = actions;
        ctx.currentActionIdx = 0;
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-A 生成 {actions.Length} 个动作");

        // 先把当前动作目标广播到队伍黑板，让队友知道我接下来打算去哪些地点；
        // 然后打开一个短暂协商窗口，进入 Negotiating，等待冲突检测/重规划。
        BroadcastContextUpdate();
        negotiationWindowEnd = Time.time + GetNegotiationWindow();
        SetStatus(ADMStatus.Negotiating);
    }

    private string BuildLLMAPrompt(PlanStep step, string relativeMap)
    {
        string constraintStr = ctx.coordinationConstraints?.Length > 0
            ? string.Join("; ", ctx.coordinationConstraints)
            : null;
        string constraintBlock = string.IsNullOrWhiteSpace(constraintStr)
            ? "无（本步骤独立执行）"
            : constraintStr + "\n【强制要求】上述约束必须体现在至少一个动作的 spatialHint 或 actionParams 中，不得只把它当背景信息忽略。";

        string perception = BuildPerceptionSnapshot();
        string perceptionBlock = string.IsNullOrWhiteSpace(perception) ? "无感知数据" : perception;

        return
            "你是无人机战术执行规划器，负责将一个任务步骤分解为具体的原子动作序列（通常3~6个动作）。\n" +
            "你的输出将直接驱动无人机执行，每个动作必须具体、可执行、有明确目标。\n\n" +
            "═══ 任务信息 ═══\n" +
            $"步骤文本：{step.text}\n" +
            $"当前角色：{ctx.role}\n" +
            $"当前位置：{ctx.currentLocationName}\n\n" +
            "═══ 协同约束 ═══\n" +
            constraintBlock + "\n\n" +
            "═══ 周边地图（以本机为中心，半径300m） ═══\n" +
            relativeMap + "\n\n" +
            "═══ 环境感知 ═══\n" +
            perceptionBlock + "\n\n" +
            "═══ 原子动作类型说明 ═══\n" +
            "• MoveTo：前往指定地点。\n" +
            "  targetName=目的地（必须是地图中存在的名称），\n" +
            "  spatialHint=路径偏好，如\"从东侧接近\"、\"沿主干道飞行\"，\n" +
            "  actionParams=飞行参数，如\"高度50米\"、\"速度慢速\"\n\n" +
            "• PatrolAround：在目标地点周围环绕巡逻。\n" +
            "  targetName=巡逻中心，\n" +
            "  actionParams=必填，如\"环绕半径40米，方向顺时针\"，\n" +
            "  duration：-1=完成一圈后结束，正数=持续秒数\n\n" +
            "• Observe：对目标区域定点观察，激活感知扫描。\n" +
            "  targetName=观察目标，\n" +
            "  actionParams=必填，如\"时长15秒\"，\n" +
            "  duration：-1=条件触发结束\n\n" +
            "• Evade：规避障碍物，或主动调整飞行高度。\n" +
            "  actionParams=必填，如\"上升至80米\"、\"向东规避30米\"、\"降至悬停高度\"\n\n" +
            "• Wait：原地悬停等待。\n" +
            "  actionParams=必填等待条件，如\"等待20秒\"、\"等待队友drone_2到达集合点\"\n\n" +
            "• FormationHold：与指定队友保持相对位置协同移动。\n" +
            "  targetAgentId=队友AgentID，\n" +
            "  actionParams=必填相对偏移，如\"左翼20米\"、\"后方30米且高度+10米\"\n\n" +
            "• Broadcast：向组内其他智能体广播信息。\n" +
            "  broadcastContent=必填，简洁描述当前状态或发现\n\n" +
            "═══ 规划流程（按顺序思考）═══\n" +
            "① 分析环境感知：感知到的障碍是否阻挡前进路径？是否需要在 MoveTo 前插入 Evade？\n" +
            "② 分析协同约束：它要求改变路径、高度、时序还是队形？如何用 spatialHint/actionParams 体现？\n" +
            "③ 规划路径：从当前位置到目标，考虑无人机可垂直起降——先升高再平飞往往更合理\n" +
            "④ 输出 3~6 个动作，覆盖从当前位置出发到完成步骤目标的完整过程\n\n" +
            "═══ 示例（步骤：\"侦察A楼\"，当前位置：操场，协同约束：\"从建筑北侧接近\"）═══\n" +
            "[\n" +
            "  {\"actionId\":\"aa_1\",\"type\":\"Evade\",\"targetName\":\"\",\"targetAgentId\":\"\",\"duration\":0," +
            "\"broadcastContent\":\"\",\"actionParams\":\"上升至60米安全高度\",\"spatialHint\":\"\"},\n" +
            "  {\"actionId\":\"aa_2\",\"type\":\"MoveTo\",\"targetName\":\"A楼\",\"targetAgentId\":\"\",\"duration\":0," +
            "\"broadcastContent\":\"\",\"actionParams\":\"保持60米高度\",\"spatialHint\":\"绕行至A楼北侧，遵守协同约束\"},\n" +
            "  {\"actionId\":\"aa_3\",\"type\":\"PatrolAround\",\"targetName\":\"A楼\",\"targetAgentId\":\"\",\"duration\":-1," +
            "\"broadcastContent\":\"\",\"actionParams\":\"环绕半径40米，方向顺时针\",\"spatialHint\":\"\"},\n" +
            "  {\"actionId\":\"aa_4\",\"type\":\"Observe\",\"targetName\":\"A楼\",\"targetAgentId\":\"\",\"duration\":15," +
            "\"broadcastContent\":\"\",\"actionParams\":\"观察时长15秒，重点关注北侧出入口\",\"spatialHint\":\"\"},\n" +
            "  {\"actionId\":\"aa_5\",\"type\":\"Broadcast\",\"targetName\":\"\",\"targetAgentId\":\"\",\"duration\":0," +
            "\"broadcastContent\":\"A楼侦察完成，北侧无异常，等待后续指令\",\"actionParams\":\"\",\"spatialHint\":\"\"}\n" +
            "]\n\n" +
            "═══ 输出要求 ═══\n" +
            "1. 只输出 JSON 数组，不包含任何说明或注释文字\n" +
            "2. 每个动作包含以下全部字段（不可缺少，无内容填\"\"或0）：\n" +
            "   actionId(\"aa_N\") / type / targetName / targetAgentId /\n" +
            "   duration / broadcastContent / actionParams / spatialHint\n" +
            "3. targetName 必须是周边地图中出现的地点名称，禁止使用地图中不存在的地名\n" +
            "4. 必须生成至少 2 个动作（除非步骤仅为纯 Wait 或纯 Broadcast）\n" +
            "5. 感知到 BlocksMovement=true 的障碍时，在对应方向的 MoveTo 前必须插入 Evade\n" +
            "6. 协同约束非空时，必须有至少一个动作通过 spatialHint 或 actionParams 明确体现约束要求\n";
    }

    // ─────────────────────────────────────────────────────────────
    // LLM-B：重规划
    // ─────────────────────────────────────────────────────────────

    private IEnumerator RunLLMB(string triggerReason)
    {
        // 这个协程负责“重规划当前步骤的原子动作队列”。
        // 输入不是新的 PlanStep，而是一个触发原因字符串，例如：
        // - 与队友目标冲突
        // - 感知到障碍/敌情
        // 它会基于现有 ctx、黑板信息和地图重新生成 AtomicAction[]。
        SetStatus(ADMStatus.Replanning);

        // 先把当前已知的队友黑板状态整理成一段文本，作为 LLM-B 的输入上下文。
        // 这里重点告诉模型：队友接下来准备去哪些目标地点。
        var teammatesInfo = new System.Text.StringBuilder();
        foreach (var kv in blackboard)
        {
            string targets = kv.Value.plannedTargets != null
                ? string.Join(", ", kv.Value.plannedTargets)
                : "无";
            teammatesInfo.AppendLine($"  {kv.Key}({kv.Value.role}): 计划目标=[{targets}]");
        }

        // 全局地图仍然只提供“全图概览”，帮助 LLM 在重规划时选择合法地点。
        string globalMap = campusGrid != null ? MapTopologySerializer.GetGlobalFoldedMap(campusGrid) : "(地图不可用)";

        // BuildLLMBPrompt() 只拼接 prompt，不发请求、不解析结果。
        string prompt    = BuildLLMBPrompt(triggerReason, teammatesInfo.ToString(), globalMap);

        // 用于接收 LLM-B 原始文本回复。
        string llmResult = null;

        // 等待 LLM 请求完成，再继续处理结果。
        yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 600));

        // 如果模型没有返回可用文本，则不强制失败，而是回退到原执行队列继续 Running。
        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM-B 返回空，维持当前执行");
            SetStatus(ADMStatus.Running);
            yield break;
        }
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-B 原始回复: {llmResult}");

        // 与 LLM-A 一样，先提取 JSON 主体，再反序列化成新的原子动作数组。
        AtomicAction[] newActions = null;
        try { newActions = JsonConvert.DeserializeObject<AtomicAction[]>(ExtractJson(llmResult)); }
        catch (Exception e)
        {
            // 解析失败时不清空旧队列，直接恢复 Running，继续执行原计划。
            Debug.LogError($"[ADM] LLM-B JSON解析失败: {e.Message}，维持当前执行");
            SetStatus(ADMStatus.Running);
            yield break;
        }

        // 即使语法合法，但如果结果为空数组，也视为“重规划无效”，继续沿用旧队列。
        if (newActions == null || newActions.Length == 0)
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM-B 解析结果为空，维持当前执行");
            SetStatus(ADMStatus.Running);
            yield break;
        }

        // 到这里说明重规划成功：用新队列整体替换旧的 actionQueue，
        // 并把执行下标重置到 0，从新计划的第一条原子动作开始执行。
        ctx.actionQueue      = newActions;
        ctx.currentActionIdx = 0;

        // 重规划后的目标集合也要重新广播给队友，然后重新打开协商窗口，
        // 让新的计划先经过一次冲突检测，再进入 Running。
        BroadcastContextUpdate();
        negotiationWindowEnd = Time.time + GetNegotiationWindow();
        SetStatus(ADMStatus.Negotiating);
    }

    private string BuildLLMBPrompt(string triggerReason, string teammatesInfo, string globalMap)
    {
        // remaining 表示“旧计划里当前仍未执行完的原子动作摘要”。
        // 它不是规划层的 PlanStep[]，而是当前 actionQueue 的文本化结果，
        // 用来告诉 LLM：这次重规划是在什么基础上调整。
        string remaining = ctx.actionQueue != null
            ? string.Join(", ", Array.ConvertAll(ctx.actionQueue, a => $"{a.type}({a.targetName})"))
            : "无";

        // 这里只做 prompt 拼装：
        // - triggerReason 说明为什么需要重规划
        // - ctx.stepText 说明原始步骤目标是什么
        // - teammatesInfo 提供队友黑板状态
        // - globalMap 提供可选地点范围
        // 它不负责请求发送，也不负责结果校验。
        return
            "你是无人机重规划器。根据新情况为本机生成新的原子动作序列。\n\n" +
            $"触发原因：{triggerReason}\n" +
            $"原始步骤：{ctx.stepText}\n" +
            $"当前位置：{ctx.currentLocationName}\n" +
            $"协调约束：{(ctx.coordinationConstraints?.Length > 0 ? string.Join("; ", ctx.coordinationConstraints) : "无")}\n" +
            $"原计划剩余：{remaining}\n\n" +
            "队友黑板状态：\n" +
            (string.IsNullOrWhiteSpace(teammatesInfo) ? "  (无数据)\n" : teammatesInfo) +
            $"\n全局地图：\n{globalMap}\n\n" +
            "输出要求：\n" +
            "1. 只输出 JSON 数组。\n" +
            "2. 字段：actionId, type, targetName, targetAgentId, duration, broadcastContent, actionParams, spatialHint（不再有 radius 字段）。\n" +
            "3. 避免与上述队友的计划目标产生地点冲突。\n" +
            "4. 完成原始步骤的核心目标。\n" +
            "5. 必须生成至少 2 个动作，除非步骤极其简单。\n";
    }

    // ─────────────────────────────────────────────────────────────
    // 冲突检测与协商
    // ─────────────────────────────────────────────────────────────

    private void CheckConflictsAndMaybeReplan()
    {
        if (ctx?.actionQueue == null) { SetStatus(ADMStatus.Running); return; }

        // 防死锁：重规划次数过多，上报失败
        if (replanCount >= MaxReplanCount)
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} 步骤 {ctx?.stepId} 重规划次数耗尽，上报失败");
            SetStatus(ADMStatus.Failed);
            planningModule?.OnStepFailed(ctx?.stepId ?? "unknown", "replan_limit_exceeded");
            return;
        }

        string myId       = agentProperties?.AgentID ?? string.Empty;
        var    ownTargets = BuildOwnTargetSet();

        // ① 全量扫描，跳过自身条目（修复 self-conflict 误计）
        int myConflict = 0;
        foreach (var kv in blackboard)
        {
            if (string.Equals(kv.Key, myId, StringComparison.OrdinalIgnoreCase)) continue;
            myConflict += CountConflicts(kv.Value.plannedTargets, ownTargets);
        }

        if (myConflict == 0) { SetStatus(ADMStatus.Running); return; }

        // ② 选举：比较各 agent 的【全局冲突总数】，总数更大者优先；相同时字典序大的 ID 优先
        //    BUG-02 修复：原代码用 pairwise 冲突与 total 冲突比较，在 N≥3 时 pairwise < total
        //    导致所有 agent 均认为自己是 resolver，现改为对等的 total vs total 比较
        bool anyoneMoreEligible = false;
        foreach (var kv in blackboard)
        {
            if (string.Equals(kv.Key, myId, StringComparison.OrdinalIgnoreCase)) continue;
            int pairwise = CountConflicts(kv.Value.plannedTargets, ownTargets);
            if (pairwise == 0) continue; // 与我无直接冲突的队友不参与选举

            // 计算该队友与所有其他 agent 的冲突总数（本机有完整黑板可以推算）
            int theirTotal = ComputeTotalConflictForPeer(kv.Key);
            if (theirTotal > myConflict ||
                (theirTotal == myConflict &&
                 string.Compare(kv.Key, myId, StringComparison.Ordinal) > 0))
            {
                anyoneMoreEligible = true;
                break;
            }
        }

        if (!anyoneMoreEligible)
        {
            // 本机为 resolver：只为自己重规划，然后广播；非 resolver 收到广播后自行处理
            replanCount++;
            // BUG-06 修复：启动新协程前先停止可能残留的旧协程，避免并发操作 ctx.actionQueue
            if (activeCoroutine != null) { StopCoroutine(activeCoroutine); activeCoroutine = null; }
            activeCoroutine = StartCoroutine(
                RunLLMB($"与队友地点冲突({myConflict}处)，本机为解决者"));
        }
        else
        {
            // 非 resolver：进入 Running 等待 resolver 广播，由 OnBoardUpdate 触发下一轮检测
            Debug.Log($"[ADM] {agentProperties?.AgentID} 非 resolver，等待");
            SetStatus(ADMStatus.Running);
        }
    }

    private HashSet<string> BuildOwnTargetSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ctx?.actionQueue == null) return set;
        foreach (var a in ctx.actionQueue)
            if (!string.IsNullOrWhiteSpace(a.targetName))
                set.Add(a.targetName);
        return set;
    }

    // TODO [SPATIAL-CONFLICT]: 当前冲突检测基于目标地点名称字符串完全匹配，存在以下已知局限：
    // 1. 相向飞行（不同目标名但路径交叉）无法检测
    // 2. 相近地名（"图书馆"与"图书馆北侧"）不识别为冲突
    // 3. PatrolAround 动作的实际占用半径未计入
    // 正确做法：集成 MapTopologySerializer 的邻域查询 API，对目标节点做 k-hop 邻域展开后再匹配。
    private static int CountConflicts(string[] theirTargets, HashSet<string> ownTargets)
    {
        if (theirTargets == null) return 0;
        int n = 0;
        foreach (string t in theirTargets)
            if (ownTargets.Contains(t)) n++;
        return n;
    }

    /// <summary>
    /// 从本机黑板视角计算指定 peer 的全局冲突总数
    /// （peer 的 plannedTargets 与黑板中所有其他 agent 的 plannedTargets 的重叠数之和）。
    /// 用于选举时的对等比较，避免 pairwise vs total 的不对称问题（BUG-02）。
    /// </summary>
    private int ComputeTotalConflictForPeer(string peerId)
    {
        if (!blackboard.TryGetValue(peerId, out AgentContextUpdate peerEntry)) return 0;
        int total = 0;
        foreach (var kv in blackboard)
        {
            if (string.Equals(kv.Key, peerId, StringComparison.OrdinalIgnoreCase)) continue;
            var otherTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (kv.Value.plannedTargets != null)
                foreach (string t in kv.Value.plannedTargets)
                    if (!string.IsNullOrWhiteSpace(t)) otherTargets.Add(t);
            total += CountConflicts(peerEntry.plannedTargets, otherTargets);
        }
        return total;
    }

    // ─────────────────────────────────────────────────────────────
    // 感知事件处理
    // ─────────────────────────────────────────────────────────────

    private void HandlePendingPerceptionEvents()
    {
        // BUG-05 修复：ctx 可能为 null（Running 状态由 CheckConflictsAndMaybeReplan 在 actionQueue==null 时设置）
        if (ctx == null) { pendingPerceptionEvents.Clear(); return; }

        while (pendingPerceptionEvents.Count > 0)
        {
            var (desc, loc) = pendingPerceptionEvents.Dequeue();
            if (!EventRequiresReplan(desc)) continue;

            // 将事件记录到上下文（最近3条，时间倒序）
            var events = new List<string>(ctx.recentEvents ?? Array.Empty<string>());
            events.Insert(0, $"[{loc}] {desc}");
            if (events.Count > 3) events.RemoveAt(3);
            ctx.recentEvents = events.ToArray();

            SetStatus(ADMStatus.Interrupted);
            BroadcastContextUpdate();
            // BUG-H3: 启动 RunLLMB 前停止旧协程
            if (activeCoroutine != null) { StopCoroutine(activeCoroutine); activeCoroutine = null; }
            activeCoroutine = StartCoroutine(RunLLMB($"感知事件: {desc} @ {loc}"));
            return;   // 每次只触发一次重规划
        }
    }

    // TODO [REPLAN-TRIGGER]: 当前由 ADM 内部轮询 ctx.recentEvents 并调用此方法判断是否重规划。
    // 后续应由 PerceptionModule 在感知到威胁/障碍事件时直接调用 adm.TriggerReplan(eventDesc)，
    // 将决策权从决策层移至感知层，消除硬编码关键词依赖。
    private static bool EventRequiresReplan(string desc)
    {
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    // 广播黑板更新
    // ─────────────────────────────────────────────────────────────

    private void BroadcastContextUpdate()
    {
        if (commModule == null || agentProperties == null || ctx == null) return;

        // 这里广播的是”当前步骤拆解出来的原子动作目标集合”，
        // 不是规划层的 PlanStep[]。plannedTargets 会从 ctx.actionQueue 中去重提取。
        // BUG-M2: 使用 HashSet 去重，避免 O(n²) 的 List.Contains
        var targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ctx.actionQueue != null)
            foreach (var a in ctx.actionQueue)
                if (!string.IsNullOrWhiteSpace(a.targetName))
                    targetSet.Add(a.targetName);
        var targets = new List<string>(targetSet);

        AtomicAction cur = GetCurrentAction();
        var update = new AgentContextUpdate
        {
            agentId        = agentProperties.AgentID,
            locationName   = ResolveCurrentLocationName(),  // BUG-H1: 实时获取，非缓存
            currentAction  = cur != null ? cur.type.ToString() : "None",
            currentTarget  = cur?.targetName ?? string.Empty,
            role           = ctx.role.ToString(),
            plannedTargets = targets.ToArray(),
            recentEvents   = ctx.recentEvents ?? Array.Empty<string>(),
            timestamp      = Time.time
        };

        // 广播给队友前，先同步写入本机黑板缓存，后续冲突检测会直接读这里。
        blackboard[agentProperties.AgentID] = update;

        commModule.SendScopedMessage(
            CommunicationScope.Team,
            MessageType.BoardUpdate,
            update,
            targetTeamId: agentProperties.TeamID.ToString(),
            reliable: true);
    }

    // ─────────────────────────────────────────────────────────────
    // 感知快照（BuildLLMAPrompt 调用）
    // ─────────────────────────────────────────────────────────────

    private string BuildPerceptionSnapshot()
    {
        if (agentState == null) return string.Empty;

        Vector3 myPos = agentState.Position;
        const float MaxDist = 50f;

        var sb = new StringBuilder();
        bool hasData = false;

        // ── 障碍物（DetectedSmallNodes 由 PerceptionModule 每帧更新，只读）──
        var nodes = agentState.DetectedSmallNodes;
        if (nodes != null && nodes.Count > 0)
        {
            var nearby = nodes.Where(n => Vector3.Distance(myPos, n.WorldPosition) <= MaxDist).ToList();
            if (nearby.Count > 0)
            {
                sb.AppendLine("附近障碍（已感知）：");
                foreach (var n in nearby)
                {
                    string dir   = GetCompassDir8(myPos, n.WorldPosition);
                    int    dist  = Mathf.RoundToInt(Vector3.Distance(myPos, n.WorldPosition) / 5f) * 5;
                    string label = !string.IsNullOrWhiteSpace(n.DisplayName)
                                   ? n.DisplayName : NodeTypeDisplayName(n.NodeType);
                    string extra = (n.IsDynamic ? "动态" : "静态")
                                 + (n.BlocksMovement ? "，阻挡移动" : "");
                    sb.AppendLine($"  - {label}({dir}约{dist}m，{extra})");
                }
                hasData = true;
            }
        }

        // ── 附近队友（NearbyAgents key 为 agentId，查 blackboard 获取状态）──
        var nearbyAgents = agentState.NearbyAgents;
        if (nearbyAgents != null && nearbyAgents.Count > 0)
        {
            sb.AppendLine("附近队友（通信已知）：");
            foreach (var kv in nearbyAgents)
            {
                if (blackboard.TryGetValue(kv.Key, out AgentContextUpdate info))
                {
                    string act = string.IsNullOrWhiteSpace(info.currentTarget)
                        ? info.currentAction
                        : $"{info.currentAction}({info.currentTarget})";
                    sb.AppendLine($"  - {kv.Key}({info.role}): 在{info.locationName}，执行{act}");
                }
                else
                {
                    sb.AppendLine($"  - {kv.Key}: 状态未知");
                }
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
        SmallNodeType.Tree              => "树木",
        SmallNodeType.Pedestrian        => "行人",
        SmallNodeType.Vehicle           => "车辆",
        SmallNodeType.ResourcePoint     => "资源点",
        SmallNodeType.TemporaryObstacle => "临时障碍",
        SmallNodeType.Agent             => "智能体",
        _                               => "未知障碍"
    };

    // ─────────────────────────────────────────────────────────────
    // 工具方法
    // ─────────────────────────────────────────────────────────────

    private float GetNegotiationWindow()
    {
        int memberCount = blackboard.Count + 1; // +1 for self
        return Mathf.Clamp(0.2f + memberCount * 0.1f, 0.3f, 1.5f);
    }

    private void SetStatus(ADMStatus s)
    {
        // 只维护 ADM 当前运行状态，并把同样的状态写入 ctx 供外部观察；
        // 它本身不负责启动动作、不推进 currentActionIdx。
        status = s;
        if (ctx != null) ctx.status = s;
        Debug.Log($"[ADM] {agentProperties?.AgentID} → {s}");
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
        // 容错提取器：LLM 可能返回纯 JSON，也可能包在 ```json 代码块里，
        // 还可能在前后夹杂说明文字。这里尽量截出最像 JSON 的主体字符串。
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        Match m = JsonBlockRe.Match(raw);
        if (m.Success) return m.Groups[1].Value.Trim();
        int start    = raw.IndexOf('[');
        int startObj = raw.IndexOf('{');
        if (startObj >= 0 && (start < 0 || startObj < start)) start = startObj;
        if (start >= 0)
        {
            char open  = raw[start];
            char close = open == '[' ? ']' : '}';
            int end    = raw.LastIndexOf(close);
            if (end > start) return raw.Substring(start, end - start + 1);
        }
        return raw.Trim();
    }

    // ─────────────────────────────────────────────────────────────
    // 仪表板查询接口（供 AgentStateServer 调用）
    // ─────────────────────────────────────────────────────────────

    public string[] GetPlannedTargets() {
        if (ctx?.actionQueue == null) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var res  = new List<string>();
        for (int i = ctx.currentActionIdx; i < ctx.actionQueue.Length; i++) {
            var t = ctx.actionQueue[i].targetName;
            if (!string.IsNullOrWhiteSpace(t) && seen.Add(t)) res.Add(t);
        }
        return res.ToArray();
    }

    public string[] GetRecentEvents() => ctx?.recentEvents ?? Array.Empty<string>();
}
