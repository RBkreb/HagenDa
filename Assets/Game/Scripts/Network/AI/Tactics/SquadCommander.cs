using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE9 指挥官规则模块。每 20s 遍历所有小队，给每个小队随机分配一个
    /// 未占领/敌方争夺点（AssignObjective）。非实体——纯服务端规则调度器，
    /// 挂在场景任意非网络对象上。
    /// </summary>
    public class SquadCommander : MonoBehaviour
    {
        [Tooltip("指令下发间隔（秒）。")]
        public float assignInterval = 20f;

        private float nextAssign;
        private readonly List<StrategicZoneState> zoneBuf = new List<StrategicZoneState>();
        private readonly List<FSMAIController> agents = new List<FSMAIController>();

        private void Start()
        {
            nextAssign = Time.time + 5f;   // 开局 5s 后首次下发
        }

        private void Update()
        {
            if (!NetworkServer.active) return;
            if (Time.time < nextAssign) return;
            nextAssign = Time.time + assignInterval;

            AssignSquadObjectives();
        }

        /// <summary>PHASE10：LLM 指挥官退化时立即下发一轮（由协调器调用），不必等下一周期。</summary>
        public void ImmediateAssign() => AssignSquadObjectives();

        private void AssignSquadObjectives()
        {
            var registry = StrategicZoneRegistry.Instance;
            if (registry == null || registry.Count == 0) return;

            zoneBuf.Clear();
            registry.GetZones(zoneBuf);
            if (zoneBuf.Count == 0) return;

            // 收集所有 FSM agent，按 (team, squad) 分组
            agents.Clear();
            // 用 FSMBattleSystem 的注册列表（已在 10Hz tick 中维护）
            var sys = FSMBattleSystem.Instance;
            var found = FindObjectsOfType<FSMAIController>();
            agents.Clear();
            agents.AddRange(found);

            // 按小队分组
            var squads = new Dictionary<(int, int), List<FSMAIController>>();
            foreach (var a in agents)
            {
                if (a == null) continue;
                var c = a.GetComponent<NetworkCombatant>();
                if (c == null || c.teamId < 0) continue;

                var key = (c.teamId, c.squadId);
                if (!squads.ContainsKey(key))
                    squads[key] = new List<FSMAIController>();
                squads[key].Add(a);
            }

            // 每个小队随机分配一个未占领/敌方要地
            foreach (var kv in squads)
            {
                int team = kv.Key.Item1;
                var members = kv.Value;
                if (members.Count == 0) continue;

                // 筛选未占领/敌方要地
                var candidates = new List<StrategicZoneState>();
                foreach (var z in zoneBuf)
                    if (z.ownerTeam != team)
                        candidates.Add(z);

                // 全部已占领时随机选一个（继续推进）
                if (candidates.Count == 0)
                    candidates.AddRange(zoneBuf);

                var chosen = candidates[Random.Range(0, candidates.Count)];

                foreach (var a in members)
                    a.AssignObjective(chosen.position);
            }
        }
    }
}
