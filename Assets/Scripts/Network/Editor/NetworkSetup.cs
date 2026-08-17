using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem.UI;
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

        // ---------------------------------------------------------------
        // PREFAB
        // ---------------------------------------------------------------
        private static GameObject BuildPlayerPrefab()
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

            controller.standCollider = standCapsule;
            controller.crouchCollider = crouchCapsule;

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
            bool hdrp = pipeline != null && pipeline.GetType().Name.Contains("HighDefinition");
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
