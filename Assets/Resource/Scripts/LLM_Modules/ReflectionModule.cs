// Scripts/Modules/ReflectionModule.cs
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

public class ReflectionModule : MonoBehaviour
{
    private MemoryModule memoryModule;
    private LLMInterface llmInterface;
    private float reflectionCooldown = 300f; // 5分钟反思一次
    private float lastReflectionTime = 0f;
    
    void Start()
    {
        memoryModule = GetComponent<MemoryModule>();
        llmInterface = FindObjectOfType<LLMInterface>();
    }
    
    void Update()
    {
        // 定期触发反思
        if (Time.time - lastReflectionTime > reflectionCooldown)
        {
            StartCoroutine(TriggerReflection());
            lastReflectionTime = Time.time;
        }
    }
    
    // 触发反思过程
    public IEnumerator TriggerReflection()
    {
        // 获取近期重要记忆
        List<Memory> recentMemories = memoryModule.RetrieveRelevantMemories("", 10);
        
        if (recentMemories.Count == 0) yield break;
        
        string memoriesText = "";
        foreach (Memory memory in recentMemories)
        {
            memoriesText += $"- {memory.description} (importance: {memory.importance})\n";
        }
        
        string prompt = $"As an AI agent, I have experienced the following events:\n{memoriesText}\n\n" +
                       "What insights or patterns can I draw from these experiences?" +
                       "What should I learn or remember in order to make better decisions in the future? " +
                       "Provide 2-3 concise insights.";
        
        yield return llmInterface.SendRequest(prompt, (result) => 
        {
            if (!string.IsNullOrEmpty(result))
            {
                // 将反思结果存储为高重要性记忆
                memoryModule.AddMemory($"Reflection and Summary: {result}", "reflection", 0.9f);
                Debug.Log($"Agent Reflection: {result}");
            }
        }, temperature: 0.8f);
    }
}