// Mirrors MatterportSpace (Unity, Godot): loads a space the server prepared (space.json + geometry, BVH and snapping
// features made natively), hands it to the WebAssembly Core (navigator, scheduler, measure tool), builds the three.js
// mesh and sky, and binds the navigator's view to the shaders every frame.
import * as THREE from 'three';
import { loadCore, fromCore } from './Core.js';
import { PanoramaCache } from './PanoramaCache.js';
import { worldVertex, meshFragment, skyVertex, skyFragment } from './shaders.js';

export class MatterportSpace {
  /** options.anisotropy overrides the shared default (the headless checks turn filtering off). */
  constructor(renderer, scene, options = {}) {
    Object.assign(this, { renderer, scene, options });
    this.sweeps = [];
    this.isLoaded = false;
    this.status = '';
    this.error = null;
    this.texturesLoaded = 0;
    this.textures = [];
    this.root = new THREE.Group();
    scene.add(this.root);
    this.depthScene = new THREE.Scene();
    this.gray = Object.assign(new THREE.DataTexture(new Uint8Array([128, 128, 128, 255]), 1, 1), { needsUpdate: true });
    this.uniforms = {
      posA: { value: new THREE.Vector3() }, posB: { value: new THREE.Vector3() },
      rotA: { value: new THREE.Matrix3() }, rotB: { value: new THREE.Matrix3() },
      faceRight: { value: Array.from({ length: 6 }, () => new THREE.Vector4()) },
      faceUp: { value: Array.from({ length: 6 }, () => new THREE.Vector4()) },
      mpState: { value: new THREE.Vector4() }, mpValid: { value: new THREE.Vector4(0, 0, 1e9, 0) },
      mpBackground: { value: new THREE.Vector3() },
      depthA: { value: null }, depthB: { value: null }, cullBack: { value: false }, spaceMatrix: { value: new THREE.Matrix4() },
    };
    this.props = []; // per prop: { mesh, tint } (null: nothing to draw)
    for (const p of ['A', 'B']) for (let i = 0; i < 6; i++) this.uniforms[`pano${p}${i}`] = { value: this.gray };
  }

  get hasMesh() { return !!this.mesh; }
  fileUrl(relative) { return `api/spaces/${encodeURIComponent(this.id)}/files/${relative}`; }

  /** Loads space `id` (already prepared by the server). */
  async load(id) {
    this.id = id;
    const started = performance.now();
    const get = async (file) => {
      const res = await fetch(`api/spaces/${encodeURIComponent(id)}/web/${file}`);
      if (!res.ok) throw new Error(`${file}: HTTP ${res.status}`);
      return file.endsWith('.json') ? res.text() : new Uint8Array(await res.arrayBuffer());
    };
    this.status = 'Loading geometry';
    const json = await get('space.json');
    const info = this.info = JSON.parse(json);
    const hasMesh = info.parts?.length > 0, none = new Uint8Array(0);
    const [geometry, bvh, features, core] = await Promise.all([
      hasMesh ? get('geometry.bin') : none, hasMesh ? get('bvh.bin') : none, hasMesh ? get('features.bin') : none, loadCore(),
    ]);
    this.core = core;
    core.Load(geometry, bvh, features, json);
    const [depthMapSize, r, g, b, anisotropy] = core.RenderSettings(); // Core ViewerSettings, shared by all three
    this.settings = { depthMapSize, anisotropy: this.options.anisotropy ?? anisotropy };
    this.uniforms.mpBackground.value.set(r, g, b);
    if (hasMesh) this.buildMesh(info, geometry);
    this.sweeps = info.sweeps.map((s) => ({ tiers: s.tiers, position: fromCore(s.position), rotation: new THREE.Matrix3().fromArray(s.rotation) }));
    for (let c = 0; c < 6; c++) {
      this.uniforms.faceRight.value[c].fromArray(info.faceRight[c]);
      this.uniforms.faceUp.value[c].set(...info.faceUp[c], 0);
    }
    this.createSky();
    this.panoramas = new PanoramaCache(this);
    this.isLoaded = true;
    this.lastLoadSeconds = (performance.now() - started) / 1000;
    this.status = `Loaded "${info.name}" (${this.sweeps.length} sweeps) in ${this.lastLoadSeconds.toFixed(1)}s`;
    if (hasMesh) this.loadTextures(info);
  }

  buildMesh(info, bytes) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const vertices = view.getInt32(4, true), indexCount = view.getInt32(8, true), hasUvs = view.getInt32(12, true) !== 0;
    const slice = (start, length) => bytes.buffer.slice(bytes.byteOffset + start, bytes.byteOffset + start + length);
    const pos = new Float32Array(slice(16, vertices * 12));
    for (let i = 2; i < pos.length; i += 3) pos[i] = -pos[i]; // Core -> three (mirror Z)
    const uvStart = 16 + vertices * 12, indexStart = uvStart + (hasUvs ? vertices * 8 : 0);
    const positions = new THREE.BufferAttribute(pos, 3);
    const uvs = new THREE.BufferAttribute(hasUvs ? new Float32Array(slice(uvStart, vertices * 8)) : new Float32Array(vertices * 2), 2);
    const index = new Uint32Array(slice(indexStart, indexCount * 4));
    const material = (meshTex, propTint) => new THREE.ShaderMaterial({
      vertexShader: worldVertex, fragmentShader: meshFragment, glslVersion: THREE.GLSL3, side: THREE.DoubleSide,
      uniforms: { ...this.uniforms, meshTex, propTint },
    });
    const noTint = () => ({ value: new THREE.Vector4(0, 0, 0, 0) }); // (a THREE.Vector4's w defaults to 1)
    this.materials = info.parts.map(() => material({ value: this.gray }, noTint()));
    // The room and each prop (Core SpaceModel.MeshPieces' split): geometries sharing the vertex buffers, a group per part.
    const owners = this.core.PropOwners(), pieces = [];
    info.parts.forEach((p, part) => {
      for (let t = p.start / 3; t < (p.start + p.count) / 3; t++) ((pieces[owners[t]] ??= info.parts.map(() => []))[part]).push(t);
    });
    const build = (parts, materials) => {
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', positions);
      g.setAttribute('uv', uvs);
      const idx = new Uint32Array(parts.reduce((n, tris) => n + tris.length * 3, 0)), used = [];
      let at = 0;
      parts.forEach((tris, part) => {
        if (tris.length === 0) return;
        g.addGroup(at, tris.length * 3, used.length);
        used.push(materials(part));
        for (const t of tris) { idx[at++] = index[3 * t]; idx[at++] = index[3 * t + 1]; idx[at++] = index[3 * t + 2]; }
      });
      g.setIndex(new THREE.BufferAttribute(idx, 1));
      g.computeBoundingSphere();
      return new THREE.Mesh(g, used);
    };
    this.mesh = build(pieces[0] ?? info.parts.map(() => []), (part) => this.materials[part]);
    this.root.add(this.mesh);
    for (let k = 1; k < pieces.length; k++) {
      if (!pieces[k]) { this.props.push(null); continue; }
      const tint = noTint();
      const mesh = build(pieces[k], (part) => material(this.materials[part].uniforms.meshTex, tint));
      this.root.add(mesh);
      this.props.push({ mesh, tint });
    }
    // The depth cubes see the scan as it was photographed: every triangle where it was scanned.
    const scanned = new THREE.BufferGeometry();
    scanned.setAttribute('position', positions);
    scanned.setIndex(new THREE.BufferAttribute(index, 1));
    this.depthScene.add(new THREE.Mesh(scanned));
  }

  /** Drawn last at the far plane with the depth test, so only pixels the mesh leaves uncovered pay for it. */
  createSky() {
    const material = new THREE.ShaderMaterial({
      vertexShader: skyVertex, fragmentShader: skyFragment, glslVersion: THREE.GLSL3,
      uniforms: this.uniforms, side: THREE.BackSide, depthWrite: false, depthTest: true, depthFunc: THREE.LessEqualDepth,
    });
    this.sky = Object.assign(new THREE.Mesh(new THREE.BoxGeometry(100, 100, 100), material), { renderOrder: 1000, frustumCulled: false });
    this.scene.add(this.sky);
  }

  /** Mesh textures decode in parallel off the main thread. */
  async loadTextures(info) {
    const flip = { imageOrientation: 'flipY', premultiplyAlpha: 'none', colorSpaceConversion: 'none' };
    await Promise.all(info.parts.map(async (p, i) => {
      if (!p.texture) return;
      try {
        const bmp = await createImageBitmap(await (await fetch(this.fileUrl(p.texture))).blob(), flip);
        if (!this.isLoaded) return;
        const t = Object.assign(new THREE.Texture(bmp), {
          flipY: false, colorSpace: THREE.NoColorSpace, wrapS: THREE.RepeatWrapping, wrapT: THREE.RepeatWrapping,
          anisotropy: this.settings.anisotropy * 2, minFilter: THREE.LinearMipmapLinearFilter, needsUpdate: true,
        });
        this.textures.push(t);
        this.materials[i].uniforms.meshTex.value = t;
        this.texturesLoaded++;
      } catch (e) { console.warn('[Matterportable] Mesh texture failed:', e.message ?? e); }
    }));
  }

  /** Once per frame, after the navigator: panorama work, then the navigator's view binding to the shaders. */
  update(f, camera) {
    this.panoramas.run(f.commands);
    this.panoramas.update();
    this.sky.position.copy(camera.position);
    const u = this.uniforms, faces = this.panoramas.faces, depth = this.panoramas.depth;
    for (let i = 0; i < 6; i++) {
      u[`panoA${i}`].value = faces.get(f.panoA)?.[i] ?? this.gray;
      u[`panoB${i}`].value = faces.get(f.panoB)?.[i] ?? this.gray;
    }
    u.depthA.value = depth.get(f.depthA)?.texture ?? null;
    u.depthB.value = depth.get(f.depthB)?.texture ?? null;
    u.posA.value.copy(this.sweeps[f.poseA].position);
    u.posB.value.copy(this.sweeps[f.poseB].position);
    u.rotA.value.copy(this.sweeps[f.poseA].rotation);
    u.rotB.value.copy(this.sweeps[f.poseB].rotation);
    u.mpState.value.set(f.blend, f.projection, f.useDepth, f.debugView);
    u.mpValid.value.set(f.validA, f.validB, f.clipHeight, 0);
    u.cullBack.value = !!(f.flags & 16);
    u.spaceMatrix.value.copy(this.root.matrixWorld);
    // Walk mode's props where the Core has them (Core frame -> three: mirror Z), tinted as it says.
    this.props.forEach((pr, k) => {
      const p = f.props[k];
      if (!pr || !p) return;
      pr.mesh.position.set(p[0], p[1], -p[2]);
      pr.mesh.quaternion.set(-p[3], -p[4], p[5], p[6]);
      pr.tint.value.set(p[7], p[8], p[9], p[10]);
    });
  }

  dispose() {
    this.isLoaded = false;
    this.panoramas?.dispose();
    this.scene.remove(this.root);
    if (this.sky) { this.scene.remove(this.sky); this.sky.geometry.dispose(); this.sky.material.dispose(); }
    this.mesh?.geometry.dispose();
    this.materials?.forEach((m) => m.dispose());
    this.props.forEach((pr) => { pr?.mesh.geometry.dispose(); pr?.mesh.material.forEach((m) => m.dispose()); });
    this.textures.forEach((t) => t.dispose());
  }
}
