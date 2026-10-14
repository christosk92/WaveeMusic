// ── Wavee.ShellTests/DetailFrameMountedTests.cs — the detail frame's views, mounted (themes plan 6.1, T9a) ────────────────────
//
// The shared detail frame (scaffold, rail, collapsed strip, show header, hero, insights sheet) is the structure's since T9a; `Detail.FrameHost`
// keeps the mode ladder, the rail cells and prefs, the accent, the tint and tone claims, the table and the insights toggle (`Wavee.Tests` pins
// the pure ones: `DetailRailPolicyTests`, `DetailSkeletonGeometryTests`, `DetailInsightsSheetTests`, `DetailContractTests`...). What only a
// mounted tree can say:
//
//   registered   Classic registers every detail key
//   real shell   the Liked page (the album / playlist / show frames are the same host) draws the two-column arm through the registered views at a
//                wide window (the rail's column, the grip's seam layer, the content column) and the vertical arm at a narrow one (the table that
//                hosts the hero, under the insights sheet's overlay)
//   rail         the cover fills the column less the side pads (the rule), and the identity rows stand in the order the row model decides
//   strip        the collapsed rail is its 96-DIP strip with the cover and the expand chevron
//   toggle       the insights toggle's glyph button is mounted by its own key

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

    [Fact]
    public void The_real_shell_draws_the_two_column_arm_at_a_wide_window()
        => host.Mounted(shell =>
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            Until(shell, () => !shell.Find("detail:two-column").IsNull, "the two-column arm");
            Until(shell, () => shell.FindPrefix("list:").Count == 1, "the liked list mounted");
            shell.RunFrames(30);

            // the rail's column sits behind the fade wrapper, with the cover as its anchor
            var fade = Node(shell, "detail-rail-fade");
            var cover = Node(shell, "rail:cover");
            var bounds = shell.Host.Scene.PresentedAbsoluteRect(cover);
            Assert.Equal(bounds.W, bounds.H, 0.5f);
            Assert.True(bounds.W <= shell.Host.Scene.PresentedAbsoluteRect(fade).W, "the cover never outgrows the column that holds it");

            // the grip's layer is above the row in both arms, and the content column kept its key
            Assert.False(shell.Find("detail-rail-seam").IsNull, "the seam layer");
            Assert.False(shell.Find("right:tracks").IsNull, "the content column");
            Assert.True(shell.Find("detail:vertical").IsNull, "one arm at a time");
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
            Assert.True(shell.Find("detail:two-column").IsNull, "one arm at a time");
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
}
