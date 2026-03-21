// Other_Modules/PerceptionVisualizer.cs
// 感知可视化实现：从 PerceptionModule 分离出来的所有渲染逻辑。
// 挂载到与 PerceptionModule 相同的 GameObject 上。
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 实现 IPerceptionVisualizer，提供完整的感知结果可视化。
/// 包括检测射线、球体标记、敌方红色标记、紧急事件闪烁标记。
/// </summary>
public class PerceptionVisualizer : MonoBehaviour, IPerceptionVisualizer
{
    [Header("全局开关")]
    public bool visualizationEnabled = true;

    [Header("颜色配置")]
    public Color resourceColor  = new Color(0.67f, 0.85f, 0.89f);
    public Color obstacleColor  = new Color(0.96f, 0.82f, 0.87f);
    public Color rangeColor     = new Color(0.72f, 0.89f, 0.87f, 0.4f);
    public Color enemyColor     = new Color(1f, 0.2f, 0.2f, 0.9f);
    public Color emergencyColor = new Color(1f, 0.65f, 0f, 0.9f);

    [Header("线宽")]
    public float lineWidth      = 0.05f;
    [Range(1f, 6f)] public float lineWidthBoost = 2.8f;

    [Header("球体标记")]
    public Vector2 sphereSizeRange  = new Vector2(0.1f, 0.2f);
    public float   blinkFrequency   = 2f;
    [Range(1f, 6f)] public float markerSizeBoost = 2.4f;
    [Min(0.5f)] public float markerLifetime = 3f;
    [Min(0f)]   public float markerHeightOffset = 0.35f;
    [Range(0.2f, 1f)] public float markerMinAlpha = 0.55f;
    [Range(0f, 6f)]   public float markerEmissionIntensity = 2.0f;

    [Header("射线")]
    [Range(0.2f, 1f)] public float rayAlpha = 0.9f;
    [Min(0.2f)] public float rayLifetime = 2f;

    // ─── 私有状态 ─────────────────────────────────────────────────
    private LineRenderer rangeRenderer;
    private Material     lineMaterial;
    private Material     sphereMaterial;

    private readonly Queue<GameObject>   spherePool   = new();
    private readonly List<GameObject>    sphereMarkers = new();
    private readonly List<(GameObject go, float expiry)> markerMeta = new();

    private const int PoolSize = 50;

    private struct RayDrawCmd
    {
        public Vector3 from, to;
        public Color   color;
        public float   expiry;
    }
    private readonly List<RayDrawCmd> pendingRays = new();

    // ─────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        lineMaterial   = new Material(Shader.Find("Sprites/Default")) { name = "PVizLine" };
        sphereMaterial = new Material(Shader.Find("Standard"))         { name = "PVizSphere" };

        rangeRenderer            = gameObject.AddComponent<LineRenderer>();
        rangeRenderer.material   = lineMaterial;
        rangeRenderer.loop       = true;
        rangeRenderer.startWidth = lineWidth * lineWidthBoost;
        rangeRenderer.endWidth   = lineWidth * lineWidthBoost;
        rangeRenderer.positionCount = 0;
    }

    private void Update()
    {
        if (!visualizationEnabled) return;
        CleanExpiredMarkers();
        DrawPendingRays();
    }

    // ─────────────────────────────────────────────────────────────
    // IPerceptionVisualizer 实现
    // ─────────────────────────────────────────────────────────────

    public void OnDetectionUpdated(
        List<DetectionPointSnapshot> points,
        Vector3 agentPos, float range, AgentType agentType)
    {
        if (!visualizationEnabled) return;

        DrawRangeCircle(agentPos, range);

        foreach (var pt in points)
        {
            Color col = pt.type == SmallNodeType.ResourcePoint ? resourceColor : obstacleColor;
            col.a = rayAlpha;
            pendingRays.Add(new RayDrawCmd
            {
                from   = agentPos,
                to     = pt.position,
                color  = col,
                expiry = Time.time + rayLifetime
            });
            PlaceSphereMarker(pt.position + Vector3.up * markerHeightOffset, col, Time.time + markerLifetime);
        }
    }

    public void OnEnemyDetected(IntelligentAgent enemy, Vector3 pos)
    {
        if (!visualizationEnabled || enemy == null) return;
        PlaceSphereMarker(pos + Vector3.up * markerHeightOffset, enemyColor, Time.time + markerLifetime);
        // 红色射线
        pendingRays.Add(new RayDrawCmd
        {
            from   = transform.position,
            to     = pos,
            color  = enemyColor,
            expiry = Time.time + rayLifetime
        });
    }

    public void OnEmergencyDetected(string eventDesc, Vector3 pos)
    {
        if (!visualizationEnabled) return;
        StartCoroutine(BlinkMarker(pos + Vector3.up * markerHeightOffset, emergencyColor, markerLifetime));
    }

    public void SetEnabled(bool enabled)
    {
        visualizationEnabled = enabled;
        if (!enabled)
        {
            rangeRenderer.positionCount = 0;
            foreach (var go in sphereMarkers) RecycleSphere(go);
            sphereMarkers.Clear();
            markerMeta.Clear();
            pendingRays.Clear();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 内部渲染
    // ─────────────────────────────────────────────────────────────

    private void DrawRangeCircle(Vector3 center, float radius)
    {
        int segments = 36;
        rangeRenderer.positionCount = segments + 1;
        for (int i = 0; i <= segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            rangeRenderer.SetPosition(i,
                center + new Vector3(Mathf.Cos(a) * radius, 0.2f, Mathf.Sin(a) * radius));
        }
        rangeRenderer.startColor = rangeColor;
        rangeRenderer.endColor   = rangeColor;
    }

    private void DrawPendingRays()
    {
        float now = Time.time;
        for (int i = pendingRays.Count - 1; i >= 0; i--)
        {
            var cmd = pendingRays[i];
            if (now > cmd.expiry) { pendingRays.RemoveAt(i); continue; }
            Debug.DrawLine(cmd.from, cmd.to, cmd.color);
        }
    }

    private void PlaceSphereMarker(Vector3 pos, Color color, float expiry)
    {
        GameObject go = GetSphere();
        go.transform.position = pos;
        float size = Random.Range(sphereSizeRange.x, sphereSizeRange.y) * markerSizeBoost;
        go.transform.localScale = Vector3.one * size;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mat = mr.material;
            mat.color = color;
            if (markerEmissionIntensity > 0f)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", color * markerEmissionIntensity);
            }
        }

        sphereMarkers.Add(go);
        markerMeta.Add((go, expiry));
    }

    private IEnumerator BlinkMarker(Vector3 pos, Color color, float lifetime)
    {
        GameObject go = GetSphere();
        go.transform.position   = pos;
        go.transform.localScale = Vector3.one * sphereSizeRange.y * markerSizeBoost * 1.5f;
        var mr  = go.GetComponent<MeshRenderer>();
        float elapsed = 0f;

        while (elapsed < lifetime)
        {
            float blink = Mathf.Abs(Mathf.Sin(elapsed * blinkFrequency * Mathf.PI));
            if (mr != null) mr.material.color = new Color(color.r, color.g, color.b, blink);
            elapsed += Time.deltaTime;
            yield return null;
        }

        RecycleSphere(go);
    }

    private void CleanExpiredMarkers()
    {
        float now = Time.time;
        for (int i = markerMeta.Count - 1; i >= 0; i--)
        {
            var (go, expiry) = markerMeta[i];
            if (now > expiry)
            {
                RecycleSphere(go);
                sphereMarkers.Remove(go);
                markerMeta.RemoveAt(i);
            }
        }
    }

    private GameObject GetSphere()
    {
        if (spherePool.Count > 0)
        {
            var go = spherePool.Dequeue();
            go.SetActive(true);
            return go;
        }
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(sphere.GetComponent<Collider>());
        sphere.GetComponent<MeshRenderer>().material = sphereMaterial;
        return sphere;
    }

    private void RecycleSphere(GameObject go)
    {
        if (go == null) return;
        go.SetActive(false);
        if (spherePool.Count < PoolSize) spherePool.Enqueue(go);
        else Destroy(go);
    }
}
