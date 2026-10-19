// ── Wavee.Tests/Theming/WinUiScreensTests.cs — WinUI's screens: the seven Settings tabs, the slugs, the six options, the records (P6 W8b) ──────────
//
// Pure facts (no host): the grouping WinUI publishes validates and draws every document section exactly once; EVERY old settings slug (the twelve area slugs, the eleven
// document-tab slugs and the aliases the page ever answered to) lands on a tab that holds what it opened; the Settings records follow the boards (title 28 / 40, the 1000 /
// 1200 cap, the stacked rows below 560 content width, the picker's forms); the option order names exactly the six W-ThemeOptions options, each in the schema of its layer.

using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class WinUiScreensTests
{
    static readonly SettingsGrouping Grouping = WinUiSettingsGrouping.Seven;

    // ── the grouping ───────────────────────────────────────────────────────────────────────�

    [Fact]
    public void WinUi_publishes_the_seven_tab_grouping_in_its_policies()
    {
        var policies = WinUiPolicies.For(WinUiParameters.Default);
        Assert.Same(Grouping, policies.Settings);
        Assert.Empty(policies.Validate());
        Assert.Empty(Grouping.Validate());
        Assert.Equal(7, Grouping.Count);
        Assert.Equal(["general", "appearance", "playback", "notifications", "storage-cache", "privacy-diagnostics", "about"], Grouping.Tabs.Select(t => t.Slug));
        Assert.Equal(
        [
            "theme.winui.screens.tabGeneral", "theme.winui.screens.tabAppearance", "theme.winui.screens.tabPlayback", "theme.winui.screens.tabNotifications",
            "theme.winui.screens.tabStorage", "theme.winui.screens.tabPrivacy", "theme.winui.screens.tabAbout",
        ], Grouping.Tabs.Select(t => t.TitleKey));
    }

    [Fact]
    public void Every_document_section_is_drawn_by_exactly_one_tab_and_general_holds_the_window_rows()
    {
        for (int a = 0; a < Settings.AreaSlugs.Length; a++)
            foreach (var part in Settings.PartsOf((Settings.Area)a))
                Assert.True(Grouping.GroupOfPart(part) >= 0, $"{part.Tab}/{part.Section} has no tab");
        Assert.Equal(0, Grouping.GroupOfPart(new Settings.Part(Settings.Tab.General, "Language & region")));
        Assert.Equal(0, Grouping.GroupOfPart(new Settings.Part(Settings.Tab.General, "Graphics")));
        Assert.Equal(0, Grouping.GroupOfPart(new Settings.Part(Settings.Tab.Keyboard, "Shortcuts")));
        Assert.Equal(1, Grouping.GroupOfPart(new Settings.Part(Settings.Tab.Appearance, "Theme")));
        Assert.Equal(1, Grouping.GroupOfPart(new Settings.Part(Settings.Tab.Appearance, "Lyrics")));
        Assert.Equal(5, Grouping.GroupOfPart(new Settings.Part(Settings.Tab.Feedback, "Help")));
    }

    /// <summary>Every slug the page ever answered to: the area slugs, the document-tab slugs, the old aliases and the new tab slugs.</summary>
    public static TheoryData<string, int> Slugs() => new()
    {
        // the twelve area slugs (each lands on its area, shown on the tab that holds it)
        { "account", 0 }, { "appearance", 1 }, { "playback", 2 }, { "audio", 2 }, { "lyrics", 1 }, { "devices", 0 }, { "storage", 4 },
        { "notifications", 3 }, { "keyboard", 0 }, { "privacy", 5 }, { "feedback", 5 }, { "about", 6 },
        // the seven old document-tab slugs (`general` is the tab's own slug now: it lands on General, where the window rows live)
        { "general", 0 },
        // the aliases the page answered to
        { "logs", 5 }, { "diagnostics", 5 }, { "help", 5 }, { "feedback-help", 5 }, { "downloads", 4 }, { "downloads-storage", 4 }, { "cache", 4 },
        { "devices-connect", 0 }, { "connect", 0 }, { "shortcuts", 0 },
        // WinUI's own tab slugs
        { "storage-cache", 4 }, { "privacy-diagnostics", 5 },
        // an unknown or empty arg opens where the page always opened a bad arg: Appearance
        { "nope", 1 }, { "", 1 },
    };

    [Theory, MemberData(nameof(Slugs))]
    public void Every_old_settings_slug_lands_on_the_tab_that_holds_its_area(string slug, int tab)
    {
        Assert.Equal(tab, Grouping.GroupFromSlug(slug));
        Assert.Contains(Grouping.AreaFromSlug(slug), Grouping.Tabs[tab].Areas);
    }

    [Fact]
    public void Every_slug_in_the_core_tables_is_covered_by_a_case_above()
    {
        var covered = new HashSet<string>(Slugs().Select(d => d.Data.Item1), StringComparer.OrdinalIgnoreCase);
        foreach (string slug in Settings.AreaSlugs) Assert.Contains(slug, covered);
        foreach (string slug in Settings.TabSlugs) Assert.Contains(slug, covered);
    }

    // ── the records ────────────────────────────────────────────────────────────────────────�

    [Fact]
    public void The_settings_records_follow_their_boards()
    {
        var compact = WinUiSettingsTiers.For(WidthTier.Compact);
        Assert.Equal((16f, 16f, 28f, 36f), (compact.InsetX, compact.InsetTop, compact.TitleSize, compact.TitleLine));
        Assert.True(compact.Touch);
        Assert.Equal(40f, compact.Target);
        Assert.True(compact.TabsScroll);
        Assert.False(compact.CapsColumn);
        Assert.Equal(WinUiThemePickerForm.Grid, compact.Picker);

        var small = WinUiSettingsTiers.For(WidthTier.Small);
        Assert.Equal((24f, 20f, 28f, 36f), (small.InsetX, small.InsetTop, small.TitleSize, small.TitleLine));
        Assert.Equal(1000f, small.ColumnMaxW);
        Assert.Equal(WinUiThemePickerForm.Row, small.Picker);

        var standard = WinUiSettingsTiers.For(WidthTier.Standard);
        Assert.Equal((36f, 24f, 40f, 52f), (standard.InsetX, standard.InsetTop, standard.TitleSize, standard.TitleLine));
        Assert.Equal(1000f, standard.ColumnMaxW);
        Assert.False(standard.Aside);
        Assert.Equal(WinUiThemePickerForm.Cards, standard.Picker);

        var huge = WinUiSettingsTiers.For(WidthTier.Huge);
        Assert.Equal((48f, 32f, 40f, 52f), (huge.InsetX, huge.InsetTop, huge.TitleSize, huge.TitleLine));
        Assert.Equal(1200f, huge.ColumnMaxW);
        Assert.True(huge.Aside);
        Assert.Same(huge, WinUiSettingsTiers.For(WidthTier.Huge4K));      // no Huge 4K record

        // The insets are the frame's: the page's title sits on the content card's inset at every tier.
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            Assert.Equal(WinUiTiers.Frame(tier).InsetX, WinUiSettingsTiers.For(tier).InsetX);
            Assert.Equal(WinUiTiers.Frame(tier).InsetTop, WinUiSettingsTiers.For(tier).InsetTop);
        }
    }

    [Fact]
    public void Cards_stack_their_control_below_560_content_width_and_only_there()
    {
        Assert.Contains(WinUiTiers.Policy.Rungs, r => r.Id == WinUiScreenRules.StackRung && r.Axis == RungAxis.Content && r.Edge.At == 560f);
        // At the Compact board (400 wide: content 400) the rows stack; at Small (800 wide, 48 rail: content 752) they do not.
        var narrow = WindowTiers.Resolve(WinUiTiers.Policy, 400f, 860f, 400f);
        var wide = WindowTiers.Resolve(WinUiTiers.Policy, 800f, 600f, 752f);
        Assert.True(WinUiScreenRules.CardsStack(narrow));
        Assert.False(WinUiScreenRules.CardsStack(wide));
        Assert.True(WinUiSettingsTiers.Of(narrow).StacksControls);
        Assert.False(WinUiSettingsTiers.Of(wide).StacksControls);
        // A view mounted alone (no policy) keeps the controls beside the labels.
        Assert.False(WinUiScreenRules.CardsStack(TierFacts.Unknown));
    }

    [Fact]
    public void The_dialog_and_release_records_follow_the_window_tier()
    {
        Assert.Equal(WinUiTiers.Frame(WidthTier.Standard).InsetTop, WinUiReleaseNotesTiers.For(WidthTier.Standard, short_: false).PageTop);
        Assert.Equal(WinUiTiers.Frame(WidthTier.Huge).InsetTop, WinUiReleaseNotesTiers.For(WidthTier.Huge, short_: false).PageTop);
        Assert.Equal(28f, WinUiReleaseNotesTiers.For(WidthTier.Small, short_: false).TitleSize);
        Assert.Equal(40f, WinUiReleaseNotesTiers.For(WidthTier.Standard, short_: false).TitleSize);
        Assert.True(WinUiScreenDialogTiers.For(WidthTier.Compact).Touch);
        Assert.Equal(40f, WinUiSetupTiers.For(WidthTier.Compact).Target);
    }

    // ── the six options ────────────────────────────────────────────────────────────────────�

    [Fact]
    public void The_option_order_names_exactly_the_six_winui_options()
    {
        var structure = new WinUiStructure().Options.All.Select(o => "structure." + o.Id);
        var style = WinUiStyle.Create().Options.All.Select(o => "style." + o.Id);
        var declared = structure.Concat(style).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(6, declared.Length);
        Assert.Equal(declared, WinUiScreenRules.OptionOrder.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        // The board's order: Backdrop, Navigation pane, Density, Tabs, Artwork in rows, Windows accent.
        Assert.Equal("style.backdrop", WinUiScreenRules.OptionOrder[0]);
        Assert.Equal("style.useWindowsAccent", WinUiScreenRules.OptionOrder[^1]);
    }
}
