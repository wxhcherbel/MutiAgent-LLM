using UnityEngine;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;

/// <summary>
/// 智能体通信模块 - 负责消息收发和处理
/// </summary>
public class CommunicationModule : MonoBehaviour
{
    private IntelligentAgent agent;                      // 所属智能体
    private Queue<AgentMessage> incomingMessages = new Queue<AgentMessage>(); // 接收消息队列
    private float lastCommunicationTime;                 // 上次通信时间
    
    [Header("通信设置")]
    [SerializeField] private float messageProcessInterval = 0.1f; // 消息处理间隔（秒）
    [SerializeField] private bool enablePeriodicProcessing = true; // 是否启用周期性处理
    private float timeSinceLastProcess;                  // 距离上次处理的时间

    public PlanningModule planningModule;              // 关联的规划模块

    void Start()
    {
        agent = GetComponent<IntelligentAgent>();
        lastCommunicationTime = Time.time;
        timeSinceLastProcess = 0f;
        
        // 注册到通信管理器
        if (CommunicationManager.Instance != null)
        {
            CommunicationManager.Instance.RegisterAgent(this);
        }
        else
        {
            Debug.LogError("CommunicationManager实例未找到！");
        }
        planningModule = GetComponent<PlanningModule>();
    }
    public void Initialize()
    {

    }
    
    // ==================== 对外接口方法 ====================

    /// <summary>
    /// 发送消息 - 供决策模块调用
    /// </summary>
    /// <param name="receiverID">接收者ID ("All"表示广播)</param>
    /// <param name="messageType">消息类型</param>
    /// <param name="content">消息内容</param>
    /// <param name="priority">优先级</param>
    public void SendMessage(string receiverID, MessageType messageType, string content, int priority = 1)
    {
        AgentMessage message = new AgentMessage
        {
            SenderID = agent.Properties.AgentID,
            ReceiverID = receiverID,
            Type = messageType,
            Priority = priority,
            Timestamp = Time.time,
            Content = content
        };

        SendMessage(message);
    }

    /// <summary>
    /// 发送消息对象 - 供决策模块调用
    /// </summary>
    public void SendMessage(AgentMessage message)
    {
        message.SenderID = agent.Properties.AgentID;
        message.Timestamp = Time.time;
        
        if (CommunicationManager.Instance != null)
        {
            CommunicationManager.Instance.ProcessMessage(message);
            Debug.Log($"{agent.Properties.AgentID} 发送消息: {message.Type} -> {message.ReceiverID}");
        }
        else
        {
            Debug.LogError("CommunicationManager实例未找到，消息发送失败！");
        }
    }

    /// <summary>
    /// 检查是否有未读消息 - 供决策模块调用
    /// </summary>
    public bool HasUnreadMessages()
    {
        return incomingMessages.Count > 0;
    }

    /// <summary>
    /// 获取所有未读消息 - 供决策模块调用
    /// </summary>
    public AgentMessage[] GetUnreadMessages()
    {
        return incomingMessages.ToArray();
    }

    /// <summary>
    /// 获取特定类型的未读消息 - 供决策模块调用
    /// </summary>
    public AgentMessage[] GetMessagesByType(MessageType messageType)
    {
        List<AgentMessage> result = new List<AgentMessage>();
        foreach (AgentMessage message in incomingMessages)
        {
            if (message.Type == messageType)
            {
                result.Add(message);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// 清空消息队列 - 供决策模块调用
    /// </summary>
    public void ClearMessageQueue()
    {
        incomingMessages.Clear();
    }

    /// <summary>
    /// 获取通信状态摘要 - 供决策模块调用
    /// </summary>
    public string GetCommunicationSummary()
    {
        return $"待处理消息: {incomingMessages.Count}条, 通信范围: {agent.Properties.CommunicationRange}m";
    }

    /// <summary>
    /// 获取详细的通信信息 - 供决策模块调用
    /// </summary>
    public string GetDetailedCommunicationInfo()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine($"通信状态 (范围: {agent.Properties.CommunicationRange}m)");
        sb.AppendLine($"待处理消息: {incomingMessages.Count}");

        // 按消息类型统计
        Dictionary<MessageType, int> messageCounts = new Dictionary<MessageType, int>();
        foreach (AgentMessage message in incomingMessages)
        {
            if (!messageCounts.ContainsKey(message.Type))
            {
                messageCounts[message.Type] = 0;
            }
            messageCounts[message.Type]++;
        }

        foreach (var kvp in messageCounts)
        {
            sb.AppendLine($"  - {kvp.Key}: {kvp.Value}条");
        }

        return sb.ToString();
    }

    // ==================== 内部方法 ====================

    /// <summary>
    /// 接收消息 - 内部使用
    /// </summary>
    public void ReceiveMessage(AgentMessage message)
    {
        // 简单的消息过滤
        if (message.ReceiverID == agent.Properties.AgentID || message.ReceiverID == "All")
        {
            incomingMessages.Enqueue(message);
            Debug.Log($"{agent.Properties.AgentID} 收到消息: {message.Type} from {message.SenderID}");
        }
    }

    /// <summary>
    /// 处理接收到的消息 - 内部使用
    /// </summary>
    public void ProcessMessages()
    {
        while (incomingMessages.Count > 0)
        {
            AgentMessage message = incomingMessages.Dequeue();
            HandleMessage(message);
        }
    }

    /// <summary>
    /// 处理单个消息 - 内部使用
    /// </summary>
    private void HandleMessage(AgentMessage message)
    {
        switch (message.Type)
        {
            case MessageType.EnvironmentAlert:
                HandleEnvironmentAlert(message);
                break;
            case MessageType.HelpRequest:
            case MessageType.RequestHelp:
                HandleHelpRequest(message);
                break;
            case MessageType.TaskUpdate:
            case MessageType.TaskAnnouncement:
                HandleTaskUpdate(message);
                break;
            case MessageType.ResourceRequest:
                HandleResourceRequest(message);
                break;
            case MessageType.ObstacleWarning:
                HandleObstacleWarning(message);
                break;
            case MessageType.RolePreference:
                HandleRolePreference(message);
                break;
            case MessageType.RoleConfirmed:
                HandleRoleConfirmed(message);
                break;
            default:
                Debug.Log($"{agent.Properties.AgentID} 处理消息: {message.Type} - {message.Content}");
                break;
        }
        
        // 将消息存入记忆（如果记忆模块存在）
        // agent.Memory.StoreMessage(message);
    }

    /// <summary>
    /// 处理环境警报
    /// </summary>
    private void HandleEnvironmentAlert(AgentMessage message)
    {
        Debug.Log($"{agent.Properties.AgentID} 收到环境警报: {message.Content}");
        // 可以在这里更新环境认知或触发相应行为
    }

    /// <summary>
    /// 处理求助请求
    /// </summary>
    private void HandleHelpRequest(AgentMessage message)
    {
        Debug.Log($"{agent.Properties.AgentID} 收到求助请求 from {message.SenderID}: {message.Content}");
        
        // 根据自身状态决定是否响应求助
        if (agent.CurrentState.Status == AgentStatus.Idle)
        {
            // 发送响应消息
            SendMessage(message.SenderID, MessageType.Response, "{\"response\":\"acknowledged\",\"action\":\"coming_to_help\"}", 2);
        }
    }

    /// <summary>
    /// 处理任务更新
    /// </summary>
    private void HandleTaskUpdate(AgentMessage message)
    {
        try
        {
            // 1. 解析 JSON 消息内容
            MissionAssignment mission = JsonUtility.FromJson<MissionAssignment>(message.Content);
            
            if (mission == null)
            {
                Debug.LogError($"{agent.Properties.AgentID} 无法解析任务消息: {message.Content}");
                return;
            }
            
            // 2. 简单检查是否应该参与（基本条件）
            if (ShouldAcceptMission(mission))
            {
                // 3. 直接传递给规划模块，role参数为null
                if (planningModule != null)
                {
                    planningModule.ReceiveMissionAssignment(mission, null);
                    //Debug.Log($"{agent.Properties.AgentID} 接受任务: {mission.missionDescription}");
                }
                else
                {
                    Debug.LogError($"{agent.Properties.AgentID} 规划模块未找到");
                }
            }
            else
            {
                Debug.Log($"{agent.Properties.AgentID} 决定不参与任务: {mission.missionDescription}");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"{agent.Properties.AgentID} 处理任务消息时出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 简单检查是否应该接受任务
    /// </summary>
    private bool ShouldAcceptMission(MissionAssignment mission)
    {
        
        return true; // 默认接受
    }

    /// <summary>
    /// 处理资源请求
    /// </summary>
    private void HandleResourceRequest(AgentMessage message)
    {
        Debug.Log($"{agent.Properties.AgentID} 收到资源请求: {message.Content}");
        // 根据自身资源情况决定是否提供帮助
    }

    /// <summary>
    /// 处理障碍物警告
    /// </summary>
    private void HandleObstacleWarning(AgentMessage message)
    {
        Debug.Log($"{agent.Properties.AgentID} 收到障碍物警告: {message.Content}");
        // 更新内部地图信息，避免前往危险区域
    }

    /// <summary>
    /// 处理角色偏好消息    
    /// </summary>
    private void HandleRolePreference(AgentMessage message)
    {
        Debug.Log($"{agent.Properties.AgentID} 收到角色偏好消息: {message.Content}");
        // 解析角色偏好并存储或处理
        if (message.Type == MessageType.RolePreference)
        {
            var pref = JsonUtility.FromJson<RolePreferenceWrapper>(message.Content);
            planningModule.receivedPreferences[message.SenderID] = pref.preferences;
        }
    }

    private void HandleRoleConfirmed(AgentMessage message)
    {
        Debug.Log($"{agent.Properties.AgentID} 收到角色确认消息: {message.Content}");
        RoleType finalRole = System.Enum.Parse<RoleType>(message.Content);
        StartCoroutine(planningModule.AnalyzeMissionAndCreatePlan(planningModule.currentMission, finalRole));
    }

    void Update()
    {
        // 处理接收到的消息
        ProcessMessages();
    }

    void OnDestroy()
    {
        // 从通信管理器注销
        if (CommunicationManager.Instance != null)
        {
            CommunicationManager.Instance.UnregisterAgent(this);
        }
    }
}