// Mirrors MatterportHud (Unity) / Hud (Godot): view modes, floors, measure, tour, empty rooms, menu, key hint, Walk
// mode's hint for the prop under the pointer, F1 stats (text from Core ViewerText through the WebAssembly bridge).
import { ViewMode } from './MatterportNavigator.js';

export class Hud {
  constructor(space, nav, measure, onMenu) {
    Object.assign(this, { space, nav, measure, onMenu });
    const el = (cls, text = '') => Object.assign(document.createElement('div'), { className: cls, textContent: text });
    const button = (text, onclick) => Object.assign(document.createElement('button'), { textContent: text, onclick });
    this.root = document.createElement('div');
    this.modes = ['Inside', 'Dollhouse', 'Floor plan', 'Walk'].map((label, mode) => button(label, () => nav.setMode(mode)));
    this.measureButton = button('📏 Measure', () => measure.setActive(!measure.active));
    this.tour = button('▶ Tour', () => nav.toggleTour());
    this.empty = button('', () => nav.setEmptyRooms(!nav.emptyRooms));
    this.empty.hidden = !(space.hasCleanPanoramas && space.props.some((p) => p)); // the furniture painted out (tools/reconstruct)
    this.floors = el('floors');
    const bar = el('hud-buttons');
    bar.append(button('☰ Menu', onMenu), ...this.modes, this.measureButton, this.tour, this.empty, this.floors);
    this.stats = el('stats panel');
    this.stats.hidden = true;
    this.status = el('status-center panel', 'Loading…');
    this.propHint = el('prop-hint');
    this.root.append(bar, el('hint', space.core.KeyHint()), this.propHint, this.stats, this.status);
    this.propState = '';
    document.body.appendChild(this.root);
    this.floorCount = -1;
    this.frames = 0;
    this.fpsTime = performance.now();
    this.fps = 0;
  }

  onKey(e) {
    if (e.code === 'F1') { this.stats.hidden = !this.stats.hidden; return true; }
    if (e.code === 'KeyH') { this.root.hidden = !this.root.hidden; return true; }
    if (e.code === 'Escape') { this.onMenu(); return true; }
    return false;
  }

  update() {
    const sp = this.space, nav = this.nav, f = nav.frame;
    if (!f) return;
    this.frames++;
    const now = performance.now();
    if (now - this.fpsTime > 500) { this.fps = this.frames * 1000 / (now - this.fpsTime); this.frames = 0; this.fpsTime = now; }
    this.status.hidden = !nav.isWaitingForPanorama;
    this.modes.forEach((b, i) => b.classList.toggle('on', f.mode === i));
    this.measureButton.classList.toggle('on', this.measure.active);
    this.measureButton.hidden = this.modes[ViewMode.Walk].hidden = !sp.hasMesh; // both need the mesh
    this.tour.textContent = nav.autoTour ? '■ Tour' : '▶ Tour';
    if (this.floorCount !== f.floorCount) this.buildFloors(f.floorCount);
    this.floors.hidden = f.mode === ViewMode.Inside || f.floorCount < 2;
    [...this.floors.children].forEach((b, i) => b.classList.toggle('on', f.selectedFloor === i - 1));
    const propState = `${f.mode}|${f.highlightProp}|${f.propFlags}`; // ask the Core for the text only when it can change
    if (propState !== this.propState) {
      this.propState = propState;
      this.propHint.textContent = sp.core.PropHint();
      this.propHint.hidden = !this.propHint.textContent;
      this.empty.textContent = sp.core.EmptyRoomsButton();
      this.empty.classList.toggle('on', nav.emptyRooms);
    }
    if (!this.stats.hidden) this.stats.textContent = sp.core.Stats(this.fps, sp.lastLoadSeconds, sp.texturesLoaded);
  }

  buildFloors(count) {
    this.floorCount = count;
    const labels = this.space.info.floors ?? [];
    this.floors.replaceChildren(...[-1, ...Array.from({ length: count }, (_, i) => i)].map((f) =>
      Object.assign(document.createElement('button'), { textContent: f < 0 ? 'All' : (labels[f] || `Floor ${f + 1}`), onclick: () => this.nav.setFloor(f) })));
  }

  dispose() { this.root.remove(); }
}
