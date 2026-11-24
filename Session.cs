using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SearchPort;

sealed record SessionTab(string Url, string Title, bool Pinned);
sealed record SessionState(SessionTab[] Tabs, int Selected, bool Vertical);

static class Session
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string LockName(string profilePath)
    {
        var key = Path.GetFullPath(profilePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return "SearchWindows-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public static IDisposable? TryAcquireLock(string profilePath)
    {
        Mutex mutex;
        try { mutex = new Mutex(false, LockName(profilePath)); }
        catch (UnauthorizedAccessException) { return null; }
        try
        {
            if (mutex.WaitOne(0)) return new SessionLock(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new SessionLock(mutex);
        }
        mutex.Dispose();
        return null;
    }

    sealed class SessionLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
    }

    // Missing, empty or corrupt → null. A file that exists but can't be read throws, so the caller won't overwrite it.
    public static SessionState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        SessionState? state;
        try { state = JsonSerializer.Deserialize<SessionState>(text, Json); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return null; }
        if (state is null) return null;
        var tabs = Filter(state.Tabs);
        return new SessionState(tabs, tabs.Length == 0 ? 0 : Math.Clamp(state.Selected, 0, tabs.Length - 1), state.Vertical);
    }

    public static void Save(string path, SessionState state)
    {
        var tabs = Filter(state.Tabs);
        var selected = tabs.Length == 0 ? 0 : Math.Clamp(state.Selected, 0, tabs.Length - 1);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Write to a unique sibling temp file then replace atomically so a crash or concurrent write cannot leave half-written JSON.
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(new SessionState(tabs, selected, state.Vertical), Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    static SessionTab[] Filter(SessionTab[]? tabs) => tabs is null
        ? []
        : [.. tabs.Where(t => t is not null && t.Url is not null && PageHandoff.Allowed(t.Url))
                  .Select(t => t with { Url = new Uri(t.Url).AbsoluteUri, Title = t.Title ?? "" })]; // normalized: Navigate rejects padded URLs
}

sealed class FaviconCache : IDisposable
{
    const int MaxPixels = 64;
    const int MaxBytes = 1024 * 1024;

    readonly string dir;
    readonly Dictionary<string, Bitmap?> icons = new(StringComparer.Ordinal);
    bool disposed;

    public FaviconCache(string sessionFile) =>
        dir = Path.Combine(Path.GetDirectoryName(sessionFile)!, "Favicons");

    public static string? HostOf(string? url) =>
        url is not null && PageHandoff.Allowed(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0
            ? uri.IdnHost.ToLowerInvariant()
            : null;

    public string PathForHost(string host) =>
        Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host))) + ".png");

    public Image? Get(string? url)
    {
        if (disposed || HostOf(url) is not { } host) return null;
        if (icons.TryGetValue(host, out var cached)) return cached;
        var loaded = LoadFromDisk(PathForHost(host));
        icons[host] = loaded;
        return loaded;
    }

    // Returns the previous bitmap so the caller can dispose it after refreshing controls.
    public Bitmap? Set(string host, Bitmap icon)
    {
        if (disposed) { icon.Dispose(); return null; }
        icons.TryGetValue(host, out var old);
        icons[host] = icon;
        return old;
    }

    public static async Task<byte[]?> ReadBoundedAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buf)) > 0)
        {
            if (ms.Length + read > MaxBytes) return null;
            ms.Write(buf, 0, read);
        }
        return ms.Length > 0 ? ms.ToArray() : null;
    }

    public static Bitmap? DecodeAndSave(byte[] bytes, string path)
    {
        var bmp = Decode(bytes);
        if (bmp is null) return null;
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                bmp.Save(tmp, ImageFormat.Png);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
            }
        }
        catch { }
        return bmp;
    }

    static Bitmap? LoadFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > MaxBytes) return null;
            return Decode(File.ReadAllBytes(path));
        }
        catch { return null; }
    }

    static Bitmap? Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var raw = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
            if (raw.Width <= 0 || raw.Height <= 0) return null;
            int w = raw.Width, h = raw.Height;
            if (w > MaxPixels || h > MaxPixels)
            {
                var scale = Math.Min((double)MaxPixels / w, (double)MaxPixels / h);
                w = Math.Max(1, (int)Math.Round(w * scale));
                h = Math.Max(1, (int)Math.Round(h * scale));
            }
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(bmp);
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = w == raw.Width && h == raw.Height
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(raw, new Rectangle(0, 0, w, h), 0, 0, raw.Width, raw.Height, GraphicsUnit.Pixel, attrs);
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var bmp in icons.Values) bmp?.Dispose();
        icons.Clear();
    }
}
