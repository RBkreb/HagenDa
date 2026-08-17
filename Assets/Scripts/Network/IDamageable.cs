using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Shared server-side damage entry point. Implemented by anything that can be
    /// damaged (players, AI, shootable targets). Explosions and bullets apply damage
    /// through this interface so a single code path covers every damageable entity.
    /// </summary>
    public interface IDamageable
    {
        /// <summary>
        /// Full-body / area damage (explosions). No hitbox multiplier is applied.
        /// </summary>
        void TakeDamage(float damage);

        /// <summary>
        /// Hitbox-aware damage (bullets). The multiplier is derived from the hit
        /// point's position on the entity's capsule (head 2x / body 1x / legs 0.5x).
        /// </summary>
        void TakeDamage(float damage, Vector3 hitPoint);
    }
}
