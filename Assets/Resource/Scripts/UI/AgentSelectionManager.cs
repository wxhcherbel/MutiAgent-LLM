// AgentSelectionManager.cs
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

public class AgentSelectionManager : MonoBehaviour
{
    [Header("UI References")]
    public GameObject agentInfoPanel;
    public TextMeshProUGUI agentIdText;
    public TextMeshProUGUI agentTypeText;
    public TextMeshProUGUI agentRoleText;
    public TextMeshProUGUI agentStatusText;
    public TextMeshProUGUI agentCapabilitiesText;
    public TMP_InputField missionDescriptionInput;
    public TMP_InputField agentCountInput;
    public Button submitMissionButton;
    public Button closePanelButton;

    [Header("其他引用")]
    public Camera mainCamera;
    public LayerMask agentLayer;

    private IntelligentAgent selectedAgent;
    private PlanningModule selectedPlanningModule;

    void Start()
    {
        // 初始化UI事件
        submitMissionButton.onClick.AddListener(OnSubmitMissionClicked);
        closePanelButton.onClick.AddListener(OnClosePanelClicked);
        // agentLayer = LayerMask.NameToLayer("Agent");
        
        // 隐藏面板
        agentInfoPanel.SetActive(false);
        
        // 获取主相机
        if (mainCamera == null)
            mainCamera = Camera.main;
    }

    void Update()
    {
        // 检测鼠标点击选择智能体
        if (Input.GetMouseButtonDown(0))
        {
            TrySelectAgent();
        }
    }

    /// <summary>
    /// 尝试选择智能体
    /// </summary>
    private void TrySelectAgent()
    {
        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, Mathf.Infinity))
        {
            GameObject hitObject = hit.collider.gameObject;
            
            // 检查是否点击了智能体
            IntelligentAgent agent = hitObject.GetComponent<IntelligentAgent>();
            if (agent != null)
            {
                SelectAgent(agent);
                return;
            }

            // 如果智能体本身没有碰撞体，检查父对象
            agent = hitObject.GetComponentInParent<IntelligentAgent>();
            if (agent != null)
            {
                SelectAgent(agent);
            }
        }
    }

    /// <summary>
    /// 选择智能体并显示信息
    /// </summary>
    private void SelectAgent(IntelligentAgent agent)
    {
        selectedAgent = agent;
        selectedPlanningModule = agent.GetComponent<PlanningModule>();

        // 更新UI显示
        UpdateAgentInfoUI();

        // 显示面板
        agentInfoPanel.SetActive(true);
    }

    /// <summary>
    /// 更新智能体信息UI
    /// </summary>
    private void UpdateAgentInfoUI()
    {
        if (selectedAgent == null || selectedAgent.Properties == null) return;

        AgentProperties props = selectedAgent.Properties;

        agentIdText.text = $"ID: {props.AgentID}";
        agentTypeText.text = $"Type: {props.Type}";
        agentRoleText.text = $"Role: {props.Role}";
        agentStatusText.text = $"Status: {GetAgentStatus()}";
        
        // 显示能力信息
        agentCapabilitiesText.text = $"Capability:\n" +
                                   $"Speed: {props.MaxSpeed}m/s\n" +
                                   $"Perception: {props.PerceptionRange}m\n" +
                                   $"Communication: {props.CommunicationRange}m\n" +
                                   $"Battery: {props.BatteryCapacity}";

        // 重置输入框
        missionDescriptionInput.text = "";
        agentCountInput.text = "1";
    }

    /// <summary>
    /// 获取智能体状态
    /// </summary>
    private string GetAgentStatus()
    {
        if (selectedPlanningModule != null)
        {
            return selectedPlanningModule.HasActiveMission() ? "Tasking" : "Idle";
        }
        return "none";
    }

    /// <summary>
    /// 提交任务按钮点击事件
    /// </summary>
    private void OnSubmitMissionClicked()
    {
        if (selectedPlanningModule == null)
        {
            Debug.LogError("选中的智能体没有PlanningModule组件！");
            return;
        }

        string missionDescription = missionDescriptionInput.text.Trim();
        if (string.IsNullOrEmpty(missionDescription))
        {
            Debug.LogWarning("请输入任务描述！");
            return;
        }

        int agentCount = 1;
        if (!int.TryParse(agentCountInput.text, out agentCount) || agentCount <= 0)
        {
            Debug.LogWarning("请输入有效的智能体数量！");
            return;
        }

        // 调用PlanningModule的任务提交方法
        selectedPlanningModule.SubmitMissionRequest(missionDescription, agentCount);
        
        //Debug.Log($"智能体 {selectedAgent.Properties.AgentID} 作为协调者发布任务: {missionDescription}");

        // 可选：提交后关闭面板或清空输入框
        missionDescriptionInput.text = "";
        // agentInfoPanel.SetActive(false);
    }

    /// <summary>
    /// 关闭面板按钮点击事件
    /// </summary>
    private void OnClosePanelClicked()
    {
        agentInfoPanel.SetActive(false);
        selectedAgent = null;
        selectedPlanningModule = null;
    }

    /// <summary>
    /// 获取当前选中的智能体
    /// </summary>
    public IntelligentAgent GetSelectedAgent()
    {
        return selectedAgent;
    }

    /// <summary>
    /// 获取当前选中的PlanningModule
    /// </summary>
    public PlanningModule GetSelectedPlanningModule()
    {
        return selectedPlanningModule;
    }
}