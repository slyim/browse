namespace SearchPort;

readonly record struct SavedWindowPlacement(
    FormBorderStyle BorderStyle,
    FormWindowState WindowState,
    Rectangle NormalBounds,
    bool ChromeHidden);

enum FullscreenTransition { None, Enter, Exit }

// Tracks F11 browser fullscreen and HTML5 element fullscreen independently so leaving element fullscreen returns to F11 when active.
sealed class FullscreenController<TTab> where TTab : class
{
    public bool BrowserFullscreen { get; private set; }
    public TTab? ElementFullscreenTab { get; private set; }
    public bool ElementFullscreen => ElementFullscreenTab is not null;
    public bool IsFullscreen => BrowserFullscreen || ElementFullscreen;
    public SavedWindowPlacement? SavedPlacement { get; private set; }

    public FullscreenTransition ToggleBrowserFullscreen()
    {
        var was = IsFullscreen;
        BrowserFullscreen = !BrowserFullscreen;
        return Transition(was, IsFullscreen);
    }

    public FullscreenTransition SetElementFullscreen(TTab tab, bool active)
    {
        var was = IsFullscreen;
        if (active)
            ElementFullscreenTab = tab;
        else if (ReferenceEquals(ElementFullscreenTab, tab))
            ElementFullscreenTab = null;
        return Transition(was, IsFullscreen);
    }

    public FullscreenTransition ClearElementFullscreen(TTab tab)
    {
        if (!ReferenceEquals(ElementFullscreenTab, tab)) return FullscreenTransition.None;
        var was = IsFullscreen;
        ElementFullscreenTab = null;
        return Transition(was, IsFullscreen);
    }

    public void UpdateSavedChromeHidden(bool hidden)
    {
        if (SavedPlacement is { } saved)
            SavedPlacement = saved with { ChromeHidden = hidden };
    }

    public void CapturePlacement(Form form, bool chromeHidden)
    {
        if (SavedPlacement is not null) return;
        var normalBounds = form.WindowState == FormWindowState.Normal
            ? form.Bounds
            : form.RestoreBounds is { Width: > 0, Height: > 0 } rb ? rb : form.Bounds;
        SavedPlacement = new SavedWindowPlacement(form.FormBorderStyle, form.WindowState, normalBounds, chromeHidden);
    }

    public SavedWindowPlacement? TakeSavedPlacement()
    {
        var saved = SavedPlacement;
        SavedPlacement = null;
        return saved;
    }

    static FullscreenTransition Transition(bool was, bool now) =>
        !was && now ? FullscreenTransition.Enter
        : was && !now ? FullscreenTransition.Exit
        : FullscreenTransition.None;
}
