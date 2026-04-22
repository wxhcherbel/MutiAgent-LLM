❯ 审阅当前所有代码，理解主流程，如果加上自主涌现应该怎么做？可以参考agent_architecture_report.html结合审阅和推理。如何才能让当前由人工发布任  
务变成agent自主涌现任务呢？就类似于斯坦福小镇，但是不能照搬斯坦福小镇，因为我们是多智能体无人机，和小镇还是有很多区别的，要更加考虑真实工程意 
义。                                                                                                                                          
                                                                                                                                              
● 2 Explore agents finished (ctrl+o to expand)                                                                                                
   ├─ Explore main codebase structure and flow · 32 tool uses · 81.8k tokens
   │  ⎿  Done                                                                                                                                 
   └─ Find and read architecture report HTML · 38 tool uses · 65.5k tokens
      ⎿  Done                                                                                                                                 
                                                                                                                                              
● Now I have a comprehensive understanding of the system. Let me write the plan.                                                              
                                                                                                                                              
● Updated plan                                                                                                                                
  ⎿  /plan to preview                                                                                                                         
                                                                                                                                              
──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
 Ready to code?                                                                                                                               
                                                                                                                                              
 Here is Claude's plan:                                                                                                                       
╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌
 Plan: 多智能体无人机自主涌现任务系统                                                                                                         
                                                                                                                                              
 Context                                                                                                                                      
                                                                                                                                              
 当前系统所有任务由人工通过 PlanningModule.SubmitMissionRequest() 注入。智能体拥有
 LLM规划、感知、记忆、反思、MAD辩论等完整组件，但这些组件的驱动力来源于外部指令，而非智能体内部的自主意图。

 目标：让无人机从"被动执行命令"变为"主动产生任务"——基于自身状态（电量、位置）、环境感知（未探索区域、威胁）和团队记忆，自主涌现出有工程意义的
 任务目标，再通过已有的 LLM 四阶段规划流水线协同执行。

 与斯坦福小镇的本质区别：
 - 斯坦福小镇：社交驱动（交流、睡觉、吃饭），每步间隔5分钟
 - 本系统：任务驱动（覆盖侦察、威胁响应、资源回收、队形协同），物理约束（电量、通信范围、避障）
 - 斯坦福小镇：单智能体自主即可呈现涌现；本系统：多机协同才产生工程价值

 ---
 架构分析：当前主流程

 人工输入
   → PlanningModule.SubmitMissionRequest()
   → LLM#1 解析任务 → LLM#2 设计槽位 → LLM#3 分配角色 → LLM#4 分解步骤
   → ActionDecisionModule 滚动执行
   → 感知/记忆/MAD辩论（被动响应，不主动驱动）

 核心缺口：没有任何模块能在没有外部输入时主动发起 SubmitMissionRequest()。

 ---
 设计方案：自主涌现驱动层

 核心思路：Drive → Appraisal → Goal → Plan

 在现有架构之上，增加一层"自主驱动-评估-目标生成"，让它成为 SubmitMissionRequest() 的新调用者。

 [新增] AutonomousDriveSystem
          ↓ 每隔T秒评估
     DriveAppraisalEngine
          ↓ 产生 GoalProposal
     TeamGoalNegotiator  ← 广播给队友，防重复
          ↓ 通过后
     PlanningModule.SubmitMissionRequest()  ← 原有流水线完全复用
          ↓
     LLM#1-4 + ActionDecisionModule

 ---
 新增模块详细设计

 1. DriveSystem（内驱力）

 每个无人机维护以下内驱力状态，代替斯坦福小镇的"需求值"：

 // 新文件: Autonomous/DriveSystem.cs
 public class AgentDriveState {
     public float BatteryUrgency;      // 电量越低越高（触发返航充电）
     public float ExplorationCuriosity; // 地图未覆盖区域越多越高（触发侦察任务）
     public float ThreatAlertness;     // 感知到威胁信号越强越高（触发拦截/跟踪）
     public float ResourceAwareness;   // 记忆中有未回收资源越多越高（触发采集）
     public float TeamSynergy;         // 队友长期单独行动越高（触发编队任务）
 }

 驱动力的计算来源（全部复用已有模块）：
 - BatteryUrgency: AgentMotionExecutor 中的电量属性
 - ExplorationCuriosity: SharedWhiteboard 中尚未写 DoneSignal 的区域 + 记忆中 WorldState 类型条目
 - ThreatAlertness: PerceptionModule.detectedObjects 中敌方/异常实体数量
 - ResourceAwareness: MemoryModule 中 tag 含 "resource" 且状态为 "unresolved" 的条目
 - TeamSynergy: CommunicationModule 最近收到的队友消息频率（低→需要协同）

 ---
 2. DriveAppraisalEngine（驱动评估引擎）

 文件：Autonomous/DriveAppraisalEngine.cs

 定期（默认 30 秒，可配置）运行，选出当前最高优先级驱动并生成目标提案：

 // 伪代码
 GoalProposal Appraise(AgentDriveState drives, MemoryModule memory) {
     // Step 1: 选出最强驱动
     var topDrive = SelectTopDrive(drives);

     // Step 2: 查询记忆中相关的历史经验（复用 MemoryModule 3D检索）
     var relevantMemories = memory.Query(topDrive.ToQueryContext(), topK: 5);

     // Step 3: 用一次 LLM 调用生成自然语言目标
     // (轻量级调用，非四阶段流水线)
     string goalNL = await LLMInterface.Call(BuildGoalGenPrompt(
         driveName: topDrive.Name,
         currentLocation: agentPos,
         relevantMemories: relevantMemories,
         teamWhiteboardState: whiteboard.ReadAll()
     ));

     return new GoalProposal {
         originAgentId = this.agentId,
         goalText = goalNL,
         driveSource = topDrive,
         urgency = topDrive.strength,
         suggestedAgentCount = EstimateNeededAgents(topDrive)
     };
 }

 LLM提示词策略（关键设计）：
 - 不直接生成执行计划（那是 LLM#1-4 的职责）
 - 只生成"自然语言任务描述" + "建议参与人数"
 - 例如输出："侦察东北区域未探索的3个节点，优先发现威胁目标" + agentCount=2

 ---
 3. TeamGoalNegotiator（团队目标协商）

 文件：Autonomous/TeamGoalNegotiator.cs

 防止多机同时产生重复目标，同时允许目标合并：

 Agent A 产生提案 → 广播 GoalProposal (via CommunicationModule.BroadcastMessage)
                      ↓ 队友收到
          队友检查自身是否正在执行类似目标（查 SharedWhiteboard）
                      ↓
          如无冲突 → 投票支持（投票消息通过 CommunicationModule）
          如有冲突 → 提出修改（目标范围缩小/时间错开）
                      ↓
          提案发起者收集投票
          → 支持>50% → 调用 PlanningModule.SubmitMissionRequest()
          → 被修改   → 更新提案后重新广播（最多2轮）
          → 被否决   → 本轮不发起，等下次驱动评估

 协商消息类型（扩展已有 AgentMessage 体系）：
 - GoalProposal: 目标提案广播
 - GoalVote: 支持/反对票 + 原因
 - GoalAmend: 修改建议

 ---
 4. AutonomousScheduler（调度器，总入口）

 文件：Autonomous/AutonomousScheduler.cs

 挂在组长智能体（Group Leader）上，统筹驱动评估周期：

 IEnumerator ScheduleLoop() {
     while (true) {
         yield return new WaitForSeconds(evaluationInterval); // 默认30s

         // 跳过：当前有活跃任务 (避免规划冲突)
         if (planningModule.HasActiveMission()) continue;

         // 跳过：电量危急，优先处理电量驱动
         if (driveSystem.BatteryUrgency > 0.9f) {
             ImmediatelyTriggerChargeTask(); // 不走协商，直接发起
             continue;
         }

         // 评估驱动，生成提案
         var proposal = await driveAppraisalEngine.Appraise();
         if (proposal == null) continue;

         // 协商
         bool accepted = await teamGoalNegotiator.Negotiate(proposal);
         if (accepted) {
             planningModule.SubmitMissionRequest(proposal.goalText, proposal.suggestedAgentCount);
         }
     }
 }

 ---
 任务涌现场景举例（工程意义体现）

 ┌──────────────────────┬───────────────────────┬──────────────────────────────────────────┬──────────────────┐
 │         驱动          │       触发条件        │             涌现出的任务示例               │     协同方式     │
 ├──────────────────────┼───────────────────────┼──────────────────────────────────────────┼──────────────────┤
 │ ExplorationCuriosity │ 地图30%未覆盖          │ "两机编队侦察西南象限，标记新发现的障碍"     │ MAD 分配东西两路 │
 ├──────────────────────┼───────────────────────┼──────────────────────────────────────────┼──────────────────┤
 │ ThreatAlertness      │ 感知到不明移动目标      │ "一机跟踪目标，一机截断退路"                 │ C3耦合约束       │
 ├──────────────────────┼───────────────────────┼──────────────────────────────────────────┼──────────────────┤
 │ BatteryUrgency       │ 电量<25%              │ "立即返回充电站，请求队友接管侦察"           │ 广播状态交接     │
 ├──────────────────────┼───────────────────────┼──────────────────────────────────────────┼──────────────────┤
 │ ResourceAwareness    │ 记忆中3个未采集资源点   │ "依据记忆中的位置，高效采集路径规划"         │ C1资源分配       │
 ├──────────────────────┼───────────────────────┼──────────────────────────────────────────┼──────────────────┤
 │ TeamSynergy          │ 队友已单独行动>5分钟    │ "汇聚至中央节点，更新各自侦察结果"          │ C2同步信号       │
 └──────────────────────┴───────────────────────┴──────────────────────────────────────────┴──────────────────┘

 ---
 与斯坦福小镇的对比设计选择

 ┌──────────┬──────────────────┬──────────────────────────────────┐
 │   维度   │    斯坦福小镇    │         本系统（无人机）         │
 ├──────────┼──────────────────┼──────────────────────────────────┤
 │ 驱动来源 │ 社交需求、日程表 │ 任务完成度、电量、威胁感知       │
 ├──────────┼──────────────────┼──────────────────────────────────┤
 │ 自主周期 │ 每5分钟重规划    │ 驱动超阈值触发（非定时）         │
 ├──────────┼──────────────────┼──────────────────────────────────┤
 │ 社交互动 │ 对话产生涌现     │ 协商广播防止重复 + 组合协同      │
 ├──────────┼──────────────────┼──────────────────────────────────┤
 │ 记忆利用 │ 角色日记/反思    │ 3D检索，直接驱动目标生成         │
 ├──────────┼──────────────────┼──────────────────────────────────┤
 │ 任务粒度 │ 单人行动         │ 多机协同任务（工程价值核心）     │
 ├──────────┼──────────────────┼──────────────────────────────────┤
 │ 评估方式 │ 对话内容丰富度   │ 地图覆盖率、任务完成率、冲突次数 │
 └──────────┴──────────────────┴──────────────────────────────────┘

 ---
 关键文件修改清单

 新增文件

 - Autonomous/DriveSystem.cs — 内驱力状态计算（依赖 PerceptionModule, MemoryModule）
 - Autonomous/DriveAppraisalEngine.cs — 驱动评估，调用一次轻量 LLM 生成目标文本
 - Autonomous/TeamGoalNegotiator.cs — 基于 CommunicationModule 的目标协商
 - Autonomous/AutonomousScheduler.cs — 调度入口，挂载在 Group Leader 上

 修改已有文件

 - Communication_Module/Data/AgentMessage.cs — 增加 GoalProposal, GoalVote, GoalAmend 消息类型
 - Enums/CommunicationEnums.cs — 增加对应 MessageType 枚举值
 - Agents/IntelligentAgent.cs — Group Leader 初始化时启动 AutonomousScheduler
 - LLM_Modules/ActionDecisionModule.cs — 感知观察事件写入 MemoryModule（修复感知-记忆断层）

 复用（零修改）

 - LLM_Modules/PlanningModule.cs — 完整复用，AutonomousScheduler 作为新的调用者
 - LLM_Modules/MemoryModule.cs — 复用 3D 检索接口
 - MAD_Module/ — 全部复用，协商冲突仍走 MAD
 - Communication_Module/CommunicationModule.cs — 复用消息收发
 - LLM_API/LLMInterface.cs — 复用 LLM 调用接口

 ---
 实施顺序（最小可验证路径）

 第一步（最快见效）：DriveSystem + AutonomousScheduler 骨架
 - 先用硬编码规则（不调 LLM）驱动目标涌现
 - 验证：无人机在电量低时能自主发起充电任务，而无需人工输入

 第二步：DriveAppraisalEngine 接入 LLM
 - 用 LLM 生成自然语言目标描述，替换硬编码字符串
 - 验证：目标描述多样化，且与感知/记忆上下文相关

 第三步：TeamGoalNegotiator 实现协商
 - 验证：两机不会同时对同一区域发起完全相同任务

 第四步：修复感知-记忆断层（ActionDecisionModule 写入感知记忆）
 - 验证：ResourceAwareness 驱动能正确读到之前感知到的资源点

 ---
 验证方法

 1. 无人工输入场景测试：启动仿真后不调用任何 SubmitMissionRequest()，观察智能体是否在30-60秒内自主发起第一个任务
 2. 驱动覆盖测试：手动设置电量为20%，验证 BatteryUrgency 触发充电任务
 3. 防重复测试：禁用协商模块，验证多机同时涌现重复目标；启用后验证去重
 4. 长时运行测试：运行10分钟不干预，记录自主涌现的任务类型分布和地图覆盖增长曲线
 5. MAD联动测试：制造两机都想去同一地点的场景，验证 MAD 仍正确仲裁
