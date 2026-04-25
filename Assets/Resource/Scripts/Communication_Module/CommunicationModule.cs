// Communication_Module/CommunicationModule.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

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
    private GroupMonitor _groupMonitor; // 惰性初始化，仅组长 GameObject 上非 null

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

            // ─── 紧急情况 & MAD 辩论协议 ─────────────────────────────────
            case MessageType.IncidentReport:
                // 成员 → Leader：上报紧急事件（仅组长 GameObject 上有 GroupMonitor）
                ForwardPayload<IncidentReport>(message,
                    r => GetGroupMonitor()?.HandleIncident(r));
                break;

            case MessageType.IncidentAnnounce:
                // Leader → 成员：广播 Critical 事件通知，ADM 检测 isCritical 后中断 Rolling Loop
                ForwardPayload<IncidentReport>(message,
                    admModule != null ? (Action<IncidentReport>)admModule.OnIncidentAnnounced : null);
                break;

            case MessageType.IncidentQuery:
                // Leader → 成员：发起辩论查询（Round 1 独立提案 / Round 2 参考修正）
                ForwardPayload<IncidentQuery>(message,
                    admModule != null ? (Action<IncidentQuery>)admModule.OnIncidentQuery : null);
                break;

            case MessageType.IncidentOpinion:
                // 成员 → Leader：回传 LLM 生成的辩论意见（仅组长 GameObject 上有 GroupMonitor）
                ForwardPayload<MemberOpinion>(message,
                    o => GetGroupMonitor()?.OnOpinionReceived(o));
                break;

            case MessageType.IncidentResolved:
                // Leader → 成员：广播最终决策（IncidentDecision），ADM 按 directives 执行
                ForwardPayload<IncidentDecision>(message,
                    admModule != null ? (Action<IncidentDecision>)admModule.OnIncidentResolved : null);
                break;

            // ─── 自主涌现协作招募协议 ─────────────────────────────────────────────
            case MessageType.ColabInvite:
            {
                // 发起者广播，Content=ColabInvitePayload JSON（含约束和角色分配）
                AutonomousDriveModule.ColabInvitePayload invitePayload = null;
                try { invitePayload = JsonConvert.DeserializeObject<AutonomousDriveModule.ColabInvitePayload>(message.Content); }
                catch (Exception ex) { Debug.LogWarning($"[CommunicationModule] ColabInvite 解析失败: {ex.Message}"); }
                if (invitePayload != null)
                    GetComponent<AutonomousDriveModule>()?.OnColabInvite(message.SenderID, invitePayload);
                break;
            }

            case MessageType.ColabAccept:
                // 接受者回传，SenderID=接受者，Content=AcceptContext JSON（电量/位置）
                GetComponent<AutonomousDriveModule>()?.OnColabAccept(message.SenderID, message.Content);
                break;

            case MessageType.ColabStart:
            {
                // 发起方向选中接受者发送最终角色+约束分配
                AutonomousDriveModule.ColabStartPayload startPayload = null;
                try { startPayload = JsonConvert.DeserializeObject<AutonomousDriveModule.ColabStartPayload>(message.Content); }
                catch (Exception ex) { Debug.LogWarning($"[CommunicationModule] ColabStart 解析失败: {ex.Message}"); }
                if (startPayload != null)
                    GetComponent<AutonomousDriveModule>()?.OnColabStart(startPayload);
                break;
            }

            default:
                Debug.Log($"[CommunicationModule] {agent?.Properties?.AgentID} 收到未处理消息: {message.Type}");
                break;
        }
    }

    // ─────────────────────────────────────────────────────────
    // 具体消息处理方法
    // ─────────────────────────────────────────────────────────

    /// <summary>惰性获取本 GameObject 上的 GroupMonitor（仅组长有此组件）。</summary>
    private GroupMonitor GetGroupMonitor()
    {
        if (_groupMonitor == null)
            _groupMonitor = GetComponent<GroupMonitor>();
        return _groupMonitor;
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
