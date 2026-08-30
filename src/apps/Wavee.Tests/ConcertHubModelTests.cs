using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wavee.Core;
using Wavee.Features.Concerts;
using Xunit;

namespace Wavee.Tests;

public sealed class ConcertHubModelTests
{
    static ConcertConcept Concept(string uri) => new(uri, uri["spotify:concept:".Length..]);

    static Concert Show(string uri) =>
        new(uri, "Title", "Venue", "City", new DateTimeOffset(2030, 6, 1, 20, 0, 0, TimeSpan.Zero));

    // ── concept toggle (zero-or-one selection) ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void ToggleConcept_SelectsFromNoSelection() =>
        Assert.Equal("spotify:concept:pop", ConcertHub.ToggleConcept((string?)null, "spotify:concept:pop"));

    [Fact]
    public void ToggleConcept_SwitchesToADifferentConcept() =>
        Assert.Equal("spotify:concept:rock", ConcertHub.ToggleConcept("spotify:concept:pop", "spotify:concept:rock"));

    [Fact]
    public void ToggleConcept_TappingTheActiveConceptClears() =>
        Assert.Null(ConcertHub.ToggleConcept("spotify:concept:pop", "spotify:concept:pop"));

    // ── concept survival across a location change ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ReconcileConcept_KeepsTheSelectionWhileStillOffered()
    {
        var offered = new[] { Concept("spotify:concept:pop"), Concept("spotify:concept:rock") };
        Assert.Equal("spotify:concept:pop", ConcertHub.ReconcileConcept("spotify:concept:pop", offered));
    }

    [Fact]
    public void ReconcileConcept_ClearsAVanishedSelection()
    {
        var offered = new[] { Concept("spotify:concept:rock") };
        Assert.Null(ConcertHub.ReconcileConcept("spotify:concept:pop", offered));
        Assert.Null(ConcertHub.ReconcileConcept("spotify:concept:pop", Array.Empty<ConcertConcept>()));
    }

    [Fact]
    public void ReconcileConcept_NoSelectionStaysNoSelection() =>
        Assert.Null(ConcertHub.ReconcileConcept(null, new[] { Concept("spotify:concept:pop") }));

    [Theory]
    [InlineData("edm", "EDM")]
    [InlineData("r&b", "R&B")]
    [InlineData("hip hop", "Hip Hop")]
    [InlineData("electronic", "Electronic")]
    public void ConceptLabel_PresentsProviderLabelsAsUiCopy(string provider, string expected) =>
        Assert.Equal(expected, ConcertHub.ConceptLabel(provider));

    // ── location-button label ────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void LocationLabel_PromptsWithoutAResolvablePlace()
    {
        Assert.Equal("Set location", ConcertHub.LocationLabel(null, null));
        Assert.Equal("Set location", ConcertHub.LocationLabel(new ConcertPlace("1", "  "), inferred: true));
    }

    [Fact]
    public void LocationLabel_SavedPlaceIsThePlainName()
    {
        Assert.Equal("Berlin", ConcertHub.LocationLabel(new ConcertPlace("1", "Berlin"), inferred: false));
        Assert.Equal("Berlin", ConcertHub.LocationLabel(new ConcertPlace("1", "Berlin"), inferred: null));
    }

    [Fact]
    public void LocationLabel_InferredPlaceIsMarkedApproximate() =>
        Assert.Equal("Near Berlin (approximate)", ConcertHub.LocationLabel(new ConcertPlace("1", "Berlin"), inferred: true));

    // ── feed emptiness ───────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void IsFeedEmpty_NullPageAndEmptySectionsAreEmpty()
    {
        Assert.True(ConcertHub.IsFeedEmpty(null));
        Assert.True(ConcertHub.IsFeedEmpty(new ConcertFeedPage(Array.Empty<ConcertFeedSection>())));
        Assert.True(ConcertHub.IsFeedEmpty(new ConcertFeedPage(new[]
        {
            new ConcertFeedSection("a", ConcertFeedSectionKind.Nearby, Array.Empty<Concert>()),
        })));
    }

    [Fact]
    public void IsFeedEmpty_ConcertsOrPromotionsMakeThePageRenderable()
    {
        Assert.False(ConcertHub.IsFeedEmpty(new ConcertFeedPage(new[]
        {
            new ConcertFeedSection("a", ConcertFeedSectionKind.AllEvents, new[] { Show("spotify:concert:1") }),
        })));
        Assert.False(ConcertHub.IsFeedEmpty(new ConcertFeedPage(new[]
        {
            new ConcertFeedSection("a", ConcertFeedSectionKind.Recommended, Array.Empty<Concert>(),
                PlaylistPromotions: new[] { new PlaylistRef("spotify:playlist:1", "Promo", null, "Spotify") }),
        })));
    }

    // ── preset date windows (weekend = Fri–Sun) ──────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("2026-07-15")]   // Wednesday → the upcoming weekend
    [InlineData("2026-07-18")]   // Saturday  → the current weekend
    [InlineData("2026-07-19")]   // Sunday    → the current weekend
    public void PresetRange_ThisWeekendIsTheFriToSunContainingOrNext(string nowIso)
    {
        var now = DateTimeOffset.Parse(nowIso, CultureInfo.InvariantCulture);
        var range = ConcertHub.PresetRange(ConcertWhenKind.ThisWeekend, now);
        Assert.Equal(new DateOnly(2026, 7, 17), range!.From);
        Assert.Equal(new DateOnly(2026, 7, 19), range.To);
    }

    [Fact]
    public void PresetRange_NextWeekendIsTheWeekendAfterThisOne()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);   // Wednesday
        var range = ConcertHub.PresetRange(ConcertWhenKind.NextWeekend, now);
        Assert.Equal(new DateOnly(2026, 7, 24), range!.From);
        Assert.Equal(new DateOnly(2026, 7, 26), range.To);
    }

    [Fact]
    public void PresetRange_TodayIsASingleDayAndAnyOrCustomCarryNoWindow()
    {
        var now = new DateTimeOffset(2026, 7, 15, 23, 0, 0, TimeSpan.Zero);
        var today = ConcertHub.PresetRange(ConcertWhenKind.Today, now);
        Assert.Equal(new DateOnly(2026, 7, 15), today!.From);
        Assert.Equal(new DateOnly(2026, 7, 15), today.To);
        Assert.Null(ConcertHub.PresetRange(ConcertWhenKind.Any, now));
        Assert.Null(ConcertHub.PresetRange(ConcertWhenKind.Custom, now));
    }

    // ── multi-select concept toggle ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ToggleConcept_MultiAppendsWhenAbsentAndRemovesWhenPresent()
    {
        IReadOnlyList<string> selected = Array.Empty<string>();
        selected = ConcertHub.ToggleConcept(selected, "spotify:concept:edm");
        Assert.Equal(new[] { "spotify:concept:edm" }, selected);

        selected = ConcertHub.ToggleConcept(selected, "spotify:concept:pop");
        Assert.Equal(new[] { "spotify:concept:edm", "spotify:concept:pop" }, selected);

        selected = ConcertHub.ToggleConcept(selected, "spotify:concept:edm");
        Assert.Equal(new[] { "spotify:concept:pop" }, selected);
    }

    // ── rest-state token selection (top-3 + selected survivors) ──────────────────────────────────────────────────────
    [Fact]
    public void TopConcepts_TakesTheHeadThenAppendsSelectedOutsideIt()
    {
        var all = new[]
        {
            Concept("spotify:concept:a"), Concept("spotify:concept:b"), Concept("spotify:concept:c"),
            Concept("spotify:concept:d"), Concept("spotify:concept:e"),
        };
        var selected = new[] { "spotify:concept:e" };

        var rest = ConcertHub.TopConcepts(all, selected, expanded: false);
        Assert.Equal(
            new[] { "spotify:concept:a", "spotify:concept:b", "spotify:concept:c", "spotify:concept:e" },
            rest.Select(x => x.Uri));

        // A selection already inside the head is not duplicated.
        Assert.Equal(
            new[] { "spotify:concept:a", "spotify:concept:b", "spotify:concept:c" },
            ConcertHub.TopConcepts(all, new[] { "spotify:concept:b" }, expanded: false).Select(x => x.Uri));

        Assert.Equal(5, ConcertHub.TopConcepts(all, selected, expanded: true).Count);
    }

    // ── fused-pill range caption ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void WhenLabel_FormatsSameMonthCrossMonthAndSingleDay()
    {
        var culture = CultureInfo.InvariantCulture;
        Assert.Equal("Jul 17 – 19",
            ConcertHub.WhenLabel(new ConcertDateRange(new DateOnly(2026, 7, 17), new DateOnly(2026, 7, 19)), culture));
        Assert.Equal("Jul 31 – Aug 2",
            ConcertHub.WhenLabel(new ConcertDateRange(new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 2)), culture));
        Assert.Equal("Jul 14",
            ConcertHub.WhenLabel(new ConcertDateRange(new DateOnly(2026, 7, 14), new DateOnly(2026, 7, 14)), culture));
    }
}
