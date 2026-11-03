#!/usr/bin/env python3
"""Paint the furniture out of a space's panoramas: the textures of the clean, empty room shell (CPU only).

    dotnet run --project tools/Matterportable.Cli -- masks MatterportCache/<id> --size 160
    python3 tools/reconstruct/clean_panos.py MatterportCache/<id> [--sam-faces N] [--sam-min 0.03]

1. Which prop each panorama pixel shows comes from the scan (the CLI's `masks`: ray casts from every sweep; the room's
   surfaces inside a prop's box count as the prop, for the thin parts the scan misses).
2. Segment Anything (MobileSAM, ONNX) sharpens the outline of every piece big enough to matter, from a box around
   a loose box around its scan mask (the scan misses glass tops and thin legs). The encoder costs ~0.5 s per face on
   4 cores whatever the size, about 2 minutes for a house; --sam-faces N limits it to the N faces with the biggest
   pieces. Everywhere the scan's mask, grown to cover calibration slack, is painted too.
3. What another sweep photographed behind a piece is copied in from that photo (the CLI's fill maps: the room
   surface behind each pixel and the nearest sweep that saw it unblocked), colour-matched on a band around the
   piece. On the user's house that covers about two thirds of what furniture hides.
4. LaMa (ONNX, fixed 512 px input, ~3.5 s) paints what no photo saw, on a square crop around it.

Writes <space>/recon/clean/<sweep>/face<N>.jpg (only the faces that changed), painted<N>.png (512 px, 8-bit: which
prop each pixel had painted out, prop + 1; the viewers show the clean photo there once that prop moved) and clean.json
(--sweeps redoes some sweeps and keeps the rest). Pre-trained models are downloaded once into <cache>/_models
(pip install onnxruntime numpy pillow). Not committed: MatterportCache/.
"""
import argparse, collections, gzip, json, os, sys, time, urllib.request, zipfile
import numpy as np
import onnxruntime as ort
from PIL import Image, ImageFilter

MODELS = {
    'lama_fp32.onnx': 'https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx',
    'mobile_sam.encoder.onnx': 'https://huggingface.co/vietanhdev/segment-anything-onnx-models/resolve/main/mobile_sam_20230629.zip',
}
DECODER = 'sam_vit_h_4b8939.decoder.onnx'  # ships in the MobileSAM zip (the SAM decoder works with MobileSAM's encoder)


def fetch_models(folder):
    os.makedirs(folder, exist_ok=True)
    for name, url in MODELS.items():
        if os.path.exists(os.path.join(folder, name)):
            continue
        target = os.path.join(folder, os.path.basename(url))
        print('downloading', url, flush=True)
        urllib.request.urlretrieve(url, target)
        if target.endswith('.zip'):
            with zipfile.ZipFile(target) as z:
                z.extractall(folder)
            os.remove(target)


class Sam:
    def __init__(self, folder):
        cpu = ['CPUExecutionProvider']
        self.enc = ort.InferenceSession(os.path.join(folder, 'mobile_sam.encoder.onnx'), providers=cpu)
        self.dec = ort.InferenceSession(os.path.join(folder, DECODER), providers=cpu)

    def outline(self, face, boxes):
        """Masks (bool, face size) for boxes (x0, y0, x1, y1 in face pixels), with the decoder's confidence."""
        w, h = face.size
        scale = 1024 / max(w, h)
        small = np.asarray(face.resize((round(w * scale), round(h * scale)), Image.BILINEAR)).astype(np.float32)
        emb = self.enc.run(None, {'input_image': small})[0]
        out = []
        for x0, y0, x1, y1 in boxes:
            coords = np.array([[[x0 * scale, y0 * scale], [x1 * scale, y1 * scale]]], np.float32)
            masks, iou, _ = self.dec.run(None, {
                'image_embeddings': emb, 'point_coords': coords, 'point_labels': np.array([[2, 3]], np.float32),
                'mask_input': np.zeros((1, 1, 256, 256), np.float32), 'has_mask_input': np.zeros(1, np.float32),
                'orig_im_size': np.array(small.shape[:2], np.float32)})
            best = int(np.argmax(iou[0]))
            m = Image.fromarray(((masks[0, best] > 0) * 255).astype(np.uint8)).resize((w, h), Image.BILINEAR)
            out.append((np.asarray(m) > 127, float(iou[0, best])))
        return out


class Lama:
    def __init__(self, folder):
        self.s = ort.InferenceSession(os.path.join(folder, 'lama_fp32.onnx'), providers=['CPUExecutionProvider'])

    def paint(self, face, hole):
        """face with the hole (bool, face size) painted in, from a square crop around it."""
        w, h = face.size
        ys, xs = np.nonzero(hole)
        x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
        side = min(max(w, h), max(512, int(2 * max(x1 - x0, y1 - y0))))
        cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
        left, top = int(np.clip(cx - side // 2, 0, w - side)), int(np.clip(cy - side // 2, 0, h - side))
        box = (left, top, left + side, top + side)
        crop = face.crop(box)
        m = Image.fromarray((hole * 255).astype(np.uint8)).crop(box)
        img = np.asarray(crop.resize((512, 512), Image.BICUBIC)).astype(np.float32) / 255
        msk = (np.asarray(m.resize((512, 512), Image.NEAREST)) > 127).astype(np.float32)
        res = self.s.run(None, {'image': img.transpose(2, 0, 1)[None], 'mask': msk[None, None]})[0][0].transpose(1, 2, 0)
        res = Image.fromarray(np.clip(res if res.max() > 2 else res * 255, 0, 255).astype(np.uint8)).resize((side, side), Image.BICUBIC)
        out = face.copy()
        # A wide feather: a hole's edge often cuts a soft shadow, and a sharp seam there reads as a rectangle on the wall.
        feather = m.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.GaussianBlur(max(3, side // 64)))
        out.paste(Image.composite(res, crop, feather), box[:2])
        return out


def grow(mask, pixels):
    """The mask grown by about `pixels`, at a quarter size (a large MaxFilter at full size takes minutes)."""
    h, w = mask.shape
    img = Image.fromarray((mask * 255).astype(np.uint8)).resize((w // 4, h // 4), Image.BOX)
    img = img.point(lambda v: 255 if v > 0 else 0).filter(ImageFilter.MaxFilter(2 * max(1, pixels // 4) + 1))
    return np.asarray(img.resize((w, h), Image.BILINEAR)) > 64


def read_fill(path):
    """The CLI's fill map (Reprojection.Serialize, gzipped): sweep, face, hidden, s, t per pixel, rows top-down."""
    b = open(path, 'rb').read()
    if b[:2] == b'\x1f\x8b':
        b = gzip.decompress(b)
    n = int.from_bytes(b[4:8], 'little')
    N, o = n * n, 8
    sweep = np.frombuffer(b, np.int16, N, o).reshape(n, n); o += 2 * N
    face = np.frombuffer(b, np.uint8, N, o).reshape(n, n); o += N
    o += N  # hidden
    s = np.frombuffer(b, np.uint16, N, o).reshape(n, n) / 65535.0; o += 2 * N
    t = np.frombuffer(b, np.uint16, N, o).reshape(n, n) / 65535.0
    return sweep, face, s, t


class Photos:
    """Other sweeps' photo faces as float arrays, a few kept in memory."""
    def __init__(self, space, paths):
        self.space, self.paths, self.cache = space, paths, collections.OrderedDict()

    def get(self, sweep, face):
        key = (sweep, face)
        if key not in self.cache:
            self.cache[key] = np.asarray(Image.open(os.path.join(self.space, self.paths[sweep][face])).convert('RGB'), np.float32)
            if len(self.cache) > 24:
                self.cache.popitem(last=False)
        self.cache.move_to_end(key)
        return self.cache[key]


def sample(img, x, y):
    """Bilinear samples of img (h, w, 3) at pixel coordinates x, y."""
    h, w = img.shape[:2]
    x, y = np.clip(x, 0, w - 1.001), np.clip(y, 0, h - 1.001)
    x0, y0 = x.astype(int), y.astype(int)
    fx, fy = (x - x0)[:, None], (y - y0)[:, None]
    return (img[y0, x0] * (1 - fx) * (1 - fy) + img[y0, x0 + 1] * fx * (1 - fy) +
            img[y0 + 1, x0] * (1 - fx) * fy + img[y0 + 1, x0 + 1] * fx * fy)


def reproject(face, hole, fill, photos):
    """face with the hole's pixels other sweeps saw copied in (gain-matched per source sweep on the band around the
    hole), and what is still missing."""
    sweep, src_face, s, t = fill
    w, h = face.size
    n = sweep.shape[0]
    target = np.asarray(face, np.float32)
    band = grow(hole, max(4, w // 128)) & ~hole
    ys, xs = np.nonzero(hole | band)
    my, mx = np.minimum(ys * n // h, n - 1), np.minimum(xs * n // w, n - 1)
    sw, sf = sweep[my, mx], src_face[my, mx]
    # The source position: bilinear between map pixels where the four agree on the source, else the nearest.
    fy, fx = (ys + 0.5) * n / h - 0.5, (xs + 0.5) * n / w - 0.5
    y0, x0 = np.clip(np.floor(fy).astype(int), 0, n - 2), np.clip(np.floor(fx).astype(int), 0, n - 2)
    same = np.ones(len(ys), bool)
    for dy, dx in ((0, 0), (0, 1), (1, 0), (1, 1)):
        same &= (sweep[y0 + dy, x0 + dx] == sw) & (src_face[y0 + dy, x0 + dx] == sf)
    ax, ay = np.clip(fx - x0, 0, 1), np.clip(fy - y0, 0, 1)
    def lerp(m):
        v = (m[y0, x0] * (1 - ax) * (1 - ay) + m[y0, x0 + 1] * ax * (1 - ay) + m[y0 + 1, x0] * (1 - ax) * ay + m[y0 + 1, x0 + 1] * ax * ay)
        return np.where(same, v, m[my, mx])
    us, vt = lerp(s), lerp(t)
    out = target.copy()
    filled = np.zeros((h, w), bool)
    pasted = np.zeros_like(target)
    has = np.zeros((h, w), bool)
    in_hole = hole[ys, xs]
    for src in np.unique(sw[sw >= 0]):
        idx = np.nonzero(sw == src)[0]
        colours = np.zeros((len(idx), 3), np.float32)
        for f in np.unique(sf[idx]):
            sel = sf[idx] == f
            img = photos.get(int(src), int(f))
            colours[sel] = sample(img, us[idx][sel] * img.shape[1] - 0.5, (1 - vt[idx][sel]) * img.shape[0] - 0.5)
        ring = ~in_hole[idx]  # band pixels: the photo is real there, so the two should agree
        gain = np.ones(3, np.float32)
        if ring.sum() >= 50:
            gain = np.clip(np.median(target[ys[idx][ring], xs[idx][ring]], 0) / np.maximum(np.median(colours[ring], 0), 1), 0.6, 1.6)
        pasted[ys[idx], xs[idx]] = colours * gain
        has[ys[idx], xs[idx]] = True
    # What is left of the colour difference on the band, spread smoothly into the hole (a cheap seamless blend): the
    # copy then meets the photo without a step at its edge.
    on_band = band & has
    if on_band.sum() >= 50:
        q = 4
        residual = np.where(on_band[..., None], target - pasted, 0)
        small = (w // q, h // q)
        r_img = Image.fromarray(np.clip(residual + 128, 0, 255).astype(np.uint8)).resize(small, Image.BOX)
        w_img = Image.fromarray((on_band * 255).astype(np.uint8)).resize(small, Image.BOX)
        radius = max(2, w // (8 * q))
        rb = np.asarray(r_img.filter(ImageFilter.GaussianBlur(radius)), np.float32) - 128
        wb = np.asarray(w_img.filter(ImageFilter.GaussianBlur(radius)), np.float32) / 255
        # r_img holds residual*weight around 128; dividing by the blurred weight interpolates it into the hole
        corr = np.where(wb[..., None] > 0.02, rb / np.maximum(wb[..., None], 0.02), 0)
        corr = np.asarray(Image.fromarray(np.clip(corr + 128, 0, 255).astype(np.uint8)).resize((w, h), Image.BILINEAR), np.float32) - 128
        pasted = pasted + corr
    keep = hole & has
    out[keep] = np.clip(pasted[keep], 0, 255)
    filled = keep
    result = Image.fromarray(out.astype(np.uint8))
    soft = Image.fromarray((filled * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(2))
    return Image.composite(result, face, soft), hole & ~filled


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('space')
    ap.add_argument('--sam-faces', type=int, default=400, help='faces Segment Anything sharpens, biggest pieces first (about 0.55 s each; all of them by default)')
    ap.add_argument('--sam-min', type=float, default=0.003, help='a piece must cover this share of a face for it')
    ap.add_argument('--min-hole', type=float, default=0.003, help='faces whose pieces cover less are left alone')
    ap.add_argument('--limit', type=int, default=0, help='only the first N faces (testing)')
    ap.add_argument('--no-fill', action='store_true', help="paint everything with LaMa (don't copy from other sweeps)")
    ap.add_argument('--sweeps', default='', help='only these sweeps, e.g. 3,35 (the others in clean.json are kept)')
    args = ap.parse_args()
    space = os.path.abspath(args.space)
    masks_dir = os.path.join(space, 'recon', 'masks')
    info = json.load(open(os.path.join(masks_dir, 'masks.json')))
    n = info['size'] ** 2
    only = {int(v) for v in args.sweeps.split(',') if v.strip()}
    faces = [f for f in info['faces'] if f['photo'] and sum(f['props'].values()) / n >= args.min_hole and (not only or f['sweep'] in only)]
    if args.limit:
        faces = faces[:args.limit]
    # The faces with the biggest pieces get Segment Anything.
    ranked = sorted(faces, key=lambda f: -max(f['props'].values()))
    sam_faces = {(f['sweep'], f['face']) for f in ranked[:args.sam_faces] if max(f['props'].values()) / n >= args.sam_min}
    models = os.path.join(os.path.dirname(space), '_models')
    fetch_models(models)
    sam, lama = Sam(models), Lama(models)
    photos = Photos(space, info.get('photos', []))
    out_root = os.path.join(space, 'recon', 'clean')
    written, t_sam, t_lama, t_fill, started = [], 0.0, 0.0, 0.0, time.time()
    for i, f in enumerate(faces):
        face = Image.open(os.path.join(space, f['photo'])).convert('RGB')
        w, h = face.size
        ids = np.asarray(Image.open(os.path.join(masks_dir, f['mask'])).convert('RGB')).astype(np.int32)
        ids = Image.fromarray((ids[..., 0] + 256 * ids[..., 1]).astype(np.int32)).resize((w, h), Image.NEAREST)
        ids = np.asarray(ids)
        slack = max(4, w // 64)  # the scan's outline is a few cm off the photo's
        regions = []  # (prop, scan mask, box around it)
        for prop in f['props']:
            m = ids == int(prop) + 1
            if not m.any():
                continue
            ys, xs = np.nonzero(m)
            pad = max(slack * 2, (xs.max() - xs.min() + ys.max() - ys.min()) // 8)  # loose: the scan misses glass tops and thin parts
            regions.append((int(prop), m, (max(0, xs.min() - pad), max(0, ys.min() - pad), min(w, xs.max() + pad), min(h, ys.max() + pad))))
        outlines = [None] * len(regions)
        if (f['sweep'], f['face']) in sam_faces and regions:
            t = time.time()
            outlines = sam.outline(face, [r[2] for r in regions])
            t_sam += time.time() - t
        hole = np.zeros((h, w), bool)
        painted = np.zeros((h, w), np.uint8)
        for (prop, m, _), outline in zip(regions, outlines):
            region = grow(m, slack)
            if outline is not None:
                sm, iou = outline  # the sharp outline too, when it is sure and mostly where the scan says
                if iou > 0.7 and (sm & region).sum() > 0.5 * max(1, sm.sum()):
                    region |= grow(sm, slack // 2)
            hole |= region
            if prop < 255:  # 8 bits
                painted[region & (painted == 0)] = prop + 1
        missing = hole
        if f.get('fill') and not args.no_fill:
            t = time.time()
            face, missing = reproject(face, hole, read_fill(os.path.join(masks_dir, f['fill'])), photos)
            t_fill += time.time() - t
        t = time.time()
        clean = lama.paint(face, missing) if missing.any() else face
        t_lama += time.time() - t
        folder = os.path.join(out_root, '%03d' % f['sweep'])
        os.makedirs(folder, exist_ok=True)
        name, painted_name = 'face%d.jpg' % f['face'], 'painted%d.png' % f['face']
        clean.save(os.path.join(folder, name), quality=92)
        # Widened by the paint's feather: the viewers switch to the clean photo inside the map, and the clean photo only
        # blends back to the photo outside the hole, so a tight map draws a hard edge where a shadow was removed.
        Image.fromarray(painted).resize((512, 512), Image.NEAREST).filter(ImageFilter.MaxFilter(17)).save(os.path.join(folder, painted_name), optimize=True)
        written.append({'sweep': f['sweep'], 'face': f['face'], 'file': '%03d/%s' % (f['sweep'], name),
                        'painted': '%03d/%s' % (f['sweep'], painted_name), 'sam': (f['sweep'], f['face']) in sam_faces})
        if (i + 1) % 10 == 0 or i + 1 == len(faces):
            print(f'{i + 1}/{len(faces)} faces, outlines {t_sam:.0f}s, copying {t_fill:.0f}s, painting {t_lama:.0f}s, total {time.time() - started:.0f}s', flush=True)
    index = os.path.join(out_root, 'clean.json')
    if only and os.path.exists(index):  # keep the other sweeps' faces
        written += [f for f in json.load(open(index))['faces'] if f['sweep'] not in only]
    json.dump({'faces': sorted(written, key=lambda f: (f['sweep'], f['face']))}, open(index, 'w'), indent=1)
    print(f'{len(faces)} faces painted ({len(sam_faces & {(f["sweep"], f["face"]) for f in faces})} outlined by Segment Anything), {len(written)} in {index}')


if __name__ == '__main__':
    sys.exit(main())
