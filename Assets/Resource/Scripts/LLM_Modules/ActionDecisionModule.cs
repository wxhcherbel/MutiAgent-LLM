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
    /// 用于把“全局粗路径”喂给 LLM，让 LLM 在此基础上结合局部障碍细化 waypoint。
    /// </summary>
    private struct CoarsePathContext
    {
        public bool HasPath;                  // 是否成功生成粗路径
        public string TargetFeatureName;      // 推断出的目标地点名（如 building_5）
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

    /// <summary>
    /// 基于系统已绑定的目标预构建 A* 粗路径（若可行）。
    /// 规则：
    /// 1) 仅在该 step 是移动型时尝试；
    /// 2) 仅对适合全局寻路的目标（地点/坐标/静态节点）构建；
    /// 3) 动态小节点和队友目标默认不预建粗路径，交给第二阶段局部滚动决策。
    /// </summary>
    private CoarsePathContext BuildCoarsePathContextForStep(string currentStep, StepNavigationDecision navDecision, StepTargetBinding stepTarget)
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
        if (campusGrid == null || campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null)
        {
            ctx.Summary = "CampusGrid2D 不可用";
            return ctx;
        }

        string featureName = string.Empty;
        string goalSource = string.Empty;
        Vector2Int goalCell = new Vector2Int(-1, -1);

        if (stepTarget.HasTarget && !ShouldBuildCoarsePathForTarget(stepTarget))
        {
            ctx.Summary = $"目标类型={stepTarget.TargetKind}，本轮不构建A*粗路径，交由局部滚动决策";
            return ctx;
        }

        if (stepTarget.HasTarget && ShouldBuildCoarsePathForTarget(stepTarget))
        {
            if (TryResolveWalkableCell(stepTarget.WorldPos, out goalCell))
            {
                featureName = !string.IsNullOrWhiteSpace(stepTarget.Uid)
                    ? stepTarget.Uid
                    : (!string.IsNullOrWhiteSpace(stepTarget.Name) ? stepTarget.Name : stepTarget.RawQuery);
                goalSource = $"StepTarget:{stepTarget.Source}";
            }
        }

        if (goalCell.x < 0 &&
            !TryResolveGoalCellForCoarsePath(currentStep, out featureName, out goalCell, out goalSource))
        {
            ctx.Summary = "未在感知库/CampusGrid中解析到目标，无法预构建粗路径";
            return ctx;
        }

        if (!TryResolveWalkableCell(transform.position, out Vector2Int startCell))
        {
            ctx.Summary = "起点不可用";
            return ctx;
        }

        // 目标地点可能是建筑内部阻塞格：先映射到最近可通行格，再做粗路径。
        if (!campusGrid.IsWalkable(goalCell.x, goalCell.y))
        {
            if (!campusGrid.TryFindNearestWalkable(goalCell, 10, out Vector2Int nearGoal))
            {
                ctx.Summary = $"目标地点不可达: {featureName}";
                return ctx;
            }
            goalCell = nearGoal;
        }

        List<Vector2Int> gridPath = campusGrid.FindPathAStar(startCell, goalCell);
        if (gridPath == null || gridPath.Count < 2)
        {
            ctx.Summary = $"A*失败: {featureName}";
            return ctx;
        }

        float y = ResolveWaypointY(transform.position);
        int stride = Mathf.Max(1, astarWaypointStride);
        List<Vector3> waypoints = new List<Vector3>();

        // 关键：粗路径第一个点固定为无人机当前位置，保证可视化从机体起始。
        Vector3 startWorld = transform.position;
        startWorld.y = y;
        waypoints.Add(startWorld);

        for (int i = stride; i < gridPath.Count; i += stride)
        {
            Vector2Int c = gridPath[i];
            Vector3 wp = campusGrid.GridToWorldCenter(c.x, c.y, astarWaypointYOffset);
            wp.y = y;
            waypoints.Add(wp);
        }

        Vector3 final = campusGrid.GridToWorldCenter(goalCell.x, goalCell.y, astarWaypointYOffset);
        final.y = y;
        if (waypoints.Count == 0 || Vector3.Distance(waypoints[waypoints.Count - 1], final) > Mathf.Max(0.5f, campusGrid.cellSize * 0.5f))
        {
            waypoints.Add(final);
        }

        int maxCount = Mathf.Max(2, astarMaxWaypoints);
        if (waypoints.Count > maxCount)
        {
            waypoints = DownsampleWaypoints(waypoints, maxCount);
        }

        ctx.HasPath = waypoints.Count > 0;
        ctx.TargetFeatureName = featureName;
        ctx.GoalWorld = final;
        ctx.CoarseWaypoints = waypoints;
        ctx.WaypointsInline = BuildWaypointInlineString(waypoints);
        ctx.Summary = ctx.HasPath
            ? $"target={featureName},source={goalSource},avoid=CampusGridBlocked,gridPath={gridPath.Count},coarseWp={waypoints.Count},waypoints={ctx.WaypointsInline}"
            : $"A*失败: {featureName}";

        if (ctx.HasPath)
        {
            UpdateCoarsePathVisualization(waypoints);
        }
        return ctx;
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
    /// 为粗路径构建解析目标网格：
    /// 优先使用已感知目标（地点/小节点），失败后再回退 CampusGrid2D 全局地点解析。
    /// </summary>
    private bool TryResolveGoalCellForCoarsePath(string stepText, out string targetName, out Vector2Int goalCell, out string source)
    {
        targetName = string.Empty;
        goalCell = new Vector2Int(-1, -1);
        source = string.Empty;

        List<string> queries = BuildTargetQueryCandidates(stepText);
        if (queries.Count == 0) return false;

        for (int i = 0; i < queries.Count; i++)
        {
            string q = queries[i];
            if (string.IsNullOrWhiteSpace(q)) continue;

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

            if (TryResolveCampusSceneObjectExact(q, out Vector3 sceneObjPos) &&
                TryResolveWalkableCell(sceneObjPos, out goalCell))
            {
                targetName = q;
                source = "CampusSceneObjectExact";
                return true;
            }

            if (TryResolveFeatureNameToCell(q, out goalCell))
            {
                targetName = q;
                source = "CampusGrid";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 构建目标查询候选：当前 step + mission 文本中的显式目标词。
    /// </summary>
    private List<string> BuildTargetQueryCandidates(string stepText)
    {
        HashSet<string> queries = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        if (ContainsSelfReference(stepText))
        {
            queries.Add("self");
        }

        AddTargetQueriesFromText(stepText, queries);

        return queries.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
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
    /// 从文本里提取显式目标词（building_x / feature:xxx / 场景已知地点名）。
    /// </summary>
    private void AddTargetQueriesFromText(string text, HashSet<string> output)
    {
        if (string.IsNullOrWhiteSpace(text) || output == null) return;
        string lower = text.ToLowerInvariant();

        if (TryExtractFeatureNameFromStep(text, out string explicitFeature) && !string.IsNullOrWhiteSpace(explicitFeature))
        {
            output.Add(explicitFeature.Trim());
        }

        System.Text.RegularExpressions.MatchCollection buildingMatches = System.Text.RegularExpressions.Regex.Matches(
            lower,
            @"(?:building|楼|建筑)\s*[_\-:：]?\s*(\d+)"
        );
        for (int i = 0; i < buildingMatches.Count; i++)
        {
            string id = buildingMatches[i].Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(id))
            {
                output.Add($"building_{id}");
            }
        }

        System.Text.RegularExpressions.MatchCollection prefixed = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"(?:feature|building)\s*[:：]\s*([^\s,，。;；]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        for (int i = 0; i < prefixed.Count; i++)
        {
            string token = prefixed[i].Groups[1].Value?.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                output.Add(token);
            }
        }
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
    /// 从当前 step 文本中提取地点名（优先匹配 CampusGrid2D 中已知地点）。
    /// </summary>
    private bool TryExtractFeatureNameFromStep(string stepText, out string featureName)
    {
        featureName = string.Empty;
        if (campusGrid == null || string.IsNullOrWhiteSpace(stepText)) return false;

        string text = stepText.Trim();

        // 1) 优先提取前缀目标（building:xxx / feature:xxx）
        System.Text.RegularExpressions.Match prefixed = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:feature|building)\s*[:：]\s*([^\s,，。;；]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (prefixed.Success)
        {
            string token = prefixed.Groups[1].Value?.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                featureName = token;
                return true;
            }
        }

        // 2) 常见建筑编号表达（building_5 / building-5 / 楼5 / 建筑5）
        System.Text.RegularExpressions.Match buildingId = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:building|楼|建筑)\s*[_\-:：]?\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (buildingId.Success)
        {
            string id = buildingId.Groups[1].Value?.Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                featureName = $"building_{id}";
                return true;
            }
        }

        if (campusGrid.cellFeatureNameGrid == null) return false;

        HashSet<string> names = new HashSet<string>();
        for (int x = 0; x < campusGrid.gridWidth; x++)
        {
            for (int z = 0; z < campusGrid.gridLength; z++)
            {
                string n = campusGrid.cellFeatureNameGrid[x, z];
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }
        }

        if (names.Count == 0) return false;

        string lowerStep = stepText.ToLowerInvariant();
        foreach (string n in names)
        {
            if (lowerStep.Contains(n.ToLowerInvariant()))
            {
                featureName = n;
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
    /// 两阶段决策主入口：
    /// 1) 第一阶段让 LLM 解析 step 的高层动作与 target；
    /// 2) 系统把 target 绑定到确定坐标并生成粗路径；
    /// 3) 第二阶段只让 LLM 细化当前这一小段局部 MoveTo；
    /// 4) 执行后若未到最终目标，则保留在当前 step 继续滚动重规划。
    /// </summary>
    public IEnumerator<object> DecideNextAction()
    {
        SyncCampusGridReference();
        lastIssuedSequenceContainsMovement = false;
        string currentStep = GetCurrentStepDescription();
        if (currentStep == "无活跃任务" || currentStep == "任务已完成")
        {
            yield break;
        }
        StepNavigationDecision navDecision = BuildStepNavigationDecision(currentStep);
        List<ActionData> highLevelActionData;
        bool reusedHighLevelDecision = TryGetCachedHighLevelDecision(currentStep, out highLevelActionData, out lastStepTargetBinding);
        if (!reusedHighLevelDecision)
        {
            string highLevelPrompt = BuildHighLevelDecisionPrompt(currentStep, navDecision);
            string highLevelResponse = string.Empty;
            yield return llmInterface.SendRequest(highLevelPrompt, result =>
            {
                highLevelResponse = result ?? string.Empty;
            }, temperature: 0.1f, maxTokens: 420);

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

            lastStepTargetBinding = ResolveStepTargetBindingFromActionData(highLevelActionData, currentStep);
            UpdateHighLevelDecisionCache(currentStep, highLevelActionData, lastStepTargetBinding);
        }

        ApplyHighLevelIntentToNavigationDecision(ref navDecision, highLevelActionData, lastStepTargetBinding);
        Debug.Log($"[ActionDecision][Stage1] step={currentStep} reused={(reusedHighLevelDecision ? 1 : 0)} target={(lastStepTargetBinding.HasTarget ? lastStepTargetBinding.Summary : "none")} nav={BuildNavigationPromptSummary(navDecision)}");

        CoarsePathContext coarsePath = BuildCoarsePathContextForStep(currentStep, navDecision, lastStepTargetBinding);
        List<ActionCommand> actionSequence = BuildActionCommandsFromActionData(highLevelActionData);
        bool deterministicStaticNavigation = ShouldPreferDeterministicStaticNavigation(lastStepTargetBinding, coarsePath);

        if (!deterministicStaticNavigation && ShouldRequestWaypointRefinement(actionSequence, lastStepTargetBinding))
        {
            string waypointPrompt = BuildWaypointRefinementPrompt(currentStep, navDecision, lastStepTargetBinding, coarsePath, highLevelActionData);
            string waypointResponse = string.Empty;
            yield return llmInterface.SendRequest(waypointPrompt, result =>
            {
                waypointResponse = result ?? string.Empty;
            }, temperature: 0.1f, maxTokens: 320);

            if (!string.IsNullOrWhiteSpace(waypointResponse))
            {
                List<ActionData> waypointActionData = ParseActionDataSequenceFromJSON(waypointResponse);
                if (waypointActionData != null && waypointActionData.Count > 0)
                {
                    List<ActionCommand> refinedMovement = BuildActionCommandsFromActionData(waypointActionData);
                    actionSequence = MergeRefinedMovementActions(actionSequence, refinedMovement);
                    Debug.Log($"[ActionDecision][Stage2] refinedMoveCount={refinedMovement.Count} coarsePath={(coarsePath.HasPath ? coarsePath.Summary : "none")}");
                }
            }
        }
        else if (deterministicStaticNavigation)
        {
            Debug.Log($"[ActionDecision] step={currentStep} 使用系统静态导航快速路径，跳过第二阶段 waypoint 细化");
        }

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
    /// 当第一阶段 LLM 没有给出可解析的 target 时，
    /// 回退到 step/mission 文本做 deterministic 目标绑定。
    /// </summary>
    private StepTargetBinding ResolveStepTargetBinding(string currentStep)
    {
        StepTargetBinding binding = new StepTargetBinding
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

        if (string.IsNullOrWhiteSpace(currentStep))
        {
            return binding;
        }

        if (ContainsSelfReference(currentStep))
        {
            return BuildSelfTargetBinding(currentStep, "StepText:SelfReference");
        }

        List<string> queries = BuildTargetQueryCandidates(currentStep);
        if (queries.Count == 0)
        {
            binding.Summary = "none (未从step提取到显式目标词)";
            return binding;
        }

        for (int i = 0; i < queries.Count; i++)
        {
            string q = queries[i];
            if (string.IsNullOrWhiteSpace(q)) continue;

            if (TryResolveCampusFeatureTarget(q, out Vector3 featurePos, out string featureSource, out string normalized))
            {
                binding.HasTarget = true;
                binding.TargetKind = "Feature";
                binding.RawQuery = q;
                binding.Source = featureSource;
                binding.WorldPos = featurePos;
                binding.Name = normalized ?? string.Empty;

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

                if (string.IsNullOrWhiteSpace(binding.Uid)) binding.Uid = normalized ?? string.Empty;
                binding.Summary =
                    $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},uid={binding.Uid},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
                return binding;
            }

            if (TryResolveCampusSceneObjectExact(q, out Vector3 scenePos))
            {
                binding.HasTarget = true;
                binding.TargetKind = "Feature";
                binding.RawQuery = q;
                binding.Source = "CampusSceneObjectExact";
                binding.WorldPos = scenePos;
                binding.Name = q;
                binding.Uid = q;
                if (TryResolveWalkableCell(scenePos, out Vector2Int c))
                {
                    binding.GridCell = c;
                }
                binding.Summary =
                    $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},uid={binding.Uid},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
                return binding;
            }

            if (TryParseWorldTarget(q, out Vector3 worldPos))
            {
                binding.HasTarget = true;
                binding.TargetKind = "World";
                binding.RawQuery = q;
                binding.Source = "WorldCoordinate";
                binding.WorldPos = worldPos;
                if (TryResolveWalkableCell(worldPos, out Vector2Int c))
                {
                    binding.GridCell = c;
                }
                binding.Summary =
                    $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
                return binding;
            }
        }

        binding.Summary = $"none (queries={string.Join(",", queries.Take(4))})";
        return binding;
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

            // 第一优先级（可选）：LLM 显式给 waypoint
            if (preferLlmWaypoints && !disableLlmLongWaypoints &&
                TryBuildLlmWaypointsFromParameters(action, out List<Vector3> llmWaypoints))
            {
                AppendWaypointsAsMoveActions(action, llmWaypoints, expanded, "LLMPath");
                virtualStart = llmWaypoints[llmWaypoints.Count - 1];
                expandedAny = true;
                continue;
            }

            // 第二优先级：A* 兜底
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
            Debug.Log($"[ActionDecision] 路径点展开完成: 原动作={inputActions.Count}, 展开后={expanded.Count}");
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
            if (existingMap.TryGetValue("pathMode", out string existingMode) &&
                (string.Equals(existingMode, "Direct", System.StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(existingMode, "LLMReactiveSegment", System.StringComparison.OrdinalIgnoreCase)))
            {
                return actions;
            }
            if (existingMap.TryGetValue("segmentSource", out string segmentSource) &&
                string.Equals(segmentSource, "LLMReactiveSegment", System.StringComparison.OrdinalIgnoreCase))
            {
                return actions;
            }

            if (!disableLlmLongWaypoints &&
                TryBuildLlmWaypointsFromParameters(a, out List<Vector3> existing) && existing.Count > 0)
            {
                return actions;
            }

            Dictionary<string, string> inject = new Dictionary<string, string>
            {
                ["pathMode"] = "AStarHint",
                ["targetFeature"] = coarsePath.TargetFeatureName
            };
            if (!disableLlmLongWaypoints)
            {
                inject["waypoints"] = coarsePath.WaypointsInline;
            }

            a.Parameters = MergeParameters(a.Parameters, inject);
            if (a.TargetPosition == transform.position || Vector3.Distance(a.TargetPosition, transform.position) < 0.5f)
            {
                a.TargetPosition = coarsePath.GoalWorld;
            }
            return actions;
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

        // 目标是地点（building/feature）时，默认走 A* 粗路径。
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
    private string BuildHighLevelDecisionPrompt(string currentStep, StepNavigationDecision navDecision)
    {
        string role = agentProperties != null ? agentProperties.Role.ToString() : "Unknown";

        return $@"你是{agentProperties?.Type}智能体，角色={role}。
        基于“当前步骤文本”做第一阶段高层决策。
        注意：这一阶段的 target 只能从 step 自然语言中解析，不允许根据地图候选、感知节点列表、队友列表来猜目标。

        [当前步骤]
        {currentStep}

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
        1) 这是第一阶段，只输出高层动作和目标，不要输出具体 waypoint，不要输出长路径。
        2) target 必须严格来自 step 的自然语言语义，不允许根据地图或感知信息替换成别的建筑名、节点名或 AgentID。
        3) 如果 step 中有明确地点词，例如 building_2，target 必须保留这个标识，不要改写成别名或中文楼名。
        4) 如果 step 目标是树、车、行人、资源点、临时障碍等小节点，可输出该节点的 displayName 或自然语言名词，不必强行编造 nodeId。
        5) 如果 step 目标是队友，只有 step 中已明确给出 AgentID 时才输出 AgentID；不要凭上下文猜队友ID。
        6) 需要移动时，输出 MoveTo，parameters 仅写路径意图，例如 pathMode:AStarReactive。
        7) 起飞、扫描、通信可以和 MoveTo 组合输出。
        8) 无目标动作用 target=""none""。

        [目标格式]
        1) 地点目标: building:名称 或 feature:名称
        2) 小节点目标: nodeId 或 displayName
        3) 队友目标: AgentID
        4) 坐标目标: world(x,z) 或 world(x,y,z)
        5) 禁止输出与上述格式无关的自由文本目标

        请输出1-3个动作，只输出JSON数组，不要解释：
        [
        {{
            ""actionType"": ""动作类型"",
            ""target"": ""none|nodeId|displayName|AgentID|building:名称|feature:名称|world(x,z)|world(x,y,z)"",
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
        3) target 只能写 world(x,z) 或 world(x,y,z)，不要再写 building:xxx、节点名或 AgentID。
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
    /// 从第一阶段动作中解析最终目标，再由系统绑定到确定坐标。
    /// 若第一阶段没有可解析 target，则回退到 step 文本解析。
    /// </summary>
    private StepTargetBinding ResolveStepTargetBindingFromActionData(List<ActionData> actionDataList, string currentStep)
    {
        if (ContainsSelfReference(currentStep))
        {
            return BuildSelfTargetBinding(currentStep, "StepText:SelfReference");
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

                if (TryBuildStepTargetBindingFromTarget(target, out StepTargetBinding parsed))
                {
                    return parsed;
                }
            }
        }

        return ResolveStepTargetBinding(currentStep);
    }

    private bool TryBuildStepTargetBindingFromTarget(string target, out StepTargetBinding binding)
    {
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

        if (string.IsNullOrWhiteSpace(target) || IsNoneTargetToken(target))
        {
            return false;
        }

        string raw = target.Trim();
        if (string.Equals(raw, "step_target", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ContainsSelfReference(raw))
        {
            binding = BuildSelfTargetBinding(raw, "HighLevel:SelfReference");
            return true;
        }

        if (TryResolveCampusFeatureTarget(raw, out Vector3 featurePos, out string featureSource, out string normalized))
        {
            binding.HasTarget = true;
            binding.TargetKind = "Feature";
            binding.Source = $"HighLevel:{featureSource}";
            binding.WorldPos = featurePos;
            binding.Name = normalized ?? string.Empty;

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

            if (string.IsNullOrWhiteSpace(binding.Uid))
            {
                binding.Uid = binding.Name;
            }

            binding.Summary =
                $"ref={binding.TargetRef},kind={binding.TargetKind},query={binding.RawQuery},uid={binding.Uid},name={binding.Name},source={binding.Source},world=({binding.WorldPos.x:F1},{binding.WorldPos.z:F1}),grid=({binding.GridCell.x},{binding.GridCell.y})";
            return true;
        }

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
    /// 解析 CampusGrid2D 的地点目标。
    /// 支持：
    /// 1) building:名称
    /// 2) feature:名称
    /// 3) 直接写名称（会尝试精确/模糊匹配）
    /// </summary>
    private bool TryResolveCampusFeatureTarget(string target, out Vector3 worldPos, out string source, out string normalizedName)
    {
        worldPos = transform.position;
        source = string.Empty;
        normalizedName = string.Empty;
        if (string.IsNullOrWhiteSpace(target)) return false;

        string query = target.Trim();
        bool hasExplicitPrefix = false;
        if (query.StartsWith("building:", System.StringComparison.OrdinalIgnoreCase))
        {
            query = query.Substring("building:".Length).Trim();
            hasExplicitPrefix = true;
        }
        else if (query.StartsWith("feature:", System.StringComparison.OrdinalIgnoreCase))
        {
            query = query.Substring("feature:".Length).Trim();
            hasExplicitPrefix = true;
        }

        if (string.IsNullOrWhiteSpace(query)) return false;
        normalizedName = query;
        bool strictToken = hasExplicitPrefix || IsCanonicalFeatureToken(query);

        // 先用当前智能体“已看见地点”解析，避免直接依赖全图静态摘要。
        // 对显式 token（如 building_5）只做精确匹配，不做模糊映射。
        if (TryResolvePerceivedCampusFeatureTarget(query, out worldPos, allowFuzzy: !strictToken))
        {
            source = strictToken ? "PerceivedCampusFeatureExact" : "PerceivedCampusFeature";
            return true;
        }

        SyncCampusGridReference();
        if (campusGrid == null || campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null)
        {
            return false;
        }

        if (TryResolveFeatureNameToCell(query, out Vector2Int cell, out string matchedToken, exactOnly: strictToken))
        {
            worldPos = campusGrid.GridToWorldCenter(cell.x, cell.y, astarWaypointYOffset);
            worldPos.y = ResolveWaypointY(worldPos);
            source = strictToken ? "CampusGrid2DExact" : "CampusGrid2D";
            if (!string.IsNullOrWhiteSpace(matchedToken))
            {
                normalizedName = matchedToken;
            }
            return true;
        }

        // 兼容 CampusJsonMapLoader 场景对象命名：
        // building_2 这类 token 可能是“生成的 GameObject 名称”，并非 JSON 的 uid/name。
        // 当严格 uid/name 解析失败时，允许按场景对象名做一次精确定位。
        if (strictToken && TryResolveCampusSceneObjectExact(query, out Vector3 sceneObjPos))
        {
            worldPos = sceneObjPos;
            source = "CampusSceneObjectExact";
            normalizedName = query;
            return true;
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
    /// exactOnly=true 时仅允许精确 name/uid，不做模糊/编号回退。
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
    /// 判断是否是“显式地点标识符”：
    /// 例如 building_5 / feature_12 / building-3。
    /// 这类 token 默认走精确解析，避免被别名模糊映射。
    /// </summary>
    private static bool IsCanonicalFeatureToken(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();
        if (q.StartsWith("building:", System.StringComparison.OrdinalIgnoreCase))
            q = q.Substring("building:".Length).Trim();
        else if (q.StartsWith("feature:", System.StringComparison.OrdinalIgnoreCase))
            q = q.Substring("feature:".Length).Trim();

        if (System.Text.RegularExpressions.Regex.IsMatch(
            q,
            @"^(?:building|feature)\s*[_\-:：]?\s*\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(
            q,
            @"^[A-Za-z]+(?:[_\-][A-Za-z0-9]+)+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// CampusJsonMapLoader 命名兼容：
    /// 用场景中生成对象名做“精确匹配”定位（如 building_2）。
    /// 注意：仅做精确 equals，不做 contains/编号模糊，避免错绑到“2号教学楼”。
    /// </summary>
    private bool TryResolveCampusSceneObjectExact(string query, out Vector3 worldPos)
    {
        worldPos = transform.position;
        if (string.IsNullOrWhiteSpace(query)) return false;

        string q = query.Trim();
        if (q.StartsWith("building:", System.StringComparison.OrdinalIgnoreCase))
            q = q.Substring("building:".Length).Trim();
        else if (q.StartsWith("feature:", System.StringComparison.OrdinalIgnoreCase))
            q = q.Substring("feature:".Length).Trim();
        if (string.IsNullOrWhiteSpace(q)) return false;

        GameObject go = GameObject.Find(q);
        if (go == null) return false;

        // 若存在 CampusJsonMapLoader，优先要求命中对象位于其层级内，避免误命中无关同名对象。
        CampusJsonMapLoader loader = campusGrid != null ? campusGrid.campusLoader : null;
        if (loader != null && go.transform != null && !go.transform.IsChildOf(loader.transform))
        {
            return false;
        }

        worldPos = go.transform.position;
        worldPos.y = ResolveWaypointY(worldPos);
        return true;
    }

    // 解析动作参数
    private void ParseActionParameters(ActionCommand command, string parameters)
    {
        command.Parameters = NormalizeActionParameters(parameters);
    }

    // 执行动作序列
    private IEnumerator ExecuteActionSequence(List<ActionCommand> actions, string currentStep)
    {
        // 打印当前步骤和动作序列信息
        Debug.Log($"=== 开始执行步骤: {currentStep} ===");
        Debug.Log($"步骤描述: {currentStep}");
        Debug.Log($"动作序列数量: {actions.Count}");
        
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            Debug.Log($"动作 {i + 1}/{actions.Count}: {action.ActionType}, 目标: {action.TargetObject?.name ?? action.TargetPosition.ToString()}, 参数: {action.Parameters}");
        }
        Debug.Log("=== 步骤信息结束 ===");

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
        if (lastStepTargetBinding.TargetKind != "Feature" && lastStepTargetBinding.TargetKind != "World")
        {
            return false;
        }

        return !IsNearStepTarget(lastStepTargetBinding);
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
        if (planar > Mathf.Max(0.5f, stepTargetReachDistance)) return false;

        // 地面车不做高度判定；无人机做宽松高度判定。
        if (agentProperties != null && agentProperties.Type == AgentType.Quadcopter)
        {
            float dy = Mathf.Abs(cur.y - goal.y);
            if (dy > Mathf.Max(0.2f, stepTargetReachHeightTolerance)) return false;
        }
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
            @"[""']?(?<k>[A-Za-z_][A-Za-z0-9_]*)[""']?\s*[:=]\s*[""']?(?<v>[^,}\r\n]+)[""']?"
        );

        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string k = m.Groups["k"].Value.Trim();
            string v = m.Groups["v"].Value.Trim();
            if (string.IsNullOrEmpty(k)) continue;
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
