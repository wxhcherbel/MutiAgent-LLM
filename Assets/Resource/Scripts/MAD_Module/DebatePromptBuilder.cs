// MAD_Module/DebatePromptBuilder.cs
// MAD 辩论 Prompt 构建工具类（静态）。
// 集中管理成员侧提案 Prompt 与 Arbiter 仲裁 Prompt 的构建逻辑，
// 供 DebateParticipant（成员侧）和 DebateCoordinator（Leader 仲裁）复用。
using System.Text;

public static class DebatePromptBuilder
{
    /// <summary>
    /// 构建成员侧辩论提案 Prompt（Proposer / Critic / Voter 通用）。
    /// </summary>
    public static string BuildMemberPrompt(DebateRoleAssignment assignment, AgentProperties agentProps)
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
        sb.AppendLine($"Agent ID：{agentProps?.AgentID}  Role：{agentProps?.Role}");
        sb.AppendLine($"辩论角色：{assignment.role}  当前轮次：{assignment.debateRound}");

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

        if (assignment.role == DebateRole.Voter)
            sb.AppendLine("  \"voteFor\": \"你支持的提案的 entryId（格式 dbt_xxx_rN_agentId）\"");
        else
            sb.AppendLine("  \"voteFor\": \"\"");

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// 构建 Arbiter 仲裁 Prompt（Leader 在轮数耗尽时使用）。
    /// </summary>
    public static string BuildArbiterPrompt(IncidentReport report, string entriesSummary, string[] memberIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是多智能体任务仲裁器，负责基于辩论记录做出最终决策。仅输出 JSON，不解释。");
        sb.AppendLine();
        sb.AppendLine("=== 紧急事件 ===");
        sb.AppendLine($"类型：{report.incidentType}");
        sb.AppendLine($"严重程度：{report.severity}");
        sb.AppendLine($"受影响 Agent：{report.affectedAgentId ?? "无"}");
        sb.AppendLine($"受影响任务：{report.affectedTaskId ?? "无"}");
        sb.AppendLine($"描述：{report.description}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(entriesSummary))
        {
            sb.AppendLine("=== 辩论记录 ===");
            sb.AppendLine(entriesSummary);
            sb.AppendLine();
        }

        sb.AppendLine("=== 可用成员 ===");
        if (memberIds != null)
            sb.AppendLine(string.Join(", ", memberIds));
        sb.AppendLine();

        sb.AppendLine("=== 输出格式（JSON） ===");
        sb.AppendLine("{");
        sb.AppendLine("  \"resolution\": \"具体可执行的最终决策（2-3句，包含执行主体和步骤）\",");
        sb.AppendLine("  \"assignedAgentId\": \"执行主体的 agentId（若无特定执行方则填空字符串）\"");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
