using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.WindowsApi.Notifications;
using Wavee.Core;

namespace Wavee;

/// <summary>What a simulated event actually did. Reported to the user verbatim — the value of a "Send event" button is
/// not the banner, it is learning which stage of the pipeline consumed the event.</summary>
enum SimOutcome
{
    /// <summary>The topic is dialled Off: the row never even reached the centre. Proves the filter works.</summary>
    Dropped,
    /// <summary>Recorded in the notification centre; no banner, as configured.</summary>
    RecordedInApp,
    /// <summary>Recorded, and a Windows banner was raised.</summary>
    Banner,
    /// <summary>Recorded, but the banner was withheld because quiet hours are active right now.</summary>
    BannerQuietDeferred,
    /// <summary>Handed to the OS for delivery at a future instant (the scheduled topics).</summary>
    Scheduled,
    /// <summary>Recorded, and this topic never banners by design (library activity).</summary>
    NeverBanners,
    /// <summary>Nothing was attempted — no toast platform, or the app is not far enough up to simulate.</summary>
    Unavailable,
}

readonly record struct SimResult(SimOutcome Outcome, DateTimeOffset? At);

/// <summary>
/// Drives one synthetic notification event down the SAME path a real one takes (Settings ▸ Notifications ▸ Send event).
/// Live topics are injected at <see cref="NotificationCenterBridge.Simulate"/> so they flow through the real merge,
/// topic filter and escalator; the two OS-scheduled topics go through their real notifier; library activity goes through
/// the real <see cref="ActivityLog"/>. Nothing here calls <c>ToastNotifier.Show</c> directly — a shortcut past the
/// pipeline would prove only that Windows can paint a banner, which is not the question being asked.
/// </summary>
static class NotificationSimulator
{
    /// <summary>Lead time for the scheduled topics. Comfortably past both notifiers' own minimums (1 and 2 minutes) —
    /// and past the real constraint, which is that Windows ACCEPTS a near-immediate schedule and then silently never
    /// paints it. Short enough that a user will actually wait, long enough to close the app first and prove the point.</summary>
    public static readonly TimeSpan ScheduleLead = TimeSpan.FromMinutes(3);

    static long _seq;

    /// <summary>Simulate <paramref name="topic"/>. Safe to call at any dial setting: a topic that would not be delivered
    /// is REPORTED, never forced, and the scheduled topics are never touched at the OS level when dialled down (their
    /// notifiers revoke on a disallowed policy, which would destroy a genuinely pending real toast).</summary>
    public static SimResult Send(Services? svc, NotifyTopic topic)
    {
        if (svc is null) return new SimResult(SimOutcome.Unavailable, null);
        var settings = svc.Settings;
        var level = NotificationPrefs.Level(settings, topic);
        var policy = NotificationPrefs.Policy(settings);
        long seq = Interlocked.Increment(ref _seq);

        if (level == NotifyLevel.Off) return new SimResult(SimOutcome.Dropped, null);

        if (NotificationPolicy.IsScheduled(topic))
            return SendScheduled(svc, topic, level, in policy, seq);

        if (topic == NotifyTopic.LibraryActivity)
        {
            // The real path for this topic is "the user did something and it was logged" — so log it. A NON-invertible
            // kind against an unresolvable target: a simulated entry must never offer an Undo that could unsave
            // something real (PlaylistCreate is never undoable — ActivityEntry.IsUndoable).
            svc.Activity.Record(ActivityKind.PlaylistCreate, SimulatedNotifications.ActivityTargetUri(seq),
                "Simulated activity");
            return new SimResult(SimOutcome.NeverBanners, null);
        }

        return SendLive(svc, topic, in policy, seq);
    }

    // ── live topics: inject at the bridge, let Rebuild do everything ──────────────────────────────────────────────────

    static SimResult SendLive(Services svc, NotifyTopic topic, in NotificationPolicy policy, long seq)
    {
        var settings = svc.Settings;
        long ts = SimulatedNotifications.NextTimestamp(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            settings.Get(WaveeSettings.NotificationsGanderLastSeenMs),
            settings.Get(WaveeSettings.NotificationsWhatsNewLastSeenMs));

        // The escalator raises nothing while its watermark is 0, so that enabling notifications never replays history.
        // A simulated event is happening NOW, not history — prime the watermark so the very first press behaves like
        // every later one (otherwise it lands in the bell with no banner and reads as broken).
        if (settings.Get(WaveeSettings.NotifyLastToastedMs) <= 0)
            settings.Set(WaveeSettings.NotifyLastToastedMs, ts - 1);

        WaveeNotification n = topic switch
        {
            NotifyTopic.NewAlbums => SimulatedNotifications.NewRelease(AlbumSeed(svc), episode: false, ts, seq),
            NotifyTopic.NewEpisodes => SimulatedNotifications.NewRelease(ShowSeed(svc), episode: true, ts, seq),
            NotifyTopic.Concerts => SimulatedNotifications.Concert(ArtistSeed(svc), ts, seq),
            NotifyTopic.Followers => SimulatedNotifications.Follower(ts, seq),
            // The app-update row is a SNAPSHOT, not a version string: the panel, the toast planner and the OS
            // escalator all read the whole observation. Available specifically — Failed renders no OS toast title and
            // would silently never banner, which reads as a broken test.
            _ => new AppUpdateNotification(ts, IsUnread: true, SimulatedUpdateSnapshot(ts)),
        };

        int raised = svc.Notifications.Simulate(n);
        if (raised > 0) return new SimResult(SimOutcome.Banner, null);
        // Distinguish "the dial said in-app" from "the dial said Windows but quiet hours are on" — same visible result,
        // completely different explanation, and the second one is the confusing one worth naming.
        if (policy.WindowsEnabled
            && NotificationPrefs.Level(svc.Settings, topic) == NotifyLevel.Windows
            && policy.Quiet.Contains(DateTimeOffset.Now))
            return new SimResult(SimOutcome.BannerQuietDeferred, policy.Quiet.NextAudible(DateTimeOffset.Now));
        return new SimResult(SimOutcome.RecordedInApp, null);
    }

    // ── scheduled topics: hand it to the OS through the real notifier ─────────────────────────────────────────────────

    static SimResult SendScheduled(Services svc, NotifyTopic topic, NotifyLevel level, in NotificationPolicy policy, long seq)
    {
        // Below Windows there is nothing to schedule, and calling the notifiers would REVOKE a real pending toast.
        if (!policy.WindowsEnabled || level != NotifyLevel.Windows)
            return new SimResult(SimOutcome.RecordedInApp, null);
        if (!ToastNotifier.IsSupported) return new SimResult(SimOutcome.Unavailable, null);

        var due = DateTimeOffset.Now.Add(ScheduleLead);
        DateTimeOffset? at = topic == NotifyTopic.DaylistRefresh
            ? DaylistNotifier.SimulateSchedule(due, DaylistContextUri(svc), "a simulated daylist")
            : ReleaseNotifier.SimulateSchedule(DropLink(svc, seq, due), due);

        return at is { } when_
            ? new SimResult(SimOutcome.Scheduled, when_)
            : new SimResult(SimOutcome.Unavailable, null);
    }

    /// <summary>A pre-release link for the drop simulation: a REAL pre-saved album when the user has one (so the banner's
    /// Play button opens something that exists), else a synthesized link seeded from any saved album.</summary>
    static PreReleaseLink DropLink(Services svc, long seq, DateTimeOffset due)
    {
        var seed = AlbumSeed(svc);
        return new PreReleaseLink(
            PreReleaseUri: "spotify:prerelease:simulated:" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AlbumUri: seed.HasReal ? seed.Uri : "spotify:album:simulated",
            ReleaseAt: due,
            Name: seed.HasReal ? seed.Name : "A simulated release",
            Type: "ALBUM",
            Artist: seed.CreatorName is { Length: > 0 } c ? new ArtistRef("", "", c) : null,
            Cover: seed.ImageUrl is { Length: > 0 } url ? new Image(url) : null);
    }

    static string? DaylistContextUri(Services svc)
    {
        // Any real playlist gives the banner somewhere to land; the actual daylist uri is only known once its card
        // hydrates, and a simulate must not wait on the feed.
        var lists = svc.LibraryStore.Playlists.Value.Peek();
        return lists.Count > 0 ? lists[0].Uri : null;
    }

    // ── seeds from the user's own library, so a simulated row is clickable and reads like the real thing ───────────────

    static SimSeed AlbumSeed(Services svc)
    {
        var albums = svc.LibraryStore.Albums.Value.Peek();
        if (albums.Count == 0) return default;
        var a = albums[Pick(albums.Count)];
        return new SimSeed(a.Uri, a.Name, a.Cover?.Url, a.Artists.Count > 0 ? a.Artists[0].Name : null);
    }

    static SimSeed ArtistSeed(Services svc)
    {
        var artists = svc.LibraryStore.Artists.Value.Peek();
        if (artists.Count == 0) return default;
        var a = artists[Pick(artists.Count)];
        return new SimSeed(a.Uri, a.Name, a.Image?.Url, a.Name);
    }

    static SimSeed ShowSeed(Services svc)
    {
        var shows = svc.LibraryStore.Shows.Value.Peek();
        if (shows.Count == 0) return default;
        var s = shows[Pick(shows.Count)];
        return new SimSeed(s.Uri, s.Name, s.Cover?.Url, s.Publisher);
    }

    /// <summary>Rotate through the collection across presses rather than always picking the first item, so repeated
    /// sends look like a feed rather than one row arriving eight times.</summary>
    static int Pick(int count) => count <= 1 ? 0 : (int)(Volatile.Read(ref _seq) % count);

    /// <summary>The synthetic observation behind a simulated update row. Deliberately a version this build could never
    /// actually be offered, so a screenshot of a simulation is never mistaken for a real release; the codename is what
    /// every string interpolates, which is the half worth exercising.</summary>
    static AppUpdateSnapshot SimulatedUpdateSnapshot(long ts)
        => new(AppUpdateState.Available, "99.9.9.999", "99.9.9", "Simulated", 0, null,
            AutoUpdateAssociated: true, LastCheckedMs: ts);
}
