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
    public bool BlockingEnabled { get; init; } = true;
    public bool CleanLinksEnabled { get; init; } = true;
    public bool SleepingTabsEnabled { get; init; } = true;
    public bool AutoUpdateCheck { get; init; } = true;
    public bool ShareEnabled { get; init; } = true;
    public ExternalAppPermission[] AllowedExternalApps { get; init; } = [];

    public bool Equals(AppSettings? other) =>
        other is not null
        && Theme == other.Theme
        && VerticalTabs == other.VerticalTabs
        && BlockingEnabled == other.BlockingEnabled
        && CleanLinksEnabled == other.CleanLinksEnabled
        && SleepingTabsEnabled == other.SleepingTabsEnabled
        && AutoUpdateCheck == other.AutoUpdateCheck
        && ShareEnabled == other.ShareEnabled
        && (AllowedExternalApps ?? []).SequenceEqual(other.AllowedExternalApps ?? []);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Theme);
        hash.Add(VerticalTabs);
        hash.Add(BlockingEnabled);
        hash.Add(CleanLinksEnabled);
        hash.Add(SleepingTabsEnabled);
        hash.Add(AutoUpdateCheck);
        hash.Add(ShareEnabled);
        foreach (var p in AllowedExternalApps ?? []) hash.Add(p);
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
            return (parsed with { AllowedExternalApps = ExternalSchemes.NormalizePermissions(parsed.AllowedExternalApps) }, true);
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
        var normalized = settings with { AllowedExternalApps = ExternalSchemes.NormalizePermissions(settings.AllowedExternalApps) };
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
            if (!partialExists || partial.Theme != ThemePreference.Dark || partial.BlockingEnabled || !partial.VerticalTabs || !partial.CleanLinksEnabled)
                return false;

            var custom = new AppSettings
            {
                Theme = ThemePreference.Light,
                VerticalTabs = false,
                BlockingEnabled = false,
                CleanLinksEnabled = false,
                SleepingTabsEnabled = false,
                AutoUpdateCheck = false,
                ShareEnabled = false,
                AllowedExternalApps = [new ExternalAppPermission("https://example.com", "mailto")],
            };
            Save(file, custom);
            var raw = File.ReadAllText(file);
            if (!raw.Contains("\"theme\"", StringComparison.Ordinal) || !raw.Contains("\"light\"", StringComparison.Ordinal) || !raw.Contains("\"verticalTabs\"", StringComparison.Ordinal) || !raw.Contains("\"allowedExternalApps\"", StringComparison.Ordinal))
                return false;
            var (loaded, loadedExists) = Load(file);
            if (!loadedExists || loaded != custom) return false;

            // Dangerous or invalid entries in settings.json must be dropped on load.
            File.WriteAllText(file, """{"allowedExternalApps":[{"site":"https://example.com","scheme":"ms-msdt"},{"site":"https://example.com","scheme":"vscode"}]}""");
            var (sanitized, sanitizedExists) = Load(file);
            return sanitizedExists && sanitized.AllowedExternalApps.SequenceEqual([new ExternalAppPermission("https://example.com", "vscode")]);
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }
}

sealed class SettingsDialog : Form
{
    readonly TableLayoutPanel table;
    readonly List<Label> sectionLabels = [];
    readonly List<Label> hintLabels = [];
    readonly ComboBox themeBox;
    readonly ComboBox layoutBox;
    readonly CheckBox blockingCheck;
    readonly CheckBox cleanLinksCheck;
    readonly CheckBox sleepingCheck;
    readonly CheckBox autoUpdateCheck;
    readonly CheckBox shareCheck;
    readonly Label defaultBrowserHint;
    readonly FlowLayoutPanel defaultBrowserButtons;
    readonly Button makeDefaultBtn;
    readonly Button removeDefaultBtn;
    readonly Button clearBtn;
    readonly Label clearAppsHint;
    readonly Button clearAppsBtn;
    readonly Button closeBtn;
    readonly Label clearHint;
    readonly Action<AppSettings> onChanged;
    readonly Func<Task> onClearBrowsingData;
    readonly Func<bool> isDefaultBrowserRegistered;
    readonly Action onRegisterDefaultBrowser;
    readonly Action onUnregisterDefaultBrowser;
    readonly bool noUpdateCheckFlag;
    readonly int? dpiOverride;
    int currentDpi;
    AppSettings current;
    bool syncing;

    internal SettingsDialog(
        AppSettings initial,
        bool noUpdateCheckFlag,
        Action<AppSettings> onChanged,
        Func<Task> onClearBrowsingData,
        Func<bool>? isDefaultBrowserRegistered = null,
        Action? onRegisterDefaultBrowser = null,
        Action? onUnregisterDefaultBrowser = null,
        int? dpiOverride = null)
    {
        current = initial;
        this.noUpdateCheckFlag = noUpdateCheckFlag;
        this.onChanged = onChanged;
        this.onClearBrowsingData = onClearBrowsingData;
        this.isDefaultBrowserRegistered = isDefaultBrowserRegistered ?? (() => false);
        this.onRegisterDefaultBrowser = onRegisterDefaultBrowser ?? (() => { });
        this.onUnregisterDefaultBrowser = onUnregisterDefaultBrowser ?? (() => { });
        this.dpiOverride = dpiOverride;
        currentDpi = dpiOverride ?? DeviceDpi;

        Text = "Settings";
        AccessibleName = "Settings";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.FontForDpi(Theme.UiFont, currentDpi);
        ForeColor = Theme.Ink;
        BackColor = Theme.Ground;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        Padding = new(S(20), S(16), S(20), S(16));

        table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Theme.Ground,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var row = 0;
        void AddSection(string text, bool first = false)
        {
            var lbl = new Label
            {
                Text = text,
                Font = Theme.FontForDpi(Theme.SectionFont, currentDpi),
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new(0, first ? 0 : S(4), 0, S(6)),
                AccessibleName = text,
            };
            sectionLabels.Add(lbl);
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(lbl, 0, row);
            table.SetColumnSpan(lbl, 2);
            row++;
        }

        void AddDivider()
        {
            var line = new Panel
            {
                Height = 1,
                Dock = DockStyle.Fill,
                BackColor = Theme.Hairline,
                Margin = new(0, S(10), 0, S(10)),
            };
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(line, 0, row);
            table.SetColumnSpan(line, 2);
            row++;
        }

        ComboBox AddChoiceRow(string labelText, string[] items, string accessibleName, string? hint = null)
        {
            var lbl = new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new(0, S(4), S(12), S(4)),
            };
            var box = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = S(132),
                Anchor = AnchorStyles.Right,
                Margin = new(0, S(2), 0, S(2)),
                AccessibleName = accessibleName,
                TabStop = true,
            };
            box.Items.AddRange(items);
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(lbl, 0, row);
            table.Controls.Add(box, 1, row);
            row++;

            if (hint is not null)
            {
                var hintLbl = new Label
                {
                    Text = hint,
                    Font = Theme.FontForDpi(Theme.HintFont, currentDpi),
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new(0, 0, 0, S(6)),
                };
                hintLabels.Add(hintLbl);
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(hintLbl, 0, row);
                table.SetColumnSpan(hintLbl, 2);
                row++;
            }
            return box;
        }

        CheckBox AddToggle(string text, string accessibleName, string? hint = null)
        {
            var cb = new CheckBox
            {
                Text = text,
                AutoSize = true,
                ForeColor = Theme.Ink,
                BackColor = Theme.Ground,
                Margin = new(0, S(3), 0, hint is null ? S(4) : S(1)),
                AccessibleName = accessibleName,
                TabStop = true,
            };
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(cb, 0, row);
            table.SetColumnSpan(cb, 2);
            row++;

            if (hint is not null)
            {
                var hintLbl = new Label
                {
                    Text = hint,
                    Font = Theme.FontForDpi(Theme.HintFont, currentDpi),
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new(S(20), 0, 0, S(4)),
                };
                hintLabels.Add(hintLbl);
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(hintLbl, 0, row);
                table.SetColumnSpan(hintLbl, 2);
                row++;
            }
            return cb;
        }

        AddSection("Appearance", first: true);
        themeBox = AddChoiceRow("Theme", ["System", "Light", "Dark"], "Theme", "Applies on next launch.");
        layoutBox = AddChoiceRow("Tab layout", ["Sidebar", "Top tabs"], "Tab layout");

        AddDivider();
        AddSection("Privacy & features");
        blockingCheck = AddToggle("Ad && tracker blocking", "Ad and tracker blocking");
        cleanLinksCheck = AddToggle("Clean tracking links", "Clean tracking links");
        sleepingCheck = AddToggle("Sleep idle background tabs", "Sleep idle background tabs");
        autoUpdateCheck = AddToggle(
            "Check for updates automatically",
            "Check for updates automatically",
            noUpdateCheckFlag ? "Disabled for this session by --no-update-check." : null);
        if (noUpdateCheckFlag) autoUpdateCheck.Enabled = false;
        shareCheck = AddToggle("Show Share button in toolbar", "Show Share button");

        AddDivider();
        AddSection("Default browser");
        defaultBrowserHint = new Label
        {
            Font = Theme.FontForDpi(Theme.HintFont, currentDpi),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Margin = new(0, 0, 0, S(6)),
        };
        hintLabels.Add(defaultBrowserHint);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(defaultBrowserHint, 0, row);
        table.SetColumnSpan(defaultBrowserHint, 2);
        row++;

        makeDefaultBtn = new Button
        {
            Text = "Make browse available as a default browser",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new(S(8), S(2), S(8), S(2)),
            Margin = new(0, 0, S(8), 0),
            AccessibleName = "Make browse available as a default browser",
            TabStop = true,
        };
        removeDefaultBtn = new Button
        {
            Text = "Remove",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new(S(8), S(2), S(8), S(2)),
            Margin = Padding.Empty,
            AccessibleName = "Remove default browser registration",
            TabStop = true,
        };
        defaultBrowserButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Ground,
            Margin = new(0, S(2), 0, S(2)),
            Padding = Padding.Empty,
        };
        defaultBrowserButtons.Controls.Add(makeDefaultBtn);
        defaultBrowserButtons.Controls.Add(removeDefaultBtn);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(defaultBrowserButtons, 0, row);
        table.SetColumnSpan(defaultBrowserButtons, 2);
        row++;

        AddDivider();
        AddSection("Data");
        clearHint = new Label
        {
            Text = "Cookies, cache, history, and site data",
            Font = Theme.FontForDpi(Theme.HintFont, currentDpi),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new(0, S(4), S(12), S(4)),
        };
        hintLabels.Add(clearHint);
        clearBtn = new Button
        {
            Text = "Clear browsing data...",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new(S(8), S(2), S(8), S(2)),
            Anchor = AnchorStyles.Right,
            Margin = new(0, S(2), 0, S(2)),
            AccessibleName = "Clear browsing data",
            TabStop = true,
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(clearHint, 0, row);
        table.Controls.Add(clearBtn, 1, row);
        row++;

        clearAppsHint = new Label
        {
            Font = Theme.FontForDpi(Theme.HintFont, currentDpi),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new(0, S(4), S(12), S(4)),
        };
        hintLabels.Add(clearAppsHint);
        clearAppsBtn = new Button
        {
            Text = "Clear remembered app permissions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new(S(8), S(2), S(8), S(2)),
            Anchor = AnchorStyles.Right,
            Margin = new(0, S(2), 0, S(2)),
            AccessibleName = "Clear remembered app permissions",
            TabStop = true,
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(clearAppsHint, 0, row);
        table.Controls.Add(clearAppsBtn, 1, row);
        row++;

        AddDivider();
        var aboutText = $"browse {Updater.CurrentVersionText}";
        var aboutLbl = new Label
        {
            Text = aboutText,
            Font = Theme.FontForDpi(Theme.HintFont, currentDpi),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new(0, S(6), S(12), S(2)),
            AccessibleName = $"About {aboutText}",
        };
        hintLabels.Add(aboutLbl);
        closeBtn = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new(S(78), 0),
            Padding = new(S(8), S(2), S(8), S(2)),
            Anchor = AnchorStyles.Right,
            Margin = new(0, S(2), 0, 0),
            AccessibleName = "Close settings",
            TabStop = true,
        };
        closeBtn.Click += (_, _) => Close();
        CancelButton = closeBtn;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(aboutLbl, 0, row);
        table.Controls.Add(closeBtn, 1, row);

        Controls.Add(table);
        SyncFrom(initial);
        RefreshDefaultBrowserState();

        themeBox.SelectedIndexChanged += (_, _) => Emit();
        layoutBox.SelectedIndexChanged += (_, _) => Emit();
        blockingCheck.CheckedChanged += (_, _) => Emit();
        cleanLinksCheck.CheckedChanged += (_, _) => Emit();
        sleepingCheck.CheckedChanged += (_, _) => Emit();
        autoUpdateCheck.CheckedChanged += (_, _) => Emit();
        shareCheck.CheckedChanged += (_, _) => Emit();
        makeDefaultBtn.Click += (_, _) => RegisterDefaultBrowserClicked();
        removeDefaultBtn.Click += (_, _) => UnregisterDefaultBrowserClicked();
        clearBtn.Click += async (_, _) => await ClearDataClickedAsync();
        clearAppsBtn.Click += (_, _) => ClearExternalAppsClicked();

        FitClientSize();
    }

    int S(int px) => dpiOverride is { } dpi
        ? (int)Math.Round(px * (dpi / 96.0), MidpointRounding.AwayFromZero)
        : LogicalToDeviceUnits(px);

    void ApplyFonts(int dpi)
    {
        Font = Theme.FontForDpi(Theme.UiFont, dpi);
        var sectionFont = Theme.FontForDpi(Theme.SectionFont, dpi);
        foreach (var lbl in sectionLabels) lbl.Font = sectionFont;
        var hintFont = Theme.FontForDpi(Theme.HintFont, dpi);
        foreach (var lbl in hintLabels) lbl.Font = hintFont;
    }

    void FitClientSize()
    {
        var minWidth = S(420);
        var pref = table.GetPreferredSize(new Size(minWidth - Padding.Horizontal, 0));
        var width = Math.Max(minWidth, pref.Width + Padding.Horizontal);
        table.Width = width - Padding.Horizontal;
        pref = table.GetPreferredSize(new Size(table.Width, 0));
        ClientSize = new(width, pref.Height + Padding.Vertical);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (dpiOverride is null && DeviceDpi != currentDpi)
        {
            var factor = (float)DeviceDpi / currentDpi;
            currentDpi = DeviceDpi;
            Scale(new SizeF(factor, factor));
            ApplyFonts(currentDpi);
            FitClientSize();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (dpiOverride is not null) return;
        currentDpi = e.DeviceDpiNew;
        Padding = new(S(20), S(16), S(20), S(16));
        themeBox.Width = S(132);
        layoutBox.Width = S(132);
        makeDefaultBtn.Padding = new(S(8), S(2), S(8), S(2));
        makeDefaultBtn.Margin = new(0, 0, S(8), 0);
        removeDefaultBtn.Padding = new(S(8), S(2), S(8), S(2));
        clearBtn.Padding = new(S(8), S(2), S(8), S(2));
        clearAppsBtn.Padding = new(S(8), S(2), S(8), S(2));
        closeBtn.MinimumSize = new(S(78), 0);
        closeBtn.Padding = new(S(8), S(2), S(8), S(2));
        ApplyFonts(currentDpi);
        FitClientSize();
    }

    internal bool VerifyLayoutInsideClient()
    {
        CreateControl();
        PerformLayout();
        var client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0 || !client.Contains(table.Bounds)) return false;
        foreach (Control c in table.Controls)
        {
            if (!c.Visible) continue;
            var r = new Rectangle(table.Left + c.Left, table.Top + c.Top, c.Width, c.Height);
            if (r.Width <= 0 || r.Height <= 0 || !client.Contains(r)) return false;
            if (c is FlowLayoutPanel flow)
            {
                foreach (Control child in flow.Controls)
                {
                    if (!child.Visible) continue;
                    var cr = new Rectangle(r.Left + child.Left, r.Top + child.Top, child.Width, child.Height);
                    if (cr.Width <= 0 || cr.Height <= 0 || !client.Contains(cr)) return false;
                }
            }
        }
        return true;
    }

    static string FormatExternalAppsHint(int count) => count switch
    {
        0 => "No remembered app permissions",
        1 => "1 remembered app permission",
        _ => $"{count} remembered app permissions",
    };

    void RefreshDefaultBrowserState(string? statusOverride = null)
    {
        var registered = isDefaultBrowserRegistered();
        removeDefaultBtn.Enabled = registered;
        defaultBrowserHint.Text = statusOverride ?? (registered
            ? "Registered for this user. Choose browse in Windows Default Apps."
            : "Register browse for this user, then choose it in Windows Default Apps.");
    }

    void RegisterDefaultBrowserClicked()
    {
        try
        {
            onRegisterDefaultBrowser();
            RefreshDefaultBrowserState();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                MessageBox.Show(this, ex.Message, "Could not register browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void UnregisterDefaultBrowserClicked()
    {
        try
        {
            onUnregisterDefaultBrowser();
            RefreshDefaultBrowserState("Default browser registration removed.");
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                MessageBox.Show(this, ex.Message, "Could not remove registration", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ClearExternalAppsClicked()
    {
        if (current.AllowedExternalApps.Length == 0) return;
        current = current with { AllowedExternalApps = [] };
        onChanged(current);
        clearAppsBtn.Enabled = false;
        clearAppsHint.Text = "Remembered app permissions cleared.";
    }

    internal void SyncFrom(AppSettings next)
    {
        syncing = true;
        try
        {
            current = next;
            themeBox.SelectedIndex = next.Theme switch
            {
                ThemePreference.Light => 1,
                ThemePreference.Dark => 2,
                _ => 0,
            };
            layoutBox.SelectedIndex = next.VerticalTabs ? 0 : 1;
            blockingCheck.Checked = next.BlockingEnabled;
            cleanLinksCheck.Checked = next.CleanLinksEnabled;
            sleepingCheck.Checked = next.SleepingTabsEnabled;
            autoUpdateCheck.Checked = !noUpdateCheckFlag && next.AutoUpdateCheck;
            shareCheck.Checked = next.ShareEnabled;
            var appCount = next.AllowedExternalApps?.Length ?? 0;
            clearAppsHint.Text = FormatExternalAppsHint(appCount);
            clearAppsBtn.Enabled = appCount > 0;
        }
        finally { syncing = false; }
    }

    void Emit()
    {
        if (syncing) return;
        var next = current with
        {
            Theme = themeBox.SelectedIndex switch
            {
                1 => ThemePreference.Light,
                2 => ThemePreference.Dark,
                _ => ThemePreference.System,
            },
            VerticalTabs = layoutBox.SelectedIndex == 0,
            BlockingEnabled = blockingCheck.Checked,
            CleanLinksEnabled = cleanLinksCheck.Checked,
            SleepingTabsEnabled = sleepingCheck.Checked,
            AutoUpdateCheck = noUpdateCheckFlag ? current.AutoUpdateCheck : autoUpdateCheck.Checked,
            ShareEnabled = shareCheck.Checked,
        };
        if (next == current) return;
        current = next;
        onChanged(next);
    }

    async Task ClearDataClickedAsync()
    {
        if (MessageBox.Show(this,
            "Clear cookies, cache, browsing history, and site data for this profile?\n\nOpen sites may sign you out.",
            "Clear browsing data",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        clearBtn.Enabled = false;
        var prevText = clearHint.Text;
        clearHint.Text = "Clearing browsing data...";
        try
        {
            await onClearBrowsingData();
            if (!IsDisposed) clearHint.Text = "Browsing data cleared.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                clearHint.Text = prevText;
                MessageBox.Show(this, ex.Message, "Could not clear browsing data", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!IsDisposed && !clearBtn.IsDisposed) clearBtn.Enabled = true;
        }
    }
}
