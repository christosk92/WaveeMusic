// ── Wavee.Tests/MuteRulesTests.cs — mute, unmute and the volume write, as reducer decisions (playback smoothness, #167) ──
//
// Plan docs/plans/wavee/playback-smoothness-implementation.md §4.9 (DoMute / DoVolume / DoTick) and §5. The facts are the
// audit's U-4 and V-1, with the first-launch volume of D6:
//
//   U-4  With the sink muted and the volume at 0, unmute was a NO-OP: `Muted` folded "the sink is muted OR the volume is at the
//        floor", and nothing ever raised the volume, so the listener could never un-mute. And dragging the volume up never
//        cleared the sink mute (the mute glyph and the audio disagreed about who was silent). The sink-mute bit used to be a
//        host static (`s_sinkMuted`) the reducer could not see; it is `State.SinkMuted` now, so both are DECISIONS here.
//   V-1  Every drain that carried a volume wrote the registry, bumped `SettingsEpoch` (re-rendering every settings
//        subscriber) and announced a PUT: ~20/s during a drag. The reducer now debounces: the first change stamps
//        `VolumeDirtySinceMs`, and the ticker turns it into ONE `PersistVolume` once `VolumePersistDebounceMs` have passed.
//   D6   First-launch volume −6 dB: `0.794³ = 0.5005` through the cubic taper. `UnmuteDefault` is the same number, so the level
//        an unmute returns to and the level a fresh profile starts at are one decision.
//
// The reducer owns no clock — every input carries its frame stamp — so the debounce is a unit test with plain numbers.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class MuteRulesTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    static QueueEdge Row(QueueBucket bucket, ulong id) => new(id, (byte)QueueProvider.Context, (byte)bucket);

    static EntityRef Track(int n)
    {
        var id = EntityId.ForGid(EntityKind.Track, (UInt128)(ulong)(0x7000 + n));
        return new EntityRef(EntityKind.Track, Entities.Current.Tracks.Slot(id));
    }

    /// <summary>One track on the deck, playing and OURS, at the state's initial full volume.</summary>
    static Playback.State Deck()
    {
        TestScope.Fresh();
        EntityRef[] refs = [Track(0), Track(1)];
        Queue.Replace(refs, [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2)]);

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

    /// <summary>A volume input at an exact WIRE value (0..65535), for the boundary facts: <c>Input.Volume(float)</c> rounds.</summary>
    static Playback.Input WireVolume(int wire, long nowMs = 0)
        => new(Playback.InputKind.SetVolume, intArg: wire, nowMs: nowMs);

    /// <summary>One wire step of the volume scale — the resolution a restored volume can be asserted to.</summary>
    const float WireStep = 1f / Playback.MaxWireVolume;

    // ── U-4: unmute at volume 0 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Muting_the_sink_is_recorded_in_the_state_and_unmuting_clears_it()
    {
        var s = Deck();
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Mute(true, hasSink: true), ref fx);
        Assert.True(s.SinkMuted);
        Assert.True(fx.Mute);
        Assert.True(fx.MuteOn);
        Assert.Equal(1f, s.Volume);                                      // a real sink mute leaves the volume where it was

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Mute(false, hasSink: true), ref fx);
        Assert.False(s.SinkMuted);
        Assert.True(fx.Mute);
        Assert.False(fx.MuteOn);
        Assert.False(fx.Volume);                                         // volume was audible: unmute has nothing to restore
        Assert.Equal(1f, s.Volume);
    }

    [Fact]
    public void A_volume_zero_mute_without_a_sink_never_marks_the_sink_muted()
    {
        var s = Deck();
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Mute(true, hasSink: false), ref fx);

        Assert.False(s.SinkMuted);                                       // the mute is the volume, not the sink
        Assert.False(fx.Mute);
        Assert.Equal(0f, s.Volume);
    }

    [Fact]
    public void Unmuting_at_volume_zero_restores_the_default_when_nothing_was_remembered()
    {
        var s = Deck();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Mute(true, hasSink: true), ref fx);          // the sink is muted…
        Playback.Step(ref s, Playback.Input.Volume(0f, nowMs: 100), ref fx);              // …and the slider is dragged to the floor
        Assert.Equal(0f, s.Volume);
        Assert.True(s.SinkMuted);                                        // (a drop TO the floor is not a raise)
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Mute(false, hasSink: true), ref fx);

        Assert.False(s.SinkMuted);
        Assert.True(fx.Mute);
        Assert.False(fx.MuteOn);
        Assert.True(fx.Volume);                                          // before U-4 this was a no-op: silent forever
        Assert.True(Math.Abs(Playback.UnmuteDefault - s.Volume) <= WireStep, $"volume {s.Volume} after the unmute");
        Assert.Equal(s.Volume, fx.VolumeValue);                          // the sink hears it too
        Assert.True(Playback.UnmuteDefault > Playback.MuteFloor);
    }

    [Fact]
    public void Unmuting_at_volume_zero_restores_the_level_the_mute_took_away_when_one_is_remembered()
    {
        var s = Deck();
        s.Volume = 0f;
        s.SinkMuted = true;
        s.MuteRestoreVolume = 0.4f;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Mute(false, hasSink: true), ref fx);

        Assert.True(Math.Abs(0.4f - s.Volume) <= WireStep, $"volume {s.Volume}");
        Assert.True(fx.Volume);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(Playback.MuteFloor, true)]           // the floor is INCLUSIVE: the glyph reads "muted" at it
    [InlineData(Playback.MuteFloor * 2f, false)]
    [InlineData(0.5f, false)]
    public void Only_a_volume_at_or_below_the_floor_is_restored_on_unmute(float volume, bool restores)
    {
        var s = Deck();
        s.Volume = volume;
        s.SinkMuted = true;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Mute(false, hasSink: true), ref fx);

        Assert.False(s.SinkMuted);
        Assert.Equal(restores, fx.Volume);
        if (restores) Assert.True(s.Volume > Playback.MuteFloor);
        else Assert.Equal(volume, s.Volume);
    }

    // ── U-4: a raise clears the sink mute ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_volume_raise_clears_the_sink_mute_and_emits_the_unmute_only_then()
    {
        var s = Deck();
        var fx = new Playback.Effects();

        // Nothing is muted: a volume change is just a volume change — no mute effect rides along.
        Playback.Step(ref s, Playback.Input.Volume(0.3f, nowMs: 100), ref fx);
        Assert.True(fx.Volume);
        Assert.False(fx.Mute);

        // The sink is muted: the SAME kind of input now un-mutes it, in the same drain, and still sets the volume.
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Mute(true, hasSink: true), ref fx);
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Volume(0.5f, nowMs: 200), ref fx);

        Assert.False(s.SinkMuted);
        Assert.True(fx.Mute);
        Assert.False(fx.MuteOn);                                         // MuteOn == false: it is an UNMUTE
        Assert.True(fx.Volume);
        Assert.True(Math.Abs(0.5f - s.Volume) <= WireStep);

        // And only once: the next change finds an audible sink.
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Volume(0.7f, nowMs: 300), ref fx);
        Assert.False(fx.Mute);
    }

    [Fact]
    public void A_volume_that_does_not_clear_the_floor_leaves_the_sink_muted()
    {
        int floorWire = Playback.Input.WireVolume(Playback.MuteFloor);

        var s = Deck();
        s.Volume = 0f;
        s.SinkMuted = true;
        var fx = new Playback.Effects();

        Playback.Step(ref s, WireVolume(floorWire, nowMs: 100), ref fx);                  // exactly the floor: still "muted"
        Assert.True(s.SinkMuted);
        Assert.False(fx.Mute);

        Playback.Step(ref s, WireVolume(floorWire + 1, nowMs: 110), ref fx);              // one wire step above it: a raise
        Assert.False(s.SinkMuted);
        Assert.True(fx.Mute);
        Assert.False(fx.MuteOn);
    }

    [Fact]
    public void A_volume_for_the_phones_slider_never_touches_our_sink_mute()
    {
        var s = Deck();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Mute(true, hasSink: true), ref fx);
        ulong phone = Playback.DeviceHash("phone");
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, phone, 1_000);
        var remote = new Playback.RemoteState(true, EntityId.ForGid(EntityKind.Track, (UInt128)0xBEEFUL), true, false, false,
            42_000, 1_000, 200_000, false, Spotify.Decode.RepeatMode.Off, 32_768, NoPrev: false, NoNext: false, NoSeek: false);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, in remote, nowMs: 1_000), ref fx);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Volume(0.9f, nowMs: 2_000), ref fx);

        Assert.True(fx.SendRemote);                                      // a volume PUT to the phone…
        Assert.True(fx.RemoteIsVolume);
        Assert.False(fx.Mute);                                           // …and our own sink is untouched
        Assert.True(s.SinkMuted);
        Assert.Equal(0L, s.VolumeDirtySinceMs);                          // nothing of OURS to persist either
    }

    // ── D6: the first-launch volume ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_unmute_default_is_the_minus_six_dB_first_launch_volume()
    {
        Assert.Equal(0.794f, Playback.UnmuteDefault);

        float amplitude = Playback.Audio.VolumeTaper.Amplitude(Playback.UnmuteDefault);
        double db = 20.0 * Math.Log10(amplitude);
        Assert.True(Math.Abs(0.5005 - amplitude) < 0.001, $"0.794³ = {amplitude}");
        Assert.True(db is > -6.1 and < -5.9, $"{db:F2} dB through the cubic taper (D6: −6.01 dB; the old 0.7 was −9.3 dB)");
    }

    // ── V-1 / V-PA11: the volume write is debounced by the reducer ──────────────────────────────────────────────────

    [Fact]
    public void A_volume_change_stamps_the_dirty_clock_and_never_persists_in_its_own_drain()
    {
        var s = Deck();
        var fx = new Playback.Effects();
        Assert.Equal(0L, s.VolumeDirtySinceMs);

        Playback.Step(ref s, Playback.Input.Volume(0.5f, nowMs: 10_000), ref fx);

        Assert.True(fx.Volume);                                          // the sink hears it at once…
        Assert.False(fx.PersistVolume);                                  // …the registry does not (V-1: it was once per drain)
        Assert.Equal(10_000L, s.VolumeDirtySinceMs);
    }

    [Fact]
    public void The_tick_persists_the_volume_once_after_the_debounce_and_not_before()
    {
        var s = Deck();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Volume(0.5f, nowMs: 10_000), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Tick(10_000 + Playback.VolumePersistDebounceMs - 1), ref fx);
        Assert.False(fx.PersistVolume);                                  // one millisecond early
        Assert.NotEqual(0L, s.VolumeDirtySinceMs);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Tick(10_000 + Playback.VolumePersistDebounceMs), ref fx);
        Assert.True(fx.PersistVolume);                                   // exactly at the debounce
        Assert.Equal(s.Volume, fx.VolumeValue);                          // the value the registry gets
        Assert.True(Math.Abs(0.5f - fx.VolumeValue) <= WireStep);
        Assert.Equal(0L, s.VolumeDirtySinceMs);                          // clean again

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Tick(10_000 + 2 * Playback.VolumePersistDebounceMs), ref fx);
        Assert.False(fx.PersistVolume);                                  // ONCE: the next tick has nothing to write
    }

    [Fact]
    public void A_drag_is_one_write_per_debounce_not_one_per_drain()
    {
        var s = Deck();
        var fx = new Playback.Effects();

        // Twenty slider steps, 20 ms apart, inside one debounce window: the first change starts the clock and the rest
        // neither restart it nor persist anything.
        for (int n = 0; n < 20; n++)
        {
            fx.Clear();
            Playback.Step(ref s, Playback.Input.Volume(1f - 0.02f * (n + 1), nowMs: 20_000 + 20L * n), ref fx);
            Assert.False(fx.PersistVolume);
            Assert.Equal(20_000L, s.VolumeDirtySinceMs);
        }

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Tick(20_000 + Playback.VolumePersistDebounceMs), ref fx);
        Assert.True(fx.PersistVolume);
        Assert.True(Math.Abs((1f - 0.02f * 20) - fx.VolumeValue) <= WireStep);   // the LAST value, not the first
    }

    [Fact]
    public void A_change_at_clock_zero_is_still_dirty_and_an_unchanged_volume_is_not()
    {
        var s = Deck();
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Volume(1f, nowMs: 0), ref fx);          // the same volume: not a change at all
        Assert.Equal(0L, s.VolumeDirtySinceMs);
        Assert.False(fx.Volume);

        Playback.Step(ref s, Playback.Input.Volume(0.5f, nowMs: 0), ref fx);        // a real change at frame-clock 0 (0 means "clean")
        Assert.NotEqual(0L, s.VolumeDirtySinceMs);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Tick(Playback.VolumePersistDebounceMs + 1), ref fx);
        Assert.True(fx.PersistVolume);
    }

    [Fact]
    public void A_paused_deck_still_closes_the_debounce_on_the_tick()
    {
        var s = Deck();
        s.Phase = Playback.Phase.Paused;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Volume(0.25f, nowMs: 5_000), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Tick(5_000 + Playback.VolumePersistDebounceMs), ref fx);

        Assert.True(fx.PersistVolume);
    }

    [Fact]
    public void A_tick_with_no_volume_change_persists_nothing()
    {
        var s = Deck();
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Tick(1_000_000), ref fx);

        Assert.False(fx.PersistVolume);
    }

    [Fact]
    public void Persist_volume_is_a_slot_the_host_will_not_skip()
    {
        // `Execute` returns early when `!Any` (V-PA1): a PersistVolume that did not count would be written by no one.
        var fx = new Playback.Effects();
        Assert.False(fx.Any);
        fx.PersistVolume = true;
        Assert.True(fx.Any);
        fx.Clear();
        Assert.False(fx.PersistVolume);
    }

    [Fact]
    public void A_sign_out_keeps_the_sink_mute_and_the_pending_volume_write()
    {
        var s = Deck();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Volume(0.5f, nowMs: 30_000), ref fx);
        Playback.Step(ref s, Playback.Input.Mute(true, hasSink: true), ref fx);
        float volume = s.Volume;
        Assert.True(s.SinkMuted);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Release(Playback.ReleaseCause.Logout), ref fx);

        Assert.True(s.SinkMuted);                                        // the sink stays muted across the account…
        Assert.Equal(volume, s.Volume);                                  // …with the volume the device had…
        Assert.Equal(30_000L, s.VolumeDirtySinceMs);                     // …and the write that was still pending

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Tick(30_000 + Playback.VolumePersistDebounceMs), ref fx);
        Assert.True(fx.PersistVolume);                                   // the signed-out device's volume is still saved
    }
}
