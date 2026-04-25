using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// 自主驱动模块：基于记忆、感知和内驱力产生自主涌现任务。
///
/// 两条执行路径（互不干扰）：
///   Solo 路径：   EmergenceLLM(goal+steps) → InjectSoloMission → Active
///   协作路径：    PerceptionEvent → CollabSetupLLM → 广播 ColabInvite
///                → 收集 ColabAccept → 发送 ColabStart
///                → InjectEmergentCollabMission → RunStepGeneration → Active
/// </summary>
[RequireComponent(typeof(IntelligentAgent))]
public class AutonomousDriveModule : MonoBehaviour
{
    [Header("配置")]
    public float evaluationInterval = 30f;
    public float driveThreshold     = 0.6f;
    [Tooltip("协作任务最多招募几个队友（含自己则 maxTeamSize-1 个队友）")]
    public int maxTeamSize = 3;

    // ─── 依赖 ────────────────────────────────────────────────────────────
    private IntelligentAgent      _agent;
    private PerceptionModule      _perceptionModule;
    private PersonalitySystem     _personalitySystem;
    private PlanningModule        _planningModule;
    private ActionDecisionModule  _actionDecisionModule;
    private CommunicationModule   _comm;
    private CampusGrid2D          _campusGrid;
    private LLMInterface          _llm;
    private MemoryModule          _memoryModule;

    // ─── 计时 ────────────────────────────────────────────────────────────
    private float _lastEvaluationTime;
    private bool  _isEvaluating;

    // ─── 协作招募：发起侧状态 ────────────────────────────────────────────
    private readonly List<AcceptorInfo> _pendingAcceptors = new List<AcceptorInfo>();
    private bool _collectingAcceptors;

    // ─── 监控用快照字段（主线程写，AgentStateServer 读）──────────────────
    private string _lastGoal    = string.Empty;
    private string _lastThought = string.Empty;
    private Dictionary<string, float> _lastDrives = new Dictionary<string, float>();

    // ── 公开只读属性（供 AgentStateServer 采集）──────────────────────────
    public bool   IsEvaluating        => _isEvaluating;
    public bool   CollectingAcceptors => _collectingAcceptors;
    public string LastGoal            => _lastGoal;
    public string LastThought         => _lastThought;
    public float  EvaluationInterval  => evaluationInterval;
    public float  LastEvaluationTime  => _lastEvaluationTime;

    // 兼容旧监控字段（AgentStateServer 可能读取）
    public bool LastNeedsHelp => false;

    public IReadOnlyList<AcceptorInfo> PendingAcceptors => _pendingAcceptors;
    public IReadOnlyDictionary<string, float> LastDrives => _lastDrives;

    /// <summary>AcceptorInfo 的公开包装（供服务器层访问）。</summary>
    public class AcceptorInfo
    {
        public string agentId;
        public float  battery;
        public string location;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────────────

    private void Start()
    {
        _agent                = GetComponent<IntelligentAgent>();
        _perceptionModule     = GetComponent<PerceptionModule>();
        _personalitySystem    = GetComponent<PersonalitySystem>();
        _planningModule       = GetComponent<PlanningModule>();
        _actionDecisionModule = GetComponent<ActionDecisionModule>();
        _comm                 = GetComponent<CommunicationModule>();
        _campusGrid           = FindObjectOfType<CampusGrid2D>();
        _llm                  = FindObjectOfType<LLMInterface>();
        _memoryModule         = GetComponent<MemoryModule>();

        _lastEvaluationTime = Time.time;
    }

    private void Update()
    {
        if (_agent.CurrentState.Status != AgentStatus.Idle)
        {
            _lastEvaluationTime = Time.time;
            return;
        }

        if (Time.time - _lastEvaluationTime >= evaluationInterval && !_isEvaluating)
        {
            _isEvaluating = true;
            _lastEvaluationTime = Time.time;
            StartCoroutine(EvaluateAndTrigger());
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 主评估流程（Solo 涌现）
    // ─────────────────────────────────────────────────────────────────────

    private IEnumerator EvaluateAndTrigger()
    {
        // 1. 计算驱动力（每个 agent 完全独立评估，不等待队友）
        var drives   = ComputeDrives();
        _lastDrives  = drives;
        var topDrive = drives.OrderByDescending(d => d.Value).First();

        string driveStr = string.Join(", ", drives.Select(kv => $"{kv.Key}={kv.Value:F2}"));
        Debug.Log($"[{_agent.Properties.AgentID}] 驱动力: {driveStr} | 最强: {topDrive.Key}={topDrive.Value:F2} 阈值={driveThreshold}");

        if (topDrive.Value < driveThreshold)
        {
            Debug.Log($"[{_agent.Properties.AgentID}] 驱动力未达阈值，跳过本次涌现");
            _isEvaluating = false;
            yield break;
        }

        // 2. 随机 jitter（0-5s），降低多 agent 同时提交概率
        yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 5f));

        // jitter 后再次检查自身状态
        if (_agent.CurrentState.Status != AgentStatus.Idle)
        {
            _isEvaluating = false;
            yield break;
        }

        // 3. 收集记忆摘要和环境上下文
        string memorySummary = BuildMemorySummary();
        string locationName  = _actionDecisionModule != null
            ? _actionDecisionModule.ResolveCurrentLocationName()
            : "未知位置";
        string relativeMap = _campusGrid != null
            ? MapTopologySerializer.GetAgentRelativeMap(_campusGrid, _agent.transform.position, null)
            : "(地图不可用)";

        // 4. 调用 LLM 生成任务目标 + 步骤
        string prompt = BuildEmergencePrompt(topDrive.Key, topDrive.Value, locationName, relativeMap, memorySummary);
        string llmResponse = null;
        yield return StartCoroutine(_llm.SendRequest(
            new LLMRequestOptions
            {
                prompt         = prompt,
                temperature    = 0.8f,
                maxTokens      = 300,
                enableJsonMode = true,
                callTag        = "SoloEmergence",
                agentId        = _agent.Properties.AgentID
            },
            res => llmResponse = res));

        // 5. 解析并注入 Solo 任务
        yield return StartCoroutine(ProcessEmergenceResult(llmResponse));

        _isEvaluating = false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 驱动力计算（记忆增强版）
    // ─────────────────────────────────────────────────────────────────────

    private Dictionary<string, float> ComputeDrives()
    {
        var drives = new Dictionary<string, float>();

        float batteryRatio = _agent.CurrentState.BatteryLevel /
                             Mathf.Max(1f, _agent.Properties.BatteryCapacity);
        drives["Battery"]       = 1.0f - batteryRatio;
        drives["Threat"]        = ComputeThreatDrive();
        drives["Exploration"]   = ComputeExplorationDrive();
        drives["Collaboration"] = ComputeCollaborationDrive();
        drives["Disruption"]    = ComputeDisruptionDrive();

        return drives;
    }

    private float ComputeThreatDrive()
    {
        float baseThreat = 0f;

        if (_memoryModule != null)
        {
            var enemyMemories = _memoryModule.Recall(new MemoryQuery
            {
                kinds    = new[] { AgentMemoryKind.Observation },
                freeText = "敌方",
                maxCount = 10
            });

            if (enemyMemories != null && enemyMemories.Count > 0)
            {
                float mostRecentAgeHours = (float)(DateTime.UtcNow - enemyMemories[0].lastAccessedAt).TotalHours;
                float recencyFactor      = Mathf.Clamp01(1f - mostRecentAgeHours / 1f);
                float densityFactor      = Mathf.Clamp01(enemyMemories.Count / 5f);
                baseThreat = recencyFactor * 0.6f + densityFactor * 0.4f;
            }
        }
        else
        {
            int enemyCount = _perceptionModule?.enemyAgents?.Count ?? 0;
            baseThreat = Mathf.Clamp01(enemyCount * 0.5f);
        }

        if (_personalitySystem != null)
            baseThreat *= Mathf.Lerp(0.5f, 1.5f, _personalitySystem.Profile.neuroticism);

        return Mathf.Clamp01(baseThreat);
    }

    private float ComputeExplorationDrive()
    {
        float curiosity = 0.75f;

        if (_memoryModule != null)
        {
            var coverageMemories = _memoryModule.Recall(new MemoryQuery
            {
                kinds    = new[] { AgentMemoryKind.Outcome },
                maxCount = 20
            });

            if (coverageMemories != null && coverageMemories.Count > 0)
            {
                int uniqueTargets = coverageMemories
                    .Where(m => !string.IsNullOrWhiteSpace(m.targetRef))
                    .Select(m => m.targetRef)
                    .Distinct()
                    .Count();
                curiosity = Mathf.Clamp01(1f - uniqueTargets / 10f);
            }
        }

        if (_personalitySystem != null)
            curiosity *= Mathf.Lerp(0.6f, 1.4f, _personalitySystem.Profile.openness);

        return Mathf.Clamp01(curiosity);
    }

    private float ComputeCollaborationDrive()
    {
        float timeSinceCollab = 300f;

        if (_memoryModule != null)
        {
            var collabMemories = _memoryModule.Recall(new MemoryQuery
            {
                kinds    = new[] { AgentMemoryKind.Coordination },
                maxCount = 1
            });

            if (collabMemories != null && collabMemories.Count > 0)
                timeSinceCollab = (float)(DateTime.UtcNow - collabMemories[0].lastAccessedAt).TotalSeconds;
        }

        float socialDrive = Mathf.Clamp01(timeSinceCollab / 300f);

        if (_personalitySystem != null)
            socialDrive *= Mathf.Lerp(0.6f, 1.4f, _personalitySystem.Profile.agreeableness);

        return Mathf.Clamp01(socialDrive);
    }

    private float ComputeDisruptionDrive()
    {
        if (_personalitySystem == null || !_personalitySystem.IsAdversarial) return 0f;

        int activeEnemies = _perceptionModule?.enemyAgents?
            .Count(e => e.CurrentState.Status == AgentStatus.ExecutingTask) ?? 0;
        return Mathf.Clamp01(activeEnemies * 0.5f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 记忆摘要（注入 prompt）
    // ─────────────────────────────────────────────────────────────────────

    private string BuildMemorySummary()
    {
        if (_memoryModule == null) return string.Empty;

        var parts = new List<string>();

        var enemyMemories = _memoryModule.Recall(new MemoryQuery
        {
            kinds    = new[] { AgentMemoryKind.Observation },
            freeText = "敌方",
            maxCount = 3
        });
        if (enemyMemories != null && enemyMemories.Count > 0)
        {
            var zones = enemyMemories
                .Where(m => !string.IsNullOrWhiteSpace(m.targetRef))
                .Select(m => m.targetRef).Distinct();
            parts.Add("近期威胁活动区域：" + string.Join("、", zones));
        }

        var coverageMemories = _memoryModule.Recall(new MemoryQuery
        {
            kinds    = new[] { AgentMemoryKind.Outcome },
            maxCount = 5
        });
        if (coverageMemories != null && coverageMemories.Count > 0)
        {
            var zones = coverageMemories
                .Where(m => !string.IsNullOrWhiteSpace(m.targetRef))
                .Select(m => m.targetRef).Distinct();
            if (zones.Any())
                parts.Add("近期已覆盖区域：" + string.Join("、", zones));
        }

        return parts.Count > 0 ? string.Join("\n", parts) : string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Prompt 构建
    // ─────────────────────────────────────────────────────────────────────

    private string BuildEmergencePrompt(
        string drive, float strength, string loc, string map, string memorySummary)
    {
        string memSection = string.IsNullOrWhiteSpace(memorySummary)
            ? string.Empty
            : $"## 近期记忆摘要\n{memorySummary}\n\n";

        string disruptionInstruction = (drive == "Disruption")
            ? "⚠️ 当前核心驱动为「干扰」，你的目标应为阻碍敌方 agent 正在执行的任务。\n\n"
            : string.Empty;

        return $"你是无人机 {_agent.Properties.AgentID}。你现在处于自主思考模式。\n" +
               $"## 当前情境\n" +
               $"- 位置：{loc}\n" +
               $"- 核心驱动：{drive} (强度 {strength:F2})\n" +
               $"- 附近地标：\n{map}\n\n" +
               memSection +
               disruptionInstruction +
               $"## 思考要求\n" +
               $"根据驱动、环境和记忆，生成一个有价值的任务目标和 2-5 个具体执行步骤。\n" +
               $"每个步骤为自然语言动作描述（如\"前往北区执行侦察\"、\"观察周边环境并记录异常\"）。\n\n" +
               $"## 输出格式（严格 JSON）\n" +
               "{\n" +
               "  \"thought\": \"简短的内心独白\",\n" +
               "  \"goal\": \"自然语言任务目标\",\n" +
               "  \"steps\": [\"步骤1\", \"步骤2\", \"步骤3\"]\n" +
               "}\n";
    }

    private string BuildCollabSetupPrompt(string eventDesc, string location, SmallNodeType nodeType)
    {
        return $"你是无人机 {_agent.Properties.AgentID}，正在执行独立任务时感知到新情况。\n" +
               $"## 感知事件\n" +
               $"- 事件描述：{eventDesc}\n" +
               $"- 发生位置：{location}\n" +
               $"- 节点类型：{nodeType}\n\n" +
               $"## 要求\n" +
               $"评估此事件是否值得发起协作，生成协作任务方案。\n" +
               $"约束格式与结构化任务规划一致（C1=资源互斥, C2=同步完成, C3=条件依赖）。\n\n" +
               $"## 输出格式（严格 JSON）\n" +
               "{\n" +
               "  \"collaborationGoal\": \"协作目标描述\",\n" +
               "  \"constraints\": [\n" +
               "    {\"constraintId\":\"c1_res\",\"cType\":\"C1\",\"channel\":\"direct\",\"groupScope\":0,\n" +
               "     \"subject\":\"主执行者\",\"targetObject\":\"目标名\",\"exclusive\":true}\n" +
               "  ],\n" +
               "  \"myRole\": \"发起者的角色描述\",\n" +
               "  \"partnerRole\": \"协作者的角色描述\",\n" +
               "  \"inviteMessage\": \"发给协作者的邀请说明\"\n" +
               "}\n";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Solo 路径：解析 LLM 输出并注入步骤
    // ─────────────────────────────────────────────────────────────────────

    private IEnumerator ProcessEmergenceResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        EmergenceResult result = null;
        try
        {
            result = JsonUtility.FromJson<EmergenceResult>(ExtractJson(json));
        }
        catch (Exception e)
        {
            Debug.LogError($"[{_agent.Properties.AgentID}] 涌现 JSON 解析失败: {e.Message}");
            yield break;
        }

        if (result == null || string.IsNullOrEmpty(result.goal)) yield break;
        if (result.steps == null || result.steps.Length == 0)
        {
            Debug.LogWarning($"[{_agent.Properties.AgentID}] 涌现结果缺少 steps，跳过");
            yield break;
        }

        _lastGoal    = result.goal;
        _lastThought = result.thought ?? string.Empty;

        Debug.Log($"<color=#00FF00>[{_agent.Properties.AgentID}] 涌现思考: {result.thought}</color>");
        Debug.Log($"<color=#00FF00>[{_agent.Properties.AgentID}] 涌现目标: {result.goal} | 步骤数: {result.steps.Length}</color>");

        _planningModule?.InjectSoloMission(result.goal, result.steps);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 感知事件触发协作（执行中触发，感知路径）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PerceptionModule 感知到 ResourcePoint 或敌方 Agent 时调用。
    /// 只在 agent 正在执行 Solo 任务时响应，评估是否需要发起协作。
    /// </summary>
    public void OnPerceptionEvent(string eventDesc, string location, SmallNodeType nodeType)
    {
        if (_isEvaluating || _collectingAcceptors)           return;
        if (_planningModule == null || !_planningModule.IsRunningSolo) return;

        StartCoroutine(EvaluateCollabTrigger(eventDesc, location, nodeType));
    }

    private IEnumerator EvaluateCollabTrigger(string eventDesc, string location, SmallNodeType nodeType)
    {
        _isEvaluating = true;

        // 1. CollabSetup LLM：从事件上下文生成协作目标、约束、角色
        string llmResponse = null;
        yield return StartCoroutine(_llm.SendRequest(
            new LLMRequestOptions
            {
                prompt         = BuildCollabSetupPrompt(eventDesc, location, nodeType),
                maxTokens      = 500,
                enableJsonMode = true,
                callTag        = "CollabSetup",
                agentId        = _agent.Properties.AgentID
            },
            res => llmResponse = res));

        CollabSetupResult setup = null;
        try
        {
            setup = JsonConvert.DeserializeObject<CollabSetupResult>(ExtractJson(llmResponse));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{_agent.Properties.AgentID}] CollabSetup JSON 解析失败: {e.Message}");
            _isEvaluating = false;
            yield break;
        }

        if (setup == null || string.IsNullOrWhiteSpace(setup.collaborationGoal))
        {
            _isEvaluating = false;
            yield break;
        }

        // 2. 广播 ColabInvite（携带约束和角色分配）
        string msnId = $"emg_{_agent.Properties.AgentID}_{Time.time:F0}";
        _pendingAcceptors.Clear();
        _collectingAcceptors = true;

        var invitePayload = new ColabInvitePayload
        {
            msnId         = msnId,
            goal          = setup.collaborationGoal,
            constraints   = setup.constraints,
            partnerRole   = setup.partnerRole,
            inviteMessage = setup.inviteMessage
        };

        _comm?.SendMessage("All", MessageType.ColabInvite,
            JsonConvert.SerializeObject(invitePayload));

        Debug.Log($"<color=#FFFF00>[{_agent.Properties.AgentID}] 广播协作邀请: {setup.collaborationGoal}</color>");

        // 3. 等 5s 收集 ColabAccept
        yield return new WaitForSeconds(5f);
        _collectingAcceptors = false;

        // 4. 按电量降序选出最优接受者，逐个发送 ColabStart
        var selected = _pendingAcceptors
            .OrderByDescending(a => a.battery)
            .Take(maxTeamSize - 1)
            .ToList();

        foreach (var acceptor in selected)
        {
            var startPayload = new ColabStartPayload
            {
                msnId        = msnId,
                goal         = setup.collaborationGoal,
                assignedRole = setup.partnerRole,
                constraints  = setup.constraints
            };
            _comm?.SendMessage(acceptor.agentId, MessageType.ColabStart,
                JsonConvert.SerializeObject(startPayload));
        }

        Debug.Log($"<color=#FFFF00>[{_agent.Properties.AgentID}] 协作招募完成，" +
                  $"伙伴数: {selected.Count}</color>");

        // 5. 发起方自己以协作角色重规划（中断当前 Solo，进入协作路径）
        _planningModule?.InjectEmergentCollabMission(
            msnId, setup.collaborationGoal, setup.constraints, setup.myRole);

        _isEvaluating = false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 接收方：处理 ColabInvite（由 CommunicationModule 路由调用）
    // ─────────────────────────────────────────────────────────────────────

    public void OnColabInvite(string initiatorId, ColabInvitePayload payload)
    {
        if (_agent.CurrentState.Status != AgentStatus.Idle) return;
        if (_planningModule != null && _planningModule.IsBusy)  return;
        if (_isEvaluating) return;

        // 人格兼容性检查：破坏型↔协作型互相拒绝，不走 LLM 评估
        if (_personalitySystem != null && CommunicationManager.Instance != null)
        {
            var initiatorMod = CommunicationManager.Instance.GetAgentModule(initiatorId);
            var initiatorPs  = initiatorMod?.GetComponent<PersonalitySystem>();
            if (initiatorPs != null && initiatorPs.IsAdversarial != _personalitySystem.IsAdversarial)
                return;
        }

        StartCoroutine(EvaluateInviteWithLLM(initiatorId, payload));
    }

    private IEnumerator EvaluateInviteWithLLM(string initiatorId, ColabInvitePayload payload)
    {
        if (_llm == null) yield break;

        float batteryRatio  = _agent.CurrentState.BatteryLevel /
                              Mathf.Max(1f, _agent.Properties.BatteryCapacity);
        string locationName = _actionDecisionModule != null
            ? _actionDecisionModule.ResolveCurrentLocationName()
            : "未知位置";

        var drives   = ComputeDrives();
        var topDrive = drives.OrderByDescending(d => d.Value).First();

        string prompt =
            $"你是无人机 {_agent.Properties.AgentID}，当前空闲。\n" +
            $"## 来自 {initiatorId} 的协作邀请\n" +
            $"任务目标：{payload.goal}\n" +
            $"邀请说明：{payload.inviteMessage}\n" +
            $"你的分配角色：{payload.partnerRole}\n\n" +
            $"## 你的当前状态\n" +
            $"- 位置：{locationName}\n" +
            $"- 电量：{batteryRatio:P0}\n" +
            $"- 当前最强内驱：{topDrive.Key}（强度 {topDrive.Value:F2}）\n\n" +
            $"## 判断要求\n" +
            $"综合考虑：电量是否充足支撑任务、位置是否靠近任务区域、" +
            $"自身当前内驱是否与该任务方向一致。\n" +
            $"如果电量低于 20% 则必须拒绝（需要充电）。\n\n" +
            $"## 输出格式（严格 JSON）\n" +
            "{\n" +
            "  \"join\": true 或 false,\n" +
            "  \"reason\": \"一句话说明原因\"\n" +
            "}\n";

        string llmResponse = null;
        yield return StartCoroutine(_llm.SendRequest(
            new LLMRequestOptions
            {
                prompt         = prompt,
                temperature    = 0.4f,
                maxTokens      = 80,
                enableJsonMode = true,
                callTag        = "ColabInviteEval",
                agentId        = _agent.Properties.AgentID
            },
            res => llmResponse = res));

        if (string.IsNullOrWhiteSpace(llmResponse)) yield break;

        JoinDecision decision = null;
        try
        {
            decision = JsonUtility.FromJson<JoinDecision>(ExtractJson(llmResponse));
        }
        catch
        {
            Debug.LogWarning($"[{_agent.Properties.AgentID}] 邀请评估 JSON 解析失败: {llmResponse}");
            yield break;
        }

        if (decision == null || !decision.join) yield break;

        Debug.Log($"<color=#00FFFF>[{_agent.Properties.AgentID}] 接受邀请 from {initiatorId}: {decision.reason}</color>");

        // 注入协作任务（eager：立即开始规划，不等 ColabStart）
        _planningModule?.InjectEmergentCollabMission(
            payload.msnId, payload.goal, payload.constraints, payload.partnerRole);

        // 回传 ColabAccept（含电量/位置供发起方筛选最优伙伴）
        _comm?.SendMessage(initiatorId, MessageType.ColabAccept,
            JsonUtility.ToJson(new AcceptContext { battery = batteryRatio, location = locationName }));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 接收方：处理 ColabStart（由 CommunicationModule 路由调用）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 收到发起方最终角色确认。若 agent 已从 EvaluateInviteWithLLM 注入任务则 busy 检查拦截，
    /// 否则（未通过 LLM 评估但被选中）此处补充注入。
    /// </summary>
    public void OnColabStart(ColabStartPayload payload)
    {
        if (_planningModule == null) return;
        Debug.Log($"<color=#00FFFF>[{_agent.Properties.AgentID}] 收到 ColabStart: " +
                  $"{payload.goal} | 角色: {payload.assignedRole}</color>");
        _planningModule.InjectEmergentCollabMission(
            payload.msnId, payload.goal, payload.constraints, payload.assignedRole);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 接收方：处理 ColabAccept（由 CommunicationModule 路由调用）
    // ─────────────────────────────────────────────────────────────────────

    public void OnColabAccept(string acceptorId, string contextJson)
    {
        if (!_collectingAcceptors) return;
        if (_pendingAcceptors.Any(a => a.agentId == acceptorId)) return;

        float battery   = 0.5f;
        string location = "未知";
        try
        {
            var ctx = JsonUtility.FromJson<AcceptContext>(contextJson);
            if (ctx != null) { battery = ctx.battery; location = ctx.location; }
        }
        catch { /* 解析失败用默认值 */ }

        _pendingAcceptors.Add(new AcceptorInfo
        {
            agentId  = acceptorId,
            battery  = battery,
            location = location
        });

        Debug.Log($"<color=#00FFFF>[{_agent.Properties.AgentID}] 收到接受: {acceptorId} " +
                  $"(电量 {battery:P0}, 位置 {location})</color>");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────────────

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        int start = raw.IndexOf('{');
        int end   = raw.LastIndexOf('}');
        if (start >= 0 && end > start) return raw.Substring(start, end - start + 1);
        return raw;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 内部数据类
    // ─────────────────────────────────────────────────────────────────────

    [Serializable]
    private class EmergenceResult
    {
        public string   thought;
        public string   goal;
        public string[] steps;
    }

    [Serializable]
    private class JoinDecision
    {
        public bool   join;
        public string reason;
    }

    [Serializable]
    private class AcceptContext
    {
        public float  battery;
        public string location;
    }

    [Serializable]
    private class CollabSetupResult
    {
        public string                 collaborationGoal;
        public StructuredConstraint[] constraints;
        public string                 myRole;
        public string                 partnerRole;
        public string                 inviteMessage;
    }

    // ── 公开数据类（CommunicationModule 路由时需要反序列化）──────────────

    [Serializable]
    public class ColabInvitePayload
    {
        public string                 msnId;
        public string                 goal;
        public StructuredConstraint[] constraints;
        public string                 partnerRole;
        public string                 inviteMessage;
    }

    [Serializable]
    public class ColabStartPayload
    {
        public string                 msnId;
        public string                 goal;
        public string                 assignedRole;
        public StructuredConstraint[] constraints;
    }

    // AcceptorInfo 已提升为公开嵌套类（见上方）。
}
