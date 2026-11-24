using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;

namespace SearchPort;

sealed record TabCrashInfo(string? Url, string Reason, int ExitCode, string? ProcessDescription)
{
    public static TabCrashInfo FromProcessFailed(
        string? url,
        CoreWebView2ProcessFailedReason reason,
        int exitCode,
        string? processDescription)
    {
        var label = reason switch
        {
            CoreWebView2ProcessFailedReason.Crashed => "Renderer process crashed",
            CoreWebView2ProcessFailedReason.OutOfMemory => "Page ran out of memory",
            CoreWebView2ProcessFailedReason.Terminated => "Renderer process was terminated",
            CoreWebView2ProcessFailedReason.Unresponsive => "Page stopped responding",
            CoreWebView2ProcessFailedReason.LaunchFailed => "Renderer process failed to start",
            CoreWebView2ProcessFailedReason.ProfileDeleted => "Browser profile was removed",
            CoreWebView2ProcessFailedReason.IntegrityFailure => "Code integrity check failed",
            CoreWebView2ProcessFailedReason.AbnormalExit => "Renderer process exited unexpectedly",
            CoreWebView2ProcessFailedReason.Unexpected => "Renderer process exited unexpectedly",
            _ => reason.ToString(),
        };
        return new TabCrashInfo(url, label, exitCode, string.IsNullOrWhiteSpace(processDescription) ? null : processDescription.Trim());
    }

    public string DetailText => ExitCode != 0
        ? $"{Reason} (exit code {ExitCode})"
        : Reason;
}

enum PageNoticeKind { None, Unresponsive, SubframeCrash }

sealed class PillButton : ChromeControl
{
    readonly string? glyph;
    readonly Action pressed;
    readonly bool primary;
    bool hovered;
    bool down;

    internal PillButton(string label, Action pressed, string? glyph = null, bool primary = false)
    {
        Text = label;
        this.glyph = glyph;
        this.pressed = pressed;
        this.primary = primary;
        AccessibleName = label;
        AccessibleRole = AccessibleRole.PushButton;
        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = Theme.Ground;
    }

    internal void PerformClick()
    {
        if (Enabled) pressed();
    }

    internal Size MeasurePreferred()
    {
        var font = Theme.FontForDpi(Theme.SectionFont, DeviceDpi);
        var textSize = TextRenderer.MeasureText(Text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var iconW = glyph is null ? 0 : S(14) + S(6);
        var padX = S(14);
        var h = S(30);
        return new Size(Math.Max(S(64), padX * 2 + iconW + textSize.Width), h);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var wasDown = down;
        down = false;
        Invalidate();
        if (wasDown && Enabled && ClientRectangle.Contains(e.Location)) pressed();
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && e.KeyCode is Keys.Enter or Keys.Space) { pressed(); e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var fill = primary
            ? (down ? Theme.PinActive : hovered ? Theme.SideActive : Theme.PinTile)
            : (down ? Theme.SideActive : hovered ? Theme.SideHover : BackColor);
        var rect = new RectangleF(0.5f, 0.5f, Math.Max(0, Width - 1f), Math.Max(0, Height - 1f));
        Theme.FillRounded(g, fill, rect, Sf(8f));
        var borderColor = Focused ? Theme.DropLine : Theme.Hairline;
        Theme.DrawRounded(g, borderColor, rect, Sf(8f), Focused ? Sf(1.5f) : 1f);

        var fg = !Enabled ? Theme.Faint : primary || hovered ? Theme.Ink : Theme.InkSoft;
        var font = Theme.FontForDpi(Theme.SectionFont, DeviceDpi);
        var textSize = TextRenderer.MeasureText(Text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var iconSize = glyph is null ? 0 : S(14);
        var gap = glyph is null ? 0 : S(6);
        var contentW = iconSize + gap + textSize.Width;
        var startX = Math.Max(S(8), (Width - contentW) / 2);

        if (glyph is not null)
        {
            var iconRect = new Rectangle(startX, (Height - iconSize) / 2, iconSize, iconSize);
            TextRenderer.DrawText(g, glyph, Theme.FontForDpi(Theme.SmallIconFont, DeviceDpi), iconRect, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            startX += iconSize + gap;
        }
        var textRect = new Rectangle(startX, 0, Math.Max(0, Width - startX - S(8)), Height);
        TextRenderer.DrawText(g, Text, font, textRect, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

sealed class CrashPagePanel : Panel
{
    static readonly Font HeadingFont = new("Segoe UI Semibold", 13.5f, FontStyle.Regular);
    static readonly Font BadgeIconFont = new(Theme.IconFont.FontFamily, 18f, FontStyle.Regular);

    readonly PillButton reloadBtn;
    TabCrashInfo? current;

    internal CrashPagePanel(Action onReload)
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Ground;
        ForeColor = Theme.Ink;
        DoubleBuffered = true;
        Visible = false;
        AccessibleName = "Page crashed";

        reloadBtn = new PillButton("Reload", onReload, glyph: Theme.Glyphs.Reload, primary: true)
        {
            BackColor = Theme.Ground,
        };
        Controls.Add(reloadBtn);
    }

    internal TabCrashInfo? CurrentCrash => current;
    internal string DisplayedHeading => "This page crashed";
    internal string DisplayedUrl => current?.Url ?? "";
    internal string DisplayedDetail => current?.DetailText ?? "";
    internal PillButton ReloadButton => reloadBtn;

    int S(int px) => LogicalToDeviceUnits(px);
    float Sf(float px) => px * (DeviceDpi / 96f);

    internal void ShowCrash(TabCrashInfo info)
    {
        current = info;
        LayoutContent();
        Invalidate();
    }

    internal void ClearCrash()
    {
        current = null;
        Visible = false;
    }

    void LayoutContent()
    {
        var btnSize = reloadBtn.MeasurePreferred();
        var (_, _, _, _, btnTop) = ComputeMetrics(btnSize);
        reloadBtn.SetBounds((ClientSize.Width - btnSize.Width) / 2, btnTop, btnSize.Width, btnSize.Height);
    }

    (Rectangle Badge, Rectangle Heading, Rectangle Url, Rectangle Detail, int ButtonTop) ComputeMetrics(Size btnSize)
    {
        var maxTextW = Math.Clamp(ClientSize.Width - S(48), S(160), S(520));
        var badgeSize = S(44);
        var headingFont = Theme.FontForDpi(HeadingFont, DeviceDpi);
        var urlFont = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        var detailFont = Theme.FontForDpi(Theme.HintFont, DeviceDpi);

        var headingH = TextRenderer.MeasureText(DisplayedHeading, headingFont, new Size(maxTextW, 0),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        var hasUrl = !string.IsNullOrWhiteSpace(DisplayedUrl);
        var urlH = hasUrl
            ? Math.Min(urlFont.Height * 2 + S(4), TextRenderer.MeasureText(DisplayedUrl, urlFont, new Size(maxTextW, 0),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height)
            : 0;
        var hasDetail = !string.IsNullOrWhiteSpace(DisplayedDetail);
        var detailH = hasDetail
            ? TextRenderer.MeasureText(DisplayedDetail, detailFont, new Size(maxTextW, 0),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height
            : 0;

        var gapBadge = S(14);
        var gapUrl = hasUrl ? S(8) : 0;
        var gapDetail = hasDetail ? S(6) : 0;
        var gapBtn = S(18);
        var totalH = badgeSize + gapBadge + headingH + gapUrl + urlH + gapDetail + detailH + gapBtn + btnSize.Height;

        var y = Math.Max(S(24), (ClientSize.Height - totalH) / 2);
        var x = (ClientSize.Width - maxTextW) / 2;

        var badgeRect = new Rectangle((ClientSize.Width - badgeSize) / 2, y, badgeSize, badgeSize);
        y += badgeSize + gapBadge;
        var headingRect = new Rectangle(x, y, maxTextW, headingH);
        y += headingH + gapUrl;
        var urlRect = new Rectangle(x, y, maxTextW, urlH);
        y += urlH + gapDetail;
        var detailRect = new Rectangle(x, y, maxTextW, detailH);
        y += detailH + gapBtn;
        return (badgeRect, headingRect, urlRect, detailRect, y);
    }

    internal bool VerifyLayoutInsideClient()
    {
        LayoutContent();
        return ClientSize.Width > 0 && ClientSize.Height > 0
            && reloadBtn.Width > 0 && reloadBtn.Height > 0
            && ClientRectangle.Contains(reloadBtn.Bounds);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutContent();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutContent();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutContent();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var (badge, heading, urlRect, detailRect, _) = ComputeMetrics(reloadBtn.Size);
        Theme.FillRounded(g, Theme.SideHover, new RectangleF(badge.X, badge.Y, badge.Width, badge.Height), Sf(12f));
        Theme.DrawRounded(g, Theme.Hairline, new RectangleF(badge.X + 0.5f, badge.Y + 0.5f, badge.Width - 1f, badge.Height - 1f), Sf(12f));
        TextRenderer.DrawText(g, Theme.Glyphs.Warning, Theme.FontForDpi(BadgeIconFont, DeviceDpi), badge, Theme.InkSoft,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        TextRenderer.DrawText(g, DisplayedHeading, Theme.FontForDpi(HeadingFont, DeviceDpi), heading, Theme.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        if (urlRect.Height > 0)
            TextRenderer.DrawText(g, DisplayedUrl, Theme.FontForDpi(Theme.UiFont, DeviceDpi), urlRect, Theme.InkSoft,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (detailRect.Height > 0)
            TextRenderer.DrawText(g, DisplayedDetail, Theme.FontForDpi(Theme.HintFont, DeviceDpi), detailRect, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

sealed class PageNoticeBanner : Panel
{
    readonly PillButton waitBtn;
    readonly PillButton reloadBtn;
    readonly IconButton dismissBtn;
    string message = "";

    internal PageNoticeBanner(Action onWait, Action onReload, Action onDismiss)
    {
        Dock = DockStyle.Top;
        BackColor = Theme.Frame;
        ForeColor = Theme.Ink;
        DoubleBuffered = true;
        Visible = false;
        AccessibleName = "Page status notice";

        waitBtn = new PillButton("Wait", onWait) { BackColor = Theme.Frame, Visible = false };
        reloadBtn = new PillButton("Reload", onReload, glyph: Theme.Glyphs.Reload, primary: true) { BackColor = Theme.Frame, Visible = false };
        dismissBtn = new IconButton(Theme.Glyphs.Close, "Dismiss", onDismiss) { Visible = false };
        dismissBtn.SetSurface(Theme.Frame, Theme.SideHover, Theme.SideActive);

        Controls.Add(waitBtn);
        Controls.Add(reloadBtn);
        Controls.Add(dismissBtn);
        Height = S(40);
    }

    internal PageNoticeKind Kind { get; private set; } = PageNoticeKind.None;
    internal string Message => message;
    internal PillButton WaitButton => waitBtn;
    internal PillButton ReloadButton => reloadBtn;
    internal IconButton DismissButton => dismissBtn;

    int S(int px) => LogicalToDeviceUnits(px);

    internal void ShowUnresponsive()
    {
        Kind = PageNoticeKind.Unresponsive;
        message = "Page isn't responding";
        AccessibleName = message;
        waitBtn.Visible = true;
        reloadBtn.Visible = true;
        dismissBtn.Visible = false;
        Height = S(40);
        LayoutControls();
        Visible = true;
        Invalidate();
    }

    internal void ShowSubframeCrash(string notice)
    {
        Kind = PageNoticeKind.SubframeCrash;
        message = notice;
        AccessibleName = notice;
        waitBtn.Visible = false;
        reloadBtn.Visible = false;
        dismissBtn.Visible = true;
        Height = S(34);
        LayoutControls();
        Visible = true;
        Invalidate();
    }

    internal void HideBanner()
    {
        Kind = PageNoticeKind.None;
        message = "";
        waitBtn.Visible = false;
        reloadBtn.Visible = false;
        dismissBtn.Visible = false;
        Visible = false;
    }

    void LayoutControls()
    {
        var padX = S(12);
        var gap = S(6);
        var btnH = S(26);
        var y = Math.Max(0, (ClientSize.Height - 1 - btnH) / 2);

        if (Kind == PageNoticeKind.Unresponsive)
        {
            var rSize = reloadBtn.MeasurePreferred();
            var wSize = waitBtn.MeasurePreferred();
            var rx = ClientSize.Width - padX - rSize.Width;
            reloadBtn.SetBounds(rx, y, rSize.Width, btnH);
            waitBtn.SetBounds(rx - gap - wSize.Width, y, wSize.Width, btnH);
        }
        else if (Kind == PageNoticeKind.SubframeCrash)
        {
            var dSize = S(24);
            dismissBtn.SetBounds(ClientSize.Width - padX - dSize, Math.Max(0, (ClientSize.Height - 1 - dSize) / 2), dSize, dSize);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutControls();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutControls();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = Kind == PageNoticeKind.SubframeCrash ? S(34) : S(40);
        LayoutControls();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var padX = S(12);
        var iconSize = S(16);
        var contentH = Math.Max(0, ClientSize.Height - 1);
        var iconRect = new Rectangle(padX, (contentH - iconSize) / 2, iconSize, iconSize);
        TextRenderer.DrawText(g, Theme.Glyphs.Warning, Theme.FontForDpi(Theme.SmallIconFont, DeviceDpi), iconRect, Theme.InkSoft,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        var rightEdge = Kind switch
        {
            PageNoticeKind.Unresponsive => waitBtn.Left - S(10),
            PageNoticeKind.SubframeCrash => dismissBtn.Left - S(10),
            _ => ClientSize.Width - padX,
        };
        var textX = iconRect.Right + S(8);
        var textRect = new Rectangle(textX, 0, Math.Max(0, rightEdge - textX), contentH);
        var color = Kind == PageNoticeKind.SubframeCrash ? Theme.Muted : Theme.Ink;
        TextRenderer.DrawText(g, message, Theme.FontForDpi(Theme.UiFont, DeviceDpi), textRect, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        using var pen = new Pen(Theme.Hairline);
        g.DrawLine(pen, 0, ClientSize.Height - 1, ClientSize.Width, ClientSize.Height - 1);
    }
}
