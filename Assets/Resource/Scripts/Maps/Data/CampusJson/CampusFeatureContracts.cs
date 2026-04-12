using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 校园地图要素类型。
/// </summary>
public enum CampusFeatureKind : byte
{
    Building = 0,
    Sports = 1,
    Water = 2,
    Road = 3,
    Expressway = 4,
    Bridge = 5,
    Parking = 6,
    Green = 7,
    Forest = 8,
    Other = 9,
}

/// <summary>
/// 单个多边形环。
/// </summary>
[Serializable]
public class CampusRing
{
    /// <summary>环上的点集合，采用地图平面 XY 坐标。</summary>
    public List<Vector2> points = new List<Vector2>();
}

/// <summary>
/// 单个校园地图要素。
/// </summary>
[Serializable]
public class CampusFeature
{
    /// <summary>要素唯一 ID。</summary>
    public string uid;

    /// <summary>要素类型。</summary>
    public CampusFeatureKind kind = CampusFeatureKind.Other;

    /// <summary>要素名称。</summary>
    public string name;

    /// <summary>原始 tags 字典。</summary>
    public Dictionary<string, string> tags = new Dictionary<string, string>();

    /// <summary>线状要素的折线点集。</summary>
    public List<Vector2> linePoints = new List<Vector2>();

    /// <summary>面要素的外环集合。</summary>
    public List<CampusRing> outerRings = new List<CampusRing>();

    /// <summary>面要素的内环孔洞集合。</summary>
    public List<CampusRing> innerRings = new List<CampusRing>();

    /// <summary>要素边界框。</summary>
    public Rect bounds;

    /// <summary>边界框是否有效。</summary>
    public bool boundsValid;

    /// <summary>是否包含有效面片。</summary>
    public bool HasArea()
    {
        for (int i = 0; i < outerRings.Count; i++)
        {
            if (outerRings[i].points != null && outerRings[i].points.Count >= 3) return true;
        }
        return false;
    }

    /// <summary>是否包含有效线段。</summary>
    public bool HasLine()
    {
        return linePoints != null && linePoints.Count >= 2;
    }
}
