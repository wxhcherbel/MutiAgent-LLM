// Other_Modules/IPerceptionVisualizer.cs
// 感知可视化接口，将感知逻辑与渲染解耦。
using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>单次感知周期中检测到的点快照（用于传递给可视化器）。</summary>
[Serializable]
public struct DetectionPointSnapshot
{
    public Vector3      position;
    public SmallNodeType type;
    public float        timestamp;
}

/// <summary>
/// 感知可视化接口。
/// PerceptionModule 通过此接口通知可视化器，可视化器负责所有渲染工作。
/// </summary>
public interface IPerceptionVisualizer
{
    /// <summary>感知周期更新时调用，传入本周期所有检测点。</summary>
    void OnDetectionUpdated(List<DetectionPointSnapshot> points, Vector3 agentPos, float range, AgentType agentType);

    /// <summary>检测到敌方智能体时调用。</summary>
    void OnEnemyDetected(IntelligentAgent enemy, Vector3 pos);

    /// <summary>检测到紧急事件（如 TemporaryObstacle）时调用。</summary>
    void OnEmergencyDetected(string eventDesc, Vector3 pos);

    /// <summary>全局开关：启用/禁用所有可视化。</summary>
    void SetEnabled(bool enabled);
}
