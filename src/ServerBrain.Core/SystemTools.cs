using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ServerBrain;

/// <summary>Finds programs the way a freshly opened terminal would, including ones installed after Server Brain started.</summary>
public static class ToolPath
{
    private static readonly object Gate = new();
    private static string? _cache;
    private static DateTime _at;

    /// <summary>
    /// PATH of this process, followed by anything new in the Machine/User PATH from the registry.
    /// A tool installed while Server Brain is running (e.g. Node.js via the System check) is therefore found without a restart.
    /// </summary>
    public static string FreshPath()
    {
        lock (Gate)
        {
            if (_cache != null && (DateTime.UtcNow - _at).TotalSeconds < 10) return _cache;
            var parts = new List<string>();
            void Add(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                foreach (var raw in p.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var d = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                    if (d.Length > 0 && !parts.Contains(d, StringComparer.OrdinalIgnoreCase)) parts.Add(d);
                }
            }
            Add(Environment.GetEnvironmentVariable("PATH"));
            if (OperatingSystem.IsWindows())
            {
                try { Add(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)); } catch { }
                try { Add(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)); } catch { }
            }
            _cache = string.Join(Path.PathSeparator, parts);
            _at = DateTime.UtcNow;
            return _cache;
        }
    }

    public static void Invalidate() { lock (Gate) _cache = null; }

    /// <summary>Full path of an executable/script on PATH, or null. Ignores the Microsoft Store "python" stubs.</summary>
    public static string? Find(params string[] names)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
        foreach (var dir in FreshPath().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
                foreach (var e in exts)
                {
                    try
                    {
                        var p = Path.Combine(dir, n + e);
                        if (!File.Exists(p)) continue;
                        if (dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase) && n.StartsWith("python", StringComparison.OrdinalIgnoreCase)) continue; // Store stub
                        return p;
                    }
                    catch { }
                }
        return null;
    }
}

public sealed record ToolDef(string Id, string Name, string[] Exes, string? WingetId, string Why, bool Optional, string Url);

public sealed class ToolStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Found { get; set; }
    public string? Path { get; set; }
    public string? Version { get; set; }
    public bool Optional { get; set; }
    public string Why { get; set; } = "";
    public string Url { get; set; } = "";
    public bool CanInstall { get; set; }
    public string? Note { get; set; }
}

public static class SystemCheck
{
    public static readonly ToolDef[] Defs =
    {
        new("node", "Node.js", new[] { "node" }, "OpenJS.NodeJS.LTS", "Runs JavaScript projects (npm start, node server.js, Vite…).", false, "https://nodejs.org/"),
        new("python", "Python", new[] { "python", "python3" }, "Python.Python.3.12", "Runs Python projects (FastAPI, Flask, Django, scripts).", false, "https://www.python.org/downloads/"),
        new("php", "PHP", new[] { "php" }, "PHP.PHP.8.4", "Runs PHP projects and the built-in php -S server.", true, "https://windows.php.net/download/"),
        new("docker", "Docker Desktop", new[] { "docker" }, "Docker.DockerDesktop", "Only for projects that use docker compose.", true, "https://www.docker.com/products/docker-desktop/"),
        new("git", "Git", new[] { "git" }, "Git.Git", "Many projects' setup scripts use it to fetch code.", true, "https://git-scm.com/download/win"),
        new("uv", "uv (Python tool)", new[] { "uv" }, "astral-sh.uv", "Some start scripts create Python environments with uv.", true, "https://docs.astral.sh/uv/"),
        new("cloudflared", "cloudflared", new[] { "cloudflared" }, "Cloudflare.cloudflared", "Needed only for Public access (share a project on the internet).", true, "https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/"),
        new("webview2", "WebView2 runtime", Array.Empty<string>(), "Microsoft.EdgeWebView2Runtime", "Lets Server Brain show its own window. Without it the dashboard opens in your browser instead.", true, "https://developer.microsoft.com/microsoft-edge/webview2/"),
    };

    public static bool WingetAvailable => OperatingSystem.IsWindows() && ToolPath.Find("winget") != null;

    public static string? WebView2Version()
    {
        if (!OperatingSystem.IsWindows()) return null;
        const string guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var (root, sub) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + guid),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + guid),
            (Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\EdgeUpdate\Clients\" + guid),
        })
        {
            try { using var k = root.OpenSubKey(sub); if (k?.GetValue("pv") is string v && v.Length > 0 && v != "0.0.0.0") return v; } catch { }
        }
        return null;
    }

    public static List<ToolStatus> Run()
    {
        ToolPath.Invalidate();
        var winget = WingetAvailable;
        var list = new List<ToolStatus>();
        foreach (var d in Defs)
        {
            var s = new ToolStatus { Id = d.Id, Name = d.Name, Optional = d.Optional, Why = d.Why, Url = d.Url };
            if (d.Id == "webview2")
            {
                s.Version = WebView2Version();
                s.Found = s.Version != null;
            }
            else
            {
                s.Path = ToolPath.Find(d.Exes);
                s.Found = s.Path != null;
                if (s.Found && s.Path != null)
                {
                    try { s.Version = FileVersionInfo.GetVersionInfo(s.Path).ProductVersion?.Split('+')[0]; } catch { }
                }
                if (d.Id == "node" && s.Found && ToolPath.Find("npm") == null) { s.Found = false; s.Note = "node was found but npm is missing. Reinstall Node.js."; }
                if (d.Id == "docker" && s.Found && OperatingSystem.IsWindows() && !DockerEngineRunning())
                    s.Note = "Installed, but Docker Desktop is not running. Start it before using Docker projects.";
            }
            s.CanInstall = !s.Found && winget && d.WingetId != null && OperatingSystem.IsWindows();
            list.Add(s);
        }
        return list;
    }

    private static bool DockerEngineRunning()
    {
        try { return File.Exists(@"\\.\pipe\docker_engine") || Directory.GetFiles(@"\\.\pipe\", "docker_engine").Length > 0; } catch { return false; }
    }

    public static ToolDef? ById(string id) => Defs.FirstOrDefault(d => d.Id == id);
}

/// <summary>Installs missing tools through winget (Windows Package Manager) with live progress.</summary>
public sealed class SystemInstaller
{
    public sealed class State
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "idle";    // running | done | error
        public string Message { get; set; } = "";
    }

    private readonly Dictionary<string, State> _states = new();
    private readonly object _gate = new();
    private readonly EventHub _hub;
    private readonly ActivityLog _activity;

    public SystemInstaller(EventHub hub, ActivityLog activity) { _hub = hub; _activity = activity; }

    public List<State> All() { lock (_gate) return _states.Values.ToList(); }

    public bool Start(string id, out string? error)
    {
        error = null;
        var def = SystemCheck.ById(id);
        if (def?.WingetId == null) { error = "Unknown tool."; return false; }
        var winget = ToolPath.Find("winget");
        if (winget == null) { error = "Windows Package Manager (winget) is not available. Use the Download page instead."; return false; }
        lock (_gate)
        {
            if (_states.TryGetValue(id, out var cur) && cur.Status == "running") return true;
            _states[id] = new State { Id = id, Status = "running", Message = "Starting the installer…" };
        }
        Publish(id);
        _ = Task.Run(() => RunInstall(def, winget));
        return true;
    }

    private void RunInstall(ToolDef def, string winget)
    {
        var last = "";
        try
        {
            var psi = new ProcessStartInfo(winget) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "install", "--id", def.WingetId!, "-e", "--silent", "--accept-source-agreements", "--accept-package-agreements" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                var t = Regex.Replace(line.Trim(), @"[\u2580-\u259F\u2588▒█]+|[-\\|/]\s*$", "").Trim();
                if (t.Length < 3) return;
                last = t;
                Set(def.Id, "running", t.Length > 160 ? t[..160] : t);
            }
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(15 * 60 * 1000)) { try { p.Kill(true); } catch { } Set(def.Id, "error", "The installer took too long and was stopped."); return; }
            ToolPath.Invalidate();
            var nowFound = def.Id == "webview2" ? SystemCheck.WebView2Version() != null : ToolPath.Find(def.Exes) != null;
            var already = last.Contains("already installed", StringComparison.OrdinalIgnoreCase) || last.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase);
            if (p.ExitCode == 0 || already || nowFound)
            {
                Set(def.Id, "done", $"{def.Name} is installed. New projects will find it right away.");
                _activity.Add("ok", "install", $"{def.Name} was installed");
            }
            else
            {
                Set(def.Id, "error", $"The installer stopped (code {p.ExitCode}). {last}".Trim());
                _activity.Add("error", "install", $"Installing {def.Name} failed (code {p.ExitCode})");
            }
        }
        catch (Exception ex) { Set(def.Id, "error", "Could not run the installer: " + ex.Message); }
    }

    private void Set(string id, string status, string message)
    {
        lock (_gate) _states[id] = new State { Id = id, Status = status, Message = message };
        Publish(id);
    }

    private void Publish(string id)
    {
        State? s; lock (_gate) _states.TryGetValue(id, out s);
        if (s != null) _hub.Publish("install", s);
    }
}

/// <summary>Spots which runtime a start command needs, so a missing one is reported in plain words before anything runs.</summary>
public static class CommandAnalyzer
{
    /// <summary>Whether a tool is installed. Replaceable so tests can simulate a PC without Node.js.</summary>
    public static Func<string, bool> IsInstalled { get; set; } = id =>
    {
        var def = SystemCheck.ById(id);
        return def != null && ToolPath.Find(def.Exes) != null;
    };

    private static readonly Dictionary<string, string> ToolOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node"] = "node", ["npm"] = "node", ["npx"] = "node",
        ["python"] = "python", ["python3"] = "python", ["pip"] = "python", ["pip3"] = "python",
        ["php"] = "php", ["docker"] = "docker", ["uv"] = "uv", ["git"] = "git", ["cloudflared"] = "cloudflared",
    };
    private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
        { "call", "cd", "set", "echo", "start", "if", "for", "rem", "cmd", "powershell", "pwsh", "chcp", "pushd", "popd", "title", "exit", "timeout", "where", "type", "copy", "del", "mkdir", "md" };

    public static string? ToolIdForName(string name)
    {
        name = name.Trim().Trim('\'', '"');
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return ToolOf.TryGetValue(name, out var id) ? id : null;
    }

    /// <summary>Id of the first required tool that is not installed, or null.</summary>
    public static string? MissingTool(string command)
    {
        foreach (var segment in Regex.Split(command, @"&&|\|\||&|\||;"))
        {
            var m = Regex.Match(segment.Trim(), @"^(?:@)?(""[^""]+""|\S+)");
            if (!m.Success) continue;
            var token = m.Groups[1].Value.Trim('"');
            if (Builtins.Contains(token) || token.Contains('\\') || token.Contains('/') || token.StartsWith('%') || token.StartsWith('.')) continue;
            var id = ToolIdForName(token);
            if (id == null) continue;
            if (!IsInstalled(id)) return id;
        }
        return null;
    }

    /// <summary>Looks through recent output for "'x' is not recognized…" style errors.</summary>
    public static string? MissingToolFromOutput(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            var m = Regex.Match(line, @"'([^']+)' is not recognized as an internal or external command|The term '([^']+)' is not recognized|([\w.\-]+): (?:command )?not found|Could not find ([\w.\-]+)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var name = m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value;
            if (name == null) continue;
            var id = ToolIdForName(name);
            if (id != null) return id;
        }
        return null;
    }
}
