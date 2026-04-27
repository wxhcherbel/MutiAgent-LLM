using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 行为执行层：
/// 1. 轮询 ADM，消费当前 AtomicAction。
/// 2. 将动作转换成路径跟随、悬停、跟踪等移动指令。
/// 3. 在执行层消费局部障碍碰撞信息，为非 A* 网格障碍生成临时绕行点。
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

    [Header("局部避障")]
    [SerializeField] private LayerMask obstacleLayers;
    [SerializeField] private LayerMask agentAvoidanceLayers;
    [SerializeField] private float obstacleForwardCheckDistance = 12f;
    [SerializeField] private float obstacleSideCheckDistance = 7f;
    [SerializeField] private float obstacleProbeRadius = 0.8f;
    [SerializeField] private float avoidanceForceStrength = 6f;
    [SerializeField] private float obstacleResumeDistance = 1.4f;
    [SerializeField] private float obstacleRetryHoldSeconds = 0.8f;
    [SerializeField] private float crowdAvoidanceRadius = 3.2f;
    [SerializeField] private float crowdAvoidanceStrength = 4.6f;
    [SerializeField] private float stuckEscapeDistance = 4.8f;
    [SerializeField] private float stuckEscapeDuration = 1.2f;
    [SerializeField] private int maxStaticBypassRetries = 2;
    [SerializeField] private bool logAvoidance = true;

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

    private bool hasBypassTarget;
    private Vector3 bypassTarget;
    private float obstacleHoldUntil;
    private Collider activeAvoidanceCollider;
    private string activeAvoidanceSide = "";
    private Vector3 forcedEscapeTarget;
    private float forcedEscapeUntil;
    private int localAvoidanceRetryCount;

    private static readonly float[] FanProbeAngles = { 0f, -15f, 15f, -25f, 25f, -35f, 35f, -50f, 50f, -65f, 65f, -80f, 80f, -100f, 100f, -130f, 130f, 160f, -160f, 180f };

    // 上一次 ResolveNavigationTarget 的探测结果（供避障逻辑 + 可视化使用）
    private struct ProbeFrame
    {
        public bool valid;
        public Vector3 origin;
        public Vector3 moveDir;
        public bool hitMid;   public float midDist;
        public bool hitLeft;  public float leftDist;
        public bool hitRight; public float rightDist;
    }
    private ProbeFrame lastProbe;

    /// <summary>
    /// 单帧实时避障计算结果。
    /// 说明：由 SphereCast 直接产出，用于决定是否立即生成局部绕行点。
    /// </summary>
    private struct RealtimeAvoidanceResult
    {
        public bool hasObstacle;
        public bool shouldCreateBypass;
        public float forwardDistance;
        public float leftDistance;
        public float rightDistance;
        public Vector3 avoidanceOffset;
        public Vector3 bypassDirection;
        public Collider blockingCollider;
        public string selectedSide;
        public bool usedFanSearch;
    }

    /// <summary>
    /// 感知库预判避障结果。
    /// 说明：优先使用共享小节点注册表，提前对前方已知障碍做侧向修正。
    /// </summary>
    private struct PerceptionAvoidanceResult
    {
        public bool hasObstacle;
        public bool shouldCreateBypass;
        public int candidateCount;
        public float strongestRepulsion;
        public Vector3 avoidanceOffset;
        public string selectedSide;
        public string strongestNodeId;
    }

    /// <summary>
    /// 近距离拥挤分离结果。
    /// 说明：用于解决 agent 与 agent 在狭窄区域对顶、擦碰和并行挤压的问题。
    /// </summary>
    private struct CrowdAvoidanceResult
    {
        public bool hasNearbyAgents;
        public bool shouldCreateBypass;
        public int nearbyCount;
        public Vector3 avoidanceOffset;
        public string selectedSide;
        public string dominantAgentId;
    }

    /// <summary>
    /// 避障探测数据快照（供 PerceptionVisualizer 读取，每帧更新）。
    /// </summary>
    public struct AvoidanceProbeSnapshot
    {
        public bool    valid;
        public Vector3 origin;
        public Vector3 forwardDir;
        public Vector3 leftDir;
        public Vector3 rightDir;
        public bool    hitForward;  public float forwardDist;
        public bool    hitLeft;     public float leftDist;
        public bool    hitRight;    public float rightDist;
        public float   maxForwardDist;
        public float   maxSideDist;
    }

    /// <summary>当前帧的避障探测快照，PerceptionVisualizer 每帧读取。</summary>
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
        {
            aerialMotion.MaxSpeed = props.MaxSpeed;
        }

        defaultTargetHeight = aerialMotion.TargetHeight;
        stuckCheckPos = transform.position;

        noiseX = UnityEngine.Random.Range(0f, 100f);
        noiseZ = UnityEngine.Random.Range(0f, 100f);

        // 优化层级初始化：如果未指定，默认包含 Obstacle 层，若不存在则尝试包含 Default
        if (obstacleLayers.value == 0)
        {
            int obstacleLayer = LayerMask.NameToLayer("Obstacle");
            if (obstacleLayer >= 0)
            {
                obstacleLayers = 1 << obstacleLayer;
            }
            else
            {
                // 如果没有 Obstacle 层，默认开启 Default 层以防万一，或者提醒用户
                Debug.LogWarning($"[AME] {name}: 未找到 'Obstacle' 层，避障模块可能失效。请确保小节点在正确层级。");
                obstacleLayers = 1 << 0; // Default layer
            }
        }

        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer >= 0)
        {
            obstacleLayers |= 1 << buildingLayer;
        }
        else
        {
            Debug.LogWarning($"[AME] {name}: 未找到 'Building' 层，大型建筑将无法通过 Building Layer 参与避障。");
        }

        if (agentAvoidanceLayers.value == 0)
        {
            int agentLayer = LayerMask.NameToLayer("Agent");
            if (agentLayer >= 0)
            {
                agentAvoidanceLayers = 1 << agentLayer;
            }
        }
    }

    private void Update()
    {
        UpdateStuckDetection();
        PollADM();

        // 不在运动时清除避障探测快照，避免可视化残留
        if (!actionRunning)
            CurrentAvoidanceProbe = default;
    }

    private void PollADM()
    {
        if (adm == null)
        {
            return;
        }

        AtomicAction next = adm.GetCurrentAction();
        if (next == null)
        {
            if (!actionRunning && aerialMotion != null)
            {
                aerialMotion.MoveTarget = null;
            }
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
        if (stuckTimer < stuckCheckInterval)
        {
            return;
        }

        float moved = HorizontalDistance(transform.position, stuckCheckPos);
        isStuck = moved < stuckMoveThreshold;
        if (isStuck)
        {
            Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住，{stuckCheckInterval:F1}s 内仅移动 {moved:F2}m");
        }

        stuckCheckPos = transform.position;
        stuckTimer = 0f;
    }

    private IEnumerator ExecuteAction(AtomicAction action)
    {
        switch (action.type)
        {
            case AtomicActionType.MoveTo:
                yield return StartCoroutine(DoMoveTo(action));
                break;
            case AtomicActionType.Wait:
                yield return StartCoroutine(DoWait(action));
                break;
            case AtomicActionType.Track:
                yield return StartCoroutine(DoTrack(action));
                break;
            case AtomicActionType.Signal:
                yield return StartCoroutine(DoSignal(action));
                break;
            case AtomicActionType.Get:
                yield return StartCoroutine(DoGet(action));
                break;
            case AtomicActionType.Put:
                yield return StartCoroutine(DoPut(action));
                break;
            case AtomicActionType.Land:
                yield return StartCoroutine(DoLand(action));
                break;
            case AtomicActionType.Takeoff:
                yield return StartCoroutine(DoTakeoff(action));
                break;
            case AtomicActionType.Patrol:
                yield return StartCoroutine(DoPatrol(action));
                break;
            default:
                Debug.LogWarning($"[AME] 未知动作类型 {action.type}，跳过执行");
                break;
        }

        actionRunning = false;
        adm?.CompleteCurrentAction();
    }

    private IEnumerator DoMoveTo(AtomicAction action)
    {
        if (campusGrid == null || aerialMotion == null)
        {
            Debug.LogWarning("[AME] MoveTo 缺少执行依赖，跳过");
            yield break;
        }

        Debug.Log($"[AME] {props?.AgentID ?? name} 开始 MoveTo -> {action.targetName}");
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "move_start", $"前往 {action.targetName}");

        if (!TryBuildPathToTargetName(action.targetName, aerialMotion.TargetHeight, out List<Vector3> path))
        {
            Debug.LogWarning($"[AME] MoveTo 找不到目标接近点：{action.targetName}");
            yield break;
        }

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
            {
                waypointIdx++;
                waypointTimer = 0f;
            }
            else if (waypointTimer >= waypointTimeout)
            {
                Debug.LogWarning($"[AME] {action.targetName} 航点[{waypointIdx}] 超时，dist={HorizontalDistance(transform.position, waypoint):F1}m");
                AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "waypoint_timeout", $"航点[{waypointIdx}] 超时");
                waypointIdx++;
                waypointTimer = 0f;
            }

            if (isStuck)
            {
                if (TryHandleLocalObstacleStuck(currentPath[Mathf.Min(waypointIdx, currentPath.Count - 1)]))
                {
                    waypointTimer = 0f;
                }
                else
                {
                    Vector3 finalGoal = currentPath[currentPath.Count - 1];
                    if (TryReplanPath(finalGoal, aerialMotion.TargetHeight, out List<Vector3> replanned))
                    {
                        currentPath = replanned;
                        waypointIdx = 0;
                        waypointTimer = 0f;
                        pathVisualizer?.ShowPath(currentPath, GetTeamColor());
                        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "obstacle_replan", "局部绕障失败，触发 A* 重规划");
                    }
                }

                isStuck = false;
            }

            yield return null;
        }

        Vector3 finalPoint = currentPath[currentPath.Count - 1];
        float finalTimer = 0f;
        while (HorizontalDistance(transform.position, finalPoint) > finalArrivalDistance && finalTimer < 6f)
        {
            CommandMoveTarget(finalPoint, allowAvoidance: true);
            finalTimer += Time.deltaTime;
            yield return null;
        }

        pathVisualizer?.ClearPath();
        ResetAvoidanceState(false, string.Empty);
        Debug.Log($"[AME] MoveTo 完成 -> {action.targetName}");
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "arrive", $"到达 {action.targetName}");
    }

    private IEnumerator DoWait(AtomicAction action)
    {
        yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 2f));
    }

    private IEnumerator DoTrack(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 10f;
        float elapsed = 0f;
        IntelligentAgent target = null;

        if (!string.IsNullOrWhiteSpace(action.targetAgentId))
        {
            foreach (IntelligentAgent candidate in FindObjectsOfType<IntelligentAgent>())
            {
                if (candidate.Properties?.AgentID == action.targetAgentId)
                {
                    target = candidate;
                    break;
                }
            }
        }

        if (target == null)
        {
            Debug.LogWarning($"[AME] Track 找不到目标智能体：{action.targetAgentId}");
        }

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
            {
                foreach (IntelligentAgent otherAgent in FindObjectsOfType<IntelligentAgent>())
                {
                    string receiverId = otherAgent.Properties?.AgentID;
                    if (!string.IsNullOrWhiteSpace(receiverId) && receiverId != props?.AgentID)
                    {
                        comm.SendMessage(receiverId, MessageType.StatusUpdate, content);
                    }
                }
            }
            else
            {
                comm.SendMessage(recipient, MessageType.StatusUpdate, content);
            }
        }

        Debug.Log($"[AME] Signal -> {recipient}: {content}");
        yield break;
    }

    private IEnumerator DoGet(AtomicAction action)
    {
        yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 1f));
        Debug.Log($"[AME] Get 完成 -> {action.targetName}");
    }

    private IEnumerator DoPut(AtomicAction action)
    {
        yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 1f));
        Debug.Log($"[AME] Put 完成 -> {action.targetName}");
    }

    private IEnumerator DoLand(AtomicAction action)
    {
        const float landingHeight = 0.2f;
        const float timeout = 15f;
        float elapsed = 0f;
        Vector3 holdPos = transform.position;

        while (Mathf.Abs(aerialMotion.TargetHeight - landingHeight) > 0.05f && elapsed < timeout)
        {
            aerialMotion.TargetHeight = Mathf.MoveTowards(aerialMotion.TargetHeight, landingHeight, 2f * Time.deltaTime);
            aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos);
            elapsed += Time.deltaTime;
            yield return null;
        }

        aerialMotion.TargetHeight = landingHeight;
        aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos);
        Debug.Log($"[AME] Land 完成，高度={transform.position.y:F2}m");
    }

    private IEnumerator DoTakeoff(AtomicAction action)
    {
        const float timeout = 15f;
        float elapsed = 0f;
        Vector3 holdPos = transform.position;

        while (Mathf.Abs(aerialMotion.TargetHeight - defaultTargetHeight) > 0.05f && elapsed < timeout)
        {
            aerialMotion.TargetHeight = Mathf.MoveTowards(aerialMotion.TargetHeight, defaultTargetHeight, 2f * Time.deltaTime);
            aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos);
            elapsed += Time.deltaTime;
            yield return null;
        }

        aerialMotion.TargetHeight = defaultTargetHeight;
        aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos);
        Debug.Log($"[AME] Takeoff 完成，高度={transform.position.y:F2}m");
    }

    private IEnumerator DoPatrol(AtomicAction action)
    {
        if (campusGrid == null || aerialMotion == null)
        {
            Debug.LogWarning("[AME] Patrol 缺少执行依赖，跳过");
            yield break;
        }

        float totalDuration = action.duration > 0f ? action.duration : 60f;

        string resolvedTarget = action.targetName;
        if (string.IsNullOrWhiteSpace(resolvedTarget) && memoryModule != null)
        {
            resolvedTarget = memoryModule.GetHighestIdlenessArea();
            if (!string.IsNullOrWhiteSpace(resolvedTarget))
            {
                Debug.Log($"[AME] Patrol 自动选区 -> {resolvedTarget}");
            }
        }

        List<Vector2Int> patrolCells = ResolvePatrolCells(resolvedTarget);
        if (patrolCells.Count == 0)
        {
            Debug.LogWarning("[AME] Patrol 没有可巡逻的格子");
            yield break;
        }

        AgentStateServer.PushMotionEvent(
            props?.AgentID ?? name,
            "patrol_start",
            $"巡逻 {resolvedTarget}，候选格数={patrolCells.Count}，时长={totalDuration:F0}s");

        HashSet<Vector2Int> visited = new();
        float elapsed = 0f;
        const float visitRadiusM = 8f;

        while (elapsed < totalDuration && visited.Count < patrolCells.Count)
        {
            Vector2Int currentCell = campusGrid.WorldToGrid(transform.position);
            if (!TryResolveNearestPathCell(currentCell, 4, "patrol_start", out currentCell))
            {
                Debug.LogWarning($"[AME] {props?.AgentID ?? name} 巡逻起点过于贴近障碍，无法找到安全格。");
                break;
            }

            Vector2Int? frontier = FindNearestUnvisitedCell(currentCell, patrolCells, visited);
            if (!frontier.HasValue)
            {
                break;
            }

            Vector2Int frontierCell = frontier.Value;
            if (!TryResolveNearestPathCell(frontierCell, 4, "patrol_goal", out frontierCell))
            {
                Debug.LogWarning($"[AME] {props?.AgentID ?? name} 巡逻目标 {frontier.Value} 周边没有可用安全格。");
                visited.Add(frontier.Value);
                continue;
            }

            List<Vector2Int> segment = campusGrid.FindPathAStar(currentCell, frontierCell) ?? new List<Vector2Int> { frontierCell };
            List<Vector3> segmentPath = BuildWorldPath(segment, aerialMotion.TargetHeight);
            if (segmentPath.Count == 0)
            {
                break;
            }

            currentPath = segmentPath;
            waypointIdx = 0;
            pathVisualizer?.ShowPath(currentPath, GetTeamColor());

            while (waypointIdx < currentPath.Count && elapsed < totalDuration)
            {
                Vector3 waypoint = currentPath[waypointIdx];
                Vector3 carrot = GetPurePursuitCarrot(currentPath, waypointIdx, lookAheadDist);
                CommandMoveTarget(carrot, allowAvoidance: true);

                if (ShouldAdvanceWaypoint(waypoint, waypointArrivalDistance + 0.4f))
                {
                    waypointIdx++;
                }

                if (isStuck)
                {
                    TryHandleLocalObstacleStuck(waypoint);
                    isStuck = false;
                }

                MarkVisitedCells(visited, patrolCells, visitRadiusM);
                elapsed += Time.deltaTime;
                yield return null;
            }

            MarkVisitedCells(visited, patrolCells, visitRadiusM);

            float dwell = UnityEngine.Random.Range(0.5f, 1.3f);
            float dwellElapsed = 0f;
            Vector3 holdPos = transform.position;
            while (dwellElapsed < dwell && elapsed < totalDuration)
            {
                aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos);
                MarkVisitedCells(visited, patrolCells, visitRadiusM);
                dwellElapsed += Time.deltaTime;
                elapsed += Time.deltaTime;
                yield return null;
            }
        }

        pathVisualizer?.ClearPath();
        ResetAvoidanceState(false, string.Empty);

        float coverage = patrolCells.Count > 0 ? (float)visited.Count / patrolCells.Count * 100f : 100f;
        AgentStateServer.PushMotionEvent(
            props?.AgentID ?? name,
            "patrol_done",
            $"巡逻完成 {resolvedTarget}，覆盖率 {coverage:F0}%，用时 {elapsed:F0}s");

        action.result = $"覆盖率:{Mathf.RoundToInt(coverage)}%";

        if (!string.IsNullOrWhiteSpace(resolvedTarget))
        {
            memoryModule?.RecordPatrolEvent(resolvedTarget, DateTime.Now);
        }
    }

    private IEnumerator HoldPosition(float duration)
    {
        float elapsed = 0f;
        Vector3 holdPos = transform.position;
        while (elapsed < duration)
        {
            aerialMotion.MoveTarget = GetNoisyHoldPos(holdPos);
            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    private bool TryBuildPathToTargetName(string targetName, float height, out List<Vector3> path)
    {
        path = new List<Vector3>();
        if (!campusGrid.TryGetFeatureApproachCells(targetName, transform.position, out Vector2Int[] approachCells, 1) ||
            approachCells.Length == 0)
        {
            return false;
        }

        Vector2Int startCell = campusGrid.WorldToGrid(transform.position);
        Vector2Int goalCell = approachCells[0];
        if (!TryResolveNearestPathCell(startCell, 4, "path_start", out startCell) ||
            !TryResolveNearestPathCell(goalCell, 4, "path_goal", out goalCell))
        {
            return false;
        }

        List<Vector2Int> gridPath = campusGrid.FindPathAStar(startCell, goalCell) ?? new List<Vector2Int> { goalCell };
        path = BuildWorldPath(gridPath, height);
        return path.Count > 0;
    }

    private List<Vector3> BuildWorldPath(IEnumerable<Vector2Int> gridPath, float height)
    {
        return gridPath
            .Select(cell =>
            {
                Vector3 world = campusGrid.GridToWorldCenter(cell.x, cell.y);
                world.y = height;
                return world;
            })
            .ToList();
    }

    private void CommandMoveTarget(Vector3 desiredTarget, bool allowAvoidance)
    {
        Vector3 targetToUse = allowAvoidance ? ResolveNavigationTarget(desiredTarget) : desiredTarget;
        targetToUse.y = aerialMotion.TargetHeight;
        aerialMotion.MoveTarget = targetToUse;
    }

    private Vector3 ResolveNavigationTarget(Vector3 desiredTarget)
    {
        if (obstacleLayers.value == 0)
        {
            ResetAvoidanceState(false, string.Empty);
            return desiredTarget;
        }

        Vector3 currentPos = transform.position;
        Vector3 moveVec = desiredTarget - currentPos;
        moveVec.y = 0f;
        if (moveVec.sqrMagnitude < 0.001f)
        {
            return desiredTarget;
        }

        Vector3 moveDir = moveVec.normalized;
        if (forcedEscapeUntil > Time.time)
        {
            if (HorizontalDistance(transform.position, forcedEscapeTarget) > obstacleResumeDistance * 0.8f)
            {
                return forcedEscapeTarget;
            }

            forcedEscapeUntil = 0f;
            forcedEscapeTarget = Vector3.zero;
        }

        RealtimeAvoidanceResult realtimeResult = TryBuildRealtimeAvoidance(moveDir);
        PerceptionAvoidanceResult perceptionResult = TryBuildPerceptionAvoidance(currentPos, moveDir);
        CrowdAvoidanceResult crowdResult = TryBuildCrowdAvoidance(currentPos, moveDir);

        bool hasAvoidanceDemand =
            realtimeResult.shouldCreateBypass ||
            perceptionResult.shouldCreateBypass ||
            crowdResult.shouldCreateBypass;
        if (hasBypassTarget)
        {
            if (hasAvoidanceDemand)
            {
                Vector3 refreshedOffset = realtimeResult.avoidanceOffset + perceptionResult.avoidanceOffset + crowdResult.avoidanceOffset;
                string side = ResolvePreferredSide(realtimeResult.selectedSide, crowdResult.selectedSide, perceptionResult.selectedSide, refreshedOffset, moveDir);
                Collider blocker = realtimeResult.blockingCollider != null ? realtimeResult.blockingCollider : activeAvoidanceCollider;
                float forwardHint = ResolveBypassForwardHint(realtimeResult, currentPos, desiredTarget);
                SetBypassTarget(
                    currentPos,
                    desiredTarget,
                    moveDir,
                    ResolvePreferredBypassHeading(realtimeResult, moveDir),
                    refreshedOffset,
                    side,
                    blocker,
                    forwardHint,
                    BuildAvoidanceReason("refresh", realtimeResult, perceptionResult, crowdResult),
                    rebuildExisting: true);
                return bypassTarget;
            }

            if (ShouldKeepBypassTarget(desiredTarget, out string keepReason))
            {
                return bypassTarget;
            }

            ResetAvoidanceState(true, keepReason);
        }

        if (!hasAvoidanceDemand)
        {
            return desiredTarget;
        }

        Vector3 combinedOffset = realtimeResult.avoidanceOffset + perceptionResult.avoidanceOffset + crowdResult.avoidanceOffset;
        string selectedSide = ResolvePreferredSide(realtimeResult.selectedSide, crowdResult.selectedSide, perceptionResult.selectedSide, combinedOffset, moveDir);
        float bypassForwardHint = ResolveBypassForwardHint(realtimeResult, currentPos, desiredTarget);
        SetBypassTarget(
            currentPos,
            desiredTarget,
            moveDir,
            ResolvePreferredBypassHeading(realtimeResult, moveDir),
            combinedOffset,
            selectedSide,
            realtimeResult.blockingCollider,
            bypassForwardHint,
            BuildAvoidanceReason("create", realtimeResult, perceptionResult, crowdResult),
            rebuildExisting: false);
        return bypassTarget;
    }

    /// <summary>
    /// 基于实时 SphereCast 生成当前帧的即时避障结果，并更新可视化快照。
    /// </summary>
    private RealtimeAvoidanceResult TryBuildRealtimeAvoidance(Vector3 moveDir)
    {
        Vector3 probeOrigin = GetObstacleProbeOrigin();
        Vector3 right = Vector3.Cross(Vector3.up, moveDir).normalized;
        Vector3 leftDir = Quaternion.Euler(0f, -25f, 0f) * moveDir;
        Vector3 rightDir = Quaternion.Euler(0f, 25f, 0f) * moveDir;
        int layerMask = GetAvoidanceLayerMask(includeAgents: true);

        bool hitMid = TrySphereCastNonSelf(
            probeOrigin,
            obstacleProbeRadius,
            moveDir,
            out RaycastHit midHit,
            obstacleForwardCheckDistance,
            layerMask);
        bool hitLeft = TrySphereCastNonSelf(
            probeOrigin,
            obstacleProbeRadius * 0.8f,
            leftDir,
            out RaycastHit leftHit,
            obstacleSideCheckDistance,
            layerMask);
        bool hitRight = TrySphereCastNonSelf(
            probeOrigin,
            obstacleProbeRadius * 0.8f,
            rightDir,
            out RaycastHit rightHit,
            obstacleSideCheckDistance,
            layerMask);

        lastProbe = new ProbeFrame
        {
            valid = true,
            origin = probeOrigin,
            moveDir = moveDir,
            hitMid = hitMid,
            midDist = hitMid ? midHit.distance : obstacleForwardCheckDistance,
            hitLeft = hitLeft,
            leftDist = hitLeft ? leftHit.distance : obstacleSideCheckDistance,
            hitRight = hitRight,
            rightDist = hitRight ? rightHit.distance : obstacleSideCheckDistance,
        };

        CurrentAvoidanceProbe = new AvoidanceProbeSnapshot
        {
            valid = true,
            origin = probeOrigin,
            forwardDir = moveDir,
            leftDir = leftDir,
            rightDir = rightDir,
            hitForward = hitMid,
            forwardDist = lastProbe.midDist,
            hitLeft = hitLeft,
            leftDist = lastProbe.leftDist,
            hitRight = hitRight,
            rightDist = lastProbe.rightDist,
            maxForwardDist = obstacleForwardCheckDistance,
            maxSideDist = obstacleSideCheckDistance,
        };

        RealtimeAvoidanceResult result = new RealtimeAvoidanceResult
        {
            hasObstacle = hitMid || hitLeft || hitRight,
            forwardDistance = hitMid ? midHit.distance : obstacleForwardCheckDistance,
            leftDistance = hitLeft ? leftHit.distance : obstacleSideCheckDistance,
            rightDistance = hitRight ? rightHit.distance : obstacleSideCheckDistance,
            avoidanceOffset = Vector3.zero,
            bypassDirection = moveDir,
            blockingCollider = hitMid ? midHit.collider : null,
            selectedSide = string.Empty,
            usedFanSearch = false,
        };

        if (!result.hasObstacle)
        {
            return result;
        }

        bool fullyBlocked = hitMid && hitLeft && hitRight;
        bool narrowBlocked = hitMid && Mathf.Min(result.leftDistance, result.rightDistance) < obstacleProbeRadius * 2.2f;
        if (result.hasObstacle)
        {
            if (TryFindBestBypassDirection(moveDir, layerMask, out Vector3 bestDir, out float bestClearance, out float bestScore, out Collider bestCollider))
            {
                float forwardClearance = result.forwardDistance;
                float forwardScore = EvaluateDirectionScore(moveDir, moveDir, forwardClearance, GetStableSideBiasSign());
                bool directionChangedEnough = Vector3.Dot(bestDir, moveDir) < 0.9985f;
                bool clearanceImproved = bestClearance > forwardClearance + obstacleProbeRadius * 0.65f;
                bool scoreImproved = bestScore > forwardScore + 0.35f;
                bool forwardLooksBlocked = fullyBlocked || narrowBlocked || result.forwardDistance < obstacleForwardCheckDistance * 0.88f;
                if (forwardLooksBlocked || (directionChangedEnough && (clearanceImproved || scoreImproved)))
                {
                    result.usedFanSearch = true;
                    result.bypassDirection = bestDir;
                    result.blockingCollider = bestCollider != null ? bestCollider : result.blockingCollider;
                    result.selectedSide = Vector3.Dot(bestDir, right) >= 0f ? "right" : "left";

                    Vector3 sidePush = Vector3.ProjectOnPlane(bestDir, moveDir);
                    if (sidePush.sqrMagnitude < 0.001f)
                    {
                        sidePush = result.selectedSide == "left" ? -right : right;
                    }

                    sidePush.Normalize();
                    float force = Mathf.Clamp01(bestClearance / Mathf.Max(0.01f, obstacleForwardCheckDistance));
                    result.avoidanceOffset = sidePush * avoidanceForceStrength + bestDir * Mathf.Lerp(1.2f, 3.4f, force);
                    result.shouldCreateBypass = true;
                    return result;
                }
            }
        }

        if (hitMid)
        {
            float leftDist = hitLeft ? leftHit.distance : obstacleSideCheckDistance;
            float rightDist = hitRight ? rightHit.distance : obstacleSideCheckDistance;
            bool goLeft = leftDist >= rightDist;
            result.selectedSide = goLeft ? "left" : "right";
            result.bypassDirection = Quaternion.Euler(0f, goLeft ? -40f : 40f, 0f) * moveDir;

            Vector3 sideDir = goLeft ? -right : right;
            float force = Mathf.Clamp01(1f - midHit.distance / Mathf.Max(0.01f, obstacleForwardCheckDistance)) * avoidanceForceStrength;
            result.avoidanceOffset += sideDir * force;
            result.avoidanceOffset -= moveDir * (force * 0.25f);
            result.shouldCreateBypass = result.avoidanceOffset.sqrMagnitude > 0.04f;
        }
        else
        {
            if (hitLeft)
            {
                float push = Mathf.Clamp01(1f - leftHit.distance / Mathf.Max(0.01f, obstacleSideCheckDistance)) * (avoidanceForceStrength * 0.55f);
                result.avoidanceOffset += right * push;
            }

            if (hitRight)
            {
                float push = Mathf.Clamp01(1f - rightHit.distance / Mathf.Max(0.01f, obstacleSideCheckDistance)) * (avoidanceForceStrength * 0.55f);
                result.avoidanceOffset -= right * push;
            }

            if (result.avoidanceOffset.sqrMagnitude > 0.01f)
            {
                result.selectedSide = Vector3.Dot(result.avoidanceOffset, right) >= 0f ? "right" : "left";
                result.blockingCollider = hitLeft && !hitRight ? leftHit.collider : hitRight && !hitLeft ? rightHit.collider : null;
                result.bypassDirection = Quaternion.Euler(0f, result.selectedSide == "left" ? -28f : 28f, 0f) * moveDir;
                result.shouldCreateBypass = result.avoidanceOffset.sqrMagnitude > 0.04f;
            }
        }

        return result;
    }

    /// <summary>
    /// 结合共享感知库，对前方已知障碍施加预判侧向偏移。
    /// </summary>
    private PerceptionAvoidanceResult TryBuildPerceptionAvoidance(Vector3 currentPos, Vector3 moveDir)
    {
        float queryRadius = obstacleForwardCheckDistance * 1.5f;
        Vector3 right = Vector3.Cross(Vector3.up, moveDir).normalized;
        List<SmallNodeData> nearbyNodes = SmallNodeRegistry.QueryNodes(currentPos, queryRadius, includeStatic: true, includeDynamic: true);

        PerceptionAvoidanceResult result = new PerceptionAvoidanceResult
        {
            hasObstacle = false,
            shouldCreateBypass = false,
            candidateCount = 0,
            strongestRepulsion = 0f,
            avoidanceOffset = Vector3.zero,
            selectedSide = string.Empty,
            strongestNodeId = string.Empty,
        };

        foreach (SmallNodeData node in nearbyNodes)
        {
            if (!IsPerceptionObstacleType(node.NodeType))
            {
                continue;
            }

            Vector3 toObstacle = node.WorldPosition - currentPos;
            toObstacle.y = 0f;
            float dist = toObstacle.magnitude;
            if (dist < 0.1f || dist > queryRadius)
            {
                continue;
            }

            Vector3 obstacleDir = toObstacle / dist;
            float forwardDot = Vector3.Dot(obstacleDir, moveDir);
            if (forwardDot < 0.45f)
            {
                continue;
            }

            Vector3 pushDir = Vector3.ProjectOnPlane(-obstacleDir, moveDir);
            if (pushDir.sqrMagnitude < 0.001f)
            {
                float fallbackSign = Vector3.Dot(obstacleDir, right) >= 0f ? -1f : 1f;
                pushDir = right * fallbackSign;
            }

            pushDir.Normalize();
            float repulsion = Mathf.Clamp01(1f - dist / queryRadius) * avoidanceForceStrength * 0.45f * Mathf.Clamp01(forwardDot);
            if (repulsion <= 0.01f)
            {
                continue;
            }

            result.hasObstacle = true;
            result.candidateCount++;
            result.avoidanceOffset += pushDir * repulsion;

            if (repulsion > result.strongestRepulsion)
            {
                result.strongestRepulsion = repulsion;
                result.strongestNodeId = node.NodeId;
            }
        }

        if (result.avoidanceOffset.sqrMagnitude > 0.01f)
        {
            result.selectedSide = Vector3.Dot(result.avoidanceOffset, right) >= 0f ? "right" : "left";
            result.shouldCreateBypass = result.avoidanceOffset.sqrMagnitude > 0.04f;
        }

        return result;
    }

    /// <summary>
    /// 对近距离 agent 做分离，避免在狭窄区域互相顶住。
    /// </summary>
    private CrowdAvoidanceResult TryBuildCrowdAvoidance(Vector3 currentPos, Vector3 moveDir)
    {
        CrowdAvoidanceResult result = new CrowdAvoidanceResult
        {
            hasNearbyAgents = false,
            shouldCreateBypass = false,
            nearbyCount = 0,
            avoidanceOffset = Vector3.zero,
            selectedSide = string.Empty,
            dominantAgentId = string.Empty,
        };

        if (agentAvoidanceLayers.value == 0)
        {
            return result;
        }

        Collider[] nearbyColliders = Physics.OverlapSphere(
            currentPos,
            crowdAvoidanceRadius,
            agentAvoidanceLayers,
            QueryTriggerInteraction.Ignore);
        if (nearbyColliders == null || nearbyColliders.Length == 0)
        {
            return result;
        }

        Vector3 right = Vector3.Cross(Vector3.up, moveDir).normalized;
        float strongestRepulsion = 0f;

        foreach (Collider otherCollider in nearbyColliders)
        {
            if (IsSelfCollider(otherCollider))
            {
                continue;
            }

            IntelligentAgent otherAgent = otherCollider.GetComponentInParent<IntelligentAgent>();
            if (otherAgent == null || otherAgent == agent)
            {
                continue;
            }

            Vector3 toOther = otherCollider.bounds.center - currentPos;
            toOther.y = 0f;
            float dist = toOther.magnitude;
            if (dist < 0.05f || dist > crowdAvoidanceRadius)
            {
                continue;
            }

            Vector3 dirToOther = toOther / dist;
            float forwardDot = Vector3.Dot(dirToOther, moveDir);
            if (forwardDot < -0.2f)
            {
                continue;
            }

            Vector3 pushDir = Vector3.ProjectOnPlane(-dirToOther, moveDir);
            if (pushDir.sqrMagnitude < 0.001f)
            {
                float sideSign = ResolveCrowdSideSign(otherAgent, right);
                pushDir = right * sideSign;
            }

            pushDir.Normalize();
            float repulsion =
                Mathf.Clamp01(1f - dist / Mathf.Max(0.01f, crowdAvoidanceRadius)) *
                crowdAvoidanceStrength *
                Mathf.Lerp(0.75f, 1.15f, Mathf.Clamp01(forwardDot + 0.2f));
            if (repulsion <= 0.01f)
            {
                continue;
            }

            result.hasNearbyAgents = true;
            result.nearbyCount++;
            result.avoidanceOffset += pushDir * repulsion;

            if (repulsion > strongestRepulsion)
            {
                strongestRepulsion = repulsion;
                result.selectedSide = Vector3.Dot(pushDir, right) >= 0f ? "right" : "left";
                result.dominantAgentId = otherAgent.Properties?.AgentID ?? otherAgent.name;
            }
        }

        result.shouldCreateBypass = result.avoidanceOffset.sqrMagnitude > 0.04f || result.nearbyCount >= 2;
        return result;
    }

    /// <summary>
    /// 将实时与预判排斥合成为一个稳定的局部绕行点，供后续数帧持续追踪。
    /// </summary>
    private void SetBypassTarget(
        Vector3 currentPos,
        Vector3 desiredTarget,
        Vector3 moveDir,
        Vector3 bypassHeading,
        Vector3 avoidanceOffset,
        string selectedSide,
        Collider blockingCollider,
        float forwardHint,
        string reason,
        bool rebuildExisting)
    {
        Vector3 nextBypassTarget = BuildBypassTarget(currentPos, desiredTarget, moveDir, bypassHeading, avoidanceOffset, selectedSide, forwardHint);
        bool positionChanged = !hasBypassTarget || HorizontalDistance(bypassTarget, nextBypassTarget) > 0.3f;
        bool sideChanged = !string.Equals(activeAvoidanceSide, selectedSide, StringComparison.Ordinal);

        hasBypassTarget = true;
        bypassTarget = nextBypassTarget;
        activeAvoidanceCollider = blockingCollider;
        activeAvoidanceSide = selectedSide;

        bool shouldLog = !rebuildExisting || positionChanged || sideChanged;
        if (logAvoidance && shouldLog)
        {
            string blockerName = blockingCollider != null ? blockingCollider.name : "registry";
            Debug.Log(
                $"[AME] {props?.AgentID ?? name} {(rebuildExisting ? "刷新" : "生成")}局部绕行点 " +
                $"side={selectedSide}, blocker={blockerName}, bypass={bypassTarget}, reason={reason}");
        }
    }

    /// <summary>
    /// 根据排斥方向构建一个更稳定的局部绕行点，而不是只对原目标做一帧偏移。
    /// </summary>
    private Vector3 BuildBypassTarget(
        Vector3 currentPos,
        Vector3 desiredTarget,
        Vector3 moveDir,
        Vector3 bypassHeading,
        Vector3 avoidanceOffset,
        string selectedSide,
        float forwardHint)
    {
        Vector3 planarOffset = avoidanceOffset;
        planarOffset.y = 0f;
        Vector3 travelDir = bypassHeading.sqrMagnitude > 0.001f ? bypassHeading.normalized : moveDir;

        Vector3 sideDir = Vector3.ProjectOnPlane(planarOffset, moveDir);
        if (sideDir.sqrMagnitude < 0.001f)
        {
            Vector3 right = Vector3.Cross(Vector3.up, moveDir).normalized;
            sideDir = selectedSide == "left" ? -right : right;
        }

        sideDir.Normalize();

        float goalDistance = HorizontalDistance(currentPos, desiredTarget);
        float forwardStep = Mathf.Clamp(
            Mathf.Max(forwardHint + obstacleProbeRadius, goalDistance * 0.45f),
            obstacleProbeRadius * 2f,
            obstacleForwardCheckDistance * 0.9f);
        float lateralStep = Mathf.Clamp(
            Mathf.Max(planarOffset.magnitude * 2.4f, obstacleProbeRadius * 2f),
            obstacleProbeRadius * 1.6f,
            obstacleSideCheckDistance + obstacleProbeRadius);

        Vector3 bypass = currentPos + travelDir * forwardStep + sideDir * lateralStep;
        bypass.y = desiredTarget.y;
        return bypass;
    }

    /// <summary>
    /// 已有绕行目标时，判断是否还要继续坚持绕行，而不是立刻回到主路径。
    /// </summary>
    private bool ShouldKeepBypassTarget(Vector3 desiredTarget, out string releaseReason)
    {
        float bypassDistance = HorizontalDistance(transform.position, bypassTarget);
        bool reachedBypass = bypassDistance <= obstacleResumeDistance;
        bool directPathClear = IsDirectPathClear(desiredTarget);
        bool obstacleReleased = IsActiveAvoidanceReleased();

        if (!reachedBypass)
        {
            releaseReason = string.Empty;
            return true;
        }

        if (!directPathClear && !obstacleReleased)
        {
            releaseReason = string.Empty;
            return true;
        }

        releaseReason = directPathClear
            ? "恢复主路径：绕行点已到达且直线路径已清空"
            : "恢复主路径：绕行点已到达且阻挡体已远离";
        return false;
    }

    /// <summary>
    /// 判断当前正在参考的阻挡体是否已经脱离恢复半径。
    /// </summary>
    private bool IsActiveAvoidanceReleased()
    {
        if (activeAvoidanceCollider == null || !activeAvoidanceCollider.enabled || !activeAvoidanceCollider.gameObject.activeInHierarchy)
        {
            return true;
        }

        Vector3 closest = activeAvoidanceCollider.ClosestPoint(transform.position);
        return HorizontalDistance(transform.position, closest) > obstacleResumeDistance * 1.5f;
    }

    /// <summary>
    /// 从实时探测和感知预判中决定优先往哪一侧绕行。
    /// </summary>
    private string ResolvePreferredSide(string realtimeSide, string crowdSide, string perceptionSide, Vector3 avoidanceOffset, Vector3 moveDir)
    {
        if (!string.IsNullOrWhiteSpace(realtimeSide))
        {
            return realtimeSide;
        }

        if (!string.IsNullOrWhiteSpace(crowdSide))
        {
            return crowdSide;
        }

        if (!string.IsNullOrWhiteSpace(perceptionSide))
        {
            return perceptionSide;
        }

        Vector3 right = Vector3.Cross(Vector3.up, moveDir).normalized;
        return Vector3.Dot(avoidanceOffset, right) >= 0f ? "right" : "left";
    }

    /// <summary>
    /// 估算绕行点应该向前放多远，避免只在原地横移。
    /// </summary>
    private float ResolveBypassForwardHint(RealtimeAvoidanceResult realtimeResult, Vector3 currentPos, Vector3 desiredTarget)
    {
        if (realtimeResult.hasObstacle)
        {
            return Mathf.Min(realtimeResult.forwardDistance, obstacleForwardCheckDistance * 0.75f);
        }

        return Mathf.Clamp(HorizontalDistance(currentPos, desiredTarget) * 0.35f, obstacleProbeRadius * 2f, obstacleForwardCheckDistance * 0.7f);
    }

    /// <summary>
    /// 生成调试日志摘要，便于观察实时探测与预判排斥是否同时生效。
    /// </summary>
    private string BuildAvoidanceReason(
        string stage,
        RealtimeAvoidanceResult realtimeResult,
        PerceptionAvoidanceResult perceptionResult,
        CrowdAvoidanceResult crowdResult)
    {
        string blocker = realtimeResult.blockingCollider != null ? realtimeResult.blockingCollider.name : "none";
        return
            $"stage={stage}, mid={realtimeResult.forwardDistance:F2}, left={realtimeResult.leftDistance:F2}, right={realtimeResult.rightDistance:F2}, " +
            $"realtimeSide={realtimeResult.selectedSide}, fan={realtimeResult.usedFanSearch}, blocker={blocker}, " +
            $"perceptionCandidates={perceptionResult.candidateCount}, perceptionSide={perceptionResult.selectedSide}, " +
            $"perceptionForce={perceptionResult.avoidanceOffset.magnitude:F2}, strongestNode={perceptionResult.strongestNodeId}, " +
            $"crowdCount={crowdResult.nearbyCount}, crowdSide={crowdResult.selectedSide}, crowdLead={crowdResult.dominantAgentId}";
    }

    /// <summary>
    /// 过滤允许参与预判避障的小节点类型。
    /// </summary>
    private static bool IsPerceptionObstacleType(SmallNodeType nodeType)
    {
        return nodeType == SmallNodeType.TemporaryObstacle ||
               nodeType == SmallNodeType.Tree ||
               nodeType == SmallNodeType.Vehicle ||
               nodeType == SmallNodeType.Pedestrian;
    }

    private bool TryFindBlockingObstacle(Vector3 desiredTarget, out RaycastHit hit)
    {
        Vector3 direction = desiredTarget - transform.position;
        direction.y = 0f;

        float distance = direction.magnitude;
        if (distance < 0.2f)
        {
            hit = default;
            return false;
        }

        return TrySphereCastNonSelf(
            GetObstacleProbeOrigin(),
            obstacleProbeRadius,
            direction.normalized,
            out hit,
            obstacleForwardCheckDistance,
            obstacleLayers.value);
    }

    private bool IsDirectPathClear(Vector3 target, bool includeAgents = false)
    {
        Vector3 direction = target - transform.position;
        direction.y = 0f;
        float distance = direction.magnitude;
        if (distance < 0.15f)
        {
            return true;
        }

        return !TrySphereCastNonSelf(
            GetObstacleProbeOrigin(),
            obstacleProbeRadius,
            direction.normalized,
            out RaycastHit _,
            distance,
            GetAvoidanceLayerMask(includeAgents));
    }

    private Vector3 GetObstacleProbeOrigin()
    {
        // 修正：探测点应基于当前高度，而不是目标高度，否则会漏掉低处障碍
        return transform.position;
    }

    private bool TryHandleLocalObstacleStuck(Vector3 desiredTarget)
    {
        if (obstacleLayers.value == 0)
        {
            return false;
        }

        bool blockedByStatic = !IsDirectPathClear(desiredTarget, includeAgents: false);
        bool blockedByCrowd = !IsDirectPathClear(desiredTarget, includeAgents: true);
        if (hasBypassTarget || obstacleHoldUntil > Time.time || blockedByCrowd)
        {
            localAvoidanceRetryCount++;
            hasBypassTarget = false;
            bypassTarget = Vector3.zero;
            activeAvoidanceCollider = null;
            activeAvoidanceSide = string.Empty;

            if (TryActivateForcedEscapeTarget(desiredTarget))
            {
                obstacleHoldUntil = Time.time + obstacleRetryHoldSeconds;
                if (logAvoidance)
                {
                    Debug.LogWarning($"[AME] {props?.AgentID ?? name} 卡住后生成强制脱困点 -> {forcedEscapeTarget}");
                }
                return true;
            }

            obstacleHoldUntil = Time.time + obstacleRetryHoldSeconds;
            if (logAvoidance)
            {
                Debug.LogWarning($"[AME] {props?.AgentID ?? name} 在局部障碍附近卡住，优先继续局部绕障，retry={localAvoidanceRetryCount}");
            }

            if (blockedByStatic && localAvoidanceRetryCount > maxStaticBypassRetries)
            {
                if (logAvoidance)
                {
                    Debug.LogWarning($"[AME] {props?.AgentID ?? name} 局部绕障重试超过阈值，允许进入 A* 重规划");
                }
                localAvoidanceRetryCount = 0;
                return false;
            }

            return true;
        }

        localAvoidanceRetryCount = 0;
        return false;
    }

    private bool TryReplanPath(Vector3 finalGoal, float height, out List<Vector3> replannedPath)
    {
        replannedPath = new List<Vector3>();
        Vector2Int nowCell = campusGrid.WorldToGrid(transform.position);
        Vector2Int goalCell = campusGrid.WorldToGrid(finalGoal);
        if (!TryResolveNearestPathCell(nowCell, 4, "replan_start", out nowCell) ||
            !TryResolveNearestPathCell(goalCell, 4, "replan_goal", out goalCell))
        {
            return false;
        }

        List<Vector2Int> newPath = campusGrid.FindPathAStar(nowCell, goalCell);
        if (newPath == null || newPath.Count == 0)
        {
            return false;
        }

        ResetAvoidanceState(false, string.Empty);
        replannedPath = BuildWorldPath(newPath, height);
        Debug.LogWarning($"[AME] {props?.AgentID ?? name} 局部避障仍无法通过，触发 A* 重规划");
        return replannedPath.Count > 0;
    }

    /// <summary>
    /// 将贴近建筑边缘的请求格吸附到最近的安全路径格。
    /// 说明：局部避障会把贴墙位置视为持续受阻，因此在进入 A* 前先把起终点修正到更稳妥的位置。
    /// </summary>
    private bool TryResolveNearestPathCell(Vector2Int requestedCell, int searchRadius, string reason, out Vector2Int resolvedCell)
    {
        resolvedCell = requestedCell;
        if (campusGrid == null)
        {
            return false;
        }

        if (!campusGrid.TryFindNearestWalkable(requestedCell, Mathf.Max(1, searchRadius), out Vector2Int safeCell))
        {
            if (logAvoidance)
            {
                Debug.LogWarning($"[AME] {props?.AgentID ?? name} 无法为 {reason} 找到安全路径格，requested={requestedCell}");
            }
            return false;
        }

        resolvedCell = safeCell;
        if (logAvoidance && safeCell != requestedCell)
        {
            Debug.Log($"[AME] {props?.AgentID ?? name} {reason} 从 {requestedCell} 修正到安全格 {safeCell}");
        }

        return true;
    }

    private bool ShouldAdvanceWaypoint(Vector3 waypoint, float baseArrivalDistance)
    {
        float planarSpeed = GetPlanarSpeed();
        float dynamicArrivalDistance = Mathf.Clamp(baseArrivalDistance + planarSpeed * 0.35f, baseArrivalDistance, baseArrivalDistance * 2.4f);
        return HorizontalDistance(transform.position, waypoint) <= dynamicArrivalDistance;
    }

    private float GetPlanarSpeed()
    {
        Vector3 velocity = aerialMotion != null ? aerialMotion.Velocity : Vector3.zero;
        return new Vector2(velocity.x, velocity.z).magnitude;
    }

    private Vector3 GetPurePursuitCarrot(List<Vector3> path, int fromIdx, float lookAhead)
    {
        if (path == null || path.Count == 0)
        {
            return transform.position;
        }

        float remaining = lookAhead;
        Vector3 pos = new Vector3(transform.position.x, path[fromIdx].y, transform.position.z);
        for (int i = fromIdx; i < path.Count; i++)
        {
            Vector3 wp = new Vector3(path[i].x, path[fromIdx].y, path[i].z);
            float segmentLength = Vector3.Distance(pos, wp);
            if (remaining <= segmentLength)
            {
                return Vector3.Lerp(pos, wp, remaining / Mathf.Max(0.001f, segmentLength));
            }

            remaining -= segmentLength;
            pos = wp;
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
        List<Vector2Int> patrolCells = new();

        if (!string.IsNullOrWhiteSpace(resolvedTarget))
        {
            if (campusGrid.TryGetFeatureOccupiedCells(resolvedTarget, out Vector2Int[] occupied))
            {
                patrolCells = occupied.Where(cell => campusGrid.IsWalkable(cell.x, cell.y)).ToList();
            }

            if (patrolCells.Count < 3)
            {
                campusGrid.TryGetFeatureApproachCells(resolvedTarget, transform.position, out Vector2Int[] approach, 256);
                patrolCells = approach?.ToList() ?? patrolCells;
            }
        }
        else
        {
            Vector2Int center = campusGrid.WorldToGrid(transform.position);
            const int radius = 20;
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int x = center.x + dx;
                    int z = center.y + dz;
                    if (campusGrid.IsInBounds(x, z) && campusGrid.IsWalkable(x, z))
                    {
                        patrolCells.Add(new Vector2Int(x, z));
                    }
                }
            }
        }

        return patrolCells;
    }

    private Vector2Int? FindNearestUnvisitedCell(Vector2Int currentCell, List<Vector2Int> patrolCells, HashSet<Vector2Int> visited)
    {
        Vector2Int? frontier = null;
        float minDistance = float.MaxValue;

        foreach (Vector2Int cell in patrolCells)
        {
            if (visited.Contains(cell))
            {
                continue;
            }

            float distance = Vector2.Distance((Vector2)currentCell, (Vector2)cell);
            if (distance < minDistance)
            {
                minDistance = distance;
                frontier = cell;
            }
        }

        return frontier;
    }

    private void MarkVisitedCells(HashSet<Vector2Int> visited, List<Vector2Int> patrolCells, float visitRadiusM)
    {
        Vector2Int nowCell = campusGrid.WorldToGrid(transform.position);
        float visitRadius = visitRadiusM / Mathf.Max(0.1f, campusGrid.cellSize);
        foreach (Vector2Int cell in patrolCells)
        {
            if (Vector2.Distance((Vector2)nowCell, (Vector2)cell) <= visitRadius)
            {
                visited.Add(cell);
            }
        }
    }

    private void ResetAvoidanceState(bool logRestore, string reason)
    {
        bool hadAvoidance = hasBypassTarget || obstacleHoldUntil > Time.time || activeAvoidanceCollider != null;
        hasBypassTarget = false;
        bypassTarget = Vector3.zero;
        obstacleHoldUntil = 0f;
        activeAvoidanceCollider = null;
        activeAvoidanceSide = string.Empty;
        forcedEscapeTarget = Vector3.zero;
        forcedEscapeUntil = 0f;
        localAvoidanceRetryCount = 0;

        if (hadAvoidance && logRestore && logAvoidance && !string.IsNullOrWhiteSpace(reason))
        {
            Debug.Log($"[AME] {props?.AgentID ?? name} {reason}");
        }
    }

    /// <summary>
    /// 获取本次避障探测要使用的层掩码。
    /// </summary>
    private int GetAvoidanceLayerMask(bool includeAgents)
    {
        int mask = obstacleLayers.value;
        if (includeAgents)
        {
            mask |= agentAvoidanceLayers.value;
        }
        return mask;
    }

    /// <summary>
    /// SphereCastAll 过滤自身碰撞体，避免把自己当成障碍。
    /// </summary>
    private bool TrySphereCastNonSelf(
        Vector3 origin,
        float radius,
        Vector3 direction,
        out RaycastHit closestHit,
        float maxDistance,
        int layerMask)
    {
        closestHit = default;
        if (layerMask == 0 || direction.sqrMagnitude < 0.001f || maxDistance <= 0.01f)
        {
            return false;
        }

        RaycastHit[] hits = Physics.SphereCastAll(
            origin,
            radius,
            direction.normalized,
            maxDistance,
            layerMask,
            QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0)
        {
            return false;
        }

        bool found = false;
        float closestDistance = float.MaxValue;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit candidate = hits[i];
            if (candidate.collider == null || IsSelfCollider(candidate.collider))
            {
                continue;
            }

            if (candidate.distance < closestDistance)
            {
                closestDistance = candidate.distance;
                closestHit = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// 判断碰撞体是否属于当前 agent 自身。
    /// </summary>
    private bool IsSelfCollider(Collider candidate)
    {
        return candidate != null &&
               (candidate.transform == transform || candidate.transform.IsChildOf(transform));
    }

    /// <summary>
    /// 在更宽的角度扇区里挑选一条最有希望脱困的方向，解决多障碍夹角卡死。
    /// </summary>
    private bool TryFindBestBypassDirection(Vector3 moveDir, int layerMask, out Vector3 bestDir, out float bestClearance, out float bestScore, out Collider bestCollider)
    {
        bestDir = moveDir;
        bestClearance = 0f;
        bestScore = float.MinValue;
        bestCollider = null;

        Vector3 probeOrigin = GetObstacleProbeOrigin();
        float sideBias = GetStableSideBiasSign();

        for (int i = 0; i < FanProbeAngles.Length; i++)
        {
            float angle = FanProbeAngles[i];
            Vector3 candidateDir = Quaternion.Euler(0f, angle, 0f) * moveDir;
            float clearance = MeasureDirectionClearance(probeOrigin, candidateDir, obstacleForwardCheckDistance, layerMask, out RaycastHit hit);
            float score = EvaluateDirectionScore(candidateDir, moveDir, clearance, sideBias);

            if (clearance >= obstacleProbeRadius * 1.5f && score > bestScore)
            {
                bestScore = score;
                bestClearance = clearance;
                bestDir = candidateDir.normalized;
                bestCollider = hit.collider;
            }
        }

        return bestClearance > obstacleProbeRadius * 1.5f;
    }

    /// <summary>
    /// 用统一评分函数评估候选方向，优先选择更通畅、贴近目标、且与当前绕行侧一致的方向。
    /// </summary>
    private float EvaluateDirectionScore(Vector3 candidateDir, Vector3 moveDir, float clearance, float sideBias)
    {
        Vector3 right = Vector3.Cross(Vector3.up, moveDir).normalized;
        float currentHeadingBonus = activeAvoidanceSide == "right" ? 1f : activeAvoidanceSide == "left" ? -1f : 0f;
        float alignment = Mathf.Clamp(Vector3.Dot(candidateDir, moveDir), -1f, 1f);
        float sideScore = Mathf.Sign(Vector3.Dot(candidateDir, right)) * sideBias * 0.35f;
        float persistenceScore = Mathf.Sign(Vector3.Dot(candidateDir, right)) * currentHeadingBonus * 0.45f;
        float perceptionScore = EvaluatePerceptionRayScore(candidateDir);
        float clearanceScore = clearance * 1.15f;
        float alignmentScore = alignment * obstacleForwardCheckDistance * 0.55f;
        return clearanceScore + alignmentScore + sideScore + persistenceScore + perceptionScore;
    }

    /// <summary>
    /// 评估某个方向上的实际可通行距离。
    /// </summary>
    private float MeasureDirectionClearance(Vector3 origin, Vector3 direction, float maxDistance, int layerMask, out RaycastHit hit)
    {
        if (TrySphereCastNonSelf(origin, obstacleProbeRadius, direction, out hit, maxDistance, layerMask))
        {
            return hit.distance;
        }

        hit = default;
        return maxDistance;
    }

    /// <summary>
    /// 生成一个短时强制脱困点，避免在狭窄区域原地摇摆。
    /// </summary>
    private bool TryActivateForcedEscapeTarget(Vector3 desiredTarget)
    {
        Vector3 currentPos = transform.position;
        Vector3 moveDir = desiredTarget - currentPos;
        moveDir.y = 0f;
        if (moveDir.sqrMagnitude < 0.001f)
        {
            return false;
        }

        int layerMask = GetAvoidanceLayerMask(includeAgents: true);
        if (!TryFindBestBypassDirection(moveDir.normalized, layerMask, out Vector3 bestDir, out _, out _, out _))
        {
            Vector3 right = Vector3.Cross(Vector3.up, moveDir.normalized).normalized;
            bestDir = Quaternion.Euler(0f, GetStableSideBiasSign() > 0f ? 110f : -110f, 0f) * moveDir.normalized;
            bestDir += right * GetStableSideBiasSign() * 0.25f;
            bestDir.Normalize();
        }

        forcedEscapeTarget = currentPos + bestDir * stuckEscapeDistance;
        forcedEscapeTarget.y = aerialMotion != null ? aerialMotion.TargetHeight : transform.position.y;
        forcedEscapeUntil = Time.time + stuckEscapeDuration;
        return true;
    }

    /// <summary>
    /// 对向会车时给每个 agent 一个稳定的侧向偏好，避免双方同时左右来回抖动。
    /// </summary>
    private float ResolveCrowdSideSign(IntelligentAgent otherAgent, Vector3 right)
    {
        string selfId = props?.AgentID ?? name;
        string otherId = otherAgent?.Properties?.AgentID ?? otherAgent?.name ?? string.Empty;
        int compare = string.CompareOrdinal(selfId, otherId);
        if (compare == 0)
        {
            return GetStableSideBiasSign();
        }

        return compare < 0 ? 1f : -1f;
    }

    /// <summary>
    /// 给当前 agent 一个稳定左右偏好，用于多障碍和会车时破坏对称。
    /// </summary>
    private float GetStableSideBiasSign()
    {
        if (activeAvoidanceSide == "left")
        {
            return -1f;
        }

        if (activeAvoidanceSide == "right")
        {
            return 1f;
        }

        string selfId = props?.AgentID ?? name;
        return Mathf.Abs(selfId.GetHashCode()) % 2 == 0 ? 1f : -1f;
    }

    /// <summary>
    /// 选择构建绕行点时的前进参考方向。
    /// </summary>
    private Vector3 ResolvePreferredBypassHeading(RealtimeAvoidanceResult realtimeResult, Vector3 fallbackMoveDir)
    {
        return realtimeResult.bypassDirection.sqrMagnitude > 0.001f
            ? realtimeResult.bypassDirection.normalized
            : fallbackMoveDir;
    }

    /// <summary>
    /// 利用感知模块最近一轮扇扫射线，对候选方向做额外评分。
    /// 命中近障碍的方向降分，连续长距离畅通的方向加分。
    /// </summary>
    private float EvaluatePerceptionRayScore(Vector3 candidateDir)
    {
        if (perceptionModule == null || perceptionModule.LatestNavigationRays == null || perceptionModule.LatestNavigationRays.Count == 0)
        {
            return 0f;
        }

        Vector3 planarCandidate = Vector3.ProjectOnPlane(candidateDir, Vector3.up);
        if (planarCandidate.sqrMagnitude < 0.001f)
        {
            return 0f;
        }

        planarCandidate.Normalize();
        float score = 0f;
        int matchedRays = 0;

        for (int i = 0; i < perceptionModule.LatestNavigationRays.Count; i++)
        {
            PerceptionModule.NavigationRaySnapshot ray = perceptionModule.LatestNavigationRays[i];
            Vector3 planarRay = Vector3.ProjectOnPlane(ray.direction, Vector3.up);
            if (planarRay.sqrMagnitude < 0.001f)
            {
                continue;
            }

            planarRay.Normalize();
            float alignment = Vector3.Dot(planarCandidate, planarRay);
            if (alignment < 0.82f)
            {
                continue;
            }

            matchedRays++;
            float alignmentWeight = Mathf.InverseLerp(0.82f, 1f, alignment);
            if (ray.hit)
            {
                float penalty = Mathf.Clamp01(1f - ray.distance / Mathf.Max(0.01f, obstacleForwardCheckDistance * 1.25f));
                score -= penalty * alignmentWeight * 3.2f;
            }
            else
            {
                score += alignmentWeight * 0.65f;
            }
        }

        if (matchedRays == 0)
        {
            return 0f;
        }

        return score;
    }

    private void AbortCurrentExecutionState()
    {
        if (actionCoroutine != null)
        {
            StopCoroutine(actionCoroutine);
            actionCoroutine = null;
        }

        pathVisualizer?.ClearPath();
        ResetAvoidanceState(false, string.Empty);
        currentPath.Clear();
        waypointIdx = 0;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
    }

    private static Vector3 ParseOffsetFromParams(string rawParams)
    {
        if (string.IsNullOrWhiteSpace(rawParams))
        {
            return new Vector3(3f, 0f, 0f);
        }

        string p = rawParams.ToLowerInvariant();
        if (p.Contains("前") || p.Contains("front"))
        {
            return new Vector3(0f, 0f, 3f);
        }

        if (p.Contains("后") || p.Contains("back"))
        {
            return new Vector3(0f, 0f, -3f);
        }

        if (p.Contains("左") || p.Contains("left"))
        {
            return new Vector3(-3f, 0f, 0f);
        }

        if (p.Contains("右") || p.Contains("right"))
        {
            return new Vector3(3f, 0f, 0f);
        }

        return new Vector3(3f, 0f, 0f);
    }

    private Color GetTeamColor()
    {
        if (props == null)
        {
            return Color.white;
        }

        float hue = (props.TeamID * 0.618f) % 1f;
        return Color.HSVToRGB(hue, 0.7f, 1f);
    }
}
