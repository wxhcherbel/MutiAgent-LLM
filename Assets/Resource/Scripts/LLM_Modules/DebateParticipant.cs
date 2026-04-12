// LLM_Modules/DebateParticipant.cs
// 个体智能体的 MAD（Multi-Agent Debate）参与模块。
//
// 职责分层说明：
//   IncidentCoordinator  — 群组级协调：广播角色、收集投票、收敛判断、最终仲裁
//   DebateParticipant    — 个体级参与：接收角色分配、调 LLM 生成提案、发回辩论条目
//
// 非 MonoBehaviour；由 ActionDecisionModule（MonoBehaviour）持有并在 Start() 中初始化。
// 需要协程时通过构造器传入的 owner（MonoBehaviour）启动。
// ═══════════════════════════════════════════════════════════════════════════════
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

/// <summary>
/// 封装单个 Agent 在 MAD 辩论中的全部参与行为，包括：
/// <list type="bullet">
///   <item>接收 IncidentAnnounce / DebateProposal / DebateResolved 消息</item>
///   <item>维护待处理辩论角色队列</item>
///   <item>在 Rolling Loop 的辩论窗口（Step 11.5）中调 LLM 生成并发送辩论条目</item>
/// </list>
/// </summary>
public class DebateParticipant
{
    // ─── 依赖（由 ActionDecisionModule 在初始化时注入）────────────────────────

    /// <summary>启动协程的宿主 MonoBehaviour（即 ActionDecisionModule 自身）。</summary>
    private readonly MonoBehaviour _owner;

    /// <summary>当前 Agent 的静态属性（AgentID、Role 等），用于构建 Prompt 和日志。</summary>
    private readonly AgentProperties _agentProps;

    /// <summary>通信模块，用于向 leader 发送 DebateUpdate 消息。</summary>
    private readonly CommunicationModule _comm;

    /// <summary>LLM 接口，用于调用辩论提案生成请求。</summary>
    private readonly LLMInterface _llm;

    // ─── 回调（与 ActionDecisionModule 解耦的接口）──────────────────────────

    /// <summary>
    /// 查询宿主 ADM 是否处于 Running 状态的回调。
    /// 用于 OnCriticalIncident 判断是否需要中断当前 action batch。
    /// </summary>
    private readonly Func<bool> _isAdmRunning;

    /// <summary>
    /// Critical 事件需要中断当前 action batch 时调用的回调。
    /// 实际执行 ADM 内部的 SetStatus(ADMStatus.BatchDone)。
    /// </summary>
    private readonly Action _onCriticalInterrupt;

    /// <summary>
    /// 辩论共识到达时的回调（由 OnDebateResolved 调用）。
    /// 实际执行 PlanningModule.RequestReplan() 等后续动作。
    /// 参数为收到的 DebateConsensusEntry。
    /// </summary>
    private readonly Action<DebateConsensusEntry> _onConsensusReceived;

    // ─── 内部状态 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 待处理的辩论角色分配，key = incidentId。
    /// AssignDebateRole() 写入，辩论窗口 ParticipateInActiveDebates() 消费后删除。
    /// </summary>
    private readonly Dictionary<string, PendingDebateInfo> _pendingDebateRoles
        = new Dictionary<string, PendingDebateInfo>();

    /// <summary>
    /// 最近收到的辩论共识，由 OnDebateResolved 写入。
    /// 当前仅供内部记录，业务后续动作通过 _onConsensusReceived 回调通知 ADM。
    /// </summary>
    private DebateConsensusEntry _latestConsensus;

    // ─── JSON 提取正则（与 ADM 各自独立，避免跨类静态依赖）────────────────

    /// <summary>匹配 Markdown 代码块内的 JSON 内容（```json ... ``` 或 ``` ... ```）。</summary>
    private static readonly Regex JsonBlockRe =
        new Regex(@"```(?:json)?\s*([\s\S]*?)```");

    // ─── 内部数据类 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 存储单条待处理辩论角色分配的包装类。
    /// Processed 标志防止同一分配被重复消费（在协程切换时保护并发安全）。
    /// </summary>
    private class PendingDebateInfo
    {
        /// <summary>群组广播来的角色分配数据（包含 incidentId、role、round、report 等）。</summary>
        public DebateRoleAssignment Assignment;

        /// <summary>是否已提交给 LLM 处理。true 后不再重复消费，等待删除。</summary>
        public bool Processed;
    }

    /// <summary>
    /// LLM 返回 JSON 的原始反序列化目标。
    /// 字段名与 Prompt 中约定的输出格式严格对应。
    /// </summary>
    [Serializable]
    private class DebateEntryRaw
    {
        /// <summary>Agent 的提案/批评/投票理由文本（2-3 句）。</summary>
        public string content;

        /// <summary>Agent 对自身提案的置信度，范围 [0, 1]。</summary>
        public float confidence;

        /// <summary>Voter 角色支持的提案 entryId；Proposer/Critic 为空字符串。</summary>
        public string voteFor;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 构造器
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 创建 DebateParticipant 实例。应在宿主 MonoBehaviour.Start() 中调用，
    /// 此时所有依赖组件已通过 GetComponent/FindObjectOfType 获取完毕。
    /// </summary>
    /// <param name="owner">宿主 MonoBehaviour，用于 StartCoroutine。</param>
    /// <param name="agentProps">当前 Agent 的静态属性。</param>
    /// <param name="comm">通信模块，用于发送 DebateUpdate 回复。</param>
    /// <param name="llm">LLM 接口，用于生成辩论提案。</param>
    /// <param name="isAdmRunning">查询 ADM 当前是否 Running 的委托。</param>
    /// <param name="onCriticalInterrupt">Critical 事件需中断 batch 时的委托。</param>
    /// <param name="onConsensusReceived">辩论共识到达时的委托（传入 DebateConsensusEntry）。</param>
    public DebateParticipant(
        MonoBehaviour owner,
        AgentProperties agentProps,
        CommunicationModule comm,
        LLMInterface llm,
        Func<bool> isAdmRunning,
        Action onCriticalInterrupt,
        Action<DebateConsensusEntry> onConsensusReceived)
    {
        _owner                = owner;
        _agentProps           = agentProps;
        _comm                 = comm;
        _llm                  = llm;
        _isAdmRunning         = isAdmRunning;
        _onCriticalInterrupt  = onCriticalInterrupt;
        _onConsensusReceived  = onConsensusReceived;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 公共接口（由 CommunicationModule 路由消息后调用）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 收到群组广播的 IncidentAnnounce 消息时调用。
    /// Critical 严重度：立即通过回调中断当前 action batch，无需等待辩论窗口。
    /// 其他严重度：仅记录日志，实际参与在下一个辩论窗口（Step 11.5）触发。
    /// </summary>
    /// <param name="report">事件报告，包含 incidentId、incidentType、severity 等。</param>
    public void OnIncidentAnnounced(IncidentReport report)
    {
        if (report == null) return;
        Debug.Log($"[Debate] {_agentProps?.AgentID} 收到事件宣告: " +
                  $"{report.incidentId} ({report.incidentType}/{report.severity})");

        // Critical 严重度走快速路径：立即中断当前 action batch，不等待辩论窗口
        if (report.severity == IncidentSeverity.Critical)
            OnCriticalIncident(report);
    }

    /// <summary>
    /// 收到群组广播的 DebateProposal 消息时调用（由 IncidentCoordinator 发出）。
    /// 将辩论角色分配加入待处理队列，同一事件的后续轮次分配会被忽略（去重）。
    /// 实际参与延迟到 Rolling Loop 的下一个辩论窗口（Step 11.5），避免打断正在执行的 action。
    /// </summary>
    /// <param name="assignment">角色分配数据，包含 incidentId、role、debateRound、existingEntriesSummary 等。</param>
    public void AssignDebateRole(DebateRoleAssignment assignment)
    {
        if (assignment == null || string.IsNullOrWhiteSpace(assignment.incidentId)) return;

        // 同一事件只保留首次分配，防止多轮广播导致重复处理
        if (!_pendingDebateRoles.ContainsKey(assignment.incidentId))
        {
            _pendingDebateRoles[assignment.incidentId] = new PendingDebateInfo
            {
                Assignment = assignment,
                Processed  = false,
            };
            Debug.Log($"[Debate] {_agentProps?.AgentID} 收到辩论角色: " +
                      $"{assignment.role} (incident={assignment.incidentId}, round={assignment.debateRound})");
        }
    }

    /// <summary>
    /// 收到群组广播的 DebateResolved 消息时调用（由 IncidentCoordinator 在 Phase 4 发出）。
    /// 缓存最终共识，并通过回调通知 ADM 触发重规划（仅当 missionScopeChanged 为 true 时）。
    /// </summary>
    /// <param name="consensus">辩论共识，包含 assignedAgentId、resolution、missionScopeChanged 等。</param>
    public void OnDebateResolved(DebateConsensusEntry consensus)
    {
        if (consensus == null) return;

        _latestConsensus = consensus;
        Debug.Log($"[Debate] {_agentProps?.AgentID} 收到辩论共识 {consensus.incidentId}: " +
                  $"assigned={consensus.assignedAgentId}, resolution={consensus.resolution}");

        // 将共识通知 ADM，由 ADM 决定是否触发 PlanningModule.RequestReplan
        _onConsensusReceived?.Invoke(consensus);
    }

    /// <summary>
    /// Critical 严重度快速路径：立即通过回调要求 ADM 中断当前 action batch。
    /// 非 Critical 事件不调用此方法，避免不必要的规划中断。
    /// 由 OnIncidentAnnounced 在判断到 Critical 时内部调用。
    /// </summary>
    /// <param name="report">事件报告，severity 必须为 Critical，否则直接返回。</param>
    public void OnCriticalIncident(IncidentReport report)
    {
        if (report == null || report.severity != IncidentSeverity.Critical) return;

        // 仅在 ADM 正在执行 action 时才中断，避免重复中断 Idle/Done 状态
        if (_isAdmRunning != null && _isAdmRunning())
        {
            Debug.LogWarning($"[Debate] {_agentProps?.AgentID} Critical 事件 {report.incidentId}，" +
                             $"通知 ADM 中断当前 action batch");
            _onCriticalInterrupt?.Invoke();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rolling Loop 集成接口（由 ActionDecisionModule.RunRollingLoop Step 11.5 调用）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 检查是否有尚未处理的辩论角色分配。
    /// 在 Rolling Loop 的 Step 11.5 处用于决定是否进入辩论参与协程。
    /// </summary>
    /// <returns>true 表示队列中存在至少一条未处理的分配。</returns>
    public bool HasPendingDebateRole() => _pendingDebateRoles.Count > 0;

    /// <summary>
    /// 辩论参与协程（Step 11.5 辩论窗口）。
    /// 每次调用最多处理一条待处理分配（1 次 LLM 调用），不阻塞超过一次 rolling 迭代。
    /// 处理流程：取队列首条 → 构建 Prompt → 调 LLM → 解析 JSON → 发 DebateUpdate 给 leader。
    /// </summary>
    public IEnumerator ParticipateInActiveDebates()
    {
        // 取队列中第一条未处理的辩论角色分配
        PendingDebateInfo pending = null;
        string pendingKey = null;
        foreach (var kvp in _pendingDebateRoles)
        {
            if (!kvp.Value.Processed)
            {
                pending    = kvp.Value;
                pendingKey = kvp.Key;
                break;
            }
        }
        if (pending == null) yield break;

        // 先标记为已处理，防止协程让出后被重复消费
        pending.Processed = true;
        var assignment = pending.Assignment;

        Debug.Log($"[Debate] {_agentProps?.AgentID} 参与辩论 {assignment.incidentId} " +
                  $"as {assignment.role} (round={assignment.debateRound})");

        // 构建包含事件描述、身份信息、已有提案摘要的辩论 Prompt
        string prompt = BuildDebatePrompt(assignment);

        // 调 LLM 生成辩论提案（JSON 模式，限制 token 避免超时）
        string llmResult = null;
        yield return _owner.StartCoroutine(_llm.SendRequest(
            new LLMRequestOptions
            {
                prompt         = prompt,
                maxTokens      = 350,
                enableJsonMode = true,
                callTag        = $"Debate_{assignment.incidentId}_r{assignment.debateRound}",
                agentId        = _agentProps?.AgentID,
            },
            r => llmResult = r));

        // LLM 返回后从队列移除（无论成功失败）
        _pendingDebateRoles.Remove(pendingKey);

        if (string.IsNullOrWhiteSpace(llmResult))
        {
            Debug.LogWarning($"[Debate] {_agentProps?.AgentID} 辩论 LLM 返回空，本轮跳过");
            yield break;
        }

        // 解析 JSON，构造 DebateEntry，发送给 leader
        DebateEntry entry = ParseDebateEntry(assignment, llmResult);
        if (entry != null && _comm != null)
        {
            _comm.SendStructuredMessage(assignment.leaderId, MessageType.DebateUpdate, entry);
            Debug.Log($"[Debate] {_agentProps?.AgentID} 辩论回复已发送: " +
                      $"{entry.entryId} (conf={entry.confidence:F2})");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有：Prompt 构建
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 根据辩论角色分配构建发送给 LLM 的辩论提案 Prompt。
    /// Prompt 包含：事件描述、当前 Agent 身份与辩论角色、已有提案摘要（第 2 轮起）、输出 JSON 格式要求。
    /// </summary>
    /// <param name="assignment">本轮辩论的角色分配，含事件报告和已有条目摘要。</param>
    /// <returns>完整的 Prompt 字符串，直接传入 LLMRequestOptions.prompt。</returns>
    private string BuildDebatePrompt(DebateRoleAssignment assignment)
    {
        var report = assignment.report;
        var sb = new StringBuilder();

        sb.AppendLine("你是多智能体协同系统中的辩论参与者。根据你的角色对紧急事件提出应对方案。仅输出 JSON，不解释。");
        sb.AppendLine();

        sb.AppendLine("=== 紧急事件 ===");
        sb.AppendLine($"类型：{report?.incidentType}  严重程度：{report?.severity}");
        sb.AppendLine($"受影响 Agent：{report?.affectedAgentId ?? "无"}");
        sb.AppendLine($"受影响任务：{report?.affectedTaskId ?? "无"}");
        sb.AppendLine($"描述：{report?.description}");
        sb.AppendLine();

        sb.AppendLine("=== 你的身份 ===");
        sb.AppendLine($"Agent ID：{_agentProps?.AgentID}  Role：{_agentProps?.Role}");
        sb.AppendLine($"辩论角色：{assignment.role}  当前轮次：{assignment.debateRound}");

        // 第 2 轮起附上已有提案摘要，供 Voter/Critic 参考
        if (!string.IsNullOrWhiteSpace(assignment.existingEntriesSummary))
        {
            sb.AppendLine();
            sb.AppendLine("=== 已有提案/批评 ===");
            sb.AppendLine(assignment.existingEntriesSummary);
        }

        sb.AppendLine();
        sb.AppendLine("=== 输出格式（JSON） ===");
        sb.AppendLine("{");
        sb.AppendLine("  \"content\": \"你的提案/批评/投票理由（2-3句，具体说明应对策略，包含执行主体 agentId）\",");
        sb.AppendLine("  \"confidence\": 0.0到1.0之间的浮点数,");

        // Voter 角色需要填写 voteFor，其他角色留空
        if (assignment.role == DebateRole.Voter)
            sb.AppendLine("  \"voteFor\": \"你支持的提案的 entryId（格式 dbt_xxx_rN_agentId）\"");
        else
            sb.AppendLine("  \"voteFor\": \"\"");

        sb.AppendLine("}");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有：LLM 结果解析
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 将 LLM 返回的原始文本解析为 DebateEntry 数据对象。
    /// 先用 ExtractJson 提取 JSON 片段，再反序列化为 DebateEntryRaw，最后组装为 DebateEntry。
    /// 解析失败时返回 null（不抛出异常），由调用方决定是否跳过本轮。
    /// </summary>
    /// <param name="assignment">本轮角色分配，用于填充 entryId、incidentId、role 等字段。</param>
    /// <param name="llmResult">LLM 返回的原始文本（可能包含 Markdown 代码块包裹）。</param>
    /// <returns>解析成功的 DebateEntry，或 null（解析失败）。</returns>
    private DebateEntry ParseDebateEntry(DebateRoleAssignment assignment, string llmResult)
    {
        try
        {
            string json   = ExtractJson(llmResult);
            var    parsed = JsonConvert.DeserializeObject<DebateEntryRaw>(json);
            if (parsed == null) return null;

            // entryId 格式：dbt_{incidentId}_r{round}_{agentId}，供 Voter 的 voteFor 字段引用
            string entryId = $"dbt_{assignment.incidentId}_r{assignment.debateRound}_{_agentProps?.AgentID}";

            return new DebateEntry
            {
                entryId     = entryId,
                incidentId  = assignment.incidentId,
                authorId    = _agentProps?.AgentID ?? string.Empty,
                debateRound = assignment.debateRound,
                role        = assignment.role,
                content     = parsed.content     ?? string.Empty,
                confidence  = Mathf.Clamp01(parsed.confidence),
                voteFor     = parsed.voteFor     ?? string.Empty,
                createdAt   = Time.time,
            };
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Debate] {_agentProps?.AgentID} 辩论条目解析失败: {e.Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有：工具方法
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 LLM 原始输出中提取 JSON 片段。
    /// 优先匹配 Markdown 代码块（```json ... ```），否则按首尾大括号/方括号截取。
    /// </summary>
    /// <param name="raw">LLM 返回的原始文本。</param>
    /// <returns>提取到的 JSON 字符串；无法提取时返回去除首尾空白的原文。</returns>
    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        // 优先匹配 ```json ... ``` 或 ``` ... ``` 代码块
        var m = JsonBlockRe.Match(raw);
        if (m.Success) return m.Groups[1].Value.Trim();

        // 退化处理：找最外层 { } 或 [ ]
        int start    = raw.IndexOf('{');
        int startArr = raw.IndexOf('[');
        if (startArr >= 0 && (start < 0 || startArr < start)) start = startArr;
        if (start >= 0)
        {
            char open  = raw[start];
            char close = open == '[' ? ']' : '}';
            int  end   = raw.LastIndexOf(close);
            if (end > start) return raw.Substring(start, end - start + 1);
        }

        return raw.Trim();
    }
}
