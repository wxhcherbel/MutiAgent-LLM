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
