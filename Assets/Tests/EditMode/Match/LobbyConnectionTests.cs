using HagenDa.Networking;
using NUnit.Framework;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Match
{
    /// <summary>
    /// 大厅连接/退出行为(PHASE15 界面系统)。
    ///
    /// 这些用例刻意**不**连接真实网络(无端口、无 NetworkManager),验证的是
    /// 规格里的三件"离线也要正确"的事:
    ///  1. 进入游戏不做 host 连接;
    ///  2. 非法的 host/join 请求被拒绝且给出可读原因(而非静默启动连接);
    ///  3. 断开/退出路径幂等,且不会因缺少 NetworkManager 而抛异常。
    /// </summary>
    [TestFixture]
    public class LobbyConnectionTests
    {
        private GameObject _go;
        private LobbyConnection _lobby;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("LobbyTest");
            _lobby = _go.AddComponent<LobbyConnection>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        [Test]
        public void StartsOffline()
        {
            Assert.AreEqual(LobbyConnection.State.Offline, _lobby.Current,
                "进入游戏时不得处于连接状态(规格:不进行 host 连接)。");
        }

        [Test]
        public void Hosting_WithoutNetworkManager_FailsGracefully()
        {
            // 测试环境无 NetworkManager.singleton → 应返回 false 并写明原因。
            bool ok = _lobby.StartHosting();

            Assert.IsFalse(ok, "无 NetworkManager 时主持应失败而不是崩溃。");
            StringAssert.Contains("NetworkManager", _lobby.LastError);
            Assert.AreEqual(LobbyConnection.State.Offline, _lobby.Current);
        }

        [Test]
        public void Joining_WithoutNetworkManager_FailsGracefully()
        {
            bool ok = _lobby.JoinAddress("127.0.0.1");

            Assert.IsFalse(ok);
            StringAssert.Contains("NetworkManager", _lobby.LastError);
        }

        [Test]
        public void Joining_EmptyAddress_IsRejectedBeforeConnecting()
        {
            bool ok = _lobby.JoinAddress("   ");

            Assert.IsFalse(ok, "空地址必须先被拒绝(不能拿空串去连接)。");
            StringAssert.Contains("地址", _lobby.LastError);
        }

        [Test]
        public void Disconnect_IsSafe_WhenOffline()
        {
            Assert.DoesNotThrow(() => _lobby.Disconnect());
            Assert.AreEqual(LobbyConnection.State.Offline, _lobby.Current);
        }

        [Test]
        public void Disconnect_IsIdempotent()
        {
            _lobby.Disconnect();
            _lobby.Disconnect();
            Assert.AreEqual(LobbyConnection.State.Offline, _lobby.Current);
        }

        [Test]
        public void ReturnToLobby_UnknownScene_DoesNotThrow()
        {
            // 场景不存在时应静默保持当前场景(便于测试/单场景运行)。
            Assert.DoesNotThrow(() => _lobby.ReturnToLobby("NoSuchScene_XYZ"));
            Assert.AreEqual(LobbyConnection.State.Offline, _lobby.Current);
        }

        [Test]
        public void DefaultPort_MatchesKcpDefault()
        {
            Assert.AreEqual(7777, LobbyConnection.DefaultPort);
            Assert.AreEqual(LobbyConnection.DefaultPort, _lobby.port);
        }
    }
}
