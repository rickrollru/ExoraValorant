using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exora.Core;
using Exora.Discord;
using Exora.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Exora;

// Borderless window hosting the web UI. Window chrome (drag, resize, buttons) is drawn in HTML
// and forwarded here through web messages.
public sealed class MainForm : Form
{
    public const string WindowTitle = "Exora";
    private const string Origin = "https://exora.app/";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, string> _resources;
    private readonly GameWatcher _watcher;
    private readonly DiscordPresence _discord = new();
    private AppSettings _settings = AppSettings.Load();
    private string _iconAccent = "";
    private string _newsLang = "";
    private bool _ready;

    public MainForm()
    {
        Text = WindowTitle;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1250, 720);
        MinimumSize = new Size(980, 620);
        BackColor = Color.FromArgb(0x0A, 0x0B, 0x0E);
        DoubleBuffered = true;
        ApplyIcon();

        // Resource names look like "wwwroot/js/app.js"; RecursiveDir may contribute backslashes on Windows.
        _resources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("wwwroot", StringComparison.Ordinal))
            .ToDictionary(n => n.Replace('\\', '/')["wwwroot/".Length..], n => n, StringComparer.OrdinalIgnoreCase);

        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);

        _watcher = new GameWatcher(() => _settings);
        _watcher.StateChanged += OnStateChanged;
        _discord.ConnectionChanged += connected => _watcher.SetDiscord(connected);

        Load += async (_, _) => await InitWebViewAsync();
        Resize += (_, _) =>
        {
            ApplyFrame();
            PostWindowState();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFrame();
    }

    // Lets the window minimise from the taskbar despite having no border.
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_MINIMIZEBOX = 0x20000;
            const int CS_DBLCLKS = 0x8;
            var cp = base.CreateParams;
            cp.Style |= WS_MINIMIZEBOX;
            cp.ClassStyle |= CS_DBLCLKS;
            return cp;
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, AppInfo.WebViewDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var r = MessageBox.Show(
                "Для работы Exora нужен Microsoft Edge WebView2 Runtime.\nОткрыть страницу загрузки?\n\nExora needs the Microsoft Edge WebView2 Runtime. Open the download page?",
                "Exora", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#endif

        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServeResource;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenUrl(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenUrl(e.Uri);
            }
        };
        core.WebMessageReceived += OnWebMessage;
        core.Navigate(Origin + "index.html");
    }

    private void ServeResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path == "") path = "index.html";
        if (!_resources.TryGetValue(path, out var name))
        {
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        var headers = $"Content-Type: {MimeType(path)}\nCache-Control: no-cache";
        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    // ---------- Messages from the page ----------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try
        {
            msg = JsonNode.Parse(e.WebMessageAsJson);
        }
        catch
        {
            return;
        }

        switch (msg?["cmd"]?.GetValue<string>())
        {
            case "ready":
                _ready = true;
                Post("settings", _settings);
                ApplyZoom();
                PostWindowState();
                Post("state", _watcher.Current);
                _watcher.Start();
                _ = LoadNewsAsync();
                break;
            case "settings":
                OnSettings(msg["data"]);
                break;
            case "win":
                OnWindowCommand(msg["action"]?.GetValue<string>(), msg["dir"]?.GetValue<string>());
                break;
            case "launch":
                LaunchValorant();
                break;
            case "openUrl":
                OpenUrl(msg["url"]?.GetValue<string>());
                break;
            case "openData":
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppInfo.DataDir}\"") { UseShellExecute = true });
                break;
        }
    }

    private void OnSettings(JsonNode? data)
    {
        if (data == null) return;
        var next = data.Deserialize<AppSettings>(AppInfo.Json);
        if (next == null) return;
        var langChanged = next.Lang != _settings.Lang;
        _settings = next;
        _settings.Save();

        ApplyZoom();
        ApplyIcon();
        ApplyFrame();
        _discord.Update(_watcher.Current, _settings, _watcher.Content);
        if (langChanged)
        {
            _watcher.Wake();
            _ = LoadNewsAsync();
        }
    }

    private void OnWindowCommand(string? action, string? dir)
    {
        switch (action)
        {
            case "drag":
                if (WindowState == FormWindowState.Maximized) RestoreUnderCursor();
                Native.StartWindowAction(Handle, Native.HTCAPTION);
                break;
            case "resize":
                if (dir != null && Native.ResizeHitTest.TryGetValue(dir, out var hit) && WindowState == FormWindowState.Normal)
                    Native.StartWindowAction(Handle, hit);
                break;
            case "min":
                WindowState = FormWindowState.Minimized;
                break;
            case "max":
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case "close":
                Close();
                break;
        }
    }

    private FormWindowState _framedState = (FormWindowState)(-1);
    private string _framedTheme = "";

    private void ApplyFrame()
    {
        if (!IsHandleCreated || (_framedState == WindowState && _framedTheme == _settings.Theme)) return;
        _framedState = WindowState;
        _framedTheme = _settings.Theme;
        Native.StyleWindow(Handle, _settings.Theme != "light", WindowState == FormWindowState.Maximized);
    }

    // Dragging a maximised window restores it and keeps the cursor at the same relative spot of the title bar.
    private void RestoreUnderCursor()
    {
        var cursor = Cursor.Position;
        var ratio = (cursor.X - Left) / (double)Math.Max(Width, 1);
        var offsetY = cursor.Y - Top;
        WindowState = FormWindowState.Normal;
        var work = Screen.FromPoint(cursor).WorkingArea;
        var x = cursor.X - (int)(Width * ratio);
        var y = Math.Max(work.Top, cursor.Y - offsetY);
        Location = new Point(x, y);
    }

    // A borderless window would cover the taskbar when maximised; fit it to the work area of its monitor.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == Native.WM_GETMINMAXINFO) Native.FitMaximizedToWorkArea(Handle, m.LParam);
    }

    // ---------- State to the page ----------

    private void OnStateChanged(UiState state)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (_ready) Post("state", state);
            _discord.Update(state, _settings, _watcher.Content);
        });
    }

    private async Task LoadNewsAsync()
    {
        var lang = _settings.Lang;
        if (_newsLang == lang) return;
        _newsLang = lang;
        var news = await NewsService.GetAsync(lang, CancellationToken.None);
        if (!IsDisposed && _ready) Post("news", news);
    }

    private void Post(string type, object data)
    {
        if (_web.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { type, data }, AppInfo.Json);
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void PostWindowState()
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "window", maximized = WindowState == FormWindowState.Maximized }));
    }

    private void ApplyZoom()
    {
        var scale = Math.Clamp(_settings.Scale, 0.75, 2.0);
        if (Math.Abs(_web.ZoomFactor - scale) > 0.001) _web.ZoomFactor = scale;
    }

    private void ApplyIcon()
    {
        if (_iconAccent == _settings.Accent) return;
        _iconAccent = _settings.Accent;
        var old = Icon;
        Icon = AppIcon.Create(_settings.Accent, 64);
        if (old != null && !ReferenceEquals(old, Icon)) old.Dispose();
    }

    // ---------- Actions ----------

    private static void LaunchValorant()
    {
        try
        {
            var installs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Riot Games\RiotClientInstalls.json");
            string? exe = null;
            if (File.Exists(installs))
            {
                var json = JsonNode.Parse(File.ReadAllText(installs));
                exe = json?["rc_default"]?.GetValue<string>() ?? json?["rc_live"]?.GetValue<string>();
            }
            exe ??= @"C:\Riot Games\Riot Client\RiotClientServices.exe";
            if (!File.Exists(exe))
            {
                Log.Warn("RiotClientServices.exe not found");
                return;
            }
            Process.Start(new ProcessStartInfo(exe, "--launch-product=valorant --launch-patchline=live") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to launch Valorant", ex);
        }
    }

    private static void OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to open url: " + ex.Message);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _watcher.Dispose();
        _discord.Dispose();
        base.OnFormClosing(e);
    }
}
