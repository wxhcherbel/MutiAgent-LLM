// Scripts/Modules/ActionDecisionModule.cs
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

public class ActionDecisionModule : MonoBehaviour
{
    private MemoryModule memoryModule;
    private PlanningModule planningModule;
    private LLMInterface llmInterface;
    private AgentProperties agentProperties;
    private AgentDynamicState agentState;
    private CommunicationModule commModule;
    private MLAgentsController mlAgentsController;
    private CampusGrid2D campusGrid;

    [Header("A*路径展开配置")]
    public bool enableAStarPathExpansion = true;     // 是否启用 MoveTo 的 A* 自动展开
    [Min(2f)] public float astarMinTriggerDistance = 10f; // 触发 A* 的最小水平距离
    [Range(1, 10)] public int astarWaypointStride = 3;    // 网格路径抽样步长
    [Range(1, 100)] public int astarMaxWaypoints = 30;    // 最多下发的 waypoint 数量（上限 100）
    public float astarWaypointYOffset = 0f;               // 网格中心点的 Y 偏移
    public bool astarPreferNearestWalkable = true;        // 起终点不可走时是否寻找最近可通行格子

    [Header("LLM路径点配置")]
    public bool preferLlmWaypoints = true;                // 优先使用 LLM 在参数中给出的 waypoint
    public bool allowAStarFallbackWhenNoLlmWaypoints = true; // LLM 未给 waypoint 时是否回退 A*
    [Range(4, 30)] public int maxCampusFeaturesInPrompt = 12; // 提示词中最多注入的建筑/地点条目
    public bool disableLlmLongWaypoints = true;           // 禁用 LLM 长 waypoint，改由系统反应式导航

    [Header("反应式导航执行")]
    [Range(1, 6)] public int reactiveAStarSegmentWaypoints = 2; // 每次只执行前N个A*子目标，下一轮再重规划
    [Min(0.5f)] public float stepTargetReachDistance = 8f;       // 判定“已到达步骤目标”的平面距离阈值
    [Min(0.2f)] public float stepTargetReachHeightTolerance = 2f; // 判定“已到达步骤目标”的高度阈值
    [Min(1f)] public float featureArrivalMinRadius = 6f;         // 建筑/地点目标的最小接近半径
    [Min(0.5f)] public float featureArrivalCellRadiusScale = 0.75f; // 按网格覆盖范围估算建筑接近半径时的缩放系数
    [Min(1f)] public float featureArrivalMaxRadius = 10f;        // 建筑/地点目标的最大接近半径，防止“大建筑半径过大导致过早判到达”

    [Header("决策性能优化")]
    public bool reuseHighLevelDecisionForSameStep = true;        // 同一步内复用第一阶段高层决策，减少重复 LLM 请求
    public bool skipWaypointRefinementForStaticNavigation = false; // 静态地点/坐标寻路默认跳过第二阶段 waypoint 细化
    [Range(1, 10)] public int fastStaticReactiveSegmentWaypoints = 5; // 静态目标且局部无明显动态障碍时，一次执行更长的 A* 子段
    [Min(2f)] public float deterministicNavigationObstacleRadius = 24f; // 判定“局部简单，可直接走系统导航”的邻域半径

    [Header("A*粗路径可视化")]
    public bool showCoarsePathGizmos = true;              // 是否显示粗路径 Gizmos
    public Color coarsePathColor = new Color(0.10f, 0.90f, 0.95f, 1f); // 粗路径颜色
    [Min(0.02f)] public float coarsePathPointRadius = 0.25f; // 粗路径点半径
    [Min(0.5f)] public float coarsePathGizmoDuration = 3f; // 粗路径显示持续时间
    public bool showCoarsePathGameView = true;            // 是否在 Game 视图显示粗路径线
    [Min(0.01f)] public float coarsePathLineWidth = 0.12f; // Game 视图粗路径线宽
    public bool keepCoarsePathUntilMissionComplete = true; // 是否保持显示到任务完成（忽略持续时间）
    private List<Vector3> lastCoarsePathForViz = new List<Vector3>(); // 最近一次粗路径（用于可视化）
    private float lastCoarsePathVizTime = -999f;          // 最近一次粗路径更新时间
    private LineRenderer coarsePathLineRenderer;          // Game 视图粗路径 LineRenderer
    private GameObject coarsePathLineObject;              // Game 视图粗路径对象
    private Material coarsePathLineMaterial;              // Game 视图粗路径材质

    /// <summary>
    /// 当前 step 的导航判定结果。
    /// 说明：
    /// 1) Mission 决定默认倾向（PlanningModule.navigationPolicy）；
    /// 2) Step 再做覆盖（移动/通信/局部/全局提示）；
    /// 3) ActionDecision 只消费这个结果，不重复定义 mission 规则。
    /// </summary>
    private struct StepNavigationDecision
    {
        public bool IsMovementStep;    // 当前 step 是否属于移动型
        public bool AllowAStarByStep;  // 当前 step 是否允许使用 A*（最终仍按动作类型决定）
        public string Reason;          // 判定原因（用于日志与提示词）
    }

    /// <summary>
    /// A* 粗路径上下文：
    /// 用于描述系统已经生成的“全局粗路径”。
    /// 在当前设计中，粗路径主要服务于：
    /// 1) 系统局部规划/局部避障；
    /// 2) 调试可视化；
    /// 3) 必要时向上层暴露路径摘要，而不是让 LLM 直接输出几何 waypoint。
    /// </summary>
    private struct CoarsePathContext
    {
        public bool HasPath;                  // 是否成功生成粗路径
        public string TargetFeatureName;      // 推断出的目标地点名（如 x_5）
        public Vector3 GoalWorld;             // 粗路径终点世界坐标
        public List<Vector3> CoarseWaypoints; // 粗路径抽样后的世界坐标点
        public string WaypointsInline;        // waypoints 的紧凑串（x z;x z;...）
        public string Summary;                // 给 LLM 的文本摘要
    }

    /// <summary>
    /// 系统维护的“步骤主目标锚点”。
    /// 正常流程下它来自第一阶段 LLM 输出的 target，再由系统解析为确定坐标；
    /// 若第一阶段缺失明确 target，则回退到 step/mission 文本解析。
    /// 用于：
    /// 1) 在第二阶段给 LLM 一个确定的已绑定目标；
    /// 2) 执行阶段做“是否到达目标”的门控，防止未到达就推进 step。
    /// </summary>
    private struct StepTargetBinding
    {
        public bool HasTarget;
        public string TargetRef;   // 固定 step_target
        public string TargetKind;  // Feature/World/SmallNode/Agent/None
        public string RawQuery;    // 从 step/mission 提取到的原始查询
        public string Uid;
        public string Name;
        public string Source;
        public Vector3 WorldPos;
        public Vector2Int GridCell;
        public string Summary;
    }

    private StepTargetBinding lastStepTargetBinding;
    private bool lastIssuedSequenceContainsMovement;
    private string cachedHighLevelStep = string.Empty;
    private List<ActionData> cachedHighLevelActionData;
    private StepTargetBinding cachedHighLevelTargetBinding;

    void Start()
    {
        memoryModule = GetComponent<MemoryModule>();
        planningModule = GetComponent<PlanningModule>();
        llmInterface = FindObjectOfType<LLMInterface>();
        commModule = GetComponent<CommunicationModule>();
        mlAgentsController = GetComponent<MLAgentsController>();
        campusGrid = FindObjectOfType<CampusGrid2D>();
        
        // 获取智能体属性组件
        IntelligentAgent intelligentAgent = GetComponent<IntelligentAgent>();
        if (intelligentAgent != null)
        {
            agentProperties = intelligentAgent.Properties;
            agentState = intelligentAgent.CurrentState;
        }
        else
        {
            Debug.LogError("未找到 IntelligentAgent 组件");
        }

        SyncCampusGridReference();
        InitializeCoarsePathLineRenderer();
    }

    /// <summary>
    /// 同步 CampusGrid2D 引用：
    /// 优先使用 AgentDynamicState.CampusGrid，其次回退场景查找。
    /// </summary>
    private void SyncCampusGridReference()
    {
        if (agentState != null && agentState.CampusGrid != null)
        {
            campusGrid = agentState.CampusGrid;
            return;
        }

        if (campusGrid == null)
        {
            campusGrid = FindObjectOfType<CampusGrid2D>();
        }

        if (agentState != null && agentState.CampusGrid == null && campusGrid != null)
        {
            agentState.CampusGrid = campusGrid;
        }
    }

    private void LateUpdate()
    {
        if (keepCoarsePathUntilMissionComplete && !IsMissionActive() && lastCoarsePathForViz.Count > 0)
        {
            ClearCoarsePathVisualization();
        }
        UpdateCoarsePathGameViewVisibility();
    }

    private void OnDestroy()
    {
        if (coarsePathLineMaterial != null)
        {
            Destroy(coarsePathLineMaterial);
            coarsePathLineMaterial = null;
        }

        if (coarsePathLineObject != null)
        {
            Destroy(coarsePathLineObject);
            coarsePathLineObject = null;
            coarsePathLineRenderer = null;
        }
    }
    // ==================== 核心决策方法 ====================
    // 获取当前具体步骤
    private string GetCurrentStepDescription()
    {
        if (planningModule?.currentPlan == null)
            return "无活跃任务";

        if (planningModule.currentPlan.currentStep >= planningModule.currentPlan.steps.Length)
            return "任务已完成";

        return planningModule.currentPlan.steps[planningModule.currentPlan.currentStep];
    }

    /// <summary>
    /// 对外接口：当前 step 是否建议优先使用 A* 粗路径。
    /// 供外部调试或行为树节点直接调用。
    /// </summary>
    public bool ShouldUseAStarForCurrentStep()
    {
        return BuildStepNavigationDecision(GetCurrentStepDescription()).AllowAStarByStep;
    }

    /// <summary>
    /// 对外接口：返回当前 step 的导航判定摘要（用于调试面板显示）。
    /// </summary>
    public string GetCurrentStepNavigationSummary()
    {
        StepNavigationDecision d = BuildStepNavigationDecision(GetCurrentStepDescription());
        return $"move={(d.IsMovementStep ? 1 : 0)},allowAStar={(d.AllowAStarByStep ? 1 : 0)},reason={d.Reason}";
    }

    /// <summary>
    /// 统一构建 step 导航判定（核心规则入口）。
    /// </summary>
    private StepNavigationDecision BuildStepNavigationDecision(string currentStep)
    {
        StepNavigationDecision decision = new StepNavigationDecision
        {
            IsMovementStep = false,
            AllowAStarByStep = false,
            Reason = "默认局部机动"
        };

        if (planningModule == null)
        {
            decision.Reason = "PlanningModule缺失，回退局部机动";
            return decision;
        }

        NavigationPolicy missionPolicy = planningModule.GetCurrentNavigationPolicy();
        bool isMovement = planningModule.IsMovementLikeStep(currentStep);
        bool isCommObs = planningModule.IsCommunicationOrObservationStep(currentStep);
        bool isLocal = planningModule.IsLikelyLocalStep(currentStep);
        bool preferAStar = planningModule.ShouldPreferAStarForStep(currentStep);

        decision.IsMovementStep = isMovement;
        decision.AllowAStarByStep = preferAStar;
        decision.Reason = $"policy={missionPolicy},move={(isMovement ? 1 : 0)},commObs={(isCommObs ? 1 : 0)},local={(isLocal ? 1 : 0)}";
        return decision;
    }

    /// <summary>
    /// 给 LLM 的导航策略摘要，尽量短，降低 token 开销。
    /// </summary>
    private string BuildNavigationPromptSummary(StepNavigationDecision decision)
    {
        NavigationPolicy p = planningModule != null ? planningModule.GetCurrentNavigationPolicy() : NavigationPolicy.Auto;
        return $"missionPolicy={p},stepMove={(decision.IsMovementStep ? 1 : 0)},allowAStar={(decision.AllowAStarByStep ? 1 : 0)},rule={decision.Reason}";
    }

    private StepIntentDefinition ResolveEffectiveStepIntent(string currentStep, List<ActionData> highLevelActionData, StepTargetBinding binding)
    {
        StepIntentDefinition plannedIntent = planningModule != null ? planningModule.GetCurrentStepIntent() : null;
        if (plannedIntent == null)
        {
            plannedIntent = new StepIntentDefinition
            {
                stepText = currentStep,
                intentType = StepIntentType.Navigate,
                primaryTarget = binding.HasTarget ? binding.RawQuery : "none",
                orderedViaTargets = new string[0],
                avoidTargets = new string[0],
                preferTargets = new string[0],
                requestedTeammateIds = new string[0],
                observationFocus = "none",
                communicationGoal = "none",
                finalBehavior = "arrive",
                completionCondition = currentStep,
                notes = "action-decision-fallback"
            };
        }

        if ((string.IsNullOrWhiteSpace(plannedIntent.primaryTarget) || plannedIntent.primaryTarget == "none") && binding.HasTarget)
        {
            plannedIntent.primaryTarget = binding.RawQuery;
        }

        if (plannedIntent.intentType == StepIntentType.Unknown &&
            highLevelActionData != null &&
            highLevelActionData.Any(a => IsMovementLikeActionData(a)))
        {
            plannedIntent.intentType = StepIntentType.Navigate;
        }

        plannedIntent.orderedViaTargets = plannedIntent.orderedViaTargets ?? new string[0];
        plannedIntent.avoidTargets = plannedIntent.avoidTargets ?? new string[0];
        plannedIntent.preferTargets = plannedIntent.preferTargets ?? new string[0];
        plannedIntent.requestedTeammateIds = plannedIntent.requestedTeammateIds ?? new string[0];
        plannedIntent.stepText = string.IsNullOrWhiteSpace(plannedIntent.stepText) ? currentStep : plannedIntent.stepText;
        return plannedIntent;
    }

    private RoutePolicyDefinition ResolveEffectiveRoutePolicy()
    {
        RoutePolicyDefinition routePolicy = planningModule != null ? planningModule.GetCurrentStepRoutePolicy() : null;
        if (routePolicy == null)
        {
            routePolicy = new RoutePolicyDefinition
            {
                approachSide = RouteApproachSide.Any,
                altitudeMode = RouteAltitudeMode.Default,
                clearance = RouteClearancePreference.Medium,
                avoidNodeTypes = new SmallNodeType[0],
                avoidFeatureNames = new string[0],
                preferFeatureNames = new string[0],
                keepTargetVisible = false,
                preferOpenSpace = false,
                allowGlobalAStar = true,
                allowLocalDetour = true,
                slowNearTarget = true,
                holdForTeammates = false,
                blockedPolicy = BlockedPolicyType.Replan,
                maxTeammatesInCorridor = 0,
                notes = "default"
            };
        }

        routePolicy.avoidNodeTypes = routePolicy.avoidNodeTypes ?? new SmallNodeType[0];
        routePolicy.avoidFeatureNames = routePolicy.avoidFeatureNames ?? new string[0];
        routePolicy.preferFeatureNames = routePolicy.preferFeatureNames ?? new string[0];
        return routePolicy;
    }

    private string BuildStepIntentSummary(StepIntentDefinition stepIntent)
    {
        if (stepIntent == null) return "none";
        string via = stepIntent.orderedViaTargets != null && stepIntent.orderedViaTargets.Length > 0
            ? string.Join(">", stepIntent.orderedViaTargets)
            : "none";
        return $"type={stepIntent.intentType},target={stepIntent.primaryTarget},via={via},final={stepIntent.finalBehavior}";
    }

    private string BuildRoutePolicySummary(RoutePolicyDefinition routePolicy)
    {
        if (routePolicy == null) return "none";
        string avoidNodes = routePolicy.avoidNodeTypes != null && routePolicy.avoidNodeTypes.Length > 0
            ? string.Join("|", routePolicy.avoidNodeTypes.Select(t => t.ToString()).ToArray())
            : "none";
        string avoidFeatures = routePolicy.avoidFeatureNames != null && routePolicy.avoidFeatureNames.Length > 0
            ? string.Join("|", routePolicy.avoidFeatureNames)
            : "none";
        return $"side={routePolicy.approachSide},alt={routePolicy.altitudeMode},clear={routePolicy.clearance},avoidNodes={avoidNodes},avoidFeatures={avoidFeatures},astar={(routePolicy.allowGlobalAStar ? 1 : 0)},detour={(routePolicy.allowLocalDetour ? 1 : 0)}";
    }

    /// <summary>
    /// 将当前计划中的协同约束压缩成给 LLM 的稳定输入。
    /// 这里的目标不是让系统解释协同语义，而是把 PlanningModule 已经结构化好的结果原样交给 LLM。
    /// </summary>
    private string BuildCoordinationPromptSummary(TeamCoordinationDirective[] directives)
    {
        if (directives == null || directives.Length == 0) return "none";

        List<string> parts = new List<string>();
        for (int i = 0; i < directives.Length; i++)
        {
            TeamCoordinationDirective d = directives[i];
            if (d == null) continue;
            string yields = d.yieldToAgentIds != null && d.yieldToAgentIds.Length > 0 ? string.Join("|", d.yieldToAgentIds) : "none";
            string syncPoints = d.syncPointTargets != null && d.syncPointTargets.Length > 0 ? string.Join("|", d.syncPointTargets) : "none";
            parts.Add($"#{i + 1}:mode={d.coordinationMode},leader={d.leaderAgentId},shared={d.sharedTarget},corridor={d.corridorReservationKey},yieldTo={yields},syncPoints={syncPoints},formation={d.formationSlot}");
        }

        return parts.Count > 0 ? string.Join(" || ", parts) : "none";
    }

    /// <summary>
    /// 给高层动作决策列出“当前允许引用的队友 ID”。
    /// 这些 ID 全部来自结构化 stepIntent / coordinationDirectives，而不是系统从文本里猜队友名。
    /// </summary>
    private string BuildTeammateReferenceSummary(StepIntentDefinition stepIntent, TeamCoordinationDirective[] directives)
    {
        HashSet<string> ids = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        if (stepIntent != null && stepIntent.requestedTeammateIds != null)
        {
            for (int i = 0; i < stepIntent.requestedTeammateIds.Length; i++)
            {
                string id = stepIntent.requestedTeammateIds[i];
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            }
        }

        if (directives != null)
        {
            for (int i = 0; i < directives.Length; i++)
            {
                TeamCoordinationDirective d = directives[i];
                if (d == null) continue;
                if (!string.IsNullOrWhiteSpace(d.leaderAgentId)) ids.Add(d.leaderAgentId.Trim());
                if (d.yieldToAgentIds != null)
                {
                    for (int j = 0; j < d.yieldToAgentIds.Length; j++)
                    {
                        string id = d.yieldToAgentIds[j];
                        if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
                    }
                }
            }
        }

        return ids.Count > 0 ? string.Join("|", ids) : "none";
    }

    /// <summary>
    /// 只根据 PlanningModule 已经产出的结构化结果构建 step_target 绑定。
    /// 可以把它理解成“最后一跳的取值规则”：
    /// 1) 当前 stepIntent.primaryTarget 最具体，所以优先用它；
    /// 2) 如果 step 没写清楚，就退回到 assignedSlot.target；
    /// 3) 如果槽位也没写清楚，再尝试任务级协同里的 sharedTarget。
    /// 换句话说，ActionDecision 在这里不再直接读用户原始自然语言，
    /// 而是只信 PlanningModule 已经整理好的结构化目标字段。
    /// </summary>
    private StepTargetBinding ResolveStructuredStepTargetBinding()
    {
        // 先准备一个“空绑定”。
        // 如果后面三层来源都拿不到目标，就把这个空结果返回给上层。
        StepTargetBinding empty = new StepTargetBinding
        {
            HasTarget = false,
            TargetRef = "step_target",
            TargetKind = "None",
            RawQuery = string.Empty,
            Uid = string.Empty,
            Name = string.Empty,
            Source = "None",
            WorldPos = transform.position,
            GridCell = new Vector2Int(-1, -1),
            Summary = "none"
        };

        if (planningModule == null) return empty;

        // 第一优先级：当前 step 自己声明的 primaryTarget。
        // 这是“这一小步现在最想处理谁”的直接答案，粒度最细。
        StepIntentDefinition stepIntent = planningModule.GetCurrentStepIntent();
        if (stepIntent != null &&
            !string.IsNullOrWhiteSpace(stepIntent.primaryTarget) &&
            !string.Equals(stepIntent.primaryTarget, "none", System.StringComparison.OrdinalIgnoreCase) &&
            TryBuildStepTargetBindingFromTarget(stepIntent.primaryTarget, out StepTargetBinding fromIntent))
        {
            fromIntent.Source = $"StructuredStepIntent:{fromIntent.Source}";
            return fromIntent;
        }

        // 第二优先级：回退到当前槽位的最终目标。
        // 如果 step 没写清楚，至少槽位通常还知道“这一整轮任务最后要去哪儿”。
        MissionTaskSlot assignedSlot = planningModule.currentPlan != null ? planningModule.currentPlan.assignedSlot : null;
        if (assignedSlot != null &&
            !string.IsNullOrWhiteSpace(assignedSlot.target) &&
            !string.Equals(assignedSlot.target, "none", System.StringComparison.OrdinalIgnoreCase) &&
            TryBuildStepTargetBindingFromTarget(assignedSlot.target, out StepTargetBinding fromSlot))
        {
            fromSlot.Source = $"AssignedSlot:{fromSlot.Source}";
            return fromSlot;
        }

        // 第三优先级：再退回到协同指令里的 sharedTarget。
        // 这通常出现在“全队围绕同一个共享目标协作”的任务里。
        TeamCoordinationDirective[] directives = planningModule.GetCurrentCoordinationDirectives();
        if (directives != null)
        {
            for (int i = 0; i < directives.Length; i++)
            {
                TeamCoordinationDirective directive = directives[i];
                if (directive == null || string.IsNullOrWhiteSpace(directive.sharedTarget)) continue;
                if (TryBuildStepTargetBindingFromTarget(directive.sharedTarget, out StepTargetBinding fromDirective))
                {
                    fromDirective.Source = $"CoordinationDirective:{fromDirective.Source}";
                    return fromDirective;
                }
            }
        }

        // 三层都没拿到，就承认当前没有稳定可绑定目标。
        return empty;
    }

    /// <summary>
    /// 基于系统已绑定的目标预构建 A* 粗路径（若可行）。
    /// 规则：
    /// 1) 仅在该 step 是移动型时尝试；
    /// 2) 仅对适合全局寻路的目标（地点/坐标/静态节点）构建；
    /// 3) 动态小节点和队友目标默认不预建粗路径，交给第二阶段局部滚动决策。
    /// </summary>
    private CoarsePathContext BuildCoarsePathContextForStep(string currentStep, StepNavigationDecision navDecision, StepTargetBinding stepTarget, StepIntentDefinition stepIntent, RoutePolicyDefinition routePolicy)
    {
        if (!keepCoarsePathUntilMissionComplete)
        {
            ClearCoarsePathVisualization();
        }

        CoarsePathContext ctx = new CoarsePathContext
        {
            HasPath = false,
            TargetFeatureName = string.Empty,
            GoalWorld = transform.position,
            CoarseWaypoints = new List<Vector3>(),
            WaypointsInline = string.Empty,
            Summary = "无A*粗路径"
        };

        if (!navDecision.IsMovementStep)
        {
            ctx.Summary = "当前step非移动步骤，无粗路径";
            return ctx;
        }
        if (routePolicy != null && !routePolicy.allowGlobalAStar)
        {
            ctx.Summary = "当前step路径策略关闭了全局A*，交由局部执行层处理";
            return ctx;
        }
        if (campusGrid == null || campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null)
        {
            ctx.Summary = "CampusGrid2D 不可用";
            return ctx;
        }

        if (stepTarget.HasTarget && !ShouldBuildCoarsePathForTarget(stepTarget))
        {
            ctx.Summary = $"目标类型={stepTarget.TargetKind}，本轮不构建A*粗路径，交由局部滚动决策";
            return ctx;
        }

        if (!TryResolveWalkableCell(transform.position, out Vector2Int startCell))
        {
            ctx.Summary = "起点不可用";
            return ctx;
        }

        if (!TryBuildRouteAnchorCells(currentStep, stepTarget, stepIntent, out List<string> routeAnchorNames, out List<Vector2Int> routeAnchorCells, out string goalSource))
        {
            ctx.Summary = "未在结构化意图/CampusGrid中解析到目标，无法预构建粗路径";
            return ctx;
        }

        List<Vector2Int> gridPath = new List<Vector2Int>();
        List<int> anchorEndGridIndices = new List<int>();
        Vector2Int cursor = startCell;
        for (int i = 0; i < routeAnchorCells.Count; i++)
        {
            Vector2Int goalCell = routeAnchorCells[i];
            string featureName = routeAnchorNames[i];

            if (goalCell == cursor)
            {
                continue;
            }

            if (!campusGrid.IsWalkable(goalCell.x, goalCell.y))
            {
                if (!campusGrid.TryFindNearestWalkable(goalCell, 10, out Vector2Int nearGoal))
                {
                    ctx.Summary = $"目标地点不可达: {featureName}";
                    return ctx;
                }
                goalCell = nearGoal;
            }

            List<Vector2Int> segmentPath = campusGrid.FindPathAStar(cursor, goalCell);
            if (segmentPath == null || segmentPath.Count < 2)
            {
                ctx.Summary = $"A*失败: {featureName}";
                return ctx;
            }

            if (gridPath.Count > 0 && segmentPath.Count > 0)
            {
                segmentPath.RemoveAt(0);
            }

            gridPath.AddRange(segmentPath);
            if (gridPath.Count > 0)
            {
                // 记录每一段锚点（via / 最终目标）在整条网格路径中的结束位置。
                // 后续即使做 waypoint 压缩，也必须保住这些“结构化检查点”，
                // 否则就会出现“系统算过粗路径，但执行时没有真正经过检查点”的问题。
                anchorEndGridIndices.Add(gridPath.Count - 1);
            }
            cursor = goalCell;
        }

        float y = ResolveWaypointY(transform.position);
        int stride = Mathf.Max(1, astarWaypointStride);
        List<Vector3> waypoints = new List<Vector3>();
        List<int> waypointSourceGridIndices = new List<int>();

        // 关键：粗路径第一个点固定为无人机当前位置，保证可视化从机体起始。
        Vector3 startWorld = transform.position;
        startWorld.y = y;
        waypoints.Add(startWorld);
        waypointSourceGridIndices.Add(-1);

        HashSet<int> sampledGridIndices = new HashSet<int>();
        for (int i = stride; i < gridPath.Count; i += stride)
        {
            sampledGridIndices.Add(i);
        }
        for (int i = 0; i < anchorEndGridIndices.Count; i++)
        {
            sampledGridIndices.Add(anchorEndGridIndices[i]);
        }

        foreach (int idx in sampledGridIndices.OrderBy(v => v))
        {
            if (idx < 0 || idx >= gridPath.Count) continue;
            Vector2Int c = gridPath[idx];
            Vector3 wp = campusGrid.GridToWorldCenter(c.x, c.y, astarWaypointYOffset);
            wp.y = y;
            if (waypoints.Count == 0 || Vector3.Distance(waypoints[waypoints.Count - 1], wp) > 0.1f)
            {
                waypoints.Add(wp);
                waypointSourceGridIndices.Add(idx);
            }
        }

        Vector2Int finalGoalCell = routeAnchorCells[routeAnchorCells.Count - 1];
        Vector3 final = campusGrid.GridToWorldCenter(finalGoalCell.x, finalGoalCell.y, astarWaypointYOffset);
        final.y = y;
        if (waypoints.Count == 0 || Vector3.Distance(waypoints[waypoints.Count - 1], final) > Mathf.Max(0.5f, campusGrid.cellSize * 0.5f))
        {
            waypoints.Add(final);
            waypointSourceGridIndices.Add(gridPath.Count - 1);
        }

        int maxCount = Mathf.Max(2, astarMaxWaypoints);
        maxCount = Mathf.Max(maxCount, anchorEndGridIndices.Count + 2);
        if (waypoints.Count > maxCount)
        {
            HashSet<int> preserveWaypointIndices = new HashSet<int> { 0, waypoints.Count - 1 };
            for (int i = 0; i < anchorEndGridIndices.Count; i++)
            {
                int gridIdx = anchorEndGridIndices[i];
                if (gridIdx < 0) continue;
                for (int j = 0; j < waypointSourceGridIndices.Count; j++)
                {
                    if (waypointSourceGridIndices[j] == gridIdx)
                    {
                        preserveWaypointIndices.Add(j);
                        break;
                    }
                }
            }

            waypoints = DownsampleWaypointsPreserveIndices(waypoints, maxCount, preserveWaypointIndices, out List<int> keptIndices);
            waypointSourceGridIndices = keptIndices
                .Where(idx => idx >= 0 && idx < waypointSourceGridIndices.Count)
                .Select(idx => waypointSourceGridIndices[idx])
                .ToList();
        }

        ctx.HasPath = waypoints.Count > 0;
        ctx.TargetFeatureName = string.Join("->", routeAnchorNames.ToArray());
        ctx.GoalWorld = final;
        ctx.CoarseWaypoints = waypoints;
        ctx.WaypointsInline = BuildWaypointInlineString(waypoints);
        ctx.Summary = ctx.HasPath
            ? $"target={ctx.TargetFeatureName},source={goalSource},policy={BuildRoutePolicySummary(routePolicy)},gridPath={gridPath.Count},coarseWp={waypoints.Count},waypoints={ctx.WaypointsInline}"
            : $"A*失败: {ctx.TargetFeatureName}";

        if (ctx.HasPath)
        {
            UpdateCoarsePathVisualization(waypoints);
        }
        return ctx;
    }

    private bool TryBuildRouteAnchorCells(string currentStep, StepTargetBinding stepTarget, StepIntentDefinition stepIntent, out List<string> routeAnchorNames, out List<Vector2Int> routeAnchorCells, out string source)
    {
        routeAnchorNames = new List<string>();
        routeAnchorCells = new List<Vector2Int>();
        source = "none";

        if (stepIntent != null && stepIntent.orderedViaTargets != null)
        {
            for (int i = 0; i < stepIntent.orderedViaTargets.Length; i++)
            {
                string viaTarget = stepIntent.orderedViaTargets[i];
                if (TryResolveRouteAnchorCell(viaTarget, out string viaName, out Vector2Int viaCell, out string viaSource))
                {
                    routeAnchorNames.Add(viaName);
                    routeAnchorCells.Add(viaCell);
                    source = source == "none" ? viaSource : $"{source}>{viaSource}";
                }
            }
        }

        if (stepTarget.HasTarget && ShouldBuildCoarsePathForTarget(stepTarget) && TryResolveWalkableCell(stepTarget.WorldPos, out Vector2Int boundCell))
        {
            string boundName = !string.IsNullOrWhiteSpace(stepTarget.Uid)
                ? stepTarget.Uid
                : (!string.IsNullOrWhiteSpace(stepTarget.Name) ? stepTarget.Name : stepTarget.RawQuery);
            routeAnchorNames.Add(boundName);
            routeAnchorCells.Add(boundCell);
            source = source == "none" ? $"StepTarget:{stepTarget.Source}" : $"{source}>StepTarget:{stepTarget.Source}";
        }
        else if (stepIntent != null &&
                 !string.IsNullOrWhiteSpace(stepIntent.primaryTarget) &&
                 !string.Equals(stepIntent.primaryTarget, "none", System.StringComparison.OrdinalIgnoreCase) &&
                 TryResolveRouteAnchorCell(stepIntent.primaryTarget, out string primaryName, out Vector2Int primaryCell, out string primarySource))
        {
            routeAnchorNames.Add(primaryName);
            routeAnchorCells.Add(primaryCell);
            source = source == "none" ? primarySource : $"{source}>{primarySource}";
        }
        return routeAnchorCells.Count > 0;
    }

    private bool TryResolveRouteAnchorCell(string targetQuery, out string targetName, out Vector2Int goalCell, out string source)
    {
        targetName = string.Empty;
        goalCell = new Vector2Int(-1, -1);
        source = "none";

        if (string.IsNullOrWhiteSpace(targetQuery)) return false;

        if (TryBuildStepTargetBindingFromTarget(targetQuery, out StepTargetBinding binding) &&
            binding.HasTarget &&
            ShouldBuildCoarsePathForTarget(binding) &&
            TryResolveWalkableCell(binding.WorldPos, out goalCell))
        {
            targetName = !string.IsNullOrWhiteSpace(binding.Uid)
                ? binding.Uid
                : (!string.IsNullOrWhiteSpace(binding.Name) ? binding.Name : binding.RawQuery);
            source = binding.Source;
            return true;
        }

        return TryResolveGoalCellForCoarsePath(targetQuery, out targetName, out goalCell, out source);
    }

    private bool ShouldBuildCoarsePathForTarget(StepTargetBinding stepTarget)
    {
        if (!stepTarget.HasTarget) return false;
        if (stepTarget.TargetKind == "Feature" || stepTarget.TargetKind == "World") return true;
        if (stepTarget.TargetKind == "Agent") return false;

        if (stepTarget.TargetKind == "SmallNode")
        {
            string source = stepTarget.Source ?? string.Empty;
            if (source.IndexOf("Pedestrian", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                source.IndexOf("Vehicle", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                source.IndexOf("NearbyAgent", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 刷新粗路径可视化缓存。
    /// </summary>
    private void UpdateCoarsePathVisualization(List<Vector3> waypoints)
    {
        lastCoarsePathForViz.Clear();
        if (waypoints == null || waypoints.Count == 0) return;
        lastCoarsePathForViz.AddRange(waypoints);
        lastCoarsePathVizTime = Time.time;
        UpdateCoarsePathLineRendererNow();
    }

    /// <summary>
    /// 清空粗路径可视化缓存。
    /// </summary>
    private void ClearCoarsePathVisualization()
    {
        lastCoarsePathForViz.Clear();
        lastCoarsePathVizTime = -999f;
        if (coarsePathLineRenderer != null)
        {
            coarsePathLineRenderer.positionCount = 0;
            coarsePathLineRenderer.enabled = false;
        }
    }

    /// <summary>
    /// 初始化 Game 视图粗路径 LineRenderer。
    /// </summary>
    private void InitializeCoarsePathLineRenderer()
    {
        if (coarsePathLineObject != null && coarsePathLineRenderer != null) return;

        coarsePathLineObject = new GameObject("CoarsePath_GameView");
        coarsePathLineObject.hideFlags = HideFlags.DontSave;
        coarsePathLineObject.transform.SetParent(null);

        coarsePathLineRenderer = coarsePathLineObject.AddComponent<LineRenderer>();
        coarsePathLineRenderer.useWorldSpace = true;
        coarsePathLineRenderer.positionCount = 0;
        coarsePathLineRenderer.enabled = false;
        coarsePathLineRenderer.alignment = LineAlignment.View;
        coarsePathLineRenderer.numCapVertices = 6;
        coarsePathLineRenderer.numCornerVertices = 6;
        coarsePathLineRenderer.textureMode = LineTextureMode.Stretch;
        coarsePathLineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        coarsePathLineRenderer.receiveShadows = false;
        coarsePathLineRenderer.sortingOrder = 6000;

        coarsePathLineMaterial = new Material(Shader.Find("Sprites/Default"));
        coarsePathLineMaterial.hideFlags = HideFlags.DontSave;
        coarsePathLineRenderer.material = coarsePathLineMaterial;
    }

    /// <summary>
    /// 立即刷新 Game 视图中的粗路径线。
    /// </summary>
    private void UpdateCoarsePathLineRendererNow()
    {
        if (!showCoarsePathGameView)
        {
            if (coarsePathLineRenderer != null) coarsePathLineRenderer.enabled = false;
            return;
        }

        if (lastCoarsePathForViz == null || lastCoarsePathForViz.Count < 2)
        {
            if (coarsePathLineRenderer != null) coarsePathLineRenderer.enabled = false;
            return;
        }

        if (coarsePathLineRenderer == null || coarsePathLineObject == null)
        {
            InitializeCoarsePathLineRenderer();
        }
        if (coarsePathLineRenderer == null) return;

        coarsePathLineRenderer.widthMultiplier = coarsePathLineWidth;
        coarsePathLineRenderer.startColor = coarsePathColor;
        coarsePathLineRenderer.endColor = coarsePathColor;
        coarsePathLineRenderer.positionCount = lastCoarsePathForViz.Count;
        coarsePathLineRenderer.SetPositions(lastCoarsePathForViz.ToArray());
        coarsePathLineRenderer.enabled = true;
    }

    /// <summary>
    /// 按持续时间与开关控制 Game 视图粗路径显示。
    /// </summary>
    private void UpdateCoarsePathGameViewVisibility()
    {
        if (coarsePathLineRenderer == null) return;

        if (!showCoarsePathGameView)
        {
            coarsePathLineRenderer.enabled = false;
            return;
        }

        bool alive = keepCoarsePathUntilMissionComplete && IsMissionActive();
        if (!alive)
        {
            alive = Time.time - lastCoarsePathVizTime <= coarsePathGizmoDuration;
        }
        if (!alive || lastCoarsePathForViz == null || lastCoarsePathForViz.Count < 2)
        {
            coarsePathLineRenderer.enabled = false;
            return;
        }

        if (!coarsePathLineRenderer.enabled)
        {
            UpdateCoarsePathLineRendererNow();
        }
    }

    /// <summary>
    /// 判断当前是否仍有活跃任务。
    /// </summary>
    private bool IsMissionActive()
    {
        return planningModule != null && planningModule.HasActiveMission();
    }

    private void OnDrawGizmos()
    {
        if (!showCoarsePathGizmos) return;
        if (lastCoarsePathForViz == null || lastCoarsePathForViz.Count < 2) return;
        bool alive = keepCoarsePathUntilMissionComplete && IsMissionActive();
        if (!alive)
        {
            alive = !Application.isPlaying || (Time.time - lastCoarsePathVizTime <= coarsePathGizmoDuration);
        }
        if (!alive) return;

        Gizmos.color = coarsePathColor;
        for (int i = 0; i < lastCoarsePathForViz.Count; i++)
        {
            Vector3 p = lastCoarsePathForViz[i];
            Gizmos.DrawSphere(p, coarsePathPointRadius);
            if (i > 0)
            {
                Gizmos.DrawLine(lastCoarsePathForViz[i - 1], p);
            }
        }
    }

    /// <summary>
    /// 为粗路径构建解析目标网格。
    /// 输入必须是已经结构化过的 target token，例如：
    /// label:x_2 / world(10,20) / nodeId / AgentID。
    /// 这里不再从自然语言 step 文本中抽取目标词。
    /// </summary>
    private bool TryResolveGoalCellForCoarsePath(string targetQuery, out string targetName, out Vector2Int goalCell, out string source)
    {
        targetName = string.Empty;
        goalCell = new Vector2Int(-1, -1);
        source = string.Empty;
        if (string.IsNullOrWhiteSpace(targetQuery)) return false;

        string q = targetQuery.Trim();

        if (ContainsSelfReference(q) && TryResolveWalkableCell(transform.position, out goalCell))
        {
            targetName = "self";
            source = "Self";
            return true;
        }

        if (TryParseWorldTarget(q, out Vector3 worldPos) &&
            TryResolveWalkableCell(worldPos, out goalCell))
        {
            targetName = q;
            source = "WorldCoordinate";
            return true;
        }

        if (TryResolvePerceivedCampusFeatureTarget(q, out Vector3 perceivedFeaturePos) &&
            TryResolveWalkableCell(perceivedFeaturePos, out goalCell))
        {
            targetName = q;
            source = "PerceivedCampus";
            return true;
        }

        if (TryResolveSmallNodeTargetWorld(q, out Vector3 smallNodePos) &&
            TryResolveWalkableCell(smallNodePos, out goalCell))
        {
            targetName = q;
            source = "PerceivedSmallNode";
            return true;
        }

        if (TryResolveFeatureNameToCell(q, out goalCell))
        {
            targetName = q;
            source = "CampusGrid";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断文本是否在引用智能体自身或当前位置。
    /// </summary>
    private static bool ContainsSelfReference(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().ToLowerInvariant();

        string[] exactTokens =
        {
            "self", "myself", "here", "current_position", "current position",
            "自身", "自己", "本机", "本体", "当前位置", "原地", "这里"
        };
        for (int i = 0; i < exactTokens.Length; i++)
        {
            if (t == exactTokens[i]) return true;
        }

        string[] phrases =
        {
            "自身", "自己", "本机", "本体", "当前位置", "原地", "这里",
            "定位自身", "定位自己", "定位当前位置", "回到原地", "回到当前位置",
            "self", "myself", "current position"
        };
        for (int i = 0; i < phrases.Length; i++)
        {
            if (t.Contains(phrases[i])) return true;
        }

        return false;
    }

    /// <summary>
    /// 构建“自身/当前位置”目标绑定。
    /// </summary>
    private StepTargetBinding BuildSelfTargetBinding(string rawQuery, string source)
    {
        StepTargetBinding binding = new StepTargetBinding
        {
            HasTarget = true,
            TargetRef = "step_target",
            TargetKind = "Self",
            RawQuery = string.IsNullOrWhiteSpace(rawQuery) ? "self" : rawQuery.Trim(),
            Uid = !string.IsNullOrWhiteSpace(agentProperties?.AgentID) ? agentProperties.AgentID : gameObject.name,
            Name = gameObject.name,
            Source = source,
            WorldPos = transform.position,
            GridCell = new Vector2Int(-1, -1)
        };

        if (TryResolveWalkableCell(binding.WorldPos, out Vector2Int cell))
        {
            binding.GridCell = cell;
        }

        binding.Summary =
            $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},uid={binding.Uid},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
        return binding;
    }


    /// <summary>
    /// 从已感知小节点中解析目标世界坐标（按 nodeId / displayName / sceneName 匹配）。
    /// </summary>
    private bool TryResolveSmallNodeTargetWorld(string query, out Vector3 worldPos)
    {
        worldPos = transform.position;
        if (agentState == null || string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();

        if (agentState.NearbySmallNodes != null && agentState.NearbySmallNodes.Count > 0)
        {
            foreach (var kv in agentState.NearbySmallNodes)
            {
                SmallNodeData n = kv.Value;
                if (n == null) continue;
                string id = n.NodeId ?? string.Empty;
                string display = n.DisplayName ?? string.Empty;
                string sceneName = n.SceneObject != null ? n.SceneObject.name : string.Empty;
                if (string.Equals(id, q, System.StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(display) && display.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrWhiteSpace(sceneName) && sceneName.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    worldPos = n.WorldPosition;
                    return true;
                }
            }
        }

        if (agentState.DetectedSmallNodes == null || agentState.DetectedSmallNodes.Count == 0) return false;
        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            SmallNodeData n = agentState.DetectedSmallNodes[i];
            if (n == null) continue;
            string id = n.NodeId ?? string.Empty;
            string display = n.DisplayName ?? string.Empty;
            string sceneName = n.SceneObject != null ? n.SceneObject.name : string.Empty;
            if (string.Equals(id, q, System.StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(display) && display.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrWhiteSpace(sceneName) && sceneName.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0))
            {
                worldPos = n.WorldPosition;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把 waypoint 列表序列化成 "x z;x z;..."，便于放入参数文本。
    /// </summary>
    private static string BuildWaypointInlineString(List<Vector3> waypoints)
    {
        if (waypoints == null || waypoints.Count == 0) return string.Empty;
        List<string> parts = new List<string>(waypoints.Count);
        for (int i = 0; i < waypoints.Count; i++)
        {
            Vector3 p = waypoints[i];
            parts.Add($"{p.x.ToString("F1", CultureInfo.InvariantCulture)} {p.z.ToString("F1", CultureInfo.InvariantCulture)}");
        }
        return string.Join(";", parts);
    }

    /// <summary>
    /// 多层决策主入口：
    /// 1) 第一阶段让 LLM 解析 step 的高层动作与自然语言 target；
    /// 2) 系统读取 PlanningModule 已生成的结构化 StepIntent/RoutePolicy；
    /// 3) 系统根据 target + 经过点 + 路径策略生成粗路径；
    /// 4) 系统局部执行层再结合实时感知进行滚动避障和轨迹跟踪；
    /// 5) 执行后若未到最终目标，则保留在当前 step 继续滚动重规划。
    /// </summary>
    public IEnumerator<object> DecideNextAction()
    {
        SyncCampusGridReference();
        lastIssuedSequenceContainsMovement = false;
        // 动作层必须尊重 PlanningModule 的执行门控。
        // 即使上层调度器直接调用了 DecideNextAction，只要协调者还没放行，本轮也不能偷跑。
        if (planningModule == null || !planningModule.HasActiveMission())
        {
            yield break;
        }

        string currentStep = GetCurrentStepDescription();
        if (currentStep == "无活跃任务" || currentStep == "任务已完成")
        {
            yield break;
        }
        StepNavigationDecision navDecision = BuildStepNavigationDecision(currentStep);
        StepTargetBinding structuredBinding = ResolveStructuredStepTargetBinding();
        List<ActionData> highLevelActionData;
        bool reusedHighLevelDecision = TryGetCachedHighLevelDecision(currentStep, out highLevelActionData, out lastStepTargetBinding);
        if (structuredBinding.HasTarget)
        {
            lastStepTargetBinding = structuredBinding;
        }

        if (!reusedHighLevelDecision)
        {
            string highLevelPrompt = BuildHighLevelDecisionPrompt(currentStep, navDecision, structuredBinding);
            string highLevelResponse = string.Empty;
            yield return llmInterface.SendRequest(highLevelPrompt, result =>
            {
                highLevelResponse = result ?? string.Empty;
            }, temperature: 0.1f, maxTokens: 520);

            string agentIdForLog = agentProperties != null ? agentProperties.AgentID : gameObject.name;
            Debug.Log($"[ActionDecision][Stage1][{agentIdForLog}] LLM 原始返回:\n{highLevelResponse}");

            if (string.IsNullOrWhiteSpace(highLevelResponse))
            {
                ExecuteDefaultAction();
                yield break;
            }

            highLevelActionData = ParseActionDataSequenceFromJSON(highLevelResponse);
            if (highLevelActionData == null || highLevelActionData.Count == 0)
            {
                ExecuteDefaultAction();
                yield break;
            }

            // step 的主目标优先来自 PlanningModule 的结构化绑定；
            // 只有当规划层没有给出目标时，才允许使用高层动作里显式声明的 target。
            lastStepTargetBinding = ResolveStepTargetBindingFromActionData(highLevelActionData);
            UpdateHighLevelDecisionCache(currentStep, highLevelActionData, lastStepTargetBinding);
        }

        ApplyHighLevelIntentToNavigationDecision(ref navDecision, highLevelActionData, lastStepTargetBinding);
        string agentId = agentProperties != null ? agentProperties.AgentID : gameObject.name;
        Debug.Log($"[ActionDecision][Stage1][{agentId}] step={currentStep} reused={(reusedHighLevelDecision ? 1 : 0)} target={(lastStepTargetBinding.HasTarget ? lastStepTargetBinding.Summary : "none")} nav={BuildNavigationPromptSummary(navDecision)}");

        StepIntentDefinition stepIntent = ResolveEffectiveStepIntent(currentStep, highLevelActionData, lastStepTargetBinding);
        RoutePolicyDefinition routePolicy = ResolveEffectiveRoutePolicy();
        TeamCoordinationDirective[] coordinationDirectives = planningModule != null ? planningModule.GetCurrentCoordinationDirectives() : new TeamCoordinationDirective[0];
        Debug.Log($"[ActionDecision][Policy][{agentId}] intent={BuildStepIntentSummary(stepIntent)} routePolicy={BuildRoutePolicySummary(routePolicy)}");

        CoarsePathContext coarsePath = BuildCoarsePathContextForStep(currentStep, navDecision, lastStepTargetBinding, stepIntent, routePolicy);
        List<ActionCommand> actionSequence = BuildActionCommandsFromActionData(highLevelActionData);
        actionSequence = ApplyRoutePolicyToActionSequence(actionSequence, stepIntent, routePolicy);
        actionSequence = ApplyCoordinationDirectivesToActionSequence(actionSequence, coordinationDirectives);

        actionSequence = ApplyCoarsePathFallbackIfNeeded(actionSequence, coarsePath);
        actionSequence = ExpandMoveActionsByAStar(actionSequence, navDecision);
        lastIssuedSequenceContainsMovement = SequenceContainsMovement(actionSequence);

        if (actionSequence != null && actionSequence.Count > 0)
        {
            agentState.Status = AgentStatus.ExecutingTask;
            StartCoroutine(ExecuteActionSequence(actionSequence, currentStep));
        }
        else
        {
            ExecuteDefaultAction();
        }
    }

    // 获取小节点感知摘要（基于 DetectedSmallNodes）
    private string GetPerceptionSmallNodeSummary()
    {
        if (agentState == null) return "agentState 未初始化";
        if (agentState.DetectedSmallNodes == null || agentState.DetectedSmallNodes.Count == 0)
            return "尚未检测到小节点";

        int resourceCount = 0, obstacleCount = 0, dynamicCount = 0;
        Dictionary<SmallNodeType, int> typeCounter = new Dictionary<SmallNodeType, int>();
        List<SmallNodeData> validNodes = new List<SmallNodeData>();

        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            SmallNodeData node = agentState.DetectedSmallNodes[i];
            if (node == null) continue;
            validNodes.Add(node);

            if (node.NodeType == SmallNodeType.ResourcePoint) resourceCount++;
            if (node.BlocksMovement) obstacleCount++;
            if (node.IsDynamic) dynamicCount++;
            if (!typeCounter.ContainsKey(node.NodeType)) typeCounter[node.NodeType] = 0;
            typeCounter[node.NodeType]++;
        }

        validNodes = validNodes
            .OrderBy(n => Vector3.Distance(transform.position, n.WorldPosition))
            .Take(6)
            .ToList();

        List<string> samples = new List<string>();
        for (int i = 0; i < validNodes.Count; i++)
        {
            SmallNodeData node = validNodes[i];
            string name = string.IsNullOrWhiteSpace(node.DisplayName) ? node.NodeType.ToString() : node.DisplayName;
            string id = string.IsNullOrWhiteSpace(node.NodeId) ? "no-id" : node.NodeId;
            float dist = Vector3.Distance(transform.position, node.WorldPosition);
            samples.Add($"{name}|id={id}|type={node.NodeType}|pos=({node.WorldPosition.x:F1},{node.WorldPosition.z:F1})|d={dist:F1}m|dyn={(node.IsDynamic ? 1 : 0)}|blk={(node.BlocksMovement ? 1 : 0)}");
        }

        string typeSummary = string.Join(", ", typeCounter.Select(kv => $"{kv.Key}:{kv.Value}"));
        return $"总数:{agentState.DetectedSmallNodes.Count},资源:{resourceCount},障碍:{obstacleCount},动态:{dynamicCount}\n类型统计:{typeSummary}\n最近节点:{(samples.Count > 0 ? string.Join(" ; ", samples) : "无")}";
    }

    /// <summary>
    /// 结构化局部导航包：把“当前位置/目标格/局部动态障碍”显式给到 LLM。
    /// 说明：LLM 不需要自己构造网格，只需在动作意图中使用这些字段。
    /// </summary>
    private string BuildLocalNavigationPacket(StepTargetBinding binding)
    {
        if (campusGrid == null || campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null)
            return "CampusGrid2D 不可用";

        if (!TryResolveWalkableCell(transform.position, out Vector2Int meCell))
            return "当前位置不可映射到可通行网格";

        string goalCellText = "none";
        string goalWorldText = "none";
        if (binding.HasTarget)
        {
            Vector2Int gc = binding.GridCell;
            if (gc.x < 0 || !campusGrid.IsInBounds(gc.x, gc.y))
            {
                if (TryResolveWalkableCell(binding.WorldPos, out Vector2Int g2)) gc = g2;
            }
            if (gc.x >= 0 && campusGrid.IsInBounds(gc.x, gc.y))
            {
                goalCellText = $"({gc.x},{gc.y})";
            }
            goalWorldText = $"({binding.WorldPos.x:F1},{binding.WorldPos.z:F1})";
        }

        List<string> dynObs = new List<string>();
        if (agentState?.DetectedSmallNodes != null)
        {
            List<SmallNodeData> obstacles = agentState.DetectedSmallNodes
                .Where(n => n != null && n.BlocksMovement)
                .OrderBy(n => Vector3.Distance(transform.position, n.WorldPosition))
                .Take(8)
                .ToList();

            for (int i = 0; i < obstacles.Count; i++)
            {
                SmallNodeData n = obstacles[i];
                if (!TryResolveWalkableCell(n.WorldPosition, out Vector2Int c))
                {
                    c = campusGrid.WorldToGrid(n.WorldPosition);
                }
                string id = string.IsNullOrWhiteSpace(n.NodeId) ? "no-id" : n.NodeId;
                dynObs.Add($"{id}@({c.x},{c.y})");
            }
        }

        return $"meCell=({meCell.x},{meCell.y}),meWorld=({transform.position.x:F1},{transform.position.z:F1}),goalCell={goalCellText},goalWorld={goalWorldText},cell={campusGrid.cellSize:F1}m,dynObs=[{string.Join(",", dynObs)}]";
    }

    /// <summary>
    /// 获取“当前智能体可见地点”摘要（建筑优先）。
    /// 说明：该信息来自 PerceptionModule 同步到 AgentDynamicState 的 DetectedCampusFeatures，
    /// 不是全图静态地图导出。
    /// </summary>
    private string GetPerceivedCampusFeatureSummary()
    {
        if (agentState == null) return "agentState 未初始化";
        if (agentState.DetectedCampusFeatures == null || agentState.DetectedCampusFeatures.Count == 0)
            return "尚未感知到地点信息";

        List<CampusFeaturePerceptionData> features = agentState.DetectedCampusFeatures
            .Where(f => f != null && !string.IsNullOrWhiteSpace(f.FeatureName))
            .OrderBy(f =>
            {
                Vector3 d = f.AnchorWorldPosition - transform.position;
                d.y = 0f;
                return d.sqrMagnitude;
            })
            .Take(8)
            .ToList();

        if (features.Count == 0) return "尚未感知到地点信息";

        List<string> lines = new List<string>(features.Count);
        for (int i = 0; i < features.Count; i++)
        {
            CampusFeaturePerceptionData f = features[i];
            Vector3 d = f.AnchorWorldPosition - transform.position;
            d.y = 0f;
            float dist = d.magnitude;
            string uid = string.IsNullOrWhiteSpace(f.FeatureUid) ? "-" : f.FeatureUid;
            lines.Add(
                $"{f.FeatureName}|kind={f.FeatureKind}|uid={uid}|anchor=({f.AnchorWorldPosition.x:F1},{f.AnchorWorldPosition.z:F1})|cells={Mathf.Max(f.ObservedCellCount, f.ObservedSampleCells?.Count ?? 0)}|d={dist:F1}m"
            );
        }

        return $"已见地点:{agentState.DetectedCampusFeatures.Count}\n- " + string.Join("\n- ", lines);
    }

    // 简洁版本的附近队友信息
    private string GetNearbyAgentsSummary()
    {
        if (agentState?.NearbyAgents == null || agentState.NearbyAgents.Count == 0)
            return "附近无其他智能体";

        string result = $"附近队友:{agentState.NearbyAgents.Count}个\n";
        
        foreach (var kv in agentState.NearbyAgents)
        {
            GameObject agentObj = kv.Value;
            if (agentObj != null)
            {
                var agentComp = agentObj.GetComponent<IntelligentAgent>();
                if (agentComp != null)
                {
                    Vector3 relativePos = agentObj.transform.position - transform.position;
                    Vector3 p = agentObj.transform.position;
                    result += $"- {agentComp.Properties.AgentID}({agentComp.Properties.Role}) pos=({p.x:F1},{p.z:F1}) d={relativePos.magnitude:F1}m\n";
                }
                else
                {
                    Vector3 p = agentObj.transform.position;
                    result += $"- {kv.Key} pos=({p.x:F1},{p.z:F1})\n";
                }
            }
        }

        return result;
    }
    // 判断动作类型是否需要目标 - 核心逻辑
    private bool IsNoTargetAction(PrimitiveActionType actionType)
    {
        // 明确列出所有不需要目标的动作类型
        switch (actionType)
        {
            case PrimitiveActionType.TakeOff:
            case PrimitiveActionType.Hover:
            case PrimitiveActionType.Stop:
            case PrimitiveActionType.Scan:
            case PrimitiveActionType.Land:
                return true;
            default:
                return false;
        }
    }
    
    // ==================== 决策上下文构建方法 ====================

    // 构建决策上下文
    private string BuildDecisionContext()
    {
        if (agentState == null || agentProperties == null) return "状态缺失";
        Vector3 p = agentState.Position;
        return $"id={agentProperties.AgentID},team={agentState.TeamID},status={agentState.Status},bat={agentState.BatteryLevel:F1}/{agentProperties.BatteryCapacity:F1},pos=({p.x:F1},{p.z:F1}),v={agentState.Velocity.magnitude:F1},load={agentState.CurrentLoad:F1}/{agentProperties.PayloadCapacity:F1}";
    }

    // 获取任务摘要
    private string GetTaskSummary()
    {
        Plan currentPlan = planningModule?.GetCurrentTask();
        
        if (currentPlan == null || string.IsNullOrEmpty(currentPlan.mission))
            return "当前任务计划: 无任务";
        
        // 获取当前步骤描述
        string currentStepDesc = "任务完成";
        if (currentPlan.currentStep < currentPlan.steps.Length)
        {
            currentStepDesc = currentPlan.steps[currentPlan.currentStep];
        }
        
        return $"mission={currentPlan.mission},role={currentPlan.agentRole},step={currentStepDesc},progress={currentPlan.currentStep + 1}/{currentPlan.steps.Length},priority={currentPlan.priority},by={currentPlan.assignedBy}";
    }

    /// <summary>
    /// 高层阶段给出明确移动意图时，允许当前 step 进入路径规划链路。
    /// 这里只决定“是否进入导航管线”，不决定具体轨迹。
    /// 具体轨迹仍由第二阶段短程 waypoint 或 A* reactive segment 给出。
    /// </summary>
    private void ApplyHighLevelIntentToNavigationDecision(
        ref StepNavigationDecision navDecision,
        List<ActionData> highLevelActionData,
        StepTargetBinding binding)
    {
        if (navDecision.IsMovementStep) return;
        if (!HasMovementLikeAction(highLevelActionData)) return;

        navDecision.IsMovementStep = true;
        if (binding.HasTarget && (binding.TargetKind == "Feature" || binding.TargetKind == "World"))
        {
            navDecision.AllowAStarByStep = true;
        }
        navDecision.Reason += "|override=highLevelMoveIntent";
    }

    /// <summary>
    /// 给 LLM 的“步骤主目标锚点”摘要。
    /// </summary>
    private static string BuildStepTargetBindingSummary(StepTargetBinding binding)
    {
        return binding.HasTarget ? binding.Summary : "none";
    }

    private string BuildHighLevelActionSummary(List<ActionData> actionDataList)
    {
        if (actionDataList == null || actionDataList.Count == 0) return "none";
        List<string> parts = new List<string>();
        for (int i = 0; i < actionDataList.Count; i++)
        {
            ActionData a = actionDataList[i];
            if (a == null) continue;
            string actionType = string.IsNullOrWhiteSpace(a.actionType) ? "Unknown" : a.actionType.Trim();
            string target = string.IsNullOrWhiteSpace(a.target) ? "none" : a.target.Trim();
            string parameters = string.IsNullOrWhiteSpace(a.parameters) ? "{}" : a.parameters.Trim();
            parts.Add($"{i + 1}:{actionType}|target={target}|parameters={parameters}");
        }
        return parts.Count > 0 ? string.Join("\n- ", parts) : "none";
    }

    private bool TryGetCachedHighLevelDecision(string currentStep, out List<ActionData> actionDataList, out StepTargetBinding binding)
    {
        actionDataList = null;
        binding = default(StepTargetBinding);

        if (!reuseHighLevelDecisionForSameStep) return false;
        if (string.IsNullOrWhiteSpace(currentStep)) return false;
        if (!string.Equals(cachedHighLevelStep, currentStep, System.StringComparison.Ordinal)) return false;
        if (cachedHighLevelActionData == null || cachedHighLevelActionData.Count == 0) return false;
        if (!ShouldReuseCachedHighLevelBinding(cachedHighLevelTargetBinding)) return false;

        actionDataList = cachedHighLevelActionData;
        binding = cachedHighLevelTargetBinding;
        return true;
    }

    private void UpdateHighLevelDecisionCache(string currentStep, List<ActionData> actionDataList, StepTargetBinding binding)
    {
        if (!reuseHighLevelDecisionForSameStep)
        {
            cachedHighLevelStep = string.Empty;
            cachedHighLevelActionData = null;
            cachedHighLevelTargetBinding = default(StepTargetBinding);
            return;
        }

        cachedHighLevelStep = currentStep ?? string.Empty;
        cachedHighLevelActionData = actionDataList;
        cachedHighLevelTargetBinding = binding;
    }

    private static bool ShouldReuseCachedHighLevelBinding(StepTargetBinding binding)
    {
        if (!binding.HasTarget) return false;

        switch (binding.TargetKind)
        {
            case "Feature":
            case "World":
            case "Self":
                return true;
            case "SmallNode":
                return binding.Source == null ||
                       binding.Source.IndexOf("Dynamic", System.StringComparison.OrdinalIgnoreCase) < 0;
            default:
                return false;
        }
    }

    private bool ShouldPreferDeterministicStaticNavigation(StepTargetBinding binding, CoarsePathContext coarsePath)
    {
        if (!skipWaypointRefinementForStaticNavigation) return false;
        if (!binding.HasTarget || !coarsePath.HasPath) return false;
        if (binding.TargetKind != "Feature" && binding.TargetKind != "World") return false;
        if (HasNearbyBlockingOrDynamicSmallNodes(deterministicNavigationObstacleRadius)) return false;
        return true;
    }

    private bool HasNearbyBlockingOrDynamicSmallNodes(float radius)
    {
        if (radius <= 0f) return false;
        if (agentState?.DetectedSmallNodes == null || agentState.DetectedSmallNodes.Count == 0) return false;

        float radiusSq = radius * radius;
        Vector3 me = transform.position;
        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            SmallNodeData node = agentState.DetectedSmallNodes[i];
            if (node == null) continue;
            if (!node.BlocksMovement && !node.IsDynamic) continue;

            Vector3 delta = node.WorldPosition - me;
            delta.y = 0f;
            if (delta.sqrMagnitude <= radiusSq)
            {
                return true;
            }
        }

        return false;
    }

    private string GetNearbyObstacleSmallNodeSummary()
    {
        if (agentState?.DetectedSmallNodes == null || agentState.DetectedSmallNodes.Count == 0)
            return "无局部小节点";

        List<SmallNodeData> candidates = agentState.DetectedSmallNodes
            .Where(n => n != null && (n.BlocksMovement || n.IsDynamic))
            .OrderBy(n => Vector3.Distance(transform.position, n.WorldPosition))
            .Take(8)
            .ToList();

        if (candidates.Count == 0) return "无阻塞或动态小节点";

        List<string> lines = new List<string>();
        for (int i = 0; i < candidates.Count; i++)
        {
            SmallNodeData n = candidates[i];
            string id = string.IsNullOrWhiteSpace(n.NodeId) ? "-" : n.NodeId;
            string name = string.IsNullOrWhiteSpace(n.DisplayName) ? n.NodeType.ToString() : n.DisplayName;
            float d = Vector3.Distance(transform.position, n.WorldPosition);
            lines.Add($"{name}|id={id}|type={n.NodeType}|pos=({n.WorldPosition.x:F1},{n.WorldPosition.z:F1})|d={d:F1}m|dyn={(n.IsDynamic ? 1 : 0)}|blk={(n.BlocksMovement ? 1 : 0)}");
        }

        return "- " + string.Join("\n- ", lines);
    }

    // 获取记忆摘要
    private string GetMemorySummary()
    {
        var memories = memoryModule.RetrieveRelevantMemories("current situation", 2);
        if (memories == null || memories.Count == 0) return "无相关历史经验";
        
        string summary = "经验:";
        foreach (var memory in memories)
        {
            summary += $" | {memory.description}";
        }
        return summary;
    }

    // ==================== 动作类型和消息类型辅助方法 ====================

    // 获取可用移动动作列表
    private string GetAvailableMoveActions()
    {
        var moveActions = new List<PrimitiveActionType>
        {
            PrimitiveActionType.MoveTo,
            PrimitiveActionType.Stop,
            PrimitiveActionType.TakeOff,
            PrimitiveActionType.Land,
            PrimitiveActionType.Hover,
            PrimitiveActionType.RotateTo,
            PrimitiveActionType.AdjustAltitude,
            PrimitiveActionType.Orbit,
            PrimitiveActionType.Follow,
            PrimitiveActionType.Align
        };
        return string.Join(", ", moveActions);
    }

    // 获取可用交互动作列表
    private string GetAvailableInteractActions()
    {
        var interactActions = new List<PrimitiveActionType>
        {
            PrimitiveActionType.PickUp,
            PrimitiveActionType.Drop
        };
        return string.Join(", ", interactActions);
    }

    // 获取可用观察动作列表
    private string GetAvailableObserveActions()
    {
        var observeActions = new List<PrimitiveActionType>
        {
            PrimitiveActionType.LookAt,
            PrimitiveActionType.Scan
        };
        return string.Join(", ", observeActions);
    }

    // 获取可用消息类型
    private string GetAvailableMessageTypes()
    {
        List<string> availableTypes = new List<string>();
        foreach (MessageType messageType in System.Enum.GetValues(typeof(MessageType)))
        {
            availableTypes.Add(messageType.ToString());
        }
        return string.Join(", ", availableTypes);
    }

    // ==================== 决策解析和执行方法 ====================

    /// <summary>
    /// 对动作序列进行“路径点展开”。
    /// 优先级：
    /// 1) 可选：使用 LLM 在 parameters 中给出的 waypoint（禁用长 waypoint 时会忽略）；
    /// 2) 默认：按“动作类型+目标类型+step策略”回退 CampusGrid2D A*，并只下发短段子目标；
    /// 3) 任一阶段失败都回退原始 MoveTo，保证鲁棒性。
    /// </summary>
    private List<ActionCommand> ExpandMoveActionsByAStar(List<ActionCommand> inputActions, StepNavigationDecision navDecision)
    {
        if (inputActions == null || inputActions.Count == 0) return inputActions;

        List<ActionCommand> expanded = new List<ActionCommand>(inputActions.Count);
        bool expandedAny = false;
        Vector3 virtualStart = transform.position;

        for (int i = 0; i < inputActions.Count; i++)
        {
            ActionCommand action = inputActions[i];
            if (action == null) continue;

            if (action.ActionType != PrimitiveActionType.MoveTo)
            {
                expanded.Add(action);
                continue;
            }

            // 第一优先级：系统粗路径。
            // 这类路径来自结构化 viaTargets/target 计算结果，必须优先落到执行层。
            if (TryBuildSystemWaypointsFromParameters(action, out List<Vector3> systemWaypoints))
            {
                AppendWaypointsAsMoveActions(action, systemWaypoints, expanded, "SystemCoarsePath");
                virtualStart = systemWaypoints[systemWaypoints.Count - 1];
                expandedAny = true;
                continue;
            }

            // 第二优先级（可选）：LLM 显式给 waypoint
            if (preferLlmWaypoints && !disableLlmLongWaypoints &&
                TryBuildLlmWaypointsFromParameters(action, out List<Vector3> llmWaypoints))
            {
                AppendWaypointsAsMoveActions(action, llmWaypoints, expanded, "LLMPath");
                virtualStart = llmWaypoints[llmWaypoints.Count - 1];
                expandedAny = true;
                continue;
            }

            // 第三优先级：A* 兜底
            if (!allowAStarFallbackWhenNoLlmWaypoints || !enableAStarPathExpansion)
            {
                expanded.Add(action);
                virtualStart = action.TargetPosition;
                continue;
            }

            if (campusGrid == null)
            {
                expanded.Add(action);
                virtualStart = action.TargetPosition;
                continue;
            }

            if (!ShouldUseAStarForMoveAction(action, virtualStart, navDecision))
            {
                expanded.Add(action);
                virtualStart = action.TargetPosition;
                continue;
            }

            if (TryBuildAStarWaypoints(virtualStart, action.TargetPosition, out List<Vector3> astarWaypoints))
            {
                List<Vector3> segment = BuildReactiveWaypointSegment(astarWaypoints);
                AppendWaypointsAsMoveActions(action, segment, expanded, "AStarReactive");
                virtualStart = segment[segment.Count - 1];
                expandedAny = true;
            }
            else
            {
                expanded.Add(action);
                virtualStart = action.TargetPosition;
            }
        }

        if (expandedAny)
        {
            string agentId = agentProperties != null ? agentProperties.AgentID : gameObject.name;
            Debug.Log($"[ActionDecision][PathExpand][{agentId}] 路径点展开完成: 原动作={inputActions.Count}, 展开后={expanded.Count}");
            return expanded;
        }
        return inputActions;
    }

    /// <summary>
    /// A* 全路径只取短前缀，执行后再进入下一轮 DecideNextAction 进行滚动重规划。
    /// </summary>
    private List<Vector3> BuildReactiveWaypointSegment(List<Vector3> fullPath)
    {
        if (fullPath == null || fullPath.Count == 0) return new List<Vector3>();
        int n = ResolveReactiveSegmentWaypointCount(fullPath.Count);
        return fullPath.Take(n).ToList();
    }

    private int ResolveReactiveSegmentWaypointCount(int fullPathCount)
    {
        if (fullPathCount <= 0) return 0;

        int baseCount = Mathf.Clamp(reactiveAStarSegmentWaypoints, 1, fullPathCount);
        if (lastStepTargetBinding.TargetKind != "Feature" && lastStepTargetBinding.TargetKind != "World")
        {
            return baseCount;
        }

        if (HasNearbyBlockingOrDynamicSmallNodes(deterministicNavigationObstacleRadius))
        {
            return baseCount;
        }

        int fastCount = Mathf.Max(baseCount, fastStaticReactiveSegmentWaypoints);
        return Mathf.Clamp(fastCount, 1, fullPathCount);
    }

    /// <summary>
    /// 从 LLM 的 parameters 中提取 waypoint 列表。
    /// 支持格式：
    /// 1) waypoints:10 20;14 26;18 32
    /// 2) waypoints:10 2 20;14 2 26
    /// 3) waypoints:world(10,20)|world(14,26)
    /// </summary>
    private bool TryBuildLlmWaypointsFromParameters(ActionCommand moveAction, out List<Vector3> waypoints)
    {
        waypoints = new List<Vector3>();
        if (moveAction == null) return false;

        string raw = ExtractWaypointsRawValue(moveAction.Parameters);
        if (string.IsNullOrWhiteSpace(raw))
        {
            Dictionary<string, string> pmap = ParseLooseParameterMap(moveAction.Parameters);
            if (!pmap.TryGetValue("waypoints", out raw) || string.IsNullOrWhiteSpace(raw)) return false;
        }

        string s = raw.Trim().Trim('\"', '\'');
        string[] tokens = s.Split(new[] { ';', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        float defaultY = ResolveWaypointY(moveAction.TargetPosition);
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i].Trim();
            if (string.IsNullOrWhiteSpace(t)) continue;

            if (TryParseWorldTarget(t, out Vector3 wp))
            {
                if (Mathf.Abs(wp.y) < 0.001f) wp.y = defaultY;
                waypoints.Add(wp);
                continue;
            }

            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                t,
                @"^\s*(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)(?:\s+(-?\d+(?:\.\d+)?))?\s*$"
            );
            if (!m.Success) continue;

            bool okX = float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
            bool ok2 = float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float second);
            if (!okX || !ok2) continue;

            float y = defaultY;
            float z = second;
            if (m.Groups[3].Success &&
                float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float third))
            {
                y = second;
                z = third;
            }

            waypoints.Add(new Vector3(x, y, z));
        }

        if (waypoints.Count == 0) return false;

        // 保证最后落到 MoveTo 的目标点附近，避免“路径点到一半停住”
        Vector3 finalTarget = moveAction.TargetPosition;
        if (Vector3.Distance(waypoints[waypoints.Count - 1], finalTarget) > 1.0f)
        {
            waypoints.Add(finalTarget);
        }

        int maxCount = Mathf.Max(1, astarMaxWaypoints);
        if (waypoints.Count > maxCount)
        {
            waypoints = DownsampleWaypoints(waypoints, maxCount);
        }

        return waypoints.Count > 0;
    }

    /// <summary>
    /// 解析系统生成的粗路径 waypoint。
    /// 这些 waypoint 来自系统基于结构化 target/viaTargets 计算出的粗路径，
    /// 不是 LLM 自由发挥的长路径，因此即使关闭了 LLM 长 waypoint，也必须允许执行。
    /// </summary>
    private bool TryBuildSystemWaypointsFromParameters(ActionCommand moveAction, out List<Vector3> waypoints)
    {
        waypoints = new List<Vector3>();
        if (moveAction == null) return false;

        Dictionary<string, string> pmap = ParseLooseParameterMap(moveAction.Parameters);
        if (!pmap.TryGetValue("segmentSource", out string segmentSource) ||
            !string.Equals(segmentSource, "SystemCoarsePath", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!pmap.TryGetValue("systemWaypoints", out string systemWaypoints) || string.IsNullOrWhiteSpace(systemWaypoints))
        {
            return false;
        }

        ActionCommand shadow = new ActionCommand
        {
            ActionType = moveAction.ActionType,
            TargetObject = moveAction.TargetObject,
            TargetPosition = moveAction.TargetPosition,
            TargetRotation = moveAction.TargetRotation,
            Parameters = $"waypoints:{systemWaypoints}"
        };

        return TryBuildLlmWaypointsFromParameters(shadow, out waypoints);
    }

    /// <summary>
    /// 当 LLM 未输出 waypoint 且已有粗路径时，把粗路径注入到第一条 MoveTo。
    /// 作用：保证“先A*粗路径，再局部细化”的链路在异常输出下仍可执行。
    /// </summary>
    private List<ActionCommand> ApplyCoarsePathFallbackIfNeeded(List<ActionCommand> actions, CoarsePathContext coarsePath)
    {
        if (actions == null || actions.Count == 0) return actions;
        if (!coarsePath.HasPath || coarsePath.CoarseWaypoints == null || coarsePath.CoarseWaypoints.Count == 0) return actions;

        for (int i = 0; i < actions.Count; i++)
        {
            ActionCommand a = actions[i];
            if (a == null || a.ActionType != PrimitiveActionType.MoveTo) continue;

            Dictionary<string, string> existingMap = ParseLooseParameterMap(a.Parameters);
            bool hasViaTargets = existingMap.TryGetValue("viaTargets", out string viaTargets) && !string.IsNullOrWhiteSpace(viaTargets);
            bool requiresApproachSide = existingMap.TryGetValue("approachSide", out string approachSide) &&
                                        !string.IsNullOrWhiteSpace(approachSide) &&
                                        !string.Equals(approachSide, "Any", System.StringComparison.OrdinalIgnoreCase);
            bool isFeatureTarget = existingMap.TryGetValue("targetKind", out string targetKind) &&
                                   string.Equals(targetKind, "Feature", System.StringComparison.OrdinalIgnoreCase);
            bool mustRespectStructuredRoute = hasViaTargets || requiresApproachSide || isFeatureTarget || coarsePath.CoarseWaypoints.Count > 2;

            if (existingMap.TryGetValue("pathMode", out string existingMode) &&
                string.Equals(existingMode, "LLMReactiveSegment", System.StringComparison.OrdinalIgnoreCase))
            {
                return actions;
            }
            if (existingMap.TryGetValue("segmentSource", out string segmentSource) &&
                (string.Equals(segmentSource, "LLMReactiveSegment", System.StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(segmentSource, "SystemCoarsePath", System.StringComparison.OrdinalIgnoreCase)))
            {
                return actions;
            }

            if (!disableLlmLongWaypoints &&
                TryBuildLlmWaypointsFromParameters(a, out List<Vector3> existing) && existing.Count > 0)
            {
                return actions;
            }

            if (!mustRespectStructuredRoute &&
                existingMap.TryGetValue("pathMode", out existingMode) &&
                string.Equals(existingMode, "Direct", System.StringComparison.OrdinalIgnoreCase))
            {
                return actions;
            }

            Dictionary<string, string> inject = new Dictionary<string, string>
            {
                ["pathMode"] = "AStarHint",
                ["targetFeature"] = coarsePath.TargetFeatureName,
                ["segmentSource"] = "SystemCoarsePath",
                ["systemWaypoints"] = coarsePath.WaypointsInline
            };

            a.Parameters = MergeParameters(a.Parameters, inject);
            if (a.TargetPosition == transform.position || Vector3.Distance(a.TargetPosition, transform.position) < 0.5f)
            {
                a.TargetPosition = coarsePath.GoalWorld;
            }
            return actions;
        }

        return actions;
    }

    private List<ActionCommand> ApplyRoutePolicyToActionSequence(List<ActionCommand> actions, StepIntentDefinition stepIntent, RoutePolicyDefinition routePolicy)
    {
        if (actions == null || actions.Count == 0 || routePolicy == null) return actions;

        string viaTargets = stepIntent != null && stepIntent.orderedViaTargets != null && stepIntent.orderedViaTargets.Length > 0
            ? string.Join("|", stepIntent.orderedViaTargets)
            : string.Empty;
        string avoidNodeTypes = routePolicy.avoidNodeTypes != null && routePolicy.avoidNodeTypes.Length > 0
            ? string.Join("|", routePolicy.avoidNodeTypes.Select(t => t.ToString()).ToArray())
            : string.Empty;
        string avoidFeatures = routePolicy.avoidFeatureNames != null && routePolicy.avoidFeatureNames.Length > 0
            ? string.Join("|", routePolicy.avoidFeatureNames)
            : string.Empty;

        for (int i = 0; i < actions.Count; i++)
        {
            ActionCommand action = actions[i];
            if (action == null || action.ActionType != PrimitiveActionType.MoveTo) continue;

            Dictionary<string, string> policyMap = new Dictionary<string, string>
            {
                ["approachSide"] = routePolicy.approachSide.ToString(),
                ["altitudeMode"] = routePolicy.altitudeMode.ToString(),
                ["clearance"] = routePolicy.clearance.ToString(),
                ["keepTargetVisible"] = routePolicy.keepTargetVisible ? "1" : "0",
                ["preferOpenSpace"] = routePolicy.preferOpenSpace ? "1" : "0",
                ["allowLocalDetour"] = routePolicy.allowLocalDetour ? "1" : "0",
                ["slowNearTarget"] = routePolicy.slowNearTarget ? "1" : "0",
                ["holdForTeammates"] = routePolicy.holdForTeammates ? "1" : "0",
                ["blockedPolicy"] = routePolicy.blockedPolicy.ToString()
            };

            if (!string.IsNullOrWhiteSpace(viaTargets)) policyMap["viaTargets"] = viaTargets;
            if (!string.IsNullOrWhiteSpace(avoidNodeTypes)) policyMap["avoidNodeTypes"] = avoidNodeTypes;
            if (!string.IsNullOrWhiteSpace(avoidFeatures)) policyMap["avoidFeatureNames"] = avoidFeatures;
            if (routePolicy.maxTeammatesInCorridor > 0) policyMap["maxTeammatesInCorridor"] = routePolicy.maxTeammatesInCorridor.ToString();

            action.Parameters = MergeParameters(action.Parameters, policyMap);
        }

        return actions;
    }

    /// <summary>
    /// 把 PlanningModule 的结构化协同约束并入动作参数。
    /// 这样做的目的不是让系统重新解释协同语义，而是把上游已经定好的约束透明地下传给执行层/日志层。
    /// </summary>
    private List<ActionCommand> ApplyCoordinationDirectivesToActionSequence(List<ActionCommand> actions, TeamCoordinationDirective[] directives)
    {
        if (actions == null || actions.Count == 0 || directives == null || directives.Length == 0) return actions;

        string modes = string.Join("|", directives.Where(d => d != null).Select(d => d.coordinationMode.ToString()).Distinct().ToArray());
        string leaders = string.Join("|", directives.Where(d => d != null && !string.IsNullOrWhiteSpace(d.leaderAgentId)).Select(d => d.leaderAgentId).Distinct().ToArray());
        string sharedTargets = string.Join("|", directives.Where(d => d != null && !string.IsNullOrWhiteSpace(d.sharedTarget)).Select(d => d.sharedTarget).Distinct().ToArray());
        string corridors = string.Join("|", directives.Where(d => d != null && !string.IsNullOrWhiteSpace(d.corridorReservationKey)).Select(d => d.corridorReservationKey).Distinct().ToArray());
        string yields = string.Join("|", directives.Where(d => d != null && d.yieldToAgentIds != null).SelectMany(d => d.yieldToAgentIds).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray());
        string formations = string.Join("|", directives.Where(d => d != null && !string.IsNullOrWhiteSpace(d.formationSlot)).Select(d => d.formationSlot).Distinct().ToArray());

        for (int i = 0; i < actions.Count; i++)
        {
            ActionCommand action = actions[i];
            if (action == null) continue;

            Dictionary<string, string> coordinationMap = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(modes)) coordinationMap["coordinationModes"] = modes;
            if (!string.IsNullOrWhiteSpace(leaders)) coordinationMap["leaderAgentIds"] = leaders;
            if (!string.IsNullOrWhiteSpace(sharedTargets)) coordinationMap["sharedTargets"] = sharedTargets;
            if (!string.IsNullOrWhiteSpace(corridors)) coordinationMap["corridorReservationKeys"] = corridors;
            if (!string.IsNullOrWhiteSpace(yields)) coordinationMap["yieldToAgentIds"] = yields;
            if (!string.IsNullOrWhiteSpace(formations)) coordinationMap["formationSlots"] = formations;

            if (coordinationMap.Count > 0)
            {
                action.Parameters = MergeParameters(action.Parameters, coordinationMap);
            }
        }

        return actions;
    }

    /// <summary>
    /// 从原始参数文本中提取 waypoints 字段，优先支持带引号的整段文本。
    /// 这样可以兼容包含逗号的写法，例如：
    /// waypoints:"world(10,20);world(14,26)"
    /// </summary>
    private static string ExtractWaypointsRawValue(string rawParameters)
    {
        if (string.IsNullOrWhiteSpace(rawParameters)) return string.Empty;
        string s = rawParameters.Trim();

        System.Text.RegularExpressions.Match quoted = System.Text.RegularExpressions.Regex.Match(
            s,
            @"waypoints\s*[:=]\s*[""'](?<v>[^""']+)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (quoted.Success)
        {
            return quoted.Groups["v"].Value.Trim();
        }

        System.Text.RegularExpressions.Match plain = System.Text.RegularExpressions.Regex.Match(
            s,
            @"waypoints\s*[:=]\s*(?<v>.+?)(?=,\s*[A-Za-z_][A-Za-z0-9_]*\s*[:=]|$|\})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (plain.Success)
        {
            return plain.Groups["v"].Value.Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// 判断某条 MoveTo 是否满足 A* 展开条件。
    /// </summary>
    private bool ShouldUseAStarForMoveAction(ActionCommand moveAction, Vector3 startPos, StepNavigationDecision navDecision)
    {
        if (moveAction == null) return false;
        if (campusGrid == null) return false;

        Dictionary<string, string> pmap = ParseLooseParameterMap(moveAction.Parameters);
        if (pmap.TryGetValue("pathMode", out string mode) &&
            (string.Equals(mode, "Direct", System.StringComparison.OrdinalIgnoreCase) ||
             string.Equals(mode, "LLMReactiveSegment", System.StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // 动作显式要求 A* 时，直接使用。
        if (pmap.TryGetValue("pathMode", out mode))
        {
            if (string.Equals(mode, "AStar", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "AStarHint", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "CoarseAStar", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (pmap.TryGetValue("targetKind", out string targetKind) &&
            string.Equals(targetKind, "Agent", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (pmap.TryGetValue("targetKind", out targetKind) &&
            string.Equals(targetKind, "SmallNode", System.StringComparison.OrdinalIgnoreCase))
        {
            if (IsDynamicOrChasingTarget(pmap)) return false;
        }

        // 目标是静态地点时，默认走 A* 粗路径。
        if (pmap.TryGetValue("targetKind", out targetKind) &&
            string.Equals(targetKind, "Feature", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        float planarDist = Vector2.Distance(
            new Vector2(startPos.x, startPos.z),
            new Vector2(moveAction.TargetPosition.x, moveAction.TargetPosition.z)
        );
        if (planarDist < astarMinTriggerDistance) return false;

        // 普通 MoveTo 是否启用 A* 由 step 导航策略给出“允许”开关，不强制。
        return navDecision.IsMovementStep && navDecision.AllowAStarByStep;
    }

    private static bool IsDynamicOrChasingTarget(Dictionary<string, string> pmap)
    {
        if (pmap == null || pmap.Count == 0) return false;

        if (pmap.TryGetValue("targetDynamic", out string dynamicFlag) &&
            (dynamicFlag == "1" || string.Equals(dynamicFlag, "true", System.StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (pmap.TryGetValue("targetNodeType", out string nodeType))
        {
            if (string.Equals(nodeType, SmallNodeType.Pedestrian.ToString(), System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nodeType, SmallNodeType.Vehicle.ToString(), System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nodeType, SmallNodeType.Agent.ToString(), System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从世界坐标构建 A* waypoint 列表。
    /// </summary>
    private bool TryBuildAStarWaypoints(Vector3 startWorld, Vector3 targetWorld, out List<Vector3> waypoints)
    {
        waypoints = new List<Vector3>();
        if (campusGrid == null) return false;

        if (!TryResolveWalkableCell(startWorld, out Vector2Int startCell)) return false;
        if (!TryResolveWalkableCell(targetWorld, out Vector2Int goalCell)) return false;

        List<Vector2Int> gridPath = campusGrid.FindPathAStar(startCell, goalCell);
        if (gridPath == null || gridPath.Count < 2) return false;

        int stride = Mathf.Max(1, astarWaypointStride);
        float y = ResolveWaypointY(targetWorld);
        for (int i = stride; i < gridPath.Count; i += stride)
        {
            Vector2Int c = gridPath[i];
            Vector3 wp = campusGrid.GridToWorldCenter(c.x, c.y, astarWaypointYOffset);
            wp.y = y;
            waypoints.Add(wp);
        }

        Vector3 goalCenter = campusGrid.GridToWorldCenter(goalCell.x, goalCell.y, astarWaypointYOffset);
        goalCenter.y = y;

        Vector3 finalTarget = new Vector3(targetWorld.x, y, targetWorld.z);
        float targetToGoalCenter = Vector2.Distance(
            new Vector2(finalTarget.x, finalTarget.z),
            new Vector2(goalCenter.x, goalCenter.z)
        );
        if (targetToGoalCenter > campusGrid.cellSize * 0.75f)
        {
            finalTarget = goalCenter;
        }

        if (waypoints.Count == 0 || Vector3.Distance(waypoints[waypoints.Count - 1], finalTarget) > Mathf.Max(0.5f, campusGrid.cellSize * 0.5f))
        {
            waypoints.Add(finalTarget);
        }

        int maxCount = Mathf.Max(1, astarMaxWaypoints);
        if (waypoints.Count > maxCount)
        {
            waypoints = DownsampleWaypoints(waypoints, maxCount);
        }

        return waypoints.Count > 0;
    }

    /// <summary>
    /// 解析世界点对应的可通行网格。
    /// </summary>
    private bool TryResolveWalkableCell(Vector3 worldPos, out Vector2Int cell)
    {
        cell = new Vector2Int(-1, -1);
        if (campusGrid == null) return false;

        Vector2Int raw = campusGrid.WorldToGrid(worldPos);
        if (!campusGrid.IsInBounds(raw.x, raw.y)) return false;
        if (campusGrid.IsWalkable(raw.x, raw.y))
        {
            cell = raw;
            return true;
        }

        if (!astarPreferNearestWalkable) return false;
        if (campusGrid.TryFindNearestWalkable(raw, 6, out Vector2Int nearest))
        {
            cell = nearest;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 把 A* waypoint 列表转换为连续 MoveTo 动作。
    /// </summary>
    private void AppendWaypointsAsMoveActions(ActionCommand originalMove, List<Vector3> waypoints, List<ActionCommand> output, string pathMode)
    {
        if (originalMove == null || waypoints == null || output == null) return;
        if (waypoints.Count == 0)
        {
            output.Add(originalMove);
            return;
        }

        int total = waypoints.Count;
        for (int i = 0; i < waypoints.Count; i++)
        {
            bool isFinalWaypoint = i == total - 1;
            Dictionary<string, string> astarInfo = new Dictionary<string, string>
            {
                ["pathMode"] = string.IsNullOrWhiteSpace(pathMode) ? "Path" : pathMode,
                ["waypointIndex"] = (i + 1).ToString(),
                ["waypointTotal"] = total.ToString(),
                ["posTol"] = "1.8",
                ["heightTol"] = "0.6",
                ["horiTol"] = "1.2",
                ["vertTol"] = "0.7",
                ["settleTime"] = "0.05"
            };

            if (!isFinalWaypoint)
            {
                // 关键修复：
                // 中间 waypoint 的完成条件必须只看“当前 waypoint 是否到达”，
                // 不能沿用最终目标建筑的 targetArrivalRadius / 感知到达捷径。
                // 否则会出现“刚到检查点附近，就把后续 waypoint 和整个 step 一起判完成”的问题。
                astarInfo["useWaypointToleranceOnly"] = "1";
                astarInfo["disableFeaturePerceptionArrival"] = "1";
                astarInfo["targetArrivalRadius"] = "0";
            }

            ActionCommand waypointMove = new ActionCommand
            {
                ActionType = PrimitiveActionType.MoveTo,
                TargetPosition = waypoints[i],
                TargetRotation = originalMove.TargetRotation,
                TargetObject = (i == total - 1) ? originalMove.TargetObject : null,
                Parameters = MergeParameters(originalMove.Parameters, astarInfo)
            };

            output.Add(waypointMove);
        }
    }

    /// <summary>
    /// 控制 waypoint 数量，避免一次下发过长动作链。
    /// </summary>
    private static List<Vector3> DownsampleWaypoints(List<Vector3> source, int maxCount)
    {
        if (source == null) return new List<Vector3>();
        if (source.Count == 0) return new List<Vector3>();
        if (maxCount <= 1) return new List<Vector3> { source[source.Count - 1] };
        if (source.Count <= maxCount) return source;

        List<Vector3> result = new List<Vector3>(maxCount);
        for (int i = 0; i < maxCount; i++)
        {
            float t = (float)i / (maxCount - 1);
            int idx = Mathf.RoundToInt(t * (source.Count - 1));
            result.Add(source[idx]);
        }
        return result;
    }

    /// <summary>
    /// 压缩 waypoint 时保留关键索引。
    /// 典型场景是：
    /// 1) 起点；
    /// 2) viaTargets 对应的检查点；
    /// 3) 最终目标点。
    /// 这样即使为了减少动作数量做采样，也不会把结构化路线要求压掉。
    /// </summary>
    private static List<Vector3> DownsampleWaypointsPreserveIndices(List<Vector3> source, int maxCount, IEnumerable<int> preserveIndices, out List<int> keptSourceIndices)
    {
        keptSourceIndices = new List<int>();
        if (source == null || source.Count == 0) return new List<Vector3>();
        if (maxCount <= 1)
        {
            keptSourceIndices.Add(source.Count - 1);
            return new List<Vector3> { source[source.Count - 1] };
        }
        if (source.Count <= maxCount)
        {
            keptSourceIndices.AddRange(Enumerable.Range(0, source.Count));
            return new List<Vector3>(source);
        }

        List<int> preserved = (preserveIndices ?? Enumerable.Empty<int>())
            .Where(i => i >= 0 && i < source.Count)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (preserved.Count == 0)
        {
            List<Vector3> fallback = DownsampleWaypoints(source, maxCount);
            for (int i = 0; i < fallback.Count; i++)
            {
                float t = fallback.Count == 1 ? 1f : (float)i / (fallback.Count - 1);
                keptSourceIndices.Add(Mathf.RoundToInt(t * (source.Count - 1)));
            }
            return fallback;
        }

        if (preserved.Count > maxCount)
        {
            preserved = preserved.Take(maxCount).ToList();
        }

        int remainingSlots = maxCount - preserved.Count;
        List<int> candidates = Enumerable.Range(0, source.Count)
            .Where(i => !preserved.Contains(i))
            .ToList();

        HashSet<int> selected = new HashSet<int>(preserved);
        for (int i = 0; i < remainingSlots && candidates.Count > 0; i++)
        {
            float t = remainingSlots == 1 ? 0.5f : (float)i / (remainingSlots - 1);
            int idx = Mathf.RoundToInt(t * (candidates.Count - 1));
            selected.Add(candidates[idx]);
        }

        List<int> ordered = selected.OrderBy(i => i).Take(maxCount).ToList();
        keptSourceIndices.AddRange(ordered);
        return ordered.Select(i => source[i]).ToList();
    }

    /// <summary>
    /// 统一 waypoint 高度：
    /// - 无人机优先保持目标高度（若目标高度无效则保持当前高度）；
    /// - 地面体保持当前高度。
    /// </summary>
    private float ResolveWaypointY(Vector3 targetWorld)
    {
        if (agentProperties != null && agentProperties.Type == AgentType.Quadcopter)
        {
            if (targetWorld.y > 0.1f) return targetWorld.y;
            return Mathf.Max(transform.position.y, 2f);
        }
        return transform.position.y;
    }

    /// <summary>
    /// 只解析动作数据，不做目标坐标绑定。
    /// 用于第一阶段高层语义解析和第二阶段局部 waypoint 细化。
    /// </summary>
    private List<ActionData> ParseActionDataSequenceFromJSON(string jsonResponse)
    {
        string cleanJson = ExtractPureJson(jsonResponse);
        if (string.IsNullOrWhiteSpace(cleanJson))
        {
            Debug.LogError("无法从响应中提取 JSON 内容");
            return null;
        }

        List<ActionData> actionDataList = TryParseActionDataListStrict(cleanJson);
        if (actionDataList == null || actionDataList.Count == 0)
        {
            actionDataList = TryParseActionDataListResilient(cleanJson);
        }

        if (actionDataList == null || actionDataList.Count == 0)
        {
            Debug.LogError($"JSON解析失败: 无法从响应中提取有效动作对象。\n原始响应: {jsonResponse}");
            return null;
        }

        return actionDataList;
    }

    /// <summary>
    /// 第一阶段：只做高层动作和 target 语义解析。
    /// 不要求 LLM 输出具体 world waypoint，避免在动态环境下给死整条路径。
    /// </summary>
    private string BuildHighLevelDecisionPrompt(string currentStep, StepNavigationDecision navDecision, StepTargetBinding structuredBinding)
    {
        string role = agentProperties != null ? agentProperties.Role.ToString() : "Unknown";
        StepIntentDefinition stepIntent = planningModule != null ? planningModule.GetCurrentStepIntent() : null;
        RoutePolicyDefinition routePolicy = planningModule != null ? planningModule.GetCurrentStepRoutePolicy() : null;
        TeamCoordinationDirective[] directives = planningModule != null ? planningModule.GetCurrentCoordinationDirectives() : new TeamCoordinationDirective[0];
        string assignedSlotSummary = planningModule != null ? planningModule.GetCurrentAssignedSlotSummary() : "none";
        string teammateSummary = BuildTeammateReferenceSummary(stepIntent, directives);

        return $@"你是{agentProperties?.Type}智能体，角色={role}。
        基于 PlanningModule 已给出的结构化执行上下文，输出当前这一轮高层动作决策。
        这一阶段只决定“做什么动作”，不要重新拆任务，不要重新解释目标语义。

        [当前步骤]
        {currentStep}

        [当前槽位]
        {assignedSlotSummary}

        [结构化主目标绑定]
        {BuildStepTargetBindingSummary(structuredBinding)}

        [结构化step意图]
        {BuildStepIntentSummary(stepIntent)}

        [结构化路径策略]
        {BuildRoutePolicySummary(routePolicy)}

        [结构化协同约束]
        {BuildCoordinationPromptSummary(directives)}

        [允许引用的队友ID]
        {teammateSummary}

        [状态]
        {BuildDecisionContext()}

        [任务]
        {GetTaskSummary()}

        [历史经验]
        {GetMemorySummary()}

        [导航策略]
        {BuildNavigationPromptSummary(navDecision)}

        [可用动作]
        Move: {GetAvailableMoveActions()}
        Interact: {GetAvailableInteractActions()}
        Observe: {GetAvailableObserveActions()}
        Comm: TransmitMessage(messageType in {GetAvailableMessageTypes()})

        [高层决策规则]
        1) 只输出高层动作。
        2) 如果动作针对当前步骤的主目标，target 优先写 ""step_target""。
        3) 只有在 [结构化主目标绑定] 为 none 时，才允许输出明确 world(x,z) 或 world(x,y,z)。
        4) 如果要与队友通信、跟随、等待或让行，target 只能写 [允许引用的队友ID] 中给出的 AgentID。
        5) 不要新造建筑名、节点名、队友名；不要把 step 文本里的自由语言重新编造成 target。
        6) 协同等待、同步、让行、跟随这些动作要参考 [结构化协同约束] 和 [结构化路径策略]。
        7) 需要移动时，输出 MoveTo/TakeOff/Land/Hover/Follow/Align 等动作，parameters 只写策略参数。
        8) 无目标动作用 target=""none""。

        [允许的target]
        1) step_target
        2) none
        3) AgentID（仅限 [允许引用的队友ID] 中已有的值）
        4) world(x,z) 或 world(x,y,z)

        请输出1-3个动作，只输出JSON数组，不要解释：
        [
        {{
            ""actionType"": ""动作类型"",
            ""target"": ""step_target|none|AgentID|world(x,z)|world(x,y,z)"",
            ""parameters"": ""key:value,key2:value2 或 JSON字符串"",
            ""reason"": ""<=20字""
        }}
        ]";
    }

    /// <summary>
    /// 第二阶段：系统已确定最终目标，只让 LLM 基于粗路径和局部障碍生成短程 waypoint 段。
    /// </summary>
    private string BuildWaypointRefinementPrompt(
        string currentStep,
        StepNavigationDecision navDecision,
        StepTargetBinding binding,
        CoarsePathContext coarsePath,
        List<ActionData> highLevelActionData)
    {
        string role = agentProperties != null ? agentProperties.Role.ToString() : "Unknown";
        string coarseSummary = coarsePath.HasPath ? coarsePath.Summary : "无A*粗路径，若必须移动只可给极短局部点";
        string highLevelActions = BuildHighLevelActionSummary(highLevelActionData);

        return $@"你是{agentProperties?.Type}智能体，角色={role}。
        现在进入第二阶段滚动局部决策。最终目标已由系统绑定，你不能改目标名称。

        [当前步骤]
        {currentStep}

        [状态]
        {BuildDecisionContext()}

        [任务]
        {GetTaskSummary()}

        [系统绑定目标]
        {BuildStepTargetBindingSummary(binding)}

        [第一阶段动作序列]
        - {highLevelActions}

        [导航策略]
        {BuildNavigationPromptSummary(navDecision)}

        [A*粗路径参考]
        {coarseSummary}

        [局部导航包]
        {BuildLocalNavigationPacket(binding)}

        [当前可见地点(建筑等，来自感知)]
        {GetPerceivedCampusFeatureSummary()}

        [环境小节点(仅局部感知)]
        {GetPerceptionSmallNodeSummary()}

        [附近阻塞/动态小节点]
        {GetNearbyObstacleSmallNodeSummary()}

        [附近队友]
        {GetNearbyAgentsSummary()}

        [细化规则]
        1) 基于第一阶段动作序列、系统绑定目标、A*粗路径和局部小节点，输出当前这一轮要执行的具体移动参数。
        2) 只输出1-4个短程 MoveTo 动作，用于当前这一小段移动。
        3) target 只能写 world(x,z) 或 world(x,y,z)，不要再写命名地点 token、节点名或 AgentID。
        4) 必须优先沿[A*粗路径参考]的下一个局部方向前进，再根据当前阻塞/动态小节点做微调。
        5) 不要规划完整长路径，不要输出超过4个 waypoint，不要假设远处未知障碍。
        6) parameters 负责给出这一轮具体动作参数，例如 pathMode:Direct,segmentSource:LLMReactiveSegment,speed:...,avoidDynamic:1。
        7) 如果当前位置已经很接近最终目标，可以只输出1个直接到目标附近的 MoveTo。

        请只输出JSON数组，不要解释：
        [
        {{
            ""actionType"": ""MoveTo"",
            ""target"": ""world(x,z) 或 world(x,y,z)"",
            ""parameters"": ""pathMode:Direct,segmentSource:LLMReactiveSegment"",
            ""reason"": ""<=20字""
        }}
        ]";
    }

    /// <summary>
    /// 解析当前 step 的主目标绑定。
    /// 优先使用 PlanningModule 已经给出的结构化目标；
    /// 只有当规划层没有目标时，才允许使用高层动作里显式声明的 target。
    /// 这里不再回退到 step 自然语言文本解析，避免系统重新做任务语义推断。
    /// </summary>
    private StepTargetBinding ResolveStepTargetBindingFromActionData(List<ActionData> actionDataList)
    {
        StepTargetBinding structuredBinding = ResolveStructuredStepTargetBinding();
        if (structuredBinding.HasTarget)
        {
            return structuredBinding;
        }

        if (actionDataList != null && actionDataList.Count > 0)
        {
            List<ActionData> ordered = actionDataList
                .OrderByDescending(a => IsMovementLikeActionData(a) ? 1 : 0)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                ActionData actionData = ordered[i];
                if (actionData == null || string.IsNullOrWhiteSpace(actionData.target)) continue;

                string target = actionData.target.Trim();
                if (IsNoneTargetToken(target)) continue;
                if (string.Equals(target, "step_target", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target, "anchor:step_target", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target, "mission_target", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target, "primary_target", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryBuildStepTargetBindingFromTarget(target, out StepTargetBinding parsed))
                {
                    return parsed;
                }
            }
        }

        return structuredBinding;
    }

    /// <summary>
    /// 把高层动作中的 target 文本，转换成系统真正能执行的“目标绑定”。
    /// 可以把它理解成一个“翻译器”：
    /// - 输入是人或 LLM 写出来的目标描述；
    /// - 输出是程序能直接使用的目标信息，比如目标类型、世界坐标、网格格子、名字、UID。
    /// 如果一句 target 根本认不出来，就返回 false，表示这句话暂时不能落地执行。
    /// </summary>
    private bool TryBuildStepTargetBindingFromTarget(string target, out StepTargetBinding binding)
    {
        // 先构造一个“空目标”。
        // 这样后面就算识别失败，binding 也仍然是完整结构，不会留下未初始化字段。
        binding = new StepTargetBinding
        {
            HasTarget = false,
            TargetRef = "step_target",
            TargetKind = "None",
            RawQuery = target ?? string.Empty,
            Uid = string.Empty,
            Name = string.Empty,
            Source = "None",
            WorldPos = transform.position,
            GridCell = new Vector2Int(-1, -1),
            Summary = "none"
        };

        // target 为空，或者明确写成 none 这类“没有目标”的值，
        // 就说明这次根本没有可绑定的对象，直接失败返回。
        if (string.IsNullOrWhiteSpace(target) || IsNoneTargetToken(target))
        {
            return false;
        }

        string raw = target.Trim();
        // step_target 只是一个占位符，意思是“请使用上游已经绑定好的主目标”。
        // 它不是一个新的具体地点，所以这里不重复解析。
        if (string.Equals(raw, "step_target", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 如果 target 指向的是“自己”，比如回到自己附近、保持当前位置这类意思，
        // 就直接构造一个以自身为目标的绑定。
        if (ContainsSelfReference(raw))
        {
            binding = BuildSelfTargetBinding(raw, "HighLevel:SelfReference");
            return true;
        }

        // 第一类：把 target 当成校园里的建筑/地点名来识别。
        // 这是最常见的“去某栋楼、去某个区域”的情况。
        if (TryResolveCampusFeatureTarget(raw, out Vector3 featurePos, out string featureSource, out string normalized))
        {
            binding.HasTarget = true;
            binding.TargetKind = "Feature";
            binding.Source = $"HighLevel:{featureSource}";
            binding.WorldPos = featurePos;
            binding.Name = normalized ?? string.Empty;

            // 找到地点后，再顺手映射到最近可通行网格。
            // 这样后面的导航模块就能直接拿它来寻路。
            if (TryResolveWalkableCell(featurePos, out Vector2Int c))
            {
                binding.GridCell = c;
                if (campusGrid != null &&
                    campusGrid.TryGetCellFeatureInfo(c.x, c.y, out string uid, out string name, out _, out _))
                {
                    binding.Uid = uid ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(binding.Name)) binding.Name = name ?? string.Empty;
                }
            }

            // 如果没拿到单独的 UID，就退回用名字做标识。
            if (string.IsNullOrWhiteSpace(binding.Uid))
            {
                binding.Uid = binding.Name;
            }

            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},uid={binding.Uid},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
            return true;
        }

        // 第二类：把 target 当成显式世界坐标。
        // 例如 world(x,z) 或 world(x,y,z)。
        if (TryParseWorldTarget(raw, out Vector3 worldPos))
        {
            binding.HasTarget = true;
            binding.TargetKind = "World";
            binding.Source = "HighLevel:WorldCoordinate";
            binding.WorldPos = worldPos;
            if (TryResolveWalkableCell(worldPos, out Vector2Int c))
            {
                binding.GridCell = c;
            }
            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
            return true;
        }

        // 第三类：按小节点 ID 找局部感知对象。
        // 这里的小节点可以理解成环境里更细的小目标，不一定是完整建筑。
        if (TryFindSmallNodeById(raw, out GameObject nodeObj, out Vector3 nodePos))
        {
            SmallNodeData nodeData = GetSmallNodeDataById(raw);
            binding.HasTarget = true;
            binding.TargetKind = "SmallNode";
            binding.Source = "HighLevel:PerceptionSmallNodeById";
            if (nodeData != null)
            {
                binding.Source += $":{nodeData.NodeType}:{(nodeData.IsDynamic ? "Dynamic" : "Static")}";
            }
            binding.WorldPos = nodePos;
            binding.Name = nodeObj != null ? nodeObj.name : raw;
            binding.Uid = nodeData?.NodeId ?? string.Empty;
            if (TryResolveWalkableCell(nodePos, out Vector2Int c))
            {
                binding.GridCell = c;
            }
            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
            return true;
        }

        // 第四类：如果没有明确 ID，就尝试按名字在当前感知结果里找对象。
        if (FindObjectByNameInPerception(raw, out GameObject perceivedObj))
        {
            binding.HasTarget = true;
            binding.TargetKind = "SmallNode";
            binding.Source = "HighLevel:PerceptionSmallNodeByName";
            binding.WorldPos = perceivedObj.transform.position;
            binding.Name = perceivedObj.name;
            if (TryResolveWalkableCell(binding.WorldPos, out Vector2Int c))
            {
                binding.GridCell = c;
            }
            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
            return true;
        }

        // 第五类：按语义去找最像的感知小节点。
        // 这一步不是精确匹配某个名字，而是看“这个词大概在说哪类东西”。
        if (TryFindSmallNodeBySemantic(raw, out SmallNodeData semanticNode, out GameObject semanticObj, out Vector3 semanticPos))
        {
            binding.HasTarget = true;
            binding.TargetKind = "SmallNode";
            binding.Source = semanticNode != null
                ? $"HighLevel:PerceptionSmallNodeSemantic:{semanticNode.NodeType}:{(semanticNode.IsDynamic ? "Dynamic" : "Static")}"
                : "HighLevel:PerceptionSmallNodeSemantic";
            binding.WorldPos = semanticPos;
            binding.Name = !string.IsNullOrWhiteSpace(semanticNode?.DisplayName)
                ? semanticNode.DisplayName
                : (semanticNode != null ? semanticNode.NodeType.ToString() : raw);
            binding.Uid = semanticNode?.NodeId ?? string.Empty;
            if (TryResolveWalkableCell(binding.WorldPos, out Vector2Int c))
            {
                binding.GridCell = c;
            }
            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},uid={binding.Uid},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
            return true;
        }

        // 第六类：把 target 当作附近队友。
        // 这给 Follow / Wait / Align 这类协同动作提供具体跟随对象。
        if (TryFindNearbyAgent(raw, out GameObject teammate))
        {
            binding.HasTarget = true;
            binding.TargetKind = "Agent";
            binding.Source = "HighLevel:NearbyAgent";
            binding.WorldPos = teammate.transform.position;
            binding.Name = raw;
            if (TryResolveWalkableCell(binding.WorldPos, out Vector2Int c))
            {
                binding.GridCell = c;
            }
            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
            return true;
        }

        // 上面所有识别方式都没命中，说明当前这句 target 没法变成可靠坐标。
        // 所以返回 false，让上层换别的来源或走兜底逻辑。
        return false;
    }

    private bool HasMovementLikeAction(List<ActionData> actionDataList)
    {
        if (actionDataList == null || actionDataList.Count == 0) return false;
        for (int i = 0; i < actionDataList.Count; i++)
        {
            if (IsMovementLikeActionData(actionDataList[i])) return true;
        }
        return false;
    }

    private bool IsMovementLikeActionData(ActionData actionData)
    {
        if (actionData == null) return false;
        return TryParsePrimitiveActionTypeFlexible(actionData.actionType, out PrimitiveActionType actionType) &&
               IsMovementLikePrimitiveAction(actionType);
    }

    private static bool IsMovementLikePrimitiveAction(PrimitiveActionType actionType)
    {
        switch (actionType)
        {
            case PrimitiveActionType.MoveTo:
            case PrimitiveActionType.TakeOff:
            case PrimitiveActionType.Land:
            case PrimitiveActionType.Hover:
            case PrimitiveActionType.RotateTo:
            case PrimitiveActionType.AdjustAltitude:
            case PrimitiveActionType.Orbit:
            case PrimitiveActionType.Follow:
            case PrimitiveActionType.Align:
                return true;
            default:
                return false;
        }
    }

    private List<ActionCommand> BuildActionCommandsFromActionData(List<ActionData> actionDataList)
    {
        List<ActionCommand> commands = new List<ActionCommand>();
        if (actionDataList == null) return commands;

        for (int i = 0; i < actionDataList.Count; i++)
        {
            ActionCommand command = ParseSingleAction(actionDataList[i]);
            if (command != null)
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    private bool ShouldRequestWaypointRefinement(List<ActionCommand> actionSequence, StepTargetBinding binding)
    {
        if (!binding.HasTarget) return false;
        if (actionSequence == null || actionSequence.Count == 0) return false;

        for (int i = 0; i < actionSequence.Count; i++)
        {
            if (actionSequence[i] != null && actionSequence[i].ActionType == PrimitiveActionType.MoveTo)
            {
                return true;
            }
        }
        return false;
    }

    private bool SequenceContainsMovement(List<ActionCommand> actions)
    {
        if (actions == null || actions.Count == 0) return false;
        for (int i = 0; i < actions.Count; i++)
        {
            ActionCommand action = actions[i];
            if (action != null && IsMovementLikePrimitiveAction(action.ActionType))
            {
                return true;
            }
        }
        return false;
    }

    private List<ActionCommand> MergeRefinedMovementActions(List<ActionCommand> baseActions, List<ActionCommand> refinedMovement)
    {
        if (baseActions == null || baseActions.Count == 0) return refinedMovement ?? new List<ActionCommand>();
        if (refinedMovement == null || refinedMovement.Count == 0) return baseActions;

        List<ActionCommand> refined = refinedMovement
            .Where(a => a != null && a.ActionType == PrimitiveActionType.MoveTo)
            .ToList();
        if (refined.Count == 0) return baseActions;

        List<ActionCommand> merged = new List<ActionCommand>();
        bool injected = false;
        for (int i = 0; i < baseActions.Count; i++)
        {
            ActionCommand action = baseActions[i];
            if (action != null && action.ActionType == PrimitiveActionType.MoveTo)
            {
                if (!injected)
                {
                    merged.AddRange(refined);
                    injected = true;
                }
                continue;
            }

            merged.Add(action);
        }

        if (!injected)
        {
            merged.InsertRange(0, refined);
        }

        return merged;
    }

    /// <summary>
    /// 严格解析动作数组（完整 JSON）。
    /// 支持：
    /// 1) 纯数组: [ ... ]
    /// 2) 对象包裹: { "actions": [ ... ] }
    /// </summary>
    private List<ActionData> TryParseActionDataListStrict(string cleanJson)
    {
        try
        {
            ActionSequenceWrapper wrapper = null;
            if (cleanJson.TrimStart().StartsWith("{"))
            {
                wrapper = JsonUtility.FromJson<ActionSequenceWrapper>(cleanJson);
            }
            if (wrapper == null || wrapper.actions == null)
            {
                wrapper = JsonUtility.FromJson<ActionSequenceWrapper>($"{{\"actions\":{cleanJson}}}");
            }
            return wrapper?.actions;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"严格JSON解析失败，尝试容错解析: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 容错解析：
    /// 当 LLM 输出被截断（例如最后一个对象没闭合）时，
    /// 提取数组中“完整闭合”的对象，忽略不完整尾部。
    /// </summary>
    private List<ActionData> TryParseActionDataListResilient(string text)
    {
        List<ActionData> actions = new List<ActionData>();
        if (string.IsNullOrWhiteSpace(text)) return actions;

        string arrayLike = ExtractActionsArrayLikeText(text);
        List<string> objectJsonList = ExtractCompleteJsonObjects(arrayLike);
        for (int i = 0; i < objectJsonList.Count; i++)
        {
            string obj = objectJsonList[i];
            if (string.IsNullOrWhiteSpace(obj)) continue;

            try
            {
                ActionData parsed = JsonUtility.FromJson<ActionData>(obj);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.actionType))
                {
                    actions.Add(parsed);
                }
            }
            catch
            {
                // 单个对象解析失败时跳过，避免整段报废
            }
        }

        if (actions.Count > 0)
        {
            Debug.LogWarning($"容错JSON解析生效: 从响应中恢复 {actions.Count} 个完整动作对象。");
        }
        return actions;
    }

    /// <summary>
    /// 定位动作数组片段（优先 actions 字段，其次首个数组起点）。
    /// </summary>
    private static string ExtractActionsArrayLikeText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim();

        int keyIdx = s.IndexOf("\"actions\"", System.StringComparison.OrdinalIgnoreCase);
        if (keyIdx >= 0)
        {
            int arrIdx = s.IndexOf('[', keyIdx);
            if (arrIdx >= 0) return s.Substring(arrIdx);
        }

        int firstArr = s.IndexOf('[');
        if (firstArr >= 0) return s.Substring(firstArr);
        return s;
    }

    /// <summary>
    /// 从数组文本中提取完整闭合的 JSON 对象字符串。
    /// </summary>
    private static List<string> ExtractCompleteJsonObjects(string text)
    {
        List<string> objects = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return objects;

        bool inString = false;
        bool escaped = false;
        int depth = 0;
        int start = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
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

            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
                continue;
            }

            if (c == '}')
            {
                if (depth <= 0) continue;
                depth--;
                if (depth == 0 && start >= 0 && i >= start)
                {
                    objects.Add(text.Substring(start, i - start + 1));
                    start = -1;
                }
            }
        }

        return objects;
    }
    private string ExtractPureJson(string response)
    {
        if (string.IsNullOrEmpty(response))
            return response;

        // 如果包含代码块标记，提取其中的内容
        if (response.Contains("```json"))
        {
            int jsonStart = response.IndexOf("```json") + 7;
            int jsonEnd = response.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                return response.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }
        
        // 如果包含普通的代码块标记
        if (response.Contains("```"))
        {
            int jsonStart = response.IndexOf("```") + 3;
            int jsonEnd = response.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                return response.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }
        
        // 如果没有代码块标记，直接返回整个响应（可能是纯 JSON）
        return response.Trim();
    }
    // 解析单个动作
    private ActionCommand ParseSingleAction(ActionData actionData)
    {
        if (string.IsNullOrEmpty(actionData.actionType))
            return null;

        ActionCommand command = new ActionCommand();
        
        // 解析动作类型（支持常见别名：Move/Observe/Comm）
        if (TryParsePrimitiveActionTypeFlexible(actionData.actionType, out PrimitiveActionType actionType))
        {
            command.ActionType = actionType;
        }
        else
        {
            Debug.LogWarning($"未知动作类型: {actionData.actionType}");
            return null;
        }

        // 默认目标为当前位置，避免空目标导致异常动作
        command.TargetPosition = transform.position;
        command.TargetRotation = transform.rotation;

        Dictionary<string, string> targetMeta = null;

        // 解析目标（新版本：world坐标 / nodeId / displayName / AgentID）
        if (!IsNoTargetAction(actionType))
        {
            string target = (actionData.target ?? string.Empty).Trim();
            targetMeta = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(target) || IsNoneTargetToken(target))
            {
                if (actionType == PrimitiveActionType.MoveTo && lastStepTargetBinding.HasTarget)
                {
                    command.TargetPosition = lastStepTargetBinding.WorldPos;
                    targetMeta["targetKind"] = lastStepTargetBinding.TargetKind;
                    targetMeta["targetSource"] = "StepTargetAutoFill";
                    targetMeta["targetRef"] = "step_target";
                    if (lastStepTargetBinding.TargetKind == "Feature" &&
                        TryGetFeatureArrivalRadius(lastStepTargetBinding, out float featureArrivalRadius))
                    {
                        targetMeta["targetArrivalRadius"] = featureArrivalRadius.ToString("F2", CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    command.TargetPosition = transform.position;
                    targetMeta["targetKind"] = "None";
                }
            }
            else if (TryResolveTargetFromAllowedSources(
                target,
                out GameObject resolvedObj,
                out Vector3 resolvedPos,
                out string resolvedKind,
                out Dictionary<string, string> resolvedMeta))
            {
                command.TargetObject = resolvedObj;
                command.TargetPosition = resolvedPos;
                targetMeta["targetKind"] = resolvedKind;
                foreach (var kv in resolvedMeta)
                {
                    targetMeta[kv.Key] = kv.Value;
                }
                if (string.Equals(resolvedKind, "Feature", System.StringComparison.OrdinalIgnoreCase) &&
                    TryGetFeatureArrivalRadiusByResolvedMeta(resolvedMeta, resolvedPos, out float featureArrivalRadius))
                {
                    targetMeta["targetArrivalRadius"] = featureArrivalRadius.ToString("F2", CultureInfo.InvariantCulture);
                }
            }
            else
            {
                command.TargetPosition = transform.position;
                targetMeta["targetKind"] = "Unknown";
                Debug.LogWarning($"无法解析目标: {target}，仅支持 CampusGrid地点/可见地点/小节点/world坐标/队友目标，已回退到当前位置。");
            }
        }
        
        // 解析参数
        if (!string.IsNullOrEmpty(actionData.parameters))
        {
            ParseActionParameters(command, actionData.parameters);
        }
        if (targetMeta != null && targetMeta.Count > 0)
        {
            command.Parameters = MergeParameters(command.Parameters, targetMeta);
        }

        // 补充旋转与高度目标（支持 RotateTo/AdjustAltitude）
        Dictionary<string, string> pmap = ParseLooseParameterMap(command.Parameters);
        if (actionType == PrimitiveActionType.RotateTo)
        {
            if (TryGetFloatFromMap(pmap, "rotY", out float rotY))
            {
                command.TargetRotation = Quaternion.Euler(0f, rotY, 0f);
            }
        }
        else if (actionType == PrimitiveActionType.AdjustAltitude)
        {
            if (TryGetFloatFromMap(pmap, "height", out float height))
            {
                command.TargetPosition = new Vector3(transform.position.x, height, transform.position.z);
            }
        }
        
        return command;
    }

    /// <summary>
    /// 动作类型宽松解析：
    /// 1) 先尝试枚举原名；
    /// 2) 再把常见别名映射到内部原子动作。
    /// </summary>
    private static bool TryParsePrimitiveActionTypeFlexible(string raw, out PrimitiveActionType actionType)
    {
        actionType = PrimitiveActionType.Idle;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (System.Enum.TryParse<PrimitiveActionType>(raw.Trim(), true, out actionType))
        {
            return true;
        }

        string n = raw.Trim().ToLowerInvariant()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(" ", string.Empty);

        switch (n)
        {
            case "move":
            case "moveto":
            case "goto":
            case "navigate":
                actionType = PrimitiveActionType.MoveTo;
                return true;
            case "observe":
            case "observation":
            case "inspect":
                actionType = PrimitiveActionType.Scan;
                return true;
            case "look":
                actionType = PrimitiveActionType.LookAt;
                return true;
            case "comm":
            case "communicate":
            case "message":
            case "transmit":
                actionType = PrimitiveActionType.TransmitMessage;
                return true;
            case "pickup":
                actionType = PrimitiveActionType.PickUp;
                return true;
            case "dropoff":
                actionType = PrimitiveActionType.Drop;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 目标解析白名单：仅允许以下来源
    /// 1) 位置 world(x,z)/(x,z)
    /// 2) 地点（当前可见地点 + CampusGrid2D 全局地点）
    /// 3) 小节点（nodeId/displayName）
    /// 4) 队友（AgentID）
    /// </summary>
    private bool TryResolveTargetFromAllowedSources(
        string target,
        out GameObject targetObject,
        out Vector3 targetPosition,
        out string targetKind,
        out Dictionary<string, string> meta)
    {
        targetObject = null;
        targetPosition = transform.position;
        targetKind = "Unknown";
        meta = new Dictionary<string, string>();

        if (ContainsSelfReference(target))
        {
            targetPosition = transform.position;
            targetKind = "Self";
            meta["targetSource"] = "SelfReference";
            return true;
        }

        if (TryResolveStepTargetReference(target, out Vector3 stepTargetPos, out string stepKind))
        {
            targetPosition = stepTargetPos;
            targetKind = stepKind;
            meta["targetSource"] = "StepTargetBinding";
            meta["targetRef"] = "step_target";
            if (!string.IsNullOrWhiteSpace(lastStepTargetBinding.Uid)) meta["targetUid"] = lastStepTargetBinding.Uid;
            if (!string.IsNullOrWhiteSpace(lastStepTargetBinding.Name)) meta["targetName"] = lastStepTargetBinding.Name;
            return true;
        }

        if (TryParseWorldTarget(target, out Vector3 worldPos))
        {
            targetPosition = worldPos;
            targetKind = "World";
            meta["targetSource"] = "WorldCoordinate";
            return true;
        }

        if (TryResolveCampusFeatureTarget(target, out Vector3 featurePos, out string featureSource, out string featureName))
        {
            targetPosition = featurePos;
            targetKind = "Feature";
            meta["targetSource"] = featureSource;
            if (!string.IsNullOrWhiteSpace(featureName))
            {
                meta["targetFeature"] = featureName;
            }
            return true;
        }

        if (TryFindSmallNodeById(target, out GameObject detectedObj, out Vector3 detectedPos))
        {
            SmallNodeData nodeData = GetSmallNodeDataById(target);
            targetObject = detectedObj;
            targetPosition = detectedPos;
            targetKind = "SmallNode";
            meta["targetSource"] = "PerceptionSmallNodeById";
            meta["targetNodeId"] = target.Trim();
            AppendSmallNodeMetadata(meta, nodeData);
            return true;
        }

        if (TryFindSmallNodeBySemantic(target, out SmallNodeData semanticNode, out GameObject semanticObj, out Vector3 semanticPos))
        {
            targetObject = semanticObj;
            targetPosition = semanticPos;
            targetKind = "SmallNode";
            meta["targetSource"] = "PerceptionSmallNodeSemantic";
            AppendSmallNodeMetadata(meta, semanticNode);
            return true;
        }

        if (FindObjectByNameInPerception(target, out GameObject nameMatched))
        {
            targetObject = nameMatched;
            targetPosition = nameMatched.transform.position;
            targetKind = "SmallNode";
            meta["targetSource"] = "PerceptionSmallNodeByName";
            return true;
        }

        if (TryFindNearbyAgent(target, out GameObject teammate))
        {
            targetObject = teammate;
            targetPosition = teammate.transform.position;
            targetKind = "Agent";
            meta["targetSource"] = "NearbyAgent";
            meta["targetAgentId"] = target.Trim();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析步骤目标锚点引用。
    /// </summary>
    private bool TryResolveStepTargetReference(string target, out Vector3 worldPos, out string targetKind)
    {
        worldPos = transform.position;
        targetKind = "Unknown";
        if (!lastStepTargetBinding.HasTarget || string.IsNullOrWhiteSpace(target)) return false;

        string t = target.Trim().ToLowerInvariant();
        if (t != "step_target" && t != "anchor:step_target" && t != "mission_target" && t != "primary_target")
        {
            return false;
        }

        worldPos = lastStepTargetBinding.WorldPos;
        targetKind = string.IsNullOrWhiteSpace(lastStepTargetBinding.TargetKind) ? "Feature" : lastStepTargetBinding.TargetKind;
        return true;
    }

    /// <summary>
    /// 从结构化 target 字符串里剥掉“标签前缀”。
    /// 这里不再枚举任何业务单词，也不假设前缀一定是某类固定地点词。
    ///
    /// 设计原因：
    /// PlanningModule 传下来的 target / primaryTarget / sharedTarget 本质上都是字符串字段，
    /// 动作层不应该因为某个词表写死而限制解析范围。
    /// 因此这里只做一个很克制的规则：
    /// - 如果像 "xxx:yyy" 这种“标签:主体”的形式，就取主体 yyy；
    /// - 否则保持原样。
    /// </summary>
    private static bool TryStripCampusFeaturePrefix(string query, out string prefix, out string stripped)
    {
        prefix = string.Empty;
        stripped = query != null ? query.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(stripped)) return false;

        int colonIndex = stripped.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= stripped.Length - 1)
        {
            return false;
        }

        string left = stripped.Substring(0, colonIndex).Trim();
        string right = stripped.Substring(colonIndex + 1).Trim();

        // 只把“短标签:主体”当成前缀处理，避免把完整自然语言里的冒号误删掉。
        // 例如：
        // - feature:a_1   => a_1
        // - campus:x_3    => x_3
        // - 说明：去 a 的南面 => 不做这里的前缀剥离
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (left.Length > 24) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(left, @"^[A-Za-z][A-Za-z0-9_\-]*$")) return false;

        prefix = left;
        stripped = right;
        return true;
    }

    /// <summary>
    /// 去掉用户描述里的“方位附加语”。
    /// 设计目的：
    /// 1) 用户经常会说“a 的南面”“x_3 北侧”这类“地点主体 + 方位附加语”；
    /// 2) 这些说法里真正可定位的核心地点其实是前面的主体；
    /// 3) 如果系统直接拿“a的南面”去查名字，通常会完全查不到位置。
    ///
    /// 注意：
    /// 这里的职责只是“先把地点主体抠出来，别因为方位词导致查找失败”。
    /// “从南侧接近”这种更细的语义，应优先由 PlanningModule 的 RoutePolicy/approachSide 来表达。
    /// </summary>
    private static string StripCampusDirectionalQualifier(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        string q = query.Trim();

        // 常见中文形式：
        // a的南面 / a南侧 / x_3北边 / 图书馆东部区域
        string stripped = System.Text.RegularExpressions.Regex.Replace(
            q,
            @"^(?<base>.+?)(?:的)?(?:东|西|南|北)(?:面|侧|边|部|向|方向|一侧|区域|附近|周边|外侧|内侧|里面|内部)?$",
            "${base}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ).Trim();
        if (!string.IsNullOrWhiteSpace(stripped) && !string.Equals(stripped, q, System.StringComparison.Ordinal))
        {
            return stripped;
        }

        // 常见英文形式：
        // alpha south side / node_3 north / area west zone
        stripped = System.Text.RegularExpressions.Regex.Replace(
            q,
            @"^(?<base>.+?)\s+(north|south|east|west)(?:\s+(?:side|area|part|zone|near|nearby))?$",
            "${base}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ).Trim();
        return stripped;
    }

    /// <summary>
    /// 为校园地点查询构造候选词列表。
    /// 目标是把“PlanningModule 传来的结构化目标字符串”整理成
    /// “CampusGrid2D 里最可能命中的几种查询字符串”。
    ///
    /// 典型例子：
    /// - a              -> a
    /// - a_1            -> a_1
    /// - building_7     -> building_7
    /// - a的南面        -> a的南面 / a
    /// - building_7北侧 -> building_7北侧 / building_7
    /// </summary>
    private static List<string> BuildCampusFeatureQueryCandidates(string rawQuery)
    {
        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string value)
        {
            string v = value != null ? value.Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(v)) return;
            if (seen.Add(v)) result.Add(v);
        }

        string q = rawQuery != null ? rawQuery.Trim() : string.Empty;
        AddCandidate(q);

        string baseQuery = StripCampusDirectionalQualifier(q);
        AddCandidate(baseQuery);

        return result;
    }

    /// <summary>
    /// 解析 CampusGrid2D 的地点目标。
    /// 这一版做了三件更贴近实际数据结构的事：
    /// 1) 不再依赖任何业务词表，而是直接消费 PlanningModule 下发的字符串目标；
    /// 2) 兼容 CampusGrid2D 内部同步的 runtime alias，例如 a_1、building_7；
    /// 3) 对“a的南面”这类带方位词的查询，先提取核心地点 a，再做位置解析。
    ///
    /// 注意：
    /// 这里解析的不是“用户随口说的话”，而是 PlanningModule 已经写进
    /// MissionTaskSlot.target / StepIntentDefinition.primaryTarget / TeamCoordinationDirective.sharedTarget
    /// 的结构化字符串。
    /// </summary>
    private bool TryResolveCampusFeatureTarget(string target, out Vector3 worldPos, out string source, out string normalizedName)
    {
        worldPos = transform.position;
        source = string.Empty;
        normalizedName = string.Empty;
        if (string.IsNullOrWhiteSpace(target)) return false;

        string query = target.Trim();
        TryStripCampusFeaturePrefix(query, out _, out query);
        if (string.IsNullOrWhiteSpace(query)) return false;
        List<string> candidates = BuildCampusFeatureQueryCandidates(query);
        SyncCampusGridReference();

        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            normalizedName = candidate;
            bool strictToken = IsCanonicalFeatureToken(candidate);

            // 第一层：先看当前感知到的地点。
            // 理由是：离自己最近、刚刚看见的对象，通常比全图静态摘要更可信。
            if (TryResolvePerceivedCampusFeatureTarget(candidate, out worldPos, allowFuzzy: !strictToken))
            {
                source = strictToken ? "PerceivedCampusFeatureExact" : "PerceivedCampusFeature";
                if (!string.Equals(candidate, query, System.StringComparison.OrdinalIgnoreCase))
                {
                    source += ":Normalized";
                }
                return true;
            }

            // 第二层：查 CampusGrid2D 的静态索引。
            // 这里既查原始 uid/name，也查从 CampusJsonMapLoader 同步进来的 runtime alias。
            if (campusGrid != null && campusGrid.blockedGrid != null && campusGrid.cellTypeGrid != null &&
                TryResolveFeatureNameToCell(candidate, out Vector2Int cell, out string matchedToken, exactOnly: strictToken))
            {
                worldPos = campusGrid.GridToWorldCenter(cell.x, cell.y, astarWaypointYOffset);
                worldPos.y = ResolveWaypointY(worldPos);
                source = strictToken ? "CampusGrid2DExact" : "CampusGrid2D";
                if (!string.Equals(candidate, query, System.StringComparison.OrdinalIgnoreCase))
                {
                    source += ":Normalized";
                }
                if (!string.IsNullOrWhiteSpace(matchedToken))
                {
                    normalizedName = matchedToken;
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从“当前感知到的地点”中解析目标地点位置。
    /// </summary>
    private bool TryResolvePerceivedCampusFeatureTarget(string query, out Vector3 worldPos, bool allowFuzzy = true)
    {
        worldPos = transform.position;
        if (agentState?.DetectedCampusFeatures == null || string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        CampusFeaturePerceptionData best = null;
        string q = query.Trim();

        for (int i = 0; i < agentState.DetectedCampusFeatures.Count; i++)
        {
            CampusFeaturePerceptionData f = agentState.DetectedCampusFeatures[i];
            if (f == null || string.IsNullOrWhiteSpace(f.FeatureName)) continue;
            bool nameExact = string.Equals(f.FeatureName, q, System.StringComparison.OrdinalIgnoreCase);
            bool uidExact = !string.IsNullOrWhiteSpace(f.FeatureUid) &&
                            string.Equals(f.FeatureUid, q, System.StringComparison.OrdinalIgnoreCase);
            if (nameExact || uidExact)
            {
                best = f;
                break;
            }

            if (!allowFuzzy) continue;

            bool nameFuzzy = f.FeatureName.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                             q.IndexOf(f.FeatureName, System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool uidFuzzy = !string.IsNullOrWhiteSpace(f.FeatureUid) &&
                            (f.FeatureUid.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                             q.IndexOf(f.FeatureUid, System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (nameFuzzy || uidFuzzy)
            {
                if (best == null) best = f;
            }
        }

        if (best == null) return false;
        worldPos = best.AnchorWorldPosition;
        worldPos.y = ResolveWaypointY(worldPos);
        return true;
    }

    /// <summary>
    /// 把地点名称映射到网格坐标。
    /// 优先精确匹配；失败后再做模糊包含匹配。
    /// </summary>
    private bool TryResolveFeatureNameToCell(string featureName, out Vector2Int cell)
    {
        bool exactOnly = IsCanonicalFeatureToken(featureName);
        return TryResolveFeatureNameToCell(featureName, out cell, out _, exactOnly: exactOnly);
    }

    /// <summary>
    /// 把地点名称映射到网格坐标。
    /// exactOnly=true 时仅允许精确 uid/name/alias，不做模糊回退。
    /// </summary>
    private bool TryResolveFeatureNameToCell(string featureName, out Vector2Int cell, out string matchedToken, bool exactOnly)
    {
        cell = new Vector2Int(-1, -1);
        matchedToken = string.Empty;
        if (campusGrid == null || string.IsNullOrWhiteSpace(featureName)) return false;
        string q = featureName.Trim();

        if (campusGrid.TryGetFeatureFirstCellByUid(q, out cell, preferWalkable: true, ignoreCase: true) ||
            campusGrid.TryGetFeatureFirstCellByUid(q, out cell, preferWalkable: false, ignoreCase: true))
        {
            matchedToken = q;
            return true;
        }

        if (campusGrid.TryGetFeatureFirstCell(q, out cell, preferWalkable: true, ignoreCase: true) ||
            campusGrid.TryGetFeatureFirstCell(q, out cell, preferWalkable: false, ignoreCase: true))
        {
            matchedToken = q;
            return true;
        }

        if (campusGrid.TryResolveFeatureAliasCell(q, out cell, out string aliasUid, out string aliasName, preferWalkable: true, ignoreCase: true) ||
            campusGrid.TryResolveFeatureAliasCell(q, out cell, out aliasUid, out aliasName, preferWalkable: false, ignoreCase: true))
        {
            matchedToken = !string.IsNullOrWhiteSpace(aliasUid) ? aliasUid : (aliasName ?? q);
            return true;
        }

        if (exactOnly) return false;

        string matchedUid;
        string matchedName;
        bool ok = campusGrid.TryResolveFeatureCell(
            q,
            out cell,
            out matchedUid,
            out matchedName,
            preferWalkable: true,
            ignoreCase: true
        );
        if (ok)
        {
            matchedToken = !string.IsNullOrWhiteSpace(matchedUid) ? matchedUid : (matchedName ?? string.Empty);
        }
        return ok;
    }

    /// <summary>
    /// 判断是否是“显式地点标识符”。
    /// 这里只认 CampusJsonMapLoader 真实命名规则里的“主体_数字”形式，
    /// 例如 a_1、building_7。
    /// 这类 token 代表“实例别名”，应优先走 CampusGrid2D 的 alias 精确查询。
    /// </summary>
    private static bool IsCanonicalFeatureToken(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();

        TryStripCampusFeaturePrefix(q, out _, out q);

        return System.Text.RegularExpressions.Regex.IsMatch(
            q,
            @"^[A-Za-z0-9]+(?:[_\-][A-Za-z0-9]+)*[_\-]\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // 解析动作参数
    private void ParseActionParameters(ActionCommand command, string parameters)
    {
        command.Parameters = NormalizeActionParameters(parameters);
    }

    // 执行动作序列
    private IEnumerator ExecuteActionSequence(List<ActionCommand> actions, string currentStep)
    {
        string agentId = agentProperties != null ? agentProperties.AgentID : gameObject.name;
        // 打印当前步骤和动作序列信息
        Debug.Log($"=== 开始执行步骤 [{agentId}]: {currentStep} ===");
        Debug.Log($"步骤描述[{agentId}]: {currentStep}");
        Debug.Log($"动作序列数量[{agentId}]: {actions.Count}");
        
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            Debug.Log($"动作[{agentId}] {i + 1}/{actions.Count}: {action.ActionType}, 目标: {action.TargetObject?.name ?? action.TargetPosition.ToString()}, 参数: {action.Parameters}");
        }
        Debug.Log($"=== 步骤信息结束 [{agentId}] ===");

        for (int i = 0; i < actions.Count; i++)
        {
            ActionCommand action = actions[i];
            // 设置步骤上下文
            var stepInfo = new Dictionary<string, string>
            {
                ["currentStep"] = currentStep,
                ["sequenceIndex"] = i.ToString(),
                ["totalActions"] = actions.Count.ToString()
            };
            action.Parameters = MergeParameters(action.Parameters, stepInfo);
            
            bool actionCompleted = false;
            bool actionSuccess = false;
            string actionResult = "";
            
            // 特殊处理通信动作
            if (action.ActionType == PrimitiveActionType.TransmitMessage)
            {
                ExecuteCommunicationAction(action);
                actionCompleted = true;
                actionSuccess = true;
                actionResult = "消息发送完成";
            }
            else
            {
                // 执行其他动作
                if (mlAgentsController != null)
                {
                    mlAgentsController.SetCurrentCommand(action, (type, success, result) => {
                        actionCompleted = true;
                        actionSuccess = success;
                        actionResult = result;
                    });
                }
            }
            
            // 等待动作完成
            yield return new WaitUntil(() => actionCompleted);
            
            // 记录执行结果
            memoryModule.AddMemory($"执行步骤'{currentStep}'-动作{i}: {action.ActionType} - 成功: {actionSuccess}", "decision", 0.8f);
            
            if (!actionSuccess)
            {
                // 动作失败，停止序列并重新决策
                Debug.Log($"动作执行失败，重新决策: {actionResult}");
                StartCoroutine(DecideNextAction());
                yield break;
            }
            
            // 短暂延迟 between actions
            yield return new WaitForSeconds(0.1f);
        }
        
        // 所有动作完成后，先检查“移动步是否已真正到达目标”。
        // 未到达则保持在当前 step，进入下一轮 DecideNextAction 做滚动重规划。
        if (ShouldContinueCurrentStepWithoutCompletion(currentStep))
        {
            Debug.Log($"当前步骤尚未到达目标，继续滚动重规划: {currentStep}");
            StartCoroutine(DecideNextAction());
            yield break;
        }

        // 已到达或非移动步：通过 PlanningModule 统一推进步骤并发出任务反馈。
        if (planningModule != null && planningModule.currentPlan != null)
        {
            planningModule.CompleteCurrentTask();

            if (planningModule.HasActiveMission())
            {
                Debug.Log("准备执行下一个步骤");
                StartCoroutine(DecideNextAction());
            }
            else if (planningModule.IsWaitingForTeamCompletion())
            {
                Debug.Log("本地槽位已完成，等待队友完成其余槽位");
            }
            else
            {
                Debug.Log($"🎉 任务完成: {planningModule.currentPlan.mission}");
            }
        }
        else
        {
            Debug.LogWarning("planningModule 或 currentPlan 为 null");
        }
    }

    /// <summary>
    /// 是否应保持在当前 step（不推进），继续滚动重规划。
    /// </summary>
    private bool ShouldContinueCurrentStepWithoutCompletion(string currentStep)
    {
        if (string.IsNullOrWhiteSpace(currentStep)) return false;
        if (!lastIssuedSequenceContainsMovement) return false;

        if (!lastStepTargetBinding.HasTarget) return false;
        // 对动态目标（队友/小节点）也要做接近性判定，否则会出现“动作刚执行完就错误推进 step”的问题。
        lastStepTargetBinding = RefreshStepTargetBindingForCompletion(lastStepTargetBinding);

        return !IsNearStepTarget(lastStepTargetBinding);
    }

    /// <summary>
    /// 在 step 完成判定前刷新一次目标绑定。
    /// 对静态 Feature/World 不需要刷新；
    /// 对 Agent/SmallNode 这类可能移动或感知更新的目标，优先按原始结构化 target 再解析一遍。
    /// </summary>
    private StepTargetBinding RefreshStepTargetBindingForCompletion(StepTargetBinding binding)
    {
        if (!binding.HasTarget) return binding;
        if (string.IsNullOrWhiteSpace(binding.RawQuery)) return binding;

        if (binding.TargetKind == "Agent" || binding.TargetKind == "SmallNode")
        {
            if (TryBuildStepTargetBindingFromTarget(binding.RawQuery, out StepTargetBinding refreshed))
            {
                return refreshed;
            }
        }

        return binding;
    }

    /// <summary>
    /// 判定是否已到达步骤目标（平面距离为主，辅以高度约束）。
    /// </summary>
    private bool IsNearStepTarget(StepTargetBinding target)
    {
        if (!target.HasTarget) return true;
        Vector3 cur = transform.position;
        Vector3 goal = target.WorldPos;

        float planar = Vector2.Distance(new Vector2(cur.x, cur.z), new Vector2(goal.x, goal.z));
        float reachDistance = Mathf.Max(0.5f, stepTargetReachDistance);
        if (target.TargetKind == "Feature" && TryGetFeatureArrivalRadius(target, out float featureArrivalRadius))
        {
            reachDistance = Mathf.Max(reachDistance, featureArrivalRadius);
        }
        if (planar > reachDistance) return false;

        // 地面车不做高度判定；无人机做宽松高度判定。
        if (agentProperties != null && agentProperties.Type == AgentType.Quadcopter)
        {
            float dy = Mathf.Abs(cur.y - goal.y);
            if (dy > Mathf.Max(0.2f, stepTargetReachHeightTolerance)) return false;
        }
        return true;
    }

    private bool IsFeatureTargetSatisfiedByPerception(StepTargetBinding target)
    {
        if (!target.HasTarget || target.TargetKind != "Feature") return false;
        if (agentState?.DetectedCampusFeatures == null || agentState.DetectedCampusFeatures.Count == 0) return false;

        float featureArrivalRadius = stepTargetReachDistance;
        if (TryGetFeatureArrivalRadius(target, out float estimatedRadius))
        {
            featureArrivalRadius = estimatedRadius;
        }
        float perceptionReach = Mathf.Max(featureArrivalRadius, Mathf.Max(stepTargetReachDistance, 12f));

        for (int i = 0; i < agentState.DetectedCampusFeatures.Count; i++)
        {
            CampusFeaturePerceptionData data = agentState.DetectedCampusFeatures[i];
            if (data == null) continue;

            bool matched =
                (!string.IsNullOrWhiteSpace(target.Uid) &&
                 string.Equals(data.FeatureUid, target.Uid, System.StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.Name) &&
                 string.Equals(data.FeatureName, target.Name, System.StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.RawQuery) &&
                 (string.Equals(data.FeatureName, target.RawQuery, System.StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(data.FeatureUid, target.RawQuery, System.StringComparison.OrdinalIgnoreCase)));
            if (!matched) continue;

            float planar = Vector2.Distance(
                new Vector2(transform.position.x, transform.position.z),
                new Vector2(data.AnchorWorldPosition.x, data.AnchorWorldPosition.z)
            );
            if (planar <= perceptionReach)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetFeatureArrivalRadius(StepTargetBinding target, out float radius)
    {
        radius = 0f;
        if (!target.HasTarget || target.TargetKind != "Feature" || campusGrid == null) return false;

        string featureToken = !string.IsNullOrWhiteSpace(target.Uid)
            ? target.Uid
            : (!string.IsNullOrWhiteSpace(target.Name) ? target.Name : target.RawQuery);
        if (string.IsNullOrWhiteSpace(featureToken)) return false;

        return TryEstimateFeatureArrivalRadius(featureToken, out radius);
    }

    private bool TryGetFeatureArrivalRadiusByResolvedMeta(Dictionary<string, string> resolvedMeta, Vector3 resolvedPos, out float radius)
    {
        radius = 0f;
        if (resolvedMeta == null) return false;

        if (resolvedMeta.TryGetValue("targetFeature", out string featureToken) &&
            !string.IsNullOrWhiteSpace(featureToken) &&
            TryEstimateFeatureArrivalRadius(featureToken, out radius))
        {
            return true;
        }

        if (resolvedMeta.TryGetValue("targetUid", out string featureUid) &&
            !string.IsNullOrWhiteSpace(featureUid) &&
            TryEstimateFeatureArrivalRadius(featureUid, out radius))
        {
            return true;
        }

        if (campusGrid != null && campusGrid.TryGetCellFeatureInfoByWorld(resolvedPos, out _, out string uid, out string name, out _, out _))
        {
            string token = !string.IsNullOrWhiteSpace(uid) ? uid : name;
            if (!string.IsNullOrWhiteSpace(token) && TryEstimateFeatureArrivalRadius(token, out radius))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryEstimateFeatureArrivalRadius(string featureToken, out float radius)
    {
        radius = 0f;
        if (campusGrid == null || string.IsNullOrWhiteSpace(featureToken)) return false;

        List<Vector2Int> cells = new List<Vector2Int>();
        string q = featureToken.Trim();

        if (campusGrid.cellFeatureUidGrid != null)
        {
            for (int x = 0; x < campusGrid.gridWidth; x++)
            {
                for (int z = 0; z < campusGrid.gridLength; z++)
                {
                    string uid = campusGrid.cellFeatureUidGrid[x, z];
                    if (!string.IsNullOrWhiteSpace(uid) &&
                        string.Equals(uid, q, System.StringComparison.OrdinalIgnoreCase))
                    {
                        cells.Add(new Vector2Int(x, z));
                    }
                }
            }
        }

        if (cells.Count == 0)
        {
            cells = campusGrid.GetCellsByFeatureName(q, ignoreCase: true);
        }

        if (cells == null || cells.Count == 0)
        {
            radius = featureArrivalMinRadius;
            return true;
        }

        int minX = cells.Min(c => c.x);
        int maxX = cells.Max(c => c.x);
        int minZ = cells.Min(c => c.y);
        int maxZ = cells.Max(c => c.y);
        float width = (maxX - minX + 1) * campusGrid.cellSize;
        float length = (maxZ - minZ + 1) * campusGrid.cellSize;
        float footprintRadius = Mathf.Max(width, length) * featureArrivalCellRadiusScale;
        float clampedMax = Mathf.Max(featureArrivalMinRadius, featureArrivalMaxRadius);
        radius = Mathf.Clamp(footprintRadius, featureArrivalMinRadius, clampedMax);
        return true;
    }

    // 执行通信动作
    private void ExecuteCommunicationAction(ActionCommand action)
    {
        try
        {
            // 从参数中解析消息类型和内容
            MessageType messageType = MessageType.StatusUpdate;
            string content = "";
            
            if (!string.IsNullOrEmpty(action.Parameters))
            {
                Dictionary<string, string> pmap = ParseLooseParameterMap(action.Parameters);
                if (pmap.TryGetValue("messageType", out string msgTypeRaw))
                {
                    System.Enum.TryParse<MessageType>(msgTypeRaw, true, out messageType);
                }
                if (pmap.TryGetValue("content", out string c))
                {
                    content = c;
                }
            }
            
            // 如果内容为空，生成默认内容
            if (string.IsNullOrEmpty(content))
            {
                content = $"执行{action.ActionType}动作，步骤:{GetCurrentStepDescription()}";
            }
            
            // 发送消息
            commModule.SendMessage(new AgentMessage {
                SenderID = agentProperties.AgentID,
                ReceiverID = "All",
                Type = messageType,
                Priority = GetMessagePriority(messageType),
                Timestamp = Time.time,
                Content = content
            });
            
            Debug.Log($"发送消息: {messageType} - {content}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"通信动作执行失败: {e.Message}");
        }
    }

    // 根据消息类型获取优先级
    private int GetMessagePriority(MessageType messageType)
    {
        switch (messageType)
        {
            case MessageType.HelpRequest:
            case MessageType.RequestHelp:
            case MessageType.ObstacleWarning:
            case MessageType.EnvironmentAlert:
                return 3; // 高优先级
            case MessageType.TaskCompletion:
            case MessageType.TaskAbort:
            case MessageType.ResourceRequest:
                return 2; // 中优先级
            default:
                return 1; // 低优先级
        }
    }

    // 合并参数字典
    private string MergeParameters(string existingParams, Dictionary<string, string> newParams)
    {
        Dictionary<string, string> merged = ParseLooseParameterMap(existingParams);
        foreach (var kv in newParams) merged[kv.Key] = kv.Value;
        return BuildJsonFromParameterMap(merged);
    }

    /// <summary>
    /// 当前回退策略：不注入额外动作。
    /// 保留该接口用于后续接入 Hover/Stop 等保底行为。
    /// </summary>
    private void ExecuteDefaultAction()
    {
        
    }
    // 在当前感知到的小节点中通过名称查找物体
    private bool FindObjectByNameInPerception(string objectName, out GameObject found)
    {
        found = null;
        if (agentState?.DetectedSmallNodes == null || string.IsNullOrWhiteSpace(objectName))
            return false;

        string searchName = objectName.ToLower();
        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            SmallNodeData node = agentState.DetectedSmallNodes[i];
            if (node == null || node.SceneObject == null) continue;

            string display = string.IsNullOrWhiteSpace(node.DisplayName) ? node.SceneObject.name : node.DisplayName;
            if (display.ToLower().Contains(searchName) || node.SceneObject.name.ToLower().Contains(searchName))
            {
                found = node.SceneObject;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断是否是无目标标记。
    /// </summary>
    private static bool IsNoneTargetToken(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return true;
        string t = target.Trim().ToLowerInvariant();
        return t == "none" || t == "no target" || t == "无目标" || t == "null";
    }

    /// <summary>
    /// 解析世界坐标目标：支持 world(x,z)、world(x,y,z)、(x,z)、x,z。
    /// </summary>
    private bool TryParseWorldTarget(string target, out Vector3 worldPos)
    {
        worldPos = transform.position;
        if (string.IsNullOrWhiteSpace(target)) return false;

        string t = target.Trim();
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            t,
            @"^world\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*(?:,\s*(-?\d+(?:\.\d+)?)\s*)?\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        if (!m.Success)
        {
            m = System.Text.RegularExpressions.Regex.Match(
                t,
                @"^\(?\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*(?:,\s*(-?\d+(?:\.\d+)?)\s*)?\)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        if (!m.Success) return false;

        bool okX = float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
        bool ok2 = float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float second);
        if (!okX || !ok2) return false;

        if (m.Groups[3].Success)
        {
            bool okZ = float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float z3);
            if (!okZ) return false;
            worldPos = new Vector3(x, second, z3);
        }
        else
        {
            worldPos = new Vector3(x, transform.position.y, second);
        }

        return true;
    }

    /// <summary>
    /// 按 nodeId 查找小节点。
    /// </summary>
    private bool TryFindSmallNodeById(string nodeId, out GameObject obj, out Vector3 pos)
    {
        obj = null;
        pos = transform.position;
        if (agentState == null || string.IsNullOrWhiteSpace(nodeId)) return false;

        if (agentState.NearbySmallNodes != null &&
            agentState.NearbySmallNodes.TryGetValue(nodeId, out SmallNodeData byId) &&
            byId != null)
        {
            obj = byId.SceneObject;
            pos = byId.SceneObject != null ? byId.SceneObject.transform.position : byId.WorldPosition;
            return true;
        }

        if (agentState.DetectedSmallNodes == null) return false;
        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            SmallNodeData n = agentState.DetectedSmallNodes[i];
            if (n == null || string.IsNullOrWhiteSpace(n.NodeId)) continue;
            if (!string.Equals(n.NodeId, nodeId, System.StringComparison.OrdinalIgnoreCase)) continue;
            obj = n.SceneObject;
            pos = n.SceneObject != null ? n.SceneObject.transform.position : n.WorldPosition;
            return true;
        }
        return false;
    }

    private SmallNodeData GetSmallNodeDataById(string nodeId)
    {
        if (agentState == null || string.IsNullOrWhiteSpace(nodeId)) return null;

        if (agentState.NearbySmallNodes != null &&
            agentState.NearbySmallNodes.TryGetValue(nodeId, out SmallNodeData byId) &&
            byId != null)
        {
            return byId;
        }

        if (agentState.DetectedSmallNodes == null) return null;
        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            SmallNodeData n = agentState.DetectedSmallNodes[i];
            if (n == null || string.IsNullOrWhiteSpace(n.NodeId)) continue;
            if (string.Equals(n.NodeId, nodeId, System.StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
        }

        return null;
    }

    private static void AppendSmallNodeMetadata(Dictionary<string, string> meta, SmallNodeData node)
    {
        if (meta == null || node == null) return;
        if (!string.IsNullOrWhiteSpace(node.NodeId)) meta["targetNodeId"] = node.NodeId;
        meta["targetNodeType"] = node.NodeType.ToString();
        meta["targetDynamic"] = node.IsDynamic ? "1" : "0";
        meta["targetBlocksMovement"] = node.BlocksMovement ? "1" : "0";
        if (!string.IsNullOrWhiteSpace(node.DisplayName)) meta["targetDisplayName"] = node.DisplayName;
    }

    /// <summary>
    /// 按 displayName、SceneObject 名称或 SmallNodeType 语义查找最近小节点。
    /// 允许第一阶段 target 只保留自然语言中的“树/车辆/资源点”等语义词，
    /// 再由系统在当前感知小节点里解析出具体对象。
    /// </summary>
    private bool TryFindSmallNodeBySemantic(string target, out SmallNodeData matchedNode, out GameObject obj, out Vector3 pos)
    {
        matchedNode = null;
        obj = null;
        pos = transform.position;
        if (agentState?.DetectedSmallNodes == null || string.IsNullOrWhiteSpace(target)) return false;

        string query = target.Trim();
        TryParseSmallNodeTypeToken(query, out SmallNodeType semanticType);
        bool hasType = semanticType != SmallNodeType.Unknown;

        float bestDist = float.MaxValue;
        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            SmallNodeData n = agentState.DetectedSmallNodes[i];
            if (n == null) continue;

            bool matched = false;
            if (hasType && n.NodeType == semanticType)
            {
                matched = true;
            }
            else
            {
                string display = n.DisplayName ?? string.Empty;
                string sceneName = n.SceneObject != null ? n.SceneObject.name : string.Empty;
                string nodeTypeText = n.NodeType.ToString();
                matched =
                    string.Equals(display, query, System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sceneName, query, System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nodeTypeText, query, System.StringComparison.OrdinalIgnoreCase) ||
                    display.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sceneName.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (!matched) continue;

            Vector3 candidatePos = n.SceneObject != null ? n.SceneObject.transform.position : n.WorldPosition;
            float dist = Vector3.Distance(transform.position, candidatePos);
            if (dist >= bestDist) continue;

            bestDist = dist;
            matchedNode = n;
            obj = n.SceneObject;
            pos = candidatePos;
        }

        return matchedNode != null;
    }

    private bool TryParseSmallNodeTypeToken(string raw, out SmallNodeType nodeType)
    {
        nodeType = SmallNodeType.Unknown;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string q = raw.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
        switch (q)
        {
            case "tree":
            case "树":
            case "树木":
                nodeType = SmallNodeType.Tree;
                return true;
            case "pedestrian":
            case "person":
            case "human":
            case "行人":
                nodeType = SmallNodeType.Pedestrian;
                return true;
            case "vehicle":
            case "car":
            case "truck":
            case "车辆":
            case "车":
                nodeType = SmallNodeType.Vehicle;
                return true;
            case "resourcepoint":
            case "resource":
            case "资源点":
            case "资源":
                nodeType = SmallNodeType.ResourcePoint;
                return true;
            case "temporaryobstacle":
            case "obstacle":
            case "障碍":
            case "临时障碍":
                nodeType = SmallNodeType.TemporaryObstacle;
                return true;
            case "agent":
            case "teammate":
            case "队友":
                nodeType = SmallNodeType.Agent;
                return true;
            case "custom":
                nodeType = SmallNodeType.Custom;
                return true;
            default:
                return System.Enum.TryParse(raw.Trim(), true, out nodeType) && nodeType != SmallNodeType.Unknown;
        }
    }

    /// <summary>
    /// 按 AgentID 查找附近队友。
    /// </summary>
    private bool TryFindNearbyAgent(string agentId, out GameObject teammate)
    {
        teammate = null;
        if (agentState?.NearbyAgents == null || string.IsNullOrWhiteSpace(agentId)) return false;

        if (agentState.NearbyAgents.TryGetValue(agentId, out teammate) && teammate != null) return true;

        foreach (var kv in agentState.NearbyAgents)
        {
            GameObject go = kv.Value;
            if (go == null) continue;
            IntelligentAgent ia = go.GetComponent<IntelligentAgent>();
            if (ia != null &&
                ia.Properties != null &&
                string.Equals(ia.Properties.AgentID, agentId, System.StringComparison.OrdinalIgnoreCase))
            {
                teammate = go;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 解析松散参数（JSON 或 key:value 列表）为字典。
    /// </summary>
    private Dictionary<string, string> ParseLooseParameterMap(string raw)
    {
        var map = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(raw)) return map;

        string s = raw.Trim();
        if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
        {
            s = s.Substring(1, s.Length - 2);
        }
        s = s.Replace("\\\"", "\"");

        var matches = System.Text.RegularExpressions.Regex.Matches(
            s,
            @"[""']?(?<k>[A-Za-z_][A-Za-z0-9_]*)[""']?\s*[:=]\s*(?:""(?<dq>(?:\\.|[^""])*)""|'(?<sq>(?:\\.|[^'])*)'|(?<raw>[^,}\r\n]+))"
        );

        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string k = m.Groups["k"].Value.Trim();
            string v = m.Groups["dq"].Success
                ? m.Groups["dq"].Value
                : (m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["raw"].Value);
            v = v.Trim();
            if (string.IsNullOrEmpty(k)) continue;
            v = v.Replace("\\\"", "\"").Replace("\\\\", "\\");
            map[k] = v;
        }

        return map;
    }

    /// <summary>
    /// 把参数字典序列化成紧凑 JSON 字符串。
    /// </summary>
    private string BuildJsonFromParameterMap(Dictionary<string, string> map)
    {
        if (map == null || map.Count == 0) return "{}";
        return "{" + string.Join(",", map.Select(kv =>
        {
            if (float.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                return $"\"{kv.Key}\":{f.ToString(CultureInfo.InvariantCulture)}";
            }
            string escaped = kv.Value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"\"{kv.Key}\":\"{escaped}\"";
        })) + "}";
    }

    /// <summary>
    /// 规范化参数，输出 JSON 字符串，便于 ML 控制器统一解析。
    /// </summary>
    private string NormalizeActionParameters(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters)) return "{}";
        Dictionary<string, string> map = ParseLooseParameterMap(parameters);
        if (map.Count == 0)
        {
            string p = parameters.Trim();
            return (p.StartsWith("{") && p.EndsWith("}")) ? p : "{}";
        }
        return BuildJsonFromParameterMap(map);
    }

    /// <summary>
    /// 从参数字典读取浮点数。
    /// </summary>
    private static bool TryGetFloatFromMap(Dictionary<string, string> map, string key, out float value)
    {
        value = 0f;
        if (map == null || string.IsNullOrWhiteSpace(key)) return false;
        if (!map.TryGetValue(key, out string raw)) return false;
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
    // 查找充电站位置

    // 支持的数据结构
    [System.Serializable]
    private class ActionSequenceWrapper
    {
        public List<ActionData> actions;
    }

    [System.Serializable]
    private class ActionData
    {
        public string actionType;
        public string target;
        public string parameters;
        public string reason;
    }
}
