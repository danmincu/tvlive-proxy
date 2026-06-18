// rdslive-resolver
// --------------------------------------------------------------------------
// The provider rotates the HLS playlist URL (host, sometimes token) every few
// hours. A real user has to open the ad-heavy player page, hit play, and read the
// "...-got.htm" request off DevTools. This service automates that with a headless
// (stealth) Chromium: it loads the player page, captures the playlist request the
// player fires, verifies it, and POSTs it to rdslive-proxy's /admin/source.
//
// Trigger: reactive (polls the proxy's /health; resolves when the stream is
// down/stalled) + a periodic safety re-resolve. A min-interval guard avoids
// hammering the source page.
// --------------------------------------------------------------------------
import { chromium } from 'playwright-extra';
import stealth from 'puppeteer-extra-plugin-stealth';

chromium.use(stealth());

const PROXY_BASE   = process.env.PROXY_BASE        || 'http://rdslive-proxy:13001';
const ADMIN_TOKEN  = process.env.PROXY_ADMIN_TOKEN || '';
const SOURCE_PAGE  = process.env.SOURCE_PAGE       || 'https://rdslive.org/antena-1/';
const ORIGIN       = process.env.PROXY_ORIGIN      || 'https://canale-tv.net';
const REFERER      = process.env.PROXY_REFERER     || 'https://canale-tv.net/';
const UA = process.env.PROXY_UA ||
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36';

const POLL_SECONDS     = +(process.env.RESOLVE_POLL_SECONDS     || 30);   // /health poll
const PERIODIC_HOURS   = +(process.env.RESOLVE_PERIODIC_HOURS   || 4);    // proactive re-resolve
const MIN_INTERVAL_SEC = +(process.env.RESOLVE_MIN_INTERVAL_SEC || 120);  // don't resolve more often than this
const NAV_TIMEOUT_MS   = +(process.env.RESOLVE_NAV_TIMEOUT_MS   || 45000);
// What a playlist request looks like — matches alpha1.yosefina1.cfd/ah1/...-got.htm etc.
const CAPTURE_RE = new RegExp(process.env.RESOLVE_PATTERN || '\\.cfd/.*-got\\.htm');

const log = (...a) => console.log(new Date().toISOString(), '[resolver]', ...a);

let busy = false;
let lastResolveAt = 0;

// Launch headless Chromium, load the player page, and capture the first request
// that matches the playlist signature (across the page and any ad popups).
async function captureUrl() {
  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled'],
  });
  try {
    const ctx = await browser.newContext({ userAgent: UA, viewport: { width: 1280, height: 720 } });

    let captured = null;
    ctx.on('request', (req) => {
      if (!captured && CAPTURE_RE.test(req.url())) captured = req.url();
    });

    const page = await ctx.newPage();
    // Drop heavy noise to speed things up (we only need the playlist request to fire).
    await page.route('**/*', (route) => {
      const t = route.request().resourceType();
      return (t === 'image' || t === 'font') ? route.abort() : route.continue();
    });

    await page.goto(SOURCE_PAGE, { waitUntil: 'domcontentloaded', timeout: NAV_TIMEOUT_MS }).catch(() => {});

    // Wait for the playlist request; after a few seconds, try common play triggers
    // in case a user gesture is required.
    const deadline = Date.now() + NAV_TIMEOUT_MS;
    let clickedAt = 0;
    while (!captured && Date.now() < deadline) {
      await page.waitForTimeout(700);
      if (!captured && !clickedAt && Date.now() > deadline - NAV_TIMEOUT_MS + 5000) {
        clickedAt = Date.now();
        for (const sel of [
          'button[aria-label*="play" i]', '.vjs-big-play-button', '.jw-icon-display',
          '.plyr__control--overlaid', 'video', '#player', '.play-button', '.play', 'body',
        ]) {
          try { await page.click(sel, { timeout: 800 }); } catch {}
          if (captured) break;
        }
      }
    }
    return captured;
  } finally {
    await browser.close().catch(() => {});
  }
}

// Confirm the captured URL actually serves an HLS playlist (with the headers the
// origin expects) before we push it — so we never set a broken source.
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
    return (await r.json()).ok === false; // stalled or unreachable upstream
  } catch { return true; }
}

async function resolve(reason) {
  if (busy) return;
  if (Date.now() - lastResolveAt < MIN_INTERVAL_SEC * 1000) { log('skip (min interval):', reason); return; }
  busy = true;
  lastResolveAt = Date.now();
  try {
    log('resolving…', reason);
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
