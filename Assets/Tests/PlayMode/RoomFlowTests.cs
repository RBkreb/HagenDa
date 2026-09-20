using System.Collections;
using System.Collections.Generic;
using HagenDa.Networking;
using HagenDa.Tests.PlayMode.Harness;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HagenDa.Tests.PlayMode
{
    /// <summary>
    /// 局间系统(PHASE15)的网络层行为:房间相位迁移、真人分队、AI 挤占/补位、部署门控。
    ///
    /// 与 EditMode 的分工:EditMode 钉住**纯规则**(RoomPhaseRules / TeamBalancer /
    /// AiSeatPolicy / DeployRoster),这里验证这些规则在**真实 NetworkBehaviour 上**
    /// 的接线 —— 相位 SyncVar、[Server] 方法与强制击杀确实被调用。
    ///
    /// harness 无端口、无 NetworkManager:实体用 PlayerFixture/SpawnComponent 造出。
    /// </summary>
    [TestFixture]
    public class RoomFlowTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private NetworkRoomController _room;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            PlayModeServer.StartServer();
            _room = PlayModeServer.SpawnComponent<NetworkRoomController>("RoomController");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var go in _spawned)
                PlayModeServer.Destroy(go);
            _spawned.Clear();

            if (_room != null) PlayModeServer.Destroy(_room.gameObject);
            _room = null;

            PlayModeServer.StopServer();
            yield return null;
        }

        private NetworkAIController SpawnAi(Vector3 pos)
        {
            var entity = PlayerFixture.Create(pos, withAI: true, withFloor: true);
            _spawned.Add(entity.Go);
            Assert.IsNotNull(entity.AI, "前置条件:实体应挂有 NetworkAIController。");
            return entity.AI;
        }

        // ---------------------------------------------------------------
        // 开局部署(规格:未选择则暂不部署;AI 无界面直接落地)
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator AiSeat_PlacedAtMatchStart()
        {
            var ai = SpawnAi(new Vector3(0f, 0.05f, 0f));
            yield return null;
            ai.GetComponent<NetworkCombatant>().teamId = TeamBalancer.Red;
            var health = ai.GetComponent<NetworkPlayerHealth>();

            _room.StartMatchLoad();
            _room.CompleteMatchLoad();

            Assert.IsFalse(health.holdInPlace, "对局开始后 AI 相位冻结应解除。");
            Assert.IsFalse(health.awaitingInitialDeploy,
                "AI 无部署界面,开局应直接落地(不等待选点)。");
        }

        [UnityTest]
        public IEnumerator SpawnDuringIdle_IsHeldImmediately()
        {
            var ai = SpawnAi(new Vector3(0f, 0.05f, 0f));
            yield return null;
            var health = ai.GetComponent<NetworkPlayerHealth>();

            Assert.IsTrue(health.holdInPlace,
                "空闲相位生成的实体应立即冻结(规格:开局前模型不进入地图)。");
        }

        [UnityTest]
        public IEnumerator PhaseHold_ClearsWhenMatchStarts()
        {
            var ai = SpawnAi(new Vector3(0f, 0.05f, 0f));
            yield return null;
            var health = ai.GetComponent<NetworkPlayerHealth>();
            Assert.IsTrue(health.holdInPlace, "空闲相位生成 → 已冻结。");

            _room.StartMatchLoad();
            yield return null;
            Assert.IsTrue(health.holdInPlace, "准备相位应保持冻结。");

            _room.CompleteMatchLoad();
            yield return null;
            Assert.IsFalse(health.holdInPlace,
                "进入对局相位后冻结应解除(AI 随即落地,真人等待选点)。");
        }

        // ---------------------------------------------------------------
        // 挤占接线(状态层:非对局中不得挤占)
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Possession_OnlyAppliesDuringMatch()
        {
            var ai = SpawnAi(new Vector3(0f, 0.05f, 0f));
            yield return null;
            ai.GetComponent<NetworkCombatant>().teamId = TeamBalancer.Red;

            // 空闲态:不应挤占,调用方应回退到"新建身体"。
            Assert.IsFalse(_room.TryPossessAiForHuman(null, TeamBalancer.Red),
                "非对局相位不得挤占 AI(此时 AI 尚未收到开局部署)。");
            Assert.AreEqual(1, _room.AiSeatCount, "未挤占时席位应保留。");
        }

        [UnityTest]
        public IEnumerator Possession_DuringMatch_TakesOverSeat()
        {
            var ai = SpawnAi(new Vector3(0f, 0.05f, 0f));
            yield return null;
            ai.GetComponent<NetworkCombatant>().teamId = TeamBalancer.Red;

            _room.StartMatchLoad();
            _room.CompleteMatchLoad();

            // 无连接对象时仍应完成"登记真人 + 挤占席位"这一半。
            Assert.IsTrue(_room.TryPossessAiForHuman(null, TeamBalancer.Red),
                "对局中应能接管一个 AI 席位。");
            Assert.AreEqual(0, _room.AiSeatCount, "被接管的席位应从名册移除。");
            Assert.AreEqual(1, _room.AssignedHumanCount, "接管同时应登记该真人。");
        }

        // ---------------------------------------------------------------
        // 部署名册(网络层接线)
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Roster_RecordsChoices_WithoutDeploying()
        {
            yield return null;
            _room.RecordDeployChoice(entityId: 1, choice: 2);
            _room.RecordDeployChoice(entityId: 2, choice: 3);

            Assert.AreEqual(2, _room.Roster.ChoiceCount);
            Assert.AreEqual(0, _room.Roster.DeployedCount,
                "仅记录选择不算部署(正式开局前模型不进入地图)。");

            var committed = _room.CommitDeployment();
            CollectionAssert.AreEqual(new[] { 1, 2 }, committed);
        }

        // ---------------------------------------------------------------
        // 相位迁移(网络层)
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator StartsInIdlePhase()
        {
            yield return null;
            Assert.AreEqual(RoomPhase.Idle, _room.phase,
                "进房间默认空闲态:不加载地图、不分配小队。");
        }

        [UnityTest]
        public IEnumerator HostStart_MovesIdleToReady()
        {
            yield return null;
            Assert.IsTrue(_room.StartMatchLoad());
            Assert.AreEqual(RoomPhase.Ready, _room.phase, "host 开局 → 准备态。");
            Assert.IsFalse(_room.StartMatchLoad(), "已在准备中,重复开局必须被拒绝。");
        }

        [UnityTest]
        public IEnumerator ReadyToMatch_OnlyAfterLoadCompletes()
        {
            yield return null;
            _room.StartMatchLoad();
            Assert.AreEqual(RoomPhase.Ready, _room.phase);

            Assert.IsTrue(_room.CompleteMatchLoad(), "地图+小队就绪 → 对局态。");
            Assert.AreEqual(RoomPhase.Match, _room.phase);
            Assert.IsFalse(_room.CompleteMatchLoad(), "重复完成必须被拒绝。");
        }

        [UnityTest]
        public IEnumerator CannotSkipStraightToMatch()
        {
            yield return null;
            Assert.IsFalse(_room.CompleteMatchLoad(),
                "跳过准备态直接进对局必须被拒绝(资源未加载)。");
            Assert.AreEqual(RoomPhase.Idle, _room.phase);
        }

        [UnityTest]
        public IEnumerator EndMatch_ReturnsToIdle()
        {
            yield return null;
            _room.StartMatchLoad();
            _room.CompleteMatchLoad();
            Assert.IsTrue(_room.EndMatch());
            Assert.AreEqual(RoomPhase.Idle, _room.phase, "对局结束 → 空闲态。");
        }

        [UnityTest]
        public IEnumerator CancelFromReady_ReturnsToIdle()
        {
            yield return null;
            _room.StartMatchLoad();
            Assert.IsTrue(_room.CancelMatchLoad());
            Assert.AreEqual(RoomPhase.Idle, _room.phase);
        }

        // ---------------------------------------------------------------
        // 真人分队(均摊 + 同队聚拢)
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator HumansAreBalancedAcrossTeams()
        {
            yield return null;
            var a0 = _room.AssignHuman(connectionKey: 100);
            var a1 = _room.AssignHuman(connectionKey: 101);
            var a2 = _room.AssignHuman(connectionKey: 102);
            var a3 = _room.AssignHuman(connectionKey: 103);

            Assert.AreEqual(TeamBalancer.Red, a0.Team);
            Assert.AreEqual(TeamBalancer.Blue, a1.Team);
            Assert.AreEqual(TeamBalancer.Red, a2.Team);
            Assert.AreEqual(TeamBalancer.Blue, a3.Team);
            Assert.AreEqual(4, _room.AssignedHumanCount);
        }

        [UnityTest]
        public IEnumerator SameTeamHumans_ShareSquad()
        {
            yield return null;
            var red1 = _room.AssignHuman(200);
            _room.AssignHuman(201);
            var red2 = _room.AssignHuman(202);

            Assert.AreEqual(red1.Team, red2.Team);
            Assert.AreEqual(red1.Squad, red2.Squad, "同队真人应聚拢在同一小队。");
        }

        // ---------------------------------------------------------------
        // 真人挤占 AI(规格:优先死席,无死席则强制击杀 1 个 AI)
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator DisplaceAi_PrefersDeadSeat_WithoutKilling()
        {
            var ai = SpawnAi(new Vector3(0f, 0.05f, 0f));
            yield return null;

            var health = ai.GetComponent<NetworkPlayerHealth>();
            var combatant = ai.GetComponent<NetworkCombatant>();
            combatant.teamId = TeamBalancer.Red;

            // 让该 AI 先"已死"。
            health.ForceKill();
            Assert.IsTrue(health.IsDead, "前置条件:AI 应已死亡。");

            var victim = _room.DisplaceAiForHuman(TeamBalancer.Red);

            Assert.AreSame(ai, victim, "有死席时应占用死掉的那个 AI。");
            Assert.AreEqual(0, _room.AiSeatCount, "被挤占的席位应从名册移除。");
        }

        [UnityTest]
        public IEnumerator DisplaceAi_NoDeadSeat_ForceKillsOneAi()
        {
            var ai = SpawnAi(new Vector3(0f, 0.05f, 0f));
            yield return null;

            ai.GetComponent<NetworkCombatant>().teamId = TeamBalancer.Red;
            var health = ai.GetComponent<NetworkPlayerHealth>();
            Assert.IsFalse(health.IsDead, "前置条件:AI 初始存活。");

            var victim = _room.DisplaceAiForHuman(TeamBalancer.Red);

            Assert.AreSame(ai, victim, "无死席时应选中一个活席。");
            Assert.IsTrue(health.IsDead,
                "规格:无死亡 AI 则强制击杀 1 个 AI —— 被挤占的席位必须已死。");
        }

        [UnityTest]
        public IEnumerator DisplaceAi_NoSeats_ReturnsNull()
        {
            yield return null;
            Assert.IsNull(_room.DisplaceAiForHuman(TeamBalancer.Red),
                "没有 AI 席位时挤占应返回 null 而非报错。");
        }

        [UnityTest]
        public IEnumerator AiSpawnsAutoRegisterAsSeats()
        {
            yield return null;
            Assert.AreEqual(0, _room.AiSeatCount);

            SpawnAi(new Vector3(2f, 0.05f, 0f));
            yield return null;

            Assert.AreEqual(1, _room.AiSeatCount,
                "AI 生成后应自动登记为可挤占席位(NetworkAIController.OnStartServer)。");
        }

        // ---------------------------------------------------------------
        // 真人退出
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator LastHumanLeaving_ForcesMatchToEnd()
        {
            yield return null;
            _room.StartMatchLoad();
            _room.CompleteMatchLoad();
            _room.AssignHuman(300);
            Assert.AreEqual(RoomPhase.Match, _room.phase);

            _room.RemoveHuman(300);

            Assert.AreEqual(RoomPhase.Idle, _room.phase,
                "规格:对局内所有真人已退出 → 强制结束回到空闲态。");
        }

        [UnityTest]
        public IEnumerator RemovingUnknownHuman_IsNoOp()
        {
            yield return null;
            _room.StartMatchLoad();
            _room.CompleteMatchLoad();

            _room.RemoveHuman(999);   // 未登记

            Assert.AreEqual(RoomPhase.Match, _room.phase,
                "移除未登记的连接不应影响相位。");
        }

        // ---------------------------------------------------------------
        // host 判定
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator FirstConnection_BecomesHost()
        {
            yield return null;
            Assert.AreEqual(-1, _room.hostConnectionId, "初始无 host 登记。");

            // 服务器自身(null 连接)恒为 host。
            Assert.IsTrue(_room.IsHostConnection(null));
        }

        // ---------------------------------------------------------------
        // 部署门控(相位冻结)
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator PhaseHold_FreezesBodyAndBlocksDeploy()
        {
            var entity = PlayerFixture.Create(new Vector3(0f, 0.05f, 0f), withFloor: true);
            _spawned.Add(entity.Go);
            yield return entity.Settle();

            entity.Health.SetPhaseHold(true);

            Assert.IsTrue(entity.Health.holdInPlace, "相位冻结标志应置位。");
            Assert.IsTrue(entity.Health.awaitingInitialDeploy,
                "冻结期间仍需等待选点(不能直接落地)。");
        }

        [UnityTest]
        public IEnumerator PhaseHold_Release_RestoresDeployWaiting()
        {
            var entity = PlayerFixture.Create(new Vector3(0f, 0.05f, 0f), withFloor: true);
            _spawned.Add(entity.Go);
            yield return entity.Settle();

            entity.Health.SetPhaseHold(true);
            entity.Health.ReleasePhaseHold();

            Assert.IsFalse(entity.Health.holdInPlace, "正式开局应解除相位冻结。");
            Assert.IsTrue(entity.Health.awaitingInitialDeploy,
                "解除后仍需玩家选点才进入地图(规格:未选择则暂不部署)。");
        }
    }
}
