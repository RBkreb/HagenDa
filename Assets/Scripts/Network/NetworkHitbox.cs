using UnityEngine;

namespace HagenDa.Networking
{
    public enum HitboxPart
    {
        Body,
        Head,
        Limb
    }

    /// <summary>
    /// Marks a trigger collider as a bullet damage hitbox (PHASE12 士兵命中部位).
    /// Lives on the soldier model's bone-attached trigger colliders (head / body /
    /// arms / legs). <see cref="NetworkBullet"/> raycasts against these triggers
    /// (QueryTriggerInteraction.Collide) and applies the part multiplier; the
    /// entity's movement capsules are excluded from bullet rays whenever the
    /// entity has any hitbox (see <see cref="NetworkPlayerHealth.HasHitboxes"/>).
    /// </summary>
    public class NetworkHitbox : MonoBehaviour
    {
        public HitboxPart part = HitboxPart.Body;

        public static float GetMultiplier(HitboxPart part)
        {
            switch (part)
            {
                case HitboxPart.Head: return HitboxUtility.HeadMultiplier;   // 2x
                case HitboxPart.Limb: return HitboxUtility.LegMultiplier;    // 0.5x
                default: return HitboxUtility.BodyMultiplier;                // 1x
            }
        }
    }
}
