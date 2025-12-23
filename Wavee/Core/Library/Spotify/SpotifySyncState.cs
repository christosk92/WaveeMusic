namespace Wavee.Core.Library.Spotify;

/// <summary>
/// Represents the sync state for the Spotify library.
/// </summary>
public sealed record SpotifySyncState
{
    /// <summary>
    /// Sync state for liked songs (tracks).
    /// </summary>
    public CollectionSyncState Tracks { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for saved albums.
    /// </summary>
    public CollectionSyncState Albums { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for followed artists.
    /// </summary>
    public CollectionSyncState Artists { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for playlists.
    /// </summary>
    public CollectionSyncState Playlists { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for subscribed shows (podcasts).
    /// </summary>
    public CollectionSyncState Shows { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for banned tracks.
    /// </summary>
    public CollectionSyncState Bans { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for banned artists.
    /// </summary>
    public CollectionSyncState ArtistBans { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for listen later queue.
    /// </summary>
    public CollectionSyncState ListenLater { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for Your Library pinned items.
    /// </summary>
    public CollectionSyncState YlPins { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Sync state for enhanced tracks.
    /// </summary>
    public CollectionSyncState Enhanced { get; init; } = CollectionSyncState.Empty;

    /// <summary>
    /// Whether any collection has been synced.
    /// </summary>
    public bool HasEverSynced =>
        Tracks.LastSyncAt.HasValue ||
        Albums.LastSyncAt.HasValue ||
        Artists.LastSyncAt.HasValue ||
        Playlists.LastSyncAt.HasValue ||
        Shows.LastSyncAt.HasValue ||
        Bans.LastSyncAt.HasValue ||
        ArtistBans.LastSyncAt.HasValue ||
        ListenLater.LastSyncAt.HasValue ||
        YlPins.LastSyncAt.HasValue ||
        Enhanced.LastSyncAt.HasValue;

    /// <summary>
    /// Gets the oldest sync time across all collections.
    /// </summary>
    public DateTimeOffset? OldestSyncTime
    {
        get
        {
            var times = new[]
            {
                Tracks.LastSyncAt,
                Albums.LastSyncAt,
                Artists.LastSyncAt,
                Playlists.LastSyncAt,
                Shows.LastSyncAt,
                Bans.LastSyncAt,
                ArtistBans.LastSyncAt,
                ListenLater.LastSyncAt,
                YlPins.LastSyncAt,
                Enhanced.LastSyncAt
            }.Where(t => t.HasValue).Select(t => t!.Value).ToList();

            return times.Count > 0 ? times.Min() : null;
        }
    }

    /// <summary>
    /// Empty sync state (never synced).
    /// </summary>
    public static SpotifySyncState Empty { get; } = new();
}

/// <summary>
/// Sync state for a single collection type.
/// </summary>
public sealed record CollectionSyncState
{
    /// <summary>
    /// Revision token for incremental sync (null if never synced or using full sync).
    /// </summary>
    public string? Revision { get; init; }

    /// <summary>
    /// When this collection was last synced (null if never).
    /// </summary>
    public DateTimeOffset? LastSyncAt { get; init; }

    /// <summary>
    /// Number of items in this collection.
    /// </summary>
    public int ItemCount { get; init; }

    /// <summary>
    /// Whether a sync is currently in progress.
    /// </summary>
    public bool IsSyncing { get; init; }

    /// <summary>
    /// Whether this collection needs a full sync (no revision or revision expired).
    /// </summary>
    public bool NeedsFullSync => string.IsNullOrEmpty(Revision);

    /// <summary>
    /// Empty state (never synced).
    /// </summary>
    public static CollectionSyncState Empty { get; } = new();

    /// <summary>
    /// Creates a state indicating sync is complete.
    /// </summary>
    public static CollectionSyncState Synced(string? revision, int itemCount) => new()
    {
        Revision = revision,
        LastSyncAt = DateTimeOffset.UtcNow,
        ItemCount = itemCount,
        IsSyncing = false
    };

    /// <summary>
    /// Creates a state indicating sync is in progress.
    /// </summary>
    public static CollectionSyncState Syncing(string? currentRevision, int currentCount) => new()
    {
        Revision = currentRevision,
        LastSyncAt = null,
        ItemCount = currentCount,
        IsSyncing = true
    };
}

