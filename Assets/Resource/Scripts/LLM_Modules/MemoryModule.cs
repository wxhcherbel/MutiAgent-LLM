// Scripts/Modules/MemoryModule.cs
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

[Serializable]
public class Memory
{
    public string id;                 // 记忆唯一标识
    public string description;        // 记忆内容描述
    public DateTime timestamp;        // 记忆时间戳
    public float importance;          // 记忆重要性（0-1）
    public string type;               // 记忆类型
    public List<string> associations; // 关联关键词
}

public class MemoryModule : MonoBehaviour
{
    public List<Memory> memories = new List<Memory>();
    public LLMInterface llmInterface;

    void Start()
    {
        llmInterface = FindObjectOfType<LLMInterface>();
        if (llmInterface == null)
        {
            Debug.LogError("LLMInterface not found in scene! Memory associations will not be generated.");
        }
    }
    
    // 添加新记忆
    public void AddMemory(string description, string type, float importance = 0.5f)
    {
        Memory newMemory = new Memory
        {
            id = Guid.NewGuid().ToString(),
            description = description,
            timestamp = DateTime.Now,
            importance = importance,
            type = type,
            associations = new List<string>()
        };
        
        memories.Add(newMemory);
        StartCoroutine(GenerateAssociations(newMemory));
    }
    
    // 使用LLM生成记忆关联关键词
    private IEnumerator GenerateAssociations(Memory memory)
    {
        string prompt = $"Given the memory: '{memory.description}'. " +
                       "Provide 3-5 keywords for retrieval. " +
                       "Return comma-separated list only.";
        
        yield return llmInterface.SendRequest(prompt, (result) => 
        {
            if (!string.IsNullOrEmpty(result))
            {
                string[] associations = result.Split(',');
                foreach (string association in associations)
                {
                    memory.associations.Add(association.Trim());
                }
            }
        }, temperature: 0.3f, maxTokens: 50);
    }
    
    // 检索相关记忆
    public List<Memory> RetrieveRelevantMemories(string query, int maxCount = 5)
    {
        // 实现关键词匹配和重要性排序的记忆检索
        List<Memory> relevant = new List<Memory>();
        
        foreach (Memory memory in memories)
        {
            // 关键词匹配逻辑
            foreach (string association in memory.associations)
            {
                if (query.ToLower().Contains(association.ToLower()) && 
                    !relevant.Contains(memory))
                {
                    relevant.Add(memory);
                    break;
                }
            }
        }
        
        // 按重要性排序
        relevant.Sort((a, b) => b.importance.CompareTo(a.importance));
        
        return relevant.GetRange(0, Mathf.Min(maxCount, relevant.Count));
    }
}