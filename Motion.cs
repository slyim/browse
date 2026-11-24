using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SearchPort;

static class Motion
{
    // SPI_GETCLIENTAREAANIMATION = 0x1042
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    public const int FrameIntervalMs = 16;
    public const double HoverDurationMs = 120;
    public const double DragSlideDurationMs = 150;
    public const double DropGlideDurationMs = 150;
    public const double OpenCloseDurationMs = 150;
    public const double PinTransitionDurationMs = 140;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    public static bool QuerySystemAnimationsEnabled()
        => !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var enabled, 0) || enabled != 0;

    public static float EaseOutCubic(float t)
    {
        var c = Math.Clamp(t, 0f, 1f);
        var inv = 1f - c;
        return 1f - inv * inv * inv;
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static Color Blend(Color from, Color to, float t)
    {
        var c = Math.Clamp(t, 0f, 1f);
        if (c <= 0f) return from;
        if (c >= 1f) return to;
        return Color.FromArgb(
            Math.Clamp((int)MathF.Round(Lerp(from.A, to.A, c)), 0, 255),
            Math.Clamp((int)MathF.Round(Lerp(from.R, to.R, c)), 0, 255),
            Math.Clamp((int)MathF.Round(Lerp(from.G, to.G, c)), 0, 255),
            Math.Clamp((int)MathF.Round(Lerp(from.B, to.B, c)), 0, 255));
    }

    public static Color WithAlpha(Color color, float alpha)
        => Color.FromArgb(Math.Clamp((int)MathF.Round(color.A * Math.Clamp(alpha, 0f, 1f)), 0, 255), color);
}

struct AnimScalar
{
    public float Value;
    public float Target;
    float from;
    double startMs;
    double durationMs;
    public bool Active;

    public void Snap(float value)
    {
        Value = Target = from = value;
        Active = false;
    }

    public bool SetTarget(float target, MotionClock? clock, double durationMs)
    {
        if (clock is not { Enabled: true } || durationMs <= 0)
        {
            var changed = MathF.Abs(Value - target) > 0.0005f || Active;
            Snap(target);
            return changed;
        }
        if (Active && MathF.Abs(Target - target) <= 0.0005f) return false;
        if (!Active && MathF.Abs(Value - target) <= 0.0005f)
        {
            Value = Target = target;
            return false;
        }
        from = Value;
        Target = target;
        startMs = clock.NowMs();
        this.durationMs = durationMs;
        Active = true;
        return true;
    }

    public bool Start(float startValue, float target, MotionClock? clock, double durationMs)
    {
        Value = from = startValue;
        if (clock is not { Enabled: true } || durationMs <= 0)
        {
            Snap(target);
            return true;
        }
        Target = target;
        startMs = clock.NowMs();
        this.durationMs = durationMs;
        Active = true;
        return true;
    }

    public bool Step(double nowMs)
    {
        if (!Active) return false;
        var elapsed = nowMs - startMs;
        if (elapsed >= durationMs)
        {
            Value = Target;
            Active = false;
            return false;
        }
        var t = elapsed <= 0 ? 0f : (float)(elapsed / durationMs);
        Value = Motion.Lerp(from, Target, Motion.EaseOutCubic(t));
        return true;
    }
}

interface IMotionTarget
{
    bool StepMotion(double nowMs);
    void SnapMotion();
}

sealed class MotionClock : IDisposable
{
    readonly System.Windows.Forms.Timer timer = new() { Interval = Motion.FrameIntervalMs };
    readonly List<IMotionTarget> targets = [];
    readonly List<IMotionTarget> stepScratch = [];
    bool systemEnabled = Motion.QuerySystemAnimationsEnabled();
    bool? forceEnabled;
    double virtualOffsetMs;
    bool disposed;

    public MotionClock()
    {
        timer.Tick += (_, _) => Tick(NowMs());
    }

    public bool SystemEnabled => systemEnabled;
    public bool Enabled => !disposed && (forceEnabled ?? systemEnabled);
    public bool IsRunning => !disposed && timer.Enabled;
    public int ActiveCount => targets.Count;

    public bool? ForceEnabled
    {
        get => forceEnabled;
        set
        {
            forceEnabled = value;
            if (!Enabled) SnapAll();
        }
    }

    public double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency + virtualOffsetMs;

    public void RefreshSystemPreference()
    {
        systemEnabled = Motion.QuerySystemAnimationsEnabled();
        if (!Enabled) SnapAll();
    }

    public void Track(IMotionTarget target)
    {
        if (disposed) return;
        if (!Enabled)
        {
            target.SnapMotion();
            return;
        }
        if (!targets.Contains(target)) targets.Add(target);
        if (!timer.Enabled) timer.Start();
    }

    public void Untrack(IMotionTarget target)
    {
        if (disposed) return;
        targets.Remove(target);
        if (targets.Count == 0 && timer.Enabled) timer.Stop();
    }

    public void Advance(double deltaMs)
    {
        if (disposed) return;
        virtualOffsetMs += deltaMs;
        Tick(NowMs());
    }

    public void SnapAll()
    {
        if (disposed) return;
        if (timer.Enabled) timer.Stop();
        if (targets.Count == 0) return;
        stepScratch.Clear();
        stepScratch.AddRange(targets);
        targets.Clear();
        foreach (var target in stepScratch)
            target.SnapMotion();
        stepScratch.Clear();
    }

    void Tick(double nowMs)
    {
        if (disposed)
        {
            if (timer.Enabled) timer.Stop();
            return;
        }
        if (!Enabled)
        {
            SnapAll();
            return;
        }
        if (targets.Count == 0)
        {
            if (timer.Enabled) timer.Stop();
            return;
        }
        stepScratch.Clear();
        stepScratch.AddRange(targets);
        foreach (var target in stepScratch)
        {
            if (!targets.Contains(target)) continue;
            if (!target.StepMotion(nowMs))
                targets.Remove(target);
        }
        stepScratch.Clear();
        if (targets.Count == 0 && timer.Enabled) timer.Stop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        targets.Clear();
        stepScratch.Clear();
        timer.Stop();
        timer.Dispose();
    }
}

sealed class TabLayoutMotion : IMotionTarget, IDisposable
{
    sealed class OffsetState
    {
        public AnimScalar X;
        public AnimScalar Y;
    }

    internal sealed class ClosingEntry(TabChromeControl control, object? afterTab, bool vertical, int fullSize)
    {
        public TabChromeControl Control { get; } = control;
        public object? AfterTab { get; set; } = afterTab;
        public bool Vertical { get; } = vertical;
        public int FullSize { get; } = fullSize;
        public AnimScalar Scale;
    }

    readonly MotionClock clock;
    readonly Action onListLayoutChanged;
    readonly Dictionary<TabChromeControl, OffsetState> offsets = [];
    readonly Dictionary<TabChromeControl, AnimScalar> openingScales = [];
    readonly List<ClosingEntry> closingEntries = [];
    readonly List<TabChromeControl> scratchControls = [];
    readonly HashSet<Control> scratchParents = [];
    bool disposed;

    public TabLayoutMotion(MotionClock clock, Action onListLayoutChanged)
    {
        this.clock = clock;
        this.onListLayoutChanged = onListLayoutChanged;
    }

    public bool HasListAnimations => openingScales.Count > 0 || closingEntries.Count > 0;
    public int ClosingCount => closingEntries.Count;
    public IReadOnlyList<ClosingEntry> ClosingEntries => closingEntries;

    public Point CurrentOffset(TabChromeControl c) => offsets.TryGetValue(c, out var s)
        ? new Point((int)MathF.Round(s.X.Value), (int)MathF.Round(s.Y.Value))
        : Point.Empty;

    public float GetOpenScale(TabChromeControl c) => openingScales.TryGetValue(c, out var s) ? s.Value : 1f;

    public void SetOffsetTarget(TabChromeControl c, float targetX, float targetY, double durationMs)
    {
        if (disposed || c.IsDisposed) return;
        if (!offsets.TryGetValue(c, out var s))
        {
            if (MathF.Abs(targetX) < 0.5f && MathF.Abs(targetY) < 0.5f) return;
            s = new OffsetState();
            offsets[c] = s;
        }
        var mx = s.X.SetTarget(targetX, clock, durationMs);
        var my = s.Y.SetTarget(targetY, clock, durationMs);
        if (!clock.Enabled)
        {
            ApplyControlOffset(c, s);
            if (MathF.Abs(s.X.Value) < 0.5f && MathF.Abs(s.Y.Value) < 0.5f) offsets.Remove(c);
            return;
        }
        if (mx || my) clock.Track(this);
    }

    public void StartOffsetGlide(TabChromeControl c, float startX, float startY, float targetX, float targetY, double durationMs)
    {
        if (disposed || c.IsDisposed) return;
        if (MathF.Abs(startX - targetX) < 0.5f && MathF.Abs(startY - targetY) < 0.5f && MathF.Abs(targetX) < 0.5f && MathF.Abs(targetY) < 0.5f)
        {
            offsets.Remove(c);
            c.Location = c.BaseBounds.Location;
            return;
        }
        if (!offsets.TryGetValue(c, out var s))
        {
            s = new OffsetState();
            offsets[c] = s;
        }
        s.X.Start(startX, targetX, clock, durationMs);
        s.Y.Start(startY, targetY, clock, durationMs);
        ApplyControlOffset(c, s);
        if (!clock.Enabled)
        {
            if (MathF.Abs(s.X.Value) < 0.5f && MathF.Abs(s.Y.Value) < 0.5f) offsets.Remove(c);
            return;
        }
        clock.Track(this);
    }

    public void ReturnAllOffsetsToZero(double durationMs, bool animate = true)
    {
        if (disposed || offsets.Count == 0) return;
        if (!animate || !clock.Enabled)
        {
            SnapOffsetsToZero();
            return;
        }
        var any = false;
        scratchControls.Clear();
        scratchControls.AddRange(offsets.Keys);
        foreach (var c in scratchControls)
        {
            if (c.IsDisposed) { offsets.Remove(c); continue; }
            var s = offsets[c];
            if (s.X.SetTarget(0f, clock, durationMs) | s.Y.SetTarget(0f, clock, durationMs))
                any = true;
        }
        scratchControls.Clear();
        if (any) clock.Track(this);
    }

    public void SnapOffsetsToZero()
    {
        if (offsets.Count == 0) return;
        scratchParents.Clear();
        foreach (var c in offsets.Keys)
            if (!c.IsDisposed && c.Parent is { IsDisposed: false } p && scratchParents.Add(p))
                p.SuspendLayout();
        foreach (var c in offsets.Keys)
            if (!c.IsDisposed) c.Location = c.BaseBounds.Location;
        foreach (var p in scratchParents)
            p.ResumeLayout(false);
        scratchParents.Clear();
        offsets.Clear();
        CheckIdle();
    }

    public void RemoveControl(TabChromeControl c)
    {
        offsets.Remove(c);
        openingScales.Remove(c);
        CheckIdle();
    }

    public void StartOpen(TabChromeControl c)
    {
        if (disposed || c.IsDisposed || !clock.Enabled) return;
        var scale = new AnimScalar();
        scale.Start(0f, 1f, clock, Motion.OpenCloseDurationMs);
        openingScales[c] = scale;
        c.SetAppearOpacity(0f);
        clock.Track(this);
    }

    public bool StartClose(TabChromeControl c, object? closingTab, object? afterTab, bool vertical, int fullSize)
    {
        if (disposed || c.IsDisposed || !clock.Enabled) return false;
        offsets.Remove(c);
        openingScales.Remove(c);
        // Re-anchor any already-shrinking entries that were parked immediately after this tab.
        foreach (var existing in closingEntries)
            if (ReferenceEquals(existing.AfterTab, closingTab))
                existing.AfterTab = afterTab;

        c.MarkClosing();
        c.CancelDrag();
        var entry = new ClosingEntry(c, afterTab, vertical, Math.Max(1, fullSize));
        entry.Scale.Start(1f, 0f, clock, Motion.OpenCloseDurationMs);
        closingEntries.Add(entry);
        clock.Track(this);
        return true;
    }

    public void RedirectClosingAnchor(object removedTab, object? newAfterTab)
    {
        foreach (var existing in closingEntries)
            if (ReferenceEquals(existing.AfterTab, removedTab))
                existing.AfterTab = newAfterTab;
    }

    public void FinishClosing()
    {
        if (closingEntries.Count == 0) return;
        var hadAny = true;
        foreach (var entry in closingEntries)
            DisposeClosingControl(entry.Control);
        closingEntries.Clear();
        CheckIdle();
        if (hadAny && !disposed) onListLayoutChanged();
    }

    public void Reset()
    {
        foreach (var entry in closingEntries)
            DisposeClosingControl(entry.Control);
        closingEntries.Clear();
        foreach (var c in openingScales.Keys)
            if (!c.IsDisposed) c.SetAppearOpacity(1f);
        openingScales.Clear();
        offsets.Clear();
        clock.Untrack(this);
    }

    public bool StepMotion(double nowMs)
    {
        if (disposed) return false;
        var listChanged = false;

        if (openingScales.Count > 0)
        {
            scratchControls.Clear();
            scratchControls.AddRange(openingScales.Keys);
            foreach (var c in scratchControls)
            {
                if (c.IsDisposed) { openingScales.Remove(c); listChanged = true; continue; }
                var scale = openingScales[c];
                var still = scale.Step(nowMs);
                listChanged = true;
                if (!still)
                {
                    openingScales.Remove(c);
                    c.SetAppearOpacity(1f);
                }
                else
                {
                    openingScales[c] = scale;
                    c.SetAppearOpacity(scale.Value);
                }
            }
            scratchControls.Clear();
        }

        if (closingEntries.Count > 0)
        {
            for (var i = closingEntries.Count - 1; i >= 0; i--)
            {
                var entry = closingEntries[i];
                if (entry.Control.IsDisposed)
                {
                    closingEntries.RemoveAt(i);
                    listChanged = true;
                    continue;
                }
                var still = entry.Scale.Step(nowMs);
                listChanged = true;
                if (!still || entry.Scale.Value <= 0.001f)
                {
                    closingEntries.RemoveAt(i);
                    DisposeClosingControl(entry.Control);
                }
                else
                {
                    entry.Control.SetAppearOpacity(entry.Scale.Value);
                }
            }
        }

        if (listChanged) onListLayoutChanged();

        var anyOffsetActive = false;
        if (offsets.Count > 0)
        {
            scratchControls.Clear();
            scratchControls.AddRange(offsets.Keys);
            scratchParents.Clear();
            foreach (var c in scratchControls)
                if (!c.IsDisposed && c.Parent is { IsDisposed: false } p && scratchParents.Add(p))
                    p.SuspendLayout();
            foreach (var c in scratchControls)
            {
                if (c.IsDisposed) { offsets.Remove(c); continue; }
                var s = offsets[c];
                var ax = s.X.Step(nowMs);
                var ay = s.Y.Step(nowMs);
                ApplyControlOffset(c, s);
                if (ax || ay)
                {
                    anyOffsetActive = true;
                }
                else if (MathF.Abs(s.X.Value) < 0.5f && MathF.Abs(s.Y.Value) < 0.5f)
                {
                    c.Location = c.BaseBounds.Location;
                    offsets.Remove(c);
                }
            }
            foreach (var p in scratchParents)
                p.ResumeLayout(false);
            scratchParents.Clear();
            scratchControls.Clear();
        }

        return openingScales.Count > 0 || closingEntries.Count > 0 || anyOffsetActive;
    }

    public void SnapMotion()
    {
        if (disposed) return;
        var listChanged = openingScales.Count > 0 || closingEntries.Count > 0;
        foreach (var c in openingScales.Keys)
            if (!c.IsDisposed) c.SetAppearOpacity(1f);
        openingScales.Clear();

        foreach (var entry in closingEntries)
            DisposeClosingControl(entry.Control);
        closingEntries.Clear();

        if (offsets.Count > 0)
        {
            scratchControls.Clear();
            scratchControls.AddRange(offsets.Keys);
            scratchParents.Clear();
            foreach (var c in scratchControls)
                if (!c.IsDisposed && c.Parent is { IsDisposed: false } p && scratchParents.Add(p))
                    p.SuspendLayout();
            foreach (var c in scratchControls)
            {
                if (c.IsDisposed) { offsets.Remove(c); continue; }
                var s = offsets[c];
                s.X.Snap(s.X.Target);
                s.Y.Snap(s.Y.Target);
                ApplyControlOffset(c, s);
                if (MathF.Abs(s.X.Value) < 0.5f && MathF.Abs(s.Y.Value) < 0.5f)
                    offsets.Remove(c);
            }
            foreach (var p in scratchParents)
                p.ResumeLayout(false);
            scratchParents.Clear();
            scratchControls.Clear();
        }

        if (listChanged) onListLayoutChanged();
    }

    void CheckIdle()
    {
        if (openingScales.Count > 0 || closingEntries.Count > 0) return;
        foreach (var s in offsets.Values)
            if (s.X.Active || s.Y.Active) return;
        clock.Untrack(this);
    }

    static void ApplyControlOffset(TabChromeControl c, OffsetState s)
    {
        var nx = c.BaseBounds.X + (int)MathF.Round(s.X.Value);
        var ny = c.BaseBounds.Y + (int)MathF.Round(s.Y.Value);
        if (c.Left != nx || c.Top != ny)
            c.Location = new Point(nx, ny);
    }

    static void DisposeClosingControl(TabChromeControl c)
    {
        if (c.IsDisposed) return;
        c.Parent?.Controls.Remove(c);
        c.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Reset();
    }
}
