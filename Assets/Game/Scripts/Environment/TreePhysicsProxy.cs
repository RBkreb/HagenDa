using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Environment
{
    /// <summary>
    /// Streams PhysX colliders for terrain-painted tree/rock instances around the active
    /// camera, keeping classic Physics.Raycast gameplay (hitscan, movement) fully working
    /// while the instances themselves render through the Terrain system.
    /// Trees/stumps (prototypeIndex < treePrototypeCount) get capsule trunk colliders;
    /// rocks get box colliders. Budgeted per frame; colliders outside the radius are recycled.
    /// </summary>
    [DisallowMultipleComponent]
    public class TreePhysicsProxy : MonoBehaviour
    {
        public Terrain terrain;
        public float radius = 150f;
        public int buildBudgetPerFrame = 800;
        public float refreshInterval = 0.25f;
        public float trunkRadius = 0.35f;

        struct Inst { public Vector3 pos; public float scale; public byte kind; public float r, h, cy; }

        readonly List<Inst> _all = new List<Inst>();
        readonly Dictionary<int, GameObject> _live = new Dictionary<int, GameObject>();
        readonly Queue<int> _pending = new Queue<int>();
        Transform _root;
        float _nextRefresh;
        int _builtTotal;

        void Start()
        {
            if (terrain == null) terrain = FindObjectOfType<Terrain>();
            if (terrain == null) return;
            var td = terrain.terrainData;
            var sr = terrain.transform.position;
            int treeProtoCount = 0;
            foreach (var tp in td.treePrototypes)
            {
                var n = tp.prefab != null ? tp.prefab.name : "";
                if (n.StartsWith("Tree_") || n.StartsWith("Stump_")) treeProtoCount++;
            }
            foreach (var ti in td.treeInstances)
            {
                var wp = new Vector3(ti.position.x * td.size.x + sr.x,
                                     ti.position.y * td.size.y + sr.y,
                                     ti.position.z * td.size.z + sr.z);
                bool isTree = ti.prototypeIndex < treeProtoCount;
                var proto = td.treePrototypes[ti.prototypeIndex].prefab;
                var rends = proto.GetComponentsInChildren<Renderer>();
                var b = new Bounds(); bool has = false;
                foreach (var r in rends) { if (has) b.Encapsulate(r.bounds); else { b = r.bounds; has = true; } }
                _all.Add(new Inst
                {
                    pos = wp,
                    scale = ti.widthScale,
                    kind = (byte)(isTree ? 0 : 1),
                    r = isTree ? trunkRadius : Mathf.Max(b.extents.x, b.extents.z) * 0.85f,
                    h = b.size.y,
                    cy = isTree ? b.min.y + b.size.y * 0.45f : b.min.y + b.extents.y
                });
            }
            _root = new GameObject("TreePhysicsProxyPool").transform;
            for (int i = 0; i < _all.Count; i++) _pending.Enqueue(i);
            Debug.Log($"[TreePhysicsProxy] instances={_all.Count} (trees+stumps<=idx{treeProtoCount - 1}, rocks after)");
        }

        void Update()
        {
            if (terrain == null || _all.Count == 0) return;
            var cam = Camera.main;
            if (cam == null) return;
            var c = cam.transform.position;

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + refreshInterval;
                float r2 = radius * radius, cull2 = (radius + 25f) * (radius + 25f);
                var dead = new List<int>();
                foreach (var kv in _live)
                {
                    if ((kv.Value.transform.position - c).sqrMagnitude > cull2) dead.Add(kv.Key);
                }
                foreach (var k in dead)
                {
                    Destroy(_live[k]);
                    _live.Remove(k);
                    _pending.Enqueue(k);
                }
            }

            int budget = buildBudgetPerFrame;
            while (budget > 0 && _pending.Count > 0)
            {
                int i = _pending.Dequeue();
                var inst = _all[i];
                if ((inst.pos - c).sqrMagnitude > radius * radius) continue; // skip, rebuild later
                var go = new GameObject($"PhysProxy_{i}");
                go.transform.SetParent(_root, false);
                go.transform.position = inst.pos;
                if (inst.kind == 0)
                {
                    var cap = go.AddComponent<CapsuleCollider>();
                    cap.radius = inst.r * inst.scale;
                    cap.height = Mathf.Max(inst.h * inst.scale, cap.radius * 2f);
                    cap.center = new Vector3(0f, inst.cy * inst.scale, 0f);
                }
                else
                {
                    var box = go.AddComponent<BoxCollider>();
                    box.size = new Vector3(inst.r * 2f * inst.scale, inst.h * inst.scale, inst.r * 2f * inst.scale);
                    box.center = new Vector3(0f, inst.cy * inst.scale, 0f);
                }
                _live[i] = go;
                budget--;
                _builtTotal++;
            }
            // requeue skipped instances lazily (they were dequeued): rebuild queue when drained
            if (_pending.Count == 0 && _live.Count + _builtTotal < _all.Count * 2)
            {
                for (int i = 0; i < _all.Count; i++)
                    if (!_live.ContainsKey(i)) _pending.Enqueue(i);
                _builtTotal = 0;
            }
        }
    }
}