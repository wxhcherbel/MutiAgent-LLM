 滚动规划 + 白板/裁判 + 结构化约束 实现计划                                                     

 Context

 当前 RunLLMA 是一次性规划：进入步骤时调用一次 LLM
 生成完整动作序列后顺序执行，无法根据执行中途的环境变化自适应调整，也没有步骤完成判断机制。     

 用户要求：
 1. 滚动规划：每次 LLM 生成 1-3 个动作，执行完后重新感知环境，LLM
 判断步骤是否完成，如未完成则继续生成下一批动作
 2. 结构化约束：将 PlanStep.constraints string[] 完全替换为
 StructuredConstraint[]，PlanningModule 的 LLM 提示同步升级
 3. 白板（SharedWhiteboard）：组内共享状态池，ADM 每次 LLM 调用前读写白板
 4. 裁判（RefereeManager）：宣布物理世界客观事实，ADM 被动监听

 用户选择：约束字段完全替换 / LLM 判断步骤完成 / 两者都实现 / LLM 自决生成 1-3 个动作。

 ---
 涉及文件

 新建

 - Assets/Resource/Scripts/SharedWhiteboard.cs - 组内白板（全局单例 MonoBehaviour）
 - Assets/Resource/Scripts/RefereeManager.cs - 裁判事件系统（全局单例 MonoBehaviour）

 修改

 - LLM_Modules/Data/Planning/PlanContracts.cs - 新增 StructuredConstraint，更新
 PlanStep、PlanSlot
 - LLM_Modules/Data/Planning/MissionContracts.cs - ParsedMission 新增
 constraints[]，GroupBootstrapPayload 新增 constraints[]
 - LLM_Modules/Data/Planning/ActionContracts.cs - ActionExecutionContext 新增滚动规划状态字段   
 - Enums/ExecutionEnums.cs - ADMStatus 新增 BatchDone
 - LLM_Modules/ActionDecisionModule.cs - 重构 RunLLMA → 滚动规划循环
 - LLM_Modules/PlanningModule.cs - 更新 LLM#1/LLM#4 提示词，新增约束查询接口

 ---
 实现步骤

 Step 1：数据结构升级（PlanContracts.cs）

 新增 StructuredConstraint 类（完整字段对应 constraint-reference.html）：
 // 通用字段
 string constraintId; string cType; string channel; string[] involvedAgents;
 // C1 Assignment
 string subject; string targetObject; bool exclusive;
 // C2 Completion
 string condition; string[] syncWith;
 // C3 Coupling
 int sign; string watchAgent; string reactTo;
 // C4 Priority
 string[] overrides; string triggerCondition; string fallback;

 更新 PlanStep：
 - 删除 string[] constraints（文本）
 - 新增 string[] constraintIds（引用 StructuredConstraint）

 更新 PlanSlot：
 - 删除 StepConstraint[] coordinationConstraints（旧类）
 - 新增 string[] constraintIds（引用）

 删除旧的 StepConstraint 类（被 StructuredConstraint 取代）

 Step 2：任务合约升级（MissionContracts.cs）

 ParsedMission 新增：
 - StructuredConstraint[] constraints - LLM#1 提取的全量约束列表

 GroupBootstrapPayload 新增：
 - StructuredConstraint[] constraints - 广播给全组成员，让每人持有完整约束集

 Step 3：执行上下文升级（ActionContracts.cs）

 ActionExecutionContext 新增：
 - StructuredConstraint[] stepConstraints - 当前步骤关联的结构化约束对象（已从 ID 解析）        
 - List<string> executedActionsSummary - 已执行动作摘要（供 LLM 判断进度）
 - int iterationCount - 当前滚动迭代次数
 - const int MaxIterations = 10 - 防死锁上限
 - bool isRollingMode - 标记当前处于滚动模式，CompleteCurrentAction 据此决定行为

 Step 4：ADMStatus 新增（ExecutionEnums.cs）

 BatchDone, // 当前批次动作全部完成，等待滚动循环处理（非步骤完成）

 Step 5：SharedWhiteboard.cs（新建）

 类设计（全局单例 MonoBehaviour，带详细注释和分区）：

 // ── 数据类型区 ──────────────────────────────
 WhiteboardEntry { agentId, timestamp, constraintId, entryType, status, progress }
 // entryType: "IntentAnnounce" | "ReadySignal" | "DoneSignal" | "StatusUpdate"

 // ── 内部存储区 ──────────────────────────────
 Dictionary<string, List<WhiteboardEntry>> _store  // key = groupId

 // ── 写接口区 ────────────────────────────────
 WriteEntry(groupId, entry)
 // 按 (agentId, constraintId) 唯一键覆盖，last-write-wins，无需锁（每个 agent 只写自己的条目） 

 // ── 读接口区 ────────────────────────────────
 QueryEntries(groupId, constraintId, agentId=null, staleSeconds=10f)
 // 返回 List<WhiteboardEntry>，自动过滤超时条目
 bool HasSignal(groupId, constraintId, agentId, entryType)
 // 快速检查某 agent 是否已写某类型信号

 // ── 清理接口区 ──────────────────────────────
 ClearGroup(groupId)           // 任务结束时清空
 ClearEntry(groupId, agentId, constraintId)  // 单条清除

 读写冲突处理：每个 agent 只写自己 agentId 的条目，不同 agent
 互不覆盖。唯一可能冲突的情况是同一 agent 同一 constraintId 更新 → 覆盖最新值即可（Unity        
 单线程，无需锁）。

 Step 6：RefereeManager.cs（新建）

 类设计（全局单例 MonoBehaviour，带详细注释和分区）：

 // ── 数据类型区 ──────────────────────────────
 RefereeEvent { eventType, constraintId, payload, visibleTo[], timestamp }
 // eventType:
 ResourceClaimed|ResourceReleased|AgentEliminated|ConditionMet|MissionResult|PriorityOverride   

 // ── 注册接口区（Agent 启动时调用）────────────
 RegisterAgent(agentId, Action<RefereeEvent> handler)
 UnregisterAgent(agentId)

 // ── 广播接口区（裁判逻辑调用）───────────────
 BroadcastEvent(RefereeEvent evt)
 // 按 visibleTo 过滤，只投递给可见 agent 的已注册 handler

 // ── 查询接口区（ADM 轮询用）─────────────────
 PollEvents(agentId, constraintId, since=0f)
 // 返回该 agent 可见且与 constraintId 匹配的未处理事件列表
 AcknowledgeEvent(agentId, eventId)
 // 标记已读，避免重复处理

 // ── 清理接口区 ──────────────────────────────
 ClearEvents(olderThanSeconds=60f)

 Step 7：PlanningModule 升级

 新增方法：
 - GetConstraint(constraintId) → StructuredConstraint - ADM 通过 ID 查询完整约束对象
 - GetGroupId() → string - 供白板读写使用

 LLM#1 提示词新增：要求输出 constraints:
 StructuredConstraint[]，包含从任务描述中提取的所有协同约束；格式参考 StructuredConstraint      
 字段说明。

 LLM#4 提示词新增：步骤拆解时为每个 PlanStep 分配 constraintIds[]（从 constraints
 中选择适用于本步骤的约束 ID）。

 OnGroupBootstrap：接收 GroupBootstrapPayload 时，将 payload.constraints 存储到本地字典，供
 GetConstraint 查询。

 广播时：GroupBootstrapPayload 中带上 constraints 数组。

 Step 8：ActionDecisionModule 重构（核心）

 StartStep 改动：
 - 构建 ctx.stepConstraints = 通过 constraintIds 从 PlanningModule 查询完整
 StructuredConstraint 对象
 - 设置 ctx.isRollingMode = true
 - 调用 RunRollingLoop(step) 协程（原 RunLLMA 改名并重构）

 CompleteCurrentAction 改动：
 当 currentActionIdx >= actionQueue.Length：
   if ctx.isRollingMode → SetStatus(BatchDone)，不调用 planningModule.CompleteCurrentStep()     
   else → 原有逻辑（SetStatus(Done), planningModule.CompleteCurrentStep()）

 RunRollingLoop(step) 新协程逻辑：
 SetStatus(Interpreting)
 loop (iterationCount < MaxIterations):
   1. CheckC4Triggers() → 如触发，执行 fallback AtomicAction，结束步骤（强制完成或Failed）      
   2. ReadWhiteboardContext() → 读取本步骤所有 channel=whiteboard 约束的白板条目
   3. CheckC3WaitConditions() → 若 C3 sign=+1 且 watchAgent 未就绪，生成 Wait
 动作并执行，continue
   4. BuildRollingPrompt(step, whiteboard_ctx, referee_ctx, perception, history) → string       
   5. LLM 调用，返回 JSON:
      {"isDone": bool, "doneReason": "...", "nextActions": [...]}
      nextActions 为 1-3 个 AtomicAction，isDone=true 时可为空
   6. 解析 JSON
      if isDone:
        WriteWhiteboardSignals(DoneSignal)   // 写 C2 完成信号
        if C2.syncWith 非空:
          yield WaitForSyncWith(groupId, constraintId, syncWith[], timeout=30s)
        PlanningModule.CompleteCurrentStep()
        SetStatus(Done)
        yield break
      if nextActions 为空:
        log warning，强制设 isDone=true，结束
   7. ctx.actionQueue = nextActions; ctx.currentActionIdx = 0
      WriteWhiteboardSignals(IntentAnnounce) // 宣告意图（可选）
      SetStatus(Running)
      yield return new WaitUntil(() => status == BatchDone || status == Failed)
      if status == Failed: yield break
   8. UpdateHistory(nextActions)  // 追加到 executedActionsSummary
      iterationCount++
      SetStatus(Interpreting)     // 进入下一轮

 // 超出 MaxIterations：强制完成，log warning
 PlanningModule.CompleteCurrentStep()
 SetStatus(Done)

 BuildRollingPrompt 新结构：
 [角色定义] 你是无人机战术执行规划器...

 [步骤目标]
 步骤文本：{step.text}
 完成条件（doneCond）：{step.doneCond}

 [已执行历史]
 {executedActionsSummary}  // 列表，供 LLM 判断进度

 [当前状态]
 位置：{currentLocationName}
 感知快照：{perception}

 [白板状态（组内协同）]
 {whiteboard_entries_for_this_step}

 [裁判事件]
 {relevant_referee_events}

 [协同约束]
 {structured_constraint_summary}  // 结构化格式，包含 cType/channel/reactTo 等

 [输出格式]
 返回 JSON:
 {
   "isDone": true/false,
   "doneReason": "说明为什么完成或未完成",
   "nextActions": [  // isDone=true 时可为空数组；最多3个
     {"actionId":"aa_N", "type":"...", ...}
   ]
 }

 ---
 关键约束处理逻辑

 ┌─────────────┬─────────────────────────────┬─────────────────────────────────────────────┐    
 │  约束类型   │   在滚动循环中的处理位置    │                白板/裁判操作                │    
 ├─────────────┼─────────────────────────────┼─────────────────────────────────────────────┤    
 │ C1          │ BuildRollingPrompt          │ 写 IntentAnnounce 宣告占有意图              │    
 │ Assignment  │ 提供分配信息                │                                             │    
 ├─────────────┼─────────────────────────────┼─────────────────────────────────────────────┤    
 │ C2          │ isDone 判断后               │ 写 DoneSignal；等待 syncWith 全部写入       │    
 │ Completion  │ WaitForSyncWith             │                                             │    
 ├─────────────┼─────────────────────────────┼─────────────────────────────────────────────┤    
 │ C3 sign=+1  │ 每轮开始                    │ 等待 watchAgent 写                          │    
 │             │ CheckC3WaitConditions       │ ReadySignal；自己到位时写 ReadySignal       │    
 ├─────────────┼─────────────────────────────┼─────────────────────────────────────────────┤    
 │ C3          │ BuildRollingPrompt          │ 监听 RefereeManager 裁判广播                │    
 │ sign=0/-1   │ 提供裁判事件                │                                             │    
 ├─────────────┼─────────────────────────────┼─────────────────────────────────────────────┤    
 │ C4 Priority │ 每轮开始 CheckC4Triggers    │ 触发时写 StatusUpdate（预警）；直接生成     │    
 │             │ 最先检查                    │ fallback 动作                               │    
 └─────────────┴─────────────────────────────┴─────────────────────────────────────────────┘    

 ---
 验证方案

 1. 单 Agent 滚动规划：给定一个步骤 text + doneCond，观察 LLM 是否多轮生成动作并最终判断        
 isDone=true，日志应看到多次 [ADM] → Interpreting → Running → BatchDone → Interpreting 循环     
 2. 白板同步：两个 Agent 执行 C3 sign=+1 约束，观察 Agent A 是否等待 Agent B 写 ReadySignal     
 后才继续
 3. C2 同步完成：两个 Agent 都完成后，观察 PlanningModule.CompleteCurrentStep 是否在双方都写    
 DoneSignal 后才触发
 4. 裁判广播：调用 RefereeManager.BroadcastEvent，验证 visibleTo 过滤是否正确，不可见 agent     
 收不到事件
 5. C4 降级：手动降低 agentState.BatteryLevel，观察 ADM 是否在下一轮开始时触发 fallback
 动作而非继续执行原计划
 6. 防死锁：禁止 watchAgent 写 ReadySignal，验证 WaitForSyncWith 超时后能正确退出

 ---
 备注

 - ParsedMission.constraints 由 LLM#1 生成；LLM#1 需要能从任务自然语言提取
 StructuredConstraint，提示词需补充示例和字段说明
 - 白板超时阈值 staleSeconds=10f 可在 Inspector 中暴露为序列化字段
 - RefereeManager 初期由开发者手动调用 BroadcastEvent 测试；后续可接入物理检测逻辑
 - 实现顺序建议：数据结构 → Whiteboard → Referee → PlanningModule → ADM 重构
╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌