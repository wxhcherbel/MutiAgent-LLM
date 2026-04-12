using System;

/// <summary>
/// AgentStateServer 的命令相关嵌套类型定义。
/// 单独拆出，避免和 HTTP 处理逻辑混在同一个文件中。
/// </summary>
public partial class AgentStateServer
{
    /// <summary>
    /// 后台线程投递到主线程的命令类型。
    /// </summary>
    private enum CommandType
    {
        SubmitTask,
        SetModel
    }

    /// <summary>
    /// 主线程命令队列中的单条命令。
    /// </summary>
    private struct PendingCommand
    {
        /// <summary>命令类型。</summary>
        public CommandType type;

        /// <summary>命令负载原始 JSON。</summary>
        public string payload;
    }

    /// <summary>
    /// 提交任务命令的负载。
    /// </summary>
    [Serializable]
    private class TaskPayload
    {
        /// <summary>任务描述文本。</summary>
        public string mission;

        /// <summary>参与智能体数量。</summary>
        public int agentCount = 1;
    }

    /// <summary>
    /// 设置模型命令的负载。
    /// </summary>
    [Serializable]
    private class ModelPayload
    {
        /// <summary>要切换到的模型名。</summary>
        public string model;
    }
}
