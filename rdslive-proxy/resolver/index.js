// rdslive-resolver
// --------------------------------------------------------------------------
// The provider rotates the HLS playlist URL (host, sometimes token) every few
// hours. A real user has to open the ad-heavy player page, hit play, and read the
// "...-got.htm" request off DevTools. This service automates that with a headless
// (stealth) Chromium: it loads the player page, captures the playlist request the
// player fires, verifies it, and POSTs it to rdslive-proxy's /admin/source.
//
// Trigger: reactive (polls the proxy's /health; resolves when down/stalled) + a
// periodic safety re-resolve. A min-interval guard avoids hammering the source page.
//
// When capture fails it dumps diagnostics (candidate requests, page title, frames,
// and a screenshot/HTML to DEBUG_DIR) so the flow can be tuned without guessing.
// --------------------------------------------------------------------------
import { chromium } from 'playwright-extra';
import stealth from 'puppeteer-extra-plugin-stealth';
import { promises as fs } from 'fs';

chromium.use(stealth());

const PROXY_BASE   = process.env.PROXY_BASE        || 'http://rdslive-proxy:13001';
const ADMIN_TOKEN  = process.env.PROXY_ADMIN_TOKEN || '';
const SOURCE_PAGE  = process.env.SOURCE_PAGE       || 'https://rdslive.org/antena-1/';
const ORIGIN       = process.env.PROXY_ORIGIN      || 'https://canale-tv.net';
const REFERER      = process.env.PROXY_REFERER     || 'https://canale-tv.net/';
const UA = process.env.PROXY_UA ||
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36';

const POLL_SECONDS     = +(process.env.RESOLVE_POLL_SECONDS     || 30);
const PERIODIC_HOURS   = +(process.env.RESOLVE_PERIODIC_HOURS   || 4);
const MIN_INTERVAL_SEC = +(process.env.RESOLVE_MIN_INTERVAL_SEC || 120);
const NAV_TIMEOUT_MS   = +(process.env.RESOLVE_NAV_TIMEOUT_MS   || 45000);
const DEBUG_DIR        = process.env.DEBUG_DIR || '/tmp/resolver-debug';
const SOURCE_HOST = (() => { try { return new URL(SOURCE_PAGE).host; } catch { return ''; } })();
// What a playlist request looks like — disguised "...-got.htm" OR a real ".m3u8",
// on any host (the provider rotates the whole domain).
const CAPTURE_RE = new RegExp(process.env.RESOLVE_PATTERN || '(-got\\.htm|\\.m3u8)');
// Looser net for diagnostics — anything that might be the stream.
const CANDIDATE_RE = /m3u8|got\.htm|tokenized|\.cfd|playlist|\.ts(\?|$)|embed|player|stream/i;

const log = (...a) => console.log(new Date().toISOString(), '[resolver]', ...a);

let busy = false;
let lastResolveAt = 0;

async function tryPlay(page) {
  const sels = [
    'button[aria-label*="play" i]', '.vjs-big-play-button', '.jw-icon-display',
    '.plyr__control--overlaid', '#player', '.play-button', '.play', 'video',
  ];
  for (const f of page.frames()) {
    for (const sel of sels) { try { await f.click(sel, { timeout: 400 }); } catch {} }
    try { await f.click('body', { position: { x: 240, y: 160 }, timeout: 400 }); } catch {}
  }
}

async function dumpDiagnostics(page, candidates) {
  let title = '', url = '';
  try { title = await page.title(); } catch {}
  try { url = page.url(); } catch {}
  const frames = page.frames().map((f) => f.url()).filter((u) => u && u !== 'about:blank');
  log('DIAG title =', JSON.stringify(title), '| final url =', url);
  log('DIAG frames:', frames.slice(0, 12).join('  |  ') || '(none)');
  log('DIAG candidate requests seen (' + candidates.length + '):');
  candidates.slice(0, 40).forEach((u) => log('   .', u));
  try {
    await fs.mkdir(DEBUG_DIR, { recursive: true });
    const ts = new Date().toISOString().replace(/[:.]/g, '-');
    await page.screenshot({ path: `${DEBUG_DIR}/fail-${ts}.png` }).catch(() => {});
    await fs.writeFile(`${DEBUG_DIR}/fail-${ts}.html`, await page.content().catch(() => '')).catch(() => {});
    log('DIAG saved screenshot + html under', DEBUG_DIR);
  } catch (e) { log('DIAG dump failed:', e?.message); }
}

async function captureUrl() {
  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled'],
  });
  try {
    const ctx = await browser.newContext({ userAgent: UA, viewport: { width: 1280, height: 720 } });

    let captured = null;
    const seen = new Set();
    const candidates = [];
    ctx.on('request', (req) => {
      const u = req.url();
      if (!captured && CAPTURE_RE.test(u)) captured = u;
      if (CANDIDATE_RE.test(u) && !seen.has(u)) { seen.add(u); candidates.push(u); }
    });

    const page = await ctx.newPage();
    await page.route('**/*', (route) => {
      const req = route.request();
      const t = req.resourceType();
      if (t === 'image' || t === 'font') return route.abort();
      // Block ad-driven attempts to navigate the MAIN frame off the source site
      // (popunder/redirect hijacks — e.g. the keto landing page). Sub-frames (the
      // player iframe, canale-tv, the .cfd stream) are allowed through.
      if (req.isNavigationRequest() && req.frame() === page.mainFrame()) {
        let h = '';
        try { h = new URL(req.url()).host; } catch {}
        if (h && SOURCE_HOST && h !== SOURCE_HOST) {
          log('blocked main-frame redirect ->', h);
          return route.abort();
        }
      }
      return route.continue();
    });
    // Auto-dismiss any popup windows ads open (they don't hijack the main page,
    // but close them to keep things clean).
    ctx.on('page', (p) => { if (p !== page) p.close().catch(() => {}); });

    await page.goto(SOURCE_PAGE, { waitUntil: 'domcontentloaded', timeout: NAV_TIMEOUT_MS }).catch(() => {});

    const deadline = Date.now() + NAV_TIMEOUT_MS;
    let clicked = false;
    while (!captured && Date.now() < deadline) {
      await page.waitForTimeout(700);
      if (!captured && !clicked && Date.now() > deadline - NAV_TIMEOUT_MS + 5000) {
        clicked = true;
        await tryPlay(page);
      }
    }

    if (!captured) await dumpDiagnostics(page, candidates);
    return captured;
  } finally {
    await browser.close().catch(() => {});
  }
}

async function verify(url) {
  try {
    const r = await fetch(url, {
      headers: { accept: '*/*', origin: ORIGIN, referer: REFERER, 'user-agent': UA },
      signal: AbortSignal.timeout(15000),
    });
    if (!r.ok) return false;
    const text = await r.text();
    return text.replace(/^﻿/, '').trimStart().startsWith('#EXTM3U');
  } catch { return false; }
}

async function proxyGetCurrent() {
  try {
    const r = await fetch(PROXY_BASE + '/admin/source', { headers: { 'X-Admin-Token': ADMIN_TOKEN } });
    if (!r.ok) return null;
    return (await r.json()).current || null;
  } catch { return null; }
}

async function proxyPush(url) {
  const r = await fetch(PROXY_BASE + '/admin/source', {
    method: 'POST',
    headers: { 'X-Admin-Token': ADMIN_TOKEN, 'Content-Type': 'text/plain' },
    body: url,
  });
  return r.ok;
}

async function proxyDown() {
  try {
    const r = await fetch(PROXY_BASE + '/health', { signal: AbortSignal.timeout(8000) });
    if (!r.ok) return true;
    return (await r.json()).ok === false;
  } catch { return true; }
}

async function resolve(reason) {
  if (busy) return;
  if (Date.now() - lastResolveAt < MIN_INTERVAL_SEC * 1000) { log('skip (min interval):', reason); return; }
  busy = true;
  lastResolveAt = Date.now();
  try {
    log('resolving...', reason);
    const url = await captureUrl();
    if (!url) { log('no playlist URL captured'); return; }
    log('captured:', url);
    if (!(await verify(url))) { log('verification failed (not an HLS playlist), ignoring'); return; }
    const current = await proxyGetCurrent();
    if (current === url) { log('unchanged, nothing to do'); return; }
    log(await proxyPush(url) ? 'pushed new source to proxy' : 'push FAILED');
  } catch (e) {
    log('resolve error:', e?.message || e);
  } finally {
    busy = false;
  }
}

async function main() {
  if (!ADMIN_TOKEN) { log('FATAL: PROXY_ADMIN_TOKEN is not set'); process.exit(1); }
  log('started. page=', SOURCE_PAGE, 'proxy=', PROXY_BASE, 'pattern=', CAPTURE_RE.source);

  await resolve('startup');

  setInterval(async () => { if (await proxyDown()) await resolve('stalled/down'); }, POLL_SECONDS * 1000);
  setInterval(() => resolve('periodic'), PERIODIC_HOURS * 3600 * 1000);
}

main();
