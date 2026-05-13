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

    /// <summary>上次任务结束（回到 Idle）的时间。用于计算空闲 boost，防止 drive 全部低于阈值。</summary>
    private float _idleStartTime;

    // ─── 协作招募：发起侧状态 ────────────────────────────────────────────
    private readonly List<AcceptorInfo> _pendingAcceptors = new List<AcceptorInfo>();
    private bool _collectingAcceptors;

    // ─── 监控用快照字段（主线程写，AgentStateServer 读）──────────────────
    private string _lastGoal    = string.Empty;
    private string _lastThought = string.Empty;
    private string[] _lastSteps  = Array.Empty<string>();
    private Dictionary<string, float> _lastDrives = new Dictionary<string, float>();

    // ── 公开只读属性（供 AgentStateServer 采集）──────────────────────────
    public bool   IsEvaluating        => _isEvaluating;
    public bool   CollectingAcceptors => _collectingAcceptors;
    public string LastGoal            => _lastGoal;
    public string LastThought         => _lastThought;
    public IReadOnlyList<string> LastSteps => _lastSteps;
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
        _idleStartTime      = Time.time;
    }

    private void Update()
    {
        if (_agent.CurrentState.Status != AgentStatus.Idle || HasOngoingMission())
        {
            _lastEvaluationTime = Time.time;
            _idleStartTime = Time.time; // 非 Idle 时持续刷新，回到 Idle 时即为起点
            return;
        }

        // 兜底保护：显式阻止非 Idle 或已有任务时继续触发 SoloEmergence。
        if (_agent.CurrentState.Status != AgentStatus.Idle || HasOngoingMission())
            return;

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

        // 3. 检查任务状态，收集环境上下文
        if (HasOngoingMission())
        {
            Debug.Log($"[{_agent.Properties.AgentID}] SoloEmergence 跳过：PlanningModule 已有活跃或处理中任务");
            _isEvaluating = false;
            yield break;
        }

        string locationName  = _actionDecisionModule != null
            ? _actionDecisionModule.ResolveCurrentLocationName()
            : "未知位置";
        string strategicMap = _campusGrid != null
            ? MapTopologySerializer.GetStrategicMap(_campusGrid, _agent.transform.position)
            : "(地图不可用)";

        // 4. 调用 LLM 生成任务目标 + 步骤
        string prompt = BuildEmergencePrompt(topDrive.Key, topDrive.Value, locationName, strategicMap);
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

        // 空闲时间 boost：agent 长时间 Idle 时递增探索驱动力，防止所有 drive 低于阈值
        float idleDuration = Time.time - _idleStartTime;
        if (idleDuration > 30f)
        {
            float idleBoost = Mathf.Min((idleDuration - 30f) / 60f, 0.3f);
            curiosity += idleBoost;
        }

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

        // 层1：基线巡逻驱动（破坏型 agent 天生需要搜索敌人）
        // 必须 >= driveThreshold 才能在无感知/无记忆时也触发搜索巡逻
        float baseDrive = 0.6f;

        // 层2：感知增强（看到敌人时大幅提升）
        int totalEnemies = _perceptionModule?.enemyAgents?.Count ?? 0;
        int activeEnemies = _perceptionModule?.enemyAgents?
            .Count(e => e.CurrentState.Status == AgentStatus.ExecutingTask) ?? 0;
        float perceptionBoost = Mathf.Clamp01(totalEnemies * 0.3f + activeEnemies * 0.2f);

        // 层3：记忆增强（近期有敌方记忆时提升）
        float memoryBoost = 0f;
        if (_memoryModule != null)
        {
            var enemyMem = _memoryModule.Recall(new MemoryQuery
            {
                kinds    = new[] { AgentMemoryKind.Observation },
                freeText = "敌方",
                maxCount = 3
            });
            if (enemyMem != null && enemyMem.Count > 0)
                memoryBoost = 0.15f;
        }

        return Mathf.Clamp01(baseDrive + perceptionBoost + memoryBoost);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 提示词构建（Solo 涌现）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 格式化当前感知到的敌方列表。无敌方时返回空字符串。
    /// </summary>
    private string FormatEnemyList()
    {
        var enemies = _perceptionModule?.enemyAgents;
        if (enemies == null || enemies.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var enemy in enemies)
        {
            if (enemy == null) continue;
            float dx = enemy.transform.position.x - _agent.transform.position.x;
            float dz = enemy.transform.position.z - _agent.transform.position.z;
            string compass = MapTopologySerializer.GetCompass(dx, dz);
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            string status = enemy.CurrentState != null ? enemy.CurrentState.Status.ToString() : "Unknown";
            sb.AppendLine($"- {enemy.Properties.AgentID}：{compass}方向 {dist:0}m，状态={status}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 构建 Solo 涌现的完整提示词。内部自行查询记忆和感知。
    /// </summary>
    private string BuildEmergencePrompt(string drive, float strength, string loc, string strategicMap)
    {
        return
        $@"你是无人机 {_agent.Properties.AgentID}。你现在处于自主思考模式。

        ## 当前情境
        - 位置：{loc}
        - 核心驱动：{drive} (强度 {strength:F2})

        ## 战略地图（建筑/地标拓扑）
        {strategicMap}

        {BuildMemorySection()}
        {BuildRoleSection(drive)}
        ## 思考要求
        根据驱动、环境和记忆，生成一个有价值的任务目标和 2-5 个具体执行步骤。
        每个步骤为自然语言动作描述（如""前往北区执行侦察""、""观察周边环境并记录异常""）。

        ## 输出格式（严格 JSON）
        {{
        ""thought"": ""简短的内心独白"",
        ""goal"": ""自然语言任务目标"",
        ""steps"": [""步骤1"", ""步骤2"", ""步骤3""],
        ""suggestion"": ""从本次决策中总结的可复用策略（如'在高威胁区域优先选择隐蔽路线'），若无则留空"",
        ""confidence"": ""高/中/低""
        }}";
    }

    /// <summary>查询环境观测 + 已覆盖区域记忆，返回格式化文本段落。无记忆时返回空。</summary>
    private string BuildMemorySection()
    {
        if (_memoryModule == null) return string.Empty;
        var parts = new List<string>();

        var observations = _memoryModule.Recall(new MemoryQuery
        {
            kinds    = new[] { AgentMemoryKind.Observation },
            maxCount = 5
        });
        if (observations != null && observations.Count > 0)
        {
            var items = new List<(int minutesAgo, string summary)>();
            foreach (var m in observations)
            {
                int minutesAgo = Mathf.Max(1, (int)(DateTime.UtcNow - m.createdAt).TotalMinutes);
                items.Add((minutesAgo, m.summary));
            }
            string obsText = MapTopologySerializer.BuildMemoryObservationsSection(items);
            if (!string.IsNullOrWhiteSpace(obsText))
                parts.Add($"## 环境观测记忆\n{obsText}");
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
                .Select(m => m.targetRef).Distinct().ToList();
            if (zones.Count > 0)
                parts.Add("## 近期已覆盖区域\n" + string.Join("、", zones));
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) + "\n" : string.Empty;
    }

    /// <summary>根据驱动类型构建角色指令段落。</summary>
    private string BuildRoleSection(string drive)
    {
        if (drive == "Disruption")
            return BuildDisruptionRole();
        else
            return BuildCooperativeRole();
    }

    /// <summary>破坏型角色指令：有情报时给干扰策略，无情报时引导搜索。</summary>
    private string BuildDisruptionRole()
    {
        string enemyList    = FormatEnemyList();
        string enemyMemText = FormatEnemyMemories();
        bool hasAnyIntel    = !string.IsNullOrWhiteSpace(enemyList) || !string.IsNullOrWhiteSpace(enemyMemText);

        if (!hasAnyIntel)
            return
            @"## 角色：破坏型智能体（未侦测到敌方）
            ### 优先任务：搜索与侦察
            - 首要任务是发现和定位敌方智能体
            - 前往高价值区域（资源点、建筑密集区、交通要道）巡逻
            - 选择尚未巡逻过的区域（参考历史观测记忆）
            - 发现敌方后记录其位置和行动状态
            ";

            string liveSection = string.IsNullOrWhiteSpace(enemyList) ? "" :
            $@"### 当前侦测到的敌方智能体
            {enemyList}
            ";
            string memSection = string.IsNullOrWhiteSpace(enemyMemText) ? "" :
            $@"### 近期敌方活动记忆
            {enemyMemText}
            ";
            return
            $@"## 角色：破坏型智能体
            核心目标：阻碍敌方任务执行。

            {liveSection}{memSection}### 干扰策略参考
            根据敌方状态选择最有效的干扰方式：
            - 拦截：飞往敌方当前位置或预判路径，阻断其移动
            - 抢占：先于敌方到达其任务目标位置，占据关键资源点
            - 跟踪监视：持续跟踪敌方，记录其行动并上报
            - 区域骚扰：在敌方任务区域反复巡逻制造干扰
            ";
    }

    /// <summary>协作型角色指令：只在实际感知到敌方时注入威胁警告。</summary>
    private string BuildCooperativeRole()
    {
        string enemyList = FormatEnemyList();
        if (string.IsNullOrWhiteSpace(enemyList)) return string.Empty;

        return
        $@"## 威胁警告
        附近存在破坏型敌方智能体，它们会试图干扰你的任务执行。

        ### 当前侦测到的敌方智能体
        {enemyList}

        ### 反干扰策略
        - 路径规避：避开敌方智能体所在区域，选择远离敌方的路线到达目标
        - 时机选择：如果敌方正在你的目标区域附近，优先前往其他同等价值的目标
        - 快速执行：在敌方活跃区域尽量缩短停留时间，快进快出
        - 预警意识：在规划步骤时加入观察周边的环节，及时发现敌方接近
        - 切勿主动接近敌方或与其对抗，你的核心目标是完成自己的任务
        ";
    }

    /// <summary>格式化记忆中的敌方活动。无记忆时返回空字符串。</summary>
    private string FormatEnemyMemories()
    {
        if (_memoryModule == null) return string.Empty;
        var enemyMem = _memoryModule.Recall(new MemoryQuery
        {
            kinds    = new[] { AgentMemoryKind.Observation },
            freeText = "敌方",
            maxCount = 5
        });
        if (enemyMem == null || enemyMem.Count == 0) return string.Empty;

        var lines = new List<string>();
        foreach (var m in enemyMem)
        {
            int minutesAgo = Mathf.Max(1, (int)(DateTime.UtcNow - m.createdAt).TotalMinutes);
            lines.Add($"- {minutesAgo}分钟前：{m.summary}");
        }
        return string.Join("\n", lines);
    }

    private string BuildCollabSetupPrompt(
        string eventDesc, string location,
        float batteryRatio, string currentLocation,
        string strategicMap)
    {
        return
        $@"你是无人机 {_agent.Properties.AgentID}，正在执行独立任务时感知到需要协作的新情况。请评估是否值得发起协作，并设计协作方案。

        ## 你的当前状态
        - 当前位置：{currentLocation}
        - 电量：{batteryRatio:P0}
        - 当前任务：正在执行 Solo 任务

        ## 触发事件
        - {eventDesc}
        - 发生区域：{location}

        ## 附近环境
        {strategicMap}

        {BuildMemorySection()}
        ## 协作约束类型详细说明
        协作任务需要设计结构化约束来协调多个智能体。共有三种约束类型：

        ### C1 - 资源互斥约束
        含义：某个资源或目标只能由一个智能体独占操作，其他智能体需等待。
        示例：只有一个充电桩，drone_A 使用时 drone_B 必须等待。
        字段：constraintId(唯一ID), cType=""C1"", subject(执行者ID), targetObject(目标名), exclusive(是否独占?true:false)

        ### C2 - 同步完成约束
        含义：多个智能体必须同时完成各自子任务，或一方完成后另一方才执行下一步。
        示例：两架无人机分别到达目标区域两端后同时开始扫描。
        字段：constraintId(唯一ID), cType=""C2"", condition(完成条件), syncWith(同步等待的智能体ID列表)

        ### C3 - 行为耦合约束（两种子类型）
        C3 有两种模式，由 sign 字段区分：

        **sign=+1（单向前置等待）**：一个智能体必须等另一个智能体到位/就绪后才允许开始行动（非对称依赖）。
        示例：drone_B 必须等 drone_A 到达掩护位置后才起飞。
        字段：constraintId(唯一ID), cType=""C3"", sign=1, watchAgent(被等待的智能体ID), reactTo=""ReadySignal""
        运行机制：watchAgent 到达就绪状态后在白板写入 ReadySignal，等待方读取后才行动。

        **sign=-1（动态互斥）**：多个智能体不能同时前往同一目标、区域、地点，先到先得，后来者需自动避让选择其他目标。
        适用条件：存在多个目标点，每个目标同一时刻只允许一个智能体占用，但具体谁去哪个目标在规划时无法确定。
        示例：3个资源点需要3架无人机分别前往采集，但谁去哪个运行时动态决定。
        字段：constraintId(唯一ID), cType=""C3"", sign=-1, watchAgent="""", reactTo=""IntentAnnounce""
        运行机制：每个智能体决定目标后在白板写入 IntentAnnounce 占位，其他智能体读取后避开已被占用的目标。

        **注意**：C3 是组内协同机制，参与互斥的智能体仍属同一协作团队，互斥/避让不代表对抗。
        只在存在明确的等待/互斥关系时才生成 C3，纯并行任务不需要。

        ## 思考步骤（请按以下顺序推理）
        1. 这个事件是否重要到需要协作？独自处理是否可行？
        2. 如果需要协作，目标是什么？需要几个协作者？
        3. 发起者和协作者分别承担什么角色？
        4. 协作过程中有哪些约束关系？（用上述C1/C2/C3描述）
        5. 如何简洁地向协作者描述任务使其理解并愿意加入？

        ## 输出格式（严格 JSON）
        {{
        ""collaborationGoal"": ""协作目标描述"",
        ""constraints"": [
            {{""constraintId"":""c1_xxx"",""cType"":""C1"",""channel"":""direct"",""groupScope"":0,
            ""subject"":""执行者ID"",""targetObject"":""目标名"",""exclusive"":true}}
        ],
        ""myRole"": ""发起者的角色描述"",
        ""partnerRole"": ""协作者的角色描述"",
        ""inviteMessage"": ""发给协作者的邀请说明（包含具体位置和任务内容）""
        }}";
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
        _lastSteps   = result.steps ?? Array.Empty<string>();

        Debug.Log($"<color=#00FF00>[{_agent.Properties.AgentID}] 涌现思考: {result.thought}</color>");
        Debug.Log($"<color=#00FF00>[{_agent.Properties.AgentID}] 涌现目标: {result.goal} | 步骤数: {result.steps.Length}</color>");

        if (HasOngoingMission())
        {
            Debug.Log($"[{_agent.Properties.AgentID}] 丢弃 SoloEmergence 结果：当前已有活跃或处理中任务");
            yield break;
        }

        _planningModule?.InjectSoloMission(result.goal, result.steps);

        // 将涌现结果写入记忆，使 TryExtractPolicyFromDetail 管道有数据可处理
        // detail 中包含 suggestion/confidence 字段，PolicyRegex 可提取为 Policy 记忆
        _memoryModule?.Remember(
            AgentMemoryKind.Outcome,
            $"自主涌现任务：{result.goal}",
            JsonUtility.ToJson(result),
            importance: 0.6f,
            confidence: 0.7f,
            sourceModule: "AutonomousDriveModule",
            tags: new[] { "emergence", "solo" });
    }

    // ─────────────────────────────────────────────────────────────────────
    // 感知事件触发协作（执行中触发，感知路径）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 感知到需要协作评估的事件时调用（资源点、敌方、突发情况等）。
    /// 只在 agent 正在执行 Solo 任务时响应。
    /// </summary>
    /// <param name="eventDesc">事件描述，如"green_41附近发现资源点"</param>
    /// <param name="location">事件发生的大节点名称，如"green_41"</param>
    public void OnPerceptionEvent(string eventDesc, string location)
    {
        if (_isEvaluating || _collectingAcceptors)           return;
        if (_planningModule == null || !_planningModule.IsRunningSolo) return;

        StartCoroutine(EvaluateCollabTrigger(eventDesc, location));
    }

    private IEnumerator EvaluateCollabTrigger(string eventDesc, string location)
    {
        _isEvaluating = true;

        // 收集上下文
        float batteryRatio = _agent.CurrentState.BatteryLevel /
                             Mathf.Max(1f, _agent.Properties.BatteryCapacity);
        string currentLoc = _actionDecisionModule != null
            ? _actionDecisionModule.ResolveCurrentLocationName()
            : "未知位置";
        string stratMap = _campusGrid != null
            ? MapTopologySerializer.GetStrategicMap(_campusGrid, _agent.transform.position)
            : "(地图不可用)";
        // 1. CollabSetup LLM：从事件上下文生成协作目标、约束、角色
        string llmResponse = null;
        yield return StartCoroutine(_llm.SendRequest(
            new LLMRequestOptions
            {
                prompt         = BuildCollabSetupPrompt(eventDesc, location,
                                     batteryRatio, currentLoc, stratMap),
                maxTokens      = 800,
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
        //    BUG-FIX: IsRunningSolo=true 意味着 busy=true，必须先中断 Solo 才能注入协作计划
        if (_planningModule != null && _planningModule.IsRunningSolo)
        {
            Debug.Log($"<color=#FF8800>[{_agent.Properties.AgentID}] 中断当前 Solo 任务，切换为协作涌现</color>");
            _actionDecisionModule?.AbortCurrentStep("协作涌现中断 Solo");
            _planningModule.ResetForNewMission();
        }
        _planningModule?.InjectEmergentCollabMission(
            msnId, setup.collaborationGoal, setup.constraints, setup.myRole);

        _isEvaluating = false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 接收方：处理 ColabInvite（由 CommunicationModule 路由调用）
    // ─────────────────────────────────────────────────────────────────────

    public void OnColabInvite(string initiatorId, ColabInvitePayload payload)
    {
        // 不处理自己发出的邀请
        if (string.Equals(initiatorId, _agent.Properties?.AgentID, System.StringComparison.OrdinalIgnoreCase))
            return;
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

        // 收集环境上下文
        string stratMap = _campusGrid != null
            ? MapTopologySerializer.GetStrategicMap(_campusGrid, _agent.transform.position)
            : "(地图不可用)";
        string constraintText = FormatConstraintsForPrompt(payload.constraints);

        string prompt =
            $"你是无人机 {_agent.Properties.AgentID}，当前处于空闲状态。你收到了一个协作邀请，需要评估是否加入。\n\n" +
            $"## 来自 {initiatorId} 的协作邀请\n" +
            $"- 任务目标：{payload.goal}\n" +
            $"- 邀请说明：{payload.inviteMessage}\n" +
            $"- 你的分配角色：{payload.partnerRole}\n" +
            $"- 协作约束：\n{constraintText}\n\n" +
            $"## 你的当前状态\n" +
            $"- 位置：{locationName}\n" +
            $"- 电量：{batteryRatio:P0}\n" +
            $"- 当前最强内驱：{topDrive.Key}（强度 {topDrive.Value:F2}）\n\n" +
            $"## 附近环境\n{stratMap}\n\n" +
            $"## 判断要求（请按以下步骤思考）\n" +
            $"1. 电量是否充足？（低于 20% 必须拒绝，需要优先充电）\n" +
            $"2. 你的位置距离任务区域远吗？（根据邀请说明中提到的位置判断）\n" +
            $"3. 你当前的内驱方向是否与该协作任务一致？（如内驱是 Exploration，邀请的是侦察任务，则一致性高）\n" +
            $"4. 你是否有能力胜任分配的角色？\n" +
            $"5. 综合以上因素，做出加入或拒绝的决策。\n\n" +
            $"## 输出格式（严格 JSON）\n" +
            "{\n" +
            "  \"join\": true/false,\n" +
            "  \"reason\": \"一句话说明决策原因\"\n" +
            "}\n";

        string llmResponse = null;
        yield return StartCoroutine(_llm.SendRequest(
            new LLMRequestOptions
            {
                prompt         = prompt,
                temperature    = 0.4f,
                maxTokens      = 150,
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

    /// <summary>
    /// PlanningModule 处于处理中或已有活跃计划时，禁止再次发起 SoloEmergence。
    /// </summary>
    private bool HasOngoingMission()
    {
        return _planningModule != null &&
               (_planningModule.IsBusy || _planningModule.HasActiveMission());
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        int start = raw.IndexOf('{');
        int end   = raw.LastIndexOf('}');
        if (start >= 0 && end > start) return raw.Substring(start, end - start + 1);
        return raw;
    }

    /// <summary>将约束数组格式化为 LLM 可读文本，用于协作邀请评估提示词。</summary>
    private static string FormatConstraintsForPrompt(StructuredConstraint[] constraints)
    {
        if (constraints == null || constraints.Length == 0)
            return "  （无特殊约束）";

        var sb = new System.Text.StringBuilder();
        foreach (var c in constraints)
        {
            if (c == null) continue;
            string typeDesc;
            switch (c.cType)
            {
                case "C1": typeDesc = "资源互斥"; break;
                case "C2": typeDesc = "同步完成"; break;
                case "C3": typeDesc = "条件依赖"; break;
                default:   typeDesc = c.cType;    break;
            }
            sb.AppendLine($"  - [{typeDesc}] {c.constraintId}：{c.targetObject ?? c.condition ?? c.reactTo ?? ""}");
        }
        return sb.ToString().TrimEnd();
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
        public string   suggestion;   // 可复用策略提示，供 MemoryModule 提取为 Policy
        public string   confidence;   // 策略置信度：高/中/低
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
