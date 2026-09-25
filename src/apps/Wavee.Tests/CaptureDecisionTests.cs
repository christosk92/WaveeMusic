// ── Wavee.Tests/CaptureDecisionTests.cs — `Capture.Decision<TVerdict>` against a fake sink (§2.3, §2.4) ─────────────
//
// Exercises the ONE impure surface unit 1 ships (the `Capture` facade), through the seam unit 2 will plug a real
// writer into (`ICaptureSink`). Two things are asserted: a boxed enum verdict round-trips to the exact value/name
// the writer would format, and — the zero-cost-when-off guarantee §2.4 makes explicit — `Capture.Enabled == false`
// allocates nothing on the calling thread.
//
// All tests in this file share `Capture`'s process-wide static state (`Sink`, the enabled gate); they run
// sequentially within this class (xUnit's default — no parallelization within one test class) and each restores
// state in a `finally`, so no other test file needs to know this class touches global state.

using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

public class CaptureDecisionTests
{
    private enum FakeVerdict { None, Adopted, Rejected }
    private enum FakeState { Idle, Active }

    private sealed class FakeSink : ICaptureSink
    {
        public readonly List<CaptureEvent> Events = [];
        public void Emit(in CaptureEvent evt, ReadOnlyMemory<byte> payload) => Events.Add(evt);
    }

    [Fact]
    public void A_boxed_verdict_round_trips_to_the_writer_through_the_sink_seam()
    {
        var sink = new FakeSink();
        Capture.Sink = sink;
        Capture.SetEnabled(true);
        try
        {
            Capture.Decision(CaptureKind.OwnershipTransition, causeId: 0, FakeVerdict.Adopted,
                before: FakeState.Idle, after: FakeState.Active, reason: "InboundTransfer");

            var evt = Assert.Single(sink.Events);
            Assert.Equal(CaptureKind.OwnershipTransition, evt.Kind);
            Assert.Equal(CapturePhase.Point, evt.Phase);
            Assert.Equal("Adopted", evt.Fields.A);
            Assert.Equal("Idle→Active", evt.Fields.B);
            Assert.Equal("InboundTransfer", evt.Fields.C);
        }
        finally { Reset(); }
    }

    [Fact]
    public void A_decision_under_a_known_cause_carries_that_causes_root_forward()
    {
        var sink = new FakeSink();
        Capture.Sink = sink;
        Capture.SetEnabled(true);
        try
        {
            long root = Capture.NewRoot(CaptureKind.ActionInvoke, a: "AddToQueue");
            Capture.Decision(CaptureKind.OwnershipTransition, causeId: root, FakeVerdict.Rejected);

            Assert.Equal(2, sink.Events.Count);
            var decision = sink.Events[1];
            Assert.Equal(root, decision.CauseId);
            Assert.Equal(root, decision.RootId);
        }
        finally { Reset(); }
    }

    [Fact]
    public void Nothing_is_emitted_and_no_id_is_minted_while_capture_is_off()
    {
        var sink = new FakeSink();
        Capture.Sink = sink;
        Capture.SetEnabled(false);
        try
        {
            long root = Capture.NewRoot(CaptureKind.ActionInvoke, a: "AddToQueue");
            Capture.Decision(CaptureKind.OwnershipTransition, causeId: root, FakeVerdict.Adopted);

            Assert.Equal(0, root);
            Assert.Empty(sink.Events);
        }
        finally { Reset(); }
    }

    [Fact]
    public void Capture_enabled_false_allocates_nothing_on_the_calling_thread()
    {
        Capture.Sink = null;
        Capture.SetEnabled(false);
        try
        {
            // Warm up (JIT of every entry point exercised below, any one-time static init) before measuring.
            Capture.Decision(CaptureKind.OwnershipTransition, 0, FakeVerdict.Adopted, FakeState.Idle, FakeState.Active, "warmup");
            _ = Capture.NewRoot(CaptureKind.ActionInvoke, "warmup");
            long warmupBegin = Capture.Begin(CaptureKind.HttpCall, 0, "a", "b", "c", 1, 2);
            Capture.End(warmupBegin, 3, 4);
            Capture.Point(CaptureKind.UiRerender, 0, "warmup");

            long before = GC.GetAllocatedBytesForCurrentThread();
            Capture.Decision(CaptureKind.OwnershipTransition, 0, FakeVerdict.Adopted, FakeState.Idle, FakeState.Active, "reason");
            long beginId = Capture.Begin(CaptureKind.HttpCall, 0, "a", "b", "c", 1, 2);
            Capture.End(beginId, 3, 4);
            Capture.Point(CaptureKind.UiRerender, 0, "dump");
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }
        finally { Reset(); }
    }

    private static void Reset()
    {
        Capture.SetEnabled(false);
        Capture.Sink = null;
    }
}
