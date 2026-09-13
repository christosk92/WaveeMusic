// ── Wavee.Tests/ClusterDecodeTests.cs — the Connect half of Spotify.Decode (Wave 2, owner E) ─────────────────────
//
// Wave 2's gate for `Spotify/+Spotify.Decode.Connect.cs`: the cluster fold that Wave 3's `Playback` consumes, and
// the remote-command decode the dealer's REQUEST frames carry. Both are pure values over spans, so both are pinned
// here with no session, no socket and no reducer — which is the point of splitting the decode out of the glue.
//
// The three rules that are easy to lose in a port, each with a fact of its own below:
//   • the volume slider follows the ACTIVE device, and ours is the fallback only when nobody is active;
//   • a restriction is a restriction when its reason list is NON-EMPTY, never when the field is merely present;
//   • `message_id` is uint32 on the wire and routinely exceeds int.MaxValue — reading it narrow threw in 0.2.9 and
//     discarded the whole command through the outer catch.

using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using P = Wavee.Protocol.Player;

namespace Wavee.Tests;

public class ClusterDecodeTests
{
    const string Ours = "wavee-device";
    const string Phone = "phone-device";

    static readonly byte[] OursUtf8 = Encoding.UTF8.GetBytes(Ours);

    static P.Cluster Fixture() => new()
    {
        ActiveDeviceId = Phone,
        ServerTimestampMs = 1_700_000_000_000,
        StartedPlayingAtTimestamp = 1_600_000_000_000,
        PlayerState = new P.PlayerState
        {
            Timestamp = 1_699_999_000_000,
            ContextUri = "spotify:playlist:0dijb70Boi9TIdmiLLq13V",
            PositionAsOfTimestamp = 5_000,
            Duration = 234_959,
            IsPlaying = true,
            QueueRevision = "rev-1",
            Track = new P.ProvidedTrack
            {
                Uri = "spotify:track:7idegBIikag5rTZP4WZihP",
                Uid = "2a826aa43895001e",
                Provider = "context",
                Metadata =
                {
                    { "title", "Cold Brew Chapters" },
                    { "artist_name", "roti." },
                    { "album_title", "Let's work slow and easy" },
                    { "image_url", "https://i.scdn.co/image/small" },
                    { "image_xlarge_url", "https://i.scdn.co/image/xlarge" },
                    { "duration", "234959" },
                },
            },
            Options = new P.ContextPlayerOptions { ShufflingContext = true, RepeatingTrack = true },
            Restrictions = new P.Restrictions { DisallowSkippingPrevReasons = { "no_prev_track" } },
            NextTracks = { new P.ProvidedTrack { Uri = "spotify:track:1111111111111111111111" } },
            PrevTracks =
            {
                new P.ProvidedTrack { Uri = "spotify:track:2222222222222222222222" },
                new P.ProvidedTrack { Uri = "spotify:track:3333333333333333333333" },
            },
        },
        Device =
        {
            { Phone, new P.DeviceInfo { DeviceId = Phone, Name = "Christos's Phone", Volume = 40_000, DeviceType = P.DeviceType.Smartphone } },
            { Ours, new P.DeviceInfo { DeviceId = Ours, Name = "Wavee", Volume = 20_000, DeviceType = P.DeviceType.Computer } },
        },
    };

    // ── the cluster fold ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_cluster_carries_the_remotes_claim_and_both_clocks()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.Equal(Phone, Encoding.UTF8.GetString(buffer.Utf8(delta.ActiveDeviceId)));
        Assert.True(delta.IsPlaying);
        Assert.False(delta.IsPaused);
        Assert.Equal(5_000, delta.PositionAsOfMs);
        Assert.Equal(234_959, delta.DurationMs);
        Assert.Equal(1_699_999_000_000, delta.TimestampMs);
        Assert.Equal(1_700_000_000_000, delta.ServerTimestampMs);
        Assert.Equal(1_600_000_000_000, delta.ActiveStartedPlayingAt);
        Assert.Equal("rev-1", Encoding.UTF8.GetString(buffer.Utf8(delta.QueueRevision)));
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void The_current_track_comes_off_the_metadata_map_with_the_cover_falling_back_by_size()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.True(delta.HasTrack);
        Assert.Equal("Cold Brew Chapters", Encoding.UTF8.GetString(buffer.Utf8(delta.Track.Title)));
        Assert.Equal("roti.", Encoding.UTF8.GetString(buffer.Utf8(delta.Track.ArtistName)));
        Assert.Equal("https://i.scdn.co/image/xlarge", Encoding.UTF8.GetString(buffer.Utf8(delta.Track.Image)));
        Assert.Equal(234_959, delta.Track.DurationMs);
        Assert.Equal("2a826aa43895001e", Encoding.UTF8.GetString(buffer.Utf8(delta.Track.Uid)));
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void The_two_queues_are_their_own_contiguous_runs_and_the_current_track_is_in_neither()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.Equal(1, delta.NextCount);
        Assert.Equal(2, delta.PrevCount);
        Assert.Equal("spotify:track:1111111111111111111111",
            Encoding.UTF8.GetString(buffer.Utf8(buffer.Tracks(delta.NextStart, delta.NextCount)[0].Uri)));
        Assert.Equal("spotify:track:3333333333333333333333",
            Encoding.UTF8.GetString(buffer.Utf8(buffer.Tracks(delta.PrevStart, delta.PrevCount)[1].Uri)));
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void The_slider_follows_the_active_device_and_ours_is_only_the_fallback()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.Equal(2, delta.DeviceCount);
        Assert.Equal(20_000, delta.OurVolume);
        Assert.Equal(40_000, delta.ActiveVolume);

        var devices = buffer.Devices(delta.DeviceStart, delta.DeviceCount);
        bool sawUs = false, sawPhone = false;
        foreach (ref readonly var d in devices)
        {
            if (d.Kind == Spotify.Decode.DeviceKind.ThisDevice) { sawUs = true; Assert.Equal("Wavee", Encoding.UTF8.GetString(buffer.Utf8(d.Name))); }
            if (d.Kind == Spotify.Decode.DeviceKind.Phone) sawPhone = true;
        }
        Assert.True(sawUs);
        Assert.True(sawPhone);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void With_nobody_active_the_slider_falls_back_to_our_own_volume()
    {
        var cluster = Fixture();
        cluster.ActiveDeviceId = "";
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(cluster.ToByteArray(), OursUtf8, buffer);

        Assert.Equal(20_000, delta.ActiveVolume);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void A_restriction_is_a_restriction_only_when_its_reason_list_is_not_empty()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.True(delta.NoPrev);
        Assert.False(delta.NoNext);
        Assert.False(delta.NoSeek);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void Repeating_track_beats_repeating_context()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);
        Assert.Equal(Spotify.Decode.RepeatMode.Track, delta.Repeat);
        Assert.True(delta.Shuffling);

        var context = Fixture();
        context.PlayerState.Options = new P.ContextPlayerOptions { RepeatingContext = true };
        var second = Spotify.Decode.Cluster(context.ToByteArray(), OursUtf8, buffer);
        Assert.Equal(Spotify.Decode.RepeatMode.Context, second.Repeat);
        Assert.False(second.Shuffling);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void A_cluster_update_stamps_the_push_origin_and_its_reason()
    {
        var update = new P.ClusterUpdate
        {
            Cluster = Fixture(),
            UpdateReason = P.ClusterUpdateReason.DeviceVolumeChanged,
        }.ToByteArray();

        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.ClusterUpdate(update, OursUtf8, buffer);

        Assert.Equal(Spotify.Decode.ClusterOrigin.Push, delta.Origin);
        Assert.Equal((int)P.ClusterUpdateReason.DeviceVolumeChanged, delta.UpdateReason);
        Assert.True(delta.HasTrack);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void A_warm_cluster_decode_allocates_nothing()
    {
        var bytes = Fixture().ToByteArray();
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        for (int i = 0; i < 2; i++) { Spotify.Decode.Cluster(bytes, OursUtf8, buffer); buffer.Reset(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Spotify.Decode.Cluster(bytes, OursUtf8, buffer);
        long after = GC.GetAllocatedBytesForCurrentThread();
        buffer.Reset();
        Spotify.Decode.ClusterBuffer.Return(buffer);

        Assert.Equal(0L, after - before);
    }

    // ── the remote command ──────────────────────────────────────────────────────────────────────────────────────────

    static Spotify.Decode.RemoteCommand Parse(string json)
        => Spotify.Decode.ConnectCommand(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_seek_survives_a_message_id_wider_than_an_int_and_a_position_spelled_as_a_float()
    {
        // Both shapes threw in 0.2.9 and discarded the whole command through the outer catch.
        var cmd = Parse("""{"message_id":4294967295,"sent_by_device_id":"abc","command":{"endpoint":"seek_to","position":42000.0}}""");

        Assert.Equal(Spotify.Decode.RemoteCmd.SeekTo, cmd.Kind);
        Assert.True(cmd.Ok);
        Assert.Equal(42_000, cmd.SeekToMs);
        Assert.Equal(int.MaxValue, cmd.MessageId);
        Assert.NotEqual(0ul, cmd.SenderHash);
    }

    [Fact]
    public void A_boolean_verb_reads_its_value_and_a_track_verb_reads_its_identity()
    {
        var shuffle = Parse("""{"command":{"endpoint":"set_shuffling_context","value":true}}""");
        Assert.Equal(Spotify.Decode.RemoteCmd.SetShufflingContext, shuffle.Kind);
        Assert.True(shuffle.BoolArg);

        var next = Parse("""{"command":{"endpoint":"skip_next","track":{"uri":"spotify:track:7idegBIikag5rTZP4WZihP","uid":"u"}}}""");
        Assert.Equal(Spotify.Decode.RemoteCmd.SkipNext, next.Kind);
        Assert.Equal(EntityKind.Track, next.Track.Kind);
        Assert.Equal("spotify:track:7idegBIikag5rTZP4WZihP", next.Track.Text);
    }

    [Fact]
    public void An_endpoint_we_do_not_know_is_not_ok_and_carries_no_verb()
    {
        var cmd = Parse("""{"message_id":7,"command":{"endpoint":"frobnicate"}}""");
        Assert.Equal(Spotify.Decode.RemoteCmd.Unknown, cmd.Kind);
        Assert.False(cmd.Ok);
        Assert.Equal(7, cmd.MessageId);
    }

    [Fact]
    public void The_dedupe_key_folds_the_endpoint_so_a_recycled_message_id_is_not_a_replay()
    {
        // Message ids are recycled ACROSS endpoints: without the endpoint term a fresh `set_shuffling_context`
        // landing on an old id looked like an exact replay of an unrelated command and was silently dropped.
        var a = Parse("""{"message_id":9,"sent_by_device_id":"abc","command":{"endpoint":"seek_to","position":1}}""");
        var b = Parse("""{"message_id":9,"sent_by_device_id":"abc","command":{"endpoint":"set_shuffling_context","value":true}}""");
        var c = Parse("""{"message_id":9,"sent_by_device_id":"abc","command":{"endpoint":"seek_to","position":2}}""");

        Assert.NotEqual(a.DedupeKey, b.DedupeKey);
        Assert.Equal(a.DedupeKey, c.DedupeKey);
    }

    // `PutState` was a named stub in Wave 2 and is a real encoder in Wave 3 (owner G, against the field map the stub
    // wrote down). Its facts live with the reducer that feeds it — `PlaybackRulesTests`, which round-trips a snapshot
    // through the generated `PutStateRequest` parser.
}
