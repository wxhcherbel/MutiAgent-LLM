using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>将 CampusGrid2D 地图数据序列化为 LLM 可读文本。</summary>
public static class MapTopologySerializer
{
    private static readonly string[] CompassLabels = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

    /// <summary>
    /// 生成以智能体为中心的局部地图文本，可选标注趋向目标的中间航点。
    /// </summary>
    /// <param name="grid">地图数据。</param>
    /// <param name="agentWorldPos">智能体当前世界坐标。</param>
    /// <param name="targetWorldPos">步骤空间目标的世界坐标（可为 null）。</param>
    /// <param name="radius">局部地图半径（米），默认 300。</param>
    public static string GetAgentRelativeMap(
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

        // ── 5. 统计每个要素 footprint 内的小节点（拥挤标注）────────
        // 同时收集所有节点，用于后续 Other 区域归并
        const float NEARBY_BUFFER      = 5f;   // footprint 半径额外 buffer（m）
        const float OTHER_MERGE_RADIUS = 50f;  // Other 节点寻找最近要素的最大距离（m）
        const int   PEDESTRIAN_THRESH  = 3;    // 行人密集阈值
        const int   VEHICLE_THRESH     = 2;    // 车辆密集阈值
        const int   TREE_THRESH        = 5;    // 树木密集阈值
        const int   RESOURCE_THRESH    = 1;    // 资源点出现阈值
        const int   OBSTACLE_THRESH    = 1;    // 临时障碍出现阈值

        // 收集局部地图范围内全部小节点（含静态与动态）
        var allNearbyNodes = SmallNodeRegistry.QueryNodes(agentWorldPos, radius,
            includeStatic: true, includeDynamic: true);

        // 记录已被某个要素 footprint 覆盖的节点 ID
        var coveredNodeIds = new HashSet<string>();

        // 每个要素的拥挤标注（uid → tag）
        var congestionTags = new Dictionary<string, string>();
        foreach (var (p, _) in nearby)
        {
            float queryR = Mathf.Max(p.footprintRadius, 10f) + NEARBY_BUFFER;
            int pedestrians = 0, vehicles = 0, trees = 0, resources = 0, obstacles = 0;
            foreach (var n in allNearbyNodes)
            {
                float d2 = (n.WorldPosition.x - p.centroidWorld.x) * (n.WorldPosition.x - p.centroidWorld.x)
                         + (n.WorldPosition.z - p.centroidWorld.z) * (n.WorldPosition.z - p.centroidWorld.z);
                if (d2 <= queryR * queryR)
                {
                    coveredNodeIds.Add(n.NodeId);
                    if      (n.NodeType == SmallNodeType.Pedestrian)        pedestrians++;
                    else if (n.NodeType == SmallNodeType.Vehicle)           vehicles++;
                    else if (n.NodeType == SmallNodeType.Tree)              trees++;
                    else if (n.NodeType == SmallNodeType.ResourcePoint)     resources++;
                    else if (n.NodeType == SmallNodeType.TemporaryObstacle) obstacles++;
                }
            }

            string tag = "";
            if (pedestrians >= PEDESTRIAN_THRESH && vehicles >= VEHICLE_THRESH) tag += "（人车密集）";
            else if (pedestrians >= PEDESTRIAN_THRESH)                           tag += "（行人多）";
            else if (vehicles >= VEHICLE_THRESH)                                 tag += "（车辆多）";
            if (trees     >= TREE_THRESH)     tag += "（树木密集）";
            if (resources >= RESOURCE_THRESH) tag += "（有资源点）";
            if (obstacles >= OBSTACLE_THRESH) tag += "（有临时障碍）";
            if (tag.Length > 0) congestionTags[p.uid] = tag;
        }

        // Other 区域节点最近邻归并：找到未覆盖节点，归入最近要素的外围计数
        var outerCounts = new Dictionary<string, (int peds, int vehs, int trees, int resources, int obstacles)>();
        foreach (var n in allNearbyNodes)
        {
            if (coveredNodeIds.Contains(n.NodeId)) continue;
            if (n.NodeType == SmallNodeType.Unknown || n.NodeType == SmallNodeType.Agent || n.NodeType == SmallNodeType.Custom) continue;

            float bestDist2 = OTHER_MERGE_RADIUS * OTHER_MERGE_RADIUS;
            string bestUid  = null;
            foreach (var (p, _) in nearby)
            {
                float d2 = (n.WorldPosition.x - p.centroidWorld.x) * (n.WorldPosition.x - p.centroidWorld.x)
                         + (n.WorldPosition.z - p.centroidWorld.z) * (n.WorldPosition.z - p.centroidWorld.z);
                if (d2 < bestDist2) { bestDist2 = d2; bestUid = p.uid; }
            }
            if (bestUid == null) continue;

            outerCounts.TryGetValue(bestUid, out var cur);
            if      (n.NodeType == SmallNodeType.Pedestrian)        outerCounts[bestUid] = (cur.peds + 1, cur.vehs, cur.trees, cur.resources, cur.obstacles);
            else if (n.NodeType == SmallNodeType.Vehicle)           outerCounts[bestUid] = (cur.peds, cur.vehs + 1, cur.trees, cur.resources, cur.obstacles);
            else if (n.NodeType == SmallNodeType.Tree)              outerCounts[bestUid] = (cur.peds, cur.vehs, cur.trees + 1, cur.resources, cur.obstacles);
            else if (n.NodeType == SmallNodeType.ResourcePoint)     outerCounts[bestUid] = (cur.peds, cur.vehs, cur.trees, cur.resources + 1, cur.obstacles);
            else if (n.NodeType == SmallNodeType.TemporaryObstacle) outerCounts[bestUid] = (cur.peds, cur.vehs, cur.trees, cur.resources, cur.obstacles + 1);
        }

        // ── 6. 要素行 ─────────────────────────────────────────────────
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

            // 拼接拥挤标注（footprint 内 + 外围归并）
            string congestion = congestionTags.TryGetValue(p.uid, out var ct) ? ct : "";
            if (outerCounts.TryGetValue(p.uid, out var oc))
            {
                var outerParts = new List<string>();
                if (oc.peds      > 0) outerParts.Add($"行人{oc.peds}");
                if (oc.vehs      > 0) outerParts.Add($"车辆{oc.vehs}");
                if (oc.trees     > 0) outerParts.Add($"树木{oc.trees}");
                if (oc.resources > 0) outerParts.Add($"资源点{oc.resources}");
                if (oc.obstacles > 0) outerParts.Add($"障碍{oc.obstacles}");
                if (outerParts.Count > 0)
                    congestion += $"（外围+{string.Join("/", outerParts)}）";
            }

            if (hasTarget)
                sb.AppendLine($"{compass,-6}  {dist,4:0}m   {displayName,-20}  {kind,-10} {toward,-2}  {congestion}".TrimEnd());
            else
                sb.AppendLine($"{compass,-6}  {dist,4:0}m   {displayName,-20}  {kind,-10}  {congestion}".TrimEnd());
        }

        if (nearby.Count == 0)
            sb.AppendLine("（当前位置半径范围内无已知地物）");

        return sb.ToString().TrimEnd();
    }

    private static string GetCompass(float dx, float dz)
    {
        // 0° = 北（+Z），顺时针增加
        float angle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg; // [-180, 180]
        if (angle < 0f) angle += 360f;
        int idx = (int)((angle + 22.5f) / 45f) % 8;
        return CompassLabels[idx];
    }
}
