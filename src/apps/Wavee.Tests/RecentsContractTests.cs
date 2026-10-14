// ── Wavee.Tests/RecentsContractTests.cs — the Recents page's published key and Classic's registration (themes plan 6.0, T10c2) ──────────────────
//
// The Recents page is the structure's picture since T10c2; `Recents.PageView` keeps the demand, the shape and its atomic publication, the midnight rollover, the
// accordion's state, the sticky band's arithmetic, the accent and its wash and every verb. What is pure about the contract: the id (a published id never
// changes), Classic registering the key under its own template id with a complete registry, the props comparing by reference (the host rebuilds one per render,
// so a data-equal record must not let the view skip the render and keep older Funcs), and the facts a row reads before its target has an identity. The grouping,
// the time buckets, the pivots and the layout arithmetic are the existing `RecentsView` / `RecentsLayout` tests.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class RecentsContractTests
{
    [Fact]
    public void The_published_id_is_the_plans()
        => Assert.Equal("Pages.Recents", ViewKeys.RecentsPage.Id);

    [Fact]
    public void Classic_registers_the_recents_page_under_its_own_template_id()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("classic.recents.page", classic.Registry.TemplateIdOf(ViewKeys.RecentsPage.Id));
        Assert.NotEqual(classic.Registry.TemplateIdOf(ViewKeys.LibraryPage.Id), classic.Registry.TemplateIdOf(ViewKeys.RecentsPage.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void Unknown_facts_carry_no_invented_strings()
    {
        var unknown = RecentsFacts.Unknown;
        Assert.False(unknown.Known);
        Assert.Equal("", unknown.Title);
        Assert.Null(unknown.Subtitle);
        Assert.Null(unknown.Cover);
    }

    [Fact]
    public void A_shape_built_from_the_pending_seed_is_not_loaded_and_keeps_its_rows_parallel()
    {
        var now = DateTimeOffset.Now;
        var seed = RecentsView.PendingSeedRows(now);
        var map = RecentsView.Filter(seed, null);
        var sections = RecentsView.BuildSections(seed, map, now, System.Globalization.CultureInfo.CurrentCulture);
        var layout = new FluentGpu.Scene.GroupedListVirtualLayout(sections.HeaderIndices, RecentsLayout.DateHeaderHeight, RecentsLayout.RowHeight);
        var shape = new RecentsShape(seed, map, RecentsView.FirstOccurrence(seed), sections,
            RecentsView.DayDensity(seed, map, now, System.Globalization.CultureInfo.CurrentCulture), layout, now.Offset, loaded: false, summary: "");

        Assert.False(shape.Loaded);
        Assert.Equal("", shape.Summary);
        Assert.Equal(seed.Count, shape.Morphable.Length);
        Assert.Equal(sections.Items.Length, shape.Sections.Items.Length);
    }
}
