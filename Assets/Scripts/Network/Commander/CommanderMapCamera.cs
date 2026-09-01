using System;
using System.Collections;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 指挥官全局地图快照相机（纯服务端）。正交俯视，长边像素=配置值，
    /// 短边按地图比例自适应（Q10）；HDRP 相机按 MinimapCamera 同款反射套路强制
    /// 不透明深色背景 + 关闭后处理（LLM 读图需要稳定对比度）。
    ///
    /// 拍照协议：PrepareForCapture(team) 切换覆盖层分组 → 渲染两帧（HDRP 异步帧
    /// 保险）→ 从 RenderTexture ReadPixels → EncodeToPNG 回调。
    /// cullingMask = CommanderMap(网格/标尺/点标记) | MapHighlight(HQ/GR 字母盘)。
    /// </summary>
    public class CommanderMapCamera : MonoBehaviour
    {
        [Header("引用")]
        public CommanderMapOverlay overlay;

        [Header("输出")]
        [Tooltip("快照长边像素；短边 = 长边 × (短轴/长轴)，16 对齐。")]
        public int longSidePixels = 2048;

        private Camera cam;
        private RenderTexture rt;
        private Texture2D readTex;
        private bool capturing;

        private static readonly Color BackgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);

        public (int width, int height) Resolution => (rt != null ? rt.width : 0, rt != null ? rt.height : 0);

        private void Start()
        {
            if (overlay == null) overlay = FindObjectOfType<CommanderMapOverlay>();
            if (overlay == null)
            {
                Debug.LogWarning("[CommanderCam] 缺少 CommanderMapOverlay，相机停用。");
                enabled = false;
                return;
            }

            SetupCamera();
        }

        private void OnDestroy()
        {
            if (rt != null) { rt.Release(); Destroy(rt); }
            if (readTex != null) Destroy(readTex);
        }

        private void SetupCamera()
        {
            cam = gameObject.GetComponent<Camera>();
            if (cam == null) cam = gameObject.AddComponent<Camera>();

            float w = overlay.MapWidth;
            float l = overlay.MapLength;
            bool landscape = w > l;             // 短边/长边比例
            float ratio = Mathf.Min(w, l) / Mathf.Max(w, l);

            int longPx = Mathf.Max(256, longSidePixels);
            int shortPx = Mathf.ClosestPowerOfTwo(Mathf.RoundToInt(longPx * ratio));
            shortPx = Mathf.Clamp(shortPx, 128, longPx);
            // 16 像素对齐（视觉编码友好）。
            longPx = Mathf.RoundToInt(longPx / 16f) * 16;
            shortPx = Mathf.RoundToInt(shortPx / 16f) * 16;

            int texW = landscape ? longPx : shortPx;
            int texH = landscape ? shortPx : longPx;

            rt = new RenderTexture(texW, texH, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;

            cam.targetTexture = rt;
            cam.orthographic = true;
            // 正交尺寸 = 长轴一半；aspect 与纹理一致保证整图恰好取满。
            cam.orthographicSize = Mathf.Max(w, l) * 0.5f;
            cam.aspect = (float)texW / texH;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 60f;
            cam.cullingMask = LayerMask.GetMask(CommanderMapOverlay.LayerName,
                                                MapLayers.HighlightName)
                              | MapLayers.GroundMask   // 地形可见，ceiling 被排除（室内可见）
                              | MapLayers.ZoneMask | MapLayers.ZoneOutlineMask;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BackgroundColor;

            // HDRP：不透明背景 + 关后处理（自动曝光会漂灰度）。
            ApplyHdrpOpaqueBackground();

            // 摆位：地图中心上空，X 轴对齐世界东向、屏幕上方=世界 +Z（北）。
            var center = overlay.GridToWorld(overlay.MapWidth * 0.5f, overlay.MapLength * 0.5f);
            transform.position = new Vector3(center.x, 40f, center.z);
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            Debug.Log($"[CommanderCam] RT {texW}x{texH} map {w}x{l}m");
        }

        /// <summary>
        /// 为指定阵营拍照。协程返回时 pngBytes 已就绪（两帧渲染保险）。
        /// </summary>
        public IEnumerator CaptureRoutine(int team, Action<byte[]> onDone)
        {
            if (cam == null || overlay == null)
            {
                onDone?.Invoke(null);
                yield break;
            }
            if (capturing)
            {
                Debug.LogWarning("[CommanderCam] 已有拍照进行中");
                onDone?.Invoke(null);
                yield break;
            }
            capturing = true;
            try
            {
                overlay.PrepareForCapture(team);

                // 两帧：让切换后的场景完成一整次 HDRP 渲染进 RT。
                yield return null;
                yield return null;

                byte[] png = ReadRtToPng();
                onDone?.Invoke(png);
            }
            finally
            {
                overlay.ResetToIdle();
                capturing = false;
            }
        }

        private byte[] ReadRtToPng()
        {
            int w = rt.width, h = rt.height;
            if (readTex == null || readTex.width != w || readTex.height != h)
            {
                if (readTex != null) Destroy(readTex);
                readTex = new Texture2D(w, h, TextureFormat.RGB24, false);
            }

            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                readTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readTex.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = prev;
            }
            return readTex.EncodeToPNG();
        }

        private void ApplyHdrpOpaqueBackground()
        {
            var hdType = Type.GetType(
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime");
            if (hdType == null) return;

            var hd = cam.GetComponent(hdType);
            if (hd == null) hd = cam.gameObject.AddComponent(hdType);
            if (hd == null) return;

            try
            {
                var modeEnum = hdType.Assembly.GetType(
                    "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData+ClearColorMode");
                var clearProp = hdType.GetProperty("clearColorMode");
                if (modeEnum != null && clearProp != null)
                    clearProp.SetValue(hd, Enum.Parse(modeEnum, "Color"));

                var bgProp = hdType.GetProperty("backgroundColorHDR");
                bgProp?.SetValue(hd, BackgroundColor);

                var ppProp = hdType.GetProperty("postProcessEnabled");
                ppProp?.SetValue(hd, false);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CommanderCam] HDRP background setup failed: {e.Message}");
            }
        }
    }
}
