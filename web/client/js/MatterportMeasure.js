// Mirrors MatterportMeasure (Unity, Godot): Matterport's measurement mode. The behaviour is Core MeasureTool running
// in WebAssembly; this file feeds it the pointer ray and draws the screen-space items it returns (lines, dots, rings,
// labels) on a 2D overlay. M or the Measure button toggles it; Escape or Confirm ends a line, Escape again leaves;
// Shift locks to an axis, Alt disables snapping, Delete removes the selected measurement.
import * as THREE from 'three';
import { coreOf } from './Core.js';

const Units = { Imperial: 0, Metric: 1 };
const Ink = '#1a1a1f', Accent = '#2e73d9'; // Core MeasureOverlay.Ink / Accent

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
    this.nav.setMeasuring(on);
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
    const keys = this.nav.keys;
    return [...coreOf(origin), ...coreOf(dir), THREE.MathUtils.degToRad(this.nav.camera.fov) / Math.max(1, this.canvas.clientHeight),
      keys.has('ShiftLeft') || keys.has('ShiftRight') ? 1 : 0, keys.has('AltLeft') || keys.has('AltRight') ? 1 : 0,
      blocked ? 1 : 0]; // the view mode and clip height come from the Core navigator
  }

  /** Places a point (or selects) at a screen position, as a click there would. For automated checks. */
  clickAt(x, y) {
    this.core.MeasureUpdate(this.input(x, y, false));
    this.core.MeasureClick();
  }

  update() {
    if (!this.active) return;
    const p = this.nav.pointer;
    this.core.MeasureUpdate(this.input(p.x, p.y, !p.over || this.nav.isChangingMode));
    if (this.nav.clicked) this.core.MeasureClick(); // the navigator leaves clicks to us while measuring
    this.frame = JSON.parse(this.core.MeasureFrame(this.canvas.clientWidth, this.canvas.clientHeight));
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

  // ------------------------------------------------------------------ drawing (screen-space items from the Core)

  draw() {
    const c = this.canvas, ctx = this.ctx, dpr = Math.min(window.devicePixelRatio || 1, 2);
    if (c.width !== Math.round(c.clientWidth * dpr) || c.height !== Math.round(c.clientHeight * dpr)) {
      c.width = Math.round(c.clientWidth * dpr);
      c.height = Math.round(c.clientHeight * dpr);
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, c.clientWidth, c.clientHeight);
    ctx.lineCap = 'round';
    ctx.font = '15px system-ui, -apple-system, "Segoe UI", sans-serif';
    ctx.textBaseline = 'middle';
    for (const [kind, x, y, x2, y2, size, color, text, selected] of this.frame.items) {
      ctx.beginPath();
      if (kind === 0) { // line
        ctx.strokeStyle = color; ctx.lineWidth = size;
        ctx.moveTo(x, y); ctx.lineTo(x2, y2); ctx.stroke();
      } else if (kind === 1) { // dot
        ctx.fillStyle = color; ctx.arc(x, y, size, 0, Math.PI * 2); ctx.fill();
      } else if (kind === 2) { // ring
        ctx.strokeStyle = color; ctx.lineWidth = 2; ctx.arc(x, y, size, 0, Math.PI * 2); ctx.stroke();
      } else { // label
        const w = ctx.measureText(text).width + 16;
        ctx.fillStyle = selected ? Accent : 'rgba(255,255,255,0.95)';
        ctx.roundRect(x - w / 2, y - 11, w, 22, 10); ctx.fill();
        ctx.fillStyle = selected ? '#ffffff' : Ink;
        ctx.fillText(text, x - w / 2 + 8, y + 1);
      }
    }
  }

  dispose() {
    this.setActive(false);
    this.canvas.remove();
    this.panel.remove();
  }
}
