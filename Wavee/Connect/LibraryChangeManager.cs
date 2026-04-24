using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Protocol;
using Wavee.Protocol.Collection;
using Wavee.Protocol.Playlist;

namespace Wavee.Connect;

/// <summary>
/// Manages real-time library change notifications via Dealer WebSocket.
/// Subscribes to collection update messages and emits change events.
/// </summary>
public sealed class LibraryChangeManager : IAsyncDisposable
{
    private readonly DealerClient _dealerClient;
    private readonly ILogger? _logger;
    private readonly Subject<LibraryChangeEvent> _changes = new();
    private IDisposable? _subscription;
    private bool _disposed;

    /// <summary>
    /// Observable stream of library change events.
    /// Subscribe to receive real-time updates when user's library changes.
    /// </summary>
    public IObservable<LibraryChangeEvent> Changes => _changes.AsObservable();

    /// <summary>
    /// Creates a new LibraryChangeManager.
    /// </summary>
    /// <param name="dealerClient">The dealer client to subscribe to.</param>
    /// <param name="logger">Optional logger.</param>
    public LibraryChangeManager(DealerClient dealerClient, ILogger? logger = null)
    {
        _dealerClient = dealerClient ?? throw new ArgumentNullException(nameof(dealerClient));
        _logger = logger;

        // Subscribe to collection update messages
        // Spotify sends updates via hm://collection/ or hm://playlist/ URIs
        _subscription = _dealerClient.Messages
            .Where(m => m.Uri.StartsWith("hm://collection/", StringComparison.OrdinalIgnoreCase) ||
                       m.Uri.StartsWith("hm://playlist/", StringComparison.OrdinalIgnoreCase) ||
                       m.Uri.Contains("collection-update", StringComparison.OrdinalIgnoreCase))
            .Subscribe(OnLibraryMessage, OnError);

        _logger?.LogInformation("LibraryChangeManager initialized and subscribed to collection updates");
    }

    private void OnLibraryMessage(DealerMessage message)
    {
        try
        {
            _logger?.LogDebug("Received library change: {Uri}", message.Uri);
            if (message.Payload.Length == 0) return;

            // Drop the text/plain JSON preview of collection deltas — it's a
            // duplicate of the binary form on /collection/{user}, and trying to
            // parse JSON text as protobuf produced a stream of "Failed to parse"
            // debug lines per liked-song toggle.
            if (message.Uri.EndsWith("/json", StringComparison.OrdinalIgnoreCase))
                return;

            // Dispatch by URI shape. Each helper catches its own exceptions and
            // returns null on failure so we fall through to the basic-event
            // fallback below — no parser-cascade log noise like the old code.
            LibraryChangeEvent? changeEvent = message.Uri switch
            {
                var u when u.Contains("/rootlist", StringComparison.OrdinalIgnoreCase)
                    => TryParseRootlistModInfo(message),

                var u when u.Contains("/list/liked-songs-artist/", StringComparison.OrdinalIgnoreCase)
                    => BuildLikedSongsArtistEvent(message),

                var u when u.Contains("/playlist/v2/playlist/", StringComparison.OrdinalIgnoreCase)
                    => TryParsePlaylistModInfo(message),

                var u when u.Contains("/collection/collection/", StringComparison.OrdinalIgnoreCase)
                    => TryParsePubSubUpdate(message),

                _ => null,
            };

            // Basic event so downstream consumers can still react to anything we
            // didn't recognize (e.g. invalidate broadly). RawPayload is preserved
            // for debugging.
            changeEvent ??= new LibraryChangeEvent
            {
                Uri = message.Uri,
                Set = DetermineSetFromUri(message.Uri),
                IsRootlist = message.Uri.Contains("rootlist", StringComparison.OrdinalIgnoreCase),
                Items = Array.Empty<LibraryChangeItem>(),
                Timestamp = DateTimeOffset.UtcNow,
                RawPayload = message.Payload,
            };

            _changes.OnNext(changeEvent);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing library change message: {Uri}", message.Uri);
        }
    }

    private LibraryChangeEvent? TryParseRootlistModInfo(DealerMessage message)
    {
        try
        {
            var info = RootlistModificationInfo.Parser.ParseFrom(message.Payload);
            _logger?.LogDebug("Parsed rootlist modification: {Uri}, {OpCount} ops",
                message.Uri, info.Ops.Count);
            return new LibraryChangeEvent
            {
                Uri = message.Uri,
                Set = "playlists",
                IsRootlist = true,
                NewRevision = info.NewRevision?.ToByteArray(),
                Timestamp = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to parse RootlistModificationInfo for {Uri}", message.Uri);
            return null;
        }
    }

    private LibraryChangeEvent? TryParsePlaylistModInfo(DealerMessage message)
    {
        try
        {
            var modInfo = PlaylistModificationInfo.Parser.ParseFrom(message.Payload);
            var playlistUri = modInfo.Uri?.Length > 0
                ? modInfo.Uri.ToStringUtf8()
                : ExtractPlaylistUriFromDealerUri(message.Uri);

            _logger?.LogDebug("Parsed playlist modification: {Uri}, {OpCount} ops",
                playlistUri, modInfo.Ops.Count);

            return new LibraryChangeEvent
            {
                Uri = message.Uri,
                Set = "playlists",
                PlaylistUri = playlistUri,
                NewRevision = modInfo.NewRevision?.ToByteArray(),
                // From-revision + full Op sequence let PlaylistCacheService apply
                // the diff locally with zero /diff round-trip when the cached
                // existing.Revision matches FromRevision (see
                // PlaylistCacheService.TryApplyMercuryOpsAsync).
                FromRevision = modInfo.ParentRevision?.ToByteArray(),
                Ops = modInfo.Ops.ToList(),
                Items = modInfo.Ops
                    .Where(op => op.Kind == Op.Types.Kind.Add && op.Add != null)
                    .SelectMany(op => op.Add.Items)
                    .Select(item => new LibraryChangeItem
                    {
                        ItemUri = item.Uri,
                        AddedAt = item.Attributes?.Timestamp > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(item.Attributes.Timestamp)
                            : null,
                        IsRemoved = false,
                    })
                    .Concat(modInfo.Ops
                        .Where(op => op.Kind == Op.Types.Kind.Rem && op.Rem != null)
                        .SelectMany(op => op.Rem.Items)
                        .Select(item => new LibraryChangeItem
                        {
                            ItemUri = item.Uri,
                            IsRemoved = true,
                        }))
                    .ToList(),
                Timestamp = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to parse PlaylistModificationInfo for {Uri}", message.Uri);
            return null;
        }
    }

    private LibraryChangeEvent? TryParsePubSubUpdate(DealerMessage message)
    {
        try
        {
            var update = PubSubUpdate.Parser.ParseFrom(message.Payload);
            return new LibraryChangeEvent
            {
                Uri = message.Uri,
                Set = string.IsNullOrEmpty(update.Set)
                    ? DetermineSetFromUri(message.Uri)
                    : update.Set,
                Username = update.Username,
                Items = update.Items.Select(i => new LibraryChangeItem
                {
                    ItemUri = i.Uri,
                    AddedAt = i.AddedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(i.AddedAt) : null,
                    IsRemoved = i.IsRemoved,
                }).ToList(),
                Timestamp = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to parse PubSubUpdate for {Uri}", message.Uri);
            return null;
        }
    }

    /// <summary>
    /// `hm://playlist/v2/list/liked-songs-artist/{artistId}` — Spotify pings
    /// us when the user's liked-songs view filtered by a particular artist
    /// changes. There's no protobuf payload to parse; the message is itself
    /// the signal. Emit a typed event so future consumers (an artist-page
    /// "your liked songs by X" pane, etc.) can subscribe.
    /// </summary>
    private static LibraryChangeEvent BuildLikedSongsArtistEvent(DealerMessage message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            message.Uri, @"liked-songs-artist/([a-zA-Z0-9]+)");
        return new LibraryChangeEvent
        {
            Uri = message.Uri,
            Set = "liked-songs-artist",
            ArtistId = match.Success ? match.Groups[1].Value : null,
            Items = Array.Empty<LibraryChangeItem>(),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Extracts playlist URI from dealer message URI.
    /// </summary>
    /// <example>
    /// "hm://playlist/v2/playlist/37i9dQZF1DX4WYpdgoIcn6" -> "spotify:playlist:37i9dQZF1DX4WYpdgoIcn6"
    /// </example>
    private static string? ExtractPlaylistUriFromDealerUri(string dealerUri)
    {
        // Pattern: hm://playlist/v2/playlist/{id}
        var match = System.Text.RegularExpressions.Regex.Match(
            dealerUri,
            @"playlist/v2/playlist/([a-zA-Z0-9]+)");

        return match.Success ? $"spotify:playlist:{match.Groups[1].Value}" : null;
    }

    private void OnError(Exception ex)
    {
        _logger?.LogError(ex, "Error in library change subscription");
    }

    private static string DetermineSetFromUri(string uri)
    {
        if (uri.Contains("track", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("collection", StringComparison.OrdinalIgnoreCase))
        {
            return "collection"; // Tracks
        }
        if (uri.Contains("album", StringComparison.OrdinalIgnoreCase))
        {
            return "albums";
        }
        if (uri.Contains("artist", StringComparison.OrdinalIgnoreCase))
        {
            return "artists";
        }
        if (uri.Contains("playlist", StringComparison.OrdinalIgnoreCase))
        {
            return "playlists";
        }
        return "unknown";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _subscription?.Dispose();
        _changes.OnCompleted();
        _changes.Dispose();

        _logger?.LogInformation("LibraryChangeManager disposed");
        await Task.CompletedTask;
    }
}

/// <summary>
/// Event representing a change to the user's library.
/// </summary>
public sealed record LibraryChangeEvent
{
    /// <summary>
    /// The Dealer message URI that triggered this event.
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// The collection set that changed: "collection" (tracks), "albums", "artists", "playlists".
    /// </summary>
    public required string Set { get; init; }

    /// <summary>
    /// The username whose library changed (if available).
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// For playlist changes, the specific playlist URI that changed.
    /// </summary>
    public string? PlaylistUri { get; init; }

    /// <summary>
    /// True when the change targets the user's rootlist rather than a specific playlist.
    /// </summary>
    public bool IsRootlist { get; init; }

    /// <summary>
    /// For <c>Set="liked-songs-artist"</c> events, the Spotify artist id whose
    /// per-artist liked-songs view changed. Null for other event kinds.
    /// </summary>
    public string? ArtistId { get; init; }

    /// <summary>
    /// For playlist changes, the new revision after modifications.
    /// </summary>
    public byte[]? NewRevision { get; init; }

    /// <summary>
    /// For playlist changes, the parent revision the diff applies on top of —
    /// i.e. the revision the user was previously at. Lets <c>PlaylistCacheService</c>
    /// apply <see cref="Ops"/> directly when the cached <c>existing.Revision</c>
    /// equals this value, skipping the <c>/diff</c> network round-trip entirely.
    /// </summary>
    public byte[]? FromRevision { get; init; }

    /// <summary>
    /// For playlist changes, the raw protobuf <see cref="Op"/> sequence carried
    /// in the Mercury push. Same shape we'd get back from a successful <c>/diff</c>
    /// fetch, so the same <c>PlaylistDiffApplier</c> can apply them locally.
    /// Empty when the parser failed or the message wasn't a playlist update.
    /// </summary>
    public IReadOnlyList<Op>? Ops { get; init; }

    /// <summary>
    /// List of items that changed.
    /// </summary>
    public IReadOnlyList<LibraryChangeItem> Items { get; init; } = Array.Empty<LibraryChangeItem>();

    /// <summary>
    /// When this change was received.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Raw payload bytes if parsing failed.
    /// </summary>
    public byte[]? RawPayload { get; init; }
}

/// <summary>
/// Individual item change within a library change event.
/// </summary>
public sealed record LibraryChangeItem
{
    /// <summary>
    /// Spotify URI of the item (e.g., "spotify:track:xxx").
    /// </summary>
    public required string ItemUri { get; init; }

    /// <summary>
    /// When the item was added (null if removed or unknown).
    /// </summary>
    public DateTimeOffset? AddedAt { get; init; }

    /// <summary>
    /// True if the item was removed from the library.
    /// </summary>
    public bool IsRemoved { get; init; }
}
