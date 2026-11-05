using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ServerBrain;

internal static class Api
{
    private sealed record PathBody(string? Path);
    private sealed record KillBody(int Port, int Pid);
    private sealed record IdsBody(List<string>? Ids);
    private sealed record StartupBody(bool Enabled);
    private sealed record ResetBody(string? Confirm);

    public static void Map(WebApplication app, HostOptions host, int dashPort, int proxyPort)
    {
        var mgr = app.Services.GetRequiredService<ProjectManager>();
        var hub = app.Services.GetRequiredService<EventHub>();
        var activity = app.Services.GetRequiredService<ActivityLog>();
        var settings = app.Services.GetRequiredService<SettingsService>();
        var api = app.MapGroup("/api");

        // ------------------------------------------------------------ state
        api.MapGet("/state", () => Results.Ok(Snapshot(mgr, activity, settings, host, dashPort, proxyPort)));

        api.MapGet("/events", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var reader = hub.Subscribe(out var unsubscribe);
            try
            {
                await ctx.Response.WriteAsync("retry: 2000\n\n");
                await ctx.Response.WriteAsync("event: state\ndata: " + JsonSerializer.Serialize(Snapshot(mgr, activity, settings, host, dashPort, proxyPort), Json.Options) + "\n\n");
                await ctx.Response.Body.FlushAsync();
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    wait.CancelAfter(15000);
                    try
                    {
                        if (!await reader.WaitToReadAsync(wait.Token)) break;
                        while (reader.TryRead(out var frame)) await ctx.Response.WriteAsync(frame);
                    }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n");
                    }
                    await ctx.Response.Body.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally { unsubscribe(); }
        });

        // --------------------------------------------------------- projects
        api.MapPost("/projects", (ProjectInput input) =>
        {
            var (view, errs) = mgr.Create(input);
            return view == null ? Results.BadRequest(new { errors = errs }) : Results.Ok(new { project = view });
        });
        api.MapPut("/projects/{id}", (string id, ProjectInput input) =>
        {
            var (view, errs) = mgr.Update(id, input);
            return view == null ? Results.BadRequest(new { errors = errs }) : Results.Ok(new { project = view });
        });
        api.MapDelete("/projects/{id}", async (string id) => await mgr.RemoveAsync(id) ? Results.Ok() : Results.NotFound());
        api.MapPost("/projects/reorder", (IdsBody body) => { mgr.Reorder(body.Ids ?? new()); return Results.Ok(); });

        api.MapPost("/projects/{id}/start", async (string id, bool? open, bool? force) =>
            Results.Ok(await mgr.StartAsync(id, "manual", openWhenReady: open ?? true, force: force ?? false)));
        api.MapPost("/projects/{id}/stop", async (string id) => { await mgr.StopAsync(id); return Results.Ok(); });
        api.MapPost("/projects/{id}/restart", async (string id) => { await mgr.RestartAsync(id); return Results.Ok(); });
        api.MapPost("/projects/{id}/open", (string id) =>
        {
            var r = mgr.Find(id);
            if (r == null) return Results.NotFound();
            var v = mgr.BuildView(r);
            var url = v.Runtime.LocalUrl ?? v.Runtime.DirectUrl;
            if (url == null) return Results.BadRequest(new { error = "This project has no port yet." });
            BrowserLauncher.Open(url);
            return Results.Ok(new { url });
        });
        api.MapPost("/projects/{id}/use-detected-port", (string id) =>
        {
            var r = mgr.Find(id);
            if (r?.DetectedPort is not int p) return Results.BadRequest(new { error = "No different port was detected." });
            var input = JsonSerializer.Deserialize<ProjectInput>(JsonSerializer.Serialize(r.Config, Json.Options), Json.Options)!;
            input.Port = p;
            var (view, errs) = mgr.Update(id, input);
            return view == null ? Results.BadRequest(new { errors = errs }) : Results.Ok(new { project = view });
        });
        api.MapPost("/projects/{id}/code/regenerate", (string id) =>
        {
            var r = mgr.Find(id);
            if (r == null) return Results.NotFound();
            var input = JsonSerializer.Deserialize<ProjectInput>(JsonSerializer.Serialize(r.Config, Json.Options), Json.Options)!;
            input.Share.Code = ProjectManager.GenerateCode();
            var (view, errs) = mgr.Update(id, input);
            return view == null ? Results.BadRequest(new { errors = errs }) : Results.Ok(new { project = view });
        });

        // ------------------------------------------------------------- logs
        api.MapGet("/projects/{id}/logs", (string id, int? tail) =>
            mgr.Find(id) is { } r ? Results.Ok(new { lines = r.Logs.Tail(Math.Clamp(tail ?? 500, 1, 3000)) }) : Results.NotFound());
        api.MapDelete("/projects/{id}/logs", (string id) =>
        {
            if (mgr.Find(id) is not { } r) return Results.NotFound();
            r.Logs.Clear();
            return Results.Ok();
        });
        api.MapGet("/projects/{id}/logs/download", (string id) =>
        {
            if (mgr.Find(id) is not { } r) return Results.NotFound();
            var text = string.Join("\n", r.Logs.Tail(3000).Select(l => $"{DateTimeOffset.FromUnixTimeMilliseconds(l.Ts).ToLocalTime():yyyy-MM-dd HH:mm:ss} {l.Text}"));
            return Results.File(Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", id + ".log");
        });
        api.MapGet("/projects/{id}/logs/stream", async (string id, HttpContext ctx) =>
        {
            if (mgr.Find(id) is not { } r) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var ch = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
            void OnLine(LogLine l) => ch.Writer.TryWrite(l);
            r.Logs.Line += OnLine;
            try
            {
                await ctx.Response.WriteAsync("retry: 2000\n\n");
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    wait.CancelAfter(15000);
                    try
                    {
                        if (!await ch.Reader.WaitToReadAsync(wait.Token)) break;
                        var sb = new StringBuilder();
                        var n = 0;
                        while (n++ < 200 && ch.Reader.TryRead(out var l)) sb.Append("event: line\ndata: ").Append(JsonSerializer.Serialize(l, Json.Options)).Append("\n\n");
                        await ctx.Response.WriteAsync(sb.ToString());
                    }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { await ctx.Response.WriteAsync(": ping\n\n"); }
                    await ctx.Response.Body.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally { r.Logs.Line -= OnLine; }
        });

        // ---------------------------------------------------------- tunnels
        api.MapPost("/projects/{id}/tunnel/start", (string id) =>
            mgr.StartTunnel(id, out var err) ? Results.Ok() : Results.BadRequest(new { error = err }));
        api.MapPost("/projects/{id}/tunnel/stop", (string id) => { mgr.StopTunnel(id); return Results.Ok(); });

        // ---------------------------------------------------------- bulk ops
        api.MapPost("/start-all", () => { _ = Task.Run(() => mgr.StartAlwaysOnAsync()); return Results.Ok(); });
        api.MapPost("/stop-all", async () => { await mgr.StopAllAsync(); return Results.Ok(); });

        // -------------------------------------------------------- detection
        api.MapPost("/detect", (PathBody body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest(new { error = "No folder given." });
            var s = Detector.Detect(body.Path);
            if (s == null) return Results.BadRequest(new { error = "Folder not found." });
            s.AlreadyAdded = mgr.FindByPath(s.Path) != null;
            AnnotateConflicts(s, mgr);
            return Results.Ok(s);
        });
        api.MapPost("/scan", (PathBody body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path) || !Directory.Exists(body.Path)) return Results.BadRequest(new { error = "Folder not found." });
            var list = Detector.Scan(body.Path, mgr.FindByPath, AppContext.BaseDirectory);
            foreach (var s in list) AnnotateConflicts(s, mgr);
            return Results.Ok(new { suggestions = list });
        });
        api.MapGet("/fs", (string? path) => Results.Ok(BrowseFolders(path, mgr)));

        // ------------------------------------------------------------ ports
        api.MapGet("/ports", () => Results.Ok(mgr.PortOverview()));
        api.MapPost("/ports/kill", (KillBody b) =>
        {
            var (ok, msg) = mgr.KillPortOwner(b.Port, b.Pid);
            return ok ? Results.Ok(new { message = msg }) : Results.BadRequest(new { error = msg });
        });
        api.MapGet("/ports/suggest", (string? project, int? from) => Results.Ok(new { port = mgr.SuggestPort(project, from) }));

        // --------------------------------------------------------- settings
        api.MapGet("/settings", () => Results.Ok(settings.Current));
        api.MapPut("/settings", (AppSettings s) =>
        {
            var old = settings.Current;
            settings.Update(s);
            if (OperatingSystem.IsWindows() && old.StartWithWindows != s.StartWithWindows) Autostart.Set(s.StartWithWindows);
            var restart = old.DashboardPort != s.DashboardPort || old.ProxyPort != s.ProxyPort || old.ProxyEnabled != s.ProxyEnabled;
            hub.Publish("settings", settings.Current);
            return Results.Ok(new { settings = settings.Current, restartRequired = restart });
        });

        // --------------------------------------------------------- activity
        api.MapGet("/activity", () => Results.Ok(new { items = activity.Recent(300) }));
        api.MapDelete("/activity", () => { activity.Clear(); return Results.Ok(); });

        // ----------------------------------------------------- import/export
        api.MapGet("/export", () => Results.File(JsonSerializer.SerializeToUtf8Bytes(mgr.Export(), Json.Pretty), "application/json", "server-brain-config.json"));
        api.MapPost("/import", async (HttpRequest req, bool? replace) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                var n = mgr.Import(doc.RootElement, replace ?? false);
                hub.Publish("state", Snapshot(mgr, activity, settings, host, dashPort, proxyPort));
                return Results.Ok(new { imported = n });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "That file is not valid JSON." }); }
        });

        // ------------------------------------------------- system check / setup
        var installer = app.Services.GetRequiredService<SystemInstaller>();
        api.MapGet("/system/check", () => Results.Ok(new
        {
            tools = SystemCheck.Run(),
            winget = SystemCheck.WingetAvailable,
            installs = installer.All(),
            ports = new { dashboard = dashPort, dashboardWanted = settings.Current.DashboardPort, names = proxyPort, namesWanted = settings.Current.ProxyPort, namesEnabled = settings.Current.ProxyEnabled },
            startup = OperatingSystem.IsWindows() && Autostart.IsEnabled(),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            dataDir = AppPaths.DataDir,
            exe = Environment.ProcessPath,
        }));
        api.MapPost("/system/install/{id}", (string id) =>
            installer.Start(id, out var err) ? Results.Ok() : Results.BadRequest(new { error = err }));
        api.MapPost("/system/startup", (StartupBody b) =>
        {
            if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "Only available on Windows." });
            Autostart.Set(b.Enabled);
            var s = settings.Current; s.StartWithWindows = b.Enabled; settings.Update(s);
            hub.Publish("settings", settings.Current);
            return Results.Ok(new { enabled = Autostart.IsEnabled() });
        });
        api.MapPost("/backup", () => Results.Ok(new { path = mgr.Backup("manual") }));
        api.MapPost("/reset", async (ResetBody b) =>
        {
            if (b.Confirm != "RESET") return Results.BadRequest(new { error = "Type RESET to confirm." });
            return Results.Ok(new { backup = await mgr.ResetAllAsync() });
        });

        // -------------------------------------------------------- app shell
        api.MapPost("/app/show", () => { host.Shell.ShowWindow(); return Results.Ok(); });
        api.MapPost("/app/quit", () => { _ = Task.Run(async () => { await Task.Delay(300); host.Shell.Quit(); }); return Results.Ok(); });
        api.MapPost("/open-external", (PathBody body) =>
        {
            if (Uri.TryCreate(body.Path, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https")) BrowserLauncher.Open(u.ToString());
            return Results.Ok();
        });
        api.MapPost("/reveal", (PathBody body) =>
        {
            try { if (OperatingSystem.IsWindows() && Directory.Exists(body.Path)) System.Diagnostics.Process.Start("explorer.exe", $"\"{body.Path}\""); } catch { }
            return Results.Ok();
        });
    }

    private static void AnnotateConflicts(Suggestion s, ProjectManager mgr)
    {
        foreach (var p in new[] { s.Port }.Concat(s.ExtraPorts.Select(x => (int?)x)).Where(p => p != null).Select(p => p!.Value))
        {
            foreach (var r in mgr.Runners().Where(r => r.Config.AllPorts.Contains(p) && !string.Equals(r.Config.Path.TrimEnd('\\'), s.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                if (!s.PortConflicts.Contains(r.Config.Name)) s.PortConflicts.Add(r.Config.Name);
            var l = PortTable.ListenerOn(p);
            if (l is { } row && !mgr.Runners().Any(r => r.Pids.Contains(row.Pid)))
                s.Notes.Add($"Port {p} is currently in use by {ProcInfo.Name(row.Pid)} (PID {row.Pid}).");
        }
    }

    private static object Snapshot(ProjectManager mgr, ActivityLog activity, SettingsService settings, HostOptions host, int dashPort, int proxyPort)
    {
        var (cpu, used, total) = SystemStats.Read();
        return new
        {
            version = ServerBrainHost.Version,
            projects = mgr.BuildAll(),
            settings = settings.Current,
            system = new { cpu = Math.Round(cpu, 1), usedMb = Math.Round(used), totalMb = Math.Round(total), cores = Environment.ProcessorCount },
            activity = activity.Recent(40),
            env = new
            {
                platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux",
                dashboardPort = dashPort,
                proxyPort,
                isDesktopApp = host.IsDesktopApp,
                dataDir = AppPaths.DataDir,
                autostart = OperatingSystem.IsWindows() && Autostart.IsEnabled(),
                tools = Tools(settings.Current),
            },
        };
    }

    private static object Tools(AppSettings s)
    {
        static string? Find(params string[] names) => ToolPath.Find(names);
        return new
        {
            node = Find("node"),
            npm = Find("npm"),
            python = Find("python", "python3"),
            php = Find("php"),
            docker = Find("docker"),
            cloudflared = TunnelManager.FindCloudflared(s.CloudflaredPath),
        };
    }

    private static object BrowseFolders(string? path, ProjectManager mgr)
    {
        var drives = OperatingSystem.IsWindows()
            ? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new { name = d.Name.TrimEnd('\\'), path = d.RootDirectory.FullName, label = d.VolumeLabel }).ToList<object>()
            : new List<object> { new { name = "/", path = "/", label = "" } };
        if (string.IsNullOrWhiteSpace(path))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new { path = "", parent = (string?)null, dirs = new List<object>(), drives, home };
        }
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists) return new { path, parent = (string?)null, dirs = new List<object>(), drives, error = "Folder not found." };
            var dirs = new List<object>();
            foreach (var d in di.EnumerateDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || d.Name.StartsWith('.')) continue;
                if (d.Name is "node_modules" or "$RECYCLE.BIN" or "System Volume Information") continue;
                var looksLikeProject = false;
                try { looksLikeProject = d.EnumerateFiles().Any(f => f.Name is "package.json" or "requirements.txt" or "pyproject.toml" or "composer.json" or "index.php" or "docker-compose.yml" or "start.bat" or "run.bat" or "start.ps1" or "app.py" or "server.js"); } catch { }
                dirs.Add(new { name = d.Name, path = d.FullName, project = looksLikeProject, added = mgr.FindByPath(d.FullName) != null });
            }
            return new { path = di.FullName, parent = di.Parent?.FullName, dirs, drives };
        }
        catch (Exception ex)
        {
            return new { path, parent = (string?)null, dirs = new List<object>(), drives, error = ex.Message };
        }
    }
}

public static class Autostart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ServerBrain";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath); return k?.GetValue(ValueName) != null; }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && !exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    k.SetValue(ValueName, $"\"{exe}\" --background");
            }
            else k.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
