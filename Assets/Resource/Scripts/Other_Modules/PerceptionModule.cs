using System;
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 智能体感知模块 - 仅可视化障碍物和资源的检测射线
/// 功能：负责智能体（无人机/无人车）的环境感知，检测障碍物、资源和其他智能体，
/// 并通过射线、球体等可视化元素在场景中展示感知结果
/// </summary>
public class PerceptionModule : MonoBehaviour
{
    [Header("基础感知配置")]
    public float updateInterval = 0.5f;                // 感知更新间隔（秒），控制多久执行一次感知检测
    public bool showGizmos = true;                     // 是否显示Gizmos可视化（Scene视图）
    public bool showGameViewVisualization = true;      // 是否显示Game视图中的LineRenderer可视化
    public bool showDetectionRays = true;              // 是否显示检测射线（Game视图）
    public int raySimplificationFactor = 2;            // 射线简化因子，每隔n条射线显示一条，减少绘制压力
    private float lastPerceptionTime = 0f;             // 上一次执行感知的时间戳

    [Header("分层检测配置")]
    public LayerMask detectionLayers;                  // 需要检测的总图层（会与下方分类图层合并）
    public LayerMask obstacleLayers;                   // 障碍物图层（除资源外的小节点）
    public LayerMask resourceLayers;                   // 资源点图层
    public LayerMask agentLayers;                      // 智能体图层
    public bool autoConfigureLayerMasks = true;        // 自动按 Layer 名称初始化分类图层
    [Tooltip("自动配置时使用的障碍层名称（除 ResourcePoint 外其余小节点）")]
    public string obstacleLayerName = "Obstacle";
    [Tooltip("自动配置时使用的资源层名称")]
    public string resourceLayerName = "Resource";
    [Tooltip("自动配置时使用的智能体层名称")]
    public string agentLayerName = "Agent";
    [Tooltip("地面层（完全不显示其射线）")]
    public LayerMask groundLayer;                      // 地面图层（检测到地面时不可视化）

    [Header("无人机专属配置")]
    public float droneVerticalRange = 10f;             // 无人机垂直检测范围（未在核心逻辑中直接使用，预留）
    public float droneScanAngleStep = 15f;             // 无人机扫描角度步长（每15度发射一条射线）

    [Header("无人车专属配置")]
    public float groundHorizontalAngle = 270f;         // 无人车水平检测角度（270度扇形范围）
    public float groundHeightRange = 2f;               // 无人车高度检测范围（只检测此高度内的物体）

    [Header("马卡龙色系可视化（仅用于障碍物和资源）")]
    [Tooltip("资源射线/标记颜色（淡蓝绿）")]
    public Color resourceColor = new Color(0.67f, 0.85f, 0.89f); // 资源可视化颜色
    [Tooltip("障碍物射线/标记颜色（淡粉）")]
    public Color obstacleColor = new Color(0.96f, 0.82f, 0.87f); // 障碍物可视化颜色
    [Tooltip("感知范围颜色（淡青）")]
    public Color rangeColor = new Color(0.72f, 0.89f, 0.87f, 0.4f); // 感知范围线颜色
    public Color rangeFillColor = new Color(0.72f, 0.89f, 0.87f, 0.15f); // 感知范围填充色
    public float lineWidth = 0.03f;                    // 可视化线宽
    public Vector2 sphereSizeRange = new Vector2(0.08f, 0.15f); // 检测点球体大小范围（闪烁动画用）
    public float blinkFrequency = 2f;                  // 球体闪烁频率（每秒闪烁次数）
    [Min(0.5f)] public float markerLifetime = 3f;      // 检测点球体保留时长（秒）
    [Min(0.2f)] public float rayLifetime = 2f;         // 射线可视化保留时长（秒）

    [Header("高可见度增强")]
    public bool enhanceVisualization = true;            // 是否启用高可见度模式（线更粗、标记更大更亮）
    [Range(1f, 6f)] public float lineWidthBoost = 2.8f; // 高可见度线宽倍率
    [Range(1f, 6f)] public float markerSizeBoost = 2.4f; // 高可见度标记大小倍率
    [Min(0f)] public float markerHeightOffset = 0.35f; // 标记抬高高度（避免被模型/地面遮住）
    [Range(0.2f, 1f)] public float rayAlpha = 0.95f;   // 检测射线透明度
    [Range(0.2f, 1f)] public float markerMinAlpha = 0.55f; // 标记最小透明度（增强可见性）
    [Range(0f, 6f)] public float markerEmissionIntensity = 2.0f; // 标记发光强度

    // 感知数据存储
    public List<SmallNodeData> detectedObjects = new List<SmallNodeData>(); // 检测到的小节点列表（树木/行人/车辆/资源等）
    public List<GameObject> nearbyAgents = new List<GameObject>(); // 附近的其他智能体列表
    private IntelligentAgent agent;                                // 所属智能体组件引用
    private AgentType agentType;                                   // 智能体类型（无人机/无人车）
    // public MapGenerator mapGenerator;                             // 旧地图生成器（已弃用）

    [Header("小节点共享感知库")]
    public bool enableSharedSmallNodeRegistry = true;               // 是否启用全局共享的小节点注册表
    public bool clearSharedRegistryOnStart = true;                  // 是否在场景启动时清空共享库（保证任务开始前为空）
    [Min(1f)] public float smallNodeQueryRadius = 25f;              // 默认查询半径（用于快照接口）
    [Min(1f)] public float dynamicNodeTtl = 10f;                    // 动态小节点失效时间（秒）
    [Range(0.01f, 1f)] public float confidenceIncrement = 0.15f;    // 每次重复观测增加的置信度
    public bool cleanupExpiredDynamicNodes = true;                  // 是否定期清理过期动态节点
    public bool logSmallNodeRegistry = false;                       // 是否输出小节点注册日志

    [Header("校园地点感知（建筑/地图要素）")]
    public bool enableCampusFeaturePerception = true;               // 是否启用校园地点感知
    public bool onlyTrackBuildings = true;                          // 是否只记录建筑（false时记录所有有名称地点）
    public bool enableGridNeighborhoodFeaturePerception = true;     // 是否通过邻域网格补充“看见的地点”
    [Min(4)] public int maxObservedFeatureCellsPerFeature = 24;     // 每个地点保留的样本网格上限
    [Min(1)] public int maxPerceptionFeatureCountPerTick = 32;      // 每轮最多记录地点数量（防止过载）

    // 调试可视化（仅记录障碍物和资源）
    private List<DetectionPointInfo> detectionPoints = new List<DetectionPointInfo>(); // 检测点信息列表（用于可视化）
    private List<GameObject> sphereMarkers = new List<GameObject>(); // 检测点球体标记列表

    // 可视化组件
    private LineRenderer rangeLineRenderer;          // 用于绘制感知范围的线渲染器
    private LineRenderer detectionLineRenderer;      // 用于绘制检测射线的线渲染器
    private Material lineMaterial;                   // 线渲染器材质
    private Material sphereMaterial;                 // 球体标记材质
    private MaterialPropertyBlock spherePropertyBlock; // 球体颜色复用块，减少GC分配
    private float lastVizUpdateTime = 0f;
    public float vizInterval = 0.1f; // 每 0.1 秒更新一次可视化（10 fps 足够流畅）
    // --- 对象池（新增） ---
    private Queue<GameObject> spherePool = new Queue<GameObject>();
    private int poolSize = 50; // 最多缓存 50 个球体
    private static bool sharedRegistryInitialized = false;          // 共享库启动标记（仅首个感知模块生效）
    private readonly HashSet<int> sensedObjectIdsThisTick = new HashSet<int>(); // 当前感知周期去重
    private readonly Dictionary<string, CampusFeaturePerceptionData> detectedCampusFeatures = new Dictionary<string, CampusFeaturePerceptionData>(); // 当前感知周期的地点观测
    [Min(8)] public int maxVisualPoints = 160;                     // 单周期最多可视化点数（防止线段和球体过载）
    private CampusGrid2D campusGrid;                                // 全局校园逻辑网格（只用于地点语义读取）

    private struct MarkerMeta
    {
        public float createdTime;
        public Color baseColor;
    }
    private readonly Dictionary<GameObject, MarkerMeta> markerMeta = new Dictionary<GameObject, MarkerMeta>();



    /// <summary>
    /// 检测点信息结构体（仅记录需要可视化的类型）
    /// </summary>
    private struct DetectionPointInfo
    {
        public Vector3 position;     // 检测点位置
        public SmallNodeType type;   // 小节点类型
        public float timestamp;      // 检测时间戳（用于控制可视化时效）
        public int rayIndex;         // 射线索引（用于射线简化显示）

        // 构造函数：初始化检测点信息
        public DetectionPointInfo(Vector3 pos, SmallNodeType nodeType, int index)
        {
            position = pos;
            type = nodeType;
            timestamp = Time.time;
            rayIndex = index;
        }
    }

    /// <summary>
    /// 初始化函数：获取智能体组件，初始化感知范围和可视化组件
    /// </summary>
    private void Start()
    {
        if (enableSharedSmallNodeRegistry && clearSharedRegistryOnStart && !sharedRegistryInitialized)
        {
            SmallNodeRegistry.ClearAll();
            sharedRegistryInitialized = true;
        }

        // 获取所属智能体组件
        agent = GetComponent<IntelligentAgent>();
        if (agent != null)
        {
            agentType = agent.Properties.Type; // 记录智能体类型
            InitializePerceptionRange();      // 初始化感知范围配置
        }
        else
        {
            Debug.LogError("PerceptionModule: 未找到所属智能体组件");
        }
        campusGrid = FindObjectOfType<CampusGrid2D>();
        InitializeVisualizationComponents(); // 初始化可视化所需组件
    }

    /// <summary>
    /// 公开接口：获取当前感知周期内检测到的小节点列表。
    /// </summary>
    public List<SmallNodeData> GetDetectedObjects()
    {
        return detectedObjects;
    }
    public void Update()
    {
        // 定时执行感知更新
        if (Time.time - lastPerceptionTime >= updateInterval)
        {
            SenseOnce();
        }
    }
    /// <summary>
    /// 手动触发一次感知检测的公开方法
    /// 功能：执行一次完整的感知更新，并刷新可视化
    /// </summary>
    public void SenseOnce()
    {
        UpdatePerception();
        if (enableSharedSmallNodeRegistry && cleanupExpiredDynamicNodes)
        {
            SmallNodeRegistry.CleanupExpiredDynamic(Time.time, dynamicNodeTtl);
        }

        // 限制可视化帧率，避免每 Update 都生成一堆线
        if (Time.time - lastVizUpdateTime >= vizInterval)
        {
            if (showDetectionRays) DrawRealTimeDetectionRays();
            UpdateSphereMarkers();

            lastVizUpdateTime = Time.time;
        }

        lastPerceptionTime = Time.time;
    }
    // --- 对象池：取出球体（新增） ---
    private GameObject GetSphere()
    {
        if (spherePool.Count > 0)
        {
            var obj = spherePool.Dequeue();
            obj.SetActive(true);
            return obj;
        }

        // 原来使用 CreatePrimitive 的逻辑（复制过来）
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

    // --- 对象池：回收球体（新增） ---
    private void RecycleSphere(GameObject sphere)
    {
        if (spherePool.Count < poolSize)
        {
            sphere.SetActive(false);
            spherePool.Enqueue(sphere);
        }
        else
        {
            Destroy(sphere);
        }
    }


    /// <summary>
    /// 初始化可视化组件（线渲染器、材质等）
    /// </summary>
    private void InitializeVisualizationComponents()
    {
        // 创建线渲染器材质（使用Sprites/Default shader）
        lineMaterial = new Material(Shader.Find("Sprites/Default"));
        lineMaterial.hideFlags = HideFlags.DontSave; // 不在Hierarchy显示

        // 创建球体标记材质（半透明效果）
        sphereMaterial = new Material(Shader.Find("Standard"));
        sphereMaterial.hideFlags = HideFlags.DontSave;
        sphereMaterial.renderQueue = 3000; // 透明队列
        // 配置半透明混合模式
        sphereMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        sphereMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        sphereMaterial.SetInt("_ZWrite", 0); // 不写入深度缓冲（避免遮挡）
        sphereMaterial.DisableKeyword("_ALPHATEST_ON");
        sphereMaterial.EnableKeyword("_ALPHABLEND_ON");
        sphereMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        sphereMaterial.EnableKeyword("_EMISSION");
        spherePropertyBlock = new MaterialPropertyBlock();

        // 创建感知范围线渲染器
        GameObject rangeObj = new GameObject("RangeVisualization");
        //rangeObj.transform.parent = transform; // 父节点设为当前物体（跟随移动）
        rangeObj.transform.parent = null; // 或设置为专门的可视化父节点
        rangeObj.transform.position = Vector3.zero; // 避免位置继承

        rangeLineRenderer = rangeObj.AddComponent<LineRenderer>();
        rangeLineRenderer.material = lineMaterial;
        rangeLineRenderer.widthMultiplier = GetEffectiveLineWidth();
        rangeLineRenderer.useWorldSpace = true; // 使用世界坐标绘制
        rangeLineRenderer.hideFlags = HideFlags.DontSave;
        rangeLineRenderer.alignment = LineAlignment.View;
        rangeLineRenderer.numCapVertices = 6;
        rangeLineRenderer.numCornerVertices = 6;
        rangeLineRenderer.textureMode = LineTextureMode.Stretch;
        rangeLineRenderer.sortingOrder = 5000;
        rangeLineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rangeLineRenderer.receiveShadows = false;

        // 创建检测射线线渲染器
        GameObject detectionObj = new GameObject("DetectionVisualization");
        //detectionObj.transform.parent = transform;
        detectionObj.transform.parent = null;
        detectionLineRenderer = detectionObj.AddComponent<LineRenderer>();
        detectionLineRenderer.material = lineMaterial;
        detectionLineRenderer.widthMultiplier = GetEffectiveLineWidth() * 0.75f; // 射线宽度增强
        detectionLineRenderer.useWorldSpace = true;
        detectionLineRenderer.hideFlags = HideFlags.DontSave;
        detectionLineRenderer.alignment = LineAlignment.View;
        detectionLineRenderer.numCapVertices = 6;
        detectionLineRenderer.numCornerVertices = 6;
        detectionLineRenderer.textureMode = LineTextureMode.Stretch;
        detectionLineRenderer.sortingOrder = 5001;
        detectionLineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        detectionLineRenderer.receiveShadows = false;
    }

    /// <summary>
    /// 初始化感知范围配置（图层设置）
    /// </summary>
    private void InitializePerceptionRange()
    {
        if (autoConfigureLayerMasks)
        {
            if (obstacleLayers == 0)
            {
                int l = LayerMask.NameToLayer(obstacleLayerName);
                if (l >= 0) obstacleLayers = (1 << l);
            }
            if (resourceLayers == 0)
            {
                int l = LayerMask.NameToLayer(resourceLayerName);
                if (l >= 0) resourceLayers = (1 << l);
            }
            if (agentLayers == 0)
            {
                int l = LayerMask.NameToLayer(agentLayerName);
                if (l >= 0) agentLayers = (1 << l);
            }
        }

        // 总检测掩码 = 手工 detectionLayers + 分类图层
        detectionLayers |= obstacleLayers;
        detectionLayers |= resourceLayers;
        detectionLayers |= agentLayers;

        // 若最终检测层为空，给出兜底掩码，避免“看起来有物体但完全检测不到”
        if (detectionLayers == 0)
        {
            detectionLayers = Physics.DefaultRaycastLayers;
            Debug.LogWarning("[PerceptionModule] detectionLayers 为空，已自动回退到 Physics.DefaultRaycastLayers。请检查 Obstacle/Resource/Agent 层是否已在 TagManager 中创建。");
        }

        // 自动初始化地面层（若未手动设置）
        if (groundLayer == 0)
        {
            int l = LayerMask.NameToLayer("Ground");
            if (l >= 0) groundLayer = (1 << l);
        }
    }

    /// <summary>
    /// 核心感知更新方法 - 仅记录障碍物和资源的可视化信息
    /// 功能：清空历史数据，根据智能体类型执行对应检测逻辑，更新可视化
    /// </summary>
    private void UpdatePerception()
    {
        if (agent == null) return; // 智能体不存在则退出
        // 清空历史数据
        detectedObjects.Clear();
        nearbyAgents.Clear();
        CleanupOldSphereMarkers(); // 清除旧的球体标记
        detectionPoints.Clear();   // 清空检测点信息（只保留当前帧）
        sensedObjectIdsThisTick.Clear();
        detectedCampusFeatures.Clear();

        // 根据智能体类型执行对应检测逻辑
        if (agentType == AgentType.Quadcopter)
        {
            PerceiveAsDrone();
        }
        else
        {
            PerceiveAsGroundVehicle();
        }

        // 补充校园地点感知（建筑等大目标），避免仅靠小节点图层时无法感知建筑信息。
        if (enableCampusFeaturePerception && enableGridNeighborhoodFeaturePerception)
        {
            PerceiveCampusFeaturesByGridNeighborhood();
        }

        UpdateAgentState(); // 更新智能体状态（将检测结果同步到智能体）
        DrawGameViewVisualization(); // 绘制Game视图的可视化元素
    }

    /// <summary>
    /// 无人机感知逻辑
    /// 功能：以当前位置为中心，发射360度水平+±45度垂直的射线网，检测障碍物和资源
    /// </summary>
    private void PerceiveAsDrone()
    {
        var origin = transform.position; // 射线起点（无人机位置）
        var maxRange = agent.Properties.PerceptionRange; // 感知最大范围,半径距离（从智能体属性获取）
        int rayIndex = 0; // 射线索引（用于射线简化）

        // 水平方向：0~360度，每隔droneScanAngleStep度发射一条射线
        for (float yaw = 0; yaw < 360; yaw += droneScanAngleStep)
        {
            // 垂直方向：-45~45度，每隔droneScanAngleStep度发射一条射线
            for (float pitch = -45; pitch <= 45; pitch += droneScanAngleStep)
            {
                // 计算射线方向（基于欧拉角旋转）
                Quaternion rotation = Quaternion.Euler(pitch, yaw, 0);
                Vector3 direction = rotation * Vector3.forward;

                // 发射射线检测
                if (Physics.Raycast(origin, direction, out RaycastHit hit, maxRange, detectionLayers))
                {
                    // 处理有效的检测结果（过滤地面，只保留障碍物和资源）
                    ProcessValidDetection(hit, rayIndex);
                }
                rayIndex++; // 射线索引递增
            }
        }

        // 额外检测上下方向（垂直射线）
        CheckVerticalDetection(origin, maxRange, rayIndex);
    }

    /// <summary>
    /// 无人车感知逻辑
    /// 功能：以车身前方为中心，发射270度水平扇形射线，检测特定高度范围内的物体
    /// </summary>
    private void PerceiveAsGroundVehicle()
    {
        Vector3 origin = transform.position + Vector3.up * 0.5f;
        float maxRange = agent.Properties.PerceptionRange;
        Vector3 forward = transform.forward;

        float halfAngle = groundHorizontalAngle / 2f;
        int rayCount = Mathf.RoundToInt(groundHorizontalAngle / 5f);

        float sphereRadius = 0.4f; // 关键：感知“厚度”

        int rayIndex = 0;

        for (int i = 0; i <= rayCount; i++)
        {
            float angle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / rayCount);
            Quaternion rotation = Quaternion.Euler(0, angle, 0);

            Vector3 direction = rotation * forward;
            direction.y = 0;
            direction.Normalize();

            if (Physics.SphereCast(
                origin,
                sphereRadius,
                direction,
                out RaycastHit hit,
                maxRange,
                detectionLayers))
            {
                // 命中点高度差过滤（现在可以放宽）
                if (Mathf.Abs(hit.collider.bounds.center.y - origin.y) <= groundHeightRange)
                {
                    ProcessValidDetection(hit, rayIndex);
                }
            }

            rayIndex++;
        }
    }


    /// <summary>
    /// 处理有效的检测结果（仅保留障碍物和资源，排除地面和其他智能体）
    /// </summary>
    private void ProcessValidDetection(RaycastHit hit, int rayIndex)
    {
        GameObject hitObject = hit.collider != null ? hit.collider.gameObject : null;
        if (hitObject == null) return;

        if (hit.collider.attachedRigidbody != null)
        {
            hitObject = hit.collider.attachedRigidbody.gameObject;
        }
        else
        {
            SmallNodeRuntimeInfo rootInfo = hitObject.GetComponentInParent<SmallNodeRuntimeInfo>();
            if (rootInfo != null) hitObject = rootInfo.gameObject;
        }

        // 1. 排除地面（地面层物体不可视化）
        bool isGround = (1 << hitObject.layer & groundLayer) != 0;
        if (isGround) return;

        // 每个感知周期按对象去重，避免同一目标被多条射线重复记录
        int objId = hitObject.GetInstanceID();
        if (!sensedObjectIdsThisTick.Add(objId)) return;

        // 2. 判断检测类型（优先：分层 -> 运行时类型组件 -> 标签/名称）
        SmallNodeType detectedType = ResolveDetectedType(hitObject);
        if (detectedType == SmallNodeType.Agent)
        {
            IntelligentAgent otherAgent = hitObject.GetComponentInParent<IntelligentAgent>();
            if (otherAgent != null && otherAgent.gameObject != gameObject)
            {
                if (!nearbyAgents.Contains(otherAgent.gameObject))
                {
                    nearbyAgents.Add(otherAgent.gameObject);
                }
                return; // 真实智能体只写入 NearbyAgents，不参与小节点可视化
            }
        }

        // 使用物体的中心坐标而非击中点
        Vector3 objectPosition = hitObject.transform.position;
        RegisterCampusFeatureObservationByWorld(hit.point);
        RegisterCampusFeatureObservationByWorld(objectPosition);
        UpdatePerceptionGrid(objectPosition, detectedType, hitObject);

        // 3. 记录小节点信息（用于数据存储和可视化）
        if (detectedType == SmallNodeType.Unknown)
        {
            detectedType = SmallNodeType.TemporaryObstacle;
        }

        if (detectedType != SmallNodeType.Unknown)
        {
            detectedObjects.Add(new SmallNodeData
            {
                NodeId = BuildSmallNodeId(hitObject, detectedType, objectPosition),
                NodeType = detectedType,
                WorldPosition = objectPosition,
                IsDynamic = InferIsDynamic(hitObject, detectedType),
                BlocksMovement = InferBlocksMovement(detectedType),
                FirstSeenTime = Time.time,
                LastSeenTime = Time.time,
                Confidence = Mathf.Clamp01(confidenceIncrement),
                SeenCount = 1,
                SourceAgentId = (agent != null && agent.Properties != null) ? agent.Properties.AgentID : string.Empty,
                DisplayName = hitObject.name,
                SceneObject = hitObject
            });

            if (detectionPoints.Count < Mathf.Max(8, maxVisualPoints))
            {
                Vector3 visualPosition = GetVisualAnchorPosition(objectPosition, hitObject);
                detectionPoints.Add(new DetectionPointInfo(visualPosition, detectedType, rayIndex)); // 存储可视化点
                CreateSphereMarker(visualPosition, detectedType); // 创建球体标记
            }
        }
    }
    /// <summary>
    /// 更新小节点共享感知库（新逻辑）：
    /// 1) 不再写入旧 PerceivedGrid。
    /// 2) CampusGrid2D 仅用于全局可通行与 A*，这里专注“小节点”发现与登记。
    /// 3) 注册表默认空，只有感知命中后才会新增节点。
    /// </summary>
    private void UpdatePerceptionGrid(Vector3 worldPosition, SmallNodeType nodeType, GameObject terrainObject)
    {
        if (!enableSharedSmallNodeRegistry) return;

        SmallNodeType smallType = InferSmallNodeType(terrainObject, nodeType);
        bool isDynamic = InferIsDynamic(terrainObject, smallType);
        bool blocksMovement = InferBlocksMovement(smallType);

        string agentId = (agent != null && agent.Properties != null) ? agent.Properties.AgentID : string.Empty;

        SmallNodeData data = new SmallNodeData
        {
            NodeId = BuildSmallNodeId(terrainObject, smallType, worldPosition),
            NodeType = smallType,
            WorldPosition = worldPosition,
            IsDynamic = isDynamic,
            BlocksMovement = blocksMovement,
            FirstSeenTime = Time.time,
            LastSeenTime = Time.time,
            Confidence = Mathf.Clamp01(confidenceIncrement),
            SeenCount = 1,
            SourceAgentId = agentId,
            DisplayName = terrainObject != null ? terrainObject.name : smallType.ToString(),
            SceneObject = terrainObject
        };

        SmallNodeRegistry.RegisterOrUpdate(data, confidenceIncrement);

        if (logSmallNodeRegistry)
        {
            Debug.Log($"[PerceptionModule] 小节点登记: id={data.NodeId}, type={data.NodeType}, dynamic={data.IsDynamic}, block={data.BlocksMovement}");
        }
    }

    /// <summary>
    /// 在网格邻域内补充“当前看见的校园地点”。
    /// 说明：
    /// 1) 这是局部感知，不是全图导出；
    /// 2) 仅把感知半径内的地点写入本轮状态；
    /// 3) 地面车可额外受扇形视角限制。
    /// </summary>
    private void PerceiveCampusFeaturesByGridNeighborhood()
    {
        if (!enableCampusFeaturePerception) return;
        if (campusGrid == null) campusGrid = FindObjectOfType<CampusGrid2D>();
        if (campusGrid == null || campusGrid.cellTypeGrid == null) return;

        float range = (agent != null && agent.Properties != null)
            ? Mathf.Max(1f, agent.Properties.PerceptionRange)
            : 15f;

        Vector2Int center = campusGrid.WorldToGrid(transform.position);
        if (!campusGrid.IsInBounds(center.x, center.y)) return;

        int radiusCell = Mathf.CeilToInt(range / Mathf.Max(0.1f, campusGrid.cellSize));
        int featureCount = 0;

        for (int gx = center.x - radiusCell; gx <= center.x + radiusCell; gx++)
        {
            for (int gz = center.y - radiusCell; gz <= center.y + radiusCell; gz++)
            {
                if (!campusGrid.IsInBounds(gx, gz)) continue;
                if (!campusGrid.TryGetCellFeatureInfo(gx, gz, out string uid, out string name, out CampusGrid2D.CellType type, out bool blocked)) continue;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (onlyTrackBuildings && type != CampusGrid2D.CellType.Building) continue;

                Vector3 wp = campusGrid.GridToWorldCenter(gx, gz, 0f);
                Vector3 delta = wp - transform.position;
                delta.y = 0f;
                float dist = delta.magnitude;
                if (dist > range) continue;

                // 地面车按扇形做近似可见约束；无人机默认 360 度。
                if (agentType != AgentType.Quadcopter && groundHorizontalAngle < 359.9f)
                {
                    float angle = Vector3.Angle(transform.forward, delta.normalized);
                    if (angle > groundHorizontalAngle * 0.5f) continue;
                }

                RegisterCampusFeatureObservation(gx, gz, wp, uid, name, type.ToString(), blocked);
                featureCount = detectedCampusFeatures.Count;
                if (featureCount >= Mathf.Max(1, maxPerceptionFeatureCountPerTick)) return;
            }
        }
    }

    /// <summary>
    /// 按世界坐标登记一次地点观测（射线命中点/物体中心均可复用此接口）。
    /// </summary>
    private void RegisterCampusFeatureObservationByWorld(Vector3 worldPos)
    {
        if (!enableCampusFeaturePerception) return;
        if (campusGrid == null) return;
        if (!campusGrid.TryGetCellFeatureInfoByWorld(worldPos, out Vector2Int grid, out string uid, out string name, out CampusGrid2D.CellType type, out bool blocked))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) return;
        if (onlyTrackBuildings && type != CampusGrid2D.CellType.Building) return;
        RegisterCampusFeatureObservation(grid.x, grid.y, worldPos, uid, name, type.ToString(), blocked);
    }

    /// <summary>
    /// 把一次网格观测合并进本轮地点感知缓存。
    /// </summary>
    private void RegisterCampusFeatureObservation(int gx, int gz, Vector3 sampleWorld, string uid, string name, string kind, bool blocked)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(uid)) return;

        string featureKey;
        if (!string.IsNullOrWhiteSpace(uid)) featureKey = $"uid:{uid.Trim()}";
        else featureKey = $"name:{name.Trim().ToLowerInvariant()}";

        if (!detectedCampusFeatures.TryGetValue(featureKey, out CampusFeaturePerceptionData data))
        {
            data = new CampusFeaturePerceptionData
            {
                FeatureUid = uid ?? string.Empty,
                FeatureName = name ?? string.Empty,
                FeatureKind = string.IsNullOrWhiteSpace(kind) ? "Other" : kind,
                BlocksMovement = blocked,
                AnchorGridCell = new Vector2Int(gx, gz),
                AnchorWorldPosition = sampleWorld,
                ApproxCenterWorldPosition = sampleWorld,
                ObservedCellCount = 0,
                FirstSeenTime = Time.time,
                LastSeenTime = Time.time,
                SeenCount = 0,
                Confidence = Mathf.Clamp01(confidenceIncrement),
                SourceAgentId = (agent != null && agent.Properties != null) ? agent.Properties.AgentID : string.Empty
            };
            detectedCampusFeatures[featureKey] = data;
        }

        data.LastSeenTime = Time.time;
        data.SeenCount += 1;
        data.Confidence = Mathf.Clamp01(Mathf.Max(data.Confidence, confidenceIncrement));
        data.BlocksMovement = blocked;
        if (!string.IsNullOrWhiteSpace(name)) data.FeatureName = name;
        if (!string.IsNullOrWhiteSpace(uid)) data.FeatureUid = uid;
        if (!string.IsNullOrWhiteSpace(kind)) data.FeatureKind = kind;

        Vector2Int gridCell = new Vector2Int(gx, gz);
        if (!ContainsGridCell(data.ObservedSampleCells, gridCell))
        {
            if (data.ObservedSampleCells.Count < Mathf.Max(4, maxObservedFeatureCellsPerFeature))
            {
                data.ObservedSampleCells.Add(gridCell);
            }
            data.ObservedCellCount += 1;
        }
        else
        {
            data.ObservedCellCount = Mathf.Max(data.ObservedCellCount, data.ObservedSampleCells.Count);
        }

        UpdateCampusFeatureAnchors(data);
    }

    /// <summary>
    /// 更新地点锚点与估计中心。
    /// </summary>
    private void UpdateCampusFeatureAnchors(CampusFeaturePerceptionData data)
    {
        if (data == null || data.ObservedSampleCells == null || data.ObservedSampleCells.Count == 0) return;

        Vector3 me = transform.position;
        float bestDist = float.MaxValue;
        Vector2Int bestCell = data.ObservedSampleCells[0];
        Vector3 sum = Vector3.zero;

        for (int i = 0; i < data.ObservedSampleCells.Count; i++)
        {
            Vector2Int c = data.ObservedSampleCells[i];
            Vector3 w = campusGrid.GridToWorldCenter(c.x, c.y, 0f);
            sum += w;

            Vector3 d = w - me;
            d.y = 0f;
            float dist = d.sqrMagnitude;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestCell = c;
            }
        }

        data.AnchorGridCell = bestCell;
        data.AnchorWorldPosition = campusGrid.GridToWorldCenter(bestCell.x, bestCell.y, 0f);
        data.ApproxCenterWorldPosition = sum / data.ObservedSampleCells.Count;
    }

    private static bool ContainsGridCell(List<Vector2Int> cells, Vector2Int cell)
    {
        if (cells == null) return false;
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i] == cell) return true;
        }
        return false;
    }

    /// <summary>
    /// 推断小节点类型。
    /// 扩展新类型时，优先在这里增加规则，避免改动核心感知流程。
    /// </summary>
    private SmallNodeType InferSmallNodeType(GameObject obj, SmallNodeType fallbackType)
    {
        if (obj == null)
        {
            return fallbackType;
        }

        SmallNodeRuntimeInfo runtimeInfo = obj.GetComponent<SmallNodeRuntimeInfo>();
        if (runtimeInfo == null)
        {
            runtimeInfo = obj.GetComponentInParent<SmallNodeRuntimeInfo>();
        }
        if (runtimeInfo != null && runtimeInfo.nodeType != SmallNodeType.Unknown)
        {
            return runtimeInfo.nodeType;
        }

        if (obj.CompareTag("Resource")) return SmallNodeType.ResourcePoint;
        if (obj.CompareTag("Agent")) return SmallNodeType.Agent;

        string n = obj.name.ToLowerInvariant();
        if (obj.CompareTag("Tree") || n.Contains("tree")) return SmallNodeType.Tree;
        if (obj.CompareTag("Pedestrian") || n.Contains("pedestrian") || n.Contains("ped")) return SmallNodeType.Pedestrian;
        if (obj.CompareTag("Vehicle") || n.Contains("vehicle") || n.Contains("car")) return SmallNodeType.Vehicle;

        return fallbackType;
    }

    /// <summary>
    /// 推断小节点是否动态。
    /// 行人/车辆/智能体默认动态；树木默认静态。
    /// </summary>
    private bool InferIsDynamic(GameObject obj, SmallNodeType nodeType)
    {
        if (obj != null)
        {
            SmallNodeRuntimeInfo runtimeInfo = obj.GetComponent<SmallNodeRuntimeInfo>();
            if (runtimeInfo == null)
            {
                runtimeInfo = obj.GetComponentInParent<SmallNodeRuntimeInfo>();
            }
            if (runtimeInfo != null)
            {
                return runtimeInfo.isDynamic;
            }
        }

        switch (nodeType)
        {
            case SmallNodeType.Pedestrian:
            case SmallNodeType.Vehicle:
            case SmallNodeType.Agent:
                return true;
            case SmallNodeType.Tree:
                return false;
            default:
                return obj != null && obj.GetComponent<Rigidbody>() != null;
        }
    }

    /// <summary>
    /// 推断是否阻塞通行。
    /// 按需求：除资源点外，其余节点均视为障碍物。
    /// </summary>
    private bool InferBlocksMovement(SmallNodeType nodeType)
    {
        return nodeType != SmallNodeType.ResourcePoint;
    }

    /// <summary>
    /// 解析命中对象类型（优先分层，再结合运行时语义组件）。
    /// </summary>
    private SmallNodeType ResolveDetectedType(GameObject obj)
    {
        if (obj == null) return SmallNodeType.Unknown;

        int mask = (1 << obj.layer);

        if ((mask & agentLayers) != 0) return SmallNodeType.Agent;
        if ((mask & resourceLayers) != 0) return SmallNodeType.ResourcePoint;

        SmallNodeRuntimeInfo info = obj.GetComponent<SmallNodeRuntimeInfo>();
        if (info == null)
        {
            info = obj.GetComponentInParent<SmallNodeRuntimeInfo>();
        }
        if (info != null && info.nodeType != SmallNodeType.Unknown)
        {
            return info.nodeType;
        }

        if ((mask & obstacleLayers) != 0)
        {
            // 除资源外都按障碍物处理，无法细分时默认临时障碍
            return InferSmallNodeType(obj, SmallNodeType.TemporaryObstacle);
        }

        return InferSmallNodeType(obj, SmallNodeType.Unknown);
    }

    /// <summary>
    /// 构建小节点ID。
    /// 有场景对象时优先用 InstanceID，保证跨智能体共享一致键。
    /// </summary>
    private string BuildSmallNodeId(GameObject obj, SmallNodeType nodeType, Vector3 pos)
    {
        if (obj != null)
        {
            return $"{nodeType}:{obj.GetInstanceID()}";
        }

        int px = Mathf.RoundToInt(pos.x * 10f);
        int py = Mathf.RoundToInt(pos.y * 10f);
        int pz = Mathf.RoundToInt(pos.z * 10f);
        return $"{nodeType}:P({px},{py},{pz})";
    }

    /// <summary>
    /// 无人机额外检测上下方向的射线
    /// </summary>
    private void CheckVerticalDetection(Vector3 origin, float maxRange, int startIndex)
    {
        // 检测下方
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit downHit, maxRange, detectionLayers))
        {
            ProcessValidDetection(downHit, startIndex);
        }

        // 检测上方
        if (Physics.Raycast(origin, Vector3.up, out RaycastHit upHit, maxRange, detectionLayers))
        {
            ProcessValidDetection(upHit, startIndex + 1);
        }
    }

    /// <summary>
    /// 为障碍物和资源创建球体标记（可视化检测点）
    /// </summary>
    private void CreateSphereMarker(Vector3 position, SmallNodeType type)
    {
        // 仅资源点与障碍物显示标记；Agent 不做球体可视化
        if (type == SmallNodeType.Agent) return;

        // 创建球体 primitive
        //GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        GameObject sphere = GetSphere(); // 使用对象池

        sphere.name = $"DetectionMarker_{type}"; // 名称仅用于调试显示
        sphere.transform.position = position; // 位置设为检测点
        Vector2 sizeRange = GetEffectiveSphereSizeRange();
        sphere.transform.localScale = Vector3.one * sizeRange.x; // 初始大小
        sphere.transform.parent = null;  // 根节点，可视化与智能体完全解耦
        sphere.hideFlags = HideFlags.DontSave; // 不在Hierarchy显示

        Collider markerCollider = sphere.GetComponent<Collider>();
        if (markerCollider != null)
        {
            Destroy(markerCollider); // 移除碰撞体（避免影响物理交互）
        }

        // 设置球体材质和颜色
        Renderer renderer = sphere.GetComponent<Renderer>();
        Color baseColor = GetColorByType(type);
        if (renderer != null)
        {
            spherePropertyBlock.Clear();
            spherePropertyBlock.SetColor("_Color", baseColor);
            spherePropertyBlock.SetColor("_BaseColor", baseColor);
            spherePropertyBlock.SetColor("_EmissionColor", baseColor * Mathf.Max(0f, markerEmissionIntensity));
            renderer.SetPropertyBlock(spherePropertyBlock);
        }
        markerMeta[sphere] = new MarkerMeta
        {
            createdTime = Time.time,
            baseColor = baseColor
        };

        sphereMarkers.Add(sphere); // 添加到标记列表
    }

    /// <summary>
    /// 更新球体标记的动画和生命周期
    /// 功能：实现球体闪烁效果，控制透明度随时间衰减，移除过期标记
    /// </summary>
    private void UpdateSphereMarkers()
    {
        float time = Time.time;
        float safeLifetime = Mathf.Max(0.5f, markerLifetime);
        Vector2 sizeRange = GetEffectiveSphereSizeRange();
        // 倒序遍历（避免删除元素时索引异常）
        for (int i = sphereMarkers.Count - 1; i >= 0; i--)
        {
            GameObject sphere = sphereMarkers[i];
            if (sphere == null)
            {
                sphereMarkers.RemoveAt(i);
                continue;
            }

            // 闪烁动画：使用正弦函数计算缩放因子（0.5~1.5倍大小）
            float blinkFactor = Mathf.Sin(time * blinkFrequency * Mathf.PI) * 0.5f + 0.5f;
            float size = Mathf.Lerp(sizeRange.x, sizeRange.y, blinkFactor);
            sphere.transform.localScale = Vector3.one * size;

            // 透明度衰减：随时间增加透明度降低
            Renderer renderer = sphere.GetComponent<Renderer>();
            if (!markerMeta.TryGetValue(sphere, out MarkerMeta meta))
            {
                meta = new MarkerMeta { createdTime = time, baseColor = obstacleColor };
                markerMeta[sphere] = meta;
            }
            Color color = meta.baseColor;
            float lifeTime = time - meta.createdTime;
            float alpha = Mathf.Clamp01(1 - (lifeTime / safeLifetime)); // 超过生命周期后完全透明
            if (lifeTime < safeLifetime)
            {
                alpha = Mathf.Max(Mathf.Clamp01(markerMinAlpha), alpha);
            }
            color.a = alpha;
            if (renderer != null)
            {
                spherePropertyBlock.Clear();
                spherePropertyBlock.SetColor("_Color", color);
                spherePropertyBlock.SetColor("_BaseColor", color);
                spherePropertyBlock.SetColor("_EmissionColor", meta.baseColor * Mathf.Max(0f, markerEmissionIntensity));
                renderer.SetPropertyBlock(spherePropertyBlock);
            }

            // 移除完全透明的球体
            if (alpha <= 0)
            {
                markerMeta.Remove(sphere);
                RecycleSphere(sphere); 
                sphereMarkers.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 清除旧的球体标记（在每次感知更新时调用）
    /// </summary>
    private void CleanupOldSphereMarkers()
    {
        foreach (var sphere in sphereMarkers)
        {
            if (sphere != null) RecycleSphere(sphere);
        }
        sphereMarkers.Clear();
        markerMeta.Clear();
    }

    /// <summary>
    /// 更新智能体状态（将检测结果同步到智能体的当前状态）
    /// </summary>
    private void UpdateAgentState()
    {
        if (agent?.CurrentState == null) return;
    
        var currentState = agent.CurrentState;
        if (currentState.NearbyAgents == null) currentState.NearbyAgents = new Dictionary<string, GameObject>();
        if (currentState.DetectedSmallNodes == null) currentState.DetectedSmallNodes = new List<SmallNodeData>();
        if (currentState.NearbySmallNodes == null) currentState.NearbySmallNodes = new Dictionary<string, SmallNodeData>();
        if (currentState.NearbyCampusFeatures == null) currentState.NearbyCampusFeatures = new Dictionary<string, CampusFeaturePerceptionData>();
        if (currentState.DetectedCampusFeatures == null) currentState.DetectedCampusFeatures = new List<CampusFeaturePerceptionData>();

        // 每轮感知都重建一次附近智能体集合，避免保留陈旧目标
        currentState.NearbyAgents.Clear();

        foreach (var agentObj in nearbyAgents)
        {
            var otherAgent = agentObj.GetComponent<IntelligentAgent>();
            if (otherAgent == null) continue;

            string otherID = otherAgent.Properties.AgentID;

            if (!currentState.NearbyAgents.ContainsKey(otherID))
            {
                currentState.NearbyAgents.Add(otherID, agentObj);
            }
        }

        // 把本轮 detectedObjects 去重后同步到 AgentDynamicState（与 NearbyAgents 同级）
        currentState.DetectedSmallNodes.Clear();
        currentState.NearbySmallNodes.Clear();

        var uniqueNodes = new Dictionary<string, SmallNodeData>();
        for (int i = 0; i < detectedObjects.Count; i++)
        {
            SmallNodeData src = detectedObjects[i];
            if (src == null) continue;

            string nodeId = src.NodeId;
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                nodeId = BuildSmallNodeId(src.SceneObject, src.NodeType, src.WorldPosition);
            }

            SmallNodeData copy = CloneSmallNodeData(src);
            copy.NodeId = nodeId;

            if (uniqueNodes.TryGetValue(nodeId, out SmallNodeData existing))
            {
                existing.LastSeenTime = Mathf.Max(existing.LastSeenTime, copy.LastSeenTime);
                existing.SeenCount += copy.SeenCount;
                existing.Confidence = Mathf.Max(existing.Confidence, copy.Confidence);
                if (existing.SceneObject == null) existing.SceneObject = copy.SceneObject;
                if (string.IsNullOrWhiteSpace(existing.DisplayName)) existing.DisplayName = copy.DisplayName;
            }
            else
            {
                uniqueNodes[nodeId] = copy;
            }
        }

        foreach (var kv in uniqueNodes)
        {
            currentState.NearbySmallNodes[kv.Key] = kv.Value;
            currentState.DetectedSmallNodes.Add(kv.Value);
        }

        // 同步当前轮“已看见地点”（建筑等）
        currentState.NearbyCampusFeatures.Clear();
        currentState.DetectedCampusFeatures.Clear();
        foreach (var kv in detectedCampusFeatures)
        {
            CampusFeaturePerceptionData copy = CloneCampusFeatureData(kv.Value);
            currentState.NearbyCampusFeatures[kv.Key] = copy;
            currentState.DetectedCampusFeatures.Add(copy);
        }
    }

    /// <summary>
    /// 复制小节点数据（避免状态层与感知层共享同一个引用对象）。
    /// </summary>
    private static SmallNodeData CloneSmallNodeData(SmallNodeData src)
    {
        if (src == null) return null;

        return new SmallNodeData
        {
            NodeId = src.NodeId,
            NodeType = src.NodeType,
            WorldPosition = src.WorldPosition,
            IsDynamic = src.IsDynamic,
            BlocksMovement = src.BlocksMovement,
            FirstSeenTime = src.FirstSeenTime,
            LastSeenTime = src.LastSeenTime,
            Confidence = src.Confidence,
            SeenCount = src.SeenCount,
            SourceAgentId = src.SourceAgentId,
            DisplayName = src.DisplayName,
            SceneObject = src.SceneObject
        };
    }

    /// <summary>
    /// 复制地点感知数据，避免上层误改感知层缓存。
    /// </summary>
    private static CampusFeaturePerceptionData CloneCampusFeatureData(CampusFeaturePerceptionData src)
    {
        if (src == null) return null;
        CampusFeaturePerceptionData dst = new CampusFeaturePerceptionData
        {
            FeatureUid = src.FeatureUid,
            FeatureName = src.FeatureName,
            FeatureKind = src.FeatureKind,
            BlocksMovement = src.BlocksMovement,
            AnchorGridCell = src.AnchorGridCell,
            AnchorWorldPosition = src.AnchorWorldPosition,
            ApproxCenterWorldPosition = src.ApproxCenterWorldPosition,
            ObservedCellCount = src.ObservedCellCount,
            FirstSeenTime = src.FirstSeenTime,
            LastSeenTime = src.LastSeenTime,
            SeenCount = src.SeenCount,
            Confidence = src.Confidence,
            SourceAgentId = src.SourceAgentId
        };

        if (src.ObservedSampleCells != null)
        {
            dst.ObservedSampleCells = new List<Vector2Int>(src.ObservedSampleCells);
        }

        return dst;
    }

    /// <summary>
    /// 绘制实时检测射线（仅障碍物和资源）
    /// 功能：在Scene视图通过Debug.DrawLine绘制射线，受射线简化因子控制
    /// </summary>
    private void DrawRealTimeDetectionRays()
    {
        if (agent == null || !showDetectionRays) return;

        // 射线起点：无人机用自身位置，无人车用车身上方0.5米
        Vector3 origin = agentType == AgentType.Quadcopter ? 
            transform.position : transform.position + Vector3.up * 0.5f;

        foreach (var pointInfo in detectionPoints)
        {
            // 仅显示符合简化条件的射线（每隔raySimplificationFactor条显示一条）
            if (pointInfo.rayIndex % Mathf.Max(1, raySimplificationFactor) != 0)
                continue;

            // 只显示2秒内的检测射线（避免显示过期数据）
            if (Time.time - pointInfo.timestamp < Mathf.Max(0.2f, rayLifetime))
            {
                Color rayColor = GetColorByType(pointInfo.type);
                rayColor.a = Mathf.Clamp01(rayAlpha); // 高可见度透明度
                Debug.DrawLine(origin, pointInfo.position, rayColor, updateInterval);
            }
        }
    }

    /// <summary>
    /// 根据类型获取可视化颜色（仅用于障碍物和资源）
    /// </summary>
    private Color GetColorByType(SmallNodeType type)
    {
        // 按需求：除资源点外都视为障碍物
        Color baseColor = (type == SmallNodeType.ResourcePoint) ? resourceColor : obstacleColor;
        return enhanceVisualization ? ToHighContrast(baseColor) : baseColor;
    }

    /// <summary>
    /// 获取用于可视化的锚点位置（默认在物体顶部上方，减少被模型遮挡）。
    /// </summary>
    private Vector3 GetVisualAnchorPosition(Vector3 worldPosition, GameObject obj)
    {
        float lift = Mathf.Max(0f, markerHeightOffset);
        if (obj != null)
        {
            Renderer r = obj.GetComponentInChildren<Renderer>();
            if (r != null)
            {
                lift += Mathf.Clamp(r.bounds.extents.y * 0.6f, 0.05f, 1.5f);
            }
        }
        return worldPosition + Vector3.up * lift;
    }

    /// <summary>
    /// 高可见度下动态计算线宽，避免线条过细。
    /// </summary>
    private float GetEffectiveLineWidth()
    {
        float baseWidth = Mathf.Max(0.01f, lineWidth);
        if (!enhanceVisualization) return baseWidth;
        return Mathf.Max(0.06f, baseWidth * Mathf.Max(1f, lineWidthBoost));
    }

    /// <summary>
    /// 高可见度下动态计算标记尺寸范围，避免球体过小。
    /// </summary>
    private Vector2 GetEffectiveSphereSizeRange()
    {
        Vector2 safe = sphereSizeRange;
        if (safe.y < safe.x) safe.y = safe.x;
        if (!enhanceVisualization)
        {
            safe.x = Mathf.Max(0.03f, safe.x);
            safe.y = Mathf.Max(safe.x, safe.y);
            return safe;
        }

        float boost = Mathf.Max(1f, markerSizeBoost);
        safe.x = Mathf.Max(0.12f, safe.x * boost);
        safe.y = Mathf.Max(safe.x, safe.y * boost);
        return safe;
    }

    /// <summary>
    /// 把颜色提升到高饱和高亮度，增强复杂场景下辨识度。
    /// </summary>
    private static Color ToHighContrast(Color c)
    {
        Color.RGBToHSV(c, out float h, out float s, out float v);
        s = Mathf.Max(0.72f, s);
        v = Mathf.Max(0.95f, v);
        Color o = Color.HSVToRGB(h, s, v);
        o.a = c.a;
        return o;
    }

    /// <summary>
    /// 获取感知范围可视化颜色（高可见度模式会提升亮度与透明度）。
    /// </summary>
    private Color GetRangeVizColor(bool fill)
    {
        Color c = fill ? rangeFillColor : rangeColor;
        if (!enhanceVisualization) return c;
        c = ToHighContrast(c);
        c.a = fill ? Mathf.Max(0.20f, c.a) : Mathf.Max(0.90f, c.a);
        return c;
    }

    /// <summary>
    /// 每帧同步线渲染样式，便于运行时调参数立即生效。
    /// </summary>
    private void ApplyLineRendererStyle()
    {
        float width = GetEffectiveLineWidth();
        if (rangeLineRenderer != null)
        {
            rangeLineRenderer.widthMultiplier = width;
        }
        if (detectionLineRenderer != null)
        {
            detectionLineRenderer.widthMultiplier = width * 0.75f;
        }
    }

    /// <summary>
    /// Gizmos绘制（在Scene视图显示感知范围和检测点）
    /// </summary>
    private void OnDrawGizmos()
    {
        if (!showGizmos || agent == null) return;

        Vector3 origin = transform.position;
        float range = agent.Properties.PerceptionRange;

        // 绘制感知范围
        if (agentType == AgentType.Quadcopter)
        {
            // 无人机：球形范围（填充+线框）
            Gizmos.color = GetRangeVizColor(true);
            Gizmos.DrawSphere(origin, range);
            
            Gizmos.color = GetRangeVizColor(false);
            Gizmos.DrawWireSphere(origin, range);
        }
        else
        {
            // 无人车：扇形范围（射线+弧线）
            float halfAngle = groundHorizontalAngle / 2;
            Vector3 leftDir = Quaternion.Euler(0, -halfAngle, 0) * transform.forward; // 左边界方向
            Vector3 rightDir = Quaternion.Euler(0, halfAngle, 0) * transform.forward; // 右边界方向

            Gizmos.color = GetRangeVizColor(false);
            Gizmos.DrawRay(origin, leftDir * range); // 左边界射线
            Gizmos.DrawRay(origin, rightDir * range); // 右边界射线
            DrawArc(origin, transform.forward, halfAngle, range); // 弧形边界
            
            Gizmos.color = GetRangeVizColor(true);
            DrawSector(origin, transform.forward, halfAngle, range); // 扇形填充
        }

        // 绘制检测点球体（障碍物和资源）
        foreach (var pointInfo in detectionPoints)
        {
            float timeSinceDetection = Time.time - pointInfo.timestamp;
            float alpha = Mathf.Clamp01(1.0f - timeSinceDetection / Mathf.Max(0.5f, markerLifetime)); // 生命周期内逐渐消失
            alpha = Mathf.Max(Mathf.Clamp01(markerMinAlpha), alpha);
            
            if (alpha > 0.1f) // 透明度大于0.1才显示
            {
                Color pointColor = GetColorByType(pointInfo.type);
                pointColor.a = alpha;
                
                // 闪烁效果：通过正弦函数调整亮度
                float blink = Mathf.Sin(Time.time * blinkFrequency * Mathf.PI) * 0.3f + 0.7f;
                Gizmos.color = new Color(pointColor.r * blink, pointColor.g * blink, pointColor.b * blink, alpha);
                // 球体大小随闪烁和透明度变化
                Vector2 sizeRange = GetEffectiveSphereSizeRange();
                Gizmos.DrawSphere(pointInfo.position, Mathf.Lerp(sizeRange.x, sizeRange.y, blink) * alpha);
            }
        }
    }

    /// <summary>
    /// 在Game视图绘制可视化元素（感知范围和检测射线）
    /// </summary>
    private void DrawGameViewVisualization()
    {
        if (!showGameViewVisualization || agent == null)
        {
            if (rangeLineRenderer != null) rangeLineRenderer.positionCount = 0;
            if (detectionLineRenderer != null) detectionLineRenderer.positionCount = 0;
            return;
        }

        ApplyLineRendererStyle();
        
        Vector3 origin = transform.position;
        float range = agent.Properties.PerceptionRange;
        
        // 绘制感知范围
        if (agentType == AgentType.Quadcopter)
        {
            DrawCircleWithLineRenderer(origin, range, GetRangeVizColor(false), 32); // 无人机：圆形范围
        }
        else
        {
            // 无人车：扇形范围
            DrawSectorWithLineRenderer(origin, transform.forward, groundHorizontalAngle, range, GetRangeVizColor(false), 32);
        }
        
        // 绘制检测射线（仅障碍物和资源，受简化因子控制）
        int lineIndex = 0;
        detectionLineRenderer.positionCount = 0; // 重置线渲染器
        foreach (var pointInfo in detectionPoints)
        {
            if (pointInfo.rayIndex % Mathf.Max(1, raySimplificationFactor) != 0)
                continue; // 跳过不符合简化条件的射线

            // 只显示2秒内的射线
            if (Time.time - pointInfo.timestamp < Mathf.Max(0.2f, rayLifetime))
            {
                Color pointColor = GetColorByType(pointInfo.type);
                pointColor.a = Mathf.Clamp01(rayAlpha);
                AddLineToRenderer(detectionLineRenderer, origin, pointInfo.position, pointColor, ref lineIndex);
            }
        }
        detectionLineRenderer.positionCount = lineIndex; // 更新线渲染器顶点数量
    }

    /// <summary>
    /// 用线渲染器绘制单条线
    /// </summary>
    private void DrawLineWithRenderer(LineRenderer renderer, Vector3 start, Vector3 end, Color color)
    {
        renderer.positionCount = 2;
        renderer.SetPosition(0, start);
        renderer.SetPosition(1, end);
        renderer.startColor = color;
        renderer.endColor = color;
    }

    /// <summary>
    /// 用线渲染器绘制圆形（无人机感知范围）
    /// </summary>
    private void DrawCircleWithLineRenderer(Vector3 center, float radius, Color color, int segments = 32)
    {
        rangeLineRenderer.positionCount = segments + 1; // 线段数量（闭合图形）
        rangeLineRenderer.startColor = color;
        rangeLineRenderer.endColor = color;
        
        // 计算圆周上的点
        for (int i = 0; i <= segments; i++)
        {
            float angle = i * 2 * Mathf.PI / segments; // 角度（弧度）
            Vector3 pos = center + new Vector3(
                Mathf.Sin(angle) * radius,
                0,
                Mathf.Cos(angle) * radius
            );
            rangeLineRenderer.SetPosition(i, pos);
        }
    }

    /// <summary>
    /// 用线渲染器绘制扇形（无人车感知范围）
    /// </summary>
    private void DrawSectorWithLineRenderer(Vector3 center, Vector3 forward, float angle, float radius, Color color, int segments = 32)
    {
        float halfAngle = angle / 2;
        rangeLineRenderer.positionCount = segments + 2; // 包含起点和扇形边缘点
        rangeLineRenderer.startColor = color;
        rangeLineRenderer.endColor = color;
        
        rangeLineRenderer.SetPosition(0, center); // 第一个点是扇形顶点（智能体位置）
        
        // 计算扇形边缘上的点
        for (int i = 0; i <= segments; i++)
        {
            float currentAngle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / segments);
            Vector3 dir = Quaternion.Euler(0, currentAngle, 0) * forward; // 旋转方向向量
            Vector3 point = center + dir * radius; // 边缘点位置
            rangeLineRenderer.SetPosition(i + 1, point);
        }
    }

    /// <summary>
    /// 向线渲染器添加一条线（用于批量绘制多条射线）
    /// </summary>
    private void AddLineToRenderer(LineRenderer renderer, Vector3 start, Vector3 end, Color color, ref int index)
    {
        renderer.startColor = color;
        renderer.endColor = color;
        renderer.positionCount += 2; // 每条线需要2个点
        renderer.SetPosition(index++, start); // 起点
        renderer.SetPosition(index++, end);   // 终点
    }

    /// <summary>
    /// 在Scene视图绘制弧形（无人车扇形范围的边缘）
    /// </summary>
    private void DrawArc(Vector3 center, Vector3 forward, float halfAngle, float radius)
    {
        int segments = 20; // 弧线分段数
        Vector3 prevPoint = center + forward * radius; // 初始点（正前方）
        
        // 从左到右绘制弧线
        for (int i = 1; i <= segments; i++)
        {
            float angle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / segments);
            Vector3 dir = Quaternion.Euler(0, angle, 0) * forward;
            Vector3 point = center + dir * radius;
            Gizmos.DrawLine(prevPoint, point); // 绘制相邻两点的线段
            prevPoint = point;
        }
    }

    /// <summary>
    /// 在Scene视图绘制扇形填充（使用Handles工具）
    /// </summary>
    private void DrawSector(Vector3 center, Vector3 forward, float halfAngle, float radius)
    {
        int segments = 20; // 扇形分段数
        
        #if UNITY_EDITOR // 仅在编辑器模式下执行（Handles是编辑器类）
        UnityEditor.Handles.BeginGUI();
        UnityEditor.Handles.color = rangeFillColor;
        
        // 构建扇形顶点数组（包含中心和边缘点）
        Vector3[] vertices = new Vector3[segments + 2];
        vertices[0] = center;
        
        for (int i = 0; i <= segments; i++)
        {
            float angle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / segments);
            Vector3 dir = Quaternion.Euler(0, angle, 0) * forward;
            vertices[i + 1] = center + dir * radius;
        }
        
        UnityEditor.Handles.DrawAAConvexPolygon(vertices); // 绘制抗锯齿凸多边形
        UnityEditor.Handles.EndGUI();
        #endif
    }
    /// <summary>
    /// 获取附近的智能体数组（公开接口）
    /// </summary>
    public GameObject[] GetNearbyAgents() => nearbyAgents.ToArray();

    /// <summary>
    /// 公开接口：查询当前智能体附近已发现的小节点。
    /// 说明：返回的是“共享注册表”中的结果，不会主动触发新的感知射线。
    /// </summary>
    public List<SmallNodeData> QueryKnownSmallNodes(float radius, bool includeStatic = true, bool includeDynamic = true)
    {
        float useRadius = Mathf.Max(0.5f, radius);
        return SmallNodeRegistry.QueryNodes(transform.position, useRadius, includeStatic, includeDynamic);
    }

    /// <summary>
    /// 公开接口：生成小节点感知快照。
    /// 用于上层决策模块统一读取静态/动态/资源类节点信息。
    /// </summary>
    public SmallNodePerceptionSnapshot GetSmallNodeSnapshot(float radius = -1f)
    {
        float useRadius = radius > 0 ? radius : smallNodeQueryRadius;
        var nodes = SmallNodeRegistry.QueryNodes(transform.position, useRadius, true, true);

        string agentId = (agent != null && agent.Properties != null) ? agent.Properties.AgentID : string.Empty;
        SmallNodePerceptionSnapshot snapshot = new SmallNodePerceptionSnapshot
        {
            AgentId = agentId,
            QueryTime = Time.time,
            AgentPosition = transform.position,
            QueryRadius = useRadius
        };

        for (int i = 0; i < nodes.Count; i++)
        {
            SmallNodeData n = nodes[i];
            if (n.NodeType == SmallNodeType.ResourcePoint)
            {
                snapshot.NearbyResourceNodes.Add(n);
            }

            if (n.IsDynamic) snapshot.NearbyDynamicNodes.Add(n);
            else snapshot.NearbyStaticNodes.Add(n);
        }

        return snapshot;
    }

    /// <summary>
    /// 公开接口：按 ID 查询已发现小节点。
    /// </summary>
    public bool TryGetKnownSmallNode(string nodeId, out SmallNodeData node)
    {
        return SmallNodeRegistry.TryGetNode(nodeId, out node);
    }

    /// <summary>
    /// 公开接口：清空共享小节点库。
    /// 场景重置或新任务开始时可主动调用。
    /// </summary>
    public static void ClearSharedSmallNodes()
    {
        SmallNodeRegistry.ClearAll();
    }


    /// <summary>
    /// 清除检测历史（可视化元素和数据）
    /// </summary>
    public void ClearDetectionHistory()
    {
        detectionPoints.Clear();
        foreach (var sphere in sphereMarkers)
        {
            if (sphere != null) RecycleSphere(sphere);
        }
        sphereMarkers.Clear();
        markerMeta.Clear();
    }

    /// <summary>
    /// 销毁时清理资源（避免内存泄漏）
    /// </summary>
    private void OnDestroy()
    {
        if (lineMaterial != null) Destroy(lineMaterial);
        if (sphereMaterial != null) Destroy(sphereMaterial);

        foreach (var sphere in sphereMarkers)
        {
            if (sphere != null) Destroy(sphere);
        }
        sphereMarkers.Clear();
        markerMeta.Clear();

        while (spherePool.Count > 0)
        {
            GameObject pooled = spherePool.Dequeue();
            if (pooled != null) Destroy(pooled);
        }

        if (rangeLineRenderer != null) Destroy(rangeLineRenderer.gameObject);
        if (detectionLineRenderer != null) Destroy(detectionLineRenderer.gameObject);
    }
}

/// <summary>
/// 全局共享小节点注册表（静态）：
/// 1) 默认空，不预扫描场景；只有感知命中才写入。
/// 2) 静态节点只保存一份（避免每个智能体重复缓存）。
/// 3) 动态节点支持 TTL 清理。
/// </summary>
public static class SmallNodeRegistry
{
    private static readonly Dictionary<string, SmallNodeData> nodes = new Dictionary<string, SmallNodeData>();
    private static readonly Dictionary<string, Vector2Int> nodeToBucket = new Dictionary<string, Vector2Int>();
    private static readonly Dictionary<Vector2Int, HashSet<string>> bucketToNodes = new Dictionary<Vector2Int, HashSet<string>>();

    // 空间分桶大小：用于降低范围查询开销。
    private const float BucketSize = 8f;

    /// <summary>
    /// 获取当前共享注册表节点数量。
    /// </summary>
    public static int Count => nodes.Count;

    /// <summary>
    /// 注册或更新一个小节点。
    /// </summary>
    public static void RegisterOrUpdate(SmallNodeData data, float confidenceIncrement)
    {
        if (data == null || string.IsNullOrWhiteSpace(data.NodeId)) return;

        if (nodes.TryGetValue(data.NodeId, out SmallNodeData existing))
        {
            existing.LastSeenTime = Mathf.Max(existing.LastSeenTime, data.LastSeenTime);
            existing.SeenCount += 1;
            existing.Confidence = Mathf.Clamp01(existing.Confidence + Mathf.Abs(confidenceIncrement));
            existing.SourceAgentId = string.IsNullOrWhiteSpace(data.SourceAgentId) ? existing.SourceAgentId : data.SourceAgentId;
            existing.DisplayName = string.IsNullOrWhiteSpace(data.DisplayName) ? existing.DisplayName : data.DisplayName;

            // 动态节点和缺失对象位置允许更新；静态节点默认保持首次位置，减少抖动。
            if (existing.IsDynamic || existing.SceneObject == null)
            {
                existing.WorldPosition = data.WorldPosition;
            }

            if (existing.SceneObject == null && data.SceneObject != null)
            {
                existing.SceneObject = data.SceneObject;
            }

            MoveNodeBucketIfNeeded(existing.NodeId, existing.WorldPosition);
            return;
        }

        SmallNodeData copy = Clone(data);
        nodes[copy.NodeId] = copy;
        AddNodeToBucket(copy.NodeId, copy.WorldPosition);
    }

    /// <summary>
    /// 按 ID 查询节点。
    /// </summary>
    public static bool TryGetNode(string nodeId, out SmallNodeData data)
    {
        if (nodes.TryGetValue(nodeId, out SmallNodeData found))
        {
            data = Clone(found);
            return true;
        }

        data = null;
        return false;
    }

    /// <summary>
    /// 按精确标识解析单个小节点。
    /// 这里只接受三类稳定身份：
    /// 1) NodeId；
    /// 2) DisplayName 精确匹配；
    /// 3) SceneObject.name 精确匹配。
    /// 不在这里做“树/车辆/最近行人”这类语义猜测。
    /// </summary>
    public static bool TryResolveExact(string token, out SmallNodeData data)
    {
        data = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        string q = token.Trim();
        if (TryGetNode(q, out data)) return true;

        foreach (KeyValuePair<string, SmallNodeData> kv in nodes)
        {
            SmallNodeData n = kv.Value;
            if (n == null) continue;

            if (!string.IsNullOrWhiteSpace(n.DisplayName) &&
                string.Equals(n.DisplayName.Trim(), q, StringComparison.OrdinalIgnoreCase))
            {
                data = Clone(n);
                return true;
            }

            if (n.SceneObject != null &&
                !string.IsNullOrWhiteSpace(n.SceneObject.name) &&
                string.Equals(n.SceneObject.name.Trim(), q, StringComparison.OrdinalIgnoreCase))
            {
                data = Clone(n);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 按多个稳定标识解析小节点集合。
    /// 典型输入来源是 StructuredTargetReference.memberEntityIds。
    /// </summary>
    public static List<SmallNodeData> QueryNodesByIds(IEnumerable<string> ids)
    {
        List<SmallNodeData> result = new List<SmallNodeData>();
        if (ids == null) return result;

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            string trimmed = id.Trim();
            if (!seen.Add(trimmed)) continue;

            if (TryResolveExact(trimmed, out SmallNodeData node) && node != null)
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>
    /// 按 DisplayName 精确匹配查询小节点集合。
    /// 这给用户显式指定“同名节点群”留了接口，但仍不做模糊语义猜测。
    /// </summary>
    public static List<SmallNodeData> QueryNodesByDisplayName(string displayName, bool includeStatic = true, bool includeDynamic = true)
    {
        List<SmallNodeData> result = new List<SmallNodeData>();
        if (string.IsNullOrWhiteSpace(displayName)) return result;

        string q = displayName.Trim();
        foreach (KeyValuePair<string, SmallNodeData> kv in nodes)
        {
            SmallNodeData n = kv.Value;
            if (n == null) continue;
            if (n.IsDynamic && !includeDynamic) continue;
            if (!n.IsDynamic && !includeStatic) continue;
            if (string.IsNullOrWhiteSpace(n.DisplayName)) continue;

            if (string.Equals(n.DisplayName.Trim(), q, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Clone(n));
            }
        }

        return result;
    }

    /// <summary>
    /// 查询半径范围内节点。
    /// </summary>
    public static List<SmallNodeData> QueryNodes(Vector3 center, float radius, bool includeStatic, bool includeDynamic)
    {
        var result = new List<SmallNodeData>();
        if (nodes.Count == 0 || radius <= 0f) return result;

        float r2 = radius * radius;
        int bxMin = Mathf.FloorToInt((center.x - radius) / BucketSize);
        int bxMax = Mathf.FloorToInt((center.x + radius) / BucketSize);
        int bzMin = Mathf.FloorToInt((center.z - radius) / BucketSize);
        int bzMax = Mathf.FloorToInt((center.z + radius) / BucketSize);

        var visited = new HashSet<string>();

        for (int bx = bxMin; bx <= bxMax; bx++)
        {
            for (int bz = bzMin; bz <= bzMax; bz++)
            {
                Vector2Int bucket = new Vector2Int(bx, bz);
                if (!bucketToNodes.TryGetValue(bucket, out HashSet<string> ids)) continue;

                foreach (string id in ids)
                {
                    if (!visited.Add(id)) continue;
                    if (!nodes.TryGetValue(id, out SmallNodeData n)) continue;

                    if (n.IsDynamic && !includeDynamic) continue;
                    if (!n.IsDynamic && !includeStatic) continue;

                    Vector3 d = n.WorldPosition - center;
                    d.y = 0f;
                    if (d.sqrMagnitude <= r2)
                    {
                        result.Add(Clone(n));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 清理过期动态节点。
    /// </summary>
    public static void CleanupExpiredDynamic(float now, float dynamicTtl)
    {
        if (dynamicTtl <= 0f || nodes.Count == 0) return;

        var removeIds = new List<string>();
        foreach (KeyValuePair<string, SmallNodeData> kv in nodes)
        {
            SmallNodeData n = kv.Value;
            if (!n.IsDynamic) continue;

            bool missingRef = n.SceneObject == null;
            bool expired = (now - n.LastSeenTime) > dynamicTtl;
            if (missingRef || expired)
            {
                removeIds.Add(kv.Key);
            }
        }

        for (int i = 0; i < removeIds.Count; i++)
        {
            RemoveNode(removeIds[i]);
        }
    }

    /// <summary>
    /// 清空注册表（任务重置时调用）。
    /// </summary>
    public static void ClearAll()
    {
        nodes.Clear();
        nodeToBucket.Clear();
        bucketToNodes.Clear();
    }

    private static void RemoveNode(string nodeId)
    {
        nodes.Remove(nodeId);

        if (nodeToBucket.TryGetValue(nodeId, out Vector2Int bucket))
        {
            nodeToBucket.Remove(nodeId);
            if (bucketToNodes.TryGetValue(bucket, out HashSet<string> ids))
            {
                ids.Remove(nodeId);
                if (ids.Count == 0) bucketToNodes.Remove(bucket);
            }
        }
    }

    private static void AddNodeToBucket(string nodeId, Vector3 pos)
    {
        Vector2Int bucket = ToBucket(pos);
        nodeToBucket[nodeId] = bucket;

        if (!bucketToNodes.TryGetValue(bucket, out HashSet<string> ids))
        {
            ids = new HashSet<string>();
            bucketToNodes[bucket] = ids;
        }

        ids.Add(nodeId);
    }

    private static void MoveNodeBucketIfNeeded(string nodeId, Vector3 pos)
    {
        Vector2Int newBucket = ToBucket(pos);
        if (!nodeToBucket.TryGetValue(nodeId, out Vector2Int oldBucket))
        {
            AddNodeToBucket(nodeId, pos);
            return;
        }

        if (newBucket == oldBucket) return;

        if (bucketToNodes.TryGetValue(oldBucket, out HashSet<string> oldIds))
        {
            oldIds.Remove(nodeId);
            if (oldIds.Count == 0) bucketToNodes.Remove(oldBucket);
        }

        nodeToBucket[nodeId] = newBucket;
        if (!bucketToNodes.TryGetValue(newBucket, out HashSet<string> newIds))
        {
            newIds = new HashSet<string>();
            bucketToNodes[newBucket] = newIds;
        }
        newIds.Add(nodeId);
    }

    private static Vector2Int ToBucket(Vector3 pos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(pos.x / BucketSize),
            Mathf.FloorToInt(pos.z / BucketSize)
        );
    }

    private static SmallNodeData Clone(SmallNodeData src)
    {
        return new SmallNodeData
        {
            NodeId = src.NodeId,
            NodeType = src.NodeType,
            WorldPosition = src.WorldPosition,
            IsDynamic = src.IsDynamic,
            BlocksMovement = src.BlocksMovement,
            FirstSeenTime = src.FirstSeenTime,
            LastSeenTime = src.LastSeenTime,
            Confidence = src.Confidence,
            SeenCount = src.SeenCount,
            SourceAgentId = src.SourceAgentId,
            DisplayName = src.DisplayName,
            SceneObject = src.SceneObject
        };
    }
}
