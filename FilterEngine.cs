using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SearchPort;

[Flags]
enum FilterResourceType : ushort
{
    None = 0,
    Script = 1 << 0,
    Image = 1 << 1,
    Stylesheet = 1 << 2,
    XmlHttpRequest = 1 << 3,
    Subdocument = 1 << 4,
    Media = 1 << 5,
    Font = 1 << 6,
    Websocket = 1 << 7,
    Ping = 1 << 8,
    Other = 1 << 9,
    Document = 1 << 10,
    AllSubresources = Script | Image | Stylesheet | XmlHttpRequest | Subdocument | Media | Font | Websocket | Ping | Other,
    All = AllSubresources | Document,
}

sealed record FilterBenchmarkResult(
    int Samples,
    int BlockedMatches,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MeanMs,
    double AllocBytesPerMatch,
    double EngineMemoryMB,
    int NetworkRules,
    int ExceptionRules,
    int CosmeticRules,
    int PslRules);

sealed class FilterEngine
{
    const byte FlagLeftAnchor = 1 << 0;
    const byte FlagRightAnchor = 1 << 1;
    const byte FlagDomainAnchor = 1 << 2;
    const byte FlagRequireThirdParty = 1 << 3;
    const byte FlagRequireFirstParty = 1 << 4;
    const byte FlagImportant = 1 << 5;
    const byte FlagMatchCase = 1 << 6;
    const byte FlagHasSpecial = 1 << 7; // pattern contains '*' or '^'

    const int CosmeticBatchSize = 100;

    static int nextVersion;

    readonly struct NetworkRule(
        string pattern,
        string[]? includeDomains,
        string[]? excludeDomains,
        FilterResourceType typeMask,
        byte flags,
        int next)
    {
        public readonly string Pattern = pattern;
        public readonly string[]? IncludeDomains = includeDomains;
        public readonly string[]? ExcludeDomains = excludeDomains;
        public readonly FilterResourceType TypeMask = typeMask;
        public readonly byte Flags = flags;
        public readonly int Next = next;
    }

    readonly struct ScopedCosmeticRule(string selector, string[]? excludeDomains)
    {
        public readonly string Selector = selector;
        public readonly string[]? ExcludeDomains = excludeDomains;
    }

    sealed class RuleBucket
    {
        public readonly HashSet<string> HostsAny;
        public readonly HashSet<string> HostsThirdParty;
        public readonly HashSet<string> HostsFirstParty;
        public readonly NetworkRule[] Pool;
        public readonly Dictionary<string, int> DomainHeads;
        public readonly Dictionary<string, int> TokenHeads;
        public readonly int[] Unindexed;

        public RuleBucket(
            HashSet<string> hostsAny,
            HashSet<string> hostsThirdParty,
            HashSet<string> hostsFirstParty,
            NetworkRule[] pool,
            Dictionary<string, int> domainHeads,
            Dictionary<string, int> tokenHeads,
            int[] unindexed)
        {
            HostsAny = hostsAny;
            HostsThirdParty = hostsThirdParty;
            HostsFirstParty = hostsFirstParty;
            Pool = pool;
            DomainHeads = domainHeads;
            TokenHeads = tokenHeads;
            Unindexed = unindexed;
        }

        public bool IsEmpty =>
            HostsAny.Count == 0 && HostsThirdParty.Count == 0 && HostsFirstParty.Count == 0 && Pool.Length == 0;

        public int RuleCount => HostsAny.Count + HostsThirdParty.Count + HostsFirstParty.Count + Pool.Length;
    }

    sealed class RuleBucketBuilder
    {
        public readonly HashSet<string> HostsAny = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> HostsThirdParty = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> HostsFirstParty = new(StringComparer.OrdinalIgnoreCase);
        readonly List<(string? DomainKey, string Pattern, string[]? Inc, string[]? Exc, FilterResourceType Mask, byte Flags)> pending = [];

        public void AddRule(string? domainKey, string pattern, string[]? inc, string[]? exc, FilterResourceType mask, byte flags)
        {
            // Fast-path pure domain-only rules (||domain^ or hosts lines with no path, no domain scope, all subresources, not important).
            if (domainKey is not null
                && pattern.Length == 0
                && inc is null
                && exc is null
                && mask == FilterResourceType.AllSubresources
                && (flags & FlagImportant) == 0)
            {
                var party = flags & (FlagRequireThirdParty | FlagRequireFirstParty);
                if (party == 0) HostsAny.Add(domainKey);
                else if (party == FlagRequireThirdParty) HostsThirdParty.Add(domainKey);
                else if (party == FlagRequireFirstParty) HostsFirstParty.Add(domainKey);
                return;
            }
            pending.Add((domainKey, pattern, inc, exc, mask, flags));
        }

        public RuleBucket Build()
        {
            var pool = new NetworkRule[pending.Count];
            var domainHeads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tokenHeads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unindexed = new List<int>();

            // First pass: count safe-token frequencies across non-domain-anchored rules so we index each rule on its rarest token.
            var tokenFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i].DomainKey is not null) continue;
                CountSafeTokens(pending[i].Pattern, pending[i].Flags, tokenFreq);
            }

            for (var i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                if (item.DomainKey is { } domKey)
                {
                    var prev = domainHeads.TryGetValue(domKey, out var h) ? h : -1;
                    pool[i] = new NetworkRule(item.Pattern, item.Inc, item.Exc, item.Mask, item.Flags, prev);
                    domainHeads[domKey] = i;
                }
                else
                {
                    var bestToken = PickRarestSafeToken(item.Pattern, item.Flags, tokenFreq);
                    if (bestToken is not null)
                    {
                        var prev = tokenHeads.TryGetValue(bestToken, out var h) ? h : -1;
                        pool[i] = new NetworkRule(item.Pattern, item.Inc, item.Exc, item.Mask, item.Flags, prev);
                        tokenHeads[bestToken] = i;
                    }
                    else
                    {
                        pool[i] = new NetworkRule(item.Pattern, item.Inc, item.Exc, item.Mask, item.Flags, -1);
                        unindexed.Add(i);
                    }
                }
            }

            return new RuleBucket(HostsAny, HostsThirdParty, HostsFirstParty, pool, domainHeads, tokenHeads, [.. unindexed]);
        }

        static bool IsCommonToken(ReadOnlySpan<char> tok) =>
            tok.Equals("http", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("https", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("www", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("com", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("net", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("org", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("html", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("php", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("js", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("css", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("png", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            || tok.Equals("gif", StringComparison.OrdinalIgnoreCase);

        static void CountSafeTokens(string pattern, byte flags, Dictionary<string, int> freq)
        {
            var lookup = freq.GetAlternateLookup<ReadOnlySpan<char>>();
            var span = pattern.AsSpan();
            var i = 0;
            while (i < span.Length)
            {
                if (!char.IsAsciiLetterOrDigit(span[i])) { i++; continue; }
                var start = i;
                while (i < span.Length && char.IsAsciiLetterOrDigit(span[i])) i++;
                var len = i - start;
                if (len < 2) continue;
                var leftSafe = start > 0 ? span[start - 1] != '*' : (flags & (FlagLeftAnchor | FlagDomainAnchor)) != 0;
                var rightSafe = i < span.Length ? span[i] != '*' : (flags & FlagRightAnchor) != 0;
                if (!leftSafe || !rightSafe) continue;
                var tok = span.Slice(start, len);
                if (lookup.TryGetValue(tok, out var c)) lookup[tok] = c + 1;
                else freq[tok.ToString().ToLowerInvariant()] = 1;
            }
        }

        static string? PickRarestSafeToken(string pattern, byte flags, Dictionary<string, int> freq)
        {
            var lookup = freq.GetAlternateLookup<ReadOnlySpan<char>>();
            var span = pattern.AsSpan();
            string? best = null;
            var bestScore = int.MaxValue;
            var i = 0;
            while (i < span.Length)
            {
                if (!char.IsAsciiLetterOrDigit(span[i])) { i++; continue; }
                var start = i;
                while (i < span.Length && char.IsAsciiLetterOrDigit(span[i])) i++;
                var len = i - start;
                if (len < 2) continue;
                var leftSafe = start > 0 ? span[start - 1] != '*' : (flags & (FlagLeftAnchor | FlagDomainAnchor)) != 0;
                var rightSafe = i < span.Length ? span[i] != '*' : (flags & FlagRightAnchor) != 0;
                if (!leftSafe || !rightSafe) continue;
                var tok = span.Slice(start, len);
                var f = lookup.TryGetValue(tok, out var c) ? c : int.MaxValue;
                // Prefer non-common, longer tokens when frequencies tie.
                var score = IsCommonToken(tok) ? Math.Min(int.MaxValue - 1, f + 100_000) : f;
                if (len < 3) score = Math.Min(int.MaxValue - 1, score + 10_000);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = tok.ToString().ToLowerInvariant();
                }
            }
            return best;
        }
    }

    static readonly string[] ProceduralMarkers =
    [
        ":has-text(", ":-abp-", ":upward(", ":xpath(", ":matches-css", ":matches-attr(",
        ":matches-path(", ":matches-prop(", ":style(", ":remove(", ":remove-attr(",
        ":remove-class(", ":watch-attr(", ":min-text-length(", ":others(",
        "+js(", "^responseheader(",
    ];

    static readonly HashSet<string> HostsLocalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost",
        "ip6-localhost", "ip6-loopback", "ip6-localnet", "ip6-mcastprefix",
        "ip6-allnodes", "ip6-allrouters", "ip6-allhosts", "0.0.0.0", "255.255.255.255",
    };

    readonly RuleBucket blockRules;
    readonly RuleBucket exceptionRules;
    readonly RuleBucket importantRules;
    readonly string[] genericCosmetics;
    readonly string defaultGenericCss;
    readonly Dictionary<string, ScopedCosmeticRule[]> scopedCosmetics;
    readonly Dictionary<string, HashSet<string>> cosmeticExceptionsByDomain;

    public int Version { get; }
    public PublicSuffixList Psl { get; }
    public bool IsFallback { get; }
    public int NetworkRuleCount => blockRules.RuleCount;
    public int ExceptionRuleCount => exceptionRules.RuleCount;
    public int CosmeticRuleCount { get; }

    FilterEngine(
        PublicSuffixList psl,
        bool isFallback,
        RuleBucket blockRules,
        RuleBucket exceptionRules,
        RuleBucket importantRules,
        string[] genericCosmetics,
        Dictionary<string, ScopedCosmeticRule[]> scopedCosmetics,
        Dictionary<string, HashSet<string>> cosmeticExceptionsByDomain,
        int cosmeticRuleCount)
    {
        Version = Interlocked.Increment(ref nextVersion);
        Psl = psl;
        IsFallback = isFallback;
        this.blockRules = blockRules;
        this.exceptionRules = exceptionRules;
        this.importantRules = importantRules;
        this.genericCosmetics = genericCosmetics;
        defaultGenericCss = BuildBatchedCss(genericCosmetics, null);
        this.scopedCosmetics = scopedCosmetics;
        this.cosmeticExceptionsByDomain = cosmeticExceptionsByDomain;
        CosmeticRuleCount = cosmeticRuleCount;
    }

    public static FilterResourceType MapContext(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Script => FilterResourceType.Script,
        CoreWebView2WebResourceContext.Image => FilterResourceType.Image,
        CoreWebView2WebResourceContext.Stylesheet => FilterResourceType.Stylesheet,
        CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch or CoreWebView2WebResourceContext.EventSource => FilterResourceType.XmlHttpRequest,
        CoreWebView2WebResourceContext.Document => FilterResourceType.Subdocument,
        CoreWebView2WebResourceContext.Media => FilterResourceType.Media,
        CoreWebView2WebResourceContext.Font => FilterResourceType.Font,
        CoreWebView2WebResourceContext.Websocket => FilterResourceType.Websocket,
        CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => FilterResourceType.Ping,
        CoreWebView2WebResourceContext.All => FilterResourceType.AllSubresources,
        _ => FilterResourceType.Other,
    };

    public static FilterEngine CreateFallback(PublicSuffixList? psl = null)
    {
        var sb = new StringBuilder(4096);
        foreach (var d in Shield.BuiltInBlockedDomains)
            sb.Append("||").Append(d).Append("^$third-party\n");
        foreach (var sel in Shield.BuiltInGenericSelectors)
            sb.Append("##").Append(sel).Append('\n');
        foreach (var (sites, sel) in Shield.BuiltInSiteSelectors)
            sb.Append(string.Join(',', sites)).Append("##").Append(sel).Append('\n');
        foreach (var sel in Shield.BuiltInRedditSelectors)
            sb.Append("reddit.com##").Append(sel).Append('\n');
        return CompileCore([sb.ToString()], psl ?? PublicSuffixList.BuiltIn, isFallback: true);
    }

    public static FilterEngine Compile(IEnumerable<string> listContents, PublicSuffixList? psl = null, bool fallbackWhenEmpty = true)
    {
        var resolvedPsl = psl ?? PublicSuffixList.BuiltIn;
        var compiled = CompileCore(listContents, resolvedPsl, isFallback: false);
        if (fallbackWhenEmpty && compiled.NetworkRuleCount == 0 && compiled.CosmeticRuleCount == 0)
            return CreateFallback(resolvedPsl);
        return compiled;
    }

    static FilterEngine CompileCore(IEnumerable<string> listContents, PublicSuffixList psl, bool isFallback)
    {
        var blockBuilder = new RuleBucketBuilder();
        var excBuilder = new RuleBucketBuilder();
        var impBuilder = new RuleBucketBuilder();
        var genericSet = new HashSet<string>(StringComparer.Ordinal);
        var genericExcSet = new HashSet<string>(StringComparer.Ordinal);
        var scopedMap = new Dictionary<string, List<ScopedCosmeticRule>>(StringComparer.OrdinalIgnoreCase);
        var cosExcByDomain = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var internedDomains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scopedCount = 0;

        string InternDomain(ReadOnlySpan<char> d)
        {
            var lookup = internedDomains.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(d, out var existing)) return existing;
            var s = d.ToString().ToLowerInvariant();
            internedDomains[s] = s;
            return s;
        }

        foreach (var content in listContents)
        {
            if (string.IsNullOrEmpty(content)) continue;
            foreach (var rawLine in content.AsSpan().EnumerateLines())
            {
                var line = rawLine.Trim();
                if (line.IsEmpty) continue;
                var first = line[0];
                if (first == '!' || first == '[') continue;

                // Cosmetic or hosts-comment check.
                var hashIdx = line.IndexOf('#');
                if (hashIdx >= 0)
                {
                    if (TryParseCosmetic(line, hashIdx, genericSet, genericExcSet, scopedMap, cosExcByDomain, InternDomain, ref scopedCount))
                        continue;
                    if (first == '#') continue; // hosts comment line
                }

                // Hosts-format line: "0.0.0.0 ads.example.com" or "127.0.0.1 tracker.example.com"
                if (TryParseHostsLine(line, blockBuilder, InternDomain))
                    continue;

                ParseNetworkLine(line, blockBuilder, excBuilder, impBuilder, InternDomain);
            }
        }

        if (genericExcSet.Count > 0)
            genericSet.ExceptWith(genericExcSet);

        var genericArray = new string[genericSet.Count];
        genericSet.CopyTo(genericArray);

        var scopedFinal = new Dictionary<string, ScopedCosmeticRule[]>(scopedMap.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in scopedMap)
            scopedFinal[kv.Key] = [.. kv.Value];

        return new FilterEngine(
            psl,
            isFallback,
            blockBuilder.Build(),
            excBuilder.Build(),
            impBuilder.Build(),
            genericArray,
            scopedFinal,
            cosExcByDomain,
            genericArray.Length + scopedCount);
    }

    static bool TryParseCosmetic(
        ReadOnlySpan<char> line,
        int hashIdx,
        HashSet<string> genericSet,
        HashSet<string> genericExcSet,
        Dictionary<string, List<ScopedCosmeticRule>> scopedMap,
        Dictionary<string, HashSet<string>> cosExcByDomain,
        Func<ReadOnlySpan<char>, string> internDomain,
        ref int scopedCount)
    {
        // Only treat '#' as a cosmetic delimiter if preceded by valid domain-list chars (or start of line).
        if (hashIdx + 1 >= line.Length) return false;
        var next = line[hashIdx + 1];
        if (next is not ('#' or '@' or '?' or '$' or '%')) return false;

        for (var i = 0; i < hashIdx; i++)
        {
            var c = line[i];
            if (c is '/' or ':' or '?' or '=' or '&' or ' ' or '\t') return false;
        }

        bool isException;
        ReadOnlySpan<char> selectorSpan;
        if (next == '#')
        {
            isException = false;
            selectorSpan = line[(hashIdx + 2)..];
        }
        else if (next == '@' && hashIdx + 2 < line.Length && line[hashIdx + 2] == '#')
        {
            isException = true;
            selectorSpan = line[(hashIdx + 3)..];
        }
        else
        {
            // #?#, #$#, #%#, #@?#, etc. -> unsupported extended cosmetic; consume and ignore.
            return true;
        }

        selectorSpan = selectorSpan.Trim();
        if (selectorSpan.IsEmpty) return true;
        if (selectorSpan[0] == '^' || selectorSpan.StartsWith("+js(", StringComparison.OrdinalIgnoreCase))
            return true; // HTML filter or scriptlet -> unsupported
        if (!IsSupportedCssSelector(selectorSpan)) return true;

        var selector = selectorSpan.ToString();
        var domainPart = line[..hashIdx].Trim();
        if (domainPart.IsEmpty)
        {
            if (isException) genericExcSet.Add(selector);
            else genericSet.Add(selector);
            return true;
        }

        List<string>? include = null;
        List<string>? exclude = null;
        var hadPositive = false;
        var rem = domainPart;
        while (!rem.IsEmpty)
        {
            var comma = rem.IndexOf(',');
            var tok = (comma < 0 ? rem : rem[..comma]).Trim();
            rem = comma < 0 ? ReadOnlySpan<char>.Empty : rem[(comma + 1)..];
            if (tok.IsEmpty) continue;
            var neg = tok[0] == '~';
            if (neg) tok = tok[1..].Trim();
            else hadPositive = true;
            if (tok.IsEmpty || tok.IndexOf('*') >= 0 || tok[0] == '/') continue; // skip entity/regex domains
            var dom = internDomain(tok);
            if (neg) (exclude ??= []).Add(dom);
            else (include ??= []).Add(dom);
        }

        if (isException)
        {
            if (include is not null)
            {
                foreach (var dom in include)
                {
                    if (!cosExcByDomain.TryGetValue(dom, out var set))
                        cosExcByDomain[dom] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(selector);
                }
            }
            return true;
        }

        if (include is { Count: > 0 })
        {
            var excArr = exclude is { Count: > 0 } ? exclude.ToArray() : null;
            var rule = new ScopedCosmeticRule(selector, excArr);
            foreach (var dom in include)
            {
                if (!scopedMap.TryGetValue(dom, out var list))
                    scopedMap[dom] = list = [];
                list.Add(rule);
            }
            scopedCount++;
        }
        else if (!hadPositive && exclude is { Count: > 0 })
        {
            // ~example.com##.ad -> generic selector with domain exception on example.com
            genericSet.Add(selector);
            foreach (var dom in exclude)
            {
                if (!cosExcByDomain.TryGetValue(dom, out var set))
                    cosExcByDomain[dom] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(selector);
            }
        }
        return true;
    }

    static bool IsSupportedCssSelector(ReadOnlySpan<char> sel)
    {
        if (sel.Length > 512) return false;
        // Reject braces (would break out of CSS rule block) and procedural pseudo-classes.
        if (sel.IndexOfAny('{', '}') >= 0) return false;
        foreach (var marker in ProceduralMarkers)
            if (sel.Contains(marker.AsSpan(), StringComparison.OrdinalIgnoreCase)) return false;

        // Basic bracket/quote/paren balance check so a broken selector doesn't invalidate its CSS chunk.
        var paren = 0;
        var bracket = 0;
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < sel.Length; i++)
        {
            var c = sel[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '(') paren++;
            else if (c == ')') { if (--paren < 0) return false; }
            else if (c == '[') bracket++;
            else if (c == ']') { if (--bracket < 0) return false; }
        }
        return quote == '\0' && paren == 0 && bracket == 0 && !escaped;
    }

    static bool TryParseHostsLine(ReadOnlySpan<char> line, RuleBucketBuilder blockBuilder, Func<ReadOnlySpan<char>, string> internDomain)
    {
        var prefixLen = 0;
        if (line.StartsWith("0.0.0.0")) prefixLen = 7;
        else if (line.StartsWith("127.0.0.1")) prefixLen = 9;
        else if (line.StartsWith("::1")) prefixLen = 3;
        else if (line.StartsWith("::")) prefixLen = 2;
        else return false;

        if (prefixLen >= line.Length || (line[prefixLen] != ' ' && line[prefixLen] != '\t'))
            return false;

        var rest = line[prefixLen..];
        var comment = rest.IndexOf('#');
        if (comment >= 0) rest = rest[..comment];
        rest = rest.Trim();
        if (rest.IsEmpty) return true;

        var addedAny = false;
        while (!rest.IsEmpty)
        {
            var ws = rest.IndexOfAny(' ', '\t');
            var host = (ws < 0 ? rest : rest[..ws]).TrimEnd('.');
            rest = ws < 0 ? ReadOnlySpan<char>.Empty : rest[(ws + 1)..].TrimStart();
            if (host.IsEmpty || !IsValidHostname(host) || HostsLocalNames.GetAlternateLookup<ReadOnlySpan<char>>().Contains(host))
                continue;
            blockBuilder.AddRule(internDomain(host), "", null, null, FilterResourceType.AllSubresources, FlagDomainAnchor);
            addedAny = true;
        }
        return addedAny || true;
    }

    static bool IsValidHostname(ReadOnlySpan<char> host)
    {
        if (host.IsEmpty || host.Length > 253 || host[0] == '.' || host[^1] == '.') return false;
        var hasDot = false;
        for (var i = 0; i < host.Length; i++)
        {
            var c = host[i];
            if (c == '.')
            {
                if (i > 0 && host[i - 1] == '.') return false;
                hasDot = true;
            }
            else if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        }
        return hasDot;
    }

    static void ParseNetworkLine(
        ReadOnlySpan<char> line,
        RuleBucketBuilder blockBuilder,
        RuleBucketBuilder excBuilder,
        RuleBucketBuilder impBuilder,
        Func<ReadOnlySpan<char>, string> internDomain)
    {
        for (var i = 0; i < line.Length; i++)
            if (line[i] < 0x20 || line[i] == 0x7F) return;

        var isException = false;
        if (line.StartsWith("@@"))
        {
            isException = true;
            line = line[2..];
            if (line.IsEmpty) return;
        }

        // Split options at the last '$' unless it's a regex pattern /.../
        ReadOnlySpan<char> patternPart = line;
        ReadOnlySpan<char> optionsPart = ReadOnlySpan<char>.Empty;
        var dollar = line.LastIndexOf('$');
        if (dollar >= 0)
        {
            // Regex rules look like /pattern/$options or /pattern/
            if (line[0] == '/' && line.Length > 2 && line[..dollar].EndsWith("/")) return;
            patternPart = line[..dollar];
            optionsPart = line[(dollar + 1)..];
        }
        else if (line[0] == '/' && line.Length > 2 && line[^1] == '/')
        {
            return; // Unsupported regex network rule
        }

        // Parse options — if any option is unsupported, skip the entire rule safely!
        byte flags = 0;
        var includeTypes = FilterResourceType.None;
        var excludeTypes = FilterResourceType.None;
        string[]? includeDomains = null;
        string[]? excludeDomains = null;

        if (!optionsPart.IsEmpty)
        {
            if (!TryParseOptions(optionsPart, internDomain, ref flags, ref includeTypes, ref excludeTypes, out includeDomains, out excludeDomains))
                return;
        }

        FilterResourceType typeMask;
        if (includeTypes != FilterResourceType.None)
            typeMask = includeTypes & ~excludeTypes;
        else if (excludeTypes != FilterResourceType.None)
            typeMask = FilterResourceType.AllSubresources & ~excludeTypes;
        else
            typeMask = FilterResourceType.AllSubresources;

        // If a rule ONLY targets top-level Document (e.g. $document on a block rule), skip because top-level navs are exempt.
        if (!isException && (typeMask & FilterResourceType.AllSubresources) == 0)
            return;
        if (typeMask == FilterResourceType.None) return;

        // Strip anchors from patternPart
        if (patternPart.StartsWith("||"))
        {
            flags |= FlagDomainAnchor;
            patternPart = patternPart[2..];
        }
        else if (patternPart.StartsWith("|"))
        {
            flags |= FlagLeftAnchor;
            patternPart = patternPart[1..];
        }

        if (patternPart.EndsWith("|"))
        {
            flags |= FlagRightAnchor;
            patternPart = patternPart[..^1];
        }

        // Trim redundant leading/trailing '*' when unanchored
        if ((flags & (FlagLeftAnchor | FlagDomainAnchor)) == 0)
            patternPart = patternPart.TrimStart('*');
        if ((flags & FlagRightAnchor) == 0)
            patternPart = patternPart.TrimEnd('*');

        if (patternPart.IsEmpty && includeDomains is null) return;

        string? domainKey = null;
        string storedPattern;

        if ((flags & FlagDomainAnchor) != 0)
        {
            // Extract anchored domain up to first separator/wildcard/path char.
            var end = 0;
            while (end < patternPart.Length)
            {
                var c = patternPart[end];
                if (char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_') end++;
                else break;
            }
            var domSpan = patternPart[..end].TrimEnd('.');
            if (!domSpan.IsEmpty && IsValidHostname(domSpan))
            {
                var rest = patternPart[end..];
                // Pure domain rule: ||example.com^ or ||example.com or ||example.com^|
                if (rest.IsEmpty || (rest.Length == 1 && rest[0] == '^'))
                {
                    domainKey = internDomain(domSpan);
                    storedPattern = "";
                }
                else if (rest[0] is '^' or '/' or ':' or '?' or '*')
                {
                    domainKey = internDomain(domSpan);
                    storedPattern = (flags & FlagMatchCase) != 0 ? patternPart.ToString() : patternPart.ToString().ToLowerInvariant();
                    if (storedPattern.AsSpan().IndexOfAny('*', '^') >= 0) flags |= FlagHasSpecial;
                }
                else
                {
                    storedPattern = (flags & FlagMatchCase) != 0 ? patternPart.ToString() : patternPart.ToString().ToLowerInvariant();
                    if (storedPattern.AsSpan().IndexOfAny('*', '^') >= 0) flags |= FlagHasSpecial;
                }
            }
            else
            {
                storedPattern = (flags & FlagMatchCase) != 0 ? patternPart.ToString() : patternPart.ToString().ToLowerInvariant();
                if (storedPattern.AsSpan().IndexOfAny('*', '^') >= 0) flags |= FlagHasSpecial;
            }
        }
        else
        {
            storedPattern = (flags & FlagMatchCase) != 0 ? patternPart.ToString() : patternPart.ToString().ToLowerInvariant();
            if (storedPattern.AsSpan().IndexOfAny('*', '^') >= 0) flags |= FlagHasSpecial;
        }

        if (isException)
        {
            excBuilder.AddRule(domainKey, storedPattern, includeDomains, excludeDomains, typeMask, flags);
        }
        else
        {
            blockBuilder.AddRule(domainKey, storedPattern, includeDomains, excludeDomains, typeMask, flags);
            if ((flags & FlagImportant) != 0)
                impBuilder.AddRule(domainKey, storedPattern, includeDomains, excludeDomains, typeMask, flags);
        }
    }

    static bool TryParseOptions(
        ReadOnlySpan<char> optionsPart,
        Func<ReadOnlySpan<char>, string> internDomain,
        ref byte flags,
        ref FilterResourceType includeTypes,
        ref FilterResourceType excludeTypes,
        out string[]? includeDomains,
        out string[]? excludeDomains)
    {
        includeDomains = null;
        excludeDomains = null;
        List<string>? incList = null;
        List<string>? excList = null;

        var rem = optionsPart;
        while (!rem.IsEmpty)
        {
            var comma = rem.IndexOf(',');
            var opt = (comma < 0 ? rem : rem[..comma]).Trim();
            rem = comma < 0 ? ReadOnlySpan<char>.Empty : rem[(comma + 1)..];
            if (opt.IsEmpty) continue;

            var eq = opt.IndexOf('=');
            if (eq >= 0)
            {
                var name = opt[..eq].Trim();
                var val = opt[(eq + 1)..].Trim();
                if (name.Equals("domain", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("from", StringComparison.OrdinalIgnoreCase))
                {
                    if (val.IsEmpty) return false;
                    var hadPositive = false;
                    var addedPositive = false;
                    var dRem = val;
                    while (!dRem.IsEmpty)
                    {
                        var pipe = dRem.IndexOf('|');
                        var dTok = (pipe < 0 ? dRem : dRem[..pipe]).Trim();
                        dRem = pipe < 0 ? ReadOnlySpan<char>.Empty : dRem[(pipe + 1)..];
                        if (dTok.IsEmpty) continue;
                        var neg = dTok[0] == '~';
                        if (neg) dTok = dTok[1..].Trim();
                        else hadPositive = true;
                        if (dTok.IsEmpty || dTok.IndexOf('*') >= 0 || dTok[0] == '/') continue;
                        var d = internDomain(dTok);
                        if (neg) (excList ??= []).Add(d);
                        else { (incList ??= []).Add(d); addedPositive = true; }
                    }
                    // If positive domains were specified but none were supported (e.g., all regex/entity), skip rule.
                    if (hadPositive && !addedPositive) return false;
                    continue;
                }
                // Any other parameterized option ($redirect=, $csp=, $removeparam=, $replace=, $header=, $to=, $denyallow=, etc.) -> skip rule!
                return false;
            }

            var negated = opt[0] == '~';
            var key = negated ? opt[1..] : opt;

            if (key.Equals("third-party", StringComparison.OrdinalIgnoreCase) || key.Equals("3p", StringComparison.OrdinalIgnoreCase))
            {
                flags |= negated ? FlagRequireFirstParty : FlagRequireThirdParty;
            }
            else if (key.Equals("first-party", StringComparison.OrdinalIgnoreCase) || key.Equals("1p", StringComparison.OrdinalIgnoreCase))
            {
                flags |= negated ? FlagRequireThirdParty : FlagRequireFirstParty;
            }
            else if (!negated && key.Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                flags |= FlagImportant;
            }
            else if (!negated && key.Equals("match-case", StringComparison.OrdinalIgnoreCase))
            {
                flags |= FlagMatchCase;
            }
            else if (!negated && key.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                includeTypes |= FilterResourceType.AllSubresources;
            }
            else if (TryMapTypeOption(key, out var rtype))
            {
                if (negated) excludeTypes |= rtype;
                else includeTypes |= rtype;
            }
            else
            {
                // Unsupported option ($popup, $badfilter, $generichide, $elemhide, $csp, $redirect, $empty, etc.) -> skip rule safely!
                return false;
            }
        }

        if (incList is { Count: > 0 }) includeDomains = [.. incList];
        if (excList is { Count: > 0 }) excludeDomains = [.. excList];
        return true;
    }

    static bool TryMapTypeOption(ReadOnlySpan<char> key, out FilterResourceType type)
    {
        if (key.Equals("script", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Script; return true; }
        if (key.Equals("image", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Image; return true; }
        if (key.Equals("stylesheet", StringComparison.OrdinalIgnoreCase) || key.Equals("css", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Stylesheet; return true; }
        if (key.Equals("xmlhttprequest", StringComparison.OrdinalIgnoreCase) || key.Equals("xhr", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.XmlHttpRequest; return true; }
        if (key.Equals("subdocument", StringComparison.OrdinalIgnoreCase) || key.Equals("frame", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Subdocument; return true; }
        if (key.Equals("media", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Media; return true; }
        if (key.Equals("font", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Font; return true; }
        if (key.Equals("websocket", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Websocket; return true; }
        if (key.Equals("ping", StringComparison.OrdinalIgnoreCase) || key.Equals("beacon", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Ping; return true; }
        if (key.Equals("other", StringComparison.OrdinalIgnoreCase) || key.Equals("object", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Other; return true; }
        if (key.Equals("document", StringComparison.OrdinalIgnoreCase) || key.Equals("doc", StringComparison.OrdinalIgnoreCase)) { type = FilterResourceType.Document; return true; }
        type = FilterResourceType.None;
        return false;
    }

    public bool ShouldBlock(Uri request, Uri? page, CoreWebView2WebResourceContext context = CoreWebView2WebResourceContext.All)
    {
        if (!request.IsAbsoluteUri) return false;
        if (request.Scheme != Uri.UriSchemeHttp && request.Scheme != Uri.UriSchemeHttps) return false;
        var reqHost = request.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? request.Host.AsSpan()
            : request.IdnHost.AsSpan();
        var pageHost = page is not null && page.IsAbsoluteUri
            ? (page.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ? page.Host.AsSpan() : page.IdnHost.AsSpan())
            : ReadOnlySpan<char>.Empty;
        var isThirdParty = Psl.IsThirdParty(request, page);
        return ShouldBlockCore(request.AbsoluteUri, reqHost, pageHost, isThirdParty, MapContext(context));
    }

    public bool ShouldBlock(string requestUrl, ReadOnlySpan<char> requestHost, ReadOnlySpan<char> pageHost, FilterResourceType resourceType)
    {
        var isThirdParty = Psl.IsThirdParty(requestHost, pageHost);
        return ShouldBlockCore(requestUrl, requestHost, pageHost, isThirdParty, resourceType);
    }

    bool ShouldBlockCore(
        string requestUrl,
        ReadOnlySpan<char> requestHost,
        ReadOnlySpan<char> pageHost,
        bool isThirdParty,
        FilterResourceType resourceType)
    {
        if (!MatchesBucket(blockRules, requestUrl, requestHost, pageHost, isThirdParty, resourceType, out var matchedImportant))
            return false;
        if (matchedImportant)
            return true;
        if (exceptionRules.IsEmpty)
            return true;
        if (!MatchesBucket(exceptionRules, requestUrl, requestHost, pageHost, isThirdParty, resourceType, out _))
            return true;
        // An exception rule matched; $important block rules override exceptions.
        return !importantRules.IsEmpty && MatchesBucket(importantRules, requestUrl, requestHost, pageHost, isThirdParty, resourceType, out _);
    }

    static bool MatchesBucket(
        RuleBucket bucket,
        string url,
        ReadOnlySpan<char> reqHost,
        ReadOnlySpan<char> pageHost,
        bool isThirdParty,
        FilterResourceType resourceType,
        out bool matchedImportant)
    {
        matchedImportant = false;
        var urlSpan = url.AsSpan();
        var hostStart = FindHostStart(urlSpan);
        var hostEnd = FindHostEnd(urlSpan, hostStart);

        // 1. Walk up request host labels: O(labels) zero-alloc hash lookup.
        var anyLookup = bucket.HostsAny.GetAlternateLookup<ReadOnlySpan<char>>();
        var tpLookup = bucket.HostsThirdParty.GetAlternateLookup<ReadOnlySpan<char>>();
        var fpLookup = bucket.HostsFirstParty.GetAlternateLookup<ReadOnlySpan<char>>();
        var domLookup = bucket.DomainHeads.GetAlternateLookup<ReadOnlySpan<char>>();
        var hasAny = bucket.HostsAny.Count > 0;
        var hasTp = isThirdParty && bucket.HostsThirdParty.Count > 0;
        var hasFp = !isThirdParty && bucket.HostsFirstParty.Count > 0;
        var hasDom = bucket.DomainHeads.Count > 0;

        if ((hasAny || hasTp || hasFp || hasDom) && !reqHost.IsEmpty)
        {
            var span = reqHost.TrimEnd('.');
            var allowSubresource = (resourceType & FilterResourceType.AllSubresources) != 0;
            while (!span.IsEmpty)
            {
                if (allowSubresource)
                {
                    if ((hasAny && anyLookup.Contains(span))
                        || (hasTp && tpLookup.Contains(span))
                        || (hasFp && fpLookup.Contains(span)))
                        return true;
                }
                if (hasDom && domLookup.TryGetValue(span, out var idx))
                {
                    while (idx >= 0)
                    {
                        ref readonly var rule = ref bucket.Pool[idx];
                        if (MatchesRule(in rule, urlSpan, hostStart, hostEnd, pageHost, isThirdParty, resourceType))
                        {
                            matchedImportant = (rule.Flags & FlagImportant) != 0;
                            return true;
                        }
                        idx = rule.Next;
                    }
                }
                var dot = span.IndexOf('.');
                if (dot < 0) break;
                span = span[(dot + 1)..];
            }
        }

        // 2. Scan URL alphanumeric tokens against token index (zero-alloc).
        if (bucket.TokenHeads.Count > 0)
        {
            var tokLookup = bucket.TokenHeads.GetAlternateLookup<ReadOnlySpan<char>>();
            var i = 0;
            while (i < urlSpan.Length)
            {
                if (!char.IsAsciiLetterOrDigit(urlSpan[i])) { i++; continue; }
                var start = i;
                while (i < urlSpan.Length && char.IsAsciiLetterOrDigit(urlSpan[i])) i++;
                if (i - start < 2) continue;
                var tok = urlSpan.Slice(start, i - start);
                if (tokLookup.TryGetValue(tok, out var idx))
                {
                    while (idx >= 0)
                    {
                        ref readonly var rule = ref bucket.Pool[idx];
                        if (MatchesRule(in rule, urlSpan, hostStart, hostEnd, pageHost, isThirdParty, resourceType))
                        {
                            matchedImportant = (rule.Flags & FlagImportant) != 0;
                            return true;
                        }
                        idx = rule.Next;
                    }
                }
            }
        }

        // 3. Small unindexed fallback rules.
        var unindexed = bucket.Unindexed;
        for (var i = 0; i < unindexed.Length; i++)
        {
            ref readonly var rule = ref bucket.Pool[unindexed[i]];
            if (MatchesRule(in rule, urlSpan, hostStart, hostEnd, pageHost, isThirdParty, resourceType))
            {
                matchedImportant = (rule.Flags & FlagImportant) != 0;
                return true;
            }
        }

        return false;
    }

    static bool MatchesRule(
        ref readonly NetworkRule rule,
        ReadOnlySpan<char> url,
        int hostStart,
        int hostEnd,
        ReadOnlySpan<char> pageHost,
        bool isThirdParty,
        FilterResourceType resourceType)
    {
        if ((rule.TypeMask & resourceType) == 0) return false;
        var flags = rule.Flags;
        if ((flags & FlagRequireThirdParty) != 0 && !isThirdParty) return false;
        if ((flags & FlagRequireFirstParty) != 0 && isThirdParty) return false;
        if (rule.IncludeDomains is not null || rule.ExcludeDomains is not null)
        {
            if (!MatchesPageDomains(pageHost, rule.IncludeDomains, rule.ExcludeDomains))
                return false;
        }
        if (rule.Pattern.Length == 0) return true; // pure domain rule already matched via domain bucket

        return MatchPattern(rule.Pattern.AsSpan(), flags, url, hostStart, hostEnd);
    }

    static bool MatchesPageDomains(ReadOnlySpan<char> pageHost, string[]? include, string[]? exclude)
    {
        var clean = pageHost.TrimEnd('.');
        if (include is not null)
        {
            if (clean.IsEmpty) return false;
            var ok = false;
            for (var i = 0; i < include.Length; i++)
            {
                if (DomainMatches(clean, include[i].AsSpan())) { ok = true; break; }
            }
            if (!ok) return false;
        }
        if (exclude is not null && !clean.IsEmpty)
        {
            for (var i = 0; i < exclude.Length; i++)
            {
                if (DomainMatches(clean, exclude[i].AsSpan())) return false;
            }
        }
        return true;
    }

    internal static bool DomainMatches(ReadOnlySpan<char> host, ReadOnlySpan<char> domain) =>
        host.Length >= domain.Length
        && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
        && (host.Length == domain.Length || host[host.Length - domain.Length - 1] == '.');

    static int FindHostStart(ReadOnlySpan<char> url)
    {
        var idx = url.IndexOf("://");
        return idx >= 0 ? idx + 3 : 0;
    }

    static int FindHostEnd(ReadOnlySpan<char> url, int hostStart)
    {
        for (var i = hostStart; i < url.Length; i++)
        {
            if (url[i] is '/' or '?' or '#' or ':') return i;
        }
        return url.Length;
    }

    static bool MatchPattern(ReadOnlySpan<char> pattern, byte flags, ReadOnlySpan<char> url, int hostStart, int hostEnd)
    {
        var matchCase = (flags & FlagMatchCase) != 0;
        var rightAnchor = (flags & FlagRightAnchor) != 0;
        var hasSpecial = (flags & FlagHasSpecial) != 0;

        if ((flags & FlagDomainAnchor) != 0)
        {
            // Try matching at hostStart and after each '.' inside the host.
            var pos = hostStart;
            while (pos < hostEnd)
            {
                if (MatchAt(pattern, url[pos..], matchCase, rightAnchor, hasSpecial))
                    return true;
                var dot = url[pos..hostEnd].IndexOf('.');
                if (dot < 0) break;
                pos += dot + 1;
            }
            return false;
        }

        if ((flags & FlagLeftAnchor) != 0)
            return MatchAt(pattern, url, matchCase, rightAnchor, hasSpecial);

        if (!hasSpecial)
        {
            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return rightAnchor ? url.EndsWith(pattern, cmp) : url.Contains(pattern, cmp);
        }

        // Unanchored pattern with '*' or '^': scan starting positions using first literal char when possible.
        var first = pattern[0];
        if (first is not ('*' or '^'))
        {
            var firstUpper = matchCase ? first : char.ToUpperInvariant(first);
            for (var i = 0; i < url.Length; i++)
            {
                var c = url[i];
                if (c != first && (matchCase || char.ToUpperInvariant(c) != firstUpper)) continue;
                if (MatchAt(pattern, url[i..], matchCase, rightAnchor, hasSpecial)) return true;
            }
            return false;
        }

        for (var i = 0; i <= url.Length; i++)
        {
            if (MatchAt(pattern, url[i..], matchCase, rightAnchor, hasSpecial)) return true;
        }
        return false;
    }

    static bool MatchAt(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, bool matchCase, bool rightAnchor, bool hasSpecial)
    {
        if (!hasSpecial)
        {
            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return rightAnchor ? text.Equals(pattern, cmp) : text.StartsWith(pattern, cmp);
        }

        var pi = 0;
        var ti = 0;
        var starPi = -1;
        var starTi = 0;

        while (pi < pattern.Length)
        {
            var pc = pattern[pi];
            if (pc == '*')
            {
                starPi = ++pi;
                starTi = ti;
                if (pi == pattern.Length && !rightAnchor) return true;
                continue;
            }

            if (pc == '^')
            {
                if (ti == text.Length)
                {
                    pi++;
                    continue;
                }
                if (IsSeparator(text[ti]))
                {
                    pi++;
                    ti++;
                    continue;
                }
            }
            else if (ti < text.Length && CharEquals(text[ti], pc, matchCase))
            {
                pi++;
                ti++;
                continue;
            }

            if (starPi >= 0 && starTi < text.Length)
            {
                pi = starPi;
                ti = ++starTi;
                continue;
            }
            return false;
        }

        if (!rightAnchor || ti == text.Length) return true;

        // If right-anchored and there was a '*', advance starTi to reach end of text.
        while (starPi >= 0 && starTi < text.Length)
        {
            pi = starPi;
            ti = ++starTi;
            while (pi < pattern.Length)
            {
                var pc = pattern[pi];
                if (pc == '*')
                {
                    starPi = ++pi;
                    starTi = ti;
                    continue;
                }
                if (pc == '^')
                {
                    if (ti == text.Length) { pi++; continue; }
                    if (IsSeparator(text[ti])) { pi++; ti++; continue; }
                }
                else if (ti < text.Length && CharEquals(text[ti], pc, matchCase))
                {
                    pi++;
                    ti++;
                    continue;
                }
                break;
            }
            if (pi == pattern.Length && ti == text.Length) return true;
        }

        return false;
    }

    static bool IsSeparator(char c) => !(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '%');

    static bool CharEquals(char textChar, char patternChar, bool matchCase) =>
        textChar == patternChar || (!matchCase && AsciiLower(textChar) == patternChar);

    static char AsciiLower(char c) => (uint)(c - 'A') <= ('Z' - 'A') ? (char)(c | 0x20) : c;

    public string? BuildCosmeticCssForHost(ReadOnlySpan<char> host)
    {
        var clean = host.TrimEnd('.');
        HashSet<string>? hostExceptions = null;
        List<string>? hostScoped = null;

        if (!clean.IsEmpty)
        {
            var excLookup = cosmeticExceptionsByDomain.GetAlternateLookup<ReadOnlySpan<char>>();
            var scopedLookup = scopedCosmetics.GetAlternateLookup<ReadOnlySpan<char>>();
            var span = clean;
            while (!span.IsEmpty)
            {
                if (cosmeticExceptionsByDomain.Count > 0 && excLookup.TryGetValue(span, out var excs))
                {
                    hostExceptions ??= new HashSet<string>(StringComparer.Ordinal);
                    hostExceptions.UnionWith(excs);
                }
                if (scopedCosmetics.Count > 0 && scopedLookup.TryGetValue(span, out var rules))
                {
                    foreach (var r in rules)
                    {
                        if (r.ExcludeDomains is not null && MatchesAnyDomain(clean, r.ExcludeDomains))
                            continue;
                        (hostScoped ??= []).Add(r.Selector);
                    }
                }
                var dot = span.IndexOf('.');
                if (dot < 0) break;
                span = span[(dot + 1)..];
            }
        }

        var genericCss = hostExceptions is null
            ? defaultGenericCss
            : BuildBatchedCss(genericCosmetics, hostExceptions);

        var scopedCss = hostScoped is { Count: > 0 }
            ? BuildBatchedCss(hostScoped, hostExceptions)
            : "";

        if (genericCss.Length == 0 && scopedCss.Length == 0) return null;
        if (scopedCss.Length == 0) return genericCss;
        if (genericCss.Length == 0) return scopedCss;
        return genericCss + "\n" + scopedCss;
    }

    static bool MatchesAnyDomain(ReadOnlySpan<char> host, string[] domains)
    {
        for (var i = 0; i < domains.Length; i++)
            if (DomainMatches(host, domains[i].AsSpan())) return true;
        return false;
    }

    static string BuildBatchedCss(IReadOnlyList<string> selectors, HashSet<string>? exclude)
    {
        if (selectors.Count == 0) return "";
        var sb = new StringBuilder(Math.Min(selectors.Count * 24, 262_144));
        var inBatch = 0;
        for (var i = 0; i < selectors.Count; i++)
        {
            var sel = selectors[i];
            if (exclude is not null && exclude.Contains(sel)) continue;
            if (inBatch > 0) sb.Append(',');
            sb.Append(sel);
            inBatch++;
            if (inBatch >= CosmeticBatchSize)
            {
                sb.Append("{display:none!important}\n");
                inBatch = 0;
            }
        }
        if (inBatch > 0) sb.Append("{display:none!important}");
        return sb.ToString();
    }

    public string? BuildCosmeticScriptForHost(ReadOnlySpan<char> host)
    {
        var css = BuildCosmeticCssForHost(host);
        if (string.IsNullOrEmpty(css)) return null;
        var cssJson = JsonSerializer.Serialize(css);
        var hostJson = JsonSerializer.Serialize(host.TrimEnd('.').ToString().ToLowerInvariant());
        // Inject into documentElement if <head> doesn't exist yet at document creation; style id keeps it idempotent and updatable.
        return $$"""
            (() => {
              try {
                if (!(document instanceof HTMLDocument)) return;
                const target = {{hostJson}};
                const h = (location.hostname || '').toLowerCase().replace(/\.+$/, '');
                if (target && h !== target) return;
                const id = 'search-shield-css';
                const css = {{cssJson}};
                const apply = () => {
                  const existing = document.getElementById(id);
                  if (existing) { existing.textContent = css; return; }
                  const root = document.head || document.documentElement;
                  if (!root) return;
                  const s = document.createElement('style');
                  s.id = id;
                  s.textContent = css;
                  root.appendChild(s);
                };
                if (document.head || document.documentElement) apply();
                else document.addEventListener('DOMContentLoaded', apply, { once: true });
              } catch {}
            })();
            """;
    }

    public static FilterBenchmarkResult RunBenchmark(FilterEngine engine, double engineMemoryMB, int sampleCount = 3000)
    {
        var pages = new[]
        {
            "www.theguardian.com", "news.bbc.co.uk", "www.nytimes.com", "github.com",
            "docs.microsoft.com", "www.reddit.com", "shop.example.co.uk", "blog.example.com.au",
            "user.github.io", "en.wikipedia.org",
        };
        var hosts = new[]
        {
            "pagead2.googlesyndication.com", "securepubads.g.doubleclick.net", "www.google-analytics.com",
            "cdn.jsdelivr.net", "images.unsplash.com", "static.bbc.co.uk", "assets.github.com",
            "ad.doubleclick.net", "cdn.taboola.com", "pixel.quantserve.com", "static.hotjar.com",
            "api.example.com", "fonts.gstatic.com", "analytics.tiktok.com", "cdn.example.co.uk",
            "ads.example.net", "media.theguardian.com", "tr.snapchat.com", "connect.facebook.net",
            "static.cloudflareinsights.com",
        };
        var paths = new[]
        {
            "/pagead/js/adsbygoogle.js", "/gampad/ads?iu=/123/top&sz=728x90", "/analytics.js",
            "/npm/react@18/umd/react.production.min.js", "/photo-123456?w=800&q=80",
            "/assets/main.bundle.css", "/banner/ad-unit-300x250.png", "/track/pixel?ev=pageview",
            "/v2/api/articles?page=2", "/fonts/inter-v12.woff2", "/ads/banner.js",
            "/beacon/collect?tid=UA-12345", "/iframe/adframe.html", "/js/app.min.js",
            "/img/logo.svg",
        };
        var types = new[]
        {
            FilterResourceType.Script, FilterResourceType.Image, FilterResourceType.Stylesheet,
            FilterResourceType.XmlHttpRequest, FilterResourceType.Subdocument, FilterResourceType.Font,
            FilterResourceType.Ping, FilterResourceType.Other,
        };

        var samples = new (string Url, string Host, string Page, FilterResourceType Type)[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var host = hosts[(i * 7 + 3) % hosts.Length];
            var path = paths[(i * 11 + 5) % paths.Length];
            var page = pages[(i * 13 + 1) % pages.Length];
            var type = types[(i * 5 + 2) % types.Length];
            samples[i] = ($"https://{host}{path}", host, page, type);
        }

        // Warmup JIT
        for (var i = 0; i < Math.Min(300, sampleCount); i++)
        {
            var s = samples[i];
            _ = engine.ShouldBlock(s.Url, s.Host.AsSpan(), s.Page.AsSpan(), s.Type);
        }

        var ticksPerSample = new long[sampleCount];
        var blocked = 0;
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < sampleCount; i++)
        {
            var s = samples[i];
            var t0 = Stopwatch.GetTimestamp();
            if (engine.ShouldBlock(s.Url, s.Host.AsSpan(), s.Page.AsSpan(), s.Type))
                blocked++;
            ticksPerSample[i] = Stopwatch.GetTimestamp() - t0;
        }
        var allocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        Array.Sort(ticksPerSample);
        var tickToMs = 1000.0 / Stopwatch.Frequency;
        long sum = 0;
        for (var i = 0; i < sampleCount; i++) sum += ticksPerSample[i];

        double Percentile(double p)
        {
            var idx = Math.Clamp((int)Math.Ceiling(p * sampleCount) - 1, 0, sampleCount - 1);
            return Math.Round(ticksPerSample[idx] * tickToMs, 5);
        }

        return new FilterBenchmarkResult(
            Samples: sampleCount,
            BlockedMatches: blocked,
            P50Ms: Percentile(0.50),
            P95Ms: Percentile(0.95),
            P99Ms: Percentile(0.99),
            MeanMs: Math.Round((sum / (double)sampleCount) * tickToMs, 5),
            AllocBytesPerMatch: Math.Round(allocBytes / (double)sampleCount, 2),
            EngineMemoryMB: Math.Round(engineMemoryMB, 2),
            NetworkRules: engine.NetworkRuleCount,
            ExceptionRules: engine.ExceptionRuleCount,
            CosmeticRules: engine.CosmeticRuleCount,
            PslRules: engine.Psl.RuleCount);
    }
}
