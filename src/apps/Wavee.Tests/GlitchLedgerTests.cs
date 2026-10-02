// ── Wavee.Tests/GlitchLedgerTests.cs — playback smoothness WP-0a (D1; V-PA26/X4, V-PA31) ────────────────────────────
//
// `GlitchLedger` (Playback/Playback.Glitch.cs) is System-only and takes primitives: the verdict keys over primitive
// inputs, the longest/last stall, the GC attribution, the byte-wait fold, `Reset`, and a concurrent Record/Read pass
// that the one lock keeps consistent. Pure: no engine, no clock, no log.

using Xunit;

namespace Wavee.Tests;

public class GlitchLedgerTests
{
    const int Rate = 48_000;
    const long Now = 1_760_000_000_000L;

    static GlitchLedger Ledger() => new();

    // ── the verdict keys ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_ledger_is_clean_and_all_zero()
    {
        GlitchLedger.Snapshot s = Ledger().Read();

        Assert.Equal(GlitchLedger.VerdictClean, s.Verdict);
        Assert.Equal(0, s.Incidents);
        Assert.Equal(0, s.ProducerStarves);
        Assert.Equal(0, s.DeviceLate);
        Assert.Equal(0, s.GcImplicated);
        Assert.Equal(0, s.ByteWaits);
        Assert.Equal(0L, s.FramesLost);
        Assert.Equal(0L, s.LongestStallMs);
        Assert.Equal(0L, s.LastStallMs);
        Assert.Equal(0L, s.LastStallAtUnixMs);
        Assert.Equal(0L, s.LongestByteWaitMs);
    }

    [Fact]
    public void The_verdict_keys_are_the_five_the_card_maps_and_nothing_else()
    {
        Assert.Equal("clean", GlitchLedger.VerdictClean);
        Assert.Equal("gcPauses", GlitchLedger.VerdictGcPauses);
        Assert.Equal("byteStarved", GlitchLedger.VerdictByteStarved);
        Assert.Equal("producerStarved", GlitchLedger.VerdictProducerStarved);
        Assert.Equal("deviceLate", GlitchLedger.VerdictDeviceLate);
    }

    [Fact]
    public void An_empty_ring_at_the_miss_is_a_producer_starve_and_a_ring_with_audio_is_a_device_late()
    {
        GlitchLedger g = Ledger();
        g.Record(480, ringFramesAtMiss: 0, gcPauseTicks: 0, Rate, Now);
        g.Record(480, ringFramesAtMiss: 0, gcPauseTicks: 0, Rate, Now);
        g.Record(480, ringFramesAtMiss: 9_600, gcPauseTicks: 0, Rate, Now);

        GlitchLedger.Snapshot s = g.Read();

        Assert.Equal(3, s.Incidents);
        Assert.Equal(2, s.ProducerStarves);
        Assert.Equal(1, s.DeviceLate);
        Assert.Equal(GlitchLedger.VerdictProducerStarved, s.Verdict);
    }

    [Fact]
    public void More_device_late_incidents_than_producer_starves_is_device_late_and_a_tie_is_the_device_too()
    {
        GlitchLedger g = Ledger();
        g.Record(480, 4_800, 0, Rate, Now);
        g.Record(480, 0, 0, Rate, Now);
        Assert.Equal(GlitchLedger.VerdictDeviceLate, g.Read().Verdict);          // 1 vs 1: the RT thread was late

        g.Record(480, 4_800, 0, Rate, Now);
        Assert.Equal(GlitchLedger.VerdictDeviceLate, g.Read().Verdict);          // 2 vs 1
    }

    [Fact]
    public void A_gc_pause_in_half_the_incidents_outranks_every_other_verdict()
    {
        GlitchLedger g = Ledger();
        g.Record(480, 0, gcPauseTicks: 12_345, Rate, Now);
        g.Record(480, 0, gcPauseTicks: 0, Rate, Now);

        GlitchLedger.Snapshot s = g.Read();

        Assert.Equal(1, s.GcImplicated);
        Assert.Equal(GlitchLedger.VerdictGcPauses, s.Verdict);                   // 1 of 2 is "half"
    }

    [Fact]
    public void A_gc_pause_in_fewer_than_half_the_incidents_does_not_win()
    {
        GlitchLedger g = Ledger();
        g.Record(480, 0, gcPauseTicks: 1, Rate, Now);
        g.Record(480, 0, 0, Rate, Now);
        g.Record(480, 0, 0, Rate, Now);

        GlitchLedger.Snapshot s = g.Read();

        Assert.Equal(1, s.GcImplicated);
        Assert.Equal(GlitchLedger.VerdictProducerStarved, s.Verdict);            // 1 of 3 is under half
    }

    [Fact]
    public void A_byte_wait_plus_an_empty_ring_is_byte_starved_but_a_byte_wait_alone_stays_clean()
    {
        GlitchLedger g = Ledger();
        g.RecordByteWait(1_600);
        Assert.Equal(GlitchLedger.VerdictClean, g.Read().Verdict);               // the link stalled, the ring covered it: not a glitch

        g.Record(480, ringFramesAtMiss: 0, gcPauseTicks: 0, Rate, Now);
        Assert.Equal(GlitchLedger.VerdictByteStarved, g.Read().Verdict);
    }

    [Fact]
    public void A_byte_wait_does_not_blame_the_link_for_a_device_late_incident()
    {
        GlitchLedger g = Ledger();
        g.RecordByteWait(2_000);
        g.Record(480, ringFramesAtMiss: 4_800, gcPauseTicks: 0, Rate, Now);       // the ring had audio: not the link

        Assert.Equal(GlitchLedger.VerdictDeviceLate, g.Read().Verdict);
    }

    // ── the numbers ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_longest_stall_is_kept_and_the_last_stall_is_the_latest()
    {
        GlitchLedger g = Ledger();
        g.Record(480, 0, 0, Rate, Now);                  // 10 ms
        g.Record(4_800, 0, 0, Rate, Now + 1_000);        // 100 ms
        g.Record(960, 0, 0, Rate, Now + 2_000);          // 20 ms

        GlitchLedger.Snapshot s = g.Read();

        Assert.Equal(100L, s.LongestStallMs);
        Assert.Equal(20L, s.LastStallMs);
        Assert.Equal(Now + 2_000, s.LastStallAtUnixMs);
        Assert.Equal(480L + 4_800 + 960, s.FramesLost);
    }

    [Fact]
    public void A_stall_is_measured_against_the_rate_the_session_runs_at()
    {
        GlitchLedger g = Ledger();
        g.Record(4_410, 0, 0, sampleRate: 44_100, Now);

        Assert.Equal(100L, g.Read().LongestStallMs);
    }

    [Fact]
    public void An_unknown_rate_counts_the_incident_and_the_frames_but_reports_no_stall_time()
    {
        GlitchLedger g = Ledger();
        g.Record(480, 0, 0, sampleRate: 0, Now);

        GlitchLedger.Snapshot s = g.Read();

        Assert.Equal(1, s.Incidents);
        Assert.Equal(480L, s.FramesLost);
        Assert.Equal(0L, s.LongestStallMs);
    }

    [Fact]
    public void A_negative_gap_is_clamped_instead_of_poisoning_the_totals()
    {
        GlitchLedger g = Ledger();
        g.Record(-5, 0, 0, Rate, Now);
        g.Record(480, 0, 0, Rate, Now);

        GlitchLedger.Snapshot s = g.Read();

        Assert.Equal(2, s.Incidents);
        Assert.Equal(480L, s.FramesLost);
        Assert.Equal(10L, s.LongestStallMs);
    }

    [Fact]
    public void The_byte_wait_fold_counts_every_edge_and_keeps_the_longest()
    {
        GlitchLedger g = Ledger();
        g.RecordByteWait(1_500);
        g.RecordByteWait(4_000);
        g.RecordByteWait(2_000);

        GlitchLedger.Snapshot s = g.Read();

        Assert.Equal(3, s.ByteWaits);
        Assert.Equal(4_000L, s.LongestByteWaitMs);
    }

    // ── Reset ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_returns_every_counter_and_the_verdict_to_clean()
    {
        GlitchLedger g = Ledger();
        g.Record(4_800, 0, gcPauseTicks: 77, Rate, Now);
        g.Record(480, 4_800, 0, Rate, Now + 5);
        g.RecordByteWait(3_000);
        Assert.NotEqual(GlitchLedger.VerdictClean, g.Read().Verdict);

        g.Reset();

        Assert.Equal(new GlitchLedger.Snapshot(0, 0, 0, 0, 0, 0L, 0L, 0L, 0L, 0L, GlitchLedger.VerdictClean), g.Read());
    }

    [Fact]
    public void A_ledger_counts_again_from_zero_after_a_reset()
    {
        GlitchLedger g = Ledger();
        g.Record(4_800, 0, 0, Rate, Now);
        g.Reset();
        g.Record(480, 0, 0, Rate, Now + 1);

        GlitchLedger.Snapshot s = g.Read();

        Assert.Equal(1, s.Incidents);
        Assert.Equal(10L, s.LongestStallMs);                                     // the 100 ms from before the reset is gone
        Assert.Equal(480L, s.FramesLost);
    }

    // ── threads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_reader_never_sees_a_torn_snapshot_while_the_tick_thread_records()
    {
        const int writers = 4, perWriter = 2_000, gap = 480;
        GlitchLedger g = Ledger();
        bool stop = false;
        int torn = 0;

        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                GlitchLedger.Snapshot s = g.Read();
                // The three counters move together under one lock: the split must add up and the frames must be exact.
                if (s.Incidents != s.ProducerStarves + s.DeviceLate || s.FramesLost != (long)s.Incidents * gap) Interlocked.Increment(ref torn);
            }
        });
        reader.Start();

        var pool = new Thread[writers];
        for (int w = 0; w < writers; w++)
        {
            int id = w;
            pool[w] = new Thread(() =>
            {
                for (int i = 0; i < perWriter; i++)
                    g.Record(gap, ringFramesAtMiss: (i + id) % 2 == 0 ? 0 : 4_800, gcPauseTicks: i % 5 == 0 ? 1 : 0, Rate, Now + i);
            });
            pool[w].Start();
        }
        foreach (Thread t in pool) t.Join();
        Volatile.Write(ref stop, true);
        reader.Join();

        GlitchLedger.Snapshot final = g.Read();
        Assert.Equal(0, Volatile.Read(ref torn));
        Assert.Equal(writers * perWriter, final.Incidents);
        Assert.Equal(final.Incidents, final.ProducerStarves + final.DeviceLate);
        Assert.Equal((long)writers * perWriter * gap, final.FramesLost);
        Assert.Equal(writers * (perWriter / 5), final.GcImplicated);
        Assert.Equal(10L, final.LongestStallMs);
    }

    // ── the open incident's tail (the feed raises one event per incident; the rest accrues into frames lost) ────────

    [Fact]
    public void Extend_grows_the_fresh_incident_to_its_real_length_and_the_longest_with_it()
    {
        var g = Ledger();
        g.Record(480, ringFramesAtMiss: 0, gcPauseTicks: 0, Rate, Now);          // the first 10 ms block
        g.Extend(48_000 - 480, Rate, Now + 500);                                  // the stall ran to a full second

        GlitchLedger.Snapshot s = g.Read();
        Assert.Equal(1, s.Incidents);                                             // still ONE incident
        Assert.Equal(48_000L, s.FramesLost);
        Assert.Equal(1_000L, s.LastStallMs);
        Assert.Equal(1_000L, s.LongestStallMs);
        Assert.Equal(Now + 500, s.LastStallAtUnixMs);
    }

    [Fact]
    public void Extend_is_a_no_op_with_no_incident_or_a_stale_one()
    {
        var g = Ledger();
        g.Extend(4_800, Rate, Now);
        Assert.Equal(0L, g.Read().FramesLost);

        g.Record(480, 0, 0, Rate, Now);
        g.Extend(4_800, Rate, Now + GlitchLedger.ExtendWindowMs + 1);             // silence long after: not this incident's
        GlitchLedger.Snapshot s = g.Read();
        Assert.Equal(480L, s.FramesLost);
        Assert.Equal(10L, s.LongestStallMs);
    }

    [Fact]
    public void Extend_after_a_newer_incident_grows_only_that_one()
    {
        var g = Ledger();
        g.Record(48_000, 0, 0, Rate, Now);                                        // a 1 s stall
        g.Record(480, 4_800, 0, Rate, Now + 5_000);                               // a later 10 ms one
        g.Extend(4_320, Rate, Now + 5_100);                                       // …that ran to 100 ms

        GlitchLedger.Snapshot s = g.Read();
        Assert.Equal(100L, s.LastStallMs);
        Assert.Equal(1_000L, s.LongestStallMs);                                   // the earlier one stays the longest
        Assert.Equal(48_000L + 4_800L, s.FramesLost);
    }
}
