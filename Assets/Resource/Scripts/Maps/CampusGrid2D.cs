using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// 把 CampusJsonMapLoader 使用的真实校园 JSON 转成二维逻辑网格（XZ 平面），用于多智能体路径规划实验。
/// 重点：
/// 1) 只做 2D 网格，不做 3D 体素。
/// 2) 网格可视化可开关。
/// 3) 提供 A* 寻路、世界坐标<->网格坐标转换。
/// </summary>
[ExecuteAlways]
public class CampusGrid2D : MonoBehaviour
{
    public enum CellType : byte
    {
        Free = 0,
        Building = 1,
        Water = 2,
        Road = 3,
        Expressway = 4,
        Bridge = 5,
        Parking = 6,
        Green = 7,
        Forest = 8,
        Sports = 9,
        Other = 10,
    }

    [Serializable]
    private class Feature2D
    {
        public string uid = "";
        public string name = "";
        public string kind = "other";
        public Rect bounds;
        public bool boundsValid;
        public readonly List<List<Vector2>> outerRings = new List<List<Vector2>>();
        public readonly List<List<Vector2>> innerRings = new List<List<Vector2>>();
        public readonly List<Vector2> linePoints = new List<Vector2>();

        public bool HasArea => outerRings.Count > 0;
        public bool HasLine => linePoints.Count >= 2;
    }

    [Header("数据来源")]
    public CampusJsonMapLoader campusLoader;

    [Header("网格参数")]
    [Min(0.2f)] public float cellSize = 2f;
    [Min(0f)] public float mapMargin = 2f;
    public bool autoBuildOnStart = true;
    public bool allowDiagonal = false;

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

    [Header("点击查询(运行时)")]
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
    [NonSerialized] public CellType[,] cellTypeGrid;
    [NonSerialized] public string[,] cellFeatureUidGrid;
    [NonSerialized] public string[,] cellFeatureNameGrid;
    [NonSerialized] public Rect mapBoundsXY;

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

        BuildLogicalGrid(features, allBounds);

        if (showGrid) RebuildVisualization();
        else ClearVisualizationOnly();
    }

    [ContextMenu("Rebuild Grid Visualization")]
    public void RebuildVisualization()
    {
        if (blockedGrid == null || cellTypeGrid == null)
        {
            Debug.LogWarning("[CampusGrid2D] 还没有逻辑网格，请先 Build Grid。");
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

    public CellType GetCellType(int x, int z)
    {
        if (!IsInBounds(x, z)) return CellType.Other;
        return cellTypeGrid[x, z];
    }

    public string GetCellFeatureUid(int x, int z)
    {
        if (!IsInBounds(x, z) || cellFeatureUidGrid == null) return string.Empty;
        return cellFeatureUidGrid[x, z] ?? string.Empty;
    }

    public string GetCellFeatureName(int x, int z)
    {
        if (!IsInBounds(x, z) || cellFeatureNameGrid == null) return string.Empty;
        return cellFeatureNameGrid[x, z] ?? string.Empty;
    }

    public bool TryGetCellFeatureInfo(int x, int z, out string uid, out string name, out CellType type, out bool blocked)
    {
        uid = string.Empty;
        name = string.Empty;
        type = CellType.Other;
        blocked = false;

        if (!IsInBounds(x, z) || blockedGrid == null || cellTypeGrid == null) return false;

        type = cellTypeGrid[x, z];
        blocked = blockedGrid[x, z];
        if (cellFeatureUidGrid != null) uid = cellFeatureUidGrid[x, z] ?? string.Empty;
        if (cellFeatureNameGrid != null) name = cellFeatureNameGrid[x, z] ?? string.Empty;

        return true;
    }

    public bool TryGetCellFeatureInfoByWorld(Vector3 worldPos, out Vector2Int grid, out string uid, out string name, out CellType type, out bool blocked)
    {
        grid = WorldToGrid(worldPos);
        if (!IsInBounds(grid.x, grid.y))
        {
            uid = string.Empty;
            name = string.Empty;
            type = CellType.Other;
            blocked = false;
            return false;
        }

        return TryGetCellFeatureInfo(grid.x, grid.y, out uid, out name, out type, out blocked);
    }

    public List<Vector2Int> GetCellsByFeatureName(string featureName, bool ignoreCase = true)
    {
        var cells = new List<Vector2Int>();
        if (string.IsNullOrWhiteSpace(featureName) || cellFeatureNameGrid == null) return cells;

        StringComparison comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                string n = cellFeatureNameGrid[x, z];
                if (!string.IsNullOrEmpty(n) && string.Equals(n, featureName, comp))
                {
                    cells.Add(new Vector2Int(x, z));
                }
            }
        }
        return cells;
    }

    public bool TryGetFeatureFirstCell(string featureName, out Vector2Int cell, bool preferWalkable = false, bool ignoreCase = true)
    {
        cell = new Vector2Int(-1, -1);
        if (string.IsNullOrWhiteSpace(featureName) || cellFeatureNameGrid == null) return false;

        StringComparison comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Vector2Int firstHit = new Vector2Int(-1, -1);

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                string n = cellFeatureNameGrid[x, z];
                if (string.IsNullOrEmpty(n) || !string.Equals(n, featureName, comp)) continue;

                if (firstHit.x < 0) firstHit = new Vector2Int(x, z);
                if (!preferWalkable || IsWalkable(x, z))
                {
                    cell = new Vector2Int(x, z);
                    return true;
                }
            }
        }

        if (firstHit.x >= 0)
        {
            cell = firstHit;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 按 feature uid 查找首个网格。
    /// </summary>
    public bool TryGetFeatureFirstCellByUid(string featureUid, out Vector2Int cell, bool preferWalkable = false, bool ignoreCase = true)
    {
        cell = new Vector2Int(-1, -1);
        if (string.IsNullOrWhiteSpace(featureUid) || cellFeatureUidGrid == null) return false;

        StringComparison comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Vector2Int firstHit = new Vector2Int(-1, -1);

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                string uid = cellFeatureUidGrid[x, z];
                if (string.IsNullOrEmpty(uid) || !string.Equals(uid, featureUid, comp)) continue;

                if (firstHit.x < 0) firstHit = new Vector2Int(x, z);
                if (!preferWalkable || IsWalkable(x, z))
                {
                    cell = new Vector2Int(x, z);
                    return true;
                }
            }
        }

        if (firstHit.x >= 0)
        {
            cell = firstHit;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 统一解析地点查询（优先精确，再到规范化匹配），同时支持 name 与 uid。
    /// </summary>
    public bool TryResolveFeatureCell(string query, out Vector2Int cell, out string matchedUid, out string matchedName, bool preferWalkable = true, bool ignoreCase = true)
    {
        cell = new Vector2Int(-1, -1);
        matchedUid = string.Empty;
        matchedName = string.Empty;
        if (string.IsNullOrWhiteSpace(query)) return false;

        string q = query.Trim();
        if (q.StartsWith("building:", StringComparison.OrdinalIgnoreCase)) q = q.Substring("building:".Length).Trim();
        else if (q.StartsWith("feature:", StringComparison.OrdinalIgnoreCase)) q = q.Substring("feature:".Length).Trim();
        if (string.IsNullOrWhiteSpace(q)) return false;

        // 1) 精确 name
        if (TryGetFeatureFirstCell(q, out cell, preferWalkable, ignoreCase))
        {
            TryGetCellFeatureInfo(cell.x, cell.y, out matchedUid, out matchedName, out _, out _);
            return true;
        }

        // 2) 精确 uid
        if (TryGetFeatureFirstCellByUid(q, out cell, preferWalkable, ignoreCase))
        {
            TryGetCellFeatureInfo(cell.x, cell.y, out matchedUid, out matchedName, out _, out _);
            return true;
        }

        if (cellFeatureNameGrid == null && cellFeatureUidGrid == null) return false;

        string normalizedQ = NormalizeFeatureToken(q);
        bool hasQueryBuildingId = TryExtractBuildingId(q, out string queryBuildingId);
        int bestScore = int.MaxValue;
        Vector2Int bestCell = new Vector2Int(-1, -1);
        string bestUid = string.Empty;
        string bestName = string.Empty;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                string uid = cellFeatureUidGrid != null ? (cellFeatureUidGrid[x, z] ?? string.Empty) : string.Empty;
                string name = cellFeatureNameGrid != null ? (cellFeatureNameGrid[x, z] ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(uid) && string.IsNullOrWhiteSpace(name)) continue;

                string key = $"{uid}|{name}";
                if (!visited.Add(key)) continue;

                if (preferWalkable && !IsWalkable(x, z))
                {
                    // 仍允许候选进入评分，但给较大惩罚，避免全部是 blocked 时完全找不到。
                }

                string nu = NormalizeFeatureToken(uid);
                string nn = NormalizeFeatureToken(name);
                bool exactNormalized = (!string.IsNullOrEmpty(nu) && nu == normalizedQ) || (!string.IsNullOrEmpty(nn) && nn == normalizedQ);

                bool idMatched = false;
                if (hasQueryBuildingId)
                {
                    bool uidHasId = TryExtractBuildingId(uid, out string uidId) && uidId == queryBuildingId;
                    bool nameHasId = TryExtractBuildingId(name, out string nameId) && nameId == queryBuildingId;
                    idMatched = uidHasId || nameHasId;
                }

                bool contains = (!string.IsNullOrEmpty(uid) && uid.IndexOf(q, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0) ||
                                (!string.IsNullOrEmpty(name) && name.IndexOf(q, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0);

                if (!exactNormalized && !idMatched && !contains) continue;

                int score = 1000;
                if (exactNormalized) score = 0;
                else if (idMatched) score = 10;
                else if (contains) score = 50;

                if (!IsWalkable(x, z)) score += 200;
                score += Math.Abs((name ?? string.Empty).Length - q.Length);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestCell = new Vector2Int(x, z);
                    bestUid = uid;
                    bestName = name;
                }
            }
        }

        if (bestCell.x < 0) return false;
        cell = bestCell;
        matchedUid = bestUid ?? string.Empty;
        matchedName = bestName ?? string.Empty;
        return true;
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
            if (TryGetCellFeatureInfoByWorld(worldPoint, out Vector2Int grid, out string uid, out string name, out CellType type, out bool blocked))
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
        found = new Vector2Int(-1, -1);
        if (blockedGrid == null) return false;

        if (IsWalkable(from.x, from.y))
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

                if (IsWalkable(a.x, a.y)) { found = a; return true; }
                if (IsWalkable(b.x, b.y)) { found = b; return true; }
            }
        }
        return false;
    }

    /// <summary>
    /// A* 寻路：返回从 start 到 goal 的网格路径（包含起点和终点）。
    /// </summary>
    public List<Vector2Int> FindPathAStar(Vector2Int start, Vector2Int goal, bool? useDiagonalOverride = null)
    {
        var path = new List<Vector2Int>();
        if (blockedGrid == null) return path;

        bool useDiagonal = useDiagonalOverride ?? allowDiagonal;

        if (!IsInBounds(start.x, start.y) || !IsInBounds(goal.x, goal.y)) return path;
        if (!IsWalkable(start.x, start.y) || !IsWalkable(goal.x, goal.y)) return path;

        float[,] gScore = new float[gridWidth, gridLength];
        bool[,] closed = new bool[gridWidth, gridLength];
        Vector2Int[,] cameFrom = new Vector2Int[gridWidth, gridLength];
        bool[,] hasParent = new bool[gridWidth, gridLength];

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                gScore[x, z] = float.PositiveInfinity;
            }
        }

        var openSet = new List<Vector2Int>(256);
        gScore[start.x, start.y] = 0f;
        openSet.Add(start);

        while (openSet.Count > 0)
        {
            int bestIndex = 0;
            float bestF = FScore(openSet[0], goal, gScore, useDiagonal);

            for (int i = 1; i < openSet.Count; i++)
            {
                float f = FScore(openSet[i], goal, gScore, useDiagonal);
                if (f < bestF)
                {
                    bestF = f;
                    bestIndex = i;
                }
            }

            Vector2Int current = openSet[bestIndex];
            openSet.RemoveAt(bestIndex);

            if (current == goal)
            {
                ReconstructPath(current, start, cameFrom, hasParent, path);
                return path;
            }

            if (closed[current.x, current.y]) continue;
            closed[current.x, current.y] = true;

            AddNeighbors(current, useDiagonal, out Vector2Int[] neighbors, out float[] moveCost);

            for (int i = 0; i < neighbors.Length; i++)
            {
                Vector2Int n = neighbors[i];
                if (!IsInBounds(n.x, n.y)) continue;
                if (!IsWalkable(n.x, n.y)) continue;
                if (closed[n.x, n.y]) continue;

                // 对角移动时，禁止从两个障碍角之间“穿角”。
                if (useDiagonal && IsDiagonal(current, n))
                {
                    Vector2Int sideA = new Vector2Int(current.x, n.y);
                    Vector2Int sideB = new Vector2Int(n.x, current.y);
                    if (!IsWalkable(sideA.x, sideA.y) && !IsWalkable(sideB.x, sideB.y))
                        continue;
                }

                float tentative = gScore[current.x, current.y] + moveCost[i];
                if (tentative < gScore[n.x, n.y])
                {
                    gScore[n.x, n.y] = tentative;
                    cameFrom[n.x, n.y] = current;
                    hasParent[n.x, n.y] = true;
                    if (!openSet.Contains(n)) openSet.Add(n);
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
        cellTypeGrid = new CellType[gridWidth, gridLength];
        cellFeatureUidGrid = new string[gridWidth, gridLength];
        cellFeatureNameGrid = new string[gridWidth, gridLength];

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridLength; z++)
            {
                blockedGrid[x, z] = false;
                cellTypeGrid[x, z] = CellType.Free;
                cellFeatureUidGrid[x, z] = null;
                cellFeatureNameGrid[x, z] = null;
            }
        }

        float roadHalfWidth = 1f;
        if (campusLoader != null) roadHalfWidth = Mathf.Max(0.5f, campusLoader.strokeWidthM * 0.5f);

        for (int i = 0; i < features.Count; i++)
        {
            Feature2D f = features[i];
            CellType t = KindToCellType(f.kind);
            bool shouldBlock = IsBlockedKind(t);

            if (!f.boundsValid) continue;

            BoundsToGridRange(f.bounds, out int xMin, out int xMax, out int zMin, out int zMax);
            if (xMax < xMin || zMax < zMin) continue;

            for (int x = xMin; x <= xMax; x++)
            {
                for (int z = zMin; z <= zMax; z++)
                {
                    Vector2 c = GetCellCenterXY(x, z);
                    bool hit = false;

                    if (f.HasArea)
                    {
                        hit = PointInMultiPolygonWithHoles(c, f.outerRings, f.innerRings);
                    }
                    else if (f.HasLine)
                    {
                        hit = DistPointToPolyline(c, f.linePoints) <= roadHalfWidth;
                    }
                    else
                    {
                        hit = f.bounds.Contains(c);
                    }

                    if (!hit) continue;

                    if (shouldBlock)
                    {
                        blockedGrid[x, z] = true;
                        cellTypeGrid[x, z] = t;
                        AssignCellFeatureIdentity(x, z, f.uid, f.name);
                    }
                    else
                    {
                        if (!blockedGrid[x, z])
                        {
                            cellTypeGrid[x, z] = t;
                            AssignCellFeatureIdentity(x, z, f.uid, f.name);
                        }
                    }
                }
            }
        }

        if (logBuildSummary)
        {
            int blockedCount = 0;
            for (int x = 0; x < gridWidth; x++)
                for (int z = 0; z < gridLength; z++)
                    if (blockedGrid[x, z]) blockedCount++;

            Debug.Log($"[CampusGrid2D] 构建完成: {gridWidth}x{gridLength}, cell={cellSize}m, 阻塞={blockedCount}/{gridWidth * gridLength}");
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
        block.SetColor("_BaseColor", c); // URP Lit 使用 _BaseColor
        r.SetPropertyBlock(block);

        visualCells.Add(cell);
    }

    private Color GetCellColor(int x, int z)
    {
        if (blockedGrid[x, z])
        {
            CellType t = cellTypeGrid[x, z];
            if (t == CellType.Building) return buildingColor;
            if (t == CellType.Water) return waterColor;
            return blockedColor;
        }

        CellType type = cellTypeGrid[x, z];
        if (type == CellType.Road || type == CellType.Expressway || type == CellType.Bridge) return roadColor;
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
            Rect rb = default;
            Rect lb = default;

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

            if (!f.boundsValid) continue;

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
        xMin = Mathf.FloorToInt((b.xMin - mapBoundsXY.xMin) / cellSize);
        xMax = Mathf.FloorToInt((b.xMax - mapBoundsXY.xMin) / cellSize);
        zMin = Mathf.FloorToInt((b.yMin - mapBoundsXY.yMin) / cellSize);
        zMax = Mathf.FloorToInt((b.yMax - mapBoundsXY.yMin) / cellSize);

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
        bounds = default;
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
        bounds = default;
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

    /// <summary>
    /// 归一化地点标识：统一大小写并移除空白、下划线、连字符，便于模糊对齐。
    /// </summary>
    private static string NormalizeFeatureToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim().ToLowerInvariant();
        s = s.Replace(" ", string.Empty)
             .Replace("_", string.Empty)
             .Replace("-", string.Empty)
             .Replace("：", ":");
        return s;
    }

    /// <summary>
    /// 从文本中提取建筑编号（如 building_12 / 楼12 / 建筑-12）。
    /// </summary>
    private static bool TryExtractBuildingId(string text, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:building|楼|建筑)?\s*[_\-:：]?\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (!m.Success) return false;

        id = m.Groups[1].Value?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(id);
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

    private static bool PointInMultiPolygonWithHoles(Vector2 p, List<List<Vector2>> outers, List<List<Vector2>> inners)
    {
        bool inOuter = false;
        for (int i = 0; i < outers.Count; i++)
        {
            List<Vector2> ring = outers[i];
            if (ring != null && ring.Count >= 3 && PointInPolygon2D(p, ring))
            {
                inOuter = true;
                break;
            }
        }
        if (!inOuter) return false;

        for (int i = 0; i < inners.Count; i++)
        {
            List<Vector2> hole = inners[i];
            if (hole != null && hole.Count >= 3 && PointInPolygon2D(p, hole))
                return false;
        }
        return true;
    }

    private static float DistPointToSegment2D(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-12f) return (p - a).magnitude;

        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        Vector2 q = a + t * ab;
        return (p - q).magnitude;
    }

    private static float DistPointToPolyline(Vector2 p, List<Vector2> line)
    {
        float best = float.PositiveInfinity;
        for (int i = 0; i < line.Count - 1; i++)
        {
            float d = DistPointToSegment2D(p, line[i], line[i + 1]);
            if (d < best) best = d;
        }
        return best;
    }

    private void AssignCellFeatureIdentity(int x, int z, string uid, string name)
    {
        if (cellFeatureUidGrid != null) cellFeatureUidGrid[x, z] = string.IsNullOrWhiteSpace(uid) ? null : uid.Trim();
        if (cellFeatureNameGrid != null) cellFeatureNameGrid[x, z] = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private CellType KindToCellType(string kind)
    {
        switch (kind)
        {
            case "building": return CellType.Building;
            case "water": return CellType.Water;
            case "road": return CellType.Road;
            case "expressway": return CellType.Expressway;
            case "bridge": return CellType.Bridge;
            case "parking": return CellType.Parking;
            case "green": return CellType.Green;
            case "forest": return CellType.Forest;
            case "sports": return CellType.Sports;
            default: return CellType.Other;
        }
    }

    private bool IsBlockedKind(CellType t)
    {
        switch (t)
        {
            case CellType.Building: return buildingBlocked;
            case CellType.Water: return waterBlocked;
            case CellType.Sports: return sportsBlocked;
            case CellType.Forest: return forestBlocked;
            case CellType.Road: return roadBlocked;
            case CellType.Expressway: return expresswayBlocked;
            case CellType.Bridge: return bridgeBlocked;
            case CellType.Parking: return parkingBlocked;
            case CellType.Other: return otherBlocked;
            default: return false;
        }
    }

    private static float Heuristic(Vector2Int a, Vector2Int b, bool diag)
    {
        int dx = Mathf.Abs(a.x - b.x);
        int dz = Mathf.Abs(a.y - b.y);
        if (!diag) return dx + dz; // 曼哈顿
        int min = Mathf.Min(dx, dz);
        int max = Mathf.Max(dx, dz);
        return 1.41421356f * min + (max - min); // 八方向近似最短
    }

    private static bool IsDiagonal(Vector2Int a, Vector2Int b)
    {
        return a.x != b.x && a.y != b.y;
    }

    private static float FScore(Vector2Int node, Vector2Int goal, float[,] gScore, bool diag)
    {
        return gScore[node.x, node.y] + Heuristic(node, goal, diag);
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
