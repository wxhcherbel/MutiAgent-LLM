# 多智能体协同仿真系统 — LLM 驱动

基于 Unity3D 的多智能体（无人机 + 无人车）协同仿真框架，使用大语言模型（LLM）进行任务规划、槽位协商与原子动作生成，支持实时 Web 仪表板监控。

---

## 系统架构

```
用户触发 PlanningModule.SubmitMissionRequest(描述, 智能体数)
    │
    ▼
LLM#1: 解析任务 → ParsedMission（分组 / 协作类型）
    │
    ▼
GroupBootstrap 广播 → 所有 Agent 知道自己的组和角色
    │
    ├─ 组长: LLM#2 → 生成 PlanSlot[] → SlotBroadcast
    └─ 成员: LLM#3 → 选槽 → SlotSelect → 组长 ResolveAndConfirm
    │
    ▼ SlotConfirm + StartExecution
    │
LLM#4: 每个 Agent 将 PlanSlot → AgentPlan.steps[]
    │
    ▼ state = Active
    │
每 2 秒: IntelligentAgent.ShouldMakeDecision()
    │
    ▼ ActionDecisionModule.DecideNextAction()
    │
LLM-A: 当前步骤 → AtomicAction[] → Negotiating（0.5s 协商窗口）
    │
    ├─ 无冲突 → Running
    └─ 有冲突 → ADM 选举 → resolver 调用 LLM-B 重规划
    │
每帧: AgentMotionExecutor.Update()
    ├─ MoveTo:  A* 路径规划 → Rigidbody PD 控制
    ├─ Patrol:  绕圆运动
    ├─ Observe: 悬停 + 触发 PerceptionModule.SenseOnce()
    └─ ... 动作完成 → CompleteCurrentAction() → PlanStep 推进
```

---

## 模块说明

### 核心智能体
| 文件 | 说明 |
|------|------|
| `Agents/IntelligentAgent.cs` | 主控制器，驱动决策循环 |
| `Agents/AgentProperties.cs` | 静态属性（ID、类型、角色、感知范围等）|
| `Agents/AgentSpawner.cs` | 场景中批量生成智能体 |
| `Agents/UIController.cs` | Unity UI 绑定 |

### LLM 规划层
| 文件 | 说明 |
|------|------|
| `LLM_Modules/PlanningModule.cs` | 四阶段协商协议（LLM#1-#4）|
| `LLM_Modules/ActionDecisionModule.cs` | 原子动作生成（LLM-A/B）+ 冲突协商 |
| `LLM_Modules/PlanningData.cs` | 数据结构（AtomicAction, AgentPlan, AgentContextUpdate 等）|
| `LLM_API/LLMInterface.cs` | LLM API 请求封装 |
| `LLM_API/LLMJsonUtils.cs` | 共用 JSON 提取工具 |

### 运动执行层
| 文件 | 说明 |
|------|------|
| `Other_Modules/AgentMotionExecutor.cs` | 无人机物理运动执行器（Rigidbody PD 控制）|
| `Maps/AStarPathVisualizer.cs` | A* 路径 LineRenderer 可视化 |

### 感知层
| 文件 | 说明 |
|------|------|
| `Other_Modules/PerceptionModule.cs` | 射线感知 + 敌方检测 + 紧急事件通知 |
| `Other_Modules/IPerceptionVisualizer.cs` | 可视化接口定义 |
| `Other_Modules/PerceptionVisualizer.cs` | 可视化实现（射线 + 球体标记）|

### 通信层
| 文件 | 说明 |
|------|------|
| `Communication_Module/CommunicationManager.cs` | 全局消息路由（直连 / 队伍 / 广播）|
| `Communication_Module/CommunicationModule.cs` | 单 Agent 通信接口 |

### 地图层
| 文件 | 说明 |
|------|------|
| `Maps/CampusGrid2D.cs` | 二维逻辑网格 + A* 寻路 |
| `Maps/CampusJsonMapLoader.cs` | 从 JSON 加载校园地图 |

### Web 仪表板
| 文件 | 说明 |
|------|------|
| `Other_Modules/AgentStateServer.cs` | Unity 内置 HttpListener HTTP 服务 |
| `web/dashboard.html` | 单文件前端仪表板（无外部依赖）|

---

## 快速开始

### Unity 场景配置
1. **地图**：在场景中添加 `CampusJsonMapLoader` + `CampusGrid2D`，配置 JSON 地图文件路径
2. **通信管理**：在场景中添加 `CommunicationManager` 单例对象
3. **LLM 接口**：在场景中添加 `LLMInterface` 并配置 API Key / URL
4. **智能体**：通过 `AgentSpawner` 生成，或手动挂载以下组件到 Prefab：
   - `IntelligentAgent` + `AgentProperties`（配置 AgentID, TeamID, Type, Role）
   - `CommunicationModule`
   - `PerceptionModule` + `PerceptionVisualizer`（可选）
   - `AgentMotionExecutor` + `AStarPathVisualizer`（可选）
   - `ActionDecisionModule`
   - `PlanningModule`
   - `Rigidbody`（useGravity=false）
5. **Web 仪表板**：在场景中添加 `AgentStateServer`（默认端口 8765）

### 触发仿真
```csharp
// 找到任意一个 PlanningModule，提交任务描述
PlanningModule pm = FindObjectOfType<PlanningModule>();
pm.SubmitMissionRequest("对校园东区进行侦察，同时封锁A楼出入口", agentCount: 4);
```

---

## Web 仪表板

Unity 运行时访问 **http://localhost:8765/**

**功能**：
- 2D 地图：实时显示所有 Agent 位置（按队伍着色），点击选中高亮感知范围
- Agent 卡片：ID / 类型 / 角色 / 电量 / ADM状态 / 当前动作 / 步骤 / 敌方列表
- 通信日志：最新 200 条消息，支持按消息类型过滤
- 轮询间隔：0.5s / 1s / 2s 可选

**REST API**：
| 路径 | 说明 |
|------|------|
| `GET /` | 仪表板 HTML |
| `GET /api/state` | 所有 Agent 快照 JSON |
| `GET /api/map` | 地图元数据 JSON |
| `GET /api/messages` | 最近 200 条通信日志 JSON |

---

## 已知限制与待优化

- **LLMJsonUtils 迁移**：ADM 和 PlanningModule 内仍保留旧的 `ExtractJson` 私有方法，可在下一阶段统一改用 `LLMJsonUtils.ExtractJson`
- **A* 高度**：目前路径点 Y 坐标固定为 `hoverHeight`，复杂三维地形需扩展
- **FormationHold 偏移**：偏移量硬编码为 `(3, 0, 0)`，应由 LLM-A 的 `AtomicAction` 参数传入
- **AgentStateServer 地图特征点**：目前 features 列表为空，需接入 `CampusGrid2D.featureAliasNameMap`
- **NavMesh 支持**：当前仅支持 Rigidbody 物理控制，地面无人车可考虑接入 NavMesh
