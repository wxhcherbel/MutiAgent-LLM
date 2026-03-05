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
    Unknown = 0,
    Tree = 1,
    Pedestrian = 2,
    Vehicle = 3,
    ResourcePoint = 4,
    TemporaryObstacle = 5,
    Agent = 6,
    Custom = 99
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
/// 基础任务类型枚举。
/// 说明：
/// 1) 该枚举用于描述任务语义；
/// 2) 是否优先使用 A* 由 PlanningModule 中的“任务类型 -> 导航策略”映射决定；
/// 3) 最终是否启用 A* 仍需结合当前 step（例如通信/扫描 step 不走 A*）。
/// </summary>
public enum MissionType
{
    Cooperation,   // 协同合作 - 默认
    Competition,    // 对抗竞争 - 两队对抗
    Exploration,    // 探索侦查 - 环境探索
    Pursuit,        // 追击围捕 - 目标追踪
    Transport,      // 运输传递 - 物资传输
    SearchRescue    // 搜索救援 - 目标搜寻
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
    HelpRequest,       // 求助响应
    Response,           // 响应确认

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
