// The wake/network kick (deferred-four-verdicts §1): `LinksKick` takes a Waiting link's retry early with its ladder reset,
// a reconnect's Welcome opens a dealer still waiting out its backoff, and `KickLimiter` caps how often the OS signals may
// ask. All of it is the pure fold and pure rules — no socket, no timer, no OS event.
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SessionLinksKickTests
{
    static Spotify.SessionEffects Step(ref Spotify.Session s, Spotify.SessionEventKind kind,
        Spotify.TokenRef text = default, long number = 0, uint? epoch = null)
        => Spotify.Step(ref s, new Spotify.SessionEvent(kind, Text: text, Number: number, Epoch: epoch ?? Live(in s, kind)));

    static uint Live(in Spotify.Session s, Spotify.SessionEventKind kind) => Spotify.LinkOf(kind) switch
    {
        Spotify.SessionLink.Ap => s.ApEpoch,
        Spotify.SessionLink.Dealer => s.Epoch,
        _ => 0u,
    };

    static Spotify.Session Online()
    {
        var s = default(Spotify.Session);
        Spotify.Step(ref s, new Spotify.SessionEvent(Spotify.SessionEventKind.Login, Flag: true));
        Step(ref s, Spotify.SessionEventKind.Hosts, text: new(0, 4));
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);
        Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Step(ref s, Spotify.SessionEventKind.ClientTokenMinted, text: new(8, 4), number: 1_000);
        Step(ref s, Spotify.SessionEventKind.AccessTokenMinted, text: new(12, 4), number: 2_000);
        Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(16, 4));
        return s;
    }

    static Spotify.SessionEffects Kick(ref Spotify.Session s) => Step(ref s, Spotify.SessionEventKind.LinksKick);

    [Fact]
    public void A_kick_takes_a_waiting_dealers_retry_now_and_resets_its_ladder()
    {
        var s = Online();
        for (int i = 0; i < 3; i++)
        {
            Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
            if (i < 2) Step(ref s, Spotify.SessionEventKind.DealerRetry);
        }
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Dealer);
        Assert.Equal(3u, s.DealerAttempt);
        uint apEpoch = s.ApEpoch, epoch = s.Epoch;

        Assert.Equal(Spotify.SessionEffects.OpenDealer, Kick(ref s));
        Assert.Equal(Spotify.LinkPhase.Opening, s.Dealer);
        Assert.Equal(0u, s.DealerAttempt);
        Assert.Equal(Spotify.LinkPhase.Up, s.Ap);                       // the AP was never waiting: untouched
        Assert.Equal(apEpoch, s.ApEpoch);
        Assert.Equal(epoch, s.Epoch);                                   // the retry's own transition moves no epoch
        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);

        // The kicked attempt failing at once retries on the bottom rung (3 s), not the 12 s it had climbed to.
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(1u, s.DealerAttempt);
        Assert.Equal(3_000, Spotify.BackoffMs(s.DealerAttempt));
    }

    [Fact]
    public void A_kick_takes_a_waiting_ap_now_and_leaves_the_phase_online()
    {
        var s = Online();
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApRetry);
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(2u, s.ApAttempt);
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Ap);

        Assert.Equal(Spotify.SessionEffects.ResolveHosts, Kick(ref s));
        Assert.Equal(Spotify.LinkPhase.Opening, s.Ap);
        Assert.Equal(0u, s.ApAttempt);
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);             // B4: an AP reconnect under Online moves only the AP
        Assert.Equal(Spotify.LinkPhase.Up, s.Dealer);
    }

    [Fact]
    public void A_kick_during_the_first_login_ladder_walks_the_login_from_the_top()
    {
        var s = default(Spotify.Session);
        Spotify.Step(ref s, new Spotify.SessionEvent(Spotify.SessionEventKind.Login, Flag: true));
        Step(ref s, Spotify.SessionEventKind.Hosts);
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);

        Assert.Equal(Spotify.SessionEffects.ResolveHosts, Kick(ref s));
        Assert.Equal(Spotify.SessionPhase.Resolving, s.Phase);
        Assert.Equal(Spotify.LinkPhase.Opening, s.Ap);
        Assert.Equal(0u, s.ApAttempt);
    }

    [Fact]
    public void Both_waiting_links_are_kicked_in_one_fold()
    {
        var s = Online();
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(Spotify.SessionEffects.ResolveHosts | Spotify.SessionEffects.OpenDealer, Kick(ref s));
        Assert.Equal(Spotify.LinkPhase.Opening, s.Ap);
        Assert.Equal(Spotify.LinkPhase.Opening, s.Dealer);
    }

    [Fact]
    public void A_kick_on_links_that_are_up_or_opening_changes_nothing()
    {
        var s = Online();
        Spotify.Session before = s;
        Assert.Equal(Spotify.SessionEffects.None, Kick(ref s));
        Assert.Equal(before, s);

        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.DealerRetry);              // Opening
        before = s;
        Assert.Equal(Spotify.SessionEffects.None, Kick(ref s));         // a second kick stacks no socket
        Assert.Equal(before, s);

        var resting = default(Spotify.Session);
        Assert.Equal(Spotify.SessionEffects.None, Kick(ref resting));   // Offline
        Assert.Equal(default(Spotify.Session), resting);
    }

    [Fact]
    public void A_retry_timer_armed_before_a_kick_folds_to_nothing()
    {
        var s = Online();
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        uint armedDealer = s.Epoch, armedAp = s.ApEpoch;
        Kick(ref s);
        Spotify.Session before = s;

        // The timers fire later with the epoch they were armed for: not stale, but no link is Waiting any more.
        Assert.False(Spotify.IsStale(in s, new Spotify.SessionEvent(Spotify.SessionEventKind.DealerRetry, Epoch: armedDealer)));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.DealerRetry, epoch: armedDealer));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.ApRetry, epoch: armedAp));
        Assert.Equal(before, s);

        // The kicked attempts fail and re-arm: the old timers are now stale by epoch, so they cannot open a second socket.
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.True(Spotify.IsStale(in s, new Spotify.SessionEvent(Spotify.SessionEventKind.DealerRetry, Epoch: armedDealer)));
        Assert.True(Spotify.IsStale(in s, new Spotify.SessionEvent(Spotify.SessionEventKind.ApRetry, Epoch: armedAp)));
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Dealer);
    }

    [Fact]
    public void A_kick_is_a_session_level_event_and_never_stale()
    {
        Assert.Equal(Spotify.SessionLink.None, Spotify.LinkOf(Spotify.SessionEventKind.LinksKick));
        Assert.False(Spotify.IsStale(default, new Spotify.SessionEvent(Spotify.SessionEventKind.LinksKick, Epoch: 99)));
    }

    [Fact]
    public void A_reconnects_welcome_opens_a_dealer_still_waiting_out_its_backoff()
    {
        var s = Online();
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.DealerRetry);
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);   // attempt 2, Waiting
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApRetry);
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Dealer);

        var fx = Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Assert.True((fx & Spotify.SessionEffects.OpenDealer) != 0);
        Assert.Equal(Spotify.LinkPhase.Opening, s.Dealer);
        Assert.Equal(0u, s.DealerAttempt);
        Assert.Equal(Spotify.LinkPhase.Up, s.Ap);
    }

    [Fact]
    public void A_reconnects_welcome_leaves_a_live_dealer_alone()
    {
        var s = Online();
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApRetry);
        var fx = Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Assert.Equal(0u, (uint)(fx & Spotify.SessionEffects.OpenDealer));
        Assert.Equal(Spotify.LinkPhase.Up, s.Dealer);
    }

    [Theory]
    [InlineData(1_000L, Spotify.KickLimiter.Never, 5_000, true)]    // never kicked
    [InlineData(10_000L, 6_000L, 5_000, false)]                      // 4 s after the last
    [InlineData(11_000L, 6_000L, 5_000, true)]                       // exactly the gap
    [InlineData(20_000L, 6_000L, 5_000, true)]
    [InlineData(10_000L, 10_000L, 5_000, false)]                     // the same instant
    [InlineData(5_000L, 10_000L, 5_000, true)]                       // a clock that went backwards must not silence kicks
    [InlineData(7_000L, 6_000L, 500, true)]                          // an explicit shorter gap
    public void KickLimiter_allows_one_kick_per_gap(long now, long last, int gap, bool due)
        => Assert.Equal(due, Spotify.KickLimiter.Due(now, last, gap));

    [Fact]
    public void KickLimiter_defaults_to_five_seconds()
    {
        Assert.False(Spotify.KickLimiter.Due(4_999, 0));
        Assert.True(Spotify.KickLimiter.Due(5_000, 0));
    }

    [Fact]
    public void A_failure_runs_first_sighting_logs_again_after_its_link_came_up()
    {
        var tally = new Spotify.DealerTally(intervalMs: 60_000);
        Assert.True(tally.Note("dealer", 1_000, out long total));
        Assert.Equal(1, total);
        Assert.False(tally.Note("dealer", 2_000, out total));            // a repeat inside the interval is silent
        Assert.Equal(2, total);
        tally.Forget("dealer");
        Assert.True(tally.Note("dealer", 3_000, out total));             // the next run's first failure logs in full
        Assert.Equal(1, total);
    }
}
