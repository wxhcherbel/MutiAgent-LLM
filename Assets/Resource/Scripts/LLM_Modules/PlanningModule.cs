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

[Serializable]
public class MissionRole
{
    public RoleType roleType;
    public AgentType agentType;
    public int requiredCount;
    public string[] responsibilities;
}

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
            "relType:\n" +
            "  Cooperation=同队协作  Competition=多队竞争同目标\n" +
            "  Adversarial=多队目标对立  Mixed=多队各有目标\n\n" +
            GetConciseTextPromptText() + "\n" +
            "输出要求:\n" +
            "1. 输出内容仅包含 JSON。\n" +
            "2. relType 从上述枚举中选择。\n" +
            "3. `groupCnt` 为整数,`groupMsns` 长度等于 `groupCnt`。\n" +
            "4. 若 `relType`=`Cooperation`,表示同队协作,全部智能体必须属于同一组,因此 `groupCnt` 必须为 1,`groupMsns` 只能有 1 条。\n" +
            "5. 只有 `Competition`、`Adversarial`、`Mixed` 才允许拆成多个组,且 `groupCnt` 必须大于等于 2。\n" +
            "6. `groupMsns` 中每组任务使用短句,适合直接执行。\n" +
            "7. `timeLimit` 为秒数,无限制时填 0。\n\n" +
            "任务拆解示例:\n" +
            "输入任务:一组无人机协作搜索南区和北区,发现目标后组内共享并统一回传位置\n" +
            "groupMsns 示例:\n" +
            "  \"协作搜索南区和北区并回传目标位置\"\n\n" +
            "输出格式示例:\n" +
            "{\n" +
            "  \"relType\": \"Cooperation\",\n" +
            "  \"groupCnt\": 1,\n" +
            "  \"groupMsns\": [\n" +
            "    \"协作搜索南区和北区并回传目标位置\"\n" +
            "  ],\n" +
            "  \"timeLimit\": 300\n" +
            "}";

        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(prompt, r => llmResult = r, maxTokens: 600));

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
    private IEnumerator RunLLM2()//【提示词待优化】,coordinationConstraint需要和desc区分,desc需要有明确动作和目标,coordinationConstraint应该作为意图来补充（比如：躲避敌方,避开路径冲突..这种没有指定目标的意图）
    {
        SetState(PlanningState.SlotGen);
        int memberCount = myGroup.memberIds.Length;
        string roleTypes = string.Join("、", Enum.GetNames(typeof(RoleType)));
        string prompt =
            "你是多智能体任务组长。为本组生成计划槽 JSON 数组。\n\n" +
            $"组任务:{myGroup.mission}\n" +
            $"成员数:{memberCount}\n\n" +
            GetConciseTextPromptText() + "\n" +
            "输出要求:\n" +
            $"1. 只输出 JSON 数组,长度严格等于 {memberCount}。\n" +
            "2. slotId 唯一,格式 s0、s1 ...\n" +
            $"3. role 从以下枚举选择:{roleTypes}\n" +
            "4. desc 覆盖该成员的完整任务序列(含途径点 + 全部动作),不得遗漏。\n" +
            "5. 若多个成员 role 相同,每个 desc 必须体现各自的具体分工(不同区域/路径/目标),不得完全一致。\n" +
            "6. doneCond:完成条件,如\"限制时间\",没有时填 \" \"。\n" +
            "7. desc 定义：该成员完成的具体任务，包含明确地点、动作和目标序列，只描述本成员自己的行动，不包含与队友的协同关系。\n" +
            "8. coordinationConstraints 是数组，每条包含：\n" +
            "   - trigger（约束生效时机描述，供人阅读，如\"出发阶段\"）\n" +
            "   - slotRef（约束涉及的另一个槽 ID，如\"s1\"；若无则填\"\"）\n" +
            "   - constraint（约束内容）\n" +
            "   如角色独立执行，此字段必须为空数组 []，严禁编造。\n" +
            "9. 禁止将任务行动内容写入 coordinationConstraints；该字段专门描述与队友/敌方的协同关系，不得重复 desc 中的行动内容。\n" +
            "示例(两个无人机从a出发从不同路径前往b巡逻,一个无人机负责通信中继):\n" +
            "[\n" +
            "  {\"slotId\":\"s0\",\"role\":\"Scout\",\"desc\":\"从a出发沿东路飞往b,到达后顺时针巡逻一周\"," +
            "\"doneCond\":\"巡逻完成\",\"coordinationConstraints\":[{\"trigger\":\"出发阶段\",\"slotRef\":\"s1\",\"constraint\":\"与s1选择不同路径(东路vs西路)\"},{\"trigger\":\"到达b后\",\"slotRef\":\"s1\",\"constraint\":\"与s1保持间隔不小于50米\"}]},\n" +
            "  {\"slotId\":\"s1\",\"role\":\"Scout\",\"desc\":\"从a出发沿西路飞往b,到达后逆时针巡逻一周\"," +
            "\"doneCond\":\"巡逻完成\",\"coordinationConstraints\":[{\"trigger\":\"出发阶段\",\"slotRef\":\"s0\",\"constraint\":\"与s0选择不同路径(西路vs东路)\"},{\"trigger\":\"到达b后\",\"slotRef\":\"s0\",\"constraint\":\"与s0保持间隔不小于50米\"}]},\n" +
            "  {\"slotId\":\"s2\",\"role\":\"Supporter\",\"desc\":\"飞至100米高空维持通信中继\"," +
            "\"doneCond\":\"任务结束\",\"coordinationConstraints\":[]}\n" +
            "]\n" +
            "注意：s2 的 coordinationConstraints 为空数组，因为通信中继角色独立执行，不与 s0/s1 存在协同约束。";
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
            Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#2 生成槽位: {string.Join(", ", generatedSlots.Select(s => s.slotId + ":" + s.role + ":" + s.desc + ":" + JsonConvert.SerializeObject(s.coordinationConstraints)))}");
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
            "2. `slotId` 是可选槽中的一个。\n" +
            "3. `reason` 使用一句短句,根据事实说明依据,不要擅自推断,无法判断请写‘无’。\n\n" +
            "输出格式示例:\n" +
            "{\n" +
            "  \"slotId\": \"s0\",\n" +
            "  \"reason\": \"距离近且电量充足\"\n" +
            "}";

        string llmResult = null;
        yield return StartCoroutine(llm.SendRequest(prompt, r => llmResult = r, maxTokens: 150));

        Debug.Log($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#3 原始回复: {llmResult}");


        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"{props?.AgentID ?? "Unknown"}: [PlanningModule] LLM#3 返回空,选第一个槽");
            llmResult = $"{{\"slotId\":\"{availableSlots[0].slotId}\",\"reason\":\"默认\"}}";
        }

        SlotSelectPayload selectPayload = null;
        try
        {
            var jobj = JsonConvert.DeserializeObject<Dictionary<string, string>>(ExtractJson(llmResult));
            selectPayload = new SlotSelectPayload
            {
                msnId   = parsed.msnId,
                agentId = props.AgentID,
                slotId  = jobj.ContainsKey("slotId") ? jobj["slotId"] : availableSlots[0].slotId,
                reason  = jobj.ContainsKey("reason") ? jobj["reason"] : string.Empty
            };
        }
        catch (Exception e)
        {
            Debug.LogError($"[PlanningModule] LLM#3 JSON解析失败: {e.Message}");
            selectPayload = new SlotSelectPayload
            {
                msnId   = parsed.msnId,
                agentId = props.AgentID,
                slotId  = availableSlots[0].slotId,
                reason  = "fallback"
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

        string constraintsJson = JsonConvert.SerializeObject(confirmedSlot.coordinationConstraints ?? new StepConstraint[0]);
        string prompt =
            "你是无人机任务拆解器。将计划拆成 JSON 步骤数组。\n\n" +
            $"计划:{confirmedSlot.desc}\n" +
            $"角色:{confirmedSlot.role}\n" +
            $"步骤级协同约束列表:{constraintsJson}\n" +
            $"完成条件:{confirmedSlot.doneCond}\n" +
            $"当前位置:{pos},电量:{battery:F0}%\n\n" +
            "要求:\n" +
            "1. 只输出 JSON 数组。\n" +
            "2. 只拆解 desc 中明确出现的动作,禁止补充推断步骤。\n" +
            "3. text 只能是意图动作(移动/巡逻/侦查/等待等)。\n" +
            "4. desc 只含一个动作时,输出 1 步。\n" +
            "5. doneCond:完成条件,如\"限制时间\",没有时填 \" \"。\n" +
            "6. stepId 格式:step_1、step_2 ...\n" +
            "7. constraints 字段：从协同约束列表中，判断哪些约束适用于本步骤，将其 constraint 文本放入数组；不适用则填 []。\n" +
            "8. 每条约束只归属最合适的一个步骤，禁止重复绑定。\n\n" +
            "示例A(移动+巡逻，含协同约束):\n" +
            "desc:\"从a出发沿东路飞往b,到达后顺时针巡逻一周\"\n" +
            "协同约束列表:[{\"trigger\":\"出发阶段\",\"slotRef\":\"s1\",\"constraint\":\"与s1选择不同路径(东路vs西路)\"},{\"trigger\":\"到达b后\",\"slotRef\":\"s1\",\"constraint\":\"与s1保持间隔不小于50米\"}]\n" +
            "[\n" +
            "  {\"stepId\":\"step_1\",\"text\":\"从a出发沿东路飞往b\",\"doneCond\":\" \",\"constraints\":[\"与s1选择不同路径(东路vs西路)\"]},\n" +
            "  {\"stepId\":\"step_2\",\"text\":\"顺时针巡逻一周\",\"doneCond\":\"巡逻完成\",\"constraints\":[\"与s1保持间隔不小于50米\"]}\n" +
            "]\n\n" +
            "示例B(独立任务，无协同约束):\n" +
            "desc:\"飞至100米高空维持通信中继\"\n" +
            "协同约束列表:[]\n" +
            "[\n" +
            "  {\"stepId\": \"step_1\", \"text\": \"飞至100米高空\", \"doneCond\": \" \",\"constraints\": []},\n" +
            "  {\"stepId\": \"step_2\", \"text\": \"维持通信中继\", \"doneCond\": \"任务结束\",\"constraints\": []}\n" +
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

        GroupBootstrapPayload bootstrap = new GroupBootstrapPayload
        {
            msnId   = p.msnId,
            relType = p.relType,
            groups  = groups
        };

        // 广播给所有智能体(含自身)
        comm.SendScopedMessage(
            CommunicationScope.Public,
            MessageType.GroupBootstrap,
            bootstrap,
            reliable: true);
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

        Debug.Log($"[PlanningModule] {props.AgentID} 加入组 {myGroup.groupId},isLeader={isLeader}");

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
        Debug.Log($"[PlanningModule] {props.AgentID} 确认槽 {p.slot.slotId}" +
                  (p.adjusted ? $"(调整,原因:{p.adjReason})" : string.Empty));

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
    // 组长专用:冲突处理
    // ─────────────────────────────────────────────────────────

    /// <summary>所有成员选择收齐后解决冲突,按到达时间先到先得,冲突者分配剩余槽。</summary>
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
            bool adjusted;
            string adjReason;
            PlanSlot assigned;

            if (wanted != null)
            {
                assigned  = wanted;
                adjusted  = false;
                adjReason = string.Empty;
            }
            else
            {
                if (remaining.Count == 0)
                {
                    Debug.LogWarning($"{props?.AgentID ?? "Unknown"}: [PlanningModule] 没有剩余槽可分配给 {agentId}");
                    continue;
                }
                assigned  = remaining[0];
                adjusted  = true;
                adjReason = $"{wantedSlotId}已被占用";
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
                        slot      = assigned,
                        adjusted  = adjusted,
                        adjReason = adjReason
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

    /// <summary>由 ADM 在重规划次数耗尽时调用，通知规划层步骤失败。</summary>
    public void OnStepFailed(string stepId, string reason)
    {
        Debug.LogWarning($"[Planning] 步骤 {stepId} 失败：{reason}");
        if (agentPlan == null) return;
        // 跳过当前失败步骤，尝试推进到下一步
        agentPlan.curIdx++;
        if (agentPlan.curIdx >= agentPlan.steps.Length)
        {
            state = PlanningState.Failed;
            busy = false;
            Debug.LogError($"[Planning] 任务 {agentPlan.msnId} 所有步骤失败或完成，终止。");
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
