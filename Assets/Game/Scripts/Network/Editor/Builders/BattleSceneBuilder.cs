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
    /// NetworkSetup (partial) — Menu entry points for the FSM battle scenes (59 AI, all-support, HGTR, Map_v1).
    /// </summary>
    public static partial class NetworkSetup
    {
                [MenuItem("HagenDa/Create FSM Battle Scene (59 AI)")]
                public static void CreateFSMBattleScene()
                {
                    CreateFSMBattleSceneInternal(FsmClass.Assault, "FSMBattle.scene");
                }

                [MenuItem("HagenDa/Create FSM Battle Scene (All Support)")]
                public static void CreateFSMBattleSceneAllSupport()
                {
                    CreateFSMBattleSceneInternal(FsmClass.Support, "FSMBattleSupport.scene");
                }

                private static void CreateFSMBattleSceneInternal(FsmClass defaultClass, string sceneFile)
                {
                    string scenePath = "Assets/Game/Scenes/" + sceneFile;
                    EnsureFolder("Assets", "Scenes");
                    EnsureFolder("Assets/Game", "Prefabs");
                    EnsureFolder("Assets/Game", "Equipment");
                    EnsureMapLayers();

                    // Prefabs + equipment assets (idempotent).
                    List<EquipmentDefinition> equipmentList = BuildEquipmentAssets();
                    GameObject grenadePrefab = BuildGrenadePrefab();
                    GameObject smokePrefab = BuildSmokePrefab();
                    GameObject rescuePrefab = BuildRescuePrefab();
                    GameObject bulletPrefab = BuildBulletPrefab();
                    GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
                    GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
                    GameObject fsmPrefab = BuildFSMAIPrefab(aiPrefab);

                    var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                        UnityEditor.SceneManagement.NewSceneMode.Single);

                    EnsureLighting();

                    // --- 120 x 220 map: red GR z<0, blue GR z>0, 3 zones along Z ---
                    const float mapW = 120f;   // X
                    const float mapL = 220f;   // Z
                    const float wallH = 6f;

                    var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    floor.name = "Floor";
                    floor.transform.position = Vector3.zero;
                    floor.transform.localScale = new Vector3(mapW / 10f, 1f, mapL / 10f);

                    CreateWall(new Vector3(0f, wallH * 0.5f, mapL * 0.5f), new Vector3(mapW, wallH, 1f));   // north
                    CreateWall(new Vector3(0f, wallH * 0.5f, -mapL * 0.5f), new Vector3(mapW, wallH, 1f));  // south
                    CreateWall(new Vector3(-mapW * 0.5f, wallH * 0.5f, 0f), new Vector3(1f, wallH, mapL));  // west
                    CreateWall(new Vector3(mapW * 0.5f, wallH * 0.5f, 0f), new Vector3(1f, wallH, mapL));   // east

                    // Garrisons (GR = 基地重生区).
                    var redGr = CreateGarrison("RedGR", new Vector3(0f, 0f, -mapL * 0.42f), (int)MatchTeam.Red, 20f);
                    var blueGr = CreateGarrison("BlueGR", new Vector3(0f, 0f, mapL * 0.42f), (int)MatchTeam.Blue, 20f);

                    // 3 capture points + StrategicZone wrappers（抽象争夺点，注册表上限 3）.
                    var zoneA = CreateCapturePoint("Zone_A", new Vector3(-18f, 0f, -45f), 12f);
                    var zoneB = CreateCapturePoint("Zone_B", new Vector3(0f, 0f, 0f), 12f);
                    var zoneC = CreateCapturePoint("Zone_C", new Vector3(18f, 0f, 45f), 12f);
                    zoneA.letter = "A";
                    zoneB.letter = "B";
                    zoneC.letter = "C";
                    CreateStrategicZone("StrategicZone_A", zoneA);
                    CreateStrategicZone("StrategicZone_B", zoneB);
                    CreateStrategicZone("StrategicZone_C", zoneC);

                    // Match manager：持续战斗（无终局），6 小队 × 5 人。
                    var mmGo = new GameObject("NetworkMatchManager");
                    var mm = mmGo.AddComponent<NetworkMatchManager>();
                    mm.winScore = 999999;
                    mm.squadsPerTeam = 6;
                    mm.squadSize = 5;
                    mm.redeployDelay = 10f;
                    mm.garrisons = new List<GarrisonZone> { redGr, blueGr };
                    mm.capturePoints = new List<CapturePoint> { zoneA, zoneB, zoneC };

                    // Zone registry + FSM 调度器（10Hz 决策 tick + 批感知 + 寻路错峰）.
                    var registryGo = new GameObject("StrategicZoneRegistry");
                    registryGo.AddComponent<StrategicZoneRegistry>();

                    var systemGo = new GameObject("FSMBattleSystem");
                    systemGo.AddComponent<FSMBattleSystem>();
                    systemGo.AddComponent<FSMStatsHud>();

                    // 静态掩体场（种子化）+ 运行时注册器.
                    BuildFSMCoverField(mapW, mapL);

                    // NetworkManager：无 HUD（全自动对局），Play 即自动 Host.
                    var nmGo = new GameObject("NetworkManager");
                    var nm = nmGo.AddComponent<NetworkManager>();
                    nm.playerPrefab = playerPrefab;
                    nm.autoCreatePlayer = false;
                    var kcp = nmGo.AddComponent<kcp2k.KcpTransport>();
                    nm.transport = kcp;
                    nmGo.AddComponent<TrainingAutoHost>();
                    RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab, fsmPrefab);
                    foreach (var def in equipmentList)
                        if (def != null && def.throwablePrefab != null)
                            RegisterSpawnPrefabs(def.throwablePrefab);
                    var empField = AssetDatabase.LoadAssetAtPath<GameObject>(EmpFieldPrefabPath);
                    if (empField != null) RegisterSpawnPrefabs(empField);

                    // 观战相机（场景无玩家相机）.
                    var camGo = new GameObject("BattleCamera");
                    camGo.transform.position = new Vector3(0f, 85f, -105f);
                    camGo.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                    var cam = camGo.AddComponent<Camera>();
                    cam.farClipPlane = 500f;

                    // PHASE9: 自由观战相机（WASD/空格/Shift，鼠标拖拽视角，无碰撞）.
                    var freeCamGo = new GameObject("FreeCamera");
                    freeCamGo.transform.position = new Vector3(0f, 60f, -50f);
                    var freeCam = freeCamGo.AddComponent<Camera>();
                    freeCam.farClipPlane = 500f;
                    freeCam.depth = 1f;   // 覆盖 BattleCamera
                    freeCamGo.AddComponent<AudioListener>();
                    freeCamGo.AddComponent<FreeCamera>();

                    // PHASE9 指挥官：每 20s 给所有小队随机分配未占领/敌方要地.
                    var cmdGo = new GameObject("SquadCommander");
                    cmdGo.AddComponent<SquadCommander>();

                    // NavMesh：必须在实体生成之前烘焙（CollectObjects.All 会把
                    // 实体胶囊当障碍物，在出生点打出洞）。
                    BuildNavMeshForFloor();

                    // 59 实体：30 红（z<0）/ 29 蓝（z>0）。生成顺序 = 小队 round-robin
                    // 顺序；每小队 5 人 = 2 突击 + 2 支援 + 1 侦察。
                    CreateFSMTeam(fsmPrefab, (int)MatchTeam.Red, 30, -92f, defaultClass);
                    CreateFSMTeam(fsmPrefab, (int)MatchTeam.Blue, 29, 92f, defaultClass);

                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[NetworkSetup] Done. Created {scenePath} " +
                              "(59 FSM AI, 3 zones, 2 GR, static covers, baked NavMesh).");
                }

                [MenuItem("HagenDa/Create HGTR Battle Scene (30v30 FSM)")]
                public static void CreateHGTRBattleScene()
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    if (!scene.name.StartsWith("HGTR"))
                    {
                        Debug.LogError($"[NetworkSetup] Active scene '{scene.name}' is not HGTR. Open HGTR.scene first.");
                        return;
                    }

                    EnsureFolder("Assets/Game", "Prefabs");
                    EnsureFolder("Assets/Game", "Equipment");
                    EnsureMapLayers();

                    var mapRoot = GameObject.Find(HGTRMapRootName);
                    if (mapRoot == null)
                    {
                        Debug.LogError($"[NetworkSetup] '{HGTRMapRootName}' not found in scene.");
                        return;
                    }

                    // --- 1) 地图层归类（幂等）：ceiling → ceiling layer，其余 → Ground ---
                    int ceilingLayer = LayerMask.NameToLayer(MapLayers.CeilingName);
                    int groundLayer = LayerMask.NameToLayer(MapLayers.GroundName);
                    if (ceilingLayer < 0 || groundLayer < 0)
                    {
                        Debug.LogError($"[NetworkSetup] Missing layers '{MapLayers.CeilingName}' / '{MapLayers.GroundName}'.");
                        return;
                    }
                    int movedGround = 0, keptCeiling = 0;
                    foreach (var t in mapRoot.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == mapRoot.transform) continue;
                        if (t.name.ToLowerInvariant().Contains("ceiling"))
                        {
                            if (t.gameObject.layer != ceilingLayer) t.gameObject.layer = ceilingLayer;
                            keptCeiling++;
                        }
                        else if (t.gameObject.layer != groundLayer)
                        {
                            t.gameObject.layer = groundLayer;
                            movedGround++;
                        }
                    }
                    Debug.Log($"[NetworkSetup] Layers: ground={movedGround + keptCeiling} objects, ceiling={keptCeiling}.");

                    EnableMapMaterialsDoubleSided();
                    EnsureMapColliders();
                    EnableBackfaceQueries();

                    // --- 2) Prefabs / assets (idempotent) ---
                    List<EquipmentDefinition> equipmentList = BuildEquipmentAssets();
                    GameObject grenadePrefab = BuildGrenadePrefab();
                    GameObject smokePrefab = BuildSmokePrefab();
                    GameObject rescuePrefab = BuildRescuePrefab();
                    GameObject bulletPrefab = BuildBulletPrefab();
                    GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
                    GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
                    GameObject fsmPrefab = BuildFSMAIPrefab(aiPrefab);

                    // --- 3) 布局：按建筑名组 bounds 中心定位（避免硬编码坐标漂移）。
                    // Blender 导出命名与场景朝向可能镜像：不信任名字的东西/西含义，
                    // 一律按 X 排序 —— X 小=西（红方），X 大=东（蓝方）。
                    // GR / W / E 为矩形：尺寸 = 所在腔室的一半，中心 = 腔室中心。
                    Vector3 groundY = new Vector3(0f, 0.5f, 0f);   // floor 顶面高度
                    var grA = ShiftBounds(ChamberRect("wsafe_a"), groundY);
                    var grB = ShiftBounds(ChamberRect("esafe_a"), groundY);
                    var grWest = grA.center.x <= grB.center.x ? grA : grB;
                    var grEast = grA.center.x <= grB.center.x ? grB : grA;

                    var cpA = ShiftBounds(ChamberRect("w1_courtyard"), groundY);
                    var cpC = ShiftBounds(ChamberRect("e_courtyard"), groundY);
                    var cpB = new Bounds(GroupCenter("canyon_boulder") + groundY, Vector3.zero);
                    var cpWest = cpA.center.x <= cpC.center.x ? cpA : cpC;
                    var cpEast = cpA.center.x <= cpC.center.x ? cpC : cpA;

                    // --- 4) 安全区 ×2（GR，红西/蓝东，矩形=腔室一半）---
                    ClearOld("MatchManager", "StrategicZoneRegistry", "FSMBattleSystem",
                             "RedGR", "BlueGR", "Zone_W", "Zone_M", "Zone_E",
                             "StrategicZone_W", "StrategicZone_M", "StrategicZone_E",
                             "NetworkManager", "BattleCamera", "FreeCamera", "SquadCommander");
                    ClearAllFSMEntities();
                    var redSafe = CreateGarrison("RedGR", grWest.center, (int)MatchTeam.Red, 25f);
                    redSafe.extent = new Vector2(grWest.size.x * 0.25f, grWest.size.z * 0.25f);
                    var blueSafe = CreateGarrison("BlueGR", grEast.center, (int)MatchTeam.Blue, 25f);
                    blueSafe.extent = new Vector2(grEast.size.x * 0.25f, grEast.size.z * 0.25f);
                    AttachDeployPointTemplate(redSafe.gameObject, 4,
                        Mathf.Min(redSafe.extent.x, redSafe.extent.y) * 0.8f);
                    AttachDeployPointTemplate(blueSafe.gameObject, 4,
                        Mathf.Min(blueSafe.extent.x, blueSafe.extent.y) * 0.8f);

                    // --- 5) 据点 ×3 + StrategicZone 包装 ---
                    // W/E 矩形 = 庭院腔室一半；M 保持在 canyon_boulder 岩石上的圆形
                    // （半径避开岩石本体，直径 ~43m）。
                    var zoneW = CreateCapturePoint("Zone_W", cpWest.center, 14f);
                    zoneW.extent = new Vector2(cpWest.size.x * 0.25f, cpWest.size.z * 0.25f);
                    var zoneM = CreateCapturePoint("Zone_M", cpB.center, 22f);
                    var zoneE = CreateCapturePoint("Zone_E", cpEast.center, 14f);
                    zoneE.extent = new Vector2(cpEast.size.x * 0.25f, cpEast.size.z * 0.25f);
                    zoneW.letter = "W"; zoneM.letter = "M"; zoneE.letter = "E";
                    AttachDeployPointTemplate(zoneW.gameObject, 2,
                        Mathf.Min(zoneW.extent.x, zoneW.extent.y) * 0.8f);
                    AttachDeployPointTemplate(zoneM.gameObject, 2, 26f);
                    AttachDeployPointTemplate(zoneE.gameObject, 2,
                        Mathf.Min(zoneE.extent.x, zoneE.extent.y) * 0.8f);
                    CreateStrategicZone("StrategicZone_W", zoneW);
                    CreateStrategicZone("StrategicZone_M", zoneM);
                    CreateStrategicZone("StrategicZone_E", zoneE);

                    // --- 6) 对局系统（持续战斗 30v30：6 小队 × 5 人） ---
                    var mmGo = new GameObject("NetworkMatchManager");
                    var mm = mmGo.AddComponent<NetworkMatchManager>();
                    mm.winScore = 999999;
                    mm.squadsPerTeam = 6;
                    mm.squadSize = 5;
                    mm.redeployDelay = 10f;
                    mm.garrisons = new List<GarrisonZone> { redSafe, blueSafe };
                    mm.capturePoints = new List<CapturePoint> { zoneW, zoneM, zoneE };

                    var registryGo = new GameObject("StrategicZoneRegistry");
                    registryGo.AddComponent<StrategicZoneRegistry>();

                    var systemGo = new GameObject("FSMBattleSystem");
                    systemGo.AddComponent<FSMBattleSystem>();
                    systemGo.AddComponent<FSMStatsHud>();

                    var cmdGo = new GameObject("SquadCommander");
                    cmdGo.AddComponent<SquadCommander>();

                    // --- 7) NetworkManager（Play 即 Host；HGTR 有真人 → 自动生成玩家）---
                    var nmGo = new GameObject("NetworkManager");
                    var nm = nmGo.AddComponent<NetworkManager>();
                    nm.playerPrefab = playerPrefab;
                    nm.autoCreatePlayer = true;   // 真人固定红队，红 AI 只生成 29 人留位
                    var kcp = nmGo.AddComponent<kcp2k.KcpTransport>();
                    nm.transport = kcp;
                    nmGo.AddComponent<TrainingAutoHost>();
                    RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab, fsmPrefab);
                    foreach (var def in equipmentList)
                        if (def != null && def.throwablePrefab != null)
                            RegisterSpawnPrefabs(def.throwablePrefab);
                    var empField = AssetDatabase.LoadAssetAtPath<GameObject>(EmpFieldPrefabPath);
                    if (empField != null) RegisterSpawnPrefabs(empField);

                    // --- 8) 观战相机（机位/远裁剪面自适应地图 AABB）---
                    var camGo = new GameObject("BattleCamera");
                    var cam = camGo.AddComponent<Camera>();
                    cam.farClipPlane = 800f;
                    if (MapLayers.TryGetMapBounds(out var mb))
                    {
                        // 55° 俯瞰全图：相机拉到地图中心后方、高度按长轴缩放。
                        camGo.transform.position = new Vector3(
                            mb.center.x,
                            Mathf.Max(mb.size.x, mb.size.z) * 0.8f,
                            mb.center.z - mb.size.z * 0.7f);
                        camGo.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                        cam.farClipPlane = mb.size.magnitude * 2f;
                    }
                    else
                    {
                        camGo.transform.position = new Vector3(0f, 120f, -200f);
                        camGo.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                    }

                    var freeCamGo = new GameObject("FreeCamera");
                    freeCamGo.transform.position = new Vector3(0f, 80f, -110f);
                    var freeCam = freeCamGo.AddComponent<Camera>();
                    freeCam.farClipPlane = 800f;
                    freeCam.depth = 1f;
                    freeCamGo.AddComponent<AudioListener>();
                    freeCamGo.AddComponent<FreeCamera>();
                    // 俯视/穿墙观战需要看到建筑内部：屏蔽 ceiling 层；
                    // 区域填充盘（MapZone）只在地图视角可见，主视角只看描边。
                    int ceilLayer = LayerMask.NameToLayer(MapLayers.CeilingName);
                    if (ceilLayer >= 0)
                        freeCam.cullingMask &= ~(1 << ceilLayer);
                    int zoneLayer = LayerMask.NameToLayer(MapLayers.ZoneName);
                    if (zoneLayer >= 0)
                        freeCam.cullingMask &= ~(1 << zoneLayer);

                    // HGTR 光照策略：只用地图自带灯（LGT_*），去除场景光照——
                    // 关闭地图外的灯（遗留 Directional Light）、环境光置黑、天空盒置空。
                    foreach (var l in Object.FindObjectsOfType<Light>(true))
                    {
                        if (l.transform.root.name == HGTRMapRootName) continue;
                        l.gameObject.SetActive(false);
                    }
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = Color.black;
                    RenderSettings.skybox = null;

                    // 导入场景可能自带 Unity 默认 Main Camera（含 AudioListener）：
                    // 移除多余的监听器/冗余相机，保证全场唯一 AudioListener 在 FreeCamera。
                    foreach (var al in Object.FindObjectsOfType<AudioListener>())
                        if (al.gameObject != freeCamGo)
                            Object.DestroyImmediate(al.gameObject.GetComponent<AudioListener>());

                    // Blender 导入的室内填充灯（120+ Point/Spot）默认全部开阴影，
                    // 远超 HDRP maxShadowRequests → 每帧刷 "Max shadow requests" 警告。
                    // 填充灯不需要阴影：全部关闭，仅保留 Directional 的阴影。
                    int shadowOff = 0;
                    foreach (var l in Object.FindObjectsOfType<Light>(true))
                    {
                        if (l.type == LightType.Directional) continue;
                        if (l.shadows != LightShadows.None)
                        {
                            l.shadows = LightShadows.None;
                            EditorUtility.SetDirty(l);
                            shadowOff++;
                        }
                    }
                    if (shadowOff > 0)
                        Debug.Log($"[NetworkSetup] Disabled shadows on {shadowOff} fill lights (kept directional).");

                    // --- 9) NavMesh 重烘（排除 ceiling；先于实体生成）---
                    BuildNavMeshForMapRoot(HGTRMapRootName);

                    // --- 10) 30v30 FSM AI：红方驻西安全区，蓝方驻东安全区 ---
                    // 真人固定红队（NetworkMatchManager.AssignCombatant），红 AI 只生成
                    // 29 人，第 30 个红名额留给 Host 玩家（autoCreatePlayer 自动生成）。
                    CreateFSMTeamAround(fsmPrefab, (int)MatchTeam.Red, 29, grWest.center);
                    CreateFSMTeamAround(fsmPrefab, (int)MatchTeam.Blue, 30, grEast.center);

                    // 玩家出生点：红 GR 内避开 AI 驻扎网格（AI z ≤ center.z-1），贴 NavMesh。
                    var spawnGo = new GameObject("PlayerStart");
                    spawnGo.transform.position = new Vector3(
                        grWest.center.x + 6f, 1.5f, grWest.center.z + 6f);
                    if (NavMesh.SamplePosition(spawnGo.transform.position, out NavMeshHit spawnHit, 6f, NavMesh.AllAreas))
                        spawnGo.transform.position = spawnHit.position + Vector3.up * 0.1f;
                    spawnGo.AddComponent<Mirror.NetworkStartPosition>();

                    // uGUI 点击（部署地图/装备栏）需要 EventSystem + Input System 模块。
                    EnsureEventSystem();

                    // --- 11) 指挥官 rig（PHASE11 符号化：共享 HexGrid，边界用全图 AABB）---
                    CommanderSetup.Setup(MapLayers.TryGetMapBounds(out var cmdB) &&
                                         cmdB.size.x > 0.01f
                        ? (Bounds?)cmdB : null);

                    // --- 12) 保障 ceiling 处于激活（NavMesh 烘焙会临时隐藏它）---
                    foreach (var r in mapRoot.GetComponentsInChildren<Renderer>(true))
                        if (r.name.ToLowerInvariant().Contains("ceiling"))
                            r.gameObject.SetActive(true);

                    // --- 13) 保存 ---
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                    AssetDatabase.SaveAssets();
                    Debug.Log("[NetworkSetup] Done. HGTR battle scene ready: " +
                              "2 safe zones, 3 capture points, 30v30 FSM AI, commander rig, NavMesh (ceiling excluded).");
                }

                [MenuItem("HagenDa/Create Map_v1 Battle Scene (1 Human + 59 AI)")]
                public static void CreateMapV1BattleScene()
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    if (!scene.name.StartsWith("Map_v1"))
                    {
                        Debug.LogError($"[NetworkSetup] Active scene '{scene.name}' is not Map_v1. Open Map_v1.unity first.");
                        return;
                    }

                    EnsureFolder("Assets/Game", "Prefabs");
                    EnsureFolder("Assets/Game", "Equipment");
                    EnsureMapLayers();
                    EnsureLayerNamed(MapLayers.GroundName);
                    int groundLayer = LayerMask.NameToLayer(MapLayers.GroundName);
                    if (groundLayer < 0)
                    {
                        Debug.LogError($"[NetworkSetup] Missing layer '{MapLayers.GroundName}'.");
                        return;
                    }

                    // --- 1) 地图层归类（幂等）：三个地图根的全部子物体 → Ground ---
                    int movedGround = 0;
                    foreach (var rootName in new[] { "Map_v1", "Map_v2", "GroundFloor_Grid" })
                    {
                        var mapRoot = GameObject.Find(rootName);
                        if (mapRoot == null)
                        {
                            Debug.LogError($"[NetworkSetup] Map root '{rootName}' not found in scene.");
                            return;
                        }
                        foreach (var t in mapRoot.GetComponentsInChildren<Transform>(true))
                        {
                            if (t == mapRoot.transform) continue;
                            if (t.gameObject.layer != groundLayer)
                            {
                                t.gameObject.layer = groundLayer;
                                movedGround++;
                            }
                        }
                    }
                    Debug.Log($"[NetworkSetup] Layers: {movedGround} map objects on '{MapLayers.GroundName}'.");

                    // --- 2) Prefabs / assets (idempotent) ---
                    List<EquipmentDefinition> equipmentList = BuildEquipmentAssets();
                    GameObject grenadePrefab = BuildGrenadePrefab();
                    GameObject smokePrefab = BuildSmokePrefab();
                    GameObject rescuePrefab = BuildRescuePrefab();
                    GameObject bulletPrefab = BuildBulletPrefab();
                    GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
                    GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
                    GameObject fsmPrefab = BuildFSMAIPrefab(aiPrefab);

                    // --- 3) 幂等清理旧对局对象 ---
                    ClearOld("MatchManager", "NetworkMatchManager", "StrategicZoneRegistry", "FSMBattleSystem",
                             "RedGR", "BlueGR", "Zone_A", "Zone_B", "Zone_C",
                             "StrategicZone_A", "StrategicZone_B", "StrategicZone_C",
                             "NetworkManager", "BattleCamera", "FreeCamera", "SquadCommander",
                             "PlayerStart");
                    ClearAllFSMEntities();

                    // --- 4) 布局（用户指定；地面统一 y=0.04）---
                    const float groundY = 0.04f;
                    var redPos = new Vector3(42f, groundY, 48f);
                    // 蓝GR 规格 (-36,10,-42) 是 Map_v2/Static 组的本地坐标（该组带 270°
                    // 旋转与平移），换算世界 ≈ (48.14, ·, -269.57)，落在南区高架路砖
                    // 上（顶面 y=1.63）；红GR 规格与世界地面吻合，按世界坐标直用。
                    var bluePos = new Vector3(48.14f, 1.63f, -269.57f);
                    var posA = new Vector3(22f, groundY, 3f);
                    var posB = new Vector3(62f, groundY, -111f);
                    var posC = new Vector3(-17f, groundY, -211f);

                    // --- 5) 安全区 ×2（矩形，半宽/半深 24m = 48×48m）---
                    var redSafe = CreateGarrison("RedGR", redPos, (int)MatchTeam.Red, 24f);
                    redSafe.extent = new Vector2(24f, 24f);
                    var blueSafe = CreateGarrison("BlueGR", bluePos, (int)MatchTeam.Blue, 24f);
                    blueSafe.extent = new Vector2(24f, 24f);
                    AttachDeployPointTemplate(redSafe.gameObject, 4,
                        Mathf.Min(redSafe.extent.x, redSafe.extent.y) * 0.8f);
                    AttachDeployPointTemplate(blueSafe.gameObject, 4,
                        Mathf.Min(blueSafe.extent.x, blueSafe.extent.y) * 0.8f);

                    // --- 6) 据点 ×3（矩形，半宽/半深 20m = 40×40m）+ StrategicZone 包装 ---
                    var zoneA = CreateCapturePoint("Zone_A", posA, 20f);
                    zoneA.extent = new Vector2(20f, 20f);
                    var zoneB = CreateCapturePoint("Zone_B", posB, 20f);
                    zoneB.extent = new Vector2(20f, 20f);
                    var zoneC = CreateCapturePoint("Zone_C", posC, 20f);
                    zoneC.extent = new Vector2(20f, 20f);
                    zoneA.letter = "A"; zoneB.letter = "B"; zoneC.letter = "C";
                    AttachDeployPointTemplate(zoneA.gameObject, 2,
                        Mathf.Min(zoneA.extent.x, zoneA.extent.y) * 0.8f);
                    AttachDeployPointTemplate(zoneB.gameObject, 2,
                        Mathf.Min(zoneB.extent.x, zoneB.extent.y) * 0.8f);
                    AttachDeployPointTemplate(zoneC.gameObject, 2,
                        Mathf.Min(zoneC.extent.x, zoneC.extent.y) * 0.8f);
                    CreateStrategicZone("StrategicZone_A", zoneA);
                    CreateStrategicZone("StrategicZone_B", zoneB);
                    CreateStrategicZone("StrategicZone_C", zoneC);

                    // --- 7) 对局系统（持续战斗 30v30：6 小队 × 5 人）---
                    var mmGo = new GameObject("NetworkMatchManager");
                    var mm = mmGo.AddComponent<NetworkMatchManager>();
                    mm.winScore = 999999;
                    mm.squadsPerTeam = 6;
                    mm.squadSize = 5;
                    mm.redeployDelay = 10f;
                    mm.garrisons = new List<GarrisonZone> { redSafe, blueSafe };
                    mm.capturePoints = new List<CapturePoint> { zoneA, zoneB, zoneC };

                    var registryGo = new GameObject("StrategicZoneRegistry");
                    registryGo.AddComponent<StrategicZoneRegistry>();

                    var systemGo = new GameObject("FSMBattleSystem");
                    systemGo.AddComponent<FSMBattleSystem>();
                    systemGo.AddComponent<FSMStatsHud>();

                    var cmdGo = new GameObject("SquadCommander");
                    cmdGo.AddComponent<SquadCommander>();

                    // --- 8) NetworkManager（Play 即 Host；真人自动生成、固定红队）---
                    var nmGo = new GameObject("NetworkManager");
                    var nm = nmGo.AddComponent<NetworkManager>();
                    nm.playerPrefab = playerPrefab;
                    nm.autoCreatePlayer = true;   // 真人固定红队，红 AI 只生成 29 人留位
                    var kcp = nmGo.AddComponent<kcp2k.KcpTransport>();
                    nm.transport = kcp;
                    nmGo.AddComponent<TrainingAutoHost>();
                    RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab, fsmPrefab);
                    foreach (var def in equipmentList)
                        if (def != null && def.throwablePrefab != null)
                            RegisterSpawnPrefabs(def.throwablePrefab);
                    var empField = AssetDatabase.LoadAssetAtPath<GameObject>(EmpFieldPrefabPath);
                    if (empField != null) RegisterSpawnPrefabs(empField);

                    // --- 9) 观战相机（机位/远裁剪面自适应全图 AABB）---
                    // BattleCamera 默认禁用：FreeCamera(depth=1) 每帧完整覆盖它，
                    // 双相机 = 每帧渲染场景两遍（严重浪费，低端卡直接掉帧）。
                    // 需要全景机位时手动启用本物体即可。
                    MapLayers.TryGetMapBounds(out var mb);
                    var camGo = new GameObject("BattleCamera");
                    var cam = camGo.AddComponent<Camera>();
                    camGo.SetActive(false);
                    if (mb.size != Vector3.zero)
                    {
                        camGo.transform.position = new Vector3(
                            mb.center.x,
                            Mathf.Max(mb.size.x, mb.size.z) * 0.8f,
                            mb.center.z - mb.size.z * 0.7f);
                        camGo.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                        cam.farClipPlane = mb.size.magnitude * 2f;
                    }
                    else
                    {
                        camGo.transform.position = new Vector3(52f, 300f, -380f);
                        camGo.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                        cam.farClipPlane = 1200f;
                    }

                    var freeCamGo = new GameObject("FreeCamera");
                    freeCamGo.transform.position = mb.size != Vector3.zero
                        ? new Vector3(mb.center.x, 120f, mb.center.z)
                        : new Vector3(52f, 120f, -120f);
                    var freeCam = freeCamGo.AddComponent<Camera>();
                    freeCam.farClipPlane = 1200f;
                    freeCam.depth = 1f;
                    freeCamGo.AddComponent<AudioListener>();
                    freeCamGo.AddComponent<FreeCamera>();
                    // 区域填充盘（MapZone）只在地图视角可见，主视角只看描边。
                    int zoneLayer = LayerMask.NameToLayer(MapLayers.ZoneName);
                    if (zoneLayer >= 0)
                        freeCam.cullingMask &= ~(1 << zoneLayer);

                    // 灯光：保留场景自带光照（此图有烘焙 GI 与天空盒），仅关闭
                    // 非平行光的阴影（填充灯不需要阴影，防止 HDRP shadow request 超限）。
                    int shadowOff = 0;
                    foreach (var l in Object.FindObjectsOfType<Light>(true))
                    {
                        if (l.type == LightType.Directional) continue;
                        if (l.shadows != LightShadows.None)
                        {
                            l.shadows = LightShadows.None;
                            EditorUtility.SetDirty(l);
                            shadowOff++;
                        }
                    }
                    if (shadowOff > 0)
                        Debug.Log($"[NetworkSetup] Disabled shadows on {shadowOff} fill lights (kept directional).");

                    // --- 10) NavMesh 全图烘焙（先于实体生成）---
                    BuildNavMeshForMapRoot("GroundFloor_Grid");

                    // --- 11) 59 FSM AI：红 29（真人占第 30 席）+ 蓝 30 ---
                    // 出生高度 = 各自安全区地面 + 1.5（蓝GR 在 1.63 高架路砖上）。
                    CreateFSMTeamAround(fsmPrefab, (int)MatchTeam.Red, 29, redPos, redPos.y + 1.5f);
                    CreateFSMTeamAround(fsmPrefab, (int)MatchTeam.Blue, 30, bluePos, bluePos.y + 1.5f);

                    // 玩家出生点：红 GR 内避开 AI 驻扎网格（AI z ≤ center.z-1），贴 NavMesh。
                    var spawnGo = new GameObject("PlayerStart");
                    spawnGo.transform.position = redPos + new Vector3(6f, 1.5f, 6f);
                    if (NavMesh.SamplePosition(spawnGo.transform.position, out NavMeshHit spawnHit, 6f, NavMesh.AllAreas))
                        spawnGo.transform.position = spawnHit.position + Vector3.up * 0.1f;
                    spawnGo.AddComponent<Mirror.NetworkStartPosition>();

                    // uGUI 点击（部署地图/装备栏）需要 EventSystem + Input System 模块。
                    EnsureEventSystem();

                    // --- 12) 指挥官 rig（PHASE11 符号化：HexGrid 边界直接用全图 AABB——
                    // 本图墙体零散，"*wall*" 名字推导严重失真）---
                    CommanderSetup.Setup(mb.size != Vector3.zero ? (Bounds?)mb : null);

                    // --- 13) 保存 ---
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                    AssetDatabase.SaveAssets();
                    Debug.Log("[NetworkSetup] Done. Map_v1 battle scene ready: " +
                              "2 rect safe zones (red 42,48 / blue 48.14,-269.57), 3 rect capture points " +
                              "A(22,3) B(62,-111) C(-17,-211), 1 human (red) + 59 FSM AI, " +
                              "commander rig, NavMesh baked, BattleCamera disabled (FreeCamera only).");
                }

                internal static void CreateFSMTeamAround(GameObject fsmPrefab, int team, int count, Vector3 center, float spawnY = 1.5f)
                {
                    string teamName = team == (int)MatchTeam.Red ? "Red" : "Blue";
                    for (int i = 0; i < count; i++)
                    {
                        int squadIndex = i / 5;
                        int inSquad = i % 5;

                        var go = (GameObject)PrefabUtility.InstantiatePrefab(fsmPrefab);
                        go.name = $"FSM_{teamName}_S{squadIndex}_{i}";
                        float x = center.x - 10f + inSquad * 4.5f + (squadIndex % 3) * 1.5f;
                        float z = center.z - 8f + (squadIndex / 3) * 5f + (squadIndex % 2) * 2f;
                        go.transform.position = new Vector3(x, spawnY, z);

                        var fsm = go.GetComponent<FSMAIController>();
                        if (fsm != null)
                            fsm.aiClass = inSquad < 2 ? FsmClass.Assault
                                        : inSquad < 4 ? FsmClass.Support
                                        : FsmClass.Recon;
                    }
                }

                private static void CreateFSMTeam(GameObject fsmPrefab, int team, int count, float zLine)
                {
                    CreateFSMTeam(fsmPrefab, team, count, zLine, FsmClass.Assault);
                }

                private static void CreateFSMTeam(GameObject fsmPrefab, int team, int count, float zLine, FsmClass classOverride)
                {
                    string teamName = team == (int)MatchTeam.Red ? "Red" : "Blue";
                    for (int i = 0; i < count; i++)
                    {
                        int squadIndex = i / 5;
                        int inSquad = i % 5;

                        var go = (GameObject)PrefabUtility.InstantiatePrefab(fsmPrefab);
                        go.name = $"FSM_{teamName}_S{squadIndex}_{i}";
                        float x = -9f + inSquad * 4.5f + (squadIndex % 3) * 1.8f;
                        float z = zLine + (squadIndex / 3) * 5f;
                        go.transform.position = new Vector3(x, 1.5f, z);

                        var fsm = go.GetComponent<FSMAIController>();
                        if (fsm != null)
                        {
                            if (classOverride != FsmClass.Assault)
                            {
                                fsm.aiClass = classOverride;
                            }
                            else
                            {
                                fsm.aiClass = inSquad < 2 ? FsmClass.Assault
                                            : inSquad < 4 ? FsmClass.Support
                                            : FsmClass.Recon;
                            }
                        }
                    }
                }

                private static void BuildFSMCoverField(float mapW, float mapL)
                {
                    var root = new GameObject("Covers");
                    root.AddComponent<CoverRegistrar>();

                    var rng = new System.Random(20260825);
                    const int count = 64;
                    const float minGap = 5f;

                    var zones = new[]
                    {
                        new Vector2(-18f, -45f), Vector2.zero, new Vector2(18f, 45f)
                    };
                    var garrisons = new[]
                    {
                        new Vector2(0f, -mapL * 0.42f), new Vector2(0f, mapL * 0.42f)
                    };

                    var placed = new List<Vector2>();
                    int made = 0;
                    int guard = 0;
                    while (made < count && guard++ < count * 40)
                    {
                        float x = ((float)rng.NextDouble() * 2f - 1f) * (mapW * 0.5f - 6f);
                        float z = ((float)rng.NextDouble() * 2f - 1f) * (mapL * 0.5f - 12f);
                        var p = new Vector2(x, z);

                        bool ok = true;
                        foreach (var zc in zones)
                            if (Vector2.Distance(p, zc) < 14f) { ok = false; break; }
                        if (ok)
                            foreach (var g in garrisons)
                                if (Vector2.Distance(p, g) < 22f) { ok = false; break; }
                        if (ok)
                            foreach (var q in placed)
                                if (Vector2.Distance(p, q) < minGap) { ok = false; break; }
                        if (!ok) continue;

                        placed.Add(p);

                        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        go.transform.SetParent(root.transform, false);
                        go.transform.localPosition = new Vector3(p.x, 0f, p.y);
                        go.transform.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

                        int roll = rng.Next(100);
                        if (roll < 35)
                        {
                            go.name = "Cover_Tall";
                            go.transform.localScale = new Vector3(2f, 2.2f, 0.6f);
                            go.transform.localPosition = new Vector3(p.x, 1.1f, p.y);
                        }
                        else if (roll < 70)
                        {
                            go.name = "Cover_Low";
                            go.transform.localScale = new Vector3(2f, 1.0f, 0.8f);
                            go.transform.localPosition = new Vector3(p.x, 0.5f, p.y);
                        }
                        else if (roll < 88)
                        {
                            go.name = "Cover_High";
                            go.transform.localScale = new Vector3(3f, 1.4f, 3f);
                            go.transform.localPosition = new Vector3(p.x, 0.7f, p.y);
                        }
                        else
                        {
                            go.name = "Cover_Ramp";
                            go.transform.localScale = new Vector3(3f, 1.2f, 6f);
                            go.transform.localPosition = new Vector3(p.x, 0.6f, p.y);
                        }
                        made++;
                    }

                    Debug.Log($"[NetworkSetup] FSM cover field: {made} covers (seed 20260825).");
                }
    }
}
