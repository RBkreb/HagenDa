using Mirror;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>PHASE9 AI posture (goal-driven).</summary>
    public enum AIPosture
    {
        Stand,
        Crouch,
        Prone
    }

    /// <summary>
    /// Server-only AI entity: NavMeshAgent pathfinding with a simple
    /// Wander / Seek / Attack state machine. Reuses <see cref="NetworkCombat"/> for
    /// attacks (identical to the player). The capsule collider and speeds mirror the
    /// player so combat / physics behave consistently.
    ///
    /// PHASE7: Wander heads toward a random HQ to simulate contesting; body colour
    /// is set per-team on spawn (red / blue) so the two sides are visually distinct.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NavMeshAgent))]
    public class NetworkAIController : NetworkBehaviour
    {
        public enum AIState { Wander, Seek, Attack }

        [Header("Movement")]
        public float walkSpeed = 3.5f;
        public float runSpeed = 7.5f;
        public float wanderRadius = 5f;
        public float wanderWaitTime = 2f;

        [Header("Combat")]
        public float detectRange = 50f;
        public float attackRange = 40f;
        public float throwInterval = 5f;
        public NetworkCombat combat;
        public NetworkGun gun;
        public NetworkEquipment equipment;

        [Header("Posture")]
        public GameObject visual;       // upright capsule mesh (Body)
        public float standHeight = 1.8f;
        public float crouchHeight = 0.9f;
        public float proneHeight = 0.5f;

        [Header("PHASE9 GOAP")]
        [Tooltip("true = 由 GOAP 驱动行为（GoalSelector + Actions）；false = 旧 FSM。")]
        public bool useGoap = false;

        private NavMeshAgent agent;
        private CapsuleCollider capsule;
        private Rigidbody rb;
        private PhysicMaterial aliveMaterial;  // cached no-friction material
        private AIState state = AIState.Wander;
        private NetworkCombatant target;
        private float targetRefresh;
        private float nextThrow;
        private float wanderNext;
        private CapturePoint currentObjective;
        private bool dead;

        public override void OnStartServer()
        {
            agent = GetComponent<NavMeshAgent>();
            agent.speed = walkSpeed;
            agent.acceleration = 20f;
            agent.stoppingDistance = 1f;

            capsule = GetComponent<CapsuleCollider>();
            rb = GetComponent<Rigidbody>();
            aliveMaterial = capsule != null ? capsule.sharedMaterial : null;

            if (combat == null)
                combat = GetComponent<NetworkCombat>();

            if (gun == null)
                gun = GetComponent<NetworkGun>();

            if (equipment == null)
                equipment = GetComponent<NetworkEquipment>();

            if (!useGoap)
                RefreshTarget();

            // PHASE7: 阵营分配后染色（延迟一帧等 NetworkMatchManager 分配 teamId）。
            Invoke(nameof(ApplyTeamColor), 0.2f);
        }

        /// <summary>
        /// PHASE7: 根据 teamId 染色 AI 身体（红方红 / 蓝方蓝）。
        /// 同小队成员染绿色（仅对人类玩家的小队成员生效，由 PlayerHud 客户端覆写）。
        /// </summary>
        [Server]
        public void ApplyTeamColor()
        {
            var c = GetComponent<NetworkCombatant>();
            int team = c != null ? c.teamId : -1;

            Color color = team == (int)MatchTeam.Blue
                ? new Color(0.15f, 0.3f, 0.8f)
                : new Color(0.8f, 0.15f, 0.15f);

            if (visual != null)
            {
                var rend = visual.GetComponent<Renderer>();
                if (rend != null)
                {
                    // 用共享材质实例避免每次 new（运行时非持久化即可）。
                    var mat = new Material(rend.material);
                    mat.color = color;
                    rend.material = mat;
                }
            }

            RpcApplyTeamColor(team);
        }

        [ClientRpc]
        private void RpcApplyTeamColor(int team)
        {
            Color color = team == (int)MatchTeam.Blue
                ? new Color(0.15f, 0.3f, 0.8f)
                : new Color(0.8f, 0.15f, 0.15f);

            if (visual != null)
            {
                var rend = visual.GetComponent<Renderer>();
                if (rend != null)
                {
                    var mat = new Material(rend.material);
                    mat.color = color;
                    rend.material = mat;
                }
            }

            // 客户端：如果是本方小队成员，染绿色以标识同小队。
            if (NetworkClient.connection != null && NetworkClient.connection.identity != null)
            {
                var localCombatant = NetworkClient.connection.identity.GetComponent<NetworkCombatant>();
                var myCombatant = GetComponent<NetworkCombatant>();
                if (localCombatant != null && myCombatant != null &&
                    localCombatant.teamId == myCombatant.teamId &&
                    localCombatant.squadId == myCombatant.squadId &&
                    localCombatant != myCombatant)
                {
                    if (visual != null)
                    {
                        var rend2 = visual.GetComponent<Renderer>();
                        if (rend2 != null)
                            rend2.material.color = new Color(0.2f, 0.8f, 0.3f);
                    }
                }
            }
        }

        public override void OnStartClient()
        {
            // Non-server clients: disable local simulation. The server drives
            // movement via NavMeshAgent; NetworkTransformReliable syncs position.
            if (!isServer)
            {
                if (agent == null) agent = GetComponent<NavMeshAgent>();
                if (agent != null) agent.enabled = false;

                var rb = GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
            }
        }

        /// <summary>
        /// Called by <see cref="NetworkPlayerHealth"/> on death/rescue. Mirrors the player: death
        /// forces prone (capsule + visual lie flat), stops the NavMeshAgent, and
        /// zeroes residual momentum so the zero-friction capsule doesn't keep sliding.
        /// Rescue restores the upright posture and resumes the agent.
        /// </summary>
        public void SetDead(bool value)
        {
            dead = value;

            // PHASE9: pause/resume the GOAP agent on death/rescue/redeploy. Pausing
            // freezes action execution so the corpse neither moves nor re-plans.
            var agentBehaviour = GetComponent<CrashKonijn.Agent.Runtime.AgentBehaviour>();
            if (agentBehaviour != null)
                agentBehaviour.IsPaused = value;

            if (agent == null)
                agent = GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.isStopped = value;
                if (value)
                    agent.ResetPath();
            }

            SetProne(value);

            if (rb == null)
                rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Kill any momentum carried over from the NavMeshAgent's movement so
                // a corpse doesn't slide on the frictionless capsule.
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            // Swap the capsule's physics material: dead bodies use default friction
            // so they stay put when pushed; alive AI restores the zero-friction
            // material (all movement friction is applied manually as forces).
            if (capsule != null)
                capsule.sharedMaterial = value ? null : aliveMaterial;
        }

        /// <summary>PHASE7: 重新部署落地后恢复站姿并恢复 AI 行为。</summary>
        public void OnRedeploy(Vector3 pos)
        {
            dead = false;
            state = AIState.Wander;
            currentObjective = null;

            // PHASE9: 停止 GOAP 当前 action 并重新规划。否则 AgentBehaviour 的 State
            // 会卡在死亡前的 PerformingAction，ActionRunner 因 `State == PerformingAction`
            // 短路而不再驱动移动，AI 会停在 GR 不去占点。
            var agentBehaviour = GetComponent<CrashKonijn.Agent.Runtime.AgentBehaviour>();
            if (agentBehaviour != null)
            {
                agentBehaviour.IsPaused = false;
                agentBehaviour.StopAction(resolveAction: true);
            }

            if (agent == null)
                agent = GetComponent<NavMeshAgent>();

            // 先 warp NavMeshAgent 到部署点，再恢复速度/姿态。
            if (agent != null)
            {
                agent.Warp(pos);
                agent.isStopped = false;
                agent.ResetPath();
            }

            // 同步 Rigidbody 位置，防止 agent warp 后 rb 仍在旧位置拉回。
            if (rb == null)
                rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.position = pos;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            SetProne(false);

            if (capsule == null)
                capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
                capsule.sharedMaterial = aliveMaterial;

            ApplyTeamColor();
        }

        private void SetProne(bool prone)
        {
            if (capsule == null)
                capsule = GetComponent<CapsuleCollider>();

            if (capsule != null)
            {
                if (prone)
                {
                    // Lie flat (direction Z): vertical extent is the capsule diameter.
                    capsule.direction = 2;
                    capsule.height = proneHeight;
                    capsule.center = new Vector3(0f, proneHeight * 0.5f, 0f);
                }
                else
                {
                    capsule.direction = 1; // Y (upright)
                    capsule.height = standHeight;
                    capsule.center = new Vector3(0f, standHeight * 0.5f, 0f);
                }
            }

            if (visual != null)
            {
                if (prone)
                {
                    // Rotate the upright capsule mesh to lie flat and drop its centre
                    // to half the diameter.
                    visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    visual.transform.localPosition = new Vector3(0f, proneHeight * 0.5f, 0f);
                }
                else
                {
                    visual.transform.localRotation = Quaternion.identity;
                    visual.transform.localPosition = new Vector3(0f, standHeight * 0.5f, 0f);
                }
            }
        }

        /// <summary>
        /// PHASE9 姿态控制：进攻站立 / 防守蹲下 / 被压制趴下。
        /// </summary>
        public void SetAIPosture(AIPosture posture)
        {
            if (capsule == null)
                capsule = GetComponent<CapsuleCollider>();

            switch (posture)
            {
                case AIPosture.Prone:
                    SetProne(true);
                    break;

                case AIPosture.Crouch:
                    if (capsule != null)
                    {
                        capsule.direction = 1;
                        capsule.height = crouchHeight;
                        capsule.center = new Vector3(0f, crouchHeight * 0.5f, 0f);
                    }
                    if (visual != null)
                    {
                        visual.transform.localRotation = Quaternion.identity;
                        visual.transform.localPosition = new Vector3(0f, crouchHeight * 0.5f, 0f);
                    }
                    break;

                default: // Stand
                    SetProne(false);
                    break;
            }
        }

        private void Update()
        {
            if (!isServer) return;
            if (dead) return;

            // PHASE9: GOAP 接管行为决策时，旧 FSM 完全停用。
            if (useGoap) return;

            // PHASE7: 对局结束后冻结一切 AI 行为。
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver)
            {
                if (agent != null && agent.isOnNavMesh)
                    agent.ResetPath();
                return;
            }

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
            }
        }

        private void Wander(float dist)
        {
            if (target != null && dist <= detectRange)
            {
                state = AIState.Seek;
                agent.speed = runSpeed;
                return;
            }

            // PHASE7: 无索敌时持续向某个 HQ 移动，模拟争夺。
            if (Time.time >= wanderNext || currentObjective == null)
            {
                wanderNext = Time.time + wanderWaitTime;
                currentObjective = PickRandomHq();
                if (currentObjective != null && agent.isOnNavMesh)
                    agent.SetDestination(currentObjective.transform.position);
            }
        }

        private void Seek(float dist)
        {
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

            agent.ResetPath();
            FaceTarget();

            if (gun != null)
            {
                // Continuous full-auto fire; the gun's fire-rate gating, spread and
                // auto-reload apply (identical to the player). No ADS for AI.
                gun.Tick(true, false, EyePosition(), transform.forward, false);
            }

            if (equipment != null && Time.time >= nextThrow)
            {
                nextThrow = Time.time + throwInterval;
                Vector3 dir = (target.transform.position - EyePosition()).normalized;
                dir += Vector3.up * 0.4f;
                dir.Normalize();

                // AI uses the same equipment runtime (index 0 = first throwable).
                equipment.Use(0, false, EyePosition(), dir, Vector3.zero, true);
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
            // Approximate capsule eye height (stand height 1.8m).
            return transform.position + Vector3.up * 0.8f;
        }

        private Vector3 RandomWanderPoint()
        {
            Vector3 center = transform.position;
            for (int i = 0; i < 10; i++)
            {
                Vector3 p = center + new Vector3(
                    Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f)).normalized
                    * Random.Range(0f, wanderRadius);

                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                    return hit.position;
            }
            return center;
        }

        /// <summary>Pick a random HQ from the match manager. Falls back to wandering if none.</summary>
        private CapturePoint PickRandomHq()
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null || mm.capturePoints == null || mm.capturePoints.Count == 0)
                return null;
            return mm.capturePoints[Random.Range(0, mm.capturePoints.Count)];
        }

        private void RefreshTarget()
        {
            targetRefresh = Time.time + 0.5f;
            target = NearestEnemy();
        }

        /// <summary>
        /// Find the nearest hostile combatant (player OR AI) within detectRange.
        /// </summary>
        private NetworkCombatant NearestEnemy()
        {
            NetworkCombatant best = null;
            float bestDist = detectRange;   // 只搜索 detectRange 内的敌人

            var self = GetComponent<NetworkCombatant>();
            int myTeam = self != null ? self.teamId : -1;

            foreach (var c in Object.FindObjectsOfType<NetworkCombatant>())
            {
                if (c == null || c == self) continue;
                if (c.IsDead) continue;
                if (c.teamId < 0 || myTeam < 0) continue;
                if (c.teamId == myTeam) continue;   // 友军跳过

                float d = Vector3.Distance(transform.position, c.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }
    }
}
