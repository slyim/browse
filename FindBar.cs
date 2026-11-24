using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace SearchPort;

sealed class FindToggle : ChromeControl
{
    readonly string label;
    readonly bool underline;
    readonly Action onToggle;
    bool active;
    bool hovered;

    internal FindToggle(string label, string tip, bool underline, Action onToggle)
    {
        this.label = label;
        this.underline = underline;
        this.onToggle = onToggle;
        AccessibleName = tip;
        AccessibleRole = AccessibleRole.CheckButton;
        BackColor = Theme.Frame;
        Cursor = Cursors.Hand;
        Size = new(S(24), S(24));
    }

    internal bool Active => active;

    internal void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) onToggle();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { onToggle(); e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (active || hovered)
            Theme.FillRounded(g, active ? Theme.SideActive : Theme.SideHover, new RectangleF(0, 0, Width, Height), Sf(6f));
        var color = active ? Theme.Ink : hovered ? Theme.InkSoft : Theme.Muted;
        TextRenderer.DrawText(g, label, Theme.FontForDpi(Theme.HintFont, DeviceDpi), ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (underline)
        {
            var y = Height - S(5);
            var inset = S(5);
            using var pen = new Pen(color, Math.Max(1f, Sf(1f)));
            g.DrawLine(pen, inset, y, Width - inset, y);
        }
    }
}

sealed class FindBar : ChromeControl
{
    readonly TextBox input;
    readonly FindToggle caseBtn;
    readonly FindToggle wordBtn;
    readonly IconButton prevBtn;
    readonly IconButton nextBtn;
    readonly IconButton closeBtn;
    readonly Action onReturnFocus;

    CoreWebView2? boundCore;
    CoreWebView2Find? boundFind;
    CoreWebView2Environment? boundEnv;
    int searchVersion;
    bool hasActiveSession;
    int matchCount;
    int activeMatchIndex;
    Rectangle inputBoxRect;
    Rectangle counterRect;
    Rectangle sepRect;

    internal FindBar(ToolTip tips, Action onReturnFocus)
    {
        this.onReturnFocus = onReturnFocus;
        BackColor = Theme.Frame;
        Visible = false;
        AccessibleName = "Find in page";

        input = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = Theme.UiFont,
            BackColor = Theme.SideHover,
            ForeColor = Theme.Ink,
            PlaceholderText = "Find in page",
            AccessibleName = "Find in page",
        };
        input.TextChanged += (_, _) => _ = StartSearchAsync();
        input.KeyDown += (_, e) =>
        {
            if (e.KeyData == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                FindNext();
            }
            else if (e.KeyData == (Keys.Shift | Keys.Enter))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                FindPrevious();
            }
            else if (e.KeyData == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Close(returnFocus: true);
            }
            else if (e.KeyData == Keys.F3)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                FindNext();
            }
            else if (e.KeyData == (Keys.Shift | Keys.F3))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                FindPrevious();
            }
        };

        caseBtn = new FindToggle("Aa", "Match case", underline: false, ToggleCaseSensitive);
        wordBtn = new FindToggle("ab", "Whole word", underline: true, ToggleWholeWord);
        prevBtn = new IconButton(Theme.Glyphs.ChevronUp, "Previous match", () => { FindPrevious(); input.Focus(); }) { Enabled = false };
        nextBtn = new IconButton(Theme.Glyphs.ChevronDown, "Next match", () => { FindNext(); input.Focus(); }) { Enabled = false };
        closeBtn = new IconButton(Theme.Glyphs.Close, "Close find bar", () => Close(returnFocus: true));

        tips.SetToolTip(caseBtn, "Match case");
        tips.SetToolTip(wordBtn, "Whole word");
        tips.SetToolTip(prevBtn, "Previous match (Shift+Enter)");
        tips.SetToolTip(nextBtn, "Next match (Enter)");
        tips.SetToolTip(closeBtn, "Close (Esc)");

        Controls.AddRange([input, caseBtn, wordBtn, prevBtn, nextBtn, closeBtn]);
        Size = new(S(344), S(36));
    }

    internal bool InputFocused => input.Focused;
    internal string Query => input.Text;
    internal void SetQuery(string value) => input.Text = value;
    internal bool CaseSensitive => caseBtn.Active;
    internal bool WholeWord => wordBtn.Active;
    internal int MatchCount => matchCount;
    internal int ActiveMatchIndex => activeMatchIndex;
    internal string CounterText => string.IsNullOrEmpty(input.Text)
        ? ""
        : matchCount <= 0 ? "0 / 0" : $"{Math.Max(1, activeMatchIndex)} / {matchCount}";
    internal bool HasSession => hasActiveSession;

    internal void Open(CoreWebView2? core, CoreWebView2Environment? env)
    {
        if (IsDisposed || core is null || env is null) return;
        if (!ReferenceEquals(boundCore, core))
        {
            Detach(stop: true);
            Attach(core, env);
        }
        else
        {
            boundEnv = env;
        }
        Visible = true;
        BringToFront();
        input.Focus();
        input.SelectAll();
        if (input.Text.Length > 0)
            _ = StartSearchAsync();
    }

    internal void ToggleCaseSensitive()
    {
        caseBtn.SetActive(!caseBtn.Active);
        input.Focus();
        _ = StartSearchAsync(resetSession: true);
    }

    internal void ToggleWholeWord()
    {
        wordBtn.SetActive(!wordBtn.Active);
        input.Focus();
        _ = StartSearchAsync(resetSession: true);
    }

    internal void FindNext()
    {
        if (boundFind is null || !Visible) return;
        if (string.IsNullOrEmpty(input.Text)) return;
        if (!hasActiveSession) { _ = StartSearchAsync(); return; }
        if (matchCount <= 0) return;
        TryFind(() => boundFind.FindNext());
        SyncCountsFromFind();
    }

    internal void FindPrevious()
    {
        if (boundFind is null || !Visible) return;
        if (string.IsNullOrEmpty(input.Text)) return;
        if (!hasActiveSession) { _ = StartSearchAsync(); return; }
        if (matchCount <= 0) return;
        TryFind(() => boundFind.FindPrevious());
        SyncCountsFromFind();
    }

    internal void Close(bool returnFocus)
    {
        var wasVisible = Visible;
        ++searchVersion;
        Detach(stop: true);
        if (wasVisible) Visible = false;
        if (returnFocus && wasVisible) onReturnFocus();
    }

    internal void StopIfBoundTo(CoreWebView2? core)
    {
        if (boundCore is null) return;
        if (core is null || ReferenceEquals(boundCore, core))
            Close(returnFocus: false);
    }

    internal void PositionIn(Control host)
    {
        if (IsDisposed || host.IsDisposed) return;
        var margin = S(12);
        var w = Math.Min(S(344), Math.Max(S(220), host.ClientSize.Width - margin * 2));
        var h = S(36);
        var x = Math.Max(0, host.ClientSize.Width - w - margin);
        SetBounds(x, margin, w, h);
    }

    void Attach(CoreWebView2 core, CoreWebView2Environment env)
    {
        boundCore = core;
        boundEnv = env;
        CoreWebView2Find? find = null;
        TryFind(() => find = core.Find);
        boundFind = find;
        if (boundFind is not null)
        {
            boundFind.MatchCountChanged += OnFindStateChanged;
            boundFind.ActiveMatchIndexChanged += OnFindStateChanged;
        }
    }

    void Detach(bool stop)
    {
        var find = boundFind;
        boundFind = null;
        boundCore = null;
        boundEnv = null;
        hasActiveSession = false;
        matchCount = 0;
        activeMatchIndex = 0;
        if (find is not null)
        {
            try { find.MatchCountChanged -= OnFindStateChanged; } catch { }
            try { find.ActiveMatchIndexChanged -= OnFindStateChanged; } catch { }
            if (stop) TryFind(find.Stop);
        }
        UpdateCountUI();
    }

    void OnFindStateChanged(object? sender, object e)
    {
        if (IsDisposed || !ReferenceEquals(sender, boundFind)) return;
        SyncCountsFromFind();
    }

    internal async Task StartSearchAsync(bool resetSession = false)
    {
        var version = ++searchVersion;
        if (IsDisposed || !Visible || boundFind is null || boundEnv is null)
        {
            hasActiveSession = false;
            matchCount = 0;
            activeMatchIndex = 0;
            UpdateCountUI();
            return;
        }

        var term = input.Text;
        if (string.IsNullOrEmpty(term))
        {
            if (hasActiveSession) TryFind(boundFind.Stop);
            hasActiveSession = false;
            matchCount = 0;
            activeMatchIndex = 0;
            UpdateCountUI();
            return;
        }

        var find = boundFind;
        var env = boundEnv;
        try
        {
            if (resetSession && hasActiveSession)
            {
                TryFind(find.Stop);
                hasActiveSession = false;
            }
            var opts = env.CreateFindOptions();
            opts.FindTerm = term;
            opts.IsCaseSensitive = caseBtn.Active;
            opts.ShouldMatchWord = wordBtn.Active;
            opts.ShouldHighlightAllMatches = true;
            opts.SuppressDefaultFindDialog = true;
            await find.StartAsync(opts);
            if (version != searchVersion || IsDisposed || !Visible || !ReferenceEquals(boundFind, find))
                return;
            hasActiveSession = true;
            SyncCountsFromFind();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            if (version == searchVersion && !IsDisposed)
            {
                hasActiveSession = false;
                matchCount = 0;
                activeMatchIndex = 0;
                UpdateCountUI();
            }
        }
    }

    void SyncCountsFromFind()
    {
        if (boundFind is null || string.IsNullOrEmpty(input.Text))
        {
            matchCount = 0;
            activeMatchIndex = 0;
        }
        else
        {
            TryFind(() =>
            {
                matchCount = boundFind.MatchCount;
                activeMatchIndex = boundFind.ActiveMatchIndex;
            });
        }
        UpdateCountUI();
    }

    void UpdateCountUI()
    {
        if (IsDisposed) return;
        var hasMatches = matchCount > 0;
        prevBtn.Enabled = hasMatches;
        nextBtn.Enabled = hasMatches;
        PerformLayout();
        Invalidate();
    }

    static void TryFind(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
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
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var pad = S(6);
        var gap = S(2);
        var btn = S(24);
        var btnY = (Height - btn) / 2;

        var rx = Width - pad - btn;
        closeBtn.SetBounds(rx, btnY, btn, btn); rx -= btn + gap;
        nextBtn.SetBounds(rx, btnY, btn, btn); rx -= btn + gap;
        prevBtn.SetBounds(rx, btnY, btn, btn); rx -= S(4) + 1 + S(4);

        var sepH = S(16);
        sepRect = new Rectangle(rx + S(4), (Height - sepH) / 2, 1, sepH);

        rx -= btn;
        wordBtn.SetBounds(rx, btnY, btn, btn); rx -= btn + gap;
        caseBtn.SetBounds(rx, btnY, btn, btn); rx -= S(6);

        var boxH = S(24);
        var boxY = (Height - boxH) / 2;
        var boxW = Math.Max(S(60), rx - pad);
        inputBoxRect = new Rectangle(pad, boxY, boxW, boxH);

        var countText = CounterText;
        var countW = string.IsNullOrEmpty(countText)
            ? 0
            : Math.Min(boxW / 2, TextRenderer.MeasureText(countText, Theme.FontForDpi(Theme.HintFont, DeviceDpi), Size.Empty, TextFormatFlags.NoPadding).Width + S(8));
        counterRect = countW > 0
            ? new Rectangle(inputBoxRect.Right - countW - S(4), inputBoxRect.Y, countW, inputBoxRect.Height)
            : Rectangle.Empty;

        var textPad = S(7);
        var inputH = input.PreferredHeight;
        var inputW = Math.Max(0, inputBoxRect.Width - textPad - (countW > 0 ? countW + S(6) : textPad));
        input.SetBounds(inputBoxRect.X + textPad, inputBoxRect.Y + Math.Max(0, (inputBoxRect.Height - inputH) / 2), inputW, inputH);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var border = new Pen(Theme.Hairline, 1f))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        if (inputBoxRect.Width > 0 && inputBoxRect.Height > 0)
        {
            Theme.FillRounded(g, Theme.SideHover, inputBoxRect, Sf(6f));
            if (input.Focused)
                Theme.DrawRounded(g, Theme.Hairline, new RectangleF(inputBoxRect.X + 0.5f, inputBoxRect.Y + 0.5f, inputBoxRect.Width - 1f, inputBoxRect.Height - 1f), Sf(5.5f));
        }

        if (!counterRect.IsEmpty && !string.IsNullOrEmpty(CounterText))
        {
            TextRenderer.DrawText(g, CounterText, Theme.FontForDpi(Theme.HintFont, DeviceDpi), counterRect, Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        if (sepRect.Width > 0 && sepRect.Height > 0)
        {
            using var sepBrush = new SolidBrush(Theme.Hairline);
            g.FillRectangle(sepBrush, sepRect);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Detach(stop: false);
        base.Dispose(disposing);
    }
}
