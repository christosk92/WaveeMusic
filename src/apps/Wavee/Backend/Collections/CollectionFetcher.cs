using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Backend.Collections;

/// <summary>How <see cref="CollectionFetcher.FetchWireSetAsync"/> converged the wire set.</summary>
public enum CollectionFetchOutcome : byte
{
    /// <summary>A token-gated delta applied; the token advanced to the delta's revision.</summary>
    Delta,
    /// <summary>A full walk, verified: adds applied, absent members swept, the walk's token stored.</summary>
    Snapshot,
    /// <summary>A full walk that could NOT prove completeness: adds applied, nothing swept, token untouched (the next
    /// fetch walks again).</summary>
    SnapshotUnverified,
}

/// <summary>What <see cref="CollectionFetcher.ReconcileWireSetAsync"/> found and did.</summary>
public enum CollectionReconcileOutcome : byte
{
    /// <summary>Local == server for every logical set (through the shields). Nothing written.</summary>
    NoDrift,
    /// <summary>Drift on a verified walk: the walked snapshot was applied, swept and its token stored.</summary>
    Repaged,
    /// <summary>Drift reported, but the walk was unverified so nothing was applied — the next pass tries again.</summary>
    SkippedUnverified,
}

// ── The live collection (library set) fetch ──────────────────────────────────────────────────────────────────────────
// POSTs the collection2v2 service per WIRE set ("collection"/"artist"/"show"/"listenlater" — CollectionSets.WireSets): a
// token-gated delta when we already have a sync token (cheap), otherwise the full page walk. Items fan out to every
// LOGICAL set the wire set carries (liked + albums share "collection", split by URI prefix) and fold onto the Store's set
// membership; the changed entity uris are handed to the hydrator. Revision get/set are injected (the cold-store seam,
// keyed by CollectionSets.RevisionKey) so the fetcher stays decoupled from persistence and unit-testable.
//
// The one invariant everything below serves: FinishSnapshot is the ONLY place that sweeps a local member or advances a
// token off a walk, and it does so only for a walk the CollectionSnapshotLedger could VERIFY. An unverified walk still
// applies its adds (a page we did receive is real) but never deletes and never stores a token — so a lost tail costs a
// re-walk, never the newest likes. ReconcileWireSetAsync is the periodic proof that the delta stream has not drifted:
// a shadow walk compared against the local set, repaged only on verified drift.
public sealed class CollectionFetcher
{
    // The collection2v2 route only accepts its vendor media type — `application/protobuf` is the extended-metadata type and
    // the gateway 400s on it at the media-type layer before it ever reads the body (confirmed against the reference client).
    const string ContentType = "application/vnd.collection-v2.spotify.proto";

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<string> _username;
    readonly IStore _store;
    readonly Func<string, string?> _getRevision;
    readonly Action<string, string?> _setRevision;
    readonly Func<IReadOnlyList<string>, CancellationToken, Task> _hydrate;
    // §7.2 pending-op shield: (setId, uri) → true when a local intent is in flight, so neither an inbound apply nor the
    // mark-and-sweep may touch it.
    readonly Func<string, string, bool>? _hasPending;
    readonly WaveeLogger _log;
    readonly Func<long> _nowMs;

    public CollectionFetcher(IHttpExchange http, Func<string> baseUrl, Func<string> username, IStore store,
        Func<string, string?> getRevision, Action<string, string?> setRevision,
        Func<IReadOnlyList<string>, CancellationToken, Task> hydrate, Func<string, string, bool>? hasPending = null,
        WaveeLogger log = default, Func<long>? nowMs = null)
    {
        _http = http;
        _baseUrl = baseUrl;
        _username = username;
        _store = store;
        _getRevision = getRevision;
        _setRevision = setRevision;
        _hydrate = hydrate;
        _hasPending = hasPending;
        _log = log;
        _nowMs = nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Converge one wire set: delta when a token is held and the server can honour it, else a verified full walk.</summary>
    public async Task<CollectionFetchOutcome> FetchWireSetAsync(string wireSet, CancellationToken ct = default)
    {
        var logical = RequireLogicalSets(wireSet);
        string key = CollectionSets.RevisionKey(wireSet);
        var token = _getRevision(key);

        // Legacy-db self-heal: a set synced before added_at was persisted has EVERY member timestamp-less (the old writer
        // always stored 0), and the delta path would never refresh them (deltas carry only changes). Ignore the token
        // once and full-page — timestamps land, the condition stops firing. (Live rows always carry a timestamp: server
        // items ship added_at; optimistic likes stamp local now.)
        if (!string.IsNullOrEmpty(token) && AnyTimestampless(logical)) { LogTokenReset(wireSet, "timestampless"); token = null; }

        if (!string.IsNullOrEmpty(token))
        {
            var delta = await DeltaAsync(wireSet, token!, ct).ConfigureAwait(false);
            if (delta.DeltaUpdatePossible)
            {
                // One parse, fanned out by prefix. DeltaResponse has no pagination, so a very large delta is whatever the
                // server chose to cap it at — the reconcile pass is what catches a capped one.
                var d = CollectionWireMapper.ParseDelta(wireSet, delta);
                using (_store.BeginBulk())
                    foreach (var set in logical) CollectionDeltaApplier.Apply(_store, ForLogicalSet(d, set));
                await HydrateAsync(d.Items, ct).ConfigureAwait(false);
                _setRevision(key, d.NewRevision);
                _log.Event(WaveeLogLevel.Debug, "collection.delta", "Collection delta applied",
                    fields: [WaveeLogField.Of("wireSet", wireSet), WaveeLogField.Of("items", d.Items.Count), WaveeLogField.Of("hasToken", d.NewRevision is not null)]);
                return CollectionFetchOutcome.Delta;
            }
            LogTokenReset(wireSet, "delta-not-possible");   // token too stale for the server → fall through to a full snapshot
        }

        bool verified;
        using (_store.BeginBulk())   // coalesce the multi-page snapshot + its sweep into one change signal
        {
            var ledger = await WalkAsync(wireSet, apply: true, ct).ConfigureAwait(false);
            verified = FinishSnapshot(ledger);
        }
        return verified ? CollectionFetchOutcome.Snapshot : CollectionFetchOutcome.SnapshotUnverified;
    }

    /// <summary>The drift check: shadow-walk the wire set (nothing written), compare per logical set, and repage — apply
    /// + sweep + token, off the SAME walk, not a second one — only when drift was found AND the walk verified.</summary>
    public async Task<CollectionReconcileOutcome> ReconcileWireSetAsync(string wireSet, string trigger, CancellationToken ct = default)
    {
        var logical = RequireLogicalSets(wireSet);
        var ledger = await WalkAsync(wireSet, apply: false, ct).ConfigureAwait(false);

        var reports = new List<CollectionDriftReport>(logical.Count);
        bool drift = false;
        var hasPending = _hasPending;
        foreach (var set in logical)
        {
            Func<string, bool>? pending = hasPending is null ? null : uri => hasPending(set, uri);
            var report = CollectionDrift.Compare(set, _store.SavedItems(set), ledger.UrisFor(CollectionSets.UriPrefix(set)), pending, ledger.StartedAtMs);
            reports.Add(report);
            drift |= report.HasDrift;
        }

        if (!drift)
        {
            _log.Event(WaveeLogLevel.Info, "collection.reconcile.pass", "Collection matches the server",
                fields: [WaveeLogField.Of("wireSet", wireSet), WaveeLogField.Of("trigger", trigger), WaveeLogField.Of("pages", ledger.Pages),
                         WaveeLogField.Of("items", ledger.ItemCount), WaveeLogField.Of("verdict", ledger.Verdict.ToString())]);
            return CollectionReconcileOutcome.NoDrift;
        }

        string action = ledger.IsVerified ? "repage" : "skip-unverified";
        foreach (var r in reports)
        {
            if (!r.HasDrift) continue;
            _log.Event(WaveeLogLevel.Warning, "collection.reconcile.drift", "Collection drifted from the server",
                fields: [WaveeLogField.Of("wireSet", wireSet), WaveeLogField.Of("setId", r.SetId), WaveeLogField.Of("local", r.Local),
                         WaveeLogField.Of("server", r.Server), WaveeLogField.Of("missing", r.Missing.Count), WaveeLogField.Of("extra", r.Extra.Count),
                         WaveeLogField.Of("action", action), WaveeLogField.Of("trigger", trigger)]);
        }

        // Unlike FetchWireSetAsync, an unverified reconcile walk applies NOTHING: the local set is a converged baseline
        // here, not an empty one, and a walk that cannot prove itself is not a better authority than the delta stream.
        if (!ledger.IsVerified) { FinishSnapshot(ledger); return CollectionReconcileOutcome.SkippedUnverified; }

        using (_store.BeginBulk())
        {
            foreach (var set in logical) ApplyItems(set, ledger.Items);
            FinishSnapshot(ledger);
        }
        // Only the members we did not have need metadata; everything else was hydrated when it first landed.
        var missing = new List<string>();
        foreach (var r in reports) missing.AddRange(r.Missing);
        await HydrateUrisAsync(missing, ct).ConfigureAwait(false);
        return CollectionReconcileOutcome.Repaged;
    }

    // The page loop. With apply=true every page's adds land as they arrive (under the caller's BeginBulk) and are
    // hydrated; with apply=false it is a pure shadow walk. Either way the ledger records what every page said. An
    // exception mid-loop propagates: the ledger never reaches FinishSnapshot, so a partial walk can neither sweep nor
    // store a token (and with apply=true the pages that did land stay — they are real).
    async Task<CollectionSnapshotLedger> WalkAsync(string wireSet, bool apply, CancellationToken ct)
    {
        var ledger = new CollectionSnapshotLedger(wireSet, _nowMs());
        string? pageToken = null;
        do
        {
            var page = await PageAsync(wireSet, pageToken, ct).ConfigureAwait(false);
            var d = CollectionWireMapper.ParsePage(wireSet, page);
            ledger.AddPage(d.Items, page.NextPageToken, d.NewRevision);
            if (apply)
            {
                foreach (var set in CollectionSets.LogicalSetsForWireSet(wireSet)) ApplyItems(set, d.Items);
                await HydrateAsync(d.Items, ct).ConfigureAwait(false);
            }
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        } while (pageToken is not null);
        return ledger;
    }

    // THE ONLY sweep and THE ONLY walk-sourced token advance. Returns whether the walk was verified (and so acted on).
    // An unverified walk logs and leaves the set exactly as the applied pages left it: nothing removed, token untouched.
    bool FinishSnapshot(CollectionSnapshotLedger ledger)
    {
        string wireSet = ledger.WireSet;
        var verdict = ledger.Verdict;
        _log.Event(WaveeLogLevel.Info, "collection.snapshot.pages", "Collection snapshot walked",
            fields: [WaveeLogField.Of("wireSet", wireSet), WaveeLogField.Of("pages", ledger.Pages), WaveeLogField.Of("items", ledger.ItemCount),
                     WaveeLogField.Of("duplicates", ledger.Duplicates), WaveeLogField.Of("emptyNonTerminal", ledger.EmptyNonTerminalPages),
                     WaveeLogField.Of("verdict", verdict.ToString()), WaveeLogField.Of("tokenSource", ledger.TokenSource)]);
        if (verdict != SnapshotVerdict.Verified)
        {
            _log.Event(WaveeLogLevel.Warning, "collection.snapshot.unverified", "Collection snapshot could not be verified; adds kept, nothing swept, token untouched",
                fields: [WaveeLogField.Of("wireSet", wireSet), WaveeLogField.Of("verdict", verdict.ToString()), WaveeLogField.Of("pages", ledger.Pages),
                         WaveeLogField.Of("duplicates", ledger.Duplicates)]);
            return false;
        }

        foreach (var set in CollectionSets.LogicalSetsForWireSet(wireSet))
        {
            var snapshot = ledger.UrisFor(CollectionSets.UriPrefix(set));
            var existing = _store.SavedItems(set);
            int removed = 0, shieldedPending = 0, shieldedRecent = 0;
            for (int i = 0; i < existing.Count; i++)
            {
                var item = existing[i];
                bool pending = _hasPending is not null && _hasPending(set, item.Uri);
                switch (CollectionSweepPolicy.Decide(snapshot.Contains(item.Uri), pending, item.AddedAtMs, ledger.StartedAtMs))
                {
                    case CollectionSweepPolicy.Keep.Remove: _store.SetSaved(set, item.Uri, false, SyncState.Confirmed); removed++; break;
                    case CollectionSweepPolicy.Keep.Pending: shieldedPending++; break;
                    case CollectionSweepPolicy.Keep.Recent: shieldedRecent++; break;
                }
            }
            _log.Event(WaveeLogLevel.Info, "collection.snapshot.sweep", "Collection snapshot swept",
                fields: [WaveeLogField.Of("wireSet", wireSet), WaveeLogField.Of("setId", set), WaveeLogField.Of("removed", removed),
                         WaveeLogField.Of("shieldedPending", shieldedPending), WaveeLogField.Of("shieldedRecent", shieldedRecent)]);
        }
        _setRevision(CollectionSets.RevisionKey(wireSet), ledger.Token);
        return true;
    }

    // Fold a page's (or a ledger's) items onto ONE logical set: prefix-filtered for the shared "collection" wire set,
    // shielded (§7.2) like the dealer direct-apply, Confirmed with the server's add timestamp (0 preserves the stored one).
    void ApplyItems(string setId, IReadOnlyList<CollectionItem> items)
    {
        string? prefix = CollectionSets.UriPrefix(setId);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (prefix is not null && !it.Uri.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (_hasPending is not null && _hasPending(setId, it.Uri)) continue;
            _store.SetSaved(setId, it.Uri, !it.Removed, SyncState.Confirmed, it.AddedAt);
        }
    }

    static IReadOnlyList<string> RequireLogicalSets(string wireSet)
    {
        var logical = CollectionSets.LogicalSetsForWireSet(wireSet);
        if (logical.Count == 0) throw new ArgumentException($"'{wireSet}' is not a collection wire set (see CollectionSets.WireSets).", nameof(wireSet));
        return logical;
    }

    void LogTokenReset(string wireSet, string reason)
        => _log.Event(WaveeLogLevel.Info, "collection.token.reset", "Collection sync token discarded; walking the full set",
            fields: [WaveeLogField.Of("wireSet", wireSet), WaveeLogField.Of("reason", reason)]);

    async Task<Col.DeltaResponse> DeltaAsync(string wireSet, string lastToken, CancellationToken ct)
    {
        var body = new Col.DeltaRequest { Username = _username(), Set = wireSet, LastSyncToken = lastToken }.ToByteArray();
        using var resp = await PostAsync("/collection/v2/delta", body, ct).ConfigureAwait(false);
        return Col.DeltaResponse.Parser.ParseFrom(resp.Body);
    }

    async Task<Col.PageResponse> PageAsync(string wireSet, string? pageToken, CancellationToken ct)
    {
        var req = new Col.PageRequest { Username = _username(), Set = wireSet, Limit = 300 };
        if (!string.IsNullOrEmpty(pageToken)) req.PaginationToken = pageToken;
        using var resp = await PostAsync("/collection/v2/paging", req.ToByteArray(), ct).ConfigureAwait(false);
        return Col.PageResponse.Parser.ParseFrom(resp.Body);
    }

    async Task<HttpResp> PostAsync(string path, byte[] body, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = ContentType, ["Accept"] = ContentType };
        var resp = await _http.SendAsync(new HttpReq("POST", _baseUrl() + path, headers, body), ct).ConfigureAwait(false);
        if (resp.Status != 200) { resp.Dispose(); throw new InvalidOperationException($"collection fetch failed ({resp.Status}) for {path}"); }
        return resp;
    }

    // Any logical set of the wire set whose members are ALL timestamp-less (see FetchWireSetAsync).
    bool AnyTimestampless(IReadOnlyList<string> logicalSets)
    {
        foreach (var set in logicalSets)
        {
            var items = _store.SavedItems(set);
            if (items.Count == 0) continue;
            bool all = true;
            for (int i = 0; i < items.Count; i++) if (items[i].AddedAtMs != 0) { all = false; break; }
            if (all) return true;
        }
        return false;
    }

    Task HydrateAsync(IReadOnlyList<CollectionItem> items, CancellationToken ct)
    {
        var uris = new List<string>(items.Count);
        for (int i = 0; i < items.Count; i++)
            if (!items[i].Removed) uris.Add(items[i].Uri);
        return HydrateUrisAsync(uris, ct);
    }

    async Task HydrateUrisAsync(IReadOnlyList<string> candidates, CancellationToken ct)
    {
        var uris = new List<string>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
            if (EntityUri.Parse(candidates[i]).Provider == EntityProviders.Spotify) uris.Add(candidates[i]);
        if (uris.Count > 0) await _hydrate(uris, ct).ConfigureAwait(false);
    }

    // The wire delta re-labelled for ONE logical set: only the items whose entity URI matches the set's prefix (the
    // "collection"-shared sets); every item for a prefix-less set. The token is the wire set's — it is not per set.
    static CollectionDelta ForLogicalSet(CollectionDelta d, string setId)
    {
        string? prefix = CollectionSets.UriPrefix(setId);
        if (prefix is null) return d with { SetId = setId };
        var kept = new List<CollectionItem>(d.Items.Count);
        for (int i = 0; i < d.Items.Count; i++)
            if (d.Items[i].Uri.StartsWith(prefix, StringComparison.Ordinal)) kept.Add(d.Items[i]);
        return d with { SetId = setId, Items = kept };
    }
}
