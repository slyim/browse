using System.Globalization;

namespace SearchPort;

sealed class PublicSuffixList
{
    static readonly string[] BuiltInSuffixes =
    [
        // UK
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "sch.uk", "nhs.uk", "police.uk",
        // AU / NZ
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "asn.au", "id.au",
        "co.nz", "net.nz", "org.nz", "govt.nz", "ac.nz", "school.nz", "geek.nz", "gen.nz",
        // JP / KR / CN / HK / TW / SG / MY / ID / TH / VN / PH / IN
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp", "ed.jp", "gr.jp", "lg.jp", "ad.jp",
        "co.kr", "or.kr", "ne.kr", "go.kr", "ac.kr", "re.kr", "pe.kr",
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "ac.cn",
        "com.hk", "org.hk", "edu.hk", "gov.hk", "net.hk", "idv.hk",
        "com.tw", "org.tw", "edu.tw", "gov.tw", "net.tw", "idv.tw",
        "com.sg", "org.sg", "edu.sg", "gov.sg", "net.sg",
        "com.my", "org.my", "edu.my", "gov.my", "net.my",
        "co.id", "or.id", "ac.id", "go.id", "web.id", "my.id",
        "co.th", "or.th", "ac.th", "go.th", "in.th",
        "com.vn", "org.vn", "edu.vn", "gov.vn", "net.vn",
        "com.ph", "org.ph", "edu.ph", "gov.ph", "net.ph",
        "co.in", "net.in", "org.in", "gen.in", "firm.in", "ind.in", "ac.in", "edu.in", "gov.in", "res.in",
        // Americas / Europe / Africa / Middle East
        "com.br", "net.br", "org.br", "gov.br", "edu.br", "jus.br", "mil.br",
        "com.mx", "org.mx", "gob.mx", "edu.mx", "net.mx",
        "com.ar", "org.ar", "gov.ar", "edu.ar", "net.ar",
        "com.co", "org.co", "gov.co", "edu.co", "com.pe", "com.ve", "com.uy", "com.ec",
        "co.za", "org.za", "gov.za", "ac.za", "net.za", "web.za",
        "co.il", "org.il", "ac.il", "gov.il", "muni.il",
        "com.tr", "org.tr", "gov.tr", "edu.tr", "net.tr", "gen.tr", "web.tr",
        "com.ua", "org.ua", "net.ua", "gov.ua", "edu.ua", "kiev.ua",
        "com.pl", "net.pl", "org.pl", "edu.pl", "gov.pl", "info.pl",
        "com.es", "nom.es", "org.es", "gob.es", "edu.es",
        "com.pk", "org.pk", "edu.pk", "gov.pk", "com.ng", "com.eg", "com.sa", "co.ke",
        // Common private / hosting suffixes
        "github.io", "gitlab.io", "pages.dev", "workers.dev", "r2.dev",
        "netlify.app", "vercel.app", "herokuapp.com", "appspot.com",
        "cloudfront.net", "s3.amazonaws.com", "amazonaws.com",
        "azurewebsites.net", "blob.core.windows.net",
        "web.app", "firebaseapp.com", "fly.dev", "onrender.com", "deno.dev",
        "blogspot.com", "wordpress.com", "tumblr.com", "github.dev", "repl.co",
    ];

    readonly HashSet<string> rules;
    readonly HashSet<string> wildcards;
    readonly HashSet<string> exceptions;

    PublicSuffixList(HashSet<string> rules, HashSet<string> wildcards, HashSet<string> exceptions, bool isFallback)
    {
        this.rules = rules;
        this.wildcards = wildcards;
        this.exceptions = exceptions;
        IsFallback = isFallback;
    }

    public bool IsFallback { get; }
    public int RuleCount => rules.Count + wildcards.Count + exceptions.Count;

    public static PublicSuffixList BuiltIn { get; } = CreateBuiltIn();

    static PublicSuffixList CreateBuiltIn()
    {
        var set = new HashSet<string>(BuiltInSuffixes.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var s in BuiltInSuffixes) set.Add(s);
        return new PublicSuffixList(set, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), isFallback: true);
    }

    public static PublicSuffixList Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return BuiltIn;
        var rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wildcards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IdnMapping? idn = null;

        foreach (var rawLine in content.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty || line.StartsWith("//")) continue;
            var ws = line.IndexOfAny(' ', '\t');
            if (ws >= 0) line = line[..ws];
            if (line.IsEmpty) continue;

            var isException = false;
            var isWildcard = false;
            if (line[0] == '!')
            {
                isException = true;
                line = line[1..];
            }
            else if (line.StartsWith("*."))
            {
                isWildcard = true;
                line = line[2..];
            }
            else if (line.Length == 1 && line[0] == '*')
            {
                continue;
            }

            line = line.Trim('.');
            if (line.IsEmpty || line.IndexOf('*') >= 0) continue;

            string normalized;
            if (IsAscii(line))
            {
                normalized = line.ToString().ToLowerInvariant();
            }
            else
            {
                try
                {
                    idn ??= new IdnMapping();
                    normalized = idn.GetAscii(line.ToString()).ToLowerInvariant();
                }
                catch
                {
                    continue;
                }
            }

            if (isException) exceptions.Add(normalized);
            else if (isWildcard) wildcards.Add(normalized);
            else rules.Add(normalized);
        }

        if (rules.Count + wildcards.Count + exceptions.Count == 0)
            return BuiltIn;
        return new PublicSuffixList(rules, wildcards, exceptions, isFallback: false);
    }

    static bool IsAscii(ReadOnlySpan<char> s)
    {
        for (var i = 0; i < s.Length; i++)
            if (s[i] > 0x7F) return false;
        return true;
    }

    static bool IsIpLiteral(ReadOnlySpan<char> host)
    {
        if (host.IndexOf(':') >= 0) return true; // IPv6
        var hasDot = false;
        for (var i = 0; i < host.Length; i++)
        {
            var c = host[i];
            if (c == '.') hasDot = true;
            else if (!char.IsAsciiDigit(c)) return false;
        }
        return hasDot;
    }

    public ReadOnlySpan<char> GetRegistrableDomain(ReadOnlySpan<char> host)
    {
        host = host.TrimEnd('.');
        if (host.IsEmpty || IsIpLiteral(host)) return host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return host;

        var firstDot = host.IndexOf('.');
        if (firstDot <= 0) return host;

        var ruleLookup = rules.GetAlternateLookup<ReadOnlySpan<char>>();
        var wildLookup = wildcards.GetAlternateLookup<ReadOnlySpan<char>>();
        var excLookup = exceptions.GetAlternateLookup<ReadOnlySpan<char>>();
        var hasWildcards = wildcards.Count > 0;
        var hasExceptions = exceptions.Count > 0;

        // Walk candidate suffixes from longest to shortest so the most specific PSL rule wins.
        var prevSpan = ReadOnlySpan<char>.Empty;
        var span = host;
        while (!span.IsEmpty)
        {
            if (hasExceptions && excLookup.Contains(span))
                return span; // !span means everything after its first label is the suffix -> span is eTLD+1

            if (ruleLookup.Contains(span))
                return prevSpan.IsEmpty ? host : prevSpan;

            var dot = span.IndexOf('.');
            if (dot < 0) break;

            var next = span[(dot + 1)..];
            if (hasWildcards && wildLookup.Contains(next))
                return prevSpan.IsEmpty ? host : prevSpan;

            prevSpan = span;
            span = next;
        }

        // Default PSL rule "*": last label is the public suffix -> return last 2 labels.
        var lastDot = host.LastIndexOf('.');
        if (lastDot <= 0) return host;
        var prevDot = host[..lastDot].LastIndexOf('.');
        return prevDot < 0 ? host : host[(prevDot + 1)..];
    }

    public string GetSiteKey(Uri uri)
    {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return uri.Host;
        var host = uri.IdnHost.AsSpan();
        var reg = GetRegistrableDomain(host);
        return reg.Length == host.Length ? uri.IdnHost : reg.ToString();
    }

    public bool IsThirdParty(Uri request, Uri? page)
    {
        if (page is null || !request.IsAbsoluteUri || !page.IsAbsoluteUri) return true;
        if (string.IsNullOrEmpty(request.Host) || string.IsNullOrEmpty(page.Host)) return true;
        if (request.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || page.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return !string.Equals(request.Host, page.Host, StringComparison.OrdinalIgnoreCase);
        return IsThirdParty(request.IdnHost.AsSpan(), page.IdnHost.AsSpan());
    }

    public bool IsThirdParty(ReadOnlySpan<char> requestHost, ReadOnlySpan<char> pageHost)
    {
        if (requestHost.IsEmpty || pageHost.IsEmpty) return true;
        if (requestHost.Equals(pageHost, StringComparison.OrdinalIgnoreCase)) return false;
        return !GetRegistrableDomain(requestHost).Equals(GetRegistrableDomain(pageHost), StringComparison.OrdinalIgnoreCase);
    }
}
