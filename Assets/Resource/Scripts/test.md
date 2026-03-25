# `RunLLMA` 改为滚动决策的最小正确方案

## Summary
把 [`RunLLMA`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs) 改成“当前 `PlanStep` 内分批生成动作”，但不新增数据结构，不给 [`ActionExecutionContext`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/Data/Planning/ActionContracts.cs) 加字段。  
核心改法是：

- `ctx.actionQueue` 不再只存“当前批”，而是存“当前 step 已生成过的累计动作序列”
- `ctx.currentActionIdx` 继续作为“下一个待执行动作下标”
- 每次已生成动作都执行完后，再向 LLM 请求下一小批 `1~2` 个动作并追加到 `actionQueue`
- `PlanStep` 是否完成，不靠 `[]` 直接定，而是先走本地完成条件判断；`[]` 只能表示“LLM 没有新的动作建议”

## Key Changes
### 1. `RunLLMA(step)` 改成“循环补批”而不是“一次出完”
在 [`ActionDecisionModule.cs`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs) 中，把 `RunLLMA` 改成围绕当前 `step.stepId` 的循环协程：

- 初始化时清空 `ctx.actionQueue`，`ctx.currentActionIdx = 0`
- 进入循环后，先判断当前是否还有未执行动作
- 如果 `currentActionIdx < actionQueue.Length`，说明还有动作没执行完，协程只等待，不再请求 LLM
- 如果 `currentActionIdx >= actionQueue.Length`，说明当前累计动作都执行完了，这时先做“步骤完成判定”
- 只有判定“还没完成”时，才再向 LLM 请求下一批动作
- 新批次动作解析成功后，追加到 `ctx.actionQueue` 末尾，而不是覆盖原数组

这样已执行动作天然保留在 `ctx.actionQueue[0 .. currentActionIdx-1]`，不需要再建新历史结构。

### 2. 已执行动作摘要直接从现有 `ctx` 计算
不新增“已执行动作列表”字段，也不在协程里额外维护本地历史集合。  
直接基于现有字段生成 prompt 用摘要：

- 已执行动作：`ctx.actionQueue[0 .. currentActionIdx-1]`
- 待执行动作：`ctx.actionQueue[currentActionIdx .. end]`

可以保留现有 [`BuildRemainingActionSummary`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs) 思路，再补一个对称的“已执行动作摘要”辅助方法，或者直接在同一个方法里按区间生成，不增加任何状态字段。

### 3. `CompleteCurrentAction()` 只推进索引，不再直接结束 step
调整 [`CompleteCurrentAction`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs) 的职责：

- 当前动作完成后：`ctx.currentActionIdx++`
- 如果后面还有累计动作，继续执行下一条
- 如果累计动作已耗尽，不在这里直接 `planningModule.CompleteCurrentStep()`
- 让 `RunLLMA` 协程在下一轮统一判断：
- 当前 `PlanStep` 是否已经满足完成条件
- 若未满足，再向 LLM 续要下一批动作

这样“动作批次结束”和“步骤完成”被拆开，语义更稳。

### 4. 步骤完成必须走本地判定，`[]` 只当参考，不当最终依据
新增一个最小本地判定方法，例如 `IsStepCompleted(PlanStep step)`，只用现有数据做保守判断：

- 输入来源：`step.text`、`step.doneCond`、`ctx.actionQueue`、`ctx.currentActionIdx`、`agentState`、`ResolveCurrentLocationName()`
- 判定时机：仅在“当前累计动作已全部执行完”时触发
- 判定规则优先级：
- 先看 `step.doneCond`
- 再结合 `step.text` 的动作语义
- 再结合最后一个已执行动作的类型和当前状态

最小可落地规则建议：

- `MoveTo` 类步骤：最后一个已执行动作是 `MoveTo`，且当前位置已到目标地点
- `PatrolAround` 类步骤：最后一个已执行动作是 `PatrolAround`，且该动作协程已自然结束
- `Observe` 类步骤：最后一个已执行动作是 `Observe`，且观察动作已自然结束
- `Wait` 类步骤：最后一个已执行动作是 `Wait`，且等待动作已自然结束
- `FormationHold`/“维持中继”这类持续性步骤：若 `doneCond` 明确要求“任务结束”或外部条件，此轮不自动判完成，继续请求 LLM 给出下一批维持/等待动作，除非后续设计里另有明确终止条件

`[]` 的处理改成：

- 若 LLM 返回 `[]`，只表示“本轮没有新动作”
- 随后立刻调用本地 `IsStepCompleted(step)`
- 判定完成才 `planningModule.CompleteCurrentStep()`
- 判定未完成则视为“LLM 未给出有效续行动作”，进入重试或失败分支，不能直接过 step

### 5. Prompt 改成“续写下一小批”，并明确受完成条件约束
修改 [`BuildLLMAPrompt`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/ActionDecisionModule.cs) 的语义，不改现有主要入参形态：

- 明确告诉 LLM：只输出“当前接下来 1~2 个动作”
- 明确告诉 LLM：不得重复“已执行动作摘要”里的动作
- 明确告诉 LLM：必须围绕 `step.text` 与 `step.doneCond` 推进
- 明确告诉 LLM：如果它判断当前不需要新增动作，可以返回 `[]`，但系统会再按完成条件自行校验

这样即使 LLM 返回了奇怪内容，系统也不会直接把 step 标成完成。

### 6. 失败与重试复用现有 `replanCount`
不加新的计数字段，直接复用现有：

- LLM 返回空文本 / 非法 JSON / `[]` 且本地完成条件不满足，`replanCount++`
- 超过 `MaxReplanCount` 后，进入 `Failed`
- 每次重试都打印清晰日志：当前 step、累计动作数、已执行数、最近一次完成条件判定结果

## Public APIs / Types
不新增 public 接口。  
不新增或修改以下类型字段：

- [`ActionExecutionContext`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/Data/Planning/ActionContracts.cs)
- [`AtomicAction`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/Data/Planning/ActionContracts.cs)
- [`PlanStep`](e:/基于Unity3D的多智能体控制与协同仿真系统/UnityLLM/LLM/Assets/Resource/Scripts/LLM_Modules/Data/Planning/PlanContracts.cs)

唯一协议层变化是：LLM-A 允许返回 `[]`，但 `[]` 不再等于“步骤完成”，只是“未提供新动作”。

## Test Plan
- 一个原本会生成多条动作的 step，应变成多轮生成，每轮只追加 `1~2` 条动作到 `ctx.actionQueue`
- `ctx.actionQueue` 应保留整个当前 step 的累计动作，`currentActionIdx` 单调递增
- `CompleteCurrentAction()` 在耗尽当前累计动作后，不能直接推进 `PlanningModule` 的 step
- LLM 返回正常新动作时，应成功追加并继续执行，不重复之前已执行动作
- LLM 返回 `[]` 且本地完成条件满足时，才允许 `planningModule.CompleteCurrentStep()`
- LLM 返回 `[]` 但本地完成条件不满足时，不能过 step，应进入重试
- 非法 JSON、空回复、多次无有效续行动作后，应进入 `Failed`
- 监控侧读取 `ctx.actionQueue` 和 `currentActionIdx` 时，能看到当前 step 的累计执行进度

## Assumptions
- 本次只改 `ActionDecisionModule` 的步内滚动决策，不顺带扩展 `PerceptionModule` 的事件重规划逻辑。
- `PlanStep.doneCond` 仍是自然语言，因此本地完成判定采取“保守规则 + 动作执行结果”的最小实现，不把 LLM 返回内容直接当完成事实。
- 持续型步骤如果缺少可程序化终止条件，本轮方案默认不因为 `[]` 自动完成，而是继续滚动请求或在重试上限后失败，避免误判完成。
