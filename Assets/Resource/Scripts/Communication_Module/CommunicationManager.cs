using UnityEngine;
using System.Collections.Generic;

public class CommunicationManager : MonoBehaviour
{
    public static CommunicationManager Instance; // 单例实例
    
    private Dictionary<string, CommunicationModule> registeredAgents = new Dictionary<string, CommunicationModule>(); // 注册的智能体
    private List<AgentMessage> messageLog = new List<AgentMessage>(); // 消息日志

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// 注册智能体
    /// </summary>
    public void RegisterAgent(CommunicationModule agentModule)
    {
        string agentId = agentModule.GetComponent<IntelligentAgent>().Properties.AgentID;
        if (!registeredAgents.ContainsKey(agentId))
        {
            registeredAgents.Add(agentId, agentModule);
        }
    }

    /// <summary>
    /// 注销智能体
    /// </summary>
    public void UnregisterAgent(CommunicationModule agentModule)
    {
        string agentId = agentModule.GetComponent<IntelligentAgent>().Properties.AgentID;
        if (registeredAgents.ContainsKey(agentId))
        {
            registeredAgents.Remove(agentId);
        }
    }

    /// <summary>
    /// 处理消息路由
    /// </summary>
    public void ProcessMessage(AgentMessage message)
    {
        // 记录消息
        messageLog.Add(message);
        
        // 模拟通信延迟
        StartCoroutine(DeliverMessageWithDelay(message, Random.Range(0.1f, 0.5f)));
    }

    private System.Collections.IEnumerator DeliverMessageWithDelay(AgentMessage message, float delay)
    {
        yield return new WaitForSeconds(delay);
        
        if (message.ReceiverID == "All")
        {
            // 广播消息
            foreach (var agent in registeredAgents.Values)
            {
                if (agent.GetComponent<IntelligentAgent>().Properties.AgentID != message.SenderID)
                {
                    agent.ReceiveMessage(message);
                }
            }
        }
        else
        {
            // 单播消息
            if (registeredAgents.ContainsKey(message.ReceiverID))
            {
                registeredAgents[message.ReceiverID].ReceiveMessage(message);
            }
        }
    }

    /// <summary>
    /// 获取智能体间的通信距离
    /// </summary>
    public float GetCommunicationDistance(string agent1Id, string agent2Id)
    {
        if (registeredAgents.ContainsKey(agent1Id) && registeredAgents.ContainsKey(agent2Id))
        {
            Vector3 pos1 = registeredAgents[agent1Id].transform.position;
            Vector3 pos2 = registeredAgents[agent2Id].transform.position;
            return Vector3.Distance(pos1, pos2);
        }
        return float.MaxValue;
    }
}