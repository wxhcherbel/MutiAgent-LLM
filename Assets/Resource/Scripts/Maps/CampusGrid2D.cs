using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// 鎶?CampusJsonMapLoader 浣跨敤鐨勭湡瀹炴牎鍥?JSON 杞垚浜岀淮閫昏緫缃戞牸锛圶Z 骞抽潰锛夛紝鐢ㄤ簬澶氭櫤鑳戒綋璺緞瑙勫垝瀹為獙銆?/// 閲嶇偣锛?/// 1) 鍙仛 2D 缃戞牸锛屼笉鍋?3D 浣撶礌銆?/// 2) 缃戞牸鍙鍖栧彲寮€鍏炽€?/// 3) 鎻愪緵 A* 瀵昏矾銆佷笘鐣屽潗鏍?->缃戞牸鍧愭爣杞崲銆?/// </summary>
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
        public string effectiveName = "";
        public string runtimeAlias = "";
        public Rect bounds;
        public bool boundsValid;
        public readonly List<List<Vector2>> outerRings = new List<List<Vector2>>();
        public readonly List<List<Vector2>> innerRings = new List<List<Vector2>>();
        public readonly List<Vector2> linePoints = new List<Vector2>();
        public readonly List<RasterAreaPart> rasterAreaParts = new List<RasterAreaPart>();
        public readonly List<RasterStrokeQuad> rasterStrokeQuads = new List<RasterStrokeQuad>();
        public Rect rasterBounds;
        public bool rasterBoundsValid;
        public FeatureRasterMode rasterMode = FeatureRasterMode.Bounds;

        public bool HasArea => outerRings.Count > 0;
        public bool HasLine => linePoints.Count >= 2;
        public bool HasRasterArea => rasterAreaParts.Count > 0;
        public bool HasRasterLine => rasterStrokeQuads.Count > 0;
    }

    private enum FeatureRasterMode : byte
    {
        Bounds = 0,
        Area = 1,
        Line = 2,
    }

    private class RasterAreaPart
    {
        public readonly List<Vector2> outer = new List<Vector2>();
        public readonly List<List<Vector2>> holes = new List<List<Vector2>>();
        public Rect bounds;
        public bool boundsValid;
    }

    private class RasterStrokeQuad
    {
        public readonly List<Vector2> points = new List<Vector2>(4);
        public Rect bounds;
        public bool boundsValid;
    }

    /// <summary>
    /// 鍦板浘涓栫晫閲屸€滀竴涓ぇ鑺傜偣瀹炰綋鈥濈殑绌洪棿妗ｆ銆?    /// 杩欓噷涓嶅啀鎶?building / forest / road 鍙湅鎴愪竴涓偣锛岃€屾槸鏄庣‘璁板綍锛?    /// 1) 瀹冨崰浜嗗摢浜涙牸瀛愶紱
    /// 2) 瀹冪殑澶ц嚧涓績鍦ㄥ摢閲岋紱
    /// 3) 浠庡綋鍓嶅弬鑰冧綅缃嚭鍙戯紝搴旇璐寸潃瀹冪殑鍝竴渚у幓鎺ヨ繎锛?    /// 4) 濡傛灉瑕佺幆缁曞畠锛屽缓璁崐寰勫ぇ姒傛槸澶氬皯銆?    ///
    /// 杩欐牱 ActionDecisionModule 鍚庨潰鎷垮埌鐨勫氨涓嶅啀鍙槸鈥滀竴涓潗鏍囩偣鈥濓紝
    /// 鑰屾槸鈥滀竴涓湡姝ｆ湁鑼冨洿鐨勪笘鐣屽疄浣撯€濄€?    /// </summary>
    [Serializable]
    public class FeatureSpatialProfile
    {
        public string uid = "";
        public string name = "";
        public string runtimeAlias = "";
        public string kind = "other";
        public string collectionKey = "";
        public CellType cellType = CellType.Other;
        public int occupiedCellCount;
        public int minX;
        public int maxX;
        public int minZ;
        public int maxZ;
        public Vector2 centroidGrid;
        public Vector2Int centroidCell = new Vector2Int(-1, -1);
        public Vector3 centroidWorld;
        public Vector2Int anchorCell = new Vector2Int(-1, -1);
        public Vector3 anchorWorld;
        public float footprintRadius;
        public string[] memberEntityIds = Array.Empty<string>();
    }

    /// <summary>
    /// 缃戞牸鍐呴儴浣跨敤鐨勭┖闂寸储寮曘€?    /// 瀹冧繚鐣欏畬鏁存牸瀛愬垪琛紝渚夸簬鍚庨潰鍔ㄦ€佺畻鈥滄帴杩戠偣鈥濃€滆鐩栧垝鍒嗏€濃€滅幆缁曞崐寰勨€濄€?    /// </summary>
    private class FeatureSpatialIndex
    {
        public string uid = "";
        public string name = "";
        public string runtimeAlias = "";
        public string kind = "other";
        public string collectionKey = "";
        public CellType cellType = CellType.Other;
        public readonly List<Vector2Int> occupiedCells = new List<Vector2Int>();
        public readonly HashSet<long> occupiedCellKeys = new HashSet<long>();
        public int minX = int.MaxValue;
        public int maxX = int.MinValue;
        public int minZ = int.MaxValue;
        public int maxZ = int.MinValue;
    }

    private struct AStarHeapEntry
    {
        public Vector2Int Cell;
        public float FScore;
        public float HScore;
    }

    private sealed class AStarMinHeap
    {
        private readonly List<AStarHeapEntry> entries = new List<AStarHeapEntry>(256);

        public int Count => entries.Count;

        public void Push(AStarHeapEntry entry)
        {
            entries.Add(entry);
            SiftUp(entries.Count - 1);
        }

        public AStarHeapEntry Pop()
        {
            int lastIndex = entries.Count - 1;
            AStarHeapEntry root = entries[0];
            AStarHeapEntry tail = entries[lastIndex];
            entries.RemoveAt(lastIndex);

            if (entries.Count > 0)
            {
                entries[0] = tail;
                SiftDown(0);
            }

            return root;
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (Compare(entries[index], entries[parent]) >= 0) break;
                Swap(index, parent);
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            int count = entries.Count;
            while (true)
            {
                int left = index * 2 + 1;
                if (left >= count) break;

                int right = left + 1;
                int smallest = left;
                if (right < count && Compare(entries[right], entries[left]) < 0)
                {
                    smallest = right;
                }

                if (Compare(entries[smallest], entries[index]) >= 0) break;
                Swap(index, smallest);
                index = smallest;
            }
        }

        private static int Compare(AStarHeapEntry a, AStarHeapEntry b)
        {
            int f = a.FScore.CompareTo(b.FScore);
            if (f != 0) return f;

            int h = a.HScore.CompareTo(b.HScore);
            if (h != 0) return h;

            int x = a.Cell.x.CompareTo(b.Cell.x);
            if (x != 0) return x;

            return a.Cell.y.CompareTo(b.Cell.y);
        }

        private void Swap(int a, int b)
        {
            AStarHeapEntry temp = entries[a];
            entries[a] = entries[b];
            entries[b] = temp;
        }
    }

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
    [NonSerialized] public CellType[,] cellTypeGrid;
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
        if (string.IsNullOrWhiteSpace(featureName)) return false;

        if (!TryResolveFeatureSpatialIndex(featureName, out FeatureSpatialIndex idx, ignoreCase)
            || idx == null || idx.occupiedCells == null || idx.occupiedCells.Count == 0)
            return false;

        if (preferWalkable)
        {
            foreach (Vector2Int c in idx.occupiedCells)
            {
                if (IsWalkable(c.x, c.y)) { cell = c; return true; }
            }
        }
        cell = idx.occupiedCells[0];
        return true;
    }

    /// <summary>
    /// 鎸?feature uid 鏌ユ壘棣栦釜缃戞牸銆?    /// </summary>
    public bool TryGetFeatureFirstCellByUid(string featureUid, out Vector2Int cell, bool preferWalkable = false, bool ignoreCase = true)
    {
        cell = new Vector2Int(-1, -1);
        if (string.IsNullOrWhiteSpace(featureUid) || featureSpatialIndexByUid == null) return false;

        if (!featureSpatialIndexByUid.TryGetValue(featureUid.Trim(), out FeatureSpatialIndex idx)
            || idx == null || idx.occupiedCells == null || idx.occupiedCells.Count == 0)
            return false;

        if (preferWalkable)
        {
            foreach (Vector2Int c in idx.occupiedCells)
            {
                if (IsWalkable(c.x, c.y)) { cell = c; return true; }
            }
        }
        cell = idx.occupiedCells[0];
        return true;
    }

    /// <summary>
    /// 缁熶竴瑙ｆ瀽鍦扮偣鏌ヨ锛堜紭鍏堢簿纭紝鍐嶅埌瑙勮寖鍖栧尮閰嶏級锛屽悓鏃舵敮鎸?name 涓?uid銆?    /// </summary>
    public bool TryResolveFeatureCell(string query, out Vector2Int cell, out string matchedUid, out string matchedName, bool preferWalkable = true, bool ignoreCase = true)
    {
        cell = new Vector2Int(-1, -1);
        matchedUid = string.Empty;
        matchedName = string.Empty;
        if (string.IsNullOrWhiteSpace(query)) return false;

        string q = query.Trim();
        TryStripStructuredTargetPrefix(q, out _, out q);
        if (string.IsNullOrWhiteSpace(q)) return false;

        // 1) 绮剧‘ name
        if (TryGetFeatureFirstCell(q, out cell, preferWalkable, ignoreCase))
        {
            TryGetCellFeatureInfo(cell.x, cell.y, out matchedUid, out matchedName, out _, out _);
            return true;
        }

        // 2) 绮剧‘ uid
        if (TryGetFeatureFirstCellByUid(q, out cell, preferWalkable, ignoreCase))
        {
            TryGetCellFeatureInfo(cell.x, cell.y, out matchedUid, out matchedName, out _, out _);
            return true;
        }

        // 3) 精确 runtime alias
        // alias 来自 CampusJsonMapLoader 为 Unity 场景对象生成的实例别名，
        // 例如 building_7、a_3、forest_2。
        // 它不直接存放在 cellFeatureNameGrid 中，所以要先翻译回真实 uid/name 再回到网格。
        if (TryResolveFeatureAliasCell(q, out cell, out matchedUid, out matchedName, preferWalkable, ignoreCase))
        {
            return true;
        }

        if (cellFeatureNameGrid == null && cellFeatureUidGrid == null) return false;

        if (TryResolveFeatureAliasCellByNormalized(q, out cell, out matchedUid, out matchedName, preferWalkable, ignoreCase))
        {
            return true;
        }

        string normalizedQ = NormalizeFeatureToken(q);
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
                    // 即便当前单元不可通行，也允许它进入候选评分；
                    // 这里只是在后面的 score 上附加更大的惩罚，避免全是 blocked 时完全找不到目标。
                }

                string nu = NormalizeFeatureToken(uid);
                string nn = NormalizeFeatureToken(name);
                bool exactNormalized = (!string.IsNullOrEmpty(nu) && nu == normalizedQ) || (!string.IsNullOrEmpty(nn) && nn == normalizedQ);

                bool contains = (!string.IsNullOrEmpty(uid) && uid.IndexOf(q, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0) ||
                                (!string.IsNullOrEmpty(name) && name.IndexOf(q, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0);

                if (!exactNormalized && !contains) continue;

                int score = 1000;
                if (exactNormalized) score = 0;
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

    /// <summary>
    /// 缁欑郴缁?璋冭瘯灞傛彁渚涗竴浠解€滃姩鎬佸湴鍥剧洰褰曗€濇憳瑕併€?    ///
    /// 娉ㄦ剰褰撳墠鐗堟湰閲岋紝杩欎唤鎽樿涓嶅啀鐩存帴鍠傜粰 LLM銆?    /// 瀹冧富瑕佹湁涓や釜鐢ㄩ€旓細
    /// 1) 鏂逛究璋冭瘯鏌ョ湅褰撳墠鍦板浘閲屽埌搴曟湁鍝簺 collection锛?    /// 2) 璁?grounded 灞傚拰浜哄伐鎺掗敊鏃惰兘鐪嬪埌 collectionKey -> members 鐨勭湡瀹炴槧灏勩€?    /// </summary>
    public string BuildFeatureCatalogSummary(int maxMembersPerCollection = 8)
    {
        if (featureCollectionMembers == null || featureCollectionMembers.Count == 0)
        {
            return "鍦板浘鐩綍鏆備笉鍙敤";
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (KeyValuePair<string, string[]> kv in featureCollectionMembers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string[] members = kv.Value ?? Array.Empty<string>();
            int previewCount = Mathf.Clamp(maxMembersPerCollection, 1, Mathf.Max(1, members.Length));
            string preview = members.Length > 0
                ? string.Join("|", members.Take(previewCount).ToArray())
                : "none";
            sb.Append("- collectionKey=")
              .Append(kv.Key)
              .Append(",count=")
              .Append(members.Length)
              .Append(",members=")
              .Append(preview);
            if (members.Length > previewCount)
            {
                sb.Append("|...");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 鎸?collectionKey 鍙栧嚭杩欎竴缁勫湴鍥惧ぇ鑺傜偣鎴愬憳銆?    /// 杩欐槸缁欌€滄墍鏈?building / 鎵€鏈?forest鈥濊繖绫婚泦鍚堢洰鏍囩敤鐨勬寮忓叆鍙ｃ€?    /// </summary>
    public bool TryGetFeatureCollectionMembers(string collectionKey, out FeatureSpatialProfile[] profiles)
    {
        profiles = Array.Empty<FeatureSpatialProfile>();
        if (string.IsNullOrWhiteSpace(collectionKey) ||
            featureCollectionMembers == null ||
            featureSpatialProfileByUid == null)
        {
            return false;
        }

        if (!featureCollectionMembers.TryGetValue(collectionKey.Trim(), out string[] memberTokens) ||
            memberTokens == null ||
            memberTokens.Length == 0)
        {
            return false;
        }

        List<FeatureSpatialProfile> result = new List<FeatureSpatialProfile>(memberTokens.Length);
        for (int i = 0; i < memberTokens.Length; i++)
        {
            string token = memberTokens[i];
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (!TryResolveFeatureSpatialIndex(token, out FeatureSpatialIndex index) || index == null) continue;
            result.Add(BuildSpatialProfileFromIndex(index));
        }

        if (result.Count == 0) return false;

        result.Sort((a, b) =>
        {
            int byX = a.centroidWorld.x.CompareTo(b.centroidWorld.x);
            if (byX != 0) return byX;
            return a.centroidWorld.z.CompareTo(b.centroidWorld.z);
        });
        profiles = result.ToArray();
        return true;
    }

    /// <summary>
    /// 鎶娾€滄墍鏈?building / 鍏ㄩ儴 parking / 鏁村紶鍦板浘閲岀殑 forest鈥濊繖绫昏嚜鐒惰瑷€閫夋嫨鐭锛?    /// 鏄犲皠鍒板綋鍓嶅湴鍥鹃噷鐪熷疄瀛樺湪鐨?collectionKey銆?    ///
    /// 杩欓噷鏁呮剰涓嶅啓姝讳换浣?building/forest 璇嶈〃锛?    /// - 鍙尮閰嶉泦鍚堝畬鍏ㄦ潵鑷綋鍓嶅湴鍥鹃噷宸茬粡鏋勫缓濂界殑 featureCollectionMembers锛?    /// - 绯荤粺鍙仛瀛楃涓插綊涓€鍖栧拰鐩镐技搴︽瘮瀵癸紝涓嶆浛 LLM 閲嶆柊鐞嗚В浠诲姟璇箟銆?    ///
    /// 杩欐牱鑱岃矗灏卞緢娓呮锛?    /// 1) LLM 璐熻矗璇粹€滅敤鎴锋兂瑕嗙洊鍝竴绫荤洰鏍団€濓紱
    /// 2) CampusGrid2D 璐熻矗鍥炵瓟鈥滃綋鍓嶅湴鍥鹃噷杩欑被鐩爣瀵瑰簲鍝竴涓湡瀹為泦鍚堥敭鈥濄€?    /// </summary>
    public bool TryResolveFeatureCollectionBySelector(string selectorText, out string collectionKey, out FeatureSpatialProfile[] profiles)
    {
        collectionKey = string.Empty;
        profiles = Array.Empty<FeatureSpatialProfile>();
        if (string.IsNullOrWhiteSpace(selectorText) ||
            featureCollectionMembers == null ||
            featureCollectionMembers.Count == 0)
        {
            return false;
        }

        string normalizedSelector = NormalizeCollectionSelectorToken(selectorText);
        if (string.IsNullOrWhiteSpace(normalizedSelector))
        {
            return false;
        }

        string bestKey = string.Empty;
        int bestScore = int.MaxValue;
        foreach (KeyValuePair<string, string[]> kv in featureCollectionMembers)
        {
            string key = string.IsNullOrWhiteSpace(kv.Key) ? string.Empty : kv.Key.Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;

            string tail = key.Contains(":")
                ? key.Substring(key.LastIndexOf(':') + 1)
                : key;

            int score = ComputeCollectionSelectorScore(
                normalizedSelector,
                NormalizeCollectionSelectorToken(key),
                NormalizeCollectionSelectorToken(tail));

            if (score < bestScore)
            {
                bestScore = score;
                bestKey = key;
            }
        }

        if (string.IsNullOrWhiteSpace(bestKey))
        {
            return false;
        }

        if (!TryGetFeatureCollectionMembers(bestKey, out profiles) ||
            profiles == null ||
            profiles.Length == 0)
        {
            return false;
        }

        collectionKey = bestKey;
        return true;
    }

    /// <summary>
    /// 鎶婁竴涓湴鐐规煡璇㈢洿鎺ヨВ鏋愭垚鈥滅┖闂存。妗堚€濄€?    /// 鍜?TryResolveFeatureCell 鐨勫尯鍒湪浜庯細
    /// TryResolveFeatureCell 鍙憡璇変綘鏌愪釜鐐癸紱
    /// TryResolveFeatureSpatialProfile 浼氭妸鏁村潡鍑犱綍鑼冨洿銆佷腑蹇冦€佹帹鑽愭帴杩戠偣涓€璧风粰鍑烘潵銆?    /// </summary>
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

    /// <summary>
    /// 鑾峰彇鏌愪釜澶у湴鍥惧疄浣撳崰鐢ㄧ殑鍏ㄩ儴缃戞牸銆?    /// 杩欐槸鐪熸鐨勨€滃缓绛戜笉鏄竴涓偣锛岃€屾槸涓€缁勬牸瀛愨€濈殑鍩虹鎺ュ彛銆?    /// </summary>
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

    /// <summary>
    /// 鑾峰彇鏌愪釜澶у湴鍥惧疄浣撶殑杈圭晫鏍笺€?    /// 杩欓噷鐨勮竟鐣屾牸浠嶇劧鏄€滆璇ュ疄浣撳崰鐢ㄧ殑鏍尖€濓紝
    /// 鍙笉杩囪繖浜涙牸鑷冲皯鏈変竴渚ф帴瑙﹀埌浜嗗疄浣撳閮ㄣ€?    /// </summary>
    public bool TryGetFeatureBoundaryCells(string query, out Vector2Int[] boundaryCells, bool ignoreCase = true)
    {
        boundaryCells = Array.Empty<Vector2Int>();
        if (!TryResolveFeatureSpatialIndex(query, out FeatureSpatialIndex index, ignoreCase) || index == null || index.occupiedCells.Count == 0)
        {
            return false;
        }

        boundaryCells = ComputeFeatureBoundaryCells(index);
        return boundaryCells.Length > 0;
    }

    /// <summary>
    /// 鑾峰彇鏌愪釜澶у湴鍥惧疄浣撯€滃渚у彲鎺ヨ繎鏍尖€濄€?    /// 杩斿洖鐨勪笉鏄缓绛戝唴閮ㄦ牸锛岃€屾槸寤虹瓚澶栧洿銆佸彲閫氳銆侀€傚悎褰撲綔鎺ヨ繎閿氱偣鐨勬牸瀛愰泦鍚堛€?    /// anchorBias 鐢ㄧ鏁ｅ钩闈㈠悜閲忚〃杈锯€滄洿鍋忓悜鍝竴杈规帴杩戔€濓紝渚嬪 (1,0) 琛ㄧず鏇村亸鍚戞 X 鏂瑰悜鐨勫渚с€?    /// </summary>
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
    /// 鐢熸垚鍥寸粫鏌愪釜澶у湴鍥惧疄浣撶殑闂幆璺緞銆?    /// 璁捐鐩爣涓嶆槸鈥滅敾涓€鏉″嚑浣曞渾鈥濓紝鑰屾槸娌跨潃瀹炰綋澶栧洿鍙€氳鏍煎舰鎴愪竴涓湡姝ｅ彲鎵ц鐨勭綉鏍肩幆璺€?    /// 濡傛灉缁欎簡 anchorBias锛岃捣濮嬪垏鍏ョ偣浼氫紭鍏堥€夋嫨涓庤鍋忕疆涓€鑷寸殑澶栦晶鏍笺€?    /// </summary>
    public bool TryBuildFeatureRingPath(string query, Vector3 referenceWorld, out Vector2Int[] ringPath, int maxApproachCells = 48, bool ignoreCase = true, Vector2Int? anchorBias = null)
    {
        ringPath = Array.Empty<Vector2Int>();
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

        Vector2Int[] approachCells = ComputeFeatureApproachCells(index, referenceCell, Mathf.Max(8, maxApproachCells), anchorBias);
        if (approachCells.Length < 4)
        {
            return false;
        }

        Vector2 center = profile.centroidGrid;
        Vector2Int[] ordered = approachCells
            .OrderBy(cell => Mathf.Atan2(cell.y - center.y, cell.x - center.x))
            .ToArray();

        if (!TryBuildVisitPath(ordered[0], ordered, out Vector2Int[] closedPath, closeLoop: true))
        {
            return false;
        }

        ringPath = closedPath;
        return ringPath.Length > 0;
    }

    /// <summary>
    /// 鍦ㄩ€昏緫缃戞牸涓婃嫾鎺ヤ竴鏉♀€滆闂嫢骞茬洰鏍囨牸鈥濈殑绂绘暎璺緞銆?    /// 璇ユ帴鍙ｆ棦鍙粰澶у湴鍥惧疄浣撹鐩栫敤锛屼篃鍙粰灏忚妭鐐归泦鍚堣鐩栫敤銆?    /// </summary>
    public bool TryBuildVisitPath(Vector2Int startCell, IReadOnlyList<Vector2Int> visitCells, out Vector2Int[] path, bool closeLoop = false)
    {
        path = Array.Empty<Vector2Int>();
        if (visitCells == null || visitCells.Count == 0) return false;

        List<Vector2Int> orderedTargets = OrderVisitCellsByNearest(startCell, visitCells);
        if (orderedTargets.Count == 0) return false;

        List<Vector2Int> built = new List<Vector2Int>();
        Vector2Int cursor = IsInBounds(startCell.x, startCell.y) ? startCell : orderedTargets[0];
        if (!IsPathWalkable(cursor.x, cursor.y, null))
        {
            if (!TryFindNearestWalkable(cursor, 8, out cursor))
            {
                cursor = orderedTargets[0];
            }
        }

        AppendPathSegment(built, cursor, includeFirst: true);
        for (int i = 0; i < orderedTargets.Count; i++)
        {
            Vector2Int target = orderedTargets[i];
            List<Vector2Int> segment = FindPathAStar(cursor, target);
            if (segment == null || segment.Count == 0)
            {
                continue;
            }

            AppendPathSegment(built, segment, includeFirst: built.Count == 0);
            cursor = target;
        }

        if (closeLoop && built.Count > 1)
        {
            Vector2Int end = built[built.Count - 1];
            Vector2Int begin = built[0];
            List<Vector2Int> closure = FindPathAStar(end, begin);
            if (closure != null && closure.Count > 0)
            {
                AppendPathSegment(built, closure, includeFirst: false);
            }
        }

        path = RemoveConsecutiveDuplicateCells(built).ToArray();
        return path.Length > 0;
    }

    /// <summary>
    /// 浠呯Щ闄ょ浉閭婚噸澶嶆牸锛屼笉鍋氬叏灞€鍘婚噸銆?    /// 杩欐牱鍙互淇濈暀闂幆璺緞鍜岄噸澶嶇粡杩囧悓涓€鏍肩殑鍚堟硶璺緞缁撴瀯銆?    /// </summary>
    private static List<Vector2Int> RemoveConsecutiveDuplicateCells(IReadOnlyList<Vector2Int> path)
    {
        List<Vector2Int> result = new List<Vector2Int>();
        if (path == null || path.Count == 0) return result;

        result.Add(path[0]);
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i] != result[result.Count - 1])
            {
                result.Add(path[i]);
            }
        }

        return result;
    }

    private bool TryResolveFeatureSpatialIndex(string query, out FeatureSpatialIndex index, bool ignoreCase = true)
    {
        index = null;
        if (string.IsNullOrWhiteSpace(query) || featureSpatialIndexByUid == null || featureSpatialIndexByUid.Count == 0)
        {
            return false;
        }

        string q = query.Trim();
        TryStripStructuredTargetPrefix(q, out _, out q);
        if (string.IsNullOrWhiteSpace(q)) return false;

        if (featureSpatialIndexByUid.TryGetValue(q, out index) && index != null)
        {
            return true;
        }

        if (featureAliasUidMap != null &&
            featureAliasUidMap.TryGetValue(q, out string aliasUid) &&
            !string.IsNullOrWhiteSpace(aliasUid) &&
            featureSpatialIndexByUid.TryGetValue(aliasUid.Trim(), out index) &&
            index != null)
        {
            return true;
        }

        if (featureUidsByName != null &&
            featureUidsByName.TryGetValue(q, out List<string> namedUids) &&
            namedUids != null &&
            namedUids.Count > 0 &&
            featureSpatialIndexByUid.TryGetValue(namedUids[0], out index) &&
            index != null)
        {
            return true;
        }

        if (TryResolveFeatureCell(q, out Vector2Int cell, out string matchedUid, out string matchedName, preferWalkable: false, ignoreCase: ignoreCase))
        {
            string resolvedUid = matchedUid;
            if (string.IsNullOrWhiteSpace(resolvedUid) &&
                TryGetCellFeatureInfo(cell.x, cell.y, out string cellUid, out string _, out _, out _))
            {
                resolvedUid = cellUid;
            }

            if (!string.IsNullOrWhiteSpace(resolvedUid) &&
                featureSpatialIndexByUid.TryGetValue(resolvedUid.Trim(), out index) &&
                index != null)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(matchedName) &&
                featureUidsByName != null &&
                featureUidsByName.TryGetValue(matchedName.Trim(), out List<string> fuzzyUids) &&
                fuzzyUids != null &&
                fuzzyUids.Count > 0 &&
                featureSpatialIndexByUid.TryGetValue(fuzzyUids[0], out index) &&
                index != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 璁＄畻瀹炰綋杈圭晫鏍笺€?    /// 鍒ゅ畾瑙勫垯寰堢畝鍗曪細鏌愪釜鍗犵敤鏍煎彧瑕佸洓閭诲煙涓瓨鍦ㄢ€滆秺鐣屾垨闈炶瀹炰綋鍗犵敤鈥濆嵆鍙畻杈圭晫銆?    /// </summary>
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

    /// <summary>
    /// 璁＄畻瀹炰綋澶栧洿鍙帴杩戞牸銆?    /// 瀹冩湰璐ㄤ笂鏄€滆竟鐣屾牸澶栦晶涓€鍦堢殑鍙€氳鏍尖€濓紝骞舵寜鍙傝€冧綅缃帓搴忓悗鎴柇銆?    /// </summary>
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
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;

                    Vector2Int c = new Vector2Int(b.x + dx, b.y + dz);
                    long key = (((long)c.x) << 32) ^ (uint)c.y;
                    if (!IsPathWalkable(c.x, c.y, null) || !seen.Add(key)) continue;

                    // 只保留真正位于实体外部的一圈格，而不是跑到很远的位置。
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

    /// <summary>
    /// 杩戦偦鎺掑簭璁块棶鐐广€?    /// 杩欓噷鏁呮剰淇濇寔鏈寸礌锛氱敤鏈€杩戦偦璐績鍗冲彲锛岄伩鍏嶅湪鍔ㄤ綔灞傚紩鍏ユ洿閲嶇殑 TSP 澶嶆潅搴︺€?    /// </summary>
    private static List<Vector2Int> OrderVisitCellsByNearest(Vector2Int startCell, IReadOnlyList<Vector2Int> visitCells)
    {
        var seen = new HashSet<Vector2Int>();
        List<Vector2Int> remaining = new List<Vector2Int>(visitCells.Count);
        for (int i = 0; i < visitCells.Count; i++)
        {
            Vector2Int c = visitCells[i];
            if (seen.Add(c))
                remaining.Add(c);
        }

        List<Vector2Int> ordered = new List<Vector2Int>(remaining.Count);
        Vector2Int cursor = startCell;
        while (remaining.Count > 0)
        {
            int bestIndex = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < remaining.Count; i++)
            {
                float dist = (remaining[i] - cursor).sqrMagnitude;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIndex = i;
                }
            }

            Vector2Int next = remaining[bestIndex];
            ordered.Add(next);
            remaining.RemoveAt(bestIndex);
            cursor = next;
        }

        return ordered;
    }

    /// <summary>
    /// 杩藉姞涓€娈佃矾寰勶紝骞堕伩鍏嶉噸澶嶅啓鍏ラ灏鹃噸鍚堟牸銆?    /// </summary>
    private static void AppendPathSegment(List<Vector2Int> output, IReadOnlyList<Vector2Int> segment, bool includeFirst)
    {
        if (output == null || segment == null || segment.Count == 0) return;

        int start = includeFirst ? 0 : 1;
        for (int i = start; i < segment.Count; i++)
        {
            if (output.Count > 0 && output[output.Count - 1] == segment[i]) continue;
            output.Add(segment[i]);
        }
    }

    private static void AppendPathSegment(List<Vector2Int> output, Vector2Int cell, bool includeFirst)
    {
        if (output == null) return;
        if (!includeFirst && output.Count > 0 && output[output.Count - 1] == cell) return;
        if (output.Count == 0 || output[output.Count - 1] != cell)
        {
            output.Add(cell);
        }
    }

    /// <summary>
    /// 璁＄畻鈥滀竴涓?selector 鍜屼竴涓湡瀹?collectionKey 鍒板簳鏈夊鍍忊€濄€?    /// 鍒嗘暟瓒婂皬琛ㄧず瓒婂儚锛沬nt.MaxValue 琛ㄧず瀹屽叏涓嶅儚銆?    ///
    /// 杩欓噷涓嶅幓鏋氫妇涓氬姟鍗曡瘝锛岃€屾槸鐩存帴鎷?selector 鍜屽綋鍓嶅湴鍥剧湡瀹為泦鍚堥敭鍋氭瘮瀵癸細
    /// - 瀹屽叏鐩哥瓑鏈€濂斤紱
    /// - selector 鍖呭惈闆嗗悎灏惧悕锛堜緥濡?allbuilding 鍖呭惈 building锛夋涔嬶紱
    /// - 鍙嶅悜鍖呭惈鏇村急锛屽彧褰撳厹搴曘€?    /// </summary>
    private static int ComputeCollectionSelectorScore(string normalizedSelector, string normalizedKey, string normalizedTail)
    {
        if (string.IsNullOrWhiteSpace(normalizedSelector))
        {
            return int.MaxValue;
        }

        int best = int.MaxValue;
        if (!string.IsNullOrWhiteSpace(normalizedTail))
        {
            if (normalizedSelector == normalizedTail)
            {
                best = 0;
            }
            else if (normalizedSelector.Contains(normalizedTail))
            {
                best = Math.Min(best, 10 + normalizedSelector.Length - normalizedTail.Length);
            }
            else if (normalizedTail.Contains(normalizedSelector))
            {
                best = Math.Min(best, 30 + normalizedTail.Length - normalizedSelector.Length);
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedKey))
        {
            if (normalizedSelector == normalizedKey)
            {
                best = Math.Min(best, 1);
            }
            else if (normalizedSelector.Contains(normalizedKey))
            {
                best = Math.Min(best, 20 + normalizedSelector.Length - normalizedKey.Length);
            }
            else if (normalizedKey.Contains(normalizedSelector))
            {
                best = Math.Min(best, 40 + normalizedKey.Length - normalizedSelector.Length);
            }
        }

        return best;
    }

    /// <summary>
    /// 鎶?selector 鏂囨湰褰掍竴鍒扳€滃彧淇濈暀瀛楁瘝銆佹暟瀛楀拰涓枃鈥濈殑绱у噾褰㈠紡锛?    /// 鏂逛究鍜?collectionKey / kind 鍚嶅仛绋冲畾姣旇緝銆?    ///
    /// 杩欎竴姝ュ彧鍋氬舰寮忓綊涓€鍖栵紝涓嶅仛璇箟纭紪鐮併€?    /// </summary>
    private static string NormalizeCollectionSelectorToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string s = raw.Trim().ToLowerInvariant();
        System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool asciiLetter = c >= 'a' && c <= 'z';
            bool digit = c >= '0' && c <= '9';
            bool cjk = c >= 0x4e00 && c <= 0x9fff;
            if (asciiLetter || digit || cjk)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
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
            maxZ = index.maxZ == int.MinValue ? -1 : index.maxZ,
            memberEntityIds = new[] { SelectFeatureReferenceToken(index.uid, index.name, index.runtimeAlias) }
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

    private static FeatureSpatialProfile CloneSpatialProfile(FeatureSpatialProfile src)
    {
        if (src == null) return null;
        return new FeatureSpatialProfile
        {
            uid = src.uid,
            name = src.name,
            runtimeAlias = src.runtimeAlias,
            kind = src.kind,
            collectionKey = src.collectionKey,
            cellType = src.cellType,
            occupiedCellCount = src.occupiedCellCount,
            minX = src.minX,
            maxX = src.maxX,
            minZ = src.minZ,
            maxZ = src.maxZ,
            centroidGrid = src.centroidGrid,
            centroidCell = src.centroidCell,
            centroidWorld = src.centroidWorld,
            anchorCell = src.anchorCell,
            anchorWorld = src.anchorWorld,
            footprintRadius = src.footprintRadius,
            memberEntityIds = src.memberEntityIds != null ? (string[])src.memberEntityIds.Clone() : Array.Empty<string>()
        };
    }

    /// <summary>
    /// 璁＄畻鍊欓€夊渚ф牸涓庣洰鏍囧亸缃殑鍑犱綍涓€鑷存€т唬浠枫€?    /// 浠ｄ环瓒婂皬锛岃鏄庤鏍艰秺鎺ヨ繎涓婃父璁″垝甯屾湜鐨勬帴杩戞柟鍚戙€?    /// 杩欓噷瀹屽叏鍩轰簬涓績鐐瑰埌鍊欓€夌偣鐨勫悜閲忓す瑙掞紝涓嶅啀渚濊禆 East/West/North/South 鏋氫妇銆?    /// </summary>
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
                    if (!IsInBounds(candidate.x, candidate.y) || !IsWalkable(candidate.x, candidate.y)) continue;

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

    /// <summary>
    /// A* 瀵昏矾锛氳繑鍥炰粠 start 鍒?goal 鐨勭綉鏍艰矾寰勶紙鍖呭惈璧风偣鍜岀粓鐐癸級銆?    /// </summary>
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
        cellTypeGrid = new CellType[gridWidth, gridLength];
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
                cellTypeGrid[x, z] = CellType.Free;
                cellFeatureUidGrid[x, z] = null;
                cellFeatureNameGrid[x, z] = null;
                cellPriorityGrid[x, z] = int.MinValue;
            }
        }

        for (int i = 0; i < features.Count; i++)
        {
            Feature2D f = features[i];
            if (f == null) continue;

            CellType t = KindToCellType(f.kind);
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
                    AssignCellFeatureIdentity(x, z, f.uid, f.effectiveName);
                }
            }
        }

        FinalizeFeatureSpatialIndexes();

        if (logBuildSummary)
        {
            int blockedCount = 0;
            for (int x = 0; x < gridWidth; x++)
                for (int z = 0; z < gridLength; z++)
                    if (blockedGrid[x, z]) blockedCount++;

            Debug.Log($"[CampusGrid2D] 鏋勫缓瀹屾垚: {gridWidth}x{gridLength}, cell={cellSize}m, 闃诲={blockedCount}/{gridWidth * gridLength}");
        }
    }

    /// <summary>
    /// 鎶?CampusJsonMapLoader 鐨勫缓妯?footprint 鍏堣浆鎴愰€傚悎 2D 鏍呮牸鍖栫殑绋冲畾鍑犱綍锛?    /// - 闈細澶嶇敤 ring 娓呮礂銆佺粫搴忋€乷uter->hole 褰掑睘鍜?bounds 鐭╁舰鍏滃簳锛?    /// - 绾匡細鐩存帴鎸?ribbon 椤堕潰鐢熸垚 2D quad锛屽拰鐪熷疄閬撹矾甯﹀涓€鑷淬€?    /// </summary>
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

    private int GetFeatureRasterPriority(CellType cellType)
    {
        switch (cellType)
        {
            case CellType.Bridge: return 1000;
            case CellType.Building: return 900;
            case CellType.Expressway: return 820;
            case CellType.Road: return 780;
            case CellType.Parking: return 730;
            case CellType.Sports: return 700;
            case CellType.Water: return 680;
            case CellType.Green: return 640;
            case CellType.Forest: return 620;
            case CellType.Other: return 600;
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

    /// <summary>
    /// 褰掍竴鍖栧湴鐐规爣璇嗭細缁熶竴澶у皬鍐欏苟绉婚櫎绌虹櫧銆佷笅鍒掔嚎銆佽繛瀛楃锛屼究浜庢ā绯婂榻愩€?    /// </summary>
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

    /// <summary>
    /// 鍙仛涓€绉嶉潪甯稿厠鍒剁殑鈥滄爣绛惧墠缂€鍓ョ鈥濓細
    /// - 鑻ヨ緭鍏ュ儚 feature:a_3 / campus:forest_2 杩欑鈥滅煭鏍囩:涓讳綋鈥濆舰寮忥紝灏卞彇涓讳綋锛?    /// - 鍚﹀垯淇濇寔鍘熸牱銆?    ///
    /// 杩欓噷鏁呮剰涓嶆灇涓?building/forest 绛変笟鍔″崟璇嶏紝
    /// 閬垮厤鍦板浘鍛藉悕瑙勫垯涓€鍙橈紝涓嬫父瑙ｆ瀽灏卞叏閮ㄥけ鏁堛€?    /// </summary>
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

        string rawName = !string.IsNullOrWhiteSpace(feature.name) && feature.name.Trim() != "-"
            ? feature.name.Trim()
            : feature.uid;
        string baseName = SanitizeFeatureAliasToken(rawName);
        string kind = NormalizeFeatureKindToken(feature.kind);

        switch (kind)
        {
            case "building":
                return (!string.IsNullOrWhiteSpace(feature.name) && feature.name.Trim() != "-")
                    ? $"{baseName}_{++buildingIndex}"
                    : $"building_{++buildingIndex}";
            case "road":
                return $"{baseName}_{++roadIndex}";
            case "expressway":
                return $"{baseName}_{++expresswayIndex}";
            case "bridge":
                return $"{baseName}_{++bridgeIndex}";
            case "water":
                return $"{baseName}_{++waterIndex}";
            case "forest":
                return $"{baseName}_{++forestIndex}";
            case "sports":
                return $"{baseName}_{++sportsIndex}";
            case "parking":
                return $"{baseName}_{++parkingIndex}";
            case "green":
                return $"{baseName}_{++greenIndex}";
            default:
                return $"{baseName}_{++greenIndex}";
        }
    }

    private static string NormalizeFeatureKindToken(string kind)
    {
        string k = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(k) ? "other" : k;
    }

    private static string SanitizeFeatureAliasToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "feature";

        string s = raw.Trim().ToLowerInvariant();
        System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
            }
            else if (c == '_' || c == '-' || char.IsWhiteSpace(c))
            {
                sb.Append('_');
            }
        }

        string result = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "feature" : result;
    }

    /// <summary>
    /// 涓烘瘡涓?feature 鍑嗗绌洪棿绱㈠紩澹冲瓙銆?    /// 鍏堝缓鈥滅┖妗ｆ鈥濓紝鍚庨潰鍦ㄦ爡鏍煎寲鏃朵笉鏂妸鍛戒腑鐨勬牸瀛愬～杩涘幓銆?    /// </summary>
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

    private void RegisterFeatureSpatialCell(Feature2D feature, CellType cellType, int x, int z)
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

    /// <summary>
    /// 鏍呮牸鍖栧畬鎴愬悗锛屾妸鍐呴儴绱㈠紩鏀舵暃鎴愮ǔ瀹氬彲璇荤殑绌洪棿妗ｆ銆?    /// </summary>
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

    public bool TryResolveFeatureAliasCell(string alias, out Vector2Int cell, out string matchedUid, out string matchedName, bool preferWalkable = true, bool ignoreCase = true)
    {
        cell = new Vector2Int(-1, -1);
        matchedUid = string.Empty;
        matchedName = string.Empty;
        if (string.IsNullOrWhiteSpace(alias) || featureAliasCellMap == null || featureAliasUidMap == null || featureAliasNameMap == null) return false;

        string key = alias.Trim();
        if (!featureAliasUidMap.TryGetValue(key, out matchedUid) && !featureAliasNameMap.TryGetValue(key, out matchedName))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(matchedName) && featureAliasNameMap.TryGetValue(key, out string aliasName))
        {
            matchedName = aliasName;
        }

        if (!string.IsNullOrWhiteSpace(matchedUid) &&
            (TryGetFeatureFirstCellByUid(matchedUid, out cell, preferWalkable, ignoreCase) ||
             TryGetFeatureFirstCellByUid(matchedUid, out cell, false, ignoreCase)))
        {
            TryGetCellFeatureInfo(cell.x, cell.y, out string uid, out string name, out _, out _);
            matchedUid = string.IsNullOrWhiteSpace(uid) ? matchedUid : uid;
            matchedName = string.IsNullOrWhiteSpace(name) ? matchedName : name;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(matchedName) &&
            (TryGetFeatureFirstCell(matchedName, out cell, preferWalkable, ignoreCase) ||
             TryGetFeatureFirstCell(matchedName, out cell, false, ignoreCase)))
        {
            TryGetCellFeatureInfo(cell.x, cell.y, out string uid, out string name, out _, out _);
            matchedUid = string.IsNullOrWhiteSpace(uid) ? matchedUid : uid;
            matchedName = string.IsNullOrWhiteSpace(name) ? matchedName : name;
            return true;
        }

        if (featureAliasCellMap.TryGetValue(key, out Vector2Int aliasCell) && IsInBounds(aliasCell.x, aliasCell.y))
        {
            cell = aliasCell;
            return true;
        }

        return false;
    }

    private bool TryResolveFeatureAliasCellByNormalized(string query, out Vector2Int cell, out string matchedUid, out string matchedName, bool preferWalkable = true, bool ignoreCase = true)
    {
        cell = new Vector2Int(-1, -1);
        matchedUid = string.Empty;
        matchedName = string.Empty;
        if (string.IsNullOrWhiteSpace(query) || featureAliasUidMap == null || featureAliasUidMap.Count == 0) return false;

        string normalizedQuery = NormalizeFeatureToken(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery)) return false;

        string bestAlias = null;
        int bestScore = int.MaxValue;
        foreach (KeyValuePair<string, string> kv in featureAliasUidMap)
        {
            string alias = kv.Key;
            if (string.IsNullOrWhiteSpace(alias)) continue;

            string normalizedAlias = NormalizeFeatureToken(alias);
            bool exactNormalized = normalizedAlias == normalizedQuery;
            bool contains = normalizedAlias.Contains(normalizedQuery) || normalizedQuery.Contains(normalizedAlias);
            if (!exactNormalized && !contains) continue;

            int score = exactNormalized ? 0 : 50;
            score += Math.Abs(alias.Length - query.Trim().Length);
            if (score < bestScore)
            {
                bestScore = score;
                bestAlias = alias;
            }
        }

        if (string.IsNullOrWhiteSpace(bestAlias)) return false;
        return TryResolveFeatureAliasCell(bestAlias, out cell, out matchedUid, out matchedName, preferWalkable, ignoreCase);
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

    private bool IsPathWalkable(int x, int z, HashSet<long> transientBlockedKeys)
    {
        if (!IsWalkable(x, z)) return false;
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
            if (!IsWalkable(x0, z0)) return false;
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

            if (!IsInBounds(nextX, nextZ) || !IsWalkable(nextX, nextZ)) return false;
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
