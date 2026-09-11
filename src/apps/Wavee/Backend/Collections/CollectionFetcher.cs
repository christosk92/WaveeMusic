using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Wavee.Backend.Sync;
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
    const string ContentType = "application/vnd.collection-v2.spotify.proto";
    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<string> _username;
    readonly WaveeLogger _log;
    readonly Func<long> _nowMs;

    public CollectionFetcher(IHttpExchange http, Func<string> baseUrl, Func<string> username,
        WaveeLogger log = default, Func<long>? nowMs = null)
        => (_http, _baseUrl, _username, _log, _nowMs) =
            (http, baseUrl, username, log, nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public async Task<CollectionReadResult> FetchWireSetAsync(string wireSet, string? token, CancellationToken ct = default)
    {
        RequireWireSet(wireSet);
        if (!string.IsNullOrEmpty(token))
        {
            var response = await DeltaAsync(wireSet, token, ct).ConfigureAwait(false);
            if (response.DeltaUpdatePossible)
            {
                var delta = CollectionWireMapper.ParseDelta(wireSet, response);
                return new CollectionReadResult(wireSet, false, true, _nowMs(), token, delta.NewRevision, delta.Items.ToImmutableArray());
            }
        }
        return await WalkAsync(wireSet, ct).ConfigureAwait(false);
    }

    public Task<CollectionReadResult> ReconcileWireSetAsync(string wireSet, string trigger, CancellationToken ct = default)
    {
        RequireWireSet(wireSet);
        return WalkAsync(wireSet, ct);
    }

    async Task<CollectionReadResult> WalkAsync(string wireSet, CancellationToken ct)
    {
        var ledger = new CollectionSnapshotLedger(wireSet, _nowMs());
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        try
        {
            do
            {
                var response = await PageAsync(wireSet, next, ct).ConfigureAwait(false);
                var page = CollectionWireMapper.ParsePage(wireSet, response);
                ledger.AddPage(page.Items, response.NextPageToken, page.NewRevision);
                next = string.IsNullOrEmpty(response.NextPageToken) ? null : response.NextPageToken;
                if (next is not null && !tokens.Add(next)) break;
            } while (next is not null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Info("collection snapshot interrupted; observed adds retained, no sweep or token advance: " + ex.Message);
        }
        return new CollectionReadResult(wireSet, true, ledger.IsVerified, ledger.StartedAtMs,
            null, ledger.Token, ledger.Items.ToImmutableArray());
    }

    static void RequireWireSet(string wireSet)
    {
        if (CollectionSets.LogicalSetsForWireSet(wireSet).Count == 0)
            throw new ArgumentException("Unknown collection wire set: " + wireSet, nameof(wireSet));
    }

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

}
