using System.Collections.Generic;
using HagenDa.Networking;
using NUnit.Framework;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Match
{
    /// <summary>
    /// 战略要地注册表（ML 训练共识 §3）。<see cref="StrategicZoneRegistry.NearestZone"/>
    /// 是纯粹的"选最近目标"逻辑，但被 AI 目标选择直接调用，因此它的**回退语义**与
    /// MaxZones 上限必须被钉住。
    ///
    /// EditMode 说明：这里显式调用 public 的 Register/Unregister，不依赖 OnEnable/Awake
    /// 的编辑器生命周期差异（游戏内注册仍走 OnEnable）。
    /// </summary>
    [TestFixture]
    public class StrategicZoneRegistryTests
    {
        private GameObject _registryGo;
        private StrategicZoneRegistry _registry;
        private readonly List<GameObject> _zoneGos = new List<GameObject>();

        private const int RedTeam = (int)MatchTeam.Red;   // 0
        private const int BlueTeam = (int)MatchTeam.Blue; // 1

        [SetUp]
        public void SetUp()
        {
            _registryGo = new GameObject("ZoneRegistryTest");
            _registry = _registryGo.AddComponent<StrategicZoneRegistry>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _zoneGos)
                Object.DestroyImmediate(go);
            _zoneGos.Clear();

            Object.DestroyImmediate(_registryGo);
        }

        private StrategicZone MakeZone(Vector3 position, int ownerTeam, float radius = 10f)
        {
            var go = new GameObject($"Zone_{ownerTeam}_{position.x}");
            go.transform.position = position;
            _zoneGos.Add(go);

            var zone = go.AddComponent<StrategicZone>();
            zone.capturePoint = null;      // 纯要地，走 transform.position 分支
            zone.ownerTeam = ownerTeam;
            zone.radius = radius;
            return zone;
        }

        private StrategicZone RegisterZone(Vector3 position, int ownerTeam, float radius = 10f)
        {
            var zone = MakeZone(position, ownerTeam, radius);
            _registry.Register(zone);
            return zone;
        }

        // ---------------------------------------------------------------
        // 空状态
        // ---------------------------------------------------------------

        [Test]
        public void EmptyRegistry_HasNoZones()
        {
            Assert.AreEqual(0, _registry.Count);
        }

        [Test]
        public void EmptyRegistry_NearestZone_ReturnsNull()
        {
            Assert.IsNull(_registry.NearestZone(Vector3.zero, RedTeam));
        }

        [Test]
        public void EmptyRegistry_GetZones_ReturnsEmptyList()
        {
            var output = new List<StrategicZoneState> { new StrategicZoneState() };
            _registry.GetZones(output);
            Assert.AreEqual(0, output.Count, "GetZones 应先清空输出列表。");
        }

        // ---------------------------------------------------------------
        // 注册表基本行为
        // ---------------------------------------------------------------

        [Test]
        public void Register_IncrementsCount()
        {
            RegisterZone(new Vector3(10f, 0f, 0f), RedTeam);
            RegisterZone(new Vector3(20f, 0f, 0f), BlueTeam);
            Assert.AreEqual(2, _registry.Count);
        }

        [Test]
        public void Register_IsIdempotent_ForSameZone()
        {
            var zone = RegisterZone(new Vector3(10f, 0f, 0f), RedTeam);
            _registry.Register(zone);
            _registry.Register(zone);
            Assert.AreEqual(1, _registry.Count, "重复注册同一要地不应产生重复条目。");
        }

        [Test]
        public void Register_IgnoresNull()
        {
            _registry.Register(null);
            Assert.AreEqual(0, _registry.Count);
        }

        [Test]
        public void Unregister_RemovesZone()
        {
            var zone = RegisterZone(new Vector3(10f, 0f, 0f), RedTeam);
            _registry.Unregister(zone);
            Assert.AreEqual(0, _registry.Count);
        }

        // ---------------------------------------------------------------
        // GetZones 快照 + MaxZones 上限
        // ---------------------------------------------------------------

        [Test]
        public void GetZones_ReturnsRegisteredStatesInOrder()
        {
            RegisterZone(new Vector3(1f, 0f, 0f), RedTeam, radius: 5f);
            RegisterZone(new Vector3(2f, 0f, 0f), BlueTeam, radius: 7f);

            var output = new List<StrategicZoneState>();
            _registry.GetZones(output);

            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), output[0].position);
            Assert.AreEqual(5f, output[0].radius);
            Assert.AreEqual(RedTeam, output[0].ownerTeam);
            Assert.AreEqual(new Vector3(2f, 0f, 0f), output[1].position);
            Assert.AreEqual(BlueTeam, output[1].ownerTeam);
        }

        [Test]
        public void GetZones_RespectsMaxZonesCap()
        {
            for (int i = 0; i < StrategicZoneRegistry.MaxZones + 2; i++)
                RegisterZone(new Vector3(i + 1f, 0f, 0f), RedTeam);

            Assert.AreEqual(StrategicZoneRegistry.MaxZones + 2, _registry.Count);

            var output = new List<StrategicZoneState>();
            _registry.GetZones(output);
            Assert.AreEqual(StrategicZoneRegistry.MaxZones, output.Count,
                "GetZones 必须封顶到 MaxZones（观测维度预算）。");
        }

        // ---------------------------------------------------------------
        // NearestZone 选择与回退语义
        // ---------------------------------------------------------------

        [Test]
        public void NearestZone_PrefersEnemyOverFriendly_EvenIfFarther()
        {
            // 近处是己方、远处是敌方 → 应选敌方（NearestZone 语义是"最近的敌方/未占领"）。
            RegisterZone(new Vector3(5f, 0f, 0f), RedTeam);
            RegisterZone(new Vector3(50f, 0f, 0f), BlueTeam);

            var result = _registry.NearestZone(Vector3.zero, RedTeam);

            Assert.IsTrue(result.HasValue, "应返回一个要地。");
            Assert.AreEqual(BlueTeam, result.Value.ownerTeam, "即使更远，也应优先选非己方要地。");
            Assert.AreEqual(new Vector3(50f, 0f, 0f), result.Value.position);
        }

        [Test]
        public void NearestZone_PicksNearestAmongMultipleEnemyZones()
        {
            RegisterZone(new Vector3(80f, 0f, 0f), BlueTeam);
            RegisterZone(new Vector3(20f, 0f, 0f), BlueTeam);
            RegisterZone(new Vector3(50f, 0f, 0f), BlueTeam);

            var result = _registry.NearestZone(Vector3.zero, RedTeam);

            Assert.IsTrue(result.HasValue);
            Assert.AreEqual(new Vector3(20f, 0f, 0f), result.Value.position,
                "多个敌方要地中应选最近的一个。");
        }

        [Test]
        public void NearestZone_AllFriendly_FallsBackToNearestOverall()
        {
            RegisterZone(new Vector3(90f, 0f, 0f), RedTeam);
            RegisterZone(new Vector3(30f, 0f, 0f), RedTeam);

            var result = _registry.NearestZone(Vector3.zero, RedTeam);

            Assert.IsTrue(result.HasValue, "没有敌方要地时应回退而非返回 null。");
            Assert.AreEqual(new Vector3(30f, 0f, 0f), result.Value.position,
                "回退应给最近的要地（即使属于己方）。");
        }

        [Test]
        public void NearestZone_NeutralZones_CountAsTargets()
        {
            // ownerTeam = -1（中立）既不等于己方，应被视为可选目标。
            RegisterZone(new Vector3(40f, 0f, 0f), -1);
            RegisterZone(new Vector3(10f, 0f, 0f), RedTeam);

            var result = _registry.NearestZone(Vector3.zero, RedTeam);

            Assert.IsTrue(result.HasValue);
            Assert.AreEqual(-1, result.Value.ownerTeam, "中立要地应被选为目标。");
        }

        [Test]
        public void NearestZone_RespectsMaxZonesCap()
        {
            // 前 3 个（被考虑的）全是远处己方；近处敌方排在第 4 位，超出上限。
            RegisterZone(new Vector3(100f, 0f, 0f), RedTeam);
            RegisterZone(new Vector3(200f, 0f, 0f), RedTeam);
            RegisterZone(new Vector3(300f, 0f, 0f), RedTeam);
            RegisterZone(new Vector3(1f, 0f, 0f), BlueTeam);   // 超出 MaxZones
            RegisterZone(new Vector3(2f, 0f, 0f), BlueTeam);   // 超出 MaxZones

            var result = _registry.NearestZone(Vector3.zero, RedTeam);

            Assert.IsTrue(result.HasValue);
            Assert.AreEqual(new Vector3(100f, 0f, 0f), result.Value.position,
                "NearestZone 只考虑前 MaxZones 个要地（与 GetZones 一致）。");
        }

        [Test]
        public void NearestZone_ReturnsNull_WhenOwnerTeamMatchesAll()
        {
            // 用 -1 作为 myTeam：中立要地全等于 myTeam，且列表非空、
            // 因此应回退（而非 null）。这里验证的是"永不漏回退"。
            RegisterZone(new Vector3(10f, 0f, 0f), -1);
            var result = _registry.NearestZone(Vector3.zero, -1);
            Assert.IsTrue(result.HasValue, "全部 equal myTeam 时仍应回退到最近要地。");
        }
    }
}
