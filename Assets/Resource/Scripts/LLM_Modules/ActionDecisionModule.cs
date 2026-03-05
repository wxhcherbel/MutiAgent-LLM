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
    private PerceptionModule perceptionModule;
    private MLAgentsController mlAgentsController;
    private CampusGrid2D campusGrid;

    [Header("A*路径展开配置")]
    public bool enableAStarPathExpansion = true;     // 是否启用 MoveTo 的 A* 自动展开
    [Min(2f)] public float astarMinTriggerDistance = 10f; // 触发 A* 的最小水平距离
    [Range(1, 10)] public int astarWaypointStride = 3;    // 网格路径抽样步长
    [Range(1, 30)] public int astarMaxWaypoints = 10;     // 最多下发的 waypoint 数量
    public float astarWaypointYOffset = 0f;               // 网格中心点的 Y 偏移
    public bool astarPreferNearestWalkable = true;        // 起终点不可走时是否寻找最近可通行格子

    [Header("LLM路径点配置")]
    public bool preferLlmWaypoints = true;                // 优先使用 LLM 在参数中给出的 waypoint
    public bool allowAStarFallbackWhenNoLlmWaypoints = true; // LLM 未给 waypoint 时是否回退 A*
    [Range(4, 30)] public int maxCampusFeaturesInPrompt = 12; // 提示词中最多注入的建筑/地点条目

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
        public bool ShouldUseAStar;    // 当前 step 是否建议优先 A*
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

    void Start()
    {
        memoryModule = GetComponent<MemoryModule>();
        planningModule = GetComponent<PlanningModule>();
        llmInterface = FindObjectOfType<LLMInterface>();
        commModule = GetComponent<CommunicationModule>();
        perceptionModule = GetComponent<PerceptionModule>();
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
        return BuildStepNavigationDecision(GetCurrentStepDescription()).ShouldUseAStar;
    }

    /// <summary>
    /// 对外接口：返回当前 step 的导航判定摘要（用于调试面板显示）。
    /// </summary>
    public string GetCurrentStepNavigationSummary()
    {
        StepNavigationDecision d = BuildStepNavigationDecision(GetCurrentStepDescription());
        return $"move={(d.IsMovementStep ? 1 : 0)},astar={(d.ShouldUseAStar ? 1 : 0)},reason={d.Reason}";
    }

    /// <summary>
    /// 统一构建 step 导航判定（核心规则入口）。
    /// </summary>
    private StepNavigationDecision BuildStepNavigationDecision(string currentStep)
    {
        StepNavigationDecision decision = new StepNavigationDecision
        {
            IsMovementStep = false,
            ShouldUseAStar = false,
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
        decision.ShouldUseAStar = preferAStar;
        decision.Reason = $"policy={missionPolicy},move={(isMovement ? 1 : 0)},commObs={(isCommObs ? 1 : 0)},local={(isLocal ? 1 : 0)}";
        return decision;
    }

    /// <summary>
    /// 给 LLM 的导航策略摘要，尽量短，降低 token 开销。
    /// </summary>
    private string BuildNavigationPromptSummary(StepNavigationDecision decision)
    {
        NavigationPolicy p = planningModule != null ? planningModule.GetCurrentNavigationPolicy() : NavigationPolicy.Auto;
        return $"missionPolicy={p},stepMove={(decision.IsMovementStep ? 1 : 0)},preferAStar={(decision.ShouldUseAStar ? 1 : 0)},rule={decision.Reason}";
    }

    /// <summary>
    /// 基于当前 step 预构建 A* 粗路径（若可行）。
    /// 规则：
    /// 1) 仅在该 step 建议 A* 时尝试；
    /// 2) 仅当能从 step 中推断到地点名时构建；
    /// 3) 输出路径摘要给 LLM，让 LLM 细化 waypoint。
    /// </summary>
    private CoarsePathContext BuildCoarsePathContextForStep(string currentStep, StepNavigationDecision navDecision)
    {
        CoarsePathContext ctx = new CoarsePathContext
        {
            HasPath = false,
            TargetFeatureName = string.Empty,
            GoalWorld = transform.position,
            CoarseWaypoints = new List<Vector3>(),
            WaypointsInline = string.Empty,
            Summary = "无A*粗路径"
        };

        if (!navDecision.ShouldUseAStar)
        {
            ctx.Summary = "当前step不建议A*";
            return ctx;
        }
        if (campusGrid == null || campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null)
        {
            ctx.Summary = "CampusGrid2D 不可用";
            return ctx;
        }

        if (!TryExtractFeatureNameFromStep(currentStep, out string featureName))
        {
            ctx.Summary = "step中未解析到地点名，无法预构建粗路径";
            return ctx;
        }

        if (!TryResolveFeatureNameToCell(featureName, out Vector2Int goalCell))
        {
            ctx.Summary = $"未找到地点: {featureName}";
            return ctx;
        }

        if (!TryResolveWalkableCell(transform.position, out Vector2Int startCell))
        {
            ctx.Summary = "起点不可用";
            return ctx;
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

        int maxCount = Mathf.Max(1, astarMaxWaypoints);
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
            ? $"target={featureName},gridPath={gridPath.Count},coarseWp={waypoints.Count},waypoints={ctx.WaypointsInline}"
            : $"A*失败: {featureName}";
        return ctx;
    }

    /// <summary>
    /// 从当前 step 文本中提取地点名（优先匹配 CampusGrid2D 中已知地点）。
    /// </summary>
    private bool TryExtractFeatureNameFromStep(string stepText, out string featureName)
    {
        featureName = string.Empty;
        if (campusGrid == null || string.IsNullOrWhiteSpace(stepText) || campusGrid.cellFeatureNameGrid == null) return false;

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

        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            lowerStep,
            @"building[\s_:-]*(\d+)"
        );
        if (m.Success)
        {
            string normalized = $"building_{m.Groups[1].Value}";
            foreach (string n in names)
            {
                if (string.Equals(n, normalized, System.StringComparison.OrdinalIgnoreCase))
                {
                    featureName = n;
                    return true;
                }
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

    // 决定下一步行动（优化版本 - 有限感知）
    public IEnumerator<object> DecideNextAction()
    {
        
        string currentStep = GetCurrentStepDescription();
        if (currentStep == "无活跃任务" || currentStep == "任务已完成")
        {
            yield break;
        }
        StepNavigationDecision navDecision = BuildStepNavigationDecision(currentStep);
        CoarsePathContext coarsePath = BuildCoarsePathContextForStep(currentStep, navDecision);

        // 轻量化提示词：只给局部信息，减少 token，提升交互速度
        string prompt = $@"你是{agentProperties.Type}智能体，角色={agentProperties.Role}。
            只基于“当前步骤+局部感知”决策，不假设已知全图。

            [当前步骤]
            {currentStep}

            [状态]
            {BuildDecisionContext()}

            [任务]
            {GetTaskSummary()}

            [CampusGrid2D全局地点]
            {GetCampusGlobalFeatureSummary()}

            [导航策略]
            {BuildNavigationPromptSummary(navDecision)}

            [A*粗路径参考]
            {coarsePath.Summary}

            [环境小节点(仅局部感知)]
            {GetPerceptionSmallNodeSummary()}

            [附近队友]
            {GetNearbyAgentsSummary()}

            [历史经验]
            {GetMemorySummary()}

            [可用动作]
            Move: {GetAvailableMoveActions()}
            Interact: {GetAvailableInteractActions()}
            Observe: {GetAvailableObserveActions()}
            Comm: TransmitMessage(messageType in {GetAvailableMessageTypes()})

            [目标规则]
            1) 无目标动作用 target=""none""
            2) 小节点目标优先用 nodeId；也可用 displayName
            3) 队友目标用 AgentID
            4) 位置目标用 world(x,z) 或 world(x,y,z)，示例: world(12.5,8.0)
            5) 地图地点可用 target=""building:名称"" 或 target=""feature:名称""
            6) 在需要移动时，请在 parameters 中给 waypoint：
               pathMode:LLMPath,waypoints:10 20;14 26;18 32
               说明：每个点是 ""x z"" 或 ""x y z""，分号分隔
            7) 若提供了[A*粗路径参考]，必须沿粗路径并结合局部障碍细化 waypoint

            请输出1-3个动作（通信动作可额外附加），只输出JSON数组，不要解释：
            [
            {{
                ""actionType"": ""动作类型"",
                ""target"": ""none|nodeId|displayName|AgentID|building:名称|feature:名称|world(x,z)|world(x,y,z)"",
                ""parameters"": ""key:value,key2:value2 或 JSON字符串"",
                ""reason"": ""<=20字""
            }}
            ]";
        Debug.Log("构建prompt结束  DecideNextAction()");

        yield return llmInterface.SendRequest(prompt, (result) =>
        {
            if (!string.IsNullOrEmpty(result))
            {
                ParseAndExecuteDecisionSequence(result, currentStep, coarsePath);
            }
            else
            {
                ExecuteDefaultAction();
            }
        }, temperature: 0.1f, maxTokens: 320);
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
    

    // ==================== 通信相关接口调用 ====================

    /// <summary>
    /// 获取通信状态摘要 - 通过通信模块接口
    /// </summary>
    private string GetCommunicationSummary()
    {
        if (commModule != null)
        {
            return commModule.GetCommunicationSummary();
        }
        return "通信模块未初始化";
    }

    /// <summary>
    /// 发送状态更新消息 - 通过通信模块接口
    /// </summary>
    private void SendStatusUpdate(string content = "")
    {
        if (commModule != null)
        {
            if (string.IsNullOrEmpty(content))
            {
                content = $"{{\"status\":\"{agentState.Status}\", \"battery\":{agentState.BatteryLevel}, \"position\":\"{agentState.Position}\"}}";
            }
            commModule.SendMessage("All", MessageType.StatusUpdate, content, 1);
        }
    }

    /// <summary>
    /// 发送求助请求 - 通过通信模块接口
    /// </summary>
    private void SendHelpRequest(string reason)
    {
        if (commModule != null)
        {
            string content = $"{{\"reason\":\"{reason}\", \"position\":\"{agentState.Position}\", \"battery\":{agentState.BatteryLevel}}}";
            commModule.SendMessage("All", MessageType.HelpRequest, content, 3);
        }
    }

    /// <summary>
    /// 发送任务完成通知 - 通过通信模块接口
    /// </summary>
    private void SendTaskCompletion(string taskDetails)
    {
        if (commModule != null)
        {
            commModule.SendMessage("All", MessageType.TaskCompletion, taskDetails, 2);
        }
    }

    /// <summary>
    /// 发送障碍物警告 - 通过通信模块接口
    /// </summary>
    private void SendObstacleWarning(Vector3 obstaclePosition, string obstacleType)
    {
        if (commModule != null)
        {
            string content = $"{{\"position\":\"{obstaclePosition}\", \"type\":\"{obstacleType}\"}}";
            commModule.SendMessage("All", MessageType.ObstacleWarning, content, 2);
        }
    }

    /// <summary>
    /// 检查是否有未读消息 - 通过通信模块接口
    /// </summary>
    private bool HasUnreadMessages()
    {
        if (commModule != null)
        {
            return commModule.HasUnreadMessages();
        }
        return false;
    }

    /// <summary>
    /// 获取未读消息 - 通过通信模块接口
    /// </summary>
    private AgentMessage[] GetUnreadMessages()
    {
        if (commModule != null)
        {
            return commModule.GetUnreadMessages();
        }
        return new AgentMessage[0];
    }

    // ==================== 决策上下文构建方法 ====================

    // 构建决策上下文
    private string BuildDecisionContext()
    {
        if (agentState == null || agentProperties == null) return "状态缺失";
        Vector3 p = agentState.Position;
        return $"id={agentProperties.AgentID},team={agentState.TeamID},status={agentState.Status},bat={agentState.BatteryLevel:F1}/{agentProperties.BatteryCapacity:F1},pos=({p.x:F1},{p.z:F1}),v={agentState.Velocity.magnitude:F1},load={agentState.CurrentLoad:F1}/{agentProperties.PayloadCapacity:F1}";
    }

    // 获取能力摘要
    private string GetCapabilitiesSummary()
    {
        return $@"移动:最大{agentProperties.MaxSpeed}m/s, 加速{agentProperties.Acceleration}m/s²
        感知:范围{agentProperties.PerceptionRange}m, 通信:{agentProperties.CommunicationRange}m
        物理:尺寸{agentProperties.PhysicalSize}, 类型:{agentProperties.Type}";
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
    /// 构建 CampusGrid2D 的全局地点摘要，注入到 LLM 提示词。
    /// 目的：
    /// 1) 让 LLM 知道可用建筑/地点名称；
    /// 2) 让 LLM 能输出 building:名称 或 feature:名称；
    /// 3) 降低“目标坐标无来源”的问题。
    /// </summary>
    private string GetCampusGlobalFeatureSummary()
    {
        if (campusGrid == null)
        {
            return "CampusGrid2D 不可用";
        }
        if (campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null || campusGrid.cellFeatureNameGrid == null)
        {
            return "CampusGrid2D 尚未构建";
        }

        Dictionary<string, Vector2Int> firstCellByName = new Dictionary<string, Vector2Int>();
        for (int x = 0; x < campusGrid.gridWidth; x++)
        {
            for (int z = 0; z < campusGrid.gridLength; z++)
            {
                string n = campusGrid.cellFeatureNameGrid[x, z];
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (!firstCellByName.ContainsKey(n))
                {
                    firstCellByName[n] = new Vector2Int(x, z);
                }
            }
        }

        if (firstCellByName.Count == 0)
        {
            return "CampusGrid2D 中暂无可用地点名称";
        }

        Vector2 me = new Vector2(transform.position.x, transform.position.z);
        List<string> lines = new List<string>();
        foreach (var kv in firstCellByName)
        {
            Vector2Int c = kv.Value;
            Vector3 wp = campusGrid.GridToWorldCenter(c.x, c.y, 0f);
            float d = Vector2.Distance(me, new Vector2(wp.x, wp.z));
            lines.Add($"{kv.Key}|grid=({c.x},{c.y})|world=({wp.x:F1},{wp.z:F1})|d={d:F1}m");
        }

        lines = lines.OrderBy(s =>
        {
            int p = s.LastIndexOf("|d=", System.StringComparison.Ordinal);
            if (p < 0) return float.MaxValue;
            string tail = s.Substring(p + 3).Replace("m", "");
            return float.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.MaxValue;
        }).Take(Mathf.Max(4, maxCampusFeaturesInPrompt)).ToList();

        Rect b = campusGrid.mapBoundsXY;
        return $"mapBoundsXZ=({b.xMin:F1},{b.yMin:F1})~({b.xMax:F1},{b.yMax:F1}),cell={campusGrid.cellSize:F1}m,features={firstCellByName.Count}\n" +
               $"可用地点示例:\n- " + string.Join("\n- ", lines);
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

    // 获取动作类型枚举值列表
    private string GetActionTypeEnumValues()
    {
        List<string> actionTypes = new List<string>();
        foreach (PrimitiveActionType actionType in System.Enum.GetValues(typeof(PrimitiveActionType)))
        {
            actionTypes.Add(actionType.ToString());
        }
        return string.Join("|", actionTypes);
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
    /// 1) 优先使用 LLM 在 parameters 中给出的 waypoint（pathMode=LLMPath）；
    /// 2) 若未给 waypoint，且 step 建议 A*，则回退到 CampusGrid2D 的 A* 路径点；
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

            // 第一优先级：LLM 显式给 waypoint
            if (preferLlmWaypoints &&
                TryBuildLlmWaypointsFromParameters(action, out List<Vector3> llmWaypoints))
            {
                AppendWaypointsAsMoveActions(action, llmWaypoints, expanded, "LLMPath");
                virtualStart = llmWaypoints[llmWaypoints.Count - 1];
                expandedAny = true;
                continue;
            }

            // 第二优先级：A* 兜底
            if (!allowAStarFallbackWhenNoLlmWaypoints || !enableAStarPathExpansion || !navDecision.ShouldUseAStar)
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

            if (!ShouldUseAStarForMoveAction(action, virtualStart))
            {
                expanded.Add(action);
                virtualStart = action.TargetPosition;
                continue;
            }

            if (TryBuildAStarWaypoints(virtualStart, action.TargetPosition, out List<Vector3> astarWaypoints))
            {
                AppendWaypointsAsMoveActions(action, astarWaypoints, expanded, "AStar");
                virtualStart = astarWaypoints[astarWaypoints.Count - 1];
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

            if (TryBuildLlmWaypointsFromParameters(a, out List<Vector3> existing) && existing.Count > 0)
            {
                return actions;
            }

            Dictionary<string, string> inject = new Dictionary<string, string>
            {
                ["pathMode"] = "AStarHint",
                ["waypoints"] = coarsePath.WaypointsInline,
                ["targetFeature"] = coarsePath.TargetFeatureName
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
    private bool ShouldUseAStarForMoveAction(ActionCommand moveAction, Vector3 startPos)
    {
        if (moveAction == null) return false;
        if (campusGrid == null) return false;

        Dictionary<string, string> pmap = ParseLooseParameterMap(moveAction.Parameters);
        if (pmap.TryGetValue("pathMode", out string mode) &&
            string.Equals(mode, "Direct", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        float planarDist = Vector2.Distance(
            new Vector2(startPos.x, startPos.z),
            new Vector2(moveAction.TargetPosition.x, moveAction.TargetPosition.z)
        );
        if (planarDist < astarMinTriggerDistance) return false;

        return true;
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
                ["waypointTotal"] = total.ToString()
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

    // 解析并执行决策序列 - 完整版本
    private void ParseAndExecuteDecisionSequence(string llmResponse, string currentStep, CoarsePathContext coarsePath)
    {
        try
        {
            // 解析JSON数组
            List<ActionCommand> actionSequence = ParseActionSequenceFromJSON(llmResponse);
            actionSequence = ApplyCoarsePathFallbackIfNeeded(actionSequence, coarsePath);
            StepNavigationDecision navDecision = BuildStepNavigationDecision(currentStep);
            actionSequence = ExpandMoveActionsByAStar(actionSequence, navDecision);
            
            if (actionSequence != null && actionSequence.Count > 0)
            {
                agentState.Status = AgentStatus.ExecutingTask;
                // 执行动作序列
                StartCoroutine(ExecuteActionSequence(actionSequence, currentStep));
            }
            else
            {
                ExecuteDefaultAction();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"决策序列解析失败: {e.Message}");
            ExecuteDefaultAction();
        }
    }

    // 从JSON解析动作序列
    private List<ActionCommand> ParseActionSequenceFromJSON(string jsonResponse)
    {
        try
        {
            // 提取纯 JSON 部分
            string cleanJson = ExtractPureJson(jsonResponse);

            if (string.IsNullOrEmpty(cleanJson))
            {
                Debug.LogError("无法从响应中提取 JSON 内容");
                return null;
            }
            Debug.Log($"解析后的动作列表 JSON: {cleanJson}");

            // 兼容两种格式：
            // 1) 纯数组: [ ... ]
            // 2) 对象包裹: { "actions": [ ... ] }
            ActionSequenceWrapper wrapper = null;
            if (cleanJson.TrimStart().StartsWith("{"))
            {
                wrapper = JsonUtility.FromJson<ActionSequenceWrapper>(cleanJson);
            }
            if (wrapper == null || wrapper.actions == null)
            {
                wrapper = JsonUtility.FromJson<ActionSequenceWrapper>($"{{\"actions\":{cleanJson}}}");
            }
            if (wrapper?.actions != null)
            {
                List<ActionCommand> commands = new List<ActionCommand>();

                foreach (var actionData in wrapper.actions)
                {
                    ActionCommand command = ParseSingleAction(actionData);
                    if (command != null)
                    {
                        commands.Add(command);
                    }
                }
                return commands;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"JSON解析失败: {e.Message}\n原始响应: {jsonResponse}");
        }

        return null;
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
        
        // 解析动作类型
        if (System.Enum.TryParse<PrimitiveActionType>(actionData.actionType, out PrimitiveActionType actionType))
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

        // 解析目标（新版本：world坐标 / nodeId / displayName / AgentID）
        if (!IsNoTargetAction(actionType))
        {
            string target = (actionData.target ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(target) || IsNoneTargetToken(target))
            {
                command.TargetPosition = transform.position;
            }
            else if (TryParseWorldTarget(target, out Vector3 worldPos))
            {
                command.TargetPosition = worldPos;
            }
            else if (TryResolveCampusFeatureTarget(target, out Vector3 featurePos))
            {
                command.TargetPosition = featurePos;
            }
            else if (TryFindSmallNodeById(target, out GameObject detectedObj, out Vector3 detectedPos))
            {
                command.TargetObject = detectedObj;
                command.TargetPosition = detectedPos;
            }
            else if (FindObjectByNameInPerception(target, out GameObject nameMatched))
            {
                command.TargetObject = nameMatched;
                command.TargetPosition = nameMatched.transform.position;
            }
            else if (TryFindNearbyAgent(target, out GameObject teammate))
            {
                command.TargetObject = teammate;
                command.TargetPosition = teammate.transform.position;
            }
            else
            {
                command.TargetPosition = transform.position;
                Debug.LogWarning($"无法解析目标: {target}，已回退到当前位置。");
            }
        }
        
        // 解析参数
        if (!string.IsNullOrEmpty(actionData.parameters))
        {
            ParseActionParameters(command, actionData.parameters);
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
    /// 解析 CampusGrid2D 的地点目标。
    /// 支持：
    /// 1) building:名称
    /// 2) feature:名称
    /// 3) 直接写名称（会尝试精确/模糊匹配）
    /// </summary>
    private bool TryResolveCampusFeatureTarget(string target, out Vector3 worldPos)
    {
        worldPos = transform.position;
        if (campusGrid == null || string.IsNullOrWhiteSpace(target)) return false;
        if (campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null) return false;

        string query = target.Trim();
        if (query.StartsWith("building:", System.StringComparison.OrdinalIgnoreCase))
        {
            query = query.Substring("building:".Length).Trim();
        }
        else if (query.StartsWith("feature:", System.StringComparison.OrdinalIgnoreCase))
        {
            query = query.Substring("feature:".Length).Trim();
        }

        if (string.IsNullOrWhiteSpace(query)) return false;

        if (TryResolveFeatureNameToCell(query, out Vector2Int cell))
        {
            worldPos = campusGrid.GridToWorldCenter(cell.x, cell.y, astarWaypointYOffset);
            worldPos.y = ResolveWaypointY(worldPos);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 把地点名称映射到网格坐标。
    /// 优先精确匹配；失败后再做模糊包含匹配。
    /// </summary>
    private bool TryResolveFeatureNameToCell(string featureName, out Vector2Int cell)
    {
        cell = new Vector2Int(-1, -1);
        if (campusGrid == null || string.IsNullOrWhiteSpace(featureName)) return false;

        if (campusGrid.TryGetFeatureFirstCell(featureName, out cell, preferWalkable: true, ignoreCase: true))
        {
            return true;
        }

        if (campusGrid.cellFeatureNameGrid == null) return false;

        string q = featureName.Trim();
        string bestName = null;
        int bestScore = int.MaxValue;
        HashSet<string> unique = new HashSet<string>();

        for (int x = 0; x < campusGrid.gridWidth; x++)
        {
            for (int z = 0; z < campusGrid.gridLength; z++)
            {
                string n = campusGrid.cellFeatureNameGrid[x, z];
                if (string.IsNullOrWhiteSpace(n) || !unique.Add(n)) continue;

                if (n.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    q.IndexOf(n, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int score = Mathf.Abs(n.Length - q.Length);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestName = n;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(bestName)) return false;
        return campusGrid.TryGetFeatureFirstCell(bestName, out cell, preferWalkable: true, ignoreCase: true);
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
        
        // 所有动作完成，推进到下一步
        if (planningModule?.currentPlan != null)
        {
            // 当前步骤完成，currentStep++
            planningModule.currentPlan.currentStep++;
            Debug.Log($"步骤完成: {currentStep}，推进到下一步: {planningModule.currentPlan.currentStep + 1}/{planningModule.currentPlan.steps.Length}");
            
            // 检查是否所有步骤都完成
            if (planningModule.currentPlan.currentStep >= planningModule.currentPlan.steps.Length)
            {
                Debug.Log($"🎉 任务完成: {planningModule.currentPlan.mission}");
                // 调用 CompleteCurrentTask 来标记任务完成
                planningModule.CompleteCurrentTask();
            }
            else
            {
                // 还有后续步骤，继续决策执行下一个步骤
                Debug.Log($"准备执行下一个步骤");
                StartCoroutine(DecideNextAction());
            }
        }
        else
        {
            Debug.LogWarning("planningModule 或 currentPlan 为 null");
        }
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
