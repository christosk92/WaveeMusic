using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// ── The signed-in user's own top artists (userTopContent) ────────────────────────────────────────────────────────────
// Affinity over a 4-week window, feeding Home's top-artist row. Two caches, deliberately layered rather than merged:
//   • the TRANSPORT cache (PathfinderResource.UseQueryAsync + TtlFor = 30 min) answers a re-navigation to Home without a
//     request, and revalidates in the background — the row is warm for the whole session after the first open;
//   * the service is RETURN-ONLY: it writes nothing into the store. The card renders from this document, and clicking
//     through opens via the facade, whose artist ladder hydrates the row properly (hydration-facade-plan.md 1.6).
// The ranking itself is never PERSISTED — this is a ME query whose answer is an ordering, not a record — so a cold start
// simply asks again. What the service does hold is the in-memory snapshot below, on two windows: the full affinity TTL
// for a real answer, and a short negative TTL for a degraded one (see FailureTtl).
sealed class SpotifyUserTopService(PathfinderResource pf, WaveeLogger log = default,
    Func<DateTimeOffset>? clock = null) : IUserTopService
{
    const int TopArtists = 10;   // the row shows a ranked strip, not a directory; 10 is what the desktop client asks for
    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    /// <summary>The NEGATIVE cache window. A failed fetch used to be stamped with the full 30 minutes, so one transient
    /// hiccup hid Home's top-artist row for half an hour — every re-navigation served the cached emptiness without ever
    /// re-asking. A degraded answer is worth about a minute, not a window sized for a 4-week affinity ranking.</summary>
    static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(60);
    readonly object _gate = new();
    Snapshot? _cached;
    DateTimeOffset _expires;
    Task<Snapshot>? _inflight;

    DateTimeOffset Now() => clock is { } c ? c() : DateTimeOffset.UtcNow;

    /// <summary><paramref name="Ok"/> separates the two answers an empty snapshot can mean. TRUE: the server answered
    /// and this account genuinely has no affinity yet (a brand-new account) — a real answer, cached for the full TTL.
    /// FALSE: the fetch threw, or the transport returned no document at all — nothing was learned, so it holds only for
    /// <see cref="FailureTtl"/>. Without the flag the two were byte-identical and the failure won the long window.</summary>
    sealed record Snapshot(IReadOnlyList<RelatedArtist> Artists, IReadOnlyList<Track> Tracks, bool Ok)
    {
        public static readonly Snapshot Failed = new(Array.Empty<RelatedArtist>(), Array.Empty<Track>(), Ok: false);
    }

    public async Task<IReadOnlyList<RelatedArtist>> GetTopArtistsAsync(CancellationToken ct = default)
        => (await GetSnapshotAsync(ct).ConfigureAwait(false)).Artists;

    public async Task<IReadOnlyList<Track>> GetTopTracksAsync(CancellationToken ct = default)
        => (await GetSnapshotAsync(ct).ConfigureAwait(false)).Tracks;

    async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        Task<Snapshot> pending;
        lock (_gate)
        {
            if (_cached is { } cached && Now() < _expires) return cached;
            pending = _inflight ??= LoadAsync(CancellationToken.None);
        }

        var result = await pending.WaitAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(_inflight, pending))
            {
                _cached = result;
                // A real answer holds the full window; a degraded one expires in a minute so the row recovers on the
                // next Home open instead of staying blank until the affinity TTL runs out.
                _expires = Now() + (result.Ok ? CacheTtl : FailureTtl);
                _inflight = null;
            }
        }
        return result;
    }

    async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await pf.UseQueryAsync(PathfinderOps.UserTopContent, PathfinderOps.UserTopContentHash,
                w =>
                {
                    // Wire-exact: BOTH facets are switched on and BOTH inputs are sent. One persisted document hosts
                    // top artists and top tracks, and omitting an input it declares is rejected outright. Both halves
                    // are mapped from this one response. SHORT_TERM is the 4-week window the row means
                    // by "your top artists"; a lifetime ranking would never visibly change.
                    w.WriteBoolean("includeTopArtists", true);
                    w.WriteStartObject("topArtistsInput");
                    w.WriteNumber("offset", 0);
                    w.WriteNumber("limit", TopArtists);
                    w.WriteString("sortBy", "AFFINITY");
                    w.WriteString("timeRange", "SHORT_TERM");
                    w.WriteEndObject();
                    w.WriteBoolean("includeTopTracks", true);
                    w.WriteStartObject("topTracksInput");
                    w.WriteNumber("offset", 0);
                    w.WriteNumber("limit", TopArtists);
                    w.WriteString("sortBy", "AFFINITY");
                    w.WriteString("timeRange", "SHORT_TERM");
                    w.WriteEndObject();
                }, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
            // No document at all — a non-2xx or a transport error the client already swallowed into null. That is a
            // FAILURE, not "this account has no top artists": it takes the short negative TTL.
            if (doc is null)
            {
                log.Event(WaveeLogLevel.Warning, "usertop.empty", "userTopContent returned no document");
                return Snapshot.Failed;
            }

            var artists = SpotifyExportMapper.TopArtistsFromUserTop(doc.RootElement);
            var tracks = SpotifyExportMapper.TopTracksFromUserTop(doc.RootElement);
            // RETURN-ONLY (design 3): the thin UpsertArtist/UpsertTrack pass that used to run here is gone. A Home
            // card carries its own name and avatar from THIS document, and clicking it opens through the facade,
            // whose ladder hydrates the real row. Seeding thin entities only taught the store rows that no rung
            // owned - and every presence gate (HydrationLevels.Of) then had to distrust them.
            // Say so on SUCCESS too, not only on failure: "did the top-artist row get data, or does this account have no
            // affinity yet?" is otherwise unanswerable from a log, and an empty row looks identical either way.
            log.Event(WaveeLogLevel.Info, "usertop.ok", "top artists landed", fields:
                [ WaveeLogField.Of("artists", artists.Count), WaveeLogField.Of("tracks", tracks.Count) ]);
            return new Snapshot(artists, tracks, Ok: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best-effort: the row hides. But it must be VISIBLE in the log — a swallowed failure here is
            // indistinguishable from a brand-new account, which is exactly the wrong diagnosis to reach silently.
            log.Event(WaveeLogLevel.Warning, "usertop.fail", "userTopContent failed", ex: ex);
            return Snapshot.Failed;
        }
    }
}
