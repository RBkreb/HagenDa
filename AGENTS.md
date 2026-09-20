## Agent skills

### Issue tracker

Issues live as markdown files under `.scratch/<feature>/`. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: one `CONTEXT.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.

> Note: `docs/agents/`, `CONTEXT.md`, `docs/adr/`, and `.scratch/` don't exist in the repo yet. If you create them, follow the layout referenced above.

## Project overview

HagenDa is a multiplayer top-down-ish FPS built in **Unity 2022.3.62t12 (Tuanjie fork)** using **Mirror** for networking and **HDRP** for rendering. Custom game code lives under `Assets/Game/Scripts/Network/`. The current (ML-branch) goal is autonomous-combat + squad-cooperation AI trained with ML-Agents, plus a multimodal-LLM **Commander** system.

## Asset layout (`Assets/`)

Three roots, split by ownership — keep new files in the matching root:

- `Assets/Game/` — **all first-party content and code.** Code lives under `Assets/Game/Scripts/`; art, audio, prefabs and data are split by type alongside it (`Prefabs/`, `Materials/`, `Equipment/`, `Animation/`, `Scenes/`, `Settings/`, `Audio/`, `Models/`, `Characters/`, `Environment/`, `VFX/`, `Match/`, `TrainingMaps/`, `Weapons/`, `Physics/`, `MLModels/`). `_Archive/` holds retired files (e.g. `Network.zip`).
- `Assets/ThirdParty/` — imported/vendored packs, never hand-edited: `Mirror/`, `TextMesh Pro/`, `Kevin Iglesias/`, `Low Poly Weapons VOL.1/`, `RPG_FPS_game_assets_industrial/`, `SimpleNaturePack/`, `Skyboxes Pack/`, `FX_Kandol_Pack/`, `LowPolyFPSLite/`, `ParticlePack/`.
- `Assets/` root — Unity- and package-mandated folders only: `ScriptTemplates/`, `StreamingAssets/`, `ML-Agents/`, `TJGenerators/`, `RigGraph.Generated/`, `Unity.VisualScripting.Generated/`, `Temp/`, `Tests/`. `TJGenerators/` and `RigGraph.Generated/` are written by their packages at hardcoded paths — do not move them.

Repo root holds code-integration and tooling dirs (`ProjectSettings/`, `Packages/`, `Training/`) plus `ArtSource/` for source/reference art that Unity does **not** import (map blockout reference images, superseded FBX exports). `MapSource/` is the same idea but gitignored for large binaries. The editor also regenerates `*.csproj` and `HagenDa.sln` here on open; they are **gitignored and untracked** — the assembly layout is defined by the `.asmdef` files, not by those generated projects, so never hand-edit them or commit them. Do not put loose assets at the repo root — this project was previously flattened that way.

Prefabs/materials/data used to sit *inside* `Assets/Scripts/`; they now live in the typed `Assets/Game/` folders. Moving any asset is safe **only** via `AssetDatabase.MoveAsset` (or the Unity editor) because references are GUID-based — moving `.meta` files by hand or via git can still work, but a bare file copy that drops the `.meta` breaks every reference.

## Script layout (`Assets/Game/Scripts/`)

First-party code lives in **named assembly definitions**, not the predefined
`Assembly-CSharp`. There are four asmdefs plus four asmrefs:

| Assembly | Defined by | Covers |
| --- | --- | --- |
| `HagenDa.Game` | `Assets/Game/Scripts/HagenDa.Game.asmdef` | every non-`Editor` script under `Network/`, `Animation/`, `Environment/`, `Vfx/` |
| `HagenDa.Game.Editor.Network` | `Network/Editor/HagenDa.Game.Editor.Network.asmdef` | `Network/Editor` (+ `Builders/`), with `Network/Commander/Editor` and `Network/Match/Editor` **asmref**-joined into it |
| `HagenDa.Game.Editor.Animation` | `Animation/RigDriver/Editor/…asmdef` | `Animation/RigDriver/Editor`, with `RigGraph/Editor` and `Showcase/Editor` asmref-joined |
| `HagenDa.Game.Editor.Vfx` | `Vfx/Editor/HagenDa.Game.Editor.Vfx.asmdef` | `Vfx/Editor` |

Two constraints are **not** preferences — they are forced, and changing them breaks the build:

1. **Runtime code must stay in one assembly.** `Network` and `Animation` reference each
   other both ways (`NetworkPlayerController` ↔ `SoldierAnimatorDriver`,
   `NetworkGun` ↔ `SoldierRigDriver`). Splitting them yields
   `Assembly with cyclic references detected`. Do not create a second runtime asmdef
   without first refactoring those cycles away.
2. **The editor folders are merged with `.asmref`, not separate asmdefs.**
   `MatchSceneBuilder` calls ~28 `internal` members of `NetworkSetup`. `internal` is
   assembly-scoped, so separate assemblies would need `InternalsVisibleTo` or a
   visibility change. The asmref keeps them in one assembly and touches no game code.

Movable but with rules: a `.cs` may be moved freely **as long as its `.cs.meta` moves
with it** (references are GUID-based). Crossing an `Editor/` boundary or a new asmdef
boundary changes which assembly it compiles into, so keep editor-only code inside
`Editor/` folders. The asmdefs are `autoReferenced: true` so `Assembly-CSharp`
(generated VisualScripting providers, `ScriptTemplates/`) can still use game types.

Folders carry the responsibility grouping; namespaces are aligned to them:

```
Network/
  Core/        NetworkInputState, IDamageable                     (shared contracts)
  Player/      NetworkPlayerController, NetworkPlayerHealth, NetworkCombatant, NetworkCombat
  Combat/      NetworkGun, NetworkBullet, NetworkEquipment, *Definition, Loadout*, hitboxes
  Throwables/  NetworkThrowable + its 6 subclasses
  Effects/     NetworkExplosion, NetworkEmpField, NetworkSmoke(+Volume), SpecialCover
  Deployment/  Deployable*, DeployBeacon, LargeSupplyCrate, NetworkInterceptor, SensorProbe
  Match/       NetworkMatchManager, CapturePoint, GarrisonZone, StrategicZone, MovingZone,
               MatchConfig, MapDefinition, Editor/MatchSceneBuilder
  AI/          Brains/ (3 interchangeable brains)  Perception/  Tactics/  Training/ (ML)
  Hud/         GameHud, PlayerHud, DebugHud, FSMStatsHud, HudMath, map/head markers
  Dev/         FreeCamera, MirrorView, AnimationTestAutoDeploy, NetworkShootableTarget
  Commander/   LLM commander system. See `PHASE10.md` for the design.
  Editor/      NetworkSetup partials + Builders/ (see below), BuildScript
Animation/
  Rigging/     reusable Animation-Rigging constraint jobs — SHARED by both anim impls
  RigGraph/    animation implementation A (PHASE13): SoldierRigSetup + NetworkSoldierAnimator
  RigDriver/   animation implementation B (PHASE14, current/live): SoldierRigDriver + SoldierAnimatorDriver
  Cameras/     AimController, HeadCamFollow, ThirdPersonCam
  Showcase/    ShowcaseLoopState + Nailong/HandGrip demo builders
Vfx/           InfinityVfxFactory (battle VFX prefab factory)
Environment/   TreePhysicsProxy
```

**Do not "deduplicate" the animation folders.** `RigGraph` and `RigDriver` are two
deliberate, parallel implementations of the same feature (PHASE13 vs PHASE14), not
duplicated responsibility. `RigDriver` is the live one (attached to the player/AI
prefabs and every battle scene); `RigGraph` is retained on purpose. The same applies
to the three AI brains in `Network/AI/Brains/` — `NetworkAIController` (intent host),
`FSMAIController` (FSM), `ScriptedAIController` (sparring) are alternative
implementations selected per scene.

### `Network/Editor/` — the editor toolkit

`NetworkSetup` is a single `static partial` class spread by concern so no file holds
the whole toolkit. Callers (`MatchSceneBuilder`, etc.) still write
`NetworkSetup.SomeMember(...)`; only the files changed, not the API. Its members are
`internal`, which is why `Network/Match/Editor` is asmref-joined to this assembly
rather than being its own (see the assembly table above).

| File | Contents |
| --- | --- |
| `NetworkSetup.cs` | index/shell only — the class overview |
| `NetworkSetupPaths.cs` | asset path constants; `EnsureFolder`, `EnsureMapLayers`, `EnsureLayerNamed`, scene save/dirty |
| `EditorScenePrimitives.cs` | spawns, walls, lighting, zones, garrisons, capture points, map geometry, NavMesh |
| `EditorPrefabFactory.cs` | player, AI-entity, bullet, throwable and deployable prefabs |
| `EditorEquipmentFactory.cs` | equipment / weapon / loadout assets, persistent materials |
| `EditorSoldierModelFactory.cs` | soldier model attach, Fatui rig + hitbox bake |
| `Builders/MultiplayerSceneBuilder.cs` | `HagenDa/Setup Multiplayer Scene`, physics, animation-test, solo FPS, rebuild player |
| `Builders/TrainingSceneBuilder.cs` | `HagenDa/Create ML Training Scene`, S1 training |
| `Builders/BattleSceneBuilder.cs` | FSM battle scenes (59 AI, all-support, HGTR, Map_v1) |
| `Builders/PhaseSceneBuilder.cs` | Phase3/5/6/7/8 and match scenes |

## Key locations

- `Assets/Game/Settings/` — scriptable-object configs (e.g. `CommanderConfig.asset`).
- `Training/config/` — ML-Agents YAML trainer configs.
- `ML-TRAINING.md`, `ML-STATUS-REPORT.md`, `PHASE*.md` — milestone/phase docs. Read the relevant one before touching ML or commander code.
- `MODEL-PORT-CHECKLIST.md` — model porting state; `PHASE14.md` — the live animation design.

## Build & run

- Editor build menus are generated under `HagenDa/` (e.g. `HagenDa/Setup Commander System`, `HagenDa/Commander/Test LLM Endpoint`).
- No command-line dotnet build is used day-to-day; open the Unity/Tuanjie editor and use a generated menu.

## Tests

Unity Test Framework (`com.unity.test-framework` 1.4.6) tests live under `Assets/Tests/`.
Test code is only discoverable because it sits in its own assemblies — an assembly
referencing `nunit.framework` is what the Test Runner looks for.

| Assembly | Platform | Contents |
| --- | --- | --- |
| `HagenDa.Tests.EditMode` | Editor only | 113 cases: wire contracts, hitbox/loadout/weapon math, smoke-volume geometry, HUD colour, strategic zones, and data-asset invariants |
| `HagenDa.Tests.PlayMode` | any | 79 cases: the server-authoritative layer — input path, match logic, zones, damage, plus the original smoke checks |
| `HagenDa.Tests.Tools` | Editor only | `TestRunnerCli` — drives `TestRunnerApi` and writes `.scratch/testrun/<mode>.{result,done}` |

EditMode and PlayMode are **separate assemblies by necessity**: EditMode requires
`includePlatforms: ["Editor"]` and `UnityEditor.TestRunner`, while PlayMode must not
restrict platforms and cannot reference `UnityEditor.TestRunner`. Do not merge them.

Run them in **Window > General > Test Runner** (the agreed workflow). For headless or
scripted runs, execute `HagenDa.Tests.Tools.TestRunnerCli.RunEditMode()` /
`RunPlayMode()` from the editor, then read `.scratch/testrun/`. There is no
`-runTests` CLI wrapper and no CI — see `docs/adr/0002-assembly-definitions-and-tests.md`.

### PlayMode harness (`Assets/Tests/PlayMode/Harness/`)

Testing the networked layer needs a live server, so the harness boots one with **no
socket and no NetworkManager**: it installs a stub `Transport`, sets
`NetworkServer.listen = false` (so `Listen` skips `Transport.ServerStart()` and never
binds a port), and calls `NetworkServer.Listen`. `PlayModeServer.StopServer()` must run
in teardown — `NetworkServer.Shutdown()` resets `listen` to `true`, so the harness
re-suppresses it to avoid a real bind on the next test.

Mirror's two distinct gates both matter, and mixing them up is the main source of
confusing failures:
- `[Server]` checks the **global** `NetworkServer.active`. Without an active server these
  methods silently do nothing (warning only).
- `isServer` is **per object** and becomes true only after `NetworkServer.Spawn`, which
  is also the only thing that runs `OnStartServer` (the initializer for health, ammo,
  pools, registration).

`PlayerFixture` / `PlayModeServer.SpawnComponent` therefore build entities **inactive,
then add every component, then spawn**. Creating a `NetworkIdentity` on an already-active
GameObject makes Mirror cache an empty behaviour list in `Awake`, after which
`OnStartServer` is never invoked on the rest — with no error at all. Keep that order.

Timing: `CapturePoint` contention accumulates per frame via `Time.deltaTime`, and
grounding needs real physics steps, so these tests poll with `WaitUntil` rather than
counting frames.

### Load-bearing guards

`WireContractTests.GameCode_LivesInNamedAssembly_NotPredefined` and
`MirrorWeaver_InjectedGeneratedNetworkCode` (EditMode) protect the assembly layout: the
latter is the only automated check that Mirror's IL weaver still processes the game
assembly — if `HagenDa.Game` ever loses its `Mirror` asmdef reference, weaving fails
*silently* (compiles fine, breaks at runtime), and that test is what catches it.

`AIInputPathTests` (PlayMode) pins the architecture rule that humans and FSM AI share
one server input path: both `CmdInput` and `SetIntent` end at the same
`pendingServerInput → SimulateServer` funnel, so that one test covers both producers.
Device sampling (`Keyboard.current` / `Mouse.current`) is deliberately **not** tested.

## ML-Agents environment (important)

- Unity ML-Agents packages are local: `file:D:/HagenTa/ml-agents-release_20` (`com.unity.ml-agents` 2.3.0-exp.3). Do not expect them from the registry.
- The **training venv is `py3.9`** with `mlagents` 0.30.0 / `mlagents-envs` 0.30.0 / `torch 2.8.0+cu126`. The repo-root `venv/` is **py3.12 and must not be used** for training.
- Run training like: `PYTHONUTF8=1 mlagents-learn Training/config/HagenDaSquad_S1.yaml --run-id=<id>`.
- Config/YAML files must be **pure ASCII** — on Chinese Windows the default GBK locale breaks YAML decoding otherwise.
- Known pitfalls: `--timeout` is not a 0.30 CLI arg; a Unity license popup freezes the main thread and can time out the trainer handshake.
- 694-dim vector observation + mixed continuous-6/discrete-6 action; behavior name `HagenDaSquad`, `DecisionPeriod=10` (6 Hz).

## Architecture / conventions

- **Server-authoritative intent injection**: AI bodies use the same `NetworkPlayerController` as players. `NetworkAIController.SetIntent(NetworkInputState)` feeds server-side `SetServerInput`, identical semantics to a human's `CmdInput`. Any new AI control path should go through this, not a bespoke controller.
- **Edge actions auto-clear**: intent flags pushed via `SetIntent` are cleared after one tick (equivalent to a single key press).
- **Rewards must be "effect-only"** (no reward for the action itself) to prevent reward-hacking. All rewards funnel through `RewardBus` → `MLAgentBridge.AddReward`.
- Avoid calling `AddReward` inside `CollectObservations` — it misaligns reward↔action causality. Put it at the end of `OnActionReceived`.
- `MLAgentBridge` is the single bridge between the Unity agent and the trainer; keep observation/action interfaces stable across phases (A/B trainer swap is a zero-Unity-change drop).

## Gotchas

- `RewardBus.Reset()` must NOT clear its registered bridge map — clearing it silently drops all subsequent rewards (was the cause of a dead training run).
- S1 eval scenes: the serialized ONNX model doesn't always apply at runtime; you may need to set the model at runtime.
- When a serious config change happens, retrain from scratch — a critic polluted by an all-negative signal will not recover.

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **HagenDa**.

> Index stale? Run `node .gitnexus/run.cjs analyze --index-only` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? Bootstrap with `npx`, `bunx`, or `pnpm dlx` — e.g. `bunx gitnexus@latest analyze` (npm 11 npx crash; #1939).

## Always Do

- **MUST run impact before editing.** Use `impact({target: "symbolName", direction: "upstream"})` or `node .gitnexus/run.cjs impact "symbolName" --direction upstream --repo .`; report callers, processes, and risk. Never substitute grep for graph analysis.
- **MUST analyze graph changes before committing.** Use `detect_changes({scope: "all"})` (MCP) or `node .gitnexus/run.cjs detect-changes --scope all --repo .` (CLI fallback). `partial: true` or `truncated: true` is not a clean check — a zero means unseen, not unaffected; re-run it. For regression review: `detect_changes({scope: "compare", base_ref: "main"})` or `node .gitnexus/run.cjs detect-changes --scope compare --base-ref "main" --repo .`.
- MUST warn on HIGH/CRITICAL `risk` pre-edit; never use `riskSharedAxes` to waive a HIGH/CRITICAL `risk` warning. Compare File/symbol: MCP File omits axes; Graph-RAG expands File.
- **MUST treat `risk: UNKNOWN` as unresolved, not as low.** An empty caller set is not evidence the symbol is unused — it can also mean the callers are not resolvable by the index (plain-object property access, dynamic dispatch, cross-language calls). `impact` pairs `UNKNOWN` with a `riskNote` saying so. Confirm with a text search before treating the symbol as safe to change or delete; do not proceed on the strength of a zero.
- **MUST use `query({search_query: "concept"})` for concepts/flows, `context({name: "symbolName"})` for a named symbol, or `impact` for blast radius, on read-only callers, dependencies, imports, or execution flow.** Graph first; text search only for empty/`UNKNOWN`/literals.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method before MCP/CLI impact analysis.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis, and never read `UNKNOWN` as an all-clear — it means the walk could not answer, which is the one verdict that requires confirming by other means.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit before MCP/CLI graph change analysis.

## Resources

| Resource | Use for |
| --- | --- |
| `gitnexus://repo/HagenDa/context` | Codebase overview, check index freshness |
| `gitnexus://repo/HagenDa/clusters` | All functional areas |
| `gitnexus://repo/HagenDa/processes` | All execution flows |
| `gitnexus://repo/HagenDa/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
| --- | --- |
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
