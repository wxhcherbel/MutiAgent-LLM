using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>将 CampusGrid2D 地图数据序列化为 LLM 可读文本。</summary>
public static class MapTopologySerializer
{
    private static readonly Dictionary<string, string> KindLabels = new()
    {
        {"building","楼"}, {"road","路"}, {"water","水域*"}, {"forest","林地#"},
        {"parking","停车□"}, {"green","绿地▽"}, {"sports","运动▽"}, {"bridge","桥"},
    };

    /// <summary>
    /// 生成全局折叠图（约150-200 token）。
    /// 格式：[建筑] A楼, 图书馆, B楼 | [道路] 主干道 | ...
    /// </summary>
    public static string GetGlobalFoldedMap(CampusGrid2D grid, int maxEntries = 60)
    {
        if (grid?.featureSpatialProfileByUid == null) return "(地图不可用)";

        var byKind = new Dictionary<string, List<string>>();
        int total = 0;

        foreach (var p in grid.featureSpatialProfileByUid.Values)
        {
            if (total >= maxEntries) break;
            string displayName = !string.IsNullOrWhiteSpace(p.runtimeAlias) ? p.runtimeAlias : p.name;
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            string kind = p.kind ?? "other";
            if (!byKind.ContainsKey(kind)) byKind[kind] = new List<string>();
            byKind[kind].Add(displayName);
            total++;
        }

        var sb = new StringBuilder("校园地图（节点列表）：\n");
        foreach (var kv in byKind)
        {
            string label = KindLabels.TryGetValue(kv.Key, out string l) ? l : kv.Key;
            sb.Append($"[{label}] {string.Join(", ", kv.Value)}\n");
        }
        sb.Append("注：水域(*)不可进入，林地(#)可通行但提供遮蔽");
        return sb.ToString();
    }

    /// <summary>
    /// 生成以智能体为中心、按罗盘方位分组的相对地图（约100-150 token）。
    /// 格式：[正北~80m] 图书馆 / [正东~120m] 教学楼A、体育馆
    /// </summary>
    public static string GetAgentRelativeMap(
        CampusGrid2D grid, Vector3 agentWorldPos,
        float radiusMeters = 300f, int maxEntries = 30)
    {
        if (grid?.featureSpatialProfileByUid == null) return "(地图不可用)";

        // 收集半径内地标，按罗盘方位分组
        var byDir = new Dictionary<string, List<(int dist, string name)>>();

        int count = 0;
        foreach (var p in grid.featureSpatialProfileByUid.Values)
        {
            if (count >= maxEntries) break;
            string displayName = !string.IsNullOrWhiteSpace(p.runtimeAlias) ? p.runtimeAlias : p.name;
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            float dist = Vector3.Distance(agentWorldPos, p.centroidWorld);
            if (dist > radiusMeters) continue;

            string dir = GetCompassDir(agentWorldPos, p.centroidWorld);
            int distRounded = Mathf.RoundToInt(dist / 10f) * 10;

            if (!byDir.ContainsKey(dir)) byDir[dir] = new List<(int, string)>();
            byDir[dir].Add((distRounded, displayName));
            count++;
        }

        if (byDir.Count == 0) return "（半径内无已知地标）";

        // 组内按距离升序排列
        var sb = new StringBuilder($"周边地图（以本机为中心，半径{(int)radiusMeters}m）：\n");
        string[] orderedDirs = { "正北", "东北", "正东", "东南", "正南", "西南", "正西", "西北" };
        foreach (string dir in orderedDirs)
        {
            if (!byDir.TryGetValue(dir, out var entries)) continue;
            entries.Sort((a, b) => a.dist.CompareTo(b.dist));
            // 同方位同距离的合并成同一行
            var grouped = new Dictionary<int, List<string>>();
            foreach (var (d, n) in entries)
            {
                if (!grouped.ContainsKey(d)) grouped[d] = new List<string>();
                grouped[d].Add(n);
            }
            foreach (var kv in grouped)
                sb.AppendLine($"[{dir}~{kv.Key}m] {string.Join("、", kv.Value)}");
        }
        return sb.ToString().TrimEnd();
    }

    // 仅供 GetAgentRelativeMap 内部使用。Z+ = 正北（Unity 左手坐标系）
    private static string GetCompassDir(Vector3 from, Vector3 to)
    {
        float angle = Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        string[] dirs = { "正北", "东北", "正东", "东南", "正南", "西南", "正西", "西北" };
        return dirs[Mathf.RoundToInt(angle / 45f) % 8];
    }

    /// <summary>
    /// 生成 fromNode→toNode 任务子图（约100-150 token），含威胁标注。
    /// 格式：列出两节点路径走廊内的邻近地物及威胁标记。
    /// </summary>
    public static string GetTaskSubgraph(
        CampusGrid2D grid,
        string fromNode,
        string toNode,
        List<string> threatLocations = null)
    {
        if (grid?.featureSpatialProfileByUid == null) return "(子图不可用)";
        if (string.IsNullOrWhiteSpace(fromNode) || string.IsNullOrWhiteSpace(toNode))
            return "(起终点无效)";

        // 获取起终点网格坐标
        if (!grid.TryGetFeatureFirstCell(fromNode, out Vector2Int fromCell, preferWalkable: true) ||
            !grid.TryGetFeatureFirstCell(toNode,   out Vector2Int toCell,   preferWalkable: true))
            return $"(无法定位节点: {fromNode} → {toNode})";

        // 计算包围矩形 + 2格 padding
        int minX = Mathf.Min(fromCell.x, toCell.x) - 2;
        int maxX = Mathf.Max(fromCell.x, toCell.x) + 2;
        int minY = Mathf.Min(fromCell.y, toCell.y) - 2;
        int maxY = Mathf.Max(fromCell.y, toCell.y) + 2;

        var nodes = new List<string>();
        var threats = new HashSet<string>(
            threatLocations ?? new List<string>(),
            System.StringComparer.OrdinalIgnoreCase);

        foreach (var p in grid.featureSpatialProfileByUid.Values)
        {
            if (p.centroidCell.x < minX || p.centroidCell.x > maxX) continue;
            if (p.centroidCell.y < minY || p.centroidCell.y > maxY) continue;
            string name = !string.IsNullOrWhiteSpace(p.runtimeAlias) ? p.runtimeAlias : p.name;
            if (string.IsNullOrWhiteSpace(name)) continue;

            string prefix = threats.Contains(name) ? "[!]" : "";
            nodes.Add($"{prefix}{name}");
        }

        var sb = new StringBuilder($"任务子图（{fromNode} → {toNode}）：\n");
        sb.Append(string.Join(", ", nodes));
        if (threats.Count > 0)
            sb.Append("\n注：[!] 标记位置有威胁报告，建议绕行");
        return sb.ToString();
    }
}
