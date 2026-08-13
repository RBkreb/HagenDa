using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

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

        [MenuItem("HagenDa/Setup Multiplayer Scene")]
        public static void Setup()
        {
            EnsureFolder("Assets/Scripts/Network", "Prefabs");

            GameObject playerPrefab = BuildPlayerPrefab();

            SetupScene(playerPrefab);

            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkSetup] Done. Built player prefab and configured the active scene.");
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

            // Physics (mirrors FPS Engine: Rigidbody + CapsuleCollider, freezeRotation, manual gravity)
            var rb = root.AddComponent<Rigidbody>();
            rb.freezeRotation = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.height = 2f;
            capsule.radius = 0.5f;
            capsule.center = new Vector3(0f, 1f, 0f);

            // Behaviour
            var controller = root.AddComponent<NetworkPlayerController>();
            root.AddComponent<NetworkPlayerHealth>();

            // Camera child (local player only)
            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(root.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var cam = camGo.AddComponent<Camera>();
            camGo.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();
            controller.playerCamera = cam;

            // Remote visual (capsule body)
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
            controller.visual = body;

            // Save
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ---------------------------------------------------------------
        // SCENE
        // ---------------------------------------------------------------
        private static void SetupScene(GameObject playerPrefab)
        {
            if (Object.FindObjectOfType<NetworkManager>() != null)
            {
                Debug.LogWarning("[NetworkSetup] NetworkManager already present, skipping scene setup.");
                return;
            }

            // NetworkManager + KCP transport + HUD
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            var kcp = nmGo.AddComponent<kcp2k.KcpTransport>();
            nm.transport = kcp;
            nm.playerPrefab = playerPrefab;
            nm.autoCreatePlayer = true;
            nmGo.AddComponent<NetworkManagerHUD>();

            // Spawn points
            CreateSpawnPoint("Spawn A", new Vector3(-5f, 1f, 0f));
            CreateSpawnPoint("Spawn B", new Vector3(5f, 1f, 0f));

            // Floor
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(10f, 1f, 10f);

            // Shootable targets
            CreateTarget(new Vector3(0f, 1f, 10f));
            CreateTarget(new Vector3(-8f, 1f, 10f));

            // Disable the scene's pre-existing camera so the player camera takes over.
            var existing = Camera.main;
            if (existing != null) existing.gameObject.SetActive(false);

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

        private static void MarkSceneDirty()
        {
            var scene = SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        }
    }
}
