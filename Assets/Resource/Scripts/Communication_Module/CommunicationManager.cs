using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class CommunicationManager : MonoBehaviour
{
    public static CommunicationManager Instance;

    private readonly Dictionary<string, CommunicationModule> registeredAgents = new();
    private readonly List<AgentMessage> messageLog = new();

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            return;
        }

        if (Instance != this)
        {
            Destroy(gameObject);
        }
    }

    public void RegisterAgent(CommunicationModule agentModule)
    {
        IntelligentAgent agent = agentModule != null ? agentModule.GetComponent<IntelligentAgent>() : null;
        string agentId = agent?.Properties?.AgentID;
        if (string.IsNullOrWhiteSpace(agentId)) return;
        registeredAgents[agentId] = agentModule;
    }

    public void UnregisterAgent(CommunicationModule agentModule)
    {
        IntelligentAgent agent = agentModule != null ? agentModule.GetComponent<IntelligentAgent>() : null;
        string agentId = agent?.Properties?.AgentID;
        if (string.IsNullOrWhiteSpace(agentId)) return;
        registeredAgents.Remove(agentId);
    }

    public CommunicationModule GetAgentModule(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        registeredAgents.TryGetValue(agentId, out CommunicationModule module);
        return module;
    }

    public string[] GetAllAgentIds()
    {
        return registeredAgents.Keys.ToArray();
    }

    /// <summary>返回最近消息日志的只读视图（供 AgentStateServer 使用）。</summary>
    public IReadOnlyList<AgentMessage> RecentMessageLog => messageLog;

    public void ProcessMessage(AgentMessage message)
    {
        if (message == null) return;
        messageLog.Add(message);
        // BUG-M1: 消息日志上限 2000，FIFO 丢弃最旧
        if (messageLog.Count > 2000) messageLog.RemoveAt(0);
        float delay = message.Reliable ? 0f : Random.Range(0.05f, 0.15f);
        StartCoroutine(DeliverMessageWithDelay(message, delay));
    }

    private IEnumerator DeliverMessageWithDelay(AgentMessage message, float delay)
    {
        if (delay > 0f)
        {
            yield return new WaitForSeconds(delay);
        }

        List<CommunicationModule> recipients = ResolveRecipients(message);
        foreach (CommunicationModule module in recipients)
        {
            if (module == null) continue;
            if (!message.Reliable && !IsInCommunicationRange(message.SenderID, module.GetComponent<IntelligentAgent>()?.Properties?.AgentID))
            {
                continue;
            }

            module.ReceiveMessage(message);
        }
    }

    private List<CommunicationModule> ResolveRecipients(AgentMessage message)
    {
        if (message == null) return new List<CommunicationModule>();

        switch (message.Scope)
        {
            case CommunicationScope.DirectAgent:
                return ResolveDirectRecipient(message);
            case CommunicationScope.Team:
                return ResolveTeamRecipients(message);
            case CommunicationScope.Public:
                return ResolvePublicRecipients(message);
            default:
                return ResolveLegacyRecipients(message);
        }
    }

    private List<CommunicationModule> ResolveDirectRecipient(AgentMessage message)
    {
        string targetAgentId = !string.IsNullOrWhiteSpace(message.TargetAgentId) ? message.TargetAgentId : message.ReceiverID;
        if (string.IsNullOrWhiteSpace(targetAgentId)) return new List<CommunicationModule>();
        return registeredAgents.TryGetValue(targetAgentId, out CommunicationModule module)
            ? new List<CommunicationModule> { module }
            : new List<CommunicationModule>();
    }

    private List<CommunicationModule> ResolveTeamRecipients(AgentMessage message)
    {
        string senderId = message.SenderID ?? string.Empty;
        string targetNumericTeam = !string.IsNullOrWhiteSpace(message.TargetTeamId)
            ? message.TargetTeamId
            : ResolveAgentTeamId(senderId);
        if (string.IsNullOrWhiteSpace(targetNumericTeam)) return new List<CommunicationModule>();

        return registeredAgents.Values
            .Where(module =>
            {
                IntelligentAgent agent = module != null ? module.GetComponent<IntelligentAgent>() : null;
                if (agent?.Properties == null) return false;
                if (string.Equals(agent.Properties.AgentID, senderId, System.StringComparison.OrdinalIgnoreCase)) return false;
                return string.Equals(agent.Properties.TeamID.ToString(), targetNumericTeam, System.StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    private List<CommunicationModule> ResolvePublicRecipients(AgentMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TargetAgentId) &&
            registeredAgents.TryGetValue(message.TargetAgentId, out CommunicationModule direct))
        {
            return new List<CommunicationModule> { direct };
        }

        return registeredAgents.Values.ToList();
    }

    private List<CommunicationModule> ResolveLegacyRecipients(AgentMessage message)
    {
        if (string.Equals(message.ReceiverID, "All", System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning("[Communication] ReceiverID=All 已弃用，请改用显式 CommunicationScope。");
            return new List<CommunicationModule>();
        }

        return ResolveDirectRecipient(message);
    }

    private string ResolveAgentTeamId(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return string.Empty;
        if (!registeredAgents.TryGetValue(agentId, out CommunicationModule module) || module == null) return string.Empty;
        IntelligentAgent agent = module.GetComponent<IntelligentAgent>();
        return agent?.Properties != null ? agent.Properties.TeamID.ToString() : string.Empty;
    }

    private bool IsInCommunicationRange(string senderId, string targetId)
    {
        if (string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(targetId)) return false;
        if (!registeredAgents.TryGetValue(senderId, out CommunicationModule senderModule) ||
            !registeredAgents.TryGetValue(targetId, out CommunicationModule targetModule) ||
            senderModule == null ||
            targetModule == null)
        {
            return false;
        }

        IntelligentAgent sender = senderModule.GetComponent<IntelligentAgent>();
        if (sender?.Properties == null) return false;
        float distance = Vector3.Distance(senderModule.transform.position, targetModule.transform.position);
        return distance <= sender.Properties.CommunicationRange;
    }

    public float GetCommunicationDistance(string agent1Id, string agent2Id)
    {
        if (string.IsNullOrWhiteSpace(agent1Id) || string.IsNullOrWhiteSpace(agent2Id)) return float.MaxValue;
        if (!registeredAgents.TryGetValue(agent1Id, out CommunicationModule agent1) ||
            !registeredAgents.TryGetValue(agent2Id, out CommunicationModule agent2) ||
            agent1 == null ||
            agent2 == null)
        {
            return float.MaxValue;
        }

        return Vector3.Distance(agent1.transform.position, agent2.transform.position);
    }
}
