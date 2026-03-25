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
        if (status == ADMStatus.Running && pendingPerceptionEvents.Count > 0)
            HandlePendingPerceptionEvents();
    }

    // ─────────────────────────────────────────────────────────────
    // 公共接口
    // ─────────────────────────────────────────────────────────────

    /// <summary>由 PlanningModule 在 Active 状态下调用，传入当前步骤。</summary>
    public void StartStep(PlanStep step)
    {
        if (step == null) return;
        if (activeCoroutine != null) { StopCoroutine(activeCoroutine); activeCoroutine = null; }

        ctx = new ActionExecutionContext
        {
            msnId = planningModule?.GetCurrentMissionId() ?? string.Empty,
            stepId = step.stepId,
            stepText = step.text,
            coordinationConstraints = step.constraints ?? Array.Empty<string>(),
            role = agentProperties != null ? agentProperties.Role : RoleType.Scout,
            actionQueue = null,
            currentActionIdx = 0,
            status = ADMStatus.Idle,
            currentLocationName = ResolveCurrentLocationName(),
            recentEvents = Array.Empty<string>(),
        };

        replanCount = 0;
        SetStatus(ADMStatus.Idle);
        activeCoroutine = StartCoroutine(RunLLMA(step));
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

    /// <summary>执行层通知当前动作已完成，ADM 推进队列。</summary>
    public void CompleteCurrentAction()
    {
        if (ctx?.actionQueue == null) return;
        ctx.currentActionIdx++;

        if (ctx.currentActionIdx >= ctx.actionQueue.Length)
        {
            SetStatus(ADMStatus.Done);
            planningModule?.CompleteCurrentStep();
            Debug.Log($"[ADM] {agentProperties?.AgentID} 当前步骤动作全部完成，通知 PlanningModule");
            if (agentState != null) agentState.Status = AgentStatus.Idle;
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
    // LLM-A：步骤 → 原子动作序列
    // ─────────────────────────────────────────────────────────────

    private IEnumerator RunLLMA(PlanStep step)
    {
        SetStatus(ADMStatus.Interpreting);

        string relativeMap = campusGrid != null
            ? MapTopologySerializer.GetAgentRelativeMap(campusGrid, agentState.Position)
            : "(地图不可用)";

        string prompt = BuildLLMAPrompt(step, relativeMap);
        string llmResult = null;

        yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 900));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM-A 返回空");
            SetStatus(ADMStatus.Failed);
            if (agentState != null) agentState.Status = AgentStatus.Idle;
            yield break;
        }
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-A 原始回复: {llmResult}");

        AtomicAction[] actions = null;
        try
        {
            actions = JsonConvert.DeserializeObject<AtomicAction[]>(ExtractJson(llmResult));
        }
        catch (Exception e)
        {
            Debug.LogError($"[ADM] LLM-A JSON解析失败: {e.Message}");
            SetStatus(ADMStatus.Failed);
            if (agentState != null) agentState.Status = AgentStatus.Idle;
            yield break;
        }

        if (actions == null || actions.Length == 0)
        {
            Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM-A 解析结果为空数组");
            SetStatus(ADMStatus.Failed);
            if (agentState != null) agentState.Status = AgentStatus.Idle;
            yield break;
        }

        ctx.actionQueue = actions;
        ctx.currentActionIdx = 0;
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-A 生成 {actions.Length} 个动作，直接进入 Running");
        SetStatus(ADMStatus.Running);
    }

    private string BuildLLMAPrompt(PlanStep step, string relativeMap)
    {
        string constraintStr = ctx.coordinationConstraints?.Length > 0
            ? string.Join("; ", ctx.coordinationConstraints)
            : null;
        string constraintBlock = string.IsNullOrWhiteSpace(constraintStr)
            ? "无（本步骤独立执行）"
            : constraintStr + "\n【强制要求】上述约束必须体现在至少一个动作的 spatialHint 或 actionParams 中，不得只当背景信息忽略。";

        string perception = BuildPerceptionSnapshot();
        string perceptionBlock = string.IsNullOrWhiteSpace(perception) ? "无感知数据" : perception;

        return
            "你是无人机战术执行规划器，负责把一个任务步骤拆成具体的原子动作序列。\n" +
            "输出会直接驱动无人机执行，每个动作都必须具体、可执行、目标明确。\n\n" +
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
            "  spatialHint=路径偏好，如“从东侧接近”“沿主干道飞行”，\n" +
            "  actionParams=飞行参数，如“高度50米”“速度慢速”\n\n" +
            "• PatrolAround：在目标地点周围环绕巡逻。\n" +
            "  targetName=巡逻中心，\n" +
            "  actionParams=必填，如“环绕半径40米，方向顺时针”，\n" +
            "  duration：-1=完成一圈后结束，正数=持续秒数\n\n" +
            "• Observe：对目标区域定点观察，激活感知扫描。\n" +
            "  targetName=观察目标，\n" +
            "  actionParams=必填，如“时长15秒”，\n" +
            "  duration：-1=条件触发结束\n\n" +
            "• Evade：规避障碍物，或主动调整飞行高度。\n" +
            "  actionParams=必填，如“上升至80米”“向东规避30米”“降至悬停高度”\n\n" +
            "• Wait：原地悬停等待。\n" +
            "  actionParams=必填等待条件，如“等待20秒”“等待队友到达集合点”\n\n" +
            "• FormationHold：与指定队友保持相对位置协同移动。\n" +
            "  targetAgentId=队友AgentID，\n" +
            "  actionParams=必填相对偏移，如“左翼20米”“后方30米且高度+10米”\n\n" +
            "═══ 规划流程（按顺序思考）═══\n" +
            "1. 先分析环境感知，障碍是否阻挡前进路径，是否需要在 MoveTo 前插入 Evade。\n" +
            "2. 再分析协同约束，它要求改变路径、高度、时序还是队形，如何用 spatialHint 或 actionParams 体现。\n" +
            "3. 最后输出覆盖完整目标的动作链，从当前位置一直到完成步骤目标。\n\n" +
            "═══ 示例（步骤：侦察A楼）═══\n" +
            "[\n" +
            "  {\"actionId\":\"aa_1\",\"type\":\"Evade\",\"targetName\":\"\",\"targetAgentId\":\"\",\"duration\":0,\"actionParams\":\"上升至60米安全高度\",\"spatialHint\":\"\"},\n" +
            "  {\"actionId\":\"aa_2\",\"type\":\"MoveTo\",\"targetName\":\"A楼\",\"targetAgentId\":\"\",\"duration\":0,\"actionParams\":\"保持60米高度\",\"spatialHint\":\"绕行至A楼北侧\"},\n" +
            "  {\"actionId\":\"aa_3\",\"type\":\"PatrolAround\",\"targetName\":\"A楼\",\"targetAgentId\":\"\",\"duration\":-1,\"actionParams\":\"环绕半径40米，方向顺时针\",\"spatialHint\":\"\"},\n" +
            "  {\"actionId\":\"aa_4\",\"type\":\"Observe\",\"targetName\":\"A楼\",\"targetAgentId\":\"\",\"duration\":15,\"actionParams\":\"观察时长15秒，重点关注北侧出入口\",\"spatialHint\":\"\"}\n" +
            "]\n\n" +
            "═══ 输出要求 ═══\n" +
            "1. 只输出 JSON 数组，不包含任何说明文字。\n" +
            "2. 每个动作必须包含全部字段：actionId / type / targetName / targetAgentId / duration / actionParams / spatialHint。\n" +
            "3. targetName 必须是地图中存在的地点名称，禁止编造地名。\n" +
            "4. 必须生成至少2个动作，除非步骤本身只适合单个 Wait。\n" +
            "5. 感知到临时障碍时，在对应方向的 MoveTo 前必须插入 Evade。\n" +
            "6. 协同约束非空时，至少有一个动作通过 spatialHint 或 actionParams 明确体现约束要求。\n";
    }

    // ─────────────────────────────────────────────────────────────
    // LLM-B：重规划
    // ─────────────────────────────────────────────────────────────

    private IEnumerator RunLLMB(string triggerReason)
    {
        if (ctx == null || llmInterface == null) yield break;
        if (replanCount >= MaxReplanCount)
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} 重规划次数达到上限，保持现有动作队列");
            yield break;
        }

        replanCount++;
        SetStatus(ADMStatus.Replanning);

        string relativeMap = campusGrid != null
            ? MapTopologySerializer.GetAgentRelativeMap(campusGrid, agentState.Position)
            : "(地图不可用)";
        string prompt = BuildLLMBPrompt(triggerReason, relativeMap);
        string llmResult = null;

        yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 900));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM-B 返回空，继续使用原动作队列");
            SetStatus(ADMStatus.Running);
            yield break;
        }

        try
        {
            AtomicAction[] actions = JsonConvert.DeserializeObject<AtomicAction[]>(ExtractJson(llmResult));
            if (actions != null && actions.Length > 0)
            {
                ctx.actionQueue = actions;
                ctx.currentActionIdx = 0;
                Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-B 生成 {actions.Length} 个新动作");
            }
            else
            {
                Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM-B 未返回有效动作，沿用原队列");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM-B 解析失败，沿用原队列: {e.Message}");
        }

        SetStatus(ADMStatus.Running);
    }

    private string BuildLLMBPrompt(string triggerReason, string relativeMap)
    {
        string perception = BuildPerceptionSnapshot();
        string recentEvents = ctx?.recentEvents != null && ctx.recentEvents.Length > 0
            ? string.Join("；", ctx.recentEvents)
            : "无";

        return
            "你是无人机战术重规划器，需要根据新的感知事件调整当前步骤剩余动作。\n" +
            "只输出新的 JSON 动作数组，不要解释。\n\n" +
            $"当前步骤：{ctx?.stepText}\n" +
            $"触发原因：{triggerReason}\n" +
            $"当前位置：{ctx?.currentLocationName}\n" +
            $"剩余动作：{BuildRemainingActionSummary()}\n" +
            $"最近事件：{recentEvents}\n\n" +
            "地图摘要：\n" + relativeMap + "\n\n" +
            "感知摘要：\n" + (string.IsNullOrWhiteSpace(perception) ? "无感知数据" : perception) + "\n\n" +
            "输出要求：\n" +
            "1. 只输出 JSON 数组。\n" +
            "2. 字段必须完整：actionId/type/targetName/targetAgentId/duration/actionParams/spatialHint。\n" +
            "3. 只保留对完成当前步骤仍有必要的动作。\n" +
            "4. 如果障碍阻挡原路径，优先通过 Evade 或新的 MoveTo 路径绕行。\n";
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

    private string BuildRemainingActionSummary()
    {
        if (ctx?.actionQueue == null || ctx.currentActionIdx >= ctx.actionQueue.Length)
            return "无";

        List<string> items = new List<string>();
        for (int i = ctx.currentActionIdx; i < ctx.actionQueue.Length; i++)
        {
            AtomicAction action = ctx.actionQueue[i];
            string target = string.IsNullOrWhiteSpace(action.targetName) ? "无目标" : action.targetName;
            items.Add($"{action.type}({target})");
        }
        return string.Join(", ", items);
    }

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
        if (ctx == null || status != ADMStatus.Running) return;

        List<string> mergedEvents = new List<string>();
        if (ctx.recentEvents != null && ctx.recentEvents.Length > 0)
            mergedEvents.AddRange(ctx.recentEvents);

        List<string> newEvents = new List<string>();
        while (pendingPerceptionEvents.Count > 0)
        {
            (string desc, string location) evt = pendingPerceptionEvents.Dequeue();
            string text = string.IsNullOrWhiteSpace(evt.location)
                ? evt.desc
                : $"{evt.desc}@{evt.location}";
            newEvents.Add(text);
            mergedEvents.Add(text);
        }

        if (mergedEvents.Count > 3)
            mergedEvents = mergedEvents.GetRange(mergedEvents.Count - 3, 3);
        ctx.recentEvents = mergedEvents.ToArray();

        if (newEvents.Count == 0) return;

        if (activeCoroutine != null)
        {
            StopCoroutine(activeCoroutine);
            activeCoroutine = null;
        }

        string triggerReason = string.Join("；", newEvents);
        Debug.Log($"[ADM] {agentProperties?.AgentID} 因感知事件触发重规划: {triggerReason}");
        activeCoroutine = StartCoroutine(RunLLMB(triggerReason));
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
        int start = raw.IndexOf('[');
        int startObj = raw.IndexOf('{');
        if (startObj >= 0 && (start < 0 || startObj < start)) start = startObj;
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
