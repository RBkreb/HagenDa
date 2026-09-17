using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// World-space head marker (PHASE7 spec, PHASE8 completion). A small bright dot
    /// 0.1m above the entity's head, visible in the FIRST-PERSON world view (unlike
    /// <see cref="MapIndicator"/> which only the map cameras see):
    ///
    ///   - marked enemy   → red dot, hidden behind geometry (does NOT pierce),
    ///   - squad member   → green dot within 50m, PIERCES occlusion,
    ///   - friendly other squad → blue dot within 50m & unoccluded.
    ///
    /// Client-side presentation only. Occlusion is a raycast from the local player's
    /// eye to the dot; the green squad dot uses a ZTest-Always material so it stays
    /// visible through walls.
    /// </summary>
    public class HeadMarker : MonoBehaviour
    {
        [Header("Marker")]
        [Tooltip("Dot diameter (m).")]
        public float diameter = 0.15f;
        [Tooltip("Distance above the capsule top (m).")]
        public float headOffset = 0.1f;
        [Tooltip("Friendly reveal radius (m).")]
        public float revealRadius = 50f;

        private Transform dot;
        private Renderer dotRenderer;
        private Material normalMat;    // depth-tested (red / blue)
        private Material pierceMat;    // ZTest Always (green squad)

        private NetworkCombatant combatant;
        private NetworkPlayerHealth health;

        private static readonly Color MarkedRed = new Color(1f, 0.1f, 0.1f);
        private static readonly Color SquadGreen = new Color(0.2f, 0.9f, 0.3f);
        private static readonly Color TeamBlue = new Color(0.2f, 0.5f, 1f);
        private static readonly Color TeamRed = new Color(0.9f, 0.2f, 0.2f);

        private void Awake()
        {
            combatant = GetComponent<NetworkCombatant>();
            health = GetComponent<NetworkPlayerHealth>();
            EnsureDot();
        }

        private void Update()
        {
            if (dot == null) return;
            UpdatePosition();
            UpdateAppearance();
        }

        private void EnsureDot()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "HeadMarker";
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * diameter;

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            dot = go.transform;
            dotRenderer = go.GetComponent<Renderer>();
            if (dotRenderer == null) return;

            Shader s = Shader.Find("HDRP/Unlit");
            if (s == null) s = Shader.Find("Sprites/Default");
            if (s == null) s = Shader.Find("Standard");
            if (s == null) return;

            normalMat = new Material(s);
            normalMat.SetColor("_BaseColor", Color.white);
            normalMat.SetColor("_UnlitColor", Color.white);
            normalMat.SetColor("_Color", Color.white);
            normalMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            normalMat.SetInt("_ZWrite", 0);

            pierceMat = new Material(normalMat);
            pierceMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);

            dotRenderer.sharedMaterial = normalMat;
        }

        private void UpdatePosition()
        {
            Vector3 p = HeadWorldPosition();
            p.y += headOffset;
            dot.position = p;
        }

        private Vector3 HeadWorldPosition()
        {
            // 头顶正中：AABB 的 X/Z 取中心、Y 取顶部（bounds.max 是角点，直接使用会偏）。
            var cap = health != null ? health.GetActiveCapsule() : GetComponent<CapsuleCollider>();
            if (cap != null)
            {
                var b = cap.bounds;
                return new Vector3(b.center.x, b.max.y, b.center.z);
            }
            return transform.position + Vector3.up * 1.8f;
        }

        private void UpdateAppearance()
        {
            bool visible = false;
            Color color = Color.white;
            bool pierce = false;

            var local = LocalCombatant();
            int myTeam = local != null ? local.teamId : -1;
            int mySquad = local != null ? local.squadId : -1;
            int team = combatant != null ? combatant.teamId : -1;

            bool dead = health != null && health.IsDead;
            bool isSelf = combatant != null && local != null && combatant == local;

            if (!dead && !isSelf && team >= 0 && myTeam >= 0)
            {
                if (team != myTeam)
                {
                    // 敌方：仅被标记显示红点，不可穿过遮挡。
                    if (combatant != null && combatant.IsMarked && !Occluded(local))
                    {
                        visible = true;
                        color = MarkedRed;
                    }
                }
                else if (combatant != null && combatant.squadId == mySquad)
                {
                    // 小队成员：50m 内绿点，可穿过遮挡。
                    float d = Vector3.Distance(transform.position, local.transform.position);
                    if (d <= revealRadius)
                    {
                        visible = true;
                        color = SquadGreen;
                        pierce = true;
                    }
                }
                else
                {
                    // 友方非同小队：50m 内无遮挡蓝点。
                    float d = Vector3.Distance(transform.position, local.transform.position);
                    if (d <= revealRadius && !Occluded(local))
                    {
                        visible = true;
                        color = team == (int)MatchTeam.Blue ? TeamBlue : TeamRed;
                    }
                }
            }

            if (dotRenderer == null) return;
            dotRenderer.enabled = visible;
            if (!visible) return;

            dotRenderer.sharedMaterial = pierce ? pierceMat : normalMat;
            dotRenderer.material.SetColor("_BaseColor", color);
            dotRenderer.material.SetColor("_UnlitColor", color);
            dotRenderer.material.SetColor("_Color", color);
        }

        private NetworkCombatant LocalCombatant()
        {
            if (!Mirror.NetworkClient.active) return null;
            var id = Mirror.NetworkClient.localPlayer;
            if (id == null) return null;
            return id.GetComponent<NetworkCombatant>();
        }

        /// <summary>True when the dot is hidden behind world geometry from the local player.</summary>
        private bool Occluded(NetworkCombatant local)
        {
            if (local == null) return true;

            Vector3 eye = local.transform.position + Vector3.up * 1.6f;
            Vector3 target = dot != null ? dot.position : transform.position + Vector3.up * 1.8f;
            Vector3 dir = target - eye;
            float dist = dir.magnitude;
            if (dist <= 0.0001f) return false;

            if (Physics.Raycast(eye, dir / dist, out RaycastHit hit, dist,
                                Physics.DefaultRaycastLayers,
                                QueryTriggerInteraction.Ignore))
            {
                // 忽略自己与目标实体自身的碰撞体。
                var hitHealth = hit.collider.GetComponentInParent<NetworkPlayerHealth>();
                if (hitHealth != null && (hitHealth == health || hitHealth == local.GetComponent<NetworkPlayerHealth>()))
                    return false;
                return true;
            }
            return false;
        }
    }
}
