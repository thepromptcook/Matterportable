// Mirrors PanoramaCache (Unity, Godot): streams sweep panoramas (six face textures each), the smallest tier first and
// then the large one, one load per sweep at a time, within an LRU memory budget. The browser decodes images off the
// main thread (createImageBitmap); GPU uploads are spread over frames. Occlusion depth cubes are rendered on the GPU
// (a cube camera at the sweep), which is how the browser gets them in milliseconds.
import * as THREE from 'three';
import { worldVertex, depthFragment } from './shaders.js';

const now = () => performance.now() / 1000;

export class PanoramaCache {
  constructor(space) {
    this.space = space;
    this.entries = new Map();
    this.queue = [];
    this.activeLoads = 0;
    this.maxConcurrentLoads = 2;
    this.isPinned = () => false;
    this.depthQueue = [];
    this.uploads = [];
    this.disposed = false;
    const r = space.renderer;
    this.depthSupported = r.capabilities.isWebGL2 && (r.extensions.has('EXT_color_buffer_float') || r.extensions.has('EXT_color_buffer_half_float'));
    this.depthMaterial = new THREE.ShaderMaterial({
      vertexShader: worldVertex, fragmentShader: depthFragment, glslVersion: THREE.GLSL3,
      uniforms: { sweepPos: { value: new THREE.Vector3() } }, side: THREE.DoubleSide,
    });
  }

  get(sweep) { return this.entries.get(sweep) ?? null; }
  isReady(sweep) { return !!this.get(sweep)?.faces; }

  getOrCreate(sweep) {
    let e = this.entries.get(sweep);
    if (!e) this.entries.set(sweep, e = { sweep, faces: null, size: 0, loadingSize: 0, depth: null, depthRequested: false, lastUsed: now(), loadSeconds: 0 });
    return e;
  }

  get bytesUsed() {
    let b = 0;
    for (const e of this.entries.values()) {
      if (e.faces) b += e.size * e.size * 4 * 6 * 4 / 3;
      if (e.depth) b += this.space.settings.depthMapSize ** 2 * 8 * 6;
    }
    return b;
  }

  tierAtMost(sweep, maxSize) {
    const tiers = this.space.sweeps[sweep].tiers;
    let best = null;
    for (const t of tiers) if (t.size <= maxSize && (!best || t.size > best.size)) best = t;
    return best ?? tiers[0] ?? null;
  }

  /** Ask for a sweep at up to maxSize pixels per face. Lower priority loads first. */
  request(sweep, maxSize, priority) {
    if (this.disposed || sweep < 0 || sweep >= this.space.sweeps.length) return;
    const tier = this.tierAtMost(sweep, maxSize);
    if (!tier) return;
    const e = this.getOrCreate(sweep);
    e.lastUsed = now();
    if (e.size >= tier.size || e.loadingSize >= tier.size) return;
    const i = this.queue.findIndex((q) => q.sweep === sweep && q.tier.size === tier.size);
    if (i < 0) this.queue.push({ sweep, tier, priority });
    else if (priority < this.queue[i].priority) this.queue[i] = { sweep, tier, priority };
  }

  /** The smallest tier first (quick preview), then up to maxSize. */
  requestProgressive(sweep, maxSize, priority) {
    if (sweep < 0 || sweep >= this.space.sweeps.length) return;
    const smallest = this.space.sweeps[sweep].tiers[0];
    if (!this.isReady(sweep) && smallest) this.request(sweep, smallest.size, priority - 0.5);
    this.request(sweep, maxSize, priority);
  }

  touch(sweep) {
    const e = this.get(sweep);
    if (e) e.lastUsed = now();
  }

  requestDepth(sweep) {
    if (this.disposed || !this.depthSupported || !this.space.hasMesh || this.space.settings.depthMapSize <= 0 || sweep < 0 || sweep >= this.space.sweeps.length) return;
    const e = this.getOrCreate(sweep);
    if (e.depthRequested) return;
    e.depthRequested = true;
    this.depthQueue.push(sweep);
  }

  update() {
    if (this.disposed) return;
    // GPU uploads: two face textures per frame, so a 2048 px panorama never stalls a frame.
    for (let n = 0; n < 2 && this.uploads.length; n++) {
      const u = this.uploads.shift();
      this.space.renderer.initTexture(u.texture);
      if (--u.job.pending === 0) u.job.done();
    }
    // One depth cube per frame.
    const depthSweep = this.depthQueue.shift();
    if (depthSweep !== undefined && this.entries.has(depthSweep)) this.renderDepth(this.entries.get(depthSweep));

    while (this.activeLoads < this.maxConcurrentLoads) {
      let best = -1;
      for (let i = 0; i < this.queue.length; i++) {
        const e = this.get(this.queue[i].sweep);
        if (e && e.loadingSize > 0) continue;
        if (best < 0 || this.queue[i].priority < this.queue[best].priority) best = i;
      }
      if (best < 0) break;
      const req = this.queue.splice(best, 1)[0];
      const entry = this.getOrCreate(req.sweep);
      if (entry.size < req.tier.size) this.load(entry, req.tier);
    }
    this.evictOverBudget();
  }

  renderDepth(entry) {
    const size = this.space.settings.depthMapSize;
    const target = new THREE.WebGLCubeRenderTarget(size, {
      type: THREE.HalfFloatType, format: THREE.RGBAFormat, generateMipmaps: false,
      minFilter: THREE.LinearFilter, magFilter: THREE.LinearFilter,
    });
    const camera = new THREE.CubeCamera(0.03, 1000, target);
    const pos = this.space.sweepWorldPosition(entry.sweep);
    camera.position.copy(pos);
    this.depthMaterial.uniforms.sweepPos.value.copy(pos);
    const r = this.space.renderer;
    const clear = r.getClearColor(new THREE.Color());
    const alpha = r.getClearAlpha();
    r.setClearColor(new THREE.Color(1000, 0, 0), 1);
    this.space.depthScene.overrideMaterial = this.depthMaterial;
    camera.update(r, this.space.depthScene);
    r.setClearColor(clear, alpha);
    entry.depth?.dispose();
    entry.depth = target;
  }

  evictOverBudget() {
    const budget = Math.max(64, this.space.settings.panoMemoryBudgetMB) * 1024 * 1024;
    let used = this.bytesUsed;
    if (used <= budget) return;
    const candidates = [...this.entries.values()].sort((a, b) => a.lastUsed - b.lastUsed);
    for (const e of candidates) {
      if (used <= budget) break;
      if (this.isPinned(e.sweep) || e.loadingSize > 0 || !e.faces) continue;
      used -= e.size * e.size * 4 * 6 * 4 / 3;
      e.faces.forEach((t) => t.dispose());
      e.faces = null;
      e.size = 0;
      e.depth?.dispose();
      e.depth = null;
      e.depthRequested = false;
    }
  }

  async load(entry, tier) {
    this.activeLoads++;
    entry.loadingSize = tier.size;
    const started = now();
    try {
      const bitmaps = await Promise.all([0, 1, 2, 3, 4, 5].map((f) => this.loadFace(tier, f)));
      if (this.disposed) return;
      const textures = bitmaps.map((b) => {
        const t = new THREE.Texture(b);
        t.flipY = false; // decoded flipped already
        t.colorSpace = THREE.NoColorSpace;
        t.minFilter = THREE.LinearMipmapLinearFilter;
        t.magFilter = THREE.LinearFilter;
        t.wrapS = t.wrapT = THREE.ClampToEdgeWrapping;
        t.anisotropy = this.space.settings.anisotropy;
        t.needsUpdate = true;
        return t;
      });
      await new Promise((done) => {
        const job = { pending: textures.length, done };
        for (const texture of textures) this.uploads.push({ texture, job });
      });
      if (this.disposed) return;
      if (!entry.faces || entry.size < tier.size) {
        entry.faces?.forEach((t) => t.dispose());
        entry.faces = textures;
        entry.size = tier.size;
      } else textures.forEach((t) => t.dispose()); // a larger tier won the race
      entry.loadSeconds = now() - started;
      entry.lastUsed = now();
    } catch (e) {
      console.warn(`[Matterportable] Could not load panorama ${tier.dir}: ${e.message ?? e}`);
    } finally {
      entry.loadingSize = 0;
      this.activeLoads--;
    }
  }

  /** One face as an ImageBitmap (flipped so V is bottom-up); tiled faces are assembled (tile rows from the top). */
  async loadFace(tier, face) {
    const url = (rel) => this.space.fileUrl(rel);
    const decode = async (u) => {
      const res = await fetch(u);
      if (!res.ok) throw new Error(`${res.status} for ${u}`);
      return res.blob();
    };
    const flip = { imageOrientation: 'flipY', premultiplyAlpha: 'none', colorSpaceConversion: 'none' };
    if (tier.tiles <= 1) return createImageBitmap(await decode(url(`${tier.dir}/face${face}.${tier.ext}`)), flip);
    const tiles = [];
    for (let y = 0; y < tier.tiles; y++)
      for (let x = 0; x < tier.tiles; x++)
        tiles.push(decode(url(`${tier.dir}/face${face}_${x}_${y}.${tier.ext}`)).then((b) => createImageBitmap(b)).then((bmp) => ({ x, y, bmp })));
    const parts = await Promise.all(tiles);
    const tw = parts[0].bmp.width;
    const canvas = new OffscreenCanvas(tw * tier.tiles, tw * tier.tiles);
    const ctx = canvas.getContext('2d');
    for (const p of parts) ctx.drawImage(p.bmp, p.x * tw, p.y * tw);
    return createImageBitmap(canvas, flip);
  }

  dispose() {
    this.disposed = true;
    for (const e of this.entries.values()) {
      e.faces?.forEach((t) => t.dispose());
      e.depth?.dispose();
    }
    this.entries.clear();
    this.queue.length = 0;
    this.depthMaterial.dispose();
  }
}
