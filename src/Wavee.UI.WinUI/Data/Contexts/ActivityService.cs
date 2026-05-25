using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.Contracts;
using Wavee.UI.Services.Actions;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Central activity feed. Producers publish via Post/Start/Complete/Fail.
/// UI binds to Items + UnreadCount. Listens to IMessenger for sync messages.
/// </summary>
public sealed partial class ActivityService : ObservableObject, IActivityService, IUserActionActivitySink
{
    private readonly ObservableCollection<IActivityItem> _items = new();
    private readonly Dictionary<string, CategoryStyle> _categoryStyles = new();
    private readonly IMetadataDatabase? _database;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue? _dispatcher;

    [ObservableProperty] private int _unreadCount;

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
            Actions = actions,
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

        var descriptorJson = System.Text.Json.JsonSerializer.Serialize(action.Descriptor);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_database != null)
        {
            await _database.UpsertUserActionActivityAsync(new UserActionActivityEntry
            {
                Id = action.Id.ToString("D"),
                Category = action.Category,
                Title = action.Title,
                Message = action.Message,
                IconGlyph = action.IconGlyph,
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
            action.Title,
            action.Message,
            action.IconGlyph,
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
            _items[index] = new NotificationActivityItem
            {
                Id = current.Id,
                Category = current.Category,
                Title = current.Title,
                Message = "Undone",
                IconGlyph = current.IconGlyph,
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
            Dispatch(() =>
            {
                foreach (var entry in entries.OrderBy(static e => e.CreatedAt))
                {
                    if (!Guid.TryParse(entry.Id, out var id))
                        continue;

                    UserActionDescriptor? descriptor = null;
                    if (!entry.IsUndone)
                    {
                        try
                        {
                            descriptor = System.Text.Json.JsonSerializer.Deserialize<UserActionDescriptor>(entry.DescriptorJson);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Failed to deserialize persisted activity action {Id}", entry.Id);
                        }
                    }

                    AddItem(CreateUserActionItem(
                        id,
                        entry.Category,
                        entry.Title,
                        entry.IsUndone ? "Undone" : entry.Message,
                        entry.IconGlyph,
                        entry.UndoLabel,
                        descriptor,
                        entry.IsUndone,
                        DateTimeOffset.FromUnixTimeSeconds(entry.CreatedAt)));
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load persisted user action activity");
        }
    }

    private static NotificationActivityItem CreateUserActionItem(
        Guid id,
        string category,
        string title,
        string? message,
        string? iconGlyph,
        string undoLabel,
        UserActionDescriptor? descriptor,
        bool isUndone,
        DateTimeOffset timestamp)
    {
        IReadOnlyList<ActivityAction>? actions = null;
        if (!isUndone && descriptor is not null)
        {
            actions =
            [
                new ActivityAction(undoLabel, null, () =>
                    Ioc.Default.GetRequiredService<IUserActionRunner>().UndoAsync(id, descriptor))
            ];
        }

        return new NotificationActivityItem
        {
            Id = id,
            Category = category,
            Title = title,
            Message = message,
            IconGlyph = iconGlyph,
            Status = ActivityStatus.Completed,
            IsRead = true,
            Timestamp = timestamp,
            Actions = actions
        };
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
