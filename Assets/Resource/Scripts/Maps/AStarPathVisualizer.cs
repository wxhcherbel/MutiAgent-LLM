// Maps/AStarPathVisualizer.cs
// A* 路径可视化：接收路径点列表，用 LineRenderer 绘制彩色路径线，完成后淡出。
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 挂载到无人机 GameObject 上，由 AgentMotionExecutor 驱动。
/// Inspector 可开关 showPath。
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class AStarPathVisualizer : MonoBehaviour
{
    [Header("可视化开关")]
    public bool showPath = true;

    [Header("淡出时长")]
    public float fadeOutDuration = 3f;

    [Header("线宽")]
    public float lineWidth = 0.15f;

    private LineRenderer lr;
    private Coroutine    fadeCoroutine;
    private Color        currentColor;

    private void Awake()
    {
        lr = GetComponent<LineRenderer>();
        lr.startWidth = lineWidth;
        lr.endWidth   = lineWidth;
        lr.positionCount = 0;

        // 使用简单的 Sprites/Default 材质
        if (lr.material == null || lr.material.name.Contains("Default-Material"))
        {
            lr.material = new Material(Shader.Find("Sprites/Default"));
        }
    }

    /// <summary>显示路径。</summary>
    public void ShowPath(List<Vector3> waypoints, Color color)
    {
        if (!showPath || waypoints == null || waypoints.Count == 0)
        {
            lr.positionCount = 0;
            return;
        }

        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);

        currentColor = color;
        lr.startColor = color;
        lr.endColor   = color;

        lr.positionCount = waypoints.Count;
        for (int i = 0; i < waypoints.Count; i++)
            lr.SetPosition(i, waypoints[i]);
    }

    /// <summary>清除路径（带淡出效果）。</summary>
    public void ClearPath()
    {
        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(FadeOut());
    }

    private IEnumerator FadeOut()
    {
        float elapsed = 0f;
        Color start   = currentColor;

        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;
            float a   = Mathf.Lerp(start.a, 0f, elapsed / fadeOutDuration);
            Color col = new Color(start.r, start.g, start.b, a);
            lr.startColor = col;
            lr.endColor   = col;
            yield return null;
        }

        lr.positionCount = 0;
        fadeCoroutine = null;
    }
}
