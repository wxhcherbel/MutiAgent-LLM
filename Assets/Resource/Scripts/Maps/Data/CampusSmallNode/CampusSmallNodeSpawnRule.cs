using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 小节点刷点规则。
/// </summary>
[Serializable]
public class SpawnRule
{
    [Header("节点类型与数量")]
    /// <summary>要生成的小节点类型。</summary>
    public SmallNodeType nodeType = SmallNodeType.Tree;

    /// <summary>本条规则要生成的节点数量。</summary>
    [Min(0)] public int count = 20;

    [Header("区域分布（按地图要素类型）")]
    /// <summary>允许生成的地图要素类型集合；为空时可在全图范围生成。</summary>
    public List<CampusFeatureKind> spawnInFeatureKinds = new List<CampusFeatureKind>();

    /// <summary>单元格内部随机扰动比例。</summary>
    [Range(0f, 1f)] public float cellJitter01 = 0.75f;

    /// <summary>节点之间的最小间距。</summary>
    [Min(0f)] public float minSpacingM = 0.8f;

    /// <summary>生成时追加的高度偏移。</summary>
    [Min(-20f)] public float yOffset = 0f;

    [Header("生成对象")]
    /// <summary>优先使用的预制体。</summary>
    public GameObject prefab;

    /// <summary>没有预制体时的兜底基础几何体。</summary>
    public PrimitiveType fallbackPrimitive = PrimitiveType.Sphere;

    /// <summary>是否覆盖对象原始缩放。</summary>
    public bool overrideScale = true;

    /// <summary>生成对象的本地缩放。</summary>
    public Vector3 localScale = Vector3.one;

    /// <summary>生成对象的着色颜色。</summary>
    public Color tintColor = new Color32(200, 230, 200, 255);

    [Header("动态行为")]
    /// <summary>是否根据节点类型自动推断动态属性。</summary>
    public bool inferDynamicFromType = true;

    /// <summary>手动指定该节点是否可移动。</summary>
    public bool isDynamic = false;

    /// <summary>动态节点移动速度。</summary>
    [Min(0f)] public float moveSpeed = 1.6f;

    /// <summary>动态节点重新选择目标点的间隔。</summary>
    [Min(0.1f)] public float retargetInterval = 2f;
}
