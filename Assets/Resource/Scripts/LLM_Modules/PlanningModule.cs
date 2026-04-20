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

    /// <summary>
    /// 当前 agent 的人格系统引用，在 Start() 中通过 GetComponent 获取。
    /// 用于在 LLM#3（选槽）阶段注入人格偏好提示，引导 LLM 做出更符合人格特征的角色选择。
    /// 为 null 时跳过人格注入，不影响正常规划流程。
    /// </summary>
    private PersonalitySystem _personalitySystem;

    /// <summary>
    /// MAD 网关（挂在同一 agent GameObject 上）。
    /// 在 ResolveAndConfirm() 中检测到槽位冲突时调用 Raise() 发起辩论。
    /// 为 null 时跳过 MAD 触发，不影响正常分配流程。
    /// </summary>
    private MADGateway _madGateway;

    private static int msnCounter;

    void Start()
    {
        llm    = FindObjectOfType<LLMInterface>();
        comm   = GetComponent<CommunicationModule>();
        memory = GetComponent<MemoryModule>();

        // 获取人格系统（挂在同一 agent GameObject 上）
        _personalitySystem = GetComponent<PersonalitySystem>();

        // 获取 MAD 网关（挂在同一 agent GameObject 上）
        _madGateway = GetComponent<MADGateway>();

        // 向 AgentPlanRegistry 注册，供 MADDecisionForwarder 在任务继承时查询本 agent 的剩余步骤
        if (props != null)
            AgentPlanRegistry.Register(props.AgentID, this);

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

        string constraintPolicies = memory?.BuildPoliciesContext(desc, new[] { "constraint_analysis" }, 3) ?? string.Empty;
        string prompt =
            "你是多智能体任务规划器。请将任务解析为合法 JSON 对象。\n\n" +
            (string.IsNullOrWhiteSpace(constraintPolicies) ? string.Empty :
                "## 历史约束识别规律（来自记忆模块，优先参考）\n" + constraintPolicies + "\n\n") +
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
            "[C1 资源分配]识别标志:任务中明确指定\" 谁负责什么目标/区域/资源\",或暗含分工避免重复。\n" +
            "  subject=执行者角色描述(agentId留空'')\n" +
            "  targetObject=被分配的目标/区域名称  exclusive=是否独占(通常为 true)\n" +
            "  channel=direct\n\n" +
            "[C2 完成同步]识别标志:任务要求多机\"都完成后一起…\"\"同步…\"\"统一…\"等,强调集体完成再进行下一步。\n" +
            "  condition=同步完成的条件描述\n" +
            "  syncWith=需要等待其写入完成信号的其他 agentId 列表(此阶段不知道 agentId,填 [])\n" +
            "  channel=whiteboard\n\n" +
            "[C3 行为耦合]两种子类型,只在存在明确的等待/互斥关系时才生成,纯并行不需要 C3:\n" +
            "  sign=+1(单向前置等待):一机必须等另一机到位/就绪后才允许开始行动(非对称依赖)。\n" +
            "    watchAgent=被等待的 agentId(此阶段不知道 agentId,填 [])  reactTo='ReadySignal'\n" +
            "  sign=-1(动态互斥):满足以下全部条件时生成:\n" +
            "    ① 每个[具体目标/路径点/资源点]同一时刻只允许一个 agent 占用/前往\n" +
            "    ③ 具体哪个 agent 去哪个目标,规划时无法静态确定\n" +
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
            "· C3-1:是否存在目标集合/路径、且每个目标只允许一人占用、且具体分配规划时不确定？→ sign=-1\n" +
            "· C3+1:是否有一机必须等另一机发出就绪信号后才能行动（单向前置依赖）？→ sign=+1\n" +
            "确认无遗漏后再输出 JSON。\n\n" +

            "─── 输出要求 ───\n" +
            "1. 输出内容仅包含 JSON,所有字符串字段不得为 null(不知道时填空字符串 '')。\n" +
            "2. 每条约束必须包含字段:constraintId / cType / channel,以及对应类型的专用字段。\n" +
            "3. 不相关类型的专用字段可省略或填默认值(bool=false, int=0, string='', array=[])。\n" +
            "4. timeLimit 为秒数,无限制时填 0。\n" +
            "5. thought 字段输出 JSON 对象，字段：reasoning（约束识别推理，1-3句）、confidence（高/中/低）、confidence_reason（原因）、suggestion（可跨任务复用的抽象决策原则：描述结构性/语义性条件，而非当前任务的具体表述；本次无新规律可提炼则填\"\"）。\n\n" +

            "─── 示例(含全部三类约束 + C3 两种子类型)───\n" +
            "输入任务:三架无人机协作侦察。A机负责东区,B机负责西区(两机区域独占不重叠);" +
            "B机须等A机完成起飞检查发出就绪信号后才允许起飞;" +
            "A和B均完成侦察后同步向指挥部回传坐标;" +
            "A与B侦察途中如需使用同一充电桩,只能一架先用,另一架等待。\n" +
            "{\n" +
            "  \"thought\": {\"reasoning\":\"A/B各负责独立区域→C1；充电桩同一时刻只能一架使用→C3互斥；均完成后统一回传→C2同步\",\"confidence\":\"高\",\"confidence_reason\":\"约束类型均有明确语义对应\",\"suggestion\":\"当资源使用顺序在规划时无法预先确定时，应用C3-1动态互斥而非C1静态分配\"},\n" +
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

        memory?.RememberConstraintAnalysis(
            missionId: p.msnId,
            missionDesc: desc,
            relType: p.relType,
            constraintCount: p.constraints?.Length ?? 0,
            thought: p.thought);

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

        string slotDesignPolicies = memory?.BuildPoliciesContext(myGroup.mission, new[] { "slot_design" }, 3) ?? string.Empty;
        string prompt =
            "你是多智能体任务组长。\n" +
            "请做两件事:\n" +
            "1. 为本组生成计划槽 slots。\n" +
            "2. 回写约束 constraints 中的槽位引用。\n\n" +
            (string.IsNullOrWhiteSpace(slotDesignPolicies) ? string.Empty :
                "## 历史槽位设计规律（来自记忆模块，优先参考）\n" + slotDesignPolicies + "\n\n") +
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
            "  \"thought\": {\"reasoning\":\"槽位设计推理（1-3句）\",\"confidence\":\"高/中/低\",\"confidence_reason\":\"原因\",\"suggestion\":\"可跨任务复用的抽象槽位设计原则，或\\\"\\\"\"},\n" +
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
            "  \"thought\": {\"reasoning\":\"C1约束指定A负责南区、B负责北区，设2个Scout槽分工明确；C3+1要求s0先行发信号，s1等待；C3-1互斥充电桩两槽都绑定\",\"confidence\":\"高\",\"confidence_reason\":\"约束与槽位一一对应无歧义\",\"suggestion\":\"存在单向前置依赖（C3+1）时，信号发出方与等待方应分设不同槽位，避免因角色合并导致循环等待\"},\n" +
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
        string slotDesignThought = string.Empty;
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
                slotDesignThought = result?.thought ?? string.Empty;
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

        memory?.RememberSlotDesign(
            missionId: parsed.msnId,
            groupMission: myGroup.mission,
            slotsSummary: string.Join(", ", generatedSlots.Select(s => s.role)),
            thought: slotDesignThought);

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

        // 构建人格偏好提示（LLM#3 专用）：
        //   GetRolePreferenceHint 根据大五维度生成偏好描述（如"倾向系统性角色"）。
        //   非空时追加到 prompt 中，让 LLM 在选槽时将人格偏好作为打破平局的依据。
        //   要求 LLM 在 thought 字段中显式说明是否参考了此偏好，便于调试和记忆回溯。
        string roleHint = _personalitySystem?.GetRolePreferenceHint() ?? string.Empty;
        string personalitySection = string.IsNullOrWhiteSpace(roleHint)
            ? string.Empty
            : roleHint + "\n请在 thought 的 reasoning 和 factors 字段中说明是否参考了上述人格倾向，以及如何影响了你的选择。\n\n";

        // 从角色列表提取查询文本，检索过去选槽经验（不知道最终角色，用可选角色名作 freeText）
        string slotRoleSummary = string.Join(" ", availableSlots.Select(s => s.role).Distinct());
        string slotPickPolicies = memory?.BuildPoliciesContext(slotRoleSummary, new[] { "slot_selection" }, 3) ?? string.Empty;

        string prompt =
            "你是无人机智能体。请从可选槽中选择最适合自己的一个,并输出合法 JSON 对象。\n\n" +
            (string.IsNullOrWhiteSpace(slotPickPolicies) ? string.Empty :
                "## 历史选槽规律（来自记忆模块，优先参考）\n" + slotPickPolicies + "\n\n") +
            $"可选槽:{slotsJson}\n" +
            $"当前电量:{battery:F0}%,当前位置:{pos}\n\n" +
            personalitySection +
            GetConciseTextPromptText() + "\n" +
            "输出要求:\n" +
            "1. 输出内容仅包含 JSON。\n" +
            "2. `slotId` 是可选槽中的一个。\n" +
            "3. `thought` 输出 JSON 对象，字段：reasoning（综合选槽理由，1-3句）、factors（决策因子字符串数组，如[\"电量充足\",\"位置靠近南区\",\"人格偏好系统性角色\"]）、confidence（高/中/低）、confidence_reason（原因）、suggestion（可跨任务复用的抽象选槽原则：描述结构性权衡条件而非具体阈值，无新规律则填\"\"）。\n\n" +
            "输出格式示例:\n" +
            "{\n" +
            "  \"thought\": {\"reasoning\":\"当前电量充足且位置靠近南区，人格倾向系统性角色，s2(Perimeter)最适合\",\"factors\":[\"电量充足\",\"位置靠近南区\",\"人格偏好系统性角色\"],\"confidence\":\"高\",\"confidence_reason\":\"与人格偏好高度匹配\",\"suggestion\":\"当续航成为瓶颈时，补给可达性的优先级应高于角色最优匹配\"},\n" +
            "  \"slotId\": \"s0\"\n" +
            "}";

        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(
            new LLMRequestOptions { prompt = prompt, maxTokens = 300, enableJsonMode = true, callTag = "LLM#3_SlotPick", agentId = props?.AgentID },
            r => llmResult = r));

        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#3 原始回复: {llmResult}");

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#3 返回空,选第一个槽");
            llmResult = $"{{\"slotId\":\"{availableSlots[0].slotId}\"}}";
        }

        SlotSelectPayload selectPayload = null;
        LLM3SlotPickResult pickResult = null;
        try
        {
            pickResult = JsonConvert.DeserializeObject<LLM3SlotPickResult>(ExtractJson(llmResult));
            selectPayload = new SlotSelectPayload
            {
                msnId   = parsed.msnId,
                agentId = props.AgentID,
                slotId  = pickResult?.slotId ?? availableSlots[0].slotId
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

        PlanSlot chosenSlot = System.Array.Find(availableSlots, s => s.slotId == selectPayload.slotId)
                              ?? availableSlots[0];
        memory?.RememberSlotSelection(
            missionId: parsed.msnId,
            slotId: chosenSlot.slotId,
            role: chosenSlot.role,
            doneCond: chosenSlot.doneCond,
            thought: pickResult?.thought);

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
        "   - 移动类任务:每一处不同目的地的移动单独为一步。若到达目的地后还需执行动作，且该动作依赖多机同步（存在C2约束），则C2约束挂载在移动步骤，到达后的动作必须拆为独立的下一步——C2协同构成同步边界，边界两侧的动作不可合并。例：'飞往能源站，到达后等全体汇合一起开启护盾'→ step_1:飞往能源站（挂C2）；step_2:开启护盾（无C2）。\n" +
        "   - 原地任务:无人机停留在同一空间位置执行的所有连续动作，且不存在C2同步边界时，打包合并为一步。\n" +
        "2. 字段填充规范:\n" +
        "   - stepId: 格式为 \"step_1\", \"step_2\"...\n" +
        "   - text: 完整保留操作及其所有的前置/后置描述（例:“从北门进入厂区东边并开启扫描”）。\n" +
        "   - targetName: 提取最核心的【主体建筑/区域实体名】（如:控制中心、厂区）。当遇到包含方位或附属结构的复合描述（如“厂区东边”、“大楼入口”）时,必须向上追溯,仅提取其依附的【绝对主实体名】（即提取为“厂区”、“大楼”）。若无明确主体实体一律填 \"\"。\n" +
        "   - doneCond: 描述该步完成时的预期状态。无则填 \"\"。\n" +
        "   - constraintIds: 填入分配到该步骤的约束ID数组,没有则填 []。\n\n" +

        "## 任务二:约束条件分配标准\n" +
        "分析约束的核心业务目的,将其匹配给最契合的那一个步骤:\n" +
        "1. C2类 (同步完成):分配给需要“集体到位”或“共同集结”的到达步骤,而不是到达后的步骤。\n" +
        "2. C3类 (条件依赖 sign=+1) — 根据自身槽位判断角色：\n" +
        "   - 若约束的 watchAgent 字段 == 当前槽位ID（" + confirmedSlot.slotId +
        "）：本机是信号发出方，将该约束挂到发出就绪信号的步骤。\n" +
        "   - 若约束的 watchAgent 字段 != 当前槽位ID（或为空）：本机是等待方，将该约束挂到需要等待信号后才能开始的前置步骤。\n" +
        "3. C3类 (资源互斥 sign=-1):分配给多机共享同一路径或目标、容易产生空间冲突的【移动步骤】。\n" +
        "⚠️ 强制要求:【待分配约束列表】中的每一个 constraintId 都必须出现在某个步骤的 constraintIds 中，不得遗漏任何一个。\n\n" +

        "## 输出要求\n" +
        "仅输出合法的 JSON 对象。thought 字段输出 JSON 对象，字段：split_reasoning（步骤拆分依据与地标提取，1-3句）、constraint_checks（数组，对每个输入 constraintId 逐条核查，每项含 constraintId/assigned_step/reason）、confidence（高/中/低）、confidence_reason（原因）、suggestion（可跨任务复用的抽象步骤拆分/地标提取原则，描述结构性条件而非当前任务细节；无新规律则填\"\"）。\n" +
        "原始计划为：'等待安全信号后，从营地出发前往哨站附近拍照，然后穿过狭窄通道飞往能源站北侧，到达后等待全体小队汇合一起开启护盾'。\n" +
        "{\n" +
        "  \"thought\": {\"split_reasoning\":\"原始计划含2个不同目的地，拆解为2步；step_1提取绝对地标'哨站'；step_2复合描述'能源站北侧'向上追溯提取主实体'能源站'\",\"constraint_checks\":[{\"constraintId\":\"c3_wait_signal\",\"assigned_step\":\"step_1\",\"reason\":\"C3+1等待前置信号，绑定需等待信号的前置步骤\"},{\"constraintId\":\"c3_channel_mutex\",\"assigned_step\":\"step_2\",\"reason\":\"C3-1互斥约束绑定狭窄通道移动步骤\"},{\"constraintId\":\"c2_sync_shield\",\"assigned_step\":\"step_2\",\"reason\":\"C2同步约束绑定需集体到位的到达步骤\"}],\"confidence\":\"高\",\"confidence_reason\":\"目的地和约束匹配关系清晰\",\"suggestion\":\"当步骤描述含方位修饰词时应向上追溯到主实体名以避免导航失配\"},\n" +
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
        "      \"text\": \"然后穿过狭窄通道飞往能源站北侧\",\n" +
        "      \"targetName\": \"能源站\",\n" +
        "      \"doneCond\": \"\",\n" +
        "      \"constraintIds\": [\"c3_channel_mutex\", \"c2_sync_shield\"]\n" +
        "    },\n" +
        "    {\n" +
        "      \"stepId\": \"step_3\",\n" +
        "      \"text\": \"到达后等待全体小队汇合一起开启护盾\",\n" +
        "      \"targetName\": \"\",\n" + 
        "      \"doneCond\": \"护盾开启\",\n" +
        "      \"constraintIds\": [ ]\n" +
        "    },\n" +
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
                thought: stepThought,
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
        // ── Step 1: 确定分配顺序（先到先得的"先到"依据）──────────────────────
        // 按成员选槽时间戳（selectionTs）升序排列：到达越早优先级越高，对应"先到先得"语义。
        // 未收到选择的成员（selectionTs 中无记录）赋值 float.MaxValue，排在最末尾。
        List<string> ordered = myGroup.memberIds
            .OrderBy(id => selectionTs.ContainsKey(id) ? selectionTs[id] : float.MaxValue)
            .ToList();

        ordered.Remove(props.AgentID);   // 先把组长从时间戳排序中移除
        ordered.Insert(0, props.AgentID); // 再把组长插回到最前：组长拥有最高优先级，自身选槽总是最先确认

        List<PlanSlot> remaining = new List<PlanSlot>(slots); // 可用槽池的副本，随分配逐步缩减
        var assignedSlotsByAgent = new Dictionary<string, PlanSlot>(StringComparer.OrdinalIgnoreCase); // 最终分配结果：agentId → 分配到的槽

        // ── Step 2: 冲突检测 + MAD 辩论触发 ──────────────────────────────────
        // 在实际执行先到先得分配之前，先扫描是否有多个 Agent 同时选了同一个槽（冲突）。
        // 若有冲突，异步发起 MAD 辩论让全组讨论更优的分配方案；
        // 但本函数不等待辩论结果——辩论是异步的，当前周期仍继续先到先得分配，
        // 保证任务执行不被辩论阻塞；辩论结果将在下一次 Replan 时体现。
        if (_madGateway != null)
        {
            // 将所有 agentId→slotId 的选择按 slotId 分组，找出被多个 Agent 同时选中的槽
            var slotConflicts = selections
                .GroupBy(kv => kv.Value)   // key=slotId, 组内是所有选了该槽的 agentId
                .Where(g => g.Count() > 1) // 只保留有 2 个及以上 Agent 选了同一槽的冲突组
                .ToList();

            if (slotConflicts.Count > 0)
            {
                // 为 MAD 辩论构建详细的上下文描述：列出每个冲突 Agent 的当前电量，
                // 供辩论参与者（LLM）参考，提出更合理的重新分配建议
                string ctx = string.Join("\n", slotConflicts.SelectMany(g =>
                    g.Select(kv =>
                    {
                        float bat = 0f;
                        var mod = CommunicationManager.Instance?.GetAgentModule(kv.Key); // 获取对应 Agent 的通信模块
                        if (mod != null)
                        {
                            var ia = mod.GetComponent<IntelligentAgent>();
                            if (ia != null) bat = ia.CurrentState?.BatteryLevel ?? 0f; // 读取实时电量
                        }
                        return $"- {kv.Key} selected {kv.Value} (battery={bat:F0}%)"; // 格式：- AgentA selected slot_1 (battery=72%)
                    })));

                // 汇总所有发生冲突的槽 ID，作为辩论 topic 的简要标题
                string conflictedSlots = string.Join(", ",
                    slotConflicts.Select(g => g.Key).Distinct());

                // 发起 MAD 辩论：槽位冲突需要协商，确定最终分配方案
                _madGateway.Raise(new IncidentReport
                {
                    reporterId   = props?.AgentID ?? string.Empty,
                    incidentType = IncidentTypes.SlotConflict,
                    isCritical   = false,
                    description  = "多个 Agent 选择了相同任务槽，需重新分配",
                    context      = $"冲突槽: {conflictedSlots}\n{ctx}",
                });

                Debug.Log($"[PlanningModule] {props?.AgentID} 检测到槽位冲突，已触发 MAD 辩论（仍继续先到先得分配）");
                // 注意：Raise() 是异步的，辩论在后台进行；本函数继续往下执行先到先得分配，不等待辩论完成
            }
        }

        // ── Step 3: 先到先得分配（按 ordered 顺序逐个处理）──────────────────
        foreach (string agentId in ordered)
        {
            if (!selections.TryGetValue(agentId, out string wantedSlotId)) continue; // 该成员未提交选择，跳过

            PlanSlot wanted   = remaining.Find(s => s.slotId == wantedSlotId); // 检查该成员首选槽是否仍在可用池中
            PlanSlot assigned;

            if (wanted != null)
            {
                assigned  = wanted; // 首选槽还在，直接分配给该成员（正常路径）
            }
            else
            {
                // 首选槽已被更高优先级的成员占用；降级分配：取剩余池中的第一个槽
                if (remaining.Count == 0)
                {
                    Debug.LogWarning($"{props?.AgentID ?? "Unknown"}: [PlanningModule] 没有剩余槽可分配给 {agentId}");
                    continue; // 槽池已空，无法分配，跳过该成员（后续需依赖辩论结果 Replan）
                }
                assigned  = remaining[0]; // 降级：取剩余池中排在最前的槽
                Debug.Log($"[PlanningModule] {agentId} 选择的槽 {wantedSlotId} 已被占用,改分配 {assigned.slotId}");
            }

            remaining.Remove(assigned);           // 从可用池移除，后续成员无法再选到该槽
            occupiedSlots.Add(assigned.slotId);   // 记录到已占用集合，供白板约束检查使用
            assignedSlotsByAgent[agentId] = assigned; // 记录最终分配结果，后续构建运行态约束时使用

            if (string.Equals(agentId, props.AgentID, StringComparison.OrdinalIgnoreCase))
            {
                // 组长自身：直接更新本地 confirmedSlot，不需要发消息给自己
                confirmedSlot = assigned;
                Debug.Log($"[PlanningModule] {props.AgentID}(组长)确认槽 {assigned.slotId}");
            }
            else
            {
                // 非组长成员：通过 SlotConfirm 消息通知该成员其最终分配结果
                // reliable=true：确保消息必达，槽确认丢失会导致成员停在等待状态
                comm.SendScopedMessage(
                    CommunicationScope.DirectAgent,
                    MessageType.SlotConfirm,
                    new SlotConfirmPayload
                    {
                        msnId     = parsed.msnId, // 任务 ID，成员用于校验消息属于当前规划轮次
                        agentId   = agentId,       // 接收方 ID（成员侧校验自己才是消息目标）
                        slot      = assigned        // 最终分配到的槽定义（含 slotId、目标点、约束等）
                    },
                    targetAgentId: agentId,
                    reliable: true);
            }
        }

        Debug.Log($"[PlanningModule] {props.AgentID} 最终分槽结果: " +
                  string.Join(", ", assignedSlotsByAgent.Select(kv => $"{kv.Key}->{kv.Value.slotId}")));

        // ── Step 4: 向每个非组长成员发送 StartExecution（按成员逐个发送，非广播）──────
        // 为何不能广播同一份约束？
        // ① 分槽后约束中的抽象引用（slotId / role / desc）需回填成真实 agentId。
        // ② C2（同步约束）的 syncWith 字段：需要从”参与同步的全部成员”中去掉接收者自身，
        //    否则 A 的约束里还包含 A 自己，会导致 A 等自己 → 死锁。
        // ③ C3（等待约束）的 watchAgent 字段：也需要回填成真实被等待 agentId。
        // 因此必须对每个 memberId 单独调用 BuildRuntimeConstraintsForAgent 生成专属约束。
        foreach (string memberId in myGroup.memberIds)
        {
            if (string.Equals(memberId, props.AgentID, StringComparison.OrdinalIgnoreCase)) continue; // 跳过组长自身，组长另行处理

            // 生成该成员视角下的运行态约束：
            // - C3 sign=+1 的 watchAgent 回填为真实被等待 agentId
            // - C2 的 syncWith 回填为真实 agentId 数组，并移除当前接收者自身
            StructuredConstraint[] runtimeConstraints = BuildRuntimeConstraintsForAgent(memberId, assignedSlotsByAgent);

            // 将”最终槽定义 + 该成员专属运行态约束”打包发给成员。
            // 成员侧收到 StartExecution 后先用此 constraints 覆盖本地约束字典，
            // 再进入 LLM#4 / ADM 执行，保证约束已经完全回填，无需再做额外解析。
            // reliable=true：StartExecution 是关键控制消息，丢失会导致成员永远不启动执行。
            comm.SendScopedMessage(
                CommunicationScope.DirectAgent,
                MessageType.StartExecution,
                new StartExecPayload
                {
                    msnId = parsed.msnId,         // 任务 ID，成员侧校验当前消息属于哪一轮规划
                    groupId = myGroup.groupId,    // 组 ID，供成员侧白板分区使用
                    constraints = runtimeConstraints // 该成员专属的已回填运行态约束
                },
                targetAgentId: memberId,
                reliable: true);
        }

        // ── Step 5: 组长自身触发 LLM#4（本地调用，不走消息队列）─────────────────
        // 组长收到自身的 StartExecution 不通过网络发送，而是直接调用 OnStartExec，
        // 确保与成员侧相同的执行逻辑，同时避免自发自收造成的消息顺序问题。
        OnStartExec(new StartExecPayload
        {
            msnId = parsed.msnId,
            groupId = myGroup.groupId,
            constraints = BuildRuntimeConstraintsForAgent(props.AgentID, assignedSlotsByAgent) // 组长自身视角的专属约束
        });

        // ── Step 6: 仅组长初始化 GroupMonitor（MAD 辩论的服务端）────────────────
        // GroupMonitor 是 MAD 辩论的处理入口（HandleIncidentReport），只在组长侧存在。
        // 检查是否已挂载：避免 Replan 时重复 AddComponent 导致多个 GroupMonitor 同时监听。
        if (GetComponent<GroupMonitor>() == null)
        {
            // 收集其他所有组的组长 ID，供 GroupMonitor 跨组通信使用
            string[] otherLeaderIds = allGroups != null
                ? allGroups
                    .Where(g => g.groupId != myGroup.groupId) // 排除自身所在组
                    .Select(g => g.leaderId)
                    .ToArray()
                : Array.Empty<string>();

            var monitor = gameObject.AddComponent<GroupMonitor>(); // 运行时动态挂载，随组长 Agent 的 GameObject 生命周期
            monitor.Initialize(myGroup, myGroup.groupId, myGroup.leaderId,
                               otherLeaderIds, llm); // 注入本组成员列表、组长 ID、LLM 接口
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

    /// <summary>返回本 Agent 所属组的组长 ID（供 MADGateway 路由 IncidentReport 使用）。</summary>
    public string GetLeaderId() => myGroup?.leaderId ?? string.Empty;

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

    // ─────────────────────────────────────────────────────────
    // MAD 决策接口（由 MADDecisionForwarder 调用的最小接口）
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 返回当前计划中尚未执行的步骤（当前步骤及其后所有步骤）。
    /// 由 MADDecisionForwarder 通过 AgentPlanRegistry 调用，用于任务继承场景：
    /// 源 agent 故障时，MADDecisionForwarder 取出其剩余步骤并插入目标 agent 的计划。
    /// </summary>
    public PlanStep[] GetRemainingSteps()
    {
        if (agentPlan?.steps == null || agentPlan.steps.Length == 0)
            return Array.Empty<PlanStep>();

        int cur = Mathf.Max(0, agentPlan.curIdx);
        // curIdx 已超出数组表示所有步骤已完成，返回空数组
        if (cur >= agentPlan.steps.Length)
            return Array.Empty<PlanStep>();

        return agentPlan.steps.Skip(cur).ToArray();
    }

    /// <summary>
    /// 向当前计划中插入步骤。由 MADDecisionForwarder 在处理 operation="insert_steps" 时调用。
    /// <para>immediate=true：在 curIdx+1 处立即插入，接下来就执行（如任务接管）。</para>
    /// <para>immediate=false：追加到 steps 数组末尾，当前步骤执行完后再执行。</para>
    /// 若当前无活跃计划（state != Active），操作静默跳过并打 Warning。
    /// </summary>
    /// <param name="steps">要插入的步骤数组。</param>
    /// <param name="immediate">true=立即插入当前位置后；false=追加到末尾。</param>
    public void InsertSteps(PlanStep[] steps, bool immediate)
    {
        if (steps == null || steps.Length == 0) return;

        if (agentPlan == null || agentPlan.steps == null)
        {
            Debug.LogWarning($"[PlanningModule] {props?.AgentID} InsertSteps：当前无活跃计划，跳过");
            return;
        }

        int insertAt = immediate
            ? Mathf.Min(agentPlan.curIdx + 1, agentPlan.steps.Length)
            : agentPlan.steps.Length;

        agentPlan.steps = agentPlan.steps.Take(insertAt)
            .Concat(steps)
            .Concat(agentPlan.steps.Skip(insertAt))
            .ToArray();

        Debug.Log($"[PlanningModule] {props?.AgentID} 插入 {steps.Length} 个步骤（immediate={immediate}，" +
                  $"位置={insertAt}），总步骤数={agentPlan.steps.Length}");
    }

    /// <summary>
    /// 强制指派槽位，跳过选槽协议，直接进入 LLM#4 步骤拆解阶段。
    /// 由 MADDecisionForwarder 在处理 operation="force_slot" 时调用（用于 MAD 仲裁 slot 冲突）。
    /// 若 startExecReceived 为 true（已收到 StartExec 信号），立即启动 LLM#4；
    /// 否则仅记录 confirmedSlot，等待 StartExec 信号到达后触发。
    /// </summary>
    /// <param name="slot">MAD 仲裁裁决的目标槽位。</param>
    public void ForceAssignSlot(PlanSlot slot)
    {
        if (slot == null)
        {
            Debug.LogWarning($"[PlanningModule] {props?.AgentID} ForceAssignSlot：slot 为 null，跳过");
            return;
        }

        confirmedSlot = slot;
        Debug.Log($"[PlanningModule] {props?.AgentID} MAD 强制指派槽位 {slot.slotId}（{slot.role}）");

        // 若已收到 StartExec 信号，立即启动步骤拆解（同 OnSlotConfirm 的触发逻辑）
        if (startExecReceived)
        {
            Debug.Log($"[PlanningModule] {props?.AgentID} StartExec 已就绪，立即启动 LLM#4");
            StartCoroutine(RunLLM4());
        }
        else
        {
            Debug.Log($"[PlanningModule] {props?.AgentID} 等待 StartExec 信号后启动 LLM#4");
        }
    }

    /// <summary>
    /// 重置规划状态，供 MADDecisionForwarder 在调用 SubmitMissionRequest 之前执行。
    /// 清空当前计划和 busy 标志，确保 SubmitMissionRequest 能正常启动新任务。
    /// 由 MADDecisionForwarder 在处理 operation="new_mission" 时调用。
    /// </summary>
    public void ResetForNewMission()
    {
        Debug.Log($"[PlanningModule] {props?.AgentID} MAD 触发重置，准备执行新任务");
        SetState(PlanningState.Idle);
        busy      = false;
        agentPlan = null;
        confirmedSlot     = null;
        startExecReceived = false;
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
