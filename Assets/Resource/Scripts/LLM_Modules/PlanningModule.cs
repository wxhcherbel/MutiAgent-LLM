// Scripts/Modules/PlanningModule.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;  // 确保 UnityEngine 在 System.Diagnostics 之前
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
// 或者移除不必要的 using System.Diagnostics

[Serializable]
public class Plan
{
    public string mission;           // 总任务描述
    public MissionType missionType;  // 任务类型
    public NavigationPolicy navigationPolicy; // 任务级默认导航策略（由 LLM 输出，step 可覆盖）
    public RoleType agentRole;       // 本智能体在此任务中的角色（使用枚举）
    public string[] steps;           // 具体步骤
    public string[] stepActionTypes; // 每步动作意图（Move/Observe/Communicate/Interact/Idle）
    public string[] stepNavigationModes; // 每步导航模式（AStar/Direct/None）
    public StepIntentDefinition[] stepIntents; // 每步结构化语义意图（目标、经过点、完成条件）
    public RoutePolicyDefinition[] stepRoutePolicies; // 每步结构化路径策略（接近侧、高度、避让对象）
    public TeamCoordinationDirective[] coordinationDirectives; // 本计划附带的多智能体协同约束
    public MissionTaskSlot assignedSlot; // 当前智能体被分配到的具体子任务槽位
    public int currentStep;          // 当前步骤
    public DateTime created;         // 创建时间
    public Priority priority;        // 任务优先级
    public string assignedBy;        // 任务分配者
    public CommunicationMode commMode; // 通信模式
}

[Serializable]
public class MissionAssignment
{
    public string missionId;         // 任务ID
    public string missionDescription;// 任务描述
    public MissionType missionType;  // 任务类型（使用枚举）
    public string coordinatorId;     // 协调者ID
    public MissionRole[] roles;      // 需要的角色分配
    public CommunicationMode communicationMode; // 推荐通信模式
    public int requiredAgentCount;   // 需要智能体数量
    public string teamObjective;     // 队伍级任务目标摘要，便于多智能体统一理解
    public TeamCoordinationDirective[] coordinationDirectives; // 任务级协同规则，例如让行、汇合、走廊预留
    public MissionTaskSlot[] taskSlots; // 任务拆解后的具体子任务槽位列表
}

[Serializable]
public class MissionRole
{
    public RoleType roleType;        // 角色类型（使用枚举）
    public AgentType agentType;      // 需要的智能体类型
    public int requiredCount;        // 需要数量
    public string[] responsibilities;// 职责描述
    public string[] preferredTargets; // 该角色通常优先接触或负责的目标类型/地点
    public string[] coordinationResponsibilities; // 该角色承担的协同职责，例如“领航”“报告瓶颈占用”
}

// 用于解析 LLM 响应的辅助类
[Serializable]
public class PlanResponse
{
    public string assignedRole; // LLM 为当前智能体建议的角色
    public string[] steps; // LLM 输出的自然语言步骤列表
    public string[] stepActionTypes; // 每步动作意图标签
    public string[] stepNavigationModes; // 每步导航模式标签
    public StepIntentDefinition[] stepIntents; // 每步结构化语义意图
    public RoutePolicyDefinition[] stepRoutePolicies; // 每步结构化路径策略
    public TeamCoordinationDirective[] coordinationDirectives; // 当前智能体在此任务中的协同规则
    public string missionNavigationPolicy; // 任务级默认导航策略
    public string[] coordinationNeeds; // 需要与队友同步的关键协同点
    public string reasoning; // LLM 给出的简短规划理由
}

[Serializable]
public class MissionAnalysisResponse
{
    public string missionType; // 任务类型，期望为 MissionType 枚举名
    public string recommendedCommMode; // 通信模式，期望为 CommunicationMode 枚举名
    public MissionRole[] roles; // LLM 给出的角色需求
    public string teamObjective; // 队伍级目标摘要
    public TeamCoordinationDirective[] coordinationDirectives; // 任务级协同约束
    public MissionTaskSlot[] taskSlots; // LLM 直接拆出的 agent-level 槽位
    public string reasoning; // 任务拆解理由
}

[Serializable]
public class MissionPhaseDefinition
{
    public string phaseId; // 阶段唯一ID，例如 phase_1
    public string phaseLabel; // 阶段名称
    public string objective; // 阶段目标
    public int agentBudget; // 该阶段建议投入的智能体规模（用于分解时参考）
    public string[] roleFocus; // 该阶段重点角色
    public string[] dependsOnPhaseIds; // 阶段依赖
    public string syncGroup; // 阶段同步组标识
    public string completionCriteria; // 阶段完成标准
    public string notes; // 阶段备注
}

[Serializable]
public class MissionPhasePlanResponse
{
    public string missionType; // 任务类型，期望为 MissionType 枚举名
    public string recommendedCommMode; // 通信模式，期望为 CommunicationMode 枚举名
    public string teamObjective; // 队伍总目标
    public MissionPhaseDefinition[] phases; // 阶段化分解
    public string reasoning; // 拆解理由
}

public enum Priority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

public class RolePreferenceWrapper
{
    public string missionId; // 当前响应的任务 ID
    public string agentId; // 上报角色偏好的智能体 ID
    public RoleType[] preferences; // 角色偏好列表，按优先级从高到低
    public string capabilitySummary; // 能力摘要，供协调者快速参考
}

public class PlanningModule : MonoBehaviour
{
    private MemoryModule memoryModule;
    private LLMInterface llmInterface;
    private AgentProperties agentProperties;
    private CommunicationModule commModule;

    public Plan currentPlan { get; private set; }
    public MissionAssignment currentMission { get; private set; }
    public CommunicationMode currentCommMode { get; private set; }

    // 只在协调者使用
    public Dictionary<RoleType, int> remainingCount = new(); // 每个角色当前剩余名额
    public Dictionary<string, RoleType[]> receivedPreferences = new(); // 兼容旧逻辑的偏好缓存
    public Dictionary<string, RolePreferencePayload> receivedPreferencePayloads = new(); // 结构化角色偏好缓存
    public Dictionary<string, RoleDecisionPayload> assignedTeamDecisions = new(); // 协调者为每个智能体裁决后的岗位分配结果
    public HashSet<string> acceptedAssignedAgents = new(); // 已确认接受分配的智能体集合
    public HashSet<string> completedAssignedAgents = new(); // 已完成各自子任务槽位的智能体集合
    public HashSet<string> releasedAssignedAgents = new(); // 已经收到执行放行的智能体集合
    private bool missionCompletionAggregated; // 当前任务是否已经由协调者完成全队聚合收口
    private bool localExecutionReleased = true; // 当前智能体本地计划是否已被协调者放行执行
    private bool teamExecutionReleased; // 当前任务是否至少发生过一次执行放行；复杂任务下允许多轮增量放行


    void Start()
    {
        memoryModule = GetComponent<MemoryModule>();
        llmInterface = FindObjectOfType<LLMInterface>();
        
        // 直接获取 IntelligentAgent 组件，然后访问其 Properties
        IntelligentAgent intelligentAgent = GetComponent<IntelligentAgent>();
        if (intelligentAgent != null)
        {
            agentProperties = intelligentAgent.Properties;
        }
        else
        {
            Debug.LogError("未找到 IntelligentAgent 组件");
        }
        
        commModule = GetComponent<CommunicationModule>();
        currentCommMode = CommunicationMode.Hybrid; // 默认混合模式
    }
    
    // 用户提交任务描述，由LLM分析生成任务分配
    public void SubmitMissionRequest(string missionDescription, int agentCount)
    {
        StartCoroutine(AnalyzeMissionDescription(missionDescription, agentCount));
    }

    // 接收中心化任务分配
    public void ReceiveMissionAssignment(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)
    {
        currentMission = mission;
        currentCommMode = mission.communicationMode;

        // 情况一：还没确定角色 —— 只分析角色偏好
        if (specificRole == null)
        {
            StartCoroutine(AnalyzeRolePreference(mission, (preferences) =>
            {
                // 把偏好发给协调者
                SendRolePreferenceToCoordinator(mission, preferences);
            }));
        }
        else
        {
            localExecutionReleased = !ShouldGateExecutionByCoordinator(mission);
            StartCoroutine(AnalyzeMissionAndCreatePlan(mission, specificRole.Value, specificSlot));
        }
        
    }
    private void SendRolePreferenceToCoordinator(MissionAssignment mission, RoleType[] preferences)
    {
        if (commModule == null || mission == null) return;

        RolePreferencePayload payload = new RolePreferencePayload
        {
            missionId = mission.missionId, // 当前任务 ID，便于协调者区分不同任务的偏好
            agentId = agentProperties.AgentID, // 当前上报偏好的智能体 ID
            preferences = preferences, // 当前智能体给出的角色偏好顺序
            agentType = agentProperties.Type, // 当前智能体的平台类型，用于角色适配打分
            currentRole = agentProperties.Role, // 当前已有角色，用于协调者做兼容裁决
            maxSpeed = agentProperties.MaxSpeed, // 当前智能体最大速度，用于衡量机动能力
            perceptionRange = agentProperties.PerceptionRange, // 当前智能体感知范围，用于衡量侦查能力
            capabilitySummary = $"{agentProperties.Role}-{agentProperties.Type}-speed:{agentProperties.MaxSpeed:F1}-sense:{agentProperties.PerceptionRange:F1}" // 便于日志阅读的能力摘要
        };

        commModule.SendStructuredMessage(mission.coordinatorId, MessageType.RolePreference, payload, 1);
    }


    /// <summary>
    /// 把任务拆解阶段的 LLM 响应拼成 MissionAssignment。
    /// 可以把它理解成“收作业”：
    /// - LLM 已经把自然语言任务拆成了角色、槽位、协同规则；
    /// - 这里负责把这些结构化字段装进系统真正使用的任务对象。
    /// 后面动作层最关心的目标信息，主要就躲在 taskSlots 的 target / viaTargets 里。
    /// </summary>
    private MissionAssignment ParseMissionFromLLM(string llmResponse, string description, int agentCount)
    {
        MissionAnalysisResponse analysis = ExtractMissionAnalysisResponse(llmResponse);
        MissionType missionType = ExtractMissionType(analysis);
        CommunicationMode commMode = ExtractCommMode(analysis);
        MissionRole[] roles = ExtractRolesFromResponse(analysis, agentCount);
        MissionTaskSlot[] taskSlots = ExtractTaskSlotsFromResponse(analysis, description, roles, missionType, agentCount);
        roles = ReconcileRolesWithTaskSlots(roles, taskSlots);
        TeamCoordinationDirective[] directives = ExtractMissionCoordinationDirectives(analysis, missionType);
        string teamObjective = !string.IsNullOrWhiteSpace(analysis != null ? analysis.teamObjective : null)
            ? analysis.teamObjective.Trim()
            : description;

        return new MissionAssignment
        {
            missionId = $"mission_{DateTime.Now:yyyyMMdd_HHmmss}",
            missionDescription = description,
            missionType = missionType,
            coordinatorId = agentProperties.AgentID, // 当前智能体作为协调者
            roles = roles,
            communicationMode = commMode,
            requiredAgentCount = agentCount,
            teamObjective = teamObjective,
            coordinationDirectives = directives,
            taskSlots = taskSlots
        };
    }
    // 阶段A：只分析角色偏好，用于提交给协调者
    public IEnumerator AnalyzeRolePreference(MissionAssignment mission,Action<RoleType[]> onResult)
    {
        string prompt = $@"任务分析请求：

        总任务描述：{mission.missionDescription}
        任务类型：{mission.missionType}

        我的属性：
        - 类型：{agentProperties.Type}
        - 当前角色倾向：{agentProperties.Role}
        - 最大速度：{agentProperties.MaxSpeed}
        - 感知范围：{agentProperties.PerceptionRange}

        可用角色：
        ";

            foreach (var role in mission.roles)
            {
                prompt += $@"
        - {role.roleType}（需要 {role.requiredCount} 名，类型 {role.agentType}）";
            }

            prompt += @"

        请只完成一件事：
        给出我最适合的 1~3 个角色，按优先级排序。

        返回 JSON：
        {
        ""preferences"": [""Scout"", ""Assault""]
        }
        ";

        yield return llmInterface.SendRequest(prompt, (result) =>
        {
            Debug.Log($"=== 智能体 {agentProperties.AgentID} 收到 LLM 角色偏好原始响应 ===");
            Debug.Log($"原始响应内容: {result}");
            try
            {
                string json = ExtractPureJson(result);
                //Debug.Log($"提取的 JSON: {json}");
                var pref = JsonConvert.DeserializeObject<RolePreferenceWrapper>(json);
                // 正确打印解析结果
                if (pref != null)
                {
                   //Debug.Log($"解析成功 - 得到 RolePreferenceWrapper 对象");
                    
                    if (pref.preferences != null)
                    {
                       // Debug.Log($"preferences 数组长度: {pref.preferences.Length}");
                        
                        // 遍历并打印每个角色
                        for (int i = 0; i < pref.preferences.Length; i++)
                        {
                            //Debug.Log($"  偏好[{i}]: {pref.preferences[i]} (类型: {pref.preferences[i].GetType().Name})");
                        }
                        
                        //Debug.Log($"角色偏好列表: {string.Join(", ", pref.preferences)}");
                        onResult?.Invoke(pref.preferences);
                    }
                    else
                    {
                        Debug.LogWarning("preferences 数组为 null");
                        onResult?.Invoke(new RoleType[] { agentProperties.Role });
                    }
                }
                else
                {
                    Debug.LogWarning("解析结果为空或无效，使用默认角色");
                    onResult?.Invoke(new RoleType[] { agentProperties.Role });
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"角色偏好解析失败: {e.Message}");
                onResult?.Invoke(new RoleType[] { agentProperties.Role });
            }
        }, temperature: 0.3f, maxTokens: 120);
    }



    /// <summary>
    /// 为已经分配到槽位的单个智能体生成个人计划。
    /// 这一步不再问“总任务是什么”，而是问：
    /// “我这个人，这个槽位，这个最终目标，现在应该分几步去完成？”
    /// 因此 prompt 会强制 LLM 把：
    /// - 最终目标写进 stepIntents.primaryTarget；
    /// - 中间检查点写进 orderedViaTargets。
    /// </summary>
    public IEnumerator AnalyzeMissionAndCreatePlan(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)
    {
        string roleTypeOptions = string.Join("|", Enum.GetNames(typeof(RoleType)));
        string stepIntentTypeOptions = string.Join("|", Enum.GetNames(typeof(StepIntentType)));
        string approachSideOptions = string.Join("|", Enum.GetNames(typeof(RouteApproachSide)));
        string altitudeModeOptions = string.Join("|", Enum.GetNames(typeof(RouteAltitudeMode)));
        string clearanceOptions = string.Join("|", Enum.GetNames(typeof(RouteClearancePreference)));
        string blockedPolicyOptions = string.Join("|", Enum.GetNames(typeof(BlockedPolicyType)));
        string coordinationModeOptions = string.Join("|", Enum.GetNames(typeof(TeamCoordinationMode)));
        string slotSummary = specificSlot != null
            ? $"slotId={specificSlot.slotId},label={specificSlot.slotLabel},role={specificSlot.roleType},target={specificSlot.target},via={(specificSlot.viaTargets != null && specificSlot.viaTargets.Length > 0 ? string.Join("|", specificSlot.viaTargets) : "none")},side={specificSlot.approachSide},altitude={specificSlot.altitudeMode},syncGroup={specificSlot.syncGroup},final={specificSlot.finalBehavior},done={specificSlot.completionCondition}"
            : "none";
        string missionCoordinationSummary = mission != null && mission.coordinationDirectives != null && mission.coordinationDirectives.Length > 0
            ? string.Join(" || ", mission.coordinationDirectives.Where(d => d != null).Select(d => $"mode={d.coordinationMode},leader={d.leaderAgentId},shared={d.sharedTarget},corridor={d.corridorReservationKey},formation={d.formationSlot}").ToArray())
            : "none";

        string prompt = $@"你是多智能体任务规划器，请根据输入给当前智能体生成精简执行计划。

        [输入]
        - mission: {mission.missionDescription}
        - missionType: {mission.missionType}
        - communicationMode: {mission.communicationMode}
        - teamObjective: {mission.teamObjective}
        - selfType: {agentProperties.Type}
        - selfRolePreference: {agentProperties.Role}
        - maxSpeed: {agentProperties.MaxSpeed:F1}
        - perceptionRange: {agentProperties.PerceptionRange:F1}
        - assignedSlot: {slotSummary}
        - missionCoordination: {missionCoordinationSummary}

        [硬性规则]
        1) 只能输出一个JSON对象，不要Markdown，不要解释文字。
        2) assignedRole 只能取: {roleTypeOptions}。
        3) steps 数量 3-5 条；每条一句、简短、可执行，建议 <= 22 字（或 <= 12 英文词）。
        4) stepActionTypes 必须与 steps 等长，每项只能取: Move|Observe|Communicate|Interact|Idle。
        5) stepNavigationModes 必须与 steps 等长，每项只能取: AStar|Direct|None。
        6) 仅当该步骤需要全局路径规划时才标记 AStar；通信/观察步骤标记 None。
        7) stepIntents 与 steps 等长；每个对象至少包含 stepText、intentType、primaryTarget、orderedViaTargets、finalBehavior、completionCondition，其中 intentType 只能取: {stepIntentTypeOptions}。
        8) stepRoutePolicies 与 steps 等长；approachSide 只能取 {approachSideOptions}；altitudeMode 只能取 {altitudeModeOptions}；clearance 只能取 {clearanceOptions}；blockedPolicy 只能取 {blockedPolicyOptions}。
        9) coordinationDirectives 返回 0-2 条，仅保留与当前智能体相关的协同要求，coordinationMode 只能取: {coordinationModeOptions}。
        10) coordinationNeeds 数量 1-3 条；每条简短，建议 <= 20 字（或 <= 10 英文词）。
        11) reasoning 必须很短，<= 40 字（或 <= 20 英文词），禁止换行。
        12) 若信息不足，仍要给出可执行默认步骤，不能返回空数组。
        13) primaryTarget、orderedViaTargets、coordinationDirectives 应优先复用 assignedSlot 和 missionCoordination 中已有的结构化信息，不要重新发明新的任务目标。
        14) 如果 assignedSlot 里有 via=...，则至少在首个移动型 stepIntent 中保留这些 orderedViaTargets，不要丢掉检查点。
        15) 如果 assignedSlot 指定了 side/altitude，则对应移动型 stepRoutePolicy 必须继承这些值，除非有更强的结构化理由覆盖。

        [输出模板]
        {{
        ""assignedRole"": ""Scout"",
        ""steps"": [""步骤1"", ""步骤2"", ""步骤3""],
        ""stepActionTypes"": [""Move"", ""Observe"", ""Move""],
        ""stepNavigationModes"": [""AStar"", ""None"", ""Direct""],
        ""stepIntents"": [
            {{
                ""stepText"": ""步骤1"",
                ""intentType"": ""Navigate"",
                ""primaryTarget"": ""target_main"",
                ""orderedViaTargets"": [""via_point_1""],
                ""avoidTargets"": [],
                ""preferTargets"": [],
                ""requestedTeammateIds"": [],
                ""observationFocus"": ""none"",
                ""communicationGoal"": ""none"",
                ""finalBehavior"": ""arrive"",
                ""completionCondition"": ""到达目标"",
                ""notes"": ""简短说明""
            }}
        ],
        ""stepRoutePolicies"": [
            {{
                ""approachSide"": ""Any"",
                ""altitudeMode"": ""Default"",
                ""clearance"": ""Medium"",
                ""avoidNodeTypes"": [""Vehicle"", ""Pedestrian""],
                ""avoidFeatureNames"": [],
                ""preferFeatureNames"": [],
                ""keepTargetVisible"": true,
                ""preferOpenSpace"": true,
                ""allowGlobalAStar"": true,
                ""allowLocalDetour"": true,
                ""slowNearTarget"": true,
                ""holdForTeammates"": false,
                ""blockedPolicy"": ""ReportAndReplan"",
                ""maxTeammatesInCorridor"": 1,
                ""notes"": ""简短说明""
            }}
        ],
        ""coordinationDirectives"": [
            {{
                ""coordinationMode"": ""{(Enum.GetNames(typeof(TeamCoordinationMode)).Length > 0 ? Enum.GetNames(typeof(TeamCoordinationMode))[0] : "Independent")}"",
                ""leaderAgentId"": """",
                ""sharedTarget"": ""shared_target"",
                ""corridorReservationKey"": """",
                ""yieldToAgentIds"": [],
                ""syncPointTargets"": [],
                ""formationSlot"": """",
                ""notes"": ""简短说明""
            }}
        ],
        ""missionNavigationPolicy"": ""Auto"",
        ""coordinationNeeds"": [""协作点1""],
        ""reasoning"": ""简短理由""
        }}";

        yield return llmInterface.SendRequest(prompt, (result) =>
        {
            try
            {
                ParseAndCreatePlan(result, mission, specificRole, specificSlot);
            }
            catch (Exception e)
            {
                Debug.LogError($"任务分析失败: {e.Message}");
                CreateDefaultPlan(mission, specificRole, specificSlot);
                if (currentPlan != null)
                {
                    SendRoleAcceptance(currentPlan.agentRole.ToString(), "fallback_default_plan");
                }
            }
        }, temperature: 0.2f, maxTokens: 220);
    }

    /// <summary>
    /// 把个人计划阶段的 JSON 真正落成 currentPlan。
    /// 目标链在这里被固定下来：
    /// assignedSlot.target -> stepIntents.primaryTarget -> ActionDecision 的 step_target。
    /// 如果这一步漏掉目标，后面动作层就只能再去猜，稳定性会明显下降。
    /// </summary>
    private void ParseAndCreatePlan(string llmResponse, MissionAssignment mission, RoleType? specificRole, MissionTaskSlot specificSlot)
    {
        RoleType assignedRole = specificRole ?? ExtractRoleTypeFromResponse(llmResponse);
        string[] steps = ExtractStepsFromResponse(llmResponse);
        string reasoning = "LLM分析分配";
        string[] stepActionTypes = ExtractStepActionTypesFromResponse(llmResponse, steps.Length);
        string[] stepNavigationModes = ExtractStepNavigationModesFromResponse(llmResponse, steps.Length);
        StepIntentDefinition[] stepIntents = ExtractStepIntentsFromResponse(llmResponse, steps, stepActionTypes, specificSlot);
        RoutePolicyDefinition[] stepRoutePolicies = ExtractStepRoutePoliciesFromResponse(llmResponse, steps.Length, specificSlot);
        TeamCoordinationDirective[] coordinationDirectives = ExtractCoordinationDirectivesFromResponse(llmResponse);
        NavigationPolicy navPolicy = ExtractMissionNavigationPolicyFromResponse(llmResponse);

        currentPlan = new Plan
        {
            mission = mission.missionDescription,
            missionType = mission.missionType,
            navigationPolicy = navPolicy,
            agentRole = assignedRole,
            steps = steps,
            stepActionTypes = stepActionTypes,
            stepNavigationModes = stepNavigationModes,
            stepIntents = stepIntents,
            stepRoutePolicies = stepRoutePolicies,
            coordinationDirectives = coordinationDirectives,
            assignedSlot = specificSlot,
            currentStep = 0,
            created = DateTime.Now,
            priority = Priority.Normal,
            assignedBy = mission.coordinatorId,
            commMode = mission.communicationMode
        };

        // 打印 currentPlan 的详细内容
        Debug.Log($"=== 智能体 {agentProperties.AgentID} 创建计划详情 ===");
        Debug.Log($"任务描述: {currentPlan.mission}");
        Debug.Log($"任务类型: {currentPlan.missionType}");
        Debug.Log($"导航策略: {currentPlan.navigationPolicy}");
        Debug.Log($"分配角色: {currentPlan.agentRole}");
        Debug.Log($"协调者: {currentPlan.assignedBy}");
        Debug.Log($"通信模式: {currentPlan.commMode}");
        Debug.Log($"优先级: {currentPlan.priority}");
        Debug.Log($"创建时间: {currentPlan.created}");
        Debug.Log($"当前步骤: {currentPlan.currentStep}");
        Debug.Log($"总步骤数: {currentPlan.steps.Length}");
        
        // 打印所有步骤详情
        for (int i = 0; i < currentPlan.steps.Length; i++)
        {
            string intent = (currentPlan.stepActionTypes != null && i < currentPlan.stepActionTypes.Length) ? currentPlan.stepActionTypes[i] : "NA";
            string nav = (currentPlan.stepNavigationModes != null && i < currentPlan.stepNavigationModes.Length) ? currentPlan.stepNavigationModes[i] : "NA";
            string target = "none";
            string via = "none";
            if (currentPlan.stepIntents != null && i < currentPlan.stepIntents.Length && currentPlan.stepIntents[i] != null)
            {
                target = currentPlan.stepIntents[i].primaryTarget;
                via = currentPlan.stepIntents[i].orderedViaTargets != null && currentPlan.stepIntents[i].orderedViaTargets.Length > 0
                    ? string.Join("|", currentPlan.stepIntents[i].orderedViaTargets)
                    : "none";
            }
            Debug.Log($"步骤 {i + 1}: {currentPlan.steps[i]} | intent={intent} | nav={nav} | target={target} | via={via}");
        }
        Debug.Log("=== 计划详情结束 ===");

        // 记录到记忆
        memoryModule.AddMemory($"接受任务：{mission.missionDescription}，担任角色：{assignedRole}，通信模式：{mission.communicationMode}",
                            "mission", 0.9f);

        // 向协调者确认角色接受
        SendRoleAcceptance(assignedRole.ToString(), reasoning);

        //Debug.Log($"智能体{agentProperties.AgentID}接受角色：{assignedRole}，步骤数：{steps.Length}，通信模式：{mission.communicationMode}");
    }

    // 向其他智能体分发任务（协调者功能）
    private void DistributeMissionToAgents(MissionAssignment mission)
    {
        Debug.Log($"DistributeMissionToAgents called by {agentProperties.AgentID}");
        if (commModule == null)
        {
            Debug.LogError("CommunicationModule 未找到，无法分发任务");
            return;
        }

        // 初始化名额
        remainingCount.Clear();
        receivedPreferences.Clear();
        receivedPreferencePayloads.Clear();
        assignedTeamDecisions.Clear();
        acceptedAssignedAgents.Clear();
        completedAssignedAgents.Clear();
        releasedAssignedAgents.Clear();
        missionCompletionAggregated = false;
        teamExecutionReleased = false;
        localExecutionReleased = !ShouldGateExecutionByCoordinator(mission);
        foreach (var role in mission.roles)
        {
            remainingCount[role.roleType] = role.requiredCount;
        }

        if (mission.taskSlots == null || mission.taskSlots.Length == 0)
        {
            mission.taskSlots = BuildTaskSlotsForMission(mission.missionDescription, mission.roles, mission.missionType, mission.requiredAgentCount);
        }

        TaskAnnouncementPayload payload = new TaskAnnouncementPayload
        {
            mission = mission,
            missionDirectives = mission.coordinationDirectives ?? new TeamCoordinationDirective[0],
            briefing = mission.teamObjective
        };

        commModule.SendStructuredMessage("All", MessageType.TaskAnnouncement, payload, 2);

        // 等待智能体提交角色偏好
        StartCoroutine(WaitAndAssignRoles(mission));
    }
    private IEnumerator WaitAndAssignRoles(MissionAssignment mission)
    {
        float waitStart = Time.time;
        float timeout = 15f;
        int expectedSlotCount = mission != null && mission.taskSlots != null && mission.taskSlots.Length > 0
            ? mission.taskSlots.Length
            : Mathf.Max(1, mission != null ? mission.requiredAgentCount : 1);

        while (Time.time - waitStart < timeout)
        {
            int receivedCount = receivedPreferencePayloads.Keys.Union(receivedPreferences.Keys).Count();
            if (receivedCount >= expectedSlotCount)
            {
                break;
            }

            yield return new WaitForSeconds(0.2f);
        }

        HashSet<string> assignedAgents = new HashSet<string>();
        List<(string agentId, MissionRole role)> assignments = new List<(string agentId, MissionRole role)>();

        MissionTaskSlot[] slots = mission.taskSlots ?? new MissionTaskSlot[0];
        if (slots.Length > 0)
        {
            foreach (MissionTaskSlot slot in slots)
            {
                string bestAgentId = FindBestAgentForSlot(slot, assignedAgents);
                if (string.IsNullOrWhiteSpace(bestAgentId)) continue;

                MissionRole slotRole = FindMissionRoleForSlot(slot, mission.roles);
                if (slotRole == null) continue;

                assignments.Add((bestAgentId, slotRole));
                assignedAgents.Add(bestAgentId);
                remainingCount[slotRole.roleType] = Mathf.Max(0, remainingCount[slotRole.roleType] - 1);
                assignedTeamDecisions[bestAgentId] = BuildRoleDecisionPayload(bestAgentId, slotRole, slot, mission);
            }
        }

        if (slots.Length > 0 && assignedTeamDecisions.Count < slots.Length)
        {
            Debug.LogWarning($"[Planning] 任务槽位分配不足，取消启动: assigned={assignedTeamDecisions.Count}, required={slots.Length}, mission={mission.missionDescription}");
            assignedTeamDecisions.Clear();
            yield break;
        }

        foreach ((string agentId, MissionRole role) in assignments)
        {
            if (assignedTeamDecisions.TryGetValue(agentId, out RoleDecisionPayload payload))
            {
                SendFinalRole(payload);
            }
        }

        //Debug.Log("角色裁决完成");
    }
    // 发送最终角色分配给智能体
    private void SendFinalRole(RoleDecisionPayload payload)
    {
        if (commModule == null || payload == null || string.IsNullOrWhiteSpace(payload.agentId)) return;
        Debug.Log($"[Planning] 协调者 {agentProperties.AgentID} 分配槽位 -> agent={payload.agentId}, slot={payload.assignedSlot?.slotLabel}, target={payload.assignedSlot?.target}, side={payload.assignedSlot?.approachSide}, directives={(payload.directives != null ? payload.directives.Length : 0)}");
        commModule.SendStructuredMessage(payload.agentId, MessageType.RoleConfirmed, payload, 1);
    }

    private string FindBestAgentForSlot(MissionTaskSlot slot, HashSet<string> assignedAgents)
    {
        string bestAgentId = string.Empty;
        float bestScore = float.NegativeInfinity;

        IEnumerable<string> candidateIds = receivedPreferencePayloads.Keys.Union(receivedPreferences.Keys);
        foreach (string agentId in candidateIds)
        {
            if (assignedAgents.Contains(agentId)) continue;

            float score = CalculateSlotAssignmentScore(agentId, slot);
            if (score > bestScore)
            {
                bestScore = score;
                bestAgentId = agentId;
            }
        }

        return bestAgentId;
    }

    private float CalculateSlotAssignmentScore(string agentId, MissionTaskSlot slot)
    {
        float score = 0f;

        if (receivedPreferencePayloads.TryGetValue(agentId, out RolePreferencePayload payload) && payload != null)
        {
            if (payload.preferences != null)
            {
                int prefIndex = Array.IndexOf(payload.preferences, slot.roleType);
                if (prefIndex >= 0)
                {
                    score += 100f - prefIndex * 20f;
                }
            }

            if (payload.agentType == slot.requiredAgentType) score += 30f;
            score += Mathf.Clamp(payload.maxSpeed, 0f, 100f) * 0.1f;
            score += Mathf.Clamp(payload.perceptionRange, 0f, 200f) * 0.05f;
        }
        else if (receivedPreferences.TryGetValue(agentId, out RoleType[] prefs) && prefs != null)
        {
            int prefIndex = Array.IndexOf(prefs, slot.roleType);
            if (prefIndex >= 0)
            {
                score += 80f - prefIndex * 15f;
            }
        }

        return score;
    }

    private MissionRole FindMissionRoleForSlot(MissionTaskSlot slot, MissionRole[] roles)
    {
        if (slot == null || roles == null) return null;
        for (int i = 0; i < roles.Length; i++)
        {
            MissionRole role = roles[i];
            if (role == null) continue;
            if (role.roleType == slot.roleType && role.agentType == slot.requiredAgentType)
            {
                return role;
            }
        }
        return roles.Length > 0 ? roles[0] : null;
    }

    private RoleDecisionPayload BuildRoleDecisionPayload(string agentId, MissionRole role, MissionTaskSlot slot, MissionAssignment mission)
    {
        // 这里给每个槽位携带“完整的相关协同指令集合”，而不是只压缩成一条 directive。
        // 复杂任务里同一个智能体可能同时需要遵守：
        // 1) 阶段同步；
        // 2) 走廊预留；
        // 3) 让行；
        // 4) 跟随/领航。
        // 如果这里只保留第一条，动作决策阶段会丢掉大量关键信息。
        TeamCoordinationDirective[] directives = BuildDirectivesForSlot(slot, mission, agentId);
        return new RoleDecisionPayload
        {
            missionId = mission.missionId,
            agentId = agentId,
            assignedRole = role.roleType,
            assignedSlot = slot,
            assignmentReason = $"slot={slot.slotLabel}, role={role.roleType}, side={slot.approachSide}",
            directive = directives != null && directives.Length > 0 ? directives[0] : null,
            directives = directives ?? new TeamCoordinationDirective[0]
        };
    }

    /// <summary>
    /// 为单个槽位构造“当前智能体视角”的协同约束集合。
    /// 设计原则：
    /// 1) 任务级协同规则由上游 LLM 决定，这里不再根据任务文本猜测新的协同语义；
    /// 2) 系统只做最小合法化和槽位上下文补全，例如把 sharedTarget / formationSlot 补到当前槽位上；
    /// 3) 如果任务级没有给任何协同规则，才生成一条最中性的 Independent 指令兜底。
    /// </summary>
    private TeamCoordinationDirective[] BuildDirectivesForSlot(MissionTaskSlot slot, MissionAssignment mission, string agentId)
    {
        List<TeamCoordinationDirective> directives = new List<TeamCoordinationDirective>();
        TeamCoordinationDirective[] missionDirectives = mission != null ? mission.coordinationDirectives : null;

        if (missionDirectives != null)
        {
            for (int i = 0; i < missionDirectives.Length; i++)
            {
                TeamCoordinationDirective src = missionDirectives[i];
                if (src == null) continue;

                TeamCoordinationDirective normalized = NormalizeCoordinationDirective(src);
                TeamCoordinationDirective enriched = new TeamCoordinationDirective
                {
                    coordinationMode = normalized.coordinationMode,
                    leaderAgentId = normalized.leaderAgentId,
                    sharedTarget = !string.IsNullOrWhiteSpace(normalized.sharedTarget)
                        ? normalized.sharedTarget
                        : (!string.IsNullOrWhiteSpace(slot != null ? slot.target : string.Empty)
                            ? slot.target
                            : (mission != null ? mission.teamObjective : string.Empty)),
                    corridorReservationKey = normalized.corridorReservationKey,
                    yieldToAgentIds = normalized.yieldToAgentIds ?? new string[0],
                    syncPointTargets = normalized.syncPointTargets ?? new string[0],
                    formationSlot = !string.IsNullOrWhiteSpace(normalized.formationSlot)
                        ? normalized.formationSlot
                        : (slot != null ? slot.slotLabel : string.Empty),
                    notes = $"{normalized.notes}|agent={agentId}|slot={(slot != null ? slot.slotLabel : "none")}|sync={(slot != null ? slot.syncGroup : "none")}"
                };
                directives.Add(enriched);
            }
        }

        if (directives.Count == 0)
        {
            directives.Add(new TeamCoordinationDirective
            {
                coordinationMode = TeamCoordinationMode.Independent,
                leaderAgentId = string.Empty,
                sharedTarget = !string.IsNullOrWhiteSpace(slot != null ? slot.target : string.Empty)
                    ? slot.target
                    : (mission != null ? mission.teamObjective : string.Empty),
                corridorReservationKey = string.Empty,
                yieldToAgentIds = new string[0],
                syncPointTargets = new string[0],
                formationSlot = slot != null ? slot.slotLabel : string.Empty,
                notes = $"generated|agent={agentId}|slot={(slot != null ? slot.slotLabel : "none")}"
            });
        }

        return directives.ToArray();
    }



    /// <summary>
    /// 从任务分析响应中提取角色列表。
    /// 这里不再根据 missionType 或任务文本做任何关键词推断。
    /// 原因是角色组合本来就应该由 LLM 基于完整任务语义直接给出，系统只负责做最小限度的合法化和兜底。
    /// </summary>
    private MissionRole[] ExtractRolesFromResponse(MissionAnalysisResponse analysis, int totalAgents)
    {
        if (analysis != null && analysis.roles != null && analysis.roles.Length > 0)
        {
            List<MissionRole> normalized = new List<MissionRole>();
            for (int i = 0; i < analysis.roles.Length; i++)
            {
                MissionRole role = analysis.roles[i];
                if (role == null) continue;

                role.requiredCount = Mathf.Max(0, role.requiredCount);
                role.responsibilities = role.responsibilities ?? new string[0];
                role.preferredTargets = role.preferredTargets ?? new string[0];
                role.coordinationResponsibilities = role.coordinationResponsibilities ?? new string[0];
                normalized.Add(role);
            }

            if (normalized.Count > 0) return normalized.ToArray();
        }

        if (analysis != null && analysis.taskSlots != null && analysis.taskSlots.Length > 0)
        {
            MissionRole[] derivedRoles = BuildRolesFromTaskSlots(analysis.taskSlots);
            if (derivedRoles.Length > 0) return derivedRoles;
        }

        // 当任务分析没有稳定返回 roles 时，只保留一个中性兜底。
        // 这个兜底不会根据任务文字猜“该分成侦查/攻击/运输”，避免重新引入狭义词表逻辑。
        return new[]
        {
            CreateRole(RoleType.Supporter, agentProperties != null ? agentProperties.Type : AgentType.Quadcopter, Mathf.Max(1, totalAgents),
                new string[] { "执行任务分配", "与队友协同推进" })
        };
    }

    /// <summary>
    /// 从任务分析响应中提取任务类型。
    /// 只接受结构化字段，不再对原始自然语言做关键词猜测。
    /// </summary>
    private MissionType ExtractMissionType(MissionAnalysisResponse analysis)
    {
        if (analysis != null && Enum.TryParse(analysis.missionType, true, out MissionType missionType))
        {
            return missionType;
        }

        return MissionType.Cooperation;
    }

    /// <summary>
    /// 当 LLM 未返回 roles 但返回了 taskSlots 时，从槽位反推角色需求。
    /// 这能让大规模任务在角色层和槽位层保持一致，避免后续分配阶段只拿到“单角色兜底”。
    /// </summary>
    private MissionRole[] BuildRolesFromTaskSlots(MissionTaskSlot[] taskSlots)
    {
        if (taskSlots == null || taskSlots.Length == 0) return new MissionRole[0];

        var grouped = taskSlots
            .Where(s => s != null)
            .GroupBy(s => new { s.roleType, s.requiredAgentType });

        List<MissionRole> roles = new List<MissionRole>();
        foreach (var g in grouped)
        {
            roles.Add(new MissionRole
            {
                roleType = g.Key.roleType,
                agentType = g.Key.requiredAgentType,
                requiredCount = Mathf.Max(1, g.Count()),
                responsibilities = new string[] { "执行已分配槽位", "按协同约束推进" },
                preferredTargets = new string[0],
                coordinationResponsibilities = new string[0]
            });
        }

        return roles.ToArray();
    }

    /// <summary>
    /// 让 roles 与 taskSlots 保持一致，避免出现“角色人数和槽位人数不匹配”导致的分配歧义。
    /// 优先策略：沿用已有 role 的职责描述，再按槽位统计结果修正 requiredCount。
    /// </summary>
    private MissionRole[] ReconcileRolesWithTaskSlots(MissionRole[] roles, MissionTaskSlot[] taskSlots)
    {
        if (taskSlots == null || taskSlots.Length == 0) return roles ?? new MissionRole[0];

        var groupedSlots = taskSlots
            .Where(s => s != null)
            .GroupBy(s => new { s.roleType, s.requiredAgentType })
            .ToList();

        if (groupedSlots.Count == 0) return roles ?? new MissionRole[0];

        Dictionary<string, MissionRole> roleMap = new Dictionary<string, MissionRole>(StringComparer.OrdinalIgnoreCase);
        if (roles != null)
        {
            for (int i = 0; i < roles.Length; i++)
            {
                MissionRole role = roles[i];
                if (role == null) continue;
                string key = $"{role.roleType}|{role.agentType}";
                roleMap[key] = role;
            }
        }

        List<MissionRole> reconciled = new List<MissionRole>();
        foreach (var g in groupedSlots)
        {
            string key = $"{g.Key.roleType}|{g.Key.requiredAgentType}";
            if (roleMap.TryGetValue(key, out MissionRole existing) && existing != null)
            {
                existing.requiredCount = g.Count();
                existing.responsibilities = existing.responsibilities ?? new string[0];
                existing.preferredTargets = existing.preferredTargets ?? new string[0];
                existing.coordinationResponsibilities = existing.coordinationResponsibilities ?? new string[0];
                reconciled.Add(existing);
            }
            else
            {
                reconciled.Add(new MissionRole
                {
                    roleType = g.Key.roleType,
                    agentType = g.Key.requiredAgentType,
                    requiredCount = g.Count(),
                    responsibilities = new string[] { "执行已分配槽位", "按协同约束推进" },
                    preferredTargets = new string[0],
                    coordinationResponsibilities = new string[0]
                });
            }
        }

        return reconciled.ToArray();
    }

    /// <summary>
    /// 对外接口：获取当前计划的任务级导航策略。
    /// </summary>
    public NavigationPolicy GetCurrentNavigationPolicy()
    {
        return currentPlan != null ? currentPlan.navigationPolicy : NavigationPolicy.Auto;
    }

    /// <summary>
    /// 对外接口：获取当前任务类型。
    /// 优先使用 currentPlan 的 missionType；若计划尚未创建，则回退到 currentMission。
    /// </summary>
    public MissionType GetCurrentMissionType()
    {
        if (currentPlan != null) return currentPlan.missionType;
        if (currentMission != null) return currentMission.missionType;
        return MissionType.Cooperation;
    }

    /// <summary>
    /// 对外接口：获取当前 step 文本。
    /// 若无计划或 step 已结束，返回空字符串。
    /// </summary>
    public string GetCurrentStepDescription()
    {
        if (currentPlan == null || currentPlan.steps == null) return string.Empty;
        if (currentPlan.currentStep < 0 || currentPlan.currentStep >= currentPlan.steps.Length) return string.Empty;
        return currentPlan.steps[currentPlan.currentStep] ?? string.Empty;
    }

    /// <summary>
    /// 对外接口：获取当前 step 的结构化语义意图。
    /// 若计划未提供结构化意图，则只依据结构化 actionType 和 assignedSlot 做中性兜底，
    /// 不再从 step 自然语言文本里猜“这是侦查/通信/支援”。
    /// </summary>
    public StepIntentDefinition GetCurrentStepIntent()
    {
        if (currentPlan == null || currentPlan.steps == null) return null;
        int idx = currentPlan.currentStep;
        if (idx < 0 || idx >= currentPlan.steps.Length) return null;

        if (currentPlan.stepIntents != null && idx < currentPlan.stepIntents.Length && currentPlan.stepIntents[idx] != null)
        {
            return NormalizeStepIntent(currentPlan.stepIntents[idx], currentPlan.steps[idx], GetStepActionTypeHint(idx), currentPlan.assignedSlot);
        }

        return NormalizeStepIntent(null, currentPlan.steps[idx], GetStepActionTypeHint(idx), currentPlan.assignedSlot);
    }

    /// <summary>
    /// 对外接口：获取当前 step 的结构化路径策略。
    /// 若计划未提供，则返回一份默认中性策略。
    /// </summary>
    public RoutePolicyDefinition GetCurrentStepRoutePolicy()
    {
        if (currentPlan == null) return NormalizeRoutePolicy(null);
        int idx = currentPlan.currentStep;
        if (idx < 0) return NormalizeRoutePolicy(null);

        if (currentPlan.stepRoutePolicies != null && idx < currentPlan.stepRoutePolicies.Length && currentPlan.stepRoutePolicies[idx] != null)
        {
            return NormalizeRoutePolicy(currentPlan.stepRoutePolicies[idx]);
        }

        return NormalizeRoutePolicy(null);
    }

    /// <summary>
    /// 对外接口：获取当前计划的协同约束列表。
    /// </summary>
    public TeamCoordinationDirective[] GetCurrentCoordinationDirectives()
    {
        if (currentPlan != null && currentPlan.coordinationDirectives != null)
        {
            return currentPlan.coordinationDirectives;
        }

        if (currentMission != null && currentMission.coordinationDirectives != null)
        {
            return currentMission.coordinationDirectives;
        }

        return new TeamCoordinationDirective[0];
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否是“移动型 step”。
    /// 仅依据 LLM 输出的 stepActionTypes/stepNavigationModes。
    /// </summary>
    public bool IsMovementLikeStep(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;

        string actionType = GetStepActionTypeHint(idx);
        if (actionType == "Move") return true;

        string navMode = GetStepNavigationModeHint(idx);
        return navMode == "AStar" || navMode == "Direct";
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否是“通信或观察型 step”。
    /// 仅依据 LLM 输出的 stepActionTypes。
    /// </summary>
    public bool IsCommunicationOrObservationStep(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;
        string actionType = GetStepActionTypeHint(idx);
        return actionType == "Communicate" || actionType == "Observe";
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否是“局部机动 step”。
    /// 仅依据 LLM 输出的 stepNavigationModes=Direct。
    /// </summary>
    public bool IsLikelyLocalStep(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;
        return GetStepNavigationModeHint(idx) == "Direct";
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否带有“全局导航提示”。
    /// 仅依据 LLM 输出的 stepNavigationModes=AStar。
    /// </summary>
    public bool HasGlobalTargetHint(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;
        return GetStepNavigationModeHint(idx) == "AStar";
    }

    /// <summary>
    /// 对外接口：当前 step 是否建议优先使用 A*。
    /// 仅依据 LLM 输出的 stepNavigationModes，不再进行自然语言关键词推断。
    /// </summary>
    public bool ShouldPreferAStarForStep(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;
        return GetStepNavigationModeHint(idx) == "AStar";
    }

    /// <summary>
    /// 对外接口：基于“当前正在执行的 step”直接判断是否建议 A*。
    /// </summary>
    public bool ShouldPreferAStarForCurrentStep()
    {
        return ShouldPreferAStarForStep(GetCurrentStepDescription());
    }

    /// <summary>
    /// 将 step 文本映射到当前计划中的索引。
    /// </summary>
    private int ResolveStepIndex(string stepText)
    {
        if (currentPlan == null || currentPlan.steps == null || currentPlan.steps.Length == 0) return -1;

        int cur = currentPlan.currentStep;
        if (cur >= 0 && cur < currentPlan.steps.Length)
        {
            string curText = currentPlan.steps[cur] ?? string.Empty;
            if (string.Equals(curText.Trim(), (stepText ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return cur;
            }
        }

        if (!string.IsNullOrWhiteSpace(stepText))
        {
            for (int i = 0; i < currentPlan.steps.Length; i++)
            {
                string s = currentPlan.steps[i] ?? string.Empty;
                if (string.Equals(s.Trim(), stepText.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (cur >= 0 && cur < currentPlan.steps.Length) return cur;
        return -1;
    }

    /// <summary>
    /// 获取某一步的动作意图（Move/Observe/Communicate/Interact/Idle）。
    /// </summary>
    private string GetStepActionTypeHint(int stepIndex)
    {
        if (currentPlan == null || currentPlan.stepActionTypes == null) return "Idle";
        if (stepIndex < 0 || stepIndex >= currentPlan.stepActionTypes.Length) return "Idle";
        return NormalizeStepActionTypeToken(currentPlan.stepActionTypes[stepIndex]);
    }

    /// <summary>
    /// 获取某一步的导航模式（AStar/Direct/None）。
    /// </summary>
    private string GetStepNavigationModeHint(int stepIndex)
    {
        if (currentPlan == null || currentPlan.stepNavigationModes == null) return "None";
        if (stepIndex < 0 || stepIndex >= currentPlan.stepNavigationModes.Length) return "None";
        return NormalizeStepNavigationModeToken(currentPlan.stepNavigationModes[stepIndex]);
    }

    /// <summary>
    /// 从任务分析响应中提取通信模式。
    /// 只信结构化字段 recommendedCommMode，不再在整段文本里用 Contains 扫描关键词。
    /// </summary>
    private CommunicationMode ExtractCommMode(MissionAnalysisResponse analysis)
    {
        if (analysis != null && Enum.TryParse(analysis.recommendedCommMode, true, out CommunicationMode mode))
        {
            return mode;
        }

        return CommunicationMode.Hybrid;
    }

    // 默认计划创建逻辑
    private void CreateDefaultPlan(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)
    {
        RoleType fallbackRole = specificRole ?? (mission != null && mission.roles != null && mission.roles.Length > 0 ? mission.roles[0].roleType : agentProperties.Role);

        // 默认计划基于角色类型
        string[] defaultSteps = fallbackRole switch
        {
            RoleType.Scout => new string[] {
                "起飞并进入任务高度",
                "扫描任务区域环境",
                "识别和标记关键目标",
                "持续报告环境信息",
                "任务完成后返回"
            },
            RoleType.Assault => new string[] {
                "检查设备和武器系统",
                "向目标区域快速移动",
                "执行攻击或突破任务",
                "保持战术队形",
                "任务完成后撤离"
            },
            RoleType.Defender => new string[] {
                "建立防御阵地",
                "监控周围环境",
                "拦截威胁目标",
                "保护重要区域",
                "维持防御状态"
            },
            RoleType.Transporter => new string[] {
                "检查载货状态",
                "规划运输路线",
                "向目的地移动",
                "执行装卸操作",
                "返回基地或下一个任务点"
            },
            _ => new string[] { "待命", "等待具体指令", "执行分配任务" }
        };

        currentPlan = new Plan
        {
            mission = mission.missionDescription,
            missionType = mission.missionType,
            navigationPolicy = NavigationPolicy.Auto,
            agentRole = fallbackRole,
            steps = defaultSteps,
            stepActionTypes = BuildFilledArray(defaultSteps.Length, "Move"),
            stepNavigationModes = BuildFilledArray(defaultSteps.Length, "Direct"),
            stepIntents = BuildFallbackStepIntents(defaultSteps, BuildFilledArray(defaultSteps.Length, "Move"), specificSlot),
            stepRoutePolicies = BuildFallbackRoutePolicies(defaultSteps.Length, specificSlot),
            coordinationDirectives = mission.coordinationDirectives ?? new TeamCoordinationDirective[0],
            assignedSlot = specificSlot,
            currentStep = 0,
            created = DateTime.Now,
            priority = Priority.Normal,
            assignedBy = mission.coordinatorId,
            commMode = mission.communicationMode
        };
    }

    /// <summary>
    /// 从用户自然语言任务出发，先拆阶段，再拆成单智能体槽位。
    /// 用高中生能懂的话说：
    /// 1) 第 1 段回答“这件事大概分哪几段”；
    /// 2) 第 2 段回答“每个智能体最后到底要去哪里、先经过哪里”；
    /// 3) 系统后面真正认目标，不再直接读用户原话，而是优先读这里产出的 slot.target 和 viaTargets。
    /// </summary>
    public IEnumerator AnalyzeMissionDescription(string description, int agentCount)
    {
        string missionTypeOptions = string.Join("|", Enum.GetNames(typeof(MissionType)));
        string commModeOptions = string.Join("|", Enum.GetNames(typeof(CommunicationMode)));
        string roleTypeOptions = string.Join("|", Enum.GetNames(typeof(RoleType)));
        string agentTypeOptions = string.Join("|", Enum.GetNames(typeof(AgentType)));
        string coordinationModeOptions = string.Join("|", Enum.GetNames(typeof(TeamCoordinationMode)));
        string approachSideOptions = string.Join("|", Enum.GetNames(typeof(RouteApproachSide)));
        string altitudeModeOptions = string.Join("|", Enum.GetNames(typeof(RouteAltitudeMode)));

        // 第1段只负责把自然语言压缩成“阶段结构”，不给 LLM 扯到槽位和单体执行细节的空间。
        // 这样第1段的目标非常单一：输出稳定、短小、能被第2段继续消费的 phasePlan。
        string phasePrompt = 
        $@"第一阶段：把自然语言任务结构化成 phasePlan，只返回一个 JSON 对象。
        输入:
        - missionDescription: {description}
        - agentCount: {agentCount}

        允许的枚举值:
        - missionType: {missionTypeOptions}
        - recommendedCommMode: {commModeOptions}
        - roleFocus[]: {roleTypeOptions}

        返回 JSON:
        {{
        ""missionType"": ""Cooperation"",
        ""recommendedCommMode"": ""Hybrid"",
        ""teamObjective"": ""一句话总目标"",
        ""phases"": [
            {{
            ""phaseId"": ""phase_1"",
            ""phaseLabel"": ""阶段1"",
            ""objective"": ""这一阶段要完成什么"",
            ""agentBudget"": 1,
            ""roleFocus"": [""Supporter""],
            ""dependsOnPhaseIds"": [],
            ""syncGroup"": ""phase_1"",
            ""completionCriteria"": ""怎样算阶段完成"",
            ""notes"": ""补充说明，没有可写空字符串""
            }}
        ],
        ""reasoning"": ""简短拆解理由""
        }}

        要求:
        1. phases 必须非空。
        2. phaseId 必须唯一。
        3. dependsOnPhaseIds 只能引用 phases 中已有的 phaseId。
        4. agentBudget 必须 >= 1 且 <= {agentCount}。
        5. roleFocus 必须非空。
        6. phases 要覆盖完整任务，不要只写一个过于笼统的大阶段。
        7. 只能返回一个 JSON 对象。";

        string phaseResultRaw = string.Empty;
        yield return llmInterface.SendRequest(phasePrompt, (result) =>
        {
            phaseResultRaw = result;
        }, temperature: 0.1f, maxTokens: 1200);

        // 打印第1阶段原始返回，方便直接核对 LLM 是否按 phasePlan schema 输出。
        Debug.Log($"[Planning] 第1阶段 LLM 原始返回:\n{phaseResultRaw}");

        MissionPhasePlanResponse phasePlan = ExtractMissionPhasePlan(phaseResultRaw, description, agentCount);
        string phasePlanJson = JsonConvert.SerializeObject(phasePlan, Formatting.None);
        Debug.Log($"[Planning] 第1阶段解析摘要: missionType={phasePlan.missionType}, commMode={phasePlan.recommendedCommMode}, phaseCount={(phasePlan.phases != null ? phasePlan.phases.Length : 0)}, phasePlan={phasePlanJson}");

        // 第2段只做“阶段 -> 槽位”分配。
        // 关键约束：
        // 1) 当前调度器是“一人一个端到端槽位”，不是“一个 phase 一个槽位”；
        // 2) LLM 必须把 phasePlan 压缩成 agent-level end-to-end assignment；
        // 3) 检查点/前置点应该进 viaTargets，最终任务目标必须进 target。
        // 否则就会出现“系统把检查点当终点”的结构性错误。
        string decompositionPrompt = 
        $@"第二阶段：基于 missionDescription 和 phasePlan 生成 agent-level taskSlots。
        每个 taskSlot 对应 1 个智能体的端到端完整职责，只返回一个 JSON 对象。

        输入:
        - missionDescription: {description}
        - agentCount: {agentCount}
        - phasePlan: {phasePlanJson}

        允许的枚举值:
        - missionType: {missionTypeOptions}
        - recommendedCommMode: {commModeOptions}
        - coordinationMode: {coordinationModeOptions}
        - roleType: {roleTypeOptions}
        - requiredAgentType: {agentTypeOptions}
        - approachSide: {approachSideOptions}
        - altitudeMode: {altitudeModeOptions}

        返回 JSON:
        {{
        ""missionType"": ""Cooperation"",
        ""recommendedCommMode"": ""Hybrid"",
        ""teamObjective"": ""一句话总目标"",
        ""coordinationDirectives"": [
            {{
            ""coordinationMode"": ""LooseSync"",
            ""leaderAgentId"": """",
            ""sharedTarget"": ""shared_target"",
            ""corridorReservationKey"": """",
            ""yieldToAgentIds"": [],
            ""syncPointTargets"": [],
            ""formationSlot"": """",
            ""notes"": ""任务级协同约束，没有可写空字符串""
            }}
        ],
        ""taskSlots"": [
            {{
            ""slotId"": ""slot_1"",
            ""slotLabel"": ""槽位名称"",
            ""roleType"": ""Supporter"",
            ""requiredAgentType"": ""Quadcopter"",
            ""target"": ""该智能体最终要作用的目标"",
            ""viaTargets"": [],
            ""approachSide"": ""Any"",
            ""altitudeMode"": ""Default"",
            ""syncGroup"": ""phase_1"",
            ""dependsOnSlotIds"": [],
            ""finalBehavior"": ""arrive"",
            ""completionCondition"": ""怎样算这个槽位完成"",
            ""notes"": ""补充说明，没有可写空字符串""
            }}
        ],
        ""reasoning"": ""简短拆解理由""
        }}

        字段要求:
        - coordinationDirectives: 任务级协同约束列表；没有协同约束就返回 []。
        - taskSlots: 单智能体粒度槽位列表；每个槽位只分给 1 个智能体，并覆盖该智能体从起步到核心任务完成的完整职责。
        - syncGroup: 用来表达并行/同步批次，应该与 phasePlan 中的阶段对应。
        - dependsOnSlotIds: 用来表达槽位级先后依赖。
        - finalBehavior: 该槽位完成前的最终动作形态，例如 arrive/observe/hold/interact/report。
        - completionCondition: 必须可验证，不能写成模糊描述。

        要求:
        1. taskSlots 数量必须等于 {agentCount}。
        2. slotId 必须唯一。
        3. dependsOnSlotIds 只能引用 taskSlots 中已有的 slotId。
        4. 不要为同一个智能体按 phase 拆出多个槽位；phasePlan 只用于帮助你组织 end-to-end 槽位。
        5. 如果任务要求“先经过A再接近B”，必须写成 target=B, viaTargets=[A]，不要把 A 写成 target。
        6. 如果 phasePlan 里有起飞、侦查准备、接近、侦查执行、撤离等多个阶段，要把这些阶段压缩进同一个智能体槽位，而不是生成 phase_1/phase_2/phase_3 多个槽位。
        7. target 必须是该智能体此轮任务真正要作用/观察/交互的最终目标；中间检查点、观察位、过渡点只能放进 viaTargets。
        8. syncGroup 和 dependsOnSlotIds 要与 phasePlan 的阶段关系一致，但不能因此增加槽位数量。
        9. coordinationDirectives 字段必须存在，可为空数组。
        10. 只能返回一个 JSON 对象。";

        string decompositionResultRaw = string.Empty;
        yield return llmInterface.SendRequest(decompositionPrompt, (result) =>
        {
            decompositionResultRaw = result;
        }, temperature: 0.1f, maxTokens: 2400);

        // 打印第2阶段原始返回，方便核对 taskSlots / coordinationDirectives 是否满足约束。
        Debug.Log($"[Planning] 第2阶段 LLM 原始返回:\n{decompositionResultRaw}");

        try
        {
            MissionAssignment mission = ParseMissionFromLLM(decompositionResultRaw, description, agentCount);
            ApplyPhasePlanHints(mission, phasePlan, description);
            Debug.Log($"[Planning] 第2阶段解析摘要: missionType={mission.missionType}, commMode={mission.communicationMode}, roleCount={(mission.roles != null ? mission.roles.Length : 0)}, slotCount={(mission.taskSlots != null ? mission.taskSlots.Length : 0)}, directiveCount={(mission.coordinationDirectives != null ? mission.coordinationDirectives.Length : 0)}");

            Debug.Log($"{agentProperties.AgentID} 成为任务协调者");
            mission.coordinatorId = agentProperties.AgentID;
            DistributeMissionToAgents(mission);
            ReceiveMissionAssignment(mission);
        }
        catch (Exception e)
        {
            Debug.LogError($"任务分析失败: {e.Message}\n第1阶段原始返回:\n{phaseResultRaw}\n第2阶段原始返回:\n{decompositionResultRaw}");
            CreateDefaultMission(description, agentCount);
        }
    }

    /// <summary>
    /// 解析第1段 phase 规划结果；若解析失败或关键字段缺失，退回最小可执行 phase 兜底。
    /// </summary>
    private MissionPhasePlanResponse ExtractMissionPhasePlan(string response, string description, int agentCount)
    {
        if (TryParseMissionPhasePlan(response, out MissionPhasePlanResponse phasePlan, out string error))
        {
            return NormalizePhasePlan(phasePlan, description, agentCount);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Debug.LogWarning($"phase plan 解析失败，使用默认阶段计划: {error}");
        }

        return BuildFallbackPhasePlan(description, agentCount);
    }

    /// <summary>
    /// 尝试解析 phase 级任务骨架 JSON。
    /// 支持 root 或 result 包装对象，兼容轻微字段名差异。
    /// </summary>
    private bool TryParseMissionPhasePlan(string response, out MissionPhasePlanResponse phasePlan, out string error)
    {
        phasePlan = null;
        error = string.Empty;

        string jsonContent = ExtractPureJson(response);
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            error = "phase plan 响应为空";
            return false;
        }

        try
        {
            if (!jsonContent.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                error = "phase plan 不是 JSON 对象";
                return false;
            }

            JObject root = JObject.Parse(jsonContent);
            JObject obj = root;
            if (root["result"] is JObject wrappedResult)
            {
                obj = wrappedResult;
            }

            phasePlan = new MissionPhasePlanResponse
            {
                missionType = ReadStringField(obj, "missionType", "type"),
                recommendedCommMode = ReadStringField(obj, "recommendedCommMode", "communicationMode", "commMode"),
                teamObjective = ReadStringField(obj, "teamObjective", "objective", "teamGoal"),
                phases = ReadMissionPhasesWithAliases(obj, "phases", "phasePlan"),
                reasoning = ReadStringField(obj, "reasoning")
            };

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取 phases 数组并兼容字段别名，减少复杂任务时因为字段轻微漂移导致的全量失败。
    /// </summary>
    private static MissionPhaseDefinition[] ReadMissionPhasesWithAliases(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return new MissionPhaseDefinition[0];

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!(obj[key] is JArray arr) || arr.Count == 0) continue;

            List<MissionPhaseDefinition> phases = new List<MissionPhaseDefinition>();
            for (int j = 0; j < arr.Count; j++)
            {
                if (!(arr[j] is JObject phaseObj)) continue;

                int budget = 0;
                JToken budgetToken = phaseObj["agentBudget"] ?? phaseObj["agentCount"] ?? phaseObj["budget"];
                if (budgetToken != null && budgetToken.Type != JTokenType.Null)
                {
                    int.TryParse(budgetToken.ToString(), out budget);
                }

                phases.Add(new MissionPhaseDefinition
                {
                    phaseId = ReadStringField(phaseObj, "phaseId", "id"),
                    phaseLabel = ReadStringField(phaseObj, "phaseLabel", "label", "name"),
                    objective = ReadStringField(phaseObj, "objective", "goal"),
                    agentBudget = budget,
                    roleFocus = ReadStringArrayField(phaseObj, "roleFocus", "roles"),
                    dependsOnPhaseIds = ReadStringArrayField(phaseObj, "dependsOnPhaseIds", "dependsOn"),
                    syncGroup = ReadStringField(phaseObj, "syncGroup", "group"),
                    completionCriteria = ReadStringField(phaseObj, "completionCriteria", "doneWhen"),
                    notes = ReadStringField(phaseObj, "notes", "note")
                });
            }

            if (phases.Count > 0) return phases.ToArray();
        }

        return new MissionPhaseDefinition[0];
    }

    /// <summary>
    /// 对 phase plan 做执行前规范化：
    /// - missionType / commMode 缺失回退默认值；
    /// - phaseId 唯一化；
    /// - 依赖关系清洗；
    /// - agentBudget 范围规范到 [1, agentCount]。
    /// </summary>
    private MissionPhasePlanResponse NormalizePhasePlan(MissionPhasePlanResponse src, string description, int agentCount)
    {
        MissionPhasePlanResponse plan = src ?? new MissionPhasePlanResponse();
        plan.missionType = string.IsNullOrWhiteSpace(plan.missionType) ? MissionType.Cooperation.ToString() : plan.missionType.Trim();
        plan.recommendedCommMode = string.IsNullOrWhiteSpace(plan.recommendedCommMode) ? CommunicationMode.Hybrid.ToString() : plan.recommendedCommMode.Trim();
        plan.teamObjective = string.IsNullOrWhiteSpace(plan.teamObjective) ? description : plan.teamObjective.Trim();

        MissionPhaseDefinition[] rawPhases = plan.phases ?? new MissionPhaseDefinition[0];
        if (rawPhases.Length == 0)
        {
            return BuildFallbackPhasePlan(description, agentCount);
        }

        int capacity = Mathf.Max(1, agentCount);
        List<MissionPhaseDefinition> normalized = new List<MissionPhaseDefinition>();
        HashSet<string> usedPhaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rawPhases.Length; i++)
        {
            MissionPhaseDefinition p = rawPhases[i] ?? new MissionPhaseDefinition();
            string baseId = string.IsNullOrWhiteSpace(p.phaseId) ? $"phase_{i + 1}" : p.phaseId.Trim();
            string uniqueId = baseId;
            int suffix = 2;
            while (usedPhaseIds.Contains(uniqueId))
            {
                uniqueId = $"{baseId}_{suffix}";
                suffix++;
            }

            p.phaseId = uniqueId;
            p.phaseLabel = string.IsNullOrWhiteSpace(p.phaseLabel) ? uniqueId : p.phaseLabel.Trim();
            p.objective = string.IsNullOrWhiteSpace(p.objective) ? $"完成 {p.phaseLabel}" : p.objective.Trim();
            p.agentBudget = Mathf.Clamp(p.agentBudget <= 0 ? capacity : p.agentBudget, 1, capacity);
            p.roleFocus = (p.roleFocus == null || p.roleFocus.Length == 0) ? new string[] { RoleType.Supporter.ToString() } : p.roleFocus;
            p.dependsOnPhaseIds = p.dependsOnPhaseIds ?? new string[0];
            p.syncGroup = string.IsNullOrWhiteSpace(p.syncGroup) ? p.phaseId : p.syncGroup.Trim();
            p.completionCriteria = string.IsNullOrWhiteSpace(p.completionCriteria) ? $"完成阶段 {p.phaseLabel}" : p.completionCriteria.Trim();
            p.notes = string.IsNullOrWhiteSpace(p.notes) ? "normalized_phase_plan" : p.notes.Trim();

            usedPhaseIds.Add(uniqueId);
            normalized.Add(p);
        }

        HashSet<string> validPhaseIds = new HashSet<string>(normalized.Select(p => p.phaseId), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < normalized.Count; i++)
        {
            normalized[i].dependsOnPhaseIds = normalized[i].dependsOnPhaseIds
                .Where(d => !string.IsNullOrWhiteSpace(d) && validPhaseIds.Contains(d.Trim()))
                .Select(d => d.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        plan.phases = normalized.ToArray();
        return plan;
    }

    private MissionPhasePlanResponse BuildFallbackPhasePlan(string description, int agentCount)
    {
        int capacity = Mathf.Max(1, agentCount);
        return new MissionPhasePlanResponse
        {
            missionType = MissionType.Cooperation.ToString(),
            recommendedCommMode = CommunicationMode.Hybrid.ToString(),
            teamObjective = description,
            phases = new[]
            {
                new MissionPhaseDefinition
                {
                    phaseId = "phase_1",
                    phaseLabel = "phase_1",
                    objective = description,
                    agentBudget = capacity,
                    roleFocus = new[] { RoleType.Supporter.ToString() },
                    dependsOnPhaseIds = new string[0],
                    syncGroup = "phase_1",
                    completionCriteria = "完成任务目标",
                    notes = "fallback_phase_plan"
                }
            },
            reasoning = "fallback_phase_plan"
        };
    }

    /// <summary>
    /// 将第1段 phase 规划的关键信息回填到最终任务对象，保证两段结果语义一致。
    /// </summary>
    private void ApplyPhasePlanHints(MissionAssignment mission, MissionPhasePlanResponse phasePlan, string description)
    {
        if (mission == null || phasePlan == null) return;

        if (Enum.TryParse(phasePlan.missionType, true, out MissionType missionType))
        {
            mission.missionType = missionType;
        }
        if (Enum.TryParse(phasePlan.recommendedCommMode, true, out CommunicationMode commMode))
        {
            mission.communicationMode = commMode;
        }
        if (!string.IsNullOrWhiteSpace(phasePlan.teamObjective))
        {
            mission.teamObjective = phasePlan.teamObjective.Trim();
        }
        else if (string.IsNullOrWhiteSpace(mission.teamObjective))
        {
            mission.teamObjective = description;
        }
    }

    // 从LLM响应中提取步骤
    private string[] ExtractStepsFromResponse(string response)
    {
        try
        {
            string parsedJson;
            string parseError;
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out parsedJson, out parseError))
            {
                if (planResponse != null && planResponse.steps != null && planResponse.steps.Length > 0)
                {
                    Debug.Log($"成功解析出 {planResponse.steps.Length} 个步骤");
                    return planResponse.steps;
                }
            }
            else if (!string.IsNullOrWhiteSpace(parseError))
            {
                Debug.LogWarning($"步骤解析未命中结构化JSON，原因: {parseError}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"步骤解析失败: {e.Message}");
        }
        
        // 如果解析失败，返回默认步骤
        return new string[] { "分析环境", "执行任务", "报告状态" };
    }

    /// <summary>
    /// 从 LLM 响应提取每步动作意图（由 LLM 明确给出，不做自然语言关键词判断）。
    /// </summary>
    private string[] ExtractStepActionTypesFromResponse(string response, int stepCount)
    {
        int n = Mathf.Max(0, stepCount);
        string[] fallback = BuildFilledArray(n, "Move");
        if (n == 0) return fallback;

        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _))
            {
                if (planResponse != null && planResponse.stepActionTypes != null && planResponse.stepActionTypes.Length > 0)
                {
                    string[] raw = planResponse.stepActionTypes;
                    string[] result = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        string token = i < raw.Length ? raw[i] : raw[raw.Length - 1];
                        result[i] = NormalizeStepActionTypeToken(token);
                    }
                    return result;
                }
            }
        }
        catch { }

        return fallback;
    }

    /// <summary>
    /// 从 LLM 响应提取每步导航模式（由 LLM 明确给出，不做自然语言关键词判断）。
    /// </summary>
    private string[] ExtractStepNavigationModesFromResponse(string response, int stepCount)
    {
        int n = Mathf.Max(0, stepCount);
        string[] fallback = BuildFilledArray(n, "Direct");
        if (n == 0) return fallback;

        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _))
            {
                if (planResponse != null && planResponse.stepNavigationModes != null && planResponse.stepNavigationModes.Length > 0)
                {
                    string[] raw = planResponse.stepNavigationModes;
                    string[] result = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        string token = i < raw.Length ? raw[i] : raw[raw.Length - 1];
                        result[i] = NormalizeStepNavigationModeToken(token);
                    }
                    return result;
                }
            }
        }
        catch { }

        return fallback;
    }

    /// <summary>
    /// 任务级导航策略由 LLM 给出；若缺失则统一回退 Auto。
    /// </summary>
    private NavigationPolicy ExtractMissionNavigationPolicyFromResponse(string response)
    {
        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _))
            {
                string token = planResponse != null ? planResponse.missionNavigationPolicy : string.Empty;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    string n = token.Trim().ToLowerInvariant();
                    if (n == "preferglobalastar" || n == "globalastar" || n == "astar") return NavigationPolicy.PreferGlobalAStar;
                    if (n == "preferlocal" || n == "local" || n == "direct") return NavigationPolicy.PreferLocal;
                    if (n == "auto") return NavigationPolicy.Auto;
                }
            }
        }
        catch { }

        return NavigationPolicy.Auto;
    }

    /// <summary>
    /// 从 LLM 响应提取每一步的结构化语义意图。
    /// 这一步在回答一个很关键的问题：
    /// “当前这一步真正要对哪个目标做事？”
    /// 优先用 LLM 明确给出的 primaryTarget；
    /// 如果 LLM 没写清楚，就退回到 assignedSlot.target，
    /// 然后再做一次一致性修复，避免把中间检查点误当成最终目标。
    /// </summary>
    private StepIntentDefinition[] ExtractStepIntentsFromResponse(string response, string[] steps, string[] stepActionTypes, MissionTaskSlot assignedSlot)
    {
        int n = steps != null ? steps.Length : 0;
        StepIntentDefinition[] fallback = BuildFallbackStepIntents(steps, stepActionTypes, assignedSlot);
        if (n == 0) return fallback;

        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _) &&
                planResponse != null &&
                planResponse.stepIntents != null &&
                planResponse.stepIntents.Length > 0)
            {
                StepIntentDefinition[] result = new StepIntentDefinition[n];
                for (int i = 0; i < n; i++)
                {
                    StepIntentDefinition src = i < planResponse.stepIntents.Length ? planResponse.stepIntents[i] : planResponse.stepIntents[planResponse.stepIntents.Length - 1];
                    string actionTypeHint = stepActionTypes != null && i < stepActionTypes.Length ? stepActionTypes[i] : "Idle";
                    result[i] = NormalizeStepIntent(src, steps[i], actionTypeHint, assignedSlot);
                }
                return RepairStepIntentsAgainstAssignedSlot(result, steps, stepActionTypes, assignedSlot);
            }
        }
        catch { }

        return RepairStepIntentsAgainstAssignedSlot(fallback, steps, stepActionTypes, assignedSlot);
    }

    /// <summary>
    /// 从 LLM 响应提取每步结构化路径策略。
    /// 若缺失，则补一份完全中性的默认策略。
    /// 这里同样不再通过 step 文本去猜“应该贴边飞/应该绕建筑/应该保持可视”。
    /// </summary>
    private RoutePolicyDefinition[] ExtractStepRoutePoliciesFromResponse(string response, int stepCount, MissionTaskSlot assignedSlot)
    {
        int n = Mathf.Max(0, stepCount);
        RoutePolicyDefinition[] fallback = BuildFallbackRoutePolicies(n, assignedSlot);
        if (n == 0) return fallback;

        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _) &&
                planResponse != null &&
                planResponse.stepRoutePolicies != null &&
                planResponse.stepRoutePolicies.Length > 0)
            {
                RoutePolicyDefinition[] result = new RoutePolicyDefinition[n];
                for (int i = 0; i < n; i++)
                {
                    RoutePolicyDefinition src = i < planResponse.stepRoutePolicies.Length
                        ? planResponse.stepRoutePolicies[i]
                        : planResponse.stepRoutePolicies[planResponse.stepRoutePolicies.Length - 1];
                    result[i] = MergeRoutePolicyWithAssignedSlot(src, assignedSlot);
                }
                return result;
            }
        }
        catch { }

        return fallback;
    }

    /// <summary>
    /// 从 LLM 响应提取当前智能体的协同约束。
    /// 若未返回，则回退到任务级协同指令。
    /// 这里不从自然语言 step 或 mission 文本里再猜新的协同关系。
    /// </summary>
    private TeamCoordinationDirective[] ExtractCoordinationDirectivesFromResponse(string response)
    {
        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _) &&
                planResponse != null &&
                planResponse.coordinationDirectives != null &&
                planResponse.coordinationDirectives.Length > 0)
            {
                return planResponse.coordinationDirectives
                    .Where(d => d != null)
                    .Select(NormalizeCoordinationDirective)
                    .ToArray();
            }
        }
        catch { }

        if (currentMission != null && currentMission.coordinationDirectives != null)
        {
            return currentMission.coordinationDirectives
                .Where(d => d != null)
                .Select(NormalizeCoordinationDirective)
                .ToArray();
        }

        return new TeamCoordinationDirective[0];
    }

    /// <summary>
    /// 当 LLM 没有稳定返回 stepIntents 时，系统自己补一份最保守的结构化意图。
    /// 这里故意不从 step 文本里猜目标，而是尽量直接复用 assignedSlot 里已经确定的 target 和 viaTargets。
    /// 这样虽然不聪明，但能避免“系统脑补错目标”。
    /// </summary>
    private StepIntentDefinition[] BuildFallbackStepIntents(string[] steps, string[] stepActionTypes, MissionTaskSlot assignedSlot)
    {
        if (steps == null || steps.Length == 0) return new StepIntentDefinition[0];

        StepIntentDefinition[] result = new StepIntentDefinition[steps.Length];
        for (int i = 0; i < steps.Length; i++)
        {
            string stepText = steps[i] ?? string.Empty;
            string actionTypeHint = stepActionTypes != null && i < stepActionTypes.Length ? stepActionTypes[i] : "Idle";

            // 这里的 fallback 故意做得很“保守”：
            // 1) intentType 只从结构化 actionType 映射，不读自然语言 stepText；
            // 2) target / via / finalBehavior 优先复用 assignedSlot；
            // 3) 如果 assignedSlot 也没有信息，就退回到 none / arrive 这种中性默认值。
            // 这样做的目的就是把语义理解责任交还给 LLM，而不是让系统偷偷再做一层词法推断。
            result[i] = new StepIntentDefinition
            {
                stepText = stepText,
                intentType = ResolveStepIntentTypeFromActionType(actionTypeHint),
                primaryTarget = !string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.target : null) ? assignedSlot.target : "none",
                orderedViaTargets = assignedSlot != null && assignedSlot.viaTargets != null ? assignedSlot.viaTargets : new string[0],
                avoidTargets = new string[0],
                preferTargets = new string[0],
                requestedTeammateIds = new string[0],
                observationFocus = "none",
                communicationGoal = "none",
                finalBehavior = !string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.finalBehavior : null) ? assignedSlot.finalBehavior : "arrive",
                completionCondition = !string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.completionCondition : null) ? assignedSlot.completionCondition : stepText,
                notes = "fallback_from_structured_defaults"
            };
        }
        return result;
    }

    private RoutePolicyDefinition[] BuildFallbackRoutePolicies(int stepCount, MissionTaskSlot assignedSlot)
    {
        if (stepCount <= 0) return new RoutePolicyDefinition[0];

        RoutePolicyDefinition[] result = new RoutePolicyDefinition[stepCount];
        for (int i = 0; i < stepCount; i++)
        {
            result[i] = MergeRoutePolicyWithAssignedSlot(null, assignedSlot);
        }
        return result;
    }

    /// <summary>
    /// 对单步意图做补空和格式纠正。
    /// 最关键的补法有两个：
    /// 1) primaryTarget 为空时，用 assignedSlot.target 补；
    /// 2) orderedViaTargets 为空时，用 assignedSlot.viaTargets 补。
    /// 它做的是“把已知目标补回来”，不是“重新猜一个新目标”。
    /// </summary>
    private StepIntentDefinition NormalizeStepIntent(StepIntentDefinition src, string stepText, string actionTypeHint, MissionTaskSlot assignedSlot)
    {
        StepIntentDefinition result = src ?? new StepIntentDefinition();
        result.stepText = string.IsNullOrWhiteSpace(result.stepText) ? (stepText ?? string.Empty) : result.stepText;
        if (result.intentType == StepIntentType.Unknown)
        {
            // 只允许用上游已经结构化过的 actionType 做兜底，不再对中文 step 文本做关键词分类。
            result.intentType = ResolveStepIntentTypeFromActionType(actionTypeHint);
        }
        if (string.IsNullOrWhiteSpace(result.primaryTarget))
        {
            // primaryTarget 缺失时，优先复用当前分配槽位的 target。
            // 这属于“沿用任务分配结果”，不是“重新理解自然语言”。
            result.primaryTarget = !string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.target : null) ? assignedSlot.target : "none";
        }
        // 关键修复：
        // 旧逻辑只在 orderedViaTargets == null 时才回填 assignedSlot.viaTargets。
        // 但很多模型会返回空数组 []，这会把槽位里明确指定的检查点直接吞掉。
        // 现在只要当前 step 没有有效 viaTargets，就统一回填槽位中的结构化经过点。
        if (result.orderedViaTargets == null || result.orderedViaTargets.Length == 0)
        {
            result.orderedViaTargets = assignedSlot != null && assignedSlot.viaTargets != null
                ? assignedSlot.viaTargets
                : new string[0];
        }
        result.avoidTargets = result.avoidTargets ?? new string[0];
        result.preferTargets = result.preferTargets ?? new string[0];
        result.requestedTeammateIds = result.requestedTeammateIds ?? new string[0];
        result.observationFocus = string.IsNullOrWhiteSpace(result.observationFocus) ? "none" : result.observationFocus;
        result.communicationGoal = string.IsNullOrWhiteSpace(result.communicationGoal) ? "none" : result.communicationGoal;
        result.finalBehavior = string.IsNullOrWhiteSpace(result.finalBehavior)
            ? (!string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.finalBehavior : null) ? assignedSlot.finalBehavior : "arrive")
            : result.finalBehavior;
        result.completionCondition = string.IsNullOrWhiteSpace(result.completionCondition)
            ? (!string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.completionCondition : null) ? assignedSlot.completionCondition : result.stepText)
            : result.completionCondition;
        result.notes = string.IsNullOrWhiteSpace(result.notes) ? "normalized" : result.notes;
        return result;
    }

    /// <summary>
    /// 用 assignedSlot 的“最终目标 + 经过点”对 stepIntents 做结构化纠偏。
    /// 背景：
    /// 1) 第二阶段槽位里，target 表示最终要作用的目标，viaTargets 表示中间检查点；
    /// 2) 计划阶段的 LLM 有时会把“检查点”抄成后续观察步骤的 primaryTarget；
    /// 3) 这会导致动作层把检查点当终点，出现“到了 via 就结束”的错误。
    ///
    /// 这里不做自然语言关键词判断，只做结构一致性修复：
    /// - 前段移动步骤允许把 via 当阶段性目标；
    /// - 非移动步骤、末段步骤如果还指向 via，则改回 assignedSlot.target。
    /// </summary>
    private StepIntentDefinition[] RepairStepIntentsAgainstAssignedSlot(StepIntentDefinition[] intents, string[] steps, string[] stepActionTypes, MissionTaskSlot assignedSlot)
    {
        if (intents == null || intents.Length == 0) return intents ?? new StepIntentDefinition[0];
        if (assignedSlot == null || string.IsNullOrWhiteSpace(assignedSlot.target)) return intents;

        string finalTarget = assignedSlot.target.Trim();
        string[] viaTargets = assignedSlot.viaTargets ?? new string[0];
        HashSet<string> viaSet = new HashSet<string>(
            viaTargets.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()),
            StringComparer.OrdinalIgnoreCase
        );

        int lastMeaningfulStep = -1;
        int lastMoveStep = -1;
        int routedMoveOwner = -1;
        for (int i = 0; i < intents.Length; i++)
        {
            string actionType = stepActionTypes != null && i < stepActionTypes.Length ? stepActionTypes[i] : "Idle";
            if (!string.Equals(actionType, "Idle", StringComparison.OrdinalIgnoreCase))
            {
                lastMeaningfulStep = i;
            }
            if (string.Equals(actionType, "Move", StringComparison.OrdinalIgnoreCase))
            {
                lastMoveStep = i;
                if (routedMoveOwner < 0)
                {
                    routedMoveOwner = i;
                }
            }
        }

        for (int i = 0; i < intents.Length; i++)
        {
            StepIntentDefinition current = intents[i] ?? new StepIntentDefinition();
            string actionType = stepActionTypes != null && i < stepActionTypes.Length ? stepActionTypes[i] : "Idle";
            string currentTarget = current.primaryTarget != null ? current.primaryTarget.Trim() : string.Empty;
            bool targetIsVia = !string.IsNullOrWhiteSpace(currentTarget) && viaSet.Contains(currentTarget);
            bool isMove = string.Equals(actionType, "Move", StringComparison.OrdinalIgnoreCase);
            bool isLastMeaningful = i == lastMeaningfulStep;
            bool isLastMove = i == lastMoveStep;
            bool isMissionFacingIntent =
                current.intentType == StepIntentType.Observe ||
                current.intentType == StepIntentType.Communicate ||
                current.intentType == StepIntentType.Interact ||
                current.intentType == StepIntentType.Support ||
                current.intentType == StepIntentType.Escort ||
                (!string.IsNullOrWhiteSpace(current.finalBehavior) &&
                 !string.Equals(current.finalBehavior, "arrive", StringComparison.OrdinalIgnoreCase));

            // 规则1：
            // 非移动步骤本质上是在“对最终目标做事”。
            // 如果它们还指向 viaTargets，说明模型把检查点错写成终点了，需要改回 assignedSlot.target。
            if (!isMove && (string.IsNullOrWhiteSpace(currentTarget) || targetIsVia || isMissionFacingIntent))
            {
                current.primaryTarget = finalTarget;
            }

            // 规则2：
            // 最后一个移动步骤如果仍然只朝向 viaTargets，也要拉回最终目标。
            // 否则路径会停在检查点，不再推进到真正目标。
            if (isMove && isLastMove && (string.IsNullOrWhiteSpace(currentTarget) || targetIsVia))
            {
                current.primaryTarget = finalTarget;
            }

            // 规则3：
            // 最后一个有效步骤如果 target 为空，也统一补成最终目标，保证末段动作有明确作用对象。
            if (isLastMeaningful && string.IsNullOrWhiteSpace(current.primaryTarget))
            {
                current.primaryTarget = finalTarget;
            }

            // 规则4：
            // viaTargets 只属于“进入路线”本身，不属于后续观察、通信、回传等步骤。
            // 因此只保留给第一个真正承担路线推进的移动步骤；后续步骤全部清空。
            //
            // 否则会出现：
            // - 已经到过慧园1号楼/3食堂；
            // - 但后续步骤仍然挂着同一个 viaTargets；
            // - ActionDecision 再次按“先经过 via 再去 target”生成路径，导致智能体回头跑检查点。
            if (viaTargets.Length > 0)
            {
                if (isMove && i == routedMoveOwner)
                {
                    current.orderedViaTargets = viaTargets;
                }
                else
                {
                    current.orderedViaTargets = new string[0];
                }
            }

            intents[i] = current;
        }

        return intents;
    }

    private RoutePolicyDefinition NormalizeRoutePolicy(RoutePolicyDefinition src)
    {
        RoutePolicyDefinition result = src ?? new RoutePolicyDefinition();
        if (src == null)
        {
            result.approachSide = RouteApproachSide.Any;
            result.altitudeMode = RouteAltitudeMode.Default;
            result.clearance = RouteClearancePreference.Medium;
            result.allowGlobalAStar = true;
            result.allowLocalDetour = true;
            result.slowNearTarget = true;
            result.blockedPolicy = BlockedPolicyType.Replan;
        }
        result.avoidNodeTypes = result.avoidNodeTypes ?? new SmallNodeType[0];
        result.avoidFeatureNames = result.avoidFeatureNames ?? new string[0];
        result.preferFeatureNames = result.preferFeatureNames ?? new string[0];
        result.maxTeammatesInCorridor = Mathf.Max(0, result.maxTeammatesInCorridor);
        result.notes = string.IsNullOrWhiteSpace(result.notes) ? "normalized" : result.notes;
        return result;
    }

    /// <summary>
    /// 把槽位层已经结构化好的路径偏好并入 stepRoutePolicy。
    /// 这样即使 LLM 把 stepRoutePolicy 写成中性默认值，系统仍能保住：
    /// 1) 必须从哪一侧接近；
    /// 2) 槽位要求的高度模式。
    /// 这些信息本来就属于任务分配结果，不是系统重新猜任务语义。
    /// </summary>
    private RoutePolicyDefinition MergeRoutePolicyWithAssignedSlot(RoutePolicyDefinition src, MissionTaskSlot assignedSlot)
    {
        RoutePolicyDefinition result = NormalizeRoutePolicy(src);
        if (assignedSlot == null) return result;

        if (result.approachSide == RouteApproachSide.Any && assignedSlot.approachSide != RouteApproachSide.Any)
        {
            result.approachSide = assignedSlot.approachSide;
        }

        if (result.altitudeMode == RouteAltitudeMode.Default && assignedSlot.altitudeMode != RouteAltitudeMode.Default)
        {
            result.altitudeMode = assignedSlot.altitudeMode;
        }

        return result;
    }

    private TeamCoordinationDirective NormalizeCoordinationDirective(TeamCoordinationDirective src)
    {
        TeamCoordinationDirective result = src ?? new TeamCoordinationDirective();
        result.yieldToAgentIds = result.yieldToAgentIds ?? new string[0];
        result.syncPointTargets = result.syncPointTargets ?? new string[0];
        result.notes = string.IsNullOrWhiteSpace(result.notes) ? "normalized" : result.notes;
        return result;
    }

    private static StepIntentType ResolveStepIntentTypeFromActionType(string actionTypeHint)
    {
        string actionType = NormalizeStepActionTypeToken(actionTypeHint);
        return actionType switch
        {
            "Move" => StepIntentType.Navigate,
            "Observe" => StepIntentType.Observe,
            "Communicate" => StepIntentType.Communicate,
            "Interact" => StepIntentType.Interact,
            _ => StepIntentType.Unknown
        };
    }

    private static string[] BuildFilledArray(int count, string value)
    {
        if (count <= 0) return new string[0];
        string[] arr = new string[count];
        for (int i = 0; i < count; i++) arr[i] = value;
        return arr;
    }

    private static string NormalizeStepActionTypeToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Idle";
        string s = raw.Trim().ToLowerInvariant();
        // 这里只做大小写归一，不再扩展自然语言同义词。
        // 上游 prompt 已经要求 LLM 输出固定枚举值，这里只负责容忍大小写差异。
        if (s == "move") return "Move";
        if (s == "observe") return "Observe";
        if (s == "communicate") return "Communicate";
        if (s == "interact") return "Interact";
        if (s == "idle") return "Idle";
        return "Idle";
    }

    private static string NormalizeStepNavigationModeToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "None";
        string s = raw.Trim().ToLowerInvariant();
        // 同样只接受固定导航枚举，不再从自然语言近义词推断导航模式。
        if (s == "astar") return "AStar";
        if (s == "direct") return "Direct";
        if (s == "none") return "None";
        return "None";
    }

    // 从响应中提取纯 JSON 内容
    private string ExtractPureJson(string response)
    {
        if (string.IsNullOrEmpty(response))
            return response;

        string text = response.Trim();

        // 如果包含代码块标记，提取其中的内容
        if (text.Contains("```json"))
        {
            int jsonStart = text.IndexOf("```json", StringComparison.Ordinal) + 7;
            int jsonEnd = text.IndexOf("```", jsonStart, StringComparison.Ordinal);
            if (jsonEnd > jsonStart)
            {
                text = text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }
        else if (text.Contains("```"))
        {
            // 如果包含普通的代码块标记
            int jsonStart = text.IndexOf("```", StringComparison.Ordinal) + 3;
            int jsonEnd = text.IndexOf("```", jsonStart, StringComparison.Ordinal);
            if (jsonEnd > jsonStart)
            {
                text = text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }

        // 从文本中截取首个完整 JSON 对象/数组，避免前后解释文本污染解析。
        int objStart = text.IndexOf('{');
        int arrStart = text.IndexOf('[');
        int start = -1;

        if (objStart >= 0 && arrStart >= 0) start = Mathf.Min(objStart, arrStart);
        else if (objStart >= 0) start = objStart;
        else if (arrStart >= 0) start = arrStart;

        if (start < 0) return text;

        char open = text[start];
        char close = open == '{' ? '}' : ']';

        int depth = 0;
        bool inString = false;
        char quote = '\0';
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == quote)
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                quote = c;
                continue;
            }

            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1).Trim();
                }
            }
        }

        // 若未找到闭合，至少返回从首个 JSON 起始处开始的内容，供后续容错解析尝试。
        return text.Substring(start).Trim();
    }

    /// <summary>
    /// 从任务分析响应里提取结构化任务对象。
    /// 这一步的目标很明确：让“任务类型、角色、槽位、协同规则”尽可能直接来自 LLM 的 JSON，
    /// 而不是系统再去读 missionDescription 做文本猜测。
    /// </summary>
    private MissionAnalysisResponse ExtractMissionAnalysisResponse(string response)
    {
        if (!TryParseMissionAnalysisResponse(response, out MissionAnalysisResponse analysis, out string error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.LogWarning($"任务分析响应解析失败，回退到系统默认兜底: {error}");
            }
            return null;
        }

        return analysis;
    }

    /// <summary>
    /// 解析任务级 JSON。
    /// 与计划解析不同，这里优先关注 roles / taskSlots / coordinationDirectives 这些“分配层”字段。
    /// </summary>
    private bool TryParseMissionAnalysisResponse(string response, out MissionAnalysisResponse analysis, out string error)
    {
        analysis = null;
        error = string.Empty;

        string jsonContent = ExtractPureJson(response);
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            error = "任务分析响应为空";
            return false;
        }

        try
        {
            if (!jsonContent.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                error = "任务分析响应不是 JSON 对象";
                return false;
            }

            JObject root = JObject.Parse(jsonContent);
            JObject obj = root;
            if (root["result"] is JObject wrappedResult)
            {
                obj = wrappedResult;
            }

            analysis = new MissionAnalysisResponse
            {
                missionType = ReadStringField(obj, "missionType", "type"),
                recommendedCommMode = ReadStringField(obj, "recommendedCommMode", "communicationMode", "commMode"),
                teamObjective = ReadStringField(obj, "teamObjective", "objective", "teamGoal"),
                roles = ReadObjectArrayField<MissionRole>(obj, "roles", "missionRoles"),
                coordinationDirectives = ReadObjectArrayField<TeamCoordinationDirective>(obj, "coordinationDirectives", "missionDirectives"),
                taskSlots = ReadTaskSlotsWithAliases(obj, "taskSlots", "slots"),
                reasoning = ReadStringField(obj, "reasoning")
            };

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取任务级字符串字段。只做字段别名兼容，不做自然语言语义猜测。
    /// </summary>
    private static string ReadStringField(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return string.Empty;
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;

            JToken token = obj[key];
            if (token != null && token.Type != JTokenType.Null)
            {
                string value = token.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 读取任务级对象数组字段，例如 roles / taskSlots / coordinationDirectives。
    /// 这里不做任何“按词猜字段内容”的处理，只在字段存在时直接反序列化。
    /// </summary>
    private static T[] ReadObjectArrayField<T>(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return new T[0];
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;

            JToken token = obj[key];
            if (token is JArray arr)
            {
                T[] result = arr.ToObject<T[]>();
                if (result != null && result.Length > 0) return result;
            }
        }

        return new T[0];
    }

    /// <summary>
    /// 读取 taskSlots，并兼容 requiredAgentType/agentType 这类字段别名。
    /// 复杂任务下，第三方模型经常会在字段名上有轻微偏差，这里做一层结构兼容，减少有效结果被误判成空槽位。
    /// </summary>
    private static MissionTaskSlot[] ReadTaskSlotsWithAliases(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return new MissionTaskSlot[0];

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!(obj[key] is JArray arr) || arr.Count == 0) continue;

            List<MissionTaskSlot> slots = new List<MissionTaskSlot>();
            for (int j = 0; j < arr.Count; j++)
            {
                if (!(arr[j] is JObject slotObj)) continue;

                slots.Add(new MissionTaskSlot
                {
                    slotId = ReadStringField(slotObj, "slotId", "id"),
                    slotLabel = ReadStringField(slotObj, "slotLabel", "label", "name"),
                    roleType = ParseEnumOrDefault(ReadStringField(slotObj, "roleType", "role"), RoleType.Supporter),
                    requiredAgentType = ParseEnumOrDefault(ReadStringField(slotObj, "requiredAgentType", "agentType"), AgentType.Quadcopter),
                    target = ReadStringField(slotObj, "target", "primaryTarget"),
                    viaTargets = ReadStringArrayField(slotObj, "viaTargets", "orderedViaTargets"),
                    approachSide = ParseEnumOrDefault(ReadStringField(slotObj, "approachSide"), RouteApproachSide.Any),
                    altitudeMode = ParseEnumOrDefault(ReadStringField(slotObj, "altitudeMode"), RouteAltitudeMode.Default),
                    syncGroup = ReadStringField(slotObj, "syncGroup", "group"),
                    dependsOnSlotIds = ReadStringArrayField(slotObj, "dependsOnSlotIds", "dependsOn"),
                    finalBehavior = ReadStringField(slotObj, "finalBehavior"),
                    completionCondition = ReadStringField(slotObj, "completionCondition"),
                    notes = ReadStringField(slotObj, "notes", "note")
                });
            }

            if (slots.Count > 0) return slots.ToArray();
        }

        return new MissionTaskSlot[0];
    }

    private static string[] ReadStringArrayField(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return new string[0];

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (obj[key] is JArray arr)
            {
                List<string> values = new List<string>();
                for (int j = 0; j < arr.Count; j++)
                {
                    string value = arr[j]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
                }

                if (values.Count > 0) return values.ToArray();
                return new string[0];
            }
        }

        return new string[0];
    }

    private static TEnum ParseEnumOrDefault<TEnum>(string raw, TEnum fallback) where TEnum : struct
    {
        if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse(raw.Trim(), true, out TEnum parsed))
        {
            return parsed;
        }

        return fallback;
    }

    // 从LLM响应中提取角色
    private RoleType ExtractRoleTypeFromResponse(string response)
    {
        try
        {
            string parsedJson;
            string parseError;
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out parsedJson, out parseError))
            {
                if (!string.IsNullOrEmpty(planResponse?.assignedRole))
                {
                    if (System.Enum.TryParse<RoleType>(planResponse.assignedRole, out RoleType role))
                    {
                        return role;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(parseError))
            {
                Debug.LogWarning($"角色解析未命中结构化JSON，原因: {parseError}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"角色解析失败: {e.Message}");
        }
        
        // 计划级角色解析失败时，不再依据平台类型猜角色，直接回退到当前已有角色。
        return agentProperties.Role;
    }

    /// <summary>
    /// 统一解析计划响应（鲁棒版）：
    /// 1) 自动从 LLM 文本里提取首个 JSON 对象/数组；
    /// 2) 优先按对象反序列化 PlanResponse；
    /// 3) 兼容仅返回步骤数组的情况。
    /// </summary>
    private bool TryParsePlanResponse(string response, out PlanResponse planResponse, out string normalizedJson, out string error)
    {
        planResponse = null;
        normalizedJson = string.Empty;
        error = string.Empty;

        string jsonContent = ExtractPureJson(response);
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            error = "提取到的JSON为空";
            return false;
        }

        normalizedJson = jsonContent.Trim();

        try
        {
            if (normalizedJson.StartsWith("{"))
            {
                planResponse = JsonConvert.DeserializeObject<PlanResponse>(normalizedJson);
                if (planResponse != null)
                {
                    JObject obj = JObject.Parse(normalizedJson);

                    if (string.IsNullOrWhiteSpace(planResponse.assignedRole))
                    {
                        // 兼容 role 字段命名差异
                        planResponse.assignedRole = (string)obj["assignedRole"] ?? (string)obj["role"] ?? string.Empty;
                    }

                    if (planResponse.steps == null || planResponse.steps.Length == 0)
                    {
                        JToken token = obj["steps"] ?? obj["planSteps"] ?? obj["actions"];
                        if (token is JArray arr)
                        {
                            var list = new List<string>();
                            for (int i = 0; i < arr.Count; i++)
                            {
                                string s = arr[i]?.ToString();
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                            }
                            planResponse.steps = list.ToArray();
                        }
                    }

                    if (planResponse.stepActionTypes == null || planResponse.stepActionTypes.Length == 0)
                    {
                        JToken token = obj["stepActionTypes"] ?? obj["actionTypes"];
                        if (token is JArray arr)
                        {
                            var list = new List<string>();
                            for (int i = 0; i < arr.Count; i++)
                            {
                                string s = arr[i]?.ToString();
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                            }
                            planResponse.stepActionTypes = list.ToArray();
                        }
                    }

                    if (planResponse.stepNavigationModes == null || planResponse.stepNavigationModes.Length == 0)
                    {
                        JToken token = obj["stepNavigationModes"] ?? obj["stepNavModes"] ?? obj["stepNavigation"];
                        if (token is JArray arr)
                        {
                            var list = new List<string>();
                            for (int i = 0; i < arr.Count; i++)
                            {
                                string s = arr[i]?.ToString();
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                            }
                            planResponse.stepNavigationModes = list.ToArray();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(planResponse.missionNavigationPolicy))
                    {
                        planResponse.missionNavigationPolicy =
                            (string)obj["missionNavigationPolicy"] ??
                            (string)obj["navigationPolicy"] ??
                            string.Empty;
                    }

                    return true;
                }
            }
            else if (normalizedJson.StartsWith("["))
            {
                // 兼容直接返回 ["step1","step2"] 的情况
                JArray arr = JArray.Parse(normalizedJson);
                var list = new List<string>();
                for (int i = 0; i < arr.Count; i++)
                {
                    string s = arr[i]?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                }

                planResponse = new PlanResponse
                {
                    assignedRole = string.Empty,
                    steps = list.ToArray()
                };
                return list.Count > 0;
            }

            if (TryParsePlanResponseLoosely(normalizedJson, out planResponse))
            {
                error = "JSON结构不标准，已使用容错解析";
                Debug.LogWarning($"计划响应使用容错解析: {error}");
                return true;
            }

            error = "JSON不是对象或数组";
            return false;
        }
        catch (Exception ex)
        {
            if (TryParsePlanResponseLoosely(normalizedJson, out planResponse))
            {
                error = $"标准JSON解析失败({ex.Message})，已回退容错解析";
                Debug.LogWarning($"计划响应使用容错解析: {error}");
                return true;
            }

            error = ex.Message;
            Debug.LogError($"计划响应解析失败: {error}\n原始片段: {normalizedJson}");
            return false;
        }
    }

    /// <summary>
    /// 非严格 JSON 容错解析：
    /// 即使 response 被截断（如 reasoning 未闭合），也尽量提取 assignedRole 与 steps。
    /// </summary>
    private bool TryParsePlanResponseLoosely(string raw, out PlanResponse planResponse)
    {
        planResponse = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        bool gotRole = TryExtractStringFieldLoose(raw, "assignedRole", out string role);
        if (!gotRole)
        {
            TryExtractStringFieldLoose(raw, "role", out role);
        }

        bool gotSteps = TryExtractStringArrayFieldLoose(raw, "steps", out string[] steps);
        if (!gotSteps)
        {
            gotSteps = TryExtractStringArrayFieldLoose(raw, "planSteps", out steps);
        }
        if (!gotSteps)
        {
            gotSteps = TryExtractStringArrayFieldLoose(raw, "actions", out steps);
        }

        bool gotActionTypes = TryExtractStringArrayFieldLoose(raw, "stepActionTypes", out string[] stepActionTypes);
        if (!gotActionTypes)
        {
        }

        bool gotNavModes = TryExtractStringArrayFieldLoose(raw, "stepNavigationModes", out string[] stepNavigationModes);
        if (!gotNavModes)
        {
            gotNavModes = TryExtractStringArrayFieldLoose(raw, "stepNavModes", out stepNavigationModes);
        }

        bool gotMissionPolicy = TryExtractStringFieldLoose(raw, "missionNavigationPolicy", out string missionNavPolicy);
        if (!gotMissionPolicy)
        {
            TryExtractStringFieldLoose(raw, "navigationPolicy", out missionNavPolicy);
        }

        if (!gotRole && !gotSteps && !gotActionTypes && !gotNavModes && !gotMissionPolicy) return false;

        planResponse = new PlanResponse
        {
            assignedRole = role ?? string.Empty,
            steps = (steps != null && steps.Length > 0) ? steps : new string[0],
            stepActionTypes = (stepActionTypes != null && stepActionTypes.Length > 0) ? stepActionTypes : new string[0],
            stepNavigationModes = (stepNavigationModes != null && stepNavigationModes.Length > 0) ? stepNavigationModes : new string[0],
            missionNavigationPolicy = missionNavPolicy ?? string.Empty
        };
        return planResponse.steps.Length > 0 ||
               !string.IsNullOrWhiteSpace(planResponse.assignedRole) ||
               planResponse.stepActionTypes.Length > 0 ||
               planResponse.stepNavigationModes.Length > 0 ||
               !string.IsNullOrWhiteSpace(planResponse.missionNavigationPolicy);
    }

    /// <summary>
    /// 容错提取字符串字段，如 "assignedRole":"Scout"
    /// </summary>
    private static bool TryExtractStringFieldLoose(string raw, string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(key)) return false;

        string pattern = $"\"{System.Text.RegularExpressions.Regex.Escape(key)}\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)";
        var m = System.Text.RegularExpressions.Regex.Match(raw, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        value = UnescapeJsonStringLoose(m.Groups["v"].Value).Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// 容错提取字符串数组字段，如 "steps":[ "...", "..." ]
    /// </summary>
    private static bool TryExtractStringArrayFieldLoose(string raw, string key, out string[] values)
    {
        values = null;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(key)) return false;

        int keyPos = raw.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
        if (keyPos < 0) return false;

        int colonPos = raw.IndexOf(':', keyPos);
        if (colonPos < 0) return false;

        int arrStart = raw.IndexOf('[', colonPos);
        if (arrStart < 0) return false;

        List<string> list = new List<string>();
        bool inString = false;
        bool escaped = false;
        int depth = 0;
        int strStart = -1;

        for (int i = arrStart; i < raw.Length; i++)
        {
            char c = raw[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                {
                    string token = raw.Substring(strStart, i - strStart);
                    string unescaped = UnescapeJsonStringLoose(token).Trim();
                    if (!string.IsNullOrWhiteSpace(unescaped)) list.Add(unescaped);
                    inString = false;
                    strStart = -1;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                strStart = i + 1;
                continue;
            }

            if (c == '[')
            {
                depth++;
                continue;
            }

            if (c == ']')
            {
                depth--;
                if (depth <= 0)
                {
                    values = list.ToArray();
                    return values.Length > 0;
                }
            }
        }

        // 数组可能被截断，仍可返回已提取到的步骤。
        values = list.ToArray();
        return values.Length > 0;
    }

    /// <summary>
    /// 容错解码 JSON 字符串中的常见转义。
    /// </summary>
    private static string UnescapeJsonStringLoose(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");
    }
    // 创建角色辅助方法
    private MissionRole CreateRole(RoleType roleType, AgentType agentType, int count, string[] responsibilities)
    {
        return new MissionRole
        {
            roleType = roleType,
            agentType = agentType,
            requiredCount = count,
            responsibilities = responsibilities,
            preferredTargets = new string[0],
            coordinationResponsibilities = new string[0]
        };
    }

    /// <summary>
    /// 计算角色需求总人数。
    /// 当 LLM 给出的 roles 数量偏少时，用它和用户要求人数做对比，避免任务被压缩成单槽位。
    /// </summary>
    private static int CountRequiredRoleAgents(MissionRole[] roles)
    {
        if (roles == null || roles.Length == 0) return 0;

        int total = 0;
        for (int i = 0; i < roles.Length; i++)
        {
            MissionRole role = roles[i];
            if (role == null) continue;
            total += Mathf.Max(0, role.requiredCount);
        }

        return total;
    }

    /// <summary>
    /// 从任务分析响应中提取 taskSlots。
    /// 对“目标解析”来说，这一层最重要，因为后续动作模块真正认的目标，
    /// 基本都先来自这里：
    /// - slot.target 是最终目标；
    /// - slot.viaTargets 是中间检查点；
    /// 如果 LLM 没给稳定槽位，系统才退回保守机械展开。
    /// </summary>
    private MissionTaskSlot[] ExtractTaskSlotsFromResponse(MissionAnalysisResponse analysis, string missionDescription, MissionRole[] roles, MissionType missionType, int agentCount)
    {
        int capacity = Mathf.Max(1, agentCount);

        if (analysis != null && analysis.taskSlots != null && analysis.taskSlots.Length > 0)
        {
            List<MissionTaskSlot> normalized = new List<MissionTaskSlot>();
            for (int i = 0; i < analysis.taskSlots.Length; i++)
            {
                MissionTaskSlot slot = analysis.taskSlots[i];
                if (slot == null) continue;
                normalized.Add(NormalizeMissionTaskSlot(slot, missionDescription, i));
            }

            if (normalized.Count > 0)
            {
                MissionTaskSlot[] repaired = EnsureTaskSlotsAreExecutable(normalized.ToArray(), missionDescription, roles, missionType, capacity);
                if (repaired.Length > 0) return repaired;
            }
        }

        return BuildTaskSlotsForMission(missionDescription, roles, missionType, capacity);
    }

    /// <summary>
    /// 提取任务级协同约束。
    /// 若 LLM 未返回，则系统只给出一份中性默认协同规则，不再根据任务文本自己猜领航/让行/分侧等语义。
    /// </summary>
    private TeamCoordinationDirective[] ExtractMissionCoordinationDirectives(MissionAnalysisResponse analysis, MissionType missionType)
    {
        if (analysis != null && analysis.coordinationDirectives != null && analysis.coordinationDirectives.Length > 0)
        {
            return analysis.coordinationDirectives
                .Where(d => d != null)
                .Select(NormalizeCoordinationDirective)
                .ToArray();
        }

        return BuildDefaultMissionCoordinationDirectives(missionType);
    }

    /// <summary>
    /// 对单个任务槽位做合法化补全。
    /// 这里只补字段默认值，不解释自然语言，不重写槽位语义。
    /// </summary>
    private MissionTaskSlot NormalizeMissionTaskSlot(MissionTaskSlot slot, string missionDescription, int index)
    {
        MissionTaskSlot result = slot ?? new MissionTaskSlot();
        result.slotId = string.IsNullOrWhiteSpace(result.slotId) ? $"slot_{index + 1}" : result.slotId;
        result.slotLabel = string.IsNullOrWhiteSpace(result.slotLabel) ? result.slotId : result.slotLabel;
        result.target = string.IsNullOrWhiteSpace(result.target) ? missionDescription : result.target;
        result.viaTargets = result.viaTargets ?? new string[0];
        result.syncGroup = string.IsNullOrWhiteSpace(result.syncGroup) ? "mission_default_group" : result.syncGroup;
        result.dependsOnSlotIds = result.dependsOnSlotIds ?? new string[0];
        result.finalBehavior = string.IsNullOrWhiteSpace(result.finalBehavior) ? "arrive" : result.finalBehavior;
        result.completionCondition = string.IsNullOrWhiteSpace(result.completionCondition) ? $"完成槽位 {result.slotLabel}" : result.completionCondition;
        result.notes = string.IsNullOrWhiteSpace(result.notes) ? "normalized_from_llm_or_role_expansion" : result.notes;
        return result;
    }

    /// <summary>
    /// 对 LLM 输出的 taskSlots 做可执行性修复。
    /// 除了查重、清依赖、补数量之外，
    /// 这里还有一个和目标强相关的工作：
    /// 如果 LLM 把同一个智能体的任务拆成多段 phase 槽位，
    /// 这里会尽量把它们重新压回一个“端到端槽位”，避免把路上的检查点误当成终点。
    /// </summary>
    private MissionTaskSlot[] EnsureTaskSlotsAreExecutable(MissionTaskSlot[] input, string missionDescription, MissionRole[] roles, MissionType missionType, int agentCount)
    {
        if (input == null || input.Length == 0) return new MissionTaskSlot[0];

        List<MissionTaskSlot> slots = new List<MissionTaskSlot>();
        HashSet<string> usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < input.Length; i++)
        {
            MissionTaskSlot slot = NormalizeMissionTaskSlot(input[i], missionDescription, i);
            string baseId = string.IsNullOrWhiteSpace(slot.slotId) ? $"slot_{i + 1}" : slot.slotId.Trim();
            string uniqueId = baseId;
            int suffix = 2;
            while (usedIds.Contains(uniqueId))
            {
                uniqueId = $"{baseId}_{suffix}";
                suffix++;
            }

            slot.slotId = uniqueId;
            if (string.IsNullOrWhiteSpace(slot.slotLabel)) slot.slotLabel = uniqueId;
            usedIds.Add(uniqueId);
            slots.Add(slot);
        }

        HashSet<string> validIds = new HashSet<string>(slots.Select(s => s.slotId), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < slots.Count; i++)
        {
            string[] deps = slots[i].dependsOnSlotIds ?? new string[0];
            slots[i].dependsOnSlotIds = deps
                .Where(d => !string.IsNullOrWhiteSpace(d) && validIds.Contains(d.Trim()))
                .Select(d => d.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        int capacity = Mathf.Max(1, agentCount);
        if (slots.Count > capacity)
        {
            // 关键修复：
            // 当前调度器只支持“一人一个槽位”。
            // 如果 LLM 错把 phasePlan 展开成多阶段链式槽位（例如 phase_1/phase_2/phase_3 都各生成一遍），
            // 直接截前 N 个会把“检查点槽位”误当成最终任务槽位，导致智能体到达中间点就结束。
            // 因此这里优先把链式槽位压缩成“每个智能体一个端到端槽位”。
            List<MissionTaskSlot> compressed = CompressTaskSlotsToEndToEndAssignments(slots, capacity);
            if (compressed.Count >= capacity)
            {
                Debug.LogWarning($"[Planning] LLM 返回 phase 级链式槽位，已压缩为端到端槽位: raw={slots.Count}, compressed={compressed.Count}, agents={capacity}");
                slots = compressed.Take(capacity).ToList();
            }
            else
            {
                Debug.LogWarning($"[Planning] LLM 返回槽位数超过可用智能体，压缩不足，退回截断: slots={slots.Count}, agents={capacity}");
                slots = slots.Take(capacity).ToList();
            }
        }

        if (slots.Count < capacity)
        {
            MissionTaskSlot[] fallback = BuildTaskSlotsForMission(missionDescription, roles, missionType, capacity);
            HashSet<string> existingIds = new HashSet<string>(slots.Select(s => s.slotId), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fallback.Length && slots.Count < capacity; i++)
            {
                MissionTaskSlot candidate = NormalizeMissionTaskSlot(fallback[i], missionDescription, slots.Count);
                string baseId = candidate.slotId;
                string uniqueId = baseId;
                int suffix = 2;
                while (existingIds.Contains(uniqueId))
                {
                    uniqueId = $"{baseId}_{suffix}";
                    suffix++;
                }
                candidate.slotId = uniqueId;
                candidate.slotLabel = string.IsNullOrWhiteSpace(candidate.slotLabel) ? uniqueId : candidate.slotLabel;
                existingIds.Add(uniqueId);
                slots.Add(candidate);
            }
        }

        // 数量修正后再清洗一次依赖，避免依赖指向被截断或未补齐成功的槽位。
        HashSet<string> finalIds = new HashSet<string>(slots.Select(s => s.slotId), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < slots.Count; i++)
        {
            string[] deps = slots[i].dependsOnSlotIds ?? new string[0];
            slots[i].dependsOnSlotIds = deps
                .Where(d => !string.IsNullOrWhiteSpace(d) && finalIds.Contains(d.Trim()))
                .Select(d => d.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return slots.ToArray();
    }

    /// <summary>
    /// 将“phase 级链式槽位”压缩成“单智能体端到端槽位”。
    /// 大白话就是：
    /// - 真正最后要到达/观察/交互的地方留在 target；
    /// - 路上先经过的点收进 viaTargets；
    /// - 这样后面动作层才知道“终点是谁，路过点是谁”。
    /// 这一步不靠关键词猜测，只根据已有结构做收敛。
    /// </summary>
    private List<MissionTaskSlot> CompressTaskSlotsToEndToEndAssignments(List<MissionTaskSlot> slots, int agentCount)
    {
        if (slots == null || slots.Count == 0) return new List<MissionTaskSlot>();

        Dictionary<string, MissionTaskSlot> byId = slots
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.slotId))
            .GroupBy(s => s.slotId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, int> inboundCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (MissionTaskSlot slot in slots)
        {
            if (slot == null || slot.dependsOnSlotIds == null) continue;
            for (int i = 0; i < slot.dependsOnSlotIds.Length; i++)
            {
                string dep = slot.dependsOnSlotIds[i];
                if (string.IsNullOrWhiteSpace(dep)) continue;
                string key = dep.Trim();
                inboundCount[key] = inboundCount.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        List<MissionTaskSlot> leaves = slots
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.slotId) && !inboundCount.ContainsKey(s.slotId.Trim()))
            .ToList();
        if (leaves.Count == 0) leaves = new List<MissionTaskSlot>(slots);

        HashSet<string> focusTargets = DeriveMissionFocusTargets(slots);
        List<MissionTaskSlot> compressed = new List<MissionTaskSlot>();
        HashSet<string> usedRepresentativeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < leaves.Count; i++)
        {
            List<MissionTaskSlot> chain = BuildTaskSlotChain(leaves[i], byId);
            if (chain.Count == 0) continue;

            MissionTaskSlot representative = SelectRepresentativeSlotFromChain(chain, focusTargets);
            if (representative == null) continue;
            if (!string.IsNullOrWhiteSpace(representative.slotId) && usedRepresentativeIds.Contains(representative.slotId)) continue;

            MissionTaskSlot merged = MergeTaskSlotChain(chain, representative, compressed.Count);
            compressed.Add(merged);
            if (!string.IsNullOrWhiteSpace(representative.slotId))
            {
                usedRepresentativeIds.Add(representative.slotId);
            }
        }

        if (compressed.Count == 0) return compressed;

        return compressed
            .OrderByDescending(s => focusTargets.Contains((s.target ?? string.Empty).Trim()) ? 1 : 0)
            .ThenBy(s => s.slotId, StringComparer.OrdinalIgnoreCase)
            .Take(Mathf.Max(1, agentCount))
            .ToList();
    }

    /// <summary>
    /// 从所有槽位里提取“任务焦点目标”。
    /// 优先选择在槽位中出现频率最高的 target；这通常是核心任务目标，
    /// 而不是只在前置接近阶段出现一次的检查点。
    /// </summary>
    private HashSet<string> DeriveMissionFocusTargets(List<MissionTaskSlot> slots)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (slots == null || slots.Count == 0) return result;

        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < slots.Count; i++)
        {
            string target = slots[i] != null ? slots[i].target : null;
            if (string.IsNullOrWhiteSpace(target)) continue;
            string key = target.Trim();
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        if (counts.Count == 0) return result;
        int maxCount = counts.Max(kv => kv.Value);
        foreach (var kv in counts)
        {
            if (kv.Value == maxCount) result.Add(kv.Key);
        }
        return result;
    }

    /// <summary>
    /// 从叶子槽位向前回溯依赖链，得到同一智能体可能被错误拆成多个 phase 槽位的完整链条。
    /// 返回顺序为“前置 -> 后置”。
    /// </summary>
    private List<MissionTaskSlot> BuildTaskSlotChain(MissionTaskSlot leaf, Dictionary<string, MissionTaskSlot> byId)
    {
        List<MissionTaskSlot> reversed = new List<MissionTaskSlot>();
        HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MissionTaskSlot cursor = leaf;

        while (cursor != null && !string.IsNullOrWhiteSpace(cursor.slotId))
        {
            string id = cursor.slotId.Trim();
            if (!visited.Add(id)) break;
            reversed.Add(cursor);

            string nextId = cursor.dependsOnSlotIds != null && cursor.dependsOnSlotIds.Length > 0
                ? cursor.dependsOnSlotIds[0]
                : string.Empty;
            if (string.IsNullOrWhiteSpace(nextId) || byId == null || !byId.TryGetValue(nextId.Trim(), out cursor))
            {
                break;
            }
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// 在一条 phase 链里挑选“真正代表核心任务”的槽位。
    /// 评分原则：
    /// 1) target 命中任务焦点目标优先；
    /// 2) finalBehavior 非 arrive 优先，说明已经进入真正执行/观察/交互阶段；
    /// 3) 有明确接近侧、同步组的优先；
    /// 4) 深层槽位优先于前置起飞/接近槽位。
    /// </summary>
    private MissionTaskSlot SelectRepresentativeSlotFromChain(List<MissionTaskSlot> chain, HashSet<string> focusTargets)
    {
        if (chain == null || chain.Count == 0) return null;

        MissionTaskSlot best = null;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < chain.Count; i++)
        {
            MissionTaskSlot slot = chain[i];
            if (slot == null) continue;

            float score = i * 10f;
            if (!string.IsNullOrWhiteSpace(slot.target) && focusTargets.Contains(slot.target.Trim())) score += 100f;
            if (!string.IsNullOrWhiteSpace(slot.finalBehavior) &&
                !string.Equals(slot.finalBehavior, "arrive", StringComparison.OrdinalIgnoreCase)) score += 35f;
            if (slot.approachSide != RouteApproachSide.Any) score += 15f;
            if (slot.altitudeMode != RouteAltitudeMode.Default) score += 8f;
            if (!string.IsNullOrWhiteSpace(slot.syncGroup)) score += 3f;

            if (score > bestScore)
            {
                bestScore = score;
                best = slot;
            }
        }

        return best ?? chain[chain.Count - 1];
    }

    /// <summary>
    /// 把链式槽位合并成一个端到端槽位。
    /// - representative.target 作为最终目标；
    /// - 代表槽位之前链上的 target/viaTargets 收敛为 viaTargets；
    /// - dependsOnSlotIds 清空，因为当前调度器把它当成单次分配给一个智能体的完整职责。
    /// </summary>
    private MissionTaskSlot MergeTaskSlotChain(List<MissionTaskSlot> chain, MissionTaskSlot representative, int index)
    {
        List<string> via = new List<string>();
        for (int i = 0; i < chain.Count; i++)
        {
            MissionTaskSlot slot = chain[i];
            if (slot == null) continue;

            bool isRepresentative = string.Equals(slot.slotId, representative.slotId, StringComparison.OrdinalIgnoreCase);
            if (!isRepresentative && !string.IsNullOrWhiteSpace(slot.target) &&
                !string.Equals(slot.target.Trim(), representative.target?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                via.Add(slot.target.Trim());
            }

            if (slot.viaTargets != null)
            {
                for (int j = 0; j < slot.viaTargets.Length; j++)
                {
                    string v = slot.viaTargets[j];
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    string trimmed = v.Trim();
                    if (!string.Equals(trimmed, representative.target?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        via.Add(trimmed);
                    }
                }
            }
        }

        List<string> orderedVia = via
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        RouteApproachSide approachSide = representative.approachSide;
        if (approachSide == RouteApproachSide.Any)
        {
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (chain[i] != null && chain[i].approachSide != RouteApproachSide.Any)
                {
                    approachSide = chain[i].approachSide;
                    break;
                }
            }
        }

        RouteAltitudeMode altitudeMode = representative.altitudeMode;
        if (altitudeMode == RouteAltitudeMode.Default)
        {
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (chain[i] != null && chain[i].altitudeMode != RouteAltitudeMode.Default)
                {
                    altitudeMode = chain[i].altitudeMode;
                    break;
                }
            }
        }

        string mergedId = !string.IsNullOrWhiteSpace(representative.slotId) ? representative.slotId : $"slot_{index + 1}";
        string mergedLabel = !string.IsNullOrWhiteSpace(representative.slotLabel) ? representative.slotLabel : mergedId;
        string chainSummary = string.Join(" -> ", chain.Where(s => s != null).Select(s => s.slotLabel ?? s.slotId).ToArray());

        return new MissionTaskSlot
        {
            slotId = mergedId,
            slotLabel = mergedLabel,
            roleType = representative.roleType,
            requiredAgentType = representative.requiredAgentType,
            target = representative.target,
            viaTargets = orderedVia.ToArray(),
            approachSide = approachSide,
            altitudeMode = altitudeMode,
            syncGroup = !string.IsNullOrWhiteSpace(representative.syncGroup) ? representative.syncGroup : "mission_default_group",
            dependsOnSlotIds = new string[0],
            finalBehavior = !string.IsNullOrWhiteSpace(representative.finalBehavior) ? representative.finalBehavior : "arrive",
            completionCondition = !string.IsNullOrWhiteSpace(representative.completionCondition) ? representative.completionCondition : $"完成槽位 {mergedLabel}",
            notes = $"compressed_chain:{chainSummary}"
        };
    }

    /// <summary>
    /// 当 LLM 没有直接提供 taskSlots 时，按 roles 做最保守的机械展开。
    /// 这时系统其实并不知道明确终点是谁，
    /// 所以只能先把 missionDescription 整句塞进 target，保证流程不断。
    /// 代价是：目标会比较模糊，后面动作层也更难精确绑定。
    /// </summary>
    private MissionTaskSlot[] BuildTaskSlotsForMission(string missionDescription, MissionRole[] roles, MissionType missionType, int agentCount)
    {
        List<MissionTaskSlot> slots = new List<MissionTaskSlot>();
        int requestedParticipants = Mathf.Max(agentCount, CountRequiredRoleAgents(roles));
        Debug.Log($"[Planning] LLM 未提供明确 taskSlots，按 roles 做保守展开: mission={missionDescription}, missionType={missionType}, requestedParticipants={requestedParticipants}");

        if (roles != null)
        {
            foreach (MissionRole role in roles)
            {
                if (role == null) continue;
                int count = Mathf.Max(0, role.requiredCount);
                for (int i = 0; i < count; i++)
                {
                    // 这里是最机械的展开方式：
                    // 一个 role 需要多少人，就生成多少个同类槽位。
                    // 它不理解“东侧”“主攻”“掩护”“前出侦查”等复杂语义，
                    // 因为这些细粒度差异应该直接由 LLM 放进 taskSlots，而不是由系统猜。
                    slots.Add(new MissionTaskSlot
                    {
                        slotId = $"slot_{role.roleType}_{i + 1}",
                        slotLabel = $"{role.roleType}_{i + 1}",
                        roleType = role.roleType,
                        requiredAgentType = role.agentType,
                        target = missionDescription,
                        viaTargets = new string[0],
                        approachSide = RouteApproachSide.Any,
                        altitudeMode = RouteAltitudeMode.Default,
                        syncGroup = $"mission_{missionType}",
                        dependsOnSlotIds = new string[0],
                        finalBehavior = "arrive",
                        completionCondition = $"完成槽位 {role.roleType}_{i + 1}",
                        notes = "expanded_from_roles_only"
                    });
                }
            }
        }

        if (slots.Count < requestedParticipants && slots.Count > 0)
        {
            MissionTaskSlot template = slots[0];
            for (int i = slots.Count; i < requestedParticipants; i++)
            {
                // 如果 LLM 给出的 roles 总人数小于请求参与人数，这里只做数量补齐，
                // 不做新的语义拆分，避免系统再次根据自然语言“脑补任务结构”。
                slots.Add(new MissionTaskSlot
                {
                    slotId = $"slot_{template.roleType}_{i + 1}",
                    slotLabel = $"{template.roleType}_{i + 1}",
                    roleType = template.roleType,
                    requiredAgentType = template.requiredAgentType,
                    target = template.target,
                    viaTargets = template.viaTargets ?? new string[0],
                    approachSide = template.approachSide,
                    altitudeMode = template.altitudeMode,
                    syncGroup = template.syncGroup,
                    dependsOnSlotIds = new string[0],
                    finalBehavior = template.finalBehavior,
                    completionCondition = template.completionCondition,
                    notes = "auto_filled_to_match_required_agent_count"
                });
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            slots[i] = NormalizeMissionTaskSlot(slots[i], missionDescription, i);
        }

        Debug.Log($"[Planning] 保守槽位展开完成: slotCount={slots.Count}");

        return slots.ToArray();
    }

    private TeamCoordinationDirective[] BuildDefaultMissionCoordinationDirectives(MissionType missionType)
    {
        TeamCoordinationDirective directive = new TeamCoordinationDirective
        {
            coordinationMode = missionType == MissionType.Cooperation ? TeamCoordinationMode.LooseSync : TeamCoordinationMode.Independent,
            leaderAgentId = string.Empty,
            sharedTarget = string.Empty,
            corridorReservationKey = string.Empty,
            yieldToAgentIds = new string[0],
            syncPointTargets = new string[0],
            formationSlot = string.Empty,
            notes = $"mission:{missionType}"
        };

        return new[] { directive };
    }

    private void CreateDefaultMission(string description, int agentCount)
    {
        // 创建默认任务分配
        currentMission = new MissionAssignment
        {
            missionId = $"mission_default_{DateTime.Now:yyyyMMdd_HHmmss}",
            missionDescription = description,
            missionType = MissionType.Cooperation,
            coordinatorId = agentProperties.AgentID,
            roles = new MissionRole[] {
                CreateRole(RoleType.Supporter, AgentType.Quadcopter, agentCount, 
                    new string[] { "执行合作任务", "完成用户指令" })
            },
            communicationMode = CommunicationMode.Hybrid,
            requiredAgentCount = agentCount,
            teamObjective = description,
            coordinationDirectives = BuildDefaultMissionCoordinationDirectives(MissionType.Cooperation),
            taskSlots = BuildTaskSlotsForMission(description, new MissionRole[] {
                CreateRole(RoleType.Supporter, AgentType.Quadcopter, agentCount, new string[] { "执行合作任务", "完成用户指令" })
            }, MissionType.Cooperation, agentCount)
        };

        CreateDefaultPlan(currentMission);
    }

    // 发送角色接受确认
    private void SendRoleAcceptance(string role, string reasoning)
    {
        if (commModule != null && currentMission != null)
        {
            RoleType acceptedRole;
            if (!Enum.TryParse(role, true, out acceptedRole))
            {
                acceptedRole = agentProperties.Role;
            }

            RoleAcceptancePayload payload = new RoleAcceptancePayload
            {
                missionId = currentMission.missionId,
                agentId = agentProperties.AgentID,
                acceptedRole = acceptedRole,
                acceptedSlotId = currentPlan != null && currentPlan.assignedSlot != null ? currentPlan.assignedSlot.slotId : string.Empty,
                agentType = agentProperties.Type,
                reasoning = reasoning,
                capabilitySummary = $"{agentProperties.Role}-{agentProperties.Type}"
            };

            commModule.SendStructuredMessage(currentMission.coordinatorId, MessageType.RoleAssignment, payload, 1);
        }
    }

    // 获取当前任务
    public Plan GetCurrentTask()
    {
        if (currentPlan == null || currentPlan.currentStep >= currentPlan.steps.Length)
            return new Plan { mission = "无任务", steps = new string[0], currentStep = 0 };

        return currentPlan;
    }

    // 获取当前任务进度
    public string GetMissionProgress()
    {
        if (currentPlan == null) return "无任务";

        return $"{currentPlan.agentRole} - 步骤 {currentPlan.currentStep + 1}/{currentPlan.steps.Length}";
    }

    /// <summary>
    /// 将当前计划上的协同约束压缩成日志/提示词可读摘要。
    /// 这里保留全部 directives，而不是只读第一条，避免复杂协同任务在摘要层被截断。
    /// </summary>
    private string BuildCurrentCoordinationSummary()
    {
        TeamCoordinationDirective[] directives = GetCurrentCoordinationDirectives();
        if (directives == null || directives.Length == 0) return "none";

        List<string> parts = new List<string>();
        for (int i = 0; i < directives.Length; i++)
        {
            TeamCoordinationDirective directive = directives[i];
            if (directive == null) continue;
            parts.Add($"#{i + 1}:mode={directive.coordinationMode},leader={directive.leaderAgentId},shared={directive.sharedTarget},corridor={directive.corridorReservationKey},formation={directive.formationSlot}");
        }

        return parts.Count > 0 ? string.Join(" || ", parts) : "none";
    }

    /// <summary>
    /// 对外暴露当前计划的协同摘要，供动作决策模块直接注入 prompt。
    /// </summary>
    public string GetCurrentCoordinationSummary()
    {
        return BuildCurrentCoordinationSummary();
    }

    /// <summary>
    /// 对外暴露当前槽位摘要。
    /// 动作决策层需要这个摘要来理解“我到底在执行哪一个具体岗位”，
    /// 而不是再从自然语言任务文本里猜测自己的位置。
    /// </summary>
    public string GetCurrentAssignedSlotSummary()
    {
        if (currentPlan == null || currentPlan.assignedSlot == null) return "none";

        MissionTaskSlot slot = currentPlan.assignedSlot;
        string deps = slot.dependsOnSlotIds != null && slot.dependsOnSlotIds.Length > 0
            ? string.Join("|", slot.dependsOnSlotIds)
            : "none";
        string via = slot.viaTargets != null && slot.viaTargets.Length > 0
            ? string.Join("|", slot.viaTargets)
            : "none";

        return $"slotId={slot.slotId},label={slot.slotLabel},role={slot.roleType},target={slot.target},via={via},syncGroup={slot.syncGroup},dependsOn={deps},final={slot.finalBehavior},done={slot.completionCondition}";
    }

    /// <summary>
    /// 对外暴露本地执行门控状态。
    /// ActionDecisionModule 在真正进入动作决策前会先读这个状态，防止上层调度器绕开协调者门控。
    /// </summary>
    public bool IsExecutionReleased()
    {
        return localExecutionReleased;
    }

    /// <summary>
    /// 判断当前任务是否需要“协调者统一放行”后才能执行。
    /// 多智能体协同任务默认走该门控，避免某一台先开跑。
    /// </summary>
    private bool ShouldGateExecutionByCoordinator(MissionAssignment mission)
    {
        if (mission == null) return false;
        int participantCount = mission.taskSlots != null && mission.taskSlots.Length > 0
            ? mission.taskSlots.Length
            : Mathf.Max(1, mission.requiredAgentCount);
        return participantCount > 1;
    }

    /// <summary>
    /// 协调者按“已接受 + 依赖已满足”逐槽位放行执行。
    /// 关键点：
    /// 1) 不再要求所有未来阶段都先接受完才允许前一阶段启动；
    /// 2) dependsOnSlotIds 真正参与运行时门控，而不是只停留在结构化字段里；
    /// 3) 复杂任务会经历多轮 release，而不是一次性全队开跑。
    /// </summary>
    private void TryReleaseAssignedExecution()
    {
        if (!IsCurrentMissionCoordinator() || currentMission == null) return;
        if (assignedTeamDecisions == null || assignedTeamDecisions.Count == 0) return;
        bool releasedAny = false;

        foreach (var kv in assignedTeamDecisions)
        {
            string agentId = kv.Key;
            RoleDecisionPayload payload = kv.Value;
            if (payload == null || string.IsNullOrWhiteSpace(agentId)) continue;
            if (releasedAssignedAgents.Contains(agentId)) continue;
            if (!acceptedAssignedAgents.Contains(agentId)) continue;
            if (!AreSlotDependenciesSatisfied(payload.assignedSlot)) continue;

            releasedAssignedAgents.Add(agentId);
            releasedAny = true;

            if (string.Equals(payload.agentId, agentProperties.AgentID, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseExecutionForAssignedPlan(payload.assignedSlot != null ? payload.assignedSlot.slotId : string.Empty);
                continue;
            }

            TaskProgressPayload releasePayload = new TaskProgressPayload
            {
                missionId = currentMission.missionId,
                missionDescription = currentMission.missionDescription,
                agentId = agentProperties.AgentID,
                role = payload.assignedRole,
                slotId = payload.assignedSlot != null ? payload.assignedSlot.slotId : string.Empty,
                completedStep = "none",
                nextStep = currentPlan != null && currentPlan.steps != null && currentPlan.steps.Length > 0 ? currentPlan.steps[0] : "none",
                completedStepIndex = 0,
                totalStepCount = 0,
                status = "execution_released",
                coordinationNote = payload.assignedSlot != null ? payload.assignedSlot.slotLabel : "none"
            };

            commModule.SendStructuredMessage(payload.agentId, MessageType.TaskUpdate, releasePayload, 1);
        }

        if (releasedAny)
        {
            teamExecutionReleased = true;
            Debug.Log($"[Planning] 协调者增量放行执行: released={releasedAssignedAgents.Count}/{assignedTeamDecisions.Count}, completed={completedAssignedAgents.Count}/{assignedTeamDecisions.Count}");
        }
    }

    /// <summary>
    /// 判断某个槽位是否已经满足执行依赖。
    /// 依赖判断只看结构化 dependsOnSlotIds，不再从任务文本猜“谁应该先做”。
    /// </summary>
    private bool AreSlotDependenciesSatisfied(MissionTaskSlot slot)
    {
        if (slot == null || slot.dependsOnSlotIds == null || slot.dependsOnSlotIds.Length == 0) return true;
        if (assignedTeamDecisions == null || assignedTeamDecisions.Count == 0) return false;

        HashSet<string> completedSlotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in assignedTeamDecisions)
        {
            if (!completedAssignedAgents.Contains(kv.Key)) continue;
            string completedSlotId = kv.Value != null && kv.Value.assignedSlot != null ? kv.Value.assignedSlot.slotId : string.Empty;
            if (!string.IsNullOrWhiteSpace(completedSlotId))
            {
                completedSlotIds.Add(completedSlotId);
            }
        }

        for (int i = 0; i < slot.dependsOnSlotIds.Length; i++)
        {
            string dep = slot.dependsOnSlotIds[i];
            if (string.IsNullOrWhiteSpace(dep)) continue;
            if (!completedSlotIds.Contains(dep))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 本地收到协调者的统一放行后，允许开始执行已生成的计划。
    /// </summary>
    public void ReleaseExecutionForAssignedPlan(string slotId)
    {
        if (currentMission == null || currentPlan == null) return;
        if (currentPlan.assignedSlot != null &&
            !string.IsNullOrWhiteSpace(slotId) &&
            !string.Equals(currentPlan.assignedSlot.slotId, slotId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        localExecutionReleased = true;
        Debug.Log($"[Planning] 本地执行已放行: agent={agentProperties.AgentID}, slot={currentPlan.assignedSlot?.slotLabel}");
    }

    /// <summary>
    /// 当前智能体是否处于“本地步骤已完成，但整队任务仍未完成”的等待状态。
    /// </summary>
    public bool IsWaitingForTeamCompletion()
    {
        if (!IsCurrentMissionCoordinator()) return false;
        if (currentPlan == null || currentPlan.steps == null) return false;
        if (currentPlan.currentStep < currentPlan.steps.Length) return false;
        return !missionCompletionAggregated;
    }

    /// <summary>
    /// 判断当前智能体是否是当前任务的协调者。
    /// </summary>
    private bool IsCurrentMissionCoordinator()
    {
        return currentMission != null &&
               !string.IsNullOrWhiteSpace(currentMission.coordinatorId) &&
               string.Equals(currentMission.coordinatorId, agentProperties.AgentID, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 协调者侧处理“角色接受回执”。
    /// 只记录本任务、且确实被分配过槽位的成员，避免旧任务或无关消息污染状态。
    /// </summary>
    public void HandleRoleAcceptancePayload(RoleAcceptancePayload payload)
    {
        if (payload == null || !IsCurrentMissionCoordinator() || currentMission == null) return;
        if (!string.Equals(payload.missionId, currentMission.missionId, StringComparison.OrdinalIgnoreCase)) return;
        if (!assignedTeamDecisions.TryGetValue(payload.agentId, out RoleDecisionPayload decision) || decision == null) return;

        string expectedSlotId = decision.assignedSlot != null ? decision.assignedSlot.slotId : string.Empty;
        if (!string.IsNullOrWhiteSpace(expectedSlotId) &&
            !string.IsNullOrWhiteSpace(payload.acceptedSlotId) &&
            !string.Equals(expectedSlotId, payload.acceptedSlotId, StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning($"[Planning] 忽略不匹配的槽位接受回执: agent={payload.agentId}, expected={expectedSlotId}, actual={payload.acceptedSlotId}");
            return;
        }

        acceptedAssignedAgents.Add(payload.agentId);
        Debug.Log($"[Planning] 协调者收到角色接受回执: agent={payload.agentId}, role={payload.acceptedRole}, slot={payload.acceptedSlotId}, accepted={acceptedAssignedAgents.Count}/{assignedTeamDecisions.Count}");
        TryReleaseAssignedExecution();
    }

    /// <summary>
    /// 协调者侧处理任务进度/完成上报。
    /// 任务完成聚合只按已分配槽位的成员统计，防止某个成员单独完成就提前收口。
    /// </summary>
    public void HandleTaskProgressPayload(TaskProgressPayload payload)
    {
        if (payload == null || !IsCurrentMissionCoordinator() || currentMission == null) return;
        if (!string.Equals(payload.missionId, currentMission.missionId, StringComparison.OrdinalIgnoreCase)) return;
        if (!assignedTeamDecisions.TryGetValue(payload.agentId, out RoleDecisionPayload decision) || decision == null) return;

        string expectedSlotId = decision.assignedSlot != null ? decision.assignedSlot.slotId : string.Empty;
        if (!string.IsNullOrWhiteSpace(expectedSlotId) &&
            !string.IsNullOrWhiteSpace(payload.slotId) &&
            !string.Equals(expectedSlotId, payload.slotId, StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning($"[Planning] 忽略不匹配的任务进度: agent={payload.agentId}, expectedSlot={expectedSlotId}, actualSlot={payload.slotId}, status={payload.status}");
            return;
        }

        if (string.Equals(payload.status, "mission_completed", StringComparison.OrdinalIgnoreCase))
        {
            completedAssignedAgents.Add(payload.agentId);
            Debug.Log($"[Planning] 协调者记录槽位完成: agent={payload.agentId}, slot={payload.slotId}, completed={completedAssignedAgents.Count}/{assignedTeamDecisions.Count}");
            // 某个前置槽位完成后，可能会解锁其后的 dependent slots，因此这里立即尝试增量放行。
            TryReleaseAssignedExecution();
            TryFinalizeCoordinatedMission();
            return;
        }

        Debug.Log($"[Planning] 协调者收到任务进度: agent={payload.agentId}, slot={payload.slotId}, status={payload.status}, step={payload.completedStep}, next={payload.nextStep}");
    }

    /// <summary>
    /// 协调者检查是否所有已分配槽位都已完成。
    /// 只有全部槽位完成后，才允许把整个 mission 判定为完成。
    /// </summary>
    private void TryFinalizeCoordinatedMission()
    {
        if (!IsCurrentMissionCoordinator() || currentMission == null) return;
        if (missionCompletionAggregated) return;
        if (assignedTeamDecisions == null || assignedTeamDecisions.Count == 0) return;

        foreach (string agentId in assignedTeamDecisions.Keys)
        {
            if (!completedAssignedAgents.Contains(agentId))
            {
                return;
            }
        }

        missionCompletionAggregated = true;
        string slotSummary = string.Join(", ", assignedTeamDecisions.Values
            .Where(v => v != null && v.assignedSlot != null)
            .Select(v => $"{v.agentId}:{v.assignedSlot.slotLabel}"));

        memoryModule.AddMemory($"协调任务完成：{currentMission.missionDescription}", "achievement", 0.95f);
        Debug.Log($"[Planning] 协调任务完成: mission={currentMission.missionDescription}, slots={slotSummary}");
    }

    // 标记当前任务完成
    public void CompleteCurrentTask()
    {
        if (currentPlan != null && currentPlan.currentStep < currentPlan.steps.Length)
        {
            string completedStep = currentPlan.steps[currentPlan.currentStep];
            memoryModule.AddMemory($"完成步骤: {completedStep}", "progress", 0.7f);
            currentPlan.currentStep++;

            // 如果是最后一步，报告任务完成
            if (currentPlan.currentStep >= currentPlan.steps.Length)
            {
                string slotLabel = currentPlan.assignedSlot != null ? currentPlan.assignedSlot.slotLabel : currentPlan.agentRole.ToString();
                memoryModule.AddMemory($"完成本地槽位: {slotLabel}", "achievement", 0.9f);

                if (IsCurrentMissionCoordinator())
                {
                    completedAssignedAgents.Add(agentProperties.AgentID);
                    Debug.Log($"[Planning] 协调者本地槽位完成: agent={agentProperties.AgentID}, slot={currentPlan.assignedSlot?.slotId}, completed={completedAssignedAgents.Count}/{assignedTeamDecisions.Count}");
                    // 协调者自己完成前置槽位后，也要立刻尝试释放后继槽位。
                    TryReleaseAssignedExecution();
                    TryFinalizeCoordinatedMission();
                }
                else
                {
                    ReportMissionCompletion();
                }
            }
            else
            {
                // 报告步骤完成
                ReportStepCompletion(completedStep);
            }
        }
    }

    // 报告步骤完成
    private void ReportStepCompletion(string step)
    {
        if (commModule != null && currentMission != null)
        {
            string nextStep = (currentPlan != null && currentPlan.currentStep >= 0 && currentPlan.currentStep < currentPlan.steps.Length)
                ? (currentPlan.steps[currentPlan.currentStep] ?? string.Empty)
                : "none";
            TaskProgressPayload payload = new TaskProgressPayload
            {
                missionId = currentMission.missionId,
                missionDescription = currentPlan != null ? currentPlan.mission : currentMission.missionDescription,
                agentId = agentProperties.AgentID,
                role = currentPlan != null ? currentPlan.agentRole : agentProperties.Role,
                slotId = currentPlan != null && currentPlan.assignedSlot != null ? currentPlan.assignedSlot.slotId : string.Empty,
                completedStep = step ?? string.Empty,
                nextStep = nextStep,
                completedStepIndex = Mathf.Max(0, currentPlan != null ? currentPlan.currentStep : 0),
                totalStepCount = currentPlan != null && currentPlan.steps != null ? currentPlan.steps.Length : 0,
                status = "step_completed",
                coordinationNote = BuildCurrentCoordinationSummary()
            };

            commModule.SendStructuredMessage(currentMission.coordinatorId, MessageType.TaskUpdate, payload, 1);
        }
    }

    // 报告任务完成
    private void ReportMissionCompletion()
    {
        if (commModule != null && currentMission != null)
        {
            TaskProgressPayload payload = new TaskProgressPayload
            {
                missionId = currentMission.missionId,
                missionDescription = currentPlan != null ? currentPlan.mission : currentMission.missionDescription,
                agentId = agentProperties.AgentID,
                role = currentPlan != null ? currentPlan.agentRole : agentProperties.Role,
                slotId = currentPlan != null && currentPlan.assignedSlot != null ? currentPlan.assignedSlot.slotId : string.Empty,
                completedStep = "none",
                nextStep = "none",
                completedStepIndex = currentPlan != null && currentPlan.steps != null ? currentPlan.steps.Length : 0,
                totalStepCount = currentPlan != null && currentPlan.steps != null ? currentPlan.steps.Length : 0,
                status = "mission_completed",
                coordinationNote = BuildCurrentCoordinationSummary()
            };

            commModule.SendStructuredMessage(currentMission.coordinatorId, MessageType.TaskCompletion, payload, 1);
        }
    }

    // 检查是否有活跃任务
    public bool HasActiveMission()
    {
        return currentPlan != null &&
               currentPlan.currentStep < currentPlan.steps.Length &&
               localExecutionReleased;
    }

    // 获取任务优先级
    public Priority GetMissionPriority()
    {
        return currentPlan?.priority ?? Priority.Low;
    }
}

// 示例用法：
// // 中心协调者发送任务
// var mission = new MissionAssignment {
//     missionId = "mission_001",
//     missionDescription = "所有无人机分成两组进行区域对抗，红队防守基地，蓝队进攻",
//     missionType = "对抗任务",
//     coordinatorId = "CentralCommand",
//     roles = new MissionRole[] {
//         new MissionRole { 
//             roleName = "攻击手", 
//             agentType = "Quadcopter", 
//             responsibilities = new string[] { "突破防线", "攻击目标" } 
//         },
//         new MissionRole { 
//             roleName = "侦查员", 
//             agentType = "Quadcopter", 
//             responsibilities = new string[] { "侦查敌情", "报告位置" } 
//         }
//     }
// };

// // 智能体接收任务
// planningModule.ReceiveMissionAssignment(mission);
