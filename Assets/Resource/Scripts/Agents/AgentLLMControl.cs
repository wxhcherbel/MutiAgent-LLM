// Scripts/Agents/AgentController.cs
using System.Collections;
using UnityEngine;

public class AgentLLMControl : MonoBehaviour
{
    public MemoryModule memoryModule;
    public ReflectionModule reflectionModule;
    public PlanningModule planningModule;
    public ActionDecisionModule actionDecisionModule;
    
    private bool isInitialized = false;
    
    void Start()
    {
        InitializeAgent();
    }
    
    /// <summary>
    /// 初始化智能体控制器
    /// </summary>
    private void InitializeAgent()
    {
        if (isInitialized) return;
        
        // 安全地获取或添加所有模块组件
        memoryModule = GetComponent<MemoryModule>();
        if (memoryModule == null)
        {
            memoryModule = gameObject.AddComponent<MemoryModule>();
            Debug.Log($"为 {gameObject.name} 添加 MemoryModule 组件");
        }
        
        reflectionModule = GetComponent<ReflectionModule>();
        if (reflectionModule == null)
        {
            reflectionModule = gameObject.AddComponent<ReflectionModule>();
            Debug.Log($"为 {gameObject.name} 添加 ReflectionModule 组件");
        }
        
        planningModule = GetComponent<PlanningModule>();
        if (planningModule == null)
        {
            planningModule = gameObject.AddComponent<PlanningModule>();
            Debug.Log($"为 {gameObject.name} 添加 PlanningModule 组件");
        }
        
        actionDecisionModule = GetComponent<ActionDecisionModule>();
        if (actionDecisionModule == null)
        {
            actionDecisionModule = gameObject.AddComponent<ActionDecisionModule>();
            Debug.Log($"为 {gameObject.name} 添加 ActionDecisionModule 组件");
        }
        
        // // 初始化起始记忆（带错误处理）
        // try
        // {
        //     if (memoryModule != null)
        //     {
        //         memoryModule.AddMemory("系统初始化完成，开始执行任务", "observation", 0.6f);
        //     }
        //     else
        //     {
        //         Debug.LogError("MemoryModule 初始化失败，无法添加初始记忆");
        //     }
        // }
        // catch (System.Exception e)
        // {
        //     Debug.LogWarning($"初始记忆添加失败: {e.Message}");
        // }
        
        isInitialized = true;
        //Debug.Log($"智能体 {gameObject.name} 初始化完成");
    }
    
    void Update()
    {
        // // 定期做出决策
        // if (Time.time - lastDecisionTime > decisionInterval)
        // {
        //     StartCoroutine(actionDecisionModule.DecideNextAction());
        //     lastDecisionTime = Time.time;
            
        //     // 随机调整决策间隔，使行为更自然
        //     decisionInterval = Random.Range(8f, 15f);
        // }
    }
    
    /// <summary>
    /// 外部调用记录事件（安全版本）
    /// </summary>
    public void RecordEvent(string description, string type = "observation", float importance = 0.5f)
    {
        // 确保已初始化
        if (!isInitialized)
        {
            InitializeAgent();
        }
        
        // 安全检查
        if (string.IsNullOrEmpty(description))
        {
            Debug.LogWarning($"尝试记录空描述的事件: {gameObject.name}");
            return;
        }
        
        if (memoryModule == null)
        {
            Debug.LogError($"MemoryModule 为 null，无法记录事件: {description}");
            return;
        }
        
        try
        {
            memoryModule.AddMemory(description, type, importance);
            Debug.Log($"事件记录成功: {type} - {description} (重要性: {importance})");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"记录事件时出错: {e.Message}\n事件内容: {description}");
        }
    }
    
    /// <summary>
    /// 安全地开始决策过程
    /// </summary>
    public void StartDecisionProcess()
    {
        if (!isInitialized)
        {
            InitializeAgent();
        }
        
        if (actionDecisionModule != null)
        {
            StartCoroutine(actionDecisionModule.DecideNextAction());
        }
        else
        {
            Debug.LogError("ActionDecisionModule 未初始化，无法开始决策");
        }
    }
    
    /// <summary>
    /// 检查是否已正确初始化
    /// </summary>
    public bool IsInitialized()
    {
        return isInitialized && memoryModule != null;
    }
    
    /// <summary>
    /// 强制重新初始化
    /// </summary>
    public void Reinitialize()
    {
        isInitialized = false;
        InitializeAgent();
    }
}