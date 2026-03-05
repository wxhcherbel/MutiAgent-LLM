阶段1：任务分析与分配（协调者）
用户输入 → SubmitMissionRequest(任务描述, 智能体数量)

LLM分析任务 → AnalyzeMissionDescription()

构建提示词发送给LLM

LLM返回结构化任务分析

解析任务结构 → ParseMissionFromLLM()

提取任务类型、通信模式、角色分配

生成MissionAssignment对象

协调者设置 → 第一个接收任务的智能体自动成为协调者

任务分发 → DistributeMissionToAgents()

协调者向所有智能体广播任务

根据角色需求发送相应数量的任务消息

阶段2：角色选择与计划制定（所有智能体）
接收任务 → ReceiveMissionAssignment()

智能体接收广播的任务分配

设置通信模式

LLM分析角色 → AnalyzeMissionAndCreatePlan()

基于自身能力和任务需求分析最适合的角色

LLM生成具体执行步骤

创建执行计划 → ParseAndCreatePlan()

解析LLM返回的角色和步骤

创建Plan对象并设置为当前计划

确认角色 → SendRoleAcceptance()

向协调者发送角色接受确认

记录到记忆模块

阶段3：任务执行与协调
执行任务步骤 → GetCurrentTask() / CompleteCurrentTask()

按步骤执行计划

定期报告进度

任务协调 → 根据通信模式进行协作

中心化：通过协调者协调

去中心化：智能体间直接通信

混合模式：结合两者优势

任务完成 → ReportMissionCompletion()

向协调者报告任务完成

更新记忆和状态