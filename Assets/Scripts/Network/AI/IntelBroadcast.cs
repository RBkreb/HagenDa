using System.Collections.Generic;
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

        // ---- Rescue request registry (server-only) ----
        // Persistent: stays active until the downed ally is rescued or redeployed.
        private static readonly List<RescueRequest> activeRescues = new List<RescueRequest>();

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

        // ---------------------------------------------------------------
        // RESCUE REQUESTS (defibrillator system)
        // ---------------------------------------------------------------

        /// <summary>
        /// Register a persistent rescue request when a combatant goes down.
        /// Stays active until cleared by Rescue or Redeploy.
        /// </summary>
        public static void BroadcastRescueRequest(NetworkCombatant downed)
        {
            if (downed == null) return;
            // Avoid duplicates.
            for (int i = 0; i < activeRescues.Count; i++)
                if (activeRescues[i].downed == downed) return;

            activeRescues.Add(new RescueRequest { downed = downed });
        }

        /// <summary>Clear a rescue request (ally rescued or redeployed).</summary>
        public static void ClearRescueRequest(NetworkCombatant downed)
        {
            if (downed == null) return;
            for (int i = activeRescues.Count - 1; i >= 0; i--)
            {
                if (activeRescues[i].downed == downed || activeRescues[i].downed == null)
                    activeRescues.RemoveAt(i);
            }
        }

        /// <summary>Get the nearest active rescue request from a same-team ally.</summary>
        public static NetworkCombatant GetNearestRescueRequest(Vector3 from, int teamId)
        {
            NetworkCombatant best = null;
            float bestDist = float.MaxValue;

            for (int i = activeRescues.Count - 1; i >= 0; i--)
            {
                var req = activeRescues[i];
                if (req.downed == null || req.downed.IsDead == false)
                {
                    // Ally was rescued (no longer dead) — clear.
                    activeRescues.RemoveAt(i);
                    continue;
                }
                if (req.downed.teamId != teamId) continue;

                float d = (req.downed.transform.position - from).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = req.downed;
                }
            }
            return best;
        }

        public struct RescueRequest
        {
            public NetworkCombatant downed;
        }
    }
}
