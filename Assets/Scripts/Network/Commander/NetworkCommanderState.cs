using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>一条小队目标（LLM 网格坐标，客户端 HUD/地图标记消费）。</summary>
    [System.Serializable]
    public struct SquadObjectiveMsg
    {
        public int team;
        public int squad;
        public float gx;
        public float gz;
    }

    /// <summary>Mirror 自动序列化的同步列表。</summary>
    public class SyncListSquadObjective : SyncList<SquadObjectiveMsg> { }

    /// <summary>
    /// PHASE10 指挥官系统共享网络状态（场景注入一个，服务器权威）：
    ///
    ///  - <see cref="matchStarted"/>：开局门控。场景中存在本对象时，比赛从"未开始"
    ///    启动（全员冻结、争夺/计分暂停），双方指挥官完成部署或超时退化后置 true。
    ///    不存在本对象的场景恒视为已开始——其他场景行为完全不变。
    ///  - <see cref="objectives"/>：各小队当前抽象争夺点（网格坐标），驱动玩家小队
    ///    的 HUD 一行文字与地图黄色菱形标记（Q17:b）。
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkCommanderState : NetworkBehaviour
    {
        public static NetworkCommanderState Instance { get; private set; }

        [SyncVar] public bool matchStarted = true;

        public readonly SyncListSquadObjective objectives = new SyncListSquadObjective();

        /// <summary>便捷判定：门控是否冻结中。</summary>
        public static bool GateActive => Instance != null && !Instance.matchStarted;

        /// <summary>对局正式开始的网络时间（信息流时间戳/时长来源，服务器权威）。</summary>
        public static double MatchStartedAt { get; private set; } = 0.0;

        public override void OnStartServer()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            matchStarted = false;   // 注入了指挥官 = 需要门控，开局冻结
        }

        public override void OnStartClient()
        {
            if (Instance == null) Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------
        // SERVER API
        // ---------------------------------------------------------------

        [Server]
        public void SetMatchStarted()
        {
            if (!matchStarted) MatchStartedAt = NetworkTime.time;
            matchStarted = true;
        }

        [Server]
        public void SetSquadObjective(int team, int squad, float gx, float gz)
        {
            for (int i = 0; i < objectives.Count; i++)
            {
                var o = objectives[i];
                if (o.team == team && o.squad == squad)
                {
                    o.gx = gx; o.gz = gz;
                    objectives[i] = o;
                    return;
                }
            }
            objectives.Add(new SquadObjectiveMsg
            {
                team = team,
                squad = squad,
                gx = gx,
                gz = gz,
            });
        }

        [Server]
        public void RemoveSquadObjectives(int team, int squad)
        {
            for (int i = objectives.Count - 1; i >= 0; i--)
            {
                var o = objectives[i];
                if (o.team == team && o.squad == squad)
                    objectives.RemoveAt(i);
            }
        }
    }
}
