using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 校园小节点刷子（按 CampusFeatureKind 区域刷点）：
/// 1) 每条规则可指定要刷的 SmallNodeType。
/// 2) 每条规则可指定“在哪些地图要素区域内刷”（CampusFeatureKind 列表）。
/// 3) 生成节点命名为 Type_序号，并放到同类目录下（Map/Type/Type_1）。
/// </summary>
public class CampusSmallNodeSpawner : MonoBehaviour
{
    [Serializable]
    public class SpawnRule
    {
        [Header("节点类型与数量")]
        public SmallNodeType nodeType = SmallNodeType.Tree;
        [Min(0)] public int count = 20;

        [Header("区域分布（按地图要素类型）")]
        [Tooltip("仅在这些 CampusFeatureKind 区域内刷点；为空时在全图范围随机。")]
        public List<CampusJsonMapLoader.CampusFeatureKind> spawnInFeatureKinds = new List<CampusJsonMapLoader.CampusFeatureKind>();
        [Range(0f, 1f)] public float cellJitter01 = 0.75f;
        [Min(0f)] public float minSpacingM = 0.8f;
        [Min(-20f)] public float yOffset = 0f;

        [Header("生成对象")]
        public GameObject prefab;
        public PrimitiveType fallbackPrimitive = PrimitiveType.Sphere;
        public bool overrideScale = true;
        public Vector3 localScale = Vector3.one;
        public Color tintColor = new Color32(200, 230, 200, 255);

        [Header("动态行为")]
        public bool inferDynamicFromType = true;
        public bool isDynamic = false;
        [Min(0f)] public float moveSpeed = 1.6f;
        [Min(0.1f)] public float retargetInterval = 2f;

        [Header("语义属性")]
        public bool blocksMovement = true;
    }

    [Header("依赖引用")]
    public CampusJsonMapLoader campusLoader;
    public CampusGrid2D campusGrid;

    [Header("运行控制")]
    public bool autoSpawnOnStart = false;
    public bool clearBeforeSpawn = true;
    public bool logSummary = true;
    public int randomSeed = 1337;
    [Min(8)] public int maxSampleAttemptsPerNode = 200;
    public bool autoBuildCampusGrid = true;

    [Header("分层设置")]
    public bool enableTypeLayering = true;
    [Tooltip("是否把层级递归应用到子物体（推荐开启，避免命中子 Collider 时分层失效）")]
    public bool applyLayerRecursively = true;
    public string obstacleLayerName = "Obstacle";
    public string resourceLayerName = "Resource";
    [Range(0, 31)] public int fallbackLayerWhenMissing = 0;

    [Header("全图兜底区域（当规则未指定要素或网格不可用时）")]
    public bool fallbackToLoaderGroundBounds = true;
    public Vector2 fallbackRegionCenterXZ = Vector2.zero;
    [Min(1f)] public float fallbackRegionWidthM = 120f;
    [Min(1f)] public float fallbackRegionLengthM = 120f;

    [Header("规则配置")]
    public List<SpawnRule> rules = new List<SpawnRule>
    {
        new SpawnRule
        {
            nodeType = SmallNodeType.Tree,
            count = 300,
            spawnInFeatureKinds = new List<CampusJsonMapLoader.CampusFeatureKind>
            {
                CampusJsonMapLoader.CampusFeatureKind.Forest,
                CampusJsonMapLoader.CampusFeatureKind.Green
            },
            fallbackPrimitive = PrimitiveType.Sphere,
            localScale = new Vector3(1.2f, 3.0f, 1.2f),
            tintColor = new Color32(185, 225, 205, 255),
            minSpacingM = 1.0f,
            blocksMovement = true
        },
        new SpawnRule
        {
            nodeType = SmallNodeType.Pedestrian,
            count = 40,
            spawnInFeatureKinds = new List<CampusJsonMapLoader.CampusFeatureKind>
            {
                CampusJsonMapLoader.CampusFeatureKind.Road,
                CampusJsonMapLoader.CampusFeatureKind.Green
            },
            fallbackPrimitive = PrimitiveType.Capsule,
            localScale = new Vector3(0.55f, 0.55f, 1.1f),
            tintColor = new Color32(245, 220, 170, 255),
            minSpacingM = 1.0f,
            blocksMovement = true,
            moveSpeed = 1.6f,
            retargetInterval = 2.0f
        },
        new SpawnRule
        {
            nodeType = SmallNodeType.ResourcePoint,
            count = 20,
            spawnInFeatureKinds = new List<CampusJsonMapLoader.CampusFeatureKind>
            {
                CampusJsonMapLoader.CampusFeatureKind.Parking,
                CampusJsonMapLoader.CampusFeatureKind.Green,
                CampusJsonMapLoader.CampusFeatureKind.Other
            },
            fallbackPrimitive = PrimitiveType.Cube,
            localScale = new Vector3(0.7f, 0.7f, 0.7f),
            tintColor = new Color32(255, 214, 120, 255),
            minSpacingM = 1.2f,
            blocksMovement = false
        }
    };

    private readonly List<GameObject> spawnedNodes = new List<GameObject>();
    private readonly Dictionary<SmallNodeType, Transform> typeRoots = new Dictionary<SmallNodeType, Transform>();
    private readonly Dictionary<SmallNodeType, int> typeCounters = new Dictionary<SmallNodeType, int>();
    private int obstacleLayerIndex = -1;
    private int resourceLayerIndex = -1;

    private void Start()
    {
        if (autoSpawnOnStart)
        {
            SpawnNodes();
        }
    }

    [ContextMenu("Spawn Small Nodes")]
    public void SpawnNodes()
    {
        ResolveReferences();
        if (clearBeforeSpawn) ClearNodes();

        Rect fallbackRegion = ResolveFallbackRegion();
        var rng = new System.Random(randomSeed);
        var globallyPlaced = new List<Vector2>(512);

        int total = 0;
        for (int r = 0; r < rules.Count; r++)
        {
            SpawnRule rule = rules[r];
            if (rule == null || rule.count <= 0) continue;

            bool isDynamic = rule.inferDynamicFromType ? IsDynamicType(rule.nodeType) : rule.isDynamic;
            List<Vector2Int> candidateCells = ResolveCandidateCells(rule);
            Rect moveBounds = ResolveMoveBoundsForRule(rule, candidateCells, fallbackRegion);

            var placed = new List<Vector2>(rule.count);
            int spawnedRuleCount = 0;

            for (int i = 0; i < rule.count; i++)
            {
                if (!TrySamplePosition(rule, candidateCells, moveBounds, placed, globallyPlaced, rng, out Vector3 spawnPos))
                    continue;

                GameObject obj = CreateNodeObject(rule);
                if (obj == null) continue;

                Transform typeRoot = EnsureTypeRoot(rule.nodeType);
                int index = NextTypeIndex(rule.nodeType);
                string typeLabel = GetTypeLabel(rule.nodeType);

                obj.name = $"{typeLabel}_{index}";
                obj.transform.SetParent(typeRoot, true);

                if (rule.overrideScale) obj.transform.localScale = rule.localScale;

                // 计算落地高度
                float y = ResolveGroundY() + rule.yOffset;
                Collider col = obj.GetComponent<Collider>();
                if (col != null) y += Mathf.Max(0f, col.bounds.extents.y);
                obj.transform.position = new Vector3(spawnPos.x, y, spawnPos.z);

                ApplyNodeAppearance(obj, rule.tintColor);
                ApplyNodeTagAndLayer(obj, rule);
                AttachRuntimeNodeInfo(obj, rule, isDynamic, index);
                AttachDynamicMoverIfNeeded(obj, isDynamic, moveBounds, rule, randomSeed + r * 100003 + i * 97);

                placed.Add(new Vector2(obj.transform.position.x, obj.transform.position.z));
                globallyPlaced.Add(new Vector2(obj.transform.position.x, obj.transform.position.z));
                spawnedNodes.Add(obj);
                spawnedRuleCount++;
                total++;
            }

            if (logSummary)
            {
                string kinds = (rule.spawnInFeatureKinds == null || rule.spawnInFeatureKinds.Count == 0)
                    ? "全图"
                    : string.Join(",", rule.spawnInFeatureKinds);
                Debug.Log($"[CampusSmallNodeSpawner] {rule.nodeType} 生成 {spawnedRuleCount}/{rule.count}，区域={kinds}");
            }
        }

        if (logSummary)
        {
            Debug.Log($"[CampusSmallNodeSpawner] 完成，总生成节点数: {total}");
        }
    }

    [ContextMenu("Clear Small Nodes")]
    public void ClearNodes()
    {
        for (int i = 0; i < spawnedNodes.Count; i++)
        {
            if (spawnedNodes[i] != null) DestroySafe(spawnedNodes[i]);
        }
        spawnedNodes.Clear();

        foreach (var kv in typeRoots)
        {
            if (kv.Value != null) DestroySafe(kv.Value.gameObject);
        }
        typeRoots.Clear();
        typeCounters.Clear();
    }

    private void ResolveReferences()
    {
        if (campusLoader == null) campusLoader = FindObjectOfType<CampusJsonMapLoader>();
        if (campusGrid == null && campusLoader != null) campusGrid = campusLoader.GetComponent<CampusGrid2D>();
        if (campusGrid == null) campusGrid = FindObjectOfType<CampusGrid2D>();
        ResolveLayerIndices();

        if (campusGrid != null && autoBuildCampusGrid && (campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null))
        {
            campusGrid.BuildGridFromCampusJson();
        }
    }

    private Rect ResolveFallbackRegion()
    {
        if (fallbackToLoaderGroundBounds && campusLoader != null)
        {
            float w = Mathf.Max(1f, campusLoader.groundWidthM);
            float h = Mathf.Max(1f, campusLoader.groundLengthM);
            Vector3 c = campusLoader.transform.position;
            return new Rect(c.x - w * 0.5f, c.z - h * 0.5f, w, h);
        }

        return new Rect(
            fallbackRegionCenterXZ.x - fallbackRegionWidthM * 0.5f,
            fallbackRegionCenterXZ.y - fallbackRegionLengthM * 0.5f,
            Mathf.Max(1f, fallbackRegionWidthM),
            Mathf.Max(1f, fallbackRegionLengthM)
        );
    }

    private List<Vector2Int> ResolveCandidateCells(SpawnRule rule)
    {
        var cells = new List<Vector2Int>();
        if (campusGrid == null || campusGrid.cellTypeGrid == null) return cells;

        bool requireKinds = rule.spawnInFeatureKinds != null && rule.spawnInFeatureKinds.Count > 0;
        for (int x = 0; x < campusGrid.gridWidth; x++)
        {
            for (int z = 0; z < campusGrid.gridLength; z++)
            {
                CampusJsonMapLoader.CampusFeatureKind kind = GridCellToFeatureKind(campusGrid.GetCellType(x, z));
                if (requireKinds && !rule.spawnInFeatureKinds.Contains(kind)) continue;
                cells.Add(new Vector2Int(x, z));
            }
        }

        return cells;
    }

    private Rect ResolveMoveBoundsForRule(SpawnRule rule, List<Vector2Int> candidateCells, Rect fallbackRegion)
    {
        if (campusGrid == null || candidateCells == null || candidateCells.Count == 0) return fallbackRegion;

        float minX = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        for (int i = 0; i < candidateCells.Count; i++)
        {
            Vector2Int c = candidateCells[i];
            Vector3 w = campusGrid.GridToWorldCenter(c.x, c.y, 0f);
            minX = Mathf.Min(minX, w.x);
            minZ = Mathf.Min(minZ, w.z);
            maxX = Mathf.Max(maxX, w.x);
            maxZ = Mathf.Max(maxZ, w.z);
        }

        float pad = Mathf.Max(0.2f, campusGrid.cellSize * 0.5f);
        return Rect.MinMaxRect(minX - pad, minZ - pad, maxX + pad, maxZ + pad);
    }

    private bool TrySamplePosition(
        SpawnRule rule,
        List<Vector2Int> candidateCells,
        Rect fallbackRegion,
        List<Vector2> placed,
        List<Vector2> globallyPlaced,
        System.Random rng,
        out Vector3 outPos
    )
    {
        outPos = Vector3.zero;
        float spacingSqr = Mathf.Max(0f, rule.minSpacingM) * Mathf.Max(0f, rule.minSpacingM);
        int attempts = Mathf.Max(8, maxSampleAttemptsPerNode);

        for (int t = 0; t < attempts; t++)
        {
            Vector3 p;
            if (campusGrid != null && candidateCells != null && candidateCells.Count > 0)
            {
                Vector2Int cell = candidateCells[rng.Next(0, candidateCells.Count)];
                p = campusGrid.GridToWorldCenter(cell.x, cell.y, 0f);

                float jitter = Mathf.Clamp01(rule.cellJitter01) * 0.5f * Mathf.Max(0.01f, campusGrid.cellSize);
                p.x += Mathf.Lerp(-jitter, jitter, (float)rng.NextDouble());
                p.z += Mathf.Lerp(-jitter, jitter, (float)rng.NextDouble());
            }
            else
            {
                if (fallbackRegion.width <= 0f || fallbackRegion.height <= 0f) return false;
                p = new Vector3(
                    Mathf.Lerp(fallbackRegion.xMin, fallbackRegion.xMax, (float)rng.NextDouble()),
                    0f,
                    Mathf.Lerp(fallbackRegion.yMin, fallbackRegion.yMax, (float)rng.NextDouble())
                );
            }

            Vector2 p2 = new Vector2(p.x, p.z);
            bool tooNear = IsTooNear(p2, placed, spacingSqr);
            if (!tooNear)
            {
                tooNear = IsTooNear(p2, globallyPlaced, spacingSqr);
            }
            if (tooNear) continue;

            outPos = p;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断目标点是否与已有点过近（平方距离比较，避免开方）。
    /// </summary>
    private static bool IsTooNear(Vector2 p, List<Vector2> existing, float spacingSqr)
    {
        if (existing == null || existing.Count == 0 || spacingSqr <= 0f) return false;
        for (int i = 0; i < existing.Count; i++)
        {
            if ((existing[i] - p).sqrMagnitude < spacingSqr) return true;
        }
        return false;
    }

    private GameObject CreateNodeObject(SpawnRule rule)
    {
        if (rule.prefab != null) return Instantiate(rule.prefab);
        return GameObject.CreatePrimitive(rule.fallbackPrimitive);
    }

    private void ApplyNodeAppearance(GameObject nodeObj, Color color)
    {
        Renderer[] renderers = nodeObj.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || r.sharedMaterial == null) continue;

            Material mat = new Material(r.sharedMaterial);
            mat.color = color;
            r.material = mat;
        }
    }

    private void ApplyNodeTagAndLayer(GameObject nodeObj, SpawnRule rule)
    {
        string tag = GetRecommendedTag(rule.nodeType);
        if (!string.IsNullOrEmpty(tag)) TryAssignTag(nodeObj, tag);

        int layer = GetRecommendedLayer(rule.nodeType);
        if (layer >= 0)
        {
            nodeObj.layer = layer;
            if (applyLayerRecursively)
            {
                ApplyLayerRecursively(nodeObj.transform, layer);
            }
        }
    }

    private void AttachRuntimeNodeInfo(GameObject nodeObj, SpawnRule rule, bool isDynamic, int serialIndex)
    {
        SmallNodeRuntimeInfo info = nodeObj.GetComponent<SmallNodeRuntimeInfo>();
        if (info == null) info = nodeObj.AddComponent<SmallNodeRuntimeInfo>();

        info.nodeType = rule.nodeType;
        info.isDynamic = isDynamic;
        info.blocksMovement = rule.nodeType != SmallNodeType.ResourcePoint;
        info.serialIndex = serialIndex;
        info.displayName = nodeObj.name;
        info.sourceSpawner = this;
    }

    private void AttachDynamicMoverIfNeeded(GameObject nodeObj, bool isDynamic, Rect moveBounds, SpawnRule rule, int seed)
    {
        if (!isDynamic) return;

        CampusSmallNodeWanderMover mover = nodeObj.GetComponent<CampusSmallNodeWanderMover>();
        if (mover == null) mover = nodeObj.AddComponent<CampusSmallNodeWanderMover>();

        float y = ResolveGroundY() + rule.yOffset + Mathf.Max(0f, nodeObj.transform.localScale.y * 0.5f);
        mover.Init(seed, moveBounds, y, rule.moveSpeed, rule.retargetInterval);
    }

    private float ResolveGroundY()
    {
        if (campusLoader != null) return campusLoader.groundZ;
        return transform.position.y;
    }

    private Transform EnsureTypeRoot(SmallNodeType nodeType)
    {
        if (typeRoots.TryGetValue(nodeType, out Transform existing) && existing != null)
        {
            return existing;
        }

        string folderName = $"Map/{GetTypeLabel(nodeType)}";
        GameObject go = new GameObject(folderName);
        go.transform.SetParent(transform, false);
        typeRoots[nodeType] = go.transform;
        return go.transform;
    }

    private int NextTypeIndex(SmallNodeType nodeType)
    {
        if (!typeCounters.TryGetValue(nodeType, out int idx))
        {
            idx = 0;
        }
        idx++;
        typeCounters[nodeType] = idx;
        return idx;
    }

    private static string GetTypeLabel(SmallNodeType nodeType)
    {
        switch (nodeType)
        {
            case SmallNodeType.ResourcePoint: return "ResourcePoint";
            case SmallNodeType.TemporaryObstacle: return "TemporaryObstacle";
            default: return nodeType.ToString();
        }
    }

    private static CampusJsonMapLoader.CampusFeatureKind GridCellToFeatureKind(CampusGrid2D.CellType t)
    {
        switch (t)
        {
            case CampusGrid2D.CellType.Building: return CampusJsonMapLoader.CampusFeatureKind.Building;
            case CampusGrid2D.CellType.Sports: return CampusJsonMapLoader.CampusFeatureKind.Sports;
            case CampusGrid2D.CellType.Water: return CampusJsonMapLoader.CampusFeatureKind.Water;
            case CampusGrid2D.CellType.Road: return CampusJsonMapLoader.CampusFeatureKind.Road;
            case CampusGrid2D.CellType.Expressway: return CampusJsonMapLoader.CampusFeatureKind.Expressway;
            case CampusGrid2D.CellType.Bridge: return CampusJsonMapLoader.CampusFeatureKind.Bridge;
            case CampusGrid2D.CellType.Parking: return CampusJsonMapLoader.CampusFeatureKind.Parking;
            case CampusGrid2D.CellType.Green: return CampusJsonMapLoader.CampusFeatureKind.Green;
            case CampusGrid2D.CellType.Forest: return CampusJsonMapLoader.CampusFeatureKind.Forest;
            default: return CampusJsonMapLoader.CampusFeatureKind.Other;
        }
    }

    private static string GetRecommendedTag(SmallNodeType nodeType)
    {
        switch (nodeType)
        {
            case SmallNodeType.ResourcePoint: return "Resource";
            case SmallNodeType.Agent: return "Agent";
            case SmallNodeType.Tree: return "Tree";
            case SmallNodeType.Pedestrian: return "Pedestrian";
            case SmallNodeType.Vehicle: return "Vehicle";
            default: return "";
        }
    }

    private int GetRecommendedLayer(SmallNodeType nodeType)
    {
        if (!enableTypeLayering)
        {
            return Mathf.Clamp(fallbackLayerWhenMissing, 0, 31);
        }

        // 除资源点外，其余小节点都按障碍物层处理
        if (nodeType == SmallNodeType.ResourcePoint)
        {
            if (resourceLayerIndex >= 0) return resourceLayerIndex;
            if (obstacleLayerIndex >= 0) return obstacleLayerIndex;
            return Mathf.Clamp(fallbackLayerWhenMissing, 0, 31);
        }

        if (obstacleLayerIndex >= 0) return obstacleLayerIndex;
        return Mathf.Clamp(fallbackLayerWhenMissing, 0, 31);
    }

    private static bool IsDynamicType(SmallNodeType nodeType)
    {
        return nodeType == SmallNodeType.Pedestrian ||
               nodeType == SmallNodeType.Vehicle ||
               nodeType == SmallNodeType.Agent;
    }

    private static void TryAssignTag(GameObject go, string tag)
    {
        try
        {
            go.tag = tag;
        }
        catch (UnityException)
        {
            // 标签不存在时忽略。
        }
    }

    private static void DestroySafe(UnityEngine.Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }

    /// <summary>
    /// 递归设置层级，确保预制体子节点与父节点保持一致分层。
    /// </summary>
    private static void ApplyLayerRecursively(Transform root, int layer)
    {
        if (root == null) return;
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
        {
            ApplyLayerRecursively(root.GetChild(i), layer);
        }
    }

    private void ResolveLayerIndices()
    {
        obstacleLayerIndex = LayerMask.NameToLayer(obstacleLayerName);
        resourceLayerIndex = LayerMask.NameToLayer(resourceLayerName);

        if (obstacleLayerIndex < 0) obstacleLayerIndex = Mathf.Clamp(fallbackLayerWhenMissing, 0, 31);
        if (resourceLayerIndex < 0) resourceLayerIndex = obstacleLayerIndex;
    }
}

/// <summary>
/// 动态小节点随机游走器：
/// 在给定 XZ 区域内随机选目标点，以恒速移动并定时换目标。
/// </summary>
public class CampusSmallNodeWanderMover : MonoBehaviour
{
    private System.Random rng;
    private Rect moveBoundsXY;
    private float fixedY;
    private float speed;
    private float retargetInterval;

    private Vector3 target;
    private float timer;

    public void Init(int seed, Rect boundsXY, float y, float moveSpeed, float retargetSec)
    {
        rng = new System.Random(seed);
        moveBoundsXY = boundsXY;
        fixedY = y;
        speed = Mathf.Max(0f, moveSpeed);
        retargetInterval = Mathf.Max(0.1f, retargetSec);
        timer = 0f;
        PickNewTarget(true);
    }

    private void Update()
    {
        if (rng == null) return;

        timer += Time.deltaTime;
        if (timer >= retargetInterval)
        {
            timer = 0f;
            PickNewTarget(false);
        }

        Vector3 pos = transform.position;
        pos.y = fixedY;
        transform.position = pos;

        Vector3 flatPos = new Vector3(pos.x, 0f, pos.z);
        Vector3 flatTar = new Vector3(target.x, 0f, target.z);

        Vector3 dir = flatTar - flatPos;
        float dist = dir.magnitude;
        if (dist < 0.08f || speed <= 0f) return;

        dir /= dist;
        float step = Mathf.Min(speed * Time.deltaTime, dist);
        Vector3 next = flatPos + dir * step;
        transform.position = new Vector3(next.x, fixedY, next.z);

        if (dir.sqrMagnitude > 1e-6f)
        {
            Quaternion rot = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z), Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, rot, 10f * Time.deltaTime);
        }
    }

    private void PickNewTarget(bool allowNear)
    {
        float x = Mathf.Lerp(moveBoundsXY.xMin, moveBoundsXY.xMax, (float)rng.NextDouble());
        float z = Mathf.Lerp(moveBoundsXY.yMin, moveBoundsXY.yMax, (float)rng.NextDouble());
        target = new Vector3(x, fixedY, z);

        if (!allowNear)
        {
            Vector3 p = transform.position;
            Vector2 d = new Vector2(target.x - p.x, target.z - p.z);
            if (d.sqrMagnitude < 1.0f)
            {
                x = Mathf.Lerp(moveBoundsXY.xMin, moveBoundsXY.xMax, (float)rng.NextDouble());
                z = Mathf.Lerp(moveBoundsXY.yMin, moveBoundsXY.yMax, (float)rng.NextDouble());
                target = new Vector3(x, fixedY, z);
            }
        }
    }
}
