using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace SearchPort;

enum DownloadItemState { InProgress, Completed, Interrupted }

sealed record DownloadRecord(
    string Id,
    string Name,
    string Path,
    string Url,
    long Size,
    DownloadItemState State,
    DateTimeOffset Time,
    string? InterruptReason = null);

static class DownloadStore
{
    public const int MaxHistory = 100;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Missing, empty, unreadable, or corrupt -> empty list, never throws.
    public static DownloadRecord[] Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return [];
            var parsed = JsonSerializer.Deserialize<DownloadRecord[]>(text, Json);
            if (parsed is null || parsed.Length == 0) return [];
            var list = new List<DownloadRecord>(Math.Min(parsed.Length, MaxHistory));
            foreach (var r in parsed)
            {
                if (r is null || string.IsNullOrWhiteSpace(r.Name) || r.State == DownloadItemState.InProgress)
                    continue;
                var id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N") : r.Id;
                list.Add(r with
                {
                    Id = id,
                    Path = r.Path ?? "",
                    Url = r.Url ?? "",
                    Size = Math.Max(0, r.Size),
                });
                if (list.Count >= MaxHistory) break;
            }
            return [.. list];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(string path, IEnumerable<DownloadRecord> records)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var capped = records
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name) && r.State != DownloadItemState.InProgress)
            .Take(MaxHistory)
            .ToArray();
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(capped, Json));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    public static bool SelfCheck(string tempDir)
    {
        var file = Path.Combine(tempDir, $"downloads-selfcheck-{Guid.NewGuid():N}.json");
        try
        {
            if (Load(file).Length != 0) return false;

            File.WriteAllText(file, "{not valid json");
            if (Load(file).Length != 0) return false;

            File.WriteAllText(file, "   ");
            if (Load(file).Length != 0) return false;

            var now = DateTimeOffset.UtcNow;
            var items = Enumerable.Range(0, MaxHistory + 20)
                .Select(i => new DownloadRecord(
                    $"id-{i}",
                    $"file-{i}.txt",
                    $@"C:\Downloads\file-{i}.txt",
                    $"https://example.com/file-{i}.txt",
                    1024 + i,
                    i % 2 == 0 ? DownloadItemState.Completed : DownloadItemState.Interrupted,
                    now.AddMinutes(-i),
                    i % 2 == 0 ? null : "Network error"))
                .ToArray();
            Save(file, items);
            var loaded = Load(file);
            if (loaded.Length != MaxHistory) return false;
            if (loaded[0].Id != "id-0" || loaded[0].State != DownloadItemState.Completed || loaded[0].Size != 1024)
                return false;
            if (loaded[1].Id != "id-1" || loaded[1].State != DownloadItemState.Interrupted || loaded[1].InterruptReason != "Network error")
                return false;
            return !Directory.EnumerateFiles(tempDir, Path.GetFileName(file) + ".*.tmp").Any();
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }
}

sealed class DownloadEntry
{
    public string Id { get; }
    public string Name { get; set; }
    public string FilePath { get; set; }
    public string Url { get; set; }
    public string Host { get; set; }
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public long Size { get; set; }
    public DateTimeOffset Time { get; set; }
    public DateTime? EstimatedEndTime { get; set; }
    public DownloadItemState State { get; set; }
    public CoreWebView2DownloadInterruptReason InterruptReason { get; set; }
    public string? InterruptText { get; set; }
    public bool CanResume { get; set; }
    internal CoreWebView2DownloadOperation? Operation { get; set; }

    public bool IsPaused => Operation is not null
        && State == DownloadItemState.Interrupted
        && InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused;

    public bool IsActive => State == DownloadItemState.InProgress || IsPaused;
    public bool IsFinished => !IsActive;

    public DownloadEntry(string id, string name, string filePath, string url, DateTimeOffset time)
    {
        Id = id;
        Name = name;
        FilePath = filePath;
        Url = url;
        Host = HostOf(url);
        Time = time;
        State = DownloadItemState.InProgress;
    }

    public static DownloadEntry FromRecord(DownloadRecord r) => new(r.Id, r.Name, r.Path, r.Url, r.Time)
    {
        Size = r.Size,
        BytesReceived = r.Size,
        TotalBytes = r.Size > 0 ? r.Size : null,
        State = r.State,
        InterruptText = r.InterruptReason,
    };

    public DownloadRecord ToRecord()
    {
        var size = Size > 0 ? Size : Math.Max(BytesReceived, TotalBytes ?? 0);
        return new DownloadRecord(
            Id,
            Name,
            FilePath,
            Url,
            Math.Max(0, size),
            State,
            Time,
            State == DownloadItemState.Interrupted ? (InterruptText ?? HumanInterruptReason(InterruptReason)) : null);
    }

    public bool FileExists() => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

    public static string HostOf(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IdnHost.Length > 0
            ? u.IdnHost
            : "";

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes / 1024.0;
        if (value < 1024.0) return $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} KB";
        value /= 1024.0;
        if (value < 1024.0) return $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} MB";
        value /= 1024.0;
        return $"{value.ToString("0.00", CultureInfo.InvariantCulture)} GB";
    }

    public static string? FormatEta(DateTime? estimatedEndTime)
    {
        if (estimatedEndTime is not { } end || end == DateTime.MinValue) return null;
        var now = end.Kind == DateTimeKind.Utc ? DateTime.UtcNow : DateTime.Now;
        var remaining = end - now;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromDays(1)) return null;
        var totalSec = (int)Math.Ceiling(remaining.TotalSeconds);
        if (totalSec < 60) return $"{totalSec}s left";
        var mins = totalSec / 60;
        var secs = totalSec % 60;
        if (mins < 60) return secs > 0 ? $"{mins}m {secs}s left" : $"{mins}m left";
        var hours = mins / 60;
        mins %= 60;
        return mins > 0 ? $"{hours}h {mins}m left" : $"{hours}h left";
    }

    public static string HumanInterruptReason(CoreWebView2DownloadInterruptReason reason) => reason switch
    {
        CoreWebView2DownloadInterruptReason.None => "Interrupted",
        CoreWebView2DownloadInterruptReason.FileFailed => "File error",
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "Access denied",
        CoreWebView2DownloadInterruptReason.FileNoSpace => "Insufficient disk space",
        CoreWebView2DownloadInterruptReason.FileNameTooLong => "File name too long",
        CoreWebView2DownloadInterruptReason.FileTooLarge => "File too large",
        CoreWebView2DownloadInterruptReason.FileMalicious => "Blocked as malicious",
        CoreWebView2DownloadInterruptReason.FileTransientError => "File unavailable",
        CoreWebView2DownloadInterruptReason.FileBlockedByPolicy => "Blocked by policy",
        CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed => "Security check failed",
        CoreWebView2DownloadInterruptReason.FileTooShort => "File incomplete",
        CoreWebView2DownloadInterruptReason.FileHashMismatch => "Verification failed",
        CoreWebView2DownloadInterruptReason.NetworkFailed => "Network error",
        CoreWebView2DownloadInterruptReason.NetworkTimeout => "Network timed out",
        CoreWebView2DownloadInterruptReason.NetworkDisconnected => "Network disconnected",
        CoreWebView2DownloadInterruptReason.NetworkServerDown => "Server unreachable",
        CoreWebView2DownloadInterruptReason.NetworkInvalidRequest => "Invalid request",
        CoreWebView2DownloadInterruptReason.ServerFailed => "Server error",
        CoreWebView2DownloadInterruptReason.ServerNoRange => "Resume not supported",
        CoreWebView2DownloadInterruptReason.ServerBadContent => "File unavailable",
        CoreWebView2DownloadInterruptReason.ServerUnauthorized => "Authorization required",
        CoreWebView2DownloadInterruptReason.ServerCertificateProblem => "Certificate error",
        CoreWebView2DownloadInterruptReason.ServerForbidden => "Access forbidden",
        CoreWebView2DownloadInterruptReason.ServerUnexpectedResponse => "Unexpected server response",
        CoreWebView2DownloadInterruptReason.ServerContentLengthMismatch => "Size mismatch",
        CoreWebView2DownloadInterruptReason.ServerCrossOriginRedirect => "Redirect error",
        CoreWebView2DownloadInterruptReason.UserCanceled => "Canceled",
        CoreWebView2DownloadInterruptReason.UserShutdown => "Canceled on exit",
        CoreWebView2DownloadInterruptReason.UserPaused => "Paused",
        CoreWebView2DownloadInterruptReason.DownloadProcessCrashed => "Download process crashed",
        _ => "Interrupted",
    };
}

sealed class DownloadManager : IDisposable
{
    readonly string historyFile;
    readonly List<DownloadEntry> entries = [];
    readonly HashSet<string> dropped = []; // ids removed here, so Persist doesn't re-add them from disk
    readonly Dictionary<string, Action> detachHandlers = [];
    long lastProgressEmitTick;
    bool disposed;

    public event Action? Changed;

    public DownloadManager(string historyFile)
    {
        this.historyFile = historyFile;
        foreach (var record in DownloadStore.Load(historyFile))
            entries.Add(DownloadEntry.FromRecord(record));
    }

    public string HistoryFile => historyFile;
    public IReadOnlyList<DownloadEntry> Entries => entries;
    public int Count => entries.Count;
    public int ActiveCount => entries.Count(e => e.IsActive);
    public bool HasFinished => entries.Any(e => e.IsFinished);

    public bool HasIndeterminateActive =>
        entries.Any(e => e.State == DownloadItemState.InProgress && (e.TotalBytes is null || e.TotalBytes <= 0));

    public double? OverallProgress
    {
        get
        {
            long received = 0;
            long total = 0;
            var any = false;
            foreach (var e in entries)
            {
                if (e.State != DownloadItemState.InProgress) continue;
                any = true;
                if (e.TotalBytes is not > 0) return null;
                received += Math.Max(0, e.BytesReceived);
                total += e.TotalBytes.Value;
            }
            if (!any || total <= 0) return null;
            return Math.Clamp(received / (double)total, 0.0, 1.0);
        }
    }

    public DownloadEntry Track(CoreWebView2DownloadOperation op, string? initialFilePath)
    {
        var path = !string.IsNullOrWhiteSpace(initialFilePath)
            ? initialFilePath
            : TryGet(() => op.ResultFilePath, "");
        var url = TryGet(() => op.Uri, "");
        var name = DeriveName(path, url);
        var entry = new DownloadEntry(Guid.NewGuid().ToString("N"), name, path, url, DateTimeOffset.UtcNow)
        {
            Operation = op,
        };

        void OnBytes(object? _, object __)
        {
            if (disposed || !ReferenceEquals(entry.Operation, op)) return;
            SyncOperation(entry, op);
            var now = Environment.TickCount64;
            if (now - lastProgressEmitTick >= 80)
            {
                lastProgressEmitTick = now;
                Changed?.Invoke();
            }
        }

        void OnEta(object? _, object __)
        {
            if (disposed || !ReferenceEquals(entry.Operation, op)) return;
            entry.EstimatedEndTime = TryGet<DateTime?>(() => op.EstimatedEndTime, null);
            var now = Environment.TickCount64;
            if (now - lastProgressEmitTick >= 200)
            {
                lastProgressEmitTick = now;
                Changed?.Invoke();
            }
        }

        void OnState(object? _, object __)
        {
            if (disposed || !ReferenceEquals(entry.Operation, op)) return;
            SyncOperation(entry, op);
            FinalizeIfDone(entry, op);
            Changed?.Invoke();
        }

        op.BytesReceivedChanged += OnBytes;
        op.EstimatedEndTimeChanged += OnEta;
        op.StateChanged += OnState;
        detachHandlers[entry.Id] = () =>
        {
            TryDo(() => op.BytesReceivedChanged -= OnBytes);
            TryDo(() => op.EstimatedEndTimeChanged -= OnEta);
            TryDo(() => op.StateChanged -= OnState);
        };

        entries.Insert(0, entry);
        SyncOperation(entry, op);
        FinalizeIfDone(entry, op);
        Changed?.Invoke();
        return entry;
    }

    void SyncOperation(DownloadEntry entry, CoreWebView2DownloadOperation op)
    {
        var latestPath = TryGet(() => op.ResultFilePath, "");
        if (!string.IsNullOrWhiteSpace(latestPath) && latestPath != entry.FilePath)
        {
            entry.FilePath = latestPath;
            entry.Name = DeriveName(latestPath, entry.Url);
        }
        entry.BytesReceived = Math.Max(0, TryGet(() => op.BytesReceived, entry.BytesReceived));
        var rawTotal = TryGet<ulong?>(() => op.TotalBytesToReceive, null);
        entry.TotalBytes = rawTotal is > 0 and <= long.MaxValue ? (long)rawTotal.Value : entry.TotalBytes;
        entry.EstimatedEndTime = TryGet<DateTime?>(() => op.EstimatedEndTime, null);
        entry.CanResume = TryGet(() => op.CanResume, false);
        entry.InterruptReason = TryGet(() => op.InterruptReason, CoreWebView2DownloadInterruptReason.None);
        var state = TryGet(() => op.State, CoreWebView2DownloadState.InProgress);
        entry.State = state switch
        {
            CoreWebView2DownloadState.Completed => DownloadItemState.Completed,
            CoreWebView2DownloadState.Interrupted => DownloadItemState.Interrupted,
            _ => DownloadItemState.InProgress,
        };
        if (entry.State == DownloadItemState.Completed)
        {
            long diskSize = 0;
            try { if (entry.FileExists()) diskSize = new FileInfo(entry.FilePath).Length; } catch { }
            entry.Size = Math.Max(diskSize, Math.Max(entry.BytesReceived, entry.TotalBytes ?? 0));
            entry.InterruptText = null;
        }
        else if (entry.State == DownloadItemState.Interrupted)
        {
            entry.InterruptText = DownloadEntry.HumanInterruptReason(entry.InterruptReason);
        }
    }

    void FinalizeIfDone(DownloadEntry entry, CoreWebView2DownloadOperation op)
    {
        if (entry.State == DownloadItemState.Completed)
        {
            Detach(entry);
            entry.Operation = null;
            entry.CanResume = false;
            TrimHistory();
            Persist();
        }
        else if (entry.State == DownloadItemState.Interrupted && !entry.IsPaused)
        {
            // Keep live op attached only while WebView2 says it can resume; still persist the interrupted entry.
            if (!entry.CanResume)
            {
                Detach(entry);
                entry.Operation = null;
            }
            TrimHistory();
            Persist();
        }
    }

    void Detach(DownloadEntry entry)
    {
        if (detachHandlers.Remove(entry.Id, out var detach))
            detach();
    }

    void TrimHistory()
    {
        var finished = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsFinished)
            {
                finished++;
                if (finished > DownloadStore.MaxHistory)
                {
                    Detach(entries[i]);
                    entries[i].Operation = null;
                    dropped.Add(entries[i].Id);
                    entries.RemoveAt(i);
                    i--;
                }
            }
        }
    }

    void Persist()
    {
        if (disposed) return;
        try
        {
            // Other windows (separate processes) share this file: keep their records, newest first.
            var mine = entries.Where(e => e.IsFinished).Select(e => e.ToRecord()).ToList();
            var known = entries.Select(e => e.Id).Concat(dropped).ToHashSet();
            var theirs = DownloadStore.Load(historyFile).Where(r => !known.Contains(r.Id));
            DownloadStore.Save(historyFile, mine.Concat(theirs).OrderByDescending(r => r.Time));
        }
        catch { }
    }

    public void Pause(string id)
    {
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry?.Operation is not { } op) return;
        TryDo(() => op.Pause());
    }

    public void Resume(string id)
    {
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry?.Operation is not { } op) return;
        if (!TryGet(() => op.CanResume, false)) return;
        TryDo(() => op.Resume());
    }

    public void Cancel(string id)
    {
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry?.Operation is not { } op) return;
        TryDo(() => op.Cancel());
    }

    public bool Remove(string id)
    {
        var idx = entries.FindIndex(e => e.Id == id);
        if (idx < 0) return false;
        var entry = entries[idx];
        if (entry.IsActive && entry.Operation is { } op)
            TryDo(() => op.Cancel());
        Detach(entry);
        entry.Operation = null;
        dropped.Add(entry.Id);
        entries.RemoveAt(idx);
        Persist();
        Changed?.Invoke();
        return true;
    }

    public void ClearFinished()
    {
        var removed = false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (!entries[i].IsFinished) continue;
            Detach(entries[i]);
            entries[i].Operation = null;
            dropped.Add(entries[i].Id);
            entries.RemoveAt(i);
            removed = true;
        }
        if (!removed) return;
        Persist();
        Changed?.Invoke();
    }

    public void CancelActive()
    {
        foreach (var entry in entries.ToArray())
        {
            if (entry.Operation is not { } op) continue;
            if (entry.IsActive) TryDo(() => op.Cancel());
            Detach(entry);
            entry.Operation = null;
        }
    }

    public static bool OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    public static bool ShowInFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            if (full.Contains('"')) return false;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    static string DeriveName(string? filePath, string? url)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var fileName = Path.GetFileName(filePath);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var seg = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments[^1].Trim('/')) : "";
            if (!string.IsNullOrWhiteSpace(seg)) return seg;
        }
        return "download";
    }

    static void TryDo(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }

    static T TryGet<T>(Func<T> func, T fallback)
    {
        try { return func(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { return fallback; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var detach in detachHandlers.Values) detach();
        detachHandlers.Clear();
    }
}

static class DownloadGlyphs
{
    public static readonly string Download = Theme.Glyphs.Update;                                      // download-simple
    public static readonly string Pause = "\uE769";                  // pause
    public static readonly string Play = "\uE768";                   // play
    public static readonly string Folder = "\uE838";                 // folder-open
    public static readonly string OpenFile = "\uE8A7";               // arrow-square-out
    public static readonly string Retry = Theme.Glyphs.Reload;                                         // arrow-clockwise
    public static readonly string Close = Theme.Glyphs.Close;                                          // x
}

sealed class DownloadsButton : ChromeControl
{
    readonly Action pressed;
    readonly System.Windows.Forms.Timer pulseTimer = new() { Interval = 60 };
    Color hoverColor = Theme.SideHover;
    Color activeColor = Theme.SideActive;
    bool hovered;
    bool active;
    bool hasActiveDownloads;
    bool indeterminate;
    double? progress;
    int spinAngle;

    internal DownloadsButton(Action pressed)
    {
        this.pressed = pressed;
        AccessibleName = "Downloads";
        AccessibleRole = AccessibleRole.PushButton;
        BackColor = Theme.Frame;
        Size = new(S(26), S(26));
        Cursor = Cursors.Hand;
        Visible = false;
        pulseTimer.Tick += (_, _) =>
        {
            if (IsDisposed || !Visible || !hasActiveDownloads || !indeterminate)
            {
                pulseTimer.Stop();
                return;
            }
            spinAngle = (spinAngle + 24) % 360;
            Invalidate();
        };
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

    internal void SetProgress(double? fraction, bool activeDownloads, bool isIndeterminate)
    {
        var changed = progress != fraction || hasActiveDownloads != activeDownloads || indeterminate != isIndeterminate;
        progress = fraction;
        hasActiveDownloads = activeDownloads;
        indeterminate = isIndeterminate;
        SyncTimer();
        if (changed) Invalidate();
    }

    void SyncTimer()
    {
        if (Visible && hasActiveDownloads && indeterminate && !IsDisposed)
        {
            if (!pulseTimer.Enabled) pulseTimer.Start();
        }
        else if (pulseTimer.Enabled)
        {
            pulseTimer.Stop();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        SyncTimer();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
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
        if (active || hovered)
            Theme.FillRounded(g, active ? activeColor : hoverColor, new RectangleF(0, 0, Width, Height), Sf(8f));

        if (hasActiveDownloads)
        {
            var stroke = Math.Max(1.6f, Sf(1.8f));
            var inset = Sf(2.5f);
            var ring = new RectangleF(inset, inset, Math.Max(4f, Width - inset * 2f), Math.Max(4f, Height - inset * 2f));
            using var trackPen = new Pen(Theme.Hairline, stroke);
            g.DrawEllipse(trackPen, ring);
            using var arcPen = new Pen(Theme.DropLine, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (progress is { } frac)
                g.DrawArc(arcPen, ring, -90f, Math.Max(12f, (float)(360.0 * Math.Clamp(frac, 0.0, 1.0))));
            else
                g.DrawArc(arcPen, ring, spinAngle, 110f);

            TextRenderer.DrawText(g, DownloadGlyphs.Download, Theme.FontForDpi(Theme.SmallIconFont, DeviceDpi), ClientRectangle, Theme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return;
        }

        var color = active ? Theme.Ink : hovered ? Theme.InkSoft : Theme.Muted;
        TextRenderer.DrawText(g, DownloadGlyphs.Download, Theme.FontForDpi(Theme.IconFont, DeviceDpi), ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pulseTimer.Stop();
            pulseTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

sealed class DownloadsFlyout : Form
{
    const int CS_DROPSHADOW = 0x00020000;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    static readonly Color ErrorInk = Theme.Dark ? Color.FromArgb(0xF8, 0x71, 0x71) : Color.FromArgb(0xB9, 0x1C, 0x1C);

    readonly DownloadManager manager;
    readonly Action<string> onRetry;
    readonly Action<bool> onClosed;
    readonly Control? anchorControl;
    readonly Panel headerPanel;
    readonly Label titleLabel;
    readonly ClearFinishedButton clearBtn;
    readonly DownloadListPanel listPanel;
    readonly Label emptyLabel;
    readonly ToolTip tips = new();
    readonly System.Windows.Forms.Timer animTimer = new() { Interval = 60 };
    readonly List<DownloadRowControl> rows = [];
    int animPhase;
    bool closedByDeactivate;

    internal DownloadsFlyout(DownloadManager manager, Action<string> onRetry, Action<bool> onClosed, Control? anchorControl = null)
    {
        this.manager = manager;
        this.onRetry = onRetry;
        this.onClosed = onClosed;
        this.anchorControl = anchorControl;

        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        KeyPreview = true;
        BackColor = Theme.Ground;
        ForeColor = Theme.Ink;
        AccessibleName = "Downloads";
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);

        headerPanel = new Panel { BackColor = Theme.Ground };
        titleLabel = new Label
        {
            Text = "Downloads",
            Font = Theme.FontForDpi(Theme.SectionFont, DeviceDpi),
            ForeColor = Theme.Ink,
            BackColor = Theme.Ground,
            AutoSize = true,
            AccessibleName = "Downloads",
        };
        clearBtn = new ClearFinishedButton(() => manager.ClearFinished());
        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(clearBtn);

        listPanel = new DownloadListPanel { BackColor = Theme.Ground };
        emptyLabel = new Label
        {
            Text = "No downloads yet",
            Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi),
            ForeColor = Theme.InkSoft,
            BackColor = Theme.Ground,
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
            AccessibleName = "No downloads yet",
        };

        Controls.Add(headerPanel);
        Controls.Add(listPanel);
        Controls.Add(emptyLabel);

        animTimer.Tick += (_, _) =>
        {
            if (IsDisposed || !Visible || !manager.HasIndeterminateActive)
            {
                animTimer.Stop();
                return;
            }
            animPhase = (animPhase + 5) % 100;
            foreach (var r in rows)
                if (r.IsIndeterminate) r.SetAnimPhase(animPhase);
        };

        Deactivate += (_, _) =>
        {
            if (IsDisposed || !Visible) return;
            closedByDeactivate = true;
            Close();
        };
        FormClosed += (_, _) => onClosed(closedByDeactivate);

        RefreshEntries();
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
        titleLabel.Font = Theme.FontForDpi(Theme.SectionFont, DeviceDpi);
        emptyLabel.Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
        LayoutAndSize();
        Invalidate(true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or (Keys.Control | Keys.J))
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
        var headerBottom = headerPanel.Bottom;
        e.Graphics.DrawLine(pen, 1, headerBottom, Width - 2, headerBottom);
    }

    internal void RefreshEntries()
    {
        if (IsDisposed) return;
        var items = manager.Entries;
        clearBtn.Visible = manager.HasFinished;

        while (rows.Count > items.Count)
        {
            var last = rows[^1];
            rows.RemoveAt(rows.Count - 1);
            tips.SetToolTip(last, null);
            last.Dispose();
        }
        for (var i = 0; i < items.Count; i++)
        {
            var entry = items[i];
            if (i < rows.Count)
            {
                rows[i].Sync(entry, animPhase);
            }
            else
            {
                var row = new DownloadRowControl(
                    entry,
                    animPhase,
                    tips,
                    onPause: id => manager.Pause(id),
                    onResume: id => manager.Resume(id),
                    onCancel: id => manager.Cancel(id),
                    onOpen: path => { if (DownloadManager.OpenFile(path) && !IsDisposed) Close(); },
                    onShowFolder: path => DownloadManager.ShowInFolder(path),
                    onRetry: url => { onRetry(url); if (!IsDisposed) Close(); },
                    onRemove: id => manager.Remove(id));
                rows.Add(row);
            }
        }

        if (manager.HasIndeterminateActive)
        {
            if (!animTimer.Enabled) animTimer.Start();
        }
        else if (animTimer.Enabled)
        {
            animTimer.Stop();
        }

        LayoutAndSize();
    }

    void LayoutAndSize()
    {
        var width = S(356);
        var border = 1;
        var innerW = width - border * 2;
        var headerH = S(38);
        var rowH = S(62);
        var rowGap = S(2);
        var listPad = S(6);

        headerPanel.SetBounds(border, border, innerW, headerH);
        titleLabel.Font = Theme.FontForDpi(Theme.SectionFont, DeviceDpi);
        var titleSize = titleLabel.GetPreferredSize(Size.Empty);
        titleLabel.SetBounds(S(14), (headerH - titleSize.Height) / 2, titleSize.Width, titleSize.Height);

        var clearSize = clearBtn.PreferredMetricSize();
        clearBtn.SetBounds(innerW - S(10) - clearSize.Width, (headerH - clearSize.Height) / 2, clearSize.Width, clearSize.Height);

        var bodyTop = border + headerH + 1;
        int bodyH;
        if (rows.Count == 0)
        {
            bodyH = S(84);
            listPanel.Visible = false;
            emptyLabel.Visible = true;
            emptyLabel.Font = Theme.FontForDpi(Theme.UiFont, DeviceDpi);
            emptyLabel.SetBounds(border, bodyTop, innerW, bodyH);
        }
        else
        {
            emptyLabel.Visible = false;
            listPanel.Visible = true;
            var contentH = listPad * 2 + rows.Count * rowH + Math.Max(0, rows.Count - 1) * rowGap;
            var maxBodyH = S(320);
            bodyH = Math.Min(contentH, maxBodyH);
            listPanel.SetBounds(border, bodyTop, innerW, bodyH);
            listPanel.SetRows(rows, rowH, rowGap, listPad);
        }

        var totalH = bodyTop + bodyH + border;
        Size = new(width, totalH);
        RepositionToAnchor();
        Invalidate();
    }

    internal void RepositionToAnchor()
    {
        if (anchorControl is null || anchorControl.IsDisposed || !anchorControl.IsHandleCreated) return;
        var anchorRect = anchorControl.RectangleToScreen(anchorControl.ClientRectangle);
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
        if (!client.Contains(headerPanel.Bounds)) return false;
        if (rows.Count == 0)
            return emptyLabel.Visible && client.Contains(emptyLabel.Bounds);
        return listPanel.Visible && client.Contains(listPanel.Bounds)
            && rows.All(r => r.Width > 0 && r.Height > 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animTimer.Stop();
            animTimer.Dispose();
            tips.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class ClearFinishedButton : ChromeControl
    {
        readonly Action onClick;
        bool hovered;

        internal ClearFinishedButton(Action onClick)
        {
            this.onClick = onClick;
            BackColor = Theme.Ground;
            Cursor = Cursors.Hand;
            AccessibleName = "Clear finished downloads";
            AccessibleRole = AccessibleRole.PushButton;
        }

        internal Size PreferredMetricSize()
        {
            var measured = TextRenderer.MeasureText("Clear finished", Theme.FontForDpi(Theme.HintFont, DeviceDpi));
            return new Size(measured.Width + S(12), S(24));
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left) onClick();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (hovered)
                Theme.FillRounded(g, Theme.StripHover, new RectangleF(0, 0, Width, Height), Sf(6f));
            TextRenderer.DrawText(g, "Clear finished", Theme.FontForDpi(Theme.HintFont, DeviceDpi), ClientRectangle,
                hovered ? Theme.Ink : Theme.InkSoft,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    sealed class DownloadListPanel : Panel
    {
        readonly Panel content = new() { BackColor = Theme.Ground };
        int scrollOffset;
        int contentHeight;

        internal DownloadListPanel()
        {
            BackColor = Theme.Ground;
            DoubleBuffered = true;
            Controls.Add(content);
        }

        internal void SetRows(IReadOnlyList<DownloadRowControl> items, int rowH, int gap, int pad)
        {
            content.SuspendLayout();
            var keep = new HashSet<Control>(items);
            for (var i = content.Controls.Count - 1; i >= 0; i--)
                if (!keep.Contains(content.Controls[i])) content.Controls.RemoveAt(i);
            var w = Math.Max(0, ClientSize.Width - pad * 2);
            var y = pad;
            for (var i = 0; i < items.Count; i++)
            {
                var c = items[i];
                if (c.Parent != content) content.Controls.Add(c);
                if (content.Controls.GetChildIndex(c) != i) content.Controls.SetChildIndex(c, i);
                c.SetBounds(pad, y, w, rowH);
                y += rowH + gap;
            }
            contentHeight = items.Count > 0 ? y - gap + pad : 0;
            ClampScroll();
            content.SetBounds(0, -scrollOffset, Math.Max(0, ClientSize.Width), Math.Max(contentHeight, ClientSize.Height));
            content.ResumeLayout();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (contentHeight <= ClientSize.Height) return;
            if (e is HandledMouseEventArgs h) h.Handled = true;
            var prev = scrollOffset;
            scrollOffset -= e.Delta / 3;
            ClampScroll();
            if (scrollOffset != prev) content.Top = -scrollOffset;
        }

        void ClampScroll()
        {
            var max = Math.Max(0, contentHeight - ClientSize.Height);
            scrollOffset = Math.Clamp(scrollOffset, 0, max);
        }
    }

    enum RowActionKind { Pause, Resume, Cancel, Open, ShowFolder, Retry, Remove }
    readonly record struct RowAction(RowActionKind Kind, string Glyph, string Tip);

    sealed class DownloadRowControl : ChromeControl
    {
        readonly ToolTip tips;
        readonly Action<string> onPause;
        readonly Action<string> onResume;
        readonly Action<string> onCancel;
        readonly Action<string> onOpen;
        readonly Action<string> onShowFolder;
        readonly Action<string> onRetry;
        readonly Action<string> onRemove;

        DownloadEntry entry;
        bool fileExists;
        int animPhase;
        bool hovered;
        int hoveredAction = -1;
        string lastTip = "";
        RowAction[] actions = [];
        Rectangle[] actionRects = [];

        internal DownloadRowControl(
            DownloadEntry entry,
            int animPhase,
            ToolTip tips,
            Action<string> onPause,
            Action<string> onResume,
            Action<string> onCancel,
            Action<string> onOpen,
            Action<string> onShowFolder,
            Action<string> onRetry,
            Action<string> onRemove)
        {
            this.entry = entry;
            this.animPhase = animPhase;
            this.tips = tips;
            this.onPause = onPause;
            this.onResume = onResume;
            this.onCancel = onCancel;
            this.onOpen = onOpen;
            this.onShowFolder = onShowFolder;
            this.onRetry = onRetry;
            this.onRemove = onRemove;
            BackColor = Theme.Ground;
            AccessibleRole = AccessibleRole.ListItem;
            Sync(entry, animPhase);
        }

        internal bool IsIndeterminate => entry.State == DownloadItemState.InProgress && (entry.TotalBytes is null || entry.TotalBytes <= 0);

        internal void SetAnimPhase(int phase)
        {
            if (animPhase == phase) return;
            animPhase = phase;
            Invalidate();
        }

        internal void Sync(DownloadEntry next, int phase)
        {
            entry = next;
            animPhase = phase;
            fileExists = entry.State == DownloadItemState.Completed && entry.FileExists();
            AccessibleName = entry.Name;
            actions = BuildActions();
            LayoutActionRects();
            Cursor = CanOpenRow() ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        bool CanOpenRow() => entry.State == DownloadItemState.Completed && fileExists;

        RowAction[] BuildActions()
        {
            if (entry.State == DownloadItemState.InProgress)
            {
                return
                [
                    new RowAction(RowActionKind.Pause, DownloadGlyphs.Pause, "Pause"),
                    new RowAction(RowActionKind.Cancel, DownloadGlyphs.Close, "Cancel"),
                ];
            }
            if (entry.IsPaused)
            {
                return entry.CanResume
                    ? [new RowAction(RowActionKind.Resume, DownloadGlyphs.Play, "Resume"), new RowAction(RowActionKind.Cancel, DownloadGlyphs.Close, "Cancel")]
                    : [new RowAction(RowActionKind.Cancel, DownloadGlyphs.Close, "Cancel")];
            }
            if (entry.State == DownloadItemState.Completed)
            {
                if (fileExists)
                {
                    return
                    [
                        new RowAction(RowActionKind.Open, DownloadGlyphs.OpenFile, "Open file"),
                        new RowAction(RowActionKind.ShowFolder, DownloadGlyphs.Folder, "Show in folder"),
                        new RowAction(RowActionKind.Remove, DownloadGlyphs.Close, "Remove from list"),
                    ];
                }
                return [new RowAction(RowActionKind.Remove, DownloadGlyphs.Close, "Remove from list")];
            }
            // Interrupted (not paused)
            if (entry.Operation is not null && entry.CanResume)
            {
                return
                [
                    new RowAction(RowActionKind.Resume, DownloadGlyphs.Play, "Resume"),
                    new RowAction(RowActionKind.Remove, DownloadGlyphs.Close, "Remove from list"),
                ];
            }
            if (PageHandoff.Allowed(entry.Url))
            {
                return
                [
                    new RowAction(RowActionKind.Retry, DownloadGlyphs.Retry, "Retry"),
                    new RowAction(RowActionKind.Remove, DownloadGlyphs.Close, "Remove from list"),
                ];
            }
            return [new RowAction(RowActionKind.Remove, DownloadGlyphs.Close, "Remove from list")];
        }

        void LayoutActionRects()
        {
            var btn = S(24);
            var gap = S(2);
            var y = (Height - btn) / 2;
            actionRects = new Rectangle[actions.Length];
            var rx = Width - S(8) - btn;
            for (var i = actions.Length - 1; i >= 0; i--)
            {
                actionRects[i] = new Rectangle(rx, y, btn, btn);
                rx -= btn + gap;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutActionRects();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = false;
            hoveredAction = -1;
            UpdateTip("");
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var idx = HitAction(e.Location);
            Cursor = idx >= 0 || CanOpenRow() ? Cursors.Hand : Cursors.Default;
            if (idx != hoveredAction)
            {
                hoveredAction = idx;
                UpdateTip(idx >= 0 ? actions[idx].Tip : (CanOpenRow() ? $"Open {entry.Name}" : entry.Name));
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !ClientRectangle.Contains(e.Location)) return;
            var idx = HitAction(e.Location);
            if (idx >= 0)
            {
                ExecuteAction(actions[idx].Kind);
                return;
            }
            if (CanOpenRow()) onOpen(entry.FilePath);
        }

        int HitAction(Point p)
        {
            for (var i = 0; i < actionRects.Length; i++)
                if (actionRects[i].Contains(p)) return i;
            return -1;
        }

        void UpdateTip(string text)
        {
            if (lastTip == text) return;
            lastTip = text;
            try { tips.SetToolTip(this, text); } catch { }
        }

        void ExecuteAction(RowActionKind kind)
        {
            switch (kind)
            {
                case RowActionKind.Pause: onPause(entry.Id); break;
                case RowActionKind.Resume: onResume(entry.Id); break;
                case RowActionKind.Cancel: onCancel(entry.Id); break;
                case RowActionKind.Open: onOpen(entry.FilePath); break;
                case RowActionKind.ShowFolder: onShowFolder(entry.FilePath); break;
                case RowActionKind.Retry: onRetry(entry.Url); break;
                case RowActionKind.Remove: onRemove(entry.Id); break;
            }
        }

        string BuildStatusText()
        {
            var host = string.IsNullOrEmpty(entry.Host) ? null : entry.Host;
            if (entry.State == DownloadItemState.InProgress)
            {
                var progressText = entry.TotalBytes is > 0 and var total
                    ? $"{DownloadEntry.FormatBytes(entry.BytesReceived)} of {DownloadEntry.FormatBytes(total)}"
                    : DownloadEntry.FormatBytes(entry.BytesReceived);
                var eta = DownloadEntry.FormatEta(entry.EstimatedEndTime);
                return JoinParts(progressText, eta, host);
            }
            if (entry.IsPaused)
            {
                var progressText = entry.TotalBytes is > 0 and var total
                    ? $"{DownloadEntry.FormatBytes(entry.BytesReceived)} of {DownloadEntry.FormatBytes(total)}"
                    : entry.BytesReceived > 0 ? DownloadEntry.FormatBytes(entry.BytesReceived) : null;
                return JoinParts("Paused", progressText, host);
            }
            if (entry.State == DownloadItemState.Completed)
            {
                if (!fileExists) return JoinParts("File removed", host);
                var sizeText = entry.Size > 0 ? DownloadEntry.FormatBytes(entry.Size) : null;
                return JoinParts("Completed", sizeText, host);
            }
            var reason = string.IsNullOrWhiteSpace(entry.InterruptText)
                ? DownloadEntry.HumanInterruptReason(entry.InterruptReason)
                : entry.InterruptText;
            return JoinParts(reason, host);
        }

        static string JoinParts(params string?[] parts) =>
            string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (hovered)
                Theme.FillRounded(g, Theme.StripHover, new RectangleF(0, 0, Width, Height), Sf(8f));

            var left = S(10);
            var actionsLeft = actionRects.Length > 0 ? actionRects[0].Left : Width - S(8);
            var textW = Math.Max(0, actionsLeft - left - S(8));

            var isError = entry.State == DownloadItemState.Interrupted
                && !entry.IsPaused
                && entry.InterruptReason != CoreWebView2DownloadInterruptReason.UserCanceled
                && !string.Equals(entry.InterruptText, "Canceled", StringComparison.Ordinal);

            var nameColor = entry.State == DownloadItemState.Completed && !fileExists ? Theme.Muted : Theme.Ink;
            var nameRect = new Rectangle(left, S(8), textW, S(20));
            TextRenderer.DrawText(g, entry.Name, Theme.FontForDpi(Theme.UiFont, DeviceDpi), nameRect, nameColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            var statusRect = new Rectangle(left, S(29), textW, S(16));
            TextRenderer.DrawText(g, BuildStatusText(), Theme.FontForDpi(Theme.HintFont, DeviceDpi), statusRect,
                isError ? ErrorInk : Theme.InkSoft,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (entry.IsActive && textW > S(12))
            {
                var barH = Math.Max(3f, Sf(3f));
                var barRect = new RectangleF(left, S(50), textW, barH);
                Theme.FillRounded(g, Theme.Hairline, barRect, barH / 2f);
                var fillColor = entry.IsPaused ? Theme.Muted : Theme.DropLine;
                if (entry.TotalBytes is > 0 and var total)
                {
                    var frac = Math.Clamp(entry.BytesReceived / (double)total, 0.0, 1.0);
                    var fillW = Math.Max(barH, (float)(barRect.Width * frac));
                    Theme.FillRounded(g, fillColor, new RectangleF(barRect.X, barRect.Y, Math.Min(barRect.Width, fillW), barH), barH / 2f);
                }
                else
                {
                    var segW = barRect.Width * 0.30f;
                    var travel = barRect.Width - segW;
                    var segX = barRect.X + travel * (animPhase / 100f);
                    Theme.FillRounded(g, fillColor, new RectangleF(segX, barRect.Y, segW, barH), barH / 2f);
                }
            }

            var iconFont = Theme.FontForDpi(Theme.SmallIconFont, DeviceDpi);
            for (var i = 0; i < actionRects.Length; i++)
            {
                var rect = actionRects[i];
                if (i == hoveredAction)
                    Theme.FillRounded(g, Theme.StripActive, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), Sf(6f));
                var fg = i == hoveredAction ? Theme.Ink : Theme.InkSoft;
                TextRenderer.DrawText(g, actions[i].Glyph, iconFont, rect, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }
}
