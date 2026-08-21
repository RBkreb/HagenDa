using System.Collections.Generic;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem.UI;
using Unity.AI.Navigation;
using CrashKonijn.Agent.Runtime;
using CrashKonijn.Goap.Runtime;
using HagenDa.Networking.AI;

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

        [MenuItem("HagenDa/Create Phase9 Scene")]
        public static void CreatePhase9Scene()
        {
            BuildMatchScene("Assets/Scenes/Phase9.scene");
        }

        /// <summary>
        /// Builds the PHASE7/8/9 100×200 match scene (GR + 2 HQ + teams + NavMesh) with
        /// the current player/AI prefabs. PHASE9 adds the GOAP components (AgentBehaviour,
        /// GoapActionProvider, AIDataProvider, GoalSelector, etc.) and the global Goap
        /// controller to the AI entities.
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

            // --- GOAP (PHASE9): global controller + code-configured agent type ---
            EnsureGoapBehaviour();

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

            // --- Cover objects (boxes, low walls, ramps) ---
            CreateCoverObjects();

            // --- Entities: 30 AI per side (10 Assault + 10 Support + 10 Recon).
            // Red AI in the south (z<0), blue AI in the north (z>0).
            // Player occupies one Assault slot on Red → Red has 9 Assault AI.
            // 6 squads per team, 5 members each. ---
            // Loadout indices: 0=Assault, 1=Support, 2=Recon

            // Red team (south, z<0): 9 Assault + 10 Support + 10 Recon = 29 AI
            // (player takes the 10th Assault slot)
            SpawnTeamAI(aiPrefab, isRed: true);

            // Blue team (north, z>0): 10 Assault + 10 Support + 10 Recon = 30 AI
            SpawnTeamAI(aiPrefab, isRed: false);

            // Set squadsPerTeam = 6 (6 squads × 5 members = 30 per team)
            mm.squadsPerTeam = 6;

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[NetworkSetup] Done. Created {scenePath} with match system, HQ, garrisons, 30 AI per side (10A/10S/10R), 6 squads.");
        }

        /// <summary>
        /// Spawn 30 AI (or 29 for red — player takes 1 assault slot) in a 5×6 grid
        /// with explicit loadout assignment: 10 Assault (0), 10 Support (1), 10 Recon (2).
        /// Red is south (z<0), blue is north (z>0).
        /// </summary>
        private static void SpawnTeamAI(GameObject aiPrefab, bool isRed)
        {
            // Loadout pattern: A×10, S×10, R×10
            // Red has only 9 Assault (player takes the 10th).
            int assaultCount = isRed ? 9 : 10;
            int supportCount = 10;
            int reconCount = 10;
            int total = assaultCount + supportCount + reconCount;

            float zBase = isRed ? -85f : 80f;
            float zDir = isRed ? 1f : 1f;   // rows spread toward 0
            float rowSpacing = 5f;

            // 5 columns (x: -8,-4,0,4,8), 6 rows
            float[] xs = { -8f, -4f, 0f, 4f, 8f };

            for (int i = 0; i < total; i++)
            {
                int col = i % 5;
                int row = i / 5;

                float x = xs[col];
                float z = zBase + row * zDir * rowSpacing;

                int loadout;
                if (i < assaultCount)
                    loadout = 0;   // Assault
                else if (i < assaultCount + supportCount)
                    loadout = 1;   // Support
                else
                    loadout = 2;   // Recon

                CreateAIEntity(aiPrefab, new Vector3(x, 1f, z), loadout);
            }
        }

        /// <summary>
        /// Create cover objects in the central combat area: 8 boxes around the two HQ
        /// points, 2 low walls across the corridor, and 4 angled ramps (2 per side).
        /// All are marked NavigationStatic so they participate in NavMesh carving.
        /// </summary>
        private static void CreateCoverObjects()
        {
            // --- Central boxes (8) around the two HQ points ---
            Vector3[] boxPositions = {
                new Vector3(-15f, 0.5f, -5f),
                new Vector3(-15f, 0.5f, 5f),
                new Vector3(-12f, 0.75f, 0f),
                new Vector3(-18f, 0.75f, 0f),
                new Vector3(15f, 0.5f, -5f),
                new Vector3(15f, 0.5f, 5f),
                new Vector3(12f, 0.75f, 0f),
                new Vector3(18f, 0.75f, 0f),
            };

            Vector3[] boxScales = {
                new Vector3(2f, 1f, 2f),
                new Vector3(2f, 1f, 2f),
                new Vector3(1.5f, 1.5f, 1.5f),
                new Vector3(1.5f, 1.5f, 1.5f),
                new Vector3(2f, 1f, 2f),
                new Vector3(2f, 1f, 2f),
                new Vector3(1.5f, 1.5f, 1.5f),
                new Vector3(1.5f, 1.5f, 1.5f),
            };

            for (int i = 0; i < boxPositions.Length; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"CoverBox_{i}";
                go.transform.position = boxPositions[i];
                go.transform.localScale = boxScales[i];
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.NavigationStatic);
            }

            // --- Low walls (2) across the central corridor ---
            var wall1 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall1.name = "LowWall_Center1";
            wall1.transform.position = new Vector3(0f, 0.4f, -8f);
            wall1.transform.localScale = new Vector3(6f, 0.8f, 0.5f);
            GameObjectUtility.SetStaticEditorFlags(wall1, StaticEditorFlags.NavigationStatic);

            var wall2 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall2.name = "LowWall_Center2";
            wall2.transform.position = new Vector3(0f, 0.4f, 8f);
            wall2.transform.localScale = new Vector3(6f, 0.8f, 0.5f);
            GameObjectUtility.SetStaticEditorFlags(wall2, StaticEditorFlags.NavigationStatic);

            // --- Ramps (4) at 30° angle for cover on each side ---
            var ramp1 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ramp1.name = "Ramp_RedLeft";
            ramp1.transform.position = new Vector3(-10f, 0.75f, -15f);
            ramp1.transform.rotation = Quaternion.Euler(-30f, 0f, 0f);
            ramp1.transform.localScale = new Vector3(3f, 0.5f, 6f);
            GameObjectUtility.SetStaticEditorFlags(ramp1, StaticEditorFlags.NavigationStatic);

            var ramp2 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ramp2.name = "Ramp_RedRight";
            ramp2.transform.position = new Vector3(10f, 0.75f, -15f);
            ramp2.transform.rotation = Quaternion.Euler(-30f, 0f, 0f);
            ramp2.transform.localScale = new Vector3(3f, 0.5f, 6f);
            GameObjectUtility.SetStaticEditorFlags(ramp2, StaticEditorFlags.NavigationStatic);

            var ramp3 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ramp3.name = "Ramp_BlueLeft";
            ramp3.transform.position = new Vector3(-10f, 0.75f, 15f);
            ramp3.transform.rotation = Quaternion.Euler(30f, 0f, 0f);
            ramp3.transform.localScale = new Vector3(3f, 0.5f, 6f);
            GameObjectUtility.SetStaticEditorFlags(ramp3, StaticEditorFlags.NavigationStatic);

            var ramp4 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ramp4.name = "Ramp_BlueRight";
            ramp4.transform.position = new Vector3(10f, 0.75f, 15f);
            ramp4.transform.rotation = Quaternion.Euler(30f, 0f, 0f);
            ramp4.transform.localScale = new Vector3(3f, 0.5f, 6f);
            GameObjectUtility.SetStaticEditorFlags(ramp4, StaticEditorFlags.NavigationStatic);
        }

        private static GarrisonZone CreateGarrison(string name, Vector3 pos, int teamId, float radius)
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

        private static CapturePoint CreateCapturePoint(string name, Vector3 pos, float radius)
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
        private static GameObject BuildPlayerPrefab()
        {
            return BuildPlayerPrefab(null, null, null, null);
        }

        private static GameObject BuildPlayerPrefab(GameObject grenadePrefab, GameObject smokePrefab, GameObject rescuePrefab, GameObject bulletPrefab, List<EquipmentDefinition> equipmentList = null)
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

        private static GameObject BuildGrenadePrefab()
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

        private static GameObject BuildSmokePrefab()
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

        private static GameObject BuildRescuePrefab()
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

        private static List<EquipmentDefinition> BuildEquipmentAssets()
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

            // 12. 快速机动装置（EMP 可禁）
            var qd = GetOrCreateEquipmentDef("QuickDash");
            qd.type = EquipmentType.QuickDash;
            qd.displayName = "快速机动装置";
            qd.useStyle = EquipmentUseStyle.SelfInstant;
            qd.maxCarry = 1; qd.supplyCost = 0; qd.dashCooldown = 15f;
            qd.empVulnerable = true;
            list.Add(qd);

            // 12b. 干扰器（瞬发型，清除标记 + 30s 免疫标记；EMP 可禁/可提前终止）
            var jm = GetOrCreateEquipmentDef("Jammer");
            jm.type = EquipmentType.Jammer;
            jm.displayName = "干扰器";
            jm.useStyle = EquipmentUseStyle.SelfInstant;
            jm.maxCarry = 1; jm.supplyCost = 0;
            jm.empVulnerable = true;
            list.Add(jm);

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
                    case EquipmentType.Jammer:         // 干扰器：瞬发型
                    case EquipmentType.Grenade:   // 手雷：瞬发型（z 键直接投掷）
                    case EquipmentType.SmokeGrenade:   // 烟雾手雷：瞬发型
                    case EquipmentType.EmpGrenade:     // 电磁手雷：瞬发型
                    case EquipmentType.Sensor:         // 感应器：瞬发型特有（g 键直接部署）
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
                    default:
                        d.deployCap = 0;
                        break;
                }
            }

            foreach (var d in list)
                if (d != null) EditorUtility.SetDirty(d);

            return list;
        }

        private static GameObject BuildBulletPrefab()
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
        private static WeaponDefinition BuildM4Definition()
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
        private static NetworkTransformReliable AddNetworkTransform(GameObject go, SyncDirection direction, bool syncRotation)
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

        private static GameObject BuildAIEntityPrefab(GameObject grenadePrefab, GameObject smokePrefab, GameObject rescuePrefab, GameObject bulletPrefab, List<EquipmentDefinition> equipmentList = null)
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

            // NavMeshAgent for pathfinding (server-driven).
            var agent = root.AddComponent<NavMeshAgent>();
            agent.height = 1.8f;
            agent.radius = 0.25f;
            agent.baseOffset = 0f;

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

            // AI controller.
            var ai = root.AddComponent<NetworkAIController>();
            ai.combat = combat;
            ai.gun = gun;

            // Equipment (PHASE6): same shared runtime as the player.
            if (equipmentList == null)
                equipmentList = BuildEquipmentAssets();
            var equipment = root.AddComponent<NetworkEquipment>();
            equipment.equipmentList = equipmentList;
            ai.equipment = equipment;

            // PHASE9 GOAP: data layer, goal selector, squad order receiver and the
            // GOAP agent wiring (AgentBehaviour + GoapActionProvider + movement).
            // NOTE: GoapActionProvider must be added before GoalSelector /
            // GoapAgentInitializer (they GetComponent<GoapActionProvider>() in Awake);
            // no [RequireComponent] is used to avoid Unity auto-adding a duplicate.
            root.AddComponent<AIDataProvider>();
            root.AddComponent<SquadOrderReceiver>();

            var agentBehaviour = root.AddComponent<AgentBehaviour>();
            var goapProvider = root.AddComponent<GoapActionProvider>();
            agentBehaviour.ActionProviderBase = goapProvider;

            root.AddComponent<GoalSelector>();
            root.AddComponent<AgentNavMeshMove>();
            root.AddComponent<GoapAgentInitializer>();

            // AI 免后座力（射击规则系统）；散布难度乘数默认 1.0。
            gun.applyRecoil = false;

            // 启用 GOAP 驱动（旧 FSM 停用）。
            ai.useGoap = true;

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

            // Wire the visual so SetDead can rotate it into the prone posture.
            ai.visual = body;

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

        private static void CreateAIEntity(GameObject aiPrefab, Vector3 pos, int loadoutIndex = -1)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(aiPrefab);
            go.name = "AIEntity";
            go.transform.position = pos;

            // Set explicit loadout (0=Assault, 1=Support, 2=Recon) if specified.
            if (loadoutIndex >= 0)
            {
                var initializer = go.GetComponent<GoapAgentInitializer>();
                if (initializer != null)
                    initializer.loadoutIndex = loadoutIndex;
            }
        }

        /// <summary>
        /// PHASE9: create the global GOAP controller (if missing) with a reactive
        /// controller and the code-configured Combatant agent type. The AIEntity
        /// prefab's GoapAgentInitializer resolves "Combatant" from this at runtime.
        /// </summary>
        private static void EnsureGoapBehaviour()
        {
            var existing = Object.FindObjectOfType<GoapBehaviour>();
            if (existing != null) return;

            var goapGo = new GameObject("Goap");
            var goap = goapGo.AddComponent<GoapBehaviour>();
            goapGo.AddComponent<ReactiveControllerBehaviour>();
            goapGo.AddComponent<AgentBatchUpdater>();

            var factory = goapGo.AddComponent<CombatantAgentTypeFactory>();
            goap.agentTypeConfigFactories.Add(factory);
        }

        private static void RegisterSpawnPrefabs(params GameObject[] prefabs)
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

        private static void BuildNavMeshForFloor()
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

        private static void EnsureFolder(string parent, string folder)
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
        private static void EnsureMapLayers()
        {
            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);

            var layersProp = tagManager.FindProperty("layers");
            EnsureLayerAt(layersProp, MapLayers.IndicatorName);
            EnsureLayerAt(layersProp, MapLayers.HighlightName);
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

        private static void SaveActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        }

        private static void CreateWall(Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Wall";
            go.transform.position = pos;
            go.transform.localScale = scale;
        }

        // Adds a directional light so the freshly created (empty) scene is visible.
        // Intensity is chosen for the active render pipeline; HDRP needs its
        // additional light data component attached for correct rendering.
        private static void EnsureLighting()
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
