// ── Wavee.Tests/ConnectCommandBodyTests.cs — the controller verbs whose body says more than the verb (gap batch B3b) ──
//
// The gate for `Spotify/Spotify.Decode.Commands.cs`. All pure: a body in, a value out, no session, no socket and no
// reducer. The PutState facts moved to `PutStateWireTests.cs` with the encoder's parity half (gap batch R4-1).
//
//   SET_OPTIONS IS TWO VERBS THE REDUCER ALREADY FOLDS. Each folded verb must be EXACTLY the command a controller would
//   have sent alone — kind, argument, message id, sender and dedupe key — or the PUT it causes is attributed to nothing
//   and a recycled message id reads as a replay.
//
//   SET_QUEUE'S REVISION DOES NOT FIT A LONG. The capture writes 17146072722624078579 as a bare JSON number; it is kept
//   as its digits, and the captured 181 KB body (8 behind, 52 queued, 48 autoplay and the delimiter ahead) decodes whole.

using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using P = Wavee.Protocol.Player;
using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;

namespace Wavee.Tests;

public class ConnectOptionVerbTests
{
    static Spotify.Decode.RemoteCommand Command(string json) => Spotify.Decode.ConnectCommand(Encoding.UTF8.GetBytes(json));

    static Spotify.Decode.RemoteCommand[] Verbs(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        var command = Spotify.Decode.ConnectCommand(payload);
        var into = new Spotify.Decode.RemoteCommand[Spotify.Decode.MaxOptionVerbs];
        int n = Spotify.Decode.OptionVerbs(payload, in command, into);
        return into[..n];
    }

    [Fact]
    public void Set_options_folds_to_the_shuffle_and_repeat_verbs_a_controller_could_send_one_at_a_time()
    {
        var verbs = Verbs("""{"message_id":7,"sent_by_device_id":"phone","command":{"endpoint":"set_options","shuffling_context":true,"repeating_track":true}}""");

        Assert.Equal(2, verbs.Length);
        Assert.Equal(RemoteCmd.SetShufflingContext, verbs[0].Kind);
        Assert.True(verbs[0].BoolArg);
        Assert.Equal(RemoteCmd.SetRepeatingTrack, verbs[1].Kind);
        Assert.True(verbs[1].BoolArg);
        Assert.Equal(7, verbs[0].MessageId);
        Assert.Equal(7, verbs[1].MessageId);
        Assert.NotEqual(verbs[0].DedupeKey, verbs[1].DedupeKey);
    }

    [Fact]
    public void Each_folded_verb_is_exactly_the_command_the_controller_would_have_sent_alone()
    {
        var verbs = Verbs("""{"message_id":7,"sent_by_device_id":"phone","command":{"endpoint":"set_options","shuffling_context":false}}""");
        var alone = Command("""{"message_id":7,"sent_by_device_id":"phone","command":{"endpoint":"set_shuffling_context","value":false}}""");

        Assert.Single(verbs);
        Assert.Equal(alone, verbs[0]);
    }

    [Theory]
    [InlineData("\"repeating_context\":true", RemoteCmd.SetRepeatingContext, true)]
    [InlineData("\"repeating_context\":false", RemoteCmd.SetRepeatingContext, false)]
    [InlineData("\"repeating_track\":false", RemoteCmd.SetRepeatingContext, false)]
    [InlineData("\"repeating_track\":false,\"repeating_context\":true", RemoteCmd.SetRepeatingContext, true)]
    [InlineData("\"repeating_context\":true,\"repeating_track\":true", RemoteCmd.SetRepeatingTrack, true)]
    public void Repeating_track_wins_then_repeating_context_and_a_stated_false_is_off(string options, RemoteCmd kind, bool on)
    {
        var verbs = Verbs("{\"message_id\":3,\"command\":{\"endpoint\":\"set_options\"," + options + "}}");

        var verb = Assert.Single(verbs);
        Assert.Equal(kind, verb.Kind);
        Assert.Equal(on, verb.BoolArg);
    }

    [Fact]
    public void A_set_options_stating_no_option_and_a_verb_that_is_not_set_options_fold_to_nothing()
    {
        Assert.Empty(Verbs("""{"message_id":3,"command":{"endpoint":"set_options","modes":{"media":"VIDEO"},"repeating_track":"yes"}}"""));
        Assert.Empty(Verbs("""{"message_id":3,"command":{"endpoint":"pause","shuffling_context":true}}"""));
    }

    [Fact]
    public void A_truncated_body_acts_on_the_options_read_before_the_fault()
    {
        var command = Command("""{"message_id":3,"command":{"endpoint":"set_options"}}""");
        byte[] truncated = Encoding.UTF8.GetBytes("""{"message_id":3,"command":{"endpoint":"set_options","shuffling_context":true,"repeating_tr""");
        var into = new Spotify.Decode.RemoteCommand[Spotify.Decode.MaxOptionVerbs];

        int n = Spotify.Decode.OptionVerbs(truncated, in command, into);

        Assert.Equal(1, n);
        Assert.Equal(RemoteCmd.SetShufflingContext, into[0].Kind);
        Assert.True(into[0].BoolArg);
    }
}

public class ConnectQueueDecodeTests
{
    static string Text(Spotify.Decode.ClusterBuffer buffer, TextRef text) => Encoding.UTF8.GetString(buffer.Utf8(text));

    [Fact]
    public void A_set_queue_carries_both_runs_in_wire_order_and_keeps_its_revision_as_digits()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        try
        {
            var queue = Spotify.Decode.ConnectQueue(Encoding.UTF8.GetBytes("""
                {"message_id":12,"command":{"endpoint":"set_queue","queue_revision":17146072722624078579,
                  "prev_tracks":[{"uri":"spotify:track:7ePpQepOptZ1M9jRRydHsZ","uid":"5bd5aabfe4434940c96f","provider":"context"}],
                  "next_tracks":[
                    {"uri":"spotify:track:5bzGoH3b8ETi5RRZtu67tj","uid":"q2","provider":"queue","metadata":{"title":"Too Serious Too Soon","is_queued":"true"}},
                    {"uri":"spotify:track:2whuBUE05tAxUytfaDcNzB","uid":"0ab15c9f39e1de3b","provider":"autoplay","restrictions":{"disallow_peeking_prev_reasons":[]}},
                    {"uri":"spotify:delimiter","uid":"delimiter0","provider":"autoplay"}]}}
                """), buffer);

            Assert.Equal(RemoteCmd.SetQueue, queue.Kind);
            Assert.Equal("17146072722624078579", Text(buffer, queue.Revision));
            Assert.Equal(1, queue.PrevCount);
            Assert.Equal(3, queue.NextCount);
            Assert.Equal("5bd5aabfe4434940c96f", Text(buffer, buffer.Tracks(queue.PrevStart, queue.PrevCount)[0].Uid));

            var next = buffer.Tracks(queue.NextStart, queue.NextCount);
            Assert.Equal("q2", Text(buffer, next[0].Uid));
            Assert.Equal("queue", Text(buffer, next[0].Provider));
            Assert.Equal("Too Serious Too Soon", Text(buffer, next[0].Title));
            Assert.Equal("autoplay", Text(buffer, next[1].Provider));
            // The marker is kept: deciding what a delimiter means is the applier's, not the decoder's.
            Assert.Equal("spotify:delimiter", Text(buffer, next[2].Uri));
        }
        finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
    }

    [Fact]
    public void The_captured_set_queue_decodes_every_row()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotify", "set-queue-52.json"));
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        try
        {
            // The capture was saved with a byte-order mark; a dealer payload carries none.
            int skip = file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF ? 3 : 0;
            var queue = Spotify.Decode.ConnectQueue(file.AsSpan(skip), buffer);

            Assert.Equal(RemoteCmd.SetQueue, queue.Kind);
            Assert.Equal("17146072722624078579", Text(buffer, queue.Revision));
            Assert.Equal(8, queue.PrevCount);
            Assert.Equal(101, queue.NextCount);

            var next = buffer.Tracks(queue.NextStart, queue.NextCount);
            int queued = 0;
            for (int i = 0; i < next.Length; i++) if (Text(buffer, next[i].Provider) == "queue") queued++;
            Assert.Equal(52, queued);
            Assert.Equal("q2", Text(buffer, next[0].Uid));
            Assert.Equal("0ab15c9f39e1de3b", Text(buffer, next[52].Uid));
            Assert.Equal("spotify:delimiter", Text(buffer, next[100].Uri));
            Assert.Equal("5bd5aabfe4434940c96f", Text(buffer, buffer.Tracks(queue.PrevStart, queue.PrevCount)[0].Uid));
        }
        finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
    }

    [Fact]
    public void An_update_context_carries_its_context_and_the_rows_its_pages_embed()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        try
        {
            var queue = Spotify.Decode.ConnectQueue(Encoding.UTF8.GetBytes("""
                {"command":{"endpoint":"update_context","session_id":"s1","context":{"uri":"spotify:playlist:37i9dQZF1DXcBWIGoYBM5M",
                  "url":"context://spotify:playlist:37i9dQZF1DXcBWIGoYBM5M",
                  "pages":[{"tracks":[{"uri":"spotify:track:4uLU6hMCjMI75M1A2tKUQC","uid":"a1"},{"uri":"spotify:track:7ePpQepOptZ1M9jRRydHsZ","uid":"a2"}]}]}}}
                """), buffer);

            Assert.Equal(RemoteCmd.UpdateContext, queue.Kind);
            Assert.Equal("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", Text(buffer, queue.ContextUri));
            Assert.Equal("context://spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", Text(buffer, queue.ContextUrl));
            Assert.Equal(2, queue.TrackCount);
            Assert.Equal("a2", Text(buffer, buffer.Tracks(queue.TrackStart, queue.TrackCount)[1].Uid));
            Assert.Equal(0, queue.PrevCount);
            Assert.Equal(0, queue.NextCount);
        }
        finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
    }

    [Fact]
    public void A_verb_that_is_not_a_queue_body_stages_nothing()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        try
        {
            var queue = Spotify.Decode.ConnectQueue(Encoding.UTF8.GetBytes(
                """{"command":{"endpoint":"play","context":{"uri":"spotify:album:1TSZDcvlPtAnekTaItI3qO","pages":[{"tracks":[{"uri":"spotify:track:4uLU6hMCjMI75M1A2tKUQC"}]}]}}}"""),
                buffer);

            Assert.Equal(RemoteCmd.Unknown, queue.Kind);
            Assert.Equal(0, queue.TrackCount);
            Assert.Equal(0, buffer.TrackCount);
        }
        finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
    }

    [Fact]
    public void A_cluster_push_joins_the_devices_that_changed_for_the_diagnostics_page()
    {
        byte[] update = new P.ClusterUpdate
        {
            Cluster = new P.Cluster { ActiveDeviceId = "phone-device", ServerTimestampMs = 5 },
            DevicesThatChanged = { "phone-device", "wavee-device" },
        }.ToByteArray();
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        try
        {
            var delta = Spotify.Decode.ClusterUpdate(update, "wavee-device"u8, buffer);

            Assert.Equal("phone-device,wavee-device", Text(buffer, delta.ChangedDevices));
            Assert.Equal("phone-device", Text(buffer, delta.ActiveDeviceId));
        }
        finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
    }
}
