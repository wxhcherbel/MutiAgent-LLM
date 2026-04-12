
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





