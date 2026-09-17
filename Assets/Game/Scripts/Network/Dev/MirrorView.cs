using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Animation-test "mirror": a secondary camera renders the LOCAL player's
    /// soldier model into a RenderTexture shown on a quad floating ahead of the
    /// first-person view — you can watch your own body's animations while
    /// playing (the body itself is hidden first-person by
    /// <see cref="NetworkSoldierAnimator"/>).
    ///
    /// Scene wiring is created by "HagenDa/Setup Animation Test Scene"
    /// (Assets/Game/Scenes/AnimationTest.scene). On first frames it:
    ///  1. re-activates the owner's team model on the MirrorBody layer
    ///     (excluded from the first-person camera's culling mask, so only the
    ///     mirror camera sees it) and disables its colliders, and
    ///  2. afterwards billboards the quad ahead of the first-person camera
    ///     (lower part of the screen) and keeps the mirror camera looking at
    ///     the body's front, so turning always shows your own front.
    /// </summary>
    public class MirrorView : MonoBehaviour
    {
        [Header("Wiring")]
        public Camera mirrorCamera;
        public Renderer mirrorQuad;

        [Header("Settings")]
        public string bodyLayerName = "MirrorBody";
        [Tooltip("Quad distance ahead of the first-person camera.")]
        public float mirrorDistance = 2.4f;
        [Tooltip("Quad drop below the view centre (keeps the crosshair clear).")]
        public float mirrorDrop = 0.6f;
        [Tooltip("Mirror camera distance from the body.")]
        public float viewDistance = 2.6f;
        public int textureWidth = 768;
        public int textureHeight = 480;

        private NetworkPlayerController player;
        private HagenDa.Animation.RigDriver.SoldierAnimatorDriver soldier;
        private GameObject bodyModel;
        private Camera fpCamera;
        private bool patched;
        private RenderTexture rt;

        private void Start()
        {
            if (mirrorCamera == null) return;

            rt = new RenderTexture(textureWidth, textureHeight, 24);
            mirrorCamera.targetTexture = rt;

            if (mirrorQuad != null)
            {
                // HDRP project: HDRP/Unlit; fallback for builtin: Unlit/Texture.
                var shader = Shader.Find("HDRP/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Texture");
                var mat = new Material(shader);
                mat.mainTexture = rt;                                   // _MainTex (builtin)
                if (mat.HasProperty("_BaseColorMap"))
                    mat.SetTexture("_BaseColorMap", rt);                // HDRP
                mirrorQuad.material = mat;
            }
        }

        private void OnDestroy()
        {
            if (rt != null)
            {
                rt.Release();
                Destroy(rt);
                rt = null;
            }
        }

        private void LateUpdate()
        {
            // Team model switched (redeploy/team change) — re-patch the new one.
            if (soldier != null && soldier.ActiveModel != bodyModel)
                patched = false;

            if (!patched) TryPatch();
            if (!patched || fpCamera == null) return;

            // 幕布跟随主视角，位于视线前下方（不挡准星）。
            Transform camT = fpCamera.transform;
            mirrorQuad.transform.position =
                camT.position + camT.forward * mirrorDistance - camT.up * mirrorDrop;
            mirrorQuad.transform.rotation = Quaternion.LookRotation(camT.forward);

            // 镜像相机位于玩家面朝方向的前方、回望玩家——镜像显示正面
            // （真实镜子的语义：镜中是自己的正面）。
            Vector3 p = player.transform.position;
            mirrorCamera.transform.position =
                p + player.transform.forward * viewDistance + Vector3.up * 1.2f;
            mirrorCamera.transform.LookAt(p + Vector3.up * 0.9f);
        }

        private void TryPatch()
        {
            var identity = NetworkClient.localPlayer;
            var ctrl = identity != null
                ? identity.GetComponent<NetworkPlayerController>()
                : FindObjectOfType<NetworkPlayerController>();
            if (ctrl == null) return;

            var sold = ctrl.GetComponent<HagenDa.Animation.RigDriver.SoldierAnimatorDriver>();
            var model = sold != null ? sold.ActiveModel : null;
            if (model == null) return;

            int layer = LayerMask.NameToLayer(bodyLayerName);
            fpCamera = ctrl.playerCamera;

            if (layer >= 0)
            {
                SetLayerRecursive(model.transform, layer);
                if (fpCamera != null)
                    fpCamera.cullingMask &= ~(1 << layer);   // 本体只对镜子相机可见
            }

            foreach (var col in model.GetComponentsInChildren<Collider>(true))
                col.enabled = false;   // 观察模型不参与物理与射线

            model.SetActive(true);

            player = ctrl;
            soldier = sold;
            bodyModel = model;
            patched = true;
            Debug.Log($"[MirrorView] patched: model={model.name} layer={(layer >= 0 ? LayerMask.LayerToName(layer) : "<none>")}");
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t)
                SetLayerRecursive(c, layer);
        }
    }
}