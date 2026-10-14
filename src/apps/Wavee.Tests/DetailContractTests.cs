// ── Wavee.Tests/DetailContractTests.cs — the detail frame's published keys and Classic's registration (themes plan 6.0, T9a) ──
//
// The shared detail frame is the structure's picture over a core host (`Detail.FrameHost`): the scaffold (`Detail.Frame`), the rail
// (`Detail.Rail`), the collapsed strip (`Detail.CompactRail`), the vertical show header (`Detail.ShowHeader`), the hero (`Detail.Hero`, and
// its loading band as a style) and the insights sheet with its two entry points. What is pure about the contract: the ids (a published id never
// changes), Classic registering every key under a distinct template id and the hero's style, and the equality the props promise: the frame's
// records never compare equal to another instance (a data-equal spec can carry newer slot builders, so a view that skipped on an "equal"
// record would keep the old ones), the hero's compares by data (the table owns that gate).

using FluentGpu.Signals;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class DetailContractTests
{
    static Detail.FrameSpec Spec(string title = "Fixture")
        => new()
        {
            Identity = new Detail.Identity { Subject = default, Kind = DetailKind.Liked, Title = title },
            Config = Detail.Config.Liked,
        };

    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Detail.Frame", ViewKeys.DetailFrame.Id);
        Assert.Equal("Detail.Rail", ViewKeys.DetailRail.Id);
        Assert.Equal("Detail.CompactRail", ViewKeys.DetailCompactRail.Id);
        Assert.Equal("Detail.ShowHeader", ViewKeys.DetailShowHeader.Id);
        Assert.Equal("Detail.Hero", ViewKeys.DetailHero.Id);
        Assert.Equal("Detail.Insights", ViewKeys.DetailInsights.Id);
        Assert.Equal("Detail.InsightsToggle", ViewKeys.DetailInsightsToggle.Id);
        Assert.Equal("Detail.HeroSkeleton", StyleKeys.DetailHero.Id);
    }

    [Fact]
    public void Classic_registers_every_detail_key_under_a_distinct_template_id()
    {
        var classic = ClassicTheme.Create();
        string[] ids =
        [
            ViewKeys.DetailFrame.Id, ViewKeys.DetailRail.Id, ViewKeys.DetailCompactRail.Id, ViewKeys.DetailShowHeader.Id,
            ViewKeys.DetailHero.Id, ViewKeys.DetailInsights.Id, ViewKeys.DetailInsightsToggle.Id,
        ];
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string template = classic.Registry.TemplateIdOf(id) ?? "";
            Assert.NotEqual("", template);
            Assert.StartsWith("classic.detail.", template, StringComparison.Ordinal);
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.True(classic.Registry.TryGetStyle(StyleKeys.DetailHero, out var style), "the hero's loading band is a registered style");
        Assert.NotNull(style.Skeleton);
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void The_frames_records_never_compare_equal_to_another_instance()
    {
        var spec = Spec();
        Action noop = static () => { };
        Func<bool> never = static () => false;

        DetailCompactRailProps Strip() => new() { Spec = spec, StripWidth = 96f, Expand = noop, Pending = never };
        var first = Strip();
        Assert.True(first.Equals(first));
        Assert.False(first.Equals(Strip()), "an equal-looking strip record is another render: the view re-runs");

        DetailShowHeaderProps Header() => new()
        {
            Spec = spec, Actions = spec.Actions, Accent = static () => default, Play = noop, PageWidth = float.NaN,
        };
        Assert.False(Header().Equals(Header()));
    }

    [Fact]
    public void The_hero_props_compare_by_data_and_the_reference_of_the_tables_pieces()
    {
        var spec = Spec();
        var accent = static () => default(FluentGpu.Foundation.ColorF);
        var vertical = new Detail.VerticalSpec(spec.Identity, spec.Config, spec.Actions, spec.Slots, accent);
        var selection = new Signal<bool>(false);
        var compact = new Signal<bool>(false);
        var searching = new Signal<bool>(false);

        Detail.HeroParts Parts(float width) => new(
            Toolbar: null, BandActions: new FluentGpu.Dsl.BoxEl(), SelectionBar: null, SelectionVisible: selection,
            CompactInteractive: compact, SearchField: null, SearchExpanded: searching, ColumnWidth: width, CompactLeft: 24f,
            HeroHeight: 300f, OnHeroMeasured: null);

        // the same band element: the table rebuilds its chrome on a re-render, and a rebuilt element is not the same props
        var parts = Parts(800f);
        Assert.Equal(new DetailHeroProps(vertical, parts), new DetailHeroProps(vertical, parts));
        Assert.NotEqual(new DetailHeroProps(vertical, parts), new DetailHeroProps(vertical, Parts(800f)));
        Assert.NotEqual(new DetailHeroProps(vertical, parts), new DetailHeroProps(vertical, parts with { ColumnWidth = 640f }));
        Assert.NotEqual(new DetailHeroProps(vertical, parts),
            new DetailHeroProps(new Detail.VerticalSpec(Spec("Another").Identity, spec.Config, spec.Actions, spec.Slots, accent), parts));
    }

    [Fact]
    public void The_insights_entry_points_are_one_toggle_in_two_forms()
    {
        var toggle = new Detail.InsightsToggle();
        var hero = new DetailInsightsToggleProps(toggle, DetailInsightsToggleForm.HeroButton);
        var band = new DetailInsightsToggleProps(toggle, DetailInsightsToggleForm.BandWord);
        Assert.NotEqual(hero, band);
        Assert.Same(hero.Toggle, band.Toggle);

        // the toggle the two forms share: closed at first, one click opens it, the band's word records the other opener
        Assert.False(toggle.Open.Peek());
        toggle.ToggleFromBand();
        Assert.True(toggle.Open.Peek());
        toggle.Close();
        Assert.False(toggle.Open.Peek());
    }
}
