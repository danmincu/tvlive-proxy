# rdslive-resolver

Headless (stealth) Chromium that discovers the **rotating** HLS playlist URL from the
public player page and pushes it to `rdslive-proxy` so the source stays fresh without
anyone pasting URLs by hand.

## How it works

1. Polls the proxy's `GET /health`. When the stream is **stalled/down** (or on a
   periodic safety timer), it triggers a resolution.
2. Launches headless Chromium, loads `SOURCE_PAGE`, and captures the first network
   request matching `RESOLVE_PATTERN` (default `*.cfd/...-got.htm`) — across the page
   and any ad popups. If nothing fires within a few seconds it tries common play
   buttons (a real gesture).
3. **Verifies** the captured URL really returns an HLS playlist (`#EXTM3U`) using the
   `canale-tv.net` origin/referer headers, then `POST`s it to the proxy's
   `/admin/source` (token-gated). The proxy no-ops if the URL is unchanged.

It runs only when needed (min-interval guard) and closes the browser after each run,
so idle memory is low.

## Config (env)

| var | default | meaning |
|-----|---------|---------|
| `PROXY_BASE` | `http://rdslive-proxy:13001` | proxy base URL (internal network) |
| `PROXY_ADMIN_TOKEN` | — | **required**; shared secret matching the proxy |
| `SOURCE_PAGE` | `https://rdslive.org/antena-1/` | page to resolve from |
| `RESOLVE_PATTERN` | `\.cfd/.*-got\.htm` | regex for the playlist request URL |
| `RESOLVE_POLL_SECONDS` | `30` | how often to check `/health` |
| `RESOLVE_PERIODIC_HOURS` | `4` | proactive re-resolve interval |
| `RESOLVE_MIN_INTERVAL_SEC` | `120` | never resolve more often than this |
| `PROXY_ORIGIN` / `PROXY_REFERER` | canale-tv.net | headers used to verify the URL |

## Caveats / tuning

- The page may bot-detect or show a Cloudflare challenge; stealth helps but isn't
  guaranteed. If capture fails, grab a **HAR** of the manual flow (DevTools → Network →
  load page → play → save all as HAR) and tune `RESOLVE_PATTERN` / the play selectors.
- Scrapers are inherently maintenance-prone: if the site/ad flow changes, the capture
  step may need updating.
