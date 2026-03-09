using UnityEngine;
using System;
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
    /// 发送结构化消息。
    /// 调用方传入结构化载荷对象，通信模块统一完成 JSON 序列化。
    /// </summary>
    public void SendStructuredMessage<TPayload>(string receiverID, MessageType messageType, TPayload payload, int priority = 1)
    {
        string content = payload != null ? JsonUtility.ToJson(payload) : "{}";
        SendMessage(receiverID, messageType, content, priority);
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
            case MessageType.TaskAnnouncement:
                HandleTaskAnnouncement(message);
                break;
            case MessageType.TaskUpdate:
                HandleTaskUpdate(message);
                break;
            case MessageType.TaskCompletion:
                HandleTaskCompletion(message);
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
            case MessageType.RoleAssignment:
                HandleRoleAssignment(message);
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
    private void HandleTaskAnnouncement(AgentMessage message)
    {
        try
        {
            MissionAssignment mission = null;

            if (TryParseMessageContent<TaskAnnouncementPayload>(message, out TaskAnnouncementPayload taskPayload) &&
                taskPayload != null &&
                taskPayload.mission != null)
            {
                mission = taskPayload.mission;
                if (taskPayload.missionDirectives != null && taskPayload.missionDirectives.Length > 0)
                {
                    mission.coordinationDirectives = taskPayload.missionDirectives;
                }
            }
            else
            {
                mission = JsonUtility.FromJson<MissionAssignment>(message.Content);
            }
             
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
    /// 尝试把消息内容解析成结构化载荷。
    /// 若解析失败，调用方可以继续回退到旧版裸字符串兼容逻辑。
    /// </summary>
    public bool TryParseMessageContent<TPayload>(AgentMessage message, out TPayload payload)
    {
        payload = default(TPayload);
        if (message == null || string.IsNullOrWhiteSpace(message.Content)) return false;

        try
        {
            payload = JsonUtility.FromJson<TPayload>(message.Content);
            return payload != null;
        }
        catch
        {
            payload = default(TPayload);
            return false;
        }
    }

    /// <summary>
    /// 处理任务进度更新（非任务分配）。
    /// </summary>
    private void HandleTaskUpdate(AgentMessage message)
    {
        if (TryParseMessageContent<TaskProgressPayload>(message, out TaskProgressPayload payload) && payload != null)
        {
            if (planningModule != null &&
                string.Equals(payload.status, "execution_released", StringComparison.OrdinalIgnoreCase))
            {
                planningModule.ReleaseExecutionForAssignedPlan(payload.slotId);
                Debug.Log($"{agent.Properties.AgentID} 收到统一执行放行: slot={payload.slotId}");
                return;
            }

            if (planningModule != null)
            {
                planningModule.HandleTaskProgressPayload(payload);
            }
            Debug.Log($"{agent.Properties.AgentID} 收到任务进度更新 from {message.SenderID}: mission={payload.missionDescription}, status={payload.status}, step={payload.completedStep}, next={payload.nextStep}");
            return;
        }

        Debug.Log($"{agent.Properties.AgentID} 收到任务进度更新 from {message.SenderID}: {message.Content}");
    }

    /// <summary>
    /// 处理任务完成消息。
    /// </summary>
    private void HandleTaskCompletion(AgentMessage message)
    {
        if (TryParseMessageContent<TaskProgressPayload>(message, out TaskProgressPayload payload) && payload != null)
        {
            if (planningModule != null)
            {
                planningModule.HandleTaskProgressPayload(payload);
            }
            Debug.Log($"{agent.Properties.AgentID} 收到任务完成消息 from {message.SenderID}: mission={payload.missionDescription}, role={payload.role}, status={payload.status}");
            return;
        }

        Debug.Log($"{agent.Properties.AgentID} 收到任务完成消息 from {message.SenderID}: {message.Content}");
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
        if (planningModule == null) return;

        if (TryParseMessageContent<RolePreferencePayload>(message, out RolePreferencePayload payload) && payload != null)
        {
            planningModule.receivedPreferencePayloads[message.SenderID] = payload;
            planningModule.receivedPreferences[message.SenderID] = payload.preferences ?? new RoleType[0];
            return;
        }

        if (message.Type == MessageType.RolePreference)
        {
            var pref = JsonUtility.FromJson<RolePreferenceWrapper>(message.Content);
            if (pref != null && pref.preferences != null)
            {
                planningModule.receivedPreferences[message.SenderID] = pref.preferences;
            }
        }
    }

    private void HandleRoleConfirmed(AgentMessage message)
    {
        Debug.Log($"{agent.Properties.AgentID} 收到角色确认消息: {message.Content}");
        if (planningModule == null) return;

        if (TryParseMessageContent<RoleDecisionPayload>(message, out RoleDecisionPayload payload) && payload != null)
        {
            if (planningModule.currentMission != null && payload.directives != null && payload.directives.Length > 0)
            {
                planningModule.currentMission.coordinationDirectives = payload.directives;
            }
            else if (planningModule.currentMission != null && payload.directive != null)
            {
                planningModule.currentMission.coordinationDirectives = new[] { payload.directive };
            }
            if (planningModule.currentMission != null)
            {
                planningModule.ReceiveMissionAssignment(planningModule.currentMission, payload.assignedRole, payload.assignedSlot);
            }
            return;
        }

        if (!System.Enum.TryParse<RoleType>(message.Content, true, out RoleType finalRole))
        {
            Debug.LogWarning($"{agent.Properties.AgentID} 无法解析角色确认: {message.Content}");
            return;
        }
        StartCoroutine(planningModule.AnalyzeMissionAndCreatePlan(planningModule.currentMission, finalRole));
    }

    /// <summary>
    /// 处理角色接受/分配回执（用于闭环可观测性）。
    /// </summary>
    private void HandleRoleAssignment(AgentMessage message)
    {
        if (TryParseMessageContent<RoleAcceptancePayload>(message, out RoleAcceptancePayload payload) && payload != null)
        {
            if (planningModule != null)
            {
                planningModule.HandleRoleAcceptancePayload(payload);
            }
            Debug.Log($"{agent.Properties.AgentID} 收到角色分配回执 from {message.SenderID}: acceptedRole={payload.acceptedRole}, agentType={payload.agentType}");
            return;
        }

        Debug.Log($"{agent.Properties.AgentID} 收到角色分配回执 from {message.SenderID}: {message.Content}");
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
