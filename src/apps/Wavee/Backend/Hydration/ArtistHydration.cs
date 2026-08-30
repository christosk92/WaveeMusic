using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Metadata;
using Wavee.Core;
// EntityKind: the ONE uri vocabulary (Wavee.Core), not the transport's thin Backend.Metadata projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.Backend.Hydration;

// ── The artist ladder (design §2.3) ──────────────────────────────────────────────────────────────────────────────────
// Three transports used to hydrate one artist behind three unrelated entry points: ArtistDiscography.EnsureAsync +
// DiscographyPrefetcher (V4 stubs → cards), SpotifyArtistStatsService (queryArtistOverview, 12h TTL), and
// SpotifyArtistPopularTracksService (spclient chart → TrackV4 → merge → kind-185 counts). Each had its OWN freshness
// gate, its own coalescer and its own "is it cold?" predicate, and they disagreed: the prefetch could leave an artist
// looking hydrated while its chart was still the 10-row seed, and the stats write rewrote TopTracks back to that seed
// under the chart service's feet.
//
// One ladder makes the ORDER explicit, which is what the three-service split kept getting wrong:
//   Open  — the discography is assembled (own stubs upgraded to resident AlbumV4 cards).
//   Rich  — + the overview: stats, the releases column, and the ~10-track seed WITH its play counts.
//   Full  — + the EXTENDED chart (~50) folded onto that seed, then the counts the extension rows lack.
// Because Full runs Rich first, "stats rewrote TopTracks to the seed" is no longer a race — it is a step.
public sealed class ArtistHydration : IKindHydration
{
    readonly IStore _store;
    readonly IEnvelopeFetch _envelopes;
    readonly IArtistChartFetch _chart;
    readonly WaveeLogger _log;

    public ArtistHydration(IStore store, IEnvelopeFetch envelopes, IArtistChartFetch chart, WaveeLogger log = default)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _log = log;
    }

    public EntityKind Kind => EntityKind.Artist;

    public HydrationLevel LevelOf(string uri) => HydrationLevels.Of(_store.GetArtist(uri));

    /// <summary>Nothing to fuse: every artist rung beyond ArtistV4 is a SECOND transport (Pathfinder / spclient), not
    /// another extension kind on the same POST.</summary>
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }

    public async Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                                    HydrationContext ctx, CancellationToken ct)
    {
        if (uris.Count == 0 || level < HydrationLevel.Open) return;   // Identity is step 0 (ArtistV4) and nothing else.

        var sub = new HydrationOptions(HydrationMode.Blocking, opts.Revalidate, TraitSurface.None, opts.Priority, SubAsk: true);

        // ── Open: upgrade the discography stubs, then assemble ───────────────────────────────────────────────────────
        // ArtistV4 carries the whole discography as gid-only stubs; AlbumV4 turns each into a resident card. Batched
        // across every artist in the wave — the prefetch used to fire one SyncAll per artist with a 250 ms breather.
        List<string>? stubs = null;
        for (int i = 0; i < uris.Count; i++)
        {
            if (_store.GetArtist(uris[i].Uri) is not { } artist) continue;
            if (artist.TopAlbums is { Count: > 0 } own)
                for (int s = 0; s < own.Count; s++)
                    if (own[s].Uri.Length > 0 && (own[s].Name.Length == 0 || _store.GetAlbum(own[s].Uri) is null))
                        (stubs ??= new List<string>()).Add(own[s].Uri);
            // Appears-on is Rich-only and capped: the shelf shows a slice, and the full set can be thousands.
            if (level >= HydrationLevel.Rich && artist.AppearsOn is { Count: > 0 } appears)
                for (int s = 0; s < appears.Count && s < ArtistDiscography.AppearsOnHydrateCap; s++)
                    if (appears[s].Uri.Length > 0 && appears[s].Name.Length == 0)
                        (stubs ??= new List<string>()).Add(appears[s].Uri);
        }
        if (stubs is { Count: > 0 })
        {
            try { await ctx.Hydrator.EnsureManyAsync(stubs, HydrationLevel.Identity, sub, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Best-effort: Assemble below still folds whatever cards ARE resident, so a failed batch costs card art,
                // never the page. The rung is what it costs, though — an artist short of Open because THIS threw must
                // not be sealed as "the discography is genuinely this small".
                for (int i = 0; i < uris.Count; i++) ctx.ReportTransient(uris[i].Uri);
                _log.Event(WaveeLogLevel.Warning, "hydration.artist.stubs.fail", "discography stub batch failed", ex: ex,
                    fields: [WaveeLogField.Of("stubs", stubs.Count)]);
            }
        }

        // ── Rich: the overview (the ONE queryArtistOverview caller) ──────────────────────────────────────────────────
        if (level >= HydrationLevel.Rich)
            for (int i = 0; i < uris.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await EnsureOverviewAsync(uris[i].Uri, ctx, ct).ConfigureAwait(false);
            }

        // Assemble LAST: the overview's stats-only write lands between the stub batch and here, and Assemble is what
        // folds the resident cards back onto the (possibly rewritten) artist row.
        for (int i = 0; i < uris.Count; i++) ArtistDiscography.Assemble(_store, uris[i].Uri);

        // ── Full: the extended chart + its play counts ───────────────────────────────────────────────────────────────
        if (level >= HydrationLevel.Full)
            for (int i = 0; i < uris.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await EnsureChartAsync(uris[i].Uri, ctx, sub, ct).ConfigureAwait(false);
            }
    }

    // ── queryArtistOverview → a STATS-ONLY upsert (port of SpotifyArtistStatsService.EnsureStatsAsync) ───────────────
    // The neutralized fields are load-bearing, not defensive noise: the overview carries only the FIRST ~10 releases per
    // facet and MergeAlbumCards treats a non-null incoming list as the authoritative set, so a raw upsert clobbers a
    // full ArtistV4 discography down to that first page ("every artist caps at 10 albums"). Totals go to 0 = unknown
    // for the same reason.
    async Task EnsureOverviewAsync(string artistUri, HydrationContext ctx, CancellationToken ct)
    {
        var current = _store.GetArtist(artistUri);
        // The age gate. Presence (Rich) alone is not enough — an artist whose overview landed a week ago is Rich and
        // stale — and freshness alone is not enough either: records persisted before LatestRelease/PopularReleases
        // existed deserialize with both null while still stamped fresh, and would show no releases column for a whole
        // TTL. Both halves, exactly as ArtistStatsCache.IsFresh had them.
        // The stamp is Artist.OverviewFetchedAt, NOT FetchedAt: FetchedAt is a max-of every writer may raise (the
        // chart step, a V4 upsert that carried one), so gating on it let an unrelated write pass off a week-old
        // overview as fresh. Only this method stamps OverviewFetchedAt, so it answers the question actually being
        // asked — "when did queryArtistOverview last land?".
        if (LevelOf(artistUri) >= HydrationLevel.Rich && current is { }
            && DateTimeOffset.UtcNow - current.OverviewFetchedAt <= ctx.Policy.ArtistRichTtl)
            return;

        Artist? overview;
        try { overview = await _envelopes.ArtistOverviewAsync(artistUri, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Must be VISIBLE: a silently-swallowed overview failure is indistinguishable from "this artist has no
            // stats", and it also leaves the chart on its cold seed with no trace of why. The ledger needs the same
            // distinction — "we could not ask" seals short, "there are no stats" seals long.
            ctx.ReportTransient(artistUri);
            _log.Event(WaveeLogLevel.Warning, "hydration.artist.overview.fail", "queryArtistOverview failed", artistUri, ex: ex);
            return;
        }
        if (overview is not { Uri.Length: > 0 }) return;

        // The chart is the SAME clobber shape as TopAlbums, one rung up. The overview carries only the ~10-track seed
        // and StoreEntityMerge takes a non-empty incoming TopTracks as authoritative, so this write collapses a resident
        // EXTENDED chart (~50) back to the seed. Within one pass that is fine — Full runs Rich first, so the chart step
        // follows — but Rich and Full are two ledger keys and therefore two passes: `ArtistPage` asks Rich while its
        // `ArtistPopular` child asks Full, and the Rich pass's overview write landing after the Full pass's chart write
        // is exactly the "stats rewrote TopTracks under the chart's feet" bug this ladder exists to end. Folding the
        // resident chart back in keeps the FRESH seed's order and its play counts at the head while the extended tail
        // survives — the same Merge the chart step itself performs, and a no-op when nothing is extended.
        var residentChart = _store.GetArtist(artistUri)?.TopTracks;
        var seed = overview.TopTracks;
        var topTracks = residentChart is { Count: > 0 } && residentChart.Count > (seed?.Count ?? 0)
            ? ArtistPopularTracks.Merge(seed, residentChart)
            : seed;

        _store.UpsertArtist(overview with
        {
            TopAlbums = null, AppearsOn = null,
            TopTracks = topTracks,
            AlbumsTotal = 0, SinglesTotal = 0, CompilationsTotal = 0,
            // BOTH stamps: FetchedAt stays the max-of "something touched this artist" clock the persistence layer and
            // every other SWR reader already use, OverviewFetchedAt is this transport's own clock (the Rich age gate
            // above and StoreEntityMerge's authoritative-absence discriminator).
            FetchedAt = DateTimeOffset.UtcNow,
            OverviewFetchedAt = DateTimeOffset.UtcNow,
        });

        // Hydrate the shared track rows from the overview too: real titles/artists/durations/covers plus the album's
        // IDENTITY for rows a thin cluster or library write may only know as a gid. NOT the album NAME — the overview's
        // topTracks `albumOfTrack` has no name field, so the mapper writes a name-less AlbumRef and this write is honest
        // about that. The CHART's play counts do not depend on this: they persist with the chart itself, precisely so no
        // other writer of these rows can drop them.
        int withCounts = 0;
        if (overview.TopTracks is { Count: > 0 } landed)
            for (int i = 0; i < landed.Count; i++)
            {
                if (landed[i].Uri.Length == 0) continue;
                _store.UpsertTrack(landed[i]);
                if (landed[i].PlayCount > 0) withCounts++;
            }

        _log.Event(WaveeLogLevel.Info, "hydration.artist.overview", "artist overview landed", artistUri,
            fields: [WaveeLogField.Of("topTracks", overview.TopTracks?.Count ?? 0), WaveeLogField.Of("withPlayCounts", withCounts)]);
    }

    // ── The extended "Popular" chart (port of SpotifyArtistPopularTracksService.LoadAsync/WithPlayCountsAsync) ───────
    // Step two: one authed spclient GET returns the FULL popular list as bare uris (~50, count-free). They hydrate over
    // the shared façade (TrackV4), then fold onto the overview seed — the seed keeps the head AND its play counts,
    // extension-only tracks append. Step three: the counts the extension rows lack come from kind 185, asked through
    // the ONE trait door and read back off the rows the pipeline wrote.
    async Task EnsureChartAsync(string artistUri, HydrationContext ctx, HydrationOptions sub, CancellationToken ct)
    {
        var artist = _store.GetArtist(artistUri);
        // Already fetched AND fresh: no GET, no hydrate. A chart persisted before step three existed (fetched, fresh,
        // count-less) still falls through to the top-up below — that is why this is not a plain early return.
        //
        // The gate is Artist.ChartFetchedAt, NOT the Full RUNG. Presence cannot express "the chart step ran":
        // HydrationLevels.Of(Artist) only calls an artist Full when TopTracks.Count > OverviewSeedCap, so an artist
        // whose real chart is shorter than the overview seed — a niche/new artist, of which a library has many — could
        // never reach Full, this gate could never be true, and the spclient GET re-fired on every ask past the
        // exhausted seal (ExhaustedPlayableTtl = 10 min), forever, to re-learn the same six rows. A chart that
        // legitimately has ≤ the seed cap of rows has been REACHED once it has been fetched; the stamp is that fact.
        // It is also the CHART's own clock rather than the overview's, so an overview refresh cannot make a stale chart
        // look fresh (or vice versa) — the same split OverviewFetchedAt made against FetchedAt.
        bool freshExtended = artist is { } && artist.ChartFetchedAt != default
                             && DateTimeOffset.UtcNow - artist.ChartFetchedAt <= ctx.Policy.ArtistRichTtl;

        IReadOnlyList<Track> chart;
        if (freshExtended)
        {
            chart = artist!.TopTracks ?? Array.Empty<Track>();
            if (ArtistPopularTracks.UrisWithoutPlayCount(chart).Count == 0) return;   // fully served from the store
        }
        else
        {
            IReadOnlyList<string> chartUris;
            try { chartUris = await _chart.TopTrackUrisAsync(artistUri, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Never blanks a painted chart: the overview seed stands. An EMPTY list below is the genuine answer
                // ("this artist has no extended chart"); a throw is not, so only the throw reports.
                ctx.ReportTransient(artistUri);
                _log.Event(WaveeLogLevel.Warning, "hydration.artist.chart.fail", "artist-top-tracks-extensions failed",
                    artistUri, ex: ex);
                return;
            }
            // The transport ANSWERED — stamp the chart clock before anything else can early-return. An empty list
            // ("this artist has no extended chart") and a six-row list are both answers, and both have to count as
            // fetched or the gate above can never close for the artists that produce them.
            StampChartFetched(artistUri);
            if (chartUris.Count == 0) return;

            try { await ctx.Hydrator.EnsureManyAsync(chartUris, HydrationLevel.Identity, sub, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ctx.ReportTransient(artistUri);
                _log.Event(WaveeLogLevel.Warning, "hydration.artist.chart.hydrate", "chart uri batch failed", artistUri, ex: ex);
            }

            var resolved = new List<Track>(chartUris.Count);
            for (int i = 0; i < chartUris.Count; i++)
                if (_store.GetTrack(chartUris[i]) is { } row) resolved.Add(row);

            // Re-read: the overview above (or a concurrent open) may have landed a richer, count-bearing seed.
            var head = _store.GetArtist(artistUri)?.TopTracks is { Count: > 0 } live ? live : Array.Empty<Track>();
            chart = ArtistPopularTracks.Merge(head, resolved);
            _log.Event(WaveeLogLevel.Info, "hydration.artist.chart", "extended chart merged", artistUri,
                fields:
                [
                    WaveeLogField.Of("overview", head.Count), WaveeLogField.Of("extension", resolved.Count),
                    WaveeLogField.Of("merged", chart.Count),
                ]);
            WriteChart(artistUri, chart);
        }

        // Step three. Ask for the WHOLE chart, not just the count-less rows: the row surface wants RowBundle for all of
        // them, and the pipeline's per-uri "already has it" marks suppress the rest.
        var traitUris = new List<string>(chart.Count);
        for (int i = 0; i < chart.Count; i++)
            if (chart[i].Uri.Length > 0) traitUris.Add(chart[i].Uri);
        if (traitUris.Count == 0) return;
        try
        {
            await ctx.Traits.EnsureAsync(traitUris, TraitSet.RowBundle | TraitSet.PlayCount, TraitSurface.ArtistPopular, ct)
                     .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.ReportTransient(artistUri);
            _log.Event(WaveeLogLevel.Warning, "hydration.artist.chart.traits", "chart trait pass failed", artistUri, ex: ex);
            return;   // the merged chart stands with the counts it already had
        }

        // The pipeline writes kind 185 onto the shared ROWS; the chart is a projection, so read them back off the rows.
        Dictionary<string, long>? counts = null;
        for (int i = 0; i < chart.Count; i++)
        {
            if (chart[i].PlayCount > 0 || chart[i].Uri.Length == 0) continue;
            if (_store.GetTrack(chart[i].Uri) is { PlayCount: > 0 } row)
                (counts ??= new Dictionary<string, long>(StringComparer.Ordinal))[chart[i].Uri] = row.PlayCount;
        }
        if (counts is null) return;

        var counted = ArtistPopularTracks.WithPlayCounts(chart, counts);
        if (ReferenceEquals(counted, chart)) return;
        WriteChart(artistUri, counted);
        _log.Event(WaveeLogLevel.Info, "hydration.artist.chart.plays", "play counts applied to the chart", artistUri,
            fields: [WaveeLogField.Of("applied", counts.Count)]);
    }

    /// <summary>Stamp "the chart transport answered for this artist, just now" — and nothing else. Its own clock, so it
    /// cannot be raised by an overview/V4/merge write and cannot raise theirs (StoreEntityMerge keeps the max of each
    /// stamp independently).</summary>
    void StampChartFetched(string artistUri)
    {
        if (_store.GetArtist(artistUri) is not { } artist) return;
        _store.UpsertArtist(artist with { ChartFetchedAt = DateTimeOffset.UtcNow });
    }

    /// <summary>TopTracks ONLY, and only when the list grew or gained counts — no stamp is touched, so writing the
    /// chart can never make a stale overview look fresh.</summary>
    void WriteChart(string artistUri, IReadOnlyList<Track> chart)
    {
        if (_store.GetArtist(artistUri) is not { } artist) return;
        if (ReferenceEquals(artist.TopTracks, chart)) return;
        if (chart.Count < (artist.TopTracks?.Count ?? 0)) return;
        _store.UpsertArtist(artist with { TopTracks = chart });
    }
}
