using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class MapGenerator : MonoBehaviour
{
    [Header("地图设置")]
    public int mapWidth = 0; // 地图宽度（网格数）
    public int mapLength = 0; // 地图长度（网格数）
    public int mapHeight = 0; // 地图高度
    
    [Header("UI界面")]
    public GameObject mapStartUI; // 地图生成UI界面
    [Header("平面预制体")]
    public GameObject groundPrefab; // 地面平面预制体

    [Header("障碍物设置")]
    public int obstacleCount = 0; // 障碍物数量
    public GameObject[] obstaclePrefabs; // 障碍物预制体数组
    public float minObstacleScale = 0.8f; // 障碍物最小缩放
    public float maxObstacleScale = 1.5f; // 障碍物最大缩放

    [Header("资源点设置")]
    public int resourcePointCount = 0; // 资源点数量
    public GameObject[] resourcePointPrefabs; // 资源点预制体数组

    [Header("摄像机设置")]
    public CameraController cameraController; // 摄像机控制器
    public bool enableFreeCamera = true; // 是否启用自由摄像机

    [Header("网格可视化设置")]
    public bool showGrid = true; // 是否显示网格
    public Material gridMaterial; // 网格材质
    public Color freeCellColor = new Color(0.675f, 0.937f, 0.510f); 
    public Color occupiedCellColor = new Color(0.933f, 0.490f, 0.490f); 
    public Color edgeCellColor = Color.yellow; // 边界网格颜色

    [Header("UI引用")]
    public Slider widthSlider; // 控制地图宽度的滑动条
    public Slider lengthSlider; // 控制地图长度的滑动条
    public Slider heightSlider; // 控制地图高度的滑动条
    public Slider obstacleDensitySlider; // 控制障碍物密度的滑动条
    public Slider ResourceSlider; // 控制资源密度的滑动条
    public TMP_InputField seedInputField; // 输入随机种子的文本框
    public Button generateButton; // 生成按钮
    public Button saveButton; // 保存按钮
    public Button loadButton; // 加载按钮
    public TMP_Text statusText; // 状态文本
    public Toggle freeCameraToggle; // 自由摄像机切换
    public Toggle gridVisibilityToggle; // 网格可见性切换
    
    // 显示Slider数值的Text组件
    public TMP_Text widthText; 
    public TMP_Text lengthText; 
    public TMP_Text heightText; 
    public TMP_Text ResourceText;
    public TMP_Text obstacleDensityText;

    // 存储所有生成对象的列表
    private List<GameObject> allGeneratedObjects = new List<GameObject>();
    private int seed = 0; // 随机种子
    
    // 网格占用状态（用于防止物体重叠）
    public bool[,] gridOccupied;
    
    // 地面实例引用
    private GameObject groundInstance;

    // 预制体占用网格大小缓存
    private Dictionary<string, Vector2Int> prefabGridSizes = new Dictionary<string, Vector2Int>();

    // 网格单元大小（世界单位）
    public int gridCellSize = 1;

    // 网格可视化对象
    public GameObject gridVisualization;
    public List<GameObject> gridCells = new List<GameObject>();


    void Start()
    {
        // 设置UI事件监听
        generateButton.onClick.AddListener(GenerateMap);
        saveButton.onClick.AddListener(SaveMap);
        loadButton.onClick.AddListener(LoadMap);
        //freeCameraToggle.onValueChanged.AddListener(OnFreeCameraToggleChanged);
        gridVisibilityToggle.onValueChanged.AddListener(OnGridVisibilityToggleChanged);
        
        // 添加Slider值变化监听
        widthSlider.onValueChanged.AddListener(OnWidthSliderChanged);
        lengthSlider.onValueChanged.AddListener(OnLengthSliderChanged);
        heightSlider.onValueChanged.AddListener(OnHeightSliderChanged);
        ResourceSlider.onValueChanged.AddListener(OnResourceSliderChanged);
        obstacleDensitySlider.onValueChanged.AddListener(OnObstacleDensitySliderChanged);
        
        // 初始化UI值
        widthSlider.value = mapWidth;
        lengthSlider.value = mapLength;
        heightSlider.value = mapHeight;
        ResourceSlider.value = resourcePointCount;
        obstacleDensitySlider.value = obstacleCount / 10f;
        
        // 初始化Slider文本显示
        UpdateSliderTexts();
        
        // 初始化网格占用状态
        InitializeGrid();
        
        // 预计算预制体网格占用大小
        PrecalculatePrefabGridSizes();
        
        // 创建网格可视化
        CreateGridVisualization();
    }

    // 预计算所有预制体的网格占用大小
    private void PrecalculatePrefabGridSizes()
    {
        prefabGridSizes.Clear();
        
        // 计算障碍物预制体的网格占用大小
        foreach (GameObject prefab in obstaclePrefabs)
        {
            if (prefab != null)
            {
                Vector2Int gridSize = CalculatePrefabGridSize(prefab);
                prefabGridSizes[prefab.name] = gridSize;
            }
        }
        
        // 计算资源点预制体的网格占用大小
        foreach (GameObject prefab in resourcePointPrefabs)
        {
            if (prefab != null)
            {
                Vector2Int gridSize = CalculatePrefabGridSize(prefab);
                prefabGridSizes[prefab.name] = gridSize;
            }
        }
    }

    // 计算预制体在网格中的占用大小
    // 改进：实例化临时对象来获取真实尺寸
    private Vector2Int CalculatePrefabGridSize(GameObject prefab)
    {
        Vector3 size = Vector3.one;

        // 临时实例化预制体
        GameObject temp = Instantiate(prefab);
        temp.SetActive(false); // 不显示
        
        Collider collider = temp.GetComponent<Collider>();
        Renderer renderer = temp.GetComponent<Renderer>();

        if (collider != null)
        {
            size = collider.bounds.size;
        }
        else if (renderer != null)
        {
            size = renderer.bounds.size;
        }

        DestroyImmediate(temp); // 立即销毁临时对象

        int gridWidth = Mathf.CeilToInt(size.x / gridCellSize);
        int gridLength = Mathf.CeilToInt(size.z / gridCellSize);

        gridWidth = Mathf.Max(1, gridWidth);
        gridLength = Mathf.Max(1, gridLength);

        return new Vector2Int(gridWidth, gridLength);
    }

    // 初始化网格占用状态数组
    private void InitializeGrid()
    {
        gridOccupied = new bool[mapWidth, mapLength];
    }

    // 新增：清空网格占用状态（不改变数组引用）
    public void ClearGrid()
    {
        if (gridOccupied == null) return;

        int w = gridOccupied.GetLength(0);
        int l = gridOccupied.GetLength(1);

        for (int x = 0; x < w; x++)
        {
            for (int z = 0; z < l; z++)
            {
                gridOccupied[x, z] = false;
            }
        }

        // 可选：刷新可视化
        UpdateGridVisualization();
    }

    // 创建网格可视化
    private void CreateGridVisualization()
    {
        // 清除旧的网格可视化
        if (gridVisualization != null)
        {
            Destroy(gridVisualization);
        }
        
        gridCells.Clear();
        
        // 创建网格父对象
        gridVisualization = new GameObject("GridVisualization");
        //gridVisualization.transform.SetParent(transform);
        
        // 根据当前设置更新网格可见性
        UpdateGridVisibility();
    }

    // 更新网格可视化
    public void UpdateGridVisualization()
    {
        // 清除旧的网格单元
        foreach (GameObject cell in gridCells)
        {
            Destroy(cell);
        }
        gridCells.Clear();
        
        if (!showGrid || gridVisualization == null) return;
        
        // 创建新的网格单元
        for (int x = 0; x < mapWidth; x++)
        {
            for (int z = 0; z < mapLength; z++)
            {
                CreateGridCell(x, z);
            }
        }
    }

    // 创建单个网格单元可视化
    public void CreateGridCell(int gridX, int gridZ)
    {
        // 计算网格单元的世界位置
        Vector3 worldPosition = GridToWorldPosition(gridX, gridZ, Vector2Int.one);
        worldPosition.y = 0.6f; // 稍微高于地面避免z-fighting
        
        // 创建网格单元
        GameObject cell = GameObject.CreatePrimitive(PrimitiveType.Quad);
        cell.transform.SetParent(gridVisualization.transform);
        cell.transform.position = worldPosition;
        cell.transform.rotation = Quaternion.Euler(90, 0, 0);
        cell.transform.localScale = Vector3.one * gridCellSize * 0.96f; // 稍微缩小避免重叠
        
        // 设置网格材质和颜色
        Renderer renderer = cell.GetComponent<Renderer>();
        if (gridMaterial != null)
        {
            renderer.material = gridMaterial;
        }
        
        // 根据占用状态设置颜色
        if (gridX < 0 || gridX >= mapWidth || gridZ < 0 || gridZ >= mapLength)
        {
            renderer.material.color = edgeCellColor;
        }
        else if (gridOccupied[gridX, gridZ])
        {
            renderer.material.color = occupiedCellColor;
        }
        else
        {
            renderer.material.color = freeCellColor;
        }
        
        gridCells.Add(cell);
    }

    // 更新网格可见性
    public void UpdateGridVisibility()
    {
        if (gridVisualization != null)
        {
            gridVisualization.SetActive(showGrid);
        }
        
        if (showGrid)
        {
            UpdateGridVisualization();
            CreateCoordinateAxes();
            //CreateGridCoordinateLabels();
        }
    }

    // Slider值变化处理方法
    private void OnWidthSliderChanged(float value)
    {
        mapWidth = (int)value;
        UpdateSliderTexts();
    }

    private void OnLengthSliderChanged(float value)
    {
        mapLength = (int)value;
        UpdateSliderTexts();
    }
    
    private void OnHeightSliderChanged(float value)
    {
        mapHeight = (int)value;
        UpdateSliderTexts();
    }
    
    private void OnResourceSliderChanged(float value)
    {
        resourcePointCount = (int)value;
        UpdateSliderTexts();
    }
    
    private void OnObstacleDensitySliderChanged(float value)
    {
        obstacleCount = (int)(value * 10f);
        UpdateSliderTexts();
    }

    // 网格可见性切换
    private void OnGridVisibilityToggleChanged(bool isVisible)
    {
        showGrid = isVisible;
        UpdateGridVisibility();
    }

    // 更新所有Slider文本显示
    private void UpdateSliderTexts()
    {
        if (widthText != null)
            widthText.text = $"{mapWidth}/{widthSlider.maxValue}";
        
        if (lengthText != null)
            lengthText.text = $"{mapLength}/{lengthSlider.maxValue}";
            
        if (heightText != null)
            heightText.text = $"{mapHeight}/{heightSlider.maxValue}";

        if (ResourceText != null)
            ResourceText.text = $"{resourcePointCount}/{(int)ResourceSlider.maxValue}";

        if (obstacleDensityText != null)
            obstacleDensityText.text = $"{obstacleCount}/{(int)(obstacleDensitySlider.maxValue * 10f)}";
    }

    // 自由摄像机切换
    private void OnFreeCameraToggleChanged(bool isOn)
    {
        enableFreeCamera = isOn;
        if (cameraController != null)
        {
            cameraController.SetFreeCameraMode(isOn);
        }
    }

    // 生成地图的主函数
    public void GenerateMap()
    {
        // 更新参数
        UpdateParametersFromUI();

        // 显示状态
        statusText.text = "generating...";

        // 清除之前生成的对象
        ClearGeneratedObjects();

        // 重新初始化网格
        InitializeGrid();

        // 开始协程分步生成
        StartCoroutine(GenerateMapCoroutine());
    }

    // 从UI更新参数
    private void UpdateParametersFromUI()
    {
        // 处理种子输入
        if (!int.TryParse(seedInputField.text, out seed))
        {
            // 如果解析失败，使用随机种子
            seed = Random.Range(0, 99999);
            seedInputField.text = seed.ToString();
        }
        mapWidth = (int)widthSlider.value;
        mapLength = (int)lengthSlider.value;
        mapHeight = (int)heightSlider.value;
        resourcePointCount = (int)ResourceSlider.value;
        obstacleCount = (int)(obstacleDensitySlider.value * 10f);
        UpdateSliderTexts();
    }

    // 清除所有生成的对象
    private void ClearGeneratedObjects()
    {
        foreach (GameObject obj in allGeneratedObjects)
        {
            if (obj != null)
                Destroy(obj);
        }
        allGeneratedObjects.Clear();
        
        // 特别处理地面实例
        if (groundInstance != null)
        {
            Destroy(groundInstance);
            groundInstance = null;
        }
    }

    // 协程：分步生成地图
    private IEnumerator GenerateMapCoroutine()
    {
        // 设置随机种子以确保可重复性
        Random.InitState(seed);

        // 步骤1：生成地面
        GenerateGround();
        yield return null;

        // 步骤2：生成障碍物
        GenerateObstacles();
        yield return null;

        // 步骤3：生成资源点
        GenerateResourcePoints();
        yield return null;

        // 更新网格可视化
        UpdateGridVisualization();

        // 调整摄像机视角
        //FocusCameraOnMap();

        // 完成
        statusText.text = $"generate complicate! obstacle: {obstacleCount}, resource: {resourcePointCount}";
        mapStartUI.SetActive(false);
    }

    // 生成地面
    private void GenerateGround()
    {
        if (groundPrefab == null)
        {
            Debug.LogError("没有设置地面预制体!");
            return;
        }
        
        // 创建地面
        groundInstance = Instantiate(groundPrefab, Vector3.zero, Quaternion.identity);
        // 调整地面大小
        groundInstance.transform.localScale = new Vector3(1.00f/25.00f*(float)mapWidth, 1, 1.00f/25.00f*(float)mapLength);
        groundInstance.name = "地面";

        // 设置地面位置
        groundInstance.layer = LayerMask.NameToLayer("Ground"); // 确保 "Ground" 层存在
        
        // 添加到管理列表
        allGeneratedObjects.Add(groundInstance);
    }

    // 获取地面高度（通过射线检测）
    public float GetGroundHeight(Vector3 position)
    {
        Ray ray = new Ray(new Vector3(position.x, mapHeight, position.z), Vector3.down);
        RaycastHit hit;
        
        // 检测地面
        if (Physics.Raycast(ray, out hit, mapHeight * 2f))
        {
            return hit.point.y;
        }
        
        // 如果没有命中，返回0
        return 0f;
    }

    // 检查网格区域是否可用
    public bool IsGridAreaAvailable(int gridX, int gridZ, int gridWidth, int gridLength)
    {
        // 检查是否在网格范围内
        if (gridX < 0 || gridX + gridWidth > mapWidth || gridZ < 0 || gridZ + gridLength > mapLength)
            return false;
        
        // 检查区域内的所有单元格是否都未被占用
        for (int x = gridX; x < gridX + gridWidth; x++)
        {
            for (int z = gridZ; z < gridZ + gridLength; z++)
            {
                if (gridOccupied[x, z])
                    return false;
            }
        }
        
        return true;
    }

    // 检查物理碰撞（防止模型重叠）
    public bool CheckPhysicalCollision(Vector3 position, Vector2Int gridSize, GameObject prefab)
    {
        // 计算检测区域的大小（基于预制体大小）
        Vector3 halfExtents = new Vector3(
            gridSize.x * gridCellSize * 0.5f,
            mapHeight * 0.5f,
            gridSize.y * gridCellSize * 0.5f
        );
        
        // 调整检测中心点的高度
        Vector3 center = position + Vector3.up * mapHeight * 0.5f;
        
        // 使用BoxCast检测碰撞
        RaycastHit[] hits = Physics.BoxCastAll(center, halfExtents, Vector3.down, Quaternion.identity, mapHeight);
        
        foreach (RaycastHit hit in hits)
        {
            // 忽略地面和其他非障碍物/资源点物体
            if (hit.collider.gameObject != groundInstance && 
                hit.collider.gameObject.transform.parent != gridVisualization.transform)
            {
                return true;
            }
        }
        
        return false;
    }

    // 占用网格区域
    public void OccupyGridArea(int gridX, int gridZ, int gridWidth, int gridLength)
    {
        for (int x = gridX; x < gridX + gridWidth; x++)
        {
            for (int z = gridZ; z < gridZ + gridLength; z++)
            {
                if (x >= 0 && x < mapWidth && z >= 0 && z < mapLength)
                {
                    gridOccupied[x, z] = true;
                }
            }
        }
        
        // 更新网格可视化
        UpdateGridVisualization();
    }

    // 世界坐标转换为网格坐标
    public Vector2Int WorldToGridPosition(Vector3 worldPosition)
    {
        int gridX = Mathf.FloorToInt((worldPosition.x + (mapWidth * gridCellSize) / 2f) / gridCellSize);
        int gridZ = Mathf.FloorToInt((worldPosition.z + (mapLength * gridCellSize) / 2f) / gridCellSize);

        // 限制坐标在网格范围内
        gridX = Mathf.Clamp(gridX, 0, mapWidth - 1);
        gridZ = Mathf.Clamp(gridZ, 0, mapLength - 1);

        return new Vector2Int(gridX, gridZ);
    }


    // 网格坐标转换为世界坐标
    public Vector3 GridToWorldPosition(int gridX, int gridZ, Vector2Int gridCellSize)
    {
        float worldX = gridX * gridCellSize.x - (mapWidth * gridCellSize.x) / 2f + gridCellSize.x / 2f;
        float worldZ = gridZ * gridCellSize.y - (mapLength * gridCellSize.y) / 2f + gridCellSize.y / 2f;
        return new Vector3(worldX, 0, worldZ);
    }


    public Vector2Int GetGridSize()
    {
        return new Vector2Int(gridCellSize, gridCellSize); 
    }

    // 生成障碍物
    private void GenerateObstacles()
    {
        if (obstaclePrefabs == null || obstaclePrefabs.Length == 0)
        {
            Debug.LogWarning("没有设置障碍物预制体");
            return;
        }
        
        int placedCount = 0;
        int attempts = 0;
        int maxAttempts = obstacleCount * 20; // 增加尝试次数以提高成功率
        
        while (placedCount < obstacleCount && attempts < maxAttempts)
        {
            attempts++;
            
            // 随机选择障碍物预制体
            GameObject obstaclePrefab = obstaclePrefabs[Random.Range(0, obstaclePrefabs.Length)];
            
            // 获取预制体的网格占用大小
            Vector2Int gridSize = prefabGridSizes.ContainsKey(obstaclePrefab.name) ? 
                prefabGridSizes[obstaclePrefab.name] : new Vector2Int(1, 1);
            
            // 随机生成网格坐标（避免过于靠近边缘）
            int margin = Mathf.Max(1, gridSize.x, gridSize.y);
            int gridX = Random.Range(margin, mapWidth - margin - gridSize.x);
            int gridZ = Random.Range(margin, mapLength - margin - gridSize.y);
            
            // 检查网格区域是否可用
            if (!IsGridAreaAvailable(gridX, gridZ, gridSize.x, gridSize.y))
                continue;
            
            // 转换为世界坐标
            Vector3 worldPosition = GridToWorldPosition(gridX, gridZ, gridSize);
            
            // 使用射线检测获取地面高度
            float groundHeight = GetGroundHeight(worldPosition);
            worldPosition.y = groundHeight;
            
            // 检查物理碰撞（防止模型重叠）
            if (CheckPhysicalCollision(worldPosition, gridSize, obstaclePrefab))
                continue;
            
            // 随机旋转和缩放
            Quaternion rotation = Quaternion.Euler(0, Random.Range(0, 360f), 0);
            Vector3 scale = Vector3.one * Random.Range(minObstacleScale, maxObstacleScale);
            
            // 实例化障碍物
            GameObject obstacle = Instantiate(obstaclePrefab, worldPosition, rotation);
            obstacle.transform.localScale = scale;
            obstacle.name = $"Obstacle_{placedCount}";

            // 设置障碍物层
            obstacle.layer = LayerMask.NameToLayer("Obstacle"); // 确保 "Obstacle" 层存在
            
            // 占用网格区域
            OccupyGridArea(gridX, gridZ, gridSize.x, gridSize.y);
            
            // 添加到管理列表
            allGeneratedObjects.Add(obstacle);
            placedCount++;
        }
        
        if (placedCount < obstacleCount)
        {
            Debug.LogWarning($"无法放置所有障碍物，只放置了 {placedCount} 个");
        }
    }

    // 生成资源点
    private void GenerateResourcePoints()
    {
        if (resourcePointPrefabs == null || resourcePointPrefabs.Length == 0)
        {
            Debug.LogWarning("没有设置资源点预制体");
            return;
        }

        int placedCount = 0;
        int attempts = 0;
        int maxAttempts = resourcePointCount * 20; // 增加尝试次数以提高成功率

        while (placedCount < resourcePointCount && attempts < maxAttempts)
        {
            attempts++;

            // 随机选择资源点预制体
            GameObject resourcePrefab = resourcePointPrefabs[Random.Range(0, resourcePointPrefabs.Length)];

            // 获取预制体的网格占用大小
            Vector2Int gridSize = prefabGridSizes.ContainsKey(resourcePrefab.name) ?
                prefabGridSizes[resourcePrefab.name] : new Vector2Int(1, 1);

            // 随机生成网格坐标（避免边缘）
            int margin = Mathf.Max(1, gridSize.x, gridSize.y);
            int gridX = Random.Range(margin, mapWidth - margin - gridSize.x);
            int gridZ = Random.Range(margin, mapLength - margin - gridSize.y);

            // 检查网格区域是否可用
            if (!IsGridAreaAvailable(gridX, gridZ, gridSize.x, gridSize.y))
                continue;

            // 转换为世界坐标
            Vector3 worldPosition = GridToWorldPosition(gridX, gridZ, gridSize);

            // 使用射线检测获取地面高度
            float groundHeight = GetGroundHeight(worldPosition);
            worldPosition.y = groundHeight;

            // 检查物理碰撞（防止模型重叠）
            if (CheckPhysicalCollision(worldPosition, gridSize, resourcePrefab))
                continue;

            // 随机旋转
            Quaternion rotation = Quaternion.Euler(0, Random.Range(0, 360f), 0);

            // 实例化资源点
            GameObject resourcePoint = Instantiate(resourcePrefab, worldPosition, rotation);
            resourcePoint.name = $"Resource_{placedCount}";

            // 设置资源点层
            resourcePoint.layer = LayerMask.NameToLayer("Obstacle"); // 确保 "Obstacle" 层存在

            // 占用网格区域
            OccupyGridArea(gridX, gridZ, gridSize.x, gridSize.y);

            // 添加到管理列表
            allGeneratedObjects.Add(resourcePoint);
            placedCount++;
        }

        if (placedCount < resourcePointCount)
        {
            Debug.LogWarning($"无法放置所有资源点，只放置了 {placedCount} 个");
        }
    }



    // 调整摄像机视角对准地图中心
    private void FocusCameraOnMap()
    {
        if (cameraController == null) return;

        // 计算地图中心
        Vector3 mapCenter = new Vector3(0, 0, 0);

        // 计算合适的高度，使整个地图可见
        float maxDimension = Mathf.Max(mapWidth, mapLength);
        float cameraHeight = maxDimension * 0.8f;

        // 设置摄像机初始位置
        //cameraController.SetInitialPosition(new Vector3(0, cameraHeight, -maxDimension * 0.5f), mapCenter);
    }

    /// <summary>
    /// 重新排列资源和障碍物，但保持地面不变
    /// </summary>
    public void RegenerateResourcesAndObstacles()
    {
        Debug.Log("Starting RegenerateResourcesAndObstacles...");
        // 分别获取现有的障碍物和资源点
        var existingObstacles = allGeneratedObjects
            .Where(obj => obj != null && obj != groundInstance && 
                   obj.name.StartsWith("Obstacle"))
            .ToList();
            
        var existingResources = allGeneratedObjects
            .Where(obj => obj != null && obj != groundInstance && 
                   obj.name.StartsWith("Resource"))
            .ToList();
    
        // 清空网格占用状态
        ClearGrid();
        
        // 重新随机放置障碍物
        foreach (var obstacle in existingObstacles)
        {
            bool placed = false;
            int attempts = 0;
            Vector2Int gridSize = prefabGridSizes.ContainsKey(obstacle.name.Replace("(Clone)", "").Trim()) ? 
                prefabGridSizes[obstacle.name.Replace("(Clone)", "").Trim()] : new Vector2Int(1, 1);
                
            while (!placed && attempts < 20)
            {
                attempts++;
                
                // 随机新位置
                int margin = Mathf.Max(1, gridSize.x, gridSize.y);
                int gridX = Random.Range(margin, mapWidth - margin - gridSize.x);
                int gridZ = Random.Range(margin, mapLength - margin - gridSize.y);
                
                if (IsGridAreaAvailable(gridX, gridZ, gridSize.x, gridSize.y))
                {
                    // 计算新的世界坐标
                    Vector3 newPosition = GridToWorldPosition(gridX, gridZ, gridSize);
                    newPosition.y = GetGroundHeight(newPosition);
                    
                    // 如果位置合适，更新位置和旋转
                    obstacle.transform.position = newPosition;
                    obstacle.transform.rotation = Quaternion.Euler(0, Random.Range(0, 360f), 0);
                    
                    // 占用网格
                    OccupyGridArea(gridX, gridZ, gridSize.x, gridSize.y);
                    placed = true;
                }
            }
        }
    
        // 重新随机放置资源点
        foreach (var resource in existingResources)
        {
            bool placed = false;
            int attempts = 0;
            Vector2Int gridSize = prefabGridSizes.ContainsKey(resource.name.Replace("(Clone)", "").Trim()) ? 
                prefabGridSizes[resource.name.Replace("(Clone)", "").Trim()] : new Vector2Int(1, 1);
                
            while (!placed && attempts < 20)
            {
                attempts++;
                
                int margin = Mathf.Max(1, gridSize.x, gridSize.y);
                int gridX = Random.Range(margin, mapWidth - margin - gridSize.x);
                int gridZ = Random.Range(margin, mapLength - margin - gridSize.y);
                
                if (IsGridAreaAvailable(gridX, gridZ, gridSize.x, gridSize.y))
                {
                    Vector3 newPosition = GridToWorldPosition(gridX, gridZ, gridSize);
                    newPosition.y = GetGroundHeight(newPosition);
                    
                    resource.transform.position = newPosition;
                    resource.transform.rotation = Quaternion.Euler(0, Random.Range(0, 360f), 0);
                    
                    OccupyGridArea(gridX, gridZ, gridSize.x, gridSize.y);
                    placed = true;
                }
            }
        }
    
        // 更新网格可视化
        UpdateGridVisualization();
    
        // 更新状态
        if (statusText != null)
        {
            statusText.text = $"重新排列完成! 障碍物: {existingObstacles.Count}, 资源: {existingResources.Count}";
        }
    }

    // 保存地图数据
    public void SaveMap()
    {
        MapData mapData = new MapData();
        mapData.mapWidth = mapWidth;
        mapData.mapLength = mapLength;
        mapData.seed = seed;
        
        // 收集所有对象数据
        foreach (GameObject obj in allGeneratedObjects)
        {
            if (obj == null || obj == groundInstance) continue;
            
            ObjectData objData = new ObjectData();
            objData.prefabName = obj.name.Replace("(Clone)", "").Trim();
            objData.position = obj.transform.position;
            objData.rotation = obj.transform.rotation;
            objData.scale = obj.transform.localScale;
            
            mapData.objects.Add(objData);
        }
        
        // 转换为JSON
        string json = JsonUtility.ToJson(mapData, true);
        
        // 保存到文件
        string filePath = Application.persistentDataPath + "/saved_map.json";
        File.WriteAllText(filePath, json);
        
        statusText.text = $"地图已保存到: {filePath}";
    }

    // 加载地图数据
    public void LoadMap()
    {
        string filePath = Application.persistentDataPath + "/saved_map.json";
        
        if (!File.Exists(filePath))
        {
            statusText.text = "没有找到保存的地图文件";
            return;
        }
        
        // 读取文件
        string json = File.ReadAllText(filePath);
        MapData mapData = JsonUtility.FromJson<MapData>(json);
        
        // 更新地图尺寸
        mapWidth = mapData.mapWidth;
        mapLength = mapData.mapLength;
        seed = mapData.seed;
        
        // 更新UI
        UpdateSliderTexts();
        seedInputField.text = seed.ToString();
        
        // 清除当前地图
        ClearGeneratedObjects();
        InitializeGrid();
        
        // 预计算预制体网格占用大小
        PrecalculatePrefabGridSizes();
        
        // 显示状态
        statusText.text = "loading...";
        
        // 开始加载协程
        StartCoroutine(LoadMapCoroutine(mapData));
    }

    // 协程：加载地图
    private IEnumerator LoadMapCoroutine(MapData mapData)
    {
        // 生成地面
        GenerateGround();
        yield return null;
        
        // 实例化所有保存的对象
        foreach (ObjectData objData in mapData.objects)
        {
            // 查找预制体
            GameObject prefab = FindPrefabByName(objData.prefabName);
            if (prefab == null)
            {
                Debug.LogWarning($"找不到预制体: {objData.prefabName}");
                continue;
            }
            
            // 实例化对象
            GameObject obj = Instantiate(prefab, objData.position, objData.rotation);
            obj.transform.localScale = objData.scale;
            
            // 添加到管理列表
            allGeneratedObjects.Add(obj);
            
            // 计算并占用网格
            Vector3 worldPos = objData.position;
            Vector2Int gridPos = WorldToGridPosition(worldPos);
            
            // 获取预制体的网格占用大小
            Vector2Int gridSize = prefabGridSizes.ContainsKey(prefab.name) ? 
                prefabGridSizes[prefab.name] : new Vector2Int(1, 1);
            
            // 占用网格区域
            OccupyGridArea(gridPos.x, gridPos.y, gridSize.x, gridSize.y);
            
            yield return null;
        }
        
        // 更新网格可视化
        UpdateGridVisualization();
        
        // 调整摄像机视角
        FocusCameraOnMap();
        
        statusText.text = "Map complicate!";
    }

    // 根据名称查找预制体
    private GameObject FindPrefabByName(string name)
    {
        // 在障碍物预制体中查找
        foreach (GameObject prefab in obstaclePrefabs)
        {
            if (prefab.name == name) return prefab;
        }
        
        // 在资源点预制体中查找
        foreach (GameObject prefab in resourcePointPrefabs)
        {
            if (prefab.name == name) return prefab;
        }
        
        return null;
    }
    // =======================================================
    // 补充的网格可视化函数（修正版）
    // =======================================================
    /// <summary>
    /// 创建网格地图的 X 和 Z 坐标轴
    /// </summary>
    private void CreateCoordinateAxes()
    {
        // === 自定义变量 ===
        Material lineMaterial = gridMaterial; // 使用现有的网格材质
        float axisLengthFactor = 1.05f;      // 轴线长度略微超出地图
        float axisWidth = 0.3f;              // **修正：轴线宽度加粗**
        float axisHeight = 0.65f;            // 轴线高度，略高于地面和网格单元
        Color xColor = Color.HSVToRGB(20f / 360f, 0.55f, 0.8f);            // X 轴颜色
        Color zColor = Color.HSVToRGB(190f / 360f, 0.55f, 0.8f);

        if (lineMaterial == null)
        {
            Debug.LogWarning("未设置网格材质 (gridMaterial)！无法绘制坐标轴。");
            return;
        }
        
        // 计算地图的真实尺寸 (世界单位)
        float mapSizeX = mapWidth * gridCellSize;
        float mapSizeZ = mapLength * gridCellSize;
        
        // 地图中心的世界坐标
        Vector3 mapCenter = new Vector3(0, 0, 0);

        // 轴线起点：地图的左下角 (X: 最小, Z: 最小)
        Vector3 axisStart = new Vector3(
            mapCenter.x - mapSizeX / 2f, 
            axisHeight, 
            mapCenter.z - mapSizeZ / 2f
        );

        // --- X 轴 (宽度方向) ---
        Vector3 xAxisEnd = new Vector3(axisStart.x + mapSizeX * axisLengthFactor, axisHeight, axisStart.z);
        CreateLine(axisStart, xAxisEnd, "X_Axis", xColor, lineMaterial, axisWidth); 
        // **新增：创建 X 轴箭头**
        CreateArrowHead(xAxisEnd, xColor, lineMaterial, Quaternion.Euler(0, 0, 0));


        // --- Z 轴 (长度方向) ---
        Vector3 zAxisEnd = new Vector3(axisStart.x, axisHeight, axisStart.z + mapSizeZ * axisLengthFactor);
        CreateLine(axisStart, zAxisEnd, "Z_Axis", zColor, lineMaterial, axisWidth); 
        // **新增：创建 Z 轴箭头**
        CreateArrowHead(zAxisEnd, zColor, lineMaterial, Quaternion.Euler(0, 90, 0));
    }

    /// <summary>
    /// 给坐标轴添加刻度线（数字标签）
    /// </summary>
    private void CreateGridCoordinateLabels()
    {
        // === 自定义变量 ===
        float labelFontSize = 4f;           // 字体大小
        float labelHeight = 0.7f;           // 标签高度
        float labelOffset = 0.5f;           // 标签相对于地图边缘的偏移量 (世界单位)

        // 计算地图的真实尺寸 (世界单位)
        float mapSizeX = mapWidth * gridCellSize;
        float mapSizeZ = mapLength * gridCellSize;
        
        // 地图中心的世界坐标
        Vector3 mapCenter = new Vector3(0, 0, 0);

        // 地图左下角的最小世界坐标
        float minWorldX = mapCenter.x - mapSizeX / 2f;
        float minWorldZ = mapCenter.z - mapSizeZ / 2f;
        
        // --- X 轴标签 (宽度) ---
        for (int x = 0; x < mapWidth; x += 1) 
        {
            // 网格单元中心的X世界坐标
            float worldX = GridToWorldPosition(x, 0, GetGridSize()).x;
            
            // 标签位置：Z轴位于地图边缘外侧
            Vector3 labelPos = new Vector3(worldX, labelHeight, minWorldZ - labelOffset);

            // 标签面向摄像机，但由于是俯视，保持90度仰角
            Quaternion rotation = Quaternion.Euler(90, 0, 0); 
            CreateCoordinateLabel(labelPos, x.ToString(), rotation, labelFontSize, Color.black);
        }

        // --- Z 轴标签 (长度) ---
        for (int z = 0; z < mapLength; z += 1) 
        {
            // 网格单元中心的Z世界坐标
            float worldZ = GridToWorldPosition(0, z, GetGridSize()).z;

            // 标签位置：X轴位于地图边缘外侧
            Vector3 labelPos = new Vector3(minWorldX - labelOffset, labelHeight, worldZ);

            // 标签面向地图
            Quaternion rotation = Quaternion.Euler(90, 90, 0); 
            CreateCoordinateLabel(labelPos, z.ToString(), rotation, labelFontSize, Color.black);
        }
    }

    // 辅助函数：创建 LineRenderer 的线
    private void CreateLine(Vector3 start, Vector3 end, string name, Color color, Material material, float width)
    {
        GameObject lineObj = new GameObject(name);
        lineObj.transform.SetParent(gridVisualization.transform); 
        allGeneratedObjects.Add(lineObj); 
        
        LineRenderer lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);
        // **修正：使用传入的宽度**
        lr.startWidth = width;
        lr.endWidth = width;
        
        // 确保材质被正确设置和实例化
        if (material != null)
        {
            Material lineMatInstance = new Material(material);
            lineMatInstance.color = color;
            lr.material = lineMatInstance;
        }

        lr.startColor = color;
        lr.endColor = color;
    }

    // 辅助函数：创建箭头头部（使用一个小的 Quad）
    private void CreateArrowHead(Vector3 position, Color color, Material material, Quaternion rotation)
    {
        // 创建一个 Quad 作为箭头
        GameObject arrowHead = GameObject.CreatePrimitive(PrimitiveType.Quad);
        arrowHead.name = "ArrowHead";
        arrowHead.transform.SetParent(gridVisualization.transform);
        allGeneratedObjects.Add(arrowHead);

        // 调整位置、大小和旋转
        arrowHead.transform.position = position;
        arrowHead.transform.rotation = rotation * Quaternion.Euler(0, 0, -45); // 旋转 45 度形成箭头形状
        arrowHead.transform.localScale = Vector3.one * 0.5f; // 箭头大小

        // 设置材质和颜色
        Renderer renderer = arrowHead.GetComponent<Renderer>();
        if (material != null)
        {
             Material arrowMatInstance = new Material(material);
             arrowMatInstance.color = color;
             renderer.material = arrowMatInstance;
        }
        
        // 移除 Collider，因为它只是一个装饰
        Destroy(arrowHead.GetComponent<Collider>());
    }

    // 辅助函数：创建TextMeshPro文本标签
    private void CreateCoordinateLabel(Vector3 position, string text, Quaternion rotation, float fontSize, Color color)
    {
        GameObject labelObj = new GameObject("Label_" + text);
        labelObj.transform.SetParent(gridVisualization.transform);
        labelObj.transform.position = position;
        labelObj.transform.rotation = rotation;
        
        allGeneratedObjects.Add(labelObj);

        // 使用 TextMeshPro 组件
        TextMeshPro textMesh = labelObj.AddComponent<TextMeshPro>();
        textMesh.text = text;
        // **确保字体大小和颜色**
        textMesh.fontSize = fontSize; 
        textMesh.color = color; 
        textMesh.alignment = TextAlignmentOptions.Center;

        // **关键：增加缩放以保证在世界空间中可见**
        // 使用一个较小的缩放值，因为 fontSize 已经很大
        textMesh.GetComponent<RectTransform>().localScale = Vector3.one * 0.1f; 
    }
}
