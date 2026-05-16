using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Services.AddToPlaylist;

/// <summary>
/// Wire-side handoff used by <see cref="AddToPlaylistSession.SubmitAsync"/>.
/// Implemented in <c>Wavee.UI.WinUI</c> by an adapter over
/// <c>ILibraryDataService.AddTracksToPlaylistAsync</c>; defined here so the
/// session lives in this framework-neutral project and is testable from
/// <c>Wavee.UI.Tests</c> with a fake.
/// </summary>
public interface IAddToPlaylistSubmitter
{
    Task SubmitAsync(string playlistId, IReadOnlyList<string> trackUris, CancellationToken ct = default);
}
