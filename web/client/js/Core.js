// The shared C# Core running as WebAssembly (web/wasm/Bridge.cs), plus the Core <-> three.js frame conversion.
// three.js is right-handed with Y up like Godot, so three = Core with Z mirrored.
import * as THREE from 'three';

let bridge = null;
let loading = null;

/** Starts the .NET WebAssembly runtime once (call early: it takes a moment) and returns the exported Bridge. */
export function loadCore() {
  loading ??= (async () => {
    const { dotnet } = await import('../_framework/dotnet.js');
    const runtime = await dotnet.create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    bridge = exports.Bridge;
    return bridge;
  })();
  return loading;
}

export const coreOf = (v) => [v.x, v.y, -v.z];
export const fromCore = (a, i = 0) => new THREE.Vector3(a[i], a[i + 1], -a[i + 2]);
