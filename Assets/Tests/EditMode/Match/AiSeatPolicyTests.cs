using System.Collections.Generic;
using HagenDa.Networking;
using NUnit.Framework;

namespace HagenDa.Tests.EditMode.Match
{
    /// <summary>
    /// 真人挤占 AI 的席位选择(PHASE15 局间系统)。
    /// 规格:"优先挤占已死亡的 AI,无死亡 AI 则强制击杀 1 个 AI"——
    /// 把"优先死席 / 必要时强制击杀 / 目标队伍内优先"三件事钉住,
    /// 因为强制击杀会真实改血,选错席位的代价是白杀一个 AI。
    /// </summary>
    [TestFixture]
    public class AiSeatPolicyTests
    {
        private static AiSeat Seat(int id, int team, bool dead) => new AiSeat(id, team, dead);

        [Test]
        public void EmptySeats_NoDisplacement()
        {
            var r = AiSeatPolicy.Choose(new List<AiSeat>(), preferredTeam: TeamBalancer.Red);
            Assert.IsFalse(r.Found);
            Assert.AreEqual(-1, r.SeatId);
        }

        [Test]
        public void DeadSeatInPreferredTeam_IsChosen()
        {
            var seats = new List<AiSeat>
            {
                Seat(10, TeamBalancer.Red, dead: true),
                Seat(11, TeamBalancer.Blue, dead: true),
            };
            var r = AiSeatPolicy.Choose(seats, TeamBalancer.Blue);
            Assert.IsTrue(r.Found);
            Assert.AreEqual(11, r.SeatId, "应选目标队伍内的死席。");
            Assert.IsTrue(r.AlreadyDead, "死席无需强制击杀。");
        }

        [Test]
        public void DeadSeatInOtherTeam_BeatsLiveSeatInPreferredTeam()
        {
            // 死了的 AI 不该被浪费一次强制击杀 —— 即使不在目标队伍,也优先占用。
            var seats = new List<AiSeat>
            {
                Seat(20, TeamBalancer.Red, dead: false),
                Seat(21, TeamBalancer.Blue, dead: true),
            };
            var r = AiSeatPolicy.Choose(seats, TeamBalancer.Red);
            Assert.AreEqual(21, r.SeatId, "任意队伍的死席优先于本队活席。");
            Assert.IsTrue(r.AlreadyDead);
        }

        [Test]
        public void NoDeadSeats_KillsLiveSeatInPreferredTeam()
        {
            var seats = new List<AiSeat>
            {
                Seat(30, TeamBalancer.Blue, dead: false),
                Seat(31, TeamBalancer.Red, dead: false),
            };
            var r = AiSeatPolicy.Choose(seats, TeamBalancer.Red);
            Assert.AreEqual(31, r.SeatId, "无死席时在目标队伍内强制击杀一个。");
            Assert.IsFalse(r.AlreadyDead, "活席需要强制击杀。");
        }

        [Test]
        public void NoSeatsInPreferredTeam_FallsBackToAnyLive()
        {
            var seats = new List<AiSeat>
            {
                Seat(40, TeamBalancer.Red, dead: false),
            };
            var r = AiSeatPolicy.Choose(seats, preferredTeam: TeamBalancer.Blue);
            Assert.AreEqual(40, r.SeatId, "目标队伍无席位时退化为任意队伍活席。");
            Assert.IsFalse(r.AlreadyDead);
        }

        [Test]
        public void UnrestrictedTeam_ChoosesFirstDead()
        {
            var seats = new List<AiSeat>
            {
                Seat(50, TeamBalancer.Red, dead: true),
                Seat(51, TeamBalancer.Blue, dead: true),
            };
            var r = AiSeatPolicy.Choose(seats, preferredTeam: -1);
            Assert.AreEqual(50, r.SeatId, "不限定队伍时选第一个死席(顺序稳定)。");
        }

        [Test]
        public void Selection_IsStable_NotRandom()
        {
            // 同一输入必须给同一答案:挤占要可复现,否则回放/测试都不可信。
            var seats = new List<AiSeat>
            {
                Seat(60, TeamBalancer.Red, dead: false),
                Seat(61, TeamBalancer.Red, dead: false),
            };
            Assert.AreEqual(AiSeatPolicy.Choose(seats, TeamBalancer.Red).SeatId,
                            AiSeatPolicy.Choose(seats, TeamBalancer.Red).SeatId);
        }
    }
}
