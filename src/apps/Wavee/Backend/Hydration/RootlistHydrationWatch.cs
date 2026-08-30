using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── rootlist hydration WATCH, not a one-shot plan (bug: cold-launch sidebar shows raw uris) ────────────────────────────
// LiveSessionHost used to compute PlaylistHydration.RootlistOpenPlan(store) exactly ONCE, synchronously, while wiring up
// the live session — before the network rootlist had ever landed (InitialHydrate is enqueued in the SAME method but only
// runs once CachedStore.WarmComplete has settled, and even then it lands asynchronously on the sync loop). On a cold
// first launch (fresh install, or right after the setup wizard) the store's rootlist was still empty at that instant, so
// the plan came back empty and was NEVER re-run: every playlist row in the sidebar stayed a raw `spotify:playlist:` uri
// with "0 songs" and no cover until the user opened it by hand.
//
// The fix is to turn the one-shot plan into a session-scoped store WATCH: re-run RootlistOpenPlan every time the
// rootlist could plausibly have changed, and re-ask the hydrator for whatever it still finds thin. Re-asking is safe —
// RootlistOpenPlan only returns rows that are still missing a header or a membership baseline, and the hydration façade
// itself dedups in-flight/sealed asks (ledger seals + the in-flight map), so a watch that fires more often than strictly
// necessary costs nothing beyond a cheap scan of the in-memory rootlist.
//
// A Bulk store change counts as a rootlist change: `LibrarySync.InitialHydrateAsync` (and a rootlist push/ReconnectResync
// convergence) writes the rootlist inside `store.BeginBulk()`, which suppresses the per-uri "rootlist" signal and fires
// exactly one `StoreChange.Bulk` at scope exit instead — so the watch has to treat `IsBulk` as "something changed,
// re-check", not just an exact `Uri == "rootlist"` match.
//
// The kick always hops off the calling thread (`Task.Run`): `Changes` can fire on the sync-loop/writer thread, and
// planning + awaiting the hydrator synchronously inside that OnNext would block store writes on network I/O.
public sealed class RootlistHydrationWatch : IDisposable
{
    readonly IStore _store;
    readonly IEntityHydrator _hydrator;
    readonly CancellationToken _ct;
    readonly WaveeLogger _log;
    readonly IDisposable _sub;

    int _scheduled;
    int _disposed;

    public RootlistHydrationWatch(IStore store, IEntityHydrator hydrator, CancellationToken ct, WaveeLogger log = default)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hydrator = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
        _ct = ct;
        _log = log;
        _sub = store.Changes.Subscribe(Observers.From<StoreChange>(OnStoreChange));
        Schedule();   // the immediate pass preserves the existing warm-launch behaviour, where the rootlist is already on disk
    }

    void OnStoreChange(StoreChange c)
    {
        if (c.IsBulk || string.Equals(c.Uri, "rootlist", StringComparison.Ordinal))
            Schedule();
    }

    void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;   // coalesce a burst of changes into one pending kick
        _ = Task.Run(Kick);
    }

    async Task Kick()
    {
        Interlocked.Exchange(ref _scheduled, 0);   // reset first: a change arriving mid-kick schedules another pass
        if (Volatile.Read(ref _disposed) != 0 || _ct.IsCancellationRequested) return;

        try
        {
            var plan = PlaylistHydration.RootlistOpenPlan(_store);
            _log.Event(WaveeLogLevel.Info, "hydration.rootlist.plan", "rootlist playlists still thin",
                fields: [WaveeLogField.Of("count", plan.Count)]);
            if (plan.Count == 0) return;

            await _hydrator.EnsureManyAsync(plan, HydrationLevel.Open, HydrationOptions.Prefetch, _ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Warn("rootlist hydration plan failed", ex); }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);
        _sub.Dispose();
    }
}
