namespace HagenDa.Networking
{
    /// <summary>
    /// Shared server-side damage entry point. Implemented by anything that can be
    /// damaged (players, AI, shootable targets). Combat hitscan and explosions
    /// apply damage through this interface so a single code path covers every
    /// damageable entity.
    /// </summary>
    public interface IDamageable
    {
        void TakeDamage(float damage);
    }
}
