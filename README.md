# Matterportable

Turn a Matterport link into a navigable, photoreal environment in Unity.

Paste `https://discover.matterport.com/space/rGMMnZDh3Tf` (or any Matterport link or model ID), press Play, and walk
the space the way Matterport does. Click to move between 360° capture points (sweeps) with the same cross-fading
transition, drag to look around, step with W/A/S/D, and switch to Dollhouse or Floor plan views.

> **Status.** This was built in a cloud environment that could not run the Unity Editor and was blocked from
> Matterport's viewer, so it has **not yet been run against a real Matterport space or inside Unity**.
>
> **What has been verified:**
> - The engine-independent core passes 48 automated tests against a generated stand-in house written in Matterport's
>   formats. This covers downloading through a fake Matterport server with expiring links and renamed fields, mesh
>   decoding, coordinate conversion and the panorama alignment solver.
> - All Unity code compiles against Unity's API, in both editor and player configurations.
> - The shader HLSL compiles with glslang.
>
> The first run on your machine may surface format differences. See **Troubleshooting**: the importer writes
> diagnostics designed to make those quick to fix.

---

## Quick start

1. Open this folder as a project in **Unity 2021.3 LTS or newer** (Unity 6 recommended; Built-in or URP).
2. **Window ▸ Matterportable ▸ Import Space**.
3. Paste the link, keep **Panorama quality: High (2048 px)**, then click **Download & Create Scene**.
4. Press **Play**.

The space is downloaded once into `<project>/MatterportCache/<model id>/` (outside `Assets`, so Unity never imports
it). The panoramas are aligned automatically, and a scene with a camera rig is created in `Assets/Matterport/`.

**No network, or just want to check your setup?** Click **Generate Demo Space** in the same window. You get a
procedural house in exactly the same format, so you can try the whole pipeline offline.

**Fastest loop:** add a **Matterport Space** component to any scene (*GameObject ▸ Matterportable ▸ Matterport
Space + Camera*), paste a link into it and press Play. If the space isn't cached yet, it downloads automatically.

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
- **Fast Enter Play Mode.** *Tools ▸ Matterportable ▸ Enable Fast Enter Play Mode* turns off domain reload. The code
  is written to be safe without it.
- **Edit-mode preview.** The textured mesh and sweep positions show in the Scene view. Click a sweep handle, or
  **Look** in the inspector, to put the Scene camera there.
- **Play from any sweep.** The inspector's sweep list has a ▶ button per sweep, plus **Start** to set the default.
- **Live debugging.**
  - F1 shows FPS, panorama cache memory, load times and alignment score.
  - F2 cycles render views: mesh texture, panorama only, occlusion visibility and blend weights.
  - F3 labels sweeps, and P runs an automatic tour.
- **Headless tools.** A CLI (`tools/Matterportable.Cli`) downloads, inspects and exports spaces to OBJ without Unity.
- **Tests that don't need Unity.** `dotnet test tools/Matterportable.Core.Tests` runs the core suite in seconds. The
  same files also run in Unity's Test Runner (EditMode), alongside a few Unity-only tests.
- **Builds.** Spaces used by build scenes are bundled into `StreamingAssets` automatically during a build. You can
  toggle this under *Tools ▸ Matterportable*.

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
| *"Matterport's bot protection blocked the request"* | Use the **browser export**: in the import window click **Copy Browser Export Script**. Open `https://my.matterport.com/show/?m=<id>` in your normal browser, paste the script into the developer console (F12 ▸ Console) and press Enter. A `matterport-<id>.json` file downloads. Then click **Import Browser Export…** and pick it. Do this promptly, because the links inside expire. |
| Panoramas look rotated, mirrored or don't line up with the mesh | Inspector ▸ **Re-run Panorama Alignment**. If the score shown is low, open **Alignment (advanced)**: change *Sweep rotation*, try **Rotate faces 90°**, or edit faces directly. Changes apply live in Play mode and are saved as manual. |
| Mesh is sideways or inside out | Inspector ▸ Alignment ▸ *Mesh frame / Sweep frame / Flip winding*. |
| Download or decode error | Look in `MatterportCache/<id>/debug/`: `graph-query.graphql`, `graph-response.json` and `graph-pruned-fields.txt`. The CLI's `matterportable info <space dir>` summarises a cached space. These files are what's needed to fix a format difference quickly. |
| "Space only offers streamed mesh tiles" | Newer spaces may only have 3D-tile meshes, which aren't supported yet. The space still works panorama-only: click navigation with transitions, skybox rendering. |
| Pink / black surfaces | The project uses HDRP, which isn't supported. Use the Built-in render pipeline or URP. |

## Repository layout

```
Packages/com.thepromptcook.matterportable/   the Unity package
  Runtime/Core/       engine-independent C#: link parsing, GraphQL, downloader, DAM decoder, BVH, solver, demo space
  Runtime/            MatterportSpace (loader), PanoramaCache, MatterportNavigator, MatterportHud, settings
  Runtime/Resources/  shaders (projection, skybox, markers, face blit)
  Editor/             import window, inspector, scene builder, build hook
  Tests/Core/         NUnit tests (run in Unity and with `dotnet test`)
  Tests/Editor/       Unity-only EditMode tests
  Tools~/             browser-export.js (fallback exporter)
tools/
  Matterportable.Cli/        command-line tool (download, import, demo, info, obj, align, query, browser-script)
  Matterportable.Core.Tests/ runs Tests/Core outside Unity
  UnityCompileCheck/         compiles the Unity code against Unity's reference assemblies (no Unity install needed)
  check_shaders.py           compiles the shader HLSL with glslang
  test_browser_export.js     runs the browser exporter against a mock endpoint
  generate_metas.py          creates .meta files for new package files
```

## Developing without Unity

```bash
dotnet test tools/Matterportable.Core.Tests
```

```bash
dotnet build tools/UnityCompileCheck
```

```bash
dotnet build tools/UnityCompileCheck -p:Player=true
```

```bash
python3 tools/check_shaders.py
```

```bash
node tools/test_browser_export.js
```

```bash
dotnet run --project tools/Matterportable.Cli -- download https://discover.matterport.com/space/rGMMnZDh3Tf
```

```bash
dotnet run --project tools/Matterportable.Cli -- demo --size 1024
```

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
