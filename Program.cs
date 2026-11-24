using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SearchPort;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var smoke = false;
        string? smokeDir = null;
        if (args is ["--smoke-test", ..])
        {
            if (args is not (["--smoke-test", _, _] or ["--smoke-test", _, _, "restore" or "perf" or "sleep"]))
                return 2;
            try { smokeDir = string.IsNullOrWhiteSpace(args[1]) ? null : Path.GetDirectoryName(Path.GetFullPath(args[1])); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return 2; }
            if (string.IsNullOrEmpty(smokeDir)) return 2;
            smoke = true;
        }
        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // experimental in .NET 10; gives native menus, scrollbars, text boxes and title bar the system theme
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        var profile = smoke ? Path.Combine(smokeDir!, "WebView") :
            args is ["--profile", var dir] ? Path.Combine(Path.GetFullPath(dir), "WebView") :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SearchWindows", "WebView");
        using var sessionLock = Session.TryAcquireLock(profile);
        var envTask = CreateEnvironment(profile);
        var sessionFile = Path.Combine(Path.GetDirectoryName(profile)!, "session.json");
        var sessionTask = !smoke || args is [.., "restore" or "perf"] ? Task.Run(() => Session.Load(sessionFile)) : null;
        using var browser = new BrowserWindow(sessionFile, envTask, sessionTask, sessionLock is not null, smoke ? args[1] : null, smoke ? args[2] : null, smoke && args.Length == 4 ? args[3] : null);
        Application.Run(browser);
        return browser.ExitCode;
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
    readonly IconButton layoutBtn;
    readonly IconButton shareBtn;
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
    readonly FaviconCache favicons;
    readonly Task<CoreWebView2Environment> envTask;
    readonly Task<SessionState?>? sessionTask;
    readonly bool canSaveSession;
    readonly string? smokeReport;
    readonly string? fixture;
    readonly string? smokePhase;
    CoreWebView2Environment? environment;
    BrowserTab? selected;
    CancellationTokenSource? pending;
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
    const int WM_MOUSEWHEEL = 0x020A;
    const int MK_LBUTTON = 0x0001;

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
        public bool Loaded { get; set; }
        public long? HiddenSince { get; set; }
        public ulong? ActiveNavigationId { get; set; }
        public Uri? PendingPageUri { get; set; }
        public bool Suspending { get; set; }
        public BrowserTab? Opener { get; set; }
        public bool IsPopup { get; init; }
        public bool Navigating => ActiveNavigationId is not null;
        public Func<int> BlockedCount { get; set; } = () => 0;
        public string? Url => View is { IsDisposed: false, CoreWebView2.Source: { } s } && PageHandoff.Allowed(s) ? s : SavedUrl;
        public string Title => Loaded && View is { IsDisposed: false, CoreWebView2.DocumentTitle: { } t } && !string.IsNullOrWhiteSpace(t) && t != "about:blank"
            ? t : (string.IsNullOrWhiteSpace(SavedTitle) ? "New tab" : SavedTitle);
    }

    public BrowserWindow(string sessionFile, Task<CoreWebView2Environment> envTask, Task<SessionState?>? sessionTask, bool canSaveSession, string? smokeReport, string? fixture, string? smokePhase = null)
    {
        this.sessionFile = sessionFile;
        favicons = new FaviconCache(sessionFile);
        this.envTask = envTask;
        this.sessionTask = sessionTask;
        this.canSaveSession = canSaveSession;
        this.smokeReport = smokeReport;
        this.fixture = fixture;
        this.smokePhase = smokePhase;
        Text = "browse";
        Font = Theme.UiFont;
        ForeColor = Theme.Ink;
        BackColor = Theme.Frame;
        MinimumSize = new(640, 420);
        Size = new(1160, 780);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        backBtn = MakeIcon(Theme.Glyphs.Back, "Back", GoBack);
        forwardBtn = MakeIcon(Theme.Glyphs.Forward, "Forward", GoForward);
        reloadBtn = MakeIcon(Theme.Glyphs.Reload, "Reload", ReloadCurrent);
        layoutBtn = MakeIcon(Theme.Glyphs.Layout, "Switch tab layout", () => SetOrientation(!vertical));
        shareBtn = MakeIcon(Theme.Glyphs.Share, "Share page text once with a local agent", () => _ = SharePage());
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

        SetOrientation(true);
        UpdateNavButtons();

        address.Input.KeyDown += (_, e) => {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _ = Go(address.Input.Text);
        };
        sleepTimer.Tick += (_, _) => _ = SleepIdleTabs(IdleSleepThreshold);
        sleepTimer.Start();
        dragScrollTimer.Tick += (_, _) => HandleDragAutoScroll();
        Activated += (_, _) => SetCaptionWindowActive(true);
        Deactivate += (_, _) => { CancelTabDrag(); ClearCaptionInteraction(); SetCaptionWindowActive(false); };
        Load += async (_, _) => await Start();
        FormClosing += (_, _) => SaveSession();
        FormClosed += (_, _) => { closed = true; CancelTabDrag(); pending?.Cancel(); sleepTimer.Stop(); sleepTimer.Dispose(); dragScrollTimer.Stop(); dragScrollTimer.Dispose(); };
    }

    int S(int px) => LogicalToDeviceUnits(px);

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
            sleepTimer.Dispose();
            dragScrollTimer.Dispose();
            if (!dropLine.IsDisposed) dropLine.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) { tips.Dispose(); favicons.Dispose(); }
    }

    void ApplyChromeMetrics()
    {
        sidebar.Width = S(232);
        sidebar.Padding = new(S(10), 0, S(10), S(10));
        sideTopBand.Height = S(36);
        sideAddressWrap.Height = S(28) + S(8);
        sideAddressWrap.Padding = new(0, 0, 0, S(8));
        sideNewTab.Height = S(28);
        topStrip.Height = S(52);
    }

    void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    void LayoutCaptionButtons()
    {
        if (closeBtn is null) return;
        var bw = S(46);
        var bh = vertical ? S(32) : topStrip.Height;
        var w = ClientSize.Width;
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
        base.OnDpiChanged(e);
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

    void LayoutSideTopBand()
    {
        if (!vertical) return;
        var btn = S(26);
        var gap = S(2);
        var y = (sideTopBand.ClientSize.Height - btn) / 2;
        var w = sideTopBand.ClientSize.Width;
        backBtn.SetBounds(0, y, btn, btn);
        forwardBtn.SetBounds(btn + gap, y, btn, btn);
        reloadBtn.SetBounds((btn + gap) * 2, y, btn, btn);
        layoutBtn.SetBounds(w - btn, y, btn, btn);
        shareBtn.SetBounds(w - btn * 2 - gap, y, btn, btn);
    }

    void LayoutTopStrip()
    {
        var w = topStrip.ClientSize.Width;
        var h = topStrip.ClientSize.Height;
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
        var rightW = btn * 2 + gap;
        var rightStart = w - captionW - S(8) - rightW;
        layoutBtn.SetBounds(rightStart, yBtn, btn, btn);
        shareBtn.SetBounds(rightStart + btn + gap, yBtn, btn, btn);

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
        if (vertical || item.Parent != stripTabs || stripContentWidth <= stripTabs.Width) return;
        if (item.Left < 0) ScrollStripBy(item.Left);
        else if (item.Right > stripTabs.Width) ScrollStripBy(item.Right - stripTabs.Width);
    }

    void HandleStripWheel(object? sender, MouseEventArgs e)
    {
        if (vertical || stripContentWidth <= stripTabs.Width) return;
        if (e is HandledMouseEventArgs h) h.Handled = true;
        ScrollStripBy(-e.Delta / 3);
    }

    void GoBack() { if (selected is { View: { IsDisposed: false, CanGoBack: true } view }) view.GoBack(); }
    void GoForward() { if (selected is { View: { IsDisposed: false, CanGoForward: true } view }) view.GoForward(); }
    void ReloadCurrent()
    {
        if (selected is not { } tab) return;
        if (tab.View is not { } view) { if (tab.Url is not null) _ = EnsureViewAndNavigate(tab); return; }
        if (!tab.Loaded && tab.SavedUrl is { } url) view.CoreWebView2?.Navigate(url); // first load never committed
        else if (tab.Url is not null && !view.IsDisposed) view.Reload();
    }

    void UpdateNavButtons()
    {
        backBtn.Enabled = selected is { View: { IsDisposed: false, CanGoBack: true } };
        forwardBtn.Enabled = selected is { View: { IsDisposed: false, CanGoForward: true } };
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
                if (selected?.Url is null) address.FocusInput(); else selected.View?.Focus(); // a focused address bar ignores redirects
                ExitCode = 0;
            }
        }
        catch (Exception ex)
        {
            if (smokeReport is not null) File.WriteAllText(smokeReport, JsonSerializer.Serialize(new { error = ex.ToString() }));
            else if (!closed) MessageBox.Show(this, ex.Message, "Could not start WebView2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (!closed && !IsDisposed && IsHandleCreated) BeginInvoke(Close);
        }
    }

    public static string? Resolve(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return null;
        if (PageHandoff.Allowed(text)) return new Uri(text).AbsoluteUri;
        if (!text.Any(char.IsWhiteSpace))
        {
            if (Uri.TryCreate("https://" + text, UriKind.Absolute, out var url) && url.UserInfo.Length == 0 && IsHost(url, text))
                return url.IsLoopback ? new Uri("http://" + text).AbsoluteUri : url.AbsoluteUri;
            // Any other explicit scheme (javascript:, file:, C:\, user:pw@…) is refused rather than searched; "name:123" stays a search.
            var colon = text.IndexOf(':');
            if (colon > 0 && Uri.CheckSchemeName(text[..colon]) && !(colon + 1 < text.Length && char.IsAsciiDigit(text[colon + 1]))) return null;
        }
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
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
            if (state is not null && state.Vertical != vertical) SetOrientation(state.Vertical);
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
                var blocked = await Shield.AttachAsync(
                    core,
                    environment!,
                    () => !view.IsDisposed && Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u : null,
                    () => tab.PendingPageUri);
                if (closed || tab.Closed || view.IsDisposed) { CleanupView(view); return; }
                tab.BlockedCount = blocked;
                var recentCleaned = new List<(string Url, long At)>(8);
                void TrackNavigation(CoreWebView2NavigationStartingEventArgs e)
                {
                    tab.ActiveNavigationId = e.NavigationId;
                    tab.PendingPageUri = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u : null;
                }
                core.NavigationStarting += (_, e) => {
                    if (!PageHandoff.Allowed(e.Uri))
                    {
                        if (!string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
                        else { tab.ActiveNavigationId = e.NavigationId; tab.PendingPageUri = null; }
                        return;
                    }
                    // Re-navigating a popup can sever window.opener mid-OAuth, so popups are never link-cleaned.
                    if (tab.IsPopup || e.NavigationKind is CoreWebView2NavigationKind.BackOrForward or CoreWebView2NavigationKind.Reload)
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
                            BeginInvoke(() => { if (!closed && !IsDisposed && !tab.Closed && !view.IsDisposed) view.CoreWebView2?.Navigate(cleaned); });
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
                };
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
                    if (PageHandoff.Allowed(core.Source)) { tab.Loaded = true; tab.SavedUrl = null; }
                    if (selected == tab && !address.InputFocused) UpdateAddress();
                    if (selected == tab) { UpdateNavButtons(); UpdateWindowTitle(); }
                    RefreshTab(tab);
                    SaveSession();
                };
                core.HistoryChanged += (_, _) => { if (selected == tab) UpdateNavButtons(); };
                core.DocumentTitleChanged += (_, _) => {
                    if (!opened.Contains(tab)) return;
                    if (tab.Loaded) tab.SavedTitle = null; // the restored title must not stick to later pages
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

    static bool IsPopupAllowed(string? uri) =>
        string.IsNullOrEmpty(uri)
        || string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase)
        || PageHandoff.Allowed(uri);

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
        var tab = new BrowserTab();
        await EnsureView(tab);
        if (closed || tab.Closed || tab.View is not { IsDisposed: false, CoreWebView2: { } core }) return tab;
        opened.Add(tab);
        SelectTab(tab);
        if (target is null) core.NavigateToString(Home);
        else core.Navigate(CleanLink.Clean(target));
        SaveSession();
        return tab;
    }

    // With no ready tab (startup, or the last tab just closed) open one instead of touching a missing CoreWebView2.
    Task Go(string text)
    {
        var target = Resolve(text);
        if (target is null) return Task.CompletedTask;
        target = CleanLink.Clean(target);
        if (selected is not { } tab) return OpenInteractive(target);
        if (tab.View is { IsDisposed: false, CoreWebView2: { } core })
        {
            core.Navigate(target);
            tab.View.Focus();
            return Task.CompletedTask;
        }
        tab.PendingUrl = target;
        tab.SavedUrl = target;
        UpdateAddress();
        return EnsureViewAndNavigate(tab);
    }

    void NewTab() => _ = OpenInteractive(null);
    async Task OpenInteractive(string? url)
    {
        if (environment is null || closed || restoring) return; // Ctrl+T skips the disabled UI during restore
        try { await Open(url); if (url is null) address.FocusInput(); }
        catch (Exception ex) { if (!closed) MessageBox.Show(this, ex.Message, "Could not open tab"); }
    }

    void UpdateAddress()
    {
        address.Input.Text = selected?.Url ?? "";
        address.Input.PlaceholderText = "Search or enter address";
    }

    void SelectTab(BrowserTab tab)
    {
        var prev = selected;
        if (prev is not null && prev != tab)
        {
            prev.HiddenSince = Environment.TickCount64;
            if (prev.View is { IsDisposed: false } prevView)
            {
                prevView.Visible = false;
                if (prevView.CoreWebView2 is { } prevCore)
                    prevCore.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            }
        }
        selected = tab;
        tab.HiddenSince = null;
        if (tab.View is { IsDisposed: false } view)
        {
            view.Visible = true;
            view.BringToFront();
            if (view.CoreWebView2 is { } core)
            {
                if (core.IsSuspended) core.Resume();
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            if (tab.PendingUrl is { } url)
            {
                tab.PendingUrl = null;
                view.CoreWebView2?.Navigate(url);
            }
        }
        else _ = EnsureViewAndNavigate(tab);
        UpdateAddress();
        UpdateNavButtons();
        UpdateWindowTitle();
        if (prev is not null && prev != tab) RefreshTab(prev);
        RefreshTab(tab);
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
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                return;
            }
            tab.HiddenSince = null;
            tab.View.Visible = true;
            tab.View.BringToFront();
            if (core.IsSuspended) core.Resume();
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            if (tab.PendingUrl is { } url)
            {
                tab.PendingUrl = null;
                core.Navigate(url);
            }
            UpdateNavButtons();
            if (!address.InputFocused) tab.View.Focus();
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
                : opened[Math.Min(index, opened.Count - 1)];
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

        if (tab.Pinned && tab.View is { IsDisposed: false, CoreWebView2.IsSuspended: true } v)
            v.CoreWebView2.Resume();

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
        if (tabControls.TryGetValue(tab, out var ctrl) && !ctrl.IsDisposed)
        {
            if (vertical && !tab.Pinned) sideTabs.EnsureVisible(ctrl);
            else if (!vertical) EnsureStripVisible(ctrl);
        }
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

    void SetOrientation(bool sidebarMode)
    {
        CancelTabDrag();
        vertical = sidebarMode;
        SuspendLayout();
        var surface = sidebarMode ? Theme.Frame : Theme.Ground;
        var hover = sidebarMode ? Theme.SideHover : Theme.StripHover;
        var activeFill = sidebarMode ? Theme.SideActive : Theme.StripActive;

        foreach (var b in new[] { backBtn, forwardBtn, reloadBtn, layoutBtn, shareBtn })
            b.SetSurface(surface, hover, activeFill);
        address.SetSurface(surface, hover);

        if (sidebarMode)
        {
            sideTopBand.Controls.AddRange([backBtn, forwardBtn, reloadBtn, shareBtn, layoutBtn]);
            sideAddressWrap.Controls.Add(address);
            ApplyChromeVisibility();
            LayoutSideTopBand();
            address.SetBounds(0, 0, sideAddressWrap.ClientSize.Width, S(28));
        }
        else
        {
            stripScroll = 0;
            topStrip.Controls.AddRange([backBtn, forwardBtn, reloadBtn, address, shareBtn, layoutBtn]);
            stripTabs.SendToBack();
            ApplyChromeVisibility();
        }
        ResumeLayout(true);
        RebuildTabs();
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
                sideTabs.SetItems(list, sideNewTab);
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
            selected?.View?.Focus();
            return true;
        }
        switch (keyData)
        {
            case Keys.Control | Keys.L: address.FocusInput(); return true;
            case Keys.Control | Keys.T: NewTab(); return true;
            case Keys.Control | Keys.W: CloseCurrent(); return true;
            case Keys.Control | Keys.Shift | Keys.T: ReopenClosedTab(); return true;
            case Keys.Control | Keys.Shift | Keys.S: SetOrientation(!vertical); return true;
            case Keys.Control | Keys.Shift | Keys.PageUp: MoveSelectedTab(-1); return true;
            case Keys.Control | Keys.Shift | Keys.PageDown: MoveSelectedTab(1); return true;
            case Keys.Alt | Keys.Left: GoBack(); return true;
            case Keys.Alt | Keys.Right: GoForward(); return true;
            case Keys.Control | Keys.R:
            case Keys.F5: ReloadCurrent(); return true;
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

    async Task SharePage()
    {
        if (selected is null || busy) return;
        var tab = selected;
        if (tab.View is not { IsDisposed: false, CoreWebView2: { } core } || !PageHandoff.Allowed(core.Source))
        {
            MessageBox.Show(this, "Open an HTTP or HTTPS page first.", "Share page");
            return;
        }
        var source = core.Source;
        if (MessageBox.Show(this, $"Offer a one-time text snapshot from {new Uri(source).IdnHost}?\n\nAny program running as you can read the snapshot while the offer is open (up to 60 seconds, once). Share only when you trust what's running. This is read-only and does not allow tab control.",
            "Share page with a local agent", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        busy = true;
        shareBtn.Enabled = false;
        try
        {
            if (core.IsSuspended) core.Resume();
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
                using var dialog = new Form { Text = "One-time Share code", ClientSize = new(425, 116),
                    StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false };
                var hint = new Label { Text = "Open up to 60s (once) to any program running as you.", AutoSize = true, Left = 14, Top = 11 };
                var code = new TextBox { Text = offer.Name, ReadOnly = true, Left = 14, Top = 35, Width = 395 };
                var cancel = new Button { Text = "Cancel share", Left = 309, Top = 69, Width = 100 };
                cancel.Click += (_, _) => dialog.Close();
                dialog.CancelButton = cancel;
                dialog.FormClosed += (_, _) => token.Cancel();
                dialog.Controls.AddRange([hint, code, cancel]);
                dialog.Show(this);
                code.Focus();
                code.SelectAll();
                shareBtn.SetActive(true);
                tips.SetToolTip(shareBtn, "Share code ready");
                await offer.Completion;
            }
            finally { token.Cancel(); if (ReferenceEquals(pending, token)) pending = null; }
        }
        catch (Exception ex) { if (!closed) MessageBox.Show(this, ex.Message, "Could not share text"); }
        finally
        {
            busy = false;
            if (!closed)
            {
                shareBtn.Enabled = true;
                shareBtn.SetActive(false);
                tips.SetToolTip(shareBtn, "Share page text once with a local agent");
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
        if (closed) return;
        var now = Environment.TickCount64;
        var minAgeMs = (long)Math.Max(0, threshold.TotalMilliseconds);
        foreach (var tab in opened.ToArray())
        {
            if (closed) return;
            if (tab == selected || tab.Closed || tab.Pinned || tab.Navigating || tab.Suspending) continue;
            if (tab.HiddenSince is not { } hiddenSince || now - hiddenSince < minAgeMs) continue;
            if (tab.View is not { IsDisposed: false, Visible: false, CoreWebView2: { } core }) continue;
            if (core.IsSuspended || core.IsDocumentPlayingAudio) continue;
            tab.Suspending = true;
            try
            {
                await core.TrySuspendAsync();
                if (!closed && !tab.Closed && (selected == tab || tab.Pinned) && tab.View is { IsDisposed: false, CoreWebView2.IsSuspended: true } v)
                    v.CoreWebView2.Resume();
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
        var stripEmptyHit = HitTestAt(topStrip.PointToScreen(new Point((stripNewTabBtn.Right + layoutBtn.Left) / 2, topStrip.Height / 2)));
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
        var layoutRect = BoundsInForm(layoutBtn);
        var shareRect = BoundsInForm(shareBtn);
        var minRect = minBtn.Bounds;
        var maxRect = maxBtn.Bounds;
        var closeRect = closeBtn.Bounds;
        var chromeButtons = new[] { plusRect, layoutRect, shareRect, minRect, maxRect, closeRect };
        var stripOverflowSafe = chromeButtons.All(r => r.Width > 0 && r.Height > 0 && winClient.Contains(r) && !stripRect.IntersectsWith(r))
            && !plusRect.IntersectsWith(layoutRect) && !plusRect.IntersectsWith(shareRect)
            && topStrip.Controls.GetChildIndex(layoutBtn) < topStrip.Controls.GetChildIndex(stripTabs)
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

        using var parsed = JsonDocument.Parse(received);
        File.WriteAllText(smokeReport!, JsonSerializer.Serialize(new {
            first = first.Title, second = secondTitle, restored, pinnedSurvivesClose = kept,
            verticalAndHorizontal = orientations, captionHidden, hitTests, maximizeToggles, chromeHiddenWorks,
            stripOverflowSafe, stripWheelScrolled, sideWheelStep, menuDeferredDispose, ncTrackedOnDown, captionNoTooltips,
            faviconCached, addressDisplayOk, tabDrag,
            snapshot = parsed.RootElement, pipeOneUse = oneUse,
            rejectsLocalFiles = Resolve("file:///C:/Windows/win.ini") == null && !PageHandoff.Allowed("file:///C:/Windows/win.ini"),
            // Must match RESOLVE in test_smoke.py.
            resolve = new[] { "example.com", "example.com/a?b=c", "Example.COM:8443/x", "localhost", "localhost:3000", "localhost/app",
                "127.0.0.1:8080/x", "[::1]:5173", "https://example.com/path", "HTTP://Example.com", "3.14", "hello world",
                "how to type http:// links", "user@example.com", "javascript:alert(1)", " JavaScript:alert(1) ", "data:text/html,hi",
                "file:///C:/Windows/win.ini", @"C:\Windows\win.ini", "mailto:a@example.com", "ftp://example.com", "http://",
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
