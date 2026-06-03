using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services.Actions;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Json;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Central activity feed. Producers publish via Post/Start/Complete/Fail.
/// UI binds to Items + UnreadCount. Listens to IMessenger for sync messages.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ActivityService : ObservableObject, IActivityService, IUserActionActivitySink
{
    private sealed record UserActionPresentation(
        string Title,
        string? Message,
        string? IconGlyph,
        string? ImageUrl,
        string? NavigationUri,
        string? DetailTitle,
        string? DetailSubtitle,
        string? DetailBody,
        IReadOnlyList<ActivityDetailRow>? DetailRows,
        ActivityOutcome Outcome,
        string? OutcomeLabel);

    private readonly ObservableCollection<IActivityItem> _items = new();
    private readonly Dictionary<string, CategoryStyle> _categoryStyles = new();
    private readonly IMetadataDatabase? _database;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue? _dispatcher;

    [ObservableProperty] public partial int UnreadCount { get; set; }

    public ReadOnlyObservableCollection<IActivityItem> Items { get; }

    public ActivityService(
        IMessenger messenger,
        IMetadataDatabase? database = null,
        ILogger<ActivityService>? logger = null)
    {
        _database = database;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Items = new ReadOnlyObservableCollection<IActivityItem>(_items);

        // Register default category styles
        RegisterCategory(new CategoryStyle("app", "App", "\uE946", "SystemAccentColor"));
        RegisterCategory(new CategoryStyle("sync", "Sync", "\uE895", "SystemAccentColor"));
        RegisterCategory(new CategoryStyle("spotify", "Spotify", "\uE8D6", "SystemAccentColor"));
        RegisterCategory(new CategoryStyle("playback", "Playback", "\uE768", "SystemAccentColor"));

        // Auto-listen to sync lifecycle messages
        messenger.Register<LibrarySyncStartedMessage>(this, (_, _) =>
        {
            var id = Start("sync", "Library Sync", "\uE895");
            // Store the ID so we can complete it later
            _activeSyncId = id;
        });

        messenger.Register<LibrarySyncCompletedMessage>(this, (_, msg) =>
        {
            if (_activeSyncId.HasValue)
            {
                var summary = msg.Value;
                if (!summary.HasChanges && !summary.HadPartialFailure)
                {
                    Remove(_activeSyncId.Value);
                    _activeSyncId = null;
                    return;
                }

                var text = string.Join(", ", summary.Entries.Select(e => $"{e.CountText} {e.Label.ToLower()}"));

                if (summary.HadPartialFailure)
                {
                    // Show as warning with failure reason appended
                    var warningText = string.IsNullOrEmpty(text) ? "" : $"{text} — ";
                    warningText += summary.PartialFailureReason ?? "some operations failed";
                    Fail(_activeSyncId.Value, warningText);
                }
                else
                {
                    Complete(_activeSyncId.Value, text);
                }
            }
            _activeSyncId = null;
        });

        messenger.Register<LibrarySyncFailedMessage>(this, (_, msg) =>
        {
            if (_activeSyncId.HasValue)
                Fail(_activeSyncId.Value, msg.Value);
            _activeSyncId = null;
        });

        _logger?.LogDebug("ActivityService initialized");

        _ = LoadPersistedUserActionsAsync();
    }

    private Guid? _activeSyncId;

    public Guid Post(string category, string title, string? iconGlyph = null,
                     ActivityStatus status = ActivityStatus.Info, string? message = null,
                     bool silent = false)
    {
        var item = new NotificationActivityItem
        {
            Category = category,
            Title = title,
            IconGlyph = iconGlyph ?? GetCategoryStyle(category)?.DefaultIconGlyph,
            Status = status,
            Message = message,
            IsRead = silent
        };

        AddItem(item);
        return item.Id;
    }

    public Guid Post(string category, string title, IReadOnlyList<ActivityAction> actions,
                     string? iconGlyph = null, ActivityStatus status = ActivityStatus.Info,
                     string? message = null, bool silent = false)
    {
        var item = new NotificationActivityItem
        {
            Category = category,
            Title = title,
            IconGlyph = iconGlyph ?? GetCategoryStyle(category)?.DefaultIconGlyph,
            Status = status,
            Message = message,
            Actions = ToActionList(actions),
            IsRead = silent
        };

        AddItem(item);
        return item.Id;
    }

    public Guid Start(string category, string title, string? iconGlyph = null)
    {
        var item = new ProgressActivityItem
        {
            Category = category,
            Title = title,
            IconGlyph = iconGlyph ?? GetCategoryStyle(category)?.DefaultIconGlyph,
            Status = ActivityStatus.InProgress
        };

        AddItem(item);
        return item.Id;
    }

    public void Complete(Guid id, string? message = null)
    {
        var item = FindItem(id);
        if (item == null) return;

        Dispatch(() =>
        {
            item.Status = ActivityStatus.Completed;
            if (message != null) item.Message = message;
            if (item is ProgressActivityItem p) p.Progress = 1.0;
        });
    }

    public void Fail(Guid id, string error)
    {
        var item = FindItem(id);
        if (item == null) return;

        Dispatch(() =>
        {
            item.Status = ActivityStatus.Failed;
            item.Message = error;
        });
    }

    public void Update(Guid id, string? message = null, double? progress = null,
                       string? progressText = null, TimeSpan? eta = null)
    {
        var item = FindItem(id);
        if (item == null) return;

        Dispatch(() =>
        {
            if (message != null) item.Message = message;
            if (item is ProgressActivityItem p)
            {
                if (progress.HasValue) p.Progress = progress;
                if (progressText != null) p.ProgressText = progressText;
                if (eta.HasValue) p.EstimatedTimeRemaining = eta;
            }
        });
    }

    public void MarkAllRead()
    {
        Dispatch(() =>
        {
            foreach (var item in _items)
                item.IsRead = true;
            UpdateUnreadCount();
        });
    }

    public void ClearAll()
    {
        Dispatch(() =>
        {
            _items.Clear();
            UpdateUnreadCount();
        });

        if (_database != null)
            _ = _database.ClearUserActionActivitiesAsync();
    }

    public void ClearCompleted()
    {
        Dispatch(() =>
        {
            var completed = _items.Where(i => i.Status == ActivityStatus.Completed).ToList();
            foreach (var item in completed)
                _items.Remove(item);
            UpdateUnreadCount();
        });
    }

    public void RegisterCategory(CategoryStyle style) =>
        _categoryStyles[style.CategoryId] = style;

    public CategoryStyle? GetCategoryStyle(string category) =>
        _categoryStyles.GetValueOrDefault(category);

    private void AddItem(IActivityItem item)
    {
        Dispatch(() =>
        {
            _items.Insert(0, item); // newest first

            // Auto-prune: max 50 items
            while (_items.Count > 50)
                _items.RemoveAt(_items.Count - 1);

            UpdateUnreadCount();
            _logger?.LogDebug("Activity: [{Category}] {Title} ({Status})",
                item.Category, item.Title, item.Status);
        });
    }

    public async Task RecordAsync(CompletedUserAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var presentation = await BuildUserActionPresentationAsync(
            action.Title,
            action.Message,
            action.IconGlyph,
            action.Descriptor,
            isUndone: false,
            ct).ConfigureAwait(false);

        var descriptorJson = JsonSerializer.Serialize(
            action.Descriptor,
            WaveeUiWinUiJsonContext.Default.UserActionDescriptor);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_database != null)
        {
            await _database.UpsertUserActionActivityAsync(new UserActionActivityEntry
            {
                Id = action.Id.ToString("D"),
                Category = action.Category,
                Title = presentation.Title,
                Message = presentation.Message,
                IconGlyph = presentation.IconGlyph,
                UndoLabel = action.UndoLabel,
                ActionKind = action.Descriptor.Kind,
                DescriptorJson = descriptorJson,
                CreatedAt = createdAt,
                IsUndone = false
            }, ct).ConfigureAwait(false);
        }

        var item = CreateUserActionItem(
            action.Id,
            action.Category,
            presentation,
            action.UndoLabel,
            action.Descriptor,
            isUndone: false,
            timestamp: DateTimeOffset.FromUnixTimeSeconds(createdAt));

        AddItem(item);
    }

    public async Task MarkUndoneAsync(Guid activityId, CancellationToken ct = default)
    {
        if (_database != null)
            await _database.MarkUserActionActivityUndoneAsync(activityId.ToString("D"), ct).ConfigureAwait(false);

        Dispatch(() =>
        {
            var index = _items
                .Select((item, i) => (item, i))
                .FirstOrDefault(pair => pair.item.Id == activityId)
                .i;
            if (index < 0 || index >= _items.Count || _items[index].Id != activityId)
                return;

            var current = _items[index];
            var notification = current as NotificationActivityItem;
            _items[index] = new NotificationActivityItem
            {
                Id = current.Id,
                Category = current.Category,
                Title = current.Title,
                Message = "Undone",
                IconGlyph = current.IconGlyph,
                ImageUrl = notification?.ImageUrl,
                NavigationUri = notification?.NavigationUri,
                DetailTitle = notification?.DetailTitle,
                DetailSubtitle = "Undone",
                DetailBody = notification?.DetailBody,
                DetailRows = notification?.DetailRows,
                Outcome = ActivityOutcome.Undo,
                OutcomeLabel = "Undone",
                ActivityType = ActivityNotificationType.UserAction,
                Status = ActivityStatus.Completed,
                IsRead = true,
                Timestamp = current.Timestamp
            };
            UpdateUnreadCount();
        });
    }

    private async Task LoadPersistedUserActionsAsync()
    {
        if (_database == null)
            return;

        try
        {
            var entries = await _database.GetUserActionActivitiesAsync().ConfigureAwait(false);
            foreach (var entry in entries.OrderBy(static e => e.CreatedAt))
            {
                if (!Guid.TryParse(entry.Id, out var id))
                    continue;

                UserActionDescriptor? descriptor = null;
                try
                {
                    descriptor = JsonSerializer.Deserialize(
                        entry.DescriptorJson,
                        WaveeUiWinUiJsonContext.Default.UserActionDescriptor);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to deserialize persisted activity action {Id}", entry.Id);
                }

                var presentation = await BuildUserActionPresentationAsync(
                    entry.Title,
                    entry.IsUndone ? "Undone" : entry.Message,
                    entry.IconGlyph,
                    descriptor,
                    entry.IsUndone).ConfigureAwait(false);

                AddItem(CreateUserActionItem(
                    id,
                    entry.Category,
                    presentation,
                    entry.UndoLabel,
                    entry.IsUndone ? null : descriptor,
                    entry.IsUndone,
                    DateTimeOffset.FromUnixTimeSeconds(entry.CreatedAt)));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load persisted user action activity");
        }
    }

    private static NotificationActivityItem CreateUserActionItem(
        Guid id,
        string category,
        UserActionPresentation presentation,
        string undoLabel,
        UserActionDescriptor? descriptor,
        bool isUndone,
        DateTimeOffset timestamp)
    {
        ObservableCollection<ActivityAction>? actions = null;
        if (!isUndone && descriptor is not null)
        {
            actions = new ObservableCollection<ActivityAction>
            {
                new ActivityAction(undoLabel, null, () =>
                    Ioc.Default.GetRequiredService<IUserActionRunner>().UndoAsync(id, descriptor))
            };
        }

        return new NotificationActivityItem
        {
            Id = id,
            Category = category,
            Title = presentation.Title,
            Message = isUndone ? "Undone" : presentation.Message,
            IconGlyph = presentation.IconGlyph,
            ImageUrl = presentation.ImageUrl,
            NavigationUri = presentation.NavigationUri,
            DetailTitle = presentation.DetailTitle,
            DetailSubtitle = isUndone ? "Undone" : presentation.DetailSubtitle,
            DetailBody = presentation.DetailBody,
            DetailRows = presentation.DetailRows,
            Outcome = isUndone ? ActivityOutcome.Undo : presentation.Outcome,
            OutcomeLabel = isUndone ? "Undone" : presentation.OutcomeLabel,
            ActivityType = ActivityNotificationType.UserAction,
            Status = ActivityStatus.Completed,
            IsRead = true,
            Timestamp = timestamp,
            Actions = actions
        };
    }

    private async Task<UserActionPresentation> BuildUserActionPresentationAsync(
        string fallbackTitle,
        string? fallbackMessage,
        string? fallbackIconGlyph,
        UserActionDescriptor? descriptor,
        bool isUndone,
        CancellationToken ct = default)
    {
        var fallback = new UserActionPresentation(
            fallbackTitle,
            fallbackMessage,
            fallbackIconGlyph,
            ImageUrl: null,
            NavigationUri: null,
            DetailTitle: fallbackTitle,
            DetailSubtitle: fallbackMessage,
            DetailBody: null,
            DetailRows: null,
            Outcome: isUndone ? ActivityOutcome.Undo : ActivityOutcome.None,
            OutcomeLabel: isUndone ? "Undone" : null);

        if (descriptor is null)
            return fallback;

        try
        {
            using var document = JsonDocument.Parse(descriptor.PayloadJson);
            var payload = document.RootElement;

            return descriptor.Kind switch
            {
                SetLibrarySavedAction.Kind => await BuildSavedActionPresentationAsync(payload, fallback, isUndone, ct).ConfigureAwait(false),
                SetPinnedAction.Kind => await BuildPinActionPresentationAsync(payload, fallback, isUndone, ct).ConfigureAwait(false),
                SetPlaylistFollowedAction.Kind => await BuildPlaylistFollowPresentationAsync(payload, fallback, isUndone, ct).ConfigureAwait(false),
                PlaylistTracksAction.Kind => await BuildPlaylistTracksPresentationAsync(payload, fallback, isUndone, ct).ConfigureAwait(false),
                CreatePlaylistAction.Kind => BuildCreatePlaylistPresentation(payload, fallback, isUndone),
                DeletePlaylistAction.Kind => await BuildDeletePlaylistPresentationAsync(payload, fallback, isUndone, ct).ConfigureAwait(false),
                _ => fallback
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to build activity presentation for {Kind}", descriptor.Kind);
            return fallback;
        }
    }

    private async Task<UserActionPresentation> BuildSavedActionPresentationAsync(
        JsonElement payload,
        UserActionPresentation fallback,
        bool isUndone,
        CancellationToken ct)
    {
        var itemUri = GetString(payload, "ItemUri");
        var newSaved = GetBool(payload, "NewSaved");
        var itemType = GetSavedItemType(payload, "ItemType");

        var entity = await TryGetEntityAsync(itemUri, ct).ConfigureAwait(false);
        var itemName = DisplayName(entity, itemUri);
        var title = FormatSavedTitle(itemType, itemName, newSaved);
        var subtitle = FormatEntitySubtitle(entity, itemType);
        var rows = new List<ActivityDetailRow>();
        AddRow(rows, "Action", isUndone ? "Undone" : FormatSavedVerb(itemType, newSaved));
        AddRow(rows, "Item", itemName);
        AddRow(rows, "Artist", entity?.ArtistName);
        AddRow(rows, "Album", entity?.AlbumName);
        AddRow(rows, "URI", itemUri);

        return fallback with
        {
            Title = title,
            Message = subtitle,
            IconGlyph = IconForUri(itemUri) ?? fallback.IconGlyph,
            ImageUrl = NormalizeActivityImageUrl(entity?.ImageUrl),
            NavigationUri = itemUri,
            DetailTitle = title,
            DetailSubtitle = subtitle,
            DetailRows = rows,
            Outcome = OutcomeFor(isUndone, newSaved ? ActivityOutcome.Positive : ActivityOutcome.Negative),
            OutcomeLabel = OutcomeLabelFor(isUndone, newSaved ? "Saved" : "Removed")
        };
    }

    private async Task<UserActionPresentation> BuildPinActionPresentationAsync(
        JsonElement payload,
        UserActionPresentation fallback,
        bool isUndone,
        CancellationToken ct)
    {
        var uri = GetString(payload, "Uri");
        var pinned = GetBool(payload, "NewPinned");
        var entity = await TryGetEntityAsync(uri, ct).ConfigureAwait(false);
        var itemName = DisplayName(entity, uri);
        var action = pinned ? "Pinned" : "Unpinned";
        var title = $"{action} \"{itemName}\"";
        var subtitle = EntityKindLabel(uri);
        var rows = new List<ActivityDetailRow>();
        AddRow(rows, "Action", isUndone ? "Undone" : action);
        AddRow(rows, "Item", itemName);
        AddRow(rows, "Type", subtitle);
        AddRow(rows, "URI", uri);

        return fallback with
        {
            Title = title,
            Message = subtitle,
            IconGlyph = IconForUri(uri) ?? "\uE840",
            ImageUrl = NormalizeActivityImageUrl(entity?.ImageUrl),
            NavigationUri = uri,
            DetailTitle = title,
            DetailSubtitle = subtitle,
            DetailRows = rows,
            Outcome = OutcomeFor(isUndone, pinned ? ActivityOutcome.Positive : ActivityOutcome.Negative),
            OutcomeLabel = OutcomeLabelFor(isUndone, pinned ? "Pinned" : "Unpinned")
        };
    }

    private async Task<UserActionPresentation> BuildPlaylistFollowPresentationAsync(
        JsonElement payload,
        UserActionPresentation fallback,
        bool isUndone,
        CancellationToken ct)
    {
        var playlistUri = GetString(payload, "PlaylistUri");
        var followed = GetBool(payload, "NewFollowed");
        var entity = await TryGetEntityAsync(playlistUri, ct).ConfigureAwait(false);
        var playlistName = DisplayName(entity, playlistUri);
        var action = followed ? "Followed" : "Unfollowed";
        var title = $"{action} \"{playlistName}\"";
        var rows = new List<ActivityDetailRow>();
        AddRow(rows, "Action", isUndone ? "Undone" : action);
        AddRow(rows, "Playlist", playlistName);
        AddRow(rows, "URI", playlistUri);

        return fallback with
        {
            Title = title,
            Message = "Playlist",
            IconGlyph = "\uE93F",
            ImageUrl = NormalizeActivityImageUrl(entity?.ImageUrl),
            NavigationUri = playlistUri,
            DetailTitle = title,
            DetailSubtitle = "Playlist",
            DetailRows = rows,
            Outcome = OutcomeFor(isUndone, followed ? ActivityOutcome.Positive : ActivityOutcome.Negative),
            OutcomeLabel = OutcomeLabelFor(isUndone, followed ? "Followed" : "Unfollowed")
        };
    }

    private async Task<UserActionPresentation> BuildPlaylistTracksPresentationAsync(
        JsonElement payload,
        UserActionPresentation fallback,
        bool isUndone,
        CancellationToken ct)
    {
        var playlistUri = GetString(payload, "PlaylistUri");
        var trackUris = GetStringArray(payload, "TrackUris");
        var added = GetBool(payload, "AddTracks");
        var count = trackUris.Count;
        var playlist = await TryGetEntityAsync(playlistUri, ct).ConfigureAwait(false);
        var tracks = await TryGetEntitiesAsync(trackUris, ct).ConfigureAwait(false);
        var trackTitles = trackUris
            .Select(uri => DisplayName(tracks.GetValueOrDefault(uri), uri))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        // Owned playlists live in spotify_playlists, not the entities table, so the
        // entity lookup above misses and DisplayName would fall back to the bare id.
        // Resolve the real name from the playlist cache when the entity has no title.
        var playlistName = FirstNonWhiteSpace(
            playlist?.Title,
            await TryGetPlaylistNameAsync(playlistUri, ct).ConfigureAwait(false),
            ShortUri(playlistUri),
            playlistUri,
            "playlist")!;
        var action = added ? "Added" : "Removed";
        var noun = count == 1 ? "song" : "songs";
        var title = $"{action} {count} {noun} to \"{playlistName}\"";
        var message = SummarizeItems(trackTitles);
        var rows = new List<ActivityDetailRow>();
        AddRow(rows, "Action", isUndone ? "Undone" : action);
        AddRow(rows, "Playlist", playlistName);
        AddRow(rows, "Songs", count.ToString());
        AddRow(rows, "Tracks", FormatTrackList(trackTitles, trackUris));
        AddRow(rows, "URI", playlistUri);

        return fallback with
        {
            Title = title,
            Message = message,
            IconGlyph = "\uE8D6",
            ImageUrl = NormalizeActivityImageUrl(
                playlist?.ImageUrl ?? tracks.Values.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.ImageUrl))?.ImageUrl),
            NavigationUri = playlistUri,
            DetailTitle = title,
            DetailSubtitle = message,
            DetailRows = rows,
            Outcome = OutcomeFor(isUndone, added ? ActivityOutcome.Positive : ActivityOutcome.Negative),
            OutcomeLabel = OutcomeLabelFor(isUndone, added ? "Added" : "Removed")
        };
    }

    private static UserActionPresentation BuildCreatePlaylistPresentation(
        JsonElement payload,
        UserActionPresentation fallback,
        bool isUndone)
    {
        var name = GetString(payload, "Name");
        var playlistUri = GetString(payload, "CreatedPlaylistUri");
        var title = !string.IsNullOrWhiteSpace(name) ? $"Created \"{name}\"" : fallback.Title;
        var rows = new List<ActivityDetailRow>();
        AddRow(rows, "Action", isUndone ? "Undone" : "Created");
        AddRow(rows, "Playlist", name);
        AddRow(rows, "URI", playlistUri);

        return fallback with
        {
            Title = title,
            Message = "Playlist",
            IconGlyph = "\uE93F",
            NavigationUri = playlistUri,
            DetailTitle = title,
            DetailSubtitle = "Playlist",
            DetailRows = rows,
            Outcome = OutcomeFor(isUndone, ActivityOutcome.Positive),
            OutcomeLabel = OutcomeLabelFor(isUndone, "Created")
        };
    }

    private async Task<UserActionPresentation> BuildDeletePlaylistPresentationAsync(
        JsonElement payload,
        UserActionPresentation fallback,
        bool isUndone,
        CancellationToken ct)
    {
        var playlistUri = GetString(payload, "PlaylistUri");
        var entity = await TryGetEntityAsync(playlistUri, ct).ConfigureAwait(false);
        var playlistName = DisplayName(entity, playlistUri);
        var title = $"Deleted \"{playlistName}\"";
        var rows = new List<ActivityDetailRow>();
        AddRow(rows, "Action", isUndone ? "Undone" : "Deleted");
        AddRow(rows, "Playlist", playlistName);
        AddRow(rows, "URI", playlistUri);

        return fallback with
        {
            Title = title,
            Message = "Playlist",
            IconGlyph = "\uE93F",
            ImageUrl = NormalizeActivityImageUrl(entity?.ImageUrl),
            NavigationUri = playlistUri,
            DetailTitle = title,
            DetailSubtitle = "Playlist",
            DetailRows = rows,
            Outcome = OutcomeFor(isUndone, ActivityOutcome.Negative),
            OutcomeLabel = OutcomeLabelFor(isUndone, "Deleted")
        };
    }

    private async Task<CachedEntity?> TryGetEntityAsync(string? uri, CancellationToken ct)
    {
        if (_database is null || string.IsNullOrWhiteSpace(uri))
            return null;

        try
        {
            return await _database.GetEntityAsync(uri, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Failed to load activity entity metadata for {Uri}", uri);
            return null;
        }
    }

    // Playlist names live in spotify_playlists (the playlist cache), not the
    // entities table — used to resolve a real name for playlist activity entries.
    private async Task<string?> TryGetPlaylistNameAsync(string? uri, CancellationToken ct)
    {
        if (_database is null || string.IsNullOrWhiteSpace(uri))
            return null;

        try
        {
            var entry = await _database.GetPlaylistCacheEntryAsync(uri, false, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry?.Name) ? null : entry!.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Failed to resolve playlist name for activity {Uri}", uri);
            return null;
        }
    }

    private async Task<Dictionary<string, CachedEntity>> TryGetEntitiesAsync(IReadOnlyList<string> uris, CancellationToken ct)
    {
        if (_database is null || uris.Count == 0)
            return new Dictionary<string, CachedEntity>(StringComparer.Ordinal);

        try
        {
            var entities = await _database.GetEntitiesAsync(uris, ct).ConfigureAwait(false);
            return entities.ToDictionary(static e => e.Uri, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Failed to load activity entities metadata");
            return new Dictionary<string, CachedEntity>(StringComparer.Ordinal);
        }
    }

    // Action payloads are serialized via WaveeUiJsonContext, which sets
    // JsonKnownNamingPolicy.CamelCase — so a payload field like ItemUri lands
    // on the wire as "itemUri". JsonElement.TryGetProperty is case-sensitive,
    // so a literal "ItemUri" lookup quietly returns null and the activity bell
    // renders "Removed 'item'" with no image and a default verb. All payload
    // reads route through this case-insensitive helper instead so a future
    // JsonContext rename can't silently break the activity surface again.
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        TryGetPropertyIgnoreCase(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBool(JsonElement element, string propertyName) =>
        TryGetPropertyIgnoreCase(element, propertyName, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        && property.GetBoolean();

    private static SavedItemType GetSavedItemType(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
            return SavedItemType.Track;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return Enum.IsDefined(typeof(SavedItemType), value) ? (SavedItemType)value : SavedItemType.Track;

        if (property.ValueKind == JsonValueKind.String
            && Enum.TryParse(property.GetString(), ignoreCase: true, out SavedItemType parsed))
        {
            return parsed;
        }

        return SavedItemType.Track;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                values.Add(item.GetString()!);
        }

        return values;
    }

    private static string DisplayName(CachedEntity? entity, string? uri) =>
        FirstNonWhiteSpace(entity?.Title, entity?.AlbumName, ShortUri(uri), uri, "item")!;

    private static string? FormatEntitySubtitle(CachedEntity? entity, SavedItemType itemType)
    {
        if (entity is null)
        {
            return itemType switch
            {
                SavedItemType.Track => "Liked Songs",
                SavedItemType.Album => "Album",
                SavedItemType.Artist => "Artist",
                SavedItemType.Show => "Show",
                _ => null
            };
        }

        return itemType switch
        {
            SavedItemType.Track => FirstNonWhiteSpace(
                JoinParts(" - ", entity.ArtistName, entity.AlbumName),
                entity.ArtistName,
                entity.AlbumName,
                "Liked Songs"),
            SavedItemType.Album => FirstNonWhiteSpace(entity.ArtistName, "Album"),
            SavedItemType.Artist => "Artist",
            SavedItemType.Show => FirstNonWhiteSpace(entity.Publisher, "Show"),
            _ => EntityKindLabel(entity.Uri)
        };
    }

    private static string FormatSavedTitle(SavedItemType type, string itemName, bool saved) => (type, saved) switch
    {
        (SavedItemType.Track, true) => $"Saved \"{itemName}\"",
        (SavedItemType.Track, false) => $"Removed \"{itemName}\"",
        (SavedItemType.Album, true) => $"Saved album \"{itemName}\"",
        (SavedItemType.Album, false) => $"Removed album \"{itemName}\"",
        (SavedItemType.Artist, true) => $"Followed {itemName}",
        (SavedItemType.Artist, false) => $"Unfollowed {itemName}",
        (SavedItemType.Show, true) => $"Followed \"{itemName}\"",
        (SavedItemType.Show, false) => $"Unfollowed \"{itemName}\"",
        _ => saved ? $"Saved \"{itemName}\"" : $"Removed \"{itemName}\""
    };

    private static string FormatSavedVerb(SavedItemType type, bool saved) => (type, saved) switch
    {
        (SavedItemType.Track, true) => "Saved to Liked Songs",
        (SavedItemType.Track, false) => "Removed from Liked Songs",
        (SavedItemType.Album, true) => "Saved album",
        (SavedItemType.Album, false) => "Removed album",
        (SavedItemType.Artist, true) => "Followed artist",
        (SavedItemType.Artist, false) => "Unfollowed artist",
        (SavedItemType.Show, true) => "Followed show",
        (SavedItemType.Show, false) => "Unfollowed show",
        _ => saved ? "Saved" : "Removed"
    };

    private static string SummarizeItems(IReadOnlyList<string> titles)
    {
        if (titles.Count == 0)
            return "";

        var visible = titles.Take(3).ToArray();
        var text = string.Join(", ", visible);
        var remaining = titles.Count - visible.Length;
        return remaining > 0 ? $"{text} + {remaining} more" : text;
    }

    private static string FormatTrackList(IReadOnlyList<string> trackTitles, IReadOnlyList<string> trackUris)
    {
        var values = trackTitles.Count > 0 ? trackTitles : trackUris;
        if (values.Count == 0)
            return "";

        var visible = values.Take(12).ToArray();
        var text = string.Join(Environment.NewLine, visible.Select(static (title, index) => $"{index + 1}. {title}"));
        var remaining = values.Count - visible.Length;
        return remaining > 0 ? $"{text}{Environment.NewLine}+ {remaining} more" : text;
    }

    private static string? IconForUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        if (uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return "\uE8D6";
        if (uri.StartsWith("spotify:album:", StringComparison.Ordinal)) return "\uE93C";
        if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal)) return "\uE77B";
        if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) return "\uE93F";
        if (uri.StartsWith("spotify:show:", StringComparison.Ordinal)) return "\uE8D6";
        return "\uE8D6";
    }

    private static string? NormalizeActivityImageUrl(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        if (imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || imageUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || imageUrl.StartsWith("ms-appx://", StringComparison.OrdinalIgnoreCase)
            || imageUrl.StartsWith("spotify:image:", StringComparison.Ordinal)
            || imageUrl.StartsWith("wavee-artwork://", StringComparison.Ordinal))
        {
            return imageUrl;
        }

        return null;
    }

    private static string EntityKindLabel(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return "Item";
        if (uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return "Song";
        if (uri.StartsWith("spotify:album:", StringComparison.Ordinal)) return "Album";
        if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal)) return "Artist";
        if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) return "Playlist";
        if (uri.StartsWith("spotify:show:", StringComparison.Ordinal)) return "Show";
        return "Item";
    }

    private static string? ShortUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var lastColon = uri.LastIndexOf(':');
        return lastColon >= 0 && lastColon + 1 < uri.Length ? uri[(lastColon + 1)..] : uri;
    }

    private static string? JoinParts(string separator, params string?[] values)
    {
        var parts = values.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return parts.Length == 0 ? null : string.Join(separator, parts);
    }

    private static string? FirstNonWhiteSpace(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static ObservableCollection<ActivityAction> ToActionList(IReadOnlyList<ActivityAction> actions) =>
        new(actions);

    private static ActivityOutcome OutcomeFor(bool isUndone, ActivityOutcome outcome) =>
        isUndone ? ActivityOutcome.Undo : outcome;

    private static string OutcomeLabelFor(bool isUndone, string label) =>
        isUndone ? "Undone" : label;

    private static void AddRow(List<ActivityDetailRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            rows.Add(new ActivityDetailRow(label, value));
    }

    private IActivityItem? FindItem(Guid id) =>
        _items.FirstOrDefault(i => i.Id == id);

    private void Remove(Guid id)
    {
        Dispatch(() =>
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null) return;

            _items.Remove(item);
            UpdateUnreadCount();
        });
    }

    private void UpdateUnreadCount() =>
        UnreadCount = _items.Count(i => !i.IsRead);

    private void Dispatch(Action action)
    {
        if (_dispatcher?.HasThreadAccess == true)
            action();
        else
            _dispatcher?.TryEnqueue(() => action());
    }
}
