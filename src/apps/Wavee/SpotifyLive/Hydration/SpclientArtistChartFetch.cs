using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Hydration;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive.Hydration;

// ── The artist chart's step two, as a port (design §2.2) ─────────────────────────────────────────────────────────────
// One authed SpClient GET returns the artist's FULL popular list as bare uris (~50). That is ALL this does now: the
// hydrate (TrackV4 over the façade), the fold onto the overview seed and the kind-185 top-up moved up into
// ArtistHydration, where the order of the three steps is visible. Lifted verbatim from
// SpotifyArtistPopularTracksService.FetchExtensionUrisAsync, including the ExtendedCap that bounds both the metadata
// batch and the persisted uri list.
//
// Best-effort by contract: a non-2xx or an unparsable body is an EMPTY list plus a structured event — never an
// exception that could blank a painted chart. The ladder treats empty as "the overview seed stands".
sealed class SpclientArtistChartFetch : IArtistChartFetch
{
    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly WaveeLogger _log;

    public SpclientArtistChartFetch(IHttpExchange http, Func<string> baseUrl, WaveeLogger log = default)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _log = log;
    }

    public async Task<IReadOnlyList<string>> TopTrackUrisAsync(string artistUri, CancellationToken ct)
    {
        // Spotify-catalog artists only: a local/synthetic artist uri has no play-context page to extend.
        if (EntityUri.Parse(artistUri) is not { IsSpotify: true, Kind: EntityKind.Artist }) return Array.Empty<string>();

        var sw = Stopwatch.StartNew();
        var url = _baseUrl() + "/artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/" + Uri.EscapeDataString(artistUri);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };

        using var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false);
        if (resp.Status is < 200 or > 299)
        {
            _log.Event(WaveeLogLevel.Warning, "hydration.artist.chart.http", "artist-top-tracks-extensions rejected",
                artistUri, sw.ElapsedMilliseconds, fields: [WaveeLogField.Of("status", resp.Status)]);
            return Array.Empty<string>();
        }

        // The comment above promises "an unparsable body is an EMPTY list plus a structured event". ParseAsync is the
        // one call that can break that promise — a 200 carrying an HTML error page, a truncated body, a stream that
        // died mid-read all throw JsonException/IOException from HERE, straight out through the ladder, where the only
        // handler left is the provider hydrator's catch-all: the whole artist batch is Failed and a painted chart can
        // blank. Parsing under a guard is what makes the contract true rather than aspirational.
        JsonDocument doc;
        try { doc = await JsonDocument.ParseAsync(resp.Body, default, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "hydration.artist.chart.parse", "artist-top-tracks-extensions body unparsable",
                artistUri, sw.ElapsedMilliseconds, ex: ex, fields: [WaveeLogField.Of("status", resp.Status)]);
            return Array.Empty<string>();
        }
        using (doc)
        {
            var uris = new List<string>(ArtistPopularTracks.ExtendedCap);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var track in tracks.EnumerateArray())
                {
                    if (uris.Count >= ArtistPopularTracks.ExtendedCap) break;
                    if (track.ValueKind == JsonValueKind.Object && track.TryGetProperty("uri", out var uri)
                        && uri.GetString() is { Length: > 0 } u)
                        uris.Add(u);
                }
            }
            _log.Event(WaveeLogLevel.Debug, "hydration.artist.chart.http.ok", "artist-top-tracks-extensions fetched",
                artistUri, sw.ElapsedMilliseconds,
                fields: [WaveeLogField.Of("status", resp.Status), WaveeLogField.Of("uriCount", uris.Count)]);
            return uris;
        }
    }
}
