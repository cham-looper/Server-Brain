using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ServerBrain;

public sealed class SettingsService
{
    private readonly JsonFile<AppSettings> _file = new(AppPaths.SettingsFile);
    public AppSettings Current { get; private set; }

    public SettingsService() => Current = _file.Load();

    public void Update(AppSettings s)
    {
        s.DashboardPort = Math.Clamp(s.DashboardPort, 1024, 65535);
        s.ProxyPort = Math.Clamp(s.ProxyPort, 1, 65535);
        s.StaggerSeconds = Math.Clamp(s.StaggerSeconds, 0, 120);
        s.DefaultIdleMinutes = Math.Clamp(s.DefaultIdleMinutes, 1, 24 * 60);
        Current = s;
        _file.Save(s);
    }

    public void Save() => _file.Save(Current);
}

public sealed record StartResult(bool Ok, string? Error = null, bool Blocked = false, List<ConflictInfo>? Conflicts = null);

public static class BrowserLauncher
{
    public static void Open(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch { }
    }
}

/// <summary>The brain: owns every project's runner, config, conflicts, auto-start and idle shutdown.</summary>
public sealed class ProjectManager : IDisposable
{
    private readonly Dictionary<string, ProjectRunner> _runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly JsonFile<StateFile> _file = new(AppPaths.StateFile);
    private readonly EventHub _hub;
    private readonly ActivityLog _activity;
    private readonly TunnelManager _tunnels;
    private readonly SettingsService _settings;
    private readonly CancellationTokenSource _cts = new();
    private string _lastConflictJson = "";
    private DateTime _lastIdleCheck = DateTime.MinValue;
    private DateTime _lastConflictPublish = DateTime.MinValue;

    public static readonly string[] ReservedIds = { "brain", "api", "www", "localhost", "dashboard", "admin" };

    public ProjectManager(EventHub hub, ActivityLog activity, TunnelManager tunnels, SettingsService settings)
    {
        _hub = hub; _activity = activity; _tunnels = tunnels; _settings = settings;
        foreach (var c in _file.Load().Projects) AddRunner(c);
        _tunnels.Changed += id => { var r = Find(id); if (r != null) PublishProject(r); };
    }

    public AppSettings Settings => _settings.Current;
    public EventHub Hub => _hub;
    public TunnelManager Tunnels => _tunnels;
    public ActivityLog Activity => _activity;

    // ------------------------------------------------------------- lookup

    public ProjectRunner? Find(string id)
    {
        lock (_gate) return _runners.TryGetValue(id, out var r) ? r : null;
    }

    public List<ProjectRunner> Runners()
    {
        lock (_gate) return _runners.Values.OrderBy(r => r.Config.Order).ThenBy(r => r.Config.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ProjectRunner? FindByHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host.EndsWith(".localhost")) return Find(host[..^".localhost".Length]);
        lock (_gate)
            return _runners.Values.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Config.Share.Hostname)
                && string.Equals(r.Config.Share.Hostname.Trim(), host, StringComparison.OrdinalIgnoreCase));
    }

    public ProjectConfig? FindByPath(string path)
    {
        var norm = path.TrimEnd('\\', '/');
        lock (_gate)
            return _runners.Values.Select(r => r.Config).FirstOrDefault(c => string.Equals(c.Path.TrimEnd('\\', '/'), norm, StringComparison.OrdinalIgnoreCase));
    }

    private ProjectRunner AddRunner(ProjectConfig c)
    {
        var r = new ProjectRunner(c, _activity);
        r.Changed += PublishProject;
        r.BecameRunning += OnBecameRunning;
        lock (_gate) _runners[c.Id] = r;
        return r;
    }

    private void OnBecameRunning(ProjectRunner r)
    {
        var open = r.OpenWhenReady || (r.Config.OpenBrowserOnStart && r.StartTrigger != "on-demand" && r.StartTrigger != "auto-restart");
        r.OpenWhenReady = false;
        if (!open) return;
        var view = BuildView(r);
        var url = view.Runtime.LocalUrl ?? view.Runtime.DirectUrl;
        if (url != null) BrowserLauncher.Open(url);
    }

    // -------------------------------------------------------------- views

    public ProjectView BuildView(ProjectRunner r, IReadOnlyList<TcpRow>? rows = null, Dictionary<int, ProjectRunner>? pidMap = null)
    {
        rows ??= PortTable.Snapshot();
        var s = _settings.Current;
        var cfg = r.Config;
        var port = r.ActivePort;
        string? local = null;
        if (s.ProxyEnabled && ServerBrainHostInfo.ProxyPort != 0)
            local = $"http://{cfg.Id}.localhost{(ServerBrainHostInfo.ProxyPort == 80 ? "" : ":" + ServerBrainHostInfo.ProxyPort)}{NormPath(cfg.UrlPath)}";
        var rt = new ProjectRuntime
        {
            Status = r.Status,
            Pid = r.Pid,
            ProcessCount = r.ProcessCount,
            StartedAt = r.StartedAt is DateTime d ? new DateTimeOffset(d, TimeSpan.Zero).ToUnixTimeMilliseconds() : null,
            EffectivePort = port,
            DetectedPort = r.DetectedPort,
            RestartCount = r.RestartCount,
            Cpu = Math.Round(r.Cpu, 1),
            MemMb = Math.Round(r.MemMb, 0),
            ExitCode = r.ExitCode,
            Error = r.Error,
            MissingTool = r.MissingTool,
            MissingToolName = r.MissingTool != null ? SystemCheck.ById(r.MissingTool)?.Name : null,
            Warning = r.Warning,
            NextRestartAt = r.NextRestartAt is DateTime n ? new DateTimeOffset(n, TimeSpan.Zero).ToUnixTimeMilliseconds() : null,
            LastActivity = new DateTimeOffset(r.LastActivity, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            LocalUrl = local,
            DirectUrl = port != null ? $"http://127.0.0.1:{port}{NormPath(cfg.UrlPath)}" : null,
            Conflicts = ComputeConflicts(r, rows, pidMap),
            Tunnel = _tunnels.Get(cfg.Id),
        };
        return new ProjectView { Config = cfg.ToPublic(), Runtime = rt };
    }

    public List<ProjectView> BuildAll()
    {
        var rows = PortTable.Snapshot();
        var runners = Runners();
        var pidMap = PidMap(runners);
        return runners.Select(r => BuildView(r, rows, pidMap)).ToList();
    }

    private static string NormPath(string p) => string.IsNullOrWhiteSpace(p) ? "/" : (p.StartsWith('/') ? p : "/" + p);

    private static Dictionary<int, ProjectRunner> PidMap(IEnumerable<ProjectRunner> runners)
    {
        var map = new Dictionary<int, ProjectRunner>();
        foreach (var r in runners.Where(r => r.IsActive))
            foreach (var pid in r.Pids) map[pid] = r;
        return map;
    }

    private void PublishProject(ProjectRunner r) => _hub.Publish("project", BuildView(r));

    // ---------------------------------------------------------- conflicts

    public List<ConflictInfo> ComputeConflicts(ProjectRunner r, IReadOnlyList<TcpRow>? rows = null, Dictionary<int, ProjectRunner>? pidMap = null)
    {
        rows ??= PortTable.Snapshot();
        var list = new List<ConflictInfo>();
        var s = _settings.Current;
        var all = Runners();
        pidMap ??= PidMap(all);
        foreach (var port in r.Config.AllPorts.Distinct())
        {
            if (port == s.DashboardPort || (s.ProxyEnabled && port == s.ProxyPort))
            {
                list.Add(new ConflictInfo(port, "reserved", "error", $"Port {port} is used by Server Brain itself. Pick another port."));
                continue;
            }
            var l = rows.FirstOrDefault(x => x.IsListen && x.LocalPort == port);
            if (l.IsListen)
            {
                pidMap.TryGetValue(l.Pid, out var owner);
                if (owner == r) { /* it is ours, fine */ }
                else if (owner != null)
                    list.Add(new ConflictInfo(port, "in-use", "error", $"Port {port} is in use by the running project “{owner.Config.Name}”.", owner.Config.Id, l.Pid, ProcInfo.Name(l.Pid)));
                else if (!r.IsUp)
                {
                    var pn = ProcInfo.Name(l.Pid);
                    list.Add(new ConflictInfo(port, "in-use", "error", $"Port {port} is already in use by {pn} (PID {l.Pid}).", null, l.Pid, pn));
                }
                else if (!r.Pids.Contains(l.Pid))
                {
                    var pn = ProcInfo.Name(l.Pid);
                    list.Add(new ConflictInfo(port, "mismatch", "warning", $"Port {port} is held by {pn} (PID {l.Pid}), not by this project.", null, l.Pid, pn));
                }
            }
            foreach (var o in all.Where(o => o != r && o.Config.AllPorts.Contains(port)))
            {
                var both = r.Config.Mode == RunMode.Always && o.Config.Mode == RunMode.Always;
                var sev = both || o.IsUp ? "error" : "warning";
                var msg = both
                    ? $"Port {port} is also assigned to “{o.Config.Name}” and both start automatically. Only one can win. Change one port."
                    : o.IsUp
                        ? $"Port {port} is assigned to “{o.Config.Name}”, which is running right now."
                        : $"Port {port} is also assigned to “{o.Config.Name}”. They can't run at the same time.";
                if (list.Any(c => c.Port == port && c.OtherProjectId == o.Config.Id)) continue;
                list.Add(new ConflictInfo(port, "duplicate", sev, msg, o.Config.Id));
            }
        }
        return list;
    }

    public object ConflictMap()
    {
        var rows = PortTable.Snapshot();
        var runners = Runners();
        var pidMap = PidMap(runners);
        return runners.ToDictionary(r => r.Config.Id, r => ComputeConflicts(r, rows, pidMap));
    }

    // --------------------------------------------------------------- CRUD

    public List<string> Validate(ProjectConfig c, bool creating)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Name)) errs.Add("Give the project a name.");
        if (string.IsNullOrWhiteSpace(c.Path)) errs.Add("Choose the project folder.");
        else if (!Directory.Exists(c.Path)) errs.Add($"Folder not found: {c.Path}");
        if (string.IsNullOrWhiteSpace(c.Command)) errs.Add("Enter the command that starts the project.");
        if (c.Port is int p && (p < 1 || p > 65535)) errs.Add("Port must be between 1 and 65535.");
        if (c.ExtraPorts.Any(x => x < 1 || x > 65535)) errs.Add("An extra port is out of range.");
        if (creating)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(c.Id, "^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")) errs.Add("The short name may only use a-z, 0-9 and dashes (it becomes http://name.localhost).");
            else if (ReservedIds.Contains(c.Id)) errs.Add($"“{c.Id}” is reserved.");
            else if (Find(c.Id) != null) errs.Add($"A project named “{c.Id}” already exists.");
        }
        if (c.Share.Mode == ShareMode.Named && string.IsNullOrWhiteSpace(c.Share.Hostname)) errs.Add("Enter the public hostname for the named tunnel.");
        return errs;
    }

    public (ProjectView? view, List<string> errors) Create(ProjectInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Id)) input.Id = Detector.Slug(input.Name);
        input.Id = input.Id.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(input.Name)) input.Name = input.Id;
        // make the slug unique when it came from the name
        var baseId = input.Id; var n = 2;
        while ((Find(input.Id) != null || ReservedIds.Contains(input.Id)) && n < 50) input.Id = $"{baseId}-{n++}";
        Normalize(input, null);
        var errs = Validate(input, creating: true);
        if (errs.Count > 0) return (null, errs);
        input.Order = Runners().Select(r => r.Config.Order).DefaultIfEmpty(0).Max() + 1;
        var cfg = input.Clone();
        cfg.Share.TokenEnc = string.IsNullOrWhiteSpace(input.ShareToken) ? null : Secrets.Protect(input.ShareToken!.Trim());
        var r = AddRunner(cfg);
        Save();
        _activity.Add("info", "added", $"Added project {cfg.Name}", cfg);
        var v = BuildView(r);
        _hub.Publish("project", v);
        return (v, errs);
    }

    public (ProjectView? view, List<string> errors) Update(string id, ProjectInput input)
    {
        var r = Find(id);
        if (r == null) return (null, new List<string> { "Project not found." });
        input.Id = r.Config.Id;
        Normalize(input, r.Config);
        var errs = Validate(input, creating: false);
        if (errs.Count > 0) return (null, errs);
        var cfg = input.Clone();
        cfg.Order = input.Order == 0 ? r.Config.Order : input.Order;
        if (input.ClearShareToken) cfg.Share.TokenEnc = null;
        else if (!string.IsNullOrWhiteSpace(input.ShareToken)) cfg.Share.TokenEnc = Secrets.Protect(input.ShareToken!.Trim());
        else cfg.Share.TokenEnc = r.Config.Share.TokenEnc;
        r.Config = cfg;
        Save();
        _activity.Add("info", "edited", $"Updated {cfg.Name}", cfg);
        var v = BuildView(r);
        _hub.Publish("project", v);
        return (v, errs);
    }

    private void Normalize(ProjectConfig c, ProjectConfig? old)
    {
        c.Name = (c.Name ?? "").Trim();
        c.Path = (c.Path ?? "").Trim().Trim('"');
        c.Command = (c.Command ?? "").Trim();
        c.StopCommand = (c.StopCommand ?? "").Trim();
        c.UrlPath = string.IsNullOrWhiteSpace(c.UrlPath) ? "/" : c.UrlPath.Trim();
        c.Env ??= new(); c.ExtraPorts ??= new(); c.DependsOn ??= new(); c.Share ??= new();
        c.ExtraPorts = c.ExtraPorts.Distinct().Where(p => p != c.Port).ToList();
        c.DependsOn = c.DependsOn.Where(d => !string.Equals(d, c.Id, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        c.IdleMinutes = Math.Clamp(c.IdleMinutes <= 0 ? _settings.Current.DefaultIdleMinutes : c.IdleMinutes, 1, 24 * 60);
        c.MaxRestarts = Math.Clamp(c.MaxRestarts, 0, 100);
        c.StartupTimeoutSec = Math.Clamp(c.StartupTimeoutSec <= 0 ? 90 : c.StartupTimeoutSec, 10, 3600);
        c.StartDelaySec = Math.Clamp(c.StartDelaySec, 0, 600);
        c.MemoryLimitMb = Math.Max(0, c.MemoryLimitMb);
        if (c.Priority is not ("idle" or "belownormal" or "normal")) c.Priority = "normal";
        if (string.IsNullOrWhiteSpace(c.PortEnvName)) c.PortEnvName = "PORT";
        if (c.Share.Mode != ShareMode.Off && c.Share.RequireCode && string.IsNullOrWhiteSpace(c.Share.Code))
            c.Share.Code = GenerateCode();
        if (c.Share.Mode == ShareMode.Off) c.Share.AutoStart = false;
    }

    public static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 8).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    public async Task<bool> RemoveAsync(string id)
    {
        var r = Find(id);
        if (r == null) return false;
        _tunnels.Stop(id, quiet: true);
        await r.StopAsync("removed");
        lock (_gate) _runners.Remove(id);
        foreach (var o in Runners()) o.Config.DependsOn.RemoveAll(d => d == id);
        Save();
        _activity.Add("info", "removed", $"Removed project {r.Config.Name}", r.Config);
        r.Dispose();
        _hub.Publish("removed", new { id });
        return true;
    }

    public void Reorder(IList<string> ids)
    {
        var i = 1;
        foreach (var id in ids) { var r = Find(id); if (r != null) r.Config.Order = i++; }
        Save();
        _hub.Publish("state", new { projects = BuildAll() });
    }

    private void Save()
    {
        lock (_gate) _file.Save(new StateFile { Projects = _runners.Values.Select(r => r.Config).OrderBy(c => c.Order).ToList() });
    }

    // --------------------------------------------------------- start / stop

    public async Task<StartResult> StartAsync(string id, string trigger = "manual", bool openWhenReady = false, bool force = false)
    {
        var r = Find(id);
        if (r == null) return new StartResult(false, "Project not found.");
        if (r.IsUp) return new StartResult(true);

        // dependencies first
        foreach (var depId in r.Config.DependsOn)
        {
            var dep = Find(depId);
            if (dep == null || dep.IsUp) continue;
            var res = await StartAsync(depId, "dependency", false, force);
            if (!res.Ok) return new StartResult(false, $"Dependency “{dep.Config.Name}” could not start: {res.Error}", res.Blocked, res.Conflicts);
            for (var i = 0; i < 60 && dep.Status == ProjectStatus.Starting; i++) await Task.Delay(1000);
        }

        // a required program (node, python, php…) that is not installed: explain it instead of a cryptic shell error
        var missingTool = CommandAnalyzer.MissingTool(r.Config.Command);
        if (missingTool != null)
        {
            r.FailMissingTool(missingTool);
            _hub.Publish("toast", new { level = "error", title = "Program missing", message = r.Error, id });
            return new StartResult(false, r.Error);
        }

        if (!force)
        {
            var blockers = ComputeConflicts(r, PortTable.Snapshot(fresh: true))
                .Where(c => c.Kind is "in-use" or "reserved" || (c.Kind == "duplicate" && c.Severity == "error" && Find(c.OtherProjectId ?? "")?.IsUp == true))
                .ToList();
            if (blockers.Count > 0)
            {
                var msg = string.Join(" ", blockers.Select(b => b.Message));
                r.Block(msg);
                _activity.Add("error", "port-conflict", $"{r.Config.Name} was not started: {msg}", r.Config);
                _hub.Publish("toast", new { level = "error", title = "Port conflict", message = $"{r.Config.Name}: {msg}", id });
                return new StartResult(false, msg, true, blockers);
            }
        }
        r.OpenWhenReady = openWhenReady && _settings.Current.AutoOpenBrowser;
        var ok = await r.StartAsync(trigger);
        return new StartResult(ok, ok ? null : r.Error);
    }

    public async Task StopAsync(string id, string reason = "stopped by you")
    {
        var r = Find(id);
        if (r != null) await r.StopAsync(reason);
    }

    public async Task RestartAsync(string id)
    {
        var r = Find(id);
        if (r == null) return;
        await r.StopAsync("restart");
        await StartAsync(id, "restart", false, force: false);
    }

    public async Task StopAllAsync()
    {
        await Task.WhenAll(Runners().Where(r => r.IsActive || r.Status == ProjectStatus.Waiting).Select(r => r.StopAsync("stop all")));
    }

    public async Task StartAlwaysOnAsync(CancellationToken ct = default)
    {
        var list = Runners().Where(r => r.Config.Mode == RunMode.Always && !r.IsUp).ToList();
        if (list.Count > 0) _activity.Add("info", "autostart", $"Starting {list.Count} always-on project{(list.Count == 1 ? "" : "s")}…");
        var first = true;
        foreach (var r in list)
        {
            if (ct.IsCancellationRequested) break;
            if (!first) await Task.Delay(TimeSpan.FromSeconds(Math.Max(_settings.Current.StaggerSeconds, r.Config.StartDelaySec)), ct).ContinueWith(_ => { });
            else if (r.Config.StartDelaySec > 0) await Task.Delay(TimeSpan.FromSeconds(r.Config.StartDelaySec), ct).ContinueWith(_ => { });
            first = false;
            await StartAsync(r.Config.Id, "boot");
        }
        foreach (var r in Runners().Where(r => r.Config.Share.Mode != ShareMode.Off && r.Config.Share.AutoStart))
            StartTunnel(r.Config.Id, out _);
    }

    // ------------------------------------------------------------- tunnels

    public bool StartTunnel(string id, out string? error)
    {
        error = null;
        var r = Find(id);
        if (r == null) { error = "Project not found."; return false; }
        int? proxy = _settings.Current.ProxyEnabled && ServerBrainHostInfo.ProxyPort != 0 ? ServerBrainHostInfo.ProxyPort : null;
        return _tunnels.Start(r.Config, proxy, r.ActivePort, out error);
    }

    public void StopTunnel(string id) => _tunnels.Stop(id);

    // ------------------------------------------------------------ port tools

    public (bool ok, string message) KillPortOwner(int port, int pid)
    {
        var l = PortTable.Listeners(fresh: true).FirstOrDefault(x => x.LocalPort == port && x.Pid == pid);
        if (l.Pid == 0 || !l.IsListen) return (false, "That process is no longer listening on this port.");
        if (ProcInfo.IsProtected(pid)) return (false, "That is a protected system process and will not be stopped.");
        var owner = Runners().FirstOrDefault(r => r.Pids.Contains(pid));
        if (owner != null) return (false, $"That is the running project “{owner.Config.Name}”. Stop it from its card instead.");
        try
        {
            var name = ProcInfo.Name(pid);
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            _activity.Add("warn", "kill-port", $"Stopped {name} (PID {pid}) to free port {port}");
            return (true, $"Stopped {name} (PID {pid}).");
        }
        catch (Exception ex) { return (false, "Could not stop it: " + ex.Message); }
    }

    public int SuggestPort(string? projectId, int? from)
    {
        var reserved = Runners().Where(r => r.Config.Id != projectId).SelectMany(r => r.Config.AllPorts).ToList();
        reserved.Add(_settings.Current.DashboardPort);
        reserved.Add(_settings.Current.ProxyPort);
        return PortTable.FindFree(from ?? 3000, reserved);
    }

    public object PortOverview()
    {
        var rows = PortTable.Listeners(fresh: true).ToList();
        var runners = Runners();
        var pidMap = PidMap(runners);
        var cache = new Dictionary<int, (string name, string? path)>();
        (string name, string? path) Info(int pid)
        {
            if (!cache.TryGetValue(pid, out var v)) cache[pid] = v = (ProcInfo.Name(pid), ProcInfo.Path(pid));
            return v;
        }
        var configured = runners.SelectMany(r => r.Config.AllPorts.Select(p => (port: p, r))).ToLookup(x => x.port, x => x.r);
        var listening = rows.GroupBy(r => r.LocalPort).Select(g =>
        {
            var first = g.First();
            pidMap.TryGetValue(first.Pid, out var owner);
            var (name, path) = Info(first.Pid);
            return new
            {
                port = g.Key,
                pid = first.Pid,
                process = name,
                path,
                address = string.Join(", ", g.Select(x => x.LocalAddress).Distinct()),
                project = owner?.Config.Id,
                projectName = owner?.Config.Name,
                configuredFor = configured[g.Key].Select(x => x.Config.Name).ToList(),
                isSelf = first.Pid == Environment.ProcessId,
                protectedPid = ProcInfo.IsProtected(first.Pid),
            };
        }).OrderBy(x => x.port).ToList();
        var listeningPorts = listening.Select(l => l.port).ToHashSet();
        var planned = runners.SelectMany(r => r.Config.AllPorts.Select(p => new
        {
            port = p, project = r.Config.Id, projectName = r.Config.Name,
            status = r.Status.ToString().ToLowerInvariant(),
            listening = listeningPorts.Contains(p),
        })).OrderBy(x => x.port).ToList();
        return new { listening, planned };
    }

    // ------------------------------------------------------- backup / reset

    /// <summary>Writes the whole setup (projects + settings, tokens excluded) to the backups folder and returns the file path.</summary>
    public string Backup(string reason = "manual")
    {
        var file = Path.Combine(AppPaths.BackupsDir, $"server-brain-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(Export(), Json.Pretty));
        // keep the 15 newest backups
        foreach (var old in new DirectoryInfo(AppPaths.BackupsDir).GetFiles("server-brain-*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(15))
            try { old.Delete(); } catch { }
        return file;
    }

    /// <summary>Factory reset: backs up first, stops and removes every project and restores default settings.</summary>
    public async Task<string> ResetAllAsync()
    {
        var backup = Backup("before-reset");
        foreach (var r in Runners()) await RemoveAsync(r.Config.Id);
        _settings.Update(new AppSettings());
        _activity.Clear();
        _activity.Add("info", "reset", "Server Brain was reset to a clean state. A backup of the previous setup was saved.");
        _hub.Publish("settings", _settings.Current);
        _hub.Publish("state", new { projects = BuildAll() });
        return backup;
    }

    // ---------------------------------------------------------- import/export

    public object Export() => new
    {
        app = "server-brain", version = 1,
        settings = _settings.Current,
        projects = Runners().Select(r => r.Config.ToPublic()).ToList(),
    };

    public int Import(JsonElement doc, bool replaceExisting)
    {
        var count = 0;
        if (!doc.TryGetProperty("projects", out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;
        foreach (var el in arr.EnumerateArray())
        {
            ProjectInput? p;
            try { p = JsonSerializer.Deserialize<ProjectInput>(el.GetRawText(), Json.Options); } catch { continue; }
            if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Detector.Slug(p.Name);
            var existing = Find(p.Id);
            if (existing != null) { if (replaceExisting) { Update(p.Id, p); count++; } continue; }
            if (Create(p).view != null) count++;
        }
        return count;
    }

    // ------------------------------------------------------------ heartbeat

    public Task RunLoopAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            var viewers = _hub.SubscriberCount > 0;
            var runners = Runners();
            var anyActive = runners.Any(r => r.IsActive);
            try { await Task.Delay(viewers ? 1000 : anyActive ? 2000 : 5000, ct); } catch { break; }
            try
            {
                var now = DateTime.UtcNow;
                if (anyActive || viewers) PortTable.Snapshot(fresh: true);
                foreach (var r in runners) r.Tick(now, viewers);
                if (viewers) PublishMetrics(runners);
                if ((now - _lastIdleCheck).TotalSeconds >= 15) { _lastIdleCheck = now; await IdleCheckAsync(runners, now); }
            }
            catch (Exception ex) { _activity.Add("error", "loop", "Internal heartbeat error: " + ex.Message); }
        }
    }, ct);

    private void PublishMetrics(List<ProjectRunner> runners)
    {
        var (cpu, used, total) = SystemStats.Read();
        _hub.Publish("metrics", new
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            system = new { cpu = Math.Round(cpu, 1), usedMb = Math.Round(used), totalMb = Math.Round(total), running = runners.Count(r => r.IsUp), total = runners.Count },
            projects = runners.Where(r => r.IsActive).Select(r => new
            {
                id = r.Config.Id, status = r.Status, cpu = Math.Round(r.Cpu, 1), memMb = Math.Round(r.MemMb), procs = r.ProcessCount, port = r.ActivePort,
            }),
        });
        if ((DateTime.UtcNow - _lastConflictPublish).TotalSeconds >= 4)
        {
            _lastConflictPublish = DateTime.UtcNow;
            var map = ConflictMap();
            var json = JsonSerializer.Serialize(map, Json.Options);
            if (json != _lastConflictJson) { _lastConflictJson = json; _hub.Publish("conflicts", map); }
        }
    }

    private async Task IdleCheckAsync(List<ProjectRunner> runners, DateTime now)
    {
        var rows = PortTable.Snapshot();
        foreach (var r in runners.Where(r => r.Config.Mode == RunMode.OnDemand && r.Status == ProjectStatus.Running))
        {
            if (r.ActivePort is int p)
            {
                var pids = r.Pids;
                // a browser talking to the project's port directly also counts as "in use"
                if (rows.Any(x => x.State == TcpRow.Established && x.RemotePort == p && x.Pid != Environment.ProcessId && !pids.Contains(x.Pid)))
                    r.Touch();
            }
            if (r.ActiveRequests > 0) r.Touch();   // an open WebSocket / streaming request counts as a visitor
            var idle = r.Config.IdleMinutes > 0 ? r.Config.IdleMinutes : _settings.Current.DefaultIdleMinutes;
            if ((now - r.LastActivity).TotalMinutes >= idle)
            {
                _activity.Add("info", "idle-stop", $"{r.Config.Name} stopped after {idle} min without visitors (on-demand)", r.Config);
                await r.StopAsync($"idle for {idle} min");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _tunnels.Dispose();
        foreach (var r in Runners()) { try { r.Dispose(); } catch { } }
        _activity.Flush();
    }
}
