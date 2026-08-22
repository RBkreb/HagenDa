using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Implemented by anything an EMP field interferes with. NetworkEquipment applies
    /// an interference timer (disables EMP-vulnerable equipment); NetworkInterceptor
    /// is destroyed outright.
    /// </summary>
    public interface IEmpTarget
    {
        /// <summary>Apply EMP interference for <paramref name="duration"/> seconds.</summary>
        void ApplyEmp(float duration);
    }

    /// <summary>
    /// Server-authoritative EMP field (PHASE6 电磁脉冲). Spawned by the EMP grenade
    /// on impact; it lives for <see cref="lifetime"/> seconds, and every tick every
    /// entity inside <see cref="radius"/> starts an interference timer (re-armed each
    /// tick it stays inside). Interceptors within radius are destroyed directly.
    /// Clients see a translucent blue sphere via ClientRpc.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkEmpField : NetworkBehaviour
    {
        [Header("EMP")]
        public float radius = 6f;
        public float lifetime = 10f;
        public float interfereDuration = 2f;

        private float spawnTime;

        public override void OnStartServer()
        {
            spawnTime = Time.time;

            // The EMP field must stay at the impact position — disable ALL physics
            // so gravity doesn't make it fall through the ground.
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            var col = GetComponent<Collider>();
            if (col != null) col.enabled = false;
        }

        public override void OnStartClient()
        {
            RpcEmpVisual(transform.position, radius, lifetime);
        }

        private void Update()
        {
            if (!isServer) return;

            if (Time.time - spawnTime >= lifetime)
            {
                NetworkServer.Destroy(gameObject);
                return;
            }

            // Use FindObjectsOfType instead of OverlapSphere + GetComponentInParent<IEmpTarget>:
            // Unity's GetComponentInParent<T> with an interface type is unreliable.
            Vector3 pos = transform.position;
            float r2 = radius * radius;

            foreach (var eq in Object.FindObjectsOfType<NetworkEquipment>())
            {
                if ((eq.transform.position - pos).sqrMagnitude <= r2)
                    eq.ApplyEmp(interfereDuration);
            }

            foreach (var ic in Object.FindObjectsOfType<NetworkInterceptor>())
            {
                if ((ic.transform.position - pos).sqrMagnitude <= r2)
                    ic.ApplyEmp(interfereDuration);
            }

            // PHASE8 感应器：EMP 直接摧毁。
            foreach (var s in Object.FindObjectsOfType<SensorProbe>())
            {
                if ((s.transform.position - pos).sqrMagnitude <= r2)
                    s.ApplyEmp(interfereDuration);
            }

            // PHASE8 部署信标：EMP 直接摧毁。
            foreach (var b in Object.FindObjectsOfType<DeployBeacon>())
            {
                if ((b.transform.position - pos).sqrMagnitude <= r2)
                    b.ApplyEmp(interfereDuration);
            }
        }

        [ClientRpc]
        private void RpcEmpVisual(Vector3 pos, float radius, float lifetime)
        {
            EmpFieldVisual.Spawn(pos, radius, lifetime);
        }
    }

    /// <summary>
    /// Client-only translucent blue sphere for the EMP field. No network identity —
    /// it is spawned identically on each client via ClientRpc.
    /// </summary>
    public class EmpFieldVisual : MonoBehaviour
    {
        private float startTime;
        private float lifetime;

        public static void Spawn(Vector3 pos, float radius, float lifetime)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "EmpField";
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * radius;

            var col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false;

            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                bool hdrp = IsHDRP();
                var mat = new Material(hdrp ? Shader.Find("HDRP/Unlit") : Shader.Find("Sprites/Default"));
                if (mat == null || mat.shader == null)
                {
                    Shader s = Shader.Find("Sprites/Default");
                    if (s == null) s = Shader.Find("Unlit/Transparent");
                    if (s != null) mat = new Material(s);
                }

                if (mat != null)
                {
                    if (hdrp)
                    {
                        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        mat.SetFloat("_SurfaceType", 1f);
                        mat.SetFloat("_BlendMode", 0f);
                        mat.SetFloat("_AlphaCutoffEnable", 0f);
                        mat.SetOverrideTag("RenderType", "Transparent");
                        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        mat.SetInt("_ZWrite", 0);
                        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                        mat.SetFloat("_CullMode", 0f);
                        mat.SetFloat("_CullModeForward", 0f);
                        mat.SetFloat("_TransparentCullMode", 0f);
                        mat.SetFloat("_OpaqueCullMode", 0f);
                        mat.EnableKeyword("_DOUBLESIDED_ON");
                    }
                    Color blue = new Color(0.2f, 0.45f, 1f, 0.25f);
                    mat.SetColor("_BaseColor", blue);
                    mat.SetColor("_UnlitColor", blue);
                    mat.color = blue;
                    rend.material = mat;
                }
            }

            var vis = go.AddComponent<EmpFieldVisual>();
            vis.startTime = Time.time;
            vis.lifetime = lifetime;
        }

        private void Update()
        {
            float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(0.001f, lifetime));
            if (t >= 1f)
                Destroy(gameObject);
        }

        private static bool IsHDRP()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return false;
            var t = pipeline.GetType();
            return t.FullName.Contains("HighDefinition") || t.Name.Contains("HDRenderPipeline");
        }
    }
}
