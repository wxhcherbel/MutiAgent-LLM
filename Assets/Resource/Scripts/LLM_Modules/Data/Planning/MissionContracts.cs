using System;

/// <summary>
/// LLM#1 输出的任务解析结果。
/// </summary>
[Serializable]
public class ParsedMission
{
    /// <summary>任务唯一 ID。</summary>
    public string msnId;

    /// <summary>任务关系类型文本，例如 Cooperation 或 Competition。</summary>
    public string relType;

    /// <summary>任务拆出的分组数量。</summary>
    public int groupCnt;

    /// <summary>每个分组的任务描述数组，下标与组序号一致。</summary>
    public string[] groupMsns;

    /// <summary>任务总时限，单位为秒，0 表示不限时。</summary>
    public float timeLimit;
}

/// <summary>
/// 广播给所有成员的分组信息。
/// </summary>
[Serializable]
public class GroupBootstrapPayload
{
    /// <summary>当前任务 ID。</summary>
    public string msnId;

    /// <summary>当前任务关系类型。</summary>
    public string relType;

    /// <summary>本次任务的全部分组定义。</summary>
    public GroupDef[] groups;
}

/// <summary>
/// 单个分组的成员与任务定义。
/// </summary>
[Serializable]
public class GroupDef
{
    /// <summary>组 ID，例如 g0、g1。</summary>
    public string groupId;

    /// <summary>该组要完成的任务文本。</summary>
    public string mission;

    /// <summary>该组组长的 AgentID。</summary>
    public string leaderId;

    /// <summary>该组全部成员的 AgentID 列表，包含组长。</summary>
    public string[] memberIds;
}

/// <summary>
/// 组长发送给组员的槽位广播消息。
/// </summary>
[Serializable]
public class SlotBroadcastPayload
{
    /// <summary>所属任务 ID。</summary>
    public string msnId;

    /// <summary>所属组 ID。</summary>
    public string groupId;

    /// <summary>广播该消息的组长 AgentID。</summary>
    public string leaderId;

    /// <summary>可供成员选择的计划槽列表。</summary>
    public PlanSlot[] slots;
}

/// <summary>
/// 成员向组长提交的选槽结果。
/// </summary>
[Serializable]
public class SlotSelectPayload
{
    /// <summary>所属任务 ID。</summary>
    public string msnId;

    /// <summary>发起选择的成员 AgentID。</summary>
    public string agentId;

    /// <summary>成员希望认领的槽位 ID。</summary>
    public string slotId;
}

/// <summary>
/// 组长一对一发送的槽位确认结果。
/// </summary>
[Serializable]
public class SlotConfirmPayload
{
    /// <summary>所属任务 ID。</summary>
    public string msnId;

    /// <summary>接收确认消息的成员 AgentID。</summary>
    public string agentId;

    /// <summary>最终分配给该成员的槽位定义。</summary>
    public PlanSlot slot;
}

/// <summary>
/// 组内开始步骤拆解的广播消息。
/// </summary>
[Serializable]
public class StartExecPayload
{
    /// <summary>所属任务 ID。</summary>
    public string msnId;

    /// <summary>所属组 ID。</summary>
    public string groupId;
}
