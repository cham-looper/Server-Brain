using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ServerBrain.App;

/// <summary>
/// The desktop window: the same dashboard you get in a browser, hosted in WebView2.
/// The browser control is created when the window opens and destroyed when it closes, so a hidden
/// Server Brain does not hold on to ~150 MB of web-view memory.
/// </summary>
internal sealed class DashboardForm : Form
{
    private static readonly Color Back = Color.FromArgb(7, 8, 15);
    private readonly string _url;
    private WebView2? _web;
    private bool _creating;

    /// <summary>Set when Server Brain is really quitting; otherwise closing only hides the window.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public DashboardForm(string url, Icon icon)
    {
        _url = url;
        Text = "Server Brain";
        Icon = icon;
        BackColor = Back;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 620);
        ClientSize = new Size(1320, 840);
        ShowInTaskbar = true;
        LoadBounds();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { var dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); DwmSetWindowAttribute(Handle, 19, ref dark, 4); } catch { }
    }

    protected override async void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _web == null && !_creating) await CreateWebViewAsync();
    }

    private string _pendingHash = "";

    /// <summary>Show a dashboard page (e.g. "#/help"), whether or not the web view exists yet.</summary>
    public void GoTo(string hash)
    {
        _pendingHash = hash;
        if (_web?.CoreWebView2 != null)
            _ = _web.CoreWebView2.ExecuteScriptAsync($"location.hash = '{hash.Replace("'", "")}'");
    }

    private async Task CreateWebViewAsync()
    {
        _creating = true;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir);
            var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Back, AllowExternalDrop = false };
            Controls.Add(web);
            await web.EnsureCoreWebView2Async(env);
            var s = web.CoreWebView2.Settings;
            s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("SB_DEBUG") == "1";
            s.AreDefaultContextMenusEnabled = Environment.GetEnvironmentVariable("SB_DEBUG") == "1";
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            web.CoreWebView2.NewWindowRequested += (_, a) => { a.Handled = true; OpenExternal(a.Uri); };
            web.CoreWebView2.NavigationStarting += (_, a) =>
            {
                // Only the dashboard itself (same host AND port) may load in this window: it can pop native dialogs.
                // Any other page, even http://127.0.0.1:<project port>, opens in the normal browser.
                var isDashboard = Uri.TryCreate(a.Uri, UriKind.Absolute, out var u) && ServerBrainHost.IsDashboardHost(u.Host)
                    && Uri.TryCreate(_url, UriKind.Absolute, out var self) && u.Port == self.Port;
                if (!isDashboard && !a.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; OpenExternal(a.Uri); }
            };
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.Navigate(_url + _pendingHash);
            _pendingHash = "";
            _web = web;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            Program.LogCrash(ex);
            Hide();
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            MessageBox.Show("The Microsoft Edge WebView2 runtime is missing, so Server Brain opened the dashboard in your browser instead.\n\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 to use the built-in window.", "Server Brain", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { _creating = false; }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var type = doc.RootElement.GetProperty("type").GetString();
            var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (type == "pickFolder")
            {
                using var dlg = new FolderBrowserDialog { Description = "Choose a folder", UseDescriptionForTitle = true, ShowNewFolderButton = false };
                var path = dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
                _web?.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, path }));
            }
        }
        catch { }
    }

    private static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https"))
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var systemEnding = e.CloseReason == CloseReason.WindowsShutDown;
        if (Environment.GetEnvironmentVariable("SB_DEBUG") == "1") Program.LogCrash(new Exception($"FormClosing reason={e.CloseReason} allow={AllowClose}"));
        if (!AllowClose && !systemEnding)
        {
            e.Cancel = true;     // closing the window just hides it; Server Brain keeps running in the tray
            SaveBounds();
            ReleaseWebView();
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private void ReleaseWebView()
    {
        if (_web == null) return;
        try { Controls.Remove(_web); _web.Dispose(); } catch { }
        _web = null;
        GC.Collect(2, GCCollectionMode.Forced, false);
    }

    // ---- remember where the window was
    private static string BoundsFile => Path.Combine(AppPaths.DataDir, "window.json");

    private void SaveBounds()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBoundsRect;
            File.WriteAllText(BoundsFile, JsonSerializer.Serialize(new { b.X, b.Y, b.Width, b.Height, Max = WindowState == FormWindowState.Maximized }));
        }
        catch { }
    }

    private Rectangle RestoreBoundsRect => base.RestoreBounds;

    private void LoadBounds()
    {
        try
        {
            if (!File.Exists(BoundsFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(BoundsFile));
            var r = doc.RootElement;
            var rect = new Rectangle(r.GetProperty("X").GetInt32(), r.GetProperty("Y").GetInt32(), r.GetProperty("Width").GetInt32(), r.GetProperty("Height").GetInt32());
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect)) && rect.Width >= 800 && rect.Height >= 500)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = rect;
                if (r.TryGetProperty("Max", out var mx) && mx.GetBoolean()) WindowState = FormWindowState.Maximized;
            }
        }
        catch { }
    }
}
