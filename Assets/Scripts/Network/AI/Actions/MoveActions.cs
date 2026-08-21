using CrashKonijn.Agent.Core;
using CrashKonijn.Agent.Runtime;
using CrashKonijn.Goap.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Movement actions. Actual navigation is driven by AgentNavMeshMove
    // (which listens to GOAP target events). These actions are reached
    // once the agent is within stopping distance and either complete
    // immediately or hold for a short window before re-resolving.
    // ============================================================

    /// <summary>Move to a contested / neutral capture point and hold.</summary>
    public class MoveToCapturePointAction : GoapActionBase<MoveToCapturePointAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            // mayResolve=true is critical: while holding the point the AI must be
            // able to re-plan (e.g. switch to EliminateEnemy when a foe appears).
            return ActionRunState.Wait(2f, mayResolve: true);
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
        }
    }

    /// <summary>Move to an owned capture point and defend it.</summary>
    public class MoveToDefendPointAction : GoapActionBase<MoveToDefendPointAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            return ActionRunState.Wait(3f, mayResolve: true);
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
        }
    }

    /// <summary>Retreat to the owned garrison (safe zone).</summary>
    public class RetreatToGarrisonAction : GoapActionBase<RetreatToGarrisonAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            return ActionRunState.Completed;   // arrived at garrison → safe
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
        }
    }

    /// <summary>Move to an already-deployed supply crate.</summary>
    public class MoveToSupplyAction : GoapActionBase<MoveToSupplyAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            return ActionRunState.Completed;   // arrived; the crate supplies passively
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
        }
    }

    /// <summary>
    /// Sprint to cover away from the nearest enemy. On arrival, crouch and hold
    /// for a short window (mayResolve=true) so a higher-priority goal can interrupt.
    /// </summary>
    public class MoveToCoverAction : GoapActionBase<MoveToCoverAction.Data>
    {
        public override void Start(IMonoAgent agent, Data data)
        {
            // Sprint to cover in crouch posture.
            if (data.Move != null) data.Move.SetRun(true);
            if (data.AI != null) data.AI.SetAIPosture(AIPosture.Crouch);
        }

        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            // Check if the agent has arrived at the cover position.
            if (data.NavAgent != null && data.NavAgent.isOnNavMesh)
            {
                if (data.NavAgent.pathPending) return ActionRunState.Continue;
                if (data.NavAgent.remainingDistance > data.NavAgent.stoppingDistance + 0.5f)
                    return ActionRunState.Continue;
            }

            // Arrived at cover — go prone and hold position briefly.
            if (data.AI != null) data.AI.SetAIPosture(AIPosture.Prone);

            // mayResolve=true: allow re-planning if health recovers or a new threat appears.
            return ActionRunState.Wait(1.5f, mayResolve: true);
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AgentNavMeshMove Move { get; set; }
            [GetComponent] public NetworkAIController AI { get; set; }
            [GetComponent] public NavMeshAgent NavAgent { get; set; }
        }
    }

    /// <summary>Follow the squad leader. Continuous behaviour: keeps chasing the
    /// (moving) leader, so it uses PerformWhileMoving and never completes — the
    /// GoalSelector replaces it when a higher-priority goal appears.</summary>
    public class FollowSquadAction : GoapActionBase<FollowSquadAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            return ActionRunState.Continue;    // keep following
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
        }
    }

    /// <summary>Patrol toward a wander point (squad leader heads to objectives).</summary>
    public class WanderAction : GoapActionBase<WanderAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            return ActionRunState.Wait(1f, mayResolve: true);    // reached; re-pick a point
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
        }
    }
}
