using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 行为执行层：
/// 1. 轮询 ADM，消费当前 AtomicAction。
/// 2. 将动作转换成路径跟随、悬停、跟踪等移动指令。
/// 3. 使用 Context Steering（上下文导向）做局部避障。
///
/// 避障核心原理（Context Steering）：
///   - 每帧对 360° 方向槽位填充 Interest/Danger/Mask 三层评分
///   - Interest：目标方向（cos 衰减）
///   - Danger：射线探测到的障碍（距离反比），当前 MoveTo 目标可被豁免
///   - Mask：网格不可走方向（硬屏蔽）
///   - 最终选 score = interest - danger 最高的未屏蔽方向
///   - 邻近槽位加权平滑，消除离散跳变
/// </summary>
public class AgentMotionExecutor : MonoBehaviour
{
    [Header("路径可视化")]
    public AStarPathVisualizer pathVisualizer;

    [Header("Pure Pursuit")]
    [SerializeField] private float lookAheadDist = 6f;
    [SerializeField] private float waypointArrivalDistance = 1.8f;
    [SerializeField] private float finalArrivalDistance = 1.2f;

    [Header("卡住检测")]
    [SerializeField] private float stuckCheckInterval = 2f;
    [SerializeField] private float stuckMoveThreshold = 0.8f;

    [Header("悬停漂移")]
    [SerializeField] private float holdDriftAmp = 0.16f;
    [SerializeField] private float holdDriftFreq = 0.25f;

    [Header("避障")]
    [SerializeField] private LayerMask obstacleLayers;
    [SerializeField] private LayerMask agentAvoidanceLayers;
    [SerializeField] private float detectionRange = 12f;        // 前方探测距离
    [SerializeField] private float probeRadius = 0.6f;          // SphereCast 半径
    [SerializeField] private float dangerDistance = 8f;          // 开始避障的距离
    [SerializeField] private float crowdAvoidanceRadius = 3.2f;
    [SerializeField] private float crowdAvoidanceStrength = 4.6f;
    [SerializeField] private bool logAvoidance = true;

    // 物理射线实际使用的掩码（排除 Building 层，建筑由网格检查处理）
    private int _dynamicObstacleMask;
    private int _agentLayerIndex = -1;

    // ── Context Steering ──
    private const int SteerSlots = 12;  // 方向槽位数（每30°一个，360°全覆盖）
    private readonly float[] _interestMap = new float[SteerSlots];
    private readonly float[] _dangerMap  = new float[SteerSlots];
    private readonly bool[]  _maskMap    = new bool[SteerSlots];
    private string _currentMoveTargetNodeId;  // 当前 MoveTo 目标小节点ID（用于避障豁免）

    private AerialMotionController aerialMotion;
    private ActionDecisionModule adm;
    private CampusGrid2D campusGrid;
    private AgentProperties props;
    private IntelligentAgent agent;
    private MemoryModule memoryModule;
    private PerceptionModule perceptionModule;

    private AtomicAction currentAction;
    private bool actionRunning;
    private Coroutine actionCoroutine;

    private List<Vector3> currentPath = new();
    private int waypointIdx;
    private float defaultTargetHeight;

    private Vector3 stuckCheckPos;
    private float stuckTimer;
    private bool isStuck;

    private float noiseX;
    private float noiseZ;

    // 卡住恢复
    private int localStuckCount;
    private float escapeAltitudeBoost;

    // Agent-Agent 让行记忆
    private readonly Dictionary<string, float> yieldMemory = new();
    private const float YieldDuration = 2.0f;
    private const float YieldMemoryCleanupInterval = 5f;
    private float yieldCleanupTimer;

    // 携带物品：恢复缩放用临时变量（主状态在 AgentDynamicState）
    private Vector3 _carriedOriginalScale;

    /// <summary>避障探测快照（供可视化）。</summary>
    public struct AvoidanceProbeSnapshot
    {
        public bool    valid;
        public Vector3 origin;
        public Vector3 resultVelocity;
        public float   maxDanger;
        public bool    gridConstrained;
    }

    public AvoidanceProbeSnapshot CurrentAvoidanceProbe { get; private set; }

    // 近距离拥挤分离结果
    private struct CrowdAvoidanceResult
    {
        public bool hasNearbyAgents;
        public bool shouldCreateBypass;
        public int nearbyCount;
        public Vector3 avoidanceOffset;
        public string selectedSide;
        public string dominantAgentId;
    }

    private void Awake()
    {
        aerialMotion = GetComponent<AerialMotionController>();
        if (aerialMotion == null)
        {
            aerialMotion = gameObject.AddComponent<AerialMotionController>();
            Debug.Log("[AME] 自动挂载 AerialMotionController");
        }
    }

    private void Start()
    {
        adm = GetComponent<ActionDecisionModule>();
        campusGrid = FindObjectOfType<CampusGrid2D>();
        agent = GetComponent<IntelligentAgent>();
        props = agent?.Properties;
        memoryModule = GetComponent<MemoryModule>();
        perceptionModule = GetComponent<PerceptionModule>();

        if (props != null && props.MaxSpeed > 0f)
            aerialMotion.MaxSpeed = props.MaxSpeed;

        defaultTargetHeight = aerialMotion.TargetHeight;
        stuckCheckPos = transform.position;
        noiseX = UnityEngine.Random.Range(0f, 100f);
        noiseZ = UnityEngine.Random.Range(0f, 100f);

        if (obstacleLayers.value == 0)
        {
            int obstacleLayer = LayerMask.NameToLayer("Obstacle");
            if (obstacleLayer >= 0)
                obstacleLayers = 1 << obstacleLayer;
            else
            {
                Debug.LogWarning($"[AME] {name}: 未找到 'Obstacle' 层，避障模块可能失效。");
                obstacleLayers = 1 << 0;
            }
        }

        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer >= 0)
            obstacleLayers |= 1 << buildingLayer;

        if (agentAvoidanceLayers.value == 0)
        {
            int agentLayer = LayerMask.NameToLayer("Agent");
            if (agentLayer >= 0)
                agentAvoidanceLayers = 1 << agentLayer;
        }
        _agentLayerIndex = LayerMask.NameToLayer("Agent");

        // 构建物理射线掩码：排除 Building 层。
        // 建筑是静态障碍，已由 A* 路径规划处理。
        // SphereCast 只检测 Obstacle 层（命中后再用 tag 过滤，仅保留 Tree）。
        // Agent-Agent 避障由 TryBuildCrowdAvoidance 的 OverlapSphere 独立处理。
        _dynamicObstacleMask = obstacleLayers.value;
        if (buildingLayer >= 0)
            _dynamicObstacleMask &= ~(1 << buildingLayer);

        Debug.Log($"[AME] {name} 避障层初始化: obstacleLayers={obstacleLayers.value}, buildingLayer={buildingLayer}, dynamicMask={_dynamicObstacleMask}, agentMask={agentAvoidanceLayers.value} (SphereCast仅检测Tree标签)");
    }

    private void Update()
    {
        UpdateStuckDetection();
        PollADM();
        if (!actionRunning) CurrentAvoidanceProbe = default;
    }

    private void PollADM()
    {
        if (adm == null) return;
        AtomicAction next = adm.GetCurrentAction();
        if (next == null)
        {
            if (!actionRunning && aerialMotion != null)
                aerialMotion.MoveTarget = null;
            return;
        }
        if (!actionRunning || currentAction?.actionId != next.actionId)
        {
            AbortCurrentExecutionState();
            currentAction = next;
            actionRunning = true;
            actionCoroutine = StartCoroutine(ExecuteAction(next));
        }
    }

    private void UpdateStuckDetection()
    {
        if (!actionRunning || currentAction == null ||
            (currentAction.type != AtomicActionType.MoveTo &&
             currentAction.type != AtomicActionType.Patrol &&
             currentAction.type != AtomicActionType.Track))
        {
            isStuck = false;
            stuckTimer = 0f;
            return;
        }
        stuckTimer += Time.deltaTime;
        if (stuckTimer < stuckCheckInterval) return;

        float moved = HorizontalDistance(transform.position, stuckCheckPos);
        isStuck = moved < stuckMoveThreshold;
        if (isStuck)
            Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住，{stuckCheckInterval:F1}s 内仅移动 {moved:F2}m");
        stuckCheckPos = transform.position;
        stuckTimer = 0f;
    }

    // ==================================================================
    // 动作执行（与原版完全一致，仅避障入口不同）
    // ==================================================================

    private IEnumerator ExecuteAction(AtomicAction action)
    {
        switch (action.type)
        {
            case AtomicActionType.MoveTo:   yield return StartCoroutine(DoMoveTo(action)); break;
            case AtomicActionType.Wait:     yield return StartCoroutine(DoWait(action)); break;
            case AtomicActionType.Track:    yield return StartCoroutine(DoTrack(action)); break;
            case AtomicActionType.Signal:   yield return StartCoroutine(DoSignal(action)); break;
            case AtomicActionType.Get:      yield return StartCoroutine(DoGet(action)); break;
            case AtomicActionType.Put:      yield return StartCoroutine(DoPut(action)); break;
            case AtomicActionType.Land:     yield return StartCoroutine(DoLand(action)); break;
            case AtomicActionType.Takeoff:  yield return StartCoroutine(DoTakeoff(action)); break;
            case AtomicActionType.Patrol:   yield return StartCoroutine(DoPatrol(action)); break;
            case AtomicActionType.Approach: yield return StartCoroutine(DoApproach(action)); break;
            case AtomicActionType.Flee:     yield return StartCoroutine(DoFlee(action)); break;
            default: Debug.LogWarning($"[AME] 未知动作类型 {action.type}，跳过执行"); break;
        }
        actionRunning = false;
        adm?.CompleteCurrentAction();
    }

    private IEnumerator DoMoveTo(AtomicAction action)
    {
        if (campusGrid == null || aerialMotion == null)
        {
            Debug.LogWarning("[AME] MoveTo 缺少执行依赖");
            action.result = "执行失败：缺少 campusGrid 或 aerialMotion 依赖";
            yield break;
        }

        Debug.Log($"[AME] {props?.AgentID ?? name} 开始 MoveTo -> {action.targetName}");
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "move_start", $"前往 {action.targetName}");

        _currentMoveTargetNodeId = null;
        if (!TryBuildPathToTargetName(action.targetName, aerialMotion.TargetHeight, out List<Vector3> path))
        {
            // 备选策略 1：尝试解析为感知到的小节点（资源点/树木等），直飞
            if (TryBuildDirectPathToSmallNode(action.targetName, aerialMotion.TargetHeight, out path))
            {
                Debug.Log($"[AME] {props?.AgentID ?? name} 小节点直飞 -> {action.targetName}");
                _currentMoveTargetNodeId = action.targetName;  // 标记为避障豁免目标
            }
            // 备选策略 2：尝试直飞目标特征质心
            else if (TryBuildFallbackPathToFeature(action.targetName, aerialMotion.TargetHeight, out path))
            {
                Debug.LogWarning($"[AME] {props?.AgentID ?? name} 找不到 {action.targetName} 标准接近点，改用质心直飞");
            }
            else
            {
                Debug.LogWarning($"[AME] MoveTo 找不到目标接近点：{action.targetName}（三级查找均失败：UID/别名/显示名均无匹配）");
                action.result = $"路径规划失败：目标 {action.targetName} 不可达，请换一个目标";
                AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "path_fail", $"目标 {action.targetName} 不可达");
                yield break;
            }
        }

        currentPath = path;
        waypointIdx = 0;
        pathVisualizer?.ShowPath(currentPath, GetTeamColor());

        // 诊断日志：路径信息
        float totalDist = HorizontalDistance(transform.position, currentPath[currentPath.Count - 1]);
        Debug.Log($"[AME] {props?.AgentID ?? name} 路径已建: {currentPath.Count} 航点, 直线距终点={totalDist:F1}m, 当前位置={transform.position}");

        const float waypointTimeout = 15f;
        float waypointTimer = 0f;

        while (waypointIdx < currentPath.Count)
        {
            Vector3 waypoint = currentPath[waypointIdx];
            Vector3 carrot = GetGridSafeCarrot(currentPath, waypointIdx, lookAheadDist);
            CommandMoveTarget(carrot, allowAvoidance: true);

            waypointTimer += Time.deltaTime;
            if (ShouldAdvanceWaypoint(waypoint, waypointArrivalDistance))
            { waypointIdx++; waypointTimer = 0f; }
            else if (waypointTimer >= waypointTimeout)
            {
                Debug.LogWarning($"[AME] {action.targetName} 航点[{waypointIdx}] 超时");
                AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "waypoint_timeout", $"航点[{waypointIdx}] 超时");
                // P4-fix: 超时时优先重规划，避免跳过关键转折航点穿越建筑
                Vector3 finalGoal = currentPath[currentPath.Count - 1];
                if (TryReplanPath(finalGoal, aerialMotion.TargetHeight, out List<Vector3> timeoutReplanned))
                {
                    currentPath = timeoutReplanned; waypointIdx = 0;
                    pathVisualizer?.ShowPath(currentPath, GetTeamColor());
                }
                else
                {
                    waypointIdx++; // 重规划失败才兜底跳过
                }
                waypointTimer = 0f;
            }

            if (isStuck)
            {
                Vector3 finalGoal = currentPath[currentPath.Count - 1];
                if (TryHandleLocalObstacleStuck(finalGoal, aerialMotion.TargetHeight, out List<Vector3> replanned))
                {
                    currentPath = replanned; waypointIdx = 0; waypointTimer = 0f;
                    pathVisualizer?.ShowPath(currentPath, GetTeamColor());
                }
                isStuck = false;
            }
            yield return null;
        }

        // 最终逼近
        Vector3 finalPoint = currentPath[currentPath.Count - 1];
        float finalTimer = 0f;
        while (HorizontalDistance(transform.position, finalPoint) > finalArrivalDistance && finalTimer < 6f)
        {
            CommandMoveTarget(finalPoint, allowAvoidance: true);
            finalTimer += Time.deltaTime;
            yield return null;
        }

        pathVisualizer?.ClearPath();
        _currentMoveTargetNodeId = null;
        ResetAvoidanceState();
        float finalDistToTarget = HorizontalDistance(transform.position, finalPoint);
        Debug.Log($"[AME] MoveTo 完成 -> {action.targetName}, 距目标={finalDistToTarget:F1}m, 位置={transform.position}");
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "arrive", $"到达 {action.targetName}");
    }

    private IEnumerator DoWait(AtomicAction action) { yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 2f)); }

    private IEnumerator DoTrack(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 10f;
        float elapsed = 0f;
        IntelligentAgent target = null;
        if (!string.IsNullOrWhiteSpace(action.targetAgentId))
            foreach (var c in FindObjectsOfType<IntelligentAgent>())
                if (c.Properties?.AgentID == action.targetAgentId) { target = c; break; }
        if (target == null) Debug.LogWarning($"[AME] Track 找不到目标智能体：{action.targetAgentId}");

        Vector3 localOffset = ParseOffsetFromParams(action.actionParams);
        while (elapsed < duration)
        {
            if (target != null)
            {
                Vector3 trackPos = target.transform.position + target.transform.rotation * localOffset;
                trackPos.y = aerialMotion.TargetHeight;
                CommandMoveTarget(trackPos, allowAvoidance: true);
            }
            elapsed += Time.deltaTime;
            yield return null;
        }
        Debug.Log($"[AME] Track 完成 -> {action.targetAgentId}");
    }

    private IEnumerator DoSignal(AtomicAction action)
    {
        string content = string.IsNullOrWhiteSpace(action.actionParams) ? "signal" : action.actionParams;
        string recipient = string.IsNullOrWhiteSpace(action.targetAgentId) ? "all" : action.targetAgentId;
        CommunicationModule comm = GetComponent<CommunicationModule>();
        if (comm != null)
        {
            if (recipient == "all")
                foreach (var oa in FindObjectsOfType<IntelligentAgent>())
                { string rid = oa.Properties?.AgentID; if (!string.IsNullOrWhiteSpace(rid) && rid != props?.AgentID) comm.SendMessage(rid, MessageType.StatusUpdate, content); }
            else
                comm.SendMessage(recipient, MessageType.StatusUpdate, content);
        }
        Debug.Log($"[AME] Signal -> {recipient}: {content}");
        yield break;
    }

    private IEnumerator DoGet(AtomicAction action)
    {
        string targetName = action.targetName;
        var state = agent?.CurrentState;

        // 已携带物品
        if (state != null && state.CarriedObject != null)
        {
            action.result = $"拾取失败：已携带 {state.CarriedItemName}，需先放下";
            Debug.LogWarning($"[AME] Get 失败 -> 已携带 {state.CarriedItemName}");
            yield break;
        }

        // 未指定目标
        if (string.IsNullOrWhiteSpace(targetName))
        {
            action.result = "拾取失败：未指定目标名称";
            Debug.LogWarning("[AME] Get 失败 -> 未指定 targetName");
            yield break;
        }

        // 通过 SmallNodeRegistry 查找目标（精确+模糊匹配）
        if (!SmallNodeRegistry.TryFindNode(targetName, transform.position, 60f, out SmallNodeData nodeData))
        {
            action.result = $"拾取失败：找不到 {targetName}";
            Debug.LogWarning($"[AME] Get 失败 -> 找不到 {targetName}");
            yield break;
        }

        GameObject targetObj = nodeData.SceneObject;
        if (targetObj == null)
        {
            action.result = $"拾取失败：{targetName} 场景对象无效";
            yield break;
        }

        // 距离检查（8m，考虑无人机悬停高度+水平误差）
        float dist = Vector3.Distance(transform.position, targetObj.transform.position);
        if (dist > 8f)
        {
            action.result = $"拾取失败：{targetName} 距离过远({dist:F1}m > 8m)，请先 MoveTo 靠近";
            Debug.LogWarning($"[AME] Get 失败 -> {targetName} 距离 {dist:F1}m");
            yield break;
        }

        // 悬停等待（模拟拾取动作）
        yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 1f));

        // 挂载物体到无人机下方
        _carriedOriginalScale = targetObj.transform.localScale;

        Rigidbody rb = targetObj.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        Collider col = targetObj.GetComponent<Collider>();
        if (col != null) col.enabled = false;

        targetObj.transform.SetParent(transform);
        targetObj.transform.localPosition = new Vector3(0f, -1.2f, 0f);
        targetObj.transform.localRotation = Quaternion.identity;

        // 更新携带状态
        if (state != null)
        {
            state.CarriedItemName = nodeData.NodeId;
            state.CarriedObject = targetObj;
        }

        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "get_done", $"拾取 {targetName} 成功");
        action.result = $"成功拾取 {targetName}";
        Debug.Log($"[AME] Get 完成 -> {targetName} (nodeId={nodeData.NodeId})");
    }

    private IEnumerator DoPut(AtomicAction action)
    {
        var state = agent?.CurrentState;

        // 没有携带物品
        if (state == null || state.CarriedObject == null)
        {
            action.result = "放置失败：当前未携带任何物品";
            Debug.LogWarning("[AME] Put 失败 -> 未携带物品");
            yield break;
        }

        // 悬停等待（模拟放置动作）
        yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 1f));

        // 分离物体
        GameObject obj = state.CarriedObject;
        string carriedName = state.CarriedItemName;

        obj.transform.SetParent(null);
        Vector3 dropPos = transform.position;
        dropPos.y = 0f;
        obj.transform.position = dropPos;
        obj.transform.localScale = _carriedOriginalScale;

        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = false;
        Collider col = obj.GetComponent<Collider>();
        if (col != null) col.enabled = true;

        // 更新 SmallNodeRegistry 中的位置
        SmallNodeRegistry.RegisterOrUpdate(new SmallNodeData
        {
            NodeId = carriedName,
            WorldPosition = dropPos,
            SceneObject = obj,
            IsDynamic = true,
            LastSeenTime = Time.time
        });

        // 清除携带状态
        state.CarriedItemName = null;
        state.CarriedObject = null;

        string displayName = string.IsNullOrWhiteSpace(action.targetName) ? carriedName : action.targetName;
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "put_done", $"放置 {displayName} 于 ({dropPos.x:F1},{dropPos.z:F1})");
        action.result = $"成功放置 {displayName}";
        Debug.Log($"[AME] Put 完成 -> {displayName} at {dropPos}");
    }

    private IEnumerator DoLand(AtomicAction action)
    {
        const float landingHeight = 0.2f; const float timeout = 15f; float elapsed = 0f;
        Vector3 holdPos = transform.position;
        while (Mathf.Abs(aerialMotion.TargetHeight - landingHeight) > 0.05f && elapsed < timeout)
        { aerialMotion.TargetHeight = Mathf.MoveTowards(aerialMotion.TargetHeight, landingHeight, 2f * Time.deltaTime); aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos); elapsed += Time.deltaTime; yield return null; }
        aerialMotion.TargetHeight = landingHeight; aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos);
        Debug.Log($"[AME] Land 完成，高度={transform.position.y:F2}m");
    }

    private IEnumerator DoTakeoff(AtomicAction action)
    {
        const float timeout = 15f; float elapsed = 0f;
        Vector3 holdPos = transform.position;
        while (Mathf.Abs(aerialMotion.TargetHeight - defaultTargetHeight) > 0.05f && elapsed < timeout)
        { aerialMotion.TargetHeight = Mathf.MoveTowards(aerialMotion.TargetHeight, defaultTargetHeight, 2f * Time.deltaTime); aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos); elapsed += Time.deltaTime; yield return null; }
        aerialMotion.TargetHeight = defaultTargetHeight; aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos);
        Debug.Log($"[AME] Takeoff 完成，高度={transform.position.y:F2}m");
    }

    private IEnumerator DoPatrol(AtomicAction action)
    {
        if (campusGrid == null || aerialMotion == null) { Debug.LogWarning("[AME] Patrol 缺少执行依赖"); yield break; }
        float totalDuration = action.duration > 0f ? action.duration : 60f;
        string resolvedTarget = action.targetName;
        if (string.IsNullOrWhiteSpace(resolvedTarget) && memoryModule != null)
        { resolvedTarget = memoryModule.GetHighestIdlenessArea(); if (!string.IsNullOrWhiteSpace(resolvedTarget)) Debug.Log($"[AME] Patrol 自动选区 -> {resolvedTarget}"); }

        List<Vector2Int> patrolCells = ResolvePatrolCells(resolvedTarget);
        if (patrolCells.Count == 0) { Debug.LogWarning("[AME] Patrol 没有可巡逻的格子"); yield break; }

        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "patrol_start", $"巡逻 {resolvedTarget}，候选格数={patrolCells.Count}，时长={totalDuration:F0}s");
        HashSet<Vector2Int> visited = new(); float elapsed = 0f; const float visitRadiusM = 8f;

        while (elapsed < totalDuration && visited.Count < patrolCells.Count)
        {
            Vector2Int currentCell = campusGrid.WorldToGrid(transform.position);
            if (!TryResolveNearestPathCell(currentCell, 4, out currentCell)) break;
            Vector2Int? frontier = FindNearestUnvisitedCell(currentCell, patrolCells, visited);
            if (!frontier.HasValue) break;
            Vector2Int frontierCell = frontier.Value;
            if (!TryResolveNearestPathCell(frontierCell, 4, out frontierCell)) { visited.Add(frontier.Value); continue; }

            List<Vector2Int> segment = campusGrid.FindPathAStar(currentCell, frontierCell) ?? new List<Vector2Int> { frontierCell };
            List<Vector3> segmentPath = BuildWorldPath(segment, aerialMotion.TargetHeight);
            if (segmentPath.Count == 0) break;

            currentPath = segmentPath; waypointIdx = 0;
            pathVisualizer?.ShowPath(currentPath, GetTeamColor());

            while (waypointIdx < currentPath.Count && elapsed < totalDuration)
            {
                Vector3 carrot = GetGridSafeCarrot(currentPath, waypointIdx, lookAheadDist);
                CommandMoveTarget(carrot, allowAvoidance: true);
                if (ShouldAdvanceWaypoint(currentPath[waypointIdx], waypointArrivalDistance + 0.4f)) waypointIdx++;
                if (isStuck) { TryHandleLocalObstacleStuck(currentPath[currentPath.Count - 1], aerialMotion.TargetHeight, out _); isStuck = false; }
                MarkVisitedCells(visited, patrolCells, visitRadiusM);
                elapsed += Time.deltaTime; yield return null;
            }
            MarkVisitedCells(visited, patrolCells, visitRadiusM);

            float dwell = UnityEngine.Random.Range(0.5f, 1.3f); float dwellElapsed = 0f;
            Vector3 holdPos = transform.position;
            while (dwellElapsed < dwell && elapsed < totalDuration)
            { aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos); MarkVisitedCells(visited, patrolCells, visitRadiusM); dwellElapsed += Time.deltaTime; elapsed += Time.deltaTime; yield return null; }
        }

        pathVisualizer?.ClearPath(); ResetAvoidanceState();
        float coverage = patrolCells.Count > 0 ? (float)visited.Count / patrolCells.Count * 100f : 100f;
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "patrol_done", $"巡逻完成 {resolvedTarget}，覆盖率 {coverage:F0}%，用时 {elapsed:F0}s");
        action.result = $"覆盖率:{Mathf.RoundToInt(coverage)}%";
        if (!string.IsNullOrWhiteSpace(resolvedTarget)) memoryModule?.RecordPatrolEvent(resolvedTarget, DateTime.Now);
    }

    private IEnumerator HoldPosition(float duration)
    {
        float elapsed = 0f; Vector3 holdPos = transform.position;
        while (elapsed < duration) { aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos); elapsed += Time.deltaTime; yield return null; }
    }

    private IEnumerator DoApproach(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 15f; float deadline = Time.time + duration; float approachDist = 3f;
        IntelligentAgent target = FindAgentById(action.targetAgentId);
        if (target == null) { action.result = "目标agent不存在"; yield break; }
        while (Time.time < deadline)
        {
            if (target == null) { action.result = "目标已消失"; yield break; }
            Vector3 targetPos = target.transform.position; targetPos.y = aerialMotion != null ? aerialMotion.TargetHeight : transform.position.y;
            float dist = HorizontalDistance(transform.position, targetPos);
            if (dist <= approachDist) { Vector3 offset = UnityEngine.Random.insideUnitSphere * 2f; offset.y = 0f; CommandMoveTarget(targetPos + offset, true); }
            else CommandMoveTarget(targetPos, true);
            yield return null;
        }
        action.result = $"逼近干扰 {action.targetAgentId} 完成";
    }

    private IEnumerator DoFlee(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 10f; float deadline = Time.time + duration; float safeDist = 25f;
        IntelligentAgent threat = FindAgentById(action.targetAgentId);
        if (threat == null) { action.result = "威胁目标不存在"; yield break; }
        while (Time.time < deadline)
        {
            if (threat == null) { action.result = "威胁已消失"; yield break; }
            Vector3 awayDir = transform.position - threat.transform.position; awayDir.y = 0f;
            if (awayDir.sqrMagnitude < 0.01f) awayDir = transform.forward;
            Vector3 fleeTarget = transform.position + awayDir.normalized * 10f;
            fleeTarget.y = aerialMotion != null ? aerialMotion.TargetHeight : transform.position.y;
            CommandMoveTarget(fleeTarget, true);
            if (HorizontalDistance(transform.position, threat.transform.position) > safeDist) { action.result = "已脱离威胁范围"; yield break; }
            yield return null;
        }
        action.result = "逃离完成";
    }

    private IntelligentAgent FindAgentById(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return null;
        foreach (var a in FindObjectsOfType<IntelligentAgent>()) if (a.Properties?.AgentID == agentId) return a;
        return null;
    }

    // ==================================================================
    // 导航与路径规划
    // ==================================================================

    private bool TryBuildPathToTargetName(string targetName, float height, out List<Vector3> path)
    {
        path = new List<Vector3>();
        if (!campusGrid.TryGetFeatureApproachCells(targetName, transform.position, out Vector2Int[] approachCells, 1) || approachCells.Length == 0) return false;
        Vector2Int startCell = campusGrid.WorldToGrid(transform.position);
        Vector2Int goalCell = approachCells[0];
        if (!TryResolveNearestPathCell(startCell, 4, out startCell) || !TryResolveNearestPathCell(goalCell, 4, out goalCell)) return false;
        List<Vector2Int> gridPath = campusGrid.FindPathAStar(startCell, goalCell) ?? new List<Vector2Int> { goalCell };
        path = BuildWorldPath(gridPath, height);
        return path.Count > 0;
    }

    private List<Vector3> BuildWorldPath(IEnumerable<Vector2Int> gridPath, float height)
    {
        return gridPath.Select(cell => { Vector3 w = campusGrid.GridToWorldCenter(cell.x, cell.y); w.y = height; return w; }).ToList();
    }

    /// <summary>
    /// 备选路径：当标准接近格不可用时，尝试用特征质心作为目标直飞。
    /// </summary>
    private bool TryBuildFallbackPathToFeature(string targetName, float height, out List<Vector3> path)
    {
        path = new List<Vector3>();
        if (campusGrid == null) return false;
        if (!campusGrid.TryResolveFeatureSpatialProfile(targetName, transform.position, out var profile))
            return false;

        Vector3 targetWorld = profile.anchorWorld;
        targetWorld.y = height;

        Vector2Int startCell = campusGrid.WorldToGrid(transform.position);
        Vector2Int goalCell = campusGrid.WorldToGrid(targetWorld);
        // 用 IsWalkable（而非 IsPathWalkable）放宽搜索，允许接近 clearance zone
        if (!campusGrid.TryFindNearestWalkable(startCell, 4, out startCell)) return false;
        if (!campusGrid.TryFindNearestWalkable(goalCell, 6, out goalCell)) return false;

        List<Vector2Int> gridPath = campusGrid.FindPathAStar(startCell, goalCell);
        if (gridPath == null || gridPath.Count == 0)
        {
            // A* 也失败，直接生成单点路径直飞
            path = new List<Vector3> { targetWorld };
            return true;
        }
        path = BuildWorldPath(gridPath, height);
        return path.Count > 0;
    }

    /// <summary>
    /// 尝试将目标名解析为 SmallNodeRegistry 中的感知小节点，生成直飞路径。
    /// </summary>
    private bool TryBuildDirectPathToSmallNode(string targetName, float height, out List<Vector3> path)
    {
        path = new List<Vector3>();
        if (string.IsNullOrWhiteSpace(targetName)) return false;
        if (!SmallNodeRegistry.TryGetNodeWorldPosition(targetName, transform.position, 60f, out Vector3 nodePos))
            return false;

        path.Add(new Vector3(nodePos.x, height, nodePos.z));
        return true;
    }

    // ==================================================================
    // ★★★ 避障核心：Context Steering（interest/danger/mask 三层评分） ★★★
    // ==================================================================

    private void CommandMoveTarget(Vector3 carrotTarget, bool allowAvoidance)
    {
        Vector3 targetToUse;
        if (allowAvoidance && obstacleLayers.value != 0)
            targetToUse = ComputeContextSteering(carrotTarget);
        else
            targetToUse = carrotTarget;

        targetToUse.y = aerialMotion.TargetHeight;
        aerialMotion.MoveTarget = targetToUse;
    }

    // ==================================================================
    // Context Steering 避障
    // ==================================================================

    /// <summary>
    /// Context Steering：根据 interest/danger/mask 三层评分选择最优移动方向。
    /// </summary>
    private Vector3 ComputeContextSteering(Vector3 carrotTarget)
    {
        CleanupYieldMemory();

        Vector3 pos = transform.position;
        Vector3 toTarget = carrotTarget - pos;
        toTarget.y = 0f;
        float distToTarget = toTarget.magnitude;
        if (distToTarget < 0.2f) return carrotTarget;

        Vector3 desiredDir = toTarget / distToTarget;

        // 清零
        System.Array.Clear(_interestMap, 0, SteerSlots);
        System.Array.Clear(_dangerMap, 0, SteerSlots);
        System.Array.Clear(_maskMap, 0, SteerSlots);

        // ── Step 1: Interest Map（目标方向） ──
        for (int i = 0; i < SteerSlots; i++)
        {
            Vector3 slotDir = SlotDirection(i);
            float dot = Vector3.Dot(slotDir, desiredDir);
            _interestMap[i] = Mathf.Max(0f, dot);
        }

        // ── Step 2: Danger Map（射线探测障碍） ──
        int layerMask = _dynamicObstacleMask;
        float maxDanger = 0f;
        for (int i = 0; i < SteerSlots; i++)
        {
            Vector3 slotDir = SlotDirection(i);
            if (TrySphereCastNonSelf(pos, probeRadius, slotDir, out RaycastHit hit, detectionRange, layerMask))
            {
                // 如果命中的是当前 MoveTo 目标小节点 → 不作为 danger（豁免）
                if (IsCurrentMoveTarget(hit.collider))
                    continue;

                float danger = 1f - Mathf.Clamp01(hit.distance / dangerDistance);
                _dangerMap[i] = Mathf.Max(_dangerMap[i], danger);
                if (danger > maxDanger) maxDanger = danger;

                // 邻近槽位衰减（模拟障碍物体积宽度）
                int prev = (i - 1 + SteerSlots) % SteerSlots;
                int next = (i + 1) % SteerSlots;
                _dangerMap[prev] = Mathf.Max(_dangerMap[prev], danger * 0.5f);
                _dangerMap[next] = Mathf.Max(_dangerMap[next], danger * 0.5f);
            }
        }

        // ── Step 3: Mask Map（网格不可走方向硬屏蔽） ──
        if (campusGrid != null)
        {
            for (int i = 0; i < SteerSlots; i++)
            {
                if (!IsDirectionGridSafe(pos, SlotDirection(i), 4f))
                    _maskMap[i] = true;
            }
        }

        // ── Step 4: 合成最终方向 ──
        float bestScore = float.MinValue;
        int bestSlot = 0;
        for (int i = 0; i < SteerSlots; i++)
        {
            if (_maskMap[i]) continue;
            float score = _interestMap[i] - _dangerMap[i];
            if (score > bestScore) { bestScore = score; bestSlot = i; }
        }

        Vector3 bestDir;
        if (bestScore == float.MinValue)
        {
            // 所有方向被 mask → 退化到安全方向搜索
            bestDir = FindSafestDirection(pos, desiredDir, layerMask);
        }
        else
        {
            // 邻近槽位加权平滑，消除离散跳变
            bestDir = SlotDirection(bestSlot);
            int prev = (bestSlot - 1 + SteerSlots) % SteerSlots;
            int next = (bestSlot + 1) % SteerSlots;
            if (!_maskMap[prev] && !_maskMap[next])
            {
                float scorePrev = _interestMap[prev] - _dangerMap[prev];
                float scoreNext = _interestMap[next] - _dangerMap[next];
                float totalW = bestScore + Mathf.Max(0f, scorePrev) + Mathf.Max(0f, scoreNext);
                if (totalW > 0.01f)
                {
                    bestDir = (bestDir * bestScore
                             + SlotDirection(prev) * Mathf.Max(0f, scorePrev)
                             + SlotDirection(next) * Mathf.Max(0f, scoreNext)) / totalW;
                    bestDir.y = 0f;
                    bestDir.Normalize();
                }
            }
        }

        // 生成目标点
        float targetDist = Mathf.Clamp(distToTarget, 3f, detectionRange * 0.8f);
        Vector3 steerTarget = pos + bestDir * targetDist;
        steerTarget.y = carrotTarget.y;

        // 可视化快照
        CurrentAvoidanceProbe = new AvoidanceProbeSnapshot
        {
            valid = true,
            origin = pos,
            resultVelocity = bestDir * targetDist,
            maxDanger = maxDanger,
            gridConstrained = bestScore == float.MinValue,
        };

        return ApplyCrowdOffset(pos, bestDir, steerTarget);
    }

    /// <summary>
    /// 将槽位索引转为世界方向向量（XZ 平面）。
    /// </summary>
    private static Vector3 SlotDirection(int slotIndex)
    {
        float angle = slotIndex * (360f / SteerSlots);
        return Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
    }

    /// <summary>
    /// 判断碰撞体是否为当前 MoveTo 目标小节点（避障豁免）。
    /// </summary>
    private bool IsCurrentMoveTarget(Collider col)
    {
        if (string.IsNullOrEmpty(_currentMoveTargetNodeId)) return false;
        if (col == null) return false;

        var info = col.GetComponentInParent<SmallNodeRuntimeInfo>();
        if (info == null) return false;

        // 尝试匹配 InstanceID
        string instanceId = col.gameObject.GetInstanceID().ToString();
        return _currentMoveTargetNodeId.Contains(instanceId);
    }

    /// <summary>
    /// Bresenham 逐格检查方向安全性。
    /// 用 IsWalkable（硬障碍）而非 IsPathSafe，因为避障是应急行为：
    /// 短暂穿越 clearance zone 比原地卡死要好。
    /// clearance zone 由 A* 路径规划层保证，避障层不应重复施加。
    /// </summary>
    private bool IsDirectionGridSafe(Vector3 pos, Vector3 dir, float checkDist)
    {
        if (campusGrid == null) return true;

        Vector2Int from = campusGrid.WorldToGrid(pos);
        Vector2Int to = campusGrid.WorldToGrid(pos + dir * checkDist);

        int x0 = from.x, z0 = from.y;
        int x1 = to.x, z1 = to.y;
        int dx = Mathf.Abs(x1 - x0), dz = Mathf.Abs(z1 - z0);
        int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1;
        int err = dx - dz;

        while (true)
        {
            if (campusGrid.IsInBounds(x0, z0) && !campusGrid.IsWalkable(x0, z0))
                return false;
            if (x0 == x1 && z0 == z1) break;

            int e2 = err * 2;
            if (e2 > -dz) { err -= dz; x0 += sx; }
            if (e2 < dx) { err += dx; z0 += sz; }
        }
        return true;
    }

    /// <summary>
    /// 所有方向被 mask 屏蔽时的退化逻辑：从候选方向中找最安全的。
    /// 限制最大角度 ±150（防止掉头后退），提高对齐权重。
    /// </summary>
    private Vector3 FindSafestDirection(Vector3 pos, Vector3 desiredDir, int layerMask)
    {
        float[] testAngles = { 0f, -30f, 30f, -60f, 60f, -90f, 90f, -120f, 120f, -150f, 150f };
        float bestScore = -1f;
        Vector3 bestDir = desiredDir;

        for (int i = 0; i < testAngles.Length; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, testAngles[i], 0f) * desiredDir;
            float clearance = detectionRange;
            if (TrySphereCastNonSelf(pos, probeRadius, dir, out RaycastHit hit, detectionRange, layerMask))
                clearance = hit.distance;

            float score = clearance;

            if (IsDirectionGridSafe(pos, dir, 4f))
                score += 5f;

            // 提高对齐权重，防止轻易选择后退方向
            float alignment = Mathf.Max(0f, Vector3.Dot(dir, desiredDir)) * detectionRange * 0.4f;
            score += alignment;

            // 使用 agent ID hash 偏好一侧，打破对称僵局
            float sideBias = GetStableSideBiasSign();
            if (Mathf.Sign(testAngles[i]) == sideBias)
                score += 2f;

            if (score > bestScore)
            {
                bestScore = score;
                bestDir = dir;
            }
        }

        return bestDir;
    }

    /// <summary>
    /// 叠加人群避障偏移。
    /// </summary>
    private Vector3 ApplyCrowdOffset(Vector3 pos, Vector3 moveDir, Vector3 target)
    {
        CrowdAvoidanceResult crowd = TryBuildCrowdAvoidance(pos, moveDir);
        if (crowd.shouldCreateBypass && crowd.avoidanceOffset.sqrMagnitude > 0.001f)
        {
            Vector3 crowdOffset = crowd.avoidanceOffset;
            crowdOffset.y = 0f;
            target += crowdOffset;
        }
        return target;
    }

    // ==================================================================
    // Agent-Agent 人群避障（保留）
    // ==================================================================

    private CrowdAvoidanceResult TryBuildCrowdAvoidance(Vector3 currentPos, Vector3 moveDir)
    {
        CrowdAvoidanceResult result = default;
        if (agentAvoidanceLayers.value == 0) return result;

        Collider[] nearbyColliders = Physics.OverlapSphere(currentPos, crowdAvoidanceRadius, agentAvoidanceLayers, QueryTriggerInteraction.Ignore);
        if (nearbyColliders == null || nearbyColliders.Length == 0) return result;

        Vector3 right = Vector3.Cross(Vector3.up, moveDir).normalized;
        float strongestRepulsion = 0f;

        foreach (Collider otherCollider in nearbyColliders)
        {
            if (IsSelfCollider(otherCollider)) continue;
            IntelligentAgent otherAgent = otherCollider.GetComponentInParent<IntelligentAgent>();
            if (otherAgent == null || otherAgent == agent) continue;

            Vector3 toOther = otherCollider.bounds.center - currentPos; toOther.y = 0f;
            float dist = toOther.magnitude;
            if (dist < 0.05f || dist > crowdAvoidanceRadius) continue;

            Vector3 dirToOther = toOther / dist;
            float forwardDot = Vector3.Dot(dirToOther, moveDir);
            if (forwardDot < -0.2f) continue;

            Vector3 pushDir = Vector3.ProjectOnPlane(-dirToOther, moveDir);
            if (pushDir.sqrMagnitude < 0.001f) pushDir = right * ResolveCrowdSideSign(otherAgent, right);
            pushDir.Normalize();

            float repulsion = Mathf.Clamp01(1f - dist / Mathf.Max(0.01f, crowdAvoidanceRadius)) * crowdAvoidanceStrength * Mathf.Lerp(0.75f, 1.15f, Mathf.Clamp01(forwardDot + 0.2f));
            if (repulsion <= 0.01f) continue;

            string otherId = otherAgent.Properties?.AgentID ?? otherAgent.name;
            string selfId = props?.AgentID ?? name;
            // P2-fix: 让步方(ID较大)获得更强位移主动避让，优先方(ID较小)轻微偏移
            if (string.CompareOrdinal(selfId, otherId) > 0) { repulsion *= 1.6f; yieldMemory[otherId] = Time.time + YieldDuration; }
            else if (yieldMemory.TryGetValue(otherId, out float yieldUntil) && yieldUntil > Time.time) repulsion *= 0.3f;

            result.hasNearbyAgents = true;
            result.nearbyCount++;
            result.avoidanceOffset += pushDir * repulsion;
            if (repulsion > strongestRepulsion) { strongestRepulsion = repulsion; result.selectedSide = Vector3.Dot(pushDir, right) >= 0f ? "right" : "left"; result.dominantAgentId = otherId; }
        }
        result.shouldCreateBypass = result.avoidanceOffset.sqrMagnitude > 0.04f || result.nearbyCount >= 2;
        return result;
    }

    private void CleanupYieldMemory()
    {
        yieldCleanupTimer += Time.deltaTime;
        if (yieldCleanupTimer < YieldMemoryCleanupInterval) return;
        yieldCleanupTimer = 0f;
        List<string> expired = null;
        foreach (var kv in yieldMemory) if (kv.Value < Time.time) { expired ??= new List<string>(); expired.Add(kv.Key); }
        if (expired != null) foreach (string key in expired) yieldMemory.Remove(key);
    }

    // ==================================================================
    // 卡住恢复
    // ==================================================================

    private bool TryHandleLocalObstacleStuck(Vector3 finalGoal, float height, out List<Vector3> replanned)
    {
        replanned = null;
        localStuckCount++;

        // 第 1 次：直接 A* 重规划
        if (localStuckCount <= 1)
        {
            Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住(×{localStuckCount})，A* 重规划");
            ResetAvoidanceState();
            if (TryReplanPath(finalGoal, height, out replanned)) { localStuckCount = 0; return true; }
        }

        // P3-fix: 第 2-3 次，判断卡住原因再决定策略
        if (localStuckCount <= 3)
        {
            bool isGridBlocked = IsCurrentPositionGridBlocked();
            if (!isGridBlocked)
            {
                // 周围网格可行走但物理碰撞阻塞 → 升高有效
                escapeAltitudeBoost += 3f;
                aerialMotion.TargetHeight = Mathf.Min(defaultTargetHeight + escapeAltitudeBoost, defaultTargetHeight + 10f);
                Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住(×{localStuckCount})，物理阻塞，升高至 {aerialMotion.TargetHeight:F1}m");
                AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "altitude_escape", $"升高 {escapeAltitudeBoost:F1}m 脱困");
                ResetAvoidanceState();
                if (TryReplanPath(finalGoal, aerialMotion.TargetHeight, out replanned)) return true;
            }
            else
            {
                // 周围网格被建筑阻挡 → 升高无效，直接跳航点
                Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住(×{localStuckCount})，网格阻塞，跳过升高直接跳航点");
            }
        }

        // 第 4 次+（或网格阻塞的 2-3 次）：跳过航点
        if (waypointIdx < currentPath.Count - 1)
        {
            waypointIdx++;
            Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住(×{localStuckCount})，跳过航点 -> [{waypointIdx}]");
        }
        return false;
    }

    /// <summary>检查当前位置周围是否被硬障碍（建筑实体）包围。</summary>
    private bool IsCurrentPositionGridBlocked()
    {
        if (campusGrid == null) return false;
        Vector2Int cur = campusGrid.WorldToGrid(transform.position);
        int blockedCount = 0;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                int x = cur.x + dx, z = cur.y + dz;
                if (campusGrid.IsInBounds(x, z) && !campusGrid.IsWalkable(x, z))
                    blockedCount++;
            }
        return blockedCount >= 5;
    }

    private bool TryReplanPath(Vector3 finalGoal, float height, out List<Vector3> replannedPath)
    {
        replannedPath = new List<Vector3>();
        if (campusGrid == null) return false;
        Vector2Int nowCell = campusGrid.WorldToGrid(transform.position);
        Vector2Int goalCell = campusGrid.WorldToGrid(finalGoal);
        if (!TryResolveNearestPathCell(nowCell, 4, out nowCell) || !TryResolveNearestPathCell(goalCell, 4, out goalCell)) return false;
        List<Vector2Int> newPath = campusGrid.FindPathAStar(nowCell, goalCell);
        if (newPath == null || newPath.Count == 0) return false;
        replannedPath = BuildWorldPath(newPath, height);
        return replannedPath.Count > 0;
    }

    private bool TryResolveNearestPathCell(Vector2Int requestedCell, int searchRadius, out Vector2Int resolvedCell)
    {
        resolvedCell = requestedCell;
        if (campusGrid == null) return false;
        if (!campusGrid.TryFindNearestWalkable(requestedCell, Mathf.Max(1, searchRadius), out Vector2Int safeCell)) return false;
        resolvedCell = safeCell;
        return true;
    }

    // ==================================================================
    // 工具方法
    // ==================================================================

    // P3-perf: 使用 NonAlloc 缓冲区避免每帧 GC 分配
    private static readonly RaycastHit[] _sphereCastBuffer = new RaycastHit[16];

    private bool TrySphereCastNonSelf(Vector3 origin, float radius, Vector3 direction, out RaycastHit closestHit, float maxDistance, int layerMask)
    {
        closestHit = default;
        if (layerMask == 0 || direction.sqrMagnitude < 0.001f || maxDistance <= 0.01f) return false;
        int count = Physics.SphereCastNonAlloc(origin, radius, direction.normalized, _sphereCastBuffer, maxDistance, layerMask, QueryTriggerInteraction.Ignore);
        if (count == 0) return false;
        bool found = false; float closestDistance = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Collider col = _sphereCastBuffer[i].collider;
            if (col == null || IsSelfCollider(col)) continue;
            // 仅对 Tree 标签的障碍物生效；行人/资源点等小节点不阻挡无人机
            if (!col.CompareTag("Tree") && !(_agentLayerIndex >= 0 && col.gameObject.layer == _agentLayerIndex)) continue;
            if (_sphereCastBuffer[i].distance < closestDistance) { closestDistance = _sphereCastBuffer[i].distance; closestHit = _sphereCastBuffer[i]; found = true; }
        }
        return found;
    }

    private bool IsSelfCollider(Collider c) => c != null && (c.transform == transform || c.transform.IsChildOf(transform));

    private void ResetAvoidanceState()
    {
        _currentMoveTargetNodeId = null;
        localStuckCount = 0;
        if (escapeAltitudeBoost > 0f && aerialMotion != null) { aerialMotion.TargetHeight = defaultTargetHeight; escapeAltitudeBoost = 0f; }
    }

    /// <summary>
    /// P3-fix: 退出避障后，将 waypointIdx 推进到当前位置前方的最近航点，
    /// 跳过已被绕过的航点，防止 Pure Pursuit carrot 回弹到障碍物背后。
    /// </summary>
    private void AdvanceWaypointsPastCurrentPos()
    {
        if (currentPath == null || currentPath.Count == 0) return;
        Vector3 pos = transform.position;

        // 找到离当前位置最近的航点
        float minDist = float.MaxValue;
        int closestIdx = waypointIdx;
        for (int i = waypointIdx; i < currentPath.Count; i++)
        {
            float d = HorizontalDistance(pos, currentPath[i]);
            if (d < minDist) { minDist = d; closestIdx = i; }
        }

        // 如果最近航点在当前索引之后，推进
        if (closestIdx > waypointIdx)
            waypointIdx = closestIdx;
    }

    private float ResolveCrowdSideSign(IntelligentAgent otherAgent, Vector3 right)
    {
        string selfId = props?.AgentID ?? name;
        string otherId = otherAgent?.Properties?.AgentID ?? otherAgent?.name ?? "";
        int cmp = string.CompareOrdinal(selfId, otherId);
        return cmp == 0 ? GetStableSideBiasSign() : cmp < 0 ? 1f : -1f;
    }

    private float GetStableSideBiasSign()
    {
        string selfId = props?.AgentID ?? name;
        return Mathf.Abs(selfId.GetHashCode()) % 2 == 0 ? 1f : -1f;
    }

    private bool ShouldAdvanceWaypoint(Vector3 waypoint, float baseArrivalDistance)
    {
        float planarSpeed = GetPlanarSpeed();
        float dynamicDist = Mathf.Clamp(baseArrivalDistance + planarSpeed * 0.35f, baseArrivalDistance, baseArrivalDistance * 2.4f);
        return HorizontalDistance(transform.position, waypoint) <= dynamicDist;
    }

    private float GetPlanarSpeed()
    {
        Vector3 v = aerialMotion != null ? aerialMotion.Velocity : Vector3.zero;
        return new Vector2(v.x, v.z).magnitude;
    }

    private Vector3 GetPurePursuitCarrot(List<Vector3> path, int fromIdx, float lookAhead)
    {
        if (path == null || path.Count == 0) return transform.position;
        float remaining = lookAhead;
        Vector3 pos = new Vector3(transform.position.x, path[fromIdx].y, transform.position.z);
        for (int i = fromIdx; i < path.Count; i++)
        {
            Vector3 wp = new Vector3(path[i].x, path[fromIdx].y, path[i].z);
            float seg = Vector3.Distance(pos, wp);
            if (remaining <= seg) return Vector3.Lerp(pos, wp, remaining / Mathf.Max(0.001f, seg));
            remaining -= seg; pos = wp;
        }
        return path[path.Count - 1];
    }

    /// <summary>
    /// 视线安全 carrot：如果 drone→carrot 直线穿过建筑格，逐步缩短 lookAhead 直到安全。
    /// 防止 Pure Pursuit 在建筑拐角处"切弯"飞进建筑墙。
    /// </summary>
    private Vector3 GetGridSafeCarrot(List<Vector3> path, int fromIdx, float lookAhead)
    {
        Vector3 carrot = GetPurePursuitCarrot(path, fromIdx, lookAhead);
        if (campusGrid == null) return carrot;

        Vector3 myPos = transform.position;

        // 检查 drone→carrot 直线是否穿过建筑
        if (IsLineGridClear(myPos, carrot))
            return carrot;

        // 穿过建筑了 → 逐步缩短 lookAhead
        for (float la = lookAhead * 0.5f; la >= 1f; la *= 0.5f)
        {
            carrot = GetPurePursuitCarrot(path, fromIdx, la);
            if (IsLineGridClear(myPos, carrot))
                return carrot;
        }

        // 最短 lookAhead 也不行 → 直接用下一个航点
        if (fromIdx < path.Count)
            return path[fromIdx];
        return carrot;
    }

    /// <summary>
    /// 检查两点之间的直线是否不穿过任何不可行走的网格（建筑等）。
    /// </summary>
    private bool IsLineGridClear(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        dir.y = 0f;
        float dist = dir.magnitude;
        if (dist < 0.5f) return true;
        return IsDirectionGridSafe(from, dir / dist, dist);
    }

    private Vector3 GetNoisyHoldPos(Vector3 anchor)
    {
        float t = Time.time;
        float dx = (Mathf.PerlinNoise(noiseX + t * holdDriftFreq, 0f) - 0.5f) * 2f * holdDriftAmp;
        float dz = (Mathf.PerlinNoise(noiseZ + t * holdDriftFreq, 0f) - 0.5f) * 2f * holdDriftAmp;
        return new Vector3(anchor.x + dx, aerialMotion.TargetHeight, anchor.z + dz);
    }

    private List<Vector2Int> ResolvePatrolCells(string resolvedTarget)
    {
        List<Vector2Int> cells = new();
        if (!string.IsNullOrWhiteSpace(resolvedTarget))
        {
            if (campusGrid.TryGetFeatureOccupiedCells(resolvedTarget, out Vector2Int[] occ))
                cells = occ.Where(c => campusGrid.IsWalkable(c.x, c.y)).ToList();
            if (cells.Count < 3) { campusGrid.TryGetFeatureApproachCells(resolvedTarget, transform.position, out Vector2Int[] app, 256); cells = app?.ToList() ?? cells; }
        }
        else
        {
            Vector2Int center = campusGrid.WorldToGrid(transform.position);
            for (int dx = -20; dx <= 20; dx++) for (int dz = -20; dz <= 20; dz++)
            { int x = center.x + dx, z = center.y + dz; if (campusGrid.IsInBounds(x, z) && campusGrid.IsWalkable(x, z)) cells.Add(new Vector2Int(x, z)); }
        }
        return cells;
    }

    private Vector2Int? FindNearestUnvisitedCell(Vector2Int cur, List<Vector2Int> cells, HashSet<Vector2Int> visited)
    {
        Vector2Int? best = null; float minD = float.MaxValue;
        foreach (var c in cells) { if (visited.Contains(c)) continue; float d = Vector2.Distance((Vector2)cur, (Vector2)c); if (d < minD) { minD = d; best = c; } }
        return best;
    }

    private void MarkVisitedCells(HashSet<Vector2Int> visited, List<Vector2Int> cells, float radiusM)
    {
        Vector2Int now = campusGrid.WorldToGrid(transform.position);
        float r = radiusM / Mathf.Max(0.1f, campusGrid.cellSize);
        foreach (var c in cells) if (Vector2.Distance((Vector2)now, (Vector2)c) <= r) visited.Add(c);
    }

    private void AbortCurrentExecutionState()
    {
        if (actionCoroutine != null) { StopCoroutine(actionCoroutine); actionCoroutine = null; }
        pathVisualizer?.ClearPath(); ResetAvoidanceState(); currentPath.Clear(); waypointIdx = 0;
    }

    /// <summary>
    /// 外部强制中断所有运动执行（由 ADM.AbortCurrentStep 调用）。
    /// 停止协程、清除路径和运动目标。
    /// </summary>
    public void ForceAbort()
    {
        AbortCurrentExecutionState();
        actionRunning = false;
        currentAction = null;
        if (aerialMotion != null) aerialMotion.MoveTarget = null;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

    private static Vector3 ParseOffsetFromParams(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Vector3(3f, 0f, 0f);
        string p = raw.ToLowerInvariant();
        if (p.Contains("前") || p.Contains("front")) return new Vector3(0f, 0f, 3f);
        if (p.Contains("后") || p.Contains("back"))  return new Vector3(0f, 0f, -3f);
        if (p.Contains("左") || p.Contains("left"))  return new Vector3(-3f, 0f, 0f);
        if (p.Contains("右") || p.Contains("right")) return new Vector3(3f, 0f, 0f);
        return new Vector3(3f, 0f, 0f);
    }

    private Color GetTeamColor()
    {
        if (props == null) return Color.white;
        return Color.HSVToRGB((props.TeamID * 0.618f) % 1f, 0.7f, 1f);
    }
}
