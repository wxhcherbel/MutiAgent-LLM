// Other_Modules/AgentMotionExecutor.cs
// 替换原 MLAgentsController.cs，负责驱动无人机 Rigidbody 执行 ADM 输出的原子动作。
using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 无人机运动执行器：每帧轮询 ActionDecisionModule.GetCurrentAction()，
/// 驱动 Rigidbody 执行对应原子动作，完成后通知 ADM 推进队列。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class AgentMotionExecutor : MonoBehaviour
{
    // ─── Inspector 配置 ───────────────────────────────────────────
    [Header("悬停高度")]
    public float hoverHeight = 5f;          // 悬停目标高度（米）
    public float hoverSpring = 8f;          // 高度弹簧刚度
    public float hoverDamp   = 4f;          // 高度阻尼

    [Header("水平移动 PD")]
    public float pdKp = 6f;                 // 位置增益
    public float pdKd = 3f;                 // 速度阻尼增益

    [Header("转向")]
    public float maxAngularSpeed = 90f;     // 最大旋转速度（度/秒）

    [Header("速度限制")]
    public float maxSpeed = 8f;             // 水平最大速度（m/s），由 AgentProperties.MaxSpeed 覆盖

    [Header("机体倾斜（视觉）")]
    public float tiltMultiplier = 15f;      // 倾斜角度倍率

    [Header("路径可视化")]
    public AStarPathVisualizer pathVisualizer; // 可选：路径可视化组件

    [Header("局部避障（APF 人工势场）")]
    [SerializeField] private float apfInfluenceRadius = 8f;  // 排斥力感应半径（m）
    [SerializeField] private float apfStrength        = 60f; // 排斥力强度系数

    // ─── 私有状态 ─────────────────────────────────────────────────
    private Rigidbody          rb;
    private ActionDecisionModule adm;
    private CampusGrid2D       campusGrid;
    private AgentProperties    props;
    private IntelligentAgent   agent;
    private MemoryModule       _memoryModule;

    private AtomicAction       currentAction;
    private bool               actionRunning;
    private Coroutine          actionCoroutine;

    // MoveTo 路径缓存
    private List<Vector3>      currentPath    = new();
    private int                waypointIdx    = 0;

    private AgentDynamicState  agentState;

    // 起飞/降落用：记录初始悬停高度以便 Takeoff 恢复
    private float              defaultHoverHeight;

    // ─────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity  = false;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
    }

    private void Start()
    {
        adm        = GetComponent<ActionDecisionModule>();
        campusGrid = FindObjectOfType<CampusGrid2D>();
        agent      = GetComponent<IntelligentAgent>();
        props         = agent?.Properties;
        agentState    = agent?.CurrentState;
        _memoryModule = GetComponent<MemoryModule>();

        if (props != null && props.MaxSpeed > 0f)
            maxSpeed = props.MaxSpeed;

        defaultHoverHeight = hoverHeight;
    }

    private void Update()
    {
        ApplyHoverForce();
        PollADM();
    }

    private void FixedUpdate()
    {
        ApplyObstacleRepulsion();
        ClampSpeed();
        ApplyTilt();
    }

    // ─────────────────────────────────────────────────────────────
    // 物理辅助
    // ─────────────────────────────────────────────────────────────

    private void ApplyHoverForce()
    {
        float heightError = hoverHeight - transform.position.y;
        float vertForce   = hoverSpring * heightError - hoverDamp * rb.velocity.y;
        // 补偿重力
        vertForce += Mathf.Abs(Physics.gravity.y) * rb.mass;
        rb.AddForce(Vector3.up * vertForce, ForceMode.Force);
    }

    private void ApplyHorizontalPD(Vector3 targetXZ)
    {
        Vector3 pos    = new Vector3(transform.position.x, 0f, transform.position.z);
        Vector3 tgt    = new Vector3(targetXZ.x, 0f, targetXZ.z);
        Vector3 vel    = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        Vector3 error  = tgt - pos;
        Vector3 force  = pdKp * error - pdKd * vel;
        rb.AddForce(force, ForceMode.Acceleration);
    }

    // ─── APF 局部障碍物排斥力（持久化，FixedUpdate 每帧调用，覆盖所有动作）────
    private void ApplyObstacleRepulsion()
    {
        var nodes = agentState?.DetectedSmallNodes;
        if (nodes == null || nodes.Count == 0) return;

        Vector3 repulsion = Vector3.zero;
        Vector3 myPosFlat = new Vector3(transform.position.x, 0f, transform.position.z);

        foreach (var node in nodes)
        {
            if (node.NodeType != SmallNodeType.Tree
             && node.NodeType != SmallNodeType.Pedestrian
             && node.NodeType != SmallNodeType.Vehicle
             && node.NodeType != SmallNodeType.TemporaryObstacle
             && node.NodeType != SmallNodeType.Agent) continue;

            Vector3 obsPosFlat = new Vector3(node.WorldPosition.x, 0f, node.WorldPosition.z);
            Vector3 toObs      = obsPosFlat - myPosFlat;
            float   dist       = toObs.magnitude;

            if (dist < 0.05f || dist >= apfInfluenceRadius) continue;

            // APF: F = k × (1/d − 1/d₀)² / d² × 反向
            float eta       = 1f / dist - 1f / apfInfluenceRadius;
            float magnitude = apfStrength * eta * eta / (dist * dist);
            repulsion      += (-toObs.normalized) * magnitude;
        }

        if (repulsion.sqrMagnitude > 0.001f)
            rb.AddForce(repulsion, ForceMode.Acceleration);
    }

    private void ClampSpeed()
    {
        Vector3 hVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        if (hVel.magnitude > maxSpeed)
        {
            hVel = hVel.normalized * maxSpeed;
            rb.velocity = new Vector3(hVel.x, rb.velocity.y, hVel.z);
        }
    }

    private void ApplyTilt()
    {
        Vector3 hAccel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        if (hAccel.magnitude < 0.1f) return;
        Vector3 tiltAxis  = Vector3.Cross(Vector3.up, hAccel.normalized);
        float   tiltAngle = Mathf.Clamp(hAccel.magnitude * tiltMultiplier, 0f, 25f);
        Quaternion tilt   = Quaternion.AngleAxis(tiltAngle, tiltAxis);
        transform.rotation = Quaternion.Slerp(transform.rotation, tilt, Time.fixedDeltaTime * 5f);
    }

    private void FaceToward(Vector3 targetPos)
    {
        Vector3 dir = targetPos - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) return;
        Quaternion desired = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, desired, maxAngularSpeed * Time.deltaTime);
    }

    // ─────────────────────────────────────────────────────────────
    // ADM 轮询
    // ─────────────────────────────────────────────────────────────

    private void PollADM()
    {
        if (adm == null) return;

        AtomicAction next = adm.GetCurrentAction();

        if (next == null)
        {
            // 没有动作：漂移制动
            if (!actionRunning)
                ApplyBrake();
            return;
        }

        // 动作切换时启动新协程
        if (!actionRunning || currentAction?.actionId != next.actionId)
        {
            if (actionCoroutine != null) StopCoroutine(actionCoroutine);
            currentAction  = next;
            actionRunning  = true;
            actionCoroutine = StartCoroutine(ExecuteAction(next));
        }
    }

    private void ApplyBrake()
    {
        Vector3 hVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        rb.AddForce(-hVel * pdKd, ForceMode.Acceleration);
    }

    // ─────────────────────────────────────────────────────────────
    // 动作执行协程
    // ─────────────────────────────────────────────────────────────

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
                Debug.LogWarning($"[AME] 未知动作类型 {action.type}，跳过");
                break;
        }

        actionRunning = false;
        adm.CompleteCurrentAction();
    }

    // ─── MoveTo ──────────────────────────────────────────────────

    private IEnumerator DoMoveTo(AtomicAction action)
    {
        if (campusGrid == null)
        {
            Debug.LogWarning("[AME] MoveTo: CampusGrid2D 不可用，跳过");
            yield break;
        }

        // 1. 查找目标世界坐标
        Debug.Log($"[AME] {props?.AgentID} DoMoveTo 开始: '{action.targetName}'");
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "move_start",
            $"▷ 前往 {action.targetName}");
        if (!campusGrid.TryGetFeatureApproachCells(action.targetName, transform.position,
                out Vector2Int[] approachArr, maxCount: 1)
            || approachArr.Length == 0)
        {
            Debug.LogWarning($"[AME] MoveTo: 找不到目标 '{action.targetName}' 的接近点");
            yield break;
        }
        Vector2Int goalCell = approachArr[0];

        Vector3 goalWorld = campusGrid.GridToWorldCenter(goalCell.x, goalCell.y, hoverHeight);
        goalWorld.y = hoverHeight;

        // 2. A* 路径
        Vector2Int startCell = campusGrid.WorldToGrid(transform.position);
        List<Vector2Int> gridPath = campusGrid.FindPathAStar(startCell, goalCell);

        if (gridPath == null || gridPath.Count == 0)
        {
            Debug.LogWarning($"[AME] MoveTo: A* 无法找到路径至 '{action.targetName}'，直线飞行");
            gridPath = new List<Vector2Int> { goalCell };
        }

        // 3. 转换为世界坐标路径
        currentPath = gridPath
            .Select(c => { var w = campusGrid.GridToWorldCenter(c.x, c.y); w.y = hoverHeight; return w; })
            .ToList();
        waypointIdx = 0;

        // 可视化
        Color teamColor = GetTeamColor();
        pathVisualizer?.ShowPath(currentPath, teamColor);

        // 4. 沿 waypoint 飞行（APF 排斥力由 FixedUpdate 持续施加，无需在此重规划）
        const float ARRIVE_THRESHOLD = 3f;   // 到达判定半径（m）
        const float WAYPOINT_TIMEOUT = 12f;  // 单航点超时（s），超时自动跳过
        float waypointTimer = 0f;

        while (waypointIdx < currentPath.Count)
        {
            Vector3 wp = currentPath[waypointIdx];
            FaceToward(wp);
            ApplyHorizontalPD(wp);

            float dist = Vector3.Distance(
                new Vector3(transform.position.x, 0f, transform.position.z),
                new Vector3(wp.x, 0f, wp.z));

            waypointTimer += Time.deltaTime;
            if (dist < ARRIVE_THRESHOLD || waypointTimer >= WAYPOINT_TIMEOUT)
            {
                if (waypointTimer >= WAYPOINT_TIMEOUT)
                {
                    Debug.LogWarning($"[AME] '{action.targetName}' 航点[{waypointIdx}] 超时跳过（dist={dist:F1}m）");
                    AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "waypoint_timeout",
                        $"⚡ 航点[{waypointIdx}]超时(dist={dist:F1}m)");
                }
                waypointIdx++;
                waypointTimer = 0f;
            }

            yield return null;
        }

        pathVisualizer?.ClearPath();
        Debug.Log($"[AME] MoveTo '{action.targetName}' 完成");
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "arrive",
            $"✓ 到达 {action.targetName}");
    }

    // ─── Observe ──────────────────────────────────────────────────

    private IEnumerator DoObserve(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 3f;
        float elapsed  = 0f;

        // 悬停原地
        Vector3 holdPos = transform.position;
        holdPos.y = hoverHeight;

        // 触发感知
        var perception = GetComponent<PerceptionModule>();
        perception?.SenseOnce();

        while (elapsed < duration)
        {
            ApplyHorizontalPD(holdPos);
            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    // ─── Wait ─────────────────────────────────────────────────────

    private IEnumerator DoWait(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 2f;
        float elapsed  = 0f;
        Vector3 holdPos = transform.position;
        holdPos.y = hoverHeight;

        while (elapsed < duration)
        {
            ApplyHorizontalPD(holdPos);
            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    // ─── Track ────────────────────────────────────────────────────

    private IEnumerator DoTrack(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 10f;
        float elapsed  = 0f;

        IntelligentAgent target = null;
        if (!string.IsNullOrWhiteSpace(action.targetAgentId))
        {
            foreach (var a in FindObjectsOfType<IntelligentAgent>())
            {
                if (a.Properties?.AgentID == action.targetAgentId) { target = a; break; }
            }
            if (target == null)
                Debug.LogWarning($"[AME] Track: 找不到目标智能体 '{action.targetAgentId}'");
        }

        Vector3 localOffset = ParseOffsetFromParams(action.actionParams);

        while (elapsed < duration)
        {
            if (target != null)
            {
                // 偏移跟随目标的局部坐标系，使"前/后/左/右"语义与目标朝向一致
                Vector3 trackPos = target.transform.position + target.transform.rotation * localOffset;
                trackPos.y = hoverHeight;
                ApplyHorizontalPD(trackPos);
                FaceToward(target.transform.position);
            }
            elapsed += Time.deltaTime;
            yield return null;
        }
        Debug.Log($"[AME] Track '{action.targetAgentId}' 完成");
    }

    // ─── Signal ───────────────────────────────────────────────────

    private IEnumerator DoSignal(AtomicAction action)
    {
        string content   = string.IsNullOrWhiteSpace(action.actionParams) ? "（Signal）" : action.actionParams;
        string recipient = string.IsNullOrWhiteSpace(action.targetAgentId) ? "all" : action.targetAgentId;

        var commModule = GetComponent<CommunicationModule>();
        if (commModule != null)
        {
            if (recipient == "all")
            {
                foreach (var a in FindObjectsOfType<IntelligentAgent>())
                {
                    string rid = a.Properties?.AgentID;
                    if (!string.IsNullOrWhiteSpace(rid) && rid != props?.AgentID)
                        commModule.SendMessage(rid, MessageType.StatusUpdate, content);
                }
            }
            else
            {
                commModule.SendMessage(recipient, MessageType.StatusUpdate, content);
            }
        }
        else
        {
            Debug.LogWarning("[AME] Signal: 找不到 CommunicationModule，消息未发送");
        }

        Debug.Log($"[AME] Signal → {recipient}: {content}");
        yield break; // 立即完成，无持续时间
    }

    // ─── Get ──────────────────────────────────────────────────────

    private IEnumerator DoGet(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 1f;
        Vector3 holdPos = transform.position;
        holdPos.y = hoverHeight;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            ApplyHorizontalPD(holdPos);
            elapsed += Time.deltaTime;
            yield return null;
        }
        Debug.Log($"[AME] Get '{action.targetName}' 完成");
    }

    // ─── Put ──────────────────────────────────────────────────────

    private IEnumerator DoPut(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 1f;
        Vector3 holdPos = transform.position;
        holdPos.y = hoverHeight;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            ApplyHorizontalPD(holdPos);
            elapsed += Time.deltaTime;
            yield return null;
        }
        Debug.Log($"[AME] Put '{action.targetName}' 完成");
    }

    // ─── Land ─────────────────────────────────────────────────────

    private IEnumerator DoLand(AtomicAction action)
    {
        const float TARGET_HEIGHT  = 0.2f;
        const float DESCEND_SPEED  = 2f;   // m/s
        const float TIMEOUT        = 15f;
        float elapsed = 0f;

        while (hoverHeight > TARGET_HEIGHT + 0.05f && elapsed < TIMEOUT)
        {
            hoverHeight = Mathf.MoveTowards(hoverHeight, TARGET_HEIGHT, DESCEND_SPEED * Time.deltaTime);
            elapsed += Time.deltaTime;
            yield return null;
        }
        hoverHeight = TARGET_HEIGHT;
        Debug.Log($"[AME] Land 完成，高度={transform.position.y:F1}m");
    }

    // ─── Takeoff ──────────────────────────────────────────────────

    private IEnumerator DoTakeoff(AtomicAction action)
    {
        const float ASCEND_SPEED = 2f;  // m/s
        const float TIMEOUT      = 15f;
        float elapsed = 0f;

        while (Mathf.Abs(hoverHeight - defaultHoverHeight) > 0.1f && elapsed < TIMEOUT)
        {
            hoverHeight = Mathf.MoveTowards(hoverHeight, defaultHoverHeight, ASCEND_SPEED * Time.deltaTime);
            elapsed += Time.deltaTime;
            yield return null;
        }
        hoverHeight = defaultHoverHeight;
        Debug.Log($"[AME] Takeoff 完成，高度={transform.position.y:F1}m");
    }

    // ─── Patrol ──────────────────────────────────────────────────

    private IEnumerator DoPatrol(AtomicAction action)
    {
        float totalDuration = action.duration > 0f ? action.duration : 60f;
        bool  withObserve   = action.actionParams?.Contains("observe") == true;

        // ── 0. targetName 为空时查空闲度自动选区 ─────────────────────
        string resolvedTarget = action.targetName;
        if (string.IsNullOrWhiteSpace(resolvedTarget) && _memoryModule != null)
        {
            resolvedTarget = _memoryModule.GetHighestIdlenessArea();
            if (!string.IsNullOrWhiteSpace(resolvedTarget))
                Debug.Log($"[AME] Patrol: targetName 为空，按空闲度选区 → '{resolvedTarget}'");
        }

        // ── 1. 获取巡逻格子集合 ─────────────────────────────────────
        // 开放区域（操场等）：occupied 内可行走格；建筑外围：全周接近格（大 maxCount 覆盖各方向）
        List<Vector2Int> patrolCells = new List<Vector2Int>();

        if (campusGrid != null && !string.IsNullOrWhiteSpace(resolvedTarget))
        {
            if (campusGrid.TryGetFeatureOccupiedCells(resolvedTarget, out Vector2Int[] occupied))
                patrolCells = occupied.Where(c => campusGrid.IsWalkable(c.x, c.y)).ToList();

            if (patrolCells.Count < 3)
            {
                // 建筑/禁飞区：取全周外围接近格（maxCount 足够大，保证覆盖各方向）
                campusGrid.TryGetFeatureApproachCells(resolvedTarget, transform.position,
                    out Vector2Int[] approach, maxCount: 256);
                patrolCells = approach?.ToList() ?? patrolCells;
            }
        }
        else if (campusGrid != null)
        {
            Vector2Int center = campusGrid.WorldToGrid(transform.position);
            const int R = 20;
            for (int dx = -R; dx <= R; dx++)
            for (int dz = -R; dz <= R; dz++)
            {
                int x = center.x + dx, z = center.y + dz;
                if (campusGrid.IsInBounds(x, z) && campusGrid.IsWalkable(x, z))
                    patrolCells.Add(new Vector2Int(x, z));
            }
        }

        if (patrolCells.Count == 0)
        {
            Debug.LogWarning("[AME] Patrol: 无可用巡逻格，跳过");
            yield break;
        }

        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "patrol_start",
            $"⬡ 巡逻 '{resolvedTarget}'，{patrolCells.Count} 个格子，时长 {totalDuration}s");

        // ── 2. Frontier + A* 覆盖循环 ──────────────────────────────
        // Frontier 负责"挑哪个格"（就近未访问，体现智能性和随机性）
        // A* 负责"怎么飞过去"（绕过建筑/禁飞区，不再直飞）
        var   visited           = new HashSet<Vector2Int>();
        float elapsed           = 0f;
        const float VISIT_RADIUS_M = 8f;
        const float WP_TIMEOUT     = 12f;
        const float ARRIVE_DIST    = 4f;

        while (elapsed < totalDuration && visited.Count < patrolCells.Count)
        {
            // a. Frontier：找当前位置最近的未访问格
            Vector2Int agentCell = campusGrid.WorldToGrid(transform.position);
            Vector2Int? target   = null;
            float minDist = float.MaxValue;
            foreach (var c in patrolCells)
            {
                if (visited.Contains(c)) continue;
                float d = Vector2.Distance((Vector2)agentCell, (Vector2)c);
                if (d < minDist) { minDist = d; target = c; }
            }
            if (target == null) break;

            // b. A* 规划到目标格的路径（自动绕过建筑/禁飞区）
            List<Vector2Int> segPath = campusGrid.FindPathAStar(agentCell, target.Value);
            if (segPath == null || segPath.Count == 0)
                segPath = new List<Vector2Int> { target.Value }; // A* 失败则直飞兜底

            // c. 逐航点跟随（与 DoMoveTo 相同逻辑）
            foreach (var cell in segPath)
            {
                if (elapsed >= totalDuration) break;
                Vector3 wp      = campusGrid.GridToWorldCenter(cell.x, cell.y, hoverHeight);
                float   wpTimer = 0f;
                while (wpTimer < WP_TIMEOUT && elapsed < totalDuration)
                {
                    FaceToward(wp);
                    ApplyHorizontalPD(wp);
                    Vector2 posXZ = new Vector2(transform.position.x, transform.position.z);
                    Vector2 wpXZ  = new Vector2(wp.x, wp.z);
                    if (Vector2.Distance(posXZ, wpXZ) < ARRIVE_DIST) break;
                    wpTimer += Time.deltaTime;
                    elapsed += Time.deltaTime;
                    yield return null;
                }
            }

            // d. 标记周边格为已访问
            Vector2Int nowCell          = campusGrid.WorldToGrid(transform.position);
            float      visitRadiusCells = VISIT_RADIUS_M / campusGrid.cellSize;
            foreach (var c in patrolCells)
                if (Vector2.Distance((Vector2)nowCell, (Vector2)c) <= visitRadiusCells)
                    visited.Add(c);

            // e. 可选感知
            if (withObserve) GetComponent<PerceptionModule>()?.SenseOnce();

            // f. 短暂悬停
            float dwell  = UnityEngine.Random.Range(0.5f, 1.5f);
            float dwellE = 0f;
            Vector3 hold = new Vector3(transform.position.x, hoverHeight, transform.position.z);
            while (dwellE < dwell && elapsed < totalDuration)
            {
                ApplyHorizontalPD(hold);
                dwellE += Time.deltaTime; elapsed += Time.deltaTime;
                yield return null;
            }
        }

        float coverage = patrolCells.Count > 0
            ? (float)visited.Count / patrolCells.Count * 100f : 100f;
        AgentStateServer.PushMotionEvent(props?.AgentID ?? name, "patrol_done",
            $"✓ 巡逻完成 '{resolvedTarget}'，覆盖率 {coverage:F0}%，用时 {elapsed:F0}s");

        // ── 3. 写巡逻时间戳（AME 负责，ADM 不写）───────────────────
        if (!string.IsNullOrWhiteSpace(resolvedTarget))
            _memoryModule?.RecordPatrolEvent(resolvedTarget, DateTime.Now);
    }

    // ─────────────────────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────────────────────

    /// <summary>从 actionParams 解析相对偏移方向（局部坐标系）。
    /// 支持关键词：前、后、左、右；默认右侧 3m。</summary>
    private static Vector3 ParseOffsetFromParams(string actionParams)
    {
        if (string.IsNullOrWhiteSpace(actionParams)) return new Vector3(3f, 0f, 0f);
        string p = actionParams;
        if (p.Contains("前")) return new Vector3(0f, 0f, 3f);
        if (p.Contains("后")) return new Vector3(0f, 0f, -3f);
        if (p.Contains("左")) return new Vector3(-3f, 0f, 0f);
        if (p.Contains("右")) return new Vector3(3f, 0f, 0f);
        return new Vector3(3f, 0f, 0f);
    }

    private Color GetTeamColor()
    {
        if (props == null) return Color.white;
        // 用 TeamID 哈希出一个颜色
        float h = (props.TeamID * 0.618f) % 1f;
        return Color.HSVToRGB(h, 0.7f, 1f);
    }
}
