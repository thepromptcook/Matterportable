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

## Algorithms worth getting right

- **Projection shader** (`MatterportMesh.shader`, `MatterportCommon.hlsl`):
  - Transform the fragment's direction from the sweep into panorama-local space and pick the major axis.
  - A per-face lookup table (right/up vectors plus slice index) comes from `calibration.json`.
  - Sample a 6-slice `Texture2DArray` with **analytic UV gradients** from the continuous direction derivatives,
    so mip selection has no seams at face edges. Within ~2 footprints of a face edge, sample with an explicit LOD
    (log2 of the same footprint) instead: gradient sampling there draws a dashed line along the seam.
  - **Occlusion:** point-sample the depth cube at the 4 nearest texels, run the visibility test on each and blend
    the 4 results bilinearly (percentage-closer filtering). Bilinear depth mixes near and far at silhouettes and
    dots every outline; unweighted 2×2 tests make blocks. Skip the test while the camera stands on the shown sweep:
    nothing it sees is hidden, and the depth map's resolution would only draw thin lines along outlines.
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
- **Walk mode** (free W/A/S/D):
  - **Body:** spheres from step height (30 cm) to the head, pushed out sideways by their deepest BVH contact
    (`Bvh.DeepestContact`). A flat contact (a table top or ceiling inside the body) refuses the move.
  - **Feet:** five downward rays find the highest walkable floor (|n.y| ≥ 0.64) up to a step above. The feet snap to
    it, falls use gravity, and a move with no floor within 4 m is refused (holes, windows, the scan's edge).
  - **Sub-steps:** 10 cm each, shorter than the radius, so walls are never crossed.
  - **View:** the photos of the two nearest sweeps in sight, weighted 1/d − 1/d₃ (d₃: the third nearest) with sticky
    slots. A sweep joins or leaves the pair at zero weight, so there are no pops; the depth cubes handle occlusion.
  - **Eye height:** the capture camera's height above its floor: the first walkable surface 1-2.5 m below the
    camera (a ray straight down often lands on a table top under the tripod), then a ring search for room to stand.
  - **Fidelity:** score Walk mode leave-one-out (at a sweep, render without its photo; SSIM/PSNR against the photo)
    and check the camera really stands at the photo's spot before trusting a score.
- **Movable props** (Walk mode):
  - **Finding them:** a 2.5D grid per floor, 5 cm columns with a 64-bit mask of 10 cm height bins (surfaces sampled
    at half a cell; floor and ceiling faces kept out). A column is *wall* when its surfaces reach within 0.3 m of the
    highest ceiling within half a metre (so a soffit over a doorway doesn't count) and fill 60 % of their span;
    *hanging* when its lowest surface is above 1.2 m; else *standing*, up to the top of its lowest run of bins (a
    lamp over a table stays). 8-connected standing patches are candidates. A triangle joins a patch when its centre
    is over it and every corner is over it or a neighbouring cell and below the local top + 15 cm. Keep pieces no
    longer than 3 m, no larger than 4.5 m², no taller than 2 m and filling 35 % of their oriented outline. Doors:
    0.55-1.3 m long, at most 0.2 m thick, 1.75-2.45 m tall, with wall cells at exactly one end (the hinge). The
    user's house: 47 props, 3 doors, 1 s.
  - **Drawing them:** a mesh per prop with its pose; the shader projects photos from the scanned position (the
    space matrix), so the prop carries its own pixels wherever it goes.
  - **Colliding:** the room's BVH with a skip mask over prop triangles, plus a small BVH per prop queried through
    the inverse pose. No second room BVH (WebAssembly would take seconds to build it).
  - **What it leaves behind:** store the piece each sweep saw next to the depth (an item buffer). The depth test's
    bias (~12 cm) can't separate a chair from the wall behind it; the piece can. Refuse a moved prop's pixels for
    other pieces (any of the four PCF taps, which also drops the outline at depth-texel resolution), and fill the hole
    with the photo's nearest other pixels (rings of 8 directions, 2° to 30°, at a coarse mip). Matterport's baked
    texture behind furniture holds a smear of it, so it is a poor fallback there.
  - **Growing the pieces:** furniture touching a wall loses those parts (wall columns). Add connected triangles
    within the bounds + 8 cm, but only non-vertical ones: vertical ones took bits of the wall (a hole in the wall
    showing the photo through it, and a wall patch carried with the chair).
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
- **Rendering fixes need measurements, not impressions** (`--fidelity` + `compare_fidelity.py`):
  - The dashed seam lines looked like mesh cracks or T-junctions; the mesh was fine (19 T-junction edges in a
    whole house). The cause was gradient sampling at panorama face seams; isotropic gradients didn't fix it, an
    explicit LOD did.
  - A finer mesh (Matterport's 3D tiles, 2.4× the triangles, 4096 px textures) rendered no better: the photos
    dominate. Don't render it for looks; it is for collision and reconstruction.
  - The first scores were wrong because the camera stood on a table top (the floor ray hit furniture): log the
    camera's offset from the photo's spot and require 0.
- **Draco in C#** (Matterport's tiles): only the sequential decoder is needed (no edgebreaker), with quantized
  attributes, difference prediction with the wrap transform, rANS tagged and raw symbols. rANS precision is
  clamp(⌊3·bits/2⌋, 12, 20) bits and the state base is 4× the precision. Validate against Google's draco3d (npm)
  value by value; a test fixture is easy to make with draco3d's encoder from a procedural grid.
- **Finding furniture in a decimated scan:** planar region growing (12°, 4 cm) splits the 50k mesh into 22,000
  regions and finds almost no walls, so whole rooms come out as one object. Height columns are robust to the noise.
  A triangle assigned by its centre alone drags in pieces of wall (long triangles): check every corner.
- **Props, engine side:** a `THREE.Vector4()` is (0, 0, 0, 1), so an "unset" tint turned the whole room black;
  Unity's unset global matrix is all zeros (the shader falls back to the object matrix while `_m33` is 0); Q/E
  already turn, so interaction is F. A scripted check must wait until the camera has its view: the first frame's
  pointer ray still has the old rotation.
- **Godot instance uniforms:** `SetInstanceShaderParameter("prop_id", 3)` with an int is silently ignored by a
  `float` uniform; pass `3f`.
- **Dropping onto the player:** the body's sphere test pushes it up onto the furniture (standing on the table).
  Test the dropped prop against the body first and refuse.
- **Signed tile links expire in ~5 minutes** (HTTP 410): download tiles right after the GraphQL query, before the
  panoramas.
- **Unplaced 360° photos** (no neighbours, no floor below, sometimes positioned at floor level) can't be walked from.
  Check that the body can stand before entering Walk mode.
- **Test walking on real scans in a scratch harness** (a console project compiling `Runtime/Core`):
  - Walk between neighbouring sweeps.
  - Count only pairs whose straight corridor is clear; Matterport's neighbour lists include pairs with walls
    between them.
  - Spawn the body at every sweep.
  - This found the unplaced-photo case and showed that collision wasn't too strict before any engine ran it.
