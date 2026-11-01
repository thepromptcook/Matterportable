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
  → cache folder (manifest.json, mesh, textures, panos, debug)
  DamDecoder → SpaceGeometry (frame conversion, per-material parts, BVH) → SweepBuilder (floor points, floors, neighbours)
  SpaceCalibrator.CheckGeometry + PanoAlignmentSolver → calibration.json      (CalibrationPipeline wires it up)
Core viewer (what all three front ends run; tested without any engine)
  SpaceModel.Load (decode + BVH + sweeps, cached) · ViewerSettings (one set of defaults) · ViewerText
  Navigator: NavigatorInput (pointer px, buttons, keys, pointer ray) → camera pose + fov, ViewBinding, markers
  PanoramaScheduler: tiers, priorities, LRU budget, pinned on-screen sweeps → IPanoramaLoader (engine) callbacks
  MeasureTool: state machine + snapping → MeasureOverlay (screen-space lines, dots, rings, labels)
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

## Algorithms worth getting right

- **Projection shader** (`MatterportMesh.shader`, `MatterportCommon.hlsl`):
  - Transform the fragment's direction from the sweep into panorama-local space and pick the major axis.
  - A per-face lookup table (right/up vectors plus slice index) comes from `calibration.json`.
  - Sample a 6-slice `Texture2DArray` with **analytic UV gradients** from the continuous direction derivatives,
    so mip selection has no seams at face edges.
  - At a sweep the result is exactly the photo; geometry only matters while moving.
  - Blend weights give B its share as the transition progresses (`t`). Each panorama takes over where the other
    is occluded, using space-aligned depth cubes ray-cast on worker threads, and falls back to the baked mesh
    texture.
- **Alignment solver:**
  - For about 8 sweeps, ray-cast the textured mesh into a 32² cube per sweep, for both V conventions.
  - For each rotation convention (direct, inverse, identity), score all 48 axis-aligned orientations of each
    source face by normalised cross-correlation against the panorama.
  - Solve the face-to-axis assignment over 720 permutations, separately for proper and mirrored layouts.
  - Report the score and margin. A failed solve is stored as `unsolved` and not retried on every load.
- **Coordinate frames:** Unity is left-handed Y-up; Matterport is right-handed Z-up, so swap y and z. That mirror
  flips handedness, and therefore winding. Image rows are bottom-up everywhere.
- **Measurement:**
  - **Snapping:** mesh features are welded crease/boundary edges (unsigned normal angle > 40°) plus corners (3 or
    more feature edges, or 2 at an angle) in a 0.5 m grid. The tolerance is 14 px converted to metres at the hit
    distance. Priority: existing points, then corners, then edges.
  - **Shift:** picks the axis (room walls from the area-weighted mean of 4×wall-normal-angle, or vertical) nearest
    the drag direction, and takes the point on that axis line nearest the pointer ray.
  - **Visibility:** outside Inside view, picks and occlusion skip back faces and geometry above the clip height,
    matching what the user sees.
  - **Drawing:** the Core projects into screen space with the navigator's camera (vertical FOV, square pixels): hidden
    pieces faded, white lines with shadows, white pill labels per segment, a + cursor with a ring when snapped.
- **Transitions:** move the camera linearly with an ease curve, cross-fade with smoothstep(0.1–0.9), and preload
  the target panorama (small tier first). Mode changes fly between poses, fading projection out early when
  leaving a sweep and in late when arriving.

## Pitfalls already hit (don't repeat them)

- **Measure mode:**
  - Unity IMGUI has no line primitive; draw rotated thin textures (`GUIUtility.RotateAroundPivot`).
  - Two components reading IMGUI block rectangles need the list cleared in `LateUpdate`, not in one reader's
    `Update`.
  - Escape is shared with the launcher's menu: make the measure tool own it while open.
  - Godot's `Input.ParseInputEvent` is delivered on a later frame, so script multi-step input checks across frames.
  - A C# local function can't capture a `ref` or `in` parameter (CS1628); copy it to a local first.
- **Web:**
  - three r169 GLSL3 `ShaderMaterial`s don't get `gl_FragColor`; declare `layout(location = 0) out vec4`.
  - Sampler arrays can't be indexed by a run-time value in WebGL 2: use six samplers and a branch.
  - `ImageBitmap` ignores `texture.flipY`; decode with `imageOrientation: 'flipY'`.
  - Target `net$(BundledNETCoreAppTargetFrameworkVersion)` with `RollForward=LatestMajor`, so the user's .NET 10 SDK
    builds and runs it without .NET 8 installed.
  - XML comments in csproj can't contain `--` (bit twice).
  - Building one project from another: run `Restore` and `Publish` as separate MSBuild calls. .NET 8 tolerated
    `Targets="Restore;Publish"`; .NET 10 produced an empty `_framework` from it ("Failed to fetch dynamically
    imported module …/dotnet.js"). Test with the user's SDK version (check.sh builds with .NET 8 and 10).
  - Headless browser checks render on the CPU: use Mesa through xvfb (not SwiftShader) and turn off MSAA and
    anisotropy for them. Otherwise "wait until loaded" checks crawl at 2 fps.
  - Draw the sky box last at the far plane (`gl_Position.xyww`, depth test on), not first under everything.
- **Godot:**
  - It needs its **.NET build** and `dotnet` on PATH; otherwise it crashes on start ("Failed to load hostfxr").
  - Opening the project **rewrites the csproj SDK version** to the editor's version, so commit an old one (4.4.0)
    and restore it after headless runs.
  - **Winding:** mirroring Z for Godot's right-handed frame made Core's triangle order front-facing as it is.
    Reversing it "to be safe" turned the dollhouse inside out (you saw the roof). Check culling with a dollhouse
    screenshot.
  - **Rows:** Godot images are top-down while Core and Unity are bottom-up. Flip in the luma loader, flip V in the
    shader for file-backed textures, and use `1 - v` for mesh UVs, but not for depth data written by C#.
  - Use `source_color` on photo samplers and the Linear tonemapper, so photos aren't washed out.
  - Sky shaders run a radiance pass; return a flat colour when `AT_CUBEMAP_PASS`.
  - State set before a node's first-frame initialisation gets overwritten (`--mode` was lost); apply it at the end
    of initialisation.
  - Commit each new script's `.uid` with the script. A missing one gets generated on the user's machine and then blocks
    their next pull ("untracked files would be overwritten").
  - Put the project in a subfolder: at the repository root, Godot would scan Unity's `Library/`.

- **Cloudflare blocks the viewer page from cloud containers.** The GraphQL endpoint and CDN work from both the
  user's machine and cloud containers. Never bypass a challenge. (A browser-console exporter fallback was built and
  later removed: no one needed it, and it had to be regenerated on every query change.)
- **Guessed GraphQL fields can be wrong** (`floors.index`, `skyboxes.options`, `locations.anchor` don't exist).
  Self-healing plus `debug/graph-pruned-fields.txt` turns this into a one-round fix.
- **DAM UVs weren't where expected**, so detect them generically. The **winding is inverted**; detect that too.
- **Zero-thickness double-sided walls** in synthetic scenes cause random back-face hits; give walls thickness.
- **A tiny-chunk mesh makes the delta/absolute heuristic a coin flip**; decide once per file, weighted by triangle
  count.
- **Unity specifics:**
  - `FileUtil` clashes with `UnityEditor.FileUtil`, so name Core helpers distinctly (`IoUtil`).
  - A struct can't share its name with a method in the same class.
  - `GUI.EnabledScope` doesn't exist at runtime.
  - Verbatim strings break on embedded `"`.
- **Unity 6.5:**
  - `FindObjectsSortMode` is obsolete; Unity 6 deprecates `enterPlayModeOptionsEnabled`.
  - Built-in RP, the old Input Manager and Dynamic Batching all warn at startup. Configure URP and the Input
    System from a project script with `Client.AddAndRemove` (no version pins), and set player settings through
    `SerializedObject`.
  - A confirmation dialog in auto-setup gets dismissed; just do the setup.
- **Unity Hub "missing version":** `ProjectVersion.txt` must name an installed editor (`6000.5.0f1`).
- **Installing the Input System package triggers a prompt** unless Active Input Handling is switched too.
- **An empty scene shows nothing on Play.** The launcher bootstraps itself into scenes with no camera and no
  space, and the setup script creates and opens `Assets/Matterport/Launcher.unity`.
- **Keep the Console quiet** (info goes to `debug/load.log`); the user reads every line as a problem.
- **Progressive loading needs one queue entry per (sweep, size).** Deduplicating per sweep made the large request
  replace the small one, so the preview never showed.
- **Never decode images on the main thread in Play mode.** `Texture2D.LoadImage` plus `Compress()` hitched about
  100 ms per texture. Use `UnityWebRequestTexture` (worker decode) with a GPU blit into a mipmapped render texture.
- **Build only what the main workflow needs.** The user wanted the in-game launcher, so the editor import window
  and the build-scene bundling hook became dead weight and were removed. Keep the editor to inspector actions and
  menus.
- **Unity settings:** a `[Serializable]` Core class works as an inspector field (no ScriptableObject needed), but
  inspector edits may replace the instance; copy them into the one the Core holds in `OnValidate`.
- **Script order:** a component that reads another's per-frame result (measure reads `Navigator.Clicked`) needs
  `[DefaultExecutionOrder]`; Unity's order is otherwise undefined.
- **The default `ViewBinding` (all zeros) clips everything above y = 0.** Start from `ViewBinding.None`.
