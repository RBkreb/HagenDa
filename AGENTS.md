## Agent skills

### Issue tracker

Issues live as markdown files under `.scratch/<feature>/`. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: one `CONTEXT.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.

> Note: `docs/agents/`, `CONTEXT.md`, `docs/adr/`, and `.scratch/` don't exist in the repo yet. If you create them, follow the layout referenced above.

## Project overview

HagenDa is a multiplayer top-down-ish FPS built in **Unity 2022.3.62t12 (Tuanjie fork)** using **Mirror** for networking and **HDRP** for rendering. Custom game code lives under `Assets/Scripts/Network/`. The current (ML-branch) goal is autonomous-combat + squad-cooperation AI trained with ML-Agents, plus a multimodal-LLM **Commander** system.

## Key locations

- `Assets/Scripts/Network/` — all custom runtime scripts (combat, AI, HUD, equipment).
- `Assets/Scripts/Network/Commander/` — LLM commander system (tool-calling agent, snapshot camera, weapon tasks). See `PHASE10.md` for the design.
- `Assets/Scripts/Network/Editor/` — editor menu generators (`NetworkSetup.cs`, `BuildScript.cs`).
- `Assets/Settings/` — scriptable-object configs (e.g. `CommanderConfig.asset`).
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
