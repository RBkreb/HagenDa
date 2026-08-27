using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 指挥官快照覆盖层（纯服务端，非网络对象）。在专用 CommanderMap
    /// layer 上维护快照图形：
    ///
    ///  - 共享组（常驻）：20m 网格线 + 贴边标尺数字 + 原点角标；HQ/GR 字母盘来自
    ///    现有 Highlight 层的 MapPointMarker（含归属配色），由相机 cullingMask 一并渲染。
    ///  - 己方组（按拍照方切换）：该方全部成员色点（阵亡/倒地=暗灰）+ 小队中心圆与编号。
    ///  - 标记敌组（按拍照方切换）：被该方标记的敌军红点。
    ///
    /// 规范（PHASE10）：每张快照只出现"己方小队 + 己方标记敌 + 全部 HQ/GR"，无作弊信息。
    /// LLM 侧坐标系：原点=地图西北角，X 向东、Z 向南，数值=米（WorldToGrid 换算），
    /// 与贴边标尺完全一致；小队编号展示为 squadId+1（1..6），工具层同口径换算。
    /// </summary>
    public class CommanderMapOverlay : MonoBehaviour
    {
        public const string LayerName = "CommanderMap";

        [Header("地图范围（世界坐标 XZ，Setup 菜单按场景填入）")]
        public Vector2 mapMinWorld = new Vector2(-60f, -110f);
        public Vector2 mapMaxWorld = new Vector2(60f, 110f);

        [Header("样式")]
        [Tooltip("网格间距（米）＝标尺步长。")]
        public float gridSize = 20f;

        [Tooltip("覆盖层世界高度（需高于场景最高物）。")]
        public float markerY = 12f;

        public float memberDotSize = 1.6f;
        public float markedDotSize = 2.2f;
        public float centerDiscSize = 3.6f;

        private static readonly Color RedColor = new Color(0.95f, 0.25f, 0.20f);
        private static readonly Color BlueColor = new Color(0.30f, 0.55f, 1.00f);
        private static readonly Color MarkRed = new Color(1f, 0.05f, 0.05f);
        private static readonly Color DimDead = new Color(0.32f, 0.32f, 0.32f);
        private static readonly Color GridColor = new Color(0.55f, 0.55f, 0.55f);

        private Transform sharedRoot;
        private Transform dotsRed;
        private Transform dotsBlue;
        private Transform marksForRed;
        private Transform marksForBlue;

        private void Start()
        {
            BuildShared();
            dotsRed = NewGroup("Dots_Red");
            dotsBlue = NewGroup("Dots_Blue");
            marksForRed = NewGroup("Marks_ForRed");
            marksForBlue = NewGroup("Marks_ForBlue");

            // 默认只显示共享组，避免非拍照时刻残留动态标记。
            SetGroupsVisible(false, false, false, false);
        }

        // ---------------------------------------------------------------
        // 地图坐标换算（LLM 网格坐标系 ⇄ 世界坐标）
        // ---------------------------------------------------------------

        public float MapWidth => mapMaxWorld.x - mapMinWorld.x;
        public float MapLength => mapMaxWorld.y - mapMinWorld.y;

        /// <summary>世界 → 网格坐标。约定（PHASE10 v2，面向 LLM 直觉）：
        /// 原点(0,0)=地图西南角（画面左下角），X 向东为正，Z 向北为正（单位米）。
        /// 与贴边标尺一致；快照图像上方=北 ⇒ 越靠上读数越大。</summary>
        public Vector2 WorldToGrid(Vector3 world)
        {
            float gx = Mathf.Clamp(world.x - mapMinWorld.x, 0f, MapWidth);
            float gz = Mathf.Clamp(world.z - mapMinWorld.y, 0f, MapLength);
            return new Vector2(gx, gz);
        }

        /// <summary>网格坐标 → 世界坐标（y=markerY）。</summary>
        public Vector3 GridToWorld(float gx, float gz)
        {
            return new Vector3(
                Mathf.Clamp(gx, 0f, MapWidth) + mapMinWorld.x,
                markerY,
                Mathf.Clamp(gz, 0f, MapLength) + mapMinWorld.y);
        }

        // ---------------------------------------------------------------
        // 网格代号（A1 风格）⇄ 格中心网格坐标
        // ---------------------------------------------------------------

        /// <summary>列数 = X 方向格子数；行数 = Z 方向格子数。</summary>
        public int Cols => Mathf.Max(1, Mathf.RoundToInt(MapWidth / gridSize));
        public int Rows => Mathf.Max(1, Mathf.RoundToInt(MapLength / gridSize));

        /// <summary>"C4" → 该格中心点的米制坐标。非法代号返回 false。</summary>
        public bool TryCellToGrid(string cell, out float gx, out float gz)
        {
            gx = gz = 0f;
            if (string.IsNullOrEmpty(cell)) return false;
            cell = cell.Trim().ToUpperInvariant();
            if (cell.Length < 2 || cell.Length > 3) return false;
            int colIdx = char.ToUpperInvariant(cell[0]) - 'A';
            if (colIdx < 0 || colIdx >= Cols) return false;
            if (!int.TryParse(cell.Substring(1), out int row) ||
                row < 1 || row > Rows) return false;
            gx = (colIdx + 0.5f) * (MapWidth / Cols);
            gz = (row - 0.5f) * (MapLength / Rows);
            return true;
        }

        /// <summary>世界坐标 → 网格代号（供事件报文/调试）。</summary>
        public string WorldToCell(Vector3 world)
        {
            var g = WorldToGrid(world);
            int ci = Mathf.Clamp((int)(g.x * Cols / MapWidth), 0, Cols - 1);
            int ri = Mathf.Clamp((int)(g.y * Rows / MapLength), 0, Rows - 1);
            return $"{(char)('A' + ci)}{ri + 1}";
        }

        // ---------------------------------------------------------------
        // 静态共享图形：网格 + 标尺 + 原点角标
        // ---------------------------------------------------------------

        private void BuildShared()
        {
            sharedRoot = NewGroup("Shared_GridRuler");

            int nx = Mathf.Max(1, Mathf.RoundToInt(MapWidth / gridSize));
            int nz = Mathf.Max(1, Mathf.RoundToInt(MapLength / gridSize));

            // 网格线：X 步进竖线 / Z 步进横线。
            for (int i = 0; i <= nx; i++)
            {
                float x = mapMinWorld.x + i * Mathf.Min(gridSize, MapWidth);
                AddVerticalLine(x);
            }
            for (int j = 0; j <= nz; j++)
            {
                float z = mapMaxWorld.y - j * Mathf.Min(gridSize, MapLength);
                AddHorizontalLine(z);
            }

            // 贴边标尺数字：原点西南角（左下），X 沿南缘向东递增，Z 沿西缘向北递增。
            // LLM 视觉分辨率有限 → 数字尽量大（实测偏小时模型难以读准）。
            for (int i = 0; i <= nx; i++)
            {
                float x = mapMinWorld.x + i * Mathf.Min(gridSize, MapWidth);
                AddLabel(((int)Mathf.Round(i * Mathf.Min(gridSize, MapWidth))).ToString(),
                         new Vector3(x, mapMinWorld.y + 4.5f), 0.62f, 64, Color.white);
            }
            for (int j = 0; j <= nz; j++)
            {
                float z = mapMinWorld.y + j * Mathf.Min(gridSize, MapLength);
                AddLabel(((int)Mathf.Round(j * Mathf.Min(gridSize, MapLength))).ToString(),
                         new Vector3(mapMinWorld.x + 4.5f, z), 0.62f, 64, Color.white);
            }

            // 原点 (0,0) 角标：白色小盘 + 文本（西南角=画面左下，锚定方向）。
            var originDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            originDisc.name = "OriginMark";
            originDisc.transform.SetParent(sharedRoot, false);
            originDisc.transform.position = new Vector3(mapMinWorld.x, markerY - 0.15f, mapMinWorld.y);
            originDisc.transform.localScale = new Vector3(5f, 0.02f, 5f);
            StripCollider(originDisc);
            SetMat(originDisc.GetComponent<Renderer>(), Color.white);
            ApplyLayer(originDisc);

            AddLabel("(0,0)", new Vector3(mapMinWorld.x + 11f, mapMinWorld.y + 11f),
                     0.72f, 72, Color.white);

            // 网格代号（PHASE10 v3，用户方案）：每个格子中央印 "列字母+行数字"
            // （如 A1、C4）。LLM 下令引用代号而非米坐标——视觉任务从"测量"
            // 降维成"识字"，规避 VLM 脆弱的精确读数。
            for (int ci = 0; ci < nx; ci++)
            {
                char col = (char)('A' + Mathf.Min(ci, 25));
                float cx = mapMinWorld.x + (ci + 0.5f) * (MapWidth / nx);
                for (int ri = 0; ri < nz; ri++)
                {
                    float cz = mapMinWorld.y + (ri + 0.5f) * (MapLength / nz);
                    AddLabel($"{col}{ri + 1}", new Vector3(cx, cz), 0.4f, 46,
                             new Color(1f, 1f, 0.75f));
                }
            }
        }

        private void AddVerticalLine(float x)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "GridLine";
            go.transform.SetParent(sharedRoot, false);
            go.transform.position = new Vector3(x, markerY + 0.02f, (mapMinWorld.y + mapMaxWorld.y) * 0.5f);
            go.transform.localScale = new Vector3(0.06f, 0.02f, MapLength);
            StripCollider(go);
            SetMat(go.GetComponent<Renderer>(), GridColor);
            ApplyLayer(go);
        }

        private void AddHorizontalLine(float z)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "GridLine";
            go.transform.SetParent(sharedRoot, false);
            go.transform.position = new Vector3((mapMinWorld.x + mapMaxWorld.x) * 0.5f, markerY + 0.02f, z);
            go.transform.localScale = new Vector3(MapWidth, 0.02f, 0.06f);
            StripCollider(go);
            SetMat(go.GetComponent<Renderer>(), GridColor);
            ApplyLayer(go);
        }

        private void AddLabel(string text, Vector2 xz, float characterSize, int fontSize, Color color)
        {
            var go = new GameObject("Label", typeof(TextMesh));
            go.transform.SetParent(sharedRoot, false);
            go.transform.position = new Vector3(xz.x, markerY + 0.12f, xz.y);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // 平躺，俯视可读（北在上）
            var tm = go.GetComponent<TextMesh>();
            tm.text = text;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontSize = fontSize;
            tm.characterSize = characterSize;
            tm.fontStyle = FontStyle.Bold;
            tm.color = color;
            ApplyLayer(go);
        }

        // ---------------------------------------------------------------
        // 分组显隐 + 拍照前刷新
        // ---------------------------------------------------------------

        /// <summary>为某一方准备一张快照：启用对应分组并重建动态标记。</summary>
        public void PrepareForCapture(int team)
        {
            bool red = team == (int)MatchTeam.Red;
            SetGroupsVisible(red, !red, red, !red);

            FillOwnTeam(red ? dotsRed : dotsBlue, team);
            FillSquadCenters(red ? dotsRed : dotsBlue, team);
            FillMarks(red ? marksForRed : marksForBlue, team);
        }

        /// <summary>拍照后归位：只保留共享组。</summary>
        public void ResetToIdle() => SetGroupsVisible(false, false, false, false);

        private void SetGroupsVisible(bool r, bool b, bool mr, bool mb)
        {
            if (dotsRed != null) dotsRed.gameObject.SetActive(r);
            if (dotsBlue != null) dotsBlue.gameObject.SetActive(b);
            if (marksForRed != null) marksForRed.gameObject.SetActive(mr);
            if (marksForBlue != null) marksForBlue.gameObject.SetActive(mb);
        }

        private void FillOwnTeam(Transform root, int team)
        {
            Clear(root);
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            Color alive = team == (int)MatchTeam.Red ? RedColor : BlueColor;

            foreach (var c in buf)
            {
                if (c == null || c.teamId != team || c.squadId < 0) continue;
                CreateSphere(root, c.transform.position,
                             memberDotSize * 0.5f, c.IsDead ? DimDead : alive);
            }
        }

        private void FillSquadCenters(Transform root, int team)
        {
            // 成员平均坐标的小队中心点（仅存活成员；全灭小队不出现在图上）。
            var acc = new Dictionary<int, Vector3>();
            var cnt = new Dictionary<int, int>();
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            foreach (var c in buf)
            {
                if (c == null || c.teamId != team || c.squadId < 0 || c.IsDead) continue;
                if (!acc.ContainsKey(c.squadId)) { acc[c.squadId] = Vector3.zero; cnt[c.squadId] = 0; }
                acc[c.squadId] += c.transform.position;
                cnt[c.squadId]++;
            }

            Color col = team == (int)MatchTeam.Red ? RedColor : BlueColor;
            foreach (var kv in acc)
            {
                Vector3 pos = acc[kv.Key] / cnt[kv.Key];
                int number = kv.Key + 1;   // LLM 口径 1..6

                var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                disc.name = $"Center_T{team}_S{kv.Key}";
                disc.transform.SetParent(root, false);
                disc.transform.position = new Vector3(pos.x, markerY, pos.z);
                disc.transform.localScale = new Vector3(centerDiscSize, 0.04f, centerDiscSize);
                StripCollider(disc);
                SetMat(disc.GetComponent<Renderer>(), col);
                ApplyLayer(disc);

                var labelGo = new GameObject($"Num_{number}", typeof(TextMesh));
                labelGo.transform.SetParent(root, false);
                labelGo.transform.position = new Vector3(pos.x, markerY + 0.12f, pos.z);
                labelGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                var tm = labelGo.GetComponent<TextMesh>();
                tm.text = number.ToString();
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.fontSize = 100;
                tm.characterSize = 0.5f;
                tm.fontStyle = FontStyle.Bold;
                tm.color = Color.black;
                ApplyLayer(labelGo);
            }
        }

        private void FillMarks(Transform root, int byTeam)
        {
            Clear(root);
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            foreach (var c in buf)
            {
                if (c == null || c.IsDead) continue;
                if (!c.IsMarked || c.markedByTeam != byTeam) continue;
                if (c.teamId == byTeam) continue;   // 只画敌方
                CreateSphere(root, c.transform.position, markedDotSize * 0.5f, MarkRed);
            }
        }

        // ---------------------------------------------------------------
        // 元素工厂
        // ---------------------------------------------------------------

        private Transform NewGroup(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            ApplyLayer(go);
            return go.transform;
        }

        private static GameObject CreateSphere(Transform parent, Vector3 worldPos,
                                               float radius, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(worldPos.x, 13f, worldPos.z);   // 高于网格一点
            go.transform.localScale = Vector3.one * radius * 2f;
            StripCollider(go);
            SetMat(go.GetComponent<Renderer>(), color);
            ApplyLayer(go);
            return go;
        }

        private static void Clear(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
                Destroy(root.GetChild(i).gameObject);
        }

        private static void StripCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        private static void SetMat(Renderer rend, Color color)
        {
            if (rend == null) return;
            Shader s = Shader.Find("HDRP/Unlit");
            if (s == null) s = Shader.Find("Sprites/Default");
            if (s == null) s = Shader.Find("Standard");
            var mat = new Material(s);
            mat.SetColor("_BaseColor", color);
            mat.SetColor("_UnlitColor", color);
            mat.SetColor("_Color", color);
            rend.sharedMaterial = mat;
        }

        private static void ApplyLayer(GameObject go)
        {
            int l = LayerMask.NameToLayer(LayerName);
            if (l >= 0) go.layer = l;
            for (int i = 0; i < go.transform.childCount; i++)
                ApplyLayer(go.transform.GetChild(i).gameObject);
        }
    }
}
