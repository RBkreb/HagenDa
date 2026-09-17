using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem.UI;
using Unity.AI.Navigation;
using HagenDa.Animation.RigGraph;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// NetworkSetup (partial) — Generic scene primitives: spawns, walls, lighting, zones, map geometry and NavMesh.
    /// </summary>
    public static partial class NetworkSetup
    {
                private static void BuildNavMeshOnFloor(string floorName)
                {
                    var floor = GameObject.Find(floorName);
                    if (floor == null)
                    {
                        Debug.LogWarning("[NetworkSetup] No Floor found; skipping NavMesh build.");
                        return;
                    }
                    var surface = floor.GetComponent<NavMeshSurface>();
                    if (surface == null)
                        surface = floor.AddComponent<NavMeshSurface>();
                    surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                    surface.collectObjects = CollectObjects.Children;
                    surface.BuildNavMesh();
                    Debug.Log("[NetworkSetup] NavMesh built on " + floorName + ".");
                }

                internal static void CreateStrategicZone(string name, CapturePoint cp)
                {
                    var go = new GameObject(name);
                    go.transform.position = cp.transform.position;
                    var z = go.AddComponent<StrategicZone>();
                    z.capturePoint = cp;
                }

                internal static void EnableBackfaceQueries()
                {
                    if (!Physics.queriesHitBackfaces)
                        Physics.queriesHitBackfaces = true;

                    // 持久化到 PhysicsSettings（防止域重载/重启动回退到默认值）。
                    var physicsAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/PhysicsSettings.asset");
                    var settings = physicsAsset != null && physicsAsset.Length > 0 ? physicsAsset[0] : null;
                    if (settings != null)
                    {
                        var so = new SerializedObject(settings);
                        var prop = so.FindProperty("m_QueriesHitBackfaces");
                        if (prop != null && !prop.boolValue)
                        {
                            prop.boolValue = true;
                            so.ApplyModifiedProperties();
                            Debug.Log("[NetworkSetup] Physics: Queries Hit Backfaces ENABLED (single-sided Blender walls).");
                        }
                    }
                }

                internal static void EnsureEventSystem()
                {
                    var es = Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>();
                    if (es != null && es.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() != null)
                        return;

                    if (es == null)
                    {
                        var go = new GameObject("EventSystem");
                        es = go.AddComponent<UnityEngine.EventSystems.EventSystem>();
                    }
                    if (es.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() == null)
                        es.gameObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                    Debug.Log("[NetworkSetup] EventSystem + InputSystemUIInputModule ensured.");
                }

                internal static void EnsureMapColliders()
                {
                    var mapRoot = GameObject.Find(HGTRMapRootName);
                    if (mapRoot == null) return;

                    int added = 0, markedStatic = 0;
                    foreach (var mf in mapRoot.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh == null) continue;
                        if (mf.GetComponent<MeshCollider>() == null)
                        {
                            mf.gameObject.AddComponent<MeshCollider>();
                            added++;
                        }
                        if (!mf.gameObject.isStatic)
                        {
                            mf.gameObject.isStatic = true;
                            markedStatic++;
                        }
                    }
                    if (added > 0 || markedStatic > 0)
                        Debug.Log($"[NetworkSetup] Map colliders: +{added} MeshColliders, {markedStatic} objects marked static.");
                }

                private static Vector3 MinX(Vector3 a, Vector3 b, Vector3 c)
                    => a.x <= b.x && a.x <= c.x ? a : (b.x <= c.x ? b : c);

                internal static void EnableMapMaterialsDoubleSided()
                {
                    const string fbxPath = "Assets/Game/Environment/HGTR_map.fbx";
                    int fixedCount = 0;
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                    {
                        if (asset is Material m && m.HasProperty("_DoubleSidedEnable")
                                               && m.GetFloat("_DoubleSidedEnable") < 0.5f)
                        {
                            m.SetFloat("_DoubleSidedEnable", 1f);
                            if (m.HasProperty("_DoubleSidedNormalMode"))
                                m.SetFloat("_DoubleSidedNormalMode", 0f);   // Flip
                            if (m.HasProperty("_CullMode"))
                                m.SetFloat("_CullMode", 0f);                // None (HDRP: 0=None,1=Front,2=Back)
                            EditorUtility.SetDirty(m);
                            fixedCount++;
                        }
                    }
                    if (fixedCount > 0)
                    {
                        AssetDatabase.SaveAssets();
                        Debug.Log($"[NetworkSetup] Enabled double-sided rendering on {fixedCount} map materials (interior walls visible).");
                    }
                }

                private static Vector3 MaxX(Vector3 a, Vector3 b, Vector3 c)
                    => a.x >= b.x && a.x >= c.x ? a : (b.x >= c.x ? b : c);

                internal static void ClearAllFSMEntities()
                {
                    foreach (var fsm in Object.FindObjectsOfType<FSMAIController>(true))
                        Object.DestroyImmediate(fsm.gameObject);
                }

                private static Bounds ChamberRect(string namePrefix)
                {
                    var mapRoot = GameObject.Find(HGTRMapRootName);
                    string lower = namePrefix.ToLowerInvariant();
                    Bounds? combined = null;
                    if (mapRoot != null)
                        foreach (var r in mapRoot.GetComponentsInChildren<Renderer>())
                        {
                            if (!r.name.ToLowerInvariant().StartsWith(lower)) continue;
                            combined = combined.HasValue
                                ? EncapsulateXZ(combined.Value, r.bounds)
                                : r.bounds;
                        }
                    if (combined.HasValue) return combined.Value;

                    Debug.LogWarning($"[NetworkSetup] ChamberRect '{namePrefix}' not found; fallback 20x20 at origin.");
                    return new Bounds(Vector3.zero, new Vector3(20f, 0f, 20f));
                }

                private static Bounds EncapsulateXZ(Bounds a, Bounds b)
                {
                    float minX = Mathf.Min(a.min.x, b.min.x), maxX = Mathf.Max(a.max.x, b.max.x);
                    float minZ = Mathf.Min(a.min.z, b.min.z), maxZ = Mathf.Max(a.max.z, b.max.z);
                    float minY = Mathf.Min(a.min.y, b.min.y), maxY = Mathf.Max(a.max.y, b.max.y);
                    return new Bounds(
                        new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f),
                        new Vector3(maxX - minX, maxY - minY, maxZ - minZ));
                }

                private static Bounds ShiftBounds(Bounds b, Vector3 offset)
                    => new Bounds(b.center + offset, b.size);

                private static Vector3 GroupCenter(string namePrefix)        {
                    var mapRoot = GameObject.Find(HGTRMapRootName);
                    if (mapRoot != null)
                    {
                        Bounds? combined = null;
                        string lower = namePrefix.ToLowerInvariant();
                        foreach (var r in mapRoot.GetComponentsInChildren<Renderer>())
                        {
                            if (!r.name.ToLowerInvariant().StartsWith(lower)) continue;
                            combined = combined.HasValue
                                ? new Bounds(
                                    (combined.Value.center + r.bounds.center) * 0.5f,
                                    Vector3.Max(combined.Value.extents, r.bounds.extents) * 2f)
                                : r.bounds;
                        }
                        if (combined.HasValue)
                            return new Vector3(combined.Value.center.x, 0f, combined.Value.center.z);
                    }
                    Debug.LogWarning($"[NetworkSetup] GroupCenter '{namePrefix}' not found; fallback to origin.");
                    return Vector3.zero;
                }

                internal static void ClearOld(params string[] names)
                {
                    foreach (var n in names)
                    {
                        var go = GameObject.Find(n);
                        if (go != null) Object.DestroyImmediate(go);
                    }
                }

                internal static void AttachDeployPointTemplate(GameObject owner, int count, float radius)
                {
                    // 清掉旧模板（幂等）。
                    foreach (Transform child in owner.transform)
                        if (child.name == "DeployPointSet")
                            Object.DestroyImmediate(child.gameObject);

                    var setGo = new GameObject("DeployPointSet");
                    setGo.transform.SetParent(owner.transform, false);
                    var set = setGo.AddComponent<DeployPointSet>();
                    for (int i = 0; i < count; i++)
                    {
                        var dp = new GameObject($"DP_{i}");
                        dp.transform.SetParent(setGo.transform, false);
                        float a = (i / (float)count) * Mathf.PI * 2f;
                        dp.transform.localPosition = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                    }
                    // 编辑期直接注册（运行时 Awake 也会兜底注册）。
                    set.points.Clear();
                    for (int i = 0; i < setGo.transform.childCount; i++)
                        set.points.Add(setGo.transform.GetChild(i));

                    var gz = owner.GetComponent<GarrisonZone>();
                    if (gz != null) gz.deployPoints = new List<Transform>(set.points);
                    var cp = owner.GetComponent<CapturePoint>();
                    if (cp != null) cp.deployPoints = new List<Transform>(set.points);
                }

                internal static GarrisonZone CreateGarrison(string name, Vector3 pos, int teamId, float radius)
                {
                    var go = new GameObject(name);
                    go.transform.position = pos;
                    go.AddComponent<NetworkIdentity>();
                    var gz = go.AddComponent<GarrisonZone>();
                    gz.teamId = teamId;
                    gz.radius = radius;

                    // Visual: a large transparent cylinder to show the zone boundary.
                    var vis = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    vis.name = "Visual";
                    vis.transform.SetParent(go.transform, false);
                    vis.transform.localPosition = Vector3.zero;
                    vis.transform.localScale = new Vector3(radius * 2f, 0.01f, radius * 2f);
                    Object.DestroyImmediate(vis.GetComponent<Collider>());
                    var rend = vis.GetComponent<Renderer>();
                    if (rend != null)
                    {
                        var mat = CreatePersistentMaterial(
                            "Assets/Game/Materials/GarrisonZone.mat",
                            teamId == (int)MatchTeam.Red ? new Color(0.6f, 0.1f, 0.1f, 0.15f) : new Color(0.1f, 0.2f, 0.6f, 0.15f));
                        if (mat != null) rend.sharedMaterial = mat;
                    }

                    // Deploy points: 3 points around the garrison center.
                    for (int i = 0; i < 3; i++)
                    {
                        var dp = new GameObject($"DeployPoint_{i}");
                        dp.transform.SetParent(go.transform, false);
                        float a = (i / 3f) * Mathf.PI * 2f;
                        dp.transform.localPosition = new Vector3(Mathf.Cos(a) * 3f, 0f, Mathf.Sin(a) * 3f);
                        gz.deployPoints.Add(dp.transform);
                    }

                    return gz;
                }

                internal static CapturePoint CreateCapturePoint(string name, Vector3 pos, float radius)
                {
                    var go = new GameObject(name);
                    go.transform.position = pos;
                    go.AddComponent<NetworkIdentity>();
                    var cp = go.AddComponent<CapturePoint>();
                    cp.radius = radius;

                    // Visual: a flat ring on the ground.
                    var vis = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    vis.name = "Visual";
                    vis.transform.SetParent(go.transform, false);
                    vis.transform.localPosition = Vector3.zero;
                    vis.transform.localScale = new Vector3(radius * 2f, 0.01f, radius * 2f);
                    Object.DestroyImmediate(vis.GetComponent<Collider>());
                    var rend = vis.GetComponent<Renderer>();
                    if (rend != null)
                    {
                        var mat = CreatePersistentMaterial(
                            "Assets/Game/Materials/CapturePoint.mat",
                            new Color(0.8f, 0.8f, 0.2f, 0.15f));
                        if (mat != null) rend.sharedMaterial = mat;
                    }

                    // Deploy points: 2 points on opposite sides of the HQ.
                    var dp1 = new GameObject("DeployPoint_A");
                    dp1.transform.SetParent(go.transform, false);
                    dp1.transform.localPosition = new Vector3(radius * 0.7f, 0f, 0f);
                    cp.deployPoints.Add(dp1.transform);

                    var dp2 = new GameObject("DeployPoint_B");
                    dp2.transform.SetParent(go.transform, false);
                    dp2.transform.localPosition = new Vector3(-radius * 0.7f, 0f, 0f);
                    cp.deployPoints.Add(dp2.transform);

                    return cp;
                }

                internal static NetworkTransformReliable AddNetworkTransform(GameObject go, SyncDirection direction, bool syncRotation)
                {
                    NetworkTransformReliable nt = null;
                    try
                    {
                        nt = go.AddComponent<NetworkTransformReliable>();
                    }
                    catch (System.NullReferenceException)
                    {
                        nt = go.GetComponent<NetworkTransformReliable>();
                    }

                    if (nt == null) return null;

                    nt.target = go.transform;
                    nt.syncDirection = direction;
                    nt.syncPosition = true;
                    nt.syncRotation = syncRotation;
                    nt.interpolatePosition = true;
                    nt.interpolateRotation = syncRotation;
                    return nt;
                }

                private static void SetupScene(GameObject playerPrefab, bool addTargets = false)
                {
                    // Find or create the NetworkManager (idempotent).
                    var nm = Object.FindObjectOfType<NetworkManager>();
                    if (nm == null)
                    {
                        var nmGo = new GameObject("NetworkManager");
                        nm = nmGo.AddComponent<NetworkManager>();
                        var kcp = nmGo.AddComponent<kcp2k.KcpTransport>();
                        nm.transport = kcp;
                        nmGo.AddComponent<NetworkManagerHUD>();
                    }

                    // Always wire the player prefab (this is the whole point).
                    nm.playerPrefab = playerPrefab;
                    nm.autoCreatePlayer = true;

                    // Register any throwable prefabs the player's combat references, so
                    // throwing works in every scene (not just Phase3).
                    var combat = playerPrefab != null ? playerPrefab.GetComponent<NetworkCombat>() : null;
                    if (combat != null)
                    {
                        RegisterSpawnPrefabs(
                            combat.grenadeThrowablePrefab != null ? combat.grenadeThrowablePrefab.gameObject : null,
                            combat.smokeThrowablePrefab != null ? combat.smokeThrowablePrefab.gameObject : null,
                            combat.rescueThrowablePrefab != null ? combat.rescueThrowablePrefab.gameObject : null);
                    }

                    // Spawn points (only if none exist).
                    if (Object.FindObjectsOfType<NetworkStartPosition>().Length == 0)
                    {
                        CreateSpawnPoint("Spawn A", new Vector3(-5f, 1f, 0f));
                        CreateSpawnPoint("Spawn B", new Vector3(5f, 1f, 0f));
                    }

                    // Floor (only if none exists).
                    if (GameObject.Find("Floor") == null)
                    {
                        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                        floor.name = "Floor";
                        floor.transform.position = Vector3.zero;
                        floor.transform.localScale = new Vector3(10f, 1f, 10f);
                    }

                    // Shootable targets.
                    if (addTargets)
                    {
                        CreateTarget(new Vector3(0f, 1f, 10f));
                        CreateTarget(new Vector3(-8f, 1f, 10f));
                        CreateTarget(new Vector3(8f, 1f, 10f));
                    }

                    // EventSystem (required by FPS Engine UI / PauseMenu).
                    if (Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
                    {
                        var esGo = new GameObject("EventSystem");
                        esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                        esGo.AddComponent<InputSystemUIInputModule>();
                    }

                    // Remove any non-networked camera in the scene (the player brings its own).
                    foreach (var cam in Object.FindObjectsOfType<Camera>())
                    {
                        if (cam.GetComponentInParent<NetworkIdentity>() == null)
                        {
                            Object.DestroyImmediate(cam.gameObject);
                        }
                    }

                    MarkSceneDirty();
                }

                private static void CreateSpawnPoint(string name, Vector3 pos)
                {
                    var go = new GameObject(name);
                    go.transform.position = pos;
                    go.AddComponent<NetworkStartPosition>();
                }

                private static void CreateTarget(Vector3 pos)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = "ShootableTarget";
                    go.transform.position = pos;
                    go.transform.localScale = new Vector3(1f, 2f, 1f);
                    go.AddComponent<NetworkIdentity>();
                    go.AddComponent<NetworkShootableTarget>();
                }

                private static void CreateAIEntity(GameObject aiPrefab, Vector3 pos)
                {
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(aiPrefab);
                    go.name = "AIEntity";
                    go.transform.position = pos;
                }

                internal static void RegisterSpawnPrefabs(params GameObject[] prefabs)
                {
                    var nm = Object.FindObjectOfType<NetworkManager>();
                    if (nm == null) return;

                    // Ensure spawnPrefabs list exists and is unique.
                    if (nm.spawnPrefabs == null)
                        nm.spawnPrefabs = new List<GameObject>();

                    foreach (var p in prefabs)
                    {
                        if (p == null) continue;
                        if (!nm.spawnPrefabs.Contains(p))
                            nm.spawnPrefabs.Add(p);
                    }

                    EditorUtility.SetDirty(nm);
                }

                internal static void BuildNavMeshForFloor()
                {
                    var floor = GameObject.Find("Floor");
                    if (floor == null)
                    {
                        Debug.LogWarning("[NetworkSetup] No Floor found; skipping NavMesh build.");
                        return;
                    }

                    var surface = floor.GetComponent<NavMeshSurface>();
                    if (surface == null)
                        surface = floor.AddComponent<NavMeshSurface>();

                    surface.collectObjects = CollectObjects.All;
                    surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                    surface.BuildNavMesh();

                    Debug.Log("[NetworkSetup] NavMesh built on Floor.");
                }

                internal static void BuildNavMeshForMapRoot(string mapRootName)
                {
                    var mapRoot = GameObject.Find(mapRootName);
                    if (mapRoot == null)
                    {
                        Debug.LogWarning($"[NetworkSetup] Map root '{mapRootName}' not found; skipping NavMesh build.");
                        return;
                    }

                    // 临时隐藏天花板（避免烘出屋顶可行走层），烘完恢复。
                    var ceilings = new List<Renderer>();
                    foreach (var r in mapRoot.GetComponentsInChildren<Renderer>(true))
                        if (r.name.ToLowerInvariant().Contains("ceiling"))
                            ceilings.Add(r);
                    foreach (var r in ceilings) r.gameObject.SetActive(false);

                    try
                    {
                        var surface = mapRoot.GetComponent<NavMeshSurface>();
                        if (surface == null)
                            surface = mapRoot.AddComponent<NavMeshSurface>();

                        surface.collectObjects = CollectObjects.All;
                        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                        surface.BuildNavMesh();

                        Debug.Log($"[NetworkSetup] NavMesh built on '{mapRootName}' ({ceilings.Count} ceilings excluded).");
                    }
                    finally
                    {
                        foreach (var r in ceilings) r.gameObject.SetActive(true);
                    }
                }

                private static void RemoveMissingScripts(GameObject root)
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                    }
                }

                internal static void CreateWall(Vector3 pos, Vector3 scale)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = "Wall";
                    go.transform.position = pos;
                    go.transform.localScale = scale;
                }

                internal static void EnsureLighting()
                {
                    if (Object.FindObjectOfType<Light>() != null) return;

                    var lightGo = new GameObject("Directional Light");
                    var light = lightGo.AddComponent<Light>();
                    light.type = LightType.Directional;

                    var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                    bool hdrp = pipeline != null &&
                        (pipeline.GetType().FullName.Contains("HighDefinition") ||
                         pipeline.GetType().Name.Contains("HDRenderPipeline"));
                    light.intensity = hdrp ? 10000f : 1f;

                    lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                    // Best-effort: attach HDRP additional light data if the HDRP assembly is
                    // present (avoid a hard compile-time dependency on the HDRP package).
                    var hdType = System.Type.GetType(
                        "UnityEngine.Rendering.HighDefinition.HDAdditionalLightData, Unity.RenderPipelines.HighDefinition.Runtime");
                    if (hdType != null && lightGo.GetComponent(hdType) == null)
                        lightGo.AddComponent(hdType);
                }
    }
}
