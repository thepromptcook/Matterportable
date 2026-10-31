# Matterportable: notes for Claude

A Unity package that turns a Matterport link into a navigable, photoreal Unity environment, plus the Unity 6
project around it. The user runs **Unity 6000.5.0f1 on Windows**, using URP and the Input System; both are
configured by `Assets/Editor/MatterportProjectSetup.cs`.

**Your environment usually can't run Unity, and can't reach Matterport's viewer.** Matterport's Cloudflare bot
check blocks cloud IPs; don't try to get around it. Verify your work with `tools/check.sh`. The user tests in Unity
and pastes back Console output or a diagnostics report.

## Commands

```bash
tools/setup-dev.sh     # once per container: .NET 8 SDK, glslang, NuGet restore
tools/check.sh         # every check (~20 s): core tests, Unity compile x3, CLI, browser script, shaders, metas
tools/check.sh --fast  # core tests + scripts only
dotnet test tools/Matterportable.Core.Tests --filter Alignment
dotnet run --project tools/Matterportable.Cli -- help   # download / demo / info / obj / align / diagnose …
```

Add `/opt/dotnet` to `PATH` if `dotnet` isn't found.

## Layout

```
Packages/com.thepromptcook.matterportable/
  Runtime/Core/        engine-agnostic C# (noEngineReferences): link parsing, JSON, self-healing GraphQL
                       client, downloader, DAM decoder, BVH, frames, alignment solver, synthetic space, diagnostics
  Runtime/Space/       MatterportSpace (component: load lifecycle + queries), SpaceMesh (Unity mesh), SpaceRenderer
                       (shader globals), PanoramaCache (streaming), CalibrationRunner, DemoSpace
  Runtime/Navigation/  MatterportNavigator (camera, transitions, view modes), NavInput, SweepMarkers
  Runtime/UI/          MatterportLauncher (in-game start menu), MatterportHud, MpGui (shared styles)
  Runtime/             MatterportRig (space + camera), MatterportViewerSettings, Utilities
  Runtime/Resources/Matterportable/   shaders: Mesh (projection), Skybox, Marker, FaceBlit + MatterportCommon.hlsl
  Editor/              import window, inspector, scene builder, build hook
  Tests/Core/          NUnit, run by dotnet AND Unity · Tests/Editor/ Unity EditMode · Tests/PlayMode/ end-to-end
  Tools~/browser-export.js   generated from the C# query (fallback exporter)
Assets/Editor/MatterportProjectSetup.cs   project-level setup (URP, Input System, Launcher scene)
tools/                CLI, test runner project, UnityCompileCheck (+ API stubs), check scripts
```

The data flow is: link → model id → GraphQL (`MatterportApi.cs`) → `SpaceDownloader` →
`MatterportCache/<id>/` (`manifest.json`, `mesh/`, `textures/`, `panos/`, `debug/`) → `CalibrationPipeline` →
`calibration.json` → `MatterportSpace` load → navigator.

## Rules

- **Keep `Runtime/Core` free of `UnityEngine`.** Put Unity glue in `Runtime/Space|Navigation|UI`. New Core logic
  gets a test in `Tests/Core`.
- **Every package file and folder needs a `.meta`.** Run `python3 tools/generate_metas.py`. When moving a script,
  `git mv` its `.meta` too: scenes reference scripts by GUID.
- **If you change `MatterportQueries`:**
  - regenerate `Tools~/browser-export.js` with the CLI `browser-script` command
  - update the schema in `Tests/Core/FakeMatterport.cs`
  - `check.sh` fails if the script is stale
- **Unity 6 deprecations aren't caught here.** The compile check uses the Unity 2021 API, so put any API that
  differs between versions behind one helper. `MpUtil.FindFirst` / `MpUtil.FindAll` already wrap the find APIs.
  Watch for new deprecations in the user's Console.
- **Keep the Console quiet.** Info messages go to `BufferLog` and `debug/load.log`, and reach the Console only
  with `verboseLogging`. Use warnings only for things the user should act on.
- **Never commit `MatterportCache/`.** It holds third-party content.
- **Commits and pushes go to the session's feature branch.** Keep each commit focused.

## Conventions you must not break

- **Coordinate frames.** Unity is left-handed with Y up. Matterport data is right-handed with Z up:
  Unity = (x, z, y), and a quaternion (x, y, z, w) becomes (-x, -z, -y, w). Conversions live in `Frames` and
  `SpaceCalibration.WorldFromPano`.
- **Image rows are bottom-up everywhere** (row 0 = bottom), matching Unity. The PNG codec flips on read and write.
- **Cube faces.** `CubeFaces.Canonical` (C#) and `MP_FaceAxis` / `MP_DepthCoord` (HLSL) must match. Face index ==
  `SignedAxis`. Panorama face order comes from `calibration.json`, never hard-coded.
- **`calibration.Method` values:**
  - `default`: never solved, so the next load runs the solver once
  - `solver` / `synthetic`: solved
  - `unsolved`: tried and failed, so it isn't retried automatically
  - `manual`: set by the user in the inspector, and kept
- **Shader globals** are written only by `SpaceRenderer.Apply` (once per frame, in `LateUpdate`). Every shader
  declares them in `MatterportCommon.hlsl`. Change both together.

## Debugging what the user reports

Ask for **Copy Diagnostics** (MatterportSpace inspector or import window). It produces
`debug/diagnostics.txt` with the manifest summary, calibration, pruned query fields, `load.log`, the DAM structure
dump and a redacted API sample. The skill `.claude/skills/matterportable-dev` maps symptoms to code. Matterport
format facts confirmed on real data are in `.claude/skills/matterportable-dev/matterport-formats.md`; update that
file when the user's data teaches you something new.
