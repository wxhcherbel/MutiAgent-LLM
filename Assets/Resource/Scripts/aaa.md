  实现差异分析                                                                                                               
                                                                                                                             
  已完整实现 ✅                                                                                                              
                                                                                                                             
  ┌─────────────────────────────────────────────────┬──────┐                                                                 
  │                      模块                       │ 状态 │                                                                 
  ├─────────────────────────────────────────────────┼──────┤
  │ PlanningModule（LLM#1/2/3/4 + 状态机）          │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ ActionDecisionModule（LLM#5 滚动循环）          │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ AgentMotionExecutor（物理控制 + A*）            │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ SharedWhiteboard（C1/C2/C3 约束协调）           │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ CommunicationModule/Manager（多作用域消息路由） │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ GroupMonitor + IncidentCoordinator（MAD辩论）   │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ DebateParticipant（个体辩论参与）               │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ LLMInterface（DeepSeek API，含重试/日志）       │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ AgentStateServer（HTTP监控 Dashboard）          │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ PerceptionModule（0.5s 感知循环）               │ 完整 │
  ├─────────────────────────────────────────────────┼──────┤
  │ CampusGrid2D + A*（空间导航）                   │ 完整 │
  └─────────────────────────────────────────────────┴──────┘

  ---
  差距详情

  🔴 P0 — 阻断自治的关键缺失

  1. 记忆检索评分公式（MemoryModule）
  - 现状：RecallMemories() 仅做 BagOfWords 关键词过滤，无权重排序
  - 目标：score = recency_weight × e^(-λt) + importance_weight × (score/10) + relevance_weight × similarity
  - 缺口：无衰减公式、无重要性加权、无语义相似度；DeepSeek /embeddings 接口未接入
  - 影响：ADM 和 Planning 注入的记忆上下文质量差，决策无法利用历史经验

  2. 感知结果未写入记忆流（PerceptionModule）
  - 现状：detectedObjects 存在 PerceptionModule 本地列表，不流向 MemoryModule
  - 目标：每次感知周期后自动调用 memoryModule.StoreMemory() 写入 Observation 类型记忆
  - 影响：智能体对环境的观察无法积累，记忆系统缺乏原料

  3. 自主目标生成器（AutonomousGoalGenerator — 未创建）
  - 现状：所有任务通过 SubmitMissionRequest() 人工注入，IntelligentAgent 的 HasActiveMission() 完全被动轮询
  - 目标：新模块读取性格 + 历史记忆 + 社会关系，调用 LLM 生成目标，通过 PlanningModule.SubmitAutonomousGoal() 提交
  - 缺口：模块本身不存在；PlanningModule 无 SubmitAutonomousGoal 接口；IntelligentAgent 无自主触发逻辑

  ---
  🟠 P1 — 制约涌现的重要缺失

  4. LLM#4 固定步骤生成（建议移除）
  - 现状：PlanningModule 有完整 StepGen 阶段（LLM#4），预生成固定 PlanStep[] 列表
  - 目标：移除 LLM#4，ADM（LLM#5）每决策周期基于当前感知动态生成下一动作，无需预定步骤
  - 当前影响：环境变化时预生成步骤过时；ADM 已是滚动循环，步骤只是上层约束
  - 注意：这是较大重构，影响 PlanningModule 状态机和 ADM 输入结构

  5. 人格系统（PersonalitySystem — 未创建）
  - 现状：AgentProperties 仅有 role, type, teamId 等字段，无性格建模
  - 目标：Big Five 特征 + 核心价值观 + 习惯，ToNarrativeDescription() 注入 LLM 提示词
  - 缺口：无 PersonalitySystem.cs；LLM#1/5 提示词未注入性格上下文

  6. 社会关系图谱（SocialRelationshipGraph — 未创建）
  - 现状：MemoryModule 有 Relationship 记忆类型，但无结构化图谱；CommunicationModule 收发消息后不更新任何社会状态
  - 目标：记录 familiarity/trust/sentiment，影响 LLM#3 槽位选择偏好和 MAD 辩论投票权重
  - 缺口：无 SocialRelationshipGraph.cs；CommunicationModule 无社会图谱更新钩子

  7. 世界语义地图（WorldSemanticMap — 未创建）
  - 现状：CampusGrid2D 有物理网格 + POI；MapTopologySerializer 产出文字描述，但无语义层次结构
  - 目标：世界 → 区域 → 对象的三层语义树，支持自然语言解析（"找一个空闲充电站" → 坐标）
  - 缺口：无语义本体层；感知结果无法以语义形式更新地图状态

  ---
  🟡 P2 — 现有代码待启用/完善

  8. ReflectionModule L3 默认禁用
  - 现状：L3 代码已存在，但 enableL3AbstractReflection = false
  - 目标：任务完成后由 GroupMonitor 显式触发，提取跨日模式洞察
  - 缺口：仅需修改触发策略，代码基本就绪

  9. SkillLibrary（未创建）
  - 现状：成功模式无任何积累机制
  - 目标：ReflectionModule 检测成功模式 → LLM 提取模板 → SkillLibrary 存储 → ADM 复用
  - 缺口：SkillLibrary.cs 不存在；ReflectionModule 无成功模式提取路径

  ---
  差距总览

  已实现        规划中         缺口
  ────────────  ─────────────  ─────────────────────────────────
  LLM#1/2/3/4  LLM#4 移除     MemoryModule 3D检索评分
  LLM#5 滚动   ADM 纯响应式   感知→记忆 自动写入
  MAD辩论      社会图谱权重   AutonomousGoalGenerator（整个模块）
  SharedWB     关系影响槽位   PersonalitySystem（整个模块）
  MemoryModule 3D检索公式     SocialRelationshipGraph（整个模块）
  Reflection   L3 实际启用    WorldSemanticMap（整个模块）
  Perception   感知→记忆流    SkillLibrary（整个模块）

  ---
  下一步详细计划

  按影响大小和依赖顺序排列：

  Phase 1：打通记忆基础设施（约 1 周）

  步骤 1.1 — MemoryModule 加入 3D 检索评分
  - 修改 RecallMemories(query) 方法
  - 新增 ComputeRetrievalScore(memory, query) ：recency（指数衰减，λ=1/72h）× importance（0-10归一化）×
  relevance（BagOfWords暂代，后期换embedding）
  - 结果按分数排序，取 top-K 返回
  - 文件：LLM_Modules/MemoryModule.cs

  步骤 1.2 — PerceptionModule 自动写入记忆
  - 在 SenseOnce() 完成后，遍历 detectedObjects，对重要观察（敌方目标、资源、障碍变化）调用
  memoryModule.StoreMemory()，类型为 Observation
  - importance 规则：enemy=8，resource=5，obstacle=4，friend=2
  - 文件：Perception/PerceptionModule.cs

  步骤 1.3 — 启用 ReflectionModule L3
  - 将 enableL3AbstractReflection = true
  - 确认 GroupMonitor 在任务完成时已调用 TriggerL3Reflection()（代码路径已存在，仅需确认）
  - 文件：LLM_Modules/ReflectionModule.cs

  ---
  Phase 2：人格系统（约 0.5 周）

  步骤 2.1 — 新建 PersonalitySystem.cs
  - 定义 AgentPersonality（Big Five + coreValues[] + habits[]）
  - 实现 ToNarrativeDescription() 输出中文人格描述
  - 文件：新建 LLM_Modules/PersonalitySystem.cs

  步骤 2.2 — AgentProperties 引用人格
  - AgentProperties 新增 personality: AgentPersonality 字段
  - 文件：Agents/Data/AgentStateContracts.cs

  步骤 2.3 — 人格注入 LLM#1 和 LLM#5 提示词
  - PlanningModule LLM#1 prompt：追加 personality.ToNarrativeDescription()
  - ADM LLM#5 prompt：追加人格描述（影响动作选择倾向）
  - 文件：LLM_Modules/PlanningModule.cs，LLM_Modules/ActionDecisionModule.cs

  ---
  Phase 3：自主目标生成（约 1 周）

  步骤 3.1 — 新建 AutonomousGoalGenerator.cs
  - GenerateGoal(personality, memories, currentState) → 调用 LLM，返回自然语言任务描述
  - prompt 包含：性格档案、top-5 历史记忆、当前位置/电量
  - 文件：新建 LLM_Modules/AutonomousGoalGenerator.cs

  步骤 3.2 — PlanningModule 新增接口
  - SubmitAutonomousGoal(string goalDescription) — 内部直接调用 SubmitMissionRequest()，与人工注入走同一流程
  - 文件：LLM_Modules/PlanningModule.cs

  步骤 3.3 — IntelligentAgent 触发逻辑
  - CheckForDecision() 中：若无 active mission 且 AutonomousGoalGenerator 就绪，调用 GenerateGoal() 后提交
  - 节流：任务间最短间隔 60s，防止频繁触发
  - 文件：Agents/IntelligentAgent.cs

  ---
  Phase 4：社会关系图谱（约 0.5 周）

  步骤 4.1 — 新建 SocialRelationshipGraph.cs
  - AgentRelationship { familiarity, trust, sentiment, lastInteraction }
  - UpdateRelationship(agentA, agentB, delta)
  - GetInfluenceWeight(agentA, agentB) → 供 MAD 辩论投票用
  - 文件：新建 LLM_Modules/SocialRelationshipGraph.cs

  步骤 4.2 — CommunicationModule 更新社会图谱
  - 每次 ReceiveMessage() 后调用 socialGraph.UpdateRelationship()
  - familiarity +0.01/次，协作成功 trust +0.05，失败 -0.02
  - 文件：Communication_Module/CommunicationModule.cs

  步骤 4.3 — LLM#3 槽位选择注入关系权重
  - prompt 追加：与各槽位关联智能体的 trust/familiarity 分数
  - 文件：LLM_Modules/PlanningModule.cs

  ---
  Phase 5：移除 LLM#4（可选，较大重构）

  这是最大的架构变更，建议在 Phase 1-4 稳定后再执行：
  - 删除 PlanningModule 中 StepGen 状态和 RunLLM4() 方法
  - PlanningState 从 SlotPick → StepGen → Active 改为 SlotPick → Active
  - ADM StartStep(step) 接口改为 StartMission(slot)，每个决策周期自主生成下一动作
  - LLM#5 prompt 需扩充：加入角色使命说明和完成条件（原来在 step 里）
  - 文件：LLM_Modules/PlanningModule.cs，LLM_Modules/ActionDecisionModule.cs

  ---
  建议执行顺序：1.1 → 1.2 → 1.3 → 2.x → 3.x → 4.x → 5（可选）

  Phase 1 收益最高（记忆系统决定所有 LLM 调用的上下文质量），且改动集中在现有文件，风险最低，建议优先启动。