using System;
using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// 按 <see cref="MapGenConfig"/> 在地形上摆放装饰物与掩体。
    ///
    /// 为什么是脚本而不是 MapMagic 节点:本项目装的是 **MapMagic 2 Core**,
    /// 只有 Map/Matrix 模块,没有 Objects Output / Trees Output,也不支持
    /// Terrain 的树木与细节层。所以所有实体摆放都由这里负责。
    ///
    /// 三类摆放:
    ///   <see cref="PlaceMapDecor"/>  地图内自由装饰(噪声聚簇 + 坡度/高程约束)
    ///   <see cref="PlaceZoneDecor"/> 据点/安全区内的结构化装饰(环形排布,不随机撒点)
    ///   <see cref="PlaceCoverFields"/> 战斗掩体场(半身/全身/大型,供 AI 找掩体)
    ///
    /// 全部幂等:重复构建会先清掉自己上一次的产物。
    /// </summary>
    public static class MapDecorPlacer
    {
        public const string MapDecorRootName = "MapDecor";
        public const string ZoneDecorRootName = "ZoneDecor";
        public const string CoversRootName = "Covers";

        // ==================================================================
        // 地图装饰物
        // ==================================================================

        /// <summary>按规则在地图上自由散布装饰物。</summary>
        public static int PlaceMapDecor(MapGenConfig config, IList<MapAnchor> anchors)
        {
            GameObject root = ResetRoot(MapDecorRootName, out int removed);
            if (root == null) return 0;

            int total = 0;
            for (int r = 0; r < config.mapDecor.Count; r++)
            {
                MapDecorRule rule = config.mapDecor[r];
                if (!IsUsable(rule)) continue;

                int wanted = ResolveCount(config, rule);
                if (wanted <= 0) continue;

                var rng = new System.Random(HashSeed(config.seed, r, 0x11223344));
                total += Scatter(root, config, rule, anchors, wanted, rng);
            }

            Debug.Log($"[MapGen] 地图装饰物:{total} 个{(removed > 0 ? $"(清理旧实例 {removed})" : "")}");
            return total;
        }

        // ==================================================================
        // 据点 / 安全区内的结构化装饰
        // ==================================================================

        /// <summary>
        /// 每个据点和安全区周围按环带结构化摆放装饰。平台已被掩码压平,
        /// 所以贴地不会出现悬空或埋进斜坡的问题。
        /// </summary>
        public static int PlaceZoneDecor(MapGenConfig config, IList<MapAnchor> anchors)
        {
            GameObject root = ResetRoot(ZoneDecorRootName, out int removed);
            if (root == null) return 0;

            var zones = new List<MapAnchor>();
            foreach (MapAnchor a in anchors)
                if (a != null && (a.role == AnchorRole.CapturePoint ||
                                  a.role == AnchorRole.GarrisonRed ||
                                  a.role == AnchorRole.GarrisonBlue))
                    zones.Add(a);

            if (zones.Count == 0 || config.zoneDecor.Count == 0)
            {
                Debug.Log("[MapGen] 据点装饰:无可用锚点或规则,跳过。");
                return 0;
            }

            int total = 0;
            for (int z = 0; z < zones.Count; z++)
            {
                MapAnchor zone = zones[z];
                int zoneIndex = z;
                float radius = Mathf.Max(5f, zone.radius);
                float innerClear = radius * config.zoneDecorInnerClearance;
                float ringInner = radius * config.zoneDecorRing.x;
                float ringOuter = radius * config.zoneDecorRing.y;

                for (int r = 0; r < config.zoneDecor.Count; r++)
                {
                    MapDecorRule rule = config.zoneDecor[r];
                    if (!IsUsable(rule)) continue;

                    var rng = new System.Random(HashSeed(config.seed, z * 977 + r, 0x55667788));
                    float spacing = Mathf.Max(2f, rule.minSpacing);

                    // 先按环带铺出**候选**位置,再均匀抽稀到 rule.count。
                    // (只按环带铺会远远超量 —— 一个 70 m 半径的环带上按 6 m 间距
                    //  就能排下一百多个,而配置里只想要十几个。)
                    var candidates = new List<(Vector3 pos, int ring, int step)>();

                    int rings = Mathf.Max(1, Mathf.RoundToInt((ringOuter - ringInner) / spacing) + 1);
                    for (int ring = 0; ring < rings; ring++)
                    {
                        float t = rings <= 1 ? 0.5f : ring / (float)(rings - 1);
                        float ringRadius = Mathf.Lerp(ringInner, ringOuter, t);
                        if (ringRadius < innerClear) continue;

                        int steps = Mathf.Max(4, Mathf.RoundToInt(2f * Mathf.PI * ringRadius / spacing));
                        // 相邻环错开半个步进,避免排成放射状栅格
                        float phase = (ring % 2 == 0) ? 0f : Mathf.PI / steps;

                        for (int s = 0; s < steps; s++)
                        {
                            float ang = phase + 2f * Mathf.PI * s / steps
                                        + (float)(rng.NextDouble() - 0.5) * (2f * Mathf.PI / steps) * 0.5f;

                            var pos = new Vector3(
                                zone.position.x + Mathf.Cos(ang) * ringRadius, 0f,
                                zone.position.z + Mathf.Sin(ang) * ringRadius);

                            candidates.Add((pos, ring, s));
                        }
                    }

                    if (candidates.Count == 0) continue;

                    int want = rule.count > 0 ? Mathf.Min(rule.count, candidates.Count) : candidates.Count;
                    int placedForRule = 0;

                    for (int k = 0; k < want; k++)
                    {
                        // 均匀跨步取样,保证抽稀后仍然分布在整个环带上而不是只填内圈
                        int idx = want == 1 ? 0 : Mathf.RoundToInt(k * (candidates.Count - 1) / (float)(want - 1));
                        var (pos, ring, step) = candidates[idx];

                        if (!PassesTerrainFilter(config, rule, pos)) continue;

                        var go = Spawn(root, rule, pos, rng);
                        if (go == null) continue;

                        go.name = $"Zone_{zone.letter}_{zoneIndex}_{rule.name}_{ring}_{step}";
                        total++;
                        placedForRule++;
                    }

                    if (placedForRule < want)
                        Debug.LogWarning($"[MapGen] 据点 {zone.letter} 的 {rule.name}: " +
                                         $"计划 {want} 个,实际放下 {placedForRule} 个(坡度/高度带过滤)。");
                }
            }

            Debug.Log($"[MapGen] 据点/安全区装饰:{total} 个{(removed > 0 ? $"(清理旧实例 {removed})" : "")}");
            return total;
        }

        // ==================================================================
        // 掩体场
        // ==================================================================

        /// <summary>
        /// 战斗掩体场。半身/全身/大型三类按比例分配,据点周围自动净空,
        /// 落点必须坡度足够平缓。根节点挂 <see cref="CoverRegistrar"/> 供 AI 查询。
        /// </summary>
        public static int PlaceCoverFields(MapGenConfig config, IList<MapAnchor> anchors)
        {
            GameObject root = ResetRoot(CoversRootName, out int removed);
            if (root == null) return 0;

            root.AddComponent<CoverRegistrar>();

            bool havePrefabs = config.coverLowPrefabs.Length > 0 ||
                               config.coverTallPrefabs.Length > 0 ||
                               config.coverBlockPrefabs.Length > 0;

            var rng = new System.Random(config.coverSeed);
            var placed = new List<Vector2>();

            float halfX = Mathf.Max(10f, config.PlayHalfX - 8f);
            float halfZ = Mathf.Max(10f, config.PlayHalfZ - 8f);

            int made = 0, guard = 0, attempts = config.coverCount * 60;
            while (made < config.coverCount && guard++ < attempts)
            {
                var p = new Vector2(
                    ((float)rng.NextDouble() * 2f - 1f) * halfX,
                    ((float)rng.NextDouble() * 2f - 1f) * halfZ);

                if (!ClearOfZones(p, anchors, config.coverZoneClearance)) continue;

                bool tooClose = false;
                for (int i = 0; i < placed.Count; i++)
                    if (Vector2.Distance(p, placed[i]) < config.coverMinGap) { tooClose = true; break; }
                if (tooClose) continue;

                var world = new Vector3(p.x, 0f, p.y);
                if (MapTerrainUtil.SampleSlope(world) > config.coverSlopeMaxDeg) continue;

                placed.Add(p);

                int roll = rng.Next(100);
                GameObject cover = SpawnCover(config, roll, out string kind);
                if (cover == null) continue;

                cover.transform.SetParent(root.transform, true);
                // 先落到目标 XZ(高度先给 0),再摆朝向,最后贴地。
                // 漏掉这一步会让所有掩体堆在世界原点。
                cover.transform.position = new Vector3(p.x, 0f, p.y);
                cover.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                cover.name = $"Cover_{kind}_{made}";

                MapTerrainUtil.AlignBottomToGround(cover, MapTerrainUtil.SampleHeight(world));
                made++;
            }

            Debug.Log($"[MapGen] 掩体:{made} 个(seed {config.coverSeed},目标 {config.coverCount}," +
                      $"预制体 {(havePrefabs ? "素材包" : "回退方块")}){(removed > 0 ? $"(清理旧实例 {removed})" : "")}");
            return made;
        }

        private static GameObject SpawnCover(MapGenConfig config, int roll, out string kind)
        {
            GameObject[] pool;
            if (roll < 34) { pool = config.coverLowPrefabs; kind = "Low"; }
            else if (roll < 68) { pool = config.coverTallPrefabs; kind = "Tall"; }
            else { pool = config.coverBlockPrefabs; kind = "Block"; }

            if (pool != null && pool.Length > 0)
            {
                var valid = new List<GameObject>();
                foreach (GameObject p in pool) if (p != null) valid.Add(p);
                if (valid.Count > 0)
                {
                    GameObject prefab = valid[UnityEngine.Random.Range(0, valid.Count)];
                    var inst = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
                    return inst != null ? inst : UnityEngine.Object.Instantiate(prefab);
                }
            }

            // 回退:与既有 MatchSceneBuilder 相同的方块约定(素材未配置时仍能出图)
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            switch (kind)
            {
                case "Low":
                    go.transform.localScale = new Vector3(2f, 1.2f, 0.8f);
                    break;
                case "Tall":
                    go.transform.localScale = new Vector3(2f, 1.8f, 0.6f);
                    break;
                default:
                    go.transform.localScale = new Vector3(3.5f, 2.6f, 3.5f);
                    break;
            }
            return go;
        }

        // ==================================================================
        // 散布内核
        // ==================================================================

        /// <summary>
        /// 抖动网格散布:格边长取规则间距,每格内随机取一点,再做坡度/高程/聚簇筛选。
        /// 比纯随机更均匀,同时保持确定性。
        /// </summary>
        private static int Scatter(GameObject root, MapGenConfig config, MapDecorRule rule,
            IList<MapAnchor> anchors, int wanted, System.Random rng)
        {
            float halfX = Mathf.Max(5f, config.HalfX - 5f);
            float halfZ = Mathf.Max(5f, config.HalfZ - 5f);

            // 目标数量 → 每格取点概率;格子面积 * 密度 ≈ 期望数量
            float cell = Mathf.Max(2f, rule.minSpacing);
            int cellsX = Mathf.Max(1, Mathf.FloorToInt(halfX * 2f / cell));
            int cellsZ = Mathf.Max(1, Mathf.FloorToInt(halfZ * 2f / cell));
            int cells = cellsX * cellsZ;

            if (cells <= 0) return 0;
            float acceptChance = Mathf.Clamp01(wanted / (float)cells);

            int made = 0;
            for (int cz = 0; cz < cellsZ; cz++)
                for (int cx = 0; cx < cellsX; cx++)
                {
                    if (made >= wanted) return made;
                    if (rng.NextDouble() > acceptChance) continue;

                    float x = -halfX + (cx + (float)rng.NextDouble()) * cell;
                    float z = -halfZ + (cz + (float)rng.NextDouble()) * cell;
                    var pos = new Vector3(x, 0f, z);

                    if (!PassesTerrainFilter(config, rule, pos)) continue;

                    if (rule.clusterScale > 0.01f)
                    {
                        float n = ClusterNoise(x, z, rule.clusterScale, config.seed);
                        if (n < rule.clusterThreshold) continue;
                    }

                    if (rule.avoidZones && !ClearOfZones(new Vector2(x, z), anchors, 1f)) continue;

                    var go = Spawn(root, rule, pos, rng);
                    if (go == null) continue;

                    go.name = $"Decor_{rule.name}_{cx}_{cz}";
                    made++;
                }

            return made;
        }

        private static GameObject Spawn(GameObject root, MapDecorRule rule, Vector3 pos,
            System.Random rng)
        {
            if (rule.prefabs == null || rule.prefabs.Length == 0) return null;

            // 只从非空槽里选,避免配置里留了空位就整条规则失效
            var valid = new List<int>();
            for (int i = 0; i < rule.prefabs.Length; i++)
                if (rule.prefabs[i] != null) valid.Add(i);
            if (valid.Count == 0) return null;

            GameObject prefab = rule.prefabs[valid[rng.Next(valid.Count)]];

            var go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            if (go == null) go = UnityEngine.Object.Instantiate(prefab);
            if (go == null) return null;

            go.transform.SetParent(root.transform, true);

            // 先落到目标 XZ(高度先给 0),再摆朝向,最后贴地。
            // 漏掉这一步会让所有实例堆在世界原点。
            go.transform.position = new Vector3(pos.x, 0f, pos.z);

            float scale = rule.scaleRange.x >= rule.scaleRange.y
                ? rule.scaleRange.x
                : Mathf.Lerp(rule.scaleRange.x, rule.scaleRange.y, (float)rng.NextDouble());
            go.transform.localScale = Vector3.one * scale;

            // 先定朝向再贴地:包围盒底边取决于旋转,顺序反了会算错
            if (rule.alignToSlope)
                go.transform.rotation = AlignToNormal(pos, (float)rng.NextDouble() * 360f);
            else if (rule.randomYaw)
                go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

            MapTerrainUtil.AlignBottomToGround(go, MapTerrainUtil.SampleHeight(pos));

            if (rule.stripColliders) MapTerrainUtil.StripColliders(go);
            return go;
        }

        /// <summary>坡度 / 高程带 / 净空筛选。</summary>
        private static bool PassesTerrainFilter(MapGenConfig config, MapDecorRule rule, Vector3 pos)
        {
            if (MapTerrainUtil.SampleSlope(pos) > rule.slopeMaxDeg) return false;

            float h = MapTerrainUtil.SampleNormalizedHeight(pos);
            if (h < rule.heightBand.x || h > rule.heightBand.y) return false;

            return true;
        }

        private static Quaternion AlignToNormal(Vector3 pos, float yaw)
        {
            Terrain t = MapTerrainUtil.FindTerrain(pos);
            Vector3 up = t != null ? t.terrainData.GetInterpolatedNormal(
                Mathf.Clamp01((pos.x - t.transform.position.x) / Mathf.Max(0.001f, t.terrainData.size.x)),
                Mathf.Clamp01((pos.z - t.transform.position.z) / Mathf.Max(0.001f, t.terrainData.size.z)))
                : Vector3.up;

            return Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.FromToRotation(Vector3.up, up);
        }

        // ==================================================================
        // 辅助
        // ==================================================================

        private static bool IsUsable(MapDecorRule rule)
        {
            if (rule == null || rule.prefabs == null || rule.prefabs.Length == 0) return false;

            foreach (GameObject p in rule.prefabs) if (p != null) return true;
            return false;
        }

        /// <summary>数量换算:密度按有效战斗区面积折算,否则用固定数量。</summary>
        private static int ResolveCount(MapGenConfig config, MapDecorRule rule)
        {
            if (rule.densityPer1000m2 > 0f)
            {
                float area = 4f * config.PlayHalfX * config.PlayHalfZ;
                return Mathf.Max(0, Mathf.RoundToInt(rule.densityPer1000m2 * area / 1000f));
            }
            return Mathf.Max(0, rule.count);
        }

        /// <summary>判断某点是否离所有据点/安全区足够远(倍率乘锚点半径)。</summary>
        private static bool ClearOfZones(Vector2 p, IList<MapAnchor> anchors, float multiplier)
        {
            if (anchors == null) return true;
            for (int i = 0; i < anchors.Count; i++)
            {
                MapAnchor a = anchors[i];
                if (a == null) continue;
                if (a.role != AnchorRole.CapturePoint &&
                    a.role != AnchorRole.GarrisonRed &&
                    a.role != AnchorRole.GarrisonBlue) continue;

                float clearance = Mathf.Max(a.radius, 5f) * Mathf.Max(1f, multiplier);
                var c = new Vector2(a.position.x, a.position.z);
                if (Vector2.Distance(p, c) < clearance) return false;
            }
            return true;
        }

        private static GameObject ResetRoot(string name, out int removed)
        {
            removed = 0;
            var existing = GameObject.Find(name);
            if (existing != null)
            {
                removed = existing.transform.childCount;
                UnityEngine.Object.DestroyImmediate(existing);
            }

            var root = new GameObject(name);
            return root;
        }

        private static int HashSeed(int seed, int a, int salt)
        {
            unchecked
            {
                int h = seed;
                h = h * 31 + a;
                h = h * 31 + salt;
                return h;
            }
        }

        private static float ClusterNoise(float x, float z, float scale, int seed)
        {
            float s = Mathf.Max(1f, scale);
            return FractalValue(x / s, z / s, seed);
        }

        private static float FractalValue(float x, float z, int seed)
        {
            float sum = 0f, amp = 1f, norm = 0f, freq = 1f;
            for (int o = 0; o < 3; o++)
            {
                sum += Value(x * freq, z * freq, seed + o * 7919) * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2f;
            }
            return norm > 0f ? sum / norm : 0.5f;
        }

        private static float Value(float x, float z, int seed)
        {
            int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fz = z - z0;
            float ux = fx * fx * (3f - 2f * fx);
            float uz = fz * fz * (3f - 2f * fz);

            float a = Mathf.Lerp(Hash(x0, z0, seed), Hash(x0 + 1, z0, seed), ux);
            float b = Mathf.Lerp(Hash(x0, z0 + 1, seed), Hash(x0 + 1, z0 + 1, seed), ux);
            return Mathf.Lerp(a, b, uz);
        }

        private static float Hash(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) + (uint)(z * 668265263) + (uint)seed * 2654435761u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFFu) / (float)0xFFFFFF;
            }
        }
    }
}
