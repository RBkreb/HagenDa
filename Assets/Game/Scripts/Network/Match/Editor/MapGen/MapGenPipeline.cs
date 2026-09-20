using System;
using System.Collections.Generic;
using System.IO;
using MapMagic.Core;
using MapMagic.Nodes;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>可序列化的验收指标(每阶段一份,落盘为 JSON)。</summary>
    [Serializable]
    public class MapGenMetrics
    {
        public string configName;
        public string generatedAtUtc;
        public int seed;
        public float sizeX, sizeZ;
        public float heightMax;
        public int resolution;

        public float terrainHeightMin;
        public float terrainHeightMax;
        public float terrainHeightMean;

        public float navigablePercent;      // NavMesh 覆盖的有效战斗区占比
        public int navMeshTriangles;

        public float slopeMeanDeg;
        public float slopeOver40Percent;    // 坡度 > 40° 的面积占比(不可行走)

        public float zoneFlatnessMaxDeg;    // 据点/安全区平台内的最大坡度

        public int coverTotal;
        public float coverHalfCoverPercent; // 满足"15 m 内至少一个半身掩体"的采样点占比
        public float coverFullCoverPercent; // 满足"40 m 内至少一个全身掩体"的采样点占比

        public int mapDecorCount;
        public int zoneDecorCount;

        public double generationSeconds;
    }

    /// <summary>
    /// 地图生成流水线的编排层。
    ///
    /// 顺序:
    ///   1. 由 <see cref="MapGenConfig"/> 合成设计掩码 → 建 MapMagic 图
    ///   2. 装配 MapMagicObject 并钉住瓦片
    ///   3. 阻塞式生成地形
    ///   4. 烘 NavMesh(整图一个 NavMeshSurface)
    ///   5. 摆放装饰物 / 据点内装饰 / 掩体
    ///   6. 度量并落 metrics.json
    ///
    /// 地形是**静态烘焙进场景**的(按决策 #5):运行时不再按种子重生成,
    /// 以保证服务端与所有客户端看到完全一致的地图。这一点对竞技公平性是硬要求。
    /// </summary>
    public static class MapGenPipeline
    {
        private const string MetricsFolder = "docs/map/mapmagic";
        private const string NavMeshSurfaceName = "MapNavMesh";

        // ==================================================================
        // 菜单入口
        // ==================================================================

        [MenuItem("HagenDa/MapMagic/Full Build (New Scene)", priority = 20)]
        public static void FullBuildFromSelection() => RunFullBuild(Selection.activeObject as MapGenConfig);

        [MenuItem("HagenDa/MapMagic/Full Build (New Scene)", true)]
        public static bool ValidateFullBuild() => Selection.activeObject is MapGenConfig;

        [MenuItem("HagenDa/MapMagic/Rebuild In Current Scene", priority = 21)]
        public static void RebuildCurrentScene() => RunFullBuild(Selection.activeObject as MapGenConfig, newScene: false);

        [MenuItem("HagenDa/MapMagic/Rebuild In Current Scene", true)]
        public static bool ValidateRebuildCurrent() =>
            Selection.activeObject is MapGenConfig &&
            !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("HagenDa/MapMagic/Regenerate Terrain Only", priority = 22)]
        public static void RegenerateTerrainOnly()
        {
            var config = Selection.activeObject as MapGenConfig;
            if (!Validate(config)) return;

            try
            {
                MapMagicObject mm = MapMagicGraphBuilder.BuildObject(config, MapMagicGraphBuilder.BuildGraph(config));
                MapMagicGraphBuilder.RunGenerate(mm);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        [MenuItem("HagenDa/MapMagic/Regenerate Terrain Only", true)]
        public static bool ValidateRegenerate() =>
            Selection.activeObject is MapGenConfig && !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("HagenDa/MapMagic/Re-place Decor Only", priority = 23)]
        public static void ReplaceDecorOnly()
        {
            var config = Selection.activeObject as MapGenConfig;
            if (!Validate(config)) return;

            try
            {
                IList<MapAnchor> anchors = config.anchors;
                MapDecorPlacer.PlaceMapDecor(config, anchors);
                MapDecorPlacer.PlaceZoneDecor(config, anchors);
                MapDecorPlacer.PlaceCoverFields(config, anchors);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        [MenuItem("HagenDa/MapMagic/Re-place Decor Only", true)]
        public static bool ValidateReplaceDecor() =>
            Selection.activeObject is MapGenConfig && !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("HagenDa/MapMagic/Bake NavMesh", priority = 40)]
        public static void BakeNavMesh()
        {
            try
            {
                BakeNavMeshInternal();
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        [MenuItem("HagenDa/MapMagic/Report Metrics", priority = 41)]
        public static void ReportMetrics()
        {
            var config = Selection.activeObject as MapGenConfig;
            if (!Validate(config)) return;

            MapGenMetrics m = Measure(config, generationSeconds: 0.0);
            WriteMetrics(config, m);
            Debug.Log(FormatMetrics(m));
        }

        [MenuItem("HagenDa/MapMagic/Report Metrics", true)]
        public static bool ValidateReport() => Selection.activeObject is MapGenConfig;

        // ==================================================================
        // 主流程
        // ==================================================================

        private static void RunFullBuild(MapGenConfig config, bool newScene = true)
        {
            if (!Validate(config)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string scenePath = null;

                if (newScene)
                {
                    // 不要用 SaveCurrentModifiedScenesIfUserWantsTo —— 它会弹模态对话框,
                    // 在批处理/桥接调用里会直接卡死整条流水线。这里确定性落盘。
                    EditorSceneManager.SaveOpenScenes();

                    Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    MapMaskComposer.EnsureFolder("Assets/Game/Scenes");
                    scenePath = $"Assets/Game/Scenes/{config.name}.scene";
                    EnsureLighting();
                }

                EditorUtility.DisplayProgressBar("MapGen", "合成设计掩码 + 建图…", 0.05f);
                MapMaskComposer.Compose(config);

                Graph graph = MapMagicGraphBuilder.BuildGraph(config);
                MapMagicObject mm = MapMagicGraphBuilder.BuildObject(config, graph);

                EditorUtility.DisplayProgressBar("MapGen", "生成地形…", 0.15f);
                if (!MapMagicGraphBuilder.RunGenerate(mm))
                {
                    Debug.LogError("[MapGen] 地形生成失败,已中止流水线。");
                    return;
                }

                EditorUtility.DisplayProgressBar("MapGen", "摆放装饰与掩体…", 0.70f);
                int mapDecor = MapDecorPlacer.PlaceMapDecor(config, config.anchors);
                int zoneDecor = MapDecorPlacer.PlaceZoneDecor(config, config.anchors);
                int covers = MapDecorPlacer.PlaceCoverFields(config, config.anchors);

                // 锚点贴地:Mask 阶段只用 XZ,所以锚点的 Y 一直是 0。下游
                // (MatchSceneBuilder / 出生点 / 部署点)是按 position 含 Y 用的,
                // 不贴地的话据点、安全区、出生点会全部埋在地形下方(实测地形 ~12 m)。
                SnapAnchorsToTerrain(config);

                // NavMesh 必须在掩体/装饰落位**之后**烘:实体障碍(集装箱、围墙)要
                // 在地形上挖出不可通行区域,否则 AI 会直接穿墙寻路。
                // 草丛等已剔除碰撞体,NavMesh 不会受影响。
                EditorUtility.DisplayProgressBar("MapGen", "烘 NavMesh…", 0.85f);
                BakeNavMeshInternal();

                EditorUtility.DisplayProgressBar("MapGen", "度量…", 0.95f);
                MapGenMetrics metrics = Measure(config, sw.Elapsed.TotalSeconds);
                // 直接用摆放调用的返回值,别在 Measure 里重新 Find —— 那里拿到的可能是 0。
                metrics.mapDecorCount = mapDecor;
                metrics.zoneDecorCount = zoneDecor;
                WriteMetrics(config, metrics);

                if (newScene && scenePath != null)
                {
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                    EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scenePath);
                    Debug.Log($"[MapGen] 场景已保存:{scenePath}");
                }

                Debug.Log(FormatMetrics(metrics));
            }
            catch (Exception e)
            {
                Debug.LogError($"[MapGen] 流水线异常:{e}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }
        }

        private static bool Validate(MapGenConfig config)
        {
            if (config == null)
            {
                Debug.LogError("[MapGen] 请先在 Project 里选中一份 MapGenConfig 资产。");
                return false;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[MapGen] 请退出 Play 模式后再构建地图。");
                return false;
            }
            if (config.anchors == null || config.anchors.Count == 0)
            {
                Debug.LogWarning("[MapGen] MapGenConfig 没有锚点,据点平台不会压平、" +
                                 "据点装饰不会生成。可用 Inspector 里的 Build Default Anchors。");
            }
            return true;
        }

        // ==================================================================
        // NavMesh
        // ==================================================================

        /// <summary>
        /// 整图一个 NavMeshSurface。
        ///
        /// 注意不要每块地形挂一个 —— MapMagic 多瓦片时每块独立烘焙会在接缝处断掉
        /// 连通性,AI 会卡在块边界。单块地图同样用整图烘焙,保持行为一致。
        /// </summary>
        private static void BakeNavMeshInternal()
        {
            GameObject root = GameObject.Find(NavMeshSurfaceName);
            if (root == null) root = new GameObject(NavMeshSurfaceName);

            var surface = root.GetComponent<NavMeshSurface>();
            if (surface == null) surface = root.AddComponent<NavMeshSurface>();

            // useGeometry=PhysicsColliders 时烘培读的是物理引擎缓存的碰撞体变换,
            // 刚生成/移动的对象在同一帧里还没同步过去,不同步就会烘出空网格。
            Physics.SyncTransforms();

            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = ~0;

            surface.BuildNavMesh();

            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            int tris = tri.indices != null ? tri.indices.Length / 3 : 0;
            Debug.Log($"[MapGen] NavMesh 已烘焙({tris} 三角面)。");
        }

        private static void EnsureLighting()
        {
            if (GameObject.Find("Directional Light") == null)
            {
                var go = new GameObject("Directional Light");
                var light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;
                go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
        }

        // ==================================================================
        // 度量
        // ==================================================================

        private static MapGenMetrics Measure(MapGenConfig config, double generationSeconds)
        {
            var m = new MapGenMetrics
            {
                configName = config.name,
                generatedAtUtc = DateTime.UtcNow.ToString("o"),
                seed = config.seed,
                sizeX = config.sizeX,
                sizeZ = config.sizeZ,
                heightMax = config.heightMax,
                resolution = config.resolution,
                generationSeconds = generationSeconds,
                mapDecorCount = CountChildren(MapDecorPlacer.MapDecorRootName),
                zoneDecorCount = CountChildren(MapDecorPlacer.ZoneDecorRootName),
            };

            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MapGen] 场景里没有 Terrain,指标只统计装饰与掩体。");
                MeasureCover(config, m);
                return m;
            }

            TerrainData data = terrain.terrainData;
            Vector3 tp = terrain.transform.position;
            Vector3 ts = data.size;

            // ---- 高程与坡度:只统计**有效战斗区** ----
            // 外圈 50 m 山脊是刻意做的不可通行墙(25 m 内抬升 90 m,接近垂直),
            // 把它算进来会让坡度均值/超限占比完全失真,掩盖真正的可玩区地形。
            const int grid = 128;
            float hMin = float.MaxValue, hMax = float.MinValue, hSum = 0f;
            int hCount = 0;
            double slopeSum = 0.0;
            int over40 = 0, slopeCount = 0;

            for (int gz = 0; gz < grid; gz++)
                for (int gx = 0; gx < grid; gx++)
                {
                    float wx = Mathf.Lerp(-config.PlayHalfX, config.PlayHalfX, (gx + 0.5f) / grid);
                    float wz = Mathf.Lerp(-config.PlayHalfZ, config.PlayHalfZ, (gz + 0.5f) / grid);

                    float u = Mathf.Clamp01((wx - tp.x) / Mathf.Max(0.001f, ts.x));
                    float v = Mathf.Clamp01((wz - tp.z) / Mathf.Max(0.001f, ts.z));

                    float h = data.GetInterpolatedHeight(u, v);
                    hMin = Mathf.Min(hMin, h);
                    hMax = Mathf.Max(hMax, h);
                    hSum += h;
                    hCount++;

                    float steep = data.GetSteepness(u, v);
                    slopeSum += steep;
                    slopeCount++;
                    if (steep > 40f) over40++;
                }

            if (hCount > 0)
            {
                m.terrainHeightMin = hMin;
                m.terrainHeightMax = hMax;
                m.terrainHeightMean = hSum / hCount;
            }
            if (slopeCount > 0)
            {
                m.slopeMeanDeg = (float)(slopeSum / slopeCount);
                m.slopeOver40Percent = 100f * over40 / slopeCount;
            }

            // ---- NavMesh 覆盖(在有效战斗区内采样) ----
            const int navGrid = 48;
            int navHit = 0, navTotal = 0;
            for (int gz = 0; gz < navGrid; gz++)
                for (int gx = 0; gx < navGrid; gx++)
                {
                    float x = Mathf.Lerp(-config.PlayHalfX, config.PlayHalfX, (gx + 0.5f) / navGrid);
                    float z = Mathf.Lerp(-config.PlayHalfZ, config.PlayHalfZ, (gz + 0.5f) / navGrid);
                    navTotal++;

                    // 从**该点的地面高度**起采样,不是从 heightMax。
                    // 从天上 120 m 起算,再小的 snap 半径也够不到脚下的 NavMesh。
                    // 半径 2 m 表示"这一格附近就站得住"(与玩家/导航体量匹配)。
                    float gy = data.GetInterpolatedHeight(
                        Mathf.Clamp01((x - tp.x) / Mathf.Max(0.001f, ts.x)),
                        Mathf.Clamp01((z - tp.z) / Mathf.Max(0.001f, ts.z))) + tp.y;

                    if (NavMesh.SamplePosition(new Vector3(x, gy + 1.5f, z),
                            out _, 2f, NavMesh.AllAreas))
                        navHit++;
                }
            if (navTotal > 0) m.navigablePercent = 100f * navHit / navTotal;

            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            m.navMeshTriangles = tri.indices != null ? tri.indices.Length / 3 : 0;

            // ---- 据点平台平整度 ----
            float worst = 0f;
            if (config.anchors != null)
                foreach (MapAnchor a in config.anchors)
                {
                    if (a == null || (a.role != AnchorRole.CapturePoint &&
                                      a.role != AnchorRole.GarrisonRed &&
                                      a.role != AnchorRole.GarrisonBlue)) continue;

                    float u = Mathf.Clamp01((a.position.x - tp.x) / Mathf.Max(0.001f, ts.x));
                    float v = Mathf.Clamp01((a.position.z - tp.z) / Mathf.Max(0.001f, ts.z));
                    worst = Mathf.Max(worst, data.GetSteepness(u, v));
                }
            m.zoneFlatnessMaxDeg = worst;

            MeasureCover(config, m);
            return m;
        }

        /// <summary>
        /// 掩体密度验收:在有效战斗区随机采样,检查 15 m 内是否有半身掩体、
        /// 40 m 内是否有全身掩体(按碰撞体包围盒高度判定)。
        ///
        /// 直接遍历 Covers 根下的碰撞体,而不是 Physics.OverlapSphere:
        /// 场景里装饰物有上千个碰撞体,固定大小的 overlap 缓冲会被它们填满,
        /// 真正的掩体反而被截断掉(实测就会得到 0%);而且地形/装饰也不该算掩体。
        /// </summary>
        private static void MeasureCover(MapGenConfig config, MapGenMetrics m)
        {
            GameObject covers = GameObject.Find(MapDecorPlacer.CoversRootName);
            if (covers == null)
            {
                m.coverTotal = 0;
                return;
            }

            Collider[] cols = covers.GetComponentsInChildren<Collider>();
            m.coverTotal = cols.Length;
            if (cols.Length == 0) return;

            // Collider.bounds 来自物理引擎缓存的变换,而缓存的更新发生在物理步/显式
            // SyncTransforms 时 —— 刚 Instantiate 出来的碰撞体在同一编辑器帧里读到的是
            // 陈旧值(全部堆在原点),掩体密度会因此被算成 0.5%。必须先同步。
            Physics.SyncTransforms();

            int n = cols.Length;
            var bounds = new Bounds[n];
            var height = new float[n];
            for (int i = 0; i < n; i++)
            {
                bounds[i] = cols[i].bounds;
                height[i] = bounds[i].size.y;
            }

            var rng = new System.Random(config.coverSeed ^ 0x5EED);
            const int samples = 400;

            int halfOk = 0, fullOk = 0, valid = 0;
            for (int s = 0; s < samples; s++)
            {
                var ground = new Vector3(
                    ((float)rng.NextDouble() * 2f - 1f) * config.PlayHalfX, 0f,
                    ((float)rng.NextDouble() * 2f - 1f) * config.PlayHalfZ);

                Terrain t = MapTerrainUtil.FindTerrain(ground);
                if (t == null) continue;
                valid++;

                // 以玩家躯干高度(脚下 +1 m)为参考点。地形高的地方若仍按地面高度
                // 采样,球心会陷进地里,掩体会被整片漏掉。
                float gy = MapTerrainUtil.SampleHeight(ground);
                var probe = new Vector3(ground.x, gy + 1f, ground.z);
                var probeXZ = new Vector2(probe.x, probe.z);

                bool half = false, full = false;
                for (int i = 0; i < n; i++)
                {
                    if (height[i] < 0.9f) continue;

                    Vector3 cp = cols[i].ClosestPointOnBounds(probe);
                    float d = Vector2.Distance(probeXZ, new Vector2(cp.x, cp.z));

                    if (!half && d <= 15f && height[i] <= 1.6f) half = true;
                    if (!full && d <= 40f && height[i] >= 1.7f) full = true;
                    if (half && full) break;
                }

                if (half) halfOk++;
                if (full) fullOk++;
            }

            if (valid > 0)
            {
                m.coverHalfCoverPercent = 100f * halfOk / valid;
                m.coverFullCoverPercent = 100f * fullOk / valid;
            }
        }

        // ==================================================================
        // 落盘
        // ==================================================================

        /// <summary>
        /// 把每个锚点的 Y 贴到地形高度。锚点既驱动据点的压平平台(只用 XZ),
        /// 也驱动场景实体的摆放(用完整 XYZ),所以生成完必须回填 Y。
        /// </summary>
        private static void SnapAnchorsToTerrain(MapGenConfig config)
        {
            if (config.anchors == null) return;

            int snapped = 0;
            foreach (MapAnchor a in config.anchors)
            {
                if (a == null) continue;
                Terrain t = MapTerrainUtil.FindTerrain(a.position);
                if (t == null) continue;

                a.position = new Vector3(a.position.x, MapTerrainUtil.SampleHeight(a.position), a.position.z);
                snapped++;
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MapGen] 锚点已贴地:{snapped}/{config.anchors.Count}");
        }

        private static int CountChildren(string rootName)
        {
            GameObject root = GameObject.Find(rootName);
            return root != null ? root.transform.childCount : 0;
        }

        private static void WriteMetrics(MapGenConfig config, MapGenMetrics m)
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                string dir = Path.Combine(root, MetricsFolder);
                Directory.CreateDirectory(dir);

                string json = JsonUtility.ToJson(m, prettyPrint: true);
                string file = Path.Combine(dir, $"metrics_{config.name}_{config.seed}.json");
                File.WriteAllText(file, json);
                Debug.Log($"[MapGen] 指标已写入 {file}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MapGen] 指标写入失败:{e.Message}");
            }
        }

        private static string FormatMetrics(MapGenMetrics m)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[MapGen] ===== {m.configName} (seed {m.seed}) =====");
            sb.AppendLine($"  尺寸 {m.sizeX}×{m.sizeZ} m @ {m.resolution},高度上限 {m.heightMax} m");
            sb.AppendLine($"  高程 {m.terrainHeightMin:F1} – {m.terrainHeightMax:F1} m(均值 {m.terrainHeightMean:F1})");
            sb.AppendLine($"  坡度均值 {m.slopeMeanDeg:F1}°,>40° 占比 {m.slopeOver40Percent:F1}%");
            sb.AppendLine($"  NavMesh 可达 {m.navigablePercent:F1}%(有效战斗区,{m.navMeshTriangles} 三角面)");
            sb.AppendLine($"  据点平台最大坡度 {m.zoneFlatnessMaxDeg:F1}°");
            sb.AppendLine($"  掩体 {m.coverTotal} 个;15 m 内有半身掩体的采样点 {m.coverHalfCoverPercent:F1}%;" +
                          $"40 m 内有全身掩体的采样点 {m.coverFullCoverPercent:F1}%");
            sb.AppendLine($"  装饰:地图 {m.mapDecorCount} / 据点内 {m.zoneDecorCount}");
            if (m.generationSeconds > 0)
                sb.AppendLine($"  生成耗时 {m.generationSeconds:F1}s");
            return sb.ToString();
        }
    }
}
