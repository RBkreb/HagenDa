using CrashKonijn.Agent.Core;
using CrashKonijn.Goap.Runtime;

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
            return ActionRunState.Wait(2f);   // hold on the point to contest it
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
            return ActionRunState.Wait(3f);   // hold the point defensively
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

    /// <summary>Follow the squad leader.</summary>
    public class FollowSquadAction : GoapActionBase<FollowSquadAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            return ActionRunState.Wait(1f);    // re-resolve as the leader moves
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
            return ActionRunState.Wait(1f);    // reached; re-pick a point
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
        }
    }
}
