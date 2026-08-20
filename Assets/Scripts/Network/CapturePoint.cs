using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// HQ capture point (PHASE7). A zone that is repeatedly contested by counting
    /// the red/blue combatants inside each tick:
    ///
    ///  - <see cref="contention"/> ranges -60 (red) .. +60 (blue). Each tick it
    ///    moves by (blue - red) per second (captureRate = 1).
    ///  - Reaching +60/-60 = fully captured: owner team gets <see cref="captureScore"/>
    ///    points and the point becomes a deploy location.
    ///  - While owned, the point keeps its deploy location even while contested,
    ///    until the contention value changes sign (crosses 0) → back to neutral.
    ///  - While owned, the owner gains <see cref="holdScore"/> every
    ///    <see cref="holdInterval"/> seconds.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class CapturePoint : NetworkBehaviour
    {
        [Header("Contention")]
        public float radius = 10f;
        public float captureRate = 1f;          // 每秒每人差值

        [SyncVar] public float contention;       // -60..+60
        [SyncVar(hook = nameof(OnOwnerChanged))] public int ownerTeam = -1;     // -1 中立, 0 红, 1 蓝

        [Header("Scoring")]
        public int captureScore = 10;
        public int holdScore = 3;
        public float holdInterval = 10f;

        [Header("Deploy")]
        public List<Transform> deployPoints = new List<Transform>();

        private float holdAccum;
        private readonly Collider[] buffer = new Collider[64];
        private readonly List<NetworkCombatant> inside = new List<NetworkCombatant>();

        public override void OnStartServer()
        {
            contention = 0f;
            ownerTeam = -1;
            holdAccum = 0f;
        }

        private void Update()
        {
            if (!isServer) return;
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver) return;

            CountTeams(out int red, out int blue);
            float diff = blue - red;

            if (diff != 0f)
            {
                contention = Mathf.Clamp(
                    contention + diff * captureRate * Time.deltaTime, -60f, 60f);

                // 完全占领（+60 蓝 / -60 红）。
                if (contention >= 60f && ownerTeam != (int)MatchTeam.Blue)
                {
                    ownerTeam = (int)MatchTeam.Blue;
                    NetworkMatchManager.Instance?.AddScore(MatchTeam.Blue, captureScore);
                }
                else if (contention <= -60f && ownerTeam != (int)MatchTeam.Red)
                {
                    ownerTeam = (int)MatchTeam.Red;
                    NetworkMatchManager.Instance?.AddScore(MatchTeam.Red, captureScore);
                }

                // 符号翻转（过 0）→ 中立，失去部署点保护。
                if (ownerTeam == (int)MatchTeam.Blue && contention < 0f) ownerTeam = -1;
                else if (ownerTeam == (int)MatchTeam.Red && contention > 0f) ownerTeam = -1;
            }

            // 拥有点位：每 holdInterval 给 owner 队 +holdScore。
            if (ownerTeam >= 0)
            {
                holdAccum += Time.deltaTime;
                if (holdAccum >= holdInterval)
                {
                    holdAccum -= holdInterval;
                    NetworkMatchManager.Instance?.AddScore((MatchTeam)ownerTeam, holdScore);
                }
            }
            else
            {
                holdAccum = 0f;
            }
        }

        private void CountTeams(out int red, out int blue)
        {
            red = 0;
            blue = 0;
            inside.Clear();

            int n = Physics.OverlapSphereNonAlloc(transform.position, radius, buffer);
            for (int i = 0; i < n; i++)
            {
                var c = buffer[i].GetComponentInParent<NetworkCombatant>();
                if (c == null || c.teamId < 0) continue;
                if (c.IsDead) continue;
                if (inside.Contains(c)) continue;
                inside.Add(c);

                if (c.teamId == (int)MatchTeam.Red) red++;
                else if (c.teamId == (int)MatchTeam.Blue) blue++;
            }
        }

        /// <summary>当前点内红/蓝人数（供 DebugHud 读取）。</summary>
        public void GetTeamCounts(out int red, out int blue)
        {
            CountTeams(out red, out blue);
        }

        // ---------------------------------------------------------------
        // VISUAL (owner color)
        // ---------------------------------------------------------------
        [Tooltip("PHASE8 地图标记的字母（HQ A/B…）。")]
        public string letter = "A";

        private MapPointMarker marker;

        private void OnOwnerChanged(int oldOwner, int newOwner)
        {
            UpdateVisualColor(newOwner);
        }

        private void UpdateVisualColor(int owner)
        {
            Color c;
            switch (owner)
            {
                case 0: c = new Color(0.7f, 0.15f, 0.15f); break;   // 红
                case 1: c = new Color(0.15f, 0.3f, 0.7f); break;    // 蓝
                default: c = new Color(0.8f, 0.75f, 0.2f); break;    // 中立黄
            }

            if (marker != null)
                marker.SetColor(c);
        }

        private void Start()
        {
            // PHASE8: 纯色 + 中心字母标记（替换旧高亮环）。
            marker = gameObject.AddComponent<MapPointMarker>();
            marker.Init(letter, Mathf.Max(10f, radius * 1.2f), new Color(0.8f, 0.75f, 0.2f));
            UpdateVisualColor(ownerTeam);
        }

        /// <summary>
        /// Pick the deploy point with the lowest nearby-enemy threat (HQ 50m 内敌方
        /// 斥力，纯逻辑计算；选"威胁最小"的部署点)。
        /// </summary>
        [Server]
        public Vector3 GetSafeDeployPoint(MatchTeam team)
        {
            if (deployPoints == null || deployPoints.Count == 0)
                return transform.position;

            var enemies = GetEnemiesWithin(transform.position, 50f, team);

            Vector3 best = deployPoints[0].position;
            float bestThreat = float.MaxValue;

            foreach (var dp in deployPoints)
            {
                if (dp == null) continue;
                float threat = 0f;
                foreach (var e in enemies)
                {
                    float d = Vector3.Distance(dp.position, e.transform.position);
                    threat += 1f / (d + 0.5f);
                }
                if (threat < bestThreat)
                {
                    bestThreat = threat;
                    best = dp.position;
                }
            }
            return best;
        }

        private List<NetworkCombatant> GetEnemiesWithin(Vector3 center, float r, MatchTeam team)
        {
            var result = new List<NetworkCombatant>();
            var cols = Physics.OverlapSphere(center, r);
            foreach (var col in cols)
            {
                var c = col.GetComponentInParent<NetworkCombatant>();
                if (c == null || c.teamId < 0) continue;
                if ((MatchTeam)c.teamId == team) continue;
                if (c.IsDead) continue;
                if (!result.Contains(c)) result.Add(c);
            }
            return result;
        }
    }
}
