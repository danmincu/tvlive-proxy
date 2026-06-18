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

// The currently-selected upstream stream. Mutated when the user submits the
// form (POST /set), or pre-loaded from the CLI arg / env var below.
var state = new StreamState();
string initialUrl =
    args.FirstOrDefault(a => !a.StartsWith('-'))
    ?? Environment.GetEnvironmentVariable("PROXY_PLAYLIST_URL")
    ?? "https://alpha1.yosefina1.cfd/ah1/usergenx304Jtlrnd2-got.htm";
state.Set(initialUrl);

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
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = true,
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
async Task<HttpResponseMessage?> SendUpstreamAsync(HttpClient http, string url, string label, CancellationToken ct)
{
    static bool IsTransient(HttpStatusCode s) =>
        (int)s >= 500 || s == HttpStatusCode.RequestTimeout || s == HttpStatusCode.TooManyRequests;

    for (int attempt = 1; ; attempt++)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(upstreamTimeout);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req);

        try
        {
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!IsTransient(resp.StatusCode) || attempt >= maxAttempts)
                return resp;
            log.LogWarning("{Label}: upstream {Status} (attempt {Attempt}/{Max}); retrying",
                label, (int)resp.StatusCode, attempt, maxAttempts);
            resp.Dispose();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the client disconnected — stop, let it bubble up
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // our per-attempt timeout, or a connection-level error
            if (attempt >= maxAttempts)
            {
                log.LogWarning("{Label}: failed after {Max} attempts ({Msg})", label, maxAttempts, ex.Message);
                return null;
            }
            log.LogWarning("{Label}: {Msg} (attempt {Attempt}/{Max}); retrying", label, ex.Message, attempt, maxAttempts);
        }

        // Exponential backoff: 200ms, 400ms, 800ms, ...
        await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);
    }
}

// Download one segment's bytes (with retries). Returns null on failure.
async Task<byte[]?> DownloadSegmentAsync(HttpClient http, string url, CancellationToken ct)
{
    using var resp = await SendUpstreamAsync(http, url, "DVR segment", ct);
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
                if (channel is not null) log.LogInformation("DVR: now recording {Url}", channel);
            }
            if (playlistUrl is null || baseUrl is null)
            {
                await Task.Delay(dvrPoll, ct);
                continue;
            }

            using var resp = await SendUpstreamAsync(http, playlistUrl, "DVR playlist", ct);
            if (resp is not null && resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
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
                    if (bytes is null)
                    {
                        // Couldn't fetch it; skip but don't stall, and flag a hole.
                        lastSeq = seq;
                        pendingDisc = true;
                        extDisc = false; pendingDur = 0;
                        continue;
                    }

                    long id = dvr.Reserve();
                    await File.WriteAllBytesAsync(dvr.PathFor(id), bytes, ct);
                    dvr.Add(new DvrSeg(id, pendingDur > 0 ? pendingDur : 6.0, DateTime.UtcNow,
                        pendingDisc || extDisc || gap, bytes.LongLength));

                    lastSeq = seq;
                    pendingDisc = false; extDisc = false; pendingDur = 0;
                }
            }

            if (DateTime.UtcNow - lastPrune > TimeSpan.FromSeconds(60))
            {
                int removed = dvr.PruneExpired(DateTime.UtcNow);
                if (removed > 0) log.LogInformation("DVR: pruned {N} expired segments", removed);
                lastPrune = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        catch (Exception ex) { log.LogWarning("DVR loop error: {Msg}", ex.Message); }

        await Task.Delay(dvrPoll, ct);
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
    return Results.NoContent();
});

// Call 1: the playlist, served to the player / VLC.
app.MapGet("/stream.m3u8", async (HttpResponse response, HttpClient http, CancellationToken ct) =>
{
    var (playlistUrl, _) = state.Snapshot();
    if (playlistUrl is null)
    {
        response.StatusCode = 409; // nothing selected yet
        await response.WriteAsync("No stream set. Open http://localhost:" + port + "/ and paste a URL.", ct);
        return;
    }

    log.LogInformation("Playlist  -> {Url}", playlistUrl);

    using var upstream = await SendUpstreamAsync(http, playlistUrl, "Playlist", ct);
    if (upstream is null)
    {
        response.StatusCode = StatusCodes.Status502BadGateway;
        await response.WriteAsync("Upstream playlist unreachable after retries.", ct);
        return;
    }
    if (!upstream.IsSuccessStatusCode)
    {
        log.LogWarning("Playlist upstream returned {Status}", (int)upstream.StatusCode);
        response.StatusCode = (int)upstream.StatusCode;
        return;
    }

    // Return the playlist verbatim — its relative segment names route back here.
    var body = await upstream.Content.ReadAsStringAsync(ct);
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

    var sb = new StringBuilder();
    sb.Append("#EXTM3U\n#EXT-X-VERSION:6\n");
    double maxDur = 6;
    foreach (var s in segs) if (s.Dur > maxDur) maxDur = s.Dur;
    sb.Append("#EXT-X-TARGETDURATION:").Append((int)Math.Ceiling(maxDur)).Append('\n');
    sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(segs.Length > 0 ? segs[0].Id : 0).Append('\n');
    if (vod) sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
    foreach (var s in segs)
    {
        if (s.Disc) sb.Append("#EXT-X-DISCONTINUITY\n");
        sb.Append("#EXTINF:").Append(s.Dur.ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("/dvr/seg/").Append(s.Id.ToString("D12")).Append(".ts\n");
    }
    if (vod) sb.Append("#EXT-X-ENDLIST\n");

    response.ContentType = "application/vnd.apple.mpegurl";
    response.Headers.CacheControl = "no-cache, no-store";
    return response.WriteAsync(sb.ToString());
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
  body { margin:0; background:#111; color:#eee; font:14px system-ui, sans-serif; }
  header { display:flex; gap:8px; padding:10px; background:#1b1b1b; border-bottom:1px solid #333; }
  input { flex:1; padding:8px; border:1px solid #444; border-radius:4px; background:#222; color:#eee; }
  button { padding:8px 16px; border:0; border-radius:4px; background:#2d7; color:#000; font-weight:600; cursor:pointer; }
  button.alt { background:#345; color:#eee; }
  button.active { outline:2px solid #2d7; }
  a.dl { padding:8px 12px; border-radius:4px; background:#345; color:#eee; text-decoration:none; font-weight:600; white-space:nowrap; }
  google-cast-launcher { width:32px; height:32px; flex:0 0 auto; cursor:pointer; --connected-color:#2d7; --disconnected-color:#eee; }
  video { width:100%; height:calc(100vh - 80px); background:#000; display:block; }
  #status { padding:4px 10px; color:#999; font-size:12px; min-height:16px; }
</style>
</head>
<body>
  <header>
    <input id="url" placeholder="Paste playlist URL, e.g. https://.../usergenx...-got.htm"
           value="{{System.Net.WebUtility.HtmlEncode(current ?? "")}}"
           onkeydown="if(event.key==='Enter')load()">
    <button onclick="load()">OK</button>
    <button id="btnLive" class="alt" onclick="goLive()" title="Live edge">Live</button>
    <button id="btnDvr" class="alt" onclick="goDvr()" title="Timeshift / DVR — scrub back through the recorded window">DVR</button>
    <a class="dl" href="/dvr/export.ts" title="Download the recorded buffer as one .ts file">&#8595; .ts</a>
    <google-cast-launcher id="castbtn" title="Cast to a Chromecast (open this page via your LAN IP, not localhost)"></google-cast-launcher>
  </header>
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

    function play(path) {
      currentPath = path;
      var live = path.indexOf('/dvr.m3u8') < 0;
      document.getElementById('btnLive').classList.toggle('active', live);
      document.getElementById('btnDvr').classList.toggle('active', !live);
      var src = path + (path.indexOf('?') >= 0 ? '&' : '?') + 't=' + Date.now();
      if (window.Hls && Hls.isSupported()) {
        if (hls) hls.destroy();
        hls = new Hls({ liveSyncDuration: 12 });
        hls.loadSource(src);
        hls.attachMedia(v);
        hls.on(Hls.Events.MANIFEST_PARSED, function () { v.play(); setStatus(live ? 'Playing (live)' : 'Playing (DVR — drag the seek bar to rewind)'); });
        hls.on(Hls.Events.ERROR, function (e, d) {
          if (d.fatal) setStatus('Error: ' + d.type + ' / ' + d.details);
        });
      } else {
        // Safari / native HLS
        v.src = src;
        v.play();
        setStatus('Playing (native HLS)');
      }
    }

    function start()  { play('/stream.m3u8'); }
    function goLive() { play('/stream.m3u8'); if (isCasting()) castLoad(); }
    function goDvr()  { play('/dvr.m3u8');    if (isCasting()) castLoad(); }

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

    // Show how much is recorded, next to the DVR button.
    function refreshDvr() {
      fetch('/dvr/status').then(function (r) { return r.json(); }).then(function (s) {
        var btn = document.getElementById('btnDvr');
        if (!s.enabled) { btn.textContent = 'DVR off'; btn.disabled = true; return; }
        btn.textContent = 'DVR ' + (s.hours || 0).toFixed(1) + 'h';
      }).catch(function () {});
    }

    // If the Cast SDK was already ready before this script ran, init now.
    window.initCast();
    refreshDvr();
    setInterval(refreshDvr, 15000);

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


// One recorded segment in the DVR index. Id is our own monotonic counter (the
// upstream media-sequence resets across channels, so we don't reuse it). The
// segment file on disk is "<Id:D12>.ts".
sealed record DvrSeg(long Id, double Dur, DateTime Utc, bool Disc, long Size);

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
            s.Size);

    static DvrSeg? Deserialize(string line)
    {
        var p = line.Split(';');
        if (p.Length < 5) return null;
        if (!long.TryParse(p[0], out var id)) return null;
        if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dur)) return null;
        if (!long.TryParse(p[2], out var ms)) return null;
        long.TryParse(p[4], out var size);
        return new DvrSeg(id, dur, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, p[3] == "1", size);
    }
}
