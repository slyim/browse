using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace SearchPort;

enum ThemePreference { System, Light, Dark }

sealed record AppSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool VerticalTabs { get; init; } = true;
    public bool BlockingEnabled { get; init; } = true;
    public bool CleanLinksEnabled { get; init; } = true;
    public bool SleepingTabsEnabled { get; init; } = true;
    public bool AutoUpdateCheck { get; init; } = true;
    public bool ShareEnabled { get; init; } = true;
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
            return parsed is null ? (new AppSettings(), false) : (parsed, true);
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
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
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
            };
            Save(file, custom);
            var raw = File.ReadAllText(file);
            if (!raw.Contains("\"theme\"", StringComparison.Ordinal) || !raw.Contains("\"light\"", StringComparison.Ordinal) || !raw.Contains("\"verticalTabs\"", StringComparison.Ordinal))
                return false;
            var (loaded, loadedExists) = Load(file);
            return loadedExists && loaded == custom;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }
}

sealed class SettingsDialog : Form
{
    readonly ComboBox themeBox;
    readonly ComboBox layoutBox;
    readonly CheckBox blockingCheck;
    readonly CheckBox cleanLinksCheck;
    readonly CheckBox sleepingCheck;
    readonly CheckBox autoUpdateCheck;
    readonly CheckBox shareCheck;
    readonly Button clearBtn;
    readonly Label clearHint;
    readonly Action<AppSettings> onChanged;
    readonly Func<Task> onClearBrowsingData;
    readonly bool noUpdateCheckFlag;
    AppSettings current;
    bool syncing;

    internal SettingsDialog(
        AppSettings initial,
        bool noUpdateCheckFlag,
        Action<AppSettings> onChanged,
        Func<Task> onClearBrowsingData)
    {
        current = initial;
        this.noUpdateCheckFlag = noUpdateCheckFlag;
        this.onChanged = onChanged;
        this.onClearBrowsingData = onClearBrowsingData;

        Text = "Settings";
        AccessibleName = "Settings";
        Font = Theme.UiFont;
        ForeColor = Theme.Ink;
        BackColor = Theme.Ground;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        Padding = new(S(20), S(16), S(20), S(16));

        var table = new TableLayoutPanel
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
                Font = Theme.SectionFont,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new(0, first ? 0 : S(4), 0, S(6)),
                AccessibleName = text,
            };
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
                    Font = Theme.HintFont,
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new(0, 0, 0, S(6)),
                };
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
                    Font = Theme.HintFont,
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new(S(20), 0, 0, S(4)),
                };
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
        AddSection("Data");
        clearHint = new Label
        {
            Text = "Cookies, cache, history, and site data",
            Font = Theme.HintFont,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new(0, S(4), S(12), S(4)),
        };
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

        AddDivider();
        var aboutText = $"browse {Updater.CurrentVersionText}";
        var aboutLbl = new Label
        {
            Text = aboutText,
            Font = Theme.HintFont,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new(0, S(6), S(12), S(2)),
            AccessibleName = $"About {aboutText}",
        };
        var closeBtn = new Button
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

        themeBox.SelectedIndexChanged += (_, _) => Emit();
        layoutBox.SelectedIndexChanged += (_, _) => Emit();
        blockingCheck.CheckedChanged += (_, _) => Emit();
        cleanLinksCheck.CheckedChanged += (_, _) => Emit();
        sleepingCheck.CheckedChanged += (_, _) => Emit();
        autoUpdateCheck.CheckedChanged += (_, _) => Emit();
        shareCheck.CheckedChanged += (_, _) => Emit();
        clearBtn.Click += async (_, _) => await ClearDataClickedAsync();

        var width = S(420);
        table.Width = width - Padding.Horizontal;
        var pref = table.GetPreferredSize(new Size(width - Padding.Horizontal, 0));
        ClientSize = new(width, pref.Height + Padding.Vertical);
    }

    int S(int px) => LogicalToDeviceUnits(px);

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
