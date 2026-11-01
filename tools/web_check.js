// Drives the web version in headless Chromium (Playwright). Used by tools/web-check.sh.
//   node tools/web_check.js <base url> <out dir> [extra query]          demo runs: inside (after a move), dollhouse, measure, walk
//   node tools/web_check.js <base url> <out dir> --compare <id> [sweeps] photo-vs-mesh pairs for tools/compare_alignment.py
const { chromium } = require('playwright');

const [base, out, ...rest] = process.argv.slice(2);

async function openPage(browser, query, size) {
  const page = await browser.newPage({ viewport: size });
  const errors = [];
  page.on('console', (m) => { if (m.type() === 'error' || m.text().includes('[Matterportable]')) errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('response', (r) => { if (r.status() >= 400) errors.push(`HTTP ${r.status()} ${r.url()}`); });
  await page.goto(base + query + (query.includes('?') ? '&' : '?') + 'aa=0&aniso=1');
  await page.waitForFunction(() => window.mp && ((window.mp.checks && window.mp.checks !== 'running') || window.mp.error), null, { timeout: 300000 });
  return { page, errors };
}

(async () => {
  // Mesa (llvmpipe) through the virtual display is ~4x faster than headless SwiftShader; quality extras are off
  // (aa=0, aniso=1) because every pixel is shaded on the CPU here.
  const browser = await chromium.launch({ headless: !process.env.DISPLAY, args: ['--ignore-gpu-blocklist', '--use-gl=angle', '--use-angle=gl'] });
  let failed = false;
  if (rest[0] === '--compare') {
    const id = rest[1], n = Number(rest[2] ?? 6);
    const { page, errors } = await openPage(browser, `?open=${encodeURIComponent(id)}`, { width: 960, height: 540 });
    const count = await page.evaluate(() => window.mp.sweepCount());
    await page.evaluate(() => window.mp.hideUi());
    for (let k = 0; k < Math.min(n, count); k++) {
      const sweep = Math.floor((k + 0.5) * count / Math.min(n, count));
      for (const yaw of [0, 90, 180, 270])
        for (const [view, name] of [[1, 'mesh'], [2, 'pano']]) {
          await page.evaluate(([s, y, v]) => window.mp.showView(s, y, v), [sweep, yaw, view]);
          await page.screenshot({ path: `${out}/s${String(sweep).padStart(3, '0')}_y${String(yaw).padStart(3, '0')}_${name}.png` });
        }
    }
    console.log(`[Matterportable] compare: ${Math.min(n, count)} sweeps saved to ${out}` + (errors.length ? `; errors: ${errors.join(' | ')}` : ''));
    failed = errors.length > 0;
  } else {
    const extra = rest.join('&');
    const runs = [['inside', 'goto=1'], ['dollhouse', 'mode=dollhouse'], ['measure', 'measure=1'], ['walk', 'mode=walk&yaw=54&walk=1']];
    for (const [name, query] of runs) {
      const { page, errors } = await openPage(browser, `?open=demo&${query}${extra ? '&' + extra : ''}`, { width: 1600, height: 900 });
      const checks = await page.evaluate(() => window.mp.checks ?? window.mp.error);
      await page.screenshot({ path: `${out}/${name}.png` });
      const expect = { measure: '1 measurement(s), drawing yes, menu closed', walk: 'mode Walk' }[name] ?? 'Loaded';
      const ok = errors.length === 0 && String(checks).includes(expect);
      console.log(`${ok ? '' : 'error: '}[Matterportable] ${checks}\n  screenshot: ${out}/${name}.png` + (errors.length ? `\n  page errors: ${errors.join(' | ')}` : ''));
      if (!ok) failed = true;
      await page.close();
    }
  }
  await browser.close();
  process.exit(failed ? 1 : 0);
})().catch((e) => { console.error('error:', e.message); process.exit(1); });
