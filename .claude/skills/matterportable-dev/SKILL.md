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

### Running it here

```bash
tools/check.sh --fast        # core tests, CLI + Godot compile, shaders, metas, dead code (~13 s): the inner loop
tools/check.sh               # everything, ~50 s; must end "All checks passed" before a push
tools/godot-run.sh --open demo --mode walk --yaw 54 --pitch 35 --move-prop -70 --screenshot /tmp/a.png   # one Godot run
tools/web-check.sh --page moved 'open=rGMMnZDh3Tf&mode=walk&sweep=35&yaw=46&pitch=35&move=-50'           # one web page
```

Then **look at** the screenshots with Read (`/tmp/matterportable-godot/*.png`, `/tmp/matterportable-web/*.png` from
the full check). `godot-run.sh` builds the C#, restores the csproj Godot rewrites, and prints only `[Matterportable]`
lines and errors. Launcher arguments, the web page's URL parameters, fidelity scoring (`--fidelity`), real-space
alignment checks (`--compare`) and the load timeline are in [verification.md](verification.md). Conventions per
subsystem (frames, image rows, shaders, props, clean photos, web specifics) are in [conventions.md](conventions.md).


## 1. Set up (once per container)

`tools/setup-dev.sh`, then `export PATH=/opt/dotnet:$PATH`. `check.sh` covers: core tests, three Unity compile
configurations against reference assemblies, CLI, Godot (compile + demo runs), web (.NET 8 and 10 builds + demo
runs in Chromium), shaders through glslang, `.meta` coverage, unused members.

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
| A smear or outline where a moved prop stood | with clean photos (`recon/clean`): the painted maps (`MP_PaintedMoved`: the room takes the clean photo where a moved prop was painted out), the sky shaders (holes in the scan behind furniture show the sky, which must use the clean photo too), and the clean photo itself (open `recon/clean/<sweep>/face<N>.jpg` next to the photo and `painted<N>.png`: a part left in the photo means the mask missed it). Without: the piece channel of the depth cubes (`MP_Hides`: a moved prop's pixels only for that prop, any of the 4 taps) and `MP_HoleFill` (fill from the photo around the hole). An outline left at a wall can also be the prop's parts the finder left with the room (`PropFinder.GrowAlongMesh` takes only non-vertical connected triangles, since vertical ones took the wall). F2 views 1 (baked texture), 2 (panorama only), 3 (visibility: the hole is black); a fragment that ignores the view is the sky through a hole |
| A ghost where demo furniture stood | the demo's clean photos (`SyntheticSpace.WriteCleanPhotos`: rendered without the found props' triangles; `PropTests.TheDemoHouseWritesExactCleanPhotos`). Remove only props: the demo's pillar and tall cabinet aren't props, and clean photos without them would draw the wall behind onto them |
| "Empty rooms" (U) shows furniture, or the button is missing | `Navigator.SetEmptyRooms` → `PropWorld.Hidden` (props hidden: Unity `SpaceMesh.ApplyProps` `enabled`, Godot `Visible`, web `mesh.visible`); the room uses clean photos everywhere (`ViewBinding.EmptyRooms`). Furniture still there is not a prop (F2 → *Props*) or has no clean face in that sweep (`clean.json`). The button shows only with props and clean photos (`SpaceModel.HasCleanPanoramas`) |
| Reconstruction: furniture not painted out, wrong area, slow | `PropMasks.PieceMask` (scan hit, plus room surfaces within `Halo` = 12 cm of a prop: thin legs and chair backs the scan misses) + CLI `masks` (check the mask PNGs line up with the photo faces); `tools/reconstruct/clean_panos.py` (`--sam-faces`, `--sam-min`, `--min-hole`, `--limit N`, `--sweeps 35` to redo one sweep and keep the rest). Big holes paint badly (LaMa fills a big floor area with the wall's colour): keep masks tight. Shards of furniture in a clean photo come from the copy step (`Reprojection.Source`: a source photo showed a prop's unscanned parts; it must reject sight lines through props' grown boxes); `--no-fill` paints without copying, to compare. A ghost of a piece that is partly left: the scan's outline missed part of it (glass, thin parts): check the mask against the photo; Segment Anything's loose box should take it |
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
- **Shaders:** keep C# and HLSL math in sync ([conventions.md](conventions.md)). `tools/check_shaders.py` compiles them.
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
