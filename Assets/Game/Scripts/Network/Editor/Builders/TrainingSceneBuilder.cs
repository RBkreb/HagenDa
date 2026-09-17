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
    /// NetworkSetup (partial) — Menu entry points for the ML training and S1 training scenes.
    /// </summary>
    public static partial class NetworkSetup
    {
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

                private static void CreateS1MultiAreaScene(int areaCount, float spacing, int targetsPerArea)
                {
                    EnsureFolder("Assets", "Scenes");
                    EnsureFolder("Assets/Game", "Prefabs");
                    EnsureFolder("Assets/Game", "TrainingMaps");

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
                    EnsureFolder("Assets/Game", "Prefabs");
                    EnsureFolder("Assets/Game", "TrainingMaps");

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
    }
}
