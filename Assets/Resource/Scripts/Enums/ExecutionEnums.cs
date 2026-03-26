/// <summary>
/// ADM 原子动作类型。
/// </summary>
public enum AtomicActionType
{
    MoveTo,
    PatrolAround,
    Observe,
    Wait,
    FormationHold,
    Evade,
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
