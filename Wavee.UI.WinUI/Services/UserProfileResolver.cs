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
/// Resolves a <c>spotify:user:{id}</c> URI (or a bare user id) to a human-readable
/// display name. The data layer sometimes surfaces the raw URI / bare id as
/// <c>OwnerName</c>; binding it directly to the playlist hero renders "spotify:user:…"
/// instead of a real name. Consumers call this resolver to look up the friendly name.
/// </summary>
public interface IUserProfileResolver
{
    /// <summary>
    /// Gets the display name for a user URI or bare user id. Returns null when
    /// resolution fails — callers should fall back to whatever name they already have.
    /// Results are memoised per-process; repeated calls for the same user hit the cache.
    /// </summary>
    Task<string?> GetDisplayNameAsync(string userUriOrId, CancellationToken ct = default);
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

    // Resolved → non-null display name. Cached misses → null entry (so we don't
    // hammer the backend for ids that really have no display name).
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);

    public UserProfileResolver(
        IExtendedMetadataClient extendedMetadata,
        ISession session,
        ILogger<UserProfileResolver>? logger = null)
    {
        _extendedMetadata = extendedMetadata;
        _session = session;
        _logger = logger;
    }

    public Task<string?> GetDisplayNameAsync(string userUriOrId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userUriOrId)) return Task.FromResult<string?>(null);

        var userUri = Normalize(userUriOrId);
        if (userUri is null) return Task.FromResult<string?>(null);

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

    private async Task<string?> ResolveAsync(string userUri, CancellationToken ct)
    {
        // 1) Extended-metadata path. Piggybacks on the store's batching + SQLite cache.
        try
        {
            var bytes = await _extendedMetadata
                .GetExtensionAsync(userUri, ExtensionKind.UserProfile, ct)
                .ConfigureAwait(false);
            var profile = SpotifyUserProfile.TryParseJson(bytes);
            if (!string.IsNullOrWhiteSpace(profile?.EffectiveDisplayName))
                return profile!.EffectiveDisplayName;
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
            return string.IsNullOrWhiteSpace(profile.EffectiveDisplayName) ? null : profile.EffectiveDisplayName;
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
