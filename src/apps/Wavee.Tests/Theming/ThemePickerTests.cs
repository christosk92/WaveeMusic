// ── Wavee.Tests/Theming/ThemePickerTests.cs — the picker's data, its Settings row, the regrouped tab and the palette (T1b) ──
//
// The theme picker is driven by `ThemePickerFacts` (installed + current, as data): one card or several, a Developer theme only
// with Developer mode (but always shown while it is the active one), a description when the manifest names one. Its Settings
// row has ONE writer, `ThemeHost.Switch`, and answers the selected card's index. The Appearance tab is regrouped once: Theme ·
// Theme options · Display, then what has no other home yet; Classic lists exactly the SettingsDoc rows of the options section.
// The palette gets a row per option command, after the builtins.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ThemePickerTests : IDisposable
{
    readonly MemoryAppSettings _settings = new();

    public ThemePickerTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    static ThemeComposition Plain(string id, ThemeAvailability availability = ThemeAvailability.Public, string? description = null)
    {
        var theme = Fakes.Theme(id, availability: availability);
        return description is null ? theme : theme with { Manifest = theme.Manifest with { DescriptionKey = description } };
    }

    // ── the entries ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void One_installed_theme_is_one_selected_card()
    {
        ThemeHost.Install(Plain("classic", description: "theme.classic.description"));
        var entries = ThemePickerFacts.State.Peek().Entries(developerMode: false);

        var only = Assert.Single(entries);
        Assert.Equal("classic", only.Id);
        Assert.True(only.Selected);
        Assert.Equal("theme.classic.name", only.NameKey);
        Assert.Equal("theme.classic.description", only.DescriptionKey);
        Assert.Equal(new ThemePreview("l.png", "d.png"), only.Preview);
    }

    [Fact]
    public void Several_themes_keep_install_order_and_exactly_one_is_selected()
    {
        ThemeHost.Install(Plain("classic"), Plain("zune"), Plain("winui"));
        var entries = ThemePickerFacts.State.Peek().Entries(false);

        Assert.Equal(new[] { "classic", "zune", "winui" }, entries.Select(e => e.Id));
        Assert.Equal(new[] { true, false, false }, entries.Select(e => e.Selected));
        Assert.All(entries, e => Assert.Null(e.DescriptionKey));   // a manifest without a description shows no line
    }

    [Fact]
    public async Task A_switch_moves_the_selection()
    {
        ThemeHost.Install(Plain("classic"), Plain("zune"));
        await ThemeHost.Switch("zune");
        Assert.Equal(new[] { false, true }, ThemePickerFacts.State.Peek().Entries(false).Select(e => e.Selected));
    }

    [Fact]
    public void A_developer_theme_needs_developer_mode_unless_it_is_the_active_one()
    {
        ThemeHost.Install(Plain("classic"), Plain("lab", ThemeAvailability.Developer));
        var state = ThemePickerFacts.State.Peek();

        Assert.Equal(new[] { "classic" }, state.Entries(false).Select(e => e.Id));
        Assert.Equal(new[] { "classic", "lab" }, state.Entries(true).Select(e => e.Id));
        Assert.Equal(ThemeAvailability.Developer, state.Entries(true)[1].Availability);

        // Developer mode went off while the Developer theme is active: the picker still shows what is selected.
        var active = new PickerState(state.Installed, "lab");
        Assert.Equal(new[] { "classic", "lab" }, active.Entries(false).Select(e => e.Id));
        Assert.True(active.Entries(false)[1].Selected);
    }

    [Fact]
    public void Before_install_there_is_nothing_to_pick_and_no_selection()
    {
        Assert.Empty(ThemePickerFacts.State.Peek().Entries(true));
        Assert.Equal(-1, SettingsVerbs.ThemeIndex());
        Assert.False(SettingsVerbs.PickTheme(0));
    }

    // ── the Settings row ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_picker_row_answers_the_selected_index_and_its_one_writer_switches_the_theme()
    {
        ThemeHost.Install(Plain("classic"), Plain("zune"), Plain("winui"));
        var row = SettingsDoc.Row(Settings.Tab.Appearance, "themePicker");
        Assert.Equal(SettingsRowKind.Picker, row.Kind);
        Assert.NotNull(row.Writer.Choose);
        Assert.Equal(1, SettingsDocTests.Arms(row.Writer));
        Assert.Equal(0, SettingsVerbs.ThemeIndex());

        long epoch = ThemeHost.RootEpoch.Peek();
        row.Writer.Choose!(2);

        Assert.Equal(2, SettingsVerbs.ThemeIndex());
        Assert.Equal("winui", ThemePickerFacts.State.Peek().CurrentId);
        Assert.Equal("winui", _settings.Get(new SettingKey<string>("theme.id", "")));
        Assert.Equal(epoch + 1, ThemeHost.RootEpoch.Peek());   // the root mounts fresh, once
    }

    [Fact]
    public void Picking_the_selected_card_or_a_card_that_is_not_there_does_nothing()
    {
        ThemeHost.Install(Plain("classic"), Plain("zune"));
        long epoch = ThemeHost.RootEpoch.Peek();

        Assert.False(SettingsVerbs.PickTheme(0));    // already selected
        Assert.False(SettingsVerbs.PickTheme(5));    // the list changed under a stale view
        Assert.False(SettingsVerbs.PickTheme(-1));

        Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());
        Assert.False(_settings.Has("theme.id"));
    }

    [Fact]
    public void The_picker_row_is_in_the_catalog_with_its_own_glyph_and_is_never_developer_only()
    {
        var entry = Settings.Catalog.Rows.Single(r => r.Tab == Settings.Tab.Appearance && r.RowId == "themePicker");
        Assert.Equal("Theme", entry.Section);
        Assert.False(entry.DeveloperOnly);
        Assert.NotEqual(Settings.Catalog.SectionGlyph(Settings.Tab.Appearance, "Theme"), entry.Glyph);
    }

    // ── the regrouped tab ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_appearance_tab_is_theme_then_theme_options_then_display_then_what_has_no_other_home()
    {
        var sections = SettingsDoc.Tabs.Single(t => t.Tab == Settings.Tab.Appearance).Sections;
        Assert.Equal(new[] { "Theme", "Theme options", "Display", "Lyrics", "Fullscreen", "Now playing", "Home" }, sections.Select(s => s.Section));
        Assert.Equal(sections.Select(s => s.Section), Settings.Catalog.Sections.Where(s => s.Tab == Settings.Tab.Appearance).Select(s => s.Title));
        Assert.Equal(ThemeOptionsSection.SectionTitle, sections[1].Section);

        Assert.Equal(new[] { "themePicker" }, sections[0].Rows.Select(r => r.Id));
        Assert.Equal(new[] { "appMode", "zoom", "marquee" }, sections[2].Rows.Select(r => r.Id));
        Assert.Equal(new[] { "daylistClock" }, sections[6].Rows.Select(r => r.Id));
    }

    [Fact]
    public void Classic_lists_exactly_the_rows_of_the_options_section_in_four_groups()
    {
        var legacy = new ClassicStructure().LegacyRows;
        var doc = SettingsDoc.Section(Settings.Tab.Appearance, ThemeOptionsSection.SectionTitle).Rows.Select(r => r.Id).ToArray();

        Assert.Equal(legacy.Count, legacy.Select(l => l.RowId).Distinct().Count());   // no row twice
        Assert.Equal(doc.OrderBy(i => i, StringComparer.Ordinal), legacy.Select(l => l.RowId).OrderBy(i => i, StringComparer.Ordinal));
        foreach (var row in legacy)
        {
            Assert.True(SettingsDoc.TryRow(Settings.Tab.Appearance, row.RowId, out var found), row.RowId);
            Assert.Equal(ThemeOptionsSection.SectionTitle, found.Section);
            Assert.False(string.IsNullOrEmpty(row.Group), row.RowId);
        }
        Assert.Equal(4, legacy.Select(l => l.Group).Distinct().Count());
        // Rows of a group sit together: no group label would ever appear twice.
        var seen = new List<string>();
        foreach (var row in legacy)
            if (seen.Count == 0 || seen[^1] != row.Group) seen.Add(row.Group);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public void The_options_section_of_the_installed_classic_shows_every_legacy_row_with_nothing_generated()
    {
        ThemeHost.Install(ClassicTheme.Create());
        var model = ThemeOptionsSection.Current(developerMode: true);

        Assert.Empty(model.Rows);          // no Classic option is migrated yet
        Assert.False(model.IsEmpty);
        Assert.Equal(13, model.Legacy.Count);
        Assert.Null(model.Reset);          // legacy keys are not the theme's stored options
        Assert.Equal("theme.classic.name", model.NameKey);
        Assert.Empty(ThemeOptionsSection.Commands(true));
    }

    [Fact]
    public void An_unknown_row_id_is_not_a_document_row()
        => Assert.False(SettingsDoc.TryRow(Settings.Tab.Appearance, "noSuchRow", out _));

    [Fact]
    public void Classic_names_and_describes_itself_with_loc_keys_the_picker_can_resolve()
    {
        var manifest = ClassicTheme.Create().Manifest;
        Assert.Equal(Strings.Theme.Classic.Name, manifest.DisplayNameKey);
        Assert.Equal(Strings.Theme.Classic.Description, manifest.DescriptionKey);
    }

    // ── the palette ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Theme_commands_follow_the_builtins_and_find_themselves_by_label()
    {
        int ran = 0;
        var commands = new[]
        {
            new ThemeCommand("theme.structure.rail.on", "Rail: on", () => ran++),
            new ThemeCommand("theme.structure.rail.off", "Rail: off", () => ran += 10),
        };

        var index = Shell.CommandTable.BuildIndex(null, null, commands);
        Assert.Equal(CommandTableShapeTests.BuiltinCount + 2, index.Length);
        Assert.Equal("nav.home", index[0].Id);
        var themeRows = index.Skip(CommandTableShapeTests.BuiltinCount).ToArray();
        Assert.All(themeRows, r => Assert.Equal(Shell.PaletteKind.ThemeOption, r.Kind));
        Assert.Equal(new[] { "Rail: on", "Rail: off" }, themeRows.Select(r => r.Label));
        Assert.Equal(new[] { "rail: on", "rail: off" }, themeRows.Select(r => r.LabelLower));

        var found = new Shell.PaletteEntry[Shell.CommandTable.MaxResults];
        int count = Shell.CommandTable.Filter(index, "rail: off", found, Shell.CommandTable.NewCatalogScratch());
        Assert.True(count >= 1);
        Assert.Equal("theme.structure.rail.off", found[0].Id);
        found[0].Run!();
        Assert.Equal(10, ran);
    }

    [Fact]
    public void Without_theme_commands_the_index_is_exactly_the_builtins()
    {
        Assert.Equal(CommandTableShapeTests.BuiltinCount, Shell.CommandTable.BuildIndex(null, null).Length);
        Assert.Equal(CommandTableShapeTests.BuiltinCount, Shell.CommandTable.BuildIndex(null, null, Array.Empty<ThemeCommand>()).Length);
    }
}
