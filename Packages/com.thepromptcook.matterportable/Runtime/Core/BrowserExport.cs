namespace Matterportable.Core
{
    /// <summary>
    /// Fallback for networks where Matterport blocks non-browser clients: a script you paste into the browser's
    /// developer console while viewing the space on my.matterport.com. It runs the same query from the page (with
    /// the browser's own session) and saves the answer as a JSON file that the Unity importer can read.
    /// </summary>
    public static class BrowserExport
    {
        const string Placeholder = "__MATTERPORTABLE_MODEL_ID__";

        /// <param name="modelId">Model to export, or null to read it from the page URL (?m=...).</param>
        public static string BuildScript(string modelId = null)
        {
            var query = MatterportQueries.ModelQuery(Placeholder).Render();
            var idExpr = string.IsNullOrEmpty(modelId)
                ? "new URLSearchParams(location.search).get('m') || prompt('Matterport model ID (11 characters):')"
                : JsonValue.Quote(modelId);
            return @"// Matterportable browser export - paste into the developer console on https://my.matterport.com/show/?m=<model id>
(async () => {
  const modelId = " + idExpr + @";
  if (!modelId) return;
  if (location.hostname !== 'my.matterport.com') {
    alert('Open https://my.matterport.com/show/?m=' + modelId + ' first, then run this script there.');
    return;
  }
  let query = " + JsonValue.Quote(query) + @".split(" + JsonValue.Quote(Placeholder) + @").join(modelId);
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
";
        }
    }
}
