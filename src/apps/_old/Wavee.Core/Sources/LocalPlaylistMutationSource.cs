using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>Fake-backend playlist mutations: local <c>wavee:playlist:*</c> add only; Spotify edits fail loud.</summary>
public sealed class LocalPlaylistMutationSource : IPlaylistMutationSource
{
    readonly UserPlaylistSource _local;

    public LocalPlaylistMutationSource(UserPlaylistSource local) => _local = local;

    /// <summary>The local flavour of the create seam: a <c>wavee:playlist:*</c> list exists the moment it is minted, so
    /// there is no outbox to observe and <see cref="PlaylistCreated.Completion"/> is already complete. Folder placement
    /// has no meaning here (local playlists live outside the Spotify rootlist) and is deliberately ignored.</summary>
    public PlaylistCreated CreatePlaylist(string name, RootlistPlacement placement)
        => new(_local.CreatePlaylist(name), Task.CompletedTask);

    public Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        foreach (var t in tracks) _local.AddTrack(playlistUri, t);
        return Task.CompletedTask;
    }

    public Task InsertTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, int toIndex, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        _local.InsertTracks(playlistUri, tracks, toIndex);
        return Task.CompletedTask;
    }

    public Task RemoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        _local.RemoveRows(playlistUri, rows);
        return Task.CompletedTask;
    }

    public Task MoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        _local.MoveRows(playlistUri, rows, toIndex);
        return Task.CompletedTask;
    }

    public Task UpdateDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        throw NotImplementedLocally("metadata editing", playlistUri);
    }

    public Task SetCoverJpegAsync(string playlistUri, byte[] jpeg, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        throw NotImplementedLocally("cover editing", playlistUri);
    }

    public Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default)
        => throw SpotifyOnly(playlistUri);

    public Task DeletePlaylistAsync(string playlistUri, CancellationToken ct = default)
        => throw SpotifyOnly(playlistUri);

    public Task<string> CreateContributorInviteAsync(string playlistUri, CancellationToken ct = default)
        => throw SpotifyOnly(playlistUri);

    // Local playlists are not in the rootlist at all, so there is nothing to move — but a no-op is the HONEST answer
    // here (unlike folder CRUD below): the sidebar's move verbs are shape-checked against this seam offline.
    public Task MoveRootlistItemsAsync(IReadOnlyList<RootlistMove> moves, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MoveRootlistItemAsync(RootlistItemRef source, RootlistItemRef target,
                                      RootlistDropPlacement placement, CancellationToken ct = default)
        => MoveRootlistItemsAsync([new RootlistMove(source, target, placement)], ct);

    // Folder CRUD is a Spotify ROOTLIST operation: local playlists are not in the rootlist at all, so there is no
    // honest local behaviour to fall back to. Named throws (wiring discipline) — never a silent no-op.
    public Task<string> CreateFolderAsync(string name, RootlistPlacement placement, CancellationToken ct = default)
        => throw FoldersAreSpotifyOnly("create");

    public Task RenameFolderAsync(string groupId, string name, CancellationToken ct = default)
        => throw FoldersAreSpotifyOnly("rename");

    public Task DeleteFolderAsync(string groupId, CancellationToken ct = default)
        => throw FoldersAreSpotifyOnly("delete");

    static NotSupportedException FoldersAreSpotifyOnly(string verb) =>
        new($"Rootlist folder {verb} is a Spotify-account operation with no local equivalent. Sign in with the real backend.");

    static void RequireLocal(string uri)
    {
        // Ownership is a provider question, asked through the ONE parser (hydration-facade-design.md §1.1):
        // `wavee:playlist:*` IS EntityProviders.User, the session-local source this type mutates.
        if (EntityUri.Parse(uri).Provider != EntityProviders.User)
            throw SpotifyOnly(uri);
    }

    static NotSupportedException SpotifyOnly(string uri) =>
        new($"Spotify playlist editing is not available offline (uri={uri}). Sign in with the real backend.");

    static NotSupportedException NotImplementedLocally(string what, string uri) =>
        new($"Local playlist {what} is not implemented (uri={uri}).");
}
