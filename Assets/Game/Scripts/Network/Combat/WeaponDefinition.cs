using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Fire selector. A weapon may expose several of these; the player cycles them
    /// with the fire-mode key (V by default).
    /// </summary>
    public enum FireMode
    {
        Auto,    // hold to keep firing at the weapon's fire rate
        Semi,    // one shot per trigger press
        Burst,   // a fixed burst per trigger press
        Bolt     // one shot, then an automatic bolt cycle before the next shot
    }

    /// <summary>
    /// Data-driven gun template (PHASE5). A single ScriptableObject holds every
    /// tunable of a firearm so the same <see cref="NetworkGun"/> runtime code can
    /// drive any weapon, and so the player and the AI share one configuration.
    ///
    /// All angles are in degrees; distances in metres; time in seconds; speed in m/s.
    /// </summary>
    [CreateAssetMenu(menuName = "HagenDa/Weapon Definition", fileName = "WeaponDefinition")]
    public class WeaponDefinition : ScriptableObject
    {
        [Header("Fire rate")]
        [Tooltip("Rounds per minute. Default 900 so full-auto bloom (0.5°/shot) outpaces the 7°/s recovery.")]
        public float fireRateRPM = 900f;

        [Header("Fire modes")]
        [Tooltip("Available selectors, cycled with the fire-mode key. Default: Auto, Burst, Semi.")]
        public List<FireMode> fireModes = new List<FireMode> { FireMode.Auto, FireMode.Burst, FireMode.Semi };

        [Tooltip("Shots per trigger press in Burst mode.")]
        public int burstCount = 3;

        [Tooltip("Bolt-action cycle time (seconds) between shots in Bolt mode.")]
        public float boltTime = 0.6f;

        [Header("Ammo")]
        [Tooltip("Rounds held in the magazine when fully loaded.")]
        public int magazineCapacity = 30;

        [Tooltip("Rounds carried in reserve (not in the magazine).")]
        public int reserveCapacity = 450;

        [Header("Damage")]
        [Tooltip("Base damage at zero distance.")]
        public float baseDamage = 30f;

        [Tooltip("Minimum damage floor (baseDamage > minDamage).")]
        public float minDamage = 15f;

        [Tooltip("Damage decay per metre travelled (1/120).")]
        public float damageDecay = 1f / 120f;

        [Header("Projectile")]
        [Tooltip("Muzzle velocity (m/s).")]
        public float bulletSpeed = 800f;

        [Tooltip("Constant deceleration applied each second (m/s^2).")]
        public float bulletDeceleration = 300f;

        [Tooltip("Seconds before a bullet expires back into the pool.")]
        public float bulletLifetime = 5f;

        [Header("Reload")]
        [Tooltip("Tactical (magazine still has rounds) reload time.")]
        public float reloadFastTime = 2f;

        [Tooltip("Empty-magazine reload time.")]
        public float reloadSlowTime = 2.5f;

        [Header("Spread (degrees)")]
        [Tooltip("Minimum bloom when fully un-aimed (hip).")]
        public float hipSpreadMin = 3f;

        [Tooltip("Maximum bloom when fully un-aimed (hip).")]
        public float hipSpreadMax = 10f;

        [Tooltip("Minimum bloom when fully aimed down sights.")]
        public float adsSpreadMin = 0.1f;

        [Tooltip("Maximum bloom when fully aimed down sights.")]
        public float adsSpreadMax = 5f;

        [Tooltip("Bloom added per shot.")]
        public float spreadPerShot = 0.5f;

        [Tooltip("Bloom recovery per second (continuous shrink).")]
        public float spreadRecovery = 6f;

        [Tooltip("Seconds to transition between hip and fully aimed (aim time).")]
        public float aimTime = 0.25f;

        [Header("Recoil (degrees)")]
        [Tooltip("Screen recoil added per shot.")]
        public float screenRecoilPerShot = 1f;

        [Tooltip("Screen recoil direction (0 = right, 90 = straight up).")]
        public float screenRecoilAngle = 90f;

        [Tooltip("Screen recoil recovery per second.")]
        public float recoilRecovery = 0f;

        [Header("Sprint -> fire")]
        [Tooltip("Hold-to-fire time required after leaving sprint before shots start.")]
        public float sprintFireCooldown = 0.1f;

        /// <summary>Seconds between shots at the configured fire rate.</summary>
        public float FireInterval => fireRateRPM > 0f ? 60f / fireRateRPM : 0f;
    }
}
