using System.Collections.Generic;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem.UI;
using Unity.AI.Navigation;
using cowsins;

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
        private const string FpsPrefabPath = "Assets/Scripts/Network/Prefabs/FpsEngineNetworkPlayer.prefab";
        private const string FpsSourcePrefab = "Assets/Cowsins/Prefabs/PlayerControllers/CowsinsFPSController.prefab";
        private const string GrenadePrefabPath = "Assets/Scripts/Network/Prefabs/GrenadeThrowable.prefab";
        private const string SmokePrefabPath = "Assets/Scripts/Network/Prefabs/SmokeThrowable.prefab";
        private const string AIPrefabPath = "Assets/Scripts/Network/Prefabs/AIEntity.prefab";

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

        [MenuItem("HagenDa/Setup FPS Engine Demo")]
        public static void SetupFpsDemo()
        {
            EnsureFolder("Assets/Scripts/Network", "Prefabs");

            GameObject playerPrefab = BuildFpsEnginePlayerPrefab();

            SetupScene(playerPrefab, addTargets: true);

            SaveActiveScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Built FPS Engine player prefab and configured the active scene.");
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

            // Build all prefabs (grenade & smoke first so player & AI can reference them).
            GameObject grenadePrefab = BuildGrenadePrefab();
            GameObject smokePrefab = BuildSmokePrefab();
            GameObject playerPrefab = BuildPlayerPrefab(grenadePrefab, smokePrefab);
            GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab);

            // Fresh empty scene.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            EnsureLighting();

            SetupScene(playerPrefab, addTargets: true);

            // Register spawnable prefabs (grenade, smoke, AI) on the NetworkManager.
            RegisterSpawnPrefabs(grenadePrefab, smokePrefab, aiPrefab);

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

        // ---------------------------------------------------------------
        // PREFAB
        // ---------------------------------------------------------------
        private static GameObject BuildPlayerPrefab()
        {
            return BuildPlayerPrefab(null, null);
        }

        private static GameObject BuildPlayerPrefab(GameObject grenadePrefab, GameObject smokePrefab = null)
        {
            var root = new GameObject("NetworkPlayer");

            // Networking
            root.AddComponent<NetworkIdentity>();

            var nt = root.AddComponent<NetworkTransformReliable>();
            nt.syncDirection = SyncDirection.ServerToClient;
            nt.syncPosition = true;
            nt.syncRotation = true;
            nt.interpolatePosition = true;
            nt.interpolateRotation = true;

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
            root.AddComponent<DebugHud>();

            // Shared combat (hitscan + throwing). Reuse prefabs if already built.
            if (grenadePrefab == null)
                grenadePrefab = BuildGrenadePrefab();
            if (smokePrefab == null)
                smokePrefab = BuildSmokePrefab();
            var combat = root.AddComponent<NetworkCombat>();
            combat.grenadeThrowablePrefab = grenadePrefab.GetComponent<NetworkThrowable>();
            combat.smokeThrowablePrefab = smokePrefab.GetComponent<NetworkThrowable>();

            controller.standCollider = standCapsule;
            controller.crouchCollider = crouchCapsule;
            controller.combat = combat;

            // Camera child (local player only). Bound to capsule top (1.8m) - 0.15m = 1.65m.
            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(root.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            var cam = camGo.AddComponent<Camera>();
            camGo.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();
            controller.playerCamera = cam;

            // Remote visual (capsule body), scaled to match the 1.8m x 0.25m collider.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
            controller.visual = body;

            // Save
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ---------------------------------------------------------------
        // FPS ENGINE NETWORKED PREFAB
        // ---------------------------------------------------------------
        private static GameObject BuildFpsEnginePlayerPrefab()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(FpsSourcePrefab);
            if (source == null)
            {
                Debug.LogError($"[NetworkSetup] FPS Engine controller prefab not found at {FpsSourcePrefab}");
                return null;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(FpsSourcePrefab);

            // Remove missing scripts (e.g. HDRP HDAdditionalCameraData whose GUID
            // doesn't resolve in Tuanjie) so SaveAsPrefabAsset doesn't refuse to save.
            RemoveMissingScripts(contents);

            // Networking
            contents.AddComponent<NetworkIdentity>();

            var nt = contents.AddComponent<NetworkTransformReliable>();
            nt.syncDirection = SyncDirection.ClientToServer; // client-authoritative movement
            nt.syncPosition = true;
            nt.syncRotation = false; // FPS Engine root never rotates (look lives on camera/orientation)
            nt.interpolatePosition = true;
            nt.interpolateRotation = false;

            contents.AddComponent<NetworkPlayerHealth>();

            var fps = contents.AddComponent<NetworkFpsPlayer>();

            // Remote proxy body (visible capsule for other players).
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "RemoteBody";
            body.transform.SetParent(contents.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
            body.SetActive(false);
            fps.remoteBody = body;

            // Wire references.
            fps.playerMovement = contents.GetComponentInChildren<PlayerMovement>(true);
            fps.weaponController = contents.GetComponentInChildren<WeaponController>(true);
            fps.playerStats = contents.GetComponentInChildren<PlayerStats>(true);
            fps.inputManager = contents.GetComponentInChildren<InputManager>(true);

            // Save.
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(contents, FpsPrefabPath);
            PrefabUtility.UnloadPrefabContents(contents);
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

            var smoke = root.AddComponent<SmokeThrowable>();
            smoke.fuseTime = 1f; // short fuse so it pops near the landing point

            EnsureFolder("Assets/Scripts/Network", "Prefabs");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, SmokePrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static GameObject BuildAIEntityPrefab(GameObject grenadePrefab, GameObject smokePrefab)
        {
            var root = new GameObject("AIEntity");

            root.AddComponent<NetworkIdentity>();

            var nt = root.AddComponent<NetworkTransformReliable>();
            nt.syncDirection = SyncDirection.ServerToClient;
            nt.syncPosition = true;
            nt.syncRotation = true;
            nt.interpolatePosition = true;
            nt.interpolateRotation = true;

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

            // Health (same as player).
            root.AddComponent<NetworkPlayerHealth>();

            // AI controller.
            var ai = root.AddComponent<NetworkAIController>();
            ai.combat = combat;

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
