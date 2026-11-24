using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SearchPort;

static class LaunchPipe
{
    public const int MaxUrlLength = 8192;
    const int MaxPipeBytes = 16_384;
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    public static string PipeName(string profilePath)
    {
        var key = Path.GetFullPath(profilePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return "SearchWindows-Open-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public static bool TryForward(string profilePath, string url, TimeSpan? timeout = null)
    {
        var validated = Program.TryValidateLaunchUrl(url);
        if (validated is null) return false;
        var payload = Encoding.UTF8.GetBytes(validated + "\n");
        if (payload.Length > MaxPipeBytes) return false;

        var pipeName = PipeName(profilePath);
        var deadline = Stopwatch.StartNew();
        // Longer than the server's 2 s per-connection limit, so one busy connection can't make us open a second window.
        var budget = timeout ?? TimeSpan.FromSeconds(5);
        while (deadline.Elapsed < budget)
        {
            var remainingMs = (int)Math.Clamp((budget - deadline.Elapsed).TotalMilliseconds, 50, 300);
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                client.Connect(remainingMs);
                try
                {
                    if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid) && pid != 0)
                        AllowSetForegroundWindow(unchecked((int)pid));
                }
                catch { }

                using var ioCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                client.WriteAsync(payload, ioCts.Token).AsTask().GetAwaiter().GetResult();
                client.FlushAsync(ioCts.Token).GetAwaiter().GetResult();

                var ack = new byte[1];
                var read = client.ReadAsync(ack, ioCts.Token).AsTask().GetAwaiter().GetResult();
                return read == 1 && ack[0] == 1;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                if (deadline.Elapsed >= budget) return false;
                Thread.Sleep(50);
            }
        }
        return false;
    }

    public static Task? StartServer(string profilePath, Func<string, bool> onUrl, CancellationToken ct)
    {
        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(
                PipeName(profilePath),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Task.Run(() => ServeLoopAsync(server, onUrl, ct), CancellationToken.None);
    }

    static async Task ServeLoopAsync(NamedPipeServerStream server, Func<string, bool> onUrl, CancellationToken ct)
    {
        await using (server)
        {
            var buf = new byte[MaxPipeBytes + 1];
            var drain = new byte[1];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }

                try
                {
                    using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connCts.CancelAfter(TimeSpan.FromSeconds(2));

                    var total = 0;
                    var overflow = false;
                    while (total < buf.Length)
                    {
                        var read = await server.ReadAsync(buf.AsMemory(total, buf.Length - total), connCts.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        var nl = Array.IndexOf(buf, (byte)'\n', total, read);
                        var nextTotal = total + read;
                        if (nl >= 0)
                        {
                            if (nl + 1 < nextTotal) overflow = true;
                            total = nl;
                            break;
                        }
                        total = nextTotal;
                        if (total > MaxPipeBytes)
                        {
                            overflow = true;
                            break;
                        }
                    }

                    var accepted = false;
                    if (!overflow && total > 0)
                    {
                        var len = total > 0 && buf[total - 1] == (byte)'\r' ? total - 1 : total;
                        if (len > 0 && Array.IndexOf(buf, (byte)0, 0, len) < 0)
                        {
                            try
                            {
                                var raw = StrictUtf8.GetString(buf, 0, len);
                                if (Program.TryValidateLaunchUrl(raw) is { } validUrl)
                                    accepted = onUrl(validUrl);
                            }
                            catch (DecoderFallbackException) { }
                        }
                    }

                    var ack = new[] { accepted ? (byte)1 : (byte)0 };
                    await server.WriteAsync(ack, connCts.Token).ConfigureAwait(false);
                    await server.FlushAsync(connCts.Token).ConfigureAwait(false);

                    // Wait briefly for the client to read the ACK and close before disconnecting.
                    using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    closeCts.CancelAfter(250);
                    try { _ = await server.ReadAsync(drain, closeCts.Token).ConfigureAwait(false); }
                    catch { }
                }
                catch { }
                finally
                {
                    // Always reset: a client that hung up mid-message leaves IsConnected false but the pipe broken,
                    // and skipping Disconnect made the next WaitForConnectionAsync throw and end this loop for good.
                    try { server.Disconnect(); } catch { }
                }
            }
        }
    }
}

static class BrowserRegistration
{
    public const string DefaultSoftwareRoot = "Software";
    public const string AppId = "browse";
    public const string ProgId = "browseHTML";
    const string AppDescription = "Small Windows web browser";

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    const int SHCNE_ASSOCCHANGED = 0x08000000;
    const uint SHCNF_IDLIST = 0x0000;

    static string NormalizeRoot(string softwareRoot)
    {
        var trimmed = softwareRoot.Trim().Trim('\\');
        if (string.IsNullOrEmpty(trimmed)) throw new ArgumentException("Registry root path cannot be empty.", nameof(softwareRoot));
        return trimmed;
    }

    static string NormalizeExePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || exePath.Contains('"') || exePath.Any(c => c < 0x20 || c == 0x7F))
            throw new ArgumentException("Invalid executable path.", nameof(exePath));
        var full = Path.GetFullPath(exePath);
        if (!Path.IsPathRooted(full)) throw new ArgumentException("Executable path must be rooted.", nameof(exePath));
        return full;
    }

    public static void Register(string exePath, RegistryKey? baseKey = null, string softwareRoot = DefaultSoftwareRoot, bool notifyShell = true)
    {
        var fullExe = NormalizeExePath(exePath);
        var root = NormalizeRoot(softwareRoot);
        var hive = baseKey ?? Registry.CurrentUser;

        var clientKeyPath = $@"{root}\Clients\StartMenuInternet\{AppId}";
        var capabilitiesPath = $@"{clientKeyPath}\Capabilities";
        var registeredAppsPath = $@"{root}\RegisteredApplications";
        var progIdPath = $@"{root}\Classes\{ProgId}";
        var iconValue = $"{fullExe},0";

        using (var clientKey = hive.CreateSubKey(clientKeyPath))
        {
            clientKey.SetValue("", AppId);
        }
        using (var defaultIcon = hive.CreateSubKey($@"{clientKeyPath}\DefaultIcon"))
        {
            defaultIcon.SetValue("", iconValue);
        }
        using (var openCmd = hive.CreateSubKey($@"{clientKeyPath}\shell\open\command"))
        {
            openCmd.SetValue("", $"\"{fullExe}\"");
        }
        using (var capKey = hive.CreateSubKey(capabilitiesPath))
        {
            capKey.SetValue("ApplicationName", AppId);
            capKey.SetValue("ApplicationDescription", AppDescription);
            capKey.SetValue("ApplicationIcon", iconValue);
        }
        using (var urlAssoc = hive.CreateSubKey($@"{capabilitiesPath}\URLAssociations"))
        {
            urlAssoc.SetValue("http", ProgId);
            urlAssoc.SetValue("https", ProgId);
        }
        using (var fileAssoc = hive.CreateSubKey($@"{capabilitiesPath}\FileAssociations"))
        {
            fileAssoc.SetValue(".htm", ProgId);
            fileAssoc.SetValue(".html", ProgId);
        }
        using (var regApps = hive.CreateSubKey(registeredAppsPath))
        {
            regApps.SetValue(AppId, capabilitiesPath);
        }
        using (var progKey = hive.CreateSubKey(progIdPath))
        {
            progKey.SetValue("", "browse HTML Document");
        }
        using (var progApp = hive.CreateSubKey($@"{progIdPath}\Application"))
        {
            progApp.SetValue("ApplicationName", AppId);
            progApp.SetValue("ApplicationDescription", AppDescription);
            progApp.SetValue("ApplicationIcon", iconValue);
        }
        using (var progIcon = hive.CreateSubKey($@"{progIdPath}\DefaultIcon"))
        {
            progIcon.SetValue("", iconValue);
        }
        using (var progCmd = hive.CreateSubKey($@"{progIdPath}\shell\open\command"))
        {
            progCmd.SetValue("", $"\"{fullExe}\" \"%1\"");
        }

        if (notifyShell) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Unregister(RegistryKey? baseKey = null, string softwareRoot = DefaultSoftwareRoot, bool notifyShell = true)
    {
        var root = NormalizeRoot(softwareRoot);
        var hive = baseKey ?? Registry.CurrentUser;

        using (var regApps = hive.OpenSubKey($@"{root}\RegisteredApplications", writable: true))
        {
            regApps?.DeleteValue(AppId, throwOnMissingValue: false);
        }
        hive.DeleteSubKeyTree($@"{root}\Clients\StartMenuInternet\{AppId}", throwOnMissingSubKey: false);
        hive.DeleteSubKeyTree($@"{root}\Classes\{ProgId}", throwOnMissingSubKey: false);

        if (notifyShell) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool IsRegistered(RegistryKey? baseKey = null, string softwareRoot = DefaultSoftwareRoot)
    {
        try
        {
            var root = NormalizeRoot(softwareRoot);
            var hive = baseKey ?? Registry.CurrentUser;
            var expectedCapabilities = $@"{root}\Clients\StartMenuInternet\{AppId}\Capabilities";
            using var regApps = hive.OpenSubKey($@"{root}\RegisteredApplications", writable: false);
            var registeredCap = regApps?.GetValue(AppId) as string;
            if (!string.Equals(registeredCap, expectedCapabilities, StringComparison.OrdinalIgnoreCase))
                return false;
            using var capKey = hive.OpenSubKey(expectedCapabilities, writable: false);
            if (capKey is null) return false;
            using var cmdKey = hive.OpenSubKey($@"{root}\Classes\{ProgId}\shell\open\command", writable: false);
            return !string.IsNullOrWhiteSpace(cmdKey?.GetValue("") as string);
        }
        catch
        {
            return false;
        }
    }

    public static void OpenDefaultAppsSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }
}

enum ExternalSchemeDecision { Deny, AllowRemembered, Prompt }

static class ExternalSchemes
{
    static readonly HashSet<string> DangerousSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Script, local file, and browser/internal schemes
        "javascript", "vbscript", "jscript", "data", "blob", "file", "about",
        "edge", "chrome", "chrome-extension", "chrome-search", "chrome-untrusted",
        "devtools", "extension", "ms-browser-extension", "view-source", "filesystem",
        "ftp", "ftps", "gopher", "ws", "wss", "res", "mk", "mhtml", "its", "ms-its",
        // Known-abused Windows protocol handlers
        "ms-msdt", "search-ms", "search", "ms-officecmd", "ms-appinstaller",
        "ms-cxh", "ms-cxh-full", "ms-settings", "shell", "hcp",
        "mshta", "hta", "cmd", "powershell", "pwsh", "wscript", "cscript",
        "smb", "ldap", "ldaps", "telnet", "rlogin",
    };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryStringW(
        uint flags,
        uint str,
        string pszAssoc,
        string? pszExtra,
        [Out] char[]? pszOut,
        ref uint pcchOut);

    const uint ASSOCF_INIT_IGNOREUNKNOWN = 0x00000400;
    const uint ASSOCSTR_FRIENDLYAPPNAME = 4;

    public static string? NormalizeScheme(string? rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri)) return null;
        var text = rawUri.Trim();
        var colon = text.IndexOf(':');
        if (colon <= 0) return null;
        var scheme = text[..colon];
        return Uri.CheckSchemeName(scheme) ? scheme.ToLowerInvariant() : null;
    }

    public static string? NormalizeSite(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        var text = origin.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.IdnHost.Length == 0)
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    public static bool IsDangerousScheme(string? scheme) =>
        string.IsNullOrWhiteSpace(scheme)
        || !Uri.CheckSchemeName(scheme)
        || scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
        || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
        || DangerousSchemes.Contains(scheme);

    public static bool CanPassNavigationToExternalHandler(string? uri)
    {
        var scheme = NormalizeScheme(uri);
        return scheme is not null && !IsDangerousScheme(scheme);
    }

    public static ExternalSchemeDecision Decide(
        string? uri,
        string? initiatingOrigin,
        bool isActiveTab,
        bool isUserInitiated,
        IReadOnlyList<ExternalAppPermission> allowed,
        out string? scheme,
        out string? site)
    {
        scheme = NormalizeScheme(uri);
        site = NormalizeSite(initiatingOrigin);
        if (scheme is null || IsDangerousScheme(scheme))
            return ExternalSchemeDecision.Deny;
        if (!isActiveTab || !isUserInitiated)
            return ExternalSchemeDecision.Deny;
        if (site is not null)
        {
            for (var i = 0; i < allowed.Count; i++)
            {
                if (string.Equals(allowed[i].Site, site, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(allowed[i].Scheme, scheme, StringComparison.OrdinalIgnoreCase))
                    return ExternalSchemeDecision.AllowRemembered;
            }
        }
        return ExternalSchemeDecision.Prompt;
    }

    public static ExternalAppPermission[] NormalizePermissions(ExternalAppPermission[]? items)
    {
        if (items is null || items.Length == 0) return [];
        var seen = new HashSet<(string Site, string Scheme)>();
        var list = new List<ExternalAppPermission>(items.Length);
        foreach (var item in items)
        {
            if (item is null) continue;
            var site = NormalizeSite(item.Site);
            var scheme = string.IsNullOrWhiteSpace(item.Scheme) ? null : item.Scheme.Trim().ToLowerInvariant();
            if (site is null || scheme is null || IsDangerousScheme(scheme)) continue;
            if (seen.Add((site, scheme))) list.Add(new ExternalAppPermission(site, scheme));
        }
        return [.. list];
    }

    public static string? ResolveAppName(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme) || !Uri.CheckSchemeName(scheme)) return null;
        try
        {
            foreach (var verb in new string?[] { "open", null })
            {
                uint length = 0;
                var hr = AssocQueryStringW(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_FRIENDLYAPPNAME, scheme, verb, null, ref length);
                if (length <= 1 || length > 512) continue;
                var buffer = new char[length];
                hr = AssocQueryStringW(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_FRIENDLYAPPNAME, scheme, verb, buffer, ref length);
                if (hr == 0 && length > 1)
                {
                    var name = new string(buffer, 0, (int)length - 1).Trim();
                    if (name.Length > 0) return name.Length > 120 ? name[..120] : name;
                }
            }
        }
        catch { }
        return null;
    }
}

sealed class ExternalAppDialog : Form
{
    readonly Label message;
    readonly CheckBox? rememberBox;
    readonly Button openBtn;
    readonly Button cancelBtn;
    readonly int? dpiOverride;
    int currentDpi;

    public bool RememberChoice => rememberBox?.Checked ?? false;

    internal ExternalAppDialog(string scheme, string? site, string? appName, int? dpiOverride = null)
    {
        this.dpiOverride = dpiOverride;
        currentDpi = dpiOverride ?? DeviceDpi;

        Text = "Open external app";
        AccessibleName = "Open external app";
        AutoScaleMode = AutoScaleMode.Dpi;
        ForeColor = Theme.Ink;
        BackColor = Theme.Ground;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var requester = site ?? "This page";
        var target = string.IsNullOrWhiteSpace(appName)
            ? $"an external application for {scheme}: links"
            : $"{appName} ({scheme}:)";
        message = new Label
        {
            Text = EscapeMnemonics($"{requester} wants to open {target}."),
            AutoSize = true,
            ForeColor = Theme.Ink,
            BackColor = Theme.Ground,
        };
        Controls.Add(message);

        if (site is not null)
        {
            rememberBox = new CheckBox
            {
                Text = EscapeMnemonics($"Always allow {scheme} links from {site}"),
                AutoSize = true,
                ForeColor = Theme.Ink,
                BackColor = Theme.Ground,
                AccessibleName = $"Always allow {scheme} links from {site}",
                TabStop = true,
            };
            Controls.Add(rememberBox);
        }

        openBtn = new Button
        {
            Text = "Open",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AccessibleName = "Open external app",
            TabStop = true,
        };
        cancelBtn = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AccessibleName = "Cancel",
            TabStop = true,
        };
        CancelButton = cancelBtn;
        Controls.Add(openBtn);
        Controls.Add(cancelBtn);
        Shown += (_, _) => cancelBtn.Focus();

        LayoutForDpi(currentDpi);
    }

    static string EscapeMnemonics(string text) => text.Replace("&", "&&", StringComparison.Ordinal);
    static int ScalePx(int px, int dpi) => (int)Math.Round(px * (dpi / 96.0), MidpointRounding.AwayFromZero);

    void LayoutForDpi(int dpi)
    {
        currentDpi = dpi;
        Font = Theme.FontForDpi(Theme.UiFont, dpi);
        var padX = ScalePx(16, dpi);
        var padY = ScalePx(14, dpi);
        var gapY = ScalePx(12, dpi);
        var btnGap = ScalePx(8, dpi);
        var maxTextW = ScalePx(440, dpi);
        var minContentW = ScalePx(360, dpi);

        openBtn.Padding = new(ScalePx(10, dpi), ScalePx(2, dpi), ScalePx(10, dpi), ScalePx(2, dpi));
        cancelBtn.Padding = new(ScalePx(10, dpi), ScalePx(2, dpi), ScalePx(10, dpi), ScalePx(2, dpi));
        openBtn.MinimumSize = new(ScalePx(78, dpi), 0);
        cancelBtn.MinimumSize = new(ScalePx(78, dpi), 0);

        message.MaximumSize = new(maxTextW, 0);
        var msgSize = message.GetPreferredSize(new Size(maxTextW, 0));
        var openSize = openBtn.GetPreferredSize(Size.Empty);
        var cancelSize = cancelBtn.GetPreferredSize(Size.Empty);
        var remSize = Size.Empty;
        if (rememberBox is not null)
        {
            rememberBox.MaximumSize = new(maxTextW, 0);
            remSize = rememberBox.GetPreferredSize(new Size(maxTextW, 0));
        }

        var contentW = Math.Max(minContentW, Math.Max(Math.Max(msgSize.Width, remSize.Width), openSize.Width + btnGap + cancelSize.Width));
        message.SetBounds(padX, padY, msgSize.Width, msgSize.Height);
        var y = message.Bottom + gapY;
        if (rememberBox is not null)
        {
            rememberBox.SetBounds(padX, y, remSize.Width, remSize.Height);
            y = rememberBox.Bottom + gapY;
        }
        var btnH = Math.Max(openSize.Height, cancelSize.Height);
        cancelBtn.SetBounds(padX + contentW - cancelSize.Width, y, cancelSize.Width, btnH);
        openBtn.SetBounds(cancelBtn.Left - btnGap - openSize.Width, y, openSize.Width, btnH);
        ClientSize = new(padX * 2 + contentW, cancelBtn.Bottom + padY);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (dpiOverride is null && DeviceDpi != currentDpi)
            LayoutForDpi(DeviceDpi);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (dpiOverride is null)
            LayoutForDpi(e.DeviceDpiNew);
    }

    internal bool VerifyLayoutInsideClient()
    {
        CreateControl();
        PerformLayout();
        var client = ClientRectangle;
        var ok = client.Width > 0 && client.Height > 0
            && message.Width > 0 && message.Height > 0 && client.Contains(message.Bounds)
            && openBtn.Width > 0 && openBtn.Height > 0 && client.Contains(openBtn.Bounds)
            && cancelBtn.Width > 0 && cancelBtn.Height > 0 && client.Contains(cancelBtn.Bounds)
            && !openBtn.Bounds.IntersectsWith(cancelBtn.Bounds)
            && !message.Bounds.IntersectsWith(openBtn.Bounds)
            && !message.Bounds.IntersectsWith(cancelBtn.Bounds);
        if (rememberBox is not null)
        {
            ok &= rememberBox.Width > 0 && rememberBox.Height > 0
                && client.Contains(rememberBox.Bounds)
                && !message.Bounds.IntersectsWith(rememberBox.Bounds)
                && !rememberBox.Bounds.IntersectsWith(openBtn.Bounds)
                && !rememberBox.Bounds.IntersectsWith(cancelBtn.Bounds);
        }
        return ok;
    }
}
