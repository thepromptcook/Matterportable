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
- **Iteration:**
  - cached, resumable downloads
  - Play from any sweep, live-tunable settings, F1 stats and F2 shader debug views
  - fast Enter Play Mode
  - tests that run without Unity
- **Target:** Unity 6000.5 on Windows with URP and the Input System. The project must open with a clean Console
  and no deprecation warnings, but the package should also run on 2021.3+ and the Built-in pipeline.
- **Second engine:** Godot 4 (.NET build) on Windows, working "the same way from the same links". It is imported
  with *Import Existing Project* from `godot/project.godot`.

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
Unity runtime
  MatterportSpace (load coroutine; worker threads for decode/BVH; geometry reused across Play sessions)
    ─ SpaceMesh ─ SpaceRenderer (shader globals) ─ PanoramaCache ─ GpuTextures (async decode → GPU blit + mips)
  MatterportNavigator (+ NavInput, SweepMarkers) · MatterportLauncher (start menu) · MatterportHud · MatterportRig
Editor: inspector (download with progress, preview, sweep list, Play-from-sweep, alignment fixes, Copy Diagnostics)
        + MatterportEditorTools (Tools menu: browser-export import/script, cache folder)
Project: Assets/Editor/MatterportProjectSetup.cs (URP + Input System + fast Enter Play Mode + Launcher scene)
Godot (godot/, .NET): Matterportable.csproj compiles Runtime/Core/** by link; scripts/ mirror the Unity runtime file
  by file (MatterportSpace = space + mesh + renderer, PanoramaCache, MatterportNavigator, SweepMarkers, Launcher,
  Hud, ViewerSettings, GodotUtil); shaders/ are ports of the HLSL. Same cache folder, so one download serves both.
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
   - Check: tests against `FakeMatterport` pass, including pruning, expired-link refresh and the browser-export
     import.
4. **Geometry:** protobuf reader/writer, DAM decoder/writer, BVH (binned SAH), SpaceGeometry, SweepBuilder.
   - Check: DAM round trips (simple and quantized), BVH matches brute force.
5. **Synthetic space and solver:** procedural house, cube math, alignment solver, geometry checks, pipeline.
   - Check: the solver recovers randomly scrambled face layouts, rotation modes and V flips. Y-up data and
     inverted winding are detected.
6. **Shaders.**
   - Write the shaders: projection with analytic gradients, depth-based occlusion and A/B blend weights; skybox;
     markers; face blit.
   - Check: glslang compiles every stage.
7. **Runtime:** MatterportSpace loading, PanoramaCache, SpaceRenderer, SpaceMesh, navigator, HUD, launcher, rig.
   - Check: the editor, player and Input System compile configurations all pass.
8. **Editor and project:** inspector, editor tools, setup script, `.meta` generation, PlayMode test.
   - Check: `tools/check.sh` is all green.
9. **Godot port** (after Unity works, or in parallel once the Core is done):
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
- **Transitions:** move the camera linearly with an ease curve, cross-fade with smoothstep(0.1–0.9), and preload
  the target panorama (small tier first). Mode changes fly between poses, fading projection out early when
  leaving a sweep and in late when arriving.

## Pitfalls already hit (don't repeat them)

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
  - Put the project in a subfolder: at the repository root, Godot would scan Unity's `Library/`.

- **Cloudflare blocks the viewer page from cloud containers.** The GraphQL endpoint works from the user's machine.
  Provide a browser-console export as a fallback instead of any bypass.
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
