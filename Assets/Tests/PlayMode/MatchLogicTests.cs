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
    /// 对局管理（PHASE7）：队伍/小队分配、计分与胜负、部署点选择。
    ///
    /// 测试形态说明：<c>AssignCombatant</c> 是 private，但它由
    /// <c>NetworkCombatant.OnStartServer → RegisterCombatant</c> 自动触发，因此这里走
    /// **真实注册路径**（生成 manager + 生成实体），而不是反射调用私有方法 —— 这样顺带
    /// 覆盖了「实体先于/后于 manager 生成」的注册时序。
    /// </summary>
    [TestFixture]
    public class MatchLogicTests
    {
        private readonly List<PlayerFixture> _players = new List<PlayerFixture>();
        private readonly List<GameObject> _extras = new List<GameObject>();
        private NetworkMatchManager _manager;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            PlayModeServer.StartServer();

            // manager 先行：这样后续生成的实体都会走 Instance 直连路径。
            _manager = PlayModeServer.SpawnComponent<NetworkMatchManager>("TestMatchManager");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var p in _players) p.Dispose();
            _players.Clear();

            foreach (var go in _extras)
                if (go != null) PlayModeServer.Destroy(go);
            _extras.Clear();

            if (_manager != null)
            {
                PlayModeServer.Destroy(_manager.gameObject);
                _manager = null;
            }

            PlayModeServer.StopServer();
            yield return null;
        }

        /// <summary>生成一个无主（AI 语义）作战单位。</summary>
        private PlayerFixture SpawnEntity(Vector3 pos)
        {
            var p = PlayerFixture.Create(pos, withAI: false, withFloor: false);
            _players.Add(p);
            return p;
        }

        /// <summary>
        /// 生成一个安全区**并登记到 manager 的 garrisons 列表**。
        /// 场景里这个列表由场景构建器预先填好；代码搭建的测试必须自己登记，
        /// 否则 <c>GetGarrisonDeployPoint</c> 遍历空列表只会返回 null。
        /// </summary>
        private GarrisonZone SpawnGarrison(Vector3 pos, int teamId)
        {
            var go = new GameObject("TestGarrison");
            go.SetActive(false);
            go.AddComponent<Mirror.NetworkIdentity>();
            var g = go.AddComponent<GarrisonZone>();
            g.teamId = teamId;
            g.radius = 20f;
            go.transform.position = pos;
            _extras.Add(go);
            PlayModeServer.Spawn(go);

            _manager.garrisons.Add(g);
            return g;
        }

        // ---------------------------------------------------------------
        // 队伍分配：AI 回退规则（按 z 符号）
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator AssignCombatant_NoGarrisons_AiTeamFallsBackToZSign()
        {
            // 契约：南（z<0）= 红，北（z>=0）= 蓝。
            var south = SpawnEntity(new Vector3(0f, 0f, -50f));
            var north = SpawnEntity(new Vector3(0f, 0f, 50f));
            yield return null;

            Assert.AreEqual((int)MatchTeam.Red, south.Combatant.teamId,
                "无安全区时 z<0 的 AI 应归红方。");
            Assert.AreEqual((int)MatchTeam.Blue, north.Combatant.teamId,
                "无安全区时 z>=0 的 AI 应归蓝方。");
        }

        [UnityTest]
        public IEnumerator AssignCombatant_PrefersNearestGarrisonTeam()
        {
            // 故意让地理符号与安全区归属相反，以证明"最近安全区"优先于 z 符号：
            // z=-40 但最近的 GR 是蓝方 → 应归蓝，而不是按 z 判红。
            SpawnGarrison(new Vector3(0f, 0f, -20f), (int)MatchTeam.Blue);
            var entity = SpawnEntity(new Vector3(0f, 0f, -40f));
            yield return null;

            Assert.AreEqual((int)MatchTeam.Blue, entity.Combatant.teamId,
                "AI 应归最近安全区的队伍，即使其 z 符号指向另一方。");
        }

        [UnityTest]
        public IEnumerator AssignCombatant_PicksNearestOfMultipleGarrisons()
        {
            SpawnGarrison(new Vector3(0f, 0f, -100f), (int)MatchTeam.Red);
            SpawnGarrison(new Vector3(0f, 0f, -10f), (int)MatchTeam.Blue);

            var entity = SpawnEntity(new Vector3(0f, 0f, -5f));   // 离蓝方 GR 更近
            yield return null;

            Assert.AreEqual((int)MatchTeam.Blue, entity.Combatant.teamId,
                "多个安全区时应取最近的那个。");
        }

        [UnityTest]
        public IEnumerator AssignCombatant_DoesNotReassign_AlreadyAssigned()
        {
            var entity = SpawnEntity(new Vector3(0f, 0f, 30f));
            yield return null;

            // 人为锁定队伍，再让 manager 重新注册一次。
            entity.Combatant.teamId = (int)MatchTeam.Red;
            entity.Combatant.squadId = 7;

            NetworkMatchManager.RegisterCombatant(entity.Combatant);
            yield return null;

            Assert.AreEqual((int)MatchTeam.Red, entity.Combatant.teamId,
                "已分配队伍的单位不得被重新分配（teamId>=0 早退）。");
            Assert.AreEqual(7, entity.Combatant.squadId, "小队号同样应被保留。");
        }

        // ---------------------------------------------------------------
        // 小队分配：轮询
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator AssignCombatant_SquadIds_RoundRobinWithinTeam()
        {
            _manager.squadsPerTeam = 2;
            yield return null;

            // 同队（z>=0 → 蓝）生成 3 个：应得到 0,1,0 的轮询。
            var a = SpawnEntity(new Vector3(0f, 0f, 10f));
            var b = SpawnEntity(new Vector3(1f, 0f, 10f));
            var c = SpawnEntity(new Vector3(2f, 0f, 10f));
            yield return null;

            Assert.AreEqual(0, a.Combatant.squadId);
            Assert.AreEqual(1, b.Combatant.squadId);
            Assert.AreEqual(0, c.Combatant.squadId,
                "小队号应在 squadsPerTeam 内轮询回绕。");
        }

        [UnityTest]
        public IEnumerator AssignCombatant_SquadCounters_ArePerTeam()
        {
            _manager.squadsPerTeam = 4;
            yield return null;

            var red = SpawnEntity(new Vector3(0f, 0f, -10f));
            var blue = SpawnEntity(new Vector3(0f, 0f, 10f));
            yield return null;

            Assert.AreEqual(0, red.Combatant.squadId);
            Assert.AreEqual(0, blue.Combatant.squadId,
                "两队的轮询计数相互独立，各自从 0 开始。");
        }

        [UnityTest]
        public IEnumerator AssignCombatant_ZeroSquadsPerTeam_DoesNotDivideByZero()
        {
            _manager.squadsPerTeam = 0;
            yield return null;

            var entity = SpawnEntity(new Vector3(0f, 0f, 10f));
            yield return null;

            Assert.AreEqual(0, entity.Combatant.squadId,
                "squadsPerTeam=0 时被 Mathf.Max(1,...) 保护，小队号应为 0 且不抛异常。");
        }

        // ---------------------------------------------------------------
        // 注册表
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator GetAllCombatants_ReturnsRegisteredUnits()
        {
            SpawnEntity(new Vector3(0f, 0f, 10f));
            SpawnEntity(new Vector3(1f, 0f, 10f));
            yield return null;

            var list = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(list);

            Assert.AreEqual(2, list.Count, "注册表应包含两个刚生成的作战单位。");
        }

        [UnityTest]
        public IEnumerator GetAllCombatants_ClearsOutputListFirst()
        {
            SpawnEntity(new Vector3(0f, 0f, 10f));
            yield return null;

            var list = new List<NetworkCombatant> { null, null, null };
            NetworkMatchManager.GetAllCombatants(list);

            Assert.AreEqual(1, list.Count,
                "GetAllCombatants 必须先清空输出列表，否则调用方会读到陈旧数据。");
        }

        // ---------------------------------------------------------------
        // 计分与胜负
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator AddScore_AccumulatesPerTeam()
        {
            _manager.AddScore(MatchTeam.Red, 5);
            _manager.AddScore(MatchTeam.Red, 3);
            _manager.AddScore(MatchTeam.Blue, 2);
            yield return null;

            Assert.AreEqual(8, _manager.redScore);
            Assert.AreEqual(2, _manager.blueScore);
            Assert.IsFalse(_manager.matchOver, "未达胜利分数不应结束对局。");
        }

        [UnityTest]
        public IEnumerator AddScore_ReachingWinScore_EndsMatch()
        {
            _manager.winScore = 10;
            yield return null;

            _manager.AddScore(MatchTeam.Red, 10);

            Assert.IsTrue(_manager.matchOver, "达到 winScore 应结束对局。");
            Assert.AreEqual((int)MatchTeam.Red, _manager.winner, "红方应为胜者。");
        }

        [UnityTest]
        public IEnumerator AddScore_AfterMatchOver_IsIgnored()
        {
            _manager.winScore = 5;
            yield return null;

            _manager.AddScore(MatchTeam.Red, 5);
            int frozen = _manager.redScore;

            _manager.AddScore(MatchTeam.Red, 100);
            _manager.AddScore(MatchTeam.Blue, 100);

            Assert.AreEqual(frozen, _manager.redScore, "结束后红方分数应冻结。");
            Assert.AreEqual(0, _manager.blueScore, "结束后蓝方分数应冻结。");
        }

        [UnityTest]
        public IEnumerator ReportKill_ScoresOnlyForEnemyKill()
        {
            _manager.winScore = 1000;
            var attacker = SpawnEntity(new Vector3(0f, 0f, 10f));
            var friend = SpawnEntity(new Vector3(1f, 0f, 10f));
            var enemy = SpawnEntity(new Vector3(0f, 0f, -10f));
            yield return null;

            // 契约：队友击杀不计分。
            Assert.AreEqual(attacker.Combatant.teamId, friend.Combatant.teamId,
                "前置条件：两单位应同队。");
            _manager.ReportKill(attacker.Combatant, friend.Combatant);
            Assert.AreEqual(0, _manager.redScore + _manager.blueScore,
                "友军击杀不应计分。");

            // 击杀敌方应 +1。
            _manager.ReportKill(attacker.Combatant, enemy.Combatant);
            int attackerTeamScore = attacker.Combatant.teamId == (int)MatchTeam.Red
                ? _manager.redScore : _manager.blueScore;
            Assert.AreEqual(1, attackerTeamScore, "击杀敌方应为攻击方队伍 +1。");
        }

        [UnityTest]
        public IEnumerator ReportKill_IgnoresUnassignedOrNull()
        {
            _manager.winScore = 1000;
            var a = SpawnEntity(new Vector3(0f, 0f, 10f));
            var b = SpawnEntity(new Vector3(0f, 0f, -10f));
            yield return null;

            _manager.ReportKill(null, b.Combatant);
            _manager.ReportKill(a.Combatant, null);
            Assert.AreEqual(0, _manager.redScore + _manager.blueScore,
                "空引用不应计分，也不应抛异常。");
        }

        // ---------------------------------------------------------------
        // 部署点
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator GetGarrisonDeployPoint_ReturnsNullWhenNoMatchingGarrison()
        {
            SpawnGarrison(new Vector3(0f, 0f, 0f), (int)MatchTeam.Red);
            yield return null;

            Assert.IsNull(_manager.GetGarrisonDeployPoint(MatchTeam.Blue),
                "没有蓝方安全区时应返回 null。");
            Assert.IsNotNull(_manager.GetGarrisonDeployPoint(MatchTeam.Red),
                "存在红方安全区时应返回一个部署点。");
        }

        [UnityTest]
        public IEnumerator GetHqDeployPoint_RequiresOwnedCapturePoint()
        {
            // 没有据点 → null。
            Assert.IsNull(_manager.GetHqDeployPoint(MatchTeam.Red),
                "无已占领据点时应返回 null。");
            yield return null;
        }

        [UnityTest]
        public IEnumerator GetSquadDeployPoint_NullWhenNoLivingTeammate()
        {
            var solo = SpawnEntity(new Vector3(0f, 0f, 10f));
            yield return null;

            // 队里只有自己 → 排除 self 后无候选 → null。
            var point = _manager.GetSquadDeployPoint(
                solo.Combatant.teamId, solo.Combatant.squadId, solo.Combatant);

            Assert.IsNull(point, "队内无其他存活成员时应返回 null。");
        }

        [UnityTest]
        public IEnumerator GetSquadDeployPoint_IsNearLivingTeammate()
        {
            _manager.squadsPerTeam = 1;   // 强制同队同小队
            yield return null;

            var mate = SpawnEntity(new Vector3(0f, 0f, 10f));
            var self = SpawnEntity(new Vector3(0f, 0f, 12f));
            yield return null;

            Assert.AreEqual(mate.Combatant.teamId, self.Combatant.teamId,
                "前置条件：两者同队。");
            Assert.AreEqual(mate.Combatant.squadId, self.Combatant.squadId,
                "前置条件：两者同小队。");

            var point = _manager.GetSquadDeployPoint(
                self.Combatant.teamId, self.Combatant.squadId, self.Combatant);

            Assert.IsTrue(point.HasValue, "有存活队友时应返回部署点。");
            float dist = Vector3.Distance(point.Value, mate.Go.transform.position);
            Assert.LessOrEqual(dist, 2.5f,
                "小队部署点应位于队友 2m 范围内（允许 NavMesh 吸附误差）。");
        }
    }
}
