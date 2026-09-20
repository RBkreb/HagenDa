using System;
using System.Collections.Generic;
using Den.Tools;
using Den.Tools.Matrices;
using Den.Tools.Tasks;
using MapMagic.Core;
using MapMagic.Nodes;
using MapMagic.Nodes.MatrixGenerators;
using MapMagic.Terrains;
using UnityEditor;
using UnityEngine;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// 用脚本搭建 HagenDa 的 MapMagic 图并装配 <see cref="MapMagicObject"/>。
    ///
    /// 图结构(核心版只有 Map/Matrix 模块):
    /// <code>
    ///   Import(设计掩码) ┐
    ///                    ├─ Blend ─ [Erosion] ─ [Blur] ─┬─ HeightOutput
    ///   Noise(细节)      ┘                              └─ 每层坡/高度掩码 ─ TexturesOutput
    /// </code>
    ///
    /// 装饰物**不在此处放置** —— 核心版没有 Objects Output 节点,装饰由
    /// <see cref="MapDecorPlacer"/> 在地形生成完成后按地形高度脚本摆放。
    /// </summary>
    public static class MapMagicGraphBuilder
    {
        /// <summary>MapMagic 对象在场景中的根名称。</summary>
        public const string MapMagicObjectName = "MapMagic";

        /// <summary>
        /// 瓦片网格原点。把 (0,0) 瓦片摆到地图中心,与工程"原点 = 地图中心"的
        /// 世界坐标约定一致(锚点/据点坐标都用这套)。
        /// </summary>
        public static Vector3 TileOrigin(MapGenConfig c) => new Vector3(-c.HalfX, 0f, -c.HalfZ);

        // ==================================================================
        // 场景装配
        // ==================================================================

        /// <summary>
        /// 创建/复用 MapMagic 对象,写好瓦片参数、输出参数与地形设置,并钉住覆盖
        /// 整张地图的瓦片。**不触发生成**,由调用方调 <see cref="RunGenerate"/>。
        /// </summary>
        public static MapMagicObject BuildObject(MapGenConfig config, Graph graph)
        {
            MapMagicObject mm = FindOrCreateObject(config);

            // 先把组件停掉再改配置:MapMagicObject.OnEnable 会立刻调 StartGenerateNonReady,
            // 若此时 graph 还是空的就会抛 "Graph data is not assigned";而且边改参数边生成
            // 也不是我们想要的。全部配置完、瓦片钉好之后再激活。
            mm.gameObject.SetActive(false);

            mm.graph = graph;

            mm.tileSize = new Vector2D(config.sizeX, config.sizeZ);
            mm.tileResolution = ToResolution(config.resolution);
            mm.tileMargins = 16;
            mm.draftsInEditor = false;      // 静态烘焙:不要草稿瓦片
            mm.draftsInPlaymode = false;
            mm.hideFarTerrains = false;     // 关掉流式:地图必须常驻
            mm.instantGenerate = false;     // 生成由菜单显式驱动
            mm.applyColliders = true;

            // 输出参数(存在 MM 对象上,不属于图)
            mm.globals.height = config.heightMax;
            // 高度图分辨率由 tileResolution × 插值决定:Scale2X 会把 1025 的图放大到
            // Unity 上限 2049(quad 密度翻倍,但高度图比 splat 大一倍)。
            // 决策 #1 要的是原生 1025 → 用 None,保持 1:1。
            mm.globals.heightInterpolation = HeightOutput200.Interpolation.None;
            mm.globals.heightMainApply = HeightOutput200.ApplyType.SetHeightsDelayLOD;
            mm.globals.heightSplit = 129;

            // 地形渲染设置
            mm.terrainSettings.material = config.terrainMaterial != null
                ? config.terrainMaterial
                : mm.DefaultTerrainMaterial();
            mm.terrainSettings.allowAutoConnect = true;
            mm.terrainSettings.groupingID = 0;
            mm.terrainSettings.drawInstanced = true;
            mm.terrainSettings.pixelError = 1;
            mm.terrainSettings.showBaseMap = true;
            mm.terrainSettings.baseMapDist = 1500;
            mm.terrainSettings.baseMapResolution = 1024;

            mm.tiles.generateInfinite = false;   // 静态地图:固定瓦片

            PinAllTiles(mm, config);

            mm.gameObject.SetActive(true);
            EditorUtility.SetDirty(mm);
            return mm;
        }

        private static MapMagicObject FindOrCreateObject(MapGenConfig config)
        {
            GameObject existing = GameObject.Find(MapMagicObjectName);
            if (existing != null)
            {
                var found = existing.GetComponent<MapMagicObject>();
                return found != null ? found : existing.AddComponent<MapMagicObject>();
            }

            // **必须**先建成未激活的 GameObject 再挂组件。
            // MapMagicObject 带 [ExecuteInEditMode],AddComponent 会立即触发 OnEnable,
            // 而 OnEnable 里会调 StartGenerateNonReady —— 此时 graph 还是空的,
            // 于是抛 "MapMagic: Graph data is not assigned"。
            // 建好 GameObject、挂好组件、配置完 graph 与瓦片之后再激活,OnEnable 才在就绪状态下跑。
            var go = new GameObject(MapMagicObjectName);
            go.SetActive(false);
            go.transform.position = TileOrigin(config);

            var mm = go.AddComponent<MapMagicObject>();
            return mm;
        }

        /// <summary>把希望的分辨率对齐到 MapMagic 支持的档位(33/65/.../2049)。</summary>
        private static MapMagicObject.Resolution ToResolution(int res)
        {
            int best = 513, bestDelta = int.MaxValue;
            foreach (MapMagicObject.Resolution r in
                     (MapMagicObject.Resolution[])Enum.GetValues(typeof(MapMagicObject.Resolution)))
            {
                int delta = Mathf.Abs((int)r - res);
                if (delta < bestDelta) { bestDelta = delta; best = (int)r; }
            }
            return (MapMagicObject.Resolution)best;
        }

        private static void PinAllTiles(MapMagicObject mm, MapGenConfig config)
        {
            // 尺寸可能变了,先撤掉旧瓦片,避免残留错位的地形
            var old = new List<Coord>(mm.tiles.pinned.Keys);
            foreach (Coord c in old) mm.tiles.Unpin(c);

            int tilesX = Mathf.Max(1, Mathf.RoundToInt(config.sizeX / mm.tileSize.x));
            int tilesZ = Mathf.Max(1, Mathf.RoundToInt(config.sizeZ / mm.tileSize.z));

            for (int z = 0; z < tilesZ; z++)
                for (int x = 0; x < tilesX; x++)
                    mm.tiles.Pin(new Coord(x, z), asDraft: false, holder: mm);

            mm.transform.position = TileOrigin(config);
        }

        // ==================================================================
        // 生成驱动
        // ==================================================================

        /// <summary>
        /// 阻塞式驱动生成直到完成。
        ///
        /// MapMagic 的生成跑在 <see cref="ThreadManager"/> 的工作线程上,而"把结果写进
        /// 地形"这一步走 <see cref="CoroutineManager"/>。这两者平时由 MonoBehaviour 的
        /// Update 抽干 —— 但在阻塞的编辑器菜单调用里 Update 不会跑,所以必须在这里手动 pump。
        /// </summary>
        public static bool RunGenerate(MapMagicObject mm, float timeoutSeconds = 1800f)
        {
            if (mm == null) { Debug.LogError("[MapMagic] MapMagicObject 为空。"); return false; }
            if (mm.graph == null) { Debug.LogError("[MapMagic] MapMagic 对象没有图。"); return false; }

            ThreadManager.useMultithreading = true;

            mm.Refresh(clearAll: true);
            mm.StartGenerate(main: true, draft: false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double nextReport = 0.0;

            while (mm.IsGenerating() || ThreadManager.IsWorking || CoroutineManager.IsWorking)
            {
                CoroutineManager.Update();     // 应用阶段(写地形)在协程里
                ThreadManager.LaunchThreads(); // 兜底再踢一次工作线程

                double t = sw.Elapsed.TotalSeconds;
                if (t > timeoutSeconds)
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogError($"[MapMagic] 生成超时({timeoutSeconds:F0}s)。");
                    return false;
                }

                if (t >= nextReport)
                {
                    nextReport = t + 10.0;
                    float p = SafeProgress(mm);
                    EditorUtility.DisplayProgressBar("MapMagic", $"生成地形… {p * 100f:F0}%  ({t:F0}s)", p);
                }
            }

            EditorUtility.ClearProgressBar();
            Debug.Log($"[MapMagic] 生成完成,耗时 {sw.Elapsed.TotalSeconds:F1}s。");
            return true;
        }

        private static float SafeProgress(MapMagicObject mm)
        {
            try { return Mathf.Clamp01(mm.GetProgress()); }
            catch { return 0f; }
        }

        // ==================================================================
        // 图构建
        // ==================================================================

        /// <summary>重建整张图(幂等:已有图资产会被清空重填)。</summary>
        public static Graph BuildGraph(MapGenConfig config)
        {
            MatrixAsset mask = MapMaskComposer.Compose(config, out Matrix padMask);

            Graph graph = LoadOrCreateGraph(config.graphPath);
            graph.generators = new Generator[0];
            graph.links.Clear();
            graph.groups = new Auxiliary[0];

            // ---------------- 初始:设计掩码 ----------------
            var import = NewGen<Import200>(graph, -560f, 0f);
            import.matrixAsset = mask;
            import.wrapMode = CoordRect.TileMode.Clamp;
            import.scale = 1f;
            import.offset = Vector2.zero;

            Generator tail = import;

            // ---------------- 细节噪声 ----------------
            // Blend 从零矩阵起算,所以 layer[0] 必须用 add/opacity 1 把输入"种"进去,
            // 之后各层才是在此之上叠加。layer[0] 的 opacity 在 GUI 里被当作 base 固定为 1。
            IOutlet<MatrixWorld> flow = import;

            if (config.detailAmplitude > 0.0001f)
            {
                var noise = NewGen<Noise200>(graph, -560f, 240f);
                noise.type = Noise200.Type.Simplex;
                noise.seed = config.seed;
                noise.intensity = 1f;
                noise.size = Mathf.Max(1f, config.detailScale);
                // detail 偏高(fractal bias 大)会让小 fractal 主导,在 0.49 m/像素下
                // 形成大量超短波长起伏 —— 实测会把坡度从掩码的 ~7° 抬到 ~20°,
                // 直接把 1/6 的地图变成不可行走。0.35 让大尺度主导演、细节只做点缀。
                noise.detail = 0.35f;
                noise.turbulence = 0f;

                var blend = NewGen<Blend200>(graph, -240f, 100f);
                SetLayers(blend, new[]
                {
                    new Blend200.Layer { algorithm = Blend200.BlendAlgorithm.add, opacity = 1f },
                    new Blend200.Layer { algorithm = Blend200.BlendAlgorithm.add, opacity = config.detailAmplitude },
                });

                Link(graph, blend.layers[0].inlet, import);
                Link(graph, blend.layers[1].inlet, noise);

                flow = blend;

                // 把平台区域压回掩码高度。细节噪声会把已经压平的据点/安全区重新揉皱
                // (实测平台坡度 12.8°,站不稳也放不了建筑),所以噪声之后要再罩一层
                // Mask:平台内取回掩码原值(平整),平台外保留带细节的地形。
                if (config.flattenZonePads)
                {
                    var padImport = NewGen<Import200>(graph, -560f, -260f);
                    padImport.matrixAsset = LoadOrCreateMatrixAsset(
                        MapMaskComposer.PadMaskPath(config), padMask);
                    padImport.wrapMode = CoordRect.TileMode.Clamp;
                    padImport.scale = 1f;
                    padImport.offset = Vector2.zero;

                    var padRestore = NewGen<Mask200>(graph, 80f, -120f);
                    Link(graph, padRestore.aIn, flow);          // A = 带细节的地形
                    Link(graph, padRestore.bIn, import);        // B = 平整的掩码
                    Link(graph, padRestore.maskIn, padImport);  // Mask = 平台权重

                    flow = padRestore;
                }
            }

            // ---------------- 侵蚀(可选,慢) ----------------
            if (config.erosionIterations > 0)
            {
                var erosion = NewGen<Erosion200>(graph, 80f, 100f);
                erosion.iterations = config.erosionIterations;
                erosion.terrainDurability = 0.9f;
                erosion.sedimentAmount = 0.75f;
                erosion.fluidityIterations = 3;

                Link(graph, erosion, flow);
                flow = erosion;
            }

            // ---------------- 模糊 ----------------
            if (config.blurIterations > 0)
            {
                var blur = NewGen<Blur200>(graph, 360f, 100f);
                blur.downsample = 0f;    // 0 → 走 GaussianBlur 分支
                blur.blur = 4f;

                Link(graph, blur, flow);
                flow = blur;
            }

            // ---------------- 高度输出 ----------------
            var heightOut = NewGen<HeightOutput200>(graph, 700f, -140f);
            heightOut.outputLevel = OutputLevel.Draft | OutputLevel.Main;
            Link(graph, heightOut, flow);

            // ---------------- 地表分层输出 ----------------
            BuildTextureChain(graph, config, flow);

            graph.CheckFixIds();

            EditorUtility.SetDirty(graph);
            AssetDatabase.SaveAssets();
            return graph;
        }

        /// <summary>
        /// 每层一个掩码:坡度区间 × 高度区间。
        ///
        /// layer[0] 由 MapMagic 强制填 1(背景层),所以只需要给 layer[1..] 接掩码。
        /// 掩码本身用 Blend 生成:layer[0] 用 add 种入坡度图,layer[1] 用 multiply 乘高度图。
        /// </summary>
        private static void BuildTextureChain(Graph graph, MapGenConfig config, IOutlet<MatrixWorld> heightTail)
        {
            int count = config.terrainLayers?.Count ?? 0;
            if (count <= 0) return;

            var texOut = NewGen<TexturesOutput200>(graph, 700f, 360f);
            var layers = new TexturesOutput200.TextureLayer[count];
            for (int i = 0; i < count; i++)
                layers[i] = new TexturesOutput200.TextureLayer
                {
                    name = config.terrainLayers[i].name,
                    // 必须给真正的 TerrainLayer 资产:MapMagic 只负责把权重画进
                    // alphamap,没有 prototype 就没有材质,地形会是一片无贴图的灰白。
                    prototype = ResolveTerrainLayer(config, i),
                };
            SetLayers(texOut, layers);

            for (int i = 1; i < count; i++)
            {
                MapLayerRule rule = config.terrainLayers[i];

                var slope = NewGen<Slope200>(graph, 80f, 400f + i * 210f);
                slope.from = rule.slopeRange.x;
                slope.to = rule.slopeRange.y;
                slope.range = rule.slopeSmooth;
                Link(graph, slope, heightTail);

                bool needsHeight = rule.heightRange.x > 0.001f || rule.heightRange.y < 0.999f;

                if (!needsHeight)
                {
                    texOut.layers[i].Opacity = 1f;
                    Link(graph, texOut.layers[i], slope);
                    continue;
                }

                var select = NewGen<Selector200>(graph, 80f, 510f + i * 210f);
                select.units = Selector200.Units.Map;
                select.rangeDet = Selector200.RangeDet.MinMax;
                select.from = new Vector2(rule.heightRange.x, rule.heightRange.x + rule.heightSmooth);
                select.to = new Vector2(rule.heightRange.y - rule.heightSmooth, rule.heightRange.y);
                Link(graph, select, heightTail);

                var mask = NewGen<Blend200>(graph, 400f, 450f + i * 210f);
                SetLayers(mask, new[]
                {
                    new Blend200.Layer { algorithm = Blend200.BlendAlgorithm.add, opacity = 1f },
                    new Blend200.Layer { algorithm = Blend200.BlendAlgorithm.multiply, opacity = 1f },
                });
                Link(graph, mask.layers[0].inlet, slope);
                Link(graph, mask.layers[1].inlet, select);

                texOut.layers[i].Opacity = 1f;
                Link(graph, texOut.layers[i], mask);
            }
        }

        /// <summary>
        /// 取得/生成某条图层规则对应的 <see cref="TerrainLayer"/> 资产。
        ///
        /// 规则里存的是漫反射贴图,但 Terrain 需要的是 TerrainLayer 资产 ——
        /// MapMagic 的 TexturesOutput 只把权重画进 alphamap,prototype 为空时
        /// 地形就是一片无贴图灰白。这里按规则名在项目内落一个资产并复用。
        /// </summary>
        private static TerrainLayer ResolveTerrainLayer(MapGenConfig config, int index)
        {
            MapLayerRule rule = config.terrainLayers[index];
            if (rule.diffuse == null) return null;

            const string folder = "Assets/Game/Materials/TerrainLayers";
            MapMaskComposer.EnsureFolder(folder);

            string safeName = string.IsNullOrEmpty(rule.name) ? $"Layer{index}" : rule.name;
            string path = $"{folder}/{safeName}.terrainlayer";

            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, path);
            }

            layer.diffuseTexture = rule.diffuse;
            layer.tileSize = rule.tileSize;
            layer.tileOffset = Vector2.zero;
            EditorUtility.SetDirty(layer);

            return layer;
        }

        // ==================================================================
        // 连线与图层辅助
        // ==================================================================

        /// <summary>IInlet&lt;out T&gt; / IOutlet&lt;out T&gt; 是协变的,MatrixWorld 端点可直接当 object 端点用。</summary>
        private static void Link(Graph graph, IInlet<MatrixWorld> inlet, IOutlet<MatrixWorld> outlet)
        {
            if (inlet == null || outlet == null) return;
            graph.Link((IInlet<object>)inlet, (IOutlet<object>)outlet);
        }

        /// <summary>
        /// 替换多入参/多图层节点的层数组。**必须重新 SetGen + 生成 Id** ——
        /// Generator.Create 只会给创建时已存在的层分配这些,替换后旧的会丢失。
        /// </summary>
        private static void SetLayers(Blend200 gen, Blend200.Layer[] layers)
        {
            gen.layers = layers;
            foreach (Blend200.Layer layer in layers)
            {
                layer.inlet.SetGen(gen);
                layer.inlet.Id = Den.Tools.Id.Generate();
            }
        }

        private static void SetLayers(TexturesOutput200 gen, TexturesOutput200.TextureLayer[] layers)
        {
            gen.layers = layers;
            foreach (TexturesOutput200.TextureLayer layer in layers)
            {
                layer.SetGen(gen);
                layer.Id = Den.Tools.Id.Generate();
            }
        }

        // ==================================================================
        // 图资产
        // ==================================================================

        private static Graph LoadOrCreateGraph(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Graph>(path);
            if (existing != null) return existing;

            MapMaskComposer.EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
            var graph = ScriptableObject.CreateInstance<Graph>();
            graph.generators = new Generator[0];
            AssetDatabase.CreateAsset(graph, path);
            return graph;
        }

        /// <summary>取得或新建一个 Imported Map 资产(内存里的矩阵直接落盘)。</summary>
        private static MatrixAsset LoadOrCreateMatrixAsset(string path, Matrix matrix)
        {
            var asset = AssetDatabase.LoadAssetAtPath<MatrixAsset>(path);
            if (asset == null)
            {
                MapMaskComposer.EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
                asset = ScriptableObject.CreateInstance<MatrixAsset>();
                AssetDatabase.CreateAsset(asset, path);
            }

            asset.source = MatrixAsset.Source.New;
            asset.matrix = matrix;
            EditorUtility.SetDirty(asset);
            return asset;
        }

        private static T NewGen<T>(Graph graph, float x, float y) where T : Generator
        {
            var gen = Generator.Create(typeof(T)) as T;
            if (gen == null)
                throw new InvalidOperationException($"MapMagic 无法创建节点 {typeof(T).Name}");

            gen.guiPosition = new Vector2(x, y);
            graph.Add(gen);
            return gen;
        }
    }
}
