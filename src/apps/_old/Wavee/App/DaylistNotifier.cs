using System;
using FluentGpu.WindowsApi.Notifications;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// "Your daylist refreshed", scheduled with the OS. The rollover moment is KNOWN IN ADVANCE (the daylist card carries the
/// end of its own window), so this needs no polling and no background task — the same trick as a pre-save release drop.
/// </summary>
/// <remarks>
/// Fed from <c>HomeDaylistHydrator</c>, i.e. a data path that runs when the home feed resolves — never from a render.
/// The tag is keyed on the WINDOW, so learning the same window twice is idempotent while a genuinely new window replaces
/// the entry rather than stacking a second banner. Gated on the <see cref="NotifyTopic.DaylistRefresh"/> dial.
/// </remarks>
static class DaylistNotifier
{
    const string Group = "wavee.daylist";
    const string Tag = "daylist-roll";

    /// <summary>Windows silently drops a scheduled toast that is due immediately, and a rollover we learn about a second
    /// before it happens is not worth announcing anyway.</summary>
    static readonly TimeSpan MinLead = TimeSpan.FromMinutes(2);

    static readonly object Gate = new();
    static IAppSettings? _settings;
    static long _scheduledFor;      // the window end we currently hold a toast for (0 = none)

    /// <summary>Composition-root install (idempotent). Before this, <see cref="Note"/> is a no-op.</summary>
    public static void Attach(IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (Gate) _settings = settings;
        // The feed reports each hydrated daylist window through this hook; the hydrator itself knows nothing about toasts.
        Wavee.SpotifyLive.HomeDaylistHydrator.WindowObserved = Note;
    }

    /// <summary>The daylist's current window ends at <paramref name="expiresAtUnixMs"/>. Called whenever the feed
    /// hydrates a daylist card; cheap and idempotent for a window already scheduled.</summary>
    public static void Note(string? contextUri, long expiresAtUnixMs, string? title)
    {
        if (expiresAtUnixMs <= 0) return;
        IAppSettings? settings;
        lock (Gate)
        {
            if (_settings is null || _scheduledFor == expiresAtUnixMs) return;   // unchanged window: nothing to do
            settings = _settings;
        }
        if (!ToastNotifier.IsSupported) return;

        var policy = NotificationPrefs.Policy(settings);
        var level = NotificationPrefs.Level(settings, NotifyTopic.DaylistRefresh);
        var due = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs).ToLocalTime();

        // Not allowed (master off / dialled below Windows): give back anything we are holding, and remember nothing — so
        // turning the dial back up re-schedules from the next feed resolve.
        if (policy.ScheduleAt(level, due) is not { } deliver)
        {
            Unschedule();
            return;
        }
        if (deliver - DateTimeOffset.Now < MinLead) return;

        try
        {
            ToastNotifier.Default.Unschedule(Tag, Group);   // replace: a new window supersedes the old entry
            var toast = ToastBuilder.Create()
                .Title("Your daylist has refreshed")
                // The NEXT window's name is not knowable at schedule time (it is minted when it rolls), so the body names
                // the window that just ENDED rather than inventing one that might be wrong.
                .Body(title is { Length: > 0 } t ? "It moved on from " + t : "A new mix is waiting.")
                .Launch(contextUri is { Length: > 0 } uri
                    ? "wavee://open?route=pl&arg=" + Uri.EscapeDataString(uri)
                    : "wavee://open?route=home")
                .Tag(Tag).Group(Group);
            if (!policy.Sound) toast.Silent();

            if (ToastNotifier.Default.Schedule(toast, deliver, Tag, Group))
                lock (Gate) _scheduledFor = expiresAtUnixMs;
        }
        catch (Exception)
        {
            // Best effort: the next feed resolve tries again.
        }
    }

    /// <summary>Diagnostic seam for Settings ▸ Notifications ▸ Send event: pretend the current daylist window ends at
    /// <paramref name="due"/> and schedule through the REAL <see cref="Note"/> path. Returns the delivery instant the OS
    /// was asked for, or null when the dial would not deliver — deliberately checked HERE so a simulate never reaches
    /// <see cref="Note"/>'s disallowed branch, which REVOKES a genuinely pending real rollover toast.</summary>
    internal static DateTimeOffset? SimulateSchedule(DateTimeOffset due, string? contextUri, string? title)
    {
        IAppSettings? settings;
        lock (Gate) settings = _settings;
        if (settings is null || !ToastNotifier.IsSupported) return null;
        var policy = NotificationPrefs.Policy(settings);
        var level = NotificationPrefs.Level(settings, NotifyTopic.DaylistRefresh);
        if (policy.ScheduleAt(level, due) is not { } deliver) return null;

        long ms = due.ToUnixTimeMilliseconds();
        lock (Gate) { if (_scheduledFor == ms) ms++; }   // an identical window is a no-op in Note; nudge so a repeat press works
        Note(contextUri, ms, title);
        lock (Gate) return _scheduledFor == ms ? deliver : null;
    }

    /// <summary>Re-check the held entry against the current dials. Called when a notification setting changes. Can only
    /// ever REVOKE here: re-scheduling needs a window end, which arrives with the next feed resolve — so turning the dial
    /// back up quietly re-arms itself rather than guessing at a stale expiry.</summary>
    public static void RequestReconcile()
    {
        IAppSettings? settings;
        lock (Gate) settings = _settings;
        if (settings is null) return;
        var policy = NotificationPrefs.Policy(settings);
        if (policy.ScheduleAt(NotificationPrefs.Level(settings, NotifyTopic.DaylistRefresh), DateTimeOffset.Now) is null)
            Unschedule();
    }

    /// <summary>Drop the held entry (the dial went down, or sign-out). Never throws.</summary>
    public static void Unschedule()
    {
        lock (Gate)
        {
            if (_scheduledFor == 0) return;
            _scheduledFor = 0;
        }
        try { ToastNotifier.Default.Unschedule(Tag, Group); } catch (Exception) { }
    }
}
