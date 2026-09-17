using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Client-side HUD (PHASE4 + PHASE5 + PHASE7), drawn with IMGUI using
    /// percentage-based sizing. Shows:
    ///   - a white crosshair at screen center that widens with weapon bloom,
    ///   - a white "X" hitmarker (hollow center) for 2 rendered frames after a hit,
    ///   - a bottom-center health bar (white fill, black for missing health),
    ///   - the current armor as a number right of the bar (hidden when 0),
    ///   - the current magazine / reserve ammo, fire mode, and reload state.
    ///
    /// PHASE7 additions:
    ///   - top-center team score (红 / 蓝),
    ///   - death redeploy menu (1=GR / 2=HQ / 3=小队) with countdown,
    ///   - match-over victory / defeat overlay.
    /// </summary>
    public class PlayerHud : MonoBehaviour
    {
        public NetworkPlayerHealth health;

        private NetworkPlayerController controller;
        private NetworkGun gun;
        private NetworkEquipment equipment;
        private NetworkCombatant combatant;
        private int hitmarkerEndFrame = -1;

        private Texture2D whiteTex;
        private Texture2D blackTex;
        private Texture2D redTex;
        private Texture2D blueTex;
        private Texture2D semiTransparentTex;
        private GUIStyle armorStyle;
        private GUIStyle ammoStyle;
        private GUIStyle modeStyle;
        private GUIStyle equipmentStyle;
        private GUIStyle scoreStyle;
        private GUIStyle deployStyle;
        private GUIStyle deployKeyStyle;
        private GUIStyle matchOverStyle;

        private void Awake()
        {
            controller = GetComponent<NetworkPlayerController>();
            if (health == null) health = GetComponent<NetworkPlayerHealth>();
            gun = GetComponent<NetworkGun>();
            equipment = GetComponent<NetworkEquipment>();
            combatant = GetComponent<NetworkCombatant>();
        }

        private void OnEnable()
        {
            if (whiteTex == null) whiteTex = MakeSolidTexture(Color.white);
            if (blackTex == null) blackTex = MakeSolidTexture(Color.black);
            if (redTex == null) redTex = MakeSolidTexture(new Color(0.8f, 0.15f, 0.15f));
            if (blueTex == null) blueTex = MakeSolidTexture(new Color(0.15f, 0.3f, 0.8f));
            if (semiTransparentTex == null) semiTransparentTex = MakeSolidTexture(new Color(0f, 0f, 0f, 0.7f));
        }

        /// <summary>Show the hitmarker for 2 rendered frames.</summary>
        public void ShowHitmarker()
        {
            hitmarkerEndFrame = Time.frameCount + 2;
        }

        private void OnGUI()
        {
            if (controller == null || !controller.isLocalPlayer) return;
            if (health == null) return;

            DrawScore();

            // Match over: show victory / defeat overlay, suppress the rest.
            var mm = NetworkMatchManager.Instance;
            if (mm != null && mm.matchOver)
            {
                DrawMatchOver(mm);
                return;
            }

            DrawCrosshair();
            DrawHitmarker();
            DrawHealthBar();
            DrawArmor();

            if (health.awaitingRedeploy)
                DrawRedeployMenu();
            else
            {
                if (equipment != null && controller.activeSlot >= 0)
                    DrawEquipment();
                else
                    DrawAmmo();
            }
        }

        // ---------------------------------------------------------------
        // TEAM SCORE (top center)
        // ---------------------------------------------------------------
        private void DrawScore()
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null) return;

            if (scoreStyle == null)
            {
                scoreStyle = new GUIStyle(GUI.skin.label);
                scoreStyle.fontSize = Mathf.Max(18, Mathf.RoundToInt(Screen.height * 0.035f));
                scoreStyle.alignment = TextAnchor.MiddleCenter;
                ApplyCjkFont(scoreStyle);
            }

            float w = Screen.width * 0.22f;
            float h = Screen.height * 0.04f;
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.02f;

            string teamLabel = combatant != null && combatant.teamId == (int)MatchTeam.Blue
                ? "蓝方" : "红方";
            scoreStyle.normal.textColor = Color.white;
            string text = $"红方 {mm.redScore}  —  蓝方 {mm.blueScore}  [{teamLabel}]";
            GUI.Label(new Rect(x, y, w, h), text, scoreStyle);
        }

        // ---------------------------------------------------------------
        // REDEPLOY MENU (death deploy selection)
        // ---------------------------------------------------------------
        private void DrawRedeployMenu()
        {
            if (deployStyle == null)
            {
                deployStyle = new GUIStyle(GUI.skin.label);
                deployStyle.fontSize = Mathf.Max(16, Mathf.RoundToInt(Screen.height * 0.03f));
                deployStyle.alignment = TextAnchor.MiddleCenter;
                deployStyle.normal.textColor = Color.white;
                ApplyCjkFont(deployStyle);

                deployKeyStyle = new GUIStyle(GUI.skin.label);
                deployKeyStyle.fontSize = Mathf.Max(14, Mathf.RoundToInt(Screen.height * 0.025f));
                deployKeyStyle.alignment = TextAnchor.MiddleCenter;
                deployKeyStyle.normal.textColor = new Color(0.8f, 0.85f, 1f);
                ApplyCjkFont(deployKeyStyle);
            }

            float w = Screen.width * 0.35f;
            float h = Screen.height * 0.22f;
            float x = (Screen.width - w) * 0.5f;
            float y = (Screen.height - h) * 0.4f;

            GUI.DrawTexture(new Rect(x, y, w, h), semiTransparentTex);

            float lineH = h * 0.2f;
            float cy = y + lineH * 0.5f;

            GUI.Label(new Rect(x, cy, w, lineH), "等待重新部署", deployStyle);
            cy += lineH;
            GUI.Label(new Rect(x, cy, w, lineH), "[1] 驻地 (GR)", deployKeyStyle);
            cy += lineH;
            GUI.Label(new Rect(x, cy, w, lineH), "[2] 据点 (HQ)", deployKeyStyle);
            cy += lineH;
            GUI.Label(new Rect(x, cy, w, lineH), "[3] 小队成员", deployKeyStyle);
        }

        // ---------------------------------------------------------------
        // MATCH OVER (victory / defeat)
        // ---------------------------------------------------------------
        private void DrawMatchOver(NetworkMatchManager mm)
        {
            if (matchOverStyle == null)
            {
                matchOverStyle = new GUIStyle(GUI.skin.label);
                matchOverStyle.fontSize = Mathf.Max(28, Mathf.RoundToInt(Screen.height * 0.06f));
                matchOverStyle.alignment = TextAnchor.MiddleCenter;
                ApplyCjkFont(matchOverStyle);
            }

            bool playerIsRed = combatant == null || combatant.teamId == (int)MatchTeam.Red;
            bool won = (mm.winner == (int)MatchTeam.Red) == playerIsRed;

            matchOverStyle.normal.textColor = won ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.25f, 0.25f);

            string text = won ? "胜利!" : "失败";
            GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, Screen.height * 0.1f), text, matchOverStyle);

            matchOverStyle.fontSize = Mathf.Max(16, Mathf.RoundToInt(Screen.height * 0.03f));
            matchOverStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(0, Screen.height * 0.5f, Screen.width, Screen.height * 0.05f),
                $"红方 {mm.redScore}  —  蓝方 {mm.blueScore}", matchOverStyle);
        }

        // ---------------------------------------------------------------
        // CROSSHAIR (spread widens the gap)
        // ---------------------------------------------------------------
        private void DrawCrosshair()
        {
            // Suppress crosshair while in the redeploy menu.
            if (health.awaitingRedeploy) return;

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float len = Screen.height * 0.012f;
            float thick = Mathf.Max(2f, Screen.height * 0.004f);

            // Bloom (degrees) widens the crosshair gap. Full bloom (10°) maps to a
            // clearly open crosshair; aimed (0.1°) collapses to a tight center.
            float bloom = gun != null ? gun.bloom : 0f;
            float gap = Screen.height * (0.005f + bloom * 0.004f);

            DrawLine(new Vector2(cx, cy - gap - len), new Vector2(cx, cy - gap), thick);
            DrawLine(new Vector2(cx, cy + gap), new Vector2(cx, cy + gap + len), thick);
            DrawLine(new Vector2(cx - gap - len, cy), new Vector2(cx - gap, cy), thick);
            DrawLine(new Vector2(cx + gap, cy), new Vector2(cx + gap + len, cy), thick);
        }

        // ---------------------------------------------------------------
        // HITMARKER (white X, hollow center)
        // ---------------------------------------------------------------
        private void DrawHitmarker()
        {
            if (Time.frameCount >= hitmarkerEndFrame) return;

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float len = Screen.height * 0.018f;
            float thick = Mathf.Max(1f, Screen.height * 0.002f);
            float gap = Screen.height * 0.008f;

            Vector2 c = new Vector2(cx, cy);
            float k = 0.70710678f;   // 1/sqrt(2)
            Vector2[] dirs =
            {
                new Vector2(k, k), new Vector2(k, -k),
                new Vector2(-k, -k), new Vector2(-k, k)
            };

            foreach (var d in dirs)
            {
                DrawLine(c + d * gap, c + d * (gap + len), thick);
            }
        }

        // ---------------------------------------------------------------
        // HEALTH BAR (bottom center: white fill, black missing)
        // ---------------------------------------------------------------
        private void DrawHealthBar()
        {
            float barW = Screen.width * 0.3f;
            float barH = Screen.height * 0.025f;
            float x = (Screen.width - barW) * 0.5f;
            float y = Screen.height * 0.93f;

            GUI.DrawTexture(new Rect(x, y, barW, barH), blackTex);

            float frac = Mathf.Clamp01(health.health / Mathf.Max(0.001f, health.maxHealth));
            GUI.DrawTexture(new Rect(x, y, barW * frac, barH), whiteTex);
        }

        // ---------------------------------------------------------------
        // ARMOR (number right of bar, hidden when 0)
        // ---------------------------------------------------------------
        private void DrawArmor()
        {
            if (health.armor <= 0f) return;

            float barW = Screen.width * 0.3f;
            float barH = Screen.height * 0.025f;
            float x = (Screen.width - barW) * 0.5f;
            float y = Screen.height * 0.93f;

            if (armorStyle == null)
            {
                armorStyle = new GUIStyle(GUI.skin.label);
                armorStyle.fontSize = Mathf.Max(12, Mathf.RoundToInt(Screen.height * 0.03f));
                armorStyle.normal.textColor = Color.white;
                armorStyle.alignment = TextAnchor.MiddleLeft;
                armorStyle.clipping = TextClipping.Overflow;
            }

            string text = Mathf.RoundToInt(health.armor).ToString();
            float tx = x + barW + Screen.width * 0.01f;
            float armorH = Screen.height * 0.035f;
            GUI.Label(new Rect(tx, y, Screen.width * 0.15f, armorH), text, armorStyle);
        }

        // ---------------------------------------------------------------
        // AMMO / FIRE MODE / RELOAD (bottom right)
        // ---------------------------------------------------------------
        private void DrawAmmo()
        {
            if (gun == null || gun.Definition == null) return;

            float y = Screen.height * 0.93f;
            float x = Screen.width * 0.82f;
            float h = Screen.height * 0.03f;

            if (ammoStyle == null)
            {
                ammoStyle = new GUIStyle(GUI.skin.label);
                ammoStyle.fontSize = Mathf.Max(14, Mathf.RoundToInt(Screen.height * 0.032f));
                ammoStyle.normal.textColor = Color.white;
                ammoStyle.alignment = TextAnchor.MiddleRight;
                ammoStyle.clipping = TextClipping.Overflow;
                ApplyCjkFont(ammoStyle);
            }

            string ammoText = $"{gun.magAmmo} / {gun.reserveAmmo}";
            if (gun.reloading)
            {
                ammoText += gun.reloadPaused ? "  (换弹暂停)" : "  (换弹中...)";
            }
            GUI.Label(new Rect(x, y, Screen.width * 0.16f, h), ammoText, ammoStyle);

            if (modeStyle == null)
            {
                modeStyle = new GUIStyle(GUI.skin.label);
                modeStyle.fontSize = Mathf.Max(12, Mathf.RoundToInt(Screen.height * 0.022f));
                modeStyle.normal.textColor = Color.white;
                modeStyle.alignment = TextAnchor.MiddleRight;
                modeStyle.clipping = TextClipping.Overflow;
                ApplyCjkFont(modeStyle);
            }

            string mode = FireModeLabel();
            GUI.Label(new Rect(x, y - h, Screen.width * 0.16f, h), mode, modeStyle);
        }

        // ---------------------------------------------------------------
        // EQUIPMENT (PHASE6: 当前选中装备 + 弹药 + 补给度)
        // ---------------------------------------------------------------
        private void DrawEquipment()
        {
            if (equipment == null) return;

            int idx = Mathf.Clamp(controller.activeSlot, 0, equipment.Count - 1);
            if (idx >= equipment.Count) return;

            var def = equipment.equipmentList[idx];
            if (def == null) return;

            float y = Screen.height * 0.93f;
            float x = Screen.width * 0.82f;
            float h = Screen.height * 0.03f;

            if (equipmentStyle == null)
            {
                equipmentStyle = new GUIStyle(GUI.skin.label);
                equipmentStyle.fontSize = Mathf.Max(14, Mathf.RoundToInt(Screen.height * 0.032f));
                equipmentStyle.normal.textColor = Color.white;
                equipmentStyle.alignment = TextAnchor.MiddleRight;
                equipmentStyle.clipping = TextClipping.Overflow;
                ApplyCjkFont(equipmentStyle);
            }

            string supplyText = def.supplyCost > 0
                ? $"弹药 {equipment.selectedAmmo} | 补给 {equipment.selectedSupply:F0}/{def.supplyCost}"
                : $"弹药 {equipment.selectedAmmo}";

            if (equipment.IsEmpDisabled)
                supplyText += "  [EMP]";

            GUI.Label(new Rect(x, y, Screen.width * 0.16f, h), def.displayName, equipmentStyle);
            GUI.Label(new Rect(x, y - h, Screen.width * 0.16f, h), supplyText, equipmentStyle);
        }

        private string FireModeLabel()
        {
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
        // HELPERS
        // ---------------------------------------------------------------
        private void DrawLine(Vector2 a, Vector2 b, float thickness)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len <= 0f) return;

            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            var oldMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - thickness * 0.5f, len, thickness), whiteTex);
            GUI.matrix = oldMatrix;
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        // IMGUI's default font has no CJK glyphs; use a dynamic OS font for the
        // Chinese ammo/mode labels.
        private static void ApplyCjkFont(GUIStyle style)
        {
            Font cjk = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", style.fontSize);
            if (cjk != null)
                style.font = cjk;
        }
    }
}
