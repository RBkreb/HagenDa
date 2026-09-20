using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// 用两个可用素材包
    /// (<c>ThirdParty/RPG_FPS_game_assets_industrial</c> 与
    /// <c>ThirdParty/SimpleNaturePack</c>)以及 MapMagic 自带的演示地形层,
    /// 生成一份开箱可用的 <see cref="MapGenConfig"/>。
    ///
    /// 预制体引用一律**按名字查找**,不硬编码 GUID —— 素材包是 vendored 的,
    /// 升级/重导会换 GUID,按名查找不会因此断掉。
    /// </summary>
    public static class MapGenDefaults
    {
        private const string IndustrialRoot = "Assets/ThirdParty/RPG_FPS_game_assets_industrial";
        private const string NatureRoot = "Assets/ThirdParty/SimpleNaturePack";
        private const string TerrainLayerFolder = "Assets/Game/Materials/TerrainLayers";
        private const string DefaultConfigPath = "Assets/Game/Settings/MapGenConfig.asset";

        // 素材包根目录名 → 优先在哪个包里找
        private static readonly string[] IndustrialDirs = { IndustrialRoot, NatureRoot };

        // ==================================================================
        // 菜单
        // ==================================================================

        [MenuItem("HagenDa/MapMagic/Create Default Config", priority = 1)]
        public static void CreateDefaultConfig()
        {
            EnsureFolder("Assets/Game/Settings");
            EnsureFolder("Assets/Game/Match/Graphs");
            EnsureFolder("Assets/Game/Materials");
            EnsureFolder(TerrainLayerFolder);

            var config = AssetDatabase.LoadAssetAtPath<MapGenConfig>(DefaultConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<MapGenConfig>();
                AssetDatabase.CreateAsset(config, DefaultConfigPath);
            }

            Populate(config);
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();

            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
            Debug.Log($"[MapGen] 默认配置已生成:{DefaultConfigPath}");
        }

        [MenuItem("HagenDa/MapMagic/Build Default Anchors", priority = 2)]
        public static void BuildDefaultAnchors()
        {
            var config = Selection.activeObject as MapGenConfig;
            if (config == null)
            {
                Debug.LogError("[MapGen] 请先选中一份 MapGenConfig 资产。");
                return;
            }

            config.BuildDefaultAnchors();
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MapGen] 已生成 {config.anchors.Count} 个默认锚点" +
                      "(安全区对角线 + 据点 A 中心 / B·C 对称)。");
        }

        [MenuItem("HagenDa/MapMagic/Build Default Anchors", true)]
        public static bool ValidateBuildDefaultAnchors() => Selection.activeObject is MapGenConfig;

        // ==================================================================
        // 填充
        // ==================================================================

        private static void Populate(MapGenConfig c)
        {
            c.sizeX = 500f;
            c.sizeZ = 500f;
            c.resolution = 1025;
            c.heightMax = 120f;
            c.symmetry = MapSymmetry.Rotate180;
            c.ringWidth = 50f;
            c.ringHeight = 0.85f;
            c.ringTransition = 25f;

            c.baseHeight = 0.09f;
            c.hillAmplitude = 0.22f;
            c.hillScale = 180f;
            c.hillDetail = 0.45f;
            c.hillSharpness = 1.15f;

            c.centerFeature = true;
            c.centerRadius = 45f;
            c.centerTransition = 40f;
            c.centerHeight = 0.22f;

            c.flattenZonePads = true;
            c.padTransition = 30f;
            c.capturePadHeight = 0.14f;
            c.garrisonPadHeight = 0.10f;
            c.padShape = MapPadShape.Circle;

            c.detailAmplitude = 0.045f;
            c.detailScale = 45f;
            c.erosionIterations = 0;
            c.blurIterations = 1;

            // ---- 通路:三通路(中路 + 左右侧翼)+ 两条纵向连接 ----
            c.lanes = new List<MapLaneDef>
            {
                // 中路:两安全区之间,穿过中央地标(南北向)
                new MapLaneDef
                {
                    name = "Mid", from = new Vector2(-175f, -175f), to = new Vector2(175f, 175f),
                    width = 45f, transition = 30f, flatten = 0.50f,
                    levelNormalized = 0.11f, heightBias = 0f,
                },
                // 侧翼 A:沿 -X 侧的绕行路
                new MapLaneDef
                {
                    name = "FlankWest", from = new Vector2(-175f, 175f), to = new Vector2(175f, -175f),
                    width = 35f, transition = 28f, flatten = 0.42f,
                    levelNormalized = 0.10f, heightBias = 0.04f,
                },
                // 侧翼 B:沿 +X 侧的绕行路
                new MapLaneDef
                {
                    name = "FlankEast", from = new Vector2(190f, 190f), to = new Vector2(-190f, -190f),
                    width = 35f, transition = 28f, flatten = 0.42f,
                    levelNormalized = 0.10f, heightBias = 0.04f,
                },
                // 纵向连接 1:据点 A 与 B/C 之间
                new MapLaneDef
                {
                    name = "LinkAB", from = new Vector2(0f, 110f), to = new Vector2(-95f, -60f),
                    width = 26f, transition = 22f, flatten = 0.45f,
                    levelNormalized = 0.13f, heightBias = 0f,
                },
                // 纵向连接 2:据点 A 与 C 之间
                new MapLaneDef
                {
                    name = "LinkAC", from = new Vector2(0f, 110f), to = new Vector2(95f, -60f),
                    width = 26f, transition = 22f, flatten = 0.45f,
                    levelNormalized = 0.13f, heightBias = 0f,
                },
            };

            // ---- 锚点 ----
            // 半径要显式设定:安全区中心按 PlayHalf - garrisonRadius - spawnInset 内缩,
            // 保证半径 70 的安全区完整落在有效战斗区内(不压进外圈山脊、不溢出地图)。
            c.garrisonRadius = 70f;
            c.captureRadius = 30f;
            c.spawnInset = 5f;
            c.captureAOffset = 110f;
            c.captureBCOffset = 115f;
            c.BuildDefaultAnchors();

            // ---- 地形层:MapMagic 演示层,顺手复制到项目资产目录 ----
            c.terrainLayers = BuildTerrainLayers();

            // ---- 掩体 ----
            BuildCoverPalette(c);

            // ---- 装饰 ----
            c.mapDecor = BuildMapDecor();
            c.zoneDecor = BuildZoneDecor();
        }

        // ==================================================================
        // 地形层
        // ==================================================================

        private static List<MapLayerRule> BuildTerrainLayers()
        {
            // MapMagic 自带 Demo/TerrainLayers 一组 HDRP 兼容的地形层贴图。
            // “沙/砾/草/泥”正好对应低地 → 台地 → 高台的高度带。
            var spec = new[]
            {
                // 名称       源层名        坡度区间      高度区间        平铺
                ("Ground", "Sand",       new Vector2(0f, 24f),  new Vector2(0f, 0.22f),  new Vector2(16f, 16f)),
                ("Meadow", "GrassGreen", new Vector2(0f, 22f),  new Vector2(0.22f, 0.55f), new Vector2(16f, 16f)),
                ("Rock",   "Gravel",     new Vector2(18f, 90f), new Vector2(0f, 1f),    new Vector2(20f, 20f)),
                ("Cliff",  "CliffDark",  new Vector2(40f, 90f), new Vector2(0.35f, 1f), new Vector2(24f, 24f)),
                ("Dirt",   "Dirt",       new Vector2(0f, 40f),  new Vector2(0.55f, 1f), new Vector2(18f, 18f)),
            };

            var rules = new List<MapLayerRule>();
            foreach ((string name, string src, Vector2 slope, Vector2 height, Vector2 tile) in spec)
            {
                Texture2D diffuse = FindTerrainLayerDiffuse(src);
                rules.Add(new MapLayerRule
                {
                    name = name,
                    diffuse = diffuse,
                    tileSize = tile,
                    slopeRange = slope,
                    slopeSmooth = 8f,
                    heightRange = height,
                    heightSmooth = 0.05f,
                });
            }
            return rules;
        }

        /// <summary>从 MapMagic 演示地形层里取漫反射贴图。</summary>
        private static Texture2D FindTerrainLayerDiffuse(string layerName)
        {
            string path = $"Assets/ThirdParty/MapMagic/Demo/TerrainLayers/{layerName}.terrainlayer";
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer != null && layer.diffuseTexture != null) return layer.diffuseTexture;
            return null;
        }

        // ==================================================================
        // 掩体调色板
        // ==================================================================

        private static void BuildCoverPalette(MapGenConfig c)
        {
            // 半身掩体 ~1.0–1.3 m:油桶、木箱、路障
            c.coverLowPrefabs = FindPrefabs(
                "Barrel_v2_single", "Barrel_v1_LD1", "Barrel_v3_single",
                "Wooden_box_v1_LD1square", "Wooden_box_v1_LD1", "Road_block_v1");

            // 全身掩体 1.8 m+:混凝土围栏、电箱、垃圾箱、木箱堆
            c.coverTallPrefabs = FindPrefabs(
                "Concrete_fence_v2_S", "Concrete_fence_v2_S_half",
                "Electric_box_v2", "Dumpsters_v1_garbadge",
                "Bags_on_pallet_v1_1", "UNIConcrete_fence_v1_s");

            // 大型块状 / 可绕行:集装箱、油罐、围墙段、桥桩
            c.coverBlockPrefabs = FindPrefabs(
                "Cargo_container_v1_LD1close", "Cargo_container_v1_LD1through",
                "Oil_tank_v2", "Concrete_fence_v1_wall_set_v1",
                "Generator_v1", "Electric_box_v3");

            // 数量由验收要求反推,不是拍脑袋:
            //   "任意位置 15 m 内至少 1 个半身掩体" → 每 707 m² 一个,
            //   有效战斗区 400×400 = 160000 m² → 理想铺满需 ~226 个。
            //   实测:600 个只能覆盖 ~60% 的采样点(陡坡放不下掩体、据点周围要净空),
            //   所以再放宽坡度上限、加密到 900 个。
            c.coverCount = 900;
            c.coverMinGap = 8f;
            c.coverSlopeMaxDeg = 25f;
            c.coverZoneClearance = 1.05f;
        }

        // ==================================================================
        // 装饰
        // ==================================================================

        private static List<MapDecorRule> BuildMapDecor()
        {
            return new List<MapDecorRule>
            {
                // 树:坡缓的台地/高地成片
                new MapDecorRule
                {
                    name = "Trees",
                    prefabs = FindPrefabs("Tree_01", "Tree_02", "Tree_03", "Tree_04", "Tree_05"),
                    densityPer1000m2 = 1.2f,
                    minSpacing = 14f,
                    slopeMaxDeg = 20f,
                    heightBand = new Vector2(0.12f, 1f),
                    scaleRange = new Vector2(0.85f, 1.3f),
                    randomYaw = true,
                    clusterScale = 90f,
                    clusterThreshold = 0.45f,
                    avoidZones = true,
                },
                // 灌木:低地/坡脚
                new MapDecorRule
                {
                    name = "Bushes",
                    prefabs = FindPrefabs("Bush_01", "Bush_02", "Bush_03"),
                    densityPer1000m2 = 2.5f,
                    minSpacing = 6f,
                    slopeMaxDeg = 28f,
                    heightBand = new Vector2(0f, 1f),
                    scaleRange = new Vector2(0.9f, 1.5f),
                    clusterScale = 45f,
                    clusterThreshold = 0.4f,
                },
                // 大石:坡地/高地,贴坡
                new MapDecorRule
                {
                    name = "Rocks",
                    prefabs = FindPrefabs("Rock_05", "Rock_04"),
                    densityPer1000m2 = 0.8f,
                    minSpacing = 16f,
                    slopeMaxDeg = 42f,
                    heightBand = new Vector2(0.15f, 1f),
                    scaleRange = new Vector2(0.8f, 1.6f),
                    randomYaw = true,
                    alignToSlope = true,
                },
                // 草丛/小花/蘑菇:纯装饰,**必须剔除碰撞体**
                new MapDecorRule
                {
                    name = "GroundCover",
                    prefabs = FindPrefabs("Grass_01", "Grass_02", "Flowers_01", "Flowers_02",
                                          "Mushroom_01", "Mushroom_02"),
                    densityPer1000m2 = 14f,
                    minSpacing = 2.5f,
                    slopeMaxDeg = 32f,
                    heightBand = new Vector2(0f, 1f),
                    scaleRange = new Vector2(0.8f, 1.4f),
                    clusterScale = 30f,
                    clusterThreshold = 0.35f,
                    stripColliders = true,
                },
                // 工业杂物:台地/据点附近零散
                new MapDecorRule
                {
                    name = "IndustrialClutter",
                    prefabs = FindPrefabs("Barrel_v1_LD1", "Barrel_v3_single", "Palet_v1_single",
                                          "Wooden_box_v1_LD1square", "Conditioner_v1"),
                    densityPer1000m2 = 1.6f,
                    minSpacing = 10f,
                    slopeMaxDeg = 14f,
                    heightBand = new Vector2(0f, 0.6f),
                    scaleRange = new Vector2(1f, 1f),
                    clusterScale = 60f,
                    clusterThreshold = 0.55f,
                },
                // 枯木/倒枝:林地地面
                new MapDecorRule
                {
                    name = "Deadwood",
                    prefabs = FindPrefabs("Stump_01", "Branch_01"),
                    densityPer1000m2 = 1.0f,
                    minSpacing = 12f,
                    slopeMaxDeg = 25f,
                    heightBand = new Vector2(0.12f, 1f),
                    scaleRange = new Vector2(0.9f, 1.4f),
                },
            };
        }

        private static List<MapDecorRule> BuildZoneDecor()
        {
            return new List<MapDecorRule>
            {
                // 集装箱:主结构,环带排布形成掩体街
                new MapDecorRule
                {
                    name = "Containers",
                    prefabs = FindPrefabs("Cargo_container_v1_LD1close", "Cargo_container_v1_LD2close",
                                          "Cargo_container_v1_LD1through"),
                    count = 14,
                    minSpacing = 11f,
                    slopeMaxDeg = 6f,
                    scaleRange = new Vector2(1f, 1f),
                    randomYaw = false,
                },
                // 混凝土围墙段:围出场地边界
                new MapDecorRule
                {
                    name = "FenceWalls",
                    prefabs = FindPrefabs("Concrete_fence_v1_wall_set_v1", "Concrete_fence_v2_S"),
                    count = 12,
                    minSpacing = 10f,
                    slopeMaxDeg = 6f,
                    scaleRange = new Vector2(1f, 1f),
                    randomYaw = false,
                },
                // 油桶/木箱:内部散件
                new MapDecorRule
                {
                    name = "Crates",
                    prefabs = FindPrefabs("Wooden_box_v1_LD1square", "Barrel_v2_single",
                                          "Barrel_v2_quadro", "Bags_on_pallet_v1_1"),
                    count = 18,
                    minSpacing = 6f,
                    slopeMaxDeg = 6f,
                    scaleRange = new Vector2(1f, 1f),
                },
                // 照明/电力设施
                new MapDecorRule
                {
                    name = "Utility",
                    prefabs = FindPrefabs("Electric_box_v1", "Generator_v1", "Conditioner_v1"),
                    count = 6,
                    minSpacing = 12f,
                    slopeMaxDeg = 6f,
                    scaleRange = new Vector2(1f, 1f),
                    randomYaw = false,
                },
            };
        }

        // ==================================================================
        // 按名找预制体
        // ==================================================================

        /// <summary>
        /// 按预制体名在素材包里查找。名字在两包内都唯一,找不到就跳过并记一条警告
        /// (素材包重导后名字可能变,不能因为缺失就整条规则失效)。
        /// </summary>
        private static GameObject[] FindPrefabs(params string[] names)
        {
            var found = new List<GameObject>();
            foreach (string name in names)
            {
                string path = FindPrefabPath(name);
                if (path == null)
                {
                    Debug.LogWarning($"[MapGen] 素材包里找不到预制体 '{name}',已跳过。");
                    continue;
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) found.Add(prefab);
                else Debug.LogWarning($"[MapGen] 预制体 '{name}' 加载失败({path})。");
            }
            return found.ToArray();
        }

        private static string FindPrefabPath(string name)
        {
            string[] guids = AssetDatabase.FindAssets($"{name} t:Prefab", IndustrialDirs);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name) return path;
            }
            return null;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
