using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>将 CampusGrid2D 地图数据序列化为 LLM 可读文本。</summary>
public static class MapTopologySerializer
{
    /// <summary>
    /// 生成以智能体为中心、按罗盘方位分组的相对地图（约100-150 token）。
    /// 格式：[正北~80m] 图书馆 / [正东~120m] 教学楼A、体育馆
    /// </summary>
    public static string GetAgentRelativeMap(CampusGrid2D grid, Vector3 agentWorldPos,float radiusMeters = 300f, int maxEntries = 30)
    {
        
    }
}
