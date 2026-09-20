using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── the track/episode ladder (design §2.3) ───────────────────────────────────────────────────────────────────────────
// ONE class, registered TWICE (Track and Episode), because the difference between the two is a single step: a thin
// TRACK has a second transport that can repair it (getTrack), an episode does not. Everything else — the rung
// predicate, the ref-closure, the seal — is identical, and writing it once is precisely what stops episodes from being
// silently dropped the way the seven `spotify:track:` gated services dropped them.
public sealed class PlayableHydration : IKindHydration
{
    // The ref-closure's bounds, ported verbatim from MetadataService.RunClosureAsync: 300 per batch (the transport's
    // entity ceiling), ≤900 per pass, and a yield every 512 rows so a 10k scan never owns a pool thread.
    const int ClosureBatchSize = Metadata.MetadataChunking.MaxEntitiesPerRequest;
    const int ClosureMaxPerPass = 900;
    const int ClosureYieldMask = 511;

    /// <summary>How many getTrack repairs one batch may fire. getTrack is a SINGLE-ENTITY Pathfinder envelope, so a
    /// list-scale batch must never fan out into hundreds of them; the surfaces that genuinely need the repair (now
    /// playing, an expanded row, a context resolve) ask about one or a handful of uris. Rows past the cap keep whatever
    /// TrackV4 gave them and seal Partial — the next surface touch, after the exhausted TTL, gets another window.</summary>
    const int MaxEnvelopeRepairsPerBatch = 8;

    readonly IStore _store;
    readonly IEnvelopeFetch _envelopes;
    readonly WaveeLogger _log;

    /// <param name="kind">Track or Episode. Anything else is a wiring bug and says so immediately.</param>
    public PlayableHydration(EntityKind kind, IStore store, IEnvelopeFetch envelopes, WaveeLogger log = default)
    {
        if (kind is not (EntityKind.Track or EntityKind.Episode))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "PlayableHydration covers Track and Episode only");
        Kind = kind;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
        _log = log;
    }

    public EntityKind Kind { get; }

    public HydrationLevel LevelOf(string uri) => Kind == EntityKind.Track
        ? HydrationLevels.Of(_store.GetTrack(uri))
        : HydrationLevels.Of(_store.GetEpisode(uri));

    /// <summary>Nothing to fuse: a playable's whole catalogue answer is its V4, and the per-row FACETS (tempo, tint,
    /// video, counts) are traits — a different POST with a different cadence, owned by the trait pipeline.</summary>
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }

    public async Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                                    HydrationContext ctx, CancellationToken ct)
    {
        // Identity is step 0 and nothing else — TrackV4/EpisodeV4 already landed.
        if (level >= HydrationLevel.Open && Kind == EntityKind.Track)
            await RepairAsync(uris, level, ctx, ct).ConfigureAwait(false);

        // ── the ref-closure, on the pump ─────────────────────────────────────────────────────────────────────────────
        // Scanning the rows we just wrote for blank AlbumRefs and still-thin tracks is what heals a library whose
        // cluster/collection writers seeded name-less refs. Depth-bounded BY CONSTRUCTION: the album re-entry asks for
        // Identity (whose ladder has no post-step at all) and the track re-entry asks for Open on uris the ledger seals
        // the moment this pass finishes — so the second wave finds nothing to do and stops. That seal is what replaces
        // MetadataService's `_closureAttempted` set.
        if (level > HydrationLevel.Open) return;
        var snapshot = new List<string>(uris.Count);
        for (int i = 0; i < uris.Count; i++) if (uris[i].Kind == EntityKind.Track) snapshot.Add(uris[i].Uri);
        if (snapshot.Count == 0) return;
        ctx.Pump.Enqueue(ClosurePriority, pumpCt => CloseRefsAsync(snapshot, ctx, pumpCt));
    }

    /// <summary>Below every interactive priority: the closure is cosmetic healing, never something a surface waits on.</summary>
    const int ClosurePriority = -1;

    async Task RepairAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationContext ctx, CancellationToken ct)
    {
        int repaired = 0;
        for (int i = 0; i < uris.Count && repaired < MaxEnvelopeRepairsPerBatch; i++)
        {
            ct.ThrowIfCancellationRequested();
            string uri = uris[i].Uri;
            // ONLY if still short of the ask. At Open this is the now-playing repair (a row TrackV4 left without an
            // album name or a cover); at Full it is the availability verdict, which only the envelope files.
            if (LevelOf(uri) >= level) continue;
            repaired++;
            try
            {
                if (await _envelopes.TrackAsync(uri, ct).ConfigureAwait(false) is { } full)
                {
                    _store.UpsertTrack(full);
                    // There was previously only a FAILURE line below — a repair that ran and landed but left the row
                    // thin anyway (getTrack answered, but the envelope itself was thin) was indistinguishable from a
                    // repair that never ran at all. This is the positive half of that signal.
                    _log.Event(WaveeLogLevel.Debug, "hydration.playable.envelope.ok", "getTrack repair landed",
                        fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("level", level.ToString())]);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Best-effort per uri: one failed envelope must not deny the rest of the batch its repair. It does have
                // to reach the seal, though — getTrack IS the Open rung for a thin track, so a swallowed failure here
                // would otherwise seal "this row is as good as it gets" off a socket error.
                ctx.ReportTransient(uri);
                _log.Event(WaveeLogLevel.Warning, "hydration.playable.envelope", "getTrack repair failed", ex: ex,
                    fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("level", level.ToString())]);
            }
        }
    }

    async Task CloseRefsAsync(IReadOnlyList<string> requested, HydrationContext ctx, CancellationToken ct)
    {
        List<string>? albums = null;
        List<string>? thin = null;
        int taken = 0;
        for (int i = 0; i < requested.Count && taken < ClosureMaxPerPass; i++)
        {
            if ((i & ClosureYieldMask) == ClosureYieldMask) await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (_store.GetTrack(requested[i]) is not { } track) continue;
            if (HydrationLevels.RefNeedsName(track.Album))
            {
                (albums ??= new List<string>(ClosureBatchSize)).Add(track.Album.Uri);
                taken++;
            }
            if (HydrationLevels.TrackUnnamed(track))
            {
                (thin ??= new List<string>(ClosureBatchSize)).Add(requested[i]);
                taken++;
            }
        }

        // Identity for the album refs (a name is all a denormalized ref needs); Open for the thin rows (which is what
        // buys them the getTrack repair above). Both Background — nothing is watching.
        var background = new HydrationOptions(HydrationMode.Background, Priority: ClosurePriority, SubAsk: true);
        if (albums is not null)
            foreach (var page in Pages(albums))
                await ctx.Hydrator.EnsureManyAsync(page, HydrationLevel.Identity, background, ct).ConfigureAwait(false);
        if (thin is not null)
            foreach (var page in Pages(thin))
                await ctx.Hydrator.EnsureManyAsync(page, HydrationLevel.Open, background, ct).ConfigureAwait(false);
    }

    static IEnumerable<List<string>> Pages(List<string> all)
    {
        for (int i = 0; i < all.Count; i += ClosureBatchSize)
            yield return all.GetRange(i, Math.Min(ClosureBatchSize, all.Count - i));
    }
}
