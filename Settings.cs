using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace SearchPort;

enum ThemePreference { System, Light, Dark }

sealed record ExternalAppPermission(string Site, string Scheme);

sealed record AppSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool VerticalTabs { get; init; } = true;
    public bool TabBarCollapsed { get; init; } = false;
    public bool BlockingEnabled { get; init; } = true;
    public bool CleanLinksEnabled { get; init; } = true;
    public bool SleepingTabsEnabled { get; init; } = true;
    public bool AutoUpdateCheck { get; init; } = true;
    public bool ShareEnabled { get; init; } = true;
    public ExternalAppPermission[] AllowedExternalApps { get; init; } = [];
    public string[] DisabledFilterLists { get; init; } = [];
    public string[] CustomFilterLists { get; init; } = [];

    public bool Equals(AppSettings? other) =>
        other is not null
        && Theme == other.Theme
        && VerticalTabs == other.VerticalTabs
        && TabBarCollapsed == other.TabBarCollapsed
        && BlockingEnabled == other.BlockingEnabled
        && CleanLinksEnabled == other.CleanLinksEnabled
        && SleepingTabsEnabled == other.SleepingTabsEnabled
        && AutoUpdateCheck == other.AutoUpdateCheck
        && ShareEnabled == other.ShareEnabled
        && (AllowedExternalApps ?? []).SequenceEqual(other.AllowedExternalApps ?? [])
        && (DisabledFilterLists ?? []).SequenceEqual(other.DisabledFilterLists ?? [], StringComparer.Ordinal)
        && (CustomFilterLists ?? []).SequenceEqual(other.CustomFilterLists ?? [], StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Theme);
        hash.Add(VerticalTabs);
        hash.Add(TabBarCollapsed);
        hash.Add(BlockingEnabled);
        hash.Add(CleanLinksEnabled);
        hash.Add(SleepingTabsEnabled);
        hash.Add(AutoUpdateCheck);
        hash.Add(ShareEnabled);
        foreach (var p in AllowedExternalApps ?? []) hash.Add(p);
        foreach (var d in DisabledFilterLists ?? []) hash.Add(d, StringComparer.Ordinal);
        foreach (var c in CustomFilterLists ?? []) hash.Add(c, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

static class SettingsStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    static AppSettings Normalize(AppSettings s) => s with
    {
        AllowedExternalApps = ExternalSchemes.NormalizePermissions(s.AllowedExternalApps),
        DisabledFilterLists = FilterListService.NormalizeDisabledLists(s.DisabledFilterLists),
        CustomFilterLists = FilterListService.NormalizeCustomLists(s.CustomFilterLists),
    };

    // Missing, empty, unreadable, or corrupt -> defaults with Exists = false.
    public static (AppSettings Settings, bool Exists) Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return (new AppSettings(), false);
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return (new AppSettings(), false);
            var parsed = JsonSerializer.Deserialize<AppSettings>(text, Json);
            if (parsed is null) return (new AppSettings(), false);
            return (Normalize(parsed), true);
        }
        catch
        {
            return (new AppSettings(), false);
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var normalized = Normalize(settings);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(normalized, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

#pragma warning disable WFO5001
    public static SystemColorMode ToColorMode(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => SystemColorMode.Classic,
        ThemePreference.Dark => SystemColorMode.Dark,
        _ => SystemColorMode.System,
    };
#pragma warning restore WFO5001

    public static CoreWebView2PreferredColorScheme ToWebViewColorScheme(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => CoreWebView2PreferredColorScheme.Light,
        ThemePreference.Dark => CoreWebView2PreferredColorScheme.Dark,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    public static bool SelfCheck(string tempDir)
    {
        var file = Path.Combine(tempDir, $"settings-selfcheck-{Guid.NewGuid():N}.json");
        try
        {
            var (missing, missingExists) = Load(file);
            if (missingExists || missing != new AppSettings()) return false;

            File.WriteAllText(file, "{not valid json");
            var (corrupt, corruptExists) = Load(file);
            if (corruptExists || corrupt != new AppSettings()) return false;

            File.WriteAllText(file, """{"theme":"dark","blockingEnabled":false}""");
            var (partial, partialExists) = Load(file);
            if (!partialExists || partial.Theme != ThemePreference.Dark || partial.BlockingEnabled || !partial.VerticalTabs || partial.TabBarCollapsed || !partial.CleanLinksEnabled)
                return false;

            var custom = new AppSettings
            {
                Theme = ThemePreference.Light,
                VerticalTabs = false,
                TabBarCollapsed = true,
                BlockingEnabled = false,
                CleanLinksEnabled = false,
                SleepingTabsEnabled = false,
                AutoUpdateCheck = false,
                ShareEnabled = false,
                AllowedExternalApps = [new ExternalAppPermission("https://example.com", "mailto")],
                DisabledFilterLists = ["peter-lowe"],
                CustomFilterLists = ["https://example.com/custom-list.txt"],
            };
            Save(file, custom);
            var raw = File.ReadAllText(file);
            if (!raw.Contains("\"theme\"", StringComparison.Ordinal)
                || !raw.Contains("\"light\"", StringComparison.Ordinal)
                || !raw.Contains("\"verticalTabs\"", StringComparison.Ordinal)
                || !raw.Contains("\"tabBarCollapsed\"", StringComparison.Ordinal)
                || !raw.Contains("\"allowedExternalApps\"", StringComparison.Ordinal)
                || !raw.Contains("\"disabledFilterLists\"", StringComparison.Ordinal)
                || !raw.Contains("\"customFilterLists\"", StringComparison.Ordinal))
                return false;
            var (loaded, loadedExists) = Load(file);
            if (!loadedExists || loaded != custom) return false;

            // Dangerous or invalid entries in settings.json must be dropped on load.
            File.WriteAllText(file, """{"allowedExternalApps":[{"site":"https://example.com","scheme":"ms-msdt"},{"site":"https://example.com","scheme":"vscode"}],"disabledFilterLists":["EasyList","unknown-list","easylist"],"customFilterLists":["http://insecure.example/list.txt","javascript:alert(1)","https://filters.example.com/my.txt"]}""");
            var (sanitized, sanitizedExists) = Load(file);
            return sanitizedExists
                && sanitized.AllowedExternalApps.SequenceEqual([new ExternalAppPermission("https://example.com", "vscode")])
                && sanitized.DisabledFilterLists.SequenceEqual(["easylist"])
                && sanitized.CustomFilterLists.SequenceEqual(["https://filters.example.com/my.txt"]);
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }
}

static class SettingsPage
{
    public const string DisplayUrl = "browse://settings";
    public const string InternalHost = "settings.browse.invalid";
    public const string InternalOrigin = "https://settings.browse.invalid";
    public const string InternalUrl = "https://settings.browse.invalid/";
    public const string ResourceName = "SearchPort.SettingsPage.html";
    public const string ResponseHeaders =
        "Content-Type: text/html; charset=utf-8\r\n" +
        "Cache-Control: no-store\r\n" +
        "X-Content-Type-Options: nosniff\r\n" +
        "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'none'; connect-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public static readonly byte[] HtmlBytes = LoadHtmlBytes();

    static byte[] LoadHtmlBytes()
    {
        using var stream = typeof(SettingsPage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {ResourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static bool IsSettingsAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        return text.Equals(DisplayUrl, StringComparison.OrdinalIgnoreCase)
            || text.Equals(DisplayUrl + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSettingsHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && string.Equals(host.Trim().TrimEnd('.'), InternalHost, StringComparison.OrdinalIgnoreCase);

    public static bool IsSettingsOrigin(string? uri) =>
        !string.IsNullOrWhiteSpace(uri)
        && Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed)
        && IsSettingsHost(parsed.IdnHost);

    public static bool IsSettingsExactUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed)) return false;
        return parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && parsed.IsDefaultPort
            && parsed.UserInfo.Length == 0
            && IsSettingsHost(parsed.IdnHost)
            && parsed.AbsolutePath == "/"
            && string.IsNullOrEmpty(parsed.Query);
    }

    public static bool IsSettingsFaviconUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed)) return false;
        return parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && parsed.IsDefaultPort
            && parsed.UserInfo.Length == 0
            && IsSettingsHost(parsed.IdnHost)
            && parsed.AbsolutePath.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBlockedExternalReference(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        var text = uri.Trim();
        if (text.StartsWith("browse:", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsSettingsOrigin(text)) return true;
        if (text.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) && text.Length > 5)
            return IsSettingsOrigin(text[5..]);
        return false;
    }
}

enum SettingsActionKind
{
    Ready,
    SetSetting,
    ToggleFilterList,
    AddCustomFilterList,
    RemoveCustomFilterList,
    UpdateFilterLists,
    RegisterDefaultBrowser,
    UnregisterDefaultBrowser,
    ClearExternalApps,
    RemoveExternalApp,
    ClearBrowsingData,
    ApplyUpdate,
}

sealed record SettingsBridgeCommand(
    SettingsActionKind Kind,
    string? Key = null,
    bool? BoolValue = null,
    ThemePreference? ThemeValue = null,
    string? TextValue = null);

static class SettingsBridge
{
    public const int MaxMessageChars = 16_384;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    static readonly HashSet<string> BuiltInFilterIds = new(
        FilterListService.BuiltInLists.Select(l => l.Id),
        StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> BoolSettingKeys = new(StringComparer.Ordinal)
    {
        "verticalTabs",
        "tabBarCollapsed",
        "blockingEnabled",
        "cleanLinksEnabled",
        "sleepingTabsEnabled",
        "autoUpdateCheck",
        "shareEnabled",
    };

    public static bool TryParse(string? rawJson, out SettingsBridgeCommand? command)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(rawJson) || rawJson.Length > MaxMessageChars) return false;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return false;

            switch (typeEl.GetString())
            {
                case "ready":
                    command = new(SettingsActionKind.Ready);
                    return true;

                case "setSetting":
                {
                    if (!root.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String)
                        return false;
                    if (!root.TryGetProperty("value", out var valEl))
                        return false;
                    var key = keyEl.GetString();
                    if (key == "theme")
                    {
                        if (valEl.ValueKind != JsonValueKind.String) return false;
                        ThemePreference? theme = valEl.GetString() switch
                        {
                            "system" => ThemePreference.System,
                            "light" => ThemePreference.Light,
                            "dark" => ThemePreference.Dark,
                            _ => null,
                        };
                        if (theme is null) return false;
                        command = new(SettingsActionKind.SetSetting, Key: key, ThemeValue: theme);
                        return true;
                    }
                    if (key is not null && BoolSettingKeys.Contains(key))
                    {
                        if (valEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                        command = new(SettingsActionKind.SetSetting, Key: key, BoolValue: valEl.GetBoolean());
                        return true;
                    }
                    return false;
                }

                case "toggleFilterList":
                {
                    if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                        return false;
                    if (!root.TryGetProperty("enabled", out var enEl) || enEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return false;
                    var id = idEl.GetString()?.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(id) || !BuiltInFilterIds.Contains(id)) return false;
                    command = new(SettingsActionKind.ToggleFilterList, Key: id, BoolValue: enEl.GetBoolean());
                    return true;
                }

                case "addCustomFilterList":
                {
                    if (!root.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                        return false;
                    var url = urlEl.GetString() ?? "";
                    if (url.Length > 2048) return false;
                    command = new(SettingsActionKind.AddCustomFilterList, TextValue: url);
                    return true;
                }

                case "removeCustomFilterList":
                {
                    if (!root.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                        return false;
                    var url = urlEl.GetString();
                    if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
                    command = new(SettingsActionKind.RemoveCustomFilterList, TextValue: url);
                    return true;
                }

                case "updateFilterLists":
                    command = new(SettingsActionKind.UpdateFilterLists);
                    return true;

                case "registerDefaultBrowser":
                    command = new(SettingsActionKind.RegisterDefaultBrowser);
                    return true;

                case "unregisterDefaultBrowser":
                    command = new(SettingsActionKind.UnregisterDefaultBrowser);
                    return true;

                case "clearExternalApps":
                    command = new(SettingsActionKind.ClearExternalApps);
                    return true;
                case "removeExternalApp":
                {
                    if (!root.TryGetProperty("site", out var siteEl) || siteEl.ValueKind != JsonValueKind.String
                        || !root.TryGetProperty("scheme", out var schemeEl) || schemeEl.ValueKind != JsonValueKind.String)
                        return false;
                    var site = siteEl.GetString();
                    var scheme = schemeEl.GetString();
                    if (string.IsNullOrWhiteSpace(site) || site.Length > 2048 || string.IsNullOrWhiteSpace(scheme) || scheme.Length > 64)
                        return false;
                    command = new(SettingsActionKind.RemoveExternalApp, Key: scheme, TextValue: site);
                    return true;
                }

                case "clearBrowsingData":
                {
                    if (!root.TryGetProperty("confirmed", out var confEl) || confEl.ValueKind != JsonValueKind.True)
                        return false;
                    command = new(SettingsActionKind.ClearBrowsingData, BoolValue: true);
                    return true;
                }

                case "applyUpdate":
                    command = new(SettingsActionKind.ApplyUpdate);
                    return true;

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool TryApply(AppSettings current, SettingsBridgeCommand cmd, bool noUpdateCheckFlag, out AppSettings next)
    {
        next = current;
        if (cmd.Kind == SettingsActionKind.SetSetting)
        {
            switch (cmd.Key)
            {
                case "theme" when cmd.ThemeValue is { } theme:
                    next = current with { Theme = theme };
                    return true;
                case "verticalTabs" when cmd.BoolValue is { } v:
                    next = current with { VerticalTabs = v };
                    return true;
                case "tabBarCollapsed" when cmd.BoolValue is { } v:
                    next = current with { TabBarCollapsed = v };
                    return true;
                case "blockingEnabled" when cmd.BoolValue is { } v:
                    next = current with { BlockingEnabled = v };
                    return true;
                case "cleanLinksEnabled" when cmd.BoolValue is { } v:
                    next = current with { CleanLinksEnabled = v };
                    return true;
                case "sleepingTabsEnabled" when cmd.BoolValue is { } v:
                    next = current with { SleepingTabsEnabled = v };
                    return true;
                case "autoUpdateCheck" when cmd.BoolValue is { } v:
                    if (noUpdateCheckFlag) return false;
                    next = current with { AutoUpdateCheck = v };
                    return true;
                case "shareEnabled" when cmd.BoolValue is { } v:
                    next = current with { ShareEnabled = v };
                    return true;
                default:
                    return false;
            }
        }

        if (cmd.Kind == SettingsActionKind.ToggleFilterList && cmd.Key is { } id && cmd.BoolValue is { } enabled)
        {
            var disabled = new HashSet<string>(current.DisabledFilterLists ?? [], StringComparer.OrdinalIgnoreCase);
            if (enabled) disabled.Remove(id);
            else disabled.Add(id);
            next = current with { DisabledFilterLists = FilterListService.NormalizeDisabledLists(disabled) };
            return true;
        }

        return false;
    }

    public static string SerializeState(
        AppSettings settings,
        ThemePalette theme,
        IReadOnlyList<FilterListStatus> filterLists,
        bool defaultBrowserRegistered,
        bool noUpdateCheckFlag,
        string version,
        string? availableUpdateVersion)
    {
        var anyUpdating = filterLists.Any(f => f.Updating);
        var lists = filterLists.Select(f => new
        {
            id = f.Id,
            name = f.Name,
            url = f.Url,
            enabled = f.Enabled,
            isCustom = f.IsCustom,
            statusText = FilterListService.FormatLastUpdated(f.LastUpdatedUtc, f.Updating, f.LastError),
            hasError = !f.Updating && f.LastUpdatedUtc is null && !string.IsNullOrEmpty(f.LastError),
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            type = "state",
            settings,
            theme,
            filterLists = lists,
            anyFilterListUpdating = anyUpdating,
            defaultBrowserRegistered,
            noUpdateCheckFlag,
            version,
            availableUpdateVersion,
        }, Json);
    }

    public static string SerializeNotice(string scope, string kind, string message) =>
        JsonSerializer.Serialize(new { type = "notice", scope, kind, message }, Json);

    public static bool SelfCheck()
    {
        if (!SettingsPage.IsSettingsAddress("browse://settings")
            || !SettingsPage.IsSettingsAddress(" BROWSE://SETTINGS/ ")
            || SettingsPage.IsSettingsAddress("edge://settings")
            || SettingsPage.IsSettingsAddress("https://settings.browse.invalid/"))
            return false;

        if (!SettingsPage.IsSettingsExactUrl("https://settings.browse.invalid/")
            || !SettingsPage.IsSettingsExactUrl("https://settings.browse.invalid/#privacy")
            || SettingsPage.IsSettingsExactUrl("https://settings.browse.invalid/other")
            || SettingsPage.IsSettingsExactUrl("https://settings.browse.invalid/?q=1")
            || SettingsPage.IsSettingsExactUrl("http://settings.browse.invalid/")
            || SettingsPage.IsSettingsExactUrl("https://settings.browse.invalid:8443/"))
            return false;

        if (!SettingsPage.IsBlockedExternalReference("browse://settings")
            || !SettingsPage.IsBlockedExternalReference("https://settings.browse.invalid/")
            || !SettingsPage.IsBlockedExternalReference("blob:https://settings.browse.invalid/123")
            || SettingsPage.IsBlockedExternalReference("https://example.com/"))
            return false;

        // Valid commands parse cleanly
        if (!TryParse("""{"type":"ready"}""", out var readyCmd) || readyCmd?.Kind != SettingsActionKind.Ready) return false;
        if (!TryParse("""{"type":"setSetting","key":"theme","value":"dark"}""", out var themeCmd)
            || themeCmd?.ThemeValue != ThemePreference.Dark) return false;
        if (!TryParse("""{"type":"setSetting","key":"verticalTabs","value":false}""", out var vertCmd)
            || vertCmd?.BoolValue != false) return false;
        if (!TryParse("""{"type":"toggleFilterList","id":"EasyList","enabled":false}""", out var flCmd)
            || flCmd?.Key != "easylist" || flCmd.BoolValue != false) return false;
        if (!TryParse("""{"type":"clearBrowsingData","confirmed":true}""", out var clearCmd)
            || clearCmd?.Kind != SettingsActionKind.ClearBrowsingData) return false;

        // Invalid/malicious messages must be rejected
        string[] invalid =
        [
            "",
            "{bad json",
            "[]",
            "\"string\"",
            """{}""",
            """{"type":"execShell"}""",
            """{"type":"setSetting","key":"unknownKey","value":true}""",
            """{"type":"setSetting","key":"theme","value":"neon"}""",
            """{"type":"setSetting","key":"theme","value":1}""",
            """{"type":"setSetting","key":"verticalTabs","value":"false"}""",
            """{"type":"setSetting","key":"blockingEnabled","value":0}""",
            """{"type":"toggleFilterList","id":"not-a-list","enabled":false}""",
            """{"type":"toggleFilterList","id":"easylist","enabled":"no"}""",
            """{"type":"addCustomFilterList","url":123}""",
            """{"type":"removeCustomFilterList","url":""}""",
            """{"type":"clearBrowsingData"}""",
            """{"type":"clearBrowsingData","confirmed":false}""",
            """{"type":"clearBrowsingData","confirmed":"true"}""",
            "{\"type\":\"addCustomFilterList\",\"url\":\"" + new string('a', 3000) + "\"}",
        ];
        foreach (var bad in invalid)
            if (TryParse(bad, out _)) return false;

        // Apply check including --no-update-check guard
        var s0 = new AppSettings();
        if (!TryApply(s0, themeCmd!, noUpdateCheckFlag: false, out var s1) || s1.Theme != ThemePreference.Dark) return false;
        if (!TryParse("""{"type":"setSetting","key":"autoUpdateCheck","value":false}""", out var updCmd)) return false;
        if (TryApply(s0, updCmd!, noUpdateCheckFlag: true, out _)) return false;
        if (!TryApply(s0, updCmd!, noUpdateCheckFlag: false, out var s2) || s2.AutoUpdateCheck) return false;

        return SettingsPage.HtmlBytes.Length > 1000;
    }
}
