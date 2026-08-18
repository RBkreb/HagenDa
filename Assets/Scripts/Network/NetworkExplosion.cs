using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-side explosion unit: radial damage with linear falloff and raycast
    /// cover detection, plus a client-side visual (yellow transient light + default
    /// particle burst).
    /// </summary>
    public static class ExplosionUtility
    {
        /// <summary>
        /// Apply explosion damage (server only). <paramref name="yield"/> is the
        /// centre damage; damage falls linearly to zero at <paramref name="radius"/>.
        /// Entities behind cover (a ray from the centre hits something other than
        /// the entity first) take no damage.
        /// </summary>
        public static void ApplyDamage(Vector3 center, float yield, float radius)
        {
            if (radius <= 0f) return;

            var colliders = Physics.OverlapSphere(center, radius);
            var damaged = new HashSet<IDamageable>();

            foreach (var c in colliders)
            {
                var target = c.GetComponentInParent<IDamageable>();
                if (target == null || damaged.Contains(target)) continue;

                Vector3 entityCenter = c.bounds.center;
                Vector3 to = entityCenter - center;
                float dist = to.magnitude;
                if (dist > radius) continue;

                if (dist <= 0.0001f)
                {
                    damaged.Add(target);
                    target.TakeDamage(yield);
                    continue;
                }

                Vector3 dir = to / dist;

                // Raycast occlusion: skip if something other than the entity blocks
                // the ray before it reaches the entity. Special cover (PHASE6 特殊掩体)
                // is penetrable by explosions, so it is filtered out of the blockers.
                var hits = Physics.RaycastAll(center, dir, dist + 0.05f,
                                              Physics.DefaultRaycastLayers,
                                              QueryTriggerInteraction.Ignore);

                bool blocked = false;
                foreach (var h in hits)
                {
                    var blocker = h.collider.GetComponentInParent<IDamageable>();
                    if (blocker != null && blocker == target) continue; // the entity itself

                    if (h.collider.GetComponentInParent<SpecialCover>() != null) continue; // 特殊掩体可穿透

                    blocked = true;
                    break;
                }

                if (blocked) continue;

                float falloff = 1f - dist / radius;
                float damage = yield * falloff;
                if (damage > 0f)
                {
                    damaged.Add(target);
                    target.TakeDamage(damage);
                }
            }
        }

        /// <summary>
        /// Client-side visual: a yellow transient point light + a default particle
        /// burst. HDRP-aware: light intensity is in candela (much higher) and the
        /// particle renderer needs an HDRP particle shader (built-in particle
        /// materials are not rendered by HDRP).
        /// </summary>
        public static void SpawnVisual(Vector3 pos, float radius)
        {
            bool hdrp = IsHDRP();

            var lightGo = new GameObject("ExplosionLight");
            lightGo.transform.position = pos + Vector3.up * 1f;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.75f, 0.1f);
            light.range = radius * 2f;
            light.intensity = hdrp ? 8000f : 8f;

            if (hdrp) AttachHDRPLightData(lightGo);

            Object.Destroy(lightGo, 0.6f);

            var pGo = new GameObject("ExplosionParticles");
            pGo.transform.position = pos;
            var ps = pGo.AddComponent<ParticleSystem>();

            // AddComponent starts the system immediately (playOnAwake defaults to
            // true); duration cannot be set while playing (throws an Assert).
            // Stop & clear first, configure everything, then play manually.
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.duration = 0.4f;
            main.loop = false;
            main.startLifetime = 0.8f;
            main.startSpeed = 6f;
            main.startSize = 0.5f;
            main.startColor = new Color(1f, 0.6f, 0.1f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 60) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.5f;

            // Built-in default particle material is invisible under HDRP — assign a
            // pipeline-appropriate unlit material. "HDRP/Particles/Unlit" is not
            // present in this project; HDRP/Unlit renders particle quads fine.
            var psr = pGo.GetComponent<ParticleSystemRenderer>();
            if (psr != null)
            {
                Shader s = hdrp
                    ? Shader.Find("HDRP/Unlit")
                    : Shader.Find("Sprites/Default");
                if (s == null) s = Shader.Find("Sprites/Default");
                if (s != null)
                {
                    var pMat = new Material(s);
                    if (hdrp)
                    {
                        pMat.SetColor("_BaseColor", new Color(1f, 0.6f, 0.1f));
                        pMat.SetColor("_UnlitColor", new Color(1f, 0.6f, 0.1f));
                    }
                    pMat.SetColor("_Color", new Color(1f, 0.6f, 0.1f));
                    psr.material = pMat;
                }
            }

            ps.Play();

            Object.Destroy(pGo, 1.5f);
        }

        // Best-effort: attach HDRP HDAdditionalLightData via reflection (avoids a
        // hard compile-time dependency on the HDRP package), same pattern as
        // NetworkSetup.EnsureLighting.
        private static void AttachHDRPLightData(GameObject lightGo)
        {
            var hdType = System.Type.GetType(
                "UnityEngine.Rendering.HighDefinition.HDAdditionalLightData, Unity.RenderPipelines.HighDefinition.Runtime");
            if (hdType != null && lightGo.GetComponent(hdType) == null)
                lightGo.AddComponent(hdType);
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
