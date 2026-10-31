// Matterportable in the browser: renderer, scene and frame loop; the Launcher opens spaces.
import * as THREE from 'three';
import { loadCore } from './Core.js';
import { Launcher } from './Launcher.js';

// ?aa=0 turns off multisampling (the headless checks render on the CPU, where it is expensive).
const renderer = new THREE.WebGLRenderer({ antialias: new URLSearchParams(location.search).get('aa') !== '0' });
renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.outputColorSpace = THREE.LinearSRGBColorSpace; // photos are shown exactly as stored (no colour conversion)
renderer.domElement.className = 'view';
document.body.appendChild(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0.11, 0.11, 0.12);
const camera = new THREE.PerspectiveCamera(75, window.innerWidth / window.innerHeight, 0.03, 1000);
window.addEventListener('resize', () => {
  renderer.setSize(window.innerWidth, window.innerHeight);
  camera.aspect = window.innerWidth / window.innerHeight;
  camera.updateProjectionMatrix();
});

loadCore(); // start the WebAssembly runtime while the menu is up
const launcher = new Launcher(renderer, scene, camera);
let last = performance.now();
renderer.setAnimationLoop((t) => {
  const dt = Math.min(0.1, (t - last) / 1000);
  last = t;
  launcher.update(dt);
  renderer.render(scene, camera);
});
