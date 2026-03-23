// Other_Modules/AgentMotionExecutor.cs
// 替换原 MLAgentsController.cs，负责驱动无人机 Rigidbody 执行 ADM 输出的原子动作。
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

    // ─── 私有状态 ─────────────────────────────────────────────────
    private Rigidbody          rb;
    private ActionDecisionModule adm;
    private CampusGrid2D       campusGrid;
    private AgentProperties    props;
    private IntelligentAgent   agent;

    private AtomicAction       currentAction;
    private bool               actionRunning;
    private Coroutine          actionCoroutine;

    // MoveTo 路径缓存
    private List<Vector3>      currentPath    = new();
    private int                waypointIdx    = 0;

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
        props      = agent?.Properties;

        if (props != null && props.MaxSpeed > 0f)
            maxSpeed = props.MaxSpeed;
    }

    private void Update()
    {
        ApplyHoverForce();
        PollADM();
    }

    private void FixedUpdate()
    {
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
            case AtomicActionType.PatrolAround:
                yield return StartCoroutine(DoPatrol(action));
                break;
            case AtomicActionType.Observe:
                yield return StartCoroutine(DoObserve(action));
                break;
            case AtomicActionType.Wait:
                yield return StartCoroutine(DoWait(action));
                break;
            case AtomicActionType.FormationHold:
                yield return StartCoroutine(DoFormation(action));
                break;
            case AtomicActionType.Broadcast:
                yield return StartCoroutine(DoBroadcast(action));
                break;
            case AtomicActionType.Evade:
                yield return StartCoroutine(DoEvade(action));
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
        if (!campusGrid.TryGetFeatureFirstCell(action.targetName, out Vector2Int goalCell, preferWalkable: true))
        {
            Debug.LogWarning($"[AME] MoveTo: 找不到目标 '{action.targetName}'");
            yield break;
        }

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

        // 4. 沿 waypoint 飞行
        while (waypointIdx < currentPath.Count)
        {
            Vector3 wp = currentPath[waypointIdx];
            FaceToward(wp);
            ApplyHorizontalPD(wp);

            float dist = Vector3.Distance(
                new Vector3(transform.position.x, 0f, transform.position.z),
                new Vector3(wp.x, 0f, wp.z));

            if (dist < 1.5f)
            {
                waypointIdx++;
            }

            yield return null;
        }

        pathVisualizer?.ClearPath();
        Debug.Log($"[AME] MoveTo '{action.targetName}' 完成");
    }

    // ─── PatrolAround ─────────────────────────────────────────────

    private IEnumerator DoPatrol(AtomicAction action)
    {
        float duration = action.duration > 0f ? action.duration : 10f;
        float radius   = ParseRadiusFromParams(action.actionParams, defaultRadius: 5f);

        Vector3 center = transform.position;
        if (campusGrid != null && !string.IsNullOrWhiteSpace(action.targetName) &&
            campusGrid.TryGetFeatureFirstCell(action.targetName, out Vector2Int tc, true))
        {
            center = campusGrid.GridToWorldCenter(tc.x, tc.y);
            center.y = hoverHeight;
        }

        float elapsed = 0f;
        float angle   = 0f;
        float speed   = 30f; // 度/秒绕圈

        while (elapsed < duration)
        {
            angle += speed * Time.deltaTime;
            float rad  = angle * Mathf.Deg2Rad;
            Vector3 wp = center + new Vector3(Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);
            wp.y = hoverHeight;
            ApplyHorizontalPD(wp);
            FaceToward(wp);
            elapsed += Time.deltaTime;
            yield return null;
        }
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

    // ─── FormationHold ────────────────────────────────────────────

    private IEnumerator DoFormation(AtomicAction action)
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
        }

        Vector3 offset = new Vector3(3f, 0f, 0f); // 默认侧方偏移

        while (elapsed < duration)
        {
            if (target != null)
            {
                Vector3 formPos = target.transform.position + offset;
                formPos.y = hoverHeight;
                ApplyHorizontalPD(formPos);
                FaceToward(target.transform.position);
            }
            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    // ─── Broadcast ────────────────────────────────────────────────

    private IEnumerator DoBroadcast(AtomicAction action)
    {
        var comm = GetComponent<CommunicationModule>();
        if (comm != null && !string.IsNullOrWhiteSpace(action.broadcastContent))
        {
            var update = new AgentContextUpdate
            {
                agentId       = props?.AgentID ?? "unknown",
                locationName  = "broadcast",
                currentAction = "Broadcast",
                currentTarget = action.targetName ?? string.Empty,
                role          = props?.Role.ToString() ?? string.Empty,
                plannedTargets = new string[0],
                recentEvents  = new string[] { action.broadcastContent },
                timestamp     = Time.time
            };
            comm.SendScopedMessage(
                CommunicationScope.Team,
                MessageType.BoardUpdate,
                update,
                targetTeamId: props?.TeamID.ToString(),
                reliable: true);
        }
        yield return null;
    }

    // ─── Evade ────────────────────────────────────────────────────

    private IEnumerator DoEvade(AtomicAction action)
    {
        Vector3 hVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        Vector3 evadeDir = hVel.sqrMagnitude > 0.01f
            ? Vector3.Cross(hVel.normalized, Vector3.up).normalized
            : transform.right;

        Vector3 evadeTarget = transform.position + evadeDir * 3f;
        evadeTarget.y = hoverHeight;

        float elapsed = 0f;
        while (elapsed < 1.5f)
        {
            ApplyHorizontalPD(evadeTarget);
            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────────────────────

    /// <summary>从 actionParams 字符串中提取半径数值（如"环绕半径40米"→40f）。</summary>
    private static float ParseRadiusFromParams(string actionParams, float defaultRadius)
    {
        if (string.IsNullOrWhiteSpace(actionParams)) return defaultRadius;
        var m = System.Text.RegularExpressions.Regex.Match(actionParams, @"半径\s*(\d+(?:\.\d+)?)");
        if (m.Success && float.TryParse(m.Groups[1].Value, out float r) && r > 0f) return r;
        return defaultRadius;
    }

    private Color GetTeamColor()
    {
        if (props == null) return Color.white;
        // 用 TeamID 哈希出一个颜色
        float h = (props.TeamID * 0.618f) % 1f;
        return Color.HSVToRGB(h, 0.7f, 1f);
    }
}
