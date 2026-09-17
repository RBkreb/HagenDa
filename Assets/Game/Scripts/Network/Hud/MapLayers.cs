using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// World-map layers (PHASE8). Two dedicated layers partition what each camera
    /// renders so the minimap / big map / main camera show exactly the right things:
    ///
    ///  - <see cref="Indicator"/> : per-entity coloured sphere (team/squad/mark),
    ///    seen ONLY by the minimap and big-map cameras.
    ///  - <see cref="Highlight"/> : GR / HQ ground highlight rings, seen by the main
    ///    camera (world) and the big map, but NOT the minimap (which shows only the
    ///    entity spheres).
    ///
    /// Layer indices are resolved by name at runtime so a scene still works after
    /// the layers are (re)assigned in TagManager. NetworkSetup.EnsureMapLayers writes
    /// them into TagManager when building scenes.
    /// </summary>
    public static class MapLayers
    {
        public const string IndicatorName = "MapIndicator";
        public const string HighlightName = "MapHighlight";

        /// <summary>
        /// HGTR (Blender map): walkable structure layer (floor / walls / cover) and
        /// the indoor ceiling. Top-down map cameras render Ground but exclude
        /// Ceiling so building interiors stay visible from above.
        /// </summary>
        public const string GroundName = "Ground";
        public const string CeilingName = "ceiling";

        /// <summary>
        /// Zone markers (据点/安全区填充盘 + 字母) — map cameras only, never the
        /// main camera. Outlines live on <see cref="ZoneOutlineName"/> (main view).
        /// </summary>
        public const string ZoneName = "MapZone";
        public const string ZoneOutlineName = "MapZoneOutline";

        /// <summary>Fixed world Y of the indicator spheres: the highest point of the
        /// map (6m perimeter wall) plus 5m.</summary>
        public const float IndicatorWorldY = 11f;

        /// <summary>Height of the minimap camera above the indicator layer (m).</summary>
        public const float MinimapHeight = 75f;

        /// <summary>Horizontal radius (m) captured by the minimap camera.</summary>
        public const float MinimapCoverage = 75f;

        /// <summary>Layer index for entity indicator spheres (-1 if not present).</summary>
        public static int Indicator => LayerMask.NameToLayer(IndicatorName);

        /// <summary>Layer index for GR/HQ ground highlight (-1 if not present).</summary>
        public static int Highlight => LayerMask.NameToLayer(HighlightName);

        /// <summary>Layer index for walkable map structure (floor/walls/cover).</summary>
        public static int Ground => LayerMask.NameToLayer(GroundName);

        /// <summary>Layer index for indoor ceilings (hidden from top-down map cameras).</summary>
        public static int Ceiling => LayerMask.NameToLayer(CeilingName);

        /// <summary>Layer index for zone marker fills (map cameras only).</summary>
        public static int Zone => LayerMask.NameToLayer(ZoneName);

        /// <summary>Layer index for zone outlines (main camera + map cameras).</summary>
        public static int ZoneOutline => LayerMask.NameToLayer(ZoneOutlineName);

        /// <summary>Layer mask containing only the entity indicator layer.</summary>
        public static int IndicatorMask => Indicator >= 0 ? 1 << Indicator : 0;

        /// <summary>Layer mask containing only the GR/HQ highlight layer.</summary>
        public static int HighlightMask => Highlight >= 0 ? 1 << Highlight : 0;

        /// <summary>Layer mask containing only the walkable map structure.</summary>
        public static int GroundMask => Ground >= 0 ? 1 << Ground : 0;

        /// <summary>Layer mask containing both map layers (used by the big-map camera).</summary>
        public static int MapMask => IndicatorMask | HighlightMask;

        /// <summary>Layer mask for zone marker fills (map cameras only).</summary>
        public static int ZoneMask => Zone >= 0 ? 1 << Zone : 0;

        /// <summary>Layer mask for zone outlines (main camera + map cameras).</summary>
        public static int ZoneOutlineMask => ZoneOutline >= 0 ? 1 << ZoneOutline : 0;

        /// <summary>
        /// Physics query mask that EXCLUDES static map geometry (Ground / ceiling /
        /// map overlay layers). Gameplay radius queries (capture / garrison / heal /
        /// explosion) must use this: the HGTR map has 700+ MeshColliders which would
        /// otherwise flood fixed-size OverlapNonAlloc buffers and drop combatants.
        /// </summary>
        public static int EntityQueryMask
        {
            get
            {
                int m = Physics.AllLayers & ~(1 << 2);   // default minus IgnoreRaycast
                if (Ground >= 0) m &= ~(1 << Ground);
                if (Ceiling >= 0) m &= ~(1 << Ceiling);
                if (Indicator >= 0) m &= ~(1 << Indicator);
                if (Highlight >= 0) m &= ~(1 << Highlight);
                if (Zone >= 0) m &= ~(1 << Zone);
                if (ZoneOutline >= 0) m &= ~(1 << ZoneOutline);
                int cmd = LayerMask.NameToLayer(CommanderMapName);
                if (cmd >= 0) m &= ~(1 << cmd);
                return m;
            }
        }

        /// <summary>CommanderMap layer name（PHASE10 快照遗留；PHASE11 起无消费者，仅为旧场景兼容保留）。</summary>
        public const string CommanderMapName = "CommanderMap";

        /// <summary>
        /// 合并 Ground 层全部渲染器的包围盒 = 地图 AABB。找不到 Ground 层或
        /// 该层无渲染器（非 HGTR 场景）返回 false，调用方走各自的旧兜底。
        /// </summary>
        public static bool TryGetMapBounds(out Bounds bounds)
        {
            bounds = default;
            int g = Ground;
            if (g < 0) return false;

            bool has = false;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r.gameObject.layer != g) continue;
                bounds = has ? Encapsulate(bounds, r.bounds) : r.bounds;
                has = true;
            }
            return has;
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            var min = Vector3.Min(a.min, b.min);
            var max = Vector3.Max(a.max, b.max);
            return new Bounds((min + max) * 0.5f, max - min);
        }

        public static bool Valid => Indicator >= 0 && Highlight >= 0;

        /// <summary>
        /// Put the "Visual" child (GR/HQ ground disc) onto the highlight layer so it
        /// shows up on the big map (and stays visible to the main camera), but stays
        /// off the minimap (which renders only entity indicators). No-op if the layer
        /// or child is missing.
        /// </summary>
        public static void ApplyHighlightLayer(Transform root)
        {
            if (root == null) return;
            if (Highlight < 0) return;

            var vis = root.Find("Visual");
            if (vis != null)
                vis.gameObject.layer = Highlight;
        }
    }
}
