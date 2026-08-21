using CrashKonijn.Agent.Core;
using CrashKonijn.Agent.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// PHASE9 movement adapter: drives the AI's <see cref="NavMeshAgent"/> from the
    /// GOAP <see cref="AgentBehaviour"/> target events. When an action requires a
    /// target position the agent moves toward it until the GOAP framework reports the
    /// target is in range.
    ///
    /// Server-side only (the client disables the NavMeshAgent in OnStartClient).
    /// </summary>
    [RequireComponent(typeof(AgentBehaviour))]
    [RequireComponent(typeof(NavMeshAgent))]
    public class AgentNavMeshMove : MonoBehaviour
    {
        [Header("Speeds")]
        public float walkSpeed = 3.5f;
        public float runSpeed = 7.5f;

        private AgentBehaviour agent;
        private NavMeshAgent navAgent;
        private ITarget currentTarget;
        private bool shouldMove;

        private void Awake()
        {
            agent = GetComponent<AgentBehaviour>();
            navAgent = GetComponent<NavMeshAgent>();
        }

        private void OnEnable()
        {
            if (agent == null) agent = GetComponent<AgentBehaviour>();
            agent.Events.OnTargetInRange += OnTargetInRange;
            agent.Events.OnTargetChanged += OnTargetChanged;
            agent.Events.OnTargetNotInRange += OnTargetNotInRange;
            agent.Events.OnTargetLost += OnTargetLost;
        }

        private void OnDisable()
        {
            agent.Events.OnTargetInRange -= OnTargetInRange;
            agent.Events.OnTargetChanged -= OnTargetChanged;
            agent.Events.OnTargetNotInRange -= OnTargetNotInRange;
            agent.Events.OnTargetLost -= OnTargetLost;
        }

        private void OnTargetInRange(ITarget target)
        {
            shouldMove = false;
            if (navAgent != null && navAgent.isOnNavMesh) navAgent.ResetPath();
        }

        private void OnTargetChanged(ITarget target, bool inRange)
        {
            currentTarget = target;
            shouldMove = !inRange;
        }

        private void OnTargetNotInRange(ITarget target)
        {
            shouldMove = true;
        }

        private void OnTargetLost()
        {
            currentTarget = null;
            shouldMove = false;
            if (navAgent != null && navAgent.isOnNavMesh) navAgent.ResetPath();
        }

        private void Update()
        {
            // The client disables the NavMeshAgent; only the server drives movement.
            if (navAgent == null || !navAgent.enabled) return;
            if (!shouldMove || currentTarget == null) return;
            if (!navAgent.isOnNavMesh) return;

            navAgent.SetDestination(currentTarget.Position);
        }

        /// <summary>Switch between run (combat) and walk (patrol) speed.</summary>
        public void SetRun(bool running)
        {
            if (navAgent == null) navAgent = GetComponent<NavMeshAgent>();
            if (navAgent != null)
                navAgent.speed = running ? runSpeed : walkSpeed;
        }

        /// <summary>Stop immediately and clear the current path.</summary>
        public void StopMoving()
        {
            shouldMove = false;
            currentTarget = null;
            if (navAgent != null && navAgent.isOnNavMesh) navAgent.ResetPath();
        }
    }
}
