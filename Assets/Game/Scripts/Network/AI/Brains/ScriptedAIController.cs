using Mirror;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>
    /// 脚本陪练（M1-S2 对手，附录 Q28-B：复活 + 增强）。与 ML AI 共用同一身体
    /// （<see cref="NetworkPlayerController"/> 力驱动 3C），但无
    /// <see cref="MLAgentBridge"/>（不进采样）。行为：
    ///
    ///  - 基础状态机（PHASE7 复活版）：Wander（走向战略要地）→ Seek（索敌接近）
    ///    → Attack（停止移动、开火、定期用装备槽 0）。
    ///  - 增强 1（受击应掩体）：血量 <70% 且被攻击 → 向最近掩体移动并蹲伏。
    ///  - 增强 2（低血后撤）：血量 <30% → 后撤方向 = 远离最近敌人。
    ///  - 增强 3（姿态利用）：Attack 距离 >25m → 蹲姿（更小弹道散布预期）。
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NavMeshAgent))]
    public class ScriptedAIController : NetworkBehaviour
    {
        public enum AIState { Wander, Seek, Attack, TakeCover, Retreat }

        [Header("S1 Target Mode")]
        [Tooltip("S1 training: stand still and do not fight back (static target).")]
        public bool targetMode = false;

        [Header("Movement")]
        public float walkSpeed = 3.5f;
        public float runSpeed = 7.5f;
        public float wanderWaitTime = 2f;

        [Header("Combat")]
        public float detectRange = 50f;
        public float attackRange = 40f;
        public float throwInterval = 5f;

        [Header("Enhanced")]
        public float coverHpThreshold = 0.7f;
        public float retreatHpThreshold = 0.3f;
        public float crouchAttackMinDist = 25f;

        private NavMeshAgent agent;
        private NetworkPlayerController body;
        private NetworkGun gun;
        private NetworkEquipment equipment;
        private NetworkPlayerHealth health;
        private NetworkCombatant combatant;

        private AIState state = AIState.Wander;
        private NetworkCombatant target;
        private float targetRefresh;
        private float nextThrow;
        private float wanderNext;
        private Vector3? objective;
        private bool dead;
        private float coverUntil;

        public override void OnStartServer()
        {
            agent = GetComponent<NavMeshAgent>();
            body = GetComponent<NetworkPlayerController>();
            gun = GetComponent<NetworkGun>();
            equipment = GetComponent<NetworkEquipment>();
            health = GetComponent<NetworkPlayerHealth>();
            combatant = GetComponent<NetworkCombatant>();

            agent.speed = walkSpeed;
            agent.acceleration = 20f;
            agent.stoppingDistance = 1f;

            RefreshTarget();
        }

        /// <summary>回合重置（TrainingSessionManager 传送后调用）。</summary>
        public void ResetForRound(Vector3 pos)
        {
            dead = false;
            state = AIState.Wander;
            objective = null;
            target = null;

            if (agent != null)
            {
                agent.Warp(pos);
                agent.isStopped = false;
                agent.ResetPath();
            }
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.position = pos;
                rb.velocity = Vector3.zero;
            }
        }

        public void SetDead(bool value)
        {
            dead = value;
            if (agent != null)
            {
                agent.isStopped = value;
                if (value) agent.ResetPath();
            }
        }

        private void Update()
        {
            if (!isServer || dead) return;
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver) return;
            if (body == null) return;

            // S1 target mode: stand still, do not fight back.
            if (targetMode) return;

            if (target == null || Time.time >= targetRefresh)
                RefreshTarget();

            float dist = target != null
                ? Vector3.Distance(transform.position, target.transform.position)
                : float.PositiveInfinity;

            switch (state)
            {
                case AIState.Wander: Wander(dist); break;
                case AIState.Seek: Seek(dist); break;
                case AIState.Attack: Attack(dist); break;
                case AIState.TakeCover: TakeCover(dist); break;
                case AIState.Retreat: Retreat(dist); break;
            }
        }

        private void Wander(float dist)
        {
            // 增强入口：受击低血优先。
            if (CheckEnhancedTransitions(dist)) return;

            if (target != null && dist <= detectRange)
            {
                state = AIState.Seek;
                agent.speed = runSpeed;
                return;
            }

            // 走向中央战略要地（任意阵营都争夺）。
            if (Time.time >= wanderNext || !objective.HasValue)
            {
                wanderNext = Time.time + wanderWaitTime;
                var zone = StrategicZoneRegistry.Instance;
                if (zone != null && zone.Count > 0)
                {
                    // 复用观测侧的 zone 快照获取中心。
                    var buf = new List<StrategicZoneState>();
                    zone.GetZones(buf);
                    if (buf.Count > 0) objective = buf[0].position;
                }
                if (objective.HasValue && agent.isOnNavMesh)
                    agent.SetDestination(objective.Value);
            }
        }

        private void Seek(float dist)
        {
            if (CheckEnhancedTransitions(dist)) return;

            if (target == null || dist > detectRange)
            {
                state = AIState.Wander;
                agent.speed = walkSpeed;
                return;
            }

            if (dist <= attackRange)
            {
                state = AIState.Attack;
                return;
            }

            if (agent.isOnNavMesh)
                agent.SetDestination(target.transform.position);
        }

        private void Attack(float dist)
        {
            if (CheckEnhancedTransitions(dist)) return;

            if (target == null || dist > detectRange)
            {
                state = AIState.Wander;
                agent.speed = walkSpeed;
                return;
            }

            if (dist > attackRange)
            {
                state = AIState.Seek;
                return;
            }

            if (agent.isOnNavMesh) agent.ResetPath();
            FaceTarget();

            // 增强 3：远距蹲姿。
            bool wantCrouch = dist > crouchAttackMinDist;
            if (wantCrouch && body.posture == PlayerPosture.Stand)
                body.SetServerInput(CrouchIntent());
            else if (!wantCrouch && body.posture == PlayerPosture.Crouch)
                body.SetServerInput(StandIntent());

            if (gun != null)
                gun.Tick(true, false, EyePosition(), transform.forward, false);

            if (equipment != null && Time.time >= nextThrow)
            {
                nextThrow = Time.time + throwInterval;
                Vector3 dir = (target.transform.position - EyePosition()).normalized;
                dir += Vector3.up * 0.4f;
                dir.Normalize();
                equipment.Use(0, false, EyePosition(), dir, Vector3.zero, true);
            }
        }

        /// <summary>增强 1/2：受击找掩体蹲伏、低血后撤。返回 true 表示已切换状态。</summary>
        private bool CheckEnhancedTransitions(float dist)
        {
            if (health == null) return false;
            float hp01 = health.health / health.maxHealth;

            if (hp01 < retreatHpThreshold)
            {
                if (state != AIState.Retreat)
                {
                    state = AIState.Retreat;
                    agent.speed = runSpeed;
                }
                return false;   // Retreat 状态自身处理
            }

            if (hp01 < coverHpThreshold && state != AIState.TakeCover &&
                state != AIState.Retreat && Time.time >= coverUntil)
            {
                state = AIState.TakeCover;
                agent.speed = runSpeed;
                return true;
            }
            return false;
        }

        private void TakeCover(float dist)
        {
            var coverPos = CoverRegistry.NearestCover(transform.position, out bool isLow);
            if (!coverPos.HasValue)
            {
                state = target != null ? AIState.Attack : AIState.Wander;
                return;
            }

            if (agent.isOnNavMesh)
                agent.SetDestination(coverPos.Value);

            bool arrived = Vector3.Distance(transform.position, coverPos.Value) < 1.5f;
            if (arrived)
            {
                // 矮掩体蹲伏、高掩体站立。
                if (isLow && body.posture == PlayerPosture.Stand)
                    body.SetServerInput(CrouchIntent());
                else if (!isLow && body.posture == PlayerPosture.Crouch)
                    body.SetServerInput(StandIntent());

                if (target != null)
                {
                    FaceTarget();
                    if (gun != null)
                        gun.Tick(true, false, EyePosition(), transform.forward, false);
                }

                // 掩体停留 4s 后回到战斗。
                coverUntil = Time.time + 4f;
                state = target != null ? AIState.Attack : AIState.Wander;
            }
        }

        private void Retreat(float dist)
        {
            // 后撤：远离最近敌人方向。
            Vector3 away = target != null
                ? (transform.position - target.transform.position).normalized
                : -transform.forward;

            if (agent.isOnNavMesh)
            {
                Vector3 dest = transform.position + away * 10f;
                if (NavMesh.SamplePosition(dest, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                    agent.SetDestination(hit.position);
            }

            if (target != null)
            {
                FaceTarget();
                if (gun != null)
                    gun.Tick(true, false, EyePosition(), transform.forward, false);
            }

            // 回血超过 50% 重新参战。
            if (health != null && health.health >= health.maxHealth * 0.5f)
            {
                state = AIState.Wander;
                agent.speed = walkSpeed;
            }
        }

        private void FaceTarget()
        {
            if (target == null) return;
            Vector3 dir = target.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(dir);
        }

        private Vector3 EyePosition()
        {
            return transform.position + Vector3.up * 1.5f;
        }

        private void RefreshTarget()
        {
            targetRefresh = Time.time + 0.5f;
            target = NearestEnemy();
        }

        private NetworkCombatant NearestEnemy()
        {
            NetworkCombatant best = null;
            float bestDist = detectRange;

            var self = GetComponent<NetworkCombatant>();
            int myTeam = self != null ? self.teamId : -1;

            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            foreach (var c in buf)
            {
                if (c == null || c == self) continue;
                if (c.IsDead) continue;
                if (c.teamId < 0 || myTeam < 0) continue;
                if (c.teamId == myTeam) continue;

                float d = Vector3.Distance(transform.position, c.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        // ---- 姿态切换意图（通过与服务端输入注入的同一路径驱动 3C）----
        private NetworkInputState CrouchIntent()
        {
            var s = default(NetworkInputState);
            s.crouchToggle = true;
            s.yaw = transform.rotation.eulerAngles.y;
            s.pitch = 0f;
            return s;
        }

        private NetworkInputState StandIntent()
        {
            var s = default(NetworkInputState);
            s.jump = true;   // 蹲→站：跳跃键
            s.yaw = transform.rotation.eulerAngles.y;
            s.pitch = 0f;
            return s;
        }
    }
}
