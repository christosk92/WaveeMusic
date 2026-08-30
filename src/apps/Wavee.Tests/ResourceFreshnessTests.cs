using System;
using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// The revision-gated policies (FreshnessPolicy.SnapshotRevision / RevisionDelta) are DELETED with the rest of the dead
// hydration paths (hydration-facade-plan.md 1.6): nothing constructed them, and the freshness authority for entities is
// now HydrationLedger (its TTL/seal/unseal behaviour is pinned by HydrationLedgerTests). Deleted with them:
//   SnapshotRevision_NotStaleUntilMarked_ThenRevalidatesOnce -> HydrationLedgerTests.Seal_SealsEveryRungUpToReached +
//                                                               .Invalidate_UnsealsEveryRung
//   RevisionDelta_Use_DoesNotHerd                            -> HydrationLedgerTests.RunOnce_CoalescesConcurrentCallers
// What survives is the generic MarkStale contract, which the LIVE policies (Etag, used by ExtensionEtagCache and the
// ledger) still depend on - so it is re-pinned here on Etag rather than lost with the policies it happened to be
// written against.
public class ResourceFreshnessTests
{
    static SessionContext Ctx() => SessionContext.LoggedOut;

    [Fact]
    public void MarkStale_OnUnknownKey_CreatesDirtyEntry()
    {
        var res = new Resource<string, int>((k, _) => Task.FromResult(0),
            new FreshnessPolicy.Etag(TimeSpan.FromHours(1)), Ctx);
        res.MarkStale("never-seen");
        Assert.True(res.Peek("never-seen").IsLoading);   // no value yet -> Use will fetch it
    }

    [Fact]
    public async Task MarkStale_ForcesExactlyOneRevalidation_ThenGoesFreshAgain()
    {
        // The anti-herd contract the deleted revision policies expressed, kept on the policy that is actually wired:
        // a resident, unmarked entry inside its TTL is never re-fetched; a marked one revalidates once and clears.
        int fetches = 0;
        var res = new Resource<string, int>((k, _) => { fetches++; return Task.FromResult(42); },
            new FreshnessPolicy.Etag(TimeSpan.FromHours(1)), Ctx);

        await res.Revalidate("p");
        Assert.Equal(1, fetches);
        Assert.False(res.Peek("p").IsStale);

        res.Use("p");                              // served resident; must NOT trigger another fetch
        res.Use("p");
        await Task.Yield();
        Assert.Equal(1, fetches);

        res.MarkStale("p");
        Assert.True(res.Peek("p").IsStale);
        await res.Revalidate("p");
        Assert.Equal(2, fetches);
        Assert.False(res.Peek("p").IsStale);       // cleared after the refetch
    }
}
