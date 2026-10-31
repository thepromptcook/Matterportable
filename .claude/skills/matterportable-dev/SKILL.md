---
name: matterportable-dev
description: Use when changing, fixing or extending the Matterportable Unity package (Matterport link → navigable Unity environment) or when the user pastes Unity Console output, a diagnostics report or a screenshot from running it. Covers toolchain setup without Unity, where each behaviour lives, the verification loop and how to turn user reports into fixes.
---

# Working on Matterportable

Claude can't run Unity or reach Matterport from a cloud container, so the loop is:

1. Change the code.
2. Run `tools/check.sh`.
3. Commit and push.
4. The user pulls and tests in Unity 6000.5.
5. The user pastes back Console output or diagnostics.

Make every round count: verify thoroughly here, and ask the user for exactly the evidence you need.

## 1. Set up (once per container)

```bash
tools/setup-dev.sh
```

```bash
export PATH=/opt/dotnet:$PATH
```

```bash
tools/check.sh
```

`check.sh` must end with "All checks passed" before any push. It runs:
- the core NUnit suite (`dotnet test`)
- three Unity compile configurations against Unity's reference assemblies (editor + tests, player, Input System)
- the CLI build
- a check that `Tools~/browser-export.js` matches the C# query
- the browser exporter against a mock endpoint (node)
- the shader HLSL through glslang
- `.meta` coverage

## 2. Where things live (symptom → code)

| Symptom / request | Look at |
|---|---|
| Link not recognised | `Runtime/Core/MatterportLink.cs` (+ `LinkAndJsonTests`) |
| "field not available" / missing data from the API | `MatterportQueries.ModelQuery` and `RemoteModelParser` in `Runtime/Core/MatterportApi.cs`; `debug/graph-response.json` |
| Download fails, expired links, wrong files | `Runtime/Core/SpaceDownloader.cs` (texture URL candidates, `<face>` templates, tiers, refresh) |
| Mesh missing, garbled, `UVs: False` | `Runtime/Core/Dam.cs` (`ReadSimpleChunk` UV detection); `debug/dam-structure.txt` |
| Sideways, mirrored or inside-out mesh | `SpaceCalibrator.CheckGeometry` (frames, winding) in `PanoAlignmentSolver.cs`; `Frames` in `MathTypes.cs` |
| Panoramas rotated or not lining up with geometry | `PanoAlignmentSolver.Solve`; `calibration.json` (method/score/faces); shader `MP_PanoCoords` |
| Slow or failed load, Play-mode errors | `Runtime/Space/MatterportSpace.cs` `LoadRoutine`; `debug/load.log` |
| Panorama streaming, memory, pop-in | `Runtime/Space/PanoramaCache.cs`; settings `panoMaxSize`, `panoMemoryBudgetMB`, `prefetchNeighbors` |
| Smearing during moves | depth maps (`PanoramaCache.ComputeDepth`) + `MP_Visibility` / `MP_BlendWeights` in `MatterportCommon.hlsl` |
| Click / WASD / drag / zoom feel, dollhouse, floor plan | `Runtime/Navigation/MatterportNavigator.cs` + `MatterportViewerSettings` |
| Markers, cursor | `SweepMarkers.cs`, `MatterportMarker.shader` |
| Menu, HUD, Esc | `Runtime/UI/MatterportLauncher.cs`, `MatterportHud.cs`, `MpGui.cs` |
| Input not working | `NavInput.cs` (legacy vs Input System via `MATTERPORTABLE_INPUT_SYSTEM`) |
| Editor windows, inspector, builds | `Editor/` (`MatterportImportWindow`, `MatterportSpaceEditor`, `MatterportBuildHook`) |
| Project setup, URP, startup warnings | `Assets/Editor/MatterportProjectSetup.cs` |

## 3. Making a change

- **Core logic** (`Runtime/Core`): add or extend a test in `Packages/.../Tests/Core`.
  - The fake Matterport server (`FakeMatterport.cs`) and the procedural house (`SyntheticSpace`) let you
    reproduce download, decode and alignment problems end to end.
  - To simulate a format the user's data revealed, make `SyntheticSpace` or `DamWriter` produce it, then test
    that decoding or calibration still recovers the truth.
- **Unity code:** compile with `check.sh`. Re-read the change for runtime logic, because nothing executes it here.
  - Prefer small, isolated edits.
  - Keep Unity-version differences in one helper (`MpUtil`).
- **Shaders:** keep C# and HLSL math in sync (see CLAUDE.md conventions). `tools/check_shaders.py` compiles them.
- **New files:** run `python3 tools/generate_metas.py`. Move scripts with `git mv` of both the file and its `.meta`.
- **Query changes:** regenerate `Tools~/browser-export.js`, and update the `FakeMatterport` schema.
- **Deprecations:** the user sees Unity 6.5 deprecation warnings that the 2021 reference assemblies can't show.
  When one is reported, add a version-guarded path (`#if UNITY_6000_5_OR_NEWER`) inside a shared helper.

## 4. Reading what the user sends

- **Console lines** carry the first line plus a stack trace. The `[Matterportable]` prefix marks ours. Info lines
  only appear when `verboseLogging` is on, so warnings and errors are the signal.
- **Diagnostics report** (inspector ▸ Copy Diagnostics, or `matterportable diagnose`): the manifest summary,
  `calibration.json`, pruned query fields, the last `load.log`, the DAM structure dump and a redacted API sample.
  Ask for it whenever a space downloads or renders wrongly.
- **Screenshots:** ask for one in Inside view at a sweep (it should look exactly like the photo) and one in
  Dollhouse view (the textured mesh). F2 cycles the shader debug views:
  - mesh texture only
  - panorama only
  - visibility A/B
  - blend weights

After learning a new fact about Matterport's data, record it in [matterport-formats.md](matterport-formats.md).

## 5. Finish

1. Run `tools/check.sh`.
2. Commit with a message that says what changed for the user.
3. Push to the session's branch.
4. In the reply, tell the user what to pull and what to look for in Unity: which Console lines should change, or
   which view to check.
