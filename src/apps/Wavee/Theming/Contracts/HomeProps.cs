// ── Theming/Contracts/HomeProps.cs — the props of the Home family's views (themes plan 6.0, T10a1) ──────────────────────────
//
// The core hosts keep the data and the decisions: the facet machine and its effects (`HomeHost`), the screen model, the load verdict and
// the shell-tint claim (`HomeScreen`), the daylist's reads and timers (`DaylistCard`, `DaylistClock`, `DaylistCountingClock`), the Recents
// subscription (`RecentGrid`), the Featured Charts demand (`ChartsBlockView`), the drill page's demand and table sync (`SectionScreen`) and
// the layout document's verbs (`CustomizeScreen`). A theme's view draws what these records carry. Every member is read-only (WVTH006);
// live values travel as read-only signals or Funcs the view binds, a writable cell as a `Func<Signal<T>>`.
//
// The verbs that name core state a theme may not reference (WVTH002: Shell, Playback, Entities...) are the static `HomeVerbs`
// (open the history, a section's "See all", Browse, the customizer, a tag search, a chart tile, Shuffle); a view calls them as
// handlers. A pure rule a view needs stays where it is (`DaylistForm`, `FacetForm`, `ShelfLead`...).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.HomeUi;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.HomePage"/>. The host rebuilds it on every render it makes (the facet and the model are
/// read there, as the page always did), so the page view re-runs exactly when the page tree used to be rebuilt.</summary>
public sealed record HomePageProps
{
    /// <summary>The PUBLISHED facet ("" is All).</summary>
    public required string Facet { get; init; }

    /// <summary>The scroll key of this facet's offset (the page's scroll scope plus the facet), so each facet keeps its own offset.</summary>
    public required string ScrollKey { get; init; }

    /// <summary>The page's scroll handle: the host's landing edge reads its offset.</summary>
    public required ScrollHandle Scroll { get; init; }

    /// <summary>The reveal group of the skeleton region (the host instance).</summary>
    public required object Group { get; init; }

    /// <summary>The facet row's props: this tab's own stable signals and verbs.</summary>
    public required FacetRowProps FacetRow { get; init; }

    /// <summary>The facet-switch machine: the dim host binds its phase.</summary>
    public required IReadSignal<FacetSwitchState> Switch { get; init; }

    /// <summary>The region is pending (the cold load): the skeleton shows.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The first load concluded with no document while online: the failure vacancy shows.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>The live screen model, else the seed (the skeleton's shape) while there is none.</summary>
    public required Func<ScreenModel> Content { get; init; }

    /// <summary>The seed for the shimmer: the facet's last-known zone shape, else a generic first-paint shape.</summary>
    public required Func<ScreenModel> Seed { get; init; }

    /// <summary>The facet content was built for this model (the host's edge-gated `facet.content` log).</summary>
    public required Action<ScreenModel> OnContent { get; init; }

    /// <summary>The failure vacancy's Retry.</summary>
    public required Action RetryFirstLoad { get; init; }

    /// <summary>The overlay service the cards' menus open on (null while the null service).</summary>
    public IOverlayService? Overlay { get; init; }

    /// <summary>The shell tint claim (`Palette.ShellTint` over the first zone's art): a zero-size leaf the page keeps in its tree so the
    /// window chrome follows the page. The core decides the claim and the accent; the Classic material view draws the washes.</summary>
    public required Element Wash { get; init; }
}

/// <summary>The props of every `Home.Zone.{Kind}` body key whose body needs nothing but the zone.</summary>
/// <param name="Zone">The zone the body draws.</param>
/// <param name="Overlay">The overlay service the cards' menus open on.</param>
/// <param name="Controller">A shelf's pager controller when the chapter header owns the pager (the shelf is then built without its own
/// pager); null for a standalone body or the skeleton. Reference-stable for the shelf's lifetime.</param>
public sealed record HomeZoneProps(Zone Zone, IOverlayService? Overlay, ShelfController? Controller = null);

/// <summary>The props of <see cref="ViewKeys.HomeDaylist"/>: the card's zone and what its host read for it.</summary>
public sealed record HomeDaylistProps
{
    /// <summary>The daylist zone (the card is its first item); a zone with no card draws nothing.</summary>
    public required Zone Zone { get; init; }

    /// <summary>The overlay service the card's menu opens on.</summary>
    public IOverlayService? Overlay { get; init; }

    /// <summary>"Good morning, Chris · your daylist" (a subscribing read of the account's name).</summary>
    public required string Eyebrow { get; init; }

    /// <summary>The primary action names Pause: this daylist is the context playing.</summary>
    public bool Pauses { get; init; }

    /// <summary>The clock, keyed on its window and daypart by its host (a core component); null when the zone has no card.</summary>
    public Element? Clock { get; init; }
}

/// <summary>Which face of the daylist clock a <see cref="HomeDaylistClockProps"/> draws.</summary>
public enum HomeDaylistClockFace : byte
{
    /// <summary>The countdown: the ring and the bars follow the live signals; the first line is bound.</summary>
    Counting,

    /// <summary>The update-time face: the same block with nothing that moves (the ring and bars at a static fraction).</summary>
    UpdateTime,

    /// <summary>The window ended and the next edition is on its way: an indeterminate ring and bar.</summary>
    Updating,

    /// <summary>The rollover ladder gave up: a full static ring, "running late" and the "Check again" link.</summary>
    Late,
}

/// <summary>The props of <see cref="ViewKeys.HomeDaylistClock"/>. The clock host owns the timers and the signals; the view binds them.</summary>
public sealed record HomeDaylistClockProps
{
    /// <summary>The face to draw.</summary>
    public required HomeDaylistClockFace Face { get; init; }

    /// <summary>The card's current daypart (its timeline segment).</summary>
    public required Daypart Current { get; init; }

    /// <summary>The second line ("{weekday} {next daypart} arrives at HH:mm", "is up next", "is on its way"); empty draws none.</summary>
    public string Caption { get; init; } = "";

    /// <summary>The static first line (every face but <see cref="HomeDaylistClockFace.Counting"/>).</summary>
    public string Line { get; init; } = "";

    /// <summary>The live countdown text (Counting): the first line's bound count.</summary>
    public IReadSignal<string>? Countdown { get; init; }

    /// <summary>The live elapsed fraction (Counting): the ring's value.</summary>
    public FloatSignal? Elapsed { get; init; }

    /// <summary>The static elapsed fraction (UpdateTime): the ring's value.</summary>
    public float ElapsedValue { get; init; }

    /// <summary>One fill per timeline segment (Counting: live; UpdateTime: static); null for Updating and Late.</summary>
    public FloatSignal[]? Segments { get; init; }

    /// <summary>The "Check again" link's verb (Late).</summary>
    public Action? CheckAgain { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeZoneRecentGrid"/>: the plays folded into cells, the week, and the live "playing" facts.</summary>
public sealed record HomeRecentsProps
{
    /// <summary>The Recents zone.</summary>
    public required Zone Zone { get; init; }

    /// <summary>The played tiles' cells (title, detail, "when").</summary>
    public required IReadOnlyList<RecentsCell> Cells { get; init; }

    /// <summary>The week's per-day play counts (the history tile's strip).</summary>
    public required RecentsWeek Week { get; init; }

    /// <summary>Whether playback is running (the equalizer's motion).</summary>
    public required IReadSignal<bool> Playing { get; init; }

    /// <summary>The Connect device the bar says it is "Playing on", or null while playback is local; read inside a bound caption, so it follows a transfer.</summary>
    public required Func<string?> RemoteDeviceName { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeCharts"/>: where the Top 50 and Viral 50 tiles go.</summary>
public sealed record HomeChartsProps(ChartTilesModel Model);

/// <summary>The props of <see cref="ViewKeys.HomeQuickplay"/> (contract 1.28): what the quickplay strip shows. The host (`QuickplayHost`)
/// hands the pins as the band's own tracked read (the user's order, capped by the structure's <c>MaxBandPins</c>), mounts the Recents zone's
/// body through its core host (so the history reads Recents exactly as the Recently-played grid does, through
/// <see cref="ViewKeys.HomeZoneRecentGrid"/>) and passes the release-list zone as it is.</summary>
public sealed record HomeQuickplayProps
{
    /// <summary>The user's pins, in order (read tracked: a pin, an unpin or a move re-renders the strip).</summary>
    public required Func<IReadOnlyList<BandPin>> Pins { get; init; }

    /// <summary>Opens where the pins are edited; null where the structure offers no such place (the "edit" word is then not drawn).</summary>
    public Action? EditPins { get; init; }

    /// <summary>The history group: the Recents zone's body slot through its core host, or null when the facet has no Recents zone.</summary>
    public Element? History { get; init; }

    /// <summary>The "new" group's zone (the release list: "From artists you follow"), or null when the facet has none.</summary>
    public Zone? Fresh { get; init; }

    /// <summary>The overlay service the cards' menus open on.</summary>
    public IOverlayService? Overlay { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeSection"/>. The host owns the demand, the table sync and the card source.</summary>
public sealed record HomeSectionProps
{
    /// <summary>The section's total (the "N items" line); zero shows none.</summary>
    public required int Total { get; init; }

    /// <summary>The key of the grid's scroll offset.</summary>
    public required string ScrollKey { get; init; }

    /// <summary>The reveal group of the skeleton region (the host instance).</summary>
    public required object Group { get; init; }

    /// <summary>The whole section has not landed: the skeleton (the seed cards, blank) shows.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The walk concluded with nothing while online: the failure vacancy shows.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>The section landed empty: the empty vacancy shows instead of the grid.</summary>
    public required Func<bool> Empty { get; init; }

    /// <summary>The failure vacancy's Retry.</summary>
    public required Action Retry { get; init; }

    /// <summary>The reactive card source (the seed's blank cards until the whole list lands).</summary>
    public required BoundItemsSource<HomeCard> Cards { get; init; }

    /// <summary>A card's surface data (the adapter reads the overlay and whether the section is a chart at slot render time).</summary>
    public required Func<HomeCard, Controls.CardData?> Adapt { get; init; }

    /// <summary>A card was invoked (open it).</summary>
    public required Action<int, HomeCard> OnInvoked { get; init; }

    /// <summary>A card is invocable (a blank seed card is not).</summary>
    public required Func<int, HomeCard, bool> IsEnabled { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeCustomize"/>: the layout document and the verbs that change it.</summary>
public sealed record HomeCustomizeProps
{
    /// <summary>The live layout document (the ONE `HomeLayout` instance Home reads too).</summary>
    public required LayoutDoc Doc { get; init; }

    /// <summary>The drag-reorder behaviour object the host owns (its scene and render hooks are wired by the view's own render).</summary>
    public required Reorderable Reorder { get; init; }

    /// <summary>Toggle one zone's visibility.</summary>
    public required Action<LayoutZone> Toggle { get; init; }

    /// <summary>Move a row (drag drop or the keyboard's Up/Down).</summary>
    public required Action<int, int> Move { get; init; }

    /// <summary>Reset to the default layout.</summary>
    public required Action Reset { get; init; }
}
