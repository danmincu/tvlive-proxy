# rdslive-resolver

Headless service that auto-discovers the **rotating** HLS playlist URL from the public
player page and pushes it to `rdslive-proxy`, so the source stays fresh without anyone
pasting URLs by hand.

## How it works

1. Polls the proxy's `GET /health`. When the stream is **stalled/down** (or on a periodic
   timer) it triggers a resolution.
2. **Probes the known-host pool first** (`recent` from `/admin/source`): the provider rotates
   the host but keeps the same path/token, so a sibling host is usually already live. A cheap
   HTTP probe (`probeLive` — fetches twice, confirms the media-sequence advances) picks a live
   one and pushes it — no browser. Only if none are live does it fall to the full capture:
3. Launches **real Google Chrome** (`channel: 'chrome'`), loads `SOURCE_PAGE`, stubs
   `window.open`, dismisses the vignette ad, peels ad overlays off the player and clicks it
   to start playback, and captures the stream request the player fires (prefers the
   `…-got.htm` media playlist over the bare `.m3u8`).
4. **Verifies** the URL returns `#EXTM3U` (with the canale-tv headers), then `POST`s it to
   the proxy's token-gated `/admin/source`. The proxy no-ops if unchanged.

### Why real Chrome (not the bundled Chromium)

The player checks for H.264/AAC codec support and shows *"Your browser does not support the
playback of this video"* — never fetching the stream — on Playwright's bundled Chromium
(which lacks proprietary codecs). Driving **real Chrome** (installed via
`npx playwright install chrome` in the Dockerfile) provides the codecs, so the player plays
and the URL can be captured. The container runs Chrome **headed under Xvfb**.

Other gotchas that are handled: the player's "correct iframe setting" lock is released by
stubbing `window.open` (its ad SDK needs a window object); a Google vignette interstitial and
a clickjack ad overlay sit over the play button (dismissed/peeled before clicking).

## Config (env)

| var | default | meaning |
|-----|---------|---------|
| `PROXY_BASE` | `http://rdslive-proxy:13001` | proxy base URL (internal network) |
| `PROXY_ADMIN_TOKEN` | — | **required**; shared secret matching the proxy |
| `SOURCE_PAGE` | `https://rdslive.org/antena-1/` | page to resolve from |
| `HEADLESS` | unset (headed) | set `1` to force headless (may be detected) |
| `RESOLVE_POLL_SECONDS` | `30` | how often to check `/health` |
| `RESOLVE_PERIODIC_HOURS` | `4` | proactive re-resolve interval |
| `RESOLVE_MIN_INTERVAL_SEC` | `120` | never resolve more often than this |
| `RESOLVE_FORCE_CAPTURE` | unset | set `1` to skip the host-pool probe and always run Chrome (testing) |
| `PROXY_ORIGIN` / `PROXY_REFERER` | canale-tv.net | headers used to verify the URL |

## Caveats

- Scraping is inherently maintenance-prone: if the player/ad flow changes substantially, the
  click/capture steps may need tuning. The manual fallback always works — copy the URL from
  DevTools and paste it into the player (password `bibita`).
- The image is large (Playwright + real Chrome). First build downloads Chrome.
