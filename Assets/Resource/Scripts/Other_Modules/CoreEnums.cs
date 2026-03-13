using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 智能体类型
/// </summary>
public enum AgentType
{
    Quadcopter,    // 四旋翼无人机
    WheeledRobot,  // 轮式机器人
}

/// <summary>
/// 小节点类型：
/// 这类对象不进入 CampusGrid2D 主网格，统一通过感知系统动态发现并维护。
/// 例如：树木、行人、车辆、临时资源点等。
/// </summary>
public enum SmallNodeType
{
    Unknown = 0,           // 未知类型；当感知层还没法稳定分类时使用。
    Tree = 1,              // 树木、树丛等静态自然障碍或环境对象。
    Pedestrian = 2,        // 行人或其他可移动的人类目标。
    Vehicle = 3,           // 车辆、车体或其他可移动载具目标。
    ResourcePoint = 4,     // 资源点、补给点、物资点或可收集目标。
    TemporaryObstacle = 5, // 临时障碍，例如施工物、路障、临时封锁物。
    Agent = 6,             // 智能体对象本身；通常用于把队友/敌方也纳入统一小节点感知。
    Custom = 99            // 自定义扩展类型；给项目侧预留。
}

/// <summary>
/// 小节点数据结构：
/// 用于保存一次可追踪的小节点观测结果。
/// </summary>
[System.Serializable]
public class SmallNodeData
{
    public string NodeId;               // 全局唯一节点ID
    public SmallNodeType NodeType;      // 节点类型
    public Vector3 WorldPosition;       // 世界坐标
    public bool IsDynamic;              // 是否动态对象（如行人/车辆）
    public bool BlocksMovement;         // 是否阻塞通行
    public float FirstSeenTime;         // 首次观测时间
    public float LastSeenTime;          // 最近观测时间
    public float Confidence;            // 置信度 [0,1]
    public int SeenCount;               // 被观测次数
    public string SourceAgentId;        // 最近上报的智能体ID
    public string DisplayName;          // 展示名称（调试/日志）
    public GameObject SceneObject;      // 场景对象引用（可为空）
}

/// <summary>
/// 小节点感知快照：
/// 供决策或调试模块一次性读取当前范围内感知信息。
/// </summary>
[System.Serializable]
public class SmallNodePerceptionSnapshot
{
    public string AgentId;                         // 智能体ID
    public float QueryTime;                        // 快照时间
    public Vector3 AgentPosition;                  // 查询时智能体位置
    public float QueryRadius;                      // 查询半径
    public List<SmallNodeData> NearbyStaticNodes = new List<SmallNodeData>();   // 附近静态小节点
    public List<SmallNodeData> NearbyDynamicNodes = new List<SmallNodeData>();  // 附近动态小节点
    public List<SmallNodeData> NearbyResourceNodes = new List<SmallNodeData>(); // 附近资源类小节点
}

/// <summary>
/// 校园地点感知数据：
/// 用于记录智能体在当前感知周期内“真实看到”的建筑/地点信息。
/// 设计说明：
/// 1) 建筑等大目标会占据多个网格，因此同时记录“样本网格列表”与“代表坐标”；
/// 2) Anchor 用于导航锚点（通常是离智能体更近的观测单元）；
/// 3) ApproxCenter 用于语义描述与提示词，不直接用于刚性路径终点。
/// </summary>
[System.Serializable]
public class CampusFeaturePerceptionData
{
    public string FeatureUid;                         // 地点唯一ID（来自 CampusGrid2D）
    public string FeatureName;                        // 地点名称（如 building_5）
    public string FeatureKind;                        // 地点类型字符串（Building/Water/...）
    public bool BlocksMovement;                       // 该地点是否阻塞通行
    public Vector2Int AnchorGridCell;                 // 导航锚点网格（局部可见样本中更靠近智能体的一格）
    public Vector3 AnchorWorldPosition;               // 导航锚点世界坐标
    public Vector3 ApproxCenterWorldPosition;         // 样本网格估计中心世界坐标
    public int ObservedCellCount;                     // 本轮观测到的网格数
    public List<Vector2Int> ObservedSampleCells = new List<Vector2Int>(); // 观测样本网格（有上限，防止爆内存）
    public float FirstSeenTime;                       // 首次观测时间
    public float LastSeenTime;                        // 最近观测时间
    public int SeenCount;                             // 被观测次数
    public float Confidence;                          // 置信度 [0,1]
    public string SourceAgentId;                      // 最近观测来源智能体ID
}

/// <summary>
/// 智能体状态
/// </summary>
public enum AgentStatus
{
    Idle,           // 空闲
    Moving,         // 移动中
    Thinking,       // 决策中
    ExecutingTask,  // 执行任务中
    Charging,       // 充电中
    Error           // 错误状态
}

/// <summary>
/// 任务目标类型枚举。
/// 设计约束：
/// 1) 这里只回答“要去做什么任务”；
/// 2) 不再混入 Cooperation / Competition 这类关系语义；
/// 3) 队伍关系统一由 TeamRelationshipType 表达。
/// </summary>
public enum MissionType
{
    Unknown = 0,         // 未知任务；当上游没有可靠任务类型时的保守默认值。
    Exploration = 1,     // 探索未知区域。
    Reconnaissance = 2,  // 定向侦察指定目标。
    SearchRescue = 3,    // 搜索伤员并协同救援。
    Patrol = 4,          // 巡逻巡查指定区域或路线。
    GuardDefense = 5,    // 守卫防御关键目标。
    Escort = 6,          // 护送对象或队伍。
    Transport = 7,       // 运输物资、设备或消息。
    Inspection = 8,      // 巡检建筑、设施或外立面。
    CoverageSurvey = 9,  // 覆盖式扫描、普查或搜索。
    PursuitEvasion = 10, // 追击、追逃或围堵动态目标。
    Interception = 11,   // 拦截路线、通道或目标。
    OccupyHold = 12,     // 占领并固守关键区域。
    Evacuation = 13,     // 疏散、撤离或引导离场。
    ConstructionRepair = 14, // 建设、搭建或维修设施。
    ResourceCollection = 15, // 采集或争夺资源。
    CommunicationRelay = 16  // 建立、保持或接力通信链路。
}

/// <summary>
/// 队伍关系类型。
/// 它回答的是“这次多智能体关系是什么”，而不是“具体去干什么任务”。
/// </summary>
public enum TeamRelationshipType
{
    Cooperation = 0, // 以合作协作为主
    Competition = 1, // 以竞争抢占为主
    Adversarial = 2, // 以敌对对抗为主
    Mixed = 3        // 同时包含协作、竞争或对抗
}

/// <summary>
/// 导航策略枚举（任务级默认倾向）。
/// 注意：这是“默认策略”，具体 step 仍可按规则覆盖。
/// </summary>
public enum NavigationPolicy
{
    Auto = 0,            // 自动：由 MissionType 与 Step 规则联合判定
    PreferLocal = 1,     // 优先局部探索/局部机动
    PreferGlobalAStar = 2// 优先全局 A* 粗路径引导
}

/// <summary>
/// 基础角色类型枚举
/// </summary>
public enum RoleType
{
    Supporter,      // 支援者 - 提供支援和掩护(默认)
    Scout,          // 侦查员 - 环境侦查和信息收集
    Assault,        // 攻击手 - 主动攻击和突破  
    Defender,       // 防御者 - 区域防守和保护
    Transporter     // 运输者 - 物资运输和传递
}

/// <summary>
/// 通信模式枚举
/// </summary>
public enum CommunicationMode
{
    Centralized,    // 中心化通信 - 通过中心节点
    Decentralized,  // 去中心化通信 - 点对点直接通信
    Hybrid          // 混合模式 - 中心化分配+去中心化执行
}

/// <summary>
/// 消息类型
/// </summary>
public enum MessageType
{
    // 基础通信类型
    Heartbeat,          // 心跳检测
    StatusUpdate,       // 状态更新

    // 任务相关类型
    TaskAnnouncement,   // 任务公告
    TaskUpdate,         // 任务更新
    TaskCompletion,     // 任务完成
    TaskAbort,          // 任务中止
    SystemAlert,      // 系统警报

    // 资源协调类型  
    ResourceRequest,    // 资源请求
    ResourceOffer,      // 资源提供

    // 环境感知类型
    EnvironmentAlert,   // 环境警报
    ObstacleWarning,    // 障碍物警告

    // 协同操作类型
    Synchronization,    // 同步请求

    // 求助响应类型
    RequestHelp,        // 求助请求
    HelpRequest,        // 兼容旧链路保留；语义上表示对求助的响应/应答
    Response,           // 通用响应确认

    // 系统管理类型
    RoleAssignment,     // 角色分配
    MissionRequest,     // 任务请求
    RolePreference,     // 角色偏好
    RoleConfirmed      // 角色确认
}

/// <summary>
/// 原子动作类型
/// </summary>
public enum PrimitiveActionType
{
    Idle,            // 默认无动作   
    MoveTo,             // 移动到指定位置
    Stop,               // 停止移动
    TakeOff,            // 起飞（无人机）
    Land,               // 降落（无人机）
    Hover,              // 悬停（无人机）
    AdjustAltitude,    // 调整高度（无人机）
    RotateTo,           // 旋转到指定角度
    PickUp,             // 拾取物体
    Drop,               // 放下物体
    LookAt,             // 看向目标
    Scan,               // 扫描环境
    Wait,              // 等待伙伴
    Align,             // 站位对齐
    Follow,            // 跟随
    Orbit,             // 绕圈动作
    TransmitMessage     // 发送消息
}

/// <summary>
/// 智能体消息结构
/// </summary>
[System.Serializable]
public class AgentMessage
{
    public string SenderID;      // 发送者ID
    public string ReceiverID;    // 接收者ID ("All" 表示广播)
    public MessageType Type;     // 消息类型
    public int Priority;         // 优先级
    public float Timestamp;      // 时间戳
    public string Content;       // 消息内容 (JSON或自然语言)
}

/// <summary>
/// 原子动作命令
/// </summary>
[System.Serializable]
public class ActionCommand
{
    public PrimitiveActionType ActionType; // 动作类型
    public Vector3 TargetPosition;         // 目标位置
    public Quaternion TargetRotation;      // 目标旋转
    public GameObject TargetObject;        // 目标物体
    public string Parameters;              // 其他参数 (JSON字符串)

    // 构造函数，提供默认值
    public ActionCommand(
        PrimitiveActionType actionType = PrimitiveActionType.Idle, // 默认值为 Idle
        Vector3 targetPosition = default,
        Quaternion targetRotation = default,
        GameObject targetObject = null,
        string parameters = null)
        {
            ActionType = actionType;
            TargetPosition = targetPosition;
            TargetRotation = targetRotation;
            TargetObject = targetObject;
            Parameters = parameters;
        }
}
