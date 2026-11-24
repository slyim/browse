using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SearchPort;

sealed record CliOptions(
    string? ProfileDir,
    bool NoUpdateCheck,
    string? SmokeReport,
    string? Fixture,
    string? SmokePhase)
{
    public bool Smoke => SmokeReport is not null;
}

static class Program
{
    internal static CliOptions? ParseArgs(string[] args)
    {
        string? profileDir = null;
        var noUpdateCheck = false;
        string? smokeReport = null;
        string? fixture = null;
        string? smokePhase = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-update-check":
                    noUpdateCheck = true;
                    break;
                case "--profile":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i])) return null;
                    profileDir = args[i];
                    break;
                case "--smoke-test":
                    if (smokeReport is not null || i + 2 >= args.Length) return null;
                    smokeReport = args[++i];
                    fixture = args[++i];
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        if (args[i + 1] is not ("restore" or "perf" or "sleep")) return null;
                        smokePhase = args[++i];
                    }
                    break;
                default:
                    if (smokeReport is not null) return null;
                    break;
            }
        }
        return new CliOptions(profileDir, noUpdateCheck, smokeReport, fixture, smokePhase);
    }

    [STAThread]
    static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return 2;
        string? smokeDir = null;
        if (opts.Smoke)
        {
            try { smokeDir = string.IsNullOrWhiteSpace(opts.SmokeReport) ? null : Path.GetDirectoryName(Path.GetFullPath(opts.SmokeReport)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return 2; }
            if (string.IsNullOrEmpty(smokeDir)) return 2;
        }
        string profile;
        try
        {
            profile = opts.Smoke ? Path.Combine(smokeDir!, "WebView") :
                opts.ProfileDir is { } dir ? Path.Combine(Path.GetFullPath(dir), "WebView") :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SearchWindows", "WebView");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return 2; }

        if (!opts.Smoke) Updater.StartCleanupOldBinary();
        var profileRoot = Path.GetDirectoryName(profile)!;
        var settingsFile = Path.Combine(profileRoot, "settings.json");
        var (initialSettings, settingsExisted) = !opts.Smoke || opts.SmokePhase is "restore" or "perf"
            ? SettingsStore.Load(settingsFile)
            : (new AppSettings(), false);
        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // experimental in .NET 10; gives native menus, scrollbars, text boxes and title bar the system theme
        Application.SetColorMode(SettingsStore.ToColorMode(initialSettings.Theme));
#pragma warning restore WFO5001
        var sessionLock = Session.TryAcquireLock(profile);
        try
        {
            var envTask = CreateEnvironment(profile);
            var sessionFile = Path.Combine(profileRoot, "session.json");
            var sessionTask = !opts.Smoke || opts.SmokePhase is "restore" or "perf" ? Task.Run(() => Session.Load(sessionFile)) : null;
            using var browser = new BrowserWindow(
                sessionFile, settingsFile, initialSettings, settingsExisted, envTask, sessionTask, sessionLock is not null,
                opts.SmokeReport, opts.Fixture, opts.SmokePhase,
                opts.NoUpdateCheck, args, () => { sessionLock?.Dispose(); sessionLock = null; });
            Application.Run(browser);
            return browser.ExitCode;
        }
        finally { sessionLock?.Dispose(); }
    }

    static Task<CoreWebView2Environment> CreateEnvironment(string profile)
    {
        try { return CoreWebView2Environment.CreateAsync(userDataFolder: profile); }
        catch (Exception ex) { return Task.FromException<CoreWebView2Environment>(ex); }
    }
}

sealed class BrowserWindow : Form
{
    const string Home = """
        <!doctype html><meta charset="utf-8"><title>New tab</title><style>
        :root{color-scheme:light dark}
        *{box-sizing:border-box}
        body{margin:0;min-height:100vh;display:grid;place-content:center;
        background:#ffffff;font:15px 'Segoe UI',system-ui,sans-serif;user-select:none;cursor:default}
        .box{width:min(560px,calc(100vw - 48px));height:50px;border-radius:14px;padding:0 22px;
        display:flex;align-items:center;background:#ffffff;border:1px solid #e8e8e8;
        color:#767676;box-shadow:0 8px 24px rgba(0,0,0,.06)}
        @media(prefers-color-scheme:dark){
        body{background:#1c1c1c}
        .box{background:#1c1c1c;border-color:#333333;color:#949494;box-shadow:0 8px 24px rgba(0,0,0,.28)}}
        </style><div class="box">Enter a web address</div>
        """;

    readonly ChromePanel sidebar = new() { Dock = DockStyle.Left, BackColor = Theme.Frame };
    readonly ChromePanel sideTopBand = new() { Dock = DockStyle.Top, BackColor = Theme.Frame };
    readonly Panel sideAddressWrap = new() { Dock = DockStyle.Top, BackColor = Theme.Frame };
    readonly PinGridPanel sidePins = new() { Dock = DockStyle.Top, Visible = false };
    readonly TabStack sideTabs = new() { Dock = DockStyle.Fill };
    readonly QuietRow sideNewTab;
    readonly DropLineBar dropLine = new();

    readonly ChromePanel topStrip = new() { Dock = DockStyle.Top, BackColor = Theme.Ground, Visible = false };
    readonly ChromePanel stripTabs = new() { BackColor = Theme.Ground };

    readonly ChromePanel cardFrame = new() { Dock = DockStyle.Fill, BackColor = Theme.Frame };
    readonly Panel cardBorder = new() { Dock = DockStyle.Fill, BackColor = Theme.Hairline };
    readonly Panel pages = new() { Dock = DockStyle.Fill, BackColor = Theme.Ground };

    readonly AddressLine address = new();
    readonly IconButton backBtn;
    readonly IconButton forwardBtn;
    readonly IconButton reloadBtn;
    readonly IconButton shareBtn;
    readonly IconButton updateBtn;
    readonly IconButton settingsBtn;
    readonly IconButton stripNewTabBtn;
    readonly CaptionButton minBtn;
    readonly CaptionButton maxBtn;
    readonly CaptionButton closeBtn;
    readonly ToolTip tips = new();

    static readonly TimeSpan IdleSleepThreshold = TimeSpan.FromMinutes(5);
    readonly System.Windows.Forms.Timer sleepTimer = new() { Interval = 30_000 };
    readonly System.Windows.Forms.Timer dragScrollTimer = new() { Interval = 30 };

    readonly List<BrowserTab> opened = [];
    readonly Dictionary<BrowserTab, Control> tabControls = [];
    readonly Stack<string> closedUrls = new();
    Hairline? stripHairline;
    BrowserTab? dragTab;
    TabChromeControl? dragControl;
    Point dragScreenPt;
    TabDropTarget? dragTarget;
    bool pinDropPlaceholder;
    readonly string sessionFile;
    readonly string settingsFile;
    readonly ThemePreference startupTheme;
    readonly FaviconCache favicons;
    readonly Task<CoreWebView2Environment> envTask;
    readonly Task<SessionState?>? sessionTask;
    readonly bool canSaveSession;
    readonly string? smokeReport;
    readonly string? fixture;
    readonly string? smokePhase;
    readonly bool noUpdateCheck;
    readonly string[] launchArgs;
    readonly Action releaseSessionLock;
    AppSettings settings;
    bool settingsExisted;
    SettingsDialog? settingsDialog;
    CancellationTokenSource? updateCts = new();
    CoreWebView2Environment? environment;
    BrowserTab? selected;
    CancellationTokenSource? pending;
    UpdateRelease? availableUpdate;
    bool updating;
    bool vertical = true;
    bool chromeHidden;
    int hoveredCaptionHit;
    int pressedCaptionHit;
    bool ncTracking;
    int stripScroll;
    int stripContentWidth;
    bool sessionReady;
    bool restoring;
    bool busy;
    bool closed;
    bool recoveringBrowser;
    Func<string, string, bool>? confirmDownloadOverride;
    Func<string, bool>? confirmShareOverride;
    Action<string>? shareErrorOverride;
    int? dpiOverride;
    long envMs;
    long drawTabsTicks;
    public int ExitCode { get; private set; } = 1;
    public bool ChromeHidden => chromeHidden;

    const int WM_NCCALCSIZE = 0x0083;
    const int WM_NCHITTEST = 0x0084;
    const int WM_NCMOUSEMOVE = 0x00A0;
    const int WM_NCLBUTTONDOWN = 0x00A1;
    const int WM_NCLBUTTONUP = 0x00A2;
    const int WM_NCLBUTTONDBLCLK = 0x00A3;
    const int WM_NCMOUSELEAVE = 0x02A2;
    const int WM_MOUSEMOVE = 0x0200;
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_MBUTTONDOWN = 0x0207;
    const int WM_MBUTTONUP = 0x0208;
    const int WM_MOUSEWHEEL = 0x020A;
    const int MK_LBUTTON = 0x0001;
    const int MK_MBUTTON = 0x0010;

    const int HTCLIENT = 1;
    const int HTCAPTION = 2;
    const int HTMINBUTTON = 8;
    const int HTMAXBUTTON = 9;
    const int HTTOP = 12;
    const int HTTOPLEFT = 13;
    const int HTTOPRIGHT = 14;
    const int HTCLOSE = 20;

    const int SM_CYFRAME = 33;
    const int SM_CXPADDEDBORDER = 92;

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;

    const uint TME_LEAVE = 0x00000002;
    const uint TME_NONCLIENT = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }

    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    readonly record struct TabDropTarget(bool Pinned, int Slot);

    sealed class BrowserTab(SessionTab? saved = null)
    {
        public WebView2? View { get; set; }
        public Task? ViewTask { get; set; }
        public bool Closed { get; set; }
        public bool Pinned { get; set; } = saved?.Pinned ?? false;
        public string? PendingUrl { get; set; } = saved?.Url;
        public string? SavedUrl { get; set; } = saved?.Url;
        public string? SavedTitle { get; set; } = saved?.Title;
        public string? LastUrl { get; set; }
        public string? LastTitle { get; set; }
        public bool Loaded { get; set; }
        public bool CrashRecovered { get; set; }
        public long? HiddenSince { get; set; }
        public ulong? ActiveNavigationId { get; set; }
        public Uri? PendingPageUri { get; set; }
        public bool Suspending { get; set; }
        public BrowserTab? Opener { get; set; }
        public bool IsPopup { get; init; }
        public bool Navigating => ActiveNavigationId is not null;
        public Func<int> BlockedCount { get; set; } = () => 0;
        public Func<bool, Task> SetShieldEnabledAsync { get; set; } = _ => Task.CompletedTask;
        public string? Url
        {
            get
            {
                try { if (View is { IsDisposed: false, CoreWebView2.Source: { } s } && PageHandoff.Allowed(s)) return s; }
                catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
                return SavedUrl ?? LastUrl;
            }
        }
        public string Title
        {
            get
            {
                if (Loaded)
                {
                    try
                    {
                        if (View is { IsDisposed: false, CoreWebView2.DocumentTitle: { } t } && !string.IsNullOrWhiteSpace(t) && t != "about:blank")
                            return t;
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
                }
                var fallback = string.IsNullOrWhiteSpace(SavedTitle) ? LastTitle : SavedTitle;
                return string.IsNullOrWhiteSpace(fallback) ? "New tab" : fallback;
            }
        }
    }

    public BrowserWindow(string sessionFile, string settingsFile, AppSettings initialSettings, bool settingsExisted, Task<CoreWebView2Environment> envTask, Task<SessionState?>? sessionTask, bool canSaveSession, string? smokeReport, string? fixture, string? smokePhase = null, bool noUpdateCheck = false, string[]? launchArgs = null, Action? releaseSessionLock = null)
    {
        this.sessionFile = sessionFile;
        this.settingsFile = settingsFile;
        settings = initialSettings;
        this.settingsExisted = settingsExisted;
        startupTheme = initialSettings.Theme;
        favicons = new FaviconCache(sessionFile);
        this.envTask = envTask;
        this.sessionTask = sessionTask;
        this.canSaveSession = canSaveSession;
        this.smokeReport = smokeReport;
        this.fixture = fixture;
        this.smokePhase = smokePhase;
        this.noUpdateCheck = noUpdateCheck;
        this.launchArgs = launchArgs ?? [];
        this.releaseSessionLock = releaseSessionLock ?? (() => { });
        Text = "browse";
        Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        ForeColor = Theme.Ink;
        BackColor = Theme.Frame;
        var minW = S(640);
        var minH = S(420);
        MinimumSize = new(minW, minH);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
        Size = wa.Width > 0 && wa.Height > 0
            ? new(Math.Max(minW, Math.Min(S(1160), wa.Width)), Math.Max(minH, Math.Min(S(780), wa.Height)))
            : new(S(1160), S(780));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        backBtn = MakeIcon(Theme.Glyphs.Back, "Back", GoBack);
        forwardBtn = MakeIcon(Theme.Glyphs.Forward, "Forward", GoForward);
        reloadBtn = MakeIcon(Theme.Glyphs.Reload, "Reload", ReloadCurrent);
        shareBtn = MakeIcon(Theme.Glyphs.Share, "Share page text once with a local agent", () => _ = SharePage());
        shareBtn.Visible = settings.ShareEnabled;
        updateBtn = MakeIcon(Theme.Glyphs.Update, "Update available", () => _ = ApplyUpdate());
        updateBtn.Visible = false;
        updateBtn.SetActive(true);
        settingsBtn = MakeIcon(Theme.Glyphs.Settings, "Settings", OpenSettings);
        stripNewTabBtn = MakeIcon(Theme.Glyphs.NewTab, "New tab", NewTab);
        stripNewTabBtn.SetSurface(Theme.Ground, Theme.StripHover, Theme.StripActive);
        sideNewTab = new QuietRow("New tab", NewTab);

        minBtn = MakeCaption(Theme.Glyphs.Minimize, "Minimize", isClose: false, () => WindowState = FormWindowState.Minimized);
        maxBtn = MakeCaption(Theme.Glyphs.Maximize, "Maximize", isClose: false, ToggleMaximize);
        closeBtn = MakeCaption(Theme.Glyphs.ChromeClose, "Close", isClose: true, Close);

        ApplyChromeMetrics();
        sideTopBand.Resize += (_, _) => LayoutSideTopBand();
        sideAddressWrap.Resize += (_, _) => { if (vertical) address.SetBounds(0, 0, sideAddressWrap.ClientSize.Width, S(28)); };

        // WinForms docks in reverse order: Fill first, then Top stack bottom-to-top.
        sidebar.Controls.Add(sideTabs);
        sidebar.Controls.Add(sidePins);
        sidebar.Controls.Add(sideAddressWrap);
        sidebar.Controls.Add(sideTopBand);

        topStrip.Controls.Add(stripTabs);
        topStrip.Controls.Add(stripNewTabBtn);
        stripTabs.MouseWheel += HandleStripWheel;
        topStrip.Resize += (_, _) => { if (!vertical) LayoutTopStrip(); };

        cardBorder.Controls.Add(pages);
        cardFrame.Controls.Add(cardBorder);

        Controls.Add(cardFrame);
        Controls.Add(sidebar);
        Controls.Add(topStrip);
        Controls.Add(minBtn);
        Controls.Add(maxBtn);
        Controls.Add(closeBtn);
        minBtn.BringToFront();
        maxBtn.BringToFront();
        closeBtn.BringToFront();

        SetOrientation(settings.VerticalTabs, persistSettings: false);
        UpdateNavButtons();

        address.Input.KeyDown += (_, e) => {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _ = Go(address.Input.Text);
        };
        sleepTimer.Tick += (_, _) => { if (settings.SleepingTabsEnabled) _ = SleepIdleTabs(IdleSleepThreshold); };
        sleepTimer.Start();
        dragScrollTimer.Tick += (_, _) => HandleDragAutoScroll();
        Activated += (_, _) => SetCaptionWindowActive(true);
        Deactivate += (_, _) => { CancelTabDrag(); ClearCaptionInteraction(); SetCaptionWindowActive(false); };
        Load += async (_, _) => await Start();
        FormClosing += (_, _) => SaveSession();
        FormClosed += (_, _) => { closed = true; CancelTabDrag(); settingsDialog?.Close(); pending?.Cancel(); updateCts?.Cancel(); sleepTimer.Stop(); sleepTimer.Dispose(); dragScrollTimer.Stop(); dragScrollTimer.Dispose(); };
    }

    int S(int px) => dpiOverride is { } dpi
        ? (int)Math.Round(px * (dpi / 96.0), MidpointRounding.AwayFromZero)
        : LogicalToDeviceUnits(px);

    IconButton MakeIcon(string glyph, string tip, Action action)
    {
        var b = new IconButton(glyph, tip, action);
        tips.SetToolTip(b, tip);
        return b;
    }

    CaptionButton MakeCaption(string glyph, string tip, bool isClose, Action action) => new(glyph, tip, isClose, action);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closed = true;
            CancelTabDrag();
            pending?.Cancel();
            updateCts?.Cancel();
            updateCts?.Dispose();
            updateCts = null;
            sleepTimer.Dispose();
            dragScrollTimer.Dispose();
            if (!dropLine.IsDisposed) dropLine.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) { tips.Dispose(); favicons.Dispose(); }
    }

    void ApplyChromeMetrics()
    {
        MinimumSize = new(S(640), S(420));
        sidebar.Width = S(232);
        sidebar.Padding = new(S(10), 0, S(10), S(10));
        sideTopBand.Height = S(36);
        sideAddressWrap.Height = S(28) + S(8);
        sideAddressWrap.Padding = new(0, 0, 0, S(8));
        sideNewTab.Height = S(28);
        topStrip.Height = S(52);
    }

    void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    void LayoutCaptionButtons() => LayoutCaptionButtons(ClientSize.Width);

    void LayoutCaptionButtons(int w)
    {
        if (closeBtn is null) return;
        var bw = S(46);
        var bh = vertical ? S(32) : topStrip.Height;
        closeBtn.SetBounds(w - bw, 0, bw, bh);
        maxBtn.SetBounds(w - bw * 2, 0, bw, bh);
        minBtn.SetBounds(w - bw * 3, 0, bw, bh);
    }

    void UpdateMaximizeButton()
    {
        if (maxBtn is null) return;
        var maxed = IsHandleCreated ? IsZoomed(Handle) : WindowState == FormWindowState.Maximized;
        var tip = maxed ? "Restore" : "Maximize";
        maxBtn.SetGlyph(maxed ? Theme.Glyphs.Restore : Theme.Glyphs.Maximize, tip);
    }

    void SetCaptionWindowActive(bool active)
    {
        minBtn.SetWindowActive(active);
        maxBtn.SetWindowActive(active);
        closeBtn.SetWindowActive(active);
    }

    void SyncCaptionInteraction()
    {
        minBtn.SetInteraction(hoveredCaptionHit == HTMINBUTTON, pressedCaptionHit == HTMINBUTTON);
        maxBtn.SetInteraction(hoveredCaptionHit == HTMAXBUTTON, pressedCaptionHit == HTMAXBUTTON);
        closeBtn.SetInteraction(hoveredCaptionHit == HTCLOSE, pressedCaptionHit == HTCLOSE);
    }

    void ClearCaptionInteraction()
    {
        hoveredCaptionHit = 0;
        pressedCaptionHit = 0;
        ncTracking = false;
        SyncCaptionInteraction();
    }

    void UpdateWindowTitle()
    {
        var title = selected?.Title;
        var next = string.IsNullOrWhiteSpace(title) ? "browse" : $"{title} — browse";
        if (Text != next) Text = next;
    }

    public void SetChromeHidden(bool hidden)
    {
        if (chromeHidden == hidden) return;
        CancelTabDrag();
        chromeHidden = hidden;
        SuspendLayout();
        ApplyChromeVisibility();
        ResumeLayout(true);
    }

    void ApplyChromeVisibility()
    {
        BackColor = chromeHidden ? Theme.Ground : (vertical ? Theme.Frame : Theme.Ground);
        sidebar.Visible = !chromeHidden && vertical;
        topStrip.Visible = !chromeHidden && !vertical;
        minBtn.Visible = maxBtn.Visible = closeBtn.Visible = !chromeHidden;

        if (chromeHidden)
        {
            cardFrame.BackColor = Theme.Ground;
            cardFrame.Padding = new(0, S(12), 0, 0);
            cardBorder.BackColor = Theme.Ground;
            cardBorder.Padding = Padding.Empty;
        }
        else if (vertical)
        {
            cardFrame.BackColor = Theme.Frame;
            cardFrame.Padding = new(0, S(36), S(8), S(8));
            cardBorder.BackColor = Theme.Hairline;
            cardBorder.Padding = new(1);
        }
        else
        {
            cardFrame.BackColor = Theme.Ground;
            cardFrame.Padding = Padding.Empty;
            cardBorder.BackColor = Theme.Ground;
            cardBorder.Padding = Padding.Empty;
        }

        var capSurface = vertical ? Theme.Frame : Theme.Ground;
        var capHover = vertical ? Theme.SideHover : Theme.StripHover;
        var capActive = vertical ? Theme.SideActive : Theme.StripActive;
        minBtn.SetSurface(capSurface, capHover, capActive);
        maxBtn.SetSurface(capSurface, capHover, capActive);
        closeBtn.SetSurface(capSurface, capHover, capActive);
        LayoutCaptionButtons();
    }

    int TopFrameInset(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = (uint)DeviceDpi;
        return GetSystemMetricsForDpi(SM_CYFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
    }

    static Point LParamPoint(IntPtr lParam)
    {
        var lp = unchecked((int)lParam.ToInt64());
        return new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
    }

    static IntPtr MakeLParam(int x, int y) => (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    bool AnyChildContains(Control container, Point formPt)
    {
        foreach (Control c in container.Controls)
            if (c.Visible && BoundsInForm(c).Contains(formPt))
                return true;
        return false;
    }

    bool AnyStripControlContains(Point formPt)
    {
        foreach (Control c in topStrip.Controls)
        {
            if (!c.Visible || !BoundsInForm(c).Contains(formPt)) continue;
            if (ReferenceEquals(c, stripTabs)) return AnyChildContains(stripTabs, formPt);
            return true;
        }
        return false;
    }

    int HitTestClient(IntPtr hwnd, Point pt)
    {
        if (!IsZoomed(hwnd))
        {
            var resize = S(5);
            var corner = S(10);
            if (pt.Y >= 0 && pt.Y < resize)
            {
                if (pt.X < corner) return HTTOPLEFT;
                if (pt.X >= ClientSize.Width - corner) return HTTOPRIGHT;
                return HTTOP;
            }
        }
        if (!chromeHidden)
        {
            if (closeBtn.Visible && closeBtn.Bounds.Contains(pt)) return HTCLOSE;
            if (maxBtn.Visible && maxBtn.Bounds.Contains(pt)) return HTMAXBUTTON;
            if (minBtn.Visible && minBtn.Bounds.Contains(pt)) return HTMINBUTTON;
        }
        if (chromeHidden)
            return pt.Y >= 0 && pt.Y < S(12) ? HTCAPTION : HTCLIENT;
        if (vertical)
        {
            if (BoundsInForm(sideTopBand).Contains(pt))
                return AnyChildContains(sideTopBand, pt) ? HTCLIENT : HTCAPTION;
            if (pt.X >= sidebar.Right && pt.Y >= 0 && pt.Y < cardFrame.Padding.Top)
                return HTCAPTION;
            return HTCLIENT;
        }
        if (BoundsInForm(topStrip).Contains(pt))
            return AnyStripControlContains(pt) ? HTCLIENT : HTCAPTION;
        return HTCLIENT;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var margins = new MARGINS { cyTopHeight = 1 };
        _ = DwmExtendFrameIntoClientArea(Handle, ref margins);
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        LayoutCaptionButtons();
        UpdateMaximizeButton();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutCaptionButtons();
        UpdateMaximizeButton();
    }

    void ArmNcTrack(IntPtr hwnd)
    {
        if (ncTracking) return;
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE | TME_NONCLIENT,
            hwndTrack = hwnd
        };
        ncTracking = TrackMouseEvent(ref tme);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Clear old MinimumSize before base.OnDpiChanged applies SuggestedRectangle so shrinking to a lower-DPI monitor is not clamped.
        MinimumSize = Size.Empty;
        base.OnDpiChanged(e);
        Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        CancelTabDrag();
        SuspendLayout();
        ApplyChromeMetrics();
        ApplyChromeVisibility();
        ResumeLayout(true);
        if (vertical)
        {
            LayoutSideTopBand();
            address.SetBounds(0, 0, sideAddressWrap.ClientSize.Width, S(28));
            address.PerformLayout();
        }
        RebuildTabs();
        Invalidate(true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero && m.LParam != IntPtr.Zero && !IsIconic(m.HWnd))
        {
            var origTop = Marshal.ReadInt32(m.LParam, 4);
            base.WndProc(ref m);
            Marshal.WriteInt32(m.LParam, 4, IsZoomed(m.HWnd) ? origTop + TopFrameInset(m.HWnd) : origTop);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)HTCLIENT && IsHandleCreated)
                m.Result = (IntPtr)HitTestClient(m.HWnd, PointToClient(LParamPoint(m.LParam)));
            return;
        }
        if (m.Msg == WM_NCMOUSEMOVE)
        {
            var ht = unchecked((int)m.WParam.ToInt64());
            var nextHover = ht is HTMINBUTTON or HTMAXBUTTON or HTCLOSE ? ht : 0;
            if (nextHover != hoveredCaptionHit)
            {
                hoveredCaptionHit = nextHover;
                SyncCaptionInteraction();
            }
            if (nextHover != 0) ArmNcTrack(m.HWnd);
            base.WndProc(ref m);
            return;
        }
        if (m.Msg == WM_NCMOUSELEAVE)
        {
            ClearCaptionInteraction();
            base.WndProc(ref m);
            return;
        }
        if (m.Msg is WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK)
        {
            var ht = unchecked((int)m.WParam.ToInt64());
            if (ht is HTMINBUTTON or HTMAXBUTTON or HTCLOSE)
            {
                pressedCaptionHit = ht;
                hoveredCaptionHit = ht;
                SyncCaptionInteraction();
                ArmNcTrack(m.HWnd);
                m.Result = IntPtr.Zero;
                return;
            }
        }
        if (m.Msg == WM_NCLBUTTONUP)
        {
            var ht = unchecked((int)m.WParam.ToInt64());
            if (ht is HTMINBUTTON or HTMAXBUTTON or HTCLOSE)
            {
                var clicked = pressedCaptionHit == ht;
                pressedCaptionHit = 0;
                SyncCaptionInteraction();
                if (clicked)
                {
                    if (ht == HTMINBUTTON) WindowState = FormWindowState.Minimized;
                    else if (ht == HTMAXBUTTON) ToggleMaximize();
                    else Close();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (pressedCaptionHit != 0) { pressedCaptionHit = 0; SyncCaptionInteraction(); }
        }
        if (m.Msg == WM_LBUTTONUP && pressedCaptionHit != 0)
        {
            pressedCaptionHit = 0;
            SyncCaptionInteraction();
        }
        base.WndProc(ref m);
    }

    void LayoutSideTopBand() => LayoutSideTopBand(sideTopBand.ClientSize.Width, sideTopBand.ClientSize.Height);

    void LayoutSideTopBand(int w, int h)
    {
        if (!vertical) return;
        var btn = S(26);
        var gap = S(2);
        var y = (h - btn) / 2;
        backBtn.SetBounds(0, y, btn, btn);
        forwardBtn.SetBounds(btn + gap, y, btn, btn);
        reloadBtn.SetBounds((btn + gap) * 2, y, btn, btn);
        var rx = w - btn;
        settingsBtn.SetBounds(rx, y, btn, btn);
        if (shareBtn.Visible) { rx -= btn + gap; shareBtn.SetBounds(rx, y, btn, btn); }
        if (updateBtn.Visible) { rx -= btn + gap; updateBtn.SetBounds(rx, y, btn, btn); }
    }

    void LayoutTopStrip() => LayoutTopStrip(topStrip.ClientSize.Width, topStrip.ClientSize.Height);

    void LayoutTopStrip(int w, int h)
    {
        var pad = S(10);
        var btn = S(26);
        var gap = S(2);
        var yBtn = (h - btn) / 2;
        var rowH = S(28);
        var yRow = (h - rowH) / 2;

        var x = pad;
        backBtn.SetBounds(x, yBtn, btn, btn); x += btn + gap;
        forwardBtn.SetBounds(x, yBtn, btn, btn); x += btn + gap;
        reloadBtn.SetBounds(x, yBtn, btn, btn); x += btn + S(8);

        var captionW = S(46) * 3;
        var rightBtns = new List<IconButton>(3);
        if (updateBtn.Visible) rightBtns.Add(updateBtn);
        if (shareBtn.Visible) rightBtns.Add(shareBtn);
        rightBtns.Add(settingsBtn);
        var rightW = btn * rightBtns.Count + gap * (rightBtns.Count - 1);
        var rightStart = w - captionW - S(8) - rightW;
        var rx = rightStart;
        foreach (var rb in rightBtns) { rb.SetBounds(rx, yBtn, btn, btn); rx += btn + gap; }

        var maxAddr = Math.Max(0, rightStart - x - S(8) - btn - S(8));
        var addrW = Math.Min(Math.Clamp((w - pad * 2) * 26 / 100, S(160), S(340)), maxAddr);
        address.SetBounds(x, yRow, addrW, rowH);
        x += addrW + S(8);

        var maxRun = Math.Max(0, rightStart - S(24) - x - gap - btn);
        var shown = LayoutStripTabItems(maxRun, rowH);
        stripTabs.SetBounds(x, yRow, shown, rowH);
        stripNewTabBtn.SetBounds(x + shown + (shown > 0 ? gap : 0), yBtn, btn, btn);
    }

    int LayoutStripTabItems(int maxRun, int rowH)
    {
        var gap = S(2);
        var pinW = S(30);
        var pins = stripTabs.Controls.OfType<PinSquare>().ToArray();
        var pills = stripTabs.Controls.OfType<TabPill>().ToArray();
        var line = stripTabs.Controls.OfType<Hairline>().FirstOrDefault();

        var x = 0;
        foreach (var _ in pins) x += pinW + gap;
        if (line is not null) x += S(4) + 1 + S(6);
        var pillW = pills.Length > 0
            ? Math.Clamp(Math.Max(0, maxRun - x - (pills.Length - 1) * gap) / pills.Length, S(36), S(186))
            : 0;
        if (pills.Length > 0) x += pills.Length * pillW + (pills.Length - 1) * gap;
        else if (pins.Length > 0) x -= gap;

        stripContentWidth = Math.Max(0, x);
        var shown = Math.Min(stripContentWidth, maxRun);
        stripScroll = Math.Clamp(stripScroll, 0, Math.Max(0, stripContentWidth - shown));

        var pos = -stripScroll;
        foreach (var p in pins)
        {
            p.SetBounds(pos, 0, pinW, rowH);
            pos += pinW + gap;
        }
        if (line is not null)
        {
            pos += S(4);
            var lineH = S(20);
            line.SetBounds(pos, (rowH - lineH) / 2, 1, lineH);
            pos += 1 + S(6);
        }
        foreach (var p in pills)
        {
            p.SetBounds(pos, 0, pillW, rowH);
            pos += pillW + gap;
        }
        return shown;
    }

    bool ScrollStripBy(int delta)
    {
        if (vertical || stripContentWidth <= stripTabs.Width || delta == 0) return false;
        var prev = stripScroll;
        stripScroll = Math.Clamp(stripScroll + delta, 0, stripContentWidth - stripTabs.Width);
        var shift = prev - stripScroll;
        if (shift == 0) return false;
        stripTabs.SuspendLayout();
        foreach (Control c in stripTabs.Controls)
            if (!ReferenceEquals(c, dropLine)) c.Left += shift;
        stripTabs.ResumeLayout();
        return true;
    }

    void EnsureStripVisible(Control item)
    {
        if (vertical || item.Parent != stripTabs || stripTabs.Width <= 0 || stripContentWidth <= stripTabs.Width) return;
        if (item.Left < 0) ScrollStripBy(item.Left);
        else if (item.Right > stripTabs.Width) ScrollStripBy(item.Right - stripTabs.Width);
    }

    void EnsureTabVisible(BrowserTab? tab)
    {
        if (tab is null || !tabControls.TryGetValue(tab, out var ctrl) || ctrl.IsDisposed) return;
        if (vertical) { if (!tab.Pinned) sideTabs.EnsureVisible(ctrl); }
        else EnsureStripVisible(ctrl);
    }

    void HandleStripWheel(object? sender, MouseEventArgs e)
    {
        if (vertical || stripContentWidth <= stripTabs.Width) return;
        if (e is HandledMouseEventArgs h) h.Handled = true;
        ScrollStripBy(-e.Delta / 3);
    }

    void GoBack() => TryCore(() => { if (selected is { View: { IsDisposed: false, CanGoBack: true } view }) view.GoBack(); });
    void GoForward() => TryCore(() => { if (selected is { View: { IsDisposed: false, CanGoForward: true } view }) view.GoForward(); });
    void ReloadCurrent()
    {
        if (selected is not { } tab) return;
        if (tab.View is not { IsDisposed: false } view)
        {
            if (tab.Url is { } pendingUrl) { tab.PendingUrl ??= pendingUrl; _ = EnsureViewAndNavigate(tab); }
            return;
        }
        TryCore(() => {
            if (!tab.Loaded && tab.SavedUrl is { } url) view.CoreWebView2?.Navigate(url); // first load never committed
            else if (tab.Url is not null) view.Reload();
        });
    }

    void UpdateNavButtons()
    {
        backBtn.Enabled = TryCore(() => selected is { View: { IsDisposed: false, CanGoBack: true } });
        forwardBtn.Enabled = TryCore(() => selected is { View: { IsDisposed: false, CanGoForward: true } });
        reloadBtn.Enabled = selected is { Url: not null };
    }

    async Task Start()
    {
        try
        {
            var envWatch = Stopwatch.StartNew();
            environment = await envTask;
            envMs = envWatch.ElapsedMilliseconds;
            if (closed) return;
            if (smokeReport is not null) await SmokeTest();
            else
            {
                await Restore();
                if (closed) return;
                if (selected?.Url is null) address.FocusInput();
                else if (selected.View is { IsDisposed: false } view) FocusView(view); // a focused address bar ignores redirects
                ExitCode = 0;
                if (!noUpdateCheck && settings.AutoUpdateCheck) _ = CheckForUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            if (smokeReport is not null) File.WriteAllText(smokeReport, JsonSerializer.Serialize(new { error = ex.ToString() }));
            else if (!closed) MessageBox.Show(this, ex.Message, "Could not start WebView2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (!closed && !IsDisposed && IsHandleCreated) BeginInvoke(Close);
        }
    }

    static readonly HashSet<string> RefusedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "data", "blob", "file", "about", "edge", "chrome",
        "mailto", "tel", "sms", "ftp", "ftps", "ws", "wss", "view-source", "filesystem",
        "ms-settings", "ms-windows-store", "intent", "urn", "magnet", "http", "https",
    };

    public static string? Resolve(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return null;
        if (PageHandoff.Allowed(text)) return new Uri(text).AbsoluteUri;
        if (!text.Any(char.IsWhiteSpace))
        {
            if (Uri.TryCreate("https://" + text, UriKind.Absolute, out var url))
            {
                if (url.UserInfo.Length == 0 && IsHost(url, text))
                    return url.IsLoopback ? new Uri("http://" + text).AbsoluteUri : url.AbsoluteUri;
                if (url.UserInfo.Contains(':')) return null;
            }
            if (IsRefusedAddress(text)) return null;
        }
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
    }

    static bool IsRefusedAddress(string text)
    {
        if (text.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/') return true;
        var colon = text.IndexOf(':');
        if (colon <= 0) return false;
        var scheme = text[..colon];
        if (!Uri.CheckSchemeName(scheme)) return false;
        return text.AsSpan(colon).StartsWith("://", StringComparison.Ordinal) || RefusedSchemes.Contains(scheme);
    }

    // Typed text is an address only if it names a real-looking host: a dotted name with a non-numeric last label, localhost, or a literal IP.
    static bool IsHost(Uri url, string text) => url.HostNameType switch
    {
        UriHostNameType.Dns => url.Host == "localhost" || (url.Host.Contains('.') && !url.Host.Split('.')[^1].All(char.IsAsciiDigit)),
        UriHostNameType.IPv4 => text.StartsWith(url.Host, StringComparison.Ordinal), // rejects shorthand like "3.14" → 3.0.0.14
        UriHostNameType.IPv6 => text.StartsWith('['),
        _ => false
    };

    async Task Restore()
    {
        SessionState? state;
        // shortcut: a session.json that exists but can't be read (locked) is left untouched and nothing is saved this run.
        try { state = sessionTask is not null ? await sessionTask : Session.Load(sessionFile); }
        catch (Exception) { if (!closed) await Open(); return; }
        if (closed) return;
        restoring = true; // no tab opening, closing or clicks while restored tabs are still being created
        sidebar.Enabled = topStrip.Enabled = false;
        try
        {
            if (!settingsExisted && state is not null && state.Vertical != vertical)
            {
                settings = settings with { VerticalTabs = state.Vertical };
                SetOrientation(state.Vertical, persistSettings: false);
            }
            if (state is not { Tabs.Length: > 0 }) await Open();
            else
            {
                foreach (var item in state.Tabs)
                {
                    if (closed) return;
                    opened.Add(new BrowserTab(item));
                }
                if (closed) return;
                SelectTab(opened[Math.Clamp(state.Selected, 0, opened.Count - 1)]);
            }
        }
        finally { restoring = false; sidebar.Enabled = topStrip.Enabled = true; }
        if (!closed) sessionReady = true;
    }

    void SaveSession()
    {
        if (!canSaveSession || !sessionReady || closed) return;
        var kept = new List<SessionTab>();
        var selectedIndex = 0;
        foreach (var tab in opened)
        {
            if (tab.Url is not { } url) continue;
            if (tab == selected) selectedIndex = kept.Count;
            kept.Add(new SessionTab(url, tab.Title, tab.Pinned));
        }
        try { Session.Save(sessionFile, new SessionState([.. kept], selectedIndex, vertical)); }
        catch { }
    }

    void CleanupView(WebView2 view)
    {
        if (!pages.IsDisposed) pages.Controls.Remove(view);
        if (!view.IsDisposed) view.Dispose();
    }

    async Task UpdateFaviconAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            if (closed || tab.Closed || tab.View is not { IsDisposed: false }) return;
            var host = FaviconCache.HostOf(core.Source);
            if (host is null || string.IsNullOrEmpty(core.FaviconUri)) return;
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null || closed || tab.Closed || tab.View is not { IsDisposed: false }) return;
            if (FaviconCache.HostOf(core.Source) != host) return;
            var bytes = await FaviconCache.ReadBoundedAsync(stream);
            if (bytes is null || closed || tab.Closed) return;
            var path = favicons.PathForHost(host);
            var bmp = await Task.Run(() => FaviconCache.DecodeAndSave(bytes, path));
            if (bmp is null) return;
            if (closed) { bmp.Dispose(); return; }
            var old = favicons.Set(host, bmp);
            foreach (var t in opened)
                if (FaviconCache.HostOf(t.Url) == host) RefreshTab(t);
            old?.Dispose();
        }
        catch { }
    }

    async Task EnsureView(BrowserTab tab)
    {
        if (tab.ViewTask is not { IsFaulted: false } task)
            tab.ViewTask = task = Create();
        await task;

        async Task Create()
        {
            var view = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = Theme.Ground };
            pages.Controls.Add(view);
            try
            {
                await view.EnsureCoreWebView2Async(environment);
                if (closed || tab.Closed || view.IsDisposed) { CleanupView(view); return; }
                var core = view.CoreWebView2;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                if (core.Profile is not null)
                    core.Profile.PreferredColorScheme = SettingsStore.ToWebViewColorScheme(startupTheme);
                var (blocked, setShieldEnabled) = await Shield.AttachAsync(
                    core,
                    environment!,
                    () => TryCore(() => !view.IsDisposed && Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u : null),
                    () => tab.PendingPageUri,
                    () => settings.BlockingEnabled);
                if (closed || tab.Closed || view.IsDisposed) { CleanupView(view); return; }
                tab.BlockedCount = blocked;
                tab.SetShieldEnabledAsync = setShieldEnabled;
                var recentCleaned = new List<(string Url, long At)>(8);
                void TrackNavigation(CoreWebView2NavigationStartingEventArgs e)
                {
                    tab.ActiveNavigationId = e.NavigationId;
                    tab.PendingPageUri = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u : null;
                }
                core.NavigationStarting += (_, e) => {
                    if (!IsNavigable(e.Uri))
                    {
                        e.Cancel = true;
                        return;
                    }
                    if (!PageHandoff.Allowed(e.Uri))
                    {
                        tab.ActiveNavigationId = e.NavigationId;
                        tab.PendingPageUri = e.Uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
                            && Uri.TryCreate(e.Uri[5..], UriKind.Absolute, out var blobOrigin)
                                ? blobOrigin
                                : null;
                        return;
                    }
                    // Re-navigating a popup can sever window.opener mid-OAuth, and Navigate() would turn a POST into a bodyless GET.
                    if (!settings.CleanLinksEnabled || tab.IsPopup || IsPostNavigation(e) || e.NavigationKind is CoreWebView2NavigationKind.BackOrForward or CoreWebView2NavigationKind.Reload)
                    {
                        TrackNavigation(e);
                        return;
                    }
                    var cleaned = CleanLink.Clean(e.Uri);
                    if (cleaned != e.Uri)
                    {
                        var now = Environment.TickCount64;
                        recentCleaned.RemoveAll(x => now - x.At >= 10_000);
                        if (recentCleaned.Any(x => x.Url == cleaned))
                        {
                            TrackNavigation(e);
                            return;
                        }
                        if (recentCleaned.Count >= 8) recentCleaned.RemoveAt(0);
                        recentCleaned.Add((cleaned, now));
                        e.Cancel = true;
                        if (!closed && !IsDisposed && IsHandleCreated)
                            BeginInvoke(() => { if (!closed && !IsDisposed && !tab.Closed && !view.IsDisposed) TryCore(() => view.CoreWebView2?.Navigate(cleaned)); });
                        return;
                    }
                    TrackNavigation(e);
                };
                core.NavigationCompleted += (_, e) => {
                    if (tab.ActiveNavigationId == e.NavigationId)
                    {
                        tab.ActiveNavigationId = null;
                        tab.PendingPageUri = null;
                    }
                    if (e.IsSuccess) tab.CrashRecovered = false;
                };
                core.PermissionRequested += (_, e) => {
                    var (state, savesInProfile) = DecidePermission(e.PermissionKind, e.IsUserInitiated);
                    e.SavesInProfile = savesInProfile;
                    if (state != CoreWebView2PermissionState.Default) e.State = state;
                };
                core.DownloadStarting += (_, e) => {
                    if (tab != selected || closed || tab.Closed)
                    {
                        e.Cancel = true;
                        return;
                    }
                    var filePath = e.ResultFilePath;
                    if (!IsDangerousDownload(filePath)) return;
                    if (IsDisposed || !IsHandleCreated) { e.Cancel = true; return; }
                    var deferral = e.GetDeferral();
                    BeginInvoke(() => {
                        try
                        {
                            if (closed || tab.Closed || IsDisposed || tab != selected)
                            {
                                e.Cancel = true;
                                return;
                            }
                            var name = Path.GetFileName(filePath);
                            if (string.IsNullOrEmpty(name)) name = filePath;
                            var host = Uri.TryCreate(e.DownloadOperation?.Uri, UriKind.Absolute, out var u) && u.IdnHost.Length > 0 ? u.IdnHost : "this site";
                            e.Cancel = !ConfirmDangerousDownload(name, host);
                        }
                        catch { e.Cancel = true; }
                        finally { deferral.Complete(); }
                    });
                };
                core.ProcessFailed += (_, e) => HandleProcessFailed(tab, view, e.ProcessFailedKind);
                core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
                core.NewWindowRequested += (_, e) => {
                    e.Handled = true;
                    if (!e.IsUserInitiated || !IsPopupAllowed(e.Uri) || environment is null || closed || restoring || IsDisposed || !IsHandleCreated)
                        return;
                    _ = OpenPopup(tab, e);
                };
                core.WindowCloseRequested += (_, _) => {
                    if (tab.IsPopup && !closed && !IsDisposed && IsHandleCreated && !tab.Closed)
                        BeginInvoke(() => { if (!closed && !IsDisposed && !tab.Closed) CloseTab(tab, recordClosed: false); });
                };
                core.SourceChanged += (_, _) => {
                    if (!opened.Contains(tab)) return;
                    var src = TryCore(() => core.Source);
                    if (src is not null && PageHandoff.Allowed(src))
                    {
                        tab.Loaded = true;
                        tab.SavedUrl = null;
                        tab.LastUrl = src;
                    }
                    else if (src is not null && src.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) && IsNavigable(src))
                    {
                        tab.Loaded = true;
                        tab.SavedUrl = null;
                        tab.LastUrl = null;
                    }
                    else if (src is not null)
                    {
                        tab.LastUrl = null;
                    }
                    if (selected == tab && !address.InputFocused) UpdateAddress();
                    if (selected == tab) { UpdateNavButtons(); UpdateWindowTitle(); }
                    RefreshTab(tab);
                    SaveSession();
                };
                core.HistoryChanged += (_, _) => { if (selected == tab) UpdateNavButtons(); };
                core.DocumentTitleChanged += (_, _) => {
                    if (!opened.Contains(tab)) return;
                    if (tab.Loaded)
                    {
                        tab.SavedTitle = null; // the restored title must not stick to later pages
                        tab.LastTitle = TryCore(() => core.DocumentTitle);
                    }
                    if (selected == tab) UpdateWindowTitle();
                    RefreshTab(tab);
                    SaveSession();
                };
                core.FaviconChanged += (_, _) => _ = UpdateFaviconAsync(tab, core);
                tab.View = view;
            }
            catch
            {
                CleanupView(view);
                if (closed || tab.Closed) return;
                throw;
            }
        }
    }

    string ProfileDir => Path.Combine(Path.GetDirectoryName(sessionFile)!, "WebView");

    static bool TryCore(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { return false; }
    }

    static T TryCore<T>(Func<T> func, T fallback = default!)
    {
        try { return func(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { return fallback; }
    }

    internal static bool IsNavigable(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (PageHandoff.Allowed(uri)) return true;
        if (string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        return uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            && uri.Length > 5 && !char.IsWhiteSpace(uri[5])
            && PageHandoff.Allowed(uri[5..]);
    }

    static bool IsPostNavigation(CoreWebView2NavigationStartingEventArgs e) =>
        e.RequestHeaders is { } h && (h.Contains("Content-Type") || h.Contains("Content-Length"));

    internal static (CoreWebView2PermissionState State, bool SavesInProfile) DecidePermission(
        CoreWebView2PermissionKind kind, bool isUserInitiated)
    {
        if (!isUserInitiated && kind is
            CoreWebView2PermissionKind.ClipboardRead or
            CoreWebView2PermissionKind.MultipleAutomaticDownloads or
            CoreWebView2PermissionKind.LocalFonts or
            CoreWebView2PermissionKind.WindowManagement or
            CoreWebView2PermissionKind.MidiSystemExclusiveMessages)
            return (CoreWebView2PermissionState.Deny, false);

        var savesInProfile = kind is not (
            CoreWebView2PermissionKind.Camera or
            CoreWebView2PermissionKind.Microphone or
            CoreWebView2PermissionKind.Geolocation or
            CoreWebView2PermissionKind.FileReadWrite or
            CoreWebView2PermissionKind.ClipboardRead);

        return (CoreWebView2PermissionState.Default, savesInProfile);
    }

    static readonly HashSet<string> DangerousDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse",
        ".wsf", ".scr", ".com", ".lnk", ".reg", ".hta", ".cpl", ".dll", ".jar",
    };

    internal static bool IsDangerousDownload(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return DangerousDownloadExtensions.Contains(Path.GetExtension(path.TrimEnd(' ', '.')));
    }

    bool ConfirmDangerousDownload(string fileName, string host) =>
        confirmDownloadOverride is not null
            ? confirmDownloadOverride(fileName, host)
            : MessageBox.Show(this,
                $"Allow downloading \"{fileName}\" from {host}?\n\nExecutable and script files can harm your computer. Only download files you trust.",
                "Confirm download",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.OK;

    void OpenDownloads()
    {
        if (selected is not { View: { IsDisposed: false, CoreWebView2: { } core } }) return;
        TryCore(() => core.OpenDefaultDownloadDialog());
    }

    void ResetTabForRecovery(BrowserTab tab, bool preserveCrashFlag = false)
    {
        var url = tab.Url;
        var title = tab.Title;
        tab.SavedUrl = url;
        tab.PendingUrl = url;
        tab.SavedTitle = string.IsNullOrWhiteSpace(title) || title == "New tab" ? tab.LastTitle : title;
        tab.Loaded = false;
        tab.ActiveNavigationId = null;
        tab.PendingPageUri = null;
        tab.Suspending = false;
        if (!preserveCrashFlag) tab.CrashRecovered = false;
        tab.BlockedCount = () => 0;
        tab.SetShieldEnabledAsync = _ => Task.CompletedTask;
        tab.ViewTask = null;
        if (tab.View is { } oldView)
        {
            tab.View = null;
            CleanupView(oldView);
        }
    }

    void RecreateTabView(BrowserTab tab)
    {
        if (closed || tab.Closed || IsDisposed || !opened.Contains(tab)) return;
        ResetTabForRecovery(tab, preserveCrashFlag: true);
        if (selected == tab && environment is not null) _ = EnsureViewAndNavigate(tab);
        UpdateNavButtons();
    }

    void HandleProcessFailed(BrowserTab tab, WebView2 view, CoreWebView2ProcessFailedKind kind)
    {
        tab.ActiveNavigationId = null;
        tab.PendingPageUri = null;
        if (closed || IsDisposed) return;

        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            if (recoveringBrowser || !IsHandleCreated) return;
            recoveringBrowser = true;
            BeginInvoke(() => _ = RecoverBrowserProcessAsync());
            return;
        }

        if (tab.Closed || tab.View != view) return;
        if (kind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
            return;
        if (tab.CrashRecovered || !IsHandleCreated) return;
        tab.CrashRecovered = true;

        BeginInvoke(() => {
            if (closed || tab.Closed || IsDisposed || tab.View != view) return;
            if (kind == CoreWebView2ProcessFailedKind.RenderProcessExited
                && !view.IsDisposed && view.CoreWebView2 is { } c
                && TryCore(() => {
                    if (!tab.Loaded && tab.SavedUrl is { } saved) c.Navigate(saved);
                    else c.Reload();
                }))
                return;
            RecreateTabView(tab);
        });
    }

    async Task RecoverBrowserProcessAsync()
    {
        try
        {
            foreach (var t in opened.ToArray()) ResetTabForRecovery(t);
            environment = null;
            UpdateNavButtons();
            UpdateAddress();
            UpdateWindowTitle();

            CoreWebView2Environment? recreated = null;
            for (var attempt = 0; attempt < 3 && !closed && !IsDisposed; attempt++)
            {
                try
                {
                    recreated = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfileDir);
                    break;
                }
                catch when (attempt < 2)
                {
                    await Task.Delay(250);
                }
            }
            if (closed || IsDisposed || recreated is null) return;
            environment = recreated;
            if (selected is { Closed: false, View: null } active)
                await EnsureViewAndNavigate(active);
        }
        catch { }
        finally { recoveringBrowser = false; }
    }

    static bool IsPopupAllowed(string? uri) =>
        string.IsNullOrEmpty(uri) || IsNavigable(uri);

    async Task OpenPopup(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        var fallbackUri = e.Uri;
        var fallback = false;
        BrowserTab? popup = null;
        try
        {
            popup = new BrowserTab { Opener = opener, IsPopup = true };
            await EnsureView(popup);
            if (closed || opener.Closed || popup.Closed || IsDisposed || popup.View is not { IsDisposed: false, CoreWebView2: { } popupCore })
            {
                popup.Closed = true;
                if (popup.View is { } stale) { popup.View = null; CleanupView(stale); }
                return;
            }
            // Attach before deferral completes and without calling Navigate so WebView2 preserves window.opener.
            e.NewWindow = popupCore;
            opened.Add(popup);
            SelectTab(popup);
        }
        catch
        {
            if (popup is not null)
            {
                popup.Closed = true;
                opened.Remove(popup);
                if (popup.View is { } stale) { popup.View = null; CleanupView(stale); }
                if (selected == popup)
                {
                    selected = null;
                    if (opened.Contains(opener)) SelectTab(opener);
                    else if (opened.Count > 0) SelectTab(opened[^1]);
                }
                SyncTabs();
            }
            fallback = true;
        }
        finally
        {
            deferral.Complete();
        }
        if (fallback && PageHandoff.Allowed(fallbackUri) && !closed && !IsDisposed && IsHandleCreated && !opener.Closed)
            BeginInvoke(() => { if (!closed && !IsDisposed && !opener.Closed) _ = OpenInteractive(fallbackUri); });
    }

    async Task<BrowserTab> Open(string? target = null)
    {
        var cleaned = target is null ? null : (settings.CleanLinksEnabled ? CleanLink.Clean(target) : target);
        var tab = new BrowserTab { SavedUrl = cleaned };
        await EnsureView(tab);
        if (closed || tab.Closed || tab.View is not { IsDisposed: false, CoreWebView2: { } core }) return tab;
        opened.Add(tab);
        SelectTab(tab, focusView: !address.InputFocused);
        if (cleaned is null) TryCore(() => core.NavigateToString(Home));
        else TryCore(() => core.Navigate(cleaned));
        SaveSession();
        return tab;
    }

    // With no ready tab (startup, or the last tab just closed) open one instead of touching a missing CoreWebView2.
    Task Go(string text)
    {
        var target = Resolve(text);
        if (target is null) return Task.CompletedTask;
        if (settings.CleanLinksEnabled) target = CleanLink.Clean(target);
        if (selected is not { } tab)
        {
            if (address.InputFocused) ActiveControl = null;
            return OpenInteractive(target);
        }
        if (tab.View is { IsDisposed: false, CoreWebView2: { } core })
        {
            tab.PendingUrl = null;
            if (!tab.Loaded)
            {
                tab.SavedUrl = target;
                tab.SavedTitle = null;
            }
            TryCore(() => core.Navigate(target));
            FocusView(tab.View);
            if (!tab.Loaded) { UpdateAddress(); UpdateNavButtons(); RefreshTab(tab); }
            return Task.CompletedTask;
        }
        tab.PendingUrl = target;
        tab.SavedUrl = target;
        tab.SavedTitle = null;
        if (address.InputFocused) ActiveControl = null;
        UpdateAddress();
        UpdateNavButtons();
        RefreshTab(tab);
        return EnsureViewAndNavigate(tab);
    }

    void NewTab() => _ = OpenInteractive(null);
    async Task OpenInteractive(string? url)
    {
        if (environment is null || closed || restoring) return; // Ctrl+T skips the disabled UI during restore
        if (url is not null && address.InputFocused) ActiveControl = null;
        try { await Open(url); if (url is null) address.FocusInput(); }
        catch (Exception ex) { if (!closed) MessageBox.Show(this, ex.Message, "Could not open tab"); }
    }

    void UpdateAddress()
    {
        address.Input.Text = selected?.Url ?? "";
        address.Input.PlaceholderText = "Search or enter address";
    }

    void FocusView(WebView2 view)
    {
        if (view.IsDisposed || !view.Visible) return;
        ActiveControl = view;
        view.Focus();
    }

    void SelectTab(BrowserTab tab, bool focusView = true)
    {
        var prev = selected;
        selected = tab;
        tab.HiddenSince = null;
        if (tab.View is { IsDisposed: false } view)
        {
            view.Visible = true;
            view.BringToFront();
            if (view.CoreWebView2 is { } core)
            {
                if (TryCore(() => core.IsSuspended))
                {
                    TryCore(() => core.Resume());
                    if (!settings.BlockingEnabled) _ = tab.SetShieldEnabledAsync(false);
                }
                TryCore(() => core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal);
            }
            if (focusView) FocusView(view);
            if (tab.PendingUrl is { } url)
            {
                tab.PendingUrl = null;
                TryCore(() => view.CoreWebView2?.Navigate(url));
            }
        }
        else
        {
            if (focusView) ActiveControl = null;
            _ = EnsureViewAndNavigate(tab);
        }
        if (prev is not null && prev != tab)
        {
            prev.HiddenSince = Environment.TickCount64;
            if (prev.View is { IsDisposed: false } prevView)
            {
                prevView.Visible = false;
                if (prevView.CoreWebView2 is { } prevCore)
                    TryCore(() => prevCore.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low);
            }
        }
        if (focusView || !address.InputFocused) UpdateAddress();
        UpdateNavButtons();
        UpdateWindowTitle();
        if (prev is not null && prev != tab) RefreshTab(prev);
        RefreshTab(tab);
        EnsureTabVisible(tab);
        SaveSession();
    }

    async Task EnsureViewAndNavigate(BrowserTab tab)
    {
        if (environment is null || closed || tab.Closed) return;
        try
        {
            await EnsureView(tab);
            if (closed || tab.Closed || tab.View is not { IsDisposed: false, CoreWebView2: { } core }) return;
            if (selected != tab)
            {
                tab.View.Visible = false;
                tab.HiddenSince ??= Environment.TickCount64;
                TryCore(() => core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low);
                return;
            }
            tab.HiddenSince = null;
            tab.View.Visible = true;
            tab.View.BringToFront();
            if (TryCore(() => core.IsSuspended)) TryCore(() => core.Resume());
            TryCore(() => core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal);
            if (tab.PendingUrl is { } url)
            {
                tab.PendingUrl = null;
                TryCore(() => core.Navigate(url));
            }
            else if (!tab.Loaded && tab.SavedUrl is null)
            {
                TryCore(() => core.NavigateToString(Home));
            }
            UpdateNavButtons();
            if (!address.InputFocused) FocusView(tab.View);
        }
        catch (Exception ex)
        {
            if (!closed && smokeReport is null) MessageBox.Show(this, ex.Message, "Could not open tab");
        }
    }

    void CloseCurrent() { if (selected is not null) CloseTab(selected); }
    void CloseTab(BrowserTab tab, bool recordClosed = true)
    {
        if (tab.Pinned) return;
        var index = opened.IndexOf(tab);
        if (index < 0) return;
        if (tab == dragTab) CancelTabDrag();
        var display = DisplayTabs();
        var displayIndex = display.IndexOf(tab);
        tab.Closed = true;
        if (recordClosed && tab.Url is { } url && PageHandoff.Allowed(url)) closedUrls.Push(url);
        opened.RemoveAt(index);
        foreach (var t in opened) if (t.Opener == tab) t.Opener = null;
        if (tab.View is { } view)
        {
            tab.View = null;
            CleanupView(view);
        }
        if (selected != tab) { SyncTabs(); SaveSession(); return; }
        selected = null;
        if (opened.Count == 0) { UpdateAddress(); UpdateNavButtons(); UpdateWindowTitle(); SyncTabs(); SaveSession(); NewTab(); }
        else
        {
            var next = tab.Opener is { Closed: false } opener && opened.Contains(opener)
                ? opener
                : displayIndex + 1 < display.Count ? display[displayIndex + 1]
                : display[displayIndex - 1];
            SyncTabs();
            SelectTab(next);
        }
    }

    void ReopenClosedTab()
    {
        if (closedUrls.TryPop(out var url)) _ = OpenInteractive(url);
    }

    List<BrowserTab> DisplayTabs() => [.. opened.OrderByDescending(t => t.Pinned)];

    int DropSlotToTargetIndex(BrowserTab tab, bool targetPinned, int slot)
    {
        if (tab.Pinned == targetPinned)
        {
            var currentIndex = opened.Where(t => t.Pinned == targetPinned).ToList().IndexOf(tab);
            if (currentIndex >= 0 && slot > currentIndex) slot--;
        }
        return slot;
    }

    bool MoveTab(BrowserTab tab, bool targetPinned, int targetIndex)
    {
        if (closed || tab.Closed || !opened.Contains(tab)) return false;
        var pinned = opened.Where(t => t.Pinned).ToList();
        var loose = opened.Where(t => !t.Pinned).ToList();
        var source = tab.Pinned ? pinned : loose;
        var oldIndex = source.IndexOf(tab);
        if (oldIndex < 0) return false;
        var dest = targetPinned ? pinned : loose;
        var maxIndex = targetPinned == tab.Pinned ? dest.Count - 1 : dest.Count;
        var clamped = Math.Clamp(targetIndex, 0, Math.Max(0, maxIndex));
        if (tab.Pinned == targetPinned && oldIndex == clamped) return false;

        source.RemoveAt(oldIndex);
        tab.Pinned = targetPinned;
        dest.Insert(clamped, tab);

        opened.Clear();
        opened.AddRange(pinned);
        opened.AddRange(loose);

        if (tab.Pinned && tab.View is { IsDisposed: false, CoreWebView2: { } vCore } && TryCore(() => vCore.IsSuspended))
            TryCore(() => vCore.Resume());

        SyncTabs();
        SaveSession();
        return true;
    }

    void MoveSelectedTab(int delta)
    {
        if (selected is not { } tab || restoring || dragTab is not null) return;
        var group = opened.Where(t => t.Pinned == tab.Pinned).ToList();
        var index = group.IndexOf(tab);
        if (index < 0) return;
        var next = Math.Clamp(index + delta, 0, group.Count - 1);
        if (next == index) return;
        if (!MoveTab(tab, tab.Pinned, next)) return;
        EnsureTabVisible(tab);
    }

    (int cols, int tileW, int tileH, int gap) PinGridMetrics(int count)
    {
        var room = Math.Max(S(100), sidebar.ClientSize.Width - sidebar.Padding.Horizontal);
        var gap = S(4);
        var tileH = S(34);
        var cols = count <= 3 ? 3 : 4;
        var tileW = Math.Max(S(24), (room - (cols - 1) * gap) / cols);
        return (cols, tileW, tileH, gap);
    }

    bool StartTabDrag(BrowserTab tab, Point screenPt)
    {
        if (closed || restoring || chromeHidden || !opened.Contains(tab)) return false;
        if (dragTab is not null) CancelTabDrag();
        dragTab = tab;
        dragControl = tabControls.TryGetValue(tab, out var ctrl) ? ctrl as TabChromeControl : null;
        dragScreenPt = screenPt;
        pinDropPlaceholder = false;
        dragScrollTimer.Start();
        UpdateTabDrag(screenPt);
        return true;
    }

    void UpdateTabDrag(Point screenPt)
    {
        if (dragTab is null) return;
        dragScreenPt = screenPt;
        dragTarget = HitTestTabDrop(screenPt, dragTab);
        UpdateDropVisuals(dragTab, dragTarget);
    }

    void DropTabDrag(Point screenPt)
    {
        if (dragTab is not { } tab) return;
        dragScreenPt = screenPt;
        var target = HitTestTabDrop(screenPt, tab);
        dragScrollTimer.Stop();
        dragTab = null;
        dragControl = null;
        dragTarget = null;
        dropLine.Visible = false;
        dropLine.Parent?.Controls.Remove(dropLine);
        sidePins.SetDropPreview(false, false, null);

        var moved = target is { } t && MoveTab(tab, t.Pinned, DropSlotToTargetIndex(tab, t.Pinned, t.Slot));
        if (pinDropPlaceholder)
        {
            pinDropPlaceholder = false;
            if (!moved && !opened.Any(x => x.Pinned))
            {
                sidePins.Visible = false;
                sidePins.Height = 0;
            }
        }
    }

    void CancelTabDrag()
    {
        dragScrollTimer.Stop();
        var ctrl = dragControl;
        dragTab = null;
        dragControl = null;
        dragTarget = null;
        ClearDropVisuals();
        if (ctrl is { IsDisposed: false }) ctrl.CancelDrag();
    }

    void ClearDropVisuals()
    {
        dropLine.Visible = false;
        dropLine.Parent?.Controls.Remove(dropLine);
        sidePins.SetDropPreview(false, false, null);
        if (pinDropPlaceholder)
        {
            pinDropPlaceholder = false;
            if (!opened.Any(t => t.Pinned))
            {
                sidePins.Visible = false;
                sidePins.Height = 0;
            }
        }
    }

    void ShowDropLine(Control host, Rectangle bounds)
    {
        if (dropLine.Parent != host)
        {
            dropLine.Parent?.Controls.Remove(dropLine);
            host.Controls.Add(dropLine);
        }
        dropLine.Bounds = bounds;
        dropLine.Visible = true;
        dropLine.BringToFront();
    }

    TabDropTarget? HitTestTabDrop(Point screenPt, BrowserTab tab)
    {
        var formPt = PointToClient(screenPt);
        if (!ClientRectangle.Contains(formPt)) return null;

        if (vertical)
        {
            var sideRect = BoundsInForm(sidebar);
            if (formPt.X < sideRect.Left || formPt.X > sideRect.Right + S(32)) return null;
            var sidePt = sidebar.PointToClient(screenPt);
            if (sidePt.Y < sideAddressWrap.Top) return null;

            var hasPins = opened.Any(t => t.Pinned);
            if (!hasPins && !tab.Pinned && !pinDropPlaceholder && sidePt.Y < sideTabs.Top)
            {
                pinDropPlaceholder = true;
                sidePins.Height = S(34) + S(10);
                sidePins.Visible = true;
                sidebar.PerformLayout();
            }

            if (sidePt.Y < sideTabs.Top && (hasPins || pinDropPlaceholder))
            {
                if (!hasPins) return new TabDropTarget(true, 0);
                var pinControls = sidePins.Controls.OfType<PinSquare>().OrderBy(c => c.Top).ThenBy(c => c.Left).ToArray();
                var (cols, _, tileH, gap) = PinGridMetrics(pinControls.Length);
                var pinPt = sidePins.PointToClient(screenPt);
                var rows = (pinControls.Length + cols - 1) / cols;
                var rowStep = tileH + gap;
                var row = Math.Clamp(pinPt.Y < 0 ? 0 : pinPt.Y / rowStep, 0, Math.Max(0, rows - 1));
                var rowStart = row * cols;
                var rowEnd = Math.Min(pinControls.Length, rowStart + cols);
                var slot = rowEnd;
                for (var i = rowStart; i < rowEnd; i++)
                {
                    if (pinPt.X < pinControls[i].Left + pinControls[i].Width / 2)
                    {
                        slot = i;
                        break;
                    }
                }
                return new TabDropTarget(true, slot);
            }

            var looseControls = sideTabs.Items.OfType<TabRow>().ToArray();
            var listPt = sideTabs.PointToClient(screenPt);
            var contentY = listPt.Y + sideTabs.ScrollOffset;
            var listSlot = looseControls.Length;
            for (var i = 0; i < looseControls.Length; i++)
            {
                if (contentY < looseControls[i].Top + looseControls[i].Height / 2)
                {
                    listSlot = i;
                    break;
                }
            }
            return new TabDropTarget(false, listSlot);
        }

        var stripRect = BoundsInForm(stripTabs);
        if (formPt.Y > topStrip.Bottom + S(28) || formPt.X < stripRect.Left - S(24) || formPt.X > stripRect.Right + S(32))
            return null;

        var stripPt = stripTabs.PointToClient(screenPt);
        var pins = stripTabs.Controls.OfType<PinSquare>().OrderBy(c => c.Left).ToArray();
        var pills = stripTabs.Controls.OfType<TabPill>().OrderBy(c => c.Left).ToArray();
        bool targetPinned;
        if (pins.Length > 0 && pills.Length > 0)
        {
            var boundaryX = stripHairline is { IsDisposed: false } hl
                ? hl.Left + hl.Width / 2
                : (pins[^1].Right + pills[0].Left) / 2;
            targetPinned = stripPt.X < boundaryX;
        }
        else targetPinned = pins.Length > 0;

        Control[] group = targetPinned ? pins : pills;
        var stripSlot = group.Length;
        for (var i = 0; i < group.Length; i++)
        {
            if (stripPt.X < group[i].Left + group[i].Width / 2)
            {
                stripSlot = i;
                break;
            }
        }
        return new TabDropTarget(targetPinned, stripSlot);
    }

    void UpdateDropVisuals(BrowserTab tab, TabDropTarget? target)
    {
        if (target is not { } t)
        {
            dropLine.Visible = false;
            dropLine.Parent?.Controls.Remove(dropLine);
            sidePins.SetDropPreview(false, pinDropPlaceholder, null);
            return;
        }

        var thickness = Math.Max(2, S(2));
        if (vertical)
        {
            if (t.Pinned)
            {
                var pinControls = sidePins.Controls.OfType<PinSquare>().OrderBy(c => c.Top).ThenBy(c => c.Left).ToArray();
                if (pinControls.Length == 0)
                {
                    dropLine.Visible = false;
                    dropLine.Parent?.Controls.Remove(dropLine);
                    sidePins.SetDropPreview(true, true, null);
                    return;
                }

                var (cols, tileW, tileH, gap) = PinGridMetrics(pinControls.Length);
                if (!tab.Pinned && t.Slot == pinControls.Length && pinControls.Length % cols != 0)
                {
                    dropLine.Visible = false;
                    dropLine.Parent?.Controls.Remove(dropLine);
                    var slotRect = new Rectangle((t.Slot % cols) * (tileW + gap), (t.Slot / cols) * (tileH + gap), tileW, tileH);
                    sidePins.SetDropPreview(true, false, slotRect);
                    return;
                }

                sidePins.SetDropPreview(!tab.Pinned, false, null);
                var barW = Math.Max(2, S(3));
                int lineX, lineY;
                if (t.Slot < pinControls.Length)
                {
                    var c = pinControls[t.Slot];
                    var rowStart = (t.Slot / cols) * cols;
                    lineX = t.Slot == rowStart ? Math.Max(0, c.Left - barW / 2) : (pinControls[t.Slot - 1].Right + c.Left - barW) / 2;
                    lineY = c.Top;
                }
                else
                {
                    var last = pinControls[^1];
                    lineX = Math.Min(Math.Max(0, sidePins.ClientSize.Width - barW), last.Right + (gap - barW) / 2);
                    lineY = last.Top;
                }
                ShowDropLine(sidePins, new Rectangle(Math.Max(0, lineX), lineY, barW, tileH));
                return;
            }

            sidePins.SetDropPreview(false, pinDropPlaceholder, null);
            var looseControls = sideTabs.Items.OfType<TabRow>().ToArray();
            int y;
            if (looseControls.Length == 0) y = S(2);
            else if (t.Slot == 0) y = looseControls[0].Top - sideTabs.ScrollOffset;
            else if (t.Slot < looseControls.Length) y = (looseControls[t.Slot - 1].Bottom + looseControls[t.Slot].Top) / 2 - sideTabs.ScrollOffset;
            else y = looseControls[^1].Bottom + S(1) - sideTabs.ScrollOffset;
            var maxY = Math.Max(0, sideTabs.ClientSize.Height - thickness);
            var clampedY = Math.Clamp(y - thickness / 2, 0, maxY);
            ShowDropLine(sideTabs, new Rectangle(S(4), clampedY, Math.Max(0, sideTabs.ClientSize.Width - S(8)), thickness));
            return;
        }

        var pins = stripTabs.Controls.OfType<PinSquare>().OrderBy(c => c.Left).ToArray();
        var pills = stripTabs.Controls.OfType<TabPill>().OrderBy(c => c.Left).ToArray();
        Control[] group = t.Pinned ? pins : pills;
        var vBarW = Math.Max(2, S(3));
        var vBarH = Math.Min(stripTabs.ClientSize.Height, S(22));
        int x;
        if (group.Length == 0) x = 0;
        else if (t.Slot == 0) x = group[0].Left;
        else if (t.Slot < group.Length) x = (group[t.Slot - 1].Right + group[t.Slot].Left) / 2;
        else x = group[^1].Right;
        var maxX = Math.Max(0, stripTabs.ClientSize.Width - vBarW);
        ShowDropLine(stripTabs, new Rectangle(Math.Clamp(x - vBarW / 2, 0, maxX), (stripTabs.ClientSize.Height - vBarH) / 2, vBarW, vBarH));
    }

    void HandleDragAutoScroll()
    {
        if (dragTab is null) { dragScrollTimer.Stop(); return; }
        var formPt = PointToClient(dragScreenPt);
        if (!ClientRectangle.Contains(formPt)) return;
        if (vertical)
        {
            var sideRect = BoundsInForm(sidebar);
            if (formPt.X < sideRect.Left || formPt.X > sideRect.Right + S(32)) return;
            var pt = sideTabs.PointToClient(dragScreenPt);
            var edge = S(24);
            var step = S(10);
            if (pt.Y >= 0 && pt.Y < edge)
            {
                if (sideTabs.ScrollBy(-step)) UpdateTabDrag(dragScreenPt);
            }
            else if (pt.Y > sideTabs.ClientSize.Height - edge && pt.Y <= sideTabs.ClientSize.Height + S(16))
            {
                if (sideTabs.ScrollBy(step)) UpdateTabDrag(dragScreenPt);
            }
        }
        else if (stripContentWidth > stripTabs.Width)
        {
            if (formPt.Y > topStrip.Bottom + S(28)) return;
            var pt = stripTabs.PointToClient(dragScreenPt);
            var edge = S(24);
            var step = S(12);
            if (pt.X >= -S(12) && pt.X < edge)
            {
                if (ScrollStripBy(-step)) UpdateTabDrag(dragScreenPt);
            }
            else if (pt.X > stripTabs.ClientSize.Width - edge && pt.X <= stripTabs.ClientSize.Width + S(12))
            {
                if (ScrollStripBy(step)) UpdateTabDrag(dragScreenPt);
            }
        }
    }

    void SetOrientation(bool sidebarMode, bool persistSettings = true)
    {
        CancelTabDrag();
        vertical = sidebarMode;
        SuspendLayout();
        var surface = sidebarMode ? Theme.Frame : Theme.Ground;
        var hover = sidebarMode ? Theme.SideHover : Theme.StripHover;
        var activeFill = sidebarMode ? Theme.SideActive : Theme.StripActive;

        foreach (var b in new[] { backBtn, forwardBtn, reloadBtn, shareBtn, updateBtn, settingsBtn })
            b.SetSurface(surface, hover, activeFill);
        address.SetSurface(surface, hover);

        if (sidebarMode)
        {
            sideTopBand.Controls.AddRange([backBtn, forwardBtn, reloadBtn, updateBtn, shareBtn, settingsBtn]);
            sideAddressWrap.Controls.Add(address);
            ApplyChromeVisibility();
            LayoutSideTopBand();
            address.SetBounds(0, 0, sideAddressWrap.ClientSize.Width, S(28));
        }
        else
        {
            stripScroll = 0;
            topStrip.Controls.AddRange([backBtn, forwardBtn, reloadBtn, address, updateBtn, shareBtn, settingsBtn]);
            stripTabs.SendToBack();
            ApplyChromeVisibility();
        }
        ResumeLayout(true);
        RebuildTabs();
        if (persistSettings && (settings.VerticalTabs != sidebarMode || !settingsExisted))
        {
            settings = settings with { VerticalTabs = sidebarMode };
            settingsExisted = true;
            SaveSettings();
            settingsDialog?.SyncFrom(settings);
        }
        SaveSession();
    }

    void AttachTabMenu(Control c, BrowserTab tab, string title)
    {
        tips.SetToolTip(c, title);
        var menu = new ContextMenuStrip();
        var pinItem = menu.Items.Add(tab.Pinned ? "Unpin tab" : "Pin tab", null, (_, _) =>
            MoveTab(tab, !tab.Pinned, tab.Pinned ? 0 : int.MaxValue));
        var closeItem = menu.Items.Add("Close tab", null, (_, _) => CloseTab(tab));
        closeItem.Enabled = !tab.Pinned;
        menu.Opening += (_, _) => {
            pinItem.Text = tab.Pinned ? "Unpin tab" : "Pin tab";
            closeItem.Enabled = !tab.Pinned;
        };
        c.ContextMenuStrip = menu;
        c.Disposed += (_, _) => {
            if (!closed && !IsDisposed) tips.SetToolTip(c, null);
            if (menu.IsDisposed) return;
            if (!closed && !IsDisposed && IsHandleCreated)
                BeginInvoke(() => { if (!menu.IsDisposed) menu.Dispose(); });
            else
                menu.Dispose();
        };
    }

    static string ClipTitle(string title) => title.Length > 250 ? title[..250] : title;

    bool MatchesControlKind(Control c, BrowserTab tab) => tab.Pinned
        ? c is PinSquare ps && ps.OnFrame == vertical
        : (vertical ? c is TabRow : c is TabPill);

    Control GetOrCreateTabControl(BrowserTab tab)
    {
        var title = ClipTitle(tab.Title);
        var active = tab == selected;
        var icon = favicons.Get(tab.Url);
        if (tabControls.TryGetValue(tab, out var existing) && !existing.IsDisposed && MatchesControlKind(existing, tab))
        {
            ((ITabItem)existing).UpdateTab(title, tab.Url, active, icon);
            if (tips.GetToolTip(existing) != title) tips.SetToolTip(existing, title);
            return existing;
        }
        var drag = new TabDragCallbacks(pt => StartTabDrag(tab, pt), UpdateTabDrag, DropTabDrag, CancelTabDrag);
        Control created = tab.Pinned
            ? new PinSquare(title, tab.Url, active, icon, onFrame: vertical, () => SelectTab(tab), () => CloseTab(tab), drag)
            : vertical
                ? new TabRow(title, tab.Url, active, icon, () => SelectTab(tab), () => CloseTab(tab), drag)
                : new TabPill(title, tab.Url, active, icon, () => SelectTab(tab), () => CloseTab(tab), drag);
        AttachTabMenu(created, tab, title);
        tabControls[tab] = created;
        return created;
    }

    void RefreshTab(BrowserTab tab)
    {
        if (!tabControls.TryGetValue(tab, out var c) || c.IsDisposed || !MatchesControlKind(c, tab))
        {
            SyncTabs();
            return;
        }
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            var title = ClipTitle(tab.Title);
            ((ITabItem)c).UpdateTab(title, tab.Url, tab == selected, favicons.Get(tab.Url));
            if (tips.GetToolTip(c) != title) tips.SetToolTip(c, title);
        }
        finally { drawTabsTicks += Stopwatch.GetTimestamp() - t0; }
    }

    void RebuildTabs()
    {
        CancelTabDrag();
        var t0 = Stopwatch.GetTimestamp();
        sidePins.SuspendLayout();
        stripTabs.SuspendLayout();
        try
        {
            foreach (var c in tabControls.Values)
                if (!c.IsDisposed) c.Dispose();
            tabControls.Clear();
            if (stripHairline is not null)
            {
                if (!stripHairline.IsDisposed) stripHairline.Dispose();
                stripHairline = null;
            }
            if (!vertical)
            {
                sidePins.Visible = false;
                sidePins.Height = 0;
            }
        }
        finally
        {
            sidePins.ResumeLayout(false);
            stripTabs.ResumeLayout(false);
            drawTabsTicks += Stopwatch.GetTimestamp() - t0;
        }
        SyncTabs();
        EnsureTabVisible(selected);
    }

    void PruneStaleControls()
    {
        if (tabControls.Count == 0) return;
        foreach (var (tab, ctrl) in tabControls.ToArray())
        {
            if (tab.Closed || !opened.Contains(tab) || ctrl.IsDisposed || !MatchesControlKind(ctrl, tab))
            {
                tabControls.Remove(tab);
                if (!ctrl.IsDisposed) ctrl.Dispose();
            }
        }
    }

    void SyncTabs()
    {
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            var pinned = opened.Where(t => t.Pinned).ToArray();
            var loose = opened.Where(t => !t.Pinned).ToArray();

            if (vertical)
            {
                sidePins.SuspendLayout();
                PruneStaleControls();
                if (pinned.Length == 0)
                {
                    sidePins.Visible = false;
                    sidePins.Height = 0;
                }
                else
                {
                    var (cols, tileW, tileH, gap) = PinGridMetrics(pinned.Length);
                    for (var i = 0; i < pinned.Length; i++)
                    {
                        var sq = GetOrCreateTabControl(pinned[i]);
                        if (sq.Parent != sidePins) sidePins.Controls.Add(sq);
                        if (sidePins.Controls.GetChildIndex(sq) != i) sidePins.Controls.SetChildIndex(sq, i);
                        sq.SetBounds((i % cols) * (tileW + gap), (i / cols) * (tileH + gap), tileW, tileH);
                    }
                    var rows = (pinned.Length + cols - 1) / cols;
                    sidePins.Height = rows * tileH + (rows - 1) * gap + S(10);
                    sidePins.Visible = true;
                }
                sidePins.ResumeLayout();

                var list = new List<Control>(loose.Length);
                foreach (var tab in loose) list.Add(GetOrCreateTabControl(tab));
                sideTabs.SetItems(list, sideNewTab, S(28), S(2));
            }
            else
            {
                stripTabs.SuspendLayout();
                PruneStaleControls();
                var ordered = new List<Control>(opened.Count + 1);
                foreach (var tab in pinned) ordered.Add(GetOrCreateTabControl(tab));
                if (pinned.Length > 0 && loose.Length > 0)
                {
                    stripHairline ??= new Hairline();
                    ordered.Add(stripHairline);
                }
                else if (stripHairline is not null)
                {
                    if (!stripHairline.IsDisposed) stripHairline.Dispose();
                    stripHairline = null;
                }
                foreach (var tab in loose) ordered.Add(GetOrCreateTabControl(tab));
                for (var i = 0; i < ordered.Count; i++)
                {
                    var c = ordered[i];
                    if (c.Parent != stripTabs) stripTabs.Controls.Add(c);
                    if (stripTabs.Controls.GetChildIndex(c) != i) stripTabs.Controls.SetChildIndex(c, i);
                }
                stripTabs.ResumeLayout();
                LayoutTopStrip();
            }
        }
        finally { drawTabsTicks += Stopwatch.GetTimestamp() - t0; }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && dragTab is not null)
        {
            CancelTabDrag();
            return true;
        }
        if (keyData == Keys.Escape && address.InputFocused)
        {
            UpdateAddress();
            if (selected?.View is { IsDisposed: false } view) FocusView(view);
            else ActiveControl = null;
            return true;
        }
        switch (keyData)
        {
            case Keys.Control | Keys.L: address.FocusInput(); return true;
            case Keys.Control | Keys.T: NewTab(); return true;
            case Keys.Control | Keys.W: CloseCurrent(); return true;
            case Keys.Control | Keys.Shift | Keys.T: ReopenClosedTab(); return true;
            case Keys.Control | Keys.Shift | Keys.S: SetOrientation(!vertical); return true;
            case Keys.Control | Keys.Oemcomma: OpenSettings(); return true;
            case Keys.Control | Keys.Shift | Keys.PageUp: MoveSelectedTab(-1); return true;
            case Keys.Control | Keys.Shift | Keys.PageDown: MoveSelectedTab(1); return true;
            case Keys.Alt | Keys.Left: GoBack(); return true;
            case Keys.Alt | Keys.Right: GoForward(); return true;
            case Keys.Control | Keys.R:
            case Keys.F5: ReloadCurrent(); return true;
            case Keys.Control | Keys.J: OpenDownloads(); return true;
        }
        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            var key = keyData & Keys.KeyCode;
            if (key is >= Keys.D1 and <= Keys.D9)
            {
                var display = DisplayTabs();
                if (display.Count > 0)
                {
                    var idx = key == Keys.D9 ? display.Count - 1 : (int)(key - Keys.D1);
                    if (idx < display.Count) SelectTab(display[idx]);
                }
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void SaveSettings()
    {
        if (closed) return;
        try { SettingsStore.Save(settingsFile, settings); }
        catch { }
    }

    void OpenSettings()
    {
        if (closed || IsDisposed) return;
        if (settingsDialog is { IsDisposed: false })
        {
            if (settingsDialog.WindowState == FormWindowState.Minimized)
                settingsDialog.WindowState = FormWindowState.Normal;
            settingsDialog.Activate();
            return;
        }
        settingsDialog = new SettingsDialog(settings, noUpdateCheck, ApplySettings, ClearBrowsingDataAsync);
        settingsDialog.FormClosed += (_, _) => { settingsDialog = null; };
        settingsDialog.Show(this);
    }

    Task ApplyBlockingAsync(bool enabled) =>
        Task.WhenAll(opened.ToArray()
            .Where(t => !t.Closed && t.View is { IsDisposed: false })
            .Select(t => t.SetShieldEnabledAsync(enabled)));

    void ApplySettings(AppSettings next)
    {
        var prev = settings;
        settings = next;
        settingsExisted = true;
        SaveSettings();

        if (next.VerticalTabs != vertical)
            SetOrientation(next.VerticalTabs, persistSettings: false);

        if (next.ShareEnabled != prev.ShareEnabled)
        {
            shareBtn.Visible = next.ShareEnabled;
            if (!next.ShareEnabled) pending?.Cancel();
            if (vertical) LayoutSideTopBand();
            else LayoutTopStrip();
        }

        if (next.BlockingEnabled != prev.BlockingEnabled)
            _ = ApplyBlockingAsync(next.BlockingEnabled);

        if (!next.SleepingTabsEnabled && prev.SleepingTabsEnabled)
        {
            foreach (var tab in opened.ToArray())
                if (!tab.Closed && tab.View is { IsDisposed: false, CoreWebView2: { } c } && TryCore(() => c.IsSuspended))
                    TryCore(() => c.Resume());
        }

        if (!next.AutoUpdateCheck && prev.AutoUpdateCheck && !updating)
            updateCts?.Cancel();
    }

    async Task ClearBrowsingDataAsync()
    {
        var profile = opened
            .Select(t => t.View is { IsDisposed: false } v ? v.CoreWebView2?.Profile : null)
            .FirstOrDefault(p => p is not null);
        if (profile is null && selected is not null)
        {
            await EnsureView(selected);
            profile = selected.View is { IsDisposed: false } v ? v.CoreWebView2?.Profile : null;
        }
        if (profile is null)
            throw new InvalidOperationException("Browser profile is not ready yet.");
        await profile.ClearBrowsingDataAsync();
    }

    async Task SharePage()
    {
        if (!settings.ShareEnabled || selected is null || busy) return;
        var tab = selected;
        if (tab.View is not { IsDisposed: false, CoreWebView2: { } core } || !PageHandoff.Allowed(core.Source))
        {
            MessageBox.Show(this, "Open an HTTP or HTTPS page first.", "Share page");
            return;
        }
        var source = core.Source;
        var host = new Uri(source).IdnHost;
        var confirmed = confirmShareOverride is not null
            ? confirmShareOverride(host)
            : MessageBox.Show(this, $"Offer a one-time text snapshot from {host}?\n\nAny program running as you can read the snapshot while the offer is open (up to 60 seconds, once). Share only when you trust what's running. This is read-only and does not allow tab control.",
                "Share page with a local agent", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        if (!confirmed) return;
        busy = true;
        shareBtn.Enabled = false;
        try
        {
            if (TryCore(() => core.IsSuspended)) TryCore(() => core.Resume());
            var snapshot = await PageHandoff.Capture(core);
            if (closed) return;
            if (selected != tab || tab.View is not { IsDisposed: false, CoreWebView2: { } current } || source != current.Source)
                throw new InvalidOperationException("The active page changed. Nothing was shared.");
            pending?.Cancel();
            using var token = new CancellationTokenSource();
            pending = token;
            try
            {
                var offer = PageHandoff.Offer(snapshot, token.Token);
                using var dialog = new ShareDialog(offer.Name, () => token.Cancel());
                dialog.Show(this);
                dialog.FocusCode();
                shareBtn.SetActive(true);
                tips.SetToolTip(shareBtn, "Share code ready");
                await offer.Completion;
            }
            finally { token.Cancel(); if (ReferenceEquals(pending, token)) pending = null; }
        }
        catch (Exception ex)
        {
            if (!closed)
            {
                if (shareErrorOverride is not null) shareErrorOverride(ex.Message);
                else MessageBox.Show(this, ex.Message, "Could not share text");
            }
        }
        finally
        {
            busy = false;
            if (!closed && !IsDisposed && !shareBtn.IsDisposed)
            {
                shareBtn.Enabled = true;
                shareBtn.SetActive(false);
                tips.SetToolTip(shareBtn, "Share page text once with a local agent");
            }
        }
    }

    async Task CheckForUpdateAsync()
    {
        if (updateCts is not { } cts) return;
        var release = await Updater.CheckForUpdateAsync(sessionFile, cts.Token);
        if (release is null || closed || IsDisposed) return;
        availableUpdate = release;
        var label = $"Update to {release.VersionText}";
        updateBtn.AccessibleName = label;
        tips.SetToolTip(updateBtn, label);
        updateBtn.Visible = true;
        if (vertical) LayoutSideTopBand();
        else LayoutTopStrip();
    }

    async Task ApplyUpdate()
    {
        if (availableUpdate is not { } release || updating || closed) return;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            _ = OpenInteractive(release.ReleasePageUrl);
            return;
        }
        var kind = Updater.DetectInstallKind(exePath);
        if (kind != InstallKind.Direct)
        {
            var cmd = kind == InstallKind.Scoop ? "scoop update browse" : "winget upgrade slyim.browse";
            MessageBox.Show(this,
                $"browse {release.VersionText} is available.\n\nThis copy is managed by {kind}. Update it by running:\n\n{cmd}",
                $"Update to {release.VersionText}", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var installDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(installDir) || !Updater.IsInstallDirWritable(installDir))
        {
            _ = OpenInteractive(release.ReleasePageUrl);
            return;
        }
        if (MessageBox.Show(this,
            $"Download and install browse {release.VersionText}?\n\nbrowse will verify the SHA-256 digest, save your tabs, and restart.",
            $"Update to {release.VersionText}", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        updating = true;
        updateBtn.Enabled = false;
        tips.SetToolTip(updateBtn, $"Updating to {release.VersionText}...");
        string? workDir = null;
        try
        {
            if (updateCts is not { } cts) return;
            (workDir, var extractDir) = await Updater.DownloadAndExtractAsync(release, cts.Token);
            if (closed || IsDisposed) return;
            Updater.SwapFiles(exePath, extractDir);
            SaveSession();
            sessionReady = false;
            releaseSessionLock();
            try { Updater.Launch(exePath, launchArgs); }
            catch
            {
                Updater.RestoreOldExe(exePath, exePath + ".old");
                throw;
            }
            ExitCode = 0;
            Close();
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (!closed && !IsDisposed)
                MessageBox.Show(this, ex.Message, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Updater.TryDeleteDirectory(workDir);
            updating = false;
            if (!closed && !IsDisposed && !updateBtn.IsDisposed)
            {
                updateBtn.Enabled = true;
                tips.SetToolTip(updateBtn, $"Update to {release.VersionText}");
            }
        }
    }

    Rectangle BoundsInForm(Control c) => RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));

    bool CheckHorizontalLayout()
    {
        if (vertical || !topStrip.Visible || sidebar.Visible) return false;
        var stripRect = BoundsInForm(topStrip);
        var pageRect = BoundsInForm(pages);
        return stripRect.Width > S(500) && stripRect.Height >= S(40) && stripRect.Height <= S(64)
            && stripRect.Bottom <= pageRect.Top && !stripRect.IntersectsWith(pageRect)
            && pageRect.Width >= ClientSize.Width - 2;
    }

    bool CheckVerticalLayout()
    {
        if (!vertical || !sidebar.Visible || topStrip.Visible) return false;
        var sideRect = BoundsInForm(sidebar);
        var pageRect = BoundsInForm(pages);
        return sideRect.Width >= S(200) && sideRect.Width <= S(260) && sideRect.Height > S(200)
            && sideRect.Right <= pageRect.Left && !sideRect.IntersectsWith(pageRect);
    }

    bool CheckPinnedFirst()
    {
        var items = vertical
            ? sidePins.Controls.OfType<Control>().OrderBy(c => c.Top).ThenBy(c => c.Left).OfType<ITabItem>()
                .Concat(sideTabs.Items.OfType<Control>().OrderBy(c => c.Top).OfType<ITabItem>()).ToArray()
            : stripTabs.Controls.OfType<Control>().OrderBy(c => c.Left).OfType<ITabItem>().ToArray();
        var pinnedCount = opened.Count(t => t.Pinned);
        return items.Length == opened.Count
            && items.Take(pinnedCount).All(i => i.Pinned)
            && items.Skip(pinnedCount).All(i => !i.Pinned);
    }

    async Task WaitForLoad(BrowserTab tab)
    {
        await EnsureView(tab);
        if ((tab.Loaded && !tab.Navigating) || tab.Closed || tab.View is not { IsDisposed: false, CoreWebView2: { } core }) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess && tab.Loaded) done.TrySetResult();
        }
        core.NavigationCompleted += Completed;
        try
        {
            if (tab.Loaded && !tab.Navigating) return;
            await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (tab.View is { IsDisposed: false, CoreWebView2: { } c }) c.NavigationCompleted -= Completed;
        }
    }

    async Task WaitForSelectedLoad()
    {
        if (selected is not { } tab || tab.Url is null) return;
        await WaitForLoad(tab);
    }

    (double memoryMB, int processCount) MeasureMemory()
    {
        using var self = Process.GetCurrentProcess();
        var seen = new HashSet<int> { self.Id };
        var bytes = self.WorkingSet64;
        var count = 1;
        foreach (var info in environment!.GetProcessInfos())
        {
            if (info.ProcessId <= 0 || !seen.Add(info.ProcessId)) continue;
            try
            {
                using var proc = Process.GetProcessById(info.ProcessId);
                bytes += proc.WorkingSet64;
                count++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return (Math.Round(bytes / (1024.0 * 1024.0), 1), count);
    }

    internal async Task SleepIdleTabs(TimeSpan threshold)
    {
        if (closed || !settings.SleepingTabsEnabled) return;
        var now = Environment.TickCount64;
        var minAgeMs = (long)Math.Max(0, threshold.TotalMilliseconds);
        foreach (var tab in opened.ToArray())
        {
            if (closed) return;
            if (tab == selected || tab.Closed || tab.Pinned || tab.Navigating || tab.Suspending) continue;
            if (tab.HiddenSince is not { } hiddenSince || now - hiddenSince < minAgeMs) continue;
            if (tab.View is not { IsDisposed: false, Visible: false, CoreWebView2: { } core }) continue;
            if (TryCore(() => core.IsSuspended || core.IsDocumentPlayingAudio, true)) continue;
            tab.Suspending = true;
            try
            {
                await core.TrySuspendAsync();
                if (!closed && !tab.Closed && (selected == tab || tab.Pinned) && tab.View is { IsDisposed: false, CoreWebView2: { } vc } && TryCore(() => vc.IsSuspended))
                    TryCore(() => vc.Resume());
            }
            catch { }
            finally { tab.Suspending = false; }
        }
    }

    int HitTestAt(Point screenPt) => unchecked((int)SendMessage(Handle, WM_NCHITTEST, IntPtr.Zero, MakeLParam(screenPt.X, screenPt.Y)).ToInt64());

    static Point CenterOnScreen(Control c) => c.PointToScreen(new Point(c.Width / 2, c.Height / 2));

    async Task SmokeTest()
    {
        if (smokePhase == "sleep")
        {
            var sleepTabs = new List<BrowserTab>(5);
            for (var i = 1; i <= 5; i++)
            {
                var t = await Open($"{fixture}/s{i}");
                await WaitForLoad(t);
                sleepTabs.Add(t);
            }
            SelectTab(sleepTabs[4]);
            sleepTabs[1].Pinned = true;
            SyncTabs();
            var (memoryBeforeMB, _) = MeasureMemory();
            await SleepIdleTabs(TimeSpan.Zero);
            await Task.Delay(2000);
            var (memoryAfterMB, _) = MeasureMemory();
            var suspended = sleepTabs.Select(t => t.View?.CoreWebView2?.IsSuspended ?? false).ToArray();
            var pinnedSuspended = suspended[1];
            SelectTab(sleepTabs[0]);
            var firstCore = sleepTabs[0].View?.CoreWebView2 ?? throw new InvalidOperationException("First sleep tab view is not ready.");
            var resumeWatch = Stopwatch.StartNew();
            while (resumeWatch.Elapsed < TimeSpan.FromSeconds(5) && (!sleepTabs[0].View!.Visible || firstCore.IsSuspended))
                await Task.Delay(50);
            var resumedTitle = JsonSerializer.Deserialize<string>(await firstCore.ExecuteScriptAsync("document.title"));
            File.WriteAllText(smokeReport!, JsonSerializer.Serialize(new
            {
                suspended,
                memoryBeforeMB,
                memoryAfterMB,
                resumedTitle,
                pinnedSuspended
            }));
            ExitCode = 0;
            Close();
            return;
        }
        if (smokePhase == "restore")
        {
            await Restore();
            await WaitForSelectedLoad();
            var firstPinned = opened.FirstOrDefault(t => t.Pinned);
            if (firstPinned is not null) CloseTab(firstPinned);
            PerformLayout();
            File.WriteAllText(smokeReport!, JsonSerializer.Serialize(new
            {
                tabs = opened.Select(t => new
                {
                    url = t.Url,
                    title = t.Title,
                    pinned = t.Pinned,
                    loaded = t.Loaded,
                    hasFavicon = favicons.Get(t.Url) is not null
                }),
                selectedIndex = selected is null ? -1 : opened.IndexOf(selected),
                vertical,
                horizontalLayout = CheckHorizontalLayout(),
                pinnedSurvivesClose = firstPinned is null || opened.Contains(firstPinned),
                pinnedFirst = CheckPinnedFirst(),
                canSaveSession
            }));
            ExitCode = 0;
            Close();
            return;
        }
        if (smokePhase == "perf")
        {
            var restoreWatch = Stopwatch.StartNew();
            await Restore();
            var restoreMs = restoreWatch.ElapsedMilliseconds;
            await WaitForSelectedLoad();
            long startupMs;
            using (var self = Process.GetCurrentProcess())
                startupMs = (long)Math.Round((DateTime.Now - self.StartTime).TotalMilliseconds);
            var drawTabsMs = Math.Round(drawTabsTicks * 1000.0 / Stopwatch.Frequency, 2);
            double titleUpdatesMs = 0;
            if (selected is { } current)
            {
                var ctrlBefore = tabControls[current];
                var wasLoaded = current.Loaded;
                var prevSaved = current.SavedTitle;
                var titleWatch = Stopwatch.StartNew();
                for (var i = 0; i < 200; i++)
                {
                    current.Loaded = false;
                    current.SavedTitle = $"Title {i}";
                    RefreshTab(current);
                }
                titleUpdatesMs = Math.Round(titleWatch.Elapsed.TotalMilliseconds, 2);
                if (!ReferenceEquals(tabControls[current], ctrlBefore) || ctrlBefore.AccessibleName != "Title 199")
                    throw new InvalidOperationException("Tab control was not reused or updated on title change.");
                current.SavedTitle = prevSaved;
                current.Loaded = wasLoaded;
                RefreshTab(current);
            }
            await Task.Delay(2000);
            var (memoryMB, processCount) = MeasureMemory();
            File.WriteAllText(smokeReport!, JsonSerializer.Serialize(new
            {
                startupMs,
                envMs,
                restoreMs,
                drawTabsMs,
                titleUpdatesMs,
                tabCount = opened.Count,
                webViewsCreated = opened.Count(t => t.View is { IsDisposed: false, CoreWebView2: not null }),
                loadedTabs = opened.Count(t => t.Loaded),
                memoryMB,
                processCount
            }));
            ExitCode = 0;
            Close();
            return;
        }
        sessionReady = true;
        var ready = environment;
        environment = null; // simulate Enter before WebView2 is ready: must neither throw nor open a tab
        await Go("example.com");
        var startupSafe = opened.Count == 0 && selected is null;
        environment = ready;
        await Go(fixture + "/first?omit=token#omit"); // no tab yet: Enter opens one
        var first = selected!;
        await WaitForLoad(first);
        var noTabNavigation = startupSafe && opened.Count == 1;
        var second = await Open(fixture + "/second");
        await WaitForLoad(second);
        var secondTitle = second.Title;
        SetOrientation(false);
        PerformLayout();
        var horizontal = CheckHorizontalLayout();
        var stripEmptyHit = HitTestAt(topStrip.PointToScreen(new Point((stripNewTabBtn.Right + shareBtn.Left) / 2, topStrip.Height / 2)));
        SetOrientation(true);
        PerformLayout();
        var orientations = horizontal && CheckVerticalLayout();
        var captionHidden = Math.Abs(RectangleToScreen(ClientRectangle).Top - Bounds.Top) <= 1;
        var firstTabRow = sideTabs.Items.OfType<TabRow>().First();
        var hitTests = new
        {
            sideEmpty = HitTestAt(sideTopBand.PointToScreen(new Point((reloadBtn.Right + shareBtn.Left) / 2, sideTopBand.Height / 2))),
            maxButton = HitTestAt(CenterOnScreen(maxBtn)),
            closeButton = HitTestAt(CenterOnScreen(closeBtn)),
            backButton = HitTestAt(CenterOnScreen(backBtn)),
            tabRow = HitTestAt(CenterOnScreen(firstTabRow)),
            topEdge = HitTestAt(PointToScreen(new Point(ClientSize.Width / 2, 2))),
            stripEmpty = stripEmptyHit,
        };
        var maxCenter1 = CenterOnScreen(maxBtn);
        var maxLp1 = MakeLParam(maxCenter1.X, maxCenter1.Y);
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTMAXBUTTON, maxLp1);
        SendMessage(Handle, WM_NCLBUTTONUP, (IntPtr)HTMAXBUTTON, maxLp1);
        var becameMax = WindowState == FormWindowState.Maximized;
        PerformLayout();
        var maxCenter2 = CenterOnScreen(maxBtn);
        var maxLp2 = MakeLParam(maxCenter2.X, maxCenter2.Y);
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTMAXBUTTON, maxLp2);
        SendMessage(Handle, WM_NCLBUTTONUP, (IntPtr)HTMAXBUTTON, maxLp2);
        var becameNormal = WindowState == FormWindowState.Normal;
        var maximizeToggles = becameMax && becameNormal;

        SetChromeHidden(true);
        PerformLayout();
        var hiddenState = ChromeHidden && !sidebar.Visible && !topStrip.Visible && !minBtn.Visible && !maxBtn.Visible && !closeBtn.Visible
            && HitTestAt(PointToScreen(new Point(ClientSize.Width / 2, S(8)))) == HTCAPTION;
        SetChromeHidden(false);
        PerformLayout();
        var chromeHiddenWorks = hiddenState && !ChromeHidden && sidebar.Visible && !topStrip.Visible && minBtn.Visible && maxBtn.Visible && closeBtn.Visible && CheckVerticalLayout();

        // Review finding smoke checks: overflow, wheel 1x, deferred menu dispose, NC tracking on down, caption tooltips.
        var prevSize = Size;
        var tempTabs = Enumerable.Range(1, 20).Select(i => new BrowserTab { SavedTitle = $"Overflow {i}" }).ToList();
        opened.AddRange(tempTabs);
        Size = MinimumSize;
        SetOrientation(false);
        PerformLayout();
        var winClient = ClientRectangle;
        var stripRect = BoundsInForm(stripTabs);
        var plusRect = BoundsInForm(stripNewTabBtn);
        var settingsRect = BoundsInForm(settingsBtn);
        var shareRect = BoundsInForm(shareBtn);
        var minRect = minBtn.Bounds;
        var maxRect = maxBtn.Bounds;
        var closeRect = closeBtn.Bounds;
        var chromeButtons = new[] { plusRect, settingsRect, shareRect, minRect, maxRect, closeRect };
        var stripOverflowSafe = chromeButtons.All(r => r.Width > 0 && r.Height > 0 && winClient.Contains(r) && !stripRect.IntersectsWith(r))
            && !plusRect.IntersectsWith(settingsRect) && !plusRect.IntersectsWith(shareRect)
            && topStrip.Controls.GetChildIndex(settingsBtn) < topStrip.Controls.GetChildIndex(stripTabs)
            && topStrip.Controls.GetChildIndex(shareBtn) < topStrip.Controls.GetChildIndex(stripTabs)
            && Controls.GetChildIndex(minBtn) < Controls.GetChildIndex(topStrip);
        var firstStripPill = stripTabs.Controls.OfType<TabPill>().First();
        var pillLeftBefore = firstStripPill.Left;
        var pillPt = CenterOnScreen(firstStripPill);
        SendMessage(firstStripPill.Handle, WM_MOUSEWHEEL, MakeLParam(0, -120), MakeLParam(pillPt.X, pillPt.Y));
        var stripWheelScrolled = firstStripPill.Left < pillLeftBefore;

        SetOrientation(true);
        PerformLayout();
        var wheelTabRow = sideTabs.Items.OfType<TabRow>().First();
        var sideTopBefore = wheelTabRow.Parent!.Top;
        var sidePt = CenterOnScreen(wheelTabRow);
        SendMessage(wheelTabRow.Handle, WM_MOUSEWHEEL, MakeLParam(0, -120), MakeLParam(sidePt.X, sidePt.Y));
        var sideWheelStep = sideTopBefore - wheelTabRow.Parent!.Top;

        var menuTab = tempTabs[^1];
        var menuCtrl = tabControls[menuTab];
        var tabMenu = menuCtrl.ContextMenuStrip!;
        tabMenu.Items[1]!.PerformClick();
        var menuAliveDuringClick = !tabMenu.IsDisposed;
        await Task.Delay(50);
        var menuDeferredDispose = menuAliveDuringClick && tabMenu.IsDisposed;

        foreach (var t in tempTabs) { t.Closed = true; opened.Remove(t); }
        Size = prevSize;
        SyncTabs();
        PerformLayout();

        ClearCaptionInteraction();
        var downPt = CenterOnScreen(maxBtn);
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTMAXBUTTON, MakeLParam(downPt.X, downPt.Y));
        var ncTrackedOnDown = ncTracking;
        ClearCaptionInteraction();

        var captionNoTooltips = string.IsNullOrEmpty(tips.GetToolTip(minBtn))
            && string.IsNullOrEmpty(tips.GetToolTip(maxBtn))
            && string.IsNullOrEmpty(tips.GetToolTip(closeBtn))
            && minBtn.AccessibleName == "Minimize"
            && maxBtn.AccessibleName == "Maximize"
            && closeBtn.AccessibleName == "Close";

        SelectTab(first);
        first.Pinned = true;
        SyncTabs();
        CloseTab(first);
        var kept = opened.Contains(first);
        CloseTab(second);
        var restored = selected?.Title;
        var snapshot = await PageHandoff.Capture(first.View?.CoreWebView2 ?? throw new InvalidOperationException("First tab view is not ready."));
        var offer = PageHandoff.Offer(snapshot, CancellationToken.None);
        var ps = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(AppContext.BaseDirectory, "read-page.ps1"), offer.Name }) ps.ArgumentList.Add(arg);
        using var consumer = Process.Start(ps) ?? throw new InvalidOperationException("Could not start the snapshot reader.");
        var received = await consumer.StandardOutput.ReadToEndAsync();
        var consumerError = await consumer.StandardError.ReadToEndAsync();
        await consumer.WaitForExitAsync();
        if (consumer.ExitCode != 0) throw new InvalidOperationException(consumerError);
        await offer.Completion;
        var oneUse = false;
        using (var client = new NamedPipeClientStream(".", offer.Name, PipeDirection.In, PipeOptions.Asynchronous))
        {
            try { await client.ConnectAsync(250); }
            catch (TimeoutException) { oneUse = true; }
        }
        var ads = await Open(fixture + "/ads?id=42&utm_source=smoke&fbclid=abc#keep");
        await WaitForLoad(ads);
        var adsCore = ads.View?.CoreWebView2 ?? throw new InvalidOperationException("Ads tab view is not ready.");
        var adStatusRaw = "null";
        var workerAdStatusRaw = "null";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            adStatusRaw = await adsCore.ExecuteScriptAsync("window.__adStatus ?? null");
            workerAdStatusRaw = await adsCore.ExecuteScriptAsync("window.__workerAdStatus ?? null");
            if (adStatusRaw != "null" && workerAdStatusRaw != "null") break;
            await Task.Delay(50);
        }
        var adsFinalUrl = adsCore.Source;
        using var adStatusDoc = JsonDocument.Parse(adStatusRaw);
        var adStatus = adStatusDoc.RootElement.Clone();
        using var workerAdStatusDoc = JsonDocument.Parse(workerAdStatusRaw);
        var workerAdStatus = workerAdStatusDoc.RootElement.Clone();
        var adHidden = await adsCore.ExecuteScriptAsync("getComputedStyle(document.getElementById('ad')).display === 'none'") == "true";
        var okVisible = await adsCore.ExecuteScriptAsync("getComputedStyle(document.getElementById('ok')).display !== 'none'") == "true";
        var blockedCount = ads.BlockedCount();
        CloseTab(ads);

        var fixtureFirstUri = new Uri(fixture + "/first");
        var fixtureAdsUri = new Uri(fixture + "/ads");
        var segmentUri = new Uri("https://segment.com/");
        var segmentCdnUri = new Uri("https://cdn.segment.com/a.js");
        var adFrameUri = new Uri("https://doubleclick.net/frame");
        var shield = new
        {
            thirdPartyBlocked = Shield.ShouldBlock(new Uri("http://doubleclick.net/pixel.js"), fixtureAdsUri),
            firstPartyAllowed = !Shield.ShouldBlock(segmentUri, segmentUri),
            firstPartySubdomainAllowed = !Shield.ShouldBlock(segmentCdnUri, segmentUri),
            topLevelNavExempt = Shield.IsTopLevelDocument(CoreWebView2WebResourceContext.Document, segmentUri, segmentUri)
                && !Shield.ShouldBlock(segmentUri, fixtureFirstUri, CoreWebView2WebResourceContext.Document, segmentUri),
            firstPartyDuringNavAllowed = !Shield.ShouldBlock(segmentCdnUri, fixtureFirstUri, CoreWebView2WebResourceContext.Script, segmentUri),
            subframeAdBlocked = !Shield.IsTopLevelDocument(CoreWebView2WebResourceContext.Document, adFrameUri, fixtureAdsUri)
                && Shield.ShouldBlock(adFrameUri, fixtureAdsUri, CoreWebView2WebResourceContext.Document, null),
        };

        var redirectThrew = false;
        BrowserTab? redirectTab = null;
        try
        {
            redirectTab = await Open(fixture + "/redirect-tracked");
            await WaitForLoad(redirectTab);
        }
        catch
        {
            redirectThrew = true;
            redirectTab ??= selected;
        }
        var redirectTracked = new
        {
            openThrew = redirectThrew,
            finalUrl = redirectTab?.Url,
            title = redirectTab?.Title,
        };
        if (redirectTab is not null && redirectTab != first) CloseTab(redirectTab);

        var loopTab = await Open(fixture + "/loop");
        await WaitForLoad(loopTab);
        var loopGuard = new
        {
            settled = loopTab is { Loaded: true, Title: "Loop settled" },
            title = loopTab.Title,
            url = loopTab.Url,
        };
        CloseTab(loopTab);

        var updaterSelfCheck = Updater.SelfCheck();
        var settingsRoundTrip = SettingsStore.SelfCheck(Path.GetDirectoryName(settingsFile)!);
        var phosphorLoaded = Theme.PhosphorLoaded;
        var iconFontFamily = Theme.IconFont.FontFamily.Name;
        var captionFontFamily = Theme.CaptionIconFont.FontFamily.Name;

        OpenSettings();
        var dlg1 = settingsDialog;
        OpenSettings();
        var dlg2 = settingsDialog;
        var settingsDialogOk = dlg1 is { IsDisposed: false, Visible: true }
            && ReferenceEquals(dlg1, dlg2)
            && dlg1.AccessibleName == "Settings"
            && settingsBtn.AccessibleName == "Settings";
        dlg1?.Close();

        ApplySettings(settings with { ShareEnabled = false });
        var shareHiddenWhenOff = !shareBtn.Visible;
        ApplySettings(settings with { ShareEnabled = true });
        var shareToggleWorks = shareHiddenWhenOff && shareBtn.Visible;

        // Verify blocking toggle: off lets a fixture ad request through and leaves #ad visible; back on blocks and hides it.
        ApplySettings(settings with { BlockingEnabled = false });
        await ApplyBlockingAsync(false);
        var adsToggleTab = await Open();
        var adsToggleCore = adsToggleTab.View?.CoreWebView2 ?? throw new InvalidOperationException("Ads toggle tab view is not ready.");
        adsToggleCore.WebResourceRequested += (_, e) => {
            if (e.Response is null && e.Request.Uri.Contains("doubleclick.net", StringComparison.OrdinalIgnoreCase))
                e.Response = environment!.CreateWebResourceResponse(null, 200, "FixtureOK", "Access-Control-Allow-Origin: *");
        };
        adsToggleTab.Loaded = false;
        adsToggleCore.Navigate(fixture + "/ads");
        await WaitForLoad(adsToggleTab);
        var adOffRaw = "null";
        var adOffWatch = Stopwatch.StartNew();
        while (adOffWatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            adOffRaw = await adsToggleCore.ExecuteScriptAsync("window.__adStatus ?? null");
            if (adOffRaw != "null") break;
            await Task.Delay(50);
        }
        using var adOffDoc = JsonDocument.Parse(adOffRaw);
        var adStatusWhenBlockingOff = adOffDoc.RootElement.Clone();
        var adVisibleWhenBlockingOff = await adsToggleCore.ExecuteScriptAsync("getComputedStyle(document.getElementById('ad')).display !== 'none'") == "true";

        ApplySettings(settings with { BlockingEnabled = true });
        await ApplyBlockingAsync(true);
        var adHiddenAfterBlockingOn = await adsToggleCore.ExecuteScriptAsync("getComputedStyle(document.getElementById('ad')).display === 'none'") == "true";
        await adsToggleCore.ExecuteScriptAsync("window.__reblockStatus = null; fetch('http://doubleclick.net/pixel.js').then(r => window.__reblockStatus = r.status).catch(() => window.__reblockStatus = 'error');");
        var adOnRaw = "null";
        var adOnWatch = Stopwatch.StartNew();
        while (adOnWatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            adOnRaw = await adsToggleCore.ExecuteScriptAsync("window.__reblockStatus ?? null");
            if (adOnRaw != "null") break;
            await Task.Delay(50);
        }
        using var adOnDoc = JsonDocument.Parse(adOnRaw);
        var adStatusAfterBlockingOn = adOnDoc.RootElement.Clone();
        CloseTab(adsToggleTab);

        // Verify link cleaning toggle: off keeps utm_source and fbclid params intact.
        ApplySettings(settings with { CleanLinksEnabled = false });
        var keepUtmUrl = fixture + "/first?id=7&utm_source=keepme&fbclid=keep123#frag";
        var keepUtmTab = await Open(keepUtmUrl);
        await WaitForLoad(keepUtmTab);
        var urlWhenCleanOff = keepUtmTab.View?.CoreWebView2?.Source;
        CloseTab(keepUtmTab);
        ApplySettings(settings with { CleanLinksEnabled = true });

        var cliFlags = Program.ParseArgs(["--no-update-check"]) is { NoUpdateCheck: true, ProfileDir: null, Smoke: false }
            && Program.ParseArgs(["--profile", @"C:\tmp\p", "--no-update-check"]) is { NoUpdateCheck: true, ProfileDir: @"C:\tmp\p", Smoke: false }
            && Program.ParseArgs(["--no-update-check", "--profile", @"C:\tmp\p"]) is { NoUpdateCheck: true, ProfileDir: @"C:\tmp\p", Smoke: false }
            && Program.ParseArgs(["--profile"]) is null
            && Program.ParseArgs(["--smoke-test", @"C:\tmp\r.json", "http://127.0.0.1:1", "restore", "--no-update-check"]) is { Smoke: true, SmokePhase: "restore", NoUpdateCheck: true }
            && Program.ParseArgs(["--smoke-test", @"C:\tmp\r.json", "http://127.0.0.1:1", "bogus"]) is null;
        async Task DispatchClick(CoreWebView2 core, int x, int y)
        {
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 }));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }));
        }

        async Task<string[]> ReadMsgs(CoreWebView2 core) =>
            JsonSerializer.Deserialize<string[]>(await core.ExecuteScriptAsync("window.__msgs")) ?? [];

        var popupOpener = await Open(fixture + "/popup-opener");
        await WaitForLoad(popupOpener);
        var popupSibling = await Open(fixture + "/second");
        await WaitForLoad(popupSibling);
        SelectTab(popupOpener);
        var openerCore = popupOpener.View?.CoreWebView2 ?? throw new InvalidOperationException("Popup opener view is not ready.");
        var baseTabs = opened.Count;
        await Task.Delay(250);
        var popupMsgs = await ReadMsgs(openerCore);
        var untrustedBlocked = !popupMsgs.Contains("untrusted") && opened.Count == baseTabs && selected == popupOpener;

        await DispatchClick(openerCore, 60, 20);
        var popupWait = Stopwatch.StartNew();
        while (popupWait.Elapsed < TimeSpan.FromSeconds(8))
        {
            popupMsgs = await ReadMsgs(openerCore);
            if (popupMsgs.Contains("ok") && opened.Count == baseTabs && selected == popupOpener) break;
            await Task.Delay(50);
        }
        var directOk = popupMsgs.Contains("ok") && opened.Count == baseTabs && selected == popupOpener;

        await DispatchClick(openerCore, 200, 20);
        popupWait.Restart();
        while (popupWait.Elapsed < TimeSpan.FromSeconds(8))
        {
            popupMsgs = await ReadMsgs(openerCore);
            if (popupMsgs.Contains("blank-ok") && opened.Count == baseTabs && selected == popupOpener) break;
            await Task.Delay(50);
        }
        var blankNavOk = popupMsgs.Contains("blank-ok") && opened.Count == baseTabs && selected == popupOpener;

        await DispatchClick(openerCore, 340, 20);
        popupWait.Restart();
        while (popupWait.Elapsed < TimeSpan.FromSeconds(8) && (opened.Count != baseTabs + 1 || selected == popupOpener))
            await Task.Delay(50);
        var blankTab = selected;
        var blankSessionClean = false;
        if (blankTab is not null && blankTab != popupOpener && blankTab.IsPopup && blankTab.Url is null)
        {
            SaveSession();
            var savedRaw = File.Exists(sessionFile) ? File.ReadAllText(sessionFile) : "";
            var savedState = Session.Load(sessionFile);
            blankSessionClean = !savedRaw.Contains("about:blank", StringComparison.OrdinalIgnoreCase)
                && savedState?.Tabs.Length == baseTabs;
            await openerCore.ExecuteScriptAsync("window.__blankPopup && window.__blankPopup.close()");
            popupWait.Restart();
            while (popupWait.Elapsed < TimeSpan.FromSeconds(8) && (opened.Count != baseTabs || selected != popupOpener))
                await Task.Delay(50);
        }
        var blankClosedBackToOpener = opened.Count == baseTabs && selected == popupOpener;
        if (opened.Count > baseTabs)
        {
            foreach (var extra in opened.Where(t => t.IsPopup).ToArray()) CloseTab(extra, recordClosed: false);
        }
        CloseTab(popupSibling);
        CloseTab(popupOpener);
        var popup = new
        {
            directOk,
            blankNavOk,
            untrustedBlocked,
            blankSessionClean,
            blankClosedBackToOpener,
            allowlistOk = IsPopupAllowed("") && IsPopupAllowed("about:blank")
                && IsPopupAllowed("https://accounts.google.com/o/oauth2/auth")
                && !IsPopupAllowed("file:///C:/Windows/win.ini")
                && !IsPopupAllowed("javascript:alert(1)")
                && !IsPopupAllowed("data:text/html,hi"),
        };

        var faviconHost = FaviconCache.HostOf(first.Url)!;
        var faviconFile = favicons.PathForHost(faviconHost);
        var faviconWait = Stopwatch.StartNew();
        while (faviconWait.Elapsed < TimeSpan.FromSeconds(5) && (favicons.Get(first.Url) is null || !File.Exists(faviconFile)))
            await Task.Delay(50);
        var faviconCached = favicons.Get(first.Url) is not null && File.Exists(faviconFile);
        var addressDisplayOk = AddressLine.FormatDisplayUrl("https://en.wikipedia.org/wiki/Web_browser") == "en.wikipedia.org/wiki/Web_browser"
            && AddressLine.FormatDisplayUrl("https://example.com/") == "example.com"
            && AddressLine.FormatDisplayUrl("http://127.0.0.1:8080/first") == "http://127.0.0.1:8080/first"
            && !address.InputFocused && !address.Input.Visible;

        // Tab drag-to-reorder, drag-to-pin/unpin, Escape/outside cancel, keyboard reorder, Ctrl+1..9, and session order.
        void SimDrag(Control ctrl, Point targetScreen, bool cancelWithEscape = false)
        {
            var startLocal = new Point(Math.Max(2, ctrl.Width / 3), ctrl.Height / 2);
            SendMessage(ctrl.Handle, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, MakeLParam(startLocal.X, startLocal.Y));
            var nudge = new Point(startLocal.X, startLocal.Y + Math.Max(8, SystemInformation.DragSize.Height + 2));
            SendMessage(ctrl.Handle, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, MakeLParam(nudge.X, nudge.Y));
            var moveLocal = ctrl.PointToClient(targetScreen);
            SendMessage(ctrl.Handle, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, MakeLParam(moveLocal.X, moveLocal.Y));
            moveLocal = ctrl.PointToClient(targetScreen);
            SendMessage(ctrl.Handle, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, MakeLParam(moveLocal.X, moveLocal.Y));
            if (cancelWithEscape)
            {
                var esc = Message.Create(Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                ProcessCmdKey(ref esc, Keys.Escape);
            }
            var endLocal = ctrl.PointToClient(targetScreen);
            SendMessage(ctrl.Handle, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(endLocal.X, endLocal.Y));
        }

        void PressKey(Keys keyData)
        {
            var km = Message.Create(Handle, 0x0100, (IntPtr)(keyData & Keys.KeyCode), IntPtr.Zero);
            ProcessCmdKey(ref km, keyData);
        }

        first.Pinned = false;
        var d1 = new BrowserTab { SavedUrl = fixture + "/d1", SavedTitle = "Drag 1" };
        var d2 = new BrowserTab { SavedUrl = fixture + "/d2", SavedTitle = "Drag 2" };
        var d3 = new BrowserTab { SavedUrl = fixture + "/d3", SavedTitle = "Drag 3" };
        opened.AddRange([d1, d2, d3]);
        SelectTab(first);
        SetOrientation(true);
        PerformLayout();

        // Sidebar reorder (unselected tab d2 before first) preserves selection.
        SimDrag(tabControls[d2], tabControls[first].PointToScreen(new Point(10, 2)));
        var sidebarReorder = opened.SequenceEqual([d2, first, d1, d3]) && selected == first && DisplayTabs().SequenceEqual(opened);

        // Plain click selects.
        var clickCtrl = tabControls[d1];
        var clickPt = new Point(10, clickCtrl.Height / 2);
        SendMessage(clickCtrl.Handle, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, MakeLParam(clickPt.X, clickPt.Y));
        SendMessage(clickCtrl.Handle, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(clickPt.X, clickPt.Y));
        var plainClickSelects = selected == d1;
        SelectTab(first);

        // Escape cancels active drag without reordering or changing selection.
        SimDrag(tabControls[d3], tabControls[d2].PointToScreen(new Point(10, 2)), cancelWithEscape: true);
        var escapeCancels = dragTab is null && opened.SequenceEqual([d2, first, d1, d3]) && selected == first;

        // Releasing outside the window cancels drag.
        SimDrag(tabControls[d3], PointToScreen(new Point(-80, -80)));
        var outsideCancels = dragTab is null && opened.SequenceEqual([d2, first, d1, d3]) && selected == first;

        // Drag unpinned tab up to pin when pin grid is empty.
        SimDrag(tabControls[d2], sideAddressWrap.PointToScreen(new Point(20, sideAddressWrap.Height / 2)));
        var sidebarDragToPinEmpty = d2.Pinned && sidePins.Visible && opened.SequenceEqual([d2, first, d1, d3]) && selected == first;

        // Drag second unpinned tab into populated pin grid at slot 0, then reorder within pin grid.
        SimDrag(tabControls[d3], tabControls[d2].PointToScreen(new Point(2, tabControls[d2].Height / 2)));
        var sidebarDragToPinSlot = d3.Pinned && opened.SequenceEqual([d3, d2, first, d1]) && selected == first;
        SimDrag(tabControls[d3], tabControls[d2].PointToScreen(new Point(tabControls[d2].Width - 2, tabControls[d2].Height / 2)));
        var sidebarPinGridReorder = opened.SequenceEqual([d2, d3, first, d1]) && selected == first;

        // Drag pinned tile down into unpinned list to unpin at specific position.
        SimDrag(tabControls[d2], tabControls[first].PointToScreen(new Point(10, tabControls[first].Height - 2)));
        var sidebarDragToUnpin = !d2.Pinned && d3.Pinned && opened.SequenceEqual([d3, first, d2, d1]) && selected == first;

        // Top strip: drag across boundary to pin, unpin, and reorder pills.
        SetOrientation(false);
        PerformLayout();
        SimDrag(tabControls[d1], tabControls[d3].PointToScreen(new Point(2, tabControls[d3].Height / 2)));
        var stripPin = d1.Pinned && d3.Pinned && opened.SequenceEqual([d1, d3, first, d2]) && selected == first;
        SimDrag(tabControls[d3], tabControls[d2].PointToScreen(new Point(tabControls[d2].Width - 2, tabControls[d2].Height / 2)));
        var stripUnpin = !d3.Pinned && d1.Pinned && opened.SequenceEqual([d1, first, d2, d3]) && selected == first;
        SimDrag(tabControls[d3], tabControls[first].PointToScreen(new Point(2, tabControls[first].Height / 2)));
        var stripReorder = opened.SequenceEqual([d1, d3, first, d2]) && selected == first;

        // Ctrl+1..9 follow the new order.
        PressKey(Keys.Control | Keys.D1);
        var ctrl1 = selected == d1;
        PressKey(Keys.Control | Keys.D2);
        var ctrl2 = selected == d3;
        PressKey(Keys.Control | Keys.D9);
        var ctrl9 = selected == d2;
        var ctrlDigitFollowsOrder = ctrl1 && ctrl2 && ctrl9;
        SelectTab(first);

        // Keyboard reordering within group (Ctrl+Shift+PageUp / PageDown).
        PressKey(Keys.Control | Keys.Shift | Keys.PageUp);
        var kbUp = opened.SequenceEqual([d1, first, d3, d2]) && selected == first;
        PressKey(Keys.Control | Keys.Shift | Keys.PageUp);
        var kbUpClamp = opened.SequenceEqual([d1, first, d3, d2]) && !first.Pinned;
        PressKey(Keys.Control | Keys.Shift | Keys.PageDown);
        var kbDown = opened.SequenceEqual([d1, d3, first, d2]) && selected == first;
        var keyboardReorder = kbUp && kbUpClamp && kbDown;

        // Saved session.json reflects pinned-first order and selected index.
        SaveSession();
        var savedDragSession = Session.Load(sessionFile);
        var sessionOrderMatches = savedDragSession is not null
            && savedDragSession.Selected == 2
            && savedDragSession.Tabs.Select(t => (t.Url, t.Pinned)).SequenceEqual([
                (fixture + "/d1", true),
                (fixture + "/d3", false),
                (first.Url!, false),
                (fixture + "/d2", false),
            ]);

        foreach (var t in new[] { d1, d2, d3 }) { t.Closed = true; opened.Remove(t); }
        first.Pinned = true;
        SelectTab(first);
        SetOrientation(true);
        PerformLayout();

        var tabDrag = new
        {
            sidebarReorder,
            plainClickSelects,
            escapeCancels,
            outsideCancels,
            sidebarDragToPinEmpty,
            sidebarDragToPinSlot,
            sidebarPinGridReorder,
            sidebarDragToUnpin,
            stripPin,
            stripUnpin,
            stripReorder,
            ctrlDigitFollowsOrder,
            keyboardReorder,
            sessionOrderMatches,
        };

        // #29: POST form on a utm_* URL keeps its body and is not converted to a GET.
        var postTab = await Open(fixture + "/post-form");
        await WaitForLoad(postTab);
        var postCore = postTab.View?.CoreWebView2 ?? throw new InvalidOperationException("Post tab view is not ready.");
        await postCore.ExecuteScriptAsync("document.getElementById('f').submit()");
        var postWait = Stopwatch.StartNew();
        while (postWait.Elapsed < TimeSpan.FromSeconds(5) && (postTab.Title != "Post received" || postTab.Navigating))
            await Task.Delay(50);
        var postClean = new
        {
            url = postCore.Source,
            echo = JsonSerializer.Deserialize<string>(await postCore.ExecuteScriptAsync("document.getElementById('echo')?.textContent ?? null")),
        };
        CloseTab(postTab, recordClosed: false);

        // #35: blob:http(s) URLs open while PageHandoff.Allowed stays strict for Share/session.
        var blobTab = await Open(fixture + "/blob-page");
        await WaitForLoad(blobTab);
        var blobCore = blobTab.View?.CoreWebView2 ?? throw new InvalidOperationException("Blob tab view is not ready.");
        var baseBlobTabs = opened.Count;
        await Task.Delay(150);
        await DispatchClick(blobCore, 60, 20);
        var blobWait = Stopwatch.StartNew();
        while (blobWait.Elapsed < TimeSpan.FromSeconds(5)
            && (opened.Count != baseBlobTabs + 1 || selected == blobTab || !(selected?.View?.CoreWebView2?.Source?.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ?? false)))
            await Task.Delay(50);
        var blobChild = selected != blobTab ? selected : null;
        string? blobChildSource = null;
        string? blobChildText = null;
        if (blobChild?.View?.CoreWebView2 is { } blobChildCore)
        {
            blobChildSource = blobChildCore.Source;
            blobWait.Restart();
            while (blobWait.Elapsed < TimeSpan.FromSeconds(5))
            {
                blobChildText = JsonSerializer.Deserialize<string>(await blobChildCore.ExecuteScriptAsync("document.getElementById('b')?.textContent ?? null"));
                if (blobChildText == "blob-ok") break;
                await Task.Delay(50);
            }
        }
        var blobUrl = new
        {
            openedOk = blobChild is not null
                && (blobChildSource?.StartsWith("blob:" + fixture, StringComparison.OrdinalIgnoreCase) ?? false)
                && blobChildText == "blob-ok"
                && blobChild.Title == "Blob doc",
            notShareableOrSaved = blobChild is { Url: null }
                && blobChildSource is not null
                && !PageHandoff.Allowed(blobChildSource),
            helperOk = IsNavigable("https://example.com")
                && IsNavigable("http://localhost:3000")
                && IsNavigable("about:blank")
                && IsNavigable("blob:https://example.com/123")
                && IsNavigable("blob:http://127.0.0.1:80/a")
                && !IsNavigable("blob:file:///C:/a")
                && !IsNavigable("blob:null/123")
                && !IsNavigable("blob: https://example.com/123")
                && !IsNavigable("file:///C:/a")
                && !IsNavigable("javascript:alert(1)")
                && !IsNavigable("data:text/html,hi")
                && !PageHandoff.Allowed("blob:https://example.com/123")
                && !PageHandoff.Allowed("about:blank"),
        };
        if (blobChild is not null) CloseTab(blobChild, recordClosed: false);
        CloseTab(blobTab, recordClosed: false);

        // #31: PermissionRequested denies non-user-initiated high-risk kinds and disables SavesInProfile for sensitive kinds.
        var geoTab = await Open();
        var geoCore = geoTab.View?.CoreWebView2 ?? throw new InvalidOperationException("Geo tab view is not ready.");
        var geoObserved = false;
        var geoKind = CoreWebView2PermissionKind.UnknownPermission;
        var geoSavesInProfile = true;
        var geoState = CoreWebView2PermissionState.Deny;
        geoCore.PermissionRequested += (_, e) => {
            if (e.PermissionKind == CoreWebView2PermissionKind.Geolocation)
            {
                geoKind = e.PermissionKind;
                geoSavesInProfile = e.SavesInProfile;
                geoState = e.State;
                geoObserved = true;
                e.State = CoreWebView2PermissionState.Deny;
            }
        };
        geoCore.Navigate(fixture + "/geo");
        var geoWait = Stopwatch.StartNew();
        while (geoWait.Elapsed < TimeSpan.FromSeconds(5) && !geoObserved)
            await Task.Delay(50);
        CloseTab(geoTab, recordClosed: false);
        var permissions = new
        {
            geoObserved,
            geoSavesInProfileFalse = geoKind == CoreWebView2PermissionKind.Geolocation && !geoSavesInProfile && geoState == CoreWebView2PermissionState.Default,
            policyOk = DecidePermission(CoreWebView2PermissionKind.ClipboardRead, false) == (CoreWebView2PermissionState.Deny, false)
                && DecidePermission(CoreWebView2PermissionKind.MultipleAutomaticDownloads, false) == (CoreWebView2PermissionState.Deny, false)
                && DecidePermission(CoreWebView2PermissionKind.LocalFonts, false) == (CoreWebView2PermissionState.Deny, false)
                && DecidePermission(CoreWebView2PermissionKind.WindowManagement, false) == (CoreWebView2PermissionState.Deny, false)
                && DecidePermission(CoreWebView2PermissionKind.MidiSystemExclusiveMessages, false) == (CoreWebView2PermissionState.Deny, false)
                && DecidePermission(CoreWebView2PermissionKind.ClipboardRead, true) == (CoreWebView2PermissionState.Default, false)
                && DecidePermission(CoreWebView2PermissionKind.Camera, true) == (CoreWebView2PermissionState.Default, false)
                && DecidePermission(CoreWebView2PermissionKind.Microphone, true) == (CoreWebView2PermissionState.Default, false)
                && DecidePermission(CoreWebView2PermissionKind.Geolocation, true) == (CoreWebView2PermissionState.Default, false)
                && DecidePermission(CoreWebView2PermissionKind.FileReadWrite, true) == (CoreWebView2PermissionState.Default, false)
                && DecidePermission(CoreWebView2PermissionKind.Notifications, true) == (CoreWebView2PermissionState.Default, true),
        };

        // #32: Downloads — cancel from background tabs, confirm executables, Ctrl+J opens default download dialog.
        var smokeDownloadsDir = Path.Combine(Path.GetDirectoryName(smokeReport!)!, "Downloads");
        Directory.CreateDirectory(smokeDownloadsDir);
        var dlBg = await Open(fixture + "/first");
        await WaitForLoad(dlBg);
        var dlFg = await Open(fixture + "/download-page");
        await WaitForLoad(dlFg);
        var dlBgCore = dlBg.View?.CoreWebView2 ?? throw new InvalidOperationException("Background download tab view is not ready.");
        var dlFgCore = dlFg.View?.CoreWebView2 ?? throw new InvalidOperationException("Foreground download tab view is not ready.");
        if (dlFgCore.Profile is not null) dlFgCore.Profile.DefaultDownloadFolderPath = smokeDownloadsDir;

        var bgDownloadFired = false;
        var bgDownloadCancelled = false;
        dlBgCore.DownloadStarting += (_, e) => {
            bgDownloadFired = true;
            bgDownloadCancelled = e.Cancel;
        };
        await dlBgCore.ExecuteScriptAsync("const a=document.createElement('a');a.href='/download/note.txt';a.download='note.txt';a.click();");
        var dlWait = Stopwatch.StartNew();
        while (dlWait.Elapsed < TimeSpan.FromSeconds(5) && !bgDownloadFired)
            await Task.Delay(50);

        CoreWebView2DownloadOperation? fgOp = null;
        dlFgCore.DownloadStarting += (_, e) => { fgOp = e.DownloadOperation; };

        string? exeDenyName = null;
        string? exeDenyHost = null;
        confirmDownloadOverride = (name, host) => { exeDenyName = name; exeDenyHost = host; return false; };
        await DispatchClick(dlFgCore, 60, 20);
        dlWait.Restart();
        while (dlWait.Elapsed < TimeSpan.FromSeconds(5)
            && (exeDenyName is null || fgOp is null || fgOp.State == CoreWebView2DownloadState.InProgress))
            await Task.Delay(50);
        var exeDenied = exeDenyName is not null
            && exeDenyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && exeDenyHost == "127.0.0.1"
            && fgOp is { State: CoreWebView2DownloadState.Interrupted, InterruptReason: CoreWebView2DownloadInterruptReason.UserCanceled }
            && !Directory.EnumerateFiles(smokeDownloadsDir, "*.exe").Any();

        fgOp = null;
        var exeAllowPrompted = false;
        confirmDownloadOverride = (_, _) => { exeAllowPrompted = true; return true; };
        await DispatchClick(dlFgCore, 60, 20);
        dlWait.Restart();
        while (dlWait.Elapsed < TimeSpan.FromSeconds(5)
            && (!exeAllowPrompted || fgOp is null || (fgOp.State == CoreWebView2DownloadState.InProgress && fgOp.BytesReceived == 0 && !File.Exists(fgOp.ResultFilePath))))
            await Task.Delay(50);
        // Edge SmartScreen may hold unverified .exe downloads in InProgress after our handler allows them.
        var exeAllowed = exeAllowPrompted
            && fgOp is not null
            && fgOp.State != CoreWebView2DownloadState.Interrupted
            && (fgOp.State == CoreWebView2DownloadState.Completed || fgOp.BytesReceived > 0 || File.Exists(fgOp.ResultFilePath));
        if (fgOp is { State: CoreWebView2DownloadState.InProgress }) fgOp.Cancel();
        confirmDownloadOverride = null;

        PressKey(Keys.Control | Keys.J);
        dlWait.Restart();
        while (dlWait.Elapsed < TimeSpan.FromSeconds(2) && !dlFgCore.IsDefaultDownloadDialogOpen)
            await Task.Delay(50);
        var ctrlJOpened = dlFgCore.IsDefaultDownloadDialogOpen;
        if (ctrlJOpened) dlFgCore.CloseDefaultDownloadDialog();

        CloseTab(dlFg, recordClosed: false);
        CloseTab(dlBg, recordClosed: false);
        var downloads = new
        {
            bgCancelled = bgDownloadFired && bgDownloadCancelled,
            exeDenied,
            exeAllowed,
            ctrlJOpened,
            extCheckOk = IsDangerousDownload(@"C:\Downloads\a.exe")
                && IsDangerousDownload(@"C:\Downloads\A.PS1")
                && IsDangerousDownload(@"C:\Downloads\run.bat.")
                && IsDangerousDownload(@"C:\Downloads\app.jar")
                && !IsDangerousDownload(@"C:\Downloads\doc.pdf")
                && !IsDangerousDownload(@"C:\Downloads\note.txt"),
        };

        // #33: ProcessFailed resets navigation state, recovers renderer crash once, and TryCore guards COM/InvalidOperation exceptions.
        var crashTab = await Open(fixture + "/third");
        await WaitForLoad(crashTab);
        var crashCore = crashTab.View?.CoreWebView2 ?? throw new InvalidOperationException("Crash tab view is not ready.");
        crashTab.ActiveNavigationId = 99999;
        crashTab.PendingPageUri = new Uri("https://stale.example/");
        var crashSawExit = false;
        var crashReloaded = false;
        crashCore.ProcessFailed += (_, e) => {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                crashSawExit = true;
        };
        crashCore.NavigationCompleted += (_, e) => {
            if (crashSawExit && e.IsSuccess) crashReloaded = true;
        };
        try { await crashCore.CallDevToolsProtocolMethodAsync("Page.crash", "{}"); }
        catch { }
        var crashWait = Stopwatch.StartNew();
        while (crashWait.Elapsed < TimeSpan.FromSeconds(8) && !(crashSawExit && crashReloaded && crashTab.ActiveNavigationId is null && crashTab.PendingPageUri is null))
            await Task.Delay(50);
        string? crashTitleAfter = null;
        if (crashTab.View is { IsDisposed: false, CoreWebView2: { } liveCore })
        {
            try { crashTitleAfter = JsonSerializer.Deserialize<string>(await liveCore.ExecuteScriptAsync("document.title")); }
            catch { }
        }
        var dummyRecoveryTab = new BrowserTab { SavedUrl = fixture + "/first", SavedTitle = "Saved", ActiveNavigationId = 42, PendingPageUri = new Uri("https://stale.example/") };
        ResetTabForRecovery(dummyRecoveryTab);
        var processCrash = new
        {
            sawRenderExit = crashSawExit,
            navReset = crashTab.ActiveNavigationId is null && crashTab.PendingPageUri is null,
            recoveredAlive = crashReloaded && crashTitleAfter == "Third page marker" && !crashTab.CrashRecovered,
            tryCoreGuards = !TryCore(() => throw new COMException("boom"))
                && !TryCore(() => throw new InvalidOperationException("dead"))
                && TryCore<string>(() => throw new COMException("boom"), "ok") == "ok",
            recoveryPreservesUrl = dummyRecoveryTab.PendingUrl == fixture + "/first"
                && dummyRecoveryTab.SavedUrl == fixture + "/first"
                && dummyRecoveryTab.ActiveNavigationId is null
                && dummyRecoveryTab.PendingPageUri is null,
        };
        CloseTab(crashTab, recordClosed: false);
        SelectTab(first);

        // Regression smoke checks for #30 (focus + redirects), #39 (stale SavedUrl reload), #40 (close neighbor + scroll), #46 (middle-click drag-off).
        var focusTab = await Open(fixture + "/second");
        await WaitForLoad(focusTab);
        PressKey(Keys.Control | Keys.L);
        SelectTab(first);
        var switchFocusesWebView = !address.InputFocused && !address.Input.Visible
            && ReferenceEquals(ActiveControl, first.View)
            && (!ContainsFocus || first.View!.ContainsFocus);
        PressKey(Keys.Control | Keys.D2);
        var ctrl2FocusesWebView = selected == focusTab && !address.InputFocused && !address.Input.Visible
            && ReferenceEquals(ActiveControl, focusTab.View)
            && (!ContainsFocus || focusTab.View!.ContainsFocus);
        PressKey(Keys.Control | Keys.L);
        address.Input.Text = "http://typing.invalid";
        PressKey(Keys.Escape);
        var escReturnsToWebView = !address.InputFocused && !address.Input.Visible
            && address.Input.Text == focusTab.Url
            && ReferenceEquals(ActiveControl, focusTab.View)
            && (!ContainsFocus || focusTab.View!.ContainsFocus);
        PressKey(Keys.Control | Keys.L);
        await Go(fixture + "/redirect-tracked");
        var redirWatch = Stopwatch.StartNew();
        while (redirWatch.Elapsed < TimeSpan.FromSeconds(10)
            && (focusTab.Url != fixture + "/second" || focusTab.Navigating))
            await Task.Delay(50);
        var redirectUpdatesAddress = focusTab.Url == fixture + "/second"
            && address.Input.Text == fixture + "/second"
            && !address.InputFocused
            && ReferenceEquals(ActiveControl, focusTab.View);
        CloseTab(focusTab);

        // #39: Reload on a not-yet-loaded tab after Go() uses the new URL (both View==null and uncommitted View!=null).
        var reloadUnloaded = new BrowserTab(new SessionTab(fixture + "/first", "Stale title", false));
        opened.Add(reloadUnloaded);
        selected = reloadUnloaded;
        SyncTabs();
        var goUnloaded = Go(fixture + "/third");
        var unloadedSavedUpdated = reloadUnloaded.SavedUrl == fixture + "/third"
            && reloadUnloaded.SavedTitle is null
            && address.Input.Text == fixture + "/third";
        ReloadCurrent();
        await goUnloaded;
        await WaitForLoad(reloadUnloaded);
        var unloadedReloadUsesNewUrl = unloadedSavedUpdated
            && reloadUnloaded.Url == fixture + "/third"
            && reloadUnloaded.Title == "Third page marker"
            && !address.InputFocused
            && ReferenceEquals(ActiveControl, reloadUnloaded.View)
            && (!ContainsFocus || reloadUnloaded.View!.ContainsFocus);
        CloseTab(reloadUnloaded);

        var reloadUncommitted = new BrowserTab(new SessionTab(fixture + "/first", "Stale title", false));
        opened.Add(reloadUncommitted);
        await EnsureView(reloadUncommitted);
        reloadUncommitted.PendingUrl = null;
        SelectTab(reloadUncommitted);
        await Go(fixture + "/third");
        var uncommittedSavedUpdated = !reloadUncommitted.Loaded
            && reloadUncommitted.SavedUrl == fixture + "/third"
            && reloadUncommitted.SavedTitle is null;
        ReloadCurrent();
        await WaitForLoad(reloadUncommitted);
        var uncommittedReloadUsesNewUrl = uncommittedSavedUpdated
            && reloadUncommitted.Url == fixture + "/third"
            && reloadUncommitted.Title == "Third page marker";
        CloseTab(reloadUncommitted);

        // #40: Closing B in [C(pinned), A, B, D] (created as A, B, C, D) selects D, then closing D selects A; off-screen tabs scroll into view.
        var tabA = new BrowserTab { SavedTitle = "Tab A" };
        var tabB = new BrowserTab { SavedTitle = "Tab B" };
        var tabC = new BrowserTab { SavedTitle = "Tab C", Pinned = true };
        var tabD = new BrowserTab { SavedTitle = "Tab D" };
        opened.AddRange([tabA, tabB, tabC, tabD]);
        var displayBeforeClose = DisplayTabs().SkipWhile(t => t == first).SequenceEqual([tabC, tabA, tabB, tabD]);
        SelectTab(tabB);
        CloseTab(tabB);
        var closeBSelectsD = displayBeforeClose && selected == tabD;
        CloseTab(tabD);
        var closeDSelectsA = selected == tabA;
        tabC.Pinned = false;
        foreach (var t in new[] { tabA, tabC }) CloseTab(t);
        SelectTab(first);

        var scrollTabs = Enumerable.Range(1, 30).Select(i => new BrowserTab { SavedTitle = $"Scroll {i}" }).ToList();
        opened.AddRange(scrollTabs);
        SetOrientation(true);
        PerformLayout();
        SelectTab(scrollTabs[^1]);
        var sideLastCtrl = tabControls[scrollTabs[^1]];
        var sidebarScrollsSelected = sideTabs.ScrollOffset > 0
            && sideLastCtrl.Top - sideTabs.ScrollOffset >= 0
            && sideLastCtrl.Bottom - sideTabs.ScrollOffset <= sideTabs.ClientSize.Height;
        SetOrientation(false);
        PerformLayout();
        SelectTab(scrollTabs[0]);
        SelectTab(scrollTabs[^1]);
        var stripLastCtrl = tabControls[scrollTabs[^1]];
        var stripScrollsSelected = stripScroll > 0
            && stripLastCtrl.Left >= 0
            && stripLastCtrl.Right <= stripTabs.Width;
        foreach (var t in scrollTabs)
        {
            t.Closed = true;
            if (t.View is { } v) { t.View = null; CleanupView(v); }
            opened.Remove(t);
        }
        SetOrientation(true);
        SelectTab(first);
        PerformLayout();

        // #46: Middle-release outside the tab does not close it; middle-click inside does; left press on title released on close dot does not close.
        var midTab = new BrowserTab { SavedTitle = "Middle test" };
        opened.Add(midTab);
        SyncTabs();
        PerformLayout();
        var midCtrl = tabControls[midTab];
        var midIn = new Point(midCtrl.Width / 2, midCtrl.Height / 2);
        var midOut = new Point(-20, -20);
        SendMessage(midCtrl.Handle, WM_MBUTTONDOWN, (IntPtr)MK_MBUTTON, MakeLParam(midIn.X, midIn.Y));
        SendMessage(midCtrl.Handle, WM_MBUTTONUP, IntPtr.Zero, MakeLParam(midOut.X, midOut.Y));
        var middleReleaseOutsideKeepsTab = opened.Contains(midTab);
        SendMessage(midCtrl.Handle, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, MakeLParam(Math.Max(2, midCtrl.Width - S(30)), midCtrl.Height / 2));
        SendMessage(midCtrl.Handle, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(midCtrl.Width - S(10), midCtrl.Height / 2));
        var leftTitleToCloseDotKeepsTab = opened.Contains(midTab);
        SendMessage(midCtrl.Handle, WM_MBUTTONDOWN, (IntPtr)MK_MBUTTON, MakeLParam(midIn.X, midIn.Y));
        SendMessage(midCtrl.Handle, WM_MBUTTONUP, IntPtr.Zero, MakeLParam(midIn.X, midIn.Y));
        var middleClickInsideClosesTab = !opened.Contains(midTab);
        if (opened.Contains(midTab)) CloseTab(midTab);
        SelectTab(first);
        SyncTabs();
        PerformLayout();

        var tabFixes = new
        {
            switchFocusesWebView,
            ctrl2FocusesWebView,
            escReturnsToWebView,
            redirectUpdatesAddress,
            unloadedReloadUsesNewUrl,
            uncommittedReloadUsesNewUrl,
            closeBSelectsD,
            closeDSelectsA,
            sidebarScrollsSelected,
            stripScrollsSelected,
            middleReleaseOutsideKeepsTab,
            leftTitleToCloseDotKeepsTab,
            middleClickInsideClosesTab,
        };

        // #38: PerMonitorV2 awareness, scaled MinimumSize, minimum-width layout invariants at 100/150/200%, Share and Settings dialogs.
        static bool NoOverlap(Rectangle[] rects)
        {
            for (var i = 0; i < rects.Length; i++)
                for (var j = i + 1; j < rects.Length; j++)
                    if (rects[i].IntersectsWith(rects[j])) return false;
            return true;
        }

        var perMonitorV2 = AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var minSizeScaled = MinimumSize == new Size(S(640), S(420));

        var prevDpiSize = Size;
        var prevDpiUpdateVisible = updateBtn.Visible;
        var prevDpiVertical = vertical;
        var dpiTempTabs = Enumerable.Range(1, 20).Select(i => new BrowserTab { SavedTitle = $"DPI {i}" }).ToList();
        opened.AddRange(dpiTempTabs);
        updateBtn.Visible = true;
        shareBtn.Visible = true;

        Size = MinimumSize;
        SetOrientation(false, persistSettings: false);
        PerformLayout();
        var liveStripRects = new[]
        {
            BoundsInForm(backBtn), BoundsInForm(forwardBtn), BoundsInForm(reloadBtn),
            BoundsInForm(address), BoundsInForm(stripTabs), BoundsInForm(stripNewTabBtn),
            BoundsInForm(updateBtn), BoundsInForm(shareBtn), BoundsInForm(settingsBtn),
            minBtn.Bounds, maxBtn.Bounds, closeBtn.Bounds,
        };
        var liveHorizOk = address.Width >= S(160) && address.Height == S(28)
            && liveStripRects.All(r => r.Width > 0 && r.Height > 0 && ClientRectangle.Contains(r))
            && NoOverlap(liveStripRects);

        SetOrientation(true, persistSettings: false);
        PerformLayout();
        var liveSideBand = BoundsInForm(sideTopBand);
        var liveSideRects = new[]
        {
            BoundsInForm(backBtn), BoundsInForm(forwardBtn), BoundsInForm(reloadBtn),
            BoundsInForm(updateBtn), BoundsInForm(shareBtn), BoundsInForm(settingsBtn),
        };
        var liveVertOk = address.Width >= S(160) && address.Height == S(28)
            && liveSideRects.All(r => r.Width > 0 && r.Height > 0 && liveSideBand.Contains(r))
            && NoOverlap(liveSideRects);
        var liveMinLayoutOk = liveHorizOk && liveVertOk;

        var otherDpi = DeviceDpi == 192 ? 96 : 192;
        var simulatedDpiOk = Theme.FontForDpi(Theme.IconFont, otherDpi).FontFamily.Name == Theme.IconFont.FontFamily.Name
            && Theme.FontForDpi(Theme.UiFont, otherDpi).Height != Theme.FontForDpi(Theme.UiFont, DeviceDpi).Height;
        try
        {
            SetOrientation(false, persistSettings: false);
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                dpiOverride = dpi;
                ApplyChromeMetrics();
                var simW = S(624);
                var simH = S(52);
                LayoutCaptionButtons(simW);
                LayoutTopStrip(simW, simH);
                var simArea = new Rectangle(0, 0, simW, simH);
                var simRects = new[]
                {
                    backBtn.Bounds, forwardBtn.Bounds, reloadBtn.Bounds,
                    address.Bounds, stripTabs.Bounds, stripNewTabBtn.Bounds,
                    updateBtn.Bounds, shareBtn.Bounds, settingsBtn.Bounds,
                    minBtn.Bounds, maxBtn.Bounds, closeBtn.Bounds,
                };
                simulatedDpiOk &= MinimumSize == new Size(S(640), S(420))
                    && address.Width >= S(160) && address.Height == S(28)
                    && simRects.All(r => r.Width > 0 && r.Height > 0 && simArea.Contains(r))
                    && NoOverlap(simRects);
            }
            dpiOverride = null;
            ApplyChromeMetrics();
            SetOrientation(true, persistSettings: false);
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                dpiOverride = dpi;
                ApplyChromeMetrics();
                var sideInnerW = S(212);
                var bandH = S(36);
                LayoutSideTopBand(sideInnerW, bandH);
                var bandArea = new Rectangle(0, 0, sideInnerW, bandH);
                var bandRects = new[]
                {
                    backBtn.Bounds, forwardBtn.Bounds, reloadBtn.Bounds,
                    updateBtn.Bounds, shareBtn.Bounds, settingsBtn.Bounds,
                };
                var (pinCols, pinW, pinH, pinGap) = PinGridMetrics(4);
                simulatedDpiOk &= bandRects.All(r => r.Width > 0 && r.Height > 0 && bandArea.Contains(r))
                    && NoOverlap(bandRects)
                    && pinCols == 4 && pinH == S(34) && pinW >= S(24)
                    && pinCols * pinW + (pinCols - 1) * pinGap <= sideInnerW;
            }
        }
        finally
        {
            dpiOverride = null;
            updateBtn.Visible = prevDpiUpdateVisible;
            foreach (var t in dpiTempTabs) { t.Closed = true; opened.Remove(t); }
            ApplyChromeMetrics();
            Size = prevDpiSize;
            SetOrientation(prevDpiVertical, persistSettings: false);
            PerformLayout();
        }

        bool shareDialogOk;
        using (var liveShare = new ShareDialog("SearchWindows-0123456789abcdef0123456789abcdef", () => { }))
        {
            liveShare.Show(this);
            shareDialogOk = liveShare.VerifyLayoutInsideClient();
            liveShare.Close();
        }
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var simShare = new ShareDialog("SearchWindows-0123456789abcdef0123456789abcdef", () => { }, dpi);
            shareDialogOk &= simShare.VerifyLayoutInsideClient();
        }

        var settingsDialogDpiOk = true;
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var simSettings = new SettingsDialog(settings, noUpdateCheck, _ => { }, () => Task.CompletedTask, dpi);
            settingsDialogDpiOk &= simSettings.VerifyLayoutInsideClient();
        }

        var highDpi = new
        {
            perMonitorV2,
            minSizeScaled,
            liveMinLayoutOk,
            simulatedDpiOk,
            shareDialogOk,
            settingsDialogDpiOk,
        };

        // #41: Share capture times out on a page with a blocked script thread and re-enables Share afterwards.
        var hangTab = await Open(fixture + "/hang");
        await WaitForLoad(hangTab);
        await Task.Delay(250);
        string? hangError = null;
        confirmShareOverride = _ => true;
        shareErrorOverride = msg => hangError = msg;
        var hangWatch = Stopwatch.StartNew();
        var hangShareTask = SharePage();
        var disabledDuringCapture = !shareBtn.Enabled && busy;
        await hangShareTask;
        hangWatch.Stop();
        confirmShareOverride = null;
        shareErrorOverride = null;
        var timedOut = !string.IsNullOrEmpty(hangError) && hangError.Contains("Timed out", StringComparison.OrdinalIgnoreCase);
        var elapsedOk = hangWatch.Elapsed >= TimeSpan.FromSeconds(4.5) && hangWatch.Elapsed < TimeSpan.FromSeconds(12);
        var reEnabledAfter = shareBtn.Enabled && !busy;
        CloseTab(hangTab, recordClosed: false);
        SelectTab(first);
        var recoveredShare = await PageHandoff.Capture(first.View?.CoreWebView2 ?? throw new InvalidOperationException("First tab view is not ready."));
        var shareTimeout = new
        {
            disabledDuringCapture,
            timedOut,
            elapsedOk,
            reEnabledAfter,
            shareWorksAfterTimeout = recoveredShare.Text == "First page marker",
        };

        using var parsed = JsonDocument.Parse(received);
        File.WriteAllText(smokeReport!, JsonSerializer.Serialize(new {
            first = first.Title, second = secondTitle, restored, pinnedSurvivesClose = kept,
            verticalAndHorizontal = orientations, captionHidden, hitTests, maximizeToggles, chromeHiddenWorks,
            stripOverflowSafe, stripWheelScrolled, sideWheelStep, menuDeferredDispose, ncTrackedOnDown, captionNoTooltips,
            updaterSelfCheck, settingsRoundTrip, phosphorLoaded, iconFontFamily, captionFontFamily,
            settingsDialogOk, shareToggleWorks,
            adStatusWhenBlockingOff, adVisibleWhenBlockingOff, adHiddenAfterBlockingOn, adStatusAfterBlockingOn,
            urlWhenCleanOff, cliFlags,
            faviconCached, addressDisplayOk, tabDrag, tabFixes, highDpi, shareTimeout,
            postClean, blobUrl, permissions, downloads, processCrash,
            snapshot = parsed.RootElement, pipeOneUse = oneUse,
            rejectsLocalFiles = Resolve("file:///C:/Windows/win.ini") == null && !PageHandoff.Allowed("file:///C:/Windows/win.ini"),
            // Must match RESOLVE in test_smoke.py.
            resolve = new[] { "example.com", "example.com/a?b=c", "Example.COM:8443/x", "localhost", "localhost:3000", "localhost/app",
                "127.0.0.1:8080/x", "[::1]:5173", "https://example.com/path", "HTTP://Example.com", "3.14", "hello world",
                "how to type http:// links", "user@example.com", "site:github.com", "std::vector", "filetype:pdf", "define:x",
                "javascript:alert(1)", " JavaScript:alert(1) ", "data:text/html,hi",
                "file:///C:/Windows/win.ini", @"C:\Windows\win.ini", "mailto:a@example.com", "ftp://example.com", "http://",
                "about:blank", "edge://settings", "blob:https://example.com/123",
                "https://user:pw@example.com", "user:pw@example.com", "", "   " }.ToDictionary(s => s, Resolve),
            noTabNavigation,
            sessionLockName = Session.LockName(Path.Combine(Path.GetDirectoryName(sessionFile)!, "WebView")),
            runtimeVersion = environment!.BrowserVersionString,
            adsFinalUrl,
            adStatus,
            workerAdStatus,
            adHidden,
            okVisible,
            blockedCount,
            shield,
            redirectTracked,
            loopGuard,
            popup,
            // Must match CLEAN_LINK in test_smoke.py.
            cleanLink = new[] {
                "https://example.com/p?utm_source=x&utm_medium=y&id=1",
                "https://example.com/p?fbclid=abc",
                "https://shop.example/p?hmac=ab12&utm_source=x",
                "https://api.example/p?oauth_signature=s%3D&fbclid=y",
                "https://example.com/p?b=two%20words&gclid=1&a=1+2",
                "https://example.com/p?id=42&utm_source=smoke&fbclid=abc#keep",
                "https://example.com/p?utm_campaign=z#sec",
                "https://s3.example.com/obj?utm_source=x&X-Amz-Signature=deadbeef&id=1",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=secret123",
                "https://example.com/page?si=keepme&id=2",
                "https://example.com/plain",
                "https://example.com/search?q=hello&page=2#top"
            }.ToDictionary(u => u, CleanLink.Clean)
        }));
        var secondSaved = await Open(fixture + "/second?a");
        await WaitForLoad(secondSaved);
        var thirdSaved = await Open(fixture + "/third");
        await WaitForLoad(thirdSaved);
        await Open();
        thirdSaved.Pinned = true;
        SelectTab(secondSaved);
        SetOrientation(false);
        ExitCode = 0;
        Close();
    }
}

sealed class ShareDialog : Form
{
    readonly Label hint;
    readonly TextBox code;
    readonly Button cancel;
    readonly int? dpiOverride;
    int currentDpi;

    internal ShareDialog(string offerName, Action onClosed, int? dpiOverride = null)
    {
        this.dpiOverride = dpiOverride;
        currentDpi = dpiOverride ?? DeviceDpi;

        Text = "One-time Share code";
        AccessibleName = "One-time Share code";
        AutoScaleMode = AutoScaleMode.Dpi;
        ForeColor = Theme.Ink;
        BackColor = Theme.Ground;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        hint = new Label { Text = "Open up to 60s (once) to any program running as you.", AutoSize = true };
        code = new TextBox { Text = offerName, ReadOnly = true };
        cancel = new Button { Text = "Cancel share", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        cancel.Click += (_, _) => Close();
        CancelButton = cancel;
        FormClosed += (_, _) => onClosed();
        Controls.AddRange([hint, code, cancel]);

        LayoutForDpi(currentDpi);
    }

    internal void FocusCode()
    {
        code.Focus();
        code.SelectAll();
    }

    static int ScalePx(int px, int dpi) => (int)Math.Round(px * (dpi / 96.0), MidpointRounding.AwayFromZero);

    void LayoutForDpi(int dpi)
    {
        currentDpi = dpi;
        Font = Theme.FontForDpi(Theme.UiFont, dpi);
        var padX = ScalePx(14, dpi);
        var padY = ScalePx(12, dpi);
        var gapY = ScalePx(10, dpi);
        cancel.Padding = new(ScalePx(8, dpi), ScalePx(2, dpi), ScalePx(8, dpi), ScalePx(2, dpi));
        cancel.MinimumSize = new(ScalePx(100, dpi), 0);

        var hintSize = hint.GetPreferredSize(Size.Empty);
        var cancelSize = cancel.GetPreferredSize(Size.Empty);
        var contentW = Math.Max(Math.Max(ScalePx(396, dpi), hintSize.Width), cancelSize.Width);
        var codeH = code.PreferredHeight;

        hint.SetBounds(padX, padY, hintSize.Width, hintSize.Height);
        code.SetBounds(padX, hint.Bottom + gapY, contentW, codeH);
        cancel.SetBounds(padX + contentW - cancelSize.Width, code.Bottom + gapY, cancelSize.Width, cancelSize.Height);
        ClientSize = new(padX * 2 + contentW, cancel.Bottom + padY);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (dpiOverride is null && DeviceDpi != currentDpi)
            LayoutForDpi(DeviceDpi);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (dpiOverride is null)
            LayoutForDpi(e.DeviceDpiNew);
    }

    internal bool VerifyLayoutInsideClient()
    {
        CreateControl();
        PerformLayout();
        var client = ClientRectangle;
        return client.Width > 0 && client.Height > 0
            && hint.Width > 0 && hint.Height > 0 && client.Contains(hint.Bounds)
            && code.Width >= ScalePx(396, currentDpi) && code.Height > 0 && client.Contains(code.Bounds)
            && cancel.Width >= cancel.GetPreferredSize(Size.Empty).Width && cancel.Height > 0 && client.Contains(cancel.Bounds)
            && !hint.Bounds.IntersectsWith(code.Bounds)
            && !code.Bounds.IntersectsWith(cancel.Bounds)
            && !hint.Bounds.IntersectsWith(cancel.Bounds);
    }
}
