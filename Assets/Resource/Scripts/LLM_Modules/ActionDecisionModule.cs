// LLM_Modules/ActionDecisionModule.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
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
    private const float        NegotiationWindowSec = 0.5f;

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
            msnId                  = planningModule?.currentMission?.missionId ?? string.Empty,
            stepId                 = step.stepId,
            stepText               = step.text,
            coordinationConstraint = step.constraint ?? string.Empty,
            role                   = agentProperties != null ? agentProperties.Role : RoleType.Scout,
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
        negotiationWindowEnd = Time.time + NegotiationWindowSec;
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

    /// <summary>向后兼容：由 IntelligentAgent.MakeDecisionCoroutine 调用的协程入口。</summary>
    public IEnumerator<object> DecideNextAction()
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

        // 给 LLM 的全局地图摘要。这里是“全图概览”，帮助模型知道有哪些合法地名。
        string globalMap    = campusGrid != null ? MapTopologySerializer.GetGlobalFoldedMap(campusGrid) : "(地图不可用)";

        string prompt = BuildLLMAPrompt(step, globalMap);

        string llmResult = null;

        // 这里的 yield return StartCoroutine(...) 表示：
        // 当前协程会暂停，等待 SendRequest() 完成网络请求并通过回调写入 llmResult，
        // 然后再继续往下执行。
        yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 600));

        // 如果 LLM 连可用文本都没返回，这一步直接失败。
        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM-A 返回空");
            SetStatus(ADMStatus.Failed);
            yield break;
        }
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-A 原始回复: {llmResult}");

        // 先用 ExtractJson() 从“可能夹杂说明文字或 ```json 代码块”的回复里提取 JSON 主体，
        // 再反序列化成 AtomicAction[]。
        AtomicAction[] actions = null;
        try { actions = JsonConvert.DeserializeObject<AtomicAction[]>(ExtractJson(llmResult)); }
        catch (Exception e) { Debug.LogError($"[ADM] LLM-A JSON解析失败: {e.Message}"); SetStatus(ADMStatus.Failed); yield break; }

        // 能解析但结果为空，也视为无效输出。
        if (actions == null || actions.Length == 0)
        {
            Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM-A 解析结果为空数组");
            SetStatus(ADMStatus.Failed);
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
        negotiationWindowEnd = Time.time + NegotiationWindowSec;
        SetStatus(ADMStatus.Negotiating);
    }

    private string BuildLLMAPrompt(PlanStep step, string globalMap)//【待优化】对于一个步骤，是否一定可以拆成动作和目标的集合；
    {
        return
            "你是无人机任务执行层。将步骤文本拆解为 JSON 原子动作数组。\n\n" +
            $"步骤文本：{step.text}\n" +
            $"当前角色：{ctx.role}\n" +
            $"当前位置：{ctx.currentLocationName}\n" +
            $"协调约束：{(string.IsNullOrWhiteSpace(ctx.coordinationConstraint) ? "无" : ctx.coordinationConstraint)}\n\n" +
            $"全局地图：\n{globalMap}\n\n" +
            "原子动作类型枚举：MoveTo, PatrolAround, Observe, Wait, FormationHold, Broadcast, Evade\n\n" +
            "输出要求：\n" +
            "1. 只输出 JSON 数组。\n" +
            "2. 每项字段：actionId(\"aa_N\"), type(枚举名), targetName(地图中存在的地名，无目标填\"\"), " +
               "targetAgentId(\"\"), radius(float), duration(float), broadcastContent(\"\")。\n" +
            "3. 禁止使用地图中不存在的地名。\n" +
            "4. 仅包含步骤所需的动作，不额外添加。\n\n" +
            "示例输入/输出：\n" +
            "步骤：\"飞往A楼\"\n" +
            "[\n" +
            "  {\n" +
            "    \"actionId\": \"aa_1\",\n" +
            "    \"type\": \"MoveTo\",\n" +
            "    \"targetName\": \"A楼\",\n" +
            "    \"targetAgentId\": \"\",\n" +
            "    \"radius\": 0,\n" +
            "    \"duration\": 0,\n" +
            "    \"broadcastContent\": \"\"\n" +
            "  }\n" +
            "]";
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
        negotiationWindowEnd = Time.time + NegotiationWindowSec;
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
            $"协调约束：{(string.IsNullOrWhiteSpace(ctx.coordinationConstraint) ? "无" : ctx.coordinationConstraint)}\n" +
            $"原计划剩余：{remaining}\n\n" +
            "队友黑板状态：\n" +
            (string.IsNullOrWhiteSpace(teammatesInfo) ? "  (无数据)\n" : teammatesInfo) +
            $"\n全局地图：\n{globalMap}\n\n" +
            "输出要求（与 LLM-A 格式完全相同）：\n" +
            "1. 只输出 JSON 数组。\n" +
            "2. 避免与上述队友的计划目标产生地点冲突。\n" +
            "3. 完成原始步骤的核心目标。";
    }

    // ─────────────────────────────────────────────────────────────
    // 冲突检测与协商
    // ─────────────────────────────────────────────────────────────

    private void CheckConflictsAndMaybeReplan()
    {
        if (ctx?.actionQueue == null) { SetStatus(ADMStatus.Running); return; }

        // 防死锁：重规划次数过多，强制 Running
        if (replanCount >= MaxReplanCount)
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} 重规划次数达上限({MaxReplanCount})，强制 Running");
            SetStatus(ADMStatus.Running);
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
            activeCoroutine = StartCoroutine(RunLLMB($"感知事件: {desc} @ {loc}"));
            return;   // 每次只触发一次重规划
        }
    }

    private static bool EventRequiresReplan(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        string l = desc.ToLowerInvariant();
        return l.Contains("障碍") || l.Contains("威胁") || l.Contains("敌方") ||
               l.Contains("blocked") || l.Contains("enemy") || l.Contains("threat");
    }

    // ─────────────────────────────────────────────────────────────
    // 广播黑板更新
    // ─────────────────────────────────────────────────────────────

    private void BroadcastContextUpdate()
    {
        if (commModule == null || agentProperties == null || ctx == null) return;

        // 这里广播的是“当前步骤拆解出来的原子动作目标集合”，
        // 不是规划层的 PlanStep[]。plannedTargets 会从 ctx.actionQueue 中去重提取。
        var targets = new List<string>();
        if (ctx.actionQueue != null)
            foreach (var a in ctx.actionQueue)
                if (!string.IsNullOrWhiteSpace(a.targetName) && !targets.Contains(a.targetName))
                    targets.Add(a.targetName);

        AtomicAction cur = GetCurrentAction();
        var update = new AgentContextUpdate
        {
            agentId        = agentProperties.AgentID,
            locationName   = ctx.currentLocationName,
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
    // 工具方法
    // ─────────────────────────────────────────────────────────────

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
}
