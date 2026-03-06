using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Globalization;
using System.Text.RegularExpressions;


// 辅助类：解析相对移动参数（用于MoveToRelative）
[System.Serializable]
public class RelativeOffsetParams
{
    public float x;
    public float y;
    public float z;
}
// 辅助类用于解析参数
[System.Serializable]
public class ParameterWrapper
{
    public float? speed;      // 问号表示可空
    public float? height;
    public float? duration;
    public float? rotX;
    public float? rotY;
    public float? rotZ;
    public float? followDist;
    public float? radius;
    public float? orbitSpeed;
}
public class MLAgentsController : MonoBehaviour
{
    [Header("核心组件")]
    public IntelligentAgent intelligentAgent; // 智能体核心组件
    private Rigidbody rb;                     // 物理组件
    private PerceptionModule perceptionModule; // 感知模块
    private ActionCommand currentCommand;      // 当前执行的动作命令
    private bool isExecutingCommand => currentCommand != null;

    [Header("环境约束")]
    public float gridWidth;         // 仅用于调试显示（由 CampusGrid2D 推导）
    public float gridLength;        // 仅用于调试显示（由 CampusGrid2D 推导）
    public float boundaryBuffer = 2f;     // 边界缓冲距离
    public float safeDistance = 3f;       // 障碍物安全距离

    [Header("智能体特性")]
    public float maxSpeed = 5f;           // 最大线速度（默认值）
    private float tempSpeed;              // 临时速度（由ActionCommand指定）
    public float maxAngularSpeed = 90f;   // 最大角速度（度/秒）
    public float droneMaxHeight = 15f;    // 无人机最大高度
    public float droneMinHeight = 0.5f;   // 无人机最小高度
    public float takeOffHeight = 2f;      // 起飞目标高度
    public float hoverStabilityThreshold = 0.1f; // 悬停稳定性阈值

    [Header("无人机飞行调校")]
    [Min(1f)] public float droneCruiseSpeed = 8f;                 // 巡航速度
    [Min(1f)] public float droneMaxHorizontalAccel = 14f;         // 水平加速度上限
    [Min(1f)] public float droneMaxHorizontalBrakeAccel = 18f;    // 水平制动上限
    [Min(0.5f)] public float droneArrivalSlowRadius = 6f;         // 进入减速区半径
    [Min(0.2f)] public float droneFinalApproachRadius = 1.2f;     // 最终进近半径
    [Range(0f, 35f)] public float droneMaxPitchAngle = 14f;       // 最大前倾角
    [Range(0f, 35f)] public float droneMaxRollAngle = 18f;        // 最大侧倾角
    [Min(0.5f)] public float droneYawResponse = 6f;               // 偏航响应速度
    [Min(0.5f)] public float droneTiltResponse = 7f;              // 倾斜响应速度
    [Min(0f)] public float droneHoverDrag = 2.4f;                 // 悬停水平阻尼
    [Min(0f)] public float droneCruiseDrag = 0.9f;                // 巡航水平阻尼

    [Header("动作精度参数")]
    public float rotatePrecision = 2f;    // 旋转精度（度）
    public float positionPrecision = 1f;// 位置精度（米）
    public float scanRange = 10f;         // 扫描范围

    [Header("动作完成判定(无人机)")]
    [Min(0.05f)] public float completionSettleTime = 0.2f; // 条件连续满足该时长才判完成
    [Min(0.05f)] public float droneAltitudeTolerance = 0.18f; // 高度误差阈值
    [Min(0.05f)] public float droneHorizontalSpeedTolerance = 0.45f; // 水平速度阈值
    [Min(0.05f)] public float droneVerticalSpeedTolerance = 0.25f;   // 垂直速度阈值
    [Min(0.05f)] public float takeoffLandHeightTolerance = 0.15f;    // 起降高度阈值
    [Min(1f)] public float maxVerticalControlAccel = 12f;            // 垂向控制最大等效加速度
    [Min(0.05f)] public float minMoveHorizontalTolerance = 0.35f;    // MoveTo 最小水平容差

    [Header("动作序列")]
    public Queue<ActionCommand> actionSequence = new Queue<ActionCommand>(); // 原子动作序列队列
    public float actionTimeout = 100f;     // 单个动作超时时间

    // 状态跟踪
    private float actionStartTime;
    private float actionStableStartTime = -1f; // 当前动作“连续稳定”起始时间
    private HashSet<Vector3Int> exploredNodes = new HashSet<Vector3Int>(); // 探索记录
    private GameObject heldObject;        // 持有的物体
    private int resourcesDelivered = 0;   // 已运送资源计数
    private int totalResources = 0;       // 总资源数

    private ActionCompletedCallback currentActionCallback; // 存储当前动作的回调函数
    // 定义动作完成回调委托
    public delegate void ActionCompletedCallback(PrimitiveActionType actionType, bool success, string result);

    // 外部模块引用
    private AgentSpawner agentSpawner;
    private CampusGrid2D campusGrid;

    // 仅使用 CampusGrid2D 的真实世界边界做约束
    private bool useAbsoluteWorldBounds = false;
    private float worldMinX;
    private float worldMaxX;
    private float worldMinZ;
    private float worldMaxZ;

    private void Awake()
    {
        intelligentAgent = GetComponent<IntelligentAgent>();
        rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        perceptionModule = GetComponent<PerceptionModule>();
        SyncMotionConfigFromAgentProperties();
        ConfigureRigidbody();
        tempSpeed = maxSpeed;
    }
    private void ConfigureRigidbody()
    {
        if (rb == null) return;
        rb.mass = 1f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.maxAngularVelocity = Mathf.Max(10f, maxAngularSpeed * Mathf.Deg2Rad * 1.5f);
        
        if (intelligentAgent != null && intelligentAgent.Properties.Type == AgentType.Quadcopter)
        {
            rb.useGravity = true;
            rb.constraints = RigidbodyConstraints.None;
            rb.drag = 0.25f;
            rb.angularDrag = 4.5f;
        }
        else
        {
            rb.useGravity = true;
            rb.drag = 0.1f;
            rb.angularDrag = 0.1f;
            rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }
    }

    private void SyncMotionConfigFromAgentProperties()
    {
        if (intelligentAgent?.Properties == null) return;

        if (intelligentAgent.Properties.MaxSpeed > 0.01f)
        {
            maxSpeed = intelligentAgent.Properties.MaxSpeed;
        }
        if (intelligentAgent.Properties.MaxAngularSpeed > 0.01f)
        {
            maxAngularSpeed = intelligentAgent.Properties.MaxAngularSpeed;
        }

        if (intelligentAgent.Properties.Type == AgentType.Quadcopter)
        {
            droneCruiseSpeed = Mathf.Max(droneCruiseSpeed, maxSpeed);
            droneMaxHorizontalAccel = Mathf.Max(6f, intelligentAgent.Properties.Acceleration > 0.01f ? intelligentAgent.Properties.Acceleration * 2.2f : droneMaxHorizontalAccel);
            droneMaxHorizontalBrakeAccel = Mathf.Max(droneMaxHorizontalAccel, droneMaxHorizontalBrakeAccel);
            takeOffHeight = Mathf.Clamp(Mathf.Max(takeOffHeight, intelligentAgent.Properties.PhysicalSize.y * 1.5f), droneMinHeight + 0.5f, droneMaxHeight);
        }
    }

    public void Start()
    {
        // 获取外部模块引用
        agentSpawner = FindObjectOfType<AgentSpawner>();
        campusGrid = FindObjectOfType<CampusGrid2D>();
        SyncMotionConfigFromAgentProperties();
        ConfigureRigidbody();

        if (campusGrid != null)
        {
            ConfigureBoundaryFromCampusGrid(campusGrid);
        }
        else
        {
            useAbsoluteWorldBounds = false;
            Debug.LogWarning("[MLAgentsController] 未找到 CampusGrid2D，边界钳制将禁用。");
        }
    }

    /// <summary>
    /// 使用 CampusGrid2D 的 mapBounds（世界 XZ）配置边界。
    /// </summary>
    public void ConfigureBoundaryFromCampusGrid(CampusGrid2D grid)
    {
        if (grid == null) return;

        if (grid.blockedGrid == null || grid.cellTypeGrid == null)
        {
            grid.BuildGridFromCampusJson();
        }

        Rect b = grid.mapBoundsXY;
        if (b.width <= 0f || b.height <= 0f)
        {
            return;
        }

        campusGrid = grid;
        useAbsoluteWorldBounds = true;
        worldMinX = b.xMin;
        worldMaxX = b.xMax;
        worldMinZ = b.yMin;
        worldMaxZ = b.yMax;

        // 保留旧字段，供旧逻辑/归一化使用
        gridWidth = b.width;
        gridLength = b.height;
    }

    private void FixedUpdate()
    {
        // 强制边界约束
        EnforceBoundaryConstraints();

        // 无人机悬停稳定（新增）
        if (intelligentAgent?.Properties.Type == AgentType.Quadcopter && !isExecutingCommand)
        {
            StabilizeDroneHover();
        }
    
        // 执行当前命令
        if (isExecutingCommand)
        {
            // 检查超时
            if (Time.time - actionStartTime > actionTimeout)
            {
                OnActionCompleted(false, "动作超时");
                return;
            }

            // 执行动作逻辑
            ExecuteCurrentCommand();
            
            // 检查动作是否完成
            if (IsActionCompleted())
            {
                OnActionCompleted(true, "动作执行成功");
            }
        }
        // 处理动作序列
        else if (actionSequence.Count > 0)
        {
            SetCurrentCommand(actionSequence.Dequeue(), OnSequenceActionCompleted);
        }
    }
    /// <summary>
    /// 无人机悬停稳定（当没有执行命令时自动稳定）
    /// </summary>
    private void StabilizeDroneHover()
    {
        float targetHoverHeight = takeOffHeight;
        ApplyDroneAltitudeControl(targetHoverHeight, 22f, 9f, maxVerticalControlAccel);
        ApplyDroneHorizontalVelocityControl(Vector3.zero, null);
        ApplyDroneFlightAttitude(Vector3.zero, Vector3.zero, 0f, null);
    }


    /// <summary>
    /// 动作序列中的动作完成回调
    /// </summary>
    private void OnSequenceActionCompleted(PrimitiveActionType actionType, bool success, string result)
    {
        if (actionSequence.Count == 0)
        {
            if (intelligentAgent != null)
            {
                intelligentAgent.CurrentState.Status = AgentStatus.Idle;
            }
        }
    }

    /// <summary>
    /// 设置当前要执行的命令
    /// </summary>
    public void SetCurrentCommand(ActionCommand command, ActionCompletedCallback callback)
    {
        currentCommand = command;
        PrepareCommandForExecution(currentCommand);
        currentActionCallback = callback;
        actionStartTime = Time.time;
        actionStableStartTime = -1f;
        
        if (intelligentAgent != null)
        {
            intelligentAgent.CurrentState.Status = AgentStatus.ExecutingTask;
        }
        
        Debug.Log($"开始执行动作: {command.ActionType}");
    }

    /// <summary>
    /// 根据当前命令和参数补全目标，提升与上层决策模块的兼容性。
    /// </summary>
    private void PrepareCommandForExecution(ActionCommand command)
    {
        if (command == null) return;

        if (command.TargetObject != null)
        {
            command.TargetPosition = command.TargetObject.transform.position;
        }

        var parameters = ParseActionParameters(command.Parameters);

        if (command.ActionType == PrimitiveActionType.RotateTo)
        {
            if (parameters.TryGetValue("rotY", out float rotY))
            {
                command.TargetRotation = Quaternion.Euler(0f, rotY, 0f);
            }
            else if (command.TargetPosition != Vector3.zero && command.TargetPosition != transform.position)
            {
                Vector3 dir = command.TargetPosition - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 1e-4f)
                {
                    command.TargetRotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                }
            }
            else
            {
                command.TargetRotation = transform.rotation;
            }
        }
        else if (command.ActionType == PrimitiveActionType.AdjustAltitude)
        {
            float targetHeight = ResolveDesiredHeightFromParameters(parameters, transform.position.y);
            command.TargetPosition = new Vector3(transform.position.x, targetHeight, transform.position.z);
        }
        else if (command.ActionType == PrimitiveActionType.TakeOff)
        {
            float targetHeight = ResolveDesiredHeightFromParameters(parameters, takeOffHeight);
            command.TargetPosition = new Vector3(transform.position.x, targetHeight, transform.position.z);
        }
        else if (command.ActionType == PrimitiveActionType.Land)
        {
            float targetHeight = ResolveDesiredHeightFromParameters(parameters, droneMinHeight);
            command.TargetPosition = new Vector3(transform.position.x, targetHeight, transform.position.z);
        }
        else if (command.ActionType == PrimitiveActionType.Hover)
        {
            float targetHeight = ResolveDesiredHeightFromParameters(parameters, transform.position.y);
            command.TargetPosition = new Vector3(transform.position.x, targetHeight, transform.position.z);
        }
    }

    /// <summary>
    /// 记录动作序列
    /// </summary>
    public void RecordActionSequence(List<ActionCommand> sequence)
    {
        actionSequence.Clear();
        foreach (var cmd in sequence)
        {
            actionSequence.Enqueue(cmd);
        }
    }

    /// <summary>
    /// 执行当前命令的主逻辑
    /// </summary>
    private void ExecuteCurrentCommand()
    {
        if (currentCommand == null) return;

        // 根据动作类型执行物理逻辑
        switch (currentCommand.ActionType)
        {
            case PrimitiveActionType.MoveTo:
                ExecuteMoveTo(currentCommand.TargetPosition);
                break;
            case PrimitiveActionType.Stop:
                ExecuteStop();
                break;
            case PrimitiveActionType.TakeOff:
                ExecuteTakeOff();
                break;
            case PrimitiveActionType.Land:
                ExecuteLand();
                break;
            case PrimitiveActionType.Hover:
                ExecuteHover();
                break;
            case PrimitiveActionType.RotateTo:
                ExecuteRotateTo(currentCommand.TargetRotation);
                break;
            case PrimitiveActionType.PickUp:
                ExecutePickUp(currentCommand.TargetObject);
                break;
            case PrimitiveActionType.Drop:
                ExecuteDrop(currentCommand.TargetPosition);
                break;
            case PrimitiveActionType.LookAt:
                ExecuteLookAt(currentCommand.TargetPosition);
                break;
            case PrimitiveActionType.Scan:
                ExecuteScan();
                break;
            case PrimitiveActionType.Wait:
                ExecuteWait();
                break;  
            case PrimitiveActionType.AdjustAltitude:
                ExecuteAdjustAltitude();
                break; 
            case PrimitiveActionType.Align:
                ExecuteAlign(currentCommand.TargetPosition);
                break;
            case PrimitiveActionType.Follow:
                ExecuteFollow(currentCommand.TargetObject);
                break;
            case PrimitiveActionType.Orbit:
                ExecuteOrbit(currentCommand.TargetPosition);
                break;
            default:
                break;
        }
    }

    #region 优化后的原子动作执行逻辑（区分无人机/地面车，完善旋转控制）
    /// <summary>
    /// 执行移动到指定位置动作（优化无人机飞行稳定性）
    /// </summary>
    private void ExecuteMoveTo(Vector3 targetPos)
    {
        // 解析速度参数
        var parameters = ParseActionParameters(currentCommand.Parameters);
        var moveSpeed = parameters.TryGetValue("speed", out float ms) ? ms : tempSpeed;

        // 无人机特殊处理：保持当前高度或使用指定高度
        if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
        {
            // 获取目标高度（优先使用参数，否则保持当前高度）
            float targetHeight = parameters.TryGetValue("height", out float height) 
                ? Mathf.Clamp(height, droneMinHeight, droneMaxHeight)
                : transform.position.y;
            
            // 保持水平移动时的目标高度
            targetPos.y = targetHeight;
            
            ExecuteDroneMoveTo(targetPos, moveSpeed, parameters);
        }
        else
        {
            // 地面车辆的原有逻辑
            ExecuteGroundMoveTo(targetPos, moveSpeed, parameters);
        }
    }

    /// <summary>
    /// 无人机专用移动逻辑（平滑稳定的飞行控制）
    /// </summary>
    private void ExecuteDroneMoveTo(Vector3 targetPos, float moveSpeed, Dictionary<string, float> parameters)
    {
        Vector3 toTarget = targetPos - transform.position;
        Vector3 horizontalError = new Vector3(toTarget.x, 0f, toTarget.z);
        float distance = horizontalError.magnitude;

        Vector3 desiredDir = distance > 0.01f ? horizontalError / distance : Vector3.zero;
        if (desiredDir.sqrMagnitude > 0.0001f)
        {
            desiredDir = ApplyObstacleAvoidance(desiredDir).normalized;
        }

        float commandedSpeed = ResolveDroneMoveSpeed(moveSpeed, parameters);
        float slowRadius = parameters.TryGetValue("slowRadius", out float sr)
            ? Mathf.Max(droneFinalApproachRadius, sr)
            : droneArrivalSlowRadius;
        float finalRadius = parameters.TryGetValue("approachRadius", out float fr)
            ? Mathf.Max(0.2f, fr)
            : droneFinalApproachRadius;

        float speedFactor = EvaluateApproachSpeedFactor(distance, slowRadius, finalRadius);
        Vector3 desiredVelocity = desiredDir * commandedSpeed * speedFactor;
        Vector3 desiredAccel = ApplyDroneHorizontalVelocityControl(desiredVelocity, parameters);

        float targetHeight = ResolveDesiredHeightFromParameters(parameters, targetPos.y > 0.01f ? targetPos.y : transform.position.y);
        float altitudeGain = distance > slowRadius ? 26f : 22f;
        float altitudeDamping = distance > slowRadius ? 8.5f : 9.5f;
        ApplyDroneAltitudeControl(targetHeight, altitudeGain, altitudeDamping, maxVerticalControlAccel);
        ApplyDroneFlightAttitude(desiredVelocity, desiredAccel, distance, parameters);
    }

    /// <summary>
    /// 地面车辆移动逻辑
    /// </summary>
    private void ExecuteGroundMoveTo(Vector3 targetPos, float moveSpeed, Dictionary<string, float> parameters)
    {
        float acceleration = 3f;      // 加速度，调低会更真实
        float deceleration = 4f;      // 制动
        float turnSpeed = maxAngularSpeed;

        Vector3 toTarget = targetPos - transform.position;
        toTarget.y = 0;

        float distance = toTarget.magnitude;
        Vector3 dir = toTarget.normalized;

        // 1. 避障处理
        Vector3 safeDir = ApplyObstacleAvoidance(dir);

        // 2. 方向控制（旋转车头）
        if (safeDir.sqrMagnitude > 0.01f)
        {
            Quaternion targetRot = Quaternion.LookRotation(safeDir);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRot,
                turnSpeed * Time.fixedDeltaTime
            );
        }

        // 3. 真实速度控制（加速度）
        Vector3 desiredVel = safeDir * moveSpeed;

        // 目标越近，速度越慢
        float slowFactor = Mathf.Clamp01(distance / 2f);
        desiredVel *= slowFactor;

        // 用 MoveTowards 实现真实加速和减速
        Vector3 newVel = Vector3.MoveTowards(
            rb.velocity,
            desiredVel,
            (desiredVel.magnitude < rb.velocity.magnitude ? deceleration : acceleration) * Time.fixedDeltaTime
        );

        newVel.y = rb.velocity.y; // 地面车不动Y
        rb.velocity = newVel;
    }

    private float ResolveDroneMoveSpeed(float requestedSpeed, Dictionary<string, float> parameters)
    {
        float speed = requestedSpeed > 0.01f ? requestedSpeed : maxSpeed;
        speed = Mathf.Min(speed, Mathf.Max(maxSpeed, droneCruiseSpeed));

        if (parameters != null && parameters.TryGetValue("cruiseSpeed", out float cruise))
        {
            speed = Mathf.Min(speed, Mathf.Max(0.5f, cruise));
        }

        return Mathf.Max(0.5f, speed);
    }

    private float EvaluateApproachSpeedFactor(float distance, float slowRadius, float finalRadius)
    {
        if (distance <= finalRadius) return 0f;
        if (distance >= slowRadius) return 1f;

        float t = Mathf.InverseLerp(finalRadius, slowRadius, distance);
        return Mathf.SmoothStep(0.12f, 1f, t);
    }

    private Vector3 ApplyDroneHorizontalVelocityControl(Vector3 desiredVelocity, Dictionary<string, float> parameters)
    {
        Vector3 currentVelocity = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        Vector3 velocityError = desiredVelocity - currentVelocity;

        float accelLimit = parameters != null && parameters.TryGetValue("accel", out float accel)
            ? Mathf.Max(1f, accel)
            : droneMaxHorizontalAccel;
        float brakeLimit = parameters != null && parameters.TryGetValue("brakeAccel", out float brake)
            ? Mathf.Max(accelLimit, brake)
            : droneMaxHorizontalBrakeAccel;

        bool braking = desiredVelocity.sqrMagnitude < currentVelocity.sqrMagnitude * 0.9f ||
                       Vector3.Dot(currentVelocity.normalized, desiredVelocity.normalized) < 0.35f;
        float maxAccel = braking ? brakeLimit : accelLimit;

        float dt = Mathf.Max(Time.fixedDeltaTime, 0.001f);
        Vector3 desiredAccel = Vector3.ClampMagnitude(velocityError / dt, maxAccel);
        float drag = desiredVelocity.sqrMagnitude > 0.05f ? droneCruiseDrag : droneHoverDrag;
        desiredAccel += -currentVelocity * drag;

        rb.AddForce(new Vector3(desiredAccel.x, 0f, desiredAccel.z) * rb.mass, ForceMode.Force);
        return desiredAccel;
    }

    private void ApplyDroneFlightAttitude(Vector3 desiredVelocity, Vector3 desiredAccel, float distanceToTarget, Dictionary<string, float> parameters)
    {
        Vector3 yawReference = desiredVelocity.sqrMagnitude > 0.2f
            ? new Vector3(desiredVelocity.x, 0f, desiredVelocity.z)
            : new Vector3(rb.velocity.x, 0f, rb.velocity.z);

        float currentYaw = transform.eulerAngles.y;
        if (yawReference.sqrMagnitude > 0.01f)
        {
            currentYaw = Quaternion.LookRotation(yawReference.normalized, Vector3.up).eulerAngles.y;
        }

        float yawResponse = parameters != null && parameters.TryGetValue("yawSpeed", out float ys)
            ? Mathf.Max(0.5f, ys)
            : droneYawResponse;
        float pitchLimit = parameters != null && parameters.TryGetValue("pitchAngle", out float pa)
            ? Mathf.Clamp(pa, 0f, 35f)
            : droneMaxPitchAngle;
        float rollLimit = parameters != null && parameters.TryGetValue("rollAngle", out float ra)
            ? Mathf.Clamp(ra, 0f, 35f)
            : droneMaxRollAngle;

        Quaternion yawBasis = Quaternion.Euler(0f, currentYaw, 0f);
        Vector3 localAccel = Quaternion.Inverse(yawBasis) * new Vector3(desiredAccel.x, 0f, desiredAccel.z);
        float accelNorm = Mathf.Max(1f, droneMaxHorizontalBrakeAccel);
        float pitch = Mathf.Clamp(-localAccel.z / accelNorm, -1f, 1f) * pitchLimit;
        float roll = Mathf.Clamp(-localAccel.x / accelNorm, -1f, 1f) * rollLimit;

        float nearFactor = distanceToTarget > 0.01f
            ? Mathf.Clamp01(distanceToTarget / Mathf.Max(0.5f, droneFinalApproachRadius * 2f))
            : 0f;
        pitch *= nearFactor;
        roll *= nearFactor;

        Quaternion targetRotation = yawBasis * Quaternion.Euler(pitch, 0f, roll);
        Quaternion blended = Quaternion.Slerp(rb.rotation, targetRotation, Time.fixedDeltaTime * Mathf.Max(yawResponse, droneTiltResponse));
        rb.MoveRotation(blended);
        rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, Vector3.zero, Time.fixedDeltaTime * 6f);
    }

    /// <summary>
    /// 无人机姿态稳定（飞行中保持水平）
    /// </summary>
    private void StabilizeDroneAttitude()
    {
        ApplyDroneFlightAttitude(Vector3.zero, Vector3.zero, 0f, null);
    }

    /// <summary>
    /// 执行旋转到指定角度动作
    /// </summary>
    private void ExecuteRotateTo(Quaternion targetRot)
    {
        ApplyRotation(targetRot, ParseActionParameters(currentCommand.Parameters));
    }

    /// <summary>
    /// 执行看向目标动作
    /// </summary>
    private void ExecuteLookAt(Vector3 targetPos)
    {
        var parameters = ParseActionParameters(currentCommand.Parameters);
        Vector3 targetDir = (targetPos - transform.position).normalized;
        
        // 区分旋转维度
        if (intelligentAgent?.Properties.Type != AgentType.Quadcopter)
        {
            targetDir.y = 0; // 地面车仅水平旋转
        }
        
        if (targetDir.sqrMagnitude > 0.01f)
        {
            Quaternion targetRot = Quaternion.LookRotation(targetDir);
            ApplyRotation(targetRot, parameters);
        }
    }

    /// <summary>
    /// 通用旋转控制方法（统一处理旋转逻辑）
    /// </summary>
    private void ApplyRotation(Quaternion targetRot, Dictionary<string, float> parameters)
    {
        // 解析角速度参数
        float angularSpeed = parameters.TryGetValue("speed", out float s)
            ? Mathf.Clamp(s, 0, maxAngularSpeed)
            : maxAngularSpeed;

        // 计算角度差（区分无人机和地面车的旋转维度）
        float angleDiff;
        if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
        {
            // 无人机：全向旋转（使用四元数直接计算角度差）
            angleDiff = Quaternion.Angle(transform.rotation, targetRot);
        }
        else
        {
            // 地面车：仅Y轴旋转（水平转向）
            float targetY = targetRot.eulerAngles.y;
            float currentY = transform.eulerAngles.y;
            angleDiff = Mathf.DeltaAngle(currentY, targetY);
        }

        // 动态调整旋转速度（接近目标时减速）
        float speedFactor = Mathf.Clamp01(Mathf.Abs(angleDiff) / rotatePrecision);
        float rotationStep = angularSpeed * speedFactor * Time.fixedDeltaTime;

        // 应用旋转
        if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
        {
            // 无人机：直接使用四元数插值（支持任意方向旋转）
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, rotationStep);
        }
        else
        {
            // 地面车：仅绕Y轴旋转
            float newYAngle = Mathf.MoveTowardsAngle(transform.eulerAngles.y, targetRot.eulerAngles.y, rotationStep);
            transform.eulerAngles = new Vector3(0, newYAngle, 0);
        }
    }
    /// <summary>
    /// 执行停止动作
    /// </summary>
    private void ExecuteStop()
    {
        // 通用平滑减速
        rb.velocity = Vector3.Lerp(rb.velocity, Vector3.zero, Time.fixedDeltaTime * 10f);
        rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, Vector3.zero, Time.fixedDeltaTime * 10f);

        // 无人机额外处理：维持当前高度
        if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
        {
            // 仅保留Y轴速度用于高度微调（抵消下降趋势）
            float heightStabilization = Mathf.Clamp(-rb.velocity.y * 0.5f, -0.1f, 0.1f);
            rb.velocity = new Vector3(rb.velocity.x, heightStabilization, rb.velocity.z);
        }
    }
    /// <summary>
    /// 执行起飞动作
    /// </summary>
    private void ExecuteTakeOff()
    {
        if (intelligentAgent?.Properties.Type != AgentType.Quadcopter) return;

        var parameters = ParseActionParameters(currentCommand.Parameters);
        float targetHeight = ResolveDesiredHeightFromParameters(
            parameters,
            currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : takeOffHeight
        );

        ApplyDroneAltitudeControl(targetHeight, 28f, 9.0f, maxVerticalControlAccel);
        StabilizeHorizontalPosition();
        StabilizeDroneAttitude();
    }

    /// <summary>
    /// 执行悬停动作
    /// </summary>
    private void ExecuteHover()
    {
        if (intelligentAgent?.Properties.Type != AgentType.Quadcopter) return;

        var parameters = ParseActionParameters(currentCommand.Parameters);
        float targetHeight = ResolveDesiredHeightFromParameters(
            parameters,
            currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : transform.position.y
        );

        ApplyDroneAltitudeControl(targetHeight, 22f, 8.5f, maxVerticalControlAccel);
        StabilizeHorizontalPosition();
        StabilizeDroneAttitude();
    }

    private void StabilizeHorizontalPosition()
    {
        ApplyDroneHorizontalVelocityControl(Vector3.zero, null);
    }

    /// <summary>
    /// 无人机垂向控制（PD + 重力补偿）。
    /// 返回值为当前高度误差，便于调试。
    /// </summary>
    private float ApplyDroneAltitudeControl(float targetHeight, float kp, float kd, float maxAccel)
    {
        targetHeight = Mathf.Clamp(targetHeight, droneMinHeight, droneMaxHeight);
        float heightError = targetHeight - transform.position.y;
        float velY = rb.velocity.y;

        float accelCmd = Mathf.Clamp(kp * heightError - kd * velY, -Mathf.Abs(maxAccel), Mathf.Abs(maxAccel));
        float gravityComp = Mathf.Abs(Physics.gravity.y);
        float totalLift = rb.mass * (gravityComp + accelCmd);
        rb.AddForce(Vector3.up * totalLift, ForceMode.Force);
        return heightError;
    }

    /// <summary>
    /// 统一解析目标高度：优先参数，其次回退高度。
    /// </summary>
    private float ResolveDesiredHeightFromParameters(Dictionary<string, float> parameters, float fallbackHeight)
    {
        float h = fallbackHeight;
        if (parameters != null)
        {
            if (parameters.TryGetValue("height", out float ph)) h = ph;
            else if (parameters.TryGetValue("altitude", out float pa)) h = pa;
        }
        return Mathf.Clamp(h, droneMinHeight, droneMaxHeight);
    }

    /// <summary>
    /// 条件需要连续满足一段时间才返回 true，避免边界抖动造成误判。
    /// </summary>
    private bool EvaluateSettled(bool condition, float settleTime = -1f)
    {
        float required = settleTime > 0f ? settleTime : completionSettleTime;
        required = Mathf.Max(0.01f, required);

        if (!condition)
        {
            actionStableStartTime = -1f;
            return false;
        }

        if (actionStableStartTime < 0f) actionStableStartTime = Time.time;
        return (Time.time - actionStableStartTime) >= required;
    }

    /// <summary>
    /// 执行降落动作（仅无人机，包含姿态控制）
    /// </summary>
    private void ExecuteLand()
    {
        if (intelligentAgent?.Properties.Type != AgentType.Quadcopter) return;

        var parameters = ParseActionParameters(currentCommand.Parameters);
        float targetHeight = ResolveDesiredHeightFromParameters(
            parameters,
            currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : droneMinHeight
        );
        
        // 垂向受控下降，接近地面时更平缓
        float heightError = Mathf.Abs(targetHeight - transform.position.y);
        float accelLimit = Mathf.Lerp(maxVerticalControlAccel * 0.35f, maxVerticalControlAccel, Mathf.Clamp01(heightError / 1.5f));
        ApplyDroneAltitudeControl(targetHeight, 20f, 9.5f, accelLimit);
        StabilizeHorizontalPosition();
        StabilizeDroneAttitude();
    }
   
    /// <summary>
    /// 执行扫描动作（记录地图节点信息）
    /// </summary>
    private void ExecuteScan()
    {
        if (perceptionModule == null)
        {
            Debug.LogWarning("感知模块未找到，无法执行扫描动作");
            return;
        }     
    }
    
    /// <summary>
    /// 执行拾取动作（包含对位旋转）
    /// </summary>
    private void ExecutePickUp(GameObject targetObj)
    {
        if (heldObject == null && targetObj != null)
        {
            // 忽略Y轴，只计算XZ平面的距离
            float distance = Mathf.Sqrt(
                Mathf.Pow(targetObj.transform.position.x - transform.position.x, 2) +
                Mathf.Pow(targetObj.transform.position.z - transform.position.z, 2)
            );
            
            // 距离足够近时直接拾取
            if (distance < positionPrecision * 2)
            {
                heldObject = targetObj;
                heldObject.transform.SetParent(transform);
                heldObject.transform.localPosition = Vector3.up * 0.5f;
                var objRb = heldObject.GetComponent<Rigidbody>();
                if (objRb != null) objRb.isKinematic = true;
                Debug.Log($"成功拾取物体: {targetObj.name}");
            }
            else
            {
                // 距离较远时，先转向目标再移动
                ExecuteLookAt(targetObj.transform.position);
                ExecuteMoveTo(targetObj.transform.position);
            }
        }
        else
        {
            Debug.LogWarning("无法拾取物体，可能已持有其他物体或目标物体为空");
        }
    }

    /// <summary>
    /// 执行放下动作（包含对位旋转）
    /// </summary>
    private void ExecuteDrop(Vector3 dropPos)
    {
        if (heldObject != null)
        {
            float distance = Vector3.Distance(transform.position, dropPos);
            
            // 到达目标位置后放下
            if (distance < positionPrecision * 2)
            {
                heldObject.transform.SetParent(null);
                heldObject.transform.position = dropPos;
                var objRb = heldObject.GetComponent<Rigidbody>();
                if (objRb != null) objRb.isKinematic = false;
                
                if (IsPositionNearCenter(dropPos))
                {
                    resourcesDelivered++;
                    Debug.Log($"成功运送资源，当前计数: {resourcesDelivered}");
                }
                
                heldObject = null;
            }
            else
            {
                // 未到达目标时，先转向再移动
                ExecuteLookAt(dropPos);
                ExecuteMoveTo(dropPos);
            }
        }
    }
    #endregion
    private void ExecuteWait()
    {
        // 等待动作不需要额外逻辑，保持当前状态即可
    }
    private void ExecuteAdjustAltitude()
    {
        if (intelligentAgent?.Properties.Type != AgentType.Quadcopter) return;

        var parameters = ParseActionParameters(currentCommand.Parameters);
        float targetHeight = ResolveDesiredHeightFromParameters(
            parameters,
            currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : transform.position.y
        );

        ApplyDroneAltitudeControl(targetHeight, 24f, 9.0f, maxVerticalControlAccel);
        StabilizeHorizontalPosition();
        StabilizeDroneAttitude();
    }
    private void ExecuteAlign(Vector3 targetPos)
    {
        var parameters = ParseActionParameters(currentCommand.Parameters);

        float rotY = parameters.TryGetValue("rotY", out float ry)
            ? ry
            : transform.eulerAngles.y;

        Quaternion targetRot = Quaternion.Euler(0, rotY, 0);

        // 先位移
        ExecuteMoveTo(targetPos);

        // 再转向
        ApplyRotation(targetRot, parameters);
    }
    private void ExecuteFollow(GameObject target)
    {
        var parameters = ParseActionParameters(currentCommand.Parameters);
        float followDist = parameters.TryGetValue("followDist", out float fd) ? fd : 3.0f;
        Vector3 offset = -target.transform.forward * followDist;
        Vector3 followPos = target.transform.position + offset;

        ExecuteMoveTo(followPos);
        ExecuteLookAt(target.transform.position);
    }


    /// <summary>
    /// 执行环绕目标公转动作（最终优化：直接使用向心力/切向力控制）
    /// </summary>
    private void ExecuteOrbit(Vector3 center)
    {
        var parameters = ParseActionParameters(currentCommand.Parameters);
        float radius = parameters.TryGetValue("radius", out float r) ? r : 5.0f;
        // 期望角速度 (度/秒)，转换为弧度/秒 ( deg * pi / 180 )
        float orbitAngularSpeed = parameters.TryGetValue("orbitSpeed", out float os) ? os : 60.0f; 
        float orbitSpeedRad = orbitAngularSpeed * Mathf.Deg2Rad; 

        // 1. 矢量计算 (仅水平平面)
        Vector3 toCenter = center - transform.position;
        toCenter.y = 0; 
        float currentDistance = toCenter.magnitude;
        Vector3 radialDir = toCenter.normalized;
        
        // 2. 切线方向 (目标移动方向)
        // 切线方向 = toCenter 向量逆时针旋转 90 度 ( -z, 0, x )
        Vector3 tangentDir = new Vector3(-toCenter.z, 0, toCenter.x).normalized;
        
        // 3. 速度和力计算
        float mass = rb.mass;
        Vector3 currentVelXZ = new Vector3(rb.velocity.x, 0, rb.velocity.z);

        // A. 向心力 (确保保持半径)
        // F_c = m * v^2 / r。但我们使用 PID 风格的控制来修正半径误差。
        float distanceError = currentDistance - radius;
        
        // 如果离圆心太近 (距离 < 半径)，向外推。如果离圆心太远 (距离 > 半径)，向内拉。
        // 使用一个比例项 P 来作为修正力。
        float P_gain = 5f; // 比例增益，控制修正速度
        Vector3 radiusCorrectionForce = -radialDir * distanceError * P_gain * mass;
        
        // B. 切向力 (确保保持期望的公转角速度)
        // 期望的线速度 V_desired = R * w
        float desiredLinearSpeed = radius * orbitSpeedRad;
        Vector3 desiredVelocity = tangentDir * desiredLinearSpeed;
        
        // 使用比例控制 (P) 确保当前速度接近期望速度
        Vector3 velocityError = desiredVelocity - currentVelXZ;
        float T_gain = 3f; // 切向速度控制增益
        Vector3 tangentialForce = velocityError * T_gain * mass;
        
        // 4. 应用总水平力
        Vector3 totalHorizontalForce = radiusCorrectionForce + tangentialForce;
        rb.AddForce(totalHorizontalForce, ForceMode.Force);

        // 5. 高度保持 (仅无人机)
        if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
        {
            float targetHeight = ResolveDesiredHeightFromParameters(parameters, currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : transform.position.y);
            ApplyDroneAltitudeControl(targetHeight, 20f, 7.5f, maxVerticalControlAccel);
            // 姿态和角速度稳定
            StabilizeDroneAttitude(); 
        }
        else
        {
            // 地面车：阻尼垂直速度（防止跳跃）
            rb.velocity = new Vector3(rb.velocity.x, 0, rb.velocity.z);
        }

        // 6. 始终面向中心点
        ExecuteLookAt(center);
        
        // 注：动作完成检查仍依赖于 IsActionCompleted 中的 Duration 计时器。
    }



    /// <summary>
    /// 检查动作是否完成
    /// </summary>
    private bool IsActionCompleted()
    {
        if (currentCommand == null) return false;

        switch (currentCommand.ActionType)
        {
            case PrimitiveActionType.MoveTo:
            {
                var parameters = ParseActionParameters(currentCommand.Parameters);
                float distXZ = Vector2.Distance(
                    new Vector2(transform.position.x, transform.position.z),
                    new Vector2(currentCommand.TargetPosition.x, currentCommand.TargetPosition.z)
                );

                float posTol = parameters.TryGetValue("posTol", out float pt)
                    ? Mathf.Max(0.1f, pt)
                    : Mathf.Max(minMoveHorizontalTolerance, positionPrecision);
                float settleTime = parameters.TryGetValue("settleTime", out float st) ? Mathf.Max(0.01f, st) : completionSettleTime;

                Vector2 horizontalVel = new Vector2(rb.velocity.x, rb.velocity.z);
                if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
                {
                    float targetHeight = ResolveDesiredHeightFromParameters(parameters, currentCommand.TargetPosition.y);
                    float heightTol = parameters.TryGetValue("heightTol", out float ht)
                        ? Mathf.Max(0.05f, ht)
                        : droneAltitudeTolerance;
                    float horiTol = parameters.TryGetValue("horiTol", out float hlt) ? Mathf.Max(0.05f, hlt) : droneHorizontalSpeedTolerance;
                    float vertTol = parameters.TryGetValue("vertTol", out float vlt) ? Mathf.Max(0.05f, vlt) : droneVerticalSpeedTolerance;

                    bool near = distXZ < posTol && Mathf.Abs(transform.position.y - targetHeight) < heightTol;
                    bool stable = horizontalVel.magnitude < horiTol &&
                                  Mathf.Abs(rb.velocity.y) < vertTol;
                    return EvaluateSettled(near && stable, settleTime);
                }

                bool slow = horizontalVel.magnitude < 0.25f;
                return EvaluateSettled(distXZ < posTol && slow, settleTime);
            }
            case PrimitiveActionType.RotateTo:
            {
                var parameters = ParseActionParameters(currentCommand.Parameters);
                float tol = parameters.TryGetValue("rotTol", out float rt) ? Mathf.Max(0.2f, rt) : rotatePrecision;

                float angleDiff;
                if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
                {
                    angleDiff = Quaternion.Angle(transform.rotation, currentCommand.TargetRotation);
                }
                else
                {
                    angleDiff = Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, currentCommand.TargetRotation.eulerAngles.y));
                }
                return EvaluateSettled(angleDiff < tol, 0.08f);
            }
            case PrimitiveActionType.TakeOff:
            {
                if (intelligentAgent?.Properties.Type != AgentType.Quadcopter) return true;
                var parameters = ParseActionParameters(currentCommand.Parameters);
                float targetHeight = ResolveDesiredHeightFromParameters(parameters, currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : takeOffHeight);
                float heightError = Mathf.Abs(transform.position.y - targetHeight);
                float hSpeed = new Vector2(rb.velocity.x, rb.velocity.z).magnitude;
                bool stable = hSpeed < droneHorizontalSpeedTolerance && Mathf.Abs(rb.velocity.y) < droneVerticalSpeedTolerance;
                float settleTime = parameters.TryGetValue("settleTime", out float st) ? Mathf.Max(0.01f, st) : 0.25f;
                return EvaluateSettled(heightError < takeoffLandHeightTolerance && stable, settleTime);
            }
            case PrimitiveActionType.Land:
            {
                if (intelligentAgent?.Properties.Type != AgentType.Quadcopter) return true;
                var parameters = ParseActionParameters(currentCommand.Parameters);
                float targetHeight = ResolveDesiredHeightFromParameters(parameters, currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : droneMinHeight);
                float heightError = Mathf.Abs(transform.position.y - targetHeight);
                float hSpeed = new Vector2(rb.velocity.x, rb.velocity.z).magnitude;
                bool stable = hSpeed < droneHorizontalSpeedTolerance && Mathf.Abs(rb.velocity.y) < droneVerticalSpeedTolerance;
                float settleTime = parameters.TryGetValue("settleTime", out float st) ? Mathf.Max(0.01f, st) : 0.25f;
                return EvaluateSettled(heightError < takeoffLandHeightTolerance && stable, settleTime);
            }
            case PrimitiveActionType.Stop:
            {
                bool stable = rb.velocity.magnitude < 0.12f && rb.angularVelocity.magnitude < 0.2f;
                return EvaluateSettled(stable, 0.08f);
            }
            case PrimitiveActionType.PickUp:
                return heldObject != null;
            case PrimitiveActionType.Drop:
                return heldObject == null;
            case PrimitiveActionType.LookAt:
            {
                Vector3 lookVec = currentCommand.TargetPosition - transform.position;
                if (lookVec.sqrMagnitude < 1e-4f) return true;

                Vector3 lookDir = lookVec.normalized;
                float lookAngle = Vector3.Angle(transform.forward, lookDir);
                return EvaluateSettled(lookAngle < rotatePrecision * 2f, 0.08f);
            }
            case PrimitiveActionType.Scan:
                return Time.time - actionStartTime > 1f; // 扫描持续1秒
            case PrimitiveActionType.Hover:
            {
                var parameters = ParseActionParameters(currentCommand.Parameters);
                float Duration = parameters.TryGetValue("duration", out float d)? d : 2.0f;
                float targetHeight = ResolveDesiredHeightFromParameters(parameters, currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : transform.position.y);
                bool atTargetHeight = Mathf.Abs(transform.position.y - targetHeight) < droneAltitudeTolerance;
                float horiTol = parameters.TryGetValue("horiTol", out float hlt) ? Mathf.Max(0.05f, hlt) : droneHorizontalSpeedTolerance;
                float vertTol = parameters.TryGetValue("vertTol", out float vlt) ? Mathf.Max(0.05f, vlt) : droneVerticalSpeedTolerance;
                bool isStable = new Vector2(rb.velocity.x, rb.velocity.z).magnitude < horiTol &&
                                Mathf.Abs(rb.velocity.y) < vertTol;
                float settleTime = parameters.TryGetValue("settleTime", out float st) ? Mathf.Max(0.01f, st) : completionSettleTime;

                if (Duration > 0)
                {
                    bool elapsed = Time.time - actionStartTime > Duration;
                    return elapsed && EvaluateSettled(atTargetHeight && isStable, 0.1f);
                }
                else
                {
                    return EvaluateSettled(atTargetHeight && isStable, settleTime);
                }
            }
            case PrimitiveActionType.Wait:
            {
                var parameters = ParseActionParameters(currentCommand.Parameters);
                float Duration = parameters.TryGetValue("duration", out float d)? d : 2.0f;
                return Time.time - actionStartTime > Duration;
            }
            case PrimitiveActionType.AdjustAltitude:
            {
                if (intelligentAgent?.Properties.Type != AgentType.Quadcopter) return true;

                var parameters = ParseActionParameters(currentCommand.Parameters);
                float targetHeight = ResolveDesiredHeightFromParameters(parameters, currentCommand.TargetPosition.y > 0.01f ? currentCommand.TargetPosition.y : transform.position.y);
                float heightTol = parameters.TryGetValue("heightTol", out float ht)
                    ? Mathf.Max(0.05f, ht)
                    : droneAltitudeTolerance;
                float horiTol = parameters.TryGetValue("horiTol", out float hlt) ? Mathf.Max(0.05f, hlt) : droneHorizontalSpeedTolerance;
                float vertTol = parameters.TryGetValue("vertTol", out float vlt) ? Mathf.Max(0.05f, vlt) : droneVerticalSpeedTolerance;
                float settleTime = parameters.TryGetValue("settleTime", out float st) ? Mathf.Max(0.01f, st) : 0.2f;

                float heightErr = Mathf.Abs(transform.position.y - targetHeight);
                bool stable = new Vector2(rb.velocity.x, rb.velocity.z).magnitude < horiTol &&
                              Mathf.Abs(rb.velocity.y) < vertTol;
                return EvaluateSettled(heightErr < heightTol && stable, settleTime);
            }
            case PrimitiveActionType.Align:
            {
                var parameters = ParseActionParameters(currentCommand.Parameters);

                float rotY = parameters.TryGetValue("rotY", out float ry) ? ry : transform.eulerAngles.y;

                Quaternion targetRot = Quaternion.Euler(0, rotY, 0);

                float dist = Vector2.Distance(
                    new Vector2(transform.position.x, transform.position.z),
                    new Vector2(currentCommand.TargetPosition.x, currentCommand.TargetPosition.z)
                );
                float ang = Quaternion.Angle(transform.rotation, targetRot);
                bool slow = rb.velocity.magnitude < 0.25f;

                return EvaluateSettled(dist < Mathf.Max(0.2f, positionPrecision * 0.5f) && ang < 5f && slow, 0.1f);
            }
            case PrimitiveActionType.Follow:
            {
                var parameters = ParseActionParameters(currentCommand.Parameters);
                float Duration = parameters.TryGetValue("duration", out float d)? d : 2.0f;
                return Time.time - actionStartTime > Duration;
            }
            case PrimitiveActionType.Orbit:
            {
                var parameters = ParseActionParameters(currentCommand.Parameters);
                float Duration = parameters.TryGetValue("duration", out float d)? d : 5.0f;
                return Time.time - actionStartTime > Duration;
            }
            default:
                return false;
        }
    }
    #region 辅助方法
    /// <summary>
    /// 避障算法实现 - 简单的潜在场法
    /// </summary>
    private Vector3 ApplyObstacleAvoidance(Vector3 desiredDir)
    {
        if (perceptionModule == null) return desiredDir;
        
        Vector3 avoidanceDir = Vector3.zero;
        float obstacleInfluence = 0f;
        
        // 获取检测到的障碍物
        var obstacles = perceptionModule.GetDetectedObjects()
            .Where(obj => obj != null && obj.BlocksMovement)
            .ToList();

        foreach (var obs in obstacles)
        {
            Vector3 obstaclePos = (obs.SceneObject != null) ? obs.SceneObject.transform.position : obs.WorldPosition;
            Vector3 toObstacle = obstaclePos - transform.position;
            float distance = toObstacle.magnitude;
            
            // 只处理安全距离内的障碍物
            if (distance < safeDistance && distance > 0.1f)
            {
                // 障碍物影响因子（距离越近影响越大）
                float influence = 1 - (distance / safeDistance);
                obstacleInfluence += influence;
                
                // 计算远离障碍物的方向
                Vector3 awayFromObstacle = -toObstacle.normalized;
                
                // 结合障碍物方向和当前移动方向，计算避障方向
                avoidanceDir += Vector3.Lerp(awayFromObstacle, desiredDir, 0.3f) * influence;
            }
        }
        
        // 混合期望方向和避障方向
        if (obstacleInfluence > 0)
        {
            avoidanceDir /= obstacleInfluence; // 归一化避障方向
            return Vector3.Lerp(desiredDir, avoidanceDir, obstacleInfluence).normalized;
        }
        
        return desiredDir;
    }
    /// <summary>
    /// 解析相对移动的偏移量参数（从JSON字符串）
    /// </summary>
    private Vector3 ParseRelativeOffset()
    {
        if (string.IsNullOrEmpty(currentCommand.Parameters))
            return Vector3.zero;

        try
        {
            RelativeOffsetParams param = JsonUtility.FromJson<RelativeOffsetParams>(currentCommand.Parameters);
            return new Vector3(param.x, param.y, param.z);
        }
        catch (Exception e)
        {
            Debug.LogError($"解析相对偏移参数失败: {e.Message}");
            return Vector3.zero;
        }
    }

    /// <summary>
    /// 停止移动和旋转
    /// </summary>
    private void StopMovement()
    {
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// 位置归一化到[-0.5, 0.5]范围
    /// </summary>
    private Vector3 NormalizePosition(Vector3 position)
    {
        float safeWidth = Mathf.Max(0.0001f, gridWidth);
        float safeLength = Mathf.Max(0.0001f, gridLength);
        float safeHeight = Mathf.Max(0.0001f, droneMaxHeight);
        return new Vector3(
            Mathf.Clamp(position.x / safeWidth, -0.5f, 0.5f),
            Mathf.Clamp(position.y / safeHeight, 0f, 1f),
            Mathf.Clamp(position.z / safeLength, -0.5f, 0.5f)
        );
    }

    /// <summary>
    /// 检查位置是否接近中心点
    /// </summary>
    private bool IsPositionNearCenter(Vector3 position)
    {
        // 假设中心点是(0,0,0)
        return Vector3.Distance(position, Vector3.zero) < positionPrecision * 3;
    }
    #endregion

    /// <summary>
    /// 强制边界约束（物理层保底）
    /// </summary>
    private void EnforceBoundaryConstraints()
    {
        // 仅在 CampusGrid2D 边界可用时进行钳制
        if (!useAbsoluteWorldBounds)
        {
            return;
        }

        Vector3 clampedPos = transform.position;
        float minX = worldMinX + boundaryBuffer;
        float maxX = worldMaxX - boundaryBuffer;
        float minZ = worldMinZ + boundaryBuffer;
        float maxZ = worldMaxZ - boundaryBuffer;

        if (minX > maxX) { minX = worldMinX; maxX = worldMaxX; }
        if (minZ > maxZ) { minZ = worldMinZ; maxZ = worldMaxZ; }

        clampedPos.x = Mathf.Clamp(clampedPos.x, minX, maxX);
        clampedPos.z = Mathf.Clamp(clampedPos.z, minZ, maxZ);

        // 无人机高度约束
        if (intelligentAgent.Properties.Type == AgentType.Quadcopter)
        {
            clampedPos.y = Mathf.Clamp(clampedPos.y, droneMinHeight, droneMaxHeight);
        }

        // 越界处理 - 只考虑XZ平面距离，忽略Y轴
        Vector3 currentPosXZ = new Vector3(transform.position.x, 0, transform.position.z);
        Vector3 clampedPosXZ = new Vector3(clampedPos.x, 0, clampedPos.z);
        
        if (Vector3.Distance(currentPosXZ, clampedPosXZ) > 0.1f)  // 只检查水平距离
        {
            // 只修正水平位置，保持当前高度
            transform.position = new Vector3(clampedPos.x, transform.position.y, clampedPos.z);
            rb.velocity = Vector3.zero; // 重置速度
        }
    }

    /// <summary>
    /// 碰撞检测处理
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        if (collision.collider.CompareTag("Obstacle"))
        {
            Debug.LogWarning("碰撞到障碍物");
            // // 碰撞时停止当前动作
            // if (isExecutingCommand)
            // {
            //     OnActionCompleted(false, "碰撞障碍物");
            // }
        }
        else if (collision.collider.CompareTag("Agent") && 
                 collision.collider.GetComponent<IntelligentAgent>()?.CurrentState.TeamID != intelligentAgent?.CurrentState.TeamID)
        {
            Debug.LogWarning("碰撞到敌方智能体");
        }
    }

    /// <summary>
    /// 动作完成后的处理（核心回调触发点）
    /// </summary>
    private void OnActionCompleted(bool success, string result)
    {
        if (currentCommand == null) return;

        // 1. 准备回调参数
        PrimitiveActionType actionType = currentCommand.ActionType;

        // 2. 调用外部传入的回调函数（通知决策模块）
        currentActionCallback?.Invoke(actionType, success, result);

        // 3. 清理当前动作状态
        currentCommand = null;
        currentActionCallback = null; // 清空回调，避免重复调用
        actionStableStartTime = -1f;

        if (intelligentAgent != null)
        {
            intelligentAgent.CurrentState.Status = AgentStatus.Idle;
        }
    }
    /// <summary>
    /// 解析动作参数JSON
    /// </summary>
    private Dictionary<string, float> ParseActionParameters(string jsonParams)
    {
        var parameters = new Dictionary<string, float>();
        if (string.IsNullOrEmpty(jsonParams)) return parameters;

        try
        {
            string raw = NormalizeParameterPayload(jsonParams);
            TryExtractFloat(raw, "speed", parameters);
            TryExtractFloat(raw, "height", parameters);
            TryExtractFloat(raw, "altitude", parameters, "height");
            TryExtractFloat(raw, "duration", parameters);
            TryExtractFloat(raw, "rotX", parameters);
            TryExtractFloat(raw, "rotY", parameters);
            TryExtractFloat(raw, "rotZ", parameters);
            TryExtractFloat(raw, "followDist", parameters);
            TryExtractFloat(raw, "radius", parameters);
            TryExtractFloat(raw, "orbitSpeed", parameters);
            TryExtractFloat(raw, "accel", parameters);
            TryExtractFloat(raw, "brakeAccel", parameters);
            TryExtractFloat(raw, "slowRadius", parameters);
            TryExtractFloat(raw, "approachRadius", parameters);
            TryExtractFloat(raw, "yawSpeed", parameters);
            TryExtractFloat(raw, "pitchAngle", parameters);
            TryExtractFloat(raw, "rollAngle", parameters);
            TryExtractFloat(raw, "posTol", parameters);
            TryExtractFloat(raw, "heightTol", parameters);
            TryExtractFloat(raw, "rotTol", parameters);
            TryExtractFloat(raw, "settleTime", parameters);
            TryExtractFloat(raw, "vertTol", parameters);
            TryExtractFloat(raw, "horiTol", parameters);
        }
        catch (Exception e)
        {
            Debug.LogError($"参数解析错误: {e.Message}");
        }
        return parameters;
    }

    /// <summary>
    /// 统一参数文本，兼容 JSON 字符串和 key:value 列表。
    /// </summary>
    private static string NormalizeParameterPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return string.Empty;
        string s = payload.Trim();
        if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
        {
            s = s.Substring(1, s.Length - 2);
        }
        s = s.Replace("\\\"", "\"");
        return s;
    }

    /// <summary>
    /// 从参数文本中提取浮点数，支持 "k:v"、"k=v"、JSON 键值。
    /// </summary>
    private static void TryExtractFloat(string raw, string key, Dictionary<string, float> output, string alias = null)
    {
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(key) || output == null) return;
        string targetKey = string.IsNullOrEmpty(alias) ? key : alias;

        Match m = Regex.Match(
            raw,
            $@"[""']?{Regex.Escape(key)}[""']?\s*[:=]\s*[""']?(-?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase
        );

        if (!m.Success) return;

        if (float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
        {
            output[targetKey] = v;
        }
    }

    /// <summary>
    /// 重置智能体状态
    /// </summary>
    public void ResetAgent()
    {
        // 重置状态变量
        currentCommand = null;
        currentActionCallback = null;
        heldObject = null;
        exploredNodes.Clear();
        resourcesDelivered = 0;
        totalResources = 0;
        tempSpeed = maxSpeed;

        // 重置物理状态
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // 随机化智能体位置（通过智能体生成模块）
        if (agentSpawner != null && intelligentAgent != null)
        {
            Vector3 randomPos = agentSpawner.GetRandomSpawnPosition(intelligentAgent.Properties.Type);
            transform.position = randomPos;
            transform.rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0, 360), 0);
        }

        // 重置电量
        if (intelligentAgent != null)
        {
            intelligentAgent.CurrentState.BatteryLevel = intelligentAgent.Properties.BatteryCapacity;
            intelligentAgent.CurrentState.Status = AgentStatus.Idle;
        }
    }

    /// <summary>
    /// 手动控制（替代 ML-Agents 的 Heuristic）。在 Update 或 FixedUpdate 中调用以响应键盘输入。
    /// WASD: 水平移动，Q/E: 旋转，Space/LeftControl: 无人机上下
    /// </summary>
    public void ManualControl()
    {
        // 横向/纵向输入
        float inputX = Input.GetKey(KeyCode.D) ? 1f : (Input.GetKey(KeyCode.A) ? -1f : 0f);
        float inputZ = Input.GetKey(KeyCode.W) ? 1f : (Input.GetKey(KeyCode.S) ? -1f : 0f);
        // 旋转输入
        float inputRot = Input.GetKey(KeyCode.E) ? 1f : (Input.GetKey(KeyCode.Q) ? -1f : 0f);
        // 无人机高度输入
        float inputY = 0f;
        if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
        {
            inputY = Input.GetKey(KeyCode.Space) ? 1f : (Input.GetKey(KeyCode.LeftControl) ? -1f : 0f);
        }

        // 局部移动向量（相对于智能体朝向）
        Vector3 localMove = new Vector3(inputX, 0f, inputZ);
        if (localMove.sqrMagnitude > 1f) localMove.Normalize();

        // 期望速度（世界坐标）
        Vector3 desiredWorld = transform.TransformDirection(localMove) * tempSpeed;

        // 无人机控制（包含竖直速度和朝向控制）
        if (intelligentAgent?.Properties.Type == AgentType.Quadcopter)
        {
            float verticalSpeed = inputY * tempSpeed;
            Vector3 targetVel = new Vector3(desiredWorld.x, verticalSpeed, desiredWorld.z);
            rb.velocity = Vector3.Lerp(rb.velocity, targetVel, Time.fixedDeltaTime * 5f);

            // 如果有水平移动则朝向移动方向旋转
            Vector3 horiz = new Vector3(desiredWorld.x, 0f, desiredWorld.z);
            if (horiz.sqrMagnitude > 0.001f)
            {
                Quaternion want = Quaternion.LookRotation(horiz.normalized);
                rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, want, maxAngularSpeed * Time.fixedDeltaTime));
            }
        }
        else // 地面轮式机器人控制
        {
            // 保持竖直速度不变（防止重置Y）
            Vector3 currentVel = rb.velocity;
            Vector3 targetVel = new Vector3(desiredWorld.x, currentVel.y, desiredWorld.z);
            rb.velocity = Vector3.Lerp(currentVel, targetVel, Time.fixedDeltaTime * 5f);

            // 旋转：优先显式旋转输入，否则朝向移动方向
            if (Mathf.Abs(inputRot) > 0.01f)
            {
                float yawDelta = inputRot * maxAngularSpeed * Time.fixedDeltaTime;
                rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, yawDelta, 0f));
            }
            else if (localMove.sqrMagnitude > 0.001f)
            {
                Quaternion want = Quaternion.LookRotation(new Vector3(desiredWorld.x, 0f, desiredWorld.z).normalized);
                rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, want, maxAngularSpeed * Time.fixedDeltaTime));
            }
        }
    }

}

