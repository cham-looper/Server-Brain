using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServerBrain;

public sealed record CommandCandidate(string Label, string Command, string Kind, bool Recommended, string? StopCommand = null, string? Note = null);

public sealed record PortHint(int Port, string Source, string? EnvName, int Score);

public sealed class Suggestion
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "custom";
    public List<CommandCandidate> Commands { get; set; } = new();
    public List<PortHint> Ports { get; set; } = new();
    public int? Port { get; set; }
    public string PortEnvName { get; set; } = "PORT";
    public List<int> ExtraPorts { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public bool AlreadyAdded { get; set; }
    public List<string> PortConflicts { get; set; } = new();
}

/// <summary>
/// Looks at a project folder and guesses how to run it and which port it uses.
/// STRICTLY READ-ONLY: it never writes into, creates files in, or executes anything from the folder.
/// </summary>
public static class Detector
{
    private static readonly string[] SkipDirs = { "node_modules", ".git", ".venv", "venv", "__pycache__", "dist", "build", ".idea", ".vscode", "$recycle.bin", "system volume information" };
    private static readonly string[] ExternalPortWords = { "COMFY", "OLLAMA", "REDIS", "MYSQL", "POSTGRES", "MONGO", "SMTP", "MAIL", "PROXY", "UPSTREAM", "REMOTE", "TARGET", "DB_", "DATABASE", "LM_STUDIO", "LMSTUDIO", "OPENAI" };
    private static readonly string[] MinorPortWords = { "MIRROR", "PREVIEW", "VITE", "DEV_", "DEBUG", "INSPECT", "HMR" };
    private static readonly string[] ScriptSkipWords = { "setup", "install", "build", "test", "stop", "uninstall", "update", "desktop", "clean", "check", "reset", "deploy" };

    public static string Slug(string name)
    {
        var s = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 40) s = s[..40].Trim('-');
        return s.Length == 0 ? "project" : s;
    }

    public static Suggestion? Detect(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return null;
            var dir = new DirectoryInfo(path);
            var s = new Suggestion { Name = Prettify(dir.Name), Id = Slug(dir.Name), Path = dir.FullName };
            var files = dir.EnumerateFiles().ToDictionary(f => f.Name.ToLowerInvariant(), f => f, StringComparer.OrdinalIgnoreCase);

            string? pkgMain = null; bool hasNodeModules = Directory.Exists(System.IO.Path.Combine(path, "node_modules"));
            var direct = new List<CommandCandidate>();
            var scripts = new List<CommandCandidate>();

            // ---- Node ----
            if (files.TryGetValue("package.json", out var pj))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pj.FullName), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var root = doc.RootElement;
                    if (root.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String && s.Name == Prettify(dir.Name)) { /* keep folder name; it's friendlier */ }
                    if (root.TryGetProperty("main", out var mn) && mn.ValueKind == JsonValueKind.String) pkgMain = mn.GetString();
                    if (root.TryGetProperty("scripts", out var sc) && sc.ValueKind == JsonValueKind.Object)
                    {
                        if (sc.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.String)
                        {
                            direct.Add(new("npm start", "npm start", "node", true, Note: st.GetString()));
                            CollectPortsFromText(st.GetString() ?? "", "package.json start script", s.Ports, 60);
                        }
                        if (sc.TryGetProperty("dev", out var dv) && dv.ValueKind == JsonValueKind.String)
                            direct.Add(new("npm run dev", "npm run dev", "node", false, Note: dv.GetString()));
                    }
                }
                catch { s.Notes.Add("package.json could not be parsed."); }
                s.Type = "node";
                if (!hasNodeModules) s.Notes.Add("Dependencies are not installed yet (no node_modules). Run npm install once, or use the start script if it installs them for you.");
            }
            foreach (var cand in new[] { pkgMain, "server.js", "index.js", "server/index.js", "server/index.mjs", "src/index.js", "app.js" })
            {
                if (string.IsNullOrEmpty(cand)) continue;
                var full = System.IO.Path.Combine(path, cand.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (File.Exists(full) && !direct.Any(d => d.Command == "node " + cand))
                {
                    direct.Add(new($"node {cand}", $"node {cand}", "node", direct.Count == 0));
                    ScanCodeForPorts(full, s.Ports);
                    if (s.Type == "custom") s.Type = "node";
                }
            }
            foreach (var cfgFile in new[] { "server/config.js", "server/config.mjs", "config.js", "src/config.js", "scripts/serve.cjs", "server/index.mjs" })
            {
                var full = System.IO.Path.Combine(path, cfgFile.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (File.Exists(full)) ScanCodeForPorts(full, s.Ports);
            }

            // ---- Python ----
            var venvPy = new[] { ".venv", "venv", "backend\\.venv" }
                .Select(v => System.IO.Path.Combine(path, v, "Scripts", "python.exe"))
                .FirstOrDefault(File.Exists);
            var py = venvPy != null ? $"\"{venvPy}\"" : "python";
            var hasPyProject = files.ContainsKey("requirements.txt") || files.ContainsKey("pyproject.toml");
            foreach (var entry in new[] { "app.py", "main.py", "server.py", "run.py", "manage.py", "backend/app.py", "backend/main.py" })
            {
                var full = System.IO.Path.Combine(path, entry.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                if (entry == "manage.py") direct.Add(new("Django runserver", $"{py} manage.py runserver 127.0.0.1:8000", "python", false));
                else direct.Add(new($"python {entry}", $"{py} {entry.Replace('/', '\\')}", "python", false));
                ScanCodeForPorts(full, s.Ports);
                if (s.Type is "custom") s.Type = "python";
            }
            if (File.Exists(System.IO.Path.Combine(path, "app", "__main__.py")))
                direct.Add(new("python -m app", $"{py} -m app", "python", false));
            if (File.Exists(System.IO.Path.Combine(path, "app", "main.py")) && hasPyProject)
                direct.Add(new("uvicorn app.main:app", $"{py} -m uvicorn app.main:app --host 127.0.0.1 --port 8000", "python", false, Note: "FastAPI layout detected. Adjust the port."));
            if (hasPyProject && s.Type == "custom") s.Type = "python";
            if (hasPyProject && venvPy == null && !Directory.Exists(System.IO.Path.Combine(path, ".venv")))
                s.Notes.Add("No Python virtual environment found (.venv). The start script may create it on first run.");

            // ---- PHP ----
            if (files.ContainsKey("artisan"))
            {
                direct.Add(new("php artisan serve", "php artisan serve --host=127.0.0.1 --port=8000", "php", false));
                s.Type = "php";
            }
            else if (File.Exists(System.IO.Path.Combine(path, "public", "index.php")))
            {
                direct.Add(new("php -S (public/)", "php -S 127.0.0.1:8080 -t public", "php", false));
                s.Type = s.Type == "custom" ? "php" : s.Type;
            }
            else if (files.ContainsKey("index.php"))
            {
                direct.Add(new("php -S", "php -S 127.0.0.1:8080", "php", false));
                s.Type = s.Type == "custom" ? "php" : s.Type;
            }

            // ---- Docker ----
            var compose = new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" }.FirstOrDefault(files.ContainsKey);
            if (compose != null)
            {
                direct.Add(new("docker compose up", "docker compose up", "docker", false, "docker compose down", "Stopping runs 'docker compose down' so the containers really stop."));
                ScanComposeForPorts(files[compose].FullName, s.Ports);
                if (s.Type == "custom") s.Type = "docker";
            }

            // ---- Plain static site ----
            if (direct.Count == 0 && files.ContainsKey("index.html"))
            {
                direct.Add(new("Static site (python http.server)", "python -m http.server 8080 --bind 127.0.0.1", "static", false));
                s.Type = "static";
            }

            // ---- Start scripts the author already wrote ----
            var browserSpam = false;
            var scriptScore = new Dictionary<string, int>();
            foreach (var f in files.Values)
            {
                var ext = f.Extension.ToLowerInvariant();
                if (ext is not (".bat" or ".cmd" or ".ps1")) continue;
                var stem = System.IO.Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                if (!Regex.IsMatch(stem, @"^(start|run|launch|serve|server|dev)")) continue;
                if (ScriptSkipWords.Any(w => stem.Contains(w))) continue;
                var text = ReadHead(f.FullName);
                var opensBrowser = Regex.IsMatch(text, @"start\s+""""\s+""?https?://|Start-Process\s+[""']?https?://|webbrowser\.open|explorer\.exe\s+https?://", RegexOptions.IgnoreCase);
                browserSpam |= opensBrowser;
                var cmd = ext == ".ps1"
                    ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{f.Name}\""
                    : $"call \"{f.Name}\"";
                scripts.Add(new($"{f.Name}", cmd, "script", false, Note: opensBrowser ? "This script opens a browser tab by itself every time it runs. Fine for Manual; for Always-on prefer a direct command." : null));
                scriptScore[cmd] = ScriptScore(stem, ext);
                CollectPortsFromText(text, f.Name, s.Ports, 70);

                // The python line the author uses (e.g. `.venv\Scripts\python.exe -m backend.app`) is the most reliable direct command,
                // but only when the script does not move into another folder first.
                var movesDir = Regex.IsMatch(text, @"(?im)^\s*(?:cd|pushd)\s+(?:/d\s+)?""?%~dp0[\\/]?[^\s""%]+");
                if (!movesDir && ext != ".ps1")
                {
                    var py2 = Regex.Match(text, @"(?im)^\s*(?:@?call\s+)?(""?(?:\.venv\\Scripts\\python(?:\.exe)?|python(?:\.exe)?)""?)\s+(-m\s+[\w\.]+(?:[ \t]+--?[\w-]+(?:[ =]+[\w\.:/-]+)?)*|[\w\\/\.-]+\.py(?:[ \t]+[^\r\n&|<>%]*)?)\s*$");
                    if (py2.Success && !py2.Value.Contains("-c ") && !py2.Value.Contains("pip"))
                    {
                        var line = (py2.Groups[1].Value + " " + py2.Groups[2].Value).Trim();
                        direct.Insert(0, new($"From {f.Name}: {py2.Groups[2].Value.Trim()}", line, "python", false, Note: "Taken from your start script (without its setup steps)."));
                    }
                }
                if (s.Type == "custom") s.Type = ext == ".ps1" ? "custom" : (Regex.IsMatch(text, @"python|uvicorn", RegexOptions.IgnoreCase) ? "python" : Regex.IsMatch(text, @"\bnode\b|npm", RegexOptions.IgnoreCase) ? "node" : "custom");
            }

            // The author's own start script is the tested entry point (it installs, builds, starts helpers), so it is recommended.
            // A command lifted straight from that script wins when the script would also pop a browser tab.
            var all = new List<CommandCandidate>();
            foreach (var d in direct) all.Add(d with { Recommended = false });
            foreach (var sc in scripts.OrderByDescending(x => scriptScore.GetValueOrDefault(x.Command))) all.Add(sc with { Recommended = false });
            if (all.Count > 0)
            {
                var lifted = all.FirstOrDefault(c => c.Label.StartsWith("From "));
                var pick = browserSpam && lifted != null ? lifted
                         : all.FirstOrDefault(c => c.Kind == "script") ?? all[0];
                var i = all.IndexOf(pick);
                all[i] = pick with { Recommended = true };
                all = all.OrderByDescending(c => c.Recommended).ThenBy(c => c.Kind == "script" ? 1 : 0).ToList();
            }
            s.Commands = all;

            // ---- Ports from env files ----
            // .env wins over .env.example: a variable already seen in a stronger file is not read again.
            var seenVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var envName in new[] { ".env", ".env.local", ".env.example" })
                if (files.TryGetValue(envName, out var ef)) ScanEnvForPorts(ef.FullName, s.Ports, envName, seenVars);

            ChoosePorts(s);
            if (s.Commands.Count == 0) s.Notes.Add("Nothing recognisable was found. You can still add it and type the start command yourself.");
            return s;
        }
        catch (Exception ex)
        {
            return new Suggestion { Name = Prettify(System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))), Id = Slug(System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))), Path = path, Notes = { "Could not read the folder: " + ex.Message } };
        }
    }

    /// <summary>Detects every immediate sub-folder that looks like a runnable project.</summary>
    public static List<Suggestion> Scan(string root, Func<string, ProjectConfig?> existing, string? selfDir = null)
    {
        var list = new List<Suggestion>();
        if (!Directory.Exists(root)) return list;
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (d.Name.StartsWith('.') || SkipDirs.Contains(d.Name.ToLowerInvariant())) continue;
            if (selfDir != null && string.Equals(d.FullName.TrimEnd('\\', '/'), selfDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
            var s = Detect(d.FullName);
            if (s == null || s.Commands.Count == 0) continue;
            s.AlreadyAdded = existing(d.FullName) != null;
            list.Add(s);
        }
        // flag identical ports between the suggestions so the user sees clashes before importing
        foreach (var a in list.Where(x => x.Port != null))
            foreach (var b in list.Where(x => x != a && x.Port == a.Port))
                a.PortConflicts.Add(b.Name);
        return list;
    }

    // ---------------------------------------------------------------- helpers

    private static string Prettify(string folder) => folder.Replace('_', '-');

    private static int ScriptScore(string stem, string ext)
    {
        var score = stem switch
        {
            "start" => 100, "run" => 90, "start-windows" or "start-server" or "serve" or "server" => 85, "launch" => 80,
            _ => stem.Contains("all") ? 40 : 55,
        };
        return score - (ext == ".ps1" ? 8 : 0);
    }

    private static string ReadHead(string file, int max = 64 * 1024)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(max, fs.Length)];
            var n = fs.Read(buf, 0, buf.Length);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        catch { return ""; }
    }

    private static bool Valid(int p) => p is >= 1024 and <= 65535;

    private static void Add(List<PortHint> list, int port, string source, string? env, int score)
    {
        if (!Valid(port)) return;
        var existing = list.FindIndex(h => h.Port == port);
        if (existing >= 0)
        {
            if (list[existing].Score < score) list[existing] = new PortHint(port, source, env ?? list[existing].EnvName, score);
            else if (list[existing].EnvName == null && env != null) list[existing] = list[existing] with { EnvName = env };
            return;
        }
        list.Add(new PortHint(port, source, env, score));
    }

    private static void ScanEnvForPorts(string file, List<PortHint> into, string label, HashSet<string> seen)
    {
        foreach (var raw in ReadHead(file, 32 * 1024).Split('\n'))
        {
            var m = Regex.Match(raw, @"^\s*(?:export\s+)?([A-Za-z0-9_]*PORT[A-Za-z0-9_]*)\s*=\s*[""']?(\d{4,5})\b");
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            if (!seen.Add(name)) continue;   // already defined by a higher-priority env file
            var upper = name.ToUpperInvariant();
            if (ExternalPortWords.Any(upper.Contains)) continue;   // that is some OTHER service's port
            var score = upper == "PORT" ? 100 : 85;
            if (MinorPortWords.Any(upper.Contains)) score = 55;
            if (label.EndsWith("example")) score -= 2;
            Add(into, int.Parse(m.Groups[2].Value), label + " → " + name, name, score);
        }
    }

    // Ports of well-known helper services a project merely TALKS to (Ollama, ComfyUI, databases...).
    private static readonly HashSet<int> ExternalServicePorts = new() { 11434, 8188, 8189, 8190, 1234, 6379, 5432, 3306, 27017, 9200, 5672, 11211, 5555, 9222 };
    private static readonly Regex ExternalLine = new(@"ollama|comfy|redis|mysql|postgres|mongo|curl|Invoke-RestMethod|Invoke-WebRequest|api/version|/health|devtools|chrome|--remote-debugging", RegexOptions.IgnoreCase);
    private static readonly Regex AnnounceLine = new(@"start\s+""""\s|Start-Process|echo|Write-Host|\bopen\b|browser", RegexOptions.IgnoreCase);

    private static void CollectPortsFromText(string text, string source, List<PortHint> into, int score)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            if (line.TrimStart().StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.TrimStart().StartsWith("::") || line.TrimStart().StartsWith('#')) { /* comments still mention ports, but are weak */ }
            var external = ExternalLine.IsMatch(line);
            var announce = AnnounceLine.IsMatch(line);
            foreach (Match m in Regex.Matches(line, @"https?://(?:127\.0\.0\.1|localhost|0\.0\.0\.0|\[::1\]):(\d{4,5})", RegexOptions.IgnoreCase))
            {
                var p = int.Parse(m.Groups[1].Value);
                if (external || ExternalServicePorts.Contains(p)) continue;
                Add(into, p, source, null, announce ? score : score - 25);
            }
            // batch variable such as `set PORT=8788` or `set "APP_PORT=8788"`
            var setVar = Regex.Match(line, @"^\s*@?set\s+""?([A-Za-z0-9_]*PORT[A-Za-z0-9_]*)=(\d{4,5})\b", RegexOptions.IgnoreCase);
            if (setVar.Success && !external)
            {
                var upper = setVar.Groups[1].Value.ToUpperInvariant();
                var p = int.Parse(setVar.Groups[2].Value);
                if (!ExternalPortWords.Any(upper.Contains) && !ExternalServicePorts.Contains(p))
                    Add(into, p, source, null, upper == "PORT" ? score : score - 10);
            }
            foreach (Match m in Regex.Matches(line, @"(?:--port[ =]|-p\s+|--port=)(\d{4,5})\b"))
            {
                var p = int.Parse(m.Groups[1].Value);
                if (!external && !ExternalServicePorts.Contains(p)) Add(into, p, source, null, score - 5);
            }
            foreach (Match m in Regex.Matches(line, @"(?:127\.0\.0\.1|localhost):(\d{4,5})\b"))
            {
                var p = int.Parse(m.Groups[1].Value);
                if (!external && !ExternalServicePorts.Contains(p)) Add(into, p, source, null, score - 30);
            }
        }
    }

    private static void ScanCodeForPorts(string file, List<PortHint> into)
    {
        var text = ReadHead(file, 96 * 1024);
        var src = System.IO.Path.GetFileName(file);
        foreach (Match m in Regex.Matches(text, @"(?:PORT|port)\s*(?:\|\||\?\?|,)\s*(\d{4,5})\b"))
            Add(into, int.Parse(m.Groups[1].Value), src, null, 50);
        foreach (Match m in Regex.Matches(text, @"\.listen\(\s*(\d{4,5})\b"))
            Add(into, int.Parse(m.Groups[1].Value), src, null, 50);
        foreach (Match m in Regex.Matches(text, @"\bport\s*=\s*(\d{4,5})\b", RegexOptions.IgnoreCase))
            Add(into, int.Parse(m.Groups[1].Value), src, null, 45);
        // config objects such as `port: 7870` (only trusted when the file also reads a PORT variable)
        if (Regex.IsMatch(text, @"process\.env\.PORT|environ"))
            foreach (Match m in Regex.Matches(text, @"\bport\s*:\s*(\d{4,5})\b"))
                Add(into, int.Parse(m.Groups[1].Value), src, "PORT", 58);
        foreach (Match m in Regex.Matches(text, @"environ\.get\(\s*[""'](\w*PORT\w*)[""']\s*,\s*[""']?(\d{4,5})", RegexOptions.IgnoreCase))
            Add(into, int.Parse(m.Groups[2].Value), src, m.Groups[1].Value, 60);
        foreach (Match m in Regex.Matches(text, @"process\.env\.(\w*PORT\w*)\s*(?:\|\||\?\?)\s*(\d{4,5})"))
            Add(into, int.Parse(m.Groups[2].Value), src, m.Groups[1].Value, 60);
        foreach (Match m in Regex.Matches(text, @"(?:127\.0\.0\.1|localhost):(\d{4,5})\b"))
            Add(into, int.Parse(m.Groups[1].Value), src, null, 40);
    }

    private static void ScanComposeForPorts(string file, List<PortHint> into)
    {
        foreach (Match m in Regex.Matches(ReadHead(file, 64 * 1024), @"-\s*[""']?(?:[\d.]+:)?(\d{4,5}):\d+"))
            Add(into, int.Parse(m.Groups[1].Value), System.IO.Path.GetFileName(file), null, 75);
    }

    private static void ChoosePorts(Suggestion s)
    {
        var ranked = s.Ports.OrderByDescending(p => p.Score).ToList();
        s.Ports = ranked;
        if (ranked.Count == 0) return;
        var primary = ranked[0];
        s.Port = primary.Port;
        s.PortEnvName = primary.EnvName ?? "PORT";
        // other ports the project itself declares in its env files (e.g. a preview/mirror server)
        s.ExtraPorts = ranked.Skip(1).Where(p => p.EnvName != null && p.Score >= 50 && p.Source.Contains(" → "))
            .Take(3).Select(p => p.Port).ToList();
    }
}
