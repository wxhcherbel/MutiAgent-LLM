# ActionDecisionModule (ADM) — 完整实现计划

## Context

将 `ADM_Design.html` 中设计的完整 ADM 系统转化为可运行的 C# 代码。当前 `ActionDecisionModule.cs` 为 84 行存根。涉及数据结构新增、旧系统兼容类型清理、状态机实现、LLM 调用、冲突解决机制和通信路由。

---

## 链路检查

### 主调用链

```
用户 → planningModule.SubmitMissionRequest()
     → PlanningModule 4阶段协商 → Active 状态
     → IntelligentAgent.MakeDecisionCoroutine()
     → ADM.DecideNextAction()
     → ADM.StartStep(step)
     → LLM-A → broadcast AgentContextUpdate
     → Negotiating → Running
     → GetCurrentAction() [执行层取动作]
     → CompleteCurrentAction()
     → planningModule.CompleteCurrentStep()
```

### 状态变化

```
Idle → Interpreting → Negotiating → Running
     → [Interrupted → Replanning → Negotiating →]
     → Running → Done
```

---

## 未验证前提

> ⚠️ 以下前提在本实现中明确标注，无法在 ADM 内部保证：

- **[未验证]** `agentProperties.TeamID` 在 `ADM.Start()` 前已由 `IntelligentAgent` 正确初始化 — 本实现直接读取此字段
- **[未验证]** 执行层（`IntelligentAgent` / `MLAgentsController`）在动作执行完成后调用 `ADM.CompleteCurrentAction()` — 本次实现仅提供该接口，触发逻辑需在 `IntelligentAgent`/`MLAgentsController` 中另行实现
- **[未验证]** `campusGrid.TryGetCellFeatureInfoByWorld()` 能正确返回语义地名 — 地图数据质量依赖 `CampusGrid2D` 的 JSON 加载

---

## 实现顺序

| # | 文件 | 操作 |
|---|------|------|
| 1 | `LLM_Modules/PlanningData.cs` | 删除旧兼容类型；新增 ADM 数据结构；`PlanSlot`/`PlanStep` 新增字段 |
| 2 | `Other_Modules/CoreEnums.cs` | 新增 `AtomicActionType`、`ADMStatus`；删除 `JudgeEventReport` |
| 3 | `Communication_Module/CommunicationManager.cs` | 删除 `DeliverToJudge` + Judge scope 分支 |
| 4 | `LLM_Modules/PlanningModule.cs` | 删除 `HandleExecutionEventReport` 存根 |
| 5 | `LLM_Modules/MemoryModule.cs` | 删除 `RememberMissionAssignment` |
| 6 | `LLM_Modules/ReflectionModule.cs` | 删除3个含 `MissionTaskSlot` 参数的方法 |
| 7 | `Maps/MapTopologySerializer.cs` | 新建 |
| 8 | `LLM_Modules/ActionDecisionModule.cs` | 全量替换 |
| 9 | `Communication_Module/CommunicationModule.cs` | 新增 `admModule` + `BoardUpdate` 路由 |

---

## 步骤 1：PlanningData.cs

**文件：** `LLM_Modules/PlanningData.cs`

### 1a. 删除整个第二节（旧系统兼容类型）

删除从注释 `// ─── 二、旧系统兼容类型 ───` 到文件末尾的全部代码，即删除：

- `GridCellCandidate` 类
- `ExecutionEventType` 枚举
- `ExecutionEventReport` 类
- `MissionTaskSlot` 类
- `ScenarioJudge` 类

### 1b. PlanSlot 新增字段（在 `doneCond` 之后）

```csharp
public string coordinationConstraint; // 协同约束，如"保持前探-侧护-后卫队形"
```

### 1c. PlanStep 新增字段（在 `doneCond` 之后）

```csharp
public string constraint; // 继承自 PlanSlot.coordinationConstraint，ADM 拆原子动作时使用
```

### 1d. 在文件末尾添加 ADM 数据结构

```csharp
// ─── ADM 执行层数据结构 ────────────────────────────────────────────

/// <summary>原子动作，ADM 的最小可执行单元。</summary>
[Serializable]
public class AtomicAction
{
    public string actionId;              // 唯一ID，格式 "aa_N"
    public AtomicActionType type;        // 动作类型（枚举在 CoreEnums.cs）
    public string targetName;            // 目标地名（MoveTo/PatrolAround/Observe 用）
    public string targetAgentId;         // 目标智能体ID（FormationHold 用）
    public float  radius;                // 巡逻半径（PatrolAround 用）
    public float  duration;              // 持续秒数（Wait/Observe 用；-1=条件触发结束）
    public string broadcastContent;      // 广播文本（Broadcast 用）
}

/// <summary>ADM 当前步骤的完整执行上下文。</summary>
[Serializable]
public class ActionExecutionContext
{
    public string   msnId;
    public string   stepId;
    public string   stepText;
    public string   coordinationConstraint;
    public RoleType role;

    public AtomicAction[] actionQueue;
    public int            currentActionIdx;
    public ADMStatus      status;

    public string   currentLocationName;
    public string   originalGoalName;
    public string[] remainingWaypoints;
    public string[] recentEvents;        // 最近3条感知事件，时间倒序
}

/// <summary>黑板条目：智能体向队友广播的状态快照。</summary>
[Serializable]
public class AgentContextUpdate
{
    public string   agentId;
    public string   locationName;
    public string   currentAction;   // AtomicActionType 枚举名
    public string   currentTarget;
    public string   role;

    public string[] plannedTargets;  // 计划访问的目标地名序列
    public string[] recentEvents;    // 最近3条事件摘要

    public float    timestamp;
}
```

---

## 步骤 2：CoreEnums.cs

**文件：** `Other_Modules/CoreEnums.cs`

### 2a. 添加两个新枚举（在已有枚举末尾，`AgentMessage` 类声明之前）

```csharp
public enum AtomicActionType
{
    MoveTo,         // 移动到目标节点
    PatrolAround,   // 绕目标节点巡逻
    Observe,        // 原地观察，激活感知
    Wait,           // 等待（时长或条件）
    FormationHold,  // 保持编队位置跟随目标智能体
    Broadcast,      // 广播消息到组内黑板
    Evade,          // 机动规避
}

public enum ADMStatus
{
    Idle,           // 空闲，等待 StartStep
    Interpreting,   // LLM-A 调用中
    Negotiating,    // 广播 plannedTargets，等待 0.5s 协商窗口
    Running,        // 正在逐步执行 actionQueue
    Interrupted,    // 被感知事件或黑板更新打断
    Replanning,     // LLM-B 调用中
    Done,           // 所有动作完成
    Failed,         // 出错
}
```

### 2b. MessageType 枚举修改

- **删除：** `JudgeEventReport,   // MLAgentsController / CommunicationManager 仍引用`
- **新增**（在 `StartExecution` 之后）：`BoardUpdate,  // 智能体→队内：黑板状态更新，payload=AgentContextUpdate`

---

## 步骤 3：CommunicationManager.cs

**文件：** `Communication_Module/CommunicationManager.cs`

删除以下内容：

1. `DeliverToJudge(AgentMessage message)` 私有方法（整个方法体，约155-170行）
2. `DeliverMessageWithDelay` 协程中 `if (message.Scope == CommunicationScope.Judge)` 的整个 if 分支（约70-74行，含 `yield break`）

> **边界保护：** 删除后 `DeliverMessageWithDelay` 直接走到 `ResolveRecipients → module.ReceiveMessage`，逻辑闭合无需额外处理。

---

## 步骤 4：PlanningModule.cs

**文件：** `LLM_Modules/PlanningModule.cs`

删除第 759 行的空存根方法：

```csharp
public void HandleExecutionEventReport(ExecutionEventReport report) { }
```

---

## 步骤 5：MemoryModule.cs

**文件：** `LLM_Modules/MemoryModule.cs`

删除以下方法（约225-243行）：

```csharp
RememberMissionAssignment(MissionAssignment mission, RoleType role, MissionTaskSlot slot, CommunicationMode commMode)
```

> 无调用方，删除后无编译影响。

---

## 步骤 6：ReflectionModule.cs

**文件：** `LLM_Modules/ReflectionModule.cs`

删除以下3个方法（均无调用方）：

- `NotifyMissionAccepted(MissionAssignment mission, MissionTaskSlot slot, RoleType role)` — 约67-87行
- `NotifyMissionOutcome(MissionAssignment mission, MissionTaskSlot slot, bool success, string summary)` — 约137-155行
- `GetPlanningGuidance(string missionText, string missionId, MissionTaskSlot slot, int maxInsights = 2)` — 约175-192行

---

## 步骤 7：新建 Maps/MapTopologySerializer.cs

**文件：** `Maps/MapTopologySerializer.cs`（新建，静态工具类）

```csharp
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>将 CampusGrid2D 地图数据序列化为 LLM 可读文本。</summary>
public static class MapTopologySerializer
{
    private static readonly Dictionary<string, string> KindLabels = new()
    {
        {"building","楼"}, {"road","路"}, {"water","水域*"}, {"forest","林地#"},
        {"parking","停车□"}, {"green","绿地▽"}, {"sports","运动▽"}, {"bridge","桥"},
    };

    /// <summary>
    /// 生成全局折叠图（约150-200 token）。
    /// 格式：[建筑] A楼, 图书馆, B楼 | [道路] 主干道 | ...
    /// </summary>
    public static string GetGlobalFoldedMap(CampusGrid2D grid, int maxEntries = 60)
    {
        if (grid?.featureSpatialProfileByUid == null) return "(地图不可用)";

        var byKind = new Dictionary<string, List<string>>();
        int total = 0;

        foreach (var p in grid.featureSpatialProfileByUid.Values)
        {
            if (total >= maxEntries) break;
            string displayName = !string.IsNullOrWhiteSpace(p.runtimeAlias) ? p.runtimeAlias : p.name;
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            string kind = p.kind ?? "other";
            if (!byKind.ContainsKey(kind)) byKind[kind] = new List<string>();
            byKind[kind].Add(displayName);
            total++;
        }

        var sb = new StringBuilder("校园地图（节点列表）：\n");
        foreach (var kv in byKind)
        {
            string label = KindLabels.TryGetValue(kv.Key, out string l) ? l : kv.Key;
            sb.Append($"[{label}] {string.Join(", ", kv.Value)}\n");
        }
        sb.Append("注：水域(*)不可进入，林地(#)可通行但提供遮蔽");
        return sb.ToString();
    }

    /// <summary>
    /// 生成 fromNode→toNode 任务子图（约100-150 token），含威胁标注。
    /// 格式：列出两节点路径走廊内的邻近地物及威胁标记。
    /// </summary>
    public static string GetTaskSubgraph(
        CampusGrid2D grid,
        string fromNode,
        string toNode,
        List<string> threatLocations = null)
    {
        if (grid?.featureSpatialProfileByUid == null) return "(子图不可用)";
        if (string.IsNullOrWhiteSpace(fromNode) || string.IsNullOrWhiteSpace(toNode))
            return "(起终点无效)";

        // 获取起终点网格坐标
        if (!grid.TryGetFeatureFirstCell(fromNode, out Vector2Int fromCell, preferWalkable: true) ||
            !grid.TryGetFeatureFirstCell(toNode,   out Vector2Int toCell,   preferWalkable: true))
            return $"(无法定位节点: {fromNode} → {toNode})";

        // 计算包围矩形 + 2格 padding
        int minX = Mathf.Min(fromCell.x, toCell.x) - 2;
        int maxX = Mathf.Max(fromCell.x, toCell.x) + 2;
        int minY = Mathf.Min(fromCell.y, toCell.y) - 2;
        int maxY = Mathf.Max(fromCell.y, toCell.y) + 2;

        var nodes = new List<string>();
        var threats = new HashSet<string>(
            threatLocations ?? new List<string>(),
            System.StringComparer.OrdinalIgnoreCase);

        foreach (var p in grid.featureSpatialProfileByUid.Values)
        {
            if (p.centroidCell.x < minX || p.centroidCell.x > maxX) continue;
            if (p.centroidCell.y < minY || p.centroidCell.y > maxY) continue;
            string name = !string.IsNullOrWhiteSpace(p.runtimeAlias) ? p.runtimeAlias : p.name;
            if (string.IsNullOrWhiteSpace(name)) continue;

            string prefix = threats.Contains(name) ? "[!]" : "";
            nodes.Add($"{prefix}{name}");
        }

        var sb = new StringBuilder($"任务子图（{fromNode} → {toNode}）：\n");
        sb.Append(string.Join(", ", nodes));
        if (threats.Count > 0)
            sb.Append("\n注：[!] 标记位置有威胁报告，建议绕行");
        return sb.ToString();
    }
}
```

---

## 步骤 8：ActionDecisionModule.cs（全量替换）

**文件：** `LLM_Modules/ActionDecisionModule.cs`

```csharp
// LLM_Modules/ActionDecisionModule.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

public class ActionDecisionModule : MonoBehaviour
{
    // ─── 外部依赖 ─────────────────────────────────────────────────
    private PlanningModule    planningModule;
    private LLMInterface      llmInterface;
    private AgentProperties   agentProperties;
    private AgentDynamicState agentState;
    private CommunicationModule commModule;
    private CampusGrid2D       campusGrid;

    // ─── ADM 状态 ─────────────────────────────────────────────────
    private ActionExecutionContext ctx;
    private ADMStatus              status = ADMStatus.Idle;

    // ─── 黑板（队友状态缓存）──────────────────────────────────────
    private readonly Dictionary<string, AgentContextUpdate> blackboard = new();

    // ─── 协商窗口 ─────────────────────────────────────────────────
    private float              negotiationWindowEnd;
    private const float        NegotiationWindowSec = 0.5f;

    // ─── 感知事件队列 ─────────────────────────────────────────────
    private readonly Queue<(string desc, string location)> pendingPerceptionEvents = new();

    // ─── 协程句柄 ─────────────────────────────────────────────────
    private Coroutine activeCoroutine;

    // ─── JSON 提取正则 ────────────────────────────────────────────
    private static readonly Regex JsonBlockRe = new Regex(@"```(?:json)?\s*([\s\S]*?)```");

    // ─────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────

    private void Start()
    {
        planningModule = GetComponent<PlanningModule>();
        llmInterface   = FindObjectOfType<LLMInterface>();
        commModule     = GetComponent<CommunicationModule>();
        campusGrid     = FindObjectOfType<CampusGrid2D>();

        IntelligentAgent agent = GetComponent<IntelligentAgent>();
        if (agent != null)
        {
            agentProperties = agent.Properties;
            agentState      = agent.CurrentState;
        }
    }

    private void Update()
    {
        if (status == ADMStatus.Running && pendingPerceptionEvents.Count > 0)
            HandlePendingPerceptionEvents();

        if (status == ADMStatus.Negotiating && Time.time >= negotiationWindowEnd)
            CheckConflictsAndMaybeReplan();
    }

    // ─────────────────────────────────────────────────────────────
    // 公共接口
    // ─────────────────────────────────────────────────────────────

    /// <summary>由 PlanningModule 在 Active 状态下调用，传入当前步骤。</summary>
    public void StartStep(PlanStep step)
    {
        if (step == null) return;
        if (activeCoroutine != null) { StopCoroutine(activeCoroutine); activeCoroutine = null; }

        ctx = new ActionExecutionContext
        {
            msnId                  = planningModule?.currentMission?.missionId ?? string.Empty,
            stepId                 = step.stepId,
            stepText               = step.text,
            coordinationConstraint = step.constraint ?? string.Empty,
            role                   = agentProperties != null ? agentProperties.Role : RoleType.Scout,
            actionQueue            = null,
            currentActionIdx       = 0,
            status                 = ADMStatus.Idle,
            currentLocationName    = ResolveCurrentLocationName(),
            originalGoalName       = step.text,
            remainingWaypoints     = Array.Empty<string>(),
            recentEvents           = Array.Empty<string>(),
        };

        SetStatus(ADMStatus.Idle);
        activeCoroutine = StartCoroutine(RunLLMA(step));
    }

    /// <summary>由感知模块调用，触发可能的打断与重规划。</summary>
    public void OnPerceptionEvent(string eventDescription, string locationName)
    {
        if (string.IsNullOrWhiteSpace(eventDescription)) return;
        pendingPerceptionEvents.Enqueue((eventDescription, locationName ?? string.Empty));
    }

    /// <summary>由 CommunicationModule 在收到 BoardUpdate 消息时调用。</summary>
    public void OnBoardUpdate(AgentContextUpdate update)
    {
        if (update == null || string.IsNullOrWhiteSpace(update.agentId)) return;
        if (string.Equals(update.agentId, agentProperties?.AgentID, StringComparison.OrdinalIgnoreCase))
            return;   // 忽略自己广播的回声

        blackboard[update.agentId] = update;
        Debug.Log($"[ADM] {agentProperties?.AgentID} 收到黑板更新 from {update.agentId}");

        // Running 状态下检测新冲突
        if (status == ADMStatus.Running && ctx?.actionQueue != null)
        {
            var ownTargets = BuildOwnTargetSet();
            if (update.plannedTargets != null)
            {
                foreach (string t in update.plannedTargets)
                {
                    if (ownTargets.Contains(t))
                    {
                        SetStatus(ADMStatus.Interrupted);
                        activeCoroutine = StartCoroutine(
                            RunLLMB($"队友 {update.agentId} 黑板更新，发现地点冲突"));
                        return;
                    }
                }
            }
        }
    }

    /// <summary>执行层查询当前应执行的原子动作。</summary>
    public AtomicAction GetCurrentAction()
    {
        if (ctx?.actionQueue == null || ctx.currentActionIdx >= ctx.actionQueue.Length) return null;
        return ctx.actionQueue[ctx.currentActionIdx];
    }

    /// <summary>执行层通知当前动作已完成，ADM 推进队列。</summary>
    public void CompleteCurrentAction()
    {
        if (ctx?.actionQueue == null) return;
        ctx.currentActionIdx++;

        if (ctx.currentActionIdx >= ctx.actionQueue.Length)
        {
            SetStatus(ADMStatus.Done);
            planningModule?.CompleteCurrentStep();
            Debug.Log($"[ADM] {agentProperties?.AgentID} 所有动作完成，通知 PlanningModule");
        }
        else
        {
            BroadcastContextUpdate();
        }
    }

    /// <summary>向后兼容：由 IntelligentAgent.MakeDecisionCoroutine 调用的协程入口。</summary>
    public IEnumerator<object> DecideNextAction()
    {
        SyncCampusGridReference();
        commModule?.ProcessMessages();

        if (planningModule == null || !planningModule.HasActiveMission()) yield break;

        PlanStep currentStep = planningModule.GetCurrentStep();
        if (currentStep == null) yield break;

        // 同一步骤已在处理中，不重复启动
        if (ctx != null && ctx.stepId == currentStep.stepId &&
            status != ADMStatus.Idle && status != ADMStatus.Done && status != ADMStatus.Failed)
        {
            yield return null;
            yield break;
        }

        if (status == ADMStatus.Idle || status == ADMStatus.Done || status == ADMStatus.Failed)
            StartStep(currentStep);

        yield return null;
    }

    // ─────────────────────────────────────────────────────────────
    // LLM-A：步骤 → 原子动作序列
    // ─────────────────────────────────────────────────────────────

    private IEnumerator RunLLMA(PlanStep step)
    {
        SetStatus(ADMStatus.Interpreting);

        string globalMap    = campusGrid != null ? MapTopologySerializer.GetGlobalFoldedMap(campusGrid) : "(地图不可用)";
        string taskSubgraph = campusGrid != null ? MapTopologySerializer.GetTaskSubgraph(campusGrid, ctx.currentLocationName, ctx.originalGoalName) : "(子图不可用)";

        string prompt = BuildLLMAPrompt(step, globalMap, taskSubgraph);

        string llmResult = null;
        yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 600));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM-A 返回空");
            SetStatus(ADMStatus.Failed);
            yield break;
        }
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-A 原始回复: {llmResult}");

        AtomicAction[] actions = null;
        try { actions = JsonConvert.DeserializeObject<AtomicAction[]>(ExtractJson(llmResult)); }
        catch (Exception e) { Debug.LogError($"[ADM] LLM-A JSON解析失败: {e.Message}"); SetStatus(ADMStatus.Failed); yield break; }

        if (actions == null || actions.Length == 0)
        {
            Debug.LogError($"[ADM] {agentProperties?.AgentID} LLM-A 解析结果为空数组");
            SetStatus(ADMStatus.Failed);
            yield break;
        }

        ctx.actionQueue      = actions;
        ctx.currentActionIdx = 0;
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-A 生成 {actions.Length} 个动作");

        BroadcastContextUpdate();
        negotiationWindowEnd = Time.time + NegotiationWindowSec;
        SetStatus(ADMStatus.Negotiating);
    }

    private string BuildLLMAPrompt(PlanStep step, string globalMap, string taskSubgraph)
    {
        return
            "你是无人机任务执行层。将步骤文本拆解为 JSON 原子动作数组。\n\n" +
            $"步骤文本：{step.text}\n" +
            $"当前角色：{ctx.role}\n" +
            $"当前位置：{ctx.currentLocationName}\n" +
            $"协调约束：{(string.IsNullOrWhiteSpace(ctx.coordinationConstraint) ? "无" : ctx.coordinationConstraint)}\n\n" +
            $"全局地图：\n{globalMap}\n\n" +
            $"局部路径图：\n{taskSubgraph}\n\n" +
            "原子动作类型枚举：MoveTo, PatrolAround, Observe, Wait, FormationHold, Broadcast, Evade\n\n" +
            "输出要求：\n" +
            "1. 只输出 JSON 数组。\n" +
            "2. 每项字段：actionId(\"aa_N\"), type(枚举名), targetName(地图中存在的地名，无目标填\"\"), " +
               "targetAgentId(\"\"), radius(float), duration(float), broadcastContent(\"\")。\n" +
            "3. 禁止使用地图中不存在的地名。\n" +
            "4. 仅包含步骤所需的动作，不额外添加。\n\n" +
            "示例输入/输出：\n" +
            "步骤：\"飞往A楼\"\n" +
            "[{\"actionId\":\"aa_1\",\"type\":\"MoveTo\",\"targetName\":\"A楼\"," +
            "\"targetAgentId\":\"\",\"radius\":0,\"duration\":0,\"broadcastContent\":\"\"}]";
    }

    // ─────────────────────────────────────────────────────────────
    // LLM-B：重规划
    // ─────────────────────────────────────────────────────────────

    private IEnumerator RunLLMB(string triggerReason)
    {
        SetStatus(ADMStatus.Replanning);

        var teammatesInfo = new System.Text.StringBuilder();
        foreach (var kv in blackboard)
        {
            string targets = kv.Value.plannedTargets != null
                ? string.Join(", ", kv.Value.plannedTargets)
                : "无";
            teammatesInfo.AppendLine($"  {kv.Key}({kv.Value.role}): 计划目标=[{targets}]");
        }

        string globalMap = campusGrid != null ? MapTopologySerializer.GetGlobalFoldedMap(campusGrid) : "(地图不可用)";
        string prompt    = BuildLLMBPrompt(triggerReason, teammatesInfo.ToString(), globalMap);

        string llmResult = null;
        yield return StartCoroutine(llmInterface.SendRequest(prompt, r => llmResult = r, maxTokens: 600));

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM-B 返回空，维持当前执行");
            SetStatus(ADMStatus.Running);
            yield break;
        }
        Debug.Log($"[ADM] {agentProperties?.AgentID} LLM-B 原始回复: {llmResult}");

        AtomicAction[] newActions = null;
        try { newActions = JsonConvert.DeserializeObject<AtomicAction[]>(ExtractJson(llmResult)); }
        catch (Exception e)
        {
            Debug.LogError($"[ADM] LLM-B JSON解析失败: {e.Message}，维持当前执行");
            SetStatus(ADMStatus.Running);
            yield break;
        }

        if (newActions == null || newActions.Length == 0)
        {
            Debug.LogWarning($"[ADM] {agentProperties?.AgentID} LLM-B 解析结果为空，维持当前执行");
            SetStatus(ADMStatus.Running);
            yield break;
        }

        ctx.actionQueue      = newActions;
        ctx.currentActionIdx = 0;
        BroadcastContextUpdate();
        negotiationWindowEnd = Time.time + NegotiationWindowSec;
        SetStatus(ADMStatus.Negotiating);
    }

    private string BuildLLMBPrompt(string triggerReason, string teammatesInfo, string globalMap)
    {
        string remaining = ctx.actionQueue != null
            ? string.Join(", ", Array.ConvertAll(ctx.actionQueue, a => $"{a.type}({a.targetName})"))
            : "无";

        return
            "你是无人机重规划器。根据新情况为本机生成新的原子动作序列。\n\n" +
            $"触发原因：{triggerReason}\n" +
            $"原始步骤：{ctx.stepText}\n" +
            $"当前位置：{ctx.currentLocationName}\n" +
            $"协调约束：{(string.IsNullOrWhiteSpace(ctx.coordinationConstraint) ? "无" : ctx.coordinationConstraint)}\n" +
            $"原计划剩余：{remaining}\n\n" +
            "队友黑板状态：\n" +
            (string.IsNullOrWhiteSpace(teammatesInfo) ? "  (无数据)\n" : teammatesInfo) +
            $"\n全局地图：\n{globalMap}\n\n" +
            "输出要求（与 LLM-A 格式完全相同）：\n" +
            "1. 只输出 JSON 数组。\n" +
            "2. 避免与上述队友的计划目标产生地点冲突。\n" +
            "3. 完成原始步骤的核心目标。";
    }

    // ─────────────────────────────────────────────────────────────
    // 冲突检测与协商
    // ─────────────────────────────────────────────────────────────

    private void CheckConflictsAndMaybeReplan()
    {
        if (ctx?.actionQueue == null) { SetStatus(ADMStatus.Running); return; }

        var ownTargets = BuildOwnTargetSet();
        int myConflict = 0;
        foreach (var kv in blackboard)
            myConflict += CountConflicts(kv.Value.plannedTargets, ownTargets);

        if (myConflict == 0) { SetStatus(ADMStatus.Running); return; }

        // 判断是否为解决者（冲突数最多；同数取 agentId 字典序最小）
        string myId = agentProperties?.AgentID ?? string.Empty;
        bool iAmResolver = true;
        foreach (var kv in blackboard)
        {
            int theirConflict = CountConflicts(kv.Value.plannedTargets, ownTargets);
            if (theirConflict > myConflict ||
                (theirConflict == myConflict && string.Compare(kv.Key, myId, StringComparison.Ordinal) < 0))
            {
                iAmResolver = false;
                break;
            }
        }

        if (iAmResolver)
        {
            activeCoroutine = StartCoroutine(RunLLMB($"与队友地点冲突({myConflict}处)，本机为解决者"));
        }
        else
        {
            // 非解决者：先进入 Running 等待解决者的 BoardUpdate 触发后续检测
            SetStatus(ADMStatus.Running);
        }
    }

    private HashSet<string> BuildOwnTargetSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ctx?.actionQueue == null) return set;
        foreach (var a in ctx.actionQueue)
            if (!string.IsNullOrWhiteSpace(a.targetName))
                set.Add(a.targetName);
        return set;
    }

    private static int CountConflicts(string[] theirTargets, HashSet<string> ownTargets)
    {
        if (theirTargets == null) return 0;
        int n = 0;
        foreach (string t in theirTargets)
            if (ownTargets.Contains(t)) n++;
        return n;
    }

    // ─────────────────────────────────────────────────────────────
    // 感知事件处理
    // ─────────────────────────────────────────────────────────────

    private void HandlePendingPerceptionEvents()
    {
        while (pendingPerceptionEvents.Count > 0)
        {
            var (desc, loc) = pendingPerceptionEvents.Dequeue();
            if (!EventRequiresReplan(desc)) continue;

            // 将事件记录到上下文（最近3条，时间倒序）
            var events = new List<string>(ctx.recentEvents ?? Array.Empty<string>());
            events.Insert(0, $"[{loc}] {desc}");
            if (events.Count > 3) events.RemoveAt(3);
            ctx.recentEvents = events.ToArray();

            SetStatus(ADMStatus.Interrupted);
            BroadcastContextUpdate();
            activeCoroutine = StartCoroutine(RunLLMB($"感知事件: {desc} @ {loc}"));
            return;   // 每次只触发一次重规划
        }
    }

    private static bool EventRequiresReplan(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        string l = desc.ToLowerInvariant();
        return l.Contains("障碍") || l.Contains("威胁") || l.Contains("敌方") ||
               l.Contains("blocked") || l.Contains("enemy") || l.Contains("threat");
    }

    // ─────────────────────────────────────────────────────────────
    // 广播黑板更新
    // ─────────────────────────────────────────────────────────────

    private void BroadcastContextUpdate()
    {
        if (commModule == null || agentProperties == null || ctx == null) return;

        var targets = new List<string>();
        if (ctx.actionQueue != null)
            foreach (var a in ctx.actionQueue)
                if (!string.IsNullOrWhiteSpace(a.targetName) && !targets.Contains(a.targetName))
                    targets.Add(a.targetName);

        AtomicAction cur = GetCurrentAction();
        var update = new AgentContextUpdate
        {
            agentId        = agentProperties.AgentID,
            locationName   = ctx.currentLocationName,
            currentAction  = cur != null ? cur.type.ToString() : "None",
            currentTarget  = cur?.targetName ?? string.Empty,
            role           = ctx.role.ToString(),
            plannedTargets = targets.ToArray(),
            recentEvents   = ctx.recentEvents ?? Array.Empty<string>(),
            timestamp      = Time.time
        };

        blackboard[agentProperties.AgentID] = update;

        commModule.SendScopedMessage(
            CommunicationScope.Team,
            MessageType.BoardUpdate,
            update,
            targetTeamId: agentProperties.TeamID.ToString(),
            reliable: true);
    }

    // ─────────────────────────────────────────────────────────────
    // 工具方法
    // ─────────────────────────────────────────────────────────────

    private void SetStatus(ADMStatus s)
    {
        status = s;
        if (ctx != null) ctx.status = s;
        Debug.Log($"[ADM] {agentProperties?.AgentID} → {s}");
    }

    private string ResolveCurrentLocationName()
    {
        if (agentState == null || campusGrid == null) return "未知位置";
        Vector3 pos = agentState.Position;
        if (campusGrid.TryGetCellFeatureInfoByWorld(pos, out _, out _, out string name, out _, out _)
            && !string.IsNullOrWhiteSpace(name))
            return name;
        return $"({pos.x:F0},{pos.z:F0})";
    }

    private void SyncCampusGridReference()
    {
        if (campusGrid == null) campusGrid = FindObjectOfType<CampusGrid2D>();
        IntelligentAgent agent = GetComponent<IntelligentAgent>();
        if (agentState != null && agent != null)
            agentState.CampusGrid = agent.CampusGrid2D ?? campusGrid;
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        Match m = JsonBlockRe.Match(raw);
        if (m.Success) return m.Groups[1].Value.Trim();
        int start    = raw.IndexOf('[');
        int startObj = raw.IndexOf('{');
        if (startObj >= 0 && (start < 0 || startObj < start)) start = startObj;
        if (start >= 0)
        {
            char open  = raw[start];
            char close = open == '[' ? ']' : '}';
            int end    = raw.LastIndexOf(close);
            if (end > start) return raw.Substring(start, end - start + 1);
        }
        return raw.Trim();
    }
}
```

---

## 步骤 9：CommunicationModule.cs

**文件：** `Communication_Module/CommunicationModule.cs`

### 9a. 新增字段（在 `planningModule` 字段声明后）

```csharp
public ActionDecisionModule admModule;
```

### 9b. Start() 中初始化（在 `planningModule = ...` 之后）

```csharp
admModule = GetComponent<ActionDecisionModule>();
```

### 9c. HandleMessage switch 新增 case（在 `StartExecution` case 之后、`default` 之前）

```csharp
case MessageType.BoardUpdate:
    ForwardPayload<AgentContextUpdate>(message,
        admModule != null ? (Action<AgentContextUpdate>)admModule.OnBoardUpdate : null);
    break;
```

---

## 复用的现有函数/模式

| 函数/模式 | 来源 |
|-----------|------|
| `yield return StartCoroutine(llm.SendRequest(prompt, r => result = r, maxTokens: 600))` | `PlanningModule.cs` |
| `ExtractJson(string raw)` | `PlanningModule.cs`（完整复制） |
| `JsonConvert.DeserializeObject<T>(json)` | `PlanningModule.cs` |
| `campusGrid.TryGetCellFeatureInfoByWorld()` | `CampusGrid2D.cs:445` |
| `campusGrid.TryGetFeatureFirstCell()` | `CampusGrid2D.cs:480` |
| `ForwardPayload<T>(message, handler)` | `CommunicationModule.cs`（已有私有方法） |
| `commModule.SendScopedMessage<T>(scope, type, payload, targetTeamId, reliable)` | `CommunicationModule.cs`（已有） |

---

## 验证方法

1. **编译验证：** Unity Editor 控制台零错误。重点：所有新类有 `[Serializable]`；`using Newtonsoft.Json` 引用正确；删除的方法无残留调用方

2. **单智能体：** 调用 `planningModule.SubmitMissionRequest("飞往图书馆", 1)` → 观察控制台 `[ADM] AgentXX → Interpreting → Negotiating → Running`

3. **冲突解决：** 2个智能体分配到需访问相同节点的步骤 → 确认只有一个 `agentId` 较小的智能体触发 LLM-B

4. **感知中断：** Running 状态下调用 `admModule.OnPerceptionEvent("障碍物检测到", "A楼")` → 观察 `→ Interrupted → Replanning → Negotiating → Running`
