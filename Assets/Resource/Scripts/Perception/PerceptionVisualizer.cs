// Other_Modules/PerceptionVisualizer.cs
// 感知结果的唯一可视化实现。PerceptionModule 不再包含任何渲染逻辑。
using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(PerceptionModule))]
public class PerceptionVisualizer : MonoBehaviour, IPerceptionVisualizer
{
    [Header("全局开关")]
    public bool visualizationEnabled = true;
    public bool showGizmos = true;
    public bool showGameViewVisualization = true;
    public bool showDetectionRays = true;
    [Min(1)] public int raySimplificationFactor = 2;

    [Header("颜色配置")]
    public Color resourceColor = new Color(0.67f, 0.85f, 0.89f);
    public Color obstacleColor = new Color(0.96f, 0.82f, 0.87f);
    public Color rangeColor = new Color(0.72f, 0.89f, 0.87f, 0.4f);
    public Color rangeFillColor = new Color(0.72f, 0.89f, 0.87f, 0.15f);
    public Color enemyColor = new Color(1f, 0.2f, 0.2f, 0.9f);
    public Color alertColor = new Color(1f, 0.65f, 0f, 0.9f);

    [Header("射线与范围")]
    public float lineWidth = 0.03f;
    [Range(1f, 6f)] public float lineWidthBoost = 2.8f;
    [Range(0.2f, 1f)] public float rayAlpha = 0.95f;
    [Min(0.2f)] public float rayLifetime = 2f;

    [Header("标记")]
    public Vector2 sphereSizeRange = new Vector2(0.08f, 0.15f);
    public float blinkFrequency = 2f;
    [Min(0.5f)] public float markerLifetime = 3f;
    [Range(1f, 6f)] public float markerSizeBoost = 2.4f;
    [Min(0f)] public float markerHeightOffset = 0.35f;
    [Range(0.2f, 1f)] public float markerMinAlpha = 0.55f;
    [Range(0f, 6f)] public float markerEmissionIntensity = 2.0f;

    private Material lineMaterial;
    private Material sphereMaterial;
    private MaterialPropertyBlock spherePropertyBlock;
    private LineRenderer rangeRenderer;
    private LineRenderer rayRenderer;

    private readonly Queue<GameObject> spherePool = new();
    private readonly List<GameObject> sphereMarkers = new();
    private readonly Dictionary<GameObject, MarkerMeta> markerMeta = new();
    private readonly List<RayDrawCmd> pendingRays = new();

    private const int PoolSize = 50;
    private PerceptionModule perceptionModule;
    private IntelligentAgent agent;
    private PerceptionVisualizationFrame latestFrame;
    private float lastConsumedPerceptionTime = -1f;

    private struct MarkerMeta
    {
        public float createdTime;
        public Color baseColor;
    }

    private struct RayDrawCmd
    {
        public Vector3 from;
        public Vector3 to;
        public Color color;
        public float createdTime;
    }

    private void Awake()
    {
        perceptionModule = GetComponent<PerceptionModule>();
        agent = GetComponent<IntelligentAgent>();

        lineMaterial = new Material(Shader.Find("Sprites/Default")) { name = "PerceptionVizLine" };
        lineMaterial.hideFlags = HideFlags.DontSave;

        sphereMaterial = new Material(Shader.Find("Standard")) { name = "PerceptionVizSphere" };
        sphereMaterial.hideFlags = HideFlags.DontSave;
        sphereMaterial.renderQueue = 3000;
        sphereMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        sphereMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        sphereMaterial.SetInt("_ZWrite", 0);
        sphereMaterial.DisableKeyword("_ALPHATEST_ON");
        sphereMaterial.EnableKeyword("_ALPHABLEND_ON");
        sphereMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        sphereMaterial.EnableKeyword("_EMISSION");
        spherePropertyBlock = new MaterialPropertyBlock();

        GameObject rangeObj = new GameObject("PerceptionRangeRenderer");
        rangeObj.transform.SetParent(transform, false);
        rangeRenderer = rangeObj.AddComponent<LineRenderer>();
        ConfigureLineRenderer(rangeRenderer, sortingOrder: 5000);

        GameObject rayObj = new GameObject("PerceptionRayRenderer");
        rayObj.transform.SetParent(transform, false);
        rayRenderer = rayObj.AddComponent<LineRenderer>();
        ConfigureLineRenderer(rayRenderer, sortingOrder: 5001);
    }

    private void Update()
    {
        if (!visualizationEnabled)
        {
            ClearRuntimeRenderers();
            return;
        }

        RefreshFromPerceptionModule();
        CleanExpiredMarkers();
        DrawPendingRays();
        DrawRangeInGameView();
    }

    /// <summary>
    /// 可视化器主动读取感知模块结果，避免 PerceptionModule 反向依赖显示层。
    /// </summary>
    private void RefreshFromPerceptionModule()
    {
        if (perceptionModule == null || agent == null || agent.Properties == null)
            return;

        float latestPerceptionTime = perceptionModule.LastPerceptionTime;
        if (latestPerceptionTime <= 0f || Mathf.Approximately(latestPerceptionTime, lastConsumedPerceptionTime))
            return;

        RenderFrame(BuildFrameFromModule(latestPerceptionTime));
        lastConsumedPerceptionTime = latestPerceptionTime;
    }

    private PerceptionVisualizationFrame BuildFrameFromModule(float timestamp)
    {
        PerceptionVisualizationFrame frame = new PerceptionVisualizationFrame
        {
            agentPosition = transform.position,
            agentForward = transform.forward,
            perceptionRange = agent.Properties.PerceptionRange,
            agentType = agent.Properties.Type,
            groundHorizontalAngle = perceptionModule != null ? perceptionModule.groundHorizontalAngle : 0f
        };

        if (perceptionModule == null)
            return frame;

        for (int i = 0; i < perceptionModule.detectedObjects.Count; i++)
        {
            SmallNodeData node = perceptionModule.detectedObjects[i];
            if (node == null) continue;

            frame.detectionPoints.Add(new DetectionPointSnapshot
            {
                position = node.WorldPosition,
                type = node.NodeType,
                timestamp = timestamp,
                rayIndex = i
            });

            if (node.NodeType == SmallNodeType.TemporaryObstacle)
            {
                frame.alerts.Add(new PerceptionAlertSnapshot
                {
                    description = $"障碍 {node.SceneObject?.name ?? node.NodeType.ToString()} 阻断路径",
                    position = node.WorldPosition,
                    timestamp = timestamp
                });
            }
        }

        for (int i = 0; i < perceptionModule.enemyAgents.Count; i++)
        {
            IntelligentAgent enemy = perceptionModule.enemyAgents[i];
            if (enemy == null || enemy.Properties == null) continue;

            frame.enemyDetections.Add(new EnemyDetectionSnapshot
            {
                agentId = enemy.Properties.AgentID,
                position = enemy.transform.position,
                timestamp = timestamp
            });
        }

        return frame;
    }

    public void RenderFrame(PerceptionVisualizationFrame frame)
    {
        latestFrame = frame;
        if (!visualizationEnabled || frame == null) return;

        Debug.Log($"[PerceptionVisualizer] 收到感知帧: points={frame.detectionPoints.Count}, enemies={frame.enemyDetections.Count}, alerts={frame.alerts.Count}");

        if (showDetectionRays)
        {
            QueueDetectionRays(frame);
        }

        QueueMarkers(frame);
    }

    public void ClearFrame()
    {
        latestFrame = null;
        pendingRays.Clear();
        ClearRuntimeRenderers();

        for (int i = sphereMarkers.Count - 1; i >= 0; i--)
        {
            RecycleSphere(sphereMarkers[i]);
        }
        sphereMarkers.Clear();
        markerMeta.Clear();
    }

    private void OnDrawGizmos()
    {
        if (!visualizationEnabled || !showGizmos || latestFrame == null) return;

        DrawRangeInSceneView(latestFrame);
        DrawDetectionPointsInSceneView(latestFrame);
    }

    private void OnDestroy()
    {
        ClearFrame();

        while (spherePool.Count > 0)
        {
            GameObject pooled = spherePool.Dequeue();
            if (pooled != null) Destroy(pooled);
        }

        if (rangeRenderer != null) Destroy(rangeRenderer.gameObject);
        if (rayRenderer != null) Destroy(rayRenderer.gameObject);
        if (lineMaterial != null) Destroy(lineMaterial);
        if (sphereMaterial != null) Destroy(sphereMaterial);
    }

    private void ConfigureLineRenderer(LineRenderer renderer, int sortingOrder)
    {
        renderer.material = lineMaterial;
        renderer.useWorldSpace = true;
        renderer.hideFlags = HideFlags.DontSave;
        renderer.alignment = LineAlignment.View;
        renderer.numCapVertices = 6;
        renderer.numCornerVertices = 6;
        renderer.textureMode = LineTextureMode.Stretch;
        renderer.sortingOrder = sortingOrder;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.widthMultiplier = GetEffectiveLineWidth();
        renderer.positionCount = 0;
    }

    private void QueueDetectionRays(PerceptionVisualizationFrame frame)
    {
        for (int i = 0; i < frame.detectionPoints.Count; i++)
        {
            DetectionPointSnapshot pt = frame.detectionPoints[i];
            if (pt.rayIndex % Mathf.Max(1, raySimplificationFactor) != 0) continue;

            Color color = GetDetectionColor(pt.type);
            color.a = Mathf.Clamp01(rayAlpha);
            pendingRays.Add(new RayDrawCmd
            {
                from = frame.agentPosition,
                to = pt.position + Vector3.up * markerHeightOffset,
                color = color,
                createdTime = Time.time
            });
        }

        for (int i = 0; i < frame.enemyDetections.Count; i++)
        {
            EnemyDetectionSnapshot enemy = frame.enemyDetections[i];
            Color color = enemyColor;
            color.a = Mathf.Clamp01(rayAlpha);
            pendingRays.Add(new RayDrawCmd
            {
                from = frame.agentPosition,
                to = enemy.position + Vector3.up * markerHeightOffset,
                color = color,
                createdTime = Time.time
            });
        }
    }

    private void QueueMarkers(PerceptionVisualizationFrame frame)
    {
        for (int i = 0; i < frame.detectionPoints.Count; i++)
        {
            DetectionPointSnapshot pt = frame.detectionPoints[i];
            PlaceSphereMarker(pt.position + Vector3.up * markerHeightOffset, GetDetectionColor(pt.type));
        }

        for (int i = 0; i < frame.enemyDetections.Count; i++)
        {
            EnemyDetectionSnapshot enemy = frame.enemyDetections[i];
            PlaceSphereMarker(enemy.position + Vector3.up * markerHeightOffset, enemyColor);
        }

        for (int i = 0; i < frame.alerts.Count; i++)
        {
            PerceptionAlertSnapshot alert = frame.alerts[i];
            PlaceSphereMarker(alert.position + Vector3.up * markerHeightOffset, alertColor, sizeMultiplier: 1.5f);
        }
    }

    private void DrawPendingRays()
    {
        if (rayRenderer == null || !showGameViewVisualization || !showDetectionRays)
        {
            if (rayRenderer != null) rayRenderer.positionCount = 0;
            return;
        }

        float now = Time.time;
        int lineIndex = 0;
        rayRenderer.positionCount = 0;
        rayRenderer.widthMultiplier = GetEffectiveLineWidth() * 0.75f;

        for (int i = pendingRays.Count - 1; i >= 0; i--)
        {
            RayDrawCmd cmd = pendingRays[i];
            float age = now - cmd.createdTime;
            if (age > Mathf.Max(0.2f, rayLifetime))
            {
                pendingRays.RemoveAt(i);
                continue;
            }

            Color color = cmd.color;
            color.a *= Mathf.Clamp01(1f - age / Mathf.Max(0.2f, rayLifetime));
            AddLineToRenderer(rayRenderer, cmd.from, cmd.to, color, ref lineIndex);
        }

        rayRenderer.positionCount = lineIndex;
    }

    private void DrawRangeInGameView()
    {
        if (rangeRenderer == null)
            return;

        if (!showGameViewVisualization || latestFrame == null)
        {
            rangeRenderer.positionCount = 0;
            return;
        }

        rangeRenderer.widthMultiplier = GetEffectiveLineWidth();
        if (latestFrame.agentType == AgentType.Quadcopter)
        {
            DrawCircleWithLineRenderer(latestFrame.agentPosition, latestFrame.perceptionRange, GetRangeVizColor(false), 32);
            return;
        }

        DrawSectorWithLineRenderer(
            latestFrame.agentPosition,
            latestFrame.agentForward,
            latestFrame.groundHorizontalAngle,
            latestFrame.perceptionRange,
            GetRangeVizColor(false),
            32);
    }

    private void DrawRangeInSceneView(PerceptionVisualizationFrame frame)
    {
        Gizmos.color = GetRangeVizColor(false);
        if (frame.agentType == AgentType.Quadcopter)
        {
            Gizmos.color = GetRangeVizColor(true);
            Gizmos.DrawSphere(frame.agentPosition, frame.perceptionRange);
            Gizmos.color = GetRangeVizColor(false);
            Gizmos.DrawWireSphere(frame.agentPosition, frame.perceptionRange);
            return;
        }

        float halfAngle = frame.groundHorizontalAngle / 2f;
        Vector3 leftDir = Quaternion.Euler(0f, -halfAngle, 0f) * frame.agentForward;
        Vector3 rightDir = Quaternion.Euler(0f, halfAngle, 0f) * frame.agentForward;
        Gizmos.DrawRay(frame.agentPosition, leftDir * frame.perceptionRange);
        Gizmos.DrawRay(frame.agentPosition, rightDir * frame.perceptionRange);
        DrawArc(frame.agentPosition, frame.agentForward, halfAngle, frame.perceptionRange);
    }

    private void DrawDetectionPointsInSceneView(PerceptionVisualizationFrame frame)
    {
        float now = Time.time;

        for (int i = 0; i < frame.detectionPoints.Count; i++)
        {
            DetectionPointSnapshot pt = frame.detectionPoints[i];
            float age = now - pt.timestamp;
            float alpha = Mathf.Clamp01(1f - age / Mathf.Max(0.5f, markerLifetime));
            alpha = Mathf.Max(Mathf.Clamp01(markerMinAlpha), alpha);
            Color color = GetDetectionColor(pt.type);
            color.a = alpha;
            Gizmos.color = color;
            float size = Mathf.Lerp(GetEffectiveSphereSizeRange().x, GetEffectiveSphereSizeRange().y, 0.5f) * alpha;
            Gizmos.DrawSphere(pt.position + Vector3.up * markerHeightOffset, size);
        }

        for (int i = 0; i < frame.enemyDetections.Count; i++)
        {
            Gizmos.color = enemyColor;
            Gizmos.DrawSphere(frame.enemyDetections[i].position + Vector3.up * markerHeightOffset, GetEffectiveSphereSizeRange().y);
        }

        for (int i = 0; i < frame.alerts.Count; i++)
        {
            Gizmos.color = alertColor;
            Gizmos.DrawSphere(frame.alerts[i].position + Vector3.up * markerHeightOffset, GetEffectiveSphereSizeRange().y * 1.2f);
        }
    }

    private void PlaceSphereMarker(Vector3 position, Color color, float sizeMultiplier = 1f)
    {
        GameObject sphere = GetSphere();
        sphere.transform.position = position;

        Vector2 sizeRange = GetEffectiveSphereSizeRange();
        float blinkFactor = Mathf.Sin(Time.time * blinkFrequency * Mathf.PI) * 0.5f + 0.5f;
        float size = Mathf.Lerp(sizeRange.x, sizeRange.y, blinkFactor) * sizeMultiplier;
        sphere.transform.localScale = Vector3.one * size;

        Renderer renderer = sphere.GetComponent<Renderer>();
        if (renderer != null)
        {
            spherePropertyBlock.Clear();
            spherePropertyBlock.SetColor("_Color", color);
            spherePropertyBlock.SetColor("_BaseColor", color);
            spherePropertyBlock.SetColor("_EmissionColor", color * Mathf.Max(0f, markerEmissionIntensity));
            renderer.SetPropertyBlock(spherePropertyBlock);
        }

        sphereMarkers.Add(sphere);
        markerMeta[sphere] = new MarkerMeta
        {
            createdTime = Time.time,
            baseColor = color
        };
    }

    private void CleanExpiredMarkers()
    {
        float now = Time.time;
        float safeLifetime = Mathf.Max(0.5f, markerLifetime);
        Vector2 sizeRange = GetEffectiveSphereSizeRange();

        for (int i = sphereMarkers.Count - 1; i >= 0; i--)
        {
            GameObject sphere = sphereMarkers[i];
            if (sphere == null)
            {
                sphereMarkers.RemoveAt(i);
                continue;
            }

            if (!markerMeta.TryGetValue(sphere, out MarkerMeta meta))
            {
                meta = new MarkerMeta { createdTime = now, baseColor = obstacleColor };
                markerMeta[sphere] = meta;
            }

            float age = now - meta.createdTime;
            float alpha = Mathf.Clamp01(1f - age / safeLifetime);
            alpha = Mathf.Max(Mathf.Clamp01(markerMinAlpha), alpha);

            float blinkFactor = Mathf.Sin(now * blinkFrequency * Mathf.PI) * 0.5f + 0.5f;
            float size = Mathf.Lerp(sizeRange.x, sizeRange.y, blinkFactor);
            sphere.transform.localScale = Vector3.one * size;

            Renderer renderer = sphere.GetComponent<Renderer>();
            if (renderer != null)
            {
                Color color = meta.baseColor;
                color.a = alpha;
                spherePropertyBlock.Clear();
                spherePropertyBlock.SetColor("_Color", color);
                spherePropertyBlock.SetColor("_BaseColor", color);
                spherePropertyBlock.SetColor("_EmissionColor", meta.baseColor * Mathf.Max(0f, markerEmissionIntensity));
                renderer.SetPropertyBlock(spherePropertyBlock);
            }

            if (age > safeLifetime)
            {
                markerMeta.Remove(sphere);
                RecycleSphere(sphere);
                sphereMarkers.RemoveAt(i);
            }
        }
    }

    private GameObject GetSphere()
    {
        if (spherePool.Count > 0)
        {
            GameObject pooled = spherePool.Dequeue();
            pooled.SetActive(true);
            return pooled;
        }

        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(sphere.GetComponent<Collider>());
        sphere.hideFlags = HideFlags.DontSave;

        Renderer renderer = sphere.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = sphereMaterial;
        }

        return sphere;
    }

    private void RecycleSphere(GameObject sphere)
    {
        if (sphere == null) return;

        if (spherePool.Count < PoolSize)
        {
            sphere.SetActive(false);
            spherePool.Enqueue(sphere);
            return;
        }

        Destroy(sphere);
    }

    private void ClearRuntimeRenderers()
    {
        if (rangeRenderer != null) rangeRenderer.positionCount = 0;
        if (rayRenderer != null) rayRenderer.positionCount = 0;
    }

    private Color GetDetectionColor(SmallNodeType type)
    {
        Color baseColor = type == SmallNodeType.ResourcePoint ? resourceColor : obstacleColor;
        return ToHighContrast(baseColor);
    }

    private float GetEffectiveLineWidth()
    {
        float baseWidth = Mathf.Max(0.01f, lineWidth);
        return Mathf.Max(0.06f, baseWidth * Mathf.Max(1f, lineWidthBoost));
    }

    private Vector2 GetEffectiveSphereSizeRange()
    {
        Vector2 safe = sphereSizeRange;
        if (safe.y < safe.x) safe.y = safe.x;
        float boost = Mathf.Max(1f, markerSizeBoost);
        safe.x = Mathf.Max(0.12f, safe.x * boost);
        safe.y = Mathf.Max(safe.x, safe.y * boost);
        return safe;
    }

    private static Color ToHighContrast(Color color)
    {
        Color.RGBToHSV(color, out float h, out float s, out float v);
        s = Mathf.Max(0.72f, s);
        v = Mathf.Max(0.95f, v);
        Color output = Color.HSVToRGB(h, s, v);
        output.a = color.a;
        return output;
    }

    private Color GetRangeVizColor(bool fill)
    {
        Color color = fill ? rangeFillColor : rangeColor;
        color = ToHighContrast(color);
        color.a = fill ? Mathf.Max(0.20f, color.a) : Mathf.Max(0.90f, color.a);
        return color;
    }

    private void DrawCircleWithLineRenderer(Vector3 center, float radius, Color color, int segments)
    {
        rangeRenderer.positionCount = segments + 1;
        rangeRenderer.startColor = color;
        rangeRenderer.endColor = color;

        for (int i = 0; i <= segments; i++)
        {
            float angle = i * 2f * Mathf.PI / segments;
            Vector3 pos = center + new Vector3(Mathf.Sin(angle) * radius, 0f, Mathf.Cos(angle) * radius);
            rangeRenderer.SetPosition(i, pos);
        }
    }

    private void DrawSectorWithLineRenderer(Vector3 center, Vector3 forward, float angle, float radius, Color color, int segments)
    {
        float halfAngle = angle / 2f;
        rangeRenderer.positionCount = segments + 2;
        rangeRenderer.startColor = color;
        rangeRenderer.endColor = color;
        rangeRenderer.SetPosition(0, center);

        for (int i = 0; i <= segments; i++)
        {
            float currentAngle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / segments);
            Vector3 dir = Quaternion.Euler(0f, currentAngle, 0f) * forward;
            Vector3 point = center + dir * radius;
            rangeRenderer.SetPosition(i + 1, point);
        }
    }

    private static void AddLineToRenderer(LineRenderer renderer, Vector3 start, Vector3 end, Color color, ref int index)
    {
        renderer.startColor = color;
        renderer.endColor = color;
        renderer.positionCount += 2;
        renderer.SetPosition(index++, start);
        renderer.SetPosition(index++, end);
    }

    private static void DrawArc(Vector3 center, Vector3 forward, float halfAngle, float radius)
    {
        int segments = 20;
        Vector3 prevPoint = center + forward * radius;

        for (int i = 1; i <= segments; i++)
        {
            float angle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / segments);
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * forward;
            Vector3 point = center + dir * radius;
            Gizmos.DrawLine(prevPoint, point);
            prevPoint = point;
        }
    }
}
