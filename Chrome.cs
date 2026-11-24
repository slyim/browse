using System.Drawing.Drawing2D;

namespace SearchPort;

static class Theme
{
    // shortcut: the theme is read once at startup; a Windows light/dark switch applies on the next launch.
#pragma warning disable WFO5001
    public static readonly bool Dark = Application.IsDarkModeEnabled;
#pragma warning restore WFO5001

    public static readonly Color Ground = Dark ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly Color Frame = Dark ? Color.FromArgb(0x13, 0x13, 0x13) : Color.FromArgb(0xF4, 0xF4, 0xF4);
    public static readonly Color Ink = Dark ? Color.FromArgb(0xED, 0xED, 0xED) : Color.FromArgb(0x17, 0x17, 0x17);
    public static readonly Color InkSoft = Dark ? Color.FromArgb(0xC2, 0xC2, 0xC2) : Color.FromArgb(0x4D, 0x4D, 0x4D);
    public static readonly Color Muted = Dark ? Color.FromArgb(0x94, 0x94, 0x94) : Color.FromArgb(0x8C, 0x8C, 0x8C);
    public static readonly Color Faint = Dark ? Color.FromArgb(0x52, 0x52, 0x52) : Color.FromArgb(0xD4, 0xD4, 0xD4);
    public static readonly Color Hairline = Dark ? Color.FromArgb(0x33, 0x33, 0x33) : Color.FromArgb(0xE8, 0xE8, 0xE8);

    public static readonly Color SideActive = Dark ? Color.FromArgb(0x28, 0x28, 0x28) : Color.FromArgb(0xE4, 0xE4, 0xE4);
    public static readonly Color SideHover = Dark ? Color.FromArgb(0x21, 0x21, 0x21) : Color.FromArgb(0xE9, 0xE9, 0xE9);

    public static readonly Color PinTile = Dark ? Color.FromArgb(0x1D, 0x1D, 0x1D) : Color.FromArgb(0xEC, 0xEC, 0xEC);
    public static readonly Color PinActive = Dark ? Color.FromArgb(0x30, 0x30, 0x30) : Color.FromArgb(0xDB, 0xDB, 0xDB);

    public static readonly Color StripActive = Dark ? Color.FromArgb(0x2D, 0x2D, 0x2D) : Color.FromArgb(0xEF, 0xEF, 0xEF);
    public static readonly Color StripHover = Dark ? Color.FromArgb(0x26, 0x26, 0x26) : Color.FromArgb(0xF6, 0xF6, 0xF6);

    public static readonly Color BadgeFill = Dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
    public static readonly Color CloseHoverFill = Dark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00);


    public static class Glyphs
    {
        public const string Back = "\uE76B";
        public const string Forward = "\uE76C";
        public const string Reload = "\uE72C";
        public const string Layout = "\uE89F";
        public const string NewTab = "\uE710";
        public const string Close = "\uE711";
        public const string Share = "\uE72D";
        public const string Update = "\uE896";
        public const string Minimize = "\uE921";
        public const string Maximize = "\uE922";
        public const string Restore = "\uE923";
        public const string ChromeClose = "\uE8BB";
    }

    static readonly string IconFamily = FontFamily.Families.Any(f => f.Name.Equals("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase))
        ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static readonly Font IconFont = new(IconFamily, 9.5f, FontStyle.Regular);
    public static readonly Font SmallIconFont = new(IconFamily, 8f, FontStyle.Regular);
    public static readonly Font CaptionIconFont = new(IconFamily, 7.5f, FontStyle.Regular);
    public static readonly Font CloseIconFont = new(IconFamily, 6f, FontStyle.Regular);
    public static readonly Font UiFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    public static readonly Font BadgeFont = new("Segoe UI Semibold", 7f, FontStyle.Regular);
    public static readonly Font PinFont = new("Segoe UI Semibold", 9.5f, FontStyle.Regular);
    public static readonly Font StripPinFont = new("Segoe UI Semibold", 9f, FontStyle.Regular);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
    {
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawRounded(Graphics g, Color color, RectangleF r, float radius, float width = 1f)
    {
        using var path = RoundedRect(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static string Monogram(string? url, string title)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.IdnHost;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            if (host.Length > 0 && char.IsLetter(host[0])) return char.ToUpperInvariant(host[0]).ToString();
        }
        var trimmed = title.Trim();
        return trimmed.Length > 0 ? char.ToUpperInvariant(trimmed[0]).ToString() : "•";
    }

    public static void DrawMonogram(Graphics g, Rectangle box, string letter, bool active)
    {
        FillRounded(g, BadgeFill, box, box.Width * 0.24f);
        TextRenderer.DrawText(g, letter, BadgeFont, box, active ? Ink : Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    public static void DrawCloseDot(Graphics g, Rectangle box, bool closeHovered)
    {
        using var brush = new SolidBrush(closeHovered ? CloseHoverFill : BadgeFill);
        g.FillEllipse(brush, box);
        TextRenderer.DrawText(g, Glyphs.Close, CloseIconFont, box, closeHovered ? Ink : Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

interface ITabItem
{
    bool Pinned { get; }
    void UpdateTab(string title, string? url, bool active);
}

abstract class ChromeControl : Control
{
    protected ChromeControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected int S(int px) => LogicalToDeviceUnits(px);
    protected float Sf(float px) => px * (DeviceDpi / 96f);
}

sealed class IconButton : ChromeControl
{
    readonly string glyph;
    readonly Action pressed;
    Color hoverColor;
    Color activeColor;
    bool hovered;
    bool active;

    internal IconButton(string glyph, string tip, Action pressed)
    {
        this.glyph = glyph;
        this.pressed = pressed;
        AccessibleName = tip;
        AccessibleRole = AccessibleRole.PushButton;
        BackColor = Theme.Frame;
        hoverColor = Theme.SideHover;
        activeColor = Theme.SideActive;
        Size = new(S(26), S(26));
        Cursor = Cursors.Hand;
    }

    internal void SetActive(bool value)
    {
        if (active != value) { active = value; Invalidate(); }
    }

    internal void SetSurface(Color surface, Color hover, Color activeFill)
    {
        BackColor = surface;
        hoverColor = hover;
        activeColor = activeFill;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        if (!Enabled) hovered = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Enabled) { hovered = true; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (Enabled && e.Button == MouseButtons.Left) pressed();
    }

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
        if (active || (hovered && Enabled))
            Theme.FillRounded(g, active ? activeColor : hoverColor, new RectangleF(0, 0, Width, Height), Sf(8f));
        var color = !Enabled ? Theme.Faint : active ? Theme.Ink : hovered ? Theme.InkSoft : Theme.Muted;
        TextRenderer.DrawText(g, glyph, Theme.IconFont, ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

// Panel that passes WM_NCHITTEST through to the Form so top resize edges and caption dragging work natively.
sealed class ChromePanel : Panel
{
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
        base.WndProc(ref m);
    }
}

sealed class CaptionButton : ChromeControl
{
    static readonly Color CloseHover = Color.FromArgb(0xC4, 0x2B, 0x1C);
    static readonly Color ClosePressed = Color.FromArgb(0xB2, 0x27, 0x1A);

    string glyph;
    readonly bool isClose;
    readonly Action pressedAction;
    Color hoverColor = Theme.SideHover;
    Color pressedColor = Theme.SideActive;
    bool hovered;
    bool pressed;
    bool windowActive = true;

    internal CaptionButton(string glyph, string name, bool isClose, Action pressedAction)
    {
        this.glyph = glyph;
        this.isClose = isClose;
        this.pressedAction = pressedAction;
        AccessibleName = name;
        AccessibleRole = AccessibleRole.PushButton;
        BackColor = Theme.Frame;
    }

    internal void SetGlyph(string value, string name)
    {
        if (glyph == value && AccessibleName == name) return;
        glyph = value;
        AccessibleName = name;
        Invalidate();
    }

    internal void SetSurface(Color surface, Color hover, Color activeFill)
    {
        BackColor = surface;
        hoverColor = hover;
        pressedColor = activeFill;
        Invalidate();
    }

    internal void SetInteraction(bool isHovered, bool isPressed)
    {
        if (hovered == isHovered && pressed == isPressed) return;
        hovered = isHovered;
        pressed = isPressed;
        Invalidate();
    }

    internal void SetWindowActive(bool active)
    {
        if (windowActive == active) return;
        windowActive = active;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { pressedAction(); e.Handled = true; }
    }

    protected override void WndProc(ref Message m)
    {
        // Let the Form return HTMINBUTTON/HTMAXBUTTON/HTCLOSE so Windows 11 Snap Layouts work on hover.
        if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var fill = pressed
            ? (isClose ? ClosePressed : pressedColor)
            : hovered
                ? (isClose ? CloseHover : hoverColor)
                : BackColor;
        g.Clear(fill);
        var fg = isClose && (hovered || pressed)
            ? Color.White
            : windowActive ? Theme.Ink : Theme.Muted;
        TextRenderer.DrawText(g, glyph, Theme.CaptionIconFont, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

sealed class AddressLine : ChromeControl
{
    readonly TextBox input;
    Color fillColor = Theme.SideHover;
    bool focused;

    internal AddressLine()
    {
        BackColor = Theme.Frame;
        Cursor = Cursors.IBeam;
        AccessibleName = "Address";
        input = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = Theme.UiFont,
            BackColor = fillColor,
            ForeColor = Theme.InkSoft,
            PlaceholderText = "Search or enter address",
            AccessibleName = "Address"
        };
        input.GotFocus += (_, _) => {
            focused = true;
            input.ForeColor = Theme.Ink;
            Invalidate();
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(() => { if (!IsDisposed && !input.IsDisposed) input.SelectAll(); });
        };
        input.LostFocus += (_, _) => { focused = false; input.ForeColor = Theme.InkSoft; Invalidate(); };
        Controls.Add(input);
        Height = S(28);
    }

    internal TextBox Input => input;
    internal bool InputFocused => input.Focused;

    internal void SetSurface(Color surface, Color fill)
    {
        BackColor = surface;
        fillColor = fill;
        input.BackColor = fill;
        Invalidate();
    }

    internal void FocusInput()
    {
        input.Focus();
        input.SelectAll();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var pad = S(10);
        var h = input.PreferredHeight;
        input.SetBounds(pad, Math.Max(0, (Height - h) / 2), Math.Max(0, Width - pad * 2), h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) input.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        Theme.FillRounded(g, fillColor, new RectangleF(0, 0, Width, Height), Sf(9f));
        if (focused)
            Theme.DrawRounded(g, Theme.Hairline, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Sf(8.5f));
    }
}

sealed class TabRow : ChromeControl, ITabItem
{
    string title;
    string? badge;
    bool active;
    readonly Action onSelect;
    readonly Action onClose;
    bool hovered;
    bool closeHovered;

    internal TabRow(string title, string? url, bool active, Action onSelect, Action onClose)
    {
        this.title = title;
        badge = url is null ? null : Theme.Monogram(url, title);
        this.active = active;
        this.onSelect = onSelect;
        this.onClose = onClose;
        BackColor = Theme.Frame;
        Height = S(28);
        Cursor = Cursors.Hand;
        AccessibleName = title;
        AccessibleRole = AccessibleRole.PageTab;
    }

    public bool Pinned => false;

    public void UpdateTab(string title, string? url, bool active)
    {
        var nextBadge = url is null ? null : Theme.Monogram(url, title);
        if (this.title == title && badge == nextBadge && this.active == active) return;
        this.title = title;
        badge = nextBadge;
        this.active = active;
        AccessibleName = title;
        Invalidate();
    }

    Rectangle CloseDotRect => new(Width - S(7) - S(15), (Height - S(15)) / 2, S(15), S(15));
    bool InCloseZone(int x) => hovered && x >= Width - S(26);

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; closeHovered = false; Invalidate(); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var over = InCloseZone(e.X);
        if (over != closeHovered) { closeHovered = over; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle) onClose();
        else if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            if (InCloseZone(e.X)) onClose();
            else onSelect();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (active || hovered)
            Theme.FillRounded(g, active ? Theme.SideActive : Theme.SideHover, new RectangleF(0, 0, Width, Height), Sf(9f));

        var x = S(10);
        if (badge is not null)
        {
            var mark = S(15);
            Theme.DrawMonogram(g, new Rectangle(x, (Height - mark) / 2, mark, mark), badge, active);
            x += mark + S(8);
        }

        var rightPad = hovered ? S(28) : S(10);
        var textRect = new Rectangle(x, 0, Math.Max(0, Width - x - rightPad), Height);
        var textColor = active ? Theme.Ink : hovered ? Theme.InkSoft : Theme.Muted;
        TextRenderer.DrawText(g, title, Theme.UiFont, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (hovered) Theme.DrawCloseDot(g, CloseDotRect, closeHovered);
    }
}

sealed class PinSquare : ChromeControl, ITabItem
{
    string letter;
    bool active;
    readonly bool onFrame;
    readonly Action onSelect;
    readonly Action onClose;
    bool hovered;

    internal PinSquare(string title, string? url, bool active, bool onFrame, Action onSelect, Action onClose)
    {
        letter = Theme.Monogram(url, title);
        this.active = active;
        this.onFrame = onFrame;
        this.onSelect = onSelect;
        this.onClose = onClose;
        BackColor = onFrame ? Theme.Frame : Theme.Ground;
        Cursor = Cursors.Hand;
        AccessibleName = title;
        AccessibleRole = AccessibleRole.PageTab;
    }

    public bool Pinned => true;
    internal bool OnFrame => onFrame;

    public void UpdateTab(string title, string? url, bool active)
    {
        var nextLetter = Theme.Monogram(url, title);
        if (AccessibleName == title && letter == nextLetter && this.active == active) return;
        letter = nextLetter;
        this.active = active;
        AccessibleName = title;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle) onClose();
        else if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) onSelect();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var restTile = !onFrame && Theme.Dark ? Color.FromArgb(0x24, 0x24, 0x24) : Theme.PinTile;
        var hoverTile = onFrame ? Theme.SideHover : Theme.StripHover;
        var fill = active ? Theme.PinActive : hovered ? hoverTile : restTile;
        Theme.FillRounded(g, fill, new RectangleF(0, 0, Width, Height), Sf(9f));
        var color = active ? Theme.Ink : hovered ? Theme.InkSoft : Theme.Muted;
        TextRenderer.DrawText(g, letter, onFrame ? Theme.PinFont : Theme.StripPinFont, ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

sealed class TabPill : ChromeControl, ITabItem
{
    string title;
    string? badge;
    string compactLetter;
    bool active;
    readonly Action onSelect;
    readonly Action onClose;
    bool hovered;
    bool closeHovered;

    internal TabPill(string title, string? url, bool active, Action onSelect, Action onClose)
    {
        this.title = title;
        badge = url is null ? null : Theme.Monogram(url, title);
        compactLetter = Theme.Monogram(url, title);
        this.active = active;
        this.onSelect = onSelect;
        this.onClose = onClose;
        BackColor = Theme.Ground;
        Height = S(28);
        Cursor = Cursors.Hand;
        AccessibleName = title;
        AccessibleRole = AccessibleRole.PageTab;
    }

    public bool Pinned => false;

    public void UpdateTab(string title, string? url, bool active)
    {
        var nextBadge = url is null ? null : Theme.Monogram(url, title);
        var nextCompact = Theme.Monogram(url, title);
        if (this.title == title && badge == nextBadge && compactLetter == nextCompact && this.active == active) return;
        this.title = title;
        badge = nextBadge;
        compactLetter = nextCompact;
        this.active = active;
        AccessibleName = title;
        Invalidate();
    }

    bool Compact => Width < S(78);
    Rectangle CloseDotRect => new(Width - S(7) - S(15), (Height - S(15)) / 2, S(15), S(15));
    bool InCloseZone(int x) => hovered && !Compact && x >= Width - S(26);

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; closeHovered = false; Invalidate(); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var over = InCloseZone(e.X);
        if (over != closeHovered) { closeHovered = over; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle) onClose();
        else if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            if (InCloseZone(e.X)) onClose();
            else onSelect();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (active || hovered)
            Theme.FillRounded(g, active ? Theme.StripActive : Theme.StripHover, new RectangleF(0, 0, Width, Height), Sf(9f));

        var mark = S(15);
        if (Compact)
        {
            Theme.DrawMonogram(g, new Rectangle((Width - mark) / 2, (Height - mark) / 2, mark, mark), compactLetter, active);
            return;
        }

        var x = S(10);
        if (badge is not null)
        {
            Theme.DrawMonogram(g, new Rectangle(x, (Height - mark) / 2, mark, mark), badge, active);
            x += mark + S(6);
        }

        var rightPad = hovered ? S(26) : S(8);
        var textRect = new Rectangle(x, 0, Math.Max(0, Width - x - rightPad), Height);
        var textColor = active ? Theme.Ink : hovered ? Theme.InkSoft : Theme.Muted;
        TextRenderer.DrawText(g, title, Theme.UiFont, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (hovered) Theme.DrawCloseDot(g, CloseDotRect, closeHovered);
    }
}

sealed class QuietRow : ChromeControl
{
    readonly Action pressed;
    bool hovered;

    internal QuietRow(string label, Action pressed)
    {
        Text = label;
        this.pressed = pressed;
        BackColor = Theme.Frame;
        Height = S(28);
        Cursor = Cursors.Hand;
        AccessibleName = label;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) pressed();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (hovered) Theme.FillRounded(g, Theme.SideHover, new RectangleF(0, 0, Width, Height), Sf(9f));
        var color = hovered ? Theme.InkSoft : Theme.Faint;
        var mark = S(15);
        var iconRect = new Rectangle(S(10), (Height - mark) / 2, mark, mark);
        TextRenderer.DrawText(g, Theme.Glyphs.NewTab, Theme.SmallIconFont, iconRect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var x = iconRect.Right + S(8);
        var textRect = new Rectangle(x, 0, Math.Max(0, Width - x - S(10)), Height);
        TextRenderer.DrawText(g, Text, Theme.UiFont, textRect, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

sealed class Hairline : ChromeControl
{
    internal Hairline()
    {
        BackColor = Theme.Ground;
        Size = new(1, S(20));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Hairline);
    }
}

// Scrollable vertical list without a visible scrollbar; wheel scrolling clamps within content bounds.
sealed class TabStack : Panel
{
    readonly Panel content = new() { BackColor = Theme.Frame };
    int scrollOffset;
    int contentHeight;

    internal TabStack()
    {
        BackColor = Theme.Frame;
        DoubleBuffered = true;
        Controls.Add(content);
    }

    internal ControlCollection Items => content.Controls;

    internal void SetItems(IReadOnlyList<Control> controls, Control footer)
    {
        content.SuspendLayout();
        var keep = new HashSet<Control>(controls) { footer };
        for (var i = content.Controls.Count - 1; i >= 0; i--)
        {
            if (keep.Contains(content.Controls[i])) continue;
            content.Controls.RemoveAt(i);
        }
        var gap = LogicalToDeviceUnits(2);
        var y = 0;
        var w = Math.Max(0, ClientSize.Width);
        for (var i = 0; i < controls.Count; i++)
        {
            var c = controls[i];
            if (c.Parent != content) content.Controls.Add(c);
            if (content.Controls.GetChildIndex(c) != i) content.Controls.SetChildIndex(c, i);
            c.SetBounds(0, y, w, c.Height);
            y += c.Height + gap;
        }
        if (footer.Parent != content) content.Controls.Add(footer);
        if (content.Controls.GetChildIndex(footer) != controls.Count) content.Controls.SetChildIndex(footer, controls.Count);
        footer.SetBounds(0, y, w, footer.Height);
        contentHeight = y + footer.Height;
        ClampScroll();
        content.SetBounds(0, -scrollOffset, w, Math.Max(contentHeight, ClientSize.Height));
        content.ResumeLayout();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (contentHeight <= ClientSize.Height) return;
        if (e is HandledMouseEventArgs h) h.Handled = true;
        scrollOffset -= e.Delta / 3;
        ClampScroll();
        content.Top = -scrollOffset;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var w = Math.Max(0, ClientSize.Width);
        foreach (Control c in content.Controls) c.Width = w;
        ClampScroll();
        content.SetBounds(0, -scrollOffset, w, Math.Max(contentHeight, ClientSize.Height));
    }

    void ClampScroll()
    {
        var max = Math.Max(0, contentHeight - ClientSize.Height);
        scrollOffset = Math.Clamp(scrollOffset, 0, max);
    }
}
