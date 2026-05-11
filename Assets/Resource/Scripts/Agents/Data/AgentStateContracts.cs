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

    /// <summary>电池容量上限。</summary>
    public float BatteryCapacity;

    /// <summary>通信半径。</summary>
    public float CommunicationRange;

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

    /// <summary>当前携带的物品名称，null 或空表示未携带。</summary>
    public string CarriedItemName;

    /// <summary>当前携带物品的场景对象引用（运行时）。</summary>
    [System.NonSerialized] public GameObject CarriedObject;

    /// <summary>当前运行状态。</summary>
    public AgentStatus Status;

    /// <summary>最近一次感知到的附近智能体映射。</summary>
    public Dictionary<string, GameObject> NearbyAgents = new();

    /// <summary>最近一次感知到的小节点列表。</summary>
    public List<SmallNodeData> DetectedSmallNodes = new();

    /// <summary>当前可用的校园二维逻辑网格引用。</summary>
    public CampusGrid2D CampusGrid;
}
