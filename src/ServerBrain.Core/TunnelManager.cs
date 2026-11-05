using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ServerBrain;

/// <summary>Runs one cloudflared process per shared project and tracks its public URL.</summary>
public sealed class TunnelManager : IDisposable
{
    private sealed class Tunnel
    {
        public Process? Proc;
        public TunnelInfo Info = new();
        public string? Hostname;
    }

    private readonly Dictionary<string, Tunnel> _tunnels = new();
    private readonly object _gate = new();
    private readonly ActivityLog _activity;
    private readonly Func<AppSettings> _settings;
    public event Action<string>? Changed;

    public TunnelManager(ActivityLog activity, Func<AppSettings> settings)
    {
        _activity = activity;
        _settings = settings;
    }

    public static string? FindCloudflared(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var exe = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        var onPath = ToolPath.Find("cloudflared");
        if (onPath != null) return onPath;
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cloudflared", exe));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cloudflared", exe));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", exe));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public TunnelInfo Get(string id)
    {
        lock (_gate) return _tunnels.TryGetValue(id, out var t) ? Copy(t.Info) : new TunnelInfo();
    }

    public bool IsPublicHost(string host)
    {
        lock (_gate) return _tunnels.Values.Any(t => t.Hostname != null && string.Equals(t.Hostname, host, StringComparison.OrdinalIgnoreCase));
    }

    public string? PublicHostFor(string id)
    {
        lock (_gate)
        {
            if (!_tunnels.TryGetValue(id, out var t) || string.IsNullOrEmpty(t.Info.Url)) return null;
            return t.Info.Url.Replace("https://", "").TrimEnd('/');
        }
    }

    private static TunnelInfo Copy(TunnelInfo i) => new() { Status = i.Status, Url = i.Url, Error = i.Error };

    /// <summary>Starts a tunnel. The origin is the Server Brain proxy so public visitors get the access gate and cold-start page.</summary>
    public bool Start(ProjectConfig project, int? proxyPort, int? directPort, out string? error)
    {
        error = null;
        Stop(project.Id, quiet: true);
        var exe = FindCloudflared(_settings().CloudflaredPath);
        if (exe == null)
        {
            error = "cloudflared was not found. Install it (winget install Cloudflare.cloudflared) or set its path in Settings.";
            Set(project.Id, new TunnelInfo { Status = "error", Error = error });
            return false;
        }
        if (project.Share.Mode == ShareMode.Off) { error = "Public sharing is switched off for this project."; return false; }

        var args = new List<string> { "tunnel", "--no-autoupdate" };
        string? namedHost = null;
        if (project.Share.Mode == ShareMode.Quick)
        {
            if (proxyPort != null)
            {
                args.AddRange(new[] { "--url", $"http://127.0.0.1:{proxyPort}", "--http-host-header", $"{project.Id}.localhost" });
            }
            else if (directPort != null) args.AddRange(new[] { "--url", $"http://127.0.0.1:{directPort}" });
            else { error = "Set a port for this project first."; return false; }
        }
        else
        {
            var token = Secrets.Unprotect(project.Share.TokenEnc);
            if (string.IsNullOrWhiteSpace(token)) { error = "No Cloudflare tunnel token saved for this project."; return false; }
            args.AddRange(new[] { "run", "--token", token });
            namedHost = project.Share.Hostname?.Trim();
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var t = new Tunnel { Hostname = namedHost, Info = new TunnelInfo { Status = "starting" } };
        try
        {
            var proc = Process.Start(psi)!;
            t.Proc = proc;
            if (OperatingSystem.IsWindows()) { try { JobHolder.Add(proc); } catch { } }
            void OnLine(string? line)
            {
                if (line == null) return;
                var m = Regex.Match(line, @"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
                if (m.Success && project.Share.Mode == ShareMode.Quick)
                {
                    t.Info = new TunnelInfo { Status = "connected", Url = m.Value };
                    _activity.Add("ok", "tunnel", $"{project.Name} is public at {m.Value}", project);
                    Changed?.Invoke(project.Id);
                }
                else if (project.Share.Mode == ShareMode.Named && Regex.IsMatch(line, "Registered tunnel connection|Connection .* registered", RegexOptions.IgnoreCase) && t.Info.Status != "connected")
                {
                    var url = string.IsNullOrWhiteSpace(namedHost) ? null : "https://" + namedHost;
                    t.Info = new TunnelInfo { Status = "connected", Url = url };
                    _activity.Add("ok", "tunnel", $"{project.Name} tunnel connected" + (url != null ? $" ({url})" : ""), project);
                    Changed?.Invoke(project.Id);
                }
                else if (Regex.IsMatch(line, @"\b(ERR|error)\b", RegexOptions.IgnoreCase) && t.Info.Status != "connected")
                {
                    t.Info.Error = Redact(line);
                }
            }
            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
            proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                lock (_gate)
                {
                    if (!_tunnels.TryGetValue(project.Id, out var cur) || cur != t) return;
                    if (t.Info.Status != "off")
                    {
                        t.Info = new TunnelInfo { Status = "error", Error = t.Info.Error ?? "cloudflared stopped unexpectedly." };
                        _activity.Add("warn", "tunnel", $"{project.Name}: tunnel closed. {t.Info.Error}", project);
                    }
                }
                Changed?.Invoke(project.Id);
            };
            lock (_gate) _tunnels[project.Id] = t;
            Changed?.Invoke(project.Id);
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not start cloudflared: " + ex.Message;
            Set(project.Id, new TunnelInfo { Status = "error", Error = error });
            return false;
        }
    }

    private static string Redact(string line) => Regex.Replace(line, @"eyJ[A-Za-z0-9_\-\.=]{20,}", "***");

    private void Set(string id, TunnelInfo info)
    {
        lock (_gate) _tunnels[id] = new Tunnel { Info = info };
        Changed?.Invoke(id);
    }

    public void Stop(string id, bool quiet = false)
    {
        Tunnel? t;
        lock (_gate) { _tunnels.TryGetValue(id, out t); _tunnels.Remove(id); }
        if (t == null) return;
        t.Info = new TunnelInfo { Status = "off" };
        try { if (t.Proc is { HasExited: false }) t.Proc.Kill(true); } catch { }
        try { t.Proc?.Dispose(); } catch { }
        if (!quiet) Changed?.Invoke(id);
    }

    public void Dispose()
    {
        List<string> ids;
        lock (_gate) ids = _tunnels.Keys.ToList();
        foreach (var id in ids) Stop(id, quiet: true);
    }
}

/// <summary>Keeps helper processes (cloudflared) tied to Server Brain's lifetime on Windows.</summary>
internal static class JobHolder
{
    private static IProcessContainer? _job;
    private static readonly object Gate = new();
    public static void Add(Process p)
    {
        lock (Gate)
        {
            _job ??= ProcessContainer.Create();
            _job.Add(p);
        }
    }
}
