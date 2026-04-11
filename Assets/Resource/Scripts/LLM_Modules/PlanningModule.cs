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
    private GroupDef[] allGroups;
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

    // ─── 结构化约束字典(由 OnGroupBootstrap 填充)────────────
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
            "─── 第一步:判断 relType ───\n" +
            "  Cooperation=同队协作  Competition=多队竞争同目标\n" +
            "  Adversarial=多队目标对立  Mixed=多队各有目标\n" +
            "  Cooperation 时 groupCnt=1;Competition/Adversarial/Mixed 时 groupCnt≥2。\n\n" +

            "─── 第二步:提取协同约束 constraints[] ───\n" +
            "按以下三种类型逐一检查任务描述,凡是任务中出现对应语义均须生成一条约束。\n" +
            "同一任务可能同时包含多种类型,数组可有多条,不得遗漏。\n\n" +
            "每条约束必须包含 groupScope 字段,规则:\n" +
            "  C1/C2/C3(组内协同)→ groupScope = 所属组的序号(单组任务填 0;多组时填 0/1/2...)\n\n" +
            "【C1 资源分配】识别标志:任务中明确指定\" 谁负责什么目标/区域/资源\",或暗含分工避免重复。\n" +
            "  subject=执行者角色描述(此时不知道 agentId,填角色名如'侦察机'或留空'')\n" +
            "  targetObject=被分配的目标/区域名称  exclusive=是否独占(通常为 true)\n" +
            "  channel=direct\n\n" +
            "【C2 完成同步】识别标志:任务要求多机\"都完成后一起…\"\"同步…\"\"统一…\"等,强调集体完成再进行下一步。\n" +
            "  condition=同步完成的条件描述\n" +
            "  syncWith=需要等待其写入完成信号的其他 agentId 列表(此阶段不知道 agentId,填 [])\n" +
            "  channel=whiteboard\n\n" +
            "【C3 行为耦合】两种子类型,只在存在明确的等待/互斥关系时才生成,纯并行不需要 C3:\n" +
            "  sign=+1(单向前置等待):一机必须等另一机到位/就绪后才允许开始行动(非对称依赖)。\n" +
            "    watchAgent=被等待的 agentId(不知道时填 '')  reactTo='ReadySignal'\n" +
            "  sign=-1(动态互斥):满足以下全部条件时生成:\n" +
            "    ① 存在多个 agent 和一个同类目标集合（如多个检查点、路径节点、共享设施等）\n" +
            "    ② 每个具体目标同一时刻只允许一个 agent 占用/前往\n" +
            "    ③ 具体哪个 agent 去哪个目标,规划时无法静态确定（若已确定则用 C1）\n" +
            "    不需要提前指定目标名,由 ADM 在运行时通过白板读取已占目标后动态选择未占目标。\n" +
            "    参与互斥的成员由绑定到同一 constraintId 的槽位共同决定。\n" +
            "    watchAgent 保持空字符串 ''  reactTo='IntentAnnounce'\n" +
            "  【重要】C3-1 是组内协同机制:参与互斥的 agent 仍属同一协作团队,relType=Cooperation；\n" +
            "         互斥/避让行为不代表对抗,不应影响 relType 的判断。\n" +
            "  channel=whiteboard\n\n" +
            GetConciseTextPromptText() + "\n" +
            "─── 输出前检查（必做）───\n" +
            "逐类型扫描任务描述:\n" +
            "· C1:有没有「谁负责什么目标/区域」？\n" +
            "· C2:有没有「都完成后一起/同步」？\n" +
            "· C3-1:是否存在目标集合、且每个目标只允许一人占用、且具体分配规划时不确定？→ sign=-1\n" +
            "· C3+1:是否有一机必须等另一机发出就绪信号后才能行动（单向前置依赖）？→ sign=+1\n" +
            "确认无遗漏后再输出 JSON。\n\n" +

            "─── 输出要求 ───\n" +
            "1. 输出内容仅包含 JSON,所有字符串字段不得为 null(不知道时填空字符串 '')。\n" +
            "2. 每条约束必须包含字段:constraintId / cType / channel,以及对应类型的专用字段。\n" +
            "3. 不相关类型的专用字段可省略或填默认值(bool=false, int=0, string='', array=[])。\n" +
            "4. timeLimit 为秒数,无限制时填 0。\n\n" +
            
            "─── 示例(含全部三类约束 + C3 两种子类型)───\n" +
            "输入任务:三架无人机协作侦察。A机负责东区,B机负责西区(两机区域独占不重叠);" +
            "B机须等A机完成起飞检查发出就绪信号后才允许起飞;" +
            "A和B均完成侦察后同步向指挥部回传坐标;" +
            "A与B侦察途中如需使用同一充电桩,只能一架先用,另一架等待。\n" +
            "{\n" +
            "  \"relType\": \"Cooperation\",\n" +
            "  \"groupCnt\": 1,\n" +
            "  \"groupMsns\": [\"A侦察东区,B侦察西区,完成后同步回传坐标\"],\n" +
            "  \"timeLimit\": 0,\n" +
            "  \"constraints\": [\n" +
            "    {\"constraintId\":\"c1_area_east\",\"cType\":\"C1\",\"channel\":\"direct\",\"groupScope\":0,\"subject\":\"侦察机A\",\"targetObject\":\"东区\",\"exclusive\":true},\n" +
            "    {\"constraintId\":\"c1_area_west\",\"cType\":\"C1\",\"channel\":\"direct\",\"groupScope\":0,\"subject\":\"侦察机B\",\"targetObject\":\"西区\",\"exclusive\":true},\n" +
            "    {\"constraintId\":\"c3_b_wait_a_ready\",\"cType\":\"C3\",\"channel\":\"whiteboard\",\"groupScope\":0,\"sign\":1,\"watchAgent\":\"\",\"reactTo\":\"ReadySignal\"},\n" +
            "    {\"constraintId\":\"c3_charger_mutex\",\"cType\":\"C3\",\"channel\":\"whiteboard\",\"groupScope\":0,\"sign\":-1,\"watchAgent\":\"\",\"reactTo\":\"IntentAnnounce\"},\n" +
            "    {\"constraintId\":\"c2_sync_report\",\"cType\":\"C2\",\"channel\":\"whiteboard\",\"groupScope\":0,\"condition\":\"A和B均完成侦察后同步回传坐标\",\"syncWith\":[]}\n" +
            "  ]\n" +
            "}\n\n" +
            "";

        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(
            new LLMRequestOptions { prompt = prompt, maxTokens = 1200, enableJsonMode = true, callTag = "LLM#1_Parse", agentId = props?.AgentID },
            r => llmResult = r));

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

        StructuredConstraint[] visibleConstraints = ExportConstraints();
        string constraintsJson = visibleConstraints.Length > 0
            ? JsonConvert.SerializeObject(visibleConstraints)
            : "[]";

        string prompt =
            "你是多智能体任务组长。\n" +
            "请做两件事:\n" +
            "1. 为本组生成计划槽 slots。\n" +
            "2. 回写约束 constraints 中的槽位引用。\n\n" +
            $"组任务:{myGroup.mission}\n" +
            $"成员数:{memberCount}\n" +
            $"可选角色:{roleTypes}\n\n" +
            "输入约束(StructuredConstraint JSON):\n" +
            $"{constraintsJson}\n\n" +
            "【Slots 规则】\n" +
            "1. 输出一个 JSON 对象,包含 slots 和 constraints。\n" +
            $"2. slots 数量必须等于 {memberCount},slotId 用 s0、s1、s2 ...\n" +
            "3. desc 只写该成员自己的行动,须覆盖完整分工,不要漏动作。\n" +
            "4. 相同 role 的多个槽,desc 也要体现不同分工。\n" +
            "4b. desc 禁止出现任务描述中未明确命名的目标点/对象。\n" +
            "    若某目标由运行时动态决定（C3-1 约束涉及的目标）,不要在 desc 中提及或命名该中间目标,\n" +
            "    反例（禁止）:「飞往检查点A,再前往艺术中心」（任务未命名「检查点A」）\n" +
            "    正例（允许）:「飞往艺术中心,到达后参与巡逻」\n" +
            "5. doneCond 没有时填 \" \";constraintIds 没有就填 []。\n\n" +
            "【Constraints 回写规则】\n" +
            "6. 必须保留输入中的每一条 constraintId,不新增,不删除。\n" +
            "7. C1:把对应 constraintId 绑定到负责该资源/区域的槽位。\n" +
            "8. C2:syncWith 填参与同步的 slotId 数组,可包含自己。\n" +
            "9. 除 watchAgent 和 syncWith 外,其他字段保持原语义;无法判断时才允许留空。\n\n" +
            "【C3 绑定规则】\n" +
            "sign=+1(单向等待):\n" +
            "  · 同一 constraintId 同时绑定到「等待方」槽位和「被等待方」槽位。\n" +
            "  · watchAgent 改写为被等待方的 slotId。\n" +
            "sign=-1(动态互斥):\n" +
            "  · 同一 constraintId 绑定到所有可能竞争该共享资源/地点/区域/路径的槽位。\n" +
            "  · watchAgent 保持空字符串,参与者由绑定范围共同决定。\n\n" +
            GetConciseTextPromptText() + "\n" +
            "输出格式:\n" +
            "{\n" +
            "  \"slots\": [PlanSlot, ...],\n" +
            "  \"constraints\": [StructuredConstraint, ...]\n" +
            "}\n\n" +
            "示例:\n" +
            "任务描述:两架无人机协作执行任务。侦察机A前往南区搜索并发出就绪信号,侦察机B收到A的就绪信号后前往北区搜索;若两机都需要使用共享充电点,谁先到谁先使用;两者完成后同步回传。\n" +
            "输入 constraints:\n" +
            "[" +
            "{\"constraintId\":\"c1_area_a\",\"cType\":\"C1\",\"subject\":\"侦察机A\",\"targetObject\":\"南区\"}," +
            "{\"constraintId\":\"c2_sync_report\",\"cType\":\"C2\",\"channel\":\"whiteboard\",\"condition\":\"南北区都完成后同步回传\",\"syncWith\":[]}," +
            "{\"constraintId\":\"c3_wait_cover\",\"cType\":\"C3\",\"channel\":\"whiteboard\",\"sign\":1,\"watchAgent\":\"\",\"reactTo\":\"ReadySignal\"}," +
            "{\"constraintId\":\"c3_charge_mutex\",\"cType\":\"C3\",\"channel\":\"whiteboard\",\"sign\":-1,\"watchAgent\":\"\",\"reactTo\":\"IntentAnnounce\"}" +
            "]\n" +
            "输出:\n" +
            "{\n" +
            "  \"slots\": [\n" +
            "    {\"slotId\":\"s0\",\"role\":\"Scout\",\"desc\":\"飞往南区执行搜索,需要时前往共享充电点补能\",\"doneCond\":\"南区搜索完成\",\"constraintIds\":[\"c1_area_a\",\"c2_sync_report\",\"c3_wait_cover\",\"c3_charge_mutex\"]},\n" +
            "    {\"slotId\":\"s1\",\"role\":\"Scout\",\"desc\":\"飞往北区执行搜索,需要时前往共享充电点补能\",\"doneCond\":\"北区搜索完成\",\"constraintIds\":[\"c2_sync_report\",\"c3_wait_cover\",\"c3_charge_mutex\"]}\n" +
            "  ],\n" +
            "  \"constraints\": [\n" +
            "    {\"constraintId\":\"c1_area_a\",\"cType\":\"C1\",\"subject\":\"侦察机A\",\"targetObject\":\"南区\"},\n" +
            "    {\"constraintId\":\"c2_sync_report\",\"cType\":\"C2\",\"channel\":\"whiteboard\",\"condition\":\"南北区都完成后同步回传\",\"syncWith\":[\"s0\",\"s1\"]},\n" +
            "    {\"constraintId\":\"c3_wait_cover\",\"cType\":\"C3\",\"channel\":\"whiteboard\",\"sign\":1,\"watchAgent\":\"s0\",\"reactTo\":\"ReadySignal\"},\n" +
            "    {\"constraintId\":\"c3_charge_mutex\",\"cType\":\"C3\",\"channel\":\"whiteboard\",\"sign\":-1,\"watchAgent\":\"\",\"reactTo\":\"IntentAnnounce\"}\n" +
            "  ]\n" +
            "}\n";
        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(
            new LLMRequestOptions { prompt = prompt, maxTokens = 1400, enableJsonMode = true, callTag = "LLM#2_SlotGen", agentId = props?.AgentID },
            r => llmResult = r));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError("[PlanningModule] LLM#2 返回空");
            busy = false; // BUG-03 修复:失败路径必须重置 busy,否则后续任务无法提交
            SetState(PlanningState.Failed);
            yield break;
        }
        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#2 生成槽原始回复: {llmResult}");
        PlanSlot[] generatedSlots = null;
        StructuredConstraint[] updatedConstraints = null;
        try
        {
            string json = ExtractJson(llmResult);
            if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith("["))
            {
                generatedSlots = JsonConvert.DeserializeObject<PlanSlot[]>(json);
            }
            else
            {
                LLM2SlotPlanResult result = JsonConvert.DeserializeObject<LLM2SlotPlanResult>(json);
                generatedSlots = result?.slots;
                updatedConstraints = result?.constraints;
            }

            if (generatedSlots == null || generatedSlots.Length == 0)
                throw new Exception("LLM#2 未返回有效 slots");

            Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#2 生成槽位: {string.Join(", ", generatedSlots.Select(s => s.slotId + ":" + s.role + ":" + s.desc + ":constraintIds=[" + string.Join(",", s.constraintIds ?? new string[0]) + "]"))}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[PlanningModule] LLM#2 JSON解析失败: {e.Message}");
            busy = false; // BUG-03 修复
            SetState(PlanningState.Failed);
            yield break;
        }

        if (updatedConstraints != null && updatedConstraints.Length > 0)
            MergeConstraintUpdates(updatedConstraints, "LLM#2 abstract constraint updates");
        else
            Debug.LogWarning($"[PlanningModule] {props.AgentID} LLM#2 未返回更新后的 constraints,后续将继续使用 LLM#1 原始约束");

        slots = generatedSlots;

        // 若组内只有自己,直接跳过广播+选槽,本地分配唯一槽
        if (memberCount == 1)
        {
            confirmedSlot = slots[0];
            occupiedSlots.Add(slots[0].slotId);
            var singleAssignment = new Dictionary<string, PlanSlot>(StringComparer.OrdinalIgnoreCase)
            {
                [props.AgentID] = slots[0]
            };
            OnStartExec(new StartExecPayload
            {
                msnId = parsed.msnId,
                groupId = myGroup.groupId,
                constraints = BuildRuntimeConstraintsForAgent(props.AgentID, singleAssignment)
            });
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
        yield return StartCoroutine(llm.SendRequest(
            new LLMRequestOptions { prompt = prompt, maxTokens = 150, enableJsonMode = true, callTag = "LLM#3_SlotPick", agentId = props?.AgentID },
            r => llmResult = r));

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

        // 收集槽位关联的约束对象,供 LLM#4 知晓可引用的约束 ID
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

        // 从 MemoryModule 检索与当前槽位/角色相关的历史经验和反思规则，注入到规划提示词
        // 帮助 LLM#4 在步骤拆分时参考"过去类似角色在此目标上踩过的坑和成功策略"
        string planningMemoryContext = string.Empty;
        if (memory != null)
        {
            planningMemoryContext = memory.BuildPlanningContext(new PlanningMemoryContextRequest
            {
                missionText = parsed?.groupMsns != null && parsed.groupMsns.Length > 0 ? parsed.groupMsns[0] : string.Empty,
                missionId = parsed?.msnId ?? string.Empty,
                roleName = confirmedSlot.role,
                slotId = confirmedSlot.slotId,
                slotLabel = confirmedSlot.role,
                slotTarget = confirmedSlot.doneCond,
                maxMemories = 4,
                maxInsights = 2
            });
        }

        string prompt =
        "你是无人机任务规划中枢。请在不改变计划原意的前提下,将整体任务拆分为具体的【执行步骤】,并精准挂载【约束条件】。\n\n" +
        "## 历史经验与反思规则（来自记忆模块）\n" +
        (string.IsNullOrWhiteSpace(planningMemoryContext) ? "（无历史经验，首次执行此类任务）" : planningMemoryContext) + "\n\n" +
        "## 输入上下文\n" +
        $"计划(desc): {confirmedSlot.desc}\n" +
        $"当前AgentID: {props?.AgentID} | 角色: {confirmedSlot.role}\n" +
        $"完成条件: {confirmedSlot.doneCond}\n" +
        $"无人机状态: [位置: {pos}, 电量: {battery:F0}%]\n" +
        $"待分配约束列表: {constraintsJson}\n\n" +

        "## 任务一:步骤拆分与提取标准（正向定义）\n" +
        "1. 什么是【完整的一步】:\n" +
        "   - 一个步骤必须包含核心动作以及它的全部上下文描述（如路线、起点、执行方式）。动作及其上下文修饰语是一个不可分割的语义整体。\n" +
        "   - 移动类任务:以【最终目的地】为划分标准。有几个不同的最终目的地,就拆成几步。\n" +
        "   - 原地任务:无人机停留在同一空间位置执行的所有连续动作,打包合并为一步。\n" +
        "2. 字段填充规范:\n" +
        "   - stepId: 格式为 \"step_1\", \"step_2\"...\n" +
        "   - text: 完整保留操作及其所有的前置/后置描述（例:“从北门进入厂区东边并开启扫描”）。\n" +
        "   - targetName: 提取最核心的【主体建筑/区域实体名】（如:控制中心、厂区）。当遇到包含方位或附属结构的复合描述（如“厂区东边”、“大楼入口”）时,必须向上追溯,仅提取其依附的【绝对主实体名】（即提取为“厂区”、“大楼”）。若无明确主体实体一律填 \"\"。\n" +
        "   - doneCond: 描述该步完成时的预期状态。无则填 \"\"。\n" +
        "   - constraintIds: 填入分配到该步骤的约束ID数组,没有则填 []。\n\n" +

        "## 任务二:约束条件分配标准\n" +
        "分析约束的核心业务目的,将其匹配给最契合的那一个步骤:\n" +
        "1. C2类 (同步完成):分配给需要“集体到位”或“共同集结”的到达步骤。\n" +
        "2. C3类 (条件依赖 sign=+1):\n" +
        "   - watchAgent:分配给负责“发出信号/状态”的操作步骤。\n" +
        "   - 其他角色:分配给需要“等待信号才能开始”的前置步骤。\n" +
        "3. C3类 (资源互斥 sign=-1):分配给多机共享同一路径或目标、容易产生空间冲突的【移动步骤】。\n\n" +

        "## 输出要求\n" +
        "仅输出合法的 JSON 对象。在 thought 字段中，直接陈述原始计划的拆分依据、实体地标的提取结果，以及各个约束的匹配理由。\n" +
        "原始计划为：'等待安全信号后，从营地出发前往哨站附近拍照，然后穿过狭窄通道飞往能源站北侧，到达后等待全体小队汇合一起开启护盾'。\n" +
        "{\n" +
        "  \"thought\": \"原始计划包含2个不同目的地，因此拆解为2步。step_1提取绝对地标'哨站'，挂载C3(+1)依赖约束(等待前置信号)；step_2包含位移与原地连续操作，按规则合并为1步，遇到复合描述'能源站北侧'向上追溯提取主实体'能源站'，同时挂载C3(-1)互斥约束(狭窄通道防撞)和C2同步约束(等待集体汇合)。\",\n" +
        "  \"steps\": [\n" +
        "    {\n" +
        "      \"stepId\": \"step_1\",\n" +
        "      \"text\": \"等待安全信号后，从营地出发前往哨站附近拍照\",\n" +
        "      \"targetName\": \"哨站\",\n" +
        "      \"doneCond\": \"拍照完成\",\n" +
        "      \"constraintIds\": [\"c3_wait_signal\"]\n" +
        "    },\n" +
        "    {\n" +
        "      \"stepId\": \"step_2\",\n" +
        "      \"text\": \"然后穿过狭窄通道飞往能源站北侧，到达后等待全体小队汇合一起开启护盾\",\n" +
        "      \"targetName\": \"能源站\",\n" +
        "      \"doneCond\": \"护盾开启\",\n" +
        "      \"constraintIds\": [\"c3_channel_mutex\", \"c2_sync_shield\"]\n" +
        "    }\n" +
        "  ]\n" +
        "}";

        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(
            new LLMRequestOptions { prompt = prompt, maxTokens = 800, enableJsonMode = true, callTag = "LLM#4_StepGen", agentId = props?.AgentID },
            r => llmResult = r));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError("[PlanningModule] LLM#4 返回空");
            busy = false; // BUG-03 修复:失败路径必须重置 busy
            SetState(PlanningState.Failed);
            yield break;
        }
        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#4 原始回复: {llmResult}");
        PlanStep[] steps = null;
        string stepThought = string.Empty;
        try
        {
            string parsedJson = ExtractJson(llmResult);
            if (!string.IsNullOrWhiteSpace(parsedJson) && parsedJson.TrimStart().StartsWith("{"))
            {
                LLM4StepGenResult result = JsonConvert.DeserializeObject<LLM4StepGenResult>(parsedJson);
                stepThought = result?.thought ?? string.Empty;
                steps = result?.steps;
            }
            else
            {
                // 兼容旧格式:LLM 直接返回步骤数组
                steps = JsonConvert.DeserializeObject<PlanStep[]>(parsedJson);
            }

            if (!string.IsNullOrWhiteSpace(stepThought))
                Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#4 Thought: {stepThought}");

            if (steps == null)
                throw new Exception("LLM#4 steps 解析结果为 null");

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
            thought = stepThought,
            steps  = steps,
            curIdx = 0
        };

        // 将 LLM#4 生成的计划快照写入记忆，供后续同类任务规划参考
        // 记录角色、步骤概要、目标条件，而非全量 JSON（避免占用过多 token）
        if (memory != null && steps.Length > 0)
        {
            string stepsSummary = string.Join(" → ", steps.Select(s => s.text));
            memory.RememberPlanSnapshot(
                missionId: parsed.msnId,
                slotId: confirmedSlot.slotId,
                stepLabel: confirmedSlot.role,
                planSummary: $"[{confirmedSlot.role}] 计划步骤: {stepsSummary}",
                targetRef: confirmedSlot.doneCond,
                tags: new[] { confirmedSlot.role, parsed.msnId });
        }

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

        // 每组只收到:全局约束(groupScope==-1)+ 本组约束(groupScope==groupIndex)
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
        // 非协调者(parsed==null)直接接受并采用消息中的 msnId;
        // 协调者则验证 msnId 是否匹配,防止跨任务消息干扰。
        if (parsed == null)
            parsed = new ParsedMission { msnId = p.msnId };
        else if (p.msnId != parsed.msnId)
            return;

        allGroups = p.groups;

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

        // 将全量约束存入本地字典,供 GetConstraint 查询
        ReplaceConstraintDict(p.constraints, "GroupBootstrap");

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

        if (p.constraints != null)
            ReplaceConstraintDict(p.constraints, "StartExec runtime constraints");
        else
            Debug.LogWarning($"[PlanningModule] {props.AgentID} 收到 StartExec 但未携带运行态约束,继续沿用当前约束字典");

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
        var assignedSlotsByAgent = new Dictionary<string, PlanSlot>(StringComparer.OrdinalIgnoreCase);

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
                Debug.Log($"[PlanningModule] {agentId} 选择的槽 {wantedSlotId} 已被占用,改分配 {assigned.slotId}");
            }

            remaining.Remove(assigned);
            occupiedSlots.Add(assigned.slotId);
            assignedSlotsByAgent[agentId] = assigned;

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

        Debug.Log($"[PlanningModule] {props.AgentID} 最终分槽结果: " +
                  string.Join(", ", assignedSlotsByAgent.Select(kv => $"{kv.Key}->{kv.Value.slotId}")));

        // StartExecution 不是简单广播同一份约束,而是“按成员逐个发送”。
        // 原因:
        // 1. 最终分槽后,watchAgent / syncWith 要从抽象引用(slotId / role / desc)回填成真实 agentId。
        // 2. 这个回填结果对不同接收者可能不完全一样。
        //    尤其是 C2.syncWith,需要从“参与同步的全部成员”里去掉当前接收者自己,
        //    否则会出现 A 的约束里还包含 A 自己,导致自己等自己。
        // 3. 所以这里不能直接复用一份全组公共 constraints,而是要对每个 memberId
        //    单独调用 BuildRuntimeConstraintsForAgent(memberId, assignedSlotsByAgent),
        //    生成“这个成员视角下可直接执行的运行态约束”。
        foreach (string memberId in myGroup.memberIds)
        {
            if (string.Equals(memberId, props.AgentID, StringComparison.OrdinalIgnoreCase)) continue;

            // 为当前接收者生成一份专属的运行态约束:
            // - C3 sign=+1: watchAgent 会被回填成真实的被等待 agentId
            // - C2: syncWith 会被回填成真实 agentId 数组,并移除当前接收者自己
            StructuredConstraint[] runtimeConstraints = BuildRuntimeConstraintsForAgent(memberId, assignedSlotsByAgent);

            // 将“最终分槽结果 + 当前成员专属约束”一起发给该成员。
            // 成员侧收到 StartExecution 后,会先用这份 constraints 覆盖本地约束字典,
            // 然后再进入 LLM#4 / ADM。这样后续执行阶段读取到的就是已经回填完成的约束。
            comm.SendScopedMessage(
                CommunicationScope.DirectAgent,
                MessageType.StartExecution,
                new StartExecPayload
                {
                    msnId = parsed.msnId,
                    groupId = myGroup.groupId,
                    constraints = runtimeConstraints
                },
                targetAgentId: memberId,
                reliable: true);
        }

        // 组长自身触发 LLM#4
        OnStartExec(new StartExecPayload
        {
            msnId = parsed.msnId,
            groupId = myGroup.groupId,
            constraints = BuildRuntimeConstraintsForAgent(props.AgentID, assignedSlotsByAgent)
        });

        // 仅组长初始化 GroupMonitor（含通信层和监控层）
        if (GetComponent<GroupMonitor>() == null)
        {
            string[] otherLeaderIds = allGroups != null
                ? allGroups
                    .Where(g => g.groupId != myGroup.groupId)
                    .Select(g => g.leaderId)
                    .ToArray()
                : Array.Empty<string>();

            var monitor = gameObject.AddComponent<GroupMonitor>();
            monitor.Initialize(myGroup, myGroup.groupId, myGroup.leaderId,
                               otherLeaderIds, llm);
        }
    }

    // ─────────────────────────────────────────────────────────
    // 状态管理
    // ─────────────────────────────────────────────────────────

    private void SetState(PlanningState s)
    {
        state     = s;
        waitStart = Time.time;
        // BUG-M4: Failed/Done 时自动释放 busy 锁,防止 PlanningModule 卡死
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

    /// <summary>返回当前任务 ID,供执行层和监控层读取。</summary>
    public string GetCurrentMissionId()
    {
        return parsed?.msnId ?? agentPlan?.msnId ?? string.Empty;
    }

    /// <summary>返回当前已确认的槽位 ID，供 ActionDecisionModule 填入 ActionExecutionContext.slotId。</summary>
    public string GetCurrentSlotId()
    {
        return confirmedSlot?.slotId ?? agentPlan?.slotId ?? string.Empty;
    }

    /// <summary>返回当前任务描述,优先使用已确认槽位的描述。</summary>
    public string GetCurrentMissionDescription()
    {
        if (!string.IsNullOrWhiteSpace(agentPlan?.desc)) return agentPlan.desc;
        if (!string.IsNullOrWhiteSpace(confirmedSlot?.desc)) return confirmedSlot.desc;
        if (!string.IsNullOrWhiteSpace(myGroup?.mission)) return myGroup.mission;
        return string.Empty;
    }

    /// <summary>
    /// 通过约束 ID 查询完整的结构化约束对象(由 OnGroupBootstrap 填充)。
    /// 若未找到则返回 null。
    /// </summary>
    public StructuredConstraint GetConstraint(string constraintId)
    {
        if (string.IsNullOrWhiteSpace(constraintId)) return null;
        _constraintDict.TryGetValue(constraintId, out var c);
        return c;
    }

    /// <summary>返回本 Agent 所属组的组 ID(供白板读写使用)。</summary>
    public string GetGroupId() => myGroup?.groupId ?? string.Empty;

    private void ReplaceConstraintDict(StructuredConstraint[] constraints, string sourceTag)
    {
        _constraintDict.Clear();
        MergeConstraintUpdates(constraints, sourceTag);
    }

    private void MergeConstraintUpdates(StructuredConstraint[] constraints, string sourceTag)
    {
        int loadedCount = 0;
        if (constraints != null)
        {
            foreach (StructuredConstraint constraint in constraints)
            {
                if (constraint == null || string.IsNullOrWhiteSpace(constraint.constraintId)) continue;
                _constraintDict.TryGetValue(constraint.constraintId, out StructuredConstraint existingConstraint);
                _constraintDict[constraint.constraintId] = MergeConstraint(existingConstraint, constraint);
                loadedCount++;
            }
        }

        Debug.Log($"[PlanningModule] {props?.AgentID} {sourceTag} 已加载 {loadedCount} 条约束,当前字典={_constraintDict.Count}");
    }

    /// <summary>
    /// 导出当前组可见的约束快照。
    /// 这里会按 constraintId 排序,并克隆一份,避免后续修改影响字典里的原对象。
    /// </summary>
    private StructuredConstraint[] ExportConstraints()
    {
        return _constraintDict.Values
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.constraintId))
            .OrderBy(c => c.constraintId, StringComparer.OrdinalIgnoreCase)
            .Select(CloneConstraint)
            .ToArray();
    }

    /// <summary>
    /// 为某个具体成员生成“运行态约束”。
    /// 输入的约束里,watchAgent / syncWith 还可能是抽象引用,例如 slotId、role、desc。
    /// 这个函数会根据最终分槽结果,把这些抽象引用回填成真实 agentId。
    /// 对 C2 还会额外去掉自己,并做去重。
    /// </summary>
    private StructuredConstraint[] BuildRuntimeConstraintsForAgent(
        string runtimeAgentId,
        Dictionary<string, PlanSlot> assignedSlotsByAgent)
    {
        StructuredConstraint[] sourceConstraints = ExportConstraints();
        if (sourceConstraints.Length == 0) return sourceConstraints;

        if (assignedSlotsByAgent == null || assignedSlotsByAgent.Count == 0)
        {
            Debug.LogWarning($"[PlanningModule] {props?.AgentID} 无法为 {runtimeAgentId} 回填约束:最终分槽为空");
            return sourceConstraints;
        }

        // 下面几张表都是“抽象引用 -> 真实 agentId”的查找表。
        // slotId 最稳定,role 和 desc 只是兜底匹配。
        var slotIdToAgentId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupAgentLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var uniqueRoleToAgentId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var repeatedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueDescToAgentId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var repeatedDescs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (myGroup?.memberIds != null)
        {
            foreach (string memberId in myGroup.memberIds)
            {
                if (!string.IsNullOrWhiteSpace(memberId))
                    groupAgentLookup[memberId] = memberId;
            }
        }

        // 根据最终分槽结果建立映射:
        // s0 -> agent_A, Scout -> agent_A(仅当 role 唯一时), 某段 desc -> agent_A(仅当 desc 唯一时)
        foreach (KeyValuePair<string, PlanSlot> kv in assignedSlotsByAgent)
        {
            string agentId = kv.Key;
            PlanSlot slot = kv.Value;
            if (string.IsNullOrWhiteSpace(agentId) || slot == null) continue;

            groupAgentLookup[agentId] = agentId;
            if (!string.IsNullOrWhiteSpace(slot.slotId))
                slotIdToAgentId[slot.slotId] = agentId;

            RegisterUniqueAgentRef(uniqueRoleToAgentId, repeatedRoles, slot.role, agentId);
            RegisterUniqueAgentRef(uniqueDescToAgentId, repeatedDescs, slot.desc, agentId);
        }

        var runtimeConstraints = new List<StructuredConstraint>(sourceConstraints.Length);
        foreach (StructuredConstraint constraint in sourceConstraints)
        {
            // C3 sign=+1: 把 watchAgent 从抽象引用回填为真实 agentId。
            if ((constraint.cType == "C3" || constraint.cType == "Coupling") && constraint.sign == 1)
            {
                string originalWatch = constraint.watchAgent;
                string resolvedWatch = ResolveRuntimeAgentRef(
                    originalWatch,
                    groupAgentLookup,
                    slotIdToAgentId,
                    uniqueRoleToAgentId,
                    uniqueDescToAgentId);

                if (!string.IsNullOrWhiteSpace(originalWatch) && string.IsNullOrWhiteSpace(resolvedWatch))
                    Debug.LogWarning($"[PlanningModule] 约束 {constraint.constraintId} 的 watchAgent 无法回填: {originalWatch}");

                constraint.watchAgent = resolvedWatch;
            }

            // C2: 把 syncWith 中的抽象引用回填为真实 agentId。
            // 这里按“当前接收约束的成员”去掉自己,避免自己等自己。
            if (constraint.cType == "C2" || constraint.cType == "Completion")
            {
                constraint.syncWith = ResolveRuntimeAgentRefs(
                    constraint.syncWith,
                    runtimeAgentId,
                    constraint.constraintId,
                    groupAgentLookup,
                    slotIdToAgentId,
                    uniqueRoleToAgentId,
                    uniqueDescToAgentId);
            }

            runtimeConstraints.Add(constraint);
        }

        Debug.Log($"[PlanningModule] {props?.AgentID} 为 {runtimeAgentId} 生成运行态约束 {runtimeConstraints.Count} 条");
        return runtimeConstraints.ToArray();
    }

    private static void RegisterUniqueAgentRef(
        Dictionary<string, string> uniqueMap,
        HashSet<string> duplicateKeys,
        string rawKey,
        string agentId)
    {
        if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(agentId)) return;
        string key = rawKey.Trim();
        if (duplicateKeys.Contains(key)) return;

        if (uniqueMap.ContainsKey(key))
        {
            uniqueMap.Remove(key);
            duplicateKeys.Add(key);
            return;
        }

        uniqueMap[key] = agentId;
    }

    private string[] ResolveRuntimeAgentRefs(
        string[] rawRefs,
        string runtimeAgentId,
        string constraintId,
        Dictionary<string, string> groupAgentLookup,
        Dictionary<string, string> slotIdToAgentId,
        Dictionary<string, string> uniqueRoleToAgentId,
        Dictionary<string, string> uniqueDescToAgentId)
    {
        if (rawRefs == null || rawRefs.Length == 0) return Array.Empty<string>();

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawRef in ExpandAgentRefs(rawRefs))
        {
            string resolvedAgentId = ResolveRuntimeAgentRef(
                rawRef,
                groupAgentLookup,
                slotIdToAgentId,
                uniqueRoleToAgentId,
                uniqueDescToAgentId);

            if (string.IsNullOrWhiteSpace(resolvedAgentId))
            {
                Debug.LogWarning($"[PlanningModule] 约束 {constraintId} 的 syncWith 项无法回填: {rawRef}");
                continue;
            }

            if (string.Equals(resolvedAgentId, runtimeAgentId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (seen.Add(resolvedAgentId))
                resolved.Add(resolvedAgentId);
        }

        return resolved.ToArray();
    }

    private string ResolveRuntimeAgentRef(
        string rawRef,
        Dictionary<string, string> groupAgentLookup,
        Dictionary<string, string> slotIdToAgentId,
        Dictionary<string, string> uniqueRoleToAgentId,
        Dictionary<string, string> uniqueDescToAgentId)
    {
        foreach (string token in ExpandAgentRefs(rawRef))
        {
            if (groupAgentLookup.TryGetValue(token, out string agentId))
                return agentId;

            if (slotIdToAgentId.TryGetValue(token, out agentId))
                return agentId;

            if (uniqueRoleToAgentId.TryGetValue(token, out agentId))
                return agentId;

            if (uniqueDescToAgentId.TryGetValue(token, out agentId))
                return agentId;
        }

        return string.Empty;
    }

    private static IEnumerable<string> ExpandAgentRefs(string rawRef)
    {
        if (string.IsNullOrWhiteSpace(rawRef)) yield break;

        string[] parts = Regex.Split(rawRef, @"[,,、;；|/]");
        foreach (string part in parts)
        {
            string token = part?.Trim();
            if (!string.IsNullOrWhiteSpace(token))
                yield return token;
        }
    }

    private static IEnumerable<string> ExpandAgentRefs(string[] rawRefs)
    {
        if (rawRefs == null) yield break;

        foreach (string rawRef in rawRefs)
        {
            foreach (string token in ExpandAgentRefs(rawRef))
                yield return token;
        }
    }

    private static StructuredConstraint CloneConstraint(StructuredConstraint source)
    {
        if (source == null) return null;

        return new StructuredConstraint
        {
            constraintId = source.constraintId,
            cType = source.cType,
            channel = source.channel,
            groupScope = source.groupScope,
            subject = source.subject,
            targetObject = source.targetObject,
            exclusive = source.exclusive,
            condition = source.condition,
            syncWith = source.syncWith != null ? source.syncWith.ToArray() : null,
            sign = source.sign,
            watchAgent = source.watchAgent,
            reactTo = source.reactTo
        };
    }

    private static StructuredConstraint MergeConstraint(StructuredConstraint existing, StructuredConstraint incoming)
    {
        if (incoming == null) return CloneConstraint(existing);
        if (existing == null) return CloneConstraint(incoming);

        StructuredConstraint merged = CloneConstraint(existing);
        merged.constraintId = !string.IsNullOrWhiteSpace(incoming.constraintId) ? incoming.constraintId : merged.constraintId;

        if (incoming.cType != null) merged.cType = incoming.cType;
        if (incoming.channel != null) merged.channel = incoming.channel;
        if (incoming.subject != null) merged.subject = incoming.subject;
        if (incoming.targetObject != null) merged.targetObject = incoming.targetObject;
        if (incoming.condition != null) merged.condition = incoming.condition;
        if (incoming.syncWith != null) merged.syncWith = incoming.syncWith.ToArray();
        if (incoming.sign != 0 || merged.sign == 0) merged.sign = incoming.sign;
        if (incoming.watchAgent != null) merged.watchAgent = incoming.watchAgent;
        if (incoming.reactTo != null) merged.reactTo = incoming.reactTo;

        return merged;
    }

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

    /// <summary>
    /// 由 DebateResolved 触发:请求重规划当前任务。
    /// 将当前状态重置为 Idle,让 IntelligentAgent 的下一次 CheckForDecision 重新发起规划。
    /// reason 写入日志供追溯,不影响执行逻辑。
    /// </summary>
    public void RequestReplan(string reason)
    {
        Debug.Log($"[PlanningModule] {props?.AgentID} 收到重规划请求: {reason}");
        if (state == PlanningState.Active || state == PlanningState.Done)
        {
            SetState(PlanningState.Idle);
            busy = false;
            agentPlan = null;
            Debug.Log($"[PlanningModule] {props?.AgentID} 规划已重置,等待重新规划");
        }
    }

    private IEnumerator TimeLimitCoroutine(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (state == PlanningState.Active)
        {
            Debug.LogWarning($"[Planning] 任务 {parsed?.msnId} 时间限制 {seconds}s 到达,强制终止。");
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
