// ── Wavee.Tests/PlaybackSeekGenTests.cs — the seek GENERATION contract end to end (playback smoothness #167, WP 2d) ──────────
//
// Plan docs/plans/wavee/playback-smoothness-implementation.md §4.9 / §4.13 and the wave-2 test list. `PlaybackReducerSeekTests` owns the
// reducer's seek arms (and WP 3d is editing it); THIS file pins the pieces WP 2d added around them, which that file cannot reach:
//
//   THE BAR'S HOLD IS RELEASED BY A GENERATION, NOT BY A POSITION. The seek bar samples `Playback.SeekGen` BEFORE it posts its commit and
//   holds the drop point until `Playback.SeekLandedAfter(sample, LastSeekLandedGen)`: a landed generation strictly NEWER than the sample,
//   wrap-safe. It never predicts the generation the commit will get, so a seek already queued ahead of the commit cannot skew it, and a
//   NEWER seek that superseded the commit releases it (the reducer drops every `Seeked` but the newest's). A commit the reducer does not
//   turn into a seek (a parked deck, a foreign owner) bumps nothing — so nothing lands past the sample and the bar's own timeout ends it.
//
//   THE VIDEO HOST SPEAKS GENERATION TOO. `Video.Seek(ms, accurate, gen)` stamps its `Position`/`Seeked` posts; `HostRules.SeekLanded` decides when
//   the seek it owes a `Seeked` for has landed (the engine stops seeking — or, for a RIDE, playback reaches the target); and a video load that
//   resolved after the reducer took a seek starts under the generation the reducer is on NOW (`Playback.VideoLoadStart`), because a load seeded
//   with the older one would stamp every `Position` it posts with it and have all of them dropped for the rest of the load.
//
//   THE STARTED ARM. A `Started` from an older generation while a newer seek is still in flight carries a pre-drop position: adopting it would
//   step the thumb back over the drop point. Once the CURRENT seek has landed, any `Started` is post-landing and is adopted.
//
// Every fact is a pure function or a `Playback.Step` over a hand-built state: no pump, no host, no clock (every input carries its own stamp).

using Wavee;
using Xunit;

using V = Wavee.Playback.Video;

namespace Wavee.Tests;

// ── the pure helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

public class SeekGenPureTests
{
    [Theory]
    [InlineData(5u, 6u, true)]                            // the next generation landed: released
    [InlineData(5u, 5u, false)]                           // the SAMPLE's own generation is not "newer": the commit has not landed yet
    [InlineData(5u, 4u, false)]                           // an older landing says nothing about the commit
    [InlineData(5u, 99u, true)]                           // a newer seek superseded it: it released the hold too
    [InlineData(0u, 1u, true)]
    [InlineData(0u, 0u, false)]
    [InlineData(10u, 5u, false)]
    [InlineData(uint.MaxValue, 0u, true)]                 // wrap: 0 comes AFTER the largest generation
    [InlineData(uint.MaxValue, 1u, true)]
    [InlineData(uint.MaxValue - 1u, 0u, true)]
    [InlineData(uint.MaxValue, uint.MaxValue, false)]
    [InlineData(0u, uint.MaxValue, false)]                // …and the largest comes before 0
    [InlineData(0u, 0x7FFF_FFFFu, true)]                  // the half range: 2³¹−1 ahead is still "newer"
    [InlineData(0u, 0x8000_0000u, false)]                 // exactly 2³¹ ahead is not
    [InlineData(0x7FFF_FFFFu, 0xFFFF_FFFFu, false)]
    public void A_seek_landed_only_when_the_landed_generation_is_strictly_newer_than_the_sample_and_wrap_safe(
        uint sampled, uint landed, bool expected)
        => Assert.Equal(expected, Playback.SeekLandedAfter(sampled, landed));

    [Fact]
    public void SeekLandedAfter_is_a_strict_order_over_any_window_of_a_thousand_generations_even_across_the_wrap()
    {
        foreach (uint gen in new[] { 0u, 1u, 12_345u, 0x7FFF_FFF0u, 0xFFFF_FC18u, uint.MaxValue - 1u, uint.MaxValue })
        {
            Assert.False(Playback.SeekLandedAfter(gen, gen));
            for (uint k = 1; k <= 1_000; k++)
            {
                uint newer = unchecked(gen + k);
                Assert.True(Playback.SeekLandedAfter(gen, newer), $"{gen} → {newer}");
                Assert.False(Playback.SeekLandedAfter(newer, gen), $"{newer} → {gen}");
            }
        }
    }

    [Theory]
    [InlineData(true, false, 0L, 90_000L, false)]         // the engine took the seek and is still on it: not landed
    [InlineData(true, false, 90_000L, 90_000L, false)]    // …whatever the position says: an engine seek lands when the ENGINE stops seeking
    [InlineData(false, false, 0L, 90_000L, true)]         // the engine stopped seeking: landed, wherever it says it is
    [InlineData(false, false, 123L, 90_000L, true)]
    [InlineData(true, true, 89_999L, 90_000L, false)]     // a RIDE made no engine call: it lands when playback REACHES the target
    [InlineData(true, true, 90_000L, 90_000L, true)]
    [InlineData(false, true, 89_999L, 90_000L, false)]    // (the engine's seeking flag is meaningless for a ride)
    [InlineData(false, true, 90_001L, 90_000L, true)]
    public void A_video_seek_lands_when_the_engine_stops_seeking_or_a_ride_reaches_its_target(
        bool engineSeeking, bool rode, long positionMs, long targetMs, bool expected)
        => Assert.Equal(expected, V.HostRules.SeekLanded(engineSeeking, rode, positionMs, targetMs));

    [Theory]
    [InlineData(4u, 61_000, 4u, 12_000, 4u, 12_000)]      // the reducer has not moved on: the load keeps its own position and generation
    [InlineData(4u, 61_000, 3u, 12_000, 4u, 61_000)]      // a seek was taken while the manifest resolved: its drop point and generation
    [InlineData(9u, 0, 3u, 12_000, 9u, 0)]                // …even a drop at 0
    [InlineData(0u, 5_000, uint.MaxValue, 12_000, 0u, 5_000)]   // across the wrap the reducer's value still wins: it is "moved", not "newer"
    [InlineData(uint.MaxValue, 7_000, 0u, 1_000, uint.MaxValue, 7_000)]
    [InlineData(0u, 5_000, 0u, 12_000, 0u, 12_000)]       // generation 0 on both sides: no seek happened, the load's position stands
    public void A_resolved_video_load_starts_under_the_generation_the_reducer_is_on_now(
        uint reducerGen, int reducerPosMs, uint loadGen, int loadFromMs, uint expectedGen, int expectedStartMs)
    {
        (uint gen, int startMs) = Playback.VideoLoadStart(reducerGen, reducerPosMs, loadGen, loadFromMs);
        Assert.Equal(expectedGen, gen);
        Assert.Equal(expectedStartMs, startMs);
    }
}

// ── the reducer scenarios the helpers exist for ──────────────────────────────────────────────────────────────────────────

[Collection(EntitiesCollection.Name)]
public class PlaybackSeekGenTests
{
    static EntityRef Track(int n)
    {
        var id = EntityId.ForGid(EntityKind.Track, (UInt128)(ulong)(0x7000 + n));
        return new EntityRef(EntityKind.Track, Entities.Current.Tracks.Slot(id));
    }

    /// <summary>Row 0 on the deck, playing and OURS. The load epoch is the state's own, so an input stamped <c>s.LoadEpoch</c> is current.</summary>
    static Playback.State Playing(int upNext = 2)
    {
        TestScope.Fresh();
        var refs = new EntityRef[upNext + 1];
        var rows = new QueueEdge[upNext + 1];
        refs[0] = Track(0);
        rows[0] = new QueueEdge(1, (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying);
        for (int i = 1; i <= upNext; i++)
        {
            refs[i] = Track(i);
            rows[i] = new QueueEdge((ulong)(i + 1), (byte)QueueProvider.Context, (byte)QueueBucket.NextUp);
        }
        Queue.Replace(refs, rows);

        var s = Playback.State.Initial;
        s.Us = Playback.DeviceHash("wavee-device");
        s.Current = refs[0];
        s.CurrentId = refs[0].Id;
        s.Cursor = Queue.CursorOf(0);
        s.Phase = Playback.Phase.Playing;
        s.DurationMs = 180_000;
        s.LoadEpoch = s.Epoch;
        Playback.Ownership.Claim(ref s.Own, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: false);
        return s;
    }

    static void Step(ref Playback.State s, ref Playback.Effects fx, Playback.Input i) => Playback.Step(ref s, i, ref fx);

    static Playback.Input Report(Playback.AudioSignal signal, uint epoch, long nowMs, long arg, uint gen)
        => Playback.Input.Audio(signal, epoch, nowMs, arg, gen);

    // ── the Started arm ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_started_of_the_current_generation_adopts_its_position()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.Seek(60_000, nowMs: 1_000));
        uint gen = s.SeekGen;
        s.Phase = Playback.Phase.Loading;

        Step(ref s, ref fx, Report(Playback.AudioSignal.Started, s.LoadEpoch, nowMs: 1_400, arg: 60_250, gen));

        Assert.Equal(Playback.Phase.Playing, s.Phase);
        Assert.Equal(60_250, s.PosMs);                                   // the report belongs to THIS seek: where the audio really is
        Assert.Equal(1_400L, s.PosQpc);
    }

    [Fact]
    public void A_started_of_an_older_generation_flips_the_phase_but_not_the_position_while_the_newer_seek_is_in_flight()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.Seek(30_000, nowMs: 1_000));
        uint older = s.SeekGen;
        Step(ref s, ref fx, Playback.Input.Seek(90_000, nowMs: 1_040));
        Assert.NotEqual(older, s.SeekGen);
        long qpc = s.PosQpc;
        s.Phase = Playback.Phase.Loading;
        s.Buffering = true;

        Step(ref s, ref fx, Report(Playback.AudioSignal.Started, s.LoadEpoch, nowMs: 1_200, arg: 30_100, older));

        Assert.Equal(Playback.Phase.Playing, s.Phase);                   // audio IS out …
        Assert.False(s.Buffering);
        Assert.Equal(90_000, s.PosMs);                                   // … but the thumb stays on the drop point (the newest Seeked will place it)
        Assert.Equal(qpc, s.PosQpc);
        Assert.Equal(0u, s.LastSeekLandedGen);
    }

    [Fact]
    public void Once_the_current_seek_has_landed_any_started_is_post_landing_and_is_adopted()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.Seek(30_000, nowMs: 1_000));
        Step(ref s, ref fx, Playback.Input.Seek(90_000, nowMs: 1_040));
        uint gen = s.SeekGen;
        Step(ref s, ref fx, Report(Playback.AudioSignal.Seeked, s.LoadEpoch, nowMs: 1_100, arg: 90_031, gen));
        Assert.Equal(gen, s.LastSeekLandedGen);
        s.Phase = Playback.Phase.Paused;                                 // the device restarts after the landing

        // the restart's Started carries the older stamp the pump had when it began the restart — but the seek has landed, so it is where we are
        Step(ref s, ref fx, Report(Playback.AudioSignal.Started, s.LoadEpoch, nowMs: 1_300, arg: 90_212, gen - 1u));

        Assert.Equal(Playback.Phase.Playing, s.Phase);
        Assert.Equal(90_212, s.PosMs);
        Assert.Equal(1_300L, s.PosQpc);
    }

    [Fact]
    public void A_started_on_a_deck_that_never_seeked_adopts_its_position()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Assert.Equal(0u, s.SeekGen);
        s.Phase = Playback.Phase.Loading;

        Step(ref s, ref fx, Report(Playback.AudioSignal.Started, s.LoadEpoch, nowMs: 500, arg: 1_234, gen: 0u));   // generation 0 == generation 0

        Assert.Equal(1_234, s.PosMs);
        Assert.Equal(500L, s.PosQpc);
    }

    [Fact]
    public void A_started_under_a_superseded_load_epoch_is_dropped_whatever_its_generation()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.Seek(60_000, nowMs: 1_000));
        uint gen = s.SeekGen;
        s.Phase = Playback.Phase.Loading;

        Step(ref s, ref fx, Report(Playback.AudioSignal.Started, s.LoadEpoch + 1u, nowMs: 1_400, arg: 60_250, gen));

        Assert.Equal(Playback.Phase.Loading, s.Phase);                   // C4 stands in front of the generation guard
        Assert.Equal(60_000, s.PosMs);
    }

    // ── video: the host stamps generations; a gen-0 report after a local seek is dropped ──────────────────────────────

    [Fact]
    public void A_video_decks_position_without_the_generation_is_dropped_after_a_local_seek_until_the_host_carries_it()
    {
        var s = Playing();
        s.Kind = Playback.PlayableKind.Video;
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.Seek(75_000, nowMs: 2_000));
        uint gen = s.SeekGen;
        Assert.Equal(1u, gen);
        long qpc = s.PosQpc;

        // The HAZARD WP 2d closed: a host that did not carry the generation (Video.Seek used to return before stamping) posts generation 0.
        Step(ref s, ref fx, Report(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 2_300, arg: 12_000, gen: 0u));
        Assert.Equal(75_000, s.PosMs);
        Assert.Equal(qpc, s.PosQpc);

        // The host now POSTS Seeked when the seek it owes has landed (HostRules.SeekLanded) — stamped with the generation it was given.
        Step(ref s, ref fx, Report(Playback.AudioSignal.Seeked, s.LoadEpoch, nowMs: 2_500, arg: 75_040, gen));
        Assert.Equal(75_040, s.PosMs);
        Assert.Equal(gen, s.LastSeekLandedGen);                          // what releases the bar's hold (it timed out before this existed)
        Assert.True(Playback.SeekLandedAfter(0u, s.LastSeekLandedGen));

        Step(ref s, ref fx, Report(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 3_000, arg: 75_540, gen));
        Assert.Equal(75_540, s.PosMs);                                   // the 5 Hz path flows again
    }

    // ── loads seed the pump's generation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pump_seeded_with_the_reducers_generation_lands_its_first_reports_and_an_unseeded_one_is_dropped_for_the_whole_load()
    {
        var s = Playing(upNext: 3);
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.Seek(30_000, nowMs: 1_000));
        Step(ref s, ref fx, Playback.Input.Seek(40_000, nowMs: 1_100));
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.Next(nowMs: 1_200));
        Assert.True(fx.Load);
        uint seeded = fx.SeekGen;
        Assert.Equal(s.SeekGen, seeded);                                  // Audio.Load(…, seekGen) / Video.Load(…, gen) start from THIS
        Assert.Equal(2u, seeded);                                         // the counter never resets: it carries across loads
        uint epoch = s.LoadEpoch;
        int afterLoad = s.PosMs;

        Step(ref s, ref fx, Report(Playback.AudioSignal.Position, epoch, nowMs: 1_700, arg: 5_000, gen: 0u));    // a pump nobody seeded
        Assert.Equal(afterLoad, s.PosMs);                                  // every report of the load would be dropped like this one
        Step(ref s, ref fx, Report(Playback.AudioSignal.Position, epoch, nowMs: 1_800, arg: 5_000, seeded));
        Assert.Equal(5_000, s.PosMs);
    }

    // ── the bar's hold protocol, end to end over the reducer ────────────────────────────────────────────────────────

    [Fact]
    public void The_hold_ends_on_the_first_landing_past_the_sample_even_when_another_seek_was_queued_ahead_of_the_commit()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        uint sample = s.SeekGen;                                          // the bar samples BEFORE it posts its commit …
        Assert.False(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));

        Step(ref s, ref fx, Playback.Input.Seek(20_000, nowMs: 1_000));   // … but a seek (a keyboard step) was already queued ahead of it …
        Step(ref s, ref fx, Playback.Input.Seek(95_000, nowMs: 1_005));   // … and then the commit's own
        uint first = fx.SeekGen - 1u, commit = fx.SeekGen;
        Assert.Equal(sample + 2u, commit);                                // the commit's generation is NOT sample + 1: it never has to be predicted

        // the first seek's confirmation is dropped and releases nothing …
        Step(ref s, ref fx, Report(Playback.AudioSignal.Seeked, s.LoadEpoch, nowMs: 1_100, arg: 20_010, first));
        Assert.False(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));
        // … the commit's lands, and the hold is over
        Step(ref s, ref fx, Report(Playback.AudioSignal.Seeked, s.LoadEpoch, nowMs: 1_200, arg: 95_020, commit));
        Assert.True(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));
        Assert.False(Playback.SeekLandedAfter(s.SeekGen, s.LastSeekLandedGen));   // a sample taken AFTER the drain needs a newer landing again
    }

    [Fact]
    public void A_newer_seek_that_superseded_the_commit_releases_its_hold_too()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        uint sample = s.SeekGen;
        Step(ref s, ref fx, Playback.Input.Seek(40_000, nowMs: 1_000));   // the commit
        Step(ref s, ref fx, Playback.Input.Seek(150_000, nowMs: 1_050));  // the user keeps dragging: a newer seek, before the first landed

        Step(ref s, ref fx, Report(Playback.AudioSignal.Seeked, s.LoadEpoch, nowMs: 1_300, arg: 150_030, s.SeekGen));

        Assert.True(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));   // the commit's own Seeked was dropped; the newer one stands in for it
    }

    [Fact]
    public void A_commit_the_reducer_does_not_turn_into_a_seek_bumps_nothing_so_only_the_bars_timeout_ends_the_hold()
    {
        var s = Playing();
        s.Parked = true;                                                  // nothing live to seek: the deck only moves where its eventual load starts
        var fx = new Playback.Effects();
        uint sample = s.SeekGen;

        Step(ref s, ref fx, Playback.Input.Seek(42_000, nowMs: 1_000));

        Assert.False(fx.Seek);
        Assert.Equal(sample, s.SeekGen);
        Assert.Equal(42_000, s.PosMs);                                    // the drop point is painted …
        Assert.False(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));   // … but no landing will ever come past the sample
        Assert.Equal(0u, s.LastSeekLandedGen);
    }

    [Fact]
    public void A_commit_to_a_foreign_owner_bumps_no_local_generation_either()
    {
        // U-3: the drop point is forwarded as ONE seek_to and held against the owner's pre-seek reports; OUR pump has nothing to land.
        var s = Playing();
        var fx = new Playback.Effects();
        ulong phone = Playback.DeviceHash("phone");
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, phone, 1_000, AckId: 0);
        var remote = new Playback.RemoteState(true, EntityId.ForGid(EntityKind.Track, (UInt128)0xBEEFUL), true, false, false,
            10_000, 1_000, 200_000, false, Spotify.Decode.RepeatMode.Off, -1, NoPrev: false, NoNext: false, NoSeek: false);
        Step(ref s, ref fx, Playback.Input.Cluster(in frame, in remote, nowMs: 1_000));
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();
        uint sample = s.SeekGen;

        Step(ref s, ref fx, Playback.Input.Seek(120_000, nowMs: 5_000));

        Assert.True(fx.SendRemote);
        Assert.False(fx.Seek);
        Assert.Equal(sample, s.SeekGen);
        Assert.False(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));
    }

    // ── the wrap, end to end ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_generation_wraps_and_the_reducer_the_guards_and_the_bar_all_keep_their_order()
    {
        var s = Playing();
        s.SeekGen = uint.MaxValue;                                        // 2³²−1 local seeks later
        s.LastSeekLandedGen = uint.MaxValue;                              // …the last of which had landed
        var fx = new Playback.Effects();
        uint sample = s.SeekGen;
        Assert.False(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));

        Step(ref s, ref fx, Playback.Input.Seek(70_000, nowMs: 1_000));

        Assert.Equal(0u, s.SeekGen);                                      // wrapped
        Assert.Equal(0u, fx.SeekGen);
        Assert.False(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));          // not landed yet

        // a report stamped before the wrap is stale; the wrapped generation's own lands
        Step(ref s, ref fx, Report(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 1_100, arg: 3_000, uint.MaxValue));
        Assert.Equal(70_000, s.PosMs);
        Step(ref s, ref fx, Report(Playback.AudioSignal.Seeked, s.LoadEpoch, nowMs: 1_200, arg: 70_020, gen: 0u));
        Assert.Equal(70_020, s.PosMs);
        Assert.Equal(0u, s.LastSeekLandedGen);
        Assert.True(Playback.SeekLandedAfter(sample, s.LastSeekLandedGen));           // released: 0 is NEWER than 2³²−1
    }
}
