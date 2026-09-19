// ── Wavee.Tests/EntitiesFakeConcertTests.cs — the concert seed's contract (Entities.Fake.Concert.cs) ─────────────────
//
// WP-5.N contract §8 and ch 31 §7.1's concerts row as facts: the tour arms (on tour now, upcoming, one date, three
// dates), the schedule shapes the page needs (a residency run, a month over the tile cap, a near-you MINORITY, a reach
// past five months), the saved + inferred places, ≥ 8 concepts, the default feed (≥ 2 sections, a continuation, a
// count), the full detail (≥ 2 offers with one unusable, a billing-only act, related shows), "nothing a concert page
// demands stays unseeded", and determinism. Everything drives the real `Entities.SeedFake` and reads HANDLES and the
// public rule values afterwards — no source text, no private seed arrays.

using FluentGpu.Foundation;
using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class EntitiesFakeConcertTests
{
    const long Now0 = 1_788_000_000;   // Platform.Clock.FixedSeedEpoch, restated so this file has no Platform dependency
    const long DayMs = 86_400_000;

    static void Seed(long now0 = Now0)
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(now0);
    }

    static string Ar(int i) => "spotify:artist:ar" + i.ToString(CultureInfo.InvariantCulture);
    static Artist ArtistOf(int i) => Entities.Artist(EntityUri.Parse(Ar(i)));
    static Concert ConcertOf(string uri) => Entities.Concert(EntityUri.Parse(uri));
    static ReadOnlySpan<int> Schedule(int artist) => Entities.Current.Edges.ArtistConcerts.Targets(ArtistOf(artist).Slot);
    static ConcertShow[] Shows(int artist) => ConcertShows.From(Schedule(artist));
    static string R(StringId id) => Entities.Strings.Resolve(id);

    [Fact]
    public void OnTourNow_ArtistHasDenseScheduleOverMonths_StartingTwoDaysOut()
    {
        Seed();
        var targets = Schedule(3);
        Assert.True(targets.Length >= 16, "ar3 has " + targets.Length + " dates");
        long first = new Concert(targets[0]).Date;
        Assert.InRange(first, (Now0 * 1000) + 2 * DayMs, (Now0 * 1000) + 3 * DayMs);
        Assert.True(ConcertScheduleShaping.GroupByMonth(Shows(3)).Count >= 3);
    }

    [Fact]
    public void OnTourNow_Schedule_HasAResidencyRun_AMonthOverTheTileCap_AndANearMinority()
    {
        Seed();
        var shows = ConcertSchedules.Chronological(Shows(3));
        var months = ConcertScheduleShaping.GroupByMonth(shows);

        Assert.Contains(months, m => m.Runs.Any(run => run.NightCount == 3));
        Assert.Contains(months, m => m.OverflowsCap);

        int near = shows.Count(s => s.IsNearUser);
        Assert.True(ConcertScheduleShaping.NearIsInformative(near, shows.Count), near + " of " + shows.Count + " are near");
        Assert.True(near * 2 < shows.Count);
    }

    [Fact]
    public void TourArms_UpcomingOneDateThreeDates_AndTheReachPastFiveMonths()
    {
        Seed();
        Assert.InRange(new Concert(Schedule(1)[0]).Date, (Now0 * 1000) + 21 * DayMs, (Now0 * 1000) + 22 * DayMs);
        Assert.Equal(1, Schedule(2).Length);
        Assert.Equal(3, Schedule(5).Length);

        foreach (int artist in new[] { 1, 3, 6, 7, 9, 10, 11 })
        {
            var targets = Schedule(artist);
            Assert.True(targets.Length > 0, Ar(artist) + " has no dates");
            Assert.True(new Concert(targets[^1]).Date >= (Now0 * 1000) + 150 * DayMs, Ar(artist) + " stops before +150 d");
        }
    }

    [Fact]
    public void EveryArtistSchedule_IsAnswered_EmptyForTheArtistsWithNone()
    {
        Seed();
        var edges = Entities.Current.Edges.ArtistConcerts;
        for (int i = 0; i < 12; i++)
            Assert.Equal(EdgeState.Complete, edges.State(ArtistOf(i).Slot));
        Assert.Equal(0, Schedule(0).Length);
        Assert.Equal(0, Schedule(4).Length);
        var noImage = Entities.Artist(EntityUri.Parse("spotify:artist:arnoimg"));
        Assert.Equal(EdgeState.Complete, edges.State(noImage.Slot));
    }

    [Fact]
    public void EverySeededShow_KnowsEverythingTheDetailPageDemands()
    {
        Seed();
        var e = Entities.Current.Edges;
        for (int artist = 0; artist < 12; artist++)
            foreach (int slot in Schedule(artist))
            {
                var c = new Concert(slot);
                Assert.True(c.Knows(ConcertFields.All), c.Uri.Text + " is missing a concert group");
                Assert.Equal(EdgeState.Complete, e.ConcertOffers.State(slot));
                Assert.Equal(EdgeState.Complete, e.ConcertLineup.State(slot));
                Assert.Equal(EdgeState.Complete, e.ConcertRelated.State(slot));
                Assert.True(c.Lineup.Length >= 1);
            }
    }

    [Fact]
    public void Places_SavedIsChosen_AndAnInferredRowExists()
    {
        Seed();
        var scope = Entities.Current;
        var saved = ConcertPlaces.From(scope.SavedPlace);
        Assert.NotNull(saved);
        Assert.Equal("London", saved!.Name);
        Assert.False(ConcertPlaces.IsInferred(scope.SavedPlace));
        Assert.NotEqual(0, scope.ArtistPagePlace);

        bool anyInferred = false;
        for (int slot = 1; slot < scope.Places.Count; slot++)
            anyInferred |= ConcertPlaces.IsInferred(slot) == true;
        Assert.True(anyInferred);
    }

    [Fact]
    public void SavedPlace_HasAtLeastEightConcepts_InWeightOrder()
    {
        Seed();
        var concepts = ConcertPlaces.ConceptsOf(Entities.Current.SavedPlace);
        Assert.True(concepts.Count >= 8);
        for (int i = 1; i < concepts.Count; i++) Assert.True(concepts[i - 1].Weight >= concepts[i].Weight);
        Assert.Equal(ConcertHub.TopConceptCount, ConcertHub.TopConcepts(concepts, Array.Empty<string>(), expanded: false).Count);
    }

    [Fact]
    public void DefaultFeed_HasSections_AContinuation_ACount_AndNoDuplicates()
    {
        Seed();
        var scope = Entities.Current;
        var place = ConcertPlaces.From(scope.SavedPlace);
        string key = ConcertFeedKey.For(ConcertFeedKey.PlaceKey(place), ConcertHub.DefaultRadiusKm, null, null);
        int feed = ConcertPlaces.FeedSlot(key);

        var row = scope.ConcertFeeds.Row[feed];
        Assert.False(row.PaginationKey.IsEmpty);
        Assert.True(row.Count > 0);

        var payload = scope.Edges.FeedSection.Payload(feed);
        var kinds = new HashSet<byte>();
        foreach (var p in payload) kinds.Add(p.Kind);
        Assert.True(kinds.Count >= 2);

        var targets = scope.Edges.FeedSection.Targets(feed).ToArray();
        Assert.Equal(targets.Length, targets.Distinct().Count());
        Assert.Contains(targets, t => new Concert(t).IsNearUser);
        Assert.True(scope.Edges.FeedSectionPlaylists.Count(feed) > 0);
        Assert.False(ConcertHub.IsFeedEmpty(targets, scope.Edges.FeedSectionPlaylists.Targets(feed)));
    }

    [Fact]
    public void Detail_HasOffersWithOneUnusable_ABillingOnlyAct_AndRelatedShows()
    {
        Seed();
        var detail = ConcertOf("spotify:concert:cc3n0");
        var offers = detail.Offers.ToArray();
        Assert.True(offers.Length >= 2);
        Assert.Contains(offers, o => ConcertOffers.ValidTicketUrl(R(o.Url)) is not null);
        Assert.Contains(offers, o => ConcertOffers.ValidTicketUrl(R(o.Url)) is null);

        var lineup = detail.Lineup.ToArray();
        var slots = detail.LineupSlots.ToArray();
        int billingOnly = Array.FindIndex(lineup, a => (a.Flags & LineupEdge.Navigable) == 0);
        Assert.True(billingOnly >= 0);
        Assert.Equal(Table.None, slots[billingOnly]);
        Assert.False(string.IsNullOrEmpty(R(lineup[billingOnly].BillingName)));

        Assert.True(detail.RelatedSlots.Length >= 1);
        Assert.Equal("Cancelled", ConcertDetailInfo.DisplayStatus(R(ConcertOf("spotify:concert:cc3n5").StatusTextId)));
    }

    [Fact]
    public void Seed_IsDeterministic()
    {
        Seed();
        var first = Shows(3).Select(s => (s.Uri, s.Date, s.Venue, s.City)).ToArray();
        Seed();
        var second = Shows(3).Select(s => (s.Uri, s.Date, s.Venue, s.City)).ToArray();
        Assert.Equal(first, second);

        Seed(Now0 + 86_400);
        Assert.Equal(first[0].Date.AddDays(1), Shows(3)[0].Date);   // every date is now0 + a constant
    }
}
