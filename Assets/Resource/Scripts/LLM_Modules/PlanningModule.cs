// Scripts/Modules/PlanningModule.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;  // 确保 UnityEngine 在 System.Diagnostics 之前
using Newtonsoft.Json;
// 或者移除不必要的 using System.Diagnostics

[Serializable]
public class Plan
{
    public string mission;           // 总任务描述
    public MissionType missionType;  // 任务类型（用于任务级导航策略判定）
    public NavigationPolicy navigationPolicy; // 任务级默认导航策略（step 可覆盖）
    public RoleType agentRole;       // 本智能体在此任务中的角色（使用枚举）
    public string[] steps;           // 具体步骤
    public int currentStep;          // 当前步骤
    public DateTime created;         // 创建时间
    public Priority priority;        // 任务优先级
    public string assignedBy;        // 任务分配者
    public CommunicationMode commMode; // 通信模式
}

[Serializable]
public class MissionAssignment
{
    public string missionId;         // 任务ID
    public string missionDescription;// 任务描述
    public MissionType missionType;  // 任务类型（使用枚举）
    public string coordinatorId;     // 协调者ID
    public MissionRole[] roles;      // 需要的角色分配
    public CommunicationMode communicationMode; // 推荐通信模式
    public int requiredAgentCount;   // 需要智能体数量
}

[Serializable]
public class MissionRole
{
    public RoleType roleType;        // 角色类型（使用枚举）
    public AgentType agentType;      // 需要的智能体类型
    public int requiredCount;        // 需要数量
    public string[] responsibilities;// 职责描述
}

// 用于解析 LLM 响应的辅助类
[Serializable]
public class PlanResponse
{
    public string assignedRole;
    public string[] steps;
    public string[] coordinationNeeds;
    public string reasoning;
}

public enum Priority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

public class RolePreferenceWrapper
{
    public RoleType[] preferences;
}

public class PlanningModule : MonoBehaviour
{
    /// <summary>
    /// 判定“移动型 step”的关键词。
    /// 只要命中这些词，就说明该 step 可能需要位移类动作。
    /// </summary>
    private static readonly string[] MovementStepKeywords =
    {
        "前往", "到达", "移动", "赶往", "巡航", "运输", "追击", "搜索区域", "路径",
        "move", "go", "travel", "navigate", "route"
    };

    /// <summary>
    /// 判定“局部近场 step”的关键词。
    /// 命中后默认不建议使用全局 A*，优先近场机动/感知。
    /// </summary>
    private static readonly string[] LocalStepKeywords =
    {
        "附近", "周边", "就近", "本地", "近处", "当前区域"
    };

    /// <summary>
    /// 判定“通信/观察型 step”的关键词。
    /// 这类 step 一般不需要全局路径规划。
    /// </summary>
    private static readonly string[] CommunicationObservationKeywords =
    {
        "通信", "广播", "汇报", "报告", "发送", "同步", "等待", "观察", "扫描", "监控",
        "communicate", "report", "scan", "observe", "wait", "sync"
    };

    /// <summary>
    /// 判定“全局目标倾向”的关键词。
    /// 命中后表示目标更可能在远距离或跨区域，适合先走 A* 粗路径。
    /// </summary>
    private static readonly string[] GlobalTargetKeywords =
    {
        "基地", "建筑", "楼", "区域", "校门", "目的地", "远处", "跨区", "跨区域",
        "destination", "building", "zone", "global"
    };

    private MemoryModule memoryModule;
    private LLMInterface llmInterface;
    private AgentProperties agentProperties;
    private CommunicationModule commModule;

    public Plan currentPlan { get; private set; }
    public MissionAssignment currentMission { get; private set; }
    public CommunicationMode currentCommMode { get; private set; }

    // 只在协调者使用
    public Dictionary<RoleType, int> remainingCount = new();
    public Dictionary<string, RoleType[]> receivedPreferences = new();


    void Start()
    {
        memoryModule = GetComponent<MemoryModule>();
        llmInterface = FindObjectOfType<LLMInterface>();
        
        // 直接获取 IntelligentAgent 组件，然后访问其 Properties
        IntelligentAgent intelligentAgent = GetComponent<IntelligentAgent>();
        if (intelligentAgent != null)
        {
            agentProperties = intelligentAgent.Properties;
        }
        else
        {
            Debug.LogError("未找到 IntelligentAgent 组件");
        }
        
        commModule = GetComponent<CommunicationModule>();
        currentCommMode = CommunicationMode.Hybrid; // 默认混合模式
    }
    
    // 用户提交任务描述，由LLM分析生成任务分配
    public void SubmitMissionRequest(string missionDescription, int agentCount)
    {
        StartCoroutine(AnalyzeMissionDescription(missionDescription, agentCount));
    }

    // 接收中心化任务分配
    public void ReceiveMissionAssignment(MissionAssignment mission,RoleType? specificRole = null)
    {
        currentMission = mission;
        currentCommMode = mission.communicationMode;

        // 情况一：还没确定角色 —— 只分析角色偏好
        if (specificRole == null)
        {
            StartCoroutine(AnalyzeRolePreference(mission, (preferences) =>
            {
                // 把偏好发给协调者
                SendRolePreferenceToCoordinator(mission, preferences);
            }));
        }
        else
            StartCoroutine(AnalyzeMissionAndCreatePlan(mission, specificRole.Value));
        
    }
    private void SendRolePreferenceToCoordinator(MissionAssignment mission, RoleType[] preferences)
    {
        var msg = new AgentMessage
        {
            SenderID = agentProperties.AgentID,
            ReceiverID = mission.coordinatorId, 
            Type = MessageType.RolePreference,
            Priority = 1,
            Timestamp = Time.time,
            Content = JsonUtility.ToJson(
                new RolePreferenceWrapper { preferences = preferences }
            )
        };

        commModule.SendMessage(msg);
    }


    // 从LLM响应解析任务分配
    private MissionAssignment ParseMissionFromLLM(string llmResponse, string description, int agentCount)
    {
        // 简化解析，实际应使用JSON解析库
        MissionType missionType = ExtractMissionType(llmResponse);
        CommunicationMode commMode = ExtractCommMode(llmResponse);
        MissionRole[] roles = ExtractRolesFromResponse(llmResponse, agentCount, missionType);

        return new MissionAssignment
        {
            missionId = $"mission_{DateTime.Now:yyyyMMdd_HHmmss}",
            missionDescription = description,
            missionType = missionType,
            coordinatorId = agentProperties.AgentID, // 当前智能体作为协调者
            roles = roles,
            communicationMode = commMode,
            requiredAgentCount = agentCount
        };
    }
    // 阶段A：只分析角色偏好，用于提交给协调者
    public IEnumerator AnalyzeRolePreference(MissionAssignment mission,Action<RoleType[]> onResult)
    {
        string prompt = $@"任务分析请求：

        总任务描述：{mission.missionDescription}
        任务类型：{mission.missionType}

        我的属性：
        - 类型：{agentProperties.Type}
        - 当前角色倾向：{agentProperties.Role}
        - 最大速度：{agentProperties.MaxSpeed}
        - 感知范围：{agentProperties.PerceptionRange}

        可用角色：
        ";

            foreach (var role in mission.roles)
            {
                prompt += $@"
        - {role.roleType}（需要 {role.requiredCount} 名，类型 {AgentType.Quadcopter}）";//之后要把AgentType.Quadcopter改成role.agentType
            }

            prompt += @"

        请只完成一件事：
        给出我最适合的 1~3 个角色，按优先级排序。

        返回 JSON：
        {
        ""preferences"": [""Scout"", ""Assault""]
        }
        ";

        yield return llmInterface.SendRequest(prompt, (result) =>
        {
            Debug.Log($"=== 智能体 {agentProperties.AgentID} 收到 LLM 角色偏好原始响应 ===");
            Debug.Log($"原始响应内容: {result}");
            try
            {
                string json = ExtractPureJson(result);
                //Debug.Log($"提取的 JSON: {json}");
                var pref = JsonConvert.DeserializeObject<RolePreferenceWrapper>(json);
                // 正确打印解析结果
                if (pref != null)
                {
                   //Debug.Log($"解析成功 - 得到 RolePreferenceWrapper 对象");
                    
                    if (pref.preferences != null)
                    {
                       // Debug.Log($"preferences 数组长度: {pref.preferences.Length}");
                        
                        // 遍历并打印每个角色
                        for (int i = 0; i < pref.preferences.Length; i++)
                        {
                            //Debug.Log($"  偏好[{i}]: {pref.preferences[i]} (类型: {pref.preferences[i].GetType().Name})");
                        }
                        
                        //Debug.Log($"角色偏好列表: {string.Join(", ", pref.preferences)}");
                        onResult?.Invoke(pref.preferences);
                    }
                    else
                    {
                        Debug.LogWarning("preferences 数组为 null");
                        onResult?.Invoke(new RoleType[] { agentProperties.Role });
                    }
                }
                else
                {
                    Debug.LogWarning("解析结果为空或无效，使用默认角色");
                    onResult?.Invoke(new RoleType[] { agentProperties.Role });
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"角色偏好解析失败: {e.Message}");
                onResult?.Invoke(new RoleType[] { agentProperties.Role });
            }
        }, temperature: 0.3f, maxTokens: 120);
    }



    // 分析任务并创建具体计划
    public IEnumerator AnalyzeMissionAndCreatePlan(MissionAssignment mission, RoleType? specificRole = null)
    {
        string prompt = $@"任务分析请求：

        总任务描述：{mission.missionDescription}
        任务类型：{mission.missionType}
        推荐通信模式：{mission.communicationMode}
        我的属性：{agentProperties.Type}类型，角色：{agentProperties.Role}
        我的能力：最大速度{agentProperties.MaxSpeed}m/s，感知范围{agentProperties.PerceptionRange}m

        请分析：
        1. 基于我的角色，制定3-5个具体执行步骤
        2. 在此通信模式({mission.communicationMode})下如何与其他智能体协作？

        返回JSON格式：
        {{
            ""assignedRole"": ""角色名称"",
            ""steps"": [""步骤1"", ""步骤2"", ...],
            ""coordinationNeeds"": [""需要协调的事项1"", ...],
            ""reasoning"": ""选择理由""
        }}
        steps描述和coordinationNeeds描述需要尽可能的简单和简洁，但不能省略；
        ";

        yield return llmInterface.SendRequest(prompt, (result) =>
        {
            try
            {
                ParseAndCreatePlan(result, mission, specificRole);
            }
            catch (Exception e)
            {
                Debug.LogError($"任务分析失败: {e.Message}");
                CreateDefaultPlan(mission);
            }
        }, temperature: 0.3f, maxTokens: 200);
    }

    private void ParseAndCreatePlan(string llmResponse, MissionAssignment mission, RoleType? specificRole)
    {
        RoleType assignedRole = specificRole ?? ExtractRoleTypeFromResponse(llmResponse);
        string[] steps = ExtractStepsFromResponse(llmResponse);
        string reasoning = "LLM分析分配";
        NavigationPolicy navPolicy = ResolveNavigationPolicyByMissionType(mission.missionType);

        currentPlan = new Plan
        {
            mission = mission.missionDescription,
            missionType = mission.missionType,
            navigationPolicy = navPolicy,
            agentRole = assignedRole,
            steps = steps,
            currentStep = 0,
            created = DateTime.Now,
            priority = Priority.Normal,
            assignedBy = mission.coordinatorId,
            commMode = mission.communicationMode
        };

        // 打印 currentPlan 的详细内容
        Debug.Log($"=== 智能体 {agentProperties.AgentID} 创建计划详情 ===");
        Debug.Log($"任务描述: {currentPlan.mission}");
        Debug.Log($"任务类型: {currentPlan.missionType}");
        Debug.Log($"导航策略: {currentPlan.navigationPolicy}");
        Debug.Log($"分配角色: {currentPlan.agentRole}");
        Debug.Log($"协调者: {currentPlan.assignedBy}");
        Debug.Log($"通信模式: {currentPlan.commMode}");
        Debug.Log($"优先级: {currentPlan.priority}");
        Debug.Log($"创建时间: {currentPlan.created}");
        Debug.Log($"当前步骤: {currentPlan.currentStep}");
        Debug.Log($"总步骤数: {currentPlan.steps.Length}");
        
        // 打印所有步骤详情
        for (int i = 0; i < currentPlan.steps.Length; i++)
        {
            Debug.Log($"步骤 {i + 1}: {currentPlan.steps[i]}");
        }
        Debug.Log("=== 计划详情结束 ===");

        // 记录到记忆
        memoryModule.AddMemory($"接受任务：{mission.missionDescription}，担任角色：{assignedRole}，通信模式：{mission.communicationMode}",
                            "mission", 0.9f);

        // 向协调者确认角色接受
        SendRoleAcceptance(assignedRole.ToString(), reasoning);

        //Debug.Log($"智能体{agentProperties.AgentID}接受角色：{assignedRole}，步骤数：{steps.Length}，通信模式：{mission.communicationMode}");
    }

    // 向其他智能体分发任务（协调者功能）
    private void DistributeMissionToAgents(MissionAssignment mission)
    {
        Debug.Log($"DistributeMissionToAgents called by {agentProperties.AgentID}");
        if (commModule == null)
        {
            Debug.LogError("CommunicationModule 未找到，无法分发任务");
            return;
        }

        // 初始化名额
        remainingCount.Clear();
        receivedPreferences.Clear();
        foreach (var role in mission.roles)
        {
            remainingCount[role.roleType] = role.requiredCount;
        }

        // 广播任务
        var message = new AgentMessage
        {
            SenderID = agentProperties.AgentID,
            ReceiverID = "All",
            Type = MessageType.TaskAnnouncement,
            Priority = 2,
            Timestamp = Time.time,
            Content = JsonUtility.ToJson(mission)
        };

        commModule.SendMessage(message);

        // 等待智能体提交角色偏好
        StartCoroutine(WaitAndAssignRoles(mission));
    }
    private IEnumerator WaitAndAssignRoles(MissionAssignment mission)
    {
        // 等待 n秒收集请求（可调）
        yield return new WaitForSeconds(5.0f);

        foreach (var kv in receivedPreferences)
        {
            string agentId = kv.Key;
            RoleType[] prefs = kv.Value;

            foreach (var role in prefs)
            {
                if (remainingCount.TryGetValue(role, out int cnt) && cnt > 0)
                {
                    remainingCount[role]--;
                    SendFinalRole(agentId, role);
                    break;
                }
            }
        }

        //Debug.Log("角色裁决完成");
    }
    // 发送最终角色分配给智能体
    private void SendFinalRole(string agentId, RoleType role)
    {
        commModule.SendMessage(new AgentMessage
        {
            SenderID = agentProperties.AgentID,
            ReceiverID = agentId,
            Type = MessageType.RoleConfirmed,
            Content = role.ToString()
        });
    }



    // 修改角色分配逻辑部分
    private MissionRole[] ExtractRolesFromResponse(string response, int totalAgents, MissionType missionType)
    {
        List<MissionRole> roles = new List<MissionRole>();

        // 根据任务类型智能分配角色
        switch (missionType)
        {
            case MissionType.Competition:
                // 对抗任务：攻击+防御组合
                roles.Add(CreateRole(RoleType.Assault, AgentType.Quadcopter, Mathf.CeilToInt(totalAgents * 0.6f), 
                    new string[] { "进攻敌方目标", "突破防线" }));
                roles.Add(CreateRole(RoleType.Defender, AgentType.Quadcopter, Mathf.CeilToInt(totalAgents * 0.4f), 
                    new string[] { "防守己方目标", "拦截敌方" }));
                break;

            case MissionType.Exploration:
                // 探索任务：侦查为主
                roles.Add(CreateRole(RoleType.Scout, AgentType.Quadcopter, totalAgents, 
                    new string[] { "环境侦查", "信息收集", "地图构建" }));
                break;

            case MissionType.Pursuit:
                // 追击任务：侦查+攻击组合
                roles.Add(CreateRole(RoleType.Scout, AgentType.Quadcopter, Mathf.CeilToInt(totalAgents * 0.5f), 
                    new string[] { "追踪目标", "报告位置" }));
                roles.Add(CreateRole(RoleType.Assault, AgentType.WheeledRobot, Mathf.CeilToInt(totalAgents * 0.5f), 
                    new string[] { "拦截目标", "实施捕捉" }));
                break;

            case MissionType.Transport:
                // 运输任务：运输+侦查组合
                roles.Add(CreateRole(RoleType.Transporter, AgentType.WheeledRobot, Mathf.CeilToInt(totalAgents * 0.7f), 
                    new string[] { "物资运输", "路径规划" }));
                roles.Add(CreateRole(RoleType.Scout, AgentType.Quadcopter, Mathf.CeilToInt(totalAgents * 0.3f), 
                    new string[] { "路线侦查", "安全保障" }));
                break;

            case MissionType.SearchRescue:
                // 搜索救援：侦查+运输组合
                roles.Add(CreateRole(RoleType.Scout, AgentType.Quadcopter, Mathf.CeilToInt(totalAgents * 0.6f), 
                    new string[] { "搜索目标", "定位位置" }));
                roles.Add(CreateRole(RoleType.Transporter, AgentType.WheeledRobot, Mathf.CeilToInt(totalAgents * 0.4f), 
                    new string[] { "运输救援物资", "协助撤离" }));
                break;

            default:
                // 默认：侦查角色
                roles.Add(CreateRole(RoleType.Scout, AgentType.Quadcopter, totalAgents, 
                    new string[] { "执行分配任务", "协作完成目标" }));
                break;
        }

        return roles.ToArray();
    }

    // 修改任务类型提取逻辑
    private MissionType ExtractMissionType(string response)
    {
        if (response.Contains("Competition") || response.Contains("对抗") || response.Contains("竞争"))
            return MissionType.Competition;
        else if (response.Contains("Exploration") || response.Contains("探索") || response.Contains("侦查"))
            return MissionType.Exploration;
        else if (response.Contains("Pursuit") || response.Contains("追击") || response.Contains("追踪"))
            return MissionType.Pursuit;
        else if (response.Contains("Transport") || response.Contains("运输") || response.Contains("传递"))
            return MissionType.Transport;
        else if (response.Contains("SearchRescue") || response.Contains("搜索") || response.Contains("救援"))
            return MissionType.SearchRescue;
        else
            return MissionType.Exploration; // 默认探索任务
    }

    /// <summary>
    /// 根据 MissionType 解析“任务级默认导航策略”。
    /// 说明：
    /// 1) 这是 mission 级默认倾向，不代表每个 step 都要走 A*；
    /// 2) step 层会按“是否移动/是否近距离搜索/是否全局目标”再覆盖；
    /// 3) 该策略供 ActionDecisionModule 判定是否优先启用 A*。
    /// </summary>
    private NavigationPolicy ResolveNavigationPolicyByMissionType(MissionType missionType)
    {
        switch (missionType)
        {
            case MissionType.Transport:
            case MissionType.SearchRescue:
            case MissionType.Pursuit:
                return NavigationPolicy.PreferGlobalAStar;

            case MissionType.Exploration:
            case MissionType.Cooperation:
                return NavigationPolicy.PreferLocal;

            case MissionType.Competition:
            default:
                return NavigationPolicy.Auto;
        }
    }

    /// <summary>
    /// 对外接口：获取当前计划的任务级导航策略。
    /// </summary>
    public NavigationPolicy GetCurrentNavigationPolicy()
    {
        return currentPlan != null ? currentPlan.navigationPolicy : NavigationPolicy.Auto;
    }

    /// <summary>
    /// 对外接口：获取当前任务类型。
    /// 优先使用 currentPlan 的 missionType；若计划尚未创建，则回退到 currentMission。
    /// </summary>
    public MissionType GetCurrentMissionType()
    {
        if (currentPlan != null) return currentPlan.missionType;
        if (currentMission != null) return currentMission.missionType;
        return MissionType.Cooperation;
    }

    /// <summary>
    /// 对外接口：获取当前 step 文本。
    /// 若无计划或 step 已结束，返回空字符串。
    /// </summary>
    public string GetCurrentStepDescription()
    {
        if (currentPlan == null || currentPlan.steps == null) return string.Empty;
        if (currentPlan.currentStep < 0 || currentPlan.currentStep >= currentPlan.steps.Length) return string.Empty;
        return currentPlan.steps[currentPlan.currentStep] ?? string.Empty;
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否是“移动型 step”。
    /// 规则：
    /// 1) 命中通信/观察关键词，直接判为非移动型；
    /// 2) 否则只要命中移动关键词，即判为移动型。
    /// </summary>
    public bool IsMovementLikeStep(string stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText)) return false;
        if (ContainsAnyKeyword(stepText, CommunicationObservationKeywords)) return false;
        return ContainsAnyKeyword(stepText, MovementStepKeywords);
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否是“通信或观察型 step”。
    /// </summary>
    public bool IsCommunicationOrObservationStep(string stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText)) return false;
        return ContainsAnyKeyword(stepText, CommunicationObservationKeywords);
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否是“局部近场 step”。
    /// 典型例子：附近搜索、就近交互、当前区域扫描。
    /// </summary>
    public bool IsLikelyLocalStep(string stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText)) return false;
        return ContainsAnyKeyword(stepText, LocalStepKeywords);
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否带有“全局目标”提示。
    /// 仅用于在 PreferLocal/Auto 策略下做覆盖判断。
    /// </summary>
    public bool HasGlobalTargetHint(string stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText)) return false;
        return ContainsAnyKeyword(stepText, GlobalTargetKeywords);
    }

    /// <summary>
    /// 对外接口：给出“当前 step 是否建议优先使用 A*”的结论。
    /// 判定顺序：
    /// 1) 非移动 step / 通信观察 step -> 不用 A*；
    /// 2) 局部近场 step -> 不用 A*；
    /// 3) 再结合任务级策略（NavigationPolicy）决定：
    ///    - PreferGlobalAStar: 直接建议使用 A*；
    ///    - PreferLocal: 仅当有全局目标提示时使用 A*；
    ///    - Auto: 仅当有全局目标提示时使用 A*。
    /// </summary>
    public bool ShouldPreferAStarForStep(string stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText)) return false;
        if (!IsMovementLikeStep(stepText)) return false;
        if (IsCommunicationOrObservationStep(stepText)) return false;
        if (IsLikelyLocalStep(stepText)) return false;

        NavigationPolicy policy = GetCurrentNavigationPolicy();
        if (policy == NavigationPolicy.PreferGlobalAStar) return true;
        if (policy == NavigationPolicy.PreferLocal) return HasGlobalTargetHint(stepText);
        return HasGlobalTargetHint(stepText);
    }

    /// <summary>
    /// 对外接口：基于“当前正在执行的 step”直接判断是否建议 A*。
    /// </summary>
    public bool ShouldPreferAStarForCurrentStep()
    {
        return ShouldPreferAStarForStep(GetCurrentStepDescription());
    }

    /// <summary>
    /// 工具函数：判定文本是否包含任意关键词（忽略大小写）。
    /// </summary>
    private static bool ContainsAnyKeyword(string text, string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(text) || keywords == null || keywords.Length == 0) return false;
        for (int i = 0; i < keywords.Length; i++)
        {
            string keyword = keywords[i];
            if (string.IsNullOrWhiteSpace(keyword)) continue;
            if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // 从LLM响应中提取通信模式
    private CommunicationMode ExtractCommMode(string response)
    {
        foreach (CommunicationMode mode in Enum.GetValues(typeof(CommunicationMode)))
        {
            if (response.Contains(mode.ToString()))
                return mode;
        }
        return CommunicationMode.Hybrid; // 默认混合模式
    }

    // 默认计划创建逻辑
    private void CreateDefaultPlan(MissionAssignment mission)
    {
        // 默认计划基于角色类型
        string[] defaultSteps = mission.roles[0].roleType switch
        {
            RoleType.Scout => new string[] {
                "起飞并爬升到侦查高度",
                "扫描任务区域环境",
                "识别和标记关键目标",
                "持续报告环境信息",
                "任务完成后返回"
            },
            RoleType.Assault => new string[] {
                "检查设备和武器系统",
                "向目标区域快速移动",
                "执行攻击或突破任务",
                "保持战术队形",
                "任务完成后撤离"
            },
            RoleType.Defender => new string[] {
                "建立防御阵地",
                "监控周围环境",
                "拦截威胁目标",
                "保护重要区域",
                "维持防御状态"
            },
            RoleType.Transporter => new string[] {
                "检查载货状态",
                "规划运输路线",
                "向目的地移动",
                "执行装卸操作",
                "返回基地或下一个任务点"
            },
            _ => new string[] { "待命", "等待具体指令", "执行分配任务" }
        };

        currentPlan = new Plan
        {
            mission = mission.missionDescription,
            missionType = mission.missionType,
            navigationPolicy = ResolveNavigationPolicyByMissionType(mission.missionType),
            agentRole = mission.roles[0].roleType,
            steps = defaultSteps,
            currentStep = 0,
            created = DateTime.Now,
            priority = Priority.Normal,
            assignedBy = mission.coordinatorId,
            commMode = mission.communicationMode
        };
    }

    //创建任务接口，利用llm结构化；
    public IEnumerator AnalyzeMissionDescription(string description, int agentCount)
    {
        string prompt = $@"任务分析请求：

        用户任务描述：{description}
        可用智能体数量：{agentCount}

        请分析这个任务并返回JSON格式的结构化任务分配:

        {{
            ""missionType"": ""任务类型(Competition/Exploration/Pursuit/Transport/SearchRescue)"",
            ""recommendedCommMode"": ""通信模式(Centralized/Decentralized/Hybrid)"",
            ""roles"": [
                {{
                    ""roleType"": ""角色类型(Scout/Assault/Defender/Transporter)"",
                    ""agentType"": ""智能体类型(Quadcopter/WheeledRobot)"",
                    ""requiredCount"": 数量,
                    ""responsibilities"": [""职责1"", ""职责2""]
                }}
            ],
            ""reasoning"": ""分配理由""
        }}";

        yield return llmInterface.SendRequest(prompt, (result) =>
        {
            try
            {
                MissionAssignment mission = ParseMissionFromLLM(result, description, agentCount);
                // 如果是第一个接收任务的智能体，自动成为协调者
               
                Debug.Log($"{agentProperties.AgentID} 成为任务协调者");
                mission.coordinatorId = agentProperties.AgentID;
                DistributeMissionToAgents(mission);

                ReceiveMissionAssignment(mission);
            }
            catch (Exception e)
            {
                Debug.LogError($"任务分析失败: {e.Message}");
                CreateDefaultMission(description, agentCount);
            }
        }, temperature: 0.3f, maxTokens: 250);
    }

    // 从LLM响应中提取步骤
    private string[] ExtractStepsFromResponse(string response)
    {
        try
        {
            // 提取纯 JSON 部分
            string jsonContent = ExtractPureJson(response);
            
            if (!string.IsNullOrEmpty(jsonContent))
            {
                // 直接解析 JSON
                var planResponse = JsonUtility.FromJson<PlanResponse>(jsonContent);
                
                if (planResponse?.steps != null && planResponse.steps.Length > 0)
                {
                    Debug.Log($"成功解析出 {planResponse.steps.Length} 个步骤");
                    return planResponse.steps;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"步骤解析失败: {e.Message}");
        }
        
        // 如果解析失败，返回默认步骤
        return new string[] { "分析环境", "执行任务", "报告状态" };
    }

    // 从响应中提取纯 JSON 内容
    private string ExtractPureJson(string response)
    {
        if (string.IsNullOrEmpty(response))
            return response;

        // 如果包含代码块标记，提取其中的内容
        if (response.Contains("```json"))
        {
            int jsonStart = response.IndexOf("```json") + 7;
            int jsonEnd = response.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                return response.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }
        
        // 如果包含普通的代码块标记
        if (response.Contains("```"))
        {
            int jsonStart = response.IndexOf("```") + 3;
            int jsonEnd = response.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                return response.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }
        
        // 如果没有代码块标记，直接返回整个响应（可能是纯 JSON）
        return response.Trim();
    }
    // 从LLM响应中提取角色
    private RoleType ExtractRoleTypeFromResponse(string response)
    {
        try
        {
            // 提取纯 JSON 部分
            string jsonContent = ExtractPureJson(response);
            
            if (!string.IsNullOrEmpty(jsonContent))
            {
                var planResponse = JsonUtility.FromJson<PlanResponse>(jsonContent);
                if (!string.IsNullOrEmpty(planResponse?.assignedRole))
                {
                    if (System.Enum.TryParse<RoleType>(planResponse.assignedRole, out RoleType role))
                    {
                        return role;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"角色解析失败: {e.Message}");
        }
        
        // 备用逻辑
        return agentProperties.Type switch
        {
            AgentType.Quadcopter => RoleType.Scout,
            AgentType.WheeledRobot => RoleType.Transporter,
            _ => RoleType.Scout
        };
    }
    // 创建角色辅助方法
    private MissionRole CreateRole(RoleType roleType, AgentType agentType, int count, string[] responsibilities)
    {
        return new MissionRole
        {
            roleType = roleType,
            agentType = agentType,
            requiredCount = count,
            responsibilities = responsibilities
        };
    }

    private void CreateDefaultMission(string description, int agentCount)
    {
        // 创建默认任务分配
        currentMission = new MissionAssignment
        {
            missionId = $"mission_default_{DateTime.Now:yyyyMMdd_HHmmss}",
            missionDescription = description,
            missionType = MissionType.Cooperation,
            coordinatorId = agentProperties.AgentID,
            roles = new MissionRole[] {
                CreateRole(RoleType.Supporter, AgentType.Quadcopter, agentCount, 
                    new string[] { "执行合作任务", "完成用户指令" })
            },
            communicationMode = CommunicationMode.Hybrid,
            requiredAgentCount = agentCount
        };

        CreateDefaultPlan(currentMission);
    }

    // 发送角色接受确认
    private void SendRoleAcceptance(string role, string reasoning)
    {
        if (commModule != null && currentMission != null)
        {
            var message = new AgentMessage
            {
                SenderID = agentProperties.AgentID,
                ReceiverID = currentMission.coordinatorId,
                Type = MessageType.RoleAssignment,
                Priority = 1,
                Timestamp = Time.time,
                Content = $"{{\"acceptedRole\":\"{role}\",\"reasoning\":\"{reasoning}\",\"capabilities\":\"{agentProperties.Role}-{agentProperties.Type}\"}}"
            };

            commModule.SendMessage(message);
        }
    }

    // 获取当前任务
    public Plan GetCurrentTask()
    {
        if (currentPlan == null || currentPlan.currentStep >= currentPlan.steps.Length)
            return new Plan { mission = "无任务", steps = new string[0], currentStep = 0 };

        return currentPlan;
    }

    // 获取当前任务进度
    public string GetMissionProgress()
    {
        if (currentPlan == null) return "无任务";

        return $"{currentPlan.agentRole} - 步骤 {currentPlan.currentStep + 1}/{currentPlan.steps.Length}";
    }

    // 标记当前任务完成
    public void CompleteCurrentTask()
    {
        if (currentPlan != null && currentPlan.currentStep < currentPlan.steps.Length)
        {
            string completedStep = currentPlan.steps[currentPlan.currentStep];
            memoryModule.AddMemory($"完成步骤: {completedStep}", "progress", 0.7f);
            currentPlan.currentStep++;

            // 如果是最后一步，报告任务完成
            if (currentPlan.currentStep >= currentPlan.steps.Length)
            {
                memoryModule.AddMemory($"完成任务: {currentPlan.mission}", "achievement", 0.9f);
                ReportMissionCompletion();
            }
            else
            {
                // 报告步骤完成
                ReportStepCompletion(completedStep);
            }
        }
    }

    // 报告步骤完成
    private void ReportStepCompletion(string step)
    {
        if (commModule != null && currentMission != null)
        {
            var message = new AgentMessage
            {
                SenderID = agentProperties.AgentID,
                ReceiverID = currentMission.coordinatorId,
                Type = MessageType.TaskUpdate,
                Priority = 1,
                Timestamp = Time.time,
                Content = $"{{\"completedStep\":\"{step}\",\"nextStep\":\"{GetCurrentTask()}\",\"progress\":\"{currentPlan.currentStep}/{currentPlan.steps.Length}\"}}"
            };

            commModule.SendMessage(message);
        }
    }

    // 报告任务完成
    private void ReportMissionCompletion()
    {
        if (commModule != null && currentMission != null)
        {
            var message = new AgentMessage
            {
                SenderID = agentProperties.AgentID,
                ReceiverID = currentMission.coordinatorId,
                Type = MessageType.TaskCompletion,
                Priority = 1,
                Timestamp = Time.time,
                Content = $"{{\"mission\":\"{currentPlan.mission}\",\"role\":\"{currentPlan.agentRole}\",\"status\":\"completed\"}}"
            };

            commModule.SendMessage(message);
        }
    }

    // 检查是否有活跃任务
    public bool HasActiveMission()
    {
        return currentPlan != null && currentPlan.currentStep < currentPlan.steps.Length;
    }

    // 获取任务优先级
    public Priority GetMissionPriority()
    {
        return currentPlan?.priority ?? Priority.Low;
    }
}

// 示例用法：
// // 中心协调者发送任务
// var mission = new MissionAssignment {
//     missionId = "mission_001",
//     missionDescription = "所有无人机分成两组进行区域对抗，红队防守基地，蓝队进攻",
//     missionType = "对抗任务",
//     coordinatorId = "CentralCommand",
//     roles = new MissionRole[] {
//         new MissionRole { 
//             roleName = "攻击手", 
//             agentType = "Quadcopter", 
//             responsibilities = new string[] { "突破防线", "攻击目标" } 
//         },
//         new MissionRole { 
//             roleName = "侦查员", 
//             agentType = "Quadcopter", 
//             responsibilities = new string[] { "侦查敌情", "报告位置" } 
//         }
//     }
// };

// // 智能体接收任务
// planningModule.ReceiveMissionAssignment(mission);
