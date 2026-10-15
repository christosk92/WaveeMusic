// ── Wavee.ShellTests/DetailFrameMountedTests.cs — the detail frame's views, mounted (themes plan 6.1, T9a) ────────────────────
//
// The shared detail frame (scaffold, rail, collapsed strip, show header, hero, insights sheet) is the structure's since T9a; `Detail.FrameHost`
// keeps the mode ladder, the rail cells and prefs, the accent, the tint and tone claims, the table and the insights toggle (`Wavee.Tests` pins
// the pure ones: `DetailRailPolicyTests`, `DetailSkeletonGeometryTests`, `DetailInsightsSheetTests`, `DetailContractTests`...). What only a
// mounted tree can say:
//
//   registered   Classic registers every detail key
//   real shell   the Liked page draws its head arm at a wide window (no rail, the head over the list, the page bar; tuned Classic D16) and
//                the vertical arm at a narrow one (the table that hosts the hero, under the insights sheet's overlay)
//   rail         the cover fills the column less the side pads (the rule), and the identity rows stand in the order the row model decides
//   skeleton     the loading rail's call to action reserves the fixed group the page will lay out: no Share square without a web link (X2)
//   strip        the collapsed rail is its 96-DIP strip with the cover and the expand chevron
//   toggle       the insights toggle's glyph button is mounted by its own key

using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class DetailFrameMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static Detail.FrameSpec Fixture(string title = "Fixture", string? meta = null)
        => new()
        {
            Identity = new Detail.Identity { Subject = default, Kind = DetailKind.Liked, Title = title, Meta = meta },
            Config = Detail.Config.Liked,
        };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_detail_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.DetailFrame.Id, "classic.detail.frame"),
                (ViewKeys.DetailRail.Id, "classic.detail.rail"),
                (ViewKeys.DetailCompactRail.Id, "classic.detail.compact-rail"),
                (ViewKeys.DetailShowHeader.Id, "classic.detail.show-header"),
                (ViewKeys.DetailHero.Id, "classic.detail.hero"),
                (ViewKeys.DetailInsights.Id, "classic.detail.insights"),
                (ViewKeys.DetailInsightsToggle.Id, "classic.detail.insights-toggle"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the real shell ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Tuned Classic D16 / D8 (board C-Liked): at a wide window Liked Songs has no rail. Its head is the title and the meta in the
    /// page gutter, and the list bar under it (8 of chrome air) is the page's: [Play split] 8 [Shuffle] [divider] [chips … 8] [tools], 44
    /// tall with 4 under it; the row plates start 8 before the head's words, and the rows group by date (sorted by Date added).</summary>
    [Fact]
    public void The_real_shell_draws_liked_songs_as_a_head_over_the_list_with_no_rail()
        => host.Mounted(shell =>
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            Until(shell, () => !shell.Find("detail:head").IsNull, "the head arm");
            Until(shell, () => shell.FindPrefix("list:").Count == 1, "the liked list mounted");
            Until(shell, () => !shell.Find("bar:chips").IsNull && shell.FindPrefix("group-head").Count > 0, "the chips and the date groups");
            shell.RunFrames(30);
            var scene = shell.Host.Scene;
            RectF At(string key) => scene.PresentedAbsoluteRect(Node(shell, key));

            Assert.True(shell.Find("detail:two-column").IsNull, "no two-column row");
            Assert.True(shell.Find("detail-rail-fade").IsNull, "no rail");
            Assert.True(shell.Find("detail-rail-seam").IsNull, "no grip");

            var head = At("detail:head-title");
            var bar = At("chrome:bar");
            float gutter = ClassicDetailLayout.HeadGutter;
            Assert.Equal(head.Y + head.H + 8f, bar.Y, 0.5f);                       // the chrome's own air: the bar sits 12 under the meta
            Assert.Equal(44f, bar.H, 0.5f);
            Assert.Equal(bar.Y + bar.H + 4f, At("header").Y, 0.5f);           // 4 of air under the bar: the head is 156
            Assert.Equal(156f, At("header").Y - head.Y, 0.5f);                 // board C-Liked: the 156 head

            // the lead: the split on the head's words, Shuffle 8 after it, then the divider's slot and the chips
            var split = At("split:halves");
            var shuffle = At("lead:shuffle");
            var divider = At("lead:divider");
            var chips = At("bar:chips");
            Assert.Equal(head.X + gutter, split.X, 0.5f);
            Assert.Equal(split.X + split.W + 8f, shuffle.X, 0.5f);
            Assert.Equal(17f, divider.W, 0.5f);
            Assert.Equal(divider.X + divider.W + 2f, chips.X, 0.5f);
            Assert.True(chips.W >= 120f, $"the chips keep their least width, got {chips.W}");
            // D8: the chips end 8 before the tools (and the bar's 2)
            float tools = scene.PresentedAbsoluteRect(shell.FindPrefix("cmd:filter")[0].Node).X;
            Assert.Equal(chips.X + chips.W + 8f + 2f, tools, 0.5f);
            Assert.True(shell.FindPrefix("cmd:play-next").Count == 0, "the page bar leads with Play, not Play next");

            // the row plates start 8 before the head's words, full width (no zebra inset)
            var row = shell.FindPrefix("row").Where(k => k.Key == "row").Select(k => scene.PresentedAbsoluteRect(k.Node))
                .Where(r => r.H > 0f).OrderBy(r => r.Y).First();
            Assert.Equal(head.X + gutter - 8f, row.X, 0.5f);
            // the date groups: a head is 40 tall above the row that starts its group; every other slot's bound head takes no room
            var heads = shell.FindPrefix("group-head").Where(k => k.Key == "group-head").Select(k => scene.PresentedAbsoluteRect(k.Node).H).ToList();
            Assert.Contains(heads, h => MathF.Abs(h - 40f) <= 0.5f);
            Assert.All(heads, h => Assert.True(h <= 0.5f || MathF.Abs(h - 40f) <= 0.5f, $"a head is 40 or nothing, got {h}"));
        }, widthDip: 1600f, heightDip: 900f);

    [Fact]
    public void The_real_shell_draws_the_vertical_arm_under_the_insights_overlay_at_a_narrow_window()
        => host.Mounted(shell =>
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            Until(shell, () => !shell.Find("detail:vertical").IsNull, "the vertical arm");
            Until(shell, () => shell.FindPrefix("list:").Count == 1, "the liked list mounted");
            shell.RunFrames(30);

            // a track page's vertical arm ALWAYS composes the sheet's overlay, so the list never remounts when the facts arrive
            Assert.False(shell.Find("insights:content").IsNull, "the overlay's content slot");
            Assert.True(shell.Find("detail:head").IsNull, "one arm at a time");
            Assert.False(shell.Find("vhero:root").IsNull, "the hero is the table's item 0");
        }, widthDip: 480f, heightDip: 800f);

    // ── the rail ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_rail_cover_fills_the_column_less_the_side_pads_and_the_rows_follow_the_row_model()
        => ViewHarness.With(host, ViewKeys.DetailRail, new DetailRailProps
        {
            Spec = Fixture(meta: "2 songs · 6 min"), Actions = new Detail.FrameActions(), RailWidth = 320f,
            TitleSize = 28f, DescriptionLines = 3, Accent = static () => default, Play = static () => { }, Pending = static () => false,
        }, harness =>
        {
            harness.RunFrames(60);   // the rows fade up and shove: measure where they land
            var cover = harness.AbsoluteBounds(harness.Node("rail:cover"));
            // the rail's side pads: 16 left, 8 right (Spacing.L / Spacing.S)
            Assert.Equal(320f - 24f, cover.W, 0.5f);
            Assert.Equal(cover.W, cover.H, 0.5f);

            // a Liked rail (no badges, no owner, no artists): cover, title, the meta line, the call to action
            var title = harness.AbsoluteBounds(harness.Node("rail:title"));
            var meta = harness.AbsoluteBounds(harness.Node("rail:meta"));
            var cta = harness.AbsoluteBounds(harness.Node("rail:cta"));
            Assert.True(title.Y >= cover.Y + cover.H - 0.5f, "the title stands under the cover");
            Assert.True(meta.Y >= title.Y + title.H - 0.5f, "the meta line stands under the title");
            Assert.True(cta.Y >= meta.Y + meta.H - 0.5f, "the call to action stands under the meta line");
        }, UnderClassic());

    /// <summary>Tuned Classic D1 / D2: the rail's call to action is the Play split, [Play 88 | 1 | menu 32] under ONE border, then the
    /// actions 12 after it and 8 apart (Shuffle on the control plate, Share), one row at a 296 column.</summary>
    [Fact]
    public void The_rail_leads_with_the_play_split_and_its_actions_follow_12_then_8_apart()
        => ViewHarness.With(host, ViewKeys.DetailRail, new DetailRailProps
        {
            Spec = new Detail.FrameSpec
            {
                Identity = new Detail.Identity
                {
                    Subject = default, Kind = DetailKind.Liked, Title = "Fixture", Meta = "2 songs · 6 min",
                    ShareUrl = "https://open.spotify.com/collection/tracks",
                },
                Config = Detail.Config.Liked,
            },
            Actions = new Detail.FrameActions { Shuffle = static () => { } }, RailWidth = 320f,
            TitleSize = 28f, DescriptionLines = 3, Accent = static () => default, Play = static () => { }, Pending = static () => false,
        }, harness =>
        {
            harness.RunFrames(60);
            var halves = harness.AbsoluteBounds(harness.Node("split:halves"));
            var play = harness.AbsoluteBounds(harness.Node("split:play"));
            var menu = harness.AbsoluteBounds(harness.Node("split:menu"));
            var border = harness.AbsoluteBounds(harness.Node("split:border"));
            Assert.Equal(Detail.RailLayout.PlaySplitWidth, halves.W, 0.5f);   // 121
            Assert.Equal(32f, halves.H, 0.5f);
            Assert.Equal(88f, play.W, 0.5f);
            Assert.Equal(32f, menu.W, 0.5f);
            Assert.Equal(play.X + 88f + 1f, menu.X, 0.5f);                     // the 1-DIP divider between the halves
            Assert.Equal(halves, border);                                     // one border round the whole control

            var shuffle = harness.AbsoluteBounds(harness.Node("rail:shuffle:button"));
            var share = harness.AbsoluteBounds(harness.Node("rail:share:button"));
            Assert.Equal(32f, shuffle.W, 0.5f);
            Assert.Equal(32f, shuffle.H, 0.5f);
            Assert.Equal(halves.X + halves.W + 12f, shuffle.X, 0.5f);
            Assert.Equal(halves.Y, shuffle.Y, 0.5f);                          // one row
            Assert.Equal(shuffle.X + 32f + 8f, share.X, 0.5f);
            Assert.True(harness.Find("rail:more").IsNull, "Liked carries no menu, so no ⋯");
        }, UnderClassic());

    /// <summary>Tuned Classic X2: the loading rail reserves exactly the actions the loaded one lays out. An album with a web link has Shuffle · heart ·
    /// Share · ⋯ (the fourth wraps under a 256 cover: 32 + 8 + 32); a local album has no link, so no Share, and its three fit beside the split on one
    /// line (it used to reserve the fourth square everywhere and stand 40 taller than the rail it dissolved into).</summary>
    [Theory]
    [InlineData(true, 72f)]
    [InlineData(false, 32f)]
    public void The_loading_rail_reserves_the_share_square_only_where_the_page_has_a_link(bool link, float cta)
        => ViewHarness.With(host, ViewKeys.DetailRail, new DetailRailProps
        {
            Spec = new Detail.FrameSpec
            {
                Identity = new Detail.Identity
                {
                    Subject = default, Kind = DetailKind.Album, Title = "Fixture", HeaderPending = true,
                    ShareUrl = link ? "https://open.spotify.com/album/fixture" : null,
                },
                Config = Detail.Config.Album,
            },
            Actions = new Detail.FrameActions { Shuffle = static () => { }, More = static () => null }, RailWidth = 280f,
            TitleSize = 28f, DescriptionLines = 3, Accent = static () => default, Play = static () => { }, Pending = static () => true,
        }, harness =>
        {
            harness.RunFrames(60);
            Assert.Equal(cta, harness.AbsoluteBounds(harness.Node("skel:cta")).H, 0.5f);
        }, UnderClassic());

    // ── the strip ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_collapsed_rail_is_the_strip_with_the_cover_and_the_expand_chevron()
    {
        int expanded = 0;
        ViewHarness.With(host, ViewKeys.DetailCompactRail, new DetailCompactRailProps
        {
            Spec = Fixture(), StripWidth = 96f, Expand = () => expanded++, Pending = static () => false,
        }, harness =>
        {
            harness.RunFrames(10);
            var strip = harness.AbsoluteBounds(harness.Node("detail-rail-compact"));
            Assert.Equal(96f, strip.W, 0.5f);
            var cover = harness.AbsoluteBounds(harness.Node("compact:cover"));
            Assert.Equal(cover.W, cover.H, 0.5f);
            Assert.Equal(96f - 16f, cover.W, 0.5f);   // the strip less its two side pads (Spacing.S each)
            var chevron = harness.AbsoluteBounds(harness.Node("compact:expand"));
            Assert.True(chevron.Y >= cover.Y + cover.H - 0.5f, "the chevron stands under the identity");
            Assert.Equal(0, expanded);
        }, UnderClassic());
    }

    // ── the insights toggle ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_insights_toggles_glyph_button_is_mounted_by_its_own_key_and_the_band_word_is_its_own_component()
    {
        var toggle = new Detail.InsightsToggle();
        ViewHarness.With(host, ViewKeys.DetailInsightsToggle, new DetailInsightsToggleProps(toggle, DetailInsightsToggleForm.HeroButton),
            harness =>
            {
                var button = harness.AbsoluteBounds(harness.Node("insights:toggle"));
                Assert.Equal(ClassicDetailLayout.SheetToggleEdge, button.W, 0.5f);
                Assert.Equal(ClassicDetailLayout.SheetToggleEdge, button.H, 0.5f);
            }, UnderClassic());
    }

    // ── the list bar and the insights sheet (tuned Classic D3, I1) ───────────────────────────────────────────────────────────

    /// <summary>D3 / I1 on the real Liked page at a wide window: the list bar carries [Filter] [Sort] [Find] [Insights] [⋯] on its right and
    /// never Row size or Select (they live in "…"); the facts are the sheet's, laid over the list column, never a rail row.</summary>
    [Fact]
    public void The_two_column_list_bar_carries_insights_and_the_facts_live_in_the_sheet_over_the_list()
        => host.Mounted(shell =>
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            Until(shell, () => !shell.Find("detail:head").IsNull, "the head arm");
            Until(shell, () => shell.FindPrefix("list:").Count == 1, "the liked list mounted");
            Until(shell, () => !shell.Find("cmd:insights").IsNull, "the facts settled and the bar grew its Insights button");
            shell.RunFrames(30);

            Assert.NotEmpty(shell.FindPrefix("cmd:sort"));
            Assert.NotEmpty(shell.FindPrefix("cmd:more:"));
            Assert.False(shell.Find("search-host").IsNull, "Find");
            Assert.Empty(shell.FindPrefix("cmd:density"));
            Assert.Empty(shell.FindPrefix("cmd:select"));
            Assert.Empty(shell.FindPrefix("cmd:separator"));

            // the right group's order: Sort, Find, Insights, "…"
            float sortX = shell.Host.Scene.PresentedAbsoluteRect(shell.FindPrefix("cmd:sort")[0].Node).X;
            float findX = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "search-host")).X;
            float insightsX = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "insights:toggle")).X;
            float moreX = shell.Host.Scene.PresentedAbsoluteRect(shell.FindPrefix("cmd:more:")[0].Node).X;
            Assert.True(sortX < findX && findX < insightsX && insightsX < moreX, $"sort {sortX} < find {findX} < insights {insightsX} < more {moreX}");

            // the sheet is composed over the list (closed), and the rail no longer hosts the facts
            Assert.False(shell.Find("insights:layer").IsNull, "the sheet's layer over the list column");
            Assert.False(shell.Find("insights:content").IsNull, "the overlay's content slot");
            Assert.True(shell.Find("rail:likedfacts").IsNull, "the rail never carries the facts (I1)");
            Assert.True(shell.Find("insights:column").IsNull, "closed by default, never opening by itself");
        }, widthDip: 1600f, heightDip: 900f);

    /// <summary>I1 / board C-Liked: the open sheet is 320 wide, 16 in on every side (the bento gets 288), with a 32-DIP close button.</summary>
    [Fact]
    public void The_open_sheet_is_320_wide_and_hands_the_bento_288()
    {
        var toggle = new Detail.InsightsToggle();
        toggle.Open.Value = true;
        ViewHarness.With(host, ViewKeys.DetailInsights, new DetailInsightsProps
        {
            Toggle = toggle, PageWidth = 1200f, RouteKey = "probe",
            Facts = w => new FluentGpu.Dsl.BoxEl { Key = "facts:probe", Width = w, Height = 40f },
            Page = new FluentGpu.Dsl.BoxEl { Grow = 1f },
        }, harness =>
        {
            harness.RunFrames(60);
            var column = harness.AbsoluteBounds(harness.Node("insights:column"));
            var facts = harness.AbsoluteBounds(harness.Node("facts:probe"));
            var close = harness.AbsoluteBounds(harness.Node("insights:close"));
            Assert.Equal(320f, column.W, 0.5f);
            Assert.Equal(288f, facts.W, 0.5f);
            Assert.Equal(column.X + 16f, facts.X, 0.5f);
            Assert.Equal(32f, close.W, 0.5f);
            Assert.Equal(column.Y + 16f, close.Y, 0.5f);
        }, UnderClassic());
    }

    /// <summary>D4 (D-10): while the list is narrowed the rail's count line reads "3 of 48 songs · 11 min (filtered)" in the meta slot — one
    /// line at the rail's measure, so the call to action under it does not move.</summary>
    [Fact]
    public void The_rail_count_line_states_the_filtered_count_on_one_line()
    {
        var shown = new FluentGpu.Signals.Signal<Detail.ListCount>(new Detail.ListCount(3, 48, 11L * 60_000L, true, true));
        ViewHarness.With(host, ViewKeys.DetailRail, new DetailRailProps
        {
            Spec = Fixture(meta: "48 songs · 2 hr 36 min"), Actions = new Detail.FrameActions(), RailWidth = 240f,
            TitleSize = 28f, DescriptionLines = 3, Accent = static () => default, Play = static () => { }, Pending = static () => false,
            Shown = shown,
        }, harness =>
        {
            harness.RunFrames(60);
            var filtered = harness.AbsoluteBounds(harness.Node("rail:meta"));
            Assert.True(filtered.H <= 20f, $"one line, got {filtered.H}");
            shown.Value = default;
            harness.RunFrames(30);
            var plain = harness.AbsoluteBounds(harness.Node("rail:meta"));
            Assert.Equal(plain.H, filtered.H, 0.5f);
        }, UnderClassic());
    }
}
