# Matterport data: what is known

Matterport doesn't document these formats. Facts marked **confirmed** were observed on the user's real space
(rGMMnZDh3Tf, "359 N Ridgewood Pl", 58 sweeps, 2 floors). Everything else is an assumption that the code
guards against. Update this file whenever real data teaches you something.

## Access

- **Viewer page and API endpoint:** the viewer page `my.matterport.com/show/?m=<id>` sits behind a Cloudflare
  managed challenge for cloud/datacenter IPs. Don't work around it.
- **GraphQL from the user's machine** (confirmed): the GraphQL endpoint the viewer uses,
  `POST https://my.matterport.com/api/mp/models/graph`, answers plain requests from the user's machine. Body:
  `{"operationName","query","variables"}`.
- **No login:** public spaces need none. Asset URLs are signed CDN links (`…?t=<token>`) that expire, so the
  downloader re-queries and retries on HTTP 401/403.
- **Fallback:** when the user's network is challenged, use `Tools~/browser-export.js`, run in the browser
  console on the viewer page.

## GraphQL schema

The server is graphql-java style. Validation errors carry a line/column location plus a message such as
`Validation error (FieldUndefined@[model/floors/index]) : Field 'index' in type 'ModelFloor' is undefined`.
`SelfHealingQuery` prunes fields by error line, and the pruned list is saved to `debug/graph-pruned-fields.txt`.

**Confirmed types and fields:**
- `model(id:)` returns `id`, `name`, `assets`, `floors`, `locations`.
- `ModelFloor` has **no** `index`; use `sequence` and `label`.
- `SkyboxPanorama` has **no** `options`.
- The location type is `AnchorLocation`, and it has **no** `anchor` field. Panoramas are under `locations[].pano`.

**`assets.meshes` in practice** (confirmed): `obj 500k`, `obj 50k`, `dam 50k` (`<internal id>_50k.dam`) and
`dam 500k` (`tesselate.dam`). `assets.textures`: `jpg 50k low` and `jpg 50k high`, whose URL template contains
`<texture>`.

**Still assumed** (accepted without errors, or not reported):
- `pano { position {x y z} rotation {x y z w} skyboxes { resolution status urlTemplate (<face> 0..5) } }`
- `neighbors`, `floor { id }`, `room { id }`
- tile fields on skyboxes

## Mesh (.dam)

- **Structure:** a protobuf `binary_mesh`. Field 1 holds *simple* chunks:
  - `vertices` (field 1) containing `xyz` floats
  - `faces` (field 2) containing uint32 indices
  - `chunk_name` (field 3) and `material_name` (field 4)

  Field 2 holds *quantized* chunks, which haven't been seen yet; their coding (absolute vs. delta) is decided
  per file.
- **Size** (confirmed): the 50k DAM has 18 chunks, about 78k vertices and 57.5k triangles.
- **UVs** (confirmed): they are **not** in vertices field 4 as first guessed. The decoder now takes any float
  field holding 2 values per vertex (or per corner), and this works on the real file. The exact field number is
  still unknown; it will be in `debug/dam-structure.txt` if it is ever needed.
- **Winding** (confirmed): triangles come out **inverted** relative to Unity after the Z-up→Y-up mirror. Only 3 %
  face the cameras before correction, so `CheckGeometry` sets `FlipWinding`.
- **Frames** (confirmed): positions are Z-up right-handed, in the same frame as the sweep positions. Cameras sit
  about 1.53 m above the floor; 55 of 58 sweeps hit a floor when ray-cast down.
- **Textures:** chunk material names map to texture files through the `<texture>` placeholder, as a zero-padded
  3-digit index.

## Panoramas

- **Faces:** six faces per sweep from the `urlTemplate` with `<face>` = 0..5. Sizes: 512 (preview and solver
  input) and 2048 (viewing). Tiled 2k/4k variants exist in the schema but aren't used yet.
- **Face layout:** the default guess (0 up, 1 front, 2 right, 3 back, 4 left, 5 down, rotation applied directly)
  looked right on the real space. The solver (`PanoAlignmentSolver`) verifies or replaces it per space and
  records the result in `calibration.json`.

## Cache layout (ours)

`MatterportCache/<id>/` contains:
- `manifest.json` (normalised, source-independent)
- `calibration.json`
- `mesh/*.dam`
- `textures/*.jpg`
- `panos/<sweep>/<size>/face0..5.jpg`
- `debug/`, holding `graph-query.graphql`, `graph-response.json`, `graph-pruned-fields.txt`, `load.log`,
  `dam-structure.txt` and `diagnostics.txt`

## Engines confirmed by the user on real data
- Unity 6000.5 (URP, Windows) and Godot 4 .NET (Windows, .NET 10 SDK) both load, render and navigate
  "359 N Ridgewood Pl" (rGMMnZDh3Tf) from the shared `MatterportCache/`.
