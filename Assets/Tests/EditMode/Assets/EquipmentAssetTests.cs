using System.Collections.Generic;
using System.Linq;
using HagenDa.Networking;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Assets
{
    /// <summary>
    /// 装备数据资产不变量。20 个 <see cref="EquipmentDefinition"/> 是配装界面的数据源，
    /// 而配装合法性由 <see cref="LoadoutRules"/> 判定——两者的契约必须一致，
    /// 否则玩家会在界面上看到无法提交的配装。
    /// </summary>
    [TestFixture]
    public class EquipmentAssetTests
    {
        private const string EquipmentFolder = "Assets/Game/Equipment";

        private List<EquipmentDefinition> _assets;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _assets = AssetDatabase
                .FindAssets("t:EquipmentDefinition", new[] { EquipmentFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<EquipmentDefinition>)
                .Where(a => a != null)
                .ToList();
        }

        [Test]
        public void EquipmentFolder_ContainsExpectedNumberOfAssets()
        {
            Assert.AreEqual(20, _assets.Count,
                $"预期 {EquipmentFolder} 下有 20 个 EquipmentDefinition；" +
                "若新增/删除装备资产，请同步更新配装界面与规则测试。");
        }

        [Test]
        public void AllAssets_LoadWithoutNull()
        {
            var guids = AssetDatabase.FindAssets("t:EquipmentDefinition", new[] { EquipmentFolder });
            Assert.AreEqual(guids.Length, _assets.Count,
                "存在无法加载为 EquipmentDefinition 的资产（脚本丢失或类型不匹配）。");
        }

        [Test]
        public void AllAssets_HaveDisplayName()
        {
            foreach (var def in _assets)
                Assert.IsFalse(string.IsNullOrWhiteSpace(def.displayName),
                    $"资产 {def.name} 缺少 displayName。");
        }

        [Test]
        public void AllAssets_HaveDefinedCategory()
        {
            foreach (var def in _assets)
                Assert.IsTrue(System.Enum.IsDefined(typeof(EquipmentCategory), def.category),
                    $"资产 {def.name} 的 category 越界：{(int)def.category}");
        }

        [Test]
        public void AllAssets_HaveDefinedTypeAndUseStyle()
        {
            foreach (var def in _assets)
            {
                Assert.IsTrue(System.Enum.IsDefined(typeof(EquipmentType), def.type),
                    $"资产 {def.name} 的 type 越界：{(int)def.type}");
                Assert.IsTrue(System.Enum.IsDefined(typeof(EquipmentUseStyle), def.useStyle),
                    $"资产 {def.name} 的 useStyle 越界：{(int)def.useStyle}");
            }
        }

        [Test]
        public void OptionalCategory_HasAtLeastTwoDistinctItems()
        {
            // LoadoutRules 要求 optional1 与 optional2 都合法且**互不相同**。
            // 若 Optional 品类少于 2 个，任何配装都无法通过校验 —— 这是界面与规则的
            // 一致性断言，退化成 1 个就是死局。
            int optionalCount = _assets.Count(a => a.category == EquipmentCategory.Optional);

            Assert.GreaterOrEqual(optionalCount, 2,
                $"Optional 品类只有 {optionalCount} 个装备，LoadoutRules 要求至少 2 个不同项才能配出合法配装。");
        }

        [Test]
        public void SpecialCategory_HasAtLeastOneItem()
        {
            Assert.GreaterOrEqual(
                _assets.Count(a => a.category == EquipmentCategory.Special), 1,
                "Special 槽必须至少有一个可选装备，否则配装无法完成。");
        }

        [Test]
        public void ThrowableCategory_HasAtLeastOneItem()
        {
            Assert.GreaterOrEqual(
                _assets.Count(a => a.category == EquipmentCategory.Throwable), 1,
                "Throwable 槽必须至少有一个可选装备，否则配装无法完成。");
        }

        [Test]
        public void AllThreeCategories_AreRepresented()
        {
            // 三个配装槽各自对应一个品类；品类缺失会让对应槽位永远为空。
            var present = _assets.Select(a => a.category).Distinct().ToArray();
            CollectionAssert.AreEquivalent(
                new[] { EquipmentCategory.Optional, EquipmentCategory.Special, EquipmentCategory.Throwable },
                present,
                "三个装备品类都必须有资产。");
        }

        [Test]
        public void NumericTunables_AreNotNegative()
        {
            foreach (var def in _assets)
            {
                Assert.GreaterOrEqual(def.maxCarry, 0, $"{def.name}.maxCarry 不得为负。");
                Assert.GreaterOrEqual(def.deployCap, 0, $"{def.name}.deployCap 不得为负。");
                Assert.GreaterOrEqual(def.supplyCost, 0, $"{def.name}.supplyCost 不得为负。");
                Assert.GreaterOrEqual(def.throwSpeed, 0f, $"{def.name}.throwSpeed 不得为负。");
                Assert.GreaterOrEqual(def.armorGrant, 0f, $"{def.name}.armorGrant 不得为负。");
                Assert.GreaterOrEqual(def.dashCooldown, 0f, $"{def.name}.dashCooldown 不得为负。");
                Assert.GreaterOrEqual(def.empRadius, 0f, $"{def.name}.empRadius 不得为负。");
                Assert.GreaterOrEqual(def.empLifetime, 0f, $"{def.name}.empLifetime 不得为负。");
                Assert.GreaterOrEqual(def.ammoRegenInterval, 0f, $"{def.name}.ammoRegenInterval 不得为负。");
            }
        }

        [Test]
        public void ShieldReduction_IsAProbability()
        {
            // 防爆盾的爆炸减免是比例（0..1），越界会让伤害计算出现负伤害或放大。
            foreach (var def in _assets)
                Assert.That(def.shieldExplosionReduction, Is.InRange(0f, 1f),
                    $"{def.name}.shieldExplosionReduction 必须落在 [0,1]。");
        }

        [Test]
        public void ThrowableBasedEquipment_HasThrowSpeed()
        {
            // 投掷类装备（Throw / BoltLauncher）必须有正的抛射速度，否则投出即落地。
            foreach (var def in _assets.Where(a =>
                         a.useStyle == EquipmentUseStyle.Throw ||
                         a.useStyle == EquipmentUseStyle.BoltLauncher))
            {
                Assert.Greater(def.throwSpeed, 0f,
                    $"{def.name} 是投掷类（useStyle={def.useStyle}），throwSpeed 必须为正。");
            }
        }
    }
}
