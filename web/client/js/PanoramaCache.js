// Mirrors PanoramaCache (Unity, Godot): loads what the Core PanoramaScheduler asks for. The browser decodes images
// off the main thread (createImageBitmap), uploads are spread over frames (two faces per frame) into six-layer
// texture arrays, and depth cubes are rendered on the GPU (a cube camera at the sweep), which is how the browser gets
// them in milliseconds. The clean photos (furniture painted out) load like the photos, with the map of which prop
// each pixel had painted out.
import * as THREE from 'three';
import { worldVertex, depthFragment } from './shaders.js';

const flipped = { imageOrientation: 'flipY', premultiplyAlpha: 'none', colorSpaceConversion: 'none' };

/** A six-layer texture array, storage allocated and nothing uploaded yet (faces are copied in by layer). */
function faceArray(size, painted) {
  const t = new THREE.DataArrayTexture(null, size, size, 6);
  Object.assign(t, painted
    ? { format: THREE.RedFormat, minFilter: THREE.NearestFilter, magFilter: THREE.NearestFilter, generateMipmaps: false } // prop numbers: never blended
    : { minFilter: THREE.LinearMipmapLinearFilter, magFilter: THREE.LinearFilter, generateMipmaps: true });
  Object.assign(t, { colorSpace: THREE.NoColorSpace, wrapS: THREE.ClampToEdgeWrapping, wrapT: THREE.ClampToEdgeWrapping, flipY: false, unpackAlignment: 1 });
  t.source.dataReady = false; // allocate (with every mip level) without uploading
  t.needsUpdate = true;
  return t;
}

export class PanoramaCache {
  constructor(space) {
    this.space = space;
    this.faces = new Map(); // sweep -> texture array
    this.depth = new Map(); // sweep -> cube render target
    this.clean = new Map(); // sweep -> { faces, painted } texture arrays
    this.uploads = [];
    this.depthQueue = [];
    this.cleanLoads = 0;
    this.disposed = false;
    const r = space.renderer;
    this.depthSupported = r.extensions.has('EXT_color_buffer_float') || r.extensions.has('EXT_color_buffer_half_float');
    // The depth cubes see the scan as photographed: every piece where it was scanned, each writing its piece id.
    this.depthUniforms = { sweepPos: { value: new THREE.Vector3() }, spaceMatrix: { value: new THREE.Matrix4() } };
    this.depthMaterials = (space.pieceGeometries ?? []).map(({ geometry, piece }) => {
      const material = new THREE.ShaderMaterial({
        vertexShader: worldVertex, fragmentShader: depthFragment, glslVersion: THREE.GLSL3,
        uniforms: { ...this.depthUniforms, pieceId: { value: piece } }, side: THREE.DoubleSide,
      });
      space.depthScene.add(new THREE.Mesh(geometry, material));
      return material;
    });
  }

  get busy() { return this.uploads.length > 0 || this.depthQueue.length > 0 || this.cleanLoads > 0; }

  /** The scheduler's commands for this frame: [0 load | 1 depth | 2 unload | 3 clean photos, sweep, tier index]. */
  run(commands) {
    for (const [type, sweep, tier] of commands) {
      if (type === 0) this.load(sweep, this.space.sweeps[sweep].tiers[tier]);
      else if (type === 1 && this.depthSupported) this.depthQueue.push(sweep);
      else if (type === 2) this.unload(sweep);
      else if (type === 3) this.loadClean(sweep);
    }
  }

  update() {
    const r = this.space.renderer;
    for (let n = 0; n < 2 && this.uploads.length; n++) {
      const u = this.uploads.shift(), size = u.bitmap.width;
      if (u.layer === 0) r.initTexture(u.target);
      const mips = u.target.generateMipmaps;
      u.target.generateMipmaps = mips && u.layer === 5; // the mip chain once, after the last face
      r.copyTextureToTexture3D(new THREE.Texture(u.bitmap), u.target, new THREE.Box3(new THREE.Vector3(), new THREE.Vector3(size, size, 1)), new THREE.Vector3(0, 0, u.layer));
      u.target.generateMipmaps = mips;
      u.bitmap.close?.();
      if (u.layer === 5) u.done();
    }
    if (this.depthQueue.length) this.renderDepth(this.depthQueue.shift());
  }

  /** Copies six decoded faces into a texture array, two per frame (resolves when all are in). */
  upload(bitmaps, painted = false) {
    const target = faceArray(bitmaps[0].width, painted);
    target.anisotropy = painted ? 1 : this.space.settings.anisotropy;
    return new Promise((done) => bitmaps.forEach((bitmap, layer) => this.uploads.push({ target, bitmap, layer, done: () => done(target) })));
  }

  async load(sweep, tier) {
    try {
      const bitmaps = await Promise.all([0, 1, 2, 3, 4, 5].map((f) => this.loadFace(tier, f)));
      if (this.disposed) return;
      const array = await this.upload(bitmaps);
      if (this.disposed || !this.space.core.PanoLoaded(sweep, tier.size)) { array.dispose(); return; }
      this.faces.get(sweep)?.dispose();
      this.faces.set(sweep, array);
    } catch (e) {
      console.warn(`[Matterportable] Could not load panorama ${tier.dir}: ${e.message ?? e}`);
      if (!this.disposed) this.space.core.PanoFailed(sweep);
    }
  }

  /** The sweep's clean photos (space.json's six paths) and painted-prop maps (null faces: nothing painted, all 0). */
  async loadClean(sweep) {
    const s = this.space.sweeps[sweep];
    this.cleanLoads++;
    try {
      const image = async (rel) => createImageBitmap(await this.fetchBlob(rel), flipped);
      const [faces, painted] = await Promise.all([Promise.all(s.clean.map(image)), Promise.all((s.painted ?? []).map((p) => p && image(p)))]);
      if (this.disposed) return;
      const size = painted.find((b) => b)?.width ?? 1, faceSize = faces[0].width; // (uploaded bitmaps are closed)
      const layers = await Promise.all([0, 1, 2, 3, 4, 5].map((f) => painted[f] ?? createImageBitmap(new ImageData(size, size))));
      const arrays = await Promise.all([this.upload(faces), this.upload(layers, true)]);
      if (this.disposed || !this.space.core.CleanLoaded(sweep, faceSize)) { arrays.forEach((a) => a.dispose()); return; }
      this.disposeClean(sweep);
      this.clean.set(sweep, { faces: arrays[0], painted: arrays[1] });
    } catch (e) {
      console.warn(`[Matterportable] Could not load the clean photos of sweep ${sweep}: ${e.message ?? e}`);
    } finally {
      this.cleanLoads--;
    }
  }

  async fetchBlob(rel) {
    const res = await fetch(this.space.fileUrl(rel));
    if (!res.ok) throw new Error(`${res.status} for ${rel}`);
    return res.blob();
  }

  /** One face as an ImageBitmap (flipped so V is bottom-up); tiled faces are assembled (tile rows from the top). */
  async loadFace(tier, face) {
    const fetchBlob = (rel) => this.fetchBlob(rel);
    if (tier.tiles <= 1) return createImageBitmap(await fetchBlob(`${tier.dir}/face${face}.${tier.ext}`), flipped);
    const tiles = [];
    for (let y = 0; y < tier.tiles; y++)
      for (let x = 0; x < tier.tiles; x++)
        tiles.push(fetchBlob(`${tier.dir}/face${face}_${x}_${y}.${tier.ext}`).then(createImageBitmap).then((bmp) => ({ x, y, bmp })));
    const parts = await Promise.all(tiles);
    const tw = parts[0].bmp.width;
    const canvas = new OffscreenCanvas(tw * tier.tiles, tw * tier.tiles);
    const ctx = canvas.getContext('2d');
    for (const p of parts) ctx.drawImage(p.bmp, p.x * tw, p.y * tw);
    return createImageBitmap(canvas, flipped);
  }

  renderDepth(sweep) {
    const size = this.space.settings.depthMapSize;
    // Point-sampled: the shader filters the visibility test, not the depths (mpDepthVisibility).
    const target = new THREE.WebGLCubeRenderTarget(size, {
      type: THREE.HalfFloatType, generateMipmaps: false, minFilter: THREE.NearestFilter, magFilter: THREE.NearestFilter,
    });
    const camera = new THREE.CubeCamera(0.03, 1000, target);
    const pos = this.space.sweeps[sweep].position;
    camera.position.copy(pos);
    this.depthUniforms.sweepPos.value.copy(pos);
    const r = this.space.renderer, clear = r.getClearColor(new THREE.Color()), alpha = r.getClearAlpha();
    r.setClearColor(new THREE.Color(1000, 0, 0), 1); // "nothing there" = far away
    camera.update(r, this.space.depthScene);
    r.setClearColor(clear, alpha);
    this.depth.get(sweep)?.dispose();
    this.depth.set(sweep, target);
    this.space.core.DepthReady(sweep);
  }

  disposeClean(sweep) {
    const c = this.clean.get(sweep);
    c?.faces.dispose();
    c?.painted.dispose();
    this.clean.delete(sweep);
  }

  unload(sweep) {
    this.faces.get(sweep)?.dispose();
    this.faces.delete(sweep);
    this.depth.get(sweep)?.dispose();
    this.depth.delete(sweep);
    this.disposeClean(sweep);
  }

  dispose() {
    this.disposed = true;
    for (const s of [...this.faces.keys(), ...this.depth.keys(), ...this.clean.keys()]) this.unload(s);
    this.depthMaterials.forEach((m) => m.dispose());
  }
}
