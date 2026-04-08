
╭───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╮
│ Plan to implement                                                                                                     │
│                                                                                                                       │
│ 计划：CampusGrid2D.cs 注释与结构重组                                                                                  │
│                                                                                                                       │
│ Context                                                                                                               │
│                                                                                                                       │
│ CampusGrid2D.cs 共 3097 行，包含约 100 个方法和大量字段，但目前没有任何 #region 分区、XML                             │
│ 文档注释或字段说明注释，外部调用的公开 API 与内部实现方法混杂排列，难以阅读和维护。                                   │
│ 目标：在不改变任何逻辑的前提下，为所有字段/公开方法添加注释，并用 #region 将方法按功能分组排列，公开 API              │
│ 在前，内部实现在后。                                                                                                  │
│                                                                                                                       │
│ ---                                                                                                                   │
│ 方案：整文件重写（Write 工具）                                                                                        │
│                                                                                                                       │
│ 由于涉及 ~100 个方法的跨区间搬移，逐段 Edit 极易出错，采用"读全文 → 重组 → Write 输出"方式。                          │
│                                                                                                                       │
│ 执行步骤                                                                                                              │
│                                                                                                                       │
│ 1. 分块读取完整文件（每次 ≤500 行，共约 7 次）                                                                        │
│ 2. 按下方分区顺序重组所有方法，原有逻辑体一字不改                                                                     │
│ 3. 为每个公开方法加 /// <summary> XML doc，为每个字段加行尾 // 注释                                                   │
│ 4. 用 #region / #endregion 包裹每个分区                                                                               │
│ 5. Write 输出完整新文件                                                                                               │
│                                                                                                                       │
│ ---                                                                                                                   │
│ 分区结构（顺序）                                                                                                      │
│                                                                                                                       │
│ 字段区（保持原有 [Header] 顺序，每行加 // 尾注）                                                                      │
│                                                                                                                       │
│ [Header("数据源")]          // campusLoader                                                                           │
│ [Header("网格参数")]        // cellSize, mapMargin, autoBuildOnStart, allowDiagonal, smoothAStarPath                  │
│ [Header("通行规则")]        // buildingBlocked … otherBlocked                                                         │
│ [Header("可视化")]          // showGrid … gridMaterial, freeColor … buildingColor                                     │
│ [Header("调试")]            // logBuildSummary                                                                        │
│ [Header("点击查询（运行时）")] // enableClickQuery … showClickQueryOnScreen, clickInfoDuration                        │
│ // ── 运行时公共数据（NonSerialized）──                                                                               │
│ gridWidth, gridLength, blockedGrid, cellTypeGrid, cellFeatureUidGrid,                                                 │
│ cellFeatureNameGrid, featureAliasCellMap, featureAliasUidMap, featureAliasNameMap,                                    │
│ mapBoundsXY, featureSpatialProfileByUid, featureCollectionMembers                                                     │
│ // ── 私有状态 ──                                                                                                     │
│ featureSpatialIndexByUid, featureUidsByName, featureUidsByCollectionKey,                                              │
│ _astarGScore, _astarClosed, _astarCameFrom, _astarHasParent,                                                          │
│ visualRoot, visualCells, runtimeDefaultMat, lastClickInfo, lastClickInfoTime, warnedNoClickCamera                     │
│ // ── 属性 ──                                                                                                         │
│ GroundY                                                                                                               │
│                                                                                                                       │
│ ---                                                                                                                   │
│ 方法分区（8 个公开 + 8 个内部）                                                                                       │
│                                                                                                                       │
│ #: 1                                                                                                                  │
│ Region 名: Unity 生命周期                                                                                             │
│ 包含方法: Start, Update, OnGUI                                                                                        │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 2                                                                                                                  │
│ Region 名: 公开 API — 网格构建                                                                                        │
│ 包含方法: BuildGridFromCampusJson, RebuildVisualization, ClearVisualizationOnly                                       │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 3                                                                                                                  │
│ Region 名: 公开 API — 坐标转换                                                                                        │
│ 包含方法: WorldToGrid, GridToWorldCenter, TryFindNearestWalkable (×2)                                                 │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 4                                                                                                                  │
│ Region 名: 公开 API — 单元格查询                                                                                      │
│ 包含方法: IsInBounds, IsWalkable, GetCellType, GetCellFeatureUid, GetCellFeatureName, TryGetCellFeatureInfo,          │
│   TryGetCellFeatureInfoByWorld, GetCellsByFeatureName                                                                 │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 5                                                                                                                  │
│ Region 名: 公开 API — 要素定位                                                                                        │
│ 包含方法: TryGetFeatureFirstCell, TryGetFeatureFirstCellByUid, TryResolveFeatureCell, TryResolveFeatureAliasCell,     │
│   TryGetFeatureOccupiedCells, TryGetFeatureApproachCells, TryBuildFeatureRingPath, TryBuildVisitPath                  │
│ 说明: 最常被外部调用                                                                                                  │
│ ────────────────────────────────────────                                                                              │
│ #: 6                                                                                                                  │
│ Region 名: 公开 API — 要素目录与集合                                                                                  │
│ 包含方法: BuildFeatureCatalogSummary, TryGetFeatureCollectionMembers, TryResolveFeatureCollectionBySelector,          │
│   TryResolveFeatureSpatialProfile                                                                                     │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 7                                                                                                                  │
│ Region 名: 公开 API — 寻路                                                                                            │
│ 包含方法: FindPathAStar, WorldPositionsToBlockedKeys                                                                  │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 8                                                                                                                  │
│ Region 名: 内部 — 要素索引解析                                                                                        │
│ 包含方法: TryResolveFeatureSpatialIndex, TryResolveFeatureAliasCellByNormalized, SelectFeatureReferenceToken,         │
│   ComputeCollectionSelectorScore, NormalizeCollectionSelectorToken, RegisterFeatureSpatialName,                       │
│   RegisterCollectionMembership, RegisterFeatureAlias                                                                  │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 9                                                                                                                  │
│ Region 名: 内部 — 空间计算                                                                                            │
│ 包含方法: BuildSpatialProfileFromIndex, CloneSpatialProfile, ComputeAnchorBiasPenalty, ResolveFeatureAnchor,          │
│   ComputeFeatureApproachCells, ComputeFeatureBoundaryCells, FindNearestCellToPoint, AppendPathSegment (×2)            │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 10                                                                                                                 │
│ Region 名: 内部 — 网格构建                                                                                            │
│ 包含方法: BuildLogicalGrid, PrepareFeatureRasterization, DetermineRasterMode, PrepareFeatureRasterArea,               │
│   PrepareFeatureRasterLine, BoundsToGridRange, InitializeFeatureSpatialIndexes, RegisterFeatureSpatialCell,           │
│   FinalizeFeatureSpatialIndexes, AppendFeatureRasterBounds, AssignCellFeatureIdentity                                 │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 11                                                                                                                 │
│ Region 名: 内部 — JSON 解析与要素元数据                                                                               │
│ 包含方法: TryLoadJsonFromCampusLoader, ParseCampusJson, AssignFeatureRuntimeMetadata, GetEffectiveFeatureName,        │
│   BuildRuntimeAliasForFeature, NormalizeFeatureKindToken, SanitizeFeatureAliasToken, GetHorizontalMapScale,           │
│   ApplyHorizontalScaleToFeatures, ScaleRingCollection, ScalePointCollection, ScalePointAroundPivot,                   │
│   ScaleRectAroundPivot, TryStripStructuredTargetPrefix, ReadXY                                                        │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 12                                                                                                                 │
│ Region 名: 内部 — 要素几何检测                                                                                        │
│ 包含方法: FeatureOverlapsCell, AreaPartOverlapsCell, PointInFilledArea, AnyRectCornerInFilledArea,                    │
│   RectFullyInsideAnyHole, RectFullyInsidePolygon, PolygonIntersectsRect, PolygonHasVertexInsideRect,                  │
│   PolygonEdgesIntersectRect, ComputeBoundsFromPolygon, ComputeBoundsFromLine, ComputeBoundsFromRings,                 │
│   GetFeatureRasterPriority                                                                                            │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 13                                                                                                                 │
│ Region 名: 内部 — 可视化                                                                                              │
│ 包含方法: RebuildVisualization (private helper), CreateOrUpdateCellVisual, GetCellColor, GetGridVisualMaterial        │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 14                                                                                                                 │
│ Region 名: 内部 — A* 寻路实现                                                                                         │
│ 包含方法: Heuristic, IsDiagonal, PackCellKey, IsPathWalkable, CanTraverseDiagonal, HasDirectLineOfSight,              │
│   SimplifyAStarPathInPlace, AddNeighbors, ReconstructPath                                                             │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 15                                                                                                                 │
│ Region 名: 内部 — 点击查询                                                                                            │
│ 包含方法: HandleRuntimeClickQuery, ResolveClickCamera, TryGetQueryWorldPoint, SetAndMaybeLogClickInfo                 │
│ 说明:                                                                                                                 │
│ ────────────────────────────────────────                                                                              │
│ #: 16                                                                                                                 │
│ Region 名: 内部 — 几何工具（静态）                                                                                    │
│ 包含方法: RectContainsPointInclusive, RectsOverlapInclusive, MergeRect, GetCellCenterXY, GetCellRectXY,               │
│   NormalizeFeatureToken, Cross2, Orient2D, OnSegment2D, SegmentsIntersect2D, PointInPolygon2D,                        │
│   PointInMultiPolygonWithHoles, DistPointToSegment2D, DistPointToPolyline, SignedArea2D, EnsureWinding,               │
│   RemoveClosingDuplicate, CleanRingInPlace, RemoveCollinearPointsInPlace, KindToCellType, IsBlockedKind,              │
│   AssignCellFeatureIdentity (if static), DestroyImmediateSafe                                                         │
│ 说明:                                                                                                                 │
│                                                                                                                       │
│ ---                                                                                                                   │
│ 注释风格规范                                                                                                          │
│                                                                                                                       │
│ 公开方法：XML doc                                                                                                     │
│                                                                                                                       │
│ /// <summary>                                                                                                         │
│ /// 将世界坐标转换为网格坐标。超出边界时返回最近边界格。                                                              │
│ /// </summary>                                                                                                        │
│ /// <param name="worldPos">世界空间坐标</param>                                                                       │
│ /// <returns>对应的网格坐标 (col, row)</returns>                                                                      │
│ public Vector2Int WorldToGrid(Vector3 worldPos) { ... }                                                               │
│                                                                                                                       │
│ 字段：行尾注释                                                                                                        │
│                                                                                                                       │
│ public float cellSize = 2f;        // 每个网格格子的世界单位大小                                                      │
│ private bool[,] _astarClosed;     // A* 已关闭集，避免重复扩展节点                                                    │
│                                                                                                                       │
│ 私有方法：单行 // 描述（函数体内关键步骤也加注释）                                                                    │
│                                                                                                                       │
│ // 从空间索引中提取质心、边界框、轮廓等几何属性，生成 FeatureSpatialProfile。                                         │
│ private FeatureSpatialProfile BuildSpatialProfileFromIndex(FeatureSpatialIndex idx) { ... }                           │
│                                                                                                                       │
│ ---                                                                                                                   │
│ 需要修改的文件                                                                                                        │
│                                                                                                                       │
│ - Maps/CampusGrid2D.cs（唯一）                                                                                        │
│                                                                                                                       │
│ ---                                                                                                                   │
│ 不改动的内容                                                                                                          │
│                                                                                                                       │
│ - 所有方法体内的逻辑（一行代码不动）                                                                                  │
│ - 字段的默认值、Attribute（[SerializeField]、[Header]、[Range] 等）                                                   │
│ - 嵌套类/结构体（在 CampusGridCellType 等外部文件中定义，不在此文件）                                                 │
│ - using 指令和命名空间声明                                                                                            │
│                                                                                                                       │
│ ---                                                                                                                   │
│ 验证方法                                                                                                              │
│                                                                                                                       │
│ 1. Unity Editor 中打开项目，确认无编译错误                                                                            │
│ 2. 进入 Play 模式，在 Inspector 中确认所有序列化字段仍然可见                                                          │
│ 3. 发起一次 MoveTo 指令，确认 A* 路径正常（功能未被破坏）                                                             │
│ 4. 在 VS/Rider 中查看公开方法，确认 XML doc tooltip 显示正常                                                          │
╰───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╯






● ---                                                                                                 
  设计概览                                                                                                                                                                                                    一、紧急情况统一成 4 种类型                                                                         
                                                                                                        除了你举例的三种，还有：目标移动/消失、资源被抢占、路径被封锁、时间压力、能力不匹配、通信中断、任务   过载等。                                                                                            
                                                                                                        这些都可以统一成 4 种 IncidentType，无需逐一列举字段：                                                
  ┌───────────────────┬─────────────────────────────────────────────┬────────────────────┐            
  │       类型        │                    描述                     │      响应方向      │
  ├───────────────────┼─────────────────────────────────────────────┼────────────────────┤
  │ AgentUnavailable  │ agent 彻底退出（没电/故障/被消灭）          │ 重新分配任务 slot  │
  ├───────────────────┼─────────────────────────────────────────────┼────────────────────┤
  │ AgentImpaired     │ agent 仍在但需要帮助（卡住/能力不足）       │ 派遣援助或缩减任务 │            
  ├───────────────────┼─────────────────────────────────────────────┼────────────────────┤              │ PlanInvalid       │ 计划的前提假设不再成立（目标移动/资源被抢） │ 触发重规划         │            
  ├───────────────────┼─────────────────────────────────────────────┼────────────────────┤              │ CapacityShortfall │ 团队整体能力不足（多人掉线/时间压力）       │ 跨组请援或降级目标 │
  └───────────────────┴─────────────────────────────────────────────┴────────────────────┘              
  二、结构化字段设计                                                                                    
  一个 IncidentReport 覆盖全部类型，靠 incidentType 区分语义，不为每种情况加特殊字段：                  
  IncidentReport {                                                                                    
      incidentId, incidentType, severity    // 标识和严重度                                           
      reporterId, groupId                   // 谁报、哪组                                             
      affectedAgentId, affectedTaskId       // 受影响对象（类型决定是否为空）                         
      description                           // 自然语言，LLM 自行理解                                 
      status                                // Open/Debating/Resolved/Abandoned                       
      finalResolutionSummary                // 最终决策                                                 }                                                                                                   
                                                                                                        Severity 自动判定（无需 LLM）：AgentUnavailable → Critical，其他按"是否阻塞当前 step"判               High/Medium。                                                                                       
                                                                                                        三、白板优化 + MAD 框架                                                                               
  问题：现有白板 informs-only，LastWriteWins，10s 过期，无法支持多轮辩论线程。                                                                                                                              
  解法：Hybrid 方案（参考 Du et al. 2023, Chan et al. 2023）                                                                                                                                                
  - CommunicationModule 处理辩论消息路由（6 个新 MessageType：IncidentReport / IncidentAnnounce /       DebateProposal / DebateCritique / DebateUpdate / DebateResolved）                                   
  - 白板只写两种结果：IncidentAnnounce（leader 宣布事件）和 DebateConsensus（最终共识），查询时用     
  staleSeconds=120f 覆盖默认 10s                                                                        
  辩论流程（4 阶段）：                                                                                  1. 角色分配：受影响 agent → Proposer，leader → Proposer，其余 → Critic                              
  2. 各自独立 LLM 提案/批评（每人 1 次 ~300 tokens）                                                    3. GroupMonitor 汇总后广播，各方互看互投票                                                          
  4. 多数收敛 or 达到最大轮数 → leader 仲裁 → 写共识到白板                                            
                                                                                                      
  Critical 最多 2 轮，High 3 轮，Medium 4 轮，超时 leader 单独仲裁。                                  
                                                                                                        四、不放弃已有任务的保障                                                                                                                                                                                    在 ADM rolling loop 的两个 action batch 之间注入辩论参与窗口：                                        - 非 Critical：当前 action batch 自然完成后才参与辩论
  - Critical：通过 Update() fast-path 立即中断（只有最高严重度才打断）                                  - 每次参与最多 1 个 LLM 调用，之后立即返回原来的 rolling loop                                       
                                                                                                      
  五、改动范围（增量，不破坏现有流程）                                                                
                                                                                                        新增 3 个文件（枚举、合约、协调器），修改 5 个现有文件，全部是增量改动：SharedWhiteboard/GroupMonito  r/ActionDecisionModule/CommunicationEnums/CommunicationModule。                                     
                                                                                                      
  ---
  有几个问题需要你确认（见计划文件第7节），我先问最关键的一个：
                                                                                                      
  CapacityShortfall 类型时，是否希望允许辩论参与者跨组（通过 RefereeManager 邀请其他组的 leader
  加入辩论），还是先只在组内决策、再通过 RefereeManager 广播求援结果？                                                                                                                                      ● User answered Claude's questions:                                                                     ⎿  · CapacityShortfall 紧急情况时，其他组是否参与辩论？ → 组内辩论，结果广播（推荐）                
     · PlanInvalid 类型的紧急情况，辩论的目的是什么？ → 辩论决定是否/如何重规划（推荐）               
     · 辩论协调逻辑放在哪里？ → 独立 IncidentCoordinator 类
                                                                                                      
● Updated plan                                                                                        
  ⎿  /plan to preview
                                                                                                      
──────────────────────────────────────────────────────────────────────────────────────────────────────
 Ready to code?                                                                                       
                                                                                                       Here is Claude's plan:                                                                               
╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌ 紧急情况处理 + MAD白板协同 设计方案                                                                                                                                                                         Context                                                                                                                                                                                                     当前系统只能顺利执行合作任务，遇到突发情况（agent没电、故障、求援等）没有结构化处理机制。白板只是通  
 报状态，不能让队友共同决策。目标：引入 Multi-Agent Debate (MAD) 框架，让 agent
 在遇到紧急情况时能够集体辩论出解决方案，同时不放弃已有任务。

 ---
 1. 紧急情况分类

 已有情况（用户举例）

 - Agent 没电
 - Agent 故障（硬件/软件）
 - Agent 需要队友帮助（卡住、被阻挡）

 还可能出现的情况

 - Agent 被消灭（对抗任务）
 - 任务目标消失/移动（环境变化）
 - 资源被抢占（竞争任务）
 - 路径被封锁（新障碍物）
 - 时间压力（deadline 将至）
 - 能力不匹配（分配了超出能力的角色）
 - 通信中断（超出通信范围）
 - 过载（部分 agent 掉线后剩余 agent 任务过多）

 统一成 4 种类型（IncidentType）

 ┌─────────────────┬────────────────────┬───────────────────────────────┬──────────────────────┐      
 │      类型       │        描述        │           典型场景            │       响应策略       │      
 ├─────────────────┼────────────────────┼───────────────────────────────┼──────────────────────┤      
 │ AgentUnavailabl │ Agent 无法继续执行 │ 没电、硬件故障、被消灭、软件  │ 任务重新分配         │      
 │ e               │ 任何任务           │ 崩溃                          │                      │      
 ├─────────────────┼────────────────────┼───────────────────────────────┼──────────────────────┤      
 │ AgentImpaired   │ Agent 能继续工作但 │ 卡住、能力不匹配、通信降级    │ 派遣援助或缩减任务范 │      
 │                 │ 需要帮助           │                               │ 围                   │      
 ├─────────────────┼────────────────────┼───────────────────────────────┼──────────────────────┤      
 │ PlanInvalid     │ 计划假设不再成立   │ 目标移动、资源被抢、环境变化  │ 触发（部分）重规划   │      
 ├─────────────────┼────────────────────┼───────────────────────────────┼──────────────────────┤      
 │ CapacityShortfa │ 团队整体能力不足   │ 多人掉线后工作量失衡、时间压  │ 优先级重排或跨组请援 │      
 │ ll              │                    │ 力                            │                      │      
 └─────────────────┴────────────────────┴───────────────────────────────┴──────────────────────┘      

 这 4 种类型能覆盖全部已知场景，且每种类型对应清晰的响应策略，不需要按情况列举字段。

 ---
 2. 关键数据结构设计（最小化字段）

 核心原则：ONE unified IncidentReport，不按 type 分子类

 IncidentReport {
     incidentId:             string   // "inc_001"
     incidentType:           IncidentType   // 4种类型
     severity:               IncidentSeverity // Critical/High/Medium/Low
     reporterId:             string   // 上报者 agentId
     groupId:                string   // 所属团队
     affectedAgentId:        string   // 受影响 agent（PlanInvalid/CapacityShortfall 时为空）
     affectedTaskId:         string   // 受影响的 stepId/missionId（可为空）
     description:            string   // 自然语言，供 LLM 理解
     status:                 IncidentStatus // Open/Debating/Resolved/Abandoned
     reportedAt:             float
     resolvedAt:             float    // 0 = 未解决
     proposedResolutionIds:  string[] // 辩论提案 id 列表
     finalResolutionSummary: string   // 最终自然语言决策
 }

 Severity 自动判定规则（无需 LLM）

 ┌─────────────────────────────────────────────┬──────────────────────────────┐
 │                    类型                     │           Severity           │
 ├─────────────────────────────────────────────┼──────────────────────────────┤
 │ AgentUnavailable                            │ Critical                     │
 ├─────────────────────────────────────────────┼──────────────────────────────┤
 │ AgentImpaired + 阻塞当前 step               │ High；非阻塞 → Medium        │
 ├─────────────────────────────────────────────┼──────────────────────────────┤
 │ PlanInvalid + 影响当前 step                 │ High；影响未来 step → Medium │
 ├─────────────────────────────────────────────┼──────────────────────────────┤
 │ CapacityShortfall + 人手不足以覆盖所有 slot │ Critical；否则 High          │
 └─────────────────────────────────────────────┴──────────────────────────────┘

 DebateEntry（辩论记录）

 DebateEntry {
     entryId:     string       // "dbt_001_r1_agentA"
     incidentId:  string       // 反向引用
     authorId:    string       // 发言 agent
     debateRound: int          // 1-based
     role:        DebateRole   // Proposer/Critic/Voter/Arbiter
     content:     string       // 提案或批评（自然语言）
     confidence:  float        // 0.0-1.0（LLM 自评）
     voteFor:     string       // 投票支持的 entryId（Voter 角色用）
     createdAt:   float
 }

 DebateConsensusEntry（白板最终写入）

 DebateConsensusEntry {
     incidentId:           string
     resolution:           string  // 最终决策自然语言
     assignedAgentId:      string  // 执行方（可为空）
     missionScopeChanged:  bool    // 是否修改了任务范围
     decidedAt:            float
 }

 新增枚举（共 4 个）

 IncidentType:     AgentUnavailable / AgentImpaired / PlanInvalid / CapacityShortfall
 IncidentSeverity: Critical / High / Medium / Low
 IncidentStatus:   Open / Debating / Resolved / Abandoned
 DebateRole:       Proposer / Critic / Voter / Arbiter

 ---
 3. 白板扩展设计

 现有白板问题

 - 只能通报状态（LastWriteWins，10s 过期）
 - 无法支持多轮辩论线程
 - 过期时间太短（辩论可能持续 30-60s game time）

 扩展策略：Hybrid 方案（推荐）

 白板负责持久化团队状态（少量关键写入）：
 - 新增 IncidentAnnounce entry type：leader 宣布活跃事件，查询时用 staleSeconds: 120f
 - 新增 DebateConsensus entry type：leader 写入最终共识，全员读取后按决策行动

 CommunicationModule负责辩论消息路由（高频、多方向、自动清理）：
 - 新增 6 个 MessageType：IncidentReport, IncidentAnnounce, DebateProposal, DebateCritique,
 DebateUpdate, DebateResolved

 DebateEntry 对象不进白板，只走通信模块，由 GroupMonitor 汇总；避免白板变成辩论日志堆积。

 命名空间约定（防止 constraintId 冲突）

 - "__inc_<incidentId>__" → IncidentAnnounce 条目
 - "__dbt_<incidentId>__" → DebateConsensus 条目

 ---
 4. MAD 辩论协议

 理论依据

 - Du et al. 2023 "Improving Factuality and Reasoning through Multiagent Debate"：多 agent
 独立生成回答 → 互看 → 更新立场 → 迭代收敛。启发：用多轮辩论代替单次决策，提升决策鲁棒性。
 - Chan et al. 2023 "ChatEval"：角色分工（Proposer/Critic）比无差别辩论效果更好。启发：按 agent       
 与事件关系分配角色。
 - Li et al. 2023 "Camel"：基于角色扮演的 agent 交互。启发：每个 agent 从其 RoleType 视角发言。       

 辩论阶段设计

 Phase 0 — 事件上报（立即异步）
 - 任何 agent 检测到紧急情况 → 创建 IncidentReport → 发给 leader
 - leader 的 GroupMonitor 接收，确定 severity，启动 DebateCoordinatorCoroutine

 Phase 1 — 独立提案（Round 1）
 - 角色分配规则：受影响 agent → Proposer；leader → Proposer；其余 → Critic
 - 每个 agent 在其"辩论参与窗口"（见第5节）做一次 LLM 调用
 - Proposer 输出：{proposal, confidence, rationale}
 - Critic 输出：{critique, preferred_proposal, confidence}
 - 结果以 DebateProposal/DebateCritique 消息发回 GroupMonitor

 Phase 2 — 交叉批评（Round 2-N）
 - GroupMonitor 收集 Round 1 结果（或超时）→ 广播 DebateUpdate 给全体
 - 每个 agent 重新评估，可改变立场或投票
 - 支持最多 maxRounds[severity] 轮：Critical=2，High=3，Medium=4

 Phase 3 — 收敛判断（每轮后）
 - 收敛条件（满足任一即收敛）：
   a. 投票多数：>= ceil(N/2) 支持同一提案
   b. 置信度集中：最高提案均值 > 0.7，第二名 < 0.5
   c. 达到轮数上限 → leader 担任 Arbiter，做最终 LLM 仲裁（1次调用）

 Phase 4 — 决策执行
 - GroupMonitor 写 DebateConsensus 到白板 + 广播 DebateResolved
 - 各 agent 在下一个参与窗口读取，按 assignedAgentId 执行决策：
   - AgentUnavailable → PlanningModule.ReassignSlot()
   - AgentImpaired → 援助 agent 注入新 PlanStep
   - PlanInvalid → leader 触发 PlanningModule.RequestReplan()
   - CapacityShortfall → 通过 RefereeManager 跨组请援

 LLM 调用预算

 - 3人组，Critical(2轮)：最多 7 次 LLM 调用（3+3+1仲裁）
 - 5人组，High(3轮)：最多 16 次
 - 每次约 300 tokens，已与 rolling loop 的 LLM 调用量级相当

 ---
 5. 不放弃已有任务的设计保障

 辩论参与窗口（Debate Participation Window）

 在 ADM RunRollingLoop 的 step11（UpdateHistory）与 iterationCount++ 之间注入一次检查：

 // ── 11.5 辩论参与窗口 ──────────────────────
 if (HasPendingDebateRole())
 {
     yield return StartCoroutine(ParticipateInActiveDebates());
     // 最多 1 次 LLM 调用后立即返回 rolling loop
 }

 关键特性：
 - 非 Critical 情况：等到 action batch 自然结束后才参与，不打断当前 action
 - Critical 情况：通过 Update() 中的 fast-path OnCriticalIncident() 立即中断 action
 - 每次辩论最多 1 个 LLM 调用：不会让辩论消耗超过 1 次 rolling 迭代的时间
 - 无 pending role 时：O(1) 字典查找，立即 pass，零成本

 任务与辩论的优先级关系

 ┌──────────┬───────────────────────┬──────────────────────────────┐
 │ Severity │       任务行为        │           辩论时机           │
 ├──────────┼───────────────────────┼──────────────────────────────┤
 │ Critical │ 中断当前 action batch │ 立即参与（Update fast-path） │
 ├──────────┼───────────────────────┼──────────────────────────────┤
 │ High     │ 继续当前 action batch │ 下一个 between-batch 窗口    │
 ├──────────┼───────────────────────┼──────────────────────────────┤
 │ Medium   │ 继续执行              │ 下一个 between-batch 窗口    │
 ├──────────┼───────────────────────┼──────────────────────────────┤
 │ Low      │ 不受影响              │ 无需辩论，leader 单方决策    │
 └──────────┴───────────────────────┴──────────────────────────────┘

 ---
 6. 集成架构（现有代码改动点）

 新增文件（3 个）

 ┌──────────────────────────────────────────────────┬─────────────────────────────────────────────┐   
 │                       文件                       │                    作用                     │   
 ├──────────────────────────────────────────────────┼─────────────────────────────────────────────┤   
 │ /Enums/IncidentEnums.cs                          │ 4 个新枚举                                  │   
 ├──────────────────────────────────────────────────┼─────────────────────────────────────────────┤   
 │ /LLM_Modules/Data/Emergency/IncidentContracts.cs │ IncidentReport, DebateEntry,                │   
 │                                                  │ DebateConsensusEntry                        │   
 ├──────────────────────────────────────────────────┼─────────────────────────────────────────────┤   
 │ /LLM_Modules/IncidentCoordinator.cs              │ DebateCoordinatorCoroutine                  │   
 │                                                  │ 逻辑（GroupMonitor 拥有）                   │   
 └──────────────────────────────────────────────────┴─────────────────────────────────────────────┘   

 修改文件（5 个，改动均为增量）

 ┌─────────────────────────┬──────────────────────────────────────────────────────────────────────┐   
 │          文件           │                                 改动                                 │   
 ├─────────────────────────┼──────────────────────────────────────────────────────────────────────┤   
 │ SharedWhiteboard.cs     │ WhiteboardEntryType 枚举增加 IncidentAnnounce,                       │   
 │                         │ DebateConsensus（2行）                                               │   
 ├─────────────────────────┼──────────────────────────────────────────────────────────────────────┤   
 │ GroupMonitor.cs         │ 增加 HandleIncidentReport()、启动                                    │   
 │                         │ IncidentCoordinator、_activeIncidents 字典                           │   
 ├─────────────────────────┼──────────────────────────────────────────────────────────────────────┤   
 │ ActionDecisionModule.cs │ 注入 debate window（step 11.5）、OnCriticalIncident()                │   
 │                         │ 入口、ParticipateInActiveDebates()                                   │   
 ├─────────────────────────┼──────────────────────────────────────────────────────────────────────┤   
 │ CommunicationEnums.cs   │ MessageType 枚举增加 6 个新值                                        │   
 ├─────────────────────────┼──────────────────────────────────────────────────────────────────────┤   
 │ CommunicationModule.cs  │ HandleMessage switch 增加 6 个新 case（复用现有 ForwardPayload<T>    │   
 │                         │ helper）                                                             │   
 └─────────────────────────┴──────────────────────────────────────────────────────────────────────┘   

 检测入口（利用现有机制）

 ┌───────────────────────────────────────────────┬────────────────────────────────────────────────┐   
 │                 现有触发路径                  │                   接入新机制                   │   
 ├───────────────────────────────────────────────┼────────────────────────────────────────────────┤   
 │ IntelligentAgent.HandleBatteryDepletion()     │ 改为创建                                       │   
 │                                               │ IncidentReport(AgentUnavailable/Critical)      │   
 ├───────────────────────────────────────────────┼────────────────────────────────────────────────┤   
 │ ReflectionModule.NotifyBlocked()              │ 创建 IncidentReport(AgentImpaired/High)        │   
 ├───────────────────────────────────────────────┼────────────────────────────────────────────────┤   
 │ ReflectionModule.NotifyActionOutcome(false×2) │ 创建 IncidentReport(AgentImpaired/Medium)      │   
 ├───────────────────────────────────────────────┼────────────────────────────────────────────────┤   
 │ RefereeEventType.AgentEliminated              │ GroupMonitor 自动创建                          │   
 │                                               │ IncidentReport(AgentUnavailable/Critical)      │   
 ├───────────────────────────────────────────────┼────────────────────────────────────────────────┤   
 │ ADM maxIterations exceeded                    │ 创建 IncidentReport(AgentImpaired/Medium)      │   
 └───────────────────────────────────────────────┴────────────────────────────────────────────────┘   

 ---
 7. 已确认设计决策

 1. CapacityShortfall：组内 MAD 辩论得出求援方案后，通过 RefereeManager 广播；其他组 leader
 自行决定是否支援，不参与辩论
 2. PlanInvalid：辩论目标是决定"是否重规划"及"重规划范围（全局 or 当前 step）"，由 PlanningModule     
 执行实际重规划
 3. IncidentCoordinator：独立类（非 MonoBehaviour），由 GroupMonitor 实例化和持有，遵循现有代码结构   
 4. 事件去重：leader 按 (incidentType, affectedAgentId, affectedTaskId) 去重，30s
 内相同事件不重复开启辩论

 ---
 8. 验证方式

 1. 单元测试场景：3 agent 小队，手动触发 HandleBatteryDepletion()，观察辩论日志（DebateEntry
 消息流）和最终白板写入
 2. AgentStateServer dashboard（8765 端口）：增加 activeIncidents 和 debateHistory 字段到
 AgentStateSnapshot
 3. 完整流程回归：验证非紧急 agent 在辩论期间的 rolling loop 延迟增量（应 < 1 次 LLM 调用延迟）   