using System.Runtime.InteropServices;

namespace SearchPort;

sealed class ShieldFlyout : Form
{
    const int CS_DROPSHADOW = 0x00020000;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    readonly Control? anchorControl;
    readonly Func<Rectangle>? anchorBoundsProvider;
    readonly Action<bool> onToggleSite;
    readonly Action onOpenSettings;
    readonly Action<bool> onClosed;
    readonly string site;
    readonly bool globalEnabled;
    readonly Label iconLabel;
    readonly Label siteLabel;
    readonly CheckBox siteToggle;
    readonly Label countLabel;
    readonly Label globalHintLabel;
    readonly Panel divider;
    readonly LinkLabel settingsLink;
    bool closedByDeactivate;
    bool syncing;

    internal string SiteName => site;
    internal CheckBox ToggleCheckBox => siteToggle;
    internal string BlockedCountText => countLabel.Text;
    internal LinkLabel SettingsLink => settingsLink;

    internal ShieldFlyout(
        string site,
        bool globalEnabled,
        bool siteEnabled,
        int blockedCount,
        Action<bool> onToggleSite,
        Action onOpenSettings,
        Action<bool> onClosed,
        Control? anchorControl = null,
        Func<Rectangle>? anchorBoundsProvider = null)
    {
        this.site = site;
        this.globalEnabled = globalEnabled;
        this.onToggleSite = onToggleSite;
        this.onOpenSettings = onOpenSettings;
        this.onClosed = onClosed;
        this.anchorControl = anchorControl;
        this.anchorBoundsProvider = anchorBoundsProvider;

        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        KeyPreview = true;
        BackColor = Theme.Ground;
        ForeColor = Theme.Ink;
        AccessibleName = $"Shield for {site}";
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);

        iconLabel = new Label
        {
            Text = Theme.Glyphs.Shield,
            Font = Theme.FontForDpi(Theme.FluentSmallIconFont, DeviceDpi),
            ForeColor = globalEnabled && siteEnabled ? Theme.DropLine : Theme.Muted,
            BackColor = Theme.Ground,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        siteLabel = new Label
        {
            Text = EscapeMnemonics(site),
            Font = Theme.FontForDpi(Theme.SectionFont, DeviceDpi),
            ForeColor = Theme.Ink,
            BackColor = Theme.Ground,
            AutoEllipsis = true,
            AccessibleName = site,
        };
        siteToggle = new CheckBox
        {
            Text = EscapeMnemonics($"Blocking on for {site}"),
            Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi),
            ForeColor = Theme.Ink,
            BackColor = Theme.Ground,
            AutoSize = true,
            Checked = siteEnabled,
            Enabled = globalEnabled,
            TabStop = true,
            AccessibleName = $"Blocking for {site}",
        };
        siteToggle.CheckedChanged += (_, _) =>
        {
            if (syncing) return;
            var enabled = siteToggle.Checked;
            iconLabel.ForeColor = globalEnabled && enabled ? Theme.DropLine : Theme.Muted;
            this.onToggleSite(enabled);
        };

        countLabel = new Label
        {
            Text = FormatBlockedCount(blockedCount, globalEnabled && siteEnabled),
            Font = Theme.FontForDpi(Theme.HintFont, DeviceDpi),
            ForeColor = Theme.Muted,
            BackColor = Theme.Ground,
            AutoSize = true,
            AccessibleName = "Blocked count",
        };

        globalHintLabel = new Label
        {
            Text = "Ad and tracker blocking is off in Settings.",
            Font = Theme.FontForDpi(Theme.HintFont, DeviceDpi),
            ForeColor = Theme.Muted,
            BackColor = Theme.Ground,
            AutoSize = true,
            Visible = !globalEnabled,
        };

        divider = new Panel { Height = 1, BackColor = Theme.Hairline };

        settingsLink = new LinkLabel
        {
            Text = "Filter lists && settings...",
            Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi),
            LinkColor = Theme.DropLine,
            ActiveLinkColor = Theme.Ink,
            VisitedLinkColor = Theme.DropLine,
            BackColor = Theme.Ground,
            LinkBehavior = LinkBehavior.HoverUnderline,
            AutoSize = true,
            TabStop = true,
            AccessibleName = "Open filter lists and settings",
        };
        settingsLink.LinkClicked += (_, _) =>
        {
            Close();
            this.onOpenSettings();
        };

        Controls.AddRange([iconLabel, siteLabel, siteToggle, countLabel, globalHintLabel, divider, settingsLink]);

        Deactivate += (_, _) =>
        {
            if (IsDisposed || !Visible) return;
            closedByDeactivate = true;
            Close();
        };
        FormClosed += (_, _) => this.onClosed(closedByDeactivate);

        LayoutAndSize();
    }

    internal void UpdateState(bool siteEnabled, int blockedCount)
    {
        if (IsDisposed) return;
        syncing = true;
        try
        {
            siteToggle.Checked = siteEnabled;
            iconLabel.ForeColor = globalEnabled && siteEnabled ? Theme.DropLine : Theme.Muted;
            countLabel.Text = FormatBlockedCount(blockedCount, globalEnabled && siteEnabled);
        }
        finally { syncing = false; }
        LayoutAndSize();
    }

    static string EscapeMnemonics(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    static string FormatBlockedCount(int count, bool active)
    {
        if (!active) return "Blocking paused on this site";
        return count switch
        {
            0 => "No items blocked on this page",
            1 => "1 item blocked on this page",
            _ => $"{count} items blocked on this page",
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    int S(int px) => LogicalToDeviceUnits(px);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var pref = DWMWCP_ROUND;
        try { _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch { }
        LayoutAndSize();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        iconLabel.Font = Theme.FontForDpi(Theme.FluentSmallIconFont, DeviceDpi);
        siteLabel.Font = Theme.FontForDpi(Theme.SectionFont, DeviceDpi);
        siteToggle.Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        countLabel.Font = Theme.FontForDpi(Theme.HintFont, DeviceDpi);
        globalHintLabel.Font = Theme.FontForDpi(Theme.HintFont, DeviceDpi);
        settingsLink.Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        LayoutAndSize();
        Invalidate(true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Hairline, 1f);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    void LayoutAndSize()
    {
        var border = 1;
        var padX = S(14);
        var padY = S(12);
        var width = S(290);
        var innerW = width - border * 2 - padX * 2;

        iconLabel.Font = Theme.FontForDpi(Theme.FluentSmallIconFont, DeviceDpi);
        siteLabel.Font = Theme.FontForDpi(Theme.SectionFont, DeviceDpi);
        siteToggle.Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        countLabel.Font = Theme.FontForDpi(Theme.HintFont, DeviceDpi);
        globalHintLabel.Font = Theme.FontForDpi(Theme.HintFont, DeviceDpi);
        settingsLink.Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);

        var iconSz = S(18);
        var siteSize = siteLabel.GetPreferredSize(new Size(innerW - iconSz - S(6), 0));
        var headerH = Math.Max(iconSz, siteSize.Height);
        var y = border + padY;
        iconLabel.SetBounds(border + padX, y + (headerH - iconSz) / 2, iconSz, iconSz);
        siteLabel.SetBounds(iconLabel.Right + S(6), y + (headerH - siteSize.Height) / 2, Math.Max(S(40), innerW - iconSz - S(6)), siteSize.Height);
        y += headerH + S(10);

        siteToggle.MaximumSize = new Size(innerW, 0);
        var toggleSize = siteToggle.GetPreferredSize(new Size(innerW, 0));
        siteToggle.SetBounds(border + padX, y, Math.Min(innerW, toggleSize.Width), toggleSize.Height);
        y += toggleSize.Height + S(6);

        countLabel.MaximumSize = new Size(innerW, 0);
        var countSize = countLabel.GetPreferredSize(new Size(innerW, 0));
        countLabel.SetBounds(border + padX, y, Math.Min(innerW, countSize.Width), countSize.Height);
        y += countSize.Height;

        if (!globalEnabled)
        {
            y += S(4);
            globalHintLabel.MaximumSize = new Size(innerW, 0);
            var hintSize = globalHintLabel.GetPreferredSize(new Size(innerW, 0));
            globalHintLabel.SetBounds(border + padX, y, Math.Min(innerW, hintSize.Width), hintSize.Height);
            y += hintSize.Height;
        }

        y += S(10);
        divider.SetBounds(border, y, width - border * 2, 1);
        y += 1 + S(9);

        var linkSize = settingsLink.GetPreferredSize(Size.Empty);
        settingsLink.SetBounds(border + padX, y, Math.Min(innerW, linkSize.Width), linkSize.Height);
        y += linkSize.Height + padY + border;

        Size = new Size(width, y);
        RepositionToAnchor();
        Invalidate();
    }

    internal void RepositionToAnchor()
    {
        if (anchorControl is null || anchorControl.IsDisposed || !anchorControl.IsHandleCreated) return;
        var localRect = anchorBoundsProvider?.Invoke() ?? anchorControl.ClientRectangle;
        if (localRect.IsEmpty) localRect = anchorControl.ClientRectangle;
        var anchorRect = anchorControl.RectangleToScreen(localRect);
        var workArea = Screen.FromControl(anchorControl).WorkingArea;
        var margin = S(8);
        var x = anchorRect.Right - Width;
        if (x < workArea.Left + margin) x = anchorRect.Left;
        x = Math.Clamp(x, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - Width - margin));
        var y = anchorRect.Bottom + S(4);
        if (y + Height > workArea.Bottom - margin && anchorRect.Top - S(4) - Height >= workArea.Top + margin)
            y = anchorRect.Top - S(4) - Height;
        else
            y = Math.Clamp(y, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - Height - margin));
        Location = new Point(x, y);
    }

    internal bool VerifyLayoutInsideClient()
    {
        CreateControl();
        PerformLayout();
        var client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0) return false;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            if (c.Width <= 0 || c.Height <= 0 || !client.Contains(c.Bounds)) return false;
        }
        return true;
    }
}
