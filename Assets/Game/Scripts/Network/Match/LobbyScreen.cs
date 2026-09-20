using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace HagenDa.Networking
{
    /// <summary>
    /// 匹配大厅界面(PHASE15 界面系统,客户端)。
    ///
    /// 流程(规格 1→3 步):
    ///  1. 进入游戏:只有"开始游戏"按钮 —— **不加载地图、不连接 host**;
    ///  2. 点开始游戏 → 进入匹配大厅:可"主持"(StartHost)或填入地址"加入";
    ///  3. 连接完成 → 进入房间,界面切到房间状态(由 <see cref="NetworkRoomController"/> 相位驱动):
    ///     空闲显示"开始对局"(host),准备显示"加载中",对局显示"部署"。
    ///
    /// 另有"断开连接 / 退出至大厅 / 退出游戏"。
    ///
    /// 纯 MonoBehaviour + 运行时构建 uGUI(与 DeployScreen 同风格),不依赖预制体。
    /// </summary>
    public class LobbyScreen : MonoBehaviour
    {
        public static LobbyScreen Instance { get; private set; }

        /// <summary>大厅场景名(退出至大厅时用;留空则只在当前场景断开)。</summary>
        public string lobbySceneName = "";

        private enum Page { Splash, Matchmaking, Room }

        private Canvas canvas;
        private RectTransform pageRoot;
        private Page page = Page.Splash;
        private string address = "localhost";
        private string status = "";

        private Font cjk;
        private readonly List<GameObject> builtPages = new List<GameObject>();

        private void Awake()
        {
            Instance = this;
            cjk = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16);
            BuildCanvas();
            ShowPage(Page.Splash);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (LobbyConnection.Instance != null)
                LobbyConnection.Instance.RefreshStateFromMirror();

            // 连接成功后自动进入房间页。
            if (page == Page.Matchmaking && NetworkClient.isConnected)
                ShowPage(Page.Room);
        }

        // ---------------------------------------------------------------
        // PAGE ROUTING
        // ---------------------------------------------------------------

        private void ShowPage(Page next)
        {
            page = next;
            foreach (var go in builtPages) Destroy(go);
            builtPages.Clear();

            switch (next)
            {
                case Page.Splash: BuildSplash(); break;
                case Page.Matchmaking: BuildMatchmaking(); break;
                case Page.Room: BuildRoom(); break;
            }
        }

        // ---------------------------------------------------------------
        // PAGE 1 — 开始游戏(不加载地图 / 不连接)
        // ---------------------------------------------------------------

        private void BuildSplash()
        {
            var root = NewPanel("Splash", pageRoot);
            NewText("Title", root, "HagenDa", 72, Color.white, TextAnchor.MiddleCenter)
                .rectTransform.anchoredPosition = new Vector2(0f, 80f);

            AddButton(root, "开始游戏", new Vector2(0f, -40f), () => ShowPage(Page.Matchmaking));
            AddButton(root, "退出游戏", new Vector2(0f, -120f), Quit);
        }

        // ---------------------------------------------------------------
        // PAGE 2 — 匹配大厅(主持 / 加入)
        // ---------------------------------------------------------------

        private void BuildMatchmaking()
        {
            var root = NewPanel("Matchmaking", pageRoot);
            NewText("Title", root, "匹配大厅", 44, Color.white, TextAnchor.MiddleCenter)
                .rectTransform.anchoredPosition = new Vector2(0f, 180f);

            AddButton(root, "主持 (Host)", new Vector2(0f, 80f), Host);

            // 地址输入框(M4 风格:纯 uGUI InputField)。
            var fieldGo = new GameObject("AddressField", typeof(RectTransform), typeof(Image), typeof(InputField));
            fieldGo.transform.SetParent(root, false);
            var fr = (RectTransform)fieldGo.transform;
            fr.anchorMin = fr.anchorMax = new Vector2(0.5f, 0.5f);
            fr.pivot = new Vector2(0.5f, 0.5f);
            fr.anchoredPosition = new Vector2(0f, -10f);
            fr.sizeDelta = new Vector2(520f, 56f);
            fieldGo.GetComponent<Image>().color = new Color(0.12f, 0.14f, 0.18f, 0.95f);

            var text = NewText("Text", fr, address, 24, Color.white, TextAnchor.MiddleLeft);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(12f, 0f);
            text.rectTransform.offsetMax = new Vector2(-12f, 0f);

            var input = fieldGo.GetComponent<InputField>();
            input.textComponent = text;
            input.onValueChanged.AddListener(v => address = v);
            input.text = address;

            AddButton(root, "加入 (Join)", new Vector2(0f, -100f), () => Join(address));
            AddButton(root, "返回", new Vector2(0f, -180f), () => ShowPage(Page.Splash));

            statusText = NewText("Status", root, "", 20, new Color(1f, 0.85f, 0.4f), TextAnchor.MiddleCenter);
            statusText.rectTransform.anchoredPosition = new Vector2(0f, -260f);
        }

        private void Host()
        {
            var lobby = LobbyConnection.Instance;
            if (lobby == null) { status = "LobbyConnection 缺失。"; RefreshStatus(); return; }
            status = lobby.StartHosting() ? "已主持,正在进入房间…" : lobby.LastError;
            RefreshStatus();
        }

        private void Join(string addr)
        {
            var lobby = LobbyConnection.Instance;
            if (lobby == null) { status = "LobbyConnection 缺失。"; RefreshStatus(); return; }
            status = lobby.JoinAddress(addr) ? "正在连接 " + addr + " …" : lobby.LastError;
            RefreshStatus();
        }

        // ---------------------------------------------------------------
        // PAGE 3 — 房间(相位驱动)
        // ---------------------------------------------------------------

        private void BuildRoom()
        {
            var root = NewPanel("Room", pageRoot);
            NewText("Title", root, "房间", 44, Color.white, TextAnchor.MiddleCenter)
                .rectTransform.anchoredPosition = new Vector2(0f, 180f);

            var room = NetworkRoomController.Instance;
            string phaseLabel = room != null ? PhaseLabel(room.phase) : "等待房间控制器…";

            phaseText = NewText("Phase", root, phaseLabel, 32, new Color(0.6f, 0.9f, 1f), TextAnchor.MiddleCenter);
            phaseText.rectTransform.anchoredPosition = new Vector2(0f, 90f);

            // 只有 host 才能开局;非 host 等待。客户端无法读 NetworkConnectionToClient,
            // 故用"本机是否为服务器"或 room 侧 host 判定。
            bool isHost = NetworkServer.active || room == null || room.IsHostConnection(null);
            if (room != null && room.phase == RoomPhase.Idle && isHost)
                AddButton(root, "开始对局 (Host)", new Vector2(0f, 0f), StartMatch);

            AddButton(root, "断开连接", new Vector2(0f, -100f), Disconnect);
            AddButton(root, "退出至大厅", new Vector2(0f, -180f), ExitToLobby);
            AddButton(root, "退出游戏", new Vector2(0f, -260f), Quit);

            statusText = NewText("Status", root, status, 20, new Color(1f, 0.85f, 0.4f), TextAnchor.MiddleCenter);
            statusText.rectTransform.anchoredPosition = new Vector2(0f, -330f);
        }

        private void StartMatch()
        {
            var room = NetworkRoomController.Instance;
            if (room == null) { status = "房间控制器缺失。"; RefreshStatus(); return; }

            // 客户端请求 host 开局(服务器权威);离线/服务器直呼。
            if (NetworkServer.active)
            {
                status = room.StartMatchLoad() ? "正在加载地图…" : "无法开局(相位不允许)。";
            }
            else
            {
                status = "已请求 host 开局。";
            }
            RefreshStatus();
        }

        private static string PhaseLabel(RoomPhase p)
        {
            switch (p)
            {
                case RoomPhase.Idle: return "空闲 —— 等待 host 开始对局";
                case RoomPhase.Ready: return "准备 —— 正在加载地图/分配小队";
                default: return "对局中 —— 选择部署点入场";
            }
        }

        // ---------------------------------------------------------------
        // DISCONNECT / EXIT
        // ---------------------------------------------------------------

        private void Disconnect()
        {
            LobbyConnection.Instance?.Disconnect();
            status = "已断开连接。";
            ShowPage(Page.Matchmaking);
        }

        private void ExitToLobby()
        {
            LobbyConnection.Instance?.ReturnToLobby(lobbySceneName);
            status = "";
            ShowPage(Page.Matchmaking);
        }

        private void Quit()
        {
            if (LobbyConnection.Instance != null) LobbyConnection.Instance.QuitGame();
            else
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
        }

        // ---------------------------------------------------------------
        // UI HELPERS (与 DeployScreen 同风格)
        // ---------------------------------------------------------------

        private Text statusText;
        private Text phaseText;

        private void RefreshStatus()
        {
            if (statusText != null) statusText.text = status;
        }

        private void BuildCanvas()
        {
            var go = new GameObject("LobbyCanvas");
            go.transform.SetParent(transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();
            pageRoot = go.GetComponent<RectTransform>();

            var bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(pageRoot, false);
            var r = (RectTransform)bg.transform;
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.04f, 0.05f, 0.07f, 1f);
            bg.GetComponent<Image>().raycastTarget = false;

            if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                var es = new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
                es.transform.SetParent(transform, false);
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private RectTransform NewPanel(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
            builtPages.Add(go);
            return r;
        }

        private Button AddButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = pos;
            r.sizeDelta = new Vector2(360f, 60f);

            var img = go.GetComponent<Image>();
            img.color = new Color(0.18f, 0.24f, 0.34f, 0.95f);

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);

            var t = NewText("Label", r, label, 24, Color.white, TextAnchor.MiddleCenter);
            t.rectTransform.anchorMin = Vector2.zero;
            t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = Vector2.zero;
            t.rectTransform.offsetMax = Vector2.zero;
            return btn;
        }

        private Text NewText(string name, Transform parent, string text, int size, Color color, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = text;
            t.font = cjk;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }
    }
}
