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
/// <see cref="ILibraryDataService"/>. The session takes
/// <see cref="IAddToPlaylistSubmitter"/> in its ctor to stay testable from
/// <c>Wavee.UI.Tests</c>; the WinUI host wires this adapter.
/// </summary>
internal sealed class LibraryDataServiceAddToPlaylistSubmitter : IAddToPlaylistSubmitter
{
    private readonly ILibraryDataService _libraryDataService;

    public LibraryDataServiceAddToPlaylistSubmitter(ILibraryDataService libraryDataService)
    {
        _libraryDataService = libraryDataService ?? throw new ArgumentNullException(nameof(libraryDataService));
    }

    public Task SubmitAsync(string playlistId, IReadOnlyList<string> trackUris, CancellationToken ct = default)
        => _libraryDataService.AddTracksToPlaylistAsync(playlistId, trackUris, ct);
}
