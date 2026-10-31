# Matterportable

Turn a Matterport link into a navigable, photoreal environment in Unity.

Paste `https://discover.matterport.com/space/rGMMnZDh3Tf` (or any Matterport link or model ID), press Play, and walk
the space the way Matterport does. Click to move between 360° capture points (sweeps) with the same cross-fading
transition, drag to look around, step with W/A/S/D, and switch to Dollhouse or Floor plan views.

> **Status.** It works on a real space ("359 N Ridgewood Pl", 58 sweeps) in Unity 6000.5 with URP.
>
> **Verified on every change, without Unity:**
> - 53 automated core tests. They run against a procedural house and a fake Matterport server, and cover downloads,
>   mesh decoding, coordinate frames and panorama alignment.
> - Compile checks of all Unity code in the editor, player and Input System configurations.
> - Shader HLSL compilation.
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
downloads, previews, aligns and diagnoses the space in Edit mode, and *Tools ▸ Matterportable* has the browser-export
fallback.

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
| 1 · 2 · 3 | Inside · Dollhouse · Floor plan | |
| Page Up / Down | | Show next / previous floor (or all) |
| `[` · `]` · Home | Previous / next sweep · start sweep | |
| P | Auto tour through all sweeps | |
| F1 · F2 · F3 · H | Stats · render debug view · sweep labels · hide hints | |

Every value (drag feel, transition speed, zoom limits, marker look, streaming budgets) lives in a
**Matterport Viewer Settings** asset (*Create ▸ Matterportable ▸ Viewer Settings*). Assign it to the space;
edits apply live during Play.

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
  - F3 labels sweeps, and P runs an automatic tour.
- **Headless tools.** A CLI (`tools/Matterportable.Cli`) downloads, inspects and diagnoses spaces without Unity.
- **Tests.** The core suite runs in seconds with `dotnet test tools/Matterportable.Core.Tests`. The same files run in
  Unity's Test Runner (EditMode), alongside Unity-only EditMode tests and a **PlayMode** test that loads a space,
  moves between sweeps and switches views.
- **Quiet Console, full logs.** Progress goes to `MatterportCache/<id>/debug/load.log`. Turn on **Verbose Logging**
  in the viewer settings to also see it in the Console.
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
   Unity: mesh + BVH ─► panorama streaming (texture arrays, LRU, progressive) ─► projection shader + navigator
```

**Why it looks like Matterport.** Matterport's realism comes from its photos, not its mesh. At a sweep, the shader
projects that sweep's 360° panorama onto the scanned geometry, so standing at a sweep shows exactly the original
photo. While moving, it cross-fades between the panoramas of the two sweeps, both projected onto the geometry.
That is the same transition Matterport uses.

Matterportable adds **occlusion awareness**. Depth maps rendered from each sweep let surfaces hidden from one sweep
take their colour from the other, or from the baked mesh texture, instead of smearing. Areas without geometry
(windows, outdoors) show the panorama as the sky. Dollhouse and Floor plan render the textured mesh with back-face
culling, so you see into the rooms.

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
| *"Matterport's bot protection blocked the request"* | Use the **browser export**: run *Tools ▸ Matterportable ▸ Copy Browser Export Script*. Open `https://my.matterport.com/show/?m=<id>` in your normal browser, paste the script into the developer console (F12 ▸ Console) and press Enter. A `matterport-<id>.json` file downloads. Then run *Tools ▸ Matterportable ▸ Import Browser Export…* and pick it. Do this promptly, because the links inside expire. |
| Panoramas look rotated, mirrored or don't line up with the mesh | Inspector ▸ **Re-run Panorama Alignment**. If the score shown is low, open **Alignment (advanced)**: change *Sweep rotation*, try **Rotate faces 90°**, or edit faces directly. Changes apply live in Play mode and are saved as manual. |
| Mesh is sideways or inside out | Inspector ▸ Alignment ▸ *Mesh frame / Sweep frame / Flip winding*. |
| Download, decode or display problem | Click **Copy Diagnostics** in the space's inspector and paste the report into your issue or chat. The raw files are in `MatterportCache/<id>/debug/`. |
| "Space only offers streamed mesh tiles" | Newer spaces may only have 3D-tile meshes, which aren't supported yet. The space still works panorama-only: click navigation with transitions, skybox rendering. |
| Pink / black surfaces | The project uses HDRP, which isn't supported. Use the Built-in render pipeline or URP. |

## Repository layout

```
Packages/com.thepromptcook.matterportable/   the Unity package
  Runtime/Core/        engine-independent C#: link parsing, GraphQL, downloader, DAM decoder, BVH, alignment
                       solver, demo space, diagnostics
  Runtime/Space/       MatterportSpace (loading), SpaceMesh, SpaceRenderer (shader state), PanoramaCache, CalibrationRunner
  Runtime/Navigation/  MatterportNavigator (camera, transitions, view modes), NavInput, SweepMarkers
  Runtime/UI/          MatterportLauncher (in-game menu), MatterportHud
  Runtime/Resources/   shaders (projection, skybox, markers, face blit)
  Editor/              MatterportSpace inspector, editor tools (downloads, diagnostics, menus)
  Tests/               Core (dotnet + Unity) · Editor (EditMode) · PlayMode
  Tools~/              browser-export.js (fallback exporter)
Assets/Editor/         project setup (URP, Input System, Launcher scene)
tools/                 check.sh, setup-dev.sh, CLI, test runner, Unity compile check, shader/meta/browser checks
CLAUDE.md, .claude/skills/   notes and skills that make future work with Claude Code faster
```

## Developing without Unity

```bash
tools/setup-dev.sh
```

```bash
tools/check.sh
```

`setup-dev.sh` prepares a machine once: .NET 8 SDK, glslang and Unity reference assemblies. `check.sh` runs every
check in about 20 seconds:
- the core tests
- Unity compile checks in editor, player and Input System configurations
- the CLI build
- that the browser script matches the query
- the browser export test
- the shader compile
- `.meta` coverage

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

- **Movement:** sweep-to-sweep only, like Matterport. There is no free walking, as requested.
- **Render pipelines:** Built-in and URP are supported; HDRP is not.
- **Platforms:** desktop is the target. Mobile and WebGL are untested (WebGL can't use the downloader).
- **Mesh tiles:** streamed 3D-tile meshes aren't decoded yet.
- **Tiled panoramas:** the importer handles tiled faces, but assumes tile rows count from the top of the face.

## Responsible use

Matterport spaces are other people's work, and Matterport's terms of service restrict copying content from its
platform. Use this only with spaces you own or have permission to use. Downloaded data stays on your machine
(`MatterportCache/` is git-ignored). Choose and add a license before you distribute this code.
