using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 智能体静态属性。
/// </summary>
[System.Serializable]
public class AgentProperties
{
    /// <summary>智能体唯一 ID。</summary>
    public string AgentID;

    /// <summary>平台类型，例如四旋翼或地面机器人。</summary>
    public AgentType Type;

    /// <summary>默认战术角色。</summary>
    public RoleType Role;

    /// <summary>所属队伍编号。</summary>
    public int TeamID;

    /// <summary>最大平移速度。</summary>
    public float MaxSpeed;

    /// <summary>最大角速度。</summary>
    public float MaxAngularSpeed;

    /// <summary>平移加速度。</summary>
    public float Acceleration;

    /// <summary>电池容量上限。</summary>
    public float BatteryCapacity;

    /// <summary>通信半径。</summary>
    public float CommunicationRange;

    /// <summary>载重能力。</summary>
    public float PayloadCapacity;

    /// <summary>物理包围尺寸。</summary>
    public Vector3 PhysicalSize;

    /// <summary>感知半径。</summary>
    public float PerceptionRange;
}

/// <summary>
/// 智能体动态状态。
/// </summary>
[System.Serializable]
public class AgentDynamicState
{
    /// <summary>当前世界坐标。</summary>
    public Vector3 Position;

    /// <summary>当前朝向旋转。</summary>
    public Quaternion Rotation;

    /// <summary>当前速度向量。</summary>
    public Vector3 Velocity;

    /// <summary>当前剩余电量。</summary>
    public float BatteryLevel;

    /// <summary>当前载荷重量。</summary>
    public float CurrentLoad;

    /// <summary>当前运行状态。</summary>
    public AgentStatus Status;

    /// <summary>最近一次感知到的附近智能体映射。</summary>
    public Dictionary<string, GameObject> NearbyAgents = new();

    /// <summary>最近一次感知到的小节点列表。</summary>
    public List<SmallNodeData> DetectedSmallNodes = new();

    /// <summary>当前可用的校园二维逻辑网格引用。</summary>
    public CampusGrid2D CampusGrid;
}
