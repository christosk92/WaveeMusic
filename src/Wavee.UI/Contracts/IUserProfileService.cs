using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Contracts;

/// <summary>
/// Loads a Spotify user profile (self or another user) for the profile page +
/// profile cache. Drains <c>ISession</c> out of ProfileViewModel + ProfileCache:
/// both surfaces now consume this interface instead of the raw session.
/// </summary>
/// <remarks>
/// The returned snapshot type lives in <c>Wavee.UI.WinUI.Services</c> for now
/// (it composes a <see cref="object"/>-typed payload) and is exposed through
/// the impl as a generic <see cref="object"/> so the contract stays neutral.
/// The single caller pattern (ProfileViewModel + ProfileCache + DI factory) is
/// well-known, so the boxed cast is contained.
/// </remarks>
public interface IUserProfileService
{
    /// <summary>
    /// True when there is an authenticated, live session backing this service.
    /// Use as the gate before any of the Load* calls.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Username of the authenticated user, or null when no session is available.
    /// Used by ProfileCache to decide what to fetch on background refresh.
    /// </summary>
    string? AuthenticatedUsername { get; }

    /// <summary>
    /// One-shot fetch of <paramref name="usernameOrUri"/>'s profile snapshot.
    /// Returns a boxed <see cref="object"/> that the caller casts back to
    /// <c>ProfileSnapshot</c>; the contract stays in <c>Wavee.UI</c> while the
    /// snapshot type stays in <c>Wavee.UI.WinUI</c> (it carries WinUI-affine
    /// fields like the hero color hex).
    /// </summary>
    Task<object> LoadAsync(string usernameOrUri, CancellationToken ct = default);

    /// <summary>
    /// Convenience: load the authenticated user's snapshot. Throws when
    /// <see cref="IsAvailable"/> is false.
    /// </summary>
    Task<object> LoadAuthenticatedUserAsync(CancellationToken ct = default);
}
