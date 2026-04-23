using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 协作化自主驱动模块：负责让智能体产生基于“内驱力”和“社交/团队意识”的自主涌现。
/// 支持语义化位置描述、周边环境感知，以及包含协作意图的任务生成。
/// </summary>
[RequireComponent(typeof(IntelligentAgent))]
public class AutonomousDriveModule : MonoBehaviour
{
    [Header("配置")]
    public float evaluationInterval = 30f; // 评估内驱力的时间间隔（秒）
    public float driveThreshold = 0.6f;    // 触发自主任务的最小驱动力阈值

    private IntelligentAgent _agent;
    private PerceptionModule _perceptionModule;
    private PersonalitySystem _personalitySystem;
    private PlanningModule _planningModule;
    private ActionDecisionModule _actionDecisionModule;
    private CampusGrid2D _campusGrid;
    private LLMInterface _llm;

    private float _lastEvaluationTime;
    private bool _isEvaluating = false;

    // 记录上次与队友交互的时间，用于计算社交/协同驱动
    private float _lastTeamInteractionTime;

    void Start()
    {
        _agent = GetComponent<IntelligentAgent>();
        _perceptionModule = GetComponent<PerceptionModule>();
        _personalitySystem = GetComponent<PersonalitySystem>();
        _planningModule = GetComponent<PlanningModule>();
        _actionDecisionModule = GetComponent<ActionDecisionModule>();
        _campusGrid = FindObjectOfType<CampusGrid2D>();
        _llm = FindObjectOfType<LLMInterface>();

        _lastEvaluationTime = Time.time;
        _lastTeamInteractionTime = Time.time;
    }

    void Update()
    {
        // 1. 如果当前智能体正在执行任务，不评估。
        // 但我们增加了一个逻辑：如果当前空闲，开始计时
        if (_agent.CurrentState.Status != AgentStatus.Idle)
        {
            _lastEvaluationTime = Time.time; 
            return;
        }

        // 2. 达到评估间隔
        if (Time.time - _lastEvaluationTime >= evaluationInterval && !_isEvaluating)
        {
            _isEvaluating = true;
            _lastEvaluationTime = Time.time;
            StartCoroutine(EvaluateAndTriggerCollaborativeEmergence());
        }
    }

    private IEnumerator EvaluateAndTriggerCollaborativeEmergence()
    {
        // --- 1. 计算驱动力（包含协同驱动） ---
        var drives = new Dictionary<string, float>();

        // A. 电量/生存
        float batteryRatio = _agent.CurrentState.BatteryLevel / _agent.Properties.BatteryCapacity;
        drives.Add("Battery", 1.0f - batteryRatio);

        // B. 威胁/安全
        int enemyCount = _perceptionModule != null && _perceptionModule.enemyAgents != null ? _perceptionModule.enemyAgents.Count : 0;
        float threat = Mathf.Clamp01(enemyCount * 0.5f);
        if (_personalitySystem != null) threat *= (0.5f + _personalitySystem.Profile.neuroticism);
        drives.Add("Threat", threat);

        // C. 探索/好奇
        float curiosity = 0.5f;
        if (_personalitySystem != null) curiosity *= (0.5f + _personalitySystem.Profile.openness);
        drives.Add("Exploration", curiosity);

        // D. 协作/社交驱动 (TeamSynergy)
        // 随着时间推移，如果一直没和队友合作，协作驱动会上升
        float timeSinceInteraction = Time.time - _lastTeamInteractionTime;
        float socialDrive = Mathf.Clamp01(timeSinceInteraction / 300f); // 5分钟没社交就想社交
        if (_personalitySystem != null) socialDrive *= (0.5f + _personalitySystem.Profile.agreeableness); // 宜人性越高越想社交
        drives.Add("Collaboration", socialDrive);

        // --- 2. 选出最高驱动 ---
        var topDrive = drives.OrderByDescending(d => d.Value).First();
        if (topDrive.Value < driveThreshold)
        {
            _isEvaluating = false;
            yield break;
        }

        // --- 3. 收集环境与队友上下文 ---
        string locationName = _actionDecisionModule != null ? _actionDecisionModule.ResolveCurrentLocationName() : "未知位置";
        
        // 获取周边拓扑图
        string relativeMap = _campusGrid != null
            ? MapTopologySerializer.GetAgentRelativeMap(_campusGrid, _agent.transform.position, null)
            : "(地图不可用)";

        // 获取队友信息
        string teamInfo = GetTeammatesStatusSummary();

        // --- 4. 调用 LLM 生成协作化任务 ---
        string prompt = BuildEmergencePrompt(topDrive.Key, topDrive.Value, locationName, relativeMap, teamInfo);
        
        LLMRequestOptions options = new LLMRequestOptions
        {
            prompt = prompt,
            temperature = 0.8f,
            maxTokens = 150,
            callTag = "CollaborativeEmergence",
            agentId = _agent.Properties.AgentID
        };

        string llmResponse = null;
        yield return StartCoroutine(_llm.SendRequest(options, (res) => llmResponse = res));

        // --- 5. 解析并提交任务 ---
        ProcessEmergenceResult(llmResponse);

        _isEvaluating = false;
    }

    private string BuildEmergencePrompt(string drive, float strength, string loc, string map, string team)
    {
        return $"你是无人机 {_agent.Properties.AgentID}。你现在处于自主思考模式。\n" +
               $"## 当前情境\n" +
               $"- 位置：{loc}\n" +
               $"- 核心驱动：{drive} (强度 {strength:F2})\n" +
               $"- 附近地标：\n{map}\n" +
               $"- 队友状态：\n{team}\n\n" +
               $"## 思考要求\n" +
               $"1. 根据你的驱动和环境，决定是否需要发起一个任务。你可以选择独立行动，也可以邀请空闲的队友一起协作。\n" +
               $"2. 如果你认为该任务具有风险或需要更广覆盖（如巡逻、拦截），请将所需人数设置为 2 或更多。\n" +
               $"3. 如果你要和某位队友协作，请在任务描述中提到它，例如：“邀请智能体B一起侦察艺术中心”。\n\n" +
               $"## 输出格式（严格 JSON）\n" +
               $"{{\n" +
               $"  \"thought\": \"简短的内心独白\",\n" +
               $"  \"goal\": \"自然语言任务目标语句\",\n" +
               $"  \"agentCount\": 需要参与的总人数(1-3)\n" +
               $"}}\n";
    }

    private string GetTeammatesStatusSummary()
    {
        if (CommunicationManager.Instance == null) return "无法获取队友信息";
        
        var allIds = CommunicationManager.Instance.GetAllAgentIds();
        List<string> summaries = new List<string>();

        foreach (var id in allIds)
        {
            if (id == _agent.Properties.AgentID) continue;
            var mod = CommunicationManager.Instance.GetAgentModule(id);
            if (mod == null) continue;
            var agent = mod.GetComponent<IntelligentAgent>();
            if (agent == null) continue;

            string status = agent.CurrentState.Status.ToString();
            string loc = "未知";
            var adm = agent.GetComponent<ActionDecisionModule>();
            if (adm != null) loc = adm.ResolveCurrentLocationName();

            summaries.Add($"- {id}: {status}, 位于 {loc}");
        }

        return summaries.Count > 0 ? string.Join("\n", summaries) : "当前无其他队友";
    }

    private void ProcessEmergenceResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            // 提取 JSON 并解析
            string cleanJson = ExtractJson(json);
            var result = JsonUtility.FromJson<EmergenceResult>(cleanJson);

            if (result != null && !string.IsNullOrEmpty(result.goal))
            {
                Debug.Log($"<color=#00FF00>[{_agent.Properties.AgentID}] 涌现决策: {result.thought}</color>");
                Debug.Log($"<color=#00FF00>[{_agent.Properties.AgentID}] 提交任务: {result.goal} (人数: {result.agentCount})</color>");
                
                // 如果涉及协作（人数>1），重置社交计时
                if (result.agentCount > 1) _lastTeamInteractionTime = Time.time;

                // 提交给规划模块
                _planningModule.SubmitMissionRequest(result.goal, result.agentCount);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"解析涌现 JSON 失败: {e.Message}. 原文: {json}");
        }
    }

    private string ExtractJson(string raw)
    {
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start >= 0 && end > start) return raw.Substring(start, end - start + 1);
        return raw;
    }

    [Serializable]
    private class EmergenceResult
    {
        public string thought;
        public string goal;
        public int agentCount;
    }
}
