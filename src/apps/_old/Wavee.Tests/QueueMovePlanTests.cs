using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The queue panel's WHOLE-LIST reorder (Features/Player/QueueMovePlan.cs): one Reorderable spans "Next in queue" →
// "Next up" → "Autoplay", headers and "Show more" rows included, and every commit it fires is a flat (from, to) over
// that list. These pin the two pure halves the panel renders and decides from — the flattened slots (QueueSlots) and the
// flat→section-local mapping (QueueMovePlan) — so a drop can never land one slot off a header, and a row can never be
// moved into a section the model keeps it out of (PlaybackSession.MoveItem is section-local, QueueSessionTests).
public class QueueMovePlanTests
{
    static Track T(string id) => new(id, "spotify:track:" + id, "T-" + id,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    static QueueEntry E(ulong id, QueueBucket bucket = QueueBucket.UserQueue,
                        QueueProvider provider = QueueProvider.Queue)
        => new(new QueueItemId(id), "i" + id, T("t" + id), bucket, provider, provider == QueueProvider.Autoplay);

    static List<QueueEntry> Queue(params ulong[] ids) => ids.Select(i => E(i)).ToList();
    static List<QueueEntry> NextUp(params ulong[] ids)
        => ids.Select(i => E(i, QueueBucket.NextUp, QueueProvider.Context)).ToList();
    static List<QueueEntry> Auto(params ulong[] ids)
        => ids.Select(i => E(i, QueueBucket.NextUp, QueueProvider.Autoplay)).ToList();

    static readonly List<QueueEntry> None = new();

    /// <summary>The rail's shape: headers on, everything realized.</summary>
    static List<QueueSlot> Rail(List<QueueEntry> q, List<QueueEntry> u, List<QueueEntry> a, bool autoplay = true)
        => QueueSlots.Build(q, q.Count, u, u.Count, a, a.Count, autoplay, headers: true);

    static string Shape(IReadOnlyList<QueueSlot> slots) => string.Join(" ", slots.Select(s => s.Kind switch
    {
        QueueSlotKind.Header => "H" + Tag(s.Section),
        QueueSlotKind.More => "M" + Tag(s.Section),
        _ => Tag(s.Section) + s.Pos + "=" + s.Entry!.ItemId.Value,
    }));

    static string Tag(QueueSection s) => s switch { QueueSection.Queue => "q", QueueSection.NextUp => "u", _ => "a" };

    // ── the flattened slots ──────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Build_ListsHeaderRowsAndMore_PerNonEmptySection_InDisplayOrder()
    {
        var slots = Rail(Queue(1, 2), NextUp(3, 4, 5), Auto(6));
        Assert.Equal("Hq q0=1 q1=2 Hu u0=3 u1=4 u2=5 Ha a0=6", Shape(slots));
    }

    [Fact]
    public void Build_SkipsAnEmptySection_HeaderIncluded()
    {
        // A header over an empty section is a lie the panel never tells — so the reorder must not count one either.
        Assert.Equal("Hu u0=3 u1=4", Shape(Rail(None, NextUp(3, 4), None)));
        Assert.Equal("Hq q0=1", Shape(Rail(Queue(1), None, None)));
        Assert.Empty(Rail(None, None, None));
    }

    [Fact]
    public void Build_OmitsAutoplay_WhileTheToggleIsOff()
    {
        Assert.Equal("Hq q0=1 Hu u0=3", Shape(Rail(Queue(1), NextUp(3), Auto(6), autoplay: false)));
    }

    [Fact]
    public void Build_RealizesOnlyTheShownRows_AndAppendsAShowMoreSlot()
    {
        var u = NextUp(3, 4, 5, 6);
        var slots = QueueSlots.Build(None, 0, u, 2, None, 0, autoplayOn: true, headers: true);
        Assert.Equal("Hu u0=3 u1=4 Mu", Shape(slots));
        // Realized whole pages, capped at what the section holds.
        Assert.Equal(2, QueueSlots.Realized(5, pages: 1, pageSize: 2));
        Assert.Equal(4, QueueSlots.Realized(5, pages: 2, pageSize: 2));
        Assert.Equal(5, QueueSlots.Realized(5, pages: 3, pageSize: 2));
        Assert.Equal(5, QueueSlots.Realized(5, pages: 0, pageSize: 100));   // a 0 page count still shows page one
    }

    [Fact]
    public void Build_WithoutHeaders_IsTheStagesContinuousList()
    {
        var slots = QueueSlots.Build(Queue(1), 1, NextUp(3), 1, Auto(6), 1, autoplayOn: true, headers: false);
        Assert.Equal("q0=1 u0=3 a0=6", Shape(slots));
    }

    // ── the flat → section-local decision ───────────────────────────────────────────────────────────────────────────
    // Rail(Queue(1,2,3), NextUp(4,5), Auto(6)) flattens to:
    //   0:Hq 1:q0 2:q1 3:q2 4:Hu 5:u0 6:u1 7:Ha 8:a0
    static List<QueueSlot> Three() => Rail(Queue(1, 2, 3), NextUp(4, 5), Auto(6));

    [Fact]
    public void Move_InsideTheSameSection_MapsToSectionRelativePositions()
    {
        var slots = Three();
        var down = QueueMovePlan.For(slots, from: 1, to: 3);     // q0 → after q2 (post-removal index 3 = end of queue)
        Assert.Equal(QueueMoveKind.Move, down.Kind);
        Assert.Equal(QueueSection.Queue, down.Section);
        Assert.Equal(1UL, down.Entry!.ItemId.Value);
        Assert.Equal((0, 2), (down.FromPos, down.ToPos));

        var up = QueueMovePlan.For(slots, from: 6, to: 5);       // u1 → before u0
        Assert.Equal(QueueMoveKind.Move, up.Kind);
        Assert.Equal(QueueSection.NextUp, up.Section);
        Assert.Equal((1, 0), (up.FromPos, up.ToPos));
    }

    [Fact]
    public void Move_MatchesTheOptimisticOrderTheSectionThenShows()
    {
        // The mapped (FromPos, ToPos) is exactly what QueueOrder.Move (and the session op it mirrors) consumes: the
        // flat commit and the section-local remove+insert must describe the same permutation.
        var q = Queue(1, 2, 3);
        var slots = Rail(q, NextUp(4, 5), Auto(6));
        var plan = QueueMovePlan.For(slots, from: 3, to: 1);     // q2 → first
        Assert.Equal(QueueMoveKind.Move, plan.Kind);
        var moved = QueueOrder.Move(q, q, plan.FromPos, plan.ToPos);
        Assert.Equal(new ulong[] { 3, 1, 2 }, moved.Select(e => e.ItemId.Value));
    }

    [Fact]
    public void Move_ClaimsTheSharedBoundaryForTheRowsOwnSection()
    {
        // Post-removal, "insert at the next header's index − 1" is the last slot of the row's own section AND the slot
        // the next section starts at. It is read as "last in my section": that is a move the model has.
        var slots = Three();
        var lastInQueue = QueueMovePlan.For(slots, from: 1, to: 3);
        Assert.Equal(QueueMoveKind.Move, lastInQueue.Kind);
        Assert.Equal(2, lastInQueue.ToPos);
        // …but one further — onto the header itself, i.e. into Next up — is another section.
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 1, to: 4).Kind);
    }

    [Fact]
    public void Move_AcrossSections_IsRefused_NotGuessedAt()
    {
        var slots = Three();
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 2, to: 5).Kind);   // queue → next up
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 5, to: 2).Kind);   // next up → queue
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 5, to: 8).Kind);   // next up → autoplay
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 8, to: 0).Kind);   // autoplay → the top
        // The refusal still names what was lifted, so the caller can say which row stayed put.
        var refused = QueueMovePlan.For(slots, from: 2, to: 5);
        Assert.Equal(QueueSection.Queue, refused.Section);
        Assert.Equal(2UL, refused.Entry!.ItemId.Value);
        Assert.Equal(1, refused.FromPos);
    }

    [Fact]
    public void ASingleRowSection_HasExactlyOneLegalSlot_EveryOtherDropIsARefusal()
    {
        // The finding: the lone queued track had no legal target at all and its drag ended as a playlist add. Now the
        // only legal slot is its own (a no-op), and every other slot is a NAMED refusal — never silence.
        var slots = Rail(Queue(1), NextUp(4, 5), None);           // 0:Hq 1:q0 2:Hu 3:u0 4:u1
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: 1, to: 1).Kind);
        for (int to = 0; to < slots.Count; to++)
        {
            if (to == 1) continue;
            Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 1, to).Kind);
        }
    }

    [Fact]
    public void NonRowsAndSameSlot_AreNoOps()
    {
        var slots = Three();
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: 2, to: 2).Kind);
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: 0, to: 2).Kind);   // a header (keyboard lift)
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: -1, to: 2).Kind);
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: 2, to: 99).Kind);
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(new List<QueueSlot>(), 0, 0).Kind);
        // A "Show more" slot lifted (it cannot be, but the keyboard path could reach it) is a no-op too.
        var paged = QueueSlots.Build(None, 0, NextUp(3, 4, 5), 2, None, 0, autoplayOn: true, headers: true);   // Hu u0 u1 Mu
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(paged, from: 3, to: 1).Kind);
    }

    [Fact]
    public void Move_PastTheShowMoreRow_IsOutsideTheSection()
    {
        // The realized rows are the section as far as the gesture is concerned; the more-row is its far edge. Landing
        // ON it (post-removal index of the last realized row) is "last realized", landing past it is refused.
        var paged = QueueSlots.Build(Queue(1, 2, 3, 4), 2, NextUp(5), 1, None, 0, autoplayOn: true, headers: true);
        // 0:Hq 1:q0 2:q1 3:Mq 4:Hu 5:u0
        var last = QueueMovePlan.For(paged, from: 1, to: 2);
        Assert.Equal(QueueMoveKind.Move, last.Kind);
        Assert.Equal(1, last.ToPos);
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(paged, from: 1, to: 3).Kind);
    }

    [Fact]
    public void WithoutHeaders_TheSectionsStillBoundTheMove()
    {
        // The stage pane draws no captions, but the model's provider split is the same — so is the decision.
        var slots = QueueSlots.Build(Queue(1, 2), 2, NextUp(3, 4), 2, Auto(5), 1, autoplayOn: true, headers: false);
        // 0:q0 1:q1 2:u0 3:u1 4:a0
        Assert.Equal(QueueMoveKind.Move, QueueMovePlan.For(slots, from: 0, to: 1).Kind);
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 1, to: 2).Kind);
        Assert.Equal(QueueMoveKind.Move, QueueMovePlan.For(slots, from: 3, to: 2).Kind);
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 4, to: 3).Kind);
    }

    // ── a foreign deposit's landing index ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void InsertIndex_CountsOnlyTheQueueRowsAboveTheBoundary()
    {
        var slots = Three();   // 0:Hq 1:q0 2:q1 3:q2 4:Hu 5:u0 6:u1 7:Ha 8:a0
        Assert.Equal(0, QueueMovePlan.InsertIndex(slots, 0));   // above the header: play next
        Assert.Equal(0, QueueMovePlan.InsertIndex(slots, 1));   // between the header and the first row: still first
        Assert.Equal(1, QueueMovePlan.InsertIndex(slots, 2));
        Assert.Equal(3, QueueMovePlan.InsertIndex(slots, 4));   // after the last queued row
        Assert.Equal(3, QueueMovePlan.InsertIndex(slots, 6));   // anywhere in Next up: append to the queue
        Assert.Equal(3, QueueMovePlan.InsertIndex(slots, 9));   // the end of the list
        Assert.Equal(3, QueueMovePlan.InsertIndex(slots, 42));  // past it (clamped)
    }

    [Fact]
    public void InsertIndex_WithNoUserQueue_IsAlwaysPlayNext()
    {
        var slots = Rail(None, NextUp(4, 5), Auto(6));
        for (int slot = 0; slot <= slots.Count; slot++)
            Assert.Equal(0, QueueMovePlan.InsertIndex(slots, slot));
        Assert.Equal(0, QueueMovePlan.InsertIndex(new List<QueueSlot>(), 0));
    }
}
