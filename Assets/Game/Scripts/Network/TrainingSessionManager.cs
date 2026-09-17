using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 训练回合驱动器（M1）。服务器端组件，随训练场景常驻：
    ///
    ///  - 回合循环：<see cref="roundDuration"/> 秒或一方全灭 → 终局奖励 +
    ///    EndEpisode → 换地图/换阵容 → 传送所有实体到 home → 新回合。
    ///  - 地图轮换（附录 C）：maps 列表逐回合轮换（Square/Wave…），由
    ///    <see cref="arena"/> 重建几何。
    ///  - 阵容轮换（附录 C）：四种对手阵容（全突击/全支援/全侦察/混合），
    ///    我方固定混合（2突击+2支援+1侦察）。兵种 = classId + 固定配装。
    ///  - 每回合 TeamIntel.Reset + RewardBus.Reset + CoverRegistry.Clear。
    /// </summary>
    public class TrainingSessionManager : NetworkBehaviour
    {
        public enum Lineup { AllAssault = 0, AllSupport = 1, AllRecon = 2, Mixed = 3 }

        [Header("Maps (rotation)")]
        public List<TrainingMap> maps = new List<TrainingMap>();
        public int mapIndex;                 // 当前地图（轮换游标）

        [Header("Arenas (multi-area)")]
        [Tooltip("单区域模式用；多区域时留空，arenas 列表生效。")]
        public TrainingArena arena;
        public List<TrainingArena> arenas = new List<TrainingArena>();

        [Header("Rounds")]
        public float roundDuration = 120f;
        public Lineup opponentLineup = Lineup.Mixed;
        public int lineupIndex;              // 当前对手阵容（轮换游标）
        public int roundCount;

        [Header("Entities (auto-found)")]
        public List<NetworkAIController> mlTeam = new List<NetworkAIController>();
        public List<NetworkAIController> opponentTeam = new List<NetworkAIController>();

        private float roundEnd;
        private bool running;

        // 回合内死亡重生（训练专用：死亡 5s 后传回本方 GR 复活，短回合不等 10s 菜单）。
        private const float RespawnDelay = 5f;
        private readonly Dictionary<NetworkAIController, float> deathTimes =
            new Dictionary<NetworkAIController, float>();

        // 多区域训练：每个实体记录自己的出生位置（区域中心 + 阵营偏移）。
        private readonly Dictionary<NetworkAIController, Vector3> spawnPositions =
            new Dictionary<NetworkAIController, Vector3>();

        public TrainingMap CurrentMap => maps != null && maps.Count > 0
            ? maps[mapIndex % maps.Count] : null;

        /// <summary>
        /// Play 后自动启动 Host：训练场景无需手动 StartHost。
        /// 注意：NetworkIdentity 场景对象在服务器未启动前被 Mirror 禁用，
        /// NetworkBehaviour.Start() 不会调用。自动启动逻辑在
        /// TrainingAutoHost（普通 MonoBehaviour，无 NetworkIdentity）中。
        /// </summary>

        public override void OnStartServer()
        {
            // 训练场景禁真人玩家。
            var nm = NetworkManager.singleton;
            if (nm != null) nm.autoCreatePlayer = false;

            // 自动收集场景中所有 TrainingArena（多区域）。
            arenas.RemoveAll(a => a == null);
            if (arenas.Count == 0)
                foreach (var a in Object.FindObjectsOfType<TrainingArena>())
                    arenas.Add(a);

            // 延迟收集实体：OnStartServer 时 NetworkCombatant 可能尚未
            // 注册到 MatchManager（spawn 顺序不确定），teamId 仍为 -1。
            // 等 2 帧确保所有 combatant 的 OnStartServer 完成后再收集。
            StartCoroutine(DelayedStart());
        }

        private System.Collections.IEnumerator DelayedStart()
        {
            yield return null;
            yield return null;

            CollectEntities();

            // 验证：如果 teamId 仍为 -1 说明 MatchManager 未跑，再等。
            int guard = 0;
            while (mlTeam.Count == 0 && opponentTeam.Count == 0 && guard++ < 60)
            {
                yield return null;
                CollectEntities();
            }

            Debug.Log($"[Training] Teams collected: ML={mlTeam.Count} Opponent={opponentTeam.Count}");
            StartRound();
        }

        private void CollectEntities()
        {
            mlTeam.Clear();
            opponentTeam.Clear();
            spawnPositions.Clear();
            foreach (var ai in Object.FindObjectsOfType<NetworkAIController>())
            {
                var c = ai.GetComponent<NetworkCombatant>();
                if (c == null) continue;
                if (c.teamId == (int)MatchTeam.Red) mlTeam.Add(ai);
                else if (c.teamId == (int)MatchTeam.Blue) opponentTeam.Add(ai);
                spawnPositions[ai] = ai.transform.position;
            }
        }

        private void Update()
        {
            if (!isServer || !running) return;

            MLTrainingConfig.SetTimeRemaining01(
                Mathf.Clamp01((roundEnd - Time.time) / roundDuration));

            if (Time.time >= roundEnd)
            {
                EndRoundByTimeout();
                return;
            }

            HandleRoundRespawns();

            // 一方全灭 → 提前终局。
            if (TeamWiped(mlTeam)) { EndRound(1 - (int)MatchTeam.Red); return; }
            if (TeamWiped(opponentTeam)) { EndRound((int)MatchTeam.Red); return; }
        }

        /// <summary>
        /// 回合内死亡重生：全灭前有 5s 缓冲（TeamWiped 判定在重生后自然解除；
        /// 全灭即刻终局，故 5s 重生只作用于仍有一人存活的队伍）。
        /// </summary>
        private void HandleRoundRespawns()
        {
            TrackDeaths(mlTeam);
            TrackDeaths(opponentTeam);

            RespawnDead(mlTeam);
            RespawnDead(opponentTeam);
        }

        private void TrackDeaths(List<NetworkAIController> team)
        {
            foreach (var ai in team)
            {
                var h = ai.GetComponent<NetworkPlayerHealth>();
                bool isDead = h != null && h.IsDead;
                if (isDead && !deathTimes.ContainsKey(ai))
                    deathTimes[ai] = Time.time;
                else if (!isDead)
                    deathTimes.Remove(ai);
            }
        }

        private void RespawnDead(List<NetworkAIController> team)
        {
            // 全灭时立即终局，不重生（由 Update 的 TeamWiped 分支处理）。
            if (TeamWiped(team)) return;

            for (int i = team.Count - 1; i >= 0; i--)
            {
                var ai = team[i];
                if (!deathTimes.TryGetValue(ai, out float diedAt)) continue;
                if (Time.time - diedAt < RespawnDelay) continue;

                var h = ai.GetComponent<NetworkPlayerHealth>();
                if (h == null || !h.IsDead) { deathTimes.Remove(ai); continue; }

                // 回合内重生：回自己的出生点（多区域安全）。
                deathTimes.Remove(ai);
                Vector3 pos = spawnPositions.TryGetValue(ai, out var sp)
                    ? sp + new Vector3(Random.Range(-2f, 2f), 0f, 0f)
                    : ai.transform.position;

                var rb = ai.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.position = pos;
                }
                h.ServerFullResetForRound();

                var scripted = ai.GetComponent<ScriptedAIController>();
                if (scripted != null) scripted.ResetForRound(pos);

                ai.OnRedeploy(pos);

                var bridge = ai.GetComponent<MLAgentBridge>();
                if (bridge != null) bridge.OnLifeReset();
            }
        }

        private bool TeamWiped(List<NetworkAIController> team)
        {
            foreach (var ai in team)
            {
                var h = ai.GetComponent<NetworkPlayerHealth>();
                if (h != null && !h.IsDead) return false;
            }
            return team.Count > 0;
        }

        // ---------------------------------------------------------------
        // ROUND LIFECYCLE
        // ---------------------------------------------------------------

        private void StartRound()
        {
            var map = CurrentMap;
            if (map != null)
            {
                map.BuildLayout();          // seed 固定 → 布局可复现
                // 多区域：每个 TrainingArena 独立应用布局（几何在各自本地坐标生成）。
                foreach (var a in arenas)
                    if (a != null) a.ApplyLayout(map);
                if (arenas.Count == 0 && arena != null)
                    arena.ApplyLayout(map);
            }

            TeamIntel.Reset();
            RewardBus.Reset();

            // 轮换游标推进（第一回合用初始值）。
            if (roundCount > 0)
            {
                mapIndex = (mapIndex + 1) % Mathf.Max(1, maps.Count);
                lineupIndex = (lineupIndex + 1) % 4;
            }
            opponentLineup = (Lineup)lineupIndex;

            ApplyLineups();
            RespawnAll();

            roundEnd = Time.time + roundDuration;
            running = true;
        }

        private void EndRound(int winnerTeam)
        {
            running = false;

            // 终局奖励只走 RewardBus.MatchEnd（bridge.EndRound 仅结束 episode，
            // 不再重复给 Win/Loss——修复双重终局奖励缺陷 #4）。
            RewardBus.MatchEnd(winnerTeam);

            foreach (var ai in mlTeam)
            {
                var bridge = ai.GetComponent<MLAgentBridge>();
                if (bridge != null)
                    bridge.EndEpisodeOnly();
            }

            roundCount++;
            StartRound();
        }

        private void EndRoundByTimeout()
        {
            // 超时：以剩余存活数多者为胜；平局不给终局奖惩（S1 靶不反击
            // 几乎必然平局，全员 -0.5 会教 critic "一切皆负"——缺陷 #3）。
            int red = AliveCount(mlTeam);
            int blue = AliveCount(opponentTeam);
            int winner = red == blue ? -1 : (red > blue ? (int)MatchTeam.Red : (int)MatchTeam.Blue);
            if (winner < 0)
            {
                running = false;
                foreach (var ai in mlTeam)
                {
                    var bridge = ai.GetComponent<MLAgentBridge>();
                    if (bridge != null) bridge.EndEpisodeOnly();
                }
                roundCount++;
                StartRound();
                return;
            }
            EndRound(winner);
        }

        private int AliveCount(List<NetworkAIController> team)
        {
            int n = 0;
            foreach (var ai in team)
            {
                var h = ai.GetComponent<NetworkPlayerHealth>();
                if (h != null && !h.IsDead) n++;
            }
            return n;
        }

        // ---------------------------------------------------------------
        // LINEUPS (附录 C：四种对手阵容轮换)
        // ---------------------------------------------------------------

        private void ApplyLineups()
        {
            // 我方（红）：固定混合 2突击+2支援+1侦察（共识 §2 编成 A）。
            int[] mine = { 0, 0, 1, 1, 2 };

            // 对手（蓝）：按阵容轮换。
            int[] theirs = LineupArray(opponentLineup);

            AssignLineup(mlTeam, mine);
            AssignLineup(opponentTeam, theirs);
        }

        private static int[] LineupArray(Lineup l)
        {
            switch (l)
            {
                case Lineup.AllAssault: return new[] { 0, 0, 0, 0, 0 };
                case Lineup.AllSupport: return new[] { 1, 1, 1, 1, 1 };
                case Lineup.AllRecon:   return new[] { 2, 2, 2, 2, 2 };
                default:                return new[] { 0, 1, 2, 0, 1 };   // 混合
            }
        }

        private void AssignLineup(List<NetworkAIController> team, int[] classIds)
        {
            for (int i = 0; i < team.Count; i++)
            {
                var ai = team[i];
                var bridge = ai.GetComponent<MLAgentBridge>();
                if (bridge != null)
                    bridge.classId = i < classIds.Length ? classIds[i] : 0;
            }
        }

        // ---------------------------------------------------------------
        // RESPAWN / TELEPORT
        // ---------------------------------------------------------------

        private void RespawnAll()
        {
            deathTimes.Clear();

            // 多区域训练：每个实体回自己的出生点（记录于 CollectEntities），
            // 而非全局 red/blue home——区域间互不干扰。
            foreach (var ai in mlTeam) RespawnEntity(ai, 0f);
            foreach (var ai in opponentTeam) RespawnEntity(ai, 180f);
        }

        private void RespawnEntity(NetworkAIController ai, float yaw)
        {
            Vector3 pos = spawnPositions.TryGetValue(ai, out var sp)
                ? sp : ai.transform.position;

            var rb = ai.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.position = pos;
            }
            else
            {
                ai.transform.position = pos;
            }

            ai.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            // 满血复活 + 站姿 + 装备/弹药重置（复用死亡重部署管线）。
            var health = ai.GetComponent<NetworkPlayerHealth>();
            if (health != null)
                health.ServerFullResetForRound();

            // 脚本陪练状态机重置（NavMesh warp 等）。
            var scripted = ai.GetComponent<ScriptedAIController>();
            if (scripted != null)
                scripted.ResetForRound(pos);

            ai.OnRedeploy(pos);
        }
    }
}
