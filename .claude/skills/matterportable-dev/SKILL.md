---
name: matterportable-dev
description: Use when changing, fixing or extending Matterportable (Matterport link → navigable environment in Unity and Godot 4, sharing one C# Core) or when the user pastes Unity Console / Godot Output text, a diagnostics report or a screenshot from running it. Covers toolchain setup without Unity, where each behaviour lives, the verification loop and how to turn user reports into fixes.
---

# Working on Matterportable

Claude can't run Unity or reach Matterport from a cloud container, but **it can run the Godot project** headless.
The loop is:

1. Change the code. If it's runtime behaviour, change both engines (table in CLAUDE.md).
2. Run `tools/check.sh`. Then **look at** `/tmp/matterportable-godot/inside.png` and `dollhouse.png` with Read.
   Rendering, projection and navigation bugs usually show there before the user ever sees them.
3. Commit and push.
4. The user pulls and tests in Unity 6000.5 and/or Godot 4 (.NET).
5. The user pastes back Console/Output text or diagnostics.

Make every round count: verify thoroughly here, and ask the user for exactly the evidence you need.

### Seeing the Godot version run

```bash
tools/godot-check.sh                 # demo house: Inside (after a move 0 → 1) + Dollhouse screenshots, fails on any engine/script/shader error
```

```bash
tools/godot-check.sh --sweep 5 --view 2   # another sweep; F2 render view 2 = panorama only (3 visibility, 4 weights)
```

The Godot launcher also takes these arguments after `--`: `--open <link|demo>`, `--sweep N`,
`--mode inside|dollhouse|floorplan`, `--view N`, `--goto N` (a real transition; the screenshot waits until it
finishes), `--screenshot file.png` (it quits after saving). Use them to
reproduce a bug in a single run. The demo's panoramas are rendered from the synthetic house, so at a sweep the
projection must line up exactly. Isolate a problem with views 1 (mesh texture), 2 (panorama) and 3 (visibility).

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
- the Godot C# compile (`-warnaserror`) and the headless Godot demo run (`tools/godot-check.sh`)
- `.meta` coverage
- the dead-code scan (`tools/find_unused.py`)

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
| Inspector, editor downloads, Tools menu | `Editor/MatterportSpaceEditor.cs`, `Editor/MatterportEditorTools.cs` |
| Images slow or hitching | `Runtime/Space/GpuTextures.cs` (async decode → GPU) used by `PanoramaCache` and `SpaceMesh` |
| Project setup, URP, startup warnings | `Assets/Editor/MatterportProjectSetup.cs` |
| Godot: anything above | same name under `godot/scripts/` (see the table in CLAUDE.md); shaders in `godot/shaders/` |
| Godot: "missing dependencies: Launcher.cs" | the user opened it with the standard (non-.NET) Godot; `main.gd` now says so on screen |
| Godot: "dotnet not found", C# build, project won't open | `godot/Matterportable.csproj` (SDK 4.4.0, links Core), `project.godot` (`dotnet/project/assembly_name`) |
| Godot: colours washed out or dark | `source_color` hints on samplers; tonemap Linear in `MatterportSpace.CreateSky` |
| Godot: upside-down or mirrored panoramas or textures | row-order flips in `GodotUtil` and `mp_pano_coords` (V flip), mesh UV `1 - v` |

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
  The Godot port (`godot/shaders/matterport_common.gdshaderinc`) mirrors `MatterportCommon.hlsl` function for
  function; change both. Godot shader errors only show up when Godot runs, so run `tools/godot-check.sh`.
- **Godot code:** mirror the Unity file's structure and names, so a diff in one engine maps line by line onto the
  other. Godot-isms:
  - pointer input comes through `_UnhandledInput`, so UI controls consume their own clicks (no blocked-rect lists)
  - `async`/`await` resumes on the main thread; use `Task.Run` for decode and `await ToSignal(GetTree(), "process_frame")`
    to wait a frame
  - the camera looks down -Z, and yaw is counter-clockwise
  - set state that must survive a node's first-frame setup through a field it applies at the end of setup (like
    `MatterportNavigator.StartMode`)
- **New files:** run `python3 tools/generate_metas.py`. Move scripts with `git mv` of both the file and its `.meta`.
- **Query changes:** regenerate `Tools~/browser-export.js`, and update the `FakeMatterport` schema.
- **Deprecations:** the user sees Unity 6.5 deprecation warnings that the 2021 reference assemblies can't show.
  When one is reported, add a version-guarded path (`#if UNITY_6000_5_OR_NEWER`) inside a shared helper.
- **Domain reload is off** in the user's project (fast Enter Play Mode), so statics survive between Play sessions.
  Reset new static state in `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`, or key caches on everything
  they depend on, as the geometry cache in `MatterportSpace.LoadGeometry` does.
- **Keep the codebase small.**
  - Delete code that no workflow reaches (the in-game launcher is the main entry point; the editor only adds
    inspector actions and menus).
  - Prefer one shared helper over a second copy.
  - `tools/check.sh` runs `tools/find_unused.py`, which fails on members nothing references. Delete them, or add
    genuine API to its `ALLOWED` list.

## Load timeline (what runs where)

This is the order for a launcher "Open space". Keep main-thread work small; everything heavy belongs on a worker or
the GPU.

1. **`MatterportRig.Create`** builds the space and camera, then `MatterportSpace.Start` begins `LoadRoutine`.
2. **Download, if not cached** (network, `SpaceDownloader`, background tasks).
3. **Read `manifest.json` and `calibration.json`** (main thread, milliseconds).
4. **First load only: alignment.** The ~50 small images are decoded on the main thread (Unity's decoder), then
   `CalibrationPipeline` runs on a worker. The result is saved; a failed solve is saved as `unsolved` and not
   retried.
5. **Geometry.** `LoadGeometry` decodes the DAM, builds the BVH and the sweeps on a worker. It is reused across Play
   sessions while the mesh and calibration are unchanged.
6. **Main-thread setup** (milliseconds): the Unity mesh, `SpaceRenderer` and `PanoramaCache`, then the `Loaded`
   event. The navigator arrives at the start sweep.
7. **Panoramas.** The 512 px preview comes first, then 2048 px, one load per sweep at a time (`GpuTextures`: decode
   off the main thread, GPU blit and mips). Up to 4 neighbours are prefetched.
8. **Mesh textures** decode in parallel off the main thread into mipmapped render textures.
9. **Depth maps**, one per visited sweep, are ray-cast on workers as half floats and uploaded at one per frame.

Per frame, the main thread does:
- `SpaceRenderer.Apply`: shader globals
- the navigator's hover ray-cast against the BVH
- up to about 20 marker draws
- IMGUI (HUD and launcher)

**Godot** has no fast-Enter-Play-Mode or static cache: each run is a fresh process, so the geometry (DAM decode, BVH,
sweeps) is rebuilt on every run, on a worker thread. Panorama faces and mesh textures decode and build mipmaps on worker tasks;
the main thread only creates `Texture2DArray`/`ImageTexture`. Depth cubes come from the same
`CubeFaces.DepthCube` (Core) and upload one per frame.

## 4. Reading what the user sends

- **Godot Output / Debugger ▸ Errors:** the same `[Matterportable]` lines (`GD.PushWarning` / `GD.PushError`).
  Ask for the Errors tab text, and for `MatterportCache/<id>/debug/load.log`, which both engines write.
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
4. In the reply, tell the user what to pull and what to look for in Unity and Godot: which Console/Output lines
   should change, or which view to check.
