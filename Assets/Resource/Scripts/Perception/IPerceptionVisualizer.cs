// Other_Modules/IPerceptionVisualizer.cs
// 感知可视化接口：PerceptionModule 不依赖它，PerceptionVisualizer 或其它显示实现可按需使用。
using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>单次感知周期中的普通检测点快照。</summary>
[Serializable]
public struct DetectionPointSnapshot
{
    public Vector3 position;
    public SmallNodeType type;
    public float timestamp;
    public int rayIndex;
}

/// <summary>单次感知周期中的敌方检测快照。</summary>
[Serializable]
public struct EnemyDetectionSnapshot
{
    public string agentId;
    public Vector3 position;
    public float timestamp;
}

/// <summary>单次感知周期中的事件快照。</summary>
[Serializable]
public struct PerceptionAlertSnapshot
{
    public string description;
    public Vector3 position;
    public float timestamp;
}

/// <summary>
/// 传给可视化器的一整帧感知数据。
/// 这里是纯数据合同，不包含任何渲染逻辑。
/// </summary>
[Serializable]
public class PerceptionVisualizationFrame
{
    public Vector3 agentPosition;
    public Vector3 agentForward;
    public float perceptionRange;
    public AgentType agentType;
    public float groundHorizontalAngle;
    public List<DetectionPointSnapshot> detectionPoints = new();
    public List<EnemyDetectionSnapshot> enemyDetections = new();
    public List<PerceptionAlertSnapshot> alerts = new();
}

/// <summary>
/// 单个小节点的共享感知记录。
/// </summary>
[System.Serializable]
public class SmallNodeData
{
    /// <summary>节点的稳定标识，用于去重和同步。</summary>
    public string NodeId;

    /// <summary>节点的语义类型，例如树木、资源点或临时障碍。</summary>
    public SmallNodeType NodeType;

    /// <summary>节点在世界坐标中的位置。</summary>
    public Vector3 WorldPosition;

    /// <summary>节点是否会移动，用于区分静态场景与动态目标。</summary>
    public bool IsDynamic;

    /// <summary>最近一次被观测到的时间。</summary>
    public float LastSeenTime;

    /// <summary>场景中的实际对象引用，仅在本地运行时有效。</summary>
    public GameObject SceneObject;
}


/// <summary>
/// 感知可视化接口。
/// 这里只描述“如何渲染一帧感知结果”，不参与感知业务逻辑。
/// </summary>
public interface IPerceptionVisualizer
{
    /// <summary>提交一整帧感知数据，由可视化器自行决定如何渲染。</summary>
    void RenderFrame(PerceptionVisualizationFrame frame);

    /// <summary>清空当前感知可视化。</summary>
    void ClearFrame();
}