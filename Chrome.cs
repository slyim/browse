using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;

namespace SearchPort;

static class Theme
{
    [DllImport("gdi32.dll")]
    static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

    [DllImport("gdi32.dll")]
    static extern bool RemoveFontMemResourceEx(IntPtr fh);

    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    static readonly int SystemDpi = GetDpiForSystem() is > 0 and var d ? (int)d : 96;
    static readonly ConcurrentDictionary<(Font, int), Font> ScaledFonts = new();

    // shortcut: the theme is read once at startup; changing Theme in Settings or Windows applies on the next launch.
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
    public static readonly Color DropLine = Dark ? Color.FromArgb(0x6B, 0xA4, 0xFF) : Color.FromArgb(0x25, 0x63, 0xEB);
    public static readonly Color DropSlotFill = Dark ? Color.FromArgb(0x28, 0x6B, 0xA4, 0xFF) : Color.FromArgb(0x1C, 0x25, 0x63, 0xEB);

    static readonly string CaptionIconFamily = FontFamily.Families.Any(f => f.Name.Equals("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase))
        ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    static readonly (PrivateFontCollection Collection, IntPtr Memory, IntPtr GdiHandle, FontFamily Family)? Phosphor = TryLoadPhosphor();
    public static bool PhosphorLoaded => Phosphor is not null;

    static (PrivateFontCollection, IntPtr, IntPtr, FontFamily)? TryLoadPhosphor()
    {
        IntPtr mem = IntPtr.Zero;
        IntPtr gdi = IntPtr.Zero;
        PrivateFontCollection? pfc = null;
        try
        {
            using var stream = typeof(Theme).Assembly.GetManifestResourceStream("Phosphor.ttf");
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return null;

            // Keep memory + GDI registration alive for the process lifetime so GDI TextRenderer resolves "Phosphor".
            mem = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            gdi = AddFontMemResourceEx(mem, (uint)bytes.Length, IntPtr.Zero, out var count);
            if (gdi == IntPtr.Zero || count == 0)
            {
                Marshal.FreeCoTaskMem(mem);
                return null;
            }
            pfc = new PrivateFontCollection();
            pfc.AddMemoryFont(mem, bytes.Length);
            if (pfc.Families.Length == 0)
            {
                pfc.Dispose();
                RemoveFontMemResourceEx(gdi);
                Marshal.FreeCoTaskMem(mem);
                return null;
            }
            return (pfc, mem, gdi, pfc.Families[0]);
        }
        catch
        {
            pfc?.Dispose();
            if (gdi != IntPtr.Zero) RemoveFontMemResourceEx(gdi);
            if (mem != IntPtr.Zero) Marshal.FreeCoTaskMem(mem);
            return null;
        }
    }

    public static class Glyphs
    {
        // Phosphor Regular (@phosphor-icons/web 2.1.2) with Segoe Fluent Icons fallback.
        public static readonly string Back = PhosphorLoaded ? "\uE058" : "\uE76B";       // arrow-left
        public static readonly string Forward = PhosphorLoaded ? "\uE06C" : "\uE76C";    // arrow-right
        public static readonly string Reload = PhosphorLoaded ? "\uE036" : "\uE72C";     // arrow-clockwise
        public static readonly string Stop = PhosphorLoaded ? "\uE4F6" : "\uE711";       // x
        public static readonly string NewTab = PhosphorLoaded ? "\uE3D4" : "\uE710";     // plus
        public static readonly string Close = PhosphorLoaded ? "\uE4F6" : "\uE711";      // x
        public static readonly string Audio = PhosphorLoaded ? "\uE44A" : "\uE767";      // speaker-high
        public static readonly string Muted = PhosphorLoaded ? "\uE45A" : "\uE74F";      // speaker-slash
        public static readonly string Share = PhosphorLoaded ? "\uE406" : "\uE72D";      // share
        public static readonly string Update = PhosphorLoaded ? "\uE20C" : "\uE896";     // download-simple
        public static readonly string Settings = PhosphorLoaded ? "\uE272" : "\uE713";   // gear-six
        public static readonly string ChevronUp = PhosphorLoaded ? "\uE13C" : "\uE70E";  // caret-up
        public static readonly string ChevronDown = PhosphorLoaded ? "\uE136" : "\uE70D"; // caret-down

        // Window caption buttons stay on Segoe Fluent Icons to match native Windows chrome.
        public const string Minimize = "\uE921";
        public const string Maximize = "\uE922";
        public const string Restore = "\uE923";
        public const string ChromeClose = "\uE8BB";
    }

    public static readonly Font IconFont = Phosphor is { } p
        ? new(p.Family, 11.5f, FontStyle.Regular)
        : new(CaptionIconFamily, 9.5f, FontStyle.Regular);
    public static readonly Font SmallIconFont = Phosphor is { } p2
        ? new(p2.Family, 9.5f, FontStyle.Regular)
        : new(CaptionIconFamily, 8f, FontStyle.Regular);
    public static readonly Font CaptionIconFont = new(CaptionIconFamily, 7.5f, FontStyle.Regular);
    public static readonly Font CloseIconFont = Phosphor is { } p3
        ? new(p3.Family, 7.5f, FontStyle.Regular)
        : new(CaptionIconFamily, 6f, FontStyle.Regular);
    public static readonly Font UiFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    public static readonly Font SectionFont = new("Segoe UI Semibold", 9f, FontStyle.Regular);
    public static readonly Font HintFont = new("Segoe UI", 8.25f, FontStyle.Regular);
    public static readonly Font BadgeFont = new("Segoe UI Semibold", 7f, FontStyle.Regular);
    public static readonly Font PinFont = new("Segoe UI Semibold", 9.5f, FontStyle.Regular);
    public static readonly Font StripPinFont = new("Segoe UI Semibold", 9f, FontStyle.Regular);

    public static Font FontForDpi(Font baseFont, int dpi)
    {
        if (dpi <= 0 || dpi == SystemDpi) return baseFont;
        return ScaledFonts.GetOrAdd((baseFont, dpi), static key =>
            new Font(key.Item1.FontFamily, key.Item1.SizeInPoints * key.Item2 / 72f, key.Item1.Style, GraphicsUnit.Pixel));
    }

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

    public static void DrawMonogram(Graphics g, Rectangle box, string letter, bool active, int dpi = 96)
    {
        FillRounded(g, BadgeFill, box, box.Width * 0.24f);
        TextRenderer.DrawText(g, letter, FontForDpi(BadgeFont, dpi), box, active ? Ink : Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    public static void DrawFavicon(Graphics g, Rectangle box, Image icon)
    {
        if (box.Width <= 0 || box.Height <= 0 || icon.Width <= 0 || icon.Height <= 0) return;
        var scale = Math.Min((float)box.Width / icon.Width, (float)box.Height / icon.Height);
        var w = Math.Max(1, (int)Math.Round(icon.Width * scale));
        var h = Math.Max(1, (int)Math.Round(icon.Height * scale));
        var dest = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        var prev = g.InterpolationMode;
        g.InterpolationMode = w == icon.Width && h == icon.Height
            ? InterpolationMode.NearestNeighbor
            : InterpolationMode.HighQualityBicubic;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(icon, dest, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attrs);
        g.InterpolationMode = prev;
    }

    public static void DrawCloseDot(Graphics g, Rectangle box, bool closeHovered, int dpi = 96)
        => DrawCloseDot(g, box, closeHovered ? 1f : 0f, 1f, Frame, dpi);

    public static void DrawCloseDot(Graphics g, Rectangle box, float closeHoverAmount, float opacity, Color surface, int dpi = 96)
    {
        if (opacity <= 0.01f) return;
        var fill = Motion.WithAlpha(Motion.Blend(BadgeFill, CloseHoverFill, closeHoverAmount), opacity);
        using var brush = new SolidBrush(fill);
        g.FillEllipse(brush, box);
        var fg = Motion.Blend(surface, Motion.Blend(Muted, Ink, closeHoverAmount), opacity);
        TextRenderer.DrawText(g, Glyphs.Close, FontForDpi(CloseIconFont, dpi), box, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    public static void DrawSpinner(Graphics g, Rectangle box, int angle, bool active, float dpiScale = 1f)
    {
        var stroke = Math.Max(1.5f, 1.5f * dpiScale);
        var inset = Math.Max(1.5f, box.Width * 0.14f);
        var rect = new RectangleF(box.X + inset, box.Y + inset, Math.Max(2f, box.Width - inset * 2f), Math.Max(2f, box.Height - inset * 2f));
        using var trackPen = new Pen(Hairline, stroke);
        g.DrawEllipse(trackPen, rect);
        using var arcPen = new Pen(active ? InkSoft : Muted, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arcPen, rect, angle, 110f);
    }

    public static void DrawAudioGlyph(Graphics g, Rectangle box, TabAudioState audio, bool active, int dpi = 96, bool tiny = false)
    {
        if (audio == TabAudioState.None) return;
        var glyph = audio == TabAudioState.Muted ? Glyphs.Muted : Glyphs.Audio;
        var font = FontForDpi(tiny ? CloseIconFont : SmallIconFont, dpi);
        var color = audio == TabAudioState.Muted ? Muted : (active ? InkSoft : Muted);
        TextRenderer.DrawText(g, glyph, font, box, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

enum TabAudioState { None, Playing, Muted }

interface ITabItem
{
    bool Pinned { get; }
    bool Loading { get; }
    TabAudioState Audio { get; }
    void UpdateTab(string title, string? url, bool active, Image? favicon, bool loading = false, TabAudioState audio = TabAudioState.None, int loadingPhase = 0);
    void SetLoadingPhase(int phase);
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
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Invalidate(); }
    protected int S(int px) => LogicalToDeviceUnits(px);
    protected float Sf(float px) => px * (DeviceDpi / 96f);
}

sealed class IconButton : ChromeControl
{
    string glyph;
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

    internal string Glyph => glyph;

    internal void SetGlyph(string value, string name)
    {
        if (glyph == value && AccessibleName == name) return;
        glyph = value;
        AccessibleName = name;
        Invalidate();
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
        TextRenderer.DrawText(g, glyph, Theme.FontForDpi(Theme.IconFont, DeviceDpi), ClientRectangle, color,
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
        TextRenderer.DrawText(g, glyph, Theme.FontForDpi(Theme.CaptionIconFont, DeviceDpi), ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

sealed class AddressLine : ChromeControl
{
    readonly TextBox input;
    Color fillColor = Theme.SideHover;
    bool focused;
    string? zoomLabel;
    Rectangle zoomPillRect;
    bool zoomHovered;
    bool zoomPressed;

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
            AccessibleName = "Address",
            Visible = false
        };
        input.GotFocus += (_, _) => {
            focused = true;
            input.Visible = true;
            input.ForeColor = Theme.Ink;
            Invalidate();
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(() => { if (!IsDisposed && !input.IsDisposed && input.Focused) input.SelectAll(); });
        };
        input.LostFocus += (_, _) => {
            focused = false;
            input.Visible = false;
            input.ForeColor = Theme.InkSoft;
            Invalidate();
        };
        input.TextChanged += (_, _) => { if (!focused) Invalidate(); };
        Controls.Add(input);
        Height = S(28);
    }

    internal TextBox Input => input;
    internal bool InputFocused => input.Focused;
    internal string DisplayText => string.IsNullOrEmpty(input.Text) ? input.PlaceholderText : FormatDisplayUrl(input.Text);
    internal bool ZoomPillVisible => zoomLabel is not null;
    internal string? ZoomPillText => zoomLabel;
    internal Rectangle ZoomPillBounds => zoomPillRect;
    internal event Action? ZoomResetRequested;

    internal void SetZoom(double factor)
    {
        var next = ZoomLevels.IsDefault(factor) ? null : ZoomLevels.FormatPercent(factor);
        if (zoomLabel == next) return;
        zoomLabel = next;
        if (next is null) { zoomHovered = false; zoomPressed = false; Cursor = Cursors.IBeam; }
        PerformLayout();
        Invalidate();
    }

    internal static string FormatDisplayUrl(string url)
    {
        if (url.Length > 8 && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url[8..];
            return rest.IndexOf('/') == rest.Length - 1 ? rest[..^1] : rest;
        }
        return url;
    }

    internal void SetSurface(Color surface, Color fill)
    {
        BackColor = surface;
        fillColor = fill;
        input.BackColor = fill;
        Invalidate();
    }

    internal void FocusInput()
    {
        input.Visible = true;
        if (!input.Focus())
        {
            input.Visible = input.Focused;
            return;
        }
        input.SelectAll();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        input.Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        PerformLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        input.Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        Height = S(28);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var pad = S(10);
        if (zoomLabel is not null && Width >= S(90))
        {
            var textW = TextRenderer.MeasureText(zoomLabel, Theme.FontForDpi(Theme.HintFont, DeviceDpi), Size.Empty, TextFormatFlags.NoPadding).Width;
            var pillW = textW + S(10);
            var pillH = S(20);
            var pillInset = S(4);
            zoomPillRect = new Rectangle(Width - pillInset - pillW, (Height - pillH) / 2, pillW, pillH);
        }
        else
        {
            zoomPillRect = Rectangle.Empty;
        }
        var rightPad = zoomPillRect.IsEmpty ? pad : (Width - zoomPillRect.Left) + S(6);
        var h = input.PreferredHeight;
        input.SetBounds(pad, Math.Max(0, (Height - h) / 2), Math.Max(0, Width - pad - rightPad), h);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var over = !zoomPillRect.IsEmpty && zoomPillRect.Contains(e.Location);
        if (over != zoomHovered)
        {
            zoomHovered = over;
            Cursor = over ? Cursors.Hand : Cursors.IBeam;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (zoomHovered)
        {
            zoomHovered = false;
            Cursor = Cursors.IBeam;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (!zoomPillRect.IsEmpty && zoomPillRect.Contains(e.Location))
        {
            zoomPressed = true;
            return;
        }
        zoomPressed = false;
        if (!input.Focused) FocusInput();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !zoomPressed) return;
        zoomPressed = false;
        if (!zoomPillRect.IsEmpty && zoomPillRect.Contains(e.Location))
            ZoomResetRequested?.Invoke();
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
        else
        {
            var pad = S(10);
            var rightPad = zoomPillRect.IsEmpty ? pad : (Width - zoomPillRect.Left) + S(6);
            var textRect = new Rectangle(pad, 0, Math.Max(0, Width - pad - rightPad), Height);
            var empty = string.IsNullOrEmpty(input.Text);
            TextRenderer.DrawText(g, DisplayText, Theme.FontForDpi(Theme.UiFont, DeviceDpi), textRect, empty ? Theme.Muted : Theme.InkSoft,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        if (!zoomPillRect.IsEmpty && zoomLabel is not null)
        {
            Theme.FillRounded(g, zoomHovered ? Theme.CloseHoverFill : Theme.BadgeFill, zoomPillRect, Sf(6f));
            TextRenderer.DrawText(g, zoomLabel, Theme.FontForDpi(Theme.HintFont, DeviceDpi), zoomPillRect, zoomHovered ? Theme.Ink : Theme.InkSoft,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}

sealed class TabDragCallbacks(Func<Point, bool> start, Action<Point> move, Action<Point> drop, Action cancel)
{
    public Func<Point, bool> Start { get; } = start;
    public Action<Point> Move { get; } = move;
    public Action<Point> Drop { get; } = drop;
    public Action Cancel { get; } = cancel;
}

abstract class TabChromeControl : ChromeControl, ITabItem, IMotionTarget
{
    const int WM_LBUTTONUP = 0x0202;
    const int WM_MBUTTONUP = 0x0208;

    readonly Action onSelect;
    readonly Action onClose;
    readonly TabDragCallbacks? drag;
    protected readonly MotionClock? motion;
    protected AnimScalar hoverAnim;
    protected AnimScalar activeAnim;
    protected AnimScalar closeHoverAnim;
    protected AnimScalar appearAnim;
    protected bool hovered;
    protected bool dragSource;
    Point? pressPoint;
    bool pressedInClose;
    bool middlePressed;
    bool dragging;
    bool hadCapture;
    bool suppressClick;
    bool releasingButton;

    protected TabChromeControl(Action onSelect, Action onClose, TabDragCallbacks? drag, MotionClock? motion, bool initialActive)
    {
        this.onSelect = onSelect;
        this.onClose = onClose;
        this.drag = drag;
        this.motion = motion;
        activeAnim.Snap(initialActive ? 1f : 0f);
        appearAnim.Snap(1f);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PageTab;
    }

    public abstract bool Pinned { get; }
    public abstract bool Loading { get; }
    public abstract TabAudioState Audio { get; }
    public abstract void UpdateTab(string title, string? url, bool active, Image? favicon, bool loading = false, TabAudioState audio = TabAudioState.None, int loadingPhase = 0);
    public abstract void SetLoadingPhase(int phase);

    Rectangle baseBounds;
    bool closing;

    internal Rectangle BaseBounds => baseBounds;
    internal bool Closing => closing;
    internal void MarkClosing() => closing = true;
    internal float HoverProgress => hoverAnim.Value;
    internal float ActiveProgress => activeAnim.Value;
    internal float CloseHoverProgress => closeHoverAnim.Value;
    internal float AppearProgress => appearAnim.Value;

    internal void SetBaseBounds(int x, int y, int w, int h, Point offset = default)
    {
        baseBounds = new Rectangle(x, y, w, h);
        SetBounds(x + offset.X, y + offset.Y, w, h);
    }

    internal void ShiftBaseX(int delta)
    {
        baseBounds = new Rectangle(baseBounds.X + delta, baseBounds.Y, baseBounds.Width, baseBounds.Height);
        Left += delta;
    }

    internal void SetHoverState(bool value)
    {
        if (hovered == value && !hoverAnim.Active) return;
        hovered = value;
        if (!value) ClearCloseHover();
        if (hoverAnim.SetTarget(value ? 1f : 0f, motion, Motion.HoverDurationMs))
        {
            if (hoverAnim.Active) motion?.Track(this);
            Invalidate();
        }
    }

    protected void AnimateActiveState(bool value)
    {
        if (activeAnim.SetTarget(value ? 1f : 0f, motion, Motion.HoverDurationMs) && activeAnim.Active)
            motion?.Track(this);
    }

    protected void SetCloseHoverState(bool value)
    {
        if (closeHoverAnim.SetTarget(value ? 1f : 0f, motion, Motion.HoverDurationMs))
        {
            if (closeHoverAnim.Active) motion?.Track(this);
            Invalidate();
        }
    }

    internal void TriggerAppear(float startOpacity = 0f, double durationMs = Motion.PinTransitionDurationMs)
    {
        if (appearAnim.Start(startOpacity, 1f, motion, durationMs) && appearAnim.Active)
            motion?.Track(this);
        Invalidate();
    }

    internal void SetAppearOpacity(float opacity)
    {
        var clamped = Math.Clamp(opacity, 0f, 1f);
        if (MathF.Abs(appearAnim.Value - clamped) <= 0.001f && !appearAnim.Active) return;
        appearAnim.Snap(clamped);
        if (!IsDisposed) Invalidate();
    }

    bool IMotionTarget.StepMotion(double nowMs)
    {
        if (IsDisposed) return false;
        var a1 = hoverAnim.Step(nowMs);
        var a2 = activeAnim.Step(nowMs);
        var a3 = closeHoverAnim.Step(nowMs);
        var a4 = appearAnim.Step(nowMs);
        Invalidate();
        return a1 || a2 || a3 || a4;
    }

    void IMotionTarget.SnapMotion()
    {
        hoverAnim.Snap(hoverAnim.Target);
        activeAnim.Snap(activeAnim.Target);
        closeHoverAnim.Snap(closeHoverAnim.Target);
        appearAnim.Snap(appearAnim.Target);
        if (!IsDisposed) Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) motion?.Untrack(this);
        base.Dispose(disposing);
    }

    protected virtual bool IsClosePoint(Point p) => false;
    protected virtual void UpdateCloseHover(Point p) { }
    protected virtual void ClearCloseHover() { }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WM_LBUTTONUP or WM_MBUTTONUP)
        {
            releasingButton = true;
            try { base.WndProc(ref m); }
            finally { releasingButton = false; }
            return;
        }
        base.WndProc(ref m);
    }

    internal void CancelDrag()
    {
        var wasDragging = dragging || dragSource;
        dragging = false;
        dragSource = false;
        hadCapture = false;
        pressPoint = null;
        pressedInClose = false;
        middlePressed = false;
        if (wasDragging) suppressClick = true;
        if (Capture) Capture = false;
        if (wasDragging) Invalidate();
    }

    protected void PaintAppearWash(Graphics g)
    {
        if (appearAnim.Value >= 0.999f) return;
        using var wash = new SolidBrush(Motion.WithAlpha(BackColor, 1f - appearAnim.Value));
        g.FillRectangle(wash, ClientRectangle);
    }

    protected void PaintDragGhost(Graphics g)
    {
        if (!dragSource) return;
        using var wash = new SolidBrush(Color.FromArgb(40, BackColor));
        g.FillRectangle(wash, ClientRectangle);
        Theme.DrawRounded(g, Theme.DropLine, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Sf(8.5f));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Closing && !dragging) SetHoverState(true);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!Closing) SetHoverState(false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Closing) return;
        if (dragging && e.Button == MouseButtons.Right)
        {
            CancelDrag();
            drag?.Cancel();
            return;
        }
        if (e.Button == MouseButtons.Middle)
        {
            middlePressed = !dragging && ClientRectangle.Contains(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        suppressClick = false;
        dragging = false;
        hadCapture = false;
        if (IsClosePoint(e.Location))
        {
            pressedInClose = true;
            pressPoint = null;
        }
        else
        {
            pressedInClose = false;
            pressPoint = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Closing) return;
        if (!dragging)
        {
            UpdateCloseHover(e.Location);
            if (pressPoint is { } start && drag is not null)
            {
                var threshold = SystemInformation.DragSize;
                if (Math.Abs(e.X - start.X) > threshold.Width / 2 || Math.Abs(e.Y - start.Y) > threshold.Height / 2)
                {
                    var screenPt = PointToScreen(e.Location);
                    if (drag.Start(screenPt))
                    {
                        dragging = true;
                        dragSource = true;
                        SetHoverState(false);
                        Capture = true;
                        hadCapture = Capture;
                        Invalidate();
                    }
                    else pressPoint = null;
                }
            }
            return;
        }
        drag?.Move(PointToScreen(e.Location));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (Closing) return;
        if (e.Button == MouseButtons.Middle)
        {
            var wasMiddle = middlePressed;
            middlePressed = false;
            if (!dragging && wasMiddle && ClientRectangle.Contains(e.Location)) onClose();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (dragging)
        {
            dragging = false;
            dragSource = false;
            hadCapture = false;
            pressPoint = null;
            pressedInClose = false;
            Invalidate();
            if (Capture) Capture = false;
            drag?.Drop(PointToScreen(e.Location));
            return;
        }
        var wasClose = pressedInClose;
        var suppressed = suppressClick;
        pressPoint = null;
        pressedInClose = false;
        suppressClick = false;
        if (suppressed || !ClientRectangle.Contains(e.Location)) return;
        if (wasClose) { if (IsClosePoint(e.Location)) onClose(); }
        else if (!IsClosePoint(e.Location)) onSelect();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (releasingButton || Capture) return;
        pressPoint = null;
        pressedInClose = false;
        middlePressed = false;
        if (dragging && hadCapture)
        {
            dragging = false;
            dragSource = false;
            hadCapture = false;
            suppressClick = true;
            Invalidate();
            drag?.Cancel();
        }
    }
}

sealed class TabRow : TabChromeControl
{
    string title;
    string? badge;
    Image? favicon;
    bool active;
    bool loading;
    TabAudioState audio;
    int loadingPhase;
    bool closeHovered;

    internal TabRow(string title, string? url, bool active, Image? favicon, Action onSelect, Action onClose, TabDragCallbacks? drag = null, bool loading = false, TabAudioState audio = TabAudioState.None, int loadingPhase = 0, MotionClock? motion = null)
        : base(onSelect, onClose, drag, motion, active)
    {
        this.title = title;
        badge = url is null ? null : Theme.Monogram(url, title);
        this.favicon = favicon;
        this.active = active;
        this.loading = loading;
        this.audio = audio;
        this.loadingPhase = loadingPhase;
        BackColor = Theme.Frame;
        Height = S(28);
        AccessibleName = title;
    }

    public override bool Pinned => false;
    public override bool Loading => loading;
    public override TabAudioState Audio => audio;

    public override void UpdateTab(string title, string? url, bool active, Image? favicon, bool loading = false, TabAudioState audio = TabAudioState.None, int loadingPhase = 0)
    {
        var nextBadge = url is null ? null : Theme.Monogram(url, title);
        if (this.title == title && badge == nextBadge && this.active == active && ReferenceEquals(this.favicon, favicon)
            && this.loading == loading && this.audio == audio && (!loading || this.loadingPhase == loadingPhase)) return;
        var activeChanged = this.active != active;
        this.title = title;
        badge = nextBadge;
        this.favicon = favicon;
        this.active = active;
        this.loading = loading;
        this.audio = audio;
        this.loadingPhase = loadingPhase;
        AccessibleName = title;
        if (activeChanged) AnimateActiveState(active);
        Invalidate();
    }

    public override void SetLoadingPhase(int phase)
    {
        if (!loading || loadingPhase == phase) return;
        loadingPhase = phase;
        Invalidate();
    }

    Rectangle CloseDotRect => new(Width - S(7) - S(15), (Height - S(15)) / 2, S(15), S(15));
    protected override bool IsClosePoint(Point p) => hovered && p.X >= Width - S(26);
    protected override void ClearCloseHover()
    {
        closeHovered = false;
        SetCloseHoverState(false);
    }
    protected override void UpdateCloseHover(Point p)
    {
        var over = IsClosePoint(p);
        if (over != closeHovered)
        {
            closeHovered = over;
            SetCloseHoverState(over);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Width <= 1 || Height <= 1) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var ht = hoverAnim.Value;
        var at = activeAnim.Value;
        var cht = closeHoverAnim.Value;
        var surface = Motion.Blend(Motion.Blend(BackColor, Theme.SideHover, ht), Theme.SideActive, at);
        if (ht > 0.001f || at > 0.001f)
        {
            var lift = (1f - MathF.Max(ht, at)) * Sf(0.6f);
            Theme.FillRounded(g, surface, new RectangleF(lift * 0.5f, lift, Math.Max(0, Width - lift), Math.Max(0, Height - lift)), Sf(9f));
        }

        var x = S(10);
        if (loading || favicon is not null || badge is not null)
        {
            var mark = S(16);
            var box = new Rectangle(x, (Height - mark) / 2, mark, mark);
            if (loading) Theme.DrawSpinner(g, box, loadingPhase, active, DeviceDpi / 96f);
            else if (favicon is not null) Theme.DrawFavicon(g, box, favicon);
            else Theme.DrawMonogram(g, box, badge!, active, DeviceDpi);
            x += mark + S(8);
        }

        var showClose = ht > 0.01f;
        var showAudio = audio != TabAudioState.None;
        var rightPad = showClose ? (showAudio ? S(46) : S(28)) : (showAudio ? S(28) : S(10));
        var textRect = new Rectangle(x, 0, Math.Max(0, Width - x - rightPad), Height);
        var textColor = Motion.Blend(Motion.Blend(Theme.Muted, Theme.InkSoft, ht), Theme.Ink, at);
        TextRenderer.DrawText(g, title, Theme.FontForDpi(Theme.UiFont, DeviceDpi), textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (showAudio)
        {
            var audioSize = S(15);
            var restAudioX = Width - S(7) - audioSize;
            var hoverAudioX = CloseDotRect.Left - S(4) - audioSize;
            var audioX = (int)MathF.Round(Motion.Lerp(restAudioX, hoverAudioX, ht));
            Theme.DrawAudioGlyph(g, new Rectangle(audioX, (Height - audioSize) / 2, audioSize, audioSize), audio, active, DeviceDpi);
        }
        if (showClose) Theme.DrawCloseDot(g, CloseDotRect, cht, ht, surface, DeviceDpi);
        PaintAppearWash(g);
        PaintDragGhost(g);
    }
}

sealed class PinSquare : TabChromeControl
{
    string letter;
    Image? favicon;
    bool active;
    bool loading;
    TabAudioState audio;
    int loadingPhase;
    readonly bool onFrame;

    internal PinSquare(string title, string? url, bool active, Image? favicon, bool onFrame, Action onSelect, Action onClose, TabDragCallbacks? drag = null, bool loading = false, TabAudioState audio = TabAudioState.None, int loadingPhase = 0, MotionClock? motion = null)
        : base(onSelect, onClose, drag, motion, active)
    {
        letter = Theme.Monogram(url, title);
        this.favicon = favicon;
        this.active = active;
        this.loading = loading;
        this.audio = audio;
        this.loadingPhase = loadingPhase;
        this.onFrame = onFrame;
        BackColor = onFrame ? Theme.Frame : Theme.Ground;
        AccessibleName = title;
    }

    public override bool Pinned => true;
    public override bool Loading => loading;
    public override TabAudioState Audio => audio;
    internal bool OnFrame => onFrame;

    public override void UpdateTab(string title, string? url, bool active, Image? favicon, bool loading = false, TabAudioState audio = TabAudioState.None, int loadingPhase = 0)
    {
        var nextLetter = Theme.Monogram(url, title);
        if (AccessibleName == title && letter == nextLetter && this.active == active && ReferenceEquals(this.favicon, favicon)
            && this.loading == loading && this.audio == audio && (!loading || this.loadingPhase == loadingPhase)) return;
        var activeChanged = this.active != active;
        letter = nextLetter;
        this.favicon = favicon;
        this.active = active;
        this.loading = loading;
        this.audio = audio;
        this.loadingPhase = loadingPhase;
        AccessibleName = title;
        if (activeChanged) AnimateActiveState(active);
        Invalidate();
    }

    public override void SetLoadingPhase(int phase)
    {
        if (!loading || loadingPhase == phase) return;
        loadingPhase = phase;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Width <= 1 || Height <= 1) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var ht = hoverAnim.Value;
        var at = activeAnim.Value;
        var restTile = !onFrame && Theme.Dark ? Color.FromArgb(0x24, 0x24, 0x24) : Theme.PinTile;
        var hoverTile = onFrame ? Theme.SideHover : Theme.StripHover;
        var fill = Motion.Blend(Motion.Blend(restTile, hoverTile, ht), Theme.PinActive, at);
        var inset = (1f - MathF.Max(ht, at)) * Sf(0.5f);
        Theme.FillRounded(g, fill, new RectangleF(inset, inset, Math.Max(0, Width - inset * 2f), Math.Max(0, Height - inset * 2f)), Sf(9f));
        if (loading)
        {
            var mark = S(16);
            Theme.DrawSpinner(g, new Rectangle((Width - mark) / 2, (Height - mark) / 2, mark, mark), loadingPhase, active, DeviceDpi / 96f);
        }
        else if (favicon is not null)
        {
            var mark = S(16);
            Theme.DrawFavicon(g, new Rectangle((Width - mark) / 2, (Height - mark) / 2, mark, mark), favicon);
        }
        else
        {
            var color = Motion.Blend(Motion.Blend(Theme.Muted, Theme.InkSoft, ht), Theme.Ink, at);
            TextRenderer.DrawText(g, letter, Theme.FontForDpi(onFrame ? Theme.PinFont : Theme.StripPinFont, DeviceDpi), ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        if (audio != TabAudioState.None)
        {
            var aSize = S(11);
            Theme.DrawAudioGlyph(g, new Rectangle(Width - aSize - S(2), S(2), aSize, aSize), audio, active, DeviceDpi, tiny: true);
        }
        PaintAppearWash(g);
        PaintDragGhost(g);
    }
}

sealed class TabPill : TabChromeControl
{
    string title;
    string? badge;
    string compactLetter;
    Image? favicon;
    bool active;
    bool loading;
    TabAudioState audio;
    int loadingPhase;
    bool closeHovered;

    internal TabPill(string title, string? url, bool active, Image? favicon, Action onSelect, Action onClose, TabDragCallbacks? drag = null, bool loading = false, TabAudioState audio = TabAudioState.None, int loadingPhase = 0, MotionClock? motion = null)
        : base(onSelect, onClose, drag, motion, active)
    {
        this.title = title;
        badge = url is null ? null : Theme.Monogram(url, title);
        compactLetter = Theme.Monogram(url, title);
        this.favicon = favicon;
        this.active = active;
        this.loading = loading;
        this.audio = audio;
        this.loadingPhase = loadingPhase;
        BackColor = Theme.Ground;
        Height = S(28);
        AccessibleName = title;
    }

    public override bool Pinned => false;
    public override bool Loading => loading;
    public override TabAudioState Audio => audio;

    public override void UpdateTab(string title, string? url, bool active, Image? favicon, bool loading = false, TabAudioState audio = TabAudioState.None, int loadingPhase = 0)
    {
        var nextBadge = url is null ? null : Theme.Monogram(url, title);
        var nextCompact = Theme.Monogram(url, title);
        if (this.title == title && badge == nextBadge && compactLetter == nextCompact && this.active == active && ReferenceEquals(this.favicon, favicon)
            && this.loading == loading && this.audio == audio && (!loading || this.loadingPhase == loadingPhase)) return;
        var activeChanged = this.active != active;
        this.title = title;
        badge = nextBadge;
        compactLetter = nextCompact;
        this.favicon = favicon;
        this.active = active;
        this.loading = loading;
        this.audio = audio;
        this.loadingPhase = loadingPhase;
        AccessibleName = title;
        if (activeChanged) AnimateActiveState(active);
        Invalidate();
    }

    public override void SetLoadingPhase(int phase)
    {
        if (!loading || loadingPhase == phase) return;
        loadingPhase = phase;
        Invalidate();
    }

    bool Compact => Width < S(78);
    Rectangle CloseDotRect => new(Width - S(7) - S(15), (Height - S(15)) / 2, S(15), S(15));
    protected override bool IsClosePoint(Point p) => hovered && !Compact && p.X >= Width - S(26);
    protected override void ClearCloseHover()
    {
        closeHovered = false;
        SetCloseHoverState(false);
    }
    protected override void UpdateCloseHover(Point p)
    {
        var over = IsClosePoint(p);
        if (over != closeHovered)
        {
            closeHovered = over;
            SetCloseHoverState(over);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Width <= 1 || Height <= 1) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var ht = hoverAnim.Value;
        var at = activeAnim.Value;
        var cht = closeHoverAnim.Value;
        var surface = Motion.Blend(Motion.Blend(BackColor, Theme.StripHover, ht), Theme.StripActive, at);
        if (ht > 0.001f || at > 0.001f)
        {
            var lift = (1f - MathF.Max(ht, at)) * Sf(0.6f);
            Theme.FillRounded(g, surface, new RectangleF(lift * 0.5f, lift, Math.Max(0, Width - lift), Math.Max(0, Height - lift)), Sf(9f));
        }

        var mark = S(16);
        if (Compact)
        {
            var box = new Rectangle((Width - mark) / 2, (Height - mark) / 2, mark, mark);
            if (loading) Theme.DrawSpinner(g, box, loadingPhase, active, DeviceDpi / 96f);
            else if (favicon is not null) Theme.DrawFavicon(g, box, favicon);
            else Theme.DrawMonogram(g, box, compactLetter, active, DeviceDpi);
            if (audio != TabAudioState.None)
            {
                var aSize = S(10);
                Theme.DrawAudioGlyph(g, new Rectangle(Width - aSize - S(2), S(2), aSize, aSize), audio, active, DeviceDpi, tiny: true);
            }
            PaintAppearWash(g);
            PaintDragGhost(g);
            return;
        }

        var x = S(10);
        if (loading || favicon is not null || badge is not null)
        {
            var box = new Rectangle(x, (Height - mark) / 2, mark, mark);
            if (loading) Theme.DrawSpinner(g, box, loadingPhase, active, DeviceDpi / 96f);
            else if (favicon is not null) Theme.DrawFavicon(g, box, favicon);
            else Theme.DrawMonogram(g, box, badge!, active, DeviceDpi);
            x += mark + S(6);
        }

        var showClose = ht > 0.01f;
        var showAudio = audio != TabAudioState.None && (!showClose || Width >= S(100));
        var rightPad = showClose ? (showAudio ? S(44) : S(26)) : (showAudio ? S(26) : S(8));
        var textRect = new Rectangle(x, 0, Math.Max(0, Width - x - rightPad), Height);
        var textColor = Motion.Blend(Motion.Blend(Theme.Muted, Theme.InkSoft, ht), Theme.Ink, at);
        TextRenderer.DrawText(g, title, Theme.FontForDpi(Theme.UiFont, DeviceDpi), textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (showAudio)
        {
            var audioSize = S(15);
            var restAudioX = Width - S(6) - audioSize;
            var hoverAudioX = CloseDotRect.Left - S(3) - audioSize;
            var audioX = (int)MathF.Round(Motion.Lerp(restAudioX, hoverAudioX, ht));
            Theme.DrawAudioGlyph(g, new Rectangle(audioX, (Height - audioSize) / 2, audioSize, audioSize), audio, active, DeviceDpi);
        }
        if (showClose) Theme.DrawCloseDot(g, CloseDotRect, cht, ht, surface, DeviceDpi);
        PaintAppearWash(g);
        PaintDragGhost(g);
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
        TextRenderer.DrawText(g, Theme.Glyphs.NewTab, Theme.FontForDpi(Theme.SmallIconFont, DeviceDpi), iconRect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var x = iconRect.Right + S(8);
        var textRect = new Rectangle(x, 0, Math.Max(0, Width - x - S(10)), Height);
        TextRenderer.DrawText(g, Text, Theme.FontForDpi(Theme.UiFont, DeviceDpi), textRect, color,
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

sealed class DropLineBar : ChromeControl
{
    internal DropLineBar() => Visible = false;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Theme.DropLine);
}

sealed class PinGridPanel : Panel
{
    bool dropHighlight;
    bool emptyPlaceholder;
    Rectangle? insertSlot;

    internal PinGridPanel()
    {
        BackColor = Theme.Frame;
        DoubleBuffered = true;
    }

    internal void SetDropPreview(bool highlight, bool placeholder, Rectangle? slot)
    {
        if (dropHighlight == highlight && emptyPlaceholder == placeholder && insertSlot == slot) return;
        dropHighlight = highlight;
        emptyPlaceholder = placeholder;
        insertSlot = slot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!dropHighlight && !emptyPlaceholder && insertSlot is null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var radius = 9f * (DeviceDpi / 96f);
        var padBottom = LogicalToDeviceUnits(10);
        if (emptyPlaceholder)
        {
            var r = new RectangleF(0.5f, 0.5f, Math.Max(0, Width - 1f), Math.Max(0, Height - padBottom - 1f));
            if (r.Width <= 1 || r.Height <= 1) return;
            Theme.FillRounded(g, dropHighlight ? Theme.DropSlotFill : Theme.SideHover, r, radius);
            Theme.DrawRounded(g, dropHighlight ? Theme.DropLine : Theme.Hairline, r, radius);
            var textRect = new Rectangle(0, 0, Width, Math.Max(0, Height - padBottom));
            TextRenderer.DrawText(g, "Drop to pin", Theme.FontForDpi(Theme.UiFont, DeviceDpi), textRect, dropHighlight ? Theme.DropLine : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return;
        }
        if (insertSlot is { } slot && slot.Width > 1 && slot.Height > 1)
        {
            var r = new RectangleF(slot.X + 0.5f, slot.Y + 0.5f, slot.Width - 1f, slot.Height - 1f);
            Theme.FillRounded(g, Theme.DropSlotFill, r, radius);
            Theme.DrawRounded(g, Theme.DropLine, r, radius, 1.5f);
        }
        else if (dropHighlight && Height > padBottom + 2)
        {
            var r = new RectangleF(0.5f, 0.5f, Math.Max(0, Width - 1f), Math.Max(0, Height - padBottom - 1f));
            Theme.FillRounded(g, Theme.DropSlotFill, r, radius);
            Theme.DrawRounded(g, Theme.DropLine, r, radius);
        }
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
    internal int ScrollOffset => scrollOffset;

    internal bool ScrollBy(int delta)
    {
        if (contentHeight <= ClientSize.Height || delta == 0) return false;
        var prev = scrollOffset;
        scrollOffset += delta;
        ClampScroll();
        if (scrollOffset == prev) return false;
        content.Top = -scrollOffset;
        return true;
    }

    internal void EnsureVisible(Control item)
    {
        if (item.Parent != content || ClientSize.Height <= 0 || contentHeight <= ClientSize.Height) return;
        if (item.Top < scrollOffset) scrollOffset = item.Top;
        else if (item.Bottom > scrollOffset + ClientSize.Height) scrollOffset = item.Bottom - ClientSize.Height;
        else return;
        ClampScroll();
        content.Top = -scrollOffset;
    }

    internal void SetItems(IReadOnlyList<Control> controls, Control footer, int? rowHeight = null, int? itemGap = null, Func<Control, int>? heightOf = null, Func<TabChromeControl, Point>? offsetOf = null)
    {
        content.SuspendLayout();
        var keep = new HashSet<Control>(controls) { footer };
        for (var i = content.Controls.Count - 1; i >= 0; i--)
        {
            if (keep.Contains(content.Controls[i])) continue;
            content.Controls.RemoveAt(i);
        }
        var rowH = rowHeight ?? LogicalToDeviceUnits(28);
        var gap = itemGap ?? LogicalToDeviceUnits(2);
        var y = 0;
        var w = Math.Max(0, ClientSize.Width);
        for (var i = 0; i < controls.Count; i++)
        {
            var c = controls[i];
            if (c.Parent != content) content.Controls.Add(c);
            if (content.Controls.GetChildIndex(c) != i) content.Controls.SetChildIndex(c, i);
            var h = heightOf is not null ? Math.Max(0, heightOf(c)) : rowH;
            if (c is TabChromeControl tc) tc.SetBaseBounds(0, y, w, h, offsetOf?.Invoke(tc) ?? Point.Empty);
            else c.SetBounds(0, y, w, h);
            if (h > 0) y += h + (h < rowH ? (int)MathF.Round(gap * (h / (float)rowH)) : gap);
        }
        if (footer.Parent != content) content.Controls.Add(footer);
        if (content.Controls.GetChildIndex(footer) != controls.Count) content.Controls.SetChildIndex(footer, controls.Count);
        footer.SetBounds(0, y, w, rowH);
        contentHeight = y + rowH;
        ClampScroll();
        content.SetBounds(0, -scrollOffset, w, Math.Max(contentHeight, ClientSize.Height));
        content.ResumeLayout();
    }

    internal void LayoutItemBounds(IReadOnlyList<Control> controls, Control footer, int rowH, int gap, Func<Control, int> heightOf, Func<TabChromeControl, Point>? offsetOf = null)
    {
        content.SuspendLayout();
        var y = 0;
        var w = Math.Max(0, ClientSize.Width);
        for (var i = 0; i < controls.Count; i++)
        {
            var c = controls[i];
            var h = Math.Max(0, heightOf(c));
            if (c is TabChromeControl tc) tc.SetBaseBounds(0, y, w, h, offsetOf?.Invoke(tc) ?? Point.Empty);
            else c.SetBounds(0, y, w, h);
            if (h > 0) y += h + (h < rowH ? (int)MathF.Round(gap * (h / (float)rowH)) : gap);
        }
        footer.SetBounds(0, y, w, rowH);
        contentHeight = y + rowH;
        ClampScroll();
        content.SetBounds(0, -scrollOffset, w, Math.Max(contentHeight, ClientSize.Height));
        content.ResumeLayout(false);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (contentHeight <= ClientSize.Height) return;
        if (e is HandledMouseEventArgs h) h.Handled = true;
        ScrollBy(-e.Delta / 3);
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
