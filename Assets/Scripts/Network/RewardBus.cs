using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Reward event bus (ML 训练共识 §5). Combat / support systems push events;
    /// the bus resolves the owning <see cref="MLAgentBridge"/> and adds reward.
    /// Human players and scripted sparring AI have no bridge → no-op.
    ///
    /// Assists: a damage log (per victim) records recent attackers; on kill, the
    /// killer's squadmates who damaged the victim within
    /// <see cref="AssistWindowSeconds"/> get <see cref="AssistReward"/>.
    /// Team share: every kill pays <see cref="TeamShareRatio"/> of the kill reward
    /// split across the killer's whole team.
    /// </summary>
    public static class RewardBus
    {
        // ---- 共识 §5 奖励常数 ----
        public const float DamageDealtPerHp = 0.002f;
        public const float KillReward = 0.2f;
        public const float AssistReward = 0.05f;
        public const float DeathPenalty = -0.1f;
        public const float DamageTakenPerHp = -0.001f;
        public const float SupportReward = 0.05f;    // 治疗 / 补给 / 除颤
        public const float InterceptorReward = 0.03f; // 拦截挡弹
        public const float MarkKillReward = 0.05f;   // 标记引导击杀（标记者）
        public const float BeaconReward = 0.02f;     // 小队经信标重生（部署者）
        public const float TeamShareRatio = 0.2f;    // 团队共享比例
        public const float WinReward = 1f;
        public const float LossPenalty = -0.5f;

        public const float AssistWindowSeconds = 5f;

        public enum SupportKind { Heal, Supply, Rescue }

        // ---- combatant → bridge registry ----
        private static readonly Dictionary<int, MLAgentBridge> bridges =
            new Dictionary<int, MLAgentBridge>();

        // ---- damage log: victim instanceId → (attacker instanceId, NetworkTime) ----
        private static readonly Dictionary<int, List<(int attacker, double time)>> damageLog =
            new Dictionary<int, List<(int, double)>>();

        public static void Register(NetworkCombatant combatant, MLAgentBridge bridge)
        {
            if (combatant != null && bridge != null)
                bridges[combatant.GetInstanceID()] = bridge;
        }

        public static void Unregister(NetworkCombatant combatant)
        {
            if (combatant != null)
                bridges.Remove(combatant.GetInstanceID());
        }

        /// <summary>
        /// 回合重开：只清伤害记录。桥接注册表保留——bridge 的注册生命周期
        /// 与实体一致（OnEnable/OnDisable），回合重置不应清空（否则所有后续
        /// 奖励 Award 找不到 bridge 而丢失，S1-v2 训练失败的根因）。
        /// </summary>
        public static void Reset()
        {
            damageLog.Clear();
        }

        private static void Award(NetworkCombatant c, float amount)
        {
            if (c == null || amount == 0f) return;
            if (bridges.TryGetValue(c.GetInstanceID(), out var b) && b != null)
                b.AddReward(amount);
        }

        // ---------------------------------------------------------------
        // COMBAT (hooks in NetworkPlayerHealth)
        // ---------------------------------------------------------------

        /// <summary>受击结算：攻方伤害奖励 + 守方受击惩罚 + 助攻记录。</summary>
        public static void Damage(NetworkPlayerHealth victim, NetworkCombatant attacker, float hpLost)
        {
            if (victim == null || hpLost <= 0f) return;
            var victimCombatant = victim.GetComponent<NetworkCombatant>();

            // S1 评估统计（服务器端，评估场景中才有）。
            var evalStats = Object.FindObjectOfType<S1EvaluationStats>();
            if (evalStats != null && attacker != null) evalStats.RecordHit();

            if (attacker != null && attacker != victimCombatant)
            {
                Award(attacker, DamageDealtPerHp * hpLost);

                // 助攻伤害记录。
                int vid = victim.GetInstanceID();
                if (!damageLog.TryGetValue(vid, out var log))
                {
                    log = new List<(int, double)>();
                    damageLog[vid] = log;
                }
                log.Add((attacker.GetInstanceID(), NetworkTime.time));
            }

            Award(victimCombatant, DamageTakenPerHp * hpLost);
        }

        /// <summary>击杀结算：击杀 + 阵亡 + 小队助攻 + 团队共享 + 标记引导。</summary>
        public static void Kill(NetworkPlayerHealth victim, NetworkCombatant killer)
        {
            var victimCombatant = victim != null ? victim.GetComponent<NetworkCombatant>() : null;
            if (victimCombatant == null) return;

            // S1 评估统计。
            var evalStats2 = Object.FindObjectOfType<S1EvaluationStats>();
            if (evalStats2 != null) evalStats2.RecordKill();

            // 阵亡惩罚。
            Award(victimCombatant, DeathPenalty);

            if (killer == null || killer == victimCombatant) return;
            int killerTeam = killer.teamId;

            // 击杀奖励。
            Award(killer, KillReward);

            // 标记引导击杀：受害者被击杀方阵营标记且标记仍有效 → 标记者获奖。
            if (victimCombatant.IsMarked && victimCombatant.markedByTeam == killerTeam &&
                victimCombatant.lastMarker != null && victimCombatant.lastMarker != killer)
            {
                Award(victimCombatant.lastMarker, MarkKillReward);
            }

            // 小队助攻：击杀者同小队成员在窗口期内伤害过受害者。
            double now = NetworkTime.time;
            if (damageLog.TryGetValue(victim.GetInstanceID(), out var log))
            {
                var paid = new HashSet<int>();
                for (int i = log.Count - 1; i >= 0; i--)
                {
                    if (now - log[i].time > AssistWindowSeconds) break;
                    if (paid.Contains(log[i].attacker)) continue;
                    paid.Add(log[i].attacker);

                    var assister = ResolveCombatant(log[i].attacker);
                    if (assister == null || assister == killer) continue;
                    if (assister.teamId != killerTeam || assister.squadId != killer.squadId) continue;
                    Award(assister, AssistReward);
                }
                damageLog.Remove(victim.GetInstanceID());
            }

            // 团队共享：击杀收益的 TeamShareRatio 平分给击杀者全队（不含击杀者）。
            float share = KillReward * TeamShareRatio / 5f;   // 5 人队
            foreach (var kv in bridges)
            {
                // bridges 只含 ML agent；枚举值取 combatant 引用判定同队。
                var other = kv.Value != null ? kv.Value.Combatant : null;
                if (other == null || other == killer) continue;
                if (other.teamId != killerTeam) continue;
                Award(other, share);
            }
        }

        // ---------------------------------------------------------------
        // SUPPORT / UTILITY (hooks in NetworkEquipment / deployables)
        // ---------------------------------------------------------------

        public static void SupportUtility(NetworkCombatant user, SupportKind kind)
        {
            Award(user, SupportReward);
        }

        public static void InterceptorBlock(NetworkCombatant owner)
        {
            Award(owner, InterceptorReward);
        }

        public static void BeaconRespawn(NetworkCombatant owner)
        {
            Award(owner, BeaconReward);
        }

        // ---------------------------------------------------------------
        // MATCH END (called by TrainingSessionManager)
        // ---------------------------------------------------------------

        public static void MatchEnd(int winnerTeam)
        {
            foreach (var kv in bridges)
            {
                var c = kv.Value != null ? kv.Value.Combatant : null;
                if (c == null || c.teamId < 0) continue;
                kv.Value.AddReward(c.teamId == winnerTeam ? WinReward : LossPenalty);
            }
        }

        // ---------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------

        private static NetworkCombatant ResolveCombatant(int instanceId)
        {
            foreach (var kv in bridges)
            {
                var c = kv.Value != null ? kv.Value.Combatant : null;
                if (c != null && c.GetInstanceID() == instanceId) return c;
            }
            return null;
        }
    }
}
