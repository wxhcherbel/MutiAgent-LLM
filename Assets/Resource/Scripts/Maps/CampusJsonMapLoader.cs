// CampusJsonMapLoader.cs
// 放到 Assets/Scripts/ 下，挂到一个空物体上。
// 需要安装 Newtonsoft Json（com.unity.nuget.newtonsoft-json）。
//
// 功能：
// 1) 读取 JSON（文本或文件）
// 2) 解析 features：building/sports/water/road/expressway/bridge/parking/green/forest/other
// 3) 生成地面、面挤出、线带状
// 4) 支持孔洞桥接（简化）+ 耳切三角剖分 + 挤出
//
// 坐标：
// - JSON 的 points/rings 使用 “米” (x,y)
// - Unity 世界单位默认也是 “米”，所以这里直接用米（不再像 UE 那样转 cm）
// - GroundZ / Thickness 等也用米
//
// 注意：
// - 若你发现面朝向反了（看不到顶面），把 BuildExtrudedSolidMesh() 里顶面/底面三角顺序反一下即可。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

[ExecuteAlways] // 允许编辑器模式下直接生成
public class CampusJsonMapLoader : MonoBehaviour
{
    // =========================
    // JSON 输入
    // =========================
    [Header("JSON Input")]
    public bool preferEmbeddedText = true;

    [TextArea(6, 30)]
    public string jsonText;

    public string jsonFilePath;

    //摄像机
    public CameraController cameraController; // 摄像机控制器
    public bool enableFreeCamera = true; // 是否启用自由摄像机

    // =========================
    // 地面
    // =========================
    [Header("Ground")]
    [Min(5f)] public float groundWidthM = 200f;
    [Min(5f)] public float groundLengthM = 200f;
    public float groundZ = 0f;
    [Min(0.01f)] public float groundThicknessM = 0.2f; // UE 20cm => Unity 0.2m

    public bool autoFitGroundToJsonBounds = true;
    [Min(0f)] public float groundMarginM = 10f;

    [Header("Map XY Scale")]
    [Tooltip("仅对地图的 XY 平面进行等比缩放。值大于 1 会拉大建筑与道路之间的水平间距，便于细网格通行。")]
    [Min(0.1f)] public float horizontalMapScale = 1f;

    // =========================
    // 随机 / 建筑高度
    // =========================
    [Header("Random / Building Height")]
    public int seed = 1337;
    [Min(1f)] public float defaultBuildingHeightM = 12f;
    [Min(0.2f)] public float metersPerLevel = 3.2f; // UE 320cm => 3.2m

    // =========================
    // 面挤出参数
    // =========================
    [Header("Area Extrusion")]
    [Min(0.01f)] public float areaThicknessM = 0.08f; // UE 8cm => 0.08m
    public bool enableCollisionForAreas = false;
    public bool enableCollisionForBuildings = true;

    [Tooltip("MeshCollider 使用 convex 会限制形状；这里默认用非 convex（更像 UE 的 ComplexAsSimple）")]
    public bool meshColliderConvex = false;

    [Min(0f)] public float zBiasStepM = 0.002f; // UE 0.2cm => 0.002m

    // =========================
    // 线带状挤出参数
    // =========================
    [Header("Stroke (Road Ribbon)")]
    [Min(0.2f)] public float strokeWidthM = 2.0f;
    [Min(0.01f)] public float strokeThicknessM = 0.08f; // UE 8cm => 0.08m
    public bool enableCollisionForStrokes = false;

    // =========================
    // 材质与颜色
    // =========================
    [Header("Visualization")]
    public Material baseMaterial; // 一个可着色的材质（建议 Standard/URP Lit 都行）
    public Color colorGround     = new Color32(240, 230, 210, 255);
    public Color colorBuilding   = new Color32(210, 190, 230, 255);
    public Color colorSports     = new Color32(170, 210, 255, 255);
    public Color colorWater      = new Color32(120, 170, 255, 255);
    public Color colorRoad       = new Color32(205, 210, 225, 255);
    public Color colorExpressway = new Color32(255, 120, 160, 255);
    public Color colorBridge     = new Color32(255, 170, 80, 255);
    public Color colorParking    = new Color32(190, 190, 190, 255);
    public Color colorGreen      = new Color32(170, 235, 170, 255);
    public Color colorForest     = new Color32(120, 200, 120, 255);

    // =========================
    // 生成物容器（相当于 UE folder）
    // =========================
    private Transform rootGround;
    private Transform rootBuilding;
    private Transform rootSports;
    private Transform rootWater;
    private Transform rootRoad;
    private Transform rootExpressway;
    private Transform rootBridge;
    private Transform rootParking;
    private Transform rootGreen;
    private Transform rootForest;
    private int buildingLayer = -1;
    private int obstacleLayer = -1;
    private int groundLayer = -1;

    // 颜色材质缓存（避免每个对象都 new 材质）
    private readonly Dictionary<Color, Material> tintMatCache = new Dictionary<Color, Material>();

    //摄像机
    // 自由摄像机切换
    public void OnFreeCameraToggleChanged(bool isOn)
    {
        enableFreeCamera = isOn;
        if (cameraController != null)
        {
            cameraController.SetFreeCameraMode(isOn);
        }
    }

    // =========================
    // 编辑器按钮（右键组件 -> ContextMenu）
    // =========================
    [ContextMenu("Import And Build")]
    public void ImportAndBuild()
    {
        if (baseMaterial == null)
        {
            Debug.LogError("[CampusImport] baseMaterial 为空，无法着色渲染。");
            return;
        }

        if (!LoadJsonString(out string json))
        {
            Debug.LogError("[CampusImport] 未读取到 JSON：请填写 jsonText 或 jsonFilePath。");
            return;
        }

        ClearBuilt();

        if (!ParseCampusJson(json, out List<CampusFeature> features, out Rect allBounds))
        {
            Debug.LogError("[CampusImport] JSON 解析失败：需要 features 数组；面要素需要 rings.outer；线要素需要 points_xy_m。");
            return;
        }

        ApplyHorizontalScaleToFeatures(features, ref allBounds, horizontalMapScale);

        EnsureFolders();
        CacheCommonLayers();

        // 地面尺寸
        float gw = groundWidthM;
        float gl = groundLengthM;

        if (autoFitGroundToJsonBounds)
        {
            float margin = Mathf.Max(0f, groundMarginM);
            gw = allBounds.width + 2f * margin;
            gl = allBounds.height + 2f * margin;

            // UE 里会把 Loader Actor 移到 bounds center；Unity 这里也把 loader 物体移到中心，方便对齐
            Vector3 pos = transform.position;
            pos.x = allBounds.center.x;
            pos.z = allBounds.center.y; // 注意：Unity 用 XZ 平面做“地面”，所以 y->z
            transform.position = pos;

            // 重新用新的中心生成一个 ground bounds
            var c = new Vector2(transform.position.x, transform.position.z);
            allBounds = new Rect(c.x - gw * 0.5f, c.y - gl * 0.5f, gw, gl);
        }

        SpawnGroundPlane(gw, gl);

        // 生成要素
        var sceneNameAllocator = new CampusFeatureSceneNameAllocator();

        for (int i = 0; i < features.Count; i++)
        {
            CampusFeature f = features[i];
            string sceneObjectBaseName = sceneNameAllocator.AllocateSceneName(
                f.name,
                CampusFeatureSceneNaming.GetDefaultScenePrefix(f.kind));

            // 建筑：必须生成（无 rings 用 bounds 兜底）
            if (f.kind == CampusFeatureKind.Building)
            {
                float h = GetBuildingHeightMFromTags(f.tags);
                float zCenter = groundZ + h * 0.5f;

                CampusFeature tmp = f;
                if (!tmp.HasArea())
                {
                    tmp.outerRings = new List<CampusRing>();
                    tmp.innerRings = new List<CampusRing>();

                    if (tmp.boundsValid)
                    {
                        var r = new CampusRing();
                        Vector2 min = new Vector2(tmp.bounds.xMin, tmp.bounds.yMin);
                        Vector2 max = new Vector2(tmp.bounds.xMax, tmp.bounds.yMax);
                        r.points.Add(new Vector2(min.x, min.y));
                        r.points.Add(new Vector2(max.x, min.y));
                        r.points.Add(new Vector2(max.x, max.y));
                        r.points.Add(new Vector2(min.x, max.y));
                        tmp.outerRings.Add(r);
                    }
                }

                SpawnExtrudedArea(
                    tmp,
                    Mathf.Max(0.5f, h),
                    zCenter,
                    colorBuilding,
                    enableCollisionForBuildings,
                    rootBuilding,
                    sceneObjectBaseName
                );
                continue;
            }

            // 线：road/expressway/bridge
            if (f.HasLine())
            {
                float width = Mathf.Max(0.1f, strokeWidthM * Mathf.Max(0.1f, horizontalMapScale));
                float thick = Mathf.Max(0.01f, strokeThicknessM);
                float zBias = ((int)f.kind) * zBiasStepM;
                float zCenter = groundZ + thick * 0.5f + zBias;

                if (f.kind == CampusFeatureKind.Road)
                {
                    SpawnStrokeRibbon(f.linePoints, width, thick, zCenter, colorRoad, enableCollisionForStrokes,
                        rootRoad, sceneObjectBaseName);
                }
                else if (f.kind == CampusFeatureKind.Expressway)
                {
                    SpawnStrokeRibbon(f.linePoints, width, thick, zCenter, colorExpressway, enableCollisionForStrokes,
                        rootExpressway, sceneObjectBaseName);
                }
                else if (f.kind == CampusFeatureKind.Bridge)
                {
                    SpawnStrokeRibbon(f.linePoints, width, thick, zCenter, colorBridge, true,
                        rootBridge, sceneObjectBaseName);
                }
            }

            // 面：薄片
            if (f.HasArea())
            {
                float zBias = ((int)f.kind) * zBiasStepM;
                float thick = Mathf.Max(0.01f, areaThicknessM);
                float zCenter = groundZ + thick * 0.5f + zBias;

                if (f.kind == CampusFeatureKind.Water)
                {
                    SpawnExtrudedArea(f, thick, zCenter, colorWater, enableCollisionForAreas, rootWater,
                        sceneObjectBaseName);
                }
                else if (f.kind == CampusFeatureKind.Green)
                {
                    SpawnExtrudedArea(f, thick, zCenter, colorGreen, enableCollisionForAreas, rootGreen,
                        sceneObjectBaseName);
                }
                else if (f.kind == CampusFeatureKind.Forest)
                {
                    SpawnExtrudedArea(f, thick, zCenter, colorForest, enableCollisionForAreas, rootForest,
                        sceneObjectBaseName);
                }
                else if (f.kind == CampusFeatureKind.Sports)
                {
                    SpawnExtrudedArea(f, thick, zCenter, colorSports, enableCollisionForAreas, rootSports,
                        sceneObjectBaseName);
                }
                else if (f.kind == CampusFeatureKind.Parking)
                {
                    SpawnExtrudedArea(f, thick, zCenter, colorParking, enableCollisionForAreas, rootParking,
                        sceneObjectBaseName);
                }
                else
                {
                    SpawnExtrudedArea(f, thick, zCenter, colorGreen, enableCollisionForAreas, rootGreen,
                        sceneObjectBaseName);
                }
            }
        }

        Debug.Log("[CampusImport] Done.");
    }

    [ContextMenu("Clear Built")]
    public void ClearBuilt()
    {
        // 清理：把我们创建的 folder root 全删掉
        // ExecuteAlways 下用 DestroyImmediate，运行时用 Destroy
        void Kill(Transform t)
        {
            if (t == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(t.gameObject);
            else Destroy(t.gameObject);
#else
            Destroy(t.gameObject);
#endif
        }

        Kill(rootGround);
        Kill(rootBuilding);
        Kill(rootSports);
        Kill(rootWater);
        Kill(rootRoad);
        Kill(rootExpressway);
        Kill(rootBridge);
        Kill(rootParking);
        Kill(rootGreen);
        Kill(rootForest);

        rootGround = rootBuilding = rootSports = rootWater = rootRoad = rootExpressway =
            rootBridge = rootParking = rootGreen = rootForest = null;

        tintMatCache.Clear();
    }

    // =========================
    // folder root（相当于 UE Outliner folder）
    // =========================
    private void EnsureFolders()
    {
        Transform Make(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        if (rootGround == null)     rootGround = Make("Map/Ground");
        if (rootBuilding == null)   rootBuilding = Make("Map/Building");
        if (rootSports == null)     rootSports = Make("Map/Sports");
        if (rootWater == null)      rootWater = Make("Map/Water");
        if (rootRoad == null)       rootRoad = Make("Map/Road");
        if (rootExpressway == null) rootExpressway = Make("Map/Expressway");
        if (rootBridge == null)     rootBridge = Make("Map/Bridge");
        if (rootParking == null)    rootParking = Make("Map/Parking");
        if (rootGreen == null)      rootGreen = Make("Map/Green");
        if (rootForest == null)     rootForest = Make("Map/Forest");

        CacheCommonLayers();
        ApplySemanticLayer(rootGround != null ? rootGround.gameObject : null, groundLayer);
        ApplySemanticLayer(rootBuilding != null ? rootBuilding.gameObject : null, buildingLayer);
    }

    // =========================
    // JSON 读取
    // =========================
    private bool LoadJsonString(out string outJson)
    {
        outJson = null;

        if (preferEmbeddedText && !string.IsNullOrWhiteSpace(jsonText))
        {
            outJson = jsonText;
            return true;
        }

        string path = (jsonFilePath ?? "").Trim();
        if (string.IsNullOrEmpty(path)) return false;

        // 允许相对路径（相对项目根/Assets 外也行）
        if (!File.Exists(path))
        {
            // 尝试相对 Application.dataPath 的上一级（项目根）
            string projRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string abs = Path.GetFullPath(Path.Combine(projRoot, path));
            if (File.Exists(abs)) path = abs;
        }

        if (!File.Exists(path))
        {
            Debug.LogError($"[CampusImport] 找不到文件：{path}");
            return false;
        }

        outJson = File.ReadAllText(path);
        return true;
    }

    private static void ApplyHorizontalScaleToFeatures(List<CampusFeature> features, ref Rect allBounds, float scale)
    {
        float safeScale = Mathf.Max(0.1f, scale);
        if (features == null || features.Count == 0 || Mathf.Abs(safeScale - 1f) < 1e-4f) return;

        Vector2 pivot = allBounds.center;
        for (int i = 0; i < features.Count; i++)
        {
            CampusFeature feature = features[i];
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

    private static void ScaleRingCollection(List<CampusRing> rings, Vector2 pivot, float scale)
    {
        if (rings == null) return;
        for (int i = 0; i < rings.Count; i++)
        {
            CampusRing ring = rings[i];
            if (ring == null || ring.points == null) continue;
            ScalePointCollection(ring.points, pivot, scale);
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

    private CampusFeatureKind ParseKind(string kindStr)
    {
        if (string.IsNullOrEmpty(kindStr)) return CampusFeatureKind.Other;
        string k = kindStr.Trim().ToLowerInvariant();

        if (k == "building") return CampusFeatureKind.Building;
        if (k == "sports") return CampusFeatureKind.Sports;
        if (k == "water") return CampusFeatureKind.Water;
        if (k == "road") return CampusFeatureKind.Road;
        if (k == "expressway") return CampusFeatureKind.Expressway;
        if (k == "bridge") return CampusFeatureKind.Bridge;
        if (k == "parking") return CampusFeatureKind.Parking;
        if (k == "green") return CampusFeatureKind.Green;
        if (k == "forest") return CampusFeatureKind.Forest;
        return CampusFeatureKind.Other;
    }

    // =========================
    // JSON 解析
    // =========================
    // =========================
    // JSON 解析（替换原函数）
    // 修复点：
    // 1) rings 不再“必须闭合才接受”，而是自动补闭合（避免丢建筑轮廓）
    // 2) rings/line 都会合并到 bounds
    // =========================
    private bool ParseCampusJson(string json, out List<CampusFeature> outFeatures, out Rect outAllBounds)
    {
        outFeatures = new List<CampusFeature>();
        outAllBounds = new Rect();
        bool allBoundsValid = false;

        JObject root;
        try { root = JObject.Parse(json); }
        catch { return false; }

        JArray featuresArr = root["features"] as JArray;
        if (featuresArr == null) return false;

        foreach (var v in featuresArr)
        {
            JObject obj = v as JObject;
            if (obj == null) continue;

            var f = new CampusFeature();

            f.uid = (string)obj["uid"];
            f.name = (string)obj["name"];

            string kindStr = (string)obj["kind"];
            f.kind = ParseKind(kindStr);

            // -------- tags --------
            JObject tagsObj = obj["tags"] as JObject;
            if (tagsObj != null)
            {
                foreach (var p in tagsObj.Properties())
                {
                    string key = p.Name;
                    string val;
                    if (p.Value.Type == JTokenType.String) val = (string)p.Value;
                    else if (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float) val = p.Value.ToString();
                    else if (p.Value.Type == JTokenType.Boolean) val = ((bool)p.Value) ? "true" : "false";
                    else val = "";
                    if (!f.tags.ContainsKey(key)) f.tags.Add(key, val);
                }
            }

            bool hasRings = false;

            // -------- rings（面）--------
            JObject ringsObj = obj["rings"] as JObject;
            if (ringsObj != null)
            {
                JArray outerArr = ringsObj["outer"] as JArray;
                if (outerArr != null)
                {
                    foreach (var ringV in outerArr)
                    {
                        JArray ringPts = ringV as JArray;
                        if (ringPts == null || ringPts.Count < 3) continue;

                        // ✅关键：自动闭合（不再过滤掉不闭合 ring）
                        List<Vector2> pts = ReadRingAndAutoClose(ringPts, 0.01f);
                        if (pts == null || pts.Count < 4) continue; // 闭合后至少4（首尾重复）

                        var ring = new CampusRing();
                        ring.points.AddRange(pts);
                        f.outerRings.Add(ring);
                    }
                }

                JArray innerArr = ringsObj["inner"] as JArray;
                if (innerArr != null)
                {
                    foreach (var ringV in innerArr)
                    {
                        JArray ringPts = ringV as JArray;
                        if (ringPts == null || ringPts.Count < 3) continue;

                        List<Vector2> pts = ReadRingAndAutoClose(ringPts, 0.01f);
                        if (pts == null || pts.Count < 4) continue;

                        var ring = new CampusRing();
                        ring.points.AddRange(pts);
                        f.innerRings.Add(ring);
                    }
                }

                hasRings = f.outerRings.Count > 0;
                if (hasRings)
                {
                    ComputeBoundsFromRings(f, out Rect rb, out bool valid);
                    f.bounds = rb;
                    f.boundsValid = valid;
                }
            }

            // -------- points_xy_m（线）--------
            JArray ptsArr = obj["points_xy_m"] as JArray;
            if (ptsArr != null)
            {
                for (int i = 0; i < ptsArr.Count; i++)
                    f.linePoints.Add(ReadXY(ptsArr[i]));

                ComputeBoundsFromLine(f, out Rect lb, out bool validLine);
                if (!hasRings && validLine)
                {
                    f.bounds = lb;
                    f.boundsValid = true;
                }
                else if (hasRings && validLine)
                {
                    Rect merged = MergeBounds(f.boundsValid ? f.bounds : lb, lb, f.boundsValid, validLine, out bool mergedValid);
                    f.bounds = merged;
                    f.boundsValid = mergedValid;
                }
            }

            // -------- bounds 兜底（避免完全无数据不生成）--------
            if (!f.boundsValid)
            {
                Vector3 o = transform.position;
                var c = new Vector2(o.x, o.z);
                f.bounds = new Rect(c.x - 0.5f, c.y - 0.5f, 1.0f, 1.0f);
                f.boundsValid = true;
            }

            // -------- 合并到全局 bounds --------
            if (!allBoundsValid)
            {
                outAllBounds = f.bounds;
                allBoundsValid = true;
            }
            else
            {
                outAllBounds = MergeBounds(outAllBounds, f.bounds, true, true, out bool _);
            }

            outFeatures.Add(f);
        }

        return outFeatures.Count > 0 && allBoundsValid;

        // ---- local helper：读 [x,y] ----
        Vector2 ReadXY(JToken tok)
        {
            var arr = tok as JArray;
            if (arr == null || arr.Count < 2) return Vector2.zero;
            float x = arr[0].Value<float>();
            float y = arr[1].Value<float>();
            return new Vector2(x, y);
        }
    }

    private void ComputeBoundsFromLine(CampusFeature f, out Rect bounds, out bool valid)
    {
        valid = false;
        bounds = new Rect();

        if (f.linePoints == null || f.linePoints.Count == 0) return;

        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;

        for (int i = 0; i < f.linePoints.Count; i++)
        {
            Vector2 p = f.linePoints[i];
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

    private void ComputeBoundsFromRings(CampusFeature f, out Rect bounds, out bool valid)
    {
        valid = false;
        bounds = new Rect();

        bool any = false;
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;

        void Consume(List<CampusRing> rings)
        {
            if (rings == null) return;
            for (int r = 0; r < rings.Count; r++)
            {
                var pts = rings[r].points;
                if (pts == null) continue;
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector2 p = pts[i];
                    minX = Mathf.Min(minX, p.x);
                    minY = Mathf.Min(minY, p.y);
                    maxX = Mathf.Max(maxX, p.x);
                    maxY = Mathf.Max(maxY, p.y);
                    any = true;
                }
            }
        }

        Consume(f.outerRings);
        Consume(f.innerRings);

        if (any && minX <= maxX && minY <= maxY)
        {
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            valid = true;
        }
    }

    private Rect MergeBounds(Rect a, Rect b, bool aValid, bool bValid, out bool outValid)
    {
        outValid = aValid || bValid;
        if (!aValid) return b;
        if (!bValid) return a;

        float minX = Mathf.Min(a.xMin, b.xMin);
        float minY = Mathf.Min(a.yMin, b.yMin);
        float maxX = Mathf.Max(a.xMax, b.xMax);
        float maxY = Mathf.Max(a.yMax, b.yMax);
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    // =========================
    // 材质（着色）/ 命名
    // =========================
    private Material GetTintMaterial(Color c)
    {
        if (tintMatCache.TryGetValue(c, out Material m) && m != null) return m;

        // 用 baseMaterial 克隆一个实例并上色
        Material nm = new Material(baseMaterial);
        // 通用方式：如果是 Standard/URP Lit，_Color/_BaseColor 都可能存在
        if (nm.HasProperty("_BaseColor")) nm.SetColor("_BaseColor", c);
        if (nm.HasProperty("_Color")) nm.SetColor("_Color", c);
        if (nm.HasProperty("_Cull"))nm.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        tintMatCache[c] = nm;
        return nm;
    }

    private float GetBuildingHeightMFromTags(Dictionary<string, string> tagMap)
    {
        if (tagMap != null)
        {
            if (tagMap.TryGetValue("height", out string hStr))
            {
                if (float.TryParse(hStr, out float h) && h > 0.1f) return h; // height 通常就是“米”
            }
            if (tagMap.TryGetValue("building:levels", out string lStr))
            {
                if (int.TryParse(lStr, out int lv) && lv > 0) return lv * metersPerLevel;
            }
        }
        return Mathf.Max(1f, defaultBuildingHeightM);
    }

    // =========================
    // 地面（用一个 Box）
    // =========================
    private GameObject SpawnGroundPlane(float widthM, float lengthM)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "ground";
        go.transform.SetParent(rootGround, false);
        ApplySemanticLayer(go, groundLayer);

        // Unity 地面一般用 XZ 平面，所以：宽->X，长->Z，厚->Y
        go.transform.position = new Vector3(transform.position.x, groundZ - groundThicknessM * 0.5f, transform.position.z);
        go.transform.localScale = new Vector3(widthM, groundThicknessM, lengthM);

        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = GetTintMaterial(colorGround);

        // 地面建议有 collider
        var col = go.GetComponent<Collider>();
        col.enabled = true;

        return go;
    }

    // =========================
    // 2D 几何（点线距离 / 点在多边形 / 多环孔洞）
    // =========================
    private static float DistPointToSegment2D(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-12f) return (p - a).magnitude;

        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        Vector2 q = a + t * ab;
        return (p - q).magnitude;
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

            bool intersect = ((a.y > p.y) != (b.y > p.y)) &&
                             (p.x < (b.x - a.x) * (p.y - a.y) / ((b.y - a.y) + 1e-12f) + a.x);
            if (intersect) inside = !inside;
        }
        return inside;
    }

    private static bool PointInMultiPolygonWithHoles2D(Vector2 p, List<CampusRing> outers, List<CampusRing> inners)
    {
        bool inOuter = false;
        if (outers != null)
        {
            for (int i = 0; i < outers.Count; i++)
            {
                var ring = outers[i].points;
                if (ring != null && ring.Count >= 3 && PointInPolygon2D(p, ring))
                {
                    inOuter = true;
                    break;
                }
            }
        }
        if (!inOuter) return false;

        if (inners != null)
        {
            for (int i = 0; i < inners.Count; i++)
            {
                var hole = inners[i].points;
                if (hole != null && hole.Count >= 3 && PointInPolygon2D(p, hole))
                    return false;
            }
        }
        return true;
    }

    // =========================
    // 多边形工具（面积 / winding / 去闭合点 / 相交）
    // =========================
    private static double SignedArea2D(List<Vector2> poly)
    {
        if (poly == null || poly.Count < 3) return 0.0;
        double a = 0.0;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i];
            Vector2 q = poly[(i + 1) % poly.Count];
            a += (double)p.x * (double)q.y - (double)q.x * (double)p.y;
        }
        return 0.5 * a;
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
            ring.RemoveAt(ring.Count - 1);
    }

    private static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    private static int Orient2D(Vector2 a, Vector2 b, Vector2 c, float eps = 1e-6f)
    {
        float v = Cross2(b - a, c - a);
        if (v > eps) return +1;
        if (v < -eps) return -1;
        return 0;
    }

    private static bool OnSegment2D(Vector2 a, Vector2 b, Vector2 p, float eps = 1e-6f)
    {
        return p.x >= Mathf.Min(a.x, b.x) - eps && p.x <= Mathf.Max(a.x, b.x) + eps &&
               p.y >= Mathf.Min(a.y, b.y) - eps && p.y <= Mathf.Max(a.y, b.y) + eps;
    }

    private static bool SegmentsIntersect2D(Vector2 A, Vector2 B, Vector2 C, Vector2 D)
    {
        int o1 = Orient2D(A, B, C);
        int o2 = Orient2D(A, B, D);
        int o3 = Orient2D(C, D, A);
        int o4 = Orient2D(C, D, B);

        if (o1 != o2 && o3 != o4) return true;
        if (o1 == 0 && OnSegment2D(A, B, C)) return true;
        if (o2 == 0 && OnSegment2D(A, B, D)) return true;
        if (o3 == 0 && OnSegment2D(C, D, A)) return true;
        if (o4 == 0 && OnSegment2D(C, D, B)) return true;
        return false;
    }

    private static bool SegmentIntersectsPolygonEdges(Vector2 A, Vector2 B, List<Vector2> poly)
    {
        if (poly == null || poly.Count < 3) return false;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 C = poly[i];
            Vector2 D = poly[(i + 1) % poly.Count];

            bool share = (A == C || A == D || B == C || B == D);
            if (share) continue;

            if (SegmentsIntersect2D(A, B, C, D)) return true;
        }
        return false;
    }

    // =========================
    // 孔洞桥接（简化工程版，和你 UE 逻辑一致）
    // =========================
    private static bool BridgeOneHole(List<Vector2> outerCCW, List<Vector2> holeCW, out List<Vector2> outMerged)
    {
        outMerged = null;
        if (outerCCW == null || holeCW == null || outerCCW.Count < 3 || holeCW.Count < 3) return false;

        // 孔洞最右点 Hr
        int hrIdx = 0;
        for (int i = 1; i < holeCW.Count; i++)
            if (holeCW[i].x > holeCW[hrIdx].x) hrIdx = i;
        Vector2 Hr = holeCW[hrIdx];

        // 外环找“可见”的连接点（优先 x 最大）
        int bestOuter = -1;
        float bestX = float.NegativeInfinity;

        for (int i = 0; i < outerCCW.Count; i++)
        {
            Vector2 Vo = outerCCW[i];

            if (SegmentIntersectsPolygonEdges(Hr, Vo, outerCCW)) continue;
            if (SegmentIntersectsPolygonEdges(Hr, Vo, holeCW)) continue;

            if (Vo.x > bestX)
            {
                bestX = Vo.x;
                bestOuter = i;
            }
        }

        if (bestOuter < 0) return false;

        var merged = new List<Vector2>(outerCCW.Count + holeCW.Count + 2);

        // Outer 从 bestOuter 开始绕一圈
        for (int k = 0; k < outerCCW.Count; k++)
            merged.Add(outerCCW[(bestOuter + k) % outerCCW.Count]);

        merged.Add(Hr);

        // Hole 从 hrIdx 开始绕一圈
        for (int k = 0; k < holeCW.Count; k++)
            merged.Add(holeCW[(hrIdx + k) % holeCW.Count]);

        merged.Add(Hr);

        outMerged = merged;
        return merged.Count >= 3;
    }

    private static bool MergeHolesIntoOuter(List<Vector2> outerCCW, List<List<Vector2>> holesCW, out List<Vector2> outSimpleCCW)
    {
        outSimpleCCW = new List<Vector2>(outerCCW);
        if (outSimpleCCW.Count < 3) return false;

        if (holesCW != null)
        {
            for (int i = 0; i < holesCW.Count; i++)
            {
                var hole = holesCW[i];
                if (hole == null || hole.Count < 3) continue;

                if (!BridgeOneHole(outSimpleCCW, hole, out List<Vector2> merged))
                {
                    // 桥接失败：工程兜底，忽略该孔洞
                    continue;
                }

                outSimpleCCW = merged;
                EnsureWinding(outSimpleCCW, true);
            }
        }

        return outSimpleCCW.Count >= 3;
    }

    // =========================
    // 耳切三角剖分（输入：简单多边形 CCW）
    // 输出：indices（按 poly 顶点索引）
    // =========================
    private static bool PointInTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float c1 = Cross2(b - a, p - a);
        float c2 = Cross2(c - b, p - b);
        float c3 = Cross2(a - c, p - c);

        bool hasNeg = (c1 < -1e-6f) || (c2 < -1e-6f) || (c3 < -1e-6f);
        bool hasPos = (c1 > 1e-6f) || (c2 > 1e-6f) || (c3 > 1e-6f);
        return !(hasNeg && hasPos);
    }

    private static bool TriangulateEarClipping(List<Vector2> polyCCW, out List<int> outTris)
    {
        outTris = new List<int>();
        int n = polyCCW.Count;
        if (n < 3) return false;

        var V = new List<int>(n);
        for (int i = 0; i < n; i++) V.Add(i);

        bool IsConvex(int i0, int i1, int i2)
        {
            Vector2 A = polyCCW[i0];
            Vector2 B = polyCCW[i1];
            Vector2 C = polyCCW[i2];
            float z = Cross2(B - A, C - A);
            return z > 1e-6f;
        }

        bool IsEar(int prev, int cur, int next)
        {
            if (!IsConvex(prev, cur, next)) return false;
            Vector2 A = polyCCW[prev];
            Vector2 B = polyCCW[cur];
            Vector2 C = polyCCW[next];

            for (int k = 0; k < V.Count; k++)
            {
                int vi = V[k];
                if (vi == prev || vi == cur || vi == next) continue;
                if (PointInTri2D(polyCCW[vi], A, B, C)) return false;
            }
            return true;
        }

        int guard = 0;
        const int guardMax = 50000;

        while (V.Count > 3 && guard++ < guardMax)
        {
            bool cut = false;
            for (int i = 0; i < V.Count; i++)
            {
                int prev = V[(i - 1 + V.Count) % V.Count];
                int cur  = V[i];
                int next = V[(i + 1) % V.Count];

                if (IsEar(prev, cur, next))
                {
                    outTris.Add(prev);
                    outTris.Add(cur);
                    outTris.Add(next);
                    V.RemoveAt(i);
                    cut = true;
                    break;
                }
            }
            if (!cut) return false;
        }

        if (V.Count == 3)
        {
            outTris.Add(V[0]);
            outTris.Add(V[1]);
            outTris.Add(V[2]);
            return true;
        }
        return false;
    }

    // =========================
    // 挤出实体：顶/底/侧（Unity XZ 地面：2D(x,y)->3D(x,z)）
    // =========================
    // =========================
    // 挤出实体：顶/底/侧（替换原函数）
    // 修复点：
    // 1) 顶/底面：对每个三角形做“强制朝上/朝下”纠正（避免背面裁剪导致“没顶面”）
    // 2) 侧面仍是硬边：每条边独立 4 个顶点
    // =========================
    private static void BuildExtrudedSolidMesh(
        List<Vector2> simplePolyCCW,
        List<int> tris2D,
        float y0,
        float y1,
        out List<Vector3> outVerts,
        out List<int> outIndices,
        out List<Vector3> outNormals,
        out List<Vector2> outUV
    )
    {
        outVerts = new List<Vector3>();
        outIndices = new List<int>();
        outNormals = new List<Vector3>();
        outUV = new List<Vector2>();

        if (simplePolyCCW == null || simplePolyCCW.Count < 3 || tris2D == null || tris2D.Count < 3) return;

        Vector2 MakeUV(Vector2 p) => new Vector2(p.x * 0.1f, p.y * 0.1f);

        // -------- 顶面：y1 --------
        int topBase = outVerts.Count;
        for (int i = 0; i < simplePolyCCW.Count; i++)
        {
            Vector2 p = simplePolyCCW[i];
            outVerts.Add(new Vector3(p.x, y1, p.y));
            outNormals.Add(Vector3.up);
            outUV.Add(MakeUV(p));
        }

        // 顶面：强制每个三角形法线 +Y
        for (int i = 0; i + 2 < tris2D.Count; i += 3)
        {
            int a = tris2D[i + 0];
            int b = tris2D[i + 1];
            int c = tris2D[i + 2];

            if (!TriangleFacesUp(simplePolyCCW[a], simplePolyCCW[b], simplePolyCCW[c]))
            {
                // 交换 b,c
                int tmp = b; b = c; c = tmp;
            }

            outIndices.Add(topBase + a);
            outIndices.Add(topBase + b);
            outIndices.Add(topBase + c);
        }

        // -------- 底面：y0 --------
        int botBase = outVerts.Count;
        for (int i = 0; i < simplePolyCCW.Count; i++)
        {
            Vector2 p = simplePolyCCW[i];
            outVerts.Add(new Vector3(p.x, y0, p.y));
            outNormals.Add(Vector3.down);
            outUV.Add(MakeUV(p));
        }

        // 底面：强制每个三角形法线 -Y（即与顶面相反）
        for (int i = 0; i + 2 < tris2D.Count; i += 3)
        {
            int a = tris2D[i + 0];
            int b = tris2D[i + 1];
            int c = tris2D[i + 2];

            // 先让它朝上，再反过来就是朝下
            if (!TriangleFacesUp(simplePolyCCW[a], simplePolyCCW[b], simplePolyCCW[c]))
            {
                int tmp = b; b = c; c = tmp;
            }
            // 反转成朝下：交换 b,c
            int tmp2 = b; b = c; c = tmp2;

            outIndices.Add(botBase + a);
            outIndices.Add(botBase + b);
            outIndices.Add(botBase + c);
        }

        // -------- 侧面：每条边一个 quad（硬边）--------
        int n = simplePolyCCW.Count;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Vector2 p0 = simplePolyCCW[i];
            Vector2 p1 = simplePolyCCW[j];

            Vector2 edge = p1 - p0;
            float len = edge.magnitude;
            if (len < 1e-6f) continue;
            edge /= len;

            // outer CCW => 外法线（2D）= (edge.y, -edge.x)
            Vector2 n2 = new Vector2(edge.y, -edge.x);
            Vector3 sideN = new Vector3(n2.x, 0f, n2.y);

            int baseIdx = outVerts.Count;

            // 0: p0 top, 1: p1 top, 2: p1 bottom, 3: p0 bottom
            outVerts.Add(new Vector3(p0.x, y1, p0.y));
            outVerts.Add(new Vector3(p1.x, y1, p1.y));
            outVerts.Add(new Vector3(p1.x, y0, p1.y));
            outVerts.Add(new Vector3(p0.x, y0, p0.y));

            outNormals.Add(sideN);
            outNormals.Add(sideN);
            outNormals.Add(sideN);
            outNormals.Add(sideN);

            outUV.Add(new Vector2(0, 0));
            outUV.Add(new Vector2(len, 0));
            outUV.Add(new Vector2(len, Mathf.Abs(y1 - y0)));
            outUV.Add(new Vector2(0, Mathf.Abs(y1 - y0)));

            // 侧面：强制外侧可见（按几何实际检查）
            // 三角 0-1-2, 0-2-3 的朝向由 sideN 决定；这里用一致的顺序
            outIndices.Add(baseIdx + 0);
            outIndices.Add(baseIdx + 1);
            outIndices.Add(baseIdx + 2);

            outIndices.Add(baseIdx + 0);
            outIndices.Add(baseIdx + 2);
            outIndices.Add(baseIdx + 3);
        }
    }

    // =========================
    // 折线带状挤出（每段一个小盒子带）
    // =========================
    // 修复 CS1628：不要在 lambda/local function 中捕获 out 参数。
    // 这里做法：函数内部使用本地 List 构建，最后一次性赋值给 out。

    private static void BuildPolylineRibbonMesh(
        List<Vector2> line,
        float widthM,
        float y0,
        float y1,
        out List<Vector3> outVerts,
        out List<int> outIndices,
        out List<Vector3> outNormals,
        out List<Vector2> outUV
    )
    {
        // 先用本地变量构建（避免 out 被捕获）
        List<Vector3> verts = new List<Vector3>();
        List<int> indices = new List<int>();
        List<Vector3> normals = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();

        if (line == null || line.Count < 2)
        {
            outVerts = verts;
            outIndices = indices;
            outNormals = normals;
            outUV = uvs;
            return;
        }

        float halfW = widthM * 0.5f;

        // 用“普通静态函数”写入 List，不用 lambda/local function
        for (int i = 0; i < line.Count - 1; i++)
        {
            Vector2 A = line[i];
            Vector2 B = line[i + 1];

            Vector2 dir = (B - A);
            float len = dir.magnitude;
            if (len < 1e-6f) continue;
            dir /= len;

            Vector2 left = new Vector2(-dir.y, dir.x);

            Vector2 A0 = A + left * halfW;
            Vector2 A1 = A - left * halfW;
            Vector2 B0 = B + left * halfW;
            Vector2 B1 = B - left * halfW;

            Vector3 vA0T = new Vector3(A0.x, y1, A0.y);
            Vector3 vA1T = new Vector3(A1.x, y1, A1.y);
            Vector3 vB1T = new Vector3(B1.x, y1, B1.y);
            Vector3 vB0T = new Vector3(B0.x, y1, B0.y);

            Vector3 vA0B = new Vector3(A0.x, y0, A0.y);
            Vector3 vA1B = new Vector3(A1.x, y0, A1.y);
            Vector3 vB1B = new Vector3(B1.x, y0, B1.y);
            Vector3 vB0B = new Vector3(B0.x, y0, B0.y);

            // 顶面/底面/侧面/端面
            AddQuad(verts, indices, normals, uvs, vA0T, vA1T, vB1T, vB0T, Vector3.up);
            AddQuad(verts, indices, normals, uvs, vB1B, vA1B, vA0B, vB0B, Vector3.down);

            Vector3 nL = new Vector3(left.x, 0f, left.y);
            Vector3 nR = -nL;
            AddQuad(verts, indices, normals, uvs, vA0T, vB0T, vB0B, vA0B, nL);
            AddQuad(verts, indices, normals, uvs, vB1T, vA1T, vA1B, vB1B, nR);

            Vector3 nBack = new Vector3(-dir.x, 0f, -dir.y);
            Vector3 nFwd  = -nBack;
            AddQuad(verts, indices, normals, uvs, vA1T, vA0T, vA0B, vA1B, nBack);
            AddQuad(verts, indices, normals, uvs, vB0T, vB1T, vB1B, vB0B, nFwd);
        }

        // 最后一次性赋值给 out（不在匿名函数里动 out）
        outVerts = verts;
        outIndices = indices;
        outNormals = normals;
        outUV = uvs;
    }

    // 单独的静态 helper：不捕获 out/ref 参数，只操作传入的 List
    private static void AddQuad(
        List<Vector3> verts,
        List<int> indices,
        List<Vector3> normals,
        List<Vector2> uvs,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector3 n
    )
    {
        int b = verts.Count;

        verts.Add(p0);
        verts.Add(p1);
        verts.Add(p2);
        verts.Add(p3);

        normals.Add(n);
        normals.Add(n);
        normals.Add(n);
        normals.Add(n);

        uvs.Add(new Vector2(0, 0));
        uvs.Add(new Vector2(1, 0));
        uvs.Add(new Vector2(1, 1));
        uvs.Add(new Vector2(0, 1));

        // 0-2-1, 0-3-2：保证外侧可见（和你之前一致）
        indices.Add(b + 0); indices.Add(b + 2); indices.Add(b + 1);
        indices.Add(b + 0); indices.Add(b + 3); indices.Add(b + 2);
    }
    // =========================
    // 生成：面挤出（含 MultiPolygon + 孔洞桥接 + 兜底 box）
    // =========================
    // =========================
    // 生成：面挤出（替换原函数）
    // 修复点：
    // 1) MultiPolygon：每个 outer 只使用“属于它”的孔洞（hole 点在 outer 内）
    // 2) 建筑的去共线 eps 更小，减少把轮廓删坏导致三角剖分失败
    // =========================
    // =========================
    // 生成：面挤出（替换原函数）
    // 策略：耳切失败 -> 清理重试 -> 凸包扇形 -> 盒子兜底（永不失败）
    // MultiPolygon：一个 feature 多个 outer => 生成多个 GameObject (_p1/_p2...)
    // 孔洞：只分配给包含它的 outer（hole 点在 outer 内）
    // =========================
    private GameObject SpawnExtrudedArea(
        CampusFeature area,
        float thickM,
        float yCenter,
        Color tint,
        bool enableCollision,
        Transform parent,
        string objectName
    )
    {
        float half = Mathf.Max(0.01f, thickM) * 0.5f;
        float y0 = yCenter - half;
        float y1 = yCenter + half;

        GameObject SpawnFallbackBox(string nm)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = nm;
            go.transform.SetParent(parent, false);
            if (area.kind == CampusFeatureKind.Building)
            {
                ApplySemanticLayer(go, buildingLayer);
            }

            Rect b = area.boundsValid ? area.bounds : new Rect(transform.position.x - 1, transform.position.z - 1, 2, 2);
            Vector3 center = new Vector3(b.center.x, yCenter, b.center.y);

            go.transform.position = center;

            float w = Mathf.Max(0.5f, b.width);
            float l = Mathf.Max(0.5f, b.height);
            float h = Mathf.Max(0.5f, thickM);
            go.transform.localScale = new Vector3(w, h, l);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = GetTintMaterial(tint);

            var col = go.GetComponent<Collider>();
            col.enabled = enableCollision;

            Debug.LogWarning($"[CampusPM] FallbackBox used: {nm} uid={area.uid}");
            return go;
        }

        // -------- 收集 outer（MultiPolygon）--------
        var outerRings = new List<List<Vector2>>();
        if (area.outerRings != null)
        {
            for (int i = 0; i < area.outerRings.Count; i++)
            {
                var ring = new List<Vector2>(area.outerRings[i].points ?? new List<Vector2>());
                if (ring.Count < 3) continue;

                RemoveClosingDuplicate(ring);
                CleanRingInPlace(ring, 1e-5f, (area.kind == CampusFeatureKind.Building) ? 0.00005f : 0.0005f);
                if (ring.Count < 3) continue;

                EnsureWinding(ring, true); // outer CCW
                outerRings.Add(ring);
            }
        }

        // outer 缺失：用 bounds 矩形外环
        if (outerRings.Count == 0 && area.boundsValid)
        {
            Rect b = area.bounds;
            var rect = new List<Vector2>()
            {
                new Vector2(b.xMin, b.yMin),
                new Vector2(b.xMax, b.yMin),
                new Vector2(b.xMax, b.yMax),
                new Vector2(b.xMin, b.yMax),
            };
            EnsureWinding(rect, true);
            outerRings.Add(rect);
        }

        if (outerRings.Count == 0)
            return SpawnFallbackBox(objectName);

        // -------- 预处理所有 holes（先清洗+CW）--------
        var allHolesCW = new List<List<Vector2>>();
        if (area.innerRings != null)
        {
            for (int h = 0; h < area.innerRings.Count; h++)
            {
                var hole = new List<Vector2>(area.innerRings[h].points ?? new List<Vector2>());
                if (hole.Count < 3) continue;

                RemoveClosingDuplicate(hole);
                CleanRingInPlace(hole, 1e-5f, (area.kind == CampusFeatureKind.Building) ? 0.00005f : 0.0005f);
                if (hole.Count < 3) continue;

                EnsureWinding(hole, false); // hole CW
                allHolesCW.Add(hole);
            }
        }

        GameObject firstCreated = null;

        // -------- 逐 outer 生成：失败自动升级兜底 --------
        for (int part = 0; part < outerRings.Count; part++)
        {
            string partName = (outerRings.Count > 1) ? $"{objectName}_p{part + 1}" : objectName;
            List<Vector2> outer = outerRings[part];

            // 分配属于这个 outer 的孔洞：hole 任意一点在 outer 内即可（工程足够）
            var holesForThisOuter = new List<List<Vector2>>();
            for (int h = 0; h < allHolesCW.Count; h++)
            {
                var hole = allHolesCW[h];
                if (hole == null || hole.Count < 3) continue;
                if (PointInPolygon2D(hole[0], outer))
                    holesForThisOuter.Add(hole);
            }

            // 先尝试：孔洞桥接 + 耳切（三次清洗重试），再凸包扇形
            if (!TryBuildAreaMeshPart(outer, holesForThisOuter, y0, y1,
                    out List<Vector3> verts,
                    out List<int> indices,
                    out List<Vector3> normals,
                    out List<Vector2> uvs))
            {
                // 最终兜底：盒子
                var fb = SpawnFallbackBox(partName);
                if (firstCreated == null) firstCreated = fb;
                continue;
            }

            // 成功：创建 mesh 对象
            var go = new GameObject(partName);
            go.transform.SetParent(parent, false);
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            if (area.kind == CampusFeatureKind.Building)
            {
                ApplySemanticLayer(go, buildingLayer);
            }

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetTintMaterial(tint);

            var mesh = new Mesh();
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(verts);
            mesh.SetTriangles(indices, 0);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.RecalculateBounds();

            mf.sharedMesh = mesh;

            if (enableCollision)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mc.convex = meshColliderConvex;
            }

            if (firstCreated == null) firstCreated = go;
        }

        return firstCreated;
    }
    // =========================
    // 新增：读 ring 并自动闭合
    // 作用：避免因为“rings.outer 不闭合”被丢弃，导致建筑走 fallback 方块
    // closeEps：米单位下的闭合容差（0.01=1cm）
    // =========================
    private static List<Vector2> ReadRingAndAutoClose(JArray ringPts, float closeEps = 0.01f)
    {
        var pts = new List<Vector2>();
        if (ringPts == null || ringPts.Count < 3) return pts;

        for (int i = 0; i < ringPts.Count; i++)
        {
            var arr = ringPts[i] as JArray;
            if (arr == null || arr.Count < 2) continue;

            float x = arr[0].Value<float>();
            float y = arr[1].Value<float>();
            pts.Add(new Vector2(x, y));
        }

        if (pts.Count < 3) return pts;

        // 若首尾不闭合，补一个首点
        if ((pts[0] - pts[pts.Count - 1]).sqrMagnitude > closeEps * closeEps)
            pts.Add(pts[0]);

        return pts;
    }

    // =========================
    // 生成：线带状
    // =========================
    private GameObject SpawnStrokeRibbon(
        List<Vector2> polyline,
        float widthM,
        float thickM,
        float yCenter,
        Color tint,
        bool enableCollision,
        Transform parent,
        string objectName
    )
    {
        if (polyline == null || polyline.Count < 2) return null;

        float half = Mathf.Max(0.01f, thickM) * 0.5f;
        float y0 = yCenter - half;
        float y1 = yCenter + half;

        BuildPolylineRibbonMesh(polyline, widthM, y0, y1,
            out List<Vector3> verts,
            out List<int> indices,
            out List<Vector3> normals,
            out List<Vector2> uv);

        if (verts.Count < 3 || indices.Count < 3) return null;

        var go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetTintMaterial(tint);

        var mesh = new Mesh();
        if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.SetVertices(verts);
        mesh.SetTriangles(indices, 0);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uv);
        mesh.RecalculateBounds();

        mf.sharedMesh = mesh;

        if (enableCollision)
        {
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = meshColliderConvex;
        }

        return go;
    }

    /// <summary>
    /// 缓存地图生成常用层。
    /// Building 不存在时回退到 Obstacle，再回退到 Default。
    /// </summary>
    private void CacheCommonLayers()
    {
        buildingLayer = ResolveLayerWithFallback("Building", "Obstacle", 0);
        obstacleLayer = ResolveLayerWithFallback("Obstacle", "Default", 0);
        groundLayer = ResolveLayerWithFallback("Ground", "Default", 0);
    }

    /// <summary>
    /// 给生成对象设置语义层，便于导航、感知和调试。
    /// </summary>
    private void ApplySemanticLayer(GameObject target, int layer)
    {
        if (target == null || layer < 0)
        {
            return;
        }

        target.layer = layer;
    }

    /// <summary>
    /// 优先取 primary 层；找不到时回退到 secondary，再不行使用 fallbackLayer。
    /// </summary>
    private int ResolveLayerWithFallback(string primary, string secondary, int fallbackLayer)
    {
        int primaryLayer = LayerMask.NameToLayer(primary);
        if (primaryLayer >= 0)
        {
            return primaryLayer;
        }

        int secondaryLayer = LayerMask.NameToLayer(secondary);
        if (secondaryLayer >= 0)
        {
            Debug.LogWarning($"[CampusImport] 未找到层 '{primary}'，已回退到 '{secondary}'。");
            return secondaryLayer;
        }

        Debug.LogWarning($"[CampusImport] 未找到层 '{primary}' / '{secondary}'，已回退到 layer={fallbackLayer}。");
        return fallbackLayer;
    }

    // =========================
    // 新增：判断 2D 三角形从“上方(+Y)”看是否为正面
    // 用 XZ 平面：Vector2(x,z)
    // cross = (b-a) x (c-a) 的 Y 分量 = (bx-ax)*(cz-az)-(bz-az)*(cx-ax)
    // >0 => 朝上（法线 +Y）
    // =========================
    private static bool TriangleFacesUp(Vector2 a, Vector2 b, Vector2 c, float eps = 1e-8f)
    {
        float y = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        return y > eps;
    }

    // =========================
    // 新增：核心兜底构建（永不失败）
    // 尝试顺序：
    // 1) outer+holes 桥接 -> 耳切
    // 2) 清洗加强后再试（最多 3 轮）
    // 3) 凸包扇形（形状可能变“更凸”，但保证不是方块）
    // =========================
    private bool TryBuildAreaMeshPart(
        List<Vector2> outerCCW,
        List<List<Vector2>> holesCW,
        float y0,
        float y1,
        out List<Vector3> verts,
        out List<int> indices,
        out List<Vector3> normals,
        out List<Vector2> uvs
    )
    {
        verts = null; indices = null; normals = null; uvs = null;

        // 复制一份，避免改坏原数据
        var outer = new List<Vector2>(outerCCW);

        // 先做基础清洗
        CleanRingInPlace(outer, 1e-5f, 0.0005f);
        if (outer.Count < 3) return false;
        EnsureWinding(outer, true);

        // holes 也复制
        var holes = new List<List<Vector2>>();
        if (holesCW != null)
        {
            for (int i = 0; i < holesCW.Count; i++)
            {
                var h = new List<Vector2>(holesCW[i]);
                CleanRingInPlace(h, 1e-5f, 0.0005f);
                if (h.Count >= 3)
                {
                    EnsureWinding(h, false);
                    holes.Add(h);
                }
            }
        }

        // 轮次：逐渐增加清洗强度（跟 UE 的“清理重试”类似）
        float[] collinearEpsRounds = new float[] { 0.0001f, 0.0005f, 0.002f };

        for (int round = 0; round < collinearEpsRounds.Length; round++)
        {
            var o = new List<Vector2>(outer);
            CleanRingInPlace(o, 1e-5f, collinearEpsRounds[round]);
            if (o.Count < 3) continue;
            EnsureWinding(o, true);

            // 1) 合并孔洞（桥接失败允许忽略孔洞，保证至少生成 outer）
            List<Vector2> simple = o;
            if (holes.Count > 0)
            {
                if (!MergeHolesIntoOuter(o, holes, out simple) || simple == null || simple.Count < 3)
                    simple = o;
            }

            // 2) 耳切
            if (TriangulateEarClipping(simple, out List<int> tris) && tris != null && tris.Count >= 3)
            {
                BuildExtrudedSolidMesh(simple, tris, y0, y1,
                    out verts, out indices, out normals, out uvs);

                if (verts != null && indices != null && verts.Count >= 3 && indices.Count >= 3)
                    return true;
            }
        }

        // 3) 最终兜底：凸包 + 扇形三角化（不会失败，除非点太少）
        {
            var hull = ConvexHull2D(outer);
            if (hull != null && hull.Count >= 3)
            {
                EnsureWinding(hull, true);
                var fanTris = TriangulateConvexFan(hull.Count);
                BuildExtrudedSolidMesh(hull, fanTris, y0, y1,
                    out verts, out indices, out normals, out uvs);

                if (verts != null && indices != null && verts.Count >= 3 && indices.Count >= 3)
                    return true;
            }
        }

        return false;
    }

    // =========================
    // 新增：环清洗（去重复点 + 去共线点）
    // dupEps：点重复阈值（米）
    // collinearEps：共线阈值（越大越“删点”）
    // =========================
    private static void CleanRingInPlace(List<Vector2> ring, float dupEps, float collinearEps)
    {
        if (ring == null) return;

        RemoveClosingDuplicate(ring);

        // 去连续重复
        var tmp = new List<Vector2>(ring.Count);
        float dupEps2 = dupEps * dupEps;
        for (int i = 0; i < ring.Count; i++)
        {
            if (tmp.Count == 0 || (tmp[tmp.Count - 1] - ring[i]).sqrMagnitude > dupEps2)
                tmp.Add(ring[i]);
        }
        ring.Clear();
        ring.AddRange(tmp);

        // 去共线（保守：只做一轮）
        RemoveCollinearPointsInPlace(ring, collinearEps);

        // 再去一次 closing duplicate
        RemoveClosingDuplicate(ring);
    }

    // =========================
    // 新增：去共线点（环）
    // =========================
    private static void RemoveCollinearPointsInPlace(List<Vector2> pts, float eps)
    {
        if (pts == null || pts.Count < 3) return;

        int n = pts.Count;
        var outPts = new List<Vector2>(n);

        for (int i = 0; i < n; i++)
        {
            Vector2 A = pts[(i - 1 + n) % n];
            Vector2 B = pts[i];
            Vector2 C = pts[(i + 1) % n];

            Vector2 AB = B - A;
            Vector2 AC = C - A;

            // |cross(AB,AC)| 小 => 共线
            float cross = AB.x * AC.y - AB.y * AC.x;
            if (Mathf.Abs(cross) < eps) continue;

            outPts.Add(B);
        }

        if (outPts.Count >= 3)
        {
            pts.Clear();
            pts.AddRange(outPts);
        }
    }

    // =========================
    // 新增：凸包（Monotonic Chain）
    // 输入：任意点集（可能有重复），输出：凸包顶点（不重复首尾）
    // =========================
    private static List<Vector2> ConvexHull2D(List<Vector2> pts)
    {
        if (pts == null || pts.Count < 3) return null;

        // 去重 + 排序
        var arr = new List<Vector2>(pts);
        arr.Sort((p, q) =>
        {
            int cx = p.x.CompareTo(q.x);
            if (cx != 0) return cx;
            return p.y.CompareTo(q.y);
        });

        // 去完全重复
        var uniq = new List<Vector2>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (uniq.Count == 0 || (uniq[uniq.Count - 1] != arr[i]))
                uniq.Add(arr[i]);
        }
        if (uniq.Count < 3) return null;

        float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        var lower = new List<Vector2>();
        for (int i = 0; i < uniq.Count; i++)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], uniq[i]) <= 0f)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(uniq[i]);
        }

        var upper = new List<Vector2>();
        for (int i = uniq.Count - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], uniq[i]) <= 0f)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(uniq[i]);
        }

        // 拼接（去掉首尾重复）
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);

        return (lower.Count >= 3) ? lower : null;
    }

    // =========================
    // 新增：凸多边形扇形三角化（0 为中心）
    // 输出 indices：0,i,i+1
    // =========================
    private static List<int> TriangulateConvexFan(int n)
    {
        var tris = new List<int>((n - 2) * 3);
        for (int i = 1; i < n - 1; i++)
        {
            tris.Add(0);
            tris.Add(i);
            tris.Add(i + 1);
        }
        return tris;
    }
}
    
