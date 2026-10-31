// Matterportable browser export - paste into the developer console on https://my.matterport.com/show/?m=<model id>
(async () => {
  const modelId = new URLSearchParams(location.search).get('m') || prompt('Matterport model ID (11 characters):');
  if (!modelId) return;
  if (location.hostname !== 'my.matterport.com') {
    alert('Open https://my.matterport.com/show/?m=' + modelId + ' first, then run this script there.');
    return;
  }
  let query = "query MatterportableModel {\n  model(id: \"__MATTERPORTABLE_MODEL_ID__\") {\n    id\n    name\n    assets {\n      meshes {\n        id\n        status\n        filename\n        format\n        resolution\n        url\n        validUntil\n      }\n      textures {\n        id\n        status\n        format\n        resolution\n        quality\n        urlTemplate\n        validUntil\n      }\n      tilesets {\n        status\n        url\n        urlTemplate\n        validUntil\n        tilesetVersion\n        tilesetDepth\n        tilesetPreset\n      }\n    }\n    floors {\n      id\n      label\n      sequence\n    }\n    locations {\n      id\n      index\n      label\n      neighbors\n      floor {\n        id\n      }\n      room {\n        id\n      }\n      position {\n        x\n        y\n        z\n      }\n      pano {\n        id\n        label\n        placement\n        source\n        position {\n          x\n          y\n          z\n        }\n        rotation {\n          x\n          y\n          z\n          w\n        }\n        resolutions\n        skyboxes {\n          id\n          resolution\n          status\n          urlTemplate\n          tileResolution\n          tileCount\n          tileUrlTemplate\n          validUntil\n        }\n      }\n    }\n  }\n}\n".split("__MATTERPORTABLE_MODEL_ID__").join(modelId);
  const prune = (text, lines) => {
    const src = text.split('\n'), out = [];
    for (let i = 0; i < src.length; i++) {
      if (!lines.has(i + 1)) { out.push(src[i]); continue; }
      if (src[i].trim().endsWith('{')) {
        let depth = 1;
        while (depth > 0 && ++i < src.length) {
          const t = src[i].trim();
          if (t.endsWith('{')) depth++;
          else if (t === '}') depth--;
        }
      }
    }
    return out.join('\n');
  };
  for (let attempt = 1; attempt <= 16; attempt++) {
    const res = await fetch('/api/mp/models/graph', {
      method: 'POST',
      credentials: 'include',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ operationName: 'MatterportableModel', query, variables: {} }),
    });
    const json = await res.json();
    const bad = (json.errors || []).filter(e => /Validation|Cannot query field|undefined|SubSelection|Unknown field/i.test(e.message || ''));
    if (bad.length === 0) {
      const payload = { matterportable: 1, modelId, exportedAt: new Date().toISOString(), response: json };
      const blob = new Blob([JSON.stringify(payload)], { type: 'application/json' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = 'matterport-' + modelId + '.json';
      document.body.appendChild(a);
      a.click();
      a.remove();
      console.log('Matterportable: saved ' + a.download + '. In Unity: Window > Matterportable > Import > Import browser export. ' +
                  'Import it soon - the download links inside expire.');
      return;
    }
    const lines = new Set();
    for (const e of bad) for (const loc of (e.locations || [])) lines.add(loc.line);
    if (lines.size === 0) { console.error('Matterportable: query rejected', bad); return; }
    query = prune(query, lines);
  }
  console.error('Matterportable: gave up after 16 attempts.');
})();

