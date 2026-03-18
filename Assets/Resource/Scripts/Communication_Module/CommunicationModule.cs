// Communication_Module/CommunicationModule.cs
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 智能体通信端点。
/// 职责：
///   1) 维护收件队列，按帧批量处理；
///   2) 提供多种发送接口（裸字符串 / 结构化载荷 / Scoped路由）；
///   3) 把收到的消息分发给 PlanningModule 对应的处理方法。
/// </summary>
public class CommunicationModule : MonoBehaviour
{
    // ─── 内部状态 ─────────────────────────────────────────────
    private IntelligentAgent agent;                               // 所属智能体
    private readonly Queue<AgentMessage> incomingMessages = new(); // 收件队列

    [Header("通信设置")]
    [SerializeField] private bool enablePeriodicProcessing = true; // 是否在 Update 中自动处理队列

    public PlanningModule planningModule;  // 关联的规划模块（Start 时自动获取）
    public ActionDecisionModule admModule; // 关联的决策模块（Start 时自动获取）

    // ─────────────────────────────────────────────────────────
    // 生命周期
    // ─────────────────────────────────────────────────────────

    private void Start()
    {
        agent          = GetComponent<IntelligentAgent>();
        planningModule = GetComponent<PlanningModule>();
        admModule      = GetComponent<ActionDecisionModule>();

        if (CommunicationManager.Instance != null)
            CommunicationManager.Instance.RegisterAgent(this);
        else
            Debug.LogError("[CommunicationModule] CommunicationManager 未找到，通信不可用。");
    }

    private void Update()
    {
        if (enablePeriodicProcessing) ProcessMessages();
    }

    private void OnDestroy()
    {
        if (CommunicationManager.Instance != null)
            CommunicationManager.Instance.UnregisterAgent(this);
    }

    /// <summary>供外部显式初始化（当前无特殊逻辑，保留接口）。</summary>
    public void Initialize() { }

    // ─────────────────────────────────────────────────────────
    // 发送接口
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 发送纯字符串内容消息（兼容旧调用路径）。
    /// receiverID 传 "All" 时将 Scope 设为 Team（广播给同队）。
    /// </summary>
    public void SendMessage(string receiverID, MessageType messageType, string content, int priority = 1)
    {
        AgentMessage msg = new AgentMessage
        {
            SenderID     = agent?.Properties?.AgentID ?? string.Empty,
            ReceiverID   = receiverID,
            TargetAgentId = receiverID,
            Type         = messageType,
            Priority     = priority,
            Timestamp    = Time.time,
            Content      = content ?? string.Empty,
            SenderTeamId = agent?.Properties != null ? agent.Properties.TeamID.ToString() : string.Empty,
            Scope        = string.Equals(receiverID, "All", StringComparison.OrdinalIgnoreCase)
                           ? CommunicationScope.Team
                           : CommunicationScope.DirectAgent
        };

        if (msg.Scope == CommunicationScope.Team)
            msg.TargetTeamId = agent?.Properties != null ? agent.Properties.TeamID.ToString() : string.Empty;

        SendMessage(msg);
    }

    /// <summary>
    /// 发送结构化载荷（自动 JSON 序列化）。
    /// </summary>
    public void SendStructuredMessage<TPayload>(
        string receiverID, MessageType messageType, TPayload payload, int priority = 1)
    {
        SendMessage(receiverID, messageType, payload != null ? JsonUtility.ToJson(payload) : "{}", priority);
    }

    /// <summary>
    /// 发送带完整路由控制的 Scoped 消息（主要供 PlanningModule 使用）。
    /// </summary>
    /// <param name="scope">路由范围：DirectAgent / Team / Public</param>
    /// <param name="targetAgentId">DirectAgent 时填目标 AgentID</param>
    /// <param name="targetTeamId">Team 时填目标队伍 ID（留空则取发送方队伍）</param>
    /// <param name="reliable">true 时跳过通信距离检测，确保送达</param>
    public void SendScopedMessage<TPayload>(
        CommunicationScope scope,
        MessageType messageType,
        TPayload payload,
        string targetAgentId = "",
        string targetTeamId  = "",
        int    priority      = 1,
        bool   reliable      = false,
        string scenarioId    = "",
        string missionId     = "")
    {
        AgentMessage msg = new AgentMessage
        {
            SenderID      = agent?.Properties?.AgentID ?? string.Empty,
            ReceiverID    = targetAgentId ?? string.Empty,
            TargetAgentId = targetAgentId ?? string.Empty,
            TargetTeamId  = string.IsNullOrWhiteSpace(targetTeamId) && scope == CommunicationScope.Team
                            ? (agent?.Properties != null ? agent.Properties.TeamID.ToString() : string.Empty)
                            : (targetTeamId ?? string.Empty),
            Type          = messageType,
            Priority      = priority,
            Timestamp     = Time.time,
            Content       = payload != null ? JsonUtility.ToJson(payload) : "{}",
            ScenarioId    = scenarioId ?? string.Empty,
            MissionId     = missionId  ?? string.Empty,
            SenderTeamId  = agent?.Properties != null ? agent.Properties.TeamID.ToString() : string.Empty,
            Scope         = scope,
            PayloadType   = typeof(TPayload).Name,
            Reliable      = reliable
        };

        SendMessage(msg);
    }

    /// <summary>发送已构建好的消息对象（最底层发送入口）。</summary>
    public void SendMessage(AgentMessage message)
    {
        if (message == null) return;

        // 补充发送方信息
        message.SenderID     = agent?.Properties?.AgentID ?? string.Empty;
        message.Timestamp    = Time.time;
        message.SenderTeamId = agent?.Properties != null ? agent.Properties.TeamID.ToString() : string.Empty;

        if (CommunicationManager.Instance != null)
            CommunicationManager.Instance.ProcessMessage(message);
        else
            Debug.LogError("[CommunicationModule] CommunicationManager 未找到，消息丢弃。");
    }

    // ─────────────────────────────────────────────────────────
    // 收件队列接口（供外部轮询）
    // ─────────────────────────────────────────────────────────

    /// <summary>是否存在未读消息。</summary>
    public bool HasUnreadMessages() => incomingMessages.Count > 0;

    /// <summary>返回当前队列中所有消息（不出队）。</summary>
    public AgentMessage[] GetUnreadMessages() => incomingMessages.ToArray();

    /// <summary>返回队列中指定类型的消息（不出队）。</summary>
    public AgentMessage[] GetMessagesByType(MessageType messageType)
    {
        List<AgentMessage> result = new();
        foreach (AgentMessage msg in incomingMessages)
        {
            if (msg.Type == messageType) result.Add(msg);
        }
        return result.ToArray();
    }

    /// <summary>清空收件队列。</summary>
    public void ClearMessageQueue() => incomingMessages.Clear();

    /// <summary>返回单行通信状态摘要（供 UI / 日志使用）。</summary>
    public string GetCommunicationSummary()
        => $"pending={incomingMessages.Count}, range={agent?.Properties?.CommunicationRange ?? 0f:F1}m";

    /// <summary>返回按类型分组的详细通信统计（供调试使用）。</summary>
    public string GetDetailedCommunicationInfo()
    {
        Dictionary<MessageType, int> counts = new();
        foreach (AgentMessage msg in incomingMessages)
        {
            counts.TryGetValue(msg.Type, out int cur);
            counts[msg.Type] = cur + 1;
        }

        List<string> lines = new();
        foreach (KeyValuePair<MessageType, int> kv in counts)
            lines.Add($"{kv.Key}:{kv.Value}");

        return string.Join(", ", lines);
    }

    // ─────────────────────────────────────────────────────────
    // 收件与分发
    // ─────────────────────────────────────────────────────────

    /// <summary>由 CommunicationManager 调用，将消息投入本端队列。</summary>
    public void ReceiveMessage(AgentMessage message)
    {
        if (message == null) return;
        incomingMessages.Enqueue(message);
    }

    /// <summary>逐条取出队列中的消息并调用 HandleMessage。</summary>
    public void ProcessMessages()
    {
        while (incomingMessages.Count > 0)
            HandleMessage(incomingMessages.Dequeue());
    }

    /// <summary>消息分发总入口：按 MessageType 路由到对应处理方法。</summary>
    private void HandleMessage(AgentMessage message)
    {
        switch (message.Type)
        {
            // ─── 环境 / 告警类 ────────────────────────────────
            case MessageType.EnvironmentAlert:
                HandleEnvironmentAlert(message);
                break;

            case MessageType.ObstacleWarning:
                HandleObstacleWarning(message);
                break;

            case MessageType.SystemAlert:
                HandleSystemAlert(message);
                break;

            // ─── 协作 / 求助类 ───────────────────────────────
            case MessageType.HelpRequest:
            case MessageType.RequestHelp:
                HandleHelpRequest(message);
                break;

            case MessageType.Response:
                HandleResponse(message);
                break;

            // ─── 任务进度类 ───────────────────────────────────
            case MessageType.TaskAnnouncement:
                HandleTaskAnnouncement(message);
                break;

            case MessageType.TaskUpdate:
                HandleTaskUpdate(message);
                break;

            case MessageType.TaskCompletion:
                HandleTaskCompletion(message);
                break;

            case MessageType.TaskAbort:
                HandleTaskAbort(message);
                break;

            // ─── 资源类 ──────────────────────────────────────
            case MessageType.ResourceRequest:
                HandleResourceRequest(message);
                break;

            case MessageType.ResourceOffer:
                HandleResourceOffer(message);
                break;

            // ─── 同步类 ──────────────────────────────────────
            case MessageType.Synchronization:
                HandleSynchronization(message);
                break;

            // ─── 角色分配（保留枚举值供 IntelligentAgent 引用）──
            case MessageType.RoleAssignment:
                HandleRoleAssignment(message);
                break;

            // ─── 新4阶段协商协议 ──────────────────────────────
            case MessageType.GroupBootstrap:
                ForwardPayload<GroupBootstrapPayload>(message,
                    planningModule != null ? (Action<GroupBootstrapPayload>)planningModule.OnGroupBootstrap : null);
                break;

            case MessageType.SlotBroadcast:
                ForwardPayload<SlotBroadcastPayload>(message,
                    planningModule != null ? (Action<SlotBroadcastPayload>)planningModule.OnSlotBroadcast : null);
                break;

            case MessageType.SlotSelect:
                ForwardPayload<SlotSelectPayload>(message,
                    planningModule != null ? (Action<SlotSelectPayload>)planningModule.OnSlotSelect : null);
                break;

            case MessageType.SlotConfirm:
                ForwardPayload<SlotConfirmPayload>(message,
                    planningModule != null ? (Action<SlotConfirmPayload>)planningModule.OnSlotConfirm : null);
                break;

            case MessageType.StartExecution:
                ForwardPayload<StartExecPayload>(message,
                    planningModule != null ? (Action<StartExecPayload>)planningModule.OnStartExec : null);
                break;

            case MessageType.BoardUpdate:
                ForwardPayload<AgentContextUpdate>(message,
                    admModule != null ? (Action<AgentContextUpdate>)admModule.OnBoardUpdate : null);
                break;

            default:
                Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到未处理消息: {message.Type}");
                break;
        }
    }

    // ─────────────────────────────────────────────────────────
    // 具体消息处理方法
    // ─────────────────────────────────────────────────────────

    /// <summary>处理环境告警（障碍、危险区域等）。</summary>
    private void HandleEnvironmentAlert(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到环境告警 from {message.SenderID}: {message.Content}");
        // 可在此更新感知层或触发规避行为
    }

    /// <summary>处理障碍物警告。</summary>
    private void HandleObstacleWarning(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到障碍警告 from {message.SenderID}: {message.Content}");
        // 更新局部地图临时阻塞
    }

    /// <summary>处理系统级告警（电量告急、模块异常等）。</summary>
    private void HandleSystemAlert(AgentMessage message)
    {
        Debug.LogWarning($"[CommunicationModule] {agent?.Properties?.AgentID} 收到系统告警 from {message.SenderID}: {message.Content}");
    }

    /// <summary>处理求助请求；若自身空闲则发送响应。</summary>
    private void HandleHelpRequest(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到求助 from {message.SenderID}: {message.Content}");
        if (agent?.CurrentState?.Status == AgentStatus.Idle)
        {
            SendMessage(message.SenderID, MessageType.Response,
                "{\"response\":\"acknowledged\",\"action\":\"coming_to_help\"}", 2);
        }
    }

    /// <summary>处理响应消息（对 HelpRequest 的回复等）。</summary>
    private void HandleResponse(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到响应 from {message.SenderID}: {message.Content}");
    }

    /// <summary>
    /// 处理任务公告（旧链路兼容）。
    /// 新4阶段协议中任务由 SubmitMissionRequest → GroupBootstrap 发起，
    /// 此处仅做日志记录，不再调用 ReceiveMissionAssignment。
    /// </summary>
    private void HandleTaskAnnouncement(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到任务公告 from {message.SenderID}");
        // 新协议下任务入口为 PlanningModule.SubmitMissionRequest，此消息类型保留仅供兼容
    }

    /// <summary>处理任务进度更新。</summary>
    private void HandleTaskUpdate(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到任务进度更新 from {message.SenderID}: {message.Content}");
    }

    /// <summary>处理任务完成消息。</summary>
    private void HandleTaskCompletion(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到任务完成 from {message.SenderID}: {message.Content}");
    }

    /// <summary>处理任务中止消息。</summary>
    private void HandleTaskAbort(AgentMessage message)
    {
        Debug.LogWarning($"[CommunicationModule] {agent?.Properties?.AgentID} 收到任务中止 from {message.SenderID}: {message.Content}");
    }

    /// <summary>处理资源请求（补给点、充电桩等）。</summary>
    private void HandleResourceRequest(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到资源请求 from {message.SenderID}: {message.Content}");
    }

    /// <summary>处理资源提供消息。</summary>
    private void HandleResourceOffer(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到资源提供 from {message.SenderID}: {message.Content}");
    }

    /// <summary>处理同步信号（集结点、等待所有就绪等）。</summary>
    private void HandleSynchronization(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到同步信号 from {message.SenderID}: {message.Content}");
    }

    /// <summary>处理角色分配消息（旧链路兼容，仅记录日志）。</summary>
    private void HandleRoleAssignment(AgentMessage message)
    {
        Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到角色分配 from {message.SenderID}: {message.Content}");
        // 新协议通过 SlotConfirm 完成角色确认，此处保留枚举值兼容性
    }

    // ─────────────────────────────────────────────────────────
    // 辅助工具
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 将消息 Content 反序列化为指定载荷类型，并调用对应处理委托。
    /// 解析失败时记录错误日志但不抛异常。
    /// </summary>
    private void ForwardPayload<TPayload>(AgentMessage message, Action<TPayload> handler) where TPayload : class
    {
        if (handler == null || message == null) return;
        if (TryParseMessageContent(message, out TPayload payload) && payload != null)
            handler(payload);
        else
            Debug.LogWarning($"[CommunicationModule] 无法解析 {typeof(TPayload).Name}，消息内容: {message.Content}");
    }

    /// <summary>
    /// 尝试将消息 Content 解析为指定类型。
    /// 返回 true 且 payload 非 null 表示成功。
    /// </summary>
    public bool TryParseMessageContent<TPayload>(AgentMessage message, out TPayload payload) where TPayload : class
    {
        payload = null;
        if (message == null || string.IsNullOrWhiteSpace(message.Content)) return false;
        try
        {
            payload = JsonUtility.FromJson<TPayload>(message.Content);
            return payload != null;
        }
        catch
        {
            return false;
        }
    }
}
