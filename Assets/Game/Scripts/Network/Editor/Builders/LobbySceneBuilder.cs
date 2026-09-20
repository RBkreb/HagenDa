using Mirror;
using UnityEditor;
using UnityEngine;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// NetworkSetup (partial) — PHASE15 局外系统:一键生成**离线大厅场景**。
    ///
    /// 规格:"进入游戏时不加载任何地图、模型,不进行 host 连接"。因此本场景刻意
    /// **不含**任何地图几何 / 实体 / 已启动的服务器,只有:
    ///   - NetworkManager(子类 <see cref="NetworkRoomManager"/>):存在但**不启动**,
    ///     等玩家在大厅点击"主持/加入"才连;
    ///   - <see cref="LobbyConnection"/> + <see cref="LobbyScreen"/>:三步界面
    ///     (开始游戏 → 匹配大厅 → 房间),含断开/退出至大厅/退出游戏;
    ///   - <see cref="NetworkRoomController"/>:承载房间相位与队伍/AI 补位逻辑,
    ///     运行期自行 DontDestroyOnLoad 以跨场景存活。
    ///
    /// 对局地图仍由既有 <c>HagenDa/Match/Build From Selected MatchConfig</c> 生成;
    /// 把生成的对局场景名填入 <see cref="NetworkRoomController.mapSceneName"/> 即可由
    /// host 开局时切换过去。
    /// </summary>
    public static partial class NetworkSetup
    {
        private const string LobbyScenePath = "Assets/Game/Scenes/Lobby.scene";

        [MenuItem("HagenDa/PHASE15/Create Lobby Scene")]
        public static void CreateLobbyScene()
        {
            EnsureFolder("Assets/Game", "Scenes");

            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();

            // ---- 房间控制器(运行期常驻,承载相位/分队/AI 补位) ----
            var roomGo = new GameObject("NetworkRoomController");
            roomGo.AddComponent<NetworkIdentity>();
            roomGo.AddComponent<NetworkRoomController>();

            // ---- NetworkManager(存在但未启动) ----
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkRoomManager>();
            nm.transport = nmGo.AddComponent<kcp2k.KcpTransport>();
            nm.autoCreatePlayer = true;
            nm.playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            nm.offlineScene = "";
            nm.onlineScene = "";

            // ---- 大厅连接 + 界面 ----
            var lobbyGo = new GameObject("Lobby");
            lobbyGo.AddComponent<LobbyConnection>();
            var screen = lobbyGo.AddComponent<LobbyScreen>();
            screen.lobbySceneName = LobbyScenePath;

            EnsureEventSystem();

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, LobbyScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PHASE15] 大厅场景就绪:{LobbyScenePath}" +
                      "(Play 后不连 host、不加载地图;点『开始游戏』进入匹配大厅)");
        }
    }
}
