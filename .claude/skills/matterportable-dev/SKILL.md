---
name: matterportable-dev
description: Use when changing, fixing or extending Matterportable (Matterport link → navigable environment in Unity, Godot 4 and a web browser, sharing one C# Core that also holds the viewer logic) or when the user pastes Unity Console / Godot Output / browser console text, a diagnostics report or a screenshot from running it. Covers toolchain setup without Unity, where each behaviour lives, the verification loop and how to turn user reports into fixes.
---

# Working on Matterportable

Claude can't run Unity or reach Matterport's viewer from a cloud container, but **it can run the Godot and web
versions** headless (Godot under xvfb, the web page in Playwright's Chromium) and download public spaces.
The loop is:

1. Change the code. **Viewer behaviour** (navigation, transitions, which panoramas load or show, measuring,
   settings, on-screen text) is Core code: `Navigator`, `PanoramaScheduler`, `MeasureTool`, `ViewerSettings`,
   `ViewerText`, tested by `ViewerTests` / `MeasurementTests` in seconds. **Adapters** (input mapping, texture
   loading, drawing) exist once per front end; change all three (table in CLAUDE.md).
2. Run `tools/check.sh`. Then **look at** `/tmp/matterportable-godot/*.png` and `/tmp/matterportable-web/*.png`
   with Read. Rendering, projection and navigation bugs usually show there before the user ever sees them; a Core
   change that renders right in Godot and the web is right in Unity too, since all three run the same code.
3. Commit and push.
4. The user pulls and tests in Unity 6000.5 and/or Godot 4 (.NET).
5. The user pastes back Console/Output text or diagnostics.

Make every round count: verify thoroughly here, and ask the user for exactly the evidence you need.

### Seeing the web version run

```bash
tools/web-check.sh                   # demo house in headless Chromium: inside (move 0 → 1), dollhouse, measure, walk
```

```bash
tools/web-check.sh --compare rGMMnZDh3Tf 6 && python3 tools/compare_alignment.py /tmp/matterportable-web
```

The page also reads URL parameters, the same as Godot's command line: `?open=<link|demo>&sweep=N&mode=…&yaw=D&pitch=D&view=N&goto=N&walk=S&measure=1&move=D`.
`window.mp.checks` reports the result. A check that hangs: `window.mp.settleState()` shows what the page is still
waiting for. Headless rendering is all on the CPU, so the check runs Chromium on Mesa under xvfb with `aa=0&aniso=1`
(multisampling and anisotropic filtering off). Those cut a 131 s check to 24 s; real browsers keep both on.

### Seeing the Godot version run

```bash
tools/godot-check.sh                 # demo house: Inside (after a move 0 → 1), Dollhouse, Measure and Walk (into the table) screenshots, fails on any engine/script/shader error
```

```bash
tools/godot-check.sh --sweep 5 --view 2   # another sweep; F2 render view 2 = panorama only (3 visibility, 4 weights)
```

The Godot launcher also takes these arguments after `--`: `--open <link|demo>`, `--sweep N`,
`--mode inside|dollhouse|floorplan|walk`, `--yaw D`, `--walk S` (holds W for S seconds; the log line gives the camera
position), `--view N`, `--views 0,2,3` (with `--screenshot f.png`: one file per render view, `f_v0.png` …, from the
same spot), `--depth N` (depth cube size), `--fidelity dir` (below), `--pitch D` (with `--yaw`), `--move-prop D` (Walk mode:
F on what the middle of the view is on, turn D degrees, F again, turn back half way; the log gives where the prop went
or why not), `--measure` (places a two-segment line with real clicks, sends a
real Escape, starts another line; the log then says "1 measurement(s), drawing yes, menu closed"), `--goto N` (a real transition; the screenshot waits until it
finishes), `--screenshot file.png` (it quits after saving). Use them to
reproduce a bug in a single run. The demo's panoramas are rendered from the synthetic house, so at a sweep the
projection must line up exactly. Isolate a problem with views 1 (mesh texture), 2 (panorama) and 3 (visibility).

### Measuring visual fidelity (screenshots → numbers)

To judge a rendering change, score it instead of eyeballing it. Walk mode shows views where no photo was taken, so
the test is leave-one-out: at a sweep, render Walk mode **without** that sweep's photo and compare with the photo.

```bash
xvfb-run -a -s "-screen 0 960x540x24" /opt/godot/godot --path godot --resolution 960x540 --rendering-method gl_compatibility -- --open rGMMnZDh3Tf --compare-sweeps 8 --fidelity /tmp/fid/a
```

```bash
python3 tools/compare_fidelity.py /tmp/fid/a /tmp/fid/b   # SSIM and PSNR per run (28 views each)
```

- Restore `godot/Matterportable.csproj` afterwards if Godot rewrote its SDK version (`git checkout` it).
- **Check placement first:** the log ends "walk camera at most X cm from the photo's spot". It must be 0.0, or
  the scores measure the offset instead of the rendering (that hid the eye-height bug).
- The photos are rendered through the same shader (view 2), so a sampling change moves both sides: diff the
  images too (`numpy` mean absolute difference), and look at the worst pair with Read.
- History on the user's house: original shaders 0.611 SSIM / 18.16 dB → PCF + seam fix + exact eye height 0.639 /
  18.65 dB. No gain from: 1024 depth cubes, sharper blend weights (worse), the 3D-tile mesh (0.636). An oracle
  colour match would add only ~0.02, so exposure differences between sweeps are minor; the rest is geometry
  (photos projected onto an approximate mesh) and disocclusion.

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
- the shader HLSL through glslang
- the Godot C# compile (`-warnaserror`) and the headless Godot demo run (`tools/godot-check.sh`)
- the web build with .NET 8 and with the user's .NET 10, and the headless Chromium demo run (`tools/web-check.sh`)
- `.meta` coverage
- the dead-code scan (`tools/find_unused.py`)

## 2. Where things live (symptom → code)

| Symptom / request | Look at |
|---|---|
| Link not recognised | `Runtime/Core/MatterportLink.cs` (+ `LinkAndJsonTests`) |
| "field not available" / missing data from the API | `MatterportQueries.ModelQuery` and `RemoteModelParser` in `Runtime/Core/MatterportApi.cs`; `debug/graph-response.json` |
| Download fails, expired links, wrong files | `Runtime/Core/SpaceDownloader.cs` (texture URL candidates, `<face>` templates, tiers, refresh) |
| Mesh tiles (`tiles/`, Draco GLBs) missing or unreadable | `SpaceDownloader.DownloadTilesAsync` (right after the mesh: tokens last ~5 min), `MeshTiles.cs` (tileset leaves, GLB → Core frame), `Draco.cs` (sequential Draco 2.2 only) + `MeshTileTests` (a grid Google's encoder wrote) |
| Mesh missing, garbled, `UVs: False` | `Runtime/Core/Dam.cs` (`ReadSimpleChunk` UV detection); `debug/dam-structure.txt` |
| Sideways, mirrored or inside-out mesh | `SpaceCalibrator.CheckGeometry` (frames, winding) in `PanoAlignmentSolver.cs`; `Frames` in `MathTypes.cs` |
| Panoramas rotated or not lining up with geometry | `PanoAlignmentSolver.Solve`; `calibration.json` (method/score/faces); shader `MP_PanoCoords` |
| Slow or failed load, Play-mode errors | `Runtime/Space/MatterportSpace.cs` `LoadRoutine`; `debug/load.log` |
| Panorama streaming, memory, pop-in | what/when: Core `PanoramaScheduler.cs` (tiers, priorities, LRU budget, pinned sweeps); how: each front end's `PanoramaCache` (decode, upload); settings `PanoMaxSize`, `PanoMemoryBudgetMB`, `PrefetchNeighbors` in `ViewerSettings` (`Core/Viewer.cs`) |
| Smearing during moves | depth cubes (scheduler `RequestDepth` → `PanoramaCache.ComputeDepth`), `UseDepth` in `Navigator.PushView`, `MP_Visibility` / `MP_BlendWeights` in `MatterportCommon.hlsl` |
| Dotted or blocky outlines around objects while moving or walking | `MP_DepthVisibility` (PCF: the visibility test on 4 point-sampled depth texels, blended); depth textures must stay point-sampled (Unity `FilterMode.Point`, Godot `filter_nearest`, web `NearestFilter` cube target with `mpCubeDir`) |
| Thin lines along outlines at rest on a sweep | `Navigator.PushView`: no depth test while the camera stands on the shown sweep (`atSweep`) |
| Dashed lines along panorama cube-face seams | `MP_SamplePano` / `mp_sample_pano` / `samplePanoA`: explicit LOD in a band along face edges (gradient sampling there draws the dashes; isotropic gradients don't help) |
| Click / WASD / drag / zoom feel, transitions, dollhouse, floor plan, tour | Core `Navigator.cs` + `ViewerTests`; settings in `ViewerSettings`. The `MatterportNavigator` adapters only map input and apply the pose (web: through `Bridge.Frame`) |
| Wrong panorama shown, bad blend, floors not clipped, culling | `Navigator.PushView` → `ViewBinding` → `MatterportSpace.ApplyView` (Unity, Godot) / `MatterportSpace.update` (web) |
| Walk mode: walks through something, gets stuck, can't climb stairs, jittery steps | Core `Walker.cs` (body spheres, foot rays, step/fall, no-floor refusal; settings `BodyRadius`, `StepHeight`, `WalkSpeed`) + `WalkingTests`; `Navigator.UpdateWalk` / `StartWalk` (`Standing`) |
| Walk mode: view at a sweep doesn't match the photo, eyes too low/high | `Walker` constructor: `FloorUnder` (first walkable surface 1-2.5 m below the eye, so table tops are skipped) and the ring search for room to stand; the eye height is the capture camera's height above that floor |
| Walk mode: a piece of furniture can't be moved, or a wall/railing comes loose | `PropFinder` in `Core/Props.cs` (column classes, patch size limits, triangle assignment: every corner over the patch and below the local top) + `PropTests`; delete `objects.json` to find again; F2 → *Props* shows every piece. Look at the patches top-down first (scratch harness dumping `PropFinder` cells), then in Godot `--mode dollhouse --view 5` |
| Walk mode: carrying, dropping, turning, doors | `PropWorld` (Carry/Update/Drop/ToggleDoor, `DropRefused`) + `Navigator.UpdateHands` (F or click on `HighlightProp`, wheel turns) + `ViewerText.PropHint`; `PropTests`, `ViewerTests.WalkModePicksUpFurniture…` |
| Moved prop drawn in the wrong place, room black, prop without its photo | engine adapters: Unity `SpaceMesh.ApplyProps`, Godot `MatterportSpace.ApplyView` (prop `Transform`), web `MatterportSpace.update` (Core → three: mirror Z, quaternion (-x, -y, z, w)); the shaders' space matrix (projection from scanned positions) and tint (zero alpha when not set) |
| A smear or outline where a moved prop stood | the piece channel of the depth cubes (`MP_Hides`: a moved prop's pixels only for that prop, any of the 4 taps) and `MP_HoleFill` (fill from the photo around the hole). An outline left at a wall is the prop's parts the finder left with the room (wall columns; `PropFinder.GrowAlongMesh` takes only non-vertical connected triangles, since vertical ones took the wall). Check with F2 views 1 (baked texture) and 3 (visibility: the hole is black) |
| Walk mode: popping or wrong photos between sweeps | `Navigator.UpdateWalkPanoramas` (two nearest sweeps in sight, weights 1/d − 1/d₃, sticky slots). Ghosting of thin unscanned things is inherent to projecting photos |
| Markers, cursor | what and where: `Navigator.BuildMarkers`; drawing: `SweepMarkers` + marker shader |
| F1 stats, key hint, floor button labels | `ViewerText` in `Core/Viewer.cs` |
| Menu, HUD, Esc | `Runtime/UI/MatterportLauncher.cs`, `MatterportHud.cs`, `MpGui.cs` |
| Input not working | `NavInput.cs` (legacy vs Input System via `MATTERPORTABLE_INPUT_SYSTEM`) |
| Inspector, editor downloads, Tools menu | `Editor/MatterportSpaceEditor.cs`, `Editor/MatterportEditorTools.cs` |
| Images slow or hitching | `Runtime/Space/GpuTextures.cs` (async decode → GPU) used by `PanoramaCache` and `SpaceMesh` |
| Project setup, URP, startup warnings | `Assets/Editor/MatterportProjectSetup.cs` |
| Measure mode: clicks, snapping, Shift/Alt, units, labels, colours, what shows where | Core `Runtime/Core/Measurement.cs` (`MeasureTool`, `MeasureOverlay` screen-space items, `MeshFeatures`) + `Tests/Core/MeasurementTests.cs`; panels in the three `MatterportMeasure` files |
| Clicks go through UI, or Escape opens the menu while measuring | `MpGui.Block` / `BlockedGuiRects` (`MatterportNavigator.PointerOverView`, cleared in its `LateUpdate`), `MatterportMeasure.OwnsEscape`; Godot: `SetInputAsHandled` in `MatterportMeasure._UnhandledInput` |
| Web: anything above | same name in `web/client/js/` (table in CLAUDE.md); server in `web/server/`, Core bridge in `web/wasm/Bridge.cs` |
| Web: blank page, "Failed to fetch", 404 on `_framework` | the server must be built (`dotnet build web/server` publishes the WebAssembly Core into its wwwroot); page errors are in the browser console and in `tools/web-check.sh` output |
| Web: space never opens | the server's job status (`api/spaces/<id>/status`), `MatterportCache/<id>/web/` (delete it to rebuild the bundle) |
| Web: slow page start | WebAssembly runs the Core interpreted: never build BVHs or features in the page. Add server-side precomputation to `WebBundle` instead |
| Godot: anything above | same name under `godot/scripts/` (see the table in CLAUDE.md); shaders in `godot/shaders/` |
| Godot: "missing dependencies: Launcher.cs" | the user opened it with the standard (non-.NET) Godot; `main.gd` now says so on screen |
| Godot: "dotnet not found", C# build, project won't open | `godot/Matterportable.csproj` (SDK 4.4.0, links Core), `project.godot` (`dotnet/project/assembly_name`) |
| Godot: colours washed out or dark | `source_color` hints on samplers; tonemap Linear in `MatterportSpace.CreateSky` |
| Godot: upside-down or mirrored panoramas or textures | row-order flips in `GodotUtil` and `mp_pano_coords` (V flip), mesh UV `1 - v` |

## 3. Making a change

- **Core logic** (`Runtime/Core`): add or extend a test in `Packages/.../Tests/Core`.
  - Viewer behaviour: `ViewerTests` runs the `Navigator` frame by frame with a fake loader on
    `TestSpaces.RoomModel()` (three sweeps in a room); clicks are rays aimed at a point (`ClickAt`).
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
  - poses come from the Core (Unity's frame); convert with `GodotUtil.ToGodot` (positions mirror Z, rotations
    take (-x, -y, z, w)), and the Godot camera then looks down -Z as usual
  - set state that must survive a node's first-frame setup through a field it applies at the end of setup (like
    `MatterportNavigator.StartMode`)
- **New files:** run `python3 tools/generate_metas.py`. Move scripts with `git mv` of both the file and its `.meta`.
- **Query changes:** update the `FakeMatterport` schema.
- **Deprecations:** the user sees Unity 6.5 deprecation warnings that the 2021 reference assemblies can't show.
  When one is reported, add a version-guarded path (`#if UNITY_6000_5_OR_NEWER`) inside a shared helper.
- **Domain reload is off** in the user's project (fast Enter Play Mode), so statics survive between Play sessions.
  Reset new static state in `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`, or key caches on everything
  they depend on, as `SpaceModel.Load` does.
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
5. **Model.** `SpaceModel.Load` decodes the DAM, builds the BVH and the sweeps on a worker. It is reused across Play
   sessions (and after the Edit-mode preview) while the mesh and calibration are unchanged.
6. **Main-thread setup** (milliseconds): the Unity mesh, shader state, `PanoramaCache` and `PanoramaScheduler`.
   The navigator's next frame creates the Core `Navigator`, which arrives at the start sweep and requests its
   panoramas and depth cube.
7. **Panoramas.** The 512 px preview comes first, then 2048 px, one load per sweep at a time (`GpuTextures`: decode
   off the main thread, GPU blit and mips). Up to 4 neighbours are prefetched.
8. **Mesh textures** decode in parallel off the main thread into mipmapped render textures.
9. **Depth maps**, one per visited sweep, are ray-cast on workers as half floats and uploaded at one per frame.

Per frame, the main thread does:
- `Navigator.Update`: input → camera pose, the hover ray-cast against the BVH, the view binding, markers
- `PanoramaScheduler.Update`: start queued loads, drop over budget
- `MatterportSpace.ApplyView`: shader globals
- up to about 20 marker draws
- IMGUI (HUD and launcher)

**Godot** has no fast-Enter-Play-Mode or static cache: each run is a fresh process, so the geometry (DAM decode, BVH,
sweeps) is rebuilt on every run, on a worker thread. Panorama faces and mesh textures decode and build mipmaps on worker tasks;
the main thread only creates `Texture2DArray`/`ImageTexture`. Depth cubes come from the same
`CubeFaces.DepthCube` (Core) and upload one per frame.

## Checking against real Matterport spaces

When fidelity is in doubt, test on real spaces here instead of asking the user. The commands are in CLAUDE.md.
1. Download a few public spaces with the CLI. Public tours used so far: `rGMMnZDh3Tf` (the user's house),
   `2ds5iarQnk3`, `4FXijnxaBcJ` and `L4VL15Qoh2e` (Indiana University tours).
2. Run Godot `--compare`. It renders, at evenly spaced sweeps and four headings, Matterport's baked mesh texture
   (view 1) and the sweep photo (view 2).
3. Run `tools/compare_alignment.py`. At rest Matterport shows exactly that photo, and it baked the textures from
   the same photos, so the pairs must line up:
   - aligned spaces gave edge correlation 0.40-0.49 at 0° offset
   - a deliberately wrong rotation convention gave 0.10, so the script prints MISALIGNED
   - also look at a few `overlay_*.png` checkerboards
4. For measurement scale, ray-cast floor to ceiling above each sweep (all four looked physically right, see
   matterport-formats.md). Blurry baked textures lower the correlation without any offset; that's expected.

## 4. Reading what the user sends

- **Godot Output / Debugger ▸ Errors:** the same `[Matterportable]` lines (`GD.PushWarning` / `GD.PushError`).
  Ask for the Errors tab text, and for `MatterportCache/<id>/debug/load.log`, which both engines write.
- **Console lines** carry the first line plus a stack trace. The `[Matterportable]` prefix marks ours. Info lines
  only appear when the space's `VerboseLogging` setting is on, so warnings and errors are the signal.
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
4. In the reply, tell the user what to pull and what to look for in Unity, Godot and the browser: which
   Console/Output lines should change, or which view to check.
