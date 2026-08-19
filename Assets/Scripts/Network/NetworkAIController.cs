using Mirror;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-only AI entity: NavMeshAgent pathfinding with a simple
    /// Wander / Seek / Attack state machine. Reuses <see cref="NetworkCombat"/> for
    /// attacks (identical to the player). The capsule collider and speeds mirror the
    /// player so combat / physics behave consistently.
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
        public float detectRange = 20f;
        public float attackRange = 15f;
        public float throwInterval = 5f;
        public NetworkCombat combat;
        public NetworkGun gun;
        public NetworkEquipment equipment;

        [Header("Posture")]
        public GameObject visual;       // upright capsule mesh (Body)
        public float standHeight = 1.8f;
        public float proneHeight = 0.5f;

        private NavMeshAgent agent;
        private CapsuleCollider capsule;
        private Rigidbody rb;
        private PhysicMaterial aliveMaterial;  // cached no-friction material
        private AIState state = AIState.Wander;
        private NetworkPlayerController target;
        private float targetRefresh;
        private float nextThrow;
        private float wanderNext;
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

            RefreshTarget();
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
        public void OnRedeploy()
        {
            dead = false;
            state = AIState.Wander;

            if (agent == null)
                agent = GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.isStopped = false;
                agent.ResetPath();
                agent.Warp(transform.position);
            }

            SetProne(false);

            if (rb == null)
                rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (capsule == null)
                capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
                capsule.sharedMaterial = aliveMaterial;
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
                    capsule.center = new Vector3(0f, proneHeight * 0.5f, 0f);
                }
                else
                {
                    capsule.direction = 1; // Y (upright)
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

        private void Update()
        {
            if (!isServer) return;
            if (dead) return;

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

            if (Time.time >= wanderNext)
            {
                wanderNext = Time.time + wanderWaitTime;
                Vector3 dest = RandomWanderPoint();
                if (agent.isOnNavMesh)
                    agent.SetDestination(dest);
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

        private void RefreshTarget()
        {
            targetRefresh = Time.time + 0.5f;
            target = NearestPlayer();
        }

        private NetworkPlayerController NearestPlayer()
        {
            NetworkPlayerController best = null;
            float bestDist = float.PositiveInfinity;

            var self = GetComponent<NetworkCombatant>();
            int myTeam = self != null ? self.teamId : -1;

            foreach (var p in Object.FindObjectsOfType<NetworkPlayerController>())
            {
                if (p == null) continue;
                var health = p.GetComponent<NetworkPlayerHealth>();
                if (health != null && health.IsDead) continue;

                // PHASE7: only target hostile entities.
                var c = p.GetComponent<NetworkCombatant>();
                if (c != null && c.teamId >= 0 && myTeam >= 0 && c.teamId == myTeam) continue;

                float d = Vector3.Distance(transform.position, p.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }
    }
}
