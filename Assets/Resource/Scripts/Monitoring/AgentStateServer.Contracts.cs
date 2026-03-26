using System;

/// <summary>
/// 单个智能体的仪表板快照。
/// </summary>
[Serializable]
public class AgentStateSnapshot
{
    /// <summary>智能体 ID。</summary>
    public string agentId;

    /// <summary>平台类型文本。</summary>
    public string type;

    /// <summary>角色文本。</summary>
    public string role;

    /// <summary>队伍编号。</summary>
    public int teamId;

    /// <summary>世界坐标数组，格式为 [x, y, z]。</summary>
    public float[] position;

    /// <summary>当前电量。</summary>
    public float battery;

    /// <summary>ADM 状态文本。</summary>
    public string admStatus;

    /// <summary>Planning 状态文本。</summary>
    public string planState;

    /// <summary>当前动作类型文本。</summary>
    public string currentAction;

    /// <summary>当前动作目标文本。</summary>
    public string currentTarget;

    /// <summary>当前任务描述。</summary>
    public string missionDesc;

    /// <summary>当前步骤文本。</summary>
    public string currentStep;

    /// <summary>最近感知事件摘要。</summary>
    public string[] recentEvents;

    /// <summary>附近友方智能体 ID 列表。</summary>
    public string[] nearbyAgentIds;

    /// <summary>附近敌方智能体 ID 列表。</summary>
    public string[] enemyAgentIds;

    /// <summary>感知半径。</summary>
    public float perceptionRange;

    /// <summary>快照采样时间。</summary>
    public float timestamp;
}

/// <summary>
/// 仪表板使用的地图元数据。
/// </summary>
[Serializable]
public class MapMetadata
{
    /// <summary>地图原点 X 坐标。</summary>
    public float originX;

    /// <summary>地图原点 Z 坐标。</summary>
    public float originZ;

    /// <summary>网格单元边长。</summary>
    public float cellSize;

    /// <summary>网格宽度。</summary>
    public int gridWidth;

    /// <summary>网格长度。</summary>
    public int gridLength;

    /// <summary>地图要素点位列表。</summary>
    public FeaturePoint[] features;
}

/// <summary>
/// 仪表板展示的地图要素抽样点。
/// </summary>
[Serializable]
public class FeaturePoint
{
    /// <summary>要素名称。</summary>
    public string name;

    /// <summary>要素类型文本。</summary>
    public string kind;

    /// <summary>要素中心点 X 坐标。</summary>
    public float x;

    /// <summary>要素中心点 Z 坐标。</summary>
    public float z;

    /// <summary>要素估计半径。</summary>
    public float radius;
}

/// <summary>
/// 通信日志条目。
/// </summary>
[Serializable]
public class MessageLogEntry
{
    /// <summary>发送者 ID。</summary>
    public string sender;

    /// <summary>接收者 ID。</summary>
    public string receiver;

    /// <summary>消息类型文本。</summary>
    public string type;

    /// <summary>消息时间戳。</summary>
    public float timestamp;

    /// <summary>消息内容摘要。</summary>
    public string content;
}

/// <summary>
/// LLM 调用日志条目。
/// </summary>
[Serializable]
public class LlmLogEntry
{
    /// <summary>发起该调用的智能体 ID。</summary>
    public string agentId;

    /// <summary>日志时间字符串。</summary>
    public string timestamp;

    /// <summary>日志类型。</summary>
    public string type;

    /// <summary>模型名。</summary>
    public string model;

    /// <summary>温度参数。</summary>
    public float temperature;

    /// <summary>最大 token 设置。</summary>
    public int maxTokens;

    /// <summary>日志内容摘要。</summary>
    public string content;
}
