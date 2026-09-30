// ── Wavee.Tests/DealerHelloRulesTests.cs — the dealer's pusher-hello deadline ─────────────────────────────────────
//
// docs/plans/wavee/dealer-hello-rca.md (2026-09-30): a dealer socket that opened, answered every keepalive for ten minutes
// and never carried the pusher's hello left the session on "Connecting…" with every Spotify fetch held. The dealer thread
// now drops such a socket after `DealerHelloRules.HelloDeadlineMs`; these facts pin the PURE half — the deadline
// arithmetic the receive loop asks, the one rule that recognises the hello (against the captured wire shape, through the
// real `DealerFrame.Parse`), and the topic redaction the always-on `dealer.frame` line prints through. The socket half is
// unverifiable here; the fold's answer to the resulting drop is `SessionStepTests`'.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DealerHelloRulesTests
{
    static byte[] Scratch() => new byte[64 * 1024];

    /// <summary>Any tick-count origin: the rule only ever subtracts.</summary>
    const long Connected = 5_000_000;

    /// <summary>A made-up id in the captured hello's shape (base64, mixed case, a <c>+</c> and padding); the real one is
    /// never written anywhere, the capture included.</summary>
    const string Id = "MTIzNDU2Nzg5MGFiY2RlZg+cmVkYWN0ZWQ=";
    const string UriId = "MTIzNDU2Nzg5MGFiY2RlZg%2BcmVkYWN0ZWQ%3D";

    /// <summary>The working session's hello (2026-09-30 capture, seq 21, +8 ms after <c>dealer connected</c>), id redacted.</summary>
    static readonly string Hello = "{\"headers\":{\"Spotify-Connection-Id\":\"" + Id + "\"},\"method\":\"PUT\",\"type\":\"message\","
        + "\"uri\":\"hm://pusher/v1/connections/" + UriId + "\"}";

    static bool IsHello(string json)
    {
        var frame = Spotify.DealerFrame.Parse(Encoding.UTF8.GetBytes(json), Scratch());
        return Spotify.DealerHelloRules.IsHello(frame.Kind, frame.Uri, frame.ConnectionId);
    }

    [Fact]
    public void Waiting_before_the_deadline()
    {
        Assert.Equal(Spotify.DealerHelloRules.HelloVerdict.Waiting, Spotify.DealerHelloRules.Verdict(false, Connected, Connected));
        Assert.Equal(Spotify.DealerHelloRules.HelloVerdict.Waiting, Spotify.DealerHelloRules.Verdict(false, Connected, Connected + 1_700));
        Assert.Equal(Spotify.DealerHelloRules.HelloVerdict.Waiting, Spotify.DealerHelloRules.Verdict(false, Connected, Connected + 9_999));
    }

    [Fact]
    public void Overdue_at_and_after_the_deadline()
    {
        Assert.Equal(10_000, Spotify.DealerHelloRules.HelloDeadlineMs);
        Assert.Equal(Spotify.DealerHelloRules.HelloVerdict.Overdue, Spotify.DealerHelloRules.Verdict(false, Connected, Connected + 10_000));
        Assert.Equal(Spotify.DealerHelloRules.HelloVerdict.Overdue, Spotify.DealerHelloRules.Verdict(false, Connected, Connected + 30_000));
        Assert.Equal(Spotify.DealerHelloRules.HelloVerdict.Overdue, Spotify.DealerHelloRules.Verdict(false, Connected, Connected + 600_637));
    }

    [Fact]
    public void Held_once_the_hello_is_seen_whatever_the_clock()
    {
        foreach (long after in new long[] { 0, 8, 9_999, 10_000, 600_000, long.MaxValue / 2 })
            Assert.Equal(Spotify.DealerHelloRules.HelloVerdict.Held, Spotify.DealerHelloRules.Verdict(true, Connected, Connected + after));
    }

    [Fact]
    public void Budget_is_the_remaining_deadline_then_zero_then_unbounded_once_held()
    {
        Assert.Equal(10_000, Spotify.DealerHelloRules.ReceiveBudgetMs(false, Connected, Connected));
        Assert.Equal(1, Spotify.DealerHelloRules.ReceiveBudgetMs(false, Connected, Connected + 9_999));
        Assert.Equal(0, Spotify.DealerHelloRules.ReceiveBudgetMs(false, Connected, Connected + 10_000));
        Assert.Equal(0, Spotify.DealerHelloRules.ReceiveBudgetMs(false, Connected, Connected + 600_000));
        Assert.Equal(Timeout.Infinite, Spotify.DealerHelloRules.ReceiveBudgetMs(true, Connected, Connected));
        Assert.Equal(Timeout.Infinite, Spotify.DealerHelloRules.ReceiveBudgetMs(true, Connected, Connected + 600_000));
    }

    /// <summary>The receive loop asks the verdict first and waits the budget only while Waiting: the two never disagree —
    /// a zero budget is exactly Overdue, an unbounded one exactly Held.</summary>
    [Fact]
    public void The_budget_and_the_verdict_agree_at_every_instant()
    {
        foreach (bool held in new[] { false, true })
            foreach (long after in new long[] { 0, 1, 8, 1_670, 9_998, 9_999, 10_000, 10_001, 30_000, 600_000 })
            {
                var verdict = Spotify.DealerHelloRules.Verdict(held, Connected, Connected + after);
                int budget = Spotify.DealerHelloRules.ReceiveBudgetMs(held, Connected, Connected + after);
                Assert.Equal(verdict == Spotify.DealerHelloRules.HelloVerdict.Held, budget == Timeout.Infinite);
                Assert.Equal(verdict == Spotify.DealerHelloRules.HelloVerdict.Overdue, budget == 0);
                if (verdict == Spotify.DealerHelloRules.HelloVerdict.Waiting)
                    Assert.Equal(Spotify.DealerHelloRules.HelloDeadlineMs - after, budget);
            }
    }

    [Fact]
    public void IsHello_pins_the_captured_wire_shape()
    {
        var frame = Spotify.DealerFrame.Parse(Encoding.UTF8.GetBytes(Hello), Scratch());
        Assert.Equal(Spotify.DealerFrameKind.Message, frame.Kind);
        Assert.Equal(Id, Encoding.UTF8.GetString(frame.ConnectionId));
        Assert.True(Spotify.DealerHelloRules.IsHello(frame.Kind, frame.Uri, frame.ConnectionId));

        // The lowercase header spelling the parser also reads.
        Assert.True(IsHello(Hello.Replace("Spotify-Connection-Id", "spotify-connection-id")));

        // A playlist push: another topic, no id.
        Assert.False(IsHello("{\"type\":\"message\",\"uri\":\"hm://playlist/v2/playlist/37i9dQZF1DXcBWIGoYBM5M\",\"payloads\":[]}"));
        // A pusher topic with the header missing, or empty: no connection id to quote, so not the hello. (The id only in
        // the uri is the "new shape" gap the RCA names — the `dealer.frame` line is what would show it.)
        Assert.False(IsHello("{\"method\":\"PUT\",\"type\":\"message\",\"uri\":\"hm://pusher/v1/connections/" + UriId + "\"}"));
        Assert.False(IsHello("{\"headers\":{\"Spotify-Connection-Id\":\"\"},\"type\":\"message\",\"uri\":\"hm://pusher/v1/connections/" + UriId + "\"}"));
        // The keepalive the stuck socket carried, twenty times over.
        Assert.False(IsHello("{\"type\":\"pong\"}"));
        Assert.False(IsHello("{\"type\":\"ping\"}"));
        // A connection id on a topic that is not the pusher's.
        Assert.False(IsHello("{\"headers\":{\"Spotify-Connection-Id\":\"" + Id + "\"},\"type\":\"message\",\"uri\":\"hm://connect-state/v1/cluster\"}"));
    }

    [Fact]
    public void The_log_topic_never_carries_a_connection_id_or_an_account()
    {
        static string Topic(string uri) => Spotify.DealerHelloRules.LogTopic(Encoding.UTF8.GetBytes(uri));

        Assert.Equal("hm://pusher/v1/connections/…", Topic("hm://pusher/v1/connections/" + UriId));
        Assert.Equal("hm://connect-state/v1/cluster", Topic("hm://connect-state/v1/cluster"));
        Assert.Equal("hm://connect-state/v1/player/command", Topic("hm://connect-state/v1/player/command"));
        Assert.Equal("hm://collection/collection/…", Topic("hm://collection/collection/someuser/json"));
        Assert.Equal("hm://collection/artist/…", Topic("hm://collection/artist/31uxxxxxxxxxxxxxxxxxxxxxxxtq"));
        Assert.Equal("hm://playlist/v2/user/…", Topic("hm://playlist/v2/user/someuser/rootlist"));
        Assert.Equal("hm://playlist/user/…", Topic("hm://playlist/user/someuser/rootlist"));
        Assert.Equal("hm://playlist/v2/playlist/…", Topic("hm://playlist/v2/playlist/37i9dQZF1DXcBWIGoYBM5M"));
        Assert.Equal("hm://presence2/user/…", Topic("hm://presence2/user/someuser"));
        Assert.Equal("hm://pusher/…", Topic("hm://pusher/Connections/x"));             // not a plain topic word: cut there
        Assert.Equal("(none)", Topic(""));
        Assert.Equal("(opaque)", Topic("wss://dealer.spotify.com/?access_token=secret"));
    }

    [Fact]
    public void The_log_label_names_each_frame_kind_and_redacts_the_hello()
    {
        var hello = Spotify.DealerFrame.Parse(Encoding.UTF8.GetBytes(Hello), Scratch());
        string label = Spotify.DealerHelloRules.LogLabel(hello.Kind, hello.Uri, hello.Ident);
        Assert.Equal("hm://pusher/v1/connections/…", label);
        Assert.DoesNotContain(UriId[..8], label);

        Assert.Equal("ping", Spotify.DealerHelloRules.LogLabel(Spotify.DealerFrameKind.Ping, default, default));
        Assert.Equal("pong", Spotify.DealerHelloRules.LogLabel(Spotify.DealerFrameKind.Pong, default, default));
        Assert.Equal("(unknown)", Spotify.DealerHelloRules.LogLabel(Spotify.DealerFrameKind.Unknown, default, default));
        Assert.Equal("req:hm://connect-state/v1/player/command", Spotify.DealerHelloRules.LogLabel(
            Spotify.DealerFrameKind.Request, default, "hm://connect-state/v1/player/command"u8));
    }
}
