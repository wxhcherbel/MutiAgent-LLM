using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// PersonalityProfile：人格档案数据类（大五人格模型 + 领域特化字段）
//
// 设计原则：
//   - 五个维度的值域均为 [0, 1]，方便在 Inspector 中配置，也方便做阈值判断
//   - 配置值在 Inspector 中填写；衍生字段（riskTolerance / cooperationBias）
//     由 PersonalitySystem.Initialize() 在运行时自动计算，不需要手动填写
//   - 此类只是纯数据，不继承 MonoBehaviour，可作为字段嵌在 PersonalitySystem 里
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 智能体人格档案（大五人格模型 + 领域特化）。
/// 在 Inspector 中配置初始值，运行时通过 PersonalitySystem 读取。
/// 人格不随任务改变，代表 agent 的稳定个体特征。
/// </summary>
[Serializable]
public class PersonalityProfile
{
    /// <summary>对应的智能体 ID，与 AgentProperties.AgentID 保持一致。</summary>
    public string agentId;

    // ── 大五人格维度 [0, 1] ──────────────────────────────────────────────────
    // 这五个维度来自心理学经典大五人格（Big Five / OCEAN）模型，
    // 在这里被映射为影响 LLM 决策偏好的语义信号。

    /// <summary>
    /// 开放性（Openness）[0, 1]。
    /// 高开放性（>0.7）：好奇心强，愿意尝试非常规路径，倾向选侦察/探索角色（Scout/Explorer）。
    /// 低开放性（<0.4）：保守，倾向走已验证路径，不主动尝试未知区域。
    /// 对记忆的影响：高开放性时 Observation 类记忆重要度×1.1，鼓励从环境学习。
    /// </summary>
    [Range(0f, 1f)] public float openness;

    /// <summary>
    /// 尽责性（Conscientiousness）[0, 1]。
    /// 高尽责性（>0.7）：纪律性强，严格按步骤系统化执行，倾向边界管控/监视角色（Perimeter/Guard）。
    /// 低尽责性（<0.4）：灵活随机应变，但可能跳步骤或遗漏细节。
    /// 对记忆的影响：高尽责性时 Plan 类记忆重要度×1.1，强化流程遵从。
    /// </summary>
    [Range(0f, 1f)] public float conscientiousness;

    /// <summary>
    /// 外向性（Extraversion）[0, 1]。
    /// 高外向性（>0.7）：主动协作，倾向占主导/协调角色（Coordinator/Leader），频繁广播状态。
    /// 低外向性（<0.4）：偏独立执行，减少主动通信，适合不需要频繁协调的任务。
    /// </summary>
    [Range(0f, 1f)] public float extraversion;

    /// <summary>
    /// 宜人性（Agreeableness）[0, 1]。
    /// 高宜人性（>0.7）：服从指令，优先配合队友需求，倾向支援角色（Supporter）。
    /// 低宜人性（<0.4）：自主判断优先于指令，可能出现与队友意见分歧时坚持己见。
    /// </summary>
    [Range(0f, 1f)] public float agreeableness;

    /// <summary>
    /// 神经质（Neuroticism）[0, 1]。
    /// 高神经质（>0.6）：对失败高度敏感，失败记忆重要度额外×1.2，反思触发阈值降低（150→100）。
    /// 低神经质（<0.4）：情绪稳定，失败后不过度反思，执行节奏稳定。
    /// </summary>
    [Range(0f, 1f)] public float neuroticism;

    // ── 领域特化描述字段 ──────────────────────────────────────────────────────
    // 这三个字段为自由文本数组，直接出现在 LLM prompt 中，
    // 补充大五维度无法覆盖的具体行为描述。

    /// <summary>
    /// 核心价值观（字符串数组，直接注入 prompt）。
    /// 示例：["安全第一", "团队优先", "效率"]。
    /// LLM 在做决策时会将这些价值观作为优先级排序依据。
    /// </summary>
    public string[] coreValues;

    /// <summary>
    /// 行为习惯（字符串数组，直接注入 prompt）。
    /// 示例：["先观察后行动", "优先保持队形", "按步骤执行任务"]。
    /// LLM 在生成 nextActions 时会倾向遵循这些习惯。
    /// </summary>
    public string[] habits;

    /// <summary>
    /// 厌恶/回避项（字符串数组，直接注入 prompt）。
    /// 示例：["冲突", "模糊指令", "独自行动"]。
    /// LLM 在选槽和行动时会尽量避免触发这些条件。
    /// </summary>
    public string[] dislikes;

    // ── 衍生倾向（运行时计算，不需要 Inspector 配置）────────────────────────
    // 由 PersonalitySystem.Initialize() 根据上方五个维度自动推算，
    // [HideInInspector] 防止 Inspector 中误填。

    /// <summary>
    /// 风险容忍度（衍生字段）[0, 1]：= openness×0.5 + (1-neuroticism)×0.5。
    /// 表示 agent 愿意接受不确定结果的程度，用于 ADM 决策偏好的语义描述。
    /// </summary>
    [HideInInspector] public float riskTolerance;

    /// <summary>
    /// 协作偏好（衍生字段）[0, 1]：= agreeableness×0.6 + extraversion×0.4。
    /// 表示 agent 优先考虑团队协调的程度，用于 LLM#3 选槽偏好的语义描述。
    /// </summary>
    [HideInInspector] public float cooperationBias;

    /// <summary>
    /// 阵营标记：true = 破坏/对抗型，false = 协作型。
    /// 由 AgentSpawner 在生成时根据 adversarialCount 赋值，不在 Inspector 手动配置。
    /// PerceptionModule 用此字段判断感知到的 agent 是友方还是敌方（IsAdversarial 不同即为敌方）。
    /// </summary>
    public bool isAdversarial;
}

// ─────────────────────────────────────────────────────────────────────────────
// PersonalitySystem：人格系统 MonoBehaviour
//
// 挂载位置：每个 agent GameObject 上（与 AgentProperties 同一对象）
// 职责：
//   1. 持有 PersonalityProfile，供其他模块读取
//   2. 提供面向 LLM prompt 的文本生成方法（GetRolePreferenceHint / GetDecisionStyleHint）
//   3. 提供面向 MemoryModule 的数值调整方法（GetImportanceModifier / GetPersonalityTag）
//   4. 提供面向 ReflectionModule 的触发判断（ShouldTriggerEarlyReflection）
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 智能体工作人格系统。
/// 为每个 agent 提供稳定的个性特征，影响：
///   - LLM#3 选槽偏好（GetRolePreferenceHint）
///   - ADM 行动决策风格（GetDecisionStyleHint）
///   - 记忆重要度权重（GetImportanceModifier）
///   - L2 反思触发阈值（ShouldTriggerEarlyReflection）
/// 人格不随任务改变，Phase 3 的长期反思可选实现缓慢漂移。
/// </summary>
public class PersonalitySystem : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Inspector 配置
    // ═══════════════════════════════════════════════════════════════════════════

    [Header("人格档案")]
    /// <summary>
    /// 该 agent 的人格档案。
    /// 五个大五维度在 Inspector 中填写初始值；coreValues / habits / dislikes
    /// 也在 Inspector 中以字符串数组填写，直接出现在 LLM prompt 里。
    /// </summary>
    public PersonalityProfile Profile = new PersonalityProfile();

    /// <summary>当前 agent 是否为破坏/对抗型人格（由 Profile.isAdversarial 决定）。</summary>
    public bool IsAdversarial => Profile.isAdversarial;

    // ═══════════════════════════════════════════════════════════════════════════
    // 生命周期
    // ═══════════════════════════════════════════════════════════════════════════

    private void Start()
    {
        Initialize();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 初始化
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 初始化人格系统：根据五个大五维度计算衍生倾向值（riskTolerance / cooperationBias）。
    /// 由 IntelligentAgent.InitializeAgent() 在 Start 阶段主动调用，也可通过 Inspector 按钮触发。
    /// </summary>
    public void Initialize()
    {
        // 风险容忍度 = 开放性（探索意愿）和稳定性（1-神经质）各占一半
        // 高开放 + 低神经质 → 愿意接受更多不确定性
        Profile.riskTolerance = Profile.openness * 0.5f + (1f - Profile.neuroticism) * 0.5f;

        // 协作偏好 = 宜人性（服从配合）占60% + 外向性（主动协作）占40%
        // 高宜人 + 高外向 → 强烈倾向团队优先
        Profile.cooperationBias = Profile.agreeableness * 0.6f + Profile.extraversion * 0.4f;

        Debug.Log($"[PersonalitySystem] {Profile.agentId} 初始化完成 | " +
                  $"riskTolerance={Profile.riskTolerance:F2} cooperationBias={Profile.cooperationBias:F2}");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LLM Prompt 注入方法
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 根据大五人格维度生成角色偏好提示字符串，注入 LLM#3（选槽）的 prompt。
    ///
    /// 逻辑：
    ///   尽责性>0.7  → 系统性/边界管控角色（如 Perimeter/Guard）
    ///   开放性>0.7  → 侦察/探索角色（如 Scout/Explorer）
    ///   外向性>0.7  → 协调/领导角色（如 Coordinator）
    ///   宜人性>0.7  → 支援角色，优先配合队友需求
    ///
    /// 期望效果：LLM 在 thought 字段中解释是否参考了此偏好，
    ///   当多个槽位的能力匹配度相近时，人格偏好成为决策的打破平局因素。
    /// </summary>
    /// <returns>
    ///   "[人格倾向] 倾向选择边界管控或系统性角色(如Perimeter/Guard)；..."
    ///   若无超过阈值的维度，返回空字符串（不向 prompt 添加噪声）。
    /// </returns>
    public string GetRolePreferenceHint()
    {
        var hints = new List<string>();

        // 尽责性高 → 规则导向、系统性执行 → 适合需要严格遵守边界的角色
        if (Profile.conscientiousness > 0.7f)
            hints.Add("倾向选择边界管控或系统性角色(如Perimeter/Guard)");

        // 开放性高 → 好奇心驱动 → 适合主动探索、信息收集的角色
        if (Profile.openness > 0.7f)
            hints.Add("倾向选择侦察或探索角色(如Scout/Explorer)");

        // 外向性高 → 主动协调 → 适合需要频繁与队友沟通的领导/协调角色
        if (Profile.extraversion > 0.7f)
            hints.Add("倾向选择协调或领导角色(如Coordinator)");

        // 宜人性高 → 配合他人 → 适合根据队友需求灵活调整的支援角色
        if (Profile.agreeableness > 0.7f)
            hints.Add("倾向选择支援角色，优先配合队友需求");

        if (hints.Count == 0)
            return string.Empty;   // 没有强烈偏好时不注入，避免弱信号干扰选槽决策

        return "[人格倾向] " + string.Join("；", hints);
    }

    /// <summary>
    /// 根据大五人格维度生成行动决策风格描述，注入 ADM Rolling Loop 的每次 prompt。
    ///
    /// 逻辑：
    ///   神经质>0.7  → "谨慎行事，优先规避风险，遇不确定情况选保守方案"
    ///   开放性>0.7  → "可尝试非常规路径，允许实验性动作"
    ///   尽责性>0.7  → "按步骤系统化执行，不跳步骤，完成后立即汇报"
    ///   协作偏好>0.6 → "优先与队友协调，主动广播状态"（由衍生字段判断）
    ///
    /// 期望效果：LLM 在生成 nextActions 时会考虑这些风格描述，
    ///   例如高神经质的 agent 会优先选择 Observe 而不是直接 MoveTo 未知区域。
    /// </summary>
    /// <returns>
    ///   行动风格描述字符串（多条用换行分隔），或空字符串。
    /// </returns>
    public string GetDecisionStyleHint()
    {
        var hints = new List<string>();

        // 神经质高 → 失败敏感 → LLM 决策时优先降低风险，保守处理不确定情况
        if (Profile.neuroticism > 0.7f)
            hints.Add("谨慎行事，优先规避风险，遇不确定情况选保守方案");

        // 神经质低 → 情绪稳定 → 可以接受失败并快速继续，不反复犹豫
        else if (Profile.neuroticism < 0.3f)
            hints.Add("执行稳健，失败后迅速调整继续推进，不过度纠结单次失败");

        // 开放性高 → 探索意愿强 → LLM 可以生成更有创意的非标准动作序列
        if (Profile.openness > 0.7f)
            hints.Add("可尝试非常规路径，允许在标准流程之外寻找更优方案");

        // 尽责性高 → 系统化执行 → LLM 应严格按步骤来，不跳过确认动作
        if (Profile.conscientiousness > 0.7f)
            hints.Add("按步骤系统化执行，每步完成后确认状态再推进下一步");

        // 协作偏好高（宜人+外向的综合值） → 主动与队友同步，通过 Signal/Whiteboard 广播状态
        if (Profile.cooperationBias > 0.6f)
            hints.Add("优先与队友协调，主动通过信号广播自身状态和意图");

        if (hints.Count == 0)
            return string.Empty;

        return string.Join("\n", hints);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MemoryModule 支持方法
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 根据人格调整记忆重要度的乘数系数，在 MemoryModule.StoreMemory 中被调用。
    ///
    /// 逻辑：
    ///   高神经质（>0.6）对 Decision 类记忆（失败决策）额外×1.2
    ///     → 失败经验被更重视，更不容易被遗忘
    ///   高尽责性（>0.7）对 Plan 类记忆额外×1.1
    ///     → 流程/计划记忆更稳固，强化按计划执行的倾向
    ///   高开放性（>0.7）对 Observation 类记忆额外×1.1
    ///     → 环境观察记忆更稳固，鼓励从环境变化中学习
    ///   其余情况返回 1.0f（不修改原始 importance）
    ///
    /// 注意：乘数作用于 StoreMemory 中已规范化的 importance 值，
    ///   之后再经 Mathf.Clamp01 保证结果不超过 1.0。
    /// </summary>
    /// <param name="kind">记忆类型，决定使用哪条人格-记忆映射规则。</param>
    /// <returns>重要度乘数，1.0f 表示不修改，>1.0f 表示增强。</returns>
    public float GetImportanceModifier(AgentMemoryKind kind)
    {
        return kind switch
        {
            // 高神经质 → 对失败决策高度敏感 → 加大失败记忆权重，帮助 agent 避免重蹈覆辙
            AgentMemoryKind.Decision when Profile.neuroticism > 0.6f => 1.2f,

            // 高尽责性 → 严格遵循计划 → 强化计划类记忆，让 LLM 在检索时更容易找到历史计划
            AgentMemoryKind.Plan when Profile.conscientiousness > 0.7f => 1.1f,

            // 高开放性 → 重视环境信息 → 强化观察类记忆，推动 agent 积累环境知识
            AgentMemoryKind.Observation when Profile.openness > 0.7f => 1.1f,

            // 默认：不修改原始重要度，保持中性
            _ => 1.0f
        };
    }

    /// <summary>
    /// 生成人格标注字符串，附加到 Memory.personalityContext 字段。
    ///
    /// 只标注明显偏高（>0.7）的维度，避免输出冗长的全量描述。
    /// 标注格式：[维度名称-数值]，例如 "[高尽责性-0.85][高宜人性-0.70]"。
    ///
    /// 用途：
    ///   ReflectionModule 在做 L2/L3 反思时，可以看到这条记忆是"在什么人格状态下产生的"，
    ///   从而识别"尽责性高的 agent 在下午担任 Perimeter 角色成功率高"这类人格-场景关联规律。
    /// </summary>
    /// <returns>人格标注字符串，若无突出维度则返回空字符串。</returns>
    public string GetPersonalityTag()
    {
        var tags = new List<string>();

        if (Profile.openness          > 0.7f) tags.Add($"[高开放性-{Profile.openness:F2}]");
        if (Profile.conscientiousness > 0.7f) tags.Add($"[高尽责性-{Profile.conscientiousness:F2}]");
        if (Profile.extraversion      > 0.7f) tags.Add($"[高外向性-{Profile.extraversion:F2}]");
        if (Profile.agreeableness     > 0.7f) tags.Add($"[高宜人性-{Profile.agreeableness:F2}]");
        if (Profile.neuroticism       > 0.7f) tags.Add($"[高神经质-{Profile.neuroticism:F2}]");

        return string.Join("", tags);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ReflectionModule 支持方法
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 判断是否应该降低 L2 反思触发阈值（让 agent 更频繁地反思）。
    ///
    /// 高神经质（>0.7）的 agent 对失败更敏感，应更积极地从失败中总结经验。
    /// 触发时 ReflectionModule 会将 MemoryModule.reflectionImportanceThreshold 从 150 降至 100。
    /// 这意味着每积累约10条高权重记忆就会触发一次 L2 反思（而非默认的15条）。
    ///
    /// 注意：更频繁的反思意味着更多的 LLM 调用（token 消耗增加），
    ///   这本身也是人格特征的体现——高神经质者的"过度思考"倾向。
    /// </summary>
    /// <returns>true 表示应降低阈值，false 表示使用默认阈值。</returns>
    public bool ShouldTriggerEarlyReflection()
    {
        // 神经质>0.7 才触发，避免对中等神经质（如0.6）也降低阈值
        return Profile.neuroticism > 0.7f;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 内存检索辅助方法
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 根据人格维度返回偏好的角色名列表，可追加到 MemoryQuery.preferredRoles。
    ///
    /// 用途：在 BuildPlanningContext 构建检索请求时，将人格偏好的角色名加入 preferredRoles，
    ///   使历史上担任这些角色的记忆在检索时获得额外加权（由 MemoryModule.ScoreMemory 处理）。
    /// </summary>
    /// <returns>偏好角色名称列表，可能为空（无突出维度时）。</returns>
    public string[] GetPreferredRoles()
    {
        var roles = new List<string>();

        if (Profile.conscientiousness > 0.7f) { roles.Add("Perimeter"); roles.Add("Guard"); }
        if (Profile.openness          > 0.7f) { roles.Add("Scout");     roles.Add("Explorer"); }
        if (Profile.extraversion      > 0.7f) { roles.Add("Coordinator"); }
        if (Profile.agreeableness     > 0.7f) { roles.Add("Supporter"); }

        return roles.ToArray();
    }

    /// <summary>
    /// 生成完整人格描述段落，供 Phase 3 自主目标生成（AutonomousGoalGenerator）使用，
    /// 也可用于 LLM#3 的完整版人格注入（比 GetRolePreferenceHint 更详细）。
    ///
    /// 包含：大五维度数值、核心价值观、行为习惯、厌恶项、衍生倾向。
    /// </summary>
    /// <returns>多行人格描述字符串。</returns>
    public string GetFullPersonalityContext()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[人格档案]");
        sb.AppendLine($"  开放性={Profile.openness:F2}  尽责性={Profile.conscientiousness:F2}  " +
                      $"外向性={Profile.extraversion:F2}  宜人性={Profile.agreeableness:F2}  " +
                      $"神经质={Profile.neuroticism:F2}");

        if (Profile.coreValues != null && Profile.coreValues.Length > 0)
            sb.AppendLine($"  核心价值观：{string.Join("、", Profile.coreValues)}");

        if (Profile.habits != null && Profile.habits.Length > 0)
            sb.AppendLine($"  行为习惯：{string.Join("、", Profile.habits)}");

        if (Profile.dislikes != null && Profile.dislikes.Length > 0)
            sb.AppendLine($"  回避项：{string.Join("、", Profile.dislikes)}");

        sb.AppendLine($"  风险容忍度={Profile.riskTolerance:F2}  协作偏好={Profile.cooperationBias:F2}");
        return sb.ToString().TrimEnd();
    }
}
