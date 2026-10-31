// Mirrors MatterportHud (Unity) / Hud (Godot): view modes, floors, measure, tour, menu, key hint, F1 stats.
import { ViewMode } from './MatterportNavigator.js';
import { DebugViewNames } from './MatterportSpace.js';

export class Hud {
  constructor(space, nav, measure, onMenu) {
    Object.assign(this, { space, nav, measure, onMenu });
    const el = (cls, html = '') => Object.assign(document.createElement('div'), { className: cls, innerHTML: html });
    this.root = document.createElement('div');
    this.buttons = el('hud-buttons');
    const button = (text, onclick) => Object.assign(document.createElement('button'), { textContent: text, onclick });
    this.modes = [
      button('Inside', () => nav.setMode(ViewMode.Inside)),
      button('Dollhouse', () => nav.setMode(ViewMode.Dollhouse)),
      button('Floor plan', () => nav.setMode(ViewMode.Floorplan)),
    ];
    this.measureButton = button('📏 Measure', () => measure.setActive(!measure.active));
    this.tour = button('▶ Tour', () => { nav.autoTour = !nav.autoTour; });
    this.floors = el('floors');
    this.buttons.append(button('☰ Menu', onMenu), ...this.modes, this.measureButton, this.tour, this.floors);
    this.hint = el('hint', 'Drag to look · Click to move · W A S D step · ←/→ turn · Wheel zoom · 1/2/3 Inside/Dollhouse/Floor plan · M measure · [ ] prev/next · P tour · F1 stats · F2 render view · H hide · Esc menu');
    this.stats = el('stats panel');
    this.stats.hidden = true;
    this.status = el('status-center panel');
    this.root.append(this.buttons, this.hint, this.stats, this.status);
    document.body.appendChild(this.root);
    this.floorCount = -1;
    this.frames = 0; this.fpsTime = performance.now(); this.fps = 0;
  }

  onKey(e) {
    if (e.code === 'F1') { this.stats.hidden = !this.stats.hidden; return true; }
    if (e.code === 'KeyH') { this.root.hidden = !this.root.hidden; return true; }
    if (e.code === 'Escape') { this.onMenu(); return true; }
    return false;
  }

  update() {
    const sp = this.space, nav = this.nav;
    this.frames++;
    if (performance.now() - this.fpsTime > 500) { this.fps = this.frames * 1000 / (performance.now() - this.fpsTime); this.frames = 0; this.fpsTime = performance.now(); }
    this.status.hidden = sp.isLoaded && !sp.error && !nav.isWaitingForPanorama;
    this.status.textContent = sp.error ? `Matterportable\n${sp.error}` : sp.isLoaded ? 'Loading…' : `Loading Matterport space…\n${sp.status}`;
    if (!sp.isLoaded) return;
    this.modes.forEach((b, i) => b.classList.toggle('on', nav.mode === i));
    this.measureButton.classList.toggle('on', this.measure.active);
    this.measureButton.hidden = !sp.hasMesh;
    this.tour.textContent = nav.autoTour ? '■ Tour' : '▶ Tour';
    if (this.floorCount !== sp.floorCount) this.buildFloors();
    this.floors.hidden = nav.mode === ViewMode.Inside || sp.floorCount < 2;
    [...this.floors.children].forEach((b, i) => b.classList.toggle('on', nav.selectedFloor === i - 1));
    if (!this.stats.hidden) this.stats.textContent = this.statsText();
  }

  buildFloors() {
    this.floorCount = this.space.floorCount;
    const labels = this.space.info.floors ?? [];
    this.floors.replaceChildren(...[-1, ...Array.from({ length: this.floorCount }, (_, i) => i)].map((f) =>
      Object.assign(document.createElement('button'), { textContent: f < 0 ? 'All' : (labels[f] || `Floor ${f + 1}`), onclick: () => this.nav.setFloor(f) })));
  }

  statsText() {
    const sp = this.space, nav = this.nav, cache = sp.panoramas;
    let loaded = 0, depth = 0;
    for (const e of cache.entries.values()) { if (e.faces) loaded++; if (e.depth) depth++; }
    const cur = nav.currentSweep, s = sp.sweeps[cur];
    const cal = sp.info.calibration;
    return [
      `${sp.info.name}  (${sp.id})`, `${this.fps.toFixed(0)} fps`,
      s ? `Sweep ${cur}/${sp.sweeps.length - 1}  id ${s.id}  floor ${s.floor}` : '',
      `Mode ${['Inside', 'Dollhouse', 'Floorplan'][nav.mode]}  FOV ${nav.camera.fov.toFixed(0)}°`,
      `Panoramas: ${loaded} cached (${(cache.bytesUsed / 1048576).toFixed(0)} MB), ${depth} depth maps, loading ${cache.activeLoads}, queued ${cache.queue.length}`,
      sp.hasMesh ? `Mesh: ${sp.info.triangles.toLocaleString()} triangles, textures ${sp.texturesLoaded}/${sp.info.parts.filter((p) => p.texture).length}` : 'Mesh: none (panorama-only)',
      `Alignment: ${cal.method}${cal.score != null ? ` (score ${cal.score.toFixed(2)})` : ''}`,
      `Render view (F2): ${DebugViewNames[sp.debugView]}`, `Loaded in ${sp.lastLoadSeconds.toFixed(1)}s`,
    ].filter(Boolean).join('\n');
  }

  dispose() { this.root.remove(); }
}
