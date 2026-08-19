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
    ///    damage is received. A RescueThrowable (or the H test key) revives it during
    ///    the down window; after 10s (PHASE7) the entity redeploys (player chooses a
    ///    point, AI picks GR/HQ). GR force-kill is unrevivable.
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

        [Tooltip("Blast shield sets this to 0.4 (60% explosion reduction). Default 1.")]
        public float explosionDamageMultiplier = 1f;

        private float buffRegenPerSecond;   // >0 = syringe / supply-pack temporary regen

        // ---------------------------------------------------------------
        // PHASE7 match / redeploy
        // ---------------------------------------------------------------
        [Tooltip("最近一次造成伤害的实体（击杀归属）。")]
        public NetworkCombatant lastAttacker;

        [Tooltip("GR 强制击杀：不可被救援。")]
        public bool unrevivable;

        [SyncVar] public bool awaitingRedeploy;   // 死亡 10s 后进入部署菜单

        private Rigidbody rb;
        private float redeployDeadline;           // server-only
        public float RedeployDelay => redeployDeadline - Time.time;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
        }

        public override void OnStartServer()
        {
            health = maxHealth;
            armor = 0f;
            lastDamageTime = Time.time;
            deathHandled = false;
            unrevivable = false;
            awaitingRedeploy = false;
            lastAttacker = null;
        }

        // ---------------------------------------------------------------
        // DAMAGE
        // ---------------------------------------------------------------

        /// <summary>Full-body / area damage (explosions).</summary>
        [Server]
        public void TakeDamage(float damage)
        {
            TakeDamageInternal(damage * explosionDamageMultiplier, 1f);
        }

        /// <summary>Hitbox-aware damage (bullets).</summary>
        [Server]
        public void TakeDamage(float damage, Vector3 hitPoint)
        {
            float mult = HitboxUtility.GetMultiplier(GetActiveCapsule(), hitPoint);
            TakeDamageInternal(damage, mult);
        }

        /// <summary>Area damage with attacker attribution (PHASE7 击杀归属).</summary>
        [Server]
        public void TakeDamage(float damage, NetworkCombatant attacker)
        {
            lastAttacker = attacker;
            TakeDamageInternal(damage * explosionDamageMultiplier, 1f);
        }

        /// <summary>Hitbox-aware damage with attacker attribution (PHASE7 击杀归属).</summary>
        [Server]
        public void TakeDamage(float damage, Vector3 hitPoint, NetworkCombatant attacker)
        {
            lastAttacker = attacker;
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

                // 回复中被伤害则去除回复 buff（治疗针 / 补给包）。
                buffRegenPerSecond = 0f;
            }

            if (health <= 0f)
                Die();
        }

        [Server]
        public void AddArmor(float amount)
        {
            armor += amount;
        }

        /// <summary>Restore health (大型补给箱). Capped at maxHealth; dead entities ignored.</summary>
        [Server]
        public void Heal(float amount)
        {
            if (IsDead) return;
            health = Mathf.Min(maxHealth, health + Mathf.Abs(amount));
        }

        /// <summary>
        /// PHASE6: start a temporary HP-per-second regen buff (治疗针 / 补给包).
        /// Removed on taking damage.
        /// </summary>
        [Server]
        public void StartBuffRegen(float hpPerSecond)
        {
            buffRegenPerSecond = Mathf.Max(0f, hpPerSecond);
        }

        // ---------------------------------------------------------------
        // REGENERATION (server)
        // ---------------------------------------------------------------

        private void Update()
        {
            if (!isServer) return;

            HandleRedeploy();

            if (IsDead) return;
            if (health >= maxHealth)
            {
                buffRegenPerSecond = 0f;
                return;
            }

            // Buff regen (治疗针 / 补给包): continuous HP/s, independent of the
            // natural regen delay.
            if (buffRegenPerSecond > 0f)
            {
                health = Mathf.Min(maxHealth, health + buffRegenPerSecond * Time.deltaTime);
                if (health >= maxHealth)
                    buffRegenPerSecond = 0f;
            }

            // Natural regen (unchanged).
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
            DieInternal();
        }

        /// <summary>GR 强制击杀：不可救援（PHASE7）。</summary>
        [Server]
        public void ForceKill()
        {
            if (deathHandled) return;
            unrevivable = true;
            DieInternal();
        }

        [Server]
        private void DieInternal()
        {
            if (deathHandled) return;
            deathHandled = true;
            health = 0f;
            awaitingRedeploy = false;
            SetDeadState(true);
            RpcDie();

            // 击杀归属计分（仅敌方击杀）。
            var self = GetComponent<NetworkCombatant>();
            NetworkMatchManager.Instance?.ReportKill(lastAttacker, self);

            // 死亡 10s 后可重新部署。
            var mm = NetworkMatchManager.Instance;
            redeployDeadline = Time.time + (mm != null ? mm.redeployDelay : 10f);
        }

        [Server]
        public void Rescue()
        {
            if (!deathHandled) return;
            if (unrevivable) return;   // GR 强制击杀不可救援

            deathHandled = false;
            awaitingRedeploy = false;
            health = maxHealth;
            lastAttacker = null;
            SetDeadState(false);
            RpcRescue();
        }

        // ---------------------------------------------------------------
        // PHASE7 REDEPLOY
        // ---------------------------------------------------------------

        [Server]
        private void HandleRedeploy()
        {
            if (!deathHandled) return;
            if (Time.time < redeployDeadline) return;

            var mm = NetworkMatchManager.Instance;
            if (mm == null) return;
            if (mm.matchOver) return;   // 对局结束：不再重新部署

            bool isHuman = connectionToClient != null;

            if (!isHuman)
            {
                // AI：随机 GR 或 HQ 自动部署。
                Vector3? pos = AiChooseDeploy();
                if (pos.HasValue) DoRedeploy(pos.Value);
                return;
            }

            // 玩家：进入部署菜单，无操作超时后自动部署回 GR。
            awaitingRedeploy = true;

            if (Time.time >= redeployDeadline + (mm.autoDeployTimeout))
            {
                Vector3? pos = mm.GetGarrisonDeployPoint(MyTeam());
                if (pos.HasValue) DoRedeploy(pos.Value);
            }
        }

        [Server]
        private Vector3? AiChooseDeploy()
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null) return null;

            if (Random.value < 0.5f)
            {
                var hq = mm.GetHqDeployPoint(MyTeam());
                if (hq.HasValue) return hq;
            }
            return mm.GetGarrisonDeployPoint(MyTeam());
        }

        /// <summary>Player deploy choice (1=GR / 2=HQ / 3=squad).</summary>
        [Server]
        public void RequestDeploy(int choice)
        {
            if (!deathHandled) return;
            if (Time.time < redeployDeadline) return;

            var mm = NetworkMatchManager.Instance;
            if (mm == null) return;

            var self = GetComponent<NetworkCombatant>();
            int team = self != null && self.teamId >= 0 ? self.teamId : (int)MatchTeam.Red;
            int squad = self != null ? self.squadId : -1;

            Vector3? pos = null;
            switch (choice)
            {
                case 1: pos = mm.GetGarrisonDeployPoint((MatchTeam)team); break;
                case 2: pos = mm.GetHqDeployPoint((MatchTeam)team); break;
                case 3: pos = mm.GetSquadDeployPoint(team, squad, self); break;
            }

            if (!pos.HasValue)
                pos = mm.GetGarrisonDeployPoint((MatchTeam)team);   // fallback GR
            if (!pos.HasValue) return;

            DoRedeploy(pos.Value);
        }

        [Server]
        private void DoRedeploy(Vector3 pos)
        {
            deathHandled = false;
            unrevivable = false;
            awaitingRedeploy = false;
            lastAttacker = null;
            health = maxHealth;
            armor = 0f;

            // 传送：先清动量再设位置。
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = pos;
            }
            else
            {
                transform.position = pos;
            }

            SetDeadState(false);

            var controller = GetComponent<NetworkPlayerController>();
            if (controller != null) controller.OnRedeploy();

            var ai = GetComponent<NetworkAIController>();
            if (ai != null) ai.OnRedeploy(pos);

            RpcRedeploy();
        }

        private MatchTeam MyTeam()
        {
            var c = GetComponent<NetworkCombatant>();
            return c != null && c.teamId >= 0 ? (MatchTeam)c.teamId : MatchTeam.Red;
        }

        [ClientRpc]
        private void RpcRedeploy() { }

        /// <summary>Disable/re-enable 3C and combat on the owning controller (player or AI).</summary>
        [Server]
        private void SetDeadState(bool dead)
        {
            var controller = GetComponent<NetworkPlayerController>();
            if (controller != null) controller.SetDead(dead);

            var ai = GetComponent<NetworkAIController>();
            if (ai != null) ai.SetDead(dead);
        }

        /// <summary>The enabled capsule collider (stand/crouch/prone), used by bullets
        /// to derive the hitbox damage multiplier. Public for <see cref="NetworkBullet"/>.</summary>
        public CapsuleCollider GetActiveCapsule()
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
