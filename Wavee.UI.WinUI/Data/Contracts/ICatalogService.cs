using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Service for browsing catalog content (albums, artists, etc.).
/// Separate from ILibraryDataService which handles user's saved library data.
/// </summary>
public interface ICatalogService
{
    /// <summary>
    /// Gets detailed information about a specific album.
    /// </summary>
    Task<AlbumDetailDto> GetAlbumAsync(string albumId, CancellationToken ct = default);

    /// <summary>
    /// Gets tracks for a specific album.
    /// </summary>
    Task<IReadOnlyList<AlbumTrackDto>> GetAlbumTracksAsync(string albumId, CancellationToken ct = default);
}
