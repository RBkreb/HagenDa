# HagenDa

> A multiplayer first-person shooter (FPS) built on the **Cowsins FPS Engine** framework, networked with **Mirror** and hosted via **Edgegap**. Developed on the **Tuanjie engine** (Unity fork).

## Project Overview

- **Product name:** `HagenDa` (company: `DefaultCompany`)
- **Engine:** Tuanjie (团结引擎) — Unity fork, `2022.3.62t12` / Tuanjie Editor `1.10.0`
- **Render pipeline:** High Definition Render Pipeline (HDRP) `14.2.0-t1` (assigned in `GraphicsSettings.asset`)
- **Genre:** First-person shooter
- **Networking:** [Mirror](https://mirror-networking.gitbook.io/docs/) `96.11.0` (MMO-scale networking library)
- **Hosting:** Edgegap plugin (`EDGEGAP_PLUGIN_SERVERS` scripting symbol)
- **Movement:** self-made force-driven capsule Rigidbody (3C: walk / sprint / jump / slide / dive; stand / crouch / prone) — see PHASE2
- **Gameplay framework:** Cowsins FPS Engine (commercial Unity FPS framework) — weapons, enemies, pickups, UI (movement replaced by self-made)

### Scripting Define Symbols

```
MIRROR
MIRROR_89_OR_NEWER
MIRROR_90_OR_NEWER
MIRROR_93_OR_NEWER
MIRROR_96_OR_NEWER
EDGEGAP_PLUGIN_SERVERS
```

## Key Scenes & Entry Point

| Scene | Path | Purpose |
|-------|------|---------|
| **PhysicsMovement** | `Assets/Scenes/PhysicsMovement.scene` | **Primary movement test scene.** Self-made 3C player (force-driven capsule + stand/crouch/prone/slide/dive), spawn points, floor, wall, shootable targets. |
| **OutdoorsScene** | `Assets/OutdoorsScene.scene` | **Build entry** (only scene in Build Settings). Lighting/environment starter (Sun, Sky and Fog Volume, Main Camera, StaticLightingSky). |
| MainMenu | `Assets/Cowsins/Demo/MainMenu.unity` | FPS Engine main menu |
| Showroom | `Assets/Cowsins/Demo/Showroom.unity` | FPS Engine weapon showcase |
| MovementShowroom | `Assets/Cowsins/Demo/MovementShowroom.unity` | Movement demo |
| BlankScene | `Assets/Cowsins/Demo/BlankScene.unity` | Empty starter scene |
| LowPolyFPS_Lite_Demo | `Assets/LowPolyFPSLite/Scenes/*.unity` | Third-party low-poly FPS demo scenes |
| Sample_Scene | `Assets/Low Poly Weapons VOL.1/Sample_Scene.unity` | Low-poly weapons sample |
| Mirror examples | `Assets/Mirror/Examples/**/Scenes/*.unity` | Mirror networking sample scenes (many) |

> Note: Tuanjie engine uses the `.scene` extension for its own scenes (e.g. `OutdoorsScene.scene`); Unity-engine assets use `.unity`. When searching for scenes, check both `*.scene` and `*.unity`.

## Core Assets

### Cowsins FPS Engine (`Assets/Cowsins/`)
The gameplay framework. Key subsystems under `Assets/Cowsins/Scripts/`:

- **Managers/** — `GameSettingsManager`, `InputManager`, `SoundManager`, `PoolManager`, `CoinManager`, `ExperienceManager`, `AddonManager`, `DeviceDetection`
- **Player/** — `PlayerControl`, `PlayerStats`, `PlayerMovement`-related providers (`IPlayerControlProvider`, `IPlayerStatsProvider`, `IPlayerMultipliers`, `IFallHeightProvider`), `PlayerDependencies`, `PlayerGraphics`, `PlayerOrientation`
- **Movement/** — `PlayerMovement`, `MovementContext`, `PlayerMovementSettings`, `PlayerMovementEvents`, `IPlayerMovementProvider`
- **Weapons/** — `WeaponController`, `Weapon_SO` (ScriptableObject), `Bullet`, `WeaponAnimator`, `WeaponIdentification`, plus `ShootStyles/` and `Attachments/`
- **Enemies/** — `EnemyHealth`, `IDamageable`, `Turret`, `CircularTargetEnemy`, `TrainingTarget`
- **UI/** — `UIController`, `Crosshair`, `Hitmarker`, `WeaponsInventoryUISlot`, `CowsinsButton`, `RebindUI`, `UIEffects`, `UIEvents`
- **PickUpSystem/** — pickup interactions
- **Effects/**, **Camera/**, **Behaviours/**, **Extra/** — supporting systems

Key prefabs:

- `Assets/Cowsins/Prefabs/PlayerControllers/CowsinsFPSController.prefab` — main FPS controller
- `Assets/Cowsins/Prefabs/PlayerControllers/MovementCowsinsFPSController.prefab`
- `Assets/Cowsins/Prefabs/Weapons/*.prefab` — Rifle, Pistol, Shotgun, MP5, Revolver, BurstRifle, Katana, RocketLauncher, blank template
- `Assets/Cowsins/Prefabs/Arms/`, `Bullets/`, `Projectiles/`, `Models/`

ScriptableObjects: `Assets/Cowsins/ScriptableObjects/{Weapons,Bullets,AttachmentsIdentifiers}/`

### Input
- New Input System (`com.unity.inputsystem` `1.14.4-t3`)
- Input actions: `Assets/Cowsins/Inputs/PlayerActions.inputactions` (generated `PlayerActions.cs`)

### Mirror Networking (`Assets/Mirror/`)
- Version `96.11.0`
- Transports: KCP (`kcp2k`), SimpleWeb, Telepathy, plus an Encryption transport
- Hosting: `Assets/Mirror/Hosting/Edgegap/`
- Authenticators: `Assets/Mirror/Authenticators/`
- Extensive examples under `Assets/Mirror/Examples/`
- Script templates under `Assets/ScriptTemplates/` (e.g. Network Manager, Network Behaviour, Network Room Manager, Network Transform)

### AI Generation (TJGenerators)
- Local UPM package `cn.tuanjie.ai.generators` (resolved from `.codely-cli/extensions/TJGenerators/Packages/cn.tuanjie.ai.generators`)
- Enables in-editor AI asset generation (models, images, materials, sprites, audio, video, etc.)

## Multiplayer Networking (`Assets/Scripts/Network/`)

Server-authoritative multiplayer built on Mirror. The player avatar is a **self-made force-driven capsule Rigidbody** (NOT Character Controller): manual gravity + driving force + speed clamp, with a stand / crouch / prone posture state machine, slide and dive.

### Authority model (Mirror "Option A")
- **Movement / physics / posture / hit-detection / health**: server-authoritative. The client only sends intent; the server applies forces, switches colliders, runs the posture state machine, and does the hitscan.
- **Aim (look)**: client-authoritative. The owning client renders its camera from raw mouse delta every frame and sends its **absolute** `yaw`/`pitch` up; the server adopts that view verbatim (no delta-accumulation / packet-loss drift), then uses it to derive movement direction and shoot direction.

### Input flow
- `Update()` samples all input every rendered frame (edge-triggered inputs like jump / crouch-toggle / prone-toggle are latched, never missed).
- `FixedUpdate()` sends an unreliable `[Command] CmdInput(NetworkInputState)` at 60 Hz server tick.
- `NetworkTransformReliable` (SyncDirection = `ServerToClient`) syncs position + yaw down; `pitch`, `posture`, `sliding` sync via `[SyncVar]`.
- Client render rate capped at 180 Hz (`Application.targetFrameRate` + vsync off); server tick = 60 Hz (Mirror `sendRate`).

### PHASE2 3C movement (see `PHASE2.md`)
- **Postures**: stand (1.8m) / crouch (0.9m) / prone (0.5m). Stand & crouch use separate upright capsules (stand + crouch collider, one enabled at a time); prone rotates the stand collider flat. Overhead clearance ray blocks standing/crouching under low ceilings.
- **Movement**: walk / sticky sprint (forward+shift, exits only on forward release) / jump (height-based impulse) / slide (ctrl + speed, crouch collider, no clamp, 1s cooldown) / dive (prone + 15 m/s downward slam).
- **Speeds**: stand 3.5 walk / 7.5 sprint; crouch 2 / 4.5; prone 0.5 m/s. Zero-friction `PhysicMaterial` on both capsules (all friction is manual forces).
- **Camera**: below capsule top by 0.15m, lerps between postures, shakes on jump / slide / dive.

### Files
| File | Purpose |
|------|---------|
| `NetworkPlayerController.cs` | Server-authoritative force-driven 3C controller (movement, posture state machine, slide/dive/jump, overhead clearance, server hitscan) |
| `NetworkPlayerHealth.cs` | Server-authoritative health (SyncVar), death/respawn |
| `NetworkShootableTarget.cs` | Static networked target for verifying hitscan |
| `NetworkInputState.cs` | Serialized input snapshot struct |
| `DebugHud.cs` | Top-right debug HUD (3D speed, 3s max speed, posture, slide state) |
| `Physics/PlayerNoFriction.physicMaterial` | Zero-friction material for both capsules |
| `Editor/NetworkSetup.cs` | `HagenDa/Setup Multiplayer Scene`, `Create Physics Movement Scene`, `Rebuild NetworkPlayer Prefab` |
| `Editor/BuildScript.cs` | `HagenDa/Build Windows Client` — builds standalone Windows client for 2-end testing |
| `Prefabs/NetworkPlayer.prefab` | Generated networked player prefab |

### Two-end testing
- Editor runs as **Host**; a standalone client build (`Build/Client/HagenDa.exe`) connects as **Client**.
- Sensitivity is calibrated: `lookSensitivity = 0.05793` (2500 DPI, 4.56 cm = 260°, linear).

## Building & Running

### Editor
1. Open the project in Tuanjie Editor (2022.3.62t12).
2. Open `Assets/Scenes/PhysicsMovement.scene` (movement test) or `Assets/OutdoorsScene.scene` (build entry).
3. Press **Play** (start Host via the NetworkManager HUD).

### Unity Tools Integration
Use the available Unity builtin tools for editor control:

- `unity_editor play` / `unity_editor stop` — enter/exit Play Mode
- `unity_workflow compile_and_validate` — compile scripts and read new console errors
- `unity_scene` — create/load/save scenes, inspect hierarchy
- `unity_asset` / `unity_gameobject` — inspect and edit assets / GameObjects

### Batch mode
Custom build script at `Assets/Scripts/Network/Editor/BuildScript.cs` (`HagenDa.Networking.EditorTools.BuildScript.PerformBuild`), menu item `HagenDa/Build Windows Client`:

```bash
Tuanjie.exe -batchmode -quit -projectPath . \
  -executeMethod HagenDa.Networking.EditorTools.BuildScript.PerformBuild \
  -logFile
```

## Development Conventions

### Folder Hierarchy
```
Assets/
├── Cowsins/               # FPS Engine framework (third-party, do not modify casually)
├── Mirror/                # Networking library (third-party)
├── LowPolyFPSLite/        # Third-party low-poly FPS demo
├── Low Poly Weapons VOL.1/# Third-party weapons pack
├── ScriptTemplates/       # Mirror script templates (right-click → Create)
├── Settings/              # HDRP pipeline assets & profiles
├── Scenes/                # Self-made scenes (PhysicsMovement.scene)
├── Scripts/Network/       # Self-made networking + 3C movement
│   ├── Physics/           # Zero-friction PhysicMaterial
│   └── Editor/            # NetworkSetup / BuildScript
└── OutdoorsScene.scene    # Build entry scene
```

### Assembly Definition Files (`*.asmdef`)
- Mirror modules are split into assemblies: `Mirror`, `Mirror.Components`, `Mirror.Authenticators`, `Mirror.CompilerSymbols`, `Mirror.Editor`, `Mirror.Examples`, `Mirror.Transports`, `Unity.Mirror.CodeGen`, `Edgegap`, `KCP`, `SimpleWebTransport`, `Telepathy`, `EncryptionTransportEditor`.
- The Cowsins FPS Engine code and project-level gameplay scripts compile into the default `Assembly-CSharp` (no asmdef).

### Code Style
- C# classes use `PascalCase`; serialized/private fields use `camelCase`.
- Editor-only scripts live under `*/Editor/` folders.
- ScriptableObjects (`*_SO`, `*Settings`) are used for data-driven configuration (weapons, movement settings).
- Input is handled through the new Input System (`.inputactions`).

## Package & Dependency List

From `Packages/manifest.json` (notable):

| Package | Version | Notes |
|---------|---------|-------|
| `com.unity.render-pipelines.high-definition` | 14.2.0-t1 | HDRP |
| `com.unity.inputsystem` | 1.14.4-t3 | New Input System |
| `com.unity.textmeshpro` | 3.0.10 | TextMeshPro |
| `com.unity.ugui` | 2.0.0 | uGUI |
| `com.unity.timeline` | 1.7.7 | Timeline |
| `com.unity.visualscripting` | 1.9.11 | Visual Scripting |
| `com.unity.nuget.newtonsoft-json` | 3.2.1 | JSON |
| `cn.tuanjie.ai.generators` | file: (local) | TJGenerators AI generation |
| `cn.tuanjie.codely.bridge` | 1.0.75 | Codely Bridge (editor automation) |

Plus standard `com.unity.modules.*` engine modules.

## Version-Control Tips

- Ignore: `Library/`, `Temp/`, `Logs/`, `obj/`, `Build/`, `UserSettings/`, `mem-log/`.
- Keep under version control: `Assets/`, `Packages/manifest.json`, `ProjectSettings/`, and any `README.*`.
- Ensure `.meta` files are committed (Unity requires them for stable asset GUIDs).
- The `.codelyignore` file controls which files Codely indexes (see https://codely.tuanjie.cn).

## TODO / Open Questions

- **Render pipeline detection discrepancy** — `unity_editor get_state` reports `srp: "builtin"` even though HDRP is installed and assigned in `GraphicsSettings.asset`. Trust `GraphicsSettings.asset` / `HDRPProjectSettings.asset` for pipeline questions.
- **Target platforms** — not specified in Player Settings (`applicationIdentifier` is empty); likely PC but unconfirmed.
- **3C animation** — character skeleton/model (PHASE2 "character" section) not yet implemented; movement is physics-only.

## Codely Structured Memories

### User
优先使用 codegraph 辅助代码读取与分析。项目已初始化索引（`.codegraph/`，，DB 已被 `.codegraph/.gitignore` 忽略）。**Why:** 号查询 + 调用链分析比通篇 read_file/grep 更快更准。**How to apply:** 本项目中需要定位符号、读代码、分析调用关系或改动影响面时，先走 codegraph CLI（`codegraph query/node/explore/callers/callees/impact`），再按需 read_file 具体片段；代码改动后可用 `codegraph sync` 同步索引。
### Feedback

### Project
- [2026-08-18 15:24:15] PHASE5 通用枪械模板已完成并通过测试（2026-08-18），下一阶段为 PHASE6 道具设计。核心实现：WeaponDefinition（数据模板 ScriptableObject）+ NetworkGun（服务端权威枪械状态机，玩家/AI 共用），枪械模型 Assets/Low Poly Weapons VOL.1/Models/M4_8.fbx，资产 Assets/Scripts/Network/Weapons/M4Definition.asset。关键决策：射速 900rpm（偏离规格默认 600，解决散布回复 7°/s 与满速射击散布持续增大的矛盾）；保留子弹穿透（与 PHASE4 一致）；第一人称枪械表现用基础版（挂 M4 模型 + 视觉后座 + 瞄准归中，无枪口火光/换弹动画）。注意：WeaponDefinition 数值改代码字段初始值不会回写已存在的 .asset（序列化值覆盖代码默认），改数值应直接编辑 M4Definition.asset 或删除该资产重建。
- [2026-08-27] PHASE10 多模态 LLM 指挥官系统已完成并通过运行验收。实现：`Assets/Scripts/Network/Commander/`（CommanderOrchestrator 红蓝各一 + LlmRestClient 直连 LM Studio localhost:1234 的 OpenAI 兼容端点 + CommanderMapCamera 快照 + 三武器系统 + 开局门控）。装配菜单 `HagenDa/Setup Commander System`；运行日志 `Logs/Commander/*.jsonl`（已 gitignore）。关键实测事实：LM Studio 上 VLM 原生 tool_calls 可用、工具结果内嵌图片会被 400 拒绝（get_snapshot 用"回执文本+追加 user 图消息"实现）、思考型模型 reasoning_content 占 max_tokens 预算、`tool_choice` 仅支持 none/auto/required、模型会输出位置参数与负值坐标（解析器已做位置回退+网格代号 cell 方案兜底）。LLM for Unity v3.0.3 已装但本项目不使用（其远程模式只说 llama.cpp 协议且无视觉路径）。易踩坑备忘：①跨域重载存活的数据必须 `[SerializeField]` 字段而非 auto-property；②Start 时 NetworkServer.active 尚未激活（TrainingAutoHost 时序），服务端组件须惰性初始化；③零摩擦材质下冻结实体必须显式刹停水平速度，否则滑向四角。
- [2026-08-27 17:30] PHASE10 实测调优追加（模型切换至 qwen3.5-4b，16384 上下文，关闭思考模式）：①视觉精度不足 → **网格代号方案**：快照每格中心印 A1..F11（列字母+行数字），squad_order/commander_weapon 参数改 cell 字符串，解析层 cell→格中心——LLM 下令从"测量米坐标"降维成"识字"，彻底规避负值/偏移问题；②**开局部署单发制**：每方仅一次对话请求，响应内 tool_calls 全部执行后立即落定解除门控（此前 Opening 相位 IsDue 恒真 + 催促轮导致重复部署请求打转）；③同回合同小队二次 squad_order 直接报错防打转；④传输层从 UnityWebRequest 迁移到后台线程 HttpClient——Play 模式下 UWR 大体积 base64 上传被主循环泵制约束导致 LM Studio 端停在 0%，停 Play 秒恢复；⑤**8GB VRAM 教训**：Unity Play(HDRP) + LM Studio 权重/KV 共存极紧张，换模型/扩上下文须 `lms unload --all` 后 `lms load <id> --context-length=16384 --gpu max` 显式控制并建议停 Play 操作；⑥qwen3.5-4b 为多模态但需在 LM Studio 安装 vision 解码器插件；⑦error 文案提取需对 `{"error":"字符串"}` 形态做类型判断，否则 "Cannot access child value" 次生异常掩盖真实服务端错误。
### Reference


- [2026-08-27 17:30] PHASE10 实测调优追加（qwen3.5-4b:2 → qwen3.5-4b 16384ctx）：①视觉精度不足 → 网格代号方案（快照每格中心印 A1..F11，squad_order/commander_weapon 参数改 cell，解析层 cell→格中心），LLM 下令从"测量"降维成"识字"；②开局部署=单发制——每方仅一次对话请求，响应中 tool_calls 全部执行后立即落定解除门控，取消催促轮（之前 Opening 相位 IsDue 恒真 + nudge 第二轮导致多次部署请求打转）；③同回合同小队二次 squad_order 直接报错防打转；④传输层从 UnityWebRequest 迁移到后台线程 HttpClient——Play 模式下 UWR 大体积上传被主循环泵制约束导致 LM Studio 卡 0%，停 Play 秒恢复；⑤VRAM 教训：8GB 卡 = Unity Play(HDRP) + LM Studio 权重/KV 共存极紧张，换模型/扩上下文必须用 lms load --context-length 显式控制并在进 Play 前预载；⑥qwen3.5-4b 是多模态但需 LM Studio 安装 vision 解码器插件；⑦error 提取要对 {"error":"字符串"} 形态做类型判断，否则次生 "Cannot access child value" 掩盖真实错误。
- Kevin Iglesias 触发型控制器必须每帧重发 weapon/posture/movement 组合条件
- Mirror host 模式下 Spawn 后立即改 SyncVar 会被本地载荷反序列化覆盖，须在 OnStartServer 内赋值或延迟设置。