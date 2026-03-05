using UnityEngine;

/// <summary>
/// Map camera controller (ground on XZ plane, height on Y).
/// 1) Scroll: zoom (move along view ray)
/// 2) RMB drag: pan
/// 3) Alt + RMB drag: orbit around pivot
/// 4) Free/locked mode switch
/// </summary>
public class CameraController : MonoBehaviour
{
    [Header("Bindings")]
    public Camera cam;
    public Transform pivot;

    [Header("Ground Height (Y)")]
    public float groundY = 0f;

    [Header("Zoom")]
    public float zoomSpeed = 30f;
    public float minDistance = 5f;
    public float maxDistance = 800f;
    public float zoomDamp = 12f;

    [Header("Pan")]
    public float panDamp = 18f;
    public float panSpeedScale = 1f;
    public float mousePanSpeed = 1f;

    [Header("Keyboard Move")]
    public float moveSpeed = 35f;
    public float sprintMultiplier = 2.5f;
    public KeyCode sprintKey = KeyCode.LeftShift;

    [Header("Orbit")]
    public float orbitSpeed = 0.25f;
    public float minPitch = 10f;
    public float maxPitch = 89f;
    public float orbitDamp = 18f;

    [Header("Mode")]
    public bool freeCamera = true;
    public bool lockCursorWhileRMB = true;

    [Header("Input")]
    public KeyCode orbitModifier = KeyCode.LeftAlt;
    public bool useMMBForPan = false;

    float targetYaw;
    float targetPitch;
    float curYaw;
    float curPitch;

    float targetDistance;
    float curDistance;

    Vector3 targetPivotPos;
    Vector3 curPivotPos;

    void Awake()
    {
        if (cam == null) cam = GetComponent<Camera>();

        if (pivot == null)
        {
            var go = new GameObject("CameraPivot");
            pivot = go.transform;
            pivot.position = new Vector3(0f, groundY, 0f);
        }

        SanitizePitchRange();

        Vector3 toCam = transform.position - pivot.position;
        curDistance = targetDistance = Mathf.Clamp(toCam.magnitude, minDistance, maxDistance);

        Vector3 dir = (toCam.sqrMagnitude < 1e-6f) ? Vector3.up : toCam.normalized;

        // Offset uses rot * (0, 0, -distance), so yaw reconstruction is inverted on X/Z.
        targetYaw = curYaw = Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg;
        targetPitch = curPitch = Mathf.Asin(dir.y) * Mathf.Rad2Deg;
        targetPitch = curPitch = Mathf.Clamp(targetPitch, minPitch, maxPitch);

        targetPivotPos = curPivotPos = pivot.position;
    }

    void OnValidate()
    {
        SanitizePitchRange();
    }

    void Update()
    {
        HandleInput();
        ApplySmooth();
        ApplyTransform();
    }

    public void SetFreeCameraMode(bool enable)
    {
        freeCamera = enable;
        if (!freeCamera)
        {
            targetPitch = curPitch = maxPitch;
        }
    }

    public void SetPivot(Vector3 worldPos)
    {
        targetPivotPos = worldPos;
        curPivotPos = worldPos;
        pivot.position = worldPos;
    }

    public void FitToBoundsXZ(Bounds b, float margin = 1.2f)
    {
        float size = Mathf.Max(b.size.x, b.size.z);
        float want = size * margin;

        float fovRad = cam != null ? cam.fieldOfView * Mathf.Deg2Rad : 60f * Mathf.Deg2Rad;
        float dist = (want * 0.5f) / Mathf.Tan(fovRad * 0.5f);

        targetDistance = curDistance = Mathf.Clamp(dist, minDistance, maxDistance);
        SetPivot(new Vector3(b.center.x, groundY, b.center.z));
    }

    void HandleInput()
    {
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.0001f)
        {
            float step = zoomSpeed * (0.2f + targetDistance * 0.02f);
            targetDistance -= scroll * step * Time.unscaledDeltaTime * 60f;
            targetDistance = Mathf.Clamp(targetDistance, minDistance, maxDistance);
        }

        bool rmb = Input.GetMouseButton(1);
        bool mmb = Input.GetMouseButton(2);

        bool wantPan = useMMBForPan ? mmb : rmb;
        bool wantOrbit = rmb && Input.GetKey(orbitModifier);

        if (lockCursorWhileRMB)
        {
            if (rmb) Cursor.lockState = CursorLockMode.Locked;
            else Cursor.lockState = CursorLockMode.None;
            Cursor.visible = !rmb;
        }

        if (freeCamera && wantOrbit)
        {
            float dx = Input.GetAxisRaw("Mouse X");
            float dy = Input.GetAxisRaw("Mouse Y");

            targetYaw += dx * orbitSpeed * 60f;
            targetPitch -= dy * orbitSpeed * 60f;
            targetPitch = Mathf.Clamp(targetPitch, minPitch, maxPitch);
        }

        if (wantPan && !wantOrbit)
        {
            ApplyMousePan();
        }

        ApplyKeyboardMove();
    }

    void ApplySmooth()
    {
        float dt = Time.unscaledDeltaTime;
        float tZoom = 1f - Mathf.Exp(-zoomDamp * dt);
        float tPan = 1f - Mathf.Exp(-panDamp * dt);
        float tRot = 1f - Mathf.Exp(-orbitDamp * dt);

        curDistance = Mathf.Lerp(curDistance, targetDistance, tZoom);
        curPivotPos = Vector3.Lerp(curPivotPos, targetPivotPos, tPan);
        curYaw = Mathf.LerpAngle(curYaw, targetYaw, tRot);
        curPitch = Mathf.Lerp(curPitch, targetPitch, tRot);
    }

    void ApplyTransform()
    {
        pivot.position = curPivotPos;

        Quaternion rot = Quaternion.Euler(curPitch, curYaw, 0f);
        Vector3 offset = rot * new Vector3(0f, 0f, -curDistance);

        transform.position = pivot.position + offset;
        transform.rotation = Quaternion.LookRotation((pivot.position - transform.position).normalized, Vector3.up);
    }

    void ApplyMousePan()
    {
        float dx = Input.GetAxis("Mouse X");
        float dy = Input.GetAxis("Mouse Y");
        if (Mathf.Abs(dx) < 1e-5f && Mathf.Abs(dy) < 1e-5f) return;

        Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

        float scale = mousePanSpeed * panSpeedScale * (0.35f + targetDistance * 0.01f);
        Vector3 delta = (-right * dx - forward * dy) * scale;

        targetPivotPos += delta;
        targetPivotPos.y = groundY;
    }

    void ApplyKeyboardMove()
    {
        float x = 0f;
        float z = 0f;

        if (Input.GetKey(KeyCode.A)) x -= 1f;
        if (Input.GetKey(KeyCode.D)) x += 1f;
        if (Input.GetKey(KeyCode.S)) z -= 1f;
        if (Input.GetKey(KeyCode.W)) z += 1f;
        if (Mathf.Abs(x) < 1e-5f && Mathf.Abs(z) < 1e-5f) return;

        Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 moveDir = (right * x + forward * z).normalized;

        float speed = moveSpeed;
        if (Input.GetKey(sprintKey)) speed *= sprintMultiplier;

        targetPivotPos += moveDir * speed * Time.unscaledDeltaTime;
        targetPivotPos.y = groundY;
    }

    void SanitizePitchRange()
    {
        minPitch = Mathf.Clamp(minPitch, 0f, 89f);
        maxPitch = Mathf.Clamp(maxPitch, minPitch + 0.1f, 89.9f);
        mousePanSpeed = Mathf.Max(0.01f, mousePanSpeed);
        moveSpeed = Mathf.Max(0f, moveSpeed);
        sprintMultiplier = Mathf.Max(1f, sprintMultiplier);
    }
}
