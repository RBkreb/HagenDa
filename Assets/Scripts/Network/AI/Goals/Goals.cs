using CrashKonijn.Goap.Runtime;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Goals. Each goal is a stateless marker; the GoalSelector (external)
    // decides which goal to request based on a hybrid priority + utility
    // algorithm. Conditions are attached in the CombatantAgentTypeFactory.
    // ============================================================

    /// <summary>Retreat to garrison / cover to survive (low health / marked).</summary>
    public class SurviveGoal : GoalBase { }

    /// <summary>Restore ammo & health (move to supply or deploy a crate).</summary>
    public class ResupplyGoal : GoalBase { }

    /// <summary>Reach and hold a contested / neutral capture point.</summary>
    public class CaptureObjectiveGoal : GoalBase { }

    /// <summary>Reach and defend an owned capture point.</summary>
    public class DefendObjectiveGoal : GoalBase { }

    /// <summary>Eliminate known enemies.</summary>
    public class EliminateEnemyGoal : GoalBase { }

    /// <summary>Follow / support the squad leader.</summary>
    public class SupportSquadGoal : GoalBase { }

    /// <summary>Default fallback patrol.</summary>
    public class PatrolGoal : GoalBase { }
}
