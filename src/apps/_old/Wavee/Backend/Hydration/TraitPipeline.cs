using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Metadata;
using Wavee.Core;
using CoreKind = Wavee.Core.EntityKind;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration;

// ── THE trait pipeline (design §2.4) ─────────────────────────────────────────────────────────────────────────────────
// One pass, one plan, one POST per 300 uris, one bulk window per POST. What it replaces: four services that each walked
// the same uri list, each applied its own prefix test, each capped at its own 300, each kept its own negative memo and
// each fired its own conditional POST — so opening an album cost three requests carrying overlapping uris, and opening
// a show cost zero because nobody had wired it up.
//
// The three rules that make it cheap, in the order they apply:
//   1. PLAN before asking. A kind is asked for a uri only when it applies to that entity kind (TraitApplicability),
//      the resident row does not already carry it (ITraitProjector.AlreadyHas) and the session has not already been
//      told "no" (NegativeMemo). A warm page therefore plans nothing and costs literally zero requests.
//   2. ONE request per page. Every wanted kind (plus its companions) rides under its uri in a single
//      ExtensionEtagCache.GetAsync — the cache groups the kinds per uri, sends the etags it holds, and answers 304s
//      without a body. The cache is REQUIRED, not optional: a raw-transport fallback would be a second, unconditional
//      path to the same bytes, which is exactly the waste this replaces.
//   3. ONE bulk window per page, opened LAZILY on the first write (TraitBatch), so a page that projects nothing
//      publishes no store change at all.
//
// Deliberately NO in-flight coalescer: ExtensionEtagCache already serialises its misses behind _batchGate, so two
// surfaces asking for the same uris at once collapse to one fetch there, and the second pass then projects from cache.
// Duplicated projection is idempotent (every projector is a fold onto the resident row), so a coalescer here would buy
// nothing and own a lifetime.

/// <summary>The real <see cref="ITraitPipeline"/>. Best-effort throughout: traits are polish, so a transport failure is
/// logged and dropped, never surfaced to the ladder that asked (design §1.3).</summary>
public sealed class TraitPipeline : ITraitPipeline
{
    readonly IStore _store;
    readonly ExtensionEtagCache _cache;
    readonly NegativeMemo _negatives;
    readonly IReadOnlyList<ITraitProjector> _projectors;
    readonly WaveeLogger _log;
    readonly Func<DateTimeOffset> _now;

    /// <param name="cache">REQUIRED. The conditional extension cache is the ONLY way this pipeline reaches the wire —
    /// it owns the etags, the 24h durable negatives and the miss serialisation.</param>
    /// <param name="negatives">Shared with <see cref="ExtensionReader"/>: a "no" learned by a drawer read must stop the
    /// row pass from re-asking, and vice-versa.</param>
    public TraitPipeline(IStore store, ExtensionEtagCache cache, NegativeMemo negatives,
                         IReadOnlyList<ITraitProjector> projectors, WaveeLogger log = default,
                         Func<DateTimeOffset>? now = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _negatives = negatives ?? throw new ArgumentNullException(nameof(negatives));
        _projectors = projectors ?? throw new ArgumentNullException(nameof(projectors));
        _log = log;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>One planned uri: which of the wanted projectors still want it, as a bitmask over the wanted list (at
    /// most seven flags exist, so a mask is both cheaper and easier to page than a list per row).</summary>
    readonly record struct PlanRow(string Uri, CoreKind Kind, int Mask);

    public async Task EnsureAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface,
                                  CancellationToken ct = default)
    {
        if (traits == TraitSet.None || uris is null || uris.Count == 0) return;

        // Which projectors does this surface's bundle switch on? Order is the registry's, which is what makes the plan
        // mask and the log tally line up run to run.
        List<ITraitProjector>? wanted = null;
        for (int i = 0; i < _projectors.Count; i++)
            if ((_projectors[i].Trait & traits) != 0)
                (wanted ??= new List<ITraitProjector>(_projectors.Count)).Add(_projectors[i]);
        if (wanted is null) return;

        var now = _now();
        var plan = Plan(uris, wanted, now);
        if (plan.Count == 0) return;

        string? clientFeatureId = surface.ClientFeatureId();

        // Page by DISTINCT URIS at the transport's own ceiling, so one page is one conditional POST and one bulk window.
        // (The cache chunks again by body size; a page that somehow exceeds 4 MB simply becomes two POSTs there — the
        // projection and the bulk window are still one per page, which is what the surfaces feel.)
        for (int start = 0; start < plan.Count; start += MetadataChunking.MaxEntitiesPerRequest)
        {
            int count = Math.Min(MetadataChunking.MaxEntitiesPerRequest, plan.Count - start);
            await RunPageAsync(plan, start, count, wanted, traits, surface, clientFeatureId, now, ct).ConfigureAwait(false);
        }
    }

    // ── Planning ────────────────────────────────────────────────────────────────────────────────────────────────────
    List<PlanRow> Plan(IReadOnlyList<string> uris, List<ITraitProjector> wanted, DateTimeOffset now)
    {
        var plan = new List<PlanRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < uris.Count; i++)
        {
            string uri = uris[i];
            if (string.IsNullOrEmpty(uri) || !seen.Add(uri)) continue;

            var e = EntityUri.Parse(uri);
            // spotify: ONLY. Trait surfaces carry MIXED uris — the queue holds `wavee:local:file:<b64url(path)>` rows
            // whenever a local import is playing, and list surfaces ask for whatever the open list holds. A kind
            // test alone admits those (a local playable is a Track too), which both wastes the round trip and ships a
            // local file path to spclient. Episodes are deliberately KEPT: they are the ask-once case, not an excluded one.
            if (!e.IsSpotify || e.Kind == CoreKind.Unknown) continue;

            int mask = 0;
            for (int j = 0; j < wanted.Count; j++)
            {
                var p = wanted[j];
                if (!p.AppliesTo(e.Kind)) continue;
                if (_negatives.Contains(uri, p.Kind)) continue;
                if (p.AlreadyHas(_store, uri, now)) continue;
                mask |= 1 << j;
            }
            if (mask != 0) plan.Add(new PlanRow(uri, e.Kind, mask));
        }
        return plan;
    }

    // ── One page: one request, one projection sweep, one bulk window ────────────────────────────────────────────────
    async Task RunPageAsync(List<PlanRow> plan, int start, int count, List<ITraitProjector> wanted, TraitSet traits,
                            TraitSurface surface, string? clientFeatureId, DateTimeOffset now, CancellationToken ct)
    {
        var reqs = new List<(string Uri, Xm.ExtensionKind Kind)>(count * 2);
        var kindsSeen = new HashSet<Xm.ExtensionKind>();
        BuildRequests(plan, start, count, wanted, reqs, kindsSeen);
        if (reqs.Count == 0) return;

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> values;
        try
        {
            values = await _cache.GetAsync(reqs, ct, clientFeatureId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Memoize NOTHING on a transport failure: "the network was down" is not "this entity has no such
            // extension", and sealing it as one is how a facet disappears for a whole session.
            _log.Event(WaveeLogLevel.Warning, "traits.fetch.fail", "trait fetch failed", ex: ex,
                fields: [WaveeLogField.Of("surface", surface.ToString()), WaveeLogField.Of("uris", count),
                         WaveeLogField.Of("kinds", kindsSeen.Count)]);
            return;
        }

        int applied = 0, unchanged = 0, negative = 0, notResident = 0, unanswered = 0;
        int pcUnans = 0, pcNeg = 0, pcNr = 0;
        int tempoUnans = 0, tempoNeg = 0, tempoNr = 0;
        int tagUnans = 0, tagNeg = 0, tagNr = 0;
        Dictionary<Xm.ExtensionKind, int>? negByKind = null;
        Dictionary<CoreKind, int>? negByEntity = null;
        int touched = 0;   // bitmask of the wanted projectors that saw at least one uri on this page

        using var batch = new TraitBatch(_store, now, surface, _log);
        for (int i = start; i < start + count; i++)
        {
            var row = plan[i];
            var payloads = new TraitPayloads(values, row.Uri);
            for (int j = 0; j < wanted.Count; j++)
            {
                if ((row.Mask & (1 << j)) == 0) continue;
                var p = wanted[j];
                touched |= 1 << j;

                // ABSENT IS NOT AN OUTCOME — the same rule ExtensionEtagCache enforces one layer down. A key the
                // response simply omitted stays unmemoized so the next pass retries it.
                if (!payloads.HasAnswer(p.Kind))
                {
                    unanswered++;
                    TallyTrait(p.Trait, ref pcUnans, ref tempoUnans, ref tagUnans);
                    continue;
                }

                TraitOutcome outcome;
                try { outcome = p.Project(batch, row.Uri, payloads); }
                catch (Exception ex)
                {
                    _log.Event(WaveeLogLevel.Warning, "traits.project.fail", "trait projector failed", ex: ex,
                        fields: [WaveeLogField.Of("kind", p.Kind.ToString()), WaveeLogField.Of("uri", row.Uri)]);
                    continue;
                }

                switch (outcome)
                {
                    case TraitOutcome.Applied: applied++; break;
                    case TraitOutcome.Unchanged:
                        unchanged++;
                        _negatives.Add(row.Uri, p.Kind);   // the store already agrees — re-asking cannot change it
                        break;
                    case TraitOutcome.Negative:
                        negative++;
                        TallyTrait(p.Trait, ref pcNeg, ref tempoNeg, ref tagNeg);
                        _negatives.Add(row.Uri, p.Kind);
                        (negByKind ??= new Dictionary<Xm.ExtensionKind, int>()).TryGetValue(p.Kind, out int k);
                        negByKind[p.Kind] = k + 1;
                        (negByEntity ??= new Dictionary<CoreKind, int>()).TryGetValue(row.Kind, out int n);
                        negByEntity[row.Kind] = n + 1;
                        break;
                    // NotResident: the row is not in the store, so there was nothing to decorate. NEVER memoized — the
                    // answer is wanted the moment the row lands, and a trait never mints one.
                    default:
                        notResident++;
                        TallyTrait(p.Trait, ref pcNr, ref tempoNr, ref tagNr);
                        break;
                }
            }
        }

        // The projection sweep is done, so PUBLISH it before the follow-ups: `BeginBulk` suppression is store-wide, and
        // the video arm of CompleteBatchAsync makes two network round trips. Holding the page's scope across them
        // silenced every change signal in the app — the now-playing fold, a save toggle, a playlist mutation — for the
        // length of a POST, and delayed this page's own tints/tempos behind it. (The service this replaced closed its
        // bulk first for the same reason.) A recovery write below simply opens a second, short scope of its own.
        batch.FlushBulk();

        // After every uri: the aggregate follow-ups (video's canonical-alias recovery).
        for (int j = 0; j < wanted.Count; j++)
        {
            if ((touched & (1 << j)) == 0) continue;
            try { await wanted[j].CompleteBatchAsync(batch, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Event(WaveeLogLevel.Warning, "traits.complete.fail", "trait batch completion failed", ex: ex,
                    fields: [WaveeLogField.Of("kind", wanted[j].Kind.ToString())]);
            }
        }

        _log.Event(WaveeLogLevel.Debug, "traits.batch", "trait batch projected",
            fields: [WaveeLogField.Of("surface", surface.ToString()),
                     WaveeLogField.Of("cfid", clientFeatureId ?? ""),
                     WaveeLogField.Of("uris", count),
                     WaveeLogField.Of("kinds", kindsSeen.Count),
                     WaveeLogField.Of("applied", applied),
                     WaveeLogField.Of("unchanged", unchanged),
                     WaveeLogField.Of("negative", negative),
                     WaveeLogField.Of("notResident", notResident),
                     WaveeLogField.Of("unanswered", unanswered),
                     WaveeLogField.Of("writes", batch.Writes),
                     WaveeLogField.Of("negByKind", Tally(negByKind)),
                     WaveeLogField.Of("negByEntity", Tally(negByEntity))]);

        LogTrackCensus(plan, start, count, traits, surface,
            new TrackHydrationCensus.TraitTallies(pcUnans, pcNeg, pcNr),
            new TrackHydrationCensus.TraitTallies(tempoUnans, tempoNeg, tempoNr),
            new TrackHydrationCensus.TraitTallies(tagUnans, tagNeg, tagNr));
    }

    /// <summary>Flatten the page into the (uri, kind) query list. A uri's kinds MUST be CONTIGUOUS:
    /// <see cref="MetadataChunking.ExtensionRanges"/> only ever flushes a chunk on a uri boundary, so a uri whose kinds
    /// straddled two chunks would be sent in two POSTs and answered as two partial entity groups.
    /// <para>Its own method because <see cref="ITraitProjector.Companions"/> is a <c>ReadOnlySpan</c> and a ref struct
    /// cannot live in an async method's frame.</para></summary>
    static void BuildRequests(List<PlanRow> plan, int start, int count, List<ITraitProjector> wanted,
                              List<(string Uri, Xm.ExtensionKind Kind)> reqs, HashSet<Xm.ExtensionKind> kindsSeen)
    {
        var perUriKinds = new HashSet<Xm.ExtensionKind>();
        for (int i = start; i < start + count; i++)
        {
            var row = plan[i];
            perUriKinds.Clear();
            for (int j = 0; j < wanted.Count; j++)
            {
                if ((row.Mask & (1 << j)) == 0) continue;
                var p = wanted[j];
                if (perUriKinds.Add(p.Kind)) { reqs.Add((row.Uri, p.Kind)); kindsSeen.Add(p.Kind); }
                // Companions ride the same uri group in the same POST because the projector cannot decide without them
                // (99 VIDEO_ASSOCIATIONS needs 182 CONSUMPTION_EXPERIENCE). They are never planned or memoized on their
                // own — they are this projector's payload.
                foreach (var companion in p.Companions)
                    if (companion != Xm.ExtensionKind.UnknownExtension && perUriKinds.Add(companion))
                    { reqs.Add((row.Uri, companion)); kindsSeen.Add(companion); }
            }
        }
    }

    /// <summary>"Kind=12,Kind=3" — one field instead of one field per bucket, because the census reads these lines by
    /// eye and a variable field set is what made the old per-service logs impossible to diff.</summary>
    static string Tally<TKey>(Dictionary<TKey, int>? counts) where TKey : notnull
    {
        if (counts is null || counts.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var (key, n) in counts)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(key).Append('=').Append(n);
        }
        return sb.ToString();
    }

    static void TallyTrait(TraitSet trait, ref int playcount, ref int tempo, ref int tags)
    {
        if (trait == TraitSet.PlayCount) playcount++;
        else if (trait == TraitSet.AudioAttributes) tempo++;
        else if (trait == TraitSet.Descriptors) tags++;
    }

    void LogTrackCensus(List<PlanRow> plan, int start, int count, TraitSet traits, TraitSurface surface,
        TrackHydrationCensus.TraitTallies playcount, TrackHydrationCensus.TraitTallies tempo,
        TrackHydrationCensus.TraitTallies tags)
    {
        if (!_log.IsEnabled(WaveeLogLevel.Info)) return;

        Track?[]? rows = null;
        int n = 0;
        for (int i = start; i < start + count; i++)
        {
            if (plan[i].Kind != CoreKind.Track) continue;
            rows ??= new Track?[count];
            rows[n++] = _store.GetTrack(plan[i].Uri);
        }
        if (rows is null || n == 0) return;
        if (n < rows.Length)
        {
            var trimmed = new Track?[n];
            Array.Copy(rows, trimmed, n);
            rows = trimmed;
        }

        var report = TrackHydrationCensus.Scan(rows, traits, playcount, tempo, tags);
        TrackHydrationCensus.LogIfNeeded(_log, report, surface, HydrationLevel.Open);
    }
}
