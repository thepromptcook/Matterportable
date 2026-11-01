#!/usr/bin/env python3
"""Scores Walk mode's novel views: compares each dir/*_walk.png (rendered from a sweep's spot without its photo) with
dir/*_photo.png (the photo). Prints SSIM (structure, 1 = identical) and PSNR (dB) per sweep and overall.
Usage: python3 tools/compare_fidelity.py DIR [DIR2 ...]   (several dirs: a table comparing variants)
"""
import pathlib
import sys

import numpy as np
from PIL import Image


def load(path):
    a = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255.0
    return a @ np.array([0.299, 0.587, 0.114])


def box(a, r):
    """Mean over (2r+1)^2 windows via integral images (valid region)."""
    c = np.cumsum(np.cumsum(np.pad(a, ((1, 0), (1, 0))), 0), 1)
    k = 2 * r + 1
    return (c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]) / (k * k)


def ssim(x, y, r=4):
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    mx, my = box(x, r), box(y, r)
    vx, vy, cxy = box(x * x, r) - mx * mx, box(y * y, r) - my * my, box(x * y, r) - mx * my
    return float(np.mean(((2 * mx * my + c1) * (2 * cxy + c2)) / ((mx * mx + my * my + c1) * (vx + vy + c2))))


def psnr(x, y):
    mse = float(np.mean((x - y) ** 2))
    return 99.0 if mse == 0 else 10 * np.log10(1.0 / mse)


def score(d):
    d = pathlib.Path(d)
    rows = {}
    for walk in sorted(d.glob("*_walk.png")):
        photo = walk.with_name(walk.name.replace("_walk", "_photo"))
        if not photo.exists():
            continue
        x, y = load(photo), load(walk)
        rows.setdefault(walk.name[:4], []).append((ssim(x, y), psnr(x, y)))
    return rows


def main():
    dirs = sys.argv[1:]
    if not dirs:
        print(__doc__)
        return 1
    results = [score(d) for d in dirs]
    if len(dirs) == 1:
        for s, v in results[0].items():
            print(f"{s}: SSIM {np.mean([a for a, _ in v]):.3f}  PSNR {np.mean([b for _, b in v]):.2f} dB")
    for d, r in zip(dirs, results):
        allv = [x for v in r.values() for x in v]
        if allv:
            print(f"{d}: {len(allv)} views, SSIM {np.mean([a for a, _ in allv]):.4f}, PSNR {np.mean([b for _, b in allv]):.2f} dB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
