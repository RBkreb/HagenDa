using System.Collections.Generic;
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

        [Tooltip("首次部署：玩家连接后处于观战/大厅状态，选定部署点+配装前不进入世界。")]
        [SyncVar] public bool awaitingInitialDeploy;

        // PHASE9: DeathSOS 定向重复（每 1s × 9 次，定向 40m 内最近支援兵）
        private float sosRepeatTimer;
        private int sosRepeatCount;
        private const float SosRepeatInterval = 1f;
        private const int SosRepeatMax = 9;
        private const float SosRange = 40f;

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
            awaitingInitialDeploy = connectionToClient != null;   // 真人首次进入部署
            lastAttacker = null;
        }

        // ---------------------------------------------------------------
        // DAMAGE
        // ---------------------------------------------------------------

        /// <summary>Full-body / area damage (explosions, no attacker).</summary>
        [Server]
        public void TakeDamage(float damage)
        {
            TakeDamageInternal(damage * explosionDamageMultiplier, 1f);
        }

        /// <summary>Hitbox-aware damage (bullets, no attacker).</summary>
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

        /// <summary>
        /// Hitbox-aware bullet damage (PHASE7 击杀归属). PHASE12: the part
        /// multiplier is applied ONCE by <see cref="NetworkBullet"/> (from the
        /// NetworkHitbox that was hit) — do not re-apply it here.
        /// </summary>
        [Server]
        public void TakeBulletDamage(float damage, Vector3 hitPoint, Vector3 bulletDir, NetworkCombatant attacker)
        {
            lastAttacker = attacker;
            TakeDamageInternal(damage, 1f);
        }

        /// <summary>Explosion damage (PHASE7 击杀归属).</summary>
        [Server]
        public void TakeExplosionDamage(float damage, NetworkCombatant attacker, Vector3 center)
        {
            lastAttacker = attacker;
            TakeDamageInternal(damage * explosionDamageMultiplier, 1f);
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
                float healthBefore = health;
                health = Mathf.Max(0f, health - remaining);
                // Regen is gated on health *actually decreasing*, not on taking
                // damage: armor-only damage must not reset the regen timer.
                lastDamageTime = Time.time;

                // 回复中被伤害则去除回复 buff（治疗针 / 补给包）。
                buffRegenPerSecond = 0f;

                // 团队广播：受击（位置 = 攻击者 → 读取方得到受击方向）（ML 训练共识 §3）。
                if (lastAttacker != null)
                {
                    var c0 = GetComponent<NetworkCombatant>();
                    if (c0 != null)
                        TeamIntel.Broadcast(c0.teamId, IntelEvent.Damaged,
                            lastAttacker.transform.position, c0.squadId, GetInstanceID());
                }

                // ML 训练奖励：按实际损失 HP 计（过量伤害不重复计分）。
                RewardBus.Damage(this, lastAttacker, healthBefore - health);
            }

            // PHASE8: 受击反馈（相机震动 + FOV 脉冲）。
            if (connectionToClient != null && amount > 0f)
                TargetRpcDamageFeedback(amount);

            // PHASE13: 受击动画已移除(受击只保留 HUD/相机反馈)。

            if (health <= 0f)
                Die();
        }

        [TargetRpc]
        private void TargetRpcDamageFeedback(float amount)
        {
            GameHud.Instance?.ShowDamageFeedback(amount);
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

            // PHASE9: DeathSOS 定向重复（每 1s × 9 次，被救起/重部署即停）。
            if (deathHandled && sosRepeatCount < SosRepeatMax && Time.time >= sosRepeatTimer)
            {
                var self = GetComponent<NetworkCombatant>();
                if (self != null)
                    SendDirectedDeathSOS(self);
                sosRepeatCount++;
                sosRepeatTimer = Time.time + SosRepeatInterval;
            }

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

            // ML 训练奖励：击杀 + 阵亡 + 助攻 + 团队共享 + 标记引导。
            RewardBus.Kill(this, lastAttacker);

            // 团队广播：阵亡求救（PHASE9：定向 40m 内最近支援兵，每 1s × 9 次重复）。
            if (self != null)
            {
                SendDirectedDeathSOS(self);
                sosRepeatTimer = Time.time + SosRepeatInterval;
                sosRepeatCount = 1;
            }

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
            sosRepeatCount = SosRepeatMax;   // 停止 SOS 重复
            health = maxHealth;
            lastAttacker = null;
            SetDeadState(false);
            RpcRescue();
        }

        // ---------------------------------------------------------------
        // PHASE7/8 DEPLOY
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
                if (pos.HasValue) DoDeploy(pos.Value);
                return;
            }

            // 玩家：进入部署菜单，等待玩家主动点选部署（不再自动超时部署）。
            awaitingRedeploy = true;
        }

        [Server]
        private Vector3? AiChooseDeploy()
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null) return null;

            var self = GetComponent<NetworkCombatant>();
            int team = self != null && self.teamId >= 0 ? self.teamId : (int)MatchTeam.Red;
            int squad = self != null ? self.squadId : -1;

            // PHASE9 重新部署优先级：信标 > 最近小队队友 > 最近己方已占点 > GR
            if (squad >= 0)
            {
                var beacon = mm.GetBeaconDeployPoint(team, squad, self);
                if (beacon.HasValue) return beacon;

                var squadPt = mm.GetSquadDeployPoint(team, squad, self);
                if (squadPt.HasValue) return squadPt;
            }

            var hq = mm.GetHqDeployPoint((MatchTeam)team);
            if (hq.HasValue) return hq;

            return mm.GetGarrisonDeployPoint((MatchTeam)team);
        }

        /// <summary>
        /// PHASE9: 定向向 40m 内最近的支援兵发送 DeathSOS 广播。
        /// 找不到支援兵时退回全队广播。
        /// </summary>
        [Server]
        private void SendDirectedDeathSOS(NetworkCombatant self)
        {
            int targetId = -1;
            float bestD = SosRange * SosRange;

            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            foreach (var c in buf)
            {
                if (c == null || c == self || c.IsDead) continue;
                if (c.teamId != self.teamId) continue;

                // 检查是否为支援兵
                var fsm = c.GetComponent<FSMAIController>();
                if (fsm == null || fsm.aiClass != FsmClass.Support) continue;

                float d = (c.transform.position - transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; targetId = fsm.GetInstanceID(); }
            }

            TeamIntel.Broadcast(self.teamId, IntelEvent.DeathSOS,
                transform.position, self.squadId, GetInstanceID(), targetId);
        }

        /// <summary>统一部署点解析（1=GR / 2=HQ / 3=squad / 4=beacon，fallback GR）。</summary>
        [Server]
        private Vector3? ResolveDeployPoint(int choice)
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null)
            {
                // 无对局场景（AnimationTest 单人动画测试等）：回退到场景出生点。
                // 否则真人永远卡在部署界面（CmdDeploy 解析不到位置而静默失败）。
                var sp = FindObjectOfType<NetworkStartPosition>();
                return sp != null ? (Vector3?)sp.transform.position : transform.position;
            }

            var self = GetComponent<NetworkCombatant>();
            int team = self != null && self.teamId >= 0 ? self.teamId : (int)MatchTeam.Red;
            int squad = self != null ? self.squadId : -1;

            switch (choice)
            {
                case 2:
                    var hq = mm.GetHqDeployPoint((MatchTeam)team);
                    if (hq.HasValue) return hq;
                    break;
                case 3:
                    var sq = mm.GetSquadDeployPoint(team, squad, self);
                    if (sq.HasValue) return sq;
                    break;
                case 4:
                    // 部署信标（同小队）。无匹配信标回退 GR。
                    var bc = mm.GetBeaconDeployPoint(team, squad, self);
                    if (bc.HasValue) return bc;
                    break;
            }
            return mm.GetGarrisonDeployPoint((MatchTeam)team);
        }

        /// <summary>Player deploy choice (1=GR / 2=HQ / 3=squad) — legacy 1/2/3 keys.</summary>
        [Server]
        public void RequestDeploy(int choice)
        {
            if (!deathHandled && !awaitingInitialDeploy) return;
            if (deathHandled && Time.time < redeployDeadline) return;
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver) return;

            Vector3? pos = ResolveDeployPoint(choice);
            if (!pos.HasValue) return;

            DoDeploy(pos.Value);
        }

        /// <summary>
        /// PHASE8 部署界面确认（带配装）。客户端选好部署点 + 配装后调用。
        /// </summary>
        [Command]
        public void CmdDeploy(int choice, LoadoutDefinition loadout)
        {
            if (!deathHandled && !awaitingInitialDeploy) return;
            if (deathHandled && Time.time < redeployDeadline) return;
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver) return;

            var eq = GetComponent<NetworkEquipment>();
            if (eq != null && loadout != null)
            {
                string reason;
                if (!eq.ValidateLoadout(loadout, out reason))
                {
                    Debug.LogWarning($"[Deploy] 配装无效: {reason}");
                    return;
                }
                eq.ApplyLoadout(loadout);
            }

            Vector3? pos = ResolveDeployPoint(choice);
            if (!pos.HasValue) return;

            DoDeploy(pos.Value);
        }

        [Server]
        private void DoDeploy(Vector3 pos)
        {
            deathHandled = false;
            unrevivable = false;
            awaitingRedeploy = false;
            awaitingInitialDeploy = false;
            sosRepeatCount = SosRepeatMax;   // 停止 SOS 重复
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

            // PHASE8: 重新部署后重置所有配备弹药/补给/冷却 + 主武器弹匣/备弹。
            var eq = GetComponent<NetworkEquipment>();
            if (eq != null) eq.ResetForRedeploy();
            var gun = GetComponent<NetworkGun>();
            if (gun != null) gun.ResetForRedeploy();

            var controller = GetComponent<NetworkPlayerController>();
            if (controller != null) controller.OnRedeploy();

            var ai = GetComponent<NetworkAIController>();
            if (ai != null) ai.OnRedeploy(pos);

            // PHASE9 FSM 大脑：从新位置重新开始常态。
            var fsm = GetComponent<FSMAIController>();
            if (fsm != null) fsm.OnRedeploy(pos);

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

            // 脚本陪练（与 ML AI 共存于同一身体）：冻结状态机。
            var scripted = GetComponent<ScriptedAIController>();
            if (scripted != null) scripted.SetDead(dead);

            // PHASE9 FSM 大脑：冻结决策（重部署钩子在 DoDeploy 里单独调用）。
            var fsm = GetComponent<FSMAIController>();
            if (fsm != null) fsm.SetDead(dead);
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

        private bool? hasHitboxesCache;

        /// <summary>
        /// True when this entity carries NetworkHitbox parts (soldier model). Bullets
        /// then ignore its movement capsules entirely — the bone hitboxes consume
        /// them instead. Legacy entities without hitboxes keep the capsule rules.
        /// </summary>
        public bool HasHitboxes
        {
            get
            {
                if (hasHitboxesCache == null)
                    hasHitboxesCache = GetComponentsInChildren<NetworkHitbox>(true).Length > 0;
                return hasHitboxesCache.Value;
            }
        }

        // ---------------------------------------------------------------
        // CLIENT FEEDBACK HOOKS
        // ---------------------------------------------------------------

        [ClientRpc]
        private void RpcDie() { }

        [ClientRpc]
        private void RpcRescue() { }

        /// <summary>
        /// ML 训练回合重置（TrainingSessionManager）：满血满甲、清死亡状态、
        /// 重置装备与弹药——绕过 redeploy 等待流程。
        /// </summary>
        [Server]
        public void ServerFullResetForRound()
        {
            deathHandled = false;
            unrevivable = false;
            awaitingRedeploy = false;
            awaitingInitialDeploy = false;
            lastAttacker = null;
            health = maxHealth;
            armor = 0f;
            buffRegenPerSecond = 0f;

            SetDeadState(false);

            var eq = GetComponent<NetworkEquipment>();
            if (eq != null) eq.ResetForRedeploy();
            var gun = GetComponent<NetworkGun>();
            if (gun != null) gun.ResetForRedeploy();
        }

        private void OnHealthChanged(float oldValue, float newValue) { }
        private void OnArmorChanged(float oldValue, float newValue) { }
    }
}
