using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Central activity feed. Producers publish via Post/Start/Complete/Fail.
/// UI binds to Items + UnreadCount. Listens to IMessenger for sync messages.
/// </summary>
public sealed partial class ActivityService : ObservableObject, IActivityService
{
    private readonly ObservableCollection<IActivityItem> _items = new();
    private readonly Dictionary<string, CategoryStyle> _categoryStyles = new();
    private readonly ILogger? _logger;
    private readonly DispatcherQueue? _dispatcher;

    [ObservableProperty] private int _unreadCount;

    public ReadOnlyObservableCollection<IActivityItem> Items { get; }

    public ActivityService(IMessenger messenger, ILogger<ActivityService>? logger = null)
    {
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
                var text = summary.HasChanges
                    ? string.Join(", ", summary.Entries.Select(e => $"{e.CountText} {e.Label.ToLower()}"))
                    : "Everything up to date";

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
    }

    private Guid? _activeSyncId;

    public Guid Post(string category, string title, string? iconGlyph = null,
                     ActivityStatus status = ActivityStatus.Info, string? message = null)
    {
        var item = new NotificationActivityItem
        {
            Category = category,
            Title = title,
            IconGlyph = iconGlyph ?? GetCategoryStyle(category)?.DefaultIconGlyph,
            Status = status,
            Message = message
        };

        AddItem(item);
        return item.Id;
    }

    public Guid Post(string category, string title, IReadOnlyList<ActivityAction> actions,
                     string? iconGlyph = null, ActivityStatus status = ActivityStatus.Info,
                     string? message = null)
    {
        var item = new NotificationActivityItem
        {
            Category = category,
            Title = title,
            IconGlyph = iconGlyph ?? GetCategoryStyle(category)?.DefaultIconGlyph,
            Status = status,
            Message = message,
            Actions = actions
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

    private IActivityItem? FindItem(Guid id) =>
        _items.FirstOrDefault(i => i.Id == id);

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
