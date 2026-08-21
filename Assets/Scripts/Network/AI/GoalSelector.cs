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
    /// Low-health behaviour: when health drops below threshold, the AI randomly picks
    /// fight-or-flight if an enemy is nearby (50/50), or seeks cover / continues to
    /// objective if no enemy. Healing is handled inline by AttackEnemyAction
    /// (fire-and-forget syringe, usable while moving).
    /// </summary>
    public class GoalSelector : NetworkBehaviour
    {
        [Header("Hard rules")]
        [Tooltip("残血阈值（< 此值触发自救/掩体/撤退）。")]
        public float surviveHealthThreshold = 35f;

        [Header("Utility")]
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
        private bool lowHealthFight;

        private void Awake()
        {
            provider = GetComponent<GoapActionProvider>();
            data = GetComponent<AIDataProvider>();
            order = GetComponent<SquadOrderReceiver>();
            ai = GetComponent<NetworkAIController>();
            move = GetComponent<AgentNavMeshMove>();

            timer = Random.Range(0f, evaluateInterval);
        }

        private void Update()
        {
            if (!isServer) return;

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

            int health = data.GetHealthLevel();

            // ---- Hard rule: rescue downed ally (if carrying defibrillator) ----
            if (data.HasEquipmentAmmo(EquipmentType.Defibrillator) &&
                data.GetNearestRescueRequest() != null)
            {
                provider.RequestGoal<RescueAllyGoal>();
                ApplyPosture(AIPosture.Stand);
                if (move != null) move.SetRun(true);
                return;
            }

            // ---- Hard rules: low health ----
            if (health < surviveHealthThreshold)
            {
                // Try self-heal first (syringe or supply crate, fire-and-forget).
                data.TrySelfHeal();

                if (data.HasKnownEnemy())
                {
                    // Fight-or-flight: 50/50 random decision.
                    lowHealthFight = Random.value < 0.5f;

                    if (lowHealthFight)
                    {
                        provider.RequestGoal<EliminateEnemyGoal>();
                        ApplyPosture(AIPosture.Stand);
                        return;
                    }
                    else
                    {
                        provider.RequestGoal<TakeCoverGoal>();
                        ApplyPosture(AIPosture.Prone);
                        if (move != null) move.SetRun(true);
                        return;
                    }
                }
                else
                {
                    // No enemy: seek cover to heal, or keep moving to objective.
                    // Don't retreat to GR — stay in the fight.
                    if (data.HasEquipmentAmmo(EquipmentType.LargeSupplyCrate) && !data.IsUnderFire())
                    {
                        provider.RequestGoal<TakeCoverGoal>();
                        ApplyPosture(AIPosture.Crouch);
                        return;
                    }

                    if (data.GetNearestSupply() != null)
                    {
                        provider.RequestGoal<ResupplyGoal>();
                        ApplyPosture(AIPosture.Stand);
                        if (move != null) move.SetRun(true);
                        return;
                    }

                    // No supply available: keep pushing objective while self-healing.
                    // Fall through to objective selection.
                }
            }

            // Self-marked + jammer available → clear the mark.
            if (data.IsSelfMarked() && data.HasEquipmentAmmo(EquipmentType.Jammer))
            {
                provider.RequestGoal<EliminateEnemyGoal>();
                ApplyPosture(AIPosture.Crouch);
                return;
            }

            // ---- Hard rule: engage known enemies ----
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
                    break;
            }

            // ---- Utility scoring (no known enemy) ----
            int ammo = data.GetAmmoLevel();
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
