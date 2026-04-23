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
    [SerializeField] private float obstacleForwardCheckDistance = 12f;
    [SerializeField] private float obstacleSideCheckDistance = 7f;
    [SerializeField] private float obstacleProbeRadius = 0.8f;
    [SerializeField] private float avoidanceForceStrength = 6f;
    [SerializeField] private float obstacleResumeDistance = 1.4f;
    [SerializeField] private float obstacleRetryHoldSeconds = 0.8f;
    [SerializeField] private bool logAvoidance = true;

    private AerialMotionController aerialMotion;
    private ActionDecisionModule adm;
    private CampusGrid2D campusGrid;
    private AgentProperties props;
    private IntelligentAgent agent;
    private MemoryModule memoryModule;

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

    // 避障射线可视化（三条独立 LineRenderer）
    private LineRenderer lrFwd;
    private LineRenderer lrLeft;
    private LineRenderer lrRight;

    // 上一次 ResolveNavigationTarget 的探测结果（供可视化读取）
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

        if (props != null && props.MaxSpeed > 0f)
        {
            aerialMotion.MaxSpeed = props.MaxSpeed;
        }

        defaultTargetHeight = aerialMotion.TargetHeight;
        stuckCheckPos = transform.position;

        // 初始化三条避障探测射线渲染器
        lrFwd   = CreateProbeRayLR("AvoidanceRay_Fwd");
        lrLeft  = CreateProbeRayLR("AvoidanceRay_Left");
        lrRight = CreateProbeRayLR("AvoidanceRay_Right");
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
    }

    private void Update()
    {
        UpdateStuckDetection();
        PollADM();
        
        DrawAvoidanceRays();
    }

    // ── 可视化辅助 ──────────────────────────────────────────────────────────

    private LineRenderer CreateProbeRayLR(string goName)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(transform);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.positionCount = 2;
        lr.enabled = false;
        return lr;
    }

    private void SetProbeRay(LineRenderer lr, Vector3 from, Vector3 to, Color col, float startW, float endW)
    {
        lr.SetPosition(0, from);
        lr.SetPosition(1, to);
        lr.startColor = col;
        lr.endColor   = new Color(col.r, col.g, col.b, 0.12f);
        lr.startWidth = startW;
        lr.endWidth   = endW;
    }

    private void DrawAvoidanceRays()
    {
        bool show = actionRunning && lastProbe.valid;
        if (lrFwd   != null) lrFwd.enabled   = show;
        if (lrLeft  != null) lrLeft.enabled  = show;
        if (lrRight != null) lrRight.enabled = show;
        if (!show) return;

        var p = lastProbe;
        Vector3 leftDir  = Quaternion.Euler(0, -25, 0) * p.moveDir;
        Vector3 rightDir = Quaternion.Euler(0,  25, 0) * p.moveDir;

        // 畅通 = 绿，前方碰撞 = 红，侧边碰撞 = 橙，侧边畅通 = 青
        Color fwdColor  = p.hitMid   ? new Color(1f, 0.2f, 0.1f, 0.95f)  : new Color(0.15f, 1f, 0.3f, 0.9f);
        Color sideColor = new Color(0.15f, 0.85f, 1f, 0.8f);
        Color sideHit   = new Color(1f, 0.55f, 0.05f, 0.9f);

        SetProbeRay(lrFwd,   p.origin, p.origin + p.moveDir * p.midDist,   fwdColor,                  0.12f, 0.03f);
        SetProbeRay(lrLeft,  p.origin, p.origin + leftDir   * p.leftDist,  p.hitLeft  ? sideHit : sideColor, 0.06f, 0.02f);
        SetProbeRay(lrRight, p.origin, p.origin + rightDir  * p.rightDist, p.hitRight ? sideHit : sideColor, 0.06f, 0.02f);
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
            case AtomicActionType.Observe:
                yield return StartCoroutine(DoObserve(action));
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

    private IEnumerator DoObserve(AtomicAction action)
    {
        GetComponent<PerceptionModule>()?.SenseOnce();
        yield return StartCoroutine(HoldPosition(action.duration > 0f ? action.duration : 3f));
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
        bool withObserve = action.actionParams?.Contains("observe", StringComparison.OrdinalIgnoreCase) == true;

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
            Vector2Int? frontier = FindNearestUnvisitedCell(currentCell, patrolCells, visited);
            if (!frontier.HasValue)
            {
                break;
            }

            List<Vector2Int> segment = campusGrid.FindPathAStar(currentCell, frontier.Value) ?? new List<Vector2Int> { frontier.Value };
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

            if (withObserve)
            {
                GetComponent<PerceptionModule>()?.SenseOnce();
            }

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
        Vector3 moveDir = (desiredTarget - currentPos).normalized;
        if (moveDir.sqrMagnitude < 0.001f) return desiredTarget;

        Vector3 probeOrigin = GetObstacleProbeOrigin();
        
        // 核心逻辑升级：三向触须探测 (左前 25度、中、右前 25度)
        // 探测距离不再受限于目标点距离，确保提前发现
        bool hitMid = Physics.SphereCast(probeOrigin, obstacleProbeRadius, moveDir, out RaycastHit midHit, obstacleForwardCheckDistance, obstacleLayers);
        
        Vector3 right = Vector3.Cross(Vector3.up, moveDir).normalized;
        Vector3 leftDir = Quaternion.Euler(0, -25, 0) * moveDir;
        Vector3 rightDir = Quaternion.Euler(0, 25, 0) * moveDir;

        bool hitLeft = Physics.SphereCast(probeOrigin, obstacleProbeRadius * 0.8f, leftDir, out RaycastHit leftHit, obstacleSideCheckDistance, obstacleLayers);
        bool hitRight = Physics.SphereCast(probeOrigin, obstacleProbeRadius * 0.8f, rightDir, out RaycastHit rightHit, obstacleSideCheckDistance, obstacleLayers);

        // 缓存本次探测结果供可视化使用
        lastProbe = new ProbeFrame
        {
            valid    = true,
            origin   = probeOrigin,
            moveDir  = moveDir,
            hitMid   = hitMid,   midDist   = hitMid   ? midHit.distance   : obstacleForwardCheckDistance,
            hitLeft  = hitLeft,  leftDist  = hitLeft  ? leftHit.distance  : obstacleSideCheckDistance,
            hitRight = hitRight, rightDist = hitRight ? rightHit.distance : obstacleSideCheckDistance,
        };

        if (!hitMid && !hitLeft && !hitRight)
        {
            if (hasBypassTarget) ResetAvoidanceState(false, "");
            return desiredTarget;
        }

        // 计算排斥力（偏移量）
        Vector3 avoidanceOffset = Vector3.zero;

        if (hitMid)
        {
            // 如果中间撞了，根据左右探测结果选择空旷的一侧
            float leftDist = hitLeft ? leftHit.distance : obstacleSideCheckDistance;
            float rightDist = hitRight ? rightHit.distance : obstacleSideCheckDistance;
            
            Vector3 pushDir = (leftDist >= rightDist) ? -right : right;
            // 距离越近，力越大
            float force = Mathf.Clamp01(1.0f - (midHit.distance / obstacleForwardCheckDistance)) * avoidanceForceStrength;
            avoidanceOffset += pushDir * force;
            
            // 加上一点向后的趋势，防止直接撞墙
            avoidanceOffset -= moveDir * (force * 0.3f);
        }
        else
        {
            // 侧边触须探测到障碍
            if (hitLeft) avoidanceOffset += right * Mathf.Clamp01(1.0f - (leftHit.distance / obstacleSideCheckDistance)) * (avoidanceForceStrength * 0.5f);
            if (hitRight) avoidanceOffset -= right * Mathf.Clamp01(1.0f - (rightHit.distance / obstacleSideCheckDistance)) * (avoidanceForceStrength * 0.5f);
        }

        if (avoidanceOffset.sqrMagnitude > 0.01f)
        {
            hasBypassTarget = true;
            // 返回偏移后的目标点
            return desiredTarget + avoidanceOffset * 3f;
        }

        return desiredTarget;
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

        return Physics.SphereCast(
            new Ray(GetObstacleProbeOrigin(), direction.normalized),
            obstacleProbeRadius,
            out hit,
            obstacleForwardCheckDistance,
            obstacleLayers,
            QueryTriggerInteraction.Ignore);
    }

    private bool IsDirectPathClear(Vector3 target)
    {
        Vector3 direction = target - transform.position;
        direction.y = 0f;
        float distance = direction.magnitude;
        if (distance < 0.15f)
        {
            return true;
        }

        return !Physics.SphereCast(
            new Ray(GetObstacleProbeOrigin(), direction.normalized),
            obstacleProbeRadius,
            out _,
            distance,
            obstacleLayers,
            QueryTriggerInteraction.Ignore);
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

        if (hasBypassTarget || obstacleHoldUntil > Time.time || !IsDirectPathClear(desiredTarget))
        {
            hasBypassTarget = false;
            obstacleHoldUntil = Time.time + obstacleRetryHoldSeconds;
            if (logAvoidance)
            {
                Debug.LogWarning($"[AME] {props?.AgentID ?? name} 在局部障碍附近卡住，优先继续局部绕障");
            }
            return true;
        }

        return false;
    }

    private bool TryReplanPath(Vector3 finalGoal, float height, out List<Vector3> replannedPath)
    {
        replannedPath = new List<Vector3>();
        Vector2Int nowCell = campusGrid.WorldToGrid(transform.position);
        Vector2Int goalCell = campusGrid.WorldToGrid(finalGoal);
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
        obstacleHoldUntil = 0f;
        activeAvoidanceCollider = null;
        activeAvoidanceSide = string.Empty;

        if (hadAvoidance && logRestore && logAvoidance && !string.IsNullOrWhiteSpace(reason))
        {
            Debug.Log($"[AME] {props?.AgentID ?? name} {reason}");
        }
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
