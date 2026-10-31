// Mirrors MatterportSpace (Unity, Godot): loads a prepared space from the server (space.json + geometry, BVH and
// snapping features made natively by web/server), hands the geometry to the WebAssembly Core for ray casts and
// measuring, builds the three.js mesh and sky, and writes the view state to the shaders every frame.
import * as THREE from 'three';
import { loadCore, coreOf, fromCore } from './Core.js';
import { PanoramaCache } from './PanoramaCache.js';
import { worldVertex, meshFragment, skyVertex, skyFragment } from './shaders.js';

export const DebugViewNames = ['Final', 'Mesh texture', 'Panorama only', 'Visibility A/B', 'Blend weights'];
const NoClip = 1e9;

export class MatterportSpace {
  constructor(renderer, scene, settings) {
    this.renderer = renderer;
    this.scene = scene;
    this.settings = settings;
    this.sweeps = [];
    this.floorHeights = [];
    this.isLoaded = false;
    this.status = '';
    this.error = null;
    this.texturesLoaded = 0;
    this.debugView = 0;
    this.viewA = -1; this.viewB = -1; this.blend = 0; this.projection = 1; this.clipHeight = Infinity;
    this.root = new THREE.Group();
    scene.add(this.root);
    this.depthScene = new THREE.Scene();
    this.textures = [];
    this.uniforms = this.makeUniforms();
  }

  get hasMesh() { return !!this.mesh; }
  get floorCount() { return this.floorHeights.length; }
  fileUrl(relative) { return `api/spaces/${encodeURIComponent(this.id)}/files/${relative}`; }

  makeUniforms() {
    const gray = new THREE.DataTexture(new Uint8Array([128, 128, 128, 255]), 1, 1);
    gray.needsUpdate = true;
    this.gray = gray;
    const u = {
      posA: { value: new THREE.Vector3() }, posB: { value: new THREE.Vector3() },
      rotA: { value: new THREE.Matrix3() }, rotB: { value: new THREE.Matrix3() },
      faceRight: { value: Array.from({ length: 6 }, () => new THREE.Vector4()) },
      faceUp: { value: Array.from({ length: 6 }, () => new THREE.Vector4()) },
      mpState: { value: new THREE.Vector4() }, mpValid: { value: new THREE.Vector4(0, 0, NoClip, 0) },
      mpBackground: { value: new THREE.Vector3(...this.settings.backgroundColor) },
      depthA: { value: null }, depthB: { value: null }, cullBack: { value: false },
    };
    for (const p of ['A', 'B']) for (let i = 0; i < 6; i++) u[`pano${p}${i}`] = { value: gray };
    return u;
  }

  /** Loads space `id` (already prepared by the server). onStatus(text) reports progress. */
  async load(id, onStatus) {
    this.id = id;
    const started = performance.now();
    const get = async (file, type) => {
      const res = await fetch(`api/spaces/${encodeURIComponent(id)}/web/${file}`);
      if (!res.ok) throw new Error(`${file}: HTTP ${res.status}`);
      return type === 'json' ? res.json() : new Uint8Array(await res.arrayBuffer());
    };
    onStatus?.('Loading geometry');
    const info = await get('space.json', 'json');
    this.info = info;
    const hasMesh = info.parts && info.parts.length > 0;
    const [geometry, bvh, features, core] = await Promise.all([
      hasMesh ? get('geometry.bin') : null, hasMesh ? get('bvh.bin') : null, hasMesh ? get('features.bin') : null, loadCore(),
    ]);
    this.core = core;
    if (hasMesh) {
      onStatus?.('Preparing ray casts');
      core.Load(geometry, bvh, features);
      this.buildMesh(info, geometry);
    }
    this.sweeps = info.sweeps.map((s) => ({
      ...s,
      position: fromCore(s.position),
      floorPoint: fromCore(s.floorPoint),
      rotation: new THREE.Matrix3().fromArray(s.rotation), // column-major: right, up, forward (Core frame)
    }));
    this.computeFloorHeights();
    for (let c = 0; c < 6; c++) {
      this.uniforms.faceRight.value[c].fromArray(info.faceRight[c]);
      this.uniforms.faceUp.value[c].set(...info.faceUp[c], 0);
    }
    this.createSky();
    this.panoramas = new PanoramaCache(this);
    this.panoramas.isPinned = (s) => s === this.viewA || s === this.viewB;
    this.isLoaded = true;
    this.lastLoadSeconds = (performance.now() - started) / 1000;
    this.status = `Loaded "${info.name}" (${this.sweeps.length} sweeps) in ${this.lastLoadSeconds.toFixed(1)}s`;
    if (hasMesh) this.loadTextures(info);
  }

  buildMesh(info, bytes) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const vertices = view.getInt32(4, true), indexCount = view.getInt32(8, true), hasUvs = view.getInt32(12, true) !== 0;
    const pos = new Float32Array(bytes.buffer.slice(bytes.byteOffset + 16, bytes.byteOffset + 16 + vertices * 12));
    for (let i = 2; i < pos.length; i += 3) pos[i] = -pos[i]; // Core -> three (mirror Z)
    let o = 16 + vertices * 12;
    const uv = hasUvs ? new Float32Array(bytes.buffer.slice(bytes.byteOffset + o, bytes.byteOffset + o + vertices * 8)) : new Float32Array(vertices * 2);
    if (hasUvs) o += vertices * 8;
    const index = new Uint32Array(bytes.buffer.slice(bytes.byteOffset + o, bytes.byteOffset + o + indexCount * 4));
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    g.setIndex(new THREE.BufferAttribute(index, 1));
    this.materials = info.parts.map((p, i) => {
      g.addGroup(p.start, p.count, i);
      return new THREE.ShaderMaterial({
        vertexShader: worldVertex, fragmentShader: meshFragment, glslVersion: THREE.GLSL3, side: THREE.DoubleSide,
        uniforms: { ...this.uniforms, meshTex: { value: this.gray } },
      });
    });
    g.computeBoundingSphere();
    this.mesh = new THREE.Mesh(g, this.materials);
    this.root.add(this.mesh);
    this.depthScene.add(new THREE.Mesh(g)); // same geometry, drawn with the depth material by the panorama cache
    this.bounds = new THREE.Box3(fromCore(info.bounds[0]), fromCore(info.bounds[1]));
    this.bounds = new THREE.Box3().setFromPoints([this.bounds.min, this.bounds.max]);
  }

  createSky() {
    // Drawn last at the far plane with the depth test, so only pixels the mesh leaves uncovered pay for it.
    const material = new THREE.ShaderMaterial({
      vertexShader: skyVertex, fragmentShader: skyFragment, glslVersion: THREE.GLSL3,
      uniforms: this.uniforms, side: THREE.BackSide, depthWrite: false, depthTest: true, depthFunc: THREE.LessEqualDepth,
    });
    this.sky = new THREE.Mesh(new THREE.BoxGeometry(100, 100, 100), material);
    this.sky.renderOrder = 1000;
    this.sky.frustumCulled = false;
    this.scene.add(this.sky);
  }

  /** Mesh textures decode in parallel off the main thread, then upload one per frame. */
  async loadTextures(info) {
    const flip = { imageOrientation: 'flipY', premultiplyAlpha: 'none', colorSpaceConversion: 'none' };
    await Promise.all(info.parts.map(async (p, i) => {
      if (!p.texture) return;
      try {
        const blob = await (await fetch(this.fileUrl(p.texture))).blob();
        const bmp = await createImageBitmap(blob, flip);
        if (!this.isLoaded) return;
        const t = new THREE.Texture(bmp);
        Object.assign(t, { flipY: false, colorSpace: THREE.NoColorSpace, wrapS: THREE.RepeatWrapping, wrapT: THREE.RepeatWrapping, anisotropy: this.settings.anisotropy * 2, minFilter: THREE.LinearMipmapLinearFilter, needsUpdate: true });
        this.textures.push(t);
        this.materials[i].uniforms.meshTex.value = t;
        this.texturesLoaded++;
      } catch (e) { console.warn('[Matterportable] Mesh texture failed:', e.message ?? e); }
    }));
  }

  computeFloorHeights() {
    let floors = 0;
    for (const s of this.sweeps) floors = Math.max(floors, s.floor + 1);
    this.floorHeights = [];
    for (let f = 0; f < floors; f++) {
      const ys = this.sweeps.filter((s) => s.floor === f).map((s) => s.floorPoint.y).sort((a, b) => a - b);
      this.floorHeights.push(ys.length ? ys[ys.length >> 1] : (f > 0 ? this.floorHeights[f - 1] + 3 : 0));
    }
  }

  // ------------------------------------------------------------------ view state

  setView(a, b, blend, projection, clipHeight = Infinity) {
    this.viewA = a; this.viewB = b;
    this.blend = Math.min(1, Math.max(0, blend));
    this.projection = Math.min(1, Math.max(0, projection));
    this.clipHeight = clipHeight;
    this.panoramas?.touch(a);
    this.panoramas?.touch(b);
  }

  setCullBack(cull) { this.uniforms.cullBack.value = cull; }

  /** Once per frame, after the navigator (same logic as SpaceRenderer.Apply): one ready panorama fills both slots. */
  update(camera) {
    if (!this.isLoaded) return;
    this.panoramas.update();
    this.sky.position.copy(camera.position);
    const u = this.uniforms, cache = this.panoramas;
    const a = this.viewA >= 0 ? cache.get(this.viewA) : null;
    const b = this.viewB >= 0 ? cache.get(this.viewB) : null;
    const validA = !!a?.faces, validB = !!b?.faces;
    const depth = this.settings.depthMapSize > 0 && !!a?.depth && (!b || !!b.depth || this.blend <= 0);
    const facesA = validA ? a.faces : validB ? b.faces : null;
    const facesB = validB ? b.faces : facesA;
    for (let i = 0; i < 6; i++) {
      u[`panoA${i}`].value = facesA ? facesA[i] : this.gray;
      u[`panoB${i}`].value = facesB ? facesB[i] : this.gray;
    }
    u.depthA.value = a?.depth?.texture ?? null;
    u.depthB.value = (b?.depth ?? a?.depth)?.texture ?? null;
    const n = this.sweeps.length;
    const ia = Math.min(n - 1, Math.max(0, validA ? this.viewA : validB ? this.viewB : this.viewA));
    const ib = Math.min(n - 1, Math.max(0, validB ? this.viewB : ia));
    u.posA.value.copy(this.sweeps[ia].position);
    u.posB.value.copy(this.sweeps[ib].position);
    u.rotA.value.copy(this.sweeps[ia].rotation);
    u.rotB.value.copy(this.sweeps[ib].rotation);
    u.mpState.value.set(validA && validB ? this.blend : validB ? 1 : 0, this.projection, depth ? 1 : 0, this.debugView);
    u.mpValid.value.set(validA || validB ? 1 : 0, validA && validB ? 1 : 0, Math.min(this.clipHeight, NoClip), 0);
  }

  // ------------------------------------------------------------------ queries (three.js world space)

  sweepWorldPosition(i) { return this.sweeps[i].position; }
  markerWorldPosition(i) { return this.sweeps[i].floorPoint; }
  floorHeightWorld(f) { return f >= 0 && f < this.floorHeights.length ? this.floorHeights[f] : 0; }

  /** Ray cast against the scanned mesh (WebAssembly Core BVH): { point, normal, distance } or null. */
  raycast(origin, dir, maxDistance) {
    if (!this.hasMesh) return null;
    const r = this.core.Raycast([...coreOf(origin), ...coreOf(dir), maxDistance]);
    return r[0] ? { point: fromCore(r, 1), normal: fromCore(r, 4), distance: r[7] } : null;
  }

  /** Ray cast that ignores everything above a clip height (dollhouse floor filtering). */
  raycastBelow(origin, dir, maxDistance, clipHeight) {
    let o = origin.clone(), travelled = 0;
    for (let i = 0; i < 8; i++) {
      const hit = this.raycast(o, dir, maxDistance - travelled);
      if (!hit) return null;
      if (hit.point.y <= clipHeight) return hit;
      travelled += hit.distance + 0.01;
      o = hit.point.clone().addScaledVector(dir, 0.01);
    }
    return null;
  }

  lineOfSight(a, b) { return !this.hasMesh || !this.core.Occluded([...coreOf(a), ...coreOf(b), 0.1]); }

  get worldBounds() {
    if (this.bounds) return this.bounds;
    return new THREE.Box3().setFromPoints(this.sweeps.map((s) => s.position));
  }

  dispose() {
    this.isLoaded = false;
    this.panoramas?.dispose();
    this.scene.remove(this.root);
    if (this.sky) { this.scene.remove(this.sky); this.sky.geometry.dispose(); this.sky.material.dispose(); }
    this.mesh?.geometry.dispose();
    this.materials?.forEach((m) => m.dispose());
    this.textures.forEach((t) => t.dispose());
  }
}
