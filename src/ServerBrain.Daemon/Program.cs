using ServerBrain;

// Headless Server Brain: same engine and dashboard as the Windows app, without the tray/window.
// Intended for Linux (systemd) and for running on a PC without a desktop session.
var shell = new DaemonShell();
var rt = ServerBrainHost.Build(args, new HostOptions { RunAutoStart = true, IsDesktopApp = false, Shell = shell });
await rt.StartAsync();
Console.WriteLine($"Server Brain {ServerBrainHost.Version}");
Console.WriteLine($"Dashboard: {rt.DashboardUrl}");
if (rt.ProxyPort > 0) Console.WriteLine($"Project names: http://<name>.localhost{(rt.ProxyPort == 80 ? "" : ":" + rt.ProxyPort)}");

Console.CancelKeyPress += (_, e) => { e.Cancel = true; shell.Quit(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shell.Quit();
await shell.Done.Task;

Console.WriteLine("Stopping projects…");
await rt.ShutdownAsync();

internal sealed class DaemonShell : IShellControl
{
    public TaskCompletionSource Done { get; } = new();
    public void ShowWindow() { }
    public void Quit() => Done.TrySetResult();
}
