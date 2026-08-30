using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Wave 4C — the queue panel's OPTIMISTIC reorder (Features/Player/QueueOrder.cs) against the AUTHORITATIVE session op
// it mirrors (PlaybackSession.MoveItem / InsertUserQueue). The panel used to apply a two-element SWAP while the session
// removed + inserted: identical for the ±1 context-menu verbs it was written for, wrong for every multi-slot drag —
// the row appeared to land somewhere the server never put it, then snapped. These pin both halves of that equivalence.
public class QueueOrderTests
{
    static Track T(string id) => new(id, "spotify:track:" + id, "T-" + id,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    static QueueEntry E(ulong id, QueueBucket bucket = QueueBucket.UserQueue,
                        QueueProvider provider = QueueProvider.Queue)
        => new(new QueueItemId(id), "i" + id, T("t" + id), bucket, provider, provider == QueueProvider.Autoplay);

    static IReadOnlyList<QueueEntry> Q(params ulong[] ids) => ids.Select(i => E(i)).ToList();

    static string Ids(IReadOnlyList<QueueEntry> q) => string.Join(",", q.Select(e => e.ItemId.Value));

    // ── remove + insert, not swap ────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Move_MultipleSlots_ShiftsEveryRowBetween()
    {
        var q = Q(1, 2, 3, 4, 5);

        Assert.Equal("2,3,1,4,5", Ids(QueueOrder.Move(q, q, 0, 2)));    // down: 1 lands AT index 2, 2 and 3 shift up
        Assert.Equal("1,5,2,3,4", Ids(QueueOrder.Move(q, q, 4, 1)));    // up: 5 lands at 1, 2..4 shift down
        Assert.Equal("5,1,2,3,4", Ids(QueueOrder.Move(q, q, 4, 0)));
    }

    [Fact]
    public void Move_AdjacentSlot_IsTheOldSwap()
    {
        var q = Q(1, 2, 3, 4);
        // The context menu's ±1 verbs must behave EXACTLY as before the rewrite: for adjacent rows a remove+insert
        // and a swap are the same permutation.
        for (int i = 0; i < q.Count; i++)
        {
            foreach (int delta in new[] { -1, 1 })
            {
                int to = i + delta;
                if ((uint)to >= (uint)q.Count) continue;
                var swapped = new List<QueueEntry>(q);
                (swapped[i], swapped[to]) = (swapped[to], swapped[i]);
                Assert.Equal(Ids(swapped), Ids(QueueOrder.Move(q, q, i, to)));
            }
        }
    }

    [Fact]
    public void Move_MatchesTheSessionOpItMirrors()
    {
        // The optimistic order and the session's own MoveItem must agree for every (from, to) — that agreement is the
        // whole point of the rewrite, and it is what stops the drag from "snapping back" on the authoritative push.
        for (int from = 0; from < 5; from++)
        {
            for (int to = 0; to < 5; to++)
            {
                var s = new PlaybackSession();
                s.SetContext("spotify:playlist:p", new[] { new QueuedTrack(T("cur"), "u-cur", "context", null, QueueRowKind.Playable) }, 0);
                s.EnqueueUser(Enumerable.Range(1, 5)
                    .Select(i => new QueuedTrack(T("t" + i), "", "queue", null, QueueRowKind.Playable)).ToList());
                var section = s.Snapshot().UserQueue;

                var authoritative = s.MoveItem(section[from].ItemId, to)?.UserQueue ?? section;
                var optimistic = QueueOrder.Move(section, section, from, to);

                Assert.Equal(string.Join(",", authoritative.Select(e => e.Track.Id)),
                             string.Join(",", optimistic.Select(e => e.Track.Id)));
            }
        }
    }

    // ── the section is a SUBSEQUENCE of the flat snapshot: other buckets never move ──────────────────────────────────
    [Fact]
    public void Move_RewritesOnlyItsOwnSectionsPositions()
    {
        var user = new[] { E(1), E(2), E(3) };
        var flat = new List<QueueEntry>
        {
            E(90, QueueBucket.NowPlaying, QueueProvider.Context),
            user[0],
            E(91, QueueBucket.NextUp, QueueProvider.Context),   // interleaved on purpose: positions, not a block
            user[1],
            user[2],
            E(92, QueueBucket.NextUp, QueueProvider.Autoplay),
        };

        var moved = QueueOrder.Move(flat, user, 2, 0);

        Assert.Equal("90,3,91,1,2,92", Ids(moved));   // only the user rows' own slots were rewritten
        Assert.Equal("90,1,91,2,3,92", Ids(flat));    // and the input is untouched
    }

    [Fact]
    public void Move_NoOpsAreTheInputInstance()
    {
        var q = Q(1, 2, 3);
        Assert.Same(q, QueueOrder.Move(q, q, 1, 1));
        Assert.Same(q, QueueOrder.Move(q, q, 7, 0));                       // out of range
        Assert.Same(q, QueueOrder.Move(q, Array.Empty<QueueEntry>(), 0, 1));
        // A section row the snapshot no longer carries: the authoritative push is the only honest answer.
        Assert.Same(q, QueueOrder.Move(q, new[] { E(1), E(42) }, 0, 1));
    }

    [Fact]
    public void Move_ClampsTheTargetSlot()
    {
        var q = Q(1, 2, 3);
        Assert.Equal("2,3,1", Ids(QueueOrder.Move(q, q, 0, 99)));
        Assert.Equal("3,1,2", Ids(QueueOrder.Move(q, q, 2, -5)));
    }

    // ── identity: the session id when both rows carry one, else the derived entry id ─────────────────────────────────
    [Fact]
    public void Remove_DropsByIdentity()
    {
        var q = Q(1, 2, 3);
        Assert.Equal("1,3", Ids(QueueOrder.Remove(q, q[1])));
        Assert.Equal("1,2,3", Ids(QueueOrder.Remove(q, E(9))));

        var idless = new QueueEntry(QueueItemId.None, "e7", T("x"), QueueBucket.UserQueue, QueueProvider.Queue, false);
        var mixed = new List<QueueEntry> { q[0], idless };
        Assert.Equal(1, QueueOrder.Remove(mixed, idless).Count);
    }

    [Fact]
    public void Positions_AreAscendingAndEmptyWhenTheSnapshotMoved()
    {
        var user = new[] { E(1), E(2) };
        var flat = new List<QueueEntry> { E(90, QueueBucket.NowPlaying, QueueProvider.Context), user[0], user[1] };
        Assert.Equal(new[] { 1, 2 }, QueueOrder.Positions(flat, user));
        Assert.Empty(QueueOrder.Positions(flat, new[] { user[0], E(77) }));
    }

    // ── the session primitive behind an insert-at-slot drop ──────────────────────────────────────────────────────────
    [Fact]
    public void InsertUserQueue_PutsTheBlockAtTheSlot_AndClamps()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", new[] { new QueuedTrack(T("cur"), "u-cur", "context", null, QueueRowKind.Playable) }, 0);
        s.EnqueueUser(new[] { "a", "b", "c" }
            .Select(i => new QueuedTrack(T(i), "", "queue", null, QueueRowKind.Playable)).ToList());

        var mid = s.InsertUserQueue(new[] { new QueuedTrack(T("x"), "", "queue", null, QueueRowKind.Playable),
                                            new QueuedTrack(T("y"), "", "queue", null, QueueRowKind.Playable) }, 2);
        Assert.Equal(new[] { "a", "b", "x", "y", "c" }, mid.UserQueue.Select(e => e.Track.Id));

        var head = s.InsertUserQueue(new[] { new QueuedTrack(T("h"), "", "queue", null, QueueRowKind.Playable) }, 0);
        Assert.Equal("h", head.UserQueue[0].Track.Id);   // slot 0 IS play-next

        var tail = s.InsertUserQueue(new[] { new QueuedTrack(T("z"), "", "queue", null, QueueRowKind.Playable) }, 999);
        Assert.Equal("z", tail.UserQueue[^1].Track.Id);  // past the end clamps to an append
        Assert.True(tail.Revision > mid.Revision);
    }
}
