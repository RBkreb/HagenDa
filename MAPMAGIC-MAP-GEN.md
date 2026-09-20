# MapMagic 2 地图生成器 — 探明报告与实施计划

状态：**已实现并跑通**（决策已确认，代码已落地，全流程已实测验收）
范围：`Assets/ThirdParty/MapMagic/`（MapMagic 2 **Core** v2.1.16）+ `Assets/Game/Scripts/Network/Match/`
说明：本文件只描述 MapMagic 这条独立路线。`Terrainscripts/` 与 `docs/map/*` 的早期内容属于
「高度图 → 手工 Terrain」的另一分支，本实现未复用、未改动它们（本实现自己的产物在
`docs/map/mapmagic/`）。

> **已确认的六项决策**（本节为实施依据）
> 1. 分块：**单块 500 m @1025**
> 2. 宏观来源：**路线 A**（脚本设计掩码 → Import）
> 3. 装饰实现：**先脚本侧**
> 4. 验收标准：不沿用旧硬参数，先做再说
> 5. 地形承载：**静态烘焙进场景**
> 6. 可用装饰素材：`RPG_FPS_game_assets_industrial` + `SimpleNaturePack`
>
> 实际实施结果见第 8 节「实施记录（as-built）」。

---

## 1. 结论速览

1. **装的是 MapMagic 2 Core，只有 Map/Matrix 一个模块。** 没有 Objects Output、Trees Output、Biomes、Stamps、Splines。手册 48 页也刚好只覆盖这一个模块。
2. **地图骨架和装饰物都必须由我们自己出**：
   - 宏观布局（三通路、外圈山脊、据点平台）用 Core 的噪声节点「涌现」不出来，必须外部给设计骨架；
   - 装饰物 / 据点内装饰 / 树木，`MapMagic` 一个都放不了，必须脚本侧放（或自己写自定义 OutputGenerator）。
3. **MapMagic 的定位应该收窄为「细节 + 侵蚀 + 贴图 + 分块 + 焊接 + 锁定」引擎**，而不是「一键出整张地图」。这是本次计划的核心判断。
4. 包本身状态健康：`MapMagic.dll` / `MapMagic.Editor.dll` / `MapMagic.Settings.dll` 均已正常编译，`MAPMAGIC2`、`MM_NATIVE` 宏已生效，HDRP 地形材质它自己会优先找 `HDRP/TerrainLit`。
5. 关键 API 全部可从编辑器脚本驱动（建图、连节点、出图、分块、生成进度、锁定），所以**不需要手搓 Graph 资产**——可以写一个 `HagenDa/MapMagic/*` 菜单一键重建。
6. 本项目已有 `MapAnchor` / `GarrisonZone` / `CapturePoint` / `MatchSceneBuilder` / `CoverRegistrar` / 种子化掩体场 / NavMesh 烘焙，**地图生成器应当接进这条既有链路，而不是另起一套**。

---

## 2. MapMagic 2 Core 能力清单（已逐类核对源码）

### 2.1 节点

| 类别 | 节点 |
| --- | --- |
| 初始 | `Constant200`、`Noise200`、`Voronoi200`、`SimpleForm200`、`Import200`、`Spot210` |
| 修改 | `Curve200`、`Levels200`、`Contrast200`、`UnityCurve200`、`Mask200`、`Blend200`、`Normalize200`、`Blur200`、`Cavity200`、`Slope200`、`Selector200`、`Terrace200`、`Direction210`、`Ledge210`、`Beach210`、`Erosion200`、`Sediment210`、`Parallax210` |
| 输出 | `HeightOutput200`、`TexturesOutput200`、`GrassOutput200`、`HolesOutput2112`、`CustomShaderOutput200`、`DirectTexturesOutput200`、`DirectMatricesOutput200` |

三个对本次计划最关键的节点：

- **`Import200`**：把一张 `MatrixAsset`（MapMagic 的「Imported Map」资产，`public Matrix matrix`）当输入图，带 `wrapMode / scale / offset`。**这是把外部设计骨架喂进 MapMagic 的唯一通道**，且 `MatrixAsset` 是普通 `ScriptableObject`，脚本可以创建并填充。
- **`Mask200`**：实现是 `dst = Mix(A, B, mask, invert)`，即**掩码驱动的插值**。这就是「在任意位置把地形压平成据点平台」的现成原语：`Mask(A=地形, B=Constant(平台高度), mask=据点圆形/矩形掩码)`。
- **`Spot210`**：`Intensity / Position / Radius / Hardness`，一个圆形斑点，配合 `Mask200` 可以纯图内做圆形平台。

### 2.2 元数据与基础设施

- `MapMagicObject`：`graph`、`tileSize`（`Vector2D`，**全局统一的方块尺寸**）、`tileResolution`（枚举 33/65/129/257/513/1025/2049）、`tileMargins`、`draftResolution/draftMargins`、`locks[]`、`mainRange`、`draftsInEditor/draftsInPlaymode`、`instantGenerate`、`hideFarTerrains`、`terrainSettings`、`globals`、`applyColliders`、`Refresh(bool clearAll)`、`IsGenerating()`、`GetProgress()`、`ApplyTileSettings()`。
- `globals`（全局输出设置，不属于图）：`height`（地形最大高度）、`heightInterpolation`（None/Smooth/Scale2X/Scale4X）、`heightMainApply`（SetHeights / SetHeightsDelayLOD / TextureToHeightmap）、`heightSplit`、`grassResDownscale`、`objectsNumPerFrame`、`holesRes`。
- `TerrainSettings`：`material`、`allowAutoConnect`(Auto Connect + groupingID)、`baseMapDist`、`baseMapResolution`、`drawInstanced`、`pixelError`、`detailDraw/detailDistance/detailDensity`、`treeDistance` 等——对应标准 Terrain 的 Settings 页，一次性应用到所有分块。
- `TerrainTileManager`：`Pin(Coord, asDraft, holder)`、`Unpin(Coord)`、`All()`、`FindByWorldPosition(x,z)`、`WorldRect`。**分块网格可以完全由脚本钉出来**。
- `Lock`：`{ locked, worldPos, worldRadius, worldTransition, rescaleDraft, relativeHeight }` + `IsIntersecting(Rect)`。语义是「生成时读走这块地形的现状，生成后再写回去」——**专门用来保护手工/脚本对地形的改动**。
- 事件：`TerrainTile.OnBeforeTilePrepare / OnAllComplete / OnBeforeResetTerrain`、`Graph.OnOutputFinalized`、`Graph.OnAfterNodeGenerated`。
- `Expose` 模块：把节点参数「暴露」出来供父图/脚本改，是官方认可的图参数化机制。
- `Den.Tools.ObjectsPool`：**在 Core 的 Tools 里，是完整可用的**（含编辑期 `PrefabUtility.InstantiatePrefab`、`Reposition / RepositionRoutine`）。虽然 Objects Output 节点没装，但对象池基础设施在——这为「以后写自定义散布 OutputGenerator」留了门。
- 地形持久化：分块 `Terrain` / `TerrainData` 是场景对象（`new TerrainData()` / `Instantiate(template)`），`TerrainTile.main` 通过 `[SerializeField]` 备份字段序列化，且 `generateReady` 为真时不会重新生成 → **钉住的瓦片会连同高程、贴图、碰撞留在场景里**。这是「烘焙进场景」这条路成立的依据。

### 2.3 三个缺口（本计划所有设计都绕着它们走）

| 缺口 | 后果 | 应对 |
| --- | --- | --- |
| **没有 Objects Output / Trees Output** | 树木、石头、掩体、据点内装饰，MapMagic 一个都放不了 | 脚本侧种子化摆放（阶段 4）；后期可选自定义 `OutputGenerator`（`ObjectsPool` 已具备） |
| **没有 Biomes / Stamps / 距离场节点** | 三通路、外圈方形山脊、对称布局这类「设计感」形状无法从噪声涌现 | 脚本按配置生成设计掩码 → `Import200` → 噪声叠加 + 侵蚀；或用 `Spot210`+`Mask200` 做据点平台 |
| **`tileSize` 是单一全局值** | 非正方形地图可行但网格/分辨率要自己算，且分辨率恒为正方形 | 在配置层做推导，不指望包自己适配 |

---

## 3. 建议架构

```
MapGenConfig (ScriptableObject)          ← 唯一参数源
        │  地图尺寸 / 种子 / 高度范围 / 锚点 / 平台 / 装饰 / 掩体 / 地形层
        ├─────────────► 设计掩码生成器 (C#, 编辑期)
        │                  外圈方形山脊 · 三通路 · 据点/安全区平台 · 对称镜像
        │                  → MatrixAsset (MapMagic 资源)
        │
        └─────────────► MapMagic Graph (脚本构建)
                           Import(设计掩码) → Blend(±噪声细节) → Erosion → Blur
                           → 据点平台 Mask 压平 → TexturesOutput + HeightOutput
                                │
                                ▼
                           MapMagicObject + 钉住的瓦片网格
                                │
                                ▼
        ┌─────────────► 落地器 (C#, 编辑期，读地形高度)
        │                  导航网格烘焙 · 可达性/坡度/掩体密度校验 → metrics.json
        │                  据点内结构化装饰 · 地图装饰物 · 掩体场 (+CoverRegistrar)
        │                  平台加 Lock（保护后续重生成）
        └─────────────► 锚点实体 (复用 MatchSceneBuilder 既有代码)
                           GarrisonZone · CapturePoint · StrategicZone · 出生点
```

**为什么这样切**：MapMagic Core 的强项（多线程生成、跨块焊接、侵蚀、Splat 归一化、锁定、分块流式）全部保留；它的弱项（宏观设计、物体散布）由我们已经有的编辑器工具箱补上。同时不碰 `Terrainscripts` 那一套。

**两种宏观来源，建议选 A**：

- **路线 A（推荐）**：脚本按 `MapGenConfig` 生成设计掩码 → `Import` → 噪声/侵蚀出细节。好处是 `docs/map/main.md` 里那套硬参数（三通路、400×400 战斗区、外圈 50 m 不可通行、据点三角 120–180 m、镜像公平性）**可验收、可复现**。
- **路线 B**：纯程序化图（Noise/Voronoi/SimpleForm/Blend + Spot/Mask 压平）。不用出掩码，但宏观布局是涌现的，**必然对不上**既定验收条款。

两条路可以混用（A 的骨架 + B 的细节，A 本身就是这么用的）。

---

## 4. 参数模型 `MapGenConfig`

放 `Assets/Game/Settings/MapGenConfig.asset`，`CreateAssetMenu` 路径 `HagenDa/Map Gen Config`：

- **尺寸**：`sizeX` / `sizeZ`（默认 500×500）、`playHalf`（200，有效战斗区半宽）、`ringWidth`（50，不可通行外圈）、`seed`
- **分块**：`tileSize`（默认 500 单块）、`tileResolution`（默认 1025 → 0.488 m/px，正好等于既有硬参数）、`tileMargins`（16）、`draft` 开关
- **高度**：`heightMin/Max`（0–120）、`bands[]`（低地/中台地/高台/山脊的分层高度与坡度规则）
- **宏观**：`mirrorAxis`（默认关于反对角 `s=0` 镜像，保证公平）、`lanes[]`（主路 + 两翼 + 垂直通路，含宽度/曲折度）、`centralFeature`（中央高地：高度、半径、可通行垭口）
- **锚点**：直接**复用现有 `MapAnchor`**（`role/letter/markerName/position/radius/extent/deployPointCount/moving/waypoints`），这样 `MatchConfig` → `MatchSceneBuilder` 链路不用改
- **平台（据点/安全区/据点内）**：`padRadius`、`padTransition`、`padHeightOffset`、`padShape`（圆/矩形）、平台是否强制水平
- **装饰**：`decorRules[]`（`prefab`、`density`、`slopeMax`、`heightBand`、`scaleRange`、`alignToNormal`、`clearanceRadius`、`areaMask`）、**以及独立的 `baseDecorRules[]`（据点/安全区内装饰：集装箱、沙袋、帐篷、哨塔等，按锚点朝向的结构化布置而非随机撒点）**
- **掩体**：扩展既有 `coverCount / coverSeed / coverMinGap` + 类型比例
- **地形层**：`TerrainLayer[]` + 每条的高度/坡度规则（HDRP 下单地形最多 8 层，实际用 4–5）
- **验收阈值**：可达性 ≥90%、15 m 内 ≥1 半身掩体、40 m 内 ≥1 全身掩体、穿越 ≤65 s、平台平整度容差

落地：给 `MapDefinition` 加第三种 `MapKind.MapMagic`，并加一个 `mapGen` 引用字段。这样 `MatchConfig` 仍是唯一入口，`MatchSceneBuilder` 只多一个分支。

---

## 5. 分阶段实施计划

按项目既有习惯，每阶段产出物 + `metrics.json` + 自检报告，达标才进下一阶段。

### P0 — 冒烟验证（先证伪，再动工）
- 编辑器内用脚本建一个最小图（`Constant` → `HeightOutput200`），挂 `MapMagicObject`，钉 1 块瓦片，跑生成，确认：HDRP 地形材质自动接上、无编译/控制台错误、地形高程确实落盘到场景。
- 验证一个风险点：**`Assembly-CSharp` 里自定义 `Generator` 能否被 MapMagic 识别**（靠 `GeneratorMenu` 特性扫描）。这决定「以后能不能写自定义散布节点」。
- 验证 HDRP 地形层上限与 Base Map 表现。
- 产出：`docs/…/mapmagic-smoke.md` + 截图 + 控制台结论。
- **不通过就不往下走**（例如 HDRP 地形材质接不上，就要先解决材质模板）。

### P1 — 配置与设计掩码
- `MapGenConfig`（第 4 节全部字段）+ `MapDefinition.MapKind.MapMagic`。
- `MapDesignMask` 生成器：按配置在内存里合成 `Matrix`（外圈山脊、三通路、中央高地、据点/安全区平台、镜像对称），写成 `MatrixAsset`。
- 产出：掩码可视化 PNG（俯视 + 高程带图）、`metrics_mask.json`（平台平整度、通路宽度、斜坡角度分布、镜像误差）。
- 门槛：镜像对称误差 < 0.1 m；平台内坡度 < 1°；战斗区可通行坡度占比达标。

### P2 — MapMagic 图构建器
- `MapMagicGraphBuilder`：脚本化建图（`Graph.Create` / `Add` / `Link`），节点链 `Import → Blend(细节噪声) → Erosion → Blur(带 Safe Borders) → 平台 Mask 压平 → TexturesOutput/HeightOutput`。
- `MapMagicObject` 装配：`tileSize`/`tileResolution`/`tileMargins`/`globals.height`/`terrainSettings`（HDRP 材质、Base Map、Draw Instanced、Pixel Error）、**关掉 Infinite Terrain**、钉满瓦片网格。
- 生成驱动：`Refresh(clearAll:true)` → 轮询 `IsGenerating()`/`GetProgress()`（编辑器 update 由 MM 自行订阅）→ 超时保护 + 进度条。
- 产出：`Assets/Game/Match/Graphs/HagenDaMacro.asset`（可手调）+ 生成耗时/内存记录。

### P3 — 落地与验收闸门
- 导航网格烘焙（**整图一个 `NavMeshSurface`**，不要每块一个，否则接缝断）。
- 度量并落 `metrics.json`：可达面积占比、坡度分布、平台平整度、掩体密度（15 m/40 m 规则）、主路径穿越时间（冲刺 8 m/s / 载具 14 m/s）、出生点散布。
- **任一硬参数不达标就报错停下**，不静默通过。

### P4 — 装饰与掩体
- 地图装饰物：按 `decorRules` 种子化摆放，`Terrain.SampleHeight` 贴地，遵守坡度/高程带/净空规则，**明确不同步到网络**（静态烘焙进场景）。
- 据点/安全区内装饰：`baseDecorRules` 按锚点朝向做**结构化布置**（不随机），平台已压平所以不会有悬空。
- 掩体场：复用既有 `BuildCoverField` 逻辑但改为**感知地形高度**，根节点挂 `CoverRegistrar`。
- 每个平台加 `Lock`，保护后续重生成不冲掉手工微调。
- 产出：装饰物统计（数量/密度/三角面预算）、遮挡与性能抽查。

### P5 — 接回对局链路
- `MatchSceneBuilder` 增加 `MapKind.MapMagic` 分支：用它生成的地形替代当前的 `Floor + 四面墙`，之后**照原样**跑既有锚点/据点/安全区/出生点/对局管理器代码。
- 产出一个可对局场景：`Assets/Game/Scenes/<cfgName>.scene`。
- 产出：30v30 场景加载时间、内存、帧时间基线。

---

## 6. 风险清单

1. **装饰物是最大工作量**，不是 MapMagic 能解决的。P0 必须先验证自定义节点可行性，再决定走脚本侧还是写自定义 OutputGenerator。
2. **跨平台确定性**：运行时重生成地形有 Mono/IL2CPP 浮点分歧风险，对竞技不公平。**建议烘焙进场景 + 关掉 Infinite Terrain**，代价是场景体积（500 m @1025 + 4–5 层 Splat 大约几十 MB）。
3. **场景体积 / 内存**：若过大，退到 2×2 × 250 m @513（同样 0.49 m/px，剔除与流式更好，但有 4 个 Terrain 与 4 个碰撞体）。配置层已支持两种。
4. **侵蚀很慢**：`Erosion200` 是 MM 里最慢的节点，每块要秒级；这是编辑期一次性烘焙，可接受，但 1025 分辨率 + 边距会放大耗时。
5. **HDRP 地形**：需确认 `TerrainSettings.material` 指向 HDRP 地形材质（MM 自己会先找 `HDRP/TerrainLit`），以及 8 层 Splatmap 上限、Base Map、`drawInstanced` 在 HDRP 下的实际表现。桥接工具报告的 `srp: builtin` 与 `GraphicsSettings.asset` 里已挂 `HDRP High Fidelity.asset` 矛盾，P0 要在编辑器里实测确认。
6. **`MapMagicObject` 是 `[ExecuteInEditMode]` 且生成会写图资产**：必须 `EditorUtility.SetDirty` + `AssetDatabase.SaveAssets`，且禁止在 Play 模式、脏场景、生成进行中执行。
7. **重生成会作废装饰物**：地形变了，贴地摆放的装饰会悬空/埋进去。约定「改地形 → 必须重跑 P4」，平台用 `Lock` 保护。
8. **不要用 `RenderTexture`/运行时 API 去读 MM 的内部矩阵**；需要高度就问 `Terrain.SampleHeight` 或 `TerrainData.GetHeights`。
9. 严格按 `AGENTS.md`：改动任何既有符号前先跑 `impact` 分析，提交前跑 `detect_changes`。

---

## 7. 需要你决策的点（已确认）

1. **分块方案**：单块 500 m @1025 ✅
2. **宏观来源**：路线 A（脚本设计掩码 → Import）✅
3. **装饰实现**：先脚本侧 ✅
4. **验收基准**：不沿用旧硬参数，先做再说 ✅
5. **地形承载**：静态烘焙进场景 ✅
6. 可用素材：工业包 + SimpleNaturePack ✅

---

## 8. 实施记录（as-built）

### 8.1 交付文件

| 文件 | 作用 |
| --- | --- |
| `Assets/Game/Scripts/Network/Match/MapGenConfig.cs` | 唯一参数源：尺寸/种子/高度/对称/通路/中央地标/锚点/平台/装饰/掩体/地表层 |
| `.../Match/Editor/MapGen/MapMaskComposer.cs` | 设计掩码合成器 → `MatrixAsset`（+ 平台权重图、PNG 预览） |
| `.../MapGen/MapMagicGraphBuilder.cs` | 脚本建图 + 装配 `MapMagicObject` + 阻塞式生成驱动 |
| `.../MapGen/MapDecorPlacer.cs` | 地图装饰 / 据点内结构化装饰 / 掩体场 |
| `.../MapGen/MapTerrainUtil.cs` | 地形采样 + **底边贴地**（兼容两包不同轴心约定） |
| `.../MapGen/MapGenPipeline.cs` | 菜单编排 + NavMesh + metrics + 锚点贴地 |
| `.../MapGen/MapGenDefaults.cs` | 一键生成默认配置（含素材按名查找的调色板） |
| `MapDefinition.cs`（改） | 新增 `MapKind.MapMagic` + `mapGen` 字段 |
| `MatchSceneBuilder.cs`（改） | 新增 MapMagic 分支（锚点取自 MapGenConfig，NavMesh 复用流水线产物，清理时保住装饰/掩体） |

菜单：`HagenDa/MapMagic/` → Create Default Config / Build Default Anchors / Full Build (New Scene) /
Rebuild In Current Scene / Regenerate Terrain Only / Re-place Decor Only / Bake NavMesh / Report Metrics

### 8.2 图结构（实际生成 18 节点 / 24 连线）

```
Import(设计掩码) ┐
                 ├─ Blend(add, detailAmplitude) ─ Mask(平台权重→压回掩码) ─ Blur ─┬─ HeightOutput
Noise(Simplex)   ┘                     ↑
                             Import(平台权重图)
                        └─ 每层:Slope × Selector → Blend → TexturesOutput(5 层)
```

关键设计（都是实测逼出来的）：

- **平台权重后置压平**：细节噪声是在图里叠加的，会把掩码阶段已压平的据点重新揉皱
  （实测平台坡度 **12.8°**，站不稳也放不了建筑）。所以掩码额外产出平台权重图，
  噪声之后用 `Mask` 节点把平台区域取回掩码原值 → 实测 **0.0°**。
- **细节噪声 `detail` 从 0.5 降到 0.35**：小 fractal 主导会在 0.49 m/像素下产生大量
  超短波长起伏，把坡度从掩码的 ~7° 抬到 ~20°、1/6 地图不可行走。
- **高度插值用 `None`**（不是 Scale2X）：Scale2X 会把高度图放大到 Unity 上限 2049，
  与「原生 1025」的决策不符。
- **地形层必须给真正的 `TerrainLayer` 资产**：MapMagic 只画 alphamap 权重，`prototype`
  为空时地形是一片无贴图灰白。

### 8.3 实测指标（seed 20260917，500×500 m @1025）

产物：`docs/map/mapmagic/metrics_MapGenConfig_20260917.json`

| 指标 | 实测 |
| --- | --- |
| 高程范围（有效战斗区） | 6.0 – 41.8 m（均值 16.5） |
| 坡度均值 / >40° 占比 | 8.7° / 2.2% |
| NavMesh 可达（400×400 战斗区） | 99.5%（19244 三角面） |
| 据点/安全区平台最大坡度 | **0.0°** |
| 掩体总数 | 1033（目标 900） |
| 15 m 内有半身掩体的采样点 | 74.5% |
| 40 m 内有全身掩体的采样点 | 94.5% |
| 装饰 | 地图 1593 / 据点内 242 |
| 生成耗时 | 5.4 s |

实测截图：`docs/map/mapmagic/mask_MapGenConfig_1025.png`（高度带预览）、
`view_ground.png`（地面视角，可见地形贴图 / 边界山脊 / 中央台地植被 / 集装箱掩体街）。

场景：`Assets/Game/Scenes/MapGenConfig.scene`（单文件约 25 MB，静态烘焙）。

### 8.4 过程中修掉的真实缺陷（都靠实测发现）

1. **世界↔像素换算混用**：对称平均与平台循环都错把 `1/(res-1)` 当世界→像素用，
   导致整张图被外圈山脊高度抹平（均值 0.58）。正确是 `(res-1)/size`。
2. **实例位置从未写入**：算出了目标 XZ 却只用来采样地形，没有赋给 transform，
   所有装饰/掩体堆在世界原点。
3. **`Collider.bounds` 读的是物理缓存**：同帧新建的碰撞体读到陈旧变换（全在原点），
   掩体密度被算成 0.5%。需 `Physics.SyncTransforms()`（NavMesh 烘焙同理）。
4. **NavMesh 烘焙顺序错**：原先在掩体落位前烘，障碍不挖洞，AI 会穿墙。
5. **NavMesh 可达性采样**：从 `heightMax` 起 500 m 半径吸附 → 恒为 100%，无意义；
   改为从地面高度起、2 m 半径。
6. **坡度统计把边界山脊算进去**：50 m 山脊是刻意做的不可通行墙（25 m 内抬升 90 m），
   混进来会掩盖可玩区真实地形。改为只统计有效战斗区。
7. **据点装饰严重超量**：只按环带铺，一个 70 m 半径环带按 6 m 间距能排一百多个，
   而配置只要十几个。改为先铺候选、再均匀抽稀到 `count`。
8. **`MapMagicObject` 是 `[ExecuteInEditMode]`**：`AddComponent` 立即触发 `OnEnable`
   → `StartGenerateNonReady` 抛 "Graph data is not assigned"。必须先把 GameObject
   建成**未激活**，配好 graph 与瓦片后再激活。
9. **`SaveCurrentModifiedScenesIfUserWantsTo()` 会弹模态框**，在批处理/桥接调用里直接
   卡死流水线。改为确定性 `SaveOpenScenes()`。
10. **锚点 Y 恒为 0**：掩码只用 XZ，但下游按完整 XYZ 用，不贴地则据点/安全区/出生点
    全部埋在地形下（实测地形 ~12 m）。生成后回填 Y。

### 8.5 与计划相比的偏差

- **没有写自定义 `OutputGenerator`**：按决策 3 走脚本侧；`Den.Tools.ObjectsPool` 已具备，
  若要「随重生成自动更新装饰」可后续升级。
- **`Lock` 未使用**：未使用 MapMagic 的 `Lock` 机制。地形重生成会作废贴地的装饰/掩体，
  当前约定是「改地形 → 重跑 `Re-place Decor Only`」。若需要保留手工微调，再引入 `Lock`。
- **地形原生噪声未做侵蚀**：`erosionIterations = 0`（侵蚀是 MM 最慢节点，且当前坡度
  指标已达标）。需要更自然的地貌时把它开到 3–5，注意每块秒级。
- **回退方块仍在**：未配置掩体预制体时会退化成 `PrimitiveType.Cube`（沿用既有
  `MatchSceneBuilder` 的尺寸约定），保证素材缺失也能出图。

### 8.6 已知遗留

- 场景体积：500 m @1025 + 5 层 Splat 单文件约 25 MB（静态烘焙的代价，符合决策 5）。
- `MatchSceneBuilder` 触发时会打印两条 Mirror `NetworkTransformReliable` 的
  `NullReferenceException`——**既有问题**，已被 `AddNetworkTransform` 内部 `try/catch`
  捕获，与本次改动无关（`EditorScenePrimitives.cs` 无改动）。
- 两个素材包**都没有 README/许可文件**，署名/授权条款无法从仓库恢复，需要你确认来源。
- 工业包 **145 个预制体是几何中心轴心**，自然包是底座轴心。`MapTerrainUtil.AlignBottomToGround`
  用渲染包围盒底边统一贴地，因此与轴心无关；若新增自定义摆放代码，务必沿用这一点。

