using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HagenDa.Networking
{
    /// <summary>
    /// Top-right on-screen debug HUD for the local player.
    /// Shows 3D speed, max speed over the last 3s, posture, and slide-trigger state.
    ///
    /// Speed is sampled every rendered frame (authoritative rb.velocity on the host,
    /// position-delta estimate on a pure client). The visible text refreshes every
    /// <see cref="updateInterval"/> (0.2s). Chinese text uses a dynamic OS font
    /// (Microsoft YaHei) because IMGUI's default font has no CJK glyphs.
    /// </summary>
    public class DebugHud : MonoBehaviour
    {
        [Tooltip("Seconds between display text refreshes.")]
        public float updateInterval = 0.1f;

        [Tooltip("Rolling window (seconds) over which the max speed is computed.")]
        public float maxWindow = 3f;

        private NetworkPlayerController controller;
        private Rigidbody rb;
        private Transform tr;

        private Vector3 velocity;
        private Vector3 lastPos;
        private float lastTime;

        // Rolling samples for the max-speed window.
        private readonly Queue<float> sampleTimes = new Queue<float>();
        private readonly Queue<float> sampleSpeeds = new Queue<float>();

        private float nextRefresh;
        private float maxSpeedWindow;

        private string speedText = "";
        private string maxText = "";
        private string postureText = "";
        private string slideText = "";

        private GUIStyle boxStyle;
        private GUIStyle labelStyle;

        private void Awake()
        {
            controller = GetComponent<NetworkPlayerController>();
            rb = GetComponent<Rigidbody>();
            tr = transform;
        }

        private void Start()
        {
            lastPos = tr.position;
            lastTime = Time.time;
        }

        private void Update()
        {
            if (controller == null || !controller.isLocalPlayer) return;

            // Sample the current 3D velocity.
            float dt = Time.time - lastTime;
            if (dt > 0.0001f)
            {
                if (controller.isServer)
                    velocity = rb.velocity;                    // authoritative on host
                else
                    velocity = (tr.position - lastPos) / dt;   // client-side estimate
            }
            lastPos = tr.position;
            lastTime = Time.time;

            sampleTimes.Enqueue(Time.time);
            sampleSpeeds.Enqueue(velocity.magnitude);

            if (Time.time >= nextRefresh)
            {
                nextRefresh = Time.time + updateInterval;
                RefreshDisplay();
            }
        }

        private void RefreshDisplay()
        {
            // Prune samples older than maxWindow, then compute the window max.
            while (sampleTimes.Count > 0 && Time.time - sampleTimes.Peek() > maxWindow)
            {
                sampleTimes.Dequeue();
                sampleSpeeds.Dequeue();
            }

            maxSpeedWindow = 0f;
            foreach (float s in sampleSpeeds)
                if (s > maxSpeedWindow)
                    maxSpeedWindow = s;

            speedText = $"三维速度: {velocity.magnitude:F2} m/s";
            maxText = $"3秒最大: {maxSpeedWindow:F2} m/s";

            switch (controller.posture)
            {
                case PlayerPosture.Crouch: postureText = "姿态: 蹲"; break;
                case PlayerPosture.Prone: postureText = "姿态: 趴"; break;
                default: postureText = "姿态: 站"; break;
            }

            slideText = "滑铲: " + EvaluateSlide();
        }

        private string EvaluateSlide()
        {
            if (controller.sliding)
                return "进行中";

            Vector3 h = new Vector3(velocity.x, 0f, velocity.z);
            float hSpeed = h.magnitude;

            var k = Keyboard.current;
            bool ctrl = k != null && k.leftCtrlKey.isPressed;

            bool speedOk = hSpeed > controller.slideTriggerSpeed;

            if (speedOk && ctrl)
                return $"可触发 (速度 {hSpeed:F1})";
            return $"待机 (速度 {hSpeed:F1}/{controller.slideTriggerSpeed:F0} ctrl:{(ctrl ? "按" : "松")})";
        }

        private void OnGUI()
        {
            if (controller == null || !controller.isLocalPlayer) return;
            EnsureStyles();

            float w = 280f;
            float h = 120f;
            float x = Screen.width - w - 10f;
            float y = 10f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none, boxStyle);

            float lx = x + 10f;
            float ly = y + 10f;
            float lh = 24f;
            float lw = w - 20f;

            GUI.Label(new Rect(lx, ly, lw, lh), speedText, labelStyle);
            GUI.Label(new Rect(lx, ly + lh, lw, lh), maxText, labelStyle);
            GUI.Label(new Rect(lx, ly + lh * 2, lw, lh), postureText, labelStyle);
            GUI.Label(new Rect(lx, ly + lh * 3, lw, lh), slideText, labelStyle);
        }

        private void EnsureStyles()
        {
            if (labelStyle != null) return;

            boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.normal.background = MakeSolidTexture(new Color(0f, 0f, 0f, 0.6f));

            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 15;
            labelStyle.normal.textColor = Color.white;

            // IMGUI's default font has no CJK glyphs; use a dynamic OS font instead.
            Font cjk = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 15);
            if (cjk != null)
                labelStyle.font = cjk;
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(2, 2);
            var pixels = new Color[4];
            for (int i = 0; i < 4; i++) pixels[i] = color;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
