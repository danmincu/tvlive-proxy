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
const SOURCE_REFERER   = process.env.SOURCE_REFERER || 'https://rdslive.org/';
// Ad / tracker / redirect networks to block so they can't hijack the page
// (popunders, top-frame redirects like the keto landing page). The player's own
// domains (rdslive.org, canale-tv.net, *.cfd) are NOT here, so the player loads.
const AD_HOST_RE = new RegExp(process.env.AD_HOSTS_RE ||
  'ketogo|doubleclick|googlesyndication|googleadservices|googletagservices|google-analytics|googletagmanager|adservice|adnxs|taboola|outbrain|popads|popcash|propeller|onclick|onclck|exoclick|adsterra|hilltopads|juicyads|clickadu|monetag|bidvertiser|ad-?maven|pushwhy|histats|amung\\.us|yandex|quantserve|scorecardresearch|moatads|criteo|smartadserver|adskeeper|mgid|revcontent',
  'i');
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
    '.jw-icon-display', '.jwplayer', '.vjs-big-play-button', '.video-js',
    'button[aria-label*="play" i]', '.plyr__control--overlaid', '.play-button', '.play', '#player', 'video',
  ];
  for (const f of page.frames()) {
    // Directly start any <video> (muted, to satisfy autoplay) and click play UIs.
    try { await f.evaluate(() => { const v = document.querySelector('video'); if (v) { v.muted = true; const p = v.play && v.play(); if (p && p.catch) p.catch(() => {}); } }); } catch {}
    for (const sel of sels) { try { await f.click(sel, { timeout: 300 }); } catch {} }
    for (const pos of [{ x: 240, y: 160 }, { x: 480, y: 270 }, { x: 640, y: 360 }]) {
      try { await f.click('body', { position: pos, timeout: 300 }); } catch {}
    }
  }
}

// Look INSIDE the player iframe(s) for the stream URL / how it's obtained: the
// <video> src, what the frame actually fetched (resource timings), and any
// stream-ish URLs embedded in its HTML/JS. Also dumps each player frame's HTML.
async function inspectPlayerFrames(page) {
  for (const f of page.frames()) {
    const u = f.url();
    if (!/canale-tv|tv\.php/i.test(u)) continue;
    try {
      const info = await f.evaluate(() => {
        const v = document.querySelector('video');
        const html = document.documentElement.outerHTML;
        const STREAMISH = /\.cfd|\.m3u8|got\.htm|tokenized|playlist/i;
        const urlRe = /(https?:\/\/[^\s"'<>()]+)/g;
        const inHtml = Array.from(new Set((html.match(urlRe) || []).filter((x) => STREAMISH.test(x)))).slice(0, 25);
        let res = [];
        try { res = performance.getEntriesByType('resource').map((e) => e.name).filter((x) => STREAMISH.test(x)).slice(0, 25); } catch {}
        return {
          videoSrc: v ? (v.currentSrc || v.src || '') : '(no <video>)',
          videoReady: v ? v.readyState : null,
          inHtml, res, htmlLen: html.length,
        };
      });
      log('DIAG player frame:', u);
      log('   video.src   =', info.videoSrc, '| readyState =', info.videoReady, '| htmlLen =', info.htmlLen);
      log('   fetched (stream-ish):', JSON.stringify(info.res));
      log('   urls in html (stream-ish):', JSON.stringify(info.inHtml));
      try {
        const name = u.replace(/[^a-z0-9]+/gi, '_').slice(0, 60);
        await fs.writeFile(`${DEBUG_DIR}/frame-${name}.html`, await f.content()).catch(() => {});
      } catch {}
    } catch (e) { log('DIAG frame inspect failed for', u, '-', e?.message); }
  }
}

async function dumpDiagnostics(page, candidates, hosts) {
  let title = '', url = '';
  try { title = await page.title(); } catch {}
  try { url = page.url(); } catch {}
  const frames = page.frames().map((f) => f.url()).filter((u) => u && u !== 'about:blank');
  log('DIAG title =', JSON.stringify(title), '| final url =', url);
  log('DIAG frames:', frames.slice(0, 12).join('  |  ') || '(none)');
  log('DIAG all hosts contacted:', [...hosts].sort().join(', ') || '(none)');
  log('DIAG candidate requests seen (' + candidates.length + '):');
  candidates.slice(0, 40).forEach((u) => log('   .', u));
  try {
    await fs.mkdir(DEBUG_DIR, { recursive: true });
    await inspectPlayerFrames(page);
  } catch {}
  try {
    const ts = new Date().toISOString().replace(/[:.]/g, '-');
    await page.screenshot({ path: `${DEBUG_DIR}/fail-${ts}.png` }).catch(() => {});
    await fs.writeFile(`${DEBUG_DIR}/fail-${ts}.html`, await page.content().catch(() => '')).catch(() => {});
    log('DIAG saved screenshot + html under', DEBUG_DIR);
  } catch (e) { log('DIAG dump failed:', e?.message); }
}

async function captureUrl() {
  const browser = await chromium.launch({
    headless: true,
    args: [
      '--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled',
      '--autoplay-policy=no-user-gesture-required', // let the player auto-start so it fetches the stream
    ],
  });
  try {
    const ctx = await browser.newContext({ userAgent: UA, viewport: { width: 1280, height: 720 } });

    // Neutralize popunders/redirect helpers before any page script runs.
    await ctx.addInitScript(() => {
      try { window.open = () => null; } catch (e) {}
    });

    let captured = null;
    const seen = new Set();
    const candidates = [];
    const hosts = new Set();
    ctx.on('request', (req) => {
      const u = req.url();
      try { hosts.add(new URL(u).host); } catch {}
      if (!captured && CAPTURE_RE.test(u)) captured = u;
      if (CANDIDATE_RE.test(u) && !seen.has(u)) { seen.add(u); candidates.push(u); }
    });

    const page = await ctx.newPage();
    await page.route('**/*', (route) => {
      const req = route.request();
      const t = req.resourceType();
      let host = '';
      try { host = new URL(req.url()).host; } catch {}
      // Drop heavy noise and ALL ad/redirect networks (so they can't hijack the tab).
      if (t === 'image' || t === 'font') return route.abort();
      if (AD_HOST_RE.test(host)) return route.abort();
      return route.continue();
    });
    // Load with the antena-1 referer (the player page may expect it).
    await page.goto(SOURCE_PAGE, { waitUntil: 'domcontentloaded', timeout: NAV_TIMEOUT_MS, referer: SOURCE_REFERER }).catch(() => {});

    // Poke the player to start (it won't fetch the stream until it begins playing).
    // Repeat every few seconds — the player iframe may still be loading at first.
    const deadline = Date.now() + NAV_TIMEOUT_MS;
    let lastClick = 0;
    while (!captured && Date.now() < deadline) {
      await page.waitForTimeout(700);
      if (!captured && Date.now() - lastClick > 3500) { lastClick = Date.now(); await tryPlay(page); }
    }

    if (!captured) await dumpDiagnostics(page, candidates, hosts);
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
