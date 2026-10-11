// ── Shell/QueueModel.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the QUEUE as one core model (themes plan C7, X42): what a queue surface (the rail panel, the stage pane, a theme's own)
// draws, as DATA — where it is playing from, the now-playing row, the three sections' counts, the autoplay toggle and the
// row it would play next, shuffle, repeat, the rows with their section and position, and the two "nothing here" facts — plus
// the paging state behind "Show more".
//
// Role: CORE
// Spec: ch 21 §6, §8, §9.5; the verbs are `Shell.IQueueVerbs` (`QueueVerbs.cs`)
//
// Before this, the surfaces read the settings key, the owner signal and the section split themselves, and each owned its
// "pages" signals. The split, the divider and the section rules stay where they have always lived (`Queue.Rules.cs`:
// `Queue.Divider`, `Queue.Split`, `QueueSlots`, `QueueMovePlan`); this projects them. A surface's row LAYOUT (slots, headers,
// extents) is still its own: that is structure, not data.

using System;

using FluentGpu.Signals;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Shell
{
    // ══ THE DATA ═════════════════════════════════════════════════════════════════════════════════════════════════════

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
        public bool Empty => QueueModel.IsEmpty(Counts);

        /// <summary>The rail's "Nothing playing" vacancy: no now-playing row and nothing queued or continuing.</summary>
        public bool NothingPlaying => QueueModel.ShowsNothingPlaying(NowPlaying.IsNone, Counts);
    }

    // ══ THE PAGING ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"Show more": how many pages of each section are realized (a visual pagination; the queue itself is whole).
    /// One per surface. The rail panel pages its three sections separately; the stage pane's continuous list pages them
    /// together (<paramref name="unified"/>).</summary>
    public sealed class QueuePages
    {
        readonly Signal<int> _queue = new(1);
        readonly Signal<int> _nextUp;
        readonly Signal<int> _autoplay;

        public QueuePages(bool unified = false)
        {
            _nextUp = unified ? _queue : new Signal<int>(1);
            _autoplay = unified ? _queue : new Signal<int>(1);
        }

        /// <summary>The page count of <paramref name="section"/> (subscribe with <c>.Value</c>).</summary>
        public Signal<int> Of(QueueSection section) => section switch
        {
            QueueSection.Queue => _queue,
            QueueSection.NextUp => _nextUp,
            _ => _autoplay,
        };

        /// <summary>One more page of <paramref name="section"/> (of every section, when unified).</summary>
        public void ShowMore(QueueSection section)
        {
            var pages = Of(section);
            pages.Value = pages.Peek() + 1;
        }

        /// <summary>Back to page one everywhere: the playing context changed.</summary>
        public void Reset()
        {
            _queue.Value = 1;
            if (!ReferenceEquals(_nextUp, _queue)) _nextUp.Value = 1;
            if (!ReferenceEquals(_autoplay, _queue)) _autoplay.Value = 1;
        }
    }

    // ══ THE MODEL ════════════════════════════════════════════════════════════════════════════════════════════════════

    public static class QueueModel
    {
        /// <summary>Is the autoplay toggle on? A peek of the setting: a reactive caller subscribes
        /// <c>Platform.SettingsChanged</c> itself.</summary>
        public static bool AutoplayEnabled() => Platform.Settings.Get(Platform.Keys.AutoplayEnabled);

        /// <summary>A remote device owns playback: the list is its MIRROR, and local edits would be overwritten.</summary>
        public static bool IsViewer() => Playback.OwnerSignal.Peek() == Playback.Owner.Foreign;

        /// <summary>The "Playing from" header, or null when the context has no name yet (never a header with no name).</summary>
        public static QueuePlayingFrom? PlayingFromOf(string? name, EntityId target)
            => name is { Length: > 0 } ? new QueuePlayingFrom(name, target) : null;

        /// <summary>The "Playing from" header of the live context: its name from the entity it names, and the entity its
        /// header opens. Reactive (it reads the context's table).</summary>
        public static QueuePlayingFrom? PlayingFromOf(EntityId context, in Queue.ContextWire wire)
            => PlayingFromOf(Queue.ContextName(context, in wire), Queue.ContextTarget(context, in wire));

        /// <summary>Nothing upcoming in any section.</summary>
        public static bool IsEmpty(in QueueCounts counts) => counts.Queue == 0 && counts.NextUp == 0 && counts.Autoplay == 0;

        /// <summary>The rail's vacancy: no now-playing row and nothing queued or continuing (autoplay alone does not keep
        /// the panel from reading "Nothing playing").</summary>
        public static bool ShowsNothingPlaying(bool nowPlayingIsNone, in QueueCounts counts)
            => nowPlayingIsNone && counts.Queue == 0 && counts.NextUp == 0;

        /// <summary>PURE: project a queue into what a surface draws. <paramref name="edges"/> is the queue's payload,
        /// <paramref name="refAt"/> resolves a flat index to its row, <paramref name="cursorIndex"/> is the reducer's cursor
        /// (ignored for a viewed remote queue, whose divider is its NowPlaying row).</summary>
        public static QueueProps Project(ReadOnlySpan<QueueEdge> edges, Func<int, EntityRef> refAt, int cursorIndex, bool viewer,
            bool autoplayEnabled, bool shuffle, RepeatMode repeat, EntityRef nowPlaying, QueuePlayingFrom? playingFrom)
        {
            int divider = Queue.Divider(edges, viewer ? -1 : cursorIndex);
            var index = new int[edges.Length];
            Queue.Split(edges, divider, index, out int user, out int next, out int auto);

            int listed = user + next + (autoplayEnabled ? auto : 0);
            var rows = new QueueRowProps[listed];
            for (int k = 0; k < listed; k++)
            {
                int i = index[k];
                QueueSection section = k < user ? QueueSection.Queue : k < user + next ? QueueSection.NextUp : QueueSection.Autoplay;
                int start = section == QueueSection.Queue ? 0 : section == QueueSection.NextUp ? user : user + next;
                ulong itemId = edges[i].ItemId;
                rows[k] = new QueueRowProps(itemId, i, refAt(i), section, k - start,
                    QueueRowRules.Removable(viewer, itemId), QueueRowRules.RowDrags(viewer));
            }
            EntityRef preview = auto > 0 ? refAt(index[user + next]) : default;
            return new QueueProps(viewer, nowPlaying, playingFrom, new QueueCounts(user, next, auto),
                new QueueAutoplay(autoplayEnabled, auto, preview), shuffle, repeat, rows);
        }

        /// <summary>The live queue as props, with the subscriptions of a render.</summary>
        public static QueueProps Read()
        {
            var current = Playback.Current.Value;
            var context = Playback.ContextUri.Value;
            var wire = Playback.ContextLabel.Value;
            bool viewer = Playback.OwnerSignal.Value == Playback.Owner.Foreign;
            _ = Entities.Current.Edges.Queue.Changed.Value;
            _ = Platform.SettingsChanged.Value;
            return Project(Queue.Rows, static i => Queue.RefAt(i), viewer ? -1 : Playback.Snap().Cursor.Index, viewer,
                AutoplayEnabled(), Playback.Shuffle.Value, Playback.Repeat.Value, current, PlayingFromOf(context, in wire));
        }
    }
}
