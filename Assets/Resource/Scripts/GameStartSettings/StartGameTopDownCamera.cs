using UnityEngine;

/// <summary>
/// 点击“开始”按钮：主摄像机切换俯视并尽可能全视地图。
/// 适配 CampusJsonMapLoader 的 groundWidthM / groundLengthM。
/// </summary>
public class StartGameTopDownCamera : MonoBehaviour
{
    [Header("Map Reference")]
    public CampusJsonMapLoader campusFeature; // 你的地图脚本引用（提供 groundWidthM/groundLengthM）

    [Header("Camera")]
    public Camera targetCamera;              // 不填则自动用 Camera.main

    [Header("Top-Down Settings")]
    [Tooltip("俯视角度（X 轴）建议 90 为纯俯视；80 会有一点透视感，但这里我们用正交所以90更合适")]
    [Range(60f, 90f)]
    public float topDownPitch = 90f;

    [Tooltip("相机朝向的 Yaw（绕Y轴旋转），0 表示沿世界Z正方向")]
    public float yaw = 0f;

    [Tooltip("地图边缘留白（米）")]
    public float marginM = 5f;

    [Tooltip("正交相机时的最低 Orthographic Size（避免太小）")]
    public float minOrthoSize = 5f;

    [Tooltip("相机高度（米）。正交模式高度不影响视野，但影响近裁剪等，给个安全值")]
    public float cameraHeightM = 80f;

    [Tooltip("近裁剪面")]
    public float nearClip = 0.1f;

    [Tooltip("远裁剪面")]
    public float farClip = 1000f;

    /// <summary>
    /// 给 UI Button 直接绑定这个方法
    /// </summary>
    public void OnClickStart()
    {
        if (campusFeature == null)
        {
            Debug.LogError("[StartGameTopDownCamera] campusFeature 未赋值。");
            return;
        }

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        if (targetCamera == null)
        {
            Debug.LogError("[StartGameTopDownCamera] 找不到 Camera.main，也未手动指定 targetCamera。");
            return;
        }

        ApplyTopDownFullView();
    }

    private void ApplyTopDownFullView()
    {
        float w = Mathf.Max(5f, campusFeature.groundWidthM);
        float l = Mathf.Max(5f, campusFeature.groundLengthM);

        // 地图中心固定在 (0,0)
        Vector3 mapCenter = new Vector3(0f, 0f, 0f);

        if (targetCamera == null)
            targetCamera = Camera.main;

        if (targetCamera == null)
        {
            Debug.LogError("Camera not found.");
            return;
        }

        // ===== 1️⃣ 强制设置 Transform =====
        targetCamera.transform.position = new Vector3(0f, 700f, 0f);
        targetCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        targetCamera.transform.localScale = Vector3.one;

        // ===== 2️⃣ 使用正交相机全视地图 =====
        targetCamera.orthographic = true;
        targetCamera.nearClipPlane = nearClip;
        targetCamera.farClipPlane = farClip;

        float halfW = w * 0.5f + marginM;
        float halfL = l * 0.5f + marginM;

        float aspect = Mathf.Max(0.1f, targetCamera.aspect);

        float needOrthoByHeight = halfL;
        float needOrthoByWidth = halfW / aspect;

        float orthoSize = Mathf.Max(minOrthoSize, Mathf.Max(needOrthoByHeight, needOrthoByWidth));

        targetCamera.orthographicSize = orthoSize;

        Debug.Log($"TopDown Camera Set: Pos(0,700,0) Rot(90,0,0) OrthoSize={orthoSize}");
    }
}