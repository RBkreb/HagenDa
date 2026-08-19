using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Garrison (GR) safe zone (PHASE7). Each team owns one garrison; enemies that
    /// stay inside for <see cref="killDelay"/> seconds are force-killed (unrevivable),
    /// so they can only briefly enter. The garrison provides deploy points for its
    /// own team.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class GarrisonZone : NetworkBehaviour
    {
        [Header("Team")]
        [Tooltip("0 = 红方, 1 = 蓝方.")]
        public int teamId = -1;

        [Header("Zone")]
        public float radius = 20f;

        [Header("Deploy")]
        public List<Transform> deployPoints = new List<Transform>();

        [Header("Kill")]
        public float killDelay = 10f;

        private readonly Dictionary<NetworkCombatant, float> enemyTimers =
            new Dictionary<NetworkCombatant, float>();
        private readonly Collider[] buffer = new Collider[64];

        private void Update()
        {
            if (!isServer) return;
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver) return;

            var present = new HashSet<NetworkCombatant>();

            int n = Physics.OverlapSphereNonAlloc(transform.position, radius, buffer);
            for (int i = 0; i < n; i++)
            {
                var c = buffer[i].GetComponentInParent<NetworkCombatant>();
                if (c == null || c.teamId < 0) continue;
                if (c.teamId == teamId) continue;   // 本方安全
                if (c.IsDead) continue;
                present.Add(c);
            }

            foreach (var c in present)
            {
                if (!enemyTimers.ContainsKey(c))
                    enemyTimers[c] = Time.time;
                else if (Time.time - enemyTimers[c] >= killDelay)
                {
                    ForceKill(c);
                    enemyTimers[c] = Time.time;   // 击杀后下个 tick 因 IsDead 被移除
                }
            }

            // 离开的敌人清除计时。
            var toRemove = new List<NetworkCombatant>();
            foreach (var kv in enemyTimers)
                if (!present.Contains(kv.Key))
                    toRemove.Add(kv.Key);
            foreach (var c in toRemove)
                enemyTimers.Remove(c);
        }

        private void ForceKill(NetworkCombatant c)
        {
            var h = c.Health;
            if (h == null || h.IsDead) return;
            h.ForceKill();
        }

        public Vector3 GetRandomDeployPoint()
        {
            if (deployPoints == null || deployPoints.Count == 0)
                return transform.position + Random.insideUnitSphere * 1f;
            return deployPoints[Random.Range(0, deployPoints.Count)].position;
        }
    }
}
