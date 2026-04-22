// MAD_Module/MADContracts.cs
// MAD（Multi-Agent Debate）模块全部数据契约。
// 新事件类型：在 IncidentTypes 加一行常量即可，MAD 内部逻辑零改动。
using System;
using Newtonsoft.Json;
using UnityEngine;

// ── 触发侧（Raise 的唯一参数）────────────────────────────────────────────────

/// <summary>
/// 任意 Agent 调用 MADGateway.Raise() 时传入的事件上报对象。
/// leader 侧 MADCoordinator 在收到后赋值 incidentId。
/// </summary>
[Serializable]
public class IncidentReport
{
    /// <summary>上报者 agentId。</summary>
    public string reporterId;

    /// <summary>事件类型字符串常量（见 IncidentTypes），新类型无需修改 MAD 逻辑。</summary>
    public string incidentType;

    /// <summary>true = 立即中断 ADM Rolling Loop；false = 下一轮窗口处理。</summary>
    public bool isCritical;

    /// <summary>"发生了什么" 1-2 句自然语言，直接注入 LLM Prompt。</summary>
    public string description;

    /// <summary>结构化补充信息（key=value 多行），由调用方按实际情况填写。</summary>
    public string context;

    /// <summary>由 MADCoordinator 在收到报告后统一赋值（如 "inc_001"）。调用方留空。</summary>
    public string incidentId;
}

// ── 已知事件类型常量（新增类型直接加行，MAD 内部零改动）─────────────────────

/// <summary>
/// 内置事件类型字符串常量。
/// 新事件只需在此加一个 public const string，MAD 协调逻辑无需任何修改。
/// </summary>
public static class IncidentTypes
{
    /// <summary>Agent 电量耗尽或硬件故障，无法继续执行任务。</summary>
    public const string AgentUnavailable = "AgentUnavailable";

    /// <summary>等待 C3 互斥锁超时，疑似死锁。</summary>
    public const string C3MutexTimeout = "C3MutexTimeout";
}

// ── Leader → Member 查询（替代旧 DebateRoleAssignment）────────────────────────

/// <summary>
/// Leader 向每位成员发送的辩论查询消息。
/// round=1 时 round1Summary 为空；round=2 时附带第一轮汇总供成员参考。
/// </summary>
[Serializable]
public class IncidentQuery
{
    public string incidentId;

    /// <summary>发送方 leaderId，成员用于将 MemberOpinion 回传。</summary>
    public string leaderId;

    public string description;
    public string context;

    /// <summary>1 = 独立提案轮；2 = 参考第一轮结果的修正轮。</summary>
    public int round;

    /// <summary>第一轮各成员建议的汇总文本，仅 round==2 时填入。</summary>
    public string round1Summary;
}

// ── Member → Leader 回复（替代旧 DebateEntry）────────────────────────────────

/// <summary>
/// 成员 LLM 生成的辩论意见，回传给 leader MADCoordinator。
/// </summary>
[Serializable]
public class MemberOpinion
{
    public string incidentId;
    public string agentId;

    /// <summary>对应的辩论轮次（1 或 2）。</summary>
    public int round;

    /// <summary>具体建议（必须包含执行主体 agentId）。</summary>
    public string recommendation;

    /// <summary>置信度，0-1。</summary>
    public float confidence;

    /// <summary>LLM 推理过程（JSON 字符串）。LLM 实际输出 JSON 对象，由 ThoughtJsonConverter 序列化为字符串。</summary>
    [JsonConverter(typeof(ThoughtJsonConverter))]
    public string thought;
}

// ── 仲裁决策（替代旧 DebateConsensusEntry）──────────────────────────────────

/// <summary>
/// Leader 经两轮辩论 + LLM 仲裁后输出的最终决策。
/// MADCoordinator 据此选择执行路径：重规划 or 逐人指令。
/// </summary>
[Serializable]
public class IncidentDecision
{
    public string incidentId;

    /// <summary>一句话决策摘要，用于日志和白板。</summary>
    public string summary;

    /// <summary>每个受影响 Agent 的具体行动指令。</summary>
    public AgentDirective[] directives;

    /// <summary>LLM 推理过程（JSON 字符串）。LLM 实际输出 JSON 对象，由 ThoughtJsonConverter 序列化为字符串。</summary>
    [JsonConverter(typeof(ThoughtJsonConverter))]
    public string thought;
}

/// <summary>
/// 针对单个 Agent 的具体行动指令（仲裁决策的原子单元）。
/// MADDecisionForwarder 根据 targetModule 和 payload 将指令路由到对应模块执行。
/// </summary>
[Serializable]
public class AgentDirective
{
    /// <summary>执行指令的 Agent ID。</summary>
    public string agentId;

    /// <summary>人类可读的指令描述，用于日志和记忆，始终存在。</summary>
    public string instruction;

    /// <summary>
    /// 目标模块标识，由仲裁 LLM 填写。
    /// 可选值："planning"（规划模块）、"adm"（动作决策模块）。
    /// MADDecisionForwarder 据此路由到对应模块执行逻辑。
    /// </summary>
    public string targetModule;

    /// <summary>
    /// JSON 字符串，含 operation 字段及该操作所需参数，由仲裁 LLM 填写。
    /// operation 可选值：
    ///   planning: "insert_steps"（插入步骤）| "new_mission"（重启 LLM#1）
    ///   adm:      "insert_actions"（插入原子动作）
    /// </summary>
    public string payload;
}
