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
