using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Data;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Stores;

// Reactive entity store for artist overview pages.
//
// IArtistService.GetOverviewAsync has no internal cache, so this store owns
// the warm tier directly via IHotCache<ArtistOverviewResult>. The hot cache
// survives both EntityStore Slot eviction (MaxSlots=64 LRU) AND page
// hibernate-on-OnNavigatedFrom — re-navigation after either reads from the
// HotCache instead of round-tripping to Pathfinder.
//
// No cold (SQLite) tier yet. The BehaviorSubject + HotCache combination is
// process-scoped; data is lost on app restart. A SQLite tier is the next
// step if we want offline / cross-restart persistence.
public sealed class ArtistStore : EntityStore<string, ArtistOverviewResult>
{
    private readonly IArtistService _artistService;
    private readonly IHotCache<ArtistOverviewResult> _hotCache;

    public ArtistStore(
        IArtistService artistService,
        IHotCache<ArtistOverviewResult> hotCache,
        ILogger<ArtistStore>? logger = null)
        : base(StringComparer.Ordinal, logger)
    {
        _artistService = artistService;
        _hotCache = hotCache;
    }

    protected override TimeSpan Ttl { get; } = TimeSpan.FromHours(24);

    protected override ValueTask<ArtistOverviewResult?> ReadHotAsync(string key)
        => new(_hotCache.Get(key));

    protected override ValueTask<ArtistOverviewResult?> ReadColdAsync(string key, CancellationToken ct)
        => new((ArtistOverviewResult?)null);

    protected override Task<ArtistOverviewResult> FetchAsync(string key, ArtistOverviewResult? previous, CancellationToken ct)
        => _artistService.GetOverviewAsync(key, ct);

    protected override void WriteHot(string key, ArtistOverviewResult value)
        => _hotCache.Set(key, value);

    protected override Task WriteColdAsync(string key, ArtistOverviewResult value, CancellationToken ct)
        => Task.CompletedTask;
}
