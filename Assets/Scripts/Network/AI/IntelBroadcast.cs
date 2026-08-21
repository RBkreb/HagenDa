using UnityEngine;
using HagenDa.Networking.AI;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE9 squad intel broadcast. When an AI directly sees an enemy it broadcasts
    /// that enemy's identity to nearby same-squad teammates, who cache it as a
    /// "known enemy" (with an expiry) so they can engage targets beyond their own
    /// direct vision — supporting the unlimited-attack-range rule.
    /// </summary>
    public static class IntelBroadcast
    {
        public const float Radius = 100f;   // broadcast range (metres)
        public const float Expiry = 5f;     // intel lifetime before it is forgotten

        /// <summary>Broadcast a seen enemy to same-squad allies within range.</summary>
        public static void BroadcastSeenEnemy(NetworkCombatant broadcaster, NetworkCombatant enemy)
        {
            if (broadcaster == null || enemy == null) return;

            var providers = Object.FindObjectsOfType<AIDataProvider>();
            for (int i = 0; i < providers.Length; i++)
            {
                var p = providers[i];
                if (p == null) continue;

                var other = p.Combatant;
                if (other == null || other == broadcaster) continue;
                if (other.teamId < 0 || other.teamId != broadcaster.teamId) continue;
                if (other.squadId < 0 || other.squadId != broadcaster.squadId) continue;

                if (Vector3.Distance(other.transform.position, broadcaster.transform.position) > Radius)
                    continue;

                p.ReceiveIntel(enemy);
            }
        }
    }
}
