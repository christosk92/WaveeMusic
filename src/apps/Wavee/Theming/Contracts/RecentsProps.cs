// ── Theming/Contracts/RecentsProps.cs — the props of the Recents page view (themes plan 6.0, T10c2) ─────────────────────────────────
//
// `Recents.PageView` (a `[CoreHost]`) keeps everything that decides and hands the view the VALUES and VERBS it draws. The page's slots (the bound list's row
// slot, the pinned day band, the rail, the calendar and its month cards) are COMPONENTS the view owns: they read the host's live state through
// `IRecentsHost` (a stable object, one per page), exactly as the old nested components read the page, so a read inside a slot's render subscribes THAT slot,
// never the page.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.RecentsPage"/>. The host builds one per render it makes (the page tree used to be rebuilt then), so it never compares
/// equal to another instance.</summary>
public sealed record RecentsPageProps
{
    /// <summary>The page's live state and verbs (the same object for the page's whole life).</summary>
    public required IRecentsHost Host { get; init; }

    /// <summary>The masthead's second line: the summary of the adopted shape ("" while pending).</summary>
    public required string Summary { get; init; }

    /// <summary>The shape's epoch at this render: the pivot strip is keyed on it (a pivot switch re-renders the live strip).</summary>
    public required int ShapeEpoch { get; init; }

    /// <summary>The relation has not answered and has not failed: the body shows the derived shimmer.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The relation failed: the body shows the error vacancy.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>The error vacancy's Retry (re-asks the relation).</summary>
    public required Action Retry { get; init; }

    /// <summary>The masthead's Overview button: zoom out to the calendar.</summary>
    public required Action OpenOverview { get; init; }

    /// <summary>The page root's key handler (Escape zooms back in).</summary>
    public required Action<KeyEventArgs> OnKeyDown { get; init; }

    /// <summary>The page accent the page PROVIDES to its subtree (`Design.AccentCtx`).</summary>
    public required IReadSignal<Design.PageAccent>? PageAccent { get; init; }

    /// <summary>The 0×0 leaf that owns the cover-grading watch and publishes the page accent and the shell wash (core).</summary>
    public required Element AccentBinder { get; init; }

    /// <summary>The 0×0 leaf that demands the playlist bylines' owners (core).</summary>
    public required Element OwnerDemand { get; init; }

    /// <inheritdoc />
    public bool Equals(RecentsPageProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>A card's facts, read LIVE off the target's columns (no copied strings). <see cref="Known"/> is the identity gate: until it holds the row paints
/// geometry, never an invented title.</summary>
public readonly record struct RecentsFacts(bool Known, string Title, string? Subtitle, string? Cover)
{
    /// <summary>Nothing is known yet.</summary>
    public static readonly RecentsFacts Unknown = new(false, "", null, null);
}

/// <summary>Everything that must agree with everything else, as ONE reference: the snapshot, the pivot's cut, the morph flags, the sections, the calendar and the
/// STATEFUL measured layout. The per-row string caches are filled lazily by the host on first bind and dropped with the shape.</summary>
public sealed class RecentsShape
{
    /// <summary>The adopted snapshot.</summary>
    public readonly RecentsSnapshot Rows;
    /// <summary>The pivot's cut: display order over the snapshot.</summary>
    public readonly int[] Display;
    /// <summary>Whether the row is the first occurrence of its target (the dormant shared-element key).</summary>
    public readonly bool[] Morphable;
    /// <summary>The flat grouped projection (day headers and rows).</summary>
    public readonly RecentsSections Sections;
    /// <summary>The calendar overview's model.</summary>
    public readonly RecentsCalendar Calendar;
    /// <summary>The stateful measured layout the list and the rail read.</summary>
    public readonly GroupedListVirtualLayout Layout;
    /// <summary>The UTC offset the shape was grouped at.</summary>
    public readonly TimeSpan BuiltOffset;
    /// <summary>Built from an ANSWERED relation (false for the pending seed): the skeleton's gate.</summary>
    public readonly bool Loaded;
    /// <summary>The masthead's summary line.</summary>
    public readonly string Summary;
    internal readonly string?[] When, Uri, Morph, Sub;
    internal readonly string?[] SubOwner;

    public RecentsShape(RecentsSnapshot rows, int[] display, bool[] morphable, RecentsSections sections, RecentsCalendar calendar,
                          GroupedListVirtualLayout layout, TimeSpan builtOffset, bool loaded, string summary)
    {
        Rows = rows; Display = display; Morphable = morphable; Sections = sections; Calendar = calendar;
        Layout = layout; BuiltOffset = builtOffset; Loaded = loaded; Summary = summary;
        int n = rows.Count;
        When = new string?[n]; Uri = new string?[n]; Morph = new string?[n]; Sub = new string?[n]; SubOwner = new string?[n];
    }

    /// <summary>The midnight relabel: the same list and layout, new day words and a fresh "when" cache.</summary>
    internal RecentsShape Relabeled(RecentsSections sections, string summary)
        => new(Rows, Display, Morphable, sections, Calendar, Layout, BuiltOffset, Loaded, summary);
}

/// <summary>The live state and verbs of the Recents page, read by the view's slot components. Every member is the host's; a read inside a render subscribes
/// the reading component.</summary>
public interface IRecentsHost
{
    // ── the shape and the page's clocks ──

    /// <summary>The adopted shape (not a signal: its changes ride <see cref="ShapeEpoch"/> and <see cref="ShapeSignal"/>).</summary>
    RecentsShape ShapeNow { get; }
    /// <summary>The skeleton's seed shape (the real day headers and the row's real geometry over the pending seed rows).</summary>
    RecentsShape PendingShape { get; }
    /// <summary>The adopted shape as a signal (the bound list projects it).</summary>
    IReadSignal<RecentsShape> ShapeSignal { get; }
    /// <summary>Moves when the same list gets new words (midnight).</summary>
    IReadSignal<int> Epoch { get; }
    /// <summary>Moves when a different list was published (the list, the rail, the pinned band, the pivots and the calendar remount).</summary>
    IReadSignal<int> ShapeEpoch { get; }
    /// <summary>The selected pivot token (null: All).</summary>
    IReadSignal<string?> Chip { get; }
    /// <summary>The open accordion row's item id (empty: none).</summary>
    IReadSignal<StringId> ExpandedRow { get; }
    /// <summary>The flat index of the pinned day band's header (-1: none yet).</summary>
    IReadSignal<int> StickyHeader { get; }
    /// <summary>Moves on a real measured-extent delta of the list's layout (the rail's memo key).</summary>
    IReadSignal<int> RailMeasuredVersion { get; }
    /// <summary>The page accent (ink and fill).</summary>
    IReadSignal<Design.PageAccent> Accent { get; }
    /// <summary>The semantic zoom's state (the control writes it: it is the controlled signal).</summary>
    Signal<bool> IsZoomedOut { get; }
    /// <summary>The day the calendar readout shows.</summary>
    IReadSignal<DateOnly> CalendarDay { get; }
    /// <summary>The culture the shape was grouped in.</summary>
    CultureInfo Culture { get; }
    /// <summary>The local "today".</summary>
    DateOnly Today { get; }
    /// <summary>The page's scroll-memory prefix (the tab's scope).</summary>
    string ScrollScope { get; }

    // ── engine controls the host owns ──

    /// <summary>The list's controller.</summary>
    ItemsViewController ListController { get; }
    /// <summary>The calendar grid's controller.</summary>
    ItemsViewController CalendarController { get; }
    /// <summary>The annotated rail's controller (its handle is the list's scroll handle).</summary>
    AnnotatedScrollBarController ScrollController { get; }
    /// <summary>The semantic zoom's controller.</summary>
    SemanticZoomController ZoomController { get; }

    // ── bound props and pre-created delegates (nothing on a hot path allocates a closure) ──

    /// <summary>The accent's ink as a bound colour.</summary>
    Prop<ColorF> AccentInkProp { get; }
    /// <summary>The accent's fill as a bound colour.</summary>
    Prop<ColorF> AccentFillProp { get; }
    /// <summary>The pinned band's push as a bound transform.</summary>
    Prop<Affine2D> StickyTransform { get; }
    /// <summary>The heat's paint for a level 0..5 as a bound colour.</summary>
    Prop<ColorF> DensityFillProp(int level);
    /// <summary>The sticky watch over the list's scroll handle.</summary>
    Action WatchStickyAction { get; }
    /// <summary>Wheel over the pinned band is forwarded to the list.</summary>
    Action<WheelEventArgs> ForwardWheelAction { get; }
    /// <summary>The calendar's pointer exit resets the readout to today.</summary>
    Action ResetCalendarDayAction { get; }
    /// <summary>Semantic zoom: the calendar item a list index zooms out to.</summary>
    Func<int, int> MapInToOutFunc { get; }
    /// <summary>Semantic zoom: the list index a calendar item zooms in to.</summary>
    Func<int, int> MapOutToInFunc { get; }
    /// <summary>The recycle pool of a flat item (one pool per arm).</summary>
    Func<int, int> ContentTypeFunc { get; }
    /// <summary>The list's invoke handler (a row toggles, plays or opens).</summary>
    Action<int, RecentsFlatItem> InvokeFlatAction { get; }
    /// <summary>The list's type-ahead text.</summary>
    Func<int, RecentsFlatItem, string> TextForFunc { get; }
    /// <summary>The rail's month labels.</summary>
    Func<AnnotatedScrollBarLabel[]> RailLabelsFunc { get; }
    /// <summary>The rail's tick offsets.</summary>
    Func<float[]> RailTicksFunc { get; }
    /// <summary>The rail's detail label at an offset.</summary>
    Func<float, AnnotatedScrollBarLabel?> RailDetailFunc { get; }

    // ── verbs ──

    /// <summary>Select a pivot (a client-side re-cut of the adopted snapshot).</summary>
    void SelectPivot(string? token);
    /// <summary>Open or close a row's drawer.</summary>
    void ToggleExpanded(int row);
    /// <summary>Open a row's context (a playable plays, Liked Songs is its own route).</summary>
    void Open(int row);
    /// <summary>The cover FAB: a track or episode plays itself, anything else starts as a context from the top.</summary>
    void Play(int row);
    /// <summary>A row's container menu (null for a kind with none).</summary>
    ContextMenuModel? MenuFor(int row);
    /// <summary>A row's resource drag chip (null for a kind with none).</summary>
    DragPayload? DragPayloadFor(int row);
    /// <summary>Play a drawer member as its own context.</summary>
    void PlayContext(EntityId id);
    /// <summary>A day header was invoked: zoom out to the calendar at that day.</summary>
    void InvokeDay(int dayIndex);
    /// <summary>A calendar cell was invoked: zoom in at the month (a calendar docked beside the list brings the list to the day instead: nothing is zoomed out).</summary>
    void JumpIn(DateOnly date, int monthIndex);
    /// <summary>A calendar cell took or lost focus.</summary>
    void FocusDay(bool focused, DateOnly date);
    /// <summary>The hovered calendar day changed.</summary>
    void HoverDay(DateOnly date);

    // ── what a target says ──

    /// <summary>A target's facts, read live off its columns.</summary>
    RecentsFacts FactsOf(EntityRef target);
    /// <summary>A member's cover for the Saved stack (null: the neutral tile).</summary>
    string? CoverOf(EntityRef target);
    /// <summary>Subscribe the calling slot to what row <paramref name="row"/> paints (the target's table, the owners, the saved members).</summary>
    void SubscribeRow(RecentsShape shape, int row);
    /// <summary>A row's uri text (cached on the shape).</summary>
    string UriOf(RecentsShape shape, int row, EntityRef target);
    /// <summary>A row's "played at" stamp (cached on the shape).</summary>
    string WhenOf(RecentsShape shape, int row);
    /// <summary>The dormant shared-element key, on the FIRST occurrence of a target only.</summary>
    string? MorphOf(RecentsShape shape, int row, EntityRef target, string uri);
    /// <summary>"Played N tracks · owner": the phrase from the wire's declared count.</summary>
    string SubtitleOf(RecentsShape shape, int row, in RecentsEdge edge, string? owner);
    /// <summary>The "N items" phrase of a day.</summary>
    string CountLabel(int count);
    /// <summary>A drawer member's "played at" stamp.</summary>
    string PlayedAt(long playedAtMs);
    /// <summary>The ONE readout the band and the cell tooltip share.</summary>
    string CalendarReadout(RecentsShape shape, DateOnly date);
    /// <summary>A calendar cell's tooltip.</summary>
    string CalendarTooltip(RecentsShape shape, DateOnly date);
}
