// ── Entities/Entities.Fake.Concert.cs ───────────────────────────────────────────────────────────────────────────────
// the concert half of the offline seed: artist schedules, feed-only shows, one full detail, places, concepts, the
// default hub feed subject
//
// Role: CORE
// Owner: N (stream N-C)
// Wave: 5
// Budget: ~550 (new seed partial, WP-5.N contract §0)
// Spec: ch 31 §2 W16 (concert dates, the tour arms), §7.1 (the concerts row: "a saved place and an inferred place, ≥ 8
//   concepts, a feed with ≥ 2 sections + a pagination key + a count, an artist schedule spanning ≥ 3 months, one detail
//   with ≥ 2 offers and a lineup including a URI-less billing name"), §10 item 31; WP-5.N contract §8
//
// THE ONE RULE (ch 31 §0.1): rows, places, concepts, the feed subject and every concert relation go through `Staging` +
// `Entities.Commit` — the same commit a live answer lands through — at `Authority.Seed`, with Known bits that say what
// the fixture filled. Nothing publishes (`SeedFake` publishes once). Every value is a pure function of a fixture index
// and `now0` (unix SECONDS), and every date is `now0` + a constant: no clock, no `Random`, no `GetHashCode`.
//
// SHAPE (contract §8):
//   ar3   ON TOUR NOW — a show at now0 + 2 d, a 3-night residency (London, nights 11-13), a DENSE block of one show every
//         2 days from +16 to +80 (any calendar month inside it holds ≥ 14 tiles, so "Show all" appears whatever now0 is),
//         then +96, +120, +150: 41 dates over > 5 months; London shows are the near-you minority (8 of 41).
//   ar1   UPCOMING TOUR — first date now0 + 21 d, five dates reaching +151.
//   ar2   exactly one date (+152).    ar5   three dates (+30, +90, +150).
//   ar6, ar7, ar9, ar10, ar11   4-7 dates each from +4, the last ≥ +150.    ar0, ar4, ar8, arnoimg   none (answered empty).
//   cf0-cf23   feed-only shows.   cc3n0   the full detail (3 offers: valid · no URL · unusable URL; a lineup with a
//   billing-only act; three related shows).   cc3n5   CANCELLED (the status pill).
// Every seeded show is committed as a DETAIL answer (ConcertFields.All, its offers/lineup/related lists answered, empty
// where the fixture has none), so a concert page in --fake is LOADED and never plans a request.

using System.Globalization;

namespace Wavee;

public static partial class Entities
{
    // ── fixture tables (ch 31 §3.1) ───────────────────────────────────────────────────────────────────────────────────

    readonly record struct SeedCity(string Name, string Region, string Country, short OffsetMinutes, float Lat, float Lon);

    static readonly SeedCity[] s_concertCities =
    [
        new("London", "England", "GB", 60, 51.5072f, -0.1276f),
        new("Manchester", "England", "GB", 60, 53.4808f, -2.2426f),
        new("Berlin", "Berlin", "DE", 120, 52.5200f, 13.4050f),
        new("Paris", "Île-de-France", "FR", 120, 48.8566f, 2.3522f),
        new("Amsterdam", "North Holland", "NL", 120, 52.3676f, 4.9041f),
        new("New York", "New York", "US", -240, 40.7128f, -74.0060f),
        new("Los Angeles", "California", "US", -420, 34.0522f, -118.2437f),
        new("Tokyo", "Tokyo", "JP", 540, 35.6762f, 139.6503f),
    ];

    static readonly string[] s_concertVenues =
    [
        "O2 Academy Brixton", "Alexandra Palace", "Rock City", "Columbiahalle", "Le Zénith",
        "Paradiso", "Brooklyn Steel", "The Wiltern", "Zepp Haneda", "Royal Albert Hall",
    ];

    /// <summary>Concepts for the saved place, in the provider's weight order (≥ 8, ch 31 §7.1).</summary>
    static readonly string[] s_concertConcepts = ["rock", "pop", "electronic", "hip hop", "indie", "jazz", "r&b", "metal", "edm"];

    const string SeedPlaceId = "2643743";                      // London's GeoNames id: the saved place
    const string SeedInferredGeoHash = "u33dc0";               // Berlin, guessed: the inferred place row
    const int SeedRadiusKm = ConcertHub.DefaultRadiusKm;
    const int SeedFeedCount = 1204;
    const string SeedPaginationKey = "seed-page-2";

    /// <summary>One seeded show.</summary>
    readonly record struct SeedShow(string Uri, int Artist, int City, int Venue, int Day, int Hour, bool Festival,
                                    bool Near, bool VenueLess, int Cover, string? Status);

    static string ScheduleUri(int artist, int k) => "spotify:concert:cc" + artist.ToString(CultureInfo.InvariantCulture)
                                                   + "n" + k.ToString(CultureInfo.InvariantCulture);
    static string FeedShowUri(int k) => "spotify:concert:cf" + k.ToString(CultureInfo.InvariantCulture);

    /// <summary>The day offsets of an artist's schedule (contract §8's matrix). Empty for an artist with no concerts.</summary>
    static int[] ScheduleDays(int artist)
    {
        switch (artist)
        {
            case 3:
                {
                    var days = new List<int>(41) { 2, 5, 8, 11, 12, 13 };
                    for (int d = 16; d <= 80; d += 2) days.Add(d);
                    days.Add(96);
                    days.Add(120);
                    days.Add(150);
                    return days.ToArray();
                }
            case 1: return [21, 50, 80, 110, 151];
            case 2: return [152];
            case 5: return [30, 90, 150];
        }
        if (Wrap(artist, 4) == 0) return [];
        int n = 4 + Wrap(artist, 4);
        var list = new int[n];
        int step = (150 - 4 + n - 2) / (n - 1);                 // ceil: the last date lands at or past +150
        for (int k = 0; k < n; k++) list[k] = 4 + k * step + Wrap(artist, 5);
        return list;
    }

    static List<SeedShow> SeedSchedule(int artist)
    {
        int[] days = ScheduleDays(artist);
        var shows = new List<SeedShow>(days.Length);
        for (int k = 0; k < days.Length; k++)
        {
            bool run = artist == 3 && k is 3 or 4 or 5;           // the 3-night residency
            int city = run ? 0 : Wrap(artist * 3 + k, s_concertCities.Length);
            // ar3's near-you minority is its London dates; a near-everything response must never be seeded (ch 17 §0.7)
            bool near = artist == 3 && city == 0;
            shows.Add(new SeedShow(
                ScheduleUri(artist, k), artist, city,
                Venue: run ? 1 : Wrap(artist + k * 7, s_concertVenues.Length),
                Day: days[k], Hour: 19 + Wrap(k, 3),
                Festival: k == 2,
                Near: near,
                VenueLess: !run && Wrap(k, 5) == 4,                // a city-primary tile, "19:30 · with Band of Silver" (parity 41)
                Cover: Wrap(k, 3) == 1 ? -1 : artist + k,          // every third show has no poster: the tinted pane
                Status: artist == 3 && k == 5 ? "CANCELLED" : k == 0 ? "CONFIRMED" : null));
        }
        return shows;
    }

    static List<SeedShow> SeedFeedShows()
    {
        var shows = new List<SeedShow>(24);
        for (int k = 0; k < 24; k++)
        {
            int artist = Wrap(k * 5 + 1, ArtistCount);
            int city = k < 6 ? Wrap(k, 2) : Wrap(k, s_concertCities.Length);
            shows.Add(new SeedShow(FeedShowUri(k), artist, city, Venue: Wrap(k * 3, s_concertVenues.Length),
                Day: 3 + k * 4, Hour: 20, Festival: Wrap(k, 7) == 3, Near: city == 0, VenueLess: false,
                Cover: Wrap(k, 4) == 2 ? -1 : k + 5, Status: null));
        }
        return shows;
    }

    // ── the seed ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The concert surfaces (contract §8). Called FIRST by <c>SeedArtistSurfaces</c> (owner N-B), which then
    /// re-derives every artist's tour banner on the fixed seed clock. Commits; never publishes.</summary>
    static void SeedConcertSurfaces(long now0)
    {
        var s = Staging.Rent();
        try
        {
            s.Authority = Authority.Seed;
            var all = new List<SeedShow>(128);
            for (int a = 0; a < ArtistCount; a++) all.AddRange(SeedSchedule(a));
            all.AddRange(SeedFeedShows());

            foreach (var show in all) StageSeedShow(s, in show, now0);
            StageSeedSchedules(s);
            StageSeedPlaces(s);
            StageSeedFeed(s);
            Commit(s);
        }
        finally
        {
            Staging.Return(s);
        }
    }

    static StagedId SeedId(Staging s, string uri) => new(s.AddText(Utf8(uri)));

    static void StageSeedShow(Staging s, in SeedShow show, long now0)
    {
        var city = s_concertCities[Wrap(show.City, s_concertCities.Length)];
        long start = (now0 + show.Day * 86_400L + show.Hour * 3_600L) * 1000L;
        var id = SeedId(s, show.Uri);
        string artistName = s_artistNames[Wrap(show.Artist, s_artistNames.Length)];

        ref var row = ref s.Concerts.RowFor(id, Authority.Seed, (uint)ConcertFields.All);
        row.Title = s.AddText(Utf8(show.Festival ? city.Name + " Sound Festival" : artistName));
        row.Venue = show.VenueLess ? default : s.AddText(Utf8(s_concertVenues[Wrap(show.Venue, s_concertVenues.Length)]));
        row.City = s.AddText(Utf8(city.Name));
        row.Date = start;
        row.OffsetMinutes = city.OffsetMinutes;
        if (show.Cover >= 0) row.Image = s.AddText(Utf8(Cover(show.Cover)));
        row.Accent = s_camelotColors[Wrap(show.Artist + show.Day, s_camelotColors.Length)];
        row.Flags = (show.Festival ? (uint)ConcertFlags.Festival : 0)
                  | (show.Cover >= 0 ? (uint)ConcertFlags.HasArt : 0)
                  | (show.Near ? (uint)ConcertFlags.NearUser : 0);
        row.FlagsMask = (uint)ConcertFlags.NearMask;
        row.Region = s.AddText(Utf8(city.Region));
        row.Country = s.AddText(Utf8(city.Country));
        row.DoorsOpenAt = start - 3_600_000L;
        row.DoorsOffsetMinutes = city.OffsetMinutes;
        row.AgeRestriction = Wrap(show.Day, 3) == 0 ? s.AddText(Utf8("14+")) : default;
        if (show.Status is { } status)
        {
            row.StatusText = s.AddText(Utf8(status));
            row.Status = status == "CANCELLED" ? (byte)1 : (byte)0;
        }
        row.Lat = city.Lat;
        row.Lon = city.Lon;

        // the lineup: the headliner, a support act on every third show, a billing-only act on a venue-less show (and on
        // the full detail) — the URI-less row ch 31 §7.1 asks for
        var lineup = s.ConcertRun(ConcertLink.Lineup);
        SeedAct(s, ref lineup, show.Artist);
        bool detail = show.Uri == ScheduleUri(3, 0);
        if (Wrap(show.Day, 3) == 0 || detail) SeedAct(s, ref lineup, Wrap(show.Artist + 2, ArtistCount));
        if (show.VenueLess || detail)
        {
            ref var billing = ref lineup.Add();
            billing.T0 = s.AddText(Utf8(detail ? "The Openers" : "Band of Silver"));
        }
        lineup.End(in id);

        // offers: three on the detail (valid · no URL · an unusable URL), one valid offer on every fourth show
        var offers = s.ConcertRun(ConcertLink.Offers);
        if (detail)
        {
            SeedOffer(s, ref offers, "Ticketmaster", "https://tickets.example.com/wavee/cc3n0", 4500, 7500, "EUR",
                (int)(now0 - 30 * 86_400L), (int)(now0 + 1 * 86_400L), city.OffsetMinutes, ConcertOfferAvailability.Available);
            SeedOffer(s, ref offers, "Resale partner", null, 0, 0, null, 0, 0, 0, ConcertOfferAvailability.Unavailable);
            SeedOffer(s, ref offers, "Box office", "not a url", 0, 0, null, 0, 0, 0, ConcertOfferAvailability.Unknown);
        }
        else if (Wrap(show.Day, 4) == 0)
        {
            SeedOffer(s, ref offers, "Ticketmaster", "https://tickets.example.com/wavee/" + show.Uri[16..], 3500, 3500, "GBP",
                0, 0, 0, ConcertOfferAvailability.Available);
        }
        offers.End(in id);

        var related = s.ConcertRun(ConcertLink.Related);
        if (detail)
        {
            related.Add(SeedId(s, ScheduleUri(1, 0)));
            related.Add(SeedId(s, ScheduleUri(5, 1)));
            related.Add(SeedId(s, FeedShowUri(2)));
        }
        related.End(in id);
    }

    static void SeedAct(Staging s, ref ConcertRun lineup, int artist)
    {
        ref var act = ref lineup.Add(SeedId(s, ArtistUri(artist)));
        act.T0 = s.AddText(Utf8(s_artistNames[Wrap(artist, s_artistNames.Length)]));
        act.T1 = s.AddText(Utf8(Cover(artist + 3)));
        act.U0 = s_camelotColors[Wrap(artist, s_camelotColors.Length)];
        act.B0 = LineupEdge.Navigable;
    }

    static void SeedOffer(Staging s, ref ConcertRun offers, string? provider, string? url, int minCents, int maxCents,
        string? currency, int saleStart, int saleEnd, short offset, ConcertOfferAvailability availability)
    {
        ref var o = ref offers.Add();
        if (provider is not null) o.T0 = s.AddText(Utf8(provider));
        if (url is not null) o.T1 = s.AddText(Utf8(url));
        if (currency is not null) o.T2 = s.AddText(Utf8(currency));
        o.B0 = (byte)availability;
        if (minCents > 0) { o.I0 = minCents; o.B1 |= OfferEdge.HasMin; }
        if (maxCents > 0) { o.I1 = maxCents; o.B1 |= OfferEdge.HasMax; }
        if (saleStart > 0) { o.L0 = saleStart; o.I2 = offset; o.B1 |= OfferEdge.HasSaleStart; }
        if (saleEnd > 0) { o.L1 = saleEnd; o.I3 = offset; o.B1 |= OfferEdge.HasSaleEnd; }
    }

    /// <summary>Every seeded artist's schedule edge, answered — empty for the artists with none (and for N-B's
    /// <c>arnoimg</c>), so no artist page and no schedule plans a request.</summary>
    static void StageSeedSchedules(Staging s)
    {
        for (int a = 0; a < ArtistCount; a++)
        {
            int n = ScheduleDays(a).Length;
            var run = s.ConcertRun(ConcertLink.ArtistConcerts);
            for (int k = 0; k < n; k++) run.Add(SeedId(s, ScheduleUri(a, k)));
            run.End(SeedId(s, ArtistUri(a)));
        }
        var none = s.ConcertRun(ConcertLink.ArtistConcerts);
        none.End(SeedId(s, "spotify:artist:arnoimg"));
    }

    static void StageSeedPlaces(Staging s)
    {
        // The inferred place FIRST, as the saved one, then the real saved place: the commit walks in order, so SavedPlace
        // ends on London (chosen) while Berlin's row keeps its Inferred bit (contract §8: "a saved place (not inferred) and
        // one inferred place row").
        StageSeedPlace(s, "", "Berlin", "Berlin", "DE", SeedInferredGeoHash, 52.5200f, 13.4050f,
            (uint)PlaceFlags.Inferred, PlaceRole.Saved);
        StageSeedPlace(s, SeedPlaceId, "London", "England", "GB", "gcpvj0", 51.5072f, -0.1276f, 0, PlaceRole.Saved);
        StageSeedPlace(s, SeedPlaceId, "London", "England", "GB", "gcpvj0", 51.5072f, -0.1276f, 0, PlaceRole.ArtistPage);

        var concepts = s.ConcertRun(ConcertLink.PlaceConcepts);
        for (int i = 0; i < s_concertConcepts.Length; i++)
        {
            ref var c = ref concepts.Add();
            c.T0 = s.AddText(Utf8("spotify:concept:" + s_concertConcepts[i].Replace(' ', '-').Replace("&", "n")));
            c.T1 = s.AddText(Utf8(s_concertConcepts[i]));
            c.U0 = BitConverter.SingleToUInt32Bits(9f - i);
        }
        concepts.EndKeyed(s.AddText(Utf8(SeedPlaceId)));
    }

    static void StageSeedPlace(Staging s, string id, string name, string region, string country, string geo, float lat,
        float lon, uint flags, PlaceRole role)
    {
        ref var p = ref s.Places.Add();
        p.Key = s.AddText(Utf8(ConcertFeedKey.PlaceKey(id, geo)));
        if (id.Length > 0) p.Id = s.AddText(Utf8(id));
        p.Name = s.AddText(Utf8(name));
        p.Region = s.AddText(Utf8(region));
        p.Country = s.AddText(Utf8(country));
        p.GeoHash = s.AddText(Utf8(geo));
        p.Lat = lat;
        p.Lon = lon;
        p.Flags = flags | (uint)PlaceFlags.HasCoords;
        p.Role = role;
    }

    /// <summary>THE DEFAULT HUB FEED SUBJECT (saved place, 100 km, any date, no concepts): Nearby + Recommended +
    /// AllEvents, promos under Nearby, a continuation and a count.</summary>
    static void StageSeedFeed(Staging s)
    {
        var key = s.AddText(Utf8(ConcertFeedKey.For(SeedPlaceId, SeedRadiusKm, null, null)));
        var nearbyKey = s.AddText(Utf8("concerts-near-you"));
        var recommendedKey = s.AddText(Utf8("recommended-events"));
        var allKey = s.AddText(Utf8("all-events"));

        var sections = s.ConcertRun(ConcertLink.FeedSection);
        SeedMember(s, ref sections, ScheduleUri(3, 3), ConcertFeedSectionKind.Nearby, nearbyKey);
        SeedMember(s, ref sections, ScheduleUri(3, 7), ConcertFeedSectionKind.Nearby, nearbyKey);   // both London
        for (int k = 0; k < 6; k++) SeedMember(s, ref sections, FeedShowUri(k), ConcertFeedSectionKind.Nearby, nearbyKey);
        for (int k = 6; k < 14; k++) SeedMember(s, ref sections, FeedShowUri(k), ConcertFeedSectionKind.Recommended, recommendedKey);
        for (int k = 14; k < 24; k++) SeedMember(s, ref sections, FeedShowUri(k), ConcertFeedSectionKind.AllEvents, allKey);
        int[] scheduleArtists = [1, 5, 6, 7, 9, 10, 11];
        foreach (int a in scheduleArtists)
            for (int k = 0; k < ScheduleDays(a).Length; k++)
                SeedMember(s, ref sections, ScheduleUri(a, k), ConcertFeedSectionKind.AllEvents, allKey);
        // one duplicate on purpose: the merge dedupes a show a later section repeats (ch 17 §8, ConcertFeedPage.Append)
        SeedMember(s, ref sections, FeedShowUri(0), ConcertFeedSectionKind.AllEvents, allKey);
        sections.EndKeyed(key);

        var promos = s.ConcertRun(ConcertLink.FeedPlaylists);
        ReadOnlySpan<int> promoPlaylists = [0, 3, 5];
        foreach (int p in promoPlaylists)
        {
            ref var e = ref promos.Add(SeedId(s, PlaylistUri(p)));
            e.B0 = (byte)ConcertFeedSectionKind.Nearby;
            e.T0 = nearbyKey;
        }
        promos.EndKeyed(key);

        ref var feed = ref s.ConcertFeedRows.Add();
        feed.Key = key;
        feed.PaginationKey = s.AddText(Utf8(SeedPaginationKey));
        feed.Count = SeedFeedCount;
        feed.CountVersion = 1;
        feed.Parts = StagedConcertFeed.PagePart | StagedConcertFeed.CountPart;
    }

    static void SeedMember(Staging s, ref ConcertRun run, string uri, ConcertFeedSectionKind kind, TextRef sectionKey)
    {
        ref var e = ref run.Add(SeedId(s, uri));
        e.B0 = (byte)kind;
        e.T0 = sectionKey;
    }
}
