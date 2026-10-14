// ── Entities/Concert.Page.cs ───────────────────────────────────────────────────────────────────────────────────────
// the concert pages' install, mount points and shared reads: the CORE of the three concert surfaces (T10b3 moved the pictures to Classic)
//
// Role: UI (core hosts)
// Owner: N (stream N-C)
// Wave: 5
// Spec: ch 17 §0 (the fifteen non-negotiables), §1 (the three trees), §2 W1-W22, §5 motion, §6 interaction, §7
//   readiness, §9 must-not-simplify; WP-5.N contract §5 (routes), §7 (install + the hub seam)
//
// THE THREE PAGES READ `Entities.Current`. The schedule demands its edge through Fetch
// (`EnsureEdge(FetchEdge.ArtistConcerts, artist)` while Unknown); the detail demands `Entities.Ensure(concert, All)`.
// The HUB is the one surface whose data is not a row a Fetch batch can plan — a feed subject, a count preview, a place's
// concepts, a place search / save — so it asks `ConcertHost.Current` (Concert.cs), which commits and publishes like any
// answer; the page then re-reads the tables. What 0.2.9 did with a query GENERATION is structural here: every filter tuple
// is its own feed subject (`ConcertFeedKey`), so a late answer lands on ITS subject and can never paint another.
//
// T10b3: the pages are registered Classic views (`Pages.Concerts`, `Pages.ArtistConcerts`, `Pages.Concert`, with `Concert.Tail` and `Concert.PlacePicker`
// as parts). The hosts are `Concert.Hub.cs` (`HubHost`, `TailHost`), `Concert.Schedule.cs` (`ScheduleHost`), `Concert.Detail.cs` (`DetailHost`) and
// `Concert.Picker.cs` (`PlacePicker`, `PlacePickerHost`); the verbs a view calls are `ConcertVerbs` (`Concert.Verbs.cs`).
//
// PROPS FREEZE AT MOUNT. Every page/child takes signals, delegates or a keyed remount (§1.4, §9 #9): the filter bar reads
// the page's signals; the month board is keyed on its composed signature; the append tail is keyed per pagination key.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace Wavee;

public readonly partial struct Concert
{
    // ══ 1. INSTALL AND THE MOUNT POINTS (contract §7) ═════════════════════════════════════════════════════════════════

    /// <summary>Owner N-C's install, called by <c>Artist.InstallPages</c>: the three concert route kinds, the loc copy the
    /// rules speak and the hub's data seam.</summary>
    internal static void InstallPages()
    {
        ConcertCopy.Current = ConcertCopy.Localized;
        ConcertHost.Current = Spotify.Api.Concerts;
        Shell.SetPage(Shell.RouteKind.Concerts, MountHub);
        Shell.SetPage(Shell.RouteKind.ArtistConcerts, MountSchedule);
        Shell.SetPage(Shell.RouteKind.Concert, MountDetail);
    }

    /// <summary><see cref="Shell.RouteKind.Concerts"/>: one hub per KeepAlive slot (its accumulated pages survive a park).</summary>
    // MOUNT POINT (stage B contract)
    public static Element MountHub(in Shell.Route route)
        => Embed.Comp(static () => new HubHost()) with { Key = "concert-hub" };

    /// <summary><see cref="Shell.RouteKind.ArtistConcerts"/>, keyed by the route: artist → artist is a fresh page.</summary>
    // MOUNT POINT (stage B contract)
    public static Element MountSchedule(in Shell.Route route)
    {
        string key = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, key, route.Arg), static () => new ScheduleHost()) with { Key = "artist-schedule:" + key };
    }

    /// <summary><see cref="Shell.RouteKind.Concert"/>, keyed by the route.</summary>
    // MOUNT POINT (stage B contract)
    public static Element MountDetail(in Shell.Route route)
    {
        string key = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, key, route.Arg), static () => new DetailHost()) with { Key = "concert-detail:" + key };
    }

    sealed record PageProps(EntityUri Subject, string RouteKey, StringId Arg);

    // ══ 2. SHARED READS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What an event tile paints, as a value: built from a handle (<see cref="TileOf"/>) or from a seed, so a
    /// skeleton derives its shimmer from the SAME builder the loaded page renders (§0 #11).</summary>
    internal readonly record struct TileData(string Key, string Caption, string Title, string Place, string? ImageUrl, uint Accent);

    /// <summary>A concert row as a tile value: the accent date caption in the provider's clock, the title (or the
    /// "Concert" fallback — never the venue twice), "Venue · City".</summary>
    internal static TileData TileOf(Concert c)
    {
        var s = Entities.Strings;
        string title = s.Resolve(c.TitleId), venue = s.Resolve(c.VenueId), city = s.Resolve(c.CityId);
        return new TileData(c.Slot.ToString(CultureInfo.InvariantCulture),
            ConcertTime.DateCaption(c.LocalDate, CultureInfo.CurrentCulture),
            string.IsNullOrWhiteSpace(title) ? Loc.Get(Strings.Concerts.Detail.Concert) : title,
            venue.Length == 0 ? city : city.Length == 0 ? venue : venue + " · " + city,
            Controls.ArtUrl(c.ImageId), c.Accent);
    }

    /// <summary>A placeholder tile for a shimmer seed (none of its text is ever shown).</summary>
    internal static TileData SeedTile(string key) =>
        new(key, "Fri, Jan 1 · 20:00", "Concert placeholder", "Venue placeholder · City", null, 0);

    /// <summary>The concert's venue and city as the artist page's upcoming card prints them (the venue, else the title-or-venue; the city).</summary>
    public static (string Venue, string City) StubFacts(Concert c)
    {
        var s = Entities.Strings;
        string venue = s.Resolve(c.VenueId), city = s.Resolve(c.CityId);
        return (venue.Length > 0 ? venue : c.TitleOrVenue, city);
    }

    /// <summary>The overlay options of the concert flyouts (the when and where flyouts and the location picker): focus-trapped, light-dismiss, constrained to the
    /// window.</summary>
    internal static PopupOptions FlyoutOptions =>
        new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true };
}
