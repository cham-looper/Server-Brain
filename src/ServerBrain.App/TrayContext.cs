using System.Diagnostics;

namespace ServerBrain.App;

/// <summary>Owns the tray icon, its menu, notifications and the dashboard window.</summary>
internal sealed class TrayContext : ApplicationContext, IShellControl
{
    private readonly NotifyIcon _tray;
    private readonly Control _ui;                  // hidden control used to marshal calls onto the UI thread
    private readonly Icon _icon;
    private readonly bool _startHidden;
    private ServerBrainRuntime? _rt;
    private DashboardForm? _form;
    private DateTime _lastBalloon = DateTime.MinValue;
    private bool _quitting;

    public TrayContext(bool startHidden)
    {
        _startHidden = startHidden;
        _icon = LoadIcon();
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle;

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Server Brain",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) },
        };
        _tray.ContextMenuStrip.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        // Windows is logging off / shutting down: stop projects cleanly (runs their stop commands, e.g. "docker compose down").
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) =>
        {
            _quitting = true;
            try { if (_rt != null) Task.Run(() => _rt.ShutdownAsync()).Wait(TimeSpan.FromSeconds(8)); } catch { }
        };
    }

    private static Icon LoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    public void Attach(ServerBrainRuntime rt)
    {
        _rt = rt;
        rt.Activity.Added += OnActivity;
        _form = new DashboardForm(rt.DashboardUrl + "?app=1", _icon);
        if (!_startHidden) _ui.BeginInvoke(ShowWindowCore);
        // Refresh the tooltip once in a while so it shows what is running.
        var t = new System.Windows.Forms.Timer { Interval = 5000 };
        t.Tick += (_, _) => UpdateTooltip();
        t.Start();
    }

    // ------------------------------------------------------------ window

    public void ShowWindow() { if (_ui.IsHandleCreated && !_quitting) _ui.BeginInvoke(ShowWindowCore); }

    private void ShowWindowCore()
    {
        if (_rt == null) return;
        if (_form == null || _form.IsDisposed) _form = new DashboardForm(_rt.DashboardUrl + "?app=1", _icon);
        if (!_form.Visible) _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    // -------------------------------------------------------------- menu

    private void BuildMenu()
    {
        var m = _tray.ContextMenuStrip!;
        m.Items.Clear();
        if (_rt == null) { m.Items.Add("Starting…").Enabled = false; return; }

        var open = new ToolStripMenuItem("Open Server Brain") { Font = new Font(m.Font, FontStyle.Bold) };
        open.Click += (_, _) => ShowWindowCore();
        m.Items.Add(open);
        m.Items.Add("Open in browser", null, (_, _) => BrowserLauncher.Open(_rt.DashboardUrl));
        m.Items.Add("Help && guide", null, (_, _) => { _form?.GoTo("#/help"); ShowWindowCore(); });
        m.Items.Add(new ToolStripSeparator());

        var runners = _rt.Manager.Runners();
        foreach (var r in runners.Take(14))
        {
            var up = r.IsUp;
            var label = $"{(up ? "● " : "○ ")}{r.Config.Name}" + (r.Status == ProjectStatus.Running && r.ActivePort is int p ? $"   :{p}" : r.Status is ProjectStatus.Crashed or ProjectStatus.Failed or ProjectStatus.Blocked ? "   ⚠" : "");
            var item = new ToolStripMenuItem(label);
            var id = r.Config.Id;
            if (up)
            {
                item.DropDownItems.Add("Open in browser", null, async (_, _) => await OpenProject(id));
                item.DropDownItems.Add("Restart", null, async (_, _) => await _rt.Manager.RestartAsync(id));
                item.DropDownItems.Add("Stop", null, async (_, _) => await _rt.Manager.StopAsync(id));
            }
            else
            {
                item.DropDownItems.Add("Start and open", null, async (_, _) => await _rt.Manager.StartAsync(id, "manual", openWhenReady: true));
                item.DropDownItems.Add("Start", null, async (_, _) => await _rt.Manager.StartAsync(id, "manual", openWhenReady: false));
            }
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add("Show logs…", null, (_, _) => { ShowWindowCore(); BrowserLauncherSafeNavigate($"#/"); });
            m.Items.Add(item);
        }
        if (runners.Count == 0) m.Items.Add("No projects yet").Enabled = false;
        if (runners.Count > 14) m.Items.Add($"…and {runners.Count - 14} more in the dashboard").Enabled = false;

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Start always-on projects", null, (_, _) => _ = Task.Run(() => _rt.Manager.StartAlwaysOnAsync()));
        var stopAll = m.Items.Add("Stop all projects", null, async (_, _) => await _rt.Manager.StopAllAsync());
        stopAll.Enabled = runners.Any(r => r.IsActive);
        m.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = _rt.Settings.Current.StartWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) =>
        {
            var s = _rt.Settings.Current; s.StartWithWindows = startup.Checked; _rt.Settings.Update(s); Autostart.Set(startup.Checked);
        };
        m.Items.Add(startup);
        m.Items.Add("Quit Server Brain", null, (_, _) => Quit());
    }

    private void BrowserLauncherSafeNavigate(string hash) { /* the window opens on the dashboard; logs are one click away */ }

    private async Task OpenProject(string id)
    {
        var r = _rt?.Manager.Find(id);
        if (r == null) return;
        var v = _rt!.Manager.BuildView(r);
        var url = v.Runtime.LocalUrl ?? v.Runtime.DirectUrl;
        if (url != null) BrowserLauncher.Open(url);
        await Task.CompletedTask;
    }

    private void UpdateTooltip()
    {
        if (_rt == null) return;
        var rs = _rt.Manager.Runners();
        var up = rs.Count(r => r.IsUp);
        var txt = $"Server Brain · {up}/{rs.Count} running";
        _tray.Text = txt.Length > 63 ? txt[..63] : txt;
    }

    // ----------------------------------------------------- notifications

    private void OnActivity(ActivityEntry e)
    {
        var s = _rt?.Settings.Current;
        if (s == null || !s.Notifications || _quitting) return;
        var interesting = e.Level == "error" || (e.Level == "warn" && e.Kind is "crash" or "port" or "memory-limit" or "tunnel") || (e.Level == "ok" && e.Kind == "tunnel");
        if (!interesting) return;
        if ((DateTime.UtcNow - _lastBalloon).TotalSeconds < 3) return;   // don't spam
        _lastBalloon = DateTime.UtcNow;
        var title = e.Kind switch { "crash" => "Project crashed", "port-conflict" => "Port conflict", "tunnel" => "Public access", "start-failed" => "Could not start", _ => "Server Brain" };
        var icon = e.Level == "error" ? ToolTipIcon.Error : e.Level == "warn" ? ToolTipIcon.Warning : ToolTipIcon.Info;
        try
        {
            _ui.BeginInvoke(() =>
            {
                var msg = e.Message.Length > 240 ? e.Message[..240] + "…" : e.Message;
                _tray.ShowBalloonTip(5000, title, msg, icon);
            });
        }
        catch { }
    }

    // ---------------------------------------------------------------- quit

    public void Quit() => Quit(force: false);

    public void Quit(bool force)
    {
        if (_quitting) return;
        if (_ui.IsHandleCreated) _ui.BeginInvoke(() => QuitCore(force)); else QuitCore(force);
    }

    private void QuitCore(bool force)
    {
        if (_quitting) return;
        var running = _rt?.Manager.Runners().Count(r => r.IsActive) ?? 0;
        if (!force && running > 0 && (_rt?.Settings.Current.ConfirmExit ?? true))
        {
            var answer = MessageBox.Show($"{running} project{(running == 1 ? " is" : "s are")} still running.\n\nQuit Server Brain and stop {(running == 1 ? "it" : "them")}?",
                "Quit Server Brain", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        _quitting = true;
        _tray.Visible = false;
        if (_form != null) _form.AllowClose = true;
        try { _form?.Hide(); } catch { }
        // Task.Run keeps the shutdown off the UI thread's sync context (waiting on it here would deadlock).
        try { if (_rt != null) Task.Run(() => _rt.ShutdownAsync()).Wait(TimeSpan.FromSeconds(15)); } catch (Exception ex) { Program.LogCrash(ex); }
        _tray.Dispose();
        _form?.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Dispose(); _ui.Dispose(); _form?.Dispose(); }
        base.Dispose(disposing);
    }
}
