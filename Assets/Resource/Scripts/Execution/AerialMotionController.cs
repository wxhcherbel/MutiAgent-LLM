using UnityEngine;

/// <summary>
/// 轻量无人机运动控制器。
/// 不模拟四旋翼底层推力，仅负责让飞行在 Unity 中看起来更平滑、更接近无人机观感。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class AerialMotionController : MonoBehaviour
{
    [Header("Cruise")]
    [SerializeField] private float maxSpeed = 8f;
    [SerializeField] private float horizontalAcceleration = 9f;
    [SerializeField] private float horizontalDeceleration = 12f;
    [SerializeField] private float slowDownDistance = 6f;
    [SerializeField] private float stopDistance = 0.25f;

    [Header("Altitude")]
    [SerializeField] private float targetHeight = 5f;
    [SerializeField] private float verticalGain = 2.5f;
    [SerializeField] private float verticalAcceleration = 8f;
    [SerializeField] private float maxClimbSpeed = 3.5f;

    [Header("Attitude")]
    [SerializeField] private float rotationSharpness = 6f;
    [SerializeField] private float maxPitchAngle = 12f;
    [SerializeField] private float maxRollAngle = 16f;
    [SerializeField] private float turnTiltBoost = 6f;

    [Header("Idle Drift")]
    [SerializeField] private float idleDriftAmplitude = 0.12f;
    [SerializeField] private float idleDriftFrequency = 0.18f;
    [SerializeField] private float idleHeightBobAmplitude = 0.08f;
    [SerializeField] private float idleHeightBobFrequency = 0.22f;

    [Header("Debug")]
    [SerializeField] private bool logStateTransitions = false;

    private Rigidbody rb;
    private Vector3? moveTarget;
    private Vector3 idleAnchor;
    private bool idleAnchorDirty = true;

    private float driftNoiseX;
    private float driftNoiseZ;
    private float bobNoise;
    private float requestedSpeedScale = 1f;

    public Vector3? MoveTarget
    {
        get => moveTarget;
        set
        {
            bool hadTarget = moveTarget.HasValue;
            moveTarget = value;

            if (value.HasValue)
            {
                idleAnchorDirty = true;
            }

            if (logStateTransitions && hadTarget != value.HasValue)
            {
                string state = value.HasValue ? $"received move target {value.Value}" : "switched to idle hover";
                Debug.Log($"[AerialMotionController] {name} {state}");
            }
        }
    }


    public float TargetHeight
    {
        get => targetHeight;
        set => targetHeight = Mathf.Max(0f, value);
    }

    public Vector3 Velocity => rb != null ? rb.velocity : Vector3.zero;

    public float MaxSpeed
    {
        get => maxSpeed;
        set => maxSpeed = Mathf.Max(0.1f, value);
    }

    /// <summary>
    /// 由上层局部规划器动态下发的速度缩放。
    /// 取值越小，飞控越会提前减速，避免贴着障碍物硬冲。
    /// </summary>
    public float RequestedSpeedScale
    {
        get => requestedSpeedScale;
        set => requestedSpeedScale = Mathf.Clamp(value, 0.05f, 1f);
    }

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.drag = 0f;
        rb.angularDrag = 0f;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        driftNoiseX = Random.Range(0f, 100f);
        driftNoiseZ = Random.Range(0f, 100f);
        bobNoise = Random.Range(0f, 100f);
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        Vector3 targetPos = ResolvePlanarTarget();
        float effectiveTargetHeight = ResolveTargetHeight();

        Vector3 currentPos = rb.position;
        Vector3 toTarget = targetPos - currentPos;
        toTarget.y = 0f;

        float planarDistance = toTarget.magnitude;
        Vector3 desiredPlanarVelocity = Vector3.zero;
        if (planarDistance > stopDistance)
        {
            float desiredSpeed = maxSpeed * requestedSpeedScale;
            if (planarDistance < slowDownDistance)
            {
                desiredSpeed *= Mathf.Clamp01(planarDistance / Mathf.Max(0.01f, slowDownDistance));
            }

            desiredPlanarVelocity = toTarget.normalized * desiredSpeed;
        }

        Vector3 currentPlanarVelocity = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        float accelLimit = desiredPlanarVelocity.magnitude >= currentPlanarVelocity.magnitude
            ? horizontalAcceleration
            : horizontalDeceleration;
        Vector3 newPlanarVelocity = Vector3.MoveTowards(
            currentPlanarVelocity,
            desiredPlanarVelocity,
            accelLimit * dt);

        float heightError = effectiveTargetHeight - currentPos.y;
        float desiredVerticalVelocity = Mathf.Clamp(heightError * verticalGain, -maxClimbSpeed, maxClimbSpeed);
        float newVerticalVelocity = Mathf.MoveTowards(
            rb.velocity.y,
            desiredVerticalVelocity,
            verticalAcceleration * dt);

        rb.velocity = new Vector3(newPlanarVelocity.x, newVerticalVelocity, newPlanarVelocity.z);
        UpdateAttitude(currentPlanarVelocity, newPlanarVelocity, dt);
    }

    private Vector3 ResolvePlanarTarget()
    {
        if (moveTarget.HasValue)
        {
            return new Vector3(moveTarget.Value.x, rb.position.y, moveTarget.Value.z);
        }

        if (idleAnchorDirty)
        {
            idleAnchor = rb.position;
            idleAnchorDirty = false;
        }

        float t = Time.time;
        float driftX = (Mathf.PerlinNoise(driftNoiseX + t * idleDriftFrequency, 0f) - 0.5f) * 2f * idleDriftAmplitude;
        float driftZ = (Mathf.PerlinNoise(driftNoiseZ + t * idleDriftFrequency, 0f) - 0.5f) * 2f * idleDriftAmplitude;
        return new Vector3(idleAnchor.x + driftX, rb.position.y, idleAnchor.z + driftZ);
    }

    private float ResolveTargetHeight()
    {
        if (moveTarget.HasValue)
        {
            return targetHeight;
        }

        float t = Time.time;
        float bob = (Mathf.PerlinNoise(bobNoise + t * idleHeightBobFrequency, 0f) - 0.5f) * 2f * idleHeightBobAmplitude;
        return targetHeight + bob;
    }

    private void UpdateAttitude(Vector3 currentPlanarVelocity, Vector3 newPlanarVelocity, float dt)
    {
        Vector3 faceDirection = newPlanarVelocity.sqrMagnitude > 0.04f
            ? newPlanarVelocity.normalized
            : currentPlanarVelocity.sqrMagnitude > 0.04f
                ? currentPlanarVelocity.normalized
                : transform.forward;

        float yaw = Mathf.Atan2(faceDirection.x, faceDirection.z) * Mathf.Rad2Deg;
        Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);

        Vector3 localVelocity = Quaternion.Inverse(yawRotation) * newPlanarVelocity;
        Vector3 planarAccel = dt > 0.0001f ? (newPlanarVelocity - currentPlanarVelocity) / dt : Vector3.zero;
        Vector3 localAccel = Quaternion.Inverse(yawRotation) * planarAccel;

        float speedRatio = Mathf.Clamp01(newPlanarVelocity.magnitude / Mathf.Max(0.1f, maxSpeed));
        float pitchFromSpeed = -Mathf.Clamp(localVelocity.z / Mathf.Max(0.1f, maxSpeed), -1f, 1f) * maxPitchAngle;
        float rollFromSpeed = Mathf.Clamp(localVelocity.x / Mathf.Max(0.1f, maxSpeed), -1f, 1f) * maxRollAngle;
        float rollFromTurn = Mathf.Clamp(localAccel.x / Mathf.Max(0.1f, horizontalAcceleration), -1f, 1f) * turnTiltBoost;

        float pitch = Mathf.Lerp(0f, pitchFromSpeed, speedRatio);
        float roll = Mathf.Lerp(0f, rollFromSpeed + rollFromTurn, Mathf.Clamp01(speedRatio + 0.2f));

        Quaternion targetRotation = Quaternion.Euler(pitch, yaw, -roll);
        rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRotation, rotationSharpness * dt));
    }
}
