using System.Text;

namespace ServerBrain.App;

internal static class Program
{
    // SB_INSTANCE lets a second, isolated copy run side by side (used for testing); normally empty.
    private static readonly string Suffix = Environment.GetEnvironmentVariable("SB_INSTANCE") is { Length: > 0 } s ? "." + s : "";
    private static readonly string MutexName = @"Local\ServerBrain.SingleInstance" + Suffix;
    private static readonly string ShowEvent = @"Local\ServerBrain.Show" + Suffix;
    private static readonly string QuitEvent = @"Local\ServerBrain.Quit" + Suffix;

    [STAThread]
    private static int Main(string[] args)
    {
        var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var wantQuit = args.Contains("--quit", StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Another copy is already running: ask it to show itself (or to quit) and leave.
            try { using var ev = EventWaitHandle.OpenExisting(wantQuit ? QuitEvent : ShowEvent); ev.Set(); } catch { }
            return 0;
        }
        if (wantQuit) return 0;   // nothing is running

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };
        ApplicationConfiguration.Initialize();

        ServerBrainRuntime rt;
        TrayContext tray;
        try
        {
            tray = new TrayContext(background);
            rt = ServerBrainHost.Build(args, new HostOptions { RunAutoStart = true, IsDesktopApp = true, Shell = tray });
            tray.Attach(rt);
            rt.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show("Server Brain could not start:\n\n" + ex.Message + "\n\nDetails were saved to:\n" + CrashFile, "Server Brain", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // The Windows startup entry is the source of truth: the installer (or the Settings toggle) creates it, a plain launch never does.
        // If it exists, keep it pointing at this exe (after a move or update) and make the setting reflect it.
        try
        {
            var registered = Autostart.IsEnabled();
            if (registered) Autostart.Set(true);
            var s = rt.Settings.Current;
            if (s.StartWithWindows != registered) { s.StartWithWindows = registered; rt.Settings.Update(s); }
        }
        catch { }

        using var showEv = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        using var quitEv = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
        var waiters = new[]
        {
            ThreadPool.RegisterWaitForSingleObject(showEv, (_, _) => tray.ShowWindow(), null, Timeout.Infinite, false),
            ThreadPool.RegisterWaitForSingleObject(quitEv, (_, _) => tray.Quit(force: true), null, Timeout.Infinite, false),
        };

        Application.Run(tray);

        foreach (var w in waiters) w.Unregister(null);
        return 0;
    }

    public static string CrashFile => Path.Combine(AppPaths.DataDir, "crash.log");

    public static void LogCrash(Exception? ex)
    {
        try { File.AppendAllText(CrashFile, $"[{DateTime.Now:u}] {ex}\r\n\r\n", Encoding.UTF8); } catch { }
    }
}
