using System;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The notification dials, as rules. Quiet hours wrap midnight and scheduled delivery SHIFTS rather than drops — both are
// the kind of thing that looks right in a settings page and is wrong at 03:00, so they are pinned here.
public class NotificationPolicyTests
{
    static DateTimeOffset At(int hour) => new(2026, 8, 14, hour, 30, 0, TimeSpan.Zero);

    static NotificationPolicy Policy(bool windows = true, bool quiet = false, int from = 22, int to = 8)
        => new(windows, Sound: true, new QuietHours(quiet, from, to));

    // ── the ladder ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMasterGate_SilencesEveryBanner_ButLeavesTheInAppRecord()
    {
        var off = Policy(windows: false);

        Assert.False(off.RaisesToastNow(NotifyLevel.Windows, At(12)));
        Assert.Null(off.ScheduleAt(NotifyLevel.Windows, At(12)));
        Assert.True(off.ShowsInApp(NotifyLevel.Windows));   // the bell is the durable log; it keeps recording
        Assert.True(off.ShowsInApp(NotifyLevel.InApp));
    }

    [Fact]
    public void OffMeansOff_EvenWithTheMasterOn()
    {
        var on = Policy();

        Assert.False(on.ShowsInApp(NotifyLevel.Off));
        Assert.False(on.RaisesToastNow(NotifyLevel.Off, At(12)));
        Assert.Null(on.ScheduleAt(NotifyLevel.Off, At(12)));
    }

    [Fact]
    public void InApp_NeverEscalatesToABanner()
    {
        var on = Policy();

        Assert.True(on.ShowsInApp(NotifyLevel.InApp));
        Assert.False(on.RaisesToastNow(NotifyLevel.InApp, At(12)));
        Assert.Null(on.ScheduleAt(NotifyLevel.InApp, At(12)));
    }

    [Fact]
    public void LibraryActivity_CannotReachWindows_AndAStoredWindowsLevelIsClampedAway()
    {
        // The dial renders two segments for this topic; a settings file that claims otherwise (hand-edited, or written by
        // a build where the ceiling was higher) must not resurrect a banner for "you just saved a song".
        Assert.Equal(NotifyLevel.InApp, NotificationPolicy.CeilingFor(NotifyTopic.LibraryActivity));
        Assert.Equal(NotifyLevel.InApp, NotificationPolicy.Clamp(NotifyTopic.LibraryActivity, NotifyLevel.Windows));
        Assert.Equal(NotifyLevel.Windows, NotificationPolicy.Clamp(NotifyTopic.NewAlbums, NotifyLevel.Windows));
    }

    [Fact]
    public void DefaultsPreserveTodaysBehaviour_AndOnlyDropsIsPreShapedForWindows()
    {
        // Every topic defaults to the centre — a fresh install behaves exactly as it did before this page existed — and
        // nothing escalates until the master gate is turned on, so the Windows default on drops is a shape, not noise.
        Assert.Equal(NotifyLevel.Windows, NotificationPolicy.DefaultFor(NotifyTopic.ReleaseDrops));
        Assert.Equal(NotifyLevel.InApp, NotificationPolicy.DefaultFor(NotifyTopic.NewAlbums));
        Assert.Equal(NotifyLevel.InApp, NotificationPolicy.DefaultFor(NotifyTopic.Concerts));
        Assert.Equal(NotifyLevel.InApp, NotificationPolicy.DefaultFor(NotifyTopic.LibraryActivity));
    }

    [Fact]
    public void OnlyTheScheduledTopics_ClaimToArriveWhenClosed()
    {
        // The UI prints this claim on the row, so it had better be true: these are the two the OS delivers on a timer.
        Assert.True(NotificationPolicy.IsScheduled(NotifyTopic.ReleaseDrops));
        Assert.True(NotificationPolicy.IsScheduled(NotifyTopic.DaylistRefresh));
        Assert.False(NotificationPolicy.IsScheduled(NotifyTopic.NewAlbums));
        Assert.False(NotificationPolicy.IsScheduled(NotifyTopic.Concerts));
    }

    // ── quiet hours ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AQuietWindowThatWrapsMidnight_CoversBothSidesOfIt()
    {
        var q = new QuietHours(true, 22, 8);

        Assert.True(q.Contains(At(23)));
        Assert.True(q.Contains(At(2)));
        Assert.True(q.Contains(At(22)));    // inclusive start
        Assert.False(q.Contains(At(8)));    // exclusive end
        Assert.False(q.Contains(At(12)));
    }

    [Fact]
    public void ASameDayQuietWindow_DoesNotWrap()
    {
        var q = new QuietHours(true, 13, 17);

        Assert.True(q.Contains(At(13)));
        Assert.True(q.Contains(At(16)));
        Assert.False(q.Contains(At(17)));
        Assert.False(q.Contains(At(2)));
    }

    [Fact]
    public void AnEqualPair_MeansNoQuietWindow_NotAlwaysQuiet()
    {
        // The safer reading of an accidental equal pair: silencing the app forever is the worse failure.
        var q = new QuietHours(true, 9, 9);

        Assert.False(q.Contains(At(9)));
        Assert.False(q.Contains(At(3)));
    }

    [Fact]
    public void DisabledQuietHours_NeverContainAnything()
    {
        Assert.False(new QuietHours(false, 22, 8).Contains(At(2)));
    }

    [Fact]
    public void CorruptHours_AreClampedRatherThanTrusted()
    {
        var q = new QuietHours(true, -5, 99).Normalized();

        Assert.Equal(0, q.FromHour);
        Assert.Equal(0, q.ToHour);
        Assert.False(q.Contains(At(12)));   // 0 → 0 collapses to "no window", not "all day"
    }

    [Fact]
    public void ALiveBanner_IsSuppressedInsideQuietHours_AndAllowedOutside()
    {
        var p = Policy(quiet: true, from: 22, to: 8);

        Assert.False(p.RaisesToastNow(NotifyLevel.Windows, At(1)));
        Assert.True(p.RaisesToastNow(NotifyLevel.Windows, At(10)));
    }

    [Fact]
    public void AScheduledBanner_ShiftsToTheEndOfQuietHours_RatherThanBeingLost()
    {
        // The album is still out — the user just hears about it at a civilised hour.
        var p = Policy(quiet: true, from: 22, to: 8);

        var due = new DateTimeOffset(2026, 8, 14, 2, 15, 0, TimeSpan.Zero);   // 02:15, inside the window
        var at = p.ScheduleAt(NotifyLevel.Windows, due);

        Assert.NotNull(at);
        Assert.Equal(8, at!.Value.Hour);
        Assert.Equal(14, at.Value.Day);      // same morning, not tomorrow
        Assert.Equal(0, at.Value.Minute);
    }

    [Fact]
    public void AScheduledBannerLateInTheEvening_ShiftsToTheNextMorning()
    {
        var p = Policy(quiet: true, from: 22, to: 8);

        var due = new DateTimeOffset(2026, 8, 14, 23, 40, 0, TimeSpan.Zero);
        var at = p.ScheduleAt(NotifyLevel.Windows, due);

        Assert.NotNull(at);
        Assert.Equal(8, at!.Value.Hour);
        Assert.Equal(15, at.Value.Day);      // crossed midnight
    }

    [Fact]
    public void AScheduledBannerOutsideQuietHours_IsNotMoved()
    {
        var p = Policy(quiet: true, from: 22, to: 8);
        var due = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(due, p.ScheduleAt(NotifyLevel.Windows, due));
    }

    [Fact]
    public void NextAudible_IsIdempotent_SoAReconcileCannotWalkTheTimeForward()
    {
        // Reconcile runs every launch and re-derives the delivery time from the SAME due date. If shifting an
        // already-shifted time moved it again, a long-lived pre-save would drift a day later on every launch.
        var q = new QuietHours(true, 22, 8);
        var due = new DateTimeOffset(2026, 8, 14, 2, 15, 0, TimeSpan.Zero);

        var once = q.NextAudible(due);
        Assert.Equal(once, q.NextAudible(once));
    }
}
