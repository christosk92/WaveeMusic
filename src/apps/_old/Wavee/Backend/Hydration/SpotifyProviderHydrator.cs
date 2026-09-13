using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── THE Spotify source's hydrator (design §2.3) ──────────────────────────────────────────────────────────────────────
// Every metadata fetch the app makes for a spotify: uri now enters here. The shape is deliberately flat:
//
//     parse → drop what has no ladder → drop what is fresh → ONE catalogue POST for the whole mixed batch
//           → per-kind continuation → seal each uri at what it actually reached
//
// Two properties that shape is chosen for. First, step 0 is SHARED: a batch holding tracks, an album and a playlist
// costs one POST, not three, because extended-metadata addresses many entities × kinds in one request and the ladders
// only differ AFTER it. Second, the ledger dedupes per (uri, level), so a page open and a prefetch that collide run the
// ladder once — the reason the old code needed six per-service memos to approximate.
public sealed class SpotifyProviderHydrator : IEntityHydrator
{
    readonly ICatalogFetch _catalog;
    readonly ITraitPipeline _traits;
    readonly TraitPolicy _policy;
    readonly HydrationPump _pump;
    readonly WaveeLogger _log;
    readonly HydrationLedger _ledger;
    readonly Dictionary<EntityKind, IKindHydration> _ladders;
    readonly HydrationContext _ctx;

    /// <param name="ladders">Whatever the composition root registered, keyed by <see cref="IKindHydration.Kind"/>. A
    /// kind with no ladder is <see cref="HydrationStatus.Unsupported"/> — not an error, and not a silent success.</param>
    public SpotifyProviderHydrator(IStore store, Func<SessionContext> ctx, ICatalogFetch catalog, ITraitPipeline traits,
                                   TraitPolicy policy, HydrationPolicy hydrationPolicy,
                                   IReadOnlyList<IKindHydration> ladders, HydrationPump pump, WaveeLogger log = default)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _traits = traits ?? throw new ArgumentNullException(nameof(traits));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        _log = log;
        _ledger = new HydrationLedger(ctx ?? throw new ArgumentNullException(nameof(ctx)),
                                      hydrationPolicy ?? throw new ArgumentNullException(nameof(hydrationPolicy)), log);

        _ladders = new Dictionary<EntityKind, IKindHydration>(ladders?.Count ?? 0);
        if (ladders is not null)
            for (int i = 0; i < ladders.Count; i++) _ladders[ladders[i].Kind] = ladders[i];   // last registration wins

        // `this` is the façade the ladders recurse through, so a ladder never calls another ladder directly (and every
        // recursive ask goes through the same ledger, which is what bounds the ref-closure).
        _ctx = new HydrationContext(store, this, traits, pump, hydrationPolicy, log);
    }

    public HydrationLevel LevelOf(string uri)
    {
        var e = EntityUri.Parse(uri);
        return _ladders.TryGetValue(e.Kind, out var ladder) ? ladder.LevelOf(e.Uri) : HydrationLevel.None;
    }

    public async Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
    {
        var batch = await EnsureManyAsync([uri], level, opts, ct).ConfigureAwait(false);
        // A one-uri batch: its status IS this uri's status, except that "Reached" has to come from the reached list
        // rather than the batch verdict (a Background batch reports Partial while its single uri may already be there).
        return new HydrationOutcome(LevelOf(uri),
            batch.Reached.Count > 0 ? HydrationStatus.Reached : batch.Status);
    }

    public async Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
    {
        if (level == HydrationLevel.None || uris is null || uris.Count == 0)
            return new HydrationBatchOutcome(Array.Empty<string>(), Array.Empty<string>(), HydrationStatus.Reached);

        long started = Stopwatch.GetTimestamp();
        var reached = new List<string>(uris.Count);
        List<string>? missing = null;
        List<EntityUri>? work = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool unsupported = false;
        int fresh = 0;
        EntityKind batchKind = EntityKind.Unknown;
        bool mixed = false;

        foreach (var raw in uris)
        {
            if (string.IsNullOrEmpty(raw) || !seen.Add(raw)) continue;
            var e = EntityUri.Parse(raw);
            // THIS PROVIDER OWNS spotify: URIS ONLY. Until the SourceRegistry router lands (P4) this hydrator is what
            // `Services.Hydrator` hands to every caller, and the queue, the recents window and the Plays toggle all
            // carry MIXED uris — a local import (`wavee:local:file:<b64url(absolute path)>`) plays through the local
            // media provider and sits in the queue like anything else. Routing one of those to extended-metadata is not
            // merely a wasted 404: that uri IS the file's path, base64url-encoded, so it would be sent to spclient.
            // The per-service `spotify:track:` prefix tests that used to stop this are gone by design (they were also
            // what dropped episodes); the provider boundary is the correct place for the rule.
            if (!e.IsSpotify || !_ladders.TryGetValue(e.Kind, out var ladder))
            {
                // Unknown kind, or a kind whose surfaces are served by a return-only service (prerelease, concert).
                (missing ??= new List<string>()).Add(raw);
                unsupported = true;
                continue;
            }
            if (batchKind == EntityKind.Unknown) batchKind = e.Kind; else if (batchKind != e.Kind) mixed = true;

            // FRESHNESS IS PRESENCE **AND** AGE (design §1.2). The seal is checked first because it is one dictionary
            // hit, while LevelOf can be a membership scan — and a warm batch is the case worth making cheap.
            if (!opts.Revalidate && _ledger.TryPeek(e, level, out var sealedOutcome))
            {
                var resident = ladder.LevelOf(e.Uri);
                // A REACHED seal skips only if the entity really is still there; an EXHAUSTED seal skips regardless —
                // that is the point of it (the ladder ran and cannot get further for now).
                if (sealedOutcome.Status == HydrationStatus.Partial || resident >= level)
                {
                    if (resident >= level) reached.Add(raw); else (missing ??= new List<string>()).Add(raw);
                    fresh++;
                    continue;
                }
            }
            (work ??= new List<EntityUri>(uris.Count)).Add(e);
        }

        var status = HydrationStatus.Reached;
        if (work is not null)
        {
            if (opts.Mode == HydrationMode.Background)
            {
                // Answer with what is resident BEFORE the job is queued — the reply describes the CALLER's instant, not
                // a race against the pump — then enqueue. The caller repaints off IStore.Changes when the work lands.
                for (int i = 0; i < work.Count; i++)
                {
                    string uri = work[i].Uri;
                    if (LevelOf(uri) >= level) reached.Add(uri); else (missing ??= new List<string>()).Add(uri);
                }
                // Re-PLAN at pump time rather than replaying this plan: the queue is a delay, and everything the plan
                // above decided can be stale by the time a slot frees. Two callers that both miss the seal enqueue two
                // jobs; the first one seals, and a job that went straight to RunAsync would run the whole ladder — a
                // second catalogue POST and a second envelope — against a uri already answered, because RunAsync does
                // not consult the ledger (only RunOnce's in-flight map, which the finished run has already left).
                // Bounded by construction: the re-plan is Blocking, so it cannot enqueue again.
                var queued = new string[work.Count];
                for (int i = 0; i < work.Count; i++) queued[i] = work[i].Uri;
                var blocking = opts with { Mode = HydrationMode.Blocking };
                _pump.Enqueue(opts.Priority, pumpCt => EnsureManyAsync(queued, level, blocking, pumpCt));
            }
            else
            {
                try { status = await RunAsync(work, level, opts, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { status = HydrationStatus.Cancelled; }
                catch (Exception ex)
                {
                    // Transport failure is an OUTCOME, never an exception out of the façade (design §1.3). Nothing was
                    // sealed, so the next ask retries.
                    status = HydrationStatus.Failed;
                    _log.Event(WaveeLogLevel.Warning, "hydration.ensure.fail", "hydration batch failed", ex: ex,
                        fields: [WaveeLogField.Of("kind", KindName(batchKind, mixed)), WaveeLogField.Of("level", level.ToString())]);
                }

                for (int i = 0; i < work.Count; i++)
                {
                    string uri = work[i].Uri;
                    if (LevelOf(uri) >= level) reached.Add(uri); else (missing ??= new List<string>()).Add(uri);
                }
            }
        }

        if (status == HydrationStatus.Reached && missing is { Count: > 0 })
            status = unsupported && reached.Count == 0 ? HydrationStatus.Unsupported : HydrationStatus.Partial;

        _log.Event(WaveeLogLevel.Debug, "hydration.ensure", "hydration batch",
            elapsedMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            fields: [WaveeLogField.Of("kind", KindName(batchKind, mixed)), WaveeLogField.Of("level", level.ToString()),
                     WaveeLogField.Of("asked", seen.Count), WaveeLogField.Of("fresh", fresh),
                     WaveeLogField.Of("fetched", work?.Count ?? 0), WaveeLogField.Of("reached", reached.Count),
                     WaveeLogField.Of("surface", opts.Surface.ToString())]);

        if (!mixed && batchKind == EntityKind.Track)
            LogTrackCensus(seen, level, opts);

        return new HydrationBatchOutcome(reached,
            (IReadOnlyCollection<string>?)missing ?? Array.Empty<string>(), status);
    }

    /// <summary>CLAIM first, then run. This caller fetches EXACTLY the uris no one else had already claimed, and simply
    /// waits for the rest — so two partially overlapping callers fetch the union once, never the overlap twice.
    ///
    /// <para>The batch runs DETACHED, on the pump's session-linked token rather than <paramref name="ct"/>. A ladder
    /// pass is shared property: the caller that happened to win the claim race contributes no lifetime to it, so a user
    /// navigating away (which trips that caller's token) cancels only that caller's WAIT, and every joiner still gets
    /// its answer and its seal. Each caller then applies its OWN token to the wait, which is where a nav-away should
    /// and does show up.</para></summary>
    async Task<HydrationStatus> RunAsync(List<EntityUri> work, HydrationLevel level, HydrationOptions opts, CancellationToken ct)
    {
        var claims = _ledger.Claim(work, level);
        if (claims.ClaimedCount > 0) _ = RunClaimedAsync(claims, level, opts);   // owns and settles `claims`
        else claims.Dispose();                                                   // nothing claimed ⇒ nothing to release

        var status = HydrationStatus.Reached;
        var waits = claims.Waits;
        for (int i = 0; i < waits.Count; i++)
        {
            // WaitAsync, not a bare await: THIS caller's cancellation must detach it from the shared pass, not kill it.
            var outcome = await waits[i].WaitAsync(ct).ConfigureAwait(false);
            if (outcome.Status is HydrationStatus.Failed or HydrationStatus.Cancelled) status = outcome.Status;
        }
        return status;
    }

    /// <summary>Run the claimed subset and settle its slots. Never throws: a pass is a shared resource, and a fault has
    /// to reach every joiner as an OUTCOME (design §1.3) rather than as an exception one of them happens to catch.</summary>
    async Task RunClaimedAsync(HydrationClaims claims, HydrationLevel level, HydrationOptions opts)
    {
        using (claims)
        {
            // The run's failure channel: a best-effort step that swallowed a transport error says so here, and the seal
            // below takes the SHORT exhausted window instead of the "genuinely absent" one.
            var scope = new HydrationRunScope();
            var ct = _pump.Token;   // session-linked — see the RunAsync remark on why this is not the caller's token
            try
            {
                await RunBatchAsync(claims.ClaimedUris, level, opts, _ctx.ForRun(scope), ct).ConfigureAwait(false);
                claims.Publish(uri =>
                {
                    var resident = LevelOf(uri.Uri);
                    return resident >= level
                        ? new HydrationOutcome(resident, HydrationStatus.Reached)
                        // The ladder ran and fell short: seal EXHAUSTED so the same thin row is not re-asked every
                        // heartbeat. Invalidate(uri) is the escape hatch when a known-better answer arrives.
                        : new HydrationOutcome(resident, HydrationStatus.Partial);
                }, scope.WasTransient);
            }
            catch (OperationCanceledException) { claims.Fail(HydrationStatus.Cancelled, null); }
            catch (Exception ex)
            {
                claims.Fail(HydrationStatus.Failed, ex.Message);
                _log.Event(WaveeLogLevel.Warning, "hydration.ensure.fail", "hydration batch failed", ex: ex,
                    fields: [WaveeLogField.Of("level", level.ToString()),
                             WaveeLogField.Of("uris", claims.ClaimedCount)]);
            }
        }
    }

    async Task RunBatchAsync(List<EntityUri> work, HydrationLevel level, HydrationOptions opts, HydrationContext ctx,
                             CancellationToken ct)
    {
        // ── step 0: ONE catalogue POST for the whole mixed batch ─────────────────────────────────────────────────────
        // Every ladder's Identity rung is the catalogue kind, so every uri rides this call; the extras a ladder wants
        // fused (a Rich album's 183) ride the SAME EntityRequest rather than a second pass.
        List<(string Uri, int Kind)>? extra = null;
        for (int i = 0; i < work.Count; i++)
        {
            if (!_ladders.TryGetValue(work[i].Kind, out var ladder)) continue;
            extra ??= new List<(string, int)>();
            ladder.ExtraCatalogKinds(work[i], level, extra);
        }
        await _catalog.FetchAsync(work, extra is { Count: > 0 } ? extra : null, opts.Surface, ct, opts.Revalidate).ConfigureAwait(false);

        // ── per-kind continuations ───────────────────────────────────────────────────────────────────────────────────
        // Sequential: a page open is single-kind anyway, and serializing keeps a mixed batch from firing several second
        // transports (getTrack, getAlbum, the overview) at once behind one user gesture.
        Dictionary<EntityKind, List<EntityUri>>? byKind = null;
        for (int i = 0; i < work.Count; i++)
        {
            var e = work[i];
            byKind ??= new Dictionary<EntityKind, List<EntityUri>>();
            if (!byKind.TryGetValue(e.Kind, out var list)) byKind[e.Kind] = list = new List<EntityUri>();
            list.Add(e);
        }
        if (byKind is null) return;
        foreach (var (kind, list) in byKind)
        {
            ct.ThrowIfCancellationRequested();
            if (_ladders.TryGetValue(kind, out var ladder))
                await ladder.ContinueAsync(list, level, opts, ctx, ct).ConfigureAwait(false);
        }
    }

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
        => EnsureTraitsAsync(uris, _policy.For(surface), surface, ct);

    public async Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface,
                                        CancellationToken ct = default)
    {
        if (traits == TraitSet.None || uris is null || uris.Count == 0) return;
        // Traits are optional polish: a failure NEVER reaches the caller (design §1.3).
        try { await _traits.EnsureAsync(uris, traits, surface, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "hydration.traits.fail", "trait pass failed", ex: ex,
                fields: [WaveeLogField.Of("surface", surface.ToString()), WaveeLogField.Of("uris", uris.Count)]);
        }
    }

    public void Invalidate(string uri) => _ledger.Invalidate(uri);

    void LogTrackCensus(HashSet<string> uris, HydrationLevel level, HydrationOptions opts)
    {
        if (!_log.IsEnabled(WaveeLogLevel.Info) || uris.Count == 0) return;

        var rows = new Track?[uris.Count];
        int i = 0, exhausted = 0;
        foreach (var uri in uris)
        {
            rows[i++] = _ctx.Store.GetTrack(uri);
            var e = EntityUri.Parse(uri);
            if (_ledger.TryPeek(e, level, out var sealedOutcome) && sealedOutcome.Status == HydrationStatus.Partial)
                exhausted++;
        }

        var report = TrackHydrationCensus.Scan(rows, _policy.For(opts.Surface), exhausted: exhausted, subAsk: opts.SubAsk);
        TrackHydrationCensus.LogIfNeeded(_log, report, opts.Surface, level);
    }

    static string KindName(EntityKind kind, bool mixed) => mixed ? "mixed" : kind.ToString();
}
