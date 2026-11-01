// Mirrors MatterportLauncher (Unity) / Launcher (Godot): the start menu. Paste a Matterport link, open the offline
// demo or a space the server already has; Esc or Menu returns here. The server downloads and prepares spaces.
// URL parameters drive automated checks (tools/web-check.sh): ?open=<link|demo>&sweep=N&mode=dollhouse|floorplan|walk
// &yaw=degrees&view=N&goto=N&walk=seconds&measure=1; window.mp reports state and offers showView() for the
// photo-vs-mesh comparison.
import { MatterportSpace } from './MatterportSpace.js';
import { MatterportNavigator, ViewMode, NavKeys } from './MatterportNavigator.js';
import { MatterportMeasure } from './MatterportMeasure.js';
import { Hud } from './Hud.js';

const LastLinkKey = 'Matterportable.LastLink', WalkKey = 'Matterportable.Walk';
const nextFrame = () => new Promise((r) => requestAnimationFrame(() => r()));

export class Launcher {
  constructor(renderer, scene, camera) {
    Object.assign(this, { renderer, scene, camera });
    this.params = new URLSearchParams(location.search);
    this.buildMenu();
    window.mp = this.automation();
    const open = this.params.get('open');
    if (open === 'demo') this.openDemo();
    else if (open) this.open(open);
  }

  buildMenu() {
    const m = this.menu = document.createElement('div');
    m.className = 'menu';
    m.innerHTML = `<div class="box">
      <h1>Matterportable</h1><p>Paste a Matterport link (or model ID) to walk through it.</p>
      <div class="row"><input placeholder="https://discover.matterport.com/space/rGMMnZDh3Tf"><button data-k="paste">Paste</button></div>
      <button data-k="open">Open space</button><button data-k="demo">Offline demo (no network)</button>
      <label class="check"><input type="checkbox" data-k="walk"> Start in Walk mode: move freely with W A S D (walls and furniture block you)</label>
      <div class="status"></div><div class="bar" hidden><div></div></div>
      <p>Spaces on this server (shared with the Unity and Godot projects):</p><div class="spaces"></div></div>`;
    document.body.appendChild(m);
    const q = (s) => m.querySelector(s);
    this.input = q('input');
    this.statusEl = q('.status');
    this.bar = q('.bar');
    this.spacesEl = q('.spaces');
    this.input.value = localStorage.getItem(LastLinkKey) ?? '';
    this.walkMode = q('[data-k=walk]');
    this.walkMode.checked = localStorage.getItem(WalkKey) === '1';
    this.walkMode.onchange = () => localStorage.setItem(WalkKey, this.walkMode.checked ? '1' : '0');
    this.input.addEventListener('keydown', (e) => { if (e.key === 'Enter') this.open(this.input.value); });
    q('[data-k=paste]').onclick = async () => {
      try { this.input.value = (await navigator.clipboard.readText()).trim(); } catch { this.input.focus(); this.setStatus('Press Ctrl+V to paste.'); }
    };
    q('[data-k=open]').onclick = () => this.open(this.input.value);
    q('[data-k=demo]').onclick = () => this.openDemo();
    this.refreshSpaces();
  }

  setStatus(text, progress) {
    this.statusEl.textContent = text;
    this.bar.hidden = progress === undefined;
    if (progress !== undefined) this.bar.firstElementChild.style.width = `${Math.round(progress * 100)}%`;
  }

  async refreshSpaces() {
    try {
      const spaces = await (await fetch('api/spaces')).json();
      this.spacesEl.replaceChildren(...spaces.map((s) => Object.assign(document.createElement('button'), {
        textContent: `${s.name}  (${s.id})`, onclick: () => this.open(s.id),
      })));
    } catch { /* server unreachable: the status line says so when opening */ }
  }

  async openDemo() {
    this.setStatus('Generating the demo house…', 0.02);
    await this.prepare(await fetch('api/demo', { method: 'POST' }));
  }

  async open(link) {
    link = (link ?? '').trim();
    if (!link) { this.setStatus("That doesn't look like a Matterport link or model ID."); return; }
    localStorage.setItem(LastLinkKey, link);
    this.setStatus('Opening…', 0.02);
    await this.prepare(await fetch(`api/open?link=${encodeURIComponent(link)}`, { method: 'POST' }));
  }

  /** Waits for the server to download and prepare the space, then shows it. */
  async prepare(response) {
    if (!response.ok) { this.setStatus((await response.text()) || `Server error ${response.status}`); return; }
    let s = await response.json();
    while (!s.ready && !s.error) {
      this.setStatus(s.stage, s.progress);
      await new Promise((r) => setTimeout(r, 400));
      s = await (await fetch(`api/spaces/${encodeURIComponent(s.id)}/status`)).json();
    }
    if (s.error) { this.setStatus(s.error); return; }
    await this.show(s.id);
  }

  async show(id) {
    this.close();
    this.menu.hidden = true;
    const sp = this.space = new MatterportSpace(this.renderer, this.scene, this.params.has('aniso') ? { anisotropy: Number(this.params.get('aniso')) } : {});
    try {
      await sp.load(id);
    } catch (e) {
      sp.error = e.message ?? String(e);
      this.close();
      this.menu.hidden = false;
      this.setStatus(sp.error);
      return;
    }
    const nav = this.nav = new MatterportNavigator(this.camera, this.renderer.domElement, sp, this.scene);
    const mode = { inside: ViewMode.Inside, dollhouse: ViewMode.Dollhouse, floorplan: ViewMode.Floorplan, walk: ViewMode.Walk }[this.params.get('mode')]
      ?? (this.walkMode.checked ? ViewMode.Walk : ViewMode.Inside);
    sp.core.Navigate(7, Number(this.params.get('sweep') ?? 0), mode, 0);
    if (this.params.has('yaw')) nav.look(Number(this.params.get('yaw')));
    nav.setDebugView(Number(this.params.get('view') ?? 0));
    const measure = this.measure = new MatterportMeasure(sp, nav);
    this.hud = new Hud(sp, nav, measure, () => this.showMenu());
    nav.onKey = (e) => measure.onKey(e) || this.hud.onKey(e);
    this.runChecks();
  }

  close() {
    this.hud?.dispose();
    this.measure?.dispose();
    this.nav?.dispose();
    this.space?.dispose();
    this.hud = this.measure = this.nav = this.space = null;
  }

  showMenu() {
    this.close();
    this.setStatus('');
    this.refreshSpaces();
    this.menu.hidden = false;
  }

  update(dt) {
    if (!this.space?.isLoaded || !this.nav) return;
    const frame = this.nav.update(dt);
    this.measure.update();
    this.space.update(frame, this.camera);
    this.hud.update();
  }

  // ------------------------------------------------------------------ automated checks

  /** Loaded, nothing loading, no animation: ready for a screenshot. */
  settled() {
    const s = this.settleState();
    return !!s && s.activeLoads === 0 && s.queued === 0 && !s.loaderBusy && !s.changingMode && !s.moving && !s.waiting && s.textures >= s.texturesWanted;
  }

  /** What settled() waits for (window.mp.settleState() shows it when a check hangs). */
  settleState() {
    const sp = this.space, nav = this.nav, f = nav?.frame;
    return f && { activeLoads: f.activeLoads, queued: f.queued, loaderBusy: sp.panoramas.busy, changingMode: nav.isChangingMode, moving: nav.isMoving,
      waiting: nav.isWaitingForPanorama, textures: sp.texturesLoaded, texturesWanted: sp.info.parts?.filter((p) => p.texture).length ?? 0 };
  }

  async waitSettled(frames = 20) {
    for (let i = 0; !this.settled() && i < 1200; i++) await nextFrame();
    for (let i = 0; i < frames; i++) await nextFrame();
  }

  /** ?goto=N moves there after loading; ?measure=1 places a measurement with real clicks and a real Escape. */
  async runChecks() {
    this.checks = 'running';
    await this.waitSettled(5);
    const go = this.params.get('goto');
    if (go !== null) { this.nav.goTo(Number(go)); await nextFrame(); await this.waitSettled(5); }
    const walk = Number(this.params.get('walk') ?? 0);
    if (walk > 0) { // hold W for that long
      this.nav.scriptedHeld = NavKeys.Forward;
      for (const start = performance.now(); performance.now() - start < walk * 1000;) await nextFrame();
      this.nav.scriptedHeld = 0;
    }
    if (this.params.get('measure')) {
      const w = this.renderer.domElement.clientWidth, h = this.renderer.domElement.clientHeight;
      this.measure.setActive(true);
      this.measure.clickAt(w * 0.30, h * 0.85);
      this.measure.clickAt(w * 0.62, h * 0.80);
      this.measure.clickAt(w * 0.62, h * 0.40);
      window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Escape', key: 'Escape' }));
      await nextFrame();
      this.measure.clickAt(w * 0.20, h * 0.60);
      Object.assign(this.nav.pointer, { x: w * 0.45, y: h * 0.55, over: true });
      for (let i = 0; i < 5; i++) await nextFrame();
    }
    await this.waitSettled(20);
    const f = this.measure.frame;
    const c = this.nav.coreCamera;
    this.checks = `${this.space.status}; mode ${['Inside', 'Dollhouse', 'Floorplan', 'Walk'][this.nav.frame.mode]}, sweep ${this.nav.frame.currentSweep}, camera (${c.map((v) => v.toFixed(2)).join(', ')})` +
      (f ? `; ${f.lines.length} measurement(s), drawing ${f.draft ? 'yes' : 'no'}, menu ${this.menu.hidden ? 'closed' : 'open'}` : '');
  }

  automation() {
    const self = this;
    return {
      get checks() { return self.checks; },
      get error() { return self.space?.error ?? null; },
      sweepCount: () => self.space?.sweeps.length ?? 0,
      settleState: () => self.settleState(),
      hideUi: () => { self.hud.root.hidden = true; self.nav.markers.hidden = true; },
      /** Photo-vs-mesh comparison: render view 1 (mesh texture) or 2 (photo) at a sweep and heading (Core yaw). */
      async showView(sweep, yaw, view) {
        self.nav.teleport(sweep, yaw, 10);
        self.nav.setDebugView(view);
        await self.waitSettled(4);
      },
    };
  }
}
