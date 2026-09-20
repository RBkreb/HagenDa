using HagenDa.Networking;
using NUnit.Framework;

namespace HagenDa.Tests.EditMode.Match
{
    /// <summary>
    /// 部署名册(PHASE15 局中系统)。规格:开局所有实体未部署,选择部署点后才落地,
    /// 未选择者不进入地图。这里的每个断言对应一句话规格,改坏任何一条都会出现
    /// "开局就全员空降"或"选了却不落地"。
    /// </summary>
    [TestFixture]
    public class DeployRosterTests
    {
        private DeployRoster _roster;

        [SetUp]
        public void SetUp() => _roster = new DeployRoster();

        [Test]
        public void FreshRoster_IsEmpty()
        {
            Assert.AreEqual(0, _roster.ChoiceCount);
            Assert.AreEqual(0, _roster.DeployedCount);
        }

        [Test]
        public void BeginMatch_ClearsEverything()
        {
            _roster.SetChoice(1, 1);
            _roster.CommitDeployment();

            _roster.BeginMatch();

            Assert.AreEqual(0, _roster.ChoiceCount, "新一局开始:所有实体暂不部署。");
            Assert.AreEqual(0, _roster.DeployedCount);
            Assert.IsFalse(_roster.HasChoice(1));
        }

        [Test]
        public void ZeroChoice_IsIgnored()
        {
            _roster.SetChoice(7, 0);
            Assert.AreEqual(0, _roster.ChoiceCount, "choice=0 表示未选择,不记录。");
            Assert.IsFalse(_roster.HasChoice(7));
        }

        [Test]
        public void SetChoice_RecordsWithoutDeploying()
        {
            _roster.SetChoice(3, 2);

            Assert.IsTrue(_roster.HasChoice(3));
            Assert.AreEqual(2, _roster.GetChoice(3));
            Assert.AreEqual(0, _roster.DeployedCount,
                "仅选择不算部署 —— 正式开局前模型不得进入地图。");
        }

        [Test]
        public void SetChoice_Overwrites()
        {
            _roster.SetChoice(3, 1);
            _roster.SetChoice(3, 4);
            Assert.AreEqual(4, _roster.GetChoice(3), "重新选择应覆盖旧选择。");
            Assert.AreEqual(1, _roster.ChoiceCount);
        }

        [Test]
        public void CommitDeployment_ReturnsOnlyChosenEntities()
        {
            _roster.SetChoice(5, 1);
            _roster.SetChoice(2, 3);
            // 实体 9 未选择

            var committed = _roster.CommitDeployment();

            CollectionAssert.AreEqual(new[] { 2, 5 }, committed,
                "只部署已选择的实体,且顺序稳定(升序)。");
            Assert.AreEqual(2, _roster.DeployedCount);
            Assert.IsFalse(_roster.HasChoice(9));
        }

        [Test]
        public void CommitDeployment_IsIdempotent()
        {
            _roster.SetChoice(5, 1);
            _roster.CommitDeployment();
            var second = _roster.CommitDeployment();

            Assert.AreEqual(0, second.Count, "重复提交不应重复部署同一实体。");
            Assert.AreEqual(1, _roster.DeployedCount);
        }

        [Test]
        public void UnchosenEntities_StayOut_ThenCanDeployLater()
        {
            _roster.SetChoice(5, 1);
            _roster.CommitDeployment();
            Assert.AreEqual(0, _roster.GetChoice(9), "未选择者暂不部署。");

            // 对局中补选 → 单独落地。
            _roster.SetChoice(9, 2);
            Assert.AreEqual(9, _roster.DeploySingle(9));
            Assert.IsTrue(_roster.DeployedEntities().Contains(9));
        }

        [Test]
        public void DeploySingle_WithoutChoice_ReturnsMinusOne()
        {
            Assert.AreEqual(-1, _roster.DeploySingle(42),
                "没选过部署点的实体不能被单点落地。");
        }

        [Test]
        public void DeploySingle_Twice_SecondReturnsMinusOne()
        {
            _roster.SetChoice(1, 1);
            Assert.AreEqual(1, _roster.DeploySingle(1));
            Assert.AreEqual(-1, _roster.DeploySingle(1), "已部署实体不得重复落地。");
        }

        [Test]
        public void Remove_ClearsEntityState()
        {
            _roster.SetChoice(1, 1);
            _roster.CommitDeployment();
            _roster.Remove(1);

            Assert.IsFalse(_roster.HasChoice(1));
            Assert.AreEqual(0, _roster.DeployedCount, "实体销毁/真人退出后名册应清干净。");
        }

        [Test]
        public void DeployedEntities_AreSorted()
        {
            _roster.SetChoice(9, 1);
            _roster.SetChoice(1, 1);
            _roster.SetChoice(5, 1);
            _roster.CommitDeployment();

            CollectionAssert.AreEqual(new[] { 1, 5, 9 }, _roster.DeployedEntities());
        }
    }
}
