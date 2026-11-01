// Mirrors SweepMarkers (Unity, Godot): marker circles and the move cursor, a pool of quads redrawn each frame.
import * as THREE from 'three';
import { markerFragment } from './shaders.js';

const vertex = `varying vec2 vUv; void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`;
const up = new THREE.Vector3(0, 1, 0);

export class SweepMarkers {
  constructor(scene) {
    this.scene = scene;
    this.pool = [];
    this.used = 0;
    this.quad = new THREE.PlaneGeometry(1, 1).rotateX(-Math.PI / 2); // lying flat, facing +Y
  }

  beginFrame() {
    for (const m of this.pool) m.visible = false;
    this.used = 0;
  }

  /** A circle lying on a surface with the given normal. color = [r, g, b, a]. */
  draw(position, normal, radius, color, inner = 0.72, fill = 0.22) {
    if (this.hidden || radius <= 0 || color[3] <= 0.001) return;
    if (this.used === this.pool.length) {
      const material = new THREE.ShaderMaterial({
        vertexShader: vertex, fragmentShader: markerFragment, glslVersion: THREE.GLSL3,
        transparent: true, depthWrite: false, side: THREE.DoubleSide,
        uniforms: { color: { value: new THREE.Vector4() }, inner: { value: inner }, fill: { value: fill } },
      });
      const m = new THREE.Mesh(this.quad, material);
      m.renderOrder = 10;
      this.scene.add(m);
      this.pool.push(m);
    }
    const m = this.pool[this.used++];
    const n = normal.lengthSq() > 1e-6 ? normal.clone().normalize() : up;
    m.quaternion.setFromUnitVectors(up, n);
    m.position.copy(position).addScaledVector(n, 0.01);
    m.scale.set(radius * 2, 1, radius * 2);
    m.material.uniforms.color.value.set(...color);
    m.material.uniforms.inner.value = inner;
    m.material.uniforms.fill.value = fill;
    m.visible = true;
  }

  dispose() {
    for (const m of this.pool) { this.scene.remove(m); m.material.dispose(); }
    this.quad.dispose();
  }
}
