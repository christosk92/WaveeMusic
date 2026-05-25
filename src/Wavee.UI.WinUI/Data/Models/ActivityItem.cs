using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Data.Models;

// ── Status enum ──

public enum ActivityStatus { Info, InProgress, Completed, Failed }

public enum ActivityNotificationType { System, UserAction, Spotify }

public enum ActivityOutcome { None, Positive, Negative, Undo }

// ── Action model ──

public sealed record ActivityAction(string Label, string? IconGlyph, Func<Task> Callback);

public sealed record ActivityDetailRow(string Label, string Value);

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
    ActivityNotificationType ActivityType { get; }
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
    public virtual ActivityNotificationType ActivityType { get; init; } = ActivityNotificationType.System;
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
    public string? ImageUrl { get; init; }
    public string? NavigationUri { get; init; }
    public string? DetailTitle { get; init; }
    public string? DetailSubtitle { get; init; }
    public string? DetailBody { get; init; }
    public IReadOnlyList<ActivityDetailRow>? DetailRows { get; init; }
    public ActivityOutcome Outcome { get; init; }
    public string? OutcomeLabel { get; init; }

    public bool HasOutcome => Outcome != ActivityOutcome.None;

    public string OutcomeGlyph => Outcome switch
    {
        ActivityOutcome.Positive => FluentGlyphs.CheckMark,
        ActivityOutcome.Negative => FluentGlyphs.Remove,
        ActivityOutcome.Undo => FluentGlyphs.Undo,
        _ => ""
    };

    public bool HasNavigationTarget => !string.IsNullOrWhiteSpace(NavigationUri);

    public bool HasDetailContent =>
        !string.IsNullOrWhiteSpace(DetailTitle)
        || !string.IsNullOrWhiteSpace(DetailSubtitle)
        || !string.IsNullOrWhiteSpace(DetailBody)
        || DetailRows is { Count: > 0 };

    public bool HasDetails => HasDetailContent || HasNavigationTarget;

    public bool HasActionRow => HasDetailContent || Actions is { Count: > 0 };
}

// ── Concrete: Spotify content notifications (new releases, friend activity) ──

public sealed partial class SpotifyActivityItem : ActivityItemBase
{
    public override ActivityNotificationType ActivityType { get; init; } = ActivityNotificationType.Spotify;
    public string? ImageUrl { get; init; }
    public string? NavigationUri { get; init; }
}
