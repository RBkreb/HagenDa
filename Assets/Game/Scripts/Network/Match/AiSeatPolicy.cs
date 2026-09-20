using System;

namespace HagenDa.Networking
{
    /// <summary>一个 AI 席位的可替换状态(服务器视角)。</summary>
    public readonly struct AiSeat
    {
        /// <summary>席位稳定标识(实体 InstanceID 或槽位号)。</summary>
        public readonly int Id;
        /// <summary>该 AI 当前所属队伍(0/1,-1 未分配)。</summary>
        public readonly int Team;
        /// <summary>该 AI 是否已死亡(死亡席位优先被真人挤占)。</summary>
        public readonly bool Dead;

        public AiSeat(int id, int team, bool dead)
        {
            Id = id;
            Team = team;
            Dead = dead;
        }
    }

    /// <summary>挤占决策结果。</summary>
    public readonly struct AiDisplacement
    {
        /// <summary>是否找到可挤占的席位。</summary>
        public readonly bool Found;
        /// <summary>被选中席位的 Id(未找到时为 -1)。</summary>
        public readonly int SeatId;
        /// <summary>该席位是否已死亡(否则需要强制击杀后再占用)。</summary>
        public readonly bool AlreadyDead;
        /// <summary>被选中席位所属队伍。</summary>
        public readonly int Team;

        public AiDisplacement(bool found, int seatId, bool alreadyDead, int team)
        {
            Found = found;
            SeatId = seatId;
            AlreadyDead = alreadyDead;
            Team = team;
        }

        public static readonly AiDisplacement None = new AiDisplacement(false, -1, false, -1);
    }

    /// <summary>
    /// 真人挤占 AI 的席位策略(PHASE15 局间系统,纯逻辑 → EditMode 直测)。
    ///
    /// 规格:"真人挤占优先挤占已死亡的 AI,无死亡 AI 则强制击杀 1 个 AI"。
    ///
    /// 本类只做**选择**(选哪个席位 / 是否需要强制击杀);击杀与转移由
    /// <c>NetworkRoomController</c> 执行。选择顺序:
    ///  1. 优先在**目标队伍**内选已死亡席位(失败则扩大到场任意死席);
    ///  2. 无死席则在目标队伍内选一个**存活**席位(需要强制击杀);
    ///  3. 目标队伍无席位则给出 <see cref="AiDisplacement.None"/>。
    ///
    /// 席位按传入顺序稳定选择(调用方应先排序,保证可复现)。
    /// </summary>
    public static class AiSeatPolicy
    {
        /// <summary>
        /// 为新加入真人选择要挤占的 AI 席位。
        /// </summary>
        /// <param name="seats">当前可挤占的 AI 席位。</param>
        /// <param name="preferredTeam">真人的目标队伍;传 -1 表示不限定。</param>
        public static AiDisplacement Choose(
            System.Collections.Generic.IReadOnlyList<AiSeat> seats, int preferredTeam)
        {
            if (seats == null || seats.Count == 0) return AiDisplacement.None;

            // 1. 目标队伍内的死席。
            var hit = FirstDead(seats, preferredTeam, teamRestricted: true);
            if (hit.Found) return hit;

            // 2. 任意队伍的死席(有死席就不该浪费一次强制击杀)。
            hit = FirstDead(seats, team: -1, teamRestricted: false);
            if (hit.Found) return hit;

            // 3. 目标队伍内的活席 → 强制击杀。
            var live = FirstAlive(seats, preferredTeam, teamRestricted: true);
            if (live.Found) return live;

            // 4. 任意队伍的活席。
            return FirstAlive(seats, team: -1, teamRestricted: false);
        }

        private static AiDisplacement FirstDead(
            System.Collections.Generic.IReadOnlyList<AiSeat> seats, int team, bool teamRestricted)
        {
            for (int i = 0; i < seats.Count; i++)
            {
                var s = seats[i];
                if (!s.Dead) continue;
                if (teamRestricted && s.Team != team) continue;
                return new AiDisplacement(true, s.Id, alreadyDead: true, s.Team);
            }
            return AiDisplacement.None;
        }

        private static AiDisplacement FirstAlive(
            System.Collections.Generic.IReadOnlyList<AiSeat> seats, int team, bool teamRestricted)
        {
            for (int i = 0; i < seats.Count; i++)
            {
                var s = seats[i];
                if (s.Dead) continue;
                if (teamRestricted && s.Team != team) continue;
                return new AiDisplacement(true, s.Id, alreadyDead: false, s.Team);
            }
            return AiDisplacement.None;
        }
    }
}
