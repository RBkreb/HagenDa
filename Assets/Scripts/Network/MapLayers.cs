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

        /// <summary>Layer mask containing only the entity indicator layer.</summary>
        public static int IndicatorMask => Indicator >= 0 ? 1 << Indicator : 0;

        /// <summary>Layer mask containing only the GR/HQ highlight layer.</summary>
        public static int HighlightMask => Highlight >= 0 ? 1 << Highlight : 0;

        /// <summary>Layer mask containing both map layers (used by the big-map camera).</summary>
        public static int MapMask => IndicatorMask | HighlightMask;

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
