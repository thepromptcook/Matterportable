// Mirrors PanoramaCache (Unity, Godot): loads what the Core PanoramaScheduler asks for. The browser decodes images
// off the main thread (createImageBitmap), uploads are spread over frames (two textures per frame), and depth cubes
// are rendered on the GPU (a cube camera at the sweep), which is how the browser gets them in milliseconds.
import * as THREE from 'three';
import { worldVertex, depthFragment } from './shaders.js';

const flipped = { imageOrientation: 'flipY', premultiplyAlpha: 'none', colorSpaceConversion: 'none' };

export class PanoramaCache {
  constructor(space) {
    this.space = space;
    this.faces = new Map(); // sweep -> six textures
    this.depth = new Map(); // sweep -> cube render target
    this.uploads = [];
    this.depthQueue = [];
    this.disposed = false;
    const r = space.renderer;
    this.depthSupported = r.extensions.has('EXT_color_buffer_float') || r.extensions.has('EXT_color_buffer_half_float');
    this.depthMaterial = new THREE.ShaderMaterial({
      vertexShader: worldVertex, fragmentShader: depthFragment, glslVersion: THREE.GLSL3,
      uniforms: { sweepPos: { value: new THREE.Vector3() }, spaceMatrix: { value: new THREE.Matrix4() } }, side: THREE.DoubleSide,
    });
  }

  get busy() { return this.uploads.length > 0 || this.depthQueue.length > 0; }

  /** The scheduler's commands for this frame: [0 load | 1 depth | 2 unload, sweep, tier index]. */
  run(commands) {
    for (const [type, sweep, tier] of commands) {
      if (type === 0) this.load(sweep, this.space.sweeps[sweep].tiers[tier]);
      else if (type === 1 && this.depthSupported) this.depthQueue.push(sweep);
      else if (type === 2) this.unload(sweep);
    }
  }

  update() {
    for (let n = 0; n < 2 && this.uploads.length; n++) {
      const u = this.uploads.shift();
      this.space.renderer.initTexture(u.texture);
      if (--u.job.pending === 0) u.job.done();
    }
    if (this.depthQueue.length) this.renderDepth(this.depthQueue.shift());
  }

  async load(sweep, tier) {
    try {
      const bitmaps = await Promise.all([0, 1, 2, 3, 4, 5].map((f) => this.loadFace(tier, f)));
      if (this.disposed) return;
      const textures = bitmaps.map((b) => Object.assign(new THREE.Texture(b), {
        flipY: false, colorSpace: THREE.NoColorSpace, minFilter: THREE.LinearMipmapLinearFilter, magFilter: THREE.LinearFilter,
        wrapS: THREE.ClampToEdgeWrapping, wrapT: THREE.ClampToEdgeWrapping, anisotropy: this.space.settings.anisotropy, needsUpdate: true,
      }));
      await new Promise((done) => {
        const job = { pending: textures.length, done };
        for (const texture of textures) this.uploads.push({ texture, job });
      });
      if (this.disposed || !this.space.core.PanoLoaded(sweep, tier.size)) { textures.forEach((t) => t.dispose()); return; }
      this.faces.get(sweep)?.forEach((t) => t.dispose());
      this.faces.set(sweep, textures);
    } catch (e) {
      console.warn(`[Matterportable] Could not load panorama ${tier.dir}: ${e.message ?? e}`);
      if (!this.disposed) this.space.core.PanoFailed(sweep);
    }
  }

  /** One face as an ImageBitmap (flipped so V is bottom-up); tiled faces are assembled (tile rows from the top). */
  async loadFace(tier, face) {
    const fetchBlob = async (rel) => {
      const res = await fetch(this.space.fileUrl(rel));
      if (!res.ok) throw new Error(`${res.status} for ${rel}`);
      return res.blob();
    };
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
    this.depthMaterial.uniforms.sweepPos.value.copy(pos);
    const r = this.space.renderer, clear = r.getClearColor(new THREE.Color()), alpha = r.getClearAlpha();
    r.setClearColor(new THREE.Color(1000, 0, 0), 1); // "nothing there" = far away
    this.space.depthScene.overrideMaterial = this.depthMaterial;
    camera.update(r, this.space.depthScene);
    r.setClearColor(clear, alpha);
    this.depth.get(sweep)?.dispose();
    this.depth.set(sweep, target);
    this.space.core.DepthReady(sweep);
  }

  unload(sweep) {
    this.faces.get(sweep)?.forEach((t) => t.dispose());
    this.faces.delete(sweep);
    this.depth.get(sweep)?.dispose();
    this.depth.delete(sweep);
  }

  dispose() {
    this.disposed = true;
    for (const s of [...this.faces.keys(), ...this.depth.keys()]) this.unload(s);
    this.depthMaterial.dispose();
  }
}
