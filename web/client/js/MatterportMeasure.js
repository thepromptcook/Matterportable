// Mirrors MatterportMeasure (Unity, Godot): Matterport's measurement mode. The behaviour is Core MeasureTool running
// in WebAssembly; this file feeds it the pointer ray and draws what it returns (lines, points, labels, the + cursor)
// on a 2D overlay, faded where hidden. M or the Measure button toggles it; Escape or Confirm ends a line, Escape
// again leaves; Shift locks to an axis, Alt disables snapping, Delete removes the selected measurement.
import * as THREE from 'three';
import { coreOf, fromCore } from './Core.js';
import { ViewMode } from './MatterportNavigator.js';

const Snap = { Surface: 1, Axis: 5 };
const Units = { Imperial: 0, Metric: 1 };
const Shadow = 'rgba(0,0,0,0.55)', Ink = '#1a1a1f', Accent = '#2e73d9', HoverColor = '#99c7ff';
const AxisColors = ['#ffffff', '#f25c4d', '#5a9eff', '#61d96b']; // MeasureAxis: None, X, Vertical, Z

/** Feet and inches where the browser's region uses them (US, Liberia, Myanmar), metres elsewhere. */
const defaultUnits = () => /-(US|LR|MM)$/i.test(navigator.language || '') ? Units.Imperial : Units.Metric;

export class MatterportMeasure {
  constructor(space, nav) {
    this.space = space;
    this.nav = nav;
    this.core = space.core;
    this.active = false;
    this.frame = null;
    this.started = false;
    this.listState = '';
    this.canvas = Object.assign(document.createElement('canvas'), { className: 'overlay' });
    document.body.appendChild(this.canvas);
    this.ctx = this.canvas.getContext('2d');
    this.buildPanel();
  }

  buildPanel() {
    const p = this.panel = document.createElement('div');
    p.className = 'panel measure-panel';
    p.hidden = true;
    p.innerHTML = `<h3>Measure</h3>
      <div class="help">Click to place points · Shift: lock to axis · Alt: no snapping<br>Esc: finish line, again to exit · Del: delete selected</div>
      <div class="list"></div>
      <div class="row"><button data-k="snap">Snapping</button><button data-k="cont">Continuous lines</button><button data-k="units"></button>
      <button data-k="confirm">✓ Confirm</button><button data-k="done">Done</button></div>`;
    this.list = p.querySelector('.list');
    this.buttons = Object.fromEntries([...p.querySelectorAll('button[data-k]')].map((b) => [b.dataset.k, b]));
    const f = () => this.frame;
    this.buttons.snap.onclick = () => this.options(!f().snapping, f().continuous, f().units);
    this.buttons.cont.onclick = () => this.options(f().snapping, !f().continuous, f().units);
    this.buttons.units.onclick = () => this.options(f().snapping, f().continuous, f().units === Units.Imperial ? Units.Metric : Units.Imperial);
    this.buttons.confirm.onclick = () => this.core.MeasureFinish();
    this.buttons.done.onclick = () => this.setActive(false);
    document.body.appendChild(p);
  }

  options(snapping, continuous, units) { this.core.MeasureOptions(snapping, continuous, units); }

  /** Enters or leaves measure mode. Measurements stay until the space is closed (they are temporary). */
  setActive(on) {
    if (on && !this.space.hasMesh) return;
    if (on === this.active) return;
    this.active = on;
    if (on && !this.started) {
      this.core.MeasureReset(defaultUnits());
      this.started = true;
    }
    if (!on) this.core.MeasureFinish();
    this.nav.clickHandler = on ? () => { this.core.MeasureClick(); return true; } : null;
    this.panel.hidden = !on;
    if (!on) this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
  }

  /** Keys, called before the HUD's. True when used. */
  onKey(e) {
    if (e.code === 'KeyM') { this.setActive(!this.active); return true; }
    if (!this.active) return false;
    if (e.code === 'Escape') { if (!this.core.MeasureEscape()) this.setActive(false); return true; }
    if (e.code === 'Delete' || e.code === 'Backspace') { if (this.frame?.sel >= 0) this.core.MeasureDelete(this.frame.sel); return true; }
    if (e.code === 'Enter' || e.code === 'NumpadEnter') { this.core.MeasureFinish(); return true; }
    return false;
  }

  input(x, y, blocked) {
    const { origin, dir } = this.nav.screenRay(x, y);
    const view = this.nav.mode === ViewMode.Inside ? 0 : this.nav.mode === ViewMode.Dollhouse ? 1 : 2;
    const clip = this.nav.viewClipHeight;
    const keys = this.nav.keys;
    return [...coreOf(origin), ...coreOf(dir), THREE.MathUtils.degToRad(this.nav.camera.fov) / Math.max(1, this.canvas.clientHeight),
      keys.has('ShiftLeft') || keys.has('ShiftRight') ? 1 : 0, keys.has('AltLeft') || keys.has('AltRight') ? 1 : 0,
      blocked ? 1 : 0, view, Number.isFinite(clip) ? clip : 1e9];
  }

  /** Places a point (or selects) at a screen position, as a click there would. For automated checks. */
  clickAt(x, y) {
    this.core.MeasureUpdate(this.input(x, y, false));
    this.core.MeasureClick();
  }

  update() {
    if (!this.active) return;
    const p = this.nav.pointer;
    this.core.MeasureUpdate(this.input(p.x, p.y, !p.inside || this.nav.isChangingMode));
    this.frame = JSON.parse(this.core.MeasureFrame(coreOf(this.nav.camera.position)));
    this.draw();
    this.updatePanel();
  }

  updatePanel() {
    const f = this.frame;
    this.buttons.snap.classList.toggle('on', f.snapping);
    this.buttons.cont.classList.toggle('on', f.continuous);
    this.buttons.units.textContent = f.units === Units.Imperial ? 'Units: ft' : 'Units: m';
    this.buttons.confirm.hidden = !f.draft;
    const state = JSON.stringify([f.lines, f.sel]);
    if (state === this.listState) return;
    this.listState = state;
    this.list.replaceChildren(...f.lines.map(([total, segments, floorPlan], i) => {
      const row = document.createElement('div');
      row.className = 'item';
      const select = document.createElement('button');
      select.textContent = `${i + 1}.  ${total}${segments > 1 ? `  (${segments} segments)` : ''}${floorPlan ? '  · floor plan' : ''}`;
      select.classList.toggle('on', f.sel === i);
      select.onclick = () => this.core.MeasureSelect(f.sel === i ? -1 : i);
      const del = document.createElement('button');
      del.textContent = '✕';
      del.onclick = () => this.core.MeasureDelete(i);
      row.append(select, del);
      return row;
    }));
  }

  // ------------------------------------------------------------------ drawing (screen space)

  project(core) {
    const v = fromCore(core).project(this.nav.camera);
    return [(v.x + 1) / 2 * this.canvas.clientWidth, (1 - v.y) / 2 * this.canvas.clientHeight, v.z <= 1];
  }

  segmentColor(s) {
    const line = s[8];
    if (line >= 0 && line === this.frame.sel) return Accent;
    if (line >= 0 && line === this.frame.hov) return HoverColor;
    return line < 0 ? AxisColors[s[7]] : '#ffffff';
  }

  /** The part of a Core-frame segment in front of the camera's near plane (Core MeasureTool.ClipToFront). */
  clipToFront(a, b) {
    const cam = this.nav.camera;
    const eye = coreOf(cam.position), fwd = coreOf(cam.getWorldDirection(new THREE.Vector3())), near = cam.near + 0.01;
    const d = (p) => (p[0] - eye[0]) * fwd[0] + (p[1] - eye[1]) * fwd[1] + (p[2] - eye[2]) * fwd[2] - near;
    const da = d(a), db = d(b);
    if (da < 0 && db < 0) return null;
    const lerp = (p, q, t) => p.map((v, i) => v + (q[i] - v) * t);
    if (da < 0) return [lerp(a, b, da / (da - db)), b];
    if (db < 0) return [a, lerp(b, a, db / (db - da))];
    return [a, b];
  }

  draw() {
    const c = this.canvas, ctx = this.ctx, dpr = Math.min(window.devicePixelRatio || 1, 2);
    if (c.width !== Math.round(c.clientWidth * dpr) || c.height !== Math.round(c.clientHeight * dpr)) {
      c.width = Math.round(c.clientWidth * dpr);
      c.height = Math.round(c.clientHeight * dpr);
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, c.clientWidth, c.clientHeight);
    ctx.lineCap = 'round';
    const f = this.frame;
    const line = (a, b, width, color, alpha = 1) => {
      ctx.globalAlpha = alpha; ctx.strokeStyle = color; ctx.lineWidth = width;
      ctx.beginPath(); ctx.moveTo(a[0], a[1]); ctx.lineTo(b[0], b[1]); ctx.stroke();
    };
    for (const pass of [1, 0]) // hidden pieces first, faded; then the visible ones
      for (const s of f.seg) {
        if (s[6] !== pass) continue;
        const clipped = this.clipToFront(s.slice(0, 3), s.slice(3, 6));
        if (!clipped) continue;
        const pa = this.project(clipped[0]), pb = this.project(clipped[1]);
        const occluded = s[6] === 1;
        line(pa, pb, occluded ? 3 : 5, Shadow, occluded ? 0.4 : 1);
        line(pa, pb, occluded ? 1.5 : 3, this.segmentColor(s), occluded ? 0.4 : 1);
      }
    ctx.globalAlpha = 1;
    for (const p of f.pts) {
      const [x, y, front] = this.project(p);
      if (!front) continue;
      ctx.fillStyle = Shadow; ctx.beginPath(); ctx.arc(x, y, 5.5, 0, Math.PI * 2); ctx.fill();
      ctx.fillStyle = p[3] >= 0 && p[3] === f.sel ? Accent : '#ffffff'; ctx.beginPath(); ctx.arc(x, y, 4, 0, Math.PI * 2); ctx.fill();
    }
    ctx.font = '15px system-ui, -apple-system, "Segoe UI", sans-serif';
    ctx.textBaseline = 'middle';
    for (const l of f.lab) {
      const [x, y, front] = this.project(l);
      if (!front) continue;
      const selected = l[3] >= 0 && l[3] === f.sel;
      const w = ctx.measureText(l[4]).width + 16;
      ctx.fillStyle = selected ? Accent : 'rgba(255,255,255,0.95)';
      ctx.beginPath(); ctx.roundRect(x - w / 2, y - 11, w, 22, 10); ctx.fill();
      ctx.fillStyle = selected ? '#ffffff' : Ink;
      ctx.fillText(l[4], x - w / 2 + 8, y + 1);
    }
    const cur = f.cur;
    if (cur[0]) {
      const [x, y, front] = this.project(cur.slice(1, 4));
      if (front) {
        for (const [w, color] of [[4, Shadow], [2, '#ffffff']]) {
          line([x - 10, y], [x + 10, y], w, color);
          line([x, y - 10], [x, y + 10], w, color);
        }
        if (cur[4] !== Snap.Surface) {
          ctx.globalAlpha = 1; ctx.strokeStyle = AxisColors[cur[5]]; ctx.lineWidth = 2;
          ctx.beginPath(); ctx.arc(x, y, 9, 0, Math.PI * 2); ctx.stroke();
        }
      }
    }
    ctx.globalAlpha = 1;
  }

  dispose() {
    this.setActive(false);
    this.canvas.remove();
    this.panel.remove();
  }
}
