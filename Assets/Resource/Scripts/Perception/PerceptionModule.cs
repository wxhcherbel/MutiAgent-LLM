using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 智能体感知模块。
/// 责任边界：
/// 1. 执行场景采样，产出本轮感知结果。
/// 2. 将结果同步到 IntelligentAgent.CurrentState。
/// 3. 将小节点写入共享注册表，并把关键事件上报给 ActionDecisionModule。
/// 4. 不负责任何可视化逻辑，显示层应由 PerceptionVisualizer 等脚本主动读取结果。
/// </summary>
public class PerceptionModule : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Inspector 配置：基础感知参数
    // ------------------------------------------------------------------

    [Header("基础感知配置")]
    [Tooltip("自动执行感知的时间间隔，单位：秒。")]
    public float updateInterval = 0.5f;

    [Header("分层检测配置")]
    [Tooltip("最终用于射线检测的层。会自动并入 obstacle/resource/agent 三类层。")]
    public LayerMask detectionLayers;
    [Tooltip("障碍物层，默认会自动按层名查找。")]
    public LayerMask obstacleLayers;
    [Tooltip("资源点层，默认会自动按层名查找。")]
    public LayerMask resourceLayers;
    [Tooltip("智能体层，默认会自动按层名查找。")]
    public LayerMask agentLayers;
    [Tooltip("是否在启动时根据层名自动补齐 LayerMask。")]
    public bool autoConfigureLayerMasks = true;
    [Tooltip("自动配置障碍物层时使用的层名。")]
    public string obstacleLayerName = "Obstacle";
    [Tooltip("自动配置资源层时使用的层名。")]
    public string resourceLayerName = "Resource";
    [Tooltip("自动配置智能体层时使用的层名。")]
    public string agentLayerName = "Agent";
    [Tooltip("地面层命中的对象会被忽略，避免把地表当作障碍。")]
    public LayerMask groundLayer;

    [Header("无人机配置")]
    [Tooltip("无人机水平和俯仰扫描角步长，值越小采样越密。")]
    public float droneScanAngleStep = 15f;

    [Header("地面车辆配置")]
    [Tooltip("地面车辆水平视场角，默认 270 度。")]
    public float groundHorizontalAngle = 270f;
    [Tooltip("地面车辆可接受的目标高度差阈值。")]
    public float groundHeightRange = 2f;

    [Header("小节点共享感知库")]
    [Tooltip("是否启用全局共享的小节点注册表。")]
    public bool enableSharedSmallNodeRegistry = true;
    [Tooltip("是否在第一个启用该模块的智能体启动时清空共享注册表。")]
    public bool clearSharedRegistryOnStart = true;
    [Min(1f)]
    [Tooltip("动态节点超时时间，超时后会从共享注册表移除。")]
    public float dynamicNodeTtl = 10f;
    [Tooltip("每次感知后是否清理超时的动态节点。")]
    public bool cleanupExpiredDynamicNodes = true;
    [Tooltip("是否打印共享注册表写入日志。")]
    public bool logSmallNodeRegistry = false;

    // ------------------------------------------------------------------
    // 对外可读结果：这些字段会被其它脚本读取
    // ------------------------------------------------------------------

    [Header("运行时感知结果")]
    [Tooltip("本轮识别到的小节点结果，供可视化层、监控层或其它业务读取。")]
    public List<SmallNodeData> detectedObjects = new List<SmallNodeData>();
    [Tooltip("本轮识别到的附近智能体对象。")]
    public List<GameObject> nearbyAgents = new List<GameObject>();
    [Tooltip("本轮识别到的敌方智能体。")]
    public List<IntelligentAgent> enemyAgents = new List<IntelligentAgent>();

    // ------------------------------------------------------------------
    // 内部缓存状态
    // ------------------------------------------------------------------

    private float lastPerceptionTime;
    private IntelligentAgent ownerAgent;
    private ActionDecisionModule actionDecisionModule;
    private AgentType ownerAgentType;

    private static bool sharedRegistryInitialized;
    private readonly HashSet<int> sensedObjectIdsThisTick = new HashSet<int>();

    /// <summary>
    /// 对外只读接口。
    /// 调用方：PerceptionVisualizer、监控脚本等。
    /// 含义：最近一次完成整轮感知的时间戳。
    /// </summary>
    public float LastPerceptionTime => lastPerceptionTime;

    // ------------------------------------------------------------------
    // Unity 生命周期：外部自动入口，优先级最高
    // ------------------------------------------------------------------

    /// <summary>
    /// Unity 启动入口。
    /// 调用链：Unity -> Start -> InitializeSharedRegistry / CacheDependencies / InitializeDetectionLayers
    /// </summary>
    private void Start()
    {
        InitializeSharedRegistry();
        if (!CacheDependencies())
        {
            return;
        }

        InitializeDetectionLayers();

        Debug.Log(
            $"[PerceptionModule] 初始化完成 agent={ownerAgent.Properties.AgentID}, " +
            $"type={ownerAgentType}, interval={updateInterval:F2}, range={ownerAgent.Properties.PerceptionRange:F1}");
    }

    /// <summary>
    /// Unity 帧更新入口。
    /// 调用链：Unity -> Update -> SenseOnce
    /// 说明：这里只负责调度，不直接写业务细节。
    /// </summary>
    private void Update()
    {
        if (Time.time - lastPerceptionTime < updateInterval)
        {
            return;
        }

        SenseOnce();
    }

    // ------------------------------------------------------------------
    // 对外接口：外部脚本调用时优先看这里
    // ------------------------------------------------------------------

    /// <summary>
    /// 对外手动触发一次完整感知。
    /// 调用方：
    /// 1. 本脚本的 Update 自动调度。
    /// 2. 其它脚本如果需要立即刷新感知，也可以直接调用。
    /// 内部调用：
    /// ExecutePerceptionCycle -> CleanupExpiredDynamicNodesIfNeeded -> 更新时间戳
    /// </summary>
    public void SenseOnce()
    {
        ExecutePerceptionCycle();
        CleanupExpiredDynamicNodesIfNeeded();
        lastPerceptionTime = Time.time;
    }

    // ------------------------------------------------------------------
    // 初始化流程：仅在启动时执行
    // ------------------------------------------------------------------

    /// <summary>
    /// 初始化共享小节点注册表。
    /// 说明：整个进程生命周期内只允许首个感知模块按配置清空一次。
    /// </summary>
    private void InitializeSharedRegistry()
    {
        if (!enableSharedSmallNodeRegistry || !clearSharedRegistryOnStart || sharedRegistryInitialized)
        {
            return;
        }

        SmallNodeRegistry.ClearAll();
        sharedRegistryInitialized = true;
        Debug.Log("[PerceptionModule] 已清空共享小节点注册表。");
    }

    /// <summary>
    /// 缓存本脚本依赖的核心组件。
    /// 依赖：
    /// 1. IntelligentAgent：提供 AgentType、PerceptionRange、CurrentState 等基础数据。
    /// 2. ActionDecisionModule：接收感知事件，例如敌方发现和临时障碍上报。
    /// </summary>
    private bool CacheDependencies()
    {
        ownerAgent = GetComponent<IntelligentAgent>();
        if (ownerAgent == null)
        {
            Debug.LogError("[PerceptionModule] 未找到 IntelligentAgent 组件。");
            return false;
        }

        if (ownerAgent.Properties == null)
        {
            Debug.LogError("[PerceptionModule] IntelligentAgent.Properties 为空，无法建立感知配置。");
            return false;
        }

        ownerAgentType = ownerAgent.Properties.Type;
        actionDecisionModule = GetComponent<ActionDecisionModule>();
        return true;
    }

    /// <summary>
    /// 初始化检测层。
    /// 内部辅助：
    /// 1. TryAssignLayerMaskByName：按层名补齐 LayerMask。
    /// 2. detectionLayers 最终会合并 obstacle/resource/agent 三类层。
    /// </summary>
    private void InitializeDetectionLayers()
    {
        if (autoConfigureLayerMasks)
        {
            TryAssignLayerMaskByName(ref obstacleLayers, obstacleLayerName);
            TryAssignLayerMaskByName(ref resourceLayers, resourceLayerName);
            TryAssignLayerMaskByName(ref agentLayers, agentLayerName);
        }

        detectionLayers |= obstacleLayers;
        detectionLayers |= resourceLayers;
        detectionLayers |= agentLayers;

        if (detectionLayers == 0)
        {
            detectionLayers = Physics.DefaultRaycastLayers;
            Debug.LogWarning("[PerceptionModule] detectionLayers 为空，已回退到 Physics.DefaultRaycastLayers。");
        }

        if (groundLayer == 0)
        {
            TryAssignLayerMaskByName(ref groundLayer, "Ground");
        }
    }

    // ------------------------------------------------------------------
    // 主感知流程：按业务优先级从上往下阅读
    // ------------------------------------------------------------------

    /// <summary>
    /// 执行一轮完整感知。
    /// 调用链：
    /// SenseOnce -> ExecutePerceptionCycle -> ClearPerceptionResults -> ScanByAgentType -> SyncDetectedResultsToAgentState
    /// </summary>
    private void ExecutePerceptionCycle()
    {
        if (ownerAgent == null || ownerAgent.Properties == null)
        {
            return;
        }

        ClearPerceptionResults();
        ScanByAgentType();
        SyncDetectedResultsToAgentState();
    }

    /// <summary>
    /// 清空本轮缓存，保证每一轮感知结果独立。
    /// </summary>
    private void ClearPerceptionResults()
    {
        detectedObjects.Clear();
        nearbyAgents.Clear();
        enemyAgents.Clear();
        sensedObjectIdsThisTick.Clear();
    }

    /// <summary>
    /// 按智能体类型选择扫描方式。
    /// 当前规则：
    /// 1. 四旋翼使用 3D 射线扫描。
    /// 2. 其它地面智能体使用扇形 SphereCast 扫描。
    /// </summary>
    private void ScanByAgentType()
    {
        if (ownerAgentType == AgentType.Quadcopter)
        {
            ScanAsDrone();
            return;
        }
    }

    /// <summary>
    /// 无人机扫描流程。
    /// 内部调用：
    /// 1. Physics.Raycast 进行 3D 扇扫。
    /// 2. HandleRaycastHit 处理单次命中。
    /// 3. ScanVerticalAxis 补充正上方和正下方探测。
    /// </summary>
    private void ScanAsDrone()
    {
        Vector3 origin = transform.position;
        float maxRange = ownerAgent.Properties.PerceptionRange;
        float angleStep = Mathf.Max(1f, droneScanAngleStep);

        for (float yaw = 0f; yaw < 360f; yaw += angleStep)
        {
            for (float pitch = -45f; pitch <= 45f; pitch += angleStep)
            {
                Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
                Vector3 direction = rotation * Vector3.forward;

                if (Physics.Raycast(origin, direction, out RaycastHit hit, maxRange, detectionLayers))
                {
                    HandleRaycastHit(hit);
                }
            }
        }

        ScanVerticalAxis(origin, maxRange);
    }


    /// <summary>
    /// 处理一次射线或球射线命中。
    /// 调用方：ScanAsDrone / ScanAsGroundVehicle / ScanVerticalAxis
    /// 处理顺序：
    /// 1. 归一化命中对象根节点。
    /// 2. 过滤地面和本轮重复对象。
    /// 3. 识别是智能体还是普通小节点。
    /// 4. 写入共享注册表、事件系统和 detectedObjects。
    /// </summary>
    private void HandleRaycastHit(RaycastHit hit)
    {
        GameObject hitObject = ResolveHitRootObject(hit);
        if (hitObject == null || IsGroundObject(hitObject))
        {
            return;
        }

        int objectId = hitObject.GetInstanceID();
        if (!sensedObjectIdsThisTick.Add(objectId))
        {
            return;
        }

        SmallNodeType detectedType = ResolveDetectedType(hitObject);
        if (detectedType == SmallNodeType.Agent)
        {
            HandleDetectedAgent(hitObject);
            return;
        }

        Vector3 worldPosition = hitObject.transform.position;
        SmallNodeType recordedType = NormalizeRecordedNodeType(detectedType);
        SmallNodeData nodeData = CreateSmallNodeData(hitObject, recordedType, worldPosition);
        if (nodeData == null)
        {
            return;
        }

        RegisterSharedNode(nodeData);
        detectedObjects.Add(nodeData);
    }

    /// <summary>
    /// 处理智能体命中结果。
    /// 调用方：HandleRaycastHit
    /// 输出：
    /// 1. nearbyAgents 记录周边智能体。
    /// 2. enemyAgents 记录敌方智能体。
    /// 3. 发现敌方时上报给 ActionDecisionModule。
    /// </summary>
    private void HandleDetectedAgent(GameObject hitObject)
    {
        IntelligentAgent otherAgent = hitObject.GetComponentInParent<IntelligentAgent>();
        if (otherAgent == null || otherAgent.gameObject == gameObject)
        {
            return;
        }

        if (!nearbyAgents.Contains(otherAgent.gameObject))
        {
            nearbyAgents.Add(otherAgent.gameObject);
        }

        bool isEnemy = otherAgent.Properties != null &&
                       ownerAgent?.Properties != null &&
                       otherAgent.Properties.TeamID != ownerAgent.Properties.TeamID;
        if (!isEnemy || enemyAgents.Contains(otherAgent))
        {
            return;
        }

        enemyAgents.Add(otherAgent);

        string description = $"敌方智能体 {otherAgent.Properties.AgentID} @ {otherAgent.transform.position}";
        actionDecisionModule?.OnPerceptionEvent(description, "enemy");
        Debug.Log($"[PerceptionModule] {ownerAgent?.Properties?.AgentID} 发现敌方: {otherAgent.Properties.AgentID}");
    }

    /// <summary>
    /// 将小节点写入共享注册表，并在必要时上报紧急事件。
    /// 调用方：HandleRaycastHit
    /// </summary>
    private void RegisterSharedNode(SmallNodeData nodeData)
    {
        if (!enableSharedSmallNodeRegistry || nodeData == null)
        {
            return;
        }

        SmallNodeRegistry.RegisterOrUpdate(nodeData);
        //CheckEmergencyEvent(nodeData);

        if (logSmallNodeRegistry)
        {
            Debug.Log(
                $"[PerceptionModule] 小节点登记: id={nodeData.NodeId}, type={nodeData.NodeType}, " +
                $"dynamic={nodeData.IsDynamic}, scene={nodeData.SceneObject?.name ?? "null"}");
        }
    }


    /// <summary>
    /// 将本轮感知结果同步到 IntelligentAgent.CurrentState。
    /// 调用方：ExecutePerceptionCycle
    /// 说明：这里做一次去重，避免同一小节点被重复写入状态。
    /// </summary>
    private void SyncDetectedResultsToAgentState()
    {
        if (ownerAgent?.CurrentState == null)
        {
            return;
        }

        AgentDynamicState currentState = ownerAgent.CurrentState;
        EnsureStateContainers(currentState);
        SyncNearbyAgentsToState(currentState);
        SyncDetectedNodesToState(currentState);
    }

    /// <summary>
    /// 感知结束后按配置清理共享注册表中的过期动态节点。
    /// 调用方：SenseOnce
    /// </summary>
    private void CleanupExpiredDynamicNodesIfNeeded()
    {
        if (!enableSharedSmallNodeRegistry || !cleanupExpiredDynamicNodes)
        {
            return;
        }

        SmallNodeRegistry.CleanupExpiredDynamic(Time.time, dynamicNodeTtl);
    }

    // ------------------------------------------------------------------
    // 类型识别与对象构造：主流程依赖的辅助函数
    // ------------------------------------------------------------------

    /// <summary>
    /// 把 RaycastHit 还原成语义根对象。
    /// 规则：
    /// 1. 优先使用 attachedRigidbody 对应的 GameObject。
    /// 2. 否则尝试向父节点查找 SmallNodeRuntimeInfo 作为语义根。
    /// 3. 如果都没有，则退回碰撞体自身对象。
    /// </summary>
    private GameObject ResolveHitRootObject(RaycastHit hit)
    {
        if (hit.collider == null)
        {
            return null;
        }

        if (hit.collider.attachedRigidbody != null)
        {
            return hit.collider.attachedRigidbody.gameObject;
        }

        GameObject hitObject = hit.collider.gameObject;
        SmallNodeRuntimeInfo runtimeInfo = hitObject.GetComponentInParent<SmallNodeRuntimeInfo>();
        return runtimeInfo != null ? runtimeInfo.gameObject : hitObject;
    }

    /// <summary>
    /// 判断对象是否属于地面层。
    /// 调用方：HandleRaycastHit
    /// </summary>
    private bool IsGroundObject(GameObject obj)
    {
        if (obj == null)
        {
            return false;
        }

        return ((1 << obj.layer) & groundLayer) != 0;
    }

    /// <summary>
    /// 将无法精确识别的命中统一记录为 TemporaryObstacle。
    /// 目的：保持 detectedObjects 与共享注册表记录一致，避免一处是 Unknown、一处是 TemporaryObstacle。
    /// </summary>
    private SmallNodeType NormalizeRecordedNodeType(SmallNodeType detectedType)
    {
        return detectedType == SmallNodeType.Unknown
            ? SmallNodeType.TemporaryObstacle
            : detectedType;
    }

    /// <summary>
    /// 根据对象生成标准 SmallNodeData。
    /// 调用方：HandleRaycastHit / 共享注册表写入逻辑
    /// </summary>
    private SmallNodeData CreateSmallNodeData(GameObject targetObject, SmallNodeType nodeType, Vector3 worldPosition)
    {
        if (nodeType == SmallNodeType.Unknown)
        {
            return null;
        }

        return new SmallNodeData
        {
            NodeId = BuildSmallNodeId(targetObject, nodeType, worldPosition),
            NodeType = nodeType,
            WorldPosition = worldPosition,
            IsDynamic = InferIsDynamic(targetObject, nodeType),
            LastSeenTime = Time.time,
            SceneObject = targetObject
        };
    }

    /// <summary>
    /// 解析命中对象的最终语义类型。
    /// 优先级：
    /// 1. LayerMask 明确命中 Agent / Resource。
    /// 2. SmallNodeRuntimeInfo 显式声明的 nodeType。
    /// 3. obstacleLayers 或对象 Tag / 名称推断。
    /// </summary>
    private SmallNodeType ResolveDetectedType(GameObject obj)
    {
        if (obj == null)
        {
            return SmallNodeType.Unknown;
        }

        int layerMask = 1 << obj.layer;
        if ((layerMask & agentLayers) != 0)
        {
            return SmallNodeType.Agent;
        }

        if ((layerMask & resourceLayers) != 0)
        {
            return SmallNodeType.ResourcePoint;
        }

        SmallNodeRuntimeInfo runtimeInfo = GetRuntimeInfo(obj);
        if (runtimeInfo != null && runtimeInfo.nodeType != SmallNodeType.Unknown)
        {
            return runtimeInfo.nodeType;
        }

        if ((layerMask & obstacleLayers) != 0)
        {
            return InferSmallNodeType(obj, SmallNodeType.TemporaryObstacle);
        }

        return InferSmallNodeType(obj, SmallNodeType.Unknown);
    }

    /// <summary>
    /// 按运行时信息、Tag、对象名来推断小节点类型。
    /// 调用方：ResolveDetectedType
    /// </summary>
    private SmallNodeType InferSmallNodeType(GameObject obj, SmallNodeType fallbackType)
    {
        if (obj == null)
        {
            return fallbackType;
        }

        SmallNodeRuntimeInfo runtimeInfo = GetRuntimeInfo(obj);
        if (runtimeInfo != null && runtimeInfo.nodeType != SmallNodeType.Unknown)
        {
            return runtimeInfo.nodeType;
        }

        if (obj.CompareTag("Resource"))
        {
            return SmallNodeType.ResourcePoint;
        }

        if (obj.CompareTag("Agent"))
        {
            return SmallNodeType.Agent;
        }

        string lowerName = obj.name.ToLowerInvariant();
        if (obj.CompareTag("Tree") || lowerName.Contains("tree"))
        {
            return SmallNodeType.Tree;
        }

        if (obj.CompareTag("Pedestrian") || lowerName.Contains("pedestrian") || lowerName.Contains("ped"))
        {
            return SmallNodeType.Pedestrian;
        }

        if (obj.CompareTag("Vehicle") || lowerName.Contains("vehicle") || lowerName.Contains("car"))
        {
            return SmallNodeType.Vehicle;
        }

        return fallbackType;
    }

    /// <summary>
    /// 推断节点是否属于动态对象。
    /// 优先级：
    /// 1. SmallNodeRuntimeInfo.isDynamic
    /// 2. 类型规则
    /// 3. Rigidbody 兜底判断
    /// </summary>
    private bool InferIsDynamic(GameObject obj, SmallNodeType nodeType)
    {
        SmallNodeRuntimeInfo runtimeInfo = GetRuntimeInfo(obj);
        if (runtimeInfo != null)
        {
            return runtimeInfo.isDynamic;
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
    /// 根据对象实例或离散化坐标生成稳定的小节点 ID。
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
    /// 获取对象或其父节点上的 SmallNodeRuntimeInfo。
    /// 调用方：ResolveDetectedType / InferSmallNodeType / InferIsDynamic
    /// </summary>
    private SmallNodeRuntimeInfo GetRuntimeInfo(GameObject obj)
    {
        if (obj == null)
        {
            return null;
        }

        SmallNodeRuntimeInfo runtimeInfo = obj.GetComponent<SmallNodeRuntimeInfo>();
        if (runtimeInfo != null)
        {
            return runtimeInfo;
        }

        return obj.GetComponentInParent<SmallNodeRuntimeInfo>();
    }

    /// <summary>
    /// 用于初始化 LayerMask 的通用辅助函数。
    /// </summary>
    private void TryAssignLayerMaskByName(ref LayerMask layerMask, string layerName)
    {
        if (layerMask != 0 || string.IsNullOrWhiteSpace(layerName))
        {
            return;
        }

        int layer = LayerMask.NameToLayer(layerName);
        if (layer >= 0)
        {
            layerMask = 1 << layer;
        }
    }

    // ------------------------------------------------------------------
    // 扫描辅助：只服务于扫描过程
    // ------------------------------------------------------------------

    /// <summary>
    /// 补充无人机上下方向探测，减少纯水平扫描带来的盲区。
    /// 调用方：ScanAsDrone
    /// </summary>
    private void ScanVerticalAxis(Vector3 origin, float maxRange)
    {
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit downHit, maxRange, detectionLayers))
        {
            HandleRaycastHit(downHit);
        }

        if (Physics.Raycast(origin, Vector3.up, out RaycastHit upHit, maxRange, detectionLayers))
        {
            HandleRaycastHit(upHit);
        }
    }

    // ------------------------------------------------------------------
    // 状态同步辅助：只负责 CurrentState 写入
    // ------------------------------------------------------------------

    /// <summary>
    /// 保证 CurrentState 里的容器已初始化。
    /// 调用方：SyncDetectedResultsToAgentState
    /// </summary>
    private void EnsureStateContainers(AgentDynamicState currentState)
    {
        if (currentState.NearbyAgents == null)
        {
            currentState.NearbyAgents = new Dictionary<string, GameObject>();
        }

        if (currentState.DetectedSmallNodes == null)
        {
            currentState.DetectedSmallNodes = new List<SmallNodeData>();
        }
    }

    /// <summary>
    /// 将 nearbyAgents 写入 CurrentState.NearbyAgents。
    /// 键：AgentID
    /// 值：对应智能体对象
    /// </summary>
    private void SyncNearbyAgentsToState(AgentDynamicState currentState)
    {
        currentState.NearbyAgents.Clear();

        for (int i = 0; i < nearbyAgents.Count; i++)
        {
            GameObject agentObject = nearbyAgents[i];
            if (agentObject == null)
            {
                continue;
            }

            IntelligentAgent otherAgent = agentObject.GetComponent<IntelligentAgent>();
            if (otherAgent == null || otherAgent.Properties == null)
            {
                continue;
            }

            string otherAgentId = otherAgent.Properties.AgentID;
            if (!currentState.NearbyAgents.ContainsKey(otherAgentId))
            {
                currentState.NearbyAgents.Add(otherAgentId, agentObject);
            }
        }
    }

    /// <summary>
    /// 将 detectedObjects 去重后写入 CurrentState.DetectedSmallNodes。
    /// 去重键：NodeId
    /// </summary>
    private void SyncDetectedNodesToState(AgentDynamicState currentState)
    {
        currentState.DetectedSmallNodes.Clear();

        Dictionary<string, SmallNodeData> uniqueNodes = new Dictionary<string, SmallNodeData>();
        for (int i = 0; i < detectedObjects.Count; i++)
        {
            SmallNodeData source = detectedObjects[i];
            if (source == null)
            {
                continue;
            }

            string nodeId = string.IsNullOrWhiteSpace(source.NodeId)
                ? BuildSmallNodeId(source.SceneObject, source.NodeType, source.WorldPosition)
                : source.NodeId;

            SmallNodeData copiedNode = CloneSmallNodeData(source);
            copiedNode.NodeId = nodeId;

            if (uniqueNodes.TryGetValue(nodeId, out SmallNodeData existingNode))
            {
                existingNode.LastSeenTime = Mathf.Max(existingNode.LastSeenTime, copiedNode.LastSeenTime);
                if (existingNode.SceneObject == null)
                {
                    existingNode.SceneObject = copiedNode.SceneObject;
                }
            }
            else
            {
                uniqueNodes[nodeId] = copiedNode;
            }
        }

        foreach (KeyValuePair<string, SmallNodeData> pair in uniqueNodes)
        {
            currentState.DetectedSmallNodes.Add(pair.Value);
        }
    }

    /// <summary>
    /// 复制 SmallNodeData，避免把运行时结果列表直接暴露给状态对象。
    /// </summary>
    private static SmallNodeData CloneSmallNodeData(SmallNodeData source)
    {
        if (source == null)
        {
            return null;
        }

        return new SmallNodeData
        {
            NodeId = source.NodeId,
            NodeType = source.NodeType,
            WorldPosition = source.WorldPosition,
            IsDynamic = source.IsDynamic,
            LastSeenTime = source.LastSeenTime,
            SceneObject = source.SceneObject
        };
    }
}

/// <summary>
/// 全局共享小节点注册表。
/// 设计原则：
/// 1. 按 NodeId 去重，避免多个智能体重复缓存同一静态节点。
/// 2. 动态节点支持 TTL 清理。
/// 3. 查询时按桶索引缩小搜索范围，降低遍历成本。
/// </summary>
public static class SmallNodeRegistry
{
    private const float BucketSize = 8f;

    private static readonly Dictionary<string, SmallNodeData> nodes = new Dictionary<string, SmallNodeData>();
    private static readonly Dictionary<string, Vector2Int> nodeToBucket = new Dictionary<string, Vector2Int>();
    private static readonly Dictionary<Vector2Int, HashSet<string>> bucketToNodes = new Dictionary<Vector2Int, HashSet<string>>();

    // ------------------------------------------------------------------
    // 对外接口：外部脚本应优先使用这些方法
    // ------------------------------------------------------------------

    /// <summary>
    /// 注册或更新一个小节点。
    /// 调用方：PerceptionModule.RegisterSharedNode
    /// </summary>
    public static void RegisterOrUpdate(SmallNodeData data)
    {
        if (data == null || string.IsNullOrWhiteSpace(data.NodeId))
        {
            return;
        }

        if (nodes.TryGetValue(data.NodeId, out SmallNodeData existing))
        {
            existing.LastSeenTime = Mathf.Max(existing.LastSeenTime, data.LastSeenTime);

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

        SmallNodeData copiedNode = Clone(data);
        nodes[copiedNode.NodeId] = copiedNode;
        AddNodeToBucket(copiedNode.NodeId, copiedNode.WorldPosition);
    }

    /// <summary>
    /// 按 NodeId 查询单个节点。
    /// 返回值是副本，避免外部直接修改注册表内部对象。
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
    /// 查询某个圆形区域内的小节点。
    /// 参数：
    /// 1. includeStatic：是否包含静态节点。
    /// 2. includeDynamic：是否包含动态节点。
    /// </summary>
    public static List<SmallNodeData> QueryNodes(Vector3 center, float radius, bool includeStatic, bool includeDynamic)
    {
        List<SmallNodeData> result = new List<SmallNodeData>();
        if (nodes.Count == 0 || radius <= 0f)
        {
            return result;
        }

        float radiusSquared = radius * radius;
        int bucketMinX = Mathf.FloorToInt((center.x - radius) / BucketSize);
        int bucketMaxX = Mathf.FloorToInt((center.x + radius) / BucketSize);
        int bucketMinZ = Mathf.FloorToInt((center.z - radius) / BucketSize);
        int bucketMaxZ = Mathf.FloorToInt((center.z + radius) / BucketSize);
        HashSet<string> visitedNodeIds = new HashSet<string>();

        for (int bucketX = bucketMinX; bucketX <= bucketMaxX; bucketX++)
        {
            for (int bucketZ = bucketMinZ; bucketZ <= bucketMaxZ; bucketZ++)
            {
                Vector2Int bucket = new Vector2Int(bucketX, bucketZ);
                if (!bucketToNodes.TryGetValue(bucket, out HashSet<string> nodeIds))
                {
                    continue;
                }

                foreach (string nodeId in nodeIds)
                {
                    if (!visitedNodeIds.Add(nodeId))
                    {
                        continue;
                    }

                    if (!nodes.TryGetValue(nodeId, out SmallNodeData node))
                    {
                        continue;
                    }

                    if (node.IsDynamic && !includeDynamic)
                    {
                        continue;
                    }

                    if (!node.IsDynamic && !includeStatic)
                    {
                        continue;
                    }

                    Vector3 delta = node.WorldPosition - center;
                    delta.y = 0f;
                    if (delta.sqrMagnitude <= radiusSquared)
                    {
                        result.Add(Clone(node));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 清理超时或场景对象已失效的动态节点。
    /// 调用方：PerceptionModule.CleanupExpiredDynamicNodesIfNeeded
    /// </summary>
    public static void CleanupExpiredDynamic(float now, float dynamicTtl)
    {
        if (dynamicTtl <= 0f || nodes.Count == 0)
        {
            return;
        }

        List<string> removeIds = new List<string>();
        foreach (KeyValuePair<string, SmallNodeData> pair in nodes)
        {
            SmallNodeData node = pair.Value;
            if (!node.IsDynamic)
            {
                continue;
            }

            bool missingSceneReference = node.SceneObject == null;
            bool expired = now - node.LastSeenTime > dynamicTtl;
            if (missingSceneReference || expired)
            {
                removeIds.Add(pair.Key);
            }
        }

        for (int i = 0; i < removeIds.Count; i++)
        {
            RemoveNode(removeIds[i]);
        }
    }

    /// <summary>
    /// 清空整个共享注册表。
    /// 调用方：PerceptionModule.InitializeSharedRegistry
    /// </summary>
    public static void ClearAll()
    {
        nodes.Clear();
        nodeToBucket.Clear();
        bucketToNodes.Clear();
    }

    // ------------------------------------------------------------------
    // 内部辅助：仅供注册表内部使用
    // ------------------------------------------------------------------

    /// <summary>
    /// 从主表和桶索引里移除一个节点。
    /// </summary>
    private static void RemoveNode(string nodeId)
    {
        nodes.Remove(nodeId);

        if (!nodeToBucket.TryGetValue(nodeId, out Vector2Int bucket))
        {
            return;
        }

        nodeToBucket.Remove(nodeId);
        if (!bucketToNodes.TryGetValue(bucket, out HashSet<string> nodeIds))
        {
            return;
        }

        nodeIds.Remove(nodeId);
        if (nodeIds.Count == 0)
        {
            bucketToNodes.Remove(bucket);
        }
    }

    /// <summary>
    /// 把节点加入空间桶索引。
    /// </summary>
    private static void AddNodeToBucket(string nodeId, Vector3 position)
    {
        Vector2Int bucket = ToBucket(position);
        nodeToBucket[nodeId] = bucket;

        if (!bucketToNodes.TryGetValue(bucket, out HashSet<string> nodeIds))
        {
            nodeIds = new HashSet<string>();
            bucketToNodes[bucket] = nodeIds;
        }

        nodeIds.Add(nodeId);
    }

    /// <summary>
    /// 如果节点位置跨桶，则更新其桶索引。
    /// </summary>
    private static void MoveNodeBucketIfNeeded(string nodeId, Vector3 position)
    {
        Vector2Int newBucket = ToBucket(position);
        if (!nodeToBucket.TryGetValue(nodeId, out Vector2Int oldBucket))
        {
            AddNodeToBucket(nodeId, position);
            return;
        }

        if (newBucket == oldBucket)
        {
            return;
        }

        if (bucketToNodes.TryGetValue(oldBucket, out HashSet<string> oldNodeIds))
        {
            oldNodeIds.Remove(nodeId);
            if (oldNodeIds.Count == 0)
            {
                bucketToNodes.Remove(oldBucket);
            }
        }

        nodeToBucket[nodeId] = newBucket;
        if (!bucketToNodes.TryGetValue(newBucket, out HashSet<string> newNodeIds))
        {
            newNodeIds = new HashSet<string>();
            bucketToNodes[newBucket] = newNodeIds;
        }

        newNodeIds.Add(nodeId);
    }

    /// <summary>
    /// 把世界坐标映射到桶索引坐标。
    /// </summary>
    private static Vector2Int ToBucket(Vector3 position)
    {
        return new Vector2Int(
            Mathf.FloorToInt(position.x / BucketSize),
            Mathf.FloorToInt(position.z / BucketSize));
    }

    /// <summary>
    /// 复制 SmallNodeData，保证对外返回的是独立对象。
    /// </summary>
    private static SmallNodeData Clone(SmallNodeData source)
    {
        return new SmallNodeData
        {
            NodeId = source.NodeId,
            NodeType = source.NodeType,
            WorldPosition = source.WorldPosition,
            IsDynamic = source.IsDynamic,
            LastSeenTime = source.LastSeenTime,
            SceneObject = source.SceneObject
        };
    }
}
