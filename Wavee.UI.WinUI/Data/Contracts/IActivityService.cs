using System;
using System.Collections.ObjectModel;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Central activity feed service. Any service can publish activities;
/// the UI binds to Items/UnreadCount for the bell icon + flyout.
/// </summary>
public interface IActivityService
{
    // ── Producers: publish activities ──

    /// <summary>Post a one-shot info/error notification.</summary>
    Guid Post(string category, string title, string? iconGlyph = null,
              ActivityStatus status = ActivityStatus.Info, string? message = null);

    /// <summary>Start a progress activity (returns ID for updates).</summary>
    Guid Start(string category, string title, string? iconGlyph = null);

    /// <summary>Mark an activity as completed.</summary>
    void Complete(Guid id, string? message = null);

    /// <summary>Mark an activity as failed.</summary>
    void Fail(Guid id, string error);

    /// <summary>Update progress mid-activity.</summary>
    void Update(Guid id, string? message = null, double? progress = null,
                string? progressText = null, TimeSpan? eta = null);

    // ── UI: bind to these ──

    /// <summary>All activity items (observable, newest first).</summary>
    ReadOnlyObservableCollection<IActivityItem> Items { get; }

    /// <summary>Count of unread items (for badge).</summary>
    int UnreadCount { get; }

    // ── Housekeeping ──

    void MarkAllRead();
    void ClearAll();
    void ClearCompleted();

    /// <summary>Register a category style for rendering.</summary>
    void RegisterCategory(CategoryStyle style);

    /// <summary>Get the style for a category (or null for default).</summary>
    CategoryStyle? GetCategoryStyle(string category);
}
