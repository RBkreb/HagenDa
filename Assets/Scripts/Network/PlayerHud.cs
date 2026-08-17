using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Client-side HUD (PHASE4), drawn with IMGUI using percentage-based sizing
    /// (all positions/sizes derive from Screen width/height fractions, no pixel
    /// constants). Shows:
    ///   - a white crosshair at screen center,
    ///   - a white "X" hitmarker (hollow center) for 2 rendered frames after a hit,
    ///   - a bottom-center health bar (white fill, black for missing health),
    ///   - the current armor as a number right of the bar (hidden when 0).
    /// </summary>
    public class PlayerHud : MonoBehaviour
    {
        public NetworkPlayerHealth health;

        private NetworkPlayerController controller;
        private int hitmarkerEndFrame = -1;

        private Texture2D whiteTex;
        private Texture2D blackTex;
        private GUIStyle armorStyle;

        private void Awake()
        {
            controller = GetComponent<NetworkPlayerController>();
            if (health == null) health = GetComponent<NetworkPlayerHealth>();
        }

        private void OnEnable()
        {
            if (whiteTex == null) whiteTex = MakeSolidTexture(Color.white);
            if (blackTex == null) blackTex = MakeSolidTexture(Color.black);
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

            DrawCrosshair();
            DrawHitmarker();
            DrawHealthBar();
            DrawArmor();
        }

        // ---------------------------------------------------------------
        // CROSSHAIR
        // ---------------------------------------------------------------
        private void DrawCrosshair()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float len = Screen.height * 0.012f;
            float thick = Mathf.Max(2f, Screen.height * 0.004f);
            float gap = Screen.height * 0.005f;

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
            }

            string text = Mathf.RoundToInt(health.armor).ToString();
            float tx = x + barW + Screen.width * 0.01f;
            GUI.Label(new Rect(tx, y, Screen.width * 0.15f, barH), text, armorStyle);
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
    }
}
