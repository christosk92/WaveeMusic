// ── Theming/Contracts/ConcertProps.cs — the props of the concert surfaces' views (themes plan 6.0, T10b3) ─────────────────────
//
// The core hosts (`Concert.HubHost`, `ScheduleHost`, `DetailHost`, `TailHost`, `PlacePickerHost`) keep the data and the decisions: the scope, the demands and the
// retries, the filter tuple and the feed subject it keys, the near-tail watch and the append gate, the readiness verdicts, the schedule shaping and the detail
// facts. A theme's view draws what these records carry. Every member is read-only (WVTH006): a live value travels as a read-only signal or a Func, a verb as
// an Action. The three page records never compare equal to another instance (the host rebuilds them on every render it makes, so the view re-runs exactly when the
// page tree used to be rebuilt).
//
// The facts a view reads off a `Concert` / `Playlist` handle or a record of `Concert.Rules.cs` are plain reads that do not subscribe; the host wakes on every
// table they come from and re-describes the page. The verbs that name core state a theme may not reference (WVTH002: Shell, Playback, Entities) are the static
// `ConcertVerbs`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

// ── shared values ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A shelf item as a VALUE (slot + version): `PagedShelf` compares items by value, so a publish that changes nothing a tile paints rebuilds no tile.
/// Slot 0 (<c>Table.None</c>) is a lead cell with no concert behind it (the related shelf's "Browse all").</summary>
public readonly record struct ConcertShelfItem(int Slot, uint Version);

/// <summary>What one block of the hub feed is.</summary>
public enum ConcertBlockKind : byte
{
    /// <summary>A Nearby or Recommended shelf of event tiles (<see cref="ConcertFeedBlock.Nearby"/> names which).</summary>
    Shelf,

    /// <summary>A shelf of playlist promos for the scene.</summary>
    Promos,

    /// <summary>The "All events" virtualized grid.</summary>
    Grid,

    /// <summary>The append tail (the host mounts it: <see cref="ConcertHubProps.Tail"/>).</summary>
    Tail,
}

/// <summary>One block of the hub feed, in draw order.</summary>
/// <param name="Kind">Which block.</param>
/// <param name="Key">The element key of the block (a shelf's and a promo shelf's carry the section kind and key, the tail's the pagination key).</param>
/// <param name="Nearby">A shelf: true for "Near you", false for "Recommended for you".</param>
/// <param name="Items">A shelf's or promo shelf's items (at most 16).</param>
/// <param name="Feed">The grid: the feed subject slot (its lazy grid is keyed by it).</param>
public sealed record ConcertFeedBlock(ConcertBlockKind Kind, string Key, bool Nearby, IReadOnlyList<ConcertShelfItem> Items, int Feed);

/// <summary>Which body the hub shows once its feed has settled.</summary>
public enum ConcertHubBodyKind : byte
{
    /// <summary>No saved place: the prompt whose one action opens the location picker.</summary>
    NoLocation,

    /// <summary>The feed answered with nothing for this place.</summary>
    Empty,

    /// <summary>The feed's blocks.</summary>
    Feed,
}

/// <summary>The hub's body: the kind and, for a feed, its blocks.</summary>
public sealed record ConcertHubBody(ConcertHubBodyKind Kind, IReadOnlyList<ConcertFeedBlock> Blocks);

// ── the hub ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The filter bar's inputs: the page's filter tuple as read-only signals and the verbs that edit it. One instance per page (the bar's props freeze at mount;
/// its members are live).</summary>
public sealed record ConcertFilterBarProps
{
    /// <summary>The saved place (null: none yet).</summary>
    public required IReadSignal<ConcertPlace?> Place { get; init; }

    /// <summary>Whether the place was inferred (an approximate IP guess), null when there is no place.</summary>
    public required IReadSignal<bool?> Inferred { get; init; }

    /// <summary>The search radius in km.</summary>
    public required IReadSignal<int> Radius { get; init; }

    /// <summary>The date window.</summary>
    public required IReadSignal<ConcertWhen> When { get; init; }

    /// <summary>The selected genre uris.</summary>
    public required IReadSignal<IReadOnlyList<string>> Selected { get; init; }

    /// <summary>The place's genre tokens.</summary>
    public required IReadSignal<IReadOnlyList<ConcertConcept>> Concepts { get; init; }

    /// <summary>The previewed count of events for the filter tuple (null until it lands).</summary>
    public required IReadSignal<int?> Count { get; init; }

    /// <summary>Sets the date window.</summary>
    public required Action<ConcertWhen> SetWhen { get; init; }

    /// <summary>Sets the selected genre uris.</summary>
    public required Action<IReadOnlyList<string>> SetSelected { get; init; }

    /// <summary>Sets the radius (a no-op for the same value).</summary>
    public required Action<int> SetRadius { get; init; }

    /// <summary>Hands the where pill's node to the host (the picker anchors on it).</summary>
    public required Action<NodeHandle> CaptureWhere { get; init; }

    /// <summary>The where pill's node.</summary>
    public required Func<NodeHandle> WhereAnchor { get; init; }

    /// <summary>Opens (or closes) the location picker at the where pill.</summary>
    public required Action OpenPicker { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ConcertsHub"/>.</summary>
public sealed record ConcertHubProps
{
    /// <inheritdoc />
    public bool Equals(ConcertHubProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>The tab's scroll scope (the keys of the page's offset and of the filter row's offset derive from it).</summary>
    public required string ScrollScope { get; init; }

    /// <summary>The hub viewport's scroll handle (the host's near-tail watch and the lazy grid's page offset read it).</summary>
    public required ScrollHandle Scroll { get; init; }

    /// <summary>Pending: the feed (or the place resolution) has not answered, so the skeleton shows.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The feed failed: the error vacancy shows.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>The body once settled; null while pending or failed.</summary>
    public required ConcertHubBody? Body { get; init; }

    /// <summary>The failure vacancy's Retry.</summary>
    public required Action Retry { get; init; }

    /// <summary>Opens (or closes) the location picker anchored at the where pill.</summary>
    public required Action OpenPicker { get; init; }

    /// <summary>The filter bar's inputs.</summary>
    public required ConcertFilterBarProps Bar { get; init; }

    /// <summary>The "All events" run's length, a value-gated memo (an unrelated publication re-renders no grid).</summary>
    public required IReadSignal<int> GridCount { get; init; }

    /// <summary>The concert slot of grid cell <c>index</c>; <c>Table.None</c> for a cell the table does not (yet) hold.</summary>
    public required Func<int, int> GridSlotAt { get; init; }

    /// <summary>Mounts the append tail for a pagination key (a keyed core host; its view is <see cref="ViewKeys.ConcertTail"/>).</summary>
    public required Func<string, Element> Tail { get; init; }
}

/// <summary>What the feed's tail draws.</summary>
public enum ConcertTailState : byte
{
    /// <summary>Nothing (three quiet failures collapsed it).</summary>
    None,

    /// <summary>Far from the tail: a quiet spacer with no shimmer subtree.</summary>
    Spacer,

    /// <summary>Near the tail or loading: four shimmering tiles of the grid's family.</summary>
    Shimmer,
}

/// <summary>The props of <see cref="ViewKeys.ConcertTail"/>.</summary>
public sealed record ConcertTailProps(ConcertTailState State);

/// <summary>The props of <see cref="ViewKeys.ConcertPlacePicker"/>: the picker's live signals and verbs. The debounced search, the locate flow and the save are the host's.</summary>
public sealed record ConcertPlacePickerProps
{
    /// <summary>The query's writable cell (the search field edits it).</summary>
    public required Func<Signal<string>> Query { get; init; }

    /// <summary>The result place slots.</summary>
    public required IReadSignal<int[]> Results { get; init; }

    /// <summary>A search or a locate is in flight.</summary>
    public required IReadSignal<bool> Loading { get; init; }

    /// <summary>The error sentence (null: none).</summary>
    public required IReadSignal<string?> Error { get; init; }

    /// <summary>"Use my location": the consent flow (it fills the results, it never saves).</summary>
    public required Action UseMyLocation { get; init; }

    /// <summary>A result row's pick: saves that place and closes the picker.</summary>
    public required Action<int> Select { get; init; }
}

// ── the artist's schedule ─────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Everything the tour dashboard paints, as VALUES: built from the artist's schedule edge (<c>ScheduleHost.Model</c>) or, for the skeleton, from
/// placeholders (<c>ScheduleHost.Seed</c>).</summary>
public sealed record ConcertScheduleModel
{
    /// <summary>The artist's name (a single space for the skeleton).</summary>
    public required string Name { get; init; }

    /// <summary>The next show (the hero's), null when every date is past.</summary>
    public required ConcertShow? Spotlight { get; init; }

    /// <summary>The tour stats over the whole schedule.</summary>
    public required ConcertTourStats Stats { get; init; }

    /// <summary>The clock the relative times read.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>The month boards, chronological (the spotlight left out).</summary>
    public required IReadOnlyList<ConcertMonthGroup> Groups { get; init; }

    /// <summary>The selected month's index in <see cref="Groups"/> (-1: none remains).</summary>
    public required int Selected { get; init; }

    /// <summary>The guarded near-you set of show uris.</summary>
    public required HashSet<string> Nearby { get; init; }

    /// <summary>The spotlight's accent (0 for none or the skeleton).</summary>
    public required uint SpotlightAccent { get; init; }

    /// <summary>The hero photo (null for the skeleton or an artist with none).</summary>
    public required string? HeroImageUrl { get; init; }

    /// <summary>The location button's label (the saved place, the artist page's place, else "Set location"; a single space for the skeleton).</summary>
    public required string LocationLabel { get; init; }

    /// <summary>True for the skeleton model (nothing in it is real: no store read, no label for the shy pill, no click).</summary>
    public required bool Seed { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ArtistConcerts"/>.</summary>
public sealed record ConcertScheduleProps
{
    /// <inheritdoc />
    public bool Equals(ConcertScheduleProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>The route's key (the keys of the page, its region, its scroller and the shy pill derive from it).</summary>
    public required string RouteKey { get; init; }

    /// <summary>The tab's scroll scope.</summary>
    public required string ScrollScope { get; init; }

    /// <summary>Pending: the schedule edge has not answered, so the skeleton shows.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The schedule edge failed: the error vacancy shows.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>The schedule answered with no shows at all: the "no upcoming" vacancy.</summary>
    public required bool Empty { get; init; }

    /// <summary>The failure vacancy's Retry.</summary>
    public required Action Retry { get; init; }

    /// <summary>The dashboard's model, read at build time (it reads the tables and the month signal).</summary>
    public required Func<ConcertScheduleModel> Model { get; init; }

    /// <summary>The skeleton's model (eight placeholder dates with a two-night run).</summary>
    public required Func<ConcertScheduleModel> Seed { get; init; }

    /// <summary>A month tab's pick (the month's <c>yyyy-MM</c> key).</summary>
    public required Action<string> SelectMonth { get; init; }

    /// <summary>Opens (or closes) the location picker anchored at the hero's location button.</summary>
    public required Action OpenPicker { get; init; }

    /// <summary>"View event": opens the spotlight's concert.</summary>
    public required Action OpenSpotlight { get; init; }

    /// <summary>Hands the location button's node to the host (the picker anchors on it).</summary>
    public required Action<NodeHandle> CaptureAnchor { get; init; }
}

// ── the event ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One ticket offer as the page paints it. <see cref="Url"/> is a validated external link or null (the quiet "not available online" line).</summary>
public readonly record struct ConcertOfferView(string Provider, string? Availability, string? Price, string? Sale, string? Url);

/// <summary>One billed act. Only a canonical artist (<see cref="Navigable"/>) opens its page.</summary>
public readonly record struct ConcertActView(string Name, string? Image, int ArtistSlot, bool Navigable);

/// <summary>Everything the detail paints, as VALUES: read off the handle (<c>Concert.DetailViewOf</c>), or the skeleton's <see cref="Seed"/> (facts, two offers and
/// three lineup rows: the exact silhouette of the loaded page).</summary>
public sealed record ConcertDetailView(string Title, string DateLine, string? Doors, string Place, string? Ages, string? Status,
                                        ConcertOfferView[] Offers, ConcertActView[] Lineup, ConcertShelfItem[] Related, string? ImageUrl, uint Accent)
{
    /// <summary>The skeleton's content.</summary>
    public static readonly ConcertDetailView Seed = new(" ", "Saturday, January 1, 2030 · 20:00", "Doors open 19:00",
        "Venue name placeholder · City placeholder", null, null,
        [new(" ", " ", " ", null, "https://seed.invalid/"), new(" ", null, null, null, null)],
        [new(" ", null, 0, true), new(" ", null, 0, true), new(" ", null, 0, false)], [], null, 0);
}

/// <summary>The props of <see cref="ViewKeys.ConcertDetail"/>.</summary>
public sealed record ConcertDetailProps
{
    /// <inheritdoc />
    public bool Equals(ConcertDetailProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>The route's key (the keys of the page's region and scroller derive from it).</summary>
    public required string RouteKey { get; init; }

    /// <summary>The tab's scroll scope.</summary>
    public required string ScrollScope { get; init; }

    /// <summary>Pending: the whole row was not asked yet, or is still in flight.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The row settled without an identity: the "not available" vacancy.</summary>
    public required bool Unavailable { get; init; }

    /// <summary>The page's content, read at build time off the concert row.</summary>
    public required Func<ConcertDetailView> Detail { get; init; }
}
