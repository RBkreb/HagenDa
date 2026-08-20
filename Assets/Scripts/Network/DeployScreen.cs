using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace HagenDa.Networking
{
    /// <summary>
    /// Deploy screen (PHASE8, client-side). A full-screen overlay shown while the
    /// local player is <see cref="NetworkPlayerHealth.awaitingInitialDeploy"/> (首次)
    /// or <see cref="NetworkPlayerHealth.awaitingRedeploy"/> (重新部署).
    ///
    /// Contains:
    ///   - a big map (RawImage fed by <see cref="GameHud.BigMapTexture"/>); clicking
    ///     it selects a deploy point (GR / owned HQ / alive squad member),
    ///   - a bottom loadout bar with 5 slots (主武器|可选1|可选2|特有|投掷物); clicking
    ///     a slot pops up the category's items to pick from,
    ///   - a deploy button (or space) that sends the choice + loadout to the server.
    ///
    /// While visible it hides the player's world camera and unlocks the cursor.
    /// </summary>
    public class DeployScreen : MonoBehaviour
    {
        public static DeployScreen Instance { get; private set; }

        public bool IsVisible { get; private set; }

        private NetworkPlayerHealth health;
        private NetworkEquipment equipment;
        private NetworkCombatant combatant;
        private Camera playerCamera;

        private Canvas canvas;
        private RawImage bigMapImage;
        private RectTransform selectionBox;   // 中空圆环跟随框（持续锁定所选部署点）
        private Text hintText;

        private int deployChoice = 1;   // 1=GR / 2=HQ / 3=squad
        private Transform selectedTarget;   // 所选部署点对象（GR/HQ/小队成员），成员移动时跟随
        private Vector3 selectedWorldPos;   // 备用：点击处地面坐标
        private LoadoutDefinition loadout = new LoadoutDefinition();

        private readonly List<Button> slotButtons = new List<Button>();
        private readonly List<Text> slotLabels = new List<Text>();

        private GameObject popup;
        private Transform popupList;
        private int popupSlot = -1;

        private Font cjk;

        // ---------------------------------------------------------------
        // LIFECYCLE
        // ---------------------------------------------------------------
        private void Awake()
        {
            Instance = this;
            cjk = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16);
            BuildUi();
            canvas.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!NetworkClient.active || NetworkClient.localPlayer == null)
            {
                SetVisible(false);
                return;
            }

            if (health == null || equipment == null)
                CacheRefs();

            // Lazy-bind the big-map render texture once the GameHud has created it
            // (GameHud.OnStartLocalPlayer runs after our Awake).
            if (bigMapImage != null && bigMapImage.texture == null &&
                GameHud.Instance != null && GameHud.Instance.BigMapTexture != null)
            {
                bigMapImage.texture = GameHud.Instance.BigMapTexture;
            }

            bool deploying = health != null &&
                (health.awaitingInitialDeploy || health.awaitingRedeploy);

            SetVisible(deploying);

            if (deploying)
            {
                RefreshSlots();
                UpdateSelectionBox();   // 每帧跟随所选部署点（小队成员可能移动）

                // 空格键 = 部署确认（与部署按钮等效）。
                var k = UnityEngine.InputSystem.Keyboard.current;
                if (k != null && k.spaceKey.wasPressedThisFrame)
                    OnDeployClick();
            }
        }

        private void CacheRefs()
        {
            var local = NetworkClient.localPlayer;
            if (local == null) return;

            health = local.GetComponent<NetworkPlayerHealth>();
            equipment = local.GetComponent<NetworkEquipment>();
            combatant = local.GetComponent<NetworkCombatant>();

            var controller = local.GetComponent<NetworkPlayerController>();
            if (controller != null) playerCamera = controller.playerCamera;
        }

        private void SetVisible(bool visible)
        {
            if (IsVisible == visible) return;
            IsVisible = visible;

            if (canvas != null) canvas.gameObject.SetActive(visible);

            if (visible)
            {
                if (playerCamera != null) playerCamera.enabled = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                RefreshFromServerLoadout();
                RefreshSlots();
            }
            else
            {
                if (playerCamera != null) playerCamera.enabled = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                ClosePopup();
            }
        }

        // ---------------------------------------------------------------
        // UI CONSTRUCTION
        // ---------------------------------------------------------------
        private void BuildUi()
        {
            var go = new GameObject("DeployScreenCanvas");
            go.transform.SetParent(transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();

            var root = go.GetComponent<RectTransform>();

            // Full-screen opaque background: hides the 3D world while deploying.
            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(root, false);
            var bgRect = (RectTransform)bg.transform;
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.05f, 0.06f, 0.08f, 0.96f);

            // Big map (centre-top, leaves room for the loadout bar below).
            // Map is 100 wide x 200 long → 0.5 aspect, matching the BigMap render texture.
            var mapGo = new GameObject("BigMap", typeof(RectTransform), typeof(RawImage));
            mapGo.transform.SetParent(root, false);
            var mapRect = (RectTransform)mapGo.transform;
            mapRect.anchorMin = new Vector2(0.5f, 0.5f);
            mapRect.anchorMax = new Vector2(0.5f, 0.5f);
            mapRect.pivot = new Vector2(0.5f, 0.5f);
            mapRect.anchoredPosition = new Vector2(0f, 40f);
            mapRect.sizeDelta = new Vector2(480f, 960f);
            bigMapImage = mapGo.GetComponent<RawImage>();
            bigMapImage.color = Color.white;   // 部署界面大地图不透明

            // Click handler for the big map.
            var click = mapGo.AddComponent<Button>();
            click.targetGraphic = bigMapImage;
            click.onClick.AddListener(OnBigMapClick);

            // Selection lock ring: a hollow ring that follows the chosen deploy point
            // every frame (squad members may move), so it tracks their current map pos.
            var boxGo = new GameObject("SelectionBox", typeof(RectTransform), typeof(Image));
            boxGo.transform.SetParent(mapGo.transform, false);
            var boxRect = (RectTransform)boxGo.transform;
            boxRect.anchorMin = new Vector2(0.5f, 0.5f);
            boxRect.anchorMax = new Vector2(0.5f, 0.5f);
            boxRect.pivot = new Vector2(0.5f, 0.5f);
            boxRect.sizeDelta = new Vector2(44f, 44f);
            selectionBox = boxRect;
            var boxImg = boxGo.GetComponent<Image>();
            boxImg.sprite = MakeRingSprite();   // 中空圆环
            boxImg.color = new Color(0.2f, 1f, 0.3f, 0.95f);   // 亮绿圆环
            selectionBox.gameObject.SetActive(false);

            // Loadout bar at the bottom.
            var bar = new GameObject("LoadoutBar", typeof(RectTransform), typeof(Image));
            bar.transform.SetParent(root, false);
            var barRect = (RectTransform)bar.transform;
            barRect.anchorMin = new Vector2(0.5f, 0f);
            barRect.anchorMax = new Vector2(0.5f, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.anchoredPosition = new Vector2(0f, 12f);
            barRect.sizeDelta = new Vector2(1400f, 90f);
            var barImg = bar.GetComponent<Image>();
            barImg.color = new Color(0f, 0f, 0f, 0.5f);

            BuildSlotButtons(barRect);

            // Deploy button.
            var deployBtnGo = new GameObject("DeployButton", typeof(RectTransform), typeof(Image), typeof(Button));
            deployBtnGo.transform.SetParent(root, false);
            var btnRect = (RectTransform)deployBtnGo.transform;
            btnRect.anchorMin = new Vector2(0.5f, 0f);
            btnRect.anchorMax = new Vector2(0.5f, 0f);
            btnRect.pivot = new Vector2(0.5f, 0f);
            btnRect.anchoredPosition = new Vector2(0f, 116f);
            btnRect.sizeDelta = new Vector2(220f, 48f);
            var btnImg = deployBtnGo.GetComponent<Image>();
            btnImg.color = new Color(0.2f, 0.5f, 0.25f, 0.9f);
            var btn = deployBtnGo.GetComponent<Button>();
            btn.targetGraphic = btnImg;
            btn.onClick.AddListener(OnDeployClick);
            var btnText = NewText("Label", btnRect, "部署 [空格]", 22, Color.white, TextAnchor.MiddleCenter);

            // Hint.
            hintText = NewText("Hint", root, "点击大地图选择部署点", 20, new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleCenter);
            var hr = hintText.rectTransform;
            hr.anchorMin = new Vector2(0.5f, 1f);
            hr.anchorMax = new Vector2(0.5f, 1f);
            hr.pivot = new Vector2(0.5f, 1f);
            hr.anchoredPosition = new Vector2(0f, -16f);
            hr.sizeDelta = new Vector2(800f, 30f);

            // Popup list (hidden until a slot is clicked).
            var popGo = new GameObject("Popup", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(Image));
            popGo.transform.SetParent(root, false);
            var popRect = (RectTransform)popGo.transform;
            popRect.anchorMin = new Vector2(0.5f, 0f);
            popRect.anchorMax = new Vector2(0.5f, 0f);
            popRect.pivot = new Vector2(0.5f, 0f);
            popRect.anchoredPosition = new Vector2(0f, 112f);
            popRect.sizeDelta = new Vector2(360f, 320f);
            var popImg = popGo.GetComponent<Image>();
            popImg.color = new Color(0f, 0f, 0f, 0.75f);
            popup = popGo;
            popupList = popGo.transform;
            popup.SetActive(false);
        }

        private void BuildSlotButtons(RectTransform bar)
        {
            string[] names = { "主武器", "可选配备1", "可选配备2", "特有配备", "通用投掷物" };
            float w = 260f;
            float gap = 16f;
            float total = w * names.Length + gap * (names.Length - 1);
            float startX = -total * 0.5f + w * 0.5f;

            for (int i = 0; i < names.Length; i++)
            {
                var bGo = new GameObject("Slot_" + names[i], typeof(RectTransform), typeof(Image), typeof(Button));
                bGo.transform.SetParent(bar, false);
                var r = (RectTransform)bGo.transform;
                r.anchorMin = new Vector2(0.5f, 0.5f);
                r.anchorMax = new Vector2(0.5f, 0.5f);
                r.pivot = new Vector2(0.5f, 0.5f);
                r.anchoredPosition = new Vector2(startX + i * (w + gap), 0f);
                r.sizeDelta = new Vector2(w, 72f);

                var img = bGo.GetComponent<Image>();
                img.color = new Color(0.15f, 0.16f, 0.2f, 0.9f);

                int slot = i;
                var btn = bGo.GetComponent<Button>();
                btn.targetGraphic = img;
                btn.onClick.AddListener(() => OnSlotClick(slot));

                var label = NewText("Label", r, names[i], 20, Color.white, TextAnchor.MiddleCenter);
                label.rectTransform.anchorMin = Vector2.zero;
                label.rectTransform.anchorMax = Vector2.one;
                label.rectTransform.offsetMin = new Vector2(4f, 2f);
                label.rectTransform.offsetMax = new Vector2(-4f, -2f);

                slotButtons.Add(btn);
                slotLabels.Add(label);
            }
        }

        // ---------------------------------------------------------------
        // CLICK HANDLERS
        // ---------------------------------------------------------------
        private void OnBigMapClick()
        {
            // Convert the pointer position to a world position on the ground plane.
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;
            Vector2 screenPos = mouse.position.ReadValue();

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    (RectTransform)bigMapImage.transform,
                    screenPos, null, out local))
                return;

            var rect = ((RectTransform)bigMapImage.transform).rect;
            Vector2 uv = new Vector2(
                Mathf.InverseLerp(rect.xMin, rect.xMax, local.x),
                Mathf.InverseLerp(rect.yMin, rect.yMax, local.y));

            var cam = GameHud.Instance != null ? GameHud.Instance.BigMapCamera : null;
            if (cam == null) return;

            Vector3? world = ScreenToGround(cam, uv);
            if (!world.HasValue) return;

            deployChoice = ClassifyDeployPoint(world.Value);
            selectedWorldPos = world.Value;
            selectedTarget = ResolveSelectedTarget(deployChoice, world.Value);
            UpdateSelectionBox();

            hintText.text = deployChoice == 1 ? "部署点：驻地 (GR)"
                : deployChoice == 2 ? "部署点：据点 (HQ)"
                : "部署点：小队成员";
        }

        /// <summary>
        /// 解析所选部署点的实际对象：
        ///   3=最近存活同小队成员（移动时跟随）、2=己方 HQ、1=己方 GR。
        /// </summary>
        private Transform ResolveSelectedTarget(int choice, Vector3 world)
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null) return null;

            int team = combatant != null ? combatant.teamId : (int)MatchTeam.Red;
            int squad = combatant != null ? combatant.squadId : -1;
            var self = combatant;

            if (choice == 3 && NetworkClient.localPlayer != null)
            {
                NetworkCombatant best = null;
                float bestDist = float.MaxValue;
                foreach (var c in Object.FindObjectsOfType<NetworkCombatant>())
                {
                    if (c == null || c == self) continue;
                    if (c.teamId != team || c.squadId != squad) continue;
                    if (c.IsDead) continue;
                    float d = Vector3.Distance(world, c.transform.position);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
                return best != null ? best.transform : null;
            }

            if (choice == 2 && mm.capturePoints != null)
            {
                CapturePoint best = null;
                float bestDist = float.MaxValue;
                foreach (var cp in mm.capturePoints)
                {
                    if (cp == null || cp.ownerTeam != team) continue;
                    float d = Vector3.Distance(world, cp.transform.position);
                    if (d < bestDist) { bestDist = d; best = cp; }
                }
                return best != null ? best.transform : null;
            }

            if (choice == 1 && mm.garrisons != null)
            {
                GarrisonZone best = null;
                float bestDist = float.MaxValue;
                foreach (var g in mm.garrisons)
                {
                    if (g == null || g.teamId != team) continue;
                    float d = Vector3.Distance(world, g.transform.position);
                    if (d < bestDist) { bestDist = d; best = g; }
                }
                return best != null ? best.transform : null;
            }

            return null;
        }

        /// <summary>
        /// 每帧跟随所选部署点：优先跟随对象（GR/HQ/小队成员实时位置），
        /// 否则用点击处世界坐标。转换为大地图 RawImage 的 local 坐标放置圆环。
        /// </summary>
        private void UpdateSelectionBox()
        {
            if (selectionBox == null) return;

            var cam = GameHud.Instance != null ? GameHud.Instance.BigMapCamera : null;
            if (cam == null) return;

            Vector3 worldPos = selectedTarget != null
                ? selectedTarget.position
                : selectedWorldPos;

            // 世界 → 视口 → 大地图 local 空间。
            Vector3 viewport = cam.WorldToViewportPoint(worldPos);
            if (viewport.z <= 0f) { selectionBox.gameObject.SetActive(false); return; }

            var mapRect = (RectTransform)bigMapImage.transform;
            Vector2 local = new Vector2(
                Mathf.Lerp(mapRect.rect.xMin, mapRect.rect.xMax, viewport.x),
                Mathf.Lerp(mapRect.rect.yMin, mapRect.rect.yMax, viewport.y));

            selectionBox.anchoredPosition = local;
            selectionBox.gameObject.SetActive(true);
        }

        private int ClassifyDeployPoint(Vector3 world)
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null) return 1;

            int team = combatant != null ? combatant.teamId : (int)MatchTeam.Red;
            int squad = combatant != null ? combatant.squadId : -1;

            // Squad member (alive) within 6m → squad deploy. Bigger radius so the
            // small entity dots on the map are easy to click.
            var local = NetworkClient.localPlayer;
            var self = combatant;
            if (local != null)
            {
                foreach (var c in Object.FindObjectsOfType<NetworkCombatant>())
                {
                    if (c == null || c == self) continue;
                    if (c.teamId != team || c.squadId != squad) continue;
                    if (c.IsDead) continue;
                    if (Vector3.Distance(world, c.transform.position) <= 6f)
                        return 3;
                }
            }

            // Owned HQ within its radius → HQ deploy.
            if (mm.capturePoints != null)
            {
                foreach (var cp in mm.capturePoints)
                {
                    if (cp == null || cp.ownerTeam != team) continue;
                    if (Vector3.Distance(world, cp.transform.position) <= cp.radius)
                        return 2;
                }
            }

            // Own garrison within radius → GR deploy.
            if (mm.garrisons != null)
            {
                foreach (var g in mm.garrisons)
                {
                    if (g == null || g.teamId != team) continue;
                    if (Vector3.Distance(world, g.transform.position) <= g.radius)
                        return 1;
                }
            }

            return 1;   // default GR
        }

        private void OnSlotClick(int slot)
        {
            if (slot == 0) return;   // 主武器：当前仅 M4，不可选
            if (equipment == null) return;

            OpenPopup(slot);
        }

        private void OpenPopup(int slot)
        {
            ClosePopup();

            popupSlot = slot;
            EquipmentCategory cat = SlotToCategory(slot);

            foreach (var def in equipment.equipmentList)
            {
                if (def == null || def.category != cat) continue;
                int idx = equipment.equipmentList.IndexOf(def);

                var itemGo = new GameObject("Item_" + def.displayName, typeof(RectTransform), typeof(Image), typeof(Button));
                itemGo.transform.SetParent(popupList, false);
                var img = itemGo.GetComponent<Image>();
                img.color = new Color(0.2f, 0.22f, 0.28f, 0.95f);
                var btn = itemGo.GetComponent<Button>();
                btn.targetGraphic = img;
                int capturedIdx = idx;
                btn.onClick.AddListener(() => OnPickItem(capturedIdx));

                var label = NewText("Label", (RectTransform)itemGo.transform, def.displayName, 18, Color.white, TextAnchor.MiddleLeft);
                label.rectTransform.anchorMin = Vector2.zero;
                label.rectTransform.anchorMax = Vector2.one;
                label.rectTransform.offsetMin = new Vector2(8f, 0f);
                label.rectTransform.offsetMax = new Vector2(-8f, 0f);
            }

            popup.SetActive(true);
        }

        private void OnPickItem(int idx)
        {
            int slot = popupSlot;
            switch (slot)
            {
                case 1: loadout.optional1 = idx; break;
                case 2: loadout.optional2 = idx; break;
                case 3: loadout.special = idx; break;
                case 4: loadout.throwable = idx; break;
            }
            ClosePopup();
            RefreshSlots();
        }

        private void ClosePopup()
        {
            if (popup == null) return;
            popup.SetActive(false);
            for (int i = popupList.childCount - 1; i >= 0; i--)
                Destroy(popupList.GetChild(i).gameObject);
            popupSlot = -1;
        }

        private void OnDeployClick()
        {
            if (health == null) return;

            // Ensure optional slots differ (avoid accidental dup via popup).
            if (loadout.optional1 == loadout.optional2)
            {
                hintText.text = "可选配备1 与可选配备2 不能相同";
                return;
            }

            health.CmdDeploy(deployChoice, loadout);
        }

        // ---------------------------------------------------------------
        // REFRESH
        // ---------------------------------------------------------------
        private void RefreshFromServerLoadout()
        {
            if (equipment == null) return;
            loadout.optional1 = equipment.opt1Index;
            loadout.optional2 = equipment.opt2Index;
            loadout.special = equipment.specialIndex;
            loadout.throwable = equipment.throwableIndex;
        }

        private void RefreshSlots()
        {
            if (equipment == null || slotLabels.Count < 5) return;

            slotLabels[0].text = "主武器: M4";
            slotLabels[1].text = "可选1: " + NameOf(loadout.optional1);
            slotLabels[2].text = "可选2: " + NameOf(loadout.optional2);
            slotLabels[3].text = "特有: " + NameOf(loadout.special);
            slotLabels[4].text = "投掷物: " + NameOf(loadout.throwable);
        }

        private string NameOf(int idx)
        {
            if (idx < 0 || equipment == null || idx >= equipment.Count)
                return "—";
            var def = equipment.equipmentList[idx];
            return def != null ? def.displayName : "—";
        }

        private EquipmentCategory SlotToCategory(int slot)
        {
            switch (slot)
            {
                case 3: return EquipmentCategory.Special;
                case 4: return EquipmentCategory.Throwable;
                default: return EquipmentCategory.Optional;
            }
        }

        // ---------------------------------------------------------------
        // HELPERS
        // ---------------------------------------------------------------
        private Vector3? ScreenToGround(Camera cam, Vector2 uv)
        {
            var ray = cam.ViewportPointToRay(new Vector3(uv.x, uv.y, 0f));
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out float enter))
                return ray.GetPoint(enter);
            return null;
        }

        private Text NewText(string name, Transform parent, string text, int fontSize, Color color, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = text;
            t.font = cjk;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            return t;
        }

        private static Sprite MakeSolid(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        /// <summary>生成中空圆环 sprite（白色圆环，内部透明）。</summary>
        private static Sprite MakeRingSprite()
        {
            int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = new Vector2(size * 0.5f, size * 0.5f);
            float outer = size * 0.5f;        // 外半径
            float inner = size * 0.38f;       // 内半径（空心）

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center);
                    tex.SetPixel(x, y, (d <= outer && d >= inner) ? Color.white : Color.clear);
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }
    }
}
