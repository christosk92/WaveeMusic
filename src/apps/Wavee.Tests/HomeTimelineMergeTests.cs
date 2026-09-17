// ── Wavee.Tests/HomeTimelineMergeTests.cs — Home's what's-new timeline over the flat Notification (ported, owner P) ──
//
// 0.2.9's facts over the four notification SUBCLASSES, ported onto 0.3's ONE `Notification` value built by `NotifyRows`
// (ForRelease / ForSocial / ForUpdate / ForActivity). The gate is on KIND, never on the category pill: every what's-new
// release, and a Social row only when `SpotifyUpdates.IsConcert` says so (a CONCERT/LIVE wire type, or a concert action
// target). Local wall-clock instants keep every day-grouping fact true in any time zone.
//
// DROPPED from 0.2.9's file (covered elsewhere, not lost): `SpotifyUpdates.IsConcertTarget / IsConcertWireType /
// CleanTitle` and the `NotificationReadIds` codec are pinned by NotifyTests.cs (their home is Platform/Notify.cs now);
// `SpotifyUpdates.ActName(n)` no longer exists — the act is the row's own `ActName` field, filled by the gander decode.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class HomeTimelineMergeTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static long At(int year, int month, int day, int hour = 12, int minute = 0)
        => new DateTimeOffset(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    static Notification Release(string id, long ts, bool unread = false)
        => NotifyRows.ForRelease(id, ts, unread, NewReleaseKind.Album, default, "Album " + id, null, "Some Artist", "ALBUM", false);

    static Notification Concert(string id, long ts, bool unread = false, string? title = null, string? actionUri = null)
        => NotifyRows.ForSocial(id, ts, unread, title ?? "Just days away: someone live in New York",
            actionUri ?? "spotify:concert:" + id, SocialActionType.NavigateWebview, null, null, null);

    static Notification Follower(string id, long ts, bool unread = false, string? wireType = null)
        => NotifyRows.ForSocial(id, ts, unread, "someone started following you", "spotify:user:" + id,
            SocialActionType.Navigate, null, "someone", wireType);

    static Notification Update()
        => NotifyRows.ForUpdate(AppUpdateSnapshot.Idle with { State = AppUpdateState.Available, TargetQuad = "9.9.9" }, isUnread: true);

    static Notification Activity(long id, long ts)
        => NotifyRows.ForActivity(id.ToString(System.Globalization.CultureInfo.InvariantCulture), ts, false, "A song", "", default);

    static string[] Ids(HomeTimelineFeed feed) => feed.Groups.SelectMany(g => g.Rows).Select(r => r.Id).ToArray();

    // ── the gate: what is timeline material ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyConcertUpdates_JoinTheReleases_EverythingElseStaysInTheCenter()
    {
        long t = At(2026, 8, 6);
        var feed = HomeTimelineMerge.Build(
        [
            Update(),
            Release("r1", t),
            Concert("c1", t - 1000),
            Follower("f1", t - 2000),
            Activity(7, t - 3000),
        ]);

        var rows = feed.Groups.SelectMany(g => g.Rows).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { "r1", "c1" }, rows.Select(r => r.Id).ToArray());
        Assert.Equal(HomeTimelineKind.Release, rows[0].Kind);
        Assert.Equal(HomeTimelineKind.Concert, rows[1].Kind);
    }

    [Fact]
    public void ASocialItemIsGatedOnItsTarget_NotOnItsCategory()
    {
        // Every one of these is NotifyCategory.Social — the "Spotify" pill. Only the concert-shaped targets reach Home.
        long t = At(2026, 8, 6);
        foreach (var target in new[]
                 {
                     "spotify:concert:abc", "https://concerts.spotify.com/event/abc",
                     "https://www.example.com/concert/123", "https://tickets.example.com/concerts/abc",
                 })
            Assert.NotNull(HomeTimelineMerge.Eligible(Concert("c", t, actionUri: target)));

        foreach (var target in new[] { "spotify:user:someone", "spotify:artist:abc", "spotify:playlist:abc", "https://open.spotify.com/user/x" })
            Assert.Null(HomeTimelineMerge.Eligible(Concert("c", t, actionUri: target)));
    }

    [Fact]
    public void TheServersOwnDiscriminatorWins_WhenThePayloadShipsOne()
    {
        // A follower-shaped row whose target we do not recognise still qualifies when the feed labelled it a concert…
        var labelled = HomeTimelineMerge.Eligible(Follower("x1", At(2026, 8, 6), wireType: "CONCERT_ANNOUNCEMENT"));
        Assert.NotNull(labelled);
        Assert.Equal(HomeTimelineKind.Concert, labelled!.Value.Kind);

        // …and an unlabelled, unrecognised one does not. A missing discriminator is not a licence to guess.
        Assert.Null(HomeTimelineMerge.Eligible(Follower("x2", At(2026, 8, 6))));
        Assert.Null(HomeTimelineMerge.Eligible(Follower("x3", At(2026, 8, 6), wireType: "SOCIAL_FOLLOW")));
    }

    [Fact]
    public void AnEmptySpotifyCategory_LeavesTheModuleExactlyAsItWas()
    {
        long t = At(2026, 8, 6);
        var a = HomeTimelineMerge.Build([Release("r1", t), Release("r2", t - 1000)]);
        var b = HomeTimelineMerge.Build([Release("r1", t), Release("r2", t - 1000), Follower("f1", t - 500), Activity(3, t - 600)]);

        Assert.Equal(a.Shown, b.Shown);
        Assert.Equal(a.Total, b.Total);
        Assert.Equal(a.Unread, b.Unread);
        Assert.Equal(a.Groups.Length, b.Groups.Length);
        Assert.Equal(Ids(a), Ids(b));
    }

    [Fact]
    public void NothingEligible_IsEmpty_NotAnEmptyGroup()
    {
        Assert.True(HomeTimelineMerge.Build(null).IsEmpty);
        Assert.True(HomeTimelineMerge.Build(Array.Empty<Notification>()).IsEmpty);

        var noise = HomeTimelineMerge.Build([Update(), Follower("f1", At(2026, 8, 6)), Activity(1, At(2026, 8, 6))]);
        Assert.True(noise.IsEmpty);
        Assert.Empty(noise.Groups);
        Assert.Equal(0, noise.Total);
        Assert.Equal(0, noise.Unread);
    }

    [Fact]
    public void A_non_positive_cap_is_empty()
        => Assert.True(HomeTimelineMerge.Build([Release("r1", At(2026, 8, 6))], maxRows: 0).IsEmpty);

    // ── ordering + day grouping ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARowSortsByItsOwnInstant_NotByTheDateItTalksAbout()
    {
        long delivered = At(2026, 8, 6, 9);
        var feed = HomeTimelineMerge.Build([Concert("c1", delivered, title: "Just days away: someone live on Sat, Aug 15")]);

        Assert.Single(feed.Groups);
        Assert.Equal(HomeTimelineMerge.LocalDay(delivered), feed.Groups[0].DayTicks);
        Assert.NotEqual(HomeTimelineMerge.LocalDay(At(2026, 8, 15)), feed.Groups[0].DayTicks);
    }

    [Fact]
    public void RowsAreNewestFirst_AcrossBothSources_AndGroupsFollowTheirRows()
    {
        var feed = HomeTimelineMerge.Build(
        [
            Release("r-old", At(2026, 8, 4, 10)),
            Concert("c-new", At(2026, 8, 6, 18)),
            Release("r-mid", At(2026, 8, 6, 9)),
            Concert("c-old", At(2026, 8, 4, 22)),
        ]);

        Assert.Equal(new[] { "c-new", "r-mid", "c-old", "r-old" }, Ids(feed));
        Assert.Equal(2, feed.Groups.Length);
        Assert.True(feed.Groups[0].DayTicks > feed.Groups[1].DayTicks);
        foreach (var g in feed.Groups)
            foreach (var r in g.Rows)
                Assert.Equal(g.DayTicks, HomeTimelineMerge.LocalDay(r.Timestamp));
    }

    [Fact]
    public void OneDayIsOneGroup_EvenWhenTheSourcesInterleave()
    {
        var feed = HomeTimelineMerge.Build(
        [
            Release("r1", At(2026, 8, 6, 20)),
            Concert("c1", At(2026, 8, 6, 14)),
            Release("r2", At(2026, 8, 6, 8)),
        ]);

        Assert.Single(feed.Groups);
        Assert.Equal(3, feed.Groups[0].Rows.Length);
    }

    [Fact]
    public void SameInstantOrdersDeterministically_SoTheGroupingIsStable()
    {
        long t = At(2026, 8, 6, 11);
        var forward = HomeTimelineMerge.Build([Release("b", t), Concert("a", t)]);
        var reversed = HomeTimelineMerge.Build([Concert("a", t), Release("b", t)]);

        Assert.Equal(new[] { "a", "b" }, forward.Groups[0].Rows.Select(r => r.Id).ToArray());
        Assert.Equal(forward.Groups[0].Rows.Select(r => r.Id), reversed.Groups[0].Rows.Select(r => r.Id));
    }

    [Fact]
    public void MidnightSplitsTwoDays_AtLocalMidnight()
    {
        var feed = HomeTimelineMerge.Build([Release("late", At(2026, 8, 5, 23, 59)), Release("early", At(2026, 8, 6, 0, 1))]);
        Assert.Equal(2, feed.Groups.Length);
        Assert.Equal(new[] { "early", "late" }, Ids(feed));
    }

    [Fact]
    public void The_day_bucketer_is_injectable()
    {
        // One bucket for everything: the grouping follows the injected clock, not the local zone.
        var feed = HomeTimelineMerge.Build([Release("a", At(2026, 8, 1)), Release("b", At(2026, 8, 6))], dayOf: static _ => 42L);
        var group = Assert.Single(feed.Groups);
        Assert.Equal(42L, group.DayTicks);
    }

    // ── the cap and the counter ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCapBoundsTheROWS_ButTheCounterDescribesTheWholeFeed()
    {
        var items = new List<Notification>();
        for (int i = 0; i < 12; i++) items.Add(Release("r" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), At(2026, 8, 6, 23) - i * 60_000, unread: i < 3));
        for (int i = 0; i < 4; i++) items.Add(Concert("c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), At(2026, 8, 6, 20) - i * 60_000, unread: i < 2));

        var feed = HomeTimelineMerge.Build(items);

        Assert.Equal(HomeTimelineMerge.MaxRows, feed.Shown);
        Assert.Equal(HomeTimelineMerge.MaxRows, feed.Groups.Sum(g => g.Rows.Length));
        Assert.Equal(16, feed.Total);   // 12 releases + 4 concerts, uncapped
        Assert.Equal(5, feed.Unread);   // 3 + 2, uncapped — including the unread rows the cap pushed off screen
    }

    [Fact]
    public void TheUnheardCounter_CountsConcertUpdates_AndNothingThatIsNotOnTheModule()
    {
        long t = At(2026, 8, 6);
        var feed = HomeTimelineMerge.Build(
        [
            Release("r1", t, unread: true),
            Concert("c1", t - 1000, unread: true),
            Follower("f1", t - 2000, unread: true),   // unread, in the badge, NOT on the timeline
            Update(),                                 // ditto
        ]);

        Assert.Equal(2, feed.Total);
        Assert.Equal(2, feed.Unread);
    }

    [Fact]
    public void A_row_carries_its_source_notification_whole()
    {
        long t = At(2026, 8, 6);
        var release = Release("r1", t, unread: true);
        var row = Assert.Single(HomeTimelineMerge.Build([release]).Groups[0].Rows);
        Assert.Equal(release, row.Source);
        Assert.Equal(t, row.Timestamp);
        Assert.True(row.IsUnread);
    }

    // ── the shared read state ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkingOneRowRead_IsAppliedByTheOneMerge_SoEverySurfaceAgrees()
    {
        long t = At(2026, 8, 6);
        var social = new[] { Concert("c1", t, unread: true), Concert("c2", t - 1000, unread: true) };
        var whatsNew = new[] { Release("r1", t - 2000, unread: true) };

        var before = Notify.Merge(null, social, 0, whatsNew, 0, Array.Empty<Notification>());
        Assert.Equal(3, before.Unread);
        Assert.All(before.Items, n => Assert.True(n.IsUnread));

        // The same list, re-merged with c1 individually marked: the centre's row, the bell's count and the timeline's
        // pip all come out of THIS, so one write moves all three.
        string readIds = Notify.ReadIds.Add("", "c1");
        var after = Notify.Merge(null, social, 0, whatsNew, 0, Array.Empty<Notification>(), readIds);

        Assert.Equal(2, after.Unread);
        Assert.False(after.Items.First(n => n.Id == "c1").IsUnread);
        Assert.True(after.Items.First(n => n.Id == "c2").IsUnread);
        Assert.Equal(2, HomeTimelineMerge.Build(after.Items).Unread);
        Assert.Equal(3, HomeTimelineMerge.Build(after.Items).Total);
    }
}
