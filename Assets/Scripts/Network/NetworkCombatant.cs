using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Per-entity match identity (PHASE7). Attached to both the player and the AI
    /// prefab; <see cref="NetworkMatchManager"/> assigns <see cref="teamId"/> and
    /// <see cref="squadId"/> on spawn.
    ///
    /// Also carries the mark state (Q key / future AI). The mark timestamp is a
    /// double (Mirror <c>NetworkTime.time</c>) so a future client-side minimap can
    /// compare it directly; the reserved "Marked" tag is maintained server-side for
    /// the future AI (占位符).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkCombatant : NetworkBehaviour
    {
        public const string MarkedTag = "Marked";

        [Tooltip("0 = 红方, 1 = 蓝方. -1 = 未分配.")]
        [SyncVar] public int teamId = -1;

        [Tooltip("队内小队索引. -1 = 未分配.")]
        [SyncVar] public int squadId = -1;

        [Tooltip("标记到期时间戳 (NetworkTime.time). -1 = 未标记.")]
        [SyncVar] public double markedUntil = -1.0;

        [Tooltip("ML 训练：标记方队伍（标记引导击杀归属）。-1 = 未知。")]
        [SyncVar] public int markedByTeam = -1;

        /// <summary>ML 训练：最近一次标记者（服务器端引用，奖励归属）。</summary>
        [System.NonSerialized] public NetworkCombatant lastMarker;

        [Tooltip("PHASE8 干扰器：免疫标记期间任何标记无效。")]
        [SyncVar] public bool markImmune;

        public bool IsMarked => NetworkTime.time < markedUntil;

        private NetworkPlayerHealth health;
        public NetworkPlayerHealth Health
        {
            get
            {
                if (health == null) health = GetComponent<NetworkPlayerHealth>();
                return health;
            }
        }

        public bool IsDead => Health != null && Health.IsDead;

        public override void OnStartServer()
        {
            NetworkMatchManager.RegisterCombatant(this);
        }

        /// <summary>Mark this entity until the given network time (server).
        /// 干扰器免疫期间忽略新标记。</summary>
        [Server]
        public void SetMarked(double until)
        {
            SetMarked(until, -1, null);
        }

        /// <summary>带归属的标记（ML 训练：标记引导击杀奖励）。</summary>
        [Server]
        public void SetMarked(double until, int byTeam, NetworkCombatant marker)
        {
            if (markImmune) return;
            markedUntil = until;
            markedByTeam = byTeam;
            lastMarker = marker;
        }

        /// <summary>立即清除当前标记（干扰器）。</summary>
        [Server]
        public void ClearMark()
        {
            markedUntil = -1.0;
        }

        private void Update()
        {
            if (!isServer) return;

            // 预留 tag：被标记期间打上 "Marked" tag，供未来 AI 识别。
            if (IsMarked)
            {
                if (!gameObject.CompareTag(MarkedTag))
                    gameObject.tag = MarkedTag;
            }
            else
            {
                if (gameObject.CompareTag(MarkedTag))
                    gameObject.tag = "Untagged";
            }
        }
    }
}
