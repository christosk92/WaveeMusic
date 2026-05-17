using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Contracts;

/// <summary>
/// Follow / unfollow another Spotify user. The two ViewModel surfaces that
/// need this — ProfileViewModel's hero follow toggle, and any future
/// follower-list grid — route through here instead of touching
/// <c>ISession.SpClient.FollowUserAsync</c> directly. Keeps the protocol-isolation
/// invariant (ViewModels never see SpClient) without forcing every consumer to
/// wire up a full user-management service surface.
/// </summary>
public interface IUserFollowService
{
    /// <summary>
    /// True when the underlying session is available and connected. ViewModels
    /// can disable the toggle button while this is false.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Follow the user. Returns true on a 2xx response, false on session-down
    /// or API failure. Never throws — the toggle button is a no-op when the
    /// service can't be reached.
    /// </summary>
    Task<bool> FollowAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Unfollow the user. Same return contract as <see cref="FollowAsync"/>.
    /// </summary>
    Task<bool> UnfollowAsync(string username, CancellationToken ct = default);
}
