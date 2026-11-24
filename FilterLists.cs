using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SearchPort;

sealed record FilterListDefinition(string Id, string Name, string Url, bool IsPsl = false);

sealed record FilterListStatus(
    string Id,
    string Name,
    string Url,
    bool Enabled,
    bool IsCustom,
    DateTimeOffset? LastUpdatedUtc,
    bool Updating,
    string? LastError);

sealed class FilterListService : IDisposable
{
    public const int MaxCustomLists = 20;
    const long MaxListBytes = 20L * 1024 * 1024;
    static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(4);
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static readonly FilterListDefinition[] BuiltInLists =
    [
        new("easylist", "EasyList", "https://easylist.to/easylist/easylist.txt"),
        new("easyprivacy", "EasyPrivacy", "https://easylist.to/easylist/easyprivacy.txt"),
        new("ublock-filters", "uBlock filters", "https://ublockorigin.github.io/uAssets/filters/filters.txt"),
        new("peter-lowe", "Peter Lowe's list", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext"),
    ];

    public static readonly FilterListDefinition PslDefinition =
        new("psl", "Public Suffix List", "https://publicsuffix.org/list/public_suffix_list.dat", IsPsl: true);

    static readonly HashSet<string> BuiltInIds = new(BuiltInLists.Select(l => l.Id), StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    sealed class ListCacheEntry
    {
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public DateTimeOffset? LastUpdatedUtc { get; set; }
        public DateTimeOffset? LastCheckedUtc { get; set; }
        public string? LastError { get; set; }
    }

    readonly string cacheDir;
    readonly string metadataFile;
    readonly bool allowLoopbackHttp;
    readonly IReadOnlyDictionary<string, string>? urlOverrides;
    readonly object stateLock = new();
    readonly SemaphoreSlim workLock = new(1, 1);
    readonly CancellationTokenSource disposeCts = new();
    readonly Dictionary<string, ListCacheEntry> metadata = new(StringComparer.OrdinalIgnoreCase);
    FilterEngine engine = FilterEngine.CreateFallback();
    bool updating;
    bool disposed;

    public event Action<FilterEngine>? EngineUpdated;
    public event Action? StatusChanged;

    public FilterEngine Engine => Volatile.Read(ref engine);
    public string CacheDir => cacheDir;

    public FilterListService(string profileRoot, bool allowLoopbackHttp = false, IReadOnlyDictionary<string, string>? urlOverrides = null)
    {
        cacheDir = Path.Combine(profileRoot, "FilterLists");
        metadataFile = Path.Combine(cacheDir, "metadata.json");
        this.allowLoopbackHttp = allowLoopbackHttp;
        this.urlOverrides = urlOverrides;
        LoadMetadata();
    }

    public static bool IsValidCustomListUrl(string? raw, out string normalized, bool allowLoopbackHttp = false)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (trimmed.Length > 2048 || trimmed.Any(c => c < 0x20 || c == 0x7F)) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.UserInfo.Length > 0 || string.IsNullOrEmpty(uri.Host)) return false;
        var schemeOk = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (allowLoopbackHttp && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback);
        if (!schemeOk) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    public static string[] NormalizeDisabledLists(IEnumerable<string>? ids)
    {
        if (ids is null) return [];
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var id = raw.Trim().ToLowerInvariant();
            if (BuiltInIds.Contains(id)) set.Add(id);
        }
        return [.. set];
    }

    public static string[] NormalizeCustomLists(IEnumerable<string>? urls, bool allowLoopbackHttp = false)
    {
        if (urls is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in urls)
        {
            if (!IsValidCustomListUrl(raw, out var norm, allowLoopbackHttp)) continue;
            if (!seen.Add(norm)) continue;
            list.Add(norm);
            if (list.Count >= MaxCustomLists) break;
        }
        return [.. list];
    }

    public static string CustomListId(string url) =>
        "custom-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToLowerInvariant())))[..16];

    string ResolveUrl(FilterListDefinition def) =>
        urlOverrides is not null && urlOverrides.TryGetValue(def.Id, out var over) ? over : def.Url;

    string FilePathForId(string id) => Path.Combine(cacheDir, id + ".txt");

    public IReadOnlyList<FilterListStatus> GetStatuses(AppSettings settings)
    {
        var disabled = new HashSet<string>(settings.DisabledFilterLists ?? [], StringComparer.OrdinalIgnoreCase);
        var custom = settings.CustomFilterLists ?? [];
        var list = new List<FilterListStatus>(BuiltInLists.Length + custom.Length);
        lock (stateLock)
        {
            foreach (var def in BuiltInLists)
            {
                metadata.TryGetValue(def.Id, out var meta);
                list.Add(new FilterListStatus(
                    def.Id,
                    def.Name,
                    def.Url,
                    !disabled.Contains(def.Id),
                    IsCustom: false,
                    meta?.LastUpdatedUtc,
                    updating,
                    meta?.LastError));
            }
            foreach (var url in custom)
            {
                var id = CustomListId(url);
                metadata.TryGetValue(id, out var meta);
                var display = Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.IdnHost}{u.AbsolutePath}" : url;
                list.Add(new FilterListStatus(
                    id,
                    display,
                    url,
                    Enabled: true,
                    IsCustom: true,
                    meta?.LastUpdatedUtc,
                    updating,
                    meta?.LastError));
            }
        }
        return list;
    }

    public void StartBackgroundLoadAndRefresh(AppSettings settings, bool skipNetworkRefresh = false)
    {
        if (disposed) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await CompileFromCacheAsync(settings, disposeCts.Token).ConfigureAwait(false);
                if (!skipNetworkRefresh && NeedsRefresh(settings))
                    await RefreshAndCompileAsync(settings, force: false, disposeCts.Token).ConfigureAwait(false);
            }
            catch { }
        });
    }

    public Task UpdateNowAsync(AppSettings settings, CancellationToken ct = default) =>
        Task.Run(() => RefreshAndCompileAsync(settings, force: true, ct), ct);

    public Task RecompileFromSettingsAsync(AppSettings settings, bool fetchMissing = true, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            await CompileFromCacheAsync(settings, ct).ConfigureAwait(false);
            if (fetchMissing && HasMissingEnabledCache(settings))
                await RefreshAndCompileAsync(settings, force: false, ct).ConfigureAwait(false);
        }, ct);

    public Task CompileFromDiskNowAsync(AppSettings settings, CancellationToken ct = default) =>
        Task.Run(() => CompileFromCacheAsync(settings, ct), ct);

    List<FilterListDefinition> ActiveListDefs(AppSettings settings)
    {
        var disabled = new HashSet<string>(settings.DisabledFilterLists ?? [], StringComparer.OrdinalIgnoreCase);
        var defs = new List<FilterListDefinition>(BuiltInLists.Length + (settings.CustomFilterLists?.Length ?? 0));
        foreach (var def in BuiltInLists)
            if (!disabled.Contains(def.Id)) defs.Add(def);
        foreach (var url in settings.CustomFilterLists ?? [])
            if (IsValidCustomListUrl(url, out var norm, allowLoopbackHttp))
                defs.Add(new FilterListDefinition(CustomListId(norm), norm, norm));
        return defs;
    }

    bool NeedsRefresh(AppSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        var defs = ActiveListDefs(settings);
        defs.Add(PslDefinition);
        lock (stateLock)
        {
            foreach (var def in defs)
            {
                var path = FilePathForId(def.Id);
                if (!File.Exists(path)) return true;
                if (!metadata.TryGetValue(def.Id, out var meta) || meta.LastCheckedUtc is null) return true;
                if (now - meta.LastCheckedUtc.Value >= RefreshInterval) return true;
            }
        }
        return false;
    }

    bool HasMissingEnabledCache(AppSettings settings)
    {
        foreach (var def in ActiveListDefs(settings))
            if (!File.Exists(FilePathForId(def.Id))) return true;
        return !File.Exists(FilePathForId(PslDefinition.Id));
    }

    async Task CompileFromCacheAsync(AppSettings settings, CancellationToken ct)
    {
        if (disposed || ct.IsCancellationRequested) return;
        await workLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed || ct.IsCancellationRequested) return;
            CompileAndSwapLocked(settings);
        }
        finally
        {
            workLock.Release();
        }
    }

    async Task RefreshAndCompileAsync(AppSettings settings, bool force, CancellationToken ct)
    {
        if (disposed || ct.IsCancellationRequested) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, disposeCts.Token);
        await workLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (disposed || linked.IsCancellationRequested) return;
            lock (stateLock) updating = true;
            StatusChanged?.Invoke();

            var defs = ActiveListDefs(settings);
            defs.Add(PslDefinition);
            var now = DateTimeOffset.UtcNow;
            var changedAny = false;

            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
            };
            using var client = new HttpClient(handler) { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("browse", Updater.CurrentVersionText));

            foreach (var def in defs)
            {
                if (linked.IsCancellationRequested) break;
                var filePath = FilePathForId(def.Id);
                ListCacheEntry entry;
                lock (stateLock)
                {
                    if (!metadata.TryGetValue(def.Id, out entry!))
                        metadata[def.Id] = entry = new ListCacheEntry();
                }

                var hasFile = File.Exists(filePath);
                if (!force && hasFile && entry.LastCheckedUtc is { } checkedAt && now - checkedAt < RefreshInterval)
                    continue;

                var url = ResolveUrl(def);
                if (!IsValidCustomListUrl(url, out var safeUrl, allowLoopbackHttp))
                {
                    lock (stateLock) entry.LastError = "Invalid URL";
                    continue;
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, safeUrl);
                    if (hasFile && !string.IsNullOrEmpty(entry.ETag))
                        req.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
                    if (hasFile && !string.IsNullOrEmpty(entry.LastModified))
                        req.Headers.TryAddWithoutValidation("If-Modified-Since", entry.LastModified);

                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                    var checkedNow = DateTimeOffset.UtcNow;
                    if (resp.StatusCode == HttpStatusCode.NotModified)
                    {
                        lock (stateLock)
                        {
                            entry.LastCheckedUtc = checkedNow;
                            entry.LastUpdatedUtc ??= checkedNow;
                            entry.LastError = null;
                        }
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        lock (stateLock) entry.LastError = $"HTTP {(int)resp.StatusCode}";
                        continue;
                    }
                    if (resp.Content.Headers.ContentLength > MaxListBytes)
                    {
                        lock (stateLock) entry.LastError = "List too large";
                        continue;
                    }

                    var bytes = await ReadBoundedBytesAsync(resp.Content, MaxListBytes, linked.Token).ConfigureAwait(false);
                    if (bytes is null)
                    {
                        lock (stateLock) entry.LastError = "List too large";
                        continue;
                    }

                    // Validate redirect target scheme wasn't downgraded to non-HTTPS.
                    if (resp.RequestMessage?.RequestUri is { } finalUri
                        && !IsValidCustomListUrl(finalUri.AbsoluteUri, out _, allowLoopbackHttp))
                    {
                        lock (stateLock) entry.LastError = "Blocked insecure redirect";
                        continue;
                    }

                    WriteAtomic(filePath, bytes);
                    var etag = resp.Headers.ETag?.ToString();
                    var lastMod = resp.Content.Headers.LastModified?.ToString("R");
                    lock (stateLock)
                    {
                        entry.ETag = etag;
                        entry.LastModified = lastMod;
                        entry.LastUpdatedUtc = checkedNow;
                        entry.LastCheckedUtc = checkedNow;
                        entry.LastError = null;
                    }
                    changedAny = true;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    lock (stateLock) entry.LastError = "Download failed";
                }
            }

            SaveMetadata();
            if (changedAny || engine.IsFallback)
                CompileAndSwapLocked(settings);
        }
        finally
        {
            lock (stateLock) updating = false;
            workLock.Release();
            StatusChanged?.Invoke();
        }
    }

    void CompileAndSwapLocked(AppSettings settings)
    {
        var psl = PublicSuffixList.BuiltIn;
        var pslText = TryReadText(FilePathForId(PslDefinition.Id));
        if (!string.IsNullOrWhiteSpace(pslText))
        {
            try { psl = PublicSuffixList.Parse(pslText); }
            catch { psl = PublicSuffixList.BuiltIn; }
        }

        var defs = ActiveListDefs(settings);
        var contents = new List<string>(defs.Count);
        foreach (var def in defs)
        {
            var text = TryReadText(FilePathForId(def.Id));
            if (!string.IsNullOrWhiteSpace(text))
                contents.Add(text);
        }

        // If user intentionally disabled ALL lists, compile an empty engine (no fallback).
        var compiled = defs.Count == 0
            ? FilterEngine.Compile([], psl, fallbackWhenEmpty: false)
            : FilterEngine.Compile(contents, psl, fallbackWhenEmpty: true);

        Volatile.Write(ref engine, compiled);
        EngineUpdated?.Invoke(compiled);
    }

    static string? TryReadText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxListBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            return text.IndexOf('\0') >= 0 ? null : text;
        }
        catch { return null; }
    }

    static async Task<byte[]?> ReadBoundedBytesAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var src = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = await src.ReadAsync(buf, ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
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

    void LoadMetadata()
    {
        try
        {
            var text = TryReadText(metadataFile);
            if (string.IsNullOrWhiteSpace(text)) return;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ListCacheEntry>>(text, Json);
            if (parsed is null) return;
            lock (stateLock)
            {
                foreach (var kv in parsed)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
                        metadata[kv.Key] = kv.Value;
            }
        }
        catch { }
    }

    void SaveMetadata()
    {
        try
        {
            Dictionary<string, ListCacheEntry> snap;
            lock (stateLock)
                snap = new Dictionary<string, ListCacheEntry>(metadata, StringComparer.OrdinalIgnoreCase);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snap, Json));
            WriteAtomic(metadataFile, bytes);
        }
        catch { }
    }

    public static string FormatLastUpdated(DateTimeOffset? utc, bool isUpdating, string? error)
    {
        if (isUpdating) return "Updating...";
        if (utc is not { } ts) return string.IsNullOrEmpty(error) ? "Not downloaded" : error;
        var age = DateTimeOffset.UtcNow - ts;
        if (age < TimeSpan.FromMinutes(1)) return "Updated just now";
        if (age < TimeSpan.FromHours(1)) return $"Updated {(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"Updated {(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(30)) return $"Updated {(int)age.TotalDays}d ago";
        return $"Updated {ts.ToLocalTime():yyyy-MM-dd}";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { disposeCts.Cancel(); } catch { }
        disposeCts.Dispose();
    }
}
