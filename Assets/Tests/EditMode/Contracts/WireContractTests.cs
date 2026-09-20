using System;
using System.Linq;
using System.Reflection;
using Mirror;
using NUnit.Framework;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Contracts
{
    /// <summary>
    /// 线路契约（wire contract）回归测试。
    ///
    /// <see cref="HagenDa.Networking.NetworkInputState"/> 与
    /// <see cref="HagenDa.Networking.LoadoutDefinition"/> 经 Mirror 的
    /// <c>[Command]</c> / <c>SyncVar</c> 序列化，字段的**名称、类型、顺序**即线上协议：
    /// 任何一项变动都会让不同构建的客户端与服务端静默失配。本测试把这些固定下来。
    /// </summary>
    [TestFixture]
    public class WireContractTests
    {
        private static Assembly GameAssembly => typeof(HagenDa.Networking.NetworkInputState).Assembly;

        private static (string type, string name)[] FieldShape(Type t) =>
            t.GetFields(BindingFlags.Public | BindingFlags.Instance)
             .Select(f => (f.FieldType.Name, f.Name))
             .ToArray();

        // ---------------------------------------------------------------
        // 1. 迁移不变量：游戏代码必须已迁入 HagenDa.Game 程序集
        // ---------------------------------------------------------------

        [Test]
        public void GameCode_LivesInNamedAssembly_NotPredefined()
        {
            Assert.AreEqual("HagenDa.Game", GameAssembly.GetName().Name,
                "游戏代码必须编译进 HagenDa.Game 程序集（asmdef 迁移的结果）；" +
                "若回到 Assembly-CSharp，则测试程序集将无法引用它。");
            Assert.AreNotEqual("Assembly-CSharp", GameAssembly.GetName().Name);
        }

        // ---------------------------------------------------------------
        // 2. Mirror 织入探针（本次 asmdef 迁移的头号风险）
        // ---------------------------------------------------------------
        // Mirror 的 ILPostProcessor 只处理"引用了 Mirror 的程序集"。若 HagenDa.Game
        // 漏掉 Mirror 引用，织入会**静默**失效——编译通过、运行期才报
        // "No writer found for X"。Mirror.GeneratedNetworkCode 是织入器注入的类型，
        // 它存在 = 织入确实跑过。

        [Test]
        public void MirrorWeaver_InjectedGeneratedNetworkCode()
        {
            var generated = GameAssembly.GetType("Mirror.GeneratedNetworkCode");
            Assert.IsNotNull(generated,
                "Mirror.GeneratedNetworkCode 未出现在 HagenDa.Game 中 —— " +
                "说明 Mirror 织入没有作用于该程序集（检查 asmdef 是否引用 Mirror）。");
        }

        [Test]
        public void MirrorWeaver_GeneratedReaderWriter_ForWireStructs()
        {
            var generated = GameAssembly.GetType("Mirror.GeneratedNetworkCode");
            Assert.IsNotNull(generated, "先决条件失败：Mirror.GeneratedNetworkCode 缺失。");

            var names = generated
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Select(m => m.Name)
                .Distinct()
                .ToArray();

            foreach (var t in new[] { "NetworkInputState", "LoadoutDefinition", "PlayerPosture" })
            {
                var suffix = "HagenDa.Networking." + t;
                Assert.IsTrue(names.Any(n => n == "_Write_" + suffix),
                    $"缺少生成的写方法 _Write_{suffix} —— 织入未覆盖该类型。");
                Assert.IsTrue(names.Any(n => n == "_Read_" + suffix),
                    $"缺少生成的读方法 _Read_{suffix} —— 织入未覆盖该类型。");
            }
        }

        // ---------------------------------------------------------------
        // 3. 真实序列化往返：语义保真
        // ---------------------------------------------------------------

        [Test]
        public void NetworkInputState_RoundTripsThroughMirror()
        {
            var original = new HagenDa.Networking.NetworkInputState
            {
                move = new Vector2(0.5f, -0.25f),
                yaw = 123.75f,
                pitch = -42.5f,
                jump = true,
                sprint = true,
                crouchToggle = true,
                proneToggle = true,
                crouchHold = true,
                fire = true,
                aim = true,
                reload = true,
                switchFireMode = true,
                mark = true,
                slotPrimary = true,
                slotOpt1 = true,
                slotOpt2 = true,
                slotSpecial = true,
                slotThrowable = true,
                deployChoice = 3,
            };

            var writer = new NetworkWriter();
            writer.Write(original);
            var restored = new NetworkReader(writer.ToArray())
                .Read<HagenDa.Networking.NetworkInputState>();

            Assert.AreEqual(original.move, restored.move, "move");
            Assert.AreEqual(original.yaw, restored.yaw, "yaw");
            Assert.AreEqual(original.pitch, restored.pitch, "pitch");
            Assert.IsTrue(restored.jump, "jump");
            Assert.IsTrue(restored.sprint, "sprint");
            Assert.IsTrue(restored.crouchToggle, "crouchToggle");
            Assert.IsTrue(restored.proneToggle, "proneToggle");
            Assert.IsTrue(restored.crouchHold, "crouchHold");
            Assert.IsTrue(restored.fire, "fire");
            Assert.IsTrue(restored.aim, "aim");
            Assert.IsTrue(restored.reload, "reload");
            Assert.IsTrue(restored.switchFireMode, "switchFireMode");
            Assert.IsTrue(restored.mark, "mark");
            Assert.IsTrue(restored.slotPrimary, "slotPrimary");
            Assert.IsTrue(restored.slotOpt1, "slotOpt1");
            Assert.IsTrue(restored.slotOpt2, "slotOpt2");
            Assert.IsTrue(restored.slotSpecial, "slotSpecial");
            Assert.IsTrue(restored.slotThrowable, "slotThrowable");
            Assert.AreEqual(original.deployChoice, restored.deployChoice, "deployChoice");
        }

        [Test]
        public void LoadoutDefinition_RoundTripsThroughMirror()
        {
            var original = new HagenDa.Networking.LoadoutDefinition
            {
                optional1 = 2,
                optional2 = 7,
                special = 15,
                throwable = 1,
            };

            var writer = new NetworkWriter();
            writer.Write(original);
            var restored = new NetworkReader(writer.ToArray())
                .Read<HagenDa.Networking.LoadoutDefinition>();

            Assert.AreEqual(original.optional1, restored.optional1, "optional1");
            Assert.AreEqual(original.optional2, restored.optional2, "optional2");
            Assert.AreEqual(original.special, restored.special, "special");
            Assert.AreEqual(original.throwable, restored.throwable, "throwable");
        }

        // ---------------------------------------------------------------
        // 4. 字段顺序钉住（顺序即协议）
        // ---------------------------------------------------------------

        [Test]
        public void NetworkInputState_Fields_AreStable()
        {
            // 顺序敏感：Mirror 按声明顺序写入，插入/重排字段会让新旧构建互相错读。
            var expected = new (string, string)[]
            {
                ("Vector2", "move"),
                ("Single", "yaw"),
                ("Single", "pitch"),
                ("Boolean", "jump"),
                ("Boolean", "sprint"),
                ("Boolean", "crouchToggle"),
                ("Boolean", "proneToggle"),
                ("Boolean", "crouchHold"),
                ("Boolean", "fire"),
                ("Boolean", "aim"),
                ("Boolean", "reload"),
                ("Boolean", "switchFireMode"),
                ("Boolean", "mark"),
                ("Boolean", "slotPrimary"),
                ("Boolean", "slotOpt1"),
                ("Boolean", "slotOpt2"),
                ("Boolean", "slotSpecial"),
                ("Boolean", "slotThrowable"),
                ("Int32", "deployChoice"),
            };

            var actual = FieldShape(typeof(HagenDa.Networking.NetworkInputState));

            Assert.AreEqual(expected.Length, actual.Length,
                "NetworkInputState 字段数量变化 —— 这是线上协议破坏。");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].Item1, actual[i].type,
                    $"第 {i} 个字段类型变化（应为 {expected[i].Item1} {expected[i].Item2}）。");
                Assert.AreEqual(expected[i].Item2, actual[i].name,
                    $"第 {i} 个字段名变化（应为 {expected[i].Item1} {expected[i].Item2}）。");
            }
        }

        [Test]
        public void LoadoutDefinition_Fields_AreStable()
        {
            var expected = new (string, string)[]
            {
                ("Int32", "optional1"),
                ("Int32", "optional2"),
                ("Int32", "special"),
                ("Int32", "throwable"),
            };

            var actual = FieldShape(typeof(HagenDa.Networking.LoadoutDefinition));

            Assert.AreEqual(expected.Length, actual.Length,
                "LoadoutDefinition 字段数量变化 —— 这是线上协议破坏。");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].Item1, actual[i].type);
                Assert.AreEqual(expected[i].Item2, actual[i].name);
            }
        }

        [Test]
        public void LoadoutDefinition_PrimarySlot_IsMinusOne()
        {
            // primary 槽固定为 -1（当前仅 M4）——该常量参与配装校验与序列化。
            Assert.AreEqual(-1, HagenDa.Networking.LoadoutDefinition.PrimaryIndex);
        }

        // ---------------------------------------------------------------
        // 5. 枚举序号钉住（以 int 上网 / 存盘）
        // ---------------------------------------------------------------

        private static void AssertEnumOrdinals<T>(params (string name, int value)[] expected)
            where T : struct, Enum
        {
            foreach (var (name, value) in expected)
            {
                Assert.IsTrue(Enum.IsDefined(typeof(T), name),
                    $"{typeof(T).Name} 中不存在枚举成员 {name}。");
                Assert.AreEqual(value, Convert.ToInt32(Enum.Parse(typeof(T), name)),
                    $"{typeof(T).Name}.{name} 的序号变了 —— 该值以 int 序列化，会破坏兼容。");
            }
        }

        [Test]
        public void Enums_UsedOnWireOrInAssets_HaveStableOrdinals()
        {
            AssertEnumOrdinals<HagenDa.Networking.PlayerPosture>(
                ("Stand", 0), ("Crouch", 1), ("Prone", 2));

            AssertEnumOrdinals<HagenDa.Networking.MatchTeam>(
                ("Red", 0), ("Blue", 1));

            AssertEnumOrdinals<HagenDa.Networking.HitboxPart>(
                ("Body", 0), ("Head", 1), ("Limb", 2));

            AssertEnumOrdinals<HagenDa.Networking.EquipmentCategory>(
                ("Optional", 0), ("Special", 1), ("Throwable", 2));

            AssertEnumOrdinals<HagenDa.Networking.FireMode>(
                ("Auto", 0), ("Semi", 1), ("Burst", 2), ("Bolt", 3));

            // EquipmentType 存进 20 个 EquipmentDefinition.asset，重排会让资产数据错位。
            Assert.AreEqual(20, Enum.GetValues(typeof(HagenDa.Networking.EquipmentType)).Length);
            AssertEnumOrdinals<HagenDa.Networking.EquipmentType>(
                ("Grenade", 0),
                ("SmokeGrenade", 1),
                ("EmpGrenade", 2),
                ("GrenadeLauncher", 3),
                ("Rpg", 5),
                ("Jammer", 19));
        }
    }
}
