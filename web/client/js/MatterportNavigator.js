// Mirrors MatterportNavigator (Unity, Godot; keep behaviour changes in sync): drag to look, click to move with the
// cross-fade, W/A/S/D to step between sweeps, wheel to zoom, Dollhouse and Floor plan. Same conventions as Godot:
// the camera looks down -Z, yaw is counter-clockwise from above, positive pitch looks up. HTML UI sits on top of the
// canvas, so pointer events on the canvas never belong to UI.
import * as THREE from 'three';
import { SweepMarkers } from './SweepMarkers.js';

export const ViewMode = { Inside: 0, Dollhouse: 1, Floorplan: 2 };
const now = () => performance.now() / 1000;
const ease = (t) => t * t * (3 - 2 * t);
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
const inverseLerp = (a, b, v) => clamp((v - a) / (b - a), 0, 1);
const rad = THREE.MathUtils.degToRad, deg = THREE.MathUtils.radToDeg;
const UP = new THREE.Vector3(0, 1, 0);

export class MatterportNavigator {
  constructor(camera, canvas, space, scene) {
    this.camera = camera;
    this.canvas = canvas;
    this.space = space;
    this.mode = ViewMode.Inside;
    this.startMode = ViewMode.Inside;
    this.currentSweep = -1;
    this.selectedFloor = -1;
    this.hoverSweep = -1;
    this.autoTour = false;
    /** Takes over left clicks (measure mode): return true to consume. Also hides markers and the move cursor. */
    this.clickHandler = null;
    this.onKey = null; // extra key handler (HUD, measure): return true to consume
    this.yaw = 0; this.pitch = 0; this.targetYaw = 0; this.targetPitch = 0; this.fov = 75; this.targetFov = 75;
    this.projection = 1;
    this.move = { active: false };
    this.anim = { active: false };
    this.queued = -1; this.pending = -1; this.pendingSince = 0; this.pendingIsFlyIn = false;
    this.orbitTarget = new THREE.Vector3(); this.orbitTargetGoal = new THREE.Vector3();
    this.orbitYaw = 0; this.orbitPitch = 40; this.orbitDistance = 12; this.orbitDistanceGoal = 12;
    this.pointer = { down: false, button: 0, pressX: 0, pressY: 0, dragging: false, x: -1, y: -1, inside: false };
    this.dragDelta = new THREE.Vector2();
    this.scroll = 0;
    this.keys = new Set();
    this.visible = [];
    this.visibleFromCurrent = [];
    this.reticle = null;
    this.initialised = false;
    this.markers = new SweepMarkers(scene);
    this.raycaster = new THREE.Raycaster();
    camera.near = 0.03;
    camera.far = 1000;
    camera.rotation.order = 'YXZ';
    this.listen();
  }

  get S() { return this.space.settings; }
  get ready() { return this.space.isLoaded && this.space.sweeps.length > 0; }
  get isMoving() { return this.move.active; }
  get isChangingMode() { return this.anim.active; }
  get isWaitingForPanorama() { return this.pending >= 0; }
  get moveT() { return clamp((now() - this.move.start) / Math.max(0.01, this.move.duration), 0, 1); }
  /** World height above which geometry is hidden in the current view (floor filter), else Infinity. */
  get viewClipHeight() { return this.mode !== ViewMode.Inside && !(this.anim.active && this.anim.target === ViewMode.Inside) ? this.clipHeight() : Infinity; }

  // ------------------------------------------------------------------ input

  listen() {
    const c = this.canvas;
    this.handlers = {
      pointerdown: (e) => {
        if (this.pointer.down || e.button > 2) return;
        Object.assign(this.pointer, { down: true, button: e.button, pressX: e.clientX, pressY: e.clientY, dragging: false });
        c.setPointerCapture(e.pointerId);
      },
      pointermove: (e) => {
        Object.assign(this.pointer, { x: e.clientX, y: e.clientY, inside: true });
        if (!this.pointer.down) return;
        if (!this.pointer.dragging && Math.hypot(e.clientX - this.pointer.pressX, e.clientY - this.pointer.pressY) > this.S.clickDragThreshold) this.pointer.dragging = true;
        if (this.pointer.dragging) this.dragDelta.add(new THREE.Vector2(e.movementX, e.movementY));
      },
      pointerup: (e) => {
        if (!this.pointer.down || e.button !== this.pointer.button) return;
        if (!this.pointer.dragging && this.pointer.button === 0 && this.ready) this.click(e.clientX, e.clientY);
        this.pointer.down = this.pointer.dragging = false;
      },
      pointerleave: () => { this.pointer.inside = false; },
      wheel: (e) => { e.preventDefault(); this.scroll += -Math.sign(e.deltaY); },
      contextmenu: (e) => e.preventDefault(),
    };
    for (const [k, f] of Object.entries(this.handlers)) c.addEventListener(k, f, k === 'wheel' ? { passive: false } : undefined);
    this.keyDown = (e) => {
      if (e.target instanceof HTMLInputElement) return;
      this.keys.add(e.code);
      if (e.repeat || !this.ready) return;
      if (this.onKey?.(e)) { e.preventDefault(); return; }
      this.handleKey(e.code);
    };
    this.keyUp = (e) => this.keys.delete(e.code);
    window.addEventListener('keydown', this.keyDown);
    window.addEventListener('keyup', this.keyUp);
    window.addEventListener('blur', () => this.keys.clear());
  }

  handleKey(code) {
    const n = this.space.sweeps.length;
    switch (code) {
      case 'Digit1': this.setMode(ViewMode.Inside); break;
      case 'Digit2': this.setMode(ViewMode.Dollhouse); break;
      case 'Digit3': this.setMode(ViewMode.Floorplan); break;
      case 'F2': this.space.debugView = (this.space.debugView + 1) % 5; break;
      case 'KeyP': this.autoTour = !this.autoTour; break;
      case 'Home': this.goTo(0); break;
      case 'BracketRight': this.goTo((this.currentSweep + 1) % n); break;
      case 'BracketLeft': this.goTo((this.currentSweep - 1 + n) % n); break;
      case 'PageUp': this.setFloor(this.selectedFloor < 0 ? 0 : Math.min(this.selectedFloor + 1, this.space.floorCount - 1)); break;
      case 'PageDown': this.setFloor(this.selectedFloor <= 0 ? -1 : this.selectedFloor - 1); break;
    }
    if (this.mode !== ViewMode.Inside) return;
    if (code === 'KeyW' || code === 'ArrowUp') this.step(new THREE.Vector3(0, 0, -1));
    if (code === 'KeyS' || code === 'ArrowDown') this.step(new THREE.Vector3(0, 0, 1));
    if (code === 'KeyA') this.step(new THREE.Vector3(-1, 0, 0));
    if (code === 'KeyD') this.step(new THREE.Vector3(1, 0, 0));
  }

  held(...codes) { return codes.some((c) => this.keys.has(c)); }

  handleHeldKeys(dt) {
    let turn = 0;
    if (this.held('ArrowLeft', 'KeyQ')) turn += 1;
    if (this.held('ArrowRight', 'KeyE')) turn -= 1;
    if (this.mode === ViewMode.Inside) { this.targetYaw += turn * this.S.keyTurnSpeed * dt; return; }
    if (this.anim.active) return;
    if (this.held('KeyA')) turn += 1;
    if (this.held('KeyD')) turn -= 1;
    this.orbitYaw += turn * this.S.keyTurnSpeed * dt;
    let zoom = 0;
    if (this.held('KeyW', 'ArrowUp')) zoom -= 1;
    if (this.held('KeyS', 'ArrowDown')) zoom += 1;
    this.orbitDistanceGoal = clamp(this.orbitDistanceGoal * Math.exp(zoom * dt), 1.5, 500);
  }

  applyPointer() {
    if (this.dragDelta.lengthSq() > 0) this.drag(this.dragDelta, this.pointer.button);
    this.dragDelta.set(0, 0);
    if (Math.abs(this.scroll) > 0.001) {
      if (this.mode === ViewMode.Inside) this.targetFov = clamp(this.targetFov - this.scroll * this.S.zoomStep, this.S.minFov, this.S.maxFov);
      else this.orbitDistanceGoal = clamp(this.orbitDistanceGoal * Math.exp(-this.scroll * 0.15), 1.5, 500);
    }
    this.scroll = 0;
  }

  drag(delta, button) {
    if (this.anim.active) return;
    const height = Math.max(1, this.canvas.clientHeight);
    if (this.mode === ViewMode.Inside) {
      // Grab-style: the point under the cursor follows the cursor.
      const degPerPixel = this.fov / height * this.S.dragSensitivity;
      this.targetYaw += delta.x * degPerPixel;
      this.targetPitch = clamp(this.targetPitch + delta.y * degPerPixel, -85, 85);
      return;
    }
    const rotate = this.mode === ViewMode.Dollhouse ? button === 0 : button !== 0;
    if (rotate) {
      this.orbitYaw -= delta.x * this.S.orbitSensitivity;
      if (this.mode === ViewMode.Dollhouse) this.orbitPitch = clamp(this.orbitPitch + delta.y * this.S.orbitSensitivity, 8, 89);
      return;
    }
    // Pan in the ground plane so the scene follows the cursor.
    const worldPerPixel = 2 * this.orbitDistance * Math.tan(rad(this.camera.fov * 0.5)) / height;
    const pan = new THREE.Vector3(-delta.x, 0, -delta.y).applyAxisAngle(UP, rad(this.orbitYaw)).multiplyScalar(worldPerPixel);
    this.orbitTargetGoal.add(pan);
    this.orbitTarget.add(pan);
  }

  screenRay(x, y) {
    const r = this.canvas.getBoundingClientRect();
    this.raycaster.setFromCamera(new THREE.Vector2((x - r.left) / r.width * 2 - 1, -((y - r.top) / r.height) * 2 + 1), this.camera);
    return { origin: this.raycaster.ray.origin.clone(), dir: this.raycaster.ray.direction.clone() };
  }

  click(x, y) {
    if (this.anim.active || (this.clickHandler && this.clickHandler(x, y))) return;
    const { origin, dir } = this.screenRay(x, y);
    const target = this.mode === ViewMode.Inside ? this.pickInside(origin, dir) : this.pickFromAbove(origin, dir);
    if (target >= 0) this.goTo(target);
  }

  // ------------------------------------------------------------------ public API

  goTo(sweep) {
    if (!this.ready || sweep < 0 || sweep >= this.space.sweeps.length) return;
    if (this.mode !== ViewMode.Inside) { this.whenPanoramaReady(sweep, true); return; }
    if (sweep === this.currentSweep && !this.move.active) return;
    if (this.move.active || this.anim.active) this.queued = sweep;
    else this.whenPanoramaReady(sweep, false);
  }

  setMode(mode) {
    if (!this.ready || this.anim.active || mode === this.mode) return;
    if (mode === ViewMode.Inside) this.whenPanoramaReady(Math.max(0, this.currentSweep), true);
    else this.startOrbitMode(mode);
  }

  setFloor(floor) {
    if (!this.ready) return;
    this.selectedFloor = clamp(floor, -1, this.space.floorCount - 1);
    if (this.mode !== ViewMode.Inside) this.orbitTargetGoal.copy(this.orbitCenter());
  }

  /** Jumps instantly, looking along yaw/pitch (degrees) - for tests and tools. */
  teleport(sweep, yaw, pitch = 0) {
    if (!this.ready) return;
    if (!this.initialised) this.initialise();
    this.move = { active: false }; this.anim = { active: false };
    this.pending = this.queued = -1;
    this.mode = ViewMode.Inside;
    this.projection = 1;
    this.space.setCullBack(false);
    this.yaw = this.targetYaw = yaw; this.pitch = this.targetPitch = pitch;
    this.fov = this.targetFov = this.S.defaultFov;
    this.arrive(sweep);
  }

  initialise() {
    this.mode = ViewMode.Inside;
    this.move = { active: false }; this.anim = { active: false };
    this.pending = this.queued = -1;
    this.selectedFloor = -1;
    this.projection = 1;
    this.yaw = this.targetYaw = 0; this.pitch = this.targetPitch = 0;
    this.fov = this.targetFov = this.S.defaultFov;
    this.space.setCullBack(false);
    this.arrive(clamp(this.space.startSweep ?? 0, 0, this.space.sweeps.length - 1));
    this.initialised = true;
    if (this.startMode !== ViewMode.Inside) this.startOrbitMode(this.startMode);
  }

  // ------------------------------------------------------------------ choosing sweeps

  pickInside(origin, dir) {
    if (this.hoverSweep >= 0) return this.hoverSweep;
    const sp = this.space, cam = sp.sweepWorldPosition(this.currentSweep);
    let best = -1, bestScore = Infinity;
    const hit = sp.raycast(origin, dir, 200);
    if (hit) {
      const floorLike = hit.normal.y > 0.6;
      // Where would you stand to look at what you clicked? On floors: right there. Elsewhere: in front of it.
      const stand = floorLike ? hit.point : hit.point.clone().addScaledVector(dir, -0.6);
      for (let i = 0; i < sp.sweeps.length; i++) {
        if (i === this.currentSweep) continue;
        const m = sp.markerWorldPosition(i);
        const flat = Math.hypot(m.x - stand.x, m.z - stand.z);
        const dy = Math.abs(m.y - (floorLike ? hit.point.y : m.y));
        let score = flat + 2 * dy;
        if (!floorLike && deg(dir.angleTo(sp.sweepWorldPosition(i).clone().sub(cam))) > 40) continue;
        if (!this.isVisible(i)) score += 3;
        if (score < bestScore) { bestScore = score; best = i; }
      }
      if (best >= 0 && bestScore < (floorLike ? 2.5 : 4) + this.S.clickSnapRadius) return best;
      if (best >= 0 && this.isVisible(best)) return best;
    }
    // Nothing hit (window, sky) or no good candidate: nearest visible sweep in the click direction.
    best = -1;
    let bestAngle = 25;
    for (let i = 0; i < sp.sweeps.length; i++) {
      if (i === this.currentSweep || !this.isVisible(i)) continue;
      const a = deg(dir.angleTo(sp.sweepWorldPosition(i).clone().sub(cam)));
      if (a < bestAngle) { bestAngle = a; best = i; }
    }
    return best;
  }

  pickFromAbove(origin, dir) {
    if (this.hoverSweep >= 0) return this.hoverSweep;
    const hit = this.space.raycastBelow(origin, dir, 1000, this.clipHeight());
    if (!hit) return -1;
    let best = -1, bestDist = Infinity;
    for (let i = 0; i < this.space.sweeps.length; i++) {
      if (!this.floorShown(this.space.sweeps[i].floor)) continue;
      const d = this.space.markerWorldPosition(i).distanceTo(hit.point);
      if (d < bestDist) { bestDist = d; best = i; }
    }
    return best;
  }

  step(localDirection) {
    if (this.currentSweep < 0) return;
    const sp = this.space, S = this.S;
    const dir = localDirection.clone().applyAxisAngle(UP, rad(this.yaw));
    const from = sp.sweepWorldPosition(this.currentSweep);
    let best = -1, bestScore = Infinity;
    const consider = (i) => {
      const v = sp.sweepWorldPosition(i).clone().sub(from);
      v.y = 0;
      const dist = v.length(), angle = deg(dir.angleTo(v));
      if (i === this.currentSweep || dist < 0.2 || dist > S.stepMaxDistance || angle > S.stepMaxAngle) return;
      const a = angle / S.stepMaxAngle;
      const score = dist * (1 + 1.5 * a * a); // prefer close sweeps straight ahead
      if (score < bestScore) { bestScore = score; best = i; }
    };
    for (const i of sp.sweeps[this.currentSweep].neighbors) consider(i);
    if (best < 0) for (let i = 0; i < sp.sweeps.length; i++) if (this.isVisible(i)) consider(i);
    if (best >= 0) this.goTo(best);
  }

  isVisible(i) { return !!this.visible[i]; }

  computeVisibility() {
    const sp = this.space, n = sp.sweeps.length, from = sp.sweepWorldPosition(this.currentSweep);
    this.visible = new Array(n).fill(false);
    this.visibleFromCurrent = [];
    for (let i = 0; i < n; i++) {
      if (i === this.currentSweep) continue;
      const marker = sp.markerWorldPosition(i).clone().add(new THREE.Vector3(0, 0.15, 0));
      const dist = marker.distanceTo(from);
      this.visible[i] = dist <= this.S.markerMaxDistance * 1.6 && sp.lineOfSight(from, marker);
      if (this.visible[i] && dist <= this.S.markerMaxDistance) this.visibleFromCurrent.push(i);
    }
  }

  // ------------------------------------------------------------------ inside transitions

  startMove(to) {
    const from = this.currentSweep >= 0 ? this.space.sweepWorldPosition(this.currentSweep) : this.camera.position;
    const target = this.space.sweepWorldPosition(to);
    this.move = { active: true, from: this.currentSweep, to, fromPos: from.clone(), toPos: target.clone(), start: now(), duration: this.S.transitionDuration(from.distanceTo(target)) };
    this.space.panoramas.requestDepth(to);
    this.space.panoramas.request(to, this.S.panoMaxSize, -5);
  }

  /** Moves (or flies in) once the target's panorama is loaded - after at most 4 s. */
  whenPanoramaReady(sweep, flyIn) {
    this.pending = sweep;
    this.pendingIsFlyIn = flyIn;
    this.pendingSince = now();
    if (!this.space.panoramas.isReady(sweep)) this.space.panoramas.requestProgressive(sweep, this.S.panoMaxSize, -10);
    this.updatePending();
  }

  updatePending() {
    if (this.pending < 0 || (!this.space.panoramas.isReady(this.pending) && now() - this.pendingSince < 4)) return;
    const target = this.pending;
    this.pending = -1;
    if (this.pendingIsFlyIn) this.beginFlyIn(target);
    else if (this.mode === ViewMode.Inside && !this.move.active) this.startMove(target);
  }

  updateMove() {
    if (!this.move.active || this.moveT < 1) return;
    this.move.active = false;
    this.arrive(this.move.to);
    if (this.queued >= 0) {
      const q = this.queued;
      this.queued = -1;
      this.goTo(q);
    }
  }

  arrive(sweep) {
    const sp = this.space, S = this.S;
    this.currentSweep = sweep;
    this.arrivedAt = now();
    sp.panoramas.requestProgressive(sweep, S.panoMaxSize, 0);
    sp.panoramas.requestDepth(sweep);
    this.computeVisibility();
    // Prefetch neighbours (nearest first): the closest two at full size, the rest at the small tier.
    const here = sp.sweepWorldPosition(sweep);
    const neighbors = [...(sp.sweeps[sweep].neighbors.length ? sp.sweeps[sweep].neighbors : this.visibleFromCurrent)];
    neighbors.sort((a, b) => sp.sweepWorldPosition(a).distanceToSquared(here) - sp.sweepWorldPosition(b).distanceToSquared(here));
    for (let k = 0; k < neighbors.length && k < S.prefetchNeighbors; k++)
      sp.panoramas.request(neighbors[k], k < 2 ? S.panoMaxSize : (sp.sweeps[neighbors[k]].tiers[0]?.size ?? S.panoMaxSize), 10 + k);
  }

  updateTour() {
    if (!this.autoTour || this.mode !== ViewMode.Inside || this.move.active || this.pending >= 0 || now() - this.arrivedAt < 2.5) return;
    this.arrivedAt = now();
    this.goTo((this.currentSweep + 1) % this.space.sweeps.length);
  }

  // ------------------------------------------------------------------ dollhouse / floor plan

  orbitCenter() {
    const center = this.space.worldBounds.getCenter(new THREE.Vector3());
    if (this.selectedFloor < 0) return center;
    const pts = this.space.sweeps.map((s, i) => s.floor === this.selectedFloor ? this.space.markerWorldPosition(i) : null).filter(Boolean);
    if (!pts.length) return center;
    const c = new THREE.Box3().setFromPoints(pts).getCenter(new THREE.Vector3());
    return new THREE.Vector3(c.x, this.space.floorHeightWorld(this.selectedFloor) + 1.2, c.z);
  }

  orbitDistanceFor(mode) {
    const size = this.space.worldBounds.getSize(new THREE.Vector3());
    const radius = Math.max(3, Math.hypot(size.x, size.z) * 0.5);
    const fov = mode === ViewMode.Floorplan ? this.S.floorplanFov : this.S.dollhouseFov;
    return radius / Math.tan(rad(fov * 0.5)) * 1.05;
  }

  /** Camera looking down at target from pitch degrees above: { position, quaternion }. */
  static orbitPose(target, yaw, pitch, distance) {
    const q = new THREE.Quaternion().setFromEuler(new THREE.Euler(rad(-pitch), rad(yaw), 0, 'YXZ'));
    const back = new THREE.Vector3(0, 0, 1).applyQuaternion(q);
    return { position: target.clone().addScaledVector(back, distance), quaternion: q };
  }

  startOrbitMode(mode) {
    this.orbitYaw = this.mode === ViewMode.Inside ? this.yaw : this.orbitYaw;
    this.orbitPitch = mode === ViewMode.Floorplan ? 89.9 : 40;
    this.orbitTarget.copy(this.orbitCenter());
    this.orbitTargetGoal.copy(this.orbitTarget);
    this.orbitDistance = this.orbitDistanceGoal = this.orbitDistanceFor(mode);
    const to = MatterportNavigator.orbitPose(this.orbitTarget, this.orbitYaw, this.orbitPitch, this.orbitDistance);
    this.animate(mode, to.position, to.quaternion, mode === ViewMode.Floorplan ? this.S.floorplanFov : this.S.dollhouseFov, 0, this.currentSweep);
    this.space.setCullBack(true);
    this.move = { active: false };
  }

  beginFlyIn(sweep) {
    this.yaw = this.targetYaw = this.mode === ViewMode.Inside ? this.yaw : this.orbitYaw;
    this.pitch = this.targetPitch = 0;
    this.fov = this.targetFov = this.S.defaultFov;
    const q = new THREE.Quaternion().setFromAxisAngle(UP, rad(this.yaw));
    this.animate(ViewMode.Inside, this.space.sweepWorldPosition(sweep), q, this.fov, 1, sweep);
    this.currentSweep = sweep;
    this.space.panoramas.requestDepth(sweep);
  }

  animate(target, toPos, toRot, toFov, toProjection, sweep) {
    this.anim = {
      active: true, target, start: now(), duration: this.S.modeTransitionTime,
      fromPos: this.camera.position.clone(), toPos: toPos.clone(), fromRot: this.camera.quaternion.clone(), toRot: toRot.clone(),
      fromFov: this.camera.fov, toFov, fromProj: this.projection, toProj: toProjection, sweep,
    };
  }

  updateModeAnim() {
    const a = this.anim;
    if (!a.active) return;
    const t = clamp((now() - a.start) / Math.max(0.01, a.duration), 0, 1);
    const e = ease(t);
    this.camera.position.lerpVectors(a.fromPos, a.toPos, e);
    this.camera.quaternion.slerpQuaternions(a.fromRot, a.toRot, e);
    this.camera.fov = a.fromFov + (a.toFov - a.fromFov) * e;
    this.camera.updateProjectionMatrix();
    // Projection fades out early when leaving a sweep, and in late when arriving, like Matterport.
    const phase = a.toProj > a.fromProj ? inverseLerp(0.55, 1, t) : inverseLerp(0, 0.45, t);
    this.projection = a.fromProj + (a.toProj - a.fromProj) * phase;
    if (t < 1) return;
    a.active = false;
    this.mode = a.target;
    this.projection = a.toProj;
    if (this.mode === ViewMode.Inside) {
      this.space.setCullBack(false);
      this.arrive(a.sweep);
    }
  }

  // ------------------------------------------------------------------ frame

  update(dt) {
    this.markers.beginFrame();
    if (!this.ready) return;
    if (!this.initialised) this.initialise();
    this.handleHeldKeys(dt);
    this.applyPointer();
    this.updatePending();
    this.updateMove();
    this.updateModeAnim();
    this.updateCamera(dt);
    this.updateHover();
    this.updateTour();
    this.pushView();
    this.drawMarkers();
  }

  updateCamera(dt) {
    if (this.anim.active) return;
    const k = 1 - Math.exp(-this.S.lookSmoothing * dt);
    const cam = this.camera;
    if (this.mode === ViewMode.Inside) {
      let dyaw = ((this.targetYaw - this.yaw) % 360 + 540) % 360 - 180; // shortest way round
      this.yaw += dyaw * k;
      this.pitch += (this.targetPitch - this.pitch) * k;
      this.fov += (this.targetFov - this.fov) * k;
      const pos = this.move.active ? new THREE.Vector3().lerpVectors(this.move.fromPos, this.move.toPos, ease(this.moveT)) : this.space.sweepWorldPosition(this.currentSweep);
      cam.position.copy(pos);
      cam.rotation.set(rad(this.pitch), rad(this.yaw), 0, 'YXZ');
      cam.fov = this.fov;
    } else {
      this.orbitTarget.lerp(this.orbitTargetGoal, k);
      this.orbitDistance += (this.orbitDistanceGoal - this.orbitDistance) * k;
      const pose = MatterportNavigator.orbitPose(this.orbitTarget, this.orbitYaw, this.orbitPitch, this.orbitDistance);
      cam.position.copy(pose.position);
      cam.quaternion.copy(pose.quaternion);
      cam.fov += ((this.mode === ViewMode.Floorplan ? this.S.floorplanFov : this.S.dollhouseFov) - cam.fov) * k;
    }
    cam.updateProjectionMatrix();
  }

  clipHeight() {
    if (this.mode === ViewMode.Inside || this.selectedFloor < 0 || this.selectedFloor + 1 >= this.space.floorCount) return Infinity;
    return this.space.floorHeightWorld(this.selectedFloor + 1) - 0.25;
  }

  floorShown(floor) { return this.selectedFloor < 0 || floor === this.selectedFloor; }

  pushView() {
    let a = this.currentSweep, b = -1, blend = 0;
    if (this.move.active) {
      a = this.move.from;
      b = this.move.to;
      blend = ease(inverseLerp(0.1, 0.9, this.moveT));
    } else if (this.anim.active) a = this.anim.sweep;
    this.space.setView(a, b, blend, this.projection, this.viewClipHeight);
  }

  // ------------------------------------------------------------------ hover, markers, cursor

  updateHover() {
    this.hoverSweep = -1;
    this.reticle = null;
    const p = this.pointer;
    if (this.anim.active || p.dragging || this.clickHandler || !p.inside) return;
    const { origin, dir } = this.screenRay(p.x, p.y);
    const sp = this.space;
    if (this.mode === ViewMode.Inside) {
      if (this.move.active) return;
      const hit = sp.raycast(origin, dir, 200);
      if (!hit) return;
      this.reticle = hit;
      let best = this.S.markerRadius * 2.2;
      for (const i of this.visibleFromCurrent) {
        const d = sp.markerWorldPosition(i).distanceTo(hit.point);
        if (d < best) { best = d; this.hoverSweep = i; }
      }
      return;
    }
    const r = this.canvas.getBoundingClientRect();
    let bestPx = 22;
    for (let i = 0; i < sp.sweeps.length; i++) {
      if (!this.floorShown(sp.sweeps[i].floor)) continue;
      const v = sp.markerWorldPosition(i).clone().project(this.camera);
      if (v.z > 1) continue;
      const d = Math.hypot((v.x + 1) / 2 * r.width + r.left - p.x, (1 - v.y) / 2 * r.height + r.top - p.y);
      if (d < bestPx) { bestPx = d; this.hoverSweep = i; }
    }
  }

  drawMarkers() {
    const S = this.S, sp = this.space;
    if (S.showMarkers && !this.anim.active && !this.clickHandler) {
      if (this.mode === ViewMode.Inside) {
        const here = sp.sweepWorldPosition(this.currentSweep);
        for (const i of this.visibleFromCurrent) {
          if (this.move.active && i === this.move.to) continue;
          const m = sp.markerWorldPosition(i);
          let fade = 1 - inverseLerp(S.markerMaxDistance * 0.6, S.markerMaxDistance, m.distanceTo(here));
          if (this.move.active) fade *= 0.5;
          const c = [...S.markerColor];
          c[3] *= fade * (i === this.hoverSweep ? 1 : 0.75);
          this.markers.draw(m, UP, S.markerRadius * (i === this.hoverSweep ? 1.2 : 1), c);
        }
      } else {
        const scale = clamp(this.orbitDistance / 25, 0.6, 3);
        for (let i = 0; i < sp.sweeps.length; i++) {
          if (!this.floorShown(sp.sweeps[i].floor)) continue;
          const c = [...S.markerColor];
          if (i !== this.hoverSweep) c[3] *= 0.7;
          this.markers.draw(sp.markerWorldPosition(i), UP, S.markerRadius * scale * (i === this.hoverSweep ? 1.3 : 1), c, 0.55, 0.5);
        }
      }
    }
    if (S.showReticle && this.reticle && this.hoverSweep < 0) this.markers.draw(this.reticle.point, this.reticle.normal, S.reticleRadius, [1, 1, 1, 0.75], 0.82, 0.12);
  }

  dispose() {
    for (const [k, f] of Object.entries(this.handlers)) this.canvas.removeEventListener(k, f);
    window.removeEventListener('keydown', this.keyDown);
    window.removeEventListener('keyup', this.keyUp);
    this.markers.dispose();
  }
}
