using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Shared, cached "From Liked Songs" grouping. The Albums and Artists library
/// tabs both derive virtual collections from the same set of liked songs; before
/// this, each tab re-fetched all liked songs and re-ran the grouper on every
/// switch into the From-Liked-Songs source AND on every like/unlike. This caches
/// the liked-songs snapshot + the grouped results in one singleton, fetching and
/// grouping once and reusing across both tabs, and rebuilding only when the
/// underlying save-state actually changes.
/// </summary>
public interface ILikedSongsGroupingCache
{
    Task<IReadOnlyList<LikedAlbumDto>> GetAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LikedArtistDto>> GetArtistsAsync(CancellationToken ct = default);

    /// <summary>Raised when the underlying liked / saved state changed; consumers re-pull and diff.</summary>
    event Action? Changed;
}

public sealed class LikedSongsGroupingCache : ILikedSongsGroupingCache, IDisposable
{
    private readonly ILibraryDataService _data;
    private readonly ITrackLikeService _likeService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<LikedSongDto>? _liked;
    private IReadOnlyList<LibraryArtistDto>? _followed;
    private IReadOnlyList<LikedAlbumDto>? _albums;
    private IReadOnlyList<LikedArtistDto>? _artists;
    private volatile bool _dirty = true;

    public event Action? Changed;

    public LikedSongsGroupingCache(ILibraryDataService data, ITrackLikeService likeService)
    {
        _data = data;
        _likeService = likeService;
        _likeService.SaveStateChanged += OnSaveStateChanged;
    }

    private void OnSaveStateChanged()
    {
        _dirty = true;
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<LikedAlbumDto>> GetAlbumsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLikedLockedAsync(ct).ConfigureAwait(false);
            return _albums ??= LikedSongsByAlbumGrouper.Group(_liked!, SavedAlbumIdSet());
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<LikedArtistDto>> GetArtistsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLikedLockedAsync(ct).ConfigureAwait(false);
            _followed ??= await _data.GetArtistsAsync(ct).ConfigureAwait(false);
            return _artists ??= LikedSongsByArtistGrouper.Group(_liked!, _followed);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLikedLockedAsync(CancellationToken ct)
    {
        if (!_dirty && _liked != null) return;
        _liked = await _data.GetLikedSongsAsync(ct).ConfigureAwait(false);
        _albums = null;
        _artists = null;
        _followed = null;
        _dirty = false;
    }

    private IReadOnlySet<string> SavedAlbumIdSet()
        => _likeService.GetSavedIds(SavedItemType.Album)
            .Select(id => $"spotify:album:{id}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Dispose() => _likeService.SaveStateChanged -= OnSaveStateChanged;
}
