using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>将 CampusGrid2D 地图数据序列化为 LLM 可读文本。</summary>
public static class MapTopologySerializer
{
    private static readonly string[] CompassLabels = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

    // ═══════════════════════════════════════════════════════════════════════════
    // 兼容接口：保留原签名，内部改为调用 GetStrategicMap（不再含拥挤标注）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 生成以智能体为中心的局部地图文本，可选标注趋向目标的中间航点。
    /// 兼容接口：内部调用 GetStrategicMap()，不再包含 SmallNodeRegistry 拥挤标注。
    /// </summary>
    public static string GetAgentRelativeMap(
        CampusGrid2D grid,
        Vector3 agentWorldPos,
        Vector3? targetWorldPos = null,
        float radius = 300f)
    {
        return GetStrategicMap(grid, agentWorldPos, targetWorldPos, radius);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 战略地图：仅建筑/地标拓扑，不依赖 SmallNodeRegistry
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 生成纯战略地图（建筑、地标的方向/距离/类型），不含小节点拥挤标注。
    /// 用于 LLM 宏观决策时了解周边地理拓扑。
    /// </summary>
    public static string GetStrategicMap(
        CampusGrid2D grid,
        Vector3 agentWorldPos,
        Vector3? targetWorldPos = null,
        float radius = 300f)
    {
        if (grid == null || grid.featureSpatialProfileByUid == null)
            return "(地图数据不可用)";

        // ── 1. 收集半径内要素（最多 20 个，按距离升序） ──────────────
        var nearby = new List<(FeatureSpatialProfile profile, float dist)>();
        foreach (var kv in grid.featureSpatialProfileByUid)
        {
            var p = kv.Value;
            if (p == null) continue;
            float dx = p.centroidWorld.x - agentWorldPos.x;
            float dz = p.centroidWorld.z - agentWorldPos.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist <= radius)
                nearby.Add((p, dist));
        }
        nearby.Sort((a, b) => a.dist.CompareTo(b.dist));
        if (nearby.Count > 20) nearby = nearby.GetRange(0, 20);

        // ── 2. 预计算目标方向向量（XZ 平面单位向量） ─────────────────
        bool hasTarget = targetWorldPos.HasValue;
        Vector2 toTargetDir = Vector2.zero;
        float targetDist = 0f;
        bool targetOutOfRange = false;

        if (hasTarget)
        {
            float tdx = targetWorldPos.Value.x - agentWorldPos.x;
            float tdz = targetWorldPos.Value.z - agentWorldPos.z;
            targetDist = Mathf.Sqrt(tdx * tdx + tdz * tdz);
            if (targetDist > 0.01f)
                toTargetDir = new Vector2(tdx, tdz).normalized;
            targetOutOfRange = targetDist > radius;
        }

        var sb = new StringBuilder();

        // ── 3. 目标方位摘要行 ─────────────────────────────────────────
        if (hasTarget)
        {
            string targetCompass = GetCompass(
                targetWorldPos.Value.x - agentWorldPos.x,
                targetWorldPos.Value.z - agentWorldPos.z);
            string rangeNote = targetOutOfRange
                ? $"超出{radius:0}m范围，需通过中间航点导航"
                : "在本地图范围内";
            sb.AppendLine($"[目标方位] {targetCompass}方向 约{targetDist:0}m（{rangeNote}）");
            sb.AppendLine();
        }

        // ── 4. 表头 ───────────────────────────────────────────────────
        if (hasTarget)
        {
            sb.AppendLine("方向    距离    名称                  类型       趋向目标");
            sb.AppendLine("──────────────────────────────────────────────────────────");
        }
        else
        {
            sb.AppendLine("方向    距离    名称                  类型");
            sb.AppendLine("────────────────────────────────────────────");
        }

        // ── 5. 要素行 ─────────────────────────────────────────────────
        foreach (var (p, dist) in nearby)
        {
            float dx = p.centroidWorld.x - agentWorldPos.x;
            float dz = p.centroidWorld.z - agentWorldPos.z;
            string compass = GetCompass(dx, dz);
            string displayName = string.IsNullOrWhiteSpace(p.runtimeAlias) ? p.name : p.runtimeAlias;
            string kind = string.IsNullOrWhiteSpace(p.kind) ? "other" : p.kind;

            string toward = "";
            if (hasTarget && toTargetDir.sqrMagnitude > 0.0001f)
            {
                Vector2 toFeature = new Vector2(dx, dz);
                float featureDist = toFeature.magnitude;
                if (featureDist > 0.01f)
                {
                    float dot = Vector2.Dot(toFeature.normalized, toTargetDir);
                    if (dot > 0.5f) toward = "★";
                }
            }

            if (hasTarget)
                sb.AppendLine($"{compass,-6}  {dist,4:0}m   {displayName,-20}  {kind,-10} {toward,-2}".TrimEnd());
            else
                sb.AppendLine($"{compass,-6}  {dist,4:0}m   {displayName,-20}  {kind,-10}".TrimEnd());
        }

        if (nearby.Count == 0)
            sb.AppendLine("（当前位置半径范围内无已知地物）");

        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 感知段：基于 agent 自身传感器的实时感知数据
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将 agent 自身 PerceptionModule 的实时检测结果转为 LLM 可读文本。
    /// 仅包含该 agent 传感器当前帧检测到的小节点和敌方智能体，不使用全局共享注册表。
    /// </summary>
    /// <param name="detectedObjects">PerceptionModule.detectedObjects（当前帧检测到的小节点）。</param>
    /// <param name="enemyAgents">PerceptionModule.enemyAgents（当前帧检测到的敌方智能体）。</param>
    /// <param name="agentWorldPos">智能体当前世界坐标。</param>
    public static string BuildPerceptionSection(
        List<SmallNodeData> detectedObjects,
        List<IntelligentAgent> enemyAgents,
        Vector3 agentWorldPos)
    {
        var sb = new StringBuilder();
        bool hasAny = false;

        // ── 1. 小节点感知（按类型+方位聚合） ──────────────────────────
        if (detectedObjects != null && detectedObjects.Count > 0)
        {
            // 按 (NodeType, compass方位) 聚合
            var groups = new Dictionary<(SmallNodeType type, string compass), (int count, float minDist)>();
            foreach (var node in detectedObjects)
            {
                if (node.NodeType == SmallNodeType.Agent) continue; // 敌方agent单独处理
                float dx = node.WorldPosition.x - agentWorldPos.x;
                float dz = node.WorldPosition.z - agentWorldPos.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                string compass = GetCompass(dx, dz);
                var key = (node.NodeType, compass);

                if (groups.TryGetValue(key, out var existing))
                    groups[key] = (existing.count + 1, Mathf.Min(existing.minDist, dist));
                else
                    groups[key] = (1, dist);
            }

            // 按距离排序输出
            var sorted = groups.OrderBy(g => g.Value.minDist);
            foreach (var kv in sorted)
            {
                string typeName = GetSmallNodeTypeName(kv.Key.type);
                string countStr = kv.Value.count > 1 ? $"({kv.Value.count})" : "";
                sb.AppendLine($"- {typeName}{countStr} @ {kv.Key.compass}方 {kv.Value.minDist:0}m");
                hasAny = true;
            }
        }

        // ── 2. 敌方智能体感知 ──────────────────────────────────────────
        if (enemyAgents != null && enemyAgents.Count > 0)
        {
            foreach (var enemy in enemyAgents)
            {
                if (enemy == null) continue;
                float dx = enemy.transform.position.x - agentWorldPos.x;
                float dz = enemy.transform.position.z - agentWorldPos.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                string compass = GetCompass(dx, dz);
                string status = enemy.CurrentState != null ? enemy.CurrentState.Status.ToString() : "Unknown";
                sb.AppendLine($"- [敌方] {enemy.Properties.AgentID} @ {compass}方 {dist:0}m（状态：{status}）");
                hasAny = true;
            }
        }

        if (!hasAny)
            sb.AppendLine("（传感器范围内未检测到目标）");

        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 历史观测记忆段：弥补传感器范围限制
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将历史观测记忆格式化为 LLM 可读文本，提供超出传感器范围的历史感知信息。
    /// </summary>
    /// <param name="observations">历史观测列表：(距今分钟数, 摘要文本)。</param>
    public static string BuildMemoryObservationsSection(List<(int minutesAgo, string summary)> observations)
    {
        if (observations == null || observations.Count == 0)
            return "（无近期历史观测记录）";

        var sb = new StringBuilder();
        foreach (var (minutesAgo, summary) in observations)
        {
            sb.AppendLine($"- {minutesAgo}分钟前：{summary}");
        }
        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 工具方法
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>根据 XZ 平面偏移量返回八方位罗盘标签。供其他模块复用。</summary>
    internal static string GetCompass(float dx, float dz)
    {
        // 0° = 北（+Z），顺时针增加
        float angle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg; // [-180, 180]
        if (angle < 0f) angle += 360f;
        int idx = (int)((angle + 22.5f) / 45f) % 8;
        return CompassLabels[idx];
    }

    private static string GetSmallNodeTypeName(SmallNodeType type)
    {
        switch (type)
        {
            case SmallNodeType.Pedestrian:        return "行人";
            case SmallNodeType.Vehicle:           return "车辆";
            case SmallNodeType.Tree:              return "树木";
            case SmallNodeType.ResourcePoint:     return "资源点";
            case SmallNodeType.TemporaryObstacle: return "临时障碍";
            case SmallNodeType.Custom:            return "自定义目标";
            default:                              return "未知目标";
        }
    }
}
