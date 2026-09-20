using HagenDa.Networking;
using NUnit.Framework;

namespace HagenDa.Tests.EditMode.Match
{
    /// <summary>
    /// 房间相位迁移规则(PHASE15 局间系统)。这些规则决定"什么时候能开局/能部署"，
    /// 一旦放松，就会出现"地图没加载完就进对局"或"对局中有人被踢回准备态"这类无法
    /// 在现场复现的状态机 bug，因此把合法迁移表与枚举序号一起钉住。
    /// </summary>
    [TestFixture]
    public class RoomPhaseRulesTests
    {
        // ---------------------------------------------------------------
        // 合法迁移表
        // ---------------------------------------------------------------

        [Test]
        public void Idle_OnlyTransitionsToReady()
        {
            Assert.IsTrue(RoomPhaseRules.CanTransition(RoomPhase.Idle, RoomPhase.Ready),
                "host 决定开局:空闲 → 准备。");
            Assert.IsFalse(RoomPhaseRules.CanTransition(RoomPhase.Idle, RoomPhase.Match),
                "空闲 → 对局 跳过资源加载,必须被拒绝。");
            Assert.IsFalse(RoomPhaseRules.CanTransition(RoomPhase.Idle, RoomPhase.Idle),
                "同态迁移不是有效事件。");
        }

        [Test]
        public void Ready_TransitionsToIdleOrMatch()
        {
            Assert.IsTrue(RoomPhaseRules.CanTransition(RoomPhase.Ready, RoomPhase.Match),
                "地图加载完成 + 小队分配完成:准备 → 对局。");
            Assert.IsTrue(RoomPhaseRules.CanTransition(RoomPhase.Ready, RoomPhase.Idle),
                "取消开局 / 解散房间:准备 → 空闲。");
        }

        [Test]
        public void Match_OnlyReturnsToIdle()
        {
            Assert.IsTrue(RoomPhaseRules.CanTransition(RoomPhase.Match, RoomPhase.Idle),
                "对局正常结束 / 真人全退:对局 → 空闲。");
            Assert.IsFalse(RoomPhaseRules.CanTransition(RoomPhase.Match, RoomPhase.Ready),
                "对局不能回退到准备态。");
            Assert.IsFalse(RoomPhaseRules.CanTransition(RoomPhase.Match, RoomPhase.Match),
                "重复进入对局必须被拒绝。");
        }

        [Test]
        public void DescribeTransition_ReportsIllegalMoves()
        {
            Assert.AreEqual("Idle → Ready", RoomPhaseRules.DescribeTransition(RoomPhase.Idle, RoomPhase.Ready));
            StringAssert.Contains("非法迁移", RoomPhaseRules.DescribeTransition(RoomPhase.Match, RoomPhase.Ready));
        }

        // ---------------------------------------------------------------
        // 语义谓词
        // ---------------------------------------------------------------

        [Test]
        public void OnlyMatchPhase_AllowsDeploy()
        {
            Assert.IsTrue(RoomPhaseRules.AllowsDeploy(RoomPhase.Match));
            Assert.IsFalse(RoomPhaseRules.AllowsDeploy(RoomPhase.Idle),
                "空闲态模型不得进入地图(规格:进入游戏时不加载任何地图/模型)。");
            Assert.IsFalse(RoomPhaseRules.AllowsDeploy(RoomPhase.Ready));
        }

        [Test]
        public void OnlyIdlePhase_AllowsHostStart()
        {
            Assert.IsTrue(RoomPhaseRules.AllowsHostStart(RoomPhase.Idle));
            Assert.IsFalse(RoomPhaseRules.AllowsHostStart(RoomPhase.Ready),
                "已在准备中,重复开局必须被拒绝。");
            Assert.IsFalse(RoomPhaseRules.AllowsHostStart(RoomPhase.Match),
                "对局中不能直接开局。");
        }

        // ---------------------------------------------------------------
        // 协议:枚举序号以 int 上 SyncVar
        // ---------------------------------------------------------------

        [Test]
        public void RoomPhase_Ordinals_AreStable()
        {
            Assert.AreEqual(0, (int)RoomPhase.Idle);
            Assert.AreEqual(1, (int)RoomPhase.Ready);
            Assert.AreEqual(2, (int)RoomPhase.Match);
        }

        [Test]
        public void FromInt_RejectsOutOfRange()
        {
            Assert.AreEqual(RoomPhase.Match, RoomPhaseRules.FromInt(2));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => RoomPhaseRules.FromInt(99));
        }
    }
}
