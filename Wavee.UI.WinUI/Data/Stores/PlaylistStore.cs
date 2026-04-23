using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Data;
using Wavee.Core.Playlists;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Stores;

// Reactive entity store for playlist detail DTOs.
//
// Wraps ILibraryDataService.GetPlaylistAsync so callers (ViewModels) can
// subscribe to an observable stream of state per playlist URI instead of
// firing imperative LoadAsync() + manually reacting to DataChanged events.
//
// The underlying PlaylistCacheService already has an in-memory + SQLite
// cache with its own inflight dedup and dealer-driven updates. This store
// adds: (a) subscription lifecycle tied to refcount — unsubscribe cancels
// the CTS so fetches don't leak TaskCanceledException into the log when
// navigation cancels a load mid-flight, and (b) push absorption so that
// when the playlist cache emits Changes (sync complete, dealer diff), any
// subscribed ViewModel gets the invalidation signal through the same
// stream rather than a parallel event wire.
public sealed class PlaylistStore : EntityStore<string, PlaylistDetailDto>
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IDisposable _changeSubscription;
    private bool _disposed;

    public PlaylistStore(
        ILibraryDataService libraryDataService,
        IPlaylistCacheService playlistCache,
        ILogger<PlaylistStore>? logger = null)
        : base(StringComparer.Ordinal, logger)
    {
        _libraryDataService = libraryDataService;

        _changeSubscription = playlistCache.Changes
            .Where(evt => !string.IsNullOrEmpty(evt.Uri))
            .Subscribe(evt => Invalidate(evt.Uri));
    }

    protected override TimeSpan Ttl { get; } = TimeSpan.FromHours(24);

    // Hot/cold are no-ops: the BehaviorSubject inside the base slot serves as
    // the warm cache, and the wrapped PlaylistCacheService already maintains
    // its own SQLite tier — we don't want a second layer here.
    protected override ValueTask<PlaylistDetailDto?> ReadHotAsync(string key)
        => new((PlaylistDetailDto?)null);

    protected override ValueTask<PlaylistDetailDto?> ReadColdAsync(string key, CancellationToken ct)
        => new((PlaylistDetailDto?)null);

    protected override Task<PlaylistDetailDto> FetchAsync(string key, PlaylistDetailDto? previous, CancellationToken ct)
        => _libraryDataService.GetPlaylistAsync(key, ct);

    protected override void WriteHot(string key, PlaylistDetailDto value) { /* no-op */ }

    protected override Task WriteColdAsync(string key, PlaylistDetailDto value, CancellationToken ct)
        => Task.CompletedTask;

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changeSubscription.Dispose();
        base.Dispose();
    }
}
