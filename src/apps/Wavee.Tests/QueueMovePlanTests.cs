// ── Wavee.Tests/QueueMovePlanTests.cs — the queue lane's slots and its flat → section-local decision ─────────────────
//
// Ported assertion-for-assertion from 0.2.9's QueueMovePlanTests (ch 21 §8): one Reorderable spans "Next in queue" →
// "Next up" → "Autoplay", headers and "Show more" rows included, and every commit it fires is a flat (from, to) over
// that list. 0.3's slots carry the section-relative POSITION instead of an entry object, so the shape strings name
// positions; the decisions are unchanged. Pure: no scope, no engine loop.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class QueueMovePlanTests
{
    static QueueSlot[] Slots(int queue, int nextUp, int autoplay, bool autoplayOn = true, bool headers = true,
                             int shownQueue = int.MaxValue, int shownNextUp = int.MaxValue, int shownAutoplay = int.MaxValue)
    {
        var dst = new QueueSlot[QueueSlots.Capacity(queue, nextUp, autoplay)];
        int n = QueueSlots.Build(queue, shownQueue, nextUp, shownNextUp, autoplay, shownAutoplay, autoplayOn, headers, dst);
        return dst[..n];
    }

    static string Shape(QueueSlot[] slots)
    {
        var parts = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            parts[i] = s.Kind switch
            {
                QueueSlotKind.Header => "H" + Tag(s.Section),
                QueueSlotKind.More => "M" + Tag(s.Section),
                _ => Tag(s.Section) + s.Pos,
            };
        }
        return string.Join(" ", parts);
    }

    static string Tag(QueueSection s) => s switch { QueueSection.Queue => "q", QueueSection.NextUp => "u", _ => "a" };

    // ── the flattened slots ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_lists_header_rows_and_more_per_non_empty_section_in_display_order()
        => Assert.Equal("Hq q0 q1 Hu u0 u1 u2 Ha a0", Shape(Slots(2, 3, 1)));

    [Fact]
    public void Build_skips_an_empty_section_header_included()
    {
        // A header over an empty section is a lie the panel never tells — so the reorder must not count one either.
        Assert.Equal("Hu u0 u1", Shape(Slots(0, 2, 0)));
        Assert.Equal("Hq q0", Shape(Slots(1, 0, 0)));
        Assert.Empty(Slots(0, 0, 0));
    }

    [Fact]
    public void Build_omits_autoplay_while_the_toggle_is_off()
        => Assert.Equal("Hq q0 Hu u0", Shape(Slots(1, 1, 1, autoplayOn: false)));

    [Fact]
    public void Build_realizes_only_the_shown_rows_and_appends_a_show_more_slot()
    {
        Assert.Equal("Hu u0 u1 Mu", Shape(Slots(0, 4, 0, shownNextUp: 2)));
        Assert.Equal(2, QueueSlots.Realized(5, pages: 1, pageSize: 2));
        Assert.Equal(4, QueueSlots.Realized(5, pages: 2, pageSize: 2));
        Assert.Equal(5, QueueSlots.Realized(5, pages: 3, pageSize: 2));
        Assert.Equal(5, QueueSlots.Realized(5, pages: 0, pageSize: 100));   // a 0 page count still shows page one
    }

    [Fact]
    public void Build_without_headers_is_the_stages_continuous_list()
        => Assert.Equal("q0 u0 a0", Shape(Slots(1, 1, 1, headers: false)));

    // ── the flat → section-local decision ───────────────────────────────────────────────────────────────────────────
    // Slots(3, 2, 1) flattens to: 0:Hq 1:q0 2:q1 3:q2 4:Hu 5:u0 6:u1 7:Ha 8:a0

    static QueueSlot[] Three() => Slots(3, 2, 1);

    [Fact]
    public void A_move_inside_the_same_section_maps_to_section_relative_positions()
    {
        var slots = Three();
        var down = QueueMovePlan.For(slots, from: 1, to: 3);     // q0 → after q2 (post-removal index 3 = end of queue)
        Assert.Equal(QueueMoveKind.Move, down.Kind);
        Assert.Equal(QueueSection.Queue, down.Section);
        Assert.Equal((0, 2), (down.FromPos, down.ToPos));

        var up = QueueMovePlan.For(slots, from: 6, to: 5);       // u1 → before u0
        Assert.Equal(QueueMoveKind.Move, up.Kind);
        Assert.Equal(QueueSection.NextUp, up.Section);
        Assert.Equal((1, 0), (up.FromPos, up.ToPos));
    }

    [Fact]
    public void The_mapped_move_is_the_permutation_the_order_rule_then_applies()
    {
        // q2 → first: the flat commit and the section-local remove+insert must describe the same permutation.
        var plan = QueueMovePlan.For(Three(), from: 3, to: 1);
        Assert.Equal(QueueMoveKind.Move, plan.Kind);
        int[] targets = [10, 20, 30];
        QueueEdge[] rows = [new(1, 1, 1), new(2, 1, 1), new(3, 1, 1)];
        int[] positions = [0, 1, 2];
        Assert.True(QueueOrder.Move(targets, rows, positions, plan.FromPos, plan.ToPos));
        Assert.Equal(new[] { 30, 10, 20 }, targets);
        Assert.Equal(new ulong[] { 3, 1, 2 }, new[] { rows[0].ItemId, rows[1].ItemId, rows[2].ItemId });
    }

    [Fact]
    public void A_move_claims_the_shared_boundary_for_the_rows_own_section()
    {
        var slots = Three();
        var lastInQueue = QueueMovePlan.For(slots, from: 1, to: 3);
        Assert.Equal(QueueMoveKind.Move, lastInQueue.Kind);
        Assert.Equal(2, lastInQueue.ToPos);
        // …but one further — onto the header itself, i.e. into Next up — is another section.
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 1, to: 4).Kind);
    }

    [Fact]
    public void A_move_across_sections_is_refused_not_guessed_at()
    {
        var slots = Three();
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 2, to: 5).Kind);   // queue → next up
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 5, to: 2).Kind);   // next up → queue
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 5, to: 8).Kind);   // next up → autoplay
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 8, to: 0).Kind);   // autoplay → the top
        // The refusal still names what was lifted, so the caller can say which row stayed put.
        var refused = QueueMovePlan.For(slots, from: 2, to: 5);
        Assert.Equal(QueueSection.Queue, refused.Section);
        Assert.Equal(1, refused.FromPos);
    }

    [Fact]
    public void A_single_row_section_has_exactly_one_legal_slot_and_every_other_drop_is_a_refusal()
    {
        var slots = Slots(1, 2, 0);                               // 0:Hq 1:q0 2:Hu 3:u0 4:u1
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: 1, to: 1).Kind);
        for (int to = 0; to < slots.Length; to++)
        {
            if (to == 1) continue;
            Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 1, to).Kind);
        }
    }

    [Fact]
    public void Non_rows_and_the_same_slot_are_no_ops()
    {
        var slots = Three();
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: 2, to: 2).Kind);
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: 0, to: 2).Kind);    // a header (keyboard lift)
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: -1, to: 2).Kind);
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(slots, from: 2, to: 99).Kind);
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(ReadOnlySpan<QueueSlot>.Empty, 0, 0).Kind);
        var paged = Slots(0, 3, 0, shownNextUp: 2);                                          // Hu u0 u1 Mu
        Assert.Equal(QueueMoveKind.NoOp, QueueMovePlan.For(paged, from: 3, to: 1).Kind);
    }

    [Fact]
    public void A_move_past_the_show_more_row_is_outside_the_section()
    {
        var paged = Slots(4, 1, 0, shownQueue: 2);                // 0:Hq 1:q0 2:q1 3:Mq 4:Hu 5:u0
        var last = QueueMovePlan.For(paged, from: 1, to: 2);
        Assert.Equal(QueueMoveKind.Move, last.Kind);
        Assert.Equal(1, last.ToPos);
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(paged, from: 1, to: 3).Kind);
    }

    [Fact]
    public void Without_headers_the_sections_still_bound_the_move()
    {
        var slots = Slots(2, 2, 1, headers: false);               // 0:q0 1:q1 2:u0 3:u1 4:a0
        Assert.Equal(QueueMoveKind.Move, QueueMovePlan.For(slots, from: 0, to: 1).Kind);
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 1, to: 2).Kind);
        Assert.Equal(QueueMoveKind.Move, QueueMovePlan.For(slots, from: 3, to: 2).Kind);
        Assert.Equal(QueueMoveKind.Refused, QueueMovePlan.For(slots, from: 4, to: 3).Kind);
    }

    // ── a foreign deposit's landing index ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_insert_index_counts_only_the_queue_rows_above_the_boundary()
    {
        var slots = Three();                                       // 0:Hq 1:q0 2:q1 3:q2 4:Hu 5:u0 6:u1 7:Ha 8:a0
        Assert.Equal(0, QueueMovePlan.InsertIndex(slots, 0));      // above the header: play next
        Assert.Equal(0, QueueMovePlan.InsertIndex(slots, 1));
        Assert.Equal(1, QueueMovePlan.InsertIndex(slots, 2));
        Assert.Equal(3, QueueMovePlan.InsertIndex(slots, 4));      // after the last queued row
        Assert.Equal(3, QueueMovePlan.InsertIndex(slots, 6));      // anywhere in Next up: append to the queue
        Assert.Equal(3, QueueMovePlan.InsertIndex(slots, 9));
        Assert.Equal(3, QueueMovePlan.InsertIndex(slots, 42));     // past the end (clamped)
    }

    [Fact]
    public void With_no_user_queue_the_insert_is_always_play_next()
    {
        var slots = Slots(0, 2, 1);
        for (int slot = 0; slot <= slots.Length; slot++) Assert.Equal(0, QueueMovePlan.InsertIndex(slots, slot));
        Assert.Equal(0, QueueMovePlan.InsertIndex(ReadOnlySpan<QueueSlot>.Empty, 0));
    }
}
