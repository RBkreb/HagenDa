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
    /// NetworkSetup (partial) — Menu entry points for the multiplayer, physics, animation-test and solo scenes.
    /// </summary>
    public static partial class NetworkSetup
    {
                [MenuItem("HagenDa/Setup Multiplayer Scene")]
                public static void Setup()
                {
                    EnsureFolder("Assets/Game", "Prefabs");

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

                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Game/Scenes/PhysicsMovement.scene");
                    AssetDatabase.SaveAssets();
                    Debug.Log("[NetworkSetup] Done. Created Assets/Game/Scenes/PhysicsMovement.scene with the force-driven player.");
                }

                [MenuItem("HagenDa/Rebuild NetworkPlayer Prefab")]
                public static void RebuildPlayerPrefab()
                {
                    EnsureFolder("Assets/Game", "Prefabs");

                    // Re-serialize the prefab from the current script defaults WITHOUT
                    // touching the active scene.
                    BuildPlayerPrefab();
                    AssetDatabase.SaveAssets();
                    Debug.Log("[NetworkSetup] Done. Rebuilt " + PrefabPath);
                }

                [MenuItem("HagenDa/Setup Animation Test Scene")]
                public static void SetupAnimationTestScene()
                {
                    EnsureFolder("Assets", "Scenes");
                    EnsureLayerNamed(MirrorBodyLayer);
                    int mirrorLayer = LayerMask.NameToLayer(MirrorBodyLayer);

                    var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                        UnityEditor.SceneManagement.NewSceneMode.Single);

                    EnsureLighting();

                    // 地面 + 方向参照物（观察 8 向移动的位移参照）。
                    var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    floor.name = "Floor";
                    floor.transform.position = new Vector3(0f, -0.25f, 0f);
                    floor.transform.localScale = new Vector3(60f, 0.5f, 60f);

                    for (int i = 0; i < 3; i++)
                    {
                        var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        marker.name = "Marker" + i;
                        marker.transform.position = new Vector3((i - 1) * 4f, 0.75f, 7f);
                        marker.transform.localScale = new Vector3(0.5f, 1.5f, 0.5f);
                    }

                    // NetworkManager：单人房间，Play 后自动 StartHost，自动出生玩家。
                    var nmGo = new GameObject("NetworkManager");
                    var nm = nmGo.AddComponent<NetworkManager>();
                    nm.transport = nmGo.AddComponent<kcp2k.KcpTransport>();
                    nm.playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                    nm.autoCreatePlayer = true;
                    nm.maxConnections = 4;
                    nmGo.AddComponent<TrainingAutoHost>();
                    nmGo.AddComponent<AnimationTestAutoDeploy>();   // 出生后自动走真实部署流程

                    var spawn = new GameObject("SpawnPoint");
                    spawn.AddComponent<NetworkStartPosition>();
                    spawn.transform.position = new Vector3(0f, 0.05f, 0f);

                    // 镜子：镜像相机（渲染到 RT）+ 主视角前下方的幕布。
                    var mirrorCamGo = new GameObject("MirrorCamera");
                    var mirrorCam = mirrorCamGo.AddComponent<Camera>();
                    mirrorCam.fieldOfView = 42f;
                    mirrorCam.clearFlags = CameraClearFlags.SolidColor;
                    mirrorCam.backgroundColor = new Color(0.16f, 0.19f, 0.26f);
                    mirrorCam.depth = -10f;
                    mirrorCam.transform.position = new Vector3(0f, 1.2f, -2.6f);
                    mirrorCam.transform.rotation = Quaternion.identity;   // MirrorView 每帧重定位

                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.name = "Mirror";
                    Object.DestroyImmediate(quad.GetComponent<Collider>());
                    quad.transform.position = new Vector3(0f, 1.0f, 2.4f);
                    quad.transform.rotation = Quaternion.Euler(0f, 180f, 0f);   // 面向出生点
                    quad.transform.localScale = new Vector3(1.5f, 0.9f, 1f);

                    var view = quad.AddComponent<MirrorView>();
                    view.mirrorCamera = mirrorCam;
                    view.mirrorQuad = quad.GetComponent<Renderer>();

                    // 镜子层不参与任何物理碰撞（模型仅供观察）。
                    if (mirrorLayer >= 0)
                        for (int i = 0; i < 32; i++)
                            Physics.IgnoreLayerCollision(mirrorLayer, i, true);

                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, AnimationTestScenePath);
                    Debug.Log($"[NetworkSetup] Animation test scene ready: {AnimationTestScenePath} (Play 即自动 StartHost，主视角前下方挂镜子)");
                }

                [MenuItem("HagenDa/Setup Soldier Solo FPS Scene")]
                public static void SetupSoldierSoloScene()
                {
                    EnsureFolder("Assets", "Scenes");

                    var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                        UnityEditor.SceneManagement.NewSceneMode.Single);

                    EnsureLighting();

                    // 地面 + 参照物(观察走/跑/冲刺位移与转向)
                    var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    floor.name = "Floor";
                    floor.transform.position = new Vector3(0f, -0.25f, 0f);
                    floor.transform.localScale = new Vector3(80f, 0.5f, 80f);

                    for (int i = 0; i < 4; i++)
                    {
                        var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        marker.name = "Marker" + i;
                        marker.transform.position = new Vector3((i - 1.5f) * 5f, 0.75f, 10f);
                        marker.transform.localScale = new Vector3(0.6f, 1.5f, 0.6f);
                    }
                    // 远处的靶标(验证腰射/瞄准可指向)
                    var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    target.name = "Target";
                    target.transform.position = new Vector3(0f, 1.0f, 25f);
                    target.transform.localScale = new Vector3(1.2f, 2f, 0.4f);

                    // 单人房间:Play 即自动 StartHost + 自动部署真人
                    var nmGo = new GameObject("NetworkManager");
                    var nm = nmGo.AddComponent<NetworkManager>();
                    nm.transport = nmGo.AddComponent<kcp2k.KcpTransport>();
                    nm.playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                    nm.autoCreatePlayer = true;
                    nm.maxConnections = 1;
                    nmGo.AddComponent<TrainingAutoHost>();
                    nmGo.AddComponent<AnimationTestAutoDeploy>();

                    var spawn = new GameObject("SpawnPoint");
                    spawn.AddComponent<NetworkStartPosition>();
                    spawn.transform.position = new Vector3(0f, 0.05f, 0f);

                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, SoldierSoloScenePath);
                    Debug.Log($"[NetworkSetup] Solo FPS scene ready: {SoldierSoloScenePath}" +
                              " (Play 自动 Host + 部署;相机在胶囊体外表面,低头可见自身;手动验证腰射/瞄准/开火/冲刺/趴/跳/转向)");
                }
    }
}
