using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// The WHAT of an action invocation — pure (Wavee.Core + BCL only), so the selection-semantics / enablement logic over it
// is unit-testable engine-free (Wavee.Tests source-includes this file). The HOW (services) rides separately in
// ActionServices; the pair is ActionContext (Actions/ActionServices.cs).

/// <summary>What a context menu / batch bar is acting on.</summary>
public enum TargetKind : byte { None, Tracks, Album, Artist, Playlist, QueueEntry, SidebarItem, NowPlaying }

/// <summary>The hosting playlist a track-set was right-clicked INSIDE (null elsewhere). Rows are resolved at open time
/// (original playlist indices, in display order) so RemoveFromThisPlaylist has indices + item-ids without re-querying.
/// For a container target (sidebar playlist row) <see cref="Rows"/> is empty and only <see cref="Caps"/> gates.</summary>
public sealed record PlaylistHost(string PlaylistUri, PlaylistCapabilities Caps, IReadOnlyList<PlaylistRowRef> Rows);

/// <summary>The action target: kind + the track set (Tracks/QueueEntry/NowPlaying) or the container uri/name, plus the
/// optional playlist host and queue-entry identity. Built at menu-open / bar-render time, never retained.</summary>
public readonly struct ActionTarget
{
    static readonly IReadOnlyList<Track> NoTracks = Array.Empty<Track>();

    public TargetKind Kind { get; init; }
    /// <summary>The tracks acted on (Tracks / QueueEntry / NowPlaying). Never null — empty for container targets.</summary>
    public IReadOnlyList<Track> Tracks { get; init; }
    /// <summary>Container uri (Album/Artist/Playlist), or Tracks[0].Uri for a track target.</summary>
    public string Uri { get; init; }
    public string Name { get; init; }
    /// <summary>Set only when the target lives inside a playlist (detail rows) or IS a playlist (sidebar row).</summary>
    public PlaylistHost? Host { get; init; }
    /// <summary>QueueEntry only: the session-stable queue row id.</summary>
    public QueueItemId QueueItemId { get; init; }
    /// <summary>QueueEntry only: the panel's remove closure (player call + optimistic display-list update).</summary>
    public Action? RemoveFromDisplay { get; init; }

    public int Count => Tracks?.Count ?? 0;
    public Track? Single => Count == 1 ? Tracks[0] : null;

    public static ActionTarget ForTracks(IReadOnlyList<Track> tracks, PlaylistHost? host = null) => new()
    {
        Kind = TargetKind.Tracks,
        Tracks = tracks ?? NoTracks,
        Uri = tracks is { Count: > 0 } ? tracks[0].Uri : "",
        Name = tracks is { Count: > 0 } ? tracks[0].Title : "",
        Host = host,
    };

    public static ActionTarget ForAlbum(string uri, string name) => new()
    { Kind = TargetKind.Album, Tracks = NoTracks, Uri = uri, Name = name };

    public static ActionTarget ForArtist(string uri, string name) => new()
    { Kind = TargetKind.Artist, Tracks = NoTracks, Uri = uri, Name = name };

    public static ActionTarget ForPlaylist(string uri, string name, PlaylistHost? host = null) => new()
    { Kind = TargetKind.Playlist, Tracks = NoTracks, Uri = uri, Name = name, Host = host };

    public static ActionTarget ForQueueEntry(QueueEntry entry, Action? removeFromDisplay) => new()
    {
        Kind = TargetKind.QueueEntry,
        Tracks = new[] { entry.Track },
        Uri = entry.Track.Uri,
        Name = entry.Track.Title,
        QueueItemId = entry.ItemId,
        RemoveFromDisplay = removeFromDisplay,
    };

    public static ActionTarget ForNowPlaying(Track track) => new()
    { Kind = TargetKind.NowPlaying, Tracks = new[] { track }, Uri = track.Uri, Name = track.Title };
}
