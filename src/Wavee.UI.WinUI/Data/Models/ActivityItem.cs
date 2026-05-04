using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.UI.WinUI.Data.Models;

// ── Status enum ──

public enum ActivityStatus { Info, InProgress, Completed, Failed }

// ── Action model ──

public sealed record ActivityAction(string Label, string? IconGlyph, Func<Task> Callback);

// ── Category styling ──

public sealed record CategoryStyle(
    string CategoryId,
    string DisplayName,
    string DefaultIconGlyph,
    string AccentColorKey);

// ── Interface: common contract for all activity items ──

public interface IActivityItem : INotifyPropertyChanged
{
    Guid Id { get; }
    string Category { get; }
    string Title { get; }
    string? Message { get; set; }
    ActivityStatus Status { get; set; }
    DateTimeOffset Timestamp { get; }
    string? IconGlyph { get; }
    bool IsRead { get; set; }
    bool IsPersistent { get; }
    IReadOnlyList<ActivityAction>? Actions { get; }
}

// ── Base: shared implementation ──

public abstract partial class ActivityItemBase : ObservableObject, IActivityItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Category { get; init; }
    public required string Title { get; init; }

    [ObservableProperty] private string? _message;
    [ObservableProperty] private ActivityStatus _status = ActivityStatus.Info;
    [ObservableProperty] private bool _isRead;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string? IconGlyph { get; init; }
    public virtual bool IsPersistent => false;
    public IReadOnlyList<ActivityAction>? Actions { get; init; }
}

// ── Concrete: progress-aware items (sync, download, import) ──

public sealed partial class ProgressActivityItem : ActivityItemBase
{
    [ObservableProperty] private double? _progress;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private TimeSpan? _estimatedTimeRemaining;

    public bool IsCancellable { get; init; }
    public Action? CancelAction { get; init; }
}

// ── Concrete: app notifications (release notes, errors) ──

public sealed partial class NotificationActivityItem : ActivityItemBase
{
    public override bool IsPersistent => true;
    public string? DetailUrl { get; init; }
}

// ── Concrete: Spotify content notifications (new releases, friend activity) ──

public sealed partial class SpotifyActivityItem : ActivityItemBase
{
    public string? ImageUrl { get; init; }
    public string? NavigationUri { get; init; }
}
