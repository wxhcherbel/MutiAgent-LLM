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

/// <summary>
/// 本地执行计划。
/// 它只是“当前智能体该做什么”的语义合同，不是动作序列，也不是连续控制参数容器。
/// ActionDecisionModule 会基于它再做目标选择、动作决策和执行。
/// </summary>
[Serializable]
public class Plan
{
    public string missionId;         // 团队任务 ID
    public string mission;           // 总任务描述
    public MissionType missionType;  // 任务类型
    public TeamRelationshipType relationshipType; // 任务关系类型（合作/竞争/对抗/混合）
    public string successCondition;  // 团队成功条件
    public string failureCondition;  // 团队失败条件
    public NavigationPolicy navigationPolicy; // 任务级默认导航策略（由 LLM 输出，step 可覆盖）
    public RoleType agentRole;       // 本智能体在此任务中的角色（使用枚举）
    public PlanStepDefinition[] planSteps; // 简化后的本地步骤定义，供决策层直接消费
    public string[] stepActionTypes; // 兼容桥接缓存：按 planSteps 推导出的动作标签，不再是规划主输出
    public string[] stepNavigationModes; // 兼容桥接缓存：按 planSteps 推导出的导航标签，不再是规划主输出
    public StepIntentDefinition[] stepIntents; // 兼容桥接缓存：供动作层老 helper 读取的结构化意图
    public RoutePolicyDefinition[] stepRoutePolicies; // 兼容桥接缓存：供动作层老 helper 读取的路径策略
    public TeamCoordinationDirective[] coordinationDirectives; // 本计划附带的多智能体协同约束
    public MissionTaskSlot assignedSlot; // 当前智能体被分配到的具体子任务槽位
    public int currentStep;          // 当前步骤
    public DateTime created;         // 创建时间
    public Priority priority;        // 任务优先级
    public string assignedBy;        // 任务分配者
    public CommunicationMode commMode; // 通信模式

    public string[] steps // 兼容旧读取逻辑保留；实际文本来源统一是 planSteps。
    {
        get
        {
            return planSteps == null
                ? Array.Empty<string>()
                : planSteps.Select(step => step != null && !string.IsNullOrWhiteSpace(step.text) ? step.text.Trim() : string.Empty).ToArray();
        }
    }
}

/// <summary>
/// 团队级任务分配结果。
/// 当前版本只保留“用户自然语言任务 -> 团队任务 -> 岗位分配 -> 本地计划”这一条主链。
/// missionSource / sourceGoalId / sourceEventId 仍保留为兼容字段，但不再作为当前主路径入口。
/// </summary>
[Serializable]
public class MissionAssignment
{
    public string missionId;         // 任务ID
    public string missionDescription;// 任务描述
    public string missionSource;     // 任务来源，例如 UserMission / WorldEvent / Routine
    public string sourceGoalId;      // 若该任务来自自治目标，这里记录 goalId
    public string sourceEventId;     // 若该任务来自世界事件，这里记录 eventId
    public MissionType missionType;  // 任务类型（使用枚举）
    public TeamRelationshipType relationshipType; // 团队关系类型
    public string coordinatorId;     // 协调者ID
    public MissionRole[] roles;      // 需要的角色分配
    public CommunicationMode communicationMode; // 推荐通信模式
    public int requiredAgentCount;   // 需要智能体数量
    public string teamObjective;     // 队伍级任务目标摘要，便于多智能体统一理解
    public string successCondition;  // 团队成功条件
    public string failureCondition;  // 团队失败条件
    public MissionPhaseDefinition[] phaseTemplates; // 团队阶段模板
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

// 用于解析个人计划阶段 LLM 响应的辅助类。
[Serializable]
public class PlanResponse
{
    public string assignedRole; // LLM 为当前智能体建议的角色；协调者已明确分配时这里只是兜底。
    public PlanStepDefinition[] planSteps; // 当前主路径唯一要求的步骤输出。
    public string[] steps; // 兼容旧容错解析保留；新主路径不再依赖它。
    public string[] stepActionTypes; // 兼容旧解析保留；新主路径不再要求 LLM 填写。
    public string[] stepNavigationModes; // 兼容旧解析保留；新主路径不再要求 LLM 填写。
    public StepIntentDefinition[] stepIntents; // 兼容旧解析保留；当前由系统按 planSteps 推导。
    public RoutePolicyDefinition[] stepRoutePolicies; // 兼容旧解析保留；当前由系统按 planSteps 推导。
    public TeamCoordinationDirective[] coordinationDirectives; // 当前智能体在此任务中的协同规则。
    public string missionNavigationPolicy; // 任务级默认导航策略。
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

/// <summary>
/// PlanningModule 的职责重新对齐为“语义计划层”：
/// 1) 它接收用户自然语言任务；
/// 2) 它把输入整理成团队语义、岗位分配和本地 Plan；
/// 3) 它负责团队分槽位和本地步骤计划；
/// 4) 它不负责动作序列、不负责路径细节，也不输出连续控制参数。
///
/// 换句话说：
/// - PlanningModule 只回答“任务是什么、怎么分工、我这一步想做什么”；
/// - ActionDecisionModule 再回答“目标选哪个格、接下来做什么动作”。
/// </summary>
public class PlanningModule : MonoBehaviour
{
    private MemoryModule memoryModule;
    private ReflectionModule reflectionModule;
    private LLMInterface llmInterface;
    private CampusGrid2D campusGrid;
    private AgentProperties agentProperties;
    private CommunicationModule commModule;

    public Plan currentPlan { get; private set; }
    public MissionAssignment currentMission { get; private set; }
    public CommunicationMode currentCommMode { get; private set; }
    public TeamExecutionState currentTeamExecutionState { get; private set; } // 当前任务最小共享执行状态，供决策层读取。

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
    private bool planningRequestInFlight; // 当前是否有规划协程在执行，避免用户入口重入


    void Start()
    {
        memoryModule = GetComponent<MemoryModule>();
        reflectionModule = GetComponent<ReflectionModule>();
        llmInterface = FindObjectOfType<LLMInterface>();
        campusGrid = FindObjectOfType<CampusGrid2D>();
        
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
    
    /// <summary>
    /// 用户手动提交任务时的主入口。
    /// 这条链路会继续进入团队语义解析、团队分配、个人计划生成，直到后续执行完成。
    /// </summary>
    /// <param name="missionDescription">用户输入的自然语言任务描述，后续会作为规划主语义输入发给 LLM。</param>
    /// <param name="agentCount">本轮任务期望投入的智能体数量，阶段拆解和 taskSlot 数量都会受它约束。</param>
    public void SubmitMissionRequest(string missionDescription, int agentCount)
    {
        // 立刻标记“规划请求进行中”，避免分析期间又被新的用户入口重入。
        planningRequestInFlight = true;
        // 启动真正的规划协程；后面的团队语义、岗位分配和个人计划都会从这里继续。
        StartCoroutine(AnalyzeMissionDescription(missionDescription, agentCount));
    }

    /// <summary>
     /// 当前是否正在处理规划协程。
     /// 这个门控用于避免：
    /// 1) 用户任务还在分析时又重复触发；
    /// 2) 团队分配还没结束时再次发起新计划。
     /// </summary>
    public bool IsPlanningBusy()
    {
        return planningRequestInFlight;
    }

    /// <summary>
    /// 返回当前任务的最小共享执行状态。
    /// 决策层只读这个快照，不直接修改协调者内部集合。
    /// </summary>
    public TeamExecutionState GetCurrentTeamExecutionState()
    {
        if (currentTeamExecutionState != null) return currentTeamExecutionState;

        return new TeamExecutionState
        {
            missionId = currentMission != null ? currentMission.missionId : string.Empty,
            currentPhaseId = ResolveCurrentPhaseIdForSnapshot(),
            releasedSlotIds = Array.Empty<string>(),
            completedSlotIds = Array.Empty<string>(),
            readyAgentIds = Array.Empty<string>(),
            claims = Array.Empty<TargetClaim>(),
            sharedFacts = Array.Empty<SharedFact>()
        };
    }

    /// <summary>
    /// 给动作层和提示词构造一个紧凑的团队执行状态摘要。
    /// 这里只暴露离散执行真正需要的放行/完成/共享事实，不再暴露旧复杂自治语义。
    /// </summary>
    public string BuildTeamExecutionStateSummary()
    {
        TeamExecutionState state = GetCurrentTeamExecutionState();
        if (state == null) return "teamState=none";

        string released = state.releasedSlotIds != null && state.releasedSlotIds.Length > 0
            ? string.Join("|", state.releasedSlotIds)
            : "none";
        string completed = state.completedSlotIds != null && state.completedSlotIds.Length > 0
            ? string.Join("|", state.completedSlotIds)
            : "none";
        string ready = state.readyAgentIds != null && state.readyAgentIds.Length > 0
            ? string.Join("|", state.readyAgentIds)
            : "none";
        string claims = state.claims != null && state.claims.Length > 0
            ? string.Join(" || ", state.claims.Where(c => c != null).Select(c => $"{c.claimKind}:{c.claimKey}->{c.ownerAgentId}").ToArray())
            : "none";
        string facts = state.sharedFacts != null && state.sharedFacts.Length > 0
            ? string.Join(" || ", state.sharedFacts.Where(f => f != null).Select(f => $"{f.factType}:{f.subjectText}@{(f.lastKnownCell != null ? $"{f.lastKnownCell.x},{f.lastKnownCell.z}" : "world")}").ToArray())
            : "none";

        return $"missionId={state.missionId},phase={state.currentPhaseId},released={released},completed={completed},ready={ready},claims={claims},facts={facts}";
    }

    /// <summary>
    /// 重新生成当前任务的共享执行状态快照。
    /// 当前版本优先复用协调者已有的 released/completed/accepted 集合，避免再引入一套并行状态机。
    /// </summary>
    private void RefreshTeamExecutionStateSnapshot()
    {
        currentTeamExecutionState = new TeamExecutionState
        {
            missionId = currentMission != null
                ? currentMission.missionId
                : (currentTeamExecutionState != null ? currentTeamExecutionState.missionId : string.Empty),
            currentPhaseId = ResolveCurrentPhaseIdForSnapshot(),
            releasedSlotIds = ResolveReleasedSlotIdsFromAssignments(),
            completedSlotIds = ResolveCompletedSlotIdsFromAssignments(),
            readyAgentIds = acceptedAssignedAgents.Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id).ToArray(),
            claims = currentTeamExecutionState != null && currentTeamExecutionState.claims != null ? currentTeamExecutionState.claims : Array.Empty<TargetClaim>(),
            sharedFacts = currentTeamExecutionState != null && currentTeamExecutionState.sharedFacts != null ? currentTeamExecutionState.sharedFacts : Array.Empty<SharedFact>()
        };
    }

    /// <summary>
    /// 初始化一轮新任务的团队执行状态。
    /// 只保留最小共享状态，不再额外挂接自治目标/世界事件来源。
    /// </summary>
    private void ResetTeamExecutionState(MissionAssignment mission)
    {
        currentTeamExecutionState = new TeamExecutionState
        {
            missionId = mission != null ? mission.missionId : string.Empty,
            currentPhaseId = mission != null && mission.phaseTemplates != null && mission.phaseTemplates.Length > 0
                ? (mission.phaseTemplates[0] != null ? mission.phaseTemplates[0].phaseId ?? string.Empty : string.Empty)
                : string.Empty,
            releasedSlotIds = Array.Empty<string>(),
            completedSlotIds = Array.Empty<string>(),
            readyAgentIds = Array.Empty<string>(),
            claims = Array.Empty<TargetClaim>(),
            sharedFacts = Array.Empty<SharedFact>()
        };
    }

    /// <summary>
    /// 取当前快照里的阶段 ID。
    /// 若还没有状态快照，则保守回退到任务第一阶段。
    /// </summary>
    private string ResolveCurrentPhaseIdForSnapshot()
    {
        if (currentTeamExecutionState != null && !string.IsNullOrWhiteSpace(currentTeamExecutionState.currentPhaseId))
        {
            return currentTeamExecutionState.currentPhaseId;
        }

        if (currentMission != null && currentMission.phaseTemplates != null)
        {
            MissionPhaseDefinition phase = currentMission.phaseTemplates.FirstOrDefault(p => p != null && !string.IsNullOrWhiteSpace(p.phaseId));
            if (phase != null) return phase.phaseId;
        }

        return string.Empty;
    }

    /// <summary>
    /// 把已放行智能体映射成 releasedSlotIds。
    /// 决策层关心的是“哪些岗位已经被放行”，不是内部 HashSet 的具体实现。
    /// </summary>
    private string[] ResolveReleasedSlotIdsFromAssignments()
    {
        if (assignedTeamDecisions == null || assignedTeamDecisions.Count == 0 || releasedAssignedAgents == null || releasedAssignedAgents.Count == 0)
        {
            return Array.Empty<string>();
        }

        return assignedTeamDecisions
            .Where(kv => kv.Value != null &&
                         kv.Value.assignedSlot != null &&
                         !string.IsNullOrWhiteSpace(kv.Value.assignedSlot.slotId) &&
                         releasedAssignedAgents.Contains(kv.Key))
            .Select(kv => kv.Value.assignedSlot.slotId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>
    /// 把已完成智能体映射成 completedSlotIds。
    /// 这样竞争/对抗/混合任务的决策层能知道哪些岗位已经完成、哪些仍在推进。
    /// </summary>
    private string[] ResolveCompletedSlotIdsFromAssignments()
    {
        if (assignedTeamDecisions == null || assignedTeamDecisions.Count == 0 || completedAssignedAgents == null || completedAssignedAgents.Count == 0)
        {
            return Array.Empty<string>();
        }

        return assignedTeamDecisions
            .Where(kv => kv.Value != null &&
                         kv.Value.assignedSlot != null &&
                         !string.IsNullOrWhiteSpace(kv.Value.assignedSlot.slotId) &&
                         completedAssignedAgents.Contains(kv.Key))
            .Select(kv => kv.Value.assignedSlot.slotId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>
    /// 接收协调者广播或本地创建好的任务对象。
    /// 当还没有最终角色时，这里只做“角色偏好上报”；当角色和槽位已确定时，这里直接进入个人计划生成。
    /// </summary>
    /// <param name="mission">已经结构化完成的团队任务对象，包含任务类型、角色需求、槽位和协同规则。</param>
    /// <param name="specificRole">若非空，表示当前智能体已经被协调者明确分配了角色；为空则只做角色偏好分析。</param>
    /// <param name="specificSlot">若非空，表示当前智能体已经被分配了具体任务槽位；个人计划要围绕这个槽位展开。</param>
    public void ReceiveMissionAssignment(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)
    {
        // 先把当前任务缓存到模块级状态，后续计划生成、进度上报、完成收口都依赖它。
        currentMission = mission;
        // 把任务推荐的通信模式同步到当前模块，供后面发消息和动作层读状态时直接使用。
        currentCommMode = mission.communicationMode;
        // 新任务切入时重置团队执行状态；同一任务重复进入时只刷新快照，不清空已有共享事实。
        if (currentTeamExecutionState == null ||
            !string.Equals(currentTeamExecutionState.missionId, mission != null ? mission.missionId : string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            ResetTeamExecutionState(mission);
        }
        RefreshTeamExecutionStateSnapshot();

        // 角色还没定时，不要提前给自己建个人计划；先只分析“我更适合什么角色”。
        if (specificRole == null)
        {
            // 启动角色偏好协程，让当前智能体基于 mission + 自身属性向 LLM 询问偏好排序。
            StartCoroutine(AnalyzeRolePreference(mission, (preferences) =>
            {
                // 这个内嵌回调只做一件事：把分析出的偏好立刻回传给协调者。
                SendRolePreferenceToCoordinator(mission, preferences);
            }));
        }
        else
        {
            // 角色已定时，先判断是否需要协调者统一放行；多人协同时默认先锁住本地执行。
            localExecutionReleased = !ShouldGateExecutionByCoordinator(mission);
            // 进入个人计划生成前，重新置位规划忙碌标记，避免生成计划过程中又触发新规划。
            planningRequestInFlight = true;
            // 启动个人计划协程，把“团队任务 + 我被分配的角色/槽位”细化成 currentPlan。
            StartCoroutine(AnalyzeMissionAndCreatePlan(mission, specificRole.Value, specificSlot));
        }
    }

    /// <summary>
    /// 把目标来源、历史经验和反思策略压成统一上下文，供团队语义 prompt 和个人计划 prompt 共用。
    /// </summary>
    /// <param name="missionText">当前要规划的任务文本，可能是用户描述，也可能是已经分配好的局部任务文本。</param>
    /// <param name="missionId">当前任务 ID；存在时可让记忆和反思模块定位到同一轮任务上下文。</param>
    /// <param name="slot">当前智能体被分配的任务槽位；个人计划阶段会把它带进去，团队级阶段通常为空。</param>
    /// <param name="role">当前智能体的已分配角色；个人计划阶段非空，任务总分析阶段通常为空。</param>
    /// <param name="teamObjective">团队级总目标摘要，供记忆和反思模块理解“这轮任务全队到底在干什么”。</param>
    private string BuildPlanningCognitiveContext(string missionText, string missionId = "", MissionTaskSlot slot = null, RoleType? role = null, string teamObjective = "")
    {
        // 再向记忆模块请求一小段“和当前任务最相关的历史经验”，避免 prompt 过长。
        string memoryContext = memoryModule != null
            ? memoryModule.BuildPlanningContext(new PlanningMemoryContextRequest
            {
                // 当前轮任务主文本，用来从记忆里检索相似经历。
                missionText = missionText,
                // 任务 ID 用于绑定同一轮历史记录。
                missionId = missionId,
                // 团队级目标帮助记忆模块理解更上层的共同意图。
                teamObjective = teamObjective,
                // 已分配角色如果存在，就让记忆检索更贴近该角色的经验。
                roleName = role.HasValue ? role.Value.ToString() : string.Empty,
                // 槽位 ID 用于定位当前岗位。
                slotId = slot != null ? slot.slotId : string.Empty,
                // 槽位名提供更适合人读的岗位标签。
                slotLabel = slot != null ? slot.slotLabel : string.Empty,
                // 槽位最终目标用于记忆检索“类似目标”的执行经验。
                slotTarget = slot != null ? slot.target : string.Empty,
                // 经过点列表用于提示“先经过哪里再去终点”的历史模式。
                viaTargets = slot != null ? slot.viaTargets : null,
                // 限制最多注入多少条历史记忆，避免 prompt 爆炸。
                maxMemories = 4,
                // 限制最多注入多少条提炼后的洞见。
                maxInsights = 2
            })
            : "无稳定历史经验";

        // 再向反思模块请求少量“执行策略提醒”，例如过去哪些坑要避开。
        string reflectionContext = reflectionModule != null
            ? reflectionModule.GetPlanningGuidance(missionText, missionId, slot, 2)
            : "无稳定反思策略";

        // 当前版本只保留用户任务入口，因此来源上下文固定为 UserMission。
        return $"[目标来源]\nsource=UserMission\n\n[历史经验]\n{memoryContext}\n\n[反思策略]\n{reflectionContext}";
    }

    /// <summary>
    /// 把当前任务来源固定成用户任务。
    /// 当前版本不再维护自治目标和世界事件并行入口。
    /// </summary>
    private void ApplyMissionSourceContext(MissionAssignment mission)
    {
        if (mission == null) return;
        mission.missionSource = "UserMission";
        mission.sourceGoalId = string.Empty;
        mission.sourceEventId = string.Empty;
    }

    /// <summary>
    /// 调试辅助：输出当前地图里的 collection 摘要。
    ///
    /// 注意：
    /// 1) 这份摘要现在不再直接注入给 LLM；
    /// 2) 当前架构里，LLM 只负责提自然语言目标语义；
    /// 3) 具体 collectionKey / memberEntityIds 由系统 grounded 层再去匹配。
    /// </summary>
    private string BuildCampusMapPlanningContext()
    {
        if (campusGrid == null)
        {
            campusGrid = FindObjectOfType<CampusGrid2D>();
        }

        if (campusGrid == null)
        {
            return "[地图目录]\n地图目录暂不可用";
        }

        string catalog = campusGrid.BuildFeatureCatalogSummary(8);
        return $"[地图目录]\n{catalog}\n\n[目录使用规则]\n- collectionKey 必须直接复用上面目录里的键。\n- memberEntityIds 若非空，必须填写目录里真实存在的 alias/uid。\n- 大节点不是一个点，而是一块有范围的实体；执行层会根据几何范围自动找接近点和环绕半径。";
    }

    /// <summary>
    /// 把槽位里的集合成员摘要成简短文本，便于直接喂给个人计划 prompt。
    /// </summary>
    private static string BuildSlotMemberSummary(MissionTaskSlot slot)
    {
        if (slot == null || slot.targetRef == null || slot.targetRef.memberEntityIds == null || slot.targetRef.memberEntityIds.Length == 0)
        {
            return "none";
        }

        string[] members = slot.targetRef.memberEntityIds
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
        if (members.Length == 0) return "none";

        string preview = string.Join("|", members.Take(10).ToArray());
        if (members.Length > 10) preview += "|...";
        return $"count={members.Length},members={preview}";
    }

    /// <summary>
    /// 给“结构化目标引用”挑一个更适合人类阅读的文本。
    /// 这里故意优先用 displayName / rawText / selectorText，
    /// 因为这些字段更接近“用户原话里真正说了什么目标”；
    /// 不会像 executableQuery / entityId 那样，更偏向系统内部落地后的 UID / alias。
    /// </summary>
    private static string ResolveStructuredTargetDisplayText(StructuredTargetReference targetRef, string fallbackText = "")
    {
        if (targetRef == null)
        {
            return string.IsNullOrWhiteSpace(fallbackText) ? string.Empty : fallbackText.Trim();
        }

        string[] candidates =
        {
            targetRef.displayName,
            targetRef.rawText,
            targetRef.selectorText,
            targetRef.areaHint,
            targetRef.anchorText,
            targetRef.entityId,
            targetRef.executableQuery,
            fallbackText
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(candidates[i]))
            {
                return candidates[i].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 把结构化目标引用数组同步回“更像人话”的旧字符串列表。
    /// 这样日志、提示词和旧链路仍然更容易读，
    /// 但真正执行时用的 grounded query 仍保存在 targetRef 里。
    /// </summary>
    private static string[] ResolveStructuredTargetDisplayTexts(StructuredTargetReference[] refs, string[] fallbackTexts = null)
    {
        List<string> values = new List<string>();
        if (refs != null)
        {
            for (int i = 0; i < refs.Length; i++)
            {
                string display = ResolveStructuredTargetDisplayText(refs[i]);
                if (!string.IsNullOrWhiteSpace(display))
                {
                    values.Add(display);
                }
            }
        }

        if (values.Count == 0 && fallbackTexts != null)
        {
            values.AddRange(fallbackTexts.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
        }

        return values.ToArray();
    }

    /// <summary>
    /// 判断一个结构化目标是否至少还保留了“人能看懂的语义”或“系统能落地的锚点”。
    /// 这个判断在迁移期很重要：
    /// 以前只看 executableQuery，但现在未 grounding 的目标也可能先只有自然语言名字。
    /// </summary>
    private static bool HasUsableStructuredTarget(StructuredTargetReference targetRef, string fallbackText = "")
    {
        return !string.IsNullOrWhiteSpace(ResolveStructuredTargetDisplayText(targetRef, fallbackText)) ||
               !string.IsNullOrWhiteSpace(ResolveStructuredTargetQuery(targetRef, fallbackText));
    }

    /// <summary>
    /// 把“结构化目标引用”重新压成短摘要，方便放进日志和 prompt。
    /// 这里同时保留两层信息：
    /// 1) display：人类读得懂的目标名；
    /// 2) 只保留 LLM 真正需要理解的轻量语义，不再暴露 entityId / executableQuery / anchorBias 这类内部细节。
    /// </summary>
    private static string BuildStructuredTargetSummary(StructuredTargetReference targetRef, string fallbackText = "")
    {
        // 缺少结构化目标时，直接回退到自然语言目标文本。
        if (targetRef == null)
        {
            return string.IsNullOrWhiteSpace(fallbackText) ? "none" : fallbackText.Trim();
        }

        // 读取给人和 LLM 看得懂的轻量字段。
        string display = ResolveStructuredTargetDisplayText(targetRef, fallbackText);
        string rawText = string.IsNullOrWhiteSpace(targetRef.rawText) ? "none" : targetRef.rawText.Trim();
        string selector = string.IsNullOrWhiteSpace(targetRef.selectorText) ? "none" : targetRef.selectorText.Trim();
        string relation = string.IsNullOrWhiteSpace(targetRef.relation) ? "none" : targetRef.relation.Trim();
        // 当前主路径只输出 mode/card/display/raw/selector/relation。
        return $"mode={targetRef.mode},card={targetRef.cardinality},display={display},raw={rawText},selector={selector},relation={relation}";
    }

    /// <summary>
    /// 统一构造一个最保守的结构化目标引用。
    /// 当上游现在还只给了字符串时，就先把它包成一个正式对象，避免后面的模块继续裸奔。
    /// </summary>
    private static StructuredTargetReference BuildTextTargetReference(
        string text,
        StructuredTargetMode mode = StructuredTargetMode.Unknown,
        StructuredTargetCardinality cardinality = StructuredTargetCardinality.One)
    {
        string value = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        return new StructuredTargetReference
        {
            mode = mode,
            cardinality = string.IsNullOrWhiteSpace(value) ? StructuredTargetCardinality.Unspecified : cardinality,
            rawText = value,
            executableQuery = string.Empty,
            entityId = string.Empty,
            entityClass = string.Empty,
            displayName = value,
            selectorText = value,
            collectionKey = string.Empty,
            memberEntityIds = Array.Empty<string>(),
            areaHint = string.Empty,
            relation = string.Empty,
            anchorText = string.Empty,
            anchorBiasX = 0,
            anchorBiasZ = 0,
            isDynamic = false,
            notes = "legacy_string_wrapped"
        };
    }

    /// <summary>
    /// 对单个结构化目标做最小合法化。
    /// 规则很简单：
    /// 1) 有结构化对象就尽量保留；
    /// 2) 关键字段空了就用 fallbackText 补；
    /// 3) 无论上游是新字段还是旧字符串，最后都保证这里能拿到一个稳定目标对象。
    /// </summary>
    private static StructuredTargetReference NormalizeStructuredTargetReference(
        StructuredTargetReference src,
        string fallbackText,
        StructuredTargetMode fallbackMode = StructuredTargetMode.Unknown,
        StructuredTargetCardinality fallbackCardinality = StructuredTargetCardinality.One)
    {
        if (src == null)
        {
            return BuildTextTargetReference(fallbackText, fallbackMode, fallbackCardinality);
        }

        StructuredTargetReference result = new StructuredTargetReference
        {
            mode = src.mode == StructuredTargetMode.Unknown && !string.IsNullOrWhiteSpace(fallbackText) ? fallbackMode : src.mode,
            cardinality = src.cardinality == StructuredTargetCardinality.Unspecified && !string.IsNullOrWhiteSpace(fallbackText)
                ? fallbackCardinality
                : src.cardinality,
            rawText = string.IsNullOrWhiteSpace(src.rawText) ? fallbackText : src.rawText,
            executableQuery = src.executableQuery,
            entityId = src.entityId,
            entityClass = src.entityClass,
            displayName = src.displayName,
            selectorText = src.selectorText,
            collectionKey = src.collectionKey,
            memberEntityIds = src.memberEntityIds != null ? (string[])src.memberEntityIds.Clone() : Array.Empty<string>(),
            areaHint = src.areaHint,
            relation = src.relation,
            anchorText = src.anchorText,
            anchorBiasX = src.anchorBiasX,
            anchorBiasZ = src.anchorBiasZ,
            isDynamic = src.isDynamic,
            notes = string.IsNullOrWhiteSpace(src.notes) ? "normalized" : src.notes.Trim()
        };

        result.rawText = string.IsNullOrWhiteSpace(result.rawText) ? string.Empty : result.rawText.Trim();
        result.entityId = string.IsNullOrWhiteSpace(result.entityId) ? string.Empty : result.entityId.Trim();
        result.entityClass = string.IsNullOrWhiteSpace(result.entityClass) ? string.Empty : result.entityClass.Trim();
        result.displayName = string.IsNullOrWhiteSpace(result.displayName) ? string.Empty : result.displayName.Trim();
        result.selectorText = string.IsNullOrWhiteSpace(result.selectorText) ? string.Empty : result.selectorText.Trim();
        result.collectionKey = string.IsNullOrWhiteSpace(result.collectionKey) ? string.Empty : result.collectionKey.Trim();
        result.memberEntityIds = result.memberEntityIds == null
            ? Array.Empty<string>()
            : result.memberEntityIds
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        result.areaHint = string.IsNullOrWhiteSpace(result.areaHint) ? string.Empty : result.areaHint.Trim();
        result.relation = string.IsNullOrWhiteSpace(result.relation) ? string.Empty : result.relation.Trim();
        result.executableQuery = string.IsNullOrWhiteSpace(result.executableQuery) ? string.Empty : result.executableQuery.Trim();
        result.anchorText = string.IsNullOrWhiteSpace(result.anchorText) ? string.Empty : result.anchorText.Trim();
        result.anchorBiasX = Mathf.Clamp(result.anchorBiasX, -1, 1);
        result.anchorBiasZ = Mathf.Clamp(result.anchorBiasZ, -1, 1);

        // 这里不再把 natural text 直接写回 executableQuery。
        // 原因很关键：
        // 1) executableQuery 表示“系统已经准备好交给执行层的 grounded 锚点”；
        // 2) 以前一归一化就把 rawText 塞进去，会让未落地目标看起来像“已经解析完成”；
        // 3) 现在只有确实已有 entityId / anchorText 时，才把 executableQuery 补齐。
        if (string.IsNullOrWhiteSpace(result.executableQuery) && !string.IsNullOrWhiteSpace(result.entityId))
        {
            result.executableQuery = result.entityId;
        }

        if (string.IsNullOrWhiteSpace(result.displayName))
        {
            result.displayName = !string.IsNullOrWhiteSpace(result.rawText)
                ? result.rawText
                : (!string.IsNullOrWhiteSpace(result.selectorText)
                    ? result.selectorText
                    : (!string.IsNullOrWhiteSpace(result.entityId) ? result.entityId : fallbackText));
        }

        if ((result.mode == StructuredTargetMode.Collection || result.mode == StructuredTargetMode.DynamicSelector) &&
            string.IsNullOrWhiteSpace(result.selectorText))
        {
            result.selectorText = !string.IsNullOrWhiteSpace(result.rawText)
                ? result.rawText
                : result.displayName;
        }

        if (string.IsNullOrWhiteSpace(result.anchorText) && !string.IsNullOrWhiteSpace(result.executableQuery))
        {
            result.anchorText = result.executableQuery;
        }

        return result;
    }

    /// <summary>
    /// 对结构化经过点数组做统一补齐。
    /// 这样后面不论是新字段 viaTargetRefs，还是旧字段 viaTargets，都能最终收敛到同一份结构。
    /// </summary>
    private static StructuredTargetReference[] NormalizeStructuredTargetReferenceArray(
        StructuredTargetReference[] refs,
        string[] fallbackTexts,
        StructuredTargetMode fallbackMode = StructuredTargetMode.Unknown)
    {
        List<StructuredTargetReference> result = new List<StructuredTargetReference>();

        if (refs != null)
        {
            for (int i = 0; i < refs.Length; i++)
            {
                StructuredTargetReference normalized = NormalizeStructuredTargetReference(refs[i], string.Empty, fallbackMode);
                if (HasUsableStructuredTarget(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        if (result.Count == 0 && fallbackTexts != null)
        {
            for (int i = 0; i < fallbackTexts.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(fallbackTexts[i])) continue;
                result.Add(BuildTextTargetReference(fallbackTexts[i], fallbackMode, StructuredTargetCardinality.One));
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// 从结构化目标引用里拿“当前执行层应该优先解析哪个字符串”。
    /// 顺序是：
    /// executableQuery -> entityId -> anchorText -> displayName -> rawText -> selectorText -> areaHint -> fallbackText。
    /// 也就是说，语义仍然完整保存在目标对象里，但执行层优先拿最像“锚点”的字段工作。
    /// </summary>
    private static string ResolveStructuredTargetQuery(StructuredTargetReference targetRef, string fallbackText = "")
    {
        if (targetRef == null)
        {
            return string.IsNullOrWhiteSpace(fallbackText) ? string.Empty : fallbackText.Trim();
        }

        string[] candidates =
        {
            targetRef.executableQuery,
            targetRef.anchorText,
            targetRef.memberEntityIds != null && targetRef.memberEntityIds.Length > 0 ? targetRef.memberEntityIds[0] : string.Empty,
            targetRef.entityId,
            targetRef.displayName,
            targetRef.rawText,
            targetRef.selectorText,
            targetRef.areaHint,
            fallbackText
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(candidates[i]))
            {
                return candidates[i].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 从结构化经过点引用数组中提取当前还能兼容旧执行链的字符串列表。
    /// 这样做的目的是先完成“目标是一等公民”的迁移，再逐步削弱旧字符串字段。
    /// </summary>
    private static string[] ResolveStructuredTargetQueries(StructuredTargetReference[] refs, string[] fallbackTexts = null)
    {
        List<string> values = new List<string>();
        if (refs != null)
        {
            for (int i = 0; i < refs.Length; i++)
            {
                string query = ResolveStructuredTargetQuery(refs[i]);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    values.Add(query);
                }
            }
        }

        if (values.Count == 0 && fallbackTexts != null)
        {
            values.AddRange(fallbackTexts.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
        }

        return values.ToArray();
    }
    /// <summary>
    /// 将当前智能体的角色偏好和能力摘要发送给协调者。
    /// 协调者后续会用这份数据给每个 taskSlot 计算适配分数。
    /// </summary>
    /// <param name="mission">当前任务对象，提供 missionId 和 coordinatorId。</param>
    /// <param name="preferences">当前智能体偏好的角色顺序，通常由 LLM 先行分析得到。</param>
    private void SendRolePreferenceToCoordinator(MissionAssignment mission, RoleType[] preferences)
    {
        // 没有通信模块或任务对象时，无法把偏好发给协调者，直接退出。
        if (commModule == null || mission == null) return;

        // 组装结构化偏好载荷，保证协调者能统一读取 missionId、角色偏好和能力指标。
        RolePreferencePayload payload = new RolePreferencePayload
        {
            // 任务 ID 用来隔离不同轮次任务的角色偏好。
            missionId = mission.missionId,
            // 当前上报偏好的智能体 ID。
            agentId = agentProperties.AgentID,
            // 当前智能体给出的角色偏好顺序。
            preferences = preferences,
            // 当前智能体的平台类型，用于和槽位 requiredAgentType 匹配。
            agentType = agentProperties.Type,
            // 当前已经具备的角色，可供协调者做兼容裁决。
            currentRole = agentProperties.Role,
            // 最大速度用于衡量机动能力。
            maxSpeed = agentProperties.MaxSpeed,
            // 感知范围用于衡量侦查能力。
            perceptionRange = agentProperties.PerceptionRange,
            // 额外保存一份紧凑字符串摘要，便于日志排查。
            capabilitySummary = $"{agentProperties.Role}-{agentProperties.Type}-speed:{agentProperties.MaxSpeed:F1}-sense:{agentProperties.PerceptionRange:F1}"
        };

        // 将偏好消息发给协调者，消息类型明确标记为 RolePreference。
        commModule.SendStructuredMessage(mission.coordinatorId, MessageType.RolePreference, payload, 1);
    }

    /// <summary>
    /// 尝试把第一阶段 LLM 返回解析成团队语义骨架。
    /// 新主路径只要求 LLM 输出 MissionSemantic，不再直接手写 taskSlots。
    /// </summary>
    private bool TryParseMissionSemanticResponse(string response, out MissionSemanticResponse semantic, out string error)
    {
        semantic = null;
        error = string.Empty;

        string jsonContent = ExtractPureJson(response);
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            error = "团队语义响应为空";
            return false;
        }

        try
        {
            if (!jsonContent.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                error = "团队语义不是 JSON 对象";
                return false;
            }

            JObject root = JObject.Parse(jsonContent);
            JObject obj = root["result"] as JObject ?? root;
            semantic = new MissionSemanticResponse
            {
                missionType = ReadStringField(obj, "missionType", "type"),
                relationshipType = ReadStringField(obj, "relationshipType", "relationType"),
                teamObjective = ReadStringField(obj, "teamObjective", "objective", "teamGoal"),
                successCondition = ReadStringField(obj, "successCondition", "doneWhen"),
                failureCondition = ReadStringField(obj, "failureCondition", "failWhen"),
                roleRequirements = ReadMissionSemanticRoleRequirements(obj, "roleRequirements", "roles", "groups"),
                phaseTemplates = ReadMissionSemanticPhaseTemplates(obj, "phaseTemplates", "phases"),
                coordinationRules = ReadMissionSemanticCoordinationRules(obj, "coordinationRules", "coordination", "rules")
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static MissionSemanticRoleRequirement[] ReadMissionSemanticRoleRequirements(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return Array.Empty<MissionSemanticRoleRequirement>();

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!(obj[key] is JArray arr) || arr.Count == 0) continue;

            List<MissionSemanticRoleRequirement> result = new List<MissionSemanticRoleRequirement>();
            for (int j = 0; j < arr.Count; j++)
            {
                if (!(arr[j] is JObject roleObj)) continue;

                int count = 0;
                JToken countToken = roleObj["count"] ?? roleObj["requiredCount"] ?? roleObj["agentCount"];
                if (countToken != null) int.TryParse(countToken.ToString(), out count);

                result.Add(new MissionSemanticRoleRequirement
                {
                    role = ReadStringField(roleObj, "role", "roleType"),
                    count = count,
                    responsibility = ReadStringField(roleObj, "responsibility", "objective", "notes"),
                    targetText = ReadStringField(roleObj, "targetText", "target", "sharedTarget"),
                    targetKindHint = ReadStringField(roleObj, "targetKindHint", "targetKind"),
                    viaTargets = ReadStringArrayField(roleObj, "viaTargets", "checkpoints"),
                    completionCondition = ReadStringField(roleObj, "completionCondition", "doneWhen"),
                    phaseIds = ReadStringArrayField(roleObj, "phaseIds", "phases")
                });
            }

            if (result.Count > 0) return result.ToArray();
        }

        return Array.Empty<MissionSemanticRoleRequirement>();
    }

    private static MissionSemanticPhaseTemplate[] ReadMissionSemanticPhaseTemplates(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return Array.Empty<MissionSemanticPhaseTemplate>();

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!(obj[key] is JArray arr) || arr.Count == 0) continue;

            List<MissionSemanticPhaseTemplate> result = new List<MissionSemanticPhaseTemplate>();
            for (int j = 0; j < arr.Count; j++)
            {
                if (!(arr[j] is JObject phaseObj)) continue;
                result.Add(new MissionSemanticPhaseTemplate
                {
                    phaseId = ReadStringField(phaseObj, "phaseId", "id"),
                    objective = ReadStringField(phaseObj, "objective", "goal"),
                    dependsOn = ReadStringArrayField(phaseObj, "dependsOn", "dependsOnPhaseIds")
                });
            }

            if (result.Count > 0) return result.ToArray();
        }

        return Array.Empty<MissionSemanticPhaseTemplate>();
    }

    private static MissionSemanticCoordinationRule[] ReadMissionSemanticCoordinationRules(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return Array.Empty<MissionSemanticCoordinationRule>();

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!(obj[key] is JArray arr) || arr.Count == 0) continue;

            List<MissionSemanticCoordinationRule> result = new List<MissionSemanticCoordinationRule>();
            for (int j = 0; j < arr.Count; j++)
            {
                if (!(arr[j] is JObject ruleObj)) continue;
                result.Add(new MissionSemanticCoordinationRule
                {
                    ruleType = ReadStringField(ruleObj, "ruleType", "type"),
                    trigger = ReadStringField(ruleObj, "trigger", "event"),
                    effect = ReadStringField(ruleObj, "effect", "action"),
                    sharedTarget = ReadStringField(ruleObj, "sharedTarget", "target"),
                    participants = ReadStringArrayField(ruleObj, "participants", "roles"),
                    notes = ReadStringField(ruleObj, "notes", "note")
                });
            }

            if (result.Count > 0) return result.ToArray();
        }

        return Array.Empty<MissionSemanticCoordinationRule>();
    }

    private MissionSemanticResponse NormalizeMissionSemanticResponse(MissionSemanticResponse src, string description, int agentCount)
    {
        MissionSemanticResponse semantic = src ?? new MissionSemanticResponse();
        semantic.missionType = string.IsNullOrWhiteSpace(semantic.missionType) ? MissionType.Unknown.ToString() : semantic.missionType.Trim();
        semantic.relationshipType = string.IsNullOrWhiteSpace(semantic.relationshipType) ? TeamRelationshipType.Cooperation.ToString() : semantic.relationshipType.Trim();
        semantic.teamObjective = string.IsNullOrWhiteSpace(semantic.teamObjective) ? description : semantic.teamObjective.Trim();
        semantic.successCondition = string.IsNullOrWhiteSpace(semantic.successCondition) ? "完成团队目标" : semantic.successCondition.Trim();
        semantic.failureCondition = string.IsNullOrWhiteSpace(semantic.failureCondition) ? "未完成团队目标或超时" : semantic.failureCondition.Trim();

        MissionSemanticRoleRequirement[] rawRoles = semantic.roleRequirements ?? Array.Empty<MissionSemanticRoleRequirement>();
        if (rawRoles.Length == 0)
        {
            rawRoles = new[]
            {
                new MissionSemanticRoleRequirement
                {
                    role = RoleType.Supporter.ToString(),
                    count = Mathf.Max(1, agentCount),
                    responsibility = "执行任务分配并与队友协同推进",
                    targetText = description,
                    targetKindHint = StructuredTargetMode.Unknown.ToString(),
                    viaTargets = Array.Empty<string>(),
                    completionCondition = "完成团队目标",
                    phaseIds = Array.Empty<string>()
                }
            };
        }

        List<MissionSemanticRoleRequirement> normalizedRoles = new List<MissionSemanticRoleRequirement>();
        for (int i = 0; i < rawRoles.Length; i++)
        {
            MissionSemanticRoleRequirement role = rawRoles[i] ?? new MissionSemanticRoleRequirement();
            normalizedRoles.Add(new MissionSemanticRoleRequirement
            {
                role = string.IsNullOrWhiteSpace(role.role) ? RoleType.Supporter.ToString() : role.role.Trim(),
                count = Mathf.Max(1, role.count),
                responsibility = string.IsNullOrWhiteSpace(role.responsibility) ? "执行任务分配" : role.responsibility.Trim(),
                targetText = string.IsNullOrWhiteSpace(role.targetText) ? description : role.targetText.Trim(),
                targetKindHint = string.IsNullOrWhiteSpace(role.targetKindHint) ? StructuredTargetMode.Unknown.ToString() : role.targetKindHint.Trim(),
                viaTargets = (role.viaTargets ?? Array.Empty<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                completionCondition = string.IsNullOrWhiteSpace(role.completionCondition) ? "完成岗位职责" : role.completionCondition.Trim(),
                phaseIds = (role.phaseIds ?? Array.Empty<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            });
        }

        int capacity = Mathf.Max(1, agentCount);
        int total = normalizedRoles.Sum(r => Mathf.Max(1, r.count));
        if (total < capacity)
        {
            normalizedRoles[normalizedRoles.Count - 1].count += capacity - total;
        }
        else if (total > capacity)
        {
            int overflow = total - capacity;
            for (int i = normalizedRoles.Count - 1; i >= 0 && overflow > 0; i--)
            {
                int reducible = Mathf.Max(0, normalizedRoles[i].count - 1);
                int take = Mathf.Min(reducible, overflow);
                normalizedRoles[i].count -= take;
                overflow -= take;
            }
        }

        semantic.roleRequirements = normalizedRoles.ToArray();

        MissionSemanticPhaseTemplate[] phases = semantic.phaseTemplates ?? Array.Empty<MissionSemanticPhaseTemplate>();
        List<MissionSemanticPhaseTemplate> normalizedPhases = new List<MissionSemanticPhaseTemplate>();
        HashSet<string> usedPhaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < phases.Length; i++)
        {
            MissionSemanticPhaseTemplate phase = phases[i] ?? new MissionSemanticPhaseTemplate();
            string baseId = string.IsNullOrWhiteSpace(phase.phaseId) ? $"phase_{i + 1}" : phase.phaseId.Trim();
            string uniqueId = baseId;
            int suffix = 2;
            while (usedPhaseIds.Contains(uniqueId))
            {
                uniqueId = $"{baseId}_{suffix}";
                suffix++;
            }

            usedPhaseIds.Add(uniqueId);
            normalizedPhases.Add(new MissionSemanticPhaseTemplate
            {
                phaseId = uniqueId,
                objective = string.IsNullOrWhiteSpace(phase.objective) ? $"完成阶段 {uniqueId}" : phase.objective.Trim(),
                dependsOn = (phase.dependsOn ?? Array.Empty<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            });
        }

        semantic.phaseTemplates = normalizedPhases.ToArray();
        semantic.coordinationRules = semantic.coordinationRules ?? Array.Empty<MissionSemanticCoordinationRule>();
        return semantic;
    }

    private TeamRelationshipType ExtractRelationshipType(MissionSemanticResponse semantic)
    {
        if (semantic != null && Enum.TryParse(semantic.relationshipType, true, out TeamRelationshipType relationshipType))
        {
            return relationshipType;
        }

        return TeamRelationshipType.Cooperation;
    }

    private MissionAssignment BuildMissionFromSemantic(MissionSemanticResponse semantic, string description, int agentCount)
    {
        MissionSemanticResponse normalized = NormalizeMissionSemanticResponse(semantic, description, agentCount);
        MissionType missionType = Enum.TryParse(normalized.missionType, true, out MissionType parsedMissionType)
            ? parsedMissionType
            : MissionType.Unknown;
        TeamRelationshipType relationshipType = ExtractRelationshipType(normalized);
        MissionRole[] roles = BuildRolesFromSemantic(normalized, agentCount);
        MissionTaskSlot[] taskSlots = BuildTaskSlotsFromSemantic(description, normalized, roles, missionType, agentCount);
        TeamCoordinationDirective[] directives = BuildCoordinationDirectivesFromSemantic(normalized, missionType);

        MissionAssignment mission = new MissionAssignment
        {
            missionId = $"mission_{DateTime.Now:yyyyMMdd_HHmmss}",
            missionDescription = description,
            missionSource = string.Empty,
            sourceGoalId = string.Empty,
            sourceEventId = string.Empty,
            missionType = missionType,
            relationshipType = relationshipType,
            coordinatorId = agentProperties.AgentID,
            roles = roles,
            communicationMode = CommunicationMode.Hybrid,
            requiredAgentCount = Mathf.Max(1, agentCount),
            teamObjective = normalized.teamObjective,
            successCondition = normalized.successCondition,
            failureCondition = normalized.failureCondition,
            phaseTemplates = normalized.phaseTemplates != null
                ? normalized.phaseTemplates.Select((phase, index) => new MissionPhaseDefinition
                {
                    phaseId = !string.IsNullOrWhiteSpace(phase?.phaseId) ? phase.phaseId : $"phase_{index + 1}",
                    phaseLabel = !string.IsNullOrWhiteSpace(phase?.phaseId) ? phase.phaseId : $"phase_{index + 1}",
                    objective = !string.IsNullOrWhiteSpace(phase?.objective) ? phase.objective : $"完成阶段 {index + 1}",
                    agentBudget = Mathf.Max(1, agentCount),
                    roleFocus = roles.Select(r => r.roleType.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    dependsOnPhaseIds = phase != null ? (phase.dependsOn ?? Array.Empty<string>()) : Array.Empty<string>(),
                    syncGroup = !string.IsNullOrWhiteSpace(phase?.phaseId) ? phase.phaseId : $"phase_{index + 1}",
                    completionCriteria = !string.IsNullOrWhiteSpace(phase?.objective) ? phase.objective : "完成阶段目标",
                    notes = "semantic_phase_template"
                }).ToArray()
                : Array.Empty<MissionPhaseDefinition>(),
            coordinationDirectives = directives,
            taskSlots = taskSlots
        };

        if (mission.taskSlots != null)
        {
            for (int i = 0; i < mission.taskSlots.Length; i++)
            {
                GroundMissionTaskSlotInPlace(mission.taskSlots[i], description);
            }
            RepairCollectionCoverageSlots(mission.taskSlots.Where(s => s != null).ToList());
        }

        GroundCoordinationDirectivesInPlace(mission.coordinationDirectives, mission.teamObjective);
        return mission;
    }

    private MissionRole[] BuildRolesFromSemantic(MissionSemanticResponse semantic, int agentCount)
    {
        List<MissionRole> roles = new List<MissionRole>();
        MissionSemanticRoleRequirement[] requirements = semantic != null ? semantic.roleRequirements : null;
        if (requirements == null || requirements.Length == 0)
        {
            return new[]
            {
                CreateRole(RoleType.Supporter, agentProperties != null ? agentProperties.Type : AgentType.Quadcopter, Mathf.Max(1, agentCount),
                    new[] { "执行任务分配", "与队友协同推进" })
            };
        }

        for (int i = 0; i < requirements.Length; i++)
        {
            MissionSemanticRoleRequirement requirement = requirements[i];
            RoleType roleType = ParseEnumOrDefault(requirement.role, RoleType.Supporter);
            string[] responsibilities = new[]
            {
                !string.IsNullOrWhiteSpace(requirement.responsibility) ? requirement.responsibility : "执行任务分配"
            };

            MissionRole role = CreateRole(
                roleType,
                agentProperties != null ? agentProperties.Type : AgentType.Quadcopter,
                Mathf.Max(1, requirement.count),
                responsibilities);
            role.preferredTargets = string.IsNullOrWhiteSpace(requirement.targetText)
                ? Array.Empty<string>()
                : new[] { requirement.targetText.Trim() };
            role.coordinationResponsibilities = requirement.phaseIds ?? Array.Empty<string>();
            roles.Add(role);
        }

        return roles.ToArray();
    }

    private MissionTaskSlot[] BuildTaskSlotsFromSemantic(
        string missionDescription,
        MissionSemanticResponse semantic,
        MissionRole[] roles,
        MissionType missionType,
        int agentCount)
    {
        List<MissionTaskSlot> slots = new List<MissionTaskSlot>();
        MissionSemanticRoleRequirement[] requirements = semantic != null ? semantic.roleRequirements : null;
        if (requirements == null || requirements.Length == 0)
        {
            return BuildTaskSlotsForMission(missionDescription, roles, missionType, agentCount);
        }

        Dictionary<string, string[]> phaseDependencies = (semantic.phaseTemplates ?? Array.Empty<MissionSemanticPhaseTemplate>())
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.phaseId))
            .ToDictionary(
                p => p.phaseId.Trim(),
                p => (p.dependsOn ?? Array.Empty<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<string>> phaseSlotIds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < requirements.Length; i++)
        {
            MissionSemanticRoleRequirement requirement = requirements[i];
            RoleType roleType = ParseEnumOrDefault(requirement.role, RoleType.Supporter);
            string[] phaseIds = requirement.phaseIds != null && requirement.phaseIds.Length > 0
                ? requirement.phaseIds
                : ((semantic.phaseTemplates != null && semantic.phaseTemplates.Length > 0)
                    ? new[] { semantic.phaseTemplates[0].phaseId }
                    : new[] { "phase_1" });

            for (int j = 0; j < Mathf.Max(1, requirement.count); j++)
            {
                string slotId = $"slot_{roleType}_{j + 1}_{i + 1}";
                string viaLabel = requirement.viaTargets != null && requirement.viaTargets.Length > 0 && !string.IsNullOrWhiteSpace(requirement.viaTargets[0])
                    ? SanitizeSlotLabelFragment(requirement.viaTargets[0])
                    : string.Empty;
                string slotLabel = !string.IsNullOrWhiteSpace(viaLabel)
                    ? $"{roleType}_{i + 1}_{j + 1}_{viaLabel}"
                    : $"{roleType}_{i + 1}_{j + 1}";
                string syncGroup = phaseIds.Length > 0 && !string.IsNullOrWhiteSpace(phaseIds[0]) ? phaseIds[0].Trim() : "phase_1";
                if (!phaseSlotIds.TryGetValue(syncGroup, out List<string> ids))
                {
                    ids = new List<string>();
                    phaseSlotIds[syncGroup] = ids;
                }
                ids.Add(slotId);

                slots.Add(new MissionTaskSlot
                {
                    slotId = slotId,
                    slotLabel = slotLabel,
                    roleType = roleType,
                    requiredAgentType = agentProperties != null ? agentProperties.Type : AgentType.Quadcopter,
                    target = string.IsNullOrWhiteSpace(requirement.targetText) ? missionDescription : requirement.targetText.Trim(),
                    targetRef = BuildSemanticTargetReference(requirement.targetText, requirement.targetKindHint, j, requirement.count),
                    viaTargets = requirement.viaTargets ?? Array.Empty<string>(),
                    viaTargetRefs = NormalizeStructuredTargetReferenceArray(null, requirement.viaTargets ?? Array.Empty<string>()),
                    altitudeMode = RouteAltitudeMode.Default,
                    syncGroup = syncGroup,
                    dependsOnSlotIds = Array.Empty<string>(),
                    finalBehavior = "arrive",
                    completionCondition = !string.IsNullOrWhiteSpace(requirement.completionCondition)
                        ? requirement.completionCondition.Trim()
                        : $"完成岗位 {roleType}_{j + 1}",
                    notes = $"semantic_assignment phases={(phaseIds.Length > 0 ? string.Join("|", phaseIds) : "none")}"
                });
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            string syncGroup = slots[i].syncGroup;
            if (string.IsNullOrWhiteSpace(syncGroup) || !phaseDependencies.TryGetValue(syncGroup, out string[] deps) || deps == null || deps.Length == 0)
            {
                continue;
            }

            List<string> dependencySlotIds = new List<string>();
            for (int j = 0; j < deps.Length; j++)
            {
                if (phaseSlotIds.TryGetValue(deps[j], out List<string> depSlots) && depSlots != null && depSlots.Count > 0)
                {
                    dependencySlotIds.Add(depSlots[0]);
                }
            }
            slots[i].dependsOnSlotIds = dependencySlotIds
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        MissionTaskSlot[] executable = EnsureTaskSlotsAreExecutable(slots.ToArray(), missionDescription, roles, missionType, Mathf.Max(1, agentCount));
        return executable.Length > 0 ? executable : BuildTaskSlotsForMission(missionDescription, roles, missionType, agentCount);
    }

    private TeamCoordinationDirective[] BuildCoordinationDirectivesFromSemantic(MissionSemanticResponse semantic, MissionType missionType)
    {
        MissionSemanticCoordinationRule[] rules = semantic != null ? semantic.coordinationRules : null;
        if (rules == null || rules.Length == 0)
        {
            return BuildDefaultMissionCoordinationDirectives(missionType);
        }

        List<TeamCoordinationDirective> directives = new List<TeamCoordinationDirective>();
        for (int i = 0; i < rules.Length; i++)
        {
            MissionSemanticCoordinationRule rule = rules[i];
            if (rule == null) continue;

            TeamCoordinationMode mode = TeamCoordinationMode.Independent;
            string ruleType = !string.IsNullOrWhiteSpace(rule.ruleType) ? rule.ruleType.Trim() : string.Empty;
            if (ruleType.IndexOf("Barrier", StringComparison.OrdinalIgnoreCase) >= 0) mode = TeamCoordinationMode.TightSync;
            else if (ruleType.IndexOf("Leader", StringComparison.OrdinalIgnoreCase) >= 0) mode = TeamCoordinationMode.LeaderFollower;
            else if (ruleType.IndexOf("Reservation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     ruleType.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0) mode = TeamCoordinationMode.CorridorReserve;
            else if (ruleType.IndexOf("Broadcast", StringComparison.OrdinalIgnoreCase) >= 0) mode = TeamCoordinationMode.LooseSync;

            directives.Add(new TeamCoordinationDirective
            {
                coordinationMode = mode,
                leaderAgentId = string.Empty,
                sharedTarget = !string.IsNullOrWhiteSpace(rule.sharedTarget) ? rule.sharedTarget.Trim() : semantic.teamObjective,
                sharedTargetRef = BuildSemanticTargetReference(rule.sharedTarget, StructuredTargetMode.Unknown.ToString(), 0, 1),
                corridorReservationKey = mode == TeamCoordinationMode.CorridorReserve
                    ? (!string.IsNullOrWhiteSpace(rule.trigger) ? rule.trigger.Trim() : $"reservation_{i + 1}")
                    : string.Empty,
                yieldToAgentIds = Array.Empty<string>(),
                syncPointTargets = Array.Empty<string>(),
                formationSlot = string.Empty
            });
        }

        return directives.Count > 0 ? directives.ToArray() : BuildDefaultMissionCoordinationDirectives(missionType);
    }

    private StructuredTargetReference BuildSemanticTargetReference(string targetText, string targetKindHint, int partitionIndex, int partitionCount)
    {
        string normalizedText = string.IsNullOrWhiteSpace(targetText) ? string.Empty : targetText.Trim();
        StructuredTargetMode mode = ParseStructuredTargetModeHint(targetKindHint);
        StructuredTargetCardinality cardinality = StructuredTargetCardinality.One;
        if (mode == StructuredTargetMode.Collection)
        {
            cardinality = partitionCount > 1 ? StructuredTargetCardinality.Subset : StructuredTargetCardinality.All;
        }

        StructuredTargetReference targetRef = NormalizeStructuredTargetReference(
            new StructuredTargetReference
            {
                mode = mode,
                cardinality = cardinality,
                rawText = normalizedText,
                displayName = normalizedText,
                selectorText = mode == StructuredTargetMode.Collection ? normalizedText : string.Empty,
                areaHint = mode == StructuredTargetMode.Area ? normalizedText : string.Empty,
                notes = partitionCount > 1
                    ? $"semantic_partition={partitionIndex + 1}/{partitionCount}"
                    : "semantic_target"
            },
            normalizedText,
            mode,
            cardinality);
        return targetRef;
    }

    private static string SanitizeSlotLabelFragment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        char[] chars = raw.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        string normalized = new string(chars).Trim('_');
        while (normalized.Contains("__"))
        {
            normalized = normalized.Replace("__", "_");
        }

        return normalized;
    }
    /// <summary>
    /// 角色偏好分析阶段。
    /// 这里不生成执行步骤，只让 LLM 判断“当前智能体最适合承担哪些角色”。
    /// </summary>
    /// <param name="mission">当前任务对象，包含任务描述、任务类型和可选角色列表。</param>
    /// <param name="onResult">解析成功后的回调，通常用于把偏好上报给协调者。</param>
    public IEnumerator AnalyzeRolePreference(MissionAssignment mission,Action<RoleType[]> onResult)
    {
        // 构造一个职责很窄的 prompt，只要求 LLM 输出角色偏好顺序。
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

            // 把任务允许的角色逐个拼到 prompt 中，让 LLM 只能在这些候选里排序。
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

        // 把 prompt 发给 LLM；回调里只做 JSON 提取、解析和结果回传。
        yield return llmInterface.SendRequest(prompt, (result) =>
        {
            // 先打印原始返回，方便调试某次角色偏好异常的原因。
            Debug.Log($"=== 智能体 {agentProperties.AgentID} 收到 LLM 角色偏好原始响应 ===");
            Debug.Log($"原始响应内容: {result}");
            try
            {
                // 先抽出纯 JSON 内容，去掉可能包着的 Markdown 或解释文字。
                string json = ExtractPureJson(result);
                // 再把 JSON 反序列化成 RolePreferenceWrapper。
                var pref = JsonConvert.DeserializeObject<RolePreferenceWrapper>(json);
                // 解析对象存在时，再进一步检查 preferences 字段是否有效。
                if (pref != null)
                {
                    if (pref.preferences != null)
                    {
                        // 正常路径：把角色偏好透传给回调。
                        onResult?.Invoke(pref.preferences);
                    }
                    else
                    {
                        // JSON 对象存在但偏好数组为空时，退回到当前角色。
                        Debug.LogWarning("preferences 数组为 null");
                        onResult?.Invoke(new RoleType[] { agentProperties.Role });
                    }
                }
                else
                {
                    // 反序列化失败时同样回退到当前角色，避免阻塞整条分配链路。
                    Debug.LogWarning("解析结果为空或无效，使用默认角色");
                    onResult?.Invoke(new RoleType[] { agentProperties.Role });
                }
            }
            catch (Exception e)
            {
                // 任意解析异常都不应该中断分配链路，因此直接使用当前角色做兜底。
                Debug.LogError($"角色偏好解析失败: {e.Message}");
                onResult?.Invoke(new RoleType[] { agentProperties.Role });
            }
        }, temperature: 0.3f, maxTokens: 120);
    }



    /// <summary>
    /// 为已经明确分到角色和槽位的当前智能体生成个人执行计划。
    /// </summary>
    /// <param name="mission">团队级任务对象，提供 missionDescription、teamObjective 和协同规则。</param>
    /// <param name="specificRole">当前智能体被协调者分配的角色。</param>
    /// <param name="specificSlot">当前智能体被协调者分配的槽位。</param>
    public IEnumerator AnalyzeMissionAndCreatePlan(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)
    {
        // 进入个人计划生成时重新置位 busy，防止并发触发新的规划入口。
        planningRequestInFlight = true;
        try
        {
            // 这些枚举字符串会直接写进 prompt，强约束 LLM 只能输出系统支持的 token。
            string roleTypeOptions = string.Join("|", Enum.GetNames(typeof(RoleType)));
            string coordinationModeOptions = string.Join("|", Enum.GetNames(typeof(TeamCoordinationMode)));
            string targetKindHintOptions = string.Join("|", Enum.GetNames(typeof(StructuredTargetMode)));
            // 将分配到的槽位压成一行摘要，让 LLM 明确“我现在负责什么岗位、目标和约束”。
            string slotSummary = specificSlot != null
                ? $"assignmentId={specificSlot.slotId},label={specificSlot.slotLabel},role={specificSlot.roleType},targetText={specificSlot.targetText},targetRef={BuildStructuredTargetSummary(specificSlot.targetRef, specificSlot.targetText)},via={(specificSlot.viaTargets != null && specificSlot.viaTargets.Length > 0 ? string.Join("|", specificSlot.viaTargets) : "none")},syncGroup={specificSlot.syncGroup},done={specificSlot.localCompletionCondition}"
                : "none";
            // 如果这是“覆盖某个集合子集”的槽位，这里会把成员摘要也喂给 LLM。
            string slotMemberSummary = BuildSlotMemberSummary(specificSlot);
            // 把任务级协同规则压成紧凑文本，提醒 LLM 计划时保留同步/编队/让行等约束。
            string missionCoordinationSummary = mission != null && mission.coordinationDirectives != null && mission.coordinationDirectives.Length > 0
                ? string.Join(" || ", mission.coordinationDirectives.Where(d => d != null).Select(d => $"mode={d.coordinationMode},leader={d.leaderAgentId},shared={d.sharedTarget},sharedRef={BuildStructuredTargetSummary(d.sharedTargetRef, d.sharedTarget)},corridor={d.corridorReservationKey},formation={d.formationSlot}").ToArray())
                : "none";
            // 再拼接目标来源、历史经验和反思建议，构成个人计划阶段的认知上下文。
            string cognitiveContext = BuildPlanningCognitiveContext(
                mission != null ? mission.missionDescription : string.Empty,
                mission != null ? mission.missionId : string.Empty,
                specificSlot,
                specificRole,
                mission != null ? mission.teamObjective : string.Empty);

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
        - assignedSlotMembers: {slotMemberSummary}
        - missionCoordination: {missionCoordinationSummary}

        {cognitiveContext}

        [硬性规则]
        1) 只能输出一个JSON对象，不要Markdown，不要解释文字。
        2) assignedRole 只能取: {roleTypeOptions}。
        3) planSteps 数量通常 2-6 条；若 assignedSlotMembers 非空，可放宽到 2-12 条。
        4) 每个 planStep 必须包含: stepId、text、targetText、targetKindHint、relationHint、viaTargets、completionCondition。
        5) targetKindHint 只能取: {targetKindHintOptions}。
        6) targetText 写本步自然语言目标；中间检查点只写进 viaTargets。
        7) relationHint 只写模糊关系，例如 南侧、附近、外围、东半区；没有则写空字符串。
        8) targetText / viaTargets / coordinationDirectives 应优先复用 assignedSlot 和 missionCoordination 中已有目标，不要发明新的地点名。
        9) 若信息不足，仍要给出可执行默认步骤，不能返回空数组。
        10) coordinationDirectives 返回 0-2 条，仅保留与当前智能体相关的协同要求，coordinationMode 只能取: {coordinationModeOptions}。
        11) 不要输出 stepActionTypes、stepNavigationModes、stepIntents、stepRoutePolicies 这类细粒度动作字段。

        [输出模板]
        {{
        ""assignedRole"": ""Scout"",
        ""planSteps"": [
            {{
                ""stepId"": ""step_1"",
                ""text"": ""前往观察位"",
                ""targetText"": ""1号教学楼"",
                ""targetKindHint"": ""Entity"",
                ""relationHint"": ""南侧"",
                ""viaTargets"": [""南门观察位""],
                ""completionCondition"": ""到达观察位并准备执行下一步""
            }}
        ],
        ""coordinationDirectives"": [
            {{
                ""coordinationMode"": ""{(Enum.GetNames(typeof(TeamCoordinationMode)).Length > 0 ? Enum.GetNames(typeof(TeamCoordinationMode))[0] : "Independent")}"",
                ""leaderAgentId"": """",
                ""sharedTarget"": ""shared_target"",
                ""sharedTargetRef"": {{
                    ""mode"": ""Entity"",
                    ""cardinality"": ""One"",
                    ""rawText"": ""shared_target"",
                    ""displayName"": ""shared_target"",
                    ""selectorText"": """",
                    ""relation"": """",
                    ""notes"": ""共享目标语义，具体 grounding 由系统完成""
                }},
                ""corridorReservationKey"": """",
                ""yieldToAgentIds"": [],
                ""syncPointTargets"": [],
                ""formationSlot"": """"
            }}
        ],
        ""missionNavigationPolicy"": ""Auto""
        }}";

            // 把个人计划 prompt 发给 LLM；这里的回调负责把 JSON 真正落到 currentPlan。
            yield return llmInterface.SendRequest(prompt, (result) =>
            {
                try
                {
                    // 正常路径：解析 LLM 输出并创建 currentPlan。
                    ParseAndCreatePlan(result, mission, specificRole, specificSlot);
                }
                catch (Exception e)
                {
                    // 计划解析失败时退回默认计划，确保执行链路不会中断。
                    Debug.LogError($"任务分析失败: {e.Message}");
                    CreateDefaultPlan(mission, specificRole, specificSlot);
                    if (currentPlan != null)
                    {
                        // 默认计划创建成功后，仍然向协调者发送接受回执。
                        SendRoleAcceptance(currentPlan.agentRole.ToString(), "fallback_default_plan");
                    }
                }
            },
            // 个人计划输出至少要覆盖 2~6 个 planSteps 和少量协同字段。
            // 之前 220 token 在 deepseek 上很容易把 JSON 截断在 coordinationDirectives 中间，
            // 导致前面其实已经生成好的 planSteps 因整体 JSON 不闭合而解析失败。
            temperature: 0.2f,
            maxTokens: 520);
        }
        finally
        {
            // 无论成功还是失败，协程结束时都要清掉 planning busy 标记。
            planningRequestInFlight = false;
        }
    }

    /// <summary>
    /// 把个人计划阶段返回的 JSON 真正解析成 currentPlan。
    /// </summary>
    /// <param name="llmResponse">个人计划阶段的原始 LLM 输出。</param>
    /// <param name="mission">团队级任务对象。</param>
    /// <param name="specificRole">协调者为当前智能体指定的角色。</param>
    /// <param name="specificSlot">协调者为当前智能体指定的任务槽位。</param>
    private void ParseAndCreatePlan(string llmResponse, MissionAssignment mission, RoleType? specificRole, MissionTaskSlot specificSlot)
    {
        // 优先使用协调者已经明确分配好的角色；只有缺失时才从 LLM 输出中兜底提取。
        RoleType assignedRole = specificRole ?? ExtractRoleTypeFromResponse(llmResponse);
        // 记录这次接受计划的原因，后面会写入记忆并上报协调者。
        string reasoning = "LLM分析分配";
        // 个人计划阶段只接受新的 planSteps 主输出，不再回退旧动作数组。
        PlanStepDefinition[] planSteps = ExtractPlanStepsFromResponse(llmResponse, specificSlot);
        if (planSteps == null || planSteps.Length == 0)
        {
            throw new InvalidOperationException("个人计划缺少 planSteps");
        }

        string[] steps = planSteps
            .Select((step, index) => !string.IsNullOrWhiteSpace(step?.text) ? step.text.Trim() : $"步骤{index + 1}")
            .ToArray();
        string[] stepActionTypes = InferStepActionTypesFromPlanSteps(planSteps, specificSlot);
        string[] stepNavigationModes = InferStepNavigationModesFromPlanSteps(planSteps, stepActionTypes);
        StepIntentDefinition[] stepIntents = BuildStepIntentsFromPlanSteps(planSteps, stepActionTypes, specificSlot);
        RoutePolicyDefinition[] stepRoutePolicies = BuildFallbackRoutePolicies(steps.Length, specificSlot);
        // 提取当前智能体视角下的协同规则。
        TeamCoordinationDirective[] coordinationDirectives = ExtractCoordinationDirectivesFromResponse(llmResponse);
        // 提取任务默认导航策略。
        NavigationPolicy navPolicy = ExtractMissionNavigationPolicyFromResponse(llmResponse);

        // 如果当前槽位本身是“覆盖一个集合的若干成员”，这里会修复 LLM 漏掉的覆盖步骤。
        RepairPlanForCoverageTargets(
            specificSlot,
            ref steps,
            ref stepActionTypes,
            ref stepNavigationModes,
            ref stepIntents,
            ref stepRoutePolicies);

        if (specificSlot != null)
        {
            // 先把 assignedSlot 做 grounding，确保其 targetRef 已经绑定到世界中的真实目标。
            GroundMissionTaskSlotInPlace(specificSlot, mission != null ? mission.missionDescription : string.Empty);
        }
        // 再把每一步 stepIntent 的 primaryTargetRef / viaTargetRefs 做 grounding。
        GroundStepIntentsInPlace(stepIntents, specificSlot);
        // 最后把计划中附带的协同规则也落到世界模型。
        GroundCoordinationDirectivesInPlace(
            coordinationDirectives,
            specificSlot != null ? specificSlot.target : (mission != null ? mission.teamObjective : string.Empty));
        // coverage repair 和 grounding 可能会改写 stepIntent，因此最后再把结果回填成规范化 planSteps。
        planSteps = BuildPlanStepsFromLegacy(steps, stepActionTypes, stepIntents, specificSlot, planSteps);

        // 真正生成 currentPlan；动作决策模块后续会持续读取这个对象。
        currentPlan = new Plan
        {
            // 保存团队任务 ID。
            missionId = mission.missionId,
            // 保存总任务描述。
            mission = mission.missionDescription,
            // 保存总任务类型。
            missionType = mission.missionType,
            // 保存关系类型。
            relationshipType = mission.relationshipType,
            // 保存团队成功条件。
            successCondition = mission.successCondition,
            // 保存团队失败条件。
            failureCondition = mission.failureCondition,
            // 保存任务级默认导航策略。
            navigationPolicy = navPolicy,
            // 保存当前智能体承担的角色。
            agentRole = assignedRole,
            // 保存简化版本地步骤定义。
            planSteps = planSteps,
            // 保存按 planSteps 推导出的兼容动作标签。
            stepActionTypes = stepActionTypes,
            // 保存按 planSteps 推导出的兼容导航标签。
            stepNavigationModes = stepNavigationModes,
            // 保存动作层仍在读取的结构化意图缓存。
            stepIntents = stepIntents,
            // 保存动作层仍在读取的路径策略缓存。
            stepRoutePolicies = stepRoutePolicies,
            // 保存当前智能体相关的协同约束。
            coordinationDirectives = coordinationDirectives,
            // 绑定当前智能体被分配的槽位。
            assignedSlot = specificSlot,
            // 从第 0 步开始执行。
            currentStep = 0,
            // 记录计划创建时间。
            created = DateTime.Now,
            // 当前默认优先级。
            priority = Priority.Normal,
            // 记录这份计划是谁分配给当前智能体的。
            assignedBy = mission.coordinatorId,
            // 记录本轮任务的通信模式。
            commMode = mission.communicationMode
        };

        // 输出计划元数据，方便检查 LLM 输出是否被正确落地。
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
        Debug.Log($"总步骤数: {currentPlan.planSteps.Length}");

        // 逐步打印步骤详情，核对动作类型、导航模式和目标信息是否一致。
        for (int i = 0; i < currentPlan.planSteps.Length; i++)
        {
            // 如果动作类型数组缺项，就用 NA 标记。
            string intent = (currentPlan.stepActionTypes != null && i < currentPlan.stepActionTypes.Length) ? currentPlan.stepActionTypes[i] : "NA";
            // 如果导航模式数组缺项，也用 NA 标记。
            string nav = (currentPlan.stepNavigationModes != null && i < currentPlan.stepNavigationModes.Length) ? currentPlan.stepNavigationModes[i] : "NA";
            // 默认主目标展示为 none。
            string target = "none";
            // 默认经过点展示为 none。
            string via = "none";
            if (currentPlan.stepIntents != null && i < currentPlan.stepIntents.Length && currentPlan.stepIntents[i] != null)
            {
                // 读取当前步骤的主目标。
                target = currentPlan.stepIntents[i].primaryTarget;
                // 读取当前步骤的经过点列表，并拼成可读字符串。
                via = currentPlan.stepIntents[i].orderedViaTargets != null && currentPlan.stepIntents[i].orderedViaTargets.Length > 0
                    ? string.Join("|", currentPlan.stepIntents[i].orderedViaTargets)
                    : "none";
            }
            // 输出当前步骤摘要。
            Debug.Log($"步骤 {i + 1}: {currentPlan.planSteps[i].text} | intent={intent} | nav={nav} | target={target} | via={via}");
        }
        Debug.Log("=== 计划详情结束 ===");

        if (memoryModule != null)
        {
            // 记录“我被分配到了哪个任务/角色/槽位”。
            memoryModule.RememberMissionAssignment(mission, assignedRole, specificSlot, mission.communicationMode);
            // 记录一份计划快照，便于后续反思回放。
            memoryModule.RememberPlanSnapshot(
                mission != null ? mission.missionId : string.Empty,
                specificSlot != null ? specificSlot.slotId : string.Empty,
                steps != null && steps.Length > 0 ? steps[0] : "plan_start",
                $"reasoning={reasoning}; steps={string.Join(" -> ", steps ?? new string[0])}",
                specificSlot != null ? specificSlot.target : string.Empty,
                tags: new[] { assignedRole.ToString(), specificSlot != null ? specificSlot.slotLabel : string.Empty });
        }

        if (reflectionModule != null)
        {
            // 告知反思模块：当前任务已被本智能体接受。
            reflectionModule.NotifyMissionAccepted(mission, specificSlot, assignedRole);
        }

        // 最后向协调者发送角色/槽位接受回执。
        SendRoleAcceptance(assignedRole.ToString(), reasoning);
    }

    /// <summary>
    /// 只从精简版 planSteps 读取个人计划。
    /// 当前主链不再接受旧 steps 数组回退，避免把字段名或半结构化文本误解析成假步骤。
    /// </summary>
    private PlanStepDefinition[] ExtractPlanStepsFromResponse(string response, MissionTaskSlot assignedSlot)
    {
        try
        {
            string parsedJson;
            string parseError;
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out parsedJson, out parseError) &&
                planResponse != null)
            {
                // 第一优先级：直接消费当前主路径要求的 planSteps。
                if (planResponse.planSteps != null && planResponse.planSteps.Length > 0)
                {
                    PlanStepDefinition[] raw = planResponse.planSteps;
                    PlanStepDefinition[] normalized = new PlanStepDefinition[raw.Length];
                    for (int i = 0; i < raw.Length; i++)
                    {
                        normalized[i] = NormalizePlanStepDefinition(raw[i], i, assignedSlot, null, null);
                    }
                    return normalized;
                }
            }

            // 如果标准解析失败，继续尝试从被截断的原始响应里抢救 planSteps。
            // 这里不要求整段 JSON 完整闭合，只要能取出若干个完整 step 对象，就允许继续执行。
            if (TryExtractPlanStepsLoosely(response, assignedSlot, out PlanStepDefinition[] rescuedPlanSteps) &&
                rescuedPlanSteps != null &&
                rescuedPlanSteps.Length > 0)
            {
                Debug.LogWarning($"planSteps 使用截断容错解析恢复 {rescuedPlanSteps.Length} 条步骤");
                return rescuedPlanSteps;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"planSteps 解析失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 当 LLM 的个人计划 JSON 在 planSteps 之后被截断时，
    /// 尝试直接从原始文本里抢救已经完整返回的步骤对象。
    /// </summary>
    /// <param name="response">LLM 原始响应文本，允许不闭合。</param>
    /// <param name="assignedSlot">当前智能体已分配的岗位，用于补默认值。</param>
    /// <param name="planSteps">成功恢复的步骤数组。</param>
    /// <returns>至少恢复出一条有效步骤时返回 true。</returns>
    private bool TryExtractPlanStepsLoosely(string response, MissionTaskSlot assignedSlot, out PlanStepDefinition[] planSteps)
    {
        // 默认失败时返回 null，调用方会继续走默认计划兜底。
        planSteps = null;
        if (string.IsNullOrWhiteSpace(response)) return false;

        // 先找 planSteps 字段；如果响应里根本没有这个键，就没有必要继续做截断恢复。
        int keyPos = response.IndexOf("\"planSteps\"", StringComparison.OrdinalIgnoreCase);
        if (keyPos < 0) return false;

        // 找到数组起始位置；planSteps 必须是数组。
        int colonPos = response.IndexOf(':', keyPos);
        if (colonPos < 0) return false;
        int arrayStart = response.IndexOf('[', colonPos);
        if (arrayStart < 0) return false;

        // 逐字符扫描 planSteps 数组，尽量提取出其中已经完整闭合的步骤对象。
        List<PlanStepDefinition> recovered = new List<PlanStepDefinition>();
        bool inString = false;
        bool escaped = false;
        int arrayDepth = 0;
        int objectDepth = 0;
        int currentObjectStart = -1;

        for (int i = arrayStart; i < response.Length; i++)
        {
            char c = response[i];

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
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '[')
            {
                arrayDepth++;
                continue;
            }

            if (c == ']')
            {
                arrayDepth--;
                if (arrayDepth <= 0)
                {
                    break;
                }
                continue;
            }

            if (c == '{')
            {
                objectDepth++;
                if (objectDepth == 1)
                {
                    currentObjectStart = i;
                }
                continue;
            }

            if (c == '}')
            {
                objectDepth--;
                if (objectDepth == 0 && currentObjectStart >= 0)
                {
                    string objectJson = response.Substring(currentObjectStart, i - currentObjectStart + 1);
                    if (TryParseRecoveredPlanStep(objectJson, recovered.Count, assignedSlot, out PlanStepDefinition recoveredStep))
                    {
                        recovered.Add(recoveredStep);
                    }
                    currentObjectStart = -1;
                }
            }
        }

        if (recovered.Count == 0) return false;
        planSteps = recovered.ToArray();
        return true;
    }

    /// <summary>
    /// 把从截断响应中切出来的单个步骤对象解析成规范化的 PlanStepDefinition。
    /// </summary>
    /// <param name="objectJson">单个步骤对象的 JSON 片段，要求该对象本身完整闭合。</param>
    /// <param name="index">当前步骤索引，用于补 stepId。</param>
    /// <param name="assignedSlot">当前岗位，用于补默认目标和完成条件。</param>
    /// <param name="step">解析成功后的步骤对象。</param>
    /// <returns>成功解析出有效步骤时返回 true。</returns>
    private bool TryParseRecoveredPlanStep(string objectJson, int index, MissionTaskSlot assignedSlot, out PlanStepDefinition step)
    {
        step = null;
        if (string.IsNullOrWhiteSpace(objectJson)) return false;

        try
        {
            JObject stepObj = JObject.Parse(objectJson);
            PlanStepDefinition raw = new PlanStepDefinition
            {
                // 逐字段读取当前步骤；缺失时再交给 NormalizePlanStepDefinition 补默认值。
                stepId = ((string)stepObj["stepId"] ?? $"step_{index + 1}").Trim(),
                text = ((string)stepObj["text"] ?? string.Empty).Trim(),
                targetText = ((string)stepObj["targetText"] ?? string.Empty).Trim(),
                targetKindHint = ((string)stepObj["targetKindHint"] ?? string.Empty).Trim(),
                relationHint = ((string)stepObj["relationHint"] ?? string.Empty).Trim(),
                completionCondition = ((string)stepObj["completionCondition"] ?? string.Empty).Trim(),
                viaTargets = ExtractStringArrayFromToken(stepObj["viaTargets"])
            };

            // 至少要有文本、目标、完成条件中的任意一项，才认为这个步骤有意义。
            if (string.IsNullOrWhiteSpace(raw.text) &&
                string.IsNullOrWhiteSpace(raw.targetText) &&
                string.IsNullOrWhiteSpace(raw.completionCondition))
            {
                return false;
            }

            step = NormalizePlanStepDefinition(raw, index, assignedSlot, null, null);
            return true;
        }
        catch
        {
            // 单个步骤对象损坏时只跳过该步骤，不让整条计划恢复失败。
            return false;
        }
    }

    /// <summary>
    /// 用最终落地后的旧兼容数组重建简化版 planSteps。
    /// 这样 ActionDecisionModule 读取到的 planSteps 与 repair/grounding 后的真实计划保持一致。
    /// </summary>
    private PlanStepDefinition[] BuildPlanStepsFromLegacy(
        string[] steps,
        string[] stepActionTypes,
        StepIntentDefinition[] stepIntents,
        MissionTaskSlot assignedSlot,
        PlanStepDefinition[] existingPlanSteps = null)
    {
        int count = steps != null ? steps.Length : 0;
        PlanStepDefinition[] result = new PlanStepDefinition[count];
        for (int i = 0; i < count; i++)
        {
            PlanStepDefinition template = existingPlanSteps != null && i < existingPlanSteps.Length ? existingPlanSteps[i] : null;
            StepIntentDefinition stepIntent = stepIntents != null && i < stepIntents.Length ? stepIntents[i] : null;
            result[i] = NormalizePlanStepDefinition(
                template,
                i,
                assignedSlot,
                stepIntent,
                steps[i]);
        }
        return result;
    }

    /// <summary>
    /// 对 planStep 做最小规范化，并在缺失时从 stepIntent / assignedSlot 补默认值。
    /// </summary>
    private PlanStepDefinition NormalizePlanStepDefinition(
        PlanStepDefinition src,
        int index,
        MissionTaskSlot assignedSlot,
        StepIntentDefinition stepIntent,
        string fallbackStepText)
    {
        string[] fallbackVia = Array.Empty<string>();
        if (src != null && src.viaTargets != null && src.viaTargets.Length > 0)
        {
            fallbackVia = src.viaTargets;
        }
        else if (stepIntent != null && stepIntent.orderedViaTargets != null && stepIntent.orderedViaTargets.Length > 0)
        {
            fallbackVia = stepIntent.orderedViaTargets;
        }
        else if (index == 0 && assignedSlot != null && assignedSlot.viaTargets != null && assignedSlot.viaTargets.Length > 0)
        {
            fallbackVia = assignedSlot.viaTargets;
        }

        string targetText = src != null ? src.targetText : string.Empty;
        if (string.IsNullOrWhiteSpace(targetText))
        {
            targetText = stepIntent != null ? stepIntent.primaryTarget : string.Empty;
        }
        if (string.IsNullOrWhiteSpace(targetText) && assignedSlot != null)
        {
            targetText = assignedSlot.target;
        }

        StructuredTargetReference targetRef = stepIntent != null ? stepIntent.primaryTargetRef : null;
        if (targetRef == null && assignedSlot != null)
        {
            targetRef = assignedSlot.targetRef;
        }

        return new PlanStepDefinition
        {
            stepId = !string.IsNullOrWhiteSpace(src != null ? src.stepId : null) ? src.stepId.Trim() : $"step_{index + 1}",
            text = !string.IsNullOrWhiteSpace(src != null ? src.text : null)
                ? src.text.Trim()
                : (!string.IsNullOrWhiteSpace(fallbackStepText) ? fallbackStepText.Trim() : $"步骤{index + 1}"),
            targetText = string.IsNullOrWhiteSpace(targetText) ? string.Empty : targetText.Trim(),
            targetKindHint = !string.IsNullOrWhiteSpace(src != null ? src.targetKindHint : null)
                ? src.targetKindHint.Trim()
                : (targetRef != null ? targetRef.mode.ToString() : StructuredTargetMode.Unknown.ToString()),
            relationHint = !string.IsNullOrWhiteSpace(src != null ? src.relationHint : null)
                ? src.relationHint.Trim()
                : (targetRef != null ? targetRef.relation : string.Empty),
            viaTargets = fallbackVia
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            completionCondition = !string.IsNullOrWhiteSpace(src != null ? src.completionCondition : null)
                ? src.completionCondition.Trim()
                : (!string.IsNullOrWhiteSpace(stepIntent != null ? stepIntent.completionCondition : null)
                    ? stepIntent.completionCondition.Trim()
                    : (assignedSlot != null && !string.IsNullOrWhiteSpace(assignedSlot.completionCondition)
                        ? assignedSlot.completionCondition.Trim()
                        : $"完成步骤{index + 1}"))
        };
    }

    /// <summary>
    /// 从简化版 planSteps 推导旧兼容动作类型数组。
    /// 这些标签主要用于兼容旧执行链，不再是规划层的核心输出。
    /// </summary>
    private string[] InferStepActionTypesFromPlanSteps(PlanStepDefinition[] planSteps, MissionTaskSlot assignedSlot)
    {
        if (planSteps == null || planSteps.Length == 0) return Array.Empty<string>();
        string[] result = new string[planSteps.Length];
        for (int i = 0; i < planSteps.Length; i++)
        {
            result[i] = InferStepActionTypeFromPlanStep(planSteps[i], assignedSlot);
        }
        return result;
    }

    private string[] InferStepNavigationModesFromPlanSteps(PlanStepDefinition[] planSteps, string[] stepActionTypes)
    {
        if (planSteps == null || planSteps.Length == 0) return Array.Empty<string>();
        string[] result = new string[planSteps.Length];
        for (int i = 0; i < planSteps.Length; i++)
        {
            string actionType = stepActionTypes != null && i < stepActionTypes.Length ? stepActionTypes[i] : "Idle";
            if (string.Equals(actionType, "Communicate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionType, "Observe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionType, "Idle", StringComparison.OrdinalIgnoreCase))
            {
                result[i] = "None";
                continue;
            }

            string kindHint = planSteps[i] != null ? planSteps[i].targetKindHint ?? string.Empty : string.Empty;
            bool hasVia = planSteps[i] != null && planSteps[i].viaTargets != null && planSteps[i].viaTargets.Length > 0;
            if (hasVia ||
                string.Equals(kindHint, StructuredTargetMode.Entity.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kindHint, StructuredTargetMode.Area.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kindHint, StructuredTargetMode.Collection.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                result[i] = "AStar";
            }
            else
            {
                result[i] = "Direct";
            }
        }

        return result;
    }

    private StepIntentDefinition[] BuildStepIntentsFromPlanSteps(PlanStepDefinition[] planSteps, string[] stepActionTypes, MissionTaskSlot assignedSlot)
    {
        if (planSteps == null || planSteps.Length == 0) return Array.Empty<StepIntentDefinition>();

        StepIntentDefinition[] result = new StepIntentDefinition[planSteps.Length];
        for (int i = 0; i < planSteps.Length; i++)
        {
            PlanStepDefinition step = NormalizePlanStepDefinition(planSteps[i], i, assignedSlot, null, null);
            string actionType = stepActionTypes != null && i < stepActionTypes.Length ? stepActionTypes[i] : "Idle";
            StepIntentType intentType = StepIntentType.Unknown;
            if (string.Equals(actionType, "Move", StringComparison.OrdinalIgnoreCase)) intentType = StepIntentType.Navigate;
            else if (string.Equals(actionType, "Observe", StringComparison.OrdinalIgnoreCase)) intentType = StepIntentType.Observe;
            else if (string.Equals(actionType, "Communicate", StringComparison.OrdinalIgnoreCase)) intentType = StepIntentType.Communicate;
            else if (string.Equals(actionType, "Interact", StringComparison.OrdinalIgnoreCase)) intentType = StepIntentType.Interact;

            StructuredTargetMode targetMode = ParseStructuredTargetModeHint(step.targetKindHint);
            StructuredTargetCardinality card = targetMode == StructuredTargetMode.Collection
                ? StructuredTargetCardinality.All
                : StructuredTargetCardinality.One;

            StructuredTargetReference targetRef = NormalizeStructuredTargetReference(
                new StructuredTargetReference
                {
                    mode = targetMode,
                    cardinality = card,
                    rawText = step.targetText,
                    displayName = BuildPlanStepDisplayName(step.targetText, step.relationHint),
                    relation = string.IsNullOrWhiteSpace(step.relationHint) ? string.Empty : step.relationHint.Trim(),
                    areaHint = targetMode == StructuredTargetMode.Area ? step.targetText : string.Empty,
                    selectorText = targetMode == StructuredTargetMode.Collection ? step.targetText : string.Empty,
                    notes = "derived_from_plan_step"
                },
                step.targetText,
                targetMode,
                card);

            string finalBehavior = "arrive";
            if (intentType == StepIntentType.Observe) finalBehavior = "observe";
            else if (intentType == StepIntentType.Communicate) finalBehavior = "report";
            else if (intentType == StepIntentType.Interact) finalBehavior = "interact";

            result[i] = new StepIntentDefinition
            {
                stepText = step.text,
                intentType = intentType,
                primaryTarget = step.targetText,
                primaryTargetRef = targetRef,
                orderedViaTargets = step.viaTargets ?? Array.Empty<string>(),
                orderedViaTargetRefs = NormalizeStructuredTargetReferenceArray(null, step.viaTargets ?? Array.Empty<string>()),
                requestedTeammateIds = Array.Empty<string>(),
                observationFocus = intentType == StepIntentType.Observe ? step.targetText : "none",
                communicationGoal = intentType == StepIntentType.Communicate ? step.completionCondition : "none",
                finalBehavior = finalBehavior,
                completionCondition = step.completionCondition
            };
        }

        return result;
    }

    private string InferStepActionTypeFromPlanStep(PlanStepDefinition step, MissionTaskSlot assignedSlot)
    {
        PlanStepDefinition normalized = NormalizePlanStepDefinition(step, 0, assignedSlot, null, null);
        string combined = $"{normalized.text} {normalized.targetText} {normalized.completionCondition}".Trim().ToLowerInvariant();

        if (ContainsAnyToken(combined, "汇报", "回传", "报告", "通知", "广播", "同步", "发送", "communicate", "report", "broadcast", "notify", "transmit", "message"))
        {
            return "Communicate";
        }

        if (ContainsAnyToken(combined, "交互", "拾取", "投放", "装卸", "修复", "搭建", "操作", "interact", "pickup", "drop", "deliver", "repair", "build", "operate"))
        {
            return "Interact";
        }

        if (ContainsAnyToken(combined, "观察", "扫描", "侦察", "搜索", "巡查", "巡逻", "监视", "确认", "observe", "scan", "inspect", "search", "recon", "watch", "monitor", "patrol"))
        {
            return "Observe";
        }

        if (ContainsAnyToken(combined, "前往", "移动", "接近", "抵达", "到达", "绕", "环绕", "跟随", "护送", "靠近", "前出", "navigate", "move", "approach", "reach", "orbit", "follow", "escort", "travel") ||
            (normalized.viaTargets != null && normalized.viaTargets.Length > 0))
        {
            return "Move";
        }

        // 只给了目标但没有明确动词时，保守认为这是一次导航/接近步骤。
        if (!string.IsNullOrWhiteSpace(normalized.targetText))
        {
            return "Move";
        }

        return "Idle";
    }

    private static StructuredTargetMode ParseStructuredTargetModeHint(string raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse(raw.Trim(), true, out StructuredTargetMode mode))
        {
            return mode;
        }

        return StructuredTargetMode.Unknown;
    }

    private static string BuildPlanStepDisplayName(string targetText, string relationHint)
    {
        string display = string.IsNullOrWhiteSpace(targetText) ? string.Empty : targetText.Trim();
        string relation = string.IsNullOrWhiteSpace(relationHint) ? string.Empty : relationHint.Trim();
        if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(relation)) return display;

        string stripped = display.Replace(relation, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stripped) ? display : stripped;
    }

    private static bool ContainsAnyToken(string text, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(text) || tokens == null) return false;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (!string.IsNullOrWhiteSpace(token) && text.Contains(token.ToLowerInvariant()))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 当槽位本身已经被明确切成一个“覆盖成员列表”时，
    /// 若 LLM 个人计划没有把这些成员真正展开成 step，系统会做一次极保守的计划修复。
    ///
    /// 这不是在猜用户文本，
    /// 而是在消费上游已经给出的结构化 memberEntityIds。
    /// 这样做可以保证“所有 building 的子集侦察”至少真的有机会逐个执行。
    /// </summary>
    private void RepairPlanForCoverageTargets(
        MissionTaskSlot assignedSlot,
        ref string[] steps,
        ref string[] stepActionTypes,
        ref string[] stepNavigationModes,
        ref StepIntentDefinition[] stepIntents,
        ref RoutePolicyDefinition[] stepRoutePolicies)
    {
        if (!ShouldExpandCoveragePlan(assignedSlot, stepIntents)) return;

        BuildCoveragePlanFromAssignedSlot(
            assignedSlot,
            out steps,
            out stepActionTypes,
            out stepNavigationModes,
            out stepIntents,
            out stepRoutePolicies);
    }

    private bool ShouldExpandCoveragePlan(MissionTaskSlot assignedSlot, StepIntentDefinition[] stepIntents)
    {
        string[] members = assignedSlot != null && assignedSlot.targetRef != null
            ? (assignedSlot.targetRef.memberEntityIds ?? Array.Empty<string>())
            : Array.Empty<string>();
        members = members.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (members.Length <= 1) return false;

        EnsureCampusGridReference();
        HashSet<string> expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < members.Length; i++)
        {
            string member = members[i];
            if (string.IsNullOrWhiteSpace(member)) continue;

            expected.Add(member.Trim());
            if (campusGrid != null &&
                campusGrid.TryResolveFeatureSpatialProfile(member, transform.position, out CampusGrid2D.FeatureSpatialProfile profile, preferWalkableApproach: true, ignoreCase: true) &&
                profile != null)
            {
                if (!string.IsNullOrWhiteSpace(profile.uid)) expected.Add(profile.uid.Trim());
                if (!string.IsNullOrWhiteSpace(profile.runtimeAlias)) expected.Add(profile.runtimeAlias.Trim());
                if (!string.IsNullOrWhiteSpace(profile.name)) expected.Add(profile.name.Trim());
            }
        }

        HashSet<string> covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (stepIntents != null)
        {
            for (int i = 0; i < stepIntents.Length; i++)
            {
                StepIntentDefinition intent = stepIntents[i];
                if (intent == null) continue;

                string query = ResolveStructuredTargetQuery(intent.primaryTargetRef, intent.primaryTarget);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    covered.Add(query.Trim());
                }

                if (campusGrid != null &&
                    !string.IsNullOrWhiteSpace(query) &&
                    campusGrid.TryResolveFeatureSpatialProfile(query, transform.position, out CampusGrid2D.FeatureSpatialProfile profile, preferWalkableApproach: true, ignoreCase: true) &&
                    profile != null)
                {
                    if (!string.IsNullOrWhiteSpace(profile.uid)) covered.Add(profile.uid.Trim());
                    if (!string.IsNullOrWhiteSpace(profile.runtimeAlias)) covered.Add(profile.runtimeAlias.Trim());
                    if (!string.IsNullOrWhiteSpace(profile.name)) covered.Add(profile.name.Trim());
                }
            }
        }

        return expected.Any(v => !covered.Contains(v));
    }

    /// <summary>
    /// 把“成员子集覆盖槽位”机械展开成一个最小可执行计划。
    /// 规则很简单：
    /// - 每个成员至少有 1 个 step；
    /// - 首个 step 继承槽位 viaTargets；
    /// - 最后如有 report 语义，再补 1 个汇总 step。
    /// </summary>
    /// <param name="assignedSlot">当前被分配的覆盖型槽位，核心信息在 `targetRef.memberEntityIds` 里。</param>
    /// <param name="steps">输出参数，返回生成好的自然语言步骤数组。</param>
    /// <param name="stepActionTypes">输出参数，返回每一步的动作类型数组。</param>
    /// <param name="stepNavigationModes">输出参数，返回每一步的导航模式数组。</param>
    /// <param name="stepIntents">输出参数，返回每一步的结构化语义意图数组。</param>
    /// <param name="stepRoutePolicies">输出参数，返回每一步的路径策略数组。</param>
    private void BuildCoveragePlanFromAssignedSlot(
        MissionTaskSlot assignedSlot,
        out string[] steps,
        out string[] stepActionTypes,
        out string[] stepNavigationModes,
        out StepIntentDefinition[] stepIntents,
        out RoutePolicyDefinition[] stepRoutePolicies)
    {
        // 先从 assignedSlot.targetRef.memberEntityIds 中取出需要覆盖的成员列表。
        string[] members = assignedSlot != null && assignedSlot.targetRef != null
            ? (assignedSlot.targetRef.memberEntityIds ?? Array.Empty<string>())
            : Array.Empty<string>();
        // 清掉空白项并去重，避免同一个成员重复生成多个步骤。
        members = members
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (members.Length == 0)
        {
            // 没有任何成员可覆盖时，直接返回一组空计划结果。
            steps = Array.Empty<string>();
            stepActionTypes = Array.Empty<string>();
            stepNavigationModes = Array.Empty<string>();
            stepIntents = Array.Empty<StepIntentDefinition>();
            stepRoutePolicies = Array.Empty<RoutePolicyDefinition>();
            return;
        }

        // 如果槽位最终行为带有 report / communicate 语义，就在最后额外补一个汇总回传步骤。
        bool needsReport = assignedSlot != null &&
                           !string.IsNullOrWhiteSpace(assignedSlot.finalBehavior) &&
                           (assignedSlot.finalBehavior.IndexOf("report", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            assignedSlot.finalBehavior.IndexOf("commun", StringComparison.OrdinalIgnoreCase) >= 0);
        // 需要汇总回传时，额外步骤数为 1，否则为 0。
        int extraSteps = needsReport ? 1 : 0;
        // 总步骤数等于成员数加上可能存在的回传步骤。
        int total = members.Length + extraSteps;

        // 按最终步骤数一次性初始化所有并行数组。
        steps = new string[total];
        stepActionTypes = new string[total];
        stepNavigationModes = new string[total];
        stepIntents = new StepIntentDefinition[total];
        stepRoutePolicies = new RoutePolicyDefinition[total];

        for (int i = 0; i < members.Length; i++)
        {
            // 当前正在展开的成员 ID。
            string member = members[i];
            // 只有第一个成员步骤会继承槽位原本的 viaTargets / viaTargetRefs。
            bool isFirst = i == 0;

            // 自然语言步骤文本：前往并侦察某个成员。
            steps[i] = $"前往并侦察 {member}";
            // 覆盖型步骤默认是移动型动作。
            stepActionTypes[i] = "Move";
            // 覆盖型步骤默认使用 A* 去接近目标成员。
            stepNavigationModes[i] = "AStar";
            // 构建当前成员对应的结构化 stepIntent。
            stepIntents[i] = new StepIntentDefinition
            {
                // 让 stepIntent.stepText 与自然语言步骤保持一致。
                stepText = steps[i],
                // 这是一个导航型意图。
                intentType = StepIntentType.Navigate,
                // 当前步骤的最终目标就是这个成员。
                primaryTarget = member,
                // 用单成员 Entity 目标构造 primaryTargetRef，避免集合目标在执行层过于模糊。
                primaryTargetRef = NormalizeStructuredTargetReference(
                    new StructuredTargetReference
                    {
                        mode = StructuredTargetMode.Entity,
                        cardinality = StructuredTargetCardinality.One,
                        rawText = member,
                        executableQuery = member,
                        entityId = member,
                        entityClass = assignedSlot != null && assignedSlot.targetRef != null ? assignedSlot.targetRef.entityClass : "CampusFeature",
                        displayName = member,
                        selectorText = member,
                        collectionKey = assignedSlot != null && assignedSlot.targetRef != null ? assignedSlot.targetRef.collectionKey : string.Empty,
                        memberEntityIds = new[] { member },
                        areaHint = assignedSlot != null && assignedSlot.targetRef != null ? assignedSlot.targetRef.areaHint : string.Empty,
                        relation = string.Empty,
                        anchorText = member,
                        isDynamic = false
                    },
                    member,
                    StructuredTargetMode.Entity,
                    StructuredTargetCardinality.One),
                // 仅首个成员步骤继承槽位原本的经过点；后续成员直接去各自目标。
                orderedViaTargets = isFirst ? (assignedSlot != null ? assignedSlot.viaTargets ?? Array.Empty<string>() : Array.Empty<string>()) : Array.Empty<string>(),
                // 首个成员步骤的结构化经过点也同步继承。
                orderedViaTargetRefs = isFirst
                    ? NormalizeStructuredTargetReferenceArray(
                        assignedSlot != null ? assignedSlot.viaTargetRefs : null,
                        assignedSlot != null ? assignedSlot.viaTargets : null)
                    : Array.Empty<StructuredTargetReference>(),
                // 覆盖步骤默认不额外请求队友参与。
                requestedTeammateIds = Array.Empty<string>(),
                // observationFocus 优先继承集合目标原本的 selectorText。
                observationFocus = assignedSlot != null && assignedSlot.targetRef != null && !string.IsNullOrWhiteSpace(assignedSlot.targetRef.selectorText)
                    ? assignedSlot.targetRef.selectorText
                    : "coverage_target",
                // 这一步不是通信动作，因此 communicationGoal 固定为 none。
                communicationGoal = "none",
                // 最终行为优先继承槽位定义，否则默认 observe。
                finalBehavior = assignedSlot != null && !string.IsNullOrWhiteSpace(assignedSlot.finalBehavior) ? assignedSlot.finalBehavior : "observe",
                // 完成条件明确写成“完成对某成员的侦察”。
                completionCondition = $"完成对 {member} 的侦察"
            };
            // 每个成员步骤都继承一份和槽位一致的路径策略。
            stepRoutePolicies[i] = MergeRoutePolicyWithAssignedSlot(null, assignedSlot);
        }

        if (needsReport)
        {
            // 最后一个步骤专门留给回传汇总结果。
            int reportIndex = total - 1;
            steps[reportIndex] = "汇总并回传覆盖结果";
            stepActionTypes[reportIndex] = "Communicate";
            stepNavigationModes[reportIndex] = "None";
            stepIntents[reportIndex] = new StepIntentDefinition
            {
                stepText = steps[reportIndex],
                intentType = StepIntentType.Communicate,
                primaryTarget = "none",
                primaryTargetRef = null,
                orderedViaTargets = Array.Empty<string>(),
                orderedViaTargetRefs = Array.Empty<StructuredTargetReference>(),
                requestedTeammateIds = Array.Empty<string>(),
                observationFocus = "none",
                // 通信目标优先复用槽位 completionCondition，明确要回传什么结果。
                communicationGoal = assignedSlot != null && !string.IsNullOrWhiteSpace(assignedSlot.completionCondition)
                    ? assignedSlot.completionCondition
                    : "提交覆盖结果",
                finalBehavior = "report",
                // 回传步骤的完成条件同样优先继承槽位 completionCondition。
                completionCondition = assignedSlot != null && !string.IsNullOrWhiteSpace(assignedSlot.completionCondition)
                    ? assignedSlot.completionCondition
                    : "完成覆盖结果回传"
            };
            // 回传步骤也保留一份与槽位一致的路径策略兜底。
            stepRoutePolicies[reportIndex] = MergeRoutePolicyWithAssignedSlot(null, assignedSlot);
        }
    }

    /// <summary>
    /// 协调者向全队广播任务，并启动后续的偏好收集与槽位裁决。
    /// </summary>
    /// <param name="mission">已经结构化完成的任务对象。</param>
    private void DistributeMissionToAgents(MissionAssignment mission)
    {
        // 记录当前由哪个协调者发起全队分配。
        Debug.Log($"DistributeMissionToAgents called by {agentProperties.AgentID}");
        if (commModule == null)
        {
            // 没有通信模块时无法广播任务，整个团队分配链路只能中断。
            Debug.LogError("CommunicationModule 未找到，无法分发任务");
            return;
        }

        // 每开始一轮新任务，都要清空上一轮残留的角色名额和消息缓存。
        remainingCount.Clear();
        receivedPreferences.Clear();
        receivedPreferencePayloads.Clear();
        assignedTeamDecisions.Clear();
        acceptedAssignedAgents.Clear();
        completedAssignedAgents.Clear();
        releasedAssignedAgents.Clear();
        // 重置“任务是否已完成聚合”的协调者状态。
        missionCompletionAggregated = false;
        // 重置“是否已经发生过执行放行”的团队状态。
        teamExecutionReleased = false;
        // 先根据参与人数判断当前协调者自己是否也要等待统一放行。
        localExecutionReleased = !ShouldGateExecutionByCoordinator(mission);
        // 新一轮团队任务开始时，先把最小共享执行状态重置干净。
        ResetTeamExecutionState(mission);
        foreach (var role in mission.roles)
        {
            // 初始化每种角色还剩多少个名额，供后续裁决时参考。
            remainingCount[role.roleType] = role.requiredCount;
        }

        if (mission.taskSlots == null || mission.taskSlots.Length == 0)
        {
            // 如果上游没给稳定的槽位列表，这里按角色需求保守展开一份兜底槽位。
            mission.taskSlots = BuildTaskSlotsForMission(mission.missionDescription, mission.roles, mission.missionType, mission.requiredAgentCount);
        }

        // 槽位和协调者内部集合都已经就绪后，刷新一次团队共享状态快照。
        RefreshTeamExecutionStateSnapshot();

        // 组装任务广播载荷，把 mission 主体、任务级协同规则和 briefing 一次发给全队。
        TaskAnnouncementPayload payload = new TaskAnnouncementPayload
        {
            // 完整任务对象。
            mission = mission,
            // 任务级协同规则数组；为空时用空数组占位。
            missionDirectives = mission.coordinationDirectives ?? new TeamCoordinationDirective[0],
            // 额外附上一句 teamObjective 作为 briefing。
            briefing = mission.teamObjective
        };

        // 广播给所有智能体，请它们先回传角色偏好。
        commModule.SendStructuredMessage("All", MessageType.TaskAnnouncement, payload, 2);

        // 启动等待协程，收集偏好并在超时后做槽位分配。
        StartCoroutine(WaitAndAssignRoles(mission));
    }

    /// <summary>
    /// 协调者等待全队上报角色偏好，然后按槽位逐个挑选最合适的智能体。
    /// </summary>
    /// <param name="mission">当前待分配的任务对象。</param>
    private IEnumerator WaitAndAssignRoles(MissionAssignment mission)
    {
        // 记录开始等待的时间。
        float waitStart = Time.time;
        // 最长等待 15 秒，避免某个智能体掉线导致整队一直卡住。
        float timeout = 15f;
        // 期望收到的偏好数量通常等于槽位数；没有槽位时退回到 requiredAgentCount。
        int expectedSlotCount = mission != null && mission.taskSlots != null && mission.taskSlots.Length > 0
            ? mission.taskSlots.Length
            : Mathf.Max(1, mission != null ? mission.requiredAgentCount : 1);

        while (Time.time - waitStart < timeout)
        {
            // 把新旧两种偏好缓存结构合并统计，兼容旧链路。
            int receivedCount = receivedPreferencePayloads.Keys.Union(receivedPreferences.Keys).Count();
            if (receivedCount >= expectedSlotCount)
            {
                // 已经等到足够多的偏好后就提前结束等待。
                break;
            }

            // 还没等够时，每 0.2 秒轮询一次。
            yield return new WaitForSeconds(0.2f);
        }

        // 记录已经分配过槽位的智能体，避免一个智能体被重复分配多个槽位。
        HashSet<string> assignedAgents = new HashSet<string>();
        // 暂存“哪个 agent 被分到了哪个角色”，稍后统一发送确认消息。
        List<(string agentId, MissionRole role)> assignments = new List<(string agentId, MissionRole role)>();

        // 拿到当前任务的全部槽位；为空时使用空数组避免空引用。
        MissionTaskSlot[] slots = mission.taskSlots ?? new MissionTaskSlot[0];
        if (slots.Length > 0)
        {
            foreach (MissionTaskSlot slot in slots)
            {
                // 针对当前槽位，从所有未分配智能体里挑得分最高者。
                string bestAgentId = FindBestAgentForSlot(slot, assignedAgents);
                if (string.IsNullOrWhiteSpace(bestAgentId)) continue;

                // 再找到这个槽位对应的 MissionRole 对象。
                MissionRole slotRole = FindMissionRoleForSlot(slot, mission.roles);
                if (slotRole == null) continue;

                // 把这次裁决先记到临时列表里。
                assignments.Add((bestAgentId, slotRole));
                // 记录该智能体已占用一个槽位，后续不再参与其他槽位竞争。
                assignedAgents.Add(bestAgentId);
                // 对应角色的剩余名额减 1。
                remainingCount[slotRole.roleType] = Mathf.Max(0, remainingCount[slotRole.roleType] - 1);
                // 保存完整角色裁决结果，后续接受回执、执行放行和完成聚合都会用到。
                assignedTeamDecisions[bestAgentId] = BuildRoleDecisionPayload(bestAgentId, slotRole, slot, mission);
            }
        }

        if (slots.Length > 0 && assignedTeamDecisions.Count < slots.Length)
        {
            // 如果最终可分配人数不足以覆盖全部槽位，就取消启动，避免半残任务开跑。
            Debug.LogWarning($"[Planning] 任务槽位分配不足，取消启动: assigned={assignedTeamDecisions.Count}, required={slots.Length}, mission={mission.missionDescription}");
            assignedTeamDecisions.Clear();
            yield break;
        }

        foreach ((string agentId, MissionRole role) in assignments)
        {
            // 再从最终决策表里把完整 payload 取出来发送给对应智能体。
            if (assignedTeamDecisions.TryGetValue(agentId, out RoleDecisionPayload payload))
            {
                SendFinalRole(payload);
            }
        }
    }

    /// <summary>
    /// 把最终角色和槽位裁决发送给目标智能体。
    /// </summary>
    /// <param name="payload">结构化角色裁决结果，包含角色、槽位和相关协同规则。</param>
    private void SendFinalRole(RoleDecisionPayload payload)
    {
        // 没有通信模块、payload 为空或 agentId 无效时，不发送。
        if (commModule == null || payload == null || string.IsNullOrWhiteSpace(payload.agentId)) return;
        // 打印裁决详情，方便核对“谁被分到哪个槽位”。
        Debug.Log($"[Planning] 协调者 {agentProperties.AgentID} 分配槽位 -> agent={payload.agentId}, slot={payload.assignedSlot?.slotLabel}, target={payload.assignedSlot?.target}, directives={(payload.directives != null ? payload.directives.Length : 0)}");
        // 向目标智能体发送 RoleConfirmed 消息。
        commModule.SendStructuredMessage(payload.agentId, MessageType.RoleConfirmed, payload, 1);
    }

    /// <summary>
    /// 从所有尚未分配的候选智能体中，挑出当前槽位得分最高者。
    /// </summary>
    /// <param name="slot">当前待分配的任务槽位。</param>
    /// <param name="assignedAgents">已经被其他槽位占用的智能体集合。</param>
    private string FindBestAgentForSlot(MissionTaskSlot slot, HashSet<string> assignedAgents)
    {
        // 默认没有选中任何智能体。
        string bestAgentId = string.Empty;
        // 用负无穷初始化，确保第一个有效候选一定能覆盖。
        float bestScore = float.NegativeInfinity;

        // 兼容新旧两类偏好缓存，把所有候选 agentId 合并后统一打分。
        IEnumerable<string> candidateIds = receivedPreferencePayloads.Keys.Union(receivedPreferences.Keys);
        foreach (string agentId in candidateIds)
        {
            // 已经分配过槽位的智能体不再参与本轮竞争。
            if (assignedAgents.Contains(agentId)) continue;

            // 计算当前候选智能体和当前槽位的匹配分。
            float score = CalculateSlotAssignmentScore(agentId, slot);
            if (score > bestScore)
            {
                // 如果分数更高，就更新当前最佳候选。
                bestScore = score;
                bestAgentId = agentId;
            }
        }

        // 返回得分最高的智能体 ID；若没有候选则返回空字符串。
        return bestAgentId;
    }

    /// <summary>
    /// 计算某个智能体对某个槽位的适配分数。
    /// </summary>
    /// <param name="agentId">候选智能体 ID。</param>
    /// <param name="slot">待分配的任务槽位。</param>
    private float CalculateSlotAssignmentScore(string agentId, MissionTaskSlot slot)
    {
        // 分数从 0 开始累加。
        float score = 0f;

        if (receivedPreferencePayloads.TryGetValue(agentId, out RolePreferencePayload payload) && payload != null)
        {
            if (payload.preferences != null)
            {
                // 偏好越靠前，加分越高。
                int prefIndex = Array.IndexOf(payload.preferences, slot.roleType);
                if (prefIndex >= 0)
                {
                    score += 100f - prefIndex * 20f;
                }
            }

            // 平台类型完全匹配时给一笔固定加分。
            if (payload.agentType == slot.requiredAgentType) score += 30f;
            // 速度越快，机动型任务通常越占优。
            score += Mathf.Clamp(payload.maxSpeed, 0f, 100f) * 0.1f;
            // 感知范围越大，侦察型任务通常越占优。
            score += Mathf.Clamp(payload.perceptionRange, 0f, 200f) * 0.05f;
        }
        else if (receivedPreferences.TryGetValue(agentId, out RoleType[] prefs) && prefs != null)
        {
            // 兼容旧偏好缓存时，只能按角色偏好顺序做一个简化版打分。
            int prefIndex = Array.IndexOf(prefs, slot.roleType);
            if (prefIndex >= 0)
            {
                score += 80f - prefIndex * 15f;
            }
        }

        // 返回最终适配分。
        return score;
    }

    /// <summary>
    /// 为当前槽位找到对应的 MissionRole 定义。
    /// </summary>
    /// <param name="slot">当前槽位。</param>
    /// <param name="roles">任务级角色需求列表。</param>
    private MissionRole FindMissionRoleForSlot(MissionTaskSlot slot, MissionRole[] roles)
    {
        // 槽位或角色数组为空时无法匹配，直接返回 null。
        if (slot == null || roles == null) return null;
        for (int i = 0; i < roles.Length; i++)
        {
            // 逐个读取任务级角色定义。
            MissionRole role = roles[i];
            if (role == null) continue;
            if (role.roleType == slot.roleType && role.agentType == slot.requiredAgentType)
            {
                // 同时匹配角色类型和平台类型时，认为找到了最合适的角色定义。
                return role;
            }
        }
        // 如果完全匹配不到，就退回角色数组中的第一个角色作为兜底。
        return roles.Length > 0 ? roles[0] : null;
    }

    /// <summary>
    /// 把“agent + role + slot”的最终裁决结果封装成可下发的 payload。
    /// </summary>
    /// <param name="agentId">被分配槽位的目标智能体 ID。</param>
    /// <param name="role">该智能体被裁决到的任务角色。</param>
    /// <param name="slot">该智能体被分配到的具体任务槽位。</param>
    /// <param name="mission">当前任务对象，用于补全 missionId 和协同规则上下文。</param>
    private RoleDecisionPayload BuildRoleDecisionPayload(string agentId, MissionRole role, MissionTaskSlot slot, MissionAssignment mission)
    {
        // 为当前槽位构造“当前智能体视角”的完整协同规则集合，避免只保留第一条指令而丢信息。
        TeamCoordinationDirective[] directives = BuildDirectivesForSlot(slot, mission, agentId);
        return new RoleDecisionPayload
        {
            // 标记这份裁决属于哪一轮任务。
            missionId = mission.missionId,
            // 被分配的目标智能体 ID。
            agentId = agentId,
            // 裁决出的角色类型。
            assignedRole = role.roleType,
            // 裁决出的任务槽位。
            assignedSlot = slot,
            // 记录一条便于日志阅读的裁决理由摘要。
            assignmentReason = $"slot={slot.slotLabel}, role={role.roleType}",
            // 兼容旧链路，仍然保留第一条 directive。
            directive = directives != null && directives.Length > 0 ? directives[0] : null,
            // 新链路使用完整 directives 数组。
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
    /// <param name="slot">当前正在分配给某个智能体的任务槽位。</param>
    /// <param name="mission">当前团队任务对象，提供任务级协同规则和 teamObjective。</param>
    /// <param name="agentId">当前目标智能体 ID；这里主要保留接口语义，便于后续按成员定制规则。</param>
    private TeamCoordinationDirective[] BuildDirectivesForSlot(MissionTaskSlot slot, MissionAssignment mission, string agentId)
    {
        // 当前槽位相关的协同规则最终都会汇总到这个列表里。
        List<TeamCoordinationDirective> directives = new List<TeamCoordinationDirective>();
        // 先取任务级协同规则作为来源；如果没有，再走后面的兜底逻辑。
        TeamCoordinationDirective[] missionDirectives = mission != null ? mission.coordinationDirectives : null;

        if (missionDirectives != null)
        {
            for (int i = 0; i < missionDirectives.Length; i++)
            {
                // 逐条读取任务级协同规则。
                TeamCoordinationDirective src = missionDirectives[i];
                if (src == null) continue;

                // 先把原始规则做一轮标准化，补齐空数组和默认字段。
                TeamCoordinationDirective normalized = NormalizeCoordinationDirective(src);
                // 再结合当前槽位和当前任务上下文补出“当前智能体视角”的最终协同规则。
                TeamCoordinationDirective enriched = new TeamCoordinationDirective
                {
                    // 保留原始协同模式，例如 LooseSync / FollowLeader / Independent。
                    coordinationMode = normalized.coordinationMode,
                    // 保留显式 leaderAgentId。
                    leaderAgentId = normalized.leaderAgentId,
                    // 共享目标优先使用规则中已给出的 sharedTarget，否则回退到槽位 target，再回退到 teamObjective。
                    sharedTarget = !string.IsNullOrWhiteSpace(normalized.sharedTarget)
                        ? normalized.sharedTarget
                        : (!string.IsNullOrWhiteSpace(slot != null ? slot.target : string.Empty)
                            ? slot.target
                            : (mission != null ? mission.teamObjective : string.Empty)),
                    // sharedTargetRef 同样按“规则 > 槽位 > teamObjective”顺序补齐。
                    sharedTargetRef = NormalizeStructuredTargetReference(
                        normalized.sharedTargetRef,
                        !string.IsNullOrWhiteSpace(normalized.sharedTarget)
                            ? normalized.sharedTarget
                            : (!string.IsNullOrWhiteSpace(slot != null ? slot.target : string.Empty)
                                ? slot.target
                                : (mission != null ? mission.teamObjective : string.Empty)),
                        StructuredTargetMode.Unknown,
                        StructuredTargetCardinality.One),
                    // 保留走廊预留键。
                    corridorReservationKey = normalized.corridorReservationKey,
                    // 让行列表为空时补空数组。
                    yieldToAgentIds = normalized.yieldToAgentIds ?? new string[0],
                    // 同步点目标为空时补空数组。
                    syncPointTargets = normalized.syncPointTargets ?? new string[0],
                    // 编队槽位优先使用规则显式给定值，否则回退到当前槽位名。
                    formationSlot = !string.IsNullOrWhiteSpace(normalized.formationSlot)
                        ? normalized.formationSlot
                        : (slot != null ? slot.slotLabel : string.Empty)
                };
                // 把当前规则加入结果列表。
                directives.Add(enriched);
            }
        }

        if (directives.Count == 0)
        {
            // 任务级完全没给协同规则时，创建一条最中性的 Independent 规则兜底。
            directives.Add(new TeamCoordinationDirective
            {
                coordinationMode = TeamCoordinationMode.Independent,
                leaderAgentId = string.Empty,
                // 兜底共享目标优先用槽位 target，否则退回 teamObjective。
                sharedTarget = !string.IsNullOrWhiteSpace(slot != null ? slot.target : string.Empty)
                    ? slot.target
                    : (mission != null ? mission.teamObjective : string.Empty),
                // 兜底 sharedTargetRef 优先用槽位 targetRef，否则用同样的文本回退逻辑补全。
                sharedTargetRef = NormalizeStructuredTargetReference(
                    slot != null ? slot.targetRef : null,
                    !string.IsNullOrWhiteSpace(slot != null ? slot.target : string.Empty)
                        ? slot.target
                        : (mission != null ? mission.teamObjective : string.Empty),
                    StructuredTargetMode.Unknown,
                    StructuredTargetCardinality.One),
                corridorReservationKey = string.Empty,
                yieldToAgentIds = new string[0],
                syncPointTargets = new string[0],
                // 兜底时仍然把当前槽位名保留下来，供后续日志和 prompt 使用。
                formationSlot = slot != null ? slot.slotLabel : string.Empty
            });
        }

        // 返回当前智能体在这个槽位上需要遵守的完整协同规则数组。
        return directives.ToArray();
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
        return MissionType.Unknown;
    }

    /// <summary>
    /// 对外接口：获取当前 step 文本。
    /// 若无计划或 step 已结束，返回空字符串。
    /// </summary>
    public string GetCurrentStepDescription()
    {
        if (currentPlan == null) return string.Empty;
        if (currentPlan.currentStep < 0) return string.Empty;

        if (currentPlan.steps != null && currentPlan.currentStep < currentPlan.steps.Length)
        {
            return currentPlan.steps[currentPlan.currentStep] ?? string.Empty;
        }

        if (currentPlan.planSteps != null && currentPlan.currentStep < currentPlan.planSteps.Length && currentPlan.planSteps[currentPlan.currentStep] != null)
        {
            return currentPlan.planSteps[currentPlan.currentStep].text ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// 对外接口：获取当前步骤的简化 planStep。
    /// 若当前计划没有显式保存 planSteps，则由旧兼容数组即时构造一份。
    /// </summary>
    public PlanStepDefinition GetCurrentPlanStep()
    {
        if (currentPlan == null) return null;
        int idx = currentPlan.currentStep;
        if (idx < 0) return null;

        if (currentPlan.planSteps != null && idx < currentPlan.planSteps.Length && currentPlan.planSteps[idx] != null)
        {
            StepIntentDefinition stepIntent = currentPlan.stepIntents != null && idx < currentPlan.stepIntents.Length ? currentPlan.stepIntents[idx] : null;
            string fallbackText = currentPlan.steps != null && idx < currentPlan.steps.Length ? currentPlan.steps[idx] : string.Empty;
            return NormalizePlanStepDefinition(currentPlan.planSteps[idx], idx, currentPlan.assignedSlot, stepIntent, fallbackText);
        }

        StepIntentDefinition fallbackIntent = currentPlan.stepIntents != null && idx < currentPlan.stepIntents.Length ? currentPlan.stepIntents[idx] : null;
        string fallbackStepText = currentPlan.steps != null && idx < currentPlan.steps.Length ? currentPlan.steps[idx] : string.Empty;
        return NormalizePlanStepDefinition(null, idx, currentPlan.assignedSlot, fallbackIntent, fallbackStepText);
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
            // currentPlan.stepIntents 在建计划阶段已经做过一次 repair。
            // 运行时再次取当前 step 时，不能把后续观察/通信步骤里故意清空的 viaTargets 又补回 assignedSlot.viaTargets，
            // 否则执行层会把已经走过的检查点重新当成路径锚点，出现“回头跑检查点”的往返行为。
            return NormalizeStepIntent(
                currentPlan.stepIntents[idx],
                currentPlan.steps[idx],
                GetStepActionTypeHint(idx),
                currentPlan.assignedSlot,
                allowViaFallbackFromAssignedSlot: false);
        }

        return NormalizeStepIntent(
            null,
            currentPlan.steps[idx],
            GetStepActionTypeHint(idx),
            currentPlan.assignedSlot,
            allowViaFallbackFromAssignedSlot: true);
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
        if (currentPlan == null) return -1;

        int cur = currentPlan.currentStep;
        if (currentPlan.steps != null && cur >= 0 && cur < currentPlan.steps.Length)
        {
            string curText = currentPlan.steps[cur] ?? string.Empty;
            if (string.Equals(curText.Trim(), (stepText ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return cur;
            }
        }

        if (!string.IsNullOrWhiteSpace(stepText))
        {
            if (currentPlan.steps != null)
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

            if (currentPlan.planSteps != null)
            {
                for (int i = 0; i < currentPlan.planSteps.Length; i++)
                {
                    string s = currentPlan.planSteps[i] != null ? currentPlan.planSteps[i].text ?? string.Empty : string.Empty;
                    if (string.Equals(s.Trim(), stepText.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }
        }

        if (currentPlan.steps != null && cur >= 0 && cur < currentPlan.steps.Length) return cur;
        if (currentPlan.planSteps != null && cur >= 0 && cur < currentPlan.planSteps.Length) return cur;
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
    /// 当个人计划解析失败时，创建一份保守但可执行的默认计划。
    /// </summary>
    /// <param name="mission">当前任务对象。</param>
    /// <param name="specificRole">协调者已指定的角色；为空时退回 mission.roles 第一个角色或当前角色。</param>
    /// <param name="specificSlot">协调者已指定的槽位；若它是集合覆盖槽位，会优先生成覆盖型默认计划。</param>
    private void CreateDefaultPlan(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)
    {
        // 优先使用已明确分配的角色，否则从任务角色列表或当前角色里找一个保守兜底。
        RoleType fallbackRole = specificRole ?? (mission != null && mission.roles != null && mission.roles.Length > 0 ? mission.roles[0].roleType : agentProperties.Role);

        if (specificSlot != null &&
            specificSlot.targetRef != null &&
            specificSlot.targetRef.memberEntityIds != null &&
            specificSlot.targetRef.memberEntityIds.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            // 对“覆盖一组成员”的槽位，优先生成覆盖型默认计划，而不是普通线性步骤。
            BuildCoveragePlanFromAssignedSlot(
                specificSlot,
                out string[] coverageSteps,
                out string[] coverageActionTypes,
                out string[] coverageNavigationModes,
                out StepIntentDefinition[] coverageIntents,
                out RoutePolicyDefinition[] coveragePolicies);

            if (coverageSteps != null && coverageSteps.Length > 0)
            {
                // 覆盖型默认计划生成成功后，直接落成 currentPlan 并返回。
                currentPlan = new Plan
                {
                    missionId = mission.missionId,
                    mission = mission.missionDescription,
                    missionType = mission.missionType,
                    relationshipType = mission.relationshipType,
                    successCondition = mission.successCondition,
                    failureCondition = mission.failureCondition,
                    navigationPolicy = NavigationPolicy.Auto,
                    agentRole = fallbackRole,
                    planSteps = BuildPlanStepsFromLegacy(coverageSteps, coverageActionTypes, coverageIntents, specificSlot),
                    stepActionTypes = coverageActionTypes,
                    stepNavigationModes = coverageNavigationModes,
                    stepIntents = coverageIntents,
                    stepRoutePolicies = coveragePolicies,
                    coordinationDirectives = mission.coordinationDirectives ?? new TeamCoordinationDirective[0],
                    assignedSlot = specificSlot,
                    currentStep = 0,
                    created = DateTime.Now,
                    priority = Priority.Normal,
                    assignedBy = mission.coordinatorId,
                    commMode = mission.communicationMode
                };
                return;
            }
        }

        // 普通默认计划按角色类型给出一组相对通用的步骤模板。
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

        // 把模板步骤和一套中性 stepIntent / routePolicy 落成 currentPlan。
        string[] defaultActionTypes = BuildFilledArray(defaultSteps.Length, "Move");
        string[] defaultNavigationModes = BuildFilledArray(defaultSteps.Length, "Direct");
        StepIntentDefinition[] defaultIntents = BuildFallbackStepIntents(defaultSteps, defaultActionTypes, specificSlot);
        RoutePolicyDefinition[] defaultPolicies = BuildFallbackRoutePolicies(defaultSteps.Length, specificSlot);
        currentPlan = new Plan
        {
            missionId = mission.missionId,
            mission = mission.missionDescription,
            missionType = mission.missionType,
            relationshipType = mission.relationshipType,
            successCondition = mission.successCondition,
            failureCondition = mission.failureCondition,
            navigationPolicy = NavigationPolicy.Auto,
            agentRole = fallbackRole,
            planSteps = BuildPlanStepsFromLegacy(defaultSteps, defaultActionTypes, defaultIntents, specificSlot),
            stepActionTypes = defaultActionTypes,
            stepNavigationModes = defaultNavigationModes,
            stepIntents = defaultIntents,
            stepRoutePolicies = defaultPolicies,
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
    /// 从用户自然语言任务出发，先生成团队语义，再由代码展开岗位和本地计划。
    /// 当前主路径只有这一段团队语义 LLM，不再回退旧 phasePlan -> taskSlots 两阶段链。
    /// </summary>
    /// <param name="description">用户输入的自然语言任务描述。</param>
    /// <param name="agentCount">本轮任务期望投入的智能体数量。</param>
    public IEnumerator AnalyzeMissionDescription(string description, int agentCount)
    {
        // 进入主规划协程时先置位 busy，防止任务分析期间再次重入。
        planningRequestInFlight = true;
        try
        {
            // 枚举字符串会直接写进团队语义 prompt，用来约束 LLM 的返回值范围。
            string missionTypeOptions = string.Join("|", Enum.GetNames(typeof(MissionType)));
            string relationshipTypeOptions = string.Join("|", Enum.GetNames(typeof(TeamRelationshipType)));
            string roleTypeOptions = string.Join("|", Enum.GetNames(typeof(RoleType)));
            string targetKindHintOptions = string.Join("|", Enum.GetNames(typeof(StructuredTargetMode)));
            // 拼接目标来源、记忆和反思上下文，增强团队语义解析的稳定性。
            string cognitiveContext = BuildPlanningCognitiveContext(description);

            // 新主路径：第一阶段只让 LLM 输出团队语义骨架，不再直接枚举 taskSlots。
            string semanticPrompt =
        $@"第一阶段：把自然语言任务压缩成团队语义骨架，只返回一个 JSON 对象。
        输入:
        - missionDescription: {description}
        - agentCount: {agentCount}

        {cognitiveContext}

        允许的枚举值:
        - missionType: {missionTypeOptions}
        - relationshipType: {relationshipTypeOptions}
        - role: {roleTypeOptions}
        - targetKindHint: {targetKindHintOptions}

        返回 JSON:
        {{
        ""missionType"": ""Patrol"",
        ""relationshipType"": ""Cooperation"",
        ""teamObjective"": ""多机围绕1号教学楼执行协同巡逻"",
        ""successCondition"": ""完成指定巡逻并回传结果"",
        ""failureCondition"": ""超时或关键区域漏检"",
        ""roleRequirements"": [
            {{
            ""role"": ""Scout"",
            ""count"": 3,
            ""responsibility"": ""沿教学楼外围巡逻并观察异常"",
            ""targetText"": ""1号教学楼"",
            ""targetKindHint"": ""Entity"",
            ""viaTargets"": [],
            ""completionCondition"": ""完成一周巡逻并上报"",
            ""phaseIds"": [""patrol""]
            }}
        ],
        ""phaseTemplates"": [
            {{
            ""phaseId"": ""patrol"",
            ""objective"": ""完成巡逻"",
            ""dependsOn"": []
            }}
        ],
        ""coordinationRules"": [
            {{
            ""ruleType"": ""BroadcastOnEvent"",
            ""trigger"": ""anomaly_detected"",
            ""effect"": ""notify_team"",
            ""sharedTarget"": ""1号教学楼"",
            ""participants"": [""Scout""],
            ""notes"": ""发现异常立即广播""
            }}
        ]
        }}

        要求:
        1. roleRequirements 必须非空，count 总数尽量等于 {agentCount}。
        2. targetText 保留自然语言目标，不要臆造地图内部 token、entityId 或 collectionKey。
        3. 只描述团队语义、角色需求、阶段模板和协同规则。
        4. 不要输出 taskSlots，不要输出 stepActionTypes、stepIntents、动作序列。
        5. 只能返回一个 JSON 对象。";

            string semanticResultRaw = string.Empty;
            yield return llmInterface.SendRequest(semanticPrompt, result =>
            {
                semanticResultRaw = result ?? string.Empty;
            }, temperature: 0.1f, maxTokens: 1400);

            Debug.Log($"[Planning] 团队语义阶段 LLM 原始返回:\n{semanticResultRaw}");

            if (TryParseMissionSemanticResponse(semanticResultRaw, out MissionSemanticResponse semantic, out string semanticError))
            {
                try
                {
                    MissionAssignment semanticMission = BuildMissionFromSemantic(semantic, description, agentCount);
                    ApplyMissionSourceContext(semanticMission);
                    Debug.Log($"[Planning] 团队语义解析摘要: missionType={semanticMission.missionType}, relationship={semanticMission.relationshipType}, roleCount={(semanticMission.roles != null ? semanticMission.roles.Length : 0)}, slotCount={(semanticMission.taskSlots != null ? semanticMission.taskSlots.Length : 0)}");

                    Debug.Log($"{agentProperties.AgentID} 成为任务协调者");
                    semanticMission.coordinatorId = agentProperties.AgentID;
                    DistributeMissionToAgents(semanticMission);
                    ReceiveMissionAssignment(semanticMission);
                    yield break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"团队语义主路径落地失败，退回默认任务: {ex.Message}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(semanticError))
            {
                Debug.LogError($"团队语义解析失败，退回默认任务: {semanticError}");
            }
            CreateDefaultMission(description, agentCount);
            yield break;
        }
        finally
        {
            // 主规划协程结束时必须清掉 busy 标记。
            planningRequestInFlight = false;
        }
    }

    /// <summary>
    /// 对 phase plan 做执行前规范化：
    /// - missionType / commMode 缺失回退默认值；
    /// - phaseId 唯一化；
    /// - 依赖关系清洗；
    /// - agentBudget 范围规范到 [1, agentCount]。
    /// </summary>
    /// <summary>
    /// 从 LLM 响应提取每步动作意图（由 LLM 明确给出，不做自然语言关键词判断）。
    /// </summary>
    /// <param name="response">个人计划阶段的原始响应文本。</param>
    /// <param name="stepCount">当前计划应有的步骤数，用于把动作数组补齐到同样长度。</param>
    private string[] ExtractStepActionTypesFromResponse(string response, int stepCount)
    {
        // 先把目标长度裁到非负。
        int n = Mathf.Max(0, stepCount);
        // 缺失时默认每一步都是 Move。
        string[] fallback = BuildFilledArray(n, "Move");
        if (n == 0) return fallback;

        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _))
            {
                if (planResponse != null && planResponse.stepActionTypes != null && planResponse.stepActionTypes.Length > 0)
                {
                    // 读取 LLM 返回的原始动作标签数组。
                    string[] raw = planResponse.stepActionTypes;
                    // 重新构造一个长度与 steps 一致的结果数组。
                    string[] result = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        // 如果 LLM 返回长度不足，就复用最后一个标签补齐。
                        string token = i < raw.Length ? raw[i] : raw[raw.Length - 1];
                        // 每个 token 都做一次标准化，约束到 Move/Observe/Communicate/Interact/Idle。
                        result[i] = NormalizeStepActionTypeToken(token);
                    }
                    return result;
                }
            }
        }
        // 动作标签解析失败时静默回退到默认值，不让上层再抛异常。
        catch { }

        return fallback;
    }

    /// <summary>
    /// 从 LLM 响应提取每步导航模式（由 LLM 明确给出，不做自然语言关键词判断）。
    /// </summary>
    /// <param name="response">个人计划阶段的原始响应文本。</param>
    /// <param name="stepCount">当前计划应有的步骤数，用于把导航模式数组补齐到同样长度。</param>
    private string[] ExtractStepNavigationModesFromResponse(string response, int stepCount)
    {
        // 先把目标长度裁到非负。
        int n = Mathf.Max(0, stepCount);
        // 缺失时默认每一步都是 Direct。
        string[] fallback = BuildFilledArray(n, "Direct");
        if (n == 0) return fallback;

        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _))
            {
                if (planResponse != null && planResponse.stepNavigationModes != null && planResponse.stepNavigationModes.Length > 0)
                {
                    // 读取 LLM 返回的原始导航模式数组。
                    string[] raw = planResponse.stepNavigationModes;
                    // 重新构造一个长度与 steps 一致的结果数组。
                    string[] result = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        // 如果 LLM 返回长度不足，就复用最后一个导航模式补齐。
                        string token = i < raw.Length ? raw[i] : raw[raw.Length - 1];
                        // 每个 token 都做一次标准化，约束到 AStar/Direct/None。
                        result[i] = NormalizeStepNavigationModeToken(token);
                    }
                    return result;
                }
            }
        }
        // 导航模式解析失败时静默回退到默认值。
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

        StructuredTargetReference slotTargetRef = NormalizeStructuredTargetReference(
            assignedSlot != null ? assignedSlot.targetRef : null,
            assignedSlot != null ? assignedSlot.target : string.Empty);
        StructuredTargetReference[] slotViaTargetRefs = NormalizeStructuredTargetReferenceArray(
            assignedSlot != null ? assignedSlot.viaTargetRefs : null,
            assignedSlot != null ? assignedSlot.viaTargets : null);

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
                primaryTarget = !string.IsNullOrWhiteSpace(ResolveStructuredTargetDisplayText(slotTargetRef))
                    ? ResolveStructuredTargetDisplayText(slotTargetRef)
                    : "none",
                primaryTargetRef = slotTargetRef,
                orderedViaTargets = ResolveStructuredTargetDisplayTexts(slotViaTargetRefs, assignedSlot != null ? assignedSlot.viaTargets : null),
                orderedViaTargetRefs = slotViaTargetRefs,
                requestedTeammateIds = new string[0],
                observationFocus = "none",
                communicationGoal = "none",
                finalBehavior = !string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.finalBehavior : null) ? assignedSlot.finalBehavior : "arrive",
                completionCondition = !string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.completionCondition : null) ? assignedSlot.completionCondition : stepText
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
    /// 2) orderedViaTargets 为空时，可按需用 assignedSlot.viaTargets 补。
    /// 它做的是“把已知目标补回来”，不是“重新猜一个新目标”。
    /// </summary>
    private StepIntentDefinition NormalizeStepIntent(
        StepIntentDefinition src,
        string stepText,
        string actionTypeHint,
        MissionTaskSlot assignedSlot,
        bool allowViaFallbackFromAssignedSlot = true)
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
        // 这里是“字符串目标 -> 结构化目标引用”的关键过渡点。
        // 无论 LLM 这次有没有返回 primaryTargetRef，系统都会把 primaryTarget 包成正式目标对象，
        // 避免后面的模块继续只拿一段字符串工作。
        result.primaryTargetRef = NormalizeStructuredTargetReference(
            result.primaryTargetRef,
            result.primaryTarget,
            StructuredTargetMode.Unknown,
            StructuredTargetCardinality.One);
        if (!HasUsableStructuredTarget(result.primaryTargetRef) && assignedSlot != null)
        {
            result.primaryTargetRef = NormalizeStructuredTargetReference(
                assignedSlot.targetRef,
                assignedSlot.target,
                StructuredTargetMode.Unknown,
                StructuredTargetCardinality.One);
        }
        result.primaryTarget = !string.IsNullOrWhiteSpace(ResolveStructuredTargetDisplayText(result.primaryTargetRef, result.primaryTarget))
            ? ResolveStructuredTargetDisplayText(result.primaryTargetRef, result.primaryTarget)
            : "none";

        // 关键修复：
        // 旧逻辑只在 orderedViaTargets == null 时才回填 assignedSlot.viaTargets。
        // 但很多模型会返回空数组 []，这会把槽位里明确指定的检查点直接吞掉。
        // 现在解析计划阶段仍允许做这类回填；但运行时若当前 step 已经被 repair 明确清空 via，
        // 则必须禁止再次从 assignedSlot 回填，否则执行层会回头重走已经完成的检查点。
        result.orderedViaTargetRefs = NormalizeStructuredTargetReferenceArray(result.orderedViaTargetRefs, result.orderedViaTargets);
        if (allowViaFallbackFromAssignedSlot &&
            (result.orderedViaTargets == null || result.orderedViaTargets.Length == 0) &&
            (result.orderedViaTargetRefs == null || result.orderedViaTargetRefs.Length == 0))
        {
            result.orderedViaTargetRefs = NormalizeStructuredTargetReferenceArray(
                assignedSlot != null ? assignedSlot.viaTargetRefs : null,
                assignedSlot != null ? assignedSlot.viaTargets : null);
        }
        string[] viaDisplayFallbackTexts = allowViaFallbackFromAssignedSlot
            ? (assignedSlot != null ? assignedSlot.viaTargets : null)
            : result.orderedViaTargets;
        result.orderedViaTargets = ResolveStructuredTargetDisplayTexts(
            result.orderedViaTargetRefs,
            viaDisplayFallbackTexts);
        result.requestedTeammateIds = result.requestedTeammateIds ?? new string[0];
        result.observationFocus = string.IsNullOrWhiteSpace(result.observationFocus) ? "none" : result.observationFocus;
        result.communicationGoal = string.IsNullOrWhiteSpace(result.communicationGoal) ? "none" : result.communicationGoal;
        result.finalBehavior = string.IsNullOrWhiteSpace(result.finalBehavior)
            ? (!string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.finalBehavior : null) ? assignedSlot.finalBehavior : "arrive")
            : result.finalBehavior;
        result.completionCondition = string.IsNullOrWhiteSpace(result.completionCondition)
            ? (!string.IsNullOrWhiteSpace(assignedSlot != null ? assignedSlot.completionCondition : null) ? assignedSlot.completionCondition : result.stepText)
            : result.completionCondition;
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
        StructuredTargetReference finalTargetRef = NormalizeStructuredTargetReference(
            assignedSlot.targetRef,
            assignedSlot.target,
            StructuredTargetMode.Unknown,
            StructuredTargetCardinality.One);
        string[] viaTargets = assignedSlot.viaTargets ?? new string[0];
        StructuredTargetReference[] viaTargetRefs = NormalizeStructuredTargetReferenceArray(assignedSlot.viaTargetRefs, viaTargets);
        HashSet<string> viaSet = new HashSet<string>(
            ResolveStructuredTargetQueries(viaTargetRefs, viaTargets)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim()),
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
            current.primaryTargetRef = NormalizeStructuredTargetReference(current.primaryTargetRef, current.primaryTarget);
            current.orderedViaTargetRefs = NormalizeStructuredTargetReferenceArray(current.orderedViaTargetRefs, current.orderedViaTargets);
            string actionType = stepActionTypes != null && i < stepActionTypes.Length ? stepActionTypes[i] : "Idle";
            string currentTarget = ResolveStructuredTargetQuery(current.primaryTargetRef, current.primaryTarget);
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
                current.primaryTargetRef = finalTargetRef;
            }

            // 规则2：
            // 最后一个移动步骤如果仍然只朝向 viaTargets，也要拉回最终目标。
            // 否则路径会停在检查点，不再推进到真正目标。
            if (isMove && isLastMove && (string.IsNullOrWhiteSpace(currentTarget) || targetIsVia))
            {
                current.primaryTarget = finalTarget;
                current.primaryTargetRef = finalTargetRef;
            }

            // 规则3：
            // 最后一个有效步骤如果 target 为空，也统一补成最终目标，保证末段动作有明确作用对象。
            if (isLastMeaningful && string.IsNullOrWhiteSpace(current.primaryTarget))
            {
                current.primaryTarget = finalTarget;
                current.primaryTargetRef = finalTargetRef;
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
                    current.orderedViaTargetRefs = viaTargetRefs;
                }
                else
                {
                    current.orderedViaTargets = new string[0];
                    current.orderedViaTargetRefs = new StructuredTargetReference[0];
                }
            }

            // 纠偏完成后，再把结构化目标重新同步回旧字符串字段。
            // 这样旧链路还能跑，但真正的“单一真相源”已经变成了 primaryTargetRef / orderedViaTargetRefs。
            current.primaryTarget = ResolveStructuredTargetDisplayText(current.primaryTargetRef, current.primaryTarget);
            current.orderedViaTargets = ResolveStructuredTargetDisplayTexts(current.orderedViaTargetRefs, current.orderedViaTargets);

            intents[i] = current;
        }

        return intents;
    }

    private RoutePolicyDefinition NormalizeRoutePolicy(RoutePolicyDefinition src)
    {
        RoutePolicyDefinition result = src ?? new RoutePolicyDefinition();
        if (src == null)
        {
            result.altitudeMode = RouteAltitudeMode.Default;
            result.clearance = RouteClearancePreference.Medium;
            result.allowGlobalAStar = true;
            result.allowLocalDetour = true;
            result.blockedPolicy = BlockedPolicyType.Replan;
        }
        result.avoidNodeTypes = result.avoidNodeTypes ?? new SmallNodeType[0];
        result.avoidFeatureNames = result.avoidFeatureNames ?? new string[0];
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

        if (result.altitudeMode == RouteAltitudeMode.Default && assignedSlot.altitudeMode != RouteAltitudeMode.Default)
        {
            result.altitudeMode = assignedSlot.altitudeMode;
        }

        return result;
    }

    private TeamCoordinationDirective NormalizeCoordinationDirective(TeamCoordinationDirective src)
    {
        TeamCoordinationDirective result = src ?? new TeamCoordinationDirective();
        result.sharedTargetRef = NormalizeStructuredTargetReference(
            result.sharedTargetRef,
            result.sharedTarget,
            StructuredTargetMode.Unknown,
            StructuredTargetCardinality.One);
        result.sharedTarget = ResolveStructuredTargetDisplayText(result.sharedTargetRef, result.sharedTarget);
        result.yieldToAgentIds = result.yieldToAgentIds ?? new string[0];
        result.syncPointTargets = result.syncPointTargets ?? new string[0];
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
    /// 读取单个结构化目标对象。
    /// 只要 JSON 里已经给出 targetRef / primaryTargetRef / sharedTargetRef，
    /// 这里就直接按结构读取，不再把它压扁成字符串。
    /// </summary>
    private static StructuredTargetReference ReadStructuredTargetField(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return null;

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!(obj[key] is JObject targetObj)) continue;

            StructuredTargetReference targetRef = targetObj.ToObject<StructuredTargetReference>();
            if (targetRef != null) return targetRef;
        }

        return null;
    }

    /// <summary>
    /// 读取结构化目标数组，例如 viaTargetRefs。
    /// 它的作用是把“一串经过点对象”保留下来，而不是只留下名字列表。
    /// </summary>
    private static StructuredTargetReference[] ReadStructuredTargetArrayField(JObject obj, params string[] keys)
    {
        if (obj == null || keys == null) return new StructuredTargetReference[0];

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!(obj[key] is JArray arr)) continue;

            List<StructuredTargetReference> result = new List<StructuredTargetReference>();
            for (int j = 0; j < arr.Count; j++)
            {
                if (arr[j] is JObject targetObj)
                {
                    StructuredTargetReference targetRef = targetObj.ToObject<StructuredTargetReference>();
                    if (targetRef != null) result.Add(targetRef);
                }
            }

            return result.ToArray();
        }

        return new StructuredTargetReference[0];
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

                    // 先手动补一次 planSteps。
                    // 这样即使自动反序列化因为 LLM 格式细节没成功命中，我们也尽量把主路径字段救回来。
                    if ((planResponse.planSteps == null || planResponse.planSteps.Length == 0) &&
                        TryExtractPlanStepsFromToken(obj["planSteps"], out PlanStepDefinition[] parsedPlanSteps))
                    {
                        planResponse.planSteps = parsedPlanSteps;
                    }

                    if (string.IsNullOrWhiteSpace(planResponse.assignedRole))
                    {
                        // 兼容 role 字段命名差异
                        planResponse.assignedRole = (string)obj["assignedRole"] ?? (string)obj["role"] ?? string.Empty;
                    }

                    if (planResponse.steps == null || planResponse.steps.Length == 0)
                    {
                        // 当前主路径不再把 planSteps 对象数组压扁成旧 steps。
                        // 这里只兼容“旧格式直接返回字符串 steps 数组”的最小情况。
                        JToken token = obj["steps"] ?? obj["actions"];
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
    /// 从任意 JSON token 中手动提取 planSteps。
    /// 这里专门处理 planSteps 数组里每个对象的字段，不依赖一次性反序列化必须完全成功。
    /// </summary>
    /// <param name="token">通常来自 JObject["planSteps"] 的 JSON token。</param>
    /// <param name="planSteps">输出解析出的 planStep 数组。</param>
    /// <returns>成功提取到至少一条有效步骤时返回 true。</returns>
    private bool TryExtractPlanStepsFromToken(JToken token, out PlanStepDefinition[] planSteps)
    {
        // 默认先给空结果，避免调用方拿到未初始化数组。
        planSteps = null;
        // 缺少 token 或 token 不是数组时，直接失败。
        if (!(token is JArray arr) || arr.Count == 0) return false;

        // 收集最终成功提取出的 planStep。
        List<PlanStepDefinition> list = new List<PlanStepDefinition>();
        for (int i = 0; i < arr.Count; i++)
        {
            // 当前数组元素应当是一个步骤对象。
            JToken item = arr[i];
            if (!(item is JObject stepObj)) continue;

            // 逐字段读取步骤内容；缺失字段后面会再交给 NormalizePlanStepDefinition 补默认值。
            PlanStepDefinition step = new PlanStepDefinition
            {
                stepId = ((string)stepObj["stepId"] ?? $"step_{i + 1}").Trim(),
                text = ((string)stepObj["text"] ?? string.Empty).Trim(),
                targetText = ((string)stepObj["targetText"] ?? string.Empty).Trim(),
                targetKindHint = ((string)stepObj["targetKindHint"] ?? string.Empty).Trim(),
                relationHint = ((string)stepObj["relationHint"] ?? string.Empty).Trim(),
                completionCondition = ((string)stepObj["completionCondition"] ?? string.Empty).Trim(),
                viaTargets = ExtractStringArrayFromToken(stepObj["viaTargets"])
            };

            // 至少文本、目标、完成条件里有一项非空时，才认为这是有效步骤。
            if (!string.IsNullOrWhiteSpace(step.text) ||
                !string.IsNullOrWhiteSpace(step.targetText) ||
                !string.IsNullOrWhiteSpace(step.completionCondition))
            {
                list.Add(step);
            }
        }

        // 一个有效步骤都没有时仍视为失败。
        if (list.Count == 0) return false;
        planSteps = list.ToArray();
        return true;
    }

    /// <summary>
    /// 从 JSON token 中提取字符串数组。
    /// 这里只做轻量提取，具体空值修复交给上层规范化。
    /// </summary>
    /// <param name="token">待读取的 JSON token。</param>
    /// <returns>提取出的字符串数组；没有有效元素时返回空数组。</returns>
    private static string[] ExtractStringArrayFromToken(JToken token)
    {
        // 缺少 token 或 token 不是数组时，统一返回空数组。
        if (!(token is JArray arr) || arr.Count == 0) return Array.Empty<string>();

        // 收集数组里所有非空字符串。
        List<string> list = new List<string>();
        for (int i = 0; i < arr.Count; i++)
        {
            string value = arr[i]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value.Trim());
            }
        }

        return list.Count > 0 ? list.ToArray() : Array.Empty<string>();
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

        // 旧容错解析只再兼容字符串 steps，不再去 planSteps 对象数组里硬抠字符串。
        bool gotSteps = TryExtractStringArrayFieldLoose(raw, "steps", out string[] steps);

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
    /// 对单个任务槽位做合法化补全。
    /// 这里只补字段默认值，不解释自然语言，不重写槽位语义。
    /// </summary>
    private MissionTaskSlot NormalizeMissionTaskSlot(MissionTaskSlot slot, string missionDescription, int index)
    {
        MissionTaskSlot result = slot ?? new MissionTaskSlot();
        result.slotId = string.IsNullOrWhiteSpace(result.slotId) ? $"slot_{index + 1}" : result.slotId;
        result.slotLabel = string.IsNullOrWhiteSpace(result.slotLabel) ? result.slotId : result.slotLabel;
        result.target = string.IsNullOrWhiteSpace(result.target) ? missionDescription : result.target;
        result.targetRef = NormalizeStructuredTargetReference(
            result.targetRef,
            result.target,
            StructuredTargetMode.Unknown,
            StructuredTargetCardinality.One);
        result.target = ResolveStructuredTargetDisplayText(result.targetRef, result.target);
        result.viaTargets = result.viaTargets ?? new string[0];
        result.viaTargetRefs = NormalizeStructuredTargetReferenceArray(result.viaTargetRefs, result.viaTargets);
        result.viaTargets = ResolveStructuredTargetDisplayTexts(result.viaTargetRefs, result.viaTargets);
        result.syncGroup = string.IsNullOrWhiteSpace(result.syncGroup) ? "mission_default_group" : result.syncGroup;
        result.dependsOnSlotIds = result.dependsOnSlotIds ?? new string[0];
        result.finalBehavior = string.IsNullOrWhiteSpace(result.finalBehavior) ? "arrive" : result.finalBehavior;
        result.completionCondition = string.IsNullOrWhiteSpace(result.completionCondition) ? $"完成槽位 {result.slotLabel}" : result.completionCondition;
        result.notes = string.IsNullOrWhiteSpace(result.notes) ? "normalized_from_llm_or_role_expansion" : result.notes;
        return result;
    }

    /// <summary>
    /// 懒加载 CampusGrid2D。
    /// Grounding 必须依赖统一世界索引，因此这里集中做一次引用同步。
    /// </summary>
    private void EnsureCampusGridReference()
    {
        if (campusGrid == null)
        {
            campusGrid = FindObjectOfType<CampusGrid2D>();
        }
    }

    /// <summary>
    /// 按“更像用户原话”的优先级，收集一组用于世界匹配的候选文本。
    /// 这里先看 displayName / rawText / selectorText，
    /// 只有这些都不够时，才退回 entityId / executableQuery 这类更像系统锚点的字段。
    /// </summary>
    private static string[] BuildGroundingCandidateTexts(StructuredTargetReference targetRef, string fallbackText)
    {
        List<string> result = new List<string>();
        AppendGroundingCandidate(result, targetRef != null ? targetRef.displayName : null);
        AppendGroundingCandidate(result, targetRef != null ? targetRef.rawText : null);
        AppendGroundingCandidate(result, targetRef != null ? targetRef.selectorText : null);
        AppendGroundingCandidate(result, targetRef != null ? targetRef.areaHint : null);
        AppendGroundingCandidate(result, fallbackText);
        AppendGroundingCandidate(result, targetRef != null ? targetRef.anchorText : null);
        AppendGroundingCandidate(result, targetRef != null ? targetRef.entityId : null);
        AppendGroundingCandidate(result, targetRef != null ? targetRef.executableQuery : null);
        return result.ToArray();
    }

    private static void AppendGroundingCandidate(List<string> values, string text)
    {
        if (values == null || string.IsNullOrWhiteSpace(text)) return;
        string normalized = text.Trim();
        if (values.Any(v => string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase))) return;
        values.Add(normalized);
    }

    /// <summary>
    /// 把一个“结构化目标语义”真正落到世界模型上。
    ///
    /// 职责边界：
    /// 1) LLM 只负责说“用户想指谁”；
    /// 2) 这里负责回答“这个目标在当前地图里具体是谁”；
    /// 3) ActionDecision 之后只消费这里已经落好的 grounded 结果。
    ///
    /// 注意这里不会反向污染 displayName / rawText，
    /// 因为这些字段要尽量保留人类可读的自然语言目标。
    /// </summary>
    private StructuredTargetReference GroundStructuredTargetReferenceAgainstWorld(
        StructuredTargetReference targetRef,
        string fallbackText,
        StructuredTargetMode fallbackMode = StructuredTargetMode.Unknown,
        StructuredTargetCardinality fallbackCardinality = StructuredTargetCardinality.One)
    {
        StructuredTargetReference result = NormalizeStructuredTargetReference(targetRef, fallbackText, fallbackMode, fallbackCardinality);
        EnsureCampusGridReference();

        // 已经是系统明确切好的集合子集时，不再重新扩回“全量集合”。
        // 只要把执行锚点补成第一个成员即可，保持一人一子集的分配结果不被覆盖。
        if (result.mode == StructuredTargetMode.Collection &&
            !string.IsNullOrWhiteSpace(result.collectionKey) &&
            result.memberEntityIds != null &&
            result.memberEntityIds.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (string.IsNullOrWhiteSpace(result.entityClass))
            {
                result.entityClass = "CampusFeature";
            }
            if (string.IsNullOrWhiteSpace(result.executableQuery))
            {
                result.executableQuery = result.memberEntityIds.First(v => !string.IsNullOrWhiteSpace(v)).Trim();
            }
            if (string.IsNullOrWhiteSpace(result.anchorText))
            {
                result.anchorText = result.executableQuery;
            }
            result.notes = AppendPlanningNote(result.notes, $"grounded_collection_subset={result.collectionKey}");
            return result;
        }

        // 世界点 / 自身 / 队友目标更偏执行域，不需要强行映射 CampusGrid。
        // 这里只做最小补齐，让它们继续往 ActionDecision 传。
        if (result.mode == StructuredTargetMode.WorldPoint ||
            result.mode == StructuredTargetMode.Self ||
            result.mode == StructuredTargetMode.Agent)
        {
            if (string.IsNullOrWhiteSpace(result.executableQuery))
            {
                result.executableQuery = ResolveStructuredTargetDisplayText(result, fallbackText);
            }
            if (string.IsNullOrWhiteSpace(result.anchorText))
            {
                result.anchorText = result.executableQuery;
            }
            return result;
        }

        bool prefersCollection = result.mode == StructuredTargetMode.Collection ||
                                 result.cardinality == StructuredTargetCardinality.All ||
                                 result.cardinality == StructuredTargetCardinality.Subset;

        if (prefersCollection &&
            TryGroundCollectionTargetReference(result, fallbackText, out StructuredTargetReference groundedCollection))
        {
            return groundedCollection;
        }

        if (!prefersCollection &&
            TryGroundSingleCampusFeatureReference(result, fallbackText, out StructuredTargetReference groundedEntity))
        {
            return groundedEntity;
        }

        // 对 mode=Unknown 的目标，再给集合一次机会。
        // 这允许 LLM 只说“所有building”，但还没明确标成 Collection 时，系统仍能靠世界模型兜住。
        if (!prefersCollection &&
            TryGroundCollectionTargetReference(result, fallbackText, out groundedCollection))
        {
            return groundedCollection;
        }

        // 最后兜底：如果系统这次还没完全落地，也至少把自然语言目标往下传。
        // 这样 ActionDecision 还能继续尝试用世界索引/感知层做一次直接解析。
        if (string.IsNullOrWhiteSpace(result.executableQuery))
        {
            result.executableQuery = ResolveStructuredTargetDisplayText(result, fallbackText);
        }
        if (string.IsNullOrWhiteSpace(result.anchorText))
        {
            result.anchorText = result.executableQuery;
        }

        return result;
    }

    /// <summary>
    /// 对结构化目标数组逐个做 grounded。
    /// 主要给 viaTargetRefs 这类“多个检查点”场景用。
    /// </summary>
    private StructuredTargetReference[] GroundStructuredTargetReferenceArrayAgainstWorld(
        StructuredTargetReference[] refs,
        string[] fallbackTexts,
        StructuredTargetMode fallbackMode = StructuredTargetMode.Unknown)
    {
        List<StructuredTargetReference> result = new List<StructuredTargetReference>();
        int max = Mathf.Max(refs != null ? refs.Length : 0, fallbackTexts != null ? fallbackTexts.Length : 0);
        for (int i = 0; i < max; i++)
        {
            StructuredTargetReference src = refs != null && i < refs.Length ? refs[i] : null;
            string fallback = fallbackTexts != null && i < fallbackTexts.Length ? fallbackTexts[i] : string.Empty;
            StructuredTargetReference grounded = GroundStructuredTargetReferenceAgainstWorld(src, fallback, fallbackMode, StructuredTargetCardinality.One);
            if (HasUsableStructuredTarget(grounded, fallback))
            {
                result.Add(grounded);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// 让单个槽位的 targetRef / viaTargetRefs 真正落到世界实体。
    /// 这一步之后：
    /// - target 继续保留人类可读文本；
    /// - targetRef 里会补齐 entityId / executableQuery / collectionKey 等系统字段。
    /// </summary>
    private void GroundMissionTaskSlotInPlace(MissionTaskSlot slot, string fallbackText)
    {
        if (slot == null) return;

        slot.targetRef = GroundStructuredTargetReferenceAgainstWorld(
            slot.targetRef,
            !string.IsNullOrWhiteSpace(slot.target) ? slot.target : fallbackText,
            StructuredTargetMode.Unknown,
            StructuredTargetCardinality.One);
        slot.target = ResolveStructuredTargetDisplayText(slot.targetRef, slot.target);

        slot.viaTargetRefs = GroundStructuredTargetReferenceArrayAgainstWorld(
            slot.viaTargetRefs,
            slot.viaTargets,
            StructuredTargetMode.Unknown);
        slot.viaTargets = ResolveStructuredTargetDisplayTexts(slot.viaTargetRefs, slot.viaTargets);
    }

    /// <summary>
    /// 让协同指令里的共享目标也走同一条 grounded 链。
    /// 这样任务级 sharedTarget 和 step/slot 目标不会各自一套规则。
    /// </summary>
    private void GroundCoordinationDirectivesInPlace(TeamCoordinationDirective[] directives, string fallbackText)
    {
        if (directives == null) return;

        for (int i = 0; i < directives.Length; i++)
        {
            TeamCoordinationDirective directive = directives[i];
            if (directive == null) continue;

            directive.sharedTargetRef = GroundStructuredTargetReferenceAgainstWorld(
                directive.sharedTargetRef,
                !string.IsNullOrWhiteSpace(directive.sharedTarget) ? directive.sharedTarget : fallbackText,
                StructuredTargetMode.Unknown,
                StructuredTargetCardinality.One);
            directive.sharedTarget = ResolveStructuredTargetDisplayText(directive.sharedTargetRef, directive.sharedTarget);
        }
    }

    /// <summary>
    /// 让个人计划里的每一步也接上 grounded 结果。
    /// 这样 PlanningModule 输出的 stepIntents，进入 ActionDecision 时就已经是：
    /// “人类可读目标 + 系统可执行锚点”同时具备的状态。
    /// </summary>
    private void GroundStepIntentsInPlace(StepIntentDefinition[] stepIntents, MissionTaskSlot assignedSlot)
    {
        if (stepIntents == null) return;

        for (int i = 0; i < stepIntents.Length; i++)
        {
            StepIntentDefinition intent = stepIntents[i];
            if (intent == null) continue;

            intent.primaryTargetRef = GroundStructuredTargetReferenceAgainstWorld(
                intent.primaryTargetRef,
                intent.primaryTarget,
                StructuredTargetMode.Unknown,
                StructuredTargetCardinality.One);
            intent.primaryTarget = ResolveStructuredTargetDisplayText(intent.primaryTargetRef, intent.primaryTarget);

            intent.orderedViaTargetRefs = GroundStructuredTargetReferenceArrayAgainstWorld(
                intent.orderedViaTargetRefs,
                intent.orderedViaTargets,
                StructuredTargetMode.Unknown);
            intent.orderedViaTargets = ResolveStructuredTargetDisplayTexts(intent.orderedViaTargetRefs, intent.orderedViaTargets);

            if (!HasUsableStructuredTarget(intent.primaryTargetRef) && assignedSlot != null && assignedSlot.targetRef != null)
            {
                intent.primaryTargetRef = assignedSlot.targetRef;
                intent.primaryTarget = ResolveStructuredTargetDisplayText(intent.primaryTargetRef, assignedSlot.target);
            }
        }
    }

    /// <summary>
    /// 尝试把一个自然语言单体目标落到 CampusGrid2D 的真实大节点实体。
    /// 例如：
    /// - “1号教学楼” -> uid=..., executableQuery=uid
    /// - “building_86” -> 同样回到真实 profile，再回写自然显示名
    /// </summary>
    private bool TryGroundSingleCampusFeatureReference(
        StructuredTargetReference targetRef,
        string fallbackText,
        out StructuredTargetReference grounded)
    {
        grounded = null;
        EnsureCampusGridReference();
        if (campusGrid == null) return false;

        string[] candidates = BuildGroundingCandidateTexts(targetRef, fallbackText);
        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            if (!campusGrid.TryResolveFeatureSpatialProfile(candidate, transform.position, out CampusGrid2D.FeatureSpatialProfile profile, preferWalkableApproach: true, ignoreCase: true) ||
                profile == null)
            {
                continue;
            }

            grounded = NormalizeStructuredTargetReference(
                targetRef,
                fallbackText,
                StructuredTargetMode.Entity,
                StructuredTargetCardinality.One);

            grounded.mode = grounded.mode == StructuredTargetMode.Unknown ? StructuredTargetMode.Entity : grounded.mode;
            grounded.cardinality = grounded.cardinality == StructuredTargetCardinality.Unspecified
                ? StructuredTargetCardinality.One
                : grounded.cardinality;
            grounded.entityClass = string.IsNullOrWhiteSpace(grounded.entityClass) ? "CampusFeature" : grounded.entityClass;
            grounded.entityId = !string.IsNullOrWhiteSpace(profile.uid)
                ? profile.uid.Trim()
                : SelectCoverageMemberToken(profile);
            grounded.executableQuery = grounded.entityId;
            grounded.anchorText = grounded.entityId;
            grounded.collectionKey = string.IsNullOrWhiteSpace(grounded.collectionKey)
                ? (profile.collectionKey ?? string.Empty)
                : grounded.collectionKey;

            // displayName 尽量保留“人类叫法”。
            // 如果地图里有真实名字，就优先回写真实名字；否则保留原自然语义。
            string naturalDisplay = ResolveStructuredTargetDisplayText(targetRef, fallbackText);
            bool profileHasSpecificName =
                !string.IsNullOrWhiteSpace(profile.name) &&
                !string.Equals(profile.name.Trim(), profile.kind ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            grounded.displayName = profileHasSpecificName
                ? profile.name.Trim()
                : (!string.IsNullOrWhiteSpace(naturalDisplay) ? naturalDisplay : ResolveStructuredTargetDisplayText(grounded, fallbackText));
            if (string.IsNullOrWhiteSpace(grounded.rawText))
            {
                grounded.rawText = candidate;
            }

            grounded.notes = AppendPlanningNote(grounded.notes, $"grounded_feature={grounded.entityId}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 尝试把“所有building / 全部parking / 某类大节点集合”落到地图真实 collection。
    /// 成功后会补 collectionKey、memberEntityIds 和当前执行锚点。
    /// </summary>
    private bool TryGroundCollectionTargetReference(
        StructuredTargetReference targetRef,
        string fallbackText,
        out StructuredTargetReference grounded)
    {
        grounded = null;
        EnsureCampusGridReference();
        if (campusGrid == null) return false;

        string[] candidates = BuildGroundingCandidateTexts(targetRef, fallbackText);
        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            if (!campusGrid.TryResolveFeatureCollectionBySelector(candidate, out string collectionKey, out CampusGrid2D.FeatureSpatialProfile[] members) ||
                members == null ||
                members.Length == 0)
            {
                continue;
            }

            grounded = NormalizeStructuredTargetReference(
                targetRef,
                fallbackText,
                StructuredTargetMode.Collection,
                StructuredTargetCardinality.All);

            grounded.mode = StructuredTargetMode.Collection;
            grounded.cardinality = grounded.cardinality == StructuredTargetCardinality.Unspecified
                ? StructuredTargetCardinality.All
                : grounded.cardinality;
            grounded.entityClass = string.IsNullOrWhiteSpace(grounded.entityClass) ? "CampusFeature" : grounded.entityClass;
            grounded.collectionKey = collectionKey;
            grounded.selectorText = string.IsNullOrWhiteSpace(grounded.selectorText) ? candidate : grounded.selectorText;
            grounded.displayName = string.IsNullOrWhiteSpace(grounded.displayName) ? candidate : grounded.displayName;
            grounded.memberEntityIds = members
                .Where(p => p != null)
                .Select(SelectCoverageMemberToken)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string anchor = grounded.memberEntityIds.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
            grounded.executableQuery = anchor;
            grounded.anchorText = anchor;
            grounded.notes = AppendPlanningNote(grounded.notes, $"grounded_collection={collectionKey}:{grounded.memberEntityIds.Length}");
            return true;
        }

        return false;
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

        for (int i = 0; i < slots.Count; i++)
        {
            GroundMissionTaskSlotInPlace(slots[i], missionDescription);
        }

        RepairCollectionCoverageSlots(slots);

        return slots.ToArray();
    }

    /// <summary>
    /// 对“集合覆盖类槽位”做一次地图驱动的修复。
    /// 这里不靠词表猜 building/forest，而是只看：
    /// 1) targetRef.mode 是否是 Collection；
    /// 2) collectionKey 是否来自地图目录；
    /// 3) 当前多个槽位是否把同一组成员分重复了。
    ///
    /// 如果 LLM 已经分得很好，这里什么都不改；
    /// 如果 LLM 只给了“所有某类目标”但没切子集，这里就按真实地图成员做一次无冲突划分。
    /// </summary>
    private void RepairCollectionCoverageSlots(List<MissionTaskSlot> slots)
    {
        if (slots == null || slots.Count == 0) return;

        if (campusGrid == null)
        {
            campusGrid = FindObjectOfType<CampusGrid2D>();
        }
        if (campusGrid == null) return;

        var groups = slots
            .Where(slot => slot != null &&
                           slot.targetRef != null &&
                           slot.targetRef.mode == StructuredTargetMode.Collection &&
                           !string.IsNullOrWhiteSpace(slot.targetRef.collectionKey))
            .GroupBy(slot => slot.targetRef.collectionKey.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            MissionTaskSlot[] groupSlots = group.Where(s => s != null).ToArray();
            if (groupSlots.Length == 0) continue;

            if (!campusGrid.TryGetFeatureCollectionMembers(group.Key, out CampusGrid2D.FeatureSpatialProfile[] members) ||
                members == null ||
                members.Length == 0)
            {
                continue;
            }

            bool needsRepair = groupSlots.Length == 1
                ? !HasExplicitCoverageMembers(groupSlots[0])
                : !HasDistinctCoverageAcrossSlots(groupSlots);
            if (!needsRepair) continue;

            List<CampusGrid2D.FeatureSpatialProfile[]> partitions = PartitionCollectionMembersByAngle(members, groupSlots.Length);
            for (int i = 0; i < groupSlots.Length && i < partitions.Count; i++)
            {
                MissionTaskSlot slot = groupSlots[i];
                CampusGrid2D.FeatureSpatialProfile[] subset = partitions[i];
                if (slot == null || subset == null || subset.Length == 0) continue;

                StructuredTargetCardinality card = groupSlots.Length > 1
                    ? StructuredTargetCardinality.Subset
                    : StructuredTargetCardinality.All;
                slot.targetRef = BuildCoverageSubsetTargetReference(slot.targetRef, group.Key, subset, card);
                slot.target = ResolveStructuredTargetDisplayText(slot.targetRef, slot.target);
                slot.notes = AppendPlanningNote(slot.notes, $"coverage_subset_members={slot.targetRef.memberEntityIds.Length}");
            }
        }
    }

    private static bool HasExplicitCoverageMembers(MissionTaskSlot slot)
    {
        return slot != null &&
               slot.targetRef != null &&
               slot.targetRef.memberEntityIds != null &&
               slot.targetRef.memberEntityIds.Any(v => !string.IsNullOrWhiteSpace(v));
    }

    private static bool HasDistinctCoverageAcrossSlots(MissionTaskSlot[] slots)
    {
        if (slots == null || slots.Length == 0) return false;

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasAnyMembers = false;
        for (int i = 0; i < slots.Length; i++)
        {
            StructuredTargetReference targetRef = slots[i] != null ? slots[i].targetRef : null;
            string[] members = targetRef != null ? targetRef.memberEntityIds : null;
            if (members == null || members.Length == 0) return false;

            for (int j = 0; j < members.Length; j++)
            {
                string member = members[j];
                if (string.IsNullOrWhiteSpace(member)) continue;
                hasAnyMembers = true;
                if (!seen.Add(member.Trim()))
                {
                    return false;
                }
            }
        }

        return hasAnyMembers;
    }

    /// <summary>
    /// 把一个集合成员列表按“围绕整体中心的角度”切成多个连续扇区。
    /// 对 2 个智能体来说，效果就是左右/前后各拿一半；
    /// 对更多智能体来说，就是按空间方位切成多块，尽量减少互相抢同一路线。
    /// </summary>
    private static List<CampusGrid2D.FeatureSpatialProfile[]> PartitionCollectionMembersByAngle(CampusGrid2D.FeatureSpatialProfile[] members, int partitionCount)
    {
        List<CampusGrid2D.FeatureSpatialProfile[]> result = new List<CampusGrid2D.FeatureSpatialProfile[]>();
        int safeCount = Mathf.Max(1, partitionCount);
        if (members == null || members.Length == 0)
        {
            for (int i = 0; i < safeCount; i++) result.Add(Array.Empty<CampusGrid2D.FeatureSpatialProfile>());
            return result;
        }

        float centerX = 0f;
        float centerZ = 0f;
        for (int i = 0; i < members.Length; i++)
        {
            centerX += members[i].centroidWorld.x;
            centerZ += members[i].centroidWorld.z;
        }
        centerX /= members.Length;
        centerZ /= members.Length;

        CampusGrid2D.FeatureSpatialProfile[] sorted = members
            .OrderBy(m => Mathf.Atan2(m.centroidWorld.z - centerZ, m.centroidWorld.x - centerX))
            .ThenBy(m => m.centroidWorld.x)
            .ThenBy(m => m.centroidWorld.z)
            .ToArray();

        int baseSize = sorted.Length / safeCount;
        int extra = sorted.Length % safeCount;
        int cursor = 0;
        for (int i = 0; i < safeCount; i++)
        {
            int take = baseSize + (i < extra ? 1 : 0);
            if (take <= 0)
            {
                result.Add(Array.Empty<CampusGrid2D.FeatureSpatialProfile>());
                continue;
            }

            CampusGrid2D.FeatureSpatialProfile[] chunk = new CampusGrid2D.FeatureSpatialProfile[take];
            Array.Copy(sorted, cursor, chunk, 0, take);
            cursor += take;
            result.Add(chunk);
        }

        return result;
    }

    private static StructuredTargetReference BuildCoverageSubsetTargetReference(
        StructuredTargetReference original,
        string collectionKey,
        CampusGrid2D.FeatureSpatialProfile[] subset,
        StructuredTargetCardinality cardinality)
    {
        string[] memberIds = subset
            .Where(p => p != null)
            .Select(SelectCoverageMemberToken)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CampusGrid2D.FeatureSpatialProfile anchorProfile = subset.FirstOrDefault(p => p != null);
        string anchor = anchorProfile != null ? SelectCoverageMemberToken(anchorProfile) : string.Empty;

        StructuredTargetReference result = NormalizeStructuredTargetReference(
            original,
            !string.IsNullOrWhiteSpace(anchor) ? anchor : ResolveStructuredTargetQuery(original),
            StructuredTargetMode.Collection,
            cardinality);

        result.mode = StructuredTargetMode.Collection;
        result.cardinality = cardinality;
        result.entityClass = string.IsNullOrWhiteSpace(result.entityClass) ? "CampusFeature" : result.entityClass;
        result.collectionKey = string.IsNullOrWhiteSpace(collectionKey) ? result.collectionKey : collectionKey.Trim();
        result.memberEntityIds = memberIds;
        result.executableQuery = !string.IsNullOrWhiteSpace(anchor) ? anchor : result.executableQuery;
        result.anchorText = !string.IsNullOrWhiteSpace(anchor) ? anchor : result.anchorText;
        if (anchorProfile != null && string.IsNullOrWhiteSpace(result.areaHint))
        {
            result.areaHint = $"{anchorProfile.kind}@({anchorProfile.centroidWorld.x:F1},{anchorProfile.centroidWorld.z:F1})";
        }
        result.notes = AppendPlanningNote(result.notes, $"coverage_repaired_by_map={memberIds.Length}");
        return result;
    }

    private static string SelectCoverageMemberToken(CampusGrid2D.FeatureSpatialProfile profile)
    {
        if (profile == null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(profile.runtimeAlias)) return profile.runtimeAlias.Trim();
        if (!string.IsNullOrWhiteSpace(profile.uid)) return profile.uid.Trim();
        return string.IsNullOrWhiteSpace(profile.name) ? string.Empty : profile.name.Trim();
    }

    private static string AppendPlanningNote(string origin, string extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return string.IsNullOrWhiteSpace(origin) ? string.Empty : origin.Trim();
        if (string.IsNullOrWhiteSpace(origin)) return extra.Trim();
        return $"{origin.Trim()} | {extra.Trim()}";
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
    /// 3) 有明确同步组、终端行为和 grounded 目标的优先；
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
            targetRef = NormalizeStructuredTargetReference(representative.targetRef, representative.target),
            viaTargets = orderedVia.ToArray(),
            viaTargetRefs = NormalizeStructuredTargetReferenceArray(null, orderedVia.ToArray()),
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
            coordinationMode = TeamCoordinationMode.LooseSync,
            leaderAgentId = string.Empty,
            sharedTarget = string.Empty,
            corridorReservationKey = string.Empty,
            yieldToAgentIds = new string[0],
            syncPointTargets = new string[0],
            formationSlot = string.Empty
        };

        return new[] { directive };
    }

    /// <summary>
    /// 当团队语义解析彻底失败时，创建一份最保守的默认任务并立即生成默认计划。
    /// </summary>
    /// <param name="description">原始任务描述。</param>
    /// <param name="agentCount">请求的智能体数量。</param>
    private void CreateDefaultMission(string description, int agentCount)
    {
        // 构造一个只有 Supporter 角色的保守默认任务，保证系统仍然能继续运行。
        currentMission = new MissionAssignment
        {
            missionId = $"mission_default_{DateTime.Now:yyyyMMdd_HHmmss}",
            missionDescription = description,
            missionSource = string.Empty,
            sourceGoalId = string.Empty,
            sourceEventId = string.Empty,
            missionType = MissionType.Unknown,
            relationshipType = TeamRelationshipType.Cooperation,
            coordinatorId = agentProperties.AgentID,
            roles = new MissionRole[] {
                CreateRole(RoleType.Supporter, AgentType.Quadcopter, agentCount,
                    new string[] { "执行合作任务", "完成当前任务目标" })
            },
            communicationMode = CommunicationMode.Hybrid,
            requiredAgentCount = agentCount,
            teamObjective = description,
            successCondition = "完成团队目标",
            failureCondition = "未完成团队目标或超时",
            phaseTemplates = Array.Empty<MissionPhaseDefinition>(),
            coordinationDirectives = BuildDefaultMissionCoordinationDirectives(MissionType.Unknown),
            taskSlots = BuildTaskSlotsForMission(description, new MissionRole[] {
                CreateRole(RoleType.Supporter, AgentType.Quadcopter, agentCount, new string[] { "执行合作任务", "完成当前任务目标" })
            }, MissionType.Unknown, agentCount)
        };

        // 即使是默认任务，也要补上来源上下文，避免后续记忆和日志缺失来源信息。
        ApplyMissionSourceContext(currentMission);
        // 默认任务也需要初始化最小团队执行状态，避免动作层拿到空快照。
        ResetTeamExecutionState(currentMission);
        RefreshTeamExecutionStateSnapshot();

        // 为默认任务立即创建一份默认计划，避免系统停在“有任务但无计划”的状态。
        CreateDefaultPlan(currentMission);
    }

    /// <summary>
    /// 当前智能体向协调者发送“我接受了这个角色/槽位”的确认消息。
    /// </summary>
    /// <param name="role">当前智能体接受的角色名字符串。</param>
    /// <param name="reasoning">接受该角色/槽位的原因摘要，通常为 `LLM分析分配` 或 `fallback_default_plan`。</param>
    private void SendRoleAcceptance(string role, string reasoning)
    {
        // 只有通信模块存在且当前任务有效时，才发送接受回执。
        if (commModule != null && currentMission != null)
        {
            // 先把角色字符串安全地解析回 RoleType。
            RoleType acceptedRole;
            if (!Enum.TryParse(role, true, out acceptedRole))
            {
                // 解析失败时退回到当前智能体已有角色。
                acceptedRole = agentProperties.Role;
            }

            // 组装角色接受回执，协调者会基于它更新 acceptedAssignedAgents。
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

            // 发给当前任务协调者，消息类型沿用 RoleAssignment 通道。
            commModule.SendStructuredMessage(currentMission.coordinatorId, MessageType.RoleAssignment, payload, 1);
        }
    }

    // 获取当前任务
    public Plan GetCurrentTask()
    {
        if (currentPlan == null || currentPlan.currentStep >= currentPlan.steps.Length)
            return new Plan { mission = "无任务", planSteps = Array.Empty<PlanStepDefinition>(), currentStep = 0 };

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
        // 优先读取当前计划上的协同规则；没有时会退回 mission 级规则。
        TeamCoordinationDirective[] directives = GetCurrentCoordinationDirectives();
        if (directives == null || directives.Length == 0) return "none";

        // 将多条协同规则压成一段适合日志和 prompt 阅读的短摘要。
        List<string> parts = new List<string>();
        for (int i = 0; i < directives.Length; i++)
        {
            TeamCoordinationDirective directive = directives[i];
            if (directive == null) continue;
            parts.Add($"#{i + 1}:mode={directive.coordinationMode},leader={directive.leaderAgentId},shared={directive.sharedTarget},sharedRef={BuildStructuredTargetSummary(directive.sharedTargetRef, directive.sharedTarget)},corridor={directive.corridorReservationKey},formation={directive.formationSlot}");
        }

        // 至少拼出一条摘要时返回 joined 结果，否则返回 none。
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
        // 汇总依赖槽位，缺失时写 none。
        string deps = slot.dependsOnSlotIds != null && slot.dependsOnSlotIds.Length > 0
            ? string.Join("|", slot.dependsOnSlotIds)
            : "none";
        // 汇总中间检查点，缺失时写 none。
        string via = slot.viaTargets != null && slot.viaTargets.Length > 0
            ? string.Join("|", slot.viaTargets)
            : "none";
        // 把结构化目标压成轻量摘要，避免把内部 token 暴露给决策层。
        string targetRef = BuildStructuredTargetSummary(slot.targetRef, slot.target);
        // 当前槽位摘要只保留分工层真正需要的字段，不再夹带旧执行语义猜测。
        return $"assignmentId={slot.slotId},label={slot.slotLabel},role={slot.roleType},target={slot.target},targetRef={targetRef},via={via},syncGroup={slot.syncGroup},dependsOn={deps},done={slot.completionCondition}";
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
        // 没有任务时不做执行门控。
        if (mission == null) return false;
        // 优先按 taskSlot 数量判断参与人数；没有稳定槽位时回退到 requiredAgentCount。
        int participantCount = mission.taskSlots != null && mission.taskSlots.Length > 0
            ? mission.taskSlots.Length
            : Mathf.Max(1, mission.requiredAgentCount);
        // 只要是多人任务，就要求协调者统一放行后才能执行。
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
        // 只有当前智能体是协调者且当前 mission 有效时，才允许统一放行执行。
        if (!IsCurrentMissionCoordinator() || currentMission == null) return;
        // 没有任何已裁决槽位时也没什么可放行的。
        if (assignedTeamDecisions == null || assignedTeamDecisions.Count == 0) return;
        // 记录这一次循环是否真的放行了新成员。
        bool releasedAny = false;

        foreach (var kv in assignedTeamDecisions)
        {
            // 读取某个已裁决成员的 agentId。
            string agentId = kv.Key;
            // 读取该成员对应的完整角色/槽位裁决 payload。
            RoleDecisionPayload payload = kv.Value;
            if (payload == null || string.IsNullOrWhiteSpace(agentId)) continue;
            // 已经放行过的成员不重复发送 release。
            if (releasedAssignedAgents.Contains(agentId)) continue;
            // 还没发送接受回执的成员不能提前放行。
            if (!acceptedAssignedAgents.Contains(agentId)) continue;
            // 槽位依赖未满足时不能放行。
            if (!AreSlotDependenciesSatisfied(payload.assignedSlot)) continue;

            // 标记该成员已经被放行。
            releasedAssignedAgents.Add(agentId);
            releasedAny = true;
            RefreshTeamExecutionStateSnapshot();

            if (string.Equals(payload.agentId, agentProperties.AgentID, StringComparison.OrdinalIgnoreCase))
            {
                // 如果当前被放行的正是协调者自己，就直接本地解锁执行。
                ReleaseExecutionForAssignedPlan(payload.assignedSlot != null ? payload.assignedSlot.slotId : string.Empty);
                continue;
            }

            // 组装一个“execution_released”进度消息发给远端成员。
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

            // 将放行消息发给目标成员。
            commModule.SendStructuredMessage(payload.agentId, MessageType.TaskUpdate, releasePayload, 1);
        }

        if (releasedAny)
        {
            // 只要这轮真的放行了成员，就把 teamExecutionReleased 标记为 true。
            teamExecutionReleased = true;
            RefreshTeamExecutionStateSnapshot();
            Debug.Log($"[Planning] 协调者增量放行执行: released={releasedAssignedAgents.Count}/{assignedTeamDecisions.Count}, completed={completedAssignedAgents.Count}/{assignedTeamDecisions.Count}");
        }
    }

    /// <summary>
    /// 判断某个槽位是否已经满足执行依赖。
    /// 依赖判断只看结构化 dependsOnSlotIds，不再从任务文本猜“谁应该先做”。
    /// </summary>
    private bool AreSlotDependenciesSatisfied(MissionTaskSlot slot)
    {
        // 没有槽位依赖时默认已满足，可以直接执行。
        if (slot == null || slot.dependsOnSlotIds == null || slot.dependsOnSlotIds.Length == 0) return true;
        // 如果当前连团队裁决都还没有，自然也无法判断依赖完成。
        if (assignedTeamDecisions == null || assignedTeamDecisions.Count == 0) return false;

        // 先收集已经完成的槽位 ID 集合。
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
            // 逐个检查当前槽位依赖的前置 slotId 是否已经完成。
            string dep = slot.dependsOnSlotIds[i];
            if (string.IsNullOrWhiteSpace(dep)) continue;
            if (!completedSlotIds.Contains(dep))
            {
                // 只要有一个前置槽位尚未完成，就不能执行。
                return false;
            }
        }

        // 所有依赖都满足时返回 true。
        return true;
    }

    /// <summary>
    /// 本地收到协调者的统一放行后，允许开始执行已生成的计划。
    /// </summary>
    public void ReleaseExecutionForAssignedPlan(string slotId)
    {
        // 没有当前任务或当前计划时，没什么可放行的。
        if (currentMission == null || currentPlan == null) return;
        if (currentPlan.assignedSlot != null &&
            !string.IsNullOrWhiteSpace(slotId) &&
            !string.Equals(currentPlan.assignedSlot.slotId, slotId, StringComparison.OrdinalIgnoreCase))
        {
            // 如果远端发来的 slotId 和本地当前槽位不一致，就忽略这次放行。
            return;
        }

        // 解锁本地执行门控，允许动作层开始按 currentPlan 执行。
        localExecutionReleased = true;
        RefreshTeamExecutionStateSnapshot();
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
        // 只有 mission 存在、coordinatorId 非空且与当前智能体 ID 一致时，当前智能体才算协调者。
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
        // 只有协调者会处理接受回执；无效消息或无效任务直接忽略。
        if (payload == null || !IsCurrentMissionCoordinator() || currentMission == null) return;
        // 只接受当前 missionId 的回执，旧任务消息直接丢弃。
        if (!string.Equals(payload.missionId, currentMission.missionId, StringComparison.OrdinalIgnoreCase)) return;
        // 必须先能在裁决表中找到这个 agent 的分配结果。
        if (!assignedTeamDecisions.TryGetValue(payload.agentId, out RoleDecisionPayload decision) || decision == null) return;

        // 读取协调者预期该成员接受的 slotId。
        string expectedSlotId = decision.assignedSlot != null ? decision.assignedSlot.slotId : string.Empty;
        if (!string.IsNullOrWhiteSpace(expectedSlotId) &&
            !string.IsNullOrWhiteSpace(payload.acceptedSlotId) &&
            !string.Equals(expectedSlotId, payload.acceptedSlotId, StringComparison.OrdinalIgnoreCase))
        {
            // 槽位不匹配时拒收，避免成员错误回执污染当前任务状态。
            Debug.LogWarning($"[Planning] 忽略不匹配的槽位接受回执: agent={payload.agentId}, expected={expectedSlotId}, actual={payload.acceptedSlotId}");
            return;
        }

        // 记录该成员已经正式接受角色/槽位。
        acceptedAssignedAgents.Add(payload.agentId);
        RefreshTeamExecutionStateSnapshot();
        Debug.Log($"[Planning] 协调者收到角色接受回执: agent={payload.agentId}, role={payload.acceptedRole}, slot={payload.acceptedSlotId}, accepted={acceptedAssignedAgents.Count}/{assignedTeamDecisions.Count}");
        // 接受人数变化后，立即尝试增量放行可执行槽位。
        TryReleaseAssignedExecution();
    }

    /// <summary>
    /// 协调者侧处理任务进度/完成上报。
    /// 任务完成聚合只按已分配槽位的成员统计，防止某个成员单独完成就提前收口。
    /// </summary>
    public void HandleTaskProgressPayload(TaskProgressPayload payload)
    {
        // 只有协调者会消费成员上报的任务进度。
        if (payload == null || !IsCurrentMissionCoordinator() || currentMission == null) return;
        // 任务 ID 不匹配时直接忽略。
        if (!string.Equals(payload.missionId, currentMission.missionId, StringComparison.OrdinalIgnoreCase)) return;
        // 必须先能在裁决表中找到该成员的槽位信息。
        if (!assignedTeamDecisions.TryGetValue(payload.agentId, out RoleDecisionPayload decision) || decision == null) return;

        // 读取预期槽位 ID，用来过滤错误回执。
        string expectedSlotId = decision.assignedSlot != null ? decision.assignedSlot.slotId : string.Empty;
        if (!string.IsNullOrWhiteSpace(expectedSlotId) &&
            !string.IsNullOrWhiteSpace(payload.slotId) &&
            !string.Equals(expectedSlotId, payload.slotId, StringComparison.OrdinalIgnoreCase))
        {
            // 错槽位的进度消息不纳入当前任务状态。
            Debug.LogWarning($"[Planning] 忽略不匹配的任务进度: agent={payload.agentId}, expectedSlot={expectedSlotId}, actualSlot={payload.slotId}, status={payload.status}");
            return;
        }

        if (string.Equals(payload.status, "mission_completed", StringComparison.OrdinalIgnoreCase))
        {
            // 成员完成自己槽位后，把它记入 completedAssignedAgents。
            completedAssignedAgents.Add(payload.agentId);
            RefreshTeamExecutionStateSnapshot();
            Debug.Log($"[Planning] 协调者记录槽位完成: agent={payload.agentId}, slot={payload.slotId}, completed={completedAssignedAgents.Count}/{assignedTeamDecisions.Count}");
            // 某个前置槽位完成后，后继依赖槽位可能被解锁，因此立即尝试重新放行。
            TryReleaseAssignedExecution();
            // 每次有成员完成槽位后，也都检查一次整队任务是否可以收口。
            TryFinalizeCoordinatedMission();
            return;
        }

        // 非完成态时只记录进度日志，不触发完成聚合。
        Debug.Log($"[Planning] 协调者收到任务进度: agent={payload.agentId}, slot={payload.slotId}, status={payload.status}, step={payload.completedStep}, next={payload.nextStep}");
    }

    /// <summary>
    /// 协调者检查是否所有已分配槽位都已完成。
    /// 只有全部槽位完成后，才允许把整个 mission 判定为完成。
    /// </summary>
    private void TryFinalizeCoordinatedMission()
    {
        // 只有协调者才能做整队完成聚合。
        if (!IsCurrentMissionCoordinator() || currentMission == null) return;
        // 已经收口过一次的任务不重复收口。
        if (missionCompletionAggregated) return;
        // 没有槽位裁决时无法判断整队是否完成。
        if (assignedTeamDecisions == null || assignedTeamDecisions.Count == 0) return;

        foreach (string agentId in assignedTeamDecisions.Keys)
        {
            if (!completedAssignedAgents.Contains(agentId))
            {
                // 只要还有任何一个已分配成员没完成，就不能结束整个 mission。
                return;
            }
        }

        // 全部已分配成员都完成后，标记这轮任务已被聚合收口。
        missionCompletionAggregated = true;
        // 拼一份完成槽位摘要，用于日志和记忆。
        string slotSummary = string.Join(", ", assignedTeamDecisions.Values
            .Where(v => v != null && v.assignedSlot != null)
            .Select(v => $"{v.agentId}:{v.assignedSlot.slotLabel}"));

        if (memoryModule != null)
        {
            // 把整队任务完成写入记忆模块。
            memoryModule.RememberMissionOutcome(
                currentMission != null ? currentMission.missionId : string.Empty,
                string.Empty,
                $"协调任务完成：{currentMission.missionDescription}",
                success: true);
        }

        if (reflectionModule != null)
        {
            // 同时通知反思模块，这轮协同任务已经整体完成。
            reflectionModule.NotifyMissionOutcome(currentMission, null, true, $"协调任务完成：{currentMission.missionDescription}");
        }

        // 输出整队完成日志。
        Debug.Log($"[Planning] 协调任务完成: mission={currentMission.missionDescription}, slots={slotSummary}");
    }

    // 标记当前任务完成
    public void CompleteCurrentTask()
    {
        // 只有当前仍有未完成步骤时，才能推进 currentStep。
        if (currentPlan != null && currentPlan.currentStep < currentPlan.steps.Length)
        {
            // 先读取当前刚刚完成的步骤文本。
            string completedStep = currentPlan.steps[currentPlan.currentStep];
            if (memoryModule != null)
            {
                // 把单步完成写入记忆模块。
                memoryModule.RememberProgress(
                    currentMission != null ? currentMission.missionId : string.Empty,
                    currentPlan.assignedSlot != null ? currentPlan.assignedSlot.slotId : string.Empty,
                    completedStep,
                    $"完成步骤: {completedStep}",
                    currentPlan.assignedSlot != null ? currentPlan.assignedSlot.target : string.Empty);
            }
            // 推进到下一步。
            currentPlan.currentStep++;

            if (currentPlan.currentStep >= currentPlan.steps.Length)
            {
                // 如果已经越过最后一步，说明本地槽位已全部完成。
                string slotLabel = currentPlan.assignedSlot != null ? currentPlan.assignedSlot.slotLabel : currentPlan.agentRole.ToString();
                if (memoryModule != null)
                {
                    // 把本地槽位完成写入记忆模块。
                    memoryModule.RememberMissionOutcome(
                        currentMission != null ? currentMission.missionId : string.Empty,
                        currentPlan.assignedSlot != null ? currentPlan.assignedSlot.slotId : string.Empty,
                        $"完成本地槽位: {slotLabel}",
                        success: true,
                        targetRef: currentPlan.assignedSlot != null ? currentPlan.assignedSlot.target : string.Empty);
                }

                if (reflectionModule != null)
                {
                    // 通知反思模块：本地槽位已完成。
                    reflectionModule.NotifyMissionOutcome(
                        currentMission,
                        currentPlan.assignedSlot,
                        true,
                        $"完成本地槽位: {slotLabel}");
                }

                if (IsCurrentMissionCoordinator())
                {
                    // 协调者完成自己的槽位后，也要把自己记入已完成成员集合。
                    completedAssignedAgents.Add(agentProperties.AgentID);
                    RefreshTeamExecutionStateSnapshot();
                    Debug.Log($"[Planning] 协调者本地槽位完成: agent={agentProperties.AgentID}, slot={currentPlan.assignedSlot?.slotId}, completed={completedAssignedAgents.Count}/{assignedTeamDecisions.Count}");
                    // 协调者自己的前置槽位完成后，可能会解锁其他依赖槽位。
                    TryReleaseAssignedExecution();
                    // 然后再检查整队任务是否已经全部完成。
                    TryFinalizeCoordinatedMission();
                }
                else
                {
                    // 普通成员完成本地槽位后，向协调者上报 mission_completed。
                    ReportMissionCompletion();
                }
            }
            else
            {
                // 还没做到最后一步时，只上报 step_completed。
                ReportStepCompletion(completedStep);
            }
        }
    }

    // 报告步骤完成
    private void ReportStepCompletion(string step)
    {
        // 只有通信模块和当前任务都有效时，才上报步骤完成。
        if (commModule != null && currentMission != null)
        {
            // 读取接下来即将执行的下一步文本，没有时填 none。
            string nextStep = (currentPlan != null && currentPlan.currentStep >= 0 && currentPlan.currentStep < currentPlan.steps.Length)
                ? (currentPlan.steps[currentPlan.currentStep] ?? string.Empty)
                : "none";
            // 组装 step_completed 进度载荷。
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

            // 发给协调者，供其记录团队级进度。
            commModule.SendStructuredMessage(currentMission.coordinatorId, MessageType.TaskUpdate, payload, 1);
        }
    }

    // 报告任务完成
    private void ReportMissionCompletion()
    {
        // 只有通信模块和当前任务都有效时，才上报槽位完成。
        if (commModule != null && currentMission != null)
        {
            // 组装 mission_completed 载荷。
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

            // 向协调者明确发送“本地槽位已全部完成”的消息。
            commModule.SendStructuredMessage(currentMission.coordinatorId, MessageType.TaskCompletion, payload, 1);
        }
    }

    // 检查是否有活跃任务
    public bool HasActiveMission()
    {
        // 必须同时满足“有计划”“步骤未做完”“执行已被放行”，才算真正存在活跃任务。
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
