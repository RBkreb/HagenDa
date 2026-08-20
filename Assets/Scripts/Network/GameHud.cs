using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace HagenDa.Networking
{
    public enum HitKind { Normal, Headshot, Kill }

    /// <summary>
    /// Client-side uGUI HUD (PHASE8). Replaces the IMGUI <see cref="PlayerHud"/> and
    /// <see cref="DebugHud"/> with a single Screen Space - Overlay canvas, built
    /// programmatically at runtime.
    ///
    /// Layout (all percentages / anchors, no pixel hacks):
    ///   - minimap: circular masked RawImage bottom-left (rotating orthographic cam),
    ///   - health/armor: hollow white bar bottom-center (red <25, blue armor number),
    ///   - ammo/equipment: bottom-right (mag/reserve + fire mode; equipment count),
    ///   - score + HQ letters: top-center,
    ///   - crosshair (spread widens gap), hitmarker (white/head-yellow/kill-red),
    ///   - damage indicator arc (points to the damage source, 100 dmg = 100°).
    ///
    /// Feedback (hitmarker / damage indicator) is server-driven via the local
    /// player's NetworkBehaviours calling the singleton methods below.
    /// </summary>
    public class GameHud : NetworkBehaviour
    {
        public static GameHud Instance { get; private set; }

        [Header("References")]
        public NetworkPlayerHealth health;
        public NetworkPlayerController controller;
        public NetworkGun gun;
        public NetworkEquipment equipment;
        public NetworkCombatant combatant;

        // --- canvas roots ---
        private Canvas canvas;
        private RectTransform root;

        // --- minimap ---
        private RawImage minimapRaw;
        private MinimapCamera minimapCam;
        private RenderTexture minimapRT;
        private MinimapCamera bigMapCam;
        private RenderTexture bigMapRT;

        // --- big map overlay (M key) ---
        private RawImage bigMapOverlay;
        private RectTransform bigMapOverlayRect;
        private bool bigMapToggled;

        /// <summary>Big-map render texture, consumed by <see cref="DeployScreen"/>.</summary>
        public RenderTexture BigMapTexture => bigMapRT;

        /// <summary>Big-map orthographic camera (for click → world conversion).</summary>
        public Camera BigMapCamera => bigMapCam != null ? bigMapCam.GetComponent<Camera>() : null;

        // --- health / armor ---
        private Image healthFill;
        private Text armorText;

        // --- ammo / equipment ---
        private Text ammoText;
        private Text modeText;
        private Text equipmentText;
        private Text otherEquipText;

        // --- score / HQ ---
        private Text scoreText;
        private Text hqText;

        // --- crosshair / hitmarker ---
        private RectTransform[] crossLines = new RectTransform[4];
        private RectTransform[] hitLines = new RectTransform[4];
        private Image[] hitLineImgs = new Image[4];

        // --- hitmarker state ---
        private int hitmarkerEndFrame = -1;
        private HitKind hitmarkerKind;

        private Font cjk;

        // Canvas reference resolution (CanvasScaler ScaleWithScreenSize). All UI math
        // must use these, NOT Screen.width/height, so layout scales proportionally on
        // any window size.
        private const float RefW = 1920f;
        private const float RefH = 1080f;

        // ---------------------------------------------------------------
        // LIFECYCLE
        // ---------------------------------------------------------------
        public override void OnStartLocalPlayer()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            CacheRefs();
            BuildUi();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ReleaseRenderTextures();
        }

        private void CacheRefs()
        {
            if (health == null) health = GetComponent<NetworkPlayerHealth>();
            if (controller == null) controller = GetComponent<NetworkPlayerController>();
            if (gun == null) gun = GetComponent<NetworkGun>();
            if (equipment == null) equipment = GetComponent<NetworkEquipment>();
            if (combatant == null) combatant = GetComponent<NetworkCombatant>();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            UpdateBigMapToggle();
            UpdateMapMode();
            UpdateMinimapCameras();
            UpdateHealth();
            UpdateAmmoEquipment();
            UpdateScoreHq();
            UpdateCrosshair();
            UpdateHitmarker();
        }

        // ---------------------------------------------------------------
        // UI CONSTRUCTION
        // ---------------------------------------------------------------
        private Font Cjk()
        {
            if (cjk == null)
                cjk = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16);
            return cjk;
        }

        private void BuildUi()
        {
            var canvasGo = new GameObject("GameHudCanvas");
            canvasGo.transform.SetParent(transform, false);
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            root = canvasGo.GetComponent<RectTransform>();

            BuildMinimap();
            BuildBigMapPanel();
            BuildHealth();
            BuildAmmoEquipment();
            BuildScoreHq();
            BuildCrosshair();
            BuildHitmarker();
        }

        // ----- minimap (circular, bottom-left) -----
        private void BuildMinimap()
        {
            // 画幅放大一倍：256 → 512（显示尺寸与渲染分辨率同时翻倍）。
            int size = 512;

            minimapRT = new RenderTexture(size, size, 16);
            minimapRT.name = "MinimapRT";
            minimapRT.Create();

            bigMapRT = new RenderTexture(512, 1024, 16);   // 100x200 地图 → 0.5 宽高比
            bigMapRT.name = "BigMapRT";
            bigMapRT.Create();

            var holder = NewRect("Minimap", root);
            holder.anchorMin = new Vector2(0f, 0f);
            holder.anchorMax = new Vector2(0f, 0f);
            holder.pivot = new Vector2(0f, 0f);
            holder.anchoredPosition = new Vector2(20f, 20f);
            holder.sizeDelta = new Vector2(size, size);

            // Circular mask (Image with a generated circle sprite + Mask).
            var maskGo = new GameObject("Mask", typeof(RectTransform), typeof(Image), typeof(Mask));
            maskGo.transform.SetParent(holder, false);
            var maskRect = (RectTransform)maskGo.transform;
            maskRect.anchorMin = Vector2.zero;
            maskRect.anchorMax = Vector2.one;
            maskRect.offsetMin = Vector2.zero;
            maskRect.offsetMax = Vector2.zero;
            var maskImg = maskGo.GetComponent<Image>();
            maskImg.sprite = MakeCircleSprite();
            // Mask 组件用 Image 的 alpha 写模板。必须保持不透明（否则模板为空，
            // 子物体被完全裁剪），再用 showMaskGraphic=false 隐藏图形本身。
            maskImg.color = Color.white;
            var mask = maskGo.GetComponent<Mask>();
            mask.showMaskGraphic = false;

            var rawGo = new GameObject("Raw", typeof(RectTransform), typeof(RawImage));
            rawGo.transform.SetParent(maskGo.transform, false);
            var rawRect = (RectTransform)rawGo.transform;
            rawRect.anchorMin = Vector2.zero;
            rawRect.anchorMax = Vector2.one;
            rawRect.offsetMin = Vector2.zero;
            rawRect.offsetMax = Vector2.zero;
            minimapRaw = rawGo.GetComponent<RawImage>();
            minimapRaw.texture = minimapRT;
            minimapRaw.color = new Color(1f, 1f, 1f, 0.5f);   // 小地图 50% 透明度

            // Player direction arrow at centre (up = forward).
            var arrow = NewImage("Arrow", holder, MakeSolid(1f, 1f, 1f, 1f));
            arrow.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            arrow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            arrow.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            arrow.rectTransform.sizeDelta = new Vector2(6f, 16f);

            // Create the two orthographic map cameras (children of the local player).
            minimapCam = CreateMapCamera("MinimapCamera", MinimapCamera.Mode.Minimap, minimapRT);
            bigMapCam = CreateMapCamera("BigMapCamera", MinimapCamera.Mode.BigMap, bigMapRT);
        }

        private MinimapCamera CreateMapCamera(string name, MinimapCamera.Mode mode, RenderTexture rt)
        {
            var go = new GameObject(name, typeof(Camera), typeof(MinimapCamera));
            go.transform.SetParent(transform, false);
            var cam = go.GetComponent<Camera>();
            cam.targetTexture = rt;
            var mc = go.GetComponent<MinimapCamera>();
            mc.mode = mode;
            return mc;
        }

        // ----- big map overlay (toggled with M) -----
        private void BuildBigMapPanel()
        {
            var go = new GameObject("BigMapOverlay", typeof(RectTransform), typeof(RawImage));
            go.transform.SetParent(root, false);
            bigMapOverlayRect = (RectTransform)go.transform;
            bigMapOverlayRect.anchorMin = new Vector2(0.5f, 0.5f);
            bigMapOverlayRect.anchorMax = new Vector2(0.5f, 0.5f);
            bigMapOverlayRect.pivot = new Vector2(0.5f, 0.5f);
            bigMapOverlayRect.anchoredPosition = new Vector2(0f, 0f);
            bigMapOverlayRect.sizeDelta = new Vector2(540f, 1080f);   // 0.5 宽高比

            bigMapOverlay = go.GetComponent<RawImage>();
            bigMapOverlay.texture = bigMapRT;
            bigMapOverlay.color = new Color(1f, 1f, 1f, 0.5f);   // 大地图 50% 透明度
            bigMapOverlay.gameObject.SetActive(false);
        }

        private void UpdateBigMapToggle()
        {
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return;

            if (k.mKey.wasPressedThisFrame)
            {
                bigMapToggled = !bigMapToggled;

                // 部署界面打开时 M 不干扰。
                bool deploying = DeployScreen.Instance != null && DeployScreen.Instance.IsVisible;
                if (!deploying && bigMapOverlay != null)
                    bigMapOverlay.gameObject.SetActive(bigMapToggled);
            }
        }

        private void UpdateMapMode()
        {
            // 部署界面或 M 键大地图打开时 → 大地图视角；否则小地图视角。
            bool deploying = DeployScreen.Instance != null && DeployScreen.Instance.IsVisible;
            MapIndicator.Mode = (deploying || bigMapToggled)
                ? MapIndicator.ViewMode.BigMap
                : MapIndicator.ViewMode.Minimap;
        }

        // ----- health / armor (bottom-center) -----
        private void BuildHealth()
        {
            var bar = NewImage("HealthBar", root, MakeSolid(0f, 0f, 0f, 0.5f));
            bar.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            bar.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            bar.rectTransform.pivot = new Vector2(0.5f, 0f);
            bar.rectTransform.anchoredPosition = new Vector2(0f, 22f);
            bar.rectTransform.sizeDelta = new Vector2(576f, 26f);

            healthFill = NewImage("HealthFill", bar.transform, MakeSolid(1f, 1f, 1f, 0.7f));
            healthFill.rectTransform.anchorMin = new Vector2(0f, 0f);
            healthFill.rectTransform.anchorMax = new Vector2(1f, 1f);
            healthFill.rectTransform.offsetMin = new Vector2(2f, 2f);
            healthFill.rectTransform.offsetMax = new Vector2(-2f, -2f);

            armorText = NewText("Armor", root, "", 20, new Color(0.3f, 0.6f, 1f, 1f), TextAnchor.MiddleLeft);
            var rt = armorText.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(296f, 35f);
            rt.sizeDelta = new Vector2(80f, 30f);
        }

        // ----- ammo / equipment (bottom-right) -----
        private void BuildAmmoEquipment()
        {
            ammoText = NewText("Ammo", root, "", 30, Color.white, TextAnchor.MiddleRight);
            var a = ammoText.rectTransform;
            a.anchorMin = new Vector2(1f, 0f);
            a.anchorMax = new Vector2(1f, 0f);
            a.pivot = new Vector2(1f, 0f);
            a.anchoredPosition = new Vector2(-20f, 30f);
            a.sizeDelta = new Vector2(320f, 40f);

            modeText = NewText("Mode", root, "", 20, Color.white, TextAnchor.MiddleRight);
            var m = modeText.rectTransform;
            m.anchorMin = new Vector2(1f, 0f);
            m.anchorMax = new Vector2(1f, 0f);
            m.pivot = new Vector2(1f, 0f);
            m.anchoredPosition = new Vector2(-20f, 70f);
            m.sizeDelta = new Vector2(320f, 30f);

            equipmentText = NewText("Equipment", root, "", 20, Color.white, TextAnchor.MiddleRight);
            var e = equipmentText.rectTransform;
            e.anchorMin = new Vector2(1f, 0f);
            e.anchorMax = new Vector2(1f, 0f);
            e.pivot = new Vector2(1f, 0f);
            e.anchoredPosition = new Vector2(-20f, 100f);
            e.sizeDelta = new Vector2(320f, 30f);

            // PHASE8: 其他装备小字逐行罗列（另一个框型）。
            otherEquipText = NewText("OtherEquipment", root, "", 16,
                new Color(0.85f, 0.85f, 0.9f, 0.9f), TextAnchor.MiddleRight);
            var oe = otherEquipText.rectTransform;
            oe.anchorMin = new Vector2(1f, 0f);
            oe.anchorMax = new Vector2(1f, 0f);
            oe.pivot = new Vector2(1f, 0f);
            oe.anchoredPosition = new Vector2(-20f, 135f);
            oe.sizeDelta = new Vector2(340f, 150f);
        }

        // ----- score + HQ (top-center) -----
        private void BuildScoreHq()
        {
            scoreText = NewText("Score", root, "", 26, Color.white, TextAnchor.MiddleCenter);
            var s = scoreText.rectTransform;
            s.anchorMin = new Vector2(0.5f, 1f);
            s.anchorMax = new Vector2(0.5f, 1f);
            s.pivot = new Vector2(0.5f, 1f);
            s.anchoredPosition = new Vector2(0f, -12f);
            s.sizeDelta = new Vector2(600f, 36f);

            hqText = NewText("HQ", root, "", 24, Color.white, TextAnchor.MiddleCenter);
            var h = hqText.rectTransform;
            h.anchorMin = new Vector2(0.5f, 1f);
            h.anchorMax = new Vector2(0.5f, 1f);
            h.pivot = new Vector2(0.5f, 1f);
            h.anchoredPosition = new Vector2(0f, -48f);
            h.sizeDelta = new Vector2(600f, 30f);
        }

        // ----- crosshair (4 lines, gap widens with bloom) -----
        private void BuildCrosshair()
        {
            for (int i = 0; i < 4; i++)
            {
                var line = NewImage("Cross" + i, root, MakeSolid(1f, 1f, 1f, 0.95f));
                var rt = line.rectTransform;
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(2f, 14f);
                crossLines[i] = rt;
            }
        }

        // ----- hitmarker (4 diagonal lines, X shape) -----
        private void BuildHitmarker()
        {
            for (int i = 0; i < 4; i++)
            {
                var line = NewImage("Hit" + i, root, MakeSolid(1f, 1f, 1f, 1f));
                line.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                line.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                line.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                line.rectTransform.sizeDelta = new Vector2(2f, 18f);
                line.gameObject.SetActive(false);
                hitLines[i] = line.rectTransform;
                hitLineImgs[i] = line;
            }
        }

        // ---------------------------------------------------------------
        // PER-FRAME UPDATES
        // ---------------------------------------------------------------
        private void UpdateMinimapCameras()
        {
            // Orthographic cameras self-position in their LateUpdate. Enable the big
            // map camera while the deploy screen or the M-key big map is visible.
            if (bigMapCam != null && bigMapCam.GetComponent<Camera>() != null)
            {
                bool deploying = DeployScreen.Instance != null && DeployScreen.Instance.IsVisible;
                bigMapCam.GetComponent<Camera>().enabled = deploying || bigMapToggled;
            }
        }

        private void UpdateHealth()
        {
            if (health == null) return;

            float frac = Mathf.Clamp01(health.health / Mathf.Max(0.001f, health.maxHealth));
            healthFill.rectTransform.anchorMax = new Vector2(frac, 1f);
            bool low = health.health < 25f;
            healthFill.color = low ? new Color(0.9f, 0.15f, 0.15f, 0.8f) : new Color(1f, 1f, 1f, 0.7f);

            if (health.armor > 0f)
            {
                armorText.text = Mathf.RoundToInt(health.armor).ToString();
                armorText.enabled = true;
            }
            else
            {
                armorText.enabled = false;
            }
        }

        private void UpdateAmmoEquipment()
        {
            if (controller == null) return;

            if (controller.activeSlot < 0 && gun != null && gun.Definition != null)
            {
                ammoText.text = $"{gun.magAmmo} / {gun.reserveAmmo}";
                if (gun.reloading)
                    ammoText.text += gun.reloadPaused ? " (换弹暂停)" : " (换弹中...)";
                modeText.text = FireModeLabel();
                equipmentText.text = "";
            }
            else if (equipment != null)
            {
                int idx = equipment.GetSlotIndex(controller.activeSlot);
                if (idx >= 0 && idx < equipment.Count)
                {
                    var def = equipment.equipmentList[idx];
                    if (def != null)
                    {
                        ammoText.text = def.displayName;
                        modeText.text = equipment.IsEmpDisabled ? "[EMP]" : "";
                        // 瞬发型（快速机动装置）：显示冷却倒计时而非剩余次数。
                        if (def.useStyle == EquipmentUseStyle.SelfInstant)
                        {
                            equipmentText.text = equipment.dashCooldownRemaining > 0f
                                ? $"冷却 {equipment.dashCooldownRemaining:F0}s"
                                : "就绪";
                        }
                        else
                        {
                            equipmentText.text = $"剩余 {equipment.selectedAmmo}";
                        }
                    }
                }
                else
                {
                    ammoText.text = "";
                    modeText.text = "";
                    equipmentText.text = "";
                }
            }
            else
            {
                ammoText.text = "";
                modeText.text = "";
                equipmentText.text = "";
            }

            UpdateOtherEquipment();
        }

        /// <summary>
        /// PHASE8: 其他装备物品小字逐行罗列（除当前手持槽位外）。
        /// 主武器：弹匣/备弹/射击方式；装备：名称 + 剩余次数。
        /// 装备槽位编号与 NetworkEquipment 一致：0=可选1, 1=可选2, 2=特有, 3=投掷物。
        /// </summary>
        private void UpdateOtherEquipment()
        {
            if (otherEquipText == null) return;
            if (equipment == null || controller == null)
            {
                otherEquipText.text = "";
                return;
            }

            var sb = new System.Text.StringBuilder();
            string[] slotNames = { "可选1", "可选2", "特有", "投掷物" };

            // 主武器行：仅当手持装备时显示（手持主武器时右下角大框已显示弹药）。
            if (controller.activeSlot >= 0 && gun != null && gun.Definition != null)
                sb.AppendLine($"主武器: {gun.magAmmo}/{gun.reserveAmmo}  {FireModeLabel()}");

            for (int slot = 0; slot < 4; slot++)
            {
                if (slot == controller.activeSlot) continue;   // 当前手持物单独显示

                int idx = equipment.GetSlotIndex(slot);
                if (idx >= 0 && idx < equipment.Count)
                {
                    var def = equipment.equipmentList[idx];
                    if (def != null)
                    {
                        // 瞬发型显示冷却/就绪，其余显示剩余次数。
                        if (def.useStyle == EquipmentUseStyle.SelfInstant)
                        {
                            string st = equipment.dashCooldownRemaining > 0f
                                ? $"冷却{equipment.dashCooldownRemaining:F0}s"
                                : "就绪";
                            sb.AppendLine($"{slotNames[slot]}: {def.displayName} {st}");
                        }
                        else
                        {
                            sb.AppendLine($"{slotNames[slot]}: {def.displayName} x{equipment.GetSlotAmmo(slot)}");
                        }
                    }
                }
            }

            otherEquipText.text = sb.ToString();
        }

        private void UpdateScoreHq()
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null)
            {
                scoreText.text = "";
                hqText.text = "";
                return;
            }

            string teamLabel = combatant != null && combatant.teamId == (int)MatchTeam.Blue ? "蓝方" : "红方";
            scoreText.text = $"红方 {mm.redScore}  —  蓝方 {mm.blueScore}  [{teamLabel}]";

            // HQ letters colored by contention.
            hqText.text = "";
            if (mm.capturePoints != null)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < mm.capturePoints.Count; i++)
                {
                    var cp = mm.capturePoints[i];
                    if (cp == null) continue;
                    sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(HudMath.ContentionColor(cp.contention))}>{(char)('A' + i)}</color>");
                    if (i < mm.capturePoints.Count - 1) sb.Append("  ");
                }
                hqText.text = sb.ToString();
            }
        }

        private void UpdateCrosshair()
        {
            float bloom = gun != null ? gun.bloom : 0f;
            float gap = RefH * (0.005f + bloom * 0.004f);
            float len = RefH * 0.014f;
            float thick = 2f;

            // Anchored to screen centre; offsets are in canvas reference units.
            SetAxisLine(crossLines[0], new Vector2(0f, gap + len * 0.5f), new Vector2(thick, len));
            SetAxisLine(crossLines[1], new Vector2(0f, -(gap + len * 0.5f)), new Vector2(thick, len));
            SetAxisLine(crossLines[2], new Vector2(gap + len * 0.5f, 0f), new Vector2(len, thick));
            SetAxisLine(crossLines[3], new Vector2(-(gap + len * 0.5f), 0f), new Vector2(len, thick));
        }

        private static void SetAxisLine(RectTransform rt, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = sizeDelta;
            rt.localRotation = Quaternion.identity;
        }

        private void UpdateHitmarker()
        {
            if (Time.frameCount >= hitmarkerEndFrame)
            {
                foreach (var l in hitLines) l.gameObject.SetActive(false);
                return;
            }

            Color c;
            switch (hitmarkerKind)
            {
                case HitKind.Headshot: c = new Color(1f, 0.85f, 0.1f); break;
                case HitKind.Kill: c = new Color(1f, 0.2f, 0.2f); break;
                default: c = Color.white; break;
            }
            foreach (var img in hitLineImgs) img.color = c;

            float gap = RefH * 0.008f;
            float len = RefH * 0.018f;
            float thick = 2f;
            float k = 0.70710678f;   // 1/sqrt(2)
            float dist = gap + len * 0.5f;

            // 4 diagonal spokes (×), combined with the crosshair (+) → 米.
            // Each line's long axis must point along its diagonal (radial, toward the
            // centre), NOT tangential: a vertical line rotated by (angle + 90°) points
            // along the diagonal instead of forming a diamond edge.
            Vector2[] dirs =
            {
                new Vector2(k, k), new Vector2(k, -k), new Vector2(-k, -k), new Vector2(-k, k)
            };
            for (int i = 0; i < 4; i++)
            {
                var rt = hitLines[i];
                rt.gameObject.SetActive(true);
                rt.anchoredPosition = dirs[i] * dist;
                rt.sizeDelta = new Vector2(thick, len);
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dirs[i].y, dirs[i].x) * Mathf.Rad2Deg + 90f);
            }
        }

        private string FireModeLabel()
        {
            if (gun == null || gun.Definition == null) return "";
            var modes = gun.Definition.fireModes;
            if (modes == null || modes.Count == 0) return "";
            int idx = ((gun.fireModeIndex % modes.Count) + modes.Count) % modes.Count;
            switch (modes[idx])
            {
                case FireMode.Semi: return "半自动";
                case FireMode.Burst: return "连射";
                case FireMode.Bolt: return "栓动";
                default: return "全自动";
            }
        }

        // ---------------------------------------------------------------
        // FEEDBACK API (called by server-driven RPCs / local components)
        // ---------------------------------------------------------------
        public void ShowHitmarker(HitKind kind)
        {
            hitmarkerEndFrame = Time.frameCount + 2;
            hitmarkerKind = kind;
        }

        public void ShowDamageFeedback(float damage)
        {
            // 受击反馈：相机轻微震动 + FOV 增大 2%（2 渲染帧内恢复）由控制器执行。
            if (controller != null)
                controller.ApplyDamageFeedback(damage);
        }

        // ---------------------------------------------------------------
        // HELPERS
        // ---------------------------------------------------------------
        private RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private Image NewImage(string name, Transform parent, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            return img;
        }

        private Text NewText(string name, Transform parent, string text, int fontSize, Color color, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = text;
            t.font = Cjk();
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            return t;
        }

        private static Sprite MakeSolid(float r, float g, float b, float a)
        {
            return MakeSolidTexture(new Color(r, g, b, a));
        }

        private static Sprite MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        private static Sprite MakeCircleSprite()
        {
            int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = new Vector2(size * 0.5f, size * 0.5f);
            float radius = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center);
                    tex.SetPixel(x, y, d <= radius ? Color.white : Color.clear);
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private void ReleaseRenderTextures()
        {
            if (minimapRT != null) { minimapRT.Release(); Destroy(minimapRT); minimapRT = null; }
            if (bigMapRT != null) { bigMapRT.Release(); Destroy(bigMapRT); bigMapRT = null; }
        }
    }
}
