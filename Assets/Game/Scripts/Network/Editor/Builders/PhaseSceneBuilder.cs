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
    /// NetworkSetup (partial) — Menu entry points for the Phase3/5/6/7/8 and match scenes.
    /// </summary>
    public static partial class NetworkSetup
    {
                [MenuItem("HagenDa/Create Phase3 Scene")]
                public static void CreatePhase3Scene()
                {
                    EnsureFolder("Assets", "Scenes");
                    EnsureFolder("Assets/Game", "Prefabs");

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

                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Game/Scenes/Phase3.scene");
                    AssetDatabase.SaveAssets();
                    Debug.Log("[NetworkSetup] Done. Created Assets/Game/Scenes/Phase3.scene with AI, throwables, and NavMesh.");
                }

                [MenuItem("HagenDa/Create Phase5 Scene")]
                public static void CreatePhase5Scene()
                {
                    EnsureFolder("Assets", "Scenes");
                    EnsureFolder("Assets/Game", "Prefabs");

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

                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Game/Scenes/Phase5.scene");
                    AssetDatabase.SaveAssets();
                    Debug.Log("[NetworkSetup] Done. Created Assets/Game/Scenes/Phase5.scene with the M4 firearm, AI, and NavMesh.");
                }

                [MenuItem("HagenDa/Create Phase6 Scene")]
                public static void CreatePhase6Scene()
                {
                    EnsureFolder("Assets", "Scenes");
                    EnsureFolder("Assets/Game", "Prefabs");
                    EnsureFolder("Assets/Game", "Equipment");

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

                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Game/Scenes/Phase6.scene");
                    AssetDatabase.SaveAssets();
                    Debug.Log("[NetworkSetup] Done. Created Assets/Game/Scenes/Phase6.scene with the equipment system, EMP, and AI.");
                }

                [MenuItem("HagenDa/Create Phase7 Scene")]
                public static void CreatePhase7Scene()
                {
                    BuildMatchScene("Assets/Game/Scenes/Phase7.scene");
                }

                [MenuItem("HagenDa/Create Phase8 Scene")]
                public static void CreatePhase8Scene()
                {
                    BuildMatchScene("Assets/Game/Scenes/Phase8.scene");
                }

                private static void BuildMatchScene(string scenePath)
                {
                    EnsureFolder("Assets", "Scenes");
                    EnsureFolder("Assets/Game", "Prefabs");
                    EnsureFolder("Assets/Game", "Equipment");
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
    }
}
