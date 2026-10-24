// ── Theming/Contracts/NotificationsProps.cs — the props of `Overlay.Notifications` (themes plan 6.0, T11a) ─────────────────────
//
// THE NOTIFICATION PANEL. The bell and the account menu's row open ONE panel (`Shell.OpenNotificationPanel` owns the popup: placement, light
// dismiss, focus trap, the panel-opened demand and the close seam the verbs use). What goes in the popup is this key: the header, the filter
// pills, the pending-sync line and the feed's rows.
//
// WHAT STAYED CORE. The feed and its states (`Notify.Items`, `SocialState`, `ReleasesState`), the active filter and which rows pass it
// (`Notify.PassesFilter`), the empty line's key, the relative clock (the 30 s tick that re-renders the open panel), the expanded row, the
// update progress mirror, and every verb a click runs (`INotificationVerbs`: open a social or release row, run an update row's pill, mark all
// read, undo an activity). The pure decisions a picture asks for (`Notify.UpdateRow`, `Notify.RelativeAge`, `Notify.ReleaseTypeKey`, the
// update sentence) are `Notify` functions the view calls.
//
// WVTH006: a sealed record built per render by the core host (the host re-renders on the feed, the filter and the tick, exactly as the panel did).

using FluentGpu.Controls;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>Where an activity row's detail points: the target's words and, when the subject routes, the "Open" jump.</summary>
/// <param name="Text">The target line (the destination's title, or the subject's own text).</param>
/// <param name="Open">Go there and dismiss the panel, or null when the subject does not route.</param>
public readonly record struct NotificationActivityTarget(string Text, Action? Open);

/// <summary>What a notification row's clicks reach: the core's state and verbs.</summary>
public interface INotificationsHost
{
    /// <summary>The panel's verbs (open, run, mark, undo).</summary>
    INotificationVerbs Verbs { get; }

    /// <summary>Pick a filter pill (on the model, so a re-open keeps it).</summary>
    void SetFilter(NotifyFilter filter);

    /// <summary>An activity row was pressed: mark it read and expand it, or collapse it when it was the expanded one.</summary>
    void ToggleActivity(in Notification n);

    /// <summary>Undo an invertible activity row (a failure says so in a toast). Only called while <see cref="NotificationsProps.CanUndo"/>.</summary>
    void Undo(in Notification n);

    /// <summary>An activity row's detail target (the subject's words and its jump), or null when the row has no subject.</summary>
    NotificationActivityTarget? ActivityTarget(in Notification n);

    /// <summary>A new-release row's right-click menu (an album release answers with the container menu), or null when the row has none.</summary>
    Func<ContextMenuModel?>? ReleaseMenu(in Notification n);
}

/// <summary>The props of <see cref="ViewKeys.OverlayNotifications"/>.</summary>
public sealed record NotificationsProps
{
    /// <summary>The active filter pill.</summary>
    public required NotifyFilter Filter { get; init; }

    /// <summary>The rows that pass the filter, in feed order.</summary>
    public required IReadOnlyList<Notification> Rows { get; init; }

    /// <summary>"Now" in unix milliseconds: the relative times are measured against it.</summary>
    public required long Now { get; init; }

    /// <summary>The expanded activity row's id, or "".</summary>
    public required string ExpandedId { get; init; }

    /// <summary>The empty line (resolved), shown when no row passes.</summary>
    public required string EmptyText { get; init; }

    /// <summary>No row passes YET: a remote feed this filter shows is still loading (contract 1.59, audit B7). A structure draws the feed's skeleton at
    /// its loaded size instead of the <see cref="EmptyText"/> line ("Loading…"), so the rows landing replace bars in place.</summary>
    public bool Loading { get; init; }

    /// <summary>"Clear" for the activity filter, or null (no link).</summary>
    public Action? Clear { get; init; }

    /// <summary>An activity row offers Undo.</summary>
    public required bool CanUndo { get; init; }

    /// <summary>The update row's download fraction (bound).</summary>
    public required FloatSignal UpdateProgress { get; init; }

    /// <summary>Playlist changes still syncing (its own component subscribes, so the outbox draining never re-renders the feed).</summary>
    public required IReadSignal<int> PendingEdits { get; init; }

    /// <summary>The state and verbs the rows reach.</summary>
    public required INotificationsHost Host { get; init; }
}
