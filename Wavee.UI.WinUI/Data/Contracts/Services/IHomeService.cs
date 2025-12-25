using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Models.Home;

namespace Wavee.UI.WinUI.Data.Contracts.Services;

/// <summary>
/// Service for fetching home feed and browse content.
/// </summary>
public interface IHomeService
{
    /// <summary>
    /// Gets the home feed sections (personalized content, recently played, etc.).
    /// </summary>
    Task<IReadOnlyList<HomeSectionModel>> GetHomeSectionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets featured playlists for the home page.
    /// </summary>
    Task<IReadOnlyList<HomeSectionItemModel>> GetFeaturedPlaylistsAsync(
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recently played items.
    /// </summary>
    Task<IReadOnlyList<HomeSectionItemModel>> GetRecentlyPlayedAsync(
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets personalized "Made for You" content.
    /// </summary>
    Task<IReadOnlyList<HomeSectionItemModel>> GetMadeForYouAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets new releases.
    /// </summary>
    Task<IReadOnlyList<HomeSectionItemModel>> GetNewReleasesAsync(
        int limit = 20,
        CancellationToken cancellationToken = default);
}
