// Scripts/Modules/ActionDecisionModule.cs
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// ActionDecisionModule 的职责重新对齐为“语义到离散动作的编译层”：
/// 1) 它读取 PlanningModule 给出的当前 PlanStep / StepIntent / Coordination；
/// 2) 它先在系统候选中选目标，再决定当前轮动作；
/// 3) 系统随后做合法性校验、A* 补全和执行兜底；
/// 4) 它不负责团队任务拆解，也不负责连续控制。
/// </summary>
public class ActionDecisionModule : MonoBehaviour
{
    private MemoryModule memoryModule;
    private ReflectionModule reflectionModule;
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

    [Header("动态小节点避让")]
    public bool useSmallNodeDynamicOverlay = true;        // 是否把感知到的小节点投影为临时阻塞格
    public bool overlayStaticBlockingSmallNodes = true;   // 静态但阻塞通行的小节点是否进入临时阻塞层
    public bool overlayDynamicSmallNodes = true;          // 动态小节点是否进入临时阻塞层
    [Range(0, 2)] public int smallNodeOverlayRadiusCells = 1; // 小节点在网格上的临时膨胀半径

    [Header("LLM路径点配置")]
    public bool preferLlmWaypoints = true;                // 优先使用 LLM 在参数中给出的 waypoint
    public bool allowAStarFallbackWhenNoLlmWaypoints = true; // LLM 未给 waypoint 时是否回退 A*
    [Range(4, 30)] public int maxCampusFeaturesInPrompt = 12; // 提示词中最多注入的建筑/地点条目
    public bool disableLlmLongWaypoints = true;           // 禁用 LLM 长 waypoint，改由系统反应式导航

    [Header("两次LLM离散决策")]
    public bool preferTwoStageDiscreteLlmDecision = true; // 当前主路径：先选候选格/对象，再决定动作。
    [Range(4, 64)] public int maxFeatureBoundaryCellsInPrompt = 24; // 单个大节点写进 prompt 的最大候选格数量。
    [Range(1, 16)] public int maxObjectCandidatesInPrompt = 8; // 写进 prompt 的对象候选上限

    [Header("反应式导航执行")]
    [Range(1, 6)] public int reactiveAStarSegmentWaypoints = 2; // 每次只执行前N个A*子目标，下一轮再重规划
    [Min(0.5f)] public float stepTargetReachDistance = 8f;       // 判定“已到达步骤目标”的平面距离阈值
    [Min(0.2f)] public float stepTargetReachHeightTolerance = 2f; // 判定“已到达步骤目标”的高度阈值
    [Min(1f)] public float featureArrivalMinRadius = 6f;         // 建筑/地点目标的最小接近半径
    [Min(0.5f)] public float featureArrivalCellRadiusScale = 0.75f; // 按网格覆盖范围估算建筑接近半径时的缩放系数
    [Min(1f)] public float featureArrivalMaxRadius = 10f;        // 建筑/地点目标的最大接近半径，防止“大建筑半径过大导致过早判到达”

    [Header("决策性能优化")]
    [Range(1, 10)] public int fastStaticReactiveSegmentWaypoints = 5; // 静态目标且局部无明显动态障碍时，一次执行更长的 A* 子段
    [Min(2f)] public float deterministicNavigationObstacleRadius = 24f; // 判定“局部简单，可直接走系统导航”的邻域半径

    [Header("离散高度层")]
    [Min(0.5f)] public float lowAltitudeLayerHeight = 4f;        // 低空层高度
    [Min(1f)] public float mediumAltitudeLayerHeight = 8f;       // 中空层高度
    [Min(1.5f)] public float highAltitudeLayerHeight = 12f;      // 高空层高度
    [Min(0.5f)] public float finalApproachAltitudeLayerHeight = 5f; // 末段接近/观察时的默认高度

    [Header("A*粗路径可视化")]
    public bool showCoarsePathGizmos = true;              // 是否显示粗路径 Gizmos
    public Color coarsePathColor = new Color(0.10f, 0.90f, 0.95f, 1f); // 粗路径颜色
    public Color coarsePathStartColor = new Color(0.25f, 1.00f, 0.40f, 1f); // 起点颜色
    public Color coarsePathEndColor = new Color(1.00f, 0.35f, 0.25f, 1f); // 终点颜色
    public Color coarsePathAnchorColor = new Color(1.00f, 0.85f, 0.20f, 1f); // via/检查点颜色
    [Min(0.02f)] public float coarsePathPointRadius = 0.25f; // 粗路径点半径
    [Min(0.02f)] public float coarsePathAnchorRadius = 0.34f; // 检查点半径
    [Min(0.02f)] public float coarsePathEndpointRadius = 0.42f; // 起终点半径
    [Min(0.5f)] public float coarsePathGizmoDuration = 3f; // 粗路径显示持续时间
    public bool showCoarsePathGameView = true;            // 是否在 Game 视图显示粗路径线
    [Min(0.01f)] public float coarsePathLineWidth = 0.12f; // Game 视图粗路径线宽
    public bool keepCoarsePathUntilMissionComplete = true; // 是否保持显示到任务完成（忽略持续时间）
    private List<Vector3> lastCoarsePathForViz = new List<Vector3>(); // 最近一次粗路径（用于可视化）
    private List<int> lastCoarsePathAnchorIndices = new List<int>(); // 最近一次粗路径中的结构化检查点索引
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
        public string TargetKind;  // Feature/World/Grid/SmallNode/Agent/Self/None
        public string RawQuery;    // 从 step/mission 提取到的原始查询
        public string Uid;
        public string Name;
        public string Source;
        public Vector3 WorldPos;
        public Vector2Int GridCell;
        public SmallNodeType SmallNodeType;
        public bool IsDynamicTarget;
        public bool BlocksMovement;
        public Vector3 FeatureCenterWorld;
        public Vector2Int FeatureCenterCell;
        public float FeatureArrivalRadius;
        public float FeatureOrbitRadius;
        public int FeatureOccupiedCellCount;
        public string CollectionKey;
        public string Summary;
    }

    /// <summary>
    /// 第一次 LLM 选择后的运行时目标。
    /// 这里保存的已经不是“自然语言目标”，而是“候选ID + 最终格 + 可选中间格”。
    /// </summary>
    private struct DiscreteTargetSelectionRuntime
    {
        public bool IsValid;
        public string SelectedCandidateId;
        public string CandidateKind;
        public string DisplayName;
        public GameObject TargetObject;
        public Vector3 WorldPos;
        public Vector2Int GoalCell;
        public List<Vector2Int> IntermediateCells;
        public string Summary;
    }

    private StepTargetBinding lastStepTargetBinding;
    private string lastStepTargetBindingStep = string.Empty;
    private TargetCandidateBundle lastTargetCandidateBundle;
    private DiscreteTargetSelectionRuntime lastDiscreteTargetSelection;
    private string lastDiscreteTargetSelectionStep = string.Empty;
    private bool lastIssuedSequenceContainsMovement;
    private string rollingRouteProgressStep = string.Empty;
    private int rollingRouteConsumedViaCount;

    void Start()
    {
        memoryModule = GetComponent<MemoryModule>();
        reflectionModule = GetComponent<ReflectionModule>();
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
    /// 统一构建当前 step 的导航判定。
    /// 这里的职责不是直接生成路径，而是先把“这一 step 到底算不算移动步、允不允许 A*”
    /// 这两个高层布尔量固定下来，后面所有动作编译和路径展开都只消费这份结论。
    /// </summary>
    /// <param name="currentStep">
    /// 当前正在执行的步骤文本。
    /// 它通常来自 <see cref="GetCurrentStepDescription"/>，本质上等于
    /// <c>planningModule.currentPlan.steps[currentPlan.currentStep]</c>。
    /// </param>
    /// <returns>
    /// 返回一个轻量判定对象：
    /// 1) <c>IsMovementStep</c> 表示该 step 是否属于移动型；
    /// 2) <c>AllowAStarByStep</c> 表示该 step 在策略上是否允许 A*；
    /// 3) <c>Reason</c> 仅用于日志、调试面板和 prompt 注入。
    /// </returns>
    private StepNavigationDecision BuildStepNavigationDecision(string currentStep)
    {
        // 先准备一份“默认值”结果。
        // 这样即使后面 PlanningModule 缺失，调用方仍能拿到稳定结构，而不是空对象。
        StepNavigationDecision decision = new StepNavigationDecision
        {
            IsMovementStep = false,      // 默认先认为当前 step 不是移动步。
            AllowAStarByStep = false,    // 默认先认为本 step 不允许全局 A*。
            Reason = "默认局部机动"        // 这条文本会在日志中说明为什么得到当前判定。
        };

        // 如果 PlanningModule 没挂上，就无法读取结构化 step 类型和导航模式，
        // 这时只能保留上面的默认结果并显式写明回退原因。
        if (planningModule == null)
        {
            decision.Reason = "PlanningModule缺失，回退局部机动";
            return decision;
        }

        // 读取任务级默认导航策略，例如 Auto / PreferLocal / PreferGlobalAStar。
        NavigationPolicy missionPolicy = planningModule.GetCurrentNavigationPolicy();
        // 判断当前 step 是否是移动型步骤，依据的是 PlanningModule 输出的结构化 action/nav hint。
        bool isMovement = planningModule.IsMovementLikeStep(currentStep);
        // 判断当前 step 是否更偏通信/观察步骤；主要用于解释性日志，不直接控制路径生成。
        bool isCommObs = planningModule.IsCommunicationOrObservationStep(currentStep);
        // 判断当前 step 是否是局部机动步骤；同样主要用于解释“为什么没有强制 A*”。
        bool isLocal = planningModule.IsLikelyLocalStep(currentStep);
        // 真正决定“本 step 策略上是否允许 A*”的布尔量。
        bool preferAStar = planningModule.ShouldPreferAStarForStep(currentStep);

        // 把上面拆出来的几个信号合并回统一判定结构，供后续流程直接消费。
        decision.IsMovementStep = isMovement;
        decision.AllowAStarByStep = preferAStar;
        // 把关键上下文压成一个短字符串，方便日志中快速看出策略来源。
        decision.Reason = $"policy={missionPolicy},move={(isMovement ? 1 : 0)},commObs={(isCommObs ? 1 : 0)},local={(isLocal ? 1 : 0)}";
        return decision;
    }

    /// <summary>
    /// 给 LLM 或日志使用的导航策略摘要。
    /// 这里故意只输出很短的压缩串，避免在 prompt 中重复展开整套规则。
    /// </summary>
    /// <param name="decision">
    /// 当前 step 已经算好的导航判定。
    /// 调用方通常先执行 <see cref="BuildStepNavigationDecision"/>，再把结果传进来。
    /// </param>
    /// <returns>
    /// 一个紧凑字符串，包含任务级策略、step 是否移动型、是否允许 A* 和规则说明。
    /// </returns>
    private string BuildNavigationPromptSummary(StepNavigationDecision decision)
    {
        // 再取一次任务级导航策略，确保日志和 prompt 能看到完整的“任务默认倾向”。
        NavigationPolicy p = planningModule != null ? planningModule.GetCurrentNavigationPolicy() : NavigationPolicy.Auto;
        return $"missionPolicy={p},stepMove={(decision.IsMovementStep ? 1 : 0)},allowAStar={(decision.AllowAStarByStep ? 1 : 0)},rule={decision.Reason}";
    }

    /// <summary>
    /// 生成当前 step 的“有效语义意图”。
    /// 它优先使用 PlanningModule 已产出的结构化 StepIntent，
    /// 如果上游没有给够信息，再用当前绑定目标和高层动作做最小兜底补全。
    /// </summary>
    /// <param name="currentStep">当前步骤文本，用于兜底填充 <c>stepText</c> 和完成条件。</param>
    /// <param name="highLevelActionData">
    /// 第一阶段产出的高层动作序列。
    /// 当 stepIntent.intentType 仍是 Unknown 时，这里会观察这些动作里是否存在移动动作来推断 Navigate。
    /// </param>
    /// <param name="binding">
    /// 当前步骤已经绑定好的主目标。
    /// 当上游 stepIntent 没给 primaryTarget 时，会用这里的结构化目标补进去。
    /// </param>
    /// <returns>
    /// 一份可以继续传给动作编译器的 stepIntent：
    /// 它保证主目标、经过点数组、队友数组等字段至少是“可消费”的稳定状态。
    /// </returns>
    private StepIntentDefinition ResolveEffectiveStepIntent(string currentStep, List<ActionData> highLevelActionData, StepTargetBinding binding)
    {
        // 第一优先级：直接取 PlanningModule 当前 step 的结构化意图。
        StepIntentDefinition plannedIntent = planningModule != null ? planningModule.GetCurrentStepIntent() : null;
        // 如果上游没给结构化意图，就在动作层构造一个最小可执行兜底版本。
        if (plannedIntent == null)
        {
            plannedIntent = new StepIntentDefinition
            {
                stepText = currentStep,                                                                // 直接沿用当前步骤文本。
                intentType = StepIntentType.Navigate,                                                 // 没有更强信息时，默认按“导航到目标”理解。
                primaryTarget = binding.HasTarget ? binding.RawQuery : "none",                        // 如果当前已绑定目标，就把原查询文本写回旧字段。
                primaryTargetRef = binding.HasTarget ? BuildLegacyStructuredTargetReference(binding.RawQuery) : null, // 同步补一个兼容旧链路的结构化目标引用。
                orderedViaTargets = new string[0],                                                    // 默认没有中间检查点。
                orderedViaTargetRefs = new StructuredTargetReference[0],                              // 与上面的旧字符串数组保持一致。
                requestedTeammateIds = new string[0],                                                 // 默认没有协同队友。
                observationFocus = "none",                                                            // 默认不是观察型 step。
                communicationGoal = "none",                                                           // 默认没有通信目标。
                finalBehavior = "arrive",                                                             // 默认到达即完成。
                completionCondition = currentStep                                                     // 兜底时把步骤文本本身当完成描述。
            };
        }

        // 如果 Planning 提供的 intent 没写清 primaryTarget，但本轮已经成功绑定了目标，
        // 就把目标从 binding 回灌到 intent，保证后续编译链只看同一份目标。
        if ((string.IsNullOrWhiteSpace(plannedIntent.primaryTarget) || plannedIntent.primaryTarget == "none") && binding.HasTarget)
        {
            plannedIntent.primaryTarget = binding.RawQuery;
            plannedIntent.primaryTargetRef = BuildLegacyStructuredTargetReference(binding.RawQuery);
        }

        // 这里统一把当前 step 的目标收口到结构化目标引用。
        // 即使 Planning 暂时还只给了旧字符串，Action 也会先包成正式对象再继续往下走。
        // 无论上游给的是旧字符串还是新结构体，这里都统一确保 primaryTargetRef 不为空。
        plannedIntent.primaryTargetRef = plannedIntent.primaryTargetRef ?? BuildLegacyStructuredTargetReference(plannedIntent.primaryTarget);

        // 如果 intentType 仍未知，但第一阶段动作里已经明确出现移动动作，
        // 则把该 step 的意图提升为 Navigate，避免后续动作编译器直接失败。
        if (plannedIntent.intentType == StepIntentType.Unknown &&
            highLevelActionData != null &&
            highLevelActionData.Any(a => IsMovementLikeActionData(a)))
        {
            plannedIntent.intentType = StepIntentType.Navigate;
        }

        // 把所有数组字段都收口到非 null，后面的代码就可以直接遍历而不必反复判空。
        plannedIntent.orderedViaTargets = plannedIntent.orderedViaTargets ?? new string[0];
        plannedIntent.orderedViaTargetRefs = plannedIntent.orderedViaTargetRefs ?? new StructuredTargetReference[0];
        plannedIntent.requestedTeammateIds = plannedIntent.requestedTeammateIds ?? new string[0];
        // 如果 stepText 缺失，就回填当前步骤文本，避免日志和记忆里出现空步骤名。
        plannedIntent.stepText = string.IsNullOrWhiteSpace(plannedIntent.stepText) ? currentStep : plannedIntent.stepText;
        // 把结构化目标引用重新解析成人类可读查询串，保证旧字段与新字段语义对齐。
        plannedIntent.primaryTarget = ResolveStructuredTargetQuery(plannedIntent.primaryTargetRef, plannedIntent.primaryTarget);
        // 同理，把经过点列表也统一成“结构化引用 + 可读字符串”一致的状态。
        plannedIntent.orderedViaTargets = ResolveStructuredTargetQueries(plannedIntent.orderedViaTargetRefs, plannedIntent.orderedViaTargets);
        return plannedIntent;
    }

    /// <summary>
    /// 生成当前 step 的有效路径策略。
    /// 优先使用 PlanningModule 已给出的 RoutePolicy；
    /// 如果上游没给，则构造一份“允许全局 A*、允许局部绕行、受阻重规划”的中性默认策略。
    /// </summary>
    /// <returns>
    /// 一份可直接用于动作参数注入和路径展开判断的 routePolicy。
    /// </returns>
    private RoutePolicyDefinition ResolveEffectiveRoutePolicy()
    {
        // 第一优先级：读取当前 step 自己的结构化路径策略。
        RoutePolicyDefinition routePolicy = planningModule != null ? planningModule.GetCurrentStepRoutePolicy() : null;
        // 如果上游没有提供路径策略，就创建一份最常见、最保守的中性配置。
        if (routePolicy == null)
        {
            routePolicy = new RoutePolicyDefinition
            {
                altitudeMode = RouteAltitudeMode.Default,                  // 默认高度策略由动作层自己选。
                clearance = RouteClearancePreference.Medium,               // 默认保持中等安全边际。
                avoidNodeTypes = new SmallNodeType[0],                    // 默认不额外强制避让某类小节点。
                avoidFeatureNames = new string[0],                        // 默认不显式绕开某些地点。
                allowGlobalAStar = true,                                  // 默认允许生成全局粗路径。
                allowLocalDetour = true,                                  // 默认允许局部避障时临时绕行。
                blockedPolicy = BlockedPolicyType.Replan                  // 默认受阻后重新规划。
            };
        }

        // 把数组字段统一成非 null，避免后续拼参数时还要额外判空。
        routePolicy.avoidNodeTypes = routePolicy.avoidNodeTypes ?? new SmallNodeType[0];
        routePolicy.avoidFeatureNames = routePolicy.avoidFeatureNames ?? new string[0];
        return routePolicy;
    }

    /// <summary>
    /// 把当前 step 的结构化语义直接编译成确定性离散动作。
    /// 设计目标：
    /// 1) 优先消费 PlanningModule 已给出的 StepIntent / RoutePolicy；
    /// 2) 对 Navigate / Observe / Communicate / Follow 这类常见步骤，不再每轮都询问 LLM；
    /// 3) 生成的仍然是现有 ActionData / ActionCommand，可无缝复用现有执行器；
    /// 4) 如果遇到表达不充分的步骤，再回退到 LLM 高层动作生成。
    /// </summary>
    /// <summary>
    /// 尝试把当前 step 直接编译成确定性的高层动作序列。
    /// <summary>
    /// 判断终端行为是否明确要求做外围闭环巡航。
    /// </summary>
    private static bool IsRingLikeFinalBehavior(string finalBehavior)
    {
        if (string.IsNullOrWhiteSpace(finalBehavior)) return false;
        string behavior = finalBehavior.Trim();
        return string.Equals(behavior, "orbit", System.StringComparison.OrdinalIgnoreCase) ||
               string.Equals(behavior, "ring", System.StringComparison.OrdinalIgnoreCase) ||
               string.Equals(behavior, "perimeter", System.StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldBuildRingPathForStep(StepIntentDefinition stepIntent)
    {
        if (stepIntent == null) return false;
        if (IsRingLikeInstructionText(stepIntent.stepText)) return true;

        // finalBehavior 更像“这一步结束前最终做什么”，
        // 但在当前计划链路里它有时会继承自槽位级字段，不能拿来驱动前置移动步骤。
        // 这里只允许观察类步骤用它来触发环绕闭环。
        if (stepIntent.intentType == StepIntentType.Observe &&
            IsRingLikeFinalBehavior(stepIntent.finalBehavior))
        {
            return true;
        }

        // completionCondition 往往是“整个槽位何时完成”的全局描述。
        // 如果这里把它也拿来判定当前 step 是否该环绕，
        // 就会把“先去检查点，再去目标，最后环绕”的前置移动步骤误编译成 ring path。
        // 因此环绕只看当前 step 自己的显式文本，或上游明确给出的 ring/orbit finalBehavior。
        return false;
    }

    private static bool IsRingLikeInstructionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string normalized = text.Trim().ToLowerInvariant();
        return normalized.Contains("环绕") ||
               normalized.Contains("围绕") ||
               normalized.Contains("绕一圈") ||
               normalized.Contains("绕圈") ||
               normalized.Contains("外围巡航") ||
               normalized.Contains("外围观察") ||
               normalized.Contains("orbit") ||
               normalized.Contains("ring") ||
               normalized.Contains("perimeter") ||
               normalized.Contains("circle around") ||
               normalized.Contains("loop around");
    }

    /// <summary>
    /// 把逻辑格恢复成当前控制链需要的世界坐标。
    /// </summary>
    private Vector3 ResolveGridCellWorld(Vector2Int cell)
    {
        if (campusGrid == null || !campusGrid.IsInBounds(cell.x, cell.y))
        {
            return transform.position;
        }

        Vector3 world = campusGrid.GridToWorldCenter(cell.x, cell.y, astarWaypointYOffset);
        world.y = ResolveWaypointY(world, true);
        return world;
    }

    /// <summary>
    /// 旧字符串 grounding 兼容函数：从结构化目标里读取接近锚点偏置。
    /// 当前两次 LLM 主路径不再依赖它来决定最终格子；它只给仍未迁完的旧 helper 保底。
    /// </summary>
    private static Vector2Int ResolveTargetAnchorBias(StructuredTargetReference targetRef, string fallbackQuery = "")
    {
        if (targetRef != null)
        {
            Vector2Int explicitBias = new Vector2Int(
                Mathf.Clamp(targetRef.anchorBiasX, -1, 1),
                Mathf.Clamp(targetRef.anchorBiasZ, -1, 1));
            if (explicitBias != Vector2Int.zero)
            {
                return explicitBias;
            }

            if (TryInferAnchorBiasFromText(targetRef.relation, out Vector2Int inferred))
            {
                return inferred;
            }

            string[] fallbackTexts =
            {
                targetRef.anchorText,
                targetRef.displayName,
                targetRef.rawText,
                targetRef.areaHint,
                fallbackQuery
            };

            for (int i = 0; i < fallbackTexts.Length; i++)
            {
                string text = fallbackTexts[i];
                if (!HasSpatialQualifier(text)) continue;
                if (TryInferAnchorBiasFromText(text, out inferred))
                {
                    return inferred;
                }
            }
        }

        if (TryInferAnchorBiasFromText(fallbackQuery, out Vector2Int fallbackBias))
        {
            return fallbackBias;
        }

        return Vector2Int.zero;
    }

    private static bool HasSpatialQualifier(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string normalized = NormalizeSpatialHintText(text);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        return normalized.Contains("东侧") ||
               normalized.Contains("西侧") ||
               normalized.Contains("南侧") ||
               normalized.Contains("北侧") ||
               normalized.Contains("东边") ||
               normalized.Contains("西边") ||
               normalized.Contains("南边") ||
               normalized.Contains("北边") ||
               normalized.Contains("东面") ||
               normalized.Contains("西面") ||
               normalized.Contains("南面") ||
               normalized.Contains("北面") ||
               normalized.Contains("附近") ||
               normalized.Contains("周边") ||
               normalized.Contains("旁边") ||
               normalized.Contains("近旁") ||
               normalized.Contains("周围") ||
               normalized.Contains("east") ||
               normalized.Contains("west") ||
               normalized.Contains("north") ||
               normalized.Contains("south") ||
               normalized.Contains("nearby") ||
               normalized.Contains("near");
    }

    private static bool TryInferAnchorBiasFromText(string text, out Vector2Int bias)
    {
        bias = Vector2Int.zero;
        string normalized = NormalizeSpatialHintText(text);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        if (normalized.Contains("东北") || normalized.Contains("northeast"))
        {
            bias = new Vector2Int(1, 1);
            return true;
        }
        if (normalized.Contains("西北") || normalized.Contains("northwest"))
        {
            bias = new Vector2Int(-1, 1);
            return true;
        }
        if (normalized.Contains("东南") || normalized.Contains("southeast"))
        {
            bias = new Vector2Int(1, -1);
            return true;
        }
        if (normalized.Contains("西南") || normalized.Contains("southwest"))
        {
            bias = new Vector2Int(-1, -1);
            return true;
        }

        if (normalized == "东" || normalized.Contains("东侧") || normalized.Contains("东边") || normalized.Contains("东面") || normalized == "east" || normalized.Contains("eastside"))
        {
            bias = new Vector2Int(1, 0);
            return true;
        }
        if (normalized == "西" || normalized.Contains("西侧") || normalized.Contains("西边") || normalized.Contains("西面") || normalized == "west" || normalized.Contains("westside"))
        {
            bias = new Vector2Int(-1, 0);
            return true;
        }
        if (normalized == "北" || normalized.Contains("北侧") || normalized.Contains("北边") || normalized.Contains("北面") || normalized == "north" || normalized.Contains("northside"))
        {
            bias = new Vector2Int(0, 1);
            return true;
        }
        if (normalized == "南" || normalized.Contains("南侧") || normalized.Contains("南边") || normalized.Contains("南面") || normalized == "south" || normalized.Contains("southside"))
        {
            bias = new Vector2Int(0, -1);
            return true;
        }

        return false;
    }

    private static string NormalizeSpatialHintText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Trim()
            .ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty);
    }

    private static string StripSpatialQualifierText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string result = text.Trim();
        string[] directTokens =
        {
            "东侧", "西侧", "南侧", "北侧",
            "东边", "西边", "南边", "北边",
            "东面", "西面", "南面", "北面",
            "东北侧", "西北侧", "东南侧", "西南侧",
            "东北", "西北", "东南", "西南",
            "附近", "周边", "旁边", "近旁", "周围"
        };

        for (int i = 0; i < directTokens.Length; i++)
        {
            result = result.Replace(directTokens[i], string.Empty);
        }

        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"\b(?:east|west|north|south)(?:side)?\b",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"\b(?:near|nearby)\b",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
        return result;
    }

    /// <summary>
    /// Action 层从结构化目标引用里拿“当前最适合执行的查询字符串”。
    /// 注意这里不是把结构化目标再压回普通字符串当真相源，
    /// 而是把结构化目标里的 executableQuery / entityId / anchorText 等可执行锚点挑出来给当前执行层用。
    /// 真正的语义仍然保留在 StructuredTargetReference 对象里。
    /// </summary>
    private static string ResolveStructuredTargetQuery(StructuredTargetReference targetRef, string fallback = "")
    {
        if (targetRef == null)
        {
            return string.IsNullOrWhiteSpace(fallback) ? string.Empty : fallback.Trim();
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
            fallback
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
    /// 把结构化经过点引用数组临时转成当前导航层还兼容的字符串列表。
    /// 这是迁移过程中的桥接函数：
    /// 上游已经开始传正式目标对象，但粗路径生成逻辑暂时还吃字符串锚点。
    /// </summary>
    private static string[] ResolveStructuredTargetQueries(StructuredTargetReference[] refs, string[] fallback = null)
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

        if (values.Count == 0 && fallback != null)
        {
            values.AddRange(fallback.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
        }

        return values.ToArray();
    }

    /// <summary>
    /// 把旧字符串目标临时包成结构化目标引用。
    /// 这能保证 Action 层后续只面对一种数据形态：StructuredTargetReference。
    /// </summary>
    private static StructuredTargetReference BuildLegacyStructuredTargetReference(string text)
    {
        string value = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        return new StructuredTargetReference
        {
            mode = StructuredTargetMode.Unknown,
            cardinality = string.IsNullOrWhiteSpace(value) ? StructuredTargetCardinality.Unspecified : StructuredTargetCardinality.One,
            rawText = value,
            executableQuery = value,
            entityId = string.Empty,
            entityClass = string.Empty,
            displayName = value,
            selectorText = value,
            collectionKey = string.Empty,
            memberEntityIds = System.Array.Empty<string>(),
            areaHint = string.Empty,
            relation = string.Empty,
            anchorText = value,
            anchorBiasX = 0,
            anchorBiasZ = 0,
            isDynamic = false,
            notes = "action_legacy_target_bridge"
        };
    }

    /// <summary>
    /// 从结构化目标引用直接构建 step 绑定。
    /// 这样如果 Planning 已经给了 targetRef，Action 就先消费这个正式对象，再退回旧字符串字段。
    ///
    /// 现在这条链的职责已经更清楚了：
    /// 1) PlanningModule 先把“1号教学楼 / 所有building”这类自然语言目标落到 grounded targetRef；
    /// 2) ActionDecision 这里只读取 targetRef 里的 executableQuery / entityId / anchorText；
    /// 3) 如果上游还没完全落地，才把 displayName / rawText 当兜底查询继续尝试。
    /// </summary>
    private bool TryBuildStepTargetBindingFromReference(StructuredTargetReference targetRef, out StepTargetBinding binding)
    {
        binding = new StepTargetBinding();
        string query = ResolveStructuredTargetQuery(targetRef);
        if (string.IsNullOrWhiteSpace(query)) return false;
        return TryBuildStepTargetBindingFromTarget(query, targetRef, out binding);
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
        return $"alt={routePolicy.altitudeMode},clear={routePolicy.clearance},avoidNodes={avoidNodes},avoidFeatures={avoidFeatures},astar={(routePolicy.allowGlobalAStar ? 1 : 0)},detour={(routePolicy.allowLocalDetour ? 1 : 0)}";
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
            string shared = ResolveStructuredTargetQuery(d.sharedTargetRef, d.sharedTarget);
            string sharedRef = d.sharedTargetRef != null ? $"mode={d.sharedTargetRef.mode},card={d.sharedTargetRef.cardinality}" : "none";
            parts.Add($"#{i + 1}:mode={d.coordinationMode},leader={d.leaderAgentId},shared={shared},sharedRef={sharedRef},corridor={d.corridorReservationKey},yieldTo={yields},syncPoints={syncPoints},formation={d.formationSlot}");
        }

        return parts.Count > 0 ? string.Join(" || ", parts) : "none";
    }

    /// <summary>
    /// 只根据 PlanningModule 已经产出的结构化结果构建 step_target 绑定。
    /// 可以把它理解成“最后一跳的取值规则”：
    /// 1) 当前 stepIntent.primaryTargetRef 最具体，所以优先用它；
    /// 2) 如果新字段没写清楚，再退回到旧字段 primaryTarget；
    /// 3) 再不够就退回到 assignedSlot.targetRef / target；
    /// 4) 最后才尝试任务级协同里的 sharedTargetRef / sharedTarget。
    /// 换句话说，ActionDecision 在这里不再直接读用户原始自然语言，
    /// 而是只信 PlanningModule 已经整理好的结构化目标字段。
    /// </summary>
    /// <param name="currentStep">当前步骤文本，用于判断缓存的 targetBinding 是否仍然可复用。</param>
    /// <returns>当前步骤的主目标绑定；如果所有来源都失败，则返回一个 <c>HasTarget=false</c> 的空绑定。</returns>
    private StepTargetBinding ResolveStructuredStepTargetBinding(string currentStep)
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
            SmallNodeType = SmallNodeType.Unknown,
            IsDynamicTarget = false,
            BlocksMovement = false,
            FeatureCenterWorld = transform.position,
            FeatureCenterCell = new Vector2Int(-1, -1),
            FeatureArrivalRadius = 0f,
            FeatureOrbitRadius = 0f,
            FeatureOccupiedCellCount = 0,
            CollectionKey = string.Empty,
            Summary = "none"
        };

        // 如果根本没有 PlanningModule，就不可能继续读取结构化 stepIntent / slot / coordination。
        if (planningModule == null) return empty;
        // 对静态目标类型，优先复用同一步里上次已经解析好的 binding，
        // 避免每一轮滚动重规划都重复解析同一栋楼或同一个世界点。
        if (!string.IsNullOrWhiteSpace(currentStep) &&
            string.Equals(lastStepTargetBindingStep, currentStep, System.StringComparison.Ordinal) &&
            lastStepTargetBinding.HasTarget &&
            (lastStepTargetBinding.TargetKind == "Feature" || lastStepTargetBinding.TargetKind == "World" || lastStepTargetBinding.TargetKind == "Grid"))
        {
            return lastStepTargetBinding;
        }

        // 第一优先级：当前 step 自己声明的 primaryTarget。
        // 这是“这一小步现在最想处理谁”的直接答案，粒度最细。
        StepIntentDefinition stepIntent = planningModule.GetCurrentStepIntent();
        // 先尝试正式结构化目标引用。
        if (stepIntent != null &&
            stepIntent.primaryTargetRef != null &&
            TryBuildStepTargetBindingFromReference(stepIntent.primaryTargetRef, out StepTargetBinding fromIntentRef))
        {
            fromIntentRef.Source = $"StructuredStepIntentRef:{fromIntentRef.Source}";
            return fromIntentRef;
        }
        // 如果新字段还没完全落地，再退回到旧的 primaryTarget 字符串。
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
        // 先尝试槽位自己的结构化目标引用。
        if (assignedSlot != null &&
            assignedSlot.targetRef != null &&
            TryBuildStepTargetBindingFromReference(assignedSlot.targetRef, out StepTargetBinding fromSlotRef))
        {
            fromSlotRef.Source = $"AssignedSlotRef:{fromSlotRef.Source}";
            return fromSlotRef;
        }
        // 如果结构化引用还没给够，再退回到槽位级旧字符串目标。
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
            // 逐条尝试协同共享目标，因为当前任务可能包含多个不同模式的协同约束。
            for (int i = 0; i < directives.Length; i++)
            {
                TeamCoordinationDirective directive = directives[i];
                if (directive == null) continue;
                // 先吃 sharedTargetRef 这种正式结构化字段。
                if (directive.sharedTargetRef != null &&
                    TryBuildStepTargetBindingFromReference(directive.sharedTargetRef, out StepTargetBinding fromDirectiveRef))
                {
                    fromDirectiveRef.Source = $"CoordinationDirectiveRef:{fromDirectiveRef.Source}";
                    return fromDirectiveRef;
                }
                // 再退回到 sharedTarget 字符串。
                if (string.IsNullOrWhiteSpace(directive.sharedTarget)) continue;
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
    /// <param name="currentStep">当前步骤文本，用于解析仍未消费的 viaTargets。</param>
    /// <param name="navDecision">当前步骤导航判定，决定本步是否允许构建粗路径。</param>
    /// <param name="stepTarget">当前步骤主目标绑定。</param>
    /// <param name="stepIntent">当前步骤意图，用于读取 orderedViaTargets。</param>
    /// <param name="routePolicy">当前路径策略，用于判断是否允许全局 A* 以及在摘要中输出策略。</param>
    /// <returns>一份粗路径上下文；失败时 <c>HasPath=false</c>，并在 Summary 里写明原因。</returns>
    private CoarsePathContext BuildCoarsePathContextForStep(string currentStep, StepNavigationDecision navDecision, StepTargetBinding stepTarget, StepIntentDefinition stepIntent, RoutePolicyDefinition routePolicy)
    {
        // 如果配置要求“任务结束前一直保留粗路径可视化”为 false，
        // 就在每次重建粗路径前先清掉上一轮的显示缓存。
        if (!keepCoarsePathUntilMissionComplete)
        {
            ClearCoarsePathVisualization();
        }

        // 先构造一个默认失败态上下文，后面任一分支只要成功就覆盖它。
        CoarsePathContext ctx = new CoarsePathContext
        {
            HasPath = false,
            TargetFeatureName = string.Empty,
            GoalWorld = transform.position,
            CoarseWaypoints = new List<Vector3>(),
            WaypointsInline = string.Empty,
            Summary = "无A*粗路径"
        };

        // 非移动型步骤不需要粗路径，例如通信、扫描、等待。
        if (!navDecision.IsMovementStep)
        {
            ctx.Summary = "当前step非移动步骤，无粗路径";
            return ctx;
        }
        // 当前路径策略如果明确禁用了全局 A*，也直接停止在这里。
        if (routePolicy != null && !routePolicy.allowGlobalAStar)
        {
            ctx.Summary = "当前step路径策略关闭了全局A*，交由局部执行层处理";
            return ctx;
        }
        // 粗路径完全依赖 CampusGrid2D；没有逻辑网格就无法继续。
        if (campusGrid == null || campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null)
        {
            ctx.Summary = "CampusGrid2D 不可用";
            return ctx;
        }

        // 某些目标类型不适合预构建粗路径，例如动态队友或动态小节点。
        if (stepTarget.HasTarget && !ShouldBuildCoarsePathForTarget(stepTarget))
        {
            ctx.Summary = $"目标类型={stepTarget.TargetKind}，本轮不构建A*粗路径，交由局部滚动决策";
            return ctx;
        }

        // 先把当前位置投影到一个可通行起点格。
        if (!TryResolveWalkableCell(transform.position, out Vector2Int startCell))
        {
            ctx.Summary = "起点不可用";
            return ctx;
        }

        // 再把“未消费的 viaTargets + 最终主目标”解析成一串路线锚点格。
        if (!TryBuildRouteAnchorCells(currentStep, stepTarget, stepIntent, out List<string> routeAnchorNames, out List<Vector2Int> routeAnchorCells, out string goalSource))
        {
            ctx.Summary = "未在结构化意图/CampusGrid中解析到目标，无法预构建粗路径";
            return ctx;
        }

        // gridPath 保存整条离散格路径；anchorEndGridIndices 记录每个结构化锚点落在整条路径中的结束索引。
        List<Vector2Int> gridPath = new List<Vector2Int>();
        List<int> anchorEndGridIndices = new List<int>();
        // cursor 始终指向当前这段 A* 的起点。
        Vector2Int cursor = startCell;
        // 按顺序把 viaTargets 和最终目标一段段拼起来。
        for (int i = 0; i < routeAnchorCells.Count; i++)
        {
            Vector2Int goalCell = routeAnchorCells[i];
            string featureName = routeAnchorNames[i];

            // 如果当前段目标刚好就是当前位置，就跳过这一段，避免生成零长度路径。
            if (goalCell == cursor)
            {
                continue;
            }

            // 如果锚点格本身不可通行，就尝试找一个最近可通行邻格作为替代终点。
            if (!campusGrid.IsWalkable(goalCell.x, goalCell.y))
            {
                if (!campusGrid.TryFindNearestWalkable(goalCell, 10, out Vector2Int nearGoal))
                {
                    ctx.Summary = $"目标地点不可达: {featureName}";
                    return ctx;
                }
                goalCell = nearGoal;
            }

            // 计算从 cursor 到当前锚点的这一段 A* 路径。
            List<Vector2Int> segmentPath = campusGrid.FindPathAStar(cursor, goalCell);
            // 只要其中一段失败，整条粗路径就视为失败。
            if (segmentPath == null || segmentPath.Count < 2)
            {
                ctx.Summary = $"A*失败: {featureName}";
                return ctx;
            }

            // 如果这不是第一段，就把当前段的起点删除，避免和上一段终点重复。
            if (gridPath.Count > 0 && segmentPath.Count > 0)
            {
                segmentPath.RemoveAt(0);
            }

            // 把这一段拼进总路径。
            gridPath.AddRange(segmentPath);
            if (gridPath.Count > 0)
            {
                // 记录每一段锚点（via / 最终目标）在整条网格路径中的结束位置。
                // 后续即使做 waypoint 压缩，也必须保住这些“结构化检查点”，
                // 否则就会出现“系统算过粗路径，但执行时没有真正经过检查点”的问题。
                anchorEndGridIndices.Add(gridPath.Count - 1);
            }
            // 下一段就从当前锚点继续出发。
            cursor = goalCell;
        }

        // 粗路径 waypoint 需要区分“中途巡航高度”和“最终接近高度”。
        float cruiseY = ResolveWaypointY(transform.position, false);
        float finalY = ResolveWaypointY(transform.position, true);
        // stride 决定网格路径抽样间隔。
        int stride = Mathf.Max(1, astarWaypointStride);
        // waypoints 是最终输出给后续注入和可视化的世界坐标点。
        List<Vector3> waypoints = new List<Vector3>();
        // waypointSourceGridIndices 用于记录每个 waypoint 对应哪一个 gridPath 索引，便于后续压缩时保留锚点。
        List<int> waypointSourceGridIndices = new List<int>();

        // 关键：粗路径第一个点固定为无人机当前位置，保证可视化从机体起始。
        Vector3 startWorld = transform.position;
        startWorld.y = cruiseY;
        waypoints.Add(startWorld);
        waypointSourceGridIndices.Add(-1);

        // 先按固定 stride 抽样整条网格路径。
        HashSet<int> sampledGridIndices = new HashSet<int>();
        for (int i = stride; i < gridPath.Count; i += stride)
        {
            sampledGridIndices.Add(i);
        }
        // 再把转折点补进去，保证粗路径摘要不会因为纯步长抽样而丢掉拐弯信息。
        foreach (int turnIndex in CollectGridTurnIndices(gridPath))
        {
            sampledGridIndices.Add(turnIndex);
        }
        // 最后把每个结构化锚点的终点索引也强制加入采样集合。
        for (int i = 0; i < anchorEndGridIndices.Count; i++)
        {
            sampledGridIndices.Add(anchorEndGridIndices[i]);
        }

        // 按索引顺序把选中的 grid 格转换为世界 waypoint。
        foreach (int idx in sampledGridIndices.OrderBy(v => v))
        {
            if (idx < 0 || idx >= gridPath.Count) continue;
            Vector2Int c = gridPath[idx];
            Vector3 wp = campusGrid.GridToWorldCenter(c.x, c.y, astarWaypointYOffset);
            wp.y = cruiseY;
            // 避免把几乎重合的点重复加入 waypoint 列表。
            if (waypoints.Count == 0 || Vector3.Distance(waypoints[waypoints.Count - 1], wp) > 0.1f)
            {
                waypoints.Add(wp);
                waypointSourceGridIndices.Add(idx);
            }
        }

        // 把最后一个锚点格再显式转成最终目标点，保证路径一定收束到最终目标附近。
        Vector2Int finalGoalCell = routeAnchorCells[routeAnchorCells.Count - 1];
        Vector3 final = campusGrid.GridToWorldCenter(finalGoalCell.x, finalGoalCell.y, astarWaypointYOffset);
        final.y = finalY;
        if (waypoints.Count == 0 || Vector3.Distance(waypoints[waypoints.Count - 1], final) > Mathf.Max(0.5f, campusGrid.cellSize * 0.5f))
        {
            waypoints.Add(final);
            waypointSourceGridIndices.Add(gridPath.Count - 1);
        }

        // 计算允许保留的最大 waypoint 数，同时保证锚点数量一定保得住。
        int maxCount = Mathf.Max(2, astarMaxWaypoints);
        maxCount = Mathf.Max(maxCount, anchorEndGridIndices.Count + 2);
        // 如果 waypoint 还是太多，就做一次“保锚点压缩”。
        if (waypoints.Count > maxCount)
        {
            HashSet<int> preserveWaypointIndices = new HashSet<int> { 0, waypoints.Count - 1 };
            // 先把所有结构化锚点对应的 waypoint 索引标成强制保留。
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

            // 执行真正的压缩，同时返回“压缩后每个 waypoint 来自压缩前哪个索引”的映射。
            waypoints = DownsampleWaypointsPreserveIndices(waypoints, maxCount, preserveWaypointIndices, out List<int> keptIndices);
            waypointSourceGridIndices = keptIndices
                .Where(idx => idx >= 0 && idx < waypointSourceGridIndices.Count)
                .Select(idx => waypointSourceGridIndices[idx])
                .ToList();
        }

        // 把最终结果写回粗路径上下文。
        ctx.HasPath = waypoints.Count > 0;
        ctx.TargetFeatureName = string.Join("->", routeAnchorNames.ToArray());
        ctx.GoalWorld = final;
        ctx.CoarseWaypoints = waypoints;
        ctx.WaypointsInline = BuildWaypointInlineString(waypoints);
        ctx.Summary = ctx.HasPath
            ? $"target={ctx.TargetFeatureName},source={goalSource},policy={BuildRoutePolicySummary(routePolicy)},gridPath={gridPath.Count},coarseWp={waypoints.Count},waypoints={ctx.WaypointsInline}"
            : $"A*失败: {ctx.TargetFeatureName}";

        // 如果粗路径生成成功，就同步刷新场景中的调试可视化缓存。
        if (ctx.HasPath)
        {
            List<Vector3> visualizationPath = BuildGridPathVisualizationPoints(gridPath, startWorld, cruiseY);
            HashSet<int> visualizationAnchorIndices = new HashSet<int>(anchorEndGridIndices.Select(idx => idx + 1));
            UpdateCoarsePathVisualization(visualizationPath, visualizationAnchorIndices);
        }
        return ctx;
    }

    /// <summary>
    /// 把“当前还没消费完的 viaTargets + 最终主目标”整理成一串粗路径锚点格。
    /// 粗路径生成器后面只关心这串锚点，不再直接理解自然语言。
    /// </summary>
    /// <param name="currentStep">当前步骤文本，用于刷新/读取滚动 via 进度。</param>
    /// <param name="stepTarget">当前主目标绑定。</param>
    /// <param name="stepIntent">当前结构化 stepIntent，用于读取 orderedViaTargets。</param>
    /// <param name="routeAnchorNames">输出参数，保存每个锚点的人类可读名称。</param>
    /// <param name="routeAnchorCells">输出参数，保存每个锚点对应的可通行格。</param>
    /// <param name="source">输出参数，记录这些锚点来自哪个解析来源。</param>
    /// <returns>只要至少解析到一个锚点就返回 true。</returns>
    private bool TryBuildRouteAnchorCells(string currentStep, StepTargetBinding stepTarget, StepIntentDefinition stepIntent, out List<string> routeAnchorNames, out List<Vector2Int> routeAnchorCells, out string source)
    {
        // 先初始化三个输出容器。
        routeAnchorNames = new List<string>();
        routeAnchorCells = new List<Vector2Int>();
        source = "none";

        // 先取“当前还没消费过”的 viaTargets。
        string[] viaTargets = ResolvePendingViaTargetsForCurrentStep(currentStep, stepIntent, refreshProgress: true);
        if (viaTargets != null && viaTargets.Length > 0)
        {
            // 按顺序尝试把每个 viaTarget 解析成粗路径锚点格。
            for (int i = 0; i < viaTargets.Length; i++)
            {
                string viaTarget = viaTargets[i];
                if (TryResolveRouteAnchorCell(viaTarget, out string viaName, out Vector2Int viaCell, out string viaSource))
                {
                    routeAnchorNames.Add(viaName);
                    routeAnchorCells.Add(viaCell);
                    source = source == "none" ? viaSource : $"{source}>{viaSource}";
                }
            }
        }

        // 再把当前主目标补到锚点链尾部；这是 viaTargets 之后真正的最终目标。
        if (stepTarget.HasTarget && ShouldBuildCoarsePathForTarget(stepTarget) && TryResolveWalkableCell(stepTarget.WorldPos, out Vector2Int boundCell))
        {
            string boundName = !string.IsNullOrWhiteSpace(stepTarget.Uid)
                ? stepTarget.Uid
                : (!string.IsNullOrWhiteSpace(stepTarget.Name) ? stepTarget.Name : stepTarget.RawQuery);
            routeAnchorNames.Add(boundName);
            routeAnchorCells.Add(boundCell);
            source = source == "none" ? $"StepTarget:{stepTarget.Source}" : $"{source}>StepTarget:{stepTarget.Source}";
        }
        else
        {
            // 如果 stepTarget 还没解析出来，就退回 stepIntent 的 primaryTarget 再试一次。
            string primaryTarget = stepIntent != null
                ? ResolveStructuredTargetQuery(stepIntent.primaryTargetRef, stepIntent.primaryTarget)
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(primaryTarget) &&
                !string.Equals(primaryTarget, "none", System.StringComparison.OrdinalIgnoreCase) &&
                TryResolveRouteAnchorCell(primaryTarget, out string primaryName, out Vector2Int primaryCell, out string primarySource))
            {
                routeAnchorNames.Add(primaryName);
                routeAnchorCells.Add(primaryCell);
                source = source == "none" ? primarySource : $"{source}>{primarySource}";
            }
        }
        // 只要 routeAnchorCells 非空，就说明粗路径可以继续往后构建。
        return routeAnchorCells.Count > 0;
    }

    private void ResetRollingRouteProgressIfNeeded(string currentStep)
    {
        string normalizedStep = currentStep ?? string.Empty;
        if (string.Equals(rollingRouteProgressStep, normalizedStep, System.StringComparison.Ordinal))
        {
            return;
        }

        rollingRouteProgressStep = normalizedStep;
        rollingRouteConsumedViaCount = 0;
    }

    /// <summary>
    /// 根据当前位置刷新“已经走过了多少个 viaTargets”。
    /// 这让同一步的多轮滚动重规划不会每次都从第一个检查点重新开始。
    /// </summary>
    /// <param name="currentStep">当前步骤文本，用于切换步骤时重置进度。</param>
    /// <param name="stepIntent">当前步骤意图，用于读取 orderedViaTargets。</param>
    private void UpdateRollingRouteProgressForCurrentPosition(string currentStep, StepIntentDefinition stepIntent)
    {
        // 先确保当前步骤切换时滚动进度已正确重置。
        ResetRollingRouteProgressIfNeeded(currentStep);

        // 取出当前步骤的完整 viaTargets 列表。
        string[] viaTargets = stepIntent != null
            ? ResolveStructuredTargetQueries(stepIntent.orderedViaTargetRefs, stepIntent.orderedViaTargets)
            : System.Array.Empty<string>();
        // 如果没有任何 viaTargets，就把已消费计数归零。
        if (viaTargets == null || viaTargets.Length == 0)
        {
            rollingRouteConsumedViaCount = 0;
            return;
        }

        // 先把已消费数量限制在合法区间内，防止越界。
        rollingRouteConsumedViaCount = Mathf.Clamp(rollingRouteConsumedViaCount, 0, viaTargets.Length);
        int before = rollingRouteConsumedViaCount;
        // 只要当前位置已经满足当前 viaTarget，就继续向后吞掉一个检查点。
        while (rollingRouteConsumedViaCount < viaTargets.Length &&
               IsRouteAnchorSatisfied(viaTargets[rollingRouteConsumedViaCount]))
        {
            rollingRouteConsumedViaCount++;
        }

        // 如果本轮真的消费掉了新的 viaTarget，就打一条日志方便观察路线推进过程。
        if (rollingRouteConsumedViaCount > before)
        {
            string agentId = agentProperties != null ? agentProperties.AgentID : gameObject.name;
            Debug.Log($"[ActionDecision][RouteProgress][{agentId}] step={currentStep} consumedVia={rollingRouteConsumedViaCount}/{viaTargets.Length}");
        }
    }

    /// <summary>
    /// 解析“当前步骤还没走完的 viaTargets”。
    /// 这个函数返回的不是完整检查点列表，而是从 <c>rollingRouteConsumedViaCount</c> 之后开始的剩余部分。
    /// </summary>
    /// <param name="currentStep">当前步骤文本。</param>
    /// <param name="stepIntent">当前步骤意图。</param>
    /// <param name="refreshProgress">是否在返回前先根据当前位置刷新已消费进度。</param>
    /// <returns>尚未消费的 viaTargets 数组。</returns>
    private string[] ResolvePendingViaTargetsForCurrentStep(string currentStep, StepIntentDefinition stepIntent, bool refreshProgress)
    {
        // 调用方如果要求刷新，就先根据当前位置推进一次已消费进度。
        if (refreshProgress)
        {
            UpdateRollingRouteProgressForCurrentPosition(currentStep, stepIntent);
        }
        else
        {
            // 否则只做“跨步骤时的重置检查”，不动当前已消费计数。
            ResetRollingRouteProgressIfNeeded(currentStep);
        }

        // 取出当前步骤完整的 viaTargets 列表。
        string[] viaTargets = stepIntent != null
            ? ResolveStructuredTargetQueries(stepIntent.orderedViaTargetRefs, stepIntent.orderedViaTargets)
            : System.Array.Empty<string>();
        if (viaTargets == null || viaTargets.Length == 0) return System.Array.Empty<string>();

        // 计算剩余列表从哪里开始切片。
        int startViaIndex = Mathf.Clamp(rollingRouteConsumedViaCount, 0, viaTargets.Length);
        // 还没消费任何 viaTarget 时，直接返回全部列表。
        if (startViaIndex <= 0) return viaTargets;
        // 全部都消费完时，返回空数组。
        if (startViaIndex >= viaTargets.Length) return System.Array.Empty<string>();
        // 否则返回剩余的未消费检查点。
        return viaTargets.Skip(startViaIndex).ToArray();
    }

    /// <summary>
    /// 判断某个路线锚点是否已经被当前位置满足。
    /// 这里会优先按结构化目标绑定做“接近/感知满足”判断，必要时再退回到纯网格接近判断。
    /// </summary>
    /// <param name="targetQuery">要检测的锚点查询串。</param>
    /// <returns>当前位置已满足该锚点则返回 true。</returns>
    private bool IsRouteAnchorSatisfied(string targetQuery)
    {
        // 空查询直接认为已满足，避免空字符串把路线推进卡死。
        if (string.IsNullOrWhiteSpace(targetQuery)) return true;

        // 第一优先级：把锚点解析成正式绑定，再按“接近目标/感知到目标”做语义判断。
        if (TryBuildStepTargetBindingFromTarget(targetQuery, out StepTargetBinding binding) && binding.HasTarget)
        {
            binding = RefreshStepTargetBindingForCompletion(binding);
            if (IsNearStepTarget(binding)) return true;
            if (binding.TargetKind == "Feature" && IsFeatureTargetSatisfiedByPerception(binding)) return true;
        }

        // 第二优先级：退回纯网格锚点判断，比较当前位置和锚点格心的平面距离。
        if (campusGrid != null &&
            TryResolveRouteAnchorCell(targetQuery, out _, out Vector2Int goalCell, out _) &&
            campusGrid.IsInBounds(goalCell.x, goalCell.y))
        {
            Vector3 world = ResolveGridCellWorld(goalCell);
            float planar = Vector2.Distance(
                new Vector2(transform.position.x, transform.position.z),
                new Vector2(world.x, world.z));
            float threshold = Mathf.Max(stepTargetReachDistance, campusGrid.cellSize * 0.75f);
            if (planar <= threshold) return true;
        }

        // 上面两条都不满足，就认为该锚点还没走到。
        return false;
    }

    /// <summary>
    /// 把一个路径锚点查询串解析成“锚点名 + 锚点格 + 解析来源”。
    /// 粗路径构建器后面只消费这个结果。
    /// </summary>
    /// <param name="targetQuery">锚点查询串，例如某个地点、grid(x,z) 或 world(x,z)。</param>
    /// <param name="targetName">输出参数，解析出的锚点名。</param>
    /// <param name="goalCell">输出参数，解析出的可通行目标格。</param>
    /// <param name="source">输出参数，记录解析来源。</param>
    /// <returns>成功解析到锚点格返回 true。</returns>
    private bool TryResolveRouteAnchorCell(string targetQuery, out string targetName, out Vector2Int goalCell, out string source)
    {
        // 先初始化输出参数。
        targetName = string.Empty;
        goalCell = new Vector2Int(-1, -1);
        source = "none";

        // 空查询没有意义，直接失败。
        if (string.IsNullOrWhiteSpace(targetQuery)) return false;

        // 第一优先级：先走统一的 StepTargetBinding 解析链。
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

        // 如果绑定链失败，再退回旧的粗路径目标格解析逻辑。
        return TryResolveGoalCellForCoarsePath(targetQuery, out targetName, out goalCell, out source);
    }

    /// <summary>
    /// 判断某个目标类型是否适合预构建全局粗路径。
    /// 静态地点/世界点/网格点通常可以；动态目标通常不可以。
    /// </summary>
    /// <param name="stepTarget">当前目标绑定。</param>
    /// <returns>适合构建粗路径返回 true。</returns>
    private bool ShouldBuildCoarsePathForTarget(StepTargetBinding stepTarget)
    {
        // 没有目标时当然不能建粗路径。
        if (!stepTarget.HasTarget) return false;
        // 静态地点、世界点和显式网格点都适合做全局路径规划。
        if (stepTarget.TargetKind == "Feature" || stepTarget.TargetKind == "World" || stepTarget.TargetKind == "Grid") return true;
        // 队友目标是动态的，不适合预先算整条粗路径。
        if (stepTarget.TargetKind == "Agent") return false;

        // 小节点只有在“静态且已投影到可通行格”时才适合做粗路径。
        if (stepTarget.TargetKind == "SmallNode")
        {
            if (stepTarget.IsDynamicTarget ||
                stepTarget.SmallNodeType == SmallNodeType.Pedestrian ||
                stepTarget.SmallNodeType == SmallNodeType.Vehicle ||
                stepTarget.SmallNodeType == SmallNodeType.Agent)
            {
                return false;
            }

            return stepTarget.GridCell.x >= 0 && stepTarget.GridCell.y >= 0;
        }

        // 其余类型默认都不建粗路径。
        return false;
    }

    /// <summary>
    /// 刷新粗路径可视化缓存。
    /// </summary>
    private void UpdateCoarsePathVisualization(List<Vector3> waypoints, IEnumerable<int> anchorIndices = null)
    {
        lastCoarsePathForViz.Clear();
        lastCoarsePathAnchorIndices.Clear();
        if (waypoints == null || waypoints.Count == 0) return;
        lastCoarsePathForViz.AddRange(waypoints);
        if (anchorIndices != null)
        {
            lastCoarsePathAnchorIndices.AddRange(anchorIndices
                .Where(idx => idx > 0 && idx < waypoints.Count - 1)
                .Distinct()
                .OrderBy(idx => idx));
        }
        lastCoarsePathVizTime = Time.time;
        UpdateCoarsePathLineRendererNow();
    }

    /// <summary>
    /// 清空粗路径可视化缓存。
    /// </summary>
    private void ClearCoarsePathVisualization()
    {
        lastCoarsePathForViz.Clear();
        lastCoarsePathAnchorIndices.Clear();
        lastCoarsePathVizTime = -999f;
        if (coarsePathLineRenderer != null)
        {
            coarsePathLineRenderer.positionCount = 0;
            coarsePathLineRenderer.enabled = false;
        }
    }

    private Gradient BuildCoarsePathGradient()
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(coarsePathStartColor, 0f),
                new GradientColorKey(coarsePathColor, 0.5f),
                new GradientColorKey(coarsePathEndColor, 1f)
            },
            new[]
            {
                new GradientAlphaKey(coarsePathStartColor.a, 0f),
                new GradientAlphaKey(coarsePathColor.a, 0.5f),
                new GradientAlphaKey(coarsePathEndColor.a, 1f)
            });
        return gradient;
    }

    private Color EvaluateCoarsePathColor(float t)
    {
        if (t <= 0.5f)
        {
            return Color.Lerp(coarsePathStartColor, coarsePathColor, t / 0.5f);
        }

        return Color.Lerp(coarsePathColor, coarsePathEndColor, (t - 0.5f) / 0.5f);
    }

    private bool IsAnchorWaypointIndex(int index)
    {
        return lastCoarsePathAnchorIndices != null && lastCoarsePathAnchorIndices.Contains(index);
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
        coarsePathLineRenderer.colorGradient = BuildCoarsePathGradient();
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

        for (int i = 0; i < lastCoarsePathForViz.Count; i++)
        {
            Vector3 p = lastCoarsePathForViz[i];
            if (i > 0)
            {
                float t = (float)i / Mathf.Max(1, lastCoarsePathForViz.Count - 1);
                Gizmos.color = EvaluateCoarsePathColor(t);
                Gizmos.DrawLine(lastCoarsePathForViz[i - 1], p);
            }

            if (i == 0)
            {
                Gizmos.color = coarsePathStartColor;
                Gizmos.DrawSphere(p, coarsePathEndpointRadius);
            }
            else if (i == lastCoarsePathForViz.Count - 1)
            {
                Gizmos.color = coarsePathEndColor;
                Gizmos.DrawSphere(p, coarsePathEndpointRadius);
            }
            else if (IsAnchorWaypointIndex(i))
            {
                Gizmos.color = coarsePathAnchorColor;
                Gizmos.DrawSphere(p, coarsePathAnchorRadius);
            }
            else
            {
                float t = (float)i / Mathf.Max(1, lastCoarsePathForViz.Count - 1);
                Gizmos.color = EvaluateCoarsePathColor(t);
                Gizmos.DrawSphere(p, coarsePathPointRadius);
            }
        }
    }

    /// <summary>
    /// 为粗路径构建解析目标网格。
    /// 输入必须是已经结构化过的 target token，例如：
    /// label:x_2 / world(10,20) / nodeId / AgentID。
    /// 这里不再从自然语言 step 文本中抽取目标词。
    /// </summary>
    /// <param name="targetQuery">要解析的目标 token。</param>
    /// <param name="targetName">输出参数，记录最终命中的目标名。</param>
    /// <param name="goalCell">输出参数，记录最终命中的目标格。</param>
    /// <param name="source">输出参数，记录命中的来源类型。</param>
    /// <returns>成功解析到目标格返回 true。</returns>
    private bool TryResolveGoalCellForCoarsePath(string targetQuery, out string targetName, out Vector2Int goalCell, out string source)
    {
        // 先初始化输出值。
        targetName = string.Empty;
        goalCell = new Vector2Int(-1, -1);
        source = string.Empty;
        // 空 token 无法继续解析。
        if (string.IsNullOrWhiteSpace(targetQuery)) return false;

        // 去掉首尾空格，后面统一基于规范化后的 token 工作。
        string q = targetQuery.Trim();

        // 如果 token 指向自身/当前位置，就把当前位置投影成一个可通行格。
        if (ContainsSelfReference(q) && TryResolveWalkableCell(transform.position, out goalCell))
        {
            targetName = "self";
            source = "Self";
            return true;
        }

        // 如果 token 本身就是显式 grid(x,z)，直接使用该格。
        if (TryParseGridTarget(q, out Vector2Int explicitGridCell) &&
            campusGrid != null &&
            campusGrid.IsInBounds(explicitGridCell.x, explicitGridCell.y))
        {
            goalCell = explicitGridCell;
            targetName = BuildGridTargetToken(explicitGridCell);
            source = "GridCell";
            return true;
        }

        // 如果 token 是 world(x,z) / world(x,y,z)，先投影到最近可通行格。
        if (TryParseWorldTarget(q, out Vector3 worldPos) &&
            TryResolveWalkableCell(worldPos, out goalCell))
        {
            targetName = q;
            source = "WorldCoordinate";
            return true;
        }

        // 如果当前感知里就能看到该地点，也允许直接从感知锚点恢复成目标格。
        if (TryResolvePerceivedCampusFeatureTarget(q, out Vector3 perceivedFeaturePos) &&
            TryResolveWalkableCell(perceivedFeaturePos, out goalCell))
        {
            targetName = q;
            source = "PerceivedCampus";
            return true;
        }

        // 小节点目标同样先取其世界坐标，再映射到最近可通行格。
        if (TryResolveSmallNodeTargetWorld(q, out Vector3 smallNodePos) &&
            TryResolveWalkableCell(smallNodePos, out goalCell))
        {
            targetName = q;
            source = "PerceivedSmallNode";
            return true;
        }

        // 最后一层：把 token 当成校园地点名，在 CampusGrid2D 静态索引里查。
        if (TryResolveFeatureNameToCell(q, out goalCell))
        {
            targetName = q;
            source = "CampusGrid";
            return true;
        }

        // 所有来源都失败则返回 false。
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
            GridCell = new Vector2Int(-1, -1),
            SmallNodeType = SmallNodeType.Agent,
            IsDynamicTarget = true,
            BlocksMovement = false
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
    /// 解析显式网格目标。
    /// 允许的格式保持非常严格，只接受 grid(x,z) 或 cell(x,z)。
    /// 这样动作层和执行层看到的是稳定的离散网格坐标，而不是再去猜自然语言。
    /// </summary>
    private static bool TryParseGridTarget(string target, out Vector2Int cell)
    {
        cell = new Vector2Int(-1, -1);
        if (string.IsNullOrWhiteSpace(target)) return false;

        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            target.Trim(),
            @"^(?:grid|cell)\s*\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
        {
            return false;
        }

        cell = new Vector2Int(x, z);
        return true;
    }

    /// <summary>
    /// 把逻辑格重新编码成动作层和执行层都能看懂的显式 token。
    /// </summary>
    private static string BuildGridTargetToken(Vector2Int cell)
    {
        return $"grid({cell.x},{cell.y})";
    }

    /// <summary>
    /// 将显式网格 token 转成世界坐标。
    /// 这里的 Y 不由 token 决定，而是继续交给离散高度层/路径策略统一控制。
    /// </summary>
    private bool TryResolveGridTargetWorld(string target, out Vector3 worldPos, out Vector2Int cell)
    {
        worldPos = transform.position;
        cell = new Vector2Int(-1, -1);
        if (campusGrid == null || !TryParseGridTarget(target, out cell)) return false;
        if (!campusGrid.IsInBounds(cell.x, cell.y)) return false;

        worldPos = campusGrid.GridToWorldCenter(cell.x, cell.y, astarWaypointYOffset);
        worldPos.y = ResolveWaypointY(worldPos, true);
        return true;
    }

    /// <summary>
    /// 按稳定身份精确解析单个小节点。
    /// 解析顺序：
    /// 1) 全局共享注册表；
    /// 2) 当前智能体本地感知快照。
    /// 不做模糊包含匹配，也不做节点类型语义猜测。
    /// </summary>
    private bool TryResolveExactSmallNodeBinding(string token, out SmallNodeData node, out GameObject obj, out Vector3 worldPos)
    {
        node = null;
        obj = null;
        worldPos = transform.position;
        if (string.IsNullOrWhiteSpace(token)) return false;

        string q = token.Trim();
        if (SmallNodeRegistry.TryResolveExact(q, out SmallNodeData registryNode) && registryNode != null)
        {
            node = registryNode;
            obj = registryNode.SceneObject;
            worldPos = obj != null ? obj.transform.position : registryNode.WorldPosition;
            return true;
        }

        if (agentState?.NearbySmallNodes != null)
        {
            foreach (KeyValuePair<string, SmallNodeData> kv in agentState.NearbySmallNodes)
            {
                SmallNodeData local = kv.Value;
                if (!IsExactSmallNodeTokenMatch(local, q)) continue;

                node = local;
                obj = local.SceneObject;
                worldPos = obj != null ? obj.transform.position : local.WorldPosition;
                return true;
            }
        }

        if (agentState?.DetectedSmallNodes == null) return false;
        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            SmallNodeData local = agentState.DetectedSmallNodes[i];
            if (!IsExactSmallNodeTokenMatch(local, q)) continue;

            node = local;
            obj = local.SceneObject;
            worldPos = obj != null ? obj.transform.position : local.WorldPosition;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 小节点稳定身份匹配。
    /// 这里只允许 NodeId / DisplayName / SceneObject.name 精确相等。
    /// </summary>
    private static bool IsExactSmallNodeTokenMatch(SmallNodeData node, string token)
    {
        if (node == null || string.IsNullOrWhiteSpace(token)) return false;
        if (!string.IsNullOrWhiteSpace(node.NodeId) &&
            string.Equals(node.NodeId.Trim(), token, System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(node.DisplayName) &&
            string.Equals(node.DisplayName.Trim(), token, System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (node.SceneObject != null &&
            !string.IsNullOrWhiteSpace(node.SceneObject.name) &&
            string.Equals(node.SceneObject.name.Trim(), token, System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// 从已感知小节点中解析目标世界坐标（按 nodeId / displayName / sceneName 匹配）。
    /// </summary>
    private bool TryResolveSmallNodeTargetWorld(string query, out Vector3 worldPos)
    {
        worldPos = transform.position;
        return TryResolveExactSmallNodeBinding(query, out _, out _, out worldPos);
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
    /// 新主路径：
    /// 1) 系统先给离散候选；
    /// 2) 第一次 LLM 选候选对象与目标格；
    /// 3) 第二次 LLM 基于已选格子决定动作；
    /// 4) 最终仍落到现有 ActionCommand/A* 执行链。
    /// </summary>
    private IEnumerator<object> TryDecideNextActionWithDiscreteCandidates(string currentStep, System.Action<bool> onCompleted)
    {
        onCompleted?.Invoke(false);
        if (planningModule == null || llmInterface == null)
        {
            yield break;
        }

        PlanStepDefinition planStep = planningModule.GetCurrentPlanStep();
        if (planStep == null || string.IsNullOrWhiteSpace(planStep.text))
        {
            yield break;
        }

        TargetCandidateBundle candidateBundle = BuildTargetCandidateBundle(planStep);
        if (!HasUsableTargetCandidates(candidateBundle))
        {
            yield break;
        }

        StepNavigationDecision navDecision = BuildStepNavigationDecision(currentStep);
        RoutePolicyDefinition routePolicy = ResolveEffectiveRoutePolicy();
        TeamCoordinationDirective[] coordinationDirectives = planningModule.GetCurrentCoordinationDirectives();

        string selectionPrompt = BuildTargetSelectionPrompt(planStep, candidateBundle, navDecision, coordinationDirectives);
        string selectionResponse = string.Empty;
        yield return llmInterface.SendRequest(selectionPrompt, result =>
        {
            selectionResponse = result ?? string.Empty;
        }, temperature: 0.1f, maxTokens: 520);

        if (string.IsNullOrWhiteSpace(selectionResponse))
        {
            yield break;
        }

        if (!TryParseTargetSelectionResponse(selectionResponse, out TargetSelectionResponse selection, out string selectionError))
        {
            Debug.LogWarning($"[ActionDecision][DiscreteTarget] 目标选择解析失败: {selectionError}");
            yield break;
        }

        DiscreteTargetSelectionRuntime resolvedSelection = ValidateTargetSelection(candidateBundle, selection);
        if (!resolvedSelection.IsValid)
        {
            Debug.LogWarning("[ActionDecision][DiscreteTarget] 目标选择校验失败，回退旧链路。");
            yield break;
        }

        lastTargetCandidateBundle = candidateBundle;
        lastDiscreteTargetSelection = resolvedSelection;
        lastDiscreteTargetSelectionStep = currentStep;
        lastStepTargetBinding = BuildStepTargetBindingFromDiscreteSelection(resolvedSelection);
        lastStepTargetBindingStep = currentStep;

        string actionPrompt = BuildDiscreteActionDecisionPrompt(
            currentStep,
            planStep,
            candidateBundle,
            resolvedSelection,
            navDecision,
            routePolicy,
            coordinationDirectives);
        string actionResponse = string.Empty;
        yield return llmInterface.SendRequest(actionPrompt, result =>
        {
            actionResponse = result ?? string.Empty;
        }, temperature: 0.1f, maxTokens: 520);

        if (string.IsNullOrWhiteSpace(actionResponse))
        {
            yield break;
        }

        List<ActionData> highLevelActionData = ParseActionDataSequenceFromJSON(actionResponse);
        if (highLevelActionData == null || highLevelActionData.Count == 0)
        {
            yield break;
        }

        StepIntentDefinition stepIntent = ResolveEffectiveStepIntent(currentStep, highLevelActionData, lastStepTargetBinding);
        CoarsePathContext coarsePath = BuildCoarsePathContextForStep(currentStep, navDecision, lastStepTargetBinding, stepIntent, routePolicy);

        List<ActionCommand> actionSequence = BuildActionCommandsFromActionData(highLevelActionData);
        actionSequence = ApplyRoutePolicyToActionSequence(actionSequence, currentStep, stepIntent, routePolicy);
        actionSequence = ApplyCoordinationDirectivesToActionSequence(actionSequence, coordinationDirectives);
        actionSequence = ApplyCoarsePathFallbackIfNeeded(actionSequence, coarsePath);
        actionSequence = ExpandMoveActionsByAStar(actionSequence, navDecision);
        lastIssuedSequenceContainsMovement = SequenceContainsMovement(actionSequence);

        if (actionSequence == null || actionSequence.Count == 0)
        {
            yield break;
        }

        agentState.Status = AgentStatus.ExecutingTask;
        StartCoroutine(ExecuteActionSequence(actionSequence, currentStep));
        onCompleted?.Invoke(true);
    }

    private bool HasUsableTargetCandidates(TargetCandidateBundle bundle)
    {
        return bundle != null &&
               ((bundle.featureCandidates != null && bundle.featureCandidates.Length > 0) ||
                (bundle.objectCandidates != null && bundle.objectCandidates.Length > 0));
    }

    private TargetCandidateBundle BuildTargetCandidateBundle(PlanStepDefinition planStep)
    {
        List<FeatureTargetCandidate> featureCandidates = new List<FeatureTargetCandidate>();
        List<ObjectTargetCandidate> objectCandidates = new List<ObjectTargetCandidate>();
        HashSet<string> usedCandidateIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        string targetText = planStep != null ? planStep.targetText ?? string.Empty : string.Empty;
        string queryText = planStep != null ? planStep.text ?? string.Empty : string.Empty;
        string kindHint = planStep != null ? planStep.targetKindHint ?? string.Empty : string.Empty;

        TryAddExplicitCoordinateCandidate(targetText, objectCandidates, usedCandidateIds);
        TryAddExplicitCoordinateCandidate(queryText, objectCandidates, usedCandidateIds);
        TryAddFeatureCandidatesForQuery(targetText, featureCandidates, usedCandidateIds);
        TryAddFeatureCandidatesForQuery(queryText, featureCandidates, usedCandidateIds);
        TryAddCollectionFeatureCandidates(targetText, featureCandidates, usedCandidateIds);
        TryAddCollectionFeatureCandidates(queryText, featureCandidates, usedCandidateIds);
        TryAddNearbyObjectCandidates(targetText, queryText, kindHint, objectCandidates, usedCandidateIds);

        if (featureCandidates.Count == 0 && objectCandidates.Count == 0)
        {
            StepTargetBinding fallbackBinding = ResolveStructuredStepTargetBinding(queryText);
            if (fallbackBinding.HasTarget)
            {
                AddBindingAsFallbackCandidate(fallbackBinding, objectCandidates, usedCandidateIds);
            }
        }

        return new TargetCandidateBundle
        {
            queryText = queryText,
            targetText = targetText,
            candidateType = featureCandidates.Count > 0 ? "FeatureCandidateCells" : (objectCandidates.Count > 0 ? "ObjectCandidates" : "None"),
            featureCandidates = featureCandidates.ToArray(),
            objectCandidates = objectCandidates.ToArray(),
            notes = "系统仅提供离散候选，由 LLM 选择最终格与中间格"
        };
    }

    private void TryAddExplicitCoordinateCandidate(string raw, List<ObjectTargetCandidate> objectCandidates, HashSet<string> usedCandidateIds)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (TryResolveGridTargetWorld(raw, out Vector3 gridWorld, out Vector2Int gridCell))
        {
            string candidateId = $"grid:{gridCell.x}_{gridCell.y}";
            if (usedCandidateIds.Add(candidateId))
            {
                objectCandidates.Add(new ObjectTargetCandidate
                {
                    candidateId = candidateId,
                    displayName = $"grid({gridCell.x},{gridCell.y})",
                    sourceKind = "Grid",
                    worldPosition = gridWorld,
                    nearestCell = new GridCellCandidate { x = gridCell.x, z = gridCell.y },
                    notes = "显式网格目标"
                });
            }
            return;
        }

        if (TryParseWorldTarget(raw, out Vector3 worldPos) && TryResolveWalkableCell(worldPos, out Vector2Int worldCell))
        {
            string candidateId = $"world:{worldCell.x}_{worldCell.y}";
            if (usedCandidateIds.Add(candidateId))
            {
                objectCandidates.Add(new ObjectTargetCandidate
                {
                    candidateId = candidateId,
                    displayName = $"world({worldPos.x:F1},{worldPos.z:F1})",
                    sourceKind = "World",
                    worldPosition = worldPos,
                    nearestCell = new GridCellCandidate { x = worldCell.x, z = worldCell.y },
                    notes = "显式世界坐标目标"
                });
            }
        }
    }

    private void TryAddFeatureCandidatesForQuery(string query, List<FeatureTargetCandidate> featureCandidates, HashSet<string> usedCandidateIds)
    {
        if (campusGrid == null || string.IsNullOrWhiteSpace(query)) return;

        if (!campusGrid.TryResolveFeatureSpatialProfile(query, transform.position, out CampusGrid2D.FeatureSpatialProfile profile, preferWalkableApproach: true, ignoreCase: true) ||
            profile == null)
        {
            return;
        }

        string profileQuery = !string.IsNullOrWhiteSpace(profile.uid) ? profile.uid : query.Trim();
        if (!campusGrid.TryGetFeatureBoundaryCells(profileQuery, out Vector2Int[] boundaryCells) || boundaryCells == null || boundaryCells.Length == 0)
        {
            return;
        }

        string candidateId = $"feature:{(!string.IsNullOrWhiteSpace(profile.uid) ? profile.uid : profile.name)}";
        if (!usedCandidateIds.Add(candidateId)) return;

        featureCandidates.Add(new FeatureTargetCandidate
        {
            candidateId = candidateId,
            displayName = !string.IsNullOrWhiteSpace(profile.name) ? profile.name : (!string.IsNullOrWhiteSpace(profile.runtimeAlias) ? profile.runtimeAlias : query.Trim()),
            sourceKind = "Feature",
            candidateCells = BuildOrderedGridCellCandidates(boundaryCells, profile.centroidGrid),
            notes = $"feature_kind={profile.kind}"
        });
    }

    private void TryAddCollectionFeatureCandidates(string query, List<FeatureTargetCandidate> featureCandidates, HashSet<string> usedCandidateIds)
    {
        if (campusGrid == null || string.IsNullOrWhiteSpace(query)) return;
        if (!campusGrid.TryResolveFeatureCollectionBySelector(query, out string collectionKey, out CampusGrid2D.FeatureSpatialProfile[] profiles) ||
            profiles == null ||
            profiles.Length == 0)
        {
            return;
        }

        List<CampusGrid2D.FeatureSpatialProfile> orderedProfiles = profiles
            .Where(p => p != null)
            .OrderBy(p => Vector3.Distance(transform.position, p.anchorWorld))
            .Take(Mathf.Max(1, maxCampusFeaturesInPrompt))
            .ToList();
        for (int i = 0; i < orderedProfiles.Count; i++)
        {
            CampusGrid2D.FeatureSpatialProfile profile = orderedProfiles[i];
            string profileQuery = !string.IsNullOrWhiteSpace(profile.uid) ? profile.uid : profile.name;
            if (string.IsNullOrWhiteSpace(profileQuery)) continue;
            if (!campusGrid.TryGetFeatureBoundaryCells(profileQuery, out Vector2Int[] boundaryCells) || boundaryCells == null || boundaryCells.Length == 0) continue;

            string candidateId = $"feature:{profileQuery}";
            if (!usedCandidateIds.Add(candidateId)) continue;

            featureCandidates.Add(new FeatureTargetCandidate
            {
                candidateId = candidateId,
                displayName = !string.IsNullOrWhiteSpace(profile.name) ? profile.name : profileQuery,
                sourceKind = "CollectionMember",
                candidateCells = BuildOrderedGridCellCandidates(boundaryCells, profile.centroidGrid),
                notes = $"collection={collectionKey}"
            });
        }
    }

    private void TryAddNearbyObjectCandidates(
        string targetText,
        string queryText,
        string targetKindHint,
        List<ObjectTargetCandidate> objectCandidates,
        HashSet<string> usedCandidateIds)
    {
        string combined = $"{targetText} {queryText}".Trim();
        bool shouldAddAgents =
            string.Equals(targetKindHint, StructuredTargetMode.Agent.ToString(), System.StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("队友") ||
            combined.Contains("跟随") ||
            combined.Contains("agent") ||
            combined.Contains("teammate");
        bool shouldAddDetectedObjects =
            string.Equals(targetKindHint, StructuredTargetMode.DynamicSelector.ToString(), System.StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("最近") ||
            combined.Contains("附近") ||
            combined.Contains("热源") ||
            combined.Contains("车辆") ||
            combined.Contains("行人") ||
            combined.Contains("vehicle") ||
            combined.Contains("pedestrian") ||
            combined.Contains("resource");

        if (shouldAddDetectedObjects && agentState?.DetectedSmallNodes != null)
        {
            List<SmallNodeData> nearbyNodes = agentState.DetectedSmallNodes
                .Where(n => n != null)
                .OrderBy(n => Vector3.Distance(transform.position, n.WorldPosition))
                .Take(Mathf.Max(1, maxObjectCandidatesInPrompt))
                .ToList();
            for (int i = 0; i < nearbyNodes.Count; i++)
            {
                SmallNodeData node = nearbyNodes[i];
                string nodeId = !string.IsNullOrWhiteSpace(node.NodeId) ? node.NodeId : (!string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : $"node_{i + 1}");
                if (!TryResolveWalkableCell(node.WorldPosition, out Vector2Int cell))
                {
                    cell = campusGrid != null ? campusGrid.WorldToGrid(node.WorldPosition) : new Vector2Int(-1, -1);
                }
                string candidateId = $"node:{nodeId}";
                if (!usedCandidateIds.Add(candidateId)) continue;
                objectCandidates.Add(new ObjectTargetCandidate
                {
                    candidateId = candidateId,
                    displayName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : nodeId,
                    sourceKind = "SmallNode",
                    worldPosition = node.WorldPosition,
                    nearestCell = new GridCellCandidate { x = cell.x, z = cell.y },
                    notes = $"nodeType={node.NodeType},dynamic={(node.IsDynamic ? 1 : 0)},blocking={(node.BlocksMovement ? 1 : 0)}"
                });
            }
        }

        if (shouldAddAgents && agentState?.NearbyAgents != null)
        {
            IEnumerable<KeyValuePair<string, GameObject>> agents = agentState.NearbyAgents
                .Where(kv => kv.Value != null)
                .OrderBy(kv => Vector3.Distance(transform.position, kv.Value.transform.position))
                .Take(Mathf.Max(1, maxObjectCandidatesInPrompt));
            foreach (KeyValuePair<string, GameObject> kv in agents)
            {
                GameObject go = kv.Value;
                IntelligentAgent ia = go != null ? go.GetComponent<IntelligentAgent>() : null;
                string agentId = ia != null && ia.Properties != null && !string.IsNullOrWhiteSpace(ia.Properties.AgentID)
                    ? ia.Properties.AgentID
                    : kv.Key;
                if (!TryResolveWalkableCell(go.transform.position, out Vector2Int cell))
                {
                    cell = campusGrid != null ? campusGrid.WorldToGrid(go.transform.position) : new Vector2Int(-1, -1);
                }

                string candidateId = $"agent:{agentId}";
                if (!usedCandidateIds.Add(candidateId)) continue;
                objectCandidates.Add(new ObjectTargetCandidate
                {
                    candidateId = candidateId,
                    displayName = agentId,
                    sourceKind = "Agent",
                    worldPosition = go.transform.position,
                    nearestCell = new GridCellCandidate { x = cell.x, z = cell.y },
                    notes = ia != null && ia.Properties != null ? $"role={ia.Properties.Role}" : "nearby_agent"
                });
            }
        }
    }

    private void AddBindingAsFallbackCandidate(StepTargetBinding binding, List<ObjectTargetCandidate> objectCandidates, HashSet<string> usedCandidateIds)
    {
        Vector2Int cell = binding.GridCell;
        if ((cell.x < 0 || cell.y < 0) && TryResolveWalkableCell(binding.WorldPos, out Vector2Int resolved))
        {
            cell = resolved;
        }

        string candidateId = $"fallback:{(!string.IsNullOrWhiteSpace(binding.Uid) ? binding.Uid : binding.Name)}";
        if (!usedCandidateIds.Add(candidateId)) return;

        objectCandidates.Add(new ObjectTargetCandidate
        {
            candidateId = candidateId,
            displayName = !string.IsNullOrWhiteSpace(binding.Name) ? binding.Name : binding.RawQuery,
            sourceKind = binding.TargetKind,
            worldPosition = binding.WorldPos,
            nearestCell = new GridCellCandidate { x = cell.x, z = cell.y },
            notes = $"fallback_binding={binding.Source}"
        });
    }

    private GridCellCandidate[] BuildOrderedGridCellCandidates(Vector2Int[] cells, Vector2 centroidGrid)
    {
        if (cells == null || cells.Length == 0) return System.Array.Empty<GridCellCandidate>();
        List<Vector2Int> ordered = cells
            .Distinct()
            .OrderBy(cell => Mathf.Atan2(cell.y - centroidGrid.y, cell.x - centroidGrid.x))
            .ToList();
        if (ordered.Count > maxFeatureBoundaryCellsInPrompt)
        {
            ordered = DownsampleGridCells(ordered, maxFeatureBoundaryCellsInPrompt);
        }

        return ordered.Select(cell => new GridCellCandidate { x = cell.x, z = cell.y }).ToArray();
    }

    private static List<Vector2Int> DownsampleGridCells(List<Vector2Int> cells, int targetCount)
    {
        if (cells == null || cells.Count <= targetCount) return cells ?? new List<Vector2Int>();
        List<Vector2Int> result = new List<Vector2Int>(targetCount);
        float stride = (cells.Count - 1f) / Mathf.Max(1, targetCount - 1);
        for (int i = 0; i < targetCount; i++)
        {
            int index = Mathf.Clamp(Mathf.RoundToInt(i * stride), 0, cells.Count - 1);
            result.Add(cells[index]);
        }
        return result.Distinct().ToList();
    }

    private string BuildTargetSelectionPrompt(
        PlanStepDefinition planStep,
        TargetCandidateBundle candidateBundle,
        StepNavigationDecision navDecision,
        TeamCoordinationDirective[] coordinationDirectives)
    {
        string planStepJson = JsonConvert.SerializeObject(planStep, Formatting.None);
        string candidatesJson = SerializeTargetCandidateBundleForPrompt(candidateBundle);
        return $@"你是{agentProperties?.Type}智能体，当前进入第一次目标选择。
        你只能在系统给出的离散候选里选目标，不要输出动作。

        [当前步骤]
        {planStepJson}

        [任务]
        {GetTaskSummary()}

        [状态]
        {BuildDecisionContext()}

        [协同]
        {BuildCoordinationPromptSummary(coordinationDirectives)}

        [导航]
        {BuildNavigationPromptSummary(navDecision)}

        [离散候选]
        {candidatesJson}

        规则:
        1) selectedCandidateId 只能从候选列表里选。
        2) goalCell 只能选自该候选的 candidateCells 或 nearestCell。
        3) intermediateCells 只能选自同一候选的合法格；不需要时返回 []。
        4) 需要环绕、绕行、从某侧接近时，用 intermediateCells 表达。
        5) 不要输出动作，不要输出解释文字，不要新造格子。

        只输出一个 JSON 对象:
        {{
          ""selectedCandidateId"": ""候选ID"",
          ""goalCell"": {{ ""x"": 0, ""z"": 0 }},
          ""intermediateCells"": [{{ ""x"": 0, ""z"": 0 }}],
          ""reason"": ""<=20字""
        }}";
    }

    /// <summary>
    /// 把候选包转换成提示词专用 JSON。
    /// 这里不能直接序列化原始对象，因为 ObjectTargetCandidate.worldPosition 是 UnityEngine.Vector3，
    /// Newtonsoft 会沿着 normalized / magnitude 等属性继续展开，最终触发自引用循环异常。
    /// </summary>
    private string SerializeTargetCandidateBundleForPrompt(TargetCandidateBundle candidateBundle)
    {
        if (candidateBundle == null)
        {
            return "{}";
        }

        var promptDto = new
        {
            queryText = candidateBundle.queryText ?? string.Empty,
            targetText = candidateBundle.targetText ?? string.Empty,
            candidateType = candidateBundle.candidateType ?? "None",
            featureCandidates = candidateBundle.featureCandidates != null
                ? candidateBundle.featureCandidates
                    .Where(c => c != null)
                    .Select(c => new
                    {
                        candidateId = c.candidateId ?? string.Empty,
                        displayName = c.displayName ?? string.Empty,
                        sourceKind = c.sourceKind ?? string.Empty,
                        candidateCells = SerializeGridCellCandidatesForPrompt(c.candidateCells),
                        notes = c.notes ?? string.Empty
                    })
                    .ToArray()
                : Array.Empty<object>(),
            objectCandidates = candidateBundle.objectCandidates != null
                ? candidateBundle.objectCandidates
                    .Where(c => c != null)
                    .Select(c => new
                    {
                        candidateId = c.candidateId ?? string.Empty,
                        displayName = c.displayName ?? string.Empty,
                        sourceKind = c.sourceKind ?? string.Empty,
                        worldPosition = new
                        {
                            x = c.worldPosition.x,
                            y = c.worldPosition.y,
                            z = c.worldPosition.z
                        },
                        nearestCell = c.nearestCell != null
                            ? new { x = c.nearestCell.x, z = c.nearestCell.z }
                            : null,
                        notes = c.notes ?? string.Empty
                    })
                    .ToArray()
                : Array.Empty<object>(),
            notes = candidateBundle.notes ?? string.Empty
        };

        return JsonConvert.SerializeObject(promptDto, Formatting.None);
    }

    /// <summary>
    /// 把格子候选压成提示词安全的匿名对象数组。
    /// </summary>
    private object[] SerializeGridCellCandidatesForPrompt(GridCellCandidate[] cells)
    {
        if (cells == null || cells.Length == 0)
        {
            return Array.Empty<object>();
        }

        return cells
            .Where(cell => cell != null)
            .Select(cell => (object)new { x = cell.x, z = cell.z })
            .ToArray();
    }

    private bool TryParseTargetSelectionResponse(string response, out TargetSelectionResponse selection, out string error)
    {
        selection = null;
        error = string.Empty;
        string json = ExtractPureJson(response);
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "目标选择响应为空";
            return false;
        }

        try
        {
            selection = JsonConvert.DeserializeObject<TargetSelectionResponse>(json);
            if (selection == null)
            {
                error = "反序列化结果为空";
                return false;
            }

            selection.selectedCandidateId = string.IsNullOrWhiteSpace(selection.selectedCandidateId)
                ? string.Empty
                : selection.selectedCandidateId.Trim();
            selection.intermediateCells = selection.intermediateCells ?? System.Array.Empty<GridCellCandidate>();
            return true;
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private DiscreteTargetSelectionRuntime ValidateTargetSelection(TargetCandidateBundle bundle, TargetSelectionResponse selection)
    {
        DiscreteTargetSelectionRuntime result = new DiscreteTargetSelectionRuntime
        {
            IsValid = false,
            SelectedCandidateId = string.Empty,
            CandidateKind = "Unknown",
            DisplayName = string.Empty,
            TargetObject = null,
            WorldPos = transform.position,
            GoalCell = new Vector2Int(-1, -1),
            IntermediateCells = new List<Vector2Int>(),
            Summary = "none"
        };

        if (bundle == null) return result;

        FeatureTargetCandidate featureCandidate = null;
        ObjectTargetCandidate objectCandidate = null;
        if (!string.IsNullOrWhiteSpace(selection != null ? selection.selectedCandidateId : null))
        {
            featureCandidate = bundle.featureCandidates != null
                ? bundle.featureCandidates.FirstOrDefault(c => c != null && string.Equals(c.candidateId, selection.selectedCandidateId, System.StringComparison.OrdinalIgnoreCase))
                : null;
            if (featureCandidate == null)
            {
                objectCandidate = bundle.objectCandidates != null
                    ? bundle.objectCandidates.FirstOrDefault(c => c != null && string.Equals(c.candidateId, selection.selectedCandidateId, System.StringComparison.OrdinalIgnoreCase))
                    : null;
            }
        }

        if (featureCandidate == null && objectCandidate == null)
        {
            featureCandidate = bundle.featureCandidates != null && bundle.featureCandidates.Length > 0 ? bundle.featureCandidates[0] : null;
            objectCandidate = featureCandidate == null && bundle.objectCandidates != null && bundle.objectCandidates.Length > 0 ? bundle.objectCandidates[0] : null;
        }

        List<Vector2Int> legalCells = new List<Vector2Int>();
        if (featureCandidate != null && featureCandidate.candidateCells != null)
        {
            for (int i = 0; i < featureCandidate.candidateCells.Length; i++)
            {
                GridCellCandidate cell = featureCandidate.candidateCells[i];
                legalCells.Add(new Vector2Int(cell.x, cell.z));
            }
            result.SelectedCandidateId = featureCandidate.candidateId;
            result.CandidateKind = "FeatureCandidateCells";
            result.DisplayName = featureCandidate.displayName;
        }
        else if (objectCandidate != null && objectCandidate.nearestCell != null)
        {
            legalCells.Add(new Vector2Int(objectCandidate.nearestCell.x, objectCandidate.nearestCell.z));
            result.SelectedCandidateId = objectCandidate.candidateId;
            result.CandidateKind = objectCandidate.sourceKind;
            result.DisplayName = objectCandidate.displayName;
            result.TargetObject = TryResolveCandidateObject(objectCandidate);
        }

        if (legalCells.Count == 0) return result;

        Vector2Int requestedGoal = selection != null && selection.goalCell != null
            ? new Vector2Int(selection.goalCell.x, selection.goalCell.z)
            : legalCells[0];
        result.GoalCell = PickNearestLegalCell(requestedGoal, legalCells);
        result.WorldPos = campusGrid != null
            ? campusGrid.GridToWorldCenter(result.GoalCell.x, result.GoalCell.y, astarWaypointYOffset)
            : transform.position;

        if (featureCandidate != null && selection != null && selection.intermediateCells != null)
        {
            HashSet<Vector2Int> valid = new HashSet<Vector2Int>(legalCells);
            for (int i = 0; i < selection.intermediateCells.Length; i++)
            {
                GridCellCandidate cell = selection.intermediateCells[i];
                Vector2Int c = new Vector2Int(cell.x, cell.z);
                if (valid.Contains(c))
                {
                    result.IntermediateCells.Add(c);
                }
            }
        }

        result.IntermediateCells = RemoveDuplicateCellsPreserveOrder(result.IntermediateCells);
        result.IsValid = result.GoalCell.x >= 0 && result.GoalCell.y >= 0;
        result.Summary = $"candidate={result.SelectedCandidateId},kind={result.CandidateKind},goal=({result.GoalCell.x},{result.GoalCell.y}),via={string.Join(";", result.IntermediateCells.Select(c => $"({c.x},{c.y})"))}";
        return result;
    }

    private GameObject TryResolveCandidateObject(ObjectTargetCandidate candidate)
    {
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.candidateId)) return null;
        if (candidate.sourceKind == "Agent")
        {
            string agentId = candidate.candidateId.Replace("agent:", string.Empty);
            if (TryFindNearbyAgent(agentId, out GameObject teammate)) return teammate;
        }
        else if (candidate.sourceKind == "SmallNode")
        {
            string nodeId = candidate.candidateId.Replace("node:", string.Empty);
            if (TryResolveExactSmallNodeBinding(nodeId, out _, out GameObject obj, out _)) return obj;
        }
        return null;
    }

    private static List<Vector2Int> RemoveDuplicateCellsPreserveOrder(List<Vector2Int> cells)
    {
        List<Vector2Int> result = new List<Vector2Int>();
        if (cells == null) return result;
        HashSet<Vector2Int> seen = new HashSet<Vector2Int>();
        for (int i = 0; i < cells.Count; i++)
        {
            if (seen.Add(cells[i]))
            {
                result.Add(cells[i]);
            }
        }
        return result;
    }

    private Vector2Int PickNearestLegalCell(Vector2Int requested, List<Vector2Int> legalCells)
    {
        if (legalCells == null || legalCells.Count == 0) return new Vector2Int(-1, -1);
        if (legalCells.Contains(requested)) return requested;

        Vector2Int reference = requested;
        if (reference.x < 0 || reference.y < 0)
        {
            reference = TryResolveWalkableCell(transform.position, out Vector2Int selfCell) ? selfCell : legalCells[0];
        }

        Vector2Int best = legalCells[0];
        int bestScore = int.MaxValue;
        for (int i = 0; i < legalCells.Count; i++)
        {
            int score = (legalCells[i].x - reference.x) * (legalCells[i].x - reference.x) +
                        (legalCells[i].y - reference.y) * (legalCells[i].y - reference.y);
            if (score < bestScore)
            {
                bestScore = score;
                best = legalCells[i];
            }
        }
        return best;
    }

    private StepTargetBinding BuildStepTargetBindingFromDiscreteSelection(DiscreteTargetSelectionRuntime selection)
    {
        return new StepTargetBinding
        {
            HasTarget = selection.IsValid,
            TargetRef = "step_target",
            TargetKind = selection.CandidateKind == "FeatureCandidateCells" ? "Grid" : selection.CandidateKind,
            RawQuery = selection.DisplayName ?? string.Empty,
            Uid = selection.SelectedCandidateId ?? string.Empty,
            Name = selection.DisplayName ?? string.Empty,
            Source = "TwoStageDiscreteSelection",
            WorldPos = selection.WorldPos,
            GridCell = selection.GoalCell,
            SmallNodeType = SmallNodeType.Unknown,
            IsDynamicTarget = selection.CandidateKind == "Agent" || selection.CandidateKind == "SmallNode",
            BlocksMovement = false,
            FeatureCenterWorld = selection.WorldPos,
            FeatureCenterCell = selection.GoalCell,
            FeatureArrivalRadius = featureArrivalMinRadius,
            FeatureOrbitRadius = featureArrivalMinRadius,
            FeatureOccupiedCellCount = 0,
            CollectionKey = string.Empty,
            Summary = selection.Summary ?? "none"
        };
    }

    private string BuildDiscreteActionDecisionPrompt(
        string currentStep,
        PlanStepDefinition planStep,
        TargetCandidateBundle candidateBundle,
        DiscreteTargetSelectionRuntime selection,
        StepNavigationDecision navDecision,
        RoutePolicyDefinition routePolicy,
        TeamCoordinationDirective[] coordinationDirectives)
    {
        string stepJson = JsonConvert.SerializeObject(planStep, Formatting.None);
        string selectionJson = JsonConvert.SerializeObject(new
        {
            selectedCandidateId = selection.SelectedCandidateId,
            candidateKind = selection.CandidateKind,
            displayName = selection.DisplayName,
            goalCell = new { x = selection.GoalCell.x, z = selection.GoalCell.y },
            intermediateCells = selection.IntermediateCells.Select(c => new { x = c.x, z = c.y }).ToArray()
        }, Formatting.None);
        return $@"你是{agentProperties?.Type}智能体，当前进入第二次动作决策。
        第一次目标选择已经完成，你只能基于已选目标决定动作。

        [当前步骤]
        {currentStep}

        [planStep]
        {stepJson}

        [已选目标]
        {selectionJson}

        [任务]
        {GetTaskSummary()}

        [状态]
        {BuildDecisionContext()}

        [导航]
        {BuildNavigationPromptSummary(navDecision)}

        [路径策略]
        {BuildRoutePolicySummary(routePolicy)}

        [协同]
        {BuildCoordinationPromptSummary(coordinationDirectives)}

        [附近地点]
        {GetPerceivedCampusFeatureSummary()}

        [环境小节点]
        {GetPerceptionSmallNodeSummary()}

        [附近队友]
        {GetNearbyAgentsSummary()}

        规则:
        1) 只输出 1-3 个动作。
        2) targetRef 只能写 goal_cell、selected_candidate、none。
        3) 需要沿中间格移动时，在 parameters 里写 useIntermediateCells:1。
        4) 不要输出新的 world 坐标、grid 坐标、地点名或对象名。
        5) 不要规划完整长路径，系统会负责合法性校验与 A* 补全。
        6) 不要输出 Follow 或 Orbit；跟随/环绕统一改写成离散 MoveTo + 可选中间格。

        只输出 JSON 数组:
        [
          {{
            ""actionType"": ""MoveTo|Scan|TransmitMessage|Hover|Wait|Align|LookAt|TakeOff|Land"",
            ""targetRef"": ""goal_cell|selected_candidate|none"",
            ""parameters"": ""key:value,key2:value2 或 JSON字符串"",
            ""reason"": ""<=20字""
          }}
        ]";
    }

    /// <summary>
    /// 当前动作决策链的总入口。
    /// 它把“当前 step 的结构化语义”一路编译成“真正可执行的 ActionCommand 序列”。
    /// </summary>
    /// <returns>
    /// 返回一个协程枚举器。
    /// 外层通常由 <see cref="IntelligentAgent.MakeDecisionCoroutine"/> 启动，
    /// 在里面串行执行 LLM 请求、路径展开和动作下发。
    /// </returns>
    public IEnumerator<object> DecideNextAction()
    {
        // 先同步一次 CampusGrid 引用，避免运行时场景里网格组件延后初始化导致空引用。
        SyncCampusGridReference();
        // 新一轮决策开始时，先清掉“上一轮动作序列里是否包含移动动作”的状态。
        // 这个标记只应该描述“当前这轮下发了什么”，不能把上轮结果带到下一轮。
        lastIssuedSequenceContainsMovement = false;
        // 动作层必须尊重 PlanningModule 的执行门控。
        // 即使上层调度器直接调用了 DecideNextAction，只要协调者还没放行，本轮也不能偷跑。
        if (planningModule == null || !planningModule.HasActiveMission())
        {
            yield break;
        }

        // 读取当前 step 的自然语言描述。
        string currentStep = GetCurrentStepDescription();
        // 如果当前没有活跃步骤，或者计划已经执行完，就直接结束本轮决策。
        if (currentStep == "无活跃任务" || currentStep == "任务已完成")
        {
            yield break;
        }

        bool handledByTwoStageLlm = false;
        yield return TryDecideNextActionWithDiscreteCandidates(currentStep, success =>
        {
            handledByTwoStageLlm = success;
        });

        if (handledByTwoStageLlm)
        {
            yield break;
        }

        Debug.LogWarning($"[ActionDecision] 两次LLM离散决策失败，执行最小 Hold 兜底: step={currentStep}");
        ExecuteTwoStageFailureFallback(currentStep);
        yield break;
    }

    /// <summary>
    /// 两次 LLM 主路径失败时的最小兜底。
    /// 当前只做原地保持，不再回退旧 deterministic/string-grounding 大链。
    /// </summary>
    private void ExecuteTwoStageFailureFallback(string currentStep)
    {
        PrimitiveActionType fallbackAction =
            agentProperties != null && agentProperties.Type == AgentType.Quadcopter
                ? PrimitiveActionType.Hover
                : PrimitiveActionType.Stop;

        ActionCommand holdCommand = new ActionCommand
        {
            ActionType = fallbackAction,
            TargetPosition = transform.position,
            TargetRotation = transform.rotation,
            TargetObject = null,
            Parameters = BuildJsonFromParameterMap(new Dictionary<string, string>
            {
                ["fallback"] = "two_stage_llm_failed",
                ["step"] = currentStep ?? string.Empty
            })
        };

        lastIssuedSequenceContainsMovement = false;
        if (agentState != null)
        {
            agentState.Status = AgentStatus.ExecutingTask;
        }
        StartCoroutine(ExecuteActionSequence(new List<ActionCommand> { holdCommand }, currentStep));
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
        string selfSummary = $"id={agentProperties.AgentID},team={agentState.TeamID},status={agentState.Status},bat={agentState.BatteryLevel:F1}/{agentProperties.BatteryCapacity:F1},pos=({p.x:F1},{p.z:F1}),v={agentState.Velocity.magnitude:F1},load={agentState.CurrentLoad:F1}/{agentProperties.PayloadCapacity:F1}";
        string teamSummary = planningModule != null ? planningModule.BuildTeamExecutionStateSummary() : "teamState=none";
        return $"{selfSummary}, {teamSummary}";
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
    /// <param name="inputActions">第一阶段决策或确定性编译后得到的原始动作序列，其中 MoveTo 可能还是“长距离一步到位”。</param>
    /// <param name="navDecision">当前步骤的导航判定结果，决定这一步是否允许/偏好 A* 展开。</param>
    /// <returns>展开后的动作序列；如果没有任何 MoveTo 被展开，则直接返回原列表，避免不必要的对象替换。</returns>
    private List<ActionCommand> ExpandMoveActionsByAStar(List<ActionCommand> inputActions, StepNavigationDecision navDecision)
    {
        // 空输入直接返回，避免后续访问空列表。
        if (inputActions == null || inputActions.Count == 0) return inputActions;

        // expanded 收集“已经展开后的最终动作序列”。
        List<ActionCommand> expanded = new List<ActionCommand>(inputActions.Count);
        // 记录这一轮是否真的发生过展开；如果完全没展开，直接返回原对象更稳妥。
        bool expandedAny = false;
        // virtualStart 表示“逻辑上的下一段起点”，而不是 rigidbody 的实时位置。
        Vector3 virtualStart = transform.position;

        // 顺序处理每个动作，确保多段 MoveTo 的前后衔接一致。
        for (int i = 0; i < inputActions.Count; i++)
        {
            // 取出当前待处理动作。
            ActionCommand action = inputActions[i];
            // 空动作直接跳过，避免污染输出。
            if (action == null) continue;

            // 非 MoveTo 动作不需要路径展开，原样透传。
            if (action.ActionType != PrimitiveActionType.MoveTo)
            {
                expanded.Add(action);
                continue;
            }

            // 第一优先级：系统粗路径。
            // 这类路径来自结构化 viaTargets/target 计算结果，必须优先落到执行层。
            if (TryBuildSystemWaypointsFromParameters(action, out List<Vector3> systemWaypoints))
            {
                // 把系统算好的粗路径切成连续 MoveTo。
                AppendWaypointsAsMoveActions(action, systemWaypoints, expanded, "SystemCoarsePath");
                // 逻辑起点推进到粗路径末端，供后续动作继续衔接。
                virtualStart = systemWaypoints[systemWaypoints.Count - 1];
                // 标记本轮确实发生过展开。
                expandedAny = true;
                continue;
            }

            // 第二优先级（可选）：LLM 显式给 waypoint。
            // 只有允许采用 LLM waypoint 且没有禁用长路径时，才接受这一路径来源。
            if (preferLlmWaypoints && !disableLlmLongWaypoints &&
                TryBuildLlmWaypointsFromParameters(action, out List<Vector3> llmWaypoints))
            {
                // 把 LLM 输出的局部路径变成连续 MoveTo。
                AppendWaypointsAsMoveActions(action, llmWaypoints, expanded, "LLMPath");
                // 推进逻辑起点到路径末端。
                virtualStart = llmWaypoints[llmWaypoints.Count - 1];
                // 标记展开成功。
                expandedAny = true;
                continue;
            }

            // 第三优先级：A* 兜底。
            // 如果当前配置不允许 A* 回退，保留原始动作即可。
            if (!allowAStarFallbackWhenNoLlmWaypoints || !enableAStarPathExpansion)
            {
                expanded.Add(action);
                // 即使没展开，也要把逻辑终点推进到该动作目标。
                virtualStart = action.TargetPosition;
                continue;
            }

            // 没有 CampusGrid2D 就无法做离散网格 A*，只能原样保留。
            if (campusGrid == null)
            {
                expanded.Add(action);
                virtualStart = action.TargetPosition;
                continue;
            }

            // 根据目标类型、pathMode、step 策略等判断当前 MoveTo 是否适合 A*。
            if (!ShouldUseAStarForMoveAction(action, virtualStart, navDecision))
            {
                expanded.Add(action);
                virtualStart = action.TargetPosition;
                continue;
            }

            // 从 virtualStart 到动作目标构建一条 A* waypoint 列表。
            if (TryBuildAStarWaypoints(virtualStart, action.TargetPosition, out List<Vector3> astarWaypoints))
            {
                // 只取短前缀，避免一次性把整条长路线全部下发。
                List<Vector3> segment = BuildReactiveWaypointSegment(astarWaypoints);
                // 把这个短段展开成多个小 MoveTo。
                AppendWaypointsAsMoveActions(action, segment, expanded, "AStarReactive");
                // 下一动作的逻辑起点推进到本段最后一个 waypoint。
                virtualStart = segment[segment.Count - 1];
                // 标记展开成功。
                expandedAny = true;
            }
            else
            {
                // A* 失败时回退原始 MoveTo，保证行为不断链。
                expanded.Add(action);
                virtualStart = action.TargetPosition;
            }
        }

        // 只有真的展开过时才返回新列表，同时打印一条调试日志方便核对动作膨胀情况。
        if (expandedAny)
        {
            // 日志里优先输出显式 AgentID，没有则退回 GameObject 名称。
            string agentId = agentProperties != null ? agentProperties.AgentID : gameObject.name;
            Debug.Log($"[ActionDecision][PathExpand][{agentId}] 路径点展开完成: 原动作={inputActions.Count}, 展开后={expanded.Count}");
            return expanded;
        }
        // 如果一条都没展开，保留原列表，避免上层误以为动作序列被重写。
        return inputActions;
    }

    /// <summary>
    /// A* 全路径只取短前缀，执行后再进入下一轮 DecideNextAction 进行滚动重规划。
    /// </summary>
    /// <param name="fullPath">TryBuildAStarWaypoints 构建出的完整离散路径 waypoint 列表。</param>
    /// <returns>只保留前若干个 waypoint 的短段路径，用于“走一小段再重规划”。</returns>
    private List<Vector3> BuildReactiveWaypointSegment(List<Vector3> fullPath)
    {
        // 没路径时返回空列表，调用方会自动回退。
        if (fullPath == null || fullPath.Count == 0) return new List<Vector3>();
        // 根据当前目标类型、障碍态势等决定这一轮只取多少个 waypoint。
        int n = ResolveReactiveSegmentWaypointCount(fullPath.Count);
        // 取路径前缀，形成“滚动执行段”。
        return fullPath.Take(n).ToList();
    }

    /// <summary>
    /// 决定一次 A* 反应式执行段包含多少个 waypoint。
    /// </summary>
    /// <param name="fullPathCount">完整路径包含的 waypoint 数量。</param>
    /// <returns>当前轮次应当执行的 waypoint 数量。</returns>
    private int ResolveReactiveSegmentWaypointCount(int fullPathCount)
    {
        // 非法长度直接返回 0，表示不可执行。
        if (fullPathCount <= 0) return 0;

        // 基准值来自配置项 reactiveAStarSegmentWaypoints，但会被完整路径长度钳制。
        int baseCount = Mathf.Clamp(reactiveAStarSegmentWaypoints, 1, fullPathCount);
        // 只有静态地点类目标（Feature/World/Grid）才有资格“走更长一点”；追踪动态目标则保守执行。
        if (lastStepTargetBinding.TargetKind != "Feature" &&
            lastStepTargetBinding.TargetKind != "World" &&
            lastStepTargetBinding.TargetKind != "Grid")
        {
            return baseCount;
        }

        // 周围存在阻塞或动态小节点时，不要放大执行段，保守一点更稳。
        if (HasNearbyBlockingOrDynamicSmallNodes(deterministicNavigationObstacleRadius))
        {
            return baseCount;
        }

        // 静态环境下允许一次多吃几个 waypoint，减少决策轮数。
        int fastCount = Mathf.Max(baseCount, fastStaticReactiveSegmentWaypoints);
        // 最终仍然钳制在合法范围内。
        return Mathf.Clamp(fastCount, 1, fullPathCount);
    }

    /// <summary>
    /// 从 LLM 的 parameters 中提取 waypoint 列表。
    /// 支持格式：
    /// 1) waypoints:10 20;14 26;18 32
    /// 2) waypoints:10 2 20;14 2 26
    /// 3) waypoints:world(10,20)|world(14,26)
    /// </summary>
    /// <param name="moveAction">原始 MoveTo 动作，主要提供 Parameters 和默认目标高度。</param>
    /// <param name="waypoints">输出解析后的世界坐标路径点列表。</param>
    /// <returns>成功解析出至少一个 waypoint 时返回 true。</returns>
    private bool TryBuildLlmWaypointsFromParameters(ActionCommand moveAction, out List<Vector3> waypoints)
    {
        // 默认先给空列表，保证 out 参数总是有效。
        waypoints = new List<Vector3>();
        // 空动作无法解析。
        if (moveAction == null) return false;

        // 优先走专门的 raw 文本提取逻辑，兼容引号包裹和包含逗号的写法。
        string raw = ExtractWaypointsRawValue(moveAction.Parameters);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 如果正则提取不到，再退回松散参数表解析。
            Dictionary<string, string> pmap = ParseLooseParameterMap(moveAction.Parameters);
            // 仍然没有 waypoints 字段则失败。
            if (!pmap.TryGetValue("waypoints", out raw) || string.IsNullOrWhiteSpace(raw)) return false;
        }

        // 去掉首尾空白和可能的成对引号。
        string s = raw.Trim().Trim('\"', '\'');
        // 支持分号或竖线分隔多个 waypoint。
        string[] tokens = s.Split(new[] { ';', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
        // 没有 token 就没有路径。
        if (tokens.Length == 0) return false;

        // 默认高度使用该 MoveTo 最终目标的接近高度，避免路径点高度缺失。
        float defaultY = ResolveWaypointY(moveAction.TargetPosition, true);
        // 逐个 token 解析 waypoint。
        for (int i = 0; i < tokens.Length; i++)
        {
            // 当前 token 去掉首尾空白。
            string t = tokens[i].Trim();
            // 空 token 跳过。
            if (string.IsNullOrWhiteSpace(t)) continue;

            // 优先支持 world(x,z) / world(x,y,z) 这种显式格式。
            if (TryParseWorldTarget(t, out Vector3 wp))
            {
                // 如果 world target 没给高度，则补上默认高度。
                if (Mathf.Abs(wp.y) < 0.001f) wp.y = defaultY;
                waypoints.Add(wp);
                continue;
            }

            // 回退支持“x z”或“x y z”的纯数字写法。
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                t,
                @"^\s*(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)(?:\s+(-?\d+(?:\.\d+)?))?\s*$"
            );
            // 不匹配数字格式则放弃当前 token。
            if (!m.Success) continue;

            // 第一位是 x。
            bool okX = float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
            // 第二位要么是 z，要么是 y。
            bool ok2 = float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float second);
            // 只要 x 或第二位失败，本 token 就无效。
            if (!okX || !ok2) continue;

            // 默认按“x z”两维格式解释。
            float y = defaultY;
            float z = second;
            // 如果第三位存在，则按“x y z”三维格式解释。
            if (m.Groups[3].Success &&
                float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float third))
            {
                y = second;
                z = third;
            }

            // 写入解析出的 waypoint。
            waypoints.Add(new Vector3(x, y, z));
        }

        // 一个都没解析出来则失败。
        if (waypoints.Count == 0) return false;

        // 保证最后落到 MoveTo 的目标点附近，避免“路径点到一半停住”。
        Vector3 finalTarget = moveAction.TargetPosition;
        if (Vector3.Distance(waypoints[waypoints.Count - 1], finalTarget) > 1.0f)
        {
            waypoints.Add(finalTarget);
        }

        // waypoint 数量过多时做降采样，避免一次下发过长序列。
        int maxCount = Mathf.Max(1, astarMaxWaypoints);
        if (waypoints.Count > maxCount)
        {
            waypoints = DownsampleWaypoints(waypoints, maxCount);
        }

        // 只要最终仍然保留至少一个点，就认为解析成功。
        return waypoints.Count > 0;
    }

    /// <summary>
    /// 解析系统生成的粗路径 waypoint。
    /// 这些 waypoint 来自系统基于结构化 target/viaTargets 计算出的粗路径，
    /// 不是 LLM 自由发挥的长路径，因此即使关闭了 LLM 长 waypoint，也必须允许执行。
    /// </summary>
    /// <param name="moveAction">待检查的 MoveTo 动作，其 parameters 里可能包含 systemWaypoints。</param>
    /// <param name="waypoints">输出系统生成的 waypoint 列表。</param>
    /// <returns>当 parameters 明确标识为系统/已校验离散路径段且成功解析出 waypoint 时返回 true。</returns>
    private bool TryBuildSystemWaypointsFromParameters(ActionCommand moveAction, out List<Vector3> waypoints)
    {
        // 默认先给空列表，保证 out 参数总是安全可用。
        waypoints = new List<Vector3>();
        // 空动作无从解析。
        if (moveAction == null) return false;

        // 解析原始参数映射，检查是否标记为系统粗路径片段。
        Dictionary<string, string> pmap = ParseLooseParameterMap(moveAction.Parameters);
        if (!pmap.TryGetValue("segmentSource", out string segmentSource) ||
            (!string.Equals(segmentSource, "SystemCoarsePath", System.StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(segmentSource, "LLMSelectedCells", System.StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // SystemCoarsePath 使用 systemWaypoints；
        // LLMSelectedCells 使用已经校验过的 waypoints。
        string serializedWaypoints = string.Empty;
        if (pmap.TryGetValue("systemWaypoints", out string systemWaypoints) && !string.IsNullOrWhiteSpace(systemWaypoints))
        {
            serializedWaypoints = systemWaypoints;
        }
        else if (pmap.TryGetValue("waypoints", out string inlineWaypoints) && !string.IsNullOrWhiteSpace(inlineWaypoints))
        {
            serializedWaypoints = inlineWaypoints;
        }

        // 两种字段都没有时，无法恢复离散路径。
        if (string.IsNullOrWhiteSpace(serializedWaypoints))
        {
            return false;
        }

        // 构造一个“影子动作”，复用 TryBuildLlmWaypointsFromParameters 的现有解析逻辑。
        ActionCommand shadow = new ActionCommand
        {
            // 动作类型保持一致，通常仍然是 MoveTo。
            ActionType = moveAction.ActionType,
            // 目标对象沿用原动作，避免丢失最终绑定。
            TargetObject = moveAction.TargetObject,
            // 目标位置沿用原动作，供默认高度和末端补点使用。
            TargetPosition = moveAction.TargetPosition,
            // 朝向也沿用原动作。
            TargetRotation = moveAction.TargetRotation,
            // 把 systemWaypoints 伪装成标准的 waypoints 字段，以复用通用解析器。
            Parameters = $"waypoints:{serializedWaypoints}"
        };

        // 直接复用 LLM waypoint 解析逻辑得到最终路径点。
        return TryBuildLlmWaypointsFromParameters(shadow, out waypoints);
    }

    /// <summary>
    /// 当 LLM 未输出 waypoint 且已有粗路径时，把粗路径注入到第一条 MoveTo。
    /// 作用：保证“先A*粗路径，再局部细化”的链路在异常输出下仍可执行。
    /// </summary>
    /// <param name="actions">待执行动作序列，通常来自确定性规划或第一阶段高层动作解析。</param>
    /// <param name="coarsePath">系统提前算好的粗路径上下文，包含目标点和内联 waypoint 文本。</param>
    /// <returns>必要时把粗路径注入首个 MoveTo 的参数后返回同一列表。</returns>
    private List<ActionCommand> ApplyCoarsePathFallbackIfNeeded(List<ActionCommand> actions, CoarsePathContext coarsePath)
    {
        // 空动作序列无需处理。
        if (actions == null || actions.Count == 0) return actions;
        // 没有有效粗路径时也不做注入。
        if (!coarsePath.HasPath || coarsePath.CoarseWaypoints == null || coarsePath.CoarseWaypoints.Count == 0) return actions;

        // 只需要处理第一条可导航的 MoveTo，把粗路径挂进去即可。
        for (int i = 0; i < actions.Count; i++)
        {
            // 当前待检查动作。
            ActionCommand a = actions[i];
            // 非 MoveTo 动作跳过。
            if (a == null || a.ActionType != PrimitiveActionType.MoveTo) continue;

            // 先解析原有参数，看看是否已经带路径信息。
            Dictionary<string, string> existingMap = ParseLooseParameterMap(a.Parameters);
            // viaTargets 不为空，说明当前动作已经承载结构化路径要求。
            bool hasViaTargets = existingMap.TryGetValue("viaTargets", out string viaTargets) && !string.IsNullOrWhiteSpace(viaTargets);
            // Feature 目标通常不应直接改成完全直线移动，因此需要尊重结构化路线。
            bool isFeatureTarget = existingMap.TryGetValue("targetKind", out string targetKind) &&
                                   string.Equals(targetKind, "Feature", System.StringComparison.OrdinalIgnoreCase);
            // 只要存在 viaTargets、Feature 目标，或粗路径本身不止两点，就认为必须尊重结构化路线。
            bool mustRespectStructuredRoute = hasViaTargets || isFeatureTarget || coarsePath.CoarseWaypoints.Count > 2;

            // 已经明确是 LLM 局部反应段时，不要再注入系统粗路径。
            if (existingMap.TryGetValue("pathMode", out string existingMode) &&
                string.Equals(existingMode, "LLMReactiveSegment", System.StringComparison.OrdinalIgnoreCase))
            {
                return actions;
            }
            // 已经带 segmentSource 的动作同样不应重复覆盖。
            if (existingMap.TryGetValue("segmentSource", out string segmentSource) &&
                (string.Equals(segmentSource, "LLMReactiveSegment", System.StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(segmentSource, "SystemCoarsePath", System.StringComparison.OrdinalIgnoreCase)))
            {
                return actions;
            }

            // 允许长 waypoint 且动作本身已经带 waypoint 时，尊重现有输出。
            if (!disableLlmLongWaypoints &&
                TryBuildLlmWaypointsFromParameters(a, out List<Vector3> existing) && existing.Count > 0)
            {
                return actions;
            }

            // 如果这条动作显式要求 Direct 且当前又没有必要强行维护结构化路线，就不做注入。
            if (!mustRespectStructuredRoute &&
                existingMap.TryGetValue("pathMode", out existingMode) &&
                string.Equals(existingMode, "Direct", System.StringComparison.OrdinalIgnoreCase))
            {
                return actions;
            }

            // 构造需要注入的参数，提示后续路径展开阶段优先采用系统粗路径。
            Dictionary<string, string> inject = new Dictionary<string, string>
            {
                // 标记这条 MoveTo 至少应该被视为 A* 提示路径。
                ["pathMode"] = "AStarHint",
                // 记录粗路径对应的目标 feature 名称，便于日志与调试。
                ["targetFeature"] = coarsePath.TargetFeatureName,
                // 显式标记路径来源，后续 ExpandMoveActionsByAStar 会优先读取。
                ["segmentSource"] = "SystemCoarsePath",
                // 把内联 waypoint 文本挂进去，供解析器恢复完整点列。
                ["systemWaypoints"] = coarsePath.WaypointsInline
            };

            // 合并到动作参数里。
            a.Parameters = MergeParameters(a.Parameters, inject);
            // 如果原动作目标位置仍然是当前位置附近，说明目标点还没真正写进去，这里补成粗路径终点。
            if (a.TargetPosition == transform.position || Vector3.Distance(a.TargetPosition, transform.position) < 0.5f)
            {
                a.TargetPosition = coarsePath.GoalWorld;
            }
            // 只处理第一条合适的 MoveTo 即可。
            return actions;
        }

        // 没找到可注入的 MoveTo 时原样返回。
        return actions;
    }

    /// <summary>
    /// 把当前 step 的 RoutePolicy 附着到动作序列中的每个 MoveTo。
    /// </summary>
    /// <param name="actions">待执行动作序列；只有 MoveTo 会被写入路径策略参数。</param>
    /// <param name="currentStep">当前自然语言步骤文本，用于解析 pending viaTargets。</param>
    /// <param name="stepIntent">当前步骤的结构化意图，可能包含 orderedViaTargets。</param>
    /// <param name="routePolicy">当前步骤的结构化路径策略。</param>
    /// <returns>参数被原地合并后的同一动作列表。</returns>
    private List<ActionCommand> ApplyRoutePolicyToActionSequence(List<ActionCommand> actions, string currentStep, StepIntentDefinition stepIntent, RoutePolicyDefinition routePolicy)
    {
        // 没动作或没路径策略时，无需注入任何参数。
        if (actions == null || actions.Count == 0 || routePolicy == null) return actions;

        // 只保留“尚未满足”的 viaTargets，避免已经经过的锚点继续污染后续 MoveTo。
        string[] pendingViaTargets = ResolvePendingViaTargetsForCurrentStep(currentStep, stepIntent, refreshProgress: false);
        // 序列化为 `a|b|c` 形式，方便后续 ParseLooseParameterMap / MergeParameters 透传。
        string viaTargets = pendingViaTargets.Length > 0
            ? string.Join("|", pendingViaTargets)
            : string.Empty;
        // 把需要规避的小节点类型压成 `Pedestrian|Vehicle` 这种字符串。
        string avoidNodeTypes = routePolicy.avoidNodeTypes != null && routePolicy.avoidNodeTypes.Length > 0
            ? string.Join("|", routePolicy.avoidNodeTypes.Select(t => t.ToString()).ToArray())
            : string.Empty;
        // 把需要避开的 feature 名称同样压成管道分隔文本。
        string avoidFeatures = routePolicy.avoidFeatureNames != null && routePolicy.avoidFeatureNames.Length > 0
            ? string.Join("|", routePolicy.avoidFeatureNames)
            : string.Empty;

        // 遍历动作，把策略参数合并进每条 MoveTo。
        for (int i = 0; i < actions.Count; i++)
        {
            // 当前动作。
            ActionCommand action = actions[i];
            // 非 MoveTo 不处理。
            if (action == null || action.ActionType != PrimitiveActionType.MoveTo) continue;

            // 这些参数都是执行层和路径展开层会读取的关键策略字段。
            Dictionary<string, string> policyMap = new Dictionary<string, string>
            {
                // 高度策略：Low / Medium / HighThenDescend 等。
                ["altitudeMode"] = routePolicy.altitudeMode.ToString(),
                // 净空要求：供后续规避或调试使用。
                ["clearance"] = routePolicy.clearance.ToString(),
                // 是否允许局部绕行。
                ["allowLocalDetour"] = routePolicy.allowLocalDetour ? "1" : "0",
                // 遇阻时策略：等待 / 绕行 / 重规划等。
                ["blockedPolicy"] = routePolicy.blockedPolicy.ToString()
            };

            // 仅在存在时写入剩余 viaTargets。
            if (!string.IsNullOrWhiteSpace(viaTargets)) policyMap["viaTargets"] = viaTargets;
            // 仅在存在时写入需要规避的小节点类型。
            if (!string.IsNullOrWhiteSpace(avoidNodeTypes)) policyMap["avoidNodeTypes"] = avoidNodeTypes;
            // 仅在存在时写入需要规避的 feature 名称。
            if (!string.IsNullOrWhiteSpace(avoidFeatures)) policyMap["avoidFeatureNames"] = avoidFeatures;

            // 把策略与原始参数合并，保持其他字段不丢。
            action.Parameters = MergeParameters(action.Parameters, policyMap);
        }

        // 返回原地更新后的动作列表。
        return actions;
    }

    /// <summary>
    /// 把 PlanningModule 的结构化协同约束并入动作参数。
    /// 这样做的目的不是让系统重新解释协同语义，而是把上游已经定好的约束透明地下传给执行层/日志层。
    /// </summary>
    /// <param name="actions">待执行动作序列，所有动作都可以携带协同上下文。</param>
    /// <param name="directives">PlanningModule 给出的结构化协同指令数组。</param>
    /// <returns>写入协同上下文参数后的动作列表。</returns>
    private List<ActionCommand> ApplyCoordinationDirectivesToActionSequence(List<ActionCommand> actions, TeamCoordinationDirective[] directives)
    {
        // 没动作或没协同约束时，无需处理。
        if (actions == null || actions.Count == 0 || directives == null || directives.Length == 0) return actions;

        // 汇总所有协同模式，例如 FollowLeader / Yield / Synchronize。
        string modes = string.Join("|", directives.Where(d => d != null).Select(d => d.coordinationMode.ToString()).Distinct().ToArray());
        // 汇总 leaderAgentId，供执行层或日志识别“跟谁走”。
        string leaders = string.Join("|", directives.Where(d => d != null && !string.IsNullOrWhiteSpace(d.leaderAgentId)).Select(d => d.leaderAgentId).Distinct().ToArray());
        // 汇总共享目标，把结构化 targetRef 转成统一字符串。
        string sharedTargets = string.Join("|", directives
            .Where(d => d != null)
            .Select(d => ResolveStructuredTargetQuery(d.sharedTargetRef, d.sharedTarget))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToArray());
        // 汇总走廊保留键，便于多个体执行时识别通行资源。
        string corridors = string.Join("|", directives.Where(d => d != null && !string.IsNullOrWhiteSpace(d.corridorReservationKey)).Select(d => d.corridorReservationKey).Distinct().ToArray());
        // 汇总需要礼让的 agent 列表。
        string yields = string.Join("|", directives.Where(d => d != null && d.yieldToAgentIds != null).SelectMany(d => d.yieldToAgentIds).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray());
        // 汇总编队槽位。
        string formations = string.Join("|", directives.Where(d => d != null && !string.IsNullOrWhiteSpace(d.formationSlot)).Select(d => d.formationSlot).Distinct().ToArray());

        // 把这些协同字段并入每个动作，确保执行链路可见上游约束。
        for (int i = 0; i < actions.Count; i++)
        {
            // 当前动作。
            ActionCommand action = actions[i];
            // 空动作跳过。
            if (action == null) continue;

            // 逐项收集非空协同字段。
            Dictionary<string, string> coordinationMap = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(modes)) coordinationMap["coordinationModes"] = modes;
            if (!string.IsNullOrWhiteSpace(leaders)) coordinationMap["leaderAgentIds"] = leaders;
            if (!string.IsNullOrWhiteSpace(sharedTargets)) coordinationMap["sharedTargets"] = sharedTargets;
            if (!string.IsNullOrWhiteSpace(corridors)) coordinationMap["corridorReservationKeys"] = corridors;
            if (!string.IsNullOrWhiteSpace(yields)) coordinationMap["yieldToAgentIds"] = yields;
            if (!string.IsNullOrWhiteSpace(formations)) coordinationMap["formationSlots"] = formations;

            // 只有存在实际协同字段时才改写参数。
            if (coordinationMap.Count > 0)
            {
                action.Parameters = MergeParameters(action.Parameters, coordinationMap);
            }
        }

        // 返回原地更新后的动作列表。
        return actions;
    }

    /// <summary>
    /// 从原始参数文本中提取 waypoints 字段，优先支持带引号的整段文本。
    /// 这样可以兼容包含逗号的写法，例如：
    /// waypoints:"world(10,20);world(14,26)"
    /// </summary>
    /// <param name="rawParameters">原始 parameters 文本，可能是松散 key:value，也可能是 JSON 风格字符串。</param>
    /// <returns>提取到的 waypoint 原始文本；如果不存在则返回空字符串。</returns>
    private static string ExtractWaypointsRawValue(string rawParameters)
    {
        // 空文本直接返回空字符串。
        if (string.IsNullOrWhiteSpace(rawParameters)) return string.Empty;
        // 去掉首尾空白，便于正则统一匹配。
        string s = rawParameters.Trim();

        // 优先匹配带引号的整段值，避免内部逗号/分号被误判为其他参数。
        System.Text.RegularExpressions.Match quoted = System.Text.RegularExpressions.Regex.Match(
            s,
            @"waypoints\s*[:=]\s*[""'](?<v>[^""']+)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (quoted.Success)
        {
            return quoted.Groups["v"].Value.Trim();
        }

        // 再匹配不带引号的普通写法，直到下一个 key:value 或文本结束为止。
        System.Text.RegularExpressions.Match plain = System.Text.RegularExpressions.Regex.Match(
            s,
            @"waypoints\s*[:=]\s*(?<v>.+?)(?=,\s*[A-Za-z_][A-Za-z0-9_]*\s*[:=]|$|\})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (plain.Success)
        {
            return plain.Groups["v"].Value.Trim();
        }

        // 两种格式都没匹配到，视为不存在 waypoint。
        return string.Empty;
    }

    /// <summary>
    /// 判断某条 MoveTo 是否满足 A* 展开条件。
    /// </summary>
    /// <param name="moveAction">待评估的 MoveTo 动作。</param>
    /// <param name="startPos">当前逻辑路径起点，而非一定等于 transform.position。</param>
    /// <param name="navDecision">当前步骤的导航决策结果。</param>
    /// <returns>若应当把该 MoveTo 交给 CampusGrid2D A* 展开则返回 true。</returns>
    private bool ShouldUseAStarForMoveAction(ActionCommand moveAction, Vector3 startPos, StepNavigationDecision navDecision)
    {
        // 没动作或没网格系统时，根本不可能做 A*。
        if (moveAction == null) return false;
        if (campusGrid == null) return false;

        // 先读当前动作参数，A* 使用与否主要由这些结构化字段控制。
        Dictionary<string, string> pmap = ParseLooseParameterMap(moveAction.Parameters);
        // 显式 Direct 或已经是 LLMReactiveSegment 的动作，不再交给 A* 二次展开。
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

        // 跟随/追踪 Agent 目标时，A* 粗路径价值不大，直接交给局部控制更合理。
        if (pmap.TryGetValue("targetKind", out string targetKind) &&
            string.Equals(targetKind, "Agent", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // SmallNode 目标如果是动态对象，也不适合做静态 A*。
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

        // 其他普通目标需要满足最小距离阈值，太近就没必要做 A*。
        float planarDist = Vector2.Distance(
            new Vector2(startPos.x, startPos.z),
            new Vector2(moveAction.TargetPosition.x, moveAction.TargetPosition.z)
        );
        if (planarDist < astarMinTriggerDistance) return false;

        // 普通 MoveTo 是否启用 A* 由 step 导航策略给出“允许”开关，不强制。
        return navDecision.IsMovementStep && navDecision.AllowAStarByStep;
    }

    /// <summary>
    /// 判断参数映射描述的目标是否属于动态追踪对象。
    /// </summary>
    /// <param name="pmap">MoveTo 参数的松散映射表。</param>
    /// <returns>若目标是动态节点、追踪对象或显式标记为 dynamic，则返回 true。</returns>
    private static bool IsDynamicOrChasingTarget(Dictionary<string, string> pmap)
    {
        // 空映射默认不是动态目标。
        if (pmap == null || pmap.Count == 0) return false;

        // 显式 targetDynamic=1/true 时，直接视为动态对象。
        if (pmap.TryGetValue("targetDynamic", out string dynamicFlag) &&
            (dynamicFlag == "1" || string.Equals(dynamicFlag, "true", System.StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 某些 SmallNodeType 天然表示动态个体，也视为不适合静态 A*。
        if (pmap.TryGetValue("targetNodeType", out string nodeType))
        {
            if (string.Equals(nodeType, SmallNodeType.Pedestrian.ToString(), System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nodeType, SmallNodeType.Vehicle.ToString(), System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nodeType, SmallNodeType.Agent.ToString(), System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 其余情况按非动态目标处理。
        return false;
    }

    /// <summary>
    /// 从世界坐标构建 A* waypoint 列表。
    /// </summary>
    /// <param name="startWorld">逻辑起点世界坐标，通常是当前位置或上一段路径的末端。</param>
    /// <param name="targetWorld">本次 MoveTo 想要接近的目标世界坐标。</param>
    /// <param name="waypoints">输出 A* 采样后得到的 waypoint 列表。</param>
    /// <returns>成功构建出至少一个可执行 waypoint 时返回 true。</returns>
    private bool TryBuildAStarWaypoints(Vector3 startWorld, Vector3 targetWorld, out List<Vector3> waypoints)
    {
        // 默认输出空列表，保证调用方安全读取。
        waypoints = new List<Vector3>();
        // 没有网格系统无法做 A*。
        if (campusGrid == null) return false;

        // 把起点解析到可通行网格；必要时可回退到最近可通行格。
        if (!TryResolveWalkableCell(startWorld, out Vector2Int startCell)) return false;
        // 把目标点同样落到可通行网格。
        if (!TryResolveWalkableCell(targetWorld, out Vector2Int goalCell)) return false;

        // 构建“临时阻塞层”，把感知到的动态/阻塞 SmallNode 投影到网格上。
        HashSet<long> transientBlocked = BuildTransientBlockedCellOverlay(startWorld, targetWorld, startCell, goalCell);
        // 在 CampusGrid2D 上执行 A* 搜索。
        List<Vector2Int> gridPath = campusGrid.FindPathAStar(startCell, goalCell, null, transientBlocked);
        // 路径为空或长度过短时，不足以形成有效展开。
        if (gridPath == null || gridPath.Count < 2) return false;

        // 每隔 stride 个网格采一个 waypoint，避免把所有离散格都下发成动作。
        int stride = Mathf.Max(1, astarWaypointStride);
        // 巡航段高度。
        float cruiseY = ResolveWaypointY(targetWorld, false);
        // 最后接近目标时的高度。
        float finalY = ResolveWaypointY(targetWorld, true);
        // 用集合收集需要保留的网格索引，避免重复。
        HashSet<int> sampledIndices = new HashSet<int>();
        // 按固定步长采样整条路径。
        for (int i = stride; i < gridPath.Count; i += stride)
        {
            sampledIndices.Add(i);
        }
        // 额外保留所有拐点，避免压缩后路径丢失转弯信息。
        foreach (int turnIndex in CollectGridTurnIndices(gridPath))
        {
            sampledIndices.Add(turnIndex);
        }

        // 按索引顺序把采样网格转换成世界坐标 waypoint。
        foreach (int idx in sampledIndices.OrderBy(v => v))
        {
            // 越界索引跳过。
            if (idx < 0 || idx >= gridPath.Count) continue;
            // 当前采样到的网格坐标。
            Vector2Int c = gridPath[idx];
            // 转成该网格中心的世界坐标。
            Vector3 wp = campusGrid.GridToWorldCenter(c.x, c.y, astarWaypointYOffset);
            // 巡航段统一使用巡航高度。
            wp.y = cruiseY;
            // 与前一个点过近时不重复加入。
            if (waypoints.Count == 0 || Vector3.Distance(waypoints[waypoints.Count - 1], wp) > 0.1f)
            {
                waypoints.Add(wp);
            }
        }

        // 计算目标网格中心点，作为必要时的保守终点。
        Vector3 goalCenter = campusGrid.GridToWorldCenter(goalCell.x, goalCell.y, astarWaypointYOffset);
        goalCenter.y = finalY;

        // 构造真正想接近的最终世界点，但高度切换为最终接近高度。
        Vector3 finalTarget = new Vector3(targetWorld.x, finalY, targetWorld.z);
        // 检查用户/系统给出的 targetWorld 是否偏离目标网格中心太远。
        float targetToGoalCenter = Vector2.Distance(
            new Vector2(finalTarget.x, finalTarget.z),
            new Vector2(goalCenter.x, goalCenter.z)
        );
        if (targetToGoalCenter > campusGrid.cellSize * 0.75f)
        {
            // 偏离过大时回退到目标格中心，更符合 A* 离散路径的落点。
            finalTarget = goalCenter;
        }

        // 确保路径末端一定包含最终接近点。
        if (waypoints.Count == 0 || Vector3.Distance(waypoints[waypoints.Count - 1], finalTarget) > Mathf.Max(0.5f, campusGrid.cellSize * 0.5f))
        {
            waypoints.Add(finalTarget);
        }

        // waypoint 数量过长时进一步压缩。
        int maxCount = Mathf.Max(1, astarMaxWaypoints);
        if (waypoints.Count > maxCount)
        {
            waypoints = DownsampleWaypoints(waypoints, maxCount);
        }

        // 至少保留一个点才算成功。
        return waypoints.Count > 0;
    }

    /// <summary>
    /// 把二维网格坐标打包成单个 long，方便放进 HashSet 做快速查重。
    /// </summary>
    /// <param name="cell">待打包的网格坐标。</param>
    /// <returns>由 x/y 组合得到的唯一 long 键。</returns>
    private static long PackGridCellKey(Vector2Int cell)
    {
        // 高 32 位放 x，低 32 位放 y，生成可哈希的唯一键。
        return (((long)cell.x) << 32) ^ (uint)cell.y;
    }

    /// <summary>
    /// 根据当前感知的小节点，构造一层“临时阻塞网格”覆盖到 A* 搜索中。
    /// </summary>
    /// <param name="startWorld">当前路径起点世界坐标。</param>
    /// <param name="goalWorld">当前路径目标世界坐标。</param>
    /// <param name="startCell">起点落到的网格。</param>
    /// <param name="goalCell">终点落到的网格。</param>
    /// <returns>被视为临时不可通行的网格键集合。</returns>
    private HashSet<long> BuildTransientBlockedCellOverlay(Vector3 startWorld, Vector3 goalWorld, Vector2Int startCell, Vector2Int goalCell)
    {
        // 收集被临时视为不可通行的网格。
        HashSet<long> blocked = new HashSet<long>();
        // 配置关闭、无网格、无感知结果时，直接返回空覆盖层。
        if (!useSmallNodeDynamicOverlay || campusGrid == null || agentState?.DetectedSmallNodes == null) return blocked;

        // 根据当前 RoutePolicy 提取“需要规避的小节点类型”。
        HashSet<SmallNodeType> routeAvoidTypes = new HashSet<SmallNodeType>();
        RoutePolicyDefinition currentRoutePolicy = planningModule != null ? planningModule.GetCurrentStepRoutePolicy() : null;
        if (currentRoutePolicy != null && currentRoutePolicy.avoidNodeTypes != null)
        {
            // 遍历 routePolicy 中声明的每个待规避类型。
            for (int i = 0; i < currentRoutePolicy.avoidNodeTypes.Length; i++)
            {
                SmallNodeType nodeType = currentRoutePolicy.avoidNodeTypes[i];
                // Unknown 没有实际语义，不加入集合。
                if (nodeType != SmallNodeType.Unknown)
                {
                    routeAvoidTypes.Add(nodeType);
                }
            }
        }

        // 离终点太近的小节点不纳入覆盖，避免把最终目标区域完全堵死。
        float goalIgnoreDistance = campusGrid.cellSize * 1.25f;
        // 遍历当前感知到的所有小节点。
        for (int i = 0; i < agentState.DetectedSmallNodes.Count; i++)
        {
            // 当前小节点。
            SmallNodeData node = agentState.DetectedSmallNodes[i];
            // 只有满足投影条件的节点才会被映射到阻塞层。
            if (!ShouldProjectSmallNodeToOverlay(node)) continue;
            // 如果 routePolicy 指定了规避类型，则普通静态无阻塞节点不应误入覆盖。
            if (routeAvoidTypes.Count > 0 && !routeAvoidTypes.Contains(node.NodeType) && !node.BlocksMovement && !node.IsDynamic)
            {
                continue;
            }

            // 优先使用场景对象实时位置，否则使用缓存世界坐标。
            Vector3 nodeWorld = node.SceneObject != null ? node.SceneObject.transform.position : node.WorldPosition;
            // 接近最终目标的小节点忽略，以免终点不可达。
            if (Vector2.Distance(new Vector2(nodeWorld.x, nodeWorld.z), new Vector2(goalWorld.x, goalWorld.z)) <= goalIgnoreDistance)
            {
                continue;
            }

            // 把小节点投影到网格。
            Vector2Int rawCell = campusGrid.WorldToGrid(nodeWorld);
            // 越界节点忽略。
            if (!campusGrid.IsInBounds(rawCell.x, rawCell.y)) continue;

            // 动态节点的扩张半径更大，静态阻塞节点略小一点。
            int radius = node.IsDynamic ? Mathf.Max(1, smallNodeOverlayRadiusCells) : Mathf.Max(0, smallNodeOverlayRadiusCells - 1);
            // 以该节点为中心向外扩一圈网格，形成临时避障区域。
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    // 当前待标记的覆盖网格。
                    Vector2Int c = new Vector2Int(rawCell.x + dx, rawCell.y + dz);
                    // 越界格跳过。
                    if (!campusGrid.IsInBounds(c.x, c.y)) continue;
                    // 起点与终点格始终保留可达，不加入阻塞层。
                    if (c == startCell || c == goalCell) continue;
                    // 加入阻塞集合。
                    blocked.Add(PackGridCellKey(c));
                }
            }
        }

        // 返回最终的临时阻塞覆盖层。
        return blocked;
    }

    /// <summary>
    /// 判断某个 SmallNode 是否应该投影到临时阻塞层。
    /// </summary>
    /// <param name="node">当前感知到的小节点。</param>
    /// <returns>需要作为动态/静态障碍参与 A* 规划时返回 true。</returns>
    private bool ShouldProjectSmallNodeToOverlay(SmallNodeData node)
    {
        // 空节点不参与投影。
        if (node == null) return false;
        // 动态节点是否参与投影由 overlayDynamicSmallNodes 决定。
        if (node.IsDynamic) return overlayDynamicSmallNodes;
        // 纯静态阻塞节点是否参与投影由 overlayStaticBlockingSmallNodes 决定。
        if (node.BlocksMovement) return overlayStaticBlockingSmallNodes;
        // 其他普通节点默认不投影。
        return false;
    }

    /// <summary>
    /// 解析世界点对应的可通行网格。
    /// </summary>
    /// <param name="worldPos">待落到网格的世界坐标。</param>
    /// <param name="cell">输出解析到的可通行网格。</param>
    /// <returns>成功解析到可通行格，或成功回退到最近可通行格时返回 true。</returns>
    private bool TryResolveWalkableCell(Vector3 worldPos, out Vector2Int cell)
    {
        // 默认输出非法网格，表示解析失败。
        cell = new Vector2Int(-1, -1);
        // 没有网格系统无法解析。
        if (campusGrid == null) return false;

        // 先按世界坐标直接映射到网格。
        Vector2Int raw = campusGrid.WorldToGrid(worldPos);
        // 越界点直接失败。
        if (!campusGrid.IsInBounds(raw.x, raw.y)) return false;
        // 该格本身可通行时直接返回。
        if (campusGrid.IsWalkable(raw.x, raw.y))
        {
            cell = raw;
            return true;
        }

        // 配置不允许“最近可通行格”回退时，立即失败。
        if (!astarPreferNearestWalkable) return false;
        // 在有限半径内找最近的可通行格。
        if (campusGrid.TryFindNearestWalkable(raw, 6, out Vector2Int nearest))
        {
            cell = nearest;
            return true;
        }

        // 找不到可通行格则失败。
        return false;
    }

    /// <summary>
    /// 为展开出来的单个 waypoint MoveTo 生成执行参数。
    /// </summary>
    /// <param name="originalMove">原始 MoveTo，用于继承已有参数与目标元数据。</param>
    /// <param name="waypoint">当前 waypoint 的世界坐标。</param>
    /// <param name="isFinalWaypoint">当前 waypoint 是否是该段路径的最后一个点。</param>
    /// <param name="pathMode">本段路径来源，例如 `SystemCoarsePath`、`LLMPath`、`AStarReactive`。</param>
    /// <param name="waypointIndex">当前 waypoint 在该段中的 1-based 序号。</param>
    /// <param name="waypointTotal">该段 waypoint 总数。</param>
    /// <returns>可直接写回 ActionCommand.Parameters 的 JSON 字符串。</returns>
    private string BuildExpandedWaypointParameters(ActionCommand originalMove, Vector3 waypoint, bool isFinalWaypoint, string pathMode, int waypointIndex, int waypointTotal)
    {
        // 先继承原始 MoveTo 的参数，避免丢失 routePolicy、coordination 等上游上下文。
        Dictionary<string, string> map = ParseLooseParameterMap(originalMove != null ? originalMove.Parameters : string.Empty);

        // 标记当前路径段的来源。
        map["pathMode"] = string.IsNullOrWhiteSpace(pathMode) ? "Path" : pathMode;
        // 记录当前 waypoint 的序号。
        map["waypointIndex"] = waypointIndex.ToString(CultureInfo.InvariantCulture);
        // 记录 waypoint 总数，便于日志和执行层判断。
        map["waypointTotal"] = waypointTotal.ToString(CultureInfo.InvariantCulture);
        // 为 waypoint 子段设置更宽松且一致的到达容差。
        map["posTol"] = "1.8";
        map["heightTol"] = "0.6";
        map["horiTol"] = "1.2";
        map["vertTol"] = "0.7";
        map["settleTime"] = "0.05";

        // 如果 waypoint 能映射到可通行网格，则把对应网格信息也并入参数。
        if (TryResolveWalkableCell(waypoint, out Vector2Int waypointCell))
        {
            map["gridX"] = waypointCell.x.ToString(CultureInfo.InvariantCulture);
            map["gridZ"] = waypointCell.y.ToString(CultureInfo.InvariantCulture);
            map["gridTarget"] = BuildGridTargetToken(waypointCell);
        }
        else if (!isFinalWaypoint)
        {
            // 中间 waypoint 如果解析不到可通行网格，就移除旧 grid 信息，避免误用。
            map.Remove("gridX");
            map.Remove("gridZ");
            map.Remove("gridTarget");
        }

        // 中间 waypoint 不应该继续携带最终 feature 目标语义，否则可能提前判定完成。
        if (!isFinalWaypoint)
        {
            // 中间段统一视为离散网格目标。
            map["targetKind"] = "Grid";
            // 标明该目标来自哪种路径展开模式。
            map["targetSource"] = string.IsNullOrWhiteSpace(pathMode) ? "Waypoint" : pathMode;
            // 到达判断只使用 waypoint 容差，不使用 feature 感知完成条件。
            map["useWaypointToleranceOnly"] = "1";
            map["disableFeaturePerceptionArrival"] = "1";
            map["targetArrivalRadius"] = "0";
            map["finalApproach"] = "0";

            // 删除只属于最终 feature/collection 目标的元数据，避免中途被误解为已到达主目标。
            map.Remove("targetUid");
            map.Remove("targetName");
            map.Remove("targetAlias");
            map.Remove("targetFeature");
            map.Remove("targetCollectionKey");
            map.Remove("targetCenterX");
            map.Remove("targetCenterZ");
            map.Remove("targetOrbitRadius");
            map.Remove("targetOccupiedCellCount");
        }

        // 序列化为标准 JSON 风格参数文本。
        return BuildJsonFromParameterMap(map);
    }

    /// <summary>
    /// 把 A* waypoint 列表转换为连续 MoveTo 动作。
    /// </summary>
    /// <param name="originalMove">原始 MoveTo 动作，作为模板继承目标朝向和最终绑定信息。</param>
    /// <param name="waypoints">待展开的 waypoint 世界坐标序列。</param>
    /// <param name="output">输出动作列表，新的 MoveTo 会按顺序追加进去。</param>
    /// <param name="pathMode">当前路径来源标签，会写入每段 waypoint 参数中。</param>
    private void AppendWaypointsAsMoveActions(ActionCommand originalMove, List<Vector3> waypoints, List<ActionCommand> output, string pathMode)
    {
        // 缺少必要输入时直接返回，不做任何展开。
        if (originalMove == null || waypoints == null || output == null) return;
        // 没有 waypoint 时回退原始 MoveTo，保证行为不丢失。
        if (waypoints.Count == 0)
        {
            output.Add(originalMove);
            return;
        }

        // 记录总 waypoint 数，供参数写入序号和末段判断。
        int total = waypoints.Count;
        // 为每个 waypoint 生成一条独立的 MoveTo。
        for (int i = 0; i < waypoints.Count; i++)
        {
            // 当前点是否为末段；只有末段保留最终目标对象绑定。
            bool isFinalWaypoint = i == total - 1;
            ActionCommand waypointMove = new ActionCommand
            {
                // 展开后每一段都是标准 MoveTo。
                ActionType = PrimitiveActionType.MoveTo,
                // 目标位置就是当前 waypoint 坐标。
                TargetPosition = waypoints[i],
                // 朝向沿用原始动作。
                TargetRotation = originalMove.TargetRotation,
                // 中间段不保留 TargetObject，避免过早触发“贴近最终目标对象”的逻辑。
                TargetObject = (i == total - 1) ? originalMove.TargetObject : null,
                // 重新构造参数，写入 waypointIndex/pathMode/gridTarget 等信息。
                Parameters = BuildExpandedWaypointParameters(originalMove, waypoints[i], isFinalWaypoint, pathMode, i + 1, total)
            };

            // 追加到输出动作序列。
            output.Add(waypointMove);
        }
    }

    /// <summary>
    /// 控制 waypoint 数量，避免一次下发过长动作链。
    /// </summary>
    /// <param name="source">原始 waypoint 列表。</param>
    /// <param name="maxCount">允许保留的最大 waypoint 数量。</param>
    /// <returns>按均匀采样压缩后的 waypoint 列表。</returns>
    private static List<Vector3> DownsampleWaypoints(List<Vector3> source, int maxCount)
    {
        // 空输入返回空列表。
        if (source == null) return new List<Vector3>();
        if (source.Count == 0) return new List<Vector3>();
        // 只允许保留 1 个点时，保留终点最有意义。
        if (maxCount <= 1) return new List<Vector3> { source[source.Count - 1] };
        // 原始数量已经不超过上限时，直接返回原列表。
        if (source.Count <= maxCount) return source;

        // 按均匀采样方式挑选 maxCount 个索引。
        List<Vector3> result = new List<Vector3>(maxCount);
        for (int i = 0; i < maxCount; i++)
        {
            // t 在 [0,1] 均匀分布。
            float t = (float)i / (maxCount - 1);
            // 映射回原始列表索引。
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
    /// <param name="source">原始 waypoint 列表。</param>
    /// <param name="maxCount">最大允许保留的 waypoint 数。</param>
    /// <param name="preserveIndices">必须保留的原始索引集合，例如 viaTargets 对应的检查点。</param>
    /// <param name="keptSourceIndices">输出最终被保留的原始索引列表。</param>
    /// <returns>压缩后且尽量保留关键索引的 waypoint 列表。</returns>
    private static List<Vector3> DownsampleWaypointsPreserveIndices(List<Vector3> source, int maxCount, IEnumerable<int> preserveIndices, out List<int> keptSourceIndices)
    {
        // 输出参数先初始化为空列表。
        keptSourceIndices = new List<int>();
        // 空输入直接返回空结果。
        if (source == null || source.Count == 0) return new List<Vector3>();
        // 只允许保留一个点时，退化为仅保留终点。
        if (maxCount <= 1)
        {
            keptSourceIndices.Add(source.Count - 1);
            return new List<Vector3> { source[source.Count - 1] };
        }
        // 原始数量本来就在上限内时，全量保留。
        if (source.Count <= maxCount)
        {
            keptSourceIndices.AddRange(Enumerable.Range(0, source.Count));
            return new List<Vector3>(source);
        }

        // 先把必须保留的索引过滤成合法、去重、升序的列表。
        List<int> preserved = (preserveIndices ?? Enumerable.Empty<int>())
            .Where(i => i >= 0 && i < source.Count)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        // 如果没有任何必须保留索引，就回退到普通均匀采样。
        if (preserved.Count == 0)
        {
            List<Vector3> fallback = DownsampleWaypoints(source, maxCount);
            for (int i = 0; i < fallback.Count; i++)
            {
                // 反推每个保留点大致对应的原始索引，方便上层调试。
                float t = fallback.Count == 1 ? 1f : (float)i / (fallback.Count - 1);
                keptSourceIndices.Add(Mathf.RoundToInt(t * (source.Count - 1)));
            }
            return fallback;
        }

        // 如果必须保留的点本身就超过上限，只能截断到 maxCount。
        if (preserved.Count > maxCount)
        {
            preserved = preserved.Take(maxCount).ToList();
        }

        // 剩余还能补多少个采样点。
        int remainingSlots = maxCount - preserved.Count;
        // 候选索引是那些不在 preserved 集合里的索引。
        List<int> candidates = Enumerable.Range(0, source.Count)
            .Where(i => !preserved.Contains(i))
            .ToList();

        // selected 最终收集所有保留索引。
        HashSet<int> selected = new HashSet<int>(preserved);
        for (int i = 0; i < remainingSlots && candidates.Count > 0; i++)
        {
            // 在候选区间内均匀挑点补足剩余槽位。
            float t = remainingSlots == 1 ? 0.5f : (float)i / (remainingSlots - 1);
            int idx = Mathf.RoundToInt(t * (candidates.Count - 1));
            selected.Add(candidates[idx]);
        }

        // 对保留索引排序并限制到 maxCount。
        List<int> ordered = selected.OrderBy(i => i).Take(maxCount).ToList();
        keptSourceIndices.AddRange(ordered);
        // 把这些索引映射回真正的 waypoint。
        return ordered.Select(i => source[i]).ToList();
    }

    /// <summary>
    /// 收集网格路径中的转弯索引。
    /// </summary>
    /// <param name="gridPath">完整的离散网格路径。</param>
    /// <returns>所有方向变化点在路径中的索引。</returns>
    private static IEnumerable<int> CollectGridTurnIndices(List<Vector2Int> gridPath)
    {
        // 小于 3 个格子不可能形成转弯。
        if (gridPath == null || gridPath.Count < 3) yield break;

        // 先记录第一段的前进方向。
        Vector2Int prevDir = gridPath[1] - gridPath[0];
        for (int i = 2; i < gridPath.Count; i++)
        {
            // 当前这一步的移动方向。
            Vector2Int currDir = gridPath[i] - gridPath[i - 1];
            if (currDir != prevDir)
            {
                // 方向发生变化，上一格就是一个转弯点。
                yield return i - 1;
                prevDir = currDir;
            }
        }
    }

    /// <summary>
    /// 把网格路径转换为用于调试/可视化的世界坐标点列。
    /// </summary>
    /// <param name="gridPath">离散网格路径。</param>
    /// <param name="startWorld">起点世界坐标。</param>
    /// <param name="y">可视化时统一使用的高度。</param>
    /// <returns>包含起点和各网格中心的世界坐标列表。</returns>
    private List<Vector3> BuildGridPathVisualizationPoints(List<Vector2Int> gridPath, Vector3 startWorld, float y)
    {
        // 预分配用于画线/调试的世界坐标序列。
        List<Vector3> vizPoints = new List<Vector3>();
        // 无路径或无网格系统时直接返回空结果。
        if (gridPath == null || gridPath.Count == 0 || campusGrid == null) return vizPoints;

        // 把起点高度统一到给定 y，作为可视化起始点。
        startWorld.y = y;
        vizPoints.Add(startWorld);
        for (int i = 0; i < gridPath.Count; i++)
        {
            // 当前网格。
            Vector2Int cell = gridPath[i];
            // 转换成该格中心的世界点。
            Vector3 wp = campusGrid.GridToWorldCenter(cell.x, cell.y, astarWaypointYOffset);
            wp.y = y;
            // 与前一点距离足够大时才加入，避免重复点影响显示。
            if (vizPoints.Count == 0 || Vector3.Distance(vizPoints[vizPoints.Count - 1], wp) > 0.05f)
            {
                vizPoints.Add(wp);
            }
        }

        return vizPoints;
    }

    /// <summary>
    /// 统一 waypoint 高度：
    /// 1) 平面运动仍走 CampusGrid2D 的离散格；
    /// 2) Y 轴不再让高层随意给连续值，而是映射到离散高度层；
    /// 3) HighThenDescend 这类策略会区分“巡航高度”和“末段接近高度”。
    /// </summary>
    /// <param name="targetWorld">当前目标世界坐标，若自带有效 y 可能直接复用。</param>
    /// <param name="isFinalApproach">是否处于末段接近目标阶段。</param>
    /// <returns>当前 waypoint 应采用的离散高度层值。</returns>
    private float ResolveWaypointY(Vector3 targetWorld, bool isFinalApproach = false)
    {
        // 非四旋翼通常不走分层高度逻辑，直接沿用当前位置高度。
        if (agentProperties == null || agentProperties.Type != AgentType.Quadcopter)
        {
            return transform.position.y;
        }

        // 计算离散高度层，并保证层间至少有一定差值。
        float low = Mathf.Max(0.5f, lowAltitudeLayerHeight);
        float medium = Mathf.Max(low + 0.5f, mediumAltitudeLayerHeight);
        float high = Mathf.Max(medium + 0.5f, highAltitudeLayerHeight);
        // 最终接近高度被限制在低层到中层之间。
        float finalApproach = Mathf.Clamp(finalApproachAltitudeLayerHeight, low, medium);
        // 当前飞行高度至少不低于 low。
        float current = Mathf.Max(transform.position.y, low);
        // 如果 targetWorld 已显式给了 y，则优先视为上游明确指定的高度。
        float explicitTargetY = targetWorld.y > 0.1f ? targetWorld.y : current;

        // 读取当前 step 的路径高度策略。
        RoutePolicyDefinition routePolicy = planningModule != null ? planningModule.GetCurrentStepRoutePolicy() : null;
        RouteAltitudeMode altitudeMode = routePolicy != null ? routePolicy.altitudeMode : RouteAltitudeMode.Default;

        switch (altitudeMode)
        {
            case RouteAltitudeMode.KeepCurrent:
                // 始终保持当前高度。
                return current;
            case RouteAltitudeMode.Low:
                // 强制低空层。
                return low;
            case RouteAltitudeMode.Medium:
                // 强制中空层。
                return medium;
            case RouteAltitudeMode.High:
                // 强制高空层。
                return high;
            case RouteAltitudeMode.HighThenDescend:
                // 巡航高空，末段下降。
                return isFinalApproach ? finalApproach : high;
            default:
                // 默认策略：若目标自带高度则尊重它，否则巡航尽量不低于中层，末段降到 finalApproach。
                return targetWorld.y > 0.1f ? explicitTargetY : (isFinalApproach ? finalApproach : Mathf.Max(current, medium));
        }
    }

    /// <summary>
    /// 只解析动作数据，不做目标坐标绑定。
    /// 用于第一阶段高层语义解析和第二阶段局部 waypoint 细化。
    /// </summary>
    /// <param name="jsonResponse">LLM 返回的原始文本，期望其中包含 JSON 数组。</param>
    /// <returns>解析出的 ActionData 列表；失败时返回 null。</returns>
    private List<ActionData> ParseActionDataSequenceFromJSON(string jsonResponse)
    {
        // 先从 LLM 文本中抽出纯 JSON 片段，剥离解释性前后文。
        string cleanJson = ExtractPureJson(jsonResponse);
        if (string.IsNullOrWhiteSpace(cleanJson))
        {
            Debug.LogError("无法从响应中提取 JSON 内容");
            return null;
        }

        // 先尝试严格解析，优先保证格式正确。
        List<ActionData> actionDataList = TryParseActionDataListStrict(cleanJson);
        if (actionDataList == null || actionDataList.Count == 0)
        {
            // 严格解析失败时，退回更宽松的容错解析。
            actionDataList = TryParseActionDataListResilient(cleanJson);
        }

        if (actionDataList == null || actionDataList.Count == 0)
        {
            Debug.LogError($"JSON解析失败: 无法从响应中提取有效动作对象。\n原始响应: {jsonResponse}");
            return null;
        }

        // 返回成功解析出的动作数据列表。
        return actionDataList;
    }

    /// <summary>
    /// 把高层动作中的 target 文本，转换成系统真正能执行的“目标绑定”。
    /// 可以把它理解成一个“翻译器”：
    /// - 输入是人或 LLM 写出来的目标描述；
    /// - 输出是程序能直接使用的目标信息，比如目标类型、世界坐标、网格格子、名字、UID。
    /// 如果一句 target 根本认不出来，就返回 false，表示这句话暂时不能落地执行。
    /// </summary>
    /// <param name="target">待解析的目标文本。</param>
    /// <param name="binding">输出解析后的 StepTargetBinding。</param>
    /// <returns>目标文本成功转成可执行绑定时返回 true。</returns>
    private bool TryBuildStepTargetBindingFromTarget(string target, out StepTargetBinding binding)
    {
        // 无结构化 targetRef 的简化入口，直接转调完整版。
        return TryBuildStepTargetBindingFromTarget(target, null, out binding);
    }

    /// <summary>
    /// 把高层动作中的 target 文本，转换成系统真正能执行的“目标绑定”。
    /// 若同时拿到了结构化 targetRef，就优先消费其中的 grounded 锚点和几何偏置。
    /// </summary>
    /// <param name="target">高层动作里的目标文本，例如 `step_target`、`grid(10,8)`、`1号楼南侧`、`Agent_2`。</param>
    /// <param name="targetRef">可选的结构化目标引用，若存在则可提供 grounded 锚点、relation、anchorBias 等信息。</param>
    /// <param name="binding">输出解析后的目标绑定。</param>
    /// <returns>成功把文本目标翻译成执行层可用绑定时返回 true。</returns>
    private bool TryBuildStepTargetBindingFromTarget(string target, StructuredTargetReference targetRef, out StepTargetBinding binding)
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
            SmallNodeType = SmallNodeType.Unknown,
            IsDynamicTarget = false,
            BlocksMovement = false,
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
        if (TryResolveCampusFeatureTarget(raw, out CampusGrid2D.FeatureSpatialProfile featureProfile, out string featureSource, out string normalized, targetRef))
        {
            binding.HasTarget = true;
            binding.TargetKind = "Feature";
            binding.Source = $"HighLevel:{featureSource}";
            binding.WorldPos = featureProfile != null ? featureProfile.anchorWorld : transform.position;
            binding.GridCell = featureProfile != null ? featureProfile.anchorCell : new Vector2Int(-1, -1);
            binding.FeatureCenterWorld = featureProfile != null ? featureProfile.centroidWorld : binding.WorldPos;
            binding.FeatureCenterCell = featureProfile != null ? featureProfile.centroidCell : binding.GridCell;
            binding.FeatureArrivalRadius = featureProfile != null
                ? Mathf.Clamp(
                    Mathf.Max(featureArrivalMinRadius, featureProfile.footprintRadius * featureArrivalCellRadiusScale),
                    featureArrivalMinRadius,
                    Mathf.Max(featureArrivalMinRadius, featureArrivalMaxRadius))
                : featureArrivalMinRadius;
            binding.FeatureOrbitRadius = featureProfile != null
                ? Mathf.Max(binding.FeatureArrivalRadius, featureProfile.footprintRadius + (campusGrid != null ? campusGrid.cellSize : 2f))
                : Mathf.Max(binding.FeatureArrivalRadius, featureArrivalMinRadius + 2f);
            binding.FeatureOccupiedCellCount = featureProfile != null ? Mathf.Max(0, featureProfile.occupiedCellCount) : 0;
            binding.CollectionKey = featureProfile != null && !string.IsNullOrWhiteSpace(featureProfile.collectionKey)
                ? featureProfile.collectionKey
                : string.Empty;
            binding.Uid = featureProfile != null ? (featureProfile.uid ?? string.Empty) : string.Empty;
            binding.Name = !string.IsNullOrWhiteSpace(normalized)
                ? normalized
                : (featureProfile != null
                    ? (!string.IsNullOrWhiteSpace(featureProfile.runtimeAlias)
                        ? featureProfile.runtimeAlias
                        : (!string.IsNullOrWhiteSpace(featureProfile.name) ? featureProfile.name : featureProfile.uid))
                    : string.Empty);

            // 对 feature 来说，真正的“要去哪儿”应该是可通行接近点，而不是建筑内部中心点。
            // Orbit 之类动作会单独读取 FeatureCenterWorld/FeatureOrbitRadius。
            if (binding.GridCell.x < 0 || binding.GridCell.y < 0)
            {
                if (TryResolveWalkableCell(binding.WorldPos, out Vector2Int c))
                {
                    binding.GridCell = c;
                }
            }

            if (string.IsNullOrWhiteSpace(binding.Uid))
            {
                binding.Uid = binding.Name;
            }

            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},uid={binding.Uid},name={binding.Name},source={binding.Source},anchor=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),center=({binding.FeatureCenterWorld.x:F1},{binding.FeatureCenterWorld.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y}),cells={binding.FeatureOccupiedCellCount},arriveR={binding.FeatureArrivalRadius:F1},orbitR={binding.FeatureOrbitRadius:F1}";
            return true;
        }

        // 第二类：显式网格坐标。
        // 这是离散执行链最直接的目标表达方式，形如 grid(12,34)。
        if (TryResolveGridTargetWorld(raw, out Vector3 gridWorld, out Vector2Int gridCell))
        {
            binding.HasTarget = true;
            binding.TargetKind = "Grid";
            binding.Source = "HighLevel:GridCell";
            binding.WorldPos = gridWorld;
            binding.GridCell = gridCell;
            binding.Name = $"grid({gridCell.x},{gridCell.y})";
            binding.Uid = binding.Name;
            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
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

        // 第四类：按稳定身份解析单个小节点。
        // 这里只接受 NodeId / DisplayName / SceneObject.name 的精确匹配，
        // 不再允许“树/车辆/最近行人”这类语义猜测把目标漂移到别的对象上。
        if (TryResolveExactSmallNodeBinding(raw, out SmallNodeData nodeData, out GameObject nodeObj, out Vector3 nodePos))
        {
            binding.HasTarget = true;
            binding.TargetKind = "SmallNode";
            binding.Source = "HighLevel:SmallNodeRegistryExact";
            binding.WorldPos = nodePos;
            binding.Name = !string.IsNullOrWhiteSpace(nodeData?.DisplayName)
                ? nodeData.DisplayName
                : (nodeObj != null ? nodeObj.name : raw);
            binding.Uid = nodeData?.NodeId ?? raw;
            binding.SmallNodeType = nodeData != null ? nodeData.NodeType : SmallNodeType.Unknown;
            binding.IsDynamicTarget = nodeData != null && nodeData.IsDynamic;
            binding.BlocksMovement = nodeData != null && nodeData.BlocksMovement;
            if (TryResolveWalkableCell(nodePos, out Vector2Int c))
            {
                binding.GridCell = c;
            }
            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},uid={binding.Uid},name={binding.Name},nodeType={binding.SmallNodeType},dynamic={(binding.IsDynamicTarget ? 1 : 0)},block={(binding.BlocksMovement ? 1 : 0)},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
            return true;
        }

        // 第五类：把 target 当作附近队友。
        // 这给 Follow / Wait / Align 这类协同动作提供具体跟随对象。
        if (TryFindNearbyAgent(raw, out GameObject teammate))
        {
            binding.HasTarget = true;
            binding.TargetKind = "Agent";
            binding.Source = "HighLevel:NearbyAgent";
            binding.WorldPos = teammate.transform.position;
            binding.Name = raw;
            binding.SmallNodeType = SmallNodeType.Agent;
            binding.IsDynamicTarget = true;
            binding.BlocksMovement = false;
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

    private bool IsMovementLikeActionData(ActionData actionData)
    {
        // 空动作数据不可能是移动类动作。
        if (actionData == null) return false;
        // 先宽松解析动作类型，再判断该原子动作是否属于移动/定位类。
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

    /// <summary>
    /// 把高层 ActionData 列表降级成可直接下发的 ActionCommand 列表。
    /// </summary>
    /// <param name="actionDataList">高层决策或确定性编译得到的动作数据序列。</param>
    /// <returns>解析后的执行命令序列。</returns>
    private List<ActionCommand> BuildActionCommandsFromActionData(List<ActionData> actionDataList)
    {
        // 收集最终可执行命令。
        List<ActionCommand> commands = new List<ActionCommand>();
        // 空输入直接返回空序列。
        if (actionDataList == null) return commands;

        for (int i = 0; i < actionDataList.Count; i++)
        {
            // 把单条 ActionData 解析成 ActionCommand。
            ActionCommand command = ParseSingleAction(actionDataList[i]);
            if (command != null)
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    private bool SequenceContainsMovement(List<ActionCommand> actions)
    {
        // 空动作序列默认不包含移动。
        if (actions == null || actions.Count == 0) return false;
        for (int i = 0; i < actions.Count; i++)
        {
            // 当前执行命令。
            ActionCommand action = actions[i];
            // 只要存在一条移动类命令，就把整段序列标记为“含移动”。
            if (action != null && IsMovementLikePrimitiveAction(action.ActionType))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 严格解析动作数组（完整 JSON）。
    /// 支持：
    /// 1) 纯数组: [ ... ]
    /// 2) 对象包裹: { "actions": [ ... ] }
    /// </summary>
    /// <param name="cleanJson">已经抽取出的纯 JSON 文本。</param>
    /// <returns>严格解析得到的动作列表；失败时返回 null。</returns>
    private List<ActionData> TryParseActionDataListStrict(string cleanJson)
    {
        try
        {
            // 先假设输入本身就是包着 actions 字段的对象。
            ActionSequenceWrapper wrapper = null;
            if (cleanJson.TrimStart().StartsWith("{"))
            {
                wrapper = JsonUtility.FromJson<ActionSequenceWrapper>(cleanJson);
            }
            if (wrapper == null || wrapper.actions == null)
            {
                // 如果不是对象包装，则人为包上一层 {"actions": ...} 再解析。
                wrapper = JsonUtility.FromJson<ActionSequenceWrapper>($"{{\"actions\":{cleanJson}}}");
            }
            return wrapper?.actions;
        }
        catch (System.Exception e)
        {
            // 严格解析失败时不直接抛错，而是交给更宽松的容错解析。
            Debug.LogWarning($"严格JSON解析失败，尝试容错解析: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 容错解析：
    /// 当 LLM 输出被截断（例如最后一个对象没闭合）时，
    /// 提取数组中“完整闭合”的对象，忽略不完整尾部。
    /// </summary>
    /// <param name="text">待容错解析的原始文本。</param>
    /// <returns>从文本中恢复出的完整 ActionData 列表。</returns>
    private List<ActionData> TryParseActionDataListResilient(string text)
    {
        // 收集最终成功恢复的动作。
        List<ActionData> actions = new List<ActionData>();
        // 空文本直接返回空列表。
        if (string.IsNullOrWhiteSpace(text)) return actions;

        // 先把可能包含动作数组的片段截出来。
        string arrayLike = ExtractActionsArrayLikeText(text);
        // 再提取其中完整闭合的 JSON 对象。
        List<string> objectJsonList = ExtractCompleteJsonObjects(arrayLike);
        for (int i = 0; i < objectJsonList.Count; i++)
        {
            // 当前对象 JSON。
            string obj = objectJsonList[i];
            if (string.IsNullOrWhiteSpace(obj)) continue;

            try
            {
                // 单独解析这个对象。
                ActionData parsed = JsonUtility.FromJson<ActionData>(obj);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.actionType))
                {
                    actions.Add(parsed);
                }
            }
            catch
            {
                // 单个对象解析失败时跳过，避免整段报废。
            }
        }

        if (actions.Count > 0)
        {
            // 打印恢复条数，便于观察 LLM 输出截断时系统是否成功自愈。
            Debug.LogWarning($"容错JSON解析生效: 从响应中恢复 {actions.Count} 个完整动作对象。");
        }
        return actions;
    }

    /// <summary>
    /// 定位动作数组片段（优先 actions 字段，其次首个数组起点）。
    /// </summary>
    /// <param name="raw">可能包含动作数组的原始文本。</param>
    /// <returns>截出来的“像数组一样”的文本片段。</returns>
    private static string ExtractActionsArrayLikeText(string raw)
    {
        // 空文本直接返回空串。
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // 清理首尾空白。
        string s = raw.Trim();

        // 优先找 `"actions"` 字段后面的数组起点。
        int keyIdx = s.IndexOf("\"actions\"", System.StringComparison.OrdinalIgnoreCase);
        if (keyIdx >= 0)
        {
            int arrIdx = s.IndexOf('[', keyIdx);
            if (arrIdx >= 0) return s.Substring(arrIdx);
        }

        // 否则退回首个 `[` 之后的内容。
        int firstArr = s.IndexOf('[');
        if (firstArr >= 0) return s.Substring(firstArr);
        return s;
    }

    /// <summary>
    /// 从数组文本中提取完整闭合的 JSON 对象字符串。
    /// </summary>
    /// <param name="text">数组样式文本。</param>
    /// <returns>其中所有完整闭合的对象 JSON 字符串。</returns>
    private static List<string> ExtractCompleteJsonObjects(string text)
    {
        // 收集提取到的完整对象。
        List<string> objects = new List<string>();
        // 空文本直接返回空结果。
        if (string.IsNullOrWhiteSpace(text)) return objects;

        // inString 表示当前是否在 JSON 字符串内部。
        bool inString = false;
        // escaped 表示上一字符是否为反斜杠转义。
        bool escaped = false;
        // depth 表示当前对象嵌套深度。
        int depth = 0;
        // start 记录当前对象起始位置。
        int start = -1;

        for (int i = 0; i < text.Length; i++)
        {
            // 当前字符。
            char c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    // 转义状态只作用于一个字符，消费后立即清空。
                    escaped = false;
                }
                else if (c == '\\')
                {
                    // 进入转义状态。
                    escaped = true;
                }
                else if (c == '"')
                {
                    // 字符串结束。
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                // 遇到引号，进入字符串上下文。
                inString = true;
                continue;
            }

            if (c == '{')
            {
                // 深度为 0 时，这个 `{` 就是新对象起点。
                if (depth == 0) start = i;
                depth++;
                continue;
            }

            if (c == '}')
            {
                // 非法多余 `}` 直接跳过。
                if (depth <= 0) continue;
                depth--;
                if (depth == 0 && start >= 0 && i >= start)
                {
                    // 当深度回到 0 时，说明拿到了一个完整闭合对象。
                    objects.Add(text.Substring(start, i - start + 1));
                    start = -1;
                }
            }
        }

        return objects;
    }

    /// <summary>
    /// 从 LLM 原始响应中抽取纯 JSON 文本。
    /// </summary>
    /// <param name="response">LLM 返回的原始字符串。</param>
    /// <returns>抽取出的 JSON 文本；若无代码块则返回修剪后的原文。</returns>
    private string ExtractPureJson(string response)
    {
        // 空响应直接返回。
        if (string.IsNullOrEmpty(response))
            return response;

        // 如果包含 ```json 代码块，优先提取其中内容。
        if (response.Contains("```json"))
        {
            int jsonStart = response.IndexOf("```json") + 7;
            int jsonEnd = response.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                return response.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }

        // 如果只包含普通代码块标记，也尝试取第一段代码块内容。
        if (response.Contains("```"))
        {
            int jsonStart = response.IndexOf("```") + 3;
            int jsonEnd = response.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                return response.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }
        
        // 如果没有代码块标记，直接返回整个响应（可能本身就是纯 JSON）。
        return response.Trim();
    }
    /// <summary>
    /// 把单条高层 ActionData 解析成执行层 ActionCommand。
    /// </summary>
    /// <param name="actionData">单条高层动作数据，包含 actionType / target / parameters / reason。</param>
    /// <returns>解析成功的 ActionCommand；无法识别动作类型时返回 null。</returns>
    private ActionCommand ParseSingleAction(ActionData actionData)
    {
        // 没给动作类型就无法解析。
        if (string.IsNullOrEmpty(actionData.actionType))
            return null;

        // 创建最终要下发给 MLAgentsController 的命令对象。
        ActionCommand command = new ActionCommand();
        
        // 解析动作类型（支持常见别名：Move/Observe/Comm）。
        if (TryParsePrimitiveActionTypeFlexible(actionData.actionType, out PrimitiveActionType actionType))
        {
            command.ActionType = actionType;
        }
        else
        {
            Debug.LogWarning($"未知动作类型: {actionData.actionType}");
            return null;
        }

        // 默认目标为当前位置，避免空目标导致异常动作。
        command.TargetPosition = transform.position;
        // 默认朝向为当前朝向。
        command.TargetRotation = transform.rotation;

        // targetMeta 最终会被并入 Parameters，承载目标几何和来源信息。
        Dictionary<string, string> targetMeta = null;

        // 只有“无目标动作”之外的动作才需要解析 target。
        if (!IsNoTargetAction(actionType))
        {
            // 取出并清理 target 文本。
            string target = !string.IsNullOrWhiteSpace(actionData.target)
                ? actionData.target.Trim()
                : (!string.IsNullOrWhiteSpace(actionData.targetRef) ? actionData.targetRef.Trim() : string.Empty);
            targetMeta = new Dictionary<string, string>();

            // target 为空或显式 none 时，优先回填当前 step 已绑定好的主目标。
            if (string.IsNullOrEmpty(target) || IsNoneTargetToken(target))
            {
                if (lastStepTargetBinding.HasTarget)
                {
                    ApplyBoundTargetToCommand(command, actionType, lastStepTargetBinding, targetMeta);
                }
                else
                {
                    // 连 step 目标都没有时，只能退回当前位置。
                    command.TargetPosition = transform.position;
                    targetMeta["targetKind"] = "None";
                }
            }
            else if (TryResolveTargetFromDiscreteSelectionToken(
                target,
                actionType,
                out GameObject discreteObj,
                out Vector3 discretePos,
                out string discreteKind,
                out Dictionary<string, string> discreteMeta))
            {
                command.TargetObject = discreteObj;
                command.TargetPosition = discretePos;
                targetMeta["targetKind"] = discreteKind;
                foreach (var kv in discreteMeta)
                {
                    targetMeta[kv.Key] = kv.Value;
                }
            }
            else if (TryResolveTargetFromAllowedSources(
                target,
                out GameObject resolvedObj,
                out Vector3 resolvedPos,
                out string resolvedKind,
                out Dictionary<string, string> resolvedMeta))
            {
                // 写入解析出的目标对象。
                command.TargetObject = resolvedObj;
                // 写入解析出的目标位置。
                command.TargetPosition = resolvedPos;
                // 记录目标类型。
                targetMeta["targetKind"] = resolvedKind;
                foreach (var kv in resolvedMeta)
                {
                    // 把所有来源元数据复制到 targetMeta，后续统一并入 Parameters。
                    targetMeta[kv.Key] = kv.Value;
                }
                if (string.Equals(resolvedKind, "Feature", System.StringComparison.OrdinalIgnoreCase))
                {
                    // Feature 目标需要补 arrivalRadius 等几何信息，供完成判定和 Orbit 使用。
                    if (TryGetFeatureArrivalRadiusByResolvedMeta(resolvedMeta, resolvedPos, out float featureArrivalRadius))
                    {
                        targetMeta["targetArrivalRadius"] = featureArrivalRadius.ToString("F2", CultureInfo.InvariantCulture);
                    }

                    // 根据动作类型把 feature 锚点/中心点修正到真正该执行的位置。
                    ApplyResolvedFeatureGeometryToAction(command, actionType, targetMeta);
                }
            }
            else
            {
                // 明确写了 target 但系统认不出来时，保守回退到当前位置。
                command.TargetPosition = transform.position;
                targetMeta["targetKind"] = "Unknown";
                Debug.LogWarning($"无法解析目标: {target}，仅支持 CampusGrid地点/可见地点/小节点/world坐标/队友目标，已回退到当前位置。");
            }
        }
        // 解析参数文本，把高度、旋转、通信内容等动作参数写进 command。
        if (!string.IsNullOrEmpty(actionData.parameters))
        {
            ParseActionParameters(command, actionData.parameters);
        }
        if (targetMeta != null && targetMeta.Count > 0)
        {
            // 目标元数据最后统一和原始参数合并，避免被 ParseActionParameters 覆盖掉。
            command.Parameters = MergeParameters(command.Parameters, targetMeta);
        }
        // 新主路径不再把 Follow/Orbit 直接下发给控制器：
        // - Follow 缺对象时会在执行层空引用；
        // - Orbit 在离散候选链里应改写成 MoveTo + intermediateCells。
        actionType = NormalizeActionTypeForDiscreteExecution(command, actionData, actionType, targetMeta);
        command.ActionType = actionType;
        ApplyDiscreteSelectionParametersToCommand(command, actionData, actionType);

        // 补充旋转与高度目标（支持 RotateTo/AdjustAltitude）。
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
                // 调高度动作只改 y，保持当前位置的 x/z 不变。
                command.TargetPosition = new Vector3(transform.position.x, height, transform.position.z);
            }
        }
        return command;
    }

    /// <summary>
    /// 把不适合当前离散执行链的动作降级成控制器稳定可消费的原子动作。
    /// 当前重点处理：
    /// 1) Follow 缺对象时改成 MoveTo；
    /// 2) Orbit 在两次 LLM 离散链里改成 MoveTo + intermediateCells。
    /// </summary>
    /// <param name="command">已经完成目标解析的执行命令。</param>
    /// <param name="actionData">原始高层动作数据，用于读取参数。</param>
    /// <param name="actionType">当前解析出的原子动作类型。</param>
    /// <param name="targetMeta">目标元数据字典，用于记录降级来源。</param>
    /// <returns>最终应下发给控制器的稳定动作类型。</returns>
    private PrimitiveActionType NormalizeActionTypeForDiscreteExecution(
        ActionCommand command,
        ActionData actionData,
        PrimitiveActionType actionType,
        Dictionary<string, string> targetMeta)
    {
        // 缺少命令对象时无法做任何归一化，直接保留原动作。
        if (command == null)
        {
            return actionType;
        }

        // Follow 只有在确实拿到了动态对象引用时才适合下放给控制器。
        // 否则控制器侧会直接访问 target.transform，导致空引用或行为失真。
        if (actionType == PrimitiveActionType.Follow && command.TargetObject == null)
        {
            if (targetMeta != null)
            {
                targetMeta["actionDowngradedFrom"] = "Follow";
            }
            return PrimitiveActionType.MoveTo;
        }

        // 在当前离散候选主路径里，Orbit 不再作为最终控制器动作存在。
        // “绕圈/巡逻一周/外围行进”统一改写成 MoveTo，并依赖第一次 LLM 选出的 intermediateCells。
        if (actionType == PrimitiveActionType.Orbit &&
            lastDiscreteTargetSelection.IsValid &&
            string.Equals(lastDiscreteTargetSelectionStep, GetCurrentStepDescription(), System.StringComparison.OrdinalIgnoreCase))
        {
            if (targetMeta != null)
            {
                targetMeta["actionDowngradedFrom"] = "Orbit";
            }

            Dictionary<string, string> pmap = ParseLooseParameterMap(command.Parameters);
            pmap["useIntermediateCells"] = "1";
            command.Parameters = BuildJsonFromParameterMap(pmap);
            return PrimitiveActionType.MoveTo;
        }

        return actionType;
    }

    /// <summary>
    /// 解析第一次 LLM 已选出的离散目标引用。
    /// 这些 token 不是自由文本，而是 goal_cell / selected_candidate 这类受控引用。
    /// </summary>
    private bool TryResolveTargetFromDiscreteSelectionToken(
        string targetToken,
        PrimitiveActionType actionType,
        out GameObject targetObject,
        out Vector3 targetPosition,
        out string targetKind,
        out Dictionary<string, string> meta)
    {
        targetObject = null;
        targetPosition = transform.position;
        targetKind = "Unknown";
        meta = new Dictionary<string, string>();

        if (!lastDiscreteTargetSelection.IsValid ||
            string.IsNullOrWhiteSpace(targetToken) ||
            !string.Equals(lastDiscreteTargetSelectionStep, GetCurrentStepDescription(), System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = targetToken.Trim().ToLowerInvariant();
        if (token != "goal_cell" && token != "selected_candidate")
        {
            return false;
        }

        targetObject = lastDiscreteTargetSelection.TargetObject;
        targetPosition = lastDiscreteTargetSelection.WorldPos;
        targetKind = lastDiscreteTargetSelection.CandidateKind == "FeatureCandidateCells"
            ? "Grid"
            : lastDiscreteTargetSelection.CandidateKind;
        meta["targetSource"] = "DiscreteSelection";
        meta["selectedCandidateId"] = lastDiscreteTargetSelection.SelectedCandidateId ?? string.Empty;
        meta["selectedCandidateKind"] = lastDiscreteTargetSelection.CandidateKind ?? string.Empty;
        meta["targetDisplayName"] = lastDiscreteTargetSelection.DisplayName ?? string.Empty;
        if (lastDiscreteTargetSelection.GoalCell.x >= 0 && lastDiscreteTargetSelection.GoalCell.y >= 0)
        {
            meta["gridX"] = lastDiscreteTargetSelection.GoalCell.x.ToString(CultureInfo.InvariantCulture);
            meta["gridZ"] = lastDiscreteTargetSelection.GoalCell.y.ToString(CultureInfo.InvariantCulture);
            meta["gridTarget"] = BuildGridTargetToken(lastDiscreteTargetSelection.GoalCell);
            meta["useWaypointToleranceOnly"] = "1";
            meta["disableFeaturePerceptionArrival"] = "1";
        }
        return true;
    }

    /// <summary>
    /// 若第二次 LLM 明确要求使用第一次选择出的 intermediateCells，
    /// 这里把它们写成现有 MoveTo 可消费的 waypoints 参数。
    /// </summary>
    private void ApplyDiscreteSelectionParametersToCommand(ActionCommand command, ActionData actionData, PrimitiveActionType actionType)
    {
        if (command == null || actionData == null || actionType != PrimitiveActionType.MoveTo) return;
        if (!lastDiscreteTargetSelection.IsValid || lastDiscreteTargetSelection.IntermediateCells == null || lastDiscreteTargetSelection.IntermediateCells.Count == 0) return;

        Dictionary<string, string> pmap = ParseLooseParameterMap(command.Parameters);
        bool useIntermediateCells =
            pmap.TryGetValue("useIntermediateCells", out string useFlag) &&
            (string.Equals(useFlag, "1", System.StringComparison.OrdinalIgnoreCase) ||
             string.Equals(useFlag, "true", System.StringComparison.OrdinalIgnoreCase));
        if (!useIntermediateCells) return;

        List<Vector3> waypoints = new List<Vector3>();
        for (int i = 0; i < lastDiscreteTargetSelection.IntermediateCells.Count; i++)
        {
            Vector2Int cell = lastDiscreteTargetSelection.IntermediateCells[i];
            if (campusGrid == null || cell.x < 0 || cell.y < 0) continue;
            Vector3 wp = campusGrid.GridToWorldCenter(cell.x, cell.y, astarWaypointYOffset);
            wp.y = ResolveWaypointY(command.TargetPosition, true);
            waypoints.Add(wp);
        }

        if (waypoints.Count == 0) return;

        Dictionary<string, string> injected = new Dictionary<string, string>
        {
            ["waypoints"] = BuildWaypointInlineString(waypoints),
            ["segmentSource"] = "LLMSelectedCells"
        };
        command.Parameters = MergeParameters(command.Parameters, injected);
    }

    /// <summary>
    /// 把当前步骤已经绑定好的目标信息写回到参数字典。
    ///
    /// 这样做的目的，是让“显式写 step_target”和“完全没写 target 由系统自动补”
    /// 两条路径都拿到同一份几何元数据：
    /// - 接近锚点
    /// - 建筑/区域中心
    /// - 到达半径
    /// - Orbit 半径
    ///
    /// 否则 Orbit 在显式写 step_target 时，就只会看到一个点，而看不到整块 feature 的范围。
    /// </summary>
    /// <param name="targetMeta">待补充的目标元数据字典。</param>
    /// <param name="binding">当前步骤已经确定好的主目标绑定。</param>
    /// <param name="sourceTag">元数据来源标签，用于标记这些字段是从哪条链路补进来的。</param>
    private void AppendBoundTargetMetadata(Dictionary<string, string> targetMeta, StepTargetBinding binding, string sourceTag)
    {
        // 没字典或 binding 无目标时，没法补任何元数据。
        if (targetMeta == null || !binding.HasTarget) return;

        // 写入目标类型。
        targetMeta["targetKind"] = binding.TargetKind;
        // 写入来源标签。
        targetMeta["targetSource"] = sourceTag;
        // 标记这是 step_target 回填过来的目标。
        targetMeta["targetRef"] = "step_target";
        if (!string.IsNullOrWhiteSpace(binding.Uid)) targetMeta["targetUid"] = binding.Uid;
        if (!string.IsNullOrWhiteSpace(binding.Name)) targetMeta["targetName"] = binding.Name;
        if (!string.IsNullOrWhiteSpace(binding.CollectionKey)) targetMeta["targetCollectionKey"] = binding.CollectionKey;

        if (binding.TargetKind == "Feature")
        {
            // Feature 到达半径。
            if (binding.FeatureArrivalRadius > 0f)
            {
                targetMeta["targetArrivalRadius"] = binding.FeatureArrivalRadius.ToString("F2", CultureInfo.InvariantCulture);
            }

            // Feature 中心点，供 LookAt / Orbit 使用。
            if (binding.FeatureCenterCell.x >= 0 && binding.FeatureCenterCell.y >= 0)
            {
                targetMeta["targetCenterX"] = binding.FeatureCenterWorld.x.ToString("F2", CultureInfo.InvariantCulture);
                targetMeta["targetCenterZ"] = binding.FeatureCenterWorld.z.ToString("F2", CultureInfo.InvariantCulture);
            }

            // Feature 环绕半径。
            if (binding.FeatureOrbitRadius > 0f)
            {
                targetMeta["targetOrbitRadius"] = binding.FeatureOrbitRadius.ToString("F2", CultureInfo.InvariantCulture);
            }
            // Feature 占用格子数，可用于调试或更细粒度规则。
            if (binding.FeatureOccupiedCellCount > 0)
            {
                targetMeta["targetOccupiedCellCount"] = binding.FeatureOccupiedCellCount.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (binding.GridCell.x >= 0 && binding.GridCell.y >= 0)
        {
            // 如果主目标带可执行网格锚点，就一并写进参数。
            targetMeta["gridX"] = binding.GridCell.x.ToString(CultureInfo.InvariantCulture);
            targetMeta["gridZ"] = binding.GridCell.y.ToString(CultureInfo.InvariantCulture);
            targetMeta["gridTarget"] = BuildGridTargetToken(binding.GridCell);
            // 中间路径和 feature 锚点通常靠 grid 到达判断，不依赖 feature 感知完成。
            targetMeta["useWaypointToleranceOnly"] = "1";
            targetMeta["disableFeaturePerceptionArrival"] = "1";
        }
    }

    /// <summary>
    /// 当高层动作没有显式写 target 时，用当前步骤绑定好的主目标补进去。
    /// 关键点：
    /// - MoveTo / Observe / Hover 这类“接近型动作”使用可通行接近点；
    /// - Orbit 这类“围绕型动作”使用 feature 几何中心，并注入专门的环绕半径。
    /// 这样同一个建筑不会再被所有动作都当成同一个点。
    /// </summary>
    /// <param name="command">待写入目标的执行命令。</param>
    /// <param name="actionType">当前命令的原子动作类型。</param>
    /// <param name="binding">当前步骤已绑定的主目标。</param>
    /// <param name="targetMeta">要同步写入的目标元数据字典。</param>
    private void ApplyBoundTargetToCommand(ActionCommand command, PrimitiveActionType actionType, StepTargetBinding binding, Dictionary<string, string> targetMeta)
    {
        // 任一关键输入缺失时直接返回。
        if (command == null || !binding.HasTarget || targetMeta == null) return;

        // 默认使用主目标的可通行接近锚点。
        command.TargetPosition = binding.WorldPos;
        // 同步把 binding 几何元数据写入参数字典。
        AppendBoundTargetMetadata(targetMeta, binding, "StepTargetAutoFill");

        if (binding.TargetKind == "Feature" &&
            binding.FeatureCenterCell.x >= 0 &&
            binding.FeatureCenterCell.y >= 0)
        {
            if (actionType == PrimitiveActionType.Orbit || actionType == PrimitiveActionType.LookAt)
            {
                // 对 LookAt / Orbit，真正要看的不是接近点，而是 feature 几何中心。
                command.TargetPosition = binding.FeatureCenterWorld;
                // 这类动作不需要再依赖 grid 锚点元数据。
                StripGridTargetMetadata(targetMeta);
            }

            if (actionType == PrimitiveActionType.Orbit)
            {
                // Orbit 还需要补专门的环绕半径。
                targetMeta["radius"] = (binding.FeatureOrbitRadius > 0f ? binding.FeatureOrbitRadius : binding.FeatureArrivalRadius).ToString("F2", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// 对显式 feature 目标，把“中心点/锚点格”按动作类型落到真正需要的几何目标。
    /// </summary>
    /// <param name="command">待修正目标位置的命令。</param>
    /// <param name="actionType">命令对应的动作类型。</param>
    /// <param name="targetMeta">显式 feature 目标解析出的元数据。</param>
    private void ApplyResolvedFeatureGeometryToAction(ActionCommand command, PrimitiveActionType actionType, Dictionary<string, string> targetMeta)
    {
        // 无命令或无元数据时无法修正。
        if (command == null || targetMeta == null) return;

        if ((actionType == PrimitiveActionType.Orbit || actionType == PrimitiveActionType.LookAt) &&
            targetMeta.TryGetValue("targetCenterX", out string sx) &&
            targetMeta.TryGetValue("targetCenterZ", out string sz) &&
            float.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out float cx) &&
            float.TryParse(sz, NumberStyles.Float, CultureInfo.InvariantCulture, out float cz))
        {
            // LookAt / Orbit 应围绕 feature 中心，而不是锚点。
            command.TargetPosition = new Vector3(cx, command.TargetPosition.y, cz);
            // 一旦切到中心点，grid 锚点字段就不再适用。
            StripGridTargetMetadata(targetMeta);
        }

        if (actionType == PrimitiveActionType.Orbit &&
            targetMeta.TryGetValue("targetOrbitRadius", out string orbitRadius))
        {
            // Orbit 动作把 targetOrbitRadius 映射为统一的 radius 参数。
            targetMeta["radius"] = orbitRadius;
        }
    }

    private static void StripGridTargetMetadata(Dictionary<string, string> targetMeta)
    {
        if (targetMeta == null) return;

        targetMeta.Remove("gridX");
        targetMeta.Remove("gridZ");
        targetMeta.Remove("gridTarget");
        targetMeta.Remove("useWaypointToleranceOnly");
        targetMeta.Remove("disableFeaturePerceptionArrival");
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
    /// 1) 显式网格格子 grid(x,z)
    /// 2) 位置 world(x,z)/(x,z)
    /// 3) 地点（当前可见地点 + CampusGrid2D 全局地点）
    /// 4) 小节点（NodeId/DisplayName/SceneObject.name 精确匹配）
    /// 5) 队友（AgentID）
    /// </summary>
    /// <param name="target">待解析的目标文本。</param>
    /// <param name="targetObject">输出解析到的目标对象；如果目标只是一个地点坐标，则可能为 null。</param>
    /// <param name="targetPosition">输出解析到的世界坐标。</param>
    /// <param name="targetKind">输出目标类别，例如 Feature / Grid / World / SmallNode / Agent / Self。</param>
    /// <param name="meta">输出与目标相关的额外元数据，会被合并进 ActionCommand.Parameters。</param>
    /// <returns>目标文本命中白名单来源并成功解析时返回 true。</returns>
    private bool TryResolveTargetFromAllowedSources(
        string target,
        out GameObject targetObject,
        out Vector3 targetPosition,
        out string targetKind,
        out Dictionary<string, string> meta)
    {
        // 默认输出初始化为“未知目标/当前位置”。
        targetObject = null;
        targetPosition = transform.position;
        targetKind = "Unknown";
        meta = new Dictionary<string, string>();

        if (ContainsSelfReference(target))
        {
            // `self` / `me` 这类自引用目标直接落到当前位置。
            targetPosition = transform.position;
            targetKind = "Self";
            meta["targetSource"] = "SelfReference";
            return true;
        }

        if (TryResolveStepTargetReference(target, out Vector3 stepTargetPos, out string stepKind))
        {
            // 显式 step_target / mission_target 时，直接采用上游已经绑定好的主目标。
            targetPosition = stepTargetPos;
            targetKind = stepKind;
            // 关键修复：
            // 以前显式写 step_target 时，这里只把“接近锚点坐标”往下传，
            // 没把 FeatureCenter / OrbitRadius / ArrivalRadius 这些几何信息继续带下去。
            // 结果就是：
            // - MoveTo 看起来还能工作，因为它本来就只需要一个接近点；
            // - 但 Orbit 会退化成“围着某个点转”，而不是围着整栋建筑转。
            // 现在显式 step_target 和自动补全共用同一份 bound target 元数据，Orbit 才能恢复成绕整个 feature。
            AppendBoundTargetMetadata(meta, lastStepTargetBinding, "StepTargetBinding");
            return true;
        }

        if (TryResolveGridTargetWorld(target, out Vector3 gridWorld, out Vector2Int gridCell))
        {
            // 显式 grid(x,z) 目标直接转成该格中心点。
            targetPosition = gridWorld;
            targetKind = "Grid";
            meta["targetSource"] = "GridCell";
            meta["gridX"] = gridCell.x.ToString(CultureInfo.InvariantCulture);
            meta["gridZ"] = gridCell.y.ToString(CultureInfo.InvariantCulture);
            meta["gridTarget"] = BuildGridTargetToken(gridCell);
            return true;
        }

        if (TryParseWorldTarget(target, out Vector3 worldPos))
        {
            // 显式 world(x,z) / world(x,y,z) 目标。
            targetPosition = worldPos;
            targetKind = "World";
            meta["targetSource"] = "WorldCoordinate";
            return true;
        }

        if (TryResolveCampusFeatureTarget(target, out CampusGrid2D.FeatureSpatialProfile featureProfile, out string featureSource, out string featureName))
        {
            // 校园静态地点目标使用可通行接近锚点。
            targetPosition = featureProfile != null ? featureProfile.anchorWorld : transform.position;
            targetKind = "Feature";
            meta["targetSource"] = featureSource;
            if (!string.IsNullOrWhiteSpace(featureName))
            {
                meta["targetFeature"] = featureName;
            }
            // 同时补充 center / arrivalRadius / orbitRadius / grid 锚点等几何元数据。
            AppendFeatureSpatialMetadata(meta, featureProfile);
            return true;
        }

        if (TryResolveExactSmallNodeBinding(target, out SmallNodeData exactNode, out GameObject detectedObj, out Vector3 detectedPos))
        {
            // 小节点目标保留对象引用，便于执行层做跟随或交互。
            targetObject = detectedObj;
            targetPosition = detectedPos;
            targetKind = "SmallNode";
            meta["targetSource"] = "SmallNodeRegistryExact";
            if (!string.IsNullOrWhiteSpace(exactNode?.NodeId))
            {
                meta["targetNodeId"] = exactNode.NodeId;
            }
            AppendSmallNodeMetadata(meta, exactNode);
            return true;
        }

        if (TryFindNearbyAgent(target, out GameObject teammate))
        {
            // 队友 AgentID 目标。
            targetObject = teammate;
            targetPosition = teammate.transform.position;
            targetKind = "Agent";
            meta["targetSource"] = "NearbyAgent";
            meta["targetAgentId"] = target.Trim();
            return true;
        }

        // 所有白名单来源都未命中时，返回 false 交给上层做回退。
        return false;
    }

    /// <summary>
    /// 解析步骤目标锚点引用。
    /// </summary>
    /// <param name="target">待检测的目标文本。</param>
    /// <param name="worldPos">输出 step 主目标的世界坐标锚点。</param>
    /// <param name="targetKind">输出 step 主目标类别。</param>
    /// <returns>当 target 是 `step_target` / `mission_target` 等占位引用时返回 true。</returns>
    private bool TryResolveStepTargetReference(string target, out Vector3 worldPos, out string targetKind)
    {
        // 默认输出为当前位置/未知。
        worldPos = transform.position;
        targetKind = "Unknown";
        // 没有上轮绑定或目标文本为空时无法解析 step_target。
        if (!lastStepTargetBinding.HasTarget || string.IsNullOrWhiteSpace(target)) return false;

        // 标准化大小写后匹配保留占位符。
        string t = target.Trim().ToLowerInvariant();
        if (t != "step_target" && t != "anchor:step_target" && t != "mission_target" && t != "primary_target")
        {
            return false;
        }

        // 把最近一次绑定的接近锚点和类型直接返回给调用方。
        worldPos = lastStepTargetBinding.WorldPos;
        targetKind = string.IsNullOrWhiteSpace(lastStepTargetBinding.TargetKind) ? "Feature" : lastStepTargetBinding.TargetKind;
        return true;
    }

    /// <summary>
    /// 从结构化 target 字符串里剥掉“标签前缀”。
    /// 这里不再枚举任何业务单词，也不假设前缀一定是某类固定地点词。
    ///
    /// 设计原因：
    /// 虽然 PlanningModule 现在已经优先传结构化目标引用，
    /// 但执行层最终仍要拿到一个“可解析锚点字符串”。
    /// 这个字符串可能来自 targetRef.executableQuery，也可能来自兼容旧链路保留的 target / primaryTarget / sharedTarget。
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
    /// 旧字符串 grounding 兼容函数：为校园地点查询构造候选词列表。
    /// 当前两次 LLM 主路径不再依赖它；它只服务于尚未删除的旧 target 文本解析 helper。
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
        if (HasSpatialQualifier(q))
        {
            AddCandidate(StripSpatialQualifierText(q));
        }

        return result;
    }

    /// <summary>
    /// 旧字符串 grounding 兼容函数：解析 CampusGrid2D 的地点目标。
    /// 当前两次 LLM 主路径应该优先使用系统候选 + LLM 选格，不再靠这里从自然语言直接拍板最终目标。
    /// </summary>
    /// <param name="target">待解析的地点查询文本。</param>
    /// <param name="profile">输出地点的空间几何画像。</param>
    /// <param name="source">输出命中的来源标签，例如 `CampusGrid2DExact`、`PerceivedCampusFeature`。</param>
    /// <param name="normalizedName">输出规范化后的地点标识。</param>
    /// <param name="targetRef">可选结构化目标引用，可提供 grounded 锚点偏置。</param>
    /// <returns>成功解析到地点几何画像时返回 true。</returns>
    private bool TryResolveCampusFeatureTarget(string target, out CampusGrid2D.FeatureSpatialProfile profile, out string source, out string normalizedName, StructuredTargetReference targetRef = null)
    {
        // 默认输出初始化。
        profile = null;
        source = string.Empty;
        normalizedName = string.Empty;
        // 空查询直接失败。
        if (string.IsNullOrWhiteSpace(target)) return false;

        // 清理输入，并尝试剥掉类似 `feature:` 这样的标签前缀。
        string query = target.Trim();
        TryStripCampusFeaturePrefix(query, out _, out query);
        if (string.IsNullOrWhiteSpace(query)) return false;
        // 生成候选查询词，必要时移除方位词等修饰。
        List<string> candidates = BuildCampusFeatureQueryCandidates(query);
        // 确保 campusGrid 引用已同步。
        SyncCampusGridReference();
        // 根据 targetRef relation / anchorBias 决定应该从 feature 哪一侧取接近锚点。
        Vector2Int anchorBias = ResolveTargetAnchorBias(targetRef, query);

        for (int i = 0; i < candidates.Count; i++)
        {
            // 当前候选地点名称。
            string candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            normalizedName = candidate;
            // 规范 token（如 uid / alias）时尽量采用严格匹配。
            bool strictToken = IsCanonicalFeatureToken(candidate);

            // 第一层：优先查 CampusGrid2D 的静态世界索引。
            // 原因很直接：
            // 1) 大节点是静态地图实体，网格才是它们几何范围的唯一真相源；
            // 2) 只有网格层知道“这个 building 占了多少格子、中心在哪、应该如何按目标偏置选择外侧锚点”；
            // 3) 执行层后面要解决的恰恰就是“别把一整个建筑压成一个点”。
            if (campusGrid != null &&
                campusGrid.TryResolveFeatureSpatialProfile(candidate, transform.position, out profile, preferWalkableApproach: true, ignoreCase: true, anchorBias: anchorBias))
            {
                // 把 anchor/center 的高度统一到当前动作应使用的离散高度层。
                profile.anchorWorld.y = ResolveWaypointY(profile.anchorWorld, true);
                profile.centroidWorld.y = ResolveWaypointY(profile.centroidWorld, true);
                // 记录来源标签。
                source = strictToken ? "CampusGrid2DExact" : "CampusGrid2D";
                if (!string.Equals(candidate, query, System.StringComparison.OrdinalIgnoreCase))
                {
                    source += ":Normalized";
                }
                // 优先用 uid / runtimeAlias / name 作为规范名。
                if (!string.IsNullOrWhiteSpace(profile.uid))
                {
                    normalizedName = profile.uid;
                }
                else if (!string.IsNullOrWhiteSpace(profile.runtimeAlias))
                {
                    normalizedName = profile.runtimeAlias;
                }
                else if (!string.IsNullOrWhiteSpace(profile.name))
                {
                    normalizedName = profile.name;
                }
                return true;
            }

            // 第二层：再退回当前感知到的地点锚点。
            // 这是兜底层：如果地图世界索引没命中，至少还能拿到一个临时可执行点。
            if (TryResolvePerceivedCampusFeatureTarget(candidate, out Vector3 perceivedWorld, allowFuzzy: !strictToken))
            {
                profile = new CampusGrid2D.FeatureSpatialProfile
                {
                    // 感知兜底时只能用 candidate 填这些标识字段。
                    uid = candidate,
                    name = candidate,
                    runtimeAlias = candidate,
                    kind = "feature",
                    collectionKey = string.Empty,
                    cellType = CampusGrid2D.CellType.Other,
                    occupiedCellCount = 0,
                    centroidCell = new Vector2Int(-1, -1),
                    anchorCell = new Vector2Int(-1, -1),
                    centroidWorld = perceivedWorld,
                    anchorWorld = perceivedWorld,
                    footprintRadius = featureArrivalMinRadius
                };
                source = strictToken ? "PerceivedCampusFeatureExact" : "PerceivedCampusFeature";
                if (!string.Equals(candidate, query, System.StringComparison.OrdinalIgnoreCase))
                {
                    source += ":Normalized";
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把 feature 的空间几何信息写进动作参数。
    /// 这样后面的 MoveTo / Orbit / 完成判定都能区分：
    /// - 接近时应该去哪里（anchor）
    /// - 环绕时应该围着哪里转（center）
    /// - 多大距离算“已经到这个建筑边上了”（arrivalRadius）
    /// </summary>
    /// <param name="meta">待写入的目标元数据字典。</param>
    /// <param name="profile">CampusGrid2D 解析出的地点空间画像。</param>
    private void AppendFeatureSpatialMetadata(Dictionary<string, string> meta, CampusGrid2D.FeatureSpatialProfile profile)
    {
        // 缺少字典或画像时无事可做。
        if (meta == null || profile == null) return;

        // 尽可能写全 feature 的稳定标识字段。
        if (!string.IsNullOrWhiteSpace(profile.uid)) meta["targetUid"] = profile.uid;
        if (!string.IsNullOrWhiteSpace(profile.name)) meta["targetName"] = profile.name;
        if (!string.IsNullOrWhiteSpace(profile.runtimeAlias)) meta["targetAlias"] = profile.runtimeAlias;
        if (!string.IsNullOrWhiteSpace(profile.collectionKey)) meta["targetCollectionKey"] = profile.collectionKey;

        // 记录 feature 几何中心。
        meta["targetCenterX"] = profile.centroidWorld.x.ToString("F2", CultureInfo.InvariantCulture);
        meta["targetCenterZ"] = profile.centroidWorld.z.ToString("F2", CultureInfo.InvariantCulture);
        // 记录到达半径。
        meta["targetArrivalRadius"] = Mathf.Clamp(
            Mathf.Max(featureArrivalMinRadius, profile.footprintRadius * featureArrivalCellRadiusScale),
            featureArrivalMinRadius,
            Mathf.Max(featureArrivalMinRadius, featureArrivalMaxRadius)).ToString("F2", CultureInfo.InvariantCulture);
        // 记录建议环绕半径。
        meta["targetOrbitRadius"] = Mathf.Max(
            featureArrivalMinRadius,
            profile.footprintRadius + (campusGrid != null ? campusGrid.cellSize : 2f)).ToString("F2", CultureInfo.InvariantCulture);
        // 记录占用网格数量。
        meta["targetOccupiedCellCount"] = Mathf.Max(0, profile.occupiedCellCount).ToString(CultureInfo.InvariantCulture);

        if (profile.anchorCell.x >= 0 && profile.anchorCell.y >= 0)
        {
            // 有可通行锚点格时，把 grid 信息也一并下传。
            meta["gridX"] = profile.anchorCell.x.ToString(CultureInfo.InvariantCulture);
            meta["gridZ"] = profile.anchorCell.y.ToString(CultureInfo.InvariantCulture);
            meta["gridTarget"] = BuildGridTargetToken(profile.anchorCell);
            meta["useWaypointToleranceOnly"] = "1";
            meta["disableFeaturePerceptionArrival"] = "1";
        }
    }

    /// <summary>
    /// 从“当前感知到的地点”中解析目标地点位置。
    /// </summary>
    /// <param name="query">待匹配的地点名或 uid。</param>
    /// <param name="worldPos">输出匹配到的感知锚点世界坐标。</param>
    /// <param name="allowFuzzy">是否允许模糊匹配。</param>
    /// <returns>成功在当前感知列表中找到对应地点时返回 true。</returns>
    private bool TryResolvePerceivedCampusFeatureTarget(string query, out Vector3 worldPos, bool allowFuzzy = true)
    {
        // 默认先给当前位置，防止调用方拿到未初始化坐标。
        worldPos = transform.position;
        // 没有感知到地点或查询为空时直接失败。
        if (agentState?.DetectedCampusFeatures == null || string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        // best 保存最佳候选感知地点。
        CampusFeaturePerceptionData best = null;
        // 标准化查询字符串。
        string q = query.Trim();

        for (int i = 0; i < agentState.DetectedCampusFeatures.Count; i++)
        {
            // 当前感知到的地点。
            CampusFeaturePerceptionData f = agentState.DetectedCampusFeatures[i];
            if (f == null || string.IsNullOrWhiteSpace(f.FeatureName)) continue;
            // 名称精确匹配。
            bool nameExact = string.Equals(f.FeatureName, q, System.StringComparison.OrdinalIgnoreCase);
            // uid 精确匹配。
            bool uidExact = !string.IsNullOrWhiteSpace(f.FeatureUid) &&
                            string.Equals(f.FeatureUid, q, System.StringComparison.OrdinalIgnoreCase);
            if (nameExact || uidExact)
            {
                best = f;
                break;
            }

            // 不允许模糊匹配时继续下一个。
            if (!allowFuzzy) continue;

            // 名称模糊包含匹配。
            bool nameFuzzy = f.FeatureName.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                             q.IndexOf(f.FeatureName, System.StringComparison.OrdinalIgnoreCase) >= 0;
            // uid 模糊包含匹配。
            bool uidFuzzy = !string.IsNullOrWhiteSpace(f.FeatureUid) &&
                            (f.FeatureUid.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                             q.IndexOf(f.FeatureUid, System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (nameFuzzy || uidFuzzy)
            {
                // 只记录第一个模糊命中的候选，精确命中仍可覆盖它。
                if (best == null) best = f;
            }
        }

        // 最终没有任何候选时失败。
        if (best == null) return false;
        // 使用感知锚点位置。
        worldPos = best.AnchorWorldPosition;
        // 高度仍统一到执行层离散高度。
        worldPos.y = ResolveWaypointY(worldPos, true);
        return true;
    }

    /// <summary>
    /// 把地点名称映射到网格坐标。
    /// 优先精确匹配；失败后再做模糊包含匹配。
    /// </summary>
    /// <param name="featureName">待解析的地点名称或 uid。</param>
    /// <param name="cell">输出匹配到的网格坐标。</param>
    /// <returns>成功映射到网格时返回 true。</returns>
    private bool TryResolveFeatureNameToCell(string featureName, out Vector2Int cell)
    {
        // 规范 token 默认走精确模式；自然语言名称允许后续模糊回退。
        bool exactOnly = IsCanonicalFeatureToken(featureName);
        return TryResolveFeatureNameToCell(featureName, out cell, out _, exactOnly: exactOnly);
    }

    /// <summary>
    /// 把地点名称映射到网格坐标。
    /// exactOnly=true 时仅允许精确 uid/name/alias，不做模糊回退。
    /// </summary>
    /// <param name="featureName">待解析的地点名称或 uid。</param>
    /// <param name="cell">输出匹配到的网格坐标。</param>
    /// <param name="matchedToken">输出最终命中的 token。</param>
    /// <param name="exactOnly">是否只允许精确匹配。</param>
    /// <returns>成功映射到网格时返回 true。</returns>
    private bool TryResolveFeatureNameToCell(string featureName, out Vector2Int cell, out string matchedToken, bool exactOnly)
    {
        // 默认输出非法网格和空命中 token。
        cell = new Vector2Int(-1, -1);
        matchedToken = string.Empty;
        // 没网格或名称为空时失败。
        if (campusGrid == null || string.IsNullOrWhiteSpace(featureName)) return false;
        // 标准化查询字符串。
        string q = featureName.Trim();

        // 先尝试按 uid 精确查首个网格。
        if (campusGrid.TryGetFeatureFirstCellByUid(q, out cell, preferWalkable: true, ignoreCase: true) ||
            campusGrid.TryGetFeatureFirstCellByUid(q, out cell, preferWalkable: false, ignoreCase: true))
        {
            matchedToken = q;
            return true;
        }

        if (campusGrid.TryGetFeatureFirstCell(q, out cell, preferWalkable: true, ignoreCase: true) ||
            campusGrid.TryGetFeatureFirstCell(q, out cell, preferWalkable: false, ignoreCase: true))
        {
            // 命中了静态名称索引。
            matchedToken = q;
            return true;
        }

        if (campusGrid.TryResolveFeatureAliasCell(q, out cell, out string aliasUid, out string aliasName, preferWalkable: true, ignoreCase: true) ||
            campusGrid.TryResolveFeatureAliasCell(q, out cell, out aliasUid, out aliasName, preferWalkable: false, ignoreCase: true))
        {
            // 命中了 runtime alias，则优先返回 alias 对应 uid/name。
            matchedToken = !string.IsNullOrWhiteSpace(aliasUid) ? aliasUid : (aliasName ?? q);
            return true;
        }

        // 精确模式到这里就停止，不再做模糊包含。
        if (exactOnly) return false;

        string matchedUid;
        string matchedName;
        // 最后退回 CampusGrid2D 的模糊查询。
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
            // 记录模糊命中的规范 token。
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

    /// <summary>
    /// 规范化并写入动作参数文本。
    /// </summary>
    /// <param name="command">待写入参数的命令。</param>
    /// <param name="parameters">原始参数字符串。</param>
    private void ParseActionParameters(ActionCommand command, string parameters)
    {
        // 当前阶段不拆解业务含义，只做统一格式清洗。
        command.Parameters = NormalizeActionParameters(parameters);
    }

    /// <summary>
    /// 逐条执行当前 step 的 ActionCommand 序列。
    /// </summary>
    /// <param name="actions">已经完成目标绑定和路径展开的执行命令序列。</param>
    /// <param name="currentStep">当前步骤文本，用于日志、记忆和反思模块记录。</param>
    private IEnumerator ExecuteActionSequence(List<ActionCommand> actions, string currentStep)
    {
        // 日志优先显示显式 AgentID，没有则回退 GameObject 名称。
        string agentId = agentProperties != null ? agentProperties.AgentID : gameObject.name;
        // 打印当前步骤和动作序列信息。
        Debug.Log($"=== 开始执行步骤 [{agentId}]: {currentStep} ===");
        Debug.Log($"步骤描述[{agentId}]: {currentStep}");
        Debug.Log($"动作序列数量[{agentId}]: {actions.Count}");
        
        for (int i = 0; i < actions.Count; i++)
        {
            // 当前待执行动作，仅用于预执行日志。
            var action = actions[i];
            Debug.Log($"动作[{agentId}] {i + 1}/{actions.Count}: {action.ActionType}, 目标: {action.TargetObject?.name ?? action.TargetPosition.ToString()}, 参数: {action.Parameters}");
        }
        Debug.Log($"=== 步骤信息结束 [{agentId}] ===");

        for (int i = 0; i < actions.Count; i++)
        {
            // 当前真正准备执行的动作。
            ActionCommand action = actions[i];
            // 设置步骤上下文，便于执行层、日志层知道这是序列中的第几条动作。
            var stepInfo = new Dictionary<string, string>
            {
                ["currentStep"] = currentStep,
                ["sequenceIndex"] = i.ToString(),
                ["totalActions"] = actions.Count.ToString()
            };
            action.Parameters = MergeParameters(action.Parameters, stepInfo);
            
            // 以下三个变量由回调填充，表示动作是否完成、是否成功、以及结果文本。
            bool actionCompleted = false;
            bool actionSuccess = false;
            string actionResult = "";
            
            // 特殊处理通信动作。
            if (action.ActionType == PrimitiveActionType.TransmitMessage)
            {
                // 通信动作不通过 MLAgentsController，直接在本模块里执行。
                ExecuteCommunicationAction(action);
                actionCompleted = true;
                actionSuccess = true;
                actionResult = "消息发送完成";
            }
            else
            {
                // 执行其他动作。
                if (mlAgentsController != null)
                {
                    // 交给 MLAgentsController 执行，并在回调里回填执行结果。
                    mlAgentsController.SetCurrentCommand(action, (type, success, result) => {
                        actionCompleted = true;
                        actionSuccess = success;
                        actionResult = result;
                    });
                }
                else
                {
                    // 缺少执行控制器时必须立即失败，不能让协程一直卡在 WaitUntil。
                    actionCompleted = true;
                    actionSuccess = false;
                    actionResult = "缺少 MLAgentsController，无法执行动作";
                }
            }
            
            // 等待动作完成。
            yield return new WaitUntil(() => actionCompleted);
            
            // 提取当前任务上下文，用于写记忆和反思模块。
            string missionId = planningModule != null && planningModule.currentMission != null
                ? planningModule.currentMission.missionId
                : string.Empty;
            string missionText = planningModule != null && planningModule.currentMission != null
                ? planningModule.currentMission.missionDescription
                : string.Empty;
            string slotId = planningModule != null && planningModule.currentPlan != null && planningModule.currentPlan.assignedSlot != null
                ? planningModule.currentPlan.assignedSlot.slotId
                : string.Empty;
            string targetRef = lastStepTargetBinding.HasTarget ? lastStepTargetBinding.RawQuery : string.Empty;
            string executionSummary = $"执行步骤'{currentStep}'-动作{i}:{action.ActionType} result={actionResult}";

            if (memoryModule != null)
            {
                // 记录动作执行记忆，供后续经验总结使用。
                memoryModule.RememberActionExecution(
                    missionId,
                    slotId,
                    currentStep,
                    action.ActionType.ToString(),
                    targetRef,
                    actionSuccess,
                    executionSummary);
            }

            if (reflectionModule != null)
            {
                // 同步把结果通知反思模块。
                reflectionModule.NotifyActionOutcome(
                    missionId,
                    missionText,
                    slotId,
                    currentStep,
                    targetRef,
                    actionSuccess,
                    executionSummary);
            }

            if (!actionSuccess)
            {
                // 动作失败，停止序列并重新决策。
                Debug.Log($"动作执行失败，重新决策: {actionResult}");
                StartCoroutine(DecideNextAction());
                yield break;
            }

            // 动作成功后，刷新一次“当前路径已经满足了哪些 viaTargets”。
            UpdateRollingRouteProgressForCurrentPosition(
                currentStep,
                planningModule != null ? planningModule.GetCurrentStepIntent() : null);
            
            // 短暂延迟 between actions，避免紧贴着下发下一条命令。
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
    /// <param name="currentStep">当前步骤文本。</param>
    /// <returns>若当前 step 仍应保持不完成、继续重规划，则返回 true。</returns>
    private bool ShouldContinueCurrentStepWithoutCompletion(string currentStep)
    {
        // 空步骤文本不触发额外保持逻辑。
        if (string.IsNullOrWhiteSpace(currentStep)) return false;
        // 本轮动作序列如果根本不含移动，就不需要做“还没到目标”的重规划判定。
        if (!lastIssuedSequenceContainsMovement) return false;

        // 先按当前步骤文本本身做一次环绕兜底。
        // 某些计划是从 planSteps 推导 stepIntents，运行时如果结构化意图被规整掉了 ring 标记，
        // 这里只要自然语言步骤明确写了“围绕/环绕/绕一圈”，也应视为环绕步。
        // 否则会出现：环绕动作序列明明执行完了，却又被当成普通接近步继续滚动重规划。
        if (IsRingLikeInstructionText(currentStep))
        {
            return false;
        }

        // 取当前 step intent，判断是否是特殊的环绕类步骤。
        StepIntentDefinition currentIntent = planningModule != null ? planningModule.GetCurrentStepIntent() : null;
        if (ShouldBuildRingPathForStep(currentIntent))
        {
            // 环绕步骤的成功完成标准是“整段闭环动作序列执行完成”，
            // 而不是最后还必须回到建筑接近锚点附近。
            // 否则会出现：已经沿外围绕了一圈，但因为停在建筑另一侧，又被判成未完成继续重规划。
            return false;
        }

        // 没有主目标绑定时，不做未完成重规划。
        if (!lastStepTargetBinding.HasTarget) return false;
        // 对动态目标（队友/小节点）也要做接近性判定，否则会出现“动作刚执行完就错误推进 step”的问题。
        lastStepTargetBinding = RefreshStepTargetBindingForCompletion(lastStepTargetBinding);

        if (lastStepTargetBinding.TargetKind == "Feature")
        {
            // 对建筑/区域目标，不能只盯着单个 anchor 点。
            // 当前步骤可能已经通过另一侧接近到目标外围，或已在感知里稳定看到该 feature；
            // 这时应允许 step 完成并推进到后续“观察/环绕”步骤，而不是继续在建筑两侧折返重规划。
            if (IsNearStepTarget(lastStepTargetBinding)) return false;
            if (IsWithinFeatureEnvelope(lastStepTargetBinding)) return false;
            if (IsFeatureTargetSatisfiedByPerception(lastStepTargetBinding)) return false;
            return true;
        }

        // 非 feature 目标则只看是否已经靠近。
        return !IsNearStepTarget(lastStepTargetBinding);
    }

    /// <summary>
    /// 在 step 完成判定前刷新一次目标绑定。
    /// 对静态 Feature/World 不需要刷新；
    /// 对 Agent/SmallNode 这类可能移动或感知更新的目标，优先按原始结构化 target 再解析一遍。
    /// </summary>
    /// <param name="binding">上一次记录的目标绑定。</param>
    /// <returns>必要时刷新后的目标绑定。</returns>
    private StepTargetBinding RefreshStepTargetBindingForCompletion(StepTargetBinding binding)
    {
        // 无目标时无需刷新。
        if (!binding.HasTarget) return binding;
        // 没有原始查询文本也无法重新解析。
        if (string.IsNullOrWhiteSpace(binding.RawQuery)) return binding;

        if (binding.TargetKind == "Agent" || binding.TargetKind == "SmallNode")
        {
            // 动态对象在完成判定前重新解析一次，避免用旧位置做判断。
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
    /// <param name="target">待判断的步骤目标绑定。</param>
    /// <returns>当前智能体是否已经靠近该目标。</returns>
    private bool IsNearStepTarget(StepTargetBinding target)
    {
        // 没目标等价于已经满足。
        if (!target.HasTarget) return true;
        // 当前自身位置。
        Vector3 cur = transform.position;
        // 目标锚点位置。
        Vector3 goal = target.WorldPos;

        // 先看水平距离。
        float planar = Vector2.Distance(new Vector2(cur.x, cur.z), new Vector2(goal.x, goal.z));
        float reachDistance = Mathf.Max(0.5f, stepTargetReachDistance);
        if (target.TargetKind == "Feature" && TryGetFeatureArrivalRadius(target, out float featureArrivalRadius))
        {
            // Feature 目标使用更合理的建筑到达半径。
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

    /// <summary>
    /// 判断当前位置是否已经进入 feature 的外围包络范围。
    /// </summary>
    /// <param name="target">feature 类型目标绑定。</param>
    /// <returns>当前是否已在 feature 包络范围内。</returns>
    private bool IsWithinFeatureEnvelope(StepTargetBinding target)
    {
        // 只对 feature 目标有意义。
        if (!target.HasTarget || target.TargetKind != "Feature") return false;

        // 先取 feature 几何中心；如果没有则退回接近锚点。
        Vector3 center = target.FeatureCenterWorld;
        if (center == Vector3.zero)
        {
            center = target.WorldPos;
        }

        // 包络半径优先用 orbitRadius，没有则退回 arrivalRadius。
        float envelopeRadius = target.FeatureOrbitRadius;
        if (envelopeRadius <= 0f && TryGetFeatureArrivalRadius(target, out float featureArrivalRadius))
        {
            envelopeRadius = featureArrivalRadius;
        }
        envelopeRadius = Mathf.Max(Mathf.Max(0.5f, stepTargetReachDistance), envelopeRadius);

        // 当前位置到中心点的平面距离在包络半径内，即视为已到建筑外围。
        float planar = Vector2.Distance(
            new Vector2(transform.position.x, transform.position.z),
            new Vector2(center.x, center.z));
        return planar <= envelopeRadius;
    }

    /// <summary>
    /// 判断 feature 目标是否已经被当前感知稳定满足。
    /// </summary>
    /// <param name="target">feature 类型步骤目标。</param>
    /// <returns>如果感知中已经稳定看到该 feature 且距离足够近，则返回 true。</returns>
    private bool IsFeatureTargetSatisfiedByPerception(StepTargetBinding target)
    {
        // 只有 feature 目标才适用这条规则。
        if (!target.HasTarget || target.TargetKind != "Feature") return false;
        // 当前没有任何地点感知时，无法通过感知满足。
        if (agentState?.DetectedCampusFeatures == null || agentState.DetectedCampusFeatures.Count == 0) return false;

        // 先取 feature 的估计到达半径。
        float featureArrivalRadius = stepTargetReachDistance;
        if (TryGetFeatureArrivalRadius(target, out float estimatedRadius))
        {
            featureArrivalRadius = estimatedRadius;
        }
        // 感知判定半径可以比纯锚点到达半径稍宽一点。
        float perceptionReach = Mathf.Max(featureArrivalRadius, Mathf.Max(stepTargetReachDistance, 12f));

        for (int i = 0; i < agentState.DetectedCampusFeatures.Count; i++)
        {
            // 当前感知到的地点。
            CampusFeaturePerceptionData data = agentState.DetectedCampusFeatures[i];
            if (data == null) continue;

            // 用 uid / name / 原始查询三套键做匹配。
            bool matched =
                (!string.IsNullOrWhiteSpace(target.Uid) &&
                 string.Equals(data.FeatureUid, target.Uid, System.StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.Name) &&
                 string.Equals(data.FeatureName, target.Name, System.StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.RawQuery) &&
                 (string.Equals(data.FeatureName, target.RawQuery, System.StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(data.FeatureUid, target.RawQuery, System.StringComparison.OrdinalIgnoreCase)));
            if (!matched) continue;

            // 只有匹配到的地点距离当前智能体也足够近时，才认为 feature 已满足。
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

    /// <summary>
    /// 估算 feature 类型步骤目标的到达半径。
    /// </summary>
    /// <param name="target">当前步骤目标绑定。</param>
    /// <param name="radius">输出估算出的到达半径。</param>
    /// <returns>成功得到有效半径时返回 true。</returns>
    private bool TryGetFeatureArrivalRadius(StepTargetBinding target, out float radius)
    {
        // 默认输出 0，表示当前还没有有效半径。
        radius = 0f;
        // 只有 feature 目标且存在网格系统时才可估算。
        if (!target.HasTarget || target.TargetKind != "Feature" || campusGrid == null) return false;

        // binding 自带的 FeatureArrivalRadius 优先级最高。
        if (target.FeatureArrivalRadius > 0f)
        {
            radius = target.FeatureArrivalRadius;
            return true;
        }

        // 否则按 uid -> name -> RawQuery 的顺序挑一个 token 去反查。
        string featureToken = !string.IsNullOrWhiteSpace(target.Uid)
            ? target.Uid
            : (!string.IsNullOrWhiteSpace(target.Name) ? target.Name : target.RawQuery);
        if (string.IsNullOrWhiteSpace(featureToken)) return false;

        return TryEstimateFeatureArrivalRadius(featureToken, out radius);
    }

    /// <summary>
    /// 从显式解析出的 target 元数据中恢复 feature 到达半径。
    /// </summary>
    /// <param name="resolvedMeta">目标解析时生成的元数据字典。</param>
    /// <param name="resolvedPos">目标位置，用于必要时按世界坐标反查 feature。</param>
    /// <param name="radius">输出估算出的到达半径。</param>
    /// <returns>成功恢复到达半径时返回 true。</returns>
    private bool TryGetFeatureArrivalRadiusByResolvedMeta(Dictionary<string, string> resolvedMeta, Vector3 resolvedPos, out float radius)
    {
        // 默认输出 0。
        radius = 0f;
        // 没元数据就没法恢复。
        if (resolvedMeta == null) return false;

        // 如果参数里已经带了 targetArrivalRadius，直接复用。
        if (resolvedMeta.TryGetValue("targetArrivalRadius", out string existingRadius) &&
            float.TryParse(existingRadius, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedRadius))
        {
            radius = parsedRadius;
            return true;
        }

        // 否则优先按 targetFeature 反查。
        if (resolvedMeta.TryGetValue("targetFeature", out string featureToken) &&
            !string.IsNullOrWhiteSpace(featureToken) &&
            TryEstimateFeatureArrivalRadius(featureToken, out radius))
        {
            return true;
        }

        // 再退回按 targetUid 反查。
        if (resolvedMeta.TryGetValue("targetUid", out string featureUid) &&
            !string.IsNullOrWhiteSpace(featureUid) &&
            TryEstimateFeatureArrivalRadius(featureUid, out radius))
        {
            return true;
        }

        // 还不行就按世界坐标查询该位置所属 feature，再估算其半径。
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

    /// <summary>
    /// 通过 feature token 反查 CampusGrid2D，估算一个合理的到达半径。
    /// </summary>
    /// <param name="featureToken">feature 的 uid / name / alias。</param>
    /// <param name="radius">输出估算出的到达半径。</param>
    /// <returns>只要给出一个可用半径就返回 true；查不到时会退回最小半径。</returns>
    private bool TryEstimateFeatureArrivalRadius(string featureToken, out float radius)
    {
        // 默认输出 0。
        radius = 0f;
        // 没网格或 token 为空时无法估算。
        if (campusGrid == null || string.IsNullOrWhiteSpace(featureToken)) return false;

        if (campusGrid.TryResolveFeatureSpatialProfile(featureToken, transform.position, out CampusGrid2D.FeatureSpatialProfile profile, preferWalkableApproach: true, ignoreCase: true) &&
            profile != null)
        {
            // 按 footprintRadius * scale 估算，并钳制在最小/最大半径范围内。
            float clampedMax = Mathf.Max(featureArrivalMinRadius, featureArrivalMaxRadius);
            radius = Mathf.Clamp(
                Mathf.Max(featureArrivalMinRadius, profile.footprintRadius * featureArrivalCellRadiusScale),
                featureArrivalMinRadius,
                clampedMax);
            return true;
        }

        // 查不到 profile 时仍给一个最小可用半径，避免 feature 永远不可达。
        radius = featureArrivalMinRadius;
        return true;
    }

    /// <summary>
    /// 直接执行通信动作，不经过 MLAgentsController。
    /// </summary>
    /// <param name="action">通信命令，参数中可包含 `messageType` 和 `content`。</param>
    private void ExecuteCommunicationAction(ActionCommand action)
    {
        try
        {
            // 从参数中解析消息类型和内容。
            MessageType messageType = MessageType.StatusUpdate;
            string content = "";
            
            if (!string.IsNullOrEmpty(action.Parameters))
            {
                // 先把参数文本解析成松散字典。
                Dictionary<string, string> pmap = ParseLooseParameterMap(action.Parameters);
                if (pmap.TryGetValue("messageType", out string msgTypeRaw))
                {
                    // 按枚举名解析消息类型；失败时保持默认 StatusUpdate。
                    System.Enum.TryParse<MessageType>(msgTypeRaw, true, out messageType);
                }
                if (pmap.TryGetValue("content", out string c))
                {
                    // 读取消息正文。
                    content = c;
                }
            }
            
            // 如果内容为空，生成默认内容。
            if (string.IsNullOrEmpty(content))
            {
                content = $"执行{action.ActionType}动作，步骤:{GetCurrentStepDescription()}";
            }
            
            // 发送消息。
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

    /// <summary>
    /// 根据消息类型映射通信优先级。
    /// </summary>
    /// <param name="messageType">待映射的消息类型。</param>
    /// <returns>整数优先级，数值越大优先级越高。</returns>
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

    /// <summary>
    /// 把一组新参数合并到已有参数文本中。
    /// </summary>
    /// <param name="existingParams">现有参数字符串。</param>
    /// <param name="newParams">待覆盖/追加的新参数字典。</param>
    /// <returns>合并后的 JSON 风格参数字符串。</returns>
    private string MergeParameters(string existingParams, Dictionary<string, string> newParams)
    {
        // 先把现有参数解析成松散映射。
        Dictionary<string, string> merged = ParseLooseParameterMap(existingParams);
        // 新参数覆盖旧参数的同名键。
        foreach (var kv in newParams) merged[kv.Key] = kv.Value;
        // 再统一序列化回字符串。
        return BuildJsonFromParameterMap(merged);
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
        public string targetRef;
        public string parameters;
        public string reason;
    }
}
