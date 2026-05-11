using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using System.Collections.Generic;

/// <summary>
/// 智能体生成器（重构版）
/// 核心目标：
/// 1) 移除对 MapGenerator(mapManager) 的依赖。
/// 2) 使用 CampusGrid2D 作为“可通行/禁飞”判定地图。
/// 3) 支持手动模式：鼠标左键拖拽一个矩形，在矩形内按规则排列生成智能体。
/// 4) 若选区包含禁飞区（blocked 单元格），提示用户重新选择。
/// </summary>
public class AgentSpawner : MonoBehaviour
{
    [Header("UI References")]
    public GameObject spawnPanel;                    // 智能体创建面板
    public TMP_Dropdown agentTypeDropdown;           // 智能体类型下拉框
    public TMP_InputField countInputField;           // 数量输入框
    public Slider perceptionRangeSlider;             // 感知范围滑块
    public Slider commRangeSlider;                   // 通信范围滑块
    public TextMeshProUGUI perceptionRangeValueText; // 感知范围数值显示
    public TextMeshProUGUI commRangeValueText;       // 通信范围数值显示
    public Button spawnButton;                       // 生成按钮
    public Button cancelButton;                      // 取消按钮
    public TMP_Dropdown spawnModeDropdown;           // 生成方式下拉菜单

    [Header("Prefab References")]
    public GameObject quadcopterPrefab;              // 无人机预制体
    public GameObject wheeledRobotPrefab;            // 轮式机器人预制体

    [Header("Spawn Settings")]
    public LayerMask spawnGroundLayer;               // 鼠标拾取/地面采样层（为空时自动回退到全部层）
    public int maxAgents = 100;                      // 最大智能体数量

    [Header("阵营配置")]
    [Tooltip("本次生成中破坏/对抗型 agent 的数量，其余均为协作型。生成时从后往前分配：后 adversarialCount 个为破坏型。")]
    public int adversarialCount = 0;                 // 破坏型 agent 数量（0 表示全部协作型）
    [Min(0f)] public float droneSpawnHeight = 2f;    // 无人机在地面基础上的起飞高度

    [Header("地图引用")]
    public CampusJsonMapLoader campusFeature;        // 校园地图特征（用于获取 groundZ 等基础参数）
    public CampusGrid2D campusGrid;                  // 网格化地图（用于禁飞区判定与世界坐标映射）
    public Camera mainCamera;                        // 主摄像机
    public bool isWaitingForSpawnLocation = false;   // 是否处于“等待用户拖拽选择矩形”状态

    [Header("手动矩形生成")]
    [Tooltip("若启用：矩形内只要包含任意禁飞格子，整次生成直接失败并提示重选。")]
    public bool requireRectFullyWalkable = true;
    [Tooltip("拖拽矩形最小边长（米），小于该值会被视为无效选区。")]
    [Min(0.1f)] public float minDragRectSize = 1f;
    [Tooltip("是否在游戏界面显示手动生成提示信息。")]
    public bool showHintOnScreen = true;

    [Header("矩形预览")]
    [Min(0.01f)] public float previewLineWidth = 0.12f;
    [Min(0f)] public float previewLineYOffset = 0.2f;
    public Color previewValidColor = new Color(0.2f, 0.9f, 0.3f, 0.95f);
    public Color previewInvalidColor = new Color(0.95f, 0.25f, 0.2f, 0.95f);

    [Header("可视性与局部实验")]
    [Tooltip("仅放大可视表现（模型缩放）。大地图下建议 2~5。")]
    [Min(0.1f)] public float agentVisualScaleMultiplier = 3f;
    [Tooltip("手动框选生成后，自动把相机聚焦到该区域。")]
    public bool autoFocusCameraToSpawnArea = true;
    [Min(1f)] public float focusMargin = 1.35f;

    private readonly List<GameObject> spawnedAgents = new List<GameObject>(); // 已生成智能体列表
    private int currentAgentCount = 0;                                         // 当前总数量

    // 手动拖拽状态
    private bool isDraggingRect = false;
    private Vector3 dragStartWorld;
    private Vector3 dragCurrentWorld;

    // 缓存本次待生成参数（点击“生成”后确定）
    private AgentType pendingAgentType = AgentType.Quadcopter;
    private int pendingCount = 0;
    private float pendingCommRange = 0f;
    private float pendingPerceptionRange = 0f;

    // 屏幕提示缓存
    private string runtimeHint = "";
    private float runtimeHintUntil = -999f;

    // 矩形线框预览
    private LineRenderer rectPreviewLine;

    // 生成方式枚举（与下拉框选项顺序保持一致）
    private enum SpawnMode
    {
        Manual = 0,
        Random = 1
    }

    private void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;

        // 自动补齐地图引用，降低 Inspector 配置出错概率
        if (campusFeature == null) campusFeature = FindObjectOfType<CampusJsonMapLoader>();
        if (campusGrid == null && campusFeature != null) campusGrid = campusFeature.GetComponent<CampusGrid2D>();
        if (campusGrid == null) campusGrid = FindObjectOfType<CampusGrid2D>();

        // UI 事件绑定
        if (spawnButton != null) spawnButton.onClick.AddListener(OnSpawnButtonClicked);
        if (cancelButton != null) cancelButton.onClick.AddListener(OnCancelButtonClicked);
        if (perceptionRangeSlider != null) perceptionRangeSlider.onValueChanged.AddListener(UpdatePerceptionRangeValue);
        if (commRangeSlider != null) commRangeSlider.onValueChanged.AddListener(UpdateCommRangeValue);

        // 初始化 UI 数值
        if (perceptionRangeSlider != null) UpdatePerceptionRangeValue(perceptionRangeSlider.value);
        if (commRangeSlider != null) UpdateCommRangeValue(commRangeSlider.value);
        InitializeSpawnModeDropdown();

        if (spawnPanel != null) spawnPanel.SetActive(false);

        // 尝试确保网格可用（用于禁飞区判定）
        EnsureGridReady();

        // 准备矩形预览线（仅运行时使用）
        EnsureRectPreviewLine();
        SetRectPreviewVisible(false);
    }

    private void Update()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (!isWaitingForSpawnLocation) return;

        HandleManualRectangleInput();
        UpdateRectPreviewVisual();
    }

    private void OnGUI()
    {
        if (!showHintOnScreen) return;

        bool hasRuntimeHint = !string.IsNullOrEmpty(runtimeHint) && Time.time <= runtimeHintUntil;
        bool showDragHint = isWaitingForSpawnLocation;
        if (!hasRuntimeHint && !showDragHint) return;

        string dragText = "";
        if (showDragHint)
        {
            dragText = isDraggingRect
                ? $"拖拽中：宽={Mathf.Abs(dragCurrentWorld.x - dragStartWorld.x):F1}m，长={Mathf.Abs(dragCurrentWorld.z - dragStartWorld.z):F1}m（松开左键确认）"
                : "手动生成：左键按下并拖拽出矩形区域，松开左键确认；右键取消。";
        }

        string finalText = hasRuntimeHint ? runtimeHint : "";
        if (!string.IsNullOrEmpty(dragText))
        {
            finalText = string.IsNullOrEmpty(finalText) ? dragText : $"{finalText}\n{dragText}";
        }

        GUI.Box(new Rect(10f, 10f, Mathf.Min(Screen.width - 20f, 900f), 64f), finalText);
    }

    /// <summary>
    /// 初始化生成方式下拉菜单。
    /// 当前版本重点完善 manual；random 保留兼容入口。
    /// </summary>
    private void InitializeSpawnModeDropdown()
    {
        if (spawnModeDropdown == null) return;
        spawnModeDropdown.ClearOptions();
        spawnModeDropdown.AddOptions(new List<string> { "manual", "random" });
        spawnModeDropdown.value = 0;
    }

    /// <summary>
    /// 更新感知范围数值显示。
    /// </summary>
    private void UpdatePerceptionRangeValue(float value)
    {
        if (perceptionRangeValueText != null) perceptionRangeValueText.text = value.ToString("F1");
    }

    /// <summary>
    /// 更新通信范围数值显示。
    /// </summary>
    private void UpdateCommRangeValue(float value)
    {
        if (commRangeValueText != null) commRangeValueText.text = value.ToString("F1");
    }

    /// <summary>
    /// 打开智能体创建面板。
    /// </summary>
    public void OpenSpawnPanel()
    {
        if (spawnPanel != null) spawnPanel.SetActive(true);
        ResetUIValues();
    }

    /// <summary>
    /// 重置 UI 默认值。
    /// </summary>
    private void ResetUIValues()
    {
        if (agentTypeDropdown != null) agentTypeDropdown.value = 0;
        if (countInputField != null) countInputField.text = "1";
        if (commRangeSlider != null) commRangeSlider.value = commRangeSlider.maxValue * 0.5f;
        if (perceptionRangeSlider != null) perceptionRangeSlider.value = perceptionRangeSlider.maxValue * 0.5f;
        if (spawnModeDropdown != null) spawnModeDropdown.value = 0;
    }

    /// <summary>
    /// 生成按钮点击逻辑。
    /// 先读取配置并缓存，再根据模式进入手动拖拽或随机入口。
    /// </summary>
    private void OnSpawnButtonClicked()
    {
        if (!TryReadSpawnConfig(out AgentType agentType, out int count, out float commRange, out float perceptionRange))
        {
            return;
        }

        if (currentAgentCount + count > maxAgents)
        {
            ShowHint($"无法生成 {count} 个智能体：超过最大限制 {maxAgents}（当前已有 {currentAgentCount} 个）", true);
            return;
        }

        pendingAgentType = agentType;
        pendingCount = count;
        pendingCommRange = commRange;
        pendingPerceptionRange = perceptionRange;

        if (spawnPanel != null) spawnPanel.SetActive(false);

        SpawnMode selectedMode = spawnModeDropdown != null ? (SpawnMode)spawnModeDropdown.value : SpawnMode.Manual;
        if (selectedMode == SpawnMode.Manual)
        {
            BeginManualSpawnSelection();
        }
        else
        {
            // 用户要求“先不用管随机生成”，因此这里保留基础兼容，但不作为当前重点入口。
            SpawnAgentsAtRandomLocation();
        }
    }

    /// <summary>
    /// 取消按钮：关闭面板并退出手动选择状态。
    /// </summary>
    private void OnCancelButtonClicked()
    {
        if (spawnPanel != null) spawnPanel.SetActive(false);
        CancelManualSpawnSelection("已取消手动生成。");
    }

    /// <summary>
    /// 进入手动生成模式：等待用户左键拖拽矩形。
    /// </summary>
    private void BeginManualSpawnSelection()
    {
        isWaitingForSpawnLocation = true;
        isDraggingRect = false;
        SetRectPreviewVisible(false);
        ShowHint("手动生成已开启：左键拖拽矩形区域，松开确认；右键取消。");
    }

    /// <summary>
    /// 取消手动生成状态并清理预览。
    /// </summary>
    private void CancelManualSpawnSelection(string message)
    {
        isWaitingForSpawnLocation = false;
        isDraggingRect = false;
        SetRectPreviewVisible(false);
        if (!string.IsNullOrEmpty(message)) ShowHint(message);
    }

    /// <summary>
    /// 手动矩形输入处理：
    /// 1) 左键按下：记录起点
    /// 2) 左键按住：更新终点
    /// 3) 左键松开：校验并执行生成
    /// 4) 右键：取消手动模式
    /// </summary>
    private void HandleManualRectangleInput()
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            return;
        }

        if (Input.GetMouseButtonDown(1))
        {
            CancelManualSpawnSelection("已取消手动生成。");
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (TryGetMouseWorldOnMap(out Vector3 world))
            {
                isDraggingRect = true;
                dragStartWorld = world;
                dragCurrentWorld = world;
            }
            else
            {
                ShowHint("未拾取到地图位置，请在可见地图区域操作。", true);
            }
            return;
        }

        if (!isDraggingRect) return;

        if (Input.GetMouseButton(0))
        {
            if (TryGetMouseWorldOnMap(out Vector3 world))
            {
                dragCurrentWorld = world;
            }
            return;
        }

        if (Input.GetMouseButtonUp(0))
        {
            if (TryGetMouseWorldOnMap(out Vector3 world))
            {
                dragCurrentWorld = world;
            }

            Rect worldRect = BuildWorldRectXZ(dragStartWorld, dragCurrentWorld);
            isDraggingRect = false;

            if (worldRect.width < minDragRectSize || worldRect.height < minDragRectSize)
            {
                ShowHint($"选区太小，请重新拖拽（最小边长 {minDragRectSize:F1}m）。", true);
                return;
            }

            if (!TrySpawnAgentsInRectangle(worldRect, pendingAgentType, pendingCount, pendingCommRange, pendingPerceptionRange, out string failReason))
            {
                ShowHint(failReason, true);
                return;
            }

            CancelManualSpawnSelection("手动生成完成。");
        }
    }

    /// <summary>
    /// 在“世界坐标 XZ 矩形”内按规则排列生成智能体。
    /// - 规则：先在网格上均匀采样候选点，再补齐到目标数量。
    /// - 禁飞校验：可配置为“矩形内出现任意 blocked 就失败”。
    /// </summary>
    private bool TrySpawnAgentsInRectangle(
        Rect worldRect,
        AgentType agentType,
        int count,
        float commRange,
        float perceptionRange,
        out string failReason)
    {
        failReason = "";

        if (count <= 0)
        {
            failReason = "生成数量必须大于 0。";
            return false;
        }

        if (currentAgentCount + count > maxAgents)
        {
            failReason = $"无法生成：超过最大数量 {maxAgents}。";
            return false;
        }

        if (!EnsureGridReady())
        {
            failReason = "地图网格未就绪（CampusGrid2D 不可用），无法判定禁飞区。";
            return false;
        }

        if (!TryBuildOrderedGridPoints(worldRect, count, out List<Vector2Int> gridPoints, out failReason))
        {
            return false;
        }

        GameObject prefabToSpawn = agentType == AgentType.Quadcopter ? quadcopterPrefab : wheeledRobotPrefab;
        if (prefabToSpawn == null)
        {
            failReason = "预制体为空，请检查 Quadcopter/WheeledRobot 预制体配置。";
            return false;
        }

        // 后 adversarialCount 个为破坏型，其余为协作型
        int clampedAdversarial = Mathf.Clamp(adversarialCount, 0, count);

        int spawnedCount = 0;
        for (int i = 0; i < gridPoints.Count; i++)
        {
            Vector2Int g = gridPoints[i];
            Vector3 worldPos = campusGrid.GridToWorldCenter(g.x, g.y);
            worldPos = AlignToGround(worldPos);

            if (agentType == AgentType.Quadcopter)
            {
                worldPos.y += droneSpawnHeight;
            }

            bool isAdversarial = i >= (count - clampedAdversarial);
            SpawnSingleAgent(prefabToSpawn, agentType, worldPos, commRange, perceptionRange, isAdversarial);
            spawnedCount++;
        }

        ShowHint($"成功生成 {spawnedCount} 个 {agentType}，当前总数: {currentAgentCount}/{maxAgents}");
        if (autoFocusCameraToSpawnArea)
        {
            FocusCameraToWorldRect(worldRect, focusMargin);
        }
        return true;
    }

    /// <summary>
    /// 从用户拖拽矩形中构建“规则排列”的网格点：
    /// 1) 先把世界矩形映射为网格范围。
    /// 2) 收集可通行格子，并按配置检查禁飞区。
    /// 3) 先做均匀采样，再按行列顺序补齐，保证排列有规律且可落地。
    /// </summary>
    private bool TryBuildOrderedGridPoints(Rect worldRect, int needCount, out List<Vector2Int> ordered, out string failReason)
    {
        ordered = new List<Vector2Int>(needCount);
        failReason = "";

        if (!TryWorldRectToGridRange(worldRect, out int xMin, out int xMax, out int zMin, out int zMax))
        {
            failReason = "选区超出地图有效范围，请重新框选。";
            return false;
        }

        var walkableCells = new List<Vector2Int>();
        bool containsBlocked = false;

        for (int z = zMin; z <= zMax; z++)
        {
            for (int x = xMin; x <= xMax; x++)
            {
                if (campusGrid.IsWalkable(x, z))
                {
                    walkableCells.Add(new Vector2Int(x, z));
                }
                else
                {
                    containsBlocked = true;
                }
            }
        }

        if (containsBlocked && requireRectFullyWalkable)
        {
            failReason = "选区包含禁飞区，请重新生成。";
            return false;
        }

        if (walkableCells.Count < needCount)
        {
            failReason = $"选区内可用格子不足：需要 {needCount}，实际仅 {walkableCells.Count}。";
            return false;
        }

        int widthCells = xMax - xMin + 1;
        int lengthCells = zMax - zMin + 1;
        float aspect = widthCells / Mathf.Max(1f, (float)lengthCells);
        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(needCount * aspect)));
        int rows = Mathf.CeilToInt(needCount / (float)cols);

        var used = new HashSet<Vector2Int>();

        // 第一轮：均匀采样，优先保证“规则排列感”
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (ordered.Count >= needCount) break;

                int x = xMin + Mathf.Clamp(Mathf.FloorToInt((c + 0.5f) * widthCells / cols), 0, widthCells - 1);
                int z = zMin + Mathf.Clamp(Mathf.FloorToInt((r + 0.5f) * lengthCells / rows), 0, lengthCells - 1);
                Vector2Int g = new Vector2Int(x, z);

                if (!campusGrid.IsWalkable(g.x, g.y)) continue;
                if (!used.Add(g)) continue;
                ordered.Add(g);
            }
        }

        // 第二轮：按行列顺序补齐，确保数量达标
        if (ordered.Count < needCount)
        {
            for (int z = zMin; z <= zMax; z++)
            {
                for (int x = xMin; x <= xMax; x++)
                {
                    if (ordered.Count >= needCount) break;
                    Vector2Int g = new Vector2Int(x, z);
                    if (!campusGrid.IsWalkable(x, z)) continue;
                    if (!used.Add(g)) continue;
                    ordered.Add(g);
                }
                if (ordered.Count >= needCount) break;
            }
        }

        if (ordered.Count < needCount)
        {
            failReason = "选区可用格子无法满足规则排列，请扩大选区后重试。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 将世界矩形（XZ）映射为网格索引范围。
    /// 采用“必须完全位于地图边界内”的策略，便于给出稳定预期。
    /// </summary>
    private bool TryWorldRectToGridRange(Rect worldRect, out int xMin, out int xMax, out int zMin, out int zMax)
    {
        xMin = xMax = zMin = zMax = -1;
        if (campusGrid == null || campusGrid.blockedGrid == null) return false;

        Rect map = campusGrid.mapBoundsXY;
        if (worldRect.xMin < map.xMin || worldRect.xMax > map.xMax || worldRect.yMin < map.yMin || worldRect.yMax > map.yMax)
        {
            return false;
        }

        float cs = Mathf.Max(0.0001f, campusGrid.cellSize);
        xMin = Mathf.Clamp(Mathf.FloorToInt((worldRect.xMin - map.xMin) / cs), 0, campusGrid.gridWidth - 1);
        xMax = Mathf.Clamp(Mathf.FloorToInt((worldRect.xMax - map.xMin) / cs), 0, campusGrid.gridWidth - 1);
        zMin = Mathf.Clamp(Mathf.FloorToInt((worldRect.yMin - map.yMin) / cs), 0, campusGrid.gridLength - 1);
        zMax = Mathf.Clamp(Mathf.FloorToInt((worldRect.yMax - map.yMin) / cs), 0, campusGrid.gridLength - 1);

        return xMin <= xMax && zMin <= zMax;
    }

    /// <summary>
    /// 尝试从鼠标位置获取“地图上的世界点”：
    /// 1) 先射线命中碰撞体（点击建筑/道路模型也可）。
    /// 2) 若未命中，再与 groundY 平面求交。
    /// 注意：最终只取 xz，有效高度统一落到 groundY，避免因点中高处模型导致 y 偏移。
    /// </summary>
    private bool TryGetMouseWorldOnMap(out Vector3 world)
    {
        world = Vector3.zero;
        if (mainCamera == null) return false;

        float groundY = GetMapGroundY();
        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

        int mask = spawnGroundLayer.value == 0 ? ~0 : spawnGroundLayer.value;
        if (Physics.Raycast(ray, out RaycastHit hit, 5000f, mask, QueryTriggerInteraction.Ignore))
        {
            world = new Vector3(hit.point.x, groundY, hit.point.z);
            return true;
        }

        Plane plane = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));
        if (plane.Raycast(ray, out float enter))
        {
            Vector3 p = ray.GetPoint(enter);
            world = new Vector3(p.x, groundY, p.z);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 构造 XZ 平面的轴对齐矩形（Rect 的 y 分量在这里表示世界 z）。
    /// </summary>
    private static Rect BuildWorldRectXZ(Vector3 a, Vector3 b)
    {
        float minX = Mathf.Min(a.x, b.x);
        float maxX = Mathf.Max(a.x, b.x);
        float minZ = Mathf.Min(a.z, b.z);
        float maxZ = Mathf.Max(a.z, b.z);
        return Rect.MinMaxRect(minX, minZ, maxX, maxZ);
    }

    /// <summary>
    /// 更新矩形预览线：
    /// - 绿色：当前选区有效
    /// - 红色：当前选区包含禁飞区/越界
    /// </summary>
    private void UpdateRectPreviewVisual()
    {
        if (!isWaitingForSpawnLocation || !isDraggingRect)
        {
            SetRectPreviewVisible(false);
            return;
        }

        Rect rect = BuildWorldRectXZ(dragStartWorld, dragCurrentWorld);
        bool valid = rect.width >= minDragRectSize && rect.height >= minDragRectSize;

        if (valid && EnsureGridReady())
        {
            if (TryWorldRectToGridRange(rect, out int xMin, out int xMax, out int zMin, out int zMax))
            {
                if (requireRectFullyWalkable)
                {
                    for (int z = zMin; z <= zMax && valid; z++)
                    {
                        for (int x = xMin; x <= xMax; x++)
                        {
                            if (!campusGrid.IsWalkable(x, z))
                            {
                                valid = false;
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                valid = false;
            }
        }

        DrawRectPreview(rect, valid ? previewValidColor : previewInvalidColor);
    }

    /// <summary>
    /// 对齐到地面高度（用于生成点最终落地）。
    /// </summary>
    private Vector3 AlignToGround(Vector3 worldPos)
    {
        int mask = spawnGroundLayer.value == 0 ? ~0 : spawnGroundLayer.value;
        Vector3 rayOrigin = new Vector3(worldPos.x, GetMapGroundY() + 500f, worldPos.z);
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 2000f, mask, QueryTriggerInteraction.Ignore))
        {
            worldPos.y = hit.point.y;
        }
        else
        {
            worldPos.y = GetMapGroundY();
        }
        return worldPos;
    }

    /// <summary>
    /// 读取并校验 UI 输入参数。
    /// </summary>
    private bool TryReadSpawnConfig(out AgentType type, out int count, out float commRange, out float perceptionRange)
    {
        type = AgentType.Quadcopter;
        count = 0;
        commRange = 0f;
        perceptionRange = 0f;

        if (agentTypeDropdown == null || countInputField == null || commRangeSlider == null || perceptionRangeSlider == null)
        {
            ShowHint("UI 引用不完整，请检查 AgentSpawner 的 Inspector 绑定。", true);
            return false;
        }

        type = (AgentType)agentTypeDropdown.value;
        commRange = commRangeSlider.value;
        perceptionRange = perceptionRangeSlider.value;

        if (!int.TryParse(countInputField.text, out count) || count <= 0)
        {
            ShowHint("请输入有效数量（正整数）。", true);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 保底确保网格就绪：用于禁飞校验和坐标映射。
    /// </summary>
    private bool EnsureGridReady()
    {
        if (campusGrid == null && campusFeature != null) campusGrid = campusFeature.GetComponent<CampusGrid2D>();
        if (campusGrid == null) campusGrid = FindObjectOfType<CampusGrid2D>();
        if (campusGrid == null) return false;

        if (campusGrid.blockedGrid == null || campusGrid.cellTypeGrid == null)
        {
            campusGrid.BuildGridFromCampusJson();
        }

        return campusGrid.blockedGrid != null && campusGrid.cellTypeGrid != null;
    }

    /// <summary>
    /// 获取地图基准地面高度。
    /// </summary>
    private float GetMapGroundY()
    {
        if (campusFeature != null) return campusFeature.groundZ;
        if (campusGrid != null) return campusGrid.GroundY;
        return 0f;
    }

    /// <summary>
    /// 随机模式（兼容入口）：
    /// 当前不是本次需求重点，这里实现为“从可通行网格随机采样”。
    /// </summary>
    public void SpawnAgentsAtRandomLocation()
    {
        if (pendingCount <= 0)
        {
            ShowHint("随机生成失败：待生成数量为 0。", true);
            return;
        }

        if (!EnsureGridReady())
        {
            ShowHint("随机生成失败：CampusGrid2D 不可用。", true);
            return;
        }

        GameObject prefabToSpawn = pendingAgentType == AgentType.Quadcopter ? quadcopterPrefab : wheeledRobotPrefab;
        if (prefabToSpawn == null)
        {
            ShowHint("随机生成失败：预制体为空。", true);
            return;
        }

        int target = Mathf.Min(pendingCount, maxAgents - currentAgentCount);
        int spawned = 0;
        int attempts = 0;
        int maxAttempts = Mathf.Max(50, target * 20);

        while (spawned < target && attempts < maxAttempts)
        {
            attempts++;
            Vector3 pos = GetRandomSpawnPosition(pendingAgentType);
            if (pos == Vector3.zero) continue;

            SpawnSingleAgent(prefabToSpawn, pendingAgentType, pos, pendingCommRange, pendingPerceptionRange, false);
            spawned++;
        }

        if (spawned < target)
        {
            ShowHint($"随机生成只完成 {spawned}/{target}，请优先使用 manual 模式。", true);
        }
        else
        {
            ShowHint($"随机生成完成：{spawned} 个。");
        }
    }

    /// <summary>
    /// 对外提供随机出生点（ML-Agents 重置时会调用）。
    /// 已改为基于 CampusGrid2D 的可通行格采样，不再依赖 mapManager。
    /// </summary>
    public Vector3 GetRandomSpawnPosition(AgentType agentType)
    {
        if (EnsureGridReady())
        {
            int tryCount = 0;
            const int maxTry = 400;
            while (tryCount++ < maxTry)
            {
                int x = Random.Range(0, campusGrid.gridWidth);
                int z = Random.Range(0, campusGrid.gridLength);
                if (!campusGrid.IsWalkable(x, z)) continue;

                Vector3 worldPos = campusGrid.GridToWorldCenter(x, z);
                worldPos = AlignToGround(worldPos);
                if (agentType == AgentType.Quadcopter) worldPos.y += droneSpawnHeight;
                return worldPos;
            }
        }

        return Vector3.zero;
    }

    /// <summary>
    /// 实例化单个智能体并完成属性初始化。
    /// </summary>
    private void SpawnSingleAgent(GameObject prefab, AgentType agentType, Vector3 worldPos, float commRange, float perceptionRange, bool isAdversarial = false)
    {
        GameObject agentObj = Instantiate(prefab, worldPos, Quaternion.identity);
        agentObj.name = $"{agentType}_{GetNextAgentID()}";
        agentObj.layer = LayerMask.NameToLayer("Agent");
        agentObj.tag = "Agent";

        // 大地图下默认放大模型，提升可见性与交互体验
        if (agentVisualScaleMultiplier > 0.1f && !Mathf.Approximately(agentVisualScaleMultiplier, 1f))
        {
            agentObj.transform.localScale *= agentVisualScaleMultiplier;
        }

        spawnedAgents.Add(agentObj);
        currentAgentCount++;

        InitializeAgent(agentObj, agentType, commRange, perceptionRange, isAdversarial);
    }

    /// <summary>
    /// 初始化智能体属性与必要组件。
    /// </summary>
    private void InitializeAgent(GameObject agentObj, AgentType type, float commRange, float perceptionRange, bool isAdversarial = false)
    {
        // 1) 碰撞体
        if (agentObj.GetComponent<Collider>() == null)
        {
            BoxCollider collider = agentObj.AddComponent<BoxCollider>();
            collider.size = type == AgentType.Quadcopter
                ? new Vector3(1f, 0.2f, 1f)
                : new Vector3(0.8f, 0.6f, 1.2f);
        }

        // 2) 刚体
        Rigidbody rb = agentObj.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = agentObj.AddComponent<Rigidbody>();
            rb.mass = type == AgentType.Quadcopter ? 2f : 10f;
            rb.drag = type == AgentType.Quadcopter ? 1f : 0.5f;
            rb.angularDrag = type == AgentType.Quadcopter ? 2f : 0.5f;
            if (type == AgentType.Quadcopter)
            {
                rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }
        }

        // 3) 核心智能体脚本
        IntelligentAgent agent = agentObj.GetComponent<IntelligentAgent>();
        if (agent == null) agent = agentObj.AddComponent<IntelligentAgent>();

        agent.Properties = new AgentProperties
        {
            AgentID = agentObj.name,
            Type = type,
            Role = RoleType.Scout,
            MaxSpeed = 10f,
            BatteryCapacity = 100f,
            CommunicationRange = commRange,
            PerceptionRange = perceptionRange
        };

        // 4) 通信模块
        CommunicationModule commModule = agentObj.GetComponent<CommunicationModule>();
        if (commModule == null) commModule = agentObj.AddComponent<CommunicationModule>();

        // 5) 感知模块 + 可视化
        PerceptionModule perceptionModule = agentObj.GetComponent<PerceptionModule>();
        if (perceptionModule == null) perceptionModule = agentObj.AddComponent<PerceptionModule>();
        if (agentObj.GetComponent<PerceptionVisualizer>() == null)
            agentObj.AddComponent<PerceptionVisualizer>();

        // 6) LLM 控制模块
        AgentLLMControl llmControl = agentObj.GetComponent<AgentLLMControl>();
        if (llmControl == null) llmControl = agentObj.AddComponent<AgentLLMControl>();

        // 7) 运动执行器
        AgentMotionExecutor mlController = agentObj.GetComponent<AgentMotionExecutor>();
        if (mlController == null) mlController = agentObj.AddComponent<AgentMotionExecutor>();

        // 7.5) 可视化增强：让大地图中的 agent 更容易被定位和观察。
        if (agentObj.GetComponent<AgentVisualMarker>() == null)
        {
            agentObj.AddComponent<AgentVisualMarker>();
        }

        // 8) 人格系统：写入阵营标记
        PersonalitySystem personalitySystem = agentObj.GetComponent<PersonalitySystem>();
        if (personalitySystem == null) personalitySystem = agentObj.AddComponent<PersonalitySystem>();
        personalitySystem.Profile.isAdversarial = isAdversarial;

        // 立即下发边界配置，避免 ML 控制器在首帧将智能体错误夹到固定点
        if (campusGrid == null) EnsureGridReady();
        // if (campusGrid != null)
        // {
        //     mlController.ConfigureBoundaryFromCampusGrid(campusGrid);
        // }
    }

    /// <summary>
    /// 获取下一个智能体 ID（3 位补零）。
    /// </summary>
    private string GetNextAgentID()
    {
        return (currentAgentCount + 1).ToString("D3");
    }

    /// <summary>
    /// 清除所有已生成智能体。
    /// </summary>
    public void ClearAllAgents()
    {
        for (int i = 0; i < spawnedAgents.Count; i++)
        {
            if (spawnedAgents[i] != null)
            {
                Destroy(spawnedAgents[i]);
            }
        }
        spawnedAgents.Clear();
        currentAgentCount = 0;
        ShowHint("已清除所有智能体。");
    }

    /// <summary>
    /// 编辑器快捷方法：右键组件可直接打开面板。
    /// </summary>
    [ContextMenu("Open Spawn Panel")]
    public void OpenPanelFromEditor()
    {
        OpenSpawnPanel();
    }

    /// <summary>
    /// 输出提示到屏幕和控制台。
    /// </summary>
    private void ShowHint(string message, bool warning = false)
    {
        runtimeHint = message ?? "";
        runtimeHintUntil = Time.time + 4f;

        if (warning) Debug.LogWarning($"[AgentSpawner] {message}");
        else Debug.Log($"[AgentSpawner] {message}");
    }

    /// <summary>
    /// 确保矩形预览线存在。
    /// </summary>
    private void EnsureRectPreviewLine()
    {
        if (rectPreviewLine != null) return;

        var go = new GameObject("SpawnRectPreview");
        go.transform.SetParent(transform, false);
        rectPreviewLine = go.AddComponent<LineRenderer>();
        rectPreviewLine.useWorldSpace = true;
        rectPreviewLine.loop = false;
        rectPreviewLine.positionCount = 5;
        rectPreviewLine.widthMultiplier = previewLineWidth;
        rectPreviewLine.alignment = LineAlignment.View;
        rectPreviewLine.numCapVertices = 2;
        rectPreviewLine.numCornerVertices = 2;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        rectPreviewLine.material = new Material(shader);
        rectPreviewLine.material.color = previewValidColor;
    }

    /// <summary>
    /// 显示/隐藏矩形预览线。
    /// </summary>
    private void SetRectPreviewVisible(bool visible)
    {
        if (rectPreviewLine == null) return;
        if (rectPreviewLine.gameObject.activeSelf != visible)
        {
            rectPreviewLine.gameObject.SetActive(visible);
        }
    }

    /// <summary>
    /// 绘制当前矩形预览线。
    /// </summary>
    private void DrawRectPreview(Rect worldRect, Color color)
    {
        EnsureRectPreviewLine();
        if (rectPreviewLine == null) return;

        float y = GetMapGroundY() + previewLineYOffset;
        Vector3 p0 = new Vector3(worldRect.xMin, y, worldRect.yMin);
        Vector3 p1 = new Vector3(worldRect.xMax, y, worldRect.yMin);
        Vector3 p2 = new Vector3(worldRect.xMax, y, worldRect.yMax);
        Vector3 p3 = new Vector3(worldRect.xMin, y, worldRect.yMax);

        rectPreviewLine.startWidth = previewLineWidth;
        rectPreviewLine.endWidth = previewLineWidth;
        rectPreviewLine.startColor = color;
        rectPreviewLine.endColor = color;

        rectPreviewLine.SetPosition(0, p0);
        rectPreviewLine.SetPosition(1, p1);
        rectPreviewLine.SetPosition(2, p2);
        rectPreviewLine.SetPosition(3, p3);
        rectPreviewLine.SetPosition(4, p0);

        SetRectPreviewVisible(true);
    }

    /// <summary>
    /// 将摄像机聚焦到当前实验区域，避免在超大地图里找不到刚生成的智能体。
    /// </summary>
    private void FocusCameraToWorldRect(Rect worldRect, float margin)
    {
        CameraController cameraController = null;
        if (campusFeature != null) cameraController = campusFeature.cameraController;
        if (cameraController == null) cameraController = FindObjectOfType<CameraController>();
        if (cameraController == null) return;

        Vector3 center = new Vector3(worldRect.center.x, GetMapGroundY(), worldRect.center.y);
        Vector3 size = new Vector3(Mathf.Max(1f, worldRect.width), 1f, Mathf.Max(1f, worldRect.height));
        Bounds b = new Bounds(center, size);
        cameraController.FitToBoundsXZ(b, Mathf.Max(1f, margin));
    }
}
