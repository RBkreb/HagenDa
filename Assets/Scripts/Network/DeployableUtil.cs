using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Shared helpers for deployables (PHASE6): large supply crate, interceptor,
    /// supply pack. These devices must NOT collide with living bodies — they only
    /// rest against world rigidbodies (ground / walls) — but must still be placed
    /// and interact with the world.
    /// </summary>
    public static class DeployableUtil
    {
        /// <summary>Ignore all collisions between <paramref name="self"/> and every living body.</summary>
        public static void IgnoreLivingCollision(GameObject self)
        {
            var own = self.GetComponentsInChildren<Collider>(true);
            foreach (var h in Object.FindObjectsOfType<NetworkPlayerHealth>())
            {
                var hcs = h.GetComponentsInChildren<Collider>(true);
                foreach (var hc in hcs)
                    foreach (var oc in own)
                        Physics.IgnoreCollision(oc, hc, true);
            }
        }
    }
}
