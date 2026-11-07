using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yarp.ReverseProxy.Forwarder;

namespace ServerBrain;

/// <summary>
/// Routes http://{project}.localhost (and public tunnel hostnames) to the right project, starting
/// on-demand projects the first time they are visited and protecting public visitors with an access code.
/// </summary>
public sealed class ProxyService
{
    private readonly ProjectManager _mgr;
    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _client;
    private readonly Dictionary<string, (int fails, DateTime since)> _attempts = new();

    public ProxyService(ProjectManager mgr, IHttpForwarder forwarder)
    {
        _mgr = mgr;
        _forwarder = forwarder;
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            ActivityHeadersPropagator = null,
        });
    }

    public static bool IsPublicRequest(HttpRequest req) =>
        req.Headers.ContainsKey("Cf-Connecting-Ip") || req.Headers.ContainsKey("Cf-Ray") || req.Headers.ContainsKey("X-Forwarded-For");

    /// <summary>The scheme the visitor really used: tunnel traffic arrives over plain http but is https for the visitor.</summary>
    private static string PublicScheme(HttpRequest req)
    {
        if (!IsPublicRequest(req)) return req.Scheme;
        var viaTunnel = req.Headers["Cf-Visitor"].ToString().Contains("\"https\"", StringComparison.OrdinalIgnoreCase)
            || string.Equals(req.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim(), "https", StringComparison.OrdinalIgnoreCase);
        return viaTunnel ? "https" : req.Scheme;
    }

    public async Task HandleAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        var isPublic = IsPublicRequest(req);
        var r = _mgr.FindByHost(req.Host.Host);
        if (r == null || (isPublic && r.Config.Share.Mode == ShareMode.Off))
        {
            await WritePage(ctx, 404, Pages.NotFound(req.Host.Host, ServerBrainHostInfo.DashboardPort, showLinks: !isPublic));
            return;
        }
        var cfg = r.Config;

        // ---- Server Brain's own helper endpoints on every project host
        if (req.Path.StartsWithSegments("/__sb", out var rest))
        {
            await HandleInternal(ctx, r, rest.Value ?? "", isPublic);
            return;
        }

        // ---- Public visitors must pass the access gate
        if (isPublic && cfg.Share.RequireCode && !string.IsNullOrEmpty(cfg.Share.Code) && !HasValidCookie(ctx, cfg))
        {
            await WritePage(ctx, 401, Pages.Gate(cfg.Name, null));
            return;
        }

        // ---- Make sure the project is up
        if (!r.IsUp)
        {
            if (cfg.Mode == RunMode.OnDemand && r.Status is not (ProjectStatus.Stopping or ProjectStatus.Waiting))
            {
                r.Touch();
                _ = _mgr.StartAsync(cfg.Id, "on-demand");
                await WritePage(ctx, 200, Pages.Splash(cfg.Name, cfg.Color, isPublic), req);
            }
            else if (r.Status is ProjectStatus.Waiting or ProjectStatus.Stopping)
                await WritePage(ctx, 200, Pages.Splash(cfg.Name, cfg.Color, isPublic), req);
            else
                await WritePage(ctx, 503, Pages.Offline(cfg.Name, cfg.Color, r.Status.ToString(), r.Error, canStart: !isPublic), req);
            return;
        }
        if (r.Status == ProjectStatus.Starting || r.ActivePort is null)
        {
            if (r.PortSearchTimedOut)
                await WritePage(ctx, 503, Pages.NoPort(cfg.Name, cfg.Color, isPublic), req);
            else
                await WritePage(ctx, 200, Pages.Splash(cfg.Name, cfg.Color, isPublic), req);
            return;
        }

        // ---- Forward
        var port = r.ActivePort!.Value;
        var transformer = new ProjectTransformer(port, req.Host.Value ?? "", cfg.RewriteHeaders);
        ForwarderError error;
        using (r.TrackRequest())
            error = await _forwarder.SendAsync(ctx, $"http://127.0.0.1:{port}", _client, ForwarderRequestConfig.Empty, transformer);
        if (error != ForwarderError.None && !ctx.Response.HasStarted)
        {
            var detail = ctx.Features.Get<IForwarderErrorFeature>()?.Exception?.Message;
            await WritePage(ctx, 502, Pages.BadGateway(cfg.Name, port, detail), req);
        }
    }

    private async Task HandleInternal(HttpContext ctx, ProjectRunner r, string rest, bool isPublic)
    {
        var cfg = r.Config;
        var req = ctx.Request;
        ctx.Response.Headers.CacheControl = "no-store";
        switch (rest.ToLowerInvariant())
        {
            case "/status":
            {
                if (isPublic && cfg.Share.RequireCode && !HasValidCookie(ctx, cfg)) { ctx.Response.StatusCode = 401; return; }
                var lines = isPublic ? new List<string>() : r.Logs.Tail(4).Select(l => l.Text).ToList();
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    status = r.Status, ready = r.Status == ProjectStatus.Running && r.ActivePort != null, noPort = r.PortSearchTimedOut,
                    error = isPublic ? null : r.Error, lines,
                }, Json.Options));
                return;
            }
            case "/start" when req.Method == "POST" && !isPublic:
            {
                if (!SameOrigin(req)) { ctx.Response.StatusCode = 403; return; }
                await _mgr.StartAsync(cfg.Id, "manual", openWhenReady: false, force: false);
                ctx.Response.StatusCode = 204;
                return;
            }
            case "/unlock" when req.Method == "POST":
            {
                var ip = ClientIp(ctx);
                if (TooManyAttempts(ip)) { await WritePage(ctx, 429, Pages.Gate(cfg.Name, "Too many attempts. Try again in a few minutes.")); return; }
                var form = await req.ReadFormAsync();
                var code = form["code"].ToString().Trim();
                if (!string.IsNullOrEmpty(cfg.Share.Code) && FixedEquals(code.ToUpperInvariant(), cfg.Share.Code.ToUpperInvariant()))
                {
                    ctx.Response.Cookies.Append(CookieName(cfg), Sign(cfg), new CookieOptions
                    {
                        HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(7), Path = "/",
                        Secure = PublicScheme(req) == "https",
                    });
                    ctx.Response.Redirect("/", false);
                    return;
                }
                RegisterFail(ip);
                await WritePage(ctx, 401, Pages.Gate(cfg.Name, "That code is not right."));
                return;
            }
            default:
                ctx.Response.StatusCode = 404;
                return;
        }
    }

    // ------------------------------------------------------------ access gate

    private static string CookieName(ProjectConfig c) => "sb_gate_" + c.Id;

    private static string Sign(ProjectConfig c)
    {
        using var h = new HMACSHA256(Secrets.SigningKey());
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(c.Id + ":" + c.Share.Code))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool HasValidCookie(HttpContext ctx, ProjectConfig c) =>
        ctx.Request.Cookies.TryGetValue(CookieName(c), out var v) && FixedEquals(v ?? "", Sign(c));

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string ClientIp(HttpContext ctx) =>
        ctx.Request.Headers["Cf-Connecting-Ip"].FirstOrDefault() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    private bool TooManyAttempts(string ip)
    {
        lock (_attempts)
        {
            if (!_attempts.TryGetValue(ip, out var a)) return false;
            if ((DateTime.UtcNow - a.since).TotalMinutes > 5) { _attempts.Remove(ip); return false; }
            return a.fails >= 8;
        }
    }

    private void RegisterFail(string ip)
    {
        lock (_attempts)
        {
            _attempts.TryGetValue(ip, out var a);
            if (a.since == default || (DateTime.UtcNow - a.since).TotalMinutes > 5) a = (0, DateTime.UtcNow);
            _attempts[ip] = (a.fails + 1, a.since);
        }
    }

    public static bool SameOrigin(HttpRequest req)
    {
        var origin = req.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var u) &&
               string.Equals(u.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WritePage(HttpContext ctx, int status, string html, HttpRequest? req = null)
    {
        // Non-navigation requests (XHR, images, ...) get a plain 503 instead of an HTML splash.
        if (req != null && status == 200 && !(req.Headers.Accept.ToString().Contains("text/html")) && req.Method == "GET" && req.Headers.ContainsKey("Accept"))
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.Headers.RetryAfter = "2";
            return;
        }
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsync(html);
    }

    private sealed class ProjectTransformer : HttpTransformer
    {
        private readonly int _port;
        private readonly string _publicHost;
        private readonly bool _rewrite;

        public ProjectTransformer(int port, string publicHost, bool rewrite) { _port = port; _publicHost = publicHost; _rewrite = rewrite; }

        public override async ValueTask TransformRequestAsync(HttpContext ctx, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken ct)
        {
            await base.TransformRequestAsync(ctx, proxyRequest, destinationPrefix, ct);
            proxyRequest.Headers.Host = $"127.0.0.1:{_port}";
            // set (not append) so a visitor cannot smuggle their own forwarded host/proto to the project
            proxyRequest.Headers.Remove("X-Forwarded-Host");
            proxyRequest.Headers.Remove("X-Forwarded-Proto");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", _publicHost);
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", PublicScheme(ctx.Request));
            if (!_rewrite) return;
            var origin = $"http://127.0.0.1:{_port}";
            if (proxyRequest.Headers.Contains("Origin")) { proxyRequest.Headers.Remove("Origin"); proxyRequest.Headers.TryAddWithoutValidation("Origin", origin); }
            if (proxyRequest.Headers.TryGetValues("Referer", out var refs) && Uri.TryCreate(refs.FirstOrDefault(), UriKind.Absolute, out var ru))
            {
                proxyRequest.Headers.Remove("Referer");
                proxyRequest.Headers.TryAddWithoutValidation("Referer", origin + ru.PathAndQuery);
            }
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext ctx, HttpResponseMessage? proxyResponse, CancellationToken ct)
        {
            var ok = await base.TransformResponseAsync(ctx, proxyResponse, ct);
            if (proxyResponse?.Headers.Location is Uri loc && _rewrite)
            {
                if (loc.IsAbsoluteUri && loc.Port == _port && (loc.Host is "127.0.0.1" or "localhost" or "::1"))
                    ctx.Response.Headers.Location = $"{PublicScheme(ctx.Request)}://{_publicHost}{loc.PathAndQuery}";
            }
            return ok;
        }
    }
}
