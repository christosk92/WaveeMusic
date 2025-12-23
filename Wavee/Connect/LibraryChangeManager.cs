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

            LibraryChangeEvent? changeEvent = null;

            if (message.Payload.Length > 0)
            {
                // Try playlist-specific parsing first for playlist URIs
                if (message.Uri.Contains("playlist", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var modInfo = PlaylistModificationInfo.Parser.ParseFrom(message.Payload);

                        // Extract playlist URI from the modification info
                        // Uri field contains the full URI as UTF-8 bytes (e.g., "spotify:playlist:xxx")
                        var playlistUri = modInfo.Uri?.Length > 0
                            ? modInfo.Uri.ToStringUtf8()
                            : ExtractPlaylistUriFromDealerUri(message.Uri);

                        changeEvent = new LibraryChangeEvent
                        {
                            Uri = message.Uri,
                            Set = "playlists",
                            PlaylistUri = playlistUri,
                            NewRevision = modInfo.NewRevision?.ToByteArray(),
                            Items = modInfo.Ops
                                .Where(op => op.Kind == Op.Types.Kind.Add && op.Add != null)
                                .SelectMany(op => op.Add.Items)
                                .Select(item => new LibraryChangeItem
                                {
                                    ItemUri = item.Uri,
                                    AddedAt = item.Attributes?.Timestamp > 0
                                        ? DateTimeOffset.FromUnixTimeMilliseconds(item.Attributes.Timestamp)
                                        : null,
                                    IsRemoved = false
                                })
                                .Concat(modInfo.Ops
                                    .Where(op => op.Kind == Op.Types.Kind.Rem && op.Rem != null)
                                    .SelectMany(op => op.Rem.Items)
                                    .Select(item => new LibraryChangeItem
                                    {
                                        ItemUri = item.Uri,
                                        IsRemoved = true
                                    }))
                                .ToList(),
                            Timestamp = DateTimeOffset.UtcNow
                        };

                        _logger?.LogDebug("Parsed playlist modification: {Uri}, {OpCount} ops",
                            playlistUri, modInfo.Ops.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to parse as PlaylistModificationInfo");
                    }
                }

                // Fall back to PubSubUpdate for collection changes
                if (changeEvent == null)
                {
                    try
                    {
                        var update = PubSubUpdate.Parser.ParseFrom(message.Payload);
                        changeEvent = new LibraryChangeEvent
                        {
                            Uri = message.Uri,
                            Set = update.Set ?? DetermineSetFromUri(message.Uri),
                            Username = update.Username,
                            Items = update.Items.Select(i => new LibraryChangeItem
                            {
                                ItemUri = i.Uri,
                                AddedAt = i.AddedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(i.AddedAt) : null,
                                IsRemoved = i.IsRemoved
                            }).ToList(),
                            Timestamp = DateTimeOffset.UtcNow
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to parse as PubSubUpdate, treating as raw payload");
                    }
                }
            }

            // If parsing failed, create a basic event with the raw info
            changeEvent ??= new LibraryChangeEvent
            {
                Uri = message.Uri,
                Set = DetermineSetFromUri(message.Uri),
                Items = new List<LibraryChangeItem>(),
                Timestamp = DateTimeOffset.UtcNow,
                RawPayload = message.Payload
            };

            _changes.OnNext(changeEvent);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing library change message");
        }
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
    /// For playlist changes, the new revision after modifications.
    /// </summary>
    public byte[]? NewRevision { get; init; }

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
