using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Data;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Stores;

// Reactive entity store for album detail pages.
//
// The underlying IAlbumService already has a 3-tier cache (HotCache + SQLite
// + Pathfinder). This store adds the subscription-lifecycle layer on top:
// an AlbumPage subscription cancels mid-fetch on navigation-away, and
// concurrent subscribers (two copies of the same album open in different
// tabs) share one inflight fetch instead of racing to the network through
// parallel GetDetailAsync calls.
//
// No push source today — albums don't get Dealer diffs. TTL handles any
// staleness. Cold tier is no-op because IAlbumService maintains its own.
public sealed class AlbumStore : EntityStore<string, AlbumDetailResult>
{
    private readonly IAlbumService _albumService;

    public AlbumStore(
        IAlbumService albumService,
        ILogger<AlbumStore>? logger = null)
        : base(StringComparer.Ordinal, logger)
    {
        _albumService = albumService;
    }

    protected override TimeSpan Ttl { get; } = TimeSpan.FromHours(24);

    protected override ValueTask<AlbumDetailResult?> ReadHotAsync(string key)
        => new((AlbumDetailResult?)null);

    protected override ValueTask<AlbumDetailResult?> ReadColdAsync(string key, CancellationToken ct)
        => new((AlbumDetailResult?)null);

    protected override Task<AlbumDetailResult> FetchAsync(string key, AlbumDetailResult? previous, CancellationToken ct)
        => _albumService.GetDetailAsync(key, ct);

    protected override void WriteHot(string key, AlbumDetailResult value) { /* no-op */ }

    protected override Task WriteColdAsync(string key, AlbumDetailResult value, CancellationToken ct)
        => Task.CompletedTask;
}
