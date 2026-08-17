using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Client-side smoke cloud: a transparent sphere with an alpha-blended material,
    /// whose alpha linearly decreases from concentration to zero over decayTime.
    /// Purely visual (spawned identically on each client via ClientRpc), so it needs
    /// no network identity.
    /// </summary>
    public class NetworkSmoke : MonoBehaviour
    {
        public float concentration = 0.6f;
        public float radius = 5f;
        public float decayTime = 5f;

        private Material mat;
        private float startTime;
        private bool hdrp;

        public static NetworkSmoke Spawn(Vector3 pos, float concentration, float radius, float decayTime)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Smoke";
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * radius;

            var col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false;

            var smoke = go.AddComponent<NetworkSmoke>();
            smoke.concentration = concentration;
            smoke.radius = radius;
            smoke.decayTime = decayTime;

            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                smoke.hdrp = IsHDRP();
                smoke.mat = new Material(smoke.hdrp ? Shader.Find("HDRP/Unlit") : Shader.Find("Sprites/Default"));
                if (smoke.mat == null || smoke.mat.shader == null)
                {
                    // fallback chain
                    Shader s = Shader.Find("Sprites/Default");
                    if (s == null) s = Shader.Find("Unlit/Transparent");
                    if (s != null) smoke.mat = new Material(s);
                }

                if (smoke.mat != null && smoke.hdrp)
                {
                    // HDRP/Unlit needs explicit transparent surface setup.
                    smoke.mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    smoke.mat.SetFloat("_SurfaceType", 1f);          // 1 = transparent
                    smoke.mat.SetFloat("_BlendMode", 0f);            // alpha blend
                    smoke.mat.SetFloat("_AlphaCutoffEnable", 0f);
                    smoke.mat.SetOverrideTag("RenderType", "Transparent");
                    smoke.mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    smoke.mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    smoke.mat.SetInt("_ZWrite", 0);
                    smoke.mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                }

                rend.material = smoke.mat;
                smoke.ApplyAlpha(concentration);
            }

            smoke.startTime = Time.time;
            return smoke;
        }

        private void Update()
        {
            float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(0.001f, decayTime));
            float alpha = concentration * (1f - t);
            ApplyAlpha(alpha);

            if (t >= 1f)
                Destroy(gameObject);
        }

        private void ApplyAlpha(float alpha)
        {
            if (mat == null) return;

            if (hdrp)
            {
                // HDRP/Unlit main colour is _UnlitColor; also set _BaseColor/_Color
                mat.SetColor("_UnlitColor", new Color(0.85f, 0.85f, 0.85f, alpha));
                mat.SetColor("_BaseColor", new Color(0.85f, 0.85f, 0.85f, alpha));
            }
            mat.color = new Color(0.85f, 0.85f, 0.85f, alpha);
        }

        private static bool IsHDRP()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return false;
            // NB: GetType().Name is "HDRenderPipelineAsset" (namespace not included),
            // so checking Name for "HighDefinition" always fails. Check FullName too.
            var t = pipeline.GetType();
            return t.FullName.Contains("HighDefinition") || t.Name.Contains("HDRenderPipeline");
        }
    }
}
