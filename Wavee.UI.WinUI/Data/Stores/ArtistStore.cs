using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Data;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Stores;

// Reactive entity store for artist overview pages.
//
// IArtistService.GetOverviewAsync currently has no cache and no inflight
// dedup (see audit in plan step 3). Every navigation to an artist fires a
// fresh Pathfinder query even for the same id. This store adds the missing
// layer: BehaviorSubject-backed per-uri warm cache, inflight dedup so
// concurrent observers share one fetch, and subscription lifecycle that
// cancels the fetch when the last observer unsubscribes (e.g., page
// navigated away mid-load).
//
// No cold tier for step 3 — we don't want to invent a new SQLite schema
// here. The BehaviorSubject holds the last Ready value for the lifetime
// of the store (process lifetime), which gives us warm re-navigation in
// practice. Cold tier can be added later without breaking consumers.
public sealed class ArtistStore : EntityStore<string, ArtistOverviewResult>
{
    private readonly IArtistService _artistService;

    public ArtistStore(
        IArtistService artistService,
        ILogger<ArtistStore>? logger = null)
        : base(StringComparer.Ordinal, logger)
    {
        _artistService = artistService;
    }

    protected override TimeSpan Ttl { get; } = TimeSpan.FromHours(24);

    protected override ValueTask<ArtistOverviewResult?> ReadHotAsync(string key)
        => new((ArtistOverviewResult?)null);

    protected override ValueTask<ArtistOverviewResult?> ReadColdAsync(string key, CancellationToken ct)
        => new((ArtistOverviewResult?)null);

    protected override Task<ArtistOverviewResult> FetchAsync(string key, ArtistOverviewResult? previous, CancellationToken ct)
        => _artistService.GetOverviewAsync(key, ct);

    protected override void WriteHot(string key, ArtistOverviewResult value) { /* no-op */ }

    protected override Task WriteColdAsync(string key, ArtistOverviewResult value, CancellationToken ct)
        => Task.CompletedTask;
}
