# PHASE11：LLM 指挥官符号化重构（去图像化）

> **状态：已完成并通过运行验收（2026-09-05）**。实现位于 `Assets/Scripts/Network/Commander/`，FSMBattle 场景实测通过。四轮 grilling 访谈定案，计划见 `.codely-cli` 会话 plans。

## 1. 背景与目标

PHASE10 指挥官依赖多模态 VLM 读俯视快照图像决策——端侧模型读不准图、无法理解整局对局，指挥无效。本次重构：**彻底移除图像感知，改为纯文本符号化输入**（六边形格空间编码 + 结构化态势文本），收窄输入/输出语义宽度，把对模型的要求从"看懂图"降为"读懂表"——纯文本后任意指令模型可用（现配 `qwen3.8-4b`）。

## 2. 设计定案（四轮访谈共识）

### 2.1 空间编码（`CommanderHexGrid`，红蓝共享一个实例）
- **范围**：全量支持 Procedural 平坦图 + SceneReference FBX 图（HGTR/Map_v1）+ 移动点位（当前仅移动 GR）
- **网格**：flat-top 六边形，**固定目标格数 ~64**（`hexTargetCells`），格尺寸随地图 AABB 二分缩放逼近；FSMBattle(120×220) 实测 63 格 / R=12.1m
- **有效格判定**：格中心向下射线（RaycastAll 跳过动态实体与触发器，忽略 `ceiling`/地图标记层，取首个静态结构命中——防 `queriesHitBackfaces` 穿墙底背面误判）+ **`NavMesh.SamplePosition` 为可走性权威**；开局惰性预计算一次，`#N` 全局稳定
- **编码**：顺序整数编号 `#N`（1..N，南→北、西→东排序）+ **图内整数中心** `(X,Z)`（原点=地图西南角，X 东 Z 北；内部世界坐标，显示时平移归一）
- **命令词汇表** = `{格#N} ∪ {据点编号 HQ-A/B/C…}`；**双方 GR 都不可作为目标**（仅态势展示，回己方 GR 走所在格）；不提供 (X,Z) 直填
- **移动 POI**：一次性语义（取下令瞬间位置）；当前地图仅 GR 移动

### 2.2 每轮输入（`CommanderSituationText.RoundInput`）
```
【触发】空闲观察                                   ← 触发器沿用 PHASE10（空闲30s/据点中立化/小队全歼/侦测完成/wait/探活恢复）
【全局态势】比分 红14:蓝9（领先+5）｜进行中｜时长04:32｜己方存活 5/6队 24人
【POI状态】                                        ← 全量列出，排序 争夺中>敌占>己占>中立，GR 随后
HQ-B｜争夺中｜争夺值+12｜红3蓝5｜格#31(60,110)
GR红｜己方安全区｜红2蓝0｜格#9(60,18)
【小队状态】                                        ← 每队一行；繁忙=任一存活成员 FSM 非 Normal
小队1｜4/5｜格#29(58,96)｜距HQ-B 14米｜上次指令35秒前｜空闲
小队6｜0/5｜全灭(重部署中)｜—｜—｜—               ← 全灭队保留一行
【侦测敌情】                                        ← 被己方标记敌军按最近 POI 聚合（60m 内）
HQ-B 附近 ~7名(威胁:小)                             ← 大≥20/中≥10/小≥5，低于小幅只报人数
游散 ~4名(格#12,#47,#33)                           ← 距所有 POI 均远 → 游散，附最多 3 个格号
【上轮以来事件】…                                   ← 现有事件流，坐标全部改用格编码
```
- **无作弊信息**：全局态势只报己方存活（敌人数由比分+威胁度间接体现）

### 2.3 输出（工具）
- `squad_order(squads:int[], target:string)`：1–6 队**同目标**（格#N 或据点编号）；单队传 `[n]`；整组前置校验（同回合同队重复下令报错防打转；全歼队报错）
- `commander_weapon(weaponNumber, target)`：目标口径与 squad_order 统一；落 POI 取下令瞬间位置
- `weapon_status()` / `wait(seconds)` 保留；**`get_snapshot` 删除**
- 开局部署维持**单发制**逐队下令（6 队目标各异无法合并），但 **`tool_choice=required` 强制工具调用**（见 4.2）
- 成功结果附带结构化字段 `{"squads":[..],"cell":N,"wx":..,"wz":..}`——LLM 可确认解析结果，Orchestrator 依此做簿记

### 2.4 提示词（`CommanderSituationText.SystemPrompt`）
静态部分每请求重发：角色 + 网格说明 + **完整可派驻格表**（必须放系统提示词——放首轮用户消息会被 10 轮滚动记忆挤掉）+ 争夺值符号约定 + 工具说明 + **三条战术示例**（敌攻我据点→抽 1-2 支最近小队回防；意图进攻→集中 2-3 支；无紧迫威胁→优先占更多据点）+ 优先级（占领最大化>回防>恋战）+ 简练约束（analysis ≤2 句）。开局变体仅暴露 squad_order、只给部署任务。实测每请求 prompt ≈1.7k tokens（格表+工具+态势），对比原 base64 PNG 压缩一个量级。

### 2.5 外围
- `SquadCommander` 规则桩**保持现状**；LLM 休眠时接管、恢复时禁用（`RecomputeStub` 逻辑不变）
- **删除**：`CommanderMapCamera` / `CommanderMapOverlay` / `CommanderPrompts` / Config 的 `snapshotLongSide`、`gridSizeMeters`、`saveSnapshotPng` / `LlmMessage.UserImage` / `RoundLogger.WriteSnapshot`
- **新增**编辑器 Gizmos：hex 轮廓绿(有效)/红(无效) + `#N` 标注（选中 HexGrid 可见）
- 客户端 `CommanderHud`：目标文本 `小队N → 格#N`，菱形标记直接用同步的世界坐标（`SquadObjectiveMsg` 改为 `team/squad/cellId/wx/wz`，客户端无需本地换算）
- 冒烟测试改纯文本探针（`HagenDa/Commander/Test LLM Endpoint`）

## 3. 实现清单

| 动作 | 文件 |
|------|------|
| 新增 | `Commander/CommanderHexGrid.cs`、`Commander/CommanderSituationText.cs`、`PHASE11.md` |
| 改造 | `CommanderOrchestrator.cs`（去图像/新态势/lastOrderTime/NoteSquadOrder）、`CommanderTools.cs`（新签名/GR拒绝/整组防重）、`CommanderWeaponSystem.cs`（TryOrder 收世界点）、`CommanderConfig.cs`（删3字段+hexTargetCells/威胁阈值/model=qwen3.8-4b）、`NetworkCommanderState.cs`（载荷 cellId+wx/wz）、`CommanderHud.cs`、`LlmRestClient.cs`（toolChoice/去图像分支）、`CommanderRoundLogger.cs`、`MapLayers.cs`(注释)、`Editor/CommanderSetup.cs`（rig 换 HexGrid+可传精确边界）、`Editor/NetworkSetup.cs`、`Match/Editor/MatchSceneBuilder.cs`、`Editor/CommanderSmokeTest.cs` |
| 删除 | `CommanderMapCamera.cs/.meta`、`CommanderMapOverlay.cs/.meta`、`CommanderPrompts.cs/.meta` |

装配入口不变：`HagenDa/Setup Commander System`（现在注入共享 HexGrid；`Setup(Bounds?)` 重载供 MatchSceneBuilder/NetworkSetup 传精确图 AABB——Procedural 图几何不在 Ground 层，`TryGetMapBounds` 不可靠，必须传）。

## 4. 关键机制与实测教训

1. **改代码字段默认值不回写已存在资产**——换模型必须直改 `Assets/Settings/CommanderConfig.asset`（本次 model: qwen3.5-4b → **qwen3.8-4b**，代码默认值同步改）。
2. **qwen3.8-4b 在 `tool_choice=auto` 的单发开局轮会把 tool_calls 写成纯文本 JSON**（```json 数组当 content 输出，覆盖 0/6，Active 轮却正常）→ 开局请求 `tool_choice=required` 强制工具调用后 6/6；Active 轮保持 auto 允许"不动"（模型 reasoning 中明确判断后正确停手）。
3. **HexGrid 首建可能撞 NavMesh/物理未就绪的瞬态**（17:47 会话整局空网格：POI/小队全落米坐标回退、模型被迫硬造 `#HQ-A` 语法）→ `EnsureBuilt` 空结果不粘滞，下次查询自动重试自愈。
4. **格显示坐标必须与提示词原点约定一致**（图内米、西南原点）——首版用世界坐标出现负值，与"原点=西南角"矛盾；且模型 prose 罗盘曾南北颠倒（ID 下令不受影响，纯分析措辞错）→ 提示词补"格编号自西南角起按南→北递增"锚定。
5. **模型目标语法容错值得做**：`#HQ-A`（# 前缀混用到据点）解析器已容忍。
6. **门控期间已落定方的空闲轮会与另一方开局请求互卡**：`SettleOpening` 即刻进入 Active（不等门控解除），空闲计时 30s 一到就打请求——与思考方的开局请求在 LM Studio 排队，把对方思考拖长甚至拖到 300s 超时退化 → `Update` 在 `GateActive` 期间抑制 Active 空闲轮（事件/探活恢复仍放行，恢复方可补部署）；`NotifyMatchStarted` 恢复计时并重置抑制日志。判定口诀：JSONL `trigger=空闲观察` 且出现抑制日志 = 空闲轮被拦；`trigger=探活恢复` + `error=超时(300s)` = 超时后二次请求（合法保留的二次部署机会）。
7. **wait 每回合限一次**：模型会连发 wait（只覆盖唤醒时刻、空耗工具迭代）→ 二次调用返回 `{"error":"本回合已使用过 wait..."}`（反馈式纠错），每轮开始重置。

## 5. 验证结果（FSMBattle，2026-09-05）

| 项 | 结果 |
|----|------|
| 编译 | 3 轮 `start_compilation_pipeline` 均零错误 |
| HexGrid 构建 | 63 有效格（目标 64）、R=12.1m、边界 X[-61,61] Z[-111,111]；抽查 3/3 格心落在 NavMesh |
| Gizmos | 用户 Scene View 目检确认六边形网格渲染正确 |
| 退化路径（directDegrade=true） | 门控 0s 落定、桩接管、对局开始、无异常 |
| LLM 开局（required） | 红 3 次多队指令 `[1,2]→HQ-A [3,4]→HQ-B [5,6]→HQ-C` 覆盖 6/6（23.8s, tokens 1725/1178）；蓝 `#29/#20/#45` 覆盖 6/6（31.9s, tokens 1737/1543）；门控总耗时 56s |
| 目标同步 | `NetworkCommanderState.objectives` 双方各 6 条，cellId+wx/wz 正确（T0S0→#20(-18,-45) 等） |
| 反馈纠错 | 全歼队报错 / 同回合同队重复下令报错 / 模型正确停手（finish=stop） |
| 战术质量 | 模型 analysis 引用出生格/敌方 GR/HQ 斜线布局做分层布防；Active 轮血战后主动抽队回防 HQ-B |
| 修复前→后 | 开局覆盖 0/6（auto+文本 JSON+空格表）→ 6/6（required+格表+自愈） |
| 门控空闲轮抑制 | 双方落定后各出现一条抑制日志；门控 45s 期间零空闲请求；门控结束才恢复计时 |
| wait 限次 | 二次调用返回 `{"error":"本回合已使用过 wait（每回合限一次）..."}` |
| 动态视觉门 | `GameView_2026-09-05_17-56-18-757.mp4`（12s/120帧，对局实拍） |

## 6. 运行说明

1. **前提**：LM Studio 运行且加载 `qwen3.8-4b`（`GET /v1/models` 可查）；VRAM 紧张时先 `lms unload --all` 再显式加载（PHASE10 教训）。
2. **场景**：FSMBattle 已重装配（HexGrid rig + `directDegrade=false`）。**HGTR / Map_v1 等其他场景需重跑一次装配菜单**换新 rig（旧 Overlay/Cam 组件已是 missing script，Setup 幂等清理重建）；Match 场景走 `HagenDa → Match → Build From Selected MatchConfig`。
3. **临时跳过 LLM**：GateController.`directDegrade=true`（桩接管，GPU 全留游戏）。
4. **日志**：`Logs/Commander/commander_{红|蓝}_*.jsonl`（触发/输入全文/工具执行/响应/tokens）。
5. 冒烟：`HagenDa/Commander/Test LLM Endpoint`（纯文本+工具调用探针）。

## 7. 已知边界与后续可选

- FSMBattle 无真人 → HUD 一行文字为空属预期；目标同步已结构化验证（objectives 6/6），真人场景待回归
- 模型 analysis 的绝对罗盘措辞偶发南北颠倒（相对方位与 ID/距离计算正确）；指令按 ID 寻址不受影响
- `DeployBeacon` kinematic velocity 报错为 PHASE8 遗留，与本次无关
- 后续可选：桩升级为按 POI 重要性派兵；移动 POI 持续追踪（`FSMAIController.RefreshObjective` 引用 `MovingZone`）；content-JSON 回退解析（若未来模型在 required 下仍异常）
- CODELY.md 结构化记忆由用户手动追加（auto-edit 禁用）
