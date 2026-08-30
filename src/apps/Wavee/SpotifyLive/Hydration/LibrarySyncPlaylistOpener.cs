using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Hydration;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;

namespace Wavee.SpotifyLive.Hydration;

// ── IPlaylistOpener over the LibrarySync writer loop (design §2.2) ───────────────────────────────────────────────────
// The playlist plane has exactly one writer — the sync loop — and this is the whole surface the hydration ladder is
// allowed to touch. Nothing here writes membership, a revision or a dirty flag; each method just names the loop
// operation that does.
public sealed class LibrarySyncPlaylistOpener : IPlaylistOpener
{
    readonly LibrarySync _sync;
    readonly PlaylistFetcher _playlists;

    public LibrarySyncPlaylistOpener(LibrarySync sync, PlaylistFetcher playlists)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _playlists = playlists ?? throw new ArgumentNullException(nameof(playlists));
    }

    /// <summary>The blocking first open: LibrarySync's own coalescing (its in-flight map keyed by uri) means several
    /// callers opening the same playlist at once share ONE fetch, and the caller's token cancels only its own await.</summary>
    public Task OpenAsync(string playlistUri, CancellationToken ct) => _sync.OpenPlaylistAsync(playlistUri, ct);

    /// <summary>The SWR path for a playlist that already has a baseline: hand the loop a command and return. The loop's
    /// 5-minute window and dirty set decide whether anything is actually fetched — the ladder does not second-guess it.</summary>
    public void Revalidate(string playlistUri) => _sync.Enqueue(new SyncCommand(SyncKind.OpenPlaylist, playlistUri));

    /// <summary>Header only (name/description/cover/capabilities), no membership — the Identity rung for a rootlist
    /// member the catalogue's 205 cannot serve.</summary>
    public Task HeaderAsync(string playlistUri, CancellationToken ct)
        => _playlists.FetchPlaylistHeaderAsync(playlistUri, ct);
}
