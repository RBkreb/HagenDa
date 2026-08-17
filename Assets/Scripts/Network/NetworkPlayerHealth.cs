using Mirror;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative health/armor for players and AI (PHASE4).
    ///
    ///  - Health max 100; after 8s with no health loss it regenerates 5 HP every
    ///    0.5s until full.
    ///  - Armor (default 0) absorbs damage before health and never regenerates.
    ///  - Death: the entity goes prone, all 3C + combat is disabled, and no further
    ///    damage is received. There is no auto-respawn; a RescueThrowable (or the H
    ///    test key) revives it, keeping the prone posture and restoring 100 HP.
    /// </summary>
    public class NetworkPlayerHealth : NetworkBehaviour, IDamageable
    {
        [Header("Health")]
        public float maxHealth = 100f;

        [Header("Regeneration")]
        [Tooltip("Seconds without damage before natural regen starts.")]
        public float regenDelay = 8f;
        [Tooltip("Seconds between regen ticks.")]
        public float regenInterval = 0.5f;
        [Tooltip("Health restored per regen tick.")]
        public float regenAmount = 5f;

        [SyncVar(hook = nameof(OnHealthChanged))]
        public float health;

        [SyncVar(hook = nameof(OnArmorChanged))]
        public float armor;

        public bool IsDead => health <= 0f;

        private bool deathHandled;   // server-side idempotency guard (health is already 0 when Die runs)
        private float lastDamageTime;
        private float regenAccumulator;

        public override void OnStartServer()
        {
            health = maxHealth;
            armor = 0f;
            lastDamageTime = Time.time;
            deathHandled = false;
        }

        // ---------------------------------------------------------------
        // DAMAGE
        // ---------------------------------------------------------------

        /// <summary>Full-body / area damage (explosions).</summary>
        [Server]
        public void TakeDamage(float damage)
        {
            TakeDamageInternal(damage, 1f);
        }

        /// <summary>Hitbox-aware damage (bullets).</summary>
        [Server]
        public void TakeDamage(float damage, Vector3 hitPoint)
        {
            float mult = HitboxUtility.GetMultiplier(GetActiveCapsule(), hitPoint);
            TakeDamageInternal(damage, mult);
        }

        [Server]
        private void TakeDamageInternal(float damage, float multiplier)
        {
            if (IsDead) return;   // dead entities receive no further damage
            if (multiplier <= 0f) return;

            float amount = Mathf.Abs(damage) * multiplier;

            // Armor absorbs first, health absorbs the remainder.
            float remaining = amount;
            if (armor > 0f)
            {
                float absorbed = Mathf.Min(armor, remaining);
                armor -= absorbed;
                remaining -= absorbed;
            }

            if (remaining > 0f)
            {
                health = Mathf.Max(0f, health - remaining);
                // Regen is gated on health *actually decreasing*, not on taking
                // damage: armor-only damage must not reset the regen timer.
                lastDamageTime = Time.time;
            }

            if (health <= 0f)
                Die();
        }

        [Server]
        public void AddArmor(float amount)
        {
            armor += amount;
        }

        // ---------------------------------------------------------------
        // REGENERATION (server)
        // ---------------------------------------------------------------

        private void Update()
        {
            if (!isServer) return;
            if (IsDead) return;
            if (health >= maxHealth) return;

            if (Time.time - lastDamageTime >= regenDelay)
            {
                regenAccumulator += Time.deltaTime;
                while (regenAccumulator >= regenInterval)
                {
                    regenAccumulator -= regenInterval;
                    health = Mathf.Min(maxHealth, health + regenAmount);
                    if (health >= maxHealth) break;
                }
            }
            else
            {
                regenAccumulator = 0f;
            }
        }

        // ---------------------------------------------------------------
        // DEATH / RESCUE
        // ---------------------------------------------------------------

        [Server]
        public void Die()
        {
            if (deathHandled) return;
            deathHandled = true;
            health = 0f;
            SetDeadState(true);
            RpcDie();
        }

        [Server]
        public void Rescue()
        {
            if (!deathHandled) return;
            deathHandled = false;
            health = maxHealth;
            SetDeadState(false);
            RpcRescue();
        }

        /// <summary>Disable/re-enable 3C and combat on the owning controller (player or AI).</summary>
        [Server]
        private void SetDeadState(bool dead)
        {
            var controller = GetComponent<NetworkPlayerController>();
            if (controller != null) controller.SetDead(dead);

            var ai = GetComponent<NetworkAIController>();
            if (ai != null) ai.SetDead(dead);
        }

        private CapsuleCollider GetActiveCapsule()
        {
            var colliders = GetComponents<CapsuleCollider>();
            foreach (var c in colliders)
                if (c.enabled) return c;
            return colliders.Length > 0 ? colliders[0] : null;
        }

        // ---------------------------------------------------------------
        // CLIENT FEEDBACK HOOKS
        // ---------------------------------------------------------------

        [ClientRpc]
        private void RpcDie() { }

        [ClientRpc]
        private void RpcRescue() { }

        private void OnHealthChanged(float oldValue, float newValue) { }
        private void OnArmorChanged(float oldValue, float newValue) { }
    }
}
