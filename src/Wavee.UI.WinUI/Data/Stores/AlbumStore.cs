using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Data;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Stores;

// Reactive entity store for album detail pages.
//
// IAlbumService.GetDetailAsync goes straight to Pathfinder with no internal
// cache, so this store owns the warm tier directly via
// IHotCache<AlbumDetailResult>. The hot cache survives both EntityStore Slot
// eviction (MaxSlots=64 LRU) AND page hibernate-on-OnNavigatedFrom —
// re-navigation after either reads from the HotCache instead of
// round-tripping to Pathfinder.
//
// No push source today — albums don't get Dealer diffs. TTL handles any
// staleness. Cold (SQLite) tier left as future work for cross-restart
// persistence; the BehaviorSubject + HotCache combination is process-scoped.
public sealed class AlbumStore : EntityStore<string, AlbumDetailResult>
{
    private readonly IAlbumService _albumService;
    private readonly IHotCache<AlbumDetailResult> _hotCache;

    public AlbumStore(
        IAlbumService albumService,
        IHotCache<AlbumDetailResult> hotCache,
        ILogger<AlbumStore>? logger = null)
        : base(StringComparer.Ordinal, logger)
    {
        _albumService = albumService;
        _hotCache = hotCache;
    }

    protected override TimeSpan Ttl { get; } = TimeSpan.FromHours(24);

    protected override ValueTask<AlbumDetailResult?> ReadHotAsync(string key)
        => new(_hotCache.Get(key));

    protected override ValueTask<AlbumDetailResult?> ReadColdAsync(string key, CancellationToken ct)
        => new((AlbumDetailResult?)null);

    protected override Task<AlbumDetailResult> FetchAsync(string key, AlbumDetailResult? previous, CancellationToken ct)
        => _albumService.GetDetailAsync(key, ct);

    protected override void WriteHot(string key, AlbumDetailResult value)
        => _hotCache.Set(key, value);

    protected override Task WriteColdAsync(string key, AlbumDetailResult value, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Seed a partial album payload parsed from an <c>ALBUM_V4</c> extended-
    /// metadata viewport prefetch. Calls <see cref="EntityStore{TKey,TValue}.Hint"/>
    /// which emits <c>Ready+Stale</c>, so the next <c>Observe(albumId)</c> subscriber
    /// (e.g. AlbumPage activation) renders the partial immediately and the
    /// authoritative Pathfinder fetch is scheduled automatically by
    /// <c>MaterializeAsync</c>. No-op if the slot already holds a Ready+Fresh
    /// (full) payload — never downgrades.
    /// </summary>
    public void HintPartial(string albumId, AlbumDetailResult partial)
    {
        if (!partial.IsPartial)
            return; // misuse: only partials should be hinted (full payloads go via Push)
        Hint(albumId, partial);
    }
}
