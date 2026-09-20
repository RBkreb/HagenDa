using System.Collections.Generic;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// 对局装配中间层(MATCH-LAYER):在 Project 中选中一份 <see cref="MatchConfig"/>
    /// 资产,菜单一键把完整对局装配到场景(幂等,可重跑)。
    ///
    ///  - <see cref="MapKind.Procedural"/>:新建空场景,程序化生成地板/围墙/种子掩体;
    ///  - <see cref="MapKind.SceneReference"/>(FBX):要求地图根已导入当前场景,
    ///    归层(Ground/ceiling)后构建进当前场景。
    ///
    /// 预制/装备/导航等复用 <see cref="NetworkSetup"/> 的 internal builder。
    /// </summary>
    public static class MatchSceneBuilder
    {
        private const string EmpFieldPrefabPath = "Assets/Game/Prefabs/EmpField.prefab";

        [MenuItem("HagenDa/Match/Build From Selected MatchConfig")]
        public static void BuildFromSelection()
        {
            Build(Selection.activeObject as MatchConfig);
        }

        [MenuItem("HagenDa/Match/Build From Selected MatchConfig", true)]
        public static bool ValidateBuildFromSelection() => Selection.activeObject is MatchConfig;

        public static void Build(MatchConfig config)
        {
            if (config == null)
            {
                Debug.LogError("[MatchBuilder] 请先在 Project 中选中一份 MatchConfig 资产。");
                return;
            }

            var map = config.map;
            if (map == null)
            {
                Debug.LogError($"[MatchBuilder] MatchConfig '{config.name}' 未引用 MapDefinition。");
                return;
            }

            bool procedural = map.kind == MapKind.Procedural;
            bool mapMagic = map.kind == MapKind.MapMagic;
            var active = SceneManager.GetActiveScene();
            if (active.isDirty)
            {
                Debug.LogError("[MatchBuilder] 当前场景有未保存改动,请先保存(SceneReference 构建会写入当前场景)。");
                return;
            }

            // ---- 配置快照为纯托管局部 ----
            // NewScene(Single) 会触发 UnloadUnusedAssets:仅被脚本局部引用的
            // SO 资产(MatchConfig/MapDefinition)可能被卸载销毁。因此所有
            // 需要跨越 NewScene 的数据先复制为值/纯托管对象,NewScene 之后
            // 不再访问任何 SO。
            string cfgName = config.name;
            var brain = config.brain;
            int winScore = config.winScore;
            int squadsPerTeam = config.squadsPerTeam;
            int squadSize = config.squadSize;
            float redeployDelay = config.redeployDelay;
            float autoDeployTimeout = config.autoDeployTimeout;
            int redAiCount = config.redAiCount;
            int blueAiCount = config.blueAiCount;
            bool mixed = config.mixedClassComposition;
            var aiClassOverride = config.aiClassOverride;
            var humanTeamPolicy = config.humanTeamPolicy;
            int redHumanSlots = config.redHumanSlots;
            int blueHumanSlots = config.blueHumanSlots;
            var commander = config.commander;
            bool spawnStatsHud = config.spawnFsmStatsHud;
            bool spawnFree = config.spawnFreeCamera;
            bool spawnBattle = config.spawnBattleCamera;
            int humanSlots = Mathf.Max(0, redHumanSlots) + Mathf.Max(0, blueHumanSlots);

            float sizeX = map.sizeX, sizeZ = map.sizeZ, wallHeight = map.wallHeight;
            int coverCount = map.coverCount, coverSeed = map.coverSeed;
            float coverMinGap = map.coverMinGap;
            // MapMagic 地图的锚点是权威来源:地形平台就是按它们压平的,
            // 若改用 MapDefinition.anchors 会与地形对不上。
            var resolved = ResolveAnchors(map);   // 纯托管 MapAnchor 克隆

            string scenePath;
            Scene scene;
            if (procedural)
            {
                NetworkSetup.EnsureFolder("Assets", "Scenes");
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                scenePath = "Assets/Game/Scenes/" + cfgName + ".scene";
                NetworkSetup.EnsureLighting();
            }
            else
            {
                if (string.IsNullOrEmpty(active.path))
                {
                    Debug.LogError("[MatchBuilder] 当前场景未保存,无法作为 SceneReference 构建目标。");
                    return;
                }

                if (mapMagic)
                {
                    // MapMagic 地图:地形已由 HagenDa/MapMagic 流水线烘焙进本场景,
                    // 这里只补对局实体,所以必须先在正确的场景里。
                    if (map.mapGen == null)
                    {
                        Debug.LogError($"[MatchBuilder] MapDefinition '{map.name}' 是 MapMagic 类型" +
                                       "但没有引用 MapGenConfig。");
                        return;
                    }
                    if (GameObject.Find(MapMagicGraphBuilder.MapMagicObjectName) == null)
                    {
                        Debug.LogError("[MatchBuilder] 当前场景找不到 MapMagic 地形。" +
                                       "请先在该场景执行 HagenDa/MapMagic/Build。");
                        return;
                    }
                }
                else
                {
                    foreach (var root in map.mapRootNames)
                    {
                        if (string.IsNullOrEmpty(root)) continue;
                        if (GameObject.Find(root) == null)
                        {
                            Debug.LogError($"[MatchBuilder] 当前场景找不到地图根 '{root}',请先导入地图。");
                            return;
                        }
                    }
                }

                scene = active;
                scenePath = active.path;
            }

            NetworkSetup.EnsureFolder("Assets/Game", "Prefabs");
            NetworkSetup.EnsureFolder("Assets/Game", "Equipment");
            NetworkSetup.EnsureMapLayers();
            NetworkSetup.EnsureLayerNamed(MapLayers.GroundName);
            NetworkSetup.EnsureLayerNamed(MapLayers.CeilingName);

            // 预制与装备资产(全部幂等)。
            List<EquipmentDefinition> equipmentList = NetworkSetup.BuildEquipmentAssets();
            GameObject grenadePrefab = NetworkSetup.BuildGrenadePrefab();
            GameObject smokePrefab = NetworkSetup.BuildSmokePrefab();
            GameObject rescuePrefab = NetworkSetup.BuildRescuePrefab();
            GameObject bulletPrefab = NetworkSetup.BuildBulletPrefab();
            GameObject playerPrefab = NetworkSetup.BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
            GameObject aiPrefab = NetworkSetup.BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
            GameObject fsmPrefab = NetworkSetup.BuildFSMAIPrefab(aiPrefab);

            // 幂等清理(Procedural 每次全新场景,无需清理)。
            // MapMagic 场景要保住流水线生成的掩体场与装饰,只清对局实体。
            if (!procedural)
            {
                if (mapMagic) NetworkSetup.ClearOld("FreeCamera", "BattleCamera", "PlayerStart");
                else ClearMatchObjects();
                NetworkSetup.ClearAllFSMEntities();
            }

            // 锚点已解析(快照阶段);校验必需锚点。
            if (!TryGetAnchor(resolved, AnchorRole.GarrisonRed, out var redGr) ||
                !TryGetAnchor(resolved, AnchorRole.GarrisonBlue, out var blueGr))
            {
                Debug.LogError("[MatchBuilder] MapDefinition 缺少 GarrisonRed / GarrisonBlue 锚点。");
                return;
            }

            // 应用地图几何 / 归层。
            if (procedural)
                BuildProceduralGeometry(sizeX, sizeZ, wallHeight, coverCount, coverSeed, coverMinGap, resolved);
            else if (!mapMagic)
                ApplySceneReferenceLayers(map);

            // 地图 AABB(相机 / LLM 指挥官 overlay 用)。
            Bounds mapBounds;
            if (procedural)
                mapBounds = new Bounds(Vector3.zero, new Vector3(sizeX, 0f, sizeZ));
            else if (mapMagic)
                mapBounds = new Bounds(Vector3.zero,
                    new Vector3(map.mapGen.sizeX, map.mapGen.heightMax, map.mapGen.sizeZ));
            else if (!MapLayers.TryGetMapBounds(out mapBounds))
                mapBounds = new Bounds(Vector3.zero, new Vector3(sizeX, 0f, sizeZ));

            // ---- 锚点 → 场景实体 ----
            GarrisonZone redZone = CreateZoneFromAnchor(redGr, "RedGR", (int)MatchTeam.Red);
            GarrisonZone blueZone = CreateZoneFromAnchor(blueGr, "BlueGR", (int)MatchTeam.Blue);
            var garrisons = new List<GarrisonZone> { redZone, blueZone };

            var capturePoints = new List<CapturePoint>();
            int letterIdx = 0;
            foreach (var a in resolved)
            {
                if (a.role != AnchorRole.CapturePoint) continue;
                string letter = string.IsNullOrEmpty(a.letter)
                    ? ((char)('A' + letterIdx)).ToString() : a.letter;
                var cp = NetworkSetup.CreateCapturePoint("Zone_" + letter, a.position, a.radius);
                cp.letter = letter;
                ApplyExtent(cp, a);
                if (a.deployPointCount > 0)
                    NetworkSetup.AttachDeployPointTemplate(cp.gameObject, a.deployPointCount, RectRadius(a) * 0.8f);
                ConfigureMovement(cp.gameObject, a);
                NetworkSetup.CreateStrategicZone("StrategicZone_" + letter, cp);
                capturePoints.Add(cp);
                letterIdx++;
            }

            // ---- 对局系统 ----
            var mmGo = new GameObject("NetworkMatchManager");
            var mm = mmGo.AddComponent<NetworkMatchManager>();
            mm.winScore = winScore;
            mm.squadsPerTeam = squadsPerTeam;
            mm.squadSize = squadSize;
            mm.redeployDelay = redeployDelay;
            mm.autoDeployTimeout = autoDeployTimeout;
            mm.garrisons = garrisons;
            mm.capturePoints = capturePoints;
            mm.humanTeamPolicy = humanTeamPolicy;
            mm.redHumanSlots = redHumanSlots;
            mm.blueHumanSlots = blueHumanSlots;

            new GameObject("StrategicZoneRegistry").AddComponent<StrategicZoneRegistry>();

            var systemGo = new GameObject("FSMBattleSystem");
            systemGo.AddComponent<FSMBattleSystem>();
            if (spawnStatsHud) systemGo.AddComponent<FSMStatsHud>();

            if (commander != CommanderMode.None)
                new GameObject("SquadCommander").AddComponent<SquadCommander>();

            // ---- NetworkManager ----
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            nm.playerPrefab = playerPrefab;
            nm.autoCreatePlayer = humanSlots > 0;
            nm.transport = nmGo.AddComponent<kcp2k.KcpTransport>();
            nmGo.AddComponent<TrainingAutoHost>();
            NetworkSetup.RegisterSpawnPrefabs(grenadePrefab, smokePrefab, rescuePrefab, aiPrefab, fsmPrefab);
            foreach (var def in equipmentList)
                if (def != null && def.throwablePrefab != null)
                    NetworkSetup.RegisterSpawnPrefabs(def.throwablePrefab);
            var empField = AssetDatabase.LoadAssetAtPath<GameObject>(EmpFieldPrefabPath);
            if (empField != null) NetworkSetup.RegisterSpawnPrefabs(empField);

            // ---- 真人出生点(部署前的初始落点)+ UI ----
            if (humanSlots > 0)
            {
                CreateHumanSpawnPositions(humanTeamPolicy, redHumanSlots, blueHumanSlots, redGr, blueGr);
                NetworkSetup.EnsureEventSystem();
            }

            // ---- 相机 ----
            if (spawnFree) CreateFreeCamera(mapBounds);
            if (spawnBattle) CreateBattleCamera(mapBounds);

            // ---- LLM 指挥官(依赖场景对象,最后装配) ----
            if (commander == CommanderMode.LlmCommander)
                CommanderSetup.Setup(mapBounds);   // PHASE11：HexGrid 边界用构建器精确 AABB

            // ---- NavMesh(必须先于 AI 生成) ----
            if (procedural) NetworkSetup.BuildNavMeshForFloor();
            else if (mapMagic) { /* NavMesh 已由 HagenDa/MapMagic 流水线烘好 */ }
            else if (map.mapRootNames.Length > 0)
                NetworkSetup.BuildNavMeshForMapRoot(map.mapRootNames[0]);

            // ---- AI 生成 ----
            SpawnFsmTeam(fsmPrefab, (int)MatchTeam.Red, redAiCount, redGr.position,
                redGr.position.y + 1.5f, mixed, aiClassOverride);
            SpawnFsmTeam(fsmPrefab, (int)MatchTeam.Blue, blueAiCount, blueGr.position,
                blueGr.position.y + 1.5f, mixed, aiClassOverride);

            // ---- 保存(先资产后场景:SaveAssets 可能标脏场景,场景保存必须最后) ----
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"[MatchBuilder] Done. {scenePath} " +
                      $"(AI {redAiCount}v{blueAiCount}, humanSlots {humanSlots}, brain {brain}).");
        }

        // ---------------------------------------------------------------
        // 锚点解析
        // ---------------------------------------------------------------

        /// <summary>按 AnchorResolve 模式解析全部锚点为世界坐标副本。</summary>
        private static List<MapAnchor> ResolveAnchors(MapDefinition map)
        {
            // MapMagic 地图:锚点存在 MapGenConfig 里(地形平台按它们压平)。
            IList<MapAnchor> source = map.kind == MapKind.MapMagic && map.mapGen != null
                ? (IList<MapAnchor>)map.mapGen.anchors
                : map.anchors;

            var list = new List<MapAnchor>();
            foreach (var a in source)
            {
                var copy = new MapAnchor
                {
                    role = a.role,
                    letter = a.letter,
                    markerName = a.markerName,
                    position = a.position,
                    radius = a.radius,
                    extent = a.extent,
                    deployPointCount = a.deployPointCount,
                    moving = a.moving,
                    waypoints = a.waypoints != null ? (Vector3[])a.waypoints.Clone() : new Vector3[0],
                    moveSpeed = a.moveSpeed,
                    dwellSeconds = a.dwellSeconds,
                    pingPong = a.pingPong,
                    startDelay = a.startDelay
                };

                Transform marker = null;
                if (map.anchorResolve == AnchorResolve.BySceneMarker &&
                    !string.IsNullOrEmpty(a.markerName))
                {
                    marker = FindMarker(a.markerName);
                    if (marker != null) copy.position = marker.position;
                    else Debug.LogWarning($"[MatchBuilder] 锚点标记 '{a.markerName}' 未找到,回退资产坐标。");
                }

                // 移动航点回退:显式坐标 > 标记物 WP* 子物体(FBX 地图摆点工作流)。
                if (copy.moving && (copy.waypoints == null || copy.waypoints.Length < 2))
                {
                    copy.waypoints = marker != null
                        ? CollectWaypointChildren(marker, copy.position)
                        : new Vector3[0];
                    if (copy.waypoints.Length < 2)
                        Debug.LogWarning($"[MatchBuilder] 移动锚点 '{a.markerName}' 航点不足(<2),该区域不会移动。");
                }
                list.Add(copy);
            }
            return list;
        }

        /// <summary>收集标记物下名为 WP* 的子物体位置(按名排序),起点回退标记物自身。</summary>
        private static Vector3[] CollectWaypointChildren(Transform marker, Vector3 fallbackStart)
        {
            var found = new List<Transform>();
            foreach (Transform t in marker)
                if (t.name.StartsWith("WP", System.StringComparison.OrdinalIgnoreCase))
                    found.Add(t);
            found.Sort((x, y) => string.CompareOrdinal(x.name, y.name));

            var result = new List<Vector3>();
            if (found.Count == 0) result.Add(fallbackStart);   // 起点自身
            else result.Add(marker.position);
            foreach (var t in found) result.Add(t.position);
            return result.ToArray();
        }

        private static Transform FindMarker(string name)
        {
            var go = GameObject.Find(name);
            if (go != null) return go.transform;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static bool TryGetAnchor(List<MapAnchor> list, AnchorRole role, out MapAnchor found)
        {
            foreach (var a in list)
            {
                if (a.role != role) continue;
                found = a;
                return true;
            }
            found = null;
            return false;
        }

        /// <summary>锚点有效判定半径:矩形取长边,圆形取 radius。</summary>
        private static float RectRadius(MapAnchor a)
        {
            return a.extent.x > 0f && a.extent.y > 0f
                ? Mathf.Max(a.extent.x, a.extent.y) : a.radius;
        }

        private static void ApplyExtent(GarrisonZone gz, MapAnchor a)
        {
            if (a.extent.x > 0f && a.extent.y > 0f) gz.extent = a.extent;
        }

        private static void ApplyExtent(CapturePoint cp, MapAnchor a)
        {
            if (a.extent.x > 0f && a.extent.y > 0f) cp.extent = a.extent;
        }

        private static GarrisonZone CreateZoneFromAnchor(MapAnchor a, string name, int teamId)
        {
            var gz = NetworkSetup.CreateGarrison(name, a.position, teamId, a.radius);
            ApplyExtent(gz, a);
            if (a.deployPointCount > 0)
                NetworkSetup.AttachDeployPointTemplate(gz.gameObject, a.deployPointCount, RectRadius(a) * 0.8f);
            ConfigureMovement(gz.gameObject, a);
            return gz;
        }

        /// <summary>移动区域装配:MovingZone(服务端驱动)+ NetworkTransformReliable(位置下行同步)。</summary>
        private static void ConfigureMovement(GameObject zoneGo, MapAnchor a)
        {
            if (!a.moving) return;
            if (a.waypoints == null || a.waypoints.Length < 2) return;

            var mover = zoneGo.AddComponent<MovingZone>();
            mover.waypoints = new List<Vector3>(a.waypoints);
            mover.moveSpeed = a.moveSpeed;
            mover.dwellSeconds = a.dwellSeconds;
            mover.pingPong = a.pingPong;
            mover.startDelay = a.startDelay;

            NetworkSetup.AddNetworkTransform(zoneGo, SyncDirection.ServerToClient, syncRotation: false);
        }

        /// <summary>SceneReference 重建前清理旧对局对象(据点字母可能自定义,按组件清)。</summary>
        private static void ClearMatchObjects()
        {
            var types = new[]
            {
                typeof(NetworkMatchManager), typeof(StrategicZoneRegistry),
                typeof(GarrisonZone), typeof(CapturePoint), typeof(StrategicZone),
                typeof(FSMBattleSystem), typeof(FSMStatsHud), typeof(SquadCommander),
                typeof(NetworkManager)
            };
            foreach (var t in types)
                foreach (var c in Object.FindObjectsOfType(t, true))
                    Object.DestroyImmediate(((Component)c).gameObject);

            NetworkSetup.ClearOld("FreeCamera", "BattleCamera", "Covers", "PlayerStart");
        }

        // ---------------------------------------------------------------
        // 地图应用
        // ---------------------------------------------------------------

        /// <summary>Procedural:地板 + 四面围墙 + 种子化掩体场(存入场景,无需运行时重建)。</summary>
        private static void BuildProceduralGeometry(float sizeX, float sizeZ, float wallHeight,
            int coverCount, int coverSeed, float coverMinGap, List<MapAnchor> anchors)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(sizeX / 10f, 1f, sizeZ / 10f);

            float wallH = wallHeight;
            NetworkSetup.CreateWall(new Vector3(0f, wallH * 0.5f, sizeZ * 0.5f), new Vector3(sizeX, wallH, 1f));
            NetworkSetup.CreateWall(new Vector3(0f, wallH * 0.5f, -sizeZ * 0.5f), new Vector3(sizeX, wallH, 1f));
            NetworkSetup.CreateWall(new Vector3(-sizeX * 0.5f, wallH * 0.5f, 0f), new Vector3(1f, wallH, sizeZ));
            NetworkSetup.CreateWall(new Vector3(sizeX * 0.5f, wallH * 0.5f, 0f), new Vector3(1f, wallH, sizeZ));

            BuildCoverField(sizeX, sizeZ, coverCount, coverSeed, coverMinGap, anchors);
        }

        /// <summary>种子化掩体场:种类比例沿 TrainingMap 约定,锚点周围自动净空。</summary>
        private static void BuildCoverField(float sizeX, float sizeZ,
            int coverCount, int coverSeed, float coverMinGap, List<MapAnchor> anchors)
        {
            var root = new GameObject("Covers");
            root.AddComponent<CoverRegistrar>();

            var rng = new System.Random(coverSeed);
            var placed = new List<Vector2>();
            int made = 0;
            int guard = 0;
            while (made < coverCount && guard++ < coverCount * 40)
            {
                float x = ((float)rng.NextDouble() * 2f - 1f) * (sizeX * 0.5f - 6f);
                float z = ((float)rng.NextDouble() * 2f - 1f) * (sizeZ * 0.5f - 12f);
                var p = new Vector2(x, z);

                bool ok = true;
                foreach (var a in anchors)
                {
                    if (a.role == AnchorRole.PlayerSpawn) continue;
                    if (Vector2.Distance(p, new Vector2(a.position.x, a.position.z)) < RectRadius(a) + 2f)
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    foreach (var q in placed)
                        if (Vector2.Distance(p, q) < coverMinGap) { ok = false; break; }
                if (!ok) continue;

                placed.Add(p);

                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.SetParent(root.transform, false);
                go.transform.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

                int roll = rng.Next(100);
                if (roll < 35)
                {
                    go.name = "Cover_Tall";
                    go.transform.localScale = new Vector3(2f, 2.2f, 0.6f);
                    go.transform.localPosition = new Vector3(p.x, 1.1f, p.y);
                }
                else if (roll < 70)
                {
                    go.name = "Cover_Low";
                    go.transform.localScale = new Vector3(2f, 1.0f, 0.8f);
                    go.transform.localPosition = new Vector3(p.x, 0.5f, p.y);
                }
                else if (roll < 88)
                {
                    go.name = "Cover_High";
                    go.transform.localScale = new Vector3(3f, 1.4f, 3f);
                    go.transform.localPosition = new Vector3(p.x, 0.7f, p.y);
                }
                else
                {
                    go.name = "Cover_Ramp";
                    go.transform.localScale = new Vector3(3f, 1.2f, 6f);
                    go.transform.localPosition = new Vector3(p.x, 0.6f, p.y);
                }
                made++;
            }
            Debug.Log($"[MatchBuilder] Cover field: {made} covers (seed {coverSeed}).");
        }

        /// <summary>SceneReference(FBX):地图根子物体归层 + 碰撞体/双面/背面查询。</summary>
        private static void ApplySceneReferenceLayers(MapDefinition map)
        {
            int ground = LayerMask.NameToLayer(MapLayers.GroundName);
            int ceiling = LayerMask.NameToLayer(MapLayers.CeilingName);
            string kw = string.IsNullOrEmpty(map.ceilingKeyword)
                ? "ceiling" : map.ceilingKeyword.ToLowerInvariant();

            int moved = 0, ceil = 0;
            foreach (var rootName in map.mapRootNames)
            {
                var root = GameObject.Find(rootName);
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == root.transform) continue;
                    if (!string.IsNullOrEmpty(kw) && t.name.ToLowerInvariant().Contains(kw))
                    {
                        if (t.gameObject.layer != ceiling) { t.gameObject.layer = ceiling; ceil++; }
                    }
                    else if (t.gameObject.layer != ground)
                    {
                        t.gameObject.layer = ground;
                        moved++;
                    }
                }
            }
            Debug.Log($"[MatchBuilder] Layers: ground={moved}, ceiling={ceil}.");

            if (map.doubleSidedMaterials) NetworkSetup.EnableMapMaterialsDoubleSided();
            NetworkSetup.EnsureMapColliders();
            NetworkSetup.EnableBackfaceQueries();
        }

        // ---------------------------------------------------------------
        // 真人出生点 / AI 生成 / 相机
        // ---------------------------------------------------------------

        /// <summary>真人部署前的初始 NetworkStartPosition(按策略分布到双方 GR)。</summary>
        private static void CreateHumanSpawnPositions(HumanTeamPolicy policy, int redSlots, int blueSlots,
            MapAnchor redGr, MapAnchor blueGr)
        {
            int redN, blueN;
            switch (policy)
            {
                case HumanTeamPolicy.FixedSlots:
                    redN = Mathf.Max(0, redSlots);
                    blueN = Mathf.Max(0, blueSlots);
                    break;
                case HumanTeamPolicy.Balance:
                    int total = Mathf.Max(1, Mathf.Max(0, redSlots) + Mathf.Max(0, blueSlots));
                    redN = (total + 1) / 2;
                    blueN = total - redN;
                    break;
                default:   // AllRed
                    redN = Mathf.Max(1, redSlots);
                    blueN = 0;
                    break;
            }
            PlaceSpawns("HumanSpawn_R", redN, redGr);
            PlaceSpawns("HumanSpawn_B", blueN, blueGr);
        }

        private static void PlaceSpawns(string prefix, int count, MapAnchor gr)
        {
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject($"{prefix}_{i}");
                Vector2 off = Random.insideUnitCircle * (RectRadius(gr) * 0.5f);
                go.transform.position = gr.position + new Vector3(off.x, 1.2f, off.y);
                go.AddComponent<NetworkStartPosition>();
            }
        }

        /// <summary>锚点周围生成 FSM 队伍:混编 2突击+2支援+1侦察,或全员 override。</summary>
        private static void SpawnFsmTeam(GameObject prefab, int team, int count, Vector3 center,
            float spawnY, bool mixed, FsmClass overrideClass)
        {
            string teamName = team == (int)MatchTeam.Red ? "Red" : "Blue";
            for (int i = 0; i < count; i++)
            {
                int squadIndex = i / 5;
                int inSquad = i % 5;

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = $"FSM_{teamName}_S{squadIndex}_{i}";
                float x = center.x - 10f + inSquad * 4.5f + (squadIndex % 3) * 1.5f;
                float z = center.z - 8f + (squadIndex / 3) * 5f + (squadIndex % 2) * 2f;
                go.transform.position = new Vector3(x, spawnY, z);

                var fsm = go.GetComponent<FSMAIController>();
                if (fsm == null) continue;
                fsm.aiClass = mixed
                    ? (inSquad < 2 ? FsmClass.Assault
                     : inSquad < 4 ? FsmClass.Support
                     : FsmClass.Recon)
                    : overrideClass;
            }
        }

        private static void CreateFreeCamera(Bounds mapBounds)
        {
            var go = new GameObject("FreeCamera");
            go.transform.position = mapBounds.size != Vector3.zero
                ? new Vector3(mapBounds.center.x, 120f, mapBounds.center.z)
                : new Vector3(0f, 120f, -120f);
            var cam = go.AddComponent<Camera>();
            cam.farClipPlane = 1200f;
            cam.depth = 1f;
            go.AddComponent<AudioListener>();
            go.AddComponent<FreeCamera>();
            int zoneLayer = LayerMask.NameToLayer(MapLayers.ZoneName);
            if (zoneLayer >= 0) cam.cullingMask &= ~(1 << zoneLayer);
        }

        private static void CreateBattleCamera(Bounds mapBounds)
        {
            var go = new GameObject("BattleCamera");
            var cam = go.AddComponent<Camera>();
            go.SetActive(false);   // 默认禁用,避免与 FreeCamera 双倍渲染
            if (mapBounds.size != Vector3.zero)
            {
                go.transform.position = new Vector3(
                    mapBounds.center.x,
                    Mathf.Max(mapBounds.size.x, mapBounds.size.z) * 0.8f,
                    mapBounds.center.z - mapBounds.size.z * 0.7f);
                go.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                cam.farClipPlane = mapBounds.size.magnitude * 2f;
            }
            else
            {
                go.transform.position = new Vector3(0f, 300f, -380f);
                go.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                cam.farClipPlane = 1200f;
            }
        }
    }
}
