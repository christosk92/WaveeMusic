using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Pal;
using Wavee.Core;
using Wavee.Features.Concerts;
using Xunit;

namespace Wavee.Tests;

public sealed class ConcertSchedulePageTests
{
    static Concert Show(string uri, int year, int month, int day, string venue = "Venue", string city = "City") =>
        new(uri, null, venue, city, new DateTimeOffset(year, month, day, 20, 0, 0, TimeSpan.Zero));

    static Concert LineupShow(string uri, string venue, string city, string? title, params string[] artistNames)
    {
        var artists = new ConcertArtist[artistNames.Length];
        for (int i = 0; i < artistNames.Length; i++) artists[i] = new ConcertArtist(artistNames[i]);
        return new Concert(uri, title, venue, city, new DateTimeOffset(2026, 9, 21, 19, 30, 0, TimeSpan.Zero),
            Artists: artists.Length > 0 ? artists : null);
    }

    // ── chronological sorting / dedup ────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Chronological_OrdersEarliestFirst()
    {
        var input = new[]
        {
            Show("c:3", 2025, 6, 1),
            Show("c:1", 2025, 1, 15),
            Show("c:2", 2025, 3, 20),
        };

        var sorted = ConcertSchedules.Chronological(input);

        Assert.Equal(new[] { "c:1", "c:2", "c:3" }, System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.Select(sorted, c => c.Uri)));
    }

    [Fact]
    public void Chronological_DedupesByCanonicalUri_KeepingFirst()
    {
        var input = new[]
        {
            Show("c:1", 2025, 1, 1, venue: "First"),
            Show("c:1", 2025, 2, 1, venue: "Duplicate"),
            Show("c:2", 2025, 3, 1),
        };

        var sorted = ConcertSchedules.Chronological(input);

        Assert.Equal(2, sorted.Count);
        Assert.Equal("First", sorted[0].Venue);   // the first occurrence survives the dedup
    }

    [Fact]
    public void Chronological_NullOrEmpty_IsEmpty()
    {
        Assert.Empty(ConcertSchedules.Chronological(null));
        Assert.Empty(ConcertSchedules.Chronological(Array.Empty<Concert>()));
    }

    // ── geolocation status → human message ───────────────────────────────────────────────────────────────────────────
    [Fact]
    public void LocationErrors_MapEachFailureToADistinctMessage()
    {
        string? denied = LocationErrors.ForStatus(GeolocationStatus.PermissionDenied);
        string? unavailable = LocationErrors.ForStatus(GeolocationStatus.Unavailable);
        string? timedOut = LocationErrors.ForStatus(GeolocationStatus.TimedOut);
        string? failed = LocationErrors.ForStatus(GeolocationStatus.Failed);

        Assert.False(string.IsNullOrWhiteSpace(denied));
        Assert.False(string.IsNullOrWhiteSpace(unavailable));
        Assert.False(string.IsNullOrWhiteSpace(timedOut));
        Assert.False(string.IsNullOrWhiteSpace(failed));

        var distinct = new HashSet<string> { denied!, unavailable!, timedOut!, failed! };
        Assert.Equal(4, distinct.Count);
    }

    [Fact]
    public void LocationErrors_SuccessAndCancel_HaveNoMessage()
    {
        Assert.Null(LocationErrors.ForStatus(GeolocationStatus.Success));
        Assert.Null(LocationErrors.ForStatus(GeolocationStatus.Canceled));
    }

    // ── schedule breakpoint hysteresis ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ScheduleWide_EntersAtEnterThreshold_LeavesBelowLeaveThreshold()
    {
        Assert.True(ConcertLayout.ScheduleWide(760f, wasWide: false, initialized: true));    // hits enter → wide
        Assert.False(ConcertLayout.ScheduleWide(759f, wasWide: false, initialized: true));   // below enter → narrow
        Assert.True(ConcertLayout.ScheduleWide(720f, wasWide: true, initialized: true));      // in the band, was wide → stays
        Assert.False(ConcertLayout.ScheduleWide(719f, wasWide: true, initialized: true));     // below leave → narrow
        Assert.False(ConcertLayout.ScheduleWide(740f, wasWide: false, initialized: true));    // in the band, was narrow → stays
    }

    // ── R3 rework: month grouping ────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void GroupByMonth_SplitsAcrossYearBoundary_DecThenJan()
    {
        var input = new[]
        {
            Show("c:1", 2025, 12, 10),
            Show("c:2", 2025, 12, 20),
            Show("c:3", 2026, 1, 5),
        };

        var groups = ConcertScheduleShaping.GroupByMonth(input);

        Assert.Equal(2, groups.Count);
        Assert.Equal("2025-12", groups[0].Key);
        Assert.Equal("2026-01", groups[1].Key);
        Assert.Equal(2, groups[0].ShowCount);
        Assert.Equal(1, groups[1].ShowCount);
    }

    // ── R3 rework: run collapse ──────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void DetectRuns_MergesConsecutiveNightsAtSameVenue()
    {
        var input = new[]
        {
            Show("c:1", 2026, 9, 21, venue: "Arena", city: "Berlin"),
            Show("c:2", 2026, 9, 22, venue: "Arena", city: "Berlin"),
            Show("c:3", 2026, 9, 23, venue: "Arena", city: "Berlin"),
        };

        var runs = ConcertScheduleShaping.DetectRuns(input);

        Assert.Single(runs);
        Assert.True(runs[0].IsMultiNight);
        Assert.Equal(3, runs[0].NightCount);
        Assert.Equal("c:1", runs[0].Uri);   // navigation identity = first night
    }

    [Fact]
    public void DetectRuns_GapBreaksTheRun()
    {
        var input = new[]
        {
            Show("c:1", 2026, 9, 21, venue: "Arena", city: "Berlin"),
            Show("c:2", 2026, 9, 22, venue: "Arena", city: "Berlin"),
            Show("c:3", 2026, 9, 24, venue: "Arena", city: "Berlin"),   // one-day gap → new run
        };

        var runs = ConcertScheduleShaping.DetectRuns(input);

        Assert.Equal(2, runs.Count);
        Assert.Equal(2, runs[0].NightCount);
        Assert.False(runs[1].IsMultiNight);
    }

    [Fact]
    public void DetectRuns_DifferentVenueBreaksTheRun_EvenWhenConsecutive()
    {
        var input = new[]
        {
            Show("c:1", 2026, 9, 21, venue: "Arena", city: "Berlin"),
            Show("c:2", 2026, 9, 22, venue: "Club", city: "Berlin"),   // consecutive day, different venue
        };

        var runs = ConcertScheduleShaping.DetectRuns(input);

        Assert.Equal(2, runs.Count);
        Assert.False(runs[0].IsMultiNight);
        Assert.False(runs[1].IsMultiNight);
    }

    // ── R3 rework: 6-tile cap boundary ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void CapBoundary_SixTilesFit_SeventhOverflows()
    {
        Concert[] Month(int count)
        {
            var xs = new Concert[count];
            for (int i = 0; i < count; i++)
                // distinct venues + a two-day gap between each → every concert is its own single tile (no run collapse)
                xs[i] = Show("c:" + i, 2026, 9, 1 + i * 2, venue: "Venue" + i);
            return xs;
        }

        var six = ConcertScheduleShaping.GroupByMonth(Month(6));
        var seven = ConcertScheduleShaping.GroupByMonth(Month(7));

        Assert.Equal(6, six[0].Runs.Count);
        Assert.False(six[0].OverflowsCap);
        Assert.Equal(7, seven[0].Runs.Count);
        Assert.True(seven[0].OverflowsCap);
    }

    // ── R3 rework: hero stats ────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Stats_CountsDistinctCities_AndOmitsCitiesSegmentWhenAllEmpty()
    {
        var withCities = new[]
        {
            Show("c:1", 2026, 3, 1, city: "Berlin"),
            Show("c:2", 2026, 4, 1, city: "berlin"),   // same city, different case → one distinct
            Show("c:3", 2026, 5, 1, city: "Munich"),
        };
        var s = ConcertScheduleShaping.Stats(withCities);
        Assert.Equal(3, s.Shows);
        Assert.Equal(2, s.Cities);
        Assert.True(s.HasCities);
        Assert.Contains("2 cities", ConcertScheduleShaping.StatsLine(s));

        var noCities = new[]
        {
            Show("c:4", 2026, 3, 1, city: ""),
            Show("c:5", 2026, 3, 20, city: "  "),
        };
        var e = ConcertScheduleShaping.Stats(noCities);
        Assert.False(e.HasCities);
        string line = ConcertScheduleShaping.StatsLine(e);
        Assert.DoesNotContain("cities", line);
        Assert.DoesNotContain("city", line);
        Assert.StartsWith("2 shows · ", line);   // both shows in one month → single-month "MMM yyyy" span, no cities segment
        string march = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(3);
        Assert.Contains(march + " 2026", line);
    }

    // ── R3 rework: relative time ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void RelativeTime_DaysWeeksAndTomorrow()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        Concert At(int m, int d) => Show("x", 2026, m, d);

        Assert.Equal("tomorrow", ConcertScheduleShaping.RelativeTime(At(9, 2).Date, now));
        Assert.Equal("in 5 days", ConcertScheduleShaping.RelativeTime(At(9, 6).Date, now));
        Assert.Equal("in 6 weeks", ConcertScheduleShaping.RelativeTime(At(10, 13).Date, now));   // 42 days
        Assert.Equal("today", ConcertScheduleShaping.RelativeTime(At(9, 1).Date, now));
    }

    // ── R3 rework: spotlight selection + board exclusion ─────────────────────────────────────────────────────────────
    [Fact]
    public void Spotlight_PicksFirstUpcoming_AndIsExcludedFromBoards()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var chrono = ConcertSchedules.Chronological(new[]
        {
            Show("past", 2026, 8, 1),
            Show("next", 2026, 9, 10),
            Show("later", 2026, 10, 5),
        });

        var spot = ConcertScheduleShaping.Spotlight(chrono, now);
        Assert.NotNull(spot);
        Assert.Equal("next", spot!.Uri);

        var board = ConcertScheduleShaping.BoardConcerts(chrono, spot);
        Assert.DoesNotContain(board, c => c.Uri == "next");   // never appears twice
        Assert.Contains(board, c => c.Uri == "past");
        Assert.Contains(board, c => c.Uri == "later");
    }

    [Fact]
    public void Spotlight_AllPast_NoSpotlight_BoardsShowEverything()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var chrono = ConcertSchedules.Chronological(new[]
        {
            Show("a", 2026, 1, 1),
            Show("b", 2026, 2, 1),
        });

        var spot = ConcertScheduleShaping.Spotlight(chrono, now);
        Assert.Null(spot);
        Assert.Equal(2, ConcertScheduleShaping.BoardConcerts(chrono, spot).Count);
    }

    // ── R3.1 polish: near-you informativeness guard ──────────────────────────────────────────────────────────────────
    [Fact]
    public void NearIsInformative_MinoritySetsOnly()
    {
        // 18 shows → threshold max(3, ceil(18/3)) = 6
        Assert.False(ConcertScheduleShaping.NearIsInformative(18, 18));   // near-everything = information-free
        Assert.False(ConcertScheduleShaping.NearIsInformative(7, 18));    // just over a third → off
        Assert.True(ConcertScheduleShaping.NearIsInformative(6, 18));     // boundary → on
        Assert.True(ConcertScheduleShaping.NearIsInformative(1, 18));
        Assert.False(ConcertScheduleShaping.NearIsInformative(0, 18));    // empty → off
        // Small tour: the floor of 3 keeps a 2-of-4 near set usable.
        Assert.True(ConcertScheduleShaping.NearIsInformative(3, 4));
        Assert.False(ConcertScheduleShaping.NearIsInformative(4, 4));
    }

    [Fact]
    public void GuardedNearSet_EmptiesWhenNearCoversTheTour()
    {
        var board = new Concert[12];
        for (int i = 0; i < 12; i++) board[i] = Show("c:" + i, 2026, 9, 1 + i);

        var allNear = ConcertScheduleShaping.GuardedNearSet(board, board);   // Nearby branch = ALL shows
        Assert.Empty(allNear);   // the blue wall never happens

        var fewNear = ConcertScheduleShaping.GuardedNearSet(board, new[] { board[0], board[1] });
        Assert.Equal(2, fewNear.Count);
        Assert.Contains("c:0", fewNear);
    }

    [Fact]
    public void GuardedNearSet_IncludesIsNearUserFlags_InCountAndSet()
    {
        var board = new[]
        {
            Show("c:0", 2026, 9, 1) with { IsNearUser = true },
            Show("c:1", 2026, 9, 3),
            Show("c:2", 2026, 9, 5),
            Show("c:3", 2026, 9, 7),
        };

        var set = ConcertScheduleShaping.GuardedNearSet(board, nearby: null);
        Assert.Single(set);
        Assert.Contains("c:0", set);
    }

    // ── R3.1 polish: tile text (venue-less events must not repeat the page artist's name) ───────────────────────────
    [Fact]
    public void TileText_VenuePrimary_CityAndTimeSecondary()
    {
        var t = ConcertScheduleShaping.TileText(LineupShow("c:1", "Arena", "Berlin", "Artist Name"), "Artist Name");
        Assert.Equal("Arena", t.Primary);
        Assert.False(t.CityIsPrimary);
        Assert.StartsWith("Berlin · ", t.Secondary);
    }

    [Fact]
    public void TileText_NoVenue_CityIsPrimary_SupportActsInSecondary()
    {
        var t = ConcertScheduleShaping.TileText(
            LineupShow("c:1", "", "Berlin", "Artist Name", "Artist Name", "Band of Silver"), "Artist Name");
        Assert.Equal("Berlin", t.Primary);
        Assert.True(t.CityIsPrimary);
        Assert.Contains("with Band of Silver", t.Secondary);
        Assert.DoesNotContain("Artist Name", t.Secondary);   // the page artist is never a "support act"
    }

    [Fact]
    public void TileText_TitleIsPrimaryOnlyWhenVenueAndCityBothEmpty()
    {
        var t = ConcertScheduleShaping.TileText(LineupShow("c:1", "", "", "Festival Special"), "Artist Name");
        Assert.Equal("Festival Special", t.Primary);
        Assert.False(t.CityIsPrimary);
    }

    [Fact]
    public void SupportActs_ExcludesPageArtistCaseInsensitive_CapsAtTwoNames()
    {
        var many = LineupShow("c:1", "", "Berlin", null, "ARTIST NAME", "One", "Two", "Three", "Four");
        Assert.Equal("with One, Two +2 more", ConcertScheduleShaping.SupportActs(many, "Artist Name"));

        var solo = LineupShow("c:2", "", "Berlin", null, "Artist Name");
        Assert.Null(ConcertScheduleShaping.SupportActs(solo, "Artist Name"));   // nothing informative left

        Assert.Null(ConcertScheduleShaping.SupportActs(LineupShow("c:3", "", "Berlin", null), "Artist Name"));
    }

    // ── R3.1 polish: redundant cities segment ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void StatsLine_OmitsCities_WhenRedundantOrSingle()
    {
        // 3 shows in 3 distinct cities → cities == shows → redundant, omitted
        var allDistinct = ConcertScheduleShaping.Stats(new[]
        {
            Show("c:1", 2026, 3, 1, city: "A"),
            Show("c:2", 2026, 4, 1, city: "B"),
            Show("c:3", 2026, 5, 1, city: "C"),
        });
        Assert.DoesNotContain("cities", ConcertScheduleShaping.StatsLine(allDistinct));

        // 5 shows all in one city → single city is noise, omitted
        var oneCity = ConcertScheduleShaping.Stats(new[]
        {
            Show("c:1", 2026, 3, 1, city: "A"), Show("c:2", 2026, 3, 8, city: "A"),
            Show("c:3", 2026, 3, 15, city: "A"), Show("c:4", 2026, 3, 22, city: "A"),
            Show("c:5", 2026, 3, 29, city: "A"),
        });
        string line = ConcertScheduleShaping.StatsLine(oneCity);
        Assert.DoesNotContain("city", line);
        Assert.DoesNotContain("cities", line);

        // 3 shows across 2 cities → informative, included
        var informative = ConcertScheduleShaping.Stats(new[]
        {
            Show("c:1", 2026, 3, 1, city: "A"),
            Show("c:2", 2026, 4, 1, city: "A"),
            Show("c:3", 2026, 5, 1, city: "B"),
        });
        Assert.Contains("2 cities", ConcertScheduleShaping.StatsLine(informative));
    }

    // ── null-service behaviour ───────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task NullConcertService_ReadsAreNullOrEmpty_AndSaveIsHonestFalse()
    {
        IConcertService svc = new NullConcertService();

        Assert.Null(await svc.GetArtistScheduleAsync("spotify:artist:x"));
        Assert.Null(await svc.GetFeedAsync(new ConcertFeedQuery()));
        Assert.Null(await svc.GetDetailsAsync("spotify:concert:x"));
        Assert.Null(await svc.GetArtistPageLocationAsync());
        Assert.Empty(await svc.GetConceptsAsync("geohash"));
        Assert.Empty(await svc.SearchLocationsAsync("berlin"));
        Assert.Empty(await svc.ReverseLocationAsync(new GeoCoordinates(52.5, 13.4)));
        Assert.False(await svc.SaveLocationAsync("place-id"));   // no silent success when nothing is wired
    }

    [Fact]
    public async Task SwitchableConcertService_ForwardsToTheCurrentInner()
    {
        var switchable = new SwitchableConcertService(new NullConcertService());
        Assert.Null(await switchable.GetArtistScheduleAsync("spotify:artist:x"));   // Null inner

        var schedule = new ArtistConcertSchedule(
            new ArtistRef("id", "spotify:artist:x", "Artist"), HeaderImage: null,
            Concerts: new[] { Show("c:1", 2025, 5, 1) });
        switchable.SetInner(new StubConcertService(schedule));

        var result = await switchable.GetArtistScheduleAsync("spotify:artist:x");
        Assert.NotNull(result);
        Assert.Single(result!.Concerts);

        switchable.SetInner(new NullConcertService());   // reset (the GoOffline path)
        Assert.Null(await switchable.GetArtistScheduleAsync("spotify:artist:x"));
    }

    [Fact]
    public void SwitchableConcertService_RejectsNullInner()
    {
        var switchable = new SwitchableConcertService(new NullConcertService());
        Assert.Throws<ArgumentNullException>(() => switchable.SetInner(null!));
    }

    // A minimal live-style adapter that only answers GetArtistScheduleAsync; every other member defers to Null semantics.
    sealed class StubConcertService : IConcertService
    {
        readonly ArtistConcertSchedule _schedule;
        readonly NullConcertService _rest = new();
        public StubConcertService(ArtistConcertSchedule schedule) => _schedule = schedule;

        public Task<ArtistConcertSchedule?> GetArtistScheduleAsync(string artistUri, string? geoHash = null,
            bool includeNearby = true, CancellationToken cancellationToken = default) =>
            Task.FromResult<ArtistConcertSchedule?>(_schedule);

        public Task<IReadOnlyList<ConcertConcept>> GetConceptsAsync(string geoHash, string? selectedConceptUri = null,
            CancellationToken cancellationToken = default) => _rest.GetConceptsAsync(geoHash, selectedConceptUri, cancellationToken);
        public Task<ConcertFeedPage?> GetFeedAsync(ConcertFeedQuery query, CancellationToken cancellationToken = default) =>
            _rest.GetFeedAsync(query, cancellationToken);
        public Task<int?> GetFeedCountAsync(ConcertFeedQuery query, CancellationToken cancellationToken = default) =>
            _rest.GetFeedCountAsync(query, cancellationToken);
        public Task<ConcertDetails?> GetDetailsAsync(string concertUri, bool authenticated = true,
            CancellationToken cancellationToken = default) => _rest.GetDetailsAsync(concertUri, authenticated, cancellationToken);
        public Task<ConcertPlace?> GetUserLocationAsync(CancellationToken cancellationToken = default) =>
            _rest.GetUserLocationAsync(cancellationToken);
        public Task<ConcertPlace?> GetArtistPageLocationAsync(CancellationToken cancellationToken = default) =>
            _rest.GetArtistPageLocationAsync(cancellationToken);
        public Task<bool?> IsUserLocationInferredAsync(CancellationToken cancellationToken = default) =>
            _rest.IsUserLocationInferredAsync(cancellationToken);
        public Task<IReadOnlyList<ConcertPlace>> SearchLocationsAsync(string query, CancellationToken cancellationToken = default) =>
            _rest.SearchLocationsAsync(query, cancellationToken);
        public Task<IReadOnlyList<ConcertPlace>> ReverseLocationAsync(GeoCoordinates coordinates, CancellationToken cancellationToken = default) =>
            _rest.ReverseLocationAsync(coordinates, cancellationToken);
        public Task<ConcertLocationSnapshot?> GetLocationDetailsAsync(string? placeId, bool isAnonymous = false,
            CancellationToken cancellationToken = default) => _rest.GetLocationDetailsAsync(placeId, isAnonymous, cancellationToken);
        public Task<bool> SaveLocationAsync(string placeId, CancellationToken cancellationToken = default) =>
            _rest.SaveLocationAsync(placeId, cancellationToken);
    }
}
