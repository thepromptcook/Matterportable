#!/usr/bin/env python3
"""Paint the furniture out of a space's panoramas: the textures of the clean, empty room shell (CPU only).

    dotnet run --project tools/Matterportable.Cli -- masks MatterportCache/<id> --size 160
    python3 tools/reconstruct/clean_panos.py MatterportCache/<id> [--sam-faces 85] [--sam-min 0.03]

1. Which prop each panorama pixel shows comes from the scan (the CLI's `masks`: ray casts from every sweep).
2. Segment Anything (MobileSAM, ONNX) sharpens the outline of every piece big enough to matter, from a box around
   its scan mask. The encoder costs ~0.5 s per face on 4 cores whatever the size, so it runs on the faces with the
   biggest pieces only (--sam-faces); elsewhere the scan's mask, grown to cover calibration slack, is used.
3. LaMa (ONNX, fixed 512 px input, ~3.5 s) paints the pieces out, on a square crop around them.

Writes <space>/recon/clean/<sweep>/face<N>.jpg (only the faces that changed) and clean.json. Pre-trained models
are downloaded once into <cache>/_models (pip install onnxruntime numpy pillow). Not committed: MatterportCache/.
"""
import argparse, json, os, sys, time, urllib.request, zipfile
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
        out.paste(Image.composite(res, crop, m.filter(ImageFilter.GaussianBlur(3))), box[:2])
        return out


def grow(mask, pixels):
    """The mask grown by about `pixels`, at a quarter size (a large MaxFilter at full size takes minutes)."""
    h, w = mask.shape
    img = Image.fromarray((mask * 255).astype(np.uint8)).resize((w // 4, h // 4), Image.BOX)
    img = img.point(lambda v: 255 if v > 0 else 0).filter(ImageFilter.MaxFilter(2 * max(1, pixels // 4) + 1))
    return np.asarray(img.resize((w, h), Image.BILINEAR)) > 64


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('space')
    ap.add_argument('--sam-faces', type=int, default=85, help='faces Segment Anything sharpens (about 0.55 s each)')
    ap.add_argument('--sam-min', type=float, default=0.03, help='a piece must cover this share of a face for it')
    ap.add_argument('--min-hole', type=float, default=0.003, help='faces whose pieces cover less are left alone')
    ap.add_argument('--limit', type=int, default=0, help='only the first N faces (testing)')
    args = ap.parse_args()
    space = os.path.abspath(args.space)
    masks_dir = os.path.join(space, 'recon', 'masks')
    info = json.load(open(os.path.join(masks_dir, 'masks.json')))
    n = info['size'] ** 2
    faces = [f for f in info['faces'] if f['photo'] and sum(f['props'].values()) / n >= args.min_hole]
    if args.limit:
        faces = faces[:args.limit]
    # The faces with the biggest pieces get Segment Anything.
    ranked = sorted(faces, key=lambda f: -max(f['props'].values()))
    sam_faces = {(f['sweep'], f['face']) for f in ranked[:args.sam_faces] if max(f['props'].values()) / n >= args.sam_min}
    models = os.path.join(os.path.dirname(space), '_models')
    fetch_models(models)
    sam, lama = Sam(models), Lama(models)
    out_root = os.path.join(space, 'recon', 'clean')
    written, t_sam, t_lama, started = [], 0.0, 0.0, time.time()
    for i, f in enumerate(faces):
        face = Image.open(os.path.join(space, f['photo'])).convert('RGB')
        w, h = face.size
        ids = np.asarray(Image.open(os.path.join(masks_dir, f['mask'])).convert('RGB')).astype(np.int32)
        ids = Image.fromarray((ids[..., 0] + 256 * ids[..., 1]).astype(np.int32)).resize((w, h), Image.NEAREST)
        ids = np.asarray(ids)
        slack = max(4, w // 64)  # the scan's outline is a few cm off the photo's
        hole = np.zeros((h, w), bool)
        boxes, scan_masks = [], []
        for prop in f['props']:
            m = ids == int(prop) + 1
            if not m.any():
                continue
            scan_masks.append(m)
            ys, xs = np.nonzero(m)
            pad = slack * 2
            boxes.append((max(0, xs.min() - pad), max(0, ys.min() - pad), min(w, xs.max() + pad), min(h, ys.max() + pad)))
        if (f['sweep'], f['face']) in sam_faces and boxes:
            t = time.time()
            for (sm, iou), m in zip(sam.outline(face, boxes), scan_masks):
                # Trust the sharp outline when it agrees with the scan; otherwise the grown scan mask.
                overlap = (sm & m).sum() / max(1, m.sum())
                hole |= grow(sm | m, slack // 2) if iou > 0.7 and overlap > 0.5 else grow(m, slack)
            t_sam += time.time() - t
        else:
            for m in scan_masks:
                hole |= grow(m, slack)
        t = time.time()
        clean = lama.paint(face, hole)
        t_lama += time.time() - t
        folder = os.path.join(out_root, '%03d' % f['sweep'])
        os.makedirs(folder, exist_ok=True)
        name = 'face%d.jpg' % f['face']
        clean.save(os.path.join(folder, name), quality=92)
        written.append({'sweep': f['sweep'], 'face': f['face'], 'file': '%03d/%s' % (f['sweep'], name), 'sam': (f['sweep'], f['face']) in sam_faces})
        if (i + 1) % 10 == 0 or i + 1 == len(faces):
            print(f'{i + 1}/{len(faces)} faces, outlines {t_sam:.0f}s, painting {t_lama:.0f}s, total {time.time() - started:.0f}s', flush=True)
    json.dump({'faces': written}, open(os.path.join(out_root, 'clean.json'), 'w'), indent=1)
    print(f'{len(written)} clean faces ({len(sam_faces)} outlined by Segment Anything) in {out_root}')


if __name__ == '__main__':
    sys.exit(main())
