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

### Feedback

### Project
- [2026-08-18 15:24:15] PHASE5 通用枪械模板已完成并通过测试（2026-08-18），下一阶段为 PHASE6 道具设计。核心实现：WeaponDefinition（数据模板 ScriptableObject）+ NetworkGun（服务端权威枪械状态机，玩家/AI 共用），枪械模型 Assets/Low Poly Weapons VOL.1/Models/M4_8.fbx，资产 Assets/Scripts/Network/Weapons/M4Definition.asset。关键决策：射速 900rpm（偏离规格默认 600，解决散布回复 7°/s 与满速射击散布持续增大的矛盾）；保留子弹穿透（与 PHASE4 一致）；第一人称枪械表现用基础版（挂 M4 模型 + 视觉后座 + 瞄准归中，无枪口火光/换弹动画）。注意：WeaponDefinition 数值改代码字段初始值不会回写已存在的 .asset（序列化值覆盖代码默认），改数值应直接编辑 M4Definition.asset 或删除该资产重建。
### Reference

