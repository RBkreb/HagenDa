using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE9 FSM 战场统计 HUD（场景级 OnGUI，仿 DebugHud 风格）。
    /// 左上角显示：状态分布、击杀比、装备使用率、要地归属、系统 tick 性能。
    /// 挂在 FSMBattleSystem 同对象或场景任意非网络对象上。
    /// </summary>
    public class FSMStatsHud : MonoBehaviour
    {
        private float nextRefresh;
        private FSMBattleSystem system;

        // 累计统计
        private int totalKillsRed, totalKillsBlue;
        private int lastRedScore, lastBlueScore;
        private int totalRescuesRed, totalRescuesBlue;
        private int lastAliveRed, lastAliveBlue;

        // 状态分布快照
        private string stateText = "";
        private string scoreText = "";
        private string zoneText = "";
        private string perfText = "";
        private string equipText = "";

        // 装备使用追踪
        private readonly Dictionary<string, int> equipUsage = new Dictionary<string, int>();

        private GUIStyle boxStyle, labelStyle;

        private void Awake()
        {
            system = GetComponent<FSMBattleSystem>();
            if (system == null)
                system = FindObjectOfType<FSMBattleSystem>();
        }

        private void Update()
        {
            if (!NetworkServer.active) return;

            // 击杀统计（比分差 = 击杀数）
            var mm = NetworkMatchManager.Instance;
            if (mm != null)
            {
                if (mm.redScore > lastRedScore)
                {
                    totalKillsRed += mm.redScore - lastRedScore;
                    lastRedScore = mm.redScore;
                }
                if (mm.blueScore > lastBlueScore)
                {
                    totalKillsBlue += mm.blueScore - lastBlueScore;
                    lastBlueScore = mm.blueScore;
                }
            }

            // 救援统计：活人数增加 = 有人被救起（不可能是新生——重生走的是死亡→重部署，不计入活人数净增）
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            int aliveRed = 0, aliveBlue = 0;
            foreach (var c in buf)
            {
                if (c == null || c.IsDead) continue;
                if (c.teamId == 0) aliveRed++;
                else if (c.teamId == 1) aliveBlue++;
            }
            if (aliveRed > lastAliveRed && lastAliveRed > 0)
                totalRescuesRed += aliveRed - lastAliveRed;
            if (aliveBlue > lastAliveBlue && lastAliveBlue > 0)
                totalRescuesBlue += aliveBlue - lastAliveBlue;
            lastAliveRed = aliveRed;
            lastAliveBlue = aliveBlue;

            if (Time.time < nextRefresh) return;
            nextRefresh = Time.time + 0.5f;
            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            var fsms = FindObjectsOfType<FSMAIController>();
            var counts = new Dictionary<FsmState, int>();
            var classCounts = new Dictionary<FsmClass, int>();
            foreach (var f in fsms)
            {
                if (!counts.ContainsKey(f.State)) counts[f.State] = 0;
                counts[f.State]++;
                if (!classCounts.ContainsKey(f.aiClass)) classCounts[f.aiClass] = 0;
                classCounts[f.aiClass]++;
            }

            stateText = "状态: " + string.Join(" | ",
                new string[] {
                    $"{FsmState.Normal}={(counts.ContainsKey(FsmState.Normal) ? counts[FsmState.Normal].ToString() : "0")}",
                    $"{FsmState.Combat}={(counts.ContainsKey(FsmState.Combat) ? counts[FsmState.Combat].ToString() : "0")}",
                    $"{FsmState.Survival}={(counts.ContainsKey(FsmState.Survival) ? counts[FsmState.Survival].ToString() : "0")}",
                    $"{FsmState.Support}={(counts.ContainsKey(FsmState.Support) ? counts[FsmState.Support].ToString() : "0")}",
                });

            var mm = NetworkMatchManager.Instance;
            if (mm != null)
                scoreText = $"击杀: 红{totalKillsRed} 蓝{totalKillsBlue} | 救援: 红{totalRescuesRed} 蓝{totalRescuesBlue} | 分数: 红{mm.redScore} 蓝{mm.blueScore}";

            // 要地
            var zones = FindObjectsOfType<CapturePoint>();
            var parts = new List<string>();
            foreach (var z in zones)
            {
                string owner = z.ownerTeam == 0 ? "红" : (z.ownerTeam == 1 ? "蓝" : "中立");
                parts.Add($"{z.name}:{owner}({z.contention:F0})");
            }
            zoneText = "要地: " + string.Join(" ", parts);

            // 性能
            if (system != null)
                perfText = $"tick: {system.LastTickMs:F2}ms / max {system.MaxTickMs:F2}ms | " +
                           $"ticks={system.TickCount} | agents={system.AgentCount} | " +
                           $"smoke={NetworkSmokeVolume.Count} | fps={1f/Time.smoothDeltaTime:F0}";

            // 装备使用率（简化：统计各兵种活跃数）
            equipText = "兵种: " + string.Join(" ",
                new string[] {
                    $"{FsmClass.Assault}={(classCounts.ContainsKey(FsmClass.Assault) ? classCounts[FsmClass.Assault].ToString() : "0")}",
                    $"{FsmClass.Support}={(classCounts.ContainsKey(FsmClass.Support) ? classCounts[FsmClass.Support].ToString() : "0")}",
                    $"{FsmClass.Recon}={(classCounts.ContainsKey(FsmClass.Recon) ? classCounts[FsmClass.Recon].ToString() : "0")}",
                });
        }

        private void OnGUI()
        {
            EnsureStyles();

            float w = 480f;
            float h = 180f;
            float x = 10f;
            float y = 10f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none, boxStyle);

            float lx = x + 10f;
            float ly = y + 8f;
            float lh = 20f;
            float lw = w - 20f;

            GUI.Label(new Rect(lx, ly, lw, lh), perfText, labelStyle);
            GUI.Label(new Rect(lx, ly + lh, lw, lh), scoreText, labelStyle);
            GUI.Label(new Rect(lx, ly + lh * 2, lw, lh), stateText, labelStyle);
            GUI.Label(new Rect(lx, ly + lh * 3, lw, lh), zoneText, labelStyle);
            GUI.Label(new Rect(lx, ly + lh * 4, lw, lh), equipText, labelStyle);
        }

        private void EnsureStyles()
        {
            if (labelStyle != null) return;
            boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.normal.background = MakeSolidTexture(new Color(0f, 0f, 0f, 0.65f));
            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 13;
            labelStyle.normal.textColor = Color.white;
            Font cjk = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 13);
            if (cjk != null) labelStyle.font = cjk;
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
