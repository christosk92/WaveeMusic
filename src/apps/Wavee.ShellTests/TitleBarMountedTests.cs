// ── Wavee.ShellTests/TitleBarMountedTests.cs — the Classic title bar and search, mounted (themes plan 6.1, T4b) ──────────────
//
// The merged chrome row is the structure's since T4b: the core allocates it (`Shell.Chrome`, pinned by `Wavee.Tests`) and hands the
// structure's `Shell.TitleBar` one frozen props record; the omnibar is `Shell.Search` and its popup body `Shell.SearchSuggestions`.
// What only a mounted tree can say:
//
//   the registered views   Classic registers all three keys; the real shell mounted on the installed theme is drawn by them (the tabs
//                          lane and the search field stand in the title row), and narrowing the window swaps the field for the
//                          magnifier and widening brings it back (the form decision is the allocator's; the picture follows it)
//   the row's states       `ViewHarness` mounts the title bar view alone under the Classic composition with fixture props: each
//                          identity chip form stands under its key, the pin keeps its width when the destination cannot be pinned
//                          (the tree stays honest to the budget), and the search is a field or a magnifier by the allocation
//   the popup body         `ViewHarness` mounts the suggestions view alone: the progress slot is always the first child, a pending
//                          body reserves the height the core's rule gives it, and rows fill the body
//
// The drag regions and the caption buttons are the engine control's (`TitleBarSuite`); nothing here replaces them.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class TitleBarMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    // ── the registered views ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_title_bar_search_and_suggestions()
        => host.Run(() =>
        {
            Assert.Equal("classic.shell.title-bar", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellTitleBar.Id));
            Assert.Equal("classic.shell.search", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellSearch.Id));
            Assert.Equal("classic.shell.search-suggestions", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellSearchSuggestions.Id));
        });

    [Fact]
    public void The_mounted_shell_draws_the_title_bar_row_from_the_classic_templates()
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
            var scene = shell.Host.Scene;
            var lane = scene.AbsoluteRect(Node(shell, "chrome-tabs-lane"));
            var field = scene.AbsoluteRect(Node(shell, "chrome-search-field"));

            // Both stand inside the 48-DIP title row; the lane takes at most its reserved width; the field is a width the row can
            // seat (the allocator's floor and cap).
            Assert.True(lane.Y >= 0f && lane.Y + lane.H <= TitleBar.ExpandedHeight + 0.5f, "the tabs lane stands in the title row");
            Assert.True(lane.W > 0f && lane.W <= Shell.ChromeLayout.Peek().TabIslandWidth + 0.5f, "the lane holds its reservation");
            Assert.True(field.Y >= 0f && field.Y + field.H <= TitleBar.ExpandedHeight + 0.5f, "the search field stands in the title row");
            Assert.InRange(field.W, Shell.Layout.ChromeSearchMinW - 0.5f, Shell.Layout.ChromeSearchMaxW + 0.5f);
            Assert.True(field.X > lane.X + lane.W - 0.5f, "the field stands to the right of the tabs island");
        });

    [Fact]
    public void Narrowing_the_window_swaps_the_field_for_the_magnifier_and_widening_brings_it_back()
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
            Assert.False(shell.Find("chrome-search-field").IsNull);

            Resized(shell, 700f);
            Until(shell, () => shell.Find("chrome-search-field").IsNull, "the field left for the magnifier");
            Assert.Equal(Shell.MergedSearchMode.Icon, Shell.ChromeLayout.Peek().SearchMode);

            Resized(shell, 1180f);
            Until(shell, () => !shell.Find("chrome-search-field").IsNull, "the field came back");
            Assert.Equal(Shell.MergedSearchMode.Field, Shell.ChromeLayout.Peek().SearchMode);
        });

    /// <summary>WM_SIZE: the window changes size and one synchronous RESIZED paint follows, then frames to rest.</summary>
    static void Resized(Shell.HeadlessShell shell, float widthDip)
    {
        shell.Resize(widthDip, 760f);
        shell.Host.Paint(0, keepAlive: true);
        shell.Host.RunFrame();
        shell.RunFrames(2);
    }

    // ── the row's states (the title bar view alone) ──────────────────────────────────────────────────────────────────────────

    static readonly Signal<int> s_selection = new(0);

    static ChromeRowState Row(ChromeChipForm chip = ChromeChipForm.Profile, bool actions = false, SearchForm search = SearchForm.Field)
        => new(ShowName: false, ShowActions: actions, ShowForward: true, ShowBack: true, ShowNewTab: true, ShowTrailing: true,
            Chip: chip, Search: search, SearchWidth: search == SearchForm.Field ? 330f : 44f, TabIslandWidth: 300f);

    static OmnibarModel FixtureModel() => new(new OmnibarPorts(
        Source: static () => null,
        FromHistory: static _ => Shell.Omnibar.Suggestions.Empty,
        SetSearchText: static _ => { },
        RequestFocus: static () => { },
        Go: static (_, _) => { },
        SearchRoute: static _ => Shell.Route.None,
        PlayContext: static () => null));

    static SearchSuggestionSource Source(SearchAnswerState state, params string[] queries) => new()
    {
        Answer = () => new SearchAnswer(state, queries, []),
        Text = new Signal<string>("lo"),
        Highlight = new Signal<int>(-1),
        Retry = static () => { },
        PendingBodyHeight = static (_, _, _) => 120f,
        PendingSkeletonRows = static (_, _) => (2, 1),
        QueryRowHeight = 44f,
        RichRowHeight = 62f,
        PopupBodyMaxHeight = 560f,
    };

    static ShellTitleBarProps TitleBarProps(ChromeRowState row, TitleBarPin? pin = null)
    {
        var text = new Signal<string>("");
        return new ShellTitleBarProps
        {
            Layout = () => row,
            PeekLayout = () => row,
            TabsVersion = static () => 0,
            ContentVersion = static () => 0,
            TabItemsVersion = static () => 0,
            OnPaneToggle = static () => { },
            PaneToggleEnabled = new Signal<bool>(true),
            PaneToggleTip = static () => "",
            TabItems = static () => [new TabViewItem { Key = "tab#1", Header = "Home", IsClosable = false }],
            TabSelection = static () => s_selection,
            OnActivateTab = static _ => { },
            OnCloseTab = static _ => { },
            OnNewTab = static () => { },
            OnTabExtent = static _ => { },
            CanBack = static () => false,
            CanForward = static () => false,
            GoBack = static () => { },
            GoForward = static () => { },
            HistoryMenu = static _ => [],
            ProfileChip = static () => new BoxEl { Width = 32f, Height = 32f },
            SignIn = static () => { },
            ToggleFriends = static () => { },
            BellUnread = static () => 0,
            ToggleNotifications = static (_, _, _) => { },
            Pin = () => pin,
            ToggleTheme = static () => { },
            Search = new ShellSearchProps
            {
                Model = FixtureModel(),
                TextCell = () => text,
                FocusTicket = static () => 0,
                FieldWidth = static measured => Math.Clamp(measured, 280f, 420f),
                Suggestions = Source(SearchAnswerState.Idle),
            },
        };
    }

    [Fact]
    public void The_connecting_chip_is_a_caption_box_and_the_profile_chip_the_builders_element()
    {
        ViewHarness.With(host, ViewKeys.ShellTitleBar, TitleBarProps(Row(ChromeChipForm.Connecting)), harness =>
        {
            var chip = harness.AbsoluteBounds(harness.Node("chrome-connecting"));
            Assert.Equal(32f, chip.H, 0.5f);
            Assert.False(harness.Contains("chrome-profile"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.ShellTitleBar, TitleBarProps(Row(ChromeChipForm.Profile)), harness =>
        {
            var chip = harness.AbsoluteBounds(harness.Node("chrome-profile"));
            Assert.Equal(32f, chip.W, 0.5f);
            Assert.False(harness.Contains("chrome-connecting"));
        }, UnderClassic());
    }

    [Fact]
    public void An_unpinnable_destination_keeps_the_pin_slots_width_in_the_actions_row()
        => ViewHarness.With(host, ViewKeys.ShellTitleBar, TitleBarProps(Row(actions: true)), harness =>
        {
            var slot = harness.AbsoluteBounds(harness.Node("chrome-pin-placeholder"));
            Assert.Equal(ShellPolicy.Now.Chrome.Budget.NavButtonW, slot.W, 0.5f);
            Assert.Equal(TitleBar.ExpandedHeight, slot.H, 0.5f);
        }, UnderClassic());

    [Fact]
    public void Without_the_actions_stage_the_row_has_no_pin_slot()
        => ViewHarness.With(host, ViewKeys.ShellTitleBar, TitleBarProps(Row(actions: false)), harness =>
        {
            Assert.False(harness.Contains("chrome-pin-placeholder"));
        }, UnderClassic());

    [Fact]
    public void The_search_is_a_field_in_the_centre_column_or_the_magnifier_by_the_allocation()
    {
        ViewHarness.With(host, ViewKeys.ShellTitleBar, TitleBarProps(Row(search: SearchForm.Field)), harness =>
        {
            harness.RunFrames(5);
            var field = harness.AbsoluteBounds(harness.Node("chrome-search-field"));
            Assert.InRange(field.W, 280f - 0.5f, 420f + 0.5f);
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.ShellTitleBar, TitleBarProps(Row(search: SearchForm.Icon)), harness =>
        {
            harness.RunFrames(5);
            Assert.False(harness.Contains("chrome-search-field"));
        }, UnderClassic());
    }

    // ── the popup body (the suggestions view alone) ──────────────────────────────────────────────────────────────────────────

    static SearchSuggestionsProps Popup(SearchAnswerState state, params string[] queries) => new()
    {
        Source = Source(state, queries),
        Width = new Signal<float>(500f),
        Choose = static _ => { },
    };

    [Theory]
    [InlineData(SearchAnswerState.Idle)]
    [InlineData(SearchAnswerState.Pending)]
    [InlineData(SearchAnswerState.Empty)]
    [InlineData(SearchAnswerState.Failed)]
    public void The_progress_slot_is_always_the_first_child_at_the_popups_width(SearchAnswerState state)
        => ViewHarness.With(host, ViewKeys.ShellSearchSuggestions, Popup(state), harness =>
        {
            harness.RunFrames(5);
            var slot = harness.AbsoluteBounds(harness.Node("omni-progress"));
            Assert.Equal(500f, slot.W, 0.5f);
            Assert.Equal(3f, slot.H, 0.5f);
        }, UnderClassic());

    [Fact]
    public void A_pending_body_with_nothing_to_show_reserves_the_height_the_core_gives_it()
        => ViewHarness.With(host, ViewKeys.ShellSearchSuggestions, Popup(SearchAnswerState.Pending), harness =>
        {
            harness.RunFrames(40);
            var scene = harness.Shell.Host.Scene;
            var root = harness.AbsoluteBounds(scene.Parent(harness.Node("omni-progress")));
            // the popup root's padding (2 + 2), the progress slot (3) and the reserved skeleton body (the fixture's 120)
            Assert.Equal(127f, root.H, 1f);
        }, UnderClassic());

    [Fact]
    public void Query_rows_fill_the_body_beneath_the_progress_slot()
    {
        float idle = 0f, rows = 0f;
        ViewHarness.With(host, ViewKeys.ShellSearchSuggestions, Popup(SearchAnswerState.Idle), harness =>
        {
            harness.RunFrames(40);
            idle = harness.AbsoluteBounds(harness.Shell.Host.Scene.Parent(harness.Node("omni-progress"))).H;
        }, UnderClassic());
        ViewHarness.With(host, ViewKeys.ShellSearchSuggestions, Popup(SearchAnswerState.Results, "lo-fi", "loafers", "lofty"), harness =>
        {
            harness.RunFrames(40);
            rows = harness.AbsoluteBounds(harness.Shell.Host.Scene.Parent(harness.Node("omni-progress"))).H;
        }, UnderClassic());
        Assert.True(rows > idle + 50f, $"three query rows make the body taller than the empty one ({idle} -> {rows})");
    }
}
