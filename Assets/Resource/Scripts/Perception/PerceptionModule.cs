using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 智能体感知模块。
/// 只负责环境检测、状态同步、共享小节点登记和事件上报，不包含任何可视化实现。
/// </summary>
public class PerceptionModule : MonoBehaviour
{
    [Header("基础感知配置")]
    public float updateInterval = 0.5f;
    private float lastPerceptionTime;

    [Header("分层检测配置")]
    public LayerMask detectionLayers;
    public LayerMask obstacleLayers;
    public LayerMask resourceLayers;
    public LayerMask agentLayers;
    public bool autoConfigureLayerMasks = true;
    [Tooltip("自动配置时使用的障碍层名称（除 ResourcePoint 外其余小节点）")]
    public string obstacleLayerName = "Obstacle";
    [Tooltip("自动配置时使用的资源层名称")]
    public string resourceLayerName = "Resource";
    [Tooltip("自动配置时使用的智能体层名称")]
    public string agentLayerName = "Agent";
    [Tooltip("地面层")]
    public LayerMask groundLayer;

    [Header("无人机配置")]
    public float droneScanAngleStep = 15f;

    [Header("无人车配置")]
    public float groundHorizontalAngle = 270f;
    public float groundHeightRange = 2f;

    [Header("小节点共享感知库")]
    public bool enableSharedSmallNodeRegistry = true;
    public bool clearSharedRegistryOnStart = true;
    [Min(1f)] public float dynamicNodeTtl = 10f;
    public bool cleanupExpiredDynamicNodes = true;
    public bool logSmallNodeRegistry = false;

    // 当前感知结果
    public List<SmallNodeData> detectedObjects = new List<SmallNodeData>();
    public List<GameObject> nearbyAgents = new List<GameObject>();
    public List<IntelligentAgent> enemyAgents = new List<IntelligentAgent>();

    private ActionDecisionModule adm;
    private IntelligentAgent agent;
    private AgentType agentType;

    private static bool sharedRegistryInitialized;
    private readonly HashSet<int> sensedObjectIdsThisTick = new HashSet<int>();

    /// <summary>最近一次完成感知的时间，供外部模块判断是否有新感知结果。</summary>
    public float LastPerceptionTime => lastPerceptionTime;

    private void Start()
    {
        if (enableSharedSmallNodeRegistry && clearSharedRegistryOnStart && !sharedRegistryInitialized)
        {
            SmallNodeRegistry.ClearAll();
            sharedRegistryInitialized = true;
        }

        agent = GetComponent<IntelligentAgent>();
        if (agent == null)
        {
            Debug.LogError("[PerceptionModule] 未找到 IntelligentAgent 组件");
            return;
        }

        agentType = agent.Properties.Type;
        adm = GetComponent<ActionDecisionModule>();
        InitializePerceptionRange();
    }

    private void Update()
    {
        if (Time.time - lastPerceptionTime >= updateInterval)
            SenseOnce();
    }

    /// <summary>
    /// 手动触发一次完整感知。
    /// </summary>
    public void SenseOnce()
    {
        UpdatePerception();

        if (enableSharedSmallNodeRegistry && cleanupExpiredDynamicNodes)
            SmallNodeRegistry.CleanupExpiredDynamic(Time.time, dynamicNodeTtl);

        lastPerceptionTime = Time.time;
    }

    private void InitializePerceptionRange()
    {
        if (autoConfigureLayerMasks)
        {
            if (obstacleLayers == 0)
            {
                int layer = LayerMask.NameToLayer(obstacleLayerName);
                if (layer >= 0) obstacleLayers = 1 << layer;
            }
            if (resourceLayers == 0)
            {
                int layer = LayerMask.NameToLayer(resourceLayerName);
                if (layer >= 0) resourceLayers = 1 << layer;
            }
            if (agentLayers == 0)
            {
                int layer = LayerMask.NameToLayer(agentLayerName);
                if (layer >= 0) agentLayers = 1 << layer;
            }
        }

        detectionLayers |= obstacleLayers;
        detectionLayers |= resourceLayers;
        detectionLayers |= agentLayers;

        if (detectionLayers == 0)
        {
            detectionLayers = Physics.DefaultRaycastLayers;
            Debug.LogWarning("[PerceptionModule] detectionLayers 为空，已自动回退到 Physics.DefaultRaycastLayers");
        }

        if (groundLayer == 0)
        {
            int layer = LayerMask.NameToLayer("Ground");
            if (layer >= 0) groundLayer = 1 << layer;
        }
    }

    /// <summary>
    /// 核心感知更新：只产出数据，不做渲染。
    /// </summary>
    private void UpdatePerception()
    {
        if (agent == null) return;

        detectedObjects.Clear();
        nearbyAgents.Clear();
        enemyAgents.Clear();
        sensedObjectIdsThisTick.Clear();

        if (agentType == AgentType.Quadcopter)
            PerceiveAsDrone();
        else
            PerceiveAsGroundVehicle();

        UpdateAgentState();
    }

    private void PerceiveAsDrone()
    {
        Vector3 origin = transform.position;
        float maxRange = agent.Properties.PerceptionRange;
        for (float yaw = 0f; yaw < 360f; yaw += droneScanAngleStep)
        {
            for (float pitch = -45f; pitch <= 45f; pitch += droneScanAngleStep)
            {
                Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
                Vector3 direction = rotation * Vector3.forward;

                if (Physics.Raycast(origin, direction, out RaycastHit hit, maxRange, detectionLayers))
                    ProcessValidDetection(hit);
            }
        }

        CheckVerticalDetection(origin, maxRange);
    }

    private void PerceiveAsGroundVehicle()
    {
        Vector3 origin = transform.position + Vector3.up * 0.5f;
        float maxRange = agent.Properties.PerceptionRange;
        Vector3 forward = transform.forward;
        float halfAngle = groundHorizontalAngle / 2f;
        int rayCount = Mathf.RoundToInt(groundHorizontalAngle / 5f);
        const float sphereRadius = 0.4f;
        for (int i = 0; i <= rayCount; i++)
        {
            float angle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / rayCount);
            Quaternion rotation = Quaternion.Euler(0f, angle, 0f);
            Vector3 direction = rotation * forward;
            direction.y = 0f;
            direction.Normalize();

            if (Physics.SphereCast(origin, sphereRadius, direction, out RaycastHit hit, maxRange, detectionLayers) &&
                Mathf.Abs(hit.collider.bounds.center.y - origin.y) <= groundHeightRange)
            {
                ProcessValidDetection(hit);
            }
        }
    }

    private void ProcessValidDetection(RaycastHit hit)
    {
        GameObject hitObject = hit.collider != null ? hit.collider.gameObject : null;
        if (hitObject == null) return;

        if (hit.collider.attachedRigidbody != null)
            hitObject = hit.collider.attachedRigidbody.gameObject;
        else
        {
            SmallNodeRuntimeInfo rootInfo = hitObject.GetComponentInParent<SmallNodeRuntimeInfo>();
            if (rootInfo != null) hitObject = rootInfo.gameObject;
        }

        if (((1 << hitObject.layer) & groundLayer) != 0)
            return;

        int objectId = hitObject.GetInstanceID();
        if (!sensedObjectIdsThisTick.Add(objectId))
            return;

        SmallNodeType detectedType = ResolveDetectedType(hitObject);
        if (detectedType == SmallNodeType.Agent)
        {
            HandleDetectedAgent(hitObject);
            return;
        }

        Vector3 objectPosition = hitObject.transform.position;
        UpdatePerceptionGrid(objectPosition, detectedType, hitObject);

        if (detectedType == SmallNodeType.Unknown)
            detectedType = SmallNodeType.TemporaryObstacle;

        if (detectedType == SmallNodeType.Unknown)
            return;

        detectedObjects.Add(new SmallNodeData
        {
            NodeId = BuildSmallNodeId(hitObject, detectedType, objectPosition),
            NodeType = detectedType,
            WorldPosition = objectPosition,
            IsDynamic = InferIsDynamic(hitObject, detectedType),
            LastSeenTime = Time.time,
            SceneObject = hitObject
        });

    }

    private void HandleDetectedAgent(GameObject hitObject)
    {
        IntelligentAgent otherAgent = hitObject.GetComponentInParent<IntelligentAgent>();
        if (otherAgent == null || otherAgent.gameObject == gameObject) return;

        if (!nearbyAgents.Contains(otherAgent.gameObject))
            nearbyAgents.Add(otherAgent.gameObject);

        bool isEnemy = agent != null && agent.Properties != null &&
                       otherAgent.Properties != null &&
                       otherAgent.Properties.TeamID != agent.Properties.TeamID;
        if (!isEnemy || enemyAgents.Contains(otherAgent)) return;

        enemyAgents.Add(otherAgent);
        string desc = $"敌方智能体 {otherAgent.Properties.AgentID} @ {otherAgent.transform.position}";
        adm?.OnPerceptionEvent(desc, "enemy");
        Debug.Log($"[PerceptionModule] {agent?.Properties?.AgentID} 发现敌方: {otherAgent.Properties.AgentID}");
    }

    private void UpdatePerceptionGrid(Vector3 worldPosition, SmallNodeType nodeType, GameObject terrainObject)
    {
        if (!enableSharedSmallNodeRegistry) return;

        SmallNodeType smallType = InferSmallNodeType(terrainObject, nodeType);
        bool isDynamic = InferIsDynamic(terrainObject, smallType);

        SmallNodeData data = new SmallNodeData
        {
            NodeId = BuildSmallNodeId(terrainObject, smallType, worldPosition),
            NodeType = smallType,
            WorldPosition = worldPosition,
            IsDynamic = isDynamic,
            LastSeenTime = Time.time,
            SceneObject = terrainObject
        };

        SmallNodeRegistry.RegisterOrUpdate(data);
        CheckEmergencyEvent(data);

        if (logSmallNodeRegistry)
        {
            Debug.Log($"[PerceptionModule] 小节点登记: id={data.NodeId}, type={data.NodeType}, dynamic={data.IsDynamic}, scene={data.SceneObject?.name ?? "null"}");
        }
    }

    /// <summary>
    /// 紧急事件属于感知结果的一部分，PerceptionModule 只负责上报和记录，不负责可视化。
    /// </summary>
    private void CheckEmergencyEvent(SmallNodeData node)
    {
        if (node == null || node.NodeType != SmallNodeType.TemporaryObstacle) return;

        string desc = $"障碍 {node.SceneObject?.name ?? node.NodeType.ToString()} 阻断路径";
        Debug.Log($"[PerceptionModule] 临时障碍事件: {desc}");
        adm?.OnPerceptionEvent(desc, node.NodeId);
    }

    private SmallNodeType InferSmallNodeType(GameObject obj, SmallNodeType fallbackType)
    {
        if (obj == null) return fallbackType;

        SmallNodeRuntimeInfo runtimeInfo = obj.GetComponent<SmallNodeRuntimeInfo>();
        if (runtimeInfo == null)
            runtimeInfo = obj.GetComponentInParent<SmallNodeRuntimeInfo>();

        if (runtimeInfo != null && runtimeInfo.nodeType != SmallNodeType.Unknown)
            return runtimeInfo.nodeType;

        if (obj.CompareTag("Resource")) return SmallNodeType.ResourcePoint;
        if (obj.CompareTag("Agent")) return SmallNodeType.Agent;

        string lowerName = obj.name.ToLowerInvariant();
        if (obj.CompareTag("Tree") || lowerName.Contains("tree")) return SmallNodeType.Tree;
        if (obj.CompareTag("Pedestrian") || lowerName.Contains("pedestrian") || lowerName.Contains("ped")) return SmallNodeType.Pedestrian;
        if (obj.CompareTag("Vehicle") || lowerName.Contains("vehicle") || lowerName.Contains("car")) return SmallNodeType.Vehicle;

        return fallbackType;
    }

    private bool InferIsDynamic(GameObject obj, SmallNodeType nodeType)
    {
        if (obj != null)
        {
            SmallNodeRuntimeInfo runtimeInfo = obj.GetComponent<SmallNodeRuntimeInfo>();
            if (runtimeInfo == null)
                runtimeInfo = obj.GetComponentInParent<SmallNodeRuntimeInfo>();

            if (runtimeInfo != null)
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

    private SmallNodeType ResolveDetectedType(GameObject obj)
    {
        if (obj == null) return SmallNodeType.Unknown;

        int mask = 1 << obj.layer;
        if ((mask & agentLayers) != 0) return SmallNodeType.Agent;
        if ((mask & resourceLayers) != 0) return SmallNodeType.ResourcePoint;

        SmallNodeRuntimeInfo info = obj.GetComponent<SmallNodeRuntimeInfo>();
        if (info == null)
            info = obj.GetComponentInParent<SmallNodeRuntimeInfo>();

        if (info != null && info.nodeType != SmallNodeType.Unknown)
            return info.nodeType;

        if ((mask & obstacleLayers) != 0)
            return InferSmallNodeType(obj, SmallNodeType.TemporaryObstacle);

        return InferSmallNodeType(obj, SmallNodeType.Unknown);
    }

    private string BuildSmallNodeId(GameObject obj, SmallNodeType nodeType, Vector3 pos)
    {
        if (obj != null)
            return $"{nodeType}:{obj.GetInstanceID()}";

        int px = Mathf.RoundToInt(pos.x * 10f);
        int py = Mathf.RoundToInt(pos.y * 10f);
        int pz = Mathf.RoundToInt(pos.z * 10f);
        return $"{nodeType}:P({px},{py},{pz})";
    }

    private void CheckVerticalDetection(Vector3 origin, float maxRange)
    {
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit downHit, maxRange, detectionLayers))
            ProcessValidDetection(downHit);

        if (Physics.Raycast(origin, Vector3.up, out RaycastHit upHit, maxRange, detectionLayers))
            ProcessValidDetection(upHit);
    }

    private void UpdateAgentState()
    {
        if (agent?.CurrentState == null) return;

        AgentDynamicState currentState = agent.CurrentState;
        if (currentState.NearbyAgents == null) currentState.NearbyAgents = new Dictionary<string, GameObject>();
        if (currentState.DetectedSmallNodes == null) currentState.DetectedSmallNodes = new List<SmallNodeData>();

        currentState.NearbyAgents.Clear();
        for (int i = 0; i < nearbyAgents.Count; i++)
        {
            GameObject agentObj = nearbyAgents[i];
            if (agentObj == null) continue;

            IntelligentAgent otherAgent = agentObj.GetComponent<IntelligentAgent>();
            if (otherAgent == null || otherAgent.Properties == null) continue;

            string otherId = otherAgent.Properties.AgentID;
            if (!currentState.NearbyAgents.ContainsKey(otherId))
                currentState.NearbyAgents.Add(otherId, agentObj);
        }

        currentState.DetectedSmallNodes.Clear();
        Dictionary<string, SmallNodeData> uniqueNodes = new Dictionary<string, SmallNodeData>();
        for (int i = 0; i < detectedObjects.Count; i++)
        {
            SmallNodeData src = detectedObjects[i];
            if (src == null) continue;

            string nodeId = string.IsNullOrWhiteSpace(src.NodeId)
                ? BuildSmallNodeId(src.SceneObject, src.NodeType, src.WorldPosition)
                : src.NodeId;

            SmallNodeData copy = CloneSmallNodeData(src);
            copy.NodeId = nodeId;

            if (uniqueNodes.TryGetValue(nodeId, out SmallNodeData existing))
            {
                existing.LastSeenTime = Mathf.Max(existing.LastSeenTime, copy.LastSeenTime);
                if (existing.SceneObject == null) existing.SceneObject = copy.SceneObject;
            }
            else
            {
                uniqueNodes[nodeId] = copy;
            }
        }

        foreach (KeyValuePair<string, SmallNodeData> kv in uniqueNodes)
            currentState.DetectedSmallNodes.Add(kv.Value);
    }

    private static SmallNodeData CloneSmallNodeData(SmallNodeData src)
    {
        if (src == null) return null;

        return new SmallNodeData
        {
            NodeId = src.NodeId,
            NodeType = src.NodeType,
            WorldPosition = src.WorldPosition,
            IsDynamic = src.IsDynamic,
            LastSeenTime = src.LastSeenTime,
            SceneObject = src.SceneObject
        };
    }

}

/// <summary>
/// 全局共享小节点注册表（静态）：
/// 1) 默认空，不预扫描场景；只有感知命中才写入。
/// 2) 静态节点只保存一份，避免每个智能体重复缓存。
/// 3) 动态节点支持 TTL 清理。
/// </summary>
public static class SmallNodeRegistry
{
    private static readonly Dictionary<string, SmallNodeData> nodes = new Dictionary<string, SmallNodeData>();
    private static readonly Dictionary<string, Vector2Int> nodeToBucket = new Dictionary<string, Vector2Int>();
    private static readonly Dictionary<Vector2Int, HashSet<string>> bucketToNodes = new Dictionary<Vector2Int, HashSet<string>>();

    private const float BucketSize = 8f;

    public static void RegisterOrUpdate(SmallNodeData data)
    {
        if (data == null || string.IsNullOrWhiteSpace(data.NodeId)) return;

        if (nodes.TryGetValue(data.NodeId, out SmallNodeData existing))
        {
            existing.LastSeenTime = Mathf.Max(existing.LastSeenTime, data.LastSeenTime);

            if (existing.IsDynamic || existing.SceneObject == null)
                existing.WorldPosition = data.WorldPosition;

            if (existing.SceneObject == null && data.SceneObject != null)
                existing.SceneObject = data.SceneObject;

            MoveNodeBucketIfNeeded(existing.NodeId, existing.WorldPosition);
            return;
        }

        SmallNodeData copy = Clone(data);
        nodes[copy.NodeId] = copy;
        AddNodeToBucket(copy.NodeId, copy.WorldPosition);
    }

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

    public static List<SmallNodeData> QueryNodes(Vector3 center, float radius, bool includeStatic, bool includeDynamic)
    {
        List<SmallNodeData> result = new List<SmallNodeData>();
        if (nodes.Count == 0 || radius <= 0f) return result;

        float r2 = radius * radius;
        int bxMin = Mathf.FloorToInt((center.x - radius) / BucketSize);
        int bxMax = Mathf.FloorToInt((center.x + radius) / BucketSize);
        int bzMin = Mathf.FloorToInt((center.z - radius) / BucketSize);
        int bzMax = Mathf.FloorToInt((center.z + radius) / BucketSize);
        HashSet<string> visited = new HashSet<string>();

        for (int bx = bxMin; bx <= bxMax; bx++)
        {
            for (int bz = bzMin; bz <= bzMax; bz++)
            {
                Vector2Int bucket = new Vector2Int(bx, bz);
                if (!bucketToNodes.TryGetValue(bucket, out HashSet<string> ids)) continue;

                foreach (string id in ids)
                {
                    if (!visited.Add(id)) continue;
                    if (!nodes.TryGetValue(id, out SmallNodeData node)) continue;
                    if (node.IsDynamic && !includeDynamic) continue;
                    if (!node.IsDynamic && !includeStatic) continue;

                    Vector3 delta = node.WorldPosition - center;
                    delta.y = 0f;
                    if (delta.sqrMagnitude <= r2)
                        result.Add(Clone(node));
                }
            }
        }

        return result;
    }

    public static void CleanupExpiredDynamic(float now, float dynamicTtl)
    {
        if (dynamicTtl <= 0f || nodes.Count == 0) return;

        List<string> removeIds = new List<string>();
        foreach (KeyValuePair<string, SmallNodeData> kv in nodes)
        {
            SmallNodeData node = kv.Value;
            if (!node.IsDynamic) continue;

            bool missingRef = node.SceneObject == null;
            bool expired = (now - node.LastSeenTime) > dynamicTtl;
            if (missingRef || expired)
                removeIds.Add(kv.Key);
        }

        for (int i = 0; i < removeIds.Count; i++)
            RemoveNode(removeIds[i]);
    }

    public static void ClearAll()
    {
        nodes.Clear();
        nodeToBucket.Clear();
        bucketToNodes.Clear();
    }

    private static void RemoveNode(string nodeId)
    {
        nodes.Remove(nodeId);

        if (!nodeToBucket.TryGetValue(nodeId, out Vector2Int bucket)) return;

        nodeToBucket.Remove(nodeId);
        if (!bucketToNodes.TryGetValue(bucket, out HashSet<string> ids)) return;

        ids.Remove(nodeId);
        if (ids.Count == 0)
            bucketToNodes.Remove(bucket);
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
            if (oldIds.Count == 0)
                bucketToNodes.Remove(oldBucket);
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
            Mathf.FloorToInt(pos.z / BucketSize));
    }

    private static SmallNodeData Clone(SmallNodeData src)
    {
        return new SmallNodeData
        {
            NodeId = src.NodeId,
            NodeType = src.NodeType,
            WorldPosition = src.WorldPosition,
            IsDynamic = src.IsDynamic,
            LastSeenTime = src.LastSeenTime,
            SceneObject = src.SceneObject
        };
    }
}
