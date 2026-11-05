using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using ServerBrain;

namespace ServerBrain.Tests;

internal static class TestEnv
{
    // All tests share one throwaway data folder so nothing touches the real %LOCALAPPDATA%\ServerBrain.
    [ModuleInitializer]
    public static void Init() => Environment.SetEnvironmentVariable("SB_DATA", Path.Combine(Path.GetTempPath(), "sb-tests-" + Environment.ProcessId));

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "sb-proj-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
}

public class DetectorTests
{
    [Fact]
    public void Slug_is_url_safe()
    {
        Assert.Equal("my-cool-app", Detector.Slug("My Cool_App!"));
        Assert.Equal("project", Detector.Slug("***"));
    }

    [Fact]
    public void Node_project_gets_npm_start_and_env_port()
    {
        var d = TestEnv.TempDir();
        File.WriteAllText(Path.Combine(d, "package.json"), """{ "name": "x", "scripts": { "start": "node server.js" } }""");
        File.WriteAllText(Path.Combine(d, "server.js"), "require('http').createServer().listen(process.env.PORT || 4555)");
        File.WriteAllText(Path.Combine(d, ".env"), "APP_PORT=4600\n# comment\nOLLAMA_PORT=11434\n");
        var s = Detector.Detect(d)!;
        Assert.Equal("node", s.Type);
        Assert.Contains(s.Commands, c => c.Command == "npm start");
        Assert.Equal(4600, s.Port);
        Assert.Equal("APP_PORT", s.PortEnvName);
        Assert.DoesNotContain(s.Ports, p => p.Port == 11434);   // another service's port is not this project's
    }

    [Fact]
    public void Env_file_wins_over_env_example()
    {
        var d = TestEnv.TempDir();
        File.WriteAllText(Path.Combine(d, "package.json"), """{ "scripts": { "start": "node a.js" } }""");
        File.WriteAllText(Path.Combine(d, ".env"), "APP_PORT=8785\n");
        File.WriteAllText(Path.Combine(d, ".env.example"), "APP_PORT=8765\n");
        var s = Detector.Detect(d)!;
        Assert.Equal(8785, s.Port);
        Assert.DoesNotContain(s.ExtraPorts, p => p == 8765);
    }

    [Fact]
    public void Start_script_is_recommended_and_external_service_urls_are_ignored()
    {
        var d = TestEnv.TempDir();
        File.WriteAllText(Path.Combine(d, "start.bat"),
            "@echo off\r\npowershell -Command \"Invoke-RestMethod http://127.0.0.1:11434/api/version\"\r\necho Open http://127.0.0.1:7123\r\nnode server/index.js\r\n");
        var s = Detector.Detect(d)!;
        Assert.Equal("call \"start.bat\"", s.Commands.First(c => c.Recommended).Command);
        Assert.Equal(7123, s.Port);
    }

    [Fact]
    public void Php_and_static_projects_are_recognised()
    {
        var php = TestEnv.TempDir();
        File.WriteAllText(Path.Combine(php, "index.php"), "<?php echo 1;");
        Assert.StartsWith("php -S", Detector.Detect(php)!.Commands[0].Command);

        var st = TestEnv.TempDir();
        File.WriteAllText(Path.Combine(st, "index.html"), "<h1>x</h1>");
        Assert.Equal("static", Detector.Detect(st)!.Type);
    }

    [Fact]
    public void Detection_never_writes_into_the_project_folder()
    {
        var d = TestEnv.TempDir();
        File.WriteAllText(Path.Combine(d, "package.json"), """{ "scripts": { "start": "node a.js" } }""");
        File.WriteAllText(Path.Combine(d, ".env"), "PORT=4000\n");
        var before = Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).Select(p => (p, File.GetLastWriteTimeUtc(p))).OrderBy(x => x.p).ToList();
        Detector.Detect(d);
        Detector.Scan(Path.GetDirectoryName(d)!, _ => null);
        var after = Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).Select(p => (p, File.GetLastWriteTimeUtc(p))).OrderBy(x => x.p).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Scan_flags_projects_that_share_a_port()
    {
        var root = TestEnv.TempDir();
        foreach (var n in new[] { "a", "b", "c" })
        {
            var p = Path.Combine(root, n); Directory.CreateDirectory(p);
            File.WriteAllText(Path.Combine(p, "package.json"), """{ "scripts": { "start": "node s.js" } }""");
            File.WriteAllText(Path.Combine(p, ".env"), $"PORT={(n == "c" ? 4002 : 4001)}\n");
        }
        var list = Detector.Scan(root, _ => null);
        Assert.Equal(3, list.Count);
        Assert.Contains("b", list.First(x => x.Name == "a").PortConflicts);
        Assert.Empty(list.First(x => x.Name == "c").PortConflicts);
    }
}

public class PortTests
{
    [Fact]
    public void Listener_is_found_with_our_pid_and_free_port_is_really_free()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        try
        {
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var row = PortTable.Listeners(fresh: true).FirstOrDefault(r => r.LocalPort == port);
            Assert.True(row.IsListen);
            if (OperatingSystem.IsWindows()) Assert.Equal(Environment.ProcessId, row.Pid);
            var free = PortTable.FindFree(port);
            Assert.NotEqual(port, free);
            Assert.False(PortTable.IsListening(free));
        }
        finally { l.Stop(); }
    }

    [Fact]
    public void System_processes_are_protected_from_being_killed()
    {
        Assert.True(ProcInfo.IsProtected(4));
        Assert.True(ProcInfo.IsProtected(Environment.ProcessId));
    }
}

public class ProcessTests
{
    private static ProjectConfig Cfg(string id, string command, string? dir = null) =>
        new() { Id = id, Name = id, Path = dir ?? TestEnv.TempDir(), Command = command, Restart = RestartPolicy.Never };

    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(100); }
        return cond();
    }

    [Fact]
    public async Task Stopping_kills_the_whole_process_tree_including_grandchildren()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var runner = new ProjectRunner(Cfg("tree-kill", "cmd /c \"ping -n 60 127.0.0.1 >nul\""), new ActivityLog(new EventHub()));
        Assert.True(await runner.StartAsync());
        Assert.True(await WaitFor(() => runner.Pids.Count >= 2));          // cmd.exe + ping.exe
        var pids = runner.Pids.ToList();
        await runner.StopAsync();
        Assert.Equal(ProjectStatus.Stopped, runner.Status);
        Assert.All(pids, pid => Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid).Id));
    }

    [Fact]
    public async Task Crash_is_reported_with_exit_code_and_policy_never_does_not_restart()
    {
        using var runner = new ProjectRunner(Cfg("crashy", OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3"), new ActivityLog(new EventHub()));
        await runner.StartAsync();
        Assert.True(await WaitFor(() => { runner.Tick(DateTime.UtcNow, false); return runner.Status == ProjectStatus.Crashed; }));
        Assert.Equal(3, runner.ExitCode);
        Assert.Equal(0, runner.RestartCount);
    }

    [Fact]
    public async Task Failure_policy_restarts_with_backoff_then_gives_up()
    {
        var cfg = Cfg("flaky", OperatingSystem.IsWindows() ? "exit /b 1" : "exit 1");
        cfg.Restart = RestartPolicy.OnFailure; cfg.MaxRestarts = 1;
        using var runner = new ProjectRunner(cfg, new ActivityLog(new EventHub()));
        await runner.StartAsync();
        Assert.True(await WaitFor(() => { runner.Tick(DateTime.UtcNow, false); return runner.Status == ProjectStatus.Waiting; }));
        Assert.True(await WaitFor(() => { runner.Tick(DateTime.UtcNow, false); return runner.Status == ProjectStatus.Failed; }, 15000));
        Assert.Equal(1, runner.RestartCount);
    }

    [Fact]
    public async Task Missing_folder_fails_cleanly()
    {
        using var runner = new ProjectRunner(Cfg("nofolder", "echo hi", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())), new ActivityLog(new EventHub()));
        Assert.False(await runner.StartAsync());
        Assert.Equal(ProjectStatus.Failed, runner.Status);
        Assert.Contains("not found", runner.Error);
    }

    [Fact]
    public async Task Port_is_detected_once_the_project_listens()
    {
        var exe = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
        if (Process.GetProcessesByName("nothing").Length == 0 && !File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) && OperatingSystem.IsWindows()) return;
        var port = TestEnv.FreePort();
        var cmd = $"powershell -NoProfile -Command \"$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,{port});$l.Start();Start-Sleep 30\"";
        var cfg = Cfg("listener", cmd); cfg.Port = port;
        using var runner = new ProjectRunner(cfg, new ActivityLog(new EventHub()));
        await runner.StartAsync();
        Assert.True(await WaitFor(() => { runner.Tick(DateTime.UtcNow, false); return runner.Status == ProjectStatus.Running; }, 20000));
        await runner.StopAsync();
        Assert.True(await WaitFor(() => !PortTable.Listeners(fresh: true).Any(r => r.LocalPort == port), 3000));   // the port is released after stop
    }
}

[Collection("tool-detection")]
public class MissingToolTests
{
    [Fact]
    public void Output_errors_are_mapped_to_the_missing_program()
    {
        Assert.Equal("node", CommandAnalyzer.MissingToolFromOutput(new[] { "starting…", "'node' is not recognized as an internal or external command," }));
        Assert.Equal("python", CommandAnalyzer.MissingToolFromOutput(new[] { "The term 'python' is not recognized as a name of a cmdlet" }));
        Assert.Equal("php", CommandAnalyzer.MissingToolFromOutput(new[] { "sh: php: command not found" }));
        Assert.Null(CommandAnalyzer.MissingToolFromOutput(new[] { "'frobnicate' is not recognized as an internal or external command" }));
    }

    [Fact]
    public void Start_command_needing_an_uninstalled_tool_is_detected_before_running()
    {
        var old = CommandAnalyzer.IsInstalled;
        try
        {
            CommandAnalyzer.IsInstalled = id => id != "node";
            Assert.Equal("node", CommandAnalyzer.MissingTool("npm start"));
            Assert.Equal("node", CommandAnalyzer.MissingTool("cd web && npx vite"));
            Assert.Null(CommandAnalyzer.MissingTool("python app.py"));
            Assert.Null(CommandAnalyzer.MissingTool("call \"start.bat\""));          // scripts are judged by their output
            Assert.Null(CommandAnalyzer.MissingTool("\"C:\\tools\\node.exe\" server.js")); // explicit path
        }
        finally { CommandAnalyzer.IsInstalled = old; }
    }

    [Fact]
    public async Task Runner_reports_missing_program_instead_of_restarting_forever()
    {
        var old = CommandAnalyzer.IsInstalled;
        try
        {
            CommandAnalyzer.IsInstalled = id => id != "node";
            var cfg = new ProjectConfig { Id = "nonode", Name = "NoNode", Path = TestEnv.TempDir(), Restart = RestartPolicy.OnFailure, MaxRestarts = 3,
                Command = OperatingSystem.IsWindows() ? "echo 'node' is not recognized as an internal or external command& exit /b 1" : "echo \"sh: node: command not found\"; exit 127" };
            using var runner = new ProjectRunner(cfg, new ActivityLog(new EventHub()));
            await runner.StartAsync();
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until && runner.Status != ProjectStatus.Failed) { runner.Tick(DateTime.UtcNow, false); await Task.Delay(100); }
            Assert.Equal(ProjectStatus.Failed, runner.Status);
            Assert.Equal("node", runner.MissingTool);
            Assert.Equal(0, runner.RestartCount);
            Assert.Contains("not installed", runner.Error);
        }
        finally { CommandAnalyzer.IsInstalled = old; }
    }

    [Fact]
    public void System_check_lists_every_known_tool_and_never_offers_install_for_found_ones()
    {
        var list = SystemCheck.Run();
        Assert.Contains(list, t => t.Id == "node");
        Assert.All(list.Where(t => t.Found), t => Assert.False(t.CanInstall));
    }
}

public class StorageTests
{
    [Fact]
    public void Secrets_round_trip_and_do_not_store_plain_text()
    {
        var enc = Secrets.Protect("super-secret-token");
        Assert.DoesNotContain("super-secret", enc);
        Assert.Equal("super-secret-token", Secrets.Unprotect(enc));
        Assert.Equal("", Secrets.Unprotect("garbage"));
    }

    [Fact]
    public void Public_copy_of_a_project_never_contains_the_tunnel_token()
    {
        var c = new ProjectConfig { Id = "a", Name = "A" };
        c.Share.TokenEnc = Secrets.Protect("tok");
        var json = System.Text.Json.JsonSerializer.Serialize(c.ToPublic(), Json.Options);
        Assert.DoesNotContain("tokenEnc", json);
        Assert.Contains("\"hasToken\":false", json);
    }

    [Fact]
    public void Json_file_survives_a_corrupt_main_file_by_using_the_backup()
    {
        var path = Path.Combine(TestEnv.TempDir(), "x.json");
        var f = new JsonFile<AppSettings>(path);
        f.Save(new AppSettings { DashboardPort = 7001 });
        f.Save(new AppSettings { DashboardPort = 7002 });   // creates .bak with 7001
        File.WriteAllText(path, "{ not json");
        Assert.Equal(7001, f.Load().DashboardPort);
    }

    [Fact]
    public void Manager_validates_and_dedupes_ids()
    {
        var hub = new EventHub();
        var mgr = new ProjectManager(hub, new ActivityLog(hub), new TunnelManager(new ActivityLog(hub), () => new AppSettings()), new SettingsService());
        var dir = TestEnv.TempDir();
        var (v1, e1) = mgr.Create(new ProjectInput { Name = "Same Name", Path = dir, Command = "echo 1" });
        var (v2, e2) = mgr.Create(new ProjectInput { Name = "Same Name", Path = dir, Command = "echo 2" });
        Assert.NotNull(v1); Assert.NotNull(v2);
        Assert.NotEqual(v1!.Config.Id, v2!.Config.Id);
        var (bad, errs) = mgr.Create(new ProjectInput { Name = "x", Path = Path.Combine(dir, "nope"), Command = "" });
        Assert.Null(bad);
        Assert.True(errs.Count >= 2);
        var (reserved, _) = mgr.Create(new ProjectInput { Id = "brain", Name = "brain", Path = dir, Command = "echo" });
        Assert.NotEqual("brain", reserved?.Config.Id);
    }

    [Fact]
    public void Duplicate_ports_are_reported_as_conflicts()
    {
        var hub = new EventHub();
        var mgr = new ProjectManager(hub, new ActivityLog(hub), new TunnelManager(new ActivityLog(hub), () => new AppSettings()), new SettingsService());
        var dir = TestEnv.TempDir();
        var port = TestEnv.FreePort();
        mgr.Create(new ProjectInput { Id = "one", Name = "One", Path = dir, Command = "echo", Port = port, Mode = RunMode.Always });
        mgr.Create(new ProjectInput { Id = "two", Name = "Two", Path = dir, Command = "echo", Port = port, Mode = RunMode.Always });
        var c = mgr.ComputeConflicts(mgr.Find("one")!);
        Assert.Contains(c, x => x.Kind == "duplicate" && x.Severity == "error" && x.OtherProjectId == "two");
    }
}
