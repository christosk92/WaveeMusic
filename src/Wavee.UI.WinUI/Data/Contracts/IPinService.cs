using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Read + mutate the user's "Pinned" sidebar section (Spotify's <c>ylpin</c>
/// collection). Carved out of <c>ILibraryDataService</c> in Phase 2; backing
/// store is unchanged (<c>IMetadataDatabase</c> + <c>ISpotifyLibraryService</c>).
/// </summary>
public interface IPinService
{
    /// <summary>
    /// Returns pinned items filtered to the kinds the UI surfaces (playlist /
    /// album / artist / show plus the Liked Songs / Your Episodes pseudo-URIs)
    /// ordered by pinned-at descending. Side-effect: refreshes the cached
    /// pinned-set used by <see cref="IsPinned"/>.
    /// </summary>
    Task<IReadOnlyList<PinnedItemDto>> GetPinnedItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Pins an item (any pinnable Spotify URI). Returns true on success.
    /// Updates the cached pinned-set and publishes <c>ChangeScope.Library</c>.
    /// </summary>
    Task<bool> PinAsync(string uri, CancellationToken ct = default);

    /// <summary>
    /// Removes an item from the Pinned section.
    /// </summary>
    Task<bool> UnpinAsync(string uri, CancellationToken ct = default);

    /// <summary>
    /// Fast in-memory check for whether the given URI is currently pinned.
    /// Backed by the cached pinned-set; refreshed on
    /// <see cref="GetPinnedItemsAsync"/> + after every Pin/Unpin.
    /// </summary>
    bool IsPinned(string uri);
}
