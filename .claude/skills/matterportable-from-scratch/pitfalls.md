# Pitfalls already hit (don't repeat them)

Reference for the from-scratch skill, grouped by area. Search it (Grep) for the area you are working on.

- **Measure mode:**
  - Unity IMGUI has no line primitive; draw rotated thin textures (`GUIUtility.RotateAroundPivot`).
  - Two components reading IMGUI block rectangles need the list cleared in `LateUpdate`, not in one reader's
    `Update`.
  - Escape is shared with the launcher's menu: make the measure tool own it while open.
  - Godot's `Input.ParseInputEvent` is delivered on a later frame, so script multi-step input checks across frames.
  - A C# local function can't capture a `ref` or `in` parameter (CS1628); copy it to a local first.
- **Web:**
  - three r169 GLSL3 `ShaderMaterial`s don't get `gl_FragColor`; declare `layout(location = 0) out vec4`.
  - Sampler arrays can't be indexed by a run-time value in WebGL 2, and there are only 16 texture units: put each
    panorama in a six-layer `DataArrayTexture` (sampler2DArray). Allocate it empty (`source.dataReady = false`,
    `needsUpdate`, `renderer.initTexture`; every mip level when `generateMipmaps`), copy each ImageBitmap in with
    `copyTextureToTexture3D` and an explicit region box (an ImageBitmap has no depth), and generate mipmaps once after
    the last layer. A closed ImageBitmap reports width 0.
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
