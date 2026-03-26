/// <summary>
/// 智能体平台类型。
/// </summary>
public enum AgentType
{
    Quadcopter,
    WheeledRobot,
}

/// <summary>
/// 智能体运行状态。
/// </summary>
public enum AgentStatus
{
    Idle,
    Moving,
    Thinking,
    ExecutingTask,
    Charging,
    Error
}

/// <summary>
/// 任务执行角色类型。
/// </summary>
public enum RoleType
{
    Supporter,
    Scout,
    Assault,
    Defender,
    Transporter
}
