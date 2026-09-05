using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem.UI;
using Unity.AI.Navigation;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// One-shot editor utility that:
    ///  1. Builds the server-authoritative NetworkPlayer prefab.
    ///  2. Sets up the active scene with a NetworkManager (KCP transport),
    ///     a HUD, spawn points, a floor, and a couple of shootable targets.
    /// Run once via menu: HagenDa/Setup Multiplayer Scene.
    /// </summary>
    public static class NetworkSetup
    {
        private const string PrefabPath = "Assets/Scripts/Network/Prefabs/NetworkPlayer.prefab";
        private const string GrenadePrefabPath = "Assets/Scripts/Network/Prefabs/GrenadeThrowable.prefab";
        private const string SmokePrefabPath = "Assets/Scripts/Network/Prefabs/SmokeThrowable.prefab";
        private const string RescuePrefabPath = "Assets/Scripts/Network/Prefabs/RescueThrowable.prefab";
        private const string BulletPrefabPath = "Assets/Scripts/Network/Prefabs/Bullet.prefab";
        private const string AIPrefabPath = "Assets/Scripts/Network/Prefabs/AIEntity.prefab";
        private const string WeaponFolder = "Assets/Scripts/Network/Weapons";
        private const string M4DefinitionPath = "Assets/Scripts/Network/Weapons/M4Definition.asset";
        private const string M4PrefabPath = "Assets/Low Poly Weapons VOL.1/Prefabs/M4_8.prefab";

        private const string EquipmentFolder = "Assets/Scripts/Network/Equipment";
        private const string EmpFieldPrefabPath = "Assets/Scripts/Network/Prefabs/EmpField.prefab";
        private const string HandGrenadePrefabPath = "Assets/Scripts/Network/Prefabs/HandGrenade.prefab";
        private const string SmokeGrenadePrefabPath = "Assets/Scripts/Network/Prefabs/SmokeGrenade.prefab";
        private const string EmpGrenadePrefabPath = "Assets/Scripts/Network/Prefabs/EmpGrenadeThrowable.prefab";
        private const string LauncherGrenadePrefabPath = "Assets/Scripts/Network/Prefabs/LauncherGrenade.prefab";
        private const string LauncherSmokePrefabPath = "Assets/Scripts/Network/Prefabs/LauncherSmoke.prefab";
        private const string RpgPrefabPath = "Assets/Scripts/Network/Prefabs/Rpg.prefab";
        private const string SignalChargePrefabPath = "Assets/Scripts/Network/Prefabs/SignalCharge.prefab";
        private const string WiredChargePrefabPath = "Assets/Scripts/Network/Prefabs/WiredCharge.prefab";
        private const string DelayedBombPrefabPath = "Assets/Scripts/Network/Prefabs/DelayedBomb.prefab";
        private const string SupplyPackPrefabPath = "Assets/Scripts/Network/Prefabs/SupplyPack.prefab";
        private const string LargeSupplyCratePrefabPath = "Assets/Scripts/Network/Prefabs/LargeSupplyCrate.prefab";
        private const string InterceptorPrefabPath = "Assets/Scripts/Network/Prefabs/Interceptor.prefab";
        private const string SensorProbePrefabPath = "Assets/Scripts/Network/Prefabs/SensorProbe.prefab";
        private const string DeployBeaconPrefabPath = "Assets/Scripts/Network/Prefabs/DeployBeacon.prefab";

        private const string RgdModelPath = "Assets/Low Poly Weapons VOL.1/Prefabs/RGD-5.prefab";
        private const string SmokeModelPath = "Assets/Low Poly Weapons VOL.1/Prefabs/Smoke.prefab";
        private const string FlashModelPath = "Assets/Low Poly Weapons VOL.1/Prefabs/Flash.prefab";
        private const string RpgModelPath = "Assets/Low Poly Weapons VOL.1/Prefabs/RPG7.prefab";

        [MenuItem("HagenDa/Setup Multiplayer Scene")]
        public static void Setup()
        {
            EnsureFolder("Assets/Scripts/Network", "Prefabs");

            GameObject playerPrefab = BuildPlayerPrefab();

            SetupScene(playerPrefab);

            SaveActiveScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Built player prefab and configured the active scene.");
        }

        [MenuItem("HagenDa/Create Physics Movement Scene")]
        public static void CreatePhysicsMovementScene()
        {
            EnsureFolder("Assets", "Scenes");

            GameObject playerPrefab = BuildPlayerPrefab();

            // Start from a fresh, empty scene.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();

            SetupScene(playerPrefab, addTargets: true);

            // A wall to verify the rigidbody collides with static geometry.
            CreateWall(new Vector3(0f, 1.5f, -6f), new Vector3(10f, 3f, 1f));

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Scenes/PhysicsMovement.scene");
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Created Assets/Scenes/PhysicsMovement.scene with the force-driven player.");
        }

        [MenuItem("HagenDa/Rebuild NetworkPlayer Prefab")]
        public static void RebuildPlayerPrefab()
        {
            EnsureFolder("Assets/Scripts/Network", "Prefabs");

            // Re-serialize the prefab from the current script defaults WITHOUT
            // touching the active scene.
            BuildPlayerPrefab();
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Rebuilt " + PrefabPath);
        }

        // ---------------------------------------------------------------
        // M1: TRAINING SCENE (ML-TRAINING.md 附录 C)
        // ---------------------------------------------------------------

        private const string TrainingScenePath = "Assets/Scenes/TrainingArena.scene";
        private const string TrainingMapsFolder = "Assets/Scripts/Network/TrainingMaps";

        [MenuItem("HagenDa/Create ML Training Scene")]
        public static void CreateTrainingScene()
        {
            CreateTrainingSceneInternal(5, 5, false, TrainingScenePath, "5v5");
        }

        [MenuItem("HagenDa/Create S1 Training Scene (1v1 vs target)")]
        public static void CreateS1TrainingScene()
        {
            CreateS1MultiAreaScene(100, 120f, 3);
        }

        private const string S1TrainingScenePath = "Assets/Scenes/S1Training.scene";

        /// <summary>
        /// S1 多区域训练场景（加速采样：20 个并行 1v1 场地，x 轴排列，间距 200m
        /// 保证射线/索敌不跨区）。所有区域共享一个 TrainingSessionManager——
        /// 全灭判定基于全部实体（S1 靶不反击，几乎不会全灭）。
        /// </summary>
        private static void CreateS1MultiAreaScene(int areaCount, float spacing, int targetsPerArea)
        {
            EnsureFolder("Assets", "Scenes");
            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            EnsureFolder("Assets/Scripts/Network", "TrainingMaps");

            // Prefabs (idempotent).
            GameObject grenadePrefab = BuildGrenadePrefab();
            GameObject smokePrefab = BuildSmokePrefab();
            GameObject rescuePrefab = BuildRescuePrefab();
            GameObject bulletPrefab = BuildBulletPrefab();
            GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab);
            GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab);
            GameObject scriptedPrefab = BuildScriptedAIPrefab(aiPrefab);

            // 紧凑 S1 专用地图：30×30m，少量掩体。
            var s1Map = GetOrCreateTrainingMap("TrainingMap_S1Compact", TrainingMap.MapShape.Square, 42);
            s1Map.size = 30f;
            s1Map.depth = 30f;
            s1Map.coverCount = 4;
            s1Map.homeOffset = 12f;
            s1Map.zoneRadius = 5f;
            EditorUtility.SetDirty(s1Map);

            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();

            // NetworkManager + MatchManager（全场景一份）。
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            nm.playerPrefab = playerPrefab;
            nm.autoCreatePlayer = false;
            var kcp = nmGo.AddComponent<kcp2k.KcpTransport>();
            nm.transport = kcp;
            nmGo.AddComponent<TrainingAutoHost>();   // Play 后自动 StartHost
            RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab, scriptedPrefab);

            var mmGo = new GameObject("NetworkMatchManager");
            var mm = mmGo.AddComponent<NetworkMatchManager>();
            mm.winScore = 999999;
            mm.squadsPerTeam = 1;
            mm.redeployDelay = 10f;
            mm.garrisons = new List<GarrisonZone>();

            // Session Manager（全场景一份）。
            var sessionGo = new GameObject("TrainingSessionManager");
            sessionGo.AddComponent<NetworkIdentity>();
            var session = sessionGo.AddComponent<TrainingSessionManager>();
            session.maps = new List<TrainingMap> { s1Map };
            session.roundDuration = 60f;   // 紧凑场地用短回合

            // Zone Registry（观测用）。
            var registryGo = new GameObject("StrategicZoneRegistry");
            registryGo.AddComponent<StrategicZoneRegistry>();

            // 100 区域：x = i * 120（紧凑场地间距缩小），红 z=-12 / 蓝 z=+12。
            for (int i = 0; i < areaCount; i++)
            {
                float cx = i * spacing;

                var areaGo = new GameObject($"Area_{i}");
                areaGo.transform.position = new Vector3(cx, 0f, 0f);

                var arena = areaGo.AddComponent<TrainingArena>();
                s1Map.BuildLayout();
                arena.ApplyLayout(s1Map);

                var grRed = CreateTrainingGarrison($"GR_Red_{i}", (int)MatchTeam.Red,
                    new Vector3(cx, 0f, -12f));
                mm.garrisons.Add(grRed);

                // 1 ML agent。
                var red = (GameObject)PrefabUtility.InstantiatePrefab(aiPrefab);
                red.name = $"ML_Red_{i}";
                red.transform.position = new Vector3(cx, 1.5f, -12f);

                // N 个靶：z=+8..+14，x 分散（保证互相间距 ≥4m，且离 ML ≥ 10m）。
                for (int t = 0; t < targetsPerArea; t++)
                {
                    float tx = cx + (t - (targetsPerArea - 1) * 0.5f) * 6f;
                    float tz = 8f + (t % 3) * 3f;   // 8, 11, 14

                    var blue = (GameObject)PrefabUtility.InstantiatePrefab(scriptedPrefab);
                    blue.name = $"Target_Blue_{i}_{t}";
                    blue.transform.position = new Vector3(tx, 1.5f, tz);
                    var scripted = blue.GetComponent<ScriptedAIController>();
                    if (scripted != null) scripted.targetMode = true;
                }

                // NavMesh（区域级）。
                BuildNavMeshOnFloor("Floor");
            }

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, S1TrainingScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[NetworkSetup] Done. Created {S1TrainingScenePath} " +
                      $"({areaCount} areas x {targetsPerArea} targets, spacing {spacing}m).");
        }

        private static void CreateTrainingSceneInternal(int mlCount, int scriptedCount,
            bool s1TargetMode, string scenePath, string label)
        {
            EnsureFolder("Assets", "Scenes");
            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            EnsureFolder("Assets/Scripts/Network", "TrainingMaps");

            // Prefabs (idempotent).
            GameObject grenadePrefab = BuildGrenadePrefab();
            GameObject smokePrefab = BuildSmokePrefab();
            GameObject rescuePrefab = BuildRescuePrefab();
            GameObject bulletPrefab = BuildBulletPrefab();
            GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab);
            GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab);
            GameObject scriptedPrefab = BuildScriptedAIPrefab(aiPrefab);

            // Map assets: Square + Wave（附录 C 两类，种子可复现）。
            var squareMap = GetOrCreateTrainingMap("TrainingMap_Square", TrainingMap.MapShape.Square, 12345);
            var waveMap = GetOrCreateTrainingMap("TrainingMap_Wave", TrainingMap.MapShape.Wave, 67890);

            // Fresh empty scene.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();

            // NetworkManager（无 HUD——训练全自动；禁自动生成真人玩家）。
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            nm.playerPrefab = playerPrefab;
            nm.autoCreatePlayer = false;
            var kcp = nmGo.AddComponent<kcp2k.KcpTransport>();
            nm.transport = kcp;
            RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab, scriptedPrefab);

            // MatchManager：注册表/击杀计分/重部署查询（训练场景无据点无胜利分）。
            var mmGo = new GameObject("NetworkMatchManager");
            var mm = mmGo.AddComponent<NetworkMatchManager>();
            mm.winScore = 999999;          // 训练回合由 TrainingSessionManager 驱动
            mm.squadsPerTeam = 1;           // Q9-B：单小队
            mm.redeployDelay = 10f;

            // 双方 GR（home 重生点，供死亡自动重部署）。
            var grRed = CreateTrainingGarrison("GR_Red", (int)MatchTeam.Red, new Vector3(0f, 0f, -24f));
            var grBlue = CreateTrainingGarrison("GR_Blue", (int)MatchTeam.Blue, new Vector3(0f, 0f, 24f));
            mm.garrisons = new List<GarrisonZone> { grRed, grBlue };

            // Arena + Session Manager + Zone Registry。
            var arenaGo = new GameObject("TrainingArena");
            arenaGo.AddComponent<TrainingArena>();

            var sessionGo = new GameObject("TrainingSessionManager");
            sessionGo.AddComponent<NetworkIdentity>();
            var session = sessionGo.AddComponent<TrainingSessionManager>();
            session.arena = arenaGo.GetComponent<TrainingArena>();
            session.maps = new List<TrainingMap> { squareMap, waveMap };
            session.roundDuration = s1TargetMode ? 90f : 120f;

            var registryGo = new GameObject("StrategicZoneRegistry");
            registryGo.AddComponent<StrategicZoneRegistry>();

            // Central abstract zone（纯要地 marker：无争夺机制，中央位置）。
            var zoneGo = new GameObject("CentralZone");
            zoneGo.transform.position = Vector3.zero;
            zoneGo.AddComponent<StrategicZone>();

            // Teams：红 ML + 蓝 Scripted。出生按 z 分队（红 z<0 / 蓝 z>0）。
            for (int i = 0; i < mlCount; i++)
            {
                var red = (GameObject)PrefabUtility.InstantiatePrefab(aiPrefab);
                red.name = "ML_Red_" + i;
                red.transform.position = new Vector3((i - (mlCount - 1) * 0.5f) * 1.5f, 1.5f, -24f);
            }
            for (int i = 0; i < scriptedCount; i++)
            {
                var blue = (GameObject)PrefabUtility.InstantiatePrefab(scriptedPrefab);
                blue.name = s1TargetMode ? "Target_Blue_" + i : "Scripted_Blue_" + i;
                blue.transform.position = new Vector3((i - (scriptedCount - 1) * 0.5f) * 1.5f, 1.5f, 24f);

                if (s1TargetMode)
                {
                    var scripted = blue.GetComponent<ScriptedAIController>();
                    if (scripted != null) scripted.targetMode = true;
                }
            }

            // 初始几何（编辑期预览；运行时每回合 ApplyLayout 重建）。
            squareMap.BuildLayout();
            arenaGo.GetComponent<TrainingArena>().ApplyLayout(squareMap);

            // NavMesh（脚本陪练用；ML AI 不依赖）。
            BuildNavMeshOnFloor("Floor");

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[NetworkSetup] Done. Created {scenePath} ({label}).");
        }

        private static GarrisonZone CreateTrainingGarrison(string name, int team, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            var gz = go.AddComponent<GarrisonZone>();
            gz.teamId = team;
            gz.radius = 6f;

            // 4 个部署点（home 周围散开）。
            for (int i = 0; i < 4; i++)
            {
                var dp = new GameObject("DeployPoint_" + i);
                dp.transform.SetParent(go.transform, false);
                float ang = i * Mathf.PI * 0.5f;
                dp.transform.localPosition = new Vector3(Mathf.Cos(ang) * 3f, 0f, Mathf.Sin(ang) * 3f);
                gz.deployPoints.Add(dp.transform);
            }
            return gz;
        }

        private static TrainingMap GetOrCreateTrainingMap(string name, TrainingMap.MapShape shape, int seed)
        {
            var path = $"{TrainingMapsFolder}/{name}.asset";
            var map = AssetDatabase.LoadAssetAtPath<TrainingMap>(path);
            if (map == null)
            {
                map = ScriptableObject.CreateInstance<TrainingMap>();
                AssetDatabase.CreateAsset(map, path);
            }
            map.shape = shape;
            map.layoutSeed = seed;
            EditorUtility.SetDirty(map);
            return map;
        }

        /// <summary>脚本陪练 prefab：ML AI prefab 克隆 + ScriptedAIController 替换桥。</summary>
        private static GameObject BuildScriptedAIPrefab(GameObject aiPrefab)
        {
            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            const string path = "Assets/Scripts/Network/Prefabs/ScriptedAIEntity.prefab";

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

        /// <summary>在指定名字的地板上烘 NavMesh（训练场脚本陪练用）。</summary>
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

        // ---------------------------------------------------------------
        // PHASE9: FSM BATTLE SCENE (59 AI, 30 vs 29)
        // ---------------------------------------------------------------

        private const string FSMAIPrefabPath = "Assets/Scripts/Network/Prefabs/FSMAIEntity.prefab";

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
            string scenePath = "Assets/Scenes/" + sceneFile;
            EnsureFolder("Assets", "Scenes");
            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            EnsureFolder("Assets/Scripts/Network", "Equipment");
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

        /// <summary>FSM AI prefab：ML AI prefab 克隆 - MLAgentBridge + FSMAIController。</summary>
        internal static GameObject BuildFSMAIPrefab(GameObject aiPrefab)
        {
            EnsureFolder("Assets/Scripts/Network", "Prefabs");

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

        /// <summary>把一个 CapturePoint 包装成 StrategicZone（AI 只见抽象要地）。</summary>
        internal static void CreateStrategicZone(string name, CapturePoint cp)
        {
            var go = new GameObject(name);
            go.transform.position = cp.transform.position;
            var z = go.AddComponent<StrategicZone>();
            z.capturePoint = cp;
        }

        // ---------------------------------------------------------------
        // HGTR (Blender map): 30v30 FSM battle on the imported map
        // ---------------------------------------------------------------

        private const string HGTRMapRootName = "HGTR_map";

        /// <summary>
        /// 在当前已打开的 HGTR 场景上部署 30v30 FSM 对局（幂等，可重跑）：
        ///   - 地图层归类：地板/墙/掩体 → Ground，天花板 → ceiling
        ///     （小地图/大地图/指挥官快照可见 Ground、不可见 ceiling，室内可见）
        ///   - 3 据点（西庭院 / 中心峡谷 / 东庭院）+ 2 安全区（西=红 / 东=蓝）
        ///   - 每个安全区/据点挂 DeployPointSet 重部署点模板
        ///   - 对局系统 + StrategicZoneRegistry + FSMBattleSystem + 指挥官快照
        ///   - 30v30 FSM AI（红出西安全区，蓝出东安全区）
        ///   - NavMesh 重烘（排除 ceiling）
        /// </summary>
        [MenuItem("HagenDa/Create HGTR Battle Scene (30v30 FSM)")]
        public static void CreateHGTRBattleScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.name.StartsWith("HGTR"))
            {
                Debug.LogError($"[NetworkSetup] Active scene '{scene.name}' is not HGTR. Open HGTR.scene first.");
                return;
            }

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            EnsureFolder("Assets/Scripts/Network", "Equipment");
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

            // --- 11) 指挥官快照 rig（含 PHASE10 网格线/标尺/格子代号）---
            CommanderSetup.Setup();

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

        // ---------------------------------------------------------------
        // Map_v1 (RPG_FPS industrial kit): 1 human + 59 AI (30v30) battle
        // ---------------------------------------------------------------

        /// <summary>
        /// 在当前已打开的 Map_v1 场景上部署 30v30 对局（幂等，可重跑）：
        ///   - 地图几何（Map_v1 / Map_v2 / GroundFloor_Grid 全部子物体）归入
        ///     Ground 层（EntityQueryMask 排除静态几何，否则 1400+ 碰撞体
        ///     灌满 OverlapNonAlloc 感知 buffer，据点/安全区漏计战斗员）
        ///   - 矩形安全区：红GR(42, 0.04, 48) / 蓝GR 世界(48.14, 1.63, -269.57)，
        ///     半宽/半深 24m（48×48m）；蓝GR 规格 (-36,10,-42) 为 Map_v2/Static
        ///     组本地坐标，已换算世界坐标并吸附到高架路砖顶面
        ///   - 矩形据点：A(22, 0.04, 3) / B(62, 0.04, -111) / C(-17, 0.04, -211)，
        ///     半宽/半深 20m（40×40m）；B 原规格 (62,0,111) 在地面网格
        ///     之外（用户已确认应为 -111）
        ///   - 对局系统 + StrategicZoneRegistry + FSMBattleSystem + SquadCommander
        ///   - NetworkManager(KCP, Play 即 Host, autoCreatePlayer 真人固定红队)
        ///   - 29 红 AI + 30 蓝 AI（红队第 30 席留给真人）
        ///   - NavMesh 全图烘焙（先于实体生成，避免胶囊在出生点打出洞）
        /// </summary>
        [MenuItem("HagenDa/Create Map_v1 Battle Scene (1 Human + 59 AI)")]
        public static void CreateMapV1BattleScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.name.StartsWith("Map_v1"))
            {
                Debug.LogError($"[NetworkSetup] Active scene '{scene.name}' is not Map_v1. Open Map_v1.unity first.");
                return;
            }

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            EnsureFolder("Assets/Scripts/Network", "Equipment");
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

            // --- 12) 指挥官 rig（红/蓝 LLM 指挥官 + 门控 + 状态 + HUD）---
            CommanderSetup.Setup();

            // CommanderSetup 从 "*wall*" 物体推导地图边界——本图墙体零散，
            // 推导结果严重失真：用全图 AABB 修正红/蓝 Overlay（网格/标尺/
            // 格子代号在运行时 Start 读取这些字段，编辑期修正即可生效）。
            if (mb.size != Vector3.zero)
            {
                int fixedOverlays = 0;
                foreach (var ov in Object.FindObjectsOfType<CommanderMapOverlay>(true))
                {
                    ov.mapMinWorld = new Vector2(mb.min.x, mb.min.z);
                    ov.mapMaxWorld = new Vector2(mb.max.x, mb.max.z);
                    EditorUtility.SetDirty(ov);
                    fixedOverlays++;
                }
                Debug.Log($"[NetworkSetup] Commander overlays fixed to map AABB " +
                          $"X[{mb.min.x:F0},{mb.max.x:F0}] Z[{mb.min.z:F0},{mb.max.z:F0}] ({fixedOverlays} overlays).");
            }

            // --- 13) 保存 ---
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Map_v1 battle scene ready: " +
                      "2 rect safe zones (red 42,48 / blue 48.14,-269.57), 3 rect capture points " +
                      "A(22,3) B(62,-111) C(-17,-211), 1 human (red) + 59 FSM AI, " +
                      "commander rig, NavMesh baked, BattleCamera disabled (FreeCamera only).");
        }

        /// <summary>确保指定名字的 layer 存在（Ground 等非 PHASE8 内置层）。幂等。</summary>
        internal static void EnsureLayerNamed(string layerName)
        {
            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layersProp = tagManager.FindProperty("layers");
            EnsureLayerAt(layersProp, layerName);
            tagManager.ApplyModifiedProperties();
        }

        /// <summary>
        /// HGTR 地图 (Blender 导出) 墙体网格面朝外（单面墙）：Physics 默认
        /// queriesHitBackfaces=false，从室内对墙的 raycast/shapecast 全部
        /// MISS（hitscan 打不中墙、部署点探测穿透）。全局开启背面查询命中
        /// 并写入 ProjectSettings 持久化。幂等。
        /// </summary>
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

        /// <summary>
        /// uGUI 点击依赖 EventSystem。项目用 Input System → 需要
        /// InputSystemUIInputModule（StandaloneInputModule 不处理新版输入）。
        /// 幂等：已存在（含模块）则跳过。
        /// </summary>
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

        /// <summary>
        /// HGTR 地图 (Blender 导出) 网格默认无碰撞体：AI/玩家开局直接坠落穿地。
        /// 给全部带网格的地图物体补 MeshCollider 并标记 static（静态几何利于
        /// 物理/光照优化）。幂等；FBX 重导入后重跑本菜单即可恢复。
        /// </summary>
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

        /// <summary>
        /// HGTR 地图 (Blender 导出) 部分墙体网格面朝向翻转：背面剔除下从室内
        /// 看不可见。FBX 内嵌材质全部启用 HDRP 双面渲染（Flip 法线照明），
        /// 内外面都渲染且光照正确。幂等；FBX 重导入后重跑本菜单即可恢复。
        /// </summary>
        internal static void EnableMapMaterialsDoubleSided()
        {
            const string fbxPath = "Assets/Map/HGTR_map.fbx";
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

                /// <summary>
        /// 腔室范围：名字前缀匹配的全部渲染器合并包围盒，返回 XZ 中心+全尺寸
        /// （bounds.y 保留真实高度；size.x/z 为腔室全宽/全深）。
        /// </summary>
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

        /// <summary>平移包围盒中心（保持尺寸）。</summary>
        private static Bounds ShiftBounds(Bounds b, Vector3 offset)
            => new Bounds(b.center + offset, b.size);

        /// <summary>按名字前缀（忽略大小写）计算一组物体的合并 bounds 中心（XZ 为主）。</summary>
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

        /// <summary>清除同名旧对象（幂等重跑）。</summary>
        internal static void ClearOld(params string[] names)
        {
            foreach (var n in names)
            {
                var go = GameObject.Find(n);
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// 在安全区/据点上挂重新部署点模板（DeployPointSet prefab 实例）：
        /// count 个点位绕中心均匀分布 radius 半径。
        /// </summary>
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

        /// <summary>
        /// 在安全区中心周围生成一支 30 人 FSM 队伍（6 小队 × 5 人，网格驻扎）。
        /// spawnY：出生高度（安全区地面可能高于 1.5，如 Map_v2 高架路砖 1.63）。
        /// </summary>
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

        /// <summary>
        /// 一支 FSM 队伍：每小队 5 人（2 突击 + 2 支援 + 1 侦察），小队顺序即
        /// 生成顺序（NetworkMatchManager 按 nextSquad round-robin 分配小队）。
        /// 出生位置 z 符号决定阵营（红 z&lt;0 / 蓝 z&gt;0）。
        /// </summary>
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

        /// <summary>
        /// 静态掩体场（种子化，编辑期摆放）：种类比例沿 TrainingMap 约定
        /// （高/矮/高位/斜面），间距 ≥5m，要地半径+2m 与 GR 半径+2m 净空。
        /// </summary>
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

        [MenuItem("HagenDa/Create Phase3 Scene")]
        public static void CreatePhase3Scene()
        {
            EnsureFolder("Assets", "Scenes");
            EnsureFolder("Assets/Scripts/Network", "Prefabs");

            // Build all prefabs (throwables first so player & AI can reference them).
            GameObject grenadePrefab = BuildGrenadePrefab();
            GameObject smokePrefab = BuildSmokePrefab();
            GameObject rescuePrefab = BuildRescuePrefab();
            GameObject bulletPrefab = BuildBulletPrefab();
            GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab);
            GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab);

            // Fresh empty scene.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();

            SetupScene(playerPrefab, addTargets: true);

            // Register spawnable prefabs (throwables + AI) on the NetworkManager.
            // The bullet is a server-only pooled projectile, so it is NOT registered.
            RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab);

            // NavMesh on the floor for AI pathfinding.
            BuildNavMeshForFloor();

            // Walls (cover for explosion / smoke testing).
            CreateWall(new Vector3(0f, 1.5f, -6f), new Vector3(10f, 3f, 1f));
            CreateWall(new Vector3(-12f, 1.5f, 0f), new Vector3(1f, 3f, 10f));
            CreateWall(new Vector3(12f, 1.5f, 0f), new Vector3(1f, 3f, 10f));

            // Cover blocks for explosion line-of-sight testing.
            CreateWall(new Vector3(0f, 0.75f, 8f), new Vector3(2f, 1.5f, 1f));

            // Place AI entities in the scene.
            CreateAIEntity(aiPrefab, new Vector3(-6f, 1f, -3f));
            CreateAIEntity(aiPrefab, new Vector3(6f, 1f, -3f));

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Scenes/Phase3.scene");
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Created Assets/Scenes/Phase3.scene with AI, throwables, and NavMesh.");
        }

        [MenuItem("HagenDa/Create Phase5 Scene")]
        public static void CreatePhase5Scene()
        {
            EnsureFolder("Assets", "Scenes");
            EnsureFolder("Assets/Scripts/Network", "Prefabs");

            // Build all prefabs (throwables, bullet, then player & AI with the M4 gun).
            GameObject grenadePrefab = BuildGrenadePrefab();
            GameObject smokePrefab = BuildSmokePrefab();
            GameObject rescuePrefab = BuildRescuePrefab();
            GameObject bulletPrefab = BuildBulletPrefab();
            GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab);
            GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab);

            // Fresh empty scene.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();

            SetupScene(playerPrefab, addTargets: true);

            RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab);

            BuildNavMeshForFloor();

            // Cover walls and target dummies for spread/recoil/decay verification.
            CreateWall(new Vector3(0f, 1.5f, -6f), new Vector3(10f, 3f, 1f));
            CreateWall(new Vector3(-12f, 1.5f, 0f), new Vector3(1f, 3f, 10f));
            CreateWall(new Vector3(12f, 1.5f, 0f), new Vector3(1f, 3f, 10f));

            // AI entities to verify shared fire/reload behaviour.
            CreateAIEntity(aiPrefab, new Vector3(-6f, 1f, -3f));
            CreateAIEntity(aiPrefab, new Vector3(6f, 1f, -3f));

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Scenes/Phase5.scene");
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Created Assets/Scenes/Phase5.scene with the M4 firearm, AI, and NavMesh.");
        }

        [MenuItem("HagenDa/Create Phase6 Scene")]
        public static void CreatePhase6Scene()
        {
            EnsureFolder("Assets", "Scenes");
            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            EnsureFolder("Assets/Scripts/Network", "Equipment");

            // Build all equipment assets + throwable prefabs (idempotent).
            List<EquipmentDefinition> equipmentList = BuildEquipmentAssets();

            // Legacy throwables (NetworkCombat G-key) + bullet.
            GameObject grenadePrefab = BuildGrenadePrefab();
            GameObject smokePrefab = BuildSmokePrefab();
            GameObject rescuePrefab = BuildRescuePrefab();
            GameObject bulletPrefab = BuildBulletPrefab();

            // Player + AI with the M4 gun + the equipment list.
            GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
            GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);

            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();
            SetupScene(playerPrefab, addTargets: true);

            RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab);

            // Register every equipment throwable + EMP field so equipment use works.
            foreach (var def in equipmentList)
                if (def != null && def.throwablePrefab != null)
                    RegisterSpawnPrefabs(def.throwablePrefab);
            var empField = AssetDatabase.LoadAssetAtPath<GameObject>(EmpFieldPrefabPath);
            if (empField != null) RegisterSpawnPrefabs(empField);

            BuildNavMeshForFloor();

            CreateWall(new Vector3(0f, 1.5f, -6f), new Vector3(10f, 3f, 1f));
            CreateWall(new Vector3(-12f, 1.5f, 0f), new Vector3(1f, 3f, 10f));
            CreateWall(new Vector3(12f, 1.5f, 0f), new Vector3(1f, 3f, 10f));

            // A special cover wall (爆炸可穿透、子弹不可穿透) for verification.
            var cover = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cover.name = "SpecialCover";
            cover.transform.position = new Vector3(0f, 0.75f, 8f);
            cover.transform.localScale = new Vector3(2f, 1.5f, 0.4f);
            cover.AddComponent<SpecialCover>();

            CreateAIEntity(aiPrefab, new Vector3(-6f, 1f, -3f));
            CreateAIEntity(aiPrefab, new Vector3(6f, 1f, -3f));

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Scenes/Phase6.scene");
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Created Assets/Scenes/Phase6.scene with the equipment system, EMP, and AI.");
        }

        [MenuItem("HagenDa/Create Phase7 Scene")]
        public static void CreatePhase7Scene()
        {
            BuildMatchScene("Assets/Scenes/Phase7.scene");
        }

        [MenuItem("HagenDa/Create Phase8 Scene")]
        public static void CreatePhase8Scene()
        {
            BuildMatchScene("Assets/Scenes/Phase8.scene");
        }

        /// <summary>
        /// Builds the PHASE7/8 100×200 match scene (GR + 2 HQ + teams + NavMesh) with
        /// the current player/AI prefabs (which now include the PHASE8 MapIndicator,
        /// GameHud and DeployScreen components).
        /// </summary>
        private static void BuildMatchScene(string scenePath)
        {
            EnsureFolder("Assets", "Scenes");
            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            EnsureFolder("Assets/Scripts/Network", "Equipment");
            EnsureMapLayers();

            // Build all equipment assets + throwable prefabs (idempotent).
            List<EquipmentDefinition> equipmentList = BuildEquipmentAssets();

            // Legacy throwables + bullet.
            GameObject grenadePrefab = BuildGrenadePrefab();
            GameObject smokePrefab = BuildSmokePrefab();
            GameObject rescuePrefab = BuildRescuePrefab();
            GameObject bulletPrefab = BuildBulletPrefab();

            // Player + AI with the M4 gun + equipment list.
            GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
            GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);

            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();

            // --- 100 x 200 map: long axis = Z, red GR at z=-100, blue GR at z=+100 ---
            const float mapW = 100f;   // X
            const float mapL = 200f;   // Z
            const float wallH = 6f;

            // Floor.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(mapW / 10f, 1f, mapL / 10f);

            // Perimeter walls (rigidbody colliders).
            CreateWall(new Vector3(0f, wallH * 0.5f, mapL * 0.5f), new Vector3(mapW, wallH, 1f));  // north
            CreateWall(new Vector3(0f, wallH * 0.5f, -mapL * 0.5f), new Vector3(mapW, wallH, 1f)); // south
            CreateWall(new Vector3(-mapW * 0.5f, wallH * 0.5f, 0f), new Vector3(1f, wallH, mapL)); // west
            CreateWall(new Vector3(mapW * 0.5f, wallH * 0.5f, 0f), new Vector3(1f, wallH, mapL));  // east

            // --- Garrisons ---
            var redGr = CreateGarrison("RedGarrison", new Vector3(0f, 0f, -mapL * 0.45f), (int)MatchTeam.Red, 20f);
            var blueGr = CreateGarrison("BlueGarrison", new Vector3(0f, 0f, mapL * 0.45f), (int)MatchTeam.Blue, 20f);

            // --- Capture Points (2 HQ in the middle) ---
            var hq1 = CreateCapturePoint("HQ_Alpha", new Vector3(-15f, 0f, 0f), 12f);
            var hq2 = CreateCapturePoint("HQ_Bravo", new Vector3(15f, 0f, 0f), 12f);
            hq1.letter = "A";
            hq2.letter = "B";

            // --- Match Manager ---
            var mmGo = new GameObject("MatchManager");
            var mm = mmGo.AddComponent<NetworkMatchManager>();
            mm.garrisons = new System.Collections.Generic.List<GarrisonZone> { redGr, blueGr };
            mm.capturePoints = new System.Collections.Generic.List<CapturePoint> { hq1, hq2 };

            // --- Spawn points (player always spawns at red GR) ---
            CreateSpawnPoint("RedSpawn1", redGr.GetRandomDeployPoint() + Vector3.up * 1f);
            CreateSpawnPoint("RedSpawn2", redGr.GetRandomDeployPoint() + Vector3.up * 1f);

            // --- NetworkManager ---
            SetupScene(playerPrefab, addTargets: false);
            RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab);
            foreach (var def in equipmentList)
                if (def != null && def.throwablePrefab != null)
                    RegisterSpawnPrefabs(def.throwablePrefab);
            var empField = AssetDatabase.LoadAssetAtPath<GameObject>(EmpFieldPrefabPath);
            if (empField != null) RegisterSpawnPrefabs(empField);

            // --- NavMesh ---
            BuildNavMeshForFloor();

            // --- Entities: red AI in the south (z<0), blue AI in the north (z>0). ---
            CreateAIEntity(aiPrefab, new Vector3(-8f, 1f, -85f));
            CreateAIEntity(aiPrefab, new Vector3(-4f, 1f, -85f));
            CreateAIEntity(aiPrefab, new Vector3(0f, 1f, -85f));
            CreateAIEntity(aiPrefab, new Vector3(4f, 1f, -85f));
            CreateAIEntity(aiPrefab, new Vector3(8f, 1f, -85f));
            CreateAIEntity(aiPrefab, new Vector3(-8f, 1f, -80f));
            CreateAIEntity(aiPrefab, new Vector3(-4f, 1f, -80f));
            CreateAIEntity(aiPrefab, new Vector3(4f, 1f, -80f));
            CreateAIEntity(aiPrefab, new Vector3(8f, 1f, -80f));

            CreateAIEntity(aiPrefab, new Vector3(-8f, 1f, 80f));
            CreateAIEntity(aiPrefab, new Vector3(-4f, 1f, 80f));
            CreateAIEntity(aiPrefab, new Vector3(0f, 1f, 80f));
            CreateAIEntity(aiPrefab, new Vector3(4f, 1f, 80f));
            CreateAIEntity(aiPrefab, new Vector3(8f, 1f, 80f));
            CreateAIEntity(aiPrefab, new Vector3(-8f, 1f, 85f));
            CreateAIEntity(aiPrefab, new Vector3(-4f, 1f, 85f));
            CreateAIEntity(aiPrefab, new Vector3(0f, 1f, 85f));
            CreateAIEntity(aiPrefab, new Vector3(4f, 1f, 85f));
            CreateAIEntity(aiPrefab, new Vector3(8f, 1f, 85f));

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[NetworkSetup] Done. Created {scenePath} with match system, HQ, garrisons, and teams.");
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
                    "Assets/Scripts/Network/Materials/GarrisonZone.mat",
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
                    "Assets/Scripts/Network/Materials/CapturePoint.mat",
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

        // ---------------------------------------------------------------
        // PREFAB
        // ---------------------------------------------------------------
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
            EnsureFolder("Assets/Scripts/Network", "Physics");
            const string noFrictionPath = "Assets/Scripts/Network/Physics/PlayerNoFriction.physicMaterial";
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

            // First-person weapon viewmodel: a pivot (driven by NetworkGun's
            // hip/ads positions in LateUpdate) holding the M4 model, rotated 180° so
            // the barrel points along the camera's +Z (the model's muzzle faces -Z).
            var weaponPivot = new GameObject("WeaponPivot");
            weaponPivot.transform.SetParent(camGo.transform, false);
            weaponPivot.transform.localPosition = gun.hipPosition;
            weaponPivot.transform.localRotation = Quaternion.identity;

            var m4Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(M4PrefabPath);
            if (m4Prefab != null)
            {
                var m4 = (GameObject)PrefabUtility.InstantiatePrefab(m4Prefab);
                m4.name = "M4_8";
                m4.transform.SetParent(weaponPivot.transform, false);
                m4.transform.localPosition = Vector3.zero;
                m4.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                m4.transform.localScale = Vector3.one;
            }
            else
            {
                Debug.LogWarning($"[NetworkSetup] M4 model not found at {M4PrefabPath}");
            }

            gun.gunModel = weaponPivot.transform;

            // Remote visual (capsule body), scaled to match the 1.8m x 0.25m collider.
            // Player body is green to distinguish from red/blue AI.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
            var playerBodyMat = CreatePersistentMaterial(
                "Assets/Scripts/Network/Materials/PlayerBodyGreen.mat",
                new Color(0.2f, 0.8f, 0.3f),
                new Color(0.1f, 0.5f, 0.2f));
            if (playerBodyMat != null)
            {
                var rend = body.GetComponent<Renderer>();
                if (rend != null) rend.sharedMaterial = playerBodyMat;
            }
            controller.visual = body;

            // Save
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ---------------------------------------------------------------
        // PHASE3 PREFABS
        // ---------------------------------------------------------------

        // Creates (or loads) a persistent material asset at the given path, using a
        // shader compatible with the active render pipeline (HDRP/Lit vs Standard).
        // Persistent assets are REQUIRED: in-memory material instances are dropped
        // by SaveAsPrefabAsset (serialized as fileID: 0), leaving prefabs untextured.
        private static Material CreatePersistentMaterial(string path, Color color, Color? emission = null)
        {
            // NB: GetType().Name is "HDRenderPipelineAsset" (namespace not included);
            // checking Name for "HighDefinition" always fails. Check FullName too.
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            bool hdrp = pipeline != null &&
                (pipeline.GetType().FullName.Contains("HighDefinition") ||
                 pipeline.GetType().Name.Contains("HDRenderPipeline"));

            Shader shader = hdrp ? Shader.Find("HDRP/Lit") : Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogError("[NetworkSetup] No usable Lit shader found (tried HDRP/Lit, Standard).");
                return null;
            }

            EnsureFolder("Assets/Scripts/Network", "Materials");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader != shader)
            {
                if (mat != null) AssetDatabase.DeleteAsset(path);
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }

            // HDRP uses _BaseColor; built-in uses _Color. Set both for safety.
            mat.SetColor("_BaseColor", color);
            mat.SetColor("_Color", color);

            if (emission.HasValue)
            {
                if (hdrp)
                {
                    mat.EnableKeyword("_EMISSIVE_COLOR_MAP"); // HDRP emission keyword
                }
                else
                {
                    mat.EnableKeyword("_EMISSION");
                }
                mat.SetColor("_EmissionColor", emission.Value);
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        internal static GameObject BuildGrenadePrefab()
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            root.name = "GrenadeThrowable";
            root.transform.localScale = Vector3.one * 0.2f;

            // Red emissive material (persistent asset — see CreatePersistentMaterial).
            var rend = root.GetComponent<Renderer>();
            var mat = CreatePersistentMaterial(
                "Assets/Scripts/Network/Materials/GrenadeRed.mat",
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

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
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
                "Assets/Scripts/Network/Materials/SmokeThrowableGrey.mat",
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

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
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
                "Assets/Scripts/Network/Materials/RescueGreen.mat",
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

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, RescuePrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ---------------------------------------------------------------
        // PHASE6 THROWABLE / EQUIPMENT PREFABS
        // ---------------------------------------------------------------

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
            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static GameObject BuildHandGrenadePrefab()
        {
            var root = NewThrowableRoot("HandGrenade", PrimitiveType.Sphere, 0.2f,
                new Color(0.9f, 0.1f, 0.1f), new Color(0.8f, 0.1f, 0.1f),
                "Assets/Scripts/Network/Materials/GrenadeRed.mat");
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
                "Assets/Scripts/Network/Materials/SmokeThrowableGrey.mat");
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
                "Assets/Scripts/Network/Materials/EmpBlue.mat");

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
                "Assets/Scripts/Network/Materials/EmpBlue.mat");
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
                "Assets/Scripts/Network/Materials/LauncherGrenade.mat");

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
                "Assets/Scripts/Network/Materials/LauncherSmoke.mat");

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
                "Assets/Scripts/Network/Materials/RpgOrange.mat");
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
                "Assets/Scripts/Network/Materials/SignalCharge.mat");

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
                "Assets/Scripts/Network/Materials/WiredCharge.mat");

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
                "Assets/Scripts/Network/Materials/DelayedBombRed.mat");

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
                "Assets/Scripts/Network/Materials/SupplyPackGreen.mat");

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
                "Assets/Scripts/Network/Materials/SupplyPackGreen.mat");

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
                "Assets/Scripts/Network/Materials/EmpBlue.mat");

            var ic = root.AddComponent<NetworkInterceptor>();
            ic.radius = 3f;
            ic.maxIntercepts = 3;

            return SaveThrowable(root, InterceptorPrefabPath);
        }

        // ---------------------------------------------------------------
        // PHASE8 感应器（蓝色圆锥，固定地面标记敌方）
        // ---------------------------------------------------------------

        private static GameObject BuildSensorProbePrefab()
        {
            var root = new GameObject("SensorProbe");
            root.AddComponent<NetworkIdentity>();

            // 圆锥 mesh 必须保存为资产：内存 mesh 无法序列化进 prefab（会变 fileID:0，
            // 导致渲染与碰撞丢失）。
            EnsureFolder("Assets/Scripts/Network", "Materials");
            const string meshPath = "Assets/Scripts/Network/Materials/SensorConeMesh.asset";
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
                "Assets/Scripts/Network/Materials/SensorBlue.mat",
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

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, SensorProbePrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ---------------------------------------------------------------
        // PHASE8 部署信标（黄色倒圆锥，同小队重部署点）
        // ---------------------------------------------------------------

        private static GameObject BuildDeployBeaconPrefab()
        {
            var root = new GameObject("DeployBeacon");
            root.AddComponent<NetworkIdentity>();

            // 倒圆锥 mesh（底面朝上、尖端朝下）：单独资产，内存 mesh 无法序列化进 prefab。
            EnsureFolder("Assets/Scripts/Network", "Materials");
            const string meshPath = "Assets/Scripts/Network/Materials/DeployBeaconConeMesh.asset";
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
                "Assets/Scripts/Network/Materials/BeaconYellow.mat",
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

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, DeployBeaconPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        /// <summary>程序化生成圆锥 mesh（顶点朝上，底部圆盘）。</summary>
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

        // ---------------------------------------------------------------
        // PHASE6 EQUIPMENT ASSETS
        // ---------------------------------------------------------------

        private static EquipmentDefinition GetOrCreateEquipmentDef(string fileName)
        {
            EnsureFolder("Assets/Scripts/Network", "Equipment");
            string path = EquipmentFolder + "/" + fileName + ".asset";
            var def = AssetDatabase.LoadAssetAtPath<EquipmentDefinition>(path);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<EquipmentDefinition>();
                def.name = fileName;
                AssetDatabase.CreateAsset(def, path);
            }
            return def;
        }

        internal static List<EquipmentDefinition> BuildEquipmentAssets()
        {
            // Build throwable prefabs first (idempotent), so equipment defs can
            // reference them.
            var handGrenade = BuildHandGrenadePrefab();
            var smokeGrenade = BuildSmokeGrenadePrefab();
            var empField = BuildEmpFieldPrefab();
            var empGrenade = BuildEmpGrenadePrefab(empField);
            var launcherGrenade = BuildLauncherGrenadePrefab();
            var launcherSmoke = BuildLauncherSmokePrefab();
            var rpg = BuildRpgPrefab();
            var signal = BuildSignalChargePrefab();
            var wired = BuildWiredChargePrefab();
            var delayedBomb = BuildDelayedBombPrefab();
            var supplyPack = BuildSupplyPackPrefab();

            var list = new List<EquipmentDefinition>();

            // 0. 手雷
            var g = GetOrCreateEquipmentDef("HandGrenade");
            g.type = EquipmentType.Grenade;
            g.displayName = "手雷";
            g.useStyle = EquipmentUseStyle.Throw;
            g.maxCarry = 1; g.supplyCost = 100; g.throwSpeed = 15f;
            g.throwablePrefab = handGrenade;
            list.Add(g);

            // 1. 烟雾手雷
            var sg = GetOrCreateEquipmentDef("SmokeGrenade");
            sg.type = EquipmentType.SmokeGrenade;
            sg.displayName = "烟雾手雷";
            sg.useStyle = EquipmentUseStyle.Throw;
            sg.maxCarry = 2; sg.supplyCost = 80; sg.throwSpeed = 15f;
            sg.throwablePrefab = smokeGrenade;
            list.Add(sg);

            // 2. 电磁手雷
            var eg = GetOrCreateEquipmentDef("EmpGrenade");
            eg.type = EquipmentType.EmpGrenade;
            eg.displayName = "电磁手雷";
            eg.useStyle = EquipmentUseStyle.Throw;
            eg.maxCarry = 1; eg.supplyCost = 100; eg.throwSpeed = 15f;
            eg.throwablePrefab = empGrenade;
            eg.empRadius = 6f; eg.empLifetime = 10f; eg.empInterfereDuration = 2f;
            list.Add(eg);

            // 3. 榴弹炮
            var gl = GetOrCreateEquipmentDef("GrenadeLauncher");
            gl.type = EquipmentType.GrenadeLauncher;
            gl.displayName = "榴弹炮";
            gl.useStyle = EquipmentUseStyle.BoltLauncher;
            gl.maxCarry = 5; gl.supplyCost = 100; gl.boltTime = 1.5f; gl.throwSpeed = 250f;
            gl.throwablePrefab = launcherGrenade;
            list.Add(gl);

            // 4. 烟雾发射器
            var sl = GetOrCreateEquipmentDef("SmokeLauncher");
            sl.type = EquipmentType.SmokeLauncher;
            sl.displayName = "烟雾发射器";
            sl.useStyle = EquipmentUseStyle.BoltLauncher;
            sl.maxCarry = 5; sl.supplyCost = 100; sl.boltTime = 1.5f; sl.throwSpeed = 250f;
            sl.throwablePrefab = launcherSmoke;
            list.Add(sl);

            // 5. RPG
            var r = GetOrCreateEquipmentDef("Rpg");
            r.type = EquipmentType.Rpg;
            r.displayName = "RPG";
            r.useStyle = EquipmentUseStyle.BoltLauncher;
            r.maxCarry = 5; r.supplyCost = 120; r.boltTime = 2f; r.throwSpeed = 750f;
            r.throwablePrefab = rpg;
            list.Add(r);

            // 6. 信号炸药（EMP 可禁）
            var sc = GetOrCreateEquipmentDef("SignalCharge");
            sc.type = EquipmentType.SignalCharge;
            sc.displayName = "信号炸药";
            sc.useStyle = EquipmentUseStyle.RemoteCharge;
            sc.maxCarry = 3; sc.supplyCost = 120; sc.throwSpeed = 3f;
            sc.empVulnerable = true;
            sc.throwablePrefab = signal;
            list.Add(sc);

            // 7. 线控炸药（EMP 不可禁）
            var wc = GetOrCreateEquipmentDef("WiredCharge");
            wc.type = EquipmentType.WiredCharge;
            wc.displayName = "线控炸药";
            wc.useStyle = EquipmentUseStyle.RemoteCharge;
            wc.maxCarry = 3; wc.supplyCost = 120; wc.throwSpeed = 2f;
            wc.empVulnerable = false;
            wc.throwablePrefab = wired;
            list.Add(wc);

            // 8. 延时炸弹（EMP 可禁）
            var db = GetOrCreateEquipmentDef("DelayedBomb");
            db.type = EquipmentType.DelayedBomb;
            db.displayName = "延时炸弹";
            db.useStyle = EquipmentUseStyle.Throw;
            db.maxCarry = 2; db.supplyCost = 120; db.throwSpeed = 3f;
            db.empVulnerable = true;
            db.throwablePrefab = delayedBomb;
            list.Add(db);

            // 9. 小型补给包（每 10s 自动 +1）
            var sp = GetOrCreateEquipmentDef("SmallSupplyPack");
            sp.type = EquipmentType.SmallSupplyPack;
            sp.displayName = "小型补给包";
            sp.useStyle = EquipmentUseStyle.Throw;
            sp.maxCarry = 3; sp.supplyCost = 100; sp.throwSpeed = 3f;
            sp.ammoRegenInterval = 10f;
            sp.throwablePrefab = supplyPack;
            list.Add(sp);

            // 10. 大型补给箱（Deploy）
            var lc = GetOrCreateEquipmentDef("LargeSupplyCrate");
            lc.type = EquipmentType.LargeSupplyCrate;
            lc.displayName = "大型补给箱";
            lc.useStyle = EquipmentUseStyle.Deploy;
            lc.maxCarry = 1; lc.supplyCost = 100; lc.ammoRegenInterval = 10f;
            lc.throwablePrefab = BuildLargeSupplyCratePrefab();
            list.Add(lc);

            // 11. 拦截系统（Deploy，部署上限 3）
            var it = GetOrCreateEquipmentDef("Interceptor");
            it.type = EquipmentType.Interceptor;
            it.displayName = "拦截系统";
            it.useStyle = EquipmentUseStyle.Deploy;
            it.maxCarry = 1; it.supplyCost = 100;
            it.deployCap = 3;
            it.throwablePrefab = BuildInterceptorPrefab();
            list.Add(it);

            // 11b. 感应器（特有，Deploy，瞬发型，部署上限 1，30s 回复，EMP 可摧毁）
            var sn = GetOrCreateEquipmentDef("Sensor");
            sn.type = EquipmentType.Sensor;
            sn.displayName = "感应器";
            sn.useStyle = EquipmentUseStyle.Deploy;
            sn.maxCarry = 1; sn.supplyCost = 0;
            sn.ammoRegenInterval = 30f;
            sn.empVulnerable = true;
            sn.deployCap = 1;
            sn.throwablePrefab = BuildSensorProbePrefab();
            list.Add(sn);

            // 11c. 部署信标（可选，Deploy，瞬发型，部署上限 1，同小队重部署点，
            //      5 次用尽自毁，EMP 可摧毁，重部署时保留）
            var bn = GetOrCreateEquipmentDef("DeployBeacon");
            bn.type = EquipmentType.DeployBeacon;
            bn.displayName = "部署信标";
            bn.useStyle = EquipmentUseStyle.Deploy;
            bn.maxCarry = 1; bn.supplyCost = 150;
            bn.empVulnerable = true;
            bn.persistOnRedeploy = true;
            bn.throwablePrefab = BuildDeployBeaconPrefab();
            list.Add(bn);

            // 12. 快速机动装置（EMP 可禁）
            var qd = GetOrCreateEquipmentDef("QuickDash");
            qd.type = EquipmentType.QuickDash;
            qd.displayName = "快速机动装置";
            qd.useStyle = EquipmentUseStyle.SelfInstant;
            qd.maxCarry = 1; qd.supplyCost = 0; qd.dashCooldown = 15f;
            qd.empVulnerable = true;
            list.Add(qd);

            // 13. 护甲板
            var ap = GetOrCreateEquipmentDef("ArmorPlate");
            ap.type = EquipmentType.ArmorPlate;
            ap.displayName = "护甲板";
            ap.useStyle = EquipmentUseStyle.SelfChannel;
            ap.maxCarry = 2; ap.supplyCost = 80; ap.channelTime = 1.5f; ap.armorGrant = 25f;
            list.Add(ap);

            // 14. 治疗针（特有）
            var hs = GetOrCreateEquipmentDef("HealingSyringe");
            hs.type = EquipmentType.HealingSyringe;
            hs.displayName = "治疗针";
            hs.useStyle = EquipmentUseStyle.SelfChannel;
            hs.maxCarry = 2; hs.supplyCost = 80; hs.channelTime = 1f;
            list.Add(hs);

            // 15. 除颤仪（特有，每 3s +1）
            var df = GetOrCreateEquipmentDef("Defibrillator");
            df.type = EquipmentType.Defibrillator;
            df.displayName = "除颤仪";
            df.useStyle = EquipmentUseStyle.TargetChannel;
            df.maxCarry = 3; df.supplyCost = 0; df.channelTime = 2f; df.ammoRegenInterval = 3f;
            list.Add(df);

            // 16. 防爆盾（batch B 填充 throwablePrefab / 组件）
            var bs = GetOrCreateEquipmentDef("BlastShield");
            bs.type = EquipmentType.BlastShield;
            bs.displayName = "防爆盾";
            bs.useStyle = EquipmentUseStyle.ShieldToggle;
            bs.maxCarry = 1; bs.supplyCost = 0; bs.shieldExplosionReduction = 0.6f;
            list.Add(bs);

            // 17. 干扰器（可选，瞬发清除自身标记 + 30s 免疫标记，免疫结束后
            //     30s 冷却恢复 1 次，EMP 可禁）
            var jm = GetOrCreateEquipmentDef("Jammer");
            jm.type = EquipmentType.Jammer;
            jm.displayName = "干扰器";
            jm.useStyle = EquipmentUseStyle.SelfInstant;
            jm.maxCarry = 1; jm.supplyCost = 0;
            jm.empVulnerable = true;
            list.Add(jm);

            // PHASE8: 按类型统一赋值 category + instantUse + deployCap。
            foreach (var d in list)
            {
                if (d == null) continue;

                switch (d.type)
                {
                    case EquipmentType.HealingSyringe:
                    case EquipmentType.Defibrillator:
                    case EquipmentType.Sensor:
                        d.category = EquipmentCategory.Special;
                        break;
                    case EquipmentType.Grenade:
                    case EquipmentType.SmokeGrenade:
                    case EquipmentType.EmpGrenade:
                        d.category = EquipmentCategory.Throwable;
                        break;
                    default:
                        d.category = EquipmentCategory.Optional;
                        break;
                }

                switch (d.type)
                {
                    case EquipmentType.LargeSupplyCrate:
                    case EquipmentType.SmallSupplyPack:
                    case EquipmentType.QuickDash:
                    case EquipmentType.HealingSyringe:
                    case EquipmentType.ArmorPlate:
                    case EquipmentType.Grenade:   // 手雷：瞬发型（z 键直接投掷）
                    case EquipmentType.SmokeGrenade:   // 烟雾手雷：瞬发型
                    case EquipmentType.EmpGrenade:     // 电磁手雷：瞬发型
                    case EquipmentType.Sensor:         // 感应器：瞬发型特有（g 键直接部署）
                    case EquipmentType.DeployBeacon:   // 部署信标：瞬发型（放置即部署）
                    case EquipmentType.Jammer:         // 干扰器：瞬发型（slot 键直接使用）
                        d.instantUse = true;
                        break;
                    default:
                        d.instantUse = false;
                        break;
                }

                switch (d.type)
                {
                    case EquipmentType.LargeSupplyCrate:
                        d.deployCap = 1;
                        break;
                    case EquipmentType.SmallSupplyPack:
                    case EquipmentType.Interceptor:
                    case EquipmentType.SignalCharge:
                    case EquipmentType.WiredCharge:
                        d.deployCap = 3;
                        break;
                    case EquipmentType.Sensor:
                        d.deployCap = 1;
                        break;
                    case EquipmentType.DeployBeacon:   // 部署信标：单人同时仅 1 个
                        d.deployCap = 1;
                        break;
                    default:
                        d.deployCap = 0;
                        break;
                }
            }

            foreach (var d in list)
                if (d != null) EditorUtility.SetDirty(d);

            return list;
        }

        internal static GameObject BuildBulletPrefab()
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            root.name = "Bullet";
            root.transform.localScale = Vector3.one * 0.05f;

            var rend = root.GetComponent<Renderer>();
            var mat = CreatePersistentMaterial(
                "Assets/Scripts/Network/Materials/BulletWhite.mat",
                new Color(1f, 1f, 1f),
                new Color(0.6f, 0.6f, 0.6f));
            if (mat != null) rend.sharedMaterial = mat;

            // Ray-based projectile: no physics collider (hit detection is a manual
            // segment raycast on the server, so a collider would only cause self-hits).
            Object.DestroyImmediate(root.GetComponent<SphereCollider>());

            root.AddComponent<NetworkBullet>();

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, BulletPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        /// <summary>
        /// Creates (or loads) the M4 weapon definition asset. Defaults are baked
        /// into <see cref="WeaponDefinition"/> field initializers (900 RPM, 30/150,
        /// 800 m/s + 300 m/s^2 decay, spread/recoil, etc.).
        /// </summary>
        internal static WeaponDefinition BuildM4Definition()
        {
            EnsureFolder("Assets/Scripts/Network", "Weapons");

            var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(M4DefinitionPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<WeaponDefinition>();
                def.name = "M4Definition";
                AssetDatabase.CreateAsset(def, M4DefinitionPath);
            }

            EditorUtility.SetDirty(def);
            return def;
        }

        /// <summary>
        /// Adds a NetworkTransformReliable without tripping Mirror's editor Reset()
        /// NRE. In this Mirror version, ResetState() reads `target` before Configure()
        /// assigns it, so AddComponent throws a harmless NullReferenceException in the
        /// editor. The component is still added; we re-fetch it and assign the target.
        /// </summary>
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

            EnsureFolder("Assets/Scripts/Network", "Physics");
            const string noFrictionPath = "Assets/Scripts/Network/Physics/PlayerNoFriction.physicMaterial";
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
                "Assets/Scripts/Network/Materials/AIBodyRed.mat",
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

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, AIPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ---------------------------------------------------------------
        // SCENE
        // ---------------------------------------------------------------
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

        /// <summary>
        /// HGTR (Blender map): bake from ALL scene colliders with the surface on the
        /// map root. Ceiling objects are temporarily deactivated so the bake cannot
        /// produce a walkable roof layer above the playable space.
        /// </summary>
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

        internal static void EnsureFolder(string parent, string folder)
        {
            string full = parent + "/" + folder;
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(parent, folder);
        }

        /// <summary>
        /// PHASE8: make sure the map indicator / highlight layers exist in
        /// TagManager (idempotent). Called by every scene builder so freshly built
        /// scenes reference valid layers.
        /// </summary>
        internal static void EnsureMapLayers()
        {
            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);

            var layersProp = tagManager.FindProperty("layers");
            EnsureLayerAt(layersProp, MapLayers.IndicatorName);
            EnsureLayerAt(layersProp, MapLayers.HighlightName);
            EnsureLayerAt(layersProp, MapLayers.ZoneName);
            EnsureLayerAt(layersProp, MapLayers.ZoneOutlineName);
            tagManager.ApplyModifiedProperties();
        }

        private static void EnsureLayerAt(SerializedProperty layersProp, string layerName)
        {
            // Layers 0-7 are reserved (built-in). Insert into the first empty slot.
            for (int i = 8; i < layersProp.arraySize; i++)
            {
                var slot = layersProp.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = layerName;
                    return;
                }
                if (slot.stringValue == layerName)
                    return;   // already present
            }
        }

        // Removes any MonoBehaviour with an unresolved script reference (missing
        // script), recursively.
        private static void RemoveMissingScripts(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            }
        }

        private static void MarkSceneDirty()
        {
            var scene = SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }

        internal static void SaveActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        }

        internal static void CreateWall(Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Wall";
            go.transform.position = pos;
            go.transform.localScale = scale;
        }

        // Adds a directional light so the freshly created (empty) scene is visible.
        // Intensity is chosen for the active render pipeline; HDRP needs its
        // additional light data component attached for correct rendering.
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
