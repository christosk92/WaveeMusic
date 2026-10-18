// ── Shell/QueueModel.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the QUEUE as one core model (themes plan C7, X42): what a queue surface (the rail panel, the stage pane, a theme's own)
// draws, as DATA (`QueueProps` and its parts, published in `Theming\Contracts\QueueProps.cs`: where it is playing from, the
// now-playing row, the three sections' counts, the autoplay toggle and the row it would play next, shuffle, repeat, the rows with
// their section and position, and the two "nothing here" facts) — plus the paging state behind "Show more".
//
// Role: CORE
// Spec: ch 21 §6, §8, §9.5; the verbs are `IQueueVerbs` (`Theming/Contracts/QueueProps.cs`, implemented by `QueueVerbs.cs`)
//
// Before this, the surfaces read the settings key, the owner signal and the section split themselves, and each owned its
// "pages" signals. The split, the divider and the section rules stay where they have always lived (`Queue.Rules.cs`:
// `Queue.Divider`, `Queue.Split`, `QueueSlots`, `QueueMovePlan`); this projects them. A surface's row LAYOUT (slots, headers,
// extents) is still its own: that is structure, not data.

using FluentGpu.Signals;
using Wavee.Theming.Contracts;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Shell
{
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

        /// <summary>How many "played before this" rows the props carry.</summary>
        public const int HistoryCap = 50;

        /// <summary>PURE: the tracks played before the current one, most recent first, from the play log (<paramref name="log"/>, newest LAST). The log's own entry for
        /// the track on the deck (its start, compared as a row) is skipped, a track appears once (its latest play), and a row that is not a track or episode
        /// (<paramref name="refOf"/> answers none or another kind) is left out. At most <paramref name="cap"/> rows.</summary>
        public static QueueHistoryRow[] History(IReadOnlyList<PlayEntry> log, EntityRef nowPlaying, Func<string, EntityRef> refOf, int cap = HistoryCap)
        {
            if (log.Count == 0 || cap <= 0) return [];
            var rows = new List<QueueHistoryRow>(Math.Min(cap, log.Count));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = log.Count - 1; i >= 0 && rows.Count < cap; i--)
            {
                PlayEntry e = log[i];
                string uri = e.Track.Text;
                if (uri.Length == 0 || !seen.Add(uri)) continue;
                EntityRef r = refOf(uri);
                if (rows.Count == 0 && !nowPlaying.IsNone && r == nowPlaying) continue;   // the deck's own start
                if (r.Kind != EntityKind.Track && r.Kind != EntityKind.Episode) continue;
                rows.Add(new QueueHistoryRow(r, uri, e.Context.IsValid ? e.Context.Text : "", e.PlayedAtMs));
            }
            return rows.ToArray();
        }

        /// <summary>The live play log as history rows: each uri resolved to its row (the catalog's row for an unseen one).</summary>
        public static QueueHistoryRow[] ReadHistory(EntityRef nowPlaying)
        {
            return History(PlayLog.Entries, nowPlaying, static uri => Entities.Ref(EntityId.Parse(uri.AsSpan())));
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
