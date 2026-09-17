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

Repo root holds code-integration and tooling dirs (`ProjectSettings/`, `Packages/`, `Training/`, `*.csproj`, `HagenDa.sln`) plus `ArtSource/` for source/reference art that Unity does **not** import (map blockout reference images, superseded FBX exports). `MapSource/` is the same idea but gitignored for large binaries. Do not put loose assets at the repo root — this project was previously flattened that way.

Prefabs/materials/data used to sit *inside* `Assets/Scripts/`; they now live in the typed `Assets/Game/` folders. Moving any asset is safe **only** via `AssetDatabase.MoveAsset` (or the Unity editor) because references are GUID-based — moving `.meta` files by hand or via git can still work, but a bare file copy that drops the `.meta` breaks every reference.

## Key locations

- `Assets/Game/Scripts/Network/` — all custom runtime scripts (combat, AI, HUD, equipment).
- `Assets/Game/Scripts/Network/Commander/` — LLM commander system (tool-calling agent, snapshot camera, weapon tasks). See `PHASE10.md` for the design.
- `Assets/Game/Scripts/Network/Editor/` — editor menu generators (`NetworkSetup.cs`, `BuildScript.cs`).
- `Assets/Game/Settings/` — scriptable-object configs (e.g. `CommanderConfig.asset`).
- `Training/config/` — ML-Agents YAML trainer configs.
- `ML-TRAINING.md`, `ML-STATUS-REPORT.md`, `PHASE*.md` — milestone/phase docs. Read the relevant one before touching ML or commander code.

## Build & run

- Editor build menus are generated under `HagenDa/` (e.g. `HagenDa/Setup Commander System`, `HagenDa/Commander/Test LLM Endpoint`).
- No command-line dotnet build is used day-to-day; open the Unity/Tuanjie editor and use a generated menu.

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
