// Tunables for navigation, rendering and streaming. Same names and defaults as MatterportViewerSettings (Unity)
// and ViewerSettings (Godot); keep them in sync.
export const ViewerSettings = {
  dragSensitivity: 1, keyTurnSpeed: 100, lookSmoothing: 18, defaultFov: 75, minFov: 25, maxFov: 95, zoomStep: 6,
  transitionBase: 0.5, transitionPerMeter: 0.12, transitionMax: 1.6, clickDragThreshold: 6, stepMaxAngle: 50, stepMaxDistance: 8, clickSnapRadius: 1.2,
  panoMaxSize: 2048, prefetchNeighbors: 4, panoMemoryBudgetMB: 1200, depthMapSize: 256,
  anisotropy: 4, // texture filtering quality (cheap on GPUs; the headless checks set ?aniso=1)
  showMarkers: true, markerRadius: 0.2, markerColor: [1, 1, 1, 0.9], markerMaxDistance: 10, showReticle: true, reticleRadius: 0.28,
  dollhouseFov: 45, floorplanFov: 20, orbitSensitivity: 0.25, modeTransitionTime: 1.1, backgroundColor: [0.11, 0.11, 0.12],
  transitionDuration(meters) {
    return Math.min(Math.max(this.transitionBase + this.transitionPerMeter * meters, this.transitionBase), Math.max(this.transitionBase, this.transitionMax));
  },
};
