using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 感知可视化器：用 LineRenderer 统一绘制感知射线 + 避障探测射线。
/// 挂载在每个智能体上，由 PerceptionModule 驱动感知帧，同时每帧从 AgentMotionExecutor 读取避障数据。
///
/// 可视化内容：
/// 1. 感知骨架线（低透明度，展示扫描覆盖区域）
/// 2. 感知命中射线（按检测物体类型着色）+ 命中点标记
/// 3. 避障探测射线（粗线，畅通=绿/前方碰撞=红/侧边碰撞=橙/侧边畅通=青）
/// </summary>
[RequireComponent(typeof(PerceptionModule))]
public class PerceptionVisualizer : MonoBehaviour, IPerceptionVisualizer
{
    [Header("可视化开关")]
    [Tooltip("是否显示感知射线可视化。")]
    public bool showVisualization = true;

    [Tooltip("是否显示骨架线（未命中的范围指示射线）。")]
    public bool showSkeletonRays = true;

    [Tooltip("是否显示命中射线。")]
    public bool showHitRays = true;

    [Tooltip("是否显示命中点标记。")]
    public bool showHitMarkers = true;

    [Tooltip("是否显示避障探测射线。")]
    public bool showAvoidanceRays = true;

    [Header("骨架线配置")]
    [Tooltip("骨架线数量（均匀分布在扫描范围内）。")]
    public int skeletonRayCount = 12;

    [Tooltip("骨架线宽度。")]
    public float skeletonWidth = 0.02f;

    [Tooltip("骨架线透明度。")]
    [Range(0f, 1f)]
    public float skeletonAlpha = 0.15f;

    [Header("命中射线配置")]
    [Tooltip("命中射线起始宽度。")]
    public float hitRayStartWidth = 0.08f;

    [Tooltip("命中射线末端宽度。")]
    public float hitRayEndWidth = 0.02f;

    [Tooltip("命中点标记尺寸。")]
    public float hitMarkerSize = 0.5f;

    [Tooltip("命中射线最大同时显示数量（性能保护）。")]
    public int maxHitRays = 30;

    [Header("避障射线配置")]
    [Tooltip("避障前方射线宽度。")]
    public float avoidanceFwdWidth = 0.12f;

    [Tooltip("避障侧边射线宽度。")]
    public float avoidanceSideWidth = 0.06f;

    // ─── 颜色常量 ──────────────────────────────────────────────────────
    private static readonly Color AvoidClear     = new Color(0.15f, 1f, 0.3f, 0.9f);   // 绿：畅通
    private static readonly Color AvoidHitFwd    = new Color(1f, 0.2f, 0.1f, 0.95f);   // 红：前方碰撞
    private static readonly Color AvoidSideClear = new Color(0.15f, 0.85f, 1f, 0.8f);  // 青：侧边畅通
    private static readonly Color AvoidSideHit   = new Color(1f, 0.55f, 0.05f, 0.9f);  // 橙：侧边碰撞

    // ─── 类型颜色映射 ────────────────────────────────────────────────────
    private static readonly Dictionary<SmallNodeType, Color> TypeColors = new Dictionary<SmallNodeType, Color>
    {
        { SmallNodeType.Agent,             new Color(0.74f, 0.55f, 1.0f, 0.9f) },
        { SmallNodeType.ResourcePoint,     new Color(1.0f, 0.6f, 0.1f, 0.9f) },
        { SmallNodeType.Tree,              new Color(0.2f, 0.8f, 0.3f, 0.9f) },
        { SmallNodeType.Pedestrian,        new Color(1.0f, 0.85f, 0.1f, 0.9f) },
        { SmallNodeType.Vehicle,           new Color(0.3f, 0.65f, 1.0f, 0.9f) },
        { SmallNodeType.TemporaryObstacle, new Color(0.6f, 0.6f, 0.6f, 0.7f) },
        { SmallNodeType.Unknown,           new Color(0.5f, 0.5f, 0.5f, 0.5f) },
    };

    // ─── 内部状态 ────────────────────────────────────────────────────────
    private Material lineMaterial;
    private readonly List<LineRenderer> skeletonPool = new List<LineRenderer>();
    private readonly List<LineRenderer> hitRayPool   = new List<LineRenderer>();
    private readonly List<GameObject>   hitMarkerPool = new List<GameObject>();
    private int activeHitRays;
    private int activeHitMarkers;

    // 避障射线（固定 3 条：前方、左、右）
    private LineRenderer lrAvoidFwd;
    private LineRenderer lrAvoidLeft;
    private LineRenderer lrAvoidRight;

    // 依赖缓存
    private AgentMotionExecutor _motionExecutor;

    // 上一次感知帧缓存（感知 0.5s 更新一次，避障每帧更新）
    private PerceptionVisualizationFrame _lastFrame;

    // ─── Unity 生命周期 ──────────────────────────────────────────────────

    private void Start()
    {
        lineMaterial = new Material(Shader.Find("Sprites/Default"));
        _motionExecutor = GetComponent<AgentMotionExecutor>();

        // 预分配骨架线
        for (int i = 0; i < skeletonRayCount; i++)
            skeletonPool.Add(CreateLR($"PercSkeleton_{i}"));

        // 预分配命中射线池
        for (int i = 0; i < maxHitRays; i++)
            hitRayPool.Add(CreateLR($"PercHitRay_{i}"));

        // 预分配命中点标记池
        for (int i = 0; i < maxHitRays; i++)
            hitMarkerPool.Add(CreateMarker($"PercHitMarker_{i}"));

        // 避障射线（固定 3 条，单独管理）
        lrAvoidFwd   = CreateLR("AvoidRay_Fwd");
        lrAvoidLeft  = CreateLR("AvoidRay_Left");
        lrAvoidRight = CreateLR("AvoidRay_Right");
    }

    private void Update()
    {
        // 避障射线每帧更新（独立于感知帧的 0.5s 间隔）
        RenderAvoidanceRays();
    }

    private void OnDestroy()
    {
        if (lineMaterial != null)
            Destroy(lineMaterial);
    }

    // ─── IPerceptionVisualizer 实现 ──────────────────────────────────────

    public void RenderFrame(PerceptionVisualizationFrame frame)
    {
        if (!showVisualization || frame == null)
        {
            ClearFrame();
            return;
        }

        _lastFrame = frame;
        RenderSkeletonRays(frame);
        RenderHitRays(frame);
    }

    public void ClearFrame()
    {
        foreach (var lr in skeletonPool)
            if (lr != null) lr.enabled = false;
        for (int i = 0; i < hitRayPool.Count; i++)
            if (hitRayPool[i] != null) hitRayPool[i].enabled = false;
        for (int i = 0; i < hitMarkerPool.Count; i++)
            if (hitMarkerPool[i] != null) hitMarkerPool[i].SetActive(false);
        activeHitRays = 0;
        activeHitMarkers = 0;
        _lastFrame = null;
    }

    // ─── 骨架线渲染 ─────────────────────────────────────────────────────

    private void RenderSkeletonRays(PerceptionVisualizationFrame frame)
    {
        bool show = showSkeletonRays && showVisualization && skeletonPool.Count > 0;
        for (int i = 0; i < skeletonPool.Count; i++)
        {
            if (skeletonPool[i] == null) continue;
            skeletonPool[i].enabled = show;
        }
        if (!show) return;

        Vector3 origin = frame.agentPosition;
        float range = frame.perceptionRange;
        Color skeletonColor = new Color(0.4f, 0.8f, 1.0f, skeletonAlpha);

        if (frame.agentType == AgentType.Quadcopter)
        {
            RenderDroneSkeletonRays(origin, range, skeletonColor);
        }
        else
        {
            RenderGroundSkeletonRays(origin, range, frame.agentForward,
                frame.groundHorizontalAngle, skeletonColor);
        }
    }

    private void RenderDroneSkeletonRays(Vector3 origin, float range, Color color)
    {
        int horizCount = Mathf.Max(4, skeletonRayCount - 4);
        int downCount  = Mathf.Min(4, skeletonRayCount);
        int idx = 0;

        // 水平层骨架
        for (int i = 0; i < horizCount && idx < skeletonPool.Count; i++, idx++)
        {
            float yaw = 360f * i / horizCount;
            Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            SetLineLR(skeletonPool[idx], origin, origin + dir * range, color, skeletonWidth, skeletonWidth * 0.5f);
        }

        // 向下锥形骨架
        for (int i = 0; i < downCount && idx < skeletonPool.Count; i++, idx++)
        {
            float yaw = 360f * i / downCount;
            Vector3 dir = Quaternion.Euler(60f, yaw, 0f) * Vector3.forward;
            SetLineLR(skeletonPool[idx], origin, origin + dir * range, color, skeletonWidth, skeletonWidth * 0.5f);
        }

        for (; idx < skeletonPool.Count; idx++)
            if (skeletonPool[idx] != null) skeletonPool[idx].enabled = false;
    }

    private void RenderGroundSkeletonRays(Vector3 origin, float range, Vector3 forward,
        float horizontalAngle, Color color)
    {
        float halfAngle = horizontalAngle / 2f;
        for (int i = 0; i < skeletonPool.Count; i++)
        {
            float t = (float)i / Mathf.Max(1, skeletonPool.Count - 1);
            float yaw = Mathf.Lerp(-halfAngle, halfAngle, t);
            Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * forward;
            SetLineLR(skeletonPool[i], origin, origin + dir * range, color, skeletonWidth, skeletonWidth * 0.5f);
        }
    }

    // ─── 命中射线渲染 ───────────────────────────────────────────────────

    private void RenderHitRays(PerceptionVisualizationFrame frame)
    {
        activeHitRays = 0;
        activeHitMarkers = 0;

        if (!showHitRays || frame.detectionPoints == null)
        {
            DisableUnusedHitRays();
            DisableUnusedMarkers();
            return;
        }

        Vector3 origin = frame.agentPosition;

        foreach (var det in frame.detectionPoints)
        {
            if (activeHitRays >= hitRayPool.Count) break;

            Color color = GetTypeColor(det.type);
            SetLineLR(hitRayPool[activeHitRays], origin, det.position,
                color, hitRayStartWidth, hitRayEndWidth);
            activeHitRays++;

            PlaceHitMarker(det.position, color, hitMarkerSize);
        }

        // 敌方检测用鲜红色高亮
        if (frame.enemyDetections != null)
        {
            Color enemyColor = new Color(1f, 0.1f, 0.1f, 0.95f);
            foreach (var enemy in frame.enemyDetections)
            {
                if (activeHitRays >= hitRayPool.Count) break;

                SetLineLR(hitRayPool[activeHitRays], origin, enemy.position,
                    enemyColor, hitRayStartWidth * 1.5f, hitRayEndWidth);
                activeHitRays++;

                PlaceHitMarker(enemy.position, enemyColor, hitMarkerSize * 1.5f);
            }
        }

        DisableUnusedHitRays();
        DisableUnusedMarkers();
    }

    // ─── 避障射线渲染（每帧更新）────────────────────────────────────────

    private void RenderAvoidanceRays()
    {
        bool show = showVisualization && showAvoidanceRays
                    && _motionExecutor != null;
        var probe = show ? _motionExecutor.CurrentAvoidanceProbe : default;

        if (!show || !probe.valid)
        {
            if (lrAvoidFwd   != null) lrAvoidFwd.enabled   = false;
            if (lrAvoidLeft  != null) lrAvoidLeft.enabled  = false;
            if (lrAvoidRight != null) lrAvoidRight.enabled = false;
            return;
        }

        // 显示转向速度向量：绿=安全，红=高危，黄=网格约束
        Color velColor = probe.gridConstrained ? AvoidSideHit
                       : probe.maxDanger > 0.5f ? AvoidHitFwd
                       : AvoidClear;
        Vector3 velEnd = probe.origin + probe.resultVelocity;
        SetLineLR(lrAvoidFwd, probe.origin, velEnd,
            velColor, avoidanceFwdWidth, avoidanceFwdWidth * 0.25f);

        // 左右射线在新系统中不再使用，隐藏
        if (lrAvoidLeft  != null) lrAvoidLeft.enabled  = false;
        if (lrAvoidRight != null) lrAvoidRight.enabled = false;
    }

    // ─── 辅助 ────────────────────────────────────────────────────────────

    private void SetLineLR(LineRenderer lr, Vector3 from, Vector3 to,
        Color color, float startW, float endW)
    {
        lr.enabled = true;
        lr.SetPosition(0, from);
        lr.SetPosition(1, to);
        lr.startColor = color;
        lr.endColor = new Color(color.r, color.g, color.b, color.a * 0.15f);
        lr.startWidth = startW;
        lr.endWidth = endW;
    }

    private void PlaceHitMarker(Vector3 position, Color color, float size)
    {
        if (!showHitMarkers || activeHitMarkers >= hitMarkerPool.Count) return;

        var marker = hitMarkerPool[activeHitMarkers];
        marker.SetActive(true);
        marker.transform.position = position;
        marker.transform.localScale = Vector3.one * size;
        var mr = marker.GetComponent<MeshRenderer>();
        if (mr != null) mr.material.color = color;
        activeHitMarkers++;
    }

    private LineRenderer CreateLR(string goName)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(transform);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.material = lineMaterial;
        lr.positionCount = 2;
        lr.enabled = false;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    private GameObject CreateMarker(string goName)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = goName;
        go.transform.SetParent(transform);
        go.transform.localScale = Vector3.one * hitMarkerSize;

        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        go.SetActive(false);
        return go;
    }

    private void DisableUnusedHitRays()
    {
        for (int i = activeHitRays; i < hitRayPool.Count; i++)
            if (hitRayPool[i] != null) hitRayPool[i].enabled = false;
    }

    private void DisableUnusedMarkers()
    {
        for (int i = activeHitMarkers; i < hitMarkerPool.Count; i++)
            if (hitMarkerPool[i] != null) hitMarkerPool[i].SetActive(false);
    }

    private static Color GetTypeColor(SmallNodeType type)
    {
        return TypeColors.TryGetValue(type, out Color c) ? c : TypeColors[SmallNodeType.Unknown];
    }
}
