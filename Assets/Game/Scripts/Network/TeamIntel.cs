using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>团队情报广播事件类型（ML 训练共识 §3：6 类，全自动规则触发）。</summary>
    public enum IntelEvent
    {
        Damaged = 0,       // 受击（位置 = 攻击者位置 → 读取方可得受击方向）
        EnemySpotted = 1,  // 目击敌人（位置 = 敌人位置）
        DeathSOS = 2,      // 阵亡求救（位置 = 阵亡位置）
        LowAmmo = 3,       // 缺弹药
        NeedHeal = 4,      // 请求治疗
        BeaconDeployed = 5 // 信标已部署（位置 = 信标位置）
    }

    /// <summary>
    /// Server-side team intel broadcast (ML 训练共识 §3/§5). Rules scattered across
    /// combat/equipment systems push events here; ML agents read the recent window
    /// per team when collecting observations. Pure training infrastructure: no
    /// networking, no persistence — cleared on match restart.
    ///
    /// 读取约定：每队保留最近 <see cref="MaxEntries"/> 条、<see cref="WindowSeconds"/>
    /// 秒窗口内的事件；观测侧自行换算相对方位/距离与年龄。
    /// </summary>
    public static class TeamIntel
    {
        public const int MaxEntries = 8;
        public const float WindowSeconds = 10f;

        public struct Entry
        {
            public IntelEvent type;
            public Vector3 position;   // world space (semantic depends on type)
            public double time;        // NetworkTime.time
            public int squadId;        // emitter's squad (-1 unknown)
            public int targetId;       // PHASE9: 定向广播目标 instanceId（-1 = 广播全部）
        }

        private static readonly Dictionary<int, List<Entry>> channels = new Dictionary<int, List<Entry>>();

        /// <summary>PHASE9: DeathSOS 独立存储——不被 59 人的 EnemySpotted/Damaged
        /// 广播挤出 8 条窗口。支援兵直接从这里读 SOS，不依赖主频道。</summary>
        private static readonly Dictionary<int, List<Entry>> sosChannels = new Dictionary<int, List<Entry>>();
        private const int MaxSOS = 16;   // 最多保留 16 条 SOS（59 人最多 ~30 同时死亡）

        /// <summary>Per-emitter spam gating（规则触发最小间隔，按事件类型）。</summary>
        private static readonly Dictionary<(int, IntelEvent), double> lastEmit =
            new Dictionary<(int, IntelEvent), double>();

        private static readonly float[] cooldowns =
        {
            2f,  // Damaged
            3f,  // EnemySpotted
            0f,  // DeathSOS（每次阵亡必发）
            8f,  // LowAmmo
            8f,  // NeedHeal
            0f   // BeaconDeployed（每次部署必发）
        };

        [Server]
        public static void Broadcast(int team, IntelEvent type, Vector3 position, int squadId, int emitterId)
        {
            Broadcast(team, type, position, squadId, emitterId, -1);
        }

        /// <summary>PHASE9: 定向广播（targetId = 目标 instanceId，-1 = 广播全部）。</summary>
        [Server]
        public static void Broadcast(int team, IntelEvent type, Vector3 position,
                                       int squadId, int emitterId, int targetId)
        {
            if (team < 0) return;

            // Spam gate per emitter+type (death/beacon bypass via cooldown 0).
            float cd = cooldowns[(int)type];
            if (cd > 0f)
            {
                var key = (emitterId, type);
                if (lastEmit.TryGetValue(key, out double last) &&
                    NetworkTime.time - last < cd)
                    return;
                lastEmit[key] = NetworkTime.time;
            }

            if (!channels.TryGetValue(team, out var list))
            {
                list = new List<Entry>();
                channels[team] = list;
            }

            var e = new Entry
            {
                type = type,
                position = position,
                time = NetworkTime.time,
                squadId = squadId,
                targetId = targetId,
            };

            // PHASE9: DeathSOS 同时写入独立频道，防止被主频道 8 条窗口挤出。
            if (type == IntelEvent.DeathSOS)
            {
                if (!sosChannels.TryGetValue(team, out var sosList))
                {
                    sosList = new List<Entry>();
                    sosChannels[team] = sosList;
                }
                sosList.Add(e);
                if (sosList.Count > MaxSOS)
                    sosList.RemoveRange(0, sosList.Count - MaxSOS);
            }

            list.Add(e);
            if (list.Count > MaxEntries)
                list.RemoveRange(0, list.Count - MaxEntries);
        }

        /// <summary>
        /// 读取某队窗口内的最近事件（新→旧），最多 max 条。观测收集在服务端调用。
        /// </summary>
        public static List<Entry> GetRecent(int team, int max = MaxEntries)
        {
            var result = new List<Entry>();
            if (team < 0) return result;
            if (!channels.TryGetValue(team, out var list)) return result;

            double now = NetworkTime.time;
            // 从最新往回收集窗口内的条目。
            for (int i = list.Count - 1; i >= 0 && result.Count < max; i--)
            {
                if (now - list[i].time > WindowSeconds) break;
                result.Add(list[i]);
            }
            return result;
        }

        /// <summary>清空全部频道与节流表（回合重开/场景切换时由会话管理器调用）。</summary>
        public static void Reset()
        {
            channels.Clear();
            sosChannels.Clear();
            lastEmit.Clear();
        }

        /// <summary>
        /// PHASE9: 读取某队最近的 DeathSOS 事件（独立频道，不受主频道 8 条窗口限制）。
        /// </summary>
        public static List<Entry> GetSOS(int team, int max = MaxSOS)
        {
            var result = new List<Entry>();
            if (team < 0) return result;
            if (!sosChannels.TryGetValue(team, out var list)) return result;

            double now = NetworkTime.time;
            for (int i = list.Count - 1; i >= 0 && result.Count < max; i--)
            {
                if (now - list[i].time > WindowSeconds) break;
                result.Add(list[i]);
            }
            return result;
        }
    }
}
