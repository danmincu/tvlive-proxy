using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
}));

var app = builder.Build();
var log = app.Logger;

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

// The web UI: a URL box + an HLS video player.
app.MapGet("/", (HttpResponse response) =>
{
    response.ContentType = "text/html; charset=utf-8";
    return response.WriteAsync(IndexPage(state.PlaylistUrl, port));
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

    using var req = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
    AddBrowserHeaders(req);

    using var upstream = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

    using var req = new HttpRequestMessage(HttpMethod.Get, target);
    AddBrowserHeaders(req);

    using var upstream = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    response.StatusCode = (int)upstream.StatusCode;

    if (upstream.Content.Headers.ContentType is { } contentType)
        response.ContentType = contentType.ToString();
    response.Headers.CacheControl = "no-cache, no-store";

    if (!upstream.IsSuccessStatusCode)
    {
        log.LogWarning("Segment upstream returned {Status} for {Path}", (int)upstream.StatusCode, path);
        return;
    }

    // Stream the bytes straight through to the player.
    await upstream.Content.CopyToAsync(response.Body, ct);
});

log.LogInformation("rdslive-proxy listening (bind {Bind})", bind);
log.LogInformation("Player (http):  http://localhost:{Port}/", port);
log.LogInformation("Player (https): https://localhost:{HttpsPort}/   <- open via LAN IP for Chromecast", httpsPort);
log.LogInformation("VLC / m3u8:     http://localhost:{Port}/stream.m3u8", port);
if (state.PlaylistUrl is { } u)
    log.LogInformation("Pre-loaded:     {Url}", u);

app.Run();


// The single-page player. hls.js drives playback in Chrome/Firefox; Safari
// falls back to the browser's native HLS support.
static string IndexPage(string? current, int httpPort) => $$"""
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
  google-cast-launcher { width:32px; height:32px; flex:0 0 auto; cursor:pointer; --connected-color:#2d7; --disconnected-color:#eee; }
  video { width:100%; height:calc(100vh - 55px); background:#000; display:block; }
  #status { padding:4px 10px; color:#999; font-size:12px; min-height:16px; }
</style>
</head>
<body>
  <header>
    <input id="url" placeholder="Paste playlist URL, e.g. https://.../usergenx...-got.htm"
           value="{{System.Net.WebUtility.HtmlEncode(current ?? "")}}"
           onkeydown="if(event.key==='Enter')load()">
    <button onclick="load()">OK</button>
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
      // plain-http endpoint (same host, http port) regardless of how this page was opened.
      var castBase = 'http://' + window.location.hostname + ':' + {{httpPort}};
      var url = castBase + '/stream.m3u8?t=' + Date.now();
      var info = new chrome.cast.media.MediaInfo(url, 'application/x-mpegurl');
      info.streamType = chrome.cast.media.StreamType.LIVE;
      session.loadMedia(new chrome.cast.media.LoadRequest(info)).then(
        function () { setStatus('Casting to ' + session.getCastDevice().friendlyName); },
        function (err) { setStatus('Cast failed: ' + JSON.stringify(err)); }
      );
    }
    // -------------------------------------------------------------------------

    function start() {
      var src = '/stream.m3u8?t=' + Date.now();
      if (window.Hls && Hls.isSupported()) {
        if (hls) hls.destroy();
        hls = new Hls({ liveSyncDuration: 12 });
        hls.loadSource(src);
        hls.attachMedia(v);
        hls.on(Hls.Events.MANIFEST_PARSED, function () { v.play(); setStatus('Playing'); });
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

    // If the Cast SDK was already ready before this script ran, init now.
    window.initCast();

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
