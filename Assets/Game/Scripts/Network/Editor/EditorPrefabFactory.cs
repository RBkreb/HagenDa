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
    /// NetworkSetup (partial) — Prefab builders: player, AI entities, bullet, throwables and deployables.
    /// </summary>
    public static partial class NetworkSetup
    {
                private static GameObject BuildScriptedAIPrefab(GameObject aiPrefab)
                {
                    EnsureFolder("Assets/Game", "Prefabs");
                    const string path = "Assets/Game/Prefabs/ScriptedAIEntity.prefab";

                    var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var root = existing != null
                        ? (GameObject)PrefabUtility.InstantiatePrefab(existing)
                        : (GameObject)PrefabUtility.InstantiatePrefab(aiPrefab);
                    root.name = "ScriptedAIEntity";

                    // 脚本陪练：无 ML 桥（不采样），加 NavMesh 驱动状态机。
                    var bridge = root.GetComponent<MLAgentBridge>();
                    if (bridge != null) Object.DestroyImmediate(bridge);
                    var sensor = root.GetComponent<AgentRaySensor>();
                    if (sensor != null) Object.DestroyImmediate(sensor);

                    if (root.GetComponent<ScriptedAIController>() == null)
                        root.AddComponent<ScriptedAIController>();

                    var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                internal static GameObject BuildFSMAIPrefab(GameObject aiPrefab)
                {
                    EnsureFolder("Assets/Game", "Prefabs");

                    var existing = AssetDatabase.LoadAssetAtPath<GameObject>(FSMAIPrefabPath);
                    var root = existing != null
                        ? (GameObject)PrefabUtility.InstantiatePrefab(existing)
                        : (GameObject)PrefabUtility.InstantiatePrefab(aiPrefab);
                    root.name = "FSMAIEntity";

                    // FSM：无 ML 桥（不进采样），大脑走 intent 管线 + 批感知。
                    var bridge = root.GetComponent<MLAgentBridge>();
                    if (bridge != null) Object.DestroyImmediate(bridge);

                    // 视觉重构：FSM 用方体查询 + 遮挡射线传感器，替换 ML 专用射线扇传感器。
                    if (root.GetComponent<AgentVisionSensor>() == null)
                        root.AddComponent<AgentVisionSensor>();
                    var raySensor = root.GetComponent<AgentRaySensor>();
                    if (raySensor != null) Object.DestroyImmediate(raySensor);

                    if (root.GetComponent<FSMAIController>() == null)
                        root.AddComponent<FSMAIController>();

                    var prefab = PrefabUtility.SaveAsPrefabAsset(root, FSMAIPrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                internal static GameObject BuildPlayerPrefab()
                {
                    return BuildPlayerPrefab(null, null, null, null);
                }

                internal static GameObject BuildPlayerPrefab(GameObject grenadePrefab, GameObject smokePrefab, GameObject rescuePrefab, GameObject bulletPrefab, List<EquipmentDefinition> equipmentList = null)
                {
                    var root = new GameObject("NetworkPlayer");

                    // Networking
                    root.AddComponent<NetworkIdentity>();

                    AddNetworkTransform(root, SyncDirection.ServerToClient, syncRotation: true);

                    // Physics: force-driven capsule rigidbody (NOT Character Controller).
                    // Capsule height 1.8m, radius 0.25m, center at half-height so the base sits at y=0.
                    var rb = root.AddComponent<Rigidbody>();
                    rb.freezeRotation = true;
                    rb.useGravity = false; // gravity applied manually (1g) by the controller
                    rb.interpolation = RigidbodyInterpolation.None;
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                    // Stand collider: 1.8m tall x 0.5m wide (radius 0.25m).
                    // Zero-friction material: the landing impact's friction impulse would
                    // otherwise eat horizontal speed inside the physics step (mu*deltaV can
                    // reach ~0.6x the fall speed), silently breaking the slide trigger.
                    // All movement friction is applied manually as forces by the controller.
                    // Saved as a persistent asset: in-memory material instances are dropped
                    // by SaveAsPrefabAsset (serialized as fileID: 0).
                    EnsureFolder("Assets/Game", "Physics");
                    const string noFrictionPath = "Assets/Game/Physics/PlayerNoFriction.physicMaterial";
                    var noFriction = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(noFrictionPath);
                    if (noFriction == null)
                    {
                        noFriction = new PhysicMaterial("PlayerNoFriction");
                        AssetDatabase.CreateAsset(noFriction, noFrictionPath);
                    }
                    noFriction.dynamicFriction = 0f;
                    noFriction.staticFriction = 0f;
                    noFriction.bounciness = 0f;
                    noFriction.frictionCombine = PhysicMaterialCombine.Minimum;
                    noFriction.bounceCombine = PhysicMaterialCombine.Minimum;
                    EditorUtility.SetDirty(noFriction);

                    var standCapsule = root.AddComponent<CapsuleCollider>();
                    standCapsule.height = 1.8f;
                    standCapsule.radius = 0.25f;
                    standCapsule.center = new Vector3(0f, 0.9f, 0f);
                    standCapsule.sharedMaterial = noFriction;

                    // Crouch collider: 0.9m tall x 0.5m wide (radius 0.25m). Disabled by default.
                    var crouchCapsule = root.AddComponent<CapsuleCollider>();
                    crouchCapsule.height = 0.9f;
                    crouchCapsule.radius = 0.25f;
                    crouchCapsule.center = new Vector3(0f, 0.45f, 0f);
                    crouchCapsule.enabled = false;
                    crouchCapsule.sharedMaterial = noFriction;

                    // Behaviour
                    var controller = root.AddComponent<NetworkPlayerController>();
                    root.AddComponent<NetworkPlayerHealth>();
                    root.AddComponent<NetworkCombatant>();
                    root.AddComponent<GameHud>();
                    root.AddComponent<DeployScreen>();
                    root.AddComponent<MapIndicator>();
                    root.AddComponent<HeadMarker>();

                    // Shared combat (projectile shooting + throwing). Reuse prefabs if already built.
                    if (grenadePrefab == null)
                        grenadePrefab = BuildGrenadePrefab();
                    if (smokePrefab == null)
                        smokePrefab = BuildSmokePrefab();
                    if (rescuePrefab == null)
                        rescuePrefab = BuildRescuePrefab();
                    if (bulletPrefab == null)
                        bulletPrefab = BuildBulletPrefab();
                    var combat = root.AddComponent<NetworkCombat>();
                    combat.grenadeThrowablePrefab = grenadePrefab.GetComponent<NetworkThrowable>();
                    combat.smokeThrowablePrefab = smokePrefab.GetComponent<NetworkThrowable>();
                    combat.rescueThrowablePrefab = rescuePrefab.GetComponent<NetworkThrowable>();

                    // Firearm (PHASE5): shared data-driven gun driving the pooled bullet.
                    var gun = root.AddComponent<NetworkGun>();
                    gun.definition = BuildM4Definition();
                    gun.bulletPrefab = bulletPrefab;
                    gun.controller = controller;
                    // PHASE14: 第三人称枪模 = 新枪(含 RearGrip/BarrelGrip/MagGrip 握把锚点 +
                    // 照门/枪机)。运行时 NetworkGun 实例化到 SoldierRigSetup.WeaponAnchor,
                    // 由 BindWeapon 绑定双手 IK 目标与握把胶囊。
                    gun.thirdPersonModelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(M4PrefabPath);

                    controller.standCollider = standCapsule;
                    controller.crouchCollider = crouchCapsule;
                    controller.combat = combat;
                    controller.gun = gun;

                    // Equipment (PHASE6): shared data-driven equipment runtime.
                    if (equipmentList == null)
                        equipmentList = BuildEquipmentAssets();
                    var equipment = root.AddComponent<NetworkEquipment>();
                    equipment.equipmentList = equipmentList;
                    equipment.controller = controller;
                    controller.equipment = equipment;

                    // Camera child (local player only). Bound to capsule top (1.8m) - 0.15m = 1.65m.
                    var camGo = new GameObject("Camera");
                    camGo.transform.SetParent(root.transform, false);
                    camGo.transform.localPosition = new Vector3(0f, 1.65f, 0f);
                    var cam = camGo.AddComponent<Camera>();
                    camGo.tag = "MainCamera";
                    camGo.AddComponent<AudioListener>();
                    controller.playerCamera = cam;

                    // Remote visual (capsule body), scaled to match the 1.8m x 0.25m collider.
                    // Player body is green to distinguish from red/blue AI.
                    var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    body.name = "Body";
                    body.transform.SetParent(root.transform, false);
                    body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                    body.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
                    Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
                    var playerBodyMat = CreatePersistentMaterial(
                        "Assets/Game/Materials/PlayerBodyGreen.mat",
                        new Color(0.2f, 0.8f, 0.3f),
                        new Color(0.1f, 0.5f, 0.2f));
                    if (playerBodyMat != null)
                    {
                        var rend = body.GetComponent<Renderer>();
                        if (rend != null) rend.sharedMaterial = playerBodyMat;
                    }
                    controller.visual = body;

                    // PHASE12: team soldier models (red Natlan / blue Fatui) + animator driver.
                    AttachSoldierModels(root);

                    // Save
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                internal static GameObject BuildGrenadePrefab()
                {
                    var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    root.name = "GrenadeThrowable";
                    root.transform.localScale = Vector3.one * 0.2f;

                    // Red emissive material (persistent asset — see CreatePersistentMaterial).
                    var rend = root.GetComponent<Renderer>();
                    var mat = CreatePersistentMaterial(
                        "Assets/Game/Materials/GrenadeRed.mat",
                        new Color(0.9f, 0.1f, 0.1f),
                        new Color(0.8f, 0.1f, 0.1f));
                    if (mat != null) rend.sharedMaterial = mat;

                    root.AddComponent<NetworkIdentity>();

                    var rb = root.AddComponent<Rigidbody>();
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rb.useGravity = true;
                    // Near-massless: the throwable spawns inside the thrower's capsule (no
                    // throwPoint assigned), and with equal 1kg masses the depenetration
                    // impulse kicks an airborne thrower into a rapid fall. A 1:100 mass
                    // ratio keeps the correction on the throwable. Trajectory is unaffected
                    // (gravity is mass-independent, drag is 0).
                    rb.mass = 0.01f;

                    var col = root.GetComponent<SphereCollider>();
                    col.material = null;

                    var grenade = root.AddComponent<GrenadeThrowable>();
                    grenade.fuseTime = 2f; // 2s fuse

                    EnsureFolder("Assets/Game", "Prefabs");
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, GrenadePrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                internal static GameObject BuildSmokePrefab()
                {
                    var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    root.name = "SmokeThrowable";
                    root.transform.localScale = Vector3.one * 0.2f;

                    var rend = root.GetComponent<Renderer>();
                    var mat = CreatePersistentMaterial(
                        "Assets/Game/Materials/SmokeThrowableGrey.mat",
                        new Color(0.7f, 0.7f, 0.7f),
                        new Color(0.25f, 0.25f, 0.25f));
                    if (mat != null) rend.sharedMaterial = mat;

                    root.AddComponent<NetworkIdentity>();

                    var rb = root.AddComponent<Rigidbody>();
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rb.useGravity = true;
                    rb.mass = 0.01f; // near-massless — see GrenadeThrowable note above

                    var smoke = root.AddComponent<SmokeThrowable>();
                    smoke.fuseTime = 1f; // short fuse so it pops near the landing point

                    EnsureFolder("Assets/Game", "Prefabs");
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, SmokePrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                internal static GameObject BuildRescuePrefab()
                {
                    var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    root.name = "RescueThrowable";
                    root.transform.localScale = Vector3.one * 0.2f;

                    var rend = root.GetComponent<Renderer>();
                    var mat = CreatePersistentMaterial(
                        "Assets/Game/Materials/RescueGreen.mat",
                        new Color(0.2f, 0.9f, 0.35f),
                        new Color(0.15f, 0.6f, 0.2f));
                    if (mat != null) rend.sharedMaterial = mat;

                    root.AddComponent<NetworkIdentity>();

                    var rb = root.AddComponent<Rigidbody>();
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rb.useGravity = true;
                    rb.mass = 0.01f; // near-massless — see GrenadeThrowable note above

                    var rescue = root.AddComponent<RescueThrowable>();
                    rescue.fuseTime = 0f; // detonate on impact (no fuse)

                    EnsureFolder("Assets/Game", "Prefabs");
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, RescuePrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                private static GameObject NewThrowableRoot(string name, PrimitiveType primitive, float scale,
                                                           Color color, Color? emission, string materialPath)
                {
                    var root = GameObject.CreatePrimitive(primitive);
                    root.name = name;
                    root.transform.localScale = Vector3.one * scale;

                    var rend = root.GetComponent<Renderer>();
                    var mat = CreatePersistentMaterial(materialPath, color, emission);
                    if (mat != null && rend != null) rend.sharedMaterial = mat;

                    root.AddComponent<NetworkIdentity>();

                    var rb = root.AddComponent<Rigidbody>();
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rb.useGravity = true;
                    rb.mass = 0.01f; // near-massless — see GrenadeThrowable note

                    var col = root.GetComponent<Collider>();
                    if (col != null) col.material = null;

                    return root;
                }

                private static void AttachModel(GameObject root, string modelPrefabPath, Vector3 localScale)
                {
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPrefabPath);
                    if (model == null)
                    {
                        Debug.LogWarning($"[NetworkSetup] Model not found at {modelPrefabPath}");
                        return;
                    }

                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
                    inst.name = model.name;
                    inst.transform.SetParent(root.transform, false);
                    inst.transform.localPosition = Vector3.zero;
                    inst.transform.localRotation = Quaternion.identity;
                    inst.transform.localScale = localScale;

                    // Strip model colliders so only the root primitive drives physics.
                    foreach (var c in inst.GetComponentsInChildren<Collider>(true))
                        Object.DestroyImmediate(c);

                    // Hide the root primitive renderer; the model is the visual.
                    var rootRend = root.GetComponent<Renderer>();
                    if (rootRend != null) rootRend.enabled = false;
                }

                private static GameObject SaveThrowable(GameObject root, string prefabPath)
                {
                    EnsureFolder("Assets/Game", "Prefabs");
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                private static GameObject BuildHandGrenadePrefab()
                {
                    var root = NewThrowableRoot("HandGrenade", PrimitiveType.Sphere, 0.2f,
                        new Color(0.9f, 0.1f, 0.1f), new Color(0.8f, 0.1f, 0.1f),
                        "Assets/Game/Materials/GrenadeRed.mat");
                    AttachModel(root, RgdModelPath, Vector3.one);

                    var g = root.AddComponent<GrenadeThrowable>();
                    g.fuseTime = 4f;
                    g.explosionYield = 120f;
                    g.explosionRadius = 5f;

                    return SaveThrowable(root, HandGrenadePrefabPath);
                }

                private static GameObject BuildSmokeGrenadePrefab()
                {
                    var root = NewThrowableRoot("SmokeGrenade", PrimitiveType.Sphere, 0.2f,
                        new Color(0.7f, 0.7f, 0.7f), new Color(0.25f, 0.25f, 0.25f),
                        "Assets/Game/Materials/SmokeThrowableGrey.mat");
                    AttachModel(root, SmokeModelPath, Vector3.one);

                    var s = root.AddComponent<SmokeThrowable>();
                    s.fuseTime = 0f;               // impact trigger
                    s.concentration = 0.95f;       // 初始透明度 95%
                    s.radius = 10f;
                    s.decayTime = 20f;

                    return SaveThrowable(root, SmokeGrenadePrefabPath);
                }

                private static GameObject BuildEmpFieldPrefab()
                {
                    var root = NewThrowableRoot("EmpField", PrimitiveType.Sphere, 0.2f,
                        new Color(0.2f, 0.45f, 1f), new Color(0.1f, 0.2f, 0.6f),
                        "Assets/Game/Materials/EmpBlue.mat");

                    var field = root.AddComponent<NetworkEmpField>();
                    field.radius = 6f;
                    field.lifetime = 10f;
                    field.interfereDuration = 2f;

                    return SaveThrowable(root, EmpFieldPrefabPath);
                }

                private static GameObject BuildEmpGrenadePrefab(GameObject empFieldPrefab)
                {
                    var root = NewThrowableRoot("EmpGrenadeThrowable", PrimitiveType.Sphere, 0.2f,
                        new Color(0.2f, 0.45f, 1f), new Color(0.1f, 0.2f, 0.6f),
                        "Assets/Game/Materials/EmpBlue.mat");
                    AttachModel(root, FlashModelPath, Vector3.one);

                    var g = root.AddComponent<EmpGrenadeThrowable>();
                    g.fuseTime = 0f;               // impact trigger
                    g.empFieldPrefab = empFieldPrefab != null
                        ? empFieldPrefab.GetComponent<NetworkEmpField>()
                        : null;
                    g.radius = 6f;
                    g.lifetime = 10f;
                    g.interfereDuration = 2f;

                    return SaveThrowable(root, EmpGrenadePrefabPath);
                }

                private static GameObject BuildLauncherGrenadePrefab()
                {
                    var root = NewThrowableRoot("LauncherGrenade", PrimitiveType.Sphere, 0.2f,
                        new Color(0.9f, 0.4f, 0.1f), new Color(0.8f, 0.3f, 0.1f),
                        "Assets/Game/Materials/LauncherGrenade.mat");

                    var g = root.AddComponent<GrenadeThrowable>();
                    g.fuseTime = 0f;               // impact trigger
                    g.explosionYield = 120f;
                    g.explosionRadius = 5f;

                    return SaveThrowable(root, LauncherGrenadePrefabPath);
                }

                private static GameObject BuildLauncherSmokePrefab()
                {
                    var root = NewThrowableRoot("LauncherSmoke", PrimitiveType.Sphere, 0.2f,
                        new Color(0.7f, 0.7f, 0.7f), new Color(0.25f, 0.25f, 0.25f),
                        "Assets/Game/Materials/LauncherSmoke.mat");

                    var s = root.AddComponent<SmokeThrowable>();
                    s.fuseTime = 0f;
                    s.concentration = 0.95f;
                    s.radius = 10f;
                    s.decayTime = 20f;

                    return SaveThrowable(root, LauncherSmokePrefabPath);
                }

                private static GameObject BuildRpgPrefab()
                {
                    var root = NewThrowableRoot("Rpg", PrimitiveType.Sphere, 0.2f,
                        new Color(0.9f, 0.4f, 0.1f), new Color(0.8f, 0.3f, 0.1f),
                        "Assets/Game/Materials/RpgOrange.mat");
                    AttachModel(root, RpgModelPath, Vector3.one);

                    var g = root.AddComponent<GrenadeThrowable>();
                    g.fuseTime = 0f;
                    g.explosionYield = 250f;
                    g.explosionRadius = 3f;

                    return SaveThrowable(root, RpgPrefabPath);
                }

                private static GameObject BuildSignalChargePrefab()
                {
                    var root = NewThrowableRoot("SignalCharge", PrimitiveType.Sphere, 0.2f,
                        new Color(0.9f, 0.6f, 0.1f), new Color(0.8f, 0.5f, 0.1f),
                        "Assets/Game/Materials/SignalCharge.mat");

                    var c = root.AddComponent<RemoteChargeThrowable>();
                    c.explosionYield = 200f;
                    c.explosionRadius = 7f;
                    c.freezeOnContact = true;

                    return SaveThrowable(root, SignalChargePrefabPath);
                }

                private static GameObject BuildWiredChargePrefab()
                {
                    var root = NewThrowableRoot("WiredCharge", PrimitiveType.Sphere, 0.2f,
                        new Color(0.8f, 0.7f, 0.2f), new Color(0.7f, 0.6f, 0.2f),
                        "Assets/Game/Materials/WiredCharge.mat");

                    var c = root.AddComponent<RemoteChargeThrowable>();
                    c.explosionYield = 180f;
                    c.explosionRadius = 6f;
                    c.freezeOnContact = true;

                    return SaveThrowable(root, WiredChargePrefabPath);
                }

                private static GameObject BuildDelayedBombPrefab()
                {
                    var root = NewThrowableRoot("DelayedBomb", PrimitiveType.Sphere, 0.2f,
                        new Color(0.9f, 0.1f, 0.1f), new Color(0.8f, 0.1f, 0.1f),
                        "Assets/Game/Materials/DelayedBombRed.mat");

                    var g = root.AddComponent<GrenadeThrowable>();
                    g.fuseTime = 3f;               // 延时 3s
                    g.explosionYield = 200f;
                    g.explosionRadius = 5f;
                    g.freezeOnContact = true;

                    return SaveThrowable(root, DelayedBombPrefabPath);
                }

                private static GameObject BuildSupplyPackPrefab()
                {
                    // 小型绿色长方体，与活体无碰撞（SupplyPackThrowable.OnStartServer 处理忽略）。
                    var root = NewThrowableRoot("SupplyPack", PrimitiveType.Cube, 0.3f,
                        new Color(0.2f, 0.9f, 0.35f), new Color(0.1f, 0.5f, 0.2f),
                        "Assets/Game/Materials/SupplyPackGreen.mat");

                    var s = root.AddComponent<SupplyPackThrowable>();
                    s.fuseTime = 0f;
                    s.supplyAmount = 80;
                    s.regenHpPerSecond = 10f;

                    return SaveThrowable(root, SupplyPackPrefabPath);
                }

                private static GameObject BuildLargeSupplyCratePrefab()
                {
                    // 0.3 边长绿色正方体，与活体无碰撞。
                    var root = NewThrowableRoot("LargeSupplyCrate", PrimitiveType.Cube, 0.3f,
                        new Color(0.2f, 0.9f, 0.35f), new Color(0.1f, 0.5f, 0.2f),
                        "Assets/Game/Materials/SupplyPackGreen.mat");

                    var c = root.AddComponent<LargeSupplyCrate>();
                    c.radius = 5f;
                    c.interval = 5f;
                    c.supplyPerTick = 25;
                    c.healthPerTick = 25f;

                    return SaveThrowable(root, LargeSupplyCratePrefabPath);
                }

                private static GameObject BuildInterceptorPrefab()
                {
                    // 蓝色 0.2 球体，无碰撞（只与刚体/地面碰撞）。
                    var root = NewThrowableRoot("Interceptor", PrimitiveType.Sphere, 0.2f,
                        new Color(0.2f, 0.45f, 1f), new Color(0.1f, 0.2f, 0.6f),
                        "Assets/Game/Materials/EmpBlue.mat");

                    var ic = root.AddComponent<NetworkInterceptor>();
                    ic.radius = 3f;
                    ic.maxIntercepts = 3;

                    return SaveThrowable(root, InterceptorPrefabPath);
                }

                private static GameObject BuildSensorProbePrefab()
                {
                    var root = new GameObject("SensorProbe");
                    root.AddComponent<NetworkIdentity>();

                    // 圆锥 mesh 必须保存为资产：内存 mesh 无法序列化进 prefab（会变 fileID:0，
                    // 导致渲染与碰撞丢失）。
                    EnsureFolder("Assets/Game", "Materials");
                    const string meshPath = "Assets/Game/Materials/SensorConeMesh.asset";
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    if (mesh == null)
                    {
                        mesh = CreateConeMesh(0.25f, 0.6f, 16);
                        mesh.name = "SensorConeMesh";
                        AssetDatabase.CreateAsset(mesh, meshPath);
                        AssetDatabase.SaveAssets();
                    }
                    // 新建资产需重新加载才能拿到有效资产实例（否则保存进 prefab 会变 fileID:0）。
                    mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

                    var mf = root.AddComponent<MeshFilter>();
                    mf.sharedMesh = mesh;

                    var mr = root.AddComponent<MeshRenderer>();
                    var mat = CreatePersistentMaterial(
                        "Assets/Game/Materials/SensorBlue.mat",
                        new Color(0.2f, 0.45f, 1f),
                        new Color(0.1f, 0.2f, 0.6f));
                    if (mat != null) mr.sharedMaterial = mat;

                    // 圆锥与地面碰撞（放置），对活体无碰撞。
                    var mc = root.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    mc.convex = true;

                    var rb = root.AddComponent<Rigidbody>();
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rb.useGravity = true;
                    rb.mass = 1f;

                    var sensor = root.AddComponent<SensorProbe>();
                    sensor.radius = 20f;
                    sensor.interval = 5f;
                    sensor.markDuration = 3f;

                    EnsureFolder("Assets/Game", "Prefabs");
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, SensorProbePrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                private static GameObject BuildDeployBeaconPrefab()
                {
                    var root = new GameObject("DeployBeacon");
                    root.AddComponent<NetworkIdentity>();

                    // 倒圆锥 mesh（底面朝上、尖端朝下）：单独资产，内存 mesh 无法序列化进 prefab。
                    EnsureFolder("Assets/Game", "Materials");
                    const string meshPath = "Assets/Game/Materials/DeployBeaconConeMesh.asset";
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    if (mesh == null)
                    {
                        mesh = CreateConeMesh(0.35f, 0.8f, 16);
                        mesh.name = "DeployBeaconConeMesh";
                        AssetDatabase.CreateAsset(mesh, meshPath);
                        AssetDatabase.SaveAssets();
                    }
                    mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

                    // 圆锥放在子节点并旋转 180°（根节点保持无旋转，地图标记文字不受影响）。
                    var cone = new GameObject("Cone");
                    cone.transform.SetParent(root.transform, false);
                    cone.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);

                    var mf = cone.AddComponent<MeshFilter>();
                    mf.sharedMesh = mesh;

                    var mr = cone.AddComponent<MeshRenderer>();
                    var mat = CreatePersistentMaterial(
                        "Assets/Game/Materials/BeaconYellow.mat",
                        new Color(0.95f, 0.8f, 0.1f),
                        new Color(0.55f, 0.45f, 0.05f));
                    if (mat != null) mr.sharedMaterial = mat;

                    // 圆锥与地面碰撞（放置后冻结），对活体无碰撞。
                    var mc = cone.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    mc.convex = true;

                    var rb = root.AddComponent<Rigidbody>();
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rb.useGravity = true;
                    rb.mass = 1f;

                    var beacon = root.AddComponent<DeployBeacon>();
                    beacon.maxUses = 5;

                    EnsureFolder("Assets/Game", "Prefabs");
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, DeployBeaconPrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                private static Mesh CreateConeMesh(float radius, float height, int segments)
                {
                    var mesh = new Mesh();
                    int n = Mathf.Max(3, segments);

                    var verts = new List<Vector3>();
                    var normals = new List<Vector3>();
                    var tris = new List<int>();

                    // Apex.
                    int apex = 0;
                    verts.Add(new Vector3(0f, height, 0f));
                    normals.Add(Vector3.up);

                    // Base ring.
                    int baseStart = 1;
                    for (int i = 0; i < n; i++)
                    {
                        float a = (i / (float)n) * Mathf.PI * 2f;
                        verts.Add(new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
                        normals.Add(Vector3.down);
                    }

                    // Cone side (two triangles per segment, using the apex).
                    for (int i = 0; i < n; i++)
                    {
                        int b = baseStart + i;
                        int c = baseStart + ((i + 1) % n);
                        tris.Add(apex); tris.Add(c); tris.Add(b);
                    }

                    // Base cap (fan, facing down).
                    for (int i = 0; i < n; i++)
                    {
                        int b = baseStart + i;
                        int c = baseStart + ((i + 1) % n);
                        tris.Add(b); tris.Add(c); tris.Add(baseStart + n);   // 中心点
                    }
                    verts.Add(Vector3.zero);
                    normals.Add(Vector3.down);

                    mesh.vertices = verts.ToArray();
                    mesh.normals = normals.ToArray();
                    mesh.triangles = tris.ToArray();
                    mesh.RecalculateBounds();
                    return mesh;
                }

                internal static GameObject BuildBulletPrefab()
                {
                    var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    root.name = "Bullet";
                    root.transform.localScale = Vector3.one * 0.05f;

                    var rend = root.GetComponent<Renderer>();
                    var mat = CreatePersistentMaterial(
                        "Assets/Game/Materials/BulletWhite.mat",
                        new Color(1f, 1f, 1f),
                        new Color(0.6f, 0.6f, 0.6f));
                    if (mat != null) rend.sharedMaterial = mat;

                    // Ray-based projectile: no physics collider (hit detection is a manual
                    // segment raycast on the server, so a collider would only cause self-hits).
                    Object.DestroyImmediate(root.GetComponent<SphereCollider>());

                    root.AddComponent<NetworkBullet>();

                    EnsureFolder("Assets/Game", "Prefabs");
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, BulletPrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }

                internal static GameObject BuildAIEntityPrefab(GameObject grenadePrefab, GameObject smokePrefab, GameObject rescuePrefab, GameObject bulletPrefab, List<EquipmentDefinition> equipmentList = null)
                {
                    var root = new GameObject("AIEntity");

                    root.AddComponent<NetworkIdentity>();

                    AddNetworkTransform(root, SyncDirection.ServerToClient, syncRotation: true);

                    // Physics: same capsule as the player (1.8m x 0.5m).
                    var rb = root.AddComponent<Rigidbody>();
                    rb.freezeRotation = true;
                    rb.useGravity = false;
                    rb.interpolation = RigidbodyInterpolation.None;
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                    EnsureFolder("Assets/Game", "Physics");
                    const string noFrictionPath = "Assets/Game/Physics/PlayerNoFriction.physicMaterial";
                    var noFriction = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(noFrictionPath);
                    if (noFriction == null)
                    {
                        noFriction = new PhysicMaterial("PlayerNoFriction");
                        AssetDatabase.CreateAsset(noFriction, noFrictionPath);
                        noFriction.dynamicFriction = 0f;
                        noFriction.staticFriction = 0f;
                        noFriction.bounciness = 0f;
                        noFriction.frictionCombine = PhysicMaterialCombine.Minimum;
                        noFriction.bounceCombine = PhysicMaterialCombine.Minimum;
                    }

                    var capsule = root.AddComponent<CapsuleCollider>();
                    capsule.height = 1.8f;
                    capsule.radius = 0.25f;
                    capsule.center = new Vector3(0f, 0.9f, 0f);
                    capsule.sharedMaterial = noFriction;

                    // Crouch collider (same as the player): 0.9m, disabled by default.
                    var aiCrouchCapsule = root.AddComponent<CapsuleCollider>();
                    aiCrouchCapsule.height = 0.9f;
                    aiCrouchCapsule.radius = 0.25f;
                    aiCrouchCapsule.center = new Vector3(0f, 0.45f, 0f);
                    aiCrouchCapsule.enabled = false;
                    aiCrouchCapsule.sharedMaterial = noFriction;

                    // Shared combat (same as player).
                    var combat = root.AddComponent<NetworkCombat>();
                    if (grenadePrefab != null)
                        combat.grenadeThrowablePrefab = grenadePrefab.GetComponent<NetworkThrowable>();
                    if (smokePrefab != null)
                        combat.smokeThrowablePrefab = smokePrefab.GetComponent<NetworkThrowable>();
                    if (rescuePrefab != null)
                        combat.rescueThrowablePrefab = rescuePrefab.GetComponent<NetworkThrowable>();

                    // Firearm (PHASE5): same data-driven gun as the player.
                    var gun = root.AddComponent<NetworkGun>();
                    gun.definition = BuildM4Definition();
                    gun.bulletPrefab = bulletPrefab;
                    gun.thirdPersonModelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(M4PrefabPath);   // PHASE14

                    // Health (same as player).
                    root.AddComponent<NetworkPlayerHealth>();
                    root.AddComponent<NetworkCombatant>();
                    root.AddComponent<MapIndicator>();
                    root.AddComponent<HeadMarker>();

                    // 3C movement (ML-branch): the SAME force-driven controller as the
                    // player. The AI controller only injects input server-side; all
                    // movement / posture / combat runs through the identical code path.
                    var aiController = root.AddComponent<NetworkPlayerController>();
                    aiController.standCollider = capsule;
                    aiController.crouchCollider = aiCrouchCapsule;
                    aiController.combat = combat;
                    aiController.gun = gun;

                    // AI controller (input provider, no built-in behavior).
                    var ai = root.AddComponent<NetworkAIController>();

                    // ML bridge (M1): observation collection + action mapping + ray sensor.
                    root.AddComponent<AgentRaySensor>();
                    var bridge = root.AddComponent<MLAgentBridge>();
                    bridge.ai = ai;

                    // Equipment (PHASE6): same shared runtime as the player.
                    if (equipmentList == null)
                        equipmentList = BuildEquipmentAssets();
                    var equipment = root.AddComponent<NetworkEquipment>();
                    equipment.equipmentList = equipmentList;
                    equipment.controller = aiController;
                    aiController.equipment = equipment;

                    // Cross-wire the gun's camera-shake / equipment dash hooks.
                    gun.controller = aiController;
                    ai.controller = aiController;

                    // Visual body.
                    // Red material for AI body (persistent, HDRP-aware — see CreatePersistentMaterial).
                    var aiMat = CreatePersistentMaterial(
                        "Assets/Game/Materials/AIBodyRed.mat",
                        new Color(0.8f, 0.15f, 0.15f),
                        new Color(0.3f, 0.05f, 0.05f));

                    var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    body.name = "Body";
                    body.transform.SetParent(root.transform, false);
                    body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                    body.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
                    Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
                    var rend = body.GetComponent<Renderer>();
                    if (rend != null)
                        rend.sharedMaterial = aiMat;

                    // Wire the visual: the controller toggles it for remote viewing, the
                    // AI controller colours it per team.
                    ai.visual = body;
                    aiController.visual = body;

                    // PHASE12: team soldier models (red Natlan / blue Fatui) + animator driver.
                    AttachSoldierModels(root);

                    EnsureFolder("Assets/Game", "Prefabs");
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, AIPrefabPath);
                    Object.DestroyImmediate(root);
                    return prefab;
                }
    }
}
