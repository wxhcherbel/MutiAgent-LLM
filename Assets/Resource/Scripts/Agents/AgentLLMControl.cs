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
            //Debug.Log($"为 {gameObject.name} 添加 MemoryModule 组件");
        }
        
        reflectionModule = GetComponent<ReflectionModule>();
        if (reflectionModule == null)
        {
            reflectionModule = gameObject.AddComponent<ReflectionModule>();
            //Debug.Log($"为 {gameObject.name} 添加 ReflectionModule 组件");
        }
        
        planningModule = GetComponent<PlanningModule>();
        if (planningModule == null)
        {
            planningModule = gameObject.AddComponent<PlanningModule>();
            //Debug.Log($"为 {gameObject.name} 添加 PlanningModule 组件");
        }
        
        actionDecisionModule = GetComponent<ActionDecisionModule>();
        if (actionDecisionModule == null)
        {
            actionDecisionModule = gameObject.AddComponent<ActionDecisionModule>();
            //Debug.Log($"为 {gameObject.name} 添加 ActionDecisionModule 组件");
        }
        
        
        isInitialized = true;

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