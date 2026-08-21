using Mirror;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    /// <summary>Squad-level order types (future LLM commander).</summary>
    public enum SquadOrderType
    {
        None,       // no order — AI decides autonomously
        Capture,    // capture the designated point
        Defend,     // defend the designated point
        Attack,     // advance toward the enemy
        Regroup,    // rally near the squad leader
        Hold,       // hold position
    }

    /// <summary>
    /// PHASE9 squad order receiver. The future LLM commander sets a squad order on
    /// the squad leader (or all squad members); <see cref="GoalSelector"/> reads it
    /// and boosts the matching goal's priority.
    /// </summary>
    public class SquadOrderReceiver : NetworkBehaviour
    {
        [SyncVar] public int orderType = (int)SquadOrderType.None;
        [SyncVar] public Vector3 orderTarget;
        [SyncVar] public int orderPointId = -1;

        public SquadOrderType CurrentOrder => (SquadOrderType)orderType;

        /// <summary>Set a squad order (server).</summary>
        [Server]
        public void SetOrder(SquadOrderType type, Vector3 target = default, int pointId = -1)
        {
            orderType = (int)type;
            orderTarget = target;
            orderPointId = pointId;
        }
    }
}
