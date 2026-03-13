using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;

/// <summary>
/// 校园 2D 小地图（可交互版）
/// 功能：
/// 1) 绘制校园轮廓（建筑 + 其他要素）。
/// 2) 实时显示无人机位置（小圆点）。
/// 3) 支持在小地图内拖动平移、滚轮缩放。
/// 4) 支持聚焦（全图 / 全部无人机）。
/// 5) 支持隐藏/显示。
/// </summary>
public class CampusMiniMap2D : MonoBehaviour
{
    [Header("依赖引用")]
    public CampusJsonMapLoader campusLoader;   // JSON 数据来源
    public CampusGrid2D campusGrid;            // 可选：复用网格边界
    public RawImage miniMapImage;              // 小地图显示目标
    public GameObject miniMapRoot;             // 可选：隐藏/显示的根节点（不填则用 miniMapImage）

    [Header("纹理与范围")]
    [Min(128)] public int textureSize = 768;
    [Min(0f)] public float boundsPaddingM = 2f;
    public bool useCampusGridBoundsIfAvailable = true;
    public bool keepRawImageAspect = true;

    [Header("轮廓样式")]
    public Color backgroundColor = new Color32(18, 26, 34, 255);
    public Color buildingOutlineColor = new Color32(255, 214, 120, 255);
    public Color otherOutlineColor = new Color32(125, 200, 255, 255);
    [Range(1, 6)] public int outlineWidthPx = 2;

    [Header("无人机圆点")]
    public bool showOnlyQuadcopter = true;
    public Color droneDotColor = new Color32(255, 76, 76, 255);
    [Range(1, 10)] public int droneDotRadiusPx = 3;
    [Min(0.02f)] public float refreshInterval = 0.12f;

    [Header("交互控制")]
    public bool enableInteraction = true;
    [Tooltip("拖动平移使用的鼠标键：0=左键，1=右键，2=中键")]
    [Range(0, 2)] public int panMouseButton = 0;
    [Range(0.02f, 0.5f)] public float zoomStep = 0.12f;
    [Range(0.02f, 1f)] public float minZoomScale = 0.08f; // 相对于全图大小
    [Range(0.1f, 1f)] public float maxZoomScale = 1.0f;
    public bool focusHotkeysOnlyWhenPointerOverMiniMap = true;
    public KeyCode focusAllMapKey = KeyCode.Home;
    public KeyCode focusAllDronesKey = KeyCode.F;
    [Min(0f)] public float focusDronePaddingM = 8f;

    [Header("隐藏显示")]
    public bool enableHideToggle = true;
    public bool startHidden = false;
    public KeyCode toggleVisibleKey = KeyCode.M;

    [Header("运行选项")]
    public bool autoInitializeOnStart = true;
    public bool logSummary = true;

    private Rect fullBoundsXY;   // 全图边界（固定）
    private Rect viewBoundsXY;   // 当前视窗边界（可缩放/平移）
    private float fullAspect = 1f;

    private Texture2D miniMapTexture;
    private Color32[] staticPixels;
    private Color32[] framePixels;

    private float nextRefreshTime;
    private bool initialized;
    private bool miniMapVisible = true;

    private bool isDraggingPan = false;
    private Vector3 lastMousePos;

    private readonly List<MiniFeature> parsedFeatures = new List<MiniFeature>(1024);

    private class MiniFeature
    {
        public string kind = "other";
        public readonly List<List<Vector2>> outerRings = new List<List<Vector2>>();
        public readonly List<List<Vector2>> innerRings = new List<List<Vector2>>();
        public readonly List<Vector2> linePoints = new List<Vector2>();
        public Rect bounds;
        public bool boundsValid;
    }

    private void Start()
    {
        if (autoInitializeOnStart)
        {
            InitializeMiniMap();
        }
    }

    private void Update()
    {
        if (!initialized) return;

        if (enableHideToggle && Input.GetKeyDown(toggleVisibleKey))
        {
            ToggleMiniMapVisible();
        }

        if (!miniMapVisible) return;

        bool viewChanged = HandleMiniMapInteraction();
        if (viewChanged)
        {
            RenderStaticContours();
            RefreshDroneDots();
            nextRefreshTime = Time.time + refreshInterval;
            return;
        }

        if (!Application.isPlaying) return;
        if (Time.time < nextRefreshTime) return;

        nextRefreshTime = Time.time + refreshInterval;
        RefreshDroneDots();
    }

    private void OnDestroy()
    {
        if (miniMapTexture != null)
        {
            Destroy(miniMapTexture);
            miniMapTexture = null;
        }
    }

    [ContextMenu("Initialize MiniMap")]
    public void InitializeMiniMap()
    {
        ResolveReferences();
        if (miniMapImage == null)
        {
            Debug.LogError("[CampusMiniMap2D] 未绑定 RawImage（miniMapImage）。");
            return;
        }

        if (!TryLoadCampusJson(out string json))
        {
            Debug.LogError("[CampusMiniMap2D] 读取校园 JSON 失败，请检查 CampusJsonMapLoader。");
            return;
        }

        if (!TryParseFeatures(json, parsedFeatures, out Rect parsedBounds))
        {
            Debug.LogError("[CampusMiniMap2D] JSON 解析失败，无法绘制轮廓。");
            return;
        }

        ApplyHorizontalScaleToFeatures(parsedFeatures, ref parsedBounds, GetHorizontalMapScale());
        fullBoundsXY = parsedBounds;
        fullBoundsXY.xMin -= boundsPaddingM;
        fullBoundsXY.yMin -= boundsPaddingM;
        fullBoundsXY.xMax += boundsPaddingM;
        fullBoundsXY.yMax += boundsPaddingM;

        if (useCampusGridBoundsIfAvailable && campusGrid != null)
        {
            if (campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null)
            {
                campusGrid.BuildGridFromCampusJson();
            }

            if (campusGrid.mapBoundsXY.width > 0f && campusGrid.mapBoundsXY.height > 0f)
            {
                fullBoundsXY = campusGrid.mapBoundsXY;
            }
        }

        fullAspect = Mathf.Max(0.0001f, fullBoundsXY.width / Mathf.Max(0.0001f, fullBoundsXY.height));
        viewBoundsXY = fullBoundsXY; // 初始显示全图

        EnsureTextureAndBuffers();
        RenderStaticContours();
        RefreshDroneDots();

        if (keepRawImageAspect) ApplyRawImageAspect();

        initialized = true;
        nextRefreshTime = Time.time + refreshInterval;

        SetMiniMapVisible(!startHidden);

        if (logSummary)
        {
            Debug.Log($"[CampusMiniMap2D] 初始化完成，features={parsedFeatures.Count}, fullBounds=({fullBoundsXY.xMin:F1},{fullBoundsXY.yMin:F1})-({fullBoundsXY.xMax:F1},{fullBoundsXY.yMax:F1})");
        }
    }

    [ContextMenu("Rebuild Static Contours")]
    public void RebuildStaticContours()
    {
        if (!initialized)
        {
            InitializeMiniMap();
            return;
        }
        RenderStaticContours();
        RefreshDroneDots();
    }

    [ContextMenu("Refresh Drone Dots")]
    public void RefreshDroneDotsNow()
    {
        RefreshDroneDots();
    }

    [ContextMenu("Focus All Map")]
    public void FocusOnFullMap()
    {
        if (!initialized) return;
        viewBoundsXY = fullBoundsXY;
        RenderStaticContours();
        RefreshDroneDots();
    }

    [ContextMenu("Focus All Drones")]
    public void FocusOnAllDrones()
    {
        if (!initialized) return;

        IntelligentAgent[] agents = FindObjectsOfType<IntelligentAgent>();
        bool has = false;
        float minX = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        for (int i = 0; i < agents.Length; i++)
        {
            IntelligentAgent a = agents[i];
            if (a == null) continue;
            if (showOnlyQuadcopter && (a.Properties == null || a.Properties.Type != AgentType.Quadcopter)) continue;

            Vector3 p = a.transform.position;
            minX = Mathf.Min(minX, p.x);
            minZ = Mathf.Min(minZ, p.z);
            maxX = Mathf.Max(maxX, p.x);
            maxZ = Mathf.Max(maxZ, p.z);
            has = true;
        }

        if (!has)
        {
            FocusOnFullMap();
            return;
        }

        Rect target = Rect.MinMaxRect(
            minX - focusDronePaddingM,
            minZ - focusDronePaddingM,
            maxX + focusDronePaddingM,
            maxZ + focusDronePaddingM
        );

        // 根据目标区域计算 scale，并保持与全图一致的宽高比，避免拉伸
        float scale = Mathf.Max(
            target.width / Mathf.Max(0.0001f, fullBoundsXY.width),
            target.height / Mathf.Max(0.0001f, fullBoundsXY.height)
        );
        scale = Mathf.Clamp(scale, minZoomScale, maxZoomScale);

        SetViewScaleAndCenter(scale, target.center);
        RenderStaticContours();
        RefreshDroneDots();
    }

    [ContextMenu("Toggle MiniMap Visible")]
    public void ToggleMiniMapVisible()
    {
        SetMiniMapVisible(!miniMapVisible);
    }

    public void SetMiniMapVisible(bool visible)
    {
        miniMapVisible = visible;

        GameObject root = GetMiniMapRoot();
        if (root != null) root.SetActive(visible);
    }

    private GameObject GetMiniMapRoot()
    {
        if (miniMapRoot != null) return miniMapRoot;
        return miniMapImage != null ? miniMapImage.gameObject : null;
    }

    private bool HandleMiniMapInteraction()
    {
        if (!enableInteraction || miniMapImage == null) return false;

        bool viewChanged = false;
        bool pointerOver = TryGetPointerUVOnMiniMap(out Vector2 uv);

        // 热键聚焦
        bool allowFocusHotkey = !focusHotkeysOnlyWhenPointerOverMiniMap || pointerOver;
        if (allowFocusHotkey && Input.GetKeyDown(focusAllMapKey))
        {
            FocusOnFullMap();
            return false; // FocusOn* 已自行刷新
        }

        if (allowFocusHotkey && Input.GetKeyDown(focusAllDronesKey))
        {
            FocusOnAllDrones();
            return false;
        }

        // 滚轮缩放（仅鼠标在小地图区域时）
        if (pointerOver)
        {
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 1e-4f)
            {
                if (ZoomAtUV(uv, scroll))
                {
                    viewChanged = true;
                }
            }
        }

        // 拖动平移
        if (Input.GetMouseButtonDown(panMouseButton) && pointerOver)
        {
            isDraggingPan = true;
            lastMousePos = Input.mousePosition;
        }

        if (isDraggingPan && Input.GetMouseButton(panMouseButton))
        {
            Vector3 cur = Input.mousePosition;
            Vector3 delta = cur - lastMousePos;
            lastMousePos = cur;

            if (PanByScreenDelta(delta))
            {
                viewChanged = true;
            }
        }

        if (isDraggingPan && Input.GetMouseButtonUp(panMouseButton))
        {
            isDraggingPan = false;
        }

        return viewChanged;
    }

    private bool PanByScreenDelta(Vector3 screenDelta)
    {
        if (miniMapImage == null) return false;
        RectTransform rt = miniMapImage.rectTransform;
        Rect rect = rt.rect;
        if (Mathf.Abs(rect.width) < 1e-4f || Mathf.Abs(rect.height) < 1e-4f) return false;

        float dx01 = screenDelta.x / rect.width;
        float dy01 = screenDelta.y / rect.height;

        Vector2 center = viewBoundsXY.center;
        center.x -= dx01 * viewBoundsXY.width;
        center.y -= dy01 * viewBoundsXY.height;

        Rect old = viewBoundsXY;
        SetViewCenter(center);
        return !ApproximatelyRect(old, viewBoundsXY);
    }

    private bool ZoomAtUV(Vector2 uv, float scrollDelta)
    {
        Rect old = viewBoundsXY;
        Vector2 worldBefore = UVToWorld(viewBoundsXY, uv);

        float currentScale = viewBoundsXY.width / Mathf.Max(0.0001f, fullBoundsXY.width);
        float factor = 1f - scrollDelta * zoomStep; // scroll > 0 时缩小视窗 => 放大
        factor = Mathf.Clamp(factor, 0.2f, 5f);
        float newScale = Mathf.Clamp(currentScale * factor, minZoomScale, maxZoomScale);

        SetViewScaleKeepCenter(newScale);

        Vector2 worldAfter = UVToWorld(viewBoundsXY, uv);
        Vector2 center = viewBoundsXY.center + (worldBefore - worldAfter);
        SetViewCenter(center);

        return !ApproximatelyRect(old, viewBoundsXY);
    }

    private void SetViewScaleAndCenter(float scale, Vector2 center)
    {
        scale = Mathf.Clamp(scale, minZoomScale, maxZoomScale);

        float w = Mathf.Max(0.0001f, fullBoundsXY.width * scale);
        float h = Mathf.Max(0.0001f, fullBoundsXY.height * scale);

        Rect r = new Rect(center.x - w * 0.5f, center.y - h * 0.5f, w, h);
        viewBoundsXY = ClampViewRectToFull(r);
    }

    private void SetViewScaleKeepCenter(float scale)
    {
        SetViewScaleAndCenter(scale, viewBoundsXY.center);
    }

    private void SetViewCenter(Vector2 center)
    {
        Rect r = new Rect(
            center.x - viewBoundsXY.width * 0.5f,
            center.y - viewBoundsXY.height * 0.5f,
            viewBoundsXY.width,
            viewBoundsXY.height
        );
        viewBoundsXY = ClampViewRectToFull(r);
    }

    private Rect ClampViewRectToFull(Rect r)
    {
        float w = Mathf.Clamp(r.width, fullBoundsXY.width * minZoomScale, fullBoundsXY.width * maxZoomScale);
        float h = Mathf.Clamp(r.height, fullBoundsXY.height * minZoomScale, fullBoundsXY.height * maxZoomScale);

        // 始终保持与 fullBounds 一致比例，避免几何变形
        float wantH = w / Mathf.Max(0.0001f, fullAspect);
        if (wantH > h) h = wantH;
        else w = h * fullAspect;

        w = Mathf.Min(w, fullBoundsXY.width);
        h = Mathf.Min(h, fullBoundsXY.height);

        float minX = fullBoundsXY.xMin;
        float maxX = fullBoundsXY.xMax - w;
        float minY = fullBoundsXY.yMin;
        float maxY = fullBoundsXY.yMax - h;

        float x = Mathf.Clamp(r.xMin, minX, Mathf.Max(minX, maxX));
        float y = Mathf.Clamp(r.yMin, minY, Mathf.Max(minY, maxY));

        return new Rect(x, y, w, h);
    }

    private static bool ApproximatelyRect(Rect a, Rect b)
    {
        return Mathf.Abs(a.x - b.x) < 1e-4f &&
               Mathf.Abs(a.y - b.y) < 1e-4f &&
               Mathf.Abs(a.width - b.width) < 1e-4f &&
               Mathf.Abs(a.height - b.height) < 1e-4f;
    }

    private Vector2 UVToWorld(Rect bounds, Vector2 uv)
    {
        float x = Mathf.Lerp(bounds.xMin, bounds.xMax, Mathf.Clamp01(uv.x));
        float z = Mathf.Lerp(bounds.yMin, bounds.yMax, Mathf.Clamp01(uv.y));
        return new Vector2(x, z);
    }

    private bool TryGetPointerUVOnMiniMap(out Vector2 uv)
    {
        uv = default;
        if (miniMapImage == null) return false;

        RectTransform rt = miniMapImage.rectTransform;
        Camera uiCam = GetUICamera(rt);
        if (!RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, uiCam)) return false;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, uiCam, out Vector2 local)) return false;

        Rect r = rt.rect;
        if (Mathf.Abs(r.width) < 1e-4f || Mathf.Abs(r.height) < 1e-4f) return false;

        float u = Mathf.InverseLerp(r.xMin, r.xMax, local.x);
        float v = Mathf.InverseLerp(r.yMin, r.yMax, local.y);
        uv = new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
        return true;
    }

    private Camera GetUICamera(RectTransform rt)
    {
        Canvas canvas = rt != null ? rt.GetComponentInParent<Canvas>() : null;
        if (canvas == null) return null;
        if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return canvas.worldCamera;
    }

    private void ResolveReferences()
    {
        if (campusLoader == null) campusLoader = FindObjectOfType<CampusJsonMapLoader>();
        if (campusGrid == null && campusLoader != null) campusGrid = campusLoader.GetComponent<CampusGrid2D>();
        if (campusGrid == null) campusGrid = FindObjectOfType<CampusGrid2D>();

        if (miniMapImage == null) miniMapImage = GetComponent<RawImage>();
    }

    private bool TryLoadCampusJson(out string outJson)
    {
        outJson = null;
        if (campusLoader == null) return false;

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

    private static void ApplyHorizontalScaleToFeatures(List<MiniFeature> features, ref Rect allBounds, float scale)
    {
        float safeScale = Mathf.Max(0.1f, scale);
        if (features == null || features.Count == 0 || Mathf.Abs(safeScale - 1f) < 1e-4f) return;

        Vector2 pivot = allBounds.center;
        for (int i = 0; i < features.Count; i++)
        {
            MiniFeature feature = features[i];
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

    private bool TryParseFeatures(string json, List<MiniFeature> outFeatures, out Rect outBounds)
    {
        outFeatures.Clear();
        outBounds = default;

        JObject root;
        try { root = JObject.Parse(json); }
        catch { return false; }

        JArray features = root["features"] as JArray;
        if (features == null) return false;

        bool hasBounds = false;
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        for (int i = 0; i < features.Count; i++)
        {
            JObject obj = features[i] as JObject;
            if (obj == null) continue;

            MiniFeature f = new MiniFeature();
            f.kind = ((string)obj["kind"] ?? "other").Trim().ToLowerInvariant();

            JObject ringsObj = obj["rings"] as JObject;
            if (ringsObj != null)
            {
                JArray outer = ringsObj["outer"] as JArray;
                if (outer != null)
                {
                    for (int r = 0; r < outer.Count; r++)
                    {
                        List<Vector2> ring = ReadLineOrRing(outer[r] as JArray);
                        if (ring.Count >= 2) f.outerRings.Add(ring);
                    }
                }

                JArray inner = ringsObj["inner"] as JArray;
                if (inner != null)
                {
                    for (int r = 0; r < inner.Count; r++)
                    {
                        List<Vector2> ring = ReadLineOrRing(inner[r] as JArray);
                        if (ring.Count >= 2) f.innerRings.Add(ring);
                    }
                }
            }

            JArray line = obj["points_xy_m"] as JArray;
            if (line != null)
            {
                for (int p = 0; p < line.Count; p++)
                {
                    f.linePoints.Add(ReadXY(line[p]));
                }
            }

            if (f.outerRings.Count == 0 && f.innerRings.Count == 0 && f.linePoints.Count < 2) continue;

            ComputeFeatureBounds(f, out Rect fb, out bool fv);
            f.bounds = fb;
            f.boundsValid = fv;

            if (fv)
            {
                if (!hasBounds)
                {
                    minX = fb.xMin; minY = fb.yMin; maxX = fb.xMax; maxY = fb.yMax;
                    hasBounds = true;
                }
                else
                {
                    minX = Mathf.Min(minX, fb.xMin);
                    minY = Mathf.Min(minY, fb.yMin);
                    maxX = Mathf.Max(maxX, fb.xMax);
                    maxY = Mathf.Max(maxY, fb.yMax);
                }
            }

            outFeatures.Add(f);
        }

        if (!hasBounds || outFeatures.Count == 0) return false;
        outBounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return outBounds.width > 0f && outBounds.height > 0f;
    }

    private static Vector2 ReadXY(JToken tok)
    {
        JArray a = tok as JArray;
        if (a == null || a.Count < 2) return Vector2.zero;
        return new Vector2(a[0].Value<float>(), a[1].Value<float>());
    }

    private static List<Vector2> ReadLineOrRing(JArray arr)
    {
        List<Vector2> pts = new List<Vector2>();
        if (arr == null) return pts;
        for (int i = 0; i < arr.Count; i++)
        {
            JArray p = arr[i] as JArray;
            if (p == null || p.Count < 2) continue;
            pts.Add(new Vector2(p[0].Value<float>(), p[1].Value<float>()));
        }
        return pts;
    }

    private static void ComputeFeatureBounds(MiniFeature f, out Rect bounds, out bool valid)
    {
        valid = false;
        bounds = default;
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
                List<Vector2> pts = rings[r];
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
        for (int i = 0; i < f.linePoints.Count; i++)
        {
            Vector2 p = f.linePoints[i];
            minX = Mathf.Min(minX, p.x);
            minY = Mathf.Min(minY, p.y);
            maxX = Mathf.Max(maxX, p.x);
            maxY = Mathf.Max(maxY, p.y);
            any = true;
        }

        valid = any;
        if (valid) bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    private void EnsureTextureAndBuffers()
    {
        textureSize = Mathf.Max(128, textureSize);
        int count = textureSize * textureSize;

        bool needCreate = miniMapTexture == null || miniMapTexture.width != textureSize || miniMapTexture.height != textureSize;
        if (needCreate)
        {
            if (miniMapTexture != null) Destroy(miniMapTexture);
            miniMapTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            miniMapTexture.wrapMode = TextureWrapMode.Clamp;
            miniMapTexture.filterMode = FilterMode.Point;
            miniMapTexture.name = "CampusMiniMapTexture";
        }

        if (staticPixels == null || staticPixels.Length != count) staticPixels = new Color32[count];
        if (framePixels == null || framePixels.Length != count) framePixels = new Color32[count];

        miniMapImage.texture = miniMapTexture;
    }

    private void RenderStaticContours()
    {
        if (miniMapTexture == null || staticPixels == null) return;

        Color32 bg = backgroundColor;
        for (int i = 0; i < staticPixels.Length; i++) staticPixels[i] = bg;

        for (int i = 0; i < parsedFeatures.Count; i++)
        {
            MiniFeature f = parsedFeatures[i];
            if (f.boundsValid && !f.bounds.Overlaps(viewBoundsXY)) continue;

            Color32 c = f.kind == "building" ? (Color32)buildingOutlineColor : (Color32)otherOutlineColor;
            DrawRingCollection(staticPixels, f.outerRings, c);
            DrawRingCollection(staticPixels, f.innerRings, c);
            DrawPolyline(staticPixels, f.linePoints, c, false);
        }
    }

    private void RefreshDroneDots()
    {
        if (miniMapTexture == null || staticPixels == null || framePixels == null) return;
        if (viewBoundsXY.width <= 0f || viewBoundsXY.height <= 0f) return;

        Array.Copy(staticPixels, framePixels, staticPixels.Length);

        IntelligentAgent[] agents = FindObjectsOfType<IntelligentAgent>();
        Color32 dot = droneDotColor;
        int radius = Mathf.Max(1, droneDotRadiusPx);

        for (int i = 0; i < agents.Length; i++)
        {
            IntelligentAgent a = agents[i];
            if (a == null) continue;

            if (showOnlyQuadcopter && (a.Properties == null || a.Properties.Type != AgentType.Quadcopter))
            {
                continue;
            }

            Vector3 wp = a.transform.position;
            if (!WorldInView(wp.x, wp.z)) continue;

            Vector2Int pix = WorldToPixel(wp.x, wp.z);
            DrawFilledCircle(framePixels, pix.x, pix.y, radius, dot);
        }

        miniMapTexture.SetPixels32(framePixels);
        miniMapTexture.Apply(false, false);
    }

    private void DrawRingCollection(Color32[] buffer, List<List<Vector2>> rings, Color32 color)
    {
        if (rings == null) return;
        for (int i = 0; i < rings.Count; i++)
        {
            DrawPolyline(buffer, rings[i], color, true);
        }
    }

    private void DrawPolyline(Color32[] buffer, List<Vector2> pts, Color32 color, bool closeLoop)
    {
        if (pts == null || pts.Count < 2) return;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2Int a = WorldToPixel(pts[i].x, pts[i].y);
            Vector2Int b = WorldToPixel(pts[i + 1].x, pts[i + 1].y);
            DrawLine(buffer, a.x, a.y, b.x, b.y, color);
        }

        if (closeLoop)
        {
            Vector2Int p0 = WorldToPixel(pts[0].x, pts[0].y);
            Vector2Int p1 = WorldToPixel(pts[pts.Count - 1].x, pts[pts.Count - 1].y);
            if (p0 != p1) DrawLine(buffer, p1.x, p1.y, p0.x, p0.y, color);
        }
    }

    private void DrawLine(Color32[] buffer, int x0, int y0, int x1, int y1, Color32 color)
    {
        int dx = Mathf.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        int width = Mathf.Max(1, outlineWidthPx);
        while (true)
        {
            if (width <= 1) DrawPixel(buffer, x0, y0, color);
            else DrawFilledCircle(buffer, x0, y0, width - 1, color);

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private void DrawFilledCircle(Color32[] buffer, int cx, int cy, int radius, Color32 color)
    {
        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= textureSize) continue;

            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int x = cx + dx;
                if (x < 0 || x >= textureSize) continue;
                buffer[y * textureSize + x] = color;
            }
        }
    }

    private void DrawPixel(Color32[] buffer, int x, int y, Color32 color)
    {
        if (x < 0 || x >= textureSize || y < 0 || y >= textureSize) return;
        buffer[y * textureSize + x] = color;
    }

    private bool WorldInView(float x, float z)
    {
        return x >= viewBoundsXY.xMin && x <= viewBoundsXY.xMax && z >= viewBoundsXY.yMin && z <= viewBoundsXY.yMax;
    }

    private Vector2Int WorldToPixel(float x, float z)
    {
        float u = (x - viewBoundsXY.xMin) / Mathf.Max(0.0001f, viewBoundsXY.width);
        float v = (z - viewBoundsXY.yMin) / Mathf.Max(0.0001f, viewBoundsXY.height);

        int px = Mathf.RoundToInt(u * (textureSize - 1));
        int py = Mathf.RoundToInt(v * (textureSize - 1));
        return new Vector2Int(px, py);
    }

    private void ApplyRawImageAspect()
    {
        RectTransform rt = miniMapImage != null ? miniMapImage.rectTransform : null;
        if (rt == null) return;

        float w = Mathf.Max(0.0001f, fullBoundsXY.width);
        float h = Mathf.Max(0.0001f, fullBoundsXY.height);
        float aspect = w / h;

        Vector2 size = rt.sizeDelta;
        if (size.x <= 1f || size.y <= 1f) return;

        size.x = size.y * aspect;
        rt.sizeDelta = size;
    }
}
