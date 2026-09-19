// ── Platform/Notify.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// NotifyLevel/NotifyTopic/QuietHours, NotificationPolicy, NotificationPrefs, the escalation plan, merge / filter /
// read-state, the notification table and its decode, and AppUpdateToasts (A8); ToastEscalator's decision half (A11)
//
// Role: CORE
// Owner: I
// Wave: 4
// Budget: 1000 lines
// Spec: ch 19 §9.4's ~1,000 CORE: ch 14 §9 (445: policy, prefs, AppUpdateToasts, the escalation plan) + ch 19 §9.4 (555: merge, read-ids, models, SpotifyNotifications, WhatsNew, the bridge's read-state half)
//
// ONE STACK, ONE OWNER (A9). The in-app panel is shell chrome and it shares the MODEL with the Action Center half, so
// both halves ship together: this file is the decisions, `Notify.Host.cs` is the WinRT. A notification is NOT an
// entity — its rows carry their own art and action uri, they are never handles — so there is no `Entities/Notification.*`
// pair (ch 19 §9.4 withdrew its own proposal in terms).
//
// ONE ENGINE REFERENCE, DELIBERATE (the open question ch 14 §8 and ch 19 §8 both record): `Prefs.Epoch` is a
// `FluentGpu.Signals.Signal<int>`, so "CORE" here means pure decisions + the state cells, not "no engine type at all".
// That is the same posture `Platform.SettingsChanged` already takes, and moving the cell to the host half would put
// the settings-page's re-read edge in a file the settings page cannot reference.

using System;
using System.Collections.Generic;
using System.Globalization;

using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

// ══ 1. THE LADDER, THE TOPICS AND THE QUIET WINDOW ══════════════════════════════════════════════════════════════════

/// <summary>How far a notification topic is allowed to travel. A LADDER, not a set of independent switches: a Windows
/// toast without the in-app record makes no sense (the centre is the durable log — a toast is a banner that
/// disappears), so <see cref="Windows"/> implies <see cref="InApp"/>. Values are PERSISTED — append only, never
/// reorder.</summary>
public enum NotifyLevel : byte
{
    /// <summary>Not surfaced at all — not even in the notification centre.</summary>
    Off = 0,
    /// <summary>Recorded in the in-app notification centre (the bell). No OS banner.</summary>
    InApp = 1,
    /// <summary>In the centre AND raised as a Windows toast.</summary>
    Windows = 2,
}

/// <summary>The topics a user dials independently. Deliberately FINER than <see cref="NotifyCategory"/>, which is a
/// display grouping: the centre's one "Spotify" pill lumps concerts and followers together, and its "New" pill lumps
/// albums with podcast episodes — the single biggest reason someone turns the whole feature off instead of the one
/// part that was too loud. Values are PERSISTED (settings keys derive from the name) — append only.</summary>
public enum NotifyTopic : byte
{
    /// <summary>A followed artist released an album or single.</summary>
    NewAlbums = 0,
    /// <summary>A followed show published an episode. Split from albums on purpose: a podcast listener gets an order
    /// of magnitude more of these, and one shared dial makes both unusable.</summary>
    NewEpisodes = 1,
    /// <summary>An album the user PRE-SAVED is out. Scheduled with the OS, so it arrives with Wavee closed.</summary>
    ReleaseDrops = 2,
    /// <summary>Live shows: both "new show announced near you" and "just days away". ONE dial, because the feed does
    /// not reliably distinguish them — the only honest discriminator is the concert action target, not the
    /// server-localized title, so a split would be guesswork dressed up as a setting.</summary>
    Concerts = 3,
    /// <summary>Someone started following the user.</summary>
    Followers = 4,
    /// <summary>The daylist rolled over into its next window. Scheduled: the rollover time is known in advance.</summary>
    DaylistRefresh = 5,
    /// <summary>A Wavee update is available / installed.</summary>
    AppUpdates = 6,
    /// <summary>The local library-mutation log (saves, follows, playlist edits) — the Undo trail. In-app only BY
    /// NATURE: it is a record of what the USER just did, so a banner telling them about it would be absurd.</summary>
    LibraryActivity = 7,
}

/// <summary>Quiet hours: a wall-clock window during which no Windows banner is raised. Half-open <c>[From, To)</c> in
/// LOCAL hours, and it may WRAP midnight (22 → 8 is the common shape). <c>From == To</c> means "no quiet window"
/// rather than "always quiet", which is the safer reading of an accidental equal pair.</summary>
public readonly record struct QuietHours(bool Enabled, int FromHour, int ToHour)
{
    public static QuietHours Off => new(false, 22, 8);

    /// <summary>Clamp to legal hours so a corrupt settings file can never produce a window that swallows
    /// everything.</summary>
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
    /// the user just hears about it at a civilised hour. Returns <paramref name="local"/> unchanged when not
    /// quiet.</summary>
    public DateTimeOffset NextAudible(DateTimeOffset local)
    {
        var q = Normalized();
        if (!q.Contains(local)) return local;
        // The window ends at ToHour on this day, or tomorrow when it wrapped past midnight.
        var endToday = new DateTimeOffset(local.Year, local.Month, local.Day, q.ToHour, 0, 0, local.Offset);
        return endToday > local ? endToday : endToday.AddDays(1);
    }
}

/// <summary>The whole dial-set: the per-topic ladder plus the two global gates. Pure and engine-free so the rules are
/// unit-testable without a shell, a toast platform or a clock.</summary>
public readonly record struct NotificationPolicy(bool WindowsEnabled, bool Sound, QuietHours Quiet)
{
    /// <summary>Every topic's default. In-app for everything the centre already shows (so a fresh install behaves
    /// exactly as it did before the settings page existed), and <see cref="NotifyLevel.Windows"/> pre-selected for
    /// release drops — the one topic whose entire point is arriving when the app is closed. Nothing escalates until
    /// <see cref="WindowsEnabled"/> is turned on, so these defaults are a SHAPE, not noise.</summary>
    public static NotifyLevel DefaultFor(NotifyTopic topic) => topic switch
    {
        NotifyTopic.ReleaseDrops => NotifyLevel.Windows,
        _ => NotifyLevel.InApp,
    };

    /// <summary>The highest level a topic can reach at all. <see cref="NotifyTopic.LibraryActivity"/> caps at
    /// <see cref="NotifyLevel.InApp"/> — the UI renders its dial with the Windows segment ABSENT rather than present
    /// and dead, because an unreachable switch is worse than no switch.</summary>
    public static NotifyLevel CeilingFor(NotifyTopic topic)
        => topic == NotifyTopic.LibraryActivity ? NotifyLevel.InApp : NotifyLevel.Windows;

    /// <summary>True when the topic is delivered by the OS at a SCHEDULED time rather than while the app runs — which
    /// is what lets it arrive with Wavee CLOSED. The UI labels these, because "even when closed" is the property a
    /// user is actually shopping for.</summary>
    public static bool IsScheduled(NotifyTopic topic)
        => topic is NotifyTopic.ReleaseDrops or NotifyTopic.DaylistRefresh;

    /// <summary>Clamp a stored level to what the topic supports (a settings file written by a newer build, or a topic
    /// whose ceiling dropped, must not resurrect an impossible level).</summary>
    public static NotifyLevel Clamp(NotifyTopic topic, NotifyLevel level)
    {
        var ceiling = CeilingFor(topic);
        return (byte)level > (byte)ceiling ? ceiling : level;
    }

    /// <summary>Should this topic appear in the in-app notification centre?</summary>
    public bool ShowsInApp(NotifyLevel level) => Clamp2(level) != NotifyLevel.Off;

    /// <summary>Should this topic raise a Windows banner NOW (at <paramref name="local"/>)? Requires the master gate,
    /// the topic dialled to Windows, and the moment to be outside quiet hours. LIVE escalation only — a SCHEDULED
    /// toast asks <see cref="ScheduleAt"/> instead, because its delivery moment is not now.</summary>
    public bool RaisesToastNow(NotifyLevel level, DateTimeOffset local)
        => WindowsEnabled && Clamp2(level) == NotifyLevel.Windows && !Quiet.Contains(local);

    /// <summary>When a scheduled toast for <paramref name="due"/> should actually be handed to the OS, or null when it
    /// must not be scheduled at all. Quiet hours SHIFT rather than suppress: the release still happened.</summary>
    public DateTimeOffset? ScheduleAt(NotifyLevel level, DateTimeOffset due)
    {
        if (!WindowsEnabled || Clamp2(level) != NotifyLevel.Windows) return null;
        return Quiet.NextAudible(due);
    }

    static NotifyLevel Clamp2(NotifyLevel level) => (byte)level > 2 ? NotifyLevel.Windows : level;
}

// ══ 2. THE ROW MODEL ════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The four categories the centre aggregates — the filter pills key on these.</summary>
public enum NotifyCategory : byte { AppUpdate, Social, NewRelease, Activity }

/// <summary>How a social notification's click resolves: an in-app route, or an external web page.</summary>
public enum SocialActionType : byte { Navigate, NavigateWebview }

/// <summary>A "What's New" item's shape.</summary>
public enum NewReleaseKind : byte { Album, Episode }

/// <summary>ONE notification-centre row. 0.2.9 modelled these as four abstract-record subclasses; flattening them to a
/// single readonly struct costs one wide record and buys a merge that allocates ONE list of values rather than four
/// lists of objects plus a `with`-copy per row. The discriminator is <see cref="Category"/>; the UI switches on it.
/// <para><b>Presentation lives ONLY in the UI</b> — the domain carries data. The one exception the wire forces is
/// <see cref="Title"/>, which for a social row IS a finished, server-localized sentence.</para></summary>
/// <param name="Id">Stable per row. The escalator's toast tag and the read-id set both key on it.</param>
/// <param name="TimestampMs">Unix ms. The app-update row pins <see cref="NotifyRows.UpdatePin"/> so it sorts FIRST —
/// a display concern, not a time, which is why the escalator folds that exact sentinel back to "now".</param>
/// <param name="Subject">The entity a new-release row is about. Default for the others.</param>
public readonly record struct Notification(
    string Id,
    long TimestampMs,
    bool IsUnread,
    NotifyCategory Category,
    string Title = "",
    string Body = "",
    string? ImageUrl = null,
    string? ActionUri = null,
    EntityUri Subject = default,
    NewReleaseKind ReleaseKind = NewReleaseKind.Album,
    string? Creator = null,
    string? AlbumType = null,
    bool Played = false,
    SocialActionType ActionType = SocialActionType.Navigate,
    string? WireType = null,
    string? ActName = null,
    AppUpdateSnapshot? Update = null);

/// <summary>Row constructors + the one sentinel. Kept beside the struct so nobody re-invents the update pin.</summary>
public static class NotifyRows
{
    /// <summary>The app-update row's timestamp: it must sort to the TOP of the centre and it is not a time.</summary>
    public const long UpdatePin = long.MaxValue;

    /// <summary>The app-update row's stable id.</summary>
    public const string UpdateId = "update";

    public static Notification ForUpdate(AppUpdateSnapshot snapshot, bool isUnread)
        => new(UpdateId, UpdatePin, isUnread, NotifyCategory.AppUpdate, Update: snapshot);

    public static Notification ForSocial(string id, long timestampMs, bool isUnread, string title,
        string? actionUri, SocialActionType actionType, string? imageUrl, string? actName, string? wireType)
        => new(id, timestampMs, isUnread, NotifyCategory.Social, Title: title, ImageUrl: imageUrl,
            ActionUri: actionUri, ActionType: actionType, WireType: wireType, ActName: actName);

    public static Notification ForRelease(string id, long timestampMs, bool isUnread, NewReleaseKind kind,
        EntityUri uri, string name, string? imageUrl, string creator, string? albumType, bool played)
        => new(id, timestampMs, isUnread, NotifyCategory.NewRelease, Title: name, ImageUrl: imageUrl,
            Subject: uri, ReleaseKind: kind, Creator: creator, AlbumType: albumType, Played: played);

    /// <summary>A local library-mutation entry — the Undo trail. The activity JOURNAL itself is `Entities/Store.cs`'s
    /// (ch 19 §7 DATA GAP 3); this only shapes the row.</summary>
    public static Notification ForActivity(string id, long timestampMs, bool isUnread, string title, string body,
        EntityUri subject)
        => new("act:" + id, timestampMs, isUnread, NotifyCategory.Activity, Title: title, Body: body, Subject: subject);
}

// ══ 3. THE SPOTIFY-FEED CLASSIFIER ══════════════════════════════════════════════════════════════════════════════════

/// <summary>What a Spotify-category (gander) row actually IS, as far as any surface outside the centre may care. The
/// feed's own taxonomy is a display CATEGORY (Social holds followers, concert announcements and generic announcements
/// alike), so a surface that wants only the live-event items has to gate on something concrete — never on the pill's
/// label.</summary>
public enum SpotifyUpdateKind : byte
{
    /// <summary>A follower, a generic announcement, anything unrecognised. Never leaves the centre.</summary>
    Other,
    /// <summary>A concert / live-show announcement, or a "days away" reminder for one.</summary>
    Concert,
}

/// <summary>The pure classifier + text hygiene for social rows. Engine-free and side-effect-free so the centre and
/// Home's timeline share ONE answer to "is this a concert announcement".</summary>
public static class SpotifyUpdates
{
    /// <summary>Classify one row. The server's own discriminator wins when it shipped one; otherwise the ACTION TARGET
    /// decides, because a concert announcement is the only Social row whose click resolves to a concert entity.
    /// Deliberately NOT derived from the title: the title is server-localized prose.</summary>
    public static SpotifyUpdateKind KindOf(in Notification n)
    {
        if (n.Category != NotifyCategory.Social) return SpotifyUpdateKind.Other;
        if (IsConcertWireType(n.WireType)) return SpotifyUpdateKind.Concert;
        return IsConcertTarget(n.ActionUri) ? SpotifyUpdateKind.Concert : SpotifyUpdateKind.Other;
    }

    public static bool IsConcert(in Notification n) => KindOf(in n) == SpotifyUpdateKind.Concert;

    /// <summary>The feed's optional per-item discriminator. Matched on CONCERT/LIVE only — both are concrete; a
    /// generic "EVENT" bucket is not, and a false positive here puts a follower row on Home.</summary>
    public static bool IsConcertWireType(string? wireType)
        => wireType is { Length: > 0 } t
           && (t.Contains("CONCERT", StringComparison.OrdinalIgnoreCase)
               || t.Contains("LIVE", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when an action target is a concert entity — the <c>spotify:concert:</c> uri form, or the web
    /// forms the feed uses when it hands out a webview target.</summary>
    public static bool IsConcertTarget(string? uri)
    {
        if (uri is not { Length: > 0 }) return false;
        if (uri.Contains(":concert:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Contains("concerts.spotify.com", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("/concert/", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("/concerts/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The title with any LEADING decorative glyphs removed ("⏰ Just days away: …" → "Just days away: …"). A
    /// row that carries its own KIND badge does not need the feed's emoji to say what it is, and the glyph throws the
    /// row's optical left edge out against its neighbours.
    /// <para>Casing is left ALONE: the string is server-localized prose, and a case transform over a localized string
    /// mangles Turkish dotted i and expands German ß.</para>
    /// <para>Never returns empty: a title that is nothing but glyphs is returned verbatim, because a blank row is
    /// worse than an emoji.</para>
    /// <para><b>0.3 applies it on BOTH surfaces</b> (ch 14 DATA GAP 11): 0.2.9's only caller was Home's timeline, so
    /// the same event read "🎵 New Keenan Te show…" as a Windows banner and "New Keenan Te show…" on Home.</para></summary>
    public static string CleanTitle(string? title)
    {
        if (title is not { Length: > 0 }) return "";
        int i = 0;
        while (i < title.Length)
        {
            char c = title[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int step = char.IsHighSurrogate(c) && i + 1 < title.Length && char.IsLowSurrogate(title[i + 1]) ? 2 : 1;
            var cat = CharUnicodeInfo.GetUnicodeCategory(title, i);
            if (cat is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                i += step;
                continue;
            }
            break;
        }
        if (i == 0) return title;                       // the common case allocates nothing
        var rest = title[i..];
        return rest.Length == 0 ? title : rest;
    }
}

// ══ 4. THE APP-UPDATE MODEL (A8 — it travels with the plan table and is worthless apart from it) ════════════════════

/// <summary>The app-update lifecycle. <see cref="None"/> = nothing pending; <see cref="Snoozed"/> = an update the user
/// pressed "Later" on (still available, no longer shouting); <see cref="Completed"/> = this launch followed an
/// update.</summary>
public enum AppUpdateState : byte { None, Checking, Available, Snoozed, Downloading, Installing, Completed, Failed }

/// <summary>Why an attempt failed, in the terms the UI needs to offer a NEXT STEP (retry, unmeter, close the helper
/// processes, open the release page).</summary>
public enum AppUpdateFailureKind : byte
{
    Network, Metered, PackagesInUse, VersionConflict, SideloadPolicy, AppInstallerOutdated, NotAssociated, Unknown,
}

/// <summary>Who asked for a feed check.</summary>
public enum UpdateCheckOrigin : byte { Scheduled, User }

/// <summary>One failure, carrying the raw HRESULT for diagnostics alongside the classified kind.</summary>
public sealed record AppUpdateFailure(AppUpdateFailureKind Kind, int HResult, string Message);

/// <summary>One immutable observation. Published WHOLE so the UI never reads a torn state.</summary>
/// <param name="State">Where the lifecycle stands.</param>
/// <param name="TargetQuad">Feed root version while pending; the running quad when Completed.</param>
/// <param name="TargetSemVer">From the notes index when known ("0.3.0").</param>
/// <param name="TargetCodename">"Crest" when known.</param>
/// <param name="ProgressPercent">0..100 while Downloading/Installing.</param>
/// <param name="Failure">Set only in <see cref="AppUpdateState.Failed"/>.</param>
/// <param name="AutoUpdateAssociated">The OS knows the feed (packaged only).</param>
/// <param name="LastCheckedMs">Unix ms of the last completed feed check, 0 when never.</param>
/// <param name="Quiet">A Failed that must NOT raise a toast: a SCHEDULED check that could not reach the feed. The
/// failure is still the state (Settings ▸ About shows it, with Retry); only the interruption is withheld — a
/// background poll on a flaky link is not the user's problem to dismiss. A user-initiated check or any APPLY failure
/// is never quiet.</param>
public sealed record AppUpdateSnapshot(
    AppUpdateState State,
    string? TargetQuad,
    string? TargetSemVer,
    string? TargetCodename,
    int ProgressPercent,
    AppUpdateFailure? Failure,
    bool AutoUpdateAssociated,
    long LastCheckedMs,
    bool Quiet = false)
{
    /// <summary>Nothing pending, nothing known — the state every process starts in.</summary>
    public static readonly AppUpdateSnapshot Idle = new(AppUpdateState.None, null, null, null, 0, null, false, 0);
}

/// <summary>What a toast may OFFER for an update. The host turns the FIRST of these into the toast's single action
/// button; the notification-centre row renders all of them.</summary>
public enum ToastActionKind : byte
{
    /// <summary>Start the download + stage + restart (packaged) / open the release page (unpackaged).</summary>
    UpdateNow,
    /// <summary>Navigate to the What's-new page.</summary>
    WhatsNew,
    /// <summary>Snooze this exact version.</summary>
    Later,
    /// <summary>Try the failed apply again.</summary>
    Retry,
    /// <summary>Open the release page — the escape hatch when deployment cannot work here.</summary>
    OpenReleasePage,
}

/// <summary>One PLANNED toast: already-localized text, the severity, whether it sticks, and the actions to offer.</summary>
/// <param name="Title">The header line ("" when the body carries everything).</param>
/// <param name="Body">The message.</param>
/// <param name="Severity">The shared info-bar severity palette itself — not a copy — so the toast, the in-app update
/// card and every info bar cannot drift, and <c>Notify.Say</c> takes the value straight through.</param>
/// <param name="Sticky">True ⇒ no auto-dismiss (a download in flight, a failure the user must answer).</param>
/// <param name="Actions">Offered actions, most important first.</param>
public readonly record struct ToastPlan(
    string Title,
    string Body,
    InfoBarSeverity Severity,
    bool Sticky,
    ToastActionKind[] Actions);

/// <summary>The notification panel's five filter pills, in the order the panel lists them.</summary>
public enum NotifyFilter : byte { All, Updates, Spotify, New, Activity }

/// <summary>How a row's relative time reads.</summary>
public enum RelativeUnit : byte { Now, Minutes, Hours, Days }

/// <summary>The update row's glyph chip, by state (the UI maps it to a glyph + tint).</summary>
public enum UpdateRowGlyph : byte { Download, Refresh, Success, Error, Info }

/// <summary>The update verbs the panel row and the in-app update toast route through <c>Notify.UpdateCommand</c>.
/// <see cref="Dismiss"/> is the row's own verb (acknowledge; the toast never offers it); <see cref="OpenReleasePage"/>
/// is the toast's escape hatch when deployment cannot work here (the row never offers it).</summary>
public enum UpdateRowAction : byte { UpdateNow, WhatsNew, Later, Retry, Dismiss, OpenReleasePage }

/// <summary>One state's update row: glyph, title loc key ("" for the no-copy arm), whether the progress strip replaces
/// the buttons, and the buttons (the FIRST is the accent one).</summary>
public readonly record struct UpdateRowArm(UpdateRowGlyph Glyph, string TitleLocKey, bool ShowsProgress,
    UpdateRowAction[] Actions);

public static partial class Notify
{
    // ══ 5. NOTIFICATION PREFS — settings ⇄ topics ═══════════════════════════════════════════════════════════════════

    /// <summary>The ONE place settings become a <see cref="NotificationPolicy"/> + per-topic <see cref="NotifyLevel"/>.
    /// Every consumer (the centre's in-app filter, the live escalator, the scheduled-drop notifier, the settings tab)
    /// reads through here, so "is this topic allowed to make a noise" has exactly one answer.</summary>
    public static class Prefs
    {
        /// <summary>Bumped on every write so mounted surfaces (the settings tab, the centre) re-read.</summary>
        public static readonly Signal<int> Epoch = new(0);

        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        /// <summary>Every topic, in the order the settings page lists them. DECLARATION ORDER IS THE UI ORDER — one
        /// list, so a new topic cannot be added to the enum and forgotten in the UI.</summary>
        public static readonly NotifyTopic[] AllTopics =
        [
            NotifyTopic.NewAlbums,
            NotifyTopic.NewEpisodes,
            NotifyTopic.ReleaseDrops,
            NotifyTopic.Concerts,
            NotifyTopic.Followers,
            NotifyTopic.DaylistRefresh,
            NotifyTopic.AppUpdates,
            NotifyTopic.LibraryActivity,
        ];

        static SettingKey<int> KeyFor(NotifyTopic topic) => topic switch
        {
            NotifyTopic.NewAlbums => Platform.Keys.NotifyNewAlbums,
            NotifyTopic.NewEpisodes => Platform.Keys.NotifyNewEpisodes,
            NotifyTopic.ReleaseDrops => Platform.Keys.NotifyReleaseDrops,
            NotifyTopic.Concerts => Platform.Keys.NotifyConcerts,
            NotifyTopic.Followers => Platform.Keys.NotifyFollowers,
            NotifyTopic.DaylistRefresh => Platform.Keys.NotifyDaylist,
            NotifyTopic.AppUpdates => Platform.Keys.NotifyAppUpdates,
            _ => Platform.Keys.NotifyLibraryActivity,
        };

        /// <summary>The stored level for <paramref name="topic"/>, CLAMPED to what the topic can actually reach.</summary>
        public static NotifyLevel Level(NotifyTopic topic)
        {
            int raw = Platform.Settings.Get(KeyFor(topic));
            var level = raw is >= 0 and <= 2 ? (NotifyLevel)raw : NotificationPolicy.DefaultFor(topic);
            return NotificationPolicy.Clamp(topic, level);
        }

        public static void SetLevel(NotifyTopic topic, NotifyLevel level)
        {
            Platform.Settings.Set(KeyFor(topic), (int)NotificationPolicy.Clamp(topic, level));
            Bump();
        }

        /// <summary>The global gates.</summary>
        public static NotificationPolicy Policy()
            => new(Platform.Settings.Get(Platform.Keys.NotifyWindows),
                   Platform.Settings.Get(Platform.Keys.NotifySound),
                   new QuietHours(
                       Platform.Settings.Get(Platform.Keys.NotifyQuietEnabled),
                       Platform.Settings.Get(Platform.Keys.NotifyQuietFromHour),
                       Platform.Settings.Get(Platform.Keys.NotifyQuietToHour)).Normalized());

        /// <summary>Which categories the in-app centre may surface, derived from the per-topic dials. A category stays
        /// visible while ANY of its topics is above Off — the pills are coarser than the dials, so hiding "Spotify"
        /// because followers were silenced would also hide the concerts the user still wants.</summary>
        public static bool ShowsCategory(NotifyCategory category) => category switch
        {
            NotifyCategory.NewRelease => Level(NotifyTopic.NewAlbums) != NotifyLevel.Off
                                      || Level(NotifyTopic.NewEpisodes) != NotifyLevel.Off,
            NotifyCategory.Social => Level(NotifyTopic.Concerts) != NotifyLevel.Off
                                  || Level(NotifyTopic.Followers) != NotifyLevel.Off,
            NotifyCategory.AppUpdate => Level(NotifyTopic.AppUpdates) != NotifyLevel.Off,
            _ => Level(NotifyTopic.LibraryActivity) != NotifyLevel.Off,
        };

        /// <summary>The topic a concrete row belongs to — the FINE answer the display category cannot give (its "New"
        /// pill covers albums AND episodes; its "Spotify" pill covers concerts AND followers).</summary>
        public static NotifyTopic TopicOf(in Notification n) => n.Category switch
        {
            NotifyCategory.NewRelease => n.ReleaseKind == NewReleaseKind.Episode
                ? NotifyTopic.NewEpisodes
                : NotifyTopic.NewAlbums,
            NotifyCategory.Social => SpotifyUpdates.IsConcert(in n) ? NotifyTopic.Concerts : NotifyTopic.Followers,
            NotifyCategory.AppUpdate => NotifyTopic.AppUpdates,
            _ => NotifyTopic.LibraryActivity,
        };
    }

    // ══ 6. THE PER-ITEM READ SET ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The PER-ITEM half of the centre's read state, as a set codec over ONE persisted string.
    /// <para>The remote feeds have no server mark-read endpoint, so read state is local. The other half is a
    /// WATERMARK per feed ("everything at or before this instant is seen"), advanced by Mark-all-read — all a panel
    /// that marks everything on open ever needed. A surface that marks ONE row seen (Home's timeline) needs this half,
    /// and it must be the SAME store, not a second one: whatever marks a row read here is what both the panel and the
    /// bell badge read back.</para>
    /// <para>Bounded by construction: trimmed to <see cref="Cap"/> ids, oldest-first, and CLEARED whenever the
    /// watermark advances (a mark-all subsumes every individual id), so it cannot grow without limit.</para>
    /// <para>Scans in place, no split and no allocation on the read path. The same codec shape as
    /// <c>Shell.TipsCore</c>, deliberately NOT shared with it: a tip id is a dotted ASCII constant this app authored,
    /// while these are opaque SERVER ids and need the separator guard + the cap.</para></summary>
    public static class ReadIds
    {
        /// <summary>The set separator. An id containing it is REFUSED by <see cref="Add"/> rather than corrupting the
        /// set.</summary>
        public const char Separator = '\n';

        /// <summary>How many individually-marked ids survive. Well past a session's worth of clicks on a 20-item feed.</summary>
        public const int Cap = 200;

        public static bool Contains(string? set, string? id)
        {
            if (string.IsNullOrEmpty(set) || string.IsNullOrEmpty(id)) return false;
            int i = 0;
            while (i <= set.Length)
            {
                int end = set.IndexOf(Separator, i);
                if (end < 0) end = set.Length;
                if (end - i == id.Length && string.CompareOrdinal(set, i, id, 0, id.Length) == 0) return true;
                i = end + 1;
            }
            return false;
        }

        /// <summary>The set with <paramref name="id"/> added — idempotent, append-ordered, trimmed to
        /// <see cref="Cap"/> from the OLDEST end. An empty id, or one containing the separator, is refused.</summary>
        public static string Add(string? set, string? id)
        {
            if (id is not { Length: > 0 } || id.IndexOf(Separator) >= 0) return set ?? "";
            if (Contains(set, id)) return set!;
            return Trim(string.IsNullOrEmpty(set) ? id : set + Separator + id);
        }

        /// <summary>The ids in stored order, empty segments dropped. For tests and diagnostics.</summary>
        public static List<string> Parse(string? set)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(set)) return ids;
            int i = 0;
            while (i <= set.Length)
            {
                int end = set.IndexOf(Separator, i);
                if (end < 0) end = set.Length;
                if (end > i) ids.Add(set.Substring(i, end - i));
                i = end + 1;
            }
            return ids;
        }

        /// <summary>Drop the oldest ids until at most <see cref="Cap"/> remain. Returns the input when it fits.</summary>
        public static string Trim(string? set)
        {
            if (string.IsNullOrEmpty(set)) return "";
            int count = 1;
            for (int i = 0; i < set.Length; i++) if (set[i] == Separator) count++;
            if (count <= Cap) return set;

            int drop = count - Cap;
            int cut = 0;
            for (int i = 0; i < drop; i++)
            {
                int end = set.IndexOf(Separator, cut);
                if (end < 0) return "";
                cut = end + 1;
            }
            return set[cut..];
        }
    }

    // ══ 7. THE MERGE ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The merged feed + the bell's count, as one value. Both are computed at REBUILD time and published
    /// together — no surface re-counts and nothing probes a store per render.</summary>
    public readonly record struct Feed(IReadOnlyList<Notification> Items, int Unread)
    {
        public static Feed Empty => new([], 0);
    }

    /// <summary>Merge the four category snapshots into ONE newest-first list plus the total unread count, applying the
    /// remote feeds' last-seen watermarks AND the per-item read set.
    /// <para><b>The unread rule, written ONCE.</b> Local activity stays visible (and may keep its unread dot inside
    /// the panel) but is INFORMATIONAL: routine actions such as liking a song must not increase the attention-seeking
    /// bell badge. 0.2.9 had this rule in the merge and a SECOND recount in the topic-visibility pass that did not
    /// reproduce it (ch 19 §7), so the badge disagreed with itself the moment anything was silenced. Here the topic
    /// filter runs INSIDE the merge and the count is produced by the same loop.</para></summary>
    /// <param name="update">The app-update row, or null when there is nothing pending.</param>
    /// <param name="social">The gander feed, any order.</param>
    /// <param name="ganderSeenMs">Mark-all-read watermark for the gander feed.</param>
    /// <param name="releases">The what's-new feed, any order.</param>
    /// <param name="whatsNewSeenMs">Mark-all-read watermark for the what's-new feed.</param>
    /// <param name="activity">The local mutation journal, any order.</param>
    /// <param name="readIds">The per-item read set.</param>
    /// <param name="topicVisible">Which topics the dials allow into the centre. Null ⇒ everything (a test, or a
    /// surface that has already filtered).</param>
    public static Feed Merge(
        Notification? update,
        IReadOnlyList<Notification> social, long ganderSeenMs,
        IReadOnlyList<Notification> releases, long whatsNewSeenMs,
        IReadOnlyList<Notification> activity,
        string? readIds = null,
        Func<NotifyTopic, bool>? topicVisible = null)
    {
        var list = new List<Notification>(
            (update is null ? 0 : 1) + social.Count + releases.Count + activity.Count);

        if (update is { } u && Allowed(u, topicVisible)) list.Add(u);
        for (int i = 0; i < social.Count; i++)
        {
            var s = social[i];
            if (!Allowed(s, topicVisible)) continue;
            list.Add(s with { IsUnread = s.IsUnread && s.TimestampMs > ganderSeenMs && !ReadIds.Contains(readIds, s.Id) });
        }
        for (int i = 0; i < releases.Count; i++)
        {
            var n = releases[i];
            if (!Allowed(n, topicVisible)) continue;
            list.Add(n with { IsUnread = n.IsUnread && n.TimestampMs > whatsNewSeenMs && !ReadIds.Contains(readIds, n.Id) });
        }
        for (int i = 0; i < activity.Count; i++)
        {
            var a = activity[i];
            if (Allowed(a, topicVisible)) list.Add(a);
        }

        list.Sort(static (a, b) => b.TimestampMs.CompareTo(a.TimestampMs));

        int unread = 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i].IsUnread && list[i].Category != NotifyCategory.Activity) unread++;
        return new Feed(list, unread);
    }

    static bool Allowed(in Notification n, Func<NotifyTopic, bool>? topicVisible)
        => topicVisible is null || topicVisible(Prefs.TopicOf(in n));

    // ══ 8. THE ESCALATION PLAN (A11 — the DECISION half; the WinRT half is Notify.Host.cs) ══════════════════════════

    /// <summary>The escalation policy, extracted so the watermark, the cap and the sentinel fold stop being
    /// untestable. 0.2.9 had every one of them inline in a WinRT-bound static and NONE of them had a test — and the
    /// sentinel fold is the exact bug that once produced a banner storm.
    /// <para><b>The watermark is the whole design.</b> It holds the newest timestamp already raised. Without it every
    /// rebuild — and every relaunch — re-toasts the entire feed, the loudest possible bug in a notification system.
    /// It advances past everything CONSIDERED, not just what was raised, so a topic the user has since silenced cannot
    /// come back as a backlog the moment they re-enable it. That is why the newest timestamp is found in a FIRST pass
    /// over everything and written unconditionally at the end; fusing the two loops IS the bug.</para></summary>
    public static class Escalation
    {
        /// <summary>At most this many individual banners per rebuild; the remainder collapse into ONE summary toast.
        /// The Action Center is not a log — the in-app centre is, and it already has every row.</summary>
        public const int MaxPerRebuild = 3;

        /// <summary>Download progress is pushed at this granularity. Windows throttles live toast updates, and a
        /// 1 %-per-tick stream is invisible to the user anyway.</summary>
        public const int ProgressStepPercent = 5;

        /// <summary>The row's own timestamp, with <see cref="NotifyRows.UpdatePin"/> folded to <paramref name="nowMs"/>.
        /// <para>Keyed on the SENTINEL, not on the TYPE: folding every app-update row to a live "now" made it beat the
        /// watermark on every single rebuild, so it re-toasted forever — a SIMULATED update carries a real timestamp
        /// and was re-raised by every unrelated rebuild.</para></summary>
        public static long TimestampOf(in Notification n, long nowMs)
            => n.TimestampMs == NotifyRows.UpdatePin ? nowMs : n.TimestampMs;

        /// <summary>What a rebuild should do: which rows to raise, how many were suppressed into the summary, and the
        /// watermark to store afterwards.</summary>
        /// <param name="Raise">Indices into the input list, OLDEST FIRST (so a truncated burst keeps the FRESHEST —
        /// the caller walks the newest-first feed backwards).</param>
        /// <param name="Suppressed">How many eligible rows were dropped past the cap.</param>
        /// <param name="Watermark">The value to persist, whether or not anything was raised.</param>
        /// <param name="ProgressPercent">-1 when there is no live download; otherwise the percentage to push.</param>
        public readonly record struct Plan(int[] Raise, int Suppressed, long Watermark, int ProgressPercent);

        /// <summary>Decide what a rebuild escalates. PURE — no OS call, no clock of its own.
        /// <para><b>Never toast on first run.</b> A zero watermark means "we have never escalated"; the first rebuild
        /// only RECORDS where the feed was, so enabling notifications does not immediately replay history.</para></summary>
        /// <param name="items">The freshly rebuilt centre, NEWEST FIRST.</param>
        /// <param name="watermark">The persisted newest-already-raised timestamp (0 = never escalated).</param>
        /// <param name="policy">The global gates.</param>
        /// <param name="levelOf">The per-topic dial.</param>
        /// <param name="now">The local instant the quiet-hours test uses.</param>
        /// <param name="updateAlreadyRaised">The <c>state:targetQuad</c> identity of the update row already
        /// banner-ed, or "" — see <see cref="UpdateIdentity"/>. An app update is STATE, not an event: its row persists
        /// for as long as the update is available and is never read-gated, so a timestamp test alone re-raises it on
        /// every rebuild.</param>
        public static Plan Decide(IReadOnlyList<Notification> items, long watermark, in NotificationPolicy policy,
            Func<NotifyTopic, NotifyLevel> levelOf, DateTimeOffset now, string updateAlreadyRaised)
        {
            long nowMs = now.ToUnixTimeMilliseconds();
            long newest = watermark;

            // FIRST PASS over everything, so the watermark advances even when nothing is raised.
            for (int i = 0; i < items.Count; i++)
            {
                long ts = TimestampOf(items[i], nowMs);
                if (ts > newest) newest = ts;
            }

            // A download's progress is not an event: the toast is raised ONCE when Downloading starts and then updated
            // in place. Deciding this BEFORE the escalation loop is what stops a 5 %-per-tick stream of twenty
            // identical banners.
            int progress = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is { Category: NotifyCategory.AppUpdate, Update.State: AppUpdateState.Downloading } d)
                {
                    progress = Math.Clamp(d.Update!.ProgressPercent, 0, 100);
                    break;
                }
            }

            bool firstRun = watermark <= 0;
            if (firstRun || !policy.WindowsEnabled) return new Plan([], 0, newest, progress);

            var raise = new List<int>(MaxPerRebuild);
            int suppressed = 0;
            // The list is NEWEST FIRST and this walks it BACKWARD, i.e. OLDEST → NEWEST, so the banners arrive in
            // chronological order and the most recent lands on top of the Action Center stack. Ch 14 §9 is explicit
            // that reversing this "truncates the burst at the wrong end", so the direction ports verbatim — note the
            // consequence, which 0.2.9's own comment stated backwards: past the cap it is the NEWEST rows that fold
            // into the summary line, not the oldest. `NotifyEscalationTests` pins the real end.
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var n = items[i];
                long ts = TimestampOf(in n, nowMs);
                if (ts <= watermark || !n.IsUnread) continue;
                var topic = Prefs.TopicOf(in n);
                if (!policy.RaisesToastNow(levelOf(topic), now)) continue;
                if (n.Category == NotifyCategory.AppUpdate
                    && string.Equals(UpdateIdentity(in n), updateAlreadyRaised, StringComparison.Ordinal)) continue;
                if (raise.Count >= MaxPerRebuild) { suppressed++; continue; }
                raise.Add(i);
            }
            return new Plan(raise.ToArray(), suppressed, newest, progress);
        }

        /// <summary>The identity that decides whether an update row is a NEW banner: the state plus the target quad.
        /// PROGRESS is deliberately excluded — a moving bar updates the live toast instead of raising a new one.</summary>
        public static string UpdateIdentity(in Notification n)
            => n.Update is { } s ? s.State.ToString() + ":" + (s.TargetQuad ?? "") : "";

        /// <summary>Should a progress tick actually be pushed into the live toast? A no-op until a toast has been
        /// raised (<paramref name="lastPushed"/> &lt; 0), and throttled to <see cref="ProgressStepPercent"/> — except
        /// at 100, which always lands.</summary>
        public static bool ShouldPushProgress(int lastPushed, int percent)
            => lastPushed >= 0 && (percent == 100 || percent - lastPushed >= ProgressStepPercent);
    }

    // ══ 9. APP-UPDATE TOASTS (A8) ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The update toast decision table: <c>(previous, next)</c> snapshot → the toast to raise, or nothing.
    /// <para>Pure and total, and deliberately separate from whatever SHOWS the card. The rule that actually matters is
    /// the one a scattered set of call sites always gets wrong: an app update is <b>STATE</b>, not an event — its row
    /// persists for as long as the update is available — so a toast is planned only when the state genuinely MOVED (or
    /// a failure changed its reason). Progress ticks re-render the bar that is already on screen; they never plan a
    /// second card.</para>
    /// <para>Checking and Snoozed are SILENT by design: a background poll must not interrupt, and "Later" means the
    /// user already answered.</para></summary>
    public static class AppUpdateToasts
    {
        /// <summary>Plan the toast for a transition, or null when nothing should be raised.</summary>
        /// <param name="previous">The snapshot the UI last saw (pass <see cref="AppUpdateSnapshot.Idle"/> at startup).</param>
        /// <param name="next">The snapshot just published.</param>
        public static ToastPlan? Plan(AppUpdateSnapshot? previous, AppUpdateSnapshot? next)
        {
            if (next is null) return null;
            bool moved = previous is null
                || previous.State != next.State
                || (next.State == AppUpdateState.Failed && previous.Failure?.Kind != next.Failure?.Kind);
            if (!moved) return null;
            // A QUIET failure (a scheduled poll that could not reach the feed) is state, not an interruption.
            if (next.State == AppUpdateState.Failed && next.Quiet) return null;

            string name = ReleaseName(next);
            return next.State switch
            {
                AppUpdateState.Available => new ToastPlan(
                    Strings.Update.Toast.Available(name),
                    // The body names the build only when the title could not: with no codename/semver known the title
                    // already IS the quad, and "Wavee 0.2.0.9002 is available / 0.2.0.9002" said it twice.
                    next.TargetQuad is { Length: > 0 } quad && quad != name ? quad : "",
                    InfoBarSeverity.Informational,
                    Sticky: false,
                    [ToastActionKind.UpdateNow, ToastActionKind.WhatsNew, ToastActionKind.Later]),

                AppUpdateState.Downloading => new ToastPlan(
                    Strings.Update.Toast.Downloading(name), "", InfoBarSeverity.Informational, Sticky: true, []),

                AppUpdateState.Installing => new ToastPlan(
                    "", Loc.Get(Strings.Update.State.Installing), InfoBarSeverity.Informational, Sticky: true, []),

                AppUpdateState.Completed => new ToastPlan(
                    Strings.Update.Toast.Updated(name), "", InfoBarSeverity.Success, Sticky: false,
                    [ToastActionKind.WhatsNew]),

                AppUpdateState.Failed => new ToastPlan(
                    "", FailureText(next.Failure), InfoBarSeverity.Error, Sticky: true,
                    next.Failure?.Kind == AppUpdateFailureKind.Metered
                        ? [ToastActionKind.Retry]
                        : [ToastActionKind.Retry, ToastActionKind.OpenReleasePage]),

                // None, Checking, Snoozed — nothing to say.
                _ => null,
            };
        }

        /// <summary>The button label for an action.</summary>
        public static string Label(ToastActionKind kind) => kind switch
        {
            ToastActionKind.UpdateNow => Loc.Get(Strings.Update.Action.UpdateNow),
            ToastActionKind.WhatsNew => Loc.Get(Strings.Update.Action.WhatsNew),
            ToastActionKind.Later => Loc.Get(Strings.Update.Action.Later),
            ToastActionKind.Retry => Loc.Get(Strings.Update.Action.Retry),
            ToastActionKind.OpenReleasePage => Loc.Get(Strings.Update.Action.OpenReleasePage),
            _ => "",
        };

        /// <summary>The sentence for a failure — the reason AND the next step, in ONE line. Shared by the toast, the
        /// notification row and Settings ▸ About so all three tell the same story.</summary>
        public static string FailureText(AppUpdateFailure? failure)
        {
            if (failure is null) return Strings.Update.Failure.Unknown("0");
            return failure.Kind switch
            {
                AppUpdateFailureKind.PackagesInUse => Loc.Get(Strings.Update.Failure.PackagesInUse),
                AppUpdateFailureKind.VersionConflict => Loc.Get(Strings.Update.Failure.VersionConflict),
                AppUpdateFailureKind.SideloadPolicy => Loc.Get(Strings.Update.Failure.SideloadPolicy),
                AppUpdateFailureKind.Network => Loc.Get(Strings.Update.Failure.Network),
                AppUpdateFailureKind.AppInstallerOutdated => Loc.Get(Strings.Update.Failure.AppInstallerOutdated),
                AppUpdateFailureKind.Metered => Loc.Get(Strings.Update.Failure.Metered),
                AppUpdateFailureKind.NotAssociated => Loc.Get(Strings.Update.Failure.NotAssociated),
                _ => Strings.Update.Failure.Unknown(Code(failure.HResult)),
            };
        }

        /// <summary>How the release is NAMED in a sentence: its codename when the index knew one, else the semver,
        /// else the raw quad. NEVER empty — a toast that says "Wavee  is available" is worse than one that says a
        /// number.</summary>
        public static string ReleaseName(AppUpdateSnapshot? snapshot)
        {
            if (snapshot is null) return "";
            if (snapshot.TargetCodename is { Length: > 0 } codename) return codename;
            if (snapshot.TargetSemVer is { Length: > 0 } semver) return semver;
            return snapshot.TargetQuad ?? "";
        }

        /// <summary>The ONE sentence under the update row, in Settings ▸ About and in the notification row.
        /// <para><b>Port the asymmetry, do not "harmonise" it.</b> Four arms (Available, Downloading, Completed, and
        /// Failed via <see cref="FailureText"/>) name the release through <see cref="ReleaseName"/> so they cannot
        /// interpolate a codename twice or print a double space; <see cref="AppUpdateState.Snoozed"/> deliberately
        /// does NOT — it prints the semver, else the quad, else "" — and it is the one arm that CAN render an empty
        /// name.</para></summary>
        public static string StateSentence(AppUpdateSnapshot? s)
        {
            if (s is null) return Loc.Get(Strings.Update.State.UpToDate);
            return s.State switch
            {
                AppUpdateState.Checking => Loc.Get(Strings.Update.State.Checking),
                AppUpdateState.Available => Strings.Update.State.Available(ReleaseName(s)),
                AppUpdateState.Snoozed => Strings.Update.State.Snoozed(s.TargetSemVer ?? s.TargetQuad ?? ""),
                AppUpdateState.Downloading => Strings.Update.State.Downloading(ReleaseName(s)),
                AppUpdateState.Installing => Loc.Get(Strings.Update.State.Installing),
                AppUpdateState.Completed => Strings.Update.State.JustUpdated(ReleaseName(s)),
                AppUpdateState.Failed => FailureText(s.Failure),
                _ => Loc.Get(Strings.Update.State.UpToDate),
            };
        }

        static string Code(int hresult)
            => hresult == 0 ? "0" : "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);
    }

    // ══ 10. THE VERSION ARITHMETIC ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The engine-free version arithmetic behind the updater: is the feed's version newer than ours, and did
    /// this launch follow an update? Pure string/int math with no I/O.</summary>
    public static class AppUpdateVersion
    {
        /// <summary>True when <paramref name="remote"/> is STRICTLY newer than <paramref name="current"/>.
        /// <para>Both sides are normalized first: a leading <c>v</c>, build metadata (<c>+…</c>) and a pre-release
        /// suffix (<c>-dev</c>, <c>-rc.1</c>) are stripped, then up to four dot-separated numeric parts are compared
        /// with missing parts treated as 0 — so <c>0.1.2</c> and <c>0.1.2.0</c> are the same version. Anything that
        /// does not parse that way on EITHER side returns false: an unstamped local build or a malformed feed must
        /// never produce an "update available" prompt.</para></summary>
        public static bool IsNewer(string? remote, string? current)
        {
            if (!TryParse(remote, out var r)) return false;
            if (!TryParse(current, out var c)) return false;
            for (int i = 0; i < 4; i++)
            {
                if (r[i] > c[i]) return true;
                if (r[i] < c[i]) return false;
            }
            return false;
        }

        /// <summary>The startup "you were updated" rule: true when the previous run recorded a DIFFERENT version than
        /// the one now running. A first-ever launch is NOT an update — a fresh install must not greet the user with an
        /// update notice.</summary>
        public static bool IsFirstRunAfterUpdate(string? lastRunVersion, string? currentVersion)
            => !string.IsNullOrEmpty(lastRunVersion) && !string.IsNullOrEmpty(currentVersion)
               && !string.Equals(lastRunVersion, currentVersion, StringComparison.Ordinal);

        /// <summary>The release-notes tag for a version: the first three numeric parts (<c>0.1.1.42</c> →
        /// <c>0.1.1</c>). Falls back to the normalized input when it does not parse. This is the <c>&amp;arg=</c> the
        /// "Updated" toast deep-links with when the semver is not otherwise known.</summary>
        public static string ReleaseTagVersion(string? version)
        {
            string norm = Normalize(version);
            if (!TryParse(version, out var v)) return norm;
            return v[0] + "." + v[1] + "." + v[2];
        }

        static string Normalize(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            string s = version.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s[..dash];
            return s;
        }

        static bool TryParse(string? version, out int[] parts)
        {
            parts = new int[4];
            string s = Normalize(version);
            if (s.Length == 0) return false;

            int index = 0;
            int start = 0;
            while (start <= s.Length)
            {
                int dot = s.IndexOf('.', start);
                int end = dot < 0 ? s.Length : dot;
                if (end == start) return false;                    // empty segment ("1..2", "1.")
                if (index >= 4) return false;                      // more than four parts is not a version we know
                if (!int.TryParse(s.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture,
                        out int value))
                    return false;                                  // non-numeric segment ("dev", "1.x")
                parts[index++] = value;
                if (dot < 0) break;
                start = dot + 1;
            }
            return index > 0;
        }
    }

    /// <summary>The "install a waiting update when I quit" decision, on its own so it can be unit-tested without a
    /// process to quit.
    /// <para>Only <see cref="AppUpdateState.Available"/> and <see cref="AppUpdateState.Snoozed"/> qualify. A
    /// <see cref="AppUpdateState.Failed"/> attempt must NOT be retried silently at quit (the user pressed something
    /// and was told it failed; a quiet retry that fails again is invisible), and Downloading/Installing are already in
    /// flight.</para></summary>
    public static class ShutdownUpdatePolicy
    {
        /// <summary>Should the orderly-shutdown path apply the pending update?</summary>
        public static bool ShouldApply(bool installOnQuit, AppUpdateState state)
            => installOnQuit && state is AppUpdateState.Available or AppUpdateState.Snoozed;

        /// <summary>Did the quit-time apply reach a state worth reporting as FINISHED? Anything else means the bounded
        /// wait timed out or the apply refused before it started.</summary>
        public static bool IsSettled(AppUpdateState state)
            => state is AppUpdateState.Installing or AppUpdateState.Failed;
    }

    // ══ 11. THE LIVE FEED CELLS ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A remote feed's coarse state, driving which surface (skeleton / rows / empty / offline / error) the
    /// panel shows. The panel must render its OWN loading state from this, never a global skeleton: a feed that has
    /// not answered is a distinct thing from an empty one.</summary>
    public enum FeedState : byte { Idle, Loading, Populated, Empty, Offline, Error }

    /// <summary>The merged feed the panel renders and the bell counts. Written once per rebuild by
    /// <see cref="Rebuild"/>; nothing else may write it.</summary>
    public static readonly Signal<Feed> Items = new(Feed.Empty);

    /// <summary>The bell's badge count. A separate cell so the chrome's trailing island re-renders on a count change
    /// without subscribing to the whole list.</summary>
    public static readonly Signal<int> Unread = new(0);

    public static readonly Signal<FeedState> SocialState = new(FeedState.Idle);
    public static readonly Signal<FeedState> ReleasesState = new(FeedState.Idle);

    /// <summary>The app-update observation the row and Settings ▸ About read.</summary>
    public static readonly Signal<AppUpdateSnapshot> Update = new(AppUpdateSnapshot.Idle);

    /// <summary>Rebuild the centre from the four sources and publish both cells TOGETHER. Every derived fact
    /// (<c>IsUnread</c>, the count) is computed here, at commit time — no surface re-derives one.</summary>
    public static Feed Rebuild(
        Notification? update,
        IReadOnlyList<Notification> social,
        IReadOnlyList<Notification> releases,
        IReadOnlyList<Notification> activity)
    {
        s_lastUpdate = update;
        s_lastSocial = social;
        s_lastReleases = releases;
        s_lastActivity = activity;
        s_hasSources = true;
        var feed = Merge(update,
            social, Platform.Settings.Get(Platform.Keys.NotificationsGanderLastSeenMs),
            releases, Platform.Settings.Get(Platform.Keys.NotificationsWhatsNewLastSeenMs),
            activity,
            Platform.Settings.Get(Platform.Keys.NotificationsReadIds),
            static topic => Prefs.Level(topic) != NotifyLevel.Off);
        Items.Value = feed;
        Unread.Value = feed.Unread;
        HostConsider(feed);
        return feed;
    }

    /// <summary>Mark ONE row read (Home's timeline row click, the panel's row click). Idempotent; a real change re-merges
    /// so the badge and the panel read the new state back (a repeat click costs nothing and never republishes).</summary>
    public static void MarkRead(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        string set = Platform.Settings.Get(Platform.Keys.NotificationsReadIds);
        string next = ReadIds.Add(set, id);
        if (string.Equals(next, set, StringComparison.Ordinal)) return;
        Platform.Settings.Set(Platform.Keys.NotificationsReadIds, next);
        Refresh();
    }

    /// <summary>Mark everything read: advance BOTH watermarks and CLEAR the per-item set, which a mark-all subsumes.
    /// Clearing it is what keeps the set bounded across a long-lived install. The local activity journal's own read
    /// flags go through <see cref="ActivityMarkAllRead"/> (the journal is `Entities/Store.cs`'s, ch 19 DATA GAP 3).</summary>
    public static void MarkAllRead(long nowMs)
    {
        AdvanceRemoteWatermarks(nowMs);
        ActivityMarkAllRead?.Invoke();
        Refresh();
    }

    /// <summary>The panel OPENED (either anchor): refetch the stale remote feeds, advance both watermarks (opening the
    /// centre IS seeing the remote rows) and re-merge. The panel's "demand" happens at OPEN, not at mount, and re-runs on
    /// every open (ch 19 §9.3 item 5).</summary>
    public static void PanelOpened(long nowMs)
    {
        RefreshFeeds?.Invoke();
        AdvanceRemoteWatermarks(nowMs);
        Refresh();
    }

    static void AdvanceRemoteWatermarks(long nowMs)
    {
        Platform.Settings.Set(Platform.Keys.NotificationsGanderLastSeenMs, nowMs);
        Platform.Settings.Set(Platform.Keys.NotificationsWhatsNewLastSeenMs, nowMs);
        // A watermark advance subsumes every individually-marked id, so the per-item set is DROPPED rather than kept
        // growing beside a watermark that already covers it — this is what bounds it in practice.
        if (Platform.Settings.Get(Platform.Keys.NotificationsReadIds).Length > 0)
            Platform.Settings.Set(Platform.Keys.NotificationsReadIds, "");
    }

    // The last four sources a rebuild merged — a read-state change re-merges them rather than waiting for a feed push.
    static Notification? s_lastUpdate;
    static IReadOnlyList<Notification> s_lastSocial = [], s_lastReleases = [], s_lastActivity = [];
    static bool s_hasSources;

    /// <summary>Re-merge the last sources against the CURRENT read state and dials (a mark-read, a panel open, a dial
    /// flip). A no-op before the first <see cref="Rebuild"/>.</summary>
    public static void Refresh()
    {
        if (!s_hasSources) return;
        Rebuild(s_lastUpdate, s_lastSocial, s_lastReleases, s_lastActivity);
    }

    // ── the panel's seams (filled by the feed/journal/updater owners; a null seam hides its affordance) ──────────────

    /// <summary>Refetch the remote feeds if stale (the gander + what's-new decode sources, Wave 2/5).</summary>
    public static Action? RefreshFeeds;

    /// <summary>Mark the local activity journal's rows read (the journal is not in this file).</summary>
    public static Action? ActivityMarkAllRead;

    /// <summary>Clear the local activity journal — the header's "Clear" (Activity filter only). Null ⇒ no Clear link.</summary>
    public static Action? ClearActivity;

    /// <summary>Undo an invertible activity row. Returns false when the undo failed (a Warning toast says so). Null ⇒ no
    /// Undo pill.</summary>
    public static Func<Notification, Task<bool>>? UndoActivity;

    /// <summary>Run an update verb (the updater's deployment shell, or the simulator while one is walking). Null ⇒ the row
    /// renders its buttons but they do nothing — an honest inert row rather than a missing one.</summary>
    public static Action<UpdateRowAction, AppUpdateSnapshot>? UpdateCommand;

    // ══ 12. THE PANEL'S DECISIONS (ch 19 W14-W17) ══════════════════════════════════════════════════════════════════

    /// <summary>The panel's fixed geometry: 380 wide, the feed scrolls at most 460, the whole panel caps at 520.</summary>
    public const float PanelWidth = 380f, FeedMaxHeight = 460f, PanelMaxHeight = 520f;

    /// <summary>Relative times advance on this cadence while the panel is open (auto-paused when parked).</summary>
    public const int RelativeTickMs = 30_000;

    /// <summary>The active filter pill. On the model, not the panel, so a re-open keeps the user's pill.</summary>
    public static readonly Signal<NotifyFilter> Filter = new(NotifyFilter.All);

    /// <summary>The download bar's fraction for the update row's progress strip in the panel — a FloatSignal so twenty
    /// progress ticks are twenty float writes, not twenty reconciles. The open panel mirrors <see cref="Update"/> into it,
    /// so a row that appears mid-download starts at the right place.</summary>
    public static readonly FloatSignal UpdateProgress = new(0f);

    /// <summary>How many playlist edits this session made that the server has not acked yet — the panel's one-line
    /// "pending sync" answer to "did that actually happen?". Written by the playlist outbox's owner (Wave 5); 0 renders
    /// nothing, which is nearly always.</summary>
    public static readonly Signal<int> PendingEdits = new(0);

    /// <summary>The toast action a planned update card offers, as the verb the row/updater seam understands.</summary>
    public static UpdateRowAction VerbFor(ToastActionKind kind) => kind switch
    {
        ToastActionKind.UpdateNow => UpdateRowAction.UpdateNow,
        ToastActionKind.WhatsNew => UpdateRowAction.WhatsNew,
        ToastActionKind.Later => UpdateRowAction.Later,
        ToastActionKind.Retry => UpdateRowAction.Retry,
        _ => UpdateRowAction.OpenReleasePage,
    };

    /// <summary>The category a pill shows; null for All.</summary>
    public static NotifyCategory? CategoryOf(NotifyFilter filter) => filter switch
    {
        NotifyFilter.Updates => NotifyCategory.AppUpdate,
        NotifyFilter.Spotify => NotifyCategory.Social,
        NotifyFilter.New => NotifyCategory.NewRelease,
        NotifyFilter.Activity => NotifyCategory.Activity,
        _ => null,
    };

    public static bool PassesFilter(in Notification n, NotifyFilter filter)
        => CategoryOf(filter) is not { } c || n.Category == c;

    /// <summary>"Clear" is rendered ONLY while the Activity pill is selected.</summary>
    public static bool ShowsClear(NotifyFilter filter) => filter == NotifyFilter.Activity;

    /// <summary>THE EMPTY LADDER. A remote category with zero rows is only EMPTY when its feed actually loaded — a
    /// loading, failed or offline fetch must say so instead of masquerading as "no notifications". Returns a loc key.</summary>
    public static string EmptyMessageKey(NotifyFilter filter, FeedState social, FeedState releases)
    {
        static string? Remote(FeedState s) => s switch
        {
            FeedState.Idle or FeedState.Loading => Strings.Notifications.Loading,
            FeedState.Error => Strings.Notifications.Feed.Error,
            FeedState.Offline => Strings.Notifications.Feed.Offline,
            _ => null,
        };
        return filter switch
        {
            NotifyFilter.Updates => Strings.Notifications.Empty.Updates,
            NotifyFilter.Spotify => Remote(social) ?? Strings.Notifications.Empty.Spotify,
            NotifyFilter.New => Remote(releases) ?? Strings.Notifications.Empty.New,
            NotifyFilter.Activity => Strings.Notifications.Empty.Activity,
            _ => social is FeedState.Idle or FeedState.Loading || releases is FeedState.Idle or FeedState.Loading
                ? Strings.Notifications.Loading
                : Strings.Notifications.Empty.All,
        };
    }

    /// <summary>A row's age as a unit + count: &lt; 1 min "now", &lt; 60 min minutes, &lt; 24 h hours, else days.
    /// Negative ages clamp to 0.</summary>
    public static (RelativeUnit Unit, long Count) RelativeAge(long ageMs)
    {
        if (ageMs < 0) ageMs = 0;
        long min = ageMs / 60_000;
        if (min < 1) return (RelativeUnit.Now, 0);
        if (min < 60) return (RelativeUnit.Minutes, min);
        long hr = min / 60;
        return hr < 24 ? (RelativeUnit.Hours, hr) : (RelativeUnit.Days, hr / 24);
    }

    /// <summary>The release type pill's FIVE labels (authored in caps in the catalogue — the eyebrow does not transform
    /// them): EPISODE for an episode, else SINGLE / EP / COMPILATION by album type, ALBUM for anything else (incl.
    /// null). Returns a loc key.</summary>
    public static string ReleaseTypeKey(NewReleaseKind kind, string? albumType)
    {
        if (kind == NewReleaseKind.Episode) return Strings.Notifications.Release.Episode;
        if (string.Equals(albumType, "single", StringComparison.OrdinalIgnoreCase)) return Strings.Notifications.Release.Single;
        if (string.Equals(albumType, "ep", StringComparison.OrdinalIgnoreCase)) return Strings.Notifications.Release.Ep;
        if (string.Equals(albumType, "compilation", StringComparison.OrdinalIgnoreCase)) return Strings.Notifications.Release.Compilation;
        return Strings.Notifications.Release.Album;
    }

    /// <summary>The update row is SIX rows, one per state (ch 19 W15): glyph, title key, and either the progress strip
    /// or the buttons — the FIRST action is the accent one. <c>None</c>/<c>Checking</c> never reach the panel, but the
    /// arm exists: an info glyph and an EMPTY title (no copy is invented for it).</summary>
    public static UpdateRowArm UpdateRow(AppUpdateState state) => state switch
    {
        AppUpdateState.Available => new(UpdateRowGlyph.Download, Strings.Notifications.Update.AvailableTitle, false,
            [UpdateRowAction.UpdateNow, UpdateRowAction.WhatsNew, UpdateRowAction.Later]),
        // Snoozed: no Later — the user already answered.
        AppUpdateState.Snoozed => new(UpdateRowGlyph.Download, Strings.Notifications.Update.AvailableTitle, false,
            [UpdateRowAction.UpdateNow, UpdateRowAction.WhatsNew]),
        AppUpdateState.Downloading or AppUpdateState.Installing =>
            new(UpdateRowGlyph.Refresh, Strings.Update.Os.Downloading, true, []),
        AppUpdateState.Completed => new(UpdateRowGlyph.Success, Strings.Notifications.Update.CompletedTitle, false,
            [UpdateRowAction.WhatsNew, UpdateRowAction.Dismiss]),
        AppUpdateState.Failed => new(UpdateRowGlyph.Error, Strings.Notifications.Update.FailedTitle, false,
            [UpdateRowAction.Retry, UpdateRowAction.Dismiss]),
        _ => new(UpdateRowGlyph.Info, "", false, []),
    };

    /// <summary>The release "What's new" opens: the RUNNING build's version once the update landed, else the target's
    /// semver, else the release tag derived from the target quad. "" ⇒ open the newest notes.</summary>
    public static string NotesVersion(AppUpdateSnapshot snapshot, string? runningVersion)
        => snapshot.State == AppUpdateState.Completed
            ? runningVersion ?? ""
            : snapshot.TargetSemVer is { Length: > 0 } semver ? semver : AppUpdateVersion.ReleaseTagVersion(snapshot.TargetQuad);

    // ══ 13. TOAST TAG HYGIENE (G-091) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>Windows toast tag/group values are capped at 64 characters; a longer one is SILENTLY DROPPED — the
    /// banner never raises and nothing throws. <c>"live:" + n.Id</c> and <c>"drop:" + preReleaseUri</c> both build the
    /// tag from a server id with no length guarantee, so any prefix+id pair long enough to blow the budget folds to a
    /// short stable hash of the id instead of the id itself — truncation would lose the very uniqueness the tag exists
    /// for, one character before it saved any length.</summary>
    public static class TagIds
    {
        public const int MaxLength = 64;

        /// <summary>How many hex characters of the id's hash to keep — comfortably unique for a process's live-toast
        /// set, and short enough that even the longest prefix this file uses stays under <see cref="MaxLength"/>.</summary>
        const int HashHexChars = 16;

        public static string Clamp(string prefix, string id)
        {
            string tag = prefix + id;
            return tag.Length <= MaxLength ? tag : prefix + Hash(id);
        }

        static string Hash(string id)
        {
            Span<byte> digest = stackalloc byte[32];
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id), digest);
            return Convert.ToHexStringLower(digest[..(HashHexChars / 2)]);
        }
    }

    // The SHELL seam (Notify.Host.cs). Erased when that file is absent, so a unit test rebuilds the feed with no
    // WinRT, no AUMID and no toast at all.
    static partial void HostConsider(in Feed feed);
}
