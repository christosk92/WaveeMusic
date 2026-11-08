// ── Shell/NotificationVerbs.cs — what a click in the notification panel DOES ────────────────────────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X21). `Shell.UI.cs` used to draw the panel's rows AND own their verbs
// (open the social target, open the release, run the update row's action, undo an activity, mark all read, close the panel
// after a navigation). The rows are still the view's; the verbs are here behind `INotificationVerbs`, so a theme that draws
// the panel differently calls the same behaviour.

using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>The notification panel's verbs.</summary>
public interface INotificationVerbs
{
    /// <summary>A social row: its target through <see cref="OpenLink"/>.</summary>
    void OpenSocial(Notification n);

    /// <summary>A new-release row: the album or episode opens its page.</summary>
    void OpenRelease(Notification n);

    /// <summary>Follow a link and dismiss the panel: in-app when it addresses a destination (a <c>spotify:</c> uri or an
    /// <c>open.spotify.com</c> link, <see cref="Shell.OpenLink"/>), else the browser (an entity with no page maps to its
    /// web page).</summary>
    void OpenLink(string? uri);

    /// <summary>An update row's pill: What's new navigates (and, on a completed update, acknowledges it); every other verb is the updater's.</summary>
    void RunUpdateRow(UpdateRowAction verb, AppUpdateSnapshot snapshot);

    /// <summary>"Mark all read" also clears the OS banners it acknowledges (G-090).</summary>
    void MarkAllRead();

    /// <summary>A routable activity detail's "Open": go there and dismiss the panel.</summary>
    void OpenRoute(in Shell.Route route);

    /// <summary>Undo a library mutation through the journal; a failure says so in a toast.</summary>
    Task UndoActivityAsync(Func<Notification, Task<bool>> undo, Notification n);

    /// <summary>A click that navigates away dismisses the panel (a floating panel over a new page reads as stuck).</summary>
    void ClosePanel();
}

/// <summary>The shell's implementation of <see cref="INotificationVerbs"/>.</summary>
public sealed class NotificationVerbs : INotificationVerbs
{
    /// <summary>The process-wide verbs the panel's rows call.</summary>
    public static INotificationVerbs Shared { get; } = new NotificationVerbs();

    /// <summary>The open panel's close action (null while closed), installed by <c>Shell.OpenNotificationPanel</c>.</summary>
    public static Action? PanelCloser { get; set; }

    string? _runningVersion;

    public void ClosePanel() => PanelCloser?.Invoke();

    public void OpenSocial(Notification n)
    {
        Notify.MarkRead(n.Id);
        Notify.Dismiss(n.Id);   // G-090: acting on the in-app row also pulls its live OS banner, if one is still up
        // Both action types: the feed hands out `https://open.spotify.com/…` targets as well as `spotify:` uris, and a web
        // target that names an entity is still that entity's page (the link door decides, never the spelling).
        OpenLink(n.ActionUri);
    }

    public void OpenRelease(Notification n)
    {
        Notify.MarkRead(n.Id);
        Notify.Dismiss(n.Id);   // G-090
        if (Shell.For(n.Subject, n.Title) is { IsNone: false } route)
        {
            Shell.GoTo(route);
            ClosePanel();
            return;
        }
        OpenLink(n.Subject.IsValid ? n.Subject.Text : null);
    }

    public void OpenLink(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (!Shell.OpenLink(uri))
        {
            // A Spotify uri with no in-app page (a search, a genre) is still a web page.
            if (!Shell.TryEntityOfLink(uri, out string entity)) return;
            string web = Actions.WebLinkOf(EntityUri.Parse(entity));
            if (web.Length == 0) return;
            Shell.OpenBrowser(web);
        }
        ClosePanel();
    }

    public void RunUpdateRow(UpdateRowAction verb, AppUpdateSnapshot snapshot)
    {
        if (verb != UpdateRowAction.WhatsNew)
        {
            Notify.UpdateCommand?.Invoke(verb, snapshot);
            return;
        }
        Shell.GoTo(Shell.Parse("whatsnew", Notify.NotesVersion(snapshot, RunningVersion())));
        ClosePanel();
        if (snapshot.State == AppUpdateState.Completed) Notify.UpdateCommand?.Invoke(UpdateRowAction.Dismiss, snapshot);
    }

    public void MarkAllRead()
    {
        Notify.MarkAllRead(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Notify.ClearLive();
    }

    public void OpenRoute(in Shell.Route route)
    {
        Shell.GoTo(route);
        ClosePanel();
    }

    public async Task UndoActivityAsync(Func<Notification, Task<bool>> undo, Notification n)
    {
        bool ok;
        try { ok = await undo(n).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("notify", "activity.undo failed", ex); ok = false; }
        // The toast is a UI-thread write; the journal's await may have resumed anywhere.
        if (!ok) Notify.ToUi(static () => Notify.Say(Loc.Get(Strings.Notifications.UndoFailed), InfoBarSeverity.Warning));
    }

    string RunningVersion()
        => _runningVersion ??= Notify.AppUpdateVersion.ReleaseTagVersion(typeof(Shell).Assembly.GetName().Version?.ToString());
}
