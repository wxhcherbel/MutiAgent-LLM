using UnityEngine;

/// <summary>
/// 小节点运行时语义组件：
/// 由 CampusSmallNodeSpawner 在生成节点时自动挂载，
/// 供感知系统直接读取节点类型、动态属性和阻塞属性。
/// </summary>
public class SmallNodeRuntimeInfo : MonoBehaviour
{
    public SmallNodeType nodeType = SmallNodeType.Unknown;
    public bool isDynamic = false;
    public bool blocksMovement = true;
    public int serialIndex = 0;
    public string displayName = "";
    public CampusSmallNodeSpawner sourceSpawner;
}

