# 主链架构评审与提示词优化报告

## 结论摘要

当前主链已经具备一个可跑通的多智能体闭环雏形: `PlanningModule` 负责任务解析、分组、槽位协商和步骤拆解, `ActionDecisionModule` 负责滚动决策, `PerceptionModule` 和 `AgentMotionExecutor` 分别负责环境采样与动作执行, `SharedWhiteboard` 承担组内协同状态共享。

但如果拿主流工程框架和主流 agent 方法论做基准, 当前系统的主要短板并不在“有没有模块”, 而在“模块之间的协议是否稳定、失败时是否可恢复、观测是否真正回流、执行是否被严格约束”。它目前更像是“多段 Prompt + 多个 MonoBehaviour 状态机”的组合, 还不是一个具备强恢复性、强可验证性、强可观测性的 agent runtime。

默认未评 `MemoryModule` / `ReflectionModule`, 仅在缺席会影响主链判断时提及。

## Findings

### 1. 高风险: API Key 明文写在代码里, 且请求/回复被完整落盘

- 证据:
  [LLMInterface.cs:20](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_API/LLMInterface.cs#L20)
  [LLMInterface.cs:25](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_API/LLMInterface.cs#L25)
  [LLMInterface.cs:189](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_API/LLMInterface.cs#L189)
  [LLMInterface.cs:258](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_API/LLMInterface.cs#L258)
  [LLMInterface.cs:366](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_API/LLMInterface.cs#L366)
- 现状:
  `providerConfig.apiKey` 在默认配置中直接写死, 同时 `AppendToLog` 和文本日志会把 prompt、响应、模型信息持续写到本地日志文件。
- 问题:
  这是工程成熟度里最明显的短板之一。它不只是“安全规范不佳”, 而是会让实验环境、共享仓库、演示机器和日志目录都变成泄露面。更严重的是, 当前主链大量 prompt 带有任务内容、组内状态、位置信息、协同约束, 一旦落盘就不再只是 API Key 风险, 也是业务上下文泄露风险。
- 对比主流:
  OpenAI Agents SDK 文档把 `Sessions`、`Guardrails`、`Tracing`、`Strict Schema` 都当成独立能力, 说明现代 agent 工程默认把“状态留存”和“安全控制”分开设计, 而不是把所有原始输入输出直接打到普通日志里。
- 判断:
  这是明确的工程缺陷, 不是路线差异。

### 2. 高风险: 主链没有形成真正闭环的“感知 -> 决策 -> 再规划”, 观测事实上没有稳定回流到 LLM

- 证据:
  [PerceptionModule.cs:403](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Perception/PerceptionModule.cs#L403)
  [ActionDecisionModule.cs:26](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L26)
  [ActionDecisionModule.cs:113](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L113)
  [ActionDecisionModule.cs:354](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L354)
  [ActionDecisionModule.cs:917](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L917)
  [IntelligentAgent.cs:166](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Agents/IntelligentAgent.cs#L166)
  [IntelligentAgent.cs:181](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Agents/IntelligentAgent.cs#L181)
- 现状:
  `PerceptionModule` 会把敌方发现事件送到 `ActionDecisionModule.OnPerceptionEvent()`。
  但 ADM 只是把事件压进 `pendingPerceptionEvents` 队列, `HandlePendingPerceptionEvents()` 仍是 `TODO`, `BuildPerceptionSnapshot()` 在构造 prompt 时又被注释掉。
  `IntelligentAgent.ShouldMakeDecision()` 里基于“有未读消息 / 有新感知”的触发条件也被注释掉了。
- 问题:
  这意味着系统名义上是 ReAct 风格的滚动决策, 实际上却主要依赖“当前步骤 + 局部地图 + 白板”来决策, 对实时环境变化的吸收是断裂的。也就是说, 感知模块产生了事实, 但这些事实没有稳定地变成 LLM 的下一轮输入。
- 对比主流:
  ReAct的核心不是“有思维链”, 而是推理和行动交替, 并且动作结果与外部观测会反过来更新后续计划。Voyager也强调环境反馈、执行错误和自验证共同驱动下一轮提示。当前实现和这两个基准的差距就在这里。
- 判断:
  这是当前主链最核心的结构性短板之一。

### 3. 高风险: ADM 在关键失败场景下会把未完成步骤直接标记为完成

- 证据:
  [ActionDecisionModule.cs:305](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L305)
  [ActionDecisionModule.cs:308](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L308)
  [ActionDecisionModule.cs:335](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L335)
  [ActionDecisionModule.cs:337](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L337)
- 现状:
  当 `isDone=false` 但 `nextActions` 为空时, 当前步骤会被强制 `CompleteCurrentStep()`。
  当滚动规划超过 `MaxIterations` 时, 当前步骤也会被强制 `CompleteCurrentStep()`。
- 问题:
  这会把“模型没想明白 / schema 不合法 / prompt 信息不足 / 执行长期卡住”的问题伪装成“任务完成”。而后续的 `DoneSignal`、`GroupMonitor`、跨组通知都会建立在这个错误前提上, 最终让系统看起来能跑完, 但其实 correctness 已经破坏了。
- 对比主流:
  LangGraph 的 durable execution 和 checkpointing, OpenAI Agents SDK 的 guardrails / strict schema, 本质都在避免“坏输出悄悄继续推进”。当前实现则是反过来, 在坏输出时主动前推状态。
- 判断:
  这是 correctness 层面的严重问题。

### 4. 高风险: 运行态约束回填过于脆弱, 协同协议依赖自由文本兜底

- 证据:
  [PlanningModule.cs:983](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L983)
  [PlanningModule.cs:1004](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L1004)
  [PlanningModule.cs:1039](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L1039)
  [PlanningModule.cs:1056](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L1056)
  [PlanningModule.cs:1136](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L1136)
  [PlanningModule.cs:229](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L229)
- 现状:
  规划阶段允许 LLM 用 `slotId`、`role`、`desc` 这些抽象引用来表达 `watchAgent` 和 `syncWith`, 运行态再通过 `ResolveRuntimeAgentRef()` 把它们回填成真实 `agentId`。
- 问题:
  `slotId` 还算稳定, 但 `role` 与 `desc` 都可能重复、模糊或被 LLM 轻微改写。当前代码已经承认这一点, 所以用了 `uniqueRoleToAgentId` 和 `uniqueDescToAgentId` 做兜底, 这恰恰说明协议边界并不牢固。对于协同系统来说, 约束绑定一旦漂移, 后果比普通计划错误更难排查。
- 对比主流:
  主流多 agent 框架更强调结构化身份、显式路由和稳定的 state schema, 而不是依赖“让模型先写一个抽象文本, 后面再猜它指的是谁”。
- 判断:
  这是协同层的结构性设计问题。

### 5. 中高风险: 任务完成判定过弱, `GroupMonitor` 很容易被错误 DoneSignal 误导

- 证据:
  [GroupMonitor.cs:147](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/GroupMonitor.cs#L147)
  [GroupMonitor.cs:151](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/GroupMonitor.cs#L151)
  [GroupMonitor.cs:165](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/GroupMonitor.cs#L165)
  [GroupMonitor.cs:170](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/GroupMonitor.cs#L170)
  [GroupMonitor.cs:232](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/GroupMonitor.cs#L232)
- 现状:
  Phase 1 只要求“每个成员至少有一个 DoneSignal”。
  Phase 2 的 prompt 只给出任务描述和成员摘要, 不带约束状态、不带未完成步骤、不带白板上下文, 也没有启用 JSON mode。
- 问题:
  如果前面的 ADM 已经存在“伪完成”, 这里就很难把它拦住。哪怕没有伪完成, 只看成员摘要也不足以判断复杂协同任务是否真的满足了互斥、同步、覆盖范围、先后依赖等约束。
- 对比主流:
  CrewAI Flows 和 LangGraph 都强调流程状态与控制流是显式对象, 完成判定应该基于状态, 而不是只让一个 LLM 对摘要做二次猜测。
- 判断:
  这是主链 correctness 的次级风险点。

### 6. 中高风险: 主链整体是“内存态 + 轮询态”, 几乎没有恢复能力

- 证据:
  [PlanningModule.cs:17](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L17)
  [PlanningModule.cs:873](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L873)
  [SharedWhiteboard.cs:56](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/SharedWhiteboard.cs#L56)
  [SharedWhiteboard.cs:68](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/SharedWhiteboard.cs#L68)
  [IntelligentAgent.cs:153](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Agents/IntelligentAgent.cs#L153)
  [CommunicationModule.cs:37](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Communication_Module/CommunicationModule.cs#L37)
- 现状:
  任务状态、组状态、约束字典、白板内容、消息队列全在内存里。状态推进依赖 `Update()`/`Coroutine` 轮询与超时。
- 问题:
  中途停机、切场景、脚本重载、网络超时后无法恢复到精确的任务点位。调试也主要靠日志和现场观察, 缺少可重放的 execution trace。
- 对比主流:
  LangGraph 强调 checkpoint 和 thread, CrewAI Flows 强调 state persistence, OpenAI Agents SDK 明确提供 sessions、interruptions、resume state。当前系统在这个维度基本没有对应能力。
- 判断:
  这更像“框架成熟度差距”, 不是单个 bug。

### 7. 中风险: 决策与通信仍是轮询主导, 不是事件驱动

- 证据:
  [IntelligentAgent.cs:30](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Agents/IntelligentAgent.cs#L30)
  [IntelligentAgent.cs:153](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Agents/IntelligentAgent.cs#L153)
  [IntelligentAgent.cs:181](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Agents/IntelligentAgent.cs#L181)
  [CommunicationModule.cs:37](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Communication_Module/CommunicationModule.cs#L37)
  [CommunicationModule.cs:198](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/Communication_Module/CommunicationModule.cs#L198)
- 现状:
  `IntelligentAgent` 每 2 秒检查一次是否需要决策, `CommunicationModule` 在 `Update()` 中处理消息, 感知触发条件和未读消息触发条件都没有真正进入决策入口。
- 问题:
  这会降低协同响应性, 也会让“消息到达 / 感知突发 / 约束满足”这些本该立即改变状态的事件, 退化成下一次轮询时才可能被看见。
- 对比主流:
  AutoGen Core 官方文档明确把异步消息和 event-driven architecture 当作核心卖点。当前实现和这一点差距很明显。
- 判断:
  这是框架层设计偏弱, 也是后续协同不稳定的重要来源。

### 8. 中风险: LLM 接口契约偏弱, Prompt 责任堆叠过多

- 证据:
  [LLMInterface.cs:270](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_API/LLMInterface.cs#L270)
  [LLMInterface.cs:297](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_API/LLMInterface.cs#L297)
  [PlanningModule.cs:118](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L118)
  [PlanningModule.cs:229](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L229)
  [PlanningModule.cs:387](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L387)
  [PlanningModule.cs:472](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L472)
  [ActionDecisionModule.cs:346](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L346)
- 现状:
  目前主要依赖“大块 user prompt + JSON mode + 反序列化”。
  没有 system/user 分层, 没有版本化 prompt 模板, 没有严格 schema 校验, 也没有把 prompt 构造与业务状态读取分离。
- 问题:
  一旦 prompt 变长、例子变多、字段变复杂, 维护成本和偶发漂移都会快速上升。尤其 `LLM#1`、`LLM#2` 和 ADM prompt 都承担了太多角色: 既要解释任务, 又要做协议绑定, 还要限定输出格式。
- 对比主流:
  OpenAI Agents SDK 把 handoffs、guardrails、strict schema、tracing 分成独立能力, 不是全压在 prompt 文本里。当前实现更像“用 prompt 补 runtime 能力”。
- 判断:
  这是当前提示词系统的根本问题。

### 9. 中风险: 槽位分配机制缺少能力约束, 只能做到“先选先得”

- 证据:
  [PlanningModule.cs:387](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L387)
  [PlanningModule.cs:747](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L747)
- 现状:
  `LLM#3` 选槽时只看 `slots + 电量 + 位置`, 组长再按到达时间解决冲突。
- 问题:
  这让分槽更像“偏好投票”, 不是“能力匹配”。当多个智能体角色相近、位置相近时还勉强可用, 但一旦任务对感知范围、载荷、速度、通信半径、地形适应性有差异, 这个机制就会失真。
- 对比主流:
  CrewAI 把能力和任务委派绑定得更紧, 而你现在这套更像先让模型随意选, 再由 leader 兜底修正。
- 判断:
  这是任务分解层设计不够扎实的表现。

## 主流框架对照

| 维度 | 当前实现 | 主流框架/方法常见做法 | 当前缺口 | 是否结构性问题 |
|---|---|---|---|---|
| 编排 | 多个 MonoBehaviour + Coroutine + 轮询状态机 | LangGraph 用显式 state graph, CrewAI Flows 用显式 flow/state, AutoGen 用 actor/message | 缺少显式流程图、检查点、统一状态推进语义 | 是 |
| 恢复 | 任务、白板、消息全在内存 | LangGraph 持久化 checkpoints, OpenAI Agents SDK 提供 sessions / interruptions / resume state | 中断后难恢复, 调试难重放 | 是 |
| 协同协议 | 约束靠自由文本和回填推断 | 主流更强调稳定 ID、明确路由、可验证 schema | `watchAgent/syncWith` 绑定脆弱 | 是 |
| 感知闭环 | 感知有采样, 但未稳定进入 prompt | ReAct/Voyager 都依赖环境反馈驱动下一步 | 缺少真正 observe-act-replan 闭环 | 是 |
| 执行约束 | `AtomicAction` 表面结构化, 细节仍靠 `actionParams` 文本 | 更倾向 typed tool args / strict schema / executable skill | 执行层对自由文本依赖过高 | 是 |
| 观测与调试 | 主要靠 Debug.Log 和普通日志 | AutoGen / LangGraph / OpenAI SDK 都强调 tracing / observability | 只能事后看日志, 难做可视化回放 | 否, 但缺口明显 |
| 安全 | 明文 key + 原始 prompt/response 落盘 | 主流默认环境变量、session 层隔离、guardrails | 安全边界明显不足 | 否, 但严重 |

## 提示词评审与优化建议

### LLM#1 任务解析 Prompt

- 位置:
  [PlanningModule.cs:118](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L118)
- 问题:
  一个 prompt 里同时做 `relType` 判断、`groupCnt` 推断、`groupMsns` 生成、`constraints` 提取, 语义负担过重。
  C1/C2/C3 的说明过长, 示例很强, 容易让模型“学例子”而不是“遵 schema”。
- 优化:
  先把 schema 放在前面, 再给规则, 最后给一个最短示例。
  明确枚举值和默认值, 尽量减少解释性 prose。
  加一条负约束: “没有明确等待/互斥关系时, 不要生成 C3”。
  加一条一致性约束: `groupCnt` 不得大于 `agentCount`, `Cooperation` 时必须为 1。
- 更关键的协议优化:
  最好拆成两个调用:
  第一步只做任务关系与分组。
  第二步只做约束抽取。
  如果暂时不拆, 也建议至少把输出对象显式列成字段清单。

### LLM#2 槽位生成 Prompt

- 位置:
  [PlanningModule.cs:229](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L229)
- 问题:
  同一个 prompt 同时生成 `slots` 和重写 `constraints`, 并且后续运行态还要再回填 agentId。
  这使它既是 planner, 又是 protocol writer, 还兼任 schema aligner。
- 优化:
  强制 `watchAgent` 和 `syncWith` 只允许写 `slotId`, 禁止写 `role`、`desc` 或自然语言描述。
  要求每个 `slot` 额外给出 `primaryTarget` 或 `coverageArea`, 让 LLM#3 有更清晰的选择依据。
  明确“同一 constraintId 绑定到哪些 slot”时必须列全, 不允许模糊表述。
- 结论:
  这是当前最值得做结构收缩的 prompt 之一。

### LLM#3 选槽 Prompt

- 位置:
  [PlanningModule.cs:387](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L387)
- 问题:
  输入过薄, 只有可选槽、位置、电量。没有速度、感知范围、通信范围、载荷、当前负担、与目标距离估计。
- 优化:
  增加最少这些输入:
  `agentType`, `role`, `maxSpeed`, `perceptionRange`, `communicationRange`, `battery`, `currentLocation`, `distanceHintToSlotTarget`。
  输出里建议至少加 `why` 和 `confidence`, 方便 leader 做冲突消解时不只靠时间戳。
- 结论:
  当前版本更像 demo 级 prompt, 不是生产级分配策略。

### LLM#4 步骤拆解 Prompt

- 位置:
  [PlanningModule.cs:472](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/PlanningModule.cs#L472)
- 问题:
  “只拆解 desc 中明确出现的动作, 禁止补充推断步骤”过于保守。
  这会把“为可执行性必须补的桥接步骤”挤压到 ADM 再去猜。
- 优化:
  改成:
  “禁止扩展业务目标, 但允许补充使执行闭合所必需的桥接步骤。”
  增加 `stepType`、`completionEvidence`、`preferredAtomicTypes` 之类的轻量字段, 让 ADM 不必全靠自然语言解释 step。
- 结论:
  当前 LLM#4 太怕过度推断, 反而导致执行层承担了过多推断。

### ADM 滚动规划 Prompt

- 位置:
  [ActionDecisionModule.cs:346](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs#L346)
- 问题:
  当前 prompt 最大的问题不是写得不细, 而是输入不闭合:
  没有稳定注入感知快照。
  没有消费待处理 perception events。
  `actionParams` 仍是自由文本。
  `thought` 被要求输出, 但没有后续机器消费价值。
- 优化:
  恢复并压缩环境观测块, 至少注入:
  `recentEvents`, `nearbyDynamicObstacles`, `nearbyEnemies`, `lastActionResult`, `blockedTargets`。
  把输出改为更强的控制信号:
  `decision = continue | wait | replan | done | fail`
  `reason`
  `nextActions`
  `blockedBy`
  `needsHumanReview`
  把 `actionParams` 改为按动作类型分开的结构化参数对象, 不再让执行器解析自然语言。
- 结论:
  这是最需要从“Prompt 驱动”转成“协议驱动”的一段。

### GroupMonitor 完成度评估 Prompt

- 位置:
  [GroupMonitor.cs:232](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/GroupMonitor.cs#L232)
- 问题:
  输入过于简陋, 只看任务和成员摘要, 不看剩余约束、不看白板状态、不看任务失败信号。
  调用时也没有启用 JSON mode。
- 优化:
  最少应加入:
  `mission`, `memberDoneSignals`, `activeConstraints`, `unmetConstraints`, `recentWhiteboardSignals`。
  并显式要求:
  “如果仍存在未满足同步/互斥/前置条件, `completed` 必须为 false。”
- 结论:
  当前这个 prompt 更像演示验证器, 不足以做可信的组任务完成判定。

## 最小改造优先级

1. 停止把明文 API Key 和完整 prompt/response 原样落盘。
2. 修正 ADM 的失败语义: `nextActions` 为空和超迭代时不能自动判完成。
3. 把感知真正接回 ADM prompt: 吃掉 `pendingPerceptionEvents`, 恢复 `BuildPerceptionSnapshot()`。
4. 收紧运行态约束协议: `watchAgent/syncWith` 只允许稳定 ID, 不再允许 `desc/role` 兜底回填。
5. 强化 `GroupMonitor` 完成判定输入, 不再只看 DoneSignal 摘要。
6. 逐步把 `actionParams` 从自由文本迁移到按动作类型的结构化字段。
7. 再考虑更高阶的 checkpoint / resume / tracing 能力。

## 参考基准

- LangGraph Overview: https://docs.langchain.com/oss/python/langgraph/overview
- LangGraph Persistence: https://docs.langchain.com/oss/python/langgraph/persistence
- AutoGen Core User Guide: https://microsoft.github.io/autogen/stable/user-guide/core-user-guide/index.html
- CrewAI Introduction: https://docs.crewai.com/en/introduction
- OpenAI Agents SDK Docs: https://openai.github.io/openai-agents-python/
- ReAct (OpenReview): https://openreview.net/forum?id=tvI4u1ylcqs
- Voyager: https://voyager.minedojo.org/
