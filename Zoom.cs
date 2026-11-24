using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SearchPort;

static class ZoomLevels
{
    public const double Min = 0.25;
    public const double Max = 5.00;
    public const double Default = 1.00;
    const double Epsilon = 0.005;

    public static readonly double[] Steps =
    [
        0.25, 0.33, 0.50, 0.67, 0.75, 0.80, 0.90,
        1.00,
        1.10, 1.25, 1.50, 1.75, 2.00, 2.50, 3.00, 4.00, 5.00,
    ];

    public static bool IsDefault(double factor) =>
        !double.IsFinite(factor) || Math.Abs(factor - Default) < Epsilon;

    public static double Normalize(double factor)
    {
        if (!double.IsFinite(factor)) return Default;
        var rounded = Math.Round(Math.Clamp(factor, Min, Max), 2, MidpointRounding.AwayFromZero);
        return Math.Abs(rounded - Default) < Epsilon ? Default : rounded;
    }

    public static double StepIn(double current)
    {
        var value = double.IsFinite(current) ? current : Default;
        foreach (var step in Steps)
            if (step > value + Epsilon) return step;
        return Max;
    }

    public static double StepOut(double current)
    {
        var value = double.IsFinite(current) ? current : Default;
        for (var i = Steps.Length - 1; i >= 0; i--)
            if (Steps[i] < value - Epsilon) return Steps[i];
        return Min;
    }

    public static string FormatPercent(double factor) =>
        $"{(int)Math.Round(Normalize(factor) * 100.0, MidpointRounding.AwayFromZero)}%";
}

sealed class SitePrefs
{
    readonly Dictionary<string, double> zoom = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> shieldDisabled = new(StringComparer.OrdinalIgnoreCase);

    public int Count => zoom.Count;
    public int ShieldDisabledCount => shieldDisabled.Count;

    public double GetZoom(string? origin) =>
        origin is not null && zoom.TryGetValue(origin, out var factor) ? factor : ZoomLevels.Default;

    public bool SetZoom(string origin, double factor)
    {
        if (SitePrefsStore.OriginOf(origin) is not { } key) return false;
        var normalized = ZoomLevels.Normalize(factor);
        if (ZoomLevels.IsDefault(normalized))
            return zoom.Remove(key);
        if (zoom.TryGetValue(key, out var existing) && Math.Abs(existing - normalized) < 0.005)
            return false;
        zoom[key] = normalized;
        return true;
    }

    public bool IsShieldDisabled(string? urlOrHost) =>
        SitePrefsStore.HostOf(urlOrHost) is { } host && shieldDisabled.Contains(host);

    public bool SetShieldDisabled(string? urlOrHost, bool disabled)
    {
        if (SitePrefsStore.HostOf(urlOrHost) is not { } host) return false;
        return disabled ? shieldDisabled.Add(host) : shieldDisabled.Remove(host);
    }

    public SortedDictionary<string, double> SnapshotZoom() => new(zoom, StringComparer.OrdinalIgnoreCase);

    public string[] SnapshotShieldDisabled() =>
        shieldDisabled.Count == 0 ? [] : [.. shieldDisabled.OrderBy(static x => x, StringComparer.Ordinal)];
}

static class SitePrefsStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    sealed record SitePrefsFile(Dictionary<string, double>? Zoom, string[]? ShieldDisabled = null);

    public static string? OriginOf(string? url) => ExternalSchemes.NormalizeSite(url);

    public static string? HostOf(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return null;
        var trimmed = urlOrHost.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            var h = uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
            h = h.TrimEnd('.').ToLowerInvariant();
            return h.Length > 0 && !SettingsPage.IsSettingsHost(h) ? h : null;
        }
        if (trimmed.Any(c => char.IsWhiteSpace(c) || c < 0x20 || c is '/' or '?' or '#' or '@'))
            return null;
        var bare = trimmed.TrimEnd('.').ToLowerInvariant();
        if (bare.Length == 0 || bare.Length > 253) return null;
        var check = bare.StartsWith('[') && bare.EndsWith(']') ? bare[1..^1] : bare;
        return Uri.CheckHostName(check) != UriHostNameType.Unknown ? bare : null;
    }

    // Missing, empty, unreadable, or corrupt -> empty prefs, never throws.
    public static SitePrefs Load(string path)
    {
        var prefs = new SitePrefs();
        try
        {
            if (!File.Exists(path)) return prefs;
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return prefs;

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return prefs;
            if (doc.RootElement.TryGetProperty("zoom", out var zoomEl) && zoomEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in zoomEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var factor))
                        prefs.SetZoom(prop.Name, factor);
                }
            }
            if (doc.RootElement.TryGetProperty("shieldDisabled", out var shieldEl) && shieldEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in shieldEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        prefs.SetShieldDisabled(item.GetString(), true);
                }
            }
        }
        catch { }
        return prefs;
    }

    public static void Save(string path, SitePrefs prefs)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var disabled = prefs.SnapshotShieldDisabled();
        var payload = JsonSerializer.Serialize(new SitePrefsFile(new(prefs.SnapshotZoom()), disabled.Length > 0 ? disabled : null), Json);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, payload);
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
        var file = Path.Combine(tempDir, $"site-prefs-selfcheck-{Guid.NewGuid():N}.json");
        try
        {
            var missing = Load(file);
            if (missing.Count != 0 || missing.ShieldDisabledCount != 0 || missing.GetZoom("https://example.com") != 1.0) return false;

            File.WriteAllText(file, "{not valid json");
            var corrupt = Load(file);
            if (corrupt.Count != 0 || corrupt.ShieldDisabledCount != 0) return false;

            File.WriteAllText(file, "");
            if (Load(file).Count != 0) return false;

            File.WriteAllText(file, """{"zoom":{"https://a.example":"bad","https://b.example":1.25,"https://c.example":1.0,"javascript:alert(1)":2.0,"https://d.example":99.0},"shieldDisabled":["Example.COM","javascript:alert(1)","http://127.0.0.1:8080/ads","bad host"]}""");
            var sanitized = Load(file);
            if (sanitized.Count != 2
                || sanitized.GetZoom("https://b.example") != 1.25
                || sanitized.GetZoom("https://d.example") != ZoomLevels.Max
                || sanitized.GetZoom("https://c.example") != 1.0
                || sanitized.ShieldDisabledCount != 2
                || !sanitized.IsShieldDisabled("https://example.com/page")
                || !sanitized.IsShieldDisabled("127.0.0.1")
                || sanitized.IsShieldDisabled("other.example"))
                return false;

            var prefs = new SitePrefs();
            prefs.SetZoom("https://Example.COM:443/path", 1.50);
            prefs.SetZoom("http://127.0.0.1:8080/x", 0.75);
            prefs.SetZoom("https://default.example/", 1.00);
            prefs.SetShieldDisabled("https://News.Example.COM/story", true);
            Save(file, prefs);
            var loaded = Load(file);
            if (loaded.Count != 2
                || loaded.GetZoom("https://example.com") != 1.50
                || loaded.GetZoom("http://127.0.0.1:8080") != 0.75
                || loaded.GetZoom("https://default.example") != 1.00
                || !loaded.IsShieldDisabled("news.example.com")
                || loaded.IsShieldDisabled("example.com"))
                return false;

            prefs.SetZoom("https://example.com", 1.00);
            prefs.SetShieldDisabled("news.example.com", false);
            Save(file, prefs);
            var afterReset = Load(file);
            if (afterReset.Count != 1 || afterReset.GetZoom("https://example.com") != 1.00 || afterReset.ShieldDisabledCount != 0) return false;

            return ZoomLevels.StepIn(1.0) == 1.10
                && ZoomLevels.StepIn(1.10) == 1.25
                && ZoomLevels.StepIn(5.0) == 5.0
                && ZoomLevels.StepOut(1.0) == 0.90
                && ZoomLevels.StepOut(0.25) == 0.25
                && ZoomLevels.FormatPercent(1.25) == "125%";
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }
}
