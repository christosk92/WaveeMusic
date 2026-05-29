using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Data.Models;

// â”€â”€ Status enum â”€â”€

public enum ActivityStatus { Info, InProgress, Completed, Failed }

public enum ActivityNotificationType { System, UserAction, Spotify }

public enum ActivityOutcome { None, Positive, Negative, Undo }

// â”€â”€ Action model â”€â”€
//
// `Callback` is intentionally INTERNAL because it's a Func<Task> — a delegate
// type that CsWinRT can't project across the WinRT ABI. If it were public,
// the CsWinRT AOT optimizer would refuse to generate the IBindableVector CCW
// for ActivityAction collections, breaking the ActivityBell's
// `ItemsSource="{x:Bind Actions}"` binding (manifests at runtime as
// `ArgumentException: 'source' is not a supported vector.`). Keeping Callback
// internal hides it from the CCW dispatch while still letting in-assembly
// code (ActivityService, NotificationActivityItem consumers) invoke it.

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ActivityAction
{
    public string Label { get; }
    public string? IconGlyph { get; }
    internal Func<Task> Callback { get; }

    public ActivityAction(string label, string? iconGlyph, Func<Task> callback)
    {
        Label = label;
        IconGlyph = iconGlyph;
        Callback = callback;
    }
}

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record ActivityDetailRow(string Label, string Value);

// â”€â”€ Category styling â”€â”€

public sealed record CategoryStyle(
    string CategoryId,
    string DisplayName,
    string DefaultIconGlyph,
    string AccentColorKey);

// â”€â”€ Interface: common contract for all activity items â”€â”€

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
    ObservableCollection<ActivityAction>? Actions { get; }
}

// â”€â”€ Base: shared implementation â”€â”€

[global::WinRT.GeneratedBindableCustomProperty]
public abstract partial class ActivityItemBase : ObservableObject, IActivityItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Category { get; init; }
    public required string Title { get; init; }

    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial ActivityStatus Status { get; set; } = ActivityStatus.Info;
    [ObservableProperty] public partial bool IsRead { get; set; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string? IconGlyph { get; init; }
    public virtual bool IsPersistent => false;
    public virtual ActivityNotificationType ActivityType { get; init; } = ActivityNotificationType.System;
    public ObservableCollection<ActivityAction>? Actions { get; init; }
}

// â”€â”€ Concrete: progress-aware items (sync, download, import) â”€â”€

public sealed partial class ProgressActivityItem : ActivityItemBase
{
    [ObservableProperty] public partial double? Progress { get; set; }
    [ObservableProperty] public partial string? ProgressText { get; set; }
    [ObservableProperty] public partial TimeSpan? EstimatedTimeRemaining { get; set; }

    public bool IsCancellable { get; init; }
    public Action? CancelAction { get; init; }
}

// â”€â”€ Concrete: app notifications (release notes, errors) â”€â”€

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

// â”€â”€ Concrete: Spotify content notifications (new releases, friend activity) â”€â”€

public sealed partial class SpotifyActivityItem : ActivityItemBase
{
    public override ActivityNotificationType ActivityType { get; init; } = ActivityNotificationType.Spotify;
    public string? ImageUrl { get; init; }
    public string? NavigationUri { get; init; }
}
