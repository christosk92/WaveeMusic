// ── Wavee.ShellTests/ZuneFrameMountedTests.cs — Zune's frame, mounted (themes plan 6.3; P5 Z2a) ────────────────────────────────────────
//
// The app under Zune at 1440 x 960 (Standard, Regular) and the frame's views alone, on the facts and the records, never on pixels:
//
//   · the registry's templates for the frame's keys are Zune's (`zune.*`);
//   · the keyed regions (title bar, page column, play bar) exist, lie inside the window, never overlap and take its whole height; the band's
//     slot and the page sit inside the page column;
//   · the title bar holds Back, the pill and the wordmark; no tab strip, no tab count, no pane toggle, no Forward;
//   · the history menu opens on a right-click of Back;
//   · the panel docks inline at Standard: flush right, from under the title bar down to the play bar, the page column ending where it begins;
//   · the search popup is exactly the box's width and hangs from its left edge;
//   · a page head with row 2 shows no title, a hero head collapses to nothing, a plain one is one title.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneFrameMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;

    static ViewHarnessOptions UnderZune() => new() { Composition = ZuneTheme.Create(), WidthDip = W, HeightDip = H };

    static readonly string[] FrameKeys =
    [
        ViewKeys.ShellFrame.Id, ViewKeys.ShellTitleBar.Id, ViewKeys.ShellSearch.Id, ViewKeys.ShellSearchSuggestions.Id,
        ViewKeys.ShellCommandPalette.Id, ViewKeys.ShellHistoryMenu.Id, ViewKeys.ShellFileDrop.Id, ViewKeys.ShellDragChip.Id,
        ViewKeys.ShellCoverScrim.Id, ViewKeys.ShellContentState.Id, ViewKeys.PageHead.Id, ViewKeys.ShellPanel.Id,
    ];

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_registers_its_own_frame_views()
    {
        var builder = new FluentGpu.Theming.ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        foreach (string id in FrameKeys)
            Assert.True(registry.TemplateIdOf(id)?.StartsWith("zune.", StringComparison.Ordinal) == true, $"{id} is {registry.TemplateIdOf(id)}");
    }

    [Fact]
    public void The_mounted_app_draws_the_frame_with_zunes_templates()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(20);
            foreach (string id in FrameKeys)
            {
                string? template = ThemeHost.Registry.TemplateIdOf(id);
                Assert.True(template?.StartsWith("zune.", StringComparison.Ordinal) == true, $"{id} is drawn by {template}");
            }
        }, W, H);

    // ── the regions ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_frame_regions_exist_and_never_overlap()
        => ZuneHost.Mounted(host, shell =>
        {
            Until(shell, () => shell.FindPrefix("page:").Count > 0, "a page on screen under Zune");
            shell.RunFrames(30);
            var scene = shell.Host.Scene;
            RectF At(string key) => scene.AbsoluteRect(Node(shell, key));
            var title = At(ZuneFrameLayout.TitleBarKey);
            var content = At(ZuneFrameLayout.ContentKey);
            var bar = At(ZuneFrameLayout.PlayerBarKey);
            var band = At(ZuneFrameRules.BandSlotKey);
            var page = At(ZuneFrameRules.PageClipKey);

            Assert.Equal(ZuneTiers.Frame(WidthTier.Standard).TitleBarH, title.H, 0.5f);
            Assert.Equal(0f, title.Y, 0.5f);
            Assert.True(title.Y + title.H <= content.Y + 0.5f, "the title bar overlaps the page column");
            Assert.True(content.Y + content.H <= bar.Y + 0.5f, "the page column overlaps the play bar");
            Assert.Equal(H, title.H + content.H + bar.H, 1f);
            Assert.True(content.X >= -0.5f && content.X + content.W <= W + 0.5f, $"the page column runs outside the window ({content})");

            // The band's slot sits on top of the page column, the page under it, both inside the column.
            Assert.Equal(content.Y, band.Y, 0.5f);
            Assert.True(band.Y + band.H <= page.Y + 0.5f, "the band's slot overlaps the page");
            Assert.True(page.Y + page.H <= content.Y + content.H + 0.5f, "the page runs below the page column");
            Assert.Equal(content.W, page.W, 0.5f);
        }, W, H);

    [Fact]
    public void The_title_bar_has_back_and_the_pill_and_no_tabs_pane_toggle_or_forward()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(20);
            var scene = shell.Host.Scene;
            var title = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.TitleBarKey));
            foreach (string key in new[] { ZuneFrameRules.WordmarkKey, ZuneFrameRules.BackKey, ZuneFrameRules.PillKey })
            {
                var r = scene.AbsoluteRect(Node(shell, key));
                Assert.True(r.Y >= title.Y - 0.5f && r.Y + r.H <= title.Y + title.H + 0.5f, $"{key} stands in the title row ({r})");
            }
            // Standard: the pill at its rest width; the magnifier is not mounted.
            Assert.Equal(ZuneFrameRules.TitleBar(WidthTier.Standard).PillW, scene.AbsoluteRect(Node(shell, ZuneFrameRules.PillKey)).W, 0.5f);
            Assert.True(shell.Find(ZuneFrameRules.SearchIconKey).IsNull, "no magnifier beside the pill");
            // The wordmark starts on the page's gutter, then Back.
            var wordmark = scene.AbsoluteRect(Node(shell, ZuneFrameRules.WordmarkKey));
            Assert.Equal(ZuneTiers.Frame(WidthTier.Standard).Gutter, wordmark.X, 1f);
            Assert.True(scene.AbsoluteRect(Node(shell, ZuneFrameRules.BackKey)).X > wordmark.X, "Back follows the wordmark");

            Assert.True(shell.Find("chrome-tab-strip").IsNull, "no tab strip");
            Assert.True(shell.Find("chrome-tab-count").IsNull, "no tab count");
            Assert.True(shell.Find("chrome-forward").IsNull, "no Forward");
            Assert.True(shell.Find(ZuneFrameRules.PaneToggleKey).IsNull, "no pane toggle");
            Assert.False(ThemeHost.Facts.Peek().Capabilities.NavTree);
        }, W, H);

    // ── the history menu ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_right_click_on_back_opens_the_history_menu()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));
            shell.RunFrames(20);
            Assert.True(shell.Find(ZuneFrameRules.HistoryMenuKey).IsNull, "no menu before the right-click");

            var back = shell.Host.Scene.AbsoluteRect(Node(shell, ZuneFrameRules.BackKey));
            var p = new Point2(back.X + back.W / 2f, back.Y + back.H / 2f);
            shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: 1000));
            shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 1, 0, TimestampMs: 1010));
            shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 1, 0, TimestampMs: 1060));
            try
            {
                Until(shell, () => !shell.Find(ZuneFrameRules.HistoryMenuKey).IsNull, "the history menu opened from Back");
            }
            finally
            {
                Key(shell, 0x1B);   // Escape closes the menu
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
                shell.RunFrames(10);
            }
        }, W, H);

    // ── the panel ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_panel_docks_inline_at_standard_from_the_title_bar_to_the_play_bar()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(20);
            try
            {
                Shell.PanelHost.Shared.SetMode(Shell.RailMode.Queue);
                shell.RunFrames(40);
                Assert.Equal(PanelPlacement.Dock, Shell.Ui.PanelPlacement.Peek());

                var scene = shell.Host.Scene;
                var panel = scene.AbsoluteRect(Node(shell, ZuneFrameRules.PanelHostKey));
                var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
                var title = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.TitleBarKey));
                var bar = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.PlayerBarKey));

                Assert.Equal(W, panel.X + panel.W, 0.5f);                    // flush right
                Assert.Equal(title.Y + title.H, panel.Y, 0.5f);              // full height: from under the title bar ...
                Assert.Equal(bar.Y, panel.Y + panel.H, 0.5f);                // ... down to the play bar
                Assert.Equal(Shell.Ui.RailWidth.Peek(), panel.W, 0.5f);
                Assert.True(content.X + content.W <= panel.X + 0.5f, $"the page column runs under the docked panel ({content} / {panel})");
                Assert.False(shell.Find(ZuneFrameRules.PanelHeadKey).IsNull, "the pivot head is drawn");
                Assert.False(shell.Find(ZuneFrameRules.PanelPivotKeyPrefix + PanelMode.Queue).IsNull, "the queue pivot");
            }
            finally
            {
                Shell.PanelHost.Shared.Close();
                shell.RunFrames(30);
            }
        }, W, H);

    // ── the search ───────────────────────────────────────────────────────────────────────────────────────────────────────

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
        QueryRowHeight = 36f,
        RichRowHeight = 56f,
        PopupBodyMaxHeight = 560f,
    };

    [Fact]
    public void The_popup_body_is_exactly_the_pills_width()
        => ViewHarness.With(host, ViewKeys.ShellSearchSuggestions, new SearchSuggestionsProps
        {
            Source = Source(SearchAnswerState.Results, "lo-fi", "loafers"),
            Width = new Signal<float>(ZuneFrameRules.TitleBar(WidthTier.Standard).PillW),
            Choose = static _ => { },
        }, harness =>
        {
            harness.RunFrames(10);
            var slot = harness.AbsoluteBounds(harness.Node(ZuneFrameRules.PopupProgressKey));
            Assert.Equal(ZuneFrameRules.TitleBar(WidthTier.Standard).PillW, slot.W, 0.5f);
            Assert.Equal(ZuneFrameRules.ProgressSlotHeight, slot.H, 0.5f);
        }, UnderZune());

    [Fact]
    public void The_search_flyout_hangs_from_the_pill_at_its_width()
    {
        var text = new Signal<string>("");
        var props = new ShellSearchProps
        {
            Form = SearchForm.Field,
            Model = FixtureModel(),
            TextCell = () => text,
            FocusTicket = static () => 0,
            FieldWidth = static () => 330f,
            Suggestions = Source(SearchAnswerState.Pending),
        };
        // NOT ViewHarness: it mounts the view with no OverlayHost, so the AutoSuggestBox's popup goes to the null overlay service and never
        // opens. Mounted here under the INSTALLED Zune (its registry and facts are the root's, so the popup layer resolves Zune's
        // suggestions body) inside an overlay host, as the frame mounts it.
        host.Run(ZuneHost.Activate);
        try
        {
            host.Run(() =>
            {
                TierFacts tiers = WindowTiers.Resolve(ThemeHost.Facts.Peek().Policies.Tiers, W, H, W);
                ThemeHost.PublishTiers(in tiers);
                var shell = Shell.Headless.Mount(() => OverlayHost.Create(FluentGpu.Theming.Slots.View(ViewKeys.ShellSearch, props)), W, H);
                try
                {
                    shell.RunFrames(5);
                    var scene = shell.Host.Scene;
                    var pill = scene.PresentedAbsoluteRect(Node(shell, ZuneFrameRules.PillKey));
                    Assert.Equal(ZuneFrameRules.TitleBar(WidthTier.Standard).PillW, pill.W, 0.5f);

                    // Click into the pill and type: the popup opens under it.
                    var p = new Point2(pill.X + pill.W / 2f, pill.Y + pill.H / 2f);
                    shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: 990));
                    shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1000));
                    shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1040));
                    shell.RunFrames(5);
                    shell.Window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'l'));
                    shell.Window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'o'));
                    Until(shell, () => !shell.Find(ZuneFrameRules.PopupProgressKey).IsNull, "the suggestions popup opened under the pill");
                    shell.RunFrames(40);   // the focus growth settles

                    // The popup is the box's width and starts at its left edge (the focused box, after its growth).
                    var box = scene.PresentedAbsoluteRect(Node(shell, ZuneFrameRules.PillKey));
                    var popup = scene.PresentedAbsoluteRect(Node(shell, ZuneFrameRules.PopupProgressKey));
                    Assert.Equal(box.W, popup.W, 1f);
                    Assert.Equal(box.X, popup.X, 1f);
                    Assert.True(popup.Y >= box.Y + box.H - 0.5f, $"the popup hangs below the pill ({popup} under {box})");
                }
                finally
                {
                    shell.Dispose();
                }
                ShellHost.AssertNoNetwork();
            });
        }
        finally
        {
            host.Run(ZuneHost.Restore);
        }
    }

    // ── the page head ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_page_head_with_row_two_or_a_hero_shows_no_title_and_a_plain_one_shows_one()
    {
        var plain = new PageChrome("Browse", [new PageCrumb("Browse", null)]);
        var withViews = plain with { Views = [new PageView("top", "Top", Current: true, Go: static () => { })] };
        var hero = new PageChrome("Concerts", [new PageCrumb("Browse", static () => { }), new PageCrumb("Concerts", null)], Form: PageHeadForm.Hero);

        ViewHarness.With(host, ViewKeys.PageHead, new PageHeadProps(plain),
            harness => Assert.True(harness.Contains(ZuneFrameRules.PageTitleKey), "a plain page draws its one title"), UnderZune());
        ViewHarness.With(host, ViewKeys.PageHead, new PageHeadProps(withViews),
            harness => Assert.False(harness.Contains(ZuneFrameRules.PageTitleKey), "a page with row 2 shows no title"), UnderZune());
        ViewHarness.With(host, ViewKeys.PageHead, new PageHeadProps(hero),
            harness => Assert.False(harness.Contains(ZuneFrameRules.PageTitleKey), "a hero head collapses to nothing"), UnderZune());
    }
}
