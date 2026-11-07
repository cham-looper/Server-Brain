using System.Diagnostics;
using System.Text;

namespace ServerBrain;

/// <summary>
/// Runs ONE project: spawns its command inside a process container, watches it, restarts it when the
/// policy says so, and stops the entire tree on request. All timing is driven by <see cref="Tick"/>,
/// so an idle Server Brain costs next to nothing.
/// </summary>
public sealed class ProjectRunner : IDisposable
{
    private readonly ActivityLog _activity;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IProcessContainer? _container;
    private Process? _root;
    private bool _userStopped;
    private DateTime _startedAt;
    private DateTime? _nextRestart;
    private int _consecutiveFailures;
    private int _portMissTicks;
    private TimeSpan _lastCpu;
    private DateTime _lastCpuAt;
    private bool _warnedStartup;

    public ProjectRunner(ProjectConfig config, ActivityLog activity)
    {
        Config = config;
        _activity = activity;
        Logs = new LogBuffer(config.Id);
    }

    public ProjectConfig Config { get; set; }
    public LogBuffer Logs { get; }
    public ProjectStatus Status { get; private set; } = ProjectStatus.Stopped;
    public int? Pid { get; private set; }
    public int ProcessCount { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public int RestartCount { get; private set; }
    public int? ExitCode { get; private set; }
    public string? Error { get; private set; }
    public string? Warning { get; private set; }
    public int? DetectedPort { get; private set; }
    public DateTime? NextRestartAt => _nextRestart;
    public double Cpu { get; private set; }
    public double MemMb { get; private set; }
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public bool OpenWhenReady { get; set; }
    public string StartTrigger { get; private set; } = "manual";
    /// <summary>Tool id (node, python…) that this project needs but the PC does not have.</summary>
    public string? MissingTool { get; private set; }

    /// <summary>Stops a start attempt because a required program is not installed, with a plain-language reason.</summary>
    public void FailMissingTool(string toolId)
    {
        var name = SystemCheck.ById(toolId)?.Name ?? toolId;
        MissingTool = toolId;
        Error = $"{name} is not installed on this PC, and this project needs it.";
        Logs.Add($"✖ {Error} Install it from the project card or Help → System check.", isError: true, system: true);
        SetStatus(ProjectStatus.Failed);
        _activity.Add("error", "missing-tool", $"{Config.Name} cannot start: {name} is not installed", Config);
    }

    /// <summary>Raised whenever anything the dashboard shows has changed.</summary>
    public event Action<ProjectRunner>? Changed;
    /// <summary>Raised once when the project first becomes ready (port open).</summary>
    public event Action<ProjectRunner>? BecameRunning;

    public bool IsActive => Status is ProjectStatus.Starting or ProjectStatus.Running or ProjectStatus.Stopping or ProjectStatus.Waiting;
    public bool IsUp => Status is ProjectStatus.Starting or ProjectStatus.Running;
    public IReadOnlyList<int> Pids => _container?.ProcessIds ?? Array.Empty<int>();

    /// <summary>The port the project is really answering on (declared port, or the one we saw it open).</summary>
    public int? ActivePort
    {
        get
        {
            if (Config.Port is not int p) return DetectedPort;
            return DetectedPort != null && !PortTable.IsListening(p) ? DetectedPort : p;
        }
    }

    public const string NoPortWarning =
        "The project is running, but Server Brain found no web port for it. Set the port in the project settings (the one your app listens on).";

    /// <summary>
    /// True when the project is up but still has no web port after the whole startup window,
    /// so the browser should be told so instead of waiting on a splash page forever.
    /// </summary>
    public bool PortSearchTimedOut =>
        Status == ProjectStatus.Running && ActivePort is null &&
        StartedAt is DateTime s && (DateTime.UtcNow - s).TotalSeconds > Math.Max(20, Config.StartupTimeoutSec);

    public void Touch() => LastActivity = DateTime.UtcNow;

    private int _activeRequests;
    /// <summary>Requests (including long-lived WebSockets) currently flowing through the proxy.</summary>
    public int ActiveRequests => Volatile.Read(ref _activeRequests);

    /// <summary>Marks a proxied request as in flight; dispose when it ends. An open WebSocket keeps an On-demand project awake.</summary>
    public IDisposable TrackRequest()
    {
        Interlocked.Increment(ref _activeRequests);
        Touch();
        return new RequestToken(this);
    }

    private sealed class RequestToken : IDisposable
    {
        private ProjectRunner? _owner;
        public RequestToken(ProjectRunner owner) => _owner = owner;
        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            if (o == null) return;
            Interlocked.Decrement(ref o._activeRequests);
            o.Touch();
        }
    }

    // ------------------------------------------------------------------ start

    public async Task<bool> StartAsync(string trigger = "manual")
    {
        await _lock.WaitAsync();
        try
        {
            if (Status is ProjectStatus.Starting or ProjectStatus.Running or ProjectStatus.Stopping) return true;
            return StartLocked(trigger, isRestart: false);
        }
        finally { _lock.Release(); }
    }

    private bool StartLocked(string trigger, bool isRestart)
    {
        StartTrigger = trigger;
        Error = null; Warning = null; ExitCode = null; DetectedPort = null; MissingTool = null;
        _nextRestart = null; _userStopped = false; _portMissTicks = 0; _warnedStartup = false;
        Cpu = 0; MemMb = 0;
        if (!isRestart) { RestartCount = 0; _consecutiveFailures = 0; }

        if (string.IsNullOrWhiteSpace(Config.Command))
            return Fail("No start command is set for this project.");
        if (!Directory.Exists(Config.Path))
            return Fail($"Project folder not found: {Config.Path}");

        try
        {
            var psi = BuildStartInfo();
            var container = ProcessContainer.Create();
            Process proc;
            try
            {
                proc = Process.Start(psi) ?? throw new InvalidOperationException("The process did not start.");
                container.Add(proc);
            }
            catch { container.Dispose(); throw; }

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) Logs.Add(e.Data.TrimEnd('\r')); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Logs.Add(e.Data.TrimEnd('\r'), isError: true); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!Config.KeepStdinOpen) { try { proc.StandardInput.Close(); } catch { } }

            ApplyPriority(proc);
            _root = proc;
            _container = container;
            Pid = proc.Id;
            _startedAt = DateTime.UtcNow;
            StartedAt = _startedAt;
            LastActivity = DateTime.UtcNow;
            _lastCpu = TimeSpan.Zero; _lastCpuAt = DateTime.UtcNow;
            Logs.Add($"▶ Starting: {Config.Command}   (in {Config.Path})", system: true);
            SetStatus(ProjectStatus.Starting);
            _activity.Add("info", isRestart ? "restart" : "start", isRestart ? $"{Config.Name} restarting (attempt {_consecutiveFailures})" : $"{Config.Name} starting ({trigger})", Config);
            return true;
        }
        catch (Exception ex)
        {
            return Fail("Could not start: " + ex.Message);
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Config.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/d /s /c \"chcp 65001>nul & " + Config.Command + "\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(Config.Command);
        }
        psi.Environment["PATH"] = ToolPath.FreshPath();   // includes tools installed after Server Brain started
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["SB_PROJECT_ID"] = Config.Id;
        if (!Config.OpenBrowserOnStart && !OpenWhenReady) psi.Environment["BROWSER"] = "none";   // honoured by Vite / CRA style tools
        foreach (var (k, v) in Config.Env)
            if (!string.IsNullOrWhiteSpace(k)) psi.Environment[k.Trim()] = v;
        if (Config.InjectPort && Config.Port is int port)
        {
            psi.Environment[string.IsNullOrWhiteSpace(Config.PortEnvName) ? "PORT" : Config.PortEnvName.Trim()] = port.ToString();
        }
        return psi;
    }

    private void ApplyPriority(Process p)
    {
        try
        {
            p.PriorityClass = Config.Priority switch
            {
                "idle" => ProcessPriorityClass.Idle,
                "belownormal" => ProcessPriorityClass.BelowNormal,
                _ => ProcessPriorityClass.Normal,
            };
        }
        catch { }
    }

    private bool Fail(string message)
    {
        Error = message;
        Logs.Add("✖ " + message, isError: true, system: true);
        SetStatus(ProjectStatus.Failed);
        _activity.Add("error", "start-failed", $"{Config.Name}: {message}", Config);
        return false;
    }

    /// <summary>Called by the manager when it refuses to start (port clash etc.).</summary>
    public void Block(string message)
    {
        Error = message;
        Logs.Add("⛔ " + message, isError: true, system: true);
        SetStatus(ProjectStatus.Blocked);
    }

    // ------------------------------------------------------------------- stop

    public async Task StopAsync(string reason = "stopped")
    {
        await _lock.WaitAsync();
        try
        {
            _nextRestart = null;
            if (Status is ProjectStatus.Stopped) return;
            if (_container == null)
            {
                Error = null; Warning = null;
                SetStatus(ProjectStatus.Stopped);
                return;
            }
            _userStopped = true;
            SetStatus(ProjectStatus.Stopping);
            Logs.Add("■ Stopping (" + reason + ")…", system: true);

            if (!string.IsNullOrWhiteSpace(Config.StopCommand))
                await Task.Run(() => RunHidden(Config.StopCommand, Config.Path, 25_000));

            _container?.KillAll();
            for (var i = 0; i < 50 && (_container?.HasLiveProcesses ?? false); i++) await Task.Delay(100);
            Cleanup();
            Error = null; Warning = null; Cpu = 0; MemMb = 0; ProcessCount = 0;
            SetStatus(ProjectStatus.Stopped);
            _activity.Add("info", "stop", $"{Config.Name} stopped ({reason})", Config);
        }
        finally { _lock.Release(); }
    }

    public async Task RestartAsync()
    {
        await StopAsync("restart");
        await StartAsync("restart");
    }

    private static void RunHidden(string command, string cwd, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (OperatingSystem.IsWindows()) { psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"; psi.Arguments = "/d /s /c \"" + command + "\""; }
            else { psi.FileName = "/bin/sh"; psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }
            using var p = Process.Start(psi);
            if (p == null) return;
            p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } }
        }
        catch { }
    }

    private void Cleanup()
    {
        try { _container?.Dispose(); } catch { }
        _container = null;
        try { _root?.Dispose(); } catch { }
        _root = null;
        Pid = null;
    }

    // ------------------------------------------------------------------- tick

    /// <summary>Heartbeat from the manager (every 1–3 s). Cheap when nothing is running.</summary>
    public void Tick(DateTime now, bool wantMetrics)
    {
        try
        {
            if (Status == ProjectStatus.Waiting)
            {
                if (_nextRestart is DateTime due && now >= due && _lock.Wait(0))
                {
                    try { StartLocked("auto-restart", isRestart: true); }
                    finally { _lock.Release(); }
                }
                return;
            }
            if (Status is not (ProjectStatus.Starting or ProjectStatus.Running)) return;
            if (!_lock.Wait(0)) return;
            try
            {
                var alive = _container?.HasLiveProcesses ?? false;
                if (!alive) { HandleExit(); return; }

                var pids = _container!.ProcessIds;
                ProcessCount = pids.Count;
                var rows = PortTable.Snapshot();
                var mine = rows.Where(r => r.IsListen && pids.Contains(r.Pid)).Select(r => r.LocalPort).Distinct().Order().ToList();
                var elapsed = (now - _startedAt).TotalSeconds;
                var changed = false;

                var wanted = Config.Port;
                bool wantedUp = wanted is int wp && rows.Any(r => r.IsListen && r.LocalPort == wp);

                var newDetected = wanted == null
                    ? mine.FirstOrDefault(p => p >= 1024 && !IsReservedLocal(p), 0)
                    : (!wantedUp ? mine.FirstOrDefault(p => p >= 1024 && !IsReservedLocal(p) && p != wanted, 0) : 0);
                if (newDetected != (DetectedPort ?? 0)) { DetectedPort = newDetected == 0 ? null : newDetected; changed = true; }

                if (Status == ProjectStatus.Starting)
                {
                    var ready = wanted != null ? wantedUp : (DetectedPort != null || elapsed >= 8);
                    if (ready)
                    {
                        Warning = null;
                        Logs.Add(wanted != null
                            ? $"✔ Ready — listening on port {wanted}"
                            : DetectedPort != null ? $"✔ Ready — detected port {DetectedPort}" : "✔ Running (no web port detected)", system: true);
                        _activity.Add("ok", "ready", $"{Config.Name} is up" + (ActivePort is int ap ? $" on port {ap}" : ""), Config);
                        SetStatus(ProjectStatus.Running, quiet: true);
                        changed = true;
                        if (_consecutiveFailures > 0 && elapsed > 60) _consecutiveFailures = 0;
                        try { BecameRunning?.Invoke(this); } catch { }
                    }
                    else if (!_warnedStartup && elapsed > Config.StartupTimeoutSec)
                    {
                        _warnedStartup = true;
                        Warning = DetectedPort != null
                            ? $"Listening on port {DetectedPort}, but this project is set to port {wanted}. Fix the port in the project settings."
                            : $"Still waiting for port {wanted} after {Config.StartupTimeoutSec}s. First runs may install dependencies; check the Logs tab.";
                        _activity.Add("warn", "slow-start", $"{Config.Name}: {Warning}", Config);
                        changed = true;
                    }
                }
                else // Running
                {
                    if (wanted != null && !wantedUp)
                    {
                        if (++_portMissTicks == 3)
                        {
                            Warning = DetectedPort != null
                                ? $"Listening on port {DetectedPort} instead of {wanted}."
                                : $"Process is running but port {wanted} is not open.";
                            changed = true;
                        }
                    }
                    else if (_portMissTicks != 0) { _portMissTicks = 0; if (Warning != null) { Warning = null; changed = true; } }
                    else if (wanted == null)
                    {
                        // no port declared: say so when nothing was found, and clear it when the port shows up
                        if (DetectedPort == null && Warning == null && elapsed > Math.Max(20, Config.StartupTimeoutSec)) { Warning = NoPortWarning; changed = true; }
                        else if (DetectedPort != null && Warning == NoPortWarning) { Warning = null; changed = true; }
                    }
                    if (elapsed > 60 && _consecutiveFailures > 0) _consecutiveFailures = 0;
                }

                if (wantMetrics || Config.MemoryLimitMb > 0) { SampleMetrics(pids, now); changed = true; }
                if (Config.MemoryLimitMb > 0 && MemMb > Config.MemoryLimitMb)
                {
                    Warning = $"Memory limit of {Config.MemoryLimitMb} MB exceeded ({MemMb:F0} MB). Restarting.";
                    _activity.Add("warn", "memory-limit", $"{Config.Name} exceeded its {Config.MemoryLimitMb} MB limit and is being restarted", Config);
                    _ = Task.Run(RestartAsync);
                    return;
                }
                if (changed) RaiseChanged();
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex)
        {
            Logs.Add("tick error: " + ex.Message, isError: true, system: true);
        }
    }

    private static bool IsReservedLocal(int p) => p == ServerBrainHostInfo.DashboardPort || p == ServerBrainHostInfo.ProxyPort;

    private void SampleMetrics(IReadOnlyList<int> pids, DateTime now)
    {
        long ws = 0;
        foreach (var id in pids.Take(64))
        {
            try { using var p = Process.GetProcessById(id); ws += p.WorkingSet64; } catch { }
        }
        MemMb = ws / 1048576.0;
        var cpuNow = _container?.TotalCpuTime ?? TimeSpan.Zero;
        var dt = (now - _lastCpuAt).TotalSeconds;
        if (dt > 0.2 && _lastCpuAt != default)
        {
            Cpu = Math.Clamp((cpuNow - _lastCpu).TotalSeconds / dt / Environment.ProcessorCount * 100.0, 0, 100);
        }
        _lastCpu = cpuNow; _lastCpuAt = now;
    }

    private void HandleExit()
    {
        int? code = null;
        try { if (_root is { HasExited: true }) code = _root.ExitCode; } catch { }
        ExitCode = code;
        var ranFor = DateTime.UtcNow - _startedAt;
        Cleanup();
        Cpu = 0; MemMb = 0; ProcessCount = 0;

        if (_userStopped) { SetStatus(ProjectStatus.Stopped); return; }

        var failed = code is null or not 0;
        Logs.Add($"■ Process exited" + (code != null ? $" with code {code}" : "") + $" after {FormatSpan(ranFor)}", isError: failed, system: true);

        // "'node' is not recognized…" in the output means a program is missing: say so clearly (and don't retry in a loop).
        if (failed)
        {
            var missing = CommandAnalyzer.MissingToolFromOutput(Logs.Tail(60).Select(l => l.Text));
            if (missing != null && !CommandAnalyzer.IsInstalled(missing))
            {
                MissingTool = missing;
                Error = $"{SystemCheck.ById(missing)?.Name ?? missing} is not installed on this PC, and this project needs it.";
                _activity.Add("error", "missing-tool", $"{Config.Name}: {Error}", Config);
                SetStatus(ProjectStatus.Failed);
                return;
            }
        }

        var wantRestart = Config.Mode != RunMode.OnDemand &&
                          (Config.Restart == RestartPolicy.Always || (Config.Restart == RestartPolicy.OnFailure && failed));
        if (ranFor.TotalSeconds > 60) _consecutiveFailures = 0;

        if (wantRestart && _consecutiveFailures < Config.MaxRestarts)
        {
            _consecutiveFailures++;
            RestartCount++;
            var delay = Math.Min(60, (int)Math.Pow(2, _consecutiveFailures));
            _nextRestart = DateTime.UtcNow.AddSeconds(delay);
            Error = $"Exited{(code != null ? $" (code {code})" : "")}. Restarting in {delay}s (attempt {_consecutiveFailures}/{Config.MaxRestarts}).";
            _activity.Add("warn", "crash", $"{Config.Name} exited{(code != null ? $" with code {code}" : "")}; restarting in {delay}s", Config);
            SetStatus(ProjectStatus.Waiting);
            return;
        }

        if (!failed)
        {
            Error = null;
            _activity.Add("info", "exit", $"{Config.Name} finished (exit code 0)", Config);
            SetStatus(ProjectStatus.Stopped);
            return;
        }

        Error = wantRestart
            ? $"Gave up after {Config.MaxRestarts} restarts. Last exit code: {code?.ToString() ?? "unknown"}."
            : $"Exited unexpectedly{(code != null ? $" (code {code})" : "")}.";
        _activity.Add("error", "crash", $"{Config.Name}: {Error}", Config);
        SetStatus(wantRestart ? ProjectStatus.Failed : ProjectStatus.Crashed);
    }

    // ---------------------------------------------------------------- helpers

    private void SetStatus(ProjectStatus s, bool quiet = false)
    {
        if (Status == s && quiet) return;
        Status = s;
        if (s is ProjectStatus.Stopped or ProjectStatus.Failed or ProjectStatus.Crashed or ProjectStatus.Blocked) { Pid = null; StartedAt = null; }
        RaiseChanged();
    }

    private void RaiseChanged() { try { Changed?.Invoke(this); } catch { } }

    public static string FormatSpan(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{t.TotalSeconds:F0}s" : t.TotalMinutes < 90 ? $"{t.TotalMinutes:F0}m" : $"{t.TotalHours:F1}h";

    public void Dispose()
    {
        try { _container?.KillAll(); } catch { }
        Cleanup();
        Logs.Dispose();
    }
}

/// <summary>Ports Server Brain itself uses; projects must not be auto-detected onto them.</summary>
public static class ServerBrainHostInfo
{
    public static int DashboardPort { get; set; } = 7777;
    public static int ProxyPort { get; set; } = 80;
}
