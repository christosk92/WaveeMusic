using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Lightweight summary returned by <see cref="IUserProfileResolver.GetProfileAsync"/>.
/// Both fields are best-effort: <see cref="DisplayName"/> falls back to null when the
/// profile lookup fails, and <see cref="AvatarUrl"/> is null for users who haven't
/// uploaded a profile picture.
/// </summary>
public sealed record UserProfileSummary(string? DisplayName, string? AvatarUrl);

/// <summary>
/// Resolves a <c>spotify:user:{id}</c> URI (or a bare user id) to a human-readable
/// display name and avatar. The data layer sometimes surfaces the raw URI / bare id
/// as <c>OwnerName</c>; binding it directly renders "spotify:user:…" instead of a
/// real name. Consumers call this resolver to look up the friendly name + avatar.
/// </summary>
public interface IUserProfileResolver
{
    /// <summary>
    /// Gets the display name for a user URI or bare user id. Returns null when
    /// resolution fails — callers should fall back to whatever name they already have.
    /// Results are memoised per-process; repeated calls for the same user hit the cache.
    /// </summary>
    Task<string?> GetDisplayNameAsync(string userUriOrId, CancellationToken ct = default);

    /// <summary>
    /// Gets the display name AND avatar URL for a user. Same caching as
    /// <see cref="GetDisplayNameAsync"/>; one fetch serves both fields. Returns
    /// null when the user can't be resolved at all.
    /// </summary>
    Task<UserProfileSummary?> GetProfileAsync(string userUriOrId, CancellationToken ct = default);
}

/// <summary>
/// Default resolver. Tries <see cref="IExtendedMetadataClient"/> first
/// (<c>ExtensionKind.UserProfile</c>) to piggyback on its SQLite + batch cache,
/// and falls back to a direct <see cref="ISpClient.GetUserProfileAsync"/> call
/// if the extension returns nothing or the bytes don't parse as the expected JSON.
/// </summary>
public sealed class UserProfileResolver : IUserProfileResolver
{
    private const string UserUriPrefix = "spotify:user:";

    private readonly IExtendedMetadataClient _extendedMetadata;
    private readonly ISession _session;
    private readonly ILogger<UserProfileResolver>? _logger;

    // Resolved profile summary. Cached misses → null entry (so we don't hammer
    // the backend for ids that really have no public profile).
    private readonly ConcurrentDictionary<string, UserProfileSummary?> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<UserProfileSummary?>> _inflight = new(StringComparer.Ordinal);

    public UserProfileResolver(
        IExtendedMetadataClient extendedMetadata,
        ISession session,
        ILogger<UserProfileResolver>? logger = null)
    {
        _extendedMetadata = extendedMetadata;
        _session = session;
        _logger = logger;
    }

    public async Task<string?> GetDisplayNameAsync(string userUriOrId, CancellationToken ct = default)
        => (await GetProfileAsync(userUriOrId, ct).ConfigureAwait(false))?.DisplayName;

    public Task<UserProfileSummary?> GetProfileAsync(string userUriOrId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userUriOrId)) return Task.FromResult<UserProfileSummary?>(null);

        var userUri = Normalize(userUriOrId);
        if (userUri is null) return Task.FromResult<UserProfileSummary?>(null);

        if (_cache.TryGetValue(userUri, out var cached)) return Task.FromResult(cached);

        // Dedup concurrent callers — one fetch serves all waiters.
        if (_inflight.TryGetValue(userUri, out var existing)) return existing;

        var task = ResolveAsync(userUri, ct);
        _inflight[userUri] = task;
        _ = task.ContinueWith(t =>
        {
            _cache[userUri] = t.IsCompletedSuccessfully ? t.Result : null;
            _inflight.TryRemove(userUri, out _);
        }, TaskScheduler.Default);
        return task;
    }

    private async Task<UserProfileSummary?> ResolveAsync(string userUri, CancellationToken ct)
    {
        // 1) Extended-metadata path. Piggybacks on the store's batching + SQLite cache.
        try
        {
            var bytes = await _extendedMetadata
                .GetExtensionAsync(userUri, ExtensionKind.UserProfile, ct)
                .ConfigureAwait(false);
            var profile = SpotifyUserProfile.TryParseJson(bytes);
            if (profile is not null
                && (!string.IsNullOrWhiteSpace(profile.EffectiveDisplayName)
                    || !string.IsNullOrWhiteSpace(profile.EffectiveImageUrl)))
            {
                return new UserProfileSummary(
                    string.IsNullOrWhiteSpace(profile.EffectiveDisplayName) ? null : profile.EffectiveDisplayName,
                    string.IsNullOrWhiteSpace(profile.EffectiveImageUrl) ? null : profile.EffectiveImageUrl);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "UserProfile extension fetch failed for {Uri}; falling back to SpClient", userUri);
        }

        // 2) Direct spclient fallback. Works even when the extension pipeline doesn't
        //    carry USER_PROFILE for this id (observed for some editorial / legacy accounts).
        var username = userUri[UserUriPrefix.Length..];
        try
        {
            var profile = await _session.SpClient.GetUserProfileAsync(username, ct).ConfigureAwait(false);
            var name = string.IsNullOrWhiteSpace(profile.EffectiveDisplayName) ? null : profile.EffectiveDisplayName;
            var avatar = string.IsNullOrWhiteSpace(profile.EffectiveImageUrl) ? null : profile.EffectiveImageUrl;
            return name is null && avatar is null ? null : new UserProfileSummary(name, avatar);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SpClient profile fetch failed for username {Username}", username);
            return null;
        }
    }

    /// <summary>
    /// Accepts either a full URI (<c>spotify:user:abc</c>) or a bare id (<c>abc</c>) and
    /// returns the canonical URI form, or null if the input is clearly not a user id
    /// (contains whitespace, colons beyond the expected prefix, etc.).
    /// </summary>
    private static string? Normalize(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith(UserUriPrefix, StringComparison.Ordinal))
        {
            var id = trimmed[UserUriPrefix.Length..];
            if (id.Length == 0 || id.Contains(':')) return null;
            return trimmed;
        }

        // Heuristic bare-id: no spaces, no colons. Spotify user ids are URL-safe slugs.
        if (trimmed.Contains(' ') || trimmed.Contains(':')) return null;
        return UserUriPrefix + trimmed;
    }
}
