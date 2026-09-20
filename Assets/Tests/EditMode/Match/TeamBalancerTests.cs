using HagenDa.Networking;
using NUnit.Framework;

namespace HagenDa.Tests.EditMode.Match
{
    /// <summary>
    /// 小队分配(PHASE15 局间系统)。规格两条约束都必须成立,且互相拉扯时优先级明确:
    /// 先均摊红蓝,再让同队真人聚拢到同一小队。
    /// </summary>
    [TestFixture]
    public class TeamBalancerTests
    {
        [Test]
        public void NoHumans_ProducesEmptyAssignment()
        {
            var a = TeamBalancer.AssignHumans(0, 5);
            Assert.AreEqual(0, a.Length);
        }

        [Test]
        public void TeamsAreBalanced_RedGetsExtraOnOddCount()
        {
            // 7 人:红 4 / 蓝 3(红先)。
            var a = TeamBalancer.AssignHumans(7, 5);
            Assert.AreEqual(4, TeamBalancer.CountTeam(a, TeamBalancer.Red));
            Assert.AreEqual(3, TeamBalancer.CountTeam(a, TeamBalancer.Blue));
        }

        [Test]
        public void EvenCount_SplitsEvenly()
        {
            var a = TeamBalancer.AssignHumans(6, 5);
            Assert.AreEqual(3, TeamBalancer.CountTeam(a, TeamBalancer.Red));
            Assert.AreEqual(3, TeamBalancer.CountTeam(a, TeamBalancer.Blue));
        }

        [Test]
        public void SameTeamHumans_ShareLowestSquadFirst()
        {
            // squadSize=5。红队真人落在 i=0,2,4,6,8,10,…;队内第 6 人(i=10)才溢出到 S1。
            var a = TeamBalancer.AssignHumans(12, 5);

            Assert.AreEqual(0, a[0].Squad, "红队第 1 人 → S0");
            Assert.AreEqual(0, a[2].Squad, "红队第 2 人 → S0");
            Assert.AreEqual(0, a[8].Squad, "红队第 5 人 → S0(填满)");
            Assert.AreEqual(1, a[10].Squad, "红队第 6 人 → S1(溢出)");
        }

        [Test]
        public void Squads_StartFromZero_PerTeam()
        {
            var a = TeamBalancer.AssignHumans(4, 5);
            // a[0]=红S0, a[1]=蓝S0, a[2]=红S0, a[3]=蓝S0
            Assert.AreEqual(TeamBalancer.Red, a[0].Team);
            Assert.AreEqual(0, a[0].Squad);
            Assert.AreEqual(TeamBalancer.Blue, a[1].Team);
            Assert.AreEqual(0, a[1].Squad, "蓝队也从小队 0 开始,两队小队序号互不影响。");
        }

        [Test]
        public void AllAssignments_AreAssigned()
        {
            var a = TeamBalancer.AssignHumans(30, 5);
            foreach (var s in a)
                Assert.IsTrue(s.IsAssigned, $"{(s.Team, s.Squad)} 应已分配。");
        }

        [Test]
        public void RealisticRoster_TwoPerTeam_InSameSquad()
        {
            // 4 名真人(2v2;例 MapV1_2v2 的红 1 蓝 1 席位扩建后的常见形态):
            // 同队两人必须同小队。
            var a = TeamBalancer.AssignHumans(4, 5);
            Assert.AreEqual(a[0].Team, a[2].Team);
            Assert.AreEqual(a[0].Squad, a[2].Squad, "同队真人应聚拢在同一小队。");
            Assert.AreEqual(a[1].Team, a[3].Team);
            Assert.AreEqual(a[1].Squad, a[3].Squad);
        }

        // ---------------------------------------------------------------
        // AI 填位
        // ---------------------------------------------------------------

        [Test]
        public void AiFill_FillsRemainingSeats()
        {
            // 编制 30,真人 4 → AI 26。
            Assert.AreEqual(26, TeamBalancer.AiFillCount(30, 4));
        }

        [Test]
        public void AiFill_NeverNegative_WhenHumansExceedCapacity()
        {
            Assert.AreEqual(0, TeamBalancer.AiFillCount(10, 12),
                "真人超编时 AI 填位为 0(不能变成负数)。");
        }

        [Test]
        public void AiFill_ExactCapacity_YieldsZero()
        {
            Assert.AreEqual(0, TeamBalancer.AiFillCount(30, 30));
        }

        [Test]
        public void RejectsBadArguments()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => TeamBalancer.AssignHumans(-1, 5));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => TeamBalancer.AssignHumans(4, 0));
        }
    }
}
