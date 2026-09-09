using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend;

/// <summary>Imports inline context observations before a controller stores URI-only occurrences. No metadata request.</summary>
internal sealed class PlaybackContextCatalogIngress(IContextResolver source, NowPlayingProjection projection) : IContextResolver
{
    async Task<ResolvedContext> Observe(Task<ResolvedContext> pending, CancellationToken ct)
    {
        var result = await pending.ConfigureAwait(false);
        await projection.ObserveTracksAsync(result.Tracks.Select(row => row.Track).ToArray(), ct).ConfigureAwait(false);
        return result;
    }
    public Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default)
        => Observe(source.ResolveAsync(spec, ct), ct);
    public Task<ResolvedContext> ResolveAutoplayAsync(string uri, IReadOnlyList<string> recent, CancellationToken ct = default)
        => Observe(source.ResolveAutoplayAsync(uri, recent, ct), ct);
    public Task<ResolvedContext> ResolveAutopodcastAsync(string uri, IReadOnlyList<string> recent, CancellationToken ct = default)
        => Observe(source.ResolveAutopodcastAsync(uri, recent, ct), ct);
    public Task<string?> ResolveRadioSeedAsync(string uri, CancellationToken ct = default) => source.ResolveRadioSeedAsync(uri, ct);
    public async Task<ContextPage> LoadMoreAsync(string url, CancellationToken ct = default)
    {
        var result = await source.LoadMoreAsync(url, ct).ConfigureAwait(false);
        await projection.ObserveTracksAsync(result.Tracks.Select(row => row.Track).ToArray(), ct).ConfigureAwait(false);
        return result;
    }
    public async Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default)
    {
        var rows = await source.HydrateAsync(refs, ct).ConfigureAwait(false);
        await projection.ObserveTracksAsync(rows.Select(row => row.Track).ToArray(), ct).ConfigureAwait(false);
        return rows;
    }
}
