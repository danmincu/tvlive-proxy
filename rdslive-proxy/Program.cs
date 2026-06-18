using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

// ---------------------------------------------------------------------------
// rdslive-proxy
//
// A tiny HLS proxy + web player.
//
//   * Open http://localhost:13001/ in a browser, paste the upstream playlist
//     URL, hit OK, and the video plays inline (via hls.js).
//   * Or point VLC at http://localhost:13001/stream.m3u8
//
// How it proxies:
//   Call 1 (playlist): we fetch the upstream .m3u8 with the browser headers the
//   origin expects, and serve it at /stream.m3u8. Its segments are listed as
//   *relative* names (tokenizedXXXX.html), so the player requests them back at
//   http://localhost:13001/tokenizedXXXX.html ...
//   Call 2 (segment): ...which we proxy to <upstream base>/tokenizedXXXX.html,
//   again injecting headers, and stream the bytes straight through.
//
// Usage:
//     dotnet run                  # then set the URL in the browser
//     dotnet run -- "<playlist-url>"   # pre-load a URL (also works for VLC)
//
// Optional overrides (env vars):
//     PROXY_ORIGIN   default: https://canale-tv.net
//     PROXY_REFERER  default: https://canale-tv.net/
//     PROXY_PORT     default: 13001
// ---------------------------------------------------------------------------

string origin = Environment.GetEnvironmentVariable("PROXY_ORIGIN") ?? "https://canale-tv.net";
string referer = Environment.GetEnvironmentVariable("PROXY_REFERER") ?? "https://canale-tv.net/";
int port = int.TryParse(Environment.GetEnvironmentVariable("PROXY_PORT"), out var p) ? p : 13001;
// HTTPS port: needed because Chrome's Cast SDK only initializes in a *secure
// context* (https:// or localhost). A plain-http LAN IP is not secure, so the
// cast button never appears. We serve both: http for media/VLC/Chromecast, and
// https (self-signed) for the player page so the cast button works.
int httpsPort = int.TryParse(Environment.GetEnvironmentVariable("PROXY_HTTPS_PORT"), out var hp) ? hp : 13443;
// Optional: the LAN IP/host the Chromecast should use to fetch media. Leave unset
// to use the browser's own hostname (works when opening the player via the LAN IP);
// set to e.g. "192.168.1.54" when reaching the player via a DDNS/public hostname.
string castHost = Environment.GetEnvironmentVariable("PROXY_CAST_HOST") ?? "";

// Resilience: how long to wait for an upstream response before giving up on an
// attempt, and how many extra attempts to make on transient failures. These guard
// the *connect + headers* phase; once bytes are streaming we can't retry. Tuned
// short because this is a live stream — the player (hls.js) also retries itself.
TimeSpan upstreamTimeout = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("PROXY_TIMEOUT_SECONDS"), out var ts) ? ts : 15);
int maxAttempts = 1 + (int.TryParse(Environment.GetEnvironmentVariable("PROXY_RETRIES"), out var rt) ? Math.Max(0, rt) : 2);

// DVR: continuously record the active stream to disk and keep a rolling window so
// the player can seek back / export the last N hours as a single .ts. Recording
// FOLLOWS the active /set stream — switching channels marks a discontinuity.
bool dvrEnabled = (Environment.GetEnvironmentVariable("PROXY_DVR_ENABLED") ?? "true")
    is "1" or "true" or "yes" or "TRUE";
string dvrDir = Environment.GetEnvironmentVariable("PROXY_DVR_DIR") ?? "dvr";
TimeSpan dvrRetention = TimeSpan.FromHours(
    double.TryParse(Environment.GetEnvironmentVariable("PROXY_DVR_HOURS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dh) ? dh : 24);
TimeSpan dvrPoll = TimeSpan.FromSeconds(
    double.TryParse(Environment.GetEnvironmentVariable("PROXY_DVR_POLL_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dp) ? dp : 4);
// Secondary safety net: if free disk space drops below this many GB, prune the
// oldest segments until back above it — regardless of the 24h retention. Guards
// against a full disk if the bitrate spikes or something else eats the volume.
// Set to 0 to disable.
double dvrMinFreeGb = double.TryParse(Environment.GetEnvironmentVariable("PROXY_DVR_MIN_FREE_GB"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mf) ? mf : 10;
long dvrMinFreeBytes = (long)(dvrMinFreeGb * 1024 * 1024 * 1024);
// Live fan-out: when DVR is on, /stream.m3u8 serves the newest N recorded segments
// (the "live edge") from the local buffer, so every viewer is served locally and the
// provider only ever sees the single recorder ingest. Bigger = more delay but safer
// against stalls. Minimum 3.
int liveSegments = Math.Max(3, int.TryParse(Environment.GetEnvironmentVariable("PROXY_LIVE_SEGMENTS"), out var ls) ? ls : 8);
// If no new segment has been recorded for this long while a stream is set, the live
// edge is "stalled" — the source may return a valid-but-frozen playlist (e.g. an
// expired token), which otherwise looks healthy. We surface this so the UI can warn.
int stallSeconds = Math.Max(15, int.TryParse(Environment.GetEnvironmentVariable("PROXY_STALL_SECONDS"), out var st) ? st : 45);
// Password required to permanently wipe all DVR recordings (POST /dvr/clear).
string wipePassword = Environment.GetEnvironmentVariable("PROXY_DVR_WIPE_PASSWORD") ?? "bibita";
// Shared secret for the auto-resolver to update the source URL (POST /admin/source).
// If unset, the admin endpoints are disabled (404).
string adminToken = Environment.GetEnvironmentVariable("PROXY_ADMIN_TOKEN") ?? "";

// The currently-selected upstream stream. Mutated when the user submits the
// form (POST /set), or pre-loaded from the CLI arg / env var below.
var state = new StreamState();
string initialUrl =
    args.FirstOrDefault(a => !a.StartsWith('-'))
    ?? Environment.GetEnvironmentVariable("PROXY_PLAYLIST_URL")
    ?? "https://alpha1.yosefina1.cfd/ah1/usergenx304Jtlrnd2-got.htm";
state.Set(initialUrl);

// Tracks whether the upstream is currently serving a valid stream, so we can warn
// the user (and stop recording junk) if the provider changes/kills the URL.
var health = new StreamHealth();

// Lets POST /set interrupt the recorder's backoff sleep so a freshly-pasted URL is
// picked up immediately instead of after the (possibly long) backoff delay.
var recorderWake = new SemaphoreSlim(0, 1);

// Bind address: 0.0.0.0 so it's reachable on the LAN (required for casting and
// for the container). Override with PROXY_BIND (e.g. "127.0.0.1").
string bind = Environment.GetEnvironmentVariable("PROXY_BIND") ?? "0.0.0.0";
IPAddress bindAddr = IPAddress.Parse(bind);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

// Generate a self-signed cert in-process (no cert files needed in the image).
// The browser will warn once; clicking through still yields a secure context,
// which is all the Cast SDK needs. Chromecast itself never sees this cert — we
// point its media fetch at the plain-http endpoint (see the cast code).
var cert = BuildSelfSignedCert();

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(bindAddr, port);                                  // http  — media, VLC, Chromecast
    options.Listen(bindAddr, httpsPort, lo => lo.UseHttps(cert));    // https — player page / cast button
});

// One shared HttpClient. Automatic decompression so we hand the player plain bytes.
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = true,
    // Recycle pooled connections every couple of minutes so the recorder doesn't pin
    // a stale Cloudflare edge over long runtimes (a cause of multi-hour freezes); also
    // lets DNS changes (the provider rotates edges) take effect.
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
})
{
    // We manage timeouts per-attempt via a CancellationToken (see SendUpstreamAsync),
    // so disable the global 100s timeout that would otherwise also cap body streaming.
    Timeout = Timeout.InfiniteTimeSpan,
});

// The Chromecast default receiver fetches the manifest/segments via XHR and
// requires CORS headers (a same-origin browser does not). Allow everything.
builder.Services.AddCors();

var app = builder.Build();
var log = app.Logger;

app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// The rolling DVR store (segment files + in-memory/​on-disk index). Loaded from
// disk on startup so recordings survive restarts; old segments are pruned past
// the retention window.
var dvr = new DvrStore(dvrDir, dvrRetention, log);
dvr.Load();

// Build a throwaway self-signed cert for the https endpoint. Export+reimport as
// PFX so Kestrel reliably gets the private key (notably on Windows).
static X509Certificate2 BuildSelfSignedCert()
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=rdslive-proxy", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    req.CertificateExtensions.Add(san.Build());
    req.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
    using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
}

// Apply the exact browser headers from the curl examples to an upstream request.
void AddBrowserHeaders(HttpRequestMessage req)
{
    req.Headers.TryAddWithoutValidation("accept", "*/*");
    req.Headers.TryAddWithoutValidation("accept-language", "en-CA,en-GB;q=0.9,en-US;q=0.8,en;q=0.7");
    req.Headers.TryAddWithoutValidation("origin", origin);
    req.Headers.TryAddWithoutValidation("priority", "u=1, i");
    req.Headers.TryAddWithoutValidation("referer", referer);
    req.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Google Chrome\";v=\"149\", \"Chromium\";v=\"149\", \"Not)A;Brand\";v=\"24\"");
    req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
    req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
    req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
    req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
    req.Headers.TryAddWithoutValidation("sec-fetch-site", "cross-site");
    req.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");
}

// Send an upstream GET (headers only) with a per-attempt timeout and bounded
// retry+backoff on transient failures: our timeout, connection errors, and
// 5xx/408/429 responses. NOT retried: the client going away, or definitive
// statuses like 403/404. Retries always happen before we stream any bytes to the
// client, so they're safe. Returns the response (which may carry an error status)
// or null if every attempt failed to get a response at all.
// perAttemptTimeout/attempts override the global defaults — the recorder uses short,
// few attempts so a dead URL fails fast (it re-polls anyway); on-demand client
// requests use the longer defaults.
async Task<HttpResponseMessage?> SendUpstreamAsync(HttpClient http, string url, string label, CancellationToken ct,
    TimeSpan? perAttemptTimeout = null, int? attempts = null)
{
    static bool IsTransient(HttpStatusCode s) =>
        (int)s >= 500 || s == HttpStatusCode.RequestTimeout || s == HttpStatusCode.TooManyRequests;

    var timeout = perAttemptTimeout ?? upstreamTimeout;
    int max = attempts ?? maxAttempts;

    for (int attempt = 1; ; attempt++)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req);

        try
        {
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!IsTransient(resp.StatusCode) || attempt >= max)
                return resp;
            log.LogWarning("{Label}: upstream {Status} (attempt {Attempt}/{Max}); retrying",
                label, (int)resp.StatusCode, attempt, max);
            resp.Dispose();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the client disconnected — stop, let it bubble up
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // our per-attempt timeout, or a connection-level error
            if (attempt >= max)
            {
                log.LogWarning("{Label}: failed after {Max} attempts ({Msg})", label, max, ex.Message);
                return null;
            }
            log.LogWarning("{Label}: {Msg} (attempt {Attempt}/{Max}); retrying", label, ex.Message, attempt, max);
        }

        // Exponential backoff: 200ms, 400ms, 800ms, ...
        await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);
    }
}

// A real HLS playlist begins with the #EXTM3U tag (after any BOM/whitespace).
// Anything else served at the playlist URL (e.g. an HTML landing/error page) means
// the source is broken — don't parse it as segments.
static bool LooksLikeHlsPlaylist(string body) =>
    body.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal);

// Build an HLS media playlist from recorded segments, pointing at the local
// /dvr/seg/<id>.ts files. endlist=false => live (player tracks the edge); true =>
// closed VOD (fully seekable, no refresh).
static string BuildDvrPlaylist(DvrSeg[] segs, bool endlist)
{
    var sb = new StringBuilder();
    sb.Append("#EXTM3U\n#EXT-X-VERSION:6\n");
    double maxDur = 6;
    foreach (var s in segs) if (s.Dur > maxDur) maxDur = s.Dur;
    sb.Append("#EXT-X-TARGETDURATION:").Append((int)Math.Ceiling(maxDur)).Append('\n');
    sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(segs.Length > 0 ? segs[0].Id : 0).Append('\n');
    if (endlist) sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
    foreach (var s in segs)
    {
        if (s.Disc) sb.Append("#EXT-X-DISCONTINUITY\n");
        sb.Append("#EXTINF:").Append(s.Dur.ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("/dvr/seg/").Append(s.Id.ToString("D12")).Append(".ts\n");
    }
    if (endlist) sb.Append("#EXT-X-ENDLIST\n");
    return sb.ToString();
}

// Download one segment's bytes (with retries). Returns null on failure. Bounded
// timeout so a stuck segment can't block the recorder loop for long.
async Task<byte[]?> DownloadSegmentAsync(HttpClient http, string url, CancellationToken ct)
{
    using var resp = await SendUpstreamAsync(http, url, "DVR segment", ct, TimeSpan.FromSeconds(10), 2);
    if (resp is null || !resp.IsSuccessStatusCode) return null;
    return await resp.Content.ReadAsByteArrayAsync(ct);
}

// The recorder loop: poll the active playlist, store every new segment to disk,
// prune past the retention window. Runs for the app's lifetime when DVR is on.
async Task IngestLoopAsync(CancellationToken ct)
{
    var http = app.Services.GetRequiredService<HttpClient>();
    string? channel = null;     // current upstream playlist URL we're recording
    long lastSeq = -1;          // last upstream media-sequence number stored
    bool pendingDisc = false;   // mark the next stored segment as a discontinuity
    long session = dvr.MaxSession(); // bumped on each channel change (resumes above disk)
    var lastPrune = DateTime.UtcNow;

    log.LogInformation("DVR: recording enabled, dir='{Dir}', retention={Hours}h, poll={Poll}s",
        dvr.Dir, dvrRetention.TotalHours, dvrPoll.TotalSeconds);

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var (playlistUrl, baseUrl) = state.Snapshot();
            if (playlistUrl != channel)
            {
                channel = playlistUrl;
                lastSeq = -1;
                pendingDisc = true; // channel switch -> discontinuity in the recording
                session++;          // new channel -> new session (live edge shows only this)
                if (channel is not null) log.LogInformation("DVR: now recording {Url}", channel);
            }
            if (playlistUrl is null || baseUrl is null)
            {
                await Task.Delay(dvrPoll, ct);
                continue;
            }

            // Short timeout / few attempts so a dead URL fails fast and the loop stays
            // responsive to channel changes (the loop re-polls regardless).
            using (var resp = await SendUpstreamAsync(http, playlistUrl, "DVR playlist", ct, TimeSpan.FromSeconds(6), 2))
            {
                string? text = resp is not null && resp.IsSuccessStatusCode
                    ? await resp.Content.ReadAsStringAsync(ct)
                    : null;

                if (text is null)
                {
                    health.Fail(resp is null ? "upstream unreachable" : $"upstream returned HTTP {(int)resp.StatusCode}");
                }
                else if (!LooksLikeHlsPlaylist(text))
                {
                    // 200 OK but not an HLS playlist — the provider likely repurposed
                    // the URL (landing/error page). Do NOT parse it as segments.
                    health.Fail("not a valid HLS playlist (the source URL may have changed)");
                }
                else
                {
                    health.Ok();
                    long mediaSeq = 0;
                    double pendingDur = 0;
                    bool extDisc = false;
                    int idx = 0;

                    foreach (var raw in text.Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0) continue;
                        if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
                        {
                            long.TryParse(line.AsSpan(22).Trim(), out mediaSeq);
                            continue;
                        }
                        if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE")) continue;
                        if (line == "#EXT-X-DISCONTINUITY") { extDisc = true; continue; }
                        if (line.StartsWith("#EXTINF:"))
                        {
                            var v = line.AsSpan(8);
                            int comma = v.IndexOf(',');
                            if (comma >= 0) v = v[..comma];
                            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDur);
                            continue;
                        }
                        if (line.StartsWith('#')) continue;

                        // A segment URI.
                        long seq = mediaSeq + idx;
                        idx++;
                        if (seq <= lastSeq) { extDisc = false; pendingDur = 0; continue; }

                        bool gap = lastSeq >= 0 && seq > lastSeq + 1; // we missed some -> discontinuity
                        string segUrl = Uri.TryCreate(line, UriKind.Absolute, out var abs) ? abs.ToString() : baseUrl + line;

                        var bytes = await DownloadSegmentAsync(http, segUrl, ct);
                        // Only store real MPEG-TS (starts with the 0x47 sync byte). A
                        // token-expired/replaced URL often returns a 200 HTML error page;
                        // don't record that as video. Treat as a hole + discontinuity.
                        if (bytes is null || bytes.Length == 0 || bytes[0] != 0x47)
                        {
                            if (bytes is { Length: > 0 })
                                log.LogWarning("DVR: segment not MPEG-TS, skipping: {Url}", segUrl);
                            lastSeq = seq;
                            pendingDisc = true;
                            extDisc = false; pendingDur = 0;
                            continue;
                        }

                        long id = dvr.Reserve();
                        await File.WriteAllBytesAsync(dvr.PathFor(id), bytes, ct);
                        dvr.Add(new DvrSeg(id, pendingDur > 0 ? pendingDur : 6.0, DateTime.UtcNow,
                            pendingDisc || extDisc || gap, bytes.LongLength, session));
                        health.Segment();

                        lastSeq = seq;
                        pendingDisc = false; extDisc = false; pendingDur = 0;
                    }
                }
            }

            if (DateTime.UtcNow - lastPrune > TimeSpan.FromSeconds(60))
            {
                int removed = dvr.PruneExpired(DateTime.UtcNow);
                if (removed > 0) log.LogInformation("DVR: pruned {N} expired segments", removed);
                if (dvrMinFreeBytes > 0)
                {
                    int freedSegs = dvr.PruneToFreeSpace(dvrMinFreeBytes);
                    if (freedSegs > 0)
                        log.LogWarning("DVR: low disk - pruned {N} oldest segments to keep >{Gb:0.#}GB free", freedSegs, dvrMinFreeGb);
                }
                lastPrune = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        catch (Exception ex) { health.Fail(ex.Message); log.LogWarning("DVR loop error: {Msg}", ex.Message); }

        // Poll normally when healthy; back off up to 30s when the source is down so
        // we don't hammer a dead URL or spam the log. POST /set wakes us early so a
        // newly-pasted URL is picked up at once.
        int fails = health.Snapshot().Failures;
        var delay = fails == 0
            ? dvrPoll
            : TimeSpan.FromSeconds(Math.Min(30, dvrPoll.TotalSeconds * Math.Pow(2, Math.Min(fails, 4))));
        await recorderWake.WaitAsync(delay, ct);
    }
}

// The web UI: a URL box + an HLS video player.
app.MapGet("/", (HttpResponse response) =>
{
    // Cast media host: by default the browser uses its own address bar host
    // (window.location.hostname) — correct when you open the player via the LAN IP.
    // For DDNS/remote access the Chromecast can't reach the public hostname, so set
    // PROXY_CAST_HOST to the proxy's LAN IP (e.g. 192.168.1.54) to override it.
    response.ContentType = "text/html; charset=utf-8";
    return response.WriteAsync(IndexPage(state.PlaylistUrl, port, castHost));
});

// Set / change the active upstream URL. Body is the raw URL (text/plain).
app.MapPost("/set", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    string url = (await reader.ReadToEndAsync()).Trim();
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        return Results.BadRequest("Provide an absolute http(s) URL.");

    state.Set(url);
    log.LogInformation("Active stream set -> {Url}", url);
    if (recorderWake.CurrentCount == 0) recorderWake.Release(); // wake the recorder now
    return Results.NoContent();
});

// Call 1: the "live" playlist, served to the player / VLC.
//
// Fan-out control: when DVR is recording, we serve EVERY live viewer from the local
// buffer at the live edge (newest segments of the current channel) instead of
// proxying the upstream playlist + segments per client. The provider therefore only
// ever sees the single recorder ingest, regardless of how many people are watching.
// Viewers sit a little behind true-live (recorder lag + player buffer). Cold start or
// DVR disabled falls back to the verbatim upstream passthrough below.
app.MapGet("/stream.m3u8", async (HttpResponse response, HttpClient http, CancellationToken ct) =>
{
    var (playlistUrl, _) = state.Snapshot();
    if (playlistUrl is null)
    {
        response.StatusCode = 409; // nothing selected yet
        await response.WriteAsync("No stream set. Open http://localhost:" + port + "/ and paste a URL.", ct);
        return;
    }

    if (dvrEnabled)
    {
        var live = dvr.LiveWindow(liveSegments);
        if (live.Length > 0)
        {
            response.ContentType = "application/vnd.apple.mpegurl";
            response.Headers.CacheControl = "no-cache, no-store";
            await response.WriteAsync(BuildDvrPlaylist(live, endlist: false), ct);
            return;
        }
        // Buffer empty (cold start / just switched) — fall through to passthrough
        // just for this brief gap until the recorder has stored a segment.
    }

    log.LogInformation("Playlist (passthrough) -> {Url}", playlistUrl);

    using var upstream = await SendUpstreamAsync(http, playlistUrl, "Playlist", ct);
    if (upstream is null)
    {
        health.Fail("upstream unreachable");
        response.StatusCode = StatusCodes.Status502BadGateway;
        await response.WriteAsync("Upstream playlist unreachable after retries.", ct);
        return;
    }
    if (!upstream.IsSuccessStatusCode)
    {
        health.Fail($"upstream returned HTTP {(int)upstream.StatusCode}");
        log.LogWarning("Playlist upstream returned {Status}", (int)upstream.StatusCode);
        response.StatusCode = (int)upstream.StatusCode;
        return;
    }

    var body = await upstream.Content.ReadAsStringAsync(ct);
    if (!LooksLikeHlsPlaylist(body))
    {
        // 200 OK but not HLS — the source URL was likely repurposed. Don't hand the
        // player a garbage "playlist"; signal failure so the UI can explain.
        health.Fail("not a valid HLS playlist (the source URL may have changed)");
        response.StatusCode = StatusCodes.Status502BadGateway;
        await response.WriteAsync("The source did not return a valid HLS playlist.", ct);
        return;
    }

    // Return the playlist verbatim — its relative segment names route back here.
    health.Ok();
    response.StatusCode = 200;
    response.ContentType = "application/vnd.apple.mpegurl";
    response.Headers.CacheControl = "no-cache, no-store";
    await response.WriteAsync(body, ct);
});

// ---- DVR -----------------------------------------------------------------
// These explicit routes take precedence over the catch-all below.

// DVR timeshift playlist: the whole retained window as a live playlist (no
// ENDLIST), so the player's seek bar spans the buffer and can ride to the live
// edge. ?hours=N bounds the window (smaller manifest); ?vod=1 closes it (ENDLIST).
app.MapGet("/dvr.m3u8", (HttpResponse response, HttpRequest request) =>
{
    var segs = dvr.Snapshot();
    if (double.TryParse(request.Query["hours"], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(h);
        segs = Array.FindAll(segs, s => s.Utc >= cutoff);
    }
    bool vod = request.Query["vod"] == "1";

    response.ContentType = "application/vnd.apple.mpegurl";
    response.Headers.CacheControl = "no-cache, no-store";
    return response.WriteAsync(BuildDvrPlaylist(segs, endlist: vod));
});

// Serve a recorded segment from disk.
app.MapGet("/dvr/seg/{name}", (string name) =>
{
    if (!Regex.IsMatch(name, @"^\d{1,15}\.ts$")) return Results.NotFound();   // guard path traversal
    var path = dvr.PathForName(name);
    return File.Exists(path)
        ? Results.File(path, "video/mp2t", enableRangeProcessing: true)
        : Results.NotFound();
});

// Export the recorded window as a single downloadable MPEG-TS (concatenated
// segments). ?from=&to= are unix-ms bounds; omit for the whole buffer.
app.MapGet("/dvr/export.ts", async (HttpResponse response, HttpRequest request, CancellationToken ct) =>
{
    var segs = dvr.Snapshot();
    if (long.TryParse(request.Query["from"], out var fromMs))
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime;
        segs = Array.FindAll(segs, s => s.Utc >= from);
    }
    if (long.TryParse(request.Query["to"], out var toMs))
    {
        var to = DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime;
        segs = Array.FindAll(segs, s => s.Utc <= to);
    }

    response.ContentType = "video/mp2t";
    response.Headers["Content-Disposition"] = "attachment; filename=\"dvr.ts\"";
    var buffer = new byte[64 * 1024];
    foreach (var s in segs)
    {
        var path = dvr.PathFor(s.Id);
        if (!File.Exists(path)) continue; // pruned mid-export — skip
        try
        {
            await using var fs = File.OpenRead(path);
            int n;
            while ((n = await fs.ReadAsync(buffer, ct)) > 0)
                await response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            break; // client went away or read error — stop
        }
    }
});

// DVR buffer stats for the UI.
app.MapGet("/dvr/status", () =>
{
    var (count, seconds, from, to, bytes) = dvr.Stats();
    return Results.Json(new
    {
        enabled = dvrEnabled,
        segments = count,
        hours = Math.Round(seconds / 3600.0, 2),
        fromUtc = from,
        toUtc = to,
        bytes,
    });
});

// ---- Admin: source control for the auto-resolver -------------------------
// Token-gated (header X-Admin-Token). Disabled with 404 unless PROXY_ADMIN_TOKEN
// is set. The resolver reaches these over the internal docker network.
app.MapGet("/admin/source", (HttpRequest request) =>
{
    if (adminToken.Length == 0 || request.Headers["X-Admin-Token"].ToString() != adminToken)
        return Results.NotFound();
    var (url, _) = state.Snapshot();
    return Results.Json(new { current = url });
});

app.MapPost("/admin/source", async (HttpRequest request) =>
{
    if (adminToken.Length == 0 || request.Headers["X-Admin-Token"].ToString() != adminToken)
        return Results.NotFound();
    using var reader = new StreamReader(request.Body);
    string url = (await reader.ReadToEndAsync()).Trim();
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        return Results.BadRequest("Provide an absolute http(s) URL.");

    var (current, _) = state.Snapshot();
    if (current == url)
        return Results.Json(new { updated = false, current = url });

    state.Set(url);
    if (recorderWake.CurrentCount == 0) recorderWake.Release(); // pick up the new URL now
    log.LogInformation("Source auto-updated by resolver -> {Url}", url);
    return Results.Json(new { updated = true, current = url });
});

// Permanently wipe ALL DVR recordings. Body is the confirmation password (text/plain).
app.MapPost("/dvr/clear", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    string pw = (await reader.ReadToEndAsync()).Trim();
    if (pw != wipePassword)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    int removed = dvr.Clear();
    log.LogWarning("DVR: ALL recordings wiped via /dvr/clear ({N} segments)", removed);
    return Results.Json(new { cleared = removed });
});

// Upstream health, for the UI to show a friendly banner when the source is down.
app.MapGet("/health", () =>
{
    var h = health.Snapshot();
    // "Stalled" = the recorder is fetching a playlist that looks valid but isn't
    // producing new segments (e.g. an expired provider token returning a frozen
    // playlist). h.Ok alone would miss this, so the live edge would silently freeze.
    bool stalled = dvrEnabled
        && h.LastSegmentUtc is { } seg
        && (DateTime.UtcNow - seg) > TimeSpan.FromSeconds(stallSeconds);
    bool ok = h.Ok && !stalled;
    string? error = stalled
        ? "no new video from the source for a while - the stream or your link may have stopped"
        : h.Error;
    return Results.Json(new
    {
        ok,
        stalled,
        hasStream = state.PlaylistUrl is not null,
        error,
        failures = h.Failures,
        lastOkUtc = h.LastOkUtc,
        lastSegmentUtc = h.LastSegmentUtc,
    });
});

// Call 2: every segment (or sub-playlist) the player requests by relative name.
app.MapGet("/{*path}", async (string path, HttpResponse response, HttpClient http, CancellationToken ct) =>
{
    var (_, baseUrl) = state.Snapshot();
    if (string.IsNullOrEmpty(path) || baseUrl is null)
    {
        response.StatusCode = 404;
        return;
    }

    string target = baseUrl + path;
    log.LogInformation("Segment   -> {Url}", target);

    using var upstream = await SendUpstreamAsync(http, target, "Segment " + path, ct);
    if (upstream is null)
    {
        response.StatusCode = StatusCodes.Status502BadGateway;
        return;
    }
    response.StatusCode = (int)upstream.StatusCode;

    // The origin serves MPEG-TS segments disguised as .html with Content-Type
    // text/html. hls.js ignores that and parses the bytes, but the Chromecast
    // receiver won't demux a text/html response — so correct it to video/mp2t.
    // (Sub-playlists, if any, keep the HLS type.)
    string? upstreamType = upstream.Content.Headers.ContentType?.ToString();
    if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        response.ContentType = "application/vnd.apple.mpegurl";
    else if (string.IsNullOrEmpty(upstreamType) || upstreamType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        response.ContentType = "video/mp2t";
    else
        response.ContentType = upstreamType;
    response.Headers.CacheControl = "no-cache, no-store";

    if (!upstream.IsSuccessStatusCode)
    {
        log.LogWarning("Segment upstream returned {Status} for {Path}", (int)upstream.StatusCode, path);
        return;
    }

    // Stream the bytes straight through to the player. If the upstream connection
    // breaks mid-segment we can't retry (bytes are already flowing) — just log it;
    // the player's own retry will re-request the fragment.
    try
    {
        await upstream.Content.CopyToAsync(response.Body, ct);
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
    {
        log.LogWarning("Segment stream interrupted for {Path}: {Msg}", path, ex.Message);
    }
});

// Start the DVR recorder for the app's lifetime (fire-and-forget; it stops when
// ApplicationStopping fires).
if (dvrEnabled)
    _ = IngestLoopAsync(app.Lifetime.ApplicationStopping);

log.LogInformation("rdslive-proxy listening (bind {Bind})", bind);
log.LogInformation("Player (http):  http://localhost:{Port}/", port);
log.LogInformation("Player (https): https://localhost:{HttpsPort}/   <- open via LAN IP for Chromecast", httpsPort);
log.LogInformation("VLC / m3u8:     http://localhost:{Port}/stream.m3u8", port);
log.LogInformation("DVR timeshift:  http://localhost:{Port}/dvr.m3u8   (recording {On})", port, dvrEnabled ? "ON" : "OFF");
if (state.PlaylistUrl is { } u)
    log.LogInformation("Pre-loaded:     {Url}", u);

app.Run();


// The single-page player. hls.js drives playback in Chrome/Firefox; Safari
// falls back to the browser's native HLS support.
static string IndexPage(string? current, int httpPort, string castHost) => $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>rdslive-proxy</title>
<style>
  /* Flex column layout: header (wraps freely) + status + video that fills the rest,
     so there's no magic-number video height to break when controls wrap. */
  html, body { height:100%; }
  body { margin:0; background:#111; color:#eee; font:14px system-ui, sans-serif; display:flex; flex-direction:column; }
  /* Two groups (URL+OK, and the control buttons). On desktop they sit on one row;
     on phones each group becomes a full-width row, so nothing gets squeezed. */
  header { display:flex; flex-wrap:wrap; align-items:center; gap:8px; padding:8px 10px; background:#1b1b1b; border-bottom:1px solid #333; }
  .grp { display:flex; align-items:center; gap:8px; }
  .grp.url { flex:1 1 300px; min-width:0; }
  .grp.ctl { flex:0 1 auto; }
  /* font-size:16px on the input stops iOS from zooming in on focus. */
  input { flex:1 1 auto; min-width:0; padding:10px; border:1px solid #444; border-radius:6px; background:#222; color:#eee; font-size:16px; }
  button, a.dl { padding:10px 14px; border:0; border-radius:6px; background:#2d7; color:#000; font-weight:600; font-size:14px; line-height:1; cursor:pointer; white-space:nowrap; text-align:center; display:inline-flex; align-items:center; justify-content:center; }
  button.alt, a.dl { background:#345; color:#eee; }
  button.danger { background:#a33; color:#fff; }
  a.dl { text-decoration:none; }
  button.active { outline:2px solid #2d7; outline-offset:-2px; }
  google-cast-launcher { width:38px; height:38px; flex:0 0 auto; cursor:pointer; --connected-color:#2d7; --disconnected-color:#eee; }
  #status { padding:4px 10px; color:#999; font-size:12px; min-height:16px; }
  #banner { display:none; padding:10px 12px; background:#5a1f1f; color:#ffd9d9; border-bottom:1px solid #803030; font-size:14px; }
  #banner.show { display:block; }
  video { flex:1 1 auto; width:100%; min-height:0; background:#000; display:block; }

  /* Phones: stack the two groups into full-width rows; the control buttons share
     their row and grow for comfortable tap targets. */
  @media (max-width:600px) {
    .grp.url, .grp.ctl { flex:1 1 100%; }
    .grp.ctl > button, .grp.ctl > a.dl { flex:1 1 auto; min-height:44px; }
  }
</style>
</head>
<body>
  <header>
    <div class="grp url">
      <input id="url" placeholder="Paste playlist URL…"
             value="{{System.Net.WebUtility.HtmlEncode(current ?? "")}}"
             onkeydown="if(event.key==='Enter')load()">
      <button onclick="load()">OK</button>
    </div>
    <div class="grp ctl">
      <button id="btnLive" class="alt" onclick="goLive()" title="Live edge">Live</button>
      <button id="btnDvr" class="alt" onclick="goDvr()" title="Timeshift / DVR — scrub back through the recorded window">DVR</button>
      <button class="alt" onclick="skip(-10)" title="Back 10 seconds">&#9194;10</button>
      <button class="alt" onclick="skip(10)" title="Forward 10 seconds">10&#9193;</button>
      <a class="dl" href="/dvr/export.ts" title="Download the recorded buffer as one .ts file">&#8595; .ts</a>
      <button class="danger" onclick="wipeDvr()" title="Permanently delete ALL recordings">Wipe</button>
      <google-cast-launcher id="castbtn" title="Cast to a Chromecast (open this page via your LAN IP, not localhost)"></google-cast-launcher>
    </div>
  </header>
  <div id="banner"></div>
  <div id="status"></div>
  <video id="v" controls autoplay playsinline></video>

  <!-- Cast SDK calls this when the framework is ready; must exist before the SDK loads. -->
  <script>
    window['__onGCastApiAvailable'] = function (isAvailable) {
      window.__castApiAvailable = isAvailable;
      if (window.initCast) window.initCast();
    };
  </script>
  <script src="https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1"></script>
  <script src="https://cdn.jsdelivr.net/npm/hls.js@1"></script>
  <script>
    var v = document.getElementById('v');
    var statusEl = document.getElementById('status');
    var hls = null;

    function setStatus(t) { statusEl.textContent = t; }

    // --- Chromecast ----------------------------------------------------------
    // hls.js plays via MSE, which Chrome won't cast. So we use the Cast SDK to
    // hand the Chromecast our /stream.m3u8 URL directly; its built-in receiver
    // plays HLS, fetching segments through this proxy (same as VLC does).
    var castContext = null;

    window.initCast = function () {
      if (castContext || !window.__castApiAvailable) return;
      castContext = cast.framework.CastContext.getInstance();
      castContext.setOptions({
        receiverApplicationId: chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
        autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED
      });
      castContext.addEventListener(
        cast.framework.CastContextEventType.SESSION_STATE_CHANGED,
        function (e) {
          if (e.sessionState === cast.framework.SessionState.SESSION_STARTED ||
              e.sessionState === cast.framework.SessionState.SESSION_RESUMED) {
            castLoad();
          }
        }
      );
    };

    function isCasting() {
      return castContext && castContext.getCurrentSession();
    }

    function castLoad() {
      var session = isCasting();
      if (!session) return;
      // The Chromecast can't validate our self-signed cert, so always hand it the
      // plain-http endpoint. Prefer the server's LAN IP (injected) since the
      // Chromecast is local; fall back to the browser's hostname (localhost case).
      var castHost = "{{castHost}}" || window.location.hostname;
      var castBase = 'http://' + castHost + ':' + {{httpPort}};
      // Cast whatever is currently playing (live or the DVR timeshift playlist).
      var url = castBase + currentPath + (currentPath.indexOf('?') >= 0 ? '&' : '?') + 't=' + Date.now();
      var info = new chrome.cast.media.MediaInfo(url, 'application/x-mpegurl');
      info.streamType = chrome.cast.media.StreamType.LIVE;
      // The segments are MPEG-TS; tell the receiver so it doesn't have to guess.
      if (chrome.cast.media.HlsSegmentFormat) info.hlsSegmentFormat = chrome.cast.media.HlsSegmentFormat.TS;
      if (chrome.cast.media.HlsVideoSegmentFormat) info.hlsVideoSegmentFormat = chrome.cast.media.HlsVideoSegmentFormat.MPEG2_TS;
      session.loadMedia(new chrome.cast.media.LoadRequest(info)).then(
        function () { setStatus('Casting to ' + session.getCastDevice().friendlyName); },
        function (err) { setStatus('Cast failed: ' + JSON.stringify(err)); }
      );
    }
    // -------------------------------------------------------------------------

    // The source currently loaded into the player: live ('/stream.m3u8') or the
    // DVR timeshift playlist ('/dvr.m3u8'). Cast and reload follow this.
    var currentPath = '/stream.m3u8';

    function play(path, startPos) {
      currentPath = path;
      var live = path.indexOf('/dvr.m3u8') < 0;
      var seekTo = (!live && typeof startPos === 'number' && startPos > 0) ? startPos : -1;
      document.getElementById('btnLive').classList.toggle('active', live);
      document.getElementById('btnDvr').classList.toggle('active', !live);
      var src = path + (path.indexOf('?') >= 0 ? '&' : '?') + 't=' + Date.now();
      if (window.Hls && Hls.isSupported()) {
        if (hls) hls.destroy();
        var cfg = { liveSyncDuration: 12 };
        if (seekTo > 0) cfg.startPosition = seekTo;   // open DVR at the resume/live-edge point
        hls = new Hls(cfg);
        hls.loadSource(src);
        hls.attachMedia(v);
        hls.on(Hls.Events.MANIFEST_PARSED, function () { v.play(); setStatus(live ? 'Playing (live)' : 'Playing (DVR — seek anywhere; re-click DVR for newer)'); });
        hls.on(Hls.Events.ERROR, function (e, d) {
          if (d.fatal) { setStatus('Error: ' + d.type + ' / ' + d.details); refreshHealth(); }
        });
      } else {
        // Safari / native HLS
        v.src = src;
        if (seekTo > 0) {
          v.addEventListener('loadedmetadata', function once() { v.removeEventListener('loadedmetadata', once); try { v.currentTime = seekTo; } catch (e) {} });
        }
        v.play();
        setStatus(live ? 'Playing (native HLS)' : 'Playing (DVR, native)');
      }
    }

    function isDvr() { return currentPath.indexOf('/dvr.m3u8') >= 0; }

    // For resume: remember the wallclock instant we're watching in the DVR (not a raw
    // offset, which shifts as old segments are pruned). Keyed per stream URL per browser.
    var dvrFromMs = 0;
    function dvrKey() { return 'dvrResume:' + ((document.getElementById('url').value || '').trim() || 'default'); }

    function saveDvrPos() {
      if (!isDvr() || !dvrFromMs) return;
      try { localStorage.setItem(dvrKey(), String(Math.round(dvrFromMs + v.currentTime * 1000))); } catch (e) {}
    }

    function start()  { play('/stream.m3u8'); }
    function goLive() { play('/stream.m3u8'); if (isCasting()) castLoad(); }

    // Load DVR as VOD (ENDLIST) so it's a fixed, fully-seekable recording — hls.js
    // won't reload it or snap to the live edge. Open at the saved spot for this browser,
    // else at the live edge (most recent). Re-click DVR to pull in newer recordings.
    function goDvr() {
      fetch('/dvr/status').then(function (r) { return r.json(); }).then(function (s) {
        var start = -1;
        if (s.fromUtc && s.toUtc) {
          var from = Date.parse(s.fromUtc), to = Date.parse(s.toUtc);
          dvrFromMs = from;
          var saved = parseFloat(localStorage.getItem(dvrKey()) || '');
          var targetMs = (saved && saved > from + 1000 && saved < to - 1000) ? saved : (to - 15000);
          start = Math.max(0, (targetMs - from) / 1000);
        }
        play('/dvr.m3u8?vod=1', start);
        if (isCasting()) castLoad();
      }).catch(function () { play('/dvr.m3u8?vod=1', -1); });
    }

    // Skip ±N seconds, clamped to the buffer's seekable range.
    function skip(delta) {
      var t = v.currentTime + delta;
      try {
        if (v.seekable && v.seekable.length) {
          var s = v.seekable.start(0), e = v.seekable.end(v.seekable.length - 1);
          t = Math.min(Math.max(t, s), e);
        } else { t = Math.max(0, t); }
      } catch (err) { t = Math.max(0, t); }
      v.currentTime = t;
      if (v.paused) v.play();
    }

    function load() {
      var url = document.getElementById('url').value.trim();
      if (!url) return;
      setStatus('Loading...');
      fetch('/set', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: url })
        .then(function (r) {
          if (!r.ok) return r.text().then(function (t) { throw new Error(t || ('HTTP ' + r.status)); });
          start();
          if (isCasting()) castLoad();   // push the new stream to an active cast too
        })
        .catch(function (e) { setStatus('Failed: ' + e.message); });
    }

    // Permanently delete all DVR recordings — strong warning + password.
    function wipeDvr() {
      if (!confirm('⚠ WARNING\n\nThis permanently DELETES ALL recorded DVR data.\nThis cannot be undone.\n\nContinue?')) return;
      var pw = prompt('Type the confirmation password to permanently delete ALL recordings:');
      if (pw === null || pw === '') return;
      fetch('/dvr/clear', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: pw })
        .then(function (r) {
          if (r.status === 403) { alert('Wrong password — nothing was deleted.'); return; }
          if (!r.ok) { alert('Failed to delete (HTTP ' + r.status + ').'); return; }
          return r.json().then(function (j) { alert('Deleted all DVR recordings (' + (j.cleared || 0) + ' segments).'); refreshDvr(); });
        })
        .catch(function (e) { alert('Failed: ' + e.message); });
    }

    // Stall watchdog: if LIVE playback stops advancing while we believe we're playing,
    // reload to recover (e.g. a wedged hls.js after a freeze). Never in DVR mode — a
    // reload there would jump out of the recording the user is watching.
    var wdLastTime = 0, wdStalledSince = 0;
    setInterval(function () {
      if (isDvr()) { wdStalledSince = 0; return; }
      if (v.paused || v.seeking || v.readyState < 2) { wdLastTime = v.currentTime; wdStalledSince = 0; return; }
      if (v.currentTime > wdLastTime + 0.25) { wdLastTime = v.currentTime; wdStalledSince = 0; return; }
      if (wdStalledSince === 0) { wdStalledSince = Date.now(); return; }
      if (Date.now() - wdStalledSince > 15000) {
        wdStalledSince = 0; wdLastTime = 0;
        setStatus('Stalled — reloading…');
        play(currentPath);
      }
    }, 5000);

    // Show how much is recorded, next to the DVR button.
    function refreshDvr() {
      fetch('/dvr/status').then(function (r) { return r.json(); }).then(function (s) {
        var btn = document.getElementById('btnDvr');
        if (!s.enabled) { btn.textContent = 'DVR off'; btn.disabled = true; return; }
        btn.textContent = 'DVR ' + (s.hours || 0).toFixed(1) + 'h';
      }).catch(function () {});
    }

    // Show a friendly banner when the upstream source is unavailable / stalled, and
    // auto-resume playback when it recovers.
    var lastHealthOk = true;
    function refreshHealth() {
      fetch('/health').then(function (r) { return r.json(); }).then(function (h) {
        var b = document.getElementById('banner');
        if (!h.hasStream) {
          b.className = ''; b.textContent = '';
        } else if (h.ok === false) {
          b.textContent = '⚠ ' + (h.error || 'Source unavailable') +
            '. Paste a fresh URL above and press OK. Recording is paused, but your recording is intact — press DVR to watch it.';
          b.className = 'show';
        } else {
          b.className = ''; b.textContent = '';
          // Recovered after being down — reload to catch the live edge, but only in
          // live mode (don't yank someone out of the DVR recording they're watching).
          if (!lastHealthOk && !isDvr()) play(currentPath);
        }
        lastHealthOk = (h.ok !== false);
      }).catch(function () {});
    }

    // Persist the DVR watch position (per browser) so re-opening DVR resumes there.
    setInterval(saveDvrPos, 5000);
    v.addEventListener('pause', saveDvrPos);
    window.addEventListener('pagehide', saveDvrPos);

    // If the Cast SDK was already ready before this script ran, init now.
    window.initCast();
    refreshDvr();
    refreshHealth();
    setInterval(refreshDvr, 15000);
    setInterval(refreshHealth, 10000);

    // Auto-start if a URL was pre-loaded at server startup.
    if (document.getElementById('url').value.trim()) start();
  </script>
</body>
</html>
""";


// Holds the active upstream playlist URL and the base it derives for segments.
sealed class StreamState
{
    readonly object _gate = new();
    public string? PlaylistUrl { get; private set; }
    public string? BaseUrl { get; private set; }

    public void Set(string playlistUrl)
    {
        lock (_gate)
        {
            PlaylistUrl = playlistUrl;
            // Base for relative segment names: everything up to the last '/'.
            BaseUrl = playlistUrl[..(playlistUrl.LastIndexOf('/') + 1)];
        }
    }

    public (string? PlaylistUrl, string? BaseUrl) Snapshot()
    {
        lock (_gate) return (PlaylistUrl, BaseUrl);
    }
}


// Tracks upstream health so the UI can warn when the source is down/changed. Ok()
// is called on a valid playlist fetch, Fail() on any failure; the recorder and the
// live /stream.m3u8 handler both feed it.
sealed record HealthSnapshot(bool Ok, DateTime? LastOkUtc, DateTime? LastSegmentUtc, string? Error, int Failures);

sealed class StreamHealth
{
    readonly object _gate = new();
    DateTime? _lastOk, _lastSeg;
    string? _error;
    int _failures;
    bool _everOk;

    public void Ok()      { lock (_gate) { _lastOk = DateTime.UtcNow; _error = null; _failures = 0; _everOk = true; } }
    public void Segment() { lock (_gate) { _lastSeg = DateTime.UtcNow; } }
    public void Fail(string error) { lock (_gate) { _error = error; _failures++; } }

    public HealthSnapshot Snapshot()
    {
        // Ok == the last fetch attempt succeeded (failures reset to 0 on Ok) and we've
        // succeeded at least once. Failing => Ok is false and Error explains why.
        lock (_gate) return new HealthSnapshot(_failures == 0 && _everOk, _lastOk, _lastSeg, _error, _failures);
    }
}


// One recorded segment in the DVR index. Id is our own monotonic counter (the
// upstream media-sequence resets across channels, so we don't reuse it). Session
// increments on each channel change, so the live-edge view can show only the
// current channel. The segment file on disk is "<Id:D12>.ts".
sealed record DvrSeg(long Id, double Dur, DateTime Utc, bool Disc, long Size, long Session);

// The rolling DVR buffer: segment files under <dir>/seg + an append-only index
// file <dir>/index.log. The in-memory list is the source of truth at runtime;
// the index is for surviving restarts. Single writer (the recorder); HTTP
// handlers only take snapshots.
sealed class DvrStore
{
    readonly object _gate = new();
    readonly List<DvrSeg> _segs = new();
    readonly string _segDir;
    readonly string _indexPath;
    readonly TimeSpan _retention;
    readonly ILogger _log;
    long _nextId = 1;

    public string Dir { get; }

    public DvrStore(string dir, TimeSpan retention, ILogger log)
    {
        Dir = Path.GetFullPath(dir);   // absolute — Results.File requires a rooted path
        _segDir = Path.Combine(Dir, "seg");
        _indexPath = Path.Combine(Dir, "index.log");
        _retention = retention;
        _log = log;
        Directory.CreateDirectory(_segDir);
    }

    public string PathFor(long id) => Path.Combine(_segDir, id.ToString("D12") + ".ts");
    public string PathForName(string name) => Path.Combine(_segDir, name);

    public long Reserve() { lock (_gate) return _nextId++; }

    public void Add(DvrSeg seg)
    {
        lock (_gate) _segs.Add(seg);
        try { File.AppendAllText(_indexPath, Serialize(seg) + "\n"); }
        catch (IOException ex) { _log.LogWarning("DVR: index append failed: {Msg}", ex.Message); }
    }

    public DvrSeg[] Snapshot() { lock (_gate) return _segs.ToArray(); }

    // The newest up-to-maxSegments segments that belong to the current channel
    // (latest session) — the "live edge" served to all viewers.
    public DvrSeg[] LiveWindow(int maxSegments)
    {
        lock (_gate)
        {
            int n = _segs.Count;
            if (n == 0) return Array.Empty<DvrSeg>();
            long sess = _segs[n - 1].Session;
            int start = n, count = 0;
            for (int i = n - 1; i >= 0 && count < maxSegments; i--)
            {
                if (_segs[i].Session != sess) break;
                start = i; count++;
            }
            var res = new DvrSeg[n - start];
            _segs.CopyTo(start, res, 0, n - start);
            return res;
        }
    }

    // Highest session id on disk, so a restart resumes with strictly newer sessions.
    public long MaxSession()
    {
        lock (_gate) { long m = 0; foreach (var s in _segs) if (s.Session > m) m = s.Session; return m; }
    }

    // Permanently delete ALL recordings: clear the index and remove every segment
    // file. Returns how many segments were dropped. The recorder simply starts
    // refilling an empty buffer afterwards.
    public int Clear()
    {
        int n;
        lock (_gate)
        {
            n = _segs.Count;
            _segs.Clear();
            _nextId = 1;
            try { if (Directory.Exists(_segDir)) Directory.Delete(_segDir, recursive: true); }
            catch (IOException) { /* a file may be mid-write; orphans get overwritten by id reuse */ }
            catch (UnauthorizedAccessException) { }
            Directory.CreateDirectory(_segDir);
            try { if (File.Exists(_indexPath)) File.Delete(_indexPath); } catch (IOException) { }
        }
        return n;
    }

    public (int count, double seconds, DateTime? from, DateTime? to, long bytes) Stats()
    {
        lock (_gate)
        {
            if (_segs.Count == 0) return (0, 0, null, null, 0);
            double secs = 0; long bytes = 0;
            foreach (var s in _segs) { secs += s.Dur; bytes += s.Size; }
            var last = _segs[^1];
            return (_segs.Count, secs, _segs[0].Utc, last.Utc.AddSeconds(last.Dur), bytes);
        }
    }

    // Drop (and delete) segments older than the retention window. Returns count removed.
    public int PruneExpired(DateTime nowUtc)
    {
        var cutoff = nowUtc - _retention;
        List<DvrSeg> removed = new();
        lock (_gate)
        {
            while (_segs.Count > 0 && _segs[0].Utc < cutoff)
            {
                removed.Add(_segs[0]);
                _segs.RemoveAt(0);
            }
            if (removed.Count > 0) RewriteIndexLocked();
        }
        foreach (var s in removed)
            try { File.Delete(PathFor(s.Id)); } catch (IOException) { /* best effort */ }
        return removed.Count;
    }

    // Secondary protection: if free disk space is below minFreeBytes, delete the
    // oldest segments (estimating by their recorded size) until we've freed enough.
    // If free space can't be determined, do nothing (never blindly wipe the buffer).
    public int PruneToFreeSpace(long minFreeBytes)
    {
        var free = AvailableFreeBytes(_segDir);
        if (free is null || free.Value >= minFreeBytes) return 0;

        long needed = minFreeBytes - free.Value;
        List<DvrSeg> removed = new();
        lock (_gate)
        {
            long freed = 0;
            while (_segs.Count > 0 && freed < needed)
            {
                freed += _segs[0].Size;
                removed.Add(_segs[0]);
                _segs.RemoveAt(0);
            }
            if (removed.Count > 0) RewriteIndexLocked();
        }
        foreach (var s in removed)
            try { File.Delete(PathFor(s.Id)); } catch (IOException) { }
        return removed.Count;
    }

    // Free bytes on the filesystem that holds `path` (longest-matching mount, so
    // it's correct for a separate volume/bind mount on Linux). Null if unknown.
    static long? AvailableFreeBytes(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (full.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase) &&
                        (best is null || d.Name.Length > best.Name.Length))
                        best = d;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return best?.AvailableFreeSpace;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
    }

    // Load the index from disk on startup: parse, drop entries whose files are
    // gone or expired (deleting expired files), resume the id counter, compact.
    public void Load()
    {
        if (!File.Exists(_indexPath)) return;
        var cutoff = DateTime.UtcNow - _retention;
        long maxId = 0;
        List<DvrSeg> toDelete = new();
        try
        {
            foreach (var line in File.ReadLines(_indexPath))
            {
                if (Deserialize(line) is not { } s) continue;
                if (s.Id > maxId) maxId = s.Id;
                if (!File.Exists(PathFor(s.Id))) continue;          // file gone — drop entry
                if (s.Utc < cutoff) { toDelete.Add(s); continue; }  // expired — delete file
                _segs.Add(s);
            }
        }
        catch (IOException ex) { _log.LogWarning("DVR: index load failed: {Msg}", ex.Message); }

        _segs.Sort((a, b) => a.Id.CompareTo(b.Id));
        _nextId = maxId + 1;
        foreach (var s in toDelete)
            try { File.Delete(PathFor(s.Id)); } catch (IOException) { }
        lock (_gate) RewriteIndexLocked();   // compact away dropped/expired entries
        _log.LogInformation("DVR: loaded {N} segments from disk", _segs.Count);
    }

    void RewriteIndexLocked()
    {
        var tmp = _indexPath + ".tmp";
        try
        {
            File.WriteAllLines(tmp, _segs.ConvertAll(Serialize));
            File.Move(tmp, _indexPath, overwrite: true);
        }
        catch (IOException ex) { _log.LogWarning("DVR: index rewrite failed: {Msg}", ex.Message); }
    }

    static string Serialize(DvrSeg s) =>
        string.Join(';',
            s.Id,
            s.Dur.ToString(CultureInfo.InvariantCulture),
            new DateTimeOffset(s.Utc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            s.Disc ? 1 : 0,
            s.Size,
            s.Session);

    static DvrSeg? Deserialize(string line)
    {
        var p = line.Split(';');
        if (p.Length < 5) return null;
        if (!long.TryParse(p[0], out var id)) return null;
        if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dur)) return null;
        if (!long.TryParse(p[2], out var ms)) return null;
        long.TryParse(p[4], out var size);
        long session = 0;
        if (p.Length > 5) long.TryParse(p[5], out session); // older index files lack this
        return new DvrSeg(id, dur, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, p[3] == "1", size, session);
    }
}
