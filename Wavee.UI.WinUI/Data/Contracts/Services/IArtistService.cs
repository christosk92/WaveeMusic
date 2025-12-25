using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Models.Artist;

namespace Wavee.UI.WinUI.Data.Contracts.Services;

/// <summary>
/// Service for fetching artist data.
/// </summary>
public interface IArtistService
{
    /// <summary>
    /// Gets full artist details.
    /// </summary>
    Task<ArtistDetailsModel?> GetArtistAsync(
        string artistId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets artist's top tracks.
    /// </summary>
    Task<IReadOnlyList<ArtistTopTrackModel>> GetArtistTopTracksAsync(
        string artistId,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets artist's albums with optional filtering.
    /// </summary>
    Task<IReadOnlyList<ArtistAlbumModel>> GetArtistAlbumsAsync(
        string artistId,
        AlbumFilter filter = AlbumFilter.All,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets related artists.
    /// </summary>
    Task<IReadOnlyList<RelatedArtistModel>> GetRelatedArtistsAsync(
        string artistId,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if artist is followed by current user.
    /// </summary>
    Task<bool> IsFollowingAsync(
        string artistId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Follows or unfollows an artist.
    /// </summary>
    Task SetFollowingAsync(
        string artistId,
        bool follow,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter for artist albums.
/// </summary>
public enum AlbumFilter
{
    All,
    Albums,
    Singles,
    Compilations,
    AppearsOn
}
