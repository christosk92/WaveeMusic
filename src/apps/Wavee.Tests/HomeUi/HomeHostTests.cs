// ── Wavee.Tests/HomeUi/HomeHostTests.cs — the Home page's facet host (C16, X43) ──────────────────────────────────────
//
// `HomeHost` owns the facet machine's state and the bodies of the effects `HomeScreen` registers. The transitions
// themselves are pinned in FacetSwitchTests / FacetCacheTests / FacetPivotTests; these facts pin the host's own promises
// without mounting anything: the row props are ONE record whose signals are the host's, the shown/words/shape record, the
// selector-index sync, and that every effect body is a no-op on the edges it must ignore (a settled switch, the target
// already selected).

using FluentGpu.Scroll.Runtime;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public sealed class HomeHostTests
{
    public HomeHostTests() => TestScope.Fresh();

    static readonly ChipInput Music = new("music-chip", "Music", "music-following-chip", "Following");
    static readonly ChipInput Podcasts = new("podcasts-chip", "Podcasts", null, null);

    static HomeHost NewHost() => new(new ScrollHandle());

    static ScreenModel Model(string facet, ChipInput[] chips, params ZoneKind[] kinds)
    {
        var zones = new Zone[kinds.Length];
        for (int i = 0; i < kinds.Length; i++) zones[i] = new Zone(kinds[i], "z" + i, null, null, Array.Empty<HomeCard>());
        return new ScreenModel(facet, zones, "", chips);
    }

    // ── the row props ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_row_props_are_one_record_over_the_hosts_own_signals()
    {
        var host = NewHost();
        var props = host.RowProps;

        Assert.Same(props, host.RowProps);
        Assert.Same(host.Words, props.Words);
        Assert.Same(host.Switch, props.Switch);
        Assert.Same(host.FacetIndex, props.Index);
    }

    [Fact]
    public void Following_memory_starts_empty()
        => Assert.False(NewHost().RowProps.FollowingMemory("music-chip"));

    [Fact]
    public void Dismissing_the_failure_closes_the_info_bar_and_keeps_the_published_facet()
    {
        var host = NewHost();
        host.Switch.Value = new FacetSwitchState(FacetPhase.Failed, "music-chip", "music-chip", StartedAtMs: 0, FailedTarget: "podcasts-chip");

        host.RowProps.Dismiss();

        Assert.Equal(FacetPhase.Idle, host.Switch.Peek().Phase);
        Assert.Equal("music-chip", host.Switch.Peek().Published);
        Assert.Null(host.Switch.Peek().FailedTarget);
    }

    // ── the shown / words / shape record ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_model_that_has_not_landed_records_nothing()
    {
        var host = NewHost();

        host.RecordShown(null);

        Assert.Empty(host.Words.Peek());
        Assert.False(host.TryLastShape("", out _));
    }

    [Fact]
    public void A_landed_model_publishes_the_pivot_words_and_remembers_its_zone_shape()
    {
        var host = NewHost();

        host.RecordShown(Model("music-chip", [Music, Podcasts], ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.CoverShelf));

        var words = host.Words.Peek();
        Assert.Equal(3, words.Length);                                            // All, Music, Podcasts
        Assert.Equal("", words[0].Id);
        Assert.Equal("music-chip", words[1].Id);
        Assert.Equal("podcasts-chip", words[2].Id);
        Assert.True(host.TryLastShape("music-chip", out var kinds));
        Assert.Equal(new[] { ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.CoverShelf }, kinds);
        Assert.False(host.TryLastShape("podcasts-chip", out _));                  // only a facet that was shown has a shape
    }

    [Fact]
    public void The_shape_is_per_facet_and_the_latest_model_wins()
    {
        var host = NewHost();

        host.RecordShown(Model("", [Music], ZoneKind.Daylist));
        host.RecordShown(Model("music-chip", [Music], ZoneKind.RecentGrid));
        host.RecordShown(Model("", [Music], ZoneKind.CoverShelf));

        Assert.True(host.TryLastShape("", out var all));
        Assert.Equal(new[] { ZoneKind.CoverShelf }, all);
        Assert.True(host.TryLastShape("music-chip", out var music));
        Assert.Equal(new[] { ZoneKind.RecentGrid }, music);
    }

    // ── the selector index ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_selector_index_follows_the_switch_target()
    {
        var host = NewHost();
        host.RecordShown(Model("", [Music, Podcasts], ZoneKind.RecentGrid));

        host.Switch.Value = FacetSwitchState.Initial("podcasts-chip");
        host.SyncFacetIndex();
        Assert.Equal(2, host.FacetIndex.Peek());

        host.Switch.Value = FacetSwitchState.Initial("music-following-chip");     // a Following sub-chip selects its word
        host.SyncFacetIndex();
        Assert.Equal(1, host.FacetIndex.Peek());

        host.Switch.Value = FacetSwitchState.Initial("");
        host.SyncFacetIndex();
        Assert.Equal(0, host.FacetIndex.Peek());
    }

    [Fact]
    public void A_selector_click_the_machine_refused_snaps_back_to_the_target()
    {
        var host = NewHost();
        host.RecordShown(Model("", [Music, Podcasts], ZoneKind.RecentGrid));
        host.Switch.Value = FacetSwitchState.Initial("music-chip");
        host.FacetIndex.Value = 2;                                                // the bar wrote a click the machine ignored

        host.SyncFacetIndex();

        Assert.Equal(1, host.FacetIndex.Peek());
    }

    // ── the edges every effect ignores ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_landing_edge_ignores_a_settled_switch()
    {
        var host = NewHost();
        var before = host.Switch.Peek();

        host.Land(tab: 0);

        Assert.Equal(before, host.Switch.Peek());
    }

    [Fact]
    public void Selecting_the_facet_that_is_already_the_target_does_nothing()
    {
        var host = NewHost();
        var before = host.Switch.Peek();

        host.Select("", tab: 0);

        Assert.Equal(before, host.Switch.Peek());
    }

    [Fact]
    public void A_route_arg_naming_no_known_facet_resolves_to_All_and_selects_nothing()
    {
        var host = NewHost();
        var before = host.Switch.Peek();

        host.ResolveRouteArg(default, tab: 0);                                    // plain `home`
        host.ResolveRouteArg(Entities.Strings.Intern("bogus-chip"), tab: 0);     // a stale deep link, no words yet

        Assert.Equal(before, host.Switch.Peek());
    }
}
