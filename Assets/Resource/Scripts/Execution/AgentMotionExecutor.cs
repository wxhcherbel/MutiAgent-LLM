using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 行为执行层：
/// 1. 轮询 ADM，消费当前 AtomicAction。
/// 2. 将动作转换成路径跟随、悬停、跟踪等移动指令。
/// 3. 使用 LocalAvoidancePlanner 生成稳定的绕障承诺，而不是每帧临时挑一个看起来安全的方向。
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
    [SerializeField] private float detectionRange = 12f;
    [SerializeField] private float avoidanceBodyRadius = 0.9f;
    [SerializeField] private float avoidanceSafetyMargin = 0.8f;
    [SerializeField] private float obstacleMemoryTtl = 0.75f;
    [SerializeField] private float localBypassForwardDistance = 4.8f;
    [SerializeField] private float localRejoinDistance = 1.6f;

    // 物理射线实际使用的掩码（排除 Building 层，建筑由网格检查处理）
    private int _dynamicObstacleMask;

    private string _currentMoveTargetNodeId;  // 当前 MoveTo 目标小节点ID（用于避障豁免）

    // 局部规划器：对近场小障碍生成稳定的绕行承诺。
    private readonly LocalAvoidancePlanner _localAvoidancePlanner = new();
    private LocalAvoidancePlanner.LocalPlanState _localPlanState;
    private readonly Dictionary<string, LocalAvoidancePlanner.LocalObstacle> _obstacleMemory = new();
    private readonly List<LocalAvoidancePlanner.LocalObstacle> _activeLocalObstacles = new();
    private readonly List<Vector3> _localCorridorPoints = new();

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

    // 局部避障请求上层重规划
    private bool _avoidanceRequestsReplan;

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
        public string  mode;
        public string  committedSide;
        public string  obstacleId;
        public Vector3 rejoinPoint;
        public float   requestedSpeedScale;
        public string  debugReason;
    }

    public AvoidanceProbeSnapshot CurrentAvoidanceProbe { get; private set; }

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

        // 构建物理射线掩码：排除 Building 层。
        // 建筑是静态障碍，已由 A* 路径规划处理。
        // 物理探测只检测 Obstacle 层（命中后再用 tag 过滤，仅保留 Tree）。
        _dynamicObstacleMask = obstacleLayers.value;
        if (buildingLayer >= 0)
            _dynamicObstacleMask &= ~(1 << buildingLayer);

        Debug.Log($"[AME] {name} 避障层初始化: obstacleLayers={obstacleLayers.value}, buildingLayer={buildingLayer}, dynamicMask={_dynamicObstacleMask} (仅检测Tree标签)");
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
    // 动作执行
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
            // 备选策略：尝试解析为感知到的小节点（资源点/树木等），直飞
            if (TryBuildDirectPathToSmallNode(action.targetName, aerialMotion.TargetHeight, out path))
            {
                Debug.Log($"[AME] {props?.AgentID ?? name} 小节点直飞 -> {action.targetName}");
                _currentMoveTargetNodeId = action.targetName;
            }
            else
            {
                Debug.LogWarning($"[AME] MoveTo 找不到目标接近点：{action.targetName}（名称解析失败或该要素无可行走的接近格）");
                action.result = $"路径规划失败：目标 {action.targetName} 不可达，请换一个目标";
                AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "path_fail", $"目标 {action.targetName} 不可达");
                yield break;
            }
        }

        currentPath = path;
        waypointIdx = 0;
        pathVisualizer?.ShowPath(currentPath, GetTeamColor());

        float totalDist = HorizontalDistance(transform.position, currentPath[currentPath.Count - 1]);
        Debug.Log($"[AME] {props?.AgentID ?? name} 路径已建: {currentPath.Count} 航点, 直线距终点={totalDist:F1}m, 当前位置={transform.position}");

        const float waypointTimeout = 15f;
        float waypointTimer = 0f;

        while (waypointIdx < currentPath.Count)
        {
            Vector3 waypoint = currentPath[waypointIdx];
            Vector3 carrot = GetGridSafeCarrot(currentPath, waypointIdx, lookAheadDist);
            CommandMoveTarget(carrot, allowAvoidance: true);

            // 局部避障请求重规划（所有绕行候选均失败）
            if (_avoidanceRequestsReplan)
            {
                _avoidanceRequestsReplan = false;
                Vector3 replanGoal = currentPath[currentPath.Count - 1];
                if (TryReplanPath(replanGoal, aerialMotion.TargetHeight, out List<Vector3> avoidReplanned))
                {
                    currentPath = avoidReplanned; waypointIdx = 0; waypointTimer = 0f;
                    pathVisualizer?.ShowPath(currentPath, GetTeamColor());
                    ResetAvoidanceState();
                    Debug.Log($"[AME] {props?.AgentID ?? name} 避障触发 A* 重规划");
                }
            }

            waypointTimer += Time.deltaTime;
            if (ShouldAdvanceWaypoint(waypoint, waypointArrivalDistance))
            { waypointIdx++; waypointTimer = 0f; }
            else if (waypointTimer >= waypointTimeout)
            {
                Debug.LogWarning($"[AME] {action.targetName} 航点[{waypointIdx}] 超时");
                AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "waypoint_timeout", $"航点[{waypointIdx}] 超时");
                Vector3 finalGoal = currentPath[currentPath.Count - 1];
                if (TryReplanPath(finalGoal, aerialMotion.TargetHeight, out List<Vector3> timeoutReplanned))
                {
                    currentPath = timeoutReplanned; waypointIdx = 0;
                    pathVisualizer?.ShowPath(currentPath, GetTeamColor());
                }
                else
                {
                    waypointIdx++;
                }
                waypointTimer = 0f;
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
        float finalDistToApproach = HorizontalDistance(transform.position, finalPoint);
        Debug.Log($"[AME] MoveTo 完成 -> {action.targetName}, 距接近点={finalDistToApproach:F1}m, 位置={transform.position}");
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "arrive", $"到达 {action.targetName}");

        if (finalDistToApproach <= finalArrivalDistance)
            action.result = $"已到达{action.targetName}";
        else
            action.result = $"接近{action.targetName}但未完全到达(距接近点{finalDistToApproach:F1}m)";
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
        string raw = string.IsNullOrWhiteSpace(action.actionParams) ? "signal" : action.actionParams;
        string recipient = string.IsNullOrWhiteSpace(action.targetAgentId) ? "all" : action.targetAgentId;
        string agentId = props?.AgentID ?? "?";

        if (raw.StartsWith("memo:", StringComparison.OrdinalIgnoreCase))
        {
            string body = raw.Substring(5).Trim();
            if (memoryModule != null)
            {
                var pm = GetComponent<PlanningModule>();
                string msnId  = pm?.GetCurrentMissionId() ?? "";
                string slotId = pm?.GetCurrentSlotId() ?? "";
                var step = pm?.GetCurrentStep();
                memoryModule.RememberProgress(
                    missionId: msnId,
                    slotId:    slotId,
                    stepLabel: step?.text ?? "",
                    summary:   body,
                    targetRef: step?.targetName ?? "");
            }
            action.result = $"已写入工作记忆: {body}";
            Debug.Log($"[AME] Signal(memo) {agentId}: {body}");
        }
        else if (raw.StartsWith("whiteboard:", StringComparison.OrdinalIgnoreCase))
        {
            string body = raw.Substring(11).Trim();
            var pm = GetComponent<PlanningModule>();
            string groupId = pm?.GetGroupId();
            if (!string.IsNullOrWhiteSpace(groupId) && SharedWhiteboard.Instance != null)
            {
                SharedWhiteboard.Instance.WriteEntry(groupId, new WhiteboardEntry
                {
                    agentId      = agentId,
                    constraintId = "_signal",
                    entryType    = WhiteboardEntryType.StatusUpdate,
                    status       = 0,
                    progress     = body,
                });
            }
            action.result = $"已写入共享白板: {body}";
            Debug.Log($"[AME] Signal(whiteboard) {agentId}: {body}");
        }
        else
        {
            string content = raw.StartsWith("broadcast:", StringComparison.OrdinalIgnoreCase)
                ? raw.Substring(10).Trim()
                : raw;
            CommunicationModule comm = GetComponent<CommunicationModule>();
            if (comm != null)
            {
                if (recipient == "all")
                    foreach (var oa in FindObjectsOfType<IntelligentAgent>())
                    { string rid = oa.Properties?.AgentID; if (!string.IsNullOrWhiteSpace(rid) && rid != agentId) comm.SendMessage(rid, MessageType.StatusUpdate, content); }
                else
                    comm.SendMessage(recipient, MessageType.StatusUpdate, content);
            }
            action.result = $"已广播: {content}";
            Debug.Log($"[AME] Signal(broadcast) -> {recipient}: {content}");
        }
        yield break;
    }

    private IEnumerator DoGet(AtomicAction action)
    {
        string targetName = action.targetName;
        var state = agent?.CurrentState;

        if (state != null && state.CarriedObject != null)
        {
            action.result = $"拾取失败：已携带 {state.CarriedItemName}，需先放下";
            Debug.LogWarning($"[AME] Get 失败 -> 已携带 {state.CarriedItemName}");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(targetName))
        {
            action.result = "拾取失败：未指定目标名称";
            Debug.LogWarning("[AME] Get 失败 -> 未指定 targetName");
            yield break;
        }

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

        float dist = Vector3.Distance(transform.position, targetObj.transform.position);
        if (dist > 8f)
        {
            action.result = $"拾取失败：{targetName} 距离过远({dist:F1}m > 8m)，请先 MoveTo 靠近";
            Debug.LogWarning($"[AME] Get 失败 -> {targetName} 距离 {dist:F1}m");
            yield break;
        }

        yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 1f));

        _carriedOriginalScale = targetObj.transform.localScale;

        Rigidbody rb = targetObj.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        Collider col = targetObj.GetComponent<Collider>();
        if (col != null) col.enabled = false;

        targetObj.transform.SetParent(transform);
        targetObj.transform.localPosition = new Vector3(0f, -1.2f, 0f);
        targetObj.transform.localRotation = Quaternion.identity;

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

        if (state == null || state.CarriedObject == null)
        {
            action.result = "放置失败：当前未携带任何物品";
            Debug.LogWarning("[AME] Put 失败 -> 未携带物品");
            yield break;
        }

        yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 1f));

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

        SmallNodeRegistry.RegisterOrUpdate(new SmallNodeData
        {
            NodeId = carriedName,
            WorldPosition = dropPos,
            SceneObject = obj,
            IsDynamic = true,
            LastSeenTime = Time.time
        });

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
    // ★★★ 避障核心：LocalAvoidancePlanner（承诺式绕行） ★★★
    // ==================================================================

    private void CommandMoveTarget(Vector3 carrotTarget, bool allowAvoidance)
    {
        Vector3 targetToUse = carrotTarget;
        float speedScale = 1f;

        if (allowAvoidance && obstacleLayers.value != 0)
        {
            ScanNearbyObstacles();
            BuildCorridorFromPath();

            var ctx = new LocalAvoidancePlanner.LocalPlanContext
            {
                position = transform.position,
                velocity = aerialMotion != null ? aerialMotion.Velocity : Vector3.zero,
                desiredTarget = carrotTarget,
                corridorPoints = _localCorridorPoints,
                obstacles = _activeLocalObstacles,
                bodyRadius = avoidanceBodyRadius,
                safetyMargin = avoidanceSafetyMargin,
                probeDistance = detectionRange,
                candidateForwardDistance = localBypassForwardDistance,
                rejoinDistance = localRejoinDistance,
                now = Time.time,
                isSegmentWalkable = IsSegmentGridWalkable,
            };

            var result = _localAvoidancePlanner.Plan(ref _localPlanState, ctx);

            if (result.valid)
            {
                targetToUse = result.targetPoint;
                speedScale = result.requestedSpeedScale;

                if (result.shouldAdvanceWaypoints)
                    AdvanceWaypointsPastCurrentPos();

                _avoidanceRequestsReplan = result.shouldReplan;
            }

            CurrentAvoidanceProbe = new AvoidanceProbeSnapshot
            {
                valid = true,
                origin = transform.position,
                resultVelocity = targetToUse - transform.position,
                maxDanger = result.nearestClearance < 1f ? 1f - Mathf.Clamp01(result.nearestClearance) : 0f,
                gridConstrained = false,
                mode = result.mode.ToString(),
                committedSide = result.committedSide.ToString(),
                obstacleId = result.obstacleId ?? "",
                rejoinPoint = result.rejoinPoint,
                requestedSpeedScale = speedScale,
                debugReason = result.debugReason ?? "",
            };
        }

        targetToUse.y = aerialMotion.TargetHeight;
        aerialMotion.RequestedSpeedScale = speedScale;
        aerialMotion.MoveTarget = targetToUse;
    }

    // ==================================================================
    // 障碍物扫描与走廊构建
    // ==================================================================

    private static readonly Collider[] _overlapBuffer = new Collider[32];

    /// <summary>
    /// OverlapSphere 探测 Tree 标签障碍，维护 _obstacleMemory（带过期清理），输出到 _activeLocalObstacles。
    /// </summary>
    private void ScanNearbyObstacles()
    {
        // 清理过期障碍
        List<string> expired = null;
        foreach (var kv in _obstacleMemory)
            if (kv.Value.expiryTime < Time.time)
            { expired ??= new List<string>(); expired.Add(kv.Key); }
        if (expired != null) foreach (var key in expired) _obstacleMemory.Remove(key);

        // OverlapSphere 探测
        int count = Physics.OverlapSphereNonAlloc(transform.position, detectionRange, _overlapBuffer, _dynamicObstacleMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider col = _overlapBuffer[i];
            if (col == null || IsSelfCollider(col)) continue;
            if (!col.CompareTag("Tree")) continue;
            if (IsCurrentMoveTarget(col)) continue;

            string id = col.gameObject.GetInstanceID().ToString();
            Bounds bounds = col.bounds;
            float radius = Mathf.Max(bounds.extents.x, bounds.extents.z);

            _obstacleMemory[id] = new LocalAvoidancePlanner.LocalObstacle
            {
                obstacleId = id,
                center = new Vector3(bounds.center.x, transform.position.y, bounds.center.z),
                radius = radius,
                expiryTime = Time.time + obstacleMemoryTtl,
                sourceCollider = col,
            };
        }

        // 填充活跃列表
        _activeLocalObstacles.Clear();
        foreach (var kv in _obstacleMemory)
            _activeLocalObstacles.Add(kv.Value);
    }

    /// <summary>
    /// 从当前路径提取走廊锚点（未来若干航点），供 LocalAvoidancePlanner 判定绕行方向和重接入点。
    /// </summary>
    private void BuildCorridorFromPath()
    {
        _localCorridorPoints.Clear();
        if (currentPath == null || currentPath.Count == 0) return;

        for (int i = waypointIdx; i < currentPath.Count && i < waypointIdx + 5; i++)
            _localCorridorPoints.Add(currentPath[i]);
    }

    /// <summary>
    /// isSegmentWalkable 回调：检查两点之间的直线是否不穿过不可行走的网格。
    /// </summary>
    private bool IsSegmentGridWalkable(Vector3 from, Vector3 to, float bodyRadius)
    {
        return IsLineGridClear(from, to);
    }

    // ==================================================================
    // 网格安全检查
    // ==================================================================

    /// <summary>
    /// Bresenham 逐格检查方向上的网格可行走性（包含建筑缓冲区）。
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
    /// 判断碰撞体是否为当前 MoveTo 目标小节点（避障豁免）。
    /// </summary>
    private bool IsCurrentMoveTarget(Collider col)
    {
        if (string.IsNullOrEmpty(_currentMoveTargetNodeId)) return false;
        if (col == null) return false;

        var info = col.GetComponentInParent<SmallNodeRuntimeInfo>();
        if (info == null) return false;

        string instanceId = col.gameObject.GetInstanceID().ToString();
        return _currentMoveTargetNodeId.Contains(instanceId);
    }

    // ==================================================================
    // 工具方法
    // ==================================================================

    private bool IsSelfCollider(Collider c) => c != null && (c.transform == transform || c.transform.IsChildOf(transform));

    private void ResetAvoidanceState()
    {
        _currentMoveTargetNodeId = null;
        _localPlanState.Clear();
        _obstacleMemory.Clear();
        _activeLocalObstacles.Clear();
        if (aerialMotion != null)
            aerialMotion.RequestedSpeedScale = 1f;
    }

    /// <summary>
    /// 退出避障后，将 waypointIdx 推进到当前位置前方的最近航点，
    /// 跳过已被绕过的航点，防止 Pure Pursuit carrot 回弹到障碍物背后。
    /// </summary>
    private void AdvanceWaypointsPastCurrentPos()
    {
        if (currentPath == null || currentPath.Count == 0) return;
        Vector3 pos = transform.position;

        float minDist = float.MaxValue;
        int closestIdx = waypointIdx;
        for (int i = waypointIdx; i < currentPath.Count; i++)
        {
            float d = HorizontalDistance(pos, currentPath[i]);
            if (d < minDist) { minDist = d; closestIdx = i; }
        }

        if (closestIdx > waypointIdx)
            waypointIdx = closestIdx;
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

        if (IsLineGridClear(myPos, carrot))
            return carrot;

        for (float la = lookAhead * 0.5f; la >= 1f; la *= 0.5f)
        {
            carrot = GetPurePursuitCarrot(path, fromIdx, la);
            if (IsLineGridClear(myPos, carrot))
                return carrot;
        }

        if (fromIdx < path.Count)
            return path[fromIdx];
        return carrot;
    }

    /// <summary>
    /// 检查两点之间的直线是否不穿过任何不可行走的网格。
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
