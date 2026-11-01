# Matterportable

Turn a Matterport link into a navigable, photoreal environment in **Unity**, **Godot 4** or a **web browser**.

Paste `https://discover.matterport.com/space/rGMMnZDh3Tf` (or any Matterport link or model ID), press Play, and walk
the space the way Matterport does. Click to move between 360° capture points (sweeps) with the same cross-fading
transition, drag to look around, step with W/A/S/D, switch to Dollhouse or Floor plan views, and measure anything
with Matterport's measurement tool. Or switch to **Walk** mode and move freely with W/A/S/D: walls, floors, stairs,
ceilings and furniture are solid.

> **Status.** It works on a real space ("359 N Ridgewood Pl", 58 sweeps) in Unity 6000.5 with URP.
>
> **Verified on every change, without Unity:**
> - Automated core tests. They run against a procedural house and a fake Matterport server, and cover downloads,
>   mesh decoding, coordinate frames, panorama alignment and the measurement tool.
> - Compile checks of all Unity code in the editor, player and Input System configurations.
> - Shader HLSL compilation.
>
> - The Godot project is compiled, then run headless on the demo house with screenshots of the Inside and
>   Dollhouse views, measure mode and Walk mode.
>
> - The web version is built, served and driven in headless Chromium, with screenshots of the demo house (move,
>   Dollhouse, measure mode, walking into furniture).
> - On four real Matterport spaces, the Godot and web versions both line up photo and mesh (`--compare`).
>
> A PlayMode test covers loading and navigation inside Unity.

---

## Quick start

1. Open this folder in **Unity 6000.5.0f1** (2021.3 LTS or newer also works).
2. On first open the project configures itself: it installs URP and the Input System for your editor version,
   turns off deprecated settings, and asks to restart Unity once. If the Input System asks to enable its backends,
   click **Yes**.
3. The **Launcher** scene opens. Press **Play**.
4. In the in-game menu, paste the link (or click **Paste**) and press **Open space**. The first visit downloads the
   space; after that it opens instantly from the **Downloaded spaces** list.
5. Press **Esc** or **☰ Menu** to go back and open another space.

**No network?** Choose **Try the offline demo house** in the menu. It's a procedural house in Matterport's format,
so you can check the whole pipeline offline.

The menu also appears automatically when you press Play in an empty scene (no camera, no Matterport Space). Downloads are cached
in `<project>/MatterportCache/<model id>/`, outside `Assets`, so Unity never imports them.

**In the editor:** *GameObject ▸ Matterportable ▸ Matterport Space + Camera* adds a space to any scene. Its inspector
downloads, previews, aligns and diagnoses the space in Edit mode.

## Quick start (Godot 4)

All three versions share one C# core: links, downloads, the cache folder, mesh decoding, panorama alignment,
navigation, panorama streaming and the measure tool. They behave the same way.

1. Install the **.NET build of Godot 4.4 or newer** (the download labelled ".NET" on godotengine.org; the standard
   build can't run C#) and the **.NET 8 SDK** (https://dotnet.microsoft.com/download).
2. In the Godot Project Manager, click **Import**, browse to this repository's **`godot`** folder and select
   `godot/project.godot`. Then click **Import & Edit**. (Don't pick the repository root: that's the Unity project.)
3. Press **Run Project** (F5). Godot builds the C# on the first run.
4. The same menu appears: paste a link and press **Open space**, or try the offline demo.

Spaces download to the same `MatterportCache/` folder Unity uses, so a space downloaded in one engine opens instantly in
the other. The controls are the same as in Unity; Esc returns to the menu.

## Quick start (web browser)

```bash
dotnet run --project web/server
```

Then open **http://localhost:8080**. You get the same start menu, controls and measure tool as in Unity and Godot. The
first run builds everything (about a minute). You need the .NET SDK (8 or newer).

How it fits together:
- **The server is needed.** Browsers can't fetch Matterport's data directly, so the small server (`web/server`)
  downloads spaces into the same `MatterportCache/` the Unity and Godot projects use. It prepares each space once
  and serves the page.
- **The page** renders with three.js (WebGL 2). The shared C# Core runs inside it as WebAssembly (navigation,
  panorama scheduling and the measure tool), so it behaves exactly as the engines do.
- **Hosting it for others:** `dotnet publish web/server -c Release -o out` gives a self-contained folder. Run it
  with:
  - `ASPNETCORE_URLS` (address and port)
  - `MATTERPORTABLE_CACHE` (where spaces are stored)
  - `MATTERPORTABLE_ALLOW_DOWNLOADS=false`, to serve only spaces you put in the cache yourself

  Only host spaces you own or have permission to share (see Responsible use).

### Use it in your own project

Add the package from git (*Package Manager ▸ + ▸ Add package from git URL*):

```
https://github.com/thepromptcook/Matterportable.git?path=/Packages/com.thepromptcook.matterportable
```

## Controls

| Input | Inside | Dollhouse / Floor plan |
|---|---|---|
| Left-drag | Look around (drag the panorama, like Matterport) | Orbit (Dollhouse) · Pan (Floor plan) |
| Right/middle-drag | – | Pan (Dollhouse) · Rotate (Floor plan) |
| Click | Move to the clicked spot (or marker) | Fly into the clicked spot |
| W / ↑ · S / ↓ | Step to the next sweep forward / back | Zoom in / out |
| A · D | Step to the next sweep left / right | Rotate |
| ← → · Q E | Turn | Rotate |
| Mouse wheel | Zoom (field of view) | Zoom (distance) |
| 1 · 2 · 3 · 4 | Inside · Dollhouse · Floor plan · Walk | |
| Page Up / Down | | Show next / previous floor (or all) |
| `[` · `]` · Home | Previous / next sweep · start sweep | |
| P | Auto tour through all sweeps | |
| F1 · F2 · H | Stats · render debug view · hide the on-screen controls | |
| M or **Measure** | Measure mode (see below) | Measure mode |

**Walk mode** (4, the **Walk** button, or *Start in Walk mode* in the start menu):

| Input | Walk |
|---|---|
| W / ↑ · S / ↓ | Walk forward / back |
| A · D | Step sideways |
| Shift | Run |
| ← → · Q E · Left-drag | Turn and look around |
| Mouse wheel | Zoom (field of view) |
| 1 · 2 · 3 | Back to Inside (moves to the nearest sweep) · Dollhouse · Floor plan |

You walk at the height the photos were taken from, and the scanned mesh is solid:
- **Blocking:** walls, furniture and anything taller than a step (30 cm) stop you, and you slide along them.
- **Stairs and floors:** you climb stairs and follow floors up and down, and you fall off ledges.
- **Ceilings and edges:** you can't squeeze under low ceilings or walk off the edge of the scan (open windows,
  holes in the mesh).
- **The view:** it blends the photos of the two nearest sweeps in sight, so it is exact at a sweep and a good
  approximation in between.
- **Walking needs a floor:** it isn't available at unplaced 360° photos, which have none.

**Measure mode** works like Matterport's: click to place points. With *Continuous lines* on (the default), each click
adds a connected segment; **Esc** or **✓ Confirm** ends the line, and Esc again leaves measure mode. Points snap to
corners, edges and other measurements. Hold **Shift** to lock the line to the nearest axis (the room's walls or
vertical), or **Alt** to place points without snapping. Click a measurement (or its row in the panel) to select it,
and press **Delete** or **✕** to remove it. The panel toggles *Snapping*, *Continuous lines* and feet/inches vs
metres. Lines drawn in Floor plan are 2D and only show there. Measurements are temporary, as they are for Matterport
visitors. While measuring, dragging still looks around and W/A/S/D still steps between sweeps.

Every value (drag feel, transition speed, zoom limits, marker look, streaming budgets) is in the space's **Settings**
(Matterport Space inspector; the same defaults in Godot and the web). Edits apply live during Play.

## Built for fast iteration

- **Paste-and-Play.** One component loads everything at runtime from the cache. Nothing is imported into `Assets`,
  so there is no import wait and no asset bloat.
- **Cached, resumable downloads.** Re-running skips finished files, and cancelling keeps partial progress.
- **Fast Enter Play Mode.** The project setup turns off domain reload, so Play starts in about a second. The
  decoded mesh is kept between Play sessions, so reopening a space skips decoding.
- **Smooth loading.** Images decode off the main thread straight into mipmapped GPU textures. Each photo point shows
  its 512 px preview first, then sharpens to 2048 px.
- **Edit-mode preview.** The textured mesh and sweep positions show in the Scene view. Click a sweep handle, or
  **Look** in the inspector, to put the Scene camera there.
- **Play from any sweep.** The inspector's sweep list has a ▶ button per sweep, plus **Start** to set the default.
- **Live debugging.**
  - F1 shows FPS, panorama cache memory, load times and alignment score.
  - F2 cycles render views: mesh texture, panorama only, occlusion visibility and blend weights.
  - P runs an automatic tour.
- **Headless tools.** A CLI (`tools/Matterportable.Cli`) downloads, inspects and diagnoses spaces without Unity.
- **Tests.** The core suite runs in seconds with `dotnet test tools/Matterportable.Core.Tests`. The same files run in
  Unity's Test Runner (EditMode), alongside Unity-only EditMode tests and a **PlayMode** test that loads a space,
  moves between sweeps and switches views.
- **Quiet Console, full logs.** Progress goes to `MatterportCache/<id>/debug/load.log`. Turn on **Verbose Logging**
  in the space's settings to also see it in the Console.
- **One-click diagnostics.** **Copy Diagnostics** in the space's inspector puts a redacted report about the
  space on your clipboard: manifest, alignment, load log, mesh structure and API sample. It's all that's needed to
  debug a space.
- **Builds.** Players download a space the first time it's opened, into `persistentDataPath`. To ship a space
  offline, copy its `MatterportCache/<id>` folder to `Assets/StreamingAssets/Matterport/<id>`.

## How it works

```
link ─► model id ─► GraphQL (same endpoint the web viewer uses) ─► manifest.json
                     │  self-healing: unknown fields are pruned & retried
                     ▼
        mesh (.dam) ─► textures ─► 6 cube faces per sweep (512 px preview + 2048 px)
                     ▼
     geometry checks (coordinate frames, triangle winding)  ─►  panorama alignment solver  ─► calibration.json
                     ▼
   Core viewer: mesh + BVH ─► navigator ─► panorama scheduler (LRU, progressive) ─► view binding per frame
                     ▼
   Unity / Godot / web: input in; camera, texture loading and projection shaders out
```

The viewer logic (navigation, which panoramas to load and show, measuring, the on-screen text) is written once in
the Core. Each front end only turns input into Core calls and draws what comes back, so all three behave the same.

**Why it looks like Matterport.** Matterport's realism comes from its photos, not its mesh. At a sweep, the shader
projects that sweep's 360° panorama onto the scanned geometry, so standing at a sweep shows exactly the original
photo. While moving, it cross-fades between the panoramas of the two sweeps, both projected onto the geometry.
That is the same transition Matterport uses.

Matterportable adds **occlusion awareness**. Depth maps rendered from each sweep let surfaces hidden from one sweep
take their colour from the other, or from the baked mesh texture, instead of smearing. The occlusion test is
filtered per pixel, so object outlines stay clean, and panorama seams are sampled so they don't show as lines.
These rendering choices were measured, not guessed: Walk mode is rendered at sweeps without their own photo and
scored against the photo. Areas without geometry (windows, outdoors) show the panorama as the sky. Dollhouse and
Floor plan render the textured mesh with back-face culling, so you see into the rooms.

**Why it should survive Matterport's undocumented formats.** Rather than hard-coding guesses, the importer measures
its assumptions against the data:
- **Query:** fields the server rejects are dropped automatically, and the final query and raw response are saved
  to `debug/`.
- **Mesh:** both simple and quantized `.dam` chunks are decoded. Ambiguous encodings are resolved by testing which
  interpretation gives plausible geometry.
- **Coordinate frames and winding:** frames are chosen so cameras sit above a floor inside the mesh. Winding is
  chosen so visible surfaces face the cameras.
- **Panorama alignment:** for several sweeps, the solver renders the textured mesh from the sweep position and
  compares it with each panorama face. It searches every rotation convention, all 48 orientations of each face
  image and both texture conventions, and keeps the best match. This works whatever face order and orientation
  Matterport uses. On the test house it recovers randomly scrambled conventions exactly.

## Troubleshooting

| Symptom | What to do |
|---|---|
| *"Matterport's bot protection blocked the request"* | Matterport challenged requests from your network. Try again later or from another network. |
| Panoramas look rotated, mirrored or don't line up with the mesh | Inspector ▸ **Re-run Panorama Alignment**. If it stays wrong, send **Copy Diagnostics**. `calibration.json` in the space folder can also be edited by hand; set `"method": "manual"` so it's kept. |
| Download, decode or display problem | Click **Copy Diagnostics** in the space's inspector and paste the report into your issue or chat. The raw files are in `MatterportCache/<id>/debug/`. |
| "Space only offers Matterport's 3D-tile mesh" | The tiles are downloaded (`tiles/` in the space folder), but the viewers don't show them yet. The space still works panorama-only: click navigation with transitions, skybox rendering. |
| Pink / black surfaces | The project uses HDRP, which isn't supported. Use the Built-in render pipeline or URP. |

## Repository layout

```
Packages/com.thepromptcook.matterportable/   the Unity package
  Runtime/Core/        engine-independent C#: link parsing, GraphQL, downloader, DAM decoder, BVH, alignment
                       solver, the viewer (navigation, panorama scheduling, measure tool), demo space, diagnostics
  Runtime/Space/       MatterportSpace (loading, shader state), SpaceMesh, PanoramaCache, CalibrationRunner
  Runtime/Navigation/  MatterportNavigator (input → Core navigator → camera), MatterportMeasure, NavInput, SweepMarkers
  Runtime/UI/          MatterportLauncher (in-game menu), MatterportHud
  Runtime/Resources/   shaders (projection, skybox, markers, face blit)
  Editor/              MatterportSpace inspector, editor tools (downloads, diagnostics, menus)
  Tests/               Core (dotnet + Unity) · Editor (EditMode) · PlayMode
Assets/Editor/         project setup (URP, Input System, Launcher scene)
godot/                 the Godot 4 (.NET) project: compiles Runtime/Core directly, plus Godot versions of the space,
                       panorama cache, navigator, HUD, launcher (scripts/) and shaders (shaders/)
web/                   the web version: server/ (ASP.NET Core; downloads and prepares spaces with the Core),
                       wasm/ (the Core as WebAssembly), client/ (three.js page: the same files as the engines, in JS)
tools/                 check.sh, setup-dev.sh, godot-check.sh, CLI, test runner, Unity compile check, other checks
CLAUDE.md, .claude/skills/   notes and skills that make future work with Claude Code faster
```

## Developing without Unity

```bash
tools/setup-dev.sh
```

```bash
tools/check.sh
```

`setup-dev.sh` prepares a machine once: .NET 8 SDK, glslang, Unity reference assemblies and a headless Godot.
`check.sh` runs every check in under a minute:
- the core tests
- Unity compile checks in editor, player and Input System configurations
- the CLI build
- the shader compile
- the Godot compile, plus a headless Godot run of the demo that saves screenshots (`tools/godot-check.sh`)
- the web build, plus a headless Chromium run of the demo that saves screenshots (`tools/web-check.sh`)
- `.meta` coverage
- a dead-code scan

The command-line tool works on spaces without Unity:

```bash
dotnet run --project tools/Matterportable.Cli -- download https://discover.matterport.com/space/rGMMnZDh3Tf
```

```bash
dotnet run --project tools/Matterportable.Cli -- diagnose MatterportCache/rGMMnZDh3Tf
```

**Working with Claude Code:**
- `CLAUDE.md` holds the project map, rules and conventions.
- `.claude/skills/matterportable-dev` is the change/verify/debug loop, with a symptom-to-code map. It includes the
  Matterport format facts learned from real data.
- `.claude/skills/matterportable-from-scratch` is the blueprint for rebuilding or porting the project.

## Limitations

- **Walk mode** sees and collides with the scanned mesh only:
  - Thin things the scan missed, such as railings, lamp poles and chair legs, don't block you, and look a little
    ghosted between sweeps (the photos are projected onto whatever surface is behind them).
  - Glass and mirrors are whatever the scan made of them.
- **Render pipelines:** Built-in and URP are supported; HDRP is not. Godot: Forward+, Mobile and Compatibility.
- **Web:** the server has no JPEG decoder, so it doesn't run the photo alignment solver. It uses Matterport's
  layout (confirmed on every real space tested), or the calibration Unity or Godot saved for the space.
- **Godot editor tools:** the Godot version has the in-game menu, navigation and HUD. The Unity inspector tools
  (re-run alignment, Copy Diagnostics) have no Godot equivalent yet; use the Unity inspector or the CLI `diagnose`
  command, which work on the same cache folder.
- **Platforms:** desktop is the target. Mobile and WebGL are untested (WebGL can't use the downloader).
- **Mesh tiles:** Matterport's finer 3D-tile mesh is downloaded and decoded (Draco), but not shown: it rendered no
  better than the standard mesh, because the photos carry the detail. Spaces that offer only tiles are
  panorama-only.
- **Tiled panoramas:** the importer handles tiled faces, but assumes tile rows count from the top of the face.

## Responsible use

Matterport spaces are other people's work, and Matterport's terms of service restrict copying content from its
platform. Use this only with spaces you own or have permission to use. Downloaded data stays on your machine
(`MatterportCache/` is git-ignored). Choose and add a license before you distribute this code.
