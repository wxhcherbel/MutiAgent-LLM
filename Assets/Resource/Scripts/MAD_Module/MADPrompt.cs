// MAD_Module/MADPrompt.cs
// 统一的 MAD 提示词构建器。
// 成员提示词（Round 1/2 共用）和仲裁提示词各一套，所有事件类型复用。
// 新增事件类型只需填好 description/context，无需修改任何提示词逻辑。
// thought 字段引导 LLM 显式推理；其中 suggestion 由记忆系统自动提炼为 Policy 记忆。

public static class MADPrompt
{
    // ─────────────────────────────────────────────────────────────────────────
    // 成员提示词（Round 1 和 Round 2 共用同一模板）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 构建成员 LLM 提示词。
    /// Round 1 时 query.round1Summary 为空，Round 2 时附带第一轮汇总，让 LLM 看到他人立场后修正。
    /// </summary>
    /// <param name="query">当前辩论查询（含 round 和 round1Summary）。</param>
    /// <param name="agentId">成员自身 ID。</param>
    /// <param name="role">成员职能描述（如 "侦察"、"运输"）。</param>
    /// <param name="currentTask">当前正在执行的任务描述（可为空）。</param>
    public static string BuildMemberPrompt(
        IncidentQuery query,
        string agentId,
        string role,
        string currentTask)
    {
        string contextSection = string.IsNullOrWhiteSpace(query.context)
            ? ""
            : "\n" + query.context;

        // Round 2 追加第一轮汇总，让成员看到他人立场后再作答——这是"辩论"的核心
        string round2Section = (query.round == 2 && !string.IsNullOrWhiteSpace(query.round1Summary))
            ? "\n【第一轮各成员建议（请参考后确认或调整你的立场）】\n" + query.round1Summary + "\n"
            : "";

        return
            "你参与一个多智能体协同系统的紧急协商。先在 thought 中完成推理，再给出建议。仅输出JSON。\n" +
            "\n" +
            "【当前问题】\n" +
            query.description + contextSection + "\n" +
            "\n" +
            "【你的当前状态】\n" +
            $"- 身份：{agentId}（职能：{role ?? "通用"}）\n" +
            $"- 当前执行任务：{currentTask ?? "无"}\n" +
            round2Section +
            "\n" +
            "输出格式（JSON）：\n" +
            "{\n" +
            "  \"thought\": {\n" +
            "    \"situation_analysis\": \"事件对团队的影响（1-2句）\",\n" +
            "    \"my_capability\": \"我当前能做什么/不能做什么（结合电量/任务）\",\n" +
            "    \"recommendation_reasoning\": \"为什么提出该建议，具体依据\",\n" +
            "    \"confidence\": \"高/中/低\",\n" +
            "    \"confidence_reason\": \"置信度原因\",\n" +
            "    \"suggestion\": \"可跨任务复用的协同决策原则（描述结构性条件而非当前细节；无新规律则填\\\"\\\"）\"\n" +
            "  },\n" +
            "  \"recommendation\": \"具体说明谁应该做什么，必须包含agentId\",\n" +
            "  \"confidence\": 0.0\n" +
            "}\n" +
            "1. thought 必填，按上方结构输出真实推理内容。\n" +
            "2. confidence（数值）与 thought.confidence（文字）保持一致：高≥0.7，中0.4-0.7，低<0.4。\n" +
            "3. recommendation 必须点名具体 agentId，不能只说\"某个成员\"。";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 仲裁提示词（Leader 最终决策）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 构建仲裁 LLM 提示词，输入两轮辩论摘要，输出 IncidentDecision JSON。
    /// </summary>
    /// <param name="description">事件描述（来自 IncidentReport）。</param>
    /// <param name="context">结构化补充信息（来自 IncidentReport）。</param>
    /// <param name="round1Summary">第一轮所有成员意见摘要。</param>
    /// <param name="round2Summary">第二轮所有成员意见摘要。</param>
    /// <param name="membersStatus">各成员当前状态（逗号分隔，供 LLM 参考）。</param>
    public static string BuildArbiterPrompt(
        string description,
        string context,
        string round1Summary,
        string round2Summary,
        string membersStatus)
    {
        string contextSection = string.IsNullOrWhiteSpace(context)
            ? ""
            : "\n" + context;

        return
            "你是多智能体系统的协调决策者。先在 thought 中完成推理，再给出最终决策。仅输出JSON。\n" +
            "\n" +
            "【问题】\n" +
            description + contextSection + "\n" +
            "\n" +
            "【两轮辩论记录】\n" +
            "第一轮（独立提案）：\n" +
            (string.IsNullOrWhiteSpace(round1Summary) ? "（无成员回复）" : round1Summary) + "\n" +
            "\n" +
            "第二轮（参考他人后的修正）：\n" +
            (string.IsNullOrWhiteSpace(round2Summary) ? "（无成员回复）" : round2Summary) + "\n" +
            "\n" +
            "【当前可用成员状态】\n" +
            (string.IsNullOrWhiteSpace(membersStatus) ? "（成员状态未知）" : membersStatus) + "\n" +
            "\n" +
            "输出格式（JSON）：\n" +
            "{\n" +
            "  \"thought\": {\n" +
            "    \"conflict_analysis\": \"两轮意见的分歧点与共识点（1-2句）\",\n" +
            "    \"decision_basis\": \"最终决策依据——综合哪些信息、为何选此方案\",\n" +
            "    \"expected_outcome\": \"预期效果及潜在风险\",\n" +
            "    \"confidence\": \"高/中/低\",\n" +
            "    \"confidence_reason\": \"置信度原因\",\n" +
            "    \"suggestion\": \"可跨任务复用的多智能体协调原则（描述结构性条件而非当前细节；无新规律则填\\\"\\\"）\"\n" +
            "  },\n" +
            "  \"summary\": \"一句话决策理由\",\n" +
            "  \"directives\": [/* 见下方格式说明 */]\n" +
            "}\n" +
            "\n" +
            "【directives 格式说明】每条指令必须含 agentId、instruction、targetModule、payload 四个字段。\n" +
            "instruction 用自然语言描述意图（供日志），payload 是 JSON 字符串，含 operation 及对应参数。\n" +
            "\n" +
            "targetModule=\"planning\" 时，payload 可选格式：\n" +
            "\n" +
            "① 插入步骤——接管某成员的剩余任务（immediate 立即接管，append 排队到最后）：\n" +
            "  payload: {\n" +
            "    \"operation\": \"insert_steps\",\n" +
            "    \"fromAgentId\": \"AgentA\",\n" +
            "    \"insertMode\": \"immediate\"\n" +
            "  }\n" +
            "\n" +
            "② 插入步骤——直接指定新步骤内容：\n" +
            "  payload: {\n" +
            "    \"operation\": \"insert_steps\",\n" +
            "    \"steps\": [\n" +
            "      {\n" +
            "        \"stepId\": \"s_mad_1\",\n" +
            "        \"text\": \"前往充电站\",\n" +
            "        \"targetName\": \"充电站\",\n" +
            "        \"doneCond\": \"充电完成\",\n" +
            "        \"constraintIds\": []\n" +
            "      }\n" +
            "    ],\n" +
            "    \"insertMode\": \"append\"\n" +
            "  }\n" +
            "\n" +
            "③ 重启完整规划（放弃当前任务，从 LLM#1 重新开始）：\n" +
            "  payload: {\n" +
            "    \"operation\": \"new_mission\",\n" +
            "    \"missionDescription\": \"重新规划：优先保障电量充足的成员完成核心任务\",\n" +
            "    \"agentCount\": 3\n" +
            "  }\n" +
            "\n" +
            "targetModule=\"adm\" 时，payload 可选格式：\n" +
            "\n" +
            "④ 插入原子动作（immediate 立即执行，append 排到当前批次末尾）：\n" +
            "  payload: {\n" +
            "    \"operation\": \"insert_actions\",\n" +
            "    \"actions\": [\n" +
            "      {\n" +
            "        \"actionId\": \"mad_act_1\",\n" +
            "        \"type\": \"Navigate\",\n" +
            "        \"targetName\": \"充电站\",\n" +
            "        \"duration\": 0,\n" +
            "        \"actionParams\": \"\",\n" +
            "        \"spatialHint\": \"\"\n" +
            "      }\n" +
            "    ],\n" +
            "    \"insertMode\": \"immediate\"\n" +
            "  }\n" +
            "\n" +
            "1. thought 必填，按上方结构输出真实推理内容。\n" +
            "2. directives 填需要操作的成员，targetModule 和 payload 必填。\n" +
            "3. payload 必须是合法的 JSON 字符串（双引号转义）。\n" +
            "4. 每种 operation 只需选择最符合当前情境的一种，不要组合多种 operation 到同一条 directive。";
    }
}
