using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 客户端表现（挂场景任意对象，Setup 菜单注入；无人类玩家时自然闲置）：
    ///
    ///  - 门控遮罩：NetworkCommanderState.matchStarted=false 期间全屏半透明黑 +
    ///    "COMMANDER DEPLOYING…"（Q16 全体冻结的视觉反馈）；
    ///  - 玩家小队目标：HUD 左下一行文字 + 小地图(Indicator 层)/大地图&主相机
    ///    (Highlight 层) 黄色菱形标记（Q17:b）。玩家本体不受任何强制。
    /// </summary>
    public class CommanderHud : MonoBehaviour
    {
        private Canvas canvas;
        private Text gateText;
        private Text objectiveText;
        private RectTransform gateRect;

        private readonly List<Transform> diamonds = new List<Transform>();
        private bool lastMatchStarted = true;

        private static readonly Color Yellow = new Color(1f, 0.85f, 0.15f);

        private void LateUpdate()
        {
            var st = NetworkCommanderState.Instance;

            // ---- 门控遮罩 ----
            bool gating = st != null && !st.matchStarted && NetworkClient.active;
            if (gating != !lastMatchStarted || canvas == null)
                EnsureCanvas();
            if (gateRect != null)
                gateRect.gameObject.SetActive(gating);
            lastMatchStarted = st != null ? st.matchStarted : true;

            // ---- 目标标记（本地玩家的己方小队）----
            RebuildDiamonds(st);

            // ---- HUD 一行文字 ----
            if (objectiveText != null)
            {
                string line = LocalObjectiveLine(st);
                objectiveText.gameObject.SetActive(!string.IsNullOrEmpty(line));
                objectiveText.text = line ?? "";
            }
        }

        // ---------------------------------------------------------------
        // Canvas 构建（一次性）
        // ---------------------------------------------------------------

        private void EnsureCanvas()
        {
            if (canvas != null) return;
            if (!NetworkClient.active) return;

            var go = new GameObject("CommanderHudCanvas", typeof(Canvas), typeof(CanvasScaler));
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;

            // 遮罩（Image 与 Text 必须分属两个 GO——Unity 禁止同挂）
            var maskGo = new GameObject("GateMask", typeof(Image));
            maskGo.transform.SetParent(canvas.transform, false);
            gateRect = maskGo.GetComponent<RectTransform>();
            gateRect.anchorMin = Vector2.zero;
            gateRect.anchorMax = Vector2.one;
            gateRect.offsetMin = Vector2.zero;
            gateRect.offsetMax = Vector2.zero;
            maskGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            var txtGo = new GameObject("GateText", typeof(Text));
            txtGo.transform.SetParent(maskGo.transform, false);
            var trect = txtGo.GetComponent<RectTransform>();
            trect.anchorMin = Vector2.zero;
            trect.anchorMax = Vector2.one;
            trect.offsetMin = Vector2.zero;
            trect.offsetMax = Vector2.zero;
            gateText = txtGo.GetComponent<Text>();
            gateText.alignment = TextAnchor.MiddleCenter;
            gateText.fontSize = Mathf.RoundToInt(Screen.height * 0.05f);
            gateText.color = Color.white;
            gateText.text = "COMMANDER DEPLOYING\u2026";

            // 目标一行
            var objGo = new GameObject("Objective", typeof(Text));
            objGo.transform.SetParent(canvas.transform, false);
            var orect = objGo.GetComponent<RectTransform>();
            orect.anchorMin = new Vector2(0.02f, 0.02f);
            orect.anchorMax = new Vector2(0.6f, 0.06f);
            orect.offsetMin = Vector2.zero;
            orect.offsetMax = Vector2.zero;
            objectiveText = objGo.GetComponent<Text>();
            objectiveText.alignment = TextAnchor.MiddleLeft;
            objectiveText.fontSize = Mathf.RoundToInt(Screen.height * 0.022f);
            objectiveText.color = Yellow;
        }

        // ---------------------------------------------------------------
        // 本地玩家小队目标
        // ---------------------------------------------------------------

        private (int team, int squad)? LocalSquad()
        {
            if (!Mirror.NetworkClient.active || Mirror.NetworkClient.localPlayer == null)
                return null;
            var c = Mirror.NetworkClient.localPlayer.GetComponent<NetworkCombatant>();
            if (c == null || c.teamId < 0 || c.squadId < 0) return null;
            return (c.teamId, c.squadId);
        }

        private string LocalObjectiveLine(NetworkCommanderState st)
        {
            var ls = LocalSquad();
            if (ls == null || st == null) return null;

            foreach (var o in st.objectives)
            {
                if (o.team == ls.Value.team && o.squad == ls.Value.squad)
                    return $"小队{o.squad + 1} → 格#{o.cellId}";
            }
            return "(小队暂无指令)";
        }

        private void RebuildDiamonds(NetworkCommanderState st)
        {
            if (st == null || !NetworkClient.active || st.objectives.Count == 0)
            {
                ClearDiamonds();
                signature = -1;
                return;
            }

            // 观战（无本地玩家）：双方全部目标可见（FSMBattle 全自动对局需求）；
            // 有本地玩家：仅己方小队目标上小地图/大地图，避免敌方情报泄露。
            bool spectator = LocalSquad() == null;

            // 变更签名：数量 + 各条目内容 → 更新也触发重建。
            int sig = st.objectives.Count * 7919;
            foreach (var o in st.objectives)
                sig = sig * 31 ^ (o.cellId * 1013 + (int)(o.wx * 2) * 31 +
                                  (int)(o.wz * 2) * 7 + o.team * 7 + o.squad);
            if (sig == signature) return;
            signature = sig;
            ClearDiamonds();

            foreach (var o in st.objectives)
            {
                if (!spectator && LocalSquad().HasValue &&
                    !(o.team == LocalSquad().Value.team && o.squad == LocalSquad().Value.squad))
                    continue;

                Color col = o.team == (int)MatchTeam.Red
                    ? new Color(1f, 0.45f, 0.25f)     // 红-亮橙
                    : new Color(0.35f, 0.85f, 1f);    // 蓝-亮青

                // PHASE11：世界坐标随 objectives 同步，客户端无需本地换算。
                Vector3 world = new Vector3(o.wx, MapLayers.IndicatorWorldY, o.wz);

                string label = $"{(o.team == (int)MatchTeam.Red ? "R" : "B")}{o.squad + 1}";
                AddDiamond(world, MapLayers.Indicator, 5f, col, label);       // 小地图
                AddDiamond(world, MapLayers.Highlight, 8f, col, label, true); // 大地图/自由相机
            }
        }

        private int signature = -1;

        private void AddDiamond(Vector3 pos, int layer, float size, Color color,
                                string label = null, bool withLabel = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "CmdMarker";
            go.transform.position = pos + Vector3.up * (layer == MapLayers.Indicator ? 0.2f : 0.1f);
            go.transform.rotation = Quaternion.Euler(90f, 45f, 0f);
            go.transform.localScale = Vector3.one * size;
            go.layer = layer;

            Shader s = Shader.Find("HDRP/Unlit");
            if (s == null) s = Shader.Find("Sprites/Default");
            var mat = new Material(s);
            mat.SetColor("_BaseColor", color);
            mat.SetColor("_UnlitColor", color);
            mat.SetColor("_Color", color);
            go.GetComponent<Renderer>().sharedMaterial = mat;

            diamonds.Add(go.transform);

            if (withLabel && !string.IsNullOrEmpty(label))
            {
                var lgo = new GameObject("CmdMarkerLabel", typeof(TextMesh));
                lgo.transform.position = go.transform.position + Vector3.up * 0.15f;
                lgo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                lgo.layer = layer;
                var tm = lgo.GetComponent<TextMesh>();
                tm.text = label;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.fontSize = 48;
                tm.characterSize = 0.28f;
                tm.fontStyle = FontStyle.Bold;
                tm.color = color;
                diamonds.Add(lgo.transform);
            }
        }

        private void ClearDiamonds()
        {
            foreach (var t in diamonds)
                if (t != null) Destroy(t.gameObject);
            diamonds.Clear();
        }
    }
}
