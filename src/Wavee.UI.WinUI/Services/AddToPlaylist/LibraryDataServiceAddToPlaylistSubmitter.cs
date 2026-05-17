using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services.AddToPlaylist;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services.AddToPlaylist;

/// <summary>
/// Adapter that lets <see cref="AddToPlaylistSession"/> (which lives in the
/// framework-neutral <c>Wavee.UI</c> project) submit via the real
/// <see cref="IPlaylistMutationService"/>. The session takes
/// <see cref="IAddToPlaylistSubmitter"/> in its ctor to stay testable from
/// <c>Wavee.UI.Tests</c>; the WinUI host wires this adapter.
/// </summary>
internal sealed class LibraryDataServiceAddToPlaylistSubmitter : IAddToPlaylistSubmitter
{
    private readonly IPlaylistMutationService _playlistMutationService;

    public LibraryDataServiceAddToPlaylistSubmitter(IPlaylistMutationService playlistMutationService)
    {
        _playlistMutationService = playlistMutationService ?? throw new ArgumentNullException(nameof(playlistMutationService));
    }

    public Task SubmitAsync(string playlistId, IReadOnlyList<string> trackUris, CancellationToken ct = default)
        => _playlistMutationService.AddTracksToPlaylistAsync(playlistId, trackUris, ct);
}
