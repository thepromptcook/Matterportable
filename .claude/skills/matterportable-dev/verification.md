# Verification and runtime reference

Read when running the engines headless, scoring rendering, checking real spaces or reasoning about load order.

## Seeing the web version run

```bash
tools/web-check.sh                   # demo house in headless Chromium: inside (move 0 → 1), dollhouse, measure, walk
```

```bash
tools/web-check.sh --compare rGMMnZDh3Tf 6 && python3 tools/compare_alignment.py /tmp/matterportable-web
```

The page also reads URL parameters, the same as Godot's command line: `?open=<link|demo>&sweep=N&mode=…&yaw=D&pitch=D&view=N&goto=N&walk=S&measure=1&move=D&empty=1`; run one with `tools/web-check.sh --page <name> '<query>'`.
`window.mp.checks` reports the result. A check that hangs: `window.mp.settleState()` shows what the page is still
waiting for. Headless rendering is all on the CPU, so the check runs Chromium on Mesa under xvfb with `aa=0&aniso=1`
(multisampling and anisotropic filtering off). Those cut a 131 s check to 24 s; real browsers keep both on.

## Seeing the Godot version run

```bash
tools/godot-check.sh                 # demo house: Inside (after a move 0 → 1), Dollhouse, Measure and Walk (into the table) screenshots, fails on any engine/script/shader error
```

```bash
tools/godot-check.sh --sweep 5 --view 2   # another sweep; F2 render view 2 = panorama only (3 visibility, 4 weights)
```

The Godot launcher takes these arguments (`tools/godot-run.sh <args>`, or after `--` on a Godot command line): `--open <link|demo>`, `--sweep N`,
`--mode inside|dollhouse|floorplan|walk`, `--yaw D`, `--walk S` (holds W for S seconds; the log line gives the camera
position), `--view N`, `--views 0,2,3` (with `--screenshot f.png`: one file per render view, `f_v0.png` …, from the
same spot), `--depth N` (depth cube size), `--fidelity dir` (below), `--pitch D` (with `--yaw`), `--move-prop D` (Walk mode:
F on what the middle of the view is on, turn D degrees, F again, turn back half way; the log gives where the prop went
or why not), `--empty` ("empty rooms"), `--measure` (places a two-segment line with real clicks, sends a
real Escape, starts another line; the log then says "1 measurement(s), drawing yes, menu closed"), `--goto N` (a real transition; the screenshot waits until it
finishes), `--screenshot file.png` (it quits after saving). Use them to
reproduce a bug in a single run. The demo's panoramas are rendered from the synthetic house, so at a sweep the
projection must line up exactly. Isolate a problem with views 1 (mesh texture), 2 (panorama) and 3 (visibility).

## Measuring visual fidelity (screenshots → numbers)

To judge a rendering change, score it instead of eyeballing it. Walk mode shows views where no photo was taken, so
the test is leave-one-out: at a sweep, render Walk mode **without** that sweep's photo and compare with the photo.

```bash
RES=960x540 tools/godot-run.sh --open rGMMnZDh3Tf --compare-sweeps 8 --fidelity /tmp/fid/a
```

```bash
python3 tools/compare_fidelity.py /tmp/fid/a /tmp/fid/b   # SSIM and PSNR per run (28 views each)
```

- **Check placement first:** the log ends "walk camera at most X cm from the photo's spot". It must be 0.0, or
  the scores measure the offset instead of the rendering (that hid the eye-height bug).
- The photos are rendered through the same shader (view 2), so a sampling change moves both sides: diff the
  images too (`numpy` mean absolute difference), and look at the worst pair with Read.
- History on the user's house: original shaders 0.611 SSIM / 18.16 dB → PCF + seam fix + exact eye height 0.639 /
  18.65 dB. No gain from: 1024 depth cubes, sharper blend weights (worse), the 3D-tile mesh (0.636). An oracle
  colour match would add only ~0.02, so exposure differences between sweeps are minor; the rest is geometry
  (photos projected onto an approximate mesh) and disocclusion.

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

When fidelity is in doubt, test on real spaces here instead of asking the user:
`dotnet run --project tools/Matterportable.Cli -- download <link>`, then
`RES=960x540 tools/godot-run.sh --open <id> --compare /tmp/cmp/<id>` and `python3 tools/compare_alignment.py /tmp/cmp/<id>`
(web: `tools/web-check.sh --compare <id> 6 && python3 tools/compare_alignment.py /tmp/matterportable-web`).
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
