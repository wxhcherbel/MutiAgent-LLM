using TMPro;
using UnityEngine;

/// <summary>
/// 智能体运行时可视化增强器。
/// 目标：
/// 1. 在大地图视角下，让 agent 更容易被看见和跟踪。
/// 2. 不依赖 prefab 预配置，运行时自动创建地面环、光柱、标签和尾迹。
/// 3. 仅增强视觉表现，不参与任务、导航和感知逻辑。
/// </summary>
[DisallowMultipleComponent]
public class AgentVisualMarker : MonoBehaviour
{
    [Header("显示开关")]
    [SerializeField] private bool showGroundRing = true;
    [SerializeField] private bool showVerticalBeacon = true;
    [SerializeField] private bool showFloatingLabel = true;
    [SerializeField] private bool showMotionTrail = true;

    [Header("地面环")]
    [SerializeField] private float ringRadius = 2.4f;
    [SerializeField] private float ringYOffset = 0.15f;
    [SerializeField] private float ringWidth = 0.18f;
    [SerializeField] private int ringSegments = 40;
    [SerializeField] private float ringPulseAmplitude = 0.18f;
    [SerializeField] private float ringPulseFrequency = 2.2f;
    [SerializeField] private float ringRotateSpeed = 45f;

    [Header("头顶光柱")]
    [SerializeField] private float beaconHeight = 12f;
    [SerializeField] private float beaconWidth = 0.22f;
    [SerializeField] private float beaconPulseAmplitude = 0.08f;
    [SerializeField] private float beaconPulseFrequency = 2.8f;

    [Header("悬浮标签")]
    [SerializeField] private float labelHeight = 4.2f;
    [SerializeField] private float labelFontSize = 5.2f;
    [SerializeField] private float labelScale = 0.2f;
    [SerializeField] private float labelDistanceScale = 0.015f;
    [SerializeField] private bool showStatusOnLabel = true;

    [Header("移动尾迹")]
    [SerializeField] private float trailTime = 1.8f;
    [SerializeField] private float trailStartWidth = 0.45f;
    [SerializeField] private float trailEndWidth = 0.05f;
    [SerializeField] private float minTrailSpeed = 0.25f;

    [Header("调试")]
    [SerializeField] private bool logInitialization = false;

    private IntelligentAgent agent;
    private Rigidbody cachedRigidbody;
    private Camera mainCamera;

    private Transform visualRoot;
    private Transform ringRoot;
    private LineRenderer ringRenderer;
    private LineRenderer beaconRenderer;
    private TextMeshPro labelText;
    private TrailRenderer motionTrail;

    private Material sharedSpriteMaterial;
    private Color baseColor = Color.cyan;
    private Color accentColor = new Color(1f, 1f, 1f, 0.95f);

    private void Awake()
    {
        agent = GetComponent<IntelligentAgent>();
        cachedRigidbody = GetComponent<Rigidbody>();
        mainCamera = Camera.main;

        EnsureVisualRoot();
        EnsureSharedMaterial();
        ResolveMarkerColors();

        if (showGroundRing)
        {
            EnsureGroundRing();
        }

        if (showVerticalBeacon)
        {
            EnsureBeacon();
        }

        if (showFloatingLabel)
        {
            EnsureFloatingLabel();
        }

        if (showMotionTrail)
        {
            EnsureMotionTrail();
        }

        if (logInitialization)
        {
            Debug.Log($"[AgentVisualMarker] {name} 可视化增强已初始化，baseColor={baseColor}");
        }
    }

    private void Start()
    {
        // Start 时其它运行时组件基本已挂齐，这里再刷新一次配色，避免生成顺序导致阵营色丢失。
        ResolveMarkerColors();
        ApplyMarkerColors();
    }

    private void LateUpdate()
    {
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
        }

        AnimateGroundRing();
        UpdateBeacon();
        UpdateFloatingLabel();
        UpdateMotionTrail();
    }

    /// <summary>
    /// 创建统一的视觉根节点，避免污染 agent 本体层级。
    /// </summary>
    private void EnsureVisualRoot()
    {
        Transform existingRoot = transform.Find("AgentVisualMarkerRoot");
        if (existingRoot != null)
        {
            visualRoot = existingRoot;
            return;
        }

        GameObject rootObject = new GameObject("AgentVisualMarkerRoot");
        visualRoot = rootObject.transform;
        visualRoot.SetParent(transform, false);
        visualRoot.localPosition = Vector3.zero;
        visualRoot.localRotation = Quaternion.identity;
        visualRoot.localScale = Vector3.one;
    }

    /// <summary>
    /// 使用 Sprite 默认材质，保证 LineRenderer / TrailRenderer 在 Game 视图中可见。
    /// </summary>
    private void EnsureSharedMaterial()
    {
        Shader spriteShader = Shader.Find("Sprites/Default");
        if (spriteShader == null)
        {
            Debug.LogWarning($"[AgentVisualMarker] {name} 未找到 Sprites/Default Shader，标记效果可能不可见。");
            return;
        }

        sharedSpriteMaterial = new Material(spriteShader);
    }

    /// <summary>
    /// 根据阵营和类型计算标记颜色，让不同 agent 一眼能区分。
    /// </summary>
    private void ResolveMarkerColors()
    {
        bool isAdversarial = false;
        PersonalitySystem personality = GetComponent<PersonalitySystem>();
        if (personality != null)
        {
            isAdversarial = personality.Profile.isAdversarial;
        }

        if (isAdversarial)
        {
            baseColor = new Color(1f, 0.28f, 0.22f, 0.95f);
            accentColor = new Color(1f, 0.85f, 0.8f, 1f);
            return;
        }

        AgentType agentType = agent != null && agent.Properties != null ? agent.Properties.Type : AgentType.Quadcopter;
        baseColor = agentType == AgentType.Quadcopter
            ? new Color(0.2f, 0.92f, 1f, 0.95f)
            : new Color(0.42f, 1f, 0.35f, 0.95f);
        accentColor = new Color(1f, 1f, 1f, 0.96f);
    }

    /// <summary>
    /// 创建地面高亮环，用于远视角快速定位 agent 所在位置。
    /// </summary>
    private void EnsureGroundRing()
    {
        if (ringRoot == null)
        {
            GameObject ringObject = new GameObject("GroundRing");
            ringRoot = ringObject.transform;
            ringRoot.SetParent(visualRoot, false);
        }

        ringRoot.localPosition = new Vector3(0f, ringYOffset, 0f);
        ringRoot.localRotation = Quaternion.identity;

        ringRenderer = ringRoot.GetComponent<LineRenderer>();
        if (ringRenderer == null)
        {
            ringRenderer = ringRoot.gameObject.AddComponent<LineRenderer>();
        }

        ringRenderer.loop = true;
        ringRenderer.useWorldSpace = false;
        ringRenderer.positionCount = Mathf.Max(16, ringSegments);
        ringRenderer.material = sharedSpriteMaterial;
        ringRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ringRenderer.receiveShadows = false;
        ringRenderer.textureMode = LineTextureMode.Stretch;
        ringRenderer.alignment = LineAlignment.View;
        ringRenderer.numCornerVertices = 4;
        ringRenderer.numCapVertices = 4;
        ringRenderer.startWidth = ringWidth;
        ringRenderer.endWidth = ringWidth * 0.85f;
        ringRenderer.startColor = baseColor;
        ringRenderer.endColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.35f);

        RebuildRingGeometry(ringRadius);
    }

    /// <summary>
    /// 创建头顶光柱，用于缩远地图后保持可见。
    /// </summary>
    private void EnsureBeacon()
    {
        Transform beaconRoot = visualRoot.Find("VerticalBeacon");
        if (beaconRoot == null)
        {
            GameObject beaconObject = new GameObject("VerticalBeacon");
            beaconRoot = beaconObject.transform;
            beaconRoot.SetParent(visualRoot, false);
        }

        beaconRenderer = beaconRoot.GetComponent<LineRenderer>();
        if (beaconRenderer == null)
        {
            beaconRenderer = beaconRoot.gameObject.AddComponent<LineRenderer>();
        }

        beaconRenderer.useWorldSpace = false;
        beaconRenderer.positionCount = 2;
        beaconRenderer.material = sharedSpriteMaterial;
        beaconRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        beaconRenderer.receiveShadows = false;
        beaconRenderer.alignment = LineAlignment.View;
        beaconRenderer.numCornerVertices = 6;
        beaconRenderer.numCapVertices = 6;
        beaconRenderer.startColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.95f);
        beaconRenderer.endColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.1f);
        beaconRenderer.SetPosition(0, new Vector3(0f, ringYOffset, 0f));
        beaconRenderer.SetPosition(1, new Vector3(0f, beaconHeight, 0f));
    }

    /// <summary>
    /// 创建漂浮标签，默认显示 agent ID 和状态。
    /// </summary>
    private void EnsureFloatingLabel()
    {
        Transform labelRoot = visualRoot.Find("FloatingLabel");
        if (labelRoot == null)
        {
            GameObject labelObject = new GameObject("FloatingLabel");
            labelRoot = labelObject.transform;
            labelRoot.SetParent(visualRoot, false);
        }

        labelRoot.localPosition = new Vector3(0f, labelHeight, 0f);
        labelRoot.localRotation = Quaternion.identity;
        labelRoot.localScale = Vector3.one * labelScale;

        labelText = labelRoot.GetComponent<TextMeshPro>();
        if (labelText == null)
        {
            labelText = labelRoot.gameObject.AddComponent<TextMeshPro>();
        }

        labelText.alignment = TextAlignmentOptions.Center;
        labelText.fontSize = labelFontSize;
        labelText.color = accentColor;
        labelText.outlineWidth = 0.22f;
        labelText.outlineColor = new Color(0f, 0f, 0f, 0.9f);
        labelText.enableWordWrapping = false;
        labelText.text = BuildLabelText();
    }

    /// <summary>
    /// 创建移动尾迹，帮助观察 agent 的运动方向和轨迹。
    /// </summary>
    private void EnsureMotionTrail()
    {
        motionTrail = GetComponent<TrailRenderer>();
        if (motionTrail == null)
        {
            motionTrail = gameObject.AddComponent<TrailRenderer>();
        }

        motionTrail.time = trailTime;
        motionTrail.startWidth = trailStartWidth;
        motionTrail.endWidth = trailEndWidth;
        motionTrail.minVertexDistance = 0.2f;
        motionTrail.autodestruct = false;
        motionTrail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        motionTrail.receiveShadows = false;
        motionTrail.alignment = LineAlignment.View;
        motionTrail.material = sharedSpriteMaterial;
        motionTrail.emitting = false;

        Gradient trailGradient = new Gradient();
        trailGradient.SetKeys(
            new[]
            {
                new GradientColorKey(baseColor, 0f),
                new GradientColorKey(accentColor, 0.5f),
                new GradientColorKey(baseColor, 1f),
            },
            new[]
            {
                new GradientAlphaKey(0.9f, 0f),
                new GradientAlphaKey(0.45f, 0.5f),
                new GradientAlphaKey(0f, 1f),
            });
        motionTrail.colorGradient = trailGradient;
    }

    /// <summary>
    /// 地面环做轻微呼吸和旋转，静止时也能被注意到。
    /// </summary>
    private void AnimateGroundRing()
    {
        if (ringRenderer == null || ringRoot == null)
        {
            return;
        }

        float pulse = 1f + Mathf.Sin(Time.time * ringPulseFrequency) * ringPulseAmplitude;
        ringRenderer.startWidth = ringWidth * pulse;
        ringRenderer.endWidth = ringWidth * 0.85f * pulse;
        ringRoot.localRotation = Quaternion.Euler(0f, Time.time * ringRotateSpeed, 0f);
    }

    /// <summary>
    /// 光柱做轻微宽度呼吸，放大缩远视角下的存在感。
    /// </summary>
    private void UpdateBeacon()
    {
        if (beaconRenderer == null)
        {
            return;
        }

        float pulse = 1f + Mathf.Sin(Time.time * beaconPulseFrequency) * beaconPulseAmplitude;
        beaconRenderer.startWidth = beaconWidth * pulse;
        beaconRenderer.endWidth = beaconWidth * 0.25f * pulse;
    }

    /// <summary>
    /// 标签始终朝向主相机，并根据距离略微放大，避免缩远后难以阅读。
    /// </summary>
    private void UpdateFloatingLabel()
    {
        if (labelText == null)
        {
            return;
        }

        labelText.text = BuildLabelText();

        Transform labelTransform = labelText.transform;
        labelTransform.localPosition = new Vector3(0f, labelHeight, 0f);

        if (mainCamera != null)
        {
            Vector3 toCamera = mainCamera.transform.position - labelTransform.position;
            if (toCamera.sqrMagnitude > 0.001f)
            {
                labelTransform.rotation = Quaternion.LookRotation(toCamera.normalized, Vector3.up);
            }

            float distance = Vector3.Distance(labelTransform.position, mainCamera.transform.position);
            float distanceScale = 1f + distance * labelDistanceScale;
            labelTransform.localScale = Vector3.one * labelScale * distanceScale;
        }
    }

    /// <summary>
    /// 根据速度开关尾迹，避免静止时长时间残留视觉噪声。
    /// </summary>
    private void UpdateMotionTrail()
    {
        if (motionTrail == null)
        {
            return;
        }

        Vector3 velocity = cachedRigidbody != null ? cachedRigidbody.velocity : Vector3.zero;
        motionTrail.emitting = velocity.magnitude >= minTrailSpeed;
    }

    /// <summary>
    /// 构建标签文本，优先展示 AgentID，其次展示当前状态。
    /// </summary>
    private string BuildLabelText()
    {
        string agentId = agent != null && agent.Properties != null && !string.IsNullOrWhiteSpace(agent.Properties.AgentID)
            ? agent.Properties.AgentID
            : name;

        if (!showStatusOnLabel || agent == null || agent.CurrentState == null)
        {
            return agentId;
        }

        return $"{agentId}\n{agent.CurrentState.Status}";
    }

    /// <summary>
    /// 重建圆环几何点位。
    /// </summary>
    private void RebuildRingGeometry(float targetRadius)
    {
        if (ringRenderer == null)
        {
            return;
        }

        int pointCount = Mathf.Max(16, ringSegments);
        ringRenderer.positionCount = pointCount;
        float angleStep = Mathf.PI * 2f / pointCount;
        for (int i = 0; i < pointCount; i++)
        {
            float angle = i * angleStep;
            float x = Mathf.Cos(angle) * targetRadius;
            float z = Mathf.Sin(angle) * targetRadius;
            ringRenderer.SetPosition(i, new Vector3(x, 0f, z));
        }
    }

    /// <summary>
    /// 将当前主题色应用到各个运行时可视化部件。
    /// </summary>
    private void ApplyMarkerColors()
    {
        if (ringRenderer != null)
        {
            ringRenderer.startColor = baseColor;
            ringRenderer.endColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.35f);
        }

        if (beaconRenderer != null)
        {
            beaconRenderer.startColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.95f);
            beaconRenderer.endColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.1f);
        }

        if (labelText != null)
        {
            labelText.color = accentColor;
        }

        if (motionTrail != null)
        {
            Gradient trailGradient = new Gradient();
            trailGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(baseColor, 0f),
                    new GradientColorKey(accentColor, 0.5f),
                    new GradientColorKey(baseColor, 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.9f, 0f),
                    new GradientAlphaKey(0.45f, 0.5f),
                    new GradientAlphaKey(0f, 1f),
                });
            motionTrail.colorGradient = trailGradient;
        }
    }
}
