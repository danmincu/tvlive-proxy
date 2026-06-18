# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-project .NET 10 ASP.NET Core minimal-API app (`rdslive-proxy`) that acts as an HLS proxy **and** an inline web player. Its job is to let a browser or VLC play an HLS live stream whose origin requires specific browser headers (Origin/Referer/sec-* etc.) and serves segments under relative names. The entire app is `Program.cs` (~250 lines, top-level statements); there is no test project.

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
./run.sh      # docker compose up -d --build  (detached, restart=unless-stopped)
./stop.sh     # docker compose down
```

There are no tests or linters. Verify changes by running the app and hitting the endpoints (the dev box already has .NET; on Windows the process locks `bin/.../rdslive-proxy.exe`, so `taskkill //F //IM rdslive-proxy.exe` before rebuilding while an instance is running).

## Architecture

Four routes in `Program.cs`, plus shared state:

- **`StreamState`** (bottom of file) — a single, process-global, lock-guarded holder for the *currently active* upstream playlist URL and its derived `BaseUrl` (everything up to the last `/`). There is **one** active stream shared by all visitors; whoever POSTs `/set` changes it for everyone. This is intentional (it's how a second visitor inherits the first visitor's URL).
- **`GET /`** — serves the self-contained HTML player (`IndexPage`). The upstream URL input is pre-filled with the current `StreamState.PlaylistUrl`, and the page auto-starts playback if one is set.
- **`POST /set`** — body is the raw URL; validates it's absolute http(s), then `StreamState.Set(url)`.
- **`GET /stream.m3u8`** — Call 1: fetches the upstream playlist with the injected headers and returns it **verbatim** as `application/vnd.apple.mpegurl`.
- **`GET /{*path}`** — Call 2: catch-all that proxies any other path to `BaseUrl + path`, streaming bytes straight through.

**The relative-URL mechanism is the crux:** the upstream playlist lists segments as bare relative names (e.g. `tokenizedXXXX.html`). Because the playlist is served from `/stream.m3u8`, the player resolves those names against `http://<host>:13001/`, so segment requests land on the `/{*path}` catch-all, which re-prepends the real upstream base. Do not rewrite segment URLs in the playlist — the verbatim pass-through is what makes this work for both browser and VLC.

**Header injection:** `AddBrowserHeaders` applies the exact header set the origin expects to every upstream request (both calls). These come from the original `curl` capture; changing/removing them will likely break upstream fetches with 403s.

**Browser playback:** raw HLS doesn't play natively in Chrome/Firefox, so `IndexPage` loads **hls.js** from a CDN and falls back to native HLS on Safari.

## Configuration (env vars, all optional)

- `PROXY_PLAYLIST_URL` — initial/default upstream URL (CLI arg takes precedence; a hard-coded default exists in `Program.cs` if neither is set).
- `PROXY_ORIGIN` / `PROXY_REFERER` — the Origin/Referer headers sent upstream.
- `PROXY_PORT` (default 13001) and `PROXY_BIND` (default `0.0.0.0` — required for the container to be reachable; set `127.0.0.1` to restrict to localhost).

## Conventions

- Keep everything in `Program.cs`; this is deliberately a single-file app.
- The HTML player is an interpolated raw string literal (`$$"""..."""`) inside `IndexPage` — mind the `{{ }}` escaping for literal braces in the embedded CSS/JS.
- `.dockerignore` excludes `bin/`/`obj/` so the image always builds from clean source — never rely on host build artifacts leaking into the container.
