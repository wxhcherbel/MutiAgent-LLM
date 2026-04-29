/// <summary>
/// ADM 原子动作类型。
/// </summary>
public enum AtomicActionType
{
    MoveTo,
    Wait,
    Track,
    Signal,
    Get,
    Put,
    Land,
    Takeoff,
    /// <summary>targetName=区域名（空=执行层自动选区），duration=覆盖时长(秒)</summary>
    Patrol,
    /// <summary>逼近目标agent至极近距离进行物理干扰，targetAgentId=目标ID，duration=持续时间(秒)</summary>
    Approach,
    /// <summary>远离威胁agent，targetAgentId=威胁ID，duration=逃离持续时间(秒)</summary>
    Flee,
}

/// <summary>
/// ActionDecisionModule 运行状态。
/// </summary>
public enum ADMStatus
{
    Idle,
    Interpreting,
    Running,
    Interrupted,
    Replanning,
    BatchDone, // 当前批次动作全部完成，等待 RunRollingLoop 处理下一轮（非步骤完成）
    Done,
    Failed,
}

/// <summary>
/// PlanningModule 状态机阶段。
/// </summary>
public enum PlanningState
{
    Idle = 0,
    Parsing = 1,
    Grouping = 2,
    SlotGen = 3,
    SlotPick = 4,
    StepGen = 5,
    Active = 6,
    Done = 7,
    Failed = 8
}
