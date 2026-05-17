using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="ITrackDetailsService"/>. Thin wrapper over the two NPV
/// Pathfinder calls that the track-details panel consumes.
/// </summary>
public sealed class TrackDetailsService : ITrackDetailsService
{
    private readonly IPathfinderClient _pathfinder;

    public TrackDetailsService(IPathfinderClient pathfinder)
    {
        _pathfinder = pathfinder;
    }

    public Task<NpvArtistResponse> GetTrackDetailsAsync(
        string artistUri,
        string trackUri,
        int contributorsLimit = 10,
        int contributorsOffset = 0,
        CancellationToken ct = default)
        => _pathfinder.GetNpvArtistAsync(artistUri, trackUri, contributorsLimit, contributorsOffset, ct);

    public Task<GetEpisodeOrChapterResponse> GetEpisodeDetailsAsync(
        string episodeUri,
        int numberOfChapters = 10,
        CancellationToken ct = default)
        => _pathfinder.GetNpvEpisodeAsync(episodeUri, numberOfChapters, ct);
}
