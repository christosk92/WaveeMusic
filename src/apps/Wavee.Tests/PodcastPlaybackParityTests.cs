using System.Text;
using Wavee;
using Xunit;
using P = Wavee.Protocol.Player;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PodcastPlaybackParityTests
{
    static readonly EntityId Episode = EntityId.ForGid(EntityKind.Episode, (UInt128)12345);
    static readonly EntityId Show = EntityId.ForGid(EntityKind.Show, (UInt128)67890);

    static Playback.State Parked()
    {
        TestScope.Fresh();
        var state = Playback.State.Initial;
        state.Us = 1;
        state.Current = Entities.Ref(Episode);
        state.CurrentId = Episode;
        state.Context = Show;
        state.Phase = Playback.Phase.Paused;
        state.Parked = true;
        state.Error = Playback.Fault.Unavailable;
        state.PosMs = 3000;
        return state;
    }

    [Fact]
    public void Faulted_resume_retries_load_at_the_requested_position()
    {
        var state = Parked();
        var fx = default(Playback.Effects);
        var command = new Spotify.Decode.RemoteCommand(Spotify.Decode.RemoteCmd.Resume, true, 14, 0, false, default, 2, 0, 3);
        Playback.Step(ref state, Playback.Input.Controller(command, 1000), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(3000, fx.LoadFromMs);
        Assert.Equal(Playback.Fault.None, state.Error);
        Assert.True(fx.PublishState);
        Assert.Equal(14u, state.LastCommandMessageId);
    }

    [Fact]
    public void Faulted_seek_moves_the_parked_position_and_publishes_even_at_zero()
    {
        var state = Parked();
        var fx = default(Playback.Effects);
        var command = new Spotify.Decode.RemoteCommand(Spotify.Decode.RemoteCmd.SeekTo, true, 15, 0, false, default, 2, 0, 4);
        Playback.Step(ref state, Playback.Input.Controller(command, 1000), ref fx);
        Assert.Equal(0, state.PosMs);
        Assert.False(fx.Seek);
        Assert.True(fx.PublishState);
        Assert.Equal(15u, state.LastCommandMessageId);
    }

    [Fact]
    public void Explicit_zero_start_is_not_replaced_by_saved_progress()
    {
        var state = Parked();
        Staging staging = Staging.Rent();
        ref StagedEpisode saved = ref staging.Episodes.RowFor(Episode, Authority.Full, (uint)(EpisodeFields.Progress | EpisodeFields.Identity));
        saved.ProgressMs = 45000;
        saved.DurationMs = 90000;
        TestScope.CommitAndPublish(staging);
        Assert.Equal(45000, Playback.EpisodeStartOf(Episode));
        var fx = default(Playback.Effects);
        var input = new Playback.Input(Playback.InputKind.Play, state.Current, Episode, Show, QueueCursor.None,
            0, Playback.Input.ExplicitPositionBit, nowMs: 1000);
        Playback.Step(ref state, input, ref fx);
        Assert.True(fx.Load);
        Assert.Equal(0, fx.LoadFromMs);
    }

    [Fact]
    public void Episode_rate_extrapolates_content_time_and_preserves_fractional_rates()
    {
        var state = Parked();
        state.Phase = Playback.Phase.Playing;
        state.PosQpc = 1000;
        state.EpisodeRate = 1.9f;
        Assert.InRange(state.Position(2000), 4899, 4900);
        Assert.Equal(1.9f, Playback.ValidEpisodeSpeed(1.9f));
    }

    [Fact]
    public void Speed_applies_to_episodes_and_to_tracks_while_video_is_up()
    {
        Assert.True(Playback.SpeedApplies(EntityKind.Episode, videoWanted: false));
        Assert.False(Playback.SpeedApplies(EntityKind.Track, videoWanted: false));
        Assert.True(Playback.SpeedApplies(EntityKind.Track, videoWanted: true));
        var track = EntityId.ForGid(EntityKind.Track, 1);
        Assert.Equal(1f, Playback.RateFor(track, videoWanted: false));
        float prior = Playback.EpisodeSpeed.Peek();
        try
        {
            Playback.EpisodeSpeed.Value = 1.25f;
            Assert.Equal(1.25f, Playback.RateFor(track, videoWanted: true));
        }
        finally { Playback.EpisodeSpeed.Value = prior; }
    }

    [Fact]
    public void Speed_write_uses_the_captured_global_content_value_shape()
    {
        byte[] body = Playback.SpeedSettingsBody(1.9f);
        var root = new Spotify.Decode.ProtoReader(body);
        Assert.True(root.Bytes(1).SequenceEqual("GLOBAL"u8));
        var setting = new Spotify.Decode.ProtoReader(new Spotify.Decode.ProtoReader(body).Bytes(2));
        Assert.Equal(3L, setting.Varint(1, 0));
        var value = new Spotify.Decode.ProtoReader(new Spotify.Decode.ProtoReader(new Spotify.Decode.ProtoReader(body).Bytes(2)).Bytes(2));
        Assert.True(value.Next());
        Assert.Equal(4, value.Field);
        Assert.Equal(1.9f, BitConverter.UInt32BitsToSingle(value.Fixed32()));
    }

    [Fact]
    public void Autopodcast_encodes_repeated_episode_field_one_without_show_context()
    {
        Span<byte> bytes = stackalloc byte[256];
        int count = Spotify.Decode.AutopodcastRequest([Episode, Show, EntityId.ForGid(EntityKind.Episode, (UInt128)42)], bytes);
        var r = new Spotify.Decode.ProtoReader(bytes[..count]);
        int rows = 0;
        while (r.Next()) { Assert.Equal(1, r.Field); Assert.StartsWith("spotify:episode:", Encoding.UTF8.GetString(r.Bytes())); rows++; }
        Assert.Equal(2, rows);
    }

    [Fact]
    public void Episode_put_preserves_desired_rate_while_paused_and_omits_show_index()
    {
        var identity = new Playback.DeviceIdentity("device", "Wavee", "client", "Windows", "1", "3.2.6");
        var row = new Playback.WireRow(Episode, 1, "canonical", QueueProvider.Context);
        var window = new Playback.WireWindow(row, [], [], -1);
        var origin = new Playback.WireOrigin("show", "captured-version", Show.Text, "", "your_library", "remote", "command-123", [new("sorting.criteria", "added_at ASC")]);
        var wire = new Playback.WireExtras(window, 1, default, false, "Speakers", "remote", 1.9f, origin);
        var snap = new Playback.Snapshot(identity, true, Playback.PublishReason.PlayerStateChanged, 1, 100,
            1000, 1000, 0, true, Episode, Show, "canonical", 45000, 1000, 90000, false, true, false, false,
            Spotify.Decode.RepeatMode.Off, Playback.PlayableKind.Audio, wire: wire);
        byte[] bytes = new byte[Spotify.Decode.PutStateCapacity(snap)];
        int n = Spotify.Decode.PutState(snap, bytes);
        var ps = P.PutStateRequest.Parser.ParseFrom(bytes.AsSpan(0, n)).Device.PlayerState;
        Assert.Equal(0, ps.PlaybackSpeed);
        Assert.Equal(1.9f, ps.Options.PlaybackSpeed);
        Assert.Null(ps.Index);
        Assert.Equal("show", ps.PlayOrigin.FeatureIdentifier);
        Assert.Equal("your_library", ps.PlayOrigin.ReferrerIdentifier);
        Assert.Equal("command-123", ps.SessionCommandId);
        Assert.Equal("added_at ASC", ps.ContextMetadata["sorting.criteria"]);
        Assert.Empty(ps.Restrictions.DisallowSettingPlaybackSpeedReasons);
    }
}
