using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Services.Playlists;

/// <summary>
/// Durable, per-playlist persistence for an in-progress refresh session so it survives
/// closing the page or the app. Keyed by playlist id; one saved session per playlist.
/// </summary>
public interface IRefreshSessionStore
{
    /// <summary>Loads the saved session for a playlist, or null if none.</summary>
    Task<RefreshSessionState?> LoadAsync(string playlistId, CancellationToken ct = default);

    /// <summary>Upserts the session. <paramref name="remaining"/> is stored alongside for the cheap entry-button query.</summary>
    Task SaveAsync(RefreshSessionState state, int remaining, CancellationToken ct = default);

    /// <summary>Removes the saved session (on Apply success, Start over, or explicit discard).</summary>
    Task ClearAsync(string playlistId, CancellationToken ct = default);

    /// <summary>Cheap "Resume · N left" lookup for the playlist entry button; null when no session is saved.</summary>
    Task<int?> GetRemainingAsync(string playlistId, CancellationToken ct = default);
}
