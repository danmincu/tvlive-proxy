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

async function verify(url) {
  try {
    const r = await fetch(url, { headers: { accept: '*/*', origin: ORIGIN, referer: REFERER, 'user-agent': UA }, signal: AbortSignal.timeout(15000) });
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
  log('started. page=', SOURCE_PAGE, 'proxy=', PROXY_BASE, 'headless=', HEADLESS);
  await resolve('startup');
  setInterval(async () => { if (await proxyDown()) await resolve('stalled/down'); }, POLL_SECONDS * 1000);
  setInterval(() => resolve('periodic'), PERIODIC_HOURS * 3600 * 1000);
}

main();
