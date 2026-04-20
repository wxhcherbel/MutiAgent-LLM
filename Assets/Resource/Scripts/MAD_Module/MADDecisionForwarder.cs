// MAD_Module/MADDecisionForwarder.cs
// MAD 决策转发器：接收 MADGateway 传来的 AgentDirective，
// 根据 targetModule 路由到 PlanningModule 或 ActionDecisionModule，
// 并在路由方法内部解析 payload、调用目标模块的最小接口执行具体操作。
//
// 目标模块（targetModule）：
//   "planning" → HandlePlanning（强制指派槽位 / 插入步骤 / 重启任务 / 软重规划）
//   "adm"      → HandleAdm（插入原子动作）
//
// 设计原则：
//   - 枚举模块（有界稳定），不枚举紧急事件类型（无界）
//   - 仲裁 LLM 在输出时直接指定 targetModule，代码不做事件类型判断
//   - 其他模块（PlanningModule / ActionDecisionModule）只暴露最小 getter/setter
// ═══════════════════════════════════════════════════════════════════════════
using UnityEngine;
using System;
using System.Linq;
using Newtonsoft.Json;

public class MADDecisionForwarder : MonoBehaviour
{
    // ─── 模块引用（Start() 中从同 GameObject 获取）────────────────────────────

    private PlanningModule       _planning;
    private ActionDecisionModule _adm;
    private AgentProperties      _props;

    // ─────────────────────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        _planning = GetComponent<PlanningModule>();
        _adm      = GetComponent<ActionDecisionModule>();
        _props    = GetComponent<IntelligentAgent>()?.Properties;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 公开接口
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MAD 决策分发入口，由 MADGateway.OnIncidentResolved 调用。
    /// 根据 directive.targetModule 路由到对应模块的处理方法。
    /// </summary>
    /// <param name="directive">仲裁 LLM 输出的单条 Agent 指令，含 targetModule 和 payload。</param>
    public void Forward(AgentDirective directive)
    {
        if (directive == null) return;

        string agentId = _props?.AgentID ?? "unknown";
        Debug.Log($"[MADForwarder] {agentId} 收到指令: targetModule={directive.targetModule}, " +
                  $"instruction={directive.instruction}");

        switch (directive.targetModule?.ToLower())
        {
            case "planning":
                HandlePlanning(directive);
                break;
            case "adm":
                HandleAdm(directive);
                break;
            default:
                Debug.LogWarning($"[MADForwarder] {agentId} 未知 targetModule: {directive.targetModule}，" +
                                 $"跳过执行（instruction={directive.instruction}）");
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 规划模块处理（targetModule="planning"）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 路由到规划模块的处理逻辑。
    /// 解析 payload 中的 operation 字段，调用 PlanningModule 对应的最小接口。
    /// <para>支持的 operation：</para>
    /// <list type="bullet">
    ///   <item><term>force_slot</term><description>强制指派槽位，跳过选槽协议（用于 MAD 仲裁 slot 冲突）</description></item>
    ///   <item><term>insert_steps</term><description>向当前计划插入步骤，支持立即插入或追加到末尾（用于任务继承）</description></item>
    ///   <item><term>new_mission</term><description>放弃当前任务，以新任务描述重启 LLM#1 完整规划流程</description></item>
    ///   <item><term>request_replan</term><description>软重规划：重置为 Idle，由 IntelligentAgent 自然触发下一轮规划</description></item>
    /// </list>
    /// </summary>
    private void HandlePlanning(AgentDirective directive)
    {
        if (_planning == null)
        {
            Debug.LogWarning($"[MADForwarder] {_props?.AgentID} 未找到 PlanningModule，跳过 planning 指令");
            return;
        }

        PlanningPayload p = ParsePayload<PlanningPayload>(directive.payload);
        if (p == null) return;

        string op = p.operation?.ToLower();
        switch (op)
        {
            // ── 强制指派槽位（slot 冲突仲裁）──────────────────────────────────────
            case "force_slot":
                if (p.slot == null)
                {
                    Debug.LogWarning($"[MADForwarder] force_slot 缺少 slot 数据，跳过");
                    return;
                }
                Debug.Log($"[MADForwarder] {_props?.AgentID} force_slot → {p.slot.slotId}");
                _planning.ForceAssignSlot(p.slot);
                break;

            // ── 插入步骤（任务继承 / 追加额外步骤）─────────────────────────────────
            case "insert_steps":
                PlanStep[] steps = ResolveSteps(p);
                if (steps == null || steps.Length == 0)
                {
                    Debug.LogWarning($"[MADForwarder] insert_steps 未取得有效步骤，跳过");
                    return;
                }
                bool immediate = string.Equals(p.insertMode, "immediate", StringComparison.OrdinalIgnoreCase);
                Debug.Log($"[MADForwarder] {_props?.AgentID} insert_steps {steps.Length} 个步骤, " +
                          $"immediate={immediate}");
                _planning.InsertSteps(steps, immediate);
                break;

            // ── 重启完整规划流程（放弃当前任务）──────────────────────────────────
            case "new_mission":
                if (string.IsNullOrWhiteSpace(p.missionDescription))
                {
                    Debug.LogWarning($"[MADForwarder] new_mission 缺少 missionDescription，跳过");
                    return;
                }
                int agentCount = p.agentCount > 0 ? p.agentCount : 1;
                Debug.Log($"[MADForwarder] {_props?.AgentID} new_mission → \"{p.missionDescription}\"");
                _planning.ResetForNewMission();
                _planning.SubmitMissionRequest(p.missionDescription, agentCount);
                break;

            // ── 软重规划（保持任务框架，重置步骤等待下一轮规划）──────────────────
            case "request_replan":
                string hint = string.IsNullOrWhiteSpace(p.replanHint)
                    ? directive.instruction
                    : p.replanHint;
                Debug.Log($"[MADForwarder] {_props?.AgentID} request_replan: {hint}");
                _planning.RequestReplan(hint);
                break;

            default:
                Debug.LogWarning($"[MADForwarder] {_props?.AgentID} 未知 planning operation: {op}");
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 动作决策模块处理（targetModule="adm"）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 路由到动作决策模块的处理逻辑。
    /// 解析 payload 中的 operation 字段，调用 ActionDecisionModule 对应的最小接口。
    /// <para>支持的 operation：</para>
    /// <list type="bullet">
    ///   <item><term>insert_actions</term><description>向当前动作队列插入原子动作，支持立即插入或追加到末尾</description></item>
    /// </list>
    /// </summary>
    private void HandleAdm(AgentDirective directive)
    {
        if (_adm == null)
        {
            Debug.LogWarning($"[MADForwarder] {_props?.AgentID} 未找到 ActionDecisionModule，跳过 adm 指令");
            return;
        }

        AdmPayload p = ParsePayload<AdmPayload>(directive.payload);
        if (p == null) return;

        string op = p.operation?.ToLower();
        switch (op)
        {
            // ── 插入原子动作（立即执行下一个动作 / 追加到队列末尾）──────────────
            case "insert_actions":
                if (p.actions == null || p.actions.Length == 0)
                {
                    Debug.LogWarning($"[MADForwarder] insert_actions 缺少 actions 数据，跳过");
                    return;
                }
                bool immediate = string.Equals(p.insertMode, "immediate", StringComparison.OrdinalIgnoreCase);
                Debug.Log($"[MADForwarder] {_props?.AgentID} insert_actions {p.actions.Length} 个动作, " +
                          $"immediate={immediate}");
                _adm.InsertActions(p.actions, immediate);
                break;

            default:
                Debug.LogWarning($"[MADForwarder] {_props?.AgentID} 未知 adm operation: {op}");
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有辅助
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析 JSON payload 字符串为目标类型。失败时返回 null 并打印 Warning。
    /// </summary>
    private T ParsePayload<T>(string payload) where T : class
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            Debug.LogWarning($"[MADForwarder] {_props?.AgentID} payload 为空");
            return null;
        }
        try
        {
            return JsonConvert.DeserializeObject<T>(payload);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MADForwarder] {_props?.AgentID} payload 解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析 insert_steps 操作的步骤来源：
    /// 若 payload 含 fromAgentId，则从 AgentPlanRegistry 查询源 agent 的剩余步骤；
    /// 否则直接使用 payload 中的 steps 字段。
    /// </summary>
    private PlanStep[] ResolveSteps(PlanningPayload p)
    {
        // 优先从源 agent 的剩余步骤继承（任务接管场景）
        if (!string.IsNullOrWhiteSpace(p.fromAgentId))
        {
            var inherited = AgentPlanRegistry.GetRemainingSteps(p.fromAgentId);
            if (inherited != null && inherited.Length > 0)
            {
                Debug.Log($"[MADForwarder] {_props?.AgentID} 从 {p.fromAgentId} 继承 " +
                          $"{inherited.Length} 个剩余步骤");
                return inherited;
            }
            Debug.LogWarning($"[MADForwarder] {_props?.AgentID} 未能从 {p.fromAgentId} 获取剩余步骤，" +
                             $"回退到 payload.steps");
        }
        return p.steps;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // payload 内部数据类（对应 LLM 输出的 JSON 字段）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>targetModule="planning" 时的 payload 结构。</summary>
    private class PlanningPayload
    {
        /// <summary>操作类型：force_slot / insert_steps / new_mission / request_replan</summary>
        public string     operation;

        /// <summary>force_slot：要强制指派的槽位对象。</summary>
        public PlanSlot   slot;

        /// <summary>insert_steps：要插入的步骤数组；也可从 fromAgentId 自动获取。</summary>
        public PlanStep[] steps;

        /// <summary>insert_steps：若非空，从该 agent 的剩余步骤继承（优先于 steps 字段）。</summary>
        public string     fromAgentId;

        /// <summary>insert_steps：插入模式，"immediate"=立即插入当前位置后，"append"=追加到末尾。</summary>
        public string     insertMode;

        /// <summary>new_mission：新任务自然语言描述。</summary>
        public string     missionDescription;

        /// <summary>new_mission：参与新任务的 agent 数量。</summary>
        public int        agentCount;

        /// <summary>request_replan：重规划方向提示（可选，为空则使用 directive.instruction）。</summary>
        public string     replanHint;
    }

    /// <summary>targetModule="adm" 时的 payload 结构。</summary>
    private class AdmPayload
    {
        /// <summary>操作类型：insert_actions</summary>
        public string         operation;

        /// <summary>要插入的原子动作数组。</summary>
        public AtomicAction[] actions;

        /// <summary>插入模式，"immediate"=立即插入当前位置后，"append"=追加到队列末尾。</summary>
        public string         insertMode;
    }
}
