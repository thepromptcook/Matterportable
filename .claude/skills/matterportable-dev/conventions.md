# Conventions per subsystem

Read the part you are changing. CLAUDE.md lists the rules every change follows; this file holds what each
subsystem must keep (frames, image rows, shaders, settings, view modes, walking, props, clean photos, measuring,
web and Godot specifics).


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
- **Panorama and depth sampling** (all three shader sets, measured with `--fidelity`): panoramas go through
  `MP_SamplePano` (gradients, but an explicit LOD along face edges, or seams draw dashed lines); occlusion goes
  through `MP_DepthVisibility` (the test on 4 point-sampled depth texels, blended), so depth textures stay
  point-sampled (two half floats: distance, piece seen). `UseDepth` is off while the camera stands on the shown sweep.
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
- **Props** (Walk mode's movable furniture and doors):
  - **Found** once per space by `PropFinder` in a 2.5D grid per floor (5 cm columns, 10 cm height bins): columns
    covered to near the ceiling are wall, columns whose lowest surface is above 1.2 m are hanging, the rest stand on
    the floor; each connected patch of standing columns is a candidate, sized like one piece of furniture. Thin,
    door-sized slabs with a wall at one end are doors (pivot = hinge foot). Saved in `objects.json` with a version
    and the mesh's key (a new frame finds them again); the web server ships them in `space.json`.
  - **Drawn** as one mesh per prop (`SpaceModel.MeshPieces`: piece 0 the room, i + 1 prop i) with its pose from
    `PropWorld.Poses` (world = rotation × scanned + translation), sharing the room's materials. The mesh shaders
    project photos from where a vertex was scanned (the space matrix: `_MP_SpaceToWorld`, `space_matrix`,
    `spaceMatrix`), not from where it is drawn, so a moved prop keeps its pixels. The tint (`PropWorld.Tint`:
    highlight, F2 "Props" view) is per object: MaterialPropertyBlock (Unity), `instance uniform` (Godot), a material
    per prop sharing the uniform objects (web; a `THREE.Vector4`'s w defaults to 1, so zero it).
  - **Used** with F (`NavKeys.Use`; Q/E already turn) or a click in Walk mode, on the prop under the pointer
    (`Navigator.HighlightProp`); the wheel turns what is carried. A drop that would land inside the body is
    refused (`DropRefused`, the hint says so). Collision: `PropWorld` is the Walker's `ICollider`, the room's BVH
    with prop triangles skipped (a mask, no second BVH) plus each prop's own BVH through its pose. Depth cubes and
    Inside-mode picking keep the scan as photographed. Once anything moved, the depth test stays on even at a sweep.
  - **What a moved prop leaves behind:** depth cubes have a second channel, the piece each sweep saw
    (`CubeFaces.DepthCube(…, SpaceModel.PieceOfTriangle)`; Unity RGHalf, Godot Rgh, web: each piece drawn into the
    cube with its `pieceId`). The shaders refuse a moved prop's photo pixels for any other piece (`MP_Hides`, moved
    flags in `_MP_Moved` / `moved_props` / `movedProps`, the drawn piece in `_MP_PropId` / `prop_id` (pass a float in
    Godot: an int is ignored) / `propId`). Where the sweep has clean photos (below), the room shows them there; without,
    the hole is filled from the photo's surroundings (`MP_HoleFill`: rings of directions around the hole, coarse mip)
    instead of the baked texture, which holds the prop.
  - **Clean photos** (`recon/clean`, from the reconstruction): `CleanPanoramas.Apply` gives a sweep `CleanFaces` (the
    painted face, else the photo) and `PaintedProps` (512 px 8-bit maps: prop + 1 that was painted out there). Once a
    prop moved, or in "empty rooms" (U, HUD button; `Navigator.SetEmptyRooms` → `PropWorld.Hidden`: props hidden and
    walked through), the navigator asks for the shown sweeps' clean photos (`PanoramaScheduler.RequestClean`, loader
    `LoadClean`, `CleanLoaded`) and binds them (`ViewBinding.CleanA/B`, `EmptyRooms` → `_MP_Clean` / `mp_clean` /
    `mpClean`). The room (piece 0) samples the clean photo where the painted map names a moved prop
    (`MP_PaintedMoved`: the photo shows a prop wider than the scan, thin legs and backs) or the depth test hid it, and
    everywhere in empty rooms. The sky does the same: the scan has holes behind furniture, and through them the sky
    showed the photo's chair. The demo house writes exact clean photos (`SyntheticOptions.CleanPhotos`: rendered
    again without the props the viewers find, after finding them), so the demo shows no ghost at all; the demo
    writers regenerate a demo without `recon/clean/clean.json`.
- **Reconstruction** (offline, toward a clean 3D rebuild of the house; see the from-scratch skill's plan): Python in
  `tools/reconstruct/`, pre-trained models only, CPU now and GPU later. Step 2: `PropMasks.PieceMask` (Core)
  ray-casts which prop each panorama pixel shows (CLI `masks`), counting room surfaces within 12 cm of a prop
  (`PropMasks.Halo`, `Bvh.Nearest`) as the prop: the scan misses thin parts the photo shows (a prop's box was too
  big: a diagonal chair in a corner took the floor and LaMa painted it wall-coloured). `clean_panos.py` sharpens the
  biggest pieces with MobileSAM (box prompts from those masks; ~0.5 s per face whatever its size, so only ~85 faces),
  grows every mask by the calibration slack, and paints them out with LaMa (fixed 512 px input, ~3.5 s per face) into
  `recon/clean/<sweep>/face<N>.jpg` + `painted<N>.png` and `clean.json` (the viewers read it at load).
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
  - **Panoramas** are six-layer `THREE.DataArrayTexture`s (photos, clean photos, painted maps; WebGL has 16 texture
    units, so six 2D textures per photo left no room). `faceArray` allocates storage without data
    (`source.dataReady = false`, `needsUpdate`, `renderer.initTexture`: every mip level when `generateMipmaps`), then
    each decoded face goes in with `renderer.copyTextureToTexture3D(new THREE.Texture(bitmap), array, box, layer)`
    (pass the region box: an ImageBitmap has no depth), two per frame, mipmaps once after the last layer. A closed
    ImageBitmap reports width 0: read sizes before uploading.
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
