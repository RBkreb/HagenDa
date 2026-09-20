using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 匹配大厅连接管理(PHASE15 界面系统,客户端)。
    ///
    /// 规格:
    ///  - 进入游戏时不进行 host 连接(本组件不自动启动任何连接);
    ///  - 点击"开始游戏"后进入匹配大厅:可主持 host,或填入 host 地址加入;
    ///  - 连接完成后进入房间(房间相位由 <see cref="NetworkRoomController"/> 管理);
    ///  - 支持断开 host 连接、退出至大厅、退出游戏。
    ///
    /// 纯 MonoBehaviour:大厅场景里没有 NetworkManager 也应当可用(离线预览 UI),
    /// 只有真正点"主持/加入"时才需要 NetworkManager.singleton。
    /// </summary>
    public class LobbyConnection : MonoBehaviour
    {
        public static LobbyConnection Instance { get; private set; }

        /// <summary>大厅默认端口(与 kcp2k 一致)。</summary>
        public const int DefaultPort = 7777;

        [Tooltip("默认 host 地址(局域网调试用)。")]
        public string defaultAddress = "localhost";

        [Tooltip("加入连接端口。")]
        public int port = DefaultPort;

        /// <summary>当前连接状态(UI 直读)。</summary>
        public enum State { Offline, Connecting, Connected, Hosting }

        public State Current { get; private set; } = State.Offline;

        /// <summary>最近一次失败原因(UI 显示)。</summary>
        public string LastError { get; private set; } = "";

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------
        // HOST / JOIN
        // ---------------------------------------------------------------

        /// <summary>主持 host:在本机开始服务器并作为 host 客户端进入。</summary>
        public bool StartHosting()
        {
            var nm = NetworkManager.singleton;
            if (nm == null)
            {
                LastError = "场景中没有 NetworkManager,无法主持。";
                return false;
            }
            if (NetworkServer.active || NetworkClient.active)
            {
                LastError = "已有活动连接,请先断开。";
                return false;
            }

            try
            {
                nm.networkAddress = "localhost";
                if (nm.transport is Mirror.PortTransport pt)
                    pt.Port = (ushort)port;

                nm.StartHost();
                Current = State.Hosting;
                LastError = "";
                return true;
            }
            catch (System.Exception e)
            {
                LastError = "主持失败:" + e.Message;
                Current = State.Offline;
                return false;
            }
        }

        /// <summary>加入指定地址的 host。</summary>
        public bool JoinAddress(string address)
        {
            // 先校验用户输入(最可执行的提示),再看环境。
            if (string.IsNullOrWhiteSpace(address))
            {
                LastError = "请填写 host 地址。";
                return false;
            }

            var nm = NetworkManager.singleton;
            if (nm == null)
            {
                LastError = "场景中没有 NetworkManager,无法加入。";
                return false;
            }
            if (NetworkServer.active || NetworkClient.active)
            {
                LastError = "已有活动连接,请先断开。";
                return false;
            }

            try
            {
                nm.networkAddress = address.Trim();
                if (nm.transport is Mirror.PortTransport pt)
                    pt.Port = (ushort)port;

                Current = State.Connecting;
                nm.StartClient();
                LastError = "";
                return true;
            }
            catch (System.Exception e)
            {
                LastError = "加入失败:" + e.Message;
                Current = State.Offline;
                return false;
            }
        }

        // ---------------------------------------------------------------
        // DISCONNECT / QUIT
        // ---------------------------------------------------------------

        /// <summary>断开 host 连接(host 则停机,客户端则断开)。</summary>
        public void Disconnect()
        {
            var nm = NetworkManager.singleton;
            if (nm != null)
            {
                if (NetworkServer.active) nm.StopHost();
                else if (NetworkClient.active) nm.StopClient();
            }

            Current = State.Offline;
        }

        /// <summary>退出至大厅:断开连接并回到大厅场景(若有配置)。</summary>
        public void ReturnToLobby(string lobbySceneName = "")
        {
            Disconnect();

            if (!string.IsNullOrEmpty(lobbySceneName))
            {
                // 大厅场景需在 Build Settings 中;不存在时静默保持当前场景(便于测试)。
                if (Application.CanStreamedLevelBeLoaded(lobbySceneName))
                    UnityEngine.SceneManagement.SceneManager.LoadScene(lobbySceneName);
            }
        }

        /// <summary>退出游戏。</summary>
        public void QuitGame()
        {
            Disconnect();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>连接状态刷新(由 UI 每帧调用;真实状态以 Mirror 为准)。</summary>
        public void RefreshStateFromMirror()
        {
            if (NetworkServer.active) Current = State.Hosting;
            else if (NetworkClient.isConnected) Current = State.Connected;
            else if (NetworkClient.active) Current = State.Connecting;
            else Current = State.Offline;
        }
    }
}
