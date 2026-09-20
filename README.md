# HagenDa

Multiplayer top-down FPS built on **Tuanjie 2022.3.62t12** (Unity fork) with **Mirror** networking and **HDRP** rendering.

## Branches

| Branch | Role |
| --- | --- |
| **`ML-branch`** | **Main development branch.** All active work lands here — ML-Agents autonomous combat AI, squad cooperation, and the multimodal-LLM Commander. |
| `main` | Legacy baseline, kept for reference only. |
| `GOAP-backup` | Archived snapshot of the PHASE9 GOAP tactical-AI work. |

`ML-branch` is where development happens; `main` and `GOAP-backup` are historical and
receive no new commits.

## Working with this repository

- **Git LFS is configured.** `.gitattributes` routes large binary assets (textures,
  meshes, audio, video, archives) through Git LFS, so run `git lfs install` before
  working with the repo. Note that assets committed before the rules existed are still
  plain Git objects; the rules apply to newly added or changed large files.
- **Generated content is not tracked.** `Library/`, `*.csproj`, `*.sln`, build output,
  caches, ML training artifacts (`results/`) and agent/editor tooling are ignored via
  `.gitignore`.
- **UPM dependencies are declared, not vendored.** See `Packages/manifest.json` and
  `Packages/packages-lock.json`. The ML-Agents packages resolve from a local `file:` path,
  so training requires the local `ml-agents-release_20` checkout.
- Build and setup menus live under the `HagenDa/` menu in the Tuanjie editor.
