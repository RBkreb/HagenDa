using CrashKonijn.Goap.Runtime;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// PHASE9 goal selector. GOAP resolves which action chain satisfies a requested
    /// goal, but does not decide *which* goal to pursue — this component does, using a
    /// hybrid "hard rules + utility" algorithm every 2 seconds.
    ///
    /// All squad members share the same objective (no leader-following): with an enemy
    /// in sight they attack, otherwise they push the nearest capturable point, then
    /// defend an owned point, then patrol. Death freezes all behaviour (the corpse
    /// stays prone) until the redeploy restores it.
    /// </summary>
    public class GoalSelector : NetworkBehaviour
    {
        [Header("Hard rules")]
        [Tooltip("残血阈值（< 此值强制 SurviveGoal）。")]
        public float surviveHealthThreshold = 25f;

        [Header("Utility")]
        [Tooltip("有敌人时改用硬规则优先攻击（不再依赖此权重）。")]
        public float eliminateWeight = 60f;
        public float captureWeight = 50f;
        public float defendWeight = 30f;
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

            // Death freezes all behaviour: the corpse stays prone and never re-evaluates
            // until the redeploy restores health.
            if (data != null && data.Health != null && data.Health.IsDead)
            {
                if (move != null) move.StopMoving();
                return;
            }

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

            // ---- Hard rule: engage known enemies (direct vision / mark / intel) ----
            // A hard rule (not a utility weight) so it cannot be silently overridden
            // by a stale serialized weight in the prefab.
            if (data.HasKnownEnemy())
            {
                provider.RequestGoal<EliminateEnemyGoal>();
                ApplyPosture(AIPosture.Stand);
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
                case SquadOrderType.Hold:
                    // No follower goal; fall through to shared-objective selection.
                    break;
            }

            // ---- Utility scoring (no known enemy; enemy handled by hard rule) ----
            int ammo = data.GetAmmoLevel();
            int health = data.GetHealthLevel();
            bool capturable = data.GetNearestUncapturedPoint() != null;
            bool owned = data.GetNearestOwnedPoint() != null;

            float resupply = (100 - ammo) * 0.4f + (100 - health) * 0.3f;
            float capture = capturable ? captureWeight : 0f;
            float defend = owned ? defendWeight : 0f;
            float patrol = patrolWeight;

            float best = resupply;
            GoalKind kind = GoalKind.Resupply;

            if (capture > best) { best = capture; kind = GoalKind.Capture; }
            if (defend > best) { best = defend; kind = GoalKind.Defend; }
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
                default:
                    provider.RequestGoal<PatrolGoal>();
                    ApplyPosture(AIPosture.Stand);
                    break;
            }
        }

        private enum GoalKind { Resupply, Capture, Defend, Patrol }

        private void ApplyPosture(AIPosture posture)
        {
            if (ai != null) ai.SetAIPosture(posture);
            if (move != null) move.SetRun(posture == AIPosture.Stand);
        }
    }
}
