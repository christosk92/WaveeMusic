// ── Wavee.ShellTests/LibraryMountedTests.cs — the library page and its panes, mounted (themes plan 6.1, T10c1) ──────────────────────────────────
//
// The library page, the album pane and the compact show pane are the structure's since T10c1; the core hosts keep the data, the shape memo, the selection, the
// search matcher, the collapse state and the pane models (`Wavee.Tests` pins the pure ones: `LibraryContractTests`, `LibraryLayoutBreakpointTests`,
// `LibraryWordRailTests`, `AlbumPaneReadinessTests`). What only a mounted tree can say:
//
//   registered   Classic is the registered template of every key
//   page         the wide arm stands the navigator column, the grip and the pane column keyed as the old tree was; a narrow window writes the collapse signal through
//                the structure's ladder; the collapsed arm stands the crumb bar over a depth-keyed body
//   album pane   the header skeleton is the stated header band, and its arm is keyed apart from the body so the swap is a crossfade
//   show pane    the arms are keyed by the ARM (never the show)

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class LibraryMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic(float width = 1180f) => new() { Composition = ClassicTheme.Create(), WidthDip = width };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_library_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.LibraryPage.Id, "classic.library.page"),
                (ViewKeys.LibraryAlbumPane.Id, "classic.library.album-pane"),
                (ViewKeys.LibraryShowPane.Id, "classic.library.show-pane"),
                (ViewKeys.LibraryArtistReader.Id, "classic.library.artist-reader"),
                (ViewKeys.LibrarySavedEpisodes.Id, "classic.library.saved-episodes"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the page ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class PageState
    {
        public bool Collapsed;
        public int Writes;
    }

    sealed class Cells
    {
        public readonly Signal<float> LeftW = new(340f), MidW = new(440f);
        public readonly Signal<int> Sort = new(0), View = new(1), Size = new(1), PodcastsMode = new(0);
        public readonly Signal<bool> Desc = new(false);
        public readonly Signal<string> Filter = new("");
    }

    static LibraryPageProps PageProps(PageState state, bool collapsed = false, int depth = 0, bool selected = false)
    {
        var cells = new Cells();
        return new()
        {
            Title = "Albums", Entity = EntityKind.Album, IsPodcasts = false,
            Collapsed = collapsed, SavedEpisodesMode = false, FullSearch = false, Shimmer = false, Awaiting = false, RailOpen = false,
            HasSelection = selected, Depth = depth, Crumbs = depth == 0 ? new[] { "Albums" } : new[] { "Albums", "Abbey Road" },
            IsCollapsed = () => state.Collapsed,
            SetCollapsed = c => { state.Collapsed = c; state.Writes++; },
            PickDepth = static _ => { },
            LeftW = () => cells.LeftW, MidW = () => cells.MidW,
            Sort = () => cells.Sort, Desc = () => cells.Desc, View = () => cells.View, Size = () => cells.Size,
            Filter = () => cells.Filter, PodcastsMode = () => cells.PodcastsMode,
            CommitLeft = static () => { }, CommitMid = static () => { },
            CountText = Prop.Of(static () => "0"),
            Pane = static () => new BoxEl { Key = "test:pane" },
            SavedEpisodes = static () => new BoxEl { Key = "test:saved" },
            SkelGroup = new object(),
            Navigator = new LibraryNavigatorProps
            {
                // Pending: the region shows the shimmer rows and never asks for the mount.
                Load = Loadable<int>.Pending(0),
                Items = BoundItems.Project(new Signal<int>(0), static n => n, static (_, _) => default(LibraryNavItem), default(LibraryNavItem)),
                Mount = static () => throw new InvalidOperationException("the navigator is pending: the mount is never asked for"),
                OnMounted = static _ => { },
                StickyLetter = new Signal<int>(-1), StickyPush = new Signal<float>(0f), Present = new Signal<uint>(0u),
                Jump = static _ => { },
            },
        };
    }

    [Fact]
    public void The_wide_page_stands_the_navigator_column_the_grip_and_the_pane_column_keyed_as_before()
    {
        var state = new PageState();
        ViewHarness.With(host, ViewKeys.LibraryPage, PageProps(state), harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("lib:wide"), "the wide arm's root is keyed");
            Assert.True(harness.Contains("lib:nav"), "the navigator column");
            Assert.True(harness.Contains("lib:grip"), "the seam");
            Assert.True(harness.Contains("lib:toolbar"), "the toolbar with its title row");
            Assert.True(harness.Contains("lib:empty"), "no selection: the placeholder column");
            Assert.False(harness.Contains("lib:collapsed"));
            // the navigator column is as wide as its persisted signal
            Assert.Equal(340f, harness.LocalBounds(harness.Node("lib:nav")).W, 0.5f);
        }, UnderClassic());
    }

    [Fact]
    public void A_selection_stands_the_pane_in_the_detail_column()
    {
        var state = new PageState();
        ViewHarness.With(host, ViewKeys.LibraryPage, PageProps(state, selected: true), harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("lib:detail"));
            Assert.True(harness.Contains("test:pane"));
            Assert.False(harness.Contains("lib:empty"));
        }, UnderClassic());
    }

    [Fact]
    public void A_narrow_window_writes_the_collapse_signal_through_the_structures_ladder()
    {
        var state = new PageState();
        ViewHarness.With(host, ViewKeys.LibraryPage, PageProps(state), harness =>
        {
            harness.RunFrames(30);
            Assert.True(state.Collapsed, "500 DIP is under the 640 rung");
            Assert.True(state.Writes >= 1);
        }, UnderClassic(width: 500f));
    }

    [Fact]
    public void The_collapsed_arm_stands_the_crumb_bar_over_a_depth_keyed_body()
    {
        var state = new PageState { Collapsed = true };
        ViewHarness.With(host, ViewKeys.LibraryPage, PageProps(state, collapsed: true, depth: 0), harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("lib:collapsed"));
            Assert.True(harness.Contains("lib:crumbs"));
            Assert.True(harness.Contains("depth:0"));
            Assert.True(harness.Contains("col:nav"));
            Assert.True(harness.Contains("lib:toolbar:compact"), "the collapsed toolbar drops the title row");
            Assert.False(harness.Contains("lib:wide"));
        }, UnderClassic(width: 500f));
    }

    [Fact]
    public void The_collapsed_drill_shows_the_pane_at_depth_one()
    {
        var state = new PageState { Collapsed = true };
        ViewHarness.With(host, ViewKeys.LibraryPage, PageProps(state, collapsed: true, depth: 1, selected: true), harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("depth:1"));
            Assert.True(harness.Contains("col:detail"));
            Assert.True(harness.Contains("test:pane"));
        }, UnderClassic(width: 500f));
    }

    // ── the album pane ───────────────────────────────────────────────────────────────────────────────────────────────────

    static LibraryAlbumPaneProps AlbumProps(AlbumPaneState state) => new()
    {
        State = state, BodyKey = state == AlbumPaneState.Header ? "paneskel:7" : "pane:7",
        Open = static () => { }, Play = static () => { }, Shuffle = static () => { }, More = static () => null, Retry = static () => { },
        Region = Loadable<int>.Pending(), ShimRows = 3, RowHeight = 48f,
        Table = static _ => new BoxEl(), AlsoBy = static () => null,
        Eyebrow = "ALBUM", Title = "Abbey Road", Meta = "17 songs", Uri = "spotify:album:test", SaveKey = "save:spotify:album:test",
        Artists = null, Notice = new BoxEl(),
    };

    [Fact]
    public void The_header_skeleton_is_the_stated_header_band_and_keyed_apart_from_the_body()
        => ViewHarness.With(host, ViewKeys.LibraryAlbumPane, AlbumProps(AlbumPaneState.Header), harness =>
        {
            harness.RunFrames(60);   // the arm's entrance (a 6 DIP rise) has run out
            Assert.True(harness.Contains("paneskel:7"));
            Assert.False(harness.Contains("pane:7"));
            Assert.Equal(ClassicLibraryPaneRules.PaneHeaderHeight, harness.LocalBounds(harness.Node("paneskel:7")).H, 0.5f);
        }, UnderClassic());

    [Fact]
    public void A_pane_with_rows_in_flight_keys_its_body_by_the_album()
        => ViewHarness.With(host, ViewKeys.LibraryAlbumPane, AlbumProps(AlbumPaneState.Rows), harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("pane:7"));
            Assert.False(harness.Contains("paneskel:7"));
        }, UnderClassic());

    // ── the show pane ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_show_panes_arms_are_keyed_by_the_arm_never_the_show()
    {
        var skeleton = new LibraryShowPaneProps
        {
            HeaderReady = false, Open = static () => { }, HeartShown = Prop.Of(static () => false), More = static () => null,
        };
        ViewHarness.With(host, ViewKeys.LibraryShowPane, skeleton, harness =>
        {
            harness.RunFrames(60);
            Assert.True(harness.Contains("showpane:skel"));
            Assert.False(harness.Contains("showpane:body"));
            Assert.Equal(ClassicLibraryPaneRules.PaneHeaderHeight, harness.LocalBounds(harness.Node("showpane:skel")).H, 0.5f);
        }, UnderClassic());
    }

    // ── Your Episodes ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ready_hands_the_body_to_the_content()
    {
        var props = new LibrarySavedEpisodesProps
        {
            Pending = false, Failed = false,
            Content = static () => new BoxEl { Key = "test:saved-table" },
            OnFailed = static () => new BoxEl { Key = "test:saved-failed" },
        };
        ViewHarness.With(host, ViewKeys.LibrarySavedEpisodes, props, harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("test:saved-table"));
            Assert.False(harness.Contains("test:saved-failed"));
        }, UnderClassic());
    }
}
