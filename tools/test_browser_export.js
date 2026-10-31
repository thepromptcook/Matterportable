// Runs Tools~/browser-export.js in Node against a mock Matterport GraphQL endpoint that rejects unknown fields
// (graphql-java style errors with line numbers), and checks that the script prunes them and saves an export.
// Usage: node tools/test_browser_export.js
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const script = fs.readFileSync(path.join(__dirname, '../Packages/com.thepromptcook.matterportable/Tools~/browser-export.js'), 'utf8');
const schema = new Set([
  'model', 'model/id', 'model/name', 'model/assets', 'model/assets/meshes', 'model/assets/meshes/url', 'model/assets/meshes/format',
  'model/assets/meshes/resolution', 'model/assets/meshes/filename', 'model/assets/textures', 'model/assets/textures/urlTemplate',
  'model/assets/textures/quality', 'model/assets/textures/resolution', 'model/assets/textures/format',
  'model/floors', 'model/floors/id', 'model/floors/label', 'model/floors/sequence',
  'model/locations', 'model/locations/id', 'model/locations/index', 'model/locations/label', 'model/locations/neighbors',
  'model/locations/floor', 'model/locations/floor/id', 'model/locations/pano', 'model/locations/pano/id',
  'model/locations/pano/position', 'model/locations/pano/position/x', 'model/locations/pano/position/y', 'model/locations/pano/position/z',
  'model/locations/pano/rotation', 'model/locations/pano/rotation/x', 'model/locations/pano/rotation/y',
  'model/locations/pano/rotation/z', 'model/locations/pano/rotation/w',
  'model/locations/pano/skyboxes', 'model/locations/pano/skyboxes/resolution', 'model/locations/pano/skyboxes/urlTemplate',
]);

let requests = 0;
let saved = null;
let done;
const finished = new Promise(r => (done = r));

function validate(query) {
  const errors = [];
  const stack = [];
  const lines = query.split('\n');
  for (let i = 1; i < lines.length; i++) {
    const t = lines[i].trim();
    if (!t) continue;
    if (t === '}') { stack.pop(); continue; }
    const opens = t.endsWith('{');
    let name = t.replace(/\{$/, '').trim();
    if (name.includes('(')) name = name.slice(0, name.indexOf('('));
    const p = [...stack, name].join('/');
    if (!schema.has(p)) errors.push({ message: `Validation error (FieldUndefined@[${p}]) : Field '${name}' is undefined`, locations: [{ line: i + 1, column: 1 }] });
    // Empty selection sets are invalid too.
    if (opens && lines[i + 1] && lines[i + 1].trim() === '}') errors.push({ message: `Validation error (SubSelectionRequired@[${p}])`, locations: [{ line: i + 1, column: 1 }] });
    if (opens) stack.push(name);
  }
  return errors;
}

const sandbox = {
  location: { search: '?m=DemoSpace01', hostname: 'my.matterport.com' },
  URLSearchParams,
  console: { log: (...a) => console.log('  [page]', ...a), error: (...a) => { console.error('  [page error]', ...a); done(); } },
  alert: m => { console.error('alert:', m); done(); },
  prompt: () => null,
  fetch: async (url, opts) => {
    requests++;
    const body = JSON.parse(opts.body);
    const errors = validate(body.query);
    const json = errors.length ? { errors, data: null } : { data: { model: { id: 'DemoSpace01', name: 'Demo', locations: [{ id: 's0' }] } } };
    return { json: async () => json };
  },
  Blob: class { constructor(parts) { this.text = parts.join(''); } },
  URL: { createObjectURL: b => { sandbox.__blob = b; return 'blob:mock'; } },
  document: {
    body: { appendChild() {} },
    createElement: () => ({ click() { saved = { name: this.download, payload: JSON.parse(sandbox.__blob.text) }; done(); }, remove() {} }),
  },
};
vm.createContext(sandbox);
vm.runInContext(script, sandbox);

finished.then(() => {
  if (!saved) { console.error('FAIL: nothing was saved'); process.exit(1); }
  const ok = saved.name === 'matterport-DemoSpace01.json' && saved.payload.response.data.model.id === 'DemoSpace01' && saved.payload.matterportable === 1;
  console.log(ok ? `ok: saved ${saved.name} after ${requests} request(s)` : 'FAIL: unexpected export ' + JSON.stringify(saved));
  process.exit(ok ? 0 : 1);
});
setTimeout(() => { console.error('FAIL: timed out'); process.exit(1); }, 5000);
