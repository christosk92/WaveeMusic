// ── Wavee.Tests/Theming/CoreSeamsP6Tests.cs — the P6 WK core seams, as pure rules and the real hosts ─────────────────────────────────
//
// Each seam is opt-in and its default is the shell as it shipped, so every block starts with the regression (Classic and Zune unchanged):
//
//   (a) TABS OFF, WORKSPACE KEPT   `ShellCapabilities.HiddenTabStrip` / `.CyclesTabs`: no strip and no island priced, Ctrl+T opens in the
//                                  background (the active tab and its page stay), Ctrl+Tab / Ctrl+Shift+Tab cycle and wrap.
//   (b) STAGE DEPTH BLUR           `LyricsLinesStyle.StageDepthBlur` through `BlurPolicy.ResolveFor(setting, onStage, styleBlur)`.
//   (c) SETTINGS GROUPING          `ThemePolicies.Settings` (`SettingsGrouping`): the default is today's twelve; a seven-tab grouping (the WinUI
//                                  board's) lands every old slug on the tab that holds its area, and draws every document section once.
//   (d) STAGE LAYOUTS PER TIER     `StageTierMetrics.Layouts` through `Stage.LayoutRules.Effective(..., allowed)`.
//   (e) PANE TAB GROUPS            `DockedPane.Tabs`: a docked pane with its own tab subset (the WinUI board's Huge: Queue + Now playing | Lyrics +
//                                  Friends), the primary's pivot leaving the whole group out, the doors picking the pane's tab.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Wavee.Themes.Zune;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(EntitiesCollection.Name)]
public sealed class CoreSeamsP6Tests
{
    static readonly ShellCapabilities Zune = ZunePolicies.Capabilities;
    static readonly ShellCapabilities Hidden = ShellCapabilities.Unrestricted with { HiddenTabStrip = true };

    // ══ (a) tabs Off with the workspace kept ═══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Classic_and_zune_keep_their_strip_and_their_chord()
    {
        Assert.False(ShellCapabilities.Unrestricted.HiddenTabStrip);
        Assert.False(ShellCapabilities.Unrestricted.CyclesTabs);
        Assert.True(ShellCapabilities.Unrestricted.ShowsTabStrip);
        Assert.False(ShellCapabilities.Unrestricted.TabCycleChord);
        Assert.False(Zune.ShowsTabStrip);
        Assert.False(Zune.TabCycleChord);
        Assert.Equal(0, Shell.FrameRules.TabCycleDelta(Keys.Tab, KeyModifiers.Ctrl, ShellCapabilities.Unrestricted));
        Assert.Equal(0, Shell.FrameRules.TabCycleDelta(Keys.Tab, KeyModifiers.Ctrl, Zune));
    }

    [Fact]
    public void A_hidden_strip_keeps_tabs_offered_and_implies_the_chord()
    {
        Assert.True(Hidden.Tabs);
        Assert.False(Hidden.ShowsTabStrip);
        Assert.True(Hidden.TabCycleChord);
        Assert.True((ShellCapabilities.Unrestricted with { CyclesTabs = true }).TabCycleChord);
        Assert.True((ShellCapabilities.Unrestricted with { CyclesTabs = true }).ShowsTabStrip);
        // Without tabs the flags mean nothing: that is the collapsed workspace (P5 ZK).
        Assert.False((Zune with { HiddenTabStrip = true, CyclesTabs = true }).TabCycleChord);
        Assert.False((Zune with { HiddenTabStrip = true }).ShowsTabStrip);
    }

    [Theory]
    [InlineData(KeyModifiers.Ctrl, 1)]
    [InlineData(KeyModifiers.Ctrl | KeyModifiers.Shift, -1)]
    [InlineData(KeyModifiers.None, 0)]
    [InlineData(KeyModifiers.Shift, 0)]
    [InlineData(KeyModifiers.Ctrl | KeyModifiers.Alt, 0)]
    public void Ctrl_tab_steps_the_workspace_only_on_its_two_chords(KeyModifiers mods, int expected)
    {
        Assert.Equal(expected, Shell.FrameRules.TabCycleDelta(Keys.Tab, mods, Hidden));
        Assert.Equal(0, Shell.FrameRules.TabCycleDelta(Keys.T, mods, Hidden));
    }

    [Theory]
    [InlineData(400f)]
    [InlineData(1440f)]
    [InlineData(2560f)]
    public void A_hidden_strip_prices_no_tab_island(float width)
    {
        var b = ChromeBudget.Default;
        var hidden = Shell.Chrome.Resolve(width, 340f, null, budget: b, tabs: Hidden.ShowsTabStrip);
        Assert.True(hidden.TabsHidden);
        Assert.False(hidden.ShowNewTab);
        Assert.Equal(Shell.Chrome.Resolve(width, 340f, null, budget: b, tabs: false), hidden);
        Assert.Equal(Shell.Chrome.Resolve(width, 340f, null, budget: b), Shell.Chrome.Resolve(width, 340f, null, budget: b, tabs: ShellCapabilities.Unrestricted.ShowsTabStrip));
    }

    static Shell.TabWorkspace Two(out int home, out int browse)
    {
        var w = new Shell.TabWorkspace();
        home = w.ActiveId;
        w.Open(new Shell.Route(Shell.RouteKind.Browse));
        browse = w.ActiveId;
        return w;
    }

    [Fact]
    public void A_background_tab_joins_the_workspace_and_the_page_on_screen_stays()
    {
        var w = Two(out int home, out int browse);
        int revision = w.PinnedRevision;

        var r = w.OpenInBackground(new Shell.Route(Shell.RouteKind.Recents));
        Assert.Equal(Shell.TabNavIntent.None, r.Intent);
        Assert.Null(r.Route);
        Assert.Equal(3, w.Count);
        Assert.Equal(browse, w.ActiveId);
        Assert.Equal(Shell.RouteKind.Browse, w.Active.Route.Kind);
        Assert.Equal(Shell.RouteKind.Recents, w.Tabs[2].Route.Kind);
        Assert.Equal(revision, w.PinnedRevision);   // a session tab: nothing to persist

        // A pinned one joins the pinned block, still behind the active tab.
        w.SetPinned(home, true);
        w.ActivateById(browse);
        var p = w.OpenInBackground(new Shell.Route(Shell.RouteKind.History), pinned: true);
        Assert.Equal(Shell.TabNavIntent.None, p.Intent);
        Assert.True(w.Tabs[1].Pinned);
        Assert.Equal(Shell.RouteKind.History, w.Tabs[1].Route.Kind);
        Assert.Equal(browse, w.ActiveId);
        Assert.Equal(w.ActiveIndex, p.ActiveIndex);
    }

    [Fact]
    public void A_collapsed_workspace_refuses_a_background_tab()
    {
        var w = Two(out _, out int browse);
        w.Collapse();
        Assert.Equal(Shell.TabNavIntent.None, w.OpenInBackground(new Shell.Route(Shell.RouteKind.Recents)).Intent);
        Assert.Single(w.Tabs);
        Assert.Equal(browse, w.ActiveId);
    }

    [Fact]
    public void Ctrl_tab_cycles_and_wraps_both_ways()
    {
        var w = Two(out int home, out int browse);
        w.OpenInBackground(new Shell.Route(Shell.RouteKind.Recents));
        int recents = w.Tabs[2].Id;

        var next = w.Cycle(+1);                                   // browse → recents
        Assert.Equal(Shell.TabNavIntent.Restore, next.Intent);
        Assert.Equal(Shell.RouteKind.Recents, next.Route!.Value.Kind);
        Assert.Equal(recents, w.ActiveId);
        w.Cycle(+1);                                              // wraps to home
        Assert.Equal(home, w.ActiveId);
        w.Cycle(-1);                                              // wraps back to recents
        Assert.Equal(recents, w.ActiveId);
        w.Cycle(-1);
        Assert.Equal(browse, w.ActiveId);
        Assert.Equal(Shell.TabNavIntent.None, w.Cycle(0).Intent);

        var one = new Shell.TabWorkspace();
        Assert.Equal(Shell.TabNavIntent.None, one.Cycle(+1).Intent);   // one tab: the chord falls through to focus
    }

    // ══ (b) the stage's depth blur ═════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(100, false, false, 100)]
    [InlineData(100, true, false, 0)]     // the stage never depth-blurs unless the style asks
    [InlineData(100, true, true, 100)]    // the style asks: the user's strength on the stage too
    [InlineData(40, true, true, 40)]
    [InlineData(0, true, true, 0)]        // the user's "off" is still off
    [InlineData(250, true, true, 100)]
    [InlineData(70, false, true, 70)]     // off the stage the style has no say
    public void The_stage_blurs_only_where_the_style_asks(int setting, bool onStage, bool styleBlur, int expected)
        => Assert.Equal(expected, Lyrics.BlurPolicy.ResolveFor(setting, onStage, styleBlur));

    [Fact]
    public void The_two_argument_resolve_is_the_style_that_does_not_ask_and_no_shipped_style_asks()
    {
        foreach (int setting in new[] { -5, 0, 40, 100, 300 })
        foreach (bool onStage in new[] { false, true })
            Assert.Equal(Lyrics.BlurPolicy.ResolveFor(setting, onStage, styleBlur: false), Lyrics.BlurPolicy.ResolveFor(setting, onStage));
        var bare = new LyricsLinesStyle((_, _) => new FluentGpu.Dsl.BoxEl(), _ => new FluentGpu.Dsl.BoxEl(), _ => default, (_, _) => new FluentGpu.Dsl.BoxEl(),
            (_, _) => new FluentGpu.Dsl.BoxEl(), (_, _) => new FluentGpu.Dsl.BoxEl());
        Assert.False(bare.StageDepthBlur);                       // a style that says nothing keeps the stage sharp
        Assert.False(ZuneListStyles.LyricsLines.StageDepthBlur);
    }

    // ══ (c) the Settings grouping ═════════════════════════════════════════════════════════════════════════════════════════

    static readonly string[] OldSlugs =
    [
        "account", "appearance", "playback", "audio", "lyrics", "devices", "storage", "notifications", "keyboard", "privacy", "feedback", "about",
        "general", "logs", "diagnostics", "help", "feedback-help", "downloads", "downloads-storage", "cache", "devices-connect", "connect", "shortcuts",
        "APPEARANCE", "nope", "", null!,
    ];

    [Fact]
    public void The_default_grouping_is_todays_twelve_areas()
    {
        var g = SettingsGrouping.Default;
        Assert.Empty(g.Validate());
        Assert.Equal(Settings.AreaSlugs.Length, g.Count);
        for (int i = 0; i < g.Count; i++)
        {
            Assert.Equal(Settings.AreaSlugs[i], g.Tabs[i].Slug);
            Assert.Equal(Settings.AreaTitleKeys[i], g.Tabs[i].TitleKey);
            Assert.Equal(i, g.GroupOf((Settings.Area)i));
            Assert.Equal((Settings.Area)i, g.HomeAreaOf(i));
            Assert.Equal(Settings.PartsOf((Settings.Area)i), g.PartsOf(i));
        }
        foreach (var slug in OldSlugs)
            Assert.Equal(Settings.AreaFromSlug(slug), g.AreaFromSlug(slug));   // every slug lands exactly where it did
    }

    [Fact]
    public void Every_shipped_bundle_keeps_the_default_grouping()
    {
        Assert.Equal(SettingsGrouping.Default, ThemePolicies.Default.Settings);
        Assert.Equal(SettingsGrouping.Default, ClassicPolicies.Bundle.Settings);
        Assert.Equal(SettingsGrouping.Default, ZunePolicies.For(ZuneParameters.Default).Settings);
        Assert.Empty(ClassicPolicies.Bundle.Validate());
    }

    /// <summary>The WinUI board's seven tabs over the twelve areas (W-Settings, W-SettingsSections): General draws the window rows the old General tab held
    /// (language, links, the notification area, graphics) above the account, devices and keyboard areas.</summary>
    static SettingsGrouping Seven()
    {
        static ImmutableArray<Settings.Area> A(params Settings.Area[] a) => [.. a];
        var general = new SettingsTab("general", "General", A(Settings.Area.Account, Settings.Area.Devices, Settings.Area.Keyboard))
        {
            Parts =
            [
                new(Settings.Tab.General, "Language & region"), new(Settings.Tab.General, "Links"), new(Settings.Tab.General, "Notification area"),
                new(Settings.Tab.General, "Graphics"), new(Settings.Tab.Account, "Account"), new(Settings.Tab.Devices, "Connect"), new(Settings.Tab.Keyboard, "Shortcuts"),
            ],
        };
        return new SettingsGrouping(
        [
            general,
            new SettingsTab("appearance", "Appearance", A(Settings.Area.Appearance, Settings.Area.Lyrics)),
            new SettingsTab("playback", "Playback", A(Settings.Area.Playback, Settings.Area.Audio)),
            new SettingsTab("notifications", "Notifications", A(Settings.Area.Notifications)),
            new SettingsTab("storage-cache", "Storage & cache", A(Settings.Area.Storage)),
            new SettingsTab("privacy-diagnostics", "Privacy & diagnostics", A(Settings.Area.Privacy, Settings.Area.Feedback)),
            new SettingsTab("about", "About", A(Settings.Area.About)),
        ]);
    }

    [Fact]
    public void Seven_tabs_draw_every_document_section_exactly_once()
    {
        var g = Seven();
        Assert.Empty(g.Validate());
        Assert.Equal(7, g.Count);
        Assert.Contains(new Settings.Part(Settings.Tab.General, "Graphics"), g.PartsOf(0));
        Assert.DoesNotContain(new Settings.Part(Settings.Tab.General, "Graphics"), g.PartsOf(1));      // moved, not drawn twice
        Assert.Contains(new Settings.Part(Settings.Tab.Appearance, "Theme"), g.PartsOf(1));
        Assert.Contains(new Settings.Part(Settings.Tab.Appearance, "Lyrics"), g.PartsOf(1));
        Assert.Equal(0, g.GroupOfPart(new Settings.Part(Settings.Tab.General, "Language & region")));
        Assert.Equal(2, g.GroupOfPart(new Settings.Part(Settings.Tab.Playback, "Sound")));
        Assert.Equal(-1, g.GroupOfPart(new Settings.Part(Settings.Tab.General, "No such section")));
    }

    [Theory]
    [InlineData("account", 0)]
    [InlineData("devices", 0)]
    [InlineData("connect", 0)]
    [InlineData("devices-connect", 0)]
    [InlineData("keyboard", 0)]
    [InlineData("shortcuts", 0)]
    [InlineData("general", 0)]               // the tab's own slug beats the old alias (which opened Appearance, where the window rows lived)
    [InlineData("appearance", 1)]
    [InlineData("lyrics", 1)]
    [InlineData("playback", 2)]
    [InlineData("audio", 2)]
    [InlineData("notifications", 3)]
    [InlineData("storage", 4)]
    [InlineData("downloads", 4)]
    [InlineData("cache", 4)]
    [InlineData("storage-cache", 4)]
    [InlineData("privacy", 5)]
    [InlineData("feedback", 5)]
    [InlineData("logs", 5)]
    [InlineData("diagnostics", 5)]
    [InlineData("help", 5)]
    [InlineData("privacy-diagnostics", 5)]
    [InlineData("about", 6)]
    [InlineData("nope", 1)]                  // a bad arg opens where it always did (Appearance), now on its tab
    public void Every_old_slug_lands_on_the_tab_that_holds_its_area(string slug, int tab)
    {
        var g = Seven();
        Assert.Equal(tab, g.GroupFromSlug(slug));
        var area = g.AreaFromSlug(slug);
        Assert.Contains(area, g.Tabs[tab].Areas);
        // An area slug still names its own area (the deep link's scroll target), whatever tab holds it.
        int i = Array.IndexOf(Settings.AreaSlugs, slug);
        if (i >= 0) Assert.Equal((Settings.Area)i, area);
    }

    [Fact]
    public void A_tab_click_opens_its_first_home_area()
    {
        var g = Seven();
        Assert.Equal(Settings.Area.Account, g.HomeAreaOf(0));
        Assert.Equal(Settings.Area.Privacy, g.HomeAreaOf(5));
        Assert.Equal(Settings.Area.About, g.HomeAreaOf(99));    // clamped
        for (int t = 0; t < g.Count; t++) Assert.Equal(t, g.GroupOf(g.HomeAreaOf(t)));
    }

    [Fact]
    public void A_grouping_that_loses_or_doubles_an_area_or_a_section_is_refused()
    {
        var seven = Seven();
        var lost = seven with { Tabs = seven.Tabs.RemoveAt(6) };                                    // About has no home
        Assert.Contains(lost.Validate(), p => p.Contains("'about'") && p.Contains("0 home"));
        var doubled = seven with { Tabs = seven.Tabs.SetItem(6, seven.Tabs[6] with { Areas = [Settings.Area.About, Settings.Area.Lyrics] }) };
        Assert.Contains(doubled.Validate(), p => p.Contains("'lyrics'") && p.Contains("2 home"));
        var slug = seven with { Tabs = seven.Tabs.SetItem(6, seven.Tabs[6] with { Slug = "General" }) };
        Assert.Contains(slug.Validate(), p => p.Contains("twice"));
        var twice = seven with { Tabs = seven.Tabs.SetItem(1, seven.Tabs[1] with { Parts = [new(Settings.Tab.General, "Graphics"), .. Settings.PartsOf(Settings.Area.Lyrics)] }) };
        Assert.Contains(twice.Validate(), p => p.Contains("drawn"));
        var invented = seven with { Tabs = seven.Tabs.SetItem(6, seven.Tabs[6] with { Parts = [.. Settings.PartsOf(Settings.Area.About), new(Settings.Tab.About, "Invented")] }) };
        Assert.Contains(invented.Validate(), p => p.Contains("not a document section"));
        Assert.Contains(new SettingsGrouping([]).Validate(), p => p.Contains("no tabs"));
        Assert.Contains((ThemePolicies.Default with { Settings = lost }).Validate(), p => p.Contains("home"));
    }

    [Fact]
    public void An_equal_grouping_republished_is_not_a_change()
    {
        Assert.Equal(Seven(), Seven());
        Assert.Equal(Seven().GetHashCode(), Seven().GetHashCode());
        Assert.NotEqual(SettingsGrouping.Default, Seven());
    }

    // ══ (d) the stage's layouts per window tier ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Every_layout_is_allowed_by_default_and_the_rule_is_unchanged()
    {
        Assert.Equal(StageLayouts.All, StageTierMetrics.Default.Layouts);
        foreach (var p in new[] { StagePolicy.Default, ClassicPolicies.Stage, ZunePolicies.For(ZuneParameters.Default).Stage })
        foreach (var tier in Enum.GetValues<WidthTier>())
            Assert.Equal(StageLayouts.All, p.MetricsFor(tier).Layouts);
        foreach (var stored in Enum.GetValues<Stage.VizLayout>())
        foreach (var aspect in Enum.GetValues<Stage.Aspect>())
        foreach (var hero in Enum.GetValues<Stage.HeroState>())
            Assert.Equal(Stage.LayoutRules.Effective(stored, aspect, hero), Stage.LayoutRules.Effective(stored, aspect, hero, StageLayouts.All));
    }

    [Theory]
    [InlineData(StageLayouts.Card, Stage.VizLayout.LargeArt, Stage.VizLayout.Card)]            // W-Tiers Compact: Card only
    [InlineData(StageLayouts.Card, Stage.VizLayout.Artist, Stage.VizLayout.Card)]
    [InlineData(StageLayouts.Card | StageLayouts.LargeArt, Stage.VizLayout.Centered, Stage.VizLayout.LargeArt)]   // W-Tiers Small
    [InlineData(StageLayouts.Card | StageLayouts.LargeArt, Stage.VizLayout.Artist, Stage.VizLayout.LargeArt)]
    [InlineData(StageLayouts.Card | StageLayouts.LargeArt, Stage.VizLayout.LargeArt, Stage.VizLayout.LargeArt)]
    [InlineData(StageLayouts.Card | StageLayouts.LargeArt, Stage.VizLayout.Card, Stage.VizLayout.Card)]
    [InlineData(StageLayouts.Card | StageLayouts.Centered, Stage.VizLayout.Artist, Stage.VizLayout.Card)]
    [InlineData(StageLayouts.Card | StageLayouts.Centered, Stage.VizLayout.Centered, Stage.VizLayout.Centered)]
    [InlineData((StageLayouts)0, Stage.VizLayout.Card, Stage.VizLayout.Card)]                  // Card is every layout's floor
    public void A_layout_the_tier_does_not_allow_draws_its_nearest_allowed_one(StageLayouts allowed, Stage.VizLayout stored, Stage.VizLayout expected)
        => Assert.Equal(expected, Stage.LayoutRules.Effective(stored, Stage.Aspect.Desktop, Stage.HeroState.Header, allowed));

    [Fact]
    public void The_compact_class_and_a_missing_hero_still_decide_first()
    {
        Assert.Equal(Stage.VizLayout.Card, Stage.LayoutRules.Effective(Stage.VizLayout.Centered, Stage.Aspect.Compact, Stage.HeroState.Header, StageLayouts.All));
        // Artist without a header is Large art, which a Card-and-Centered tier steps down to Card.
        Assert.Equal(Stage.VizLayout.Card,
            Stage.LayoutRules.Effective(Stage.VizLayout.Artist, Stage.Aspect.Desktop, Stage.HeroState.None, StageLayouts.Card | StageLayouts.Centered | StageLayouts.Artist));
        Assert.True(Stage.LayoutRules.Allows((StageLayouts)0, Stage.VizLayout.Card));
        Assert.False(Stage.LayoutRules.Allows(StageLayouts.Card, Stage.VizLayout.Artist));
    }

    [Fact]
    public void The_resolved_layout_carries_its_tiers_allowed_layouts_and_a_tier_without_card_is_refused()
    {
        var small = StageTierMetrics.Default with { Layouts = StageLayouts.Card | StageLayouts.LargeArt };
        Assert.Equal(StageLayouts.Card | StageLayouts.LargeArt, Stage.Layout.Resolve(1000f, 700f, null, null, small).AllowedLayouts);
        Assert.Equal(StageLayouts.All, Stage.Layout.Resolve(1440f, 960f, null).AllowedLayouts);
        Assert.Contains((StageTierMetrics.Default with { Layouts = StageLayouts.LargeArt }).Validate("Small"), p => p.Contains("Card"));
        Assert.Empty(small.Validate("Small"));
        Assert.Contains((StagePolicy.Default with { Small = StageTierMetrics.Default with { Layouts = StageLayouts.Centered } }).Validate(), p => p.Contains("Card"));
    }

    // ══ (e) a docked pane's own tab group ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The WinUI board's Huge (W-Shell-Huge): the docked pane is Queue + Now playing, the primary Lyrics + Friends.</summary>
    static readonly PanelArrangement TwoGroups = new(new DockedPane(PanelMode.Queue) { Tabs = default(PanelModeSet).With(PanelMode.NowPlaying) }, Linked: true);

    [Fact]
    public void A_pane_without_tabs_is_the_one_body_it_always_was()
    {
        var queue = new DockedPane(PanelMode.Queue);
        Assert.False(queue.HasTabs);
        Assert.Equal(default(PanelModeSet).With(PanelMode.Queue), queue.Bodies);
        Assert.Equal(PanelMode.Queue, queue.BodyFor(-1));
        Assert.Equal(PanelMode.Queue, queue.BodyFor((int)PanelMode.NowPlaying));   // a body it does not offer
        Assert.False(new DockedPane(PanelMode.Queue) { Tabs = default(PanelModeSet).With(PanelMode.Queue) }.HasTabs);
        foreach (var panes in new[] { PanelArrangement.QueueBeside, ZunePanelTiers.HugePanes, ZunePanelTiers.Huge4KPanes })
        {
            Assert.False(panes.Second is { HasTabs: true });
            Assert.False(panes.Third is { HasTabs: true });
        }
    }

    [Fact]
    public void A_tab_group_is_held_whole_so_the_primarys_pivot_leaves_it_out()
    {
        Assert.True(TwoGroups.Second!.Value.HasTabs);
        Assert.True(TwoGroups.Holds(PanelMode.Queue));
        Assert.True(TwoGroups.Holds(PanelMode.NowPlaying));
        Assert.False(TwoGroups.Holds(PanelMode.Lyrics));
        Assert.True(TwoGroups.Modes.Contains(PanelMode.NowPlaying));
        Assert.False(TwoGroups.Modes.Contains(PanelMode.Friends));
        Assert.Equal(0, TwoGroups.IndexHolding(PanelMode.NowPlaying));
        Assert.Equal(-1, TwoGroups.IndexHolding(PanelMode.Friends));
        Assert.Equal(PanelMode.Lyrics, TwoGroups.Presented(PanelMode.NowPlaying));   // the primary steps aside to its fallback
        Assert.Equal(PanelMode.Friends, TwoGroups.Presented(PanelMode.Friends));
    }

    [Fact]
    public void The_policy_refuses_a_tab_group_the_host_cannot_keep()
    {
        PanelPolicy With(PanelArrangement panes) => ClassicPolicies.Panel with { Tiers = [new PanelTierRule(PanelPlacement.Dock, MaxDocked: 1 + Math.Max(1, panes.Count)) { Panes = panes }] };
        Assert.Empty(With(TwoGroups).Validate());
        var withNowPlaying = default(PanelModeSet).With(PanelMode.NowPlaying);
        Assert.Contains(With(new PanelArrangement(new DockedPane(PanelMode.Queue) { Tabs = withNowPlaying })).Validate(), p => p.Contains("independent"));
        Assert.Contains(With(new PanelArrangement(new DockedPane(PanelMode.Queue) { Tabs = withNowPlaying }, new DockedPane(PanelMode.NowPlaying), Linked: true)).Validate(),
            p => p.Contains("twice"));
        Assert.Contains(With(new PanelArrangement(new DockedPane(PanelMode.Queue) { Tabs = default(PanelModeSet).With(PanelMode.Video) }, Linked: true)).Validate(),
            p => p.Contains("video"));
        Assert.Contains(With(new PanelArrangement(new DockedPane(PanelMode.Queue) { Tabs = default(PanelModeSet).With(PanelMode.Lyrics) }, Linked: true)).Validate(),
            p => p.Contains("fallback"));
    }

    static (Shell.PanelHost Host, Shell.PanelCells Cells) Host(PanelArrangement panes)
    {
        var cells = Shell.PanelCells.Fresh();
        cells.SecondAllowed.Value = true;
        cells.Panes.Value = panes;
        return (new Shell.PanelHost(cells, new Shell.PanelSizes(new DictionaryPanelSizeStore()), new RecordingPanelEnv()), cells);
    }

    [Fact]
    public void The_pane_shows_its_first_tab_and_switches_within_its_group()
    {
        var (host, _) = Host(TwoGroups);
        var pane = TwoGroups.Second!.Value;
        Assert.Equal(PanelMode.Queue, host.PaneBodyNow(0, pane));
        host.ShowPaneTab(0, PanelMode.NowPlaying);
        Assert.Equal(PanelMode.NowPlaying, host.PaneBodyNow(0, pane));
        host.ShowPaneTab(0, PanelMode.Lyrics);                     // not in its group: ignored
        Assert.Equal(PanelMode.NowPlaying, host.PaneBodyNow(0, pane));
        host.ShowPaneTab(1, PanelMode.Queue);                      // no third pane: ignored
        host.ShowPaneTab(0, PanelMode.Queue);
        Assert.Equal(PanelMode.Queue, host.PaneBodyNow(0, pane));
    }

    [Fact]
    public void A_door_whose_body_sits_behind_the_panes_other_tab_picks_that_tab()
    {
        var (host, cells) = Host(TwoGroups);
        var pane = TwoGroups.Second!.Value;
        host.Toggle(Shell.RailMode.NowPlaying);                    // closed: the door opens the panel on the pane's Now playing tab
        Assert.True(cells.Open.Peek());
        Assert.Equal(PanelMode.NowPlaying, host.PaneBodyNow(0, pane));
        Assert.True(host.PaneOpenNow(0));
        host.Toggle(Shell.RailMode.Queue);                         // open, on the other tab: picks the queue, stays open
        Assert.True(cells.Open.Peek());
        Assert.Equal(PanelMode.Queue, host.PaneBodyNow(0, pane));
        host.Toggle(Shell.RailMode.Queue);                         // the body on screen: closes the panel, as a linked door always did
        Assert.False(cells.Open.Peek());
        host.Toggle(Shell.RailMode.Friends);                       // a body of the primary's group opens in the primary
        Assert.True(cells.Open.Peek());
        Assert.Equal(Shell.RailMode.Friends, cells.Mode.Peek());
    }
}
