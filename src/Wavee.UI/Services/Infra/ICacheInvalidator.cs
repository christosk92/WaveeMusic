using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Playlists;

namespace Wavee.UI.Services.Infra;

/// <summary>
/// Centralizes "I just mutated playlist X" → "invalidate cache + publish
/// <see cref="ChangeScope.Playlists"/>" so call sites don't repeat the dance.
/// </summary>
public interface ICacheInvalidator
{
    /// <summary>
    /// Invalidate a single playlist's cache entry and signal that the
    /// playlist tree may have changed shape (so subscribers reload).
    /// </summary>
    Task InvalidatePlaylistAsync(string playlistUri, CancellationToken ct = default);

    /// <summary>
    /// Invalidate the rootlist cache and emit <see cref="ChangeScope.Playlists"/>.
    /// </summary>
    Task InvalidateRootlistAsync(CancellationToken ct = default);

    /// <summary>
    /// Emit <see cref="ChangeScope.Library"/> without touching any cache —
    /// for mutations whose payload is already consistent but whose existence
    /// downstream consumers need to know about (e.g. like-state flips).
    /// </summary>
    void SignalLibraryChanged();
}

/// <summary>
/// Default <see cref="ICacheInvalidator"/>. Composes <see cref="IPlaylistCacheService"/>
/// with <see cref="IChangeBus"/>.
/// </summary>
public sealed class CacheInvalidator : ICacheInvalidator
{
    private readonly IPlaylistCacheService _playlistCache;
    private readonly IChangeBus _changeBus;
    private readonly ILogger<CacheInvalidator>? _logger;

    public CacheInvalidator(
        IPlaylistCacheService playlistCache,
        IChangeBus changeBus,
        ILogger<CacheInvalidator>? logger = null)
    {
        _playlistCache = playlistCache;
        _changeBus = changeBus;
        _logger = logger;
    }

    public async Task InvalidatePlaylistAsync(string playlistUri, CancellationToken ct = default)
    {
        _logger?.LogDebug("CacheInvalidator.InvalidatePlaylist({Uri})", playlistUri);
        await _playlistCache.InvalidateAsync(playlistUri, ct).ConfigureAwait(false);
        _changeBus.Publish(ChangeScope.Playlists);
        _changeBus.Publish(ChangeScope.Library);
    }

    public async Task InvalidateRootlistAsync(CancellationToken ct = default)
    {
        _logger?.LogDebug("CacheInvalidator.InvalidateRootlist");
        await _playlistCache.InvalidateAsync(PlaylistCacheUris.Rootlist, ct).ConfigureAwait(false);
        _changeBus.Publish(ChangeScope.Playlists);
        _changeBus.Publish(ChangeScope.Library);
    }

    public void SignalLibraryChanged()
    {
        _logger?.LogDebug("CacheInvalidator.SignalLibraryChanged");
        _changeBus.Publish(ChangeScope.Library);
    }
}
