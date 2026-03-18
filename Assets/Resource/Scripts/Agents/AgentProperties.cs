using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 智能体静态属性。
/// </summary>
[System.Serializable]
public class AgentProperties
{
    [Header("Basic")]
    public string AgentID;
    public AgentType Type;
    public RoleType Role;

    [Header("Team")]
    public int TeamID;

    [Header("Mobility")]
    public float MaxSpeed;
    public float MaxAngularSpeed;
    public float Acceleration;

    [Header("Physical")]
    public float BatteryCapacity;
    public float CommunicationRange;
    public float PayloadCapacity;
    public Vector3 PhysicalSize;

    [Header("Perception")]
    public float PerceptionRange;
}


/// <summary>
/// 智能体动态状态。
/// </summary>
[System.Serializable]
public class AgentDynamicState
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
    public float BatteryLevel;
    public float CurrentLoad;
    public AgentStatus Status;
    public string CurrentTaskID;
    public string CurrentScenarioId;
    public string CurrentMissionId;
    public string CurrentAssignmentId;
    public Dictionary<RoleType, int> remainingCount;
    public Dictionary<string, GameObject> NearbyAgents = new();
    public List<SmallNodeData> DetectedSmallNodes = new();
    public CampusGrid2D CampusGrid;
}
