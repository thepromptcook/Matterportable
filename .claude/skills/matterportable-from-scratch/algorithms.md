# Algorithms worth getting right

Reference for the from-scratch skill; read the section you are building.

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

## Reconstruction plan (the user's goal: a clean 3D house with real objects)

Projected photos can't hide what moving furniture reveals, so the target is an offline rebuild loaded as a normal
scene: (1) clean shell (walls/floors fitted, textured from photos, hidden spots inpainted), (2) objects outlined in the
photos, (3) objects completed as 3D assets (photos + image-to-3D, library fallback), (4) delighting and real lights,
(5) one glTF package for all three engines. Pre-trained models only (no training), CPU now, GPU later.
Measured on 4 CPU cores: MobileSAM encoder ~0.5 s per face at any input size (it pads to 1024; the quantized model is
slower; 4 processes × 1 thread = 1 × 4 threads, ~2 faces/s), decoder 0.1 s; LaMa 3.5 s per 512 px inpaint. So outline
only faces with big pieces, using box prompts from the scan's own prop masks, and grow the scan mask elsewhere.
PIL `MaxFilter` with a big kernel on a 2048 px mask takes minutes: dilate at a quarter size.

Step 2 is in the viewers (all three): the painted faces load as a second panorama per sweep, plus an 8-bit map of
which prop each pixel had painted out. The room draws the clean photo where the map names a prop that moved, and
everywhere in "empty rooms" (props hidden). What it took to get no remnants (sweep 35 of rGMMnZDh3Tf, the corner
chair, is the test case; compare with `--move-prop` / `move=` and `--empty` / `empty=1`):
- **The scan's prop masks miss thin parts** (chair backs and legs, often not in the mesh at all), so the photo shows
  them on the wall behind. Mark room surfaces within ~12 cm of a prop's triangles as the prop (a nearest-triangle
  query that skips the room's). A 2D dilation of the depth cube's piece channel was not enough (the back posts were
  4° from the scan's chair), and the prop's box was too much (a diagonal chair's box took a big floor area, which
  LaMa painted wall-coloured).
- **Choose the clean photo from the painted map**, not the depth test: it says exactly where the clean photo differs.
- **Copy before inventing:** most of what furniture hides in one photo was photographed from another sweep. For each
  hidden pixel, ray-cast the scan with the props skipped to the room surface behind, then take the nearest other
  sweep that sees that point first, not at a grazing angle, with no prop within 12 cm of it and no prop's box (grown
  by 10 cm) across the line of sight. Without the last test the copies were full of chair-leg shards (the scan misses
  thin parts the photos show). Try sources in one order per face (the sweep that sees most of the hidden area first,
  from a 64 px pass), or the copy is a patchwork of perspectives. Match exposure per source on a band just outside
  the hole, spread what is left of the difference smoothly into the copy (blurred residual / blurred weight), then
  let LaMa paint only what no sweep saw.
- **Copy floors only:** a copied patch of plain wall from another viewpoint differs in exposure enough to read as a
  lighter rectangle; floors carry texture painting can't invent. And widen the painted map by the paint's feather,
  or the viewer's switch to the clean photo draws a hard edge where a shadow was removed.
- **Glass and reflections:** the scanner sees through a glass table top, so the scan's outline misses it and the
  unpainted part reads as a ghost; a loose box prompt lets Segment Anything take the whole table. A polished floor
  reflects furniture outside every outline; LaMa stretches the reflection into a soft smear (still open). On the user's house that copies about a quarter of the hidden pixels (two thirds before the
  sight-line test); the floor around the dining chairs came out clean wood instead of a smear.
- **The sky shows through holes in the scan** behind furniture: give the sky shader the same rule. (A fragment that
  ignores the F2 debug views is the sky.)
