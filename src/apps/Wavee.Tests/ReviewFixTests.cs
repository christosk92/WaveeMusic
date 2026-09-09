using System.Security.Cryptography;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Xunit;

namespace Wavee.Tests;

// Regression tests for the real issues surfaced by the backend code review. Each one fails against the pre-fix code.

public class ApSignatureTests
{
    [Fact]
    public void Verify_RejectsBogusSignature()
    {
        // A random 256-byte "signature" must NOT verify against Spotify's real server key — proves verification is real,
        // not a no-op that returns true (the pre-fix code skipped verification entirely → MITM-able).
        var gs = new byte[96]; RandomNumberGenerator.Fill(gs);
        var bogus = new byte[256]; RandomNumberGenerator.Fill(bogus);
        Assert.False(ApSignature.Verify(gs, bogus));
    }

    [Fact]
    public void Verify_MalformedSignature_FailsClosed()
        => Assert.False(ApSignature.Verify(new byte[96], new byte[10]));   // wrong length → false, not an exception
}

public class ResourceRevalidationRegressionTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task SynchronouslyCompletingFetch_StillRevalidatesAgain()
    {
        // Pre-fix: a synchronously-completing (Task.FromResult) fetch left the in-flight slot non-null forever, so the key
        // never revalidated again. A huge class of reads (cache hits, immutable gids, offline) complete synchronously.
        int fetches = 0;
        var res = new Resource<string, int>(
            (k, ctx) => { fetches++; return Task.FromResult(fetches); },
            new FreshnessPolicy.Immutable(), () => Ctx);

        await res.Revalidate("k");
        Assert.Equal(1, fetches);
        await res.Revalidate("k");   // must fetch again — the slot is now cleared by identity
        Assert.Equal(2, fetches);
    }
}

public class MutationConcurrencyRegressionTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task SameUri_InTwoDifferentSets_DoNotCollide()
    {
        await using var host = new ReplicaTestHost(account: "me");
        var m = host.Mutations;
        await m.SaveAsync("liked", "spotify:track:1", true);
        await m.SaveAsync("rock", "spotify:track:1", true);
        Assert.Equal(2, m.Pending);   // pre-fix: keyed by (type, uri) → the two sets collided into one row
    }

    [Fact]
    public async Task SaveDuringReplay_DoesNotLoseTheNewerIntent()
    {
        var store = new InMemoryStore();
        var gate = new TaskCompletionSource();
        await using var host = new ReplicaTestHost(account: "me", store: store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var m = new MutationEngine(host.Replicas, [new GatedSetStrategy(gate.Task, entered)]);

        await m.SaveAsync("liked", "spotify:track:1", true);          // op A: save
        var drain = m.Drain(null!, Ctx);                   // snapshots [A], then blocks in Replay on the gate
        await entered.Task;
        await m.SaveAsync("liked", "spotify:track:1", false);         // op B: unsave — coalesces in DURING the in-flight replay
        gate.SetResult();                                  // let Replay(A) succeed
        await drain;

        // Pre-fix: Drain removed B and wrote Confirmed(saved) → the user's later unsave was silently lost.
        Assert.False(store.IsSaved("liked", "spotify:track:1"));   // B's intent (unsaved) survives
        Assert.Equal(1, m.Pending);                                // B is still Pending, not clobbered
    }

    sealed class GatedSetStrategy(Task gate, TaskCompletionSource entered) : IMutationStrategy
    {
        public string Type => "set";
        public OutboxOp Prepare(OutboxOp op) => op;
        public async Task<MutationReplayResult> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
        { entered.TrySetResult(); await gate; return new(MutationReplayDisposition.Applied); }
    }
}
