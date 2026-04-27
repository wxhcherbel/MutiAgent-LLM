using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json.Linq;


[ExecuteAlways]
public class CampusGrid2D : MonoBehaviour
{
    [Header("数据源")]
    public CampusJsonMapLoader campusLoader;

    [Header("网格参数")]
    [Min(0.2f)] public float cellSize = 2f;
    [Min(0f)] public float mapMargin = 2f;
    public bool autoBuildOnStart = true;
    public bool allowDiagonal = false;
    public bool smoothAStarPath = false;

    [Header("通行规则")]
    public bool buildingBlocked = true;
    public bool waterBlocked = true;
    public bool sportsBlocked = false;
    public bool forestBlocked = false;
    public bool roadBlocked = false;
    public bool expresswayBlocked = false;
    public bool bridgeBlocked = false;
    public bool parkingBlocked = false;
    public bool otherBlocked = false;

    [Header("建筑安全边距")]
    [Tooltip("路径规划和靠近目标时与 Building 保持的最小距离，避免智能体被引导到贴墙位置。")]
    [Min(0f)] public float buildingPathClearance = 2.4f;

    [Header("可视化")]
    public bool showGrid = true;
    public bool showAllCells = false;
    [Tooltip("仅显示可通行格子（showAllCells=false 时生效）")]
    public bool showWalkableOnly = false;
    [Range(0.1f, 1f)] public float cellVisualScale = 0.95f;
    public float gridVisualY = 0.05f;
    public Material gridMaterial;
    public Color freeColor = new Color(0.55f, 0.85f, 0.55f, 0.35f);
    public Color blockedColor = new Color(0.90f, 0.30f, 0.30f, 0.55f);
    public Color roadColor = new Color(0.75f, 0.78f, 0.85f, 0.45f);
    public Color waterColor = new Color(0.35f, 0.60f, 0.95f, 0.50f);
    public Color buildingColor = new Color(0.62f, 0.48f, 0.70f, 0.60f);

    [Header("调试")]
    public bool logBuildSummary = true;

    [Header("点击查询（运行时）")]
    public bool enableClickQuery = true;
    public Camera clickQueryCamera;
    public LayerMask clickLayerMask = ~0;
    [Min(1f)] public float clickMaxDistance = 2000f;
    public bool logClickQuery = true;
    public bool showClickQueryOnScreen = true;
    [Min(0.1f)] public float clickInfoDuration = 6f;

    [NonSerialized] public int gridWidth;
    [NonSerialized] public int gridLength;
    [NonSerialized] public bool[,] blockedGrid;
    [NonSerialized] public CampusGridCellType[,] cellTypeGrid;
    [NonSerialized] public string[,] cellFeatureUidGrid;
    [NonSerialized] public string[,] cellFeatureNameGrid;
    [NonSerialized] public Dictionary<string, Vector2Int> featureAliasCellMap;
    [NonSerialized] public Dictionary<string, string> featureAliasUidMap;
    [NonSerialized] public Dictionary<string, string> featureAliasNameMap;
    [NonSerialized] public Rect mapBoundsXY;
    [NonSerialized] public Dictionary<string, FeatureSpatialProfile> featureSpatialProfileByUid;
    [NonSerialized] public Dictionary<string, string[]> featureCollectionMembers;

    private Dictionary<string, FeatureSpatialIndex> featureSpatialIndexByUid;
    private Dictionary<string, List<string>> featureUidsByName;
    private Dictionary<string, List<string>> featureUidsByCollectionKey;
    private bool[,] pathClearanceBlockedGrid;

    private float[,]      _astarGScore;
    private bool[,]       _astarClosed;
    private Vector2Int[,] _astarCameFrom;
    private bool[,]       _astarHasParent;

    private Transform visualRoot;
    private readonly List<GameObject> visualCells = new List<GameObject>();
    private Material runtimeDefaultMat;
    private string lastClickInfo = "";
    private float lastClickInfoTime = -999f;
    private bool warnedNoClickCamera;

    public float GroundY => (campusLoader != null) ? campusLoader.groundZ : 0f;

    private void Start()
    {
        if (autoBuildOnStart)
        {
            BuildGridFromCampusJson();
        }
    }

    private void Update()
    {
        if (!Application.isPlaying) return;
        if (!enableClickQuery) return;
        if (!Input.GetMouseButtonDown(0)) return;

        HandleRuntimeClickQuery();
    }

    private void OnGUI()
    {
        if (!Application.isPlaying) return;
        if (!enableClickQuery || !showClickQueryOnScreen) return;
        if (string.IsNullOrEmpty(lastClickInfo)) return;
        if (Time.time - lastClickInfoTime > clickInfoDuration) return;

        GUI.Box(new Rect(12f, 12f, Mathf.Min(Screen.width - 24f, 900f), 52f), lastClickInfo);
    }

    [ContextMenu("Build Grid From Campus JSON")]
    public void BuildGridFromCampusJson()
    {
        if (campusLoader == null) campusLoader = GetComponent<CampusJsonMapLoader>();
        if (campusLoader == null)
        {
            Debug.LogError("[CampusGrid2D] 未找到 CampusJsonMapLoader。");
            return;
        }

        if (!TryLoadJsonFromCampusLoader(out string json))
        {
            Debug.LogError("[CampusGrid2D] 读取 JSON 失败，请检查 CampusJsonMapLoader 的 jsonText/jsonFilePath。");
            return;
        }

        if (!ParseCampusJson(json, out List<Feature2D> features, out Rect allBounds))
        {
            Debug.LogError("[CampusGrid2D] JSON 解析失败，未找到 features。");
            return;
        }

        ApplyHorizontalScaleToFeatures(features, ref allBounds, GetHorizontalMapScale());
        BuildLogicalGrid(features, allBounds);

        if (showGrid) RebuildVisualization();
        else ClearVisualizationOnly();
    }

    [ContextMenu("Rebuild Grid Visualization")]
    public void RebuildVisualization()
    {
        if (blockedGrid == null || cellTypeGrid == null)
        {
            Debug.LogWarning("[CampusGrid2D] 还没有逻辑网格，请先执行 Build Grid。");
            return;
        }

        ClearVisualizationOnly();

        var rootGo = new GameObject("Grid2D_Visualization");
        rootGo.transform.SetParent(transform, false);
        visualRoot = rootGo.transform;

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                bool isBlocked = blockedGrid[x, z];
                if (!showAllCells)
                {
                    if (showWalkableOnly)
                    {
                        if (isBlocked) continue;
                    }
                    else
                    {
                        if (!isBlocked) continue;
                    }
                }
                CreateOrUpdateCellVisual(x, z);
            }
        }

        visualRoot.gameObject.SetActive(showGrid);
    }

    [ContextMenu("Clear Grid Visualization")]
    public void ClearVisualizationOnly()
    {
        if (visualCells.Count > 0)
        {
            for (int i = 0; i < visualCells.Count; i++)
            {
                if (visualCells[i] != null)
                {
                    DestroyImmediateSafe(visualCells[i]);
                }
            }
            visualCells.Clear();
        }

        if (visualRoot != null)
        {
            DestroyImmediateSafe(visualRoot.gameObject);
            visualRoot = null;
        }
    }

    public bool IsInBounds(int x, int z)
    {
        return x >= 0 && x < gridWidth && z >= 0 && z < gridLength;
    }

    public bool IsWalkable(int x, int z)
    {
        return IsInBounds(x, z) && !blockedGrid[x, z];
    }

    public CampusGridCellType GetCellType(int x, int z)
    {
        if (!IsInBounds(x, z)) return CampusGridCellType.Other;
        return cellTypeGrid[x, z];
    }

    public bool TryGetCellFeatureInfo(int x, int z, out string uid, out string name, out CampusGridCellType type, out bool blocked)
    {
        uid = string.Empty;
        name = string.Empty;
        type = CampusGridCellType.Other;
        blocked = false;

        if (!IsInBounds(x, z) || blockedGrid == null || cellTypeGrid == null) return false;

        type = cellTypeGrid[x, z];
        blocked = blockedGrid[x, z];
        if (cellFeatureUidGrid != null) uid = cellFeatureUidGrid[x, z] ?? string.Empty;
        if (cellFeatureNameGrid != null) name = cellFeatureNameGrid[x, z] ?? string.Empty;

        return true;
    }

    public bool TryGetCellFeatureInfoByWorld(Vector3 worldPos, out Vector2Int grid, out string uid, out string name, out CampusGridCellType type, out bool blocked)
    {
        grid = WorldToGrid(worldPos);
        if (!IsInBounds(grid.x, grid.y))
        {
            uid = string.Empty;
            name = string.Empty;
            type = CampusGridCellType.Other;
            blocked = false;
            return false;
        }

        return TryGetCellFeatureInfo(grid.x, grid.y, out uid, out name, out type, out blocked);
    }

    public bool TryResolveFeatureSpatialProfile(string query, Vector3 referenceWorld, out FeatureSpatialProfile profile, bool preferWalkableApproach = true, bool ignoreCase = true, Vector2Int? anchorBias = null)
    {
        profile = null;
        if (!TryResolveFeatureSpatialIndex(query, out FeatureSpatialIndex index, ignoreCase) || index == null)
        {
            return false;
        }

        FeatureSpatialProfile cloned = BuildSpatialProfileFromIndex(index);
        ResolveFeatureAnchor(cloned, referenceWorld, preferWalkableApproach, anchorBias);
        profile = cloned;
        return true;
    }

    public bool TryGetFeatureOccupiedCells(string query, out Vector2Int[] occupiedCells, bool ignoreCase = true)
    {
        occupiedCells = Array.Empty<Vector2Int>();
        if (!TryResolveFeatureSpatialIndex(query, out FeatureSpatialIndex index, ignoreCase) || index == null || index.occupiedCells.Count == 0)
        {
            return false;
        }

        occupiedCells = index.occupiedCells.ToArray();
        return occupiedCells.Length > 0;
    }



    public bool TryGetFeatureApproachCells(string query, Vector3 referenceWorld, out Vector2Int[] approachCells, int maxCount = 16, bool ignoreCase = true, Vector2Int? anchorBias = null)
    {
        approachCells = Array.Empty<Vector2Int>();
        if (!TryResolveFeatureSpatialIndex(query, out FeatureSpatialIndex index, ignoreCase) || index == null || index.occupiedCells.Count == 0)
        {
            return false;
        }

        FeatureSpatialProfile profile = BuildSpatialProfileFromIndex(index);
        Vector2Int referenceCell = WorldToGrid(referenceWorld);
        if (!IsInBounds(referenceCell.x, referenceCell.y))
        {
            referenceCell = profile.centroidCell;
        }

        approachCells = ComputeFeatureApproachCells(index, referenceCell, Mathf.Max(1, maxCount), anchorBias);
        return approachCells.Length > 0;
    }

    /// <summary>
    /// 根据查询字符串解析对应的要素空间索引（FeatureSpatialIndex）。
    /// 依次尝试三种匹配策略：
    ///   1. 直接以 UID 精确匹配 featureSpatialIndexByUid；
    ///   2. 通过别名表 featureAliasUidMap 将别名映射到 UID 后再查找；
    ///   3. 通过名称表 featureUidsByName 获取候选 UID 列表，取第一个匹配项。
    /// 查询前会先剥离结构化目标前缀（如 "target:"）。
    /// </summary>
    /// <param name="query">要素标识字符串，可以是 UID、别名或显示名称。</param>
    /// <param name="index">输出找到的空间索引；未找到时为 null。</param>
    /// <param name="ignoreCase">保留参数，当前实现中匹配逻辑依赖字典键的大小写规则。</param>
    /// <returns>成功解析到非空索引时返回 true，否则返回 false。</returns>
    private bool TryResolveFeatureSpatialIndex(string query, out FeatureSpatialIndex index, bool ignoreCase = true)
    {
        index = null;
        if (string.IsNullOrWhiteSpace(query) || featureSpatialIndexByUid == null || featureSpatialIndexByUid.Count == 0)
        {
            return false;
        }

        string q = query.Trim();
        // 剥离结构化前缀（如 "building:图书馆" → "图书馆"），保留纯查询词
        TryStripStructuredTargetPrefix(q, out _, out q);
        if (string.IsNullOrWhiteSpace(q)) return false;

        // 策略 1：直接 UID 精确查找
        if (featureSpatialIndexByUid.TryGetValue(q, out index) && index != null)
        {
            return true;
        }

        // 策略 2：别名 → UID → 索引
        if (featureAliasUidMap != null &&
            featureAliasUidMap.TryGetValue(q, out string aliasUid) &&
            !string.IsNullOrWhiteSpace(aliasUid) &&
            featureSpatialIndexByUid.TryGetValue(aliasUid.Trim(), out index) &&
            index != null)
        {
            return true;
        }

        // 策略 3：显示名称 → UID 列表 → 取首个有效索引
        if (featureUidsByName != null &&
            featureUidsByName.TryGetValue(q, out List<string> namedUids) &&
            namedUids != null &&
            namedUids.Count > 0 &&
            featureSpatialIndexByUid.TryGetValue(namedUids[0], out index) &&
            index != null)
        {
            return true;
        }

        return false;
    }

    private Vector2Int[] ComputeFeatureBoundaryCells(FeatureSpatialIndex index)
    {
        if (index == null || index.occupiedCells.Count == 0) return Array.Empty<Vector2Int>();

        List<Vector2Int> result = new List<Vector2Int>();
        for (int i = 0; i < index.occupiedCells.Count; i++)
        {
            Vector2Int c = index.occupiedCells[i];
            bool isBoundary = false;
            for (int dir = 0; dir < 4; dir++)
            {
                Vector2Int n;
                switch (dir)
                {
                    case 0:
                        n = new Vector2Int(c.x + 1, c.y);
                        break;
                    case 1:
                        n = new Vector2Int(c.x - 1, c.y);
                        break;
                    case 2:
                        n = new Vector2Int(c.x, c.y + 1);
                        break;
                    default:
                        n = new Vector2Int(c.x, c.y - 1);
                        break;
                }

                long key = (((long)n.x) << 32) ^ (uint)n.y;
                if (!IsInBounds(n.x, n.y) || !index.occupiedCellKeys.Contains(key))
                {
                    isBoundary = true;
                    break;
                }
            }

            if (isBoundary)
            {
                result.Add(c);
            }
        }

        return result.ToArray();
    }


    private Vector2Int[] ComputeFeatureApproachCells(FeatureSpatialIndex index, Vector2Int referenceCell, int maxCount, Vector2Int? anchorBias = null)
    {
        if (index == null || index.occupiedCells.Count == 0) return Array.Empty<Vector2Int>();

        HashSet<long> seen = new HashSet<long>();
        List<Vector2Int> candidates = new List<Vector2Int>();
        Vector2 center = new Vector2(
            (index.minX + index.maxX) * 0.5f,
            (index.minZ + index.maxZ) * 0.5f);

        Vector2Int[] boundary = ComputeFeatureBoundaryCells(index);
        for (int i = 0; i < boundary.Length; i++)
        {
            Vector2Int b = boundary[i];
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    if (dx == 0 && dz == 0) continue;

                    Vector2Int c = new Vector2Int(b.x + dx, b.y + dz);
                    long key = (((long)c.x) << 32) ^ (uint)c.y;
                    // 跳过建筑内部格（避免把occupied cell当接近格）
                    if (index.occupiedCellKeys.Contains(key)) continue;
                    if (!IsPathWalkable(c.x, c.y, null) || !seen.Add(key)) continue;

                    // 只保留真正位于实体外部的格，而不是跑到很远的位置。
                    Vector2 outward = new Vector2(c.x - center.x, c.y - center.y);
                    if (outward.sqrMagnitude < 0.25f) continue;
                    candidates.Add(c);
                }
            }
        }

        return candidates
            .OrderBy(c => (c - referenceCell).sqrMagnitude + ComputeAnchorBiasPenalty(c, center, anchorBias))
            .ThenBy(c => Mathf.Atan2(c.y - center.y, c.x - center.x))
            .Take(Mathf.Max(1, maxCount))
            .ToArray();
    }

    private FeatureSpatialProfile BuildSpatialProfileFromIndex(FeatureSpatialIndex index)
    {
        FeatureSpatialProfile profile = new FeatureSpatialProfile
        {
            uid = index.uid,
            name = index.name,
            runtimeAlias = index.runtimeAlias,
            kind = index.kind,
            collectionKey = index.collectionKey,
            cellType = index.cellType,
            occupiedCellCount = index.occupiedCells.Count,
            minX = index.minX == int.MaxValue ? -1 : index.minX,
            maxX = index.maxX == int.MinValue ? -1 : index.maxX,
            minZ = index.minZ == int.MaxValue ? -1 : index.minZ,
            maxZ = index.maxZ == int.MinValue ? -1 : index.maxZ
        };

        if (index.occupiedCells.Count == 0)
        {
            profile.centroidGrid = Vector2.zero;
            profile.centroidCell = new Vector2Int(-1, -1);
            profile.centroidWorld = transform.position;
            profile.anchorCell = new Vector2Int(-1, -1);
            profile.anchorWorld = transform.position;
            profile.footprintRadius = cellSize;
            return profile;
        }

        float sumX = 0f;
        float sumZ = 0f;
        for (int i = 0; i < index.occupiedCells.Count; i++)
        {
            Vector2Int c = index.occupiedCells[i];
            sumX += c.x;
            sumZ += c.y;
        }

        profile.centroidGrid = new Vector2(sumX / index.occupiedCells.Count, sumZ / index.occupiedCells.Count);
        profile.centroidCell = FindNearestCellToPoint(index.occupiedCells, profile.centroidGrid);
        profile.centroidWorld = GridToWorldCenter(profile.centroidCell.x, profile.centroidCell.y);
        profile.anchorCell = profile.centroidCell;
        profile.anchorWorld = profile.centroidWorld;

        float maxDistSq = 0f;
        for (int i = 0; i < index.occupiedCells.Count; i++)
        {
            Vector3 cellWorld = GridToWorldCenter(index.occupiedCells[i].x, index.occupiedCells[i].y);
            Vector3 delta = cellWorld - profile.centroidWorld;
            delta.y = 0f;
            float distSq = delta.sqrMagnitude;
            if (distSq > maxDistSq) maxDistSq = distSq;
        }

        profile.footprintRadius = Mathf.Sqrt(maxDistSq) + cellSize * 0.75f;
        return profile;
    }

    private static float ComputeAnchorBiasPenalty(Vector2Int candidate, Vector2 centroid, Vector2Int? anchorBias)
    {
        if (!anchorBias.HasValue) return 0f;
        Vector2 bias = anchorBias.Value;
        if (bias.sqrMagnitude < 0.01f)
        {
            return 0f;
        }

        Vector2 outward = new Vector2(candidate.x - centroid.x, candidate.y - centroid.y);
        if (outward.sqrMagnitude < 0.01f) return 0f;

        float alignment = Vector2.Dot(outward.normalized, bias.normalized);
        return (1f - alignment) * 32f;
    }

    private void ResolveFeatureAnchor(FeatureSpatialProfile profile, Vector3 referenceWorld, bool preferWalkableApproach, Vector2Int? anchorBias = null)
    {
        if (profile == null) return;

        if (!preferWalkableApproach || featureSpatialIndexByUid == null || string.IsNullOrWhiteSpace(profile.uid))
        {
            profile.anchorCell = profile.centroidCell;
            profile.anchorWorld = profile.centroidWorld;
            return;
        }

        if (!featureSpatialIndexByUid.TryGetValue(profile.uid, out FeatureSpatialIndex index) || index == null || index.occupiedCells.Count == 0)
        {
            profile.anchorCell = profile.centroidCell;
            profile.anchorWorld = profile.centroidWorld;
            return;
        }

        Vector2Int referenceCell = WorldToGrid(referenceWorld);
        if (!IsInBounds(referenceCell.x, referenceCell.y))
        {
            referenceCell = profile.centroidCell;
        }

        Vector2Int best = new Vector2Int(-1, -1);
        float bestDist = float.MaxValue;
        HashSet<long> visited = new HashSet<long>();

        for (int i = 0; i < index.occupiedCells.Count; i++)
        {
            Vector2Int occupied = index.occupiedCells[i];
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    Vector2Int candidate = new Vector2Int(occupied.x + dx, occupied.y + dz);
                    if (!IsPathWalkable(candidate.x, candidate.y, null)) continue;

                    long key = (((long)candidate.x) << 32) ^ (uint)candidate.y;
                    if (!visited.Add(key)) continue;

                    float sidePenalty = ComputeAnchorBiasPenalty(candidate, profile.centroidGrid, anchorBias);
                    float dist = (candidate - referenceCell).sqrMagnitude + sidePenalty;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = candidate;
                    }
                }
            }
        }

        if (best.x >= 0)
        {
            profile.anchorCell = best;
            profile.anchorWorld = GridToWorldCenter(best.x, best.y);
            return;
        }

        if (profile.centroidCell.x >= 0 &&
            profile.centroidCell.y >= 0 &&
            TryFindNearestWalkable(profile.centroidCell, 8, out Vector2Int nearest))
        {
            profile.anchorCell = nearest;
            profile.anchorWorld = GridToWorldCenter(nearest.x, nearest.y);
            return;
        }

        profile.anchorCell = profile.centroidCell;
        profile.anchorWorld = profile.centroidWorld;
    }

    private static Vector2Int FindNearestCellToPoint(List<Vector2Int> cells, Vector2 point)
    {
        if (cells == null || cells.Count == 0) return new Vector2Int(-1, -1);

        Vector2Int best = cells[0];
        float bestDist = float.MaxValue;
        for (int i = 0; i < cells.Count; i++)
        {
            Vector2Int c = cells[i];
            float dx = c.x - point.x;
            float dz = c.y - point.y;
            float dist = dx * dx + dz * dz;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = c;
            }
        }
        return best;
    }

    private static string SelectFeatureReferenceToken(string uid, string name, string runtimeAlias)
    {
        if (!string.IsNullOrWhiteSpace(runtimeAlias)) return runtimeAlias.Trim();
        if (!string.IsNullOrWhiteSpace(uid)) return uid.Trim();
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
    }

    private void HandleRuntimeClickQuery()
    {
        if (blockedGrid == null || cellTypeGrid == null)
        {
            SetAndMaybeLogClickInfo("[CampusGrid2D] 网格尚未构建，无法查询点击点信息。");
            return;
        }

        Camera cam = ResolveClickCamera();
        if (cam == null)
        {
            if (!warnedNoClickCamera)
            {
                warnedNoClickCamera = true;
                SetAndMaybeLogClickInfo("[CampusGrid2D] 未找到可用相机（clickQueryCamera/Camera.main），无法查询。");
            }
            return;
        }

        warnedNoClickCamera = false;
        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        if (TryGetQueryWorldPoint(ray, out Vector3 worldPoint, out string hitObjectName))
        {
            if (TryGetCellFeatureInfoByWorld(worldPoint, out Vector2Int grid, out string uid, out string name, out CampusGridCellType type, out bool blocked))
            {
                if (string.IsNullOrEmpty(name)) name = "(无名称)";
                if (string.IsNullOrEmpty(uid)) uid = "-";

                string msg = $"grid=({grid.x},{grid.y}) type={type} blocked={blocked} name={name} uid={uid}";
                if (!string.IsNullOrEmpty(hitObjectName)) msg += $" hit={hitObjectName}";
                SetAndMaybeLogClickInfo(msg);
            }
            else
            {
                string msg = $"点击点不在网格范围内 xz=({worldPoint.x:F2},{worldPoint.z:F2})";
                if (!string.IsNullOrEmpty(hitObjectName)) msg += $" hit={hitObjectName}";
                SetAndMaybeLogClickInfo(msg);
            }
            return;
        }

        SetAndMaybeLogClickInfo("[CampusGrid2D] 射线未命中碰撞体，且与地面平面无交点。");
    }

    private Camera ResolveClickCamera()
    {
        if (clickQueryCamera != null) return clickQueryCamera;
        return Camera.main;
    }

    private bool TryGetQueryWorldPoint(Ray ray, out Vector3 worldPoint, out string hitObjectName)
    {
        worldPoint = Vector3.zero;
        hitObjectName = "";

        if (Physics.Raycast(ray, out RaycastHit hit, clickMaxDistance, clickLayerMask, QueryTriggerInteraction.Ignore))
        {
            worldPoint = hit.point;
            if (hit.collider != null)
            {
                hitObjectName = hit.collider.gameObject.name;
            }
            return true;
        }

        Plane groundPlane = new Plane(Vector3.up, new Vector3(0f, GroundY, 0f));
        if (groundPlane.Raycast(ray, out float enter))
        {
            worldPoint = ray.GetPoint(enter);
            return true;
        }

        return false;
    }

    private void SetAndMaybeLogClickInfo(string info)
    {
        lastClickInfo = string.IsNullOrWhiteSpace(info) ? "" : info;
        lastClickInfoTime = Time.time;
        if (logClickQuery && !string.IsNullOrEmpty(lastClickInfo))
        {
            Debug.Log($"[CampusGrid2D] {lastClickInfo}");
        }
    }

    public Vector2Int WorldToGrid(Vector3 worldPos)
    {
        if (blockedGrid == null) return new Vector2Int(-1, -1);

        int gx = Mathf.FloorToInt((worldPos.x - mapBoundsXY.xMin) / cellSize);
        int gz = Mathf.FloorToInt((worldPos.z - mapBoundsXY.yMin) / cellSize);

        if (!IsInBounds(gx, gz)) return new Vector2Int(-1, -1);
        return new Vector2Int(gx, gz);
    }

    public Vector3 GridToWorldCenter(int x, int z, float yOffset = 0f)
    {
        float wx = mapBoundsXY.xMin + (x + 0.5f) * cellSize;
        float wz = mapBoundsXY.yMin + (z + 0.5f) * cellSize;
        return new Vector3(wx, GroundY + yOffset, wz);
    }

    public bool TryFindNearestWalkable(Vector2Int from, int maxRadius, out Vector2Int found)
    {
        return TryFindNearestWalkable(from, maxRadius, out found, null);
    }

    public bool TryFindNearestWalkable(Vector2Int from, int maxRadius, out Vector2Int found, HashSet<long> transientBlockedKeys)
    {
        found = new Vector2Int(-1, -1);
        if (blockedGrid == null) return false;

        if (IsPathWalkable(from.x, from.y, transientBlockedKeys))
        {
            found = from;
            return true;
        }

        for (int r = 1; r <= Mathf.Max(1, maxRadius); r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int dz = r - Mathf.Abs(dx);
                Vector2Int a = new Vector2Int(from.x + dx, from.y + dz);
                Vector2Int b = new Vector2Int(from.x + dx, from.y - dz);

                if (IsPathWalkable(a.x, a.y, transientBlockedKeys)) { found = a; return true; }
                if (IsPathWalkable(b.x, b.y, transientBlockedKeys)) { found = b; return true; }
            }
        }
        return false;
    }


    public List<Vector2Int> FindPathAStar(Vector2Int start, Vector2Int goal, bool? useDiagonalOverride = null)
    {
        return FindPathAStar(start, goal, useDiagonalOverride, null);
    }

    public List<Vector2Int> FindPathAStar(Vector2Int start, Vector2Int goal, bool? useDiagonalOverride, HashSet<long> transientBlockedKeys)
    {
        var path = new List<Vector2Int>();
        if (blockedGrid == null) return path;

        bool useDiagonal = useDiagonalOverride ?? allowDiagonal;

        if (!IsInBounds(start.x, start.y) || !IsInBounds(goal.x, goal.y)) return path;
        if (!IsPathWalkable(start.x, start.y, transientBlockedKeys) || !IsPathWalkable(goal.x, goal.y, transientBlockedKeys)) return path;

        Array.Clear(_astarClosed,   0, _astarClosed.Length);
        Array.Clear(_astarCameFrom, 0, _astarCameFrom.Length);
        Array.Clear(_astarHasParent,0, _astarHasParent.Length);
        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                _astarGScore[x, z] = float.PositiveInfinity;
            }
        }
        float[,] gScore = _astarGScore;
        bool[,] closed = _astarClosed;
        Vector2Int[,] cameFrom = _astarCameFrom;
        bool[,] hasParent = _astarHasParent;

        var openHeap = new AStarMinHeap();
        gScore[start.x, start.y] = 0f;
        float startHeuristic = Heuristic(start, goal, useDiagonal);
        openHeap.Push(new AStarHeapEntry
        {
            Cell = start,
            FScore = startHeuristic,
            HScore = startHeuristic
        });

        while (openHeap.Count > 0)
        {
            Vector2Int current = openHeap.Pop().Cell;

            if (current == goal)
            {
                ReconstructPath(current, start, cameFrom, hasParent, path);
                if (smoothAStarPath && path.Count > 2)
                {
                    SimplifyAStarPathInPlace(path);
                }
                return path;
            }

            if (closed[current.x, current.y]) continue;
            closed[current.x, current.y] = true;

            AddNeighbors(current, useDiagonal, out Vector2Int[] neighbors, out float[] moveCost);

            for (int i = 0; i < neighbors.Length; i++)
            {
                Vector2Int n = neighbors[i];
                if (!IsInBounds(n.x, n.y)) continue;
                if (!IsPathWalkable(n.x, n.y, transientBlockedKeys)) continue;
                if (closed[n.x, n.y]) continue;

                // 对角移动时，禁止从两个障碍物夹角之间“穿角”。
                if (useDiagonal && IsDiagonal(current, n))
                {
                    if (!CanTraverseDiagonal(current, n, transientBlockedKeys))
                        continue;
                }

                float tentative = gScore[current.x, current.y] + moveCost[i];
                if (tentative < gScore[n.x, n.y])
                {
                    gScore[n.x, n.y] = tentative;
                    cameFrom[n.x, n.y] = current;
                    hasParent[n.x, n.y] = true;
                    float heuristic = Heuristic(n, goal, useDiagonal);
                    openHeap.Push(new AStarHeapEntry
                    {
                        Cell = n,
                        FScore = tentative + heuristic,
                        HScore = heuristic
                    });
                }
            }
        }

        return path;
    }

    private void BuildLogicalGrid(List<Feature2D> features, Rect allBounds)
    {
        Rect b = allBounds;
        b.xMin -= mapMargin;
        b.yMin -= mapMargin;
        b.xMax += mapMargin;
        b.yMax += mapMargin;

        mapBoundsXY = b;

        gridWidth = Mathf.Max(1, Mathf.CeilToInt(b.width / cellSize));
        gridLength = Mathf.Max(1, Mathf.CeilToInt(b.height / cellSize));

        blockedGrid = new bool[gridWidth, gridLength];
        cellTypeGrid = new CampusGridCellType[gridWidth, gridLength];
        cellFeatureUidGrid = new string[gridWidth, gridLength];
        cellFeatureNameGrid = new string[gridWidth, gridLength];
        _astarGScore   = new float[gridWidth, gridLength];
        _astarClosed   = new bool[gridWidth, gridLength];
        _astarCameFrom = new Vector2Int[gridWidth, gridLength];
        _astarHasParent = new bool[gridWidth, gridLength];
        int[,] cellPriorityGrid = new int[gridWidth, gridLength];
        featureAliasCellMap = new Dictionary<string, Vector2Int>(StringComparer.OrdinalIgnoreCase);
        featureAliasUidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        featureAliasNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        featureSpatialProfileByUid = new Dictionary<string, FeatureSpatialProfile>(StringComparer.OrdinalIgnoreCase);
        featureCollectionMembers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        featureSpatialIndexByUid = new Dictionary<string, FeatureSpatialIndex>(StringComparer.OrdinalIgnoreCase);
        featureUidsByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        featureUidsByCollectionKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        AssignFeatureRuntimeMetadata(features);
        InitializeFeatureSpatialIndexes(features);
        PrepareFeatureRasterization(features);

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                blockedGrid[x, z] = false;
                cellTypeGrid[x, z] = CampusGridCellType.Free;
                cellFeatureUidGrid[x, z] = null;
                cellFeatureNameGrid[x, z] = null;
                cellPriorityGrid[x, z] = int.MinValue;
            }
        }

        for (int i = 0; i < features.Count; i++)
        {
            Feature2D f = features[i];
            if (f == null) continue;

            CampusGridCellType t = KindToCellType(f.kind);
            bool shouldBlock = IsBlockedKind(t);
            int featurePriority = GetFeatureRasterPriority(t);

            if (!f.rasterBoundsValid) continue;
            Rect rasterBounds = f.rasterBounds;

            BoundsToGridRange(rasterBounds, out int xMin, out int xMax, out int zMin, out int zMax);
            if (xMax < xMin || zMax < zMin) continue;

            for (int x = xMin; x <= xMax; x++)
            {
                for (int z = zMin; z <= zMax; z++)
                {
                    Rect cellRect = GetCellRectXY(x, z);
                    Vector2 cellCenter = GetCellCenterXY(x, z);
                    if (!FeatureOverlapsCell(f, cellRect, cellCenter)) continue;

                    RegisterFeatureAlias(f, x, z);
                    RegisterFeatureSpatialCell(f, t, x, z);

                    if (featurePriority < cellPriorityGrid[x, z]) continue;

                    cellPriorityGrid[x, z] = featurePriority;
                    blockedGrid[x, z] = shouldBlock;
                    cellTypeGrid[x, z] = t;
                    AssignCellFeatureIdentity(x, z, f.uid,
                        string.IsNullOrWhiteSpace(f.runtimeAlias) ? f.effectiveName : f.runtimeAlias);
                }
            }
        }

        FinalizeFeatureSpatialIndexes();
        BuildPathClearanceGrid();

        if (logBuildSummary)
        {
            int blockedCount = 0;
            int clearanceCount = 0;
            for (int x = 0; x < gridWidth; x++)
                for (int z = 0; z < gridLength; z++)
                {
                    if (blockedGrid[x, z]) blockedCount++;
                    if (pathClearanceBlockedGrid != null && pathClearanceBlockedGrid[x, z]) clearanceCount++;
                }

            Debug.Log($"[CampusGrid2D] : {gridWidth}x{gridLength}, cell={cellSize}m, blocked={blockedCount}/{gridWidth * gridLength}, buildingClearance={clearanceCount}, clearanceDist={buildingPathClearance:F1}m");
        }
    }


    private void PrepareFeatureRasterization(List<Feature2D> features)
    {
        if (features == null || features.Count == 0) return;

        float strokeWidth = campusLoader != null
            ? Mathf.Max(0.1f, campusLoader.strokeWidthM * GetHorizontalMapScale())
            : 2f;

        for (int i = 0; i < features.Count; i++)
        {
            Feature2D feature = features[i];
            if (feature == null) continue;

            feature.rasterAreaParts.Clear();
            feature.rasterStrokeQuads.Clear();
            feature.rasterBounds = default(Rect);
            feature.rasterBoundsValid = false;

            FeatureRasterMode mode = DetermineRasterMode(feature);
            feature.rasterMode = mode;
            if (mode == FeatureRasterMode.Area)
            {
                PrepareFeatureRasterArea(feature);
            }
            else if (mode == FeatureRasterMode.Line)
            {
                PrepareFeatureRasterLine(feature, strokeWidth);
            }
            else if (feature.boundsValid)
            {
                feature.rasterBounds = feature.bounds;
                feature.rasterBoundsValid = true;
            }
        }
    }

    private FeatureRasterMode DetermineRasterMode(Feature2D feature)
    {
        if (feature == null) return FeatureRasterMode.Bounds;

        switch (NormalizeFeatureKindToken(feature.kind))
        {
            case "building":
            case "water":
            case "parking":
            case "green":
            case "forest":
            case "sports":
                return FeatureRasterMode.Area;

            case "road":
            case "expressway":
            case "bridge":
                return FeatureRasterMode.Line;

            default:
                if (feature.HasArea) return FeatureRasterMode.Area;
                if (feature.HasLine) return FeatureRasterMode.Line;
                return FeatureRasterMode.Bounds;
        }
    }

    private void PrepareFeatureRasterArea(Feature2D feature)
    {
        if (feature == null) return;

        float collinearEps = NormalizeFeatureKindToken(feature.kind) == "building" ? 0.00005f : 0.0005f;
        List<List<Vector2>> cleanedOuters = new List<List<Vector2>>();
        List<List<Vector2>> cleanedHoles = new List<List<Vector2>>();

        if (feature.outerRings != null)
        {
            for (int i = 0; i < feature.outerRings.Count; i++)
            {
                List<Vector2> ring = CloneAndCleanRing(feature.outerRings[i], true, collinearEps);
                if (ring != null) cleanedOuters.Add(ring);
            }
        }

        if (cleanedOuters.Count == 0 && feature.boundsValid &&
            (NormalizeFeatureKindToken(feature.kind) == "building" || feature.HasArea))
        {
            cleanedOuters.Add(BuildRectRing(feature.bounds));
        }

        if (feature.innerRings != null)
        {
            for (int i = 0; i < feature.innerRings.Count; i++)
            {
                List<Vector2> hole = CloneAndCleanRing(feature.innerRings[i], false, collinearEps);
                if (hole != null) cleanedHoles.Add(hole);
            }
        }

        for (int i = 0; i < cleanedOuters.Count; i++)
        {
            List<Vector2> outer = cleanedOuters[i];
            RasterAreaPart part = new RasterAreaPart();
            part.outer.AddRange(outer);

            for (int h = 0; h < cleanedHoles.Count; h++)
            {
                List<Vector2> hole = cleanedHoles[h];
                if (hole == null || hole.Count < 3) continue;
                if (PointInPolygon2D(hole[0], outer))
                {
                    part.holes.Add(new List<Vector2>(hole));
                }
            }

            ComputeBoundsFromRings(new List<List<Vector2>> { part.outer }, part.holes, out part.bounds, out part.boundsValid);
            if (!part.boundsValid) continue;

            feature.rasterAreaParts.Add(part);
            AppendFeatureRasterBounds(feature, part.bounds, part.boundsValid);
        }
    }

    private void PrepareFeatureRasterLine(Feature2D feature, float strokeWidth)
    {
        if (feature == null || feature.linePoints == null || feature.linePoints.Count < 2) return;

        float halfW = Mathf.Max(0.1f, strokeWidth) * 0.5f;
        for (int i = 0; i < feature.linePoints.Count - 1; i++)
        {
            Vector2 a = feature.linePoints[i];
            Vector2 b = feature.linePoints[i + 1];
            Vector2 dir = b - a;
            float len = dir.magnitude;
            if (len < 1e-6f) continue;

            dir /= len;
            Vector2 left = new Vector2(-dir.y, dir.x);

            RasterStrokeQuad quad = new RasterStrokeQuad();
            quad.points.Add(a + left * halfW);
            quad.points.Add(a - left * halfW);
            quad.points.Add(b - left * halfW);
            quad.points.Add(b + left * halfW);
            ComputeBoundsFromPolygon(quad.points, out quad.bounds, out quad.boundsValid);
            if (!quad.boundsValid) continue;

            feature.rasterStrokeQuads.Add(quad);
            AppendFeatureRasterBounds(feature, quad.bounds, quad.boundsValid);
        }
    }

    private static List<Vector2> CloneAndCleanRing(List<Vector2> source, bool wantCCW, float collinearEps)
    {
        if (source == null || source.Count < 3) return null;

        List<Vector2> ring = new List<Vector2>(source);
        RemoveClosingDuplicate(ring);
        CleanRingInPlace(ring, 1e-5f, collinearEps);
        if (ring.Count < 3) return null;

        EnsureWinding(ring, wantCCW);
        return ring;
    }

    private static List<Vector2> BuildRectRing(Rect rect)
    {
        List<Vector2> ring = new List<Vector2>(4)
        {
            new Vector2(rect.xMin, rect.yMin),
            new Vector2(rect.xMax, rect.yMin),
            new Vector2(rect.xMax, rect.yMax),
            new Vector2(rect.xMin, rect.yMax),
        };
        EnsureWinding(ring, true);
        return ring;
    }

    private static void ComputeBoundsFromPolygon(List<Vector2> polygon, out Rect bounds, out bool valid)
    {
        bounds = default(Rect);
        valid = false;
        if (polygon == null || polygon.Count < 3) return;

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 p = polygon[i];
            if (p.x < minX) minX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.x > maxX) maxX = p.x;
            if (p.y > maxY) maxY = p.y;
        }

        if (minX <= maxX && minY <= maxY)
        {
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            valid = true;
        }
    }

    private static void AppendFeatureRasterBounds(Feature2D feature, Rect bounds, bool boundsValid)
    {
        if (feature == null || !boundsValid) return;

        if (!feature.rasterBoundsValid)
        {
            feature.rasterBounds = bounds;
            feature.rasterBoundsValid = true;
            return;
        }

        feature.rasterBounds = MergeRect(feature.rasterBounds, bounds);
    }

    private bool FeatureOverlapsCell(Feature2D feature, Rect cellRect, Vector2 cellCenter)
    {
        if (feature == null) return false;

        FeatureRasterMode mode = feature.rasterMode;
        if (mode == FeatureRasterMode.Area && feature.HasRasterArea)
        {
            for (int i = 0; i < feature.rasterAreaParts.Count; i++)
            {
                if (AreaPartOverlapsCell(feature.rasterAreaParts[i], cellRect, cellCenter))
                {
                    return true;
                }
            }
            return false;
        }
        if (mode == FeatureRasterMode.Area) return false;

        if (mode == FeatureRasterMode.Line && feature.HasRasterLine)
        {
            for (int i = 0; i < feature.rasterStrokeQuads.Count; i++)
            {
                RasterStrokeQuad quad = feature.rasterStrokeQuads[i];
                if (!quad.boundsValid || !RectsOverlapInclusive(quad.bounds, cellRect)) continue;
                if (PolygonIntersectsRect(quad.points, cellRect))
                {
                    return true;
                }
            }
            return false;
        }
        if (mode == FeatureRasterMode.Line) return false;

        return feature.rasterBoundsValid && RectsOverlapInclusive(feature.rasterBounds, cellRect);
    }

    private static bool AreaPartOverlapsCell(RasterAreaPart part, Rect cellRect, Vector2 cellCenter)
    {
        if (part == null || !part.boundsValid || !RectsOverlapInclusive(part.bounds, cellRect)) return false;
        if (!PolygonIntersectsRect(part.outer, cellRect)) return false;
        if (part.holes == null || part.holes.Count == 0) return true;

        if (PointInFilledArea(cellCenter, part.outer, part.holes)) return true;
        if (AnyRectCornerInFilledArea(cellRect, part.outer, part.holes)) return true;
        if (PolygonHasVertexInsideRect(part.outer, cellRect)) return true;
        if (PolygonEdgesIntersectRect(part.outer, cellRect)) return true;

        for (int i = 0; i < part.holes.Count; i++)
        {
            List<Vector2> hole = part.holes[i];
            if (hole == null || hole.Count < 3) continue;
            if (PolygonHasVertexInsideRect(hole, cellRect)) return true;
            if (PolygonEdgesIntersectRect(hole, cellRect)) return true;
        }

        return !RectFullyInsideAnyHole(cellRect, cellCenter, part.holes);
    }

    private static bool PointInFilledArea(Vector2 point, List<Vector2> outer, List<List<Vector2>> holes)
    {
        if (!PointInPolygon2D(point, outer)) return false;
        if (holes == null) return true;

        for (int i = 0; i < holes.Count; i++)
        {
            List<Vector2> hole = holes[i];
            if (hole != null && hole.Count >= 3 && PointInPolygon2D(point, hole))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyRectCornerInFilledArea(Rect rect, List<Vector2> outer, List<List<Vector2>> holes)
    {
        return PointInFilledArea(new Vector2(rect.xMin, rect.yMin), outer, holes) ||
               PointInFilledArea(new Vector2(rect.xMax, rect.yMin), outer, holes) ||
               PointInFilledArea(new Vector2(rect.xMax, rect.yMax), outer, holes) ||
               PointInFilledArea(new Vector2(rect.xMin, rect.yMax), outer, holes);
    }

    private static bool RectFullyInsideAnyHole(Rect rect, Vector2 rectCenter, List<List<Vector2>> holes)
    {
        if (holes == null || holes.Count == 0) return false;

        for (int i = 0; i < holes.Count; i++)
        {
            List<Vector2> hole = holes[i];
            if (hole == null || hole.Count < 3) continue;
            if (RectFullyInsidePolygon(rect, rectCenter, hole))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RectFullyInsidePolygon(Rect rect, Vector2 rectCenter, List<Vector2> polygon)
    {
        if (polygon == null || polygon.Count < 3) return false;

        if (!PointInPolygon2D(rectCenter, polygon)) return false;

        Vector2 c0 = new Vector2(rect.xMin, rect.yMin);
        Vector2 c1 = new Vector2(rect.xMax, rect.yMin);
        Vector2 c2 = new Vector2(rect.xMax, rect.yMax);
        Vector2 c3 = new Vector2(rect.xMin, rect.yMax);

        return PointInPolygon2D(c0, polygon) &&
               PointInPolygon2D(c1, polygon) &&
               PointInPolygon2D(c2, polygon) &&
               PointInPolygon2D(c3, polygon) &&
               !PolygonEdgesIntersectRect(polygon, rect);
    }

    private static bool PolygonIntersectsRect(List<Vector2> polygon, Rect rect)
    {
        if (polygon == null || polygon.Count < 3) return false;

        if (PointInPolygon2D(new Vector2(rect.xMin, rect.yMin), polygon) ||
            PointInPolygon2D(new Vector2(rect.xMax, rect.yMin), polygon) ||
            PointInPolygon2D(new Vector2(rect.xMax, rect.yMax), polygon) ||
            PointInPolygon2D(new Vector2(rect.xMin, rect.yMax), polygon))
        {
            return true;
        }

        if (PointInPolygon2D(new Vector2(rect.center.x, rect.center.y), polygon)) return true;
        if (PolygonHasVertexInsideRect(polygon, rect)) return true;
        return PolygonEdgesIntersectRect(polygon, rect);
    }

    private static bool PolygonHasVertexInsideRect(List<Vector2> polygon, Rect rect)
    {
        if (polygon == null || polygon.Count < 3) return false;

        for (int i = 0; i < polygon.Count; i++)
        {
            if (RectContainsPointInclusive(rect, polygon[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PolygonEdgesIntersectRect(List<Vector2> polygon, Rect rect)
    {
        if (polygon == null || polygon.Count < 2) return false;

        Vector2 r0 = new Vector2(rect.xMin, rect.yMin);
        Vector2 r1 = new Vector2(rect.xMax, rect.yMin);
        Vector2 r2 = new Vector2(rect.xMax, rect.yMax);
        Vector2 r3 = new Vector2(rect.xMin, rect.yMax);

        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % polygon.Count];
            if (SegmentsIntersect2D(a, b, r0, r1) ||
                SegmentsIntersect2D(a, b, r1, r2) ||
                SegmentsIntersect2D(a, b, r2, r3) ||
                SegmentsIntersect2D(a, b, r3, r0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RectContainsPointInclusive(Rect rect, Vector2 point, float eps = 1e-5f)
    {
        return point.x >= rect.xMin - eps &&
               point.x <= rect.xMax + eps &&
               point.y >= rect.yMin - eps &&
               point.y <= rect.yMax + eps;
    }

    private static bool RectsOverlapInclusive(Rect a, Rect b, float eps = 1e-5f)
    {
        return a.xMin <= b.xMax + eps &&
               a.xMax >= b.xMin - eps &&
               a.yMin <= b.yMax + eps &&
               a.yMax >= b.yMin - eps;
    }

    private int GetFeatureRasterPriority(CampusGridCellType cellType)
    {
        switch (cellType)
        {
            case CampusGridCellType.Bridge: return 1000;
            case CampusGridCellType.Building: return 900;
            case CampusGridCellType.Expressway: return 820;
            case CampusGridCellType.Road: return 780;
            case CampusGridCellType.Parking: return 730;
            case CampusGridCellType.Sports: return 700;
            case CampusGridCellType.Water: return 680;
            case CampusGridCellType.Green: return 640;
            case CampusGridCellType.Forest: return 620;
            case CampusGridCellType.Other: return 600;
            default: return 0;
        }
    }

    private void CreateOrUpdateCellVisual(int x, int z)
    {
        GameObject cell = GameObject.CreatePrimitive(PrimitiveType.Quad);
        cell.name = $"cell_{x}_{z}";
        cell.transform.SetParent(visualRoot, false);
        cell.transform.position = GridToWorldCenter(x, z, gridVisualY);
        cell.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        cell.transform.localScale = Vector3.one * cellSize * cellVisualScale;

        Collider col = cell.GetComponent<Collider>();
        if (col != null) DestroyImmediateSafe(col);

        Renderer r = cell.GetComponent<Renderer>();
        r.sharedMaterial = GetGridVisualMaterial();

        Color c = GetCellColor(x, z);
        var block = new MaterialPropertyBlock();
        block.SetColor("_Color", c);
        block.SetColor("_BaseColor", c); // URP Lit 浣跨敤 _BaseColor
        r.SetPropertyBlock(block);

        visualCells.Add(cell);
    }

    private Color GetCellColor(int x, int z)
    {
        if (blockedGrid[x, z])
        {
            CampusGridCellType t = cellTypeGrid[x, z];
            if (t == CampusGridCellType.Building) return buildingColor;
            if (t == CampusGridCellType.Water) return waterColor;
            return blockedColor;
        }

        CampusGridCellType type = cellTypeGrid[x, z];
        if (type == CampusGridCellType.Road || type == CampusGridCellType.Expressway || type == CampusGridCellType.Bridge) return roadColor;
        return freeColor;
    }

    private Material GetGridVisualMaterial()
    {
        if (gridMaterial != null) return gridMaterial;
        if (runtimeDefaultMat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            runtimeDefaultMat = new Material(shader);
            runtimeDefaultMat.color = Color.white;
        }
        return runtimeDefaultMat;
    }

    private bool TryLoadJsonFromCampusLoader(out string outJson)
    {
        outJson = null;

        if (campusLoader.preferEmbeddedText && !string.IsNullOrWhiteSpace(campusLoader.jsonText))
        {
            outJson = campusLoader.jsonText;
            return true;
        }

        string path = (campusLoader.jsonFilePath ?? "").Trim();
        if (string.IsNullOrEmpty(path)) return false;

        if (!File.Exists(path))
        {
            string projRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string abs = Path.GetFullPath(Path.Combine(projRoot, path));
            if (File.Exists(abs)) path = abs;
        }

        if (!File.Exists(path)) return false;

        outJson = File.ReadAllText(path);
        return !string.IsNullOrWhiteSpace(outJson);
    }

    private float GetHorizontalMapScale()
    {
        if (campusLoader == null) return 1f;
        return Mathf.Max(0.1f, campusLoader.horizontalMapScale);
    }

    private static void ApplyHorizontalScaleToFeatures(List<Feature2D> features, ref Rect allBounds, float scale)
    {
        float safeScale = Mathf.Max(0.1f, scale);
        if (features == null || features.Count == 0 || Mathf.Abs(safeScale - 1f) < 1e-4f) return;

        Vector2 pivot = allBounds.center;
        for (int i = 0; i < features.Count; i++)
        {
            Feature2D feature = features[i];
            if (feature == null) continue;

            ScaleRingCollection(feature.outerRings, pivot, safeScale);
            ScaleRingCollection(feature.innerRings, pivot, safeScale);
            ScalePointCollection(feature.linePoints, pivot, safeScale);

            if (feature.boundsValid)
            {
                feature.bounds = ScaleRectAroundPivot(feature.bounds, pivot, safeScale);
            }
        }

        allBounds = ScaleRectAroundPivot(allBounds, pivot, safeScale);
    }

    private static void ScaleRingCollection(List<List<Vector2>> rings, Vector2 pivot, float scale)
    {
        if (rings == null) return;
        for (int i = 0; i < rings.Count; i++)
        {
            ScalePointCollection(rings[i], pivot, scale);
        }
    }

    private static void ScalePointCollection(List<Vector2> points, Vector2 pivot, float scale)
    {
        if (points == null) return;
        for (int i = 0; i < points.Count; i++)
        {
            points[i] = ScalePointAroundPivot(points[i], pivot, scale);
        }
    }

    private static Vector2 ScalePointAroundPivot(Vector2 point, Vector2 pivot, float scale)
    {
        return pivot + (point - pivot) * scale;
    }

    private static Rect ScaleRectAroundPivot(Rect rect, Vector2 pivot, float scale)
    {
        Vector2 min = ScalePointAroundPivot(new Vector2(rect.xMin, rect.yMin), pivot, scale);
        Vector2 max = ScalePointAroundPivot(new Vector2(rect.xMax, rect.yMax), pivot, scale);
        return Rect.MinMaxRect(
            Mathf.Min(min.x, max.x),
            Mathf.Min(min.y, max.y),
            Mathf.Max(min.x, max.x),
            Mathf.Max(min.y, max.y)
        );
    }

    private bool ParseCampusJson(string json, out List<Feature2D> outFeatures, out Rect outAllBounds)
    {
        outFeatures = new List<Feature2D>();
        outAllBounds = new Rect();
        bool allBoundsValid = false;

        JObject root;
        try { root = JObject.Parse(json); }
        catch { return false; }

        JArray features = root["features"] as JArray;
        if (features == null) return false;

        for (int i = 0; i < features.Count; i++)
        {
            JObject obj = features[i] as JObject;
            if (obj == null) continue;

            Feature2D f = new Feature2D();
            f.uid = ((string)obj["uid"] ?? "").Trim();
            f.name = ((string)obj["name"] ?? "").Trim();
            f.kind = ((string)obj["kind"] ?? "other").Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(f.name))
            {
                JObject tagsObj = obj["tags"] as JObject;
                if (tagsObj != null) f.name = ((string)tagsObj["name"] ?? "").Trim();
            }

            JObject ringsObj = obj["rings"] as JObject;
            if (ringsObj != null)
            {
                JArray outer = ringsObj["outer"] as JArray;
                if (outer != null)
                {
                    for (int r = 0; r < outer.Count; r++)
                    {
                        JArray ringPts = outer[r] as JArray;
                        List<Vector2> ring = ReadRingAndAutoClose(ringPts, 0.01f);
                        if (ring.Count >= 4) f.outerRings.Add(ring);
                    }
                }

                JArray inner = ringsObj["inner"] as JArray;
                if (inner != null)
                {
                    for (int r = 0; r < inner.Count; r++)
                    {
                        JArray ringPts = inner[r] as JArray;
                        List<Vector2> ring = ReadRingAndAutoClose(ringPts, 0.01f);
                        if (ring.Count >= 4) f.innerRings.Add(ring);
                    }
                }
            }

            JArray linePts = obj["points_xy_m"] as JArray;
            if (linePts != null)
            {
                for (int p = 0; p < linePts.Count; p++)
                {
                    Vector2 xy = ReadXY(linePts[p]);
                    f.linePoints.Add(xy);
                }
            }

            bool hasBounds = false;
            Rect rb = default(Rect);
            Rect lb = default(Rect);

            if (f.HasArea)
            {
                ComputeBoundsFromRings(f.outerRings, f.innerRings, out rb, out bool rv);
                if (rv)
                {
                    f.bounds = rb;
                    f.boundsValid = true;
                    hasBounds = true;
                }
            }

            if (f.HasLine)
            {
                ComputeBoundsFromLine(f.linePoints, out lb, out bool lv);
                if (lv)
                {
                    if (!hasBounds)
                    {
                        f.bounds = lb;
                        f.boundsValid = true;
                        hasBounds = true;
                    }
                    else
                    {
                        f.bounds = MergeRect(f.bounds, lb);
                        f.boundsValid = true;
                    }
                }
            }

            if (!f.boundsValid)
            {
                Vector3 origin = transform.position;
                Vector2 center = new Vector2(origin.x, origin.z);
                f.bounds = new Rect(center.x - 0.5f, center.y - 0.5f, 1f, 1f);
                f.boundsValid = true;
            }

            outFeatures.Add(f);
            if (!allBoundsValid)
            {
                outAllBounds = f.bounds;
                allBoundsValid = true;
            }
            else
            {
                outAllBounds = MergeRect(outAllBounds, f.bounds);
            }
        }

        return outFeatures.Count > 0 && allBoundsValid;
    }

    private void BoundsToGridRange(Rect b, out int xMin, out int xMax, out int zMin, out int zMax)
    {
        float eps = Mathf.Max(1e-4f, cellSize * 0.001f);
        float rasterMaxX = Mathf.Max(b.xMin + eps, b.xMax - eps);
        float rasterMaxZ = Mathf.Max(b.yMin + eps, b.yMax - eps);

        xMin = Mathf.FloorToInt((b.xMin - mapBoundsXY.xMin) / cellSize);
        xMax = Mathf.FloorToInt((rasterMaxX - mapBoundsXY.xMin) / cellSize);
        zMin = Mathf.FloorToInt((b.yMin - mapBoundsXY.yMin) / cellSize);
        zMax = Mathf.FloorToInt((rasterMaxZ - mapBoundsXY.yMin) / cellSize);

        xMin = Mathf.Clamp(xMin, 0, gridWidth - 1);
        xMax = Mathf.Clamp(xMax, 0, gridWidth - 1);
        zMin = Mathf.Clamp(zMin, 0, gridLength - 1);
        zMax = Mathf.Clamp(zMax, 0, gridLength - 1);
    }

    private Vector2 GetCellCenterXY(int x, int z)
    {
        float px = mapBoundsXY.xMin + (x + 0.5f) * cellSize;
        float py = mapBoundsXY.yMin + (z + 0.5f) * cellSize;
        return new Vector2(px, py);
    }

    private Rect GetCellRectXY(int x, int z)
    {
        float x0 = mapBoundsXY.xMin + x * cellSize;
        float z0 = mapBoundsXY.yMin + z * cellSize;
        return Rect.MinMaxRect(x0, z0, x0 + cellSize, z0 + cellSize);
    }

    private static Vector2 ReadXY(JToken tok)
    {
        JArray a = tok as JArray;
        if (a == null || a.Count < 2) return Vector2.zero;
        return new Vector2(a[0].Value<float>(), a[1].Value<float>());
    }

    private static List<Vector2> ReadRingAndAutoClose(JArray ringPts, float closeEps)
    {
        var pts = new List<Vector2>();
        if (ringPts == null || ringPts.Count < 3) return pts;

        for (int i = 0; i < ringPts.Count; i++)
        {
            JArray p = ringPts[i] as JArray;
            if (p == null || p.Count < 2) continue;
            pts.Add(new Vector2(p[0].Value<float>(), p[1].Value<float>()));
        }

        if (pts.Count < 3) return pts;

        if ((pts[0] - pts[pts.Count - 1]).sqrMagnitude > closeEps * closeEps)
        {
            pts.Add(pts[0]);
        }

        return pts;
    }

    private static void ComputeBoundsFromLine(List<Vector2> line, out Rect bounds, out bool valid)
    {
        bounds = default(Rect);
        valid = false;
        if (line == null || line.Count < 2) return;

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        for (int i = 0; i < line.Count; i++)
        {
            Vector2 p = line[i];
            minX = Mathf.Min(minX, p.x);
            minY = Mathf.Min(minY, p.y);
            maxX = Mathf.Max(maxX, p.x);
            maxY = Mathf.Max(maxY, p.y);
        }

        if (minX <= maxX && minY <= maxY)
        {
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            valid = true;
        }
    }

    private static void ComputeBoundsFromRings(List<List<Vector2>> outers, List<List<Vector2>> inners, out Rect bounds, out bool valid)
    {
        bounds = default(Rect);
        valid = false;

        bool any = false;
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        void Consume(List<List<Vector2>> rings)
        {
            if (rings == null) return;
            for (int r = 0; r < rings.Count; r++)
            {
                List<Vector2> ring = rings[r];
                if (ring == null || ring.Count < 3) continue;
                for (int i = 0; i < ring.Count; i++)
                {
                    Vector2 p = ring[i];
                    minX = Mathf.Min(minX, p.x);
                    minY = Mathf.Min(minY, p.y);
                    maxX = Mathf.Max(maxX, p.x);
                    maxY = Mathf.Max(maxY, p.y);
                    any = true;
                }
            }
        }

        Consume(outers);
        Consume(inners);

        if (any)
        {
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            valid = true;
        }
    }

    private static Rect MergeRect(Rect a, Rect b)
    {
        return Rect.MinMaxRect(
            Mathf.Min(a.xMin, b.xMin),
            Mathf.Min(a.yMin, b.yMin),
            Mathf.Max(a.xMax, b.xMax),
            Mathf.Max(a.yMax, b.yMax)
        );
    }


    private static string NormalizeFeatureToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim().ToLowerInvariant();
        s = s.Replace(" ", string.Empty)
             .Replace("_", string.Empty)
             .Replace("-", string.Empty)
             .Replace(":", string.Empty)
             .Replace("：", string.Empty);
        return s;
    }


    private static bool TryStripStructuredTargetPrefix(string text, out string prefix, out string stripped)
    {
        prefix = string.Empty;
        stripped = text != null ? text.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(stripped)) return false;

        int colonIndex = stripped.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= stripped.Length - 1)
        {
            return false;
        }

        string left = stripped.Substring(0, colonIndex).Trim();
        string right = stripped.Substring(colonIndex + 1).Trim();
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (left.Length > 24) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(left, @"^[A-Za-z][A-Za-z0-9_\-]*$")) return false;

        prefix = left;
        stripped = right;
        return true;
    }

    private void AssignFeatureRuntimeMetadata(List<Feature2D> features)
    {
        if (features == null || features.Count == 0) return;

        int buildingIndex = 0;
        int sportsIndex = 0;
        int waterIndex = 0;
        int roadIndex = 0;
        int expresswayIndex = 0;
        int bridgeIndex = 0;
        int parkingIndex = 0;
        int greenIndex = 0;
        int forestIndex = 0;

        for (int i = 0; i < features.Count; i++)
        {
            Feature2D f = features[i];
            if (f == null) continue;

            f.effectiveName = GetEffectiveFeatureName(f);
            f.runtimeAlias = BuildRuntimeAliasForFeature(
                f,
                ref buildingIndex,
                ref sportsIndex,
                ref waterIndex,
                ref roadIndex,
                ref expresswayIndex,
                ref bridgeIndex,
                ref parkingIndex,
                ref greenIndex,
                ref forestIndex
            );
        }
    }

    private static string GetEffectiveFeatureName(Feature2D feature)
    {
        if (feature == null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(feature.name) && feature.name.Trim() != "-")
        {
            return feature.name.Trim();
        }

        // 网格层保存的是真实业务名，不是 Unity 场景里的实例编号名。
        // 对没有显式名字的要素，退回到 kind 默认名，保证 building/forest 这类查询仍然可用。
        return NormalizeFeatureKindToken(feature.kind);
    }

    private static string BuildRuntimeAliasForFeature(
        Feature2D feature,
        ref int buildingIndex,
        ref int sportsIndex,
        ref int waterIndex,
        ref int roadIndex,
        ref int expresswayIndex,
        ref int bridgeIndex,
        ref int parkingIndex,
        ref int greenIndex,
        ref int forestIndex)
    {
        if (feature == null) return string.Empty;

        bool hasName = !string.IsNullOrWhiteSpace(feature.name) && feature.name.Trim() != "-";
        if (hasName) return feature.name.Trim();

        string kind = NormalizeFeatureKindToken(feature.kind);
        switch (kind)
        {
            case "building":   return $"building_{++buildingIndex}";
            case "road":       return $"road_{++roadIndex}";
            case "expressway": return $"expressway_{++expresswayIndex}";
            case "bridge":     return $"bridge_{++bridgeIndex}";
            case "water":      return $"water_{++waterIndex}";
            case "forest":     return $"forest_{++forestIndex}";
            case "sports":     return $"sports_{++sportsIndex}";
            case "parking":    return $"parking_{++parkingIndex}";
            case "green":
            default:           return $"green_{++greenIndex}";
        }
    }

    private static string NormalizeFeatureKindToken(string kind)
    {
        string k = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(k) ? "other" : k;
    }

    private void InitializeFeatureSpatialIndexes(List<Feature2D> features)
    {
        if (features == null || featureSpatialIndexByUid == null) return;

        for (int i = 0; i < features.Count; i++)
        {
            Feature2D feature = features[i];
            if (feature == null || string.IsNullOrWhiteSpace(feature.uid)) continue;

            string uid = feature.uid.Trim();
            if (featureSpatialIndexByUid.ContainsKey(uid)) continue;

            string collectionKey = $"feature_kind:{NormalizeFeatureKindToken(feature.kind)}";
            featureSpatialIndexByUid[uid] = new FeatureSpatialIndex
            {
                uid = uid,
                name = string.IsNullOrWhiteSpace(feature.effectiveName) ? string.Empty : feature.effectiveName.Trim(),
                runtimeAlias = string.IsNullOrWhiteSpace(feature.runtimeAlias) ? string.Empty : feature.runtimeAlias.Trim(),
                kind = NormalizeFeatureKindToken(feature.kind),
                collectionKey = collectionKey,
                cellType = KindToCellType(feature.kind)
            };

            RegisterFeatureSpatialName(uid, feature.effectiveName);
            RegisterCollectionMembership(collectionKey, uid);
        }
    }

    private void RegisterFeatureSpatialCell(Feature2D feature, CampusGridCellType cellType, int x, int z)
    {
        if (feature == null || string.IsNullOrWhiteSpace(feature.uid) || featureSpatialIndexByUid == null) return;

        string uid = feature.uid.Trim();
        if (!featureSpatialIndexByUid.TryGetValue(uid, out FeatureSpatialIndex index) || index == null)
        {
            return;
        }

        long key = (((long)x) << 32) ^ (uint)z;
        if (!index.occupiedCellKeys.Add(key)) return;

        index.cellType = cellType;
        index.occupiedCells.Add(new Vector2Int(x, z));
        if (x < index.minX) index.minX = x;
        if (x > index.maxX) index.maxX = x;
        if (z < index.minZ) index.minZ = z;
        if (z > index.maxZ) index.maxZ = z;
    }


    private void FinalizeFeatureSpatialIndexes()
    {
        if (featureSpatialIndexByUid == null || featureSpatialProfileByUid == null || featureCollectionMembers == null) return;

        featureSpatialProfileByUid.Clear();
        featureCollectionMembers.Clear();

        foreach (KeyValuePair<string, FeatureSpatialIndex> kv in featureSpatialIndexByUid)
        {
            FeatureSpatialIndex index = kv.Value;
            if (index == null) continue;

            FeatureSpatialProfile profile = BuildSpatialProfileFromIndex(index);
            featureSpatialProfileByUid[kv.Key] = profile;
        }

        if (featureUidsByCollectionKey == null) return;

        foreach (KeyValuePair<string, List<string>> kv in featureUidsByCollectionKey)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || kv.Value.Count == 0) continue;

            List<string> members = kv.Value
                .Where(uid => !string.IsNullOrWhiteSpace(uid))
                .Select(uid =>
                {
                    FeatureSpatialProfile profile = featureSpatialProfileByUid.TryGetValue(uid, out FeatureSpatialProfile found) ? found : null;
                    return profile != null ? SelectFeatureReferenceToken(profile.uid, profile.name, profile.runtimeAlias) : uid;
                })
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var profileCache = new Dictionary<string, FeatureSpatialProfile>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string token in members)
            {
                if (TryResolveFeatureSpatialIndex(token, out FeatureSpatialIndex idx))
                    profileCache[token] = BuildSpatialProfileFromIndex(idx);
            }

            members.Sort((a, b) =>
            {
                profileCache.TryGetValue(a, out FeatureSpatialProfile pa);
                profileCache.TryGetValue(b, out FeatureSpatialProfile pb);
                if (pa == null && pb == null) return StringComparer.OrdinalIgnoreCase.Compare(a, b);
                if (pa == null) return 1;
                if (pb == null) return -1;

                int byX = pa.centroidWorld.x.CompareTo(pb.centroidWorld.x);
                if (byX != 0) return byX;
                return pa.centroidWorld.z.CompareTo(pb.centroidWorld.z);
            });

            featureCollectionMembers[kv.Key] = members.ToArray();
        }
    }

    private void RegisterFeatureSpatialName(string uid, string featureName)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(featureName) || featureUidsByName == null) return;

        string key = featureName.Trim();
        if (!featureUidsByName.TryGetValue(key, out List<string> list))
        {
            list = new List<string>();
            featureUidsByName[key] = list;
        }

        if (!list.Any(existing => string.Equals(existing, uid, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(uid);
        }
    }

    private void RegisterCollectionMembership(string collectionKey, string uid)
    {
        if (string.IsNullOrWhiteSpace(collectionKey) || string.IsNullOrWhiteSpace(uid) || featureUidsByCollectionKey == null) return;

        if (!featureUidsByCollectionKey.TryGetValue(collectionKey, out List<string> list))
        {
            list = new List<string>();
            featureUidsByCollectionKey[collectionKey] = list;
        }

        if (!list.Any(existing => string.Equals(existing, uid, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(uid);
        }
    }

    private void RegisterFeatureAlias(Feature2D feature, int x, int z)
    {
        if (featureAliasCellMap == null || featureAliasUidMap == null || featureAliasNameMap == null || feature == null) return;
        if (string.IsNullOrWhiteSpace(feature.runtimeAlias)) return;

        string alias = feature.runtimeAlias.Trim();
        if (!featureAliasCellMap.ContainsKey(alias))
        {
            featureAliasCellMap[alias] = new Vector2Int(x, z);
        }
        if (!featureAliasUidMap.ContainsKey(alias))
        {
            featureAliasUidMap[alias] = string.IsNullOrWhiteSpace(feature.uid) ? string.Empty : feature.uid.Trim();
        }
        if (!featureAliasNameMap.ContainsKey(alias))
        {
            featureAliasNameMap[alias] = string.IsNullOrWhiteSpace(feature.effectiveName) ? string.Empty : feature.effectiveName.Trim();
        }
    }

    private static double SignedArea2D(List<Vector2> poly)
    {
        if (poly == null || poly.Count < 3) return 0.0;

        double area = 0.0;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i];
            Vector2 q = poly[(i + 1) % poly.Count];
            area += (double)p.x * q.y - (double)q.x * p.y;
        }

        return 0.5 * area;
    }

    private static void EnsureWinding(List<Vector2> ring, bool wantCCW)
    {
        if (ring == null || ring.Count < 3) return;
        bool isCCW = SignedArea2D(ring) > 0.0;
        if (isCCW != wantCCW) ring.Reverse();
    }

    private static void RemoveClosingDuplicate(List<Vector2> ring, float eps = 1e-4f)
    {
        if (ring == null || ring.Count < 2) return;
        if ((ring[0] - ring[ring.Count - 1]).sqrMagnitude <= eps * eps)
        {
            ring.RemoveAt(ring.Count - 1);
        }
    }

    private static void CleanRingInPlace(List<Vector2> ring, float dupEps, float collinearEps)
    {
        if (ring == null) return;

        RemoveClosingDuplicate(ring);

        List<Vector2> unique = new List<Vector2>(ring.Count);
        float dupEpsSq = dupEps * dupEps;
        for (int i = 0; i < ring.Count; i++)
        {
            if (unique.Count == 0 || (unique[unique.Count - 1] - ring[i]).sqrMagnitude > dupEpsSq)
            {
                unique.Add(ring[i]);
            }
        }

        ring.Clear();
        ring.AddRange(unique);
        RemoveCollinearPointsInPlace(ring, collinearEps);
        RemoveClosingDuplicate(ring);
    }

    private static void RemoveCollinearPointsInPlace(List<Vector2> points, float eps)
    {
        if (points == null || points.Count < 3) return;

        int count = points.Count;
        List<Vector2> filtered = new List<Vector2>(count);
        for (int i = 0; i < count; i++)
        {
            Vector2 a = points[(i - 1 + count) % count];
            Vector2 b = points[i];
            Vector2 c = points[(i + 1) % count];
            Vector2 ab = b - a;
            Vector2 ac = c - a;
            float cross = ab.x * ac.y - ab.y * ac.x;
            if (Mathf.Abs(cross) < eps) continue;
            filtered.Add(b);
        }

        if (filtered.Count >= 3)
        {
            points.Clear();
            points.AddRange(filtered);
        }
    }

    private static float Cross2(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static int Orient2D(Vector2 a, Vector2 b, Vector2 c, float eps = 1e-6f)
    {
        float v = Cross2(b - a, c - a);
        if (v > eps) return 1;
        if (v < -eps) return -1;
        return 0;
    }

    private static bool OnSegment2D(Vector2 a, Vector2 b, Vector2 p, float eps = 1e-6f)
    {
        return p.x >= Mathf.Min(a.x, b.x) - eps &&
               p.x <= Mathf.Max(a.x, b.x) + eps &&
               p.y >= Mathf.Min(a.y, b.y) - eps &&
               p.y <= Mathf.Max(a.y, b.y) + eps;
    }

    private static bool SegmentsIntersect2D(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        int o1 = Orient2D(a, b, c);
        int o2 = Orient2D(a, b, d);
        int o3 = Orient2D(c, d, a);
        int o4 = Orient2D(c, d, b);

        if (o1 != o2 && o3 != o4) return true;
        if (o1 == 0 && OnSegment2D(a, b, c)) return true;
        if (o2 == 0 && OnSegment2D(a, b, d)) return true;
        if (o3 == 0 && OnSegment2D(c, d, a)) return true;
        if (o4 == 0 && OnSegment2D(c, d, b)) return true;
        return false;
    }

    private static bool PointInPolygon2D(Vector2 p, List<Vector2> poly)
    {
        int n = poly.Count;
        if (n < 3) return false;

        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[j];
            bool intersect = ((a.y > p.y) != (b.y > p.y))
                && (p.x < (b.x - a.x) * (p.y - a.y) / ((b.y - a.y) + 1e-12f) + a.x);
            if (intersect) inside = !inside;
        }
        return inside;
    }

    private void AssignCellFeatureIdentity(int x, int z, string uid, string name)
    {
        if (cellFeatureUidGrid != null) cellFeatureUidGrid[x, z] = string.IsNullOrWhiteSpace(uid) ? null : uid.Trim();
        if (cellFeatureNameGrid != null) cellFeatureNameGrid[x, z] = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// 在 worldPos 附近 searchRadius 格半径内，找到最近有名字的特征格，返回其名称；找不到返回 null。
    /// </summary>
    public string TryGetNearestFeatureNameByWorld(Vector3 worldPos, int searchRadius = 5)
    {
        if (cellFeatureNameGrid == null) return null;
        Vector2Int center = WorldToGrid(worldPos);
        string best = null;
        int bestDist = int.MaxValue;
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dz = -searchRadius; dz <= searchRadius; dz++)
            {
                int nx = center.x + dx, nz = center.y + dz;
                if (!IsInBounds(nx, nz)) continue;
                string n = cellFeatureNameGrid[nx, nz];
                if (string.IsNullOrEmpty(n)) continue;
                int dist = dx * dx + dz * dz;
                if (dist < bestDist) { bestDist = dist; best = n; }
            }
        }
        return best;
    }

    private CampusGridCellType KindToCellType(string kind)
    {
        switch (kind)
        {
            case "building": return CampusGridCellType.Building;
            case "water": return CampusGridCellType.Water;
            case "road": return CampusGridCellType.Road;
            case "expressway": return CampusGridCellType.Expressway;
            case "bridge": return CampusGridCellType.Bridge;
            case "parking": return CampusGridCellType.Parking;
            case "green": return CampusGridCellType.Green;
            case "forest": return CampusGridCellType.Forest;
            case "sports": return CampusGridCellType.Sports;
            default: return CampusGridCellType.Other;
        }
    }

    private bool IsBlockedKind(CampusGridCellType t)
    {
        switch (t)
        {
            case CampusGridCellType.Building: return buildingBlocked;
            case CampusGridCellType.Water: return waterBlocked;
            case CampusGridCellType.Sports: return sportsBlocked;
            case CampusGridCellType.Forest: return forestBlocked;
            case CampusGridCellType.Road: return roadBlocked;
            case CampusGridCellType.Expressway: return expresswayBlocked;
            case CampusGridCellType.Bridge: return bridgeBlocked;
            case CampusGridCellType.Parking: return parkingBlocked;
            case CampusGridCellType.Other: return otherBlocked;
            default: return false;
        }
    }

    private static float Heuristic(Vector2Int a, Vector2Int b, bool diag)
    {
        int dx = Mathf.Abs(a.x - b.x);
        int dz = Mathf.Abs(a.y - b.y);
        // 曼哈顿启发值。
        if (!diag) return dx + dz;
        int min = Mathf.Min(dx, dz);
        int max = Mathf.Max(dx, dz);
        // 八方向近似最短路启发值。
        return 1.41421356f * min + (max - min);
    }

    private static bool IsDiagonal(Vector2Int a, Vector2Int b)
    {
        return a.x != b.x && a.y != b.y;
    }

    private static long PackCellKey(int x, int z)
    {
        return (((long)x) << 32) ^ (uint)z;
    }

    /// <summary>
    /// 构建建筑外围的路径安全边距缓存。
    /// 说明：这些格子并未真正被建筑占据，但对于局部避障来说过于贴墙，继续规划进去很容易出现“没碰撞却卡住”。
    /// </summary>
    private void BuildPathClearanceGrid()
    {
        pathClearanceBlockedGrid = new bool[gridWidth, gridLength];
        if (blockedGrid == null || cellTypeGrid == null) return;
        if (!buildingBlocked || buildingPathClearance <= 0.01f) return;

        int searchRadius = Mathf.Max(1, Mathf.CeilToInt(buildingPathClearance / Mathf.Max(0.1f, cellSize)));
        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                if (blockedGrid[x, z]) continue;
                pathClearanceBlockedGrid[x, z] = IsWithinBuildingClearance(x, z, searchRadius);
            }
        }
    }

    /// <summary>
    /// 判断候选格中心是否落在建筑的安全缓冲区内。
    /// 说明：用“格中心到建筑格矩形的最短距离”而不是简单看相邻关系，这样斜角贴墙也能被识别出来。
    /// </summary>
    private bool IsWithinBuildingClearance(int x, int z, int searchRadius)
    {
        Vector2 center = GetCellCenterXY(x, z);
        float clearanceSq = buildingPathClearance * buildingPathClearance;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dz = -searchRadius; dz <= searchRadius; dz++)
            {
                int nx = x + dx;
                int nz = z + dz;
                if (!IsInBounds(nx, nz)) continue;
                if (cellTypeGrid[nx, nz] != CampusGridCellType.Building) continue;

                Rect buildingRect = GetCellRectXY(nx, nz);
                float closestX = Mathf.Clamp(center.x, buildingRect.xMin, buildingRect.xMax);
                float closestZ = Mathf.Clamp(center.y, buildingRect.yMin, buildingRect.yMax);
                float distSq = (center - new Vector2(closestX, closestZ)).sqrMagnitude;
                if (distSq < clearanceSq)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsPathWalkable(int x, int z, HashSet<long> transientBlockedKeys)
    {
        if (!IsWalkable(x, z)) return false;
        if (pathClearanceBlockedGrid != null && IsInBounds(x, z) && pathClearanceBlockedGrid[x, z]) return false;
        return transientBlockedKeys == null || !transientBlockedKeys.Contains(PackCellKey(x, z));
    }

    private bool CanTraverseDiagonal(Vector2Int from, Vector2Int to, HashSet<long> transientBlockedKeys = null)
    {
        if (!IsDiagonal(from, to)) return true;

        Vector2Int sideA = new Vector2Int(from.x, to.y);
        Vector2Int sideB = new Vector2Int(to.x, from.y);
        return IsPathWalkable(sideA.x, sideA.y, transientBlockedKeys) || IsPathWalkable(sideB.x, sideB.y, transientBlockedKeys);
    }

    private bool HasDirectLineOfSight(Vector2Int from, Vector2Int to)
    {
        int x0 = from.x;
        int z0 = from.y;
        int x1 = to.x;
        int z1 = to.y;

        int dx = Mathf.Abs(x1 - x0);
        int dz = Mathf.Abs(z1 - z0);
        int sx = x0 < x1 ? 1 : -1;
        int sz = z0 < z1 ? 1 : -1;
        int err = dx - dz;

        while (true)
        {
            if (!IsPathWalkable(x0, z0, null)) return false;
            if (x0 == x1 && z0 == z1) return true;

            int e2 = err * 2;
            int nextX = x0;
            int nextZ = z0;
            bool movedX = false;
            bool movedZ = false;

            if (e2 > -dz)
            {
                err -= dz;
                nextX += sx;
                movedX = true;
            }
            if (e2 < dx)
            {
                err += dx;
                nextZ += sz;
                movedZ = true;
            }

            if (!IsInBounds(nextX, nextZ) || !IsPathWalkable(nextX, nextZ, null)) return false;
            if (movedX && movedZ && !CanTraverseDiagonal(new Vector2Int(x0, z0), new Vector2Int(nextX, nextZ))) return false;

            x0 = nextX;
            z0 = nextZ;
        }
    }

    private void SimplifyAStarPathInPlace(List<Vector2Int> path)
    {
        if (path == null || path.Count <= 2) return;

        List<Vector2Int> simplified = new List<Vector2Int>(path.Count);
        int anchor = 0;
        simplified.Add(path[anchor]);

        while (anchor < path.Count - 1)
        {
            int furthestVisible = anchor + 1;
            for (int candidate = path.Count - 1; candidate > anchor + 1; candidate--)
            {
                if (HasDirectLineOfSight(path[anchor], path[candidate]))
                {
                    furthestVisible = candidate;
                    break;
                }
            }

            simplified.Add(path[furthestVisible]);
            anchor = furthestVisible;
        }

        path.Clear();
        path.AddRange(simplified);
    }

    private void AddNeighbors(Vector2Int c, bool diag, out Vector2Int[] neighbors, out float[] costs)
    {
        if (!diag)
        {
            neighbors = new[]
            {
                new Vector2Int(c.x + 1, c.y),
                new Vector2Int(c.x - 1, c.y),
                new Vector2Int(c.x, c.y + 1),
                new Vector2Int(c.x, c.y - 1),
            };
            costs = new[] { 1f, 1f, 1f, 1f };
            return;
        }

        neighbors = new[]
        {
            new Vector2Int(c.x + 1, c.y),
            new Vector2Int(c.x - 1, c.y),
            new Vector2Int(c.x, c.y + 1),
            new Vector2Int(c.x, c.y - 1),
            new Vector2Int(c.x + 1, c.y + 1),
            new Vector2Int(c.x + 1, c.y - 1),
            new Vector2Int(c.x - 1, c.y + 1),
            new Vector2Int(c.x - 1, c.y - 1),
        };

        costs = new[] { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };
    }

    private static void ReconstructPath(
        Vector2Int current,
        Vector2Int start,
        Vector2Int[,] cameFrom,
        bool[,] hasParent,
        List<Vector2Int> outPath
    )
    {
        outPath.Clear();
        outPath.Add(current);

        while (current != start)
        {
            if (!hasParent[current.x, current.y]) break;
            current = cameFrom[current.x, current.y];
            outPath.Add(current);
        }

        outPath.Reverse();
    }

    private static void DestroyImmediateSafe(UnityEngine.Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }
}

