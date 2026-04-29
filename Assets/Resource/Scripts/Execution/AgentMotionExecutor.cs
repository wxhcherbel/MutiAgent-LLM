using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 行为执行层：
/// 1. 轮询 ADM，消费当前 AtomicAction。
/// 2. 将动作转换成路径跟随、悬停、跟踪等移动指令。
/// 3. 使用 前向射线扇 + 承诺式侧向避障 + 网格安全验证 做局部避障。
///
/// 避障核心原理（Bug Algorithm 变体）：
///   - 前方无障碍 → 直飞目标
///   - 前方有障碍 → 选择较空旷的一侧，承诺转向，直到前方再次畅通
///   - 转向前验证目标网格是否可行走，防止飞入禁飞区
///   - 不使用加权平均（容易产生局部极小值），而是明确的二选一决策
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

    // 探测射线：前方锥形，7 根，覆盖 ±60°
    private static readonly float[] ProbeAngles = { 0f, -20f, 20f, -40f, 40f, -60f, 60f };

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

    // ── 避障状态机 ──
    private bool _isAvoiding;                // 是否正在避障
    private int _committedSide;              // 承诺侧向：+1 右转，-1 左转
    private float _forwardClearTimer;        // 前方畅通持续计时（防止过早退出避障）
    private const float ClearConfirmTime = 0.4f;  // 前方需要畅通多久才退出避障

    // 卡住恢复
    private int localStuckCount;
    private float escapeAltitudeBoost;

    // Agent-Agent 让行记忆
    private readonly Dictionary<string, float> yieldMemory = new();
    private const float YieldDuration = 2.0f;
    private const float YieldMemoryCleanupInterval = 5f;
    private float yieldCleanupTimer;

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
        if (campusGrid == null || aerialMotion == null) { Debug.LogWarning("[AME] MoveTo 缺少执行依赖"); yield break; }

        Debug.Log($"[AME] {props?.AgentID ?? name} 开始 MoveTo -> {action.targetName}");
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "move_start", $"前往 {action.targetName}");

        if (!TryBuildPathToTargetName(action.targetName, aerialMotion.TargetHeight, out List<Vector3> path))
        { Debug.LogWarning($"[AME] MoveTo 找不到目标接近点：{action.targetName}"); yield break; }

        currentPath = path;
        waypointIdx = 0;
        pathVisualizer?.ShowPath(currentPath, GetTeamColor());

        const float waypointTimeout = 15f;
        float waypointTimer = 0f;

        while (waypointIdx < currentPath.Count)
        {
            Vector3 waypoint = currentPath[waypointIdx];
            Vector3 carrot = GetPurePursuitCarrot(currentPath, waypointIdx, lookAheadDist);
            CommandMoveTarget(carrot, allowAvoidance: true);

            waypointTimer += Time.deltaTime;
            if (ShouldAdvanceWaypoint(waypoint, waypointArrivalDistance))
            { waypointIdx++; waypointTimer = 0f; }
            else if (waypointTimer >= waypointTimeout)
            {
                Debug.LogWarning($"[AME] {action.targetName} 航点[{waypointIdx}] 超时");
                AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "waypoint_timeout", $"航点[{waypointIdx}] 超时");
                waypointIdx++; waypointTimer = 0f;
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
        ResetAvoidanceState();
        Debug.Log($"[AME] MoveTo 完成 -> {action.targetName}");
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

    private IEnumerator DoGet(AtomicAction action) { yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 1f)); Debug.Log($"[AME] Get 完成 -> {action.targetName}"); }
    private IEnumerator DoPut(AtomicAction action) { yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 1f)); Debug.Log($"[AME] Put 完成 -> {action.targetName}"); }

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
                Vector3 carrot = GetPurePursuitCarrot(currentPath, waypointIdx, lookAheadDist);
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

    // ==================================================================
    // ★★★ 避障核心：前向射线扇 + 承诺式侧向避障 ★★★
    // ==================================================================
    //
    //  原理（类似 Bug Algorithm）：
    //
    //  1. 沿 desiredDir 和两侧各 ±20°/±40°/±60° 共 7 根射线探测
    //  2. 如果中心射线（0°）在 dangerDistance 内命中障碍：
    //     a. 统计左侧总净空 vs 右侧总净空
    //     b. 选较大一侧，**承诺**转向该侧
    //     c. 承诺期间不再切换侧向（消除震荡）
    //  3. 承诺期间：
    //     - 持续检测前方是否畅通
    //     - 畅通持续 0.4s 以上 → 退出避障，恢复直飞
    //     - 转向角度与障碍物距离成反比：越近转越狠（30°~90°）
    //  4. 每次转向前，在 CampusGrid2D 上验证目标网格是否可行走
    //     如果不可行走 → 尝试另一侧 / 减小转向角度
    //  5. 拥挤分离（agent-agent）作为额外偏移叠加
    //
    // ==================================================================

    private void CommandMoveTarget(Vector3 carrotTarget, bool allowAvoidance)
    {
        Vector3 targetToUse;
        if (allowAvoidance && obstacleLayers.value != 0)
            targetToUse = ComputeAvoidedTarget(carrotTarget);
        else
            targetToUse = carrotTarget;

        targetToUse.y = aerialMotion.TargetHeight;
        aerialMotion.MoveTarget = targetToUse;
    }

    /// <summary>
    /// 核心避障：计算避障后的实际导航目标点。
    /// </summary>
    private Vector3 ComputeAvoidedTarget(Vector3 carrotTarget)
    {
        CleanupYieldMemory();

        Vector3 pos = transform.position;
        Vector3 toTarget = carrotTarget - pos;
        toTarget.y = 0f;
        float distToTarget = toTarget.magnitude;
        if (distToTarget < 0.2f) return carrotTarget;

        Vector3 desiredDir = toTarget / distToTarget;
        int layerMask = obstacleLayers.value | agentAvoidanceLayers.value;

        // ── 1. 前向锥形射线探测 ──
        float[] clearances = new float[ProbeAngles.Length];
        bool forwardBlocked = false;
        float closestForward = detectionRange;
        float leftClearanceSum = 0f;
        float rightClearanceSum = 0f;
        Vector3 forwardHitNormal = Vector3.zero;

        for (int i = 0; i < ProbeAngles.Length; i++)
        {
            Vector3 rayDir = Quaternion.Euler(0f, ProbeAngles[i], 0f) * desiredDir;
            float clearance = detectionRange;

            if (TrySphereCastNonSelf(pos, probeRadius, rayDir, out RaycastHit hit, detectionRange, layerMask))
            {
                clearance = hit.distance;
                if (i == 0) forwardHitNormal = hit.normal;
            }

            // 叠加网格检查：如果射线方向 4m 处是不可行走区域，视为有近距离障碍
            if (campusGrid != null)
            {
                Vector3 gridSample = pos + rayDir * Mathf.Min(clearance, 4f);
                Vector2Int cell = campusGrid.WorldToGrid(gridSample);
                if (campusGrid.IsInBounds(cell.x, cell.y) && !campusGrid.IsWalkable(cell.x, cell.y))
                    clearance = Mathf.Min(clearance, 1.5f);

                // 更近距离（2m）网格检查
                Vector3 nearSample = pos + rayDir * Mathf.Min(clearance, 2f);
                Vector2Int nearCell = campusGrid.WorldToGrid(nearSample);
                if (campusGrid.IsInBounds(nearCell.x, nearCell.y) && !campusGrid.IsWalkable(nearCell.x, nearCell.y))
                    clearance = Mathf.Min(clearance, 0.5f);
            }

            clearances[i] = clearance;

            // 中心射线判断是否需要避障
            if (i == 0 && clearance < dangerDistance) forwardBlocked = true;
            if (i == 0) closestForward = clearance;

            // 统计左右净空（左=负角度，右=正角度）
            if (ProbeAngles[i] < 0f) leftClearanceSum += clearance;
            if (ProbeAngles[i] > 0f) rightClearanceSum += clearance;
        }

        // ── 2. 避障状态机决策 ──

        if (!forwardBlocked && !_isAvoiding)
        {
            // 前方畅通且未在避障中 → 直飞目标
            _forwardClearTimer = 0f;
            return ApplyCrowdOffset(pos, desiredDir, carrotTarget);
        }

        if (forwardBlocked && !_isAvoiding)
        {
            // 前方阻塞，进入避障：选择较空旷的一侧并承诺
            _isAvoiding = true;
            _committedSide = PickAvoidanceSide(pos, desiredDir, leftClearanceSum, rightClearanceSum, forwardHitNormal);
            _forwardClearTimer = 0f;
            if (logAvoidance)
                Debug.Log($"[AME] {props?.AgentID ?? name} 开始避障，承诺侧={(_committedSide > 0 ? "右" : "左")}，前方净空={closestForward:F1}m");
        }

        if (_isAvoiding)
        {
            // 检查是否可以退出避障
            if (!forwardBlocked)
            {
                _forwardClearTimer += Time.deltaTime;
                if (_forwardClearTimer >= ClearConfirmTime)
                {
                    // 前方已畅通足够长时间，退出避障
                    _isAvoiding = false;
                    _forwardClearTimer = 0f;
                    if (logAvoidance)
                        Debug.Log($"[AME] {props?.AgentID ?? name} 退出避障，恢复直飞");
                    return ApplyCrowdOffset(pos, desiredDir, carrotTarget);
                }
            }
            else
            {
                _forwardClearTimer = 0f;
            }

            // ── 3. 计算避障转向 ──
            // 转向角度：障碍物越近，转角越大（30°~90°）
            float proximityRatio = 1f - Mathf.Clamp01(closestForward / dangerDistance);
            float steerAngle = Mathf.Lerp(30f, 90f, proximityRatio);

            Vector3 avoidDir = Quaternion.Euler(0f, _committedSide * steerAngle, 0f) * desiredDir;

            // ── 4. 网格安全验证 ──
            if (!IsDirectionGridSafe(pos, avoidDir, 4f))
            {
                // 主方向不安全，尝试减小角度
                avoidDir = Quaternion.Euler(0f, _committedSide * steerAngle * 0.5f, 0f) * desiredDir;
                if (!IsDirectionGridSafe(pos, avoidDir, 3f))
                {
                    // 还是不安全，尝试另一侧
                    avoidDir = Quaternion.Euler(0f, -_committedSide * steerAngle, 0f) * desiredDir;
                    if (!IsDirectionGridSafe(pos, avoidDir, 4f))
                    {
                        // 两侧都不安全，减速朝最安全的方向
                        avoidDir = FindSafestDirection(pos, desiredDir, layerMask);
                    }
                    else
                    {
                        // 切换到另一侧
                        _committedSide = -_committedSide;
                        if (logAvoidance)
                            Debug.Log($"[AME] {props?.AgentID ?? name} 网格约束，切换到另一侧={(_committedSide > 0 ? "右" : "左")}");
                    }
                }
            }

            // ── 5. 生成避障目标点 ──
            // 在避障方向上放一个固定距离的目标（不是偏移原目标！）
            float targetDist = Mathf.Max(3f, closestForward * 0.8f);
            Vector3 avoidTarget = pos + avoidDir * targetDist;
            avoidTarget.y = carrotTarget.y;

            // 障碍物极近时减速（通过缩短目标距离实现）
            if (closestForward < probeRadius * 3f)
            {
                avoidTarget = pos + avoidDir * Mathf.Max(1f, closestForward * 0.5f);
                avoidTarget.y = carrotTarget.y;
            }

            // 可视化
            CurrentAvoidanceProbe = new AvoidanceProbeSnapshot
            {
                valid = true,
                origin = pos,
                resultVelocity = avoidDir * targetDist,
                maxDanger = proximityRatio,
                gridConstrained = false,
            };

            return ApplyCrowdOffset(pos, avoidDir, avoidTarget);
        }

        return carrotTarget;
    }

    /// <summary>
    /// 选择避障侧向：综合净空统计 + 表面法线 + 网格安全性。
    /// </summary>
    private int PickAvoidanceSide(Vector3 pos, Vector3 desiredDir, float leftSum, float rightSum, Vector3 hitNormal)
    {
        // 1. 基础选择：哪侧净空更大
        int side = rightSum >= leftSum ? 1 : -1;

        // 2. 如果有表面法线信息，用法线判断更可靠
        if (hitNormal.sqrMagnitude > 0.01f)
        {
            Vector3 normalH = Vector3.ProjectOnPlane(hitNormal, Vector3.up);
            if (normalH.sqrMagnitude > 0.01f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, desiredDir).normalized;
                float normalSide = Vector3.Dot(normalH.normalized, right);
                // 法线指向右 → 障碍在左侧 → 应该右转（+1）
                if (Mathf.Abs(normalSide) > 0.3f)
                    side = normalSide > 0f ? 1 : -1;
            }
        }

        // 3. 网格验证：如果选择的侧向不可行走，切换
        Vector3 testDir = Quaternion.Euler(0f, side * 60f, 0f) * desiredDir;
        if (!IsDirectionGridSafe(pos, testDir, 4f))
        {
            Vector3 otherDir = Quaternion.Euler(0f, -side * 60f, 0f) * desiredDir;
            if (IsDirectionGridSafe(pos, otherDir, 4f))
                side = -side;
        }

        // 4. 稳定性：用 agent ID hash 打破完全对称的僵局
        if (Mathf.Abs(leftSum - rightSum) < 1f)
            side = GetStableSideBiasSign() > 0f ? 1 : -1;

        return side;
    }

    /// <summary>
    /// 检查某个方向在网格上是否安全（可行走）。
    /// </summary>
    private bool IsDirectionGridSafe(Vector3 pos, Vector3 dir, float checkDist)
    {
        if (campusGrid == null) return true;

        // 检查 2m 和 checkDist 处
        Vector3 nearPoint = pos + dir * 2f;
        Vector2Int nearCell = campusGrid.WorldToGrid(nearPoint);
        if (campusGrid.IsInBounds(nearCell.x, nearCell.y) && !campusGrid.IsWalkable(nearCell.x, nearCell.y))
            return false;

        Vector3 farPoint = pos + dir * checkDist;
        Vector2Int farCell = campusGrid.WorldToGrid(farPoint);
        if (campusGrid.IsInBounds(farCell.x, farCell.y) && !campusGrid.IsWalkable(farCell.x, farCell.y))
            return false;

        return true;
    }

    /// <summary>
    /// 两侧都不安全时，从多个候选方向中找最安全的。
    /// </summary>
    private Vector3 FindSafestDirection(Vector3 pos, Vector3 desiredDir, int layerMask)
    {
        float[] testAngles = { 0f, -30f, 30f, -60f, 60f, -90f, 90f, -120f, 120f, 180f };
        float bestClearance = -1f;
        Vector3 bestDir = desiredDir;

        for (int i = 0; i < testAngles.Length; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, testAngles[i], 0f) * desiredDir;
            float clearance = detectionRange;
            if (TrySphereCastNonSelf(pos, probeRadius, dir, out RaycastHit hit, detectionRange, layerMask))
                clearance = hit.distance;

            // 网格安全加成
            if (IsDirectionGridSafe(pos, dir, 4f))
                clearance += 5f;  // 网格安全的方向加大权重

            // 朝目标方向加成
            float alignment = Mathf.Max(0f, Vector3.Dot(dir, desiredDir)) * 2f;
            clearance += alignment;

            if (clearance > bestClearance)
            {
                bestClearance = clearance;
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
            if (string.CompareOrdinal(selfId, otherId) < 0) { repulsion *= 1.6f; yieldMemory[otherId] = Time.time + YieldDuration; }
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

        // 第 2-3 次：升高 + 重规划
        if (localStuckCount <= 3)
        {
            escapeAltitudeBoost += 3f;
            aerialMotion.TargetHeight = Mathf.Min(defaultTargetHeight + escapeAltitudeBoost, defaultTargetHeight + 10f);
            Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住(×{localStuckCount})，升高至 {aerialMotion.TargetHeight:F1}m");
            AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "altitude_escape", $"升高 {escapeAltitudeBoost:F1}m 脱困");
            ResetAvoidanceState();
            if (TryReplanPath(finalGoal, aerialMotion.TargetHeight, out replanned)) return true;
        }

        // 第 4 次+：跳过航点
        if (waypointIdx < currentPath.Count - 1)
        {
            waypointIdx++;
            Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住(×{localStuckCount})，跳过航点 -> [{waypointIdx}]");
        }
        return false;
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

    private bool TrySphereCastNonSelf(Vector3 origin, float radius, Vector3 direction, out RaycastHit closestHit, float maxDistance, int layerMask)
    {
        closestHit = default;
        if (layerMask == 0 || direction.sqrMagnitude < 0.001f || maxDistance <= 0.01f) return false;
        RaycastHit[] hits = Physics.SphereCastAll(origin, radius, direction.normalized, maxDistance, layerMask, QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0) return false;
        bool found = false; float closestDistance = float.MaxValue;
        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].collider == null || IsSelfCollider(hits[i].collider)) continue;
            if (hits[i].distance < closestDistance) { closestDistance = hits[i].distance; closestHit = hits[i]; found = true; }
        }
        return found;
    }

    private bool IsSelfCollider(Collider c) => c != null && (c.transform == transform || c.transform.IsChildOf(transform));

    private void ResetAvoidanceState()
    {
        _isAvoiding = false;
        _committedSide = 0;
        _forwardClearTimer = 0f;
        localStuckCount = 0;
        if (escapeAltitudeBoost > 0f && aerialMotion != null) { aerialMotion.TargetHeight = defaultTargetHeight; escapeAltitudeBoost = 0f; }
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
