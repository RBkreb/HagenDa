using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HagenDa.Tests.PlayMode
{
    /// <summary>
    /// PlayMode 管道冒烟测试。
    ///
    /// 本程序集存在的意义是"保留 PlayMode 口子"：EditMode 测试无法真正跑
    /// <c>NetworkBehaviour</c> 生命周期，而本项目核心是联网对局。这里只做最小验证——
    /// **运行时可执行 + Mirror 织入在运行时依旧成立**，证明接下去可以直接往这个
    /// 程序集里加 `NetworkServer` / 输入注入等真实集成测试，无需再动 asmdef。
    /// </summary>
    [TestFixture]
    public class PlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator PlayMode_RunsAndGameAssemblyIsLoaded()
        {
            yield return null;   // 至少跨一帧，证明协程式测试循环真的在跑

            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "HagenDa.Game");

            Assert.IsNotNull(asm, "PlayMode 下 HagenDa.Game 程序集未加载。");
            Assert.AreNotEqual("Assembly-CSharp", asm.GetName().Name,
                "游戏代码仍停留在预定义程序集，测试程序集将无法引用它。");
        }

        [UnityTest]
        public IEnumerator PlayMode_MirrorWeaving_IsPresentAtRuntime()
        {
            yield return null;

            var asm = typeof(HagenDa.Networking.NetworkPlayerController).Assembly;

            // 织入器注入的类型：存在即证明 ILPostProcessor 处理了 HagenDa.Game。
            var generated = asm.GetType("Mirror.GeneratedNetworkCode");
            Assert.IsNotNull(generated,
                "运行时未找到 Mirror.GeneratedNetworkCode —— 织入未生效，" +
                "NetworkBehaviour 的序列化会在联机时静默失败。");

            var writerNames = generated
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Select(m => m.Name)
                .ToHashSet();

            Assert.IsTrue(writerNames.Contains("_Write_HagenDa.Networking.NetworkInputState"),
                "运行时缺少 NetworkInputState 的生成写方法。");
            Assert.IsTrue(writerNames.Contains("_Read_HagenDa.Networking.NetworkInputState"),
                "运行时缺少 NetworkInputState 的生成读方法。");
        }

        [UnityTest]
        public IEnumerator PlayMode_WireStruct_RoundTripsInRuntime()
        {
            yield return null;

            // EditMode 已覆盖同一断言；此处确认运行时（含织入后的 JIT 路径）行为一致。
            var original = new HagenDa.Networking.NetworkInputState
            {
                move = new Vector2(0.75f, -0.5f),
                yaw = 90f,
                pitch = -30f,
                fire = true,
                mark = true,
                deployChoice = 2,
            };

            var writer = new Mirror.NetworkWriter();
            writer.Write(original);
            var restored = new Mirror.NetworkReader(writer.ToArray())
                .Read<HagenDa.Networking.NetworkInputState>();

            Assert.AreEqual(original.move, restored.move);
            Assert.AreEqual(original.yaw, restored.yaw);
            Assert.AreEqual(original.pitch, restored.pitch);
            Assert.IsTrue(restored.fire);
            Assert.IsTrue(restored.mark);
            Assert.AreEqual(original.deployChoice, restored.deployChoice);
        }

        [UnityTest]
        public IEnumerator PlayMode_CanInstantiateGameMonobehaviour()
        {
            yield return null;

            // 轻量健全性检查：游戏组件能在运行时实例化（不启动 Mirror 服务端）。
            var go = new GameObject("PlayModeSmoke");
            try
            {
                var hitbox = go.AddComponent<HagenDa.Networking.NetworkHitbox>();
                Assert.IsNotNull(hitbox);
                Assert.AreEqual(HagenDa.Networking.HitboxPart.Body, hitbox.part,
                    "NetworkHitbox 默认部位应为躯干。");
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }

            yield return null;
        }
    }
}
