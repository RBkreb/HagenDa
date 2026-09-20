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
    /// 区域逻辑：争夺点（contention/归属/计分）、安全区（敌方滞留击杀）、移动区（航点巡航）。
    ///
    /// 时间相关的注意：<c>CapturePoint.Update</c> 用的是 <c>Time.deltaTime</c>，
    /// 因此争夺值随帧数累积。测试**只断言方向与阈值行为**，不断言精确数值，
    /// 避免因帧率波动而 flaky。需要"占满"时直接预置 contention（public SyncVar 字段）。
    /// </summary>
    [TestFixture]
    public class ZoneTests
    {
        private readonly List<PlayerFixture> _players = new List<PlayerFixture>();
        private readonly List<GameObject> _extras = new List<GameObject>();
        private NetworkMatchManager _manager;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            PlayModeServer.StartServer();
            _manager = PlayModeServer.SpawnComponent<NetworkMatchManager>("TestMatchManager");
            _manager.winScore = 1000000;   // 避免测试中途意外结束对局
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

        /// <summary>在指定位置生成一个已分配队伍/小队的作战单位（带碰撞体用于范围查询）。</summary>
        private PlayerFixture SpawnCombatant(Vector3 pos, int teamId, bool alive = true)
        {
            var p = PlayerFixture.Create(pos, withAI: false, withFloor: false);
            _players.Add(p);

            // 直接指定阵营/小队，避免依赖 manager 的分配策略（本文件测的是区域逻辑）。
            p.Combatant.teamId = teamId;
            p.Combatant.squadId = 0;
            if (!alive)
                p.Health.health = 0f;    // IsDead => health <= 0
            return p;
        }

        private CapturePoint SpawnCapturePoint(Vector3 pos, float radius = 10f)
        {
            var go = new GameObject("TestCapturePoint");
            go.SetActive(false);
            go.AddComponent<Mirror.NetworkIdentity>();
            var cp = go.AddComponent<CapturePoint>();
            cp.radius = radius;
            cp.captureRate = 1f;
            go.transform.position = pos;
            _extras.Add(go);
            PlayModeServer.Spawn(go);
            return cp;
        }

        private GarrisonZone SpawnGarrison(Vector3 pos, int teamId, float radius = 20f, float killDelay = 10f)
        {
            var go = new GameObject("TestGarrison");
            go.SetActive(false);
            go.AddComponent<Mirror.NetworkIdentity>();
            var g = go.AddComponent<GarrisonZone>();
            g.teamId = teamId;
            g.radius = radius;
            g.killDelay = killDelay;
            go.transform.position = pos;
            _extras.Add(go);
            PlayModeServer.Spawn(go);
            return g;
        }

        // ---------------------------------------------------------------
        // CapturePoint：人数统计
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator CapturePoint_GetTeamCounts_CountsOnlyLivingAssignedUnits()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 10f);

            SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Red);
            SpawnCombatant(new Vector3(1f, 0f, 1f), (int)MatchTeam.Red);
            SpawnCombatant(new Vector3(-1f, 0f, -1f), (int)MatchTeam.Blue);
            SpawnCombatant(new Vector3(2f, 0f, 2f), (int)MatchTeam.Blue, alive: false); // 死者不计
            yield return new WaitForFixedUpdate();

            cp.GetTeamCounts(out int red, out int blue);

            Assert.AreEqual(2, red, "两名存活红方单位应被计入。");
            Assert.AreEqual(1, blue, "蓝方应只计存活的 1 名（死者排除）。");
        }

        [UnityTest]
        public IEnumerator CapturePoint_GetTeamCounts_ExcludesUnassignedCombatants()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 10f);

            var unassigned = SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Red);
            unassigned.Combatant.teamId = -1;   // 未分配
            yield return new WaitForFixedUpdate();

            cp.GetTeamCounts(out int red, out int blue);

            Assert.AreEqual(0, red, "teamId<0（未分配）的单位不应计入任何一方。");
            Assert.AreEqual(0, blue);
        }

        [UnityTest]
        public IEnumerator CapturePoint_GetTeamCounts_IgnoresUnitsOutsideRadius()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 5f);

            SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Red);       // 圈内
            SpawnCombatant(new Vector3(50f, 0f, 0f), (int)MatchTeam.Red);      // 圈外
            yield return new WaitForFixedUpdate();

            cp.GetTeamCounts(out int red, out int blue);

            Assert.AreEqual(1, red, "半径外的单位不应计入。");
        }

        // ---------------------------------------------------------------
        // CapturePoint：争夺值累积与归属
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator CapturePoint_SingleTeamPresent_ContentionMovesTowardThatTeam()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 10f);
            SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return new WaitForFixedUpdate();

            // 只有蓝方在内 → diff = blue-red = +1 → contention 应向 +（蓝）增长。
            float before = cp.contention;
            for (int i = 0; i < 5; i++)
                yield return new WaitForFixedUpdate();

            Assert.Greater(cp.contention, before,
                "仅蓝方在点时争夺值应朝蓝方（正）累积。");
        }

        [UnityTest]
        public IEnumerator CapturePoint_BalancedPresence_DoesNotAccumulate()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 10f);
            SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            SpawnCombatant(new Vector3(1f, 0f, 0f), (int)MatchTeam.Red);
            yield return new WaitForFixedUpdate();

            float before = cp.contention;
            for (int i = 0; i < 5; i++)
                yield return new WaitForFixedUpdate();

            Assert.AreEqual(before, cp.contention, 1e-4f,
                "双方人数相等（diff=0）时争夺值不应变化。");
        }

        /// <summary>
        /// 推进帧直到条件成立或超时。用于争夺值这类**按 deltaTime 累积**的量——
        /// 精确帧数取决于帧时长，轮询比硬编码帧数更稳。
        /// </summary>
        private IEnumerator WaitUntil(System.Func<bool> condition, float timeoutSeconds = 3f)
        {
            float t0 = Time.time;
            while (!condition() && Time.time - t0 < timeoutSeconds)
                yield return null;
        }

        [UnityTest]
        public IEnumerator CapturePoint_FullContention_CapturesAndScores()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 10f);
            cp.captureScore = 10;
            cp.captureRate = 1000f;      // 加速：每帧直接冲向 ±60
            int blueBefore = _manager.blueScore;

            SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return null;

            yield return WaitUntil(() => cp.ownerTeam == (int)MatchTeam.Blue);

            Assert.AreEqual((int)MatchTeam.Blue, cp.ownerTeam,
                "争夺值到达 +60 应把归属判给蓝方。");
            Assert.AreEqual(blueBefore + 10, _manager.blueScore,
                "占领瞬间应给蓝方加 captureScore。");
        }

        [UnityTest]
        public IEnumerator CapturePoint_SignFlip_LosesOwnership()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 10f);
            cp.captureRate = 1000f;

            // 蓝方先占满。
            var blue = SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return null;
            yield return WaitUntil(() => cp.ownerTeam == (int)MatchTeam.Blue);
            Assert.AreEqual((int)MatchTeam.Blue, cp.ownerTeam, "前置条件：蓝方应已占领。");

            // 蓝方撤离、红方进入 → contention 转负 → 归属翻转为中立。
            blue.Go.transform.position = new Vector3(200f, 0f, 0f);
            SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Red);
            yield return null;

            yield return WaitUntil(() => cp.ownerTeam != (int)MatchTeam.Blue);

            Assert.AreNotEqual((int)MatchTeam.Blue, cp.ownerTeam,
                "争夺值过 0 后应失去归属（转为中立或易主），不得仍归蓝方。");
        }

        [UnityTest]
        public IEnumerator CapturePoint_Contention_IsClampedToSixty()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 10f);
            cp.captureRate = 100000f;   // 极端速率，试图越界

            SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            for (int i = 0; i < 3; i++)
                yield return new WaitForFixedUpdate();

            Assert.LessOrEqual(cp.contention, 60f, "争夺值上限应被钳制在 +60。");
            Assert.GreaterOrEqual(cp.contention, -60f, "争夺值下限应被钳制在 -60。");
        }

        [UnityTest]
        public IEnumerator CapturePoint_Holding_AccumulatesHoldScore()
        {
            var cp = SpawnCapturePoint(Vector3.zero, radius: 10f);
            cp.holdScore = 3;
            cp.holdInterval = 0.05f;    // 缩短到几帧内触发
            cp.captureRate = 1000f;

            SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return null;

            yield return WaitUntil(() => cp.ownerTeam == (int)MatchTeam.Blue);
            Assert.AreEqual((int)MatchTeam.Blue, cp.ownerTeam, "前置条件：蓝方应已占领。");
            int afterCapture = _manager.blueScore;

            // 持续驻守 → 每 holdInterval 应再得 holdScore。
            yield return WaitUntil(() => _manager.blueScore > afterCapture, 2f);

            Assert.Greater(_manager.blueScore, afterCapture,
                "占领后持续驻守应按 holdInterval 累积 holdScore。");
        }

        // ---------------------------------------------------------------
        // GarrisonZone：敌方滞留击杀
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator GarrisonZone_EnemyInside_KilledAfterDelay()
        {
            var g = SpawnGarrison(Vector3.zero, (int)MatchTeam.Red, radius: 15f, killDelay: 0.15f);
            var enemy = SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);

            // 等待超过 killDelay。
            float t0 = Time.time;
            while (Time.time - t0 < 1.2f && !enemy.Health.IsDead)
                yield return new WaitForFixedUpdate();

            Assert.IsTrue(enemy.Health.IsDead,
                "敌方单位在安全区内滞留超过 killDelay 应被强制击杀。");
        }

        [UnityTest]
        public IEnumerator GarrisonZone_FriendlyInside_NotKilled()
        {
            SpawnGarrison(Vector3.zero, (int)MatchTeam.Red, radius: 15f, killDelay: 0.1f);
            var friend = SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Red);

            float t0 = Time.time;
            while (Time.time - t0 < 0.8f)
                yield return new WaitForFixedUpdate();

            Assert.IsFalse(friend.Health.IsDead,
                "本方单位在自家安全区内不应被击杀。");
        }

        [UnityTest]
        public IEnumerator GarrisonZone_EnemyOutside_NotKilled()
        {
            SpawnGarrison(Vector3.zero, (int)MatchTeam.Red, radius: 5f, killDelay: 0.1f);
            var enemy = SpawnCombatant(new Vector3(100f, 0f, 0f), (int)MatchTeam.Blue);

            float t0 = Time.time;
            while (Time.time - t0 < 0.8f)
                yield return new WaitForFixedUpdate();

            Assert.IsFalse(enemy.Health.IsDead,
                "区外的敌方单位不应被安全区击杀。");
        }

        [UnityTest]
        public IEnumerator GarrisonZone_EnemyLeavingBeforeDelay_Survives()
        {
            SpawnGarrison(Vector3.zero, (int)MatchTeam.Red, radius: 15f, killDelay: 0.6f);
            var enemy = SpawnCombatant(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);

            // 在计时未到前撤离（契约：离开即清空计时）。
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            enemy.Go.transform.position = new Vector3(200f, 0f, 0f);

            float t0 = Time.time;
            while (Time.time - t0 < 1.2f)
                yield return new WaitForFixedUpdate();

            Assert.IsFalse(enemy.Health.IsDead,
                "计时未满即离开安全区的敌方单位不应被击杀（离开会重置计时）。");
        }

        [UnityTest]
        public IEnumerator GarrisonZone_GetRandomDeployPoint_FallsBackToCenter()
        {
            var g = SpawnGarrison(new Vector3(7f, 0f, 9f), (int)MatchTeam.Red);
            yield return null;

            var p = g.GetRandomDeployPoint();

            // 无 deployPoints 时回退到 transform.position 附近（±1m 随机）。
            Assert.LessOrEqual(Vector3.Distance(p, g.transform.position), 1.5f,
                "无部署点列表时应回退到安全区中心附近。");
        }

        // ---------------------------------------------------------------
        // MovingZone：航点巡航（纯 MonoBehaviour，无 NetworkIdentity）
        // ---------------------------------------------------------------

        private MovingZone SpawnMovingZone(Vector3 start, List<Vector3> waypoints,
                                           bool pingPong = true, float speed = 5f)
        {
            var go = new GameObject("TestMovingZone");
            go.transform.position = start;
            var mz = go.AddComponent<MovingZone>();
            mz.waypoints = waypoints;
            mz.moveSpeed = speed;
            mz.pingPong = pingPong;
            mz.startDelay = 0f;
            mz.dwellSeconds = 0f;
            _extras.Add(go);
            return mz;
        }

        [UnityTest]
        public IEnumerator MovingZone_MovesTowardWaypoint_WhenServerActive()
        {
            var mz = SpawnMovingZone(Vector3.zero, new List<Vector3>
            {
                new Vector3(10f, 0f, 0f),
                new Vector3(20f, 0f, 0f),
            });

            Vector3 before = mz.transform.position;
            for (int i = 0; i < 10; i++)
                yield return null;

            Assert.Greater(mz.transform.position.x, before.x,
                "服务端激活时移动区应朝目标航点前进。");
            Assert.IsTrue(mz.IsRunning, "越过 startDelay 后 IsRunning 应为 true。");
        }

        [UnityTest]
        public IEnumerator MovingZone_ReachesWaypoint_ThenPingPongsBack()
        {
            var mz = SpawnMovingZone(Vector3.zero, new List<Vector3>
            {
                new Vector3(1f, 0f, 0f),
                new Vector3(2f, 0f, 0f),
            }, pingPong: true, speed: 50f);

            // 跑到足够远处后，ping-pong 应把 x 拉回较小值。
            float peak = 0f;
            for (int i = 0; i < 60; i++)
            {
                yield return null;
                peak = Mathf.Max(peak, mz.transform.position.x);
            }

            Assert.Less(mz.transform.position.x, peak,
                "ping-pong 模式下移动区到达端点后应折返。");
        }

        [UnityTest]
        public IEnumerator MovingZone_NoMovement_WithFewerThanTwoWaypoints()
        {
            var mz = SpawnMovingZone(Vector3.zero, new List<Vector3>
            {
                new Vector3(10f, 0f, 0f),
            });

            Vector3 before = mz.transform.position;
            for (int i = 0; i < 10; i++)
                yield return null;

            Assert.AreEqual(before, mz.transform.position,
                "航点少于 2 个时不应移动（无法构成路径）。");
            Assert.IsFalse(mz.IsRunning);
        }

        [UnityTest]
        public IEnumerator MovingZone_ZeroSpeed_DoesNotMove()
        {
            var mz = SpawnMovingZone(Vector3.zero, new List<Vector3>
            {
                new Vector3(10f, 0f, 0f),
                new Vector3(20f, 0f, 0f),
            }, speed: 0f);

            Vector3 before = mz.transform.position;
            for (int i = 0; i < 10; i++)
                yield return null;

            Assert.AreEqual(before, mz.transform.position,
                "moveSpeed<=0 时不应移动（用于脚本化关停）。");
        }
    }
}
