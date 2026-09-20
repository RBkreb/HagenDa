using System;

namespace HagenDa.Networking
{
    /// <summary>一名真人的小队归属(小队分配的唯一产物)。</summary>
    public readonly struct SquadAssignment
    {
        /// <summary>0 = 红,1 = 蓝。</summary>
        public readonly int Team;
        /// <summary>队内小队索引(0 起)。</summary>
        public readonly int Squad;

        public SquadAssignment(int team, int squad)
        {
            Team = team;
            Squad = squad;
        }

        public bool IsAssigned => Team >= 0 && Squad >= 0;

        public override string ToString() =>
            IsAssigned ? $"{(Team == 0 ? "Red" : "Blue")}/S{Squad}" : "Unassigned";
    }

    /// <summary>
    /// 小队分配(PHASE15 局间系统,纯逻辑 → EditMode 直测)。
    ///
    /// 规格:"优先把真人分在同一小队,且均摊红蓝双方,完成分配后用 AI 填充剩余"。
    ///
    /// 因此真人分配有两条约束,顺序固定:
    ///  1. **均摊**:红蓝轮流收人(红先,故奇数人在红队多一人 —— 与既有
    ///     <see cref="HumanTeamPolicy.Balance"/> 的平局进红一致)。
    ///  2. **同队聚拢**:同一队的真人从最低小队号开始填,填满 <c>squadSize</c> 再进下一队,
    ///     这样真人优先落在同一小队(而非打散)。
    ///
    /// AI 只负责"填剩余席位":每队补齐到编制上限。
    /// </summary>
    public static class TeamBalancer
    {
        public const int Red = 0;
        public const int Blue = 1;

        /// <summary>
        /// 把 <paramref name="humanCount"/> 名真人分配到队伍/小队。
        /// 返回数组下标即真人序号,长度 == humanCount。
        /// </summary>
        /// <param name="squadSize">单个小队容量(必须 &gt; 0)。</param>
        public static SquadAssignment[] AssignHumans(int humanCount, int squadSize)
        {
            if (humanCount < 0) throw new ArgumentOutOfRangeException(nameof(humanCount));
            if (squadSize <= 0) throw new ArgumentOutOfRangeException(nameof(squadSize));

            var result = new SquadAssignment[humanCount];
            for (int i = 0; i < humanCount; i++)
            {
                // 红先轮换 → 均衡且奇数人时红多一。
                int team = (i % 2 == 0) ? Red : Blue;

                // 该队内第几个真人(0 起)→ 决定落在哪个小队。
                int indexWithinTeam = i / 2;
                int squad = indexWithinTeam / squadSize;

                result[i] = new SquadAssignment(team, squad);
            }
            return result;
        }

        /// <summary>某队还需多少 AI 才能填满编制(不会为负)。</summary>
        public static int AiFillCount(int teamCapacity, int humansOnTeam)
        {
            int missing = teamCapacity - humansOnTeam;
            return missing > 0 ? missing : 0;
        }

        /// <summary>按小队分配结果统计某队真人数量。</summary>
        public static int CountTeam(SquadAssignment[] assignments, int team)
        {
            if (assignments == null) return 0;
            int n = 0;
            foreach (var a in assignments)
                if (a.Team == team) n++;
            return n;
        }
    }
}
