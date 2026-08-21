using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE9 AI vision. A target is "seen" only when it is within the forward view
    /// cone (160° total), within the sense range (50m), not blocked by world geometry
    /// and not blocked by sufficiently dense smoke (server-side registry).
    ///
    /// Attack range is unlimited — a target may be attacked from any distance as long
    /// as its position is known (direct vision, the mark system, or squad intel).
    /// </summary>
    public static class VisionSystem
    {
        public const float ViewAngle = 160f;   // total forward cone
        public const float SenseRange = 50f;   // direct perception range

        /// <summary>Eye position for a combatant (approx capsule eye height).</summary>
        public static Vector3 EyePosition(Transform t, float height = 0.8f)
        {
            return t.position + Vector3.up * height;
        }

        /// <summary>
        /// True when the target position is directly visible from the agent.
        /// </summary>
        public static bool CanSee(Transform self, Vector3 targetPos, float eyeHeight = 0.8f)
        {
            Vector3 eye = EyePosition(self, eyeHeight);
            Vector3 to = targetPos - eye;
            float dist = to.magnitude;
            if (dist <= 0.0001f) return true;
            if (dist > SenseRange) return false;

            // Forward cone: target within ViewAngle/2 of forward.
            Vector3 dir = to / dist;
            float angle = Vector3.Angle(self.forward, dir);
            if (angle > ViewAngle * 0.5f) return false;

            // World geometry occlusion (ignore triggers, skip self/target bodies via
            // a simple two-segment check handled by the caller where needed).
            if (Physics.Raycast(eye, dir, out RaycastHit hit, dist,
                                Physics.DefaultRaycastLayers,
                                QueryTriggerInteraction.Ignore))
            {
                // Allow the ray to reach the target's own collider (target body).
                if (hit.collider == null) return false;
                var hitHealth = hit.collider.GetComponentInParent<NetworkPlayerHealth>();
                if (hitHealth == null || !IsPositionNear(hit.collider.transform, targetPos, 0.5f))
                    return false;
            }

            // Dense smoke occlusion.
            if (ServerSmokeRegistry.IsSmokeBlocked(eye, targetPos))
                return false;

            return true;
        }

        private static bool IsPositionNear(Transform t, Vector3 pos, float tol)
        {
            var h = t.GetComponentInParent<NetworkPlayerHealth>();
            return h != null && Vector3.Distance(t.position, pos) <= tol + 1f;
        }
    }
}
