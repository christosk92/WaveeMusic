using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Playlists;

public interface IPlaylistCacheService
{
    Task<RootlistSnapshot> GetRootlistAsync(bool forceRefresh = false, CancellationToken ct = default);
    Task<RootlistTree> GetRootlistTreeAsync(bool forceRefresh = false, CancellationToken ct = default);
    Task<CachedPlaylist> GetPlaylistAsync(string playlistUri, bool forceRefresh = false, CancellationToken ct = default);
    Task InvalidateAsync(string playlistUri, CancellationToken ct = default);
    IObservable<PlaylistChangeEvent> Changes { get; }
}

public static class PlaylistCacheUris
{
    public const string Rootlist = "rootlist";
}

public sealed record RootlistSnapshot
{
    public byte[] Revision { get; init; } = [];
    public IReadOnlyList<RootlistEntry> Items { get; init; } = Array.Empty<RootlistEntry>();
    public IReadOnlyDictionary<string, RootlistDecoration> Decorations { get; init; }
        = new Dictionary<string, RootlistDecoration>(StringComparer.Ordinal);
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
}

public abstract record RootlistEntry;

public sealed record RootlistPlaylist(string Uri) : RootlistEntry;

public sealed record RootlistFolderStart(string Id, string Name) : RootlistEntry;

public sealed record RootlistFolderEnd(string Id) : RootlistEntry;

public sealed record RootlistDecoration
{
    public byte[] Revision { get; init; } = [];
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? OwnerUsername { get; init; }
    public int Length { get; init; }
    public bool IsPublic { get; init; }
    public bool IsCollaborative { get; init; }
    public int StatusCode { get; init; }
}

public sealed record RootlistTree
{
    public required RootlistNode Root { get; init; }
}

/// <summary>
/// One node in the rootlist tree. <see cref="Children"/> holds folders and playlists
/// in the **server's original arrival order** — preserving interleaving (a folder may
/// appear between two top-level playlists). Earlier versions used separate <c>Folders</c>
/// and <c>PlaylistUris</c> collections, which silently lost that order and caused folders
/// to render at the end of the sidebar regardless of their true position.
/// </summary>
public sealed record RootlistNode
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<RootlistChild> Children { get; init; } = Array.Empty<RootlistChild>();
}

/// <summary>Discriminated union: a child of a <see cref="RootlistNode"/> is either a playlist or a sub-folder.</summary>
public abstract record RootlistChild;
public sealed record RootlistChildPlaylist(string Uri) : RootlistChild;
public sealed record RootlistChildFolder(RootlistNode Folder) : RootlistChild;

public enum CachedPlaylistBasePermission
{
    Viewer,
    Contributor,
    Owner
}

public sealed record CachedPlaylistCapabilities
{
    public bool CanView { get; init; } = true;
    public bool CanEditItems { get; init; }
    public bool CanAdministratePermissions { get; init; }
    public bool CanCancelMembership { get; init; }
    public bool CanAbuseReport { get; init; }

    public static CachedPlaylistCapabilities ViewOnly { get; } = new();
}

public sealed record CachedPlaylistItem
{
    public required string Uri { get; init; }
    public string? AddedBy { get; init; }
    public DateTimeOffset? AddedAt { get; init; }
    public byte[]? ItemId { get; init; }
}

public sealed record CachedPlaylist : ICacheEntry
{
    public required string Uri { get; init; }
    public EntityType EntityType => EntityType.Playlist;
    public byte[] Revision { get; init; } = [];
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    /// <summary>
    /// Wide editorial hero image, populated from the playlist's
    /// <c>header_image_url_desktop</c> format attribute when present. Null for
    /// user-created playlists.
    /// </summary>
    public string? HeaderImageUrl { get; init; }
    public string OwnerUsername { get; init; } = "";
    public int Length { get; init; }
    public bool IsPublic { get; init; }
    public bool IsCollaborative { get; init; }
    public bool DeletedByOwner { get; init; }
    public bool AbuseReportingEnabled { get; init; }
    public bool HasContentsSnapshot { get; init; }
    public CachedPlaylistBasePermission BasePermission { get; init; } = CachedPlaylistBasePermission.Viewer;
    public CachedPlaylistCapabilities Capabilities { get; init; } = CachedPlaylistCapabilities.ViewOnly;
    public IReadOnlyList<CachedPlaylistItem> Items { get; init; } = Array.Empty<CachedPlaylistItem>();
    public IReadOnlyList<string> OrderedTrackUris => Items.Select(static item => item.Uri).ToArray();
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CachedAt => FetchedAt;
    public DateTimeOffset? LastAccessedAt { get; init; }
}

public enum PlaylistChangeKind
{
    Replaced,
    Updated,
    Removed
}

public sealed record PlaylistChangeEvent
{
    public required string Uri { get; init; }
    public PlaylistChangeKind Kind { get; init; }
}
