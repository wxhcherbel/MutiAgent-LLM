using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;


public class UIController : MonoBehaviour
{
    [Header("Main UI")]
    public Button createAgentButton; // 创建智能体按钮
    public Button manualControlButton; // 手动控制按钮
    public TMP_Dropdown agentSelectionDropdown; // 智能体选择下拉框

    [Header("References")]
    public AgentSpawner agentSpawner; // 智能体生成器
    public MLAgentsController currentlyControlledAgent; // 当前控制的智能体

    private IntelligentAgent[] allAgents; // 所有智能体

    void Start()
    {
        // 初始化UI事件
        createAgentButton.onClick.AddListener(OnCreateAgentClicked);
        manualControlButton.onClick.AddListener(OnManualControlClicked);
        agentSelectionDropdown.onValueChanged.AddListener(OnAgentSelected);

        // 初始刷新
        RefreshAgentList();
    }

    /// <summary>
    /// 创建智能体按钮点击事件
    /// </summary>
    private void OnCreateAgentClicked()
    {
        agentSpawner.OpenSpawnPanel();
    }

    /// <summary>
    /// 手动控制按钮点击事件
    /// </summary>
    private void OnManualControlClicked()
    {
    }

    /// <summary>
    /// 智能体选择事件
    /// </summary>
    private void OnAgentSelected(int index)
    {
        if (index >= 0 && index < allAgents.Length)
        {
            // 可以在这里实现镜头跟随等功能
            Debug.Log($"Selected agent: {allAgents[index].Properties.AgentID}");
        }
    }

    /// <summary>
    /// 刷新智能体列表
    /// </summary>
    public void RefreshAgentList()
    {
        allAgents = FindObjectsOfType<IntelligentAgent>();
        agentSelectionDropdown.ClearOptions();

        List<string> options = new List<string>();
        foreach (var agent in allAgents)
        {
            options.Add($"{agent.Properties.AgentID} ({agent.Properties.Type})");
        }

        agentSelectionDropdown.AddOptions(options);
    }

    /// <summary>
    /// 当新智能体生成时调用
    /// </summary>
    public void OnAgentsSpawned()
    {
        RefreshAgentList();
    }
}