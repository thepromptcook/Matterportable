---
name: matterportable-from-scratch
description: Blueprint for building a Matterport-link-to-Unity/Godot viewer like Matterportable from an empty repository - requirements, architecture, build order with acceptance checks, key algorithms and the pitfalls already hit. Use when asked to recreate, port or substantially redesign the project, or to start a similar "scan/panorama tour → Unity" importer.
---

# Rebuilding Matterportable from scratch

The existing repository is the reference implementation. Read it rather than re-deriving anything. This file
records the decisions behind it and the order that worked.

## Requirements (as the user settled them)

- **Input:** paste any Matterport link (`discover.matterport.com/space/<id>`, `my.matterport.com/show/?m=<id>`,
  embed links or a raw 11-character ID) **inside the running game**. A start menu opens on Play, and Esc returns
  to it.
- **Navigation:** sweep-to-sweep, like Matterport, with no free walking:
  - click to move, with the cross-fading transition
  - W/A/S/D steps to the neighbouring sweep in that direction, arrows turn
  - drag to look (the point under the cursor stays put), wheel zooms
  - Dollhouse and Floor plan views with floor filtering
- **Look:** as photoreal as Matterport, meaning the original 360° photos at each sweep.
- **Measure mode,** as close to Matterport's as possible (rules in `matterportable-dev/matterport-formats.md`):
  - clicks place points, with continuous lines on by default
  - Esc or Confirm ends a line; Esc again exits
  - snapping to corners, edges and existing points; Shift locks to an axis, Alt disables snapping
  - ft/in or metric labels
  - floor-plan lines are 2D and shown only in floor plan
  - measurements are temporary
- **Iteration:**
  - cached, resumable downloads
  - Play from any sweep, live-tunable settings, F1 stats and F2 shader debug views
  - fast Enter Play Mode
  - tests that run without Unity
- **Target:** Unity 6000.5 on Windows with URP and the Input System. The project must open with a clean Console
  and no deprecation warnings, but the package should also run on 2021.3+ and the Built-in pipeline.
- **Second engine:** Godot 4 (.NET build) on Windows, working "the same way from the same links". It is imported
  with *Import Existing Project* from `godot/project.godot`.
- **Third version:** a web browser, deployable for others later. It is a server plus page, with the Core running in
  the page as WebAssembly so behaviour is identical.

## Constraints that shape the design

- **No Unity, no Matterport access.** Cloud containers can't run Unity, and Matterport's viewer page is
  Cloudflare-challenged from cloud IPs; never try to get around that. So:
  - Put all logic in an **engine-agnostic C# Core** (`noEngineReferences`) tested with `dotnet test`.
  - Compile Unity code against **Unity reference DLLs from NuGet** (`Unity3D.SDK`), plus small stubs for the
    Input System and Test Runner.
  - Check shader HLSL with **glslang**.
  - Build a **procedural house written in the exact cache formats** (`SyntheticSpace`) and a **fake Matterport
    server** (schema validation with graphql-java-style errors, signed expiring links) so download, decode and
    alignment are tested end to end.
- **Undocumented formats.** Measure assumptions against the data instead of trusting them:
  - **Self-healing GraphQL:** one field per line, prune any field the server rejects (matched by error line),
    retry.
  - **Mesh decoding:** pick between ambiguous encodings by geometric plausibility (edge length vs. random vertex
    pairs, decided per file). Take UVs from whichever float field has the right count.
  - **Frames and winding:** choose the frames that put cameras above a floor inside the mesh, and the winding
    that makes visible faces point at the cameras.
  - **Panorama alignment solver:** see Algorithms.
  - **Diagnostics:** write `debug/` files and a single redacted diagnostics report, so one user round trip is
    enough to fix a mismatch.

## Architecture

```
Core (pure C#)
  MatterportLink → MatterportGraphClient + SelfHealingQuery → RemoteModelParser
  → SpaceDownloader (mesh first, material names → texture URLs, faces per tier; resumable, refreshes expired links)
  → cache folder (manifest.json, mesh, textures, panos, tiles, debug)
  MeshTiles + Draco: Matterport's 3D-tile mesh (tileset leaves, Draco GLBs → Core frame), saved for reconstruction
  DamDecoder → SpaceGeometry (frame conversion, per-material parts, BVH) → SweepBuilder (floor points, floors, neighbours)
  SpaceCalibrator.CheckGeometry + PanoAlignmentSolver → calibration.json      (CalibrationPipeline wires it up)
Core viewer (what all three front ends run; tested without any engine)
  SpaceModel.Load (decode + BVH + sweeps, cached) · ViewerSettings (one set of defaults) · ViewerText
  Navigator: NavigatorInput (pointer px, buttons, keys, pointer ray) → camera pose + fov, ViewBinding, markers
  PanoramaScheduler: tiers, priorities, LRU budget, pinned on-screen sweeps → IPanoramaLoader (engine) callbacks
  MeasureTool: state machine + snapping → MeasureOverlay (screen-space lines, dots, rings, labels)
  Walker: Walk mode's body against an ICollider (the scanned mesh is the collider: walls, furniture, stairs, ledges)
  PropFinder (objects.json) → PropWorld: movable furniture and doors (poses, carrying, door swings, collision)
Unity runtime (thin adapters)
  MatterportSpace (load coroutine, shader globals from the binding) ─ SpaceMesh ─ PanoramaCache (loader)
    ─ GpuTextures (async decode → GPU blit + mips)
  MatterportNavigator (NavInput → Navigator → camera, SweepMarkers) · MatterportMeasure · MatterportLauncher · MatterportHud · MatterportRig
Editor: inspector (download with progress, preview, sweep list, Play-from-sweep, re-run alignment, Copy Diagnostics)
        + MatterportEditorTools (downloads, diagnostics, cache folder menu)
Project: Assets/Editor/MatterportProjectSetup.cs (URP + Input System + fast Enter Play Mode + Launcher scene)
Godot (godot/, .NET): Matterportable.csproj compiles Runtime/Core/** by link; scripts/ are the same adapters file by
  file (MatterportSpace, PanoramaCache, MatterportNavigator, MatterportMeasure, SweepMarkers, Launcher, Hud,
  GodotUtil); shaders/ are ports of the HLSL. Same cache folder, so one download serves all three.
Web: server (ASP.NET Core, the Core natively: download, bundle, BVH + features) · wasm/Bridge.cs (the Core as
  WebAssembly: one Frame call per frame) · client/js (three.js adapters with the same file names)
```

## Build order (each step has a check before moving on)

1. **Toolchain.**
   - Install .NET 8 and glslang (`tools/setup-dev.sh`).
   - Create a compile-check project referencing `Unity3D.SDK`.
   - Check: a probe MonoBehaviour compiles.
2. **Core basics:** JSON DOM, link parser, logging, math types (Vec3, Mat3, Quat, SignedAxis, frames).
   - Check: link and JSON tests pass.
3. **GraphQL and downloader.**
   - Build the query builder, the self-healing runner, the parser, the HTTP abstraction and the downloader.
   - Check: tests against `FakeMatterport` pass, including pruning and expired-link refresh.
4. **Geometry:** protobuf reader/writer, DAM decoder/writer, BVH (binned SAH), SpaceGeometry, SweepBuilder.
   - Check: DAM round trips (simple and quantized), BVH matches brute force.
5. **Synthetic space and solver:** procedural house, cube math, alignment solver, geometry checks, pipeline.
   - Check: the solver recovers randomly scrambled face layouts, rotation modes and V flips. Y-up data and
     inverted winding are detected.
6. **Shaders.**
   - Write the shaders: projection with analytic gradients, depth-based occlusion and A/B blend weights; skybox;
     markers; face blit.
   - Check: glslang compiles every stage.
7. **Viewer in Core, then runtimes:** `Navigator`, `PanoramaScheduler`, `MeasureTool` with tests driving them
   frame by frame (fake loader, a three-sweep room). Then each runtime as adapters: MatterportSpace loading and
   shader state, PanoramaCache (loader), SpaceMesh, navigator (input → Core), HUD, launcher, rig.
   - Check: Core tests, then the editor, player and Input System compile configurations all pass.
   - Writing the viewer once in Core (instead of per engine) is what keeps three front ends identical. The first
     version wrote it three times and drifted (Unity had features the others lacked); moving it into Core removed
     about 2,900 lines of engine code for about 1,100 lines of Core.
8. **Editor and project:** inspector, editor tools, setup script, `.meta` generation, PlayMode test.
   - Check: `tools/check.sh` is all green.
9. **Real-space fidelity check:**
   - Matterport's API and CDN are reachable from cloud containers, so download public spaces.
   - Render photo-only vs baked-mesh-texture pairs and measure their offset and correlation (Godot `--compare` +
     `tools/compare_alignment.py`), with a negative control: break the calibration and confirm it fails.
   - Check floor-to-ceiling heights for scale.
   - Make the convention found on every real space the default.
9a. **Novel-view fidelity:** Godot `--fidelity` renders Walk mode at sweeps without their own photo, plus the photo;
   `tools/compare_fidelity.py` scores SSIM/PSNR. Change the shaders only for a measured gain, and port each gain to
   all three shader sets.
9c. **Web version:**
   - The browser can't call Matterport (no CORS), so an ASP.NET Core server reuses the Core to download spaces and
     prepare a per-space bundle.
   - The page uses three.js, with GLSL ported from the Godot shaders.
   - The Core is compiled to WebAssembly with `Microsoft.NET.Sdk.WebAssembly` (no workload).
   - Measure first: interpreted WebAssembly was about 25× slower than native, so ship the BVH and features from the
     server.
   - Check: `tools/web-check.sh` (Playwright screenshots), and `--compare` on real spaces.
9b. **Godot port** (after Unity works, or in parallel once the Core is done):
   - Create the .NET project linking the Core, then port the runtime file by file and the shaders function by
     function.
   - Check: `dotnet build` against `Godot.NET.Sdk` from NuGet, then `tools/godot-check.sh`. That runs Godot headless
     under xvfb with OpenGL/llvmpipe and saves Inside and Dollhouse screenshots; read them.
10. **Real data with the user:** expect two or three round trips (schema field names, UV location, winding).
   Record the facts in `matterportable-dev/matterport-formats.md`.

## References (read when needed)

- [algorithms.md](algorithms.md): projection and occlusion shaders, alignment solver, frames, measuring, Walk mode,
  props, transitions, and the reconstruction plan with what it took to remove furniture without remnants.
- [pitfalls.md](pitfalls.md): what went wrong before, by area (measure mode, web, Godot, Unity, data, rendering).
- `../matterportable-dev/matterport-formats.md`: facts about Matterport's data, confirmed on real spaces.
