using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;
using static Wavee.Tests.HydrationTestSupport;

namespace Wavee.Tests;

// ── One ladder pass is SHARED PROPERTY (design §2.1, the ledger's whole reason to exist) ─────────────────────────────
// Two properties, both of which the first cut of `RunOnce` got wrong, and both of which are invisible until two callers
// actually overlap in time:
//
//   • The batch covers what THIS caller CLAIMED, not what it asked for. Publishing a slot per uri and then letting the
//     first claimant fetch its own whole list means a page open [x,y] and a prefetch [y,z] BOTH fetch y — the exact
//     double-fetch the ledger exists to prevent, and one that no single-caller test can see.
//   • The batch's lifetime belongs to the SESSION, not to whichever caller happened to win the race. Running it on the
//     first caller's token meant a user navigating away cancelled the fetch every joiner was still waiting on, and left
//     the uri unsealed for the next surface to ask all over again.
//
// The gate in each test is what makes the overlap deterministic: caller A cannot finish until the test says so, which
// is the only way caller B is guaranteed to arrive while A is still in flight.
public class HydrationSharedRunTests
{
    /// <summary>A catalogue arm that blocks on a gate and RECORDS every uri it was ever asked for, per pass. "Each uri
    /// fetched once" is a claim about the union of those passes, so counting calls is not enough — the uris matter.</summary>
    sealed class GatedRecordingCatalog : ICatalogFetch
    {
        readonly IStore _store;
        readonly Task _gate;
        readonly object _lock = new();
        public readonly List<List<string>> Passes = new();
        public readonly TaskCompletionSource FirstPassStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedRecordingCatalog(IStore store, Task gate) { _store = store; _gate = gate; }

        public List<string> AllAsked
        {
            get { lock (_lock) return Passes.SelectMany(p => p).ToList(); }
        }

        public async Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityUri> uris,
            IReadOnlyList<(string Uri, int Kind)>? extraKinds, TraitSurface surface, CancellationToken ct, bool revalidate = false)
        {
            lock (_lock) Passes.Add(uris.Select(u => u.Uri).ToList());
            FirstPassStarted.TrySetResult();
            await _gate.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var landed = new List<string>(uris.Count);
            foreach (var u in uris) { _store.UpsertTrack(TrackAt(u.Uri, HydrationLevel.Open)); landed.Add(u.Uri); }
            return landed;
        }
    }

    static SpotifyProviderHydrator Hydrator(IStore store, ICatalogFetch catalog, HydrationPump pump)
        => HydrationTestSupport.Hydrator(store, catalog, new RecordingTraitPipeline(), pump,
            [new PlayableHydration(EntityKind.Track, store, new FakeEnvelopeFetch())]);

    [Fact]
    public async Task OverlappingCallers_FetchEachUriExactlyOnce()
    {
        var store = new InMemoryStore();
        using var pump = new HydrationPump(CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new GatedRecordingCatalog(store, gate.Task);
        var hydrator = Hydrator(store, catalog, pump);

        var a = hydrator.EnsureManyAsync(["spotify:track:x", "spotify:track:y"], HydrationLevel.Open);
        await catalog.FirstPassStarted.Task;                 // A is genuinely in flight before B asks
        var b = hydrator.EnsureManyAsync(["spotify:track:y", "spotify:track:z"], HydrationLevel.Open);

        gate.SetResult();
        await Task.WhenAll(a, b);
        await DrainAsync(pump);

        var asked = catalog.AllAsked;
        Assert.Equal(3, asked.Count);                        // x, y, z — y is NOT in both passes
        Assert.Equal(["spotify:track:x", "spotify:track:y", "spotify:track:z"], asked.OrderBy(u => u, StringComparer.Ordinal));
        // …and both callers still got their whole answer, which is what makes the split invisible from outside.
        Assert.Equal(2, (await a).Reached.Count);
        Assert.Equal(2, (await b).Reached.Count);
    }

    [Fact]
    public async Task CallerThatNavigatesAway_LeavesTheSharedPassRunning_ForItsJoiner()
    {
        var store = new InMemoryStore();
        using var pump = new HydrationPump(CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new GatedRecordingCatalog(store, gate.Task);
        var hydrator = Hydrator(store, catalog, pump);
        using var navAway = new CancellationTokenSource();

        var a = hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open, default, navAway.Token);
        await catalog.FirstPassStarted.Task;                 // A owns the claim and is inside the transport
        var b = hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);

        navAway.Cancel();                                     // the user leaves the page
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);

        gate.SetResult();
        var outcome = await b;                                // B is untouched by A's exit

        Assert.Equal(HydrationStatus.Reached, outcome.Status);
        Assert.Equal(["spotify:track:t1"], outcome.Reached);
        Assert.Single(catalog.Passes);                        // ONE pass served both — A contributed no lifetime to it

        // And the seal really landed, so the next surface pays nothing rather than re-asking what A abandoned.
        await hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);
        Assert.Single(catalog.Passes);
    }

    [Fact]
    public async Task AJoinerReadsAStatus_NotTheOwnersException()
    {
        // A pass that dies has to reach every caller as an OUTCOME (design §1.3). If the owner's exception propagated
        // through the shared slot, a joiner that merely rode along would be catching a stack trace from a call it never
        // made — and, worse, EnsureManyAsync's own catch would report it as that joiner's own transport failure.
        var store = new InMemoryStore();
        using var pump = new HydrationPump(CancellationToken.None);
        var catalog = new ExplodingCatalog();
        var hydrator = Hydrator(store, catalog, pump);

        var a = hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);
        var b = hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);
        catalog.Release();

        Assert.Equal(HydrationStatus.Failed, (await a).Status);
        Assert.Equal(HydrationStatus.Failed, (await b).Status);
        Assert.Equal(1, catalog.Calls);
        // Nothing sealed on a failure, so the next ask retries — for BOTH of them.
        await hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);
        Assert.Equal(2, catalog.Calls);
    }

    sealed class ExplodingCatalog : ICatalogFetch
    {
        readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public void Release() => _gate.TrySetResult();

        public async Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityUri> uris,
            IReadOnlyList<(string Uri, int Kind)>? extraKinds, TraitSurface surface, CancellationToken ct, bool revalidate = false)
        {
            Interlocked.Increment(ref Calls);
            await _gate.Task.ConfigureAwait(false);
            throw new InvalidOperationException("extended-metadata fetch failed (503)");
        }
    }
}
