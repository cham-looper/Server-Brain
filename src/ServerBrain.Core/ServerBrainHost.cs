using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ServerBrain;

/// <summary>Hooks the desktop shell (tray app) provides to the web API.</summary>
public interface IShellControl
{
    void ShowWindow();
    void Quit();
}

public sealed class NullShell : IShellControl
{
    public void ShowWindow() { }
    public void Quit() { }
}

public sealed class HostOptions
{
    public bool RunAutoStart { get; set; } = true;
    public IShellControl Shell { get; set; } = new NullShell();
    public bool IsDesktopApp { get; set; }
}

public sealed class ServerBrainRuntime
{
    public WebApplication App { get; init; } = default!;
    public ProjectManager Manager { get; init; } = default!;
    public EventHub Hub { get; init; } = default!;
    public ActivityLog Activity { get; init; } = default!;
    public SettingsService Settings { get; init; } = default!;
    public int DashboardPort { get; init; }
    public int ProxyPort { get; init; }
    public string DashboardUrl => $"http://127.0.0.1:{DashboardPort}/";
    public CancellationTokenSource Stop { get; } = new();

    public async Task StartAsync()
    {
        await App.StartAsync();
        _ = Manager.RunLoopAsync(Stop.Token);
    }

    public async Task ShutdownAsync()
    {
        Stop.Cancel();
        try { await Manager.StopAllAsync(); } catch { }
        Manager.Dispose();
        try { await App.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
    }
}

public static class ServerBrainHost
{
    public static readonly string Version = typeof(ServerBrainHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    /// <summary>
    /// When the program version changed since the last run (an update), keep a copy of the project list and settings first.
    /// Data lives outside the install folder, so updates never delete it; this is just a safety net.
    /// </summary>
    private static void BackupOnUpgrade()
    {
        try
        {
            var marker = Path.Combine(AppPaths.DataDir, "version.txt");
            var old = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (old != null && old != Version && File.Exists(AppPaths.StateFile))
            {
                var dir = Directory.CreateDirectory(Path.Combine(AppPaths.BackupsDir, $"upgrade-{old}-to-{Version}-{DateTime.Now:yyyyMMdd-HHmmss}")).FullName;
                foreach (var f in new[] { AppPaths.StateFile, AppPaths.SettingsFile }) if (File.Exists(f)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                foreach (var d in new DirectoryInfo(AppPaths.BackupsDir).GetDirectories("upgrade-*").OrderByDescending(x => x.CreationTimeUtc).Skip(5)) try { d.Delete(true); } catch { }
            }
            File.WriteAllText(marker, Version);
        }
        catch { }
    }

    public static ServerBrainRuntime Build(string[] args, HostOptions options)
    {
        BackupOnUpgrade();
        var settings = new SettingsService();
        var s = settings.Current;
        var dashPort = PickPort(s.DashboardPort, 20);
        var proxyPort = s.ProxyEnabled ? PickPort(s.ProxyPort, 20, fallbackStart: 8080) : 0;
        ServerBrainHostInfo.DashboardPort = dashPort;
        ServerBrainHostInfo.ProxyPort = proxyPort;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 512L * 1024 * 1024;   // projects may accept big uploads through the proxy
            foreach (var port in new[] { dashPort, proxyPort }.Where(p => p > 0).Distinct())
            {
                if (CanBind(IPAddress.Loopback, port)) k.Listen(IPAddress.Loopback, port);
                if (Socket.OSSupportsIPv6 && CanBind(IPAddress.IPv6Loopback, port)) k.Listen(IPAddress.IPv6Loopback, port);
            }
        });
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        builder.Services.AddHttpForwarder();
        var hub = new EventHub();
        var activity = new ActivityLog(hub);
        var tunnels = new TunnelManager(activity, () => settings.Current);
        var manager = new ProjectManager(hub, activity, tunnels, settings);
        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton(activity);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(tunnels);
        builder.Services.AddSingleton(manager);
        builder.Services.AddSingleton(new SystemInstaller(hub, activity));
        builder.Services.AddSingleton(options.Shell);
        builder.Services.AddSingleton<ProxyService>();

        var app = builder.Build();

        // 1) Any host that is not the dashboard is a project name -> reverse proxy.
        app.UseWhen(c => !IsDashboardHost(c.Request.Host.Host), branch =>
            branch.Run(ctx => ctx.RequestServices.GetRequiredService<ProxyService>().HandleAsync(ctx)));

        // 2) Dashboard hosts: strict guard, then API and the embedded UI.
        app.Use(DashboardGuard);
        Api.Map(app, options, dashPort, proxyPort);
        app.MapFallback("/{**path}", EmbeddedWeb.Serve);   // explicit pattern: the default one skips paths with a file extension

        activity.Add("info", "engine", $"Server Brain {Version} ready · dashboard on port {dashPort}" + (proxyPort > 0 ? $" · names on port {proxyPort}" : ""));
        if (proxyPort > 0 && proxyPort != s.ProxyPort)
            activity.Add("warn", "port", $"Port {s.ProxyPort} is taken, so project names use port {proxyPort} instead (http://name.localhost:{proxyPort}).");
        if (dashPort != s.DashboardPort)
            activity.Add("warn", "port", $"Dashboard port {s.DashboardPort} is taken; using {dashPort}.");

        var rt = new ServerBrainRuntime { App = app, Manager = manager, Hub = hub, Activity = activity, Settings = settings, DashboardPort = dashPort, ProxyPort = proxyPort };
        if (options.RunAutoStart)
            app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => manager.StartAlwaysOnAsync(rt.Stop.Token)));
        return rt;
    }

    public static bool IsDashboardHost(string host)
    {
        host = host.Trim('[', ']').ToLowerInvariant();
        return host is "127.0.0.1" or "localhost" or "::1" or "brain.localhost";
    }

    private static async Task DashboardGuard(HttpContext ctx, Func<Task> next)
    {
        var req = ctx.Request;
        // Public tunnel traffic must never reach the dashboard.
        if (ProxyService.IsPublicRequest(req)) { ctx.Response.StatusCode = 403; return; }
        if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method) && !HttpMethods.IsOptions(req.Method))
        {
            // Browsers always send Origin on writes; a different site (CSRF) is refused.
            // Scripts (curl...) must opt in with a custom header a foreign web page cannot set.
            var origin = req.Headers.Origin.ToString();
            var ok = !string.IsNullOrEmpty(origin)
                ? ProxyService.SameOrigin(req)
                : req.Headers.ContainsKey("X-SB-Client");
            if (!ok) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("Cross-site request refused."); return; }
        }
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        await next();
    }

    private static bool CanBind(IPAddress ip, int port)
    {
        try { var l = new TcpListener(ip, port); l.Start(); l.Stop(); return true; }
        catch { return false; }
    }

    private static int PickPort(int preferred, int tries, int? fallbackStart = null)
    {
        if (CanBind(IPAddress.Loopback, preferred)) return preferred;
        var start = fallbackStart ?? preferred + 1;
        for (var p = start; p < start + tries; p++) if (CanBind(IPAddress.Loopback, p)) return p;
        return preferred;
    }
}

/// <summary>Serves the dashboard UI that is embedded in the assembly (or a folder during development).</summary>
internal static class EmbeddedWeb
{
    private static readonly Dictionary<string, string> Resources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .Where(n => n.StartsWith("wwwroot/")).ToDictionary(n => n["wwwroot/".Length..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);
    private static readonly FileExtensionContentTypeProvider Types = new();
    private static readonly string? DevDir = Environment.GetEnvironmentVariable("SB_WEB_DIR");
    private static readonly string Etag = "\"" + Assembly.GetExecutingAssembly().GetName().Version + "-" + File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location).Ticks.ToString("x") + "\"";

    public static async Task Serve(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 404; return; }
        var rel = path.TrimStart('/');
        if (rel.Length == 0) rel = "index.html";
        Stream? stream = Open(rel);
        var isAsset = rel.StartsWith("assets/");
        if (stream == null)
        {
            if (Path.HasExtension(rel)) { ctx.Response.StatusCode = 404; return; }
            stream = Open("index.html");         // SPA fallback
            rel = "index.html";
        }
        if (stream == null)
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("The dashboard UI is not built yet. Run scripts/build.ps1 (or `npm run build` in /web).");
            return;
        }
        await using (stream)
        {
            if (!Types.TryGetContentType(rel, out var ct)) ct = "application/octet-stream";
            ctx.Response.ContentType = ct;
            if (isAsset) ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            else
            {
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers.ETag = Etag;
                if (ctx.Request.Headers.IfNoneMatch == Etag && DevDir == null) { ctx.Response.StatusCode = 304; return; }
            }
            await stream.CopyToAsync(ctx.Response.Body);
        }
    }

    private static Stream? Open(string rel)
    {
        rel = rel.Replace('\\', '/');
        if (DevDir != null)
        {
            var full = Path.GetFullPath(Path.Combine(DevDir, rel));
            if (full.StartsWith(Path.GetFullPath(DevDir), StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return File.OpenRead(full);
            return null;
        }
        return Resources.TryGetValue(rel, out var name) ? Assembly.GetExecutingAssembly().GetManifestResourceStream(name) : null;
    }
}
