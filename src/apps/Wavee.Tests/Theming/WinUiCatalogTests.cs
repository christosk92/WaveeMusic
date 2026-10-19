// ── Wavee.Tests/Theming/WinUiCatalogTests.cs — the WinUI composition: manifest, resolution, options, capabilities, tiers (P6 W0) ──────
//
// WinUI is Developer-only: it validates and installs next to Classic and Zune, resolves only with Developer mode on (Classic otherwise, the
// stored id kept). Its structure declares the three structure options of W-ThemeOptions with their apply modes and no hidden parameter; its
// capabilities follow `tabs`; its window ladder is NavigationView's 641 / 1008 plus 1920, held 24 on narrowing, with no Huge 4K edge; the
// nav pane's modes follow `navigationPane` and `artworkInRows` is the list policy's artwork. `Themes` is process state, so the class is in the
// platform collection and resets it around every fact.

using Wavee.Themes.Classic;
using Wavee.Themes.WinUi;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class WinUiCatalogTests : IDisposable
{
    readonly MemoryAppSettings _settings = new();

    public WinUiCatalogTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    static OptionValues Structure(WinUiNavigationPane pane = WinUiNavigationPane.Automatic, bool tabs = true, bool artwork = true)
        => OptionValues.Empty
            .With(WinUiStructure.NavigationPane.Id, pane)
            .With(WinUiStructure.Tabs.Id, tabs)
            .With(WinUiStructure.ArtworkInRows.Id, artwork);

    // ── the manifest and resolution ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_manifest_is_winui_structure_by_winui_style_and_developer_only()
    {
        var m = WinUiTheme.Create().Manifest;
        Assert.Equal("winui", m.Id);
        Assert.Equal("WinUi.Structure", m.StructureId);
        Assert.Equal("WinUi.Style", m.StyleId);
        Assert.Equal(ThemeAvailability.Developer, m.Availability);
        Assert.Equal("theme.winui.name", m.DisplayNameKey);
        Assert.Equal("theme.winui.description", m.DescriptionKey);
        Assert.Null(m.InheritsFrom);
    }

    [Fact]
    public void WinUi_resolves_only_with_developer_mode_and_a_stored_winui_is_kept()
    {
        Platform.Developer.Enabled.Value = false;
        _settings.Set(new SettingKey<string>("theme.id", ClassicTheme.Id), WinUiTheme.Id);
        ThemeHost.Install(ClassicTheme.Create(), ZuneTheme.Create(), WinUiTheme.Create());
        Assert.Equal(ClassicTheme.Id, ThemeHost.Current.Peek().Manifest.Id);
        Assert.Equal(WinUiTheme.Id, _settings.Get(new SettingKey<string>("theme.id", "")));
        Assert.Equal(ClassicTheme.Id, ThemeCatalog.Resolve("winui", developerMode: false).Manifest.Id);
        Assert.Equal(WinUiTheme.Id, ThemeCatalog.Resolve("winui", developerMode: true).Manifest.Id);
        Assert.Equal(new[] { ClassicTheme.Id, ZuneTheme.Id, WinUiTheme.Id }, ThemeCatalog.Installed.Select(x => x.Id));
    }

    // ── the options ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_structure_options_are_navigation_pane_tabs_and_artwork_in_rows_with_their_apply_modes()
    {
        var theme = WinUiTheme.Create();
        var structure = theme.AllOptions.Where(lo => lo.Layer == OptionLayer.Structure).ToList();
        Assert.Equal(new[] { "navigationPane", "tabs", "artworkInRows" }, structure.Select(lo => lo.Option.Id));
        Assert.All(structure, lo => Assert.True(theme.IsExposed(lo), lo.QualifiedId + " is hidden: WinUI has no structure parameter"));
        Assert.Equal(new[] { WinUiStructure.NavigationPaneQualifiedId, WinUiStructure.TabsQualifiedId, WinUiStructure.ArtworkInRowsQualifiedId },
            structure.Select(lo => lo.QualifiedId));

        Assert.Equal(ApplyMode.Live, WinUiStructure.NavigationPane.Apply);
        Assert.Equal(ApplyMode.Live, WinUiStructure.Tabs.Apply);
        Assert.Equal(ApplyMode.Transaction, WinUiStructure.ArtworkInRows.Apply);
        Assert.Equal(WinUiNavigationPane.Automatic, WinUiStructure.NavigationPane.Default);
        Assert.True(WinUiStructure.Tabs.Default);
        Assert.True(WinUiStructure.ArtworkInRows.Default);
        Assert.Equal(new WinUiParameters(WinUiNavigationPane.Automatic, Tabs: true, ArtworkInRows: true), WinUiParameters.Default);
    }

    // ── capabilities and policies ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_capabilities_follow_tabs_and_offer_the_nav_tree_forward_history_and_friends_without_band_pins()
    {
        var structure = new WinUiStructure();
        var on = structure.Capabilities(Structure(tabs: true));
        var off = structure.Capabilities(Structure(tabs: false));
        // Tabs off hides the strip and keeps the workspace (owner Q1, WK (a)); Ctrl+Tab cycles either way.
        Assert.True(on.ShowsTabStrip);
        Assert.True(off.Tabs);
        Assert.True(off.HiddenTabStrip);
        Assert.False(off.ShowsTabStrip);
        Assert.True(on.CyclesTabs && off.CyclesTabs);
        foreach (var c in new[] { on, off })
        {
            Assert.True(c.NavTree);
            Assert.True(c.ForwardButton);
            Assert.True(c.HistoryMenu);
            Assert.True(c.FriendsPanel);
            Assert.Equal(0, c.MaxBandPins);
        }
        Assert.Same(on, structure.Capabilities(Structure(tabs: true)));
    }

    [Fact]
    public void The_policies_carry_winuis_tiers_the_280_pane_and_the_artwork_option()
    {
        var structure = new WinUiStructure();
        var automatic = structure.Policies(Structure(), StyleMetrics.Unset);
        Assert.Same(WinUiTiers.Policy, automatic.Tiers);
        Assert.Same(automatic, structure.Policies(Structure(), StyleMetrics.Unset));
        Assert.Empty(automatic.NavPane.Validate());
        Assert.Equal(280f, automatic.NavPane.DefaultWidth);
        Assert.Equal(48f, automatic.NavPane.RailWidth);
        Assert.False(automatic.NavPane.Resizable);
        Assert.Equal(NavPaneMode.Overlay, automatic.NavPane.ModeFor(WidthTier.Compact));
        Assert.Equal(NavPaneMode.Compact, automatic.NavPane.ModeFor(WidthTier.Small));
        Assert.Equal(NavPaneMode.Expanded, automatic.NavPane.ModeFor(WidthTier.Standard));
        Assert.True(automatic.Lists.TrackArtwork);

        var expanded = structure.Policies(Structure(pane: WinUiNavigationPane.Expanded), StyleMetrics.Unset);
        Assert.Equal(NavPaneMode.Expanded, expanded.NavPane.ModeFor(WidthTier.Small));
        Assert.Equal(NavPaneMode.Overlay, expanded.NavPane.ModeFor(WidthTier.Compact));

        Assert.False(structure.Policies(Structure(artwork: false), StyleMetrics.Unset).Lists.TrackArtwork);
    }

    // ── the tiers ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(300f, WidthTier.Compact)]
    [InlineData(640f, WidthTier.Compact)]
    [InlineData(641f, WidthTier.Small)]
    [InlineData(1007f, WidthTier.Small)]
    [InlineData(1008f, WidthTier.Standard)]
    [InlineData(1919f, WidthTier.Standard)]
    [InlineData(1920f, WidthTier.Huge)]
    [InlineData(3840f, WidthTier.Huge)]
    public void A_fresh_window_resolves_on_navigation_views_edges_with_no_huge_4k(float width, WidthTier expected)
        => Assert.Equal(expected, WindowTiers.Resolve(WinUiTiers.Policy, width, 960f, width).Window);

    [Theory]
    [InlineData(617f, 800f, WidthTier.Small)]
    [InlineData(616f, 800f, WidthTier.Compact)]
    [InlineData(984f, 1440f, WidthTier.Standard)]
    [InlineData(983f, 1440f, WidthTier.Small)]
    [InlineData(1896f, 2560f, WidthTier.Huge)]
    [InlineData(1895f, 2560f, WidthTier.Standard)]
    public void A_narrowing_window_holds_every_edge_24(float width, float from, WidthTier expected)
    {
        var previous = WindowTiers.Resolve(WinUiTiers.Policy, from, 960f, from);
        Assert.Equal(expected, WindowTiers.Resolve(WinUiTiers.Policy, width, 960f, width, previous: previous).Window);
    }

    [Theory]
    [InlineData(719f, HeightTier.Short)]
    [InlineData(720f, HeightTier.Regular)]
    public void The_window_is_short_below_720(float height, HeightTier expected)
        => Assert.Equal(expected, WindowTiers.Resolve(WinUiTiers.Policy, 1440f, height, 1440f).Height);

    [Fact]
    public void The_frame_records_hold_the_insets_the_title_rows_and_the_bar_per_tier()
    {
        Assert.Equal(new[] { 16f, 24f, 36f, 48f },
            new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge }.Select(t => WinUiTiers.Frame(t).InsetX));
        Assert.Equal(new[] { 16f, 20f, 24f, 32f },
            new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge }.Select(t => WinUiTiers.Frame(t).InsetTop));
        Assert.Equal(64f, WinUiTiers.Frame(WidthTier.Compact).BarH);
        Assert.Equal(72f, WinUiTiers.Frame(WidthTier.Standard).BarH);
        Assert.Same(WinUiTiers.Frame(WidthTier.Huge), WinUiTiers.Frame(WidthTier.Huge4K));
        Assert.Equal(88f, WinUiTiers.Frame(WidthTier.Standard).TitleBarH(tabs: true));
        Assert.Equal(48f, WinUiTiers.Frame(WidthTier.Standard).TitleBarH(tabs: false));
        foreach (var t in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            Assert.Equal(t, WinUiTiers.Frame(t).Tier);
            Assert.Empty(TierClip.Validate(WinUiTiers.Frame(t)));
        }
        // The Short floor is what is left at the 330 minimum under both title rows and the 72 bar.
        Assert.Equal(TierPolicy.WindowMinHeight - 40f - 48f - 72f, WinUiTiers.Policy.ShortFloor);
    }
}
