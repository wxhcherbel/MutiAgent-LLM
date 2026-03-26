// LLM_Modules/PlanningModule.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Diagnostics;
using Debug = UnityEngine.Debug;

// ─── PlanningModule ──────────────────────────────────────────────────────────

/// <summary>
/// 规划模块 v3:4阶段协商协议状态机。
/// 阶段0: LLM#1 解析任务 → 阶段1: 本地分组 → 阶段2: 广播分组 →
/// 阶段3/4: 组长生成槽/广播 → 阶段5: 选槽 → 阶段6: 冲突处理+确认 →
/// 阶段7: LLM#4 拆解步骤 → 阶段8: Active(ActionDecisionModule 消费)
/// </summary>
public class PlanningModule : MonoBehaviour
{
    // ─── 状态机 ──────────────────────────────────────────────
    public PlanningState state = PlanningState.Idle;
    private bool busy;

    // ─── 本轮任务数据 ─────────────────────────────────────────
    private ParsedMission parsed;
    private GroupDef myGroup;
    private bool isLeader;

    // ─── 组长专用 ─────────────────────────────────────────────
    private PlanSlot[] slots;
    private Dictionary<string, string> selections  = new Dictionary<string, string>();
    private Dictionary<string, float>  selectionTs = new Dictionary<string, float>();
    private HashSet<string> occupiedSlots           = new HashSet<string>();

    // ─── 成员专用 ─────────────────────────────────────────────
    private PlanSlot confirmedSlot;
    private bool startExecReceived;

    // ─── 步骤执行 ─────────────────────────────────────────────
    private AgentPlan agentPlan;

    // ─── 结构化约束字典（由 OnGroupBootstrap 填充）────────────
    private Dictionary<string, StructuredConstraint> _constraintDict
        = new Dictionary<string, StructuredConstraint>();

    // ─── 超时 ─────────────────────────────────────────────────
    private float waitStart;
    private const float WaitSec = 25f;

    // ─── 外部依赖 ─────────────────────────────────────────────
    private LLMInterface llm;
    private CommunicationModule comm;
    private AgentProperties props;
    private AgentDynamicState dynState;
    private MemoryModule memory;

    private static int msnCounter;

    void Start()
    {
        llm    = FindObjectOfType<LLMInterface>();
        comm   = GetComponent<CommunicationModule>();
        memory = GetComponent<MemoryModule>();

        IntelligentAgent agent = GetComponent<IntelligentAgent>();
        if (agent != null)
        {
            props    = agent.Properties;
            dynState = agent.CurrentState;
        }
        else
        {
            Debug.LogError("[PlanningModule] 未找到 IntelligentAgent 组件");
        }
    }

    void Update()
    {
        CheckTimeout();
    }
    private static string GetConciseTextPromptText()
    {
        return
            "文本要求:\n" +
            "1. 自然语言字段使用简洁短句。\n" +
            "2. 优先使用“动作+目标+参数”或“动作+目标”。\n" +
            "3. 保留核心动作、目标、参数,表达直接易懂。\n";
    }

    // ─────────────────────────────────────────────────────────
    // 用户入口
    // ─────────────────────────────────────────────────────────

    /// <summary>用户触发任务请求,启动 LLM#1。</summary>
    public void SubmitMissionRequest(string missionDescription, int agentCount)
    {
        if (busy)
        {
            Debug.LogWarning("[PlanningModule] 已有任务请求在处理中");
            return;
        }
        busy = true;
        StartCoroutine(RunLLM1(missionDescription, agentCount));
    }

    // ─────────────────────────────────────────────────────────
    // LLM 协程
    // ─────────────────────────────────────────────────────────

    /// <summary>LLM#1:解析自然语言任务为 ParsedMission。</summary>
    private IEnumerator RunLLM1(string desc, int cnt)
    {
        SetState(PlanningState.Parsing);

        string prompt =
            "你是多智能体任务规划器。请将任务解析为合法 JSON 对象。\n\n" +
            $"任务:{desc}\n" +
            $"智能体数量:{cnt}\n\n" +
            "─── 第一步：判断 relType ───\n" +
            "  Cooperation=同队协作  Competition=多队竞争同目标\n" +
            "  Adversarial=多队目标对立  Mixed=多队各有目标\n" +
            "  Cooperation 时 groupCnt=1；Competition/Adversarial/Mixed 时 groupCnt≥2。\n\n" +

            "─── 第二步：提取协同约束 constraints[] ───\n" +
            "按以下三种类型逐一检查任务描述，凡是任务中出现对应语义均须生成一条约束。\n" +
            "同一任务可能同时包含多种类型，数组可有多条，不得遗漏。\n\n" +
            "每条约束必须包含 groupScope 字段，规则：\n" +
            "  C1/C2/C3（组内协同）→ groupScope = 所属组的序号（单组任务填 0；多组时填 0/1/2...）\n\n" +
            "【C1 资源分配】识别标志：任务中明确指定"谁负责什么目标/区域/资源"，或暗含分工避免重复。\n" +
            "  subject=执行者角色描述（此时不知道 agentId，填角色名如'侦察机'或留空''）\n" +
            "  targetObject=被分配的目标/区域名称  exclusive=是否独占（通常为 true）\n" +
            "  channel=direct\n\n" +
            "【C2 完成同步】识别标志：任务要求多机"都完成后一起…""同步…""统一…"等，强调集体完成再进行下一步。\n" +
            "  condition=同步完成的条件描述\n" +
            "  syncWith=需要等待其写入完成信号的其他 agentId 列表（此阶段不知道 agentId，填 []）\n" +
            "  channel=whiteboard\n\n" +
            "【C3 行为耦合】两种子类型，只在存在明确的等待/互斥关系时才生成，纯并行不需要 C3：\n" +
            "  sign=+1（单向前置等待）：一机必须等另一机到位/就绪后才允许开始行动（非对称依赖）。\n" +
            "    watchAgent=被等待的 agentId（不知道时填 ''）  reactTo='ReadySignal'\n" +
            "  sign=-1（动态互斥）：多 agent 运行时动态争夺同一目标，先到先得，后到者等待。\n" +
            "    不需要提前指定目标名，由 ADM 在决策时从白板读取已占目标。\n" +
            "    与 C1 区别：C1 是静态分配（规划时决定谁用什么），C3-1 是运行时动态抢占（任何一方可先到）。\n" +
            "    watchAgent=竞争方 agentId（不知道时填 ''）  reactTo='IntentAnnounce'\n" +
            "  channel=whiteboard\n\n" +
            GetConciseTextPromptText() + "\n" +

            "─── 输出要求 ───\n" +
            "1. 输出内容仅包含 JSON，所有字符串字段不得为 null（不知道时填空字符串 ''）。\n" +
            "2. 每条约束必须包含字段：constraintId / cType / channel，以及对应类型的专用字段。\n" +
            "3. 不相关类型的专用字段可省略或填默认值（bool=false, int=0, string='', array=[]）。\n" +
            "4. timeLimit 为秒数，无限制时填 0。\n\n" +
            
            "─── 示例（含全部三类约束 + C3 两种子类型）───\n" +
            "输入任务：三架无人机协作侦察。A机负责东区，B机负责西区（两机区域独占不重叠）；" +
            "B机须等A机完成起飞检查发出就绪信号后才允许起飞；" +
            "A和B均完成侦察后同步向指挥部回传坐标；" +
            "A与B侦察途中如需使用同一充电桩，只能一架先用，另一架等待。\n" +
            "{\n" +
            "  \"relType\": \"Cooperation\",\n" +
            "  \"groupCnt\": 1,\n" +
            "  \"groupMsns\": [\"A侦察东区，B侦察西区，完成后同步回传坐标\"],\n" +
            "  \"timeLimit\": 0,\n" +
            "  \"constraints\": [\n" +
            "    {\"constraintId\":\"c1_area_east\",\"cType\":\"C1\",\"channel\":\"direct\",\"groupScope\":0,\"subject\":\"侦察机A\",\"targetObject\":\"东区\",\"exclusive\":true},\n" +
            "    {\"constraintId\":\"c1_area_west\",\"cType\":\"C1\",\"channel\":\"direct\",\"groupScope\":0,\"subject\":\"侦察机B\",\"targetObject\":\"西区\",\"exclusive\":true},\n" +
            "    {\"constraintId\":\"c3_b_wait_a_ready\",\"cType\":\"C3\",\"channel\":\"whiteboard\",\"groupScope\":0,\"sign\":1,\"watchAgent\":\"\",\"reactTo\":\"ReadySignal\"},\n" +
            "    {\"constraintId\":\"c3_charger_mutex\",\"cType\":\"C3\",\"channel\":\"whiteboard\",\"groupScope\":0,\"sign\":-1,\"watchAgent\":\"\",\"reactTo\":\"IntentAnnounce\"},\n" +
            "    {\"constraintId\":\"c2_sync_report\",\"cType\":\"C2\",\"channel\":\"whiteboard\",\"groupScope\":0,\"condition\":\"A和B均完成侦察后同步回传坐标\",\"syncWith\":[]}\n" +
            "  ]\n" +
            "}\n\n" +
            "注意：\n" +
            "· c3_b_wait_a_ready (sign=+1)：B单向等待A就绪，watchAgent 填 '' 因运行时才知道 agentId。\n" +
            "· c3_charger_mutex (sign=-1)：动态互斥，无需填 mutexTarget，被占目标由白板运行时记录（ADM 写 IntentAnnounce.progress）。\n" +
            "· c2_sync_report 中 syncWith 填 []，由运行时确定等待对象。";

        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(prompt, r => llmResult = r, maxTokens: 1200));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError("[PlanningModule] LLM#1 返回空");
            SetState(PlanningState.Failed);
            busy = false;
            yield break;
        }
        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#1 任务解析原始回复: {llmResult}");
        ParsedMission p = null;
        try
        {
            p = JsonConvert.DeserializeObject<ParsedMission>(ExtractJson(llmResult));
            Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#1 任务解析结果: relType={p.relType}, groupCnt={p.groupCnt}, timeLimit={p.timeLimit}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[PlanningModule] LLM#1 JSON解析失败: {e.Message}\n原文: {llmResult}");
            SetState(PlanningState.Failed);
            busy = false;
            yield break;
        }

        p.msnId = GenMsnId();
        parsed  = p;

        string[] agentIds = CommunicationManager.Instance != null
            ? CommunicationManager.Instance.GetAllAgentIds()
            : new[] { props.AgentID };

        AssignGroups(p, agentIds);
    }

    /// <summary>LLM#2(组长):根据组任务和成员数生成计划槽列表。</summary>
    private IEnumerator RunLLM2()
    {
        SetState(PlanningState.SlotGen);
        int memberCount = myGroup.memberIds.Length;
        string roleTypes = string.Join("、", Enum.GetNames(typeof(RoleType)));

        // 分类整理约束：C1（分配类）单独提取，其余约束以 ID 列表提供
        var c1Constraints = new List<StructuredConstraint>();
        var otherConstraintIds = new List<string>();
        foreach (var kv in _constraintDict)
        {
            if (kv.Value.cType == "C1" || kv.Value.cType == "Assignment")
                c1Constraints.Add(kv.Value);
            else
                otherConstraintIds.Add(kv.Key);
        }
        string c1Json = c1Constraints.Count > 0
            ? JsonConvert.SerializeObject(c1Constraints)
            : "[]";
        string otherIds = otherConstraintIds.Count > 0
            ? string.Join("、", otherConstraintIds)
            : "（无）";

        string prompt =
            "你是多智能体任务组长。为本组生成计划槽 JSON 数组。\n\n" +
            $"组任务:{myGroup.mission}\n" +
            $"成员数:{memberCount}\n\n" +
            "─── C1 资源分配约束（生成 desc 时必须遵守）───\n" +
            $"{c1Json}\n" +
            "说明：若某条 C1 约束的 subject 非空，对应槽的 desc 必须承担该 subject 指定的 targetObject 任务。\n" +
            "      若 subject 为空（任务中未指定具体执行者），则由你自行合理分配，但每个 targetObject 只能分配给一个槽。\n\n" +
            "─── 其他约束 ID（constraintIds 字段引用用）───\n" +
            $"{otherIds}\n\n" +
            GetConciseTextPromptText() + "\n" +
            "─── 输出要求 ───\n" +
            $"1. 只输出 JSON 数组，长度严格等于 {memberCount}。\n" +
            "2. slotId 唯一，格式 s0、s1 ...\n" +
            $"3. role 从以下枚举选择:{roleTypes}\n" +
            "4. desc 覆盖该成员的完整任务序列（含途径点 + 全部动作），不得遗漏。\n" +
            "5. 若多个成员 role 相同，每个 desc 必须体现各自的具体分工（不同区域/路径/目标），不得完全一致。\n" +
            "6. doneCond：完成条件，没有时填 \" \"。\n" +
            "7. desc 只描述本成员自己的行动，不描述与队友的协同关系。\n" +
            "8. constraintIds：字符串数组，从 C1 约束的 constraintId 和其他约束ID中，选择适用于本槽的填入；若无则填 []。\n\n" +
            "─── 示例（C1 约束指定了分区，C2 约束要求同步）───\n" +
            "C1约束:[{\"constraintId\":\"c1_area_a\",\"subject\":\"侦察机A\",\"targetObject\":\"南区\"},{\"constraintId\":\"c1_area_b\",\"subject\":\"侦察机B\",\"targetObject\":\"北区\"}]\n" +
            "其他约束ID:c2_sync_report\n" +
            "[\n" +
            "  {\"slotId\":\"s0\",\"role\":\"Scout\",\"desc\":\"从出发点飞往南区执行搜索\",\"doneCond\":\"南区搜索完成\",\"constraintIds\":[\"c1_area_a\",\"c2_sync_report\"]},\n" +
            "  {\"slotId\":\"s1\",\"role\":\"Scout\",\"desc\":\"从出发点飞往北区执行搜索\",\"doneCond\":\"北区搜索完成\",\"constraintIds\":[\"c1_area_b\",\"c2_sync_report\"]}\n" +
            "]\n" +
            "注意：s0 的 desc 体现了 c1_area_a 规定的南区分配；s1 的 desc 体现了 c1_area_b 规定的北区分配。";
        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(prompt, r => llmResult = r, maxTokens: 1000));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError("[PlanningModule] LLM#2 返回空");
            busy = false; // BUG-03 修复：失败路径必须重置 busy，否则后续任务无法提交
            SetState(PlanningState.Failed);
            yield break;
        }
        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#2 生成槽原始回复: {llmResult}");
        PlanSlot[] generatedSlots = null;
        try
        {
            generatedSlots = JsonConvert.DeserializeObject<PlanSlot[]>(ExtractJson(llmResult));
            Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#2 生成槽位: {string.Join(", ", generatedSlots.Select(s => s.slotId + ":" + s.role + ":" + s.desc + ":constraintIds=[" + string.Join(",", s.constraintIds ?? new string[0]) + "]"))}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[PlanningModule] LLM#2 JSON解析失败: {e.Message}");
            busy = false; // BUG-03 修复
            SetState(PlanningState.Failed);
            yield break;
        }

        slots = generatedSlots;

        // 若组内只有自己,直接跳过广播+选槽,本地分配唯一槽
        if (memberCount == 1)
        {
            confirmedSlot = slots[0];
            occupiedSlots.Add(slots[0].slotId);
            OnStartExec(new StartExecPayload { msnId = parsed.msnId, groupId = myGroup.groupId });
            yield break;
        }

        // 广播槽列表给组内所有成员(含自身)
        SlotBroadcastPayload broadcastPayload = new SlotBroadcastPayload
        {
            msnId    = parsed.msnId,
            groupId  = myGroup.groupId,
            leaderId = props.AgentID,
            slots    = slots
        };

        foreach (string memberId in myGroup.memberIds)
        { 
            if (string.Equals(memberId, props.AgentID, StringComparison.OrdinalIgnoreCase))
                continue; // 组长跳过自身,下方直接调用 RunLLM3
            comm.SendScopedMessage(
                CommunicationScope.DirectAgent,
                MessageType.SlotBroadcast,
                broadcastPayload,
                targetAgentId: memberId,
                reliable: true);
        }

        // 组长自身也调用 LLM#3
        StartCoroutine(RunLLM3(slots));
    }

    /// <summary>LLM#3(全员):从槽列表中选出最适合自身的槽。</summary>
    private IEnumerator RunLLM3(PlanSlot[] availableSlots)
    {
        SetState(PlanningState.SlotPick);

        string slotsJson = JsonConvert.SerializeObject(availableSlots);
        Vector3 pos      = dynState != null ? dynState.Position : transform.position;
        float battery    = dynState != null ? dynState.BatteryLevel : 100f;

        string prompt =
            "你是无人机智能体。请从可选槽中选择最适合自己的一个,并输出合法 JSON 对象。\n\n" +
            $"可选槽:{slotsJson}\n" +
            $"当前电量:{battery:F0}%,当前位置:{pos}\n\n" +
            GetConciseTextPromptText() + "\n" +
            "输出要求:\n" +
            "1. 输出内容仅包含 JSON。\n" +
            "2. `slotId` 是可选槽中的一个。\n\n" +
            "输出格式示例:\n" +
            "{\n" +
            "  \"slotId\": \"s0\"\n" +
            "}";

        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(prompt, r => llmResult = r, maxTokens: 150));

        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#3 原始回复: {llmResult}");


        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#3 返回空,选第一个槽");
            llmResult = $"{{\"slotId\":\"{availableSlots[0].slotId}\"}}";
        }

        SlotSelectPayload selectPayload = null;
        try
        {
            var jobj = JsonConvert.DeserializeObject<Dictionary<string, string>>(ExtractJson(llmResult));
            selectPayload = new SlotSelectPayload
            {
                msnId   = parsed.msnId,
                agentId = props.AgentID,
                slotId  = jobj.ContainsKey("slotId") ? jobj["slotId"] : availableSlots[0].slotId
            };
        }
        catch (Exception e)
        {
            Debug.LogError($"[PlanningModule] LLM#3 JSON解析失败: {e.Message}");
            selectPayload = new SlotSelectPayload
            {
                msnId   = parsed.msnId,
                agentId = props.AgentID,
                slotId  = availableSlots[0].slotId
            };
        }

        if (isLeader)
        {
            // 组长自身选择本地处理(先到先得)
            OnSlotSelect(selectPayload);
        }
        else
        {
            comm.SendScopedMessage(
                CommunicationScope.DirectAgent,
                MessageType.SlotSelect,
                selectPayload,
                targetAgentId: myGroup.leaderId,
                reliable: true);
        }
    }

    /// <summary>LLM#4(全员):将确认的计划槽拆解为有序步骤。</summary>
    private IEnumerator RunLLM4()
    {
        SetState(PlanningState.StepGen);

        Vector3 pos   = dynState != null ? dynState.Position : transform.position;
        float battery = dynState != null ? dynState.BatteryLevel : 100f;

        // 收集槽位关联的约束对象，供 LLM#4 知晓可引用的约束 ID
        var slotConstraints = new List<StructuredConstraint>();
        if (confirmedSlot.constraintIds != null)
        {
            foreach (var cid in confirmedSlot.constraintIds)
            {
                var c = GetConstraint(cid);
                if (c != null) slotConstraints.Add(c);
            }
        }
        string constraintsJson = JsonConvert.SerializeObject(slotConstraints);

        string prompt =
            "你是无人机任务拆解器。将计划拆成 JSON 步骤数组。\n\n" +
            $"计划:{confirmedSlot.desc}\n" +
            $"角色:{confirmedSlot.role}\n" +
            $"可用约束列表（StructuredConstraint JSON）:{constraintsJson}\n" +
            $"完成条件:{confirmedSlot.doneCond}\n" +
            $"当前位置:{pos},电量:{battery:F0}%\n\n" +
            "要求:\n" +
            "1. 只输出 JSON 数组。\n" +
            "2. 只拆解 desc 中明确出现的动作，禁止补充推断步骤。\n" +
            "3. text 只能是意图动作（移动/巡逻/侦察/等待等）。\n" +
            "4. desc 只含一个动作时，输出 1 步。\n" +
            "5. doneCond：完成条件，没有时填 \" \"。\n" +
            "6. stepId 格式：step_1、step_2 ...\n" +
            "7. constraintIds：按以下规则将约束分配到对应步骤：\n" +
            "   C1（资源分配）→ 分配给第一个进入/使用 targetObject 的步骤\n" +
            "   C2（完成同步）→ 分配给最后一个实质性动作步骤（即触发同步的步骤）\n" +
            "   C3 sign=+1（单向等待）→ 分配给第一步（出发前等待前置条件就绪）\n" +
            "   C3 sign=-1（动态互斥）→ 分配给可能发生目标竞争的动作步骤（进入共享区域/资源的步骤）\n" +
            "8. 同一条约束只分配给一个步骤，不重复绑定。\n\n" +
            "示例(移动→巡逻，含 C3+C2 约束):\n" +
            "desc:\"等A就绪后从出发点飞往b，到达后顺时针巡逻一周再回传\"\n" +
            "可用约束: c3_wait_a(C3), c2_sync_report(C2)\n" +
            "[\n" +
            "  {\"stepId\":\"step_1\",\"text\":\"等A就绪后飞往b\",\"doneCond\":\" \",\"constraintIds\":[\"c3_wait_a\"]},\n" +
            "  {\"stepId\":\"step_2\",\"text\":\"顺时针巡逻一周\",\"doneCond\":\"巡逻完成\",\"constraintIds\":[]},\n" +
            "  {\"stepId\":\"step_3\",\"text\":\"回传坐标\",\"doneCond\":\"回传完成\",\"constraintIds\":[\"c2_sync_report\"]}\n" +
            "]\n\n";

        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(prompt, r => llmResult = r, maxTokens: 800));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError("[PlanningModule] LLM#4 返回空");
            busy = false; // BUG-03 修复：失败路径必须重置 busy
            SetState(PlanningState.Failed);
            yield break;
        }
        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#4 原始回复: {llmResult}");
        PlanStep[] steps = null;
        try
        {
            steps = JsonConvert.DeserializeObject<PlanStep[]>(ExtractJson(llmResult));
            Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#4 生成步骤: {string.Join(", ", steps.Select(s => s.stepId + ":" + s.text))}");
        }
        catch (Exception e)
        {
            Debug.LogError($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#4 JSON解析失败: {e.Message}");
            busy = false; // BUG-03 修复
            SetState(PlanningState.Failed);
            yield break;
        }

        agentPlan = new AgentPlan
        {
            msnId  = parsed.msnId,
            slotId = confirmedSlot.slotId,
            role   = confirmedSlot.role,
            desc   = confirmedSlot.desc,
            steps  = steps,
            curIdx = 0
        };

        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] {props.AgentID} 计划就绪,共 {steps.Length} 步");
        SetState(PlanningState.Active);
        if (parsed != null && parsed.timeLimit > 0f)
            StartCoroutine(TimeLimitCoroutine(parsed.timeLimit));
    }

    // ─────────────────────────────────────────────────────────
    // 分组逻辑(协调者本地执行)
    // ─────────────────────────────────────────────────────────

    /// <summary>按 relType 和 groupCnt 将 agentIds 分配到各组,选组长,广播 GroupBootstrap。</summary>
    private void AssignGroups(ParsedMission p, string[] agentIds)
    {
        SetState(PlanningState.Grouping);

        int n        = agentIds.Length;
        int groupCnt = Mathf.Max(1, p.groupCnt);
        GroupDef[] groups = new GroupDef[groupCnt];

        int baseSize  = n / groupCnt;
        int remainder = n % groupCnt;
        int idx = 0;

        for (int g = 0; g < groupCnt; g++)
        {
            int size    = baseSize + (g == 0 ? remainder : 0);
            string[] members = agentIds.Skip(idx).Take(size).ToArray();
            idx += size;

            string mission = (p.groupMsns != null && g < p.groupMsns.Length)
                ? p.groupMsns[g]
                : $"子任务{g}";

            groups[g] = new GroupDef
            {
                groupId   = $"g{g}",
                mission   = mission,
                leaderId  = SelectLeader(members),
                memberIds = members
            };
        }

        StructuredConstraint[] allConstraints = p.constraints ?? new StructuredConstraint[0];

        // 每组只收到：全局约束（groupScope==-1）+ 本组约束（groupScope==groupIndex）
        // 避免在对抗/竞争场景下各组互相看到对方的内部协同约束
        for (int g = 0; g < groupCnt; g++)
        {
            var groupConstraints = System.Array.FindAll(allConstraints,
                c => c.groupScope < 0 || c.groupScope == g);

            GroupBootstrapPayload bootstrap = new GroupBootstrapPayload
            {
                msnId       = p.msnId,
                relType     = p.relType,
                groups      = groups,
                constraints = groupConstraints
            };

            foreach (string memberId in groups[g].memberIds)
            {
                comm.SendScopedMessage(
                    CommunicationScope.DirectAgent,
                    MessageType.GroupBootstrap,
                    bootstrap,
                    targetAgentId: memberId,
                    reliable: true);
            }
        }
    }

    /// <summary>从 memberIds 中选出当前电量最高的 AgentID 作为组长。</summary>
    private string SelectLeader(string[] memberIds)
    {
        string leaderId  = memberIds[0];
        float maxBattery = float.MinValue;

        foreach (string id in memberIds)
        {
            CommunicationModule mod = CommunicationManager.Instance?.GetAgentModule(id);
            if (mod == null) continue;
            IntelligentAgent agent = mod.GetComponent<IntelligentAgent>();
            float bat = agent?.CurrentState?.BatteryLevel ?? 0f;
            if (bat > maxBattery)
            {
                maxBattery = bat;
                leaderId   = id;
            }
        }

        return leaderId;
    }

    // ─────────────────────────────────────────────────────────
    // 消息接收(由 CommunicationModule 转发调用)
    // ─────────────────────────────────────────────────────────

    /// <summary>收到分组通知,提取本组信息,分支进入组长或成员路径。</summary>
    public void OnGroupBootstrap(GroupBootstrapPayload p)
    {
        // 非协调者(parsed==null)直接接受并采用消息中的 msnId；
        // 协调者则验证 msnId 是否匹配,防止跨任务消息干扰。
        if (parsed == null)
            parsed = new ParsedMission { msnId = p.msnId };
        else if (p.msnId != parsed.msnId)
            return;

        for (int groupIndex = 0; groupIndex < p.groups.Length; groupIndex++)
        {
            GroupDef g = p.groups[groupIndex];
            if (g.memberIds == null) continue;
            foreach (string id in g.memberIds)
            {
                if (!string.Equals(id, props.AgentID, StringComparison.OrdinalIgnoreCase)) continue;
                myGroup  = g;
                isLeader = string.Equals(g.leaderId, props.AgentID, StringComparison.OrdinalIgnoreCase);
                // Team scope reads AgentProperties.TeamID, so sync the runtime group index here.
                props.TeamID = groupIndex;
                break;
            }
            if (myGroup != null) break;
        }

        if (myGroup == null)
        {
            Debug.LogWarning($"[PlanningModule] {props.AgentID} 未找到所属组");
            return;
        }

        // 将全量约束存入本地字典，供 GetConstraint 查询
        _constraintDict.Clear();
        if (p.constraints != null)
        {
            foreach (var c in p.constraints)
            {
                if (c != null && !string.IsNullOrWhiteSpace(c.constraintId))
                    _constraintDict[c.constraintId] = c;
            }
        }

        Debug.Log($"[PlanningModule] {props.AgentID} 加入组 {myGroup.groupId},isLeader={isLeader}," +
                  $"已加载 {_constraintDict.Count} 条结构化约束");

        if (isLeader)
            StartCoroutine(RunLLM2());
        // 成员等待 SlotBroadcast
    }

    /// <summary>收到槽列表广播,启动 LLM#3 选槽。</summary>
    public void OnSlotBroadcast(SlotBroadcastPayload p)
    {
        if (parsed == null || p.msnId != parsed.msnId) return;
        StartCoroutine(RunLLM3(p.slots));
    }

    /// <summary>组长收到成员的槽选择,记录并检查是否收齐。</summary>
    public void OnSlotSelect(SlotSelectPayload p)
    {
        if (!isLeader || parsed == null || p.msnId != parsed.msnId) return;

        if (!selections.ContainsKey(p.agentId))
        {
            selections[p.agentId] = p.slotId;
            selectionTs[p.agentId] = Time.time;
        }

        bool allReceived = myGroup.memberIds.All(id => selections.ContainsKey(id));
        if (allReceived)
            ResolveAndConfirm();
    }

    /// <summary>成员收到槽确认,记录 confirmedSlot,等待 StartExec。</summary>
    public void OnSlotConfirm(SlotConfirmPayload p)
    {
        if (parsed == null || p.msnId != parsed.msnId) return;
        if (!string.Equals(p.agentId, props.AgentID, StringComparison.OrdinalIgnoreCase)) return;

        confirmedSlot = p.slot;
        Debug.Log($"[PlanningModule] {props.AgentID} 确认槽 {p.slot.slotId}");

        if (startExecReceived)
            StartCoroutine(RunLLM4());
    }

    /// <summary>收到开始执行信号,启动 LLM#4 拆解步骤。</summary>
    public void OnStartExec(StartExecPayload p)
    {
        if (parsed == null || p.msnId != parsed.msnId) return;

        startExecReceived = true;

        if (confirmedSlot != null)
            StartCoroutine(RunLLM4());
        // 否则等 SlotConfirm 到达后触发
    }

    // ─────────────────────────────────────────────────────────
    // 组长专用:分配确认
    // ─────────────────────────────────────────────────────────

    /// <summary>所有成员选择收齐后做唯一分配,按到达时间先到先得,重复选择者分配剩余槽。</summary>
    private void ResolveAndConfirm()
    {
        // 组长自身排最前,其余按到达时间排序
        List<string> ordered = myGroup.memberIds
            .OrderBy(id => selectionTs.ContainsKey(id) ? selectionTs[id] : float.MaxValue)
            .ToList();

        ordered.Remove(props.AgentID);
        ordered.Insert(0, props.AgentID);

        List<PlanSlot> remaining = new List<PlanSlot>(slots);

        foreach (string agentId in ordered)
        {
            if (!selections.TryGetValue(agentId, out string wantedSlotId)) continue;

            PlanSlot wanted   = remaining.Find(s => s.slotId == wantedSlotId);
            PlanSlot assigned;

            if (wanted != null)
            {
                assigned  = wanted;
            }
            else
            {
                if (remaining.Count == 0)
                {
                    Debug.LogWarning($"{props?.AgentID ?? "Unknown"}: [PlanningModule] 没有剩余槽可分配给 {agentId}");
                    continue;
                }
                assigned  = remaining[0];
                Debug.Log($"[PlanningModule] {agentId} 选择的槽 {wantedSlotId} 已被占用，改分配 {assigned.slotId}");
            }

            remaining.Remove(assigned);
            occupiedSlots.Add(assigned.slotId);

            if (string.Equals(agentId, props.AgentID, StringComparison.OrdinalIgnoreCase))
            {
                confirmedSlot = assigned;
                Debug.Log($"[PlanningModule] {props.AgentID}(组长)确认槽 {assigned.slotId}");
            }
            else
            {
                comm.SendScopedMessage(
                    CommunicationScope.DirectAgent,
                    MessageType.SlotConfirm,
                    new SlotConfirmPayload
                    {
                        msnId     = parsed.msnId,
                        agentId   = agentId,
                        slot      = assigned
                    },
                    targetAgentId: agentId,
                    reliable: true);
            }
        }

        // 广播 StartExecution 给组内成员(不含自身)
        StartExecPayload startExec = new StartExecPayload
        {
            msnId   = parsed.msnId,
            groupId = myGroup.groupId
        };

        foreach (string memberId in myGroup.memberIds)
        {
            if (string.Equals(memberId, props.AgentID, StringComparison.OrdinalIgnoreCase)) continue;
            comm.SendScopedMessage(
                CommunicationScope.DirectAgent,
                MessageType.StartExecution,
                startExec,
                targetAgentId: memberId,
                reliable: true);
        }

        // 组长自身触发 LLM#4
        OnStartExec(startExec);
    }

    // ─────────────────────────────────────────────────────────
    // 状态管理
    // ─────────────────────────────────────────────────────────

    private void SetState(PlanningState s)
    {
        state     = s;
        waitStart = Time.time;
        // BUG-M4: Failed/Done 时自动释放 busy 锁，防止 PlanningModule 卡死
        if (s == PlanningState.Failed || s == PlanningState.Done) busy = false;
        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] {props?.AgentID} → {s}");
    }

    private void CheckTimeout()
    {
        if (state == PlanningState.Idle   ||
            state == PlanningState.Active ||
            state == PlanningState.Done   ||
            state == PlanningState.Failed) return;

        if (Time.time - waitStart > WaitSec)
        {
            Debug.LogWarning($"{props?.AgentID ?? "Unknown"}: [PlanningModule] {props?.AgentID} 等待超时,状态={state}");
            SetState(PlanningState.Failed);
            busy = false;
        }
    }

    // ─────────────────────────────────────────────────────────
    // ActionDecisionModule 对外接口
    // ─────────────────────────────────────────────────────────

    /// <summary>state==Active 且 agentPlan 非空时返回 true。</summary>
    public bool HasActiveMission()
    {
        return state == PlanningState.Active &&
               agentPlan != null &&
               agentPlan.steps != null &&
               agentPlan.steps.Length > 0;
    }

    /// <summary>返回 agentPlan.steps[agentPlan.curIdx],即当前步骤。</summary>
    public PlanStep GetCurrentStep()
    {
        if (!HasActiveMission()) return null;
        if (agentPlan.curIdx >= agentPlan.steps.Length) return null;
        return agentPlan.steps[agentPlan.curIdx];
    }

    /// <summary>返回当前任务 ID，供执行层和监控层读取。</summary>
    public string GetCurrentMissionId()
    {
        return parsed?.msnId ?? agentPlan?.msnId ?? string.Empty;
    }

    /// <summary>返回当前任务描述，优先使用已确认槽位的描述。</summary>
    public string GetCurrentMissionDescription()
    {
        if (!string.IsNullOrWhiteSpace(agentPlan?.desc)) return agentPlan.desc;
        if (!string.IsNullOrWhiteSpace(confirmedSlot?.desc)) return confirmedSlot.desc;
        if (!string.IsNullOrWhiteSpace(myGroup?.mission)) return myGroup.mission;
        return string.Empty;
    }

    /// <summary>
    /// 通过约束 ID 查询完整的结构化约束对象（由 OnGroupBootstrap 填充）。
    /// 若未找到则返回 null。
    /// </summary>
    public StructuredConstraint GetConstraint(string constraintId)
    {
        if (string.IsNullOrWhiteSpace(constraintId)) return null;
        _constraintDict.TryGetValue(constraintId, out var c);
        return c;
    }

    /// <summary>返回本 Agent 所属组的组 ID（供白板读写使用）。</summary>
    public string GetGroupId() => myGroup?.groupId ?? string.Empty;

    /// <summary>完成当前步骤:curIdx++,若超出数组长度则转 Done。</summary>
    public void CompleteCurrentStep()
    {
        if (agentPlan == null || agentPlan.steps == null) return;
        agentPlan.curIdx++;
        if (agentPlan.curIdx >= agentPlan.steps.Length)
        {
            Debug.Log($"[PlanningModule] {props?.AgentID} 所有步骤完成 → Done");
            SetState(PlanningState.Done);
            busy = false;
        }
    }

    private IEnumerator TimeLimitCoroutine(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (state == PlanningState.Active)
        {
            Debug.LogWarning($"[Planning] 任务 {parsed?.msnId} 时间限制 {seconds}s 到达，强制终止。");
            state = PlanningState.Failed;
            busy = false;
        }
    }
    // ─────────────────────────────────────────────────────────
    // 工具方法
    // ─────────────────────────────────────────────────────────

    /// <summary>从 LLM 回复字符串中提取 JSON(去除 ```json...``` 包裹)。</summary>
    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        Match m = Regex.Match(raw, @"```(?:json)?\s*([\s\S]*?)```");
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

    /// <summary>生成任务ID,格式 "msn_yyyyMMdd_N"。</summary>
    private static string GenMsnId()
    {
        return $"msn_{DateTime.Now:yyyyMMdd}_{++msnCounter}";
    }
}
