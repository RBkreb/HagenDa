# HagenDa

> A multiplayer first-person shooter (FPS) built on the **Cowsins FPS Engine** framework, networked with **Mirror** and hosted via **Edgegap**. Developed on the **Tuanjie engine** (Unity fork).

## Project Overview

- **Product name:** `HagenDa` (company: `DefaultCompany`)
- **Engine:** Tuanjie (团结引擎) — Unity fork, `2022.3.62t12` / Tuanjie Editor `1.10.0`
- **Render pipeline:** High Definition Render Pipeline (HDRP) `14.2.0-t1` (assigned in `GraphicsSettings.asset`)
- **Genre:** First-person shooter
- **Networking:** [Mirror](https://mirror-networking.gitbook.io/docs/) `96.11.0` (MMO-scale networking library)
- **Hosting:** Edgegap plugin (`EDGEGAP_PLUGIN_SERVERS` scripting symbol)
- **Gameplay framework:** Cowsins FPS Engine (commercial Unity FPS framework) — movement, weapons, enemies, pickups, UI

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
| **OutdoorsScene** | `Assets/OutdoorsScene.scene` | **Main scene / build entry** (only scene in Build Settings). Currently a lighting/environment starter (Sun, Sky and Fog Volume, Main Camera, StaticLightingSky). |
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

## Building & Running

### Editor
1. Open the project in Tuanjie Editor (2022.3.62t12).
2. Open `Assets/OutdoorsScene.scene` (the build entry).
3. Press **Play**.

### Unity Tools Integration
Use the available Unity builtin tools for editor control:

- `unity_editor play` / `unity_editor stop` — enter/exit Play Mode
- `unity_workflow compile_and_validate` — compile scripts and read new console errors
- `unity_scene` — create/load/save scenes, inspect hierarchy
- `unity_asset` / `unity_gameobject` — inspect and edit assets / GameObjects

### Batch mode
No custom build script was found in the project (no `Build/` folder or `BuildScript.cs`). For a batch-mode build, use a generic invocation (adapt the method name once a build script exists):

```bash
Tuanjie.exe -batchmode -quit -projectPath . \
  -executeMethod <BuildScript.MethodName> \
  -buildTarget <Target> -logFile
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
└── OutdoorsScene.scene    # Main scene
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

- **No custom build script** — batch-mode builds need a `BuildScript.cs` under an `Editor/` folder (not yet present).
- **Render pipeline detection discrepancy** — `unity_editor get_state` reports `srp: "builtin"` even though HDRP is installed and assigned in `GraphicsSettings.asset`. Trust `GraphicsSettings.asset` / `HDRPProjectSettings.asset` for pipeline questions.
- **Main scene is a lighting starter** — `OutdoorsScene.scene` currently contains only environment/lighting objects (Sun, Sky and Fog Volume, Main Camera, StaticLightingSky). Actual gameplay objects (player controller, weapons, network manager) have not yet been placed.
- **Target platforms** — not specified in Player Settings (`applicationIdentifier` is empty); likely PC but unconfirmed.
