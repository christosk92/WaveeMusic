// ── Wavee.Tests/DealerAckRulesTests.cs — the dealer's pure decisions beside the hello deadline ────────────────────
//
// 2026-10-01 audit of nine days of dealer capture: every REQUEST was acked success:true before anything ran, a retry of
// a command already run ran again, a half-open socket was noticed ~90 s late, a graceful server Close cost a 3 s gap, and
// only the first apresolve host was ever used. Each decision is a pure class (Spotify.Dealer.Rules.cs); the socket half
// is unverifiable here.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DealerAckRulesTests
{
    static readonly byte[] Ident = "hm://connect-state/v1/player/command"u8.ToArray();

    static Spotify.Decode.RemoteCommand Command(string json) => Spotify.Decode.ConnectCommand(Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("skip_next")]
    [InlineData("add_to_queue")]
    [InlineData("set_queue")]
    [InlineData("transfer")]
    public void A_known_endpoint_on_the_command_ident_is_acked_true(string endpoint)
    {
        var command = Command("{\"message_id\":1,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"" + endpoint + "\"}}");
        Assert.NotEqual(Spotify.Decode.RemoteCmd.Unknown, command.Kind);
        Assert.True(Spotify.DealerAck.For(Ident, command.Kind));
    }

    [Fact]
    public void An_unknown_endpoint_is_refused()
    {
        var command = Command("{\"message_id\":1,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"set_sleep_timer\"}}");
        Assert.Equal(Spotify.Decode.RemoteCmd.Unknown, command.Kind);
        Assert.False(Spotify.DealerAck.For(Ident, command.Kind));
    }

    [Fact]
    public void A_body_with_no_command_is_refused()
    {
        var command = Command("{\"message_id\":1}");
        Assert.Equal(Spotify.Decode.RemoteCmd.Unknown, command.Kind);
        Assert.False(Spotify.DealerAck.For(Ident, command.Kind));
    }

    [Fact]
    public void A_known_endpoint_with_a_garbled_body_is_still_acked_true()
    {
        // The 0.2.9 lesson: a failure reply on a KNOWN endpoint gets the sender cached out of ever sending it again.
        Assert.True(Spotify.DealerAck.For(Ident, Spotify.Decode.RemoteCmd.SeekTo));
    }

    [Theory]
    [InlineData("hm://connect-state/v1/connect/volume")]
    [InlineData("hm://connect-state/v1/player/command/extra")]
    [InlineData("")]
    public void A_request_that_is_not_a_player_command_is_refused(string ident)
        => Assert.False(Spotify.DealerAck.For(Encoding.UTF8.GetBytes(ident), Spotify.Decode.RemoteCmd.Pause));
}

public class DealerDedupeRingTests
{
    [Fact]
    public void A_key_seen_twice_is_a_duplicate_the_second_time()
    {
        var ring = new Spotify.DealerDedupeRing();
        Assert.False(ring.SeenOrAdd(7));
        Assert.True(ring.SeenOrAdd(7));
        Assert.False(ring.SeenOrAdd(8));
    }

    [Fact]
    public void The_oldest_key_is_forgotten_past_capacity()
    {
        var ring = new Spotify.DealerDedupeRing(capacity: 3);
        for (ulong k = 1; k <= 3; k++) Assert.False(ring.SeenOrAdd(k));
        Assert.False(ring.SeenOrAdd(4));       // overwrites 1
        Assert.False(ring.SeenOrAdd(1));       // 1 is new again (overwrites 2)
        Assert.True(ring.SeenOrAdd(4));
    }

    [Fact]
    public void Key_zero_is_never_a_duplicate()
    {
        var ring = new Spotify.DealerDedupeRing();
        Assert.False(ring.SeenOrAdd(0));
        Assert.False(ring.SeenOrAdd(0));
    }

    static Spotify.Decode.RemoteCommand Command(string json) => Spotify.Decode.ConnectCommand(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void The_same_sender_id_and_endpoint_dedupe_but_another_endpoint_on_the_id_does_not()
    {
        var ring = new Spotify.DealerDedupeRing();
        var pause = Command("{\"message_id\":9,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"pause\"}}");
        var again = Command("{\"message_id\":9,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"pause\"}}");
        var resume = Command("{\"message_id\":9,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"resume\"}}");
        Assert.True(Spotify.DealerDedupeRing.IsDedupable(in pause));
        Assert.False(ring.SeenOrAdd(pause.DedupeKey));
        Assert.True(ring.SeenOrAdd(again.DedupeKey));
        Assert.False(ring.SeenOrAdd(resume.DedupeKey));
    }

    [Fact]
    public void A_command_that_cannot_be_told_apart_is_not_dedupable()
    {
        var noId = Command("{\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"pause\"}}");
        var noSender = Command("{\"message_id\":3,\"command\":{\"endpoint\":\"pause\"}}");
        var unknown = Command("{\"message_id\":3,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"nope\"}}");
        Assert.False(Spotify.DealerDedupeRing.IsDedupable(in noId));
        Assert.False(Spotify.DealerDedupeRing.IsDedupable(in noSender));
        Assert.False(Spotify.DealerDedupeRing.IsDedupable(in unknown));
    }
}

public class DealerTallyTests
{
    [Fact]
    public void The_first_sighting_logs_then_one_line_per_interval_with_the_running_total()
    {
        var tally = new Spotify.DealerTally(intervalMs: 1_000);
        Assert.True(tally.Note("a", 10_000, out long total)); Assert.Equal(1, total);
        Assert.False(tally.Note("a", 10_001, out total)); Assert.Equal(2, total);
        Assert.False(tally.Note("a", 10_999, out total)); Assert.Equal(3, total);
        Assert.True(tally.Note("a", 11_000, out total)); Assert.Equal(4, total);
    }

    [Fact]
    public void Keys_count_separately()
    {
        var tally = new Spotify.DealerTally(intervalMs: 60_000);
        Assert.True(tally.Note("a", 0, out _));
        Assert.True(tally.Note("b", 1, out long total)); Assert.Equal(1, total);
        Assert.False(tally.Note("a", 2, out total)); Assert.Equal(2, total);
    }

    [Fact]
    public void Past_capacity_the_rest_share_one_other_slot()
    {
        var tally = new Spotify.DealerTally(intervalMs: 60_000, capacity: 3);
        Assert.True(tally.Note("a", 0, out _));
        Assert.True(tally.Note("b", 0, out _));
        Assert.True(tally.Note("c", 0, out long total)); Assert.Equal(1, total);
        Assert.False(tally.Note("d", 1, out total)); Assert.Equal(2, total);
    }
}

public class DealerTopicRulesTests
{
    static Spotify.DealerTopicRules.Kind Classify(string uri) => Spotify.DealerTopicRules.Classify(Encoding.UTF8.GetBytes(uri));

    [Fact]
    public void Topics_are_classified()
    {
        Assert.Equal(Spotify.DealerTopicRules.Kind.NonHm, Classify("social-connect/v2/broadcast_status_update"));
        Assert.Equal(Spotify.DealerTopicRules.Kind.IgnoredOnPurpose, Classify("hm://playlist/v2/list/liked-songs-artist/0123456789abcdef"));
        Assert.Equal(Spotify.DealerTopicRules.Kind.Hm, Classify("hm://playlist/v2/playlist/abc"));
        Assert.Equal(Spotify.DealerTopicRules.Kind.NonHm, Classify(""));
    }

    [Fact]
    public void The_liked_songs_artist_push_is_no_library_relation()
        => Assert.Equal(LibraryPush.None, LibraryPushRules.Classify("hm://playlist/v2/list/liked-songs-artist/0123456789abcdef"u8));

    [Theory]
    [InlineData("social-connect/v2/broadcast_status_update", "social-connect/v2/broadcast_status_update")]
    [InlineData("social-connect/v2/broadcast_status_update/Abc123", "social-connect/v2/broadcast_status_update/…")]
    [InlineData("social-connect/V2Id", "social-connect/…")]
    [InlineData("Weird", "(opaque)")]
    public void The_log_key_never_carries_an_id(string uri, string expected)
        => Assert.Equal(expected, Spotify.DealerTopicRules.LogKey(Encoding.UTF8.GetBytes(uri)));
}

public class DealerLivenessTests
{
    [Fact]
    public void A_frame_after_the_ping_is_alive_whatever_the_clock()
        => Assert.False(Spotify.DealerLiveness.PongOverdue(pingSentAtMs: 1_000, lastFrameMs: 1_030, nowMs: 600_000));

    [Fact]
    public void No_frame_since_the_ping_is_dead_only_once_the_deadline_passed()
    {
        Assert.Equal(8_000, Spotify.DealerLiveness.PongDeadlineMs);
        Assert.False(Spotify.DealerLiveness.PongOverdue(1_000, 500, 1_000));
        Assert.False(Spotify.DealerLiveness.PongOverdue(1_000, 500, 8_999));
        Assert.True(Spotify.DealerLiveness.PongOverdue(1_000, 500, 9_000));
        Assert.True(Spotify.DealerLiveness.PongOverdue(1_000, 500, 90_000));
    }

    [Fact]
    public void The_ping_cadence_leaves_room_for_the_deadline()
        => Assert.True(Spotify.DealerLiveness.PingIntervalMs > Spotify.DealerLiveness.PongDeadlineMs);
}

public class ReconnectDelayTests
{
    [Fact]
    public void A_clean_close_reconnects_at_once_with_a_small_jitter()
    {
        Assert.Equal(0, Spotify.ReconnectDelay.For(1, wasCleanClose: true, jitterSample: 0));
        Assert.Equal(500, Spotify.ReconnectDelay.For(1, wasCleanClose: true, jitterSample: 500));
        for (int sample = 0; sample < 5_000; sample += 37)
            Assert.InRange(Spotify.ReconnectDelay.For(1, true, sample), 0, 500);
    }

    [Fact]
    public void Errors_and_resets_climb_the_ladder()
    {
        int[] ladder = [3_000, 6_000, 12_000, 24_000, 30_000, 30_000];
        for (uint attempt = 1; attempt <= ladder.Length; attempt++)
            Assert.Equal(ladder[attempt - 1], Spotify.ReconnectDelay.For(attempt, wasCleanClose: false, jitterSample: 123));
    }

    [Fact]
    public void A_server_that_keeps_closing_falls_back_to_the_ladder()
    {
        Assert.Equal(6_000, Spotify.ReconnectDelay.For(2, wasCleanClose: true, jitterSample: 0));
        Assert.Equal(30_000, Spotify.ReconnectDelay.For(9, wasCleanClose: true, jitterSample: 0));
    }
}

public class HostRotationTests
{
    [Fact]
    public void Next_wraps_and_a_single_host_stays()
    {
        Assert.Equal(1, Spotify.HostRotation.Next(0, 4));
        Assert.Equal(0, Spotify.HostRotation.Next(3, 4));
        Assert.Equal(0, Spotify.HostRotation.Next(0, 1));
        Assert.Equal(0, Spotify.HostRotation.Next(0, 0));
    }

    [Fact]
    public void A_failure_of_the_current_host_moves_to_the_next_and_wraps()
    {
        var hosts = new Spotify.HostRotation();
        Assert.Null(hosts.Current);
        hosts.Set(["a", "b", "c"]);
        Assert.Equal("a", hosts.Current);
        Assert.True(hosts.NoteFailure("a")); Assert.Equal("b", hosts.Current);
        Assert.True(hosts.NoteFailure("b")); Assert.Equal("c", hosts.Current);
        Assert.True(hosts.NoteFailure("c")); Assert.Equal("a", hosts.Current);
    }

    [Fact]
    public void A_stale_failure_or_a_single_host_moves_nothing()
    {
        var hosts = new Spotify.HostRotation();
        hosts.Set(["a", "b"]);
        Assert.True(hosts.NoteFailure("a"));
        Assert.False(hosts.NoteFailure("a"));          // a burst of failures against the old host advances once
        Assert.Equal("b", hosts.Current);

        hosts.Set(["only"]);
        Assert.False(hosts.NoteFailure("only"));
        Assert.Equal("only", hosts.Current);
    }

    [Fact]
    public void Set_starts_again_from_the_first_host()
    {
        var hosts = new Spotify.HostRotation();
        hosts.Set(["a", "b"]);
        hosts.NoteFailure("a");
        hosts.Set(["x", "y"]);
        Assert.Equal("x", hosts.Current);
    }
}
