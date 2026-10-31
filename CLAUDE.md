# Matterportable: notes for Claude

Turns a Matterport link into a navigable, photoreal environment in **three front ends that share one C# Core**:
- **Unity:** a package plus the Unity 6 project around it. The user runs **Unity 6000.5.0f1 on Windows**, using URP
  and the Input System; both are configured by `Assets/Editor/MatterportProjectSetup.cs`.
- **Godot 4 (.NET build):** the project in `godot/`, which compiles `Runtime/Core` directly. The user imports
  `godot/project.godot` with Godot 4.4+ .NET on Windows.
- **Web:**
  - `web/server` (ASP.NET Core) downloads and prepares spaces with the Core and serves the page.
  - `web/wasm` is the Core compiled to WebAssembly (ray casts, measure tool).
  - `web/client` is three.js. Run it with `dotnet run --project web/server`, then open http://localhost:8080.
  - It will be deployed for others later.

**Your environment can't run Unity or reach Matterport's viewer page, but it can run Godot, and Matterport's data API
and CDN work from here**, so the CLI can download public spaces for testing. The viewer page is blocked by Cloudflare's
bot check for cloud IPs; don't try to get around it. Verify your work with `tools/check.sh`. `tools/godot-check.sh`
runs the Godot project headless on the demo house and saves screenshots: open them with Read to see what you changed.
The user tests in Unity and Godot and pastes back Console/Output text or a diagnostics report.

## Commands

```bash
tools/setup-dev.sh     # once per container: .NET 8 SDK, glslang, NuGet restore
tools/check.sh         # every check (~20 s): core tests, Unity compile x3, CLI, browser script, shaders, metas, dead code
tools/check.sh --fast  # core tests + scripts only
tools/godot-check.sh   # Godot demo runs → /tmp/matterportable-godot/{inside,dollhouse,measure}.png (+ --sweep N, --view N)
tools/web-check.sh     # web demo runs in headless Chromium → /tmp/matterportable-web/{inside,dollhouse,measure}.png
tools/web-check.sh --compare <id> 6 && python3 tools/compare_alignment.py /tmp/matterportable-web   # web real-space check
# real-space check: download, render photo-vs-mesh pairs in Godot, measure alignment (verdict ALIGNED/MISALIGNED)
dotnet run --project tools/Matterportable.Cli -- download https://my.matterport.com/show/?m=<id>
xvfb-run -a -s "-screen 0 960x540x24" /opt/godot/godot --path godot --resolution 960x540 --rendering-method gl_compatibility -- --open <id> --compare /tmp/cmp/<id>
python3 tools/compare_alignment.py /tmp/cmp/<id>
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
web/server/           Program.cs (API + static files), SpaceService (download/prepare jobs), WebBundle (space.json,
                       geometry.bin, bvh.bin, features.bin in MatterportCache/<id>/web/)
web/wasm/Bridge.cs    [JSExport] surface of the Core: Load, Raycast, Occluded, Measure*
web/client/           index.html, style.css, vendor/three.module.min.js (r169, MIT), js/ mirroring the engine files
tools/                CLI, test runner project, UnityCompileCheck (+ API stubs), check scripts, godot-check.sh, web-check.sh
```

The data flow is: link → model id → GraphQL (`MatterportApi.cs`) → `SpaceDownloader` →
`MatterportCache/<id>/` (`manifest.json`, `mesh/`, `textures/`, `panos/`, `debug/`) → `CalibrationPipeline` →
`calibration.json` → `MatterportSpace` load → navigator.

## Rules

- **Two engines, one behaviour.** Put logic that both engines need in `Runtime/Core`. Engine code is mirrored file
  by file, with the same names and structure. A behaviour change in one engine's runtime must be mirrored in the
  other in the same commit:

  | Unity (`Packages/.../Runtime/`) | Godot (`godot/`) | Web (`web/client/js/`) |
  |---|---|---|
  | `Space/MatterportSpace.cs` + `SpaceMesh` + `SpaceRenderer` | `scripts/MatterportSpace.cs` | `MatterportSpace.js` |
  | `Space/PanoramaCache.cs` + `GpuTextures` | `scripts/PanoramaCache.cs` | `PanoramaCache.js` |
  | `Navigation/MatterportNavigator.cs` + `NavInput` | `scripts/MatterportNavigator.cs` | `MatterportNavigator.js` |
  | `Navigation/SweepMarkers.cs` | `scripts/SweepMarkers.cs` | `SweepMarkers.js` |
  | `Navigation/MatterportMeasure.cs` (IMGUI) | `scripts/MatterportMeasure.cs` (Control) | `MatterportMeasure.js` (canvas 2D) |
  | `UI/MatterportLauncher.cs`, `UI/MatterportHud.cs` | `scripts/Launcher.cs`, `scripts/Hud.cs` | `Launcher.js`, `Hud.js` |
  | `MatterportViewerSettings.cs` | `scripts/ViewerSettings.cs` | `ViewerSettings.js` |
  | `Resources/Matterportable/*.shader` + `MatterportCommon.hlsl` | `shaders/*.gdshader` + `.gdshaderinc` | `shaders.js` |

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
  Keep `Godot.NET.Sdk/4.4.0` committed; `godot-check.sh` restores it. Commit Godot's `.uid` files in the same commit as the script: if the user's Godot generates one first, their pull fails with "untracked files would be overwritten" (`check.sh` enforces this). Godot needs
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
- **Web specifics:**
  - **Frame:** three.js is right-handed with Y up like Godot, so web = Core with Z mirrored (`Core.js coreOf/fromCore`).
  - **Image rows:** photos are decoded with `imageOrientation: 'flipY'`, so V is bottom-up as in Core.
  - **Panoramas** are six 2D textures each (WebGL can't index sampler arrays at run time).
  - **Depth cubes** are rendered on the GPU (`PanoramaCache.renderDepth`).
  - **Culling:** Core triangle order is back-facing to three.js, so `cullBack` discards `gl_FrontFacing`.
  - **Server-built data:** WebAssembly runs the Core interpreted, about 25× slower than native (the BVH took 4.9 s vs
    0.17 s). So the server builds the BVH and snapping features natively (`Bvh.SerializeHierarchy`,
    `MeshFeatures.Serialize`), and the page only loads them.
  - **JSExport** methods are called from JS only; `find_unused.py` reads `web/client/js` too.
  - **Shaders:** GLSL 3 shaders declare `out vec4 mpColor` (three r169 doesn't define `gl_FragColor` for GLSL3).
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
