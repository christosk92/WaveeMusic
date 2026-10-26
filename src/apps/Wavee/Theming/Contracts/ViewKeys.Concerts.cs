// ── Theming/Contracts/ViewKeys.Concerts.cs — the concert surfaces' published view keys (themes plan 6.0, T10b3) ────────────────
//
// T10b3 publishes the three concert PAGES (`Pages.Concerts` the hub, `Pages.ArtistConcerts` an artist's tour dashboard and `Pages.Concert` one event) and the
// two surfaces the hub opens or mounts as parts: the append tail of the feed (`Concert.Tail`) and the location picker's flyout (`Concert.PlacePicker`).
// The filter bar, the when / where flyouts, the count ticker, the month board and the shy month pill are the page views' own parts (they hold view state only:
// an expansion, a drill-down, a tween), so they are not keys.
//
// What stays core: the data and its demands (the feed subject per filter tuple, the count preview, the place's concepts, the saved place, the artist schedule edge,
// the concert row), the filter tuple and its decisions (`ConcertHub`), the near-tail watch and the append gate, the readiness verdicts, the schedule shaping
// (`ConcertScheduleShaping`: spotlight, month groups, runs, the near-you guard), the detail facts (`ConcertOffers`, `ConcertDetailInfo`), the location picker's
// search / locate / save, and every verb that names core state (`ConcertVerbs`). The pictures are the structure's.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Concerts hub: the header card, the pinned filter bar and the feed (shelves, promos, the virtualized grid, the tail) in its skeleton region.
    /// Mounted by `Concert.HubHost`. Props: <see cref="ConcertHubProps"/>.</summary>
    public static readonly ViewKey<ConcertHubProps> ConcertsHub = new("Pages.Concerts");

    /// <summary>An artist's tour dashboard: the split hero with the next show, the tour-dates card with its month tabs and board, the shy month pill. Mounted by
    /// `Concert.ScheduleHost`. Props: <see cref="ConcertScheduleProps"/>.</summary>
    public static readonly ViewKey<ConcertScheduleProps> ArtistConcerts = new("Pages.ArtistConcerts");

    /// <summary>One event: the split hero with the facts, the tickets, the lineup and the related shelf. Mounted by `Concert.DetailHost`. Props: <see cref="ConcertDetailProps"/>.</summary>
    public static readonly ViewKey<ConcertDetailProps> ConcertDetail = new("Pages.Concert");

    /// <summary>The hub feed's tail: nothing, a quiet spacer or four shimmering tiles, as the append gate decides. Mounted by `Concert.TailHost`.
    /// Props: <see cref="ConcertTailProps"/>.</summary>
    public static readonly ViewKey<ConcertTailProps> ConcertTail = new("Concert.Tail");

    /// <summary>The location picker's flyout body (search field, "Use my location", the error line, the results). Mounted by `Concert.PlacePickerHost` inside the
    /// overlay `Concert.PlacePicker` opens. Props: <see cref="ConcertPlacePickerProps"/>.</summary>
    public static readonly ViewKey<ConcertPlacePickerProps> ConcertPlacePicker = new("Concert.PlacePicker");
}
