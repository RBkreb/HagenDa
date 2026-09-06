using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE11 指挥官六边形空间编码（纯服务端，红蓝指挥官共享一个实例）。
    ///
    /// flat-top 六边形；格尺寸随地图 AABB 自动缩放，二分逼近目标格数。
    /// 有效格 = 格中心向下射线粗筛（RaycastAll 跳过动态实体与触发器，忽略
    /// ceiling/地图标记层，取首个静态结构命中）+ NavMesh.SamplePosition 可走性权威。
    /// 惰性预计算一次，#N 全局稳定（LLM 可跨轮引用）。
    ///
    /// LLM 词汇表：顺序整数编号 #N（1..N，按南→北、西→东排序）+ 整数中心 (X,Z)
    /// （米，原点=地图西南角，X 东 Z 北）。偏移坐标仅内部使用（Gizmos/未来邻接）。
    /// </summary>
    public class CommanderHexGrid : MonoBehaviour
    {
        [Header("网格")]
        [Tooltip("目标有效格数（格尺寸自动缩放逼近该值）。")]
        public int targetCells = 64;

        [Tooltip("NavMesh 采样容差（米）：射线命中点吸附到可走面的最大距离。")]
        public float navSampleMaxDist = 1.5f;

        [Tooltip("地图边界覆盖。CommanderSetup 按场景写入；为空时依次回退\n" +
                 "MapLayers.TryGetMapBounds（FBX 图 Ground 层 AABB）→ 默认 ±60/±110。")]
        public Bounds boundsOverride;

        private sealed class Cell
        {
            public int id;          // 1..N（LLM 口径）
            public Vector2 center;  // 格中心 XZ
            public Vector3 world;   // NavMesh 吸附后的可驻点（y=地面）
        }

        private readonly List<Cell> cells = new List<Cell>();

        // 全部候选格 + 有效性（仅供 Gizmos 可视化）。
        private readonly List<(Vector2 c, bool valid)> candidates =
            new List<(Vector2, bool)>();

        private float hexRadius;
        private Bounds bounds;
        private bool built;

        public int CellCount => cells.Count;
        public float HexRadius => hexRadius;
        public Bounds MapBounds => bounds;

        // ---------------------------------------------------------------
        // 构建（惰性一次；Start 运行时 / Gizmos 编辑期均可触发）
        // ---------------------------------------------------------------

        private void Start() => EnsureBuilt();

        public void EnsureBuilt()
        {
            // 构建结果为空时不视作完成（首建可能撞上 NavMesh/物理未就绪的瞬态，
            // 实测踩坑）——下次查询自动重试，自愈。
            if (built && cells.Count > 0) return;
            built = true;

            bounds = ResolveBounds();
            float area = Mathf.Max(1f, bounds.size.x * bounds.size.z);
            // 六边形面积 = 3√3/2 · R² → 由目标格数反推初始半径。
            float r0 = Mathf.Sqrt(area / Mathf.Max(1, targetCells) *
                                  (2f / (3f * Mathf.Sqrt(3f))));

            // 二分逼近目标格数（格数随 R 单调递减）。
            float lo = r0 * 0.5f, hi = r0 * 2f;
            float bestR = r0;
            int bestErr = int.MaxValue;
            var probe = new List<Cell>();
            int want = Mathf.Max(1, targetCells);
            for (int i = 0; i < 22 && lo < hi; i++)
            {
                float mid = (lo + hi) * 0.5f;
                int n = BuildAt(mid, probe);
                int err = Mathf.Abs(n - want);
                if (err < bestErr) { bestErr = err; bestR = mid; }
                if (n == want) break;
                if (n > want) lo = mid; else hi = mid;
            }

            hexRadius = bestR;
            cells.Clear();
            candidates.Clear();
            BuildAt(hexRadius, cells);
            BuildCandidates();

            // 编号：南→北、西→东（LLM 读表时相邻编号空间上聚类）。
            cells.Sort((a, b) => a.center.y != b.center.y
                ? a.center.y.CompareTo(b.center.y)
                : a.center.x.CompareTo(b.center.x));
            for (int i = 0; i < cells.Count; i++) cells[i].id = i + 1;

            Debug.Log($"[HexGrid] 有效格 {cells.Count}（目标 {want}）R={hexRadius:F1}m " +
                      $"bounds X[{bounds.min.x:F0},{bounds.max.x:F0}] " +
                      $"Z[{bounds.min.z:F0},{bounds.max.z:F0}]");
        }

        private Bounds ResolveBounds()
        {
            if (boundsOverride.size.x > 0.01f && boundsOverride.size.z > 0.01f)
                return boundsOverride;
            if (MapLayers.TryGetMapBounds(out var b) && b.size.x > 0.01f)
                return b;
            return new Bounds(Vector3.zero, new Vector3(120f, 20f, 220f));
        }

        /// <summary>给定半径生成全部格心、逐格做有效格判定，填充并返回有效格数。</summary>
        private int BuildAt(float R, List<Cell> sink)
        {
            sink.Clear();
            int mask = StructureMask();
            if (mask == 0) return 0;

            float top = bounds.max.y + 5f;
            float len = bounds.size.y + 20f;

            foreach (var c in Centers(R))
            {
                if (TryValidate(c, top, len, mask, out var world))
                    sink.Add(new Cell { center = c, world = world });
            }
            return sink.Count;
        }

        /// <summary>flat-top 六边形 odd-q 偏移布局的全部格心（格心落在 AABB 内）。</summary>
        private IEnumerable<Vector2> Centers(float R)
        {
            float dxz = Mathf.Sqrt(3f) * R;   // 行距（flat-to-flat）
            float dx = 1.5f * R;              // 列距
            for (int c = 0; ; c++)
            {
                float cx = bounds.min.x + R + dx * c;
                if (cx > bounds.max.x + 0.001f) break;
                float odd = (c & 1) == 1 ? dxz * 0.5f : 0f;   // odd-q 垂直偏移
                for (int r = 0; ; r++)
                {
                    float cz = bounds.min.z + dxz * 0.5f + dxz * r + odd;
                    if (cz > bounds.max.z + 0.001f) break;
                    yield return new Vector2(cx, cz);
                }
            }
        }

        /// <summary>
        /// 单格有效判定：向下射线取首个静态结构命中（跳过动态实体——AI/玩家
        /// 站在格心不能杀死该格；墙顶等无 NavMesh 的顶面则判无效），再以
        /// NavMesh.SamplePosition 为"这里能站人"的权威。
        /// </summary>
        private bool TryValidate(Vector2 c, float top, float len, int mask,
                                 out Vector3 snapped)
        {
            snapped = default;
            var hits = Physics.RaycastAll(new Vector3(c.x, top, c.y), Vector3.down,
                                          len, mask, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return false;
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var h in hits)
            {
                if (h.collider.attachedRigidbody != null) continue;          // 动态物体
                if (h.collider.GetComponentInParent<NetworkCombatant>() != null) continue;
                // 只取首个静态结构命中：墙顶无 NavMesh → 该格无效（防止
                // queriesHitBackfaces 下穿墙底背面误判为可走）。
                if (NavMesh.SamplePosition(h.point, out var nav,
                                           navSampleMaxDist, NavMesh.AllAreas))
                {
                    snapped = nav.position;
                    return true;
                }
                return false;
            }
            return false;
        }

        /// <summary>静态结构射线掩码：Default 排除 ceiling 与地图标记层（Ground ⊂ Default）。</summary>
        private static int StructureMask()
        {
            int m = Physics.DefaultRaycastLayers;
            int[] exclude =
            {
                MapLayers.Ceiling, MapLayers.Indicator, MapLayers.Highlight,
                MapLayers.Zone, MapLayers.ZoneOutline,
                LayerMask.NameToLayer(MapLayers.CommanderMapName),
            };
            foreach (var l in exclude)
                if (l >= 0) m &= ~(1 << l);
            return m;
        }

        private void BuildCandidates()
        {
            int mask = StructureMask();
            float top = bounds.max.y + 5f;
            float len = bounds.size.y + 20f;
            foreach (var c in Centers(hexRadius))
                candidates.Add((c, TryValidate(c, top, len, mask, out _)));
        }

        // ---------------------------------------------------------------
        // 查询 / LLM 词汇表
        // ---------------------------------------------------------------

        /// <summary>1 基编号 → 可驻世界点。</summary>
        public bool TryGetCell(int id1based, out Vector3 world)
        {
            world = default;
            if (id1based < 1 || id1based > cells.Count) return false;
            world = cells[id1based - 1].world;
            return true;
        }

        /// <summary>世界点 → 最近有效格编号（1 基；无有效格返回 0）。</summary>
        public int NearestCellId(Vector3 world)
        {
            int best = 0;
            float bestD = float.MaxValue;
            foreach (var cell in cells)
            {
                float dx = cell.center.x - world.x;
                float dz = cell.center.y - world.z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = cell.id; }
            }
            return best;
        }

        /// <summary>
        /// 格显示文本：#31(60,110)。坐标=图内米（原点=地图西南角，X 东 Z 北，
        /// 与系统提示词约定一致；内部为世界坐标，显示时平移归一）。
        /// </summary>
        public string CellDisplay(int id1based)
        {
            if (id1based < 1 || id1based > cells.Count) return $"#{id1based}(?)";
            var cell = cells[id1based - 1];
            int ix = Mathf.RoundToInt(cell.center.x - bounds.min.x);
            int iz = Mathf.RoundToInt(cell.center.y - bounds.min.z);
            return $"#{cell.id}({ix},{iz})";
        }

        /// <summary>系统提示词用：全部可派驻格一行文本。</summary>
        public string BuildCellListText()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(CellDisplay(cells[i].id));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 解析格编号 token：#31 / 31 / 格31 / 格#31 → 编号 + 可驻世界点。
        /// </summary>
        public bool TryParseCellToken(string token, out int id, out Vector3 world)
        {
            id = 0;
            world = default;
            if (string.IsNullOrEmpty(token)) return false;
            string t = token.Trim().Replace("格", "").Replace("#", "").Trim();
            if (!int.TryParse(t, out int n)) return false;
            if (!TryGetCell(n, out world)) return false;
            id = n;
            return true;
        }

        // ---------------------------------------------------------------
        // Gizmos（编辑期可视化：绿=有效格，红=无效候选；#N 标注）
        // ---------------------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            EnsureBuilt();
            float y = bounds.max.y + 0.5f;

            foreach (var (c, valid) in candidates)
            {
                Gizmos.color = valid
                    ? new Color(0.2f, 0.9f, 0.3f, 0.85f)
                    : new Color(0.9f, 0.2f, 0.2f, 0.45f);
                var prev = Vector3.zero;
                for (int k = 0; k <= 6; k++)
                {
                    float a = Mathf.Deg2Rad * 60f * k;   // flat-top：顶点在东西
                    var p = new Vector3(c.x + hexRadius * Mathf.Cos(a), y,
                                        c.y + hexRadius * Mathf.Sin(a));
                    if (k > 0) Gizmos.DrawLine(prev, p);
                    prev = p;
                }
            }

#if UNITY_EDITOR
            var style = new GUIStyle(UnityEditor.EditorStyles.boldLabel)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
            };
            style.normal.textColor = Color.black;
            foreach (var cell in cells)
                UnityEditor.Handles.Label(
                    new Vector3(cell.center.x, y + 0.6f, cell.center.y),
                    $"#{cell.id}", style);
#endif
        }
    }
}
