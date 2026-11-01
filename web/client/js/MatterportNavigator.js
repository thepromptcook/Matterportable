// Mirrors MatterportNavigator (Unity, Godot): reads browser input, runs the Core Navigator (WebAssembly) once per
// frame and applies what comes back - camera, markers - and hands the rest (view binding, panorama work) to the
// space. HTML UI sits on top of the canvas, so canvas pointer events never belong to UI.
import * as THREE from 'three';
import { coreOf, fromCore, quatFromCore } from './Core.js';
import { SweepMarkers } from './SweepMarkers.js';

export const ViewMode = { Inside: 0, Dollhouse: 1, Floorplan: 2, Walk: 3 };
const K = { Inside: 1, Dollhouse: 2, Floorplan: 4, Forward: 8, Back: 16, Left: 32, Right: 64, TurnLeft: 128, TurnRight: 256, Next: 512, Previous: 1024, Home: 2048, FloorUp: 4096, FloorDown: 8192, Tour: 16384, DebugView: 32768, Walk: 65536, Run: 131072, Use: 262144 };
const PressedKeys = { Digit1: K.Inside, Digit2: K.Dollhouse, Digit3: K.Floorplan, Digit4: K.Walk, KeyW: K.Forward, ArrowUp: K.Forward, KeyS: K.Back, ArrowDown: K.Back, KeyA: K.Left, KeyD: K.Right, BracketRight: K.Next, BracketLeft: K.Previous, Home: K.Home, PageUp: K.FloorUp, PageDown: K.FloorDown, KeyP: K.Tour, F2: K.DebugView, KeyF: K.Use };
const HeldKeys = { ArrowLeft: K.TurnLeft, KeyQ: K.TurnLeft, ArrowRight: K.TurnRight, KeyE: K.TurnRight, KeyW: K.Forward, ArrowUp: K.Forward, KeyS: K.Back, ArrowDown: K.Back, KeyA: K.Left, KeyD: K.Right, ShiftLeft: K.Run, ShiftRight: K.Run };
export const NavKeys = K;
const ButtonBit = [1, 4, 2]; // DOM button 0 left, 1 middle, 2 right -> Core bits (1 left, 2 right, 4 middle)

export class MatterportNavigator {
  constructor(camera, canvas, space, scene) {
    Object.assign(this, { camera, canvas, space, core: space.core });
    this.markers = new SweepMarkers(scene);
    this.pointer = { x: -1, y: -1, over: false };
    this.down = this.up = this.buttons = this.scroll = this.pressed = 0;
    this.keys = new Set();
    this.onKey = null; // extra key handler (measure, HUD): return true to consume
    this.scriptedHeld = 0; // keys held by a script (automated checks), as if pressed
    this.scriptedPressed = 0; // keys pressed once by a script
    this.centrePointer = false; // aim through the middle of the view (automated checks)
    this.frame = null;
    this.raycaster = new THREE.Raycaster();
    camera.near = 0.03;
    camera.far = 1000;
    this.listen();
  }

  listen() {
    const c = this.canvas, p = this.pointer;
    this.handlers = {
      pointerdown: (e) => { this.down |= ButtonBit[e.button] ?? 0; this.buttons = e.buttons; c.setPointerCapture(e.pointerId); },
      pointermove: (e) => { Object.assign(p, { x: e.clientX, y: e.clientY, over: true }); this.buttons = e.buttons; },
      pointerup: (e) => { this.up |= ButtonBit[e.button] ?? 0; this.buttons = e.buttons; },
      pointerleave: () => { p.over = false; },
      wheel: (e) => { e.preventDefault(); this.scroll -= Math.sign(e.deltaY); },
      contextmenu: (e) => e.preventDefault(),
    };
    for (const [k, f] of Object.entries(this.handlers)) c.addEventListener(k, f, k === 'wheel' ? { passive: false } : undefined);
    this.keyDown = (e) => {
      if (e.target instanceof HTMLInputElement) return;
      this.keys.add(e.code);
      if (e.repeat) return;
      if (this.onKey?.(e)) e.preventDefault();
      else this.pressed |= PressedKeys[e.code] ?? 0;
    };
    this.keyUp = (e) => this.keys.delete(e.code);
    this.blur = () => this.keys.clear();
    window.addEventListener('keydown', this.keyDown);
    window.addEventListener('keyup', this.keyUp);
    window.addEventListener('blur', this.blur);
  }

  screenRay(x, y) {
    const r = this.canvas.getBoundingClientRect();
    this.raycaster.setFromCamera(new THREE.Vector2((x - r.left) / r.width * 2 - 1, -((y - r.top) / r.height) * 2 + 1), this.camera);
    return { origin: this.raycaster.ray.origin.clone(), dir: this.raycaster.ray.direction.clone() };
  }

  /** Runs the Core Navigator for one frame; returns the decoded frame (camera, binding, markers, commands). */
  update(dt) {
    this.markers.beginFrame();
    let held = this.scriptedHeld;
    for (const code of this.keys) held |= HeldKeys[code] ?? 0;
    const r = this.canvas.getBoundingClientRect(), height = this.canvas.clientHeight;
    const p = this.centrePointer ? { x: r.left + r.width / 2, y: r.top + r.height / 2, over: true } : this.pointer;
    const { origin, dir } = this.screenRay(p.x, p.y);
    const o = this.core.Frame([dt, p.x, p.y, p.over ? 1 : 0, this.down, this.buttons, this.up, ...coreOf(origin), ...coreOf(dir),
      THREE.MathUtils.degToRad(this.camera.fov) / Math.max(1, height), height, this.scroll, this.pressed | this.scriptedPressed, held]);
    this.down = this.up = this.scroll = this.pressed = this.scriptedPressed = 0;
    const f = this.frame = {
      mode: o[8], currentSweep: o[9], hoverSweep: o[10], selectedFloor: o[11], flags: o[12],
      panoA: o[13], panoB: o[14], depthA: o[15], depthB: o[16], poseA: o[17], poseB: o[18],
      blend: o[19], projection: o[20], useDepth: o[21], debugView: o[22], validA: o[23], validB: o[24], clipHeight: o[25],
      floorCount: o[26], viewClipHeight: o[27], activeLoads: o[28], queued: o[29], commands: [],
    };
    this.coreCamera = [o[0], o[1], o[2]]; // Core frame, for reports
    this.camera.position.copy(fromCore(o, 0));
    this.camera.quaternion.copy(quatFromCore(o, 3));
    this.camera.fov = o[7];
    this.camera.updateProjectionMatrix();
    let i = 31;
    for (let m = 0; m < o[30]; m++, i += 13) this.markers.draw(fromCore(o, i), fromCore(o, i + 3), o[i + 6], [o[i + 7], o[i + 8], o[i + 9], o[i + 10]], o[i + 11], o[i + 12]);
    for (let c = o[i++]; c > 0; c--, i += 3) f.commands.push([o[i], o[i + 1], o[i + 2]]);
    f.highlightProp = o[i++];
    f.propFlags = o[i++]; // 1 carrying, 2 drop refused
    f.props = [];
    for (let k = o[i++]; k > 0; k--, i += 11) f.props.push(o.slice(i, i + 11));
    return f;
  }

  get isMoving() { return !!(this.frame?.flags & 1); }
  get isChangingMode() { return !!(this.frame?.flags & 2); }
  get isWaitingForPanorama() { return !!(this.frame?.flags & 4); }
  get autoTour() { return !!(this.frame?.flags & 8); }
  get cullBack() { return !!(this.frame?.flags & 16); }
  get clicked() { return !!(this.frame?.flags & 32); }

  goTo(sweep) { this.core.Navigate(0, sweep, 0, 0); }
  setMode(mode) { this.core.Navigate(1, mode, 0, 0); }
  setFloor(floor) { this.core.Navigate(2, floor, 0, 0); }
  /** Jumps instantly, looking along yaw/pitch in degrees (Core convention: yaw clockwise, pitch down). */
  teleport(sweep, yaw, pitch) { this.core.Navigate(3, sweep, yaw, pitch); }
  /** Turns the view instantly (Core convention, degrees). */
  look(yaw, pitch = 0) { this.core.Navigate(8, 0, yaw, pitch); }
  toggleTour() { this.core.Navigate(4, 0, 0, 0); }
  setDebugView(view) { this.core.Navigate(5, view, 0, 0); }
  setMeasuring(on) { this.core.Navigate(6, on ? 1 : 0, 0, 0); }

  dispose() {
    for (const [k, f] of Object.entries(this.handlers)) this.canvas.removeEventListener(k, f);
    window.removeEventListener('keydown', this.keyDown);
    window.removeEventListener('keyup', this.keyUp);
    window.removeEventListener('blur', this.blur);
    this.markers.dispose();
  }
}
