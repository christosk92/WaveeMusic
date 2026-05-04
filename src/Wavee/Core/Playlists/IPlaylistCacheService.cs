using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Playlists;

public interface IPlaylistCacheService
{
    Task<RootlistSnapshot> GetRootlistAsync(bool forceRefresh = false, CancellationToken ct = default);
    Task<RootlistTree> GetRootlistTreeAsync(bool forceRefresh = false, CancellationToken ct = default);
    Task<CachedPlaylist> GetPlaylistAsync(string playlistUri, bool forceRefresh = false, CancellationToken ct = default);
    Task InvalidateAsync(string playlistUri, CancellationToken ct = default);

    /// <summary>
    /// Drops every in-memory cache held by this service: hot playlist cache, hot rootlist,
    /// in-flight refresh tasks, negative cache, dealer-settle window, schema-stale markers.
    /// Caller is responsible for clearing the underlying SQLite tables (spotify_playlists,
    /// rootlist_cache) — usually via <c>IMetadataDatabase.WipeAllUserDataAsync</c>. Used
    /// when the signed-in Spotify user changes.
    /// </summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Apply a freshly-fetched <see cref="Wavee.Protocol.Playlist.SelectedListContent"/>
    /// (e.g. the response from <c>POST /playlist/v2/playlist/{id}/signals</c>) to the
    /// cache. Maps it to a <see cref="CachedPlaylist"/>, merges with the existing
    /// hot/persisted entry via the same path used by the network-fetch flow,
    /// updates SQLite + hot cache, and fires <see cref="Changes"/> so subscribers
    /// (the playlist store) re-emit. Avoids a redundant GET round-trip and the
    /// race between server-side signal processing and a follow-up fetch.
    /// </summary>
    Task<CachedPlaylist> ApplyFreshContentAsync(
        string playlistUri,
        Wavee.Protocol.Playlist.SelectedListContent content,
        CancellationToken ct = default);

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

    /// <summary>
    /// Mirrors the proto <c>can_edit_metadata</c> flag — gates rename / description /
    /// cover / collaborative-toggle. The Spotify HTTP playlist endpoint also returns
    /// per-attribute flags under <c>listAttributeCapabilities</c>, but those don't
    /// reach this cache layer; the UI derives the four per-attribute booleans from
    /// this single flag for now.
    /// </summary>
    public bool CanEditMetadata { get; init; }
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

    /// <summary>
    /// Format attributes returned by the playlist service for this item —
    /// e.g. recommender signals like <c>core:list_uid</c>, <c>item-score</c>,
    /// <c>decision_id</c>, <c>interaction_id</c>. Empty for user-authored
    /// playlists. Forwarded verbatim into <c>ProvidedTrack.metadata</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> FormatAttributes { get; init; } = _emptyAttributes;

    private static readonly IReadOnlyDictionary<string, string> _emptyAttributes
        = new Dictionary<string, string>(0);
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

    /// <summary>
    /// Playlist-level format attributes — editorial-playlist chrome and
    /// recommender context (e.g. <c>format</c>, <c>request_id</c>, <c>tag</c>,
    /// <c>source-loader</c>, <c>image_url</c>, session display names).
    /// Forwarded into <c>PlayerState.context_metadata</c> to reproduce the
    /// rich "Now Playing" context card on remote clients.
    /// </summary>
    public IReadOnlyDictionary<string, string> FormatAttributes { get; init; } = _emptyAttributes;

    /// <summary>
    /// Fully-formed signal identifiers the server advertises for this playlist
    /// — e.g. <c>session_control_display$&lt;group&gt;$&lt;option&gt;</c> entries and
    /// specials like <c>session-control-reset</c>. Pass the exact string back
    /// to <c>/playlist/v2/playlist/{id}/signals</c> on click. Empty for
    /// playlists without session-control chrome.
    /// </summary>
    public IReadOnlyList<string> AvailableSignals { get; init; } = Array.Empty<string>();

    private static readonly IReadOnlyDictionary<string, string> _emptyAttributes
        = new Dictionary<string, string>(0);

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
