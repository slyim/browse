using System.Text.Json;
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

static class Shield
{
    static readonly string[] BlockedDomains =
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

    static readonly HashSet<string> BlockedSet = new(BlockedDomains, StringComparer.OrdinalIgnoreCase);

    static readonly string[] GenericSelectors =
    [
        ".adsbygoogle", "ins.adsbygoogle", "[id^=\"google_ads_\"]",
        "[id^=\"div-gpt-ad\"]", "[id^=\"taboola-\"]", "#taboola-below-article",
        "iframe[src*=\"doubleclick.net\"]", "iframe[src*=\"googlesyndication\"]",
        "iframe[src*=\"amazon-adsystem\"]",
        ".ad-slot", ".ad-slot-container", ".top-banner-ad-container",
        ".ad-leaderboard", ".ad-billboard", ".ad-giga", ".ad-mpu", ".ad-mrec",
        ".ad-unit", ".adunit", ".adslot", ".dfp-ad", ".gpt-ad", ".w_ad",
    ];

    static readonly (string[] Sites, string Selector)[] SiteSelectors =
    [
        (["as.com", "elpais.com"], ".ad"),
        (["theguardian.com"], ".top-fronts-banner-ad-container"),
        (["independent.co.uk", "the-independent.com"], "#billboard-wrapper"),
        (["cnn.com"], ".ad-slot-header__wrapper"),
        (["albumoftheyear.org"], ".headerAd, .homePageAdSquare"),
    ];

    static readonly string[] RedditSelectors =
    [
        "shreddit-ad-post", "shreddit-comments-page-ad", "shreddit-sidebar-ad",
        "shreddit-dynamic-ad-link", "comment-tree-ad", ".promotedlink",
    ];

    public static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var lookup = BlockedSet.GetAlternateLookup<ReadOnlySpan<char>>();
        var span = host.AsSpan().TrimEnd('.');
        while (!span.IsEmpty)
        {
            if (lookup.Contains(span)) return true;
            var dot = span.IndexOf('.');
            if (dot < 0) return false;
            span = span[(dot + 1)..];
        }
        return false;
    }

    // shortcut: last-two-labels eTLD+1 heuristic treats multi-label public suffixes (e.g. co.uk) as one site; upgrade to a Public Suffix List if first/third-party precision on those matters.
    public static bool IsThirdParty(Uri request, Uri? page)
    {
        if (page is null || !request.IsAbsoluteUri || !page.IsAbsoluteUri) return true;
        if (string.IsNullOrEmpty(request.Host) || string.IsNullOrEmpty(page.Host)) return true;
        return !string.Equals(SiteKey(request), SiteKey(page), StringComparison.OrdinalIgnoreCase);
    }

    static string SiteKey(Uri uri)
    {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return uri.Host;
        var host = uri.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return host;
        var last = host.LastIndexOf('.');
        if (last <= 0) return host;
        var prev = host.LastIndexOf('.', last - 1);
        return prev < 0 ? host : host[(prev + 1)..];
    }

    public static bool ShouldBlock(Uri request, Uri? page) =>
        request.IsAbsoluteUri
        && (request.Scheme == Uri.UriSchemeHttp || request.Scheme == Uri.UriSchemeHttps)
        && IsBlockedHost(request.Host)
        && IsThirdParty(request, page);

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
        && ShouldBlock(request, pendingMainFrame ?? page);

    public static string CosmeticScript { get; } = BuildCosmeticScript();

    static string BuildCosmeticScript()
    {
        var generic = JsonSerializer.Serialize($"{string.Join(", ", GenericSelectors)}{{display:none!important}}");
        var scoped = JsonSerializer.Serialize(
            SiteSelectors
                .Append((Sites: new[] { "reddit.com" }, Selector: string.Join(", ", RedditSelectors)))
                .Select(x => new object[] { x.Sites, $"{x.Selector}{{display:none!important}}" }));

        // Inject into documentElement if <head> doesn't exist yet at document creation; style id keeps it idempotent.
        return $$"""
            (() => {
              try {
                const id = 'search-shield-css';
                if (document.getElementById(id)) return;
                const h = (location.hostname || '').toLowerCase().replace(/\.+$/, '');
                const match = d => h === d || h.endsWith('.' + d);
                const css = [{{generic}}];
                for (const [sites, rule] of {{scoped}}) {
                  if (sites.some(match)) css.push(rule);
                }
                const inject = () => {
                  if (document.getElementById(id)) return;
                  const root = document.head || document.documentElement;
                  if (!root) return;
                  const s = document.createElement('style');
                  s.id = id;
                  s.textContent = css.join('\n');
                  root.appendChild(s);
                };
                if (document.head || document.documentElement) inject();
                else document.addEventListener('DOMContentLoaded', inject, { once: true });
              } catch {}
            })();
            """;
    }

    public static async Task<Func<int>> AttachAsync(CoreWebView2 core, CoreWebView2Environment environment, Func<Uri?> pageUri, Func<Uri?> pendingMainFrameUri)
    {
        // Profile-wide and idempotent. Balanced (Edge default) adds Edge's tracker list; Strict breaks embedded sign-ins and pre-empts this handler.
        if (core.Profile is not null)
            core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;

        foreach (var d in BlockedDomains)
        {
            core.AddWebResourceRequestedFilter($"*://{d}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.AddWebResourceRequestedFilter($"*://*.{d}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }

        var blocked = 0;
        core.WebResourceRequested += (_, e) =>
        {
            if (e.Response is not null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var req)) return;
            var secFetchDest = e.Request.Headers.Contains("Sec-Fetch-Dest") ? e.Request.Headers.GetHeader("Sec-Fetch-Dest") : null;
            if (ShouldBlock(req, pageUri(), e.ResourceContext, pendingMainFrameUri(), e.RequestedSourceKind, secFetchDest))
            {
                e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Access-Control-Allow-Origin: *");
                Interlocked.Increment(ref blocked);
            }
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticScript);
        return () => Volatile.Read(ref blocked);
    }
}
