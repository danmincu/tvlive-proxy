// rdslive-resolver
// --------------------------------------------------------------------------
// Auto-discovers the rotating HLS playlist URL from the public player page and
// pushes it to rdslive-proxy's /admin/source, so the source stays fresh with no
// manual pasting.
//
// The player only fetches the stream if the browser has real H.264/AAC codecs, so
// we drive REAL Google Chrome (channel:'chrome'), not Playwright's codec-less
// Chromium. We also stub window.open (releases the player's ad gate) and click the
// player center (clearing ad overlays) to start playback, then capture the URL.
//
// Trigger: reactive (polls /health; resolves when down/stalled) + periodic safety.
// --------------------------------------------------------------------------
import { chromium } from 'playwright-extra';
import stealth from 'puppeteer-extra-plugin-stealth';

chromium.use(stealth());
process.on('unhandledRejection', (e) => console.log('[resolver] unhandledRejection:', e && e.message));
process.on('uncaughtException', (e) => console.log('[resolver] uncaughtException:', e && e.message));

const PROXY_BASE   = process.env.PROXY_BASE        || 'http://rdslive-proxy:13001';
const ADMIN_TOKEN  = process.env.PROXY_ADMIN_TOKEN || '';
const SOURCE_PAGE  = process.env.SOURCE_PAGE       || 'https://rdslive.org/antena-1/';
const ORIGIN       = process.env.PROXY_ORIGIN      || 'https://canale-tv.net';
const REFERER      = process.env.PROXY_REFERER     || 'https://canale-tv.net/';
const UA = process.env.PROXY_UA ||
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36';

const POLL_SECONDS     = +(process.env.RESOLVE_POLL_SECONDS     || 30);
const PERIODIC_HOURS   = +(process.env.RESOLVE_PERIODIC_HOURS   || 4);
const MIN_INTERVAL_SEC = +(process.env.RESOLVE_MIN_INTERVAL_SEC || 120);
const NAV_TIMEOUT_MS   = +(process.env.RESOLVE_NAV_TIMEOUT_MS   || 45000);
const HEADLESS         = /^(1|true|yes)$/i.test(process.env.HEADLESS || ''); // default headed (under xvfb)
const FORCE_CAPTURE    = /^(1|true|yes)$/i.test(process.env.RESOLVE_FORCE_CAPTURE || ''); // skip probes, always run Chrome (for testing)

const log = (...a) => console.log(new Date().toISOString(), '[resolver]', ...a);

let busy = false;
let lastResolveAt = 0;

// Drive real Chrome to the player, start playback, capture the rotating URL.
// Prefers the "...-got.htm" media playlist (what the proxy/DVR expect) over the
// bare ".m3u8" master.
async function captureUrl() {
  const browser = await chromium.launch({
    channel: 'chrome', // real Chrome = has H.264/AAC; codec-less Chromium fails here
    headless: HEADLESS,
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled', '--autoplay-policy=no-user-gesture-required'],
  });
  try {
    const ctx = await browser.newContext({ userAgent: UA, viewport: { width: 1280, height: 720 } });
    // Stub window.open: the player's ad SDK needs it to return a window object, else
    // it locks the player behind a "correct iframe setting" message.
    await ctx.addInitScript(() => {
      try { const s = { closed: false, close() {}, focus() {}, blur() {}, postMessage() {}, location: { href: '' }, document: { write() {}, close() {} } }; window.open = () => s; } catch (e) {}
    });

    let best = null, alt = null;
    ctx.on('request', (r) => {
      const u = r.url();
      if (/-got\.htm/i.test(u)) best = best || u;
      else if (/\.m3u8(\?|$)/i.test(u)) alt = alt || u;
    });

    const page = await ctx.newPage();
    await page.goto(SOURCE_PAGE, { waitUntil: 'domcontentloaded', timeout: NAV_TIMEOUT_MS, referer: 'https://rdslive.org/' }).catch(() => {});
    await page.waitForTimeout(5000);
    if (page.url().includes('#google_vignette')) { await page.goBack().catch(() => {}); await page.waitForTimeout(1500); }

    const cf = page.frames().find((fr) => /canale-tv|tv\.php/i.test(fr.url()));
    if (cf) {
      try {
        const fe = await cf.frameElement();
        await fe.scrollIntoViewIfNeeded().catch(() => {});
        const box = await fe.boundingBox();
        if (box) {
          const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
          for (let k = 0; k < 8 && !best; k++) {
            // Peel ad/clickjack overlays sitting over the player, then click it.
            await page.evaluate(({ cx, cy }) => {
              for (let n = 0; n < 10; n++) {
                const el = document.elementFromPoint(cx, cy);
                if (!el || el === document.body) break;
                if (el.tagName === 'IFRAME' && /tv\.php|rdslive/i.test(el.src || '')) break;
                el.style.setProperty('display', 'none', 'important');
              }
            }, { cx, cy }).catch(() => {});
            await page.mouse.click(cx, cy);
            await page.waitForTimeout(2500);
          }
        }
      } catch (e) { log('click err:', e?.message?.split('\n')[0]); }
    }
    // brief grace so the preferred -got.htm arrives alongside the .m3u8
    if (!best && alt) await page.waitForTimeout(1500);
    return best || alt;
  } finally {
    await browser.close().catch(() => {});
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Fetch a playlist with the upstream headers; return its text iff it's HLS, else null.
async function fetchPlaylist(url) {
  try {
    const r = await fetch(url, { headers: { accept: '*/*', origin: ORIGIN, referer: REFERER, 'user-agent': UA }, signal: AbortSignal.timeout(8000) });
    if (!r.ok) return null;
    const t = await r.text();
    return t.replace(/^﻿/, '').trimStart().startsWith('#EXTM3U') ? t : null;
  } catch { return null; }
}

async function verify(url) { return (await fetchPlaylist(url)) !== null; }

// Fetch the playlist's first segment and confirm it's real MPEG-TS (0x47 sync byte).
// This catches the common stall where the PLAYLIST advances but the SEGMENTS are dead
// (403/expired) — which a playlist-only check would wrongly call "live".
async function segmentOk(playlistUrl, text) {
  const seg = text.split('\n').map((l) => l.trim()).find((l) => l && !l.startsWith('#'));
  if (!seg) return false;
  let segUrl;
  try { segUrl = new URL(seg, playlistUrl).href; } catch { return false; }
  try {
    const r = await fetch(segUrl, { headers: { accept: '*/*', origin: ORIGIN, referer: REFERER, 'user-agent': UA }, signal: AbortSignal.timeout(8000) });
    if (!r.ok || !r.body) return false;
    const reader = r.body.getReader();
    const { value } = await reader.read();
    reader.cancel().catch(() => {});
    return !!value && value.length > 0 && value[0] === 0x47;
  } catch { return false; }
}

// Is this URL FULLY working — advancing playlist AND fetchable MPEG-TS segments?
async function probeLive(url) {
  const t1 = await fetchPlaylist(url);
  if (!t1) return false;
  if (!(await segmentOk(url, t1))) return false; // playlist ok but segments dead -> not live
  const m1 = t1.match(/#EXT-X-MEDIA-SEQUENCE:(\d+)/);
  if (!m1) return true; // valid + segments ok, can't measure advance -> accept
  await sleep(3000);
  const t2 = await fetchPlaylist(url);
  if (!t2) return false;
  const m2 = t2.match(/#EXT-X-MEDIA-SEQUENCE:(\d+)/);
  return !!m2 && parseInt(m2[1], 10) > parseInt(m1[1], 10);
}

// {current, recent[]} from the proxy — recent is the persisted pool of known-good hosts.
async function proxyGetState() {
  try {
    const r = await fetch(PROXY_BASE + '/admin/source', { headers: { 'X-Admin-Token': ADMIN_TOKEN } });
    if (!r.ok) return { current: null, recent: [] };
    const j = await r.json();
    return { current: j.current || null, recent: Array.isArray(j.recent) ? j.recent : [] };
  } catch { return { current: null, recent: [] }; }
}

async function proxyPush(url) {
  const r = await fetch(PROXY_BASE + '/admin/source', { method: 'POST', headers: { 'X-Admin-Token': ADMIN_TOKEN, 'Content-Type': 'text/plain' }, body: url });
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
    const { current, recent } = await proxyGetState();
    const isStall = /stall|down/i.test(reason);

    if (FORCE_CAPTURE) {
      log('FORCE_CAPTURE set — skipping probes, running browser capture');
    } else {
      // On a real stall the proxy can't record from `current`, so DON'T trust it —
      // go find a different working host. On periodic/startup, if `current` is fully
      // working (playlist advancing AND segments fetchable) there's nothing to do.
      if (!isStall && current && await probeLive(current)) { log('current source still live, nothing to do'); return; }

      // Try the known-good host pool (same path/token, provider just rotates the host).
      // A cheap HTTP probe — no browser. First fully-working one wins.
      for (const u of recent) {
        if (u === current) continue;
        if (await probeLive(u)) {
          log('recovered via cached host:', u);
          log(await proxyPush(u) ? 'pushed (cached host)' : 'push FAILED');
          return;
        }
      }
      log('no cached host is live; running browser capture');
    }

    // Full browser capture (token likely rotated, or forced).
    const url = await captureUrl();
    if (!url) { log('no playlist URL captured'); return; }
    log('captured:', url);
    if (!(await verify(url))) { log('verification failed (not an HLS playlist), ignoring'); return; }
    if (current === url) { log('unchanged, nothing to do'); return; }
    log(await proxyPush(url) ? 'recovered via browser capture: ' + url : 'push FAILED');
  } catch (e) {
    log('resolve error:', e?.message || e);
  } finally {
    busy = false;
  }
}

async function main() {
  if (!ADMIN_TOKEN) { log('FATAL: PROXY_ADMIN_TOKEN is not set'); process.exit(1); }
  log('started. page=', SOURCE_PAGE, 'proxy=', PROXY_BASE, 'headless=', HEADLESS);
  await resolve('startup');
  setInterval(async () => { if (await proxyDown()) await resolve('stalled/down'); }, POLL_SECONDS * 1000);
  setInterval(() => resolve('periodic'), PERIODIC_HOURS * 3600 * 1000);
}

main();
