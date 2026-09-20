using System.Collections.Generic;
using HagenDa.Networking;
using NUnit.Framework;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Combat
{
    /// <summary>
    /// 配装校验规则（PHASE8）。<see cref="HagenDa.Networking.LoadoutRules"/> 是从
    /// <c>NetworkEquipment.ValidateLoadout</c> 抽出的纯逻辑，本身标注"extracted so they
    /// can be unit-tested"——这里就是它的测试。
    /// </summary>
    [TestFixture]
    public class LoadoutRulesTests
    {
        private readonly List<EquipmentDefinition> _created = new List<EquipmentDefinition>();
        private List<EquipmentDefinition> _inventory;

        [TearDown]
        public void TearDown()
        {
            foreach (var def in _created)
                Object.DestroyImmediate(def);
            _created.Clear();
            _inventory = null;
        }

        private EquipmentDefinition Make(EquipmentCategory category)
        {
            var def = ScriptableObject.CreateInstance<EquipmentDefinition>();
            def.category = category;
            _created.Add(def);
            return def;
        }

        /// <summary>
        /// 构造与真实资产同构的库存：索引即配装槽位存的值。
        /// 0/1 = Optional×2，2 = Special，3 = Throwable。
        /// </summary>
        private List<EquipmentDefinition> StandardInventory()
        {
            _inventory = new List<EquipmentDefinition>
            {
                Make(EquipmentCategory.Optional),
                Make(EquipmentCategory.Optional),
                Make(EquipmentCategory.Special),
                Make(EquipmentCategory.Throwable),
            };
            return _inventory;
        }

        private static LoadoutDefinition ValidLoadout() => new LoadoutDefinition
        {
            optional1 = 0,
            optional2 = 1,
            special = 2,
            throwable = 3,
        };

        // ---------------------------------------------------------------
        // 成功路径
        // ---------------------------------------------------------------

        [Test]
        public void ValidLoadout_Passes()
        {
            Assert.IsTrue(LoadoutRules.Validate(ValidLoadout(), StandardInventory(), out var reason),
                $"合法配装应通过，但被拒绝：{reason}");
            Assert.IsEmpty(reason);
        }

        // ---------------------------------------------------------------
        // 空值防御
        // ---------------------------------------------------------------

        [Test]
        public void NullLoadout_IsRejected()
        {
            Assert.IsFalse(LoadoutRules.Validate(null, StandardInventory(), out var reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void NullInventory_IsRejected()
        {
            Assert.IsFalse(LoadoutRules.Validate(ValidLoadout(), null, out var reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void EmptyInventory_IsRejected()
        {
            Assert.IsFalse(LoadoutRules.Validate(
                ValidLoadout(), new List<EquipmentDefinition>(), out var reason));
            Assert.IsNotEmpty(reason);
        }

        // ---------------------------------------------------------------
        // 索引越界
        // ---------------------------------------------------------------

        [TestCase(-1)]
        [TestCase(4)]
        [TestCase(999)]
        public void OutOfRangeOptional1_IsRejected(int index)
        {
            var loadout = ValidLoadout();
            loadout.optional1 = index;
            Assert.IsFalse(LoadoutRules.Validate(loadout, StandardInventory(), out var reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void DefaultLoadout_AllMinusOne_IsRejected()
        {
            // LoadoutDefinition 的字段默认全是 -1（"未选择"），必须被拒绝。
            Assert.IsFalse(LoadoutRules.Validate(
                new LoadoutDefinition(), StandardInventory(), out var reason));
            Assert.IsNotEmpty(reason);
        }

        // ---------------------------------------------------------------
        // 类别不符
        // ---------------------------------------------------------------

        [Test]
        public void OptionalSlotPointingAtSpecial_IsRejected()
        {
            var loadout = ValidLoadout();
            loadout.optional1 = 2;   // 索引 2 是 Special
            loadout.optional2 = 1;
            Assert.IsFalse(LoadoutRules.Validate(loadout, StandardInventory(), out var reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void SpecialSlotPointingAtOptional_IsRejected()
        {
            var loadout = ValidLoadout();
            loadout.special = 0;     // 索引 0 是 Optional
            Assert.IsFalse(LoadoutRules.Validate(loadout, StandardInventory(), out var reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void ThrowableSlotPointingAtOptional_IsRejected()
        {
            var loadout = ValidLoadout();
            loadout.throwable = 0;
            Assert.IsFalse(LoadoutRules.Validate(loadout, StandardInventory(), out var reason));
            Assert.IsNotEmpty(reason);
        }

        // ---------------------------------------------------------------
        // 两个可选槽必须不同
        // ---------------------------------------------------------------

        [Test]
        public void DuplicateOptionals_AreRejected()
        {
            var loadout = ValidLoadout();
            loadout.optional1 = 1;
            loadout.optional2 = 1;
            Assert.IsFalse(LoadoutRules.Validate(loadout, StandardInventory(), out var reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void SwappedOptionals_AreStillValid()
        {
            // 顺序无关：0/1 与 1/0 等价。
            var loadout = ValidLoadout();
            loadout.optional1 = 1;
            loadout.optional2 = 0;
            Assert.IsTrue(LoadoutRules.Validate(loadout, StandardInventory(), out _));
        }

        // ---------------------------------------------------------------
        // IsCategory 直接测试
        // ---------------------------------------------------------------

        [Test]
        public void IsCategory_ChecksIndexAndCategory()
        {
            var inventory = StandardInventory();

            Assert.IsTrue(LoadoutRules.IsCategory(inventory, 0, EquipmentCategory.Optional));
            Assert.IsTrue(LoadoutRules.IsCategory(inventory, 2, EquipmentCategory.Special));
            Assert.IsTrue(LoadoutRules.IsCategory(inventory, 3, EquipmentCategory.Throwable));

            Assert.IsFalse(LoadoutRules.IsCategory(inventory, 0, EquipmentCategory.Special),
                "类别不符应返回 false。");
            Assert.IsFalse(LoadoutRules.IsCategory(inventory, -1, EquipmentCategory.Optional),
                "负索引应返回 false。");
            Assert.IsFalse(LoadoutRules.IsCategory(inventory, 4, EquipmentCategory.Optional),
                "越界索引应返回 false。");
        }

        [Test]
        public void IsCategory_NullEntryInInventory_IsRejected()
        {
            var inventory = StandardInventory();
            inventory[0] = null;
            Assert.IsFalse(LoadoutRules.IsCategory(inventory, 0, EquipmentCategory.Optional),
                "库存中的空槽应被拒绝而非抛异常。");
        }
    }
}
