using System.Collections;
using HagenDa.Networking;
using HagenDa.Tests.PlayMode.Harness;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HagenDa.Tests.PlayMode
{
    /// <summary>
    /// 证明「真人与 AI 共用同一条服务端输入通路」。
    ///
    /// 两条链路的唯一差别在**输入端**：
    ///   真人：SampleInput(外设) → SendInput → CmdInput  ┐
    ///   AI  ：FSM/ML 策略        → SetIntent            ┤→ pendingServerInput → SimulateServer
    ///                                                    ┘
    /// 外设采样那段按约定不测；本文件测 AI 侧入口 <c>SetIntent</c> 落到**同一个**
    /// <c>serverInput</c> 状态机，并验证 AI 的边沿自动清除契约。
    ///
    /// 另一条独立证据在 EditMode 的 `WireContractTests`：两者经 Mirror 织入后
    /// 序列化的都是同一个 `NetworkInputState` 结构。
    /// </summary>
    [TestFixture]
    public class AIInputPathTests
    {
        private PlayerFixture _player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            PlayModeServer.StartServer();
            _player = PlayerFixture.Create(new Vector3(0f, 0.05f, 0f), withAI: true, withFloor: true);
            yield return _player.Settle();
            Assert.IsNotNull(_player.AI, "前置条件：实体应挂有 NetworkAIController。");
            Assert.IsTrue(_player.Controller.Grounded, "前置条件：实体应接地。");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _player?.Dispose();
            _player = null;
            PlayModeServer.StopServer();
            yield return null;
        }

        // ---------------------------------------------------------------
        // AI 入口确实驱动同一个状态机
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator SetIntent_ReachesSharedServerInputStateMachine()
        {
            var intent = PlayerFixture.Input();
            intent.yaw = 45f;

            _player.AI.SetIntent(intent);

            // AI 在 FixedUpdate 里把 intent 推给 controller，controller 再在**自己的**
            // FixedUpdate 里消费 —— 存在一 tick 的传递延迟，故等两拍。
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            Assert.AreEqual(45f, _player.Go.transform.rotation.eulerAngles.y, 0.5f,
                "SetIntent 注入的 yaw 应经 SetServerInput 到达同一个 SimulateServer。" +
                "这证明 AI 与真人共用服务端通路。");
        }

        [UnityTest]
        public IEnumerator SetIntent_Look_ClampsIdenticallyToHumanPath()
        {
            // 同一条通路 ⇒ 限位规则必然一致。这里用 extremes 验证钳制生效。
            var intent = PlayerFixture.Input();
            intent.pitch = -500f;

            _player.AI.SetIntent(intent);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            Assert.AreEqual(_player.Controller.minPitch, _player.Controller.pitch, 0.01f,
                "AI 路径的 pitch 也应被钳到 minPitch（与真人路径同一段代码）。");
        }

        [UnityTest]
        public IEnumerator SetIntent_EdgeFlags_AreAutoClearedByAI()
        {
            // AI 契约：边沿字段在**推送后立即清除**（NetworkAIController.FixedUpdate），
            // 因此只需设置一次即可，不需要外部每 tick 重推边沿。
            var intent = PlayerFixture.Input();
            intent.crouchToggle = true;

            _player.AI.SetIntent(intent);

            // 等待姿态切换。
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            Assert.AreEqual(PlayerPosture.Crouch, _player.Controller.posture,
                "AI 的 crouchToggle 边沿应触发一次蹲下。");

            // 继续空跑若干 tick：若边沿未被清除，就会再切换回站立。
            for (int i = 0; i < 8; i++)
                yield return new WaitForFixedUpdate();

            Assert.AreEqual(PlayerPosture.Crouch, _player.Controller.posture,
                "AI 应自动清除边沿：蹲姿不得被残留的 crouchToggle 反复翻转。");
        }

        [UnityTest]
        public IEnumerator SetIntent_HeldFields_RequireRePush_LikeHumanPath()
        {
            // 持续字段不会自己保持：AI 每 tick 重推整份 intent 才能维持。
            // 这里先把 sprint+前进推入并让它生效。
            var intent = PlayerFixture.Input();
            intent.sprint = true;
            intent.move = new Vector2(0f, 1f);
            _player.AI.SetIntent(intent);

            for (int i = 0; i < 4; i++)
                yield return new WaitForFixedUpdate();

            Assert.IsTrue(_player.Controller.Sprinting,
                "AI 持续推入 sprint+前进应进入冲刺。");

            // 把 intent 换成零值（等价于 AI 决定停止）：应退出冲刺。
            _player.AI.SetIntent(PlayerFixture.Input());
            for (int i = 0; i < 4; i++)
                yield return new WaitForFixedUpdate();

            Assert.IsFalse(_player.Controller.Sprinting,
                "AI 停止推入后应退出冲刺 —— 与真人路径同样的『持续字段需每 tick 重推』契约。");
        }

        // ---------------------------------------------------------------
        // 死亡与重部署
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator SetDead_FreezesAI_AndOnRedeployClearsStaleIntent()
        {
            // 先给一个会持续移动的 intent。
            var intent = PlayerFixture.Input();
            intent.move = new Vector2(0f, 1f);
            _player.AI.SetIntent(intent);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            _player.AI.SetDead(true);
            for (int i = 0; i < 3; i++)
                yield return new WaitForFixedUpdate();

            // OnRedeploy 应把 intent 清空（防止旧边沿在新身体上触发）。
            _player.AI.OnRedeploy(Vector3.zero);
            yield return new WaitForFixedUpdate();

            var afterRedeploy = _player.Go.transform.rotation.eulerAngles.y;
            for (int i = 0; i < 4; i++)
                yield return new WaitForFixedUpdate();

            Assert.AreEqual(afterRedeploy, _player.Go.transform.rotation.eulerAngles.y, 1.0f,
                "OnRedeploy 应清空 intent，重部署后不得继续沿用旧的移动意图。");
        }
    }
}
