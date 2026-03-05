using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 智能体静态属性（初始化后不变）
/// </summary>
[System.Serializable]
public class AgentProperties
{
    [Header("基础属性")]
    public string AgentID;                  // 唯一标识符
    public AgentType Type;                  // 智能体类型
    public RoleType Role;                   // 角色 (e.g., "Scout", "Assault")

    [Header("移动能力")]
    public float MaxSpeed;                  // 最大移动速度
    public float MaxAngularSpeed;           // 最大旋转速度
    public float Acceleration;              // 加速度

    [Header("物理属性")]
    public float BatteryCapacity;           // 电池总容量
    public float CommunicationRange;        // 最大通信距离
    public float PayloadCapacity;           // 最大负载能力
    public Vector3 PhysicalSize;            // 碰撞体尺寸

    [Header("感知能力")]
    public float PerceptionRange;        // 默认传感器感知范围
}

/// <summary>
/// 智能体动态状态（随时间变化）
/// </summary>
[System.Serializable]
public class AgentDynamicState
{
    public Vector3 Position;                // 世界坐标位置
    public Quaternion Rotation;             // 旋转
    public Vector3 Velocity;                // 当前速度
    public float BatteryLevel;              // 当前电量
    public float CurrentLoad;               // 当前负载
    public AgentStatus Status;              // 当前状态
    public string CurrentTaskID;            // 当前任务ID
    public int TeamID;                      // 所属队伍
    //作为协调者的时候得到的任务分配记录
    public Dictionary<RoleType, int> remainingCount;

    // 当前感知周期内附近智能体（按 AgentID 索引）
    public Dictionary<string, GameObject> NearbyAgents = new Dictionary<string, GameObject>();

    // 当前感知周期内检测到的小节点（去重后的线性列表，便于遍历）
    public List<SmallNodeData> DetectedSmallNodes = new List<SmallNodeData>();

    // 当前感知周期内检测到的小节点（按 NodeId 索引，便于快速查询）
    public Dictionary<string, SmallNodeData> NearbySmallNodes = new Dictionary<string, SmallNodeData>();

    // 当前使用的校园二维逻辑网格（新网格系统）
    public CampusGrid2D CampusGrid;
}


