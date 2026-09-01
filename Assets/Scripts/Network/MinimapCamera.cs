using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Top-down orthographic map camera (PHASE8). Two instances are created at
    /// runtime by <see cref="GameHud"/> and render into dedicated RenderTextures:
    ///
    ///  - <see cref="Mode.Minimap"/>: follows the local player (X/Z), fixed at
    ///    <see cref="MapLayers.IndicatorWorldY"/> + height, orthographicSize covering
    ///    <see cref="MapLayers.MinimapCoverage"/> metres, and rotates around Y with
    ///    the player's yaw so "up" = forward. Renders only the indicator layer.
    ///  - <see cref="Mode.BigMap"/>: fixed at the map centre, orthographicSize covers
    ///    the whole 100×200 map, never rotates. Renders indicators + highlights.
    ///
    /// A plain MonoBehaviour (no NetworkBehaviour): presentation only, client-side.
    /// </summary>
    public class MinimapCamera : MonoBehaviour
    {
        public enum Mode { Minimap, BigMap }

        [Header("Mode")]
        public Mode mode = Mode.Minimap;

        [Header("Big map")]
        [Tooltip("World X/Z centre of the big map.")]
        public Vector2 bigMapCenter = Vector2.zero;
        [Tooltip("Orthographic half-extent (m) covering the map's long axis (Z=100).")]
        public float bigMapHalfExtent = 100f;

        private Camera cam;

        private void Awake()
        {
            cam = GetComponent<Camera>();
            if (cam == null) cam = gameObject.AddComponent<Camera>();
        }

        private void Start()
        {
            Configure();
        }

        private void Configure()
        {
            if (cam == null) return;

            cam.orthographic = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = MapLayers.MinimapHeight * 2f + 50f;

            if (mode == Mode.Minimap)
            {
                cam.orthographicSize = MapLayers.MinimapCoverage;   // ±75m
                // 小地图：地形(Ground，不含 ceiling——室内可见) + 实体球体
                // + 据点/安全区标记(MapZone 填充 + MapZoneOutline 描边)。
                cam.cullingMask = MapLayers.IndicatorMask | MapLayers.HighlightMask
                                  | MapLayers.GroundMask
                                  | MapLayers.ZoneMask | MapLayers.ZoneOutlineMask;
            }
            else
            {
                // ML-branch: 大地图视野自适应地图 AABB（Ground 层）。orthoSize 是
                // 垂直半高：横向覆盖 = orthoSize * aspect，必须保证两个方向都
                // 容纳地图 → orthoSize = max(mapW/aspect, mapL) / 2。
                if (MapLayers.TryGetMapBounds(out var b))
                {
                    bigMapCenter = new Vector2(b.center.x, b.center.z);
                    float mapW = b.size.x;
                    float mapL = b.size.z;
                    float aspect = Mathf.Max(0.05f, cam.aspect);
                    bigMapHalfExtent = Mathf.Max(mapW / aspect, mapL) * 0.5f;
                }
                cam.orthographicSize = bigMapHalfExtent;
                // 大地图：同样可见地形但排除 ceiling（ceiling 不在 GroundMask 内）。
                cam.cullingMask = MapLayers.MapMask | MapLayers.GroundMask
                                  | MapLayers.ZoneMask | MapLayers.ZoneOutlineMask;
            }

            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);

            // HDRP 忽略 Camera.clearFlags。必须设置 HDAdditionalCameraData：
            //   clearColorMode = Color（背景色），透明背景（alpha 0），
            //   并禁用后处理（自动曝光会把深色背景提亮成中灰）。
            ApplyHdrpTransparentBackground();
        }

        /// <summary>
        /// HDRP: force the map cameras to clear to a TRANSPARENT background and
        /// disable post-processing (auto-exposure would lift the dark clear colour to
        /// mid-grey). Done via reflection to avoid a hard dependency on the HDRP package.
        /// </summary>
        private void ApplyHdrpTransparentBackground()
        {
            var hdType = System.Type.GetType(
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime");
            if (hdType == null) return;

            var hd = cam.GetComponent(hdType);
            if (hd == null) hd = cam.gameObject.AddComponent(hdType);
            if (hd == null) return;

            try
            {
                // clearColorMode = Color
                var modeEnum = hdType.Assembly.GetType(
                    "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData+ClearColorMode");
                if (modeEnum != null)
                {
                    var clearProp = hdType.GetProperty("clearColorMode");
                    if (clearProp != null)
                        clearProp.SetValue(hd, System.Enum.Parse(modeEnum, "Color"));
                }

                // 透明背景（alpha 0）→ UI 上地图 RawImage 的 alpha 真正生效。
                var bgProp = hdType.GetProperty("backgroundColorHDR");
                if (bgProp != null)
                    bgProp.SetValue(hd, new Color(0f, 0f, 0f, 0f));

                // 禁用 HDRP 后处理（自动曝光/tonemapping 会把背景色提亮）。
                var ppProp = hdType.GetProperty("postProcessEnabled");
                if (ppProp != null)
                    ppProp.SetValue(hd, false);

                // 显式关闭所有后处理（旧版本通过 antiAliasing / stopNaN… 不相关；直接关闭整体）。
                var stopNanProp = hdType.GetProperty("stopNaN");
                if (stopNanProp != null) stopNanProp.SetValue(hd, false);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MinimapCamera] HDRP background setup failed: {e.Message}");
            }
        }

        private void LateUpdate()
        {
            if (cam == null) return;

            if (mode == Mode.Minimap)
            {
                if (!NetworkClient.active || NetworkClient.localPlayer == null)
                {
                    cam.enabled = false;
                    return;
                }

                cam.enabled = true;
                var local = NetworkClient.localPlayer.transform;
                float yaw = local.eulerAngles.y;

                transform.position = new Vector3(
                    local.position.x,
                    MapLayers.IndicatorWorldY + MapLayers.MinimapHeight,
                    local.position.z);
                transform.rotation = Quaternion.Euler(90f, yaw, 0f);
            }
            else
            {
                transform.position = new Vector3(
                    bigMapCenter.x,
                    MapLayers.IndicatorWorldY + MapLayers.MinimapHeight,
                    bigMapCenter.y);
                transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
        }
    }
}
