// ── Wavee.Tests/SpotifyConnectTests.cs — the dealer route, the mailbox, and the outbound bodies ───────────────────
//
// Wave 2's gate for `Spotify/Spotify.Connect.cs`. The frames are BUILT here from the generated `Wavee.Protocol`
// messages rather than read from a checked-in blob — `Wavee.Tests.csproj` says why: a decoder pinned against the
// canonical encoder is pinned against something anybody can regenerate, and 0.2.9's `cluster-complex-queue.json` is a
// JSON capture of a shape this decoder no longer reads (the dealer's cluster payload is base64 protobuf).
//
// No socket is opened. `Spotify.Reply` no-ops without a dealer websocket and `Spotify.Post` defaults to running its
// action inline, which is what makes `OnDealer` a synchronous, assertable function in a unit test. With no dealer
// connection id the glue arms no put-state timer (gap batch R4-1), so a fact that reaches the reducer sends nothing.
//
// The dealer facts join the entities collection: a play / transfer / queue body goes straight to the playback host's
// process-static intake, and the host's drain must not run beside another collection's.

using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Wavee;
using Xunit;
using Pb = Wavee.Protocol.Player;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
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

    /// <summary>G-074: set_options reaches the reducer as the verbs it already folds — one item per option, each carrying
    /// the controller's message id — and never as a bare set_options the reducer would ignore.</summary>
    [Fact]
    public void A_set_options_frame_becomes_the_option_verbs_the_reducer_already_folds()
    {
        Drain();
        Spotify.Connect.OnDealer(RequestFrame(
            "{\"message_id\":42,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"set_options\"," +
            "\"shuffling_context\":true,\"repeating_context\":true}}"));

        Assert.True(Spotify.Connect.TryDequeue(out Spotify.Connect.Item shuffle));
        Assert.True(Spotify.Connect.TryDequeue(out Spotify.Connect.Item repeat));
        Assert.False(Spotify.Connect.TryDequeue(out _));

        Assert.Equal(Spotify.Decode.RemoteCmd.SetShufflingContext, shuffle.Command.Kind);
        Assert.True(shuffle.Command.BoolArg);
        Assert.Equal(Spotify.Decode.RemoteCmd.SetRepeatingContext, repeat.Command.Kind);
        Assert.True(repeat.Command.BoolArg);
        Assert.Equal(42, repeat.Command.MessageId);
    }

    /// <summary>G-074 / G-250: a set_queue body is handed to the playback host's intake — decoded into a pooled buffer and
    /// folded in arrival order — and NO command item is enqueued beside it (a second copy would claim twice). The host
    /// folding it is visible on the reducer: the claim, and the PUT's attribution to the sender.</summary>
    [Fact]
    public void A_set_queue_frame_enqueues_nothing_and_reaches_the_host_intake()
    {
        Drain();
        Playback.ToUi = static a => a();
        Playback.ResetForTests();
        try
        {
            Spotify.Connect.OnDealer(RequestFrame(
                "{\"message_id\":77,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"set_queue\",\"queue_revision\":5," +
                "\"next_tracks\":[{\"uri\":\"spotify:track:4uLU6hMCjMI75M1A2tKUQC\",\"uid\":\"q2\",\"provider\":\"queue\"}]}}"));

            Assert.False(Spotify.Connect.TryDequeue(out _));             // no command item
            Playback.State s = Playback.Snap();
            Assert.Equal(77u, s.LastCommandMessageId);                   // the host's intake folded the command…
            Assert.Equal(Playback.DeviceHash("phone"), s.LastCommandSender);
            Assert.Equal(Playback.Owner.Us, s.Owner);                    // …and its claim, in the same drain
            Assert.Equal(0, Playback.PendingIntakes);
        }
        finally { Playback.ResetForTests(); }
    }
}

/// <summary>What a rejected put, a re-announce and a sign-out do (gap batch R4-1, G-247 / G-036). Pure rules; the sends
/// themselves need a live session.</summary>
public class SpotifyConnectPutRulesTests
{
    [Theory]
    [InlineData(Spotify.Connect.PutReason.BecameInactive, 422, Spotify.Connect.PutRejection.SoftAck)]
    [InlineData(Spotify.Connect.PutReason.BecameInactive, 500, Spotify.Connect.PutRejection.Rejected)]
    [InlineData(Spotify.Connect.PutReason.PlayerStateChanged, 422, Spotify.Connect.PutRejection.Reannounce)]
    [InlineData(Spotify.Connect.PutReason.VolumeChanged, 422, Spotify.Connect.PutRejection.Reannounce)]
    [InlineData(Spotify.Connect.PutReason.PlayerStateChanged, 503, Spotify.Connect.PutRejection.Reannounce)]
    [InlineData(Spotify.Connect.PutReason.NewDevice, 422, Spotify.Connect.PutRejection.Rejected)]
    public void A_rejected_state_or_volume_put_re_announces_and_an_inactive_422_is_a_soft_ack(Spotify.Connect.PutReason reason,
        int status, Spotify.Connect.PutRejection expected)
        => Assert.Equal(expected, Spotify.Connect.RejectionOf(reason, status));

    [Theory]
    [InlineData(40_000L, long.MinValue / 2, true)]      // never re-announced
    [InlineData(40_000L, 10_000L, true)]                // 30 s on
    [InlineData(39_999L, 10_000L, false)]               // inside the window: a hard-down service is not a hot loop
    public void A_re_announce_goes_at_most_once_every_thirty_seconds(long now, long last, bool due)
        => Assert.Equal(due, Spotify.Connect.ReannounceDue(now, last));

    [Theory]
    [InlineData(true, true, true, Spotify.Connect.RetireRoute.InactiveFirst)]
    [InlineData(false, true, true, Spotify.Connect.RetireRoute.SignOutNow)]   // no connection: nothing could carry it
    [InlineData(true, false, true, Spotify.Connect.RetireRoute.SignOutNow)]   // no player host
    [InlineData(true, true, false, Spotify.Connect.RetireRoute.SignOutNow)]   // playback was not ours
    public void A_sign_out_sends_its_inactive_put_first_only_when_playback_is_ours(bool connection, bool player, bool owns,
        Spotify.Connect.RetireRoute expected)
        => Assert.Equal(expected, Spotify.Connect.RetireRouteOf(connection, player, owns));
}

/// <summary>Where an acked REQUEST goes (gap batch B3b). The load route is the one that must never ALSO enqueue a
/// command item: the playback host folds the claim from the load slot, and a second copy would claim twice.</summary>
public class SpotifyConnectRouteTests
{
    [Theory]
    [InlineData(Spotify.Decode.RemoteCmd.Unknown, Spotify.Connect.CommandRoute.Drop)]
    [InlineData(Spotify.Decode.RemoteCmd.Play, Spotify.Connect.CommandRoute.Load)]
    [InlineData(Spotify.Decode.RemoteCmd.Transfer, Spotify.Connect.CommandRoute.Load)]
    [InlineData(Spotify.Decode.RemoteCmd.SetOptions, Spotify.Connect.CommandRoute.Options)]
    [InlineData(Spotify.Decode.RemoteCmd.SetQueue, Spotify.Connect.CommandRoute.QueueBody)]
    [InlineData(Spotify.Decode.RemoteCmd.UpdateContext, Spotify.Connect.CommandRoute.QueueBody)]
    [InlineData(Spotify.Decode.RemoteCmd.Pause, Spotify.Connect.CommandRoute.Mailbox)]
    [InlineData(Spotify.Decode.RemoteCmd.SkipNext, Spotify.Connect.CommandRoute.Mailbox)]
    [InlineData(Spotify.Decode.RemoteCmd.SetShufflingContext, Spotify.Connect.CommandRoute.Mailbox)]
    [InlineData(Spotify.Decode.RemoteCmd.AddToQueue, Spotify.Connect.CommandRoute.Mailbox)]
    public void Each_verb_has_one_route(Spotify.Decode.RemoteCmd kind, Spotify.Connect.CommandRoute route)
        => Assert.Equal(route, Spotify.Connect.RouteOf(kind));
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

    // ── the queue forwards (G-248), against 0.2.9's OutboundEnvelopeTests ───────────────────────────────────────────

    static JsonDocument Json(Action<System.Buffers.ArrayBufferWriter<byte>> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        write(buffer);
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    [Fact]
    public void Play_is_the_desktop_envelope_with_the_context_and_a_skip_to()
    {
        using var doc = Json(b => Spotify.Connect.PlayBody(b, "spotify:playlist:abc", "spotify:track:xyz", true, "us", "cmd1", "intent1", 7));
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("intent1", root.GetProperty("intent_id").GetString());
        var cmd = root.GetProperty("command");
        Assert.Equal("play", cmd.GetProperty("endpoint").GetString());
        var ctx = cmd.GetProperty("context");
        Assert.Equal("spotify:playlist:abc", ctx.GetProperty("uri").GetString());
        Assert.Equal("spotify:playlist:abc", ctx.GetProperty("entity_uri").GetString());
        Assert.Equal("context://spotify:playlist:abc", ctx.GetProperty("url").GetString());
        Assert.False(ctx.TryGetProperty("pages", out _));                 // URI-only: the owner resolves it
        Assert.Equal("playlist", cmd.GetProperty("play_origin").GetProperty("feature_identifier").GetString());
        var prep = cmd.GetProperty("prepare_play_options");
        Assert.False(prep.GetProperty("always_play_something").GetBoolean());
        Assert.Equal("spotify:track:xyz", prep.GetProperty("skip_to").GetProperty("track_uri").GetString());
        Assert.Equal("premium", prep.GetProperty("license").GetString());
        Assert.True(prep.GetProperty("player_options_override").GetProperty("shuffling_context").GetBoolean());
        var play = cmd.GetProperty("play_options");
        Assert.Equal("interactive", play.GetProperty("reason").GetString());
        Assert.Equal("replace", play.GetProperty("operation").GetString());
        Assert.Equal("immediately", play.GetProperty("trigger").GetString());
        Assert.Equal("us", cmd.GetProperty("logging_params").GetProperty("device_identifier").GetString());
    }

    [Fact]
    public void Play_from_the_head_has_no_skip_to_and_names_the_surface_of_the_context()
    {
        using var doc = Json(b => Spotify.Connect.PlayBody(b, "spotify:album:abc", null, false, "us", "c", "i", 7));
        var cmd = doc.RootElement.GetProperty("command");
        Assert.False(cmd.GetProperty("prepare_play_options").TryGetProperty("skip_to", out _));
        Assert.Equal("album", cmd.GetProperty("play_origin").GetProperty("feature_identifier").GetString());
        Assert.Equal("your_library", Spotify.Connect.PlayFeatureOf("spotify:user:me:collection"));
        Assert.Equal("track", Spotify.Connect.PlayFeatureOf("spotify:track:x"));
        Assert.Equal("harmony", Spotify.Connect.PlayFeatureOf("spotify:station:track:x"));
    }

    [Fact]
    public void Add_to_queue_is_the_desktop_envelope_with_a_single_track()
    {
        using var doc = Json(b => Spotify.Connect.AddToQueueBody(b, "spotify:track:x", "us", "cmd1", "intent1", "", 7));
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("intent1", root.GetProperty("intent_id").GetString());

        var cmd = root.GetProperty("command");
        Assert.Equal("add_to_queue", cmd.GetProperty("endpoint").GetString());
        Assert.False(cmd.TryGetProperty("uri", out _));                 // NOT the legacy flat command.uri
        var track = cmd.GetProperty("track");
        Assert.Equal("spotify:track:x", track.GetProperty("uri").GetString());
        Assert.Equal("", track.GetProperty("uid").GetString());         // present even when empty
        Assert.Empty(track.GetProperty("metadata").EnumerateObject());  // explicit {}

        var options = cmd.GetProperty("options");
        Assert.False(options.GetProperty("override_restrictions").GetBoolean());
        Assert.False(options.GetProperty("only_for_local_device").GetBoolean());
        Assert.False(options.GetProperty("system_initiated").GetBoolean());

        var log = cmd.GetProperty("logging_params");
        Assert.Equal("us", log.GetProperty("device_identifier").GetString());
        Assert.Equal("cmd1", log.GetProperty("command_id").GetString());
        Assert.Equal(7L, log.GetProperty("command_initiated_time").GetInt64());
        Assert.Equal(7L, log.GetProperty("command_received_time").GetInt64());
        Assert.Empty(log.GetProperty("interaction_ids").EnumerateArray());
    }

    [Fact]
    public void Set_queue_is_the_full_snapshot_envelope_with_its_revision_a_bare_number()
    {
        Spotify.Connect.QueueWireRow[] next =
        [
            new("spotify:track:n1", "q1", Queued: true),
            new("spotify:track:n2", "", Queued: true),
            new("spotify:track:cx", "5bd5aabfe4434940c96f", Queued: false),
        ];
        using var doc = Json(b => Spotify.Connect.SetQueueBody(b, 10_355_548_321_371_651_421UL, [], next, "us", "c", "i", "iact-9", 5));
        var cmd = doc.RootElement.GetProperty("command");

        Assert.Equal("set_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Equal(JsonValueKind.Number, cmd.GetProperty("queue_revision").ValueKind);
        Assert.Equal(10_355_548_321_371_651_421UL, cmd.GetProperty("queue_revision").GetUInt64());
        Assert.Empty(cmd.GetProperty("prev_tracks").EnumerateArray());

        var rows = cmd.GetProperty("next_tracks");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("queue", rows[0].GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.String, rows[0].GetProperty("metadata").GetProperty("is_queued").ValueKind);
        Assert.Equal("true", rows[0].GetProperty("metadata").GetProperty("is_queued").GetString());
        Assert.Equal("", rows[1].GetProperty("uid").GetString());
        Assert.Equal("context", rows[2].GetProperty("provider").GetString());
        Assert.False(rows[2].GetProperty("metadata").TryGetProperty("is_queued", out _));
        foreach (var row in rows.EnumerateArray())
        {
            Assert.Empty(row.GetProperty("removed").EnumerateArray());
            Assert.Empty(row.GetProperty("blocked").EnumerateArray());
            Assert.Equal(22, row.GetProperty("restrictions").EnumerateObject().Count());
        }
        Assert.Equal("iact-9", cmd.GetProperty("logging_params").GetProperty("interaction_ids")[0].GetString());
        Assert.Equal("wlan", doc.RootElement.GetProperty("connection_type").GetString());
    }

    [Fact]
    public void Play_next_lands_at_the_head_of_the_owners_queued_rows_and_an_append_behind_them()
    {
        Spotify.Connect.QueueWireRow[] owner =
        [
            new("spotify:track:q1", "q1", Queued: true),
            new("spotify:track:q2", "q2", Queued: true),
            new("spotify:track:c1", "c1", Queued: false),
        ];
        Spotify.Connect.QueueWireRow[] added = [new("spotify:track:new", "", Queued: false)];

        var next = Spotify.Connect.SpliceQueued(owner, added, slot: 0);
        Assert.Equal(new[] { "spotify:track:new", "spotify:track:q1", "spotify:track:q2", "spotify:track:c1" }, next.Select(r => r.Uri).ToArray());
        Assert.True(next[0].Queued);                                    // an added row is a queued row

        var append = Spotify.Connect.SpliceQueued(owner, added, slot: int.MaxValue);
        Assert.Equal(new[] { "spotify:track:q1", "spotify:track:q2", "spotify:track:new", "spotify:track:c1" }, append.Select(r => r.Uri).ToArray());

        var empty = Spotify.Connect.SpliceQueued([], added, slot: int.MaxValue);
        Assert.Equal("spotify:track:new", Assert.Single(empty).Uri);
    }
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
