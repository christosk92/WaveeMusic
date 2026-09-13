// ── Wavee.Tests/SpotifyConnectTests.cs — the dealer route, the mailbox, and the outbound bodies ───────────────────
//
// Wave 2's gate for `Spotify/Spotify.Connect.cs`. The frames are BUILT here from the generated `Wavee.Protocol`
// messages rather than read from a checked-in blob — `Wavee.Tests.csproj` says why: a decoder pinned against the
// canonical encoder is pinned against something anybody can regenerate, and 0.2.9's `cluster-complex-queue.json` is a
// JSON capture of a shape this decoder no longer reads (the dealer's cluster payload is base64 protobuf).
//
// No socket is opened. `Spotify.Reply` no-ops without a dealer websocket and `Spotify.Post` defaults to running its
// action inline, which is what makes `OnDealer` a synchronous, assertable function in a unit test.

using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using Pb = Wavee.Protocol.Player;

namespace Wavee.Tests;

public class SpotifyConnectDealerTests
{
    static byte[] ClusterFrame(string activeDeviceId, string contextUri, long serverTimestampMs, bool playing)
    {
        var update = new Pb.ClusterUpdate
        {
            Cluster = new Pb.Cluster
            {
                ActiveDeviceId = activeDeviceId,
                ServerTimestampMs = serverTimestampMs,
                PlayerState = new Pb.PlayerState
                {
                    ContextUri = contextUri,
                    IsPlaying = playing,
                    IsPaused = !playing,
                    Duration = 180_000,
                    PositionAsOfTimestamp = 1_000,
                    Track = new Pb.ProvidedTrack { Uri = "spotify:track:abc", Uid = "q0" },
                },
            },
        };
        string payload = Convert.ToBase64String(update.ToByteArray());
        return Encoding.UTF8.GetBytes(
            "{\"type\":\"message\",\"uri\":\"hm://connect-state/v1/cluster\",\"payloads\":[\"" + payload + "\"]}");
    }

    static byte[] RequestFrame(string commandJson)
    {
        string payload = Convert.ToBase64String(Gzip(Encoding.UTF8.GetBytes(commandJson)));
        return Encoding.UTF8.GetBytes(
            "{\"type\":\"request\",\"key\":\"k1\",\"message_ident\":\"hm://connect-state/v1/player/command\"," +
            "\"payload\":{\"compressed\":\"" + payload + "\"}}");
    }

    static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    static void Drain() => Spotify.Connect.Clear();

    [Fact]
    public void A_cluster_frame_becomes_exactly_one_delta_in_the_mailbox()
    {
        Drain();
        Spotify.Connect.OnDealer(ClusterFrame("deviceB", "spotify:album:x", 1_700_000_000_000, playing: true));

        Assert.True(Spotify.Connect.TryDequeue(out Spotify.Connect.Item item));
        try
        {
            Assert.Equal(Spotify.Connect.ItemKind.Cluster, item.Kind);
            Assert.Equal(1_700_000_000_000, item.Delta.ServerTimestampMs);
            Assert.True(item.Delta.IsPlaying);
            Assert.NotNull(item.Buffer);
        }
        finally { Spotify.Connect.Release(item); }

        Assert.False(Spotify.Connect.TryDequeue(out _));
    }

    [Fact]
    public void The_cluster_deltas_text_resolves_through_the_buffer_it_carries()
    {
        Drain();
        Spotify.Connect.OnDealer(ClusterFrame("deviceB", "spotify:album:x", 1, playing: false));

        Assert.True(Spotify.Connect.TryDequeue(out Spotify.Connect.Item item));
        try
        {
            Assert.NotNull(item.Buffer);
            Assert.Equal("deviceB", Encoding.UTF8.GetString(item.Buffer!.Utf8(item.Delta.ActiveDeviceId)));
            Assert.Equal("spotify:album:x", Encoding.UTF8.GetString(item.Buffer!.Utf8(item.Delta.ContextUri)));
        }
        finally { Spotify.Connect.Release(item); }
    }

    [Fact]
    public void A_command_frame_becomes_one_remote_command()
    {
        Drain();
        Spotify.Connect.OnDealer(RequestFrame(
            "{\"message_id\":42,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"pause\"}}"));

        Assert.True(Spotify.Connect.TryDequeue(out Spotify.Connect.Item item));
        Assert.Equal(Spotify.Connect.ItemKind.RemoteCommand, item.Kind);
        Assert.Equal(Spotify.Decode.RemoteCmd.Pause, item.Command.Kind);
        Spotify.Connect.Release(item);
    }

    /// <summary>A frame that is neither a cluster nor a request is somebody else's traffic — the dealer carries a lot
    /// of it — and must not become an item nobody can interpret.</summary>
    [Fact]
    public void A_frame_we_do_not_read_enqueues_nothing()
    {
        Drain();
        Spotify.Connect.OnDealer("{\"type\":\"message\",\"uri\":\"hm://presence2/user/bob\"}"u8);
        Assert.False(Spotify.Connect.TryDequeue(out _));
    }

    [Fact]
    public void A_malformed_frame_is_dropped_rather_than_thrown()
    {
        Drain();
        Spotify.Connect.OnDealer("{not json"u8);
        Spotify.Connect.OnDealer([]);
        Assert.False(Spotify.Connect.TryDequeue(out _));
    }

    /// <summary>The mailbox is BOUNDED (C8). A controller pushing faster than a frame drains loses the OLDEST pushes,
    /// which are the ones that no longer describe anything, and the loss is counted rather than silent.</summary>
    [Fact]
    public void The_mailbox_drops_the_oldest_rather_than_growing()
    {
        Drain();
        int droppedBefore = Spotify.Connect.DroppedItems;

        for (int i = 0; i < Spotify.Connect.InboxDepth + 10; i++)
            Spotify.Connect.OnDealer(ClusterFrame("deviceB", "spotify:album:" + i, i + 1, playing: true));

        Assert.True(Spotify.Connect.Pending <= Spotify.Connect.InboxDepth);
        Assert.Equal(droppedBefore + 10, Spotify.Connect.DroppedItems);

        // What survived is the TAIL: the newest push is still there.
        Spotify.Connect.Item last = default;
        while (Spotify.Connect.TryDequeue(out Spotify.Connect.Item item))
        {
            Spotify.Connect.Release(last);
            last = item;
        }
        Assert.Equal((long)(Spotify.Connect.InboxDepth + 10), last.Delta.ServerTimestampMs);
        Spotify.Connect.Release(last);
    }

    [Fact]
    public void Clearing_the_mailbox_empties_it()
    {
        Drain();
        Spotify.Connect.OnDealer(ClusterFrame("deviceB", "spotify:album:x", 5, playing: true));
        Assert.Equal(1, Spotify.Connect.Pending);

        Spotify.Connect.Clear();

        Assert.Equal(0, Spotify.Connect.Pending);
        Assert.False(Spotify.Connect.TryDequeue(out _));
    }
}

public class SpotifyConnectOutboundTests
{
    /// <summary>The captured `SetVolumeCommand` body, byte for byte: volume 19496 is
    /// <c>08 a8 98 01 1a 00 22 04 'wlan'</c> — field 1 varint, an empty logging_params, and the connection type. It is
    /// hand-encoded rather than generated because three fields of a fixed wire spec do not need a message class, and
    /// because this assertion is then the whole specification.</summary>
    [Fact]
    public void The_volume_body_is_the_captured_nine_bytes()
    {
        Span<byte> buffer = stackalloc byte[16];
        int n = Spotify.Connect.VolumeBody(19496, buffer);

        ReadOnlySpan<byte> expected =
        [
            0x08, 0xa8, 0x98, 0x01,
            0x1a, 0x00,
            0x22, 0x04, (byte)'w', (byte)'l', (byte)'a', (byte)'n',
        ];
        Assert.True(buffer[..n].SequenceEqual(expected));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(65535, 65535)]
    [InlineData(100000, 65535)]
    public void The_volume_is_clamped_into_the_wires_range(int asked, int expected)
    {
        Span<byte> buffer = stackalloc byte[16];
        int n = Spotify.Connect.VolumeBody(asked, buffer);

        // Read field 1's varint back out.
        Assert.Equal(0x08, buffer[0]);
        long value = 0;
        int shift = 0;
        int i = 1;
        while (i < n)
        {
            byte b = buffer[i++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        Assert.Equal(expected, value);
    }

    [Fact]
    public void The_publish_debounce_is_the_one_named_window_this_file_owns()
        => Assert.Equal(50, Spotify.Connect.PublishDebounceMs);
}

/// <summary>The hello PUT (headless plan §1.6 item 5): the session's `AnnounceDevice` effect asks `Connect.Hello`, which
/// `Playback.Boot` points at a snapshot captured on the UI thread. The pure halves are pinned here; the fold that fires it
/// once per connection is `SessionStepTests.Going_online_announces_the_device_once_per_connection`. Joins the entities
/// collection because `SnapshotForConnect` reads the process-static reducer state `Playback.ResetForTests` owns.</summary>
[Collection(EntitiesCollection.Name)]
public class SpotifyConnectHelloTests
{
    [Fact]
    public void The_first_hello_is_a_new_device_and_every_later_one_a_new_connection()
    {
        Assert.Equal(Playback.PublishReason.NewDevice, Playback.HelloReason(0));
        Assert.Equal(Playback.PublishReason.NewConnection, Playback.HelloReason(1));
        Assert.Equal(Playback.PublishReason.NewConnection, Playback.HelloReason(7));
    }

    [Fact]
    public void A_hello_from_an_idle_player_is_an_inactive_device_with_no_player_half()
    {
        // librespot's shape: while we do not own playback the PUT carries an idle player_state and OUR volume, never a
        // mirror of somebody else's track. The glue mints the message id; the capture carries 0.
        Playback.ResetForTests();
        Playback.Snapshot hello = Playback.SnapshotForConnect();

        Assert.Equal(Playback.PublishReason.NewDevice, hello.Reason);
        Assert.False(hello.IsActive);
        Assert.False(hello.HasTrack);
        Assert.False(hello.IsPlaying);
        Assert.Equal(0u, hello.MessageId);
        Assert.Equal(Playback.PublishReason.NewConnection, Playback.SnapshotForConnect(Playback.PublishReason.NewConnection).Reason);
    }

    [Fact]
    public void The_hello_is_announced_on_online_unless_a_host_turned_it_off()
    {
        // The GUI is a Connect device the moment it is online; a headless smoke is one only when asked (`--connect`).
        Assert.True(Spotify.Connect.AnnounceOnOnline);
    }
}
