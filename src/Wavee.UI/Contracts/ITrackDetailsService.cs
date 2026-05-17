using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http.Pathfinder;

namespace Wavee.UI.Contracts;

/// <summary>
/// Backs <c>TrackDetailsViewModel</c>'s NPV ("Now Playing View") fetches.
/// Drains <c>IPathfinderClient</c> out of the VM — the VM speaks this
/// service-level surface only.
///
/// <para>The response types are still Pathfinder-shaped today; Phase 5 (DTO ↔
/// Result unification) lifts them into framework-neutral records, at which
/// point this surface narrows further.</para>
/// </summary>
public interface ITrackDetailsService
{
    /// <summary>
    /// NPV artist + track details — credits, listener count, related entities.
    /// </summary>
    Task<NpvArtistResponse> GetTrackDetailsAsync(
        string artistUri,
        string trackUri,
        int contributorsLimit = 10,
        int contributorsOffset = 0,
        CancellationToken ct = default);

    /// <summary>
    /// NPV podcast episode details — generated chapters, transcript metadata,
    /// show metadata, description.
    /// </summary>
    Task<GetEpisodeOrChapterResponse> GetEpisodeDetailsAsync(
        string episodeUri,
        int numberOfChapters = 10,
        CancellationToken ct = default);
}
