// ── Wavee.Tests/SettingsPageContractTests.cs — the Settings page's published keys, Classic's registration and the core's option grouping (themes plan 6.0, T11b) ──
//
// The Settings page is the structure's picture since T11b: the masthead, the seven-tab pivot, the scroll lane and every tab's sections and rows are drawn from
// `SettingsDoc` by `Pages.Settings`; the core host keeps the hooks, the page epoch, the Storage census and the pivot's cell. What is pure about the contract:
// the ids (a published id never changes), Classic registering every key under a distinct template id, the props record changing when the epoch does (a host rebuilds
// one per render and an equal record skips the template), a row's value read through `Current` (a theme never reaches the store), and `SettingsThemeOptions`, the
// grouping of the active theme's options (one group per label, a group's generated rows above its legacy rows, developer rows absent, the wizard's subset).

using FluentGpu.Signals;
using FluentGpu.WindowsApi.Notifications;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class SettingsPageContractTests : IDisposable
{
    readonly MemoryAppSettings _settings = new();

    public SettingsPageContractTests() => Platform.UseSettings(_settings);

    public void Dispose() => Platform.UseSettings(null);

    /// <summary>A scriptable world: only the developer switch matters here.</summary>
    sealed record Facts(bool Developer) : ISettingsFacts
    {
        public bool DeveloperMode => Developer;
        public bool Packaged => false;
        public Tray.IconMode TrayMode => Tray.IconMode.WhileHidden;
        public bool StartOnLogin => true;
        public bool WindowsNotifications => true;
        public bool QuietHours => true;
        public ToastDeliverySetting ToastDelivery => ToastDeliverySetting.Enabled;
        public bool CanDerive => true;
        public bool EqualizerEnabled => true;
        public bool CrossfadeEnabled => true;
        public bool VideoOverridesPresent => true;
        public bool SidebarEditing => false;
        public bool SidebarModified => false;
        public bool AudioCacheAvailable => true;
        public bool MetadataClearAvailable => true;
        public bool CrashServiceConfigured => true;
        public bool CrashReportingOn => true;
        public bool TestCrashBusy => false;
    }

    static ThemeOptionsModel Model(params LegacyOptionRow[] legacy)
        => new("classic", "theme.classic.name", Array.Empty<GeneratedOptionRow>(), legacy, null);

    // ── the keys ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Pages.Settings", ViewKeys.SettingsPage.Id);
        Assert.Equal("Settings.ThemePicker", ViewKeys.SettingsThemePicker.Id);
        Assert.Equal("Settings.ThemeOptions", ViewKeys.SettingsThemeOptions.Id);
    }

    [Fact]
    public void Classic_registers_every_settings_key_under_a_distinct_template_id()
    {
        var classic = ClassicTheme.Create();
        (string Id, string Template)[] expected =
        [
            (ViewKeys.SettingsPage.Id, "classic.screens.settings"),
            (ViewKeys.SettingsThemePicker.Id, "classic.screens.settings.theme-picker"),
            (ViewKeys.SettingsThemeOptions.Id, "classic.screens.settings.theme-options"),
        ];
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, template) in expected)
        {
            Assert.Equal(template, classic.Registry.TemplateIdOf(id));
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Empty(classic.Validate());
    }

    // ── the props ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_page_props_record_changes_with_the_revision_the_tab_and_the_census()
    {
        Func<Signal<int>> cell = static () => new Signal<int>(0);
        var props = new SettingsPageProps { Area = 1, Revision = 3, AreaCell = cell, Storage = SettingsStorageProps.Idle };

        Assert.Equal(props, props with { });
        Assert.NotEqual(props, props with { Revision = 4 });
        Assert.NotEqual(props, props with { Area = 2 });
        Assert.NotEqual(props, props with { Storage = SettingsStorageProps.Idle with { Phase = Settings.StorageLoadPhase.Loading } });
    }

    [Fact]
    public void Another_tab_carries_no_census_and_the_idle_one_holds_nothing_to_read()
    {
        var idle = SettingsStorageProps.Idle;
        Assert.Equal(Settings.StorageLoadPhase.NotStarted, idle.Phase);
        Assert.Null(idle.Snapshot);
        Assert.Null(idle.Audio);
        Assert.Null(idle.KeyCount);
        Assert.Null(idle.Meta);
        Assert.Empty(idle.AudioDirectory);
    }

    // ── a row's value ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_row_answers_its_value_from_the_live_store_through_Current()
    {
        var row = SettingsDoc.Row(Settings.Tab.General, "closeToTray");
        _settings.Set(Platform.Keys.TrayCloseToTray, true);
        Assert.True(row.Current is true);
        _settings.Set(Platform.Keys.TrayCloseToTray, false);
        Assert.False(row.Current is true);
        Assert.Equal(row.Value(_settings), row.Current);
    }

    // ── the options as the page shows them ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_legacy_rows_come_in_order_one_group_per_label()
    {
        var model = Model(new LegacyOptionRow("sidebarReset", "Lists"), new LegacyOptionRow("sidebarCustomize", "Side"),
            new LegacyOptionRow("sidebarResetEverything", "Lists"));

        var groups = SettingsThemeOptions.Groups(model, new Facts(false), wizard: false);

        Assert.Equal(new[] { "Lists", "Side" }, groups.Select(g => g.Title));
        Assert.Equal(new[] { "legacy:sidebarReset", "legacy:sidebarResetEverything" }, groups[0].Items.Select(i => i.Key));
        Assert.Equal(new[] { "legacy:sidebarCustomize" }, groups[1].Items.Select(i => i.Key));
        Assert.All(groups.SelectMany(g => g.Items), i => Assert.Equal(SettingsOptionItemKind.Legacy, i.Kind));
    }

    [Fact]
    public void A_developer_only_row_is_absent_until_developer_mode_and_an_unknown_row_is_left_out()
    {
        var model = Model(new LegacyOptionRow("npvPresentation", "Motion"), new LegacyOptionRow("noSuchRow", "Motion"));

        Assert.Empty(SettingsThemeOptions.Groups(model, new Facts(false), wizard: false));

        var shown = Assert.Single(SettingsThemeOptions.Groups(model, new Facts(true), wizard: false));
        var item = Assert.Single(shown.Items);
        Assert.Equal("legacy:npvPresentation", item.Key);
    }

    [Fact]
    public void The_wizard_shows_no_legacy_row()
    {
        var model = Model(new LegacyOptionRow("sidebarCustomize", "Side"), new LegacyOptionRow("sidebarResetEverything", "Lists"),
            new LegacyOptionRow("sidebarReset", "Side"));

        Assert.Empty(SettingsThemeOptions.Groups(model, new Facts(false), wizard: true));
    }

    [Fact]
    public void A_groups_generated_rows_come_before_its_legacy_rows_and_the_groups_keep_the_legacy_order()
    {
        // Classic's sidebar group: the layout cards, the density and the covers toggle (generated) sit above the three sidebar actions (legacy), as the rows they replaced did;
        // the groups that hold generated rows only (Surfaces, then the track pages' layout and rails) come after the legacy groups, in schema order.
        var theme = ClassicTheme.Create();
        var model = ThemeOptionsSection.Build(theme, ThemeOptionStore.Load(theme, _settings), developerMode: false);

        var groups = SettingsThemeOptions.Groups(model, new Facts(false), wizard: false);

        Assert.Equal(new[] { Strings.Settings.ThemeOptions.Group.Sidebar, Strings.Settings.ThemeOptions.Group.TrackLists,
            Strings.Settings.ThemeOptions.Group.Surfaces, Strings.Settings.ThemeOptions.Group.TrackPages }.Select(ThemeOptionsSection.GroupTitle), groups.Select(g => g.Title));
        Assert.Equal(new[] { "opt:structure.sidebar", "opt:structure.libraryDensity", "opt:structure.showCovers", "legacy:sidebarReset", "legacy:sidebarCustomize", "legacy:sidebarResetEverything" },
            groups[0].Items.Select(i => i.Key));
        Assert.Equal(new[] { "opt:structure.surfaces", "opt:structure.accentFromArtwork", "opt:structure.nowPlayingColors" }, groups[2].Items.Select(i => i.Key));
        Assert.Equal(new[] { "opt:structure.pageLayout", "opt:structure.railUniform", "opt:structure.clearRememberedSizes" }, groups[3].Items.Select(i => i.Key));
    }

    [Fact]
    public void The_wizard_shows_the_generated_rows_that_are_not_actions()
    {
        var theme = ClassicTheme.Create();
        var model = ThemeOptionsSection.Build(theme, ThemeOptionStore.Load(theme, _settings), developerMode: false);

        var groups = SettingsThemeOptions.Groups(model, new Facts(false), wizard: true);

        var keys = groups.SelectMany(g => g.Items).Select(i => i.Key).ToArray();
        Assert.Equal(new[] { "opt:structure.sidebar", "opt:structure.libraryDensity" }, keys.Take(2));   // the sidebar group opens the page
        Assert.Equal(new[] { "opt:structure.accentFromArtwork", "opt:structure.hideTrackArt", "opt:structure.libraryDensity", "opt:structure.nowPlayingColors", "opt:structure.pageLayout", "opt:structure.railUniform", "opt:structure.rowStyle", "opt:structure.showCovers", "opt:structure.sidebar", "opt:structure.surfaces", "opt:style.density" },
            keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(groups.SelectMany(g => g.Items), i => Assert.Equal(SettingsOptionItemKind.Generated, i.Kind));
    }

    [Fact]
    public void A_migration_that_dropped_something_adds_its_note_after_the_reset_everything_row()
    {
        _settings.Set(Platform.Keys.SidebarMigrationDropped, "playlists");
        var model = Model(new LegacyOptionRow("sidebarResetEverything", "Side"));

        var group = Assert.Single(SettingsThemeOptions.Groups(model, new Facts(false), wizard: false));

        Assert.Equal(new[] { SettingsOptionItemKind.Legacy, SettingsOptionItemKind.SidebarWhatChanged }, group.Items.Select(i => i.Kind));
        Assert.False(string.IsNullOrEmpty(group.Items[1].Text));
    }

    [Fact]
    public void A_theme_with_no_rows_has_no_groups()
    {
        var groups = SettingsThemeOptions.Groups(Model(), new Facts(true), wizard: false);

        Assert.Empty(groups);
    }
}
