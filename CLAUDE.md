# Matterportable: notes for Claude

Turns a Matterport link into a navigable, photoreal environment in **three front ends that share one C# Core**:
- **Unity:** a package plus the Unity 6 project around it. The user runs **Unity 6000.5.0f1 on Windows**, using URP
  and the Input System; both are configured by `Assets/Editor/MatterportProjectSetup.cs`.
- **Godot 4 (.NET build):** the project in `godot/`, which compiles `Runtime/Core` directly. The user imports
  `godot/project.godot` with Godot 4.4+ .NET on Windows.
- **Web:**
  - `web/server` (ASP.NET Core) downloads and prepares spaces with the Core and serves the page.
  - `web/wasm` is the Core compiled to WebAssembly (navigator, panorama scheduler, measure tool).
  - `web/client` is three.js. Run it with `dotnet run --project web/server`, then open http://localhost:8080.
  - It will be deployed for others later.

**Your environment can't run Unity or reach Matterport's viewer page, but it can run Godot, and Matterport's data API
and CDN work from here**, so the CLI can download public spaces for testing. The viewer page is blocked by Cloudflare's
bot check for cloud IPs; don't try to get around it. Verify your work with `tools/check.sh`. `tools/godot-check.sh`
runs the Godot project headless on the demo house and saves screenshots: open them with Read to see what you changed.
The user tests in Unity, Godot and the browser and pastes back Console/Output text or a diagnostics report.

## Commands

```bash
tools/setup-dev.sh     # once per container: .NET 8 SDK, glslang, NuGet restore
tools/check.sh         # every check (~2 min): core tests, Unity compile x3, CLI, Godot + web builds and runs, shaders, metas, dead code
tools/check.sh --fast  # core tests + scripts only
tools/godot-check.sh   # Godot demo runs → /tmp/matterportable-godot/{inside,dollhouse,measure,walk}.png (+ --sweep N, --view N)
tools/web-check.sh     # web demo runs in headless Chromium → /tmp/matterportable-web/{inside,dollhouse,measure,walk}.png
tools/web-check.sh --compare <id> 6 && python3 tools/compare_alignment.py /tmp/matterportable-web   # web real-space check
# real-space check: download, render photo-vs-mesh pairs in Godot, measure alignment (verdict ALIGNED/MISALIGNED)
dotnet run --project tools/Matterportable.Cli -- download https://my.matterport.com/show/?m=<id>
xvfb-run -a -s "-screen 0 960x540x24" /opt/godot/godot --path godot --resolution 960x540 --rendering-method gl_compatibility -- --open <id> --compare /tmp/cmp/<id>
python3 tools/compare_alignment.py /tmp/cmp/<id>
dotnet test tools/Matterportable.Core.Tests --filter Alignment
dotnet run --project tools/Matterportable.Cli -- help   # download / demo / info / diagnose
```

Add `/opt/dotnet` to `PATH` if `dotnet` isn't found.

## Layout

```
Packages/com.thepromptcook.matterportable/
  Runtime/Core/        engine-agnostic C# (noEngineReferences): link parsing, JSON, self-healing GraphQL
                       client, downloader, DAM decoder, BVH, frames, alignment solver, synthetic space, diagnostics,
                       and the viewer every front end runs:
    Viewer.cs          ViewerSettings (the one set of defaults), SpaceModel (sweeps, floors, ray casts, load cache),
                       ViewerText (key hint, F1 stats, floor labels)
    Navigator.cs       Matterport navigation: input → camera pose, ViewBinding (what the shaders show), markers;
                       Walk mode (free W/A/S/D) with panoramas from the two nearest sweeps in sight
    Walker.cs          the Walk-mode body: the scanned mesh as collider (walls, furniture, stairs, ledges, ceilings)
    PanoramaScheduler.cs   which panoramas to load/show/drop; engines implement IPanoramaLoader
    Measurement.cs     MeasureTool (measure mode, MeasureOverlay screen-space draw items), MeshFeatures (snapping)
  Runtime/Space/       MatterportSpace (component: load lifecycle, shader globals), SpaceMesh (Unity mesh),
                       PanoramaCache (IPanoramaLoader), GpuTextures (async image → GPU), CalibrationRunner, DemoSpace
  Runtime/Navigation/  MatterportNavigator (input → Core Navigator → camera), MatterportMeasure, NavInput, SweepMarkers
  Runtime/UI/          MatterportLauncher (in-game start menu), MatterportHud, MpGui (shared styles)
  Runtime/             MatterportRig (space + camera), Utilities
  Runtime/Resources/Matterportable/   shaders: Mesh (projection), Skybox, Marker, FaceBlit + MatterportCommon.hlsl
  Editor/              MatterportSpaceEditor (inspector), MatterportEditorTools (downloads with progress, diagnostics, menus)
  Tests/Core/          NUnit, run by dotnet AND Unity (ViewerTests drive the Navigator with a fake loader)
                       · Tests/Editor/ Unity EditMode · Tests/PlayMode/ end-to-end
Assets/Editor/MatterportProjectSetup.cs   project-level setup (URP, Input System, fast Enter Play Mode, Launcher scene)
godot/                Godot 4 .NET project (Matterportable.csproj links Runtime/Core/**)
  scripts/             MatterportSpace, PanoramaCache, MatterportNavigator, MatterportMeasure, SweepMarkers, Launcher, Hud,
                       GodotUtil (frame conversion, worker-thread image decode, cache path, demo)
  shaders/             matterport_common.gdshaderinc (port of MatterportCommon.hlsl), mesh, sky, marker
web/server/           Program.cs (API + static files), SpaceService (download/prepare jobs), WebBundle (space.json,
                       geometry.bin, bvh.bin, features.bin in MatterportCache/<id>/web/)
web/wasm/Bridge.cs    [JSExport] surface of the Core: Load, Frame (one call per frame: input in; camera, binding,
                       markers, loader commands out - layout in its comment), Navigate, Pano*/DepthReady, Measure*
web/client/           index.html, style.css, vendor/three.module.min.js (r169, MIT), js/ mirroring the engine files
tools/                CLI, test runner project, UnityCompileCheck (+ API stubs), check scripts, godot-check.sh, web-check.sh
```

The data flow is: link → model id → GraphQL (`MatterportApi.cs`) → `SpaceDownloader` →
`MatterportCache/<id>/` (`manifest.json`, `mesh/`, `textures/`, `panos/`, `debug/`) → `CalibrationPipeline` →
`calibration.json` → `MatterportSpace` load → navigator.

## Rules

- **Three front ends, one behaviour.** Viewer behaviour lives once in Core: navigation and transitions
  (`Navigator`), walking and collision (`Walker`), panorama loading/eviction (`PanoramaScheduler`), measuring and its drawing (`MeasureTool`,
  `MeasureOverlay`), settings (`ViewerSettings`) and on-screen text (`ViewerText`). Change it there, with a test in
  `Tests/Core` (`ViewerTests`, `MeasurementTests`). Engine files are thin adapters, mirrored file by file with the
  same names: they turn input into Core calls, load textures when the scheduler asks, and draw what the Core
  returns. An adapter change in one front end must be mirrored in the others in the same commit:

  | Unity (`Packages/.../Runtime/`) | Godot (`godot/`) | Web (`web/client/js/`) |
  |---|---|---|
  | `Space/MatterportSpace.cs` + `SpaceMesh` | `scripts/MatterportSpace.cs` | `MatterportSpace.js` |
  | `Space/PanoramaCache.cs` + `GpuTextures` | `scripts/PanoramaCache.cs` | `PanoramaCache.js` |
  | `Navigation/MatterportNavigator.cs` + `NavInput` | `scripts/MatterportNavigator.cs` | `MatterportNavigator.js` (+ `Bridge.Frame`) |
  | `Navigation/SweepMarkers.cs` | `scripts/SweepMarkers.cs` | `SweepMarkers.js` |
  | `Navigation/MatterportMeasure.cs` (IMGUI) | `scripts/MatterportMeasure.cs` (Control) | `MatterportMeasure.js` (canvas 2D) |
  | `UI/MatterportLauncher.cs`, `UI/MatterportHud.cs` | `scripts/Launcher.cs`, `scripts/Hud.cs` | `Launcher.js`, `Hud.js` |
  | `Resources/Matterportable/*.shader` + `MatterportCommon.hlsl` | `shaders/*.gdshader` + `.gdshaderinc` | `shaders.js` |

- **Keep `Runtime/Core` free of `UnityEngine`.** Put Unity glue in `Runtime/Space|Navigation|UI`. New Core logic
  gets a test in `Tests/Core`.
- **Every package file and folder needs a `.meta`.** Run `python3 tools/generate_metas.py`. When moving a script,
  `git mv` its `.meta` too: scenes reference scripts by GUID.
- **If you change `MatterportQueries`,** update the schema in `Tests/Core/FakeMatterport.cs`.
- **The Core and Unity code is C# 8** (Unity's compiler): no `new()`, `is not`, records or init. Godot scripts may use
  newer C#. A local function can't capture an `in` parameter (CS1628): copy it to a local first.
- **Unity 6 deprecations aren't caught here.** The compile check uses the Unity 2021 API, so put any API that
  differs between versions behind one helper. `MpUtil.FindFirst` / `MpUtil.FindAll` already wrap the find APIs.
  Watch for new deprecations in the user's Console.
- **Keep the Console quiet.** Info messages go to `BufferLog` and `debug/load.log`, and reach the Console only
  with `settings.VerboseLogging`. Use warnings only for things the user should act on.
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
- **Core frame = Unity frame**, local to the space: the Unity adapters convert rays and poses through
  `space.transform`, and a Core `Quat` is a Unity `Quaternion` as it is. The camera looks along +Z; Godot/three.js
  take (-x, -y, z, w).
- **Shader globals** are written only by `MatterportSpace.ApplyView` (once per frame, in `LateUpdate`, from the
  navigator's `ViewBinding`). Every shader declares them in `MatterportCommon.hlsl`. Change both together.
- **Settings** are a `ViewerSettings` field on `MatterportSpace` (inspector), read live by the Core objects. In Play
  mode `OnValidate` copies inspector edits into the instance the Core holds, in case Unity replaced it.
- **Domain reload is off** (fast Enter Play Mode, set by the project setup), so statics survive between Play sessions.
  - Deliberate: `SpaceModel.Load` keeps the last model (cache key: mesh timestamp + sweep count + calibration), so
    reopening a space, or the Edit-mode preview followed by Play, skips decoding.
  - Anything else static must be reset with `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (see
    `NavInput`).
- **View modes:** `ViewMode.FromAbove()` (Dollhouse, Floor plan) decides back-face culling, floor clipping and
  measuring from outside; Inside and Walk are both "inside". Walk starts only where `Walker.Standing` (a floor
  below and room for the body; unplaced 360° photos have neither) and otherwise stays Inside. Its eye height is the
  capture camera's height above the floor, so the view at a sweep is exactly the photo. Leaving Walk for Inside moves
  to the nearest sweep with the usual transition.
- **Walking physics** (`Walker`): a stack of spheres from one step height (30 cm) up to the head, plus five
  downward foot rays. A move that leaves no floor within 4 m, or a flat surface inside the body, is refused. Tested by
  `WalkingTests` on hand-made rooms, furniture and stairs. For real scans, simulate it in a scratch console project
  that compiles `Runtime/Core`: walking between neighbouring sweeps arrived on 765 of 768 clear paths, and every
  staircase was climbed. The checks script walking with `--mode walk --yaw D --walk S` (Godot) and
  `?mode=walk&yaw=D&walk=S` (web).
- **Measurement** behaviour (clicks, Escape, snapping, axes, units, what shows in which view, colours and line
  widths) lives only in Core `MeasureTool`; the engine files feed it the pointer ray and draw the screen-space
  `MeasureOverlay` items (line, dot, ring, label). While measuring, `Navigator.Measuring` hides markers and leaves
  clicks to the tool (`Navigator.Clicked`); Unity's `MatterportMeasure` runs after the navigator (execution order).
- **Unity UI blocking:** IMGUI registers its rectangles with `MpGui.Block` while drawing. The next frame's Updates read
  `BlockedGuiRects` (`MatterportNavigator.PointerOverView`), and the navigator clears the list in `LateUpdate`. Escape
  belongs to measure mode while it is open (`MatterportMeasure.OwnsEscape`), then to the launcher. In Godot, measure
  mode marks Escape as handled.
- **Web specifics:**
  - **Frame:** three.js is right-handed with Y up like Godot, so web = Core with Z mirrored (`Core.js coreOf/fromCore`).
  - **Image rows:** photos are decoded with `imageOrientation: 'flipY'`, so V is bottom-up as in Core.
  - **Panoramas** are six 2D textures each (WebGL can't index sampler arrays at run time).
  - **Depth cubes** are rendered on the GPU (`PanoramaCache.renderDepth`).
  - **Culling:** Core triangle order is back-facing to three.js, so `cullBack` discards `gl_FrontFacing`.
  - **Server-built data:** WebAssembly runs the Core interpreted, about 25× slower than native (the BVH took 4.9 s vs
    0.17 s). So the server builds the BVH and snapping features natively (`Bvh.SerializeHierarchy`,
    `MeshFeatures.Serialize`), and the page only loads them.
  - **One call per frame:** the page sends input to `Bridge.Frame` and decodes a flat double array (layout in the
    Bridge comment); the scheduler's load/depth/unload requests come back in it as commands. Keep JS↔WASM calls few.
  - **JSExport** methods are called from JS only; `find_unused.py` reads `web/client/js` too.
  - **Shaders:** GLSL 3 shaders declare `out vec4 mpColor` (three r169 doesn't define `gl_FragColor` for GLSL3).
  - **SDK versions:** the user builds with .NET 10, while this container's default is .NET 8. The server's build
    restores and publishes the WebAssembly Core in separate MSBuild calls (one combined call gave .NET 10 an empty
    `_framework`: "Failed to fetch … dotnet.js"). `check.sh` builds the web project with both SDKs; .NET 10 lives in
    `/opt/dotnet10` and builds in its own copy.
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
