using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.AI.Artists;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;

namespace Wavee.UI.WinUI.Services.Ai;

public sealed partial class WinUiMusicCatalogSearchProvider : IMusicCatalogSearchProvider
{
    private readonly ISearchService _searchService;

    public WinUiMusicCatalogSearchProvider(ISearchService searchService)
    {
        _searchService = searchService;
    }

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<ArtistSearchFact>> SearchArtistsAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var page = await _searchService.SearchArtistsAsync(query, 0, Math.Max(1, limit), cancellationToken)
            .ConfigureAwait(false);

        return page.Items
            .Where(i => i.Type == SearchResultType.Artist)
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Take(limit)
            .Select(i => new ArtistSearchFact(i.Name, i.Uri, i.ImageUrl))
            .ToList();
    }

    public async Task<IReadOnlyList<ArtistTrackFact>> SearchTracksAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var page = await _searchService.SearchTracksAsync(query, 0, Math.Max(1, limit), cancellationToken)
            .ConfigureAwait(false);

        return page.Items
            .Where(i => i.Type == SearchResultType.Track)
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Where(i => !string.IsNullOrWhiteSpace(i.Uri))
            .Take(limit)
            .Select(i => new ArtistTrackFact(
                i.Name,
                i.Uri,
                i.AlbumName,
                AlbumUri: null,
                i.ImageUrl,
                PlayCount: 0,
                Year: null,
                ArtistNames: i.ArtistNames ?? []))
            .ToList();
    }
}
