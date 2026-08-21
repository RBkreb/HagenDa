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

        /// <summary>Eye position just above the capsule top, so the sight ray does not
        /// immediately hit the agent's own collider.</summary>
        public static Vector3 EyePosition(Transform t)
        {
            return t.position + Vector3.up * 1.9f;
        }

        /// <summary>
        /// True when <paramref name="target"/> is directly visible from the agent.
        /// The ray starts above the agent's own capsule and may only be stopped by
        /// world geometry or a third body (the target's own body is allowed).
        /// </summary>
        public static bool CanSee(Transform self, Transform target)
        {
            Vector3 eye = EyePosition(self);
            Vector3 targetPos = target.position + Vector3.up * 1.0f;   // target body centre-ish
            Vector3 to = targetPos - eye;
            float dist = to.magnitude;
            if (dist <= 0.0001f) return true;
            if (dist > SenseRange) return false;

            // Forward cone: target within ViewAngle/2 of forward.
            Vector3 dir = to / dist;
            float angle = Vector3.Angle(self.forward, dir);
            if (angle > ViewAngle * 0.5f) return false;

            // Geometry occlusion: ignore all living bodies (friend OR foe) — only
            // non-entity geometry (walls, obstacles) blocks sight. This avoids the
            // "crowded capture point" case where a ray hits a friendly between the
            // AI and its target and is wrongly treated as occlusion.
            var hits = Physics.RaycastAll(eye, dir, dist,
                                Physics.DefaultRaycastLayers,
                                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var health = hits[i].collider.GetComponentInParent<NetworkPlayerHealth>();
                if (health != null) continue;   // living body: pass through
                return false;                    // wall / obstacle: blocked
            }

            // Dense smoke occlusion.
            if (ServerSmokeRegistry.IsSmokeBlocked(eye, targetPos))
                return false;

            return true;
        }

        // ---------------------------------------------------------------
        // AIM-POINT SELECTION (foot / body / head)
        // ---------------------------------------------------------------

        /// <summary>Candidate aim heights relative to the target's capsule base.</summary>
        public static readonly float[] AimHeights = { 0.2f, 1.0f, 1.6f };   // foot, body, head

        /// <summary>
        /// Pick an aim point on the target that is actually visible from the agent.
        /// When the target is fully exposed this favours body/head (weighted random);
        /// when only part of the target is exposed (e.g. just the head above cover)
        /// only the visible points are candidates — so the AI shoots the head instead
        /// of wasting rounds into the cover.
        /// </summary>
        public static Vector3 GetVisibleAimPoint(Transform self, Transform target)
        {
            Vector3 eye = EyePosition(self);

            var points = new Vector3[AimHeights.Length];
            var visible = new bool[AimHeights.Length];
            for (int i = 0; i < AimHeights.Length; i++)
            {
                points[i] = target.position + Vector3.up * AimHeights[i];
                visible[i] = IsPointVisible(eye, points[i]);
            }

            // Weights bias toward body then head when all are exposed.
            float[] weights = { 0.2f, 0.5f, 0.3f };   // foot, body, head

            float total = 0f;
            for (int i = 0; i < 3; i++)
                if (visible[i]) total += weights[i];

            // Nothing visible (should not happen for a valid target): fall back to body.
            if (total <= 0.0001f)
                return points[1];

            float roll = Random.value * total;
            float acc = 0f;
            for (int i = 0; i < 3; i++)
            {
                if (!visible[i]) continue;
                acc += weights[i];
                if (roll <= acc) return points[i];
            }
            return points[1];
        }

        /// <summary>True when the point <paramref name="to"/> is not blocked by world
        /// geometry from <paramref name="from"/> (living bodies are ignored).</summary>
        public static bool IsPointVisible(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist <= 0.0001f) return true;
            dir /= dist;

            var hits = Physics.RaycastAll(from, dir, dist,
                                Physics.DefaultRaycastLayers,
                                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider.GetComponentInParent<NetworkPlayerHealth>() != null)
                    continue;   // living body: pass through
                return false;    // wall / obstacle: blocked
            }
            return true;
        }
    }
}
