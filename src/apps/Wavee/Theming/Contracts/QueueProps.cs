// ── Theming/Contracts/QueueProps.cs — the queue surfaces' data, verbs and props (themes plan 6.0, C7 + T7d) ─────────────────────
//
// THE QUEUE IS A CORE HOST AND A THEME PICTURE. `Queue.RailPanelHost` and `Queue.StagePaneHost` (Entities\Queue.UI.cs) keep what decides and
// holds state: the reads and their subscriptions (the queue edge, the row tables, the deck's current row, the owner, the settings epoch), the
// section split (`Shell.QueueModel.Project`), the page counters behind "Show more" (`Shell.QueuePages`), the realized slots (`QueueSlots`),
// the reorder lane (`Reorderable`: the drag, the keyboard reorder, the foreign-drop rule, `QueueMovePlan`) and the row's drag payload,
// context menu and verbs. On each render they build one of the props below and mount `Slots.View(ViewKeys.QueuePanel | QueueStagePane, props)`;
// the structure's template draws the pills or the header, the sections, the rows and the empty states.
//
// WHAT A TEMPLATE REACHES. `QueueProps` (what the queue is), `IQueueVerbs` (what a press does) and `IQueueLane` (the reorder lane and the row facts
// that need the entity tables). It never names `Shell`, `Entities`, `Playback` or `Prefs` (WVTH002): the row style and "hide track art" are the
// structure's own options (M2, M4: `ClassicOptions`), and the two navigations it may offer (`OpenPlayingFrom`, `OpenNowPlaying`) arrive on the props.
//
// A row's LAYOUT (slots, headers, extents) is structure. The template tells the lane how tall each slot is (`IQueueLane.SetMetrics`) before it
// builds the lane, because the reorder maths needs those heights EXACT.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Theming.Contracts;

/// <summary>How many upcoming rows each section holds. <see cref="Autoplay"/> is the raw count: whether those rows are
/// LISTED is the autoplay toggle's business.</summary>
public readonly record struct QueueCounts(int Queue, int NextUp, int Autoplay)
{
    public int Total => Queue + NextUp + Autoplay;

    public int Of(QueueSection section) => section switch
    {
        QueueSection.Queue => Queue,
        QueueSection.NextUp => NextUp,
        _ => Autoplay,
    };
}

/// <summary>"Playing from {Name}": the context's name, and the entity its header opens (empty: opens nothing).</summary>
public readonly record struct QueuePlayingFrom(string Name, EntityId Target);

/// <summary>The autoplay lane: the toggle, how many rows the radio tail holds, and the one it would play first (the preview).</summary>
public readonly record struct QueueAutoplay(bool Enabled, int Count, EntityRef Preview);

/// <summary>One listed row: its stable item id (0 = none), its flat index in the queue, the section and position within
/// it, and what the viewer may do with it. A surface re-locates a row by item id at invoke time, never by this index.</summary>
public readonly record struct QueueRowProps(ulong ItemId, int Index, EntityRef Ref, QueueSection Section, int Position,
    bool Removable, bool Draggable);

/// <summary>What a queue surface draws. <see cref="Rows"/> lists the upcoming rows section by section in list order: the
/// queue, the continuation, then the autoplay rows ONLY while the toggle is on.</summary>
public sealed record QueueProps(bool Viewer, EntityRef NowPlaying, QueuePlayingFrom? PlayingFrom, QueueCounts Counts,
    QueueAutoplay Autoplay, bool Shuffle, RepeatMode Repeat, QueueRowProps[] Rows)
{
    /// <summary>"Next in queue": the rows the user queued.</summary>
    public int NextInQueue => Counts.Queue;

    /// <summary>"Next up": the context's own continuation.</summary>
    public int NextUp => Counts.NextUp;

    /// <summary>Nothing upcoming in ANY section (the stage pane's "nothing up next").</summary>
    public bool Empty => Shell.QueueModel.IsEmpty(Counts);

    /// <summary>The rail's "Nothing playing" vacancy: no now-playing row and nothing queued or continuing.</summary>
    public bool NothingPlaying => Shell.QueueModel.ShowsNothingPlaying(NowPlaying.IsNone, Counts);

    /// <summary>The listed row at <paramref name="position"/> of <paramref name="section"/>; false when the section has no such row (a
    /// section's rows follow each other in <see cref="Rows"/>, the autoplay rows listed only while the toggle is on).</summary>
    public bool TryRow(QueueSection section, int position, out QueueRowProps row)
    {
        int start = section switch
        {
            QueueSection.Queue => 0,
            QueueSection.NextUp => Counts.Queue,
            _ => Counts.Queue + Counts.NextUp,
        };
        int count = section == QueueSection.Autoplay && !Autoplay.Enabled ? 0 : Counts.Of(section);
        if ((uint)position < (uint)count && start + position < Rows.Length)
        {
            row = Rows[start + position];
            return true;
        }
        row = default;
        return false;
    }
}

/// <summary>What a queue surface can ask of the queue.</summary>
public interface IQueueVerbs
{
    /// <summary>Play a row: a cursor move inside the live session (the rows the skip passes over are trimmed), never a
    /// rebuild; a viewed queue forwards it to the owner. <paramref name="index"/> and <paramref name="row"/> are only the
    /// fallback for a row with no item id (0).</summary>
    void Play(ulong itemId, int index = -1, EntityRef row = default);

    /// <summary>Remove an upcoming row. Refused on a viewed queue.</summary>
    void Remove(ulong itemId, int index, EntityRef row);

    /// <summary>Clear the user's queue ("Next in queue"). Refused on a viewed queue.</summary>
    void Clear();

    /// <summary>Move a row within its section, from position <paramref name="from"/> to <paramref name="to"/>.</summary>
    void Move(QueueSection section, int from, int to);

    /// <summary>Flip autoplay.</summary>
    void ToggleAutoplay();

    /// <summary>Set autoplay (a switch that states the value it wants).</summary>
    void SetAutoplay(bool on);

    /// <summary>One more page of <paramref name="section"/> on this surface.</summary>
    void ShowMore(QueueSection section);

    /// <summary>The queue-entry menu: header "{artists} · {section}", the strip Play now · Play next · Add to queue ·
    /// Like, the track rows, Move up / Move down where legal, Remove last. Null for a row that is gone or not a track.</summary>
    ContextMenuModel? RowMenu(ulong itemId, int index, EntityRef row, bool viewer);

    /// <summary>A foreign drop on the lane: the dropped tracks go into the user queue at <paramref name="userIndex"/>
    /// (0 = play next), capped like the player bar's drop, and the toast says how many landed.</summary>
    void DropInsert(object? payload, int userIndex);
}

/// <summary>What a row paints, read off the entity tables by the core (a template never touches them). A row whose title is not known
/// is THIN (its seed face, or two bars), never a bare uri. <see cref="Unavailable"/>: a ruled-unavailable track — the row says so (no
/// artists, no cover) instead of painting what the table still holds of it. Never a <c>default</c>: the strings are never null.</summary>
public readonly record struct QueueRowText(string Uri, string Title, string Artists, string? Art, int DurationMs, bool Thin, bool Explicit,
    bool Unavailable = false, string Album = "");

/// <summary>The reorder lane and the facts a queue surface's rows need from the core (<c>Queue.QueueSurface</c> implements it).</summary>
public interface IQueueLane
{
    /// <summary>How many slots this render realized: the section headers, the rows and the "Show more" rows, in list order.</summary>
    int SlotCount { get; }

    /// <summary>The slot at lane item <paramref name="item"/> (an index <see cref="ItemAt"/> answered).</summary>
    QueueSlot SlotAt(int item);

    /// <summary>The lane item under the live drag projection at column position <paramref name="position"/>.</summary>
    int ItemAt(int position);

    /// <summary>How many rows one "Show more" press realizes.</summary>
    int PageSize { get; }

    /// <summary>How many of <paramref name="section"/>'s rows are not realized yet.</summary>
    int Remaining(QueueSection section);

    /// <summary>The slot heights the lane's drag maths needs EXACT: the resting height of a row, and each slot's. Call it before building the
    /// lane, every render (a skin or a scale change moves it).</summary>
    void SetMetrics(float rowExtent, Func<QueueSlot, float> extentOf);

    /// <summary>Wrap a row as a lane item (a column, or the hover plate collapses to the text); a viewed queue has no lane and answers the row.</summary>
    Element Lane(int item, Element row, string key);

    /// <summary>The lane around <paramref name="body"/> (a foreign drop on it inserts into the queue).</summary>
    Element List(Element body, string key);

    /// <summary>The row's own drag, only when no lane drags for it (a viewed queue); null otherwise.</summary>
    DragSource? OwnDrag(ulong itemId, int index, EntityRef row);

    /// <summary>Attach the entry's context menu to the row (right-click, the Menu key, Shift+F10).</summary>
    Element Attach(BoxEl row, ulong itemId, int index, EntityRef rowRef);

    /// <summary>What <paramref name="row"/> paints (see <see cref="QueueRowText"/>).</summary>
    QueueRowText TextOf(EntityRef row);

    /// <summary>A duration as the player's clock writes it ("3:42"); empty is the caller's business.</summary>
    string Clock(int durationMs);

    /// <summary>The row's keyed identity: its item id, else its index.</summary>
    string RowKey(string prefix, ulong itemId, int index);
}

/// <summary>The section's one-letter tag in row and slot keys (and the queue's diagnostic line).</summary>
public static class QueueSections
{
    public static string Tag(QueueSection section) => section switch { QueueSection.Queue => "q", QueueSection.NextUp => "u", _ => "a" };
}

/// <summary>The props of <see cref="ViewKeys.QueuePanel"/>: the rail's queue panel.</summary>
public sealed record QueuePanelProps
{
    /// <summary>What the queue is (this render's reads, the rail also tracking shuffle and repeat and "Playing from").</summary>
    public required QueueProps Queue { get; init; }

    /// <summary>The reorder lane and the row facts (one per surface).</summary>
    public required IQueueLane Lane { get; init; }

    /// <summary>The queue's verbs, bound to this surface's page counters.</summary>
    public required IQueueVerbs Verbs { get; init; }

    /// <summary>The player's verbs (the shuffle and repeat pills, the now-playing card's play button).</summary>
    public required IPlayerVerbs Player { get; init; }

    /// <summary>Repeat is on (context or track), and it is the repeat-one mode (the pill's glyph).</summary>
    public required bool RepeatOn { get; init; }

    public required bool RepeatTrack { get; init; }

    /// <summary>Opens the playing context's page from the "Playing from" header; null when it has none (the header is then display-only).</summary>
    public Action? OpenPlayingFrom { get; init; }

    /// <summary>Opens where the now-playing row plays from (the context's page, else the track's album); null when there is nowhere to go.</summary>
    public Action? OpenNowPlaying { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.QueueStagePane"/>: the fullscreen stage's Up next column (the stage's accent, scale and width
/// travel in the stage's ambient context, <c>Stage.StageContext</c>).</summary>
public sealed record QueueStagePaneProps
{
    /// <summary>What the queue is.</summary>
    public required QueueProps Queue { get; init; }

    /// <summary>The reorder lane and the row facts (one per surface).</summary>
    public required IQueueLane Lane { get; init; }

    /// <summary>The queue's verbs, bound to this surface's page counters.</summary>
    public required IQueueVerbs Verbs { get; init; }

    /// <summary>The column stands in its own frame beside the lyrics (the Huge 4K stage's Up next column, C-Stage-Huge4K): it fills that frame's
    /// width at the board's scale instead of the pane region's width and the stage's queue scale. Appended (contract 1.22).</summary>
    public bool Aside { get; init; }

    /// <summary>The player's shuffle and repeat verbs (the page's "shuffle" and "repeat" words; the lit state is <see cref="QueueProps.Shuffle"/> / <see cref="QueueProps.Repeat"/>).
    /// Appended (contract 1.41); null: no words.</summary>
    public IPlayerVerbs? Player { get; init; }
}
