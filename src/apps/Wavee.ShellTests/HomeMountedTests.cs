// ── Wavee.ShellTests/HomeMountedTests.cs — Home's views, mounted (themes plan 6.1, T10a1) ─────────────────────────────────
//
// The Home page, the facet row, the daylist card and clock, the zone bodies, the drill page and the customizer are the structure's since T10a1;
// the core hosts keep the facet machine, the model, the verdict, the wash claim, the timers and the layout document (`Wavee.Tests` pins the
// pure ones: `HomeContractTests`, `HomeHostTests`, `FacetDimPlanTests`...). What only a mounted tree can say:
//
//   registered   Classic registers every Home key, and the real shell draws the Home page through them (the column, the pinned facet band, the
//                zone chapters)
//   facet row    the band is exactly the rule's height and holds no failure bar
//   failure      a failed switch lays its InfoBar over the content's top edge in a slot of its own: the content keeps its place (H1)
//   customizer   the rows stand in the document's order at the rule's row height

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee;
using Wavee.HomeUi;
using Wavee.Themes.Classic;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class HomeMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_home_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.HomePage.Id, "classic.home.page"),
                (ViewKeys.HomeFacetRow.Id, "classic.home.facet-row"),
                (ViewKeys.HomeDaylist.Id, "classic.home.daylist"),
                (ViewKeys.HomeDaylistClock.Id, "classic.home.daylist-clock"),
                (ViewKeys.HomeZoneRecentGrid.Id, "classic.home.zone.recent-grid"),
                (ViewKeys.HomeCharts.Id, "classic.home.charts"),
                (ViewKeys.HomeZoneCoverShelf.Id, "classic.home.zone.cover-shelf"),
                (ViewKeys.HomeSection.Id, "classic.home.section"),
                (ViewKeys.HomeCustomize.Id, "common.customize"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    [Fact]
    public void The_real_shell_draws_the_home_page_through_the_registered_views()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            Until(shell, () => !shell.Find("home:column").IsNull, "the home page's column");

            var band = Node(shell, "home:facet:band");
            Assert.Equal(ClassicHomeRules.FacetRowH, shell.Host.Scene.Bounds(band).H, 0.5f);
            Assert.False(shell.Find("home:content").IsNull, "the sticky-clipped content column under the band");

            UntilBackground(shell, () => shell.FindPrefix("zone:").Count > 0, "the zone chapters of the fake feed");
        });

    // ── the facet row ────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly FacetWord[] Words =
    [
        new("", "All", false, null, null),
        new("music-chip", "Music", true, "music-following-chip", "Following"),
        new("podcasts-chip", "Podcasts", false, null, null),
    ];

    static FacetRowProps RowProps(FacetSwitchState state)
    {
        var index = new Signal<int>(0);
        return new FacetRowProps(new Signal<FacetWord[]>(Words), new Signal<FacetSwitchState>(state), () => index,
            static _ => { }, static () => { }, static _ => false);
    }

    [Fact]
    public void The_facet_band_is_exactly_the_rules_height_and_the_failure_bar_is_not_drawn_at_rest()
        => ViewHarness.With(host, ViewKeys.HomeFacetRow, RowProps(FacetSwitchState.Initial("")),
            harness =>
            {
                Assert.Equal(ClassicHomeRules.FacetRowH, harness.AbsoluteBounds(harness.Node("home:facet:band")).H, 0.5f);
                Assert.True(harness.Contains("home:facet:bar"), "the busy bar's 3-px slot is always laid out");
                Assert.False(harness.Contains("home:facet:bar:busy"), "the bar itself is not drawn at rest");
                Assert.False(harness.Contains("home:facet:failure"));
            }, UnderClassic());

    [Fact]
    public void A_failed_switch_draws_no_failure_bar_in_the_facet_row()
        => ViewHarness.With(host, ViewKeys.HomeFacetRow,
            RowProps(new FacetSwitchState(FacetPhase.Failed, "", "", StartedAtMs: 0, FailedTarget: "podcasts-chip")),
            harness =>
            {
                harness.RunFrames(10);
                Assert.Equal(ClassicHomeRules.FacetRowH, harness.AbsoluteBounds(harness.Node("home:facet")).H, 0.5f);
                Assert.False(harness.Contains("home:facet:failure"), "the page lays the bar in its own slot");
            }, UnderClassic());

    // ── the failure slot (tuned Classic H1) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_failed_switch_lays_its_failure_bar_over_the_contents_top_and_the_content_keeps_its_place()
    {
        var sw = new Signal<FacetSwitchState>(FacetSwitchState.Initial(""));
        var index = new Signal<int>(0);
        var row = new FacetRowProps(new Signal<FacetWord[]>(Words), sw, () => index, static _ => { }, static () => { }, static _ => false);
        var model = new ScreenModel("", Array.Empty<Zone>(), "", Array.Empty<ChipInput>());
        var props = new HomePageProps
        {
            Facet = "", ScrollKey = "test:home", Scroll = new ScrollHandle(), Group = new object(), FacetRow = row, Switch = sw,
            Pending = static () => false, Failed = static () => false, Content = () => model, Seed = () => model,
            OnContent = static _ => { }, RetryFirstLoad = static () => { }, Wash = new BoxEl(),
        };
        ViewHarness.With(host, ViewKeys.HomePage, props, harness =>
        {
            harness.RunFrames(10);
            float rest = harness.AbsoluteBounds(harness.Node("home:content")).Y;
            Assert.False(harness.Contains("home:facet:failure"), "no bar at rest");

            sw.Value = new FacetSwitchState(FacetPhase.Failed, "", "", StartedAtMs: 0, FailedTarget: "podcasts-chip");
            harness.RunFrames(60);   // the bar slides in from above (its Enter Dy): measure where it lands, not mid-entrance
            var band = harness.AbsoluteBounds(harness.Node("home:facet:band"));
            var content = harness.AbsoluteBounds(harness.Node("home:content"));
            var failure = harness.AbsoluteBounds(harness.Node("home:facet:failure"));
            Assert.Equal(rest, content.Y, 0.5f);                 // the content never moves
            Assert.Equal(content.Y, failure.Y, 0.5f);            // the bar lies on the content's top edge
            Assert.True(failure.Y >= band.Y + band.H - 0.5f, "the bar sits below the pinned band");

            sw.Value = FacetSwitchState.Initial("");
            harness.RunFrames(60);
            Assert.Equal(rest, harness.AbsoluteBounds(harness.Node("home:content")).Y, 0.5f);
        }, UnderClassic());
    }

    // ── the customizer ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_customizer_stands_its_rows_in_the_documents_order_at_the_rules_row_height()
    {
        var doc = LayoutFile.DefaultDoc();
        var props = new HomeCustomizeProps
        {
            Doc = doc,
            Reorder = new Reorderable("wavee.test-home-zone-layout") { Spacing = 0f, RequireDropOnList = true },
            Toggle = static _ => { },
            Move = static (_, _) => { },
            Reset = static () => { },
        };
        ViewHarness.With(host, ViewKeys.HomeCustomize, props,
            harness =>
            {
                Assert.True(harness.Contains("home-customize-header"));
                float previous = float.NegativeInfinity;
                foreach (var entry in doc.Zones)
                {
                    var bounds = harness.AbsoluteBounds(harness.Node(entry.Kind));
                    Assert.Equal(CommonCustomizeRules.CustomizeRowH, bounds.H, 0.5f);
                    Assert.True(bounds.Y > previous, "'" + entry.Kind + "' stands below the row before it");
                    previous = bounds.Y;
                }
            }, UnderClassic());
    }
}
