# MATCH-LAYER:场景装载对局系统与动态点位指南

> 对局装配中间层使用文档。三份资产 + 一个菜单,把完整对局(FSM AI、计分据点、部署、真人席位)装配到任意场景,并支持**移动的据点与安全区**。
>
> 涉及代码:`Assets/Scripts/Network/Match/`(MatchConfig / MapDefinition / Editor/MatchSceneBuilder)、`Assets/Scripts/Network/MovingZone.cs`、`NetworkMatchManager.cs`。

---

## 1. 快速开始(3 步装好一场对局)

1. **创建地图资产**:Project 右键 `Create → HagenDa → Map Definition`,填锚点(GR / 据点)。
2. **创建对局配置**:右键 `Create → HagenDa → Match Config`,引用上一步的地图,填 AI 数量 / 真人席位 / 系统开关。
3. **一键装配**:Project 中**选中该 MatchConfig 资产**,菜单 `HagenDa → Match → Build From Selected MatchConfig`。

构建完成后按 Play:`TrainingAutoHost` 自动开局(无需手动 StartHost),FSM AI 与据点系统即刻运行。

场景策略:

| 地图类型 | 构建目标 | 前提 |
|---|---|---|
| `Procedural`(程序生成) | 新建空场景 `Assets/Scenes/<配置名>.scene`,自动生成地板/围墙/种子掩体 | 无 |
| `SceneReference`(FBX) | 构建进**当前打开的场景**并保存 | 地图根对象已在场景中,场景已保存过 |

> 装配是**幂等**的:可反复重跑。SceneReference 模式会先清理旧对局对象(按组件识别,不会误删地图几何)。

---

## 2. MatchConfig 字段参考

| 字段 | 默认 | 说明 |
|---|---|---|
| `map` | — | 引用的 MapDefinition(必填) |
| `winScore` | 999999 | 先到该分获胜;**999999 = 持续战(不终局)** |
| `squadsPerTeam` / `squadSize` | 6 / 5 | 每队小队数 × 每小队人数 |
| `redeployDelay` | 10 | 死亡到可重新部署的秒数 |
| `autoDeployTimeout` | 10 | 部署菜单无操作自动回 GR 的秒数 |
| `brain` | Fsm | AI 大脑(ML 仅占位,当前只装配 FSM) |
| `redAiCount` / `blueAiCount` | 29 / 30 | 红蓝 FSM AI 数量(真人席位另计) |
| `mixedClassComposition` | true | 按小队混编 2 突击 + 2 支援 + 1 侦察;关闭则全员 `aiClassOverride` |
| `humanTeamPolicy` | AllRed | 真人分队策略:`AllRed` 全红 / `Balance` 进人少的队 / `FixedSlots` 先填满红队席位 |
| `redHumanSlots` / `blueHumanSlots` | 1 / 0 | FixedSlots 的双方席位数(也是构建时生成出生点的数量依据) |
| `commander` | RuleSquadCommander | `None` / `RuleSquadCommander`(每 20s 规则调度) / `LlmCommander`(PHASE10 LLM 指挥官 rig) |
| `spawnFsmStatsHud` | true | FSM tick 耗时统计 HUD |
| `spawnFreeCamera` | true | 自由观战相机(WASD / 鼠标拖拽) |
| `spawnBattleCamera` | false | 默认禁用的全景机位(避免与 FreeCamera 双倍渲染,需要时手动启用) |

多真人说明:Mirror 每个连接自动生成一名玩家(`autoCreatePlayer = 席位 > 0` 时开启);每客户端一份 DeployScreen / GameHud,天然支持多真人。部署前出生点是 GR 内的 `NetworkStartPosition`(镜像随机选),部署界面确认后的落位才是权威位置。

---

## 3. MapDefinition 字段参考

### 3.1 地图类型

| 字段 | 说明 |
|---|---|
| `kind` | `Procedural` 程序生成 / `SceneReference` FBX 导入 |
| `sizeX` / `sizeZ` / `wallHeight` | Procedural:地图长宽 (m) 与围墙高度 |
| `coverCount` / `coverSeed` / `coverMinGap` | Procedural:种子化掩体数量 / 随机种子 / 最小间距(编辑期生成并存入场景,非运行时重建) |
| `mapRootNames` | SceneReference:地图根对象名列表,如 `{"HGTR_map"}` 或 `{"Map_v1","Map_v2","GroundFloor_Grid"}` |
| `ceilingKeyword` | 名字含该关键字的子物体归入 `ceiling` 层(俯视相机不可见),其余归 `Ground`(小地图/指挥官快照可见) |
| `doubleSidedMaterials` | 启用地图材质双面渲染(HGTR 单面墙需要) |
| `anchorResolve` | 锚点解析方式:`ByCoordinates` 用资产内坐标 / `BySceneMarker` 按场景标记物名查找(找不到回退坐标) |

### 3.2 锚点(`anchors` 列表)

| 字段 | 说明 |
|---|---|
| `role` | `GarrisonRed` / `GarrisonBlue`(双方安全区 GR)、`CapturePoint`(争夺据点)、`PlayerSpawn`(真人出生点) |
| `letter` | 据点字母(A/B/C…),留空按顺序自动编号 |
| `markerName` | BySceneMarker:场景对象名(含未激活),其位置即锚点位置 |
| `position` | ByCoordinates:世界坐标;也是标记物缺失时的回退 |
| `radius` | 圆形判定半径(默认 12) |
| `extent` | 矩形半宽/半深 (XZ),x/y > 0 时生效并忽略 radius |
| `deployPointCount` | 在该区域上生成 `DeployPointSet` 重新部署点的数量(0 = 不生成) |

锚点在 Scene 视图的解析结果以移动区域 gizmo 一并可视化(见下节)。

---

## 4. 动态点位:移动的据点与安全区

给任意 `GarrisonRed / GarrisonBlue / CapturePoint` 锚点勾选 `moving`,装配器会为该区域挂上 `MovingZone` + `NetworkTransformReliable`,区域沿航点路径移动。

### 4.1 移动参数(MapAnchor 的 Movement 组)

| 字段 | 默认 | 说明 |
|---|---|---|
| `moving` | false | 开启移动 |
| `waypoints` | 空 | 航点(世界坐标),**少于 2 个不移动** |
| `moveSpeed` | 3 | 移动速度 (m/s) |
| `dwellSeconds` | 0 | 到达航点后的驻留秒数 |
| `pingPong` | true | true = 走到终点原路折返;false = 循环(终点瞬回起点) |
| `startDelay` | 5 | 开局等待秒数(部署阶段区域内不动) |

### 4.2 航点的两种来源

1. **显式坐标**:`waypoints` 数组直接填世界坐标(Procedural 与 ByCoordinates 模式的常规做法)。
2. **FBX 标记物子物体**:`anchorResolve = BySceneMarker` 且锚点留空 `waypoints` 时,自动收集标记物下名字以 **`WP` 开头**的子物体(按名字排序),并把**标记物自身位置作为第一个航点**。工作流:在 FBX 地图上把标记物空物体摆到起点,再在其下放几个 `WP_0 / WP_1 / …` 空子物体即可,无需手填坐标。

### 4.3 运行时机制(为什么"区域动了,一切都跟着动")

- **服务端权威**:仅 `NetworkServer.active`(Host / 服务器)驱动 `transform`;客户端不自行积分,通过 `NetworkTransformReliable`(ServerToClient,仅位置)收快照跟随,双端不漂移。
- **逻辑天然跟随**:CapturePoint 的占领计数(`OverlapSphere` 围绕自身位置)、GarrisonZone 的敌入强杀、部署点(DeployPointSet 子物体)、地图标记(MapPointMarker 子物体)、AI/LLM 的要地快照(`StrategicZone.GetState()` 实时读 position)全部锚定 `transform.position` —— 静态与动态的唯一区别是"有没有东西在改 transform"。
- **Scene 视图 gizmo**:黄色路径线 + 航点球,编辑器内直观预览巡逻路线。
- 已知边界:AI 对移动要地的目标点取自 `SquadCommander` 每 20s 的重下发(即下发时刻的位置);若需 AI 实时追逐移动据点,可在 `FSMAIController.RefreshObjective` 改为引用 `MovingZone` 当前位置(后续增强)。

---

## 5. 装配流水线(构建器做了什么)

按顺序,全部幂等:

1. 前置校验(Procedural 新建场景;SceneReference 校验地图根存在、场景已保存;**脏场景守卫**——当前场景有未保存改动会拒绝构建)。
2. 确保 MapLayers(Indicator/Highlight/Zone/ZoneOutline/Ground/ceiling)。
3. 构建预制与装备资产(玩家/AI/FSM 实体、投掷物、子弹、装备定义 —— 复用 NetworkSetup 的 internal builder)。
4. 清理旧对局对象(仅 SceneReference 模式)。
5. 解析锚点(标记物 → 坐标回退;移动航点收集)。
6. 应用地图:Procedural 生成地板/围墙/掩体场;SceneReference 归层 Ground/ceiling + 双面材质 + 碰撞体 + 背面查询。
7. 创建 GR ×2(含 DeployPointSet)、据点 ×N(+ StrategicZone 包装)、真人出生点 `HumanSpawn_R/B_*`(按席位分布到双方 GR)。
8. 创建对局系统:`NetworkMatchManager`(计分/小队/部署点查询/真人分队)、`StrategicZoneRegistry`、`FSMBattleSystem`(+FSMStatsHud)、`SquadCommander` / LLM 指挥官 rig。
9. `NetworkManager` + KCP + `TrainingAutoHost`(Play 即自动 Host;`autoCreatePlayer = 任一真人席位 > 0`);注册出生预制。
10. 相机(FreeCamera / 默认禁用的 BattleCamera,按地图 AABB 自适应)、EventSystem(有真人时)。
11. **烘焙 NavMesh(必须先于 AI 生成)**。
12. 生成 FSM AI 两队(锚点 GR 中心周围网格驻扎,出生高度 = GR 地面 + 1.5)。
13. 保存场景。

## 6. 示例资产(`Assets/Scripts/Network/Match/`)

| 资产 | 说明 |
|---|---|
| `FSMBattle59_Map` + `FSMBattle59` | Procedural 120×220,59 AI(29红/30蓝),AllRed 1 真人 → `FSMBattle59.scene` |
| `FSMBattle59_2H` | 同图,FixedSlots 红1蓝1 双真人 → `FSMBattle59_2H.scene` |
| `FSMBattle59_Moving_Map` + `FSMBattle59_Moving` | **动态点位演示**:据点 B 三角巡逻 (0,0)→(0,60)→(40,30),红方 GR 前出 (0,-92)→(25,-50)→(0,-20),均折返 → `FSMBattle59_Moving.scene` |
| `MapV1_2v2_Map` + `MapV1_2v2` | SceneReference(FBX),打开 Map_v1 场景后选中 `MapV1_2v2` 构建即可,双真人席位 |

## 7. 运行验证要点(回归清单)

- 对局系统齐全:NetworkMatchManager(数值与配置一致)/ StrategicZoneRegistry / FSMBattleSystem / SquadCommander;AI 数量、DeployPointSet 数、HumanSpawn 数与配置一致。
- 幂等:重跑构建对象数不翻倍。
- 移动区域:Play 后越过 `startDelay` 观察位移;**占领跟随**用基线对比法测(冻结全部 AI → 单实体跟随区域 3.5s → contention ≈ -3.5;移走实体 → contention 冻结),避免 59 AI 自行进出据点的杂讯。
- 多真人:Editor Host + `Build/Client/HagenDa.exe` 两件套,断言两名真人分别落在红/蓝队。

## 8. 注意事项与排错

| 现象 | 原因 / 处理 |
|---|---|
| 构建报"当前场景有未保存改动" | 脏场景守卫;先 `Ctrl+S`(SceneReference 写入当前场景,Procedural 也会替换活动场景) |
| 报"当前场景找不到地图根" | SceneReference 需先打开含 FBX 地图的场景再构建 |
| 警告"移动锚点 … 航点不足" | `waypoints` 少于 2 个且标记物下无 WP* 子物体 → 该区域不移动 |
| 区域不动 | 依次检查:航点 ≥2、`moveSpeed > 0`、已 Host(NetworkServer.active)、`startDelay` 是否已过 |
| 构建时 Console 出现 Mirror NetworkTransform NullReferenceException | **无害**:编辑期 AddComponent 的既有怪癖,`AddNetworkTransform` 已 try/catch 兜底并显式赋 `target`,运行时自愈 |
| 多真人 + LLM 指挥官 | 机制兼容(指挥官只向 FSM agent 下令),但开局门控在双真人下未充分实测;建议多真人配置先用 `RuleSquadCommander` |
| 验证时行为与预期不符 | 确认 Play 会话是最新代码(域重载后重新进 Play;旧会话挂在旧程序集上) |


