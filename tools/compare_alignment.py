#!/usr/bin/env python3
"""Measures how well the sweep photos line up with Matterport's mesh in renders made by the Godot launcher's
--compare mode (pairs s{sweep}_y{yaw}_mesh.png / _pano.png).

For each pair it finds the image shift (within +-MAX_SHIFT px) that best matches the edges of the two renders, and
reports it in degrees, with the normalised correlation of edges at zero shift and at the best shift. Aligned
photos give ~0 deg and a correlation around 0.4-0.5 (a wrong rotation convention gave 0.10); exit code 1 if not. A constant offset across sweeps means a rotation error; a
scrambled match means a wrong face layout. It also writes overlay_*.png (mesh/photo checkerboard) to look at.

Usage: python3 tools/compare_alignment.py DIR [--fov 75]
"""
import pathlib
import sys

import numpy as np
from PIL import Image

MAX_SHIFT = 24


def edges(path):
    g = np.asarray(Image.open(path).convert("L"), dtype=np.float32)
    gx = np.zeros_like(g)
    gy = np.zeros_like(g)
    gx[:, 1:-1] = g[:, 2:] - g[:, :-2]
    gy[1:-1, :] = g[2:, :] - g[:-2, :]
    return np.hypot(gx, gy)


def ncc(a, b):
    a = a - a.mean()
    b = b - b.mean()
    d = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / d) if d > 0 else 0.0


def best_shift(mesh, pano):
    h, w = mesh.shape
    m = MAX_SHIFT
    core = mesh[m:h - m, m:w - m]
    best = (-2.0, 0, 0)
    for dy in range(-m, m + 1, 2):
        for dx in range(-m, m + 1, 2):
            c = ncc(core, pano[m + dy:h - m + dy, m + dx:w - m + dx])
            if c > best[0]:
                best = (c, dx, dy)
    c0, dx0, dy0 = best
    for dy in range(dy0 - 1, dy0 + 2):          # refine to 1 px
        for dx in range(dx0 - 1, dx0 + 2):
            if abs(dx) <= m and abs(dy) <= m:
                c = ncc(core, pano[m + dy:h - m + dy, m + dx:w - m + dx])
                if c > best[0]:
                    best = (c, dx, dy)
    return best, ncc(core, pano[m:h - m, m:w - m])


def overlay(mesh_png, pano_png, out):
    a = np.asarray(Image.open(mesh_png).convert("RGB"))
    b = np.asarray(Image.open(pano_png).convert("RGB"))
    yy, xx = np.indices(a.shape[:2])
    mask = (((yy // 64) + (xx // 64)) % 2 == 0)[..., None]
    Image.fromarray(np.where(mask, a, b)).save(out)


def main():
    d = pathlib.Path(sys.argv[1])
    fov = float(sys.argv[sys.argv.index("--fov") + 1]) if "--fov" in sys.argv else 75.0
    pairs = sorted(d.glob("s*_mesh.png"))
    if not pairs:
        print("no s*_mesh.png files in", d)
        return 1
    rows = []
    for mesh_png in pairs:
        pano_png = mesh_png.with_name(mesh_png.name.replace("_mesh", "_pano"))
        mesh, pano = edges(mesh_png), edges(pano_png)
        deg_per_px = fov / mesh.shape[0]
        (c, dx, dy), c0 = best_shift(mesh, pano)
        rows.append((mesh_png.stem.replace("_mesh", ""), dx * deg_per_px, dy * deg_per_px, c0, c))
        overlay(mesh_png, pano_png, d / ("overlay_" + mesh_png.stem.replace("_mesh", "") + ".png"))
    print(f"{'view':16} {'dx deg':>7} {'dy deg':>7} {'corr@0':>7} {'best':>6}")
    for name, ax, ay, c0, c in rows:
        print(f"{name:16} {ax:7.2f} {ay:7.2f} {c0:7.3f} {c:6.3f}")
    off = np.array([[r[1], r[2]] for r in rows])
    print(f"median offset: {np.median(off[:, 0]):.2f} deg horizontal, {np.median(off[:, 1]):.2f} deg vertical; "
          f"median corr at zero shift {np.median([r[3] for r in rows]):.3f}")
    # Calibrated on real spaces: aligned photos score 0.40-0.49; a wrong rotation convention scored 0.10.
    aligned = np.median([r[3] for r in rows]) >= 0.25 and abs(np.median(off[:, 0])) < 0.5 and abs(np.median(off[:, 1])) < 0.5
    print("verdict:", "ALIGNED" if aligned else "MISALIGNED - photos and mesh disagree (check calibration.json)")
    return 0 if aligned else 1


if __name__ == "__main__":
    sys.exit(main())
