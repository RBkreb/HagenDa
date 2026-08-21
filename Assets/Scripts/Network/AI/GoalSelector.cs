using CrashKonijn.Goap.Runtime;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// PHASE9 goal selector. GOAP resolves which action chain satisfies a requested
    /// goal, but does not decide *which* goal to pursue — this component does, using a
    /// hybrid "hard rules + utility" algorithm every 2 seconds. It also applies the
    /// posture (stand attack / crouch defend / prone under fire).
    /// </summary>
    [RequireComponent(typeof(GoapActionProvider))]
    public class GoalSelector : NetworkBehaviour
    {
        [Header("Hard rules")]
        [Tooltip("残血阈值（< 此值强制 SurviveGoal）。")]
        public float surviveHealthThreshold = 25f;

        [Header("Utility")]
        public float captureWeight = 50f;
        public float defendWeight = 30f;
        public float eliminateWeight = 40f;
        public float supportWeight = 25f;
        public float patrolWeight = 10f;

        [Tooltip("Goal 评估间隔（秒）。")]
        public float evaluateInterval = 2f;

        private GoapActionProvider provider;
        private AIDataProvider data;
        private SquadOrderReceiver order;
        private NetworkAIController ai;
        private AgentNavMeshMove move;

        private float timer;
        private bool matchOverFrozen;

        private void Awake()
        {
            provider = GetComponent<GoapActionProvider>();
            data = GetComponent<AIDataProvider>();
            order = GetComponent<SquadOrderReceiver>();
            ai = GetComponent<NetworkAIController>();
            move = GetComponent<AgentNavMeshMove>();

            // PHASE9 分批：随机错开每个 AI 的评估相位，避免 60 AI 同帧 resolve 造成峰值。
            timer = Random.Range(0f, evaluateInterval);
        }

        private void Update()
        {
            if (!isServer) return;

            // Freeze everything once the match ends.
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver)
            {
                if (!matchOverFrozen)
                {
                    matchOverFrozen = true;
                    if (move != null) move.StopMoving();
                }
                return;
            }
            matchOverFrozen = false;

            timer += Time.deltaTime;
            if (timer < evaluateInterval) return;
            timer = 0f;

            Evaluate();
        }

        private void Evaluate()
        {
            if (data == null || provider == null) return;

            // ---- Hard rules (absolute priority) ----
            if (data.GetHealthLevel() < surviveHealthThreshold)
            {
                provider.RequestGoal<SurviveGoal>();
                ApplyPosture(AIPosture.Crouch);
                return;
            }

            // Self-marked + jammer available → clear the mark.
            if (data.IsSelfMarked() && data.HasEquipmentAmmo(EquipmentType.Jammer))
            {
                provider.RequestGoal<SurviveGoal>();
                ApplyPosture(AIPosture.Crouch);
                return;
            }

            // ---- Squad order override (future LLM commander) ----
            var sq = order != null ? order.CurrentOrder : SquadOrderType.None;
            switch (sq)
            {
                case SquadOrderType.Capture:
                    provider.RequestGoal<CaptureObjectiveGoal>();
                    ApplyPosture(AIPosture.Stand);
                    return;
                case SquadOrderType.Defend:
                    provider.RequestGoal<DefendObjectiveGoal>();
                    ApplyPosture(AIPosture.Crouch);
                    return;
                case SquadOrderType.Attack:
                    provider.RequestGoal<EliminateEnemyGoal>();
                    ApplyPosture(AIPosture.Stand);
                    return;
                case SquadOrderType.Regroup:
                    provider.RequestGoal<SupportSquadGoal>();
                    ApplyPosture(AIPosture.Stand);
                    return;
                case SquadOrderType.Hold:
                    provider.RequestGoal<PatrolGoal>();
                    ApplyPosture(AIPosture.Crouch);
                    return;
            }

            // ---- Utility scoring ----
            int ammo = data.GetAmmoLevel();
            int health = data.GetHealthLevel();
            bool contested = data.GetNearestUncapturedPoint() != null;
            bool owned = data.GetNearestOwnedPoint() != null;
            bool hasEnemy = data.HasKnownEnemy();
            bool leader = data.GetSquadLeader() != null;

            float resupply = (100 - ammo) * 0.4f + (100 - health) * 0.3f;
            float capture = contested ? captureWeight : 0f;
            float defend = owned ? defendWeight : 0f;
            float eliminate = hasEnemy ? eliminateWeight : 0f;
            float support = leader ? supportWeight : 0f;
            float patrol = patrolWeight;

            // Pick the highest-scoring goal.
            float best = resupply;
            GoalKind kind = GoalKind.Resupply;

            if (capture > best) { best = capture; kind = GoalKind.Capture; }
            if (defend > best) { best = defend; kind = GoalKind.Defend; }
            if (eliminate > best) { best = eliminate; kind = GoalKind.Eliminate; }
            if (support > best) { best = support; kind = GoalKind.Support; }
            if (patrol > best) { best = patrol; kind = GoalKind.Patrol; }

            switch (kind)
            {
                case GoalKind.Resupply:
                    provider.RequestGoal<ResupplyGoal>();
                    ApplyPosture(AIPosture.Stand);
                    break;
                case GoalKind.Capture:
                    provider.RequestGoal<CaptureObjectiveGoal>();
                    ApplyPosture(AIPosture.Stand);
                    break;
                case GoalKind.Defend:
                    provider.RequestGoal<DefendObjectiveGoal>();
                    ApplyPosture(AIPosture.Crouch);
                    break;
                case GoalKind.Eliminate:
                    provider.RequestGoal<EliminateEnemyGoal>();
                    ApplyPosture(AIPosture.Stand);
                    break;
                case GoalKind.Support:
                    provider.RequestGoal<SupportSquadGoal>();
                    ApplyPosture(AIPosture.Stand);
                    break;
                default:
                    provider.RequestGoal<PatrolGoal>();
                    ApplyPosture(AIPosture.Stand);
                    break;
            }
        }

        private enum GoalKind { Resupply, Capture, Defend, Eliminate, Support, Patrol }

        private void ApplyPosture(AIPosture posture)
        {
            if (ai != null) ai.SetAIPosture(posture);
            if (move != null) move.SetRun(posture == AIPosture.Stand);
        }
    }
}
