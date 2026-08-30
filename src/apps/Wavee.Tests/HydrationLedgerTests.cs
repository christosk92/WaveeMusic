using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The session freshness authority (design §2.1). Everything here is about the DIFFERENCE between "we got there" and
// "we tried and could not" — the distinction the six per-service negative memos never made consistently.
public class HydrationLedgerTests
{
    static readonly EntityUri Track = EntityUri.Parse("spotify:track:t1");

    static HydrationLedger Ledger(HydrationPolicy? policy = null)
        => new(HydrationTestSupport.Session, policy ?? HydrationPolicy.Default);

    [Fact]
    public void Seal_SealsEveryRungUpToReached()
    {
        var ledger = Ledger();
        ledger.Seal(Track, HydrationLevel.Open, new HydrationOutcome(HydrationLevel.Open, HydrationStatus.Reached));

        Assert.True(ledger.IsFresh(Track, HydrationLevel.Identity));
        Assert.True(ledger.IsFresh(Track, HydrationLevel.Open));
        // Nothing above the reached rung is claimed — a Rich ask must still run.
        Assert.False(ledger.TryPeek(Track, HydrationLevel.Rich, out _));
    }

    [Fact]
    public void Seal_AboveReached_IsExhausted_NotFresh()
    {
        var ledger = Ledger();
        // The ladder ran for Open and only got to Identity: Identity seals Reached, Open seals EXHAUSTED.
        ledger.Seal(Track, HydrationLevel.Open, new HydrationOutcome(HydrationLevel.Identity, HydrationStatus.Partial));

        Assert.True(ledger.IsFresh(Track, HydrationLevel.Identity));
        Assert.False(ledger.IsFresh(Track, HydrationLevel.Open));
        Assert.True(ledger.TryPeek(Track, HydrationLevel.Open, out var sealedOutcome));
        Assert.Equal(HydrationStatus.Partial, sealedOutcome.Status);
    }

    [Fact]
    public void Seal_IgnoresFailureAndCancellation()
    {
        var ledger = Ledger();
        ledger.Seal(Track, HydrationLevel.Open, new HydrationOutcome(HydrationLevel.None, HydrationStatus.Failed, "boom"));
        ledger.Seal(Track, HydrationLevel.Open, new HydrationOutcome(HydrationLevel.None, HydrationStatus.Cancelled));
        // A transport error is not an answer — nothing is sealed, so the next ask really retries.
        Assert.False(ledger.TryPeek(Track, HydrationLevel.Identity, out _));
    }

    [Fact]
    public void ExhaustedSeal_ExpiresOnItsOwnTtl()
    {
        // The exhausted TTL is the ONLY clock on a Partial seal, so a zero window must un-seal it while the Reached
        // rung below keeps its (one hour) window.
        var ledger = Ledger(HydrationPolicy.Default with { ExhaustedPlayableTtl = TimeSpan.Zero });
        ledger.Seal(Track, HydrationLevel.Open, new HydrationOutcome(HydrationLevel.Identity, HydrationStatus.Partial));

        Assert.True(ledger.IsFresh(Track, HydrationLevel.Identity));
        Assert.False(ledger.TryPeek(Track, HydrationLevel.Open, out _));
    }

    [Fact]
    public void Invalidate_UnsealsEveryRung()
    {
        var ledger = Ledger();
        ledger.Seal(Track, HydrationLevel.Full, new HydrationOutcome(HydrationLevel.Full, HydrationStatus.Reached));
        Assert.True(ledger.IsFresh(Track, HydrationLevel.Full));

        ledger.Invalidate(Track.Uri);

        Assert.False(ledger.TryPeek(Track, HydrationLevel.Identity, out _));
        Assert.False(ledger.TryPeek(Track, HydrationLevel.Open, out _));
        Assert.False(ledger.TryPeek(Track, HydrationLevel.Rich, out _));
        Assert.False(ledger.TryPeek(Track, HydrationLevel.Full, out _));
    }

    // ── the transient failure channel (HydrationRunScope) ────────────────────────────────────────────────────────────

    [Fact]
    public void ExhaustedSeal_AfterATransientFailure_TakesTheShortWindow()
    {
        // The bug: an album's Rich rung seals Exhausted for 24 h because "this release carries no publishing facet"
        // does not change. A trait POST that 503'd looks identical from the ledger's side — so one blip cost the ©/℗
        // line and the RowBundle for a day. The run's failure channel is what tells the two apart.
        var album = EntityUri.Parse("spotify:album:al1");
        var policy = HydrationPolicy.Default with { ExhaustedPlayableTtl = TimeSpan.Zero };
        var ledger = Ledger(policy);

        ledger.Seal(album, HydrationLevel.Rich, new HydrationOutcome(HydrationLevel.Open, HydrationStatus.Partial),
                    transient: true);

        Assert.True(ledger.IsFresh(album, HydrationLevel.Open));      // what it DID reach is sealed normally
        Assert.False(ledger.TryPeek(album, HydrationLevel.Rich, out _));   // the rung it failed is already retryable
    }

    [Fact]
    public void ExhaustedSeal_WithoutATransientFailure_KeepsTheLongAlbumWindow()
    {
        // The other half: a clean run that simply found no publishing facet must NOT be re-asked in ten minutes.
        var album = EntityUri.Parse("spotify:album:al1");
        var ledger = Ledger(HydrationPolicy.Default with { ExhaustedPlayableTtl = TimeSpan.Zero });

        ledger.Seal(album, HydrationLevel.Rich, new HydrationOutcome(HydrationLevel.Open, HydrationStatus.Partial));

        Assert.True(ledger.TryPeek(album, HydrationLevel.Rich, out var o));
        Assert.Equal(HydrationStatus.Partial, o.Status);
    }

    // ── claim-then-run ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Claim_CoalescesConcurrentCallers()
    {
        var ledger = Ledger();
        var a = ledger.Claim([Track], HydrationLevel.Open);
        var b = ledger.Claim([Track], HydrationLevel.Open);

        Assert.Equal(1, a.ClaimedCount);      // the first caller owns the fetch…
        Assert.Equal(0, b.ClaimedCount);      // …and the second only waits for it
        Assert.Equal(1, b.Waits.Count);   // (Count, not Assert.Single: the element IS a Task, and an unawaited one reads as a bug)
        Assert.Equal(1, ledger.InFlight);

        a.Publish(_ => new HydrationOutcome(HydrationLevel.Open, HydrationStatus.Reached), static _ => false);
        var outcomes = await Task.WhenAll(b.Waits[0], a.Waits[0]);

        Assert.All(outcomes, o => Assert.Equal(HydrationStatus.Reached, o.Status));
        Assert.Equal(0, ledger.InFlight);     // and the slot is released, not stranded
        Assert.True(ledger.IsFresh(Track, HydrationLevel.Open));
    }

    [Fact]
    public void Claim_PartiallyOverlappingCallers_SplitTheWork_WithNoUriInBoth()
    {
        // THE double-fetch fix. The predecessor published one slot per uri but let the first claimant run a batch over
        // its OWN whole list, so a page open [x,y] and a prefetch [y,z] each fetched y. Claiming first makes the two
        // batches disjoint by construction: whoever gets there second fetches only what is left.
        var x = EntityUri.Parse("spotify:track:x");
        var y = EntityUri.Parse("spotify:track:y");
        var z = EntityUri.Parse("spotify:track:z");
        var ledger = Ledger();

        var first = ledger.Claim([x, y], HydrationLevel.Open);
        var second = ledger.Claim([y, z], HydrationLevel.Open);

        Assert.Equal(["spotify:track:x", "spotify:track:y"], first.ClaimedUris.Select(u => u.Uri));
        Assert.Equal(["spotify:track:z"], second.ClaimedUris.Select(u => u.Uri));
        Assert.Equal(2, second.Waits.Count);   // z's own slot + the join on y

        first.Fail(HydrationStatus.Failed, null);
        second.Fail(HydrationStatus.Failed, null);
        Assert.Equal(0, ledger.InFlight);
    }

    [Fact]
    public async Task Claim_DifferentLevels_AreDifferentRuns()
    {
        var ledger = Ledger();
        var identity = ledger.Claim([Track], HydrationLevel.Identity);
        var open = ledger.Claim([Track], HydrationLevel.Open);

        // A uri sealed at Identity says nothing about Open — that is exactly why the key carries the level.
        Assert.Equal(1, identity.ClaimedCount);
        Assert.Equal(1, open.ClaimedCount);

        identity.Publish(_ => new HydrationOutcome(HydrationLevel.Identity, HydrationStatus.Reached), static _ => false);
        open.Publish(_ => new HydrationOutcome(HydrationLevel.Open, HydrationStatus.Reached), static _ => false);
        await Task.WhenAll(identity.Waits[0], open.Waits[0]);
        Assert.Equal(0, ledger.InFlight);
    }

    // Ported from SpotifyArtistPopularTracksServiceTests.Ensure_CancelledCaller_Throws_WithoutKillingTheSharedLoad:
    // the claim detaches the run from any one caller, so a caller that walks away sees a cancellation while the shared
    // pass finishes and still serves everyone else. The bug it hides is a stranded slot that wedges the uri for the
    // whole session.
    [Fact]
    public async Task AbandonedJoiner_DoesNotKillTheSharedRun()
    {
        var ledger = Ledger();
        var owner = ledger.Claim([Track], HydrationLevel.Open);
        var joiner = ledger.Claim([Track], HydrationLevel.Open);

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner.Waits[0].WaitAsync(cts.Token));

        owner.Publish(_ => new HydrationOutcome(HydrationLevel.Open, HydrationStatus.Reached), static _ => false);

        Assert.Equal(HydrationStatus.Reached, (await owner.Waits[0]).Status);
        Assert.Equal(0, ledger.InFlight);
        Assert.True(ledger.IsFresh(Track, HydrationLevel.Open));
    }

    [Fact]
    public async Task Fail_SealsNothing_AndReleasesTheSlot()
    {
        var ledger = Ledger();
        var claims = ledger.Claim([Track], HydrationLevel.Open);
        claims.Fail(HydrationStatus.Failed, "transport");

        // A joiner reads a STATUS, never someone else's stack trace.
        Assert.Equal(HydrationStatus.Failed, (await claims.Waits[0]).Status);
        Assert.Equal(0, ledger.InFlight);
        Assert.False(ledger.TryPeek(Track, HydrationLevel.Open, out _));
    }

    [Fact]
    public async Task Dispose_WithoutPublishing_NeverStrandsAJoiner()
    {
        // Belt and braces: a runner that escaped on an unforeseen path must not leave a uri in flight forever, nor a
        // joiner awaiting a task that will never complete.
        var ledger = Ledger();
        Task<HydrationOutcome> waiting;
        using (var claims = ledger.Claim([Track], HydrationLevel.Open)) waiting = claims.Waits[0];

        Assert.Equal(HydrationStatus.Failed, (await waiting).Status);
        Assert.Equal(0, ledger.InFlight);
    }
}
