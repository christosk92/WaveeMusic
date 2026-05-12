using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;
using Wavee.Local.Enrichment;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Windows-side implementation of <see cref="ISpotifyTrackSearcher"/> —
/// wraps <see cref="IPathfinderClient.SearchTracksAsync"/> and projects the
/// rich <see cref="SearchResultItem"/> to the slim <see cref="SpotifyTrackMatch"/>
/// shape Wavee.Local consumes.
///
/// <para>Lives in Wavee.UI.WinUI because Wavee.Local can't reference
/// Wavee.dll. Same pattern as <c>MediaFoundationEmbeddedTrackProber</c>.</para>
///
/// <para>When the session isn't ready / authenticated, the underlying
/// Pathfinder call throws — we swallow it and return empty so the enrichment
/// service quietly treats it as a no-match.</para>
/// </summary>
internal sealed class PathfinderSpotifyTrackSearcher : ISpotifyTrackSearcher
{
    private readonly ISession _session;
    private readonly ILogger? _logger;

    public PathfinderSpotifyTrackSearcher(ISession session, ILogger<PathfinderSpotifyTrackSearcher>? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger;
    }

    public async Task<IReadOnlyList<SpotifyTrackMatch>> SearchTracksAsync(
        string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SpotifyTrackMatch>();

        SearchResult result;
        try
        {
            result = await _session.Pathfinder.SearchTracksAsync(query, limit, 0, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[enrich] Spotify search threw for query '{Query}'", query);
            return Array.Empty<SpotifyTrackMatch>();
        }

        // SearchResultItem.ImageUrl is already an HTTPS i.scdn.co URL by the
        // time SearchResult.FromResponse projects the raw TrackData, so we
        // don't need SpotifyImageHelper.ToHttpsUrl here.
        return result.Items
            .Where(i => i.Type == SearchResultType.Track && !string.IsNullOrEmpty(i.Uri))
            .Select(i => new SpotifyTrackMatch(
                TrackUri: i.Uri,
                Title: i.Name ?? string.Empty,
                DurationMs: i.DurationMs,
                AlbumUri: null,    // Not surfaced on SearchResultItem; the raw TrackData.AlbumOfTrack.Uri exists but isn't flattened. Follow-up.
                AlbumName: i.AlbumName,
                CoverImageUri: i.ImageUrl,
                FirstArtistUri: i.ArtistUris?.FirstOrDefault(),
                FirstArtistName: i.ArtistNames?.FirstOrDefault()))
            .ToList();
    }
}
