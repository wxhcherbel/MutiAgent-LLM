using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地图序列化结果。
/// </summary>
[System.Serializable]
public class MapData
{
    /// <summary>地图宽度。</summary>
    public int mapWidth;

    /// <summary>地图长度。</summary>
    public int mapLength;

    /// <summary>地图随机种子。</summary>
    public int seed;

    /// <summary>地图内所有对象的序列化数据。</summary>
    public List<ObjectData> objects = new List<ObjectData>();
}

/// <summary>
/// 单个地图对象的序列化结果。
/// </summary>
[System.Serializable]
public class ObjectData
{
    /// <summary>对象对应的预制体名称。</summary>
    public string prefabName;

    /// <summary>对象位置。</summary>
    public Vector3 position;

    /// <summary>对象旋转。</summary>
    public Quaternion rotation;

    /// <summary>对象缩放。</summary>
    public Vector3 scale;
}
