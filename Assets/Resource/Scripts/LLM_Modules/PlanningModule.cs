// Scripts/Modules/PlanningModule.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;  // 确保 UnityEngine 在 System.Diagnostics 之前
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
// 或者移除不必要的 using System.Diagnostics

[Serializable]
public class Plan
{
    public string mission;           // 总任务描述
    public MissionType missionType;  // 任务类型
    public NavigationPolicy navigationPolicy; // 任务级默认导航策略（由 LLM 输出，step 可覆盖）
    public RoleType agentRole;       // 本智能体在此任务中的角色（使用枚举）
    public string[] steps;           // 具体步骤
    public string[] stepActionTypes; // 每步动作意图（Move/Observe/Communicate/Interact/Idle）
    public string[] stepNavigationModes; // 每步导航模式（AStar/Direct/None）
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
    public string[] stepActionTypes;
    public string[] stepNavigationModes;
    public string missionNavigationPolicy;
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
        string prompt = $@"你是多智能体任务规划器，请根据输入给当前智能体生成精简执行计划。

        [输入]
        - mission: {mission.missionDescription}
        - missionType: {mission.missionType}
        - communicationMode: {mission.communicationMode}
        - selfType: {agentProperties.Type}
        - selfRolePreference: {agentProperties.Role}
        - maxSpeed: {agentProperties.MaxSpeed:F1}
        - perceptionRange: {agentProperties.PerceptionRange:F1}

        [硬性规则]
        1) 只能输出一个JSON对象，不要Markdown，不要解释文字。
        2) assignedRole 只能取: Supporter|Scout|Assault|Defender|Transporter。
        3) steps 数量 3-5 条；每条一句、简短、可执行，建议 <= 22 字（或 <= 12 英文词）。
        4) stepActionTypes 必须与 steps 等长，每项只能取: Move|Observe|Communicate|Interact|Idle。
        5) stepNavigationModes 必须与 steps 等长，每项只能取: AStar|Direct|None。
        6) 仅当该步骤需要全局路径规划时才标记 AStar；通信/观察步骤标记 None。
        7) coordinationNeeds 数量 1-3 条；每条简短，建议 <= 20 字（或 <= 10 英文词）。
        8) reasoning 必须很短，<= 40 字（或 <= 20 英文词），禁止换行。
        9) 若信息不足，仍要给出可执行默认步骤，不能返回空数组。

        [输出模板]
        {{
        ""assignedRole"": ""Scout"",
        ""steps"": [""步骤1"", ""步骤2"", ""步骤3""],
        ""stepActionTypes"": [""Move"", ""Observe"", ""Move""],
        ""stepNavigationModes"": [""AStar"", ""None"", ""Direct""],
        ""missionNavigationPolicy"": ""Auto"",
        ""coordinationNeeds"": [""协作点1""],
        ""reasoning"": ""简短理由""
        }}";

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
        }, temperature: 0.2f, maxTokens: 220);
    }

    private void ParseAndCreatePlan(string llmResponse, MissionAssignment mission, RoleType? specificRole)
    {
        RoleType assignedRole = specificRole ?? ExtractRoleTypeFromResponse(llmResponse);
        string[] steps = ExtractStepsFromResponse(llmResponse);
        string reasoning = "LLM分析分配";
        string[] stepActionTypes = ExtractStepActionTypesFromResponse(llmResponse, steps.Length);
        string[] stepNavigationModes = ExtractStepNavigationModesFromResponse(llmResponse, steps.Length);
        NavigationPolicy navPolicy = ExtractMissionNavigationPolicyFromResponse(llmResponse);

        currentPlan = new Plan
        {
            mission = mission.missionDescription,
            missionType = mission.missionType,
            navigationPolicy = navPolicy,
            agentRole = assignedRole,
            steps = steps,
            stepActionTypes = stepActionTypes,
            stepNavigationModes = stepNavigationModes,
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
            string intent = (currentPlan.stepActionTypes != null && i < currentPlan.stepActionTypes.Length) ? currentPlan.stepActionTypes[i] : "NA";
            string nav = (currentPlan.stepNavigationModes != null && i < currentPlan.stepNavigationModes.Length) ? currentPlan.stepNavigationModes[i] : "NA";
            Debug.Log($"步骤 {i + 1}: {currentPlan.steps[i]} | intent={intent} | nav={nav}");
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
    /// 仅依据 LLM 输出的 stepActionTypes/stepNavigationModes。
    /// </summary>
    public bool IsMovementLikeStep(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;

        string actionType = GetStepActionTypeHint(idx);
        if (actionType == "Move") return true;

        string navMode = GetStepNavigationModeHint(idx);
        return navMode == "AStar" || navMode == "Direct";
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否是“通信或观察型 step”。
    /// 仅依据 LLM 输出的 stepActionTypes。
    /// </summary>
    public bool IsCommunicationOrObservationStep(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;
        string actionType = GetStepActionTypeHint(idx);
        return actionType == "Communicate" || actionType == "Observe";
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否是“局部机动 step”。
    /// 仅依据 LLM 输出的 stepNavigationModes=Direct。
    /// </summary>
    public bool IsLikelyLocalStep(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;
        return GetStepNavigationModeHint(idx) == "Direct";
    }

    /// <summary>
    /// 对外接口：判断某个 step 是否带有“全局导航提示”。
    /// 仅依据 LLM 输出的 stepNavigationModes=AStar。
    /// </summary>
    public bool HasGlobalTargetHint(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;
        return GetStepNavigationModeHint(idx) == "AStar";
    }

    /// <summary>
    /// 对外接口：当前 step 是否建议优先使用 A*。
    /// 仅依据 LLM 输出的 stepNavigationModes，不再进行自然语言关键词推断。
    /// </summary>
    public bool ShouldPreferAStarForStep(string stepText)
    {
        int idx = ResolveStepIndex(stepText);
        if (idx < 0) return false;
        return GetStepNavigationModeHint(idx) == "AStar";
    }

    /// <summary>
    /// 对外接口：基于“当前正在执行的 step”直接判断是否建议 A*。
    /// </summary>
    public bool ShouldPreferAStarForCurrentStep()
    {
        return ShouldPreferAStarForStep(GetCurrentStepDescription());
    }

    /// <summary>
    /// 将 step 文本映射到当前计划中的索引。
    /// </summary>
    private int ResolveStepIndex(string stepText)
    {
        if (currentPlan == null || currentPlan.steps == null || currentPlan.steps.Length == 0) return -1;

        int cur = currentPlan.currentStep;
        if (cur >= 0 && cur < currentPlan.steps.Length)
        {
            string curText = currentPlan.steps[cur] ?? string.Empty;
            if (string.Equals(curText.Trim(), (stepText ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return cur;
            }
        }

        if (!string.IsNullOrWhiteSpace(stepText))
        {
            for (int i = 0; i < currentPlan.steps.Length; i++)
            {
                string s = currentPlan.steps[i] ?? string.Empty;
                if (string.Equals(s.Trim(), stepText.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (cur >= 0 && cur < currentPlan.steps.Length) return cur;
        return -1;
    }

    /// <summary>
    /// 获取某一步的动作意图（Move/Observe/Communicate/Interact/Idle）。
    /// </summary>
    private string GetStepActionTypeHint(int stepIndex)
    {
        if (currentPlan == null || currentPlan.stepActionTypes == null) return "Idle";
        if (stepIndex < 0 || stepIndex >= currentPlan.stepActionTypes.Length) return "Idle";
        return NormalizeStepActionTypeToken(currentPlan.stepActionTypes[stepIndex]);
    }

    /// <summary>
    /// 获取某一步的导航模式（AStar/Direct/None）。
    /// </summary>
    private string GetStepNavigationModeHint(int stepIndex)
    {
        if (currentPlan == null || currentPlan.stepNavigationModes == null) return "None";
        if (stepIndex < 0 || stepIndex >= currentPlan.stepNavigationModes.Length) return "None";
        return NormalizeStepNavigationModeToken(currentPlan.stepNavigationModes[stepIndex]);
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
            navigationPolicy = NavigationPolicy.Auto,
            agentRole = mission.roles[0].roleType,
            steps = defaultSteps,
            stepActionTypes = BuildFilledArray(defaultSteps.Length, "Move"),
            stepNavigationModes = BuildFilledArray(defaultSteps.Length, "Direct"),
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
            string parsedJson;
            string parseError;
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out parsedJson, out parseError))
            {
                if (planResponse != null && planResponse.steps != null && planResponse.steps.Length > 0)
                {
                    Debug.Log($"成功解析出 {planResponse.steps.Length} 个步骤");
                    return planResponse.steps;
                }
            }
            else if (!string.IsNullOrWhiteSpace(parseError))
            {
                Debug.LogWarning($"步骤解析未命中结构化JSON，原因: {parseError}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"步骤解析失败: {e.Message}");
        }
        
        // 如果解析失败，返回默认步骤
        return new string[] { "分析环境", "执行任务", "报告状态" };
    }

    /// <summary>
    /// 从 LLM 响应提取每步动作意图（由 LLM 明确给出，不做自然语言关键词判断）。
    /// </summary>
    private string[] ExtractStepActionTypesFromResponse(string response, int stepCount)
    {
        int n = Mathf.Max(0, stepCount);
        string[] fallback = BuildFilledArray(n, "Move");
        if (n == 0) return fallback;

        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _))
            {
                if (planResponse != null && planResponse.stepActionTypes != null && planResponse.stepActionTypes.Length > 0)
                {
                    string[] raw = planResponse.stepActionTypes;
                    string[] result = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        string token = i < raw.Length ? raw[i] : raw[raw.Length - 1];
                        result[i] = NormalizeStepActionTypeToken(token);
                    }
                    return result;
                }
            }
        }
        catch { }

        return fallback;
    }

    /// <summary>
    /// 从 LLM 响应提取每步导航模式（由 LLM 明确给出，不做自然语言关键词判断）。
    /// </summary>
    private string[] ExtractStepNavigationModesFromResponse(string response, int stepCount)
    {
        int n = Mathf.Max(0, stepCount);
        string[] fallback = BuildFilledArray(n, "Direct");
        if (n == 0) return fallback;

        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _))
            {
                if (planResponse != null && planResponse.stepNavigationModes != null && planResponse.stepNavigationModes.Length > 0)
                {
                    string[] raw = planResponse.stepNavigationModes;
                    string[] result = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        string token = i < raw.Length ? raw[i] : raw[raw.Length - 1];
                        result[i] = NormalizeStepNavigationModeToken(token);
                    }
                    return result;
                }
            }
        }
        catch { }

        return fallback;
    }

    /// <summary>
    /// 任务级导航策略由 LLM 给出；若缺失则统一回退 Auto。
    /// </summary>
    private NavigationPolicy ExtractMissionNavigationPolicyFromResponse(string response)
    {
        try
        {
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out _, out _))
            {
                string token = planResponse != null ? planResponse.missionNavigationPolicy : string.Empty;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    string n = token.Trim().ToLowerInvariant();
                    if (n == "preferglobalastar" || n == "globalastar" || n == "astar") return NavigationPolicy.PreferGlobalAStar;
                    if (n == "preferlocal" || n == "local" || n == "direct") return NavigationPolicy.PreferLocal;
                    if (n == "auto") return NavigationPolicy.Auto;
                }
            }
        }
        catch { }

        return NavigationPolicy.Auto;
    }

    private static string[] BuildFilledArray(int count, string value)
    {
        if (count <= 0) return new string[0];
        string[] arr = new string[count];
        for (int i = 0; i < count; i++) arr[i] = value;
        return arr;
    }

    private static string NormalizeStepActionTypeToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Idle";
        string s = raw.Trim().ToLowerInvariant();
        if (s == "move" || s == "navigation" || s == "navigate") return "Move";
        if (s == "observe" || s == "scan" || s == "sensing") return "Observe";
        if (s == "communicate" || s == "communication" || s == "comm") return "Communicate";
        if (s == "interact" || s == "interaction" || s == "pickup" || s == "drop") return "Interact";
        if (s == "idle" || s == "none") return "Idle";
        return "Idle";
    }

    private static string NormalizeStepNavigationModeToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "None";
        string s = raw.Trim().ToLowerInvariant();
        if (s == "astar" || s == "globalastar" || s == "global") return "AStar";
        if (s == "direct" || s == "local" || s == "reactive") return "Direct";
        if (s == "none" || s == "na" || s == "n/a") return "None";
        return "None";
    }

    // 从响应中提取纯 JSON 内容
    private string ExtractPureJson(string response)
    {
        if (string.IsNullOrEmpty(response))
            return response;

        string text = response.Trim();

        // 如果包含代码块标记，提取其中的内容
        if (text.Contains("```json"))
        {
            int jsonStart = text.IndexOf("```json", StringComparison.Ordinal) + 7;
            int jsonEnd = text.IndexOf("```", jsonStart, StringComparison.Ordinal);
            if (jsonEnd > jsonStart)
            {
                text = text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }
        else if (text.Contains("```"))
        {
            // 如果包含普通的代码块标记
            int jsonStart = text.IndexOf("```", StringComparison.Ordinal) + 3;
            int jsonEnd = text.IndexOf("```", jsonStart, StringComparison.Ordinal);
            if (jsonEnd > jsonStart)
            {
                text = text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }

        // 从文本中截取首个完整 JSON 对象/数组，避免前后解释文本污染解析。
        int objStart = text.IndexOf('{');
        int arrStart = text.IndexOf('[');
        int start = -1;

        if (objStart >= 0 && arrStart >= 0) start = Mathf.Min(objStart, arrStart);
        else if (objStart >= 0) start = objStart;
        else if (arrStart >= 0) start = arrStart;

        if (start < 0) return text;

        char open = text[start];
        char close = open == '{' ? '}' : ']';

        int depth = 0;
        bool inString = false;
        char quote = '\0';
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == quote)
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                quote = c;
                continue;
            }

            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1).Trim();
                }
            }
        }

        // 若未找到闭合，至少返回从首个 JSON 起始处开始的内容，供后续容错解析尝试。
        return text.Substring(start).Trim();
    }
    // 从LLM响应中提取角色
    private RoleType ExtractRoleTypeFromResponse(string response)
    {
        try
        {
            string parsedJson;
            string parseError;
            if (TryParsePlanResponse(response, out PlanResponse planResponse, out parsedJson, out parseError))
            {
                if (!string.IsNullOrEmpty(planResponse?.assignedRole))
                {
                    if (System.Enum.TryParse<RoleType>(planResponse.assignedRole, out RoleType role))
                    {
                        return role;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(parseError))
            {
                Debug.LogWarning($"角色解析未命中结构化JSON，原因: {parseError}");
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

    /// <summary>
    /// 统一解析计划响应（鲁棒版）：
    /// 1) 自动从 LLM 文本里提取首个 JSON 对象/数组；
    /// 2) 优先按对象反序列化 PlanResponse；
    /// 3) 兼容仅返回步骤数组的情况。
    /// </summary>
    private bool TryParsePlanResponse(string response, out PlanResponse planResponse, out string normalizedJson, out string error)
    {
        planResponse = null;
        normalizedJson = string.Empty;
        error = string.Empty;

        string jsonContent = ExtractPureJson(response);
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            error = "提取到的JSON为空";
            return false;
        }

        normalizedJson = jsonContent.Trim();

        try
        {
            if (normalizedJson.StartsWith("{"))
            {
                planResponse = JsonConvert.DeserializeObject<PlanResponse>(normalizedJson);
                if (planResponse != null)
                {
                    JObject obj = JObject.Parse(normalizedJson);

                    if (string.IsNullOrWhiteSpace(planResponse.assignedRole))
                    {
                        // 兼容 role 字段命名差异
                        planResponse.assignedRole = (string)obj["assignedRole"] ?? (string)obj["role"] ?? string.Empty;
                    }

                    if (planResponse.steps == null || planResponse.steps.Length == 0)
                    {
                        JToken token = obj["steps"] ?? obj["planSteps"] ?? obj["actions"];
                        if (token is JArray arr)
                        {
                            var list = new List<string>();
                            for (int i = 0; i < arr.Count; i++)
                            {
                                string s = arr[i]?.ToString();
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                            }
                            planResponse.steps = list.ToArray();
                        }
                    }

                    if (planResponse.stepActionTypes == null || planResponse.stepActionTypes.Length == 0)
                    {
                        JToken token = obj["stepActionTypes"] ?? obj["stepIntents"] ?? obj["actionTypes"];
                        if (token is JArray arr)
                        {
                            var list = new List<string>();
                            for (int i = 0; i < arr.Count; i++)
                            {
                                string s = arr[i]?.ToString();
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                            }
                            planResponse.stepActionTypes = list.ToArray();
                        }
                    }

                    if (planResponse.stepNavigationModes == null || planResponse.stepNavigationModes.Length == 0)
                    {
                        JToken token = obj["stepNavigationModes"] ?? obj["stepNavModes"] ?? obj["stepNavigation"];
                        if (token is JArray arr)
                        {
                            var list = new List<string>();
                            for (int i = 0; i < arr.Count; i++)
                            {
                                string s = arr[i]?.ToString();
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                            }
                            planResponse.stepNavigationModes = list.ToArray();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(planResponse.missionNavigationPolicy))
                    {
                        planResponse.missionNavigationPolicy =
                            (string)obj["missionNavigationPolicy"] ??
                            (string)obj["navigationPolicy"] ??
                            string.Empty;
                    }

                    return true;
                }
            }
            else if (normalizedJson.StartsWith("["))
            {
                // 兼容直接返回 ["step1","step2"] 的情况
                JArray arr = JArray.Parse(normalizedJson);
                var list = new List<string>();
                for (int i = 0; i < arr.Count; i++)
                {
                    string s = arr[i]?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                }

                planResponse = new PlanResponse
                {
                    assignedRole = string.Empty,
                    steps = list.ToArray()
                };
                return list.Count > 0;
            }

            if (TryParsePlanResponseLoosely(normalizedJson, out planResponse))
            {
                error = "JSON结构不标准，已使用容错解析";
                Debug.LogWarning($"计划响应使用容错解析: {error}");
                return true;
            }

            error = "JSON不是对象或数组";
            return false;
        }
        catch (Exception ex)
        {
            if (TryParsePlanResponseLoosely(normalizedJson, out planResponse))
            {
                error = $"标准JSON解析失败({ex.Message})，已回退容错解析";
                Debug.LogWarning($"计划响应使用容错解析: {error}");
                return true;
            }

            error = ex.Message;
            Debug.LogError($"计划响应解析失败: {error}\n原始片段: {normalizedJson}");
            return false;
        }
    }

    /// <summary>
    /// 非严格 JSON 容错解析：
    /// 即使 response 被截断（如 reasoning 未闭合），也尽量提取 assignedRole 与 steps。
    /// </summary>
    private bool TryParsePlanResponseLoosely(string raw, out PlanResponse planResponse)
    {
        planResponse = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        bool gotRole = TryExtractStringFieldLoose(raw, "assignedRole", out string role);
        if (!gotRole)
        {
            TryExtractStringFieldLoose(raw, "role", out role);
        }

        bool gotSteps = TryExtractStringArrayFieldLoose(raw, "steps", out string[] steps);
        if (!gotSteps)
        {
            gotSteps = TryExtractStringArrayFieldLoose(raw, "planSteps", out steps);
        }
        if (!gotSteps)
        {
            gotSteps = TryExtractStringArrayFieldLoose(raw, "actions", out steps);
        }

        bool gotActionTypes = TryExtractStringArrayFieldLoose(raw, "stepActionTypes", out string[] stepActionTypes);
        if (!gotActionTypes)
        {
            gotActionTypes = TryExtractStringArrayFieldLoose(raw, "stepIntents", out stepActionTypes);
        }

        bool gotNavModes = TryExtractStringArrayFieldLoose(raw, "stepNavigationModes", out string[] stepNavigationModes);
        if (!gotNavModes)
        {
            gotNavModes = TryExtractStringArrayFieldLoose(raw, "stepNavModes", out stepNavigationModes);
        }

        bool gotMissionPolicy = TryExtractStringFieldLoose(raw, "missionNavigationPolicy", out string missionNavPolicy);
        if (!gotMissionPolicy)
        {
            TryExtractStringFieldLoose(raw, "navigationPolicy", out missionNavPolicy);
        }

        if (!gotRole && !gotSteps && !gotActionTypes && !gotNavModes && !gotMissionPolicy) return false;

        planResponse = new PlanResponse
        {
            assignedRole = role ?? string.Empty,
            steps = (steps != null && steps.Length > 0) ? steps : new string[0],
            stepActionTypes = (stepActionTypes != null && stepActionTypes.Length > 0) ? stepActionTypes : new string[0],
            stepNavigationModes = (stepNavigationModes != null && stepNavigationModes.Length > 0) ? stepNavigationModes : new string[0],
            missionNavigationPolicy = missionNavPolicy ?? string.Empty
        };
        return planResponse.steps.Length > 0 ||
               !string.IsNullOrWhiteSpace(planResponse.assignedRole) ||
               planResponse.stepActionTypes.Length > 0 ||
               planResponse.stepNavigationModes.Length > 0 ||
               !string.IsNullOrWhiteSpace(planResponse.missionNavigationPolicy);
    }

    /// <summary>
    /// 容错提取字符串字段，如 "assignedRole":"Scout"
    /// </summary>
    private static bool TryExtractStringFieldLoose(string raw, string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(key)) return false;

        string pattern = $"\"{System.Text.RegularExpressions.Regex.Escape(key)}\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)";
        var m = System.Text.RegularExpressions.Regex.Match(raw, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        value = UnescapeJsonStringLoose(m.Groups["v"].Value).Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// 容错提取字符串数组字段，如 "steps":[ "...", "..." ]
    /// </summary>
    private static bool TryExtractStringArrayFieldLoose(string raw, string key, out string[] values)
    {
        values = null;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(key)) return false;

        int keyPos = raw.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
        if (keyPos < 0) return false;

        int colonPos = raw.IndexOf(':', keyPos);
        if (colonPos < 0) return false;

        int arrStart = raw.IndexOf('[', colonPos);
        if (arrStart < 0) return false;

        List<string> list = new List<string>();
        bool inString = false;
        bool escaped = false;
        int depth = 0;
        int strStart = -1;

        for (int i = arrStart; i < raw.Length; i++)
        {
            char c = raw[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                {
                    string token = raw.Substring(strStart, i - strStart);
                    string unescaped = UnescapeJsonStringLoose(token).Trim();
                    if (!string.IsNullOrWhiteSpace(unescaped)) list.Add(unescaped);
                    inString = false;
                    strStart = -1;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                strStart = i + 1;
                continue;
            }

            if (c == '[')
            {
                depth++;
                continue;
            }

            if (c == ']')
            {
                depth--;
                if (depth <= 0)
                {
                    values = list.ToArray();
                    return values.Length > 0;
                }
            }
        }

        // 数组可能被截断，仍可返回已提取到的步骤。
        values = list.ToArray();
        return values.Length > 0;
    }

    /// <summary>
    /// 容错解码 JSON 字符串中的常见转义。
    /// </summary>
    private static string UnescapeJsonStringLoose(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");
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
            string safeCompleted = (step ?? string.Empty).Replace("\"", "\\\"");
            string nextStep = (currentPlan != null && currentPlan.currentStep >= 0 && currentPlan.currentStep < currentPlan.steps.Length)
                ? (currentPlan.steps[currentPlan.currentStep] ?? string.Empty)
                : "none";
            string safeNext = nextStep.Replace("\"", "\\\"");

            var message = new AgentMessage
            {
                SenderID = agentProperties.AgentID,
                ReceiverID = currentMission.coordinatorId,
                Type = MessageType.TaskUpdate,
                Priority = 1,
                Timestamp = Time.time,
                Content = $"{{\"completedStep\":\"{safeCompleted}\",\"nextStep\":\"{safeNext}\",\"progress\":\"{currentPlan.currentStep}/{currentPlan.steps.Length}\"}}"
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
