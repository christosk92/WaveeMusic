// ── Wavee.Tests/ApKeepAliveTests.cs — the AP keepalive fold (B6) ────────────────────────────────────────────────────
//
// `Spotify.ApKeepAlive.Step` is librespot's keep-alive (core/src/session.rs, #1359) as a pure fold: the Pong to a Ping is
// HELD 60 s, the server's PongAck is owed 20 s after the pong goes out, the next Ping 80 s after the ack, and the first Ping
// 20 s after the channel comes up; any of them missing is a verdict. The clock is an argument, so every fact here is "feed
// these inputs at these instants, assert the phase and the effect" — no socket, no timer, no sleep. The shell half
// (`ApPulse`, Spotify.Session.cs) only executes the effects and re-arms one timer for `DueInMs`.

using K = Wavee.Spotify.ApKeepAlive;
using Xunit;

namespace Wavee.Tests;

public class ApKeepAliveTests
{
    const long T0 = 5_000_000;

    /// <summary>Connected at <see cref="T0"/>, the Ping 60 s before <paramref name="pongAt"/>, the held pong sent then.</summary>
    static K.State Ponged(long pongAt)
    {
        var s = K.Step(default, K.Input.Connected, T0).Next;
        s = K.Step(in s, K.Input.PingReceived, pongAt - K.PongDelayMs).Next;
        var r = K.Step(in s, K.Input.Tick, pongAt);
        Assert.Equal(K.EffectKind.SendPong, r.Effect.Kind);
        return r.Next;
    }

    /// <summary><see cref="Ponged"/>, and the server's ack 40 ms later at <paramref name="ackAt"/>.</summary>
    static K.State Acked(long ackAt)
    {
        var s = Ponged(ackAt - 40);
        var r = K.Step(in s, K.Input.PongAckReceived, ackAt);
        Assert.Equal(K.Effect.None, r.Effect);
        return r.Next;
    }

    /// <summary>The numbers are librespot's (core/src/session.rs:683-686) — the provenance each constant's doc cites.</summary>
    [Fact]
    public void The_windows_are_librespots()
    {
        Assert.Equal(60_000, K.PongDelayMs);          // PONG_DELAY
        Assert.Equal(20_000, K.PongAckTimeoutMs);     // PONG_ACK_TIMEOUT
        Assert.Equal(80_000, K.PingTimeoutMs);        // PING_TIMEOUT, "60s expected + 20s buffer"
        Assert.Equal(20_000, K.FirstPingTimeoutMs);   // INITIAL_PING_TIMEOUT
        Assert.Equal("no-first-ping", K.Word(K.DeadReason.NoFirstPing));
        Assert.Equal("no-pong-ack", K.Word(K.DeadReason.NoPongAck));
        Assert.Equal("no-ping", K.Word(K.DeadReason.NoPing));
    }

    /// <summary>THE change B6 makes: a Ping is never answered on the spot (8 of 12 resets on 2026-09-19 came 11-57 ms after
    /// the immediate pong). The pong is held exactly <see cref="K.PongDelayMs"/>, and an early wake changes nothing.</summary>
    [Fact]
    public void A_ping_is_answered_only_after_the_pong_delay()
    {
        long pingAt = T0 + 1_000;
        var s = K.Step(default, K.Input.Connected, T0).Next;
        Assert.Equal(K.Phase.AwaitingFirstPing, s.Phase);

        var ping = K.Step(in s, K.Input.PingReceived, pingAt);
        Assert.Equal(K.Effect.None, ping.Effect);
        s = ping.Next;
        Assert.Equal(K.Phase.PendingPong, s.Phase);
        Assert.Equal((long)K.PongDelayMs, K.DueInMs(in s, pingAt));

        var early = K.Step(in s, K.Input.Tick, pingAt + K.PongDelayMs - 1);
        Assert.Equal(K.Effect.None, early.Effect);
        Assert.Equal(s, early.Next);                                         // a spurious wake moves nothing
        Assert.Equal(1L, K.DueInMs(in s, pingAt + K.PongDelayMs - 1));

        var due = K.Step(in s, K.Input.Tick, pingAt + K.PongDelayMs);
        Assert.Equal(K.Effect.SendPong, due.Effect);
        Assert.Equal(K.Phase.AwaitingPongAck, due.Next.Phase);
        // The ack window runs from the pong's actual send.
        Assert.Equal((long)K.PongAckTimeoutMs, K.DueInMs(due.Next, pingAt + K.PongDelayMs));
    }

    /// <summary>The Ping that rode behind the welcome is fed AFTER <c>Connected</c> (the pump starts once the tokens are
    /// minted) but at the instant it arrived, so its pong is held from the ping, not from the pump.</summary>
    [Fact]
    public void The_ping_that_came_with_the_welcome_is_held_from_its_own_instant()
    {
        long pingAt = T0, pumpAt = T0 + 3_000;
        var s = K.Step(default, K.Input.Connected, pumpAt).Next;
        s = K.Step(in s, K.Input.PingReceived, pingAt).Next;

        Assert.Equal(K.Phase.PendingPong, s.Phase);
        Assert.Equal((long)(K.PongDelayMs - 3_000), K.DueInMs(in s, pumpAt));
        Assert.Equal(K.Effect.SendPong, K.Step(in s, K.Input.Tick, pingAt + K.PongDelayMs).Effect);
    }

    [Fact]
    public void The_pong_ack_clears_the_ack_watchdog()
    {
        long pongAt = T0 + K.PongDelayMs;
        var s = Ponged(pongAt);

        var ack = K.Step(in s, K.Input.PongAckReceived, pongAt + 40);
        Assert.Equal(K.Effect.None, ack.Effect);
        Assert.Equal(K.Phase.AwaitingPing, ack.Next.Phase);
        Assert.Equal((long)K.PingTimeoutMs, K.DueInMs(ack.Next, pongAt + 40));
        // The instant the ack window would have lapsed is now an ordinary moment of waiting for the next ping.
        Assert.Equal(K.Effect.None, K.Step(ack.Next, K.Input.Tick, pongAt + K.PongAckTimeoutMs).Effect);
    }

    [Fact]
    public void No_ack_inside_its_window_is_dead_and_said_once()
    {
        long pongAt = T0 + K.PongDelayMs;
        var s = Ponged(pongAt);

        Assert.Equal(K.Effect.None, K.Step(in s, K.Input.Tick, pongAt + K.PongAckTimeoutMs - 1).Effect);
        var dead = K.Step(in s, K.Input.Tick, pongAt + K.PongAckTimeoutMs);
        Assert.Equal(K.Effect.Dead(K.DeadReason.NoPongAck), dead.Effect);
        Assert.Equal(K.Phase.Idle, dead.Next.Phase);

        // Returned once: the machine is stopped, nothing is armed, and a later tick or a late ack is nothing.
        Assert.Equal(-1L, K.DueInMs(dead.Next, pongAt + K.PongAckTimeoutMs));
        Assert.Equal(K.Effect.None, K.Step(dead.Next, K.Input.Tick, pongAt + 10L * K.PongAckTimeoutMs).Effect);
        Assert.Equal(K.Phase.Idle, K.Step(dead.Next, K.Input.PongAckReceived, pongAt + 30_000).Next.Phase);
    }

    [Fact]
    public void No_ping_after_an_ack_is_dead()
    {
        long ackAt = T0 + K.PongDelayMs + 40;
        var s = Acked(ackAt);

        Assert.Equal(K.Effect.None, K.Step(in s, K.Input.Tick, ackAt + K.PingTimeoutMs - 1).Effect);
        Assert.Equal(K.Effect.Dead(K.DeadReason.NoPing), K.Step(in s, K.Input.Tick, ackAt + K.PingTimeoutMs).Effect);
    }

    [Fact]
    public void No_ping_after_connecting_is_dead()
    {
        var s = K.Step(default, K.Input.Connected, T0).Next;

        Assert.Equal(K.Effect.None, K.Step(in s, K.Input.Tick, T0 + K.FirstPingTimeoutMs - 1).Effect);
        Assert.Equal(K.Effect.Dead(K.DeadReason.NoFirstPing), K.Step(in s, K.Input.Tick, T0 + K.FirstPingTimeoutMs).Effect);
    }

    /// <summary>A new channel restarts the machine from the top whatever it was doing — the old channel's owed ack no longer
    /// exists — and a stopped machine starts again the same way.</summary>
    [Fact]
    public void A_new_Connected_restarts_the_machine_from_the_top()
    {
        long pongAt = T0 + K.PongDelayMs;
        var s = Ponged(pongAt);                                              // an ack owed by pongAt + 20 s
        long reconnectAt = pongAt + 5_000;

        var fresh = K.Step(in s, K.Input.Connected, reconnectAt);
        Assert.Equal(K.Effect.None, fresh.Effect);
        Assert.Equal(new K.State(K.Phase.AwaitingFirstPing, reconnectAt + K.FirstPingTimeoutMs), fresh.Next);
        Assert.Equal(K.Effect.None, K.Step(fresh.Next, K.Input.Tick, pongAt + K.PongAckTimeoutMs).Effect);

        var dead = K.Step(in s, K.Input.Tick, pongAt + K.PongAckTimeoutMs).Next;
        Assert.Equal(K.Phase.Idle, dead.Phase);
        Assert.Equal(new K.State(K.Phase.AwaitingFirstPing, reconnectAt + K.FirstPingTimeoutMs),
            K.Step(in dead, K.Input.Connected, reconnectAt).Next);
    }

    [Fact]
    public void A_stopped_machine_ignores_everything_but_Connected()
    {
        K.State idle = default;
        foreach (var input in new[] { K.Input.PingReceived, K.Input.PongAckReceived, K.Input.Tick })
        {
            var r = K.Step(in idle, input, T0);
            Assert.Equal(idle, r.Next);
            Assert.Equal(K.Effect.None, r.Effect);
        }
        Assert.False(K.IsDue(in idle, long.MaxValue));
        Assert.Equal(-1L, K.DueInMs(in idle, T0));
    }

    /// <summary>librespot's out-of-turn rules, kept (it warns, then obeys): a second Ping before the held pong went out
    /// re-arms the hold from the new ping; an ack in any phase starts the wait for the next ping. Both are out of turn for
    /// the shell's log line.</summary>
    [Fact]
    public void Out_of_turn_packets_are_obeyed_as_librespot_obeys_them()
    {
        var s = K.Step(default, K.Input.Connected, T0).Next;
        Assert.True(K.Expected(s.Phase, K.Input.PingReceived));
        s = K.Step(in s, K.Input.PingReceived, T0).Next;
        Assert.False(K.Expected(s.Phase, K.Input.PingReceived));

        var again = K.Step(in s, K.Input.PingReceived, T0 + 30_000).Next;
        Assert.Equal(new K.State(K.Phase.PendingPong, T0 + 30_000 + K.PongDelayMs), again);
        Assert.Equal(K.Effect.None, K.Step(in again, K.Input.Tick, T0 + K.PongDelayMs).Effect);   // the first ping's moment passes

        Assert.False(K.Expected(K.Phase.PendingPong, K.Input.PongAckReceived));
        Assert.True(K.Expected(K.Phase.AwaitingPongAck, K.Input.PongAckReceived));
        Assert.Equal(new K.State(K.Phase.AwaitingPing, T0 + 31_000 + K.PingTimeoutMs),
            K.Step(in again, K.Input.PongAckReceived, T0 + 31_000).Next);

        Assert.True(K.Expected(K.Phase.AwaitingPing, K.Input.PingReceived));
        Assert.True(K.Expected(K.Phase.Idle, K.Input.Tick));
        Assert.True(K.Expected(K.Phase.PendingPong, K.Input.Connected));
    }

    /// <summary>The server's own cadence — a Ping every 120 s (2026-09-19), the ack right behind each pong — runs for a
    /// hundred minutes without a verdict, one pong per ping, each held exactly 60 s: librespot's timeline, with the shell's
    /// timer waking at every deadline the fold reports and never in between.</summary>
    [Fact]
    public void The_servers_two_minute_cadence_never_trips_a_window()
    {
        const long Period = 120_000, AckRttMs = 40;
        var s = K.Step(default, K.Input.Connected, T0).Next;
        int pongs = 0;
        long lastPongAt = 0;
        for (int cycle = 0; cycle < 50; cycle++)
        {
            long pingAt = T0 + 1_000 + cycle * Period;
            s = WakeUntil(s, pingAt, ref pongs, ref lastPongAt);
            s = K.Step(in s, K.Input.PingReceived, pingAt).Next;

            long ackAt = pingAt + K.PongDelayMs + AckRttMs;
            s = WakeUntil(s, ackAt, ref pongs, ref lastPongAt);
            Assert.Equal(cycle + 1, pongs);
            Assert.Equal((long)K.PongDelayMs, lastPongAt - pingAt);
            Assert.True(K.Expected(s.Phase, K.Input.PongAckReceived));
            s = K.Step(in s, K.Input.PongAckReceived, ackAt).Next;
        }
        Assert.Equal(K.Phase.AwaitingPing, s.Phase);

        // Every timer wake the shell would take before `at`: one per deadline that falls due, none of them a verdict.
        static K.State WakeUntil(K.State s, long at, ref int pongs, ref long lastPongAt)
        {
            while (K.IsDue(in s, at))
            {
                long wake = s.DeadlineMs;
                var r = K.Step(in s, K.Input.Tick, wake);
                Assert.NotEqual(K.EffectKind.Dead, r.Effect.Kind);
                if (r.Effect.Kind == K.EffectKind.SendPong)
                {
                    pongs++;
                    lastPongAt = wake;
                }
                s = r.Next;
            }
            return s;
        }
    }

    /// <summary>Times are compared by difference: a clock that wraps past <c>long.MaxValue</c> mid-cycle still holds the pong
    /// exactly 60 s and still calls a missing ack exactly 20 s after it.</summary>
    [Fact]
    public void The_clock_may_wrap_mid_cycle()
    {
        long start = long.MaxValue - 30_000;
        long pingAt = start + 1_000;
        var s = K.Step(default, K.Input.Connected, start).Next;
        s = K.Step(in s, K.Input.PingReceived, pingAt).Next;
        Assert.True(s.DeadlineMs < 0, "the pong's deadline wrapped: " + s.DeadlineMs);

        long pongAt = unchecked(pingAt + K.PongDelayMs);
        Assert.Equal(K.Effect.None, K.Step(in s, K.Input.Tick, long.MaxValue).Effect);          // before the wrap: not yet
        Assert.Equal(K.Effect.None, K.Step(in s, K.Input.Tick, unchecked(pongAt - 1)).Effect);  // after it: one ms early
        Assert.Equal((long)(K.PongDelayMs - 1_000), K.DueInMs(in s, start + 1_000 + 1_000));

        var pong = K.Step(in s, K.Input.Tick, pongAt);
        Assert.Equal(K.Effect.SendPong, pong.Effect);
        Assert.Equal((long)K.PongAckTimeoutMs, K.DueInMs(pong.Next, pongAt));
        Assert.Equal(K.Effect.None, K.Step(pong.Next, K.Input.Tick, unchecked(pongAt + K.PongAckTimeoutMs - 1)).Effect);
        Assert.Equal(K.Effect.Dead(K.DeadReason.NoPongAck),
            K.Step(pong.Next, K.Input.Tick, unchecked(pongAt + K.PongAckTimeoutMs)).Effect);
    }
}
