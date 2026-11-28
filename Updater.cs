using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SearchPort;

enum InstallKind { Direct, Scoop, WinGet }

sealed record UpdateRelease(
    string VersionText,
    Version Version,
    string ReleasePageUrl,
    string? DownloadUrl,
    string? Digest);

static class Updater
{
    const string LatestReleaseApiUrl = "https://api.github.com/repos/slyim/browse/releases/latest";
    const string FallbackReleasePageUrl = "https://github.com/slyim/browse/releases/latest";
    const string CheckFileName = "update-check.txt";
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);
    const long MaxZipBytes = 100L * 1024 * 1024;
    const long MaxExtractedBytes = 200L * 1024 * 1024;
    const int MaxRedirects = 5;

    public static Version CurrentVersion => NormalizeVersion(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0, 0));

    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    internal static Version NormalizeVersion(Version v) =>
        new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    internal static bool TryParseReleaseTag(string? tag, out string versionText, out Version version)
    {
        versionText = "";
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrEmpty(tag)) return false;
        var clean = tag[0] is 'v' or 'V' ? tag[1..] : tag;
        if (clean.Length == 0 || clean[0] == '.' || clean[^1] == '.') return false;
        foreach (var ch in clean)
            if (!char.IsAsciiDigit(ch) && ch != '.')
                return false;
        if (!Version.TryParse(clean, out var parsed)) return false;
        versionText = clean;
        version = NormalizeVersion(parsed);
        return true;
    }

    internal static bool IsNewerVersion(string? tag, Version current, out string versionText, out Version version) =>
        TryParseReleaseTag(tag, out versionText, out version) && version > NormalizeVersion(current);

    static bool IsSafeHttps(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0;

    internal static bool IsAllowedDownloadUrl(string? raw, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var parsed)) return false;
        if (!IsSafeHttps(parsed)) return false;
        if (!parsed.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    internal static bool IsAllowedRedirectUrl(Uri? uri)
    {
        if (uri is null || !IsSafeHttps(uri)) return false;
        var host = uri.IdnHost;
        const string suffix = ".githubusercontent.com";
        return host.Length > suffix.Length
            && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && host[0] != '.'
            && !host.Contains("..", StringComparison.Ordinal);
    }

    internal static bool TryParseSha256Digest(string? digest, out byte[] expectedHash)
    {
        expectedHash = [];
        if (string.IsNullOrEmpty(digest) || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        var hex = digest.AsSpan(7);
        if (hex.Length != 64) return false;
        foreach (var ch in hex)
            if (!char.IsAsciiHexDigit(ch))
                return false;
        expectedHash = Convert.FromHexString(hex);
        return true;
    }

    internal static bool VerifySha256(ReadOnlySpan<byte> data, string? digest) =>
        TryParseSha256Digest(digest, out var expected)
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), expected);

    internal static InstallKind DetectInstallKind(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return InstallKind.Direct;
        var normalized = exePath.Replace('/', '\\');
        if (normalized.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase))
            return InstallKind.Scoop;
        if (normalized.Contains(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\Microsoft\WinGet\", StringComparison.OrdinalIgnoreCase))
            return InstallKind.WinGet;
        return InstallKind.Direct;
    }

    public static bool IsInstallDirWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".browse-write-test-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public static void StartCleanupOldBinary()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;
        var oldPath = exePath + ".old";
        if (!File.Exists(oldPath)) return;
        _ = Task.Run(async () =>
        {
            // Retry briefly in case the replaced process is still releasing its image handle.
            for (var i = 0; i < 6; i++)
            {
                try
                {
                    if (!File.Exists(oldPath)) return;
                    File.Delete(oldPath);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
                catch { return; }
            }
        });
    }

    static bool ShouldCheck(string checkFile, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(checkFile)) return true;
            var raw = File.ReadAllText(checkFile).Trim();
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                && last <= now && now - last < CheckInterval)
                return false;
        }
        catch { }
        return true;
    }

    static void RecordCheck(string checkFile, DateTimeOffset now)
    {
        try
        {
            var dir = Path.GetDirectoryName(checkFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(checkFile, now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
        catch { }
    }

    public static async Task<UpdateRelease?> CheckForUpdateAsync(string sessionFile, CancellationToken ct)
    {
        try
        {
            var profileDir = Path.GetDirectoryName(sessionFile);
            if (string.IsNullOrEmpty(profileDir)) return null;
            var checkFile = Path.Combine(profileDir, CheckFileName);
            var now = DateTimeOffset.UtcNow;
            if (!ShouldCheck(checkFile, now)) return null;

            RecordCheck(checkFile, now);

            using var client = new HttpClient { Timeout = ApiTimeout };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("browse", CurrentVersionText));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await client.GetAsync(LatestReleaseApiUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return TryParseReleaseJson(json, CurrentVersion);
        }
        catch
        {
            return null;
        }
    }

    internal static UpdateRelease? TryParseReleaseJson(string json, Version currentVersion)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String) return null;
            if (!IsNewerVersion(tagEl.GetString(), currentVersion, out var versionText, out var version)) return null;

            var pageUrl = FallbackReleasePageUrl;
            if (root.TryGetProperty("html_url", out var htmlEl) && htmlEl.ValueKind == JsonValueKind.String
                && IsAllowedDownloadUrl(htmlEl.GetString(), out var parsedPage) && parsedPage is not null)
                pageUrl = parsedPage.AbsoluteUri;

            var expectedAsset = $"browse-{versionText}-win-x64.zip";
            string? downloadUrl = null;
            string? digest = null;
            var foundAsset = false;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    if (!asset.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                    if (!string.Equals(nameEl.GetString(), expectedAsset, StringComparison.Ordinal)) continue;
                    foundAsset = true;
                    if (asset.TryGetProperty("browser_download_url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                        downloadUrl = urlEl.GetString();
                    if (asset.TryGetProperty("digest", out var digestEl) && digestEl.ValueKind == JsonValueKind.String)
                        digest = digestEl.GetString();
                    break;
                }
            }

            return foundAsset ? new UpdateRelease(versionText, version, pageUrl, downloadUrl, digest) : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<(string WorkDir, string ExtractDir)> DownloadAndExtractAsync(UpdateRelease release, CancellationToken ct)
    {
        if (!IsAllowedDownloadUrl(release.DownloadUrl, out var initialUri) || initialUri is null)
            throw new InvalidOperationException("Update download URL is missing or not an HTTPS github.com URL.");
        if (!TryParseSha256Digest(release.Digest, out var expectedHash))
            throw new InvalidOperationException("Update release is missing a valid SHA-256 digest. Update aborted.");

        var workDir = Path.Combine(Path.GetTempPath(), $"browse-update-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(workDir, "update.zip");
        var extractDir = Path.Combine(workDir, "extracted");
        Directory.CreateDirectory(workDir);
        try
        {
            await DownloadZipAsync(initialUri, zipPath, expectedHash, ct).ConfigureAwait(false);
            ExtractZipSafely(zipPath, extractDir);
            if (!File.Exists(Path.Combine(extractDir, "browse.exe")))
                throw new InvalidOperationException("Update archive does not contain browse.exe.");
            return (workDir, extractDir);
        }
        catch
        {
            TryDeleteDirectory(workDir);
            throw;
        }
    }

    static async Task DownloadZipAsync(Uri initialUri, string zipPath, byte[] expectedHash, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        using var client = new HttpClient(handler) { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("browse", CurrentVersionText));

        var currentUri = initialUri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (hop == MaxRedirects)
                    throw new InvalidOperationException("Too many redirects while downloading the update.");
                var location = resp.Headers.Location
                    ?? throw new InvalidOperationException("Redirect response did not include a Location header.");
                var next = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!IsAllowedRedirectUrl(next))
                    throw new InvalidOperationException($"Blocked redirect to untrusted host: {next.Host}");
                currentUri = next;
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Update download failed (HTTP {(int)resp.StatusCode}).");
            if (resp.Content.Headers.ContentLength > MaxZipBytes)
                throw new InvalidOperationException("Update archive exceeds the maximum allowed size.");

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await src.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaxZipBytes)
                    throw new InvalidOperationException("Update archive exceeds the maximum allowed size.");
                hasher.AppendData(buffer, 0, read);
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            var actualHash = hasher.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new InvalidOperationException("Downloaded update failed SHA-256 verification. Update aborted.");
            return;
        }
    }

    static void ExtractZipSafely(string zipPath, string extractDir)
    {
        Directory.CreateDirectory(extractDir);
        var root = Path.GetFullPath(extractDir);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!dest.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update archive contains an invalid path.");
            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            using var src = entry.Open();
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            while (true)
            {
                var read = src.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total += read;
                if (total > MaxExtractedBytes)
                    throw new InvalidOperationException("Extracted update exceeds the maximum allowed size.");
                dst.Write(buffer, 0, read);
            }
        }
    }

    // Must match AppId in packaging/browse.iss (Inno appends "_is1").
    internal const string InstallerUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3C2A1E-8B4D-4E7A-9C51-2D0B7E9A4F13}_is1";

    // Keeps Settings > Apps showing the right version after an in-app update of an installer copy.
    // Only touches the entry when it belongs to this exe's folder; portable copies have no entry.
    internal static bool RecordInstalledVersion(string exePath, string versionText, string keyPath = InstallerUninstallKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key?.GetValue("InstallLocation") is not string location) return false;
            var installDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)), installDir, StringComparison.OrdinalIgnoreCase))
                return false;
            key.SetValue("DisplayVersion", versionText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SwapFiles(string exePath, string extractDir)
    {
        var installDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("Could not locate the install folder.");
        var oldExePath = exePath + ".old";
        var movedOld = false;
        try
        {
            if (File.Exists(oldExePath)) File.Delete(oldExePath);
            File.Move(exePath, oldExePath);
            movedOld = true;

            var extractedRoot = Path.GetFullPath(extractDir);
            File.Copy(Path.Combine(extractedRoot, "browse.exe"), exePath, overwrite: true);

            foreach (var src in Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(extractedRoot, src);
                if (rel.Equals("browse.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(installDir, rel);
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(src, dest, overwrite: true);
            }
        }
        catch
        {
            if (movedOld) RestoreOldExe(exePath, oldExePath);
            throw;
        }
    }

    public static void RestoreOldExe(string exePath, string oldExePath)
    {
        try
        {
            if (!File.Exists(oldExePath)) return;
            if (File.Exists(exePath)) File.Delete(exePath);
            File.Move(oldExePath, exePath);
        }
        catch { }
    }

    public static void Launch(string exePath, string[] args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (Process.Start(psi) is null)
            throw new InvalidOperationException("Could not launch the updated browser.");
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Updater self-check failed: " + message);
    }

    public static bool SelfCheck()
    {
        var v001 = new Version(0, 0, 1, 0);

        // Version tag parsing and comparison
        Assert(IsNewerVersion("v0.0.2", v001, out var vt, out _) && vt == "0.0.2", "v0.0.2 > 0.0.1.0");
        Assert(IsNewerVersion("v0.1.0", new Version(0, 0, 9, 0), out _, out _), "v0.1.0 > 0.0.9.0");
        Assert(IsNewerVersion("v1.0.0", new Version(0, 9, 9, 0), out _, out _), "v1.0.0 > 0.9.9.0");
        Assert(IsNewerVersion("v0.0.10", new Version(0, 0, 2, 0), out _, out _), "v0.0.10 > 0.0.2.0");
        Assert(!IsNewerVersion("v0.0.1", v001, out _, out _), "equal version not newer");
        Assert(!IsNewerVersion("v0.0.0", v001, out _, out _), "older version not newer");
        Assert(!IsNewerVersion("v0.0.2-beta.1", v001, out _, out _), "reject prerelease tag");
        Assert(!IsNewerVersion("v0.0.2+abc", v001, out _, out _), "reject build metadata tag");
        Assert(!IsNewerVersion("vv0.0.2", v001, out _, out _), "reject double v");
        Assert(!IsNewerVersion("release-2", v001, out _, out _), "reject non-version tag");
        Assert(!IsNewerVersion("v1", v001, out _, out _), "reject single-number tag");
        Assert(!IsNewerVersion("v 0.0.2", v001, out _, out _), "reject padded tag");
        Assert(!IsNewerVersion("", v001, out _, out _), "reject empty tag");

        // URL host & scheme validation
        Assert(IsAllowedDownloadUrl("https://github.com/slyim/browse/releases/download/v0.0.2/browse-0.0.2-win-x64.zip", out _), "allow https github.com");
        Assert(!IsAllowedDownloadUrl("http://github.com/slyim/browse/releases/download/v0.0.2/browse-0.0.2-win-x64.zip", out _), "reject http github.com");
        Assert(!IsAllowedDownloadUrl("https://evilgithub.com/a.zip", out _), "reject evilgithub.com");
        Assert(!IsAllowedDownloadUrl("https://github.com.evil.com/a.zip", out _), "reject github.com.evil.com");
        Assert(!IsAllowedDownloadUrl("https://objects.githubusercontent.com/a.zip", out _), "initial URL must be github.com");
        Assert(!IsAllowedDownloadUrl("https://user:pw@github.com/a.zip", out _), "reject userinfo");
        Assert(!IsAllowedDownloadUrl("https://github.com:8443/a.zip", out _), "reject non-default port");
        Assert(!IsAllowedDownloadUrl("file:///C:/browse.zip", out _), "reject file scheme");

        Assert(IsAllowedRedirectUrl(new Uri("https://objects.githubusercontent.com/a.zip")), "allow objects.githubusercontent.com");
        Assert(IsAllowedRedirectUrl(new Uri("https://release-assets.githubusercontent.com/a.zip")), "allow release-assets.githubusercontent.com");
        Assert(!IsAllowedRedirectUrl(new Uri("http://objects.githubusercontent.com/a.zip")), "reject http redirect");
        Assert(!IsAllowedRedirectUrl(new Uri("https://githubusercontent.com/a.zip")), "reject bare githubusercontent.com");
        Assert(!IsAllowedRedirectUrl(new Uri("https://evilgithubusercontent.com/a.zip")), "reject suffix spoof");
        Assert(!IsAllowedRedirectUrl(new Uri("https://objects.githubusercontent.com.evil.com/a.zip")), "reject domain spoof");
        Assert(!IsAllowedRedirectUrl(new Uri("https://github.com/a.zip")), "redirect must be *.githubusercontent.com");
        Assert(!IsAllowedRedirectUrl(new Uri("https://user@objects.githubusercontent.com/a.zip")), "reject redirect userinfo");
        Assert(!IsAllowedRedirectUrl(new Uri("https://objects.githubusercontent.com:8443/a.zip")), "reject redirect non-default port");

        // SHA-256 digest validation
        var sample = Encoding.UTF8.GetBytes("browse-update-test");
        var sampleHex = Convert.ToHexStringLower(SHA256.HashData(sample));
        Assert(VerifySha256(sample, "sha256:" + sampleHex), "valid lowercase sha256");
        Assert(VerifySha256(sample, "sha256:" + sampleHex.ToUpperInvariant()), "valid uppercase sha256");
        Assert(!VerifySha256(sample, "sha256:" + new string('0', 64)), "reject mismatched sha256");
        Assert(!VerifySha256(sample, null), "reject null digest");
        Assert(!VerifySha256(sample, ""), "reject empty digest");
        Assert(!VerifySha256(sample, "md5:" + sampleHex), "reject wrong algorithm");
        Assert(!VerifySha256(sample, "sha256:abcd"), "reject short hex");
        Assert(!VerifySha256(sample, "sha256:" + new string('g', 64)), "reject non-hex");

        // Release JSON parsing (draft/prerelease/asset/digest)
        var validJson = $$"""
            {"tag_name":"v0.0.2","draft":false,"prerelease":false,"html_url":"https://github.com/slyim/browse/releases/tag/v0.0.2",
             "assets":[{"name":"browse-0.0.2-win-x64.zip","browser_download_url":"https://github.com/slyim/browse/releases/download/v0.0.2/browse-0.0.2-win-x64.zip","digest":"sha256:{{sampleHex}}"}]}
            """;
        var parsed = TryParseReleaseJson(validJson, v001);
        Assert(parsed is { VersionText: "0.0.2" } && parsed.Digest == "sha256:" + sampleHex, "parse valid release");
        Assert(TryParseReleaseJson(validJson.Replace("\"draft\":false", "\"draft\":true"), v001) is null, "ignore draft");
        Assert(TryParseReleaseJson(validJson.Replace("\"prerelease\":false", "\"prerelease\":true"), v001) is null, "ignore prerelease");
        Assert(TryParseReleaseJson(validJson.Replace("v0.0.2", "v0.0.1").Replace("0.0.2", "0.0.1"), v001) is null, "ignore same version");
        Assert(TryParseReleaseJson(validJson.Replace("browse-0.0.2-win-x64.zip", "other.zip"), v001) is null, "ignore missing matching asset");
        var missingDigestJson = """
            {"tag_name":"v0.0.2","draft":false,"prerelease":false,
             "assets":[{"name":"browse-0.0.2-win-x64.zip","browser_download_url":"https://github.com/slyim/browse/releases/download/v0.0.2/browse-0.0.2-win-x64.zip"}]}
            """;
        var missingDigest = TryParseReleaseJson(missingDigestJson, v001);
        Assert(missingDigest is { Digest: null }, "preserve missing digest for click-time abort");

        // Package-manager path detection
        Assert(DetectInstallKind(@"C:\Users\u\scoop\apps\browse\current\browse.exe") == InstallKind.Scoop, "detect scoop");
        Assert(DetectInstallKind(@"C:\Users\u\AppData\Local\Microsoft\WinGet\Packages\slyim.browse\browse.exe") == InstallKind.WinGet, "detect Microsoft WinGet");
        Assert(DetectInstallKind(@"C:\Program Files\WinGet\Packages\slyim.browse\browse.exe") == InstallKind.WinGet, "detect WinGet Packages");
        Assert(DetectInstallKind(@"C:\Tools\browse\browse.exe") == InstallKind.Direct, "detect direct install");

        // Installer version write-back, under a throwaway HKCU key
        var testKey = $@"Software\browse-selfcheck-{Environment.ProcessId}";
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(testKey))
            {
                k.SetValue("InstallLocation", @"C:\Inst\browse\");
                k.SetValue("DisplayVersion", "0.0.1");
            }
            Assert(!RecordInstalledVersion(@"C:\Portable\browse.exe", "0.0.9", testKey), "skip other folder");
            Assert(RecordInstalledVersion(@"C:\Inst\browse\browse.exe", "0.0.9", testKey), "record own folder");
            using (var after = Registry.CurrentUser.OpenSubKey(testKey))
                Assert((after?.GetValue("DisplayVersion") as string) == "0.0.9", "DisplayVersion written");
            Assert(!RecordInstalledVersion(@"C:\Inst\browse\browse.exe", "0.0.9", testKey + @"\missing"), "no entry, no write");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
        }

        return true;
    }
}
