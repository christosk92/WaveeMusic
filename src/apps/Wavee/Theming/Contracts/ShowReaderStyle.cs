// ── Theming/Contracts/ShowReaderStyle.cs — what a structure supplies for the podcast show page (themes plan 6.1, T9c) ──────────────
//
// From `Entities\Show.UI.cs` and the rail slots of `Show.Page.cs`, `Show.Podcasts.cs` (the library pane's column is the `Library.ShowPane` view, T10c1). The structure registers ONE
// `ShowReaderStyle` under `StyleKeys.ShowReader`; the core hosts (`Show.PageHost`, the rail slot components, `Show.ReaderHost` and its
// slots) keep everything that is not a picture and call the templates while they render, so subscriptions, keys, extents and
// effect timing are what they were.
//
// WHAT THE CORE KEEPS. The reader model and its one snapshot (filter, sort, find, the visit, the ledger, the cadence, the item list), the
// persisted view prefs, the paging cursor, the toolbar's stage rule (`ShowToolbarLayout`) and its measured inputs, the selection model, the
// date index and the sticky month, the list's analytic extents (`ShowReaderRules.SeedExtent` / `ExactExtent`: the arithmetic the skeleton
// and the rows must agree on, with `CheckExtent` holding the arranged heights to it), the reveal gates (`Episode.ReaderRow` / `Episode.Reveal`
// over `ReaderLoadState`), the follow state and the primary's decision (`ShowReaderRules.PrimaryOf`), the tone memo, the trailer, the rate
// flyout's open / close, the demand. A template draws and never names `Playback`, `Entities`, `Store`, `Shell`, `Spotify` or `Prefs`
// (WVTH002): the verbs and the text the core resolved arrive in the args.
//
// A host mounted where no registry provides the key draws nothing (logged once, `ListStyleLookup`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The rail primary's decision (the core decided WHICH, the style draws): the pill, and beside a Follow the ghost play.</summary>
internal sealed class ShowPrimaryArgs
{
    /// <summary>The primary by visit (<see cref="ShowReaderRules.PrimaryOf"/>).</summary>
    public required ShowReaderRules.Primary Primary { get; init; }

    /// <summary>The ghost play beside a Follow (<see cref="ShowReaderRules.GhostOf"/>).</summary>
    public required ShowReaderRules.Primary Ghost { get; init; }

    /// <summary>Whole minutes left on listen-next's resume (the Resume label).</summary>
    public required int ResumeLeft { get; init; }

    /// <summary>The first episode's number (the "Episode N" label).</summary>
    public required int FirstNumber { get; init; }

    /// <summary>Follow (the library write).</summary>
    public required Action Follow { get; init; }

    /// <summary>Play the resume episode.</summary>
    public required Action Resume { get; init; }

    /// <summary>Play the first episode.</summary>
    public required Action First { get; init; }

    /// <summary>Play the latest episode.</summary>
    public required Action Latest { get; init; }

    /// <summary>The pill's (and the ghost's) control size: the stock Medium, or Large where the host's surface wants 40-DIP targets (the library show pane at
    /// Compact; contract 1.22).</summary>
    public ControlSize Size { get; init; } = ControlSize.Medium;

    /// <summary>The show is known to hold no episodes: its play verbs (Latest, Episode 1, Resume) have nothing to start, so a style draws them disabled, in
    /// place, never hidden (Follow stays live). Appended (contract 1.54).</summary>
    public bool Empty { get; init; }
}

/// <summary>The played ledger: the bar and its counts line, or the "progress unavailable" line.</summary>
internal sealed class ShowLedgerArgs
{
    /// <summary>The login hydrate failed (the line says so, no bar).</summary>
    public required bool Failed { get; init; }

    /// <summary>Played count.</summary>
    public required int Played { get; init; }

    /// <summary>In-progress count.</summary>
    public required int InProgress { get; init; }

    /// <summary>To-go count.</summary>
    public required int ToGo { get; init; }

    /// <summary>New-since count.</summary>
    public required int Fresh { get; init; }

    /// <summary>The rail's measure (NaN: fill the column).</summary>
    public required float Width { get; init; }

    /// <summary>The show's tone, a live read.</summary>
    public required Func<ColorF> Tone { get; init; }
}

/// <summary>The rating row: a button that opens the rate flyout.</summary>
internal sealed class ShowRatingArgs
{
    /// <summary>The average ("4.8", culture format).</summary>
    public required string Average { get; init; }

    /// <summary>"12,431 ratings" (+ " · yours 5★").</summary>
    public required string Count { get; init; }

    /// <summary>The button's accessible name ("Rate {show}").</summary>
    public required string Name { get; init; }

    /// <summary>The rail's measure (NaN: no cap).</summary>
    public required float Width { get; init; }

    /// <summary>The show's tone, a live read.</summary>
    public required Func<ColorF> Tone { get; init; }

    /// <summary>Open or close the rate flyout.</summary>
    public required Action Toggle { get; init; }

    /// <summary>The flyout's anchor handle.</summary>
    public required Action<NodeHandle> Realized { get; init; }
}

/// <summary>The badge chips on the eyebrow's line.</summary>
internal sealed class ShowBadgesArgs
{
    /// <summary>The show's flags (exclusive, explicit, video).</summary>
    public required ShowFlags Flags { get; init; }

    /// <summary>The show's tone, a live read.</summary>
    public required Func<ColorF> Tone { get; init; }
}

/// <summary>The topic words under the CTA.</summary>
internal sealed class ShowTopicsArgs
{
    /// <summary>The topics, resolved.</summary>
    public required string[] Topics { get; init; }

    /// <summary>Open a topic's search.</summary>
    public required Action<string> Open { get; init; }

    /// <summary>The rail's measure (NaN: no cap).</summary>
    public required float Width { get; init; }
}

/// <summary>The CTA's satellites: heart (hidden while Follow is the primary), trailer (when the show names one), the bell (disabled with its
/// reason), share and the more menu.</summary>
internal sealed class ShowSatellitesArgs
{
    /// <summary>The show's uri text (the heart's target).</summary>
    public required string Uri { get; init; }

    /// <summary>The show's title (the heart's name).</summary>
    public required string Name { get; init; }

    /// <summary>The heart's presence bind.</summary>
    public required Prop<bool> HeartShown { get; init; }

    /// <summary>The trailer's presence bind.</summary>
    public required Prop<bool> TrailerShown { get; init; }

    /// <summary>Play the trailer alone.</summary>
    public required Action PlayTrailer { get; init; }

    /// <summary>Copy the web link.</summary>
    public required Action Share { get; init; }

    /// <summary>The ⋯ menu, built lazily at open.</summary>
    public required Func<ContextMenuModel?> More { get; init; }
}

/// <summary>The sticky toolbar's decisions: the rung of the collapse ladder, the find's state and every bound cell and verb.</summary>
internal sealed class ShowToolbarArgs
{
    /// <summary>The rung (<see cref="ShowToolbarLayout.For"/>).</summary>
    public required ToolbarStage Stage { get; init; }

    /// <summary>The collapsed find box is open.</summary>
    public required bool Finding { get; init; }

    /// <summary>The narrow arm (the side gutter).</summary>
    public required bool Compact { get; init; }

    /// <summary>Whether the toolbar pads a trailing gutter: false where a <see cref="ShowReaderStyle.YearsInMargin"/> reader ends on the column edge (its
    /// rows run to it). Appended (contract 1.73, audit Z-OWNER).</summary>
    public bool Trailing { get; init; } = true;

    /// <summary>The filter chips (their counts are binds over the snapshot).</summary>
    public required Controls.Words.Word[] FilterWords { get; init; }

    /// <summary>The two sort words.</summary>
    public required Controls.Words.Word[] SortWords { get; init; }

    /// <summary>The status filter (0 all, 1 unplayed, 2 in progress, 3 played).</summary>
    public required Signal<int> Status { get; init; }

    /// <summary>The order (0 newest, 1 oldest).</summary>
    public required Signal<int> Order { get; init; }

    /// <summary>The in-show find (session-only).</summary>
    public required Signal<string> Find { get; init; }

    /// <summary>The count for a status: the number once progress is trusted, else "". Reads the snapshot (subscribes).</summary>
    public required Func<int, string> StatusCount { get; init; }

    /// <summary>The show's tone, a live read.</summary>
    public required Func<ColorF> Tone { get; init; }

    /// <summary>Is multi-select armed?</summary>
    public required Func<bool> IsSelecting { get; init; }

    /// <summary>Open the collapsed find.</summary>
    public required Action OpenFind { get; init; }

    /// <summary>Close it (clears the query).</summary>
    public required Action CloseFind { get; init; }

    /// <summary>Arm or disarm multi-select.</summary>
    public required Action ToggleSelecting { get; init; }

    /// <summary>The filter rail's measure.</summary>
    public required Controls.Words.Measured OnFiltersMeasured { get; init; }

    /// <summary>The sort rail's measure.</summary>
    public required Controls.Words.Measured OnSortMeasured { get; init; }

    /// <summary>The toolbar's own arranged bounds (the stage rule's input).</summary>
    public required Action<RectF> OnToolbarBounds { get; init; }

    /// <summary>The overlay the menus open through (null under the null overlay).</summary>
    public IOverlayService? Overlay { get; init; }
}

/// <summary>What the visit head paints: the core's decided visit and everything resolved about the show and its episodes' slots.</summary>
internal sealed class ShowHeadArgs
{
    /// <summary>Which head (<see cref="ShowReaderRules.HeadOf"/>).</summary>
    public required ShowReaderRules.Head Kind { get; init; }

    /// <summary>The narrow arm.</summary>
    public required bool Narrow { get; init; }

    /// <summary>The show's tone, a live read.</summary>
    public required Func<ColorF> Tone { get; init; }

    /// <summary>The door column count (<see cref="ShowReaderRules.HeadColumns"/>).</summary>
    public required int Doors { get; init; }

    /// <summary>The up-next column count.</summary>
    public required int Minis { get; init; }

    /// <summary>A serial leads with episode 1.</summary>
    public required bool Serial { get; init; }

    /// <summary>The first episode's slot (0: none).</summary>
    public required int FirstSlot { get; init; }

    /// <summary>The latest episode's slot (0: none).</summary>
    public required int LatestSlot { get; init; }

    /// <summary>The trailer's slot (0: none).</summary>
    public required int TrailerSlot { get; init; }

    /// <summary>The resume episode's slot (0: none).</summary>
    public required int ResumeSlot { get; init; }

    /// <summary>The up-next slots.</summary>
    public required int[] UpNext { get; init; }

    /// <summary>The "new since" slots.</summary>
    public required int[] Fresh { get; init; }

    /// <summary>The show's full description (html or plain, resolved), null when it has none.</summary>
    public string? About { get; init; }

    /// <summary>"128 episodes · weekly · since 2023".</summary>
    public required string Meta { get; init; }

    /// <summary>The ledger's played count (the continue section's subtitle).</summary>
    public required int Played { get; init; }

    /// <summary>The ledger's in-progress count.</summary>
    public required int InProgress { get; init; }

    /// <summary>The ledger's to-go count.</summary>
    public required int ToGo { get; init; }

    /// <summary>The weekday new episodes usually land (the caught-up line).</summary>
    public DayOfWeek? Day { get; init; }

    /// <summary>The head's episode context (the menu seam, the overlay, the tone, play); it owns no selection.</summary>
    public required Episode.RowContext RowCtx { get; init; }

    /// <summary>The structure's episode row (the new-since rows).</summary>
    public EpisodeRowStyle? EpisodeStyle { get; init; }

    /// <summary>Play the SHOW context at an episode (already through <c>Episode.Invoke</c>).</summary>
    public required Action<Episode> Play { get; init; }

    /// <summary>Play the trailer alone (already through <c>Episode.Invoke</c>).</summary>
    public required Action<Episode> PlayAlone { get; init; }

    /// <summary>"mark all played" on the new-since block.</summary>
    public required Action MarkFreshPlayed { get; init; }
}

/// <summary>The "episodes N" header.</summary>
internal sealed class ShowEpisodesHeaderArgs
{
    /// <summary>The view's count.</summary>
    public required int View { get; init; }

    /// <summary>The total.</summary>
    public required int Total { get; init; }

    /// <summary>A filter or a find is on ("3 of 128").</summary>
    public required bool Filtered { get; init; }

    /// <summary>The view is empty (the empty arm shows).</summary>
    public required bool IsEmpty { get; init; }

    /// <summary>The show itself has no episodes (else: nothing matches).</summary>
    public required bool EmptyShow { get; init; }

    /// <summary>Reset the view (a tap on "nothing matches").</summary>
    public required Action Reset { get; init; }
}

/// <summary>A month header, bound to its slot's item.</summary>
internal sealed class ShowGroupArgs
{
    /// <summary>The air above it (none right under the "episodes" head).</summary>
    public required Prop<float> Air { get; init; }

    /// <summary>The culture's month word.</summary>
    public required Prop<string> Month { get; init; }

    /// <summary>The group's month key (<see cref="DateKeys"/>), live with the recycled slot. Appended (contract 1.77, audit Z-OWNER jump list).</summary>
    public Prop<int>? MonthKey { get; init; }

    /// <summary>The reader's month jump list, which a header may open (a structure's jump list over the months the view holds). Null where the host
    /// offers none. Appended (contract 1.77, audit Z-OWNER jump list).</summary>
    public ShowJumpList? Jump { get; init; }
}

/// <summary>The show reader's month JUMP LIST (Windows Phone's long-list jump list): the months the current view holds, and the verb that brings a
/// month's header to the list's top. A structure draws the grid; the core keeps the view and the scroll. Appended (contract 1.77, audit Z-OWNER jump
/// list).</summary>
internal sealed class ShowJumpList
{
    /// <summary>The month keys (<see cref="DateKeys"/>) of the view's month groups, in the view's order (newest first unless the order is oldest).</summary>
    public required Func<int[]> Months { get; init; }

    /// <summary>Brings the header of the month with this key to the list's top (a no-op for a month the view does not hold).</summary>
    public required Action<int> Go { get; init; }
}

/// <summary>The list's foot.</summary>
internal sealed class ShowFootArgs
{
    /// <summary>The membership pages: the load-more pill shows.</summary>
    public required bool CanLoadMore { get; init; }

    /// <summary>A page is out.</summary>
    public required bool Paging { get; init; }

    /// <summary>Load the next page.</summary>
    public required Action LoadMore { get; init; }

    /// <summary>"More like this" (a core component over the reader's request).</summary>
    public required Element Similar { get; init; }
}

internal delegate Element ShowPrimaryTemplate(ShowPrimaryArgs args);
internal delegate Element ShowLedgerTemplate(ShowLedgerArgs args);
internal delegate Element ShowRatingTemplate(ShowRatingArgs args);
internal delegate Element ShowBadgesTemplate(ShowBadgesArgs args);
internal delegate Element ShowTopicsTemplate(ShowTopicsArgs args);
internal delegate Element ShowPublisherTemplate(string publisher, float width);
internal delegate Element[] ShowSatellitesTemplate(ShowSatellitesArgs args);
internal delegate Element ShowToolbarTemplate(ShowToolbarArgs args);
internal delegate Element ShowHeadShellTemplate(Func<bool> pending, Func<Element> content);
internal delegate Element ShowHeadTemplate(ShowHeadArgs args);
internal delegate Element ShowEpisodesHeaderTemplate(ShowEpisodesHeaderArgs args);
internal delegate Element ShowGroupTemplate(ShowGroupArgs args);
internal delegate Element ShowStickyMonthTemplate(IReadSignal<int> monthKey, bool narrow);
internal delegate Element ShowYearStripTemplate(int[] years, IReadSignal<int> currentMonth, Action<int> jump);
internal delegate Element ShowYearReserveTemplate();
internal delegate Element ShowFootTemplate(ShowFootArgs args);
internal delegate Element ShowSeedTemplate(EpisodeRowStyle? episodeStyle, bool narrow);

/// <summary>The structure's style for the show page (<c>StyleKeys.ShowReader</c>).</summary>
public sealed class ShowReaderStyle
{
    internal ShowReaderStyle(ShowPrimaryTemplate primary, ShowLedgerTemplate ledger, ShowRatingTemplate rating, ShowBadgesTemplate badges,
                             ShowTopicsTemplate topics, ShowPublisherTemplate publisher, ShowSatellitesTemplate satellites,
                             ShowToolbarTemplate toolbar, ShowHeadShellTemplate headShell, ShowHeadTemplate head,
                             ShowEpisodesHeaderTemplate episodesHeader, ShowGroupTemplate group, ShowStickyMonthTemplate stickyMonth,
                             ShowYearStripTemplate yearStrip, ShowYearReserveTemplate yearReserve, ShowFootTemplate foot, ShowSeedTemplate seed)
    {
        Primary = primary;
        Ledger = ledger;
        Rating = rating;
        Badges = badges;
        Topics = topics;
        Publisher = publisher;
        Satellites = satellites;
        Toolbar = toolbar;
        HeadShell = headShell;
        Head = head;
        EpisodesHeader = episodesHeader;
        Group = group;
        StickyMonth = stickyMonth;
        YearStrip = yearStrip;
        YearReserve = yearReserve;
        Foot = foot;
        Seed = seed;
    }

    /// <summary>The rail's primary (a pill, beside a Follow the ghost play).</summary>
    internal ShowPrimaryTemplate Primary { get; }

    /// <summary>The played ledger.</summary>
    internal ShowLedgerTemplate Ledger { get; }

    /// <summary>The rating row.</summary>
    internal ShowRatingTemplate Rating { get; }

    /// <summary>The badge chips.</summary>
    internal ShowBadgesTemplate Badges { get; }

    /// <summary>The topic words.</summary>
    internal ShowTopicsTemplate Topics { get; }

    /// <summary>The publisher line (the Attribution slot).</summary>
    internal ShowPublisherTemplate Publisher { get; }

    /// <summary>The CTA's keyed satellites.</summary>
    internal ShowSatellitesTemplate Satellites { get; }

    /// <summary>The sticky toolbar's body.</summary>
    internal ShowToolbarTemplate Toolbar { get; }

    /// <summary>The edge of a glyph control in the toolbar under the reader's NARROW arm (<see cref="ShowToolbarArgs.Compact"/>): a Compact target the
    /// style draws above <see cref="ShowToolbarLayout.Glyph"/> (Classic: 40, the Compact floor its board draws), which the stage rule then sums in
    /// place of the 30. 0: the stage rule's own <see cref="ShowToolbarLayout.Glyph"/>. Appended (contract 1.55, audit B6).</summary>
    internal float NarrowToolbarGlyph { get; init; }

    /// <summary>The year strip stands in the frame's OUTER MARGIN, not in a column of the reader: the reader reserves no width for it and pads no trailing
    /// gutter, so its rows (the toolbar, the head, every row) run to the column edge, and <see cref="YearStrip"/> / <see cref="YearReserve"/> place
    /// themselves past the list's trailing edge (taking no width there, or only the margin's where the reader's own edge is the window's). False: the
    /// strip's <see cref="ShowReaderRules.YearStripWidth"/> column and both gutters. Appended (contract 1.73, audit Z-OWNER).</summary>
    internal bool YearsInMargin { get; init; }

    /// <summary>The visit head's padded shell around its reveal region, and the head's shimmer source.</summary>
    internal ShowHeadShellTemplate HeadShell { get; }

    /// <summary>The visit head's content by visit.</summary>
    internal ShowHeadTemplate Head { get; }

    /// <summary>The "episodes N" header and its empty arm.</summary>
    internal ShowEpisodesHeaderTemplate EpisodesHeader { get; }

    /// <summary>A month header.</summary>
    internal ShowGroupTemplate Group { get; }

    /// <summary>The month pinned under the toolbar.</summary>
    internal ShowStickyMonthTemplate StickyMonth { get; }

    /// <summary>The year strip on the reader's right edge.</summary>
    internal ShowYearStripTemplate YearStrip { get; }

    /// <summary>The strip's reserved column when the view holds no more than one year.</summary>
    internal ShowYearReserveTemplate YearReserve { get; }

    /// <summary>The list's foot.</summary>
    internal ShowFootTemplate Foot { get; }

    /// <summary>The cold skeleton's source: the filter words, the "episodes" head and the seed rows.</summary>
    internal ShowSeedTemplate Seed { get; }
}
