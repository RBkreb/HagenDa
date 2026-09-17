using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Simplified multi-hitbox damage model (PHASE4): a capsule is split into a top
    /// hemisphere (head, 2x), a middle cylinder (body, 1x) and a bottom hemisphere
    /// (legs, 0.5x). Only applies to upright (Y-axis) capsules; a lying (prone)
    /// capsule falls back to 1x everywhere.
    /// </summary>
    public static class HitboxUtility
    {
        public const float HeadMultiplier = 2f;
        public const float BodyMultiplier = 1f;
        public const float LegMultiplier = 0.5f;

        /// <summary>Return the damage multiplier for a hit point on a capsule.</summary>
        public static float GetMultiplier(CapsuleCollider capsule, Vector3 worldPoint)
        {
            if (capsule == null) return BodyMultiplier;
            if (capsule.direction != 1) return BodyMultiplier; // not upright (prone)

            Vector3 local = capsule.transform.InverseTransformPoint(worldPoint);
            float radius = capsule.radius;
            // Half-length of the straight cylinder section between the two hemispheres.
            float halfCylinder = capsule.height * 0.5f - radius;

            float y = local.y - capsule.center.y;

            if (y > halfCylinder) return HeadMultiplier;   // top hemisphere
            if (y < -halfCylinder) return LegMultiplier;   // bottom hemisphere
            return BodyMultiplier;                          // middle cylinder
        }
    }
}
