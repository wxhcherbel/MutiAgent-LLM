using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 校园二维网格单元类型。
/// </summary>
public enum CampusGridCellType : byte
{
    Free = 0,
    Building = 1,
    Water = 2,
    Road = 3,
    Expressway = 4,
    Bridge = 5,
    Parking = 6,
    Green = 7,
    Forest = 8,
    Sports = 9,
    Other = 10,
}

/// <summary>
/// 原始地图要素的二维解析结果。
/// </summary>
[Serializable]
internal class Feature2D
{
    /// <summary>要素唯一 ID。</summary>
    public string uid = "";

    /// <summary>原始名称。</summary>
    public string name = "";

    /// <summary>归一化后的要素类型文本。</summary>
    public string kind = "other";

    /// <summary>用于运行时查询的最终有效名称。</summary>
    public string effectiveName = "";

    /// <summary>运行时生成的别名。</summary>
    public string runtimeAlias = "";

    /// <summary>要素边界框。</summary>
    public Rect bounds;

    /// <summary>边界框是否有效。</summary>
    public bool boundsValid;

    /// <summary>面要素外环集合。</summary>
    public readonly List<List<Vector2>> outerRings = new List<List<Vector2>>();

    /// <summary>面要素孔洞集合。</summary>
    public readonly List<List<Vector2>> innerRings = new List<List<Vector2>>();

    /// <summary>线要素点集。</summary>
    public readonly List<Vector2> linePoints = new List<Vector2>();

    /// <summary>预计算后的面栅格化片段。</summary>
    public readonly List<RasterAreaPart> rasterAreaParts = new List<RasterAreaPart>();

    /// <summary>预计算后的线栅格化四边形。</summary>
    public readonly List<RasterStrokeQuad> rasterStrokeQuads = new List<RasterStrokeQuad>();

    /// <summary>栅格化边界框。</summary>
    public Rect rasterBounds;

    /// <summary>栅格化边界框是否有效。</summary>
    public bool rasterBoundsValid;

    /// <summary>当前采用的栅格化模式。</summary>
    public FeatureRasterMode rasterMode = FeatureRasterMode.Bounds;

    /// <summary>是否存在面片数据。</summary>
    public bool HasArea => outerRings.Count > 0;

    /// <summary>是否存在线数据。</summary>
    public bool HasLine => linePoints.Count >= 2;

    /// <summary>是否存在预计算面片栅格数据。</summary>
    public bool HasRasterArea => rasterAreaParts.Count > 0;

    /// <summary>是否存在预计算线栅格数据。</summary>
    public bool HasRasterLine => rasterStrokeQuads.Count > 0;
}

/// <summary>
/// 地图要素的栅格化模式。
/// </summary>
internal enum FeatureRasterMode : byte
{
    Bounds = 0,
    Area = 1,
    Line = 2,
}

/// <summary>
/// 面要素栅格片段。
/// </summary>
internal class RasterAreaPart
{
    /// <summary>外边界多边形。</summary>
    public readonly List<Vector2> outer = new List<Vector2>();

    /// <summary>孔洞集合。</summary>
    public readonly List<List<Vector2>> holes = new List<List<Vector2>>();

    /// <summary>该片段边界框。</summary>
    public Rect bounds;

    /// <summary>该片段边界框是否有效。</summary>
    public bool boundsValid;
}

/// <summary>
/// 线段拉伸后的栅格四边形。
/// </summary>
internal class RasterStrokeQuad
{
    /// <summary>四边形四个角点。</summary>
    public readonly List<Vector2> points = new List<Vector2>(4);

    /// <summary>四边形边界框。</summary>
    public Rect bounds;

    /// <summary>四边形边界框是否有效。</summary>
    public bool boundsValid;
}

/// <summary>
/// 对外暴露的地图要素空间画像。
/// </summary>
[Serializable]
public class FeatureSpatialProfile
{
    /// <summary>要素唯一 ID。</summary>
    public string uid = "";

    /// <summary>要素名称。</summary>
    public string name = "";

    /// <summary>运行时别名。</summary>
    public string runtimeAlias = "";

    /// <summary>要素类型文本。</summary>
    public string kind = "other";

    /// <summary>所属集合键，例如 feature_kind:building。</summary>
    public string collectionKey = "";

    /// <summary>网格单元类型。</summary>
    public CampusGridCellType cellType = CampusGridCellType.Other;

    /// <summary>被该要素占据的网格数量。</summary>
    public int occupiedCellCount;

    /// <summary>最小 X 网格坐标。</summary>
    public int minX;

    /// <summary>最大 X 网格坐标。</summary>
    public int maxX;

    /// <summary>最小 Z 网格坐标。</summary>
    public int minZ;

    /// <summary>最大 Z 网格坐标。</summary>
    public int maxZ;

    /// <summary>网格质心位置。</summary>
    public Vector2 centroidGrid;

    /// <summary>最接近质心的网格单元。</summary>
    public Vector2Int centroidCell = new Vector2Int(-1, -1);

    /// <summary>世界坐标质心。</summary>
    public Vector3 centroidWorld;

    /// <summary>用于接近该要素的锚点网格。</summary>
    public Vector2Int anchorCell = new Vector2Int(-1, -1);

    /// <summary>用于接近该要素的锚点世界坐标。</summary>
    public Vector3 anchorWorld;

    /// <summary>要素外接半径估计值。</summary>
    public float footprintRadius;
}

/// <summary>
/// 内部使用的要素占格索引。
/// </summary>
internal class FeatureSpatialIndex
{
    /// <summary>要素唯一 ID。</summary>
    public string uid = "";

    /// <summary>要素名称。</summary>
    public string name = "";

    /// <summary>运行时别名。</summary>
    public string runtimeAlias = "";

    /// <summary>要素类型文本。</summary>
    public string kind = "other";

    /// <summary>所属集合键。</summary>
    public string collectionKey = "";

    /// <summary>网格单元类型。</summary>
    public CampusGridCellType cellType = CampusGridCellType.Other;

    /// <summary>该要素占据的网格列表。</summary>
    public readonly List<Vector2Int> occupiedCells = new List<Vector2Int>();

    /// <summary>用于快速判重的占格键集合。</summary>
    public readonly HashSet<long> occupiedCellKeys = new HashSet<long>();

    /// <summary>最小 X 网格坐标。</summary>
    public int minX = int.MaxValue;

    /// <summary>最大 X 网格坐标。</summary>
    public int maxX = int.MinValue;

    /// <summary>最小 Z 网格坐标。</summary>
    public int minZ = int.MaxValue;

    /// <summary>最大 Z 网格坐标。</summary>
    public int maxZ = int.MinValue;
}

/// <summary>
/// A* 堆条目。
/// </summary>
internal struct AStarHeapEntry
{
    /// <summary>对应的网格坐标。</summary>
    public Vector2Int Cell;

    /// <summary>F 分数。</summary>
    public float FScore;

    /// <summary>H 分数。</summary>
    public float HScore;
}

/// <summary>
/// A* 使用的小根堆。
/// </summary>
internal sealed class AStarMinHeap
{
    private readonly List<AStarHeapEntry> entries = new List<AStarHeapEntry>(256);

    /// <summary>当前堆元素数量。</summary>
    public int Count => entries.Count;

    /// <summary>入堆。</summary>
    public void Push(AStarHeapEntry entry)
    {
        entries.Add(entry);
        SiftUp(entries.Count - 1);
    }

    /// <summary>弹出堆顶。</summary>
    public AStarHeapEntry Pop()
    {
        int lastIndex = entries.Count - 1;
        AStarHeapEntry root = entries[0];
        AStarHeapEntry tail = entries[lastIndex];
        entries.RemoveAt(lastIndex);

        if (entries.Count > 0)
        {
            entries[0] = tail;
            SiftDown(0);
        }

        return root;
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (Compare(entries[index], entries[parent]) >= 0) break;
            Swap(index, parent);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        int count = entries.Count;
        while (true)
        {
            int left = index * 2 + 1;
            if (left >= count) break;

            int right = left + 1;
            int smallest = left;
            if (right < count && Compare(entries[right], entries[left]) < 0)
            {
                smallest = right;
            }

            if (Compare(entries[smallest], entries[index]) >= 0) break;
            Swap(index, smallest);
            index = smallest;
        }
    }

    private static int Compare(AStarHeapEntry a, AStarHeapEntry b)
    {
        int f = a.FScore.CompareTo(b.FScore);
        if (f != 0) return f;

        int h = a.HScore.CompareTo(b.HScore);
        if (h != 0) return h;

        int x = a.Cell.x.CompareTo(b.Cell.x);
        if (x != 0) return x;

        return a.Cell.y.CompareTo(b.Cell.y);
    }

    private void Swap(int a, int b)
    {
        AStarHeapEntry temp = entries[a];
        entries[a] = entries[b];
        entries[b] = temp;
    }
}
