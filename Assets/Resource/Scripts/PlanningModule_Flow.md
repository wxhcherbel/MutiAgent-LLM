# PlanningModule Execution Flow

这份文档配合 `CodeTour` 使用。

- 文档作用：先用中文把 `PlanningModule.cs` 的执行流程顺下来。
- CodeTour 作用：按同一顺序逐步跳到对应函数，边看边对照代码。

建议实验场景：

> 两架无人机从 `building:building_2` 的东侧和西侧同时侦查，并持续同步回报。

这个场景会覆盖 `PlanningModule.cs` 里最重要的一条主链：

`任务提交 -> 任务结构化 -> 槽位拆分 -> 角色偏好上报 -> 协调者分配 -> 个人计划生成 -> 统一放行 -> 步骤上报 -> 全队完成聚合`

## 1. 先看几个核心数据结构

- [`Plan`](./LLM_Modules/PlanningModule.cs) `L13`
  当前智能体自己的执行计划。重点字段有 `steps`、`stepActionTypes`、`stepNavigationModes`、`stepIntents`、`stepRoutePolicies`、`assignedSlot`、`currentStep`。
- [`MissionAssignment`](./LLM_Modules/PlanningModule.cs) `L34`
  整个队伍共享的一张任务单。重点字段有 `missionDescription`、`roles`、`coordinationDirectives`、`taskSlots`。
- [`MissionRole`](./LLM_Modules/PlanningModule.cs) `L49`
  描述一个角色需要什么类型的智能体、几个人、主要职责是什么。
- [`PlanResponse`](./LLM_Modules/PlanningModule.cs) `L61`
  用来接 LLM 返回的结构化计划结果。

## 2. 初始化

- [`Start()`](./LLM_Modules/PlanningModule.cs) `L126`
  拿到 `memoryModule`、`llmInterface`、`agentProperties`、`commModule`，并把默认通信模式设成 `Hybrid`。

你可以把这一步理解成：

> 先确认“我能记忆、我能问大模型、我知道自己是谁、我能和队友说话”。

## 3. 用户提交任务

- [`SubmitMissionRequest(string missionDescription, int agentCount)`](./LLM_Modules/PlanningModule.cs) `L147`
  入口函数。接收用户输入的人话任务和参与智能体数量。
- [`AnalyzeMissionDescription(string description, int agentCount)`](./LLM_Modules/PlanningModule.cs) `L1031`
  把人话任务交给 LLM，请它直接输出结构化的任务类型、通信模式、角色配置、协同规则和 `taskSlots`。
- [`ParseMissionFromLLM(string llmResponse, string description, int agentCount)`](./LLM_Modules/PlanningModule.cs) `L195`
  把 LLM 的回复装配成 `MissionAssignment`。

本例中，`missionDescription` 可以是：

> 两架无人机从教学楼 `building:building_2` 的东侧和西侧同时侦查，并持续同步回报。

这一步结束后，系统已经得到一张“全队任务单”。

## 4. 从大任务拆成具体槽位

- [`ExtractTaskSlotsFromResponse(...)`](./LLM_Modules/PlanningModule.cs) `L2057`
  优先使用 LLM 在任务分析阶段直接给出的 `taskSlots`。
- [`BuildTaskSlotsForMission(string missionDescription, MissionRole[] roles, MissionType missionType, int agentCount)`](./LLM_Modules/PlanningModule.cs) `L2116`
  只有当 LLM 没有提供 `taskSlots` 时，系统才按 `roles.requiredCount` 做最保守的机械展开。

现在这部分的原则已经改成：

- 槽位语义由 LLM 决定
- 系统不再从 `missionDescription` 里枚举“东侧/西侧/侦查/接近”这类关键词
- 系统 fallback 只保证“能分配、能执行”，不再自己脑补复杂协同含义

所以如果本例真的需要“东侧一台、西侧一台”，正确来源应该是：

> LLM 在 `taskSlots` 里明确返回两个不同的槽位，而不是系统自己猜出来。

## 5. 协调者开始广播任务

- [`DistributeMissionToAgents(MissionAssignment mission)`](./LLM_Modules/PlanningModule.cs) `L482`
  由协调者负责广播任务、清空旧状态、初始化新一轮分配状态。

这一步会重置一些很重要的状态变量：

- `remainingCount`：每种角色还剩几个名额
- `receivedPreferences` / `receivedPreferencePayloads`：已经收到哪些智能体的角色偏好
- `assignedTeamDecisions`：最后每个智能体被分到了什么岗位和槽位
- `acceptedAssignedAgents`：哪些人已经确认接受分配
- `completedAssignedAgents`：哪些人已经完成各自槽位
- `missionCompletionAggregated`：整队任务是否已经正式收口
- `teamExecutionReleased`：是否已经统一放行执行

## 6. 智能体第一次收到任务：先报角色偏好

- [`ReceiveMissionAssignment(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)`](./LLM_Modules/PlanningModule.cs) `L153`
  这是任务下发后的统一入口。

当 `specificRole == null` 时，说明：

> 现在只是让你了解整队任务，还没正式给你定岗位。

这时会走：

- [`AnalyzeRolePreference(MissionAssignment mission, Action<RoleType[]> onResult)`](./LLM_Modules/PlanningModule.cs) `L222`
  让 LLM 根据智能体属性判断“我更适合什么角色”。
- [`SendRolePreferenceToCoordinator(MissionAssignment mission, RoleType[] preferences)`](./LLM_Modules/PlanningModule.cs) `L174`
  把角色偏好发给协调者。

本例里，两架无人机大概率都会回报 `Scout` 相关偏好。

## 7. 协调者裁决谁去哪个槽位

- [`WaitAndAssignRoles(MissionAssignment mission)`](./LLM_Modules/PlanningModule.cs) `L523`
  等待一段时间，尽量收齐大家的角色偏好。
- [`FindBestAgentForSlot(MissionTaskSlot slot, HashSet<string> assignedAgents)`](./LLM_Modules/PlanningModule.cs) `L588`
  给一个槽位找最合适的智能体。
- [`CalculateSlotAssignmentScore(string agentId, MissionTaskSlot slot)`](./LLM_Modules/PlanningModule.cs) `L609`
  具体打分函数。

打分思路很直观：

- 角色偏好越匹配，分越高
- 机型越匹配，分越高
- 速度、感知范围更强，会加分
- 如果 LLM 已经在槽位里明确给出某些结构化偏好，评分会尽量利用这些显式信息

最后协调者会把最终裁决发出去。裁决结果里不只包含角色，还包含：

- 分到的 `slot`
- 该槽位的目标
- 接近方向
- 与该槽位绑定的协同约束

## 8. 智能体第二次收到任务：这次开始生成个人计划

当 `ReceiveMissionAssignment(...)` 里 `specificRole` 不为空时，说明：

> 协调者已经决定“你是谁、你去哪、你负责什么”。

这时会走：

- [`AnalyzeMissionAndCreatePlan(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)`](./LLM_Modules/PlanningModule.cs) `L305`
  让 LLM 按当前智能体的岗位和槽位生成 3 到 5 步的个人执行计划。
- [`ParseAndCreatePlan(string llmResponse, MissionAssignment mission, RoleType? specificRole, MissionTaskSlot specificSlot)`](./LLM_Modules/PlanningModule.cs) `L414`
  把 LLM 返回的步骤、动作类型、导航模式、结构化意图、路径策略都写进 `currentPlan`。

本例中，只要 LLM 返回了两个不同的 `assignedSlot`，两台侦查机的计划就会自然分化；系统本身不再负责从自然语言里猜这个差异。

## 9. LLM 输出解析与兜底

这一组函数主要解决一个现实问题：

> 大模型有时返回的 JSON 不够规范，但系统还是得尽量把计划救回来。

关键函数：

- [`ExtractPureJson(string response)`](./LLM_Modules/PlanningModule.cs) `L1484`
  从一大段文本里尽量截出真正的 JSON 主体。
- [`ExtractMissionAnalysisResponse(string response)`](./LLM_Modules/PlanningModule.cs) `L1581`
  负责解析任务级 JSON，重点关注 `roles`、`taskSlots`、`coordinationDirectives`。
- [`TryParsePlanResponse(string response, out PlanResponse planResponse, out string normalizedJson, out string error)`](./LLM_Modules/PlanningModule.cs) `L1722`
  标准解析入口。
- [`TryParsePlanResponseLoosely(string raw, out PlanResponse planResponse)`](./LLM_Modules/PlanningModule.cs) `L1856`
  当 JSON 缺逗号、缺结尾、被截断时，尝试容错恢复。
- [`CreateDefaultPlan(MissionAssignment mission, RoleType? specificRole = null, MissionTaskSlot specificSlot = null)`](./LLM_Modules/PlanningModule.cs) `L971`
  如果计划解析失败，就给一个保底计划。
- [`CreateDefaultMission(string description, int agentCount)`](./LLM_Modules/PlanningModule.cs) `L2207`
  如果连整队任务结构化都失败，就给一个保底任务。

所以这份代码不是“只能在理想情况下工作”，而是做了明显的兜底。

## 10. 计划生成后，先确认，再统一放行

- [`SendRoleAcceptance(string role, string reasoning)`](./LLM_Modules/PlanningModule.cs) `L2233`
  智能体告诉协调者：“我收到了，并接受这个岗位和槽位。”
- [`HandleRoleAcceptancePayload(RoleAcceptancePayload payload)`](./LLM_Modules/PlanningModule.cs) `L2388`
  协调者记录谁已经接受。
- [`TryReleaseAssignedExecution()`](./LLM_Modules/PlanningModule.cs) `L2300`
  当所有已分配成员都确认接受后，协调者统一发出执行放行。
- [`ReleaseExecutionForAssignedPlan(string slotId)`](./LLM_Modules/PlanningModule.cs) `L2349`
  本地收到放行后，允许当前计划真正开始执行。

这里的设计思想是：

> 多智能体任务先把队形站好，再一起起跑，避免有人先冲出去破坏协同。

对应的关键变量：

- `localExecutionReleased`
- `teamExecutionReleased`

## 11. 执行阶段，对外提供“当前该做什么”

这组函数是给其它模块查当前计划用的：

- [`GetCurrentTask()`](./LLM_Modules/PlanningModule.cs) `L2259`
- [`GetMissionProgress()`](./LLM_Modules/PlanningModule.cs) `L2268`
- [`GetCurrentStepDescription()`](./LLM_Modules/PlanningModule.cs) `L772`
- [`GetCurrentStepIntent()`](./LLM_Modules/PlanningModule.cs) `L784`
- [`GetCurrentStepRoutePolicy()`](./LLM_Modules/PlanningModule.cs) `L802`
- [`GetCurrentCoordinationDirectives()`](./LLM_Modules/PlanningModule.cs) `L819`
- [`HasActiveMission()`](./LLM_Modules/PlanningModule.cs) `L2551`
- [`GetMissionPriority()`](./LLM_Modules/PlanningModule.cs) `L2559`

判断当前步骤“像不像移动、要不要 A*、是不是通信观察”的函数有：

- [`IsMovementLikeStep(string stepText)`](./LLM_Modules/PlanningModule.cs) `L838`
- [`IsCommunicationOrObservationStep(string stepText)`](./LLM_Modules/PlanningModule.cs) `L854`
- [`IsLikelyLocalStep(string stepText)`](./LLM_Modules/PlanningModule.cs) `L866`
- [`HasGlobalTargetHint(string stepText)`](./LLM_Modules/PlanningModule.cs) `L877`
- [`ShouldPreferAStarForStep(string stepText)`](./LLM_Modules/PlanningModule.cs) `L888`
- [`ShouldPreferAStarForCurrentStep()`](./LLM_Modules/PlanningModule.cs) `L898`

它们不是负责“执行动作”，而是负责回答：

> 当前步骤更像移动、观察还是通信？更适合全局导航还是局部直行？

## 12. 每完成一步，就上报进度

- [`CompleteCurrentTask()`](./LLM_Modules/PlanningModule.cs) `L2466`
  当前步骤完成后，把 `currentStep` 往后推一格。
- [`ReportStepCompletion(string step)`](./LLM_Modules/PlanningModule.cs) `L2500`
  如果还没做完最后一步，向协调者上报“我完成了哪一步，下一步是什么”。
- [`ReportMissionCompletion()`](./LLM_Modules/PlanningModule.cs) `L2527`
  如果当前槽位的最后一步也完成了，就向协调者上报“我这个槽位做完了”。

## 13. 协调者收所有人的进度，最后统一收口

- [`HandleTaskProgressPayload(TaskProgressPayload payload)`](./LLM_Modules/PlanningModule.cs) `L2412`
  协调者接收普通进度上报和任务完成上报。
- [`TryFinalizeCoordinatedMission()`](./LLM_Modules/PlanningModule.cs) `L2442`
  只有当所有已分配槽位都完成后，才把整个协同任务记为完成。
- [`IsWaitingForTeamCompletion()`](./LLM_Modules/PlanningModule.cs) `L2366`
  协调者本地步骤可能已经跑完了，但只要全队还没完成，它仍处于等待队友的状态。

这一步体现了文件里最重要的协同思想：

> “我做完”不等于“全队做完”。

## 14. 建议的阅读顺序

如果你是第一次顺代码，建议按这个顺序点：

1. `Start`
2. `SubmitMissionRequest`
3. `AnalyzeMissionDescription`
4. `ParseMissionFromLLM`
5. `ExtractTaskSlotsFromResponse`
6. `DistributeMissionToAgents`
7. `ReceiveMissionAssignment`
8. `AnalyzeRolePreference`
9. `SendRolePreferenceToCoordinator`
10. `WaitAndAssignRoles`
11. `FindBestAgentForSlot`
12. `CalculateSlotAssignmentScore`
13. `AnalyzeMissionAndCreatePlan`
14. `ParseAndCreatePlan`
15. `HandleRoleAcceptancePayload`
16. `TryReleaseAssignedExecution`
17. `ReleaseExecutionForAssignedPlan`
18. `CompleteCurrentTask`
19. `ReportStepCompletion`
20. `ReportMissionCompletion`
21. `HandleTaskProgressPayload`
22. `TryFinalizeCoordinatedMission`

## 15. 与 CodeTour 的配合方式

1. 安装 `CodeTour` 插件。
2. 在 VS Code 里打开 `CodeTour: Start Tour`。
3. 选择 `PlanningModule Execution Flow`。
4. 一边看 tour 的步骤说明，一边对照这份 `PlanningModule_Flow.md`。

如果你只想快速抓主线，就重点看第 3 到第 13 节。
