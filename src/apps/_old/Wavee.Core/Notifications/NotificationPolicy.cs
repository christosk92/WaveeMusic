using System;

namespace Wavee.Core;

/// <summary>How far a notification topic is allowed to travel. A LADDER, not a set of independent switches: a Windows
/// toast without the in-app record makes no sense (the centre is the durable log — a toast is a banner that disappears),
/// so <see cref="Windows"/> implies <see cref="InApp"/>. Values are PERSISTED — append only, never reorder.</summary>
public enum NotifyLevel : byte
{
    /// <summary>Not surfaced at all — not even in the notification centre.</summary>
    Off = 0,
    /// <summary>Recorded in the in-app notification centre (the bell). No OS banner.</summary>
    InApp = 1,
    /// <summary>In the centre AND raised as a Windows toast.</summary>
    Windows = 2,
}

/// <summary>The notification topics a user dials independently. Deliberately finer than
/// <see cref="NotificationCategory"/>, which is a display grouping: the centre's one "Spotify" pill lumps concerts and
/// followers together, and its "New" pill lumps albums with podcast episodes — the single biggest reason someone turns
/// the whole feature off instead of the one part that was too loud. Values are PERSISTED (settings keys derive from the
/// name) — append only, never reorder.</summary>
public enum NotifyTopic : byte
{
    /// <summary>A followed artist released an album or single.</summary>
    NewAlbums = 0,
    /// <summary>A followed show published an episode. Split from albums on purpose: a podcast listener gets an order of
    /// magnitude more of these, and one shared dial makes both unusable.</summary>
    NewEpisodes = 1,
    /// <summary>An album the user PRE-SAVED is out. Scheduled with the OS, so it arrives with Wavee closed.</summary>
    ReleaseDrops = 2,
    /// <summary>Live shows: both "new show announced near you" and "just days away". One dial because the feed does not
    /// reliably distinguish them — the only honest discriminator is the concert action target, not the (server-localized)
    /// title, so a split would be guesswork dressed up as a setting.</summary>
    Concerts = 3,
    /// <summary>Someone started following the user.</summary>
    Followers = 4,
    /// <summary>The daylist rolled over into its next window. Scheduled: the rollover time is known in advance.</summary>
    DaylistRefresh = 5,
    /// <summary>A Wavee update is available / installed.</summary>
    AppUpdates = 6,
    /// <summary>The local library-mutation log (saves, follows, playlist edits) — the Undo trail. In-app only by nature:
    /// it is a record of what the USER just did, so a banner telling them about it would be absurd.</summary>
    LibraryActivity = 7,
}

/// <summary>Quiet hours: a wall-clock window during which no Windows banner is raised. Half-open <c>[From, To)</c> in
/// LOCAL hours, and it may wrap midnight (22 → 8 is the common shape). <c>From == To</c> means "no quiet window"
/// rather than "always quiet", which is the safer reading of an accidental equal pair.</summary>
public readonly record struct QuietHours(bool Enabled, int FromHour, int ToHour)
{
    public static QuietHours Off => new(false, 22, 8);

    /// <summary>Clamp to legal hours so a corrupt settings file can never produce a window that swallows everything.</summary>
    public QuietHours Normalized() => new(Enabled, Wrap(FromHour), Wrap(ToHour));

    static int Wrap(int h) => h < 0 || h > 23 ? 0 : h;

    /// <summary>True when <paramref name="local"/> falls inside the quiet window.</summary>
    public bool Contains(DateTimeOffset local)
    {
        var q = Normalized();
        if (!q.Enabled || q.FromHour == q.ToHour) return false;
        int h = local.Hour;
        return q.FromHour < q.ToHour
            ? h >= q.FromHour && h < q.ToHour          // same-day window, e.g. 13 → 17
            : h >= q.FromHour || h < q.ToHour;         // wraps midnight, e.g. 22 → 8
    }

    /// <summary>The first instant at or after <paramref name="local"/> that is NOT quiet. Used to SHIFT a scheduled
    /// toast (a release drop, a daylist roll) out of the quiet window instead of dropping it: the album is still out,
    /// the user just hears about it at a civilised hour. Returns <paramref name="local"/> unchanged when not quiet.</summary>
    public DateTimeOffset NextAudible(DateTimeOffset local)
    {
        var q = Normalized();
        if (!q.Contains(local)) return local;
        // The window ends at ToHour on this day, or tomorrow when it wrapped past midnight.
        var endToday = new DateTimeOffset(local.Year, local.Month, local.Day, q.ToHour, 0, 0, local.Offset);
        return endToday > local ? endToday : endToday.AddDays(1);
    }
}

/// <summary>The whole notification dial-set: the per-topic ladder plus the two global gates. Pure and engine-free so the
/// rules are unit-testable without a shell, a toast platform or a clock.</summary>
public readonly record struct NotificationPolicy(bool WindowsEnabled, bool Sound, QuietHours Quiet)
{
    /// <summary>Every topic's default. In-app for everything the centre already shows today (so a fresh install behaves
    /// exactly as it did before this page existed), and <see cref="NotifyLevel.Windows"/> pre-selected for release drops
    /// — the one topic whose entire point is arriving when the app is closed. Nothing escalates until
    /// <see cref="WindowsEnabled"/> is turned on, so these defaults are a *shape*, not noise.</summary>
    public static NotifyLevel DefaultFor(NotifyTopic topic) => topic switch
    {
        NotifyTopic.ReleaseDrops => NotifyLevel.Windows,
        _ => NotifyLevel.InApp,
    };

    /// <summary>The highest level a topic can reach at all. <see cref="NotifyTopic.LibraryActivity"/> caps at
    /// <see cref="NotifyLevel.InApp"/> — the UI renders its dial with the Windows segment absent rather than present and
    /// dead, because an unreachable switch is worse than no switch.</summary>
    public static NotifyLevel CeilingFor(NotifyTopic topic) =>
        topic == NotifyTopic.LibraryActivity ? NotifyLevel.InApp : NotifyLevel.Windows;

    /// <summary>True when the topic is delivered by the OS at a scheduled time rather than while the app runs — which is
    /// what lets it arrive with Wavee CLOSED. The UI labels these, because "even when closed" is the property a user is
    /// actually shopping for.</summary>
    public static bool IsScheduled(NotifyTopic topic) =>
        topic is NotifyTopic.ReleaseDrops or NotifyTopic.DaylistRefresh;

    /// <summary>Clamp a stored level to what the topic supports (a settings file written by a newer build, or a topic
    /// whose ceiling dropped, must not resurrect an impossible level).</summary>
    public static NotifyLevel Clamp(NotifyTopic topic, NotifyLevel level)
    {
        var ceiling = CeilingFor(topic);
        if ((byte)level > (byte)ceiling) return ceiling;
        return level;
    }

    /// <summary>Should this topic appear in the in-app notification centre?</summary>
    public bool ShowsInApp(NotifyLevel level) => Clamp2(level) != NotifyLevel.Off;

    /// <summary>Should this topic raise a Windows banner NOW (at <paramref name="local"/>)? Requires the master gate,
    /// the topic dialled to Windows, and the moment to be outside quiet hours. Live escalation only — a SCHEDULED toast
    /// asks <see cref="ScheduleAt"/> instead, because its delivery moment is not now.</summary>
    public bool RaisesToastNow(NotifyLevel level, DateTimeOffset local) =>
        WindowsEnabled && Clamp2(level) == NotifyLevel.Windows && !Quiet.Contains(local);

    /// <summary>When a scheduled toast for <paramref name="due"/> should actually be handed to the OS, or null when it
    /// must not be scheduled at all. Quiet hours SHIFT rather than suppress: the release still happened.</summary>
    public DateTimeOffset? ScheduleAt(NotifyLevel level, DateTimeOffset due)
    {
        if (!WindowsEnabled || Clamp2(level) != NotifyLevel.Windows) return null;
        return Quiet.NextAudible(due);
    }

    static NotifyLevel Clamp2(NotifyLevel level) => (byte)level > 2 ? NotifyLevel.Windows : level;
}
