// ── Wavee.Tests/QueueSeedTests.cs — bug I's pure gate: where a queue seeds from ────────────────────────────────────
//
// Bug I: open the queue rail while a track is already playing but before Play was pressed in this session — a
// restored deck, or a mirrored remote cluster — and the rail shows one grey shimmer row forever, because
// `Queue.State` (Entities/Queue.cs) never leaves `EdgeState.Unknown` until something calls `Queue.Replace`, and
// none of the host's writes ever ran for a row that arrived this way. `Queue.DecideSeed` is the pure decision the
// fix hangs the host's wiring (`Playback.Host.Context.cs`'s `Restore`/`ResolveSeedContext`/`LandSeedContext`,
// `Playback.Host.Remote.cs`'s `SeedQueueFromCluster`) off — no session, no scope, no engine loop, exactly the
// house rule ("no source-text tests": the decision lives in a small pure class, and this tests THAT, the same way
// `SetupGating`, `AppUpdateToasts`, `ShutdownUpdatePolicy` and `ReleaseNotesRange` do for their own screens).
//
// THE ONE THING THAT MUST NEVER HAPPEN: a queue that already answered "what plays next" — by a real Play, or by an
// earlier seed — gets re-decided out from under it. That is not a sequence number race here; it is simply that
// `DecideSeed` refuses everything the moment `state` is not `Unknown` (the last group below).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class QueueSeedTests
{
    // ── the skeleton is still correct when nothing is playing ─────────────────────────────────────────────────────

    [Fact]
    public void No_current_track_and_no_queue_stays_Unknown()
    {
        // The one case EdgeState.Unknown exists for (Queue.cs:274-276): nobody has answered yet, so the rail must
        // render its skeleton — not an empty "up next", which would be a confident lie about a session that has
        // simply not loaded anything.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: false, hasClusterTracks: false, hasContext: false);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    [Fact]
    public void No_current_track_stays_None_even_with_rows_to_seed_from()
    {
        // A cluster or a context resolve carrying rows means nothing without a real current row to hang them off —
        // there is no "now playing" to seed a deck for, so the answer is still None, not a guess.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: false, hasClusterTracks: true, hasContext: true);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    // ── a real current row must not sit next to an Unknown queue ──────────────────────────────────────────────────

    [Fact]
    public void Real_current_with_a_resolvable_context_seeds_from_the_context()
    {
        // A restored session (or a claim with a known context and no cluster rows in hand): resolve it locally, the
        // same path a Play takes — so the rail is not a skeleton once the resolve lands.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: false, hasContext: true);
        Assert.Equal(Queue.SeedSource.Context, source);
    }

    [Fact]
    public void Real_current_with_cluster_next_tracks_seeds_from_the_cluster()
    {
        // A mirrored remote cluster carries its own prev/next tracks already in hand (no further I/O needed) — build
        // straight from those instead of going back out to resolve a context we may not even own.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: true, hasContext: false);
        Assert.Equal(Queue.SeedSource.Cluster, source);
    }

    [Fact]
    public void Cluster_rows_are_preferred_over_a_resolvable_context()
    {
        // Both available at once: the cluster's rows are already in hand and free (no I/O), the context resolve is
        // not — so a cluster that carries rows wins outright rather than kicking off a redundant resolve.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: true, hasContext: true);
        Assert.Equal(Queue.SeedSource.Cluster, source);
    }

    [Fact]
    public void Real_current_with_nothing_to_seed_from_falls_back_to_the_bare_row()
    {
        // PINNED BEHAVIOUR: a real current row, no cluster rows, no context worth resolving (a bare track played with
        // no context, or a context that turned out empty) — CurrentOnly, never None. Chosen over leaving the queue
        // Unknown because Unknown is reserved for "we have not answered yet"; here the answer already IS "just this
        // row" and saying so (a one-row "Playing from" card, no history, no next up) is honest, whereas an Unknown
        // queue next to a real, populated player bar is exactly bug I. It also matches the launch restore's existing
        // one-row fallback (Playback.Host.Context.cs's SeedCurrentRow), so Next/Previous have a cursor to walk at once.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: false, hasContext: false);
        Assert.Equal(Queue.SeedSource.CurrentOnly, source);
    }

    // ── a seed must never clobber a queue that already answered "what plays next" ──────────────────────────────────

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_queue_that_left_Unknown_is_never_reseeded(EdgeState state)
    {
        // A real Play (or an earlier seed) already wrote real rows and moved the queue off Unknown — a late cluster
        // heartbeat or a straggling resolve answer must not re-decide the question and clobber what is there, even
        // when every other input looks like a perfect seed candidate.
        Queue.SeedSource source = Queue.DecideSeed(state,
            hasCurrent: true, hasClusterTracks: true, hasContext: true);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    [Fact]
    public void A_non_Unknown_queue_with_no_current_is_also_left_alone()
    {
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Complete,
            hasCurrent: false, hasClusterTracks: false, hasContext: false);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    // ── the TAKEOVER override (A4, playback plan Wave A): the user claiming a mirrored row ───────────────────────────
    //
    // A takeover is not "a late cluster" — it is the listener adopting the session that row already belongs to. It
    // overrides the Unknown gate ONLY (a real current row is still required, and a takeover with nothing to seed
    // from still lands CurrentOnly rather than staying silent — that is what un-sticks a permanently cursor-less
    // mirrored row even once the cluster's own prev/next have gone stale).

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_takeover_reseeds_from_the_cluster_even_though_the_queue_already_left_Unknown(EdgeState state)
    {
        Queue.SeedSource source = Queue.DecideSeed(state,
            hasCurrent: true, hasClusterTracks: true, hasContext: false, takeover: true);
        Assert.Equal(Queue.SeedSource.Cluster, source);
    }

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_takeover_with_no_cluster_rows_still_lands_the_bare_current_row_rather_than_staying_silent(EdgeState state)
    {
        Queue.SeedSource source = Queue.DecideSeed(state,
            hasCurrent: true, hasClusterTracks: false, hasContext: false, takeover: true);
        Assert.Equal(Queue.SeedSource.CurrentOnly, source);
    }

    [Fact]
    public void A_takeover_still_answers_None_with_no_current_row_to_hang_it_off()
    {
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Complete,
            hasCurrent: false, hasClusterTracks: true, hasContext: true, takeover: true);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    [Fact]
    public void A_takeover_on_an_already_Unknown_queue_behaves_exactly_like_a_normal_seed()
    {
        // takeover changes nothing when the gate it overrides was never going to refuse in the first place.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: true, hasContext: false, takeover: true);
        Assert.Equal(Queue.SeedSource.Cluster, source);
    }

    // ── the seed RETRY decision (bug: a boot-time context resolve asked before the session authorises) ──────────────
    //
    // `Queue.SeedRetryOn` is the pure half of the fix for a second, milder form of bug I: the launch restore's context
    // resolve (`Playback.Host.Context.cs`'s `ResolveSeedContext`/`LandSeedContext`) can go out before the session has
    // adopted its catalog scope and comes back 401. The old code treated that identically to a genuinely empty
    // context and landed the bare row — which then made `DecideSeed` refuse to ever re-seed once the session came
    // online, because the queue had already left `EdgeState.Unknown`.

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void An_authentication_refusal_retries_on_the_next_online_transition(int status)
    {
        Queue.SeedRetryDecision decision = Queue.SeedRetryOn(status);
        Assert.Equal(Queue.SeedRetryDecision.RetryOnline, decision);
    }

    [Fact]
    public void A_genuinely_empty_context_lands_the_bare_row_now()
    {
        // 2xx, no tracks: the resolve answered honestly, so there is nothing further worth waiting on.
        Queue.SeedRetryDecision decision = Queue.SeedRetryOn(200);
        Assert.Equal(Queue.SeedRetryDecision.CurrentOnly, decision);
    }

    [Fact]
    public void A_saturated_local_api_queue_lands_the_bare_row_now_rather_than_wait()
    {
        // No server was ever asked (Queue.SeedResolveQueueFull), so there is no auth question to retry either —
        // exactly like an empty context, land the bare row.
        Queue.SeedRetryDecision decision = Queue.SeedRetryOn(Queue.SeedResolveQueueFull);
        Assert.Equal(Queue.SeedRetryDecision.CurrentOnly, decision);
    }

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_queue_that_left_Unknown_is_never_retried_even_once_online(EdgeState state)
    {
        // The armed retry re-runs DecideSeed from scratch before ever asking SeedRetryOn anything — a real Play, or
        // the cluster seed (Playback.Host.Remote.cs's SeedQueueFromCluster), may have already answered "what plays
        // next" while the retry waited for Online, and that answer must never be clobbered.
        Queue.SeedSource source = Queue.DecideSeed(state, hasCurrent: true, hasClusterTracks: false, hasContext: true);
        Assert.Equal(Queue.SeedSource.None, source);
    }
}
