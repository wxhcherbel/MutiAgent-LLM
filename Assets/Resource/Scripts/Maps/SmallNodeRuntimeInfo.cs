using UnityEngine;

/// <summary>
/// 小节点运行时语义组件：
/// 由 CampusSmallNodeSpawner 在生成节点时自动挂载，
/// 供感知系统直接读取节点类型、动态属性和阻塞属性。
/// </summary>
public class SmallNodeRuntimeInfo : MonoBehaviour
{
    /// <summary>该节点的语义类型，供感知系统快速判别用途。</summary>
    public SmallNodeType nodeType = SmallNodeType.Unknown;

    /// <summary>该节点是否属于动态对象，例如行人或车辆。</summary>
    public bool isDynamic = false;
}
