using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Throwable that revives downed (dead) players/AI within a radius. On impact it
    /// finds every dead NetworkPlayerHealth nearby and calls Rescue(), which restores
    /// 100 HP and re-enables 3C + combat while keeping the prone posture.
    /// </summary>
    public class RescueThrowable : NetworkThrowable
    {
        [Header("Rescue")]
        public float rescueRadius = 3f;

        protected override void OnImpact()
        {
            var colliders = Physics.OverlapSphere(transform.position, rescueRadius);
            foreach (var c in colliders)
            {
                var health = c.GetComponentInParent<NetworkPlayerHealth>();
                if (health != null && health.IsDead)
                    health.Rescue();
            }

            RpcRescueVisual(transform.position);
        }

        [ClientRpc]
        private void RpcRescueVisual(Vector3 pos)
        {
            // Simple visual: a green flash (mirrors the explosion light pattern).
            bool hdrp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null;

            var lightGo = new GameObject("RescueLight");
            lightGo.transform.position = pos + Vector3.up * 1f;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.3f, 1f, 0.4f);
            light.range = rescueRadius * 2f;
            light.intensity = hdrp ? 8000f : 8f;
            Object.Destroy(lightGo, 0.6f);
        }
    }
}
