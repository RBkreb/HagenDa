using System.Collections;
using HagenDa.Networking;
using HagenDa.Tests.PlayMode.Harness;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HagenDa.Tests.PlayMode
{
    /// <summary>
    /// 共享服务端输入通路测试。
    ///
    /// 核心事实（已核对代码确认）：**真人与 FSM AI 走的是同一条服务端通路**。
    /// <c>CmdInput</c>（真人，经 Mirror 传输）与 <c>SetIntent</c>（AI）最终都只做同一件事
    /// ——写入 <c>pendingServerInput</c>，随后由 <c>SimulateServer</c> 消费为
    /// <c>serverInput</c> 并清空 pending。因此**直接测 <c>SetServerInput</c> 即同时覆盖
    /// 两条路径**，无需也无法测外设采样（<c>Keyboard.current</c> 那段）。
    ///
    /// 本文件只测这一条通路的行为契约：
    ///   - 边沿输入只生效一 tick（jump/crouchToggle/... 不得跨 tick 重复触发）；
    ///   - 持续输入需要每 tick 重推（否则下一 tick 变为零输入）；
    ///   - yaw/pitch 为绝对值且受姿态限位钳制；
    ///   - 姿态状态机与跳跃消费规则。
    /// </summary>
    [TestFixture]
    public class ServerInputPathTests
    {
        private PlayerFixture _player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            PlayModeServer.StartServer();
            // 需要地板：接地由 OnCollisionStay 驱动，而 jump / slide / dive 都以 grounded 为前提
            //（跳跃冲量只在 ApplyMovement 的 grounded 分支里施加）。
            _player = PlayerFixture.Create(new Vector3(0f, 0.05f, 0f), withAI: false, withFloor: true);
            yield return _player.Settle();
            Assert.IsTrue(_player.Controller.Grounded,
                "测试前置条件失败：实体未接地，接地相关断言（跳跃/滑铲）将不可靠。");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _player?.Dispose();
            _player = null;
            PlayModeServer.StopServer();
            yield return null;
        }

        /// <summary>推进 N 个物理 tick，每 tick 都注入同一份输入（模拟真人/AI 的重推行为）。</summary>
        private IEnumerator Tick(NetworkInputState s, int ticks = 1)
        {
            for (int i = 0; i < ticks; i++)
            {
                _player.Inject(s);
                yield return new WaitForFixedUpdate();
            }
        }

        // ---------------------------------------------------------------
        // 通路存在性
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator SetServerInput_IsTheSharedServerFunnel()
        {
            var s = PlayerFixture.Input();
            s.yaw = 123f;

            yield return Tick(s);

            Assert.AreEqual(123f, _player.Go.transform.rotation.eulerAngles.y, 0.5f,
                "SetServerInput 注入的 yaw 应被 SimulateServer 采纳（真人 CmdInput 与 AI SetIntent 都走这里）。");
        }

        [UnityTest]
        public IEnumerator WithNoInput_InjectedYawIsNotRetainedByStalePending()
        {
            var a = PlayerFixture.Input();
            a.yaw = 90f;
            yield return Tick(a);

            // 注入零输入后，yaw 回到 0 —— 证明 pending 每 tick 被清空、注入方必须重推全量。
            yield return Tick(PlayerFixture.Input(), ticks: 2);

            Assert.AreEqual(0f, _player.Go.transform.rotation.eulerAngles.y, 1.0f,
                "停止注入后 yaw 应归零：pendingServerInput 每 tick 清空，持续输入必须每 tick 重推。");
        }

        // ---------------------------------------------------------------
        // 边沿语义：只生效一 tick
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator CrouchToggle_EdgeFiresOnce_NotEveryTick()
        {
            var s = PlayerFixture.Input();
            s.crouchToggle = true;

            // 连续 3 tick 都带 crouchToggle=true。因为 pending 每 tick 清空、注入的是"同一份
            // 带边沿的输入"，这里等价于"每 tick 都按下一次" —— 所以会来回切换。
            // 这正是契约：边沿的"一次性"由**生产者**负责（AI 在 FixedUpdate 清位，
            // 真人按键只在一帧置位），而非 SimulateServer。
            yield return Tick(s, 1);
            Assert.AreEqual(PlayerPosture.Crouch, _player.Controller.posture,
                "第一次 crouchToggle 应由 站→蹲。");

            yield return Tick(s, 1);
            Assert.AreEqual(PlayerPosture.Stand, _player.Controller.posture,
                "再次推动 crouchToggle 应由 蹲→站（toggle 语义）。");
        }

        [UnityTest]
        public IEnumerator CrouchToggle_NotRetriggered_WhenOnlyPushedOnce()
        {
            var press = PlayerFixture.Input();
            press.crouchToggle = true;
            yield return Tick(press, 1);
            Assert.AreEqual(PlayerPosture.Crouch, _player.Controller.posture);

            // 之后若干 tick 推零输入：姿态必须保持蹲，不得因为"边沿残留"再次切换。
            yield return Tick(PlayerFixture.Input(), ticks: 3);

            Assert.AreEqual(PlayerPosture.Crouch, _player.Controller.posture,
                "边沿不得跨 tick 残留：只推一次应只切换一次。");
        }

        [UnityTest]
        public IEnumerator Jump_IncrementsJumpCount_OncePerPress()
        {
            uint before = _player.Controller.jumpCount;

            var jump = PlayerFixture.Input();
            jump.jump = true;
            yield return Tick(jump, 1);

            Assert.AreEqual(before + 1, _player.Controller.jumpCount,
                "一次 jump 边沿应恰好触发一次跳跃。");

            // 落地后推零输入，计数不得继续增长。
            yield return _player.Settle();
            yield return Tick(PlayerFixture.Input(), ticks: 3);

            Assert.AreEqual(before + 1, _player.Controller.jumpCount,
                "边沿不得残留：无跳跃输入时 jumpCount 不应再增长。");
        }

        [UnityTest]
        public IEnumerator Jump_WhileCrouched_StandsUpAndDoesNotAddJump()
        {
            // 先蹲下。
            var crouch = PlayerFixture.Input();
            crouch.crouchToggle = true;
            yield return Tick(crouch, 1);
            Assert.AreEqual(PlayerPosture.Crouch, _player.Controller.posture);

            uint before = _player.Controller.jumpCount;

            // 蹲姿下按跳：契约是"站起来并消费掉这次跳"，不产生实际跳跃冲量。
            var jump = PlayerFixture.Input();
            jump.jump = true;
            yield return Tick(jump, 1);

            Assert.AreEqual(PlayerPosture.Stand, _player.Controller.posture,
                "蹲姿按跳应先站起。");
            Assert.AreEqual(before, _player.Controller.jumpCount,
                "蹲姿按跳被姿态机消费，不应计入跳跃次数。");
        }

        // ---------------------------------------------------------------
        // 绝对值视角与限位
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Pitch_ClampsToViewLimits()
        {
            var look = PlayerFixture.Input();
            look.pitch = -500f;                 // 远超下界
            yield return Tick(look, 1);

            Assert.AreEqual(_player.Controller.minPitch, _player.Controller.pitch, 0.01f,
                "pitch 应被钳到 minPitch。");
        }

        [UnityTest]
        public IEnumerator Pitch_Prone_UsesProneLimits()
        {
            // 进入趴姿。
            var prone = PlayerFixture.Input();
            prone.proneToggle = true;
            yield return Tick(prone, 1);
            Assert.AreEqual(PlayerPosture.Prone, _player.Controller.posture);

            // 趴姿下给一个正向（低头）pitch：应被钳到 proneMaxPitch(=0)。
            var look = PlayerFixture.Input();
            look.pitch = 45f;
            yield return Tick(look, 1);

            Assert.AreEqual(_player.Controller.proneMaxPitch, _player.Controller.pitch, 0.01f,
                "趴姿 pitch 上限应为 proneMaxPitch（不可瞄地）。");
        }

        // ---------------------------------------------------------------
        // 姿态状态机
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Prone_ThenProneToggle_StandsUp()
        {
            var prone = PlayerFixture.Input();
            prone.proneToggle = true;
            yield return Tick(prone, 1);
            Assert.AreEqual(PlayerPosture.Prone, _player.Controller.posture);

            yield return Tick(prone, 1);
            Assert.AreEqual(PlayerPosture.Stand, _player.Controller.posture,
                "趴姿再次 proneToggle 应起身。");
        }

        [UnityTest]
        public IEnumerator Crouch_Posture_SwitchesActiveCollider()
        {
            var crouch = PlayerFixture.Input();
            crouch.crouchToggle = true;
            yield return Tick(crouch, 1);

            Assert.IsTrue(_player.CrouchCollider.enabled, "蹲姿应启用蹲姿胶囊。");
            Assert.IsFalse(_player.StandCollider.enabled, "蹲姿应停用站立胶囊。");

            var stand = PlayerFixture.Input();
            stand.crouchToggle = true;
            yield return Tick(stand, 1);

            Assert.IsTrue(_player.StandCollider.enabled, "起身应恢复站立胶囊。");
            Assert.IsFalse(_player.CrouchCollider.enabled);
        }

        [UnityTest]
        public IEnumerator Sprint_RequiresForwardAndShift()
        {
            // 只有 sprint 没有前进 → 不进入冲刺。
            var sprintOnly = PlayerFixture.Input();
            sprintOnly.sprint = true;
            yield return Tick(sprintOnly, 2);
            Assert.IsFalse(_player.Controller.Sprinting,
                "仅按冲刺键（无前进输入）不应进入冲刺。");

            // 前进 + 冲刺 → 进入。
            var both = PlayerFixture.Input();
            both.sprint = true;
            both.move = new Vector2(0f, 1f);
            yield return Tick(both, 2);
            Assert.IsTrue(_player.Controller.Sprinting,
                "前进+冲刺应按契约进入冲刺。");
        }

        [UnityTest]
        public IEnumerator Sprint_ExitsWhenForwardReleased()
        {
            var both = PlayerFixture.Input();
            both.sprint = true;
            both.move = new Vector2(0f, 1f);
            yield return Tick(both, 3);
            Assert.IsTrue(_player.Controller.Sprinting);

            // 松开前进（仍按住 sprint）→ 契约：松前进即退出冲刺。
            var holdOnly = PlayerFixture.Input();
            holdOnly.sprint = true;
            holdOnly.move = Vector2.zero;
            yield return Tick(holdOnly, 2);

            Assert.IsFalse(_player.Controller.Sprinting,
                "松开前进应退出冲刺（sticky sprint）。");
        }

        // ---------------------------------------------------------------
        // 死亡冻结
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Dead_FreezesMovementAndPosture()
        {
            _player.Controller.SetDead(true);
            yield return new WaitForFixedUpdate();

            var want = PlayerFixture.Input();
            want.move = new Vector2(0f, 1f);
            want.crouchToggle = true;
            yield return Tick(want, 3);

            // 死亡状态下 3C 冻结（保持趴姿、不响应姿态/移动输入）。
            Assert.AreEqual(PlayerPosture.Prone, _player.Controller.posture,
                "死亡应强制趴姿并冻结姿态机。");
        }
    }
}
