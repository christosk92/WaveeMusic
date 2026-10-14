// ── Shell/QueueVerbs.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the queue's VERBS (themes plan C2 X6 + C7 X42): play a row, remove it, clear the queue, move a row, flip autoplay, show more,
// the row's menu, a foreign drop on the lane, and the player bar's DROP PLAY NEXT. A queue surface of any theme takes an
// `IQueueVerbs` and wires its rows and buttons to it; it never edits the queue, the settings or a page counter itself.
//
// Role: CORE
// Spec: ch 19 #87, G-211; ch 21 §6.4, §8, §9.5
//
// A row is identified by its stable ITEM ID and re-located in the LIVE queue when the verb runs (a render captures an id and a
// flat index, never a span), so a queue that shifted since the surface drew still edits the row the user meant. A viewed
// (remote) queue is a mirror: local edits would be overwritten, so every edit refuses it and a play is forwarded.
//
// The queue's session writes (`Queue.SkipTo`, `RemoveUpcoming`, `MoveInSection`, `ClearUserQueue`, `InsertUser`) stay in
// `Queue.Rules.cs`; this is the door in front of them.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Shell
{
    public sealed class QueueVerbs(QueuePages pages) : IQueueVerbs
    {
        /// <summary>Verbs over a private page counter, for a caller with no surface of its own.</summary>
        public static readonly QueueVerbs Default = new(new QueuePages());

        // ══ THE RULES (pure) ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Where a row a render captured lives NOW: by its item id, else by the captured index while it still holds
        /// the same ref (−1 when it is gone).</summary>
        public static int Locate(ulong itemId, int index, EntityRef row, Func<ulong, int> indexOfItem, Func<int, EntityRef> refAt)
            => itemId != 0 ? indexOfItem(itemId) : refAt(index) == row && !row.IsNone ? index : -1;

        /// <summary><see cref="Locate(ulong, int, EntityRef, Func{ulong, int}, Func{int, EntityRef})"/> in the live queue.</summary>
        public static int Locate(ulong itemId, int index, EntityRef row) => Locate(itemId, index, row, Queue.IndexOfItem, Queue.RefAt);

        /// <summary>May the menu offer edits for this row? Only on a queue we own, for a row with a stable id that is upcoming.</summary>
        public static bool RowMenuEditable(bool viewer, ulong itemId, int positionInSection)
            => !viewer && itemId != 0 && positionInSection >= 0;

        public static bool CanMoveUp(int positionInSection) => positionInSection > 0;

        public static bool CanMoveDown(int positionInSection, int sectionCount) => positionInSection + 1 < sectionCount;

        /// <summary>The section's name in the menu header.</summary>
        public static string SectionNameKey(QueueSection section) => section switch
        {
            QueueSection.Queue => Strings.Player.QueueSectionNext,
            QueueSection.Autoplay => Strings.Player.QueueSectionAutoplay,
            _ => Strings.Player.QueueSectionNextUp,
        };

        /// <summary>What a play-next drop inserts, as DATA. <see cref="Order"/> is the indices into the dropped tracks in the
        /// order they are handed to the queue seam: the FIRST <see cref="PlayerBarRules.MaxPlayNextDrop"/> tracks, LAST
        /// first (each is front-inserted, so the dropped order is the play order), skipping any that are not valid.
        /// <see cref="Truncated"/> says whether the cap actually cut anything, i.e. whether the toast reads "the first N"
        /// rather than just "N".</summary>
        public readonly record struct PlayNextPlan(int[] Order, bool Truncated)
        {
            /// <summary>How many tracks the drop inserts (the toast's number).</summary>
            public int Count => Order.Length;
        }

        /// <summary>The plan for a drop of <paramref name="valid"/>.Length tracks, <c>valid[i]</c> saying whether track
        /// <c>i</c> resolved to a real row.</summary>
        public static PlayNextPlan PlanPlayNext(ReadOnlySpan<bool> valid)
        {
            int cap = PlayerBarRules.DropInsertCount(valid.Length);
            var order = new List<int>(cap);
            for (int i = cap - 1; i >= 0; i--)
            {
                if (!valid[i]) continue;
                order.Add(i);
            }
            return new PlayNextPlan(order.ToArray(), PlayerBarRules.DropWasTruncated(valid.Length));
        }

        // ══ THE EDITS ════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A queue edit landed: publish it now (a click is a drain boundary) and let the deck re-arm what plays next.</summary>
        public static void Changed()
        {
            Entities.Publish();
            Playback.Post(Playback.Input.QueueChanged(Playback.FrameNowMs()));
        }

        public void Play(ulong itemId, int index = -1, EntityRef row = default)
        {
            int at = Locate(itemId, index, row);
            if (at < 0) return;
            EntityId context = Playback.ContextUri.Peek();
            if (QueueModel.IsViewer()) { Playback.PlayNow(Queue.RefAt(at), context, Queue.CursorOf(at)); return; }   // the reducer forwards it
            if (!Queue.SkipTo(at, Playback.Snap().Cursor, out QueueCursor cursor)) return;
            Entities.Publish();
            Playback.PlayNow(Queue.RefAt(cursor), context, cursor);
        }

        public void Remove(ulong itemId, int index, EntityRef row)
        {
            int at = Locate(itemId, index, row);
            if (at >= 0 && !QueueModel.IsViewer() && Queue.RemoveUpcoming(at, Playback.Snap().Cursor)) Changed();
        }

        public void Clear()
        {
            if (!QueueModel.IsViewer() && Queue.ClearUserQueue(Playback.Snap().Cursor) > 0) Changed();
        }

        public void Move(QueueSection section, int from, int to)
        {
            if (!QueueModel.IsViewer() && Queue.MoveInSection(section, from, to, Playback.Snap().Cursor)) Changed();
        }

        public void ToggleAutoplay() => SetAutoplay(!QueueModel.AutoplayEnabled());

        public void SetAutoplay(bool on) => Platform.Settings.Set(Platform.Keys.AutoplayEnabled, on);

        public void ShowMore(QueueSection section) => pages.ShowMore(section);

        public ContextMenuModel? RowMenu(ulong itemId, int index, EntityRef row, bool viewer)
        {
            int at = Locate(itemId, index, row);
            if (at < 0 || row.Kind != EntityKind.Track || new Track(row.Slot) is not { IsValid: true } track) return null;
            var rows = Queue.Rows;
            int pos = Queue.PositionInSection(rows, Queue.Divider(rows, viewer ? -1 : Playback.Snap().Cursor.Index), at, out int count);
            QueueSection section = Queue.SectionOf(rows[at]);
            ulong id = rows[at].ItemId;
            var ctx = new ActionContext(ActionTarget.ForQueueEntry(track, (long)id), Actions.Services);

            var strip = new List<AppBarCommand>(4) { new AppBarCommand(Icons.Play, Loc.Get(Strings.Menu.PlayNow), () => Play(itemId, index, row)) };
            ReadOnlySpan<ActionId> verbs = [ActionId.PlayNext, ActionId.AddToQueue, ActionId.ToggleLike];
            for (int v = 0; v < verbs.Length; v++)
                if (Actions.Menu.Command(verbs[v], in ctx) is { } command) strip.Add(command);

            var list = new List<MenuFlyoutItem>(10);
            Actions.Menu.AddRows(list, in ctx, [ActionId.AddToPlaylist, ActionId.GoToAlbum, ActionId.GoToArtist]);
            if (Actions.Menu.Share(in ctx) is { } share) list.Add(share);
            Actions.Menu.AddRows(list, in ctx, [ActionId.ViewCredits, ActionId.GoToSongRadio]);
            bool editable = RowMenuEditable(viewer, id, pos);
            if (editable && (CanMoveUp(pos) || CanMoveDown(pos, count)))
            {
                Actions.Menu.OpenGroup(list);
                Actions.Menu.AddMoveRows(list, CanMoveUp(pos) ? () => Move(section, pos, pos - 1) : null,
                                         CanMoveDown(pos, count) ? () => Move(section, pos, pos + 1) : null);
            }
            if (editable)
            {
                Actions.Menu.OpenGroup(list);
                list.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.RemoveFromQueue), ActionIcons.Resolve(ActionIcons.Remove), true,
                                            () => Remove(itemId, index, row)));
            }
            string name = Loc.Get(SectionNameKey(section));
            string artists = Entities.Strings.Resolve(track.ArtistLineId);
            return new ContextMenuModel(strip, list,
                Actions.Menu.Header(Controls.ArtUrl(track.ImageId), track.Title, artists.Length > 0 ? artists + " · " + name : name));
        }

        public void DropInsert(object? payload, int userIndex)
        {
            if (Drag.Unwrap(payload) is { CanCopyTracks: true } p) _ = DropInsertAsync(p, userIndex);
        }

        static async Task DropInsertAsync(DragPayload payload, int userIndex)
        {
            Track[] tracks;
            try { tracks = await payload.ResolveTracksAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("queue", "drop could not resolve its tracks", ex); return; }
            if (tracks.Length == 0) return;
            Playback.ToUi(() =>
            {
                int cap = PlayerBarRules.DropInsertCount(tracks.Length), n = 0;
                var refs = new EntityRef[cap];
                for (int i = 0; i < cap; i++) if (tracks[i].IsValid) refs[n++] = new EntityRef(EntityKind.Track, tracks[i].Slot);
                if (QueueModel.IsViewer())
                {
                    // G-248: another device plays — the rows go to its queue through Connect, not into a queue we do not own.
                    if (!Playback.QueueToOwner(refs.AsSpan(0, n), next: userIndex == 0))
                    { Notify.Say(Loc.Get(Strings.Detail.QueueUnavailable), InfoBarSeverity.Warning); return; }
                    Notify.Say(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), InfoBarSeverity.Success);
                    return;
                }
                int added = Queue.InsertUser(refs.AsSpan(0, n), Playback.Snap().Cursor, userIndex);
                if (added == 0) return;
                Changed();
                Notify.Say(PlayerBarRules.DropWasTruncated(tracks.Length)
                    ? Strings.Detail.AddedFirstToQueue(Strings.Detail.SongCount(added))
                    : Strings.Detail.AddedToQueue(Strings.Detail.SongCount(added)), InfoBarSeverity.Success);
            });
        }

        // ══ THE PLAYER BAR'S DROP (play next) ════════════════════════════════════════════════════════════════════════

        /// <summary>Fire-and-forget entry for a drop handler: resolve the payload's tracks and deposit them.</summary>
        public static void DropPlayNext(DragPayload payload) => _ = DropPlayNextAsync(payload);

        /// <summary>The bar's one deposit: resolve the payload's tracks (cold: after the drop, never during the drag) and
        /// FRONT-insert them, last first, so the dropped order is the play order. Said out loud with a success toast.</summary>
        public static async Task DropPlayNextAsync(DragPayload payload)
        {
            Track[] tracks;
            try { tracks = await payload.ResolveTracksAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("playerbar", "play-next drop could not resolve its tracks", ex); return; }
            if (tracks.Length == 0) return;
            Playback.ToUi(() =>
            {
                // G-211: nothing must answer with silence: a drop with no queue seam behind it still tells the user why,
                // instead of the drag simply evaporating.
                if (Actions.Services.PlayNext is not { } playNext)
                {
                    Notify.Say(Loc.Get(Strings.Detail.QueueUnavailable), InfoBarSeverity.Warning);
                    return;
                }
                // The batch cap: take the FIRST MaxPlayNextDrop tracks of the drop (in drop order), still front-inserted
                // last-first so the kept prefix plays in the order it was dropped.
                var valid = new bool[tracks.Length];
                for (int i = 0; i < valid.Length; i++) valid[i] = tracks[i].IsValid;
                var plan = PlanPlayNext(valid);
                foreach (int i in plan.Order) playNext(tracks[i].Uri);
                if (plan.Count == 0) return;
                Notify.Say(plan.Truncated
                        ? Strings.Detail.AddedFirstToQueue(Strings.Detail.SongCount(plan.Count))
                        : Strings.Detail.AddedToQueue(Strings.Detail.SongCount(plan.Count)),
                    InfoBarSeverity.Success);
            });
        }
    }
}
