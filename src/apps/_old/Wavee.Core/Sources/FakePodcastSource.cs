namespace Wavee.Core;

/// <summary>The in-process Podcasts source (docs/plans/wavee/architecture.md §2, §9): synthesizes shows + episodes (the export has
/// none) and owns the <c>wavee:show:*</c> / <c>wavee:episode:*</c> namespace. Declares only the Podcasts capability, so
/// the aggregate routes podcast reads here via <c>OfCapability(Podcasts)</c> — capability-segregated, like every facet.</summary>
public sealed class FakePodcastSource : IPodcastSource
{
    public string Id => "podcasts";
    // `wavee:show:*` / `wavee:episode:*` are exactly EntityProviders.WaveePodcast (hydration-facade-design.md §1.1).
    public bool Owns(string uri) => EntityUri.Parse(uri).Provider == EntityProviders.WaveePodcast;
    public SourceCapabilities Capabilities => SourceCapabilities.Podcasts;

    public Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default) => Task.FromResult(FakeData.Shows());
    // The synthetic show arrives complete, so TotalEpisodes == PagedThrough == the resident count (nothing left to ask
    // for) and LoadMoreEpisodesAsync (the interface default) returns the cursor unmoved — the episode list then never
    // offers a load-more it could not honour.
    public Task<Show?> GetShowAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
        => Task.FromResult(FakeData.Show(uri) is { } s
            ? s with { TotalEpisodes = s.Episodes?.Count ?? 0, PagedThrough = s.Episodes?.Count ?? 0 }
            : null);
}
