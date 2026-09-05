using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>Two opposing teams (PHASE7).</summary>
    public enum MatchTeam
    {
        Red = 0,
        Blue = 1
    }

    /// <summary>
    /// Server-authoritative match state (PHASE7). A single scene object that:
    ///
    ///  - registers every <see cref="NetworkCombatant"/> (player + AI) and assigns
    ///    them a team / squad (humans follow <see cref="humanTeamPolicy"/>),
    ///  - tracks team scores, awards kills / captures / hold ticks, and ends the
    ///    match at <see cref="winScore"/> (freezing combat),
    ///  - answers deploy-point queries for garrison / HQ / squad redeployment.
    ///
    /// Scores and winner are SyncVars so the HUD reads them directly.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkMatchManager : NetworkBehaviour
    {
        public static NetworkMatchManager Instance { get; private set; }

        [Header("Score")]
        [Tooltip("先到该分的一方获胜（调试阶段 1000，防止对局过早结束）。")]
        public int winScore = 1000;

        [SyncVar] public int redScore;
        [SyncVar] public int blueScore;

        [Tooltip("-1 = 进行中, 0 = 红方胜, 1 = 蓝方胜.")]
        [SyncVar] public int winner = -1;

        [SyncVar] public bool matchOver;

        [Header("Team / squad")]
        public int squadsPerTeam = 2;
        public int squadSize = 5;

        [Header("Humans (MATCH-LAYER)")]
        [Tooltip("真人分队策略:AllRed=全红(历史行为) / Balance=进人少的队 / FixedSlots=先填满红队席位。")]
        public HumanTeamPolicy humanTeamPolicy = HumanTeamPolicy.AllRed;
        [Tooltip("FixedSlots:红队真人席位数。")]
        public int redHumanSlots = 1;
        [Tooltip("FixedSlots:蓝队真人席位数。")]
        public int blueHumanSlots = 0;

        [Header("Redeploy")]
        [Tooltip("死亡后到可重新部署的秒数.")]
        public float redeployDelay = 10f;
        [Tooltip("玩家进入部署菜单后，无操作自动部署回 GR 的秒数.")]
        public float autoDeployTimeout = 10f;

        [Header("Zones")]
        public List<CapturePoint> capturePoints = new List<CapturePoint>();
        public List<GarrisonZone> garrisons = new List<GarrisonZone>();

        // ---- server-only registry ----
        private readonly List<NetworkCombatant> combatants = new List<NetworkCombatant>();
        private readonly int[] nextSquad = new int[2];
        private readonly int[] humanCount = new int[2];   // 已分配真人数(按队)

        // Combatants whose OnStartServer ran before this manager's (registered
        // statically, drained once the manager starts).
        private static readonly List<NetworkCombatant> pending = new List<NetworkCombatant>();

        public override void OnStartServer()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            winner = -1;
            matchOver = false;
            redScore = 0;
            blueScore = 0;
            humanCount[0] = 0;
            humanCount[1] = 0;

            if (capturePoints == null) capturePoints = new List<CapturePoint>();
            if (garrisons == null) garrisons = new List<GarrisonZone>();

            // Drain combatants registered before this manager started.
            foreach (var c in pending)
                AddCombatant(c);
            pending.Clear();

            // Scene objects spawned at server start.
            foreach (var c in FindObjectsOfType<NetworkCombatant>())
                AddCombatant(c);
        }

        public override void OnStartClient()
        {
            if (Instance == null) Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Called by <see cref="NetworkCombatant.OnStartServer"/>.</summary>
        public static void RegisterCombatant(NetworkCombatant c)
        {
            if (c == null) return;
            if (Instance != null) Instance.AddCombatant(c);
            else pending.Add(c);
        }

        private void AddCombatant(NetworkCombatant c)
        {
            if (c == null || combatants.Contains(c)) return;
            combatants.Add(c);
            AssignCombatant(c);
        }

        /// <summary>ML 训练观测：当前注册的全部作战单位（服务器权威注册表）。</summary>
        public static void GetAllCombatants(List<NetworkCombatant> output)
        {
            output.Clear();
            if (Instance != null)
            {
                // 剔除已销毁的引用。
                Instance.combatants.RemoveAll(c => c == null);
                output.AddRange(Instance.combatants);
            }
            else
            {
                pending.RemoveAll(c => c == null);
                output.AddRange(pending);
            }
        }

        // ---------------------------------------------------------------
        // TEAM / SQUAD ASSIGNMENT
        // ---------------------------------------------------------------

        private void AssignCombatant(NetworkCombatant c)
        {
            if (c.teamId >= 0) return;   // already assigned

            // Human player (has a client connection) follows the configured
            // team policy (MATCH-LAYER multi-human support).
            bool isHuman = c.connectionToClient != null;
            int team;
            if (isHuman)
            {
                switch (humanTeamPolicy)
                {
                    case HumanTeamPolicy.Balance:
                        team = humanCount[(int)MatchTeam.Red] <= humanCount[(int)MatchTeam.Blue]
                            ? (int)MatchTeam.Red : (int)MatchTeam.Blue;
                        break;
                    case HumanTeamPolicy.FixedSlots:
                        team = humanCount[(int)MatchTeam.Red] < Mathf.Max(0, redHumanSlots)
                            ? (int)MatchTeam.Red : (int)MatchTeam.Blue;
                        break;
                    default:   // AllRed
                        team = (int)MatchTeam.Red;
                        break;
                }
                humanCount[team]++;
            }
            else
            {
                // AI: 归属最近安全区的队伍（兼容任意 GR 布局，例如双 GR 同在
                // 地图北侧的 Map_v1）；无安全区时回退按 z 符号（南红 / 北蓝）。
                team = -1;
                float bestDist = float.MaxValue;
                if (garrisons != null)
                {
                    foreach (var g in garrisons)
                    {
                        if (g == null) continue;
                        float d = (g.transform.position - c.transform.position).sqrMagnitude;
                        if (d < bestDist) { bestDist = d; team = g.teamId; }
                    }
                }
                if (team < 0)
                    team = c.transform.position.z < 0f ? (int)MatchTeam.Red : (int)MatchTeam.Blue;
            }

            int squad = nextSquad[team];
            nextSquad[team] = (nextSquad[team] + 1) % Mathf.Max(1, squadsPerTeam);

            c.teamId = team;
            c.squadId = squad;
        }

        // ---------------------------------------------------------------
        // SCORING
        // ---------------------------------------------------------------

        [Server]
        public void AddScore(MatchTeam team, int amount)
        {
            if (matchOver) return;

            if (team == MatchTeam.Red) redScore += amount;
            else blueScore += amount;

            if (redScore >= winScore) EndMatch(MatchTeam.Red);
            else if (blueScore >= winScore) EndMatch(MatchTeam.Blue);
        }

        [Server]
        public void ReportKill(NetworkCombatant attacker, NetworkCombatant victim)
        {
            if (attacker == null || victim == null) return;
            if (attacker.teamId < 0 || victim.teamId < 0) return;
            if (attacker.teamId == victim.teamId) return;   // 友军击杀不计分
            AddScore((MatchTeam)attacker.teamId, 1);
        }

        [Server]
        private void EndMatch(MatchTeam team)
        {
            if (matchOver) return;
            winner = (int)team;
            matchOver = true;
        }

        // ---------------------------------------------------------------
        // DEPLOY POINT QUERIES
        // ---------------------------------------------------------------

        [Server]
        public Vector3? GetGarrisonDeployPoint(MatchTeam team)
        {
            foreach (var g in garrisons)
            {
                if (g == null || (MatchTeam)g.teamId != team) continue;
                return g.GetRandomDeployPoint();
            }
            return null;
        }

        [Server]
        public Vector3? GetHqDeployPoint(MatchTeam team)
        {
            CapturePoint best = null;
            foreach (var cp in capturePoints)
            {
                if (cp == null || (MatchTeam)cp.ownerTeam != team) continue;
                best = cp;
                break;   // first owned HQ; use its safe deploy point
            }
            if (best == null) return null;
            return best.GetSafeDeployPoint(team);
        }

        [Server]
        public Vector3? GetSquadDeployPoint(int team, int squad, NetworkCombatant self)
        {
            var alive = new List<NetworkCombatant>();
            foreach (var c in combatants)
            {
                if (c == null || c == self) continue;
                if (c.teamId != team || c.squadId != squad) continue;
                if (c.IsDead) continue;
                alive.Add(c);
            }
            if (alive.Count == 0) return null;

            var member = alive[Random.Range(0, alive.Count)];

            // 2m 内无遮挡位置（最多尝试 10 次）。
            for (int i = 0; i < 10; i++)
            {
                Vector2 o = Random.insideUnitCircle * 2f;
                Vector3 p = member.transform.position + new Vector3(o.x, 0f, o.y);
                p.y = member.transform.position.y;

                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 1f, NavMesh.AllAreas))
                    p = hit.position;

                if (!Physics.CheckSphere(p + Vector3.up * 0.9f, 0.5f,
                                         Physics.DefaultRaycastLayers,
                                         QueryTriggerInteraction.Ignore))
                    return p;
            }
            return member.transform.position;
        }

        /// <summary>
        /// 部署信标（PHASE8 可选配备）：同小队的重部署点。找到匹配信标后消耗 1 次
        /// 使用次数（用尽时信标自毁）。无匹配信标返回 null（回退 GR）。
        /// </summary>
        [Server]
        public Vector3? GetBeaconDeployPoint(int team, int squad, NetworkCombatant self)
        {
            DeployBeacon best = null;
            float bestDist = float.MaxValue;

            foreach (var b in Object.FindObjectsOfType<DeployBeacon>())
            {
                if (b == null || !b.MatchesSquad(team, squad)) continue;
                float d = (b.transform.position - self.transform.position).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = b; }
            }

            if (best == null) return null;
            return best.ConsumeDeployPoint();
        }
    }
}
