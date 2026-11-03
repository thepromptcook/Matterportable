# Matterportable: notes for Claude

Turns a Matterport link into a navigable, photoreal environment in **three front ends that share one C# Core**:
- **Unity:** a package plus the Unity 6 project around it. The user runs **Unity 6000.5.0f1 on Windows** (URP, Input
  System, both configured by `Assets/Editor/MatterportProjectSetup.cs`).
- **Godot 4 (.NET):** `godot/`, which compiles `Runtime/Core` directly. The user opens `godot/project.godot` with
  Godot 4.4+ .NET on Windows.
- **Web:** `web/server` (ASP.NET Core: downloads and prepares spaces, serves the page), `web/wasm` (the Core as
  WebAssembly), `web/client` (three.js). `dotnet run --project web/server`, then http://localhost:8080. To be
  deployed for others later.

**This environment can't run Unity or reach Matterport's viewer page** (Cloudflare blocks cloud IPs; never get around
it), **but it runs Godot and the web page headless, and Matterport's data API and CDN work**, so the CLI downloads
public spaces. The user tests in Unity, Godot and the browser and pastes back Console/Output text or diagnostics.

For any change, load the skill `.claude/skills/matterportable-dev` (symptom → code map, workflow). Its reference files
are read on demand: `conventions.md` (per-subsystem rules: frames, rows, shaders, props, clean photos, web/Godot
specifics), `verification.md` (launcher arguments, fidelity and real-space checks, load timeline),
`matterport-formats.md` (facts about Matterport's data).

## Commands

```bash
tools/setup-dev.sh       # once per container: .NET 8 (+10), glslang, Godot, NuGet restore
tools/check.sh --fast    # ~13 s: core tests, CLI + Godot compile, shaders, metas, dead code (the inner loop)
tools/check.sh           # ~50 s: everything (Unity compile x3, CLI, Godot + web builds and demo runs, shaders, metas, dead code)
tools/godot-run.sh <launcher args>            # one headless Godot run; restores the csproj; prints our lines only
tools/web-check.sh --page <name> '<query>'    # one web page → /tmp/matterportable-web/<name>.png
dotnet test tools/Matterportable.Core.Tests --filter <Name>   # one test class
dotnet run --project tools/Matterportable.Cli -- download https://my.matterport.com/show/?m=<id>   # also: info, diagnose, masks, demo
# the user's house, sweep 35: move the corner chair / empty rooms (Godot: --empty, web: empty=1)
tools/godot-run.sh --open rGMMnZDh3Tf --mode walk --sweep 35 --yaw 46 --pitch 35 --move-prop -50 --screenshot /tmp/a.png
# reconstruction (CPU, pre-trained ONNX models): furniture painted out of the panoramas (masks ~3 min, painting ~20 min; --sweeps 35 redoes one)
dotnet run --project tools/Matterportable.Cli -- masks MatterportCache/<id> --size 160
pip install -r tools/reconstruct/requirements.txt && python3 tools/reconstruct/clean_panos.py MatterportCache/<id>
```

Add `/opt/dotnet` to `PATH` if `dotnet` isn't found. Read screenshots with Read to see what changed.

## Layout

```
Packages/com.thepromptcook.matterportable/
  Runtime/Core/       engine-agnostic C# (no UnityEngine): link, JSON, self-healing GraphQL, downloader, DAM/Draco
                      decoders, BVH, frames, alignment solver, synthetic demo house, diagnostics, and the viewer:
                      Viewer.cs (ViewerSettings, SpaceModel, ViewerText) · Navigator.cs (input → camera, ViewBinding,
                      Walk mode) · Walker.cs (body collision) · Props.cs (PropFinder, PropWorld) · PanoramaScheduler.cs
                      · Measurement.cs · Reconstruction.cs (prop masks, clean photos)
  Runtime/Space|Navigation|UI/   Unity adapters · Runtime/Resources/Matterportable/ shaders · Editor/ · Tests/Core (NUnit,
                      run by dotnet and Unity) · Tests/Editor · Tests/PlayMode
godot/scripts, godot/shaders   Godot adapters and shader ports (same file names)
web/server, web/wasm/Bridge.cs, web/client/js   web adapters (same file names)
tools/                CLI, check scripts, UnityCompileCheck (+ API stubs), reconstruct/ (Python, offline)
```

Data flow: link → model id → GraphQL (`MatterportApi.cs`) → `SpaceDownloader` → `MatterportCache/<id>/` →
`CalibrationPipeline` → `calibration.json` → `SpaceModel.Load` (also props: `objects.json`; clean photos:
`recon/clean/clean.json`) → navigator.

## Rules

- **Three front ends, one behaviour.** Viewer behaviour lives once in Core (`Navigator`, `Walker`, `PropWorld`,
  `PanoramaScheduler`, `MeasureTool`, `ViewerSettings`, `ViewerText`), changed there with a test in `Tests/Core`.
  Engine files are thin adapters with the same names; an adapter change in one front end is mirrored in the others
  in the same commit:

  | Unity (`Packages/.../Runtime/`) | Godot (`godot/`) | Web (`web/client/js/`) |
  |---|---|---|
  | `Space/MatterportSpace.cs` + `SpaceMesh` | `scripts/MatterportSpace.cs` | `MatterportSpace.js` |
  | `Space/PanoramaCache.cs` + `GpuTextures` | `scripts/PanoramaCache.cs` | `PanoramaCache.js` |
  | `Navigation/MatterportNavigator.cs` + `NavInput` | `scripts/MatterportNavigator.cs` | `MatterportNavigator.js` (+ `Bridge.Frame`) |
  | `Navigation/SweepMarkers.cs` | `scripts/SweepMarkers.cs` | `SweepMarkers.js` |
  | `Navigation/MatterportMeasure.cs` | `scripts/MatterportMeasure.cs` | `MatterportMeasure.js` |
  | `UI/MatterportLauncher.cs`, `UI/MatterportHud.cs` | `scripts/Launcher.cs`, `scripts/Hud.cs` | `Launcher.js`, `Hud.js` |
  | `Resources/Matterportable/*.shader` + `MatterportCommon.hlsl` | `shaders/*.gdshader` + `.gdshaderinc` | `shaders.js` |

- **Core and Unity code is C# 8** (no `new()`, `is not`, records, init; a local function can't capture an `in`
  parameter). Godot scripts may use newer C#. Unity-version differences go behind one helper (`MpUtil`); the compile
  check uses the 2021 API, so watch the user's Console for Unity 6 deprecations.
- **Every package file and folder needs a `.meta`** (`python3 tools/generate_metas.py`); `git mv` the `.meta` with
  a moved script. Commit Godot's `.uid` files with their scripts, and keep `Godot.NET.Sdk/4.4.0` in the csproj (the
  scripts restore it).
- **If you change `MatterportQueries`,** update the schema in `Tests/Core/FakeMatterport.cs`.
- **Keep the Console quiet:** info goes to `BufferLog` / `debug/load.log` (Console only with `VerboseLogging`);
  warnings only for things the user should act on.
- **Never commit `MatterportCache/`** (third-party content). Commit and push only to the session's branch, in focused
  commits.
- **Keep docs short and current, without being asked.** A change that adds a feature, moves code or teaches a lesson
  updates, in the same work: the dev skill (symptom map, workflow) and its `conventions.md`; the from-scratch skill
  and its `algorithms.md` / `pitfalls.md`; `matterport-formats.md` for data facts; this file only for rules every
  session needs. Keep each skill file under ~200 lines: put detail in a reference file, not in the main one.

## Debugging what the user reports

Ask for **Copy Diagnostics** (MatterportSpace inspector, also in Play mode): `debug/diagnostics.txt` with the manifest
summary, calibration, pruned query fields, `load.log`, the DAM structure and a redacted API sample. The dev skill maps
symptoms to code.
