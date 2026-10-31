# Matterportable: notes for Claude

Turns a Matterport link into a navigable, photoreal environment in **two engines that share one C# Core**:
- **Unity:** a package plus the Unity 6 project around it. The user runs **Unity 6000.5.0f1 on Windows**, using URP
  and the Input System; both are configured by `Assets/Editor/MatterportProjectSetup.cs`.
- **Godot 4 (.NET build):** the project in `godot/`, which compiles `Runtime/Core` directly. The user imports
  `godot/project.godot` with Godot 4.4+ .NET on Windows.

**Your environment can't run Unity or reach Matterport's viewer, but it can run Godot.** Matterport's Cloudflare bot
check blocks cloud IPs; don't try to get around it. Verify your work with `tools/check.sh`. `tools/godot-check.sh`
runs the Godot project headless on the demo house and saves screenshots: open them with Read to see what you changed.
The user tests in Unity and Godot and pastes back Console/Output text or a diagnostics report.

## Commands

```bash
tools/setup-dev.sh     # once per container: .NET 8 SDK, glslang, NuGet restore
tools/check.sh         # every check (~20 s): core tests, Unity compile x3, CLI, browser script, shaders, metas, dead code
tools/check.sh --fast  # core tests + scripts only
tools/godot-check.sh   # Godot demo runs → /tmp/matterportable-godot/{inside,dollhouse,measure}.png (+ --sweep N, --view N)
dotnet test tools/Matterportable.Core.Tests --filter Alignment
dotnet run --project tools/Matterportable.Cli -- help   # download / import / demo / info / align / diagnose / browser-script
```

Add `/opt/dotnet` to `PATH` if `dotnet` isn't found.

## Layout

```
Packages/com.thepromptcook.matterportable/
  Runtime/Core/        engine-agnostic C# (noEngineReferences): link parsing, JSON, self-healing GraphQL
                       client, downloader, DAM decoder, BVH, frames, alignment solver, synthetic space, diagnostics
  Runtime/Space/       MatterportSpace (component: load lifecycle + queries), SpaceMesh (Unity mesh), SpaceRenderer
                       (shader globals), PanoramaCache (streaming), GpuTextures (async image → GPU),
                       CalibrationRunner, DemoSpace
  Runtime/Core/Measurement.cs   MeasureTool (Matterport measure mode: snapping, axis lock, units, draw list),
                       MeshFeatures (crease edges/corners, wall axes) - shared by both engines' measure UI
  Runtime/Navigation/  MatterportNavigator (camera, transitions, view modes), MatterportMeasure, NavInput, SweepMarkers
  Runtime/UI/          MatterportLauncher (in-game start menu), MatterportHud, MpGui (shared styles)
  Runtime/             MatterportRig (space + camera), MatterportViewerSettings, Utilities
  Runtime/Resources/Matterportable/   shaders: Mesh (projection), Skybox, Marker, FaceBlit + MatterportCommon.hlsl
  Editor/              MatterportSpaceEditor (inspector), MatterportEditorTools (downloads with progress, diagnostics, menus)
  Tests/Core/          NUnit, run by dotnet AND Unity · Tests/Editor/ Unity EditMode · Tests/PlayMode/ end-to-end
  Tools~/browser-export.js   generated from the C# query (fallback exporter)
Assets/Editor/MatterportProjectSetup.cs   project-level setup (URP, Input System, fast Enter Play Mode, Launcher scene)
godot/                Godot 4 .NET project (Matterportable.csproj links Runtime/Core/**)
  scripts/             MatterportSpace, PanoramaCache, MatterportNavigator, MatterportMeasure, SweepMarkers, Launcher, Hud,
                       ViewerSettings, GodotUtil (frame conversion, worker-thread image decode, cache path, demo)
  shaders/             matterport_common.gdshaderinc (port of MatterportCommon.hlsl), mesh, sky, marker
tools/                CLI, test runner project, UnityCompileCheck (+ API stubs), check scripts, godot-check.sh
```

The data flow is: link → model id → GraphQL (`MatterportApi.cs`) → `SpaceDownloader` →
`MatterportCache/<id>/` (`manifest.json`, `mesh/`, `textures/`, `panos/`, `debug/`) → `CalibrationPipeline` →
`calibration.json` → `MatterportSpace` load → navigator.

## Rules

- **Two engines, one behaviour.** Put logic that both engines need in `Runtime/Core`. Engine code is mirrored file
  by file, with the same names and structure. A behaviour change in one engine's runtime must be mirrored in the
  other in the same commit:

  | Unity (`Packages/.../Runtime/`) | Godot (`godot/`) |
  |---|---|
  | `Space/MatterportSpace.cs` + `SpaceMesh` + `SpaceRenderer` | `scripts/MatterportSpace.cs` |
  | `Space/PanoramaCache.cs` + `GpuTextures` | `scripts/PanoramaCache.cs` |
  | `Navigation/MatterportNavigator.cs` + `NavInput` | `scripts/MatterportNavigator.cs` |
  | `Navigation/SweepMarkers.cs` | `scripts/SweepMarkers.cs` |
  | `Navigation/MatterportMeasure.cs` (IMGUI overlay + panel) | `scripts/MatterportMeasure.cs` (Control overlay + panel) |
  | `UI/MatterportLauncher.cs`, `UI/MatterportHud.cs` | `scripts/Launcher.cs`, `scripts/Hud.cs` |
  | `MatterportViewerSettings.cs` | `scripts/ViewerSettings.cs` |
  | `Resources/Matterportable/*.shader` + `MatterportCommon.hlsl` | `shaders/*.gdshader` + `matterport_common.gdshaderinc` |

- **Keep `Runtime/Core` free of `UnityEngine`.** Put Unity glue in `Runtime/Space|Navigation|UI`. New Core logic
  gets a test in `Tests/Core`.
- **Every package file and folder needs a `.meta`.** Run `python3 tools/generate_metas.py`. When moving a script,
  `git mv` its `.meta` too: scenes reference scripts by GUID.
- **If you change `MatterportQueries`:**
  - regenerate `Tools~/browser-export.js` with the CLI `browser-script` command
  - update the schema in `Tests/Core/FakeMatterport.cs`
  - `check.sh` fails if the script is stale
- **Unity 6 deprecations aren't caught here.** The compile check uses the Unity 2021 API, so put any API that
  differs between versions behind one helper. `MpUtil.FindFirst` / `MpUtil.FindAll` already wrap the find APIs.
  Watch for new deprecations in the user's Console.
- **Keep the Console quiet.** Info messages go to `BufferLog` and `debug/load.log`, and reach the Console only
  with `verboseLogging`. Use warnings only for things the user should act on.
- **Godot specifics.** Godot rewrites the csproj SDK version to the editor's version when it opens the project.
  Keep `Godot.NET.Sdk/4.4.0` committed; `godot-check.sh` restores it. Commit Godot's `.uid` files. Godot needs
  `dotnet` on PATH.
- **Never commit `MatterportCache/`.** It holds third-party content.
- **Commits and pushes go to the session's feature branch.** Keep each commit focused.

## Conventions you must not break

- **Coordinate frames.** Unity is left-handed with Y up. Matterport data is right-handed with Z up:
  Unity = (x, z, y), and a quaternion (x, y, z, w) becomes (-x, -z, -y, w). Conversions live in `Frames` and
  `SpaceCalibration.WorldFromPano`.
- **Godot frame:** Godot is right-handed with Y up, so Godot = Core with Z mirrored (`GodotUtil.ToGodot/ToCore`). The
  shaders mirror back with `to_core()` and reuse the Unity math. Core triangle order is front-facing in Godot as it
  is (checked with dollhouse back-face culling).
- **Image rows are bottom-up in Core and Unity** (row 0 = bottom). The PNG codec flips on read and write. **Godot
  images are top-down**: `GodotUtil.LoadLuma` flips, the shaders flip V for file-backed textures, and mesh UVs get
  `1 - v`. Depth cubes are written by C# in Core order, so they are not flipped.
- **Cube faces.** `CubeFaces.Canonical` (C#) and `MP_FaceAxis` / `MP_DepthCoord` (HLSL) must match. Face index ==
  `SignedAxis`. Panorama face order comes from `calibration.json`, never hard-coded.
- **`calibration.Method` values:**
  - `default`: never solved, so the next load runs the solver once
  - `solver` / `synthetic`: solved
  - `unsolved`: tried and failed, so it isn't retried automatically
  - `manual`: set by the user in the inspector, and kept
- **Shader globals** are written only by `SpaceRenderer.Apply` (once per frame, in `LateUpdate`). Every shader
  declares them in `MatterportCommon.hlsl`. Change both together.
- **Domain reload is off** (fast Enter Play Mode, set by the project setup), so statics survive between Play sessions.
  - Deliberate: `MatterportSpace` reuses the last decoded geometry (`LoadGeometry` cache key: mesh timestamp +
    calibration).
  - Anything else static must be reset with `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (see
    `NavInput`).
- **Measurement** behaviour (clicks, Escape, snapping, axes, units, what shows in which view) lives only in Core
  `MeasureTool`; the engine files just feed it the pointer ray and draw its `MeasureDrawList`. Change behaviour there,
  with a test in `Tests/Core/MeasurementTests.cs`.
- **Unity UI blocking:** IMGUI registers its rectangles with `MpGui.Block` while drawing. The next frame's Updates read
  `BlockedGuiRects` (navigator and measure tool), and the navigator clears the list in `LateUpdate`. Escape belongs to
  measure mode while it is open (`MatterportMeasure.OwnsEscape`), then to the launcher. In Godot, measure mode marks
  Escape as handled.
- **Godot shader values** are set per material every frame by `MatterportSpace.ApplyView` (no globals). The sky
  shader returns a flat colour in `AT_CUBEMAP_PASS`, so radiance updates stay cheap.
- **Images load through `GpuTextures`** (UnityWebRequestTexture decode off the main thread, GPU blit and mips).
  Don't decode images on the main thread in Play mode; `Texture2D.LoadImage` is only for Edit mode and the solver.
  Godot: `GodotUtil.LoadImage` plus `GenerateMipmaps` run on worker tasks (Godot `Image` is thread-safe); the main
  thread only creates the textures.

## Debugging what the user reports

Ask for **Copy Diagnostics** (MatterportSpace inspector, also in Play mode). It produces
`debug/diagnostics.txt` with the manifest summary, calibration, pruned query fields, `load.log`, the DAM structure
dump and a redacted API sample. The skill `.claude/skills/matterportable-dev` maps symptoms to code. Matterport
format facts confirmed on real data are in `.claude/skills/matterportable-dev/matterport-formats.md`; update that
file when the user's data teaches you something new.
