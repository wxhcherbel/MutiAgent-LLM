using UnityEngine;
using System.Collections;
using System.Collections.Generic;
// using System.Diagnostics;

/// <summary>
/// 智能体主控制器。
/// 现在它除了“有任务就执行”之外，还承担自治触发门的职责：
/// - 如果已经有活跃任务，就驱动 ActionDecisionModule 继续执行；
/// - 如果当前空闲但自治目标收件箱非空，就先唤起 PlanningModule 发起新一轮规划。
/// </summary>
public class IntelligentAgent : MonoBehaviour
{
    [Header("属性配置")]
    public AgentProperties Properties;      // 静态属性
    public AgentDynamicState CurrentState;  // 动态状态

    [Header("子系统")]
    public CommunicationModule CommModule;  // 通信模块
    public PerceptionModule PerceptionModule; // 感知模块
    public AgentLLMControl LLMControl;      // LLM控制模块
    public ActionDecisionModule ActionDecisionModule; // 动作决策模块
    public PlanningModule PlanningModule;   // 规划模块
    // public MapGenerator MapGenerator;     // 旧地图生成器（已弃用，保留注释用于迁移对照）
    public CampusJsonMapLoader CampusJsonMapLoader; // 校园 JSON 地图加载器
    public CampusGrid2D CampusGrid2D;              // 校园二维逻辑网格（当前使用）

    // 决策状态
    private bool isMakingDecision = false;  // 是否正在决策中
    private float decisionCheckInterval = 2.0f; // 决策检查间隔
    private float lastDecisionTime = 0f;    // 上次决策时间

    void Start()
    {
        InitializeAgent();
    }

    void Update()
    {
        UpdateState();
        CheckForDecision();
    }

    /// <summary>
    /// 初始化智能体
    /// </summary>
    private void InitializeAgent()
    {
        // 获取所有必要的模块
        // 检查并获取必要的模块组件，如果不存在则添加
        CommModule = GetComponent<CommunicationModule>();
        if (CommModule == null) CommModule = gameObject.AddComponent<CommunicationModule>();
        
        PerceptionModule = GetComponent<PerceptionModule>();
        if (PerceptionModule == null) PerceptionModule = gameObject.AddComponent<PerceptionModule>();
        
        LLMControl = GetComponent<AgentLLMControl>();
        if (LLMControl == null) LLMControl = gameObject.AddComponent<AgentLLMControl>();
        
        ActionDecisionModule = GetComponent<ActionDecisionModule>();
        if (ActionDecisionModule == null) ActionDecisionModule = gameObject.AddComponent<ActionDecisionModule>();
        
        PlanningModule = GetComponent<PlanningModule>();
        if (PlanningModule == null) PlanningModule = gameObject.AddComponent<PlanningModule>();

        // ===== 旧网格系统（MapGenerator）已弃用 =====
        // MapGenerator = FindObjectOfType<MapGenerator>();
        // if (MapGenerator == null)
        // {
        //     Debug.LogWarning("【IntelligentAgent】 MapGenerator 未找到");
        // }
        // Debug.Log($"MapWidth: {MapGenerator.mapWidth}, MapLength: {MapGenerator.mapLength}");
        // ===== 新网格系统：CampusJsonMapLoader + CampusGrid2D =====
        if (CampusJsonMapLoader == null) CampusJsonMapLoader = FindObjectOfType<CampusJsonMapLoader>();
        if (CampusGrid2D == null && CampusJsonMapLoader != null) CampusGrid2D = CampusJsonMapLoader.GetComponent<CampusGrid2D>();
        if (CampusGrid2D == null) CampusGrid2D = FindObjectOfType<CampusGrid2D>();

        if (CampusJsonMapLoader == null)
        {
            Debug.LogWarning("【IntelligentAgent】 CampusJsonMapLoader 未找到");
        }

        if (CampusGrid2D == null)
        {
            Debug.LogWarning("【IntelligentAgent】 CampusGrid2D 未找到，将无法使用校园网格信息");
        }
        else
        {
            // 如果网格尚未构建，启动时尝试自动构建一次
            if (CampusGrid2D.blockedGrid == null || CampusGrid2D.cellTypeGrid == null)
            {
                CampusGrid2D.BuildGridFromCampusJson();
            }

            Debug.Log($"GridWidth: {CampusGrid2D.gridWidth}, GridLength: {CampusGrid2D.gridLength}");
        }

        // 初始化状态
        CurrentState = new AgentDynamicState
        {
            Position = transform.position,
            Rotation = transform.rotation,
            BatteryLevel = Properties.BatteryCapacity,
            Status = AgentStatus.Idle,
            CampusGrid = CampusGrid2D,
        };
        // 旧感知网格 PerceivedGrid 已弃用，改用 CurrentState.CampusGrid（CampusGrid2D）
        // 初始化各子系统
        CommModule?.Initialize();
        //PerceptionModule?.Initialize();
        LLMControl?.RecordEvent("Agent initialized", "system", 1.0f);
        
        // 启动决策检查
        lastDecisionTime = Time.time;
        //PrintPerceivedGrid();
        Debug.Log($"智能体 {Properties.AgentID} 初始化完成，类型: {Properties.Type}, 角色: {Properties.Role}");
    }

    /// <summary>
    /// 更新智能体状态
    /// </summary>
    private void UpdateState()
    {
        CurrentState.Position = transform.position;
        CurrentState.Rotation = transform.rotation;
        
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            CurrentState.Velocity = rb.velocity;
        }
        
        // 模拟电量消耗（根据状态不同消耗速率不同）
        float powerConsumption = CurrentState.Status == AgentStatus.ExecutingTask ? 0.02f : 0.005f;
        CurrentState.BatteryLevel -= Time.deltaTime * powerConsumption;
        
        if (CurrentState.BatteryLevel <= 0)
        {
            HandleBatteryDepletion();
        }
    }

    /// <summary>
    /// 检查是否需要决策
    /// </summary>
    private void CheckForDecision()
    {
        // 如果正在决策中，不进行新的决策
        if (isMakingDecision)
            return;

        // 按时间间隔检查决策
        if (Time.time - lastDecisionTime >= decisionCheckInterval)
        {
            if (ShouldMakeDecision())
            {
                StartCoroutine(MakeDecisionCoroutine());
            }
            lastDecisionTime = Time.time;
        }
    }

    /// <summary>
    /// 判断是否需要决策的条件
    /// </summary>
    private bool ShouldMakeDecision()
    {
        // 条件1: 有活跃任务需要执行下一步
        if (PlanningModule != null && PlanningModule.HasActiveMission() && CurrentState.Status == AgentStatus.Idle)
        {
            Debug.Log($"【IntelligentAgent】 智能体 {Properties.AgentID} 需要决策：有活跃任务");
            return true;
        }

        // // 条件2: 当前状态为闲置
        // if (CurrentState.Status == AgentStatus.Idle)
        // {
        //     return true;
        // }

        // // 条件3: 接收到新消息需要处理
        // if (CommModule != null && CommModule.HasUnreadMessages())
        // {
        //     return true;
        // }

        // // 条件4: 感知到新环境变化
        // if (PerceptionModule != null && PerceptionModule.HasNewPerceptions())
        // {
        //     return true;
        // }

        return false;
    }

    /// <summary>
    /// 决策协程 - 主要入口
    /// </summary>
    private IEnumerator MakeDecisionCoroutine()
    {
        Debug.Log($"【IntelligentAgent】智能体 {Properties.AgentID} 进入决策协程 MakeDecisionCoroutine()");
        isMakingDecision = true;
        CurrentState.Status = AgentStatus.Thinking;
        try
        {
            // 使用 ActionDecisionModule 进行高级决策
            if (ActionDecisionModule != null)
            {
                yield return StartCoroutine(ActionDecisionModule.DecideNextAction());
            }
            else
            {
                Debug.LogWarning("ActionDecisionModule 未找到，使用备用决策");
                yield return StartCoroutine(DecideNextActionFallback());
            }
        }
        finally
        {
            isMakingDecision = false;
            // 注意：状态会在 ActionDecisionModule 执行动作后更新
        }
    }

    /// <summary>
    /// 备用决策逻辑（当 ActionDecisionModule 不可用时）
    /// </summary>
    private IEnumerator DecideNextActionFallback()
    {
        // 简单的基于状态的决策
        if (PlanningModule != null && PlanningModule.HasActiveMission())
        {
            // 有任务但无法使用决策模块，报告问题
            Debug.LogWarning($"智能体 {Properties.AgentID} 有任务但无法决策");
            
            if (CommModule != null)
            {
                AgentMessage errorMsg = new AgentMessage
                {
                    SenderID = Properties.AgentID,
                    ReceiverID = "All",
                    Type = MessageType.SystemAlert,
                    Priority = 2,
                    Content = "{\"error\":\"decision_module_unavailable\",\"agent\":\"" + Properties.AgentID + "\"}"
                };
                CommModule.SendMessage(errorMsg);
            }
        }
        else
        {
            // 没有任务，更新状态为闲置
            CurrentState.Status = AgentStatus.Idle;
        }
        
        yield return null;
    }

    /// <summary>
    /// 处理电量耗尽
    /// </summary>
    private void HandleBatteryDepletion()
    {
        CurrentState.Status = AgentStatus.Error;
        CurrentState.BatteryLevel = 0;

        Debug.LogError($"智能体 {Properties.AgentID} 电量耗尽！");

        // 发送紧急求助消息
        if (CommModule != null)
        {
            AgentMessage helpMessage = new AgentMessage
            {
                SenderID = Properties.AgentID,
                ReceiverID = "All",
                Type = MessageType.RequestHelp,
                Priority = 10, // 最高优先级
                Timestamp = Time.time,
                Content = $"{{\"type\":\"battery_exhausted\",\"position\":\"{CurrentState.Position}\",\"agent\":\"{Properties.AgentID}\"}}"
            };

            CommModule.SendMessage(helpMessage);
        }
    }

    /// <summary>
    /// 外部触发立即决策（例如接收到新任务时调用）
    /// </summary>
    public void TriggerImmediateDecision()
    {
        if (!isMakingDecision)
        {
            Debug.Log($"智能体 {Properties.AgentID} 接收到立即决策触发");
            StartCoroutine(MakeDecisionCoroutine());
        }
    }

    /// <summary>
    /// 接收任务分配（由 PlanningModule 调用）
    /// </summary>
    public void OnMissionAssigned(MissionAssignment mission)
    {
        Debug.Log($"智能体 {Properties.AgentID} 接收到任务分配: {mission.missionDescription}");
        
        // 立即触发决策来处理新任务
        TriggerImmediateDecision();
    }

    /// <summary>
    /// 接收消息处理（由 CommunicationModule 调用）
    /// </summary>
    public void OnMessageReceived(AgentMessage message)
    {
        Debug.Log($"智能体 {Properties.AgentID} 接收到消息: {message.Type} from {message.SenderID}");

        // 根据消息类型决定是否立即决策
        switch (message.Type)
        {
            case MessageType.TaskAnnouncement:
            case MessageType.RoleAssignment:
            case MessageType.HelpRequest:
            case MessageType.ObstacleWarning:
                // 高优先级消息，立即决策
                TriggerImmediateDecision();
                break;
                
            case MessageType.StatusUpdate:
            case MessageType.TaskUpdate:
                // 普通消息，等待下次决策周期处理
                break;
        }
    }

    /// <summary>
    /// 获取智能体状态摘要
    /// </summary>
    public string GetStatusSummary()
    {
        return $"ID: {Properties.AgentID}, " +
               $"状态: {CurrentState.Status}, " +
               $"电量: {CurrentState.BatteryLevel:F1}/{Properties.BatteryCapacity}, " +
               $"位置: {CurrentState.Position}, " +
               $"决策中: {isMakingDecision}";
    }
}
