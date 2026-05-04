using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Playlists;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services;

internal sealed class UserScopeGuard : IUserScopeGuard
{
    private readonly IMetadataDatabase _database;
    private readonly IPlaylistCacheService _playlistCache;
    private readonly ITrackLikeService _trackLikeService;
    private readonly IProfileCache _profileCache;
    private readonly ILogger<UserScopeGuard>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UserScopeGuard(
        IMetadataDatabase database,
        IPlaylistCacheService playlistCache,
        ITrackLikeService trackLikeService,
        IProfileCache profileCache,
        ILogger<UserScopeGuard>? logger = null)
    {
        _database = database;
        _playlistCache = playlistCache;
        _trackLikeService = trackLikeService;
        _profileCache = profileCache;
        _logger = logger;
    }

    public async Task EnsureScopeAsync(string spotifyUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spotifyUserId)) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var markerPath = GetMarkerPath();
            var previous = TryReadMarker(markerPath);

            if (string.Equals(previous, spotifyUserId, StringComparison.Ordinal))
            {
                _logger?.LogDebug("User-scope unchanged ('{User}') — keeping cache", spotifyUserId);
                return;
            }

            _logger?.LogWarning(
                "User changed (was='{Previous}', now='{New}') — wiping local user-bound caches",
                previous ?? "<none>", spotifyUserId);

            // 1. Drop in-memory tiers first so concurrent reads can't repopulate the
            //    SQLite rows we're about to delete.
            _trackLikeService.ClearCache();
            await _playlistCache.ClearAllAsync(ct).ConfigureAwait(false);
            _profileCache.Clear();

            // 2. Wipe every user-bound SQLite table in a single transaction.
            await _database.WipeAllUserDataAsync(ct).ConfigureAwait(false);

            // 3. Persist the new marker last — if we crash mid-wipe, the next sign-in
            //    sees no marker and re-runs the wipe (idempotent against an empty DB).
            WriteMarker(markerPath, spotifyUserId);

            _logger?.LogInformation("User-scope wipe complete for '{User}'", spotifyUserId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetMarkerPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Wavee");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "last_spotify_user.txt");
    }

    private string? TryReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var contents = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(contents) ? null : contents;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read user-scope marker — treating as unset");
            return null;
        }
    }

    private void WriteMarker(string path, string userId)
    {
        try
        {
            File.WriteAllText(path, userId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist user-scope marker for '{User}'", userId);
        }
    }
}
