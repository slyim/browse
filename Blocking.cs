using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace SearchPort;

static class CleanLink
{
    static readonly HashSet<string> Tracking = new(StringComparer.Ordinal)
    {
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "yclid",
        "twclid", "ttclid", "mc_eid", "igshid", "igsh", "_hsenc", "_hsmi", "mkt_tok",
    };

    static readonly Dictionary<string, string[]> TrackingOn = new(StringComparer.Ordinal)
    {
        ["si"] = ["youtube.com", "youtu.be", "spotify.com"],
    };

    static readonly HashSet<string> Signatures = new(StringComparer.Ordinal)
    {
        "x-amz-signature", "x-goog-signature", "signature", "sig",
        "oauth_signature", "hmac", "x-oss-signature", "q-signature", "x-signature", "x-hub-signature", "x-ms-signature", "hdnts", "hdnea",
    };

    public static string Clean(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return url;

        // Slice the raw string rather than rebuilding via UriBuilder so encoding, order, and #fragment stay byte-identical.
        var hashIdx = url.IndexOf('#');
        var queryIdx = hashIdx < 0 ? url.IndexOf('?') : url.IndexOf('?', 0, hashIdx);
        if (queryIdx < 0) return url;

        var queryEnd = hashIdx < 0 ? url.Length : hashIdx;
        if (queryEnd == queryIdx + 1) return url;

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        var pieces = url[(queryIdx + 1)..queryEnd].Split('&');
        var names = new string[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            names[i] = DecodeName(pieces[i]);
            if (Signatures.Contains(names[i])) return url;
        }

        var kept = new List<string>(pieces.Length);
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!Tracks(names[i], host)) kept.Add(pieces[i]);
        }

        if (kept.Count == pieces.Length) return url;

        var prefix = url[..queryIdx];
        var fragment = hashIdx < 0 ? "" : url[hashIdx..];
        return kept.Count == 0 ? prefix + fragment : $"{prefix}?{string.Join('&', kept)}{fragment}";
    }

    static string DecodeName(string piece)
    {
        var eq = piece.IndexOf('=');
        var raw = eq < 0 ? piece : piece[..eq];
        return Uri.UnescapeDataString(raw.Replace('+', ' ')).ToLowerInvariant();
    }

    static bool Tracks(string name, string host)
    {
        if (name.StartsWith("utm_", StringComparison.Ordinal) || Tracking.Contains(name)) return true;
        if (!TrackingOn.TryGetValue(name, out var sites)) return false;
        foreach (var site in sites)
        {
            if (host == site || (host.Length > site.Length && host.EndsWith(site, StringComparison.Ordinal) && host[host.Length - site.Length - 1] == '.'))
                return true;
        }
        return false;
    }
}

sealed class ShieldAttachment
{
    readonly CoreWebView2 core;
    readonly Func<Uri?> pageUri;
    readonly Func<Uri?> pendingMainFrameUri;
    readonly Func<bool> isGlobalEnabled;
    readonly Func<string?, bool> isSiteEnabled;
    readonly SemaphoreSlim cosmeticLock = new(1, 1);
    int blocked;
    string? cosmeticScriptId;
    string? cosmeticHost;
    int cosmeticEngineVersion;
    bool cosmeticActive;
    bool ownsWorkerFilter;

    internal ShieldAttachment(
        CoreWebView2 core,
        Func<Uri?> pageUri,
        Func<Uri?> pendingMainFrameUri,
        Func<bool> isGlobalEnabled,
        Func<string?, bool> isSiteEnabled)
    {
        this.core = core;
        this.pageUri = pageUri;
        this.pendingMainFrameUri = pendingMainFrameUri;
        this.isGlobalEnabled = isGlobalEnabled;
        this.isSiteEnabled = isSiteEnabled;
    }

    public int BlockedCount => Volatile.Read(ref blocked);
    public bool OwnsWorkerFilter => ownsWorkerFilter;

    public int IncrementBlocked() => Interlocked.Increment(ref blocked);
    public void ResetBlocked() => Interlocked.Exchange(ref blocked, 0);

    public void SetWorkerFilterOwned(bool owned)
    {
        if (ownsWorkerFilter == owned) return;
        try
        {
            if (owned)
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, Shield.WorkerSourceKinds);
            else
                core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, Shield.WorkerSourceKinds);
            ownsWorkerFilter = owned;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
    }

    public bool NeedsCosmeticSync(string? host)
    {
        var wantActive = isGlobalEnabled() && !string.IsNullOrWhiteSpace(host) && isSiteEnabled(host);
        if (!wantActive) return cosmeticActive || cosmeticScriptId is not null;
        var normHost = NormalizeHost(host);
        return !cosmeticActive
            || cosmeticScriptId is null
            || cosmeticEngineVersion != Shield.CurrentEngine.Version
            || !string.Equals(cosmeticHost, normHost, StringComparison.Ordinal);
    }

    public void ClearCosmeticScriptSync()
    {
        if (cosmeticScriptId is not null)
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(cosmeticScriptId); } catch { }
            cosmeticScriptId = null;
        }
        cosmeticActive = false;
        cosmeticHost = null;
        cosmeticEngineVersion = 0;
    }

    public Task SetEnabledAsync(bool enabled) => SyncCosmeticAsync((pendingMainFrameUri() ?? pageUri())?.IdnHost, applyToCurrentPage: true);

    public async Task SyncCosmeticAsync(string? host, bool applyToCurrentPage)
    {
        await cosmeticLock.WaitAsync();
        try
        {
            var globalOn = isGlobalEnabled();
            try
            {
                if (core.Profile is not null)
                    core.Profile.PreferredTrackingPreventionLevel = globalOn
                        ? CoreWebView2TrackingPreventionLevel.Balanced
                        : CoreWebView2TrackingPreventionLevel.None;
            }
            catch { }

            var wantActive = globalOn && !string.IsNullOrWhiteSpace(host) && isSiteEnabled(host);
            var engine = Shield.CurrentEngine;
            var normHost = NormalizeHost(host);

            if (!wantActive)
            {
                if (cosmeticScriptId is not null)
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(cosmeticScriptId); } catch { }
                    cosmeticScriptId = null;
                }
                cosmeticActive = false;
                cosmeticHost = null;
                cosmeticEngineVersion = 0;
                if (applyToCurrentPage)
                {
                    try
                    {
                        if (!core.IsSuspended)
                            await core.ExecuteScriptAsync("document.getElementById('search-shield-css')?.remove()");
                    }
                    catch { }
                }
                return;
            }

            if (cosmeticActive
                && cosmeticScriptId is not null
                && cosmeticEngineVersion == engine.Version
                && string.Equals(cosmeticHost, normHost, StringComparison.Ordinal))
            {
                if (applyToCurrentPage)
                {
                    try
                    {
                        var curScript = engine.BuildCosmeticScriptForHost(normHost.AsSpan());
                        if (curScript is not null && !core.IsSuspended)
                            await core.ExecuteScriptAsync(curScript);
                    }
                    catch { }
                }
                return;
            }

            var script = engine.BuildCosmeticScriptForHost(normHost.AsSpan());
            if (cosmeticScriptId is not null)
            {
                try { core.RemoveScriptToExecuteOnDocumentCreated(cosmeticScriptId); } catch { }
                cosmeticScriptId = null;
            }

            if (script is not null)
            {
                var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                if (!isGlobalEnabled() || !isSiteEnabled(host))
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(id); } catch { }
                    cosmeticActive = false;
                    cosmeticHost = null;
                    cosmeticEngineVersion = 0;
                    return;
                }
                cosmeticScriptId = id;
                cosmeticHost = normHost;
                cosmeticEngineVersion = engine.Version;
                cosmeticActive = true;
                if (applyToCurrentPage && !core.IsSuspended)
                    await core.ExecuteScriptAsync(script);
            }
            else
            {
                cosmeticHost = normHost;
                cosmeticEngineVersion = engine.Version;
                cosmeticActive = true;
                if (applyToCurrentPage && !core.IsSuspended)
                    await core.ExecuteScriptAsync("document.getElementById('search-shield-css')?.remove()");
            }
        }
        catch { }
        finally
        {
            cosmeticLock.Release();
        }
    }

    static string NormalizeHost(string? host) =>
        string.IsNullOrWhiteSpace(host) ? "" : host.Trim().TrimEnd('.').ToLowerInvariant();
}

static class Shield
{
    internal const CoreWebView2WebResourceRequestSourceKinds WorkerSourceKinds =
        CoreWebView2WebResourceRequestSourceKinds.SharedWorker | CoreWebView2WebResourceRequestSourceKinds.ServiceWorker;

    internal static readonly string[] BuiltInBlockedDomains =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "googletagservices.com", "google-analytics.com", "googletagmanager.com",
        "adservice.google.com", "amazon-adsystem.com", "adnxs.com", "adsrvr.org",
        "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "rubiconproject.com", "pubmatic.com", "openx.net", "casalemedia.com",
        "smartadserver.com", "sharethrough.com", "indexww.com", "bidswitch.net",
        "33across.com", "teads.tv", "moatads.com", "adroll.com",
        "scorecardresearch.com", "quantserve.com", "chartbeat.com",
        "hotjar.com", "mouseflow.com", "fullstory.com", "clarity.ms",
        "mixpanel.com", "amplitude.com", "segment.com", "segment.io",
        "branch.io", "appsflyer.com", "adjust.com", "analytics.tiktok.com",
        "connect.facebook.net", "ads-twitter.com", "analytics.twitter.com",
        "intergient.com", "btloader.com", "ad-delivery.net", "mentorsticks.com",
        "dns-finder.com", "confiant-integrations.net", "cloudflareinsights.com",
    ];

    internal static readonly string[] BuiltInGenericSelectors =
    [
        ".adsbygoogle", "ins.adsbygoogle", "[id^=\"google_ads_\"]",
        "[id^=\"div-gpt-ad\"]", "[id^=\"taboola-\"]", "#taboola-below-article",
        "iframe[src*=\"doubleclick.net\"]", "iframe[src*=\"googlesyndication\"]",
        "iframe[src*=\"amazon-adsystem\"]",
        ".ad-slot", ".ad-slot-container", ".top-banner-ad-container",
        ".ad-leaderboard", ".ad-billboard", ".ad-giga", ".ad-mpu", ".ad-mrec",
        ".ad-unit", ".adunit", ".adslot", ".dfp-ad", ".gpt-ad", ".w_ad",
    ];

    internal static readonly (string[] Sites, string Selector)[] BuiltInSiteSelectors =
    [
        (["as.com", "elpais.com"], ".ad"),
        (["theguardian.com"], ".top-fronts-banner-ad-container"),
        (["independent.co.uk", "the-independent.com"], "#billboard-wrapper"),
        (["cnn.com"], ".ad-slot-header__wrapper"),
        (["albumoftheyear.org"], ".headerAd, .homePageAdSquare"),
    ];

    internal static readonly string[] BuiltInRedditSelectors =
    [
        "shreddit-ad-post", "shreddit-comments-page-ad", "shreddit-sidebar-ad",
        "shreddit-dynamic-ad-link", "comment-tree-ad", ".promotedlink",
    ];

    static FilterEngine currentEngine = FilterEngine.CreateFallback();
    static int workerBlockedIntercepted;

    public static FilterEngine CurrentEngine => Volatile.Read(ref currentEngine);
    public static void SetEngine(FilterEngine engine) => Volatile.Write(ref currentEngine, engine);
    internal static int WorkerBlockedIntercepted => Volatile.Read(ref workerBlockedIntercepted);
    internal static void ResetWorkerBlockedIntercepted() => Interlocked.Exchange(ref workerBlockedIntercepted, 0);

    public static bool IsThirdParty(Uri request, Uri? page) =>
        CurrentEngine.Psl.IsThirdParty(request, page);

    public static bool ShouldBlock(Uri request, Uri? page) =>
        CurrentEngine.ShouldBlock(request, page, CoreWebView2WebResourceContext.All);

    public static bool IsTopLevelDocument(
        CoreWebView2WebResourceContext context,
        Uri request,
        Uri? pendingMainFrame,
        CoreWebView2WebResourceRequestSourceKinds sourceKind = CoreWebView2WebResourceRequestSourceKinds.Document,
        string? secFetchDest = null) =>
        context == CoreWebView2WebResourceContext.Document
        && sourceKind == CoreWebView2WebResourceRequestSourceKinds.Document
        && (string.Equals(secFetchDest, "document", StringComparison.OrdinalIgnoreCase)
            || (pendingMainFrame is not null
                && pendingMainFrame.IsAbsoluteUri
                && request.IsAbsoluteUri
                && Uri.Compare(request, pendingMainFrame, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0));

    public static bool ShouldBlock(
        Uri request,
        Uri? page,
        CoreWebView2WebResourceContext context,
        Uri? pendingMainFrame,
        CoreWebView2WebResourceRequestSourceKinds sourceKind = CoreWebView2WebResourceRequestSourceKinds.Document,
        string? secFetchDest = null) =>
        !IsTopLevelDocument(context, request, pendingMainFrame, sourceKind, secFetchDest)
        && CurrentEngine.ShouldBlock(request, pendingMainFrame ?? page, context);

    static Uri? ExtractWorkerInitiator(CoreWebView2HttpRequestHeaders headers, Func<Uri?> activePageUri)
    {
        foreach (var name in new[] { "Origin", "Referer" })
        {
            if (!headers.Contains(name)) continue;
            var val = headers.GetHeader(name);
            if (!string.IsNullOrWhiteSpace(val)
                && Uri.TryCreate(val, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                return parsed;
        }
        return activePageUri();
    }

    public static async Task<ShieldAttachment> AttachAsync(
        CoreWebView2 core,
        CoreWebView2Environment environment,
        Func<Uri?> pageUri,
        Func<Uri?> pendingMainFrameUri,
        Func<Uri?> activePageUri,
        Func<bool> isGlobalEnabled,
        Func<string?, bool> isSiteEnabled,
        Action onBlocked,
        Action<Uri?> onWorkerBlocked)
    {
        // Profile-wide and idempotent. Balanced (Edge default) adds Edge's tracker list; Strict breaks embedded sign-ins and pre-empts this handler.
        if (core.Profile is not null)
            core.Profile.PreferredTrackingPreventionLevel = isGlobalEnabled()
                ? CoreWebView2TrackingPreventionLevel.Balanced
                : CoreWebView2TrackingPreventionLevel.None;

        // Register a single wildcard Document filter per tab; SharedWorker/ServiceWorker filter is attached to only one live tab per profile.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);

        var attachment = new ShieldAttachment(core, pageUri, pendingMainFrameUri, isGlobalEnabled, isSiteEnabled);

        core.WebResourceRequested += (_, e) =>
        {
            if (!isGlobalEnabled() || e.Response is not null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var req)) return;
            if (req.Scheme != Uri.UriSchemeHttp && req.Scheme != Uri.UriSchemeHttps) return;

            if (e.RequestedSourceKind != CoreWebView2WebResourceRequestSourceKinds.Document)
            {
                var initiator = ExtractWorkerInitiator(e.Request.Headers, activePageUri);
                if (!isSiteEnabled(initiator?.IdnHost)) return;
                if (CurrentEngine.ShouldBlock(req, initiator, e.ResourceContext))
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Access-Control-Allow-Origin: *");
                    Interlocked.Increment(ref workerBlockedIntercepted);
                    onWorkerBlocked(initiator);
                }
                return;
            }

            var secFetchDest = e.Request.Headers.Contains("Sec-Fetch-Dest") ? e.Request.Headers.GetHeader("Sec-Fetch-Dest") : null;
            var pending = pendingMainFrameUri();
            if (IsTopLevelDocument(e.ResourceContext, req, pending, e.RequestedSourceKind, secFetchDest)) return;

            var effectivePage = pending ?? pageUri();
            if (!isSiteEnabled(effectivePage?.IdnHost)) return;

            if (CurrentEngine.ShouldBlock(req, effectivePage, e.ResourceContext))
            {
                e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Access-Control-Allow-Origin: *");
                attachment.IncrementBlocked();
                onBlocked();
            }
        };

        await attachment.SyncCosmeticAsync(pageUri()?.IdnHost, applyToCurrentPage: false);
        return attachment;
    }
}
