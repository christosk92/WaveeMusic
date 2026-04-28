using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Detects when the signed-in Spotify user changes between sessions and wipes every
/// local cache that would otherwise leak the previous user's library, playlists, or
/// sync state into the new account. Persists the current user id in a small file
/// alongside <c>settings.json</c> so the marker survives sign-out (credentials are
/// wiped on logout, so we can't reuse those).
/// </summary>
public interface IUserScopeGuard
{
    /// <summary>
    /// Compares the supplied Spotify user id against the persisted "last user" marker.
    /// On a mismatch (or first run), wipes every user-bound cache (in-memory hot tiers
    /// + every SQLite table holding library / playlists / sync state / outbox / rootlist)
    /// and writes the new marker. On a match, returns immediately — same user
    /// re-authenticating after a token expiry stays fast.
    /// </summary>
    Task EnsureScopeAsync(string spotifyUserId, CancellationToken ct = default);
}
