# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-project .NET 10 ASP.NET Core minimal-API app (`rdslive-proxy`) that acts as an HLS proxy **and** an inline web player. Its job is to let a browser or VLC play an HLS live stream whose origin requires specific browser headers (Origin/Referer/sec-* etc.) and serves segments under relative names. It also Chromecasts the stream and runs a rolling **DVR** (records the active stream to disk for timeshift/seek and `.ts` export). The proxy itself is one file, `Program.cs` (~900 lines, top-level statements); there is no test project. A second container under `resolver/` (Node + Playwright) auto-discovers the rotating upstream URL (see "The resolver service" below).

## Commands

Local dev (requires .NET 10 SDK):
```bash
dotnet run                 # uses the built-in default playlist URL
dotnet run -- "<playlist-url>"   # override the upstream URL
dotnet build               # compile only
```

Container (host needs **only Docker**, never .NET — the SDK lives in the build stage):
```bash
./build.sh    # docker compose build --pull --no-cache
./run.sh      # primary antena-1 stack on 13001/13443, 24h DVR (detached, restart=unless-stopped)
./stop.sh     # docker compose down (the primary stack)
```

**Multiple channels:** `run.sh`/`stop.sh` are parameterized so each channel runs as its own
Compose stack (own ports, own DVR volume `rdslive-<name>_dvr-data`, own resolver `SOURCE_PAGE`).
The bare `./run.sh` is the **primary** stack (project = directory default, container `rdslive-proxy`,
volume unchanged) so an existing install is updated in place, not duplicated. Any deviation
(`--source`/`--name`/non-default ports) makes a namespaced stack `rdslive-<name>`.
```bash
./run.sh --source https://rdslive.org/antena-3-cnn/ --http 14001 --https 14443 --dvr-hours 6
./stop.sh antena-3-cnn          # stop that channel (name = last path segment of --source, or --name)
./run.sh --help                 # all flags: --source --http --https --dvr-hours --ip --name
```
`run.sh` exports `SOURCE_PAGE`, `PROXY_PORT`, `PROXY_HTTPS_PORT`, `PROXY_DVR_HOURS`,
`PROXY_CAST_HOST`, plus `COMPOSE_PROJECT_NAME`/`CHANNEL_SUFFIX` (empty for primary) which
`docker-compose.yml` consumes. The proxy listens on `PROXY_PORT`/`PROXY_HTTPS_PORT` inside the
container (must match the published ports); the resolver reaches it at `rdslive-proxy:$PROXY_PORT`
on the per-stack internal network. A fresh channel briefly has the stale antena-1 default URL
until its resolver discovers the right one (the recorder rejects the dead default, recording nothing
until then — so no wrong-channel content is stored).

There are no tests or linters. Verify changes by running the app and hitting the endpoints (the dev box already has .NET; on Windows the process locks `bin/.../rdslive-proxy.exe`, so `taskkill //F //IM rdslive-proxy.exe` before rebuilding while an instance is running).

## Architecture

Routes in `Program.cs`, plus shared state (`StreamState`, `DvrStore` at the bottom of the file):

- **`StreamState`** — a single, process-global, lock-guarded holder for the *currently active* upstream playlist URL and its derived `BaseUrl` (everything up to the last `/`). There is **one** active stream shared by all visitors; whoever POSTs `/set` changes it for everyone. This is intentional (it's how a second visitor inherits the first visitor's URL).
- **`GET /`** — serves the self-contained HTML player (`IndexPage`). URL input pre-filled with the current `StreamState.PlaylistUrl`; auto-starts if one is set. ONE unified timeline (`/dvr.m3u8` as a live playlist): starts at the live edge, scrub back the full window, `● Live` jumps to the edge, `±1m/±5m` skip buttons, Chromecast button, `.ts` download link.
- **`POST /set`** — manual URL override from the player's OK button. **Password-protected** (header `X-Set-Password` == `PROXY_DVR_WIPE_PASSWORD`, default `bibita`) since the resolver auto-heals the URL — a wrong/absent password → 403. Body is the raw URL; validates absolute http(s), then `StreamState.Set(url)`. The UI prompts for the password on OK and rolls the field back to the live `currentUrl` (from `/health`) on failure.
- **`GET /stream.m3u8`** — Call 1, the "live" playlist. **When DVR is on, this serves the local buffer at the live edge** (the newest `PROXY_LIVE_SEGMENTS` segments of the current session, via `BuildDvrPlaylist`, no ENDLIST), NOT the upstream — so every viewer is served locally and the provider only ever sees the single recorder ingest (see Fan-out below). Only on cold start (buffer empty) or with DVR disabled does it fall back to fetching the upstream playlist **verbatim** and proxying segments via the catch-all.
- **`GET /{*path}`** — Call 2: catch-all that proxies any other path to `BaseUrl + path`, streaming bytes through. Rewrites `text/html` segment content-types to `video/mp2t` (the origin disguises MPEG-TS segments as `.html`) so the Chromecast receiver will demux them; hls.js doesn't care.
- **DVR routes** — `GET /dvr.m3u8` (generated timeshift playlist from recorded segments; `?hours=N` bounds the window, `?vod=1` closes it with ENDLIST), `GET /dvr/seg/{id}.ts` (serve a recorded segment), `GET /dvr/export.ts` (concatenate the recorded window into one downloadable MPEG-TS; `?from=&to=` are unix-ms bounds), `GET /dvr/status` (buffer stats JSON), `POST /dvr/clear` (permanently wipe ALL recordings — body is the confirmation password `PROXY_DVR_WIPE_PASSWORD`, default `bibita`; the UI "Wipe" button warns + prompts for it).
- **Admin routes** (`GET`/`POST /admin/source`) — token-gated (`X-Admin-Token` == `PROXY_ADMIN_TOKEN`; 404 if the token is unset). `POST` updates the active source URL (validates, dedupes against current, `state.Set` + wakes recorder) and remembers it in the persisted pool. `GET` returns `{current, recent}` where `recent` is the pool of recently-good URLs. Used by the resolver service.

**Source persistence:** good source URLs (via `/set` or `/admin/source`) are kept in a most-recent-first pool persisted to `<PROXY_DVR_DIR>/sources.txt` (on the DVR volume). On startup the newest is pre-loaded (CLI arg / `PROXY_PLAYLIST_URL` still take precedence) so a restart resumes on the last-good URL instead of the stale hard-coded default. The resolver reads `recent` to probe known hosts before doing a browser capture.
- **`GET /health`** — upstream health JSON (`ok`, `stalled`, `error`, `failures`, `lastOkUtc`, `lastSegmentUtc`) from `StreamHealth`, which both the recorder and `/stream.m3u8` feed. `ok` is false when the recorder is failing **or** `stalled` (no new segment for `PROXY_STALL_SECONDS` — catches a valid-but-frozen upstream playlist that would otherwise look healthy). The player polls it, shows a red banner when down/stalled, and auto-resumes playback when health recovers.

**The relative-URL mechanism is the crux:** the upstream playlist lists segments as bare relative names (e.g. `tokenizedXXXX.html`). Because the playlist is served from `/stream.m3u8`, the player resolves those names against `http://<host>:13001/`, so segment requests land on the `/{*path}` catch-all, which re-prepends the real upstream base. Do not rewrite segment URLs in the playlist — the verbatim pass-through is what makes this work for both browser and VLC.

**Header injection:** `AddBrowserHeaders` applies the exact header set the origin expects to every upstream request (all upstream fetches go through `SendUpstreamAsync`). These come from the original `curl` capture; changing/removing them will likely break upstream fetches with 403s.

**Resilience:** `SendUpstreamAsync` wraps every upstream GET with a per-attempt timeout (`PROXY_TIMEOUT_SECONDS`) and bounded retry+backoff (`PROXY_RETRIES`) on transient failures (timeouts, connection errors, 5xx/408/429) — only before any bytes stream to the client. The shared `HttpClient` has an infinite global timeout because timeouts are managed per-attempt.

**HTTPS + Chromecast:** Kestrel listens on http (`PROXY_PORT`, media/VLC/Chromecast) and https (`PROXY_HTTPS_PORT`, default 13443) with a runtime-generated self-signed cert. The Cast SDK only initializes in a secure context (https or localhost), so the player page must be opened via https for the cast button to appear. The Chromecast can't validate the self-signed cert, so the page hands it the **plain-http** media URL on the proxy's LAN IP (`PROXY_CAST_HOST`; `run.sh` auto-detects it) — the cast device fetches over the LAN directly, no router port-forward needed. CORS is enabled app-wide because the Cast receiver fetches via XHR.

**DVR:** `IngestLoopAsync` (started for the app lifetime when `PROXY_DVR_ENABLED`) polls the active playlist every `PROXY_DVR_POLL_SECONDS`, downloads each new segment (tracked by upstream `EXT-X-MEDIA-SEQUENCE`) to `<PROXY_DVR_DIR>/seg/<id>.ts`, and appends to `DvrStore`. Recording **follows the active `/set` stream** — switching channels (or a restart/gap) marks an `EXT-X-DISCONTINUITY` on the next stored segment. `DvrStore` keeps an in-memory index (source of truth) plus an append-only `index.log` for restart recovery; a janitor prunes segments past `PROXY_DVR_HOURS`, and as a secondary safety net also prunes the oldest segments when free disk drops below `PROXY_DVR_MIN_FREE_GB` (`DvrStore.PruneToFreeSpace`, which finds the correct filesystem via the longest-matching `DriveInfo` mount). Stored segment ids are our own monotonic counter (upstream sequence resets across channels); each segment also carries a `Session` id that bumps on every channel change. NOTE: recordings must live on a persistent volume in Docker (compose mounts `dvr-data:/app/dvr`).

**Fan-out (the reason DVR is always-on by default):** the recorder is the *only* component that fetches from the provider. All live viewers are served `/stream.m3u8` from the local buffer at the live edge (`DvrStore.LiveWindow` returns the newest N segments, NOT filtered by session — so the window slides smoothly with contiguous `MEDIA-SEQUENCE` + a `#EXT-X-DISCONTINUITY` across a channel/URL switch; the live edge is always the newest segment so it still shows the new channel immediately. Session-filtering caused an abrupt window reset + sequence jump that stopped the Chromecast receiver mid-cast). So 1 viewer or 100, the provider only ever sees one ingest — this keeps upstream traffic/attention minimal. Trade-off: viewers are ~20-40s behind true-live (recorder lag + player buffer). `PROXY_LIVE_SEGMENTS` sizes the live window. The catch-all upstream segment proxy is then only used during cold-start/DVR-off passthrough.

**Bad-source handling:** if the provider changes/kills the URL, the recorder does NOT record junk and never crashes. It validates the playlist (`LooksLikeHlsPlaylist` — must start with `#EXTM3U`, so a 200-OK HTML landing/error page is rejected) and validates each segment is real MPEG-TS (first byte `0x47`, else skip + discontinuity). The recorder polls with a short timeout / few attempts (so a dead URL fails fast and the loop stays responsive), backs off up to 30s when failing, and `POST /set` releases `recorderWake` to interrupt the backoff so a freshly-pasted URL is picked up immediately. Health is surfaced via `/health` and the UI banner.

**Browser playback (unified live+DVR timeline):** raw HLS doesn't play natively in Chrome/Firefox, so `IndexPage` loads **hls.js** (CDN) and falls back to native HLS on Safari. There is now ONE source for the player: **`/dvr.m3u8`** — the full DVR window served as a *live* playlist (no ENDLIST). It starts at the live edge, and the user can scrub back the whole window or press `● Live`. The key is hls.js config **`liveMaxLatencyDurationCount: Infinity`** (+ `backBufferLength: Infinity`): without it hls.js force-snaps a far-behind playhead to the live edge on every reload (that's why scrubbing used to fail); with it you can rewind freely on a live playlist — and because it's live (not VOD) there's no "end" to loop at. `chooseSrc()` falls back to `/stream.m3u8` (light live edge) when DVR is off or cold-starting (0 segments). The playlists emit `#EXT-X-PROGRAM-DATE-TIME` so the player has `hls.playingDate` (wall-clock of the playhead). The stall-watchdog and health auto-resume only fire **at the live edge** (`atLiveEdge()`), so a scrubbed-back viewer is never yanked forward.

**Resume (per-browser localStorage, `rdslive:resume`):** `saveResume()` continuously persists the **wall-clock instant** being watched (`hls.playingDate`, robust to the DVR window sliding) plus an `atLive` flag, on a 5s interval and on `pause`/`visibilitychange`/`pagehide`. On load `bootstrap()` reads it: if you were *live* (or the save is >24h old) it starts at the live edge; if you were *scrubbed back* it `applyRestore()`s — waits for `hls.playingDate`, then `seekToDate()` maps the saved wall-clock to a `currentTime` and seeks there. This is what lets a refresh keep your place mid-game instead of jumping to the live edge (and spoiling a score).

**Cast follows the browser:** the cast button casts the light `/stream.m3u8` (LIVE) at the live edge, or `/dvr.m3u8?vod=1` (VOD) at `currentTime = hls.playingDate − fromUtc` when scrubbed. As the user scrubs/skips, `scheduleCastSync` → `syncCast` mirrors the new position to the cast session via the Cast SDK `RemotePlayerController` (a `seek()` within the loaded VOD — smooth, no reload), and switches media only when crossing live↔history or scrubbing past the loaded snapshot. (HLS servers can't move a receiver's playhead by changing a per-session playlist — the browser-as-sender controlling the receiver is the correct mechanism.) **On cast reconnect** (the SDK auto-rejoins via `ORIGIN_SCOPED` after the browser slept): if a scrubbed-back resume is in progress (`restorePending`), the `SESSION_RESUMED` handler calls `castDvrAt(savedDateMs)` to snap the TV straight to the saved instant instead of the live edge — so a refresh re-establishes control AND keeps the TV behind live (no score spoiler).

## Configuration (env vars, all optional)

- `PROXY_PLAYLIST_URL` — initial/default upstream URL (CLI arg takes precedence; a hard-coded default exists in `Program.cs` if neither is set).
- `PROXY_ORIGIN` / `PROXY_REFERER` — the Origin/Referer headers sent upstream.
- `PROXY_PORT` (default 13001) and `PROXY_BIND` (default `0.0.0.0` — required for the container to be reachable; set `127.0.0.1` to restrict to localhost).
- `PROXY_HTTPS_PORT` (default 13443) — https port (self-signed) for the player page / Chromecast.
- `PROXY_CAST_HOST` — the proxy's LAN IP, used as the Chromecast media host **only when the page is opened on the LAN** (private IP/localhost). When the page is opened via a **public/DDNS** host, the cast uses that public host instead (so a Chromecast in another house can reach the proxy over the internet — this requires the http port, e.g. 13001, to be **port-forwarded**, since the Chromecast can't validate the self-signed 13443 cert). `run.sh` auto-detects the LAN IP.
- `PROXY_TIMEOUT_SECONDS` (default 15), `PROXY_RETRIES` (default 2) — upstream resilience.
- `PROXY_DVR_ENABLED` (default true), `PROXY_DVR_DIR` (default `dvr`), `PROXY_DVR_HOURS` (default 24), `PROXY_DVR_POLL_SECONDS` (default 4) — DVR recording.
- `PROXY_DVR_MIN_FREE_GB` (default 10) — secondary safety net: the janitor also prunes the oldest segments whenever free disk on the DVR volume drops below this, independent of the time window (0 disables).
- `PROXY_LIVE_SEGMENTS` (default 8, min 3) — size of the live-edge window served to all viewers from the local buffer (fan-out). Smaller = closer to live but more stall-prone.
- `PROXY_STALL_SECONDS` (default 45, min 15) — if no new segment is recorded for this long while a stream is set, `/health` reports `ok:false, stalled:true`. Catches a provider that returns a valid-but-frozen playlist (e.g. expired token), which `StreamHealth.Ok` alone would miss.
- `PROXY_DVR_WIPE_PASSWORD` (default `bibita`) — confirmation password for `POST /dvr/clear` / the UI "Wipe" button.
- `PROXY_ADMIN_TOKEN` — shared secret enabling the `/admin/source` routes for the resolver. Unset → routes 404. Reachable on the public 13443 port, so set a strong value.

## The resolver service (`resolver/`)

A **separate** Node + Playwright (stealth) container (`docker-compose` service `resolver`, image from `resolver/Dockerfile`) that auto-discovers the **rotating** upstream playlist URL. The provider changes the host (sometimes token) every few hours; a human would otherwise have to open the ad-heavy player page, hit play, and read the `…-got.htm` request off DevTools. The resolver does that automatically: polls the proxy's `/health`, and when stalled/down (or on a periodic timer) first **probes the known-good host pool** (`recent` from `/admin/source`) with a cheap HTTP check (the provider rotates the host but keeps the same path/token, so a sibling host is usually already live — no browser needed). Only if none are live does it drive **real Google Chrome** (Playwright `channel: 'chrome'`, headed under Xvfb) to `SOURCE_PAGE`, start playback, capture the stream request (prefers the `…-got.htm` media playlist), verify it returns `#EXTM3U`, and `POST` it to `/admin/source`. `probeLive` fetches the playlist twice and confirms the media-sequence advances, so it never picks a frozen host. It's deliberately separate so heavy/crash-prone Chrome can't take down the streaming proxy. See `resolver/README.md`. KEY: it must use **real Chrome** (has H.264/AAC) — the player refuses to fetch the stream on Playwright's codec-less bundled Chromium ("browser does not support playback"). It also stubs `window.open` (releases the player's ad gate) and peels ad overlays off the play button. Maintenance-prone if the player/ad flow changes; manual paste (password `bibita`) is the fallback.

Other long-run robustness: the shared `HttpClient` uses `SocketsHttpHandler` with `PooledConnectionLifetime = 2min` so the recorder can't pin a stale CDN edge (a cause of multi-hour freezes); and the player runs a client-side stall watchdog that reloads the source if playback stops advancing for ~15s.

## Conventions

- Keep everything in `Program.cs`; this is deliberately a single-file app.
- The HTML player is an interpolated raw string literal (`$$"""..."""`) inside `IndexPage` — mind the `{{ }}` escaping for literal braces in the embedded CSS/JS.
- `.dockerignore` excludes `bin/`/`obj/` so the image always builds from clean source — never rely on host build artifacts leaking into the container.
