using HagenDa.Networking;
using NUnit.Framework;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Combat
{
    /// <summary>
    /// 武器数据模板（PHASE5）。唯一的派生逻辑是射速 → 射击间隔换算，
    /// 而它是全自动武器节奏、弹药消耗与 AI 开火节流的共同基准。
    /// </summary>
    [TestFixture]
    public class WeaponDefinitionTests
    {
        private WeaponDefinition _def;

        [SetUp]
        public void SetUp() => _def = ScriptableObject.CreateInstance<WeaponDefinition>();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_def);

        [Test]
        public void FireInterval_Is60OverRpm()
        {
            _def.fireRateRPM = 600f;
            Assert.AreEqual(0.1f, _def.FireInterval, 1e-6f);
        }

        [Test]
        public void FireInterval_Default900Rpm_Is15Hz()
        {
            // 默认值 900 RPM → 每秒 15 发。
            Assert.AreEqual(900f, _def.fireRateRPM);
            Assert.AreEqual(1f / 15f, _def.FireInterval, 1e-6f);
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void FireInterval_NonPositiveRpm_IsZero_NotInfinity(float rpm)
        {
            // 必须防除零：返回 0 而不是 Infinity，否则上层冷却判断会失效。
            _def.fireRateRPM = rpm;
            Assert.AreEqual(0f, _def.FireInterval);
            Assert.IsFalse(float.IsInfinity(_def.FireInterval));
        }

        [Test]
        public void FireInterval_HigherRpm_MeansShorterInterval()
        {
            _def.fireRateRPM = 300f;
            float slow = _def.FireInterval;
            _def.fireRateRPM = 1200f;
            float fast = _def.FireInterval;

            Assert.Less(fast, slow);
        }

        [Test]
        public void Defaults_MatchDesignerDocumentedInvariants()
        {
            // 文档化默认值（Tooltip 中已声明），用于捕获意外改默认值。
            Assert.AreEqual(30, _def.magazineCapacity, "弹匣容量");
            Assert.AreEqual(450, _def.reserveCapacity, "备弹");
            Assert.Greater(_def.baseDamage, _def.minDamage,
                "baseDamage 必须大于 minDamage（Tooltip 明确要求）。");
            Assert.AreEqual(3, _def.burstCount, "点射发数");
        }

        [Test]
        public void DefaultFireModes_ContainAutoBurstSemi_AndAreDistinct()
        {
            CollectionAssert.AreEquivalent(
                new[] { FireMode.Auto, FireMode.Burst, FireMode.Semi },
                _def.fireModes);
            CollectionAssert.AllItemsAreUnique(_def.fireModes,
                "默认射击模式不应重复（V 键循环会卡住）。");
        }

        [Test]
        public void SpreadBounds_AreOrdered()
        {
            Assert.LessOrEqual(_def.hipSpreadMin, _def.hipSpreadMax, "腰射散布区间");
            Assert.LessOrEqual(_def.adsSpreadMin, _def.adsSpreadMax, "开镜散布区间");
            // 开镜应当比腰射更准。
            Assert.Less(_def.adsSpreadMin, _def.hipSpreadMin);
        }
    }
}
