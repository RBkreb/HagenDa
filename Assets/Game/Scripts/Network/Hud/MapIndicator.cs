using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// World-map indicator sphere (PHASE8). Attached to the player and AI prefabs;
    /// every entity carries one fixed-diameter sphere on the <see cref="MapLayers"/>
    /// indicator layer, rendered ONLY by the minimap / big-map cameras (the main
    /// camera excludes that layer).
    ///
    /// The sphere is positioned directly above the entity (X/Z follows, Y fixed at
    /// the world indicator height) and its colour / visibility follow the local
    /// player's perspective:
    ///
    ///   - enemy  → shown only while marked (red),
    ///   - same squad → green (50m on minimap, always on the big map),
    ///   - same team, other squad → team colour, 50m & unoccluded (minimap).
    ///
    /// This is a plain MonoBehaviour (client-side presentation): every networked
    /// entity runs it on every client and resolves its own appearance from that
    /// client's local player.
    /// </summary>
    public class MapIndicator : MonoBehaviour
    {
        public enum ViewMode { Minimap, BigMap }

        /// <summary>Active view mode shared by all indicators. The deploy screen
        /// switches this to <see cref="ViewMode.BigMap"/> while it is open.</summary>
        public static ViewMode Mode = ViewMode.Minimap;

        [Header("Indicator")]
        [Tooltip("Fixed sphere diameter (m). Every entity uses the same width.")]
        public float diameter = 4f;

        [Tooltip("How close (m) a friendly entity must be to appear on the minimap.")]
        public float friendlyRevealRadius = 50f;

        private Transform indicator;
        private Renderer indicatorRenderer;
        private MaterialPropertyBlock mpb;

        private NetworkCombatant combatant;
        private NetworkPlayerHealth health;

        private static readonly Color RedTeam = new Color(0.85f, 0.15f, 0.15f);
        private static readonly Color BlueTeam = new Color(0.15f, 0.35f, 0.85f);
        private static readonly Color SquadGreen = new Color(0.2f, 0.9f, 0.3f);
        private static readonly Color MarkedRed = new Color(1f, 0.1f, 0.1f);

        private void Awake()
        {
            combatant = GetComponent<NetworkCombatant>();
            health = GetComponent<NetworkPlayerHealth>();
            EnsureIndicator();
        }

        private void Update()
        {
            if (indicator == null) return;

            UpdatePosition();
            UpdateAppearance();
        }

        // ---------------------------------------------------------------
        // SETUP
        // ---------------------------------------------------------------

        private void EnsureIndicator()
        {
            int layer = MapLayers.Indicator;
            if (layer < 0)
            {
                Debug.LogWarning("[MapIndicator] 'MapIndicator' layer missing; indicator disabled.");
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "MapIndicator";
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * diameter;
            go.layer = layer;

            // No collision: this sphere is a pure visual marker.
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Unlit opaque material so the top-down map camera shows a flat, bright
            // dot regardless of lighting. HDRP uses HDRP/Unlit; built-in falls back.
            indicatorRenderer = go.GetComponent<Renderer>();
            if (indicatorRenderer != null)
            {
                Shader s = Shader.Find("HDRP/Unlit");
                if (s == null) s = Shader.Find("Sprites/Default");
                if (s == null) s = Shader.Find("Standard");
                if (s != null)
                {
                    var mat = new Material(s);
                    mat.SetColor("_BaseColor", Color.white);
                    mat.SetColor("_UnlitColor", Color.white);
                    mat.SetColor("_Color", Color.white);
                    indicatorRenderer.sharedMaterial = mat;
                }
            }

            indicator = go.transform;
            mpb = new MaterialPropertyBlock();
        }

        // ---------------------------------------------------------------
        // POSITION (X/Z follows, Y fixed)
        // ---------------------------------------------------------------

        private void UpdatePosition()
        {
            Vector3 p = transform.position;
            p.y = MapLayers.IndicatorWorldY;
            indicator.position = p;
        }

        // ---------------------------------------------------------------
        // APPEARANCE
        // ---------------------------------------------------------------

        private void UpdateAppearance()
        {
            bool visible = false;
            Color color = Color.white;

            var local = LocalCombatant();
            int myTeam = local != null ? local.teamId : -1;
            int mySquad = local != null ? local.squadId : -1;
            int team = combatant != null ? combatant.teamId : -1;

            bool dead = health != null && health.IsDead;
            bool isSelf = combatant != null && local != null && combatant == local;

            // Self: show on the big map (player's own position); hidden on the minimap.
            if (isSelf && !dead)
            {
                if (Mode == ViewMode.BigMap)
                {
                    visible = true;
                    color = SquadGreen;
                }
                Apply(visible, color);
                return;
            }

            if (!dead && !isSelf && team >= 0 && myTeam >= 0)
            {
                if (team != myTeam)
                {
                    // Enemy: shown only while marked.
                    if (combatant != null && combatant.IsMarked)
                    {
                        visible = true;
                        color = MarkedRed;
                    }
                }
                else
                {
                    bool sameSquad = combatant != null && combatant.squadId == mySquad;

                    if (Mode == ViewMode.BigMap)
                    {
                        // Big map: whole team visible (squad green, others team colour).
                        visible = true;
                        color = sameSquad ? SquadGreen : (team == (int)MatchTeam.Blue ? BlueTeam : RedTeam);
                    }
                    else
                    {
                        // 小地图是俯视视角，不做世界几何遮挡判定：
                        // 50m 内友方（含小队）均显示（小队绿 / 同队其他队伍色）。
                        float d = Vector3.Distance(transform.position, local.transform.position);
                        visible = d <= friendlyRevealRadius;
                        color = sameSquad ? SquadGreen : (team == (int)MatchTeam.Blue ? BlueTeam : RedTeam);
                    }
                }
            }

            Apply(visible, color);
        }

        private NetworkCombatant LocalCombatant()
        {
            if (!Mirror.NetworkClient.active) return null;
            var id = Mirror.NetworkClient.localPlayer;
            if (id == null) return null;
            return id.GetComponent<NetworkCombatant>();
        }

        /// <summary>True when the entity's indicator is hidden behind world geometry
        /// from the local player (line of sight). Not used for squad members.</summary>
        private bool Occluded(NetworkCombatant local)
        {
            if (local == null) return true;
            Vector3 a = transform.position + Vector3.up * 1.5f;
            Vector3 b = local.transform.position + Vector3.up * 1.5f;
            float dist = Vector3.Distance(a, b);
            if (dist <= 0.0001f) return false;

            return Physics.Raycast(a, (b - a) / dist, dist,
                                   Physics.DefaultRaycastLayers,
                                   QueryTriggerInteraction.Ignore);
        }

        private void Apply(bool visible, Color color)
        {
            if (indicatorRenderer != null)
                indicatorRenderer.enabled = visible;

            if (indicatorRenderer == null || !visible) return;

            indicatorRenderer.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", color);
            mpb.SetColor("_UnlitColor", color);
            mpb.SetColor("_Color", color);
            indicatorRenderer.SetPropertyBlock(mpb);
        }
    }
}
