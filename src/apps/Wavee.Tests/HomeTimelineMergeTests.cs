using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Home's what's-new timeline once it carries TWO sources: the what's-new feed and the Spotify category's concert /
/// live-show announcements. Everything here is the pure merge (<c>HomeTimelineMerge</c>) plus the domain classifier
/// (<c>SpotifyUpdates</c>) and the shared per-item read set (<c>NotificationReadIds</c>) — no engine, no page.
///
/// <para>Modelled on <c>MergedChromeLayoutTests</c>: properties over spot checks. The defect class here is a row that
/// lands in the wrong day, an announcement that sorts by the CONCERT date instead of its own, and — the expensive one —
/// a follower or an activity entry leaking onto Home because the gate was written against the display category. Each of
/// those gets an invariant rather than an example.</para>
/// </summary>
public class HomeTimelineMergeTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Local wall-clock instants, so every day-grouping assertion holds in any time zone the test box happens to be in.
    static long At(int year, int month, int day, int hour = 12, int minute = 0)
        => new DateTimeOffset(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    static NewReleaseNotification Release(string id, long ts, bool unread = false)
        => new(id, ts, unread, NewReleaseKind.Album, "spotify:album:" + id, "Album " + id, null, "Some Artist", "ALBUM", false);

    static SocialNotification Concert(string id, long ts, bool unread = false, string? title = null, string? act = null)
        => new(id, ts, unread, title ?? "Just days away: someone live in New York",
               "spotify:concert:" + id, SocialActionType.NavigateWebview, null,
               act is null ? Array.Empty<string>() : new[] { act }, "stor-" + id);

    static SocialNotification Follower(string id, long ts, bool unread = false)
        => new(id, ts, unread, "someone started following you", "spotify:user:" + id,
               SocialActionType.Navigate, null, new[] { "someone" }, "stor-" + id);

    static AppUpdateNotification Update(long ts)
        => new(ts, true, AppUpdateSnapshot.Idle with { State = AppUpdateState.Available, TargetQuad = "9.9.9" });

    static ActivityNotification Activity(long id, long ts)
        => new(new ActivityEntry(id, ActivityKind.Save, "spotify:track:t" + id, "A song", null, ts, ActivityStatus.Done, false));

    // ── the gate: what is timeline material ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyConcertUpdates_JoinTheReleases_EverythingElseStaysInTheCenter()
    {
        long t = At(2026, 8, 6);
        var feed = HomeTimelineMerge.Build(
        [
            Update(long.MaxValue),
            Release("r1", t),
            Concert("c1", t - 1000),
            Follower("f1", t - 2000),
            Activity(7, t - 3000),
        ]);

        var kinds = feed.Groups.SelectMany(g => g.Rows).ToArray();
        Assert.Equal(2, kinds.Length);
        Assert.Equal(new[] { "r1", "c1" }, kinds.Select(r => r.Id).ToArray());
        Assert.Equal(HomeTimelineKind.Release, kinds[0].Kind);
        Assert.Equal(HomeTimelineKind.Concert, kinds[1].Kind);
    }

    [Fact]
    public void ASocialItemIsGatedOnItsTarget_NotOnItsCategory()
    {
        // Every one of these is NotificationCategory.Social — the "Spotify" pill. Only the concert-shaped targets are
        // timeline material, which is the entire point of classifying rather than reading the pill.
        Assert.All(new[]
        {
            "spotify:concert:abc",
            "https://concerts.spotify.com/event/abc",
            "https://www.example.com/concert/123",
            "https://tickets.example.com/concerts/abc",
        }, uri => Assert.True(SpotifyUpdates.IsConcertTarget(uri), uri));

        Assert.All(new[] { null, "", "spotify:user:someone", "spotify:artist:abc", "spotify:playlist:abc", "https://open.spotify.com/user/x" },
            uri => Assert.False(SpotifyUpdates.IsConcertTarget(uri), uri ?? "<null>"));
    }

    [Fact]
    public void TheServersOwnDiscriminatorWins_WhenThePayloadShipsOne()
    {
        // A concert announcement whose action target we do not recognise still qualifies when the feed labelled it.
        var labelled = Follower("x1", At(2026, 8, 6)) with { WireType = "CONCERT_ANNOUNCEMENT" };
        Assert.True(SpotifyUpdates.IsConcert(labelled));

        // ...and an unlabelled, unrecognised one does not. A missing discriminator is not a licence to guess.
        Assert.False(SpotifyUpdates.IsConcert(Follower("x2", At(2026, 8, 6))));
        Assert.False(SpotifyUpdates.IsConcertWireType("SOCIAL_FOLLOW"));
        Assert.False(SpotifyUpdates.IsConcertWireType(null));
    }

    [Fact]
    public void AnEmptySpotifyCategory_LeavesTheModuleExactlyAsItWas()
    {
        long t = At(2026, 8, 6);
        var releasesOnly = new WaveeNotification[] { Release("r1", t), Release("r2", t - 1000) };
        var withNoise = new WaveeNotification[] { Release("r1", t), Release("r2", t - 1000), Follower("f1", t - 500), Activity(3, t - 600) };

        var a = HomeTimelineMerge.Build(releasesOnly);
        var b = HomeTimelineMerge.Build(withNoise);

        Assert.Equal(a.Shown, b.Shown);
        Assert.Equal(a.Total, b.Total);
        Assert.Equal(a.Unread, b.Unread);
        Assert.Equal(a.Groups.Length, b.Groups.Length);
        Assert.Equal(a.Groups.SelectMany(g => g.Rows).Select(r => r.Id),
                     b.Groups.SelectMany(g => g.Rows).Select(r => r.Id));
    }

    [Fact]
    public void NothingEligible_IsEmpty_NotAnEmptyGroup()
    {
        Assert.True(HomeTimelineMerge.Build(null).IsEmpty);
        Assert.True(HomeTimelineMerge.Build(Array.Empty<WaveeNotification>()).IsEmpty);

        var noise = HomeTimelineMerge.Build([Update(long.MaxValue), Follower("f1", At(2026, 8, 6)), Activity(1, At(2026, 8, 6))]);
        Assert.True(noise.IsEmpty);
        Assert.Empty(noise.Groups);
        Assert.Equal(0, noise.Total);
        Assert.Equal(0, noise.Unread);
    }

    // ── ordering + day grouping ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARowSortsByItsOwnInstant_NotByTheDateItTalksAbout()
    {
        // A "days away" reminder for a concert on the 15th, which ARRIVED on the 6th, belongs on the 6th — the timeline
        // is news, not a calendar. The merge only ever sees the notification timestamp, so this pins that the module
        // never grew a second date source: the row lands under the day it was delivered.
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

        var order = feed.Groups.SelectMany(g => g.Rows).Select(r => r.Id).ToArray();
        Assert.Equal(new[] { "c-new", "r-mid", "c-old", "r-old" }, order);

        // Two days, newest day first, and every row inside a group really belongs to that day.
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
        // 23:59 and 00:01 either side of one local midnight are TWO groups — the conversion, not UTC's day boundary,
        // is what decides (an evening release must not fall under "yesterday" east of UTC).
        var feed = HomeTimelineMerge.Build([Release("late", At(2026, 8, 5, 23, 59)), Release("early", At(2026, 8, 6, 0, 1))]);
        Assert.Equal(2, feed.Groups.Length);
        Assert.Equal(new[] { "early", "late" }, feed.Groups.SelectMany(g => g.Rows).Select(r => r.Id).ToArray());
    }

    // ── the cap and the counter ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCapBoundsTheROWS_ButTheCounterDescribesTheWholeFeed()
    {
        var items = new List<WaveeNotification>();
        for (int i = 0; i < 12; i++) items.Add(Release("r" + i.ToString("00"), At(2026, 8, 6, 23) - i * 60_000, unread: i < 3));
        for (int i = 0; i < 4; i++) items.Add(Concert("c" + i, At(2026, 8, 6, 20) - i * 60_000, unread: i < 2));

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
            Update(long.MaxValue),                    // ditto
        ]);

        Assert.Equal(2, feed.Total);
        Assert.Equal(2, feed.Unread);
    }

    // ── the row's presentation inputs ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LeadingGlyphsAreStripped_ButTheProseIsNeverRecased()
    {
        Assert.Equal("Just days away: Porter Robinson live in New York on Sat, Aug 15",
            SpotifyUpdates.CleanTitle("⏰ Just days away: Porter Robinson live in New York on Sat, Aug 15"));
        Assert.Equal("New Keenan Te show just announced near you. Save the date!",
            SpotifyUpdates.CleanTitle("\U0001F3B5 New Keenan Te show just announced near you. Save the date!"));
        Assert.Equal("New show announced", SpotifyUpdates.CleanTitle("⏰️  New show announced"));

        // A title that needs nothing is returned as-is (reference equality: the common path allocates nothing).
        const string plain = "iPhone tickets on sale";
        Assert.Same(plain, SpotifyUpdates.CleanTitle(plain));

        // ...and a glyph INSIDE the sentence is the server's prose, not our business.
        Assert.Equal("Tickets ❤ on sale", SpotifyUpdates.CleanTitle("Tickets ❤ on sale"));

        // Never blanks a row.
        Assert.Equal("⏰", SpotifyUpdates.CleanTitle("⏰"));
        Assert.Equal("", SpotifyUpdates.CleanTitle(null));
    }

    [Fact]
    public void TheSourceLineIsTheActWhenTheFeedNamedOne()
    {
        Assert.Equal("Porter Robinson", SpotifyUpdates.ActName(Concert("c1", At(2026, 8, 6), act: "Porter Robinson")));
        Assert.Null(SpotifyUpdates.ActName(Concert("c2", At(2026, 8, 6))));
        Assert.Null(SpotifyUpdates.ActName(null));
    }

    // ── the shared read state ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkingOneRowRead_IsAppliedByTheOneMerge_SoEverySurfaceAgrees()
    {
        long t = At(2026, 8, 6);
        var social = new[] { Concert("c1", t, unread: true), Concert("c2", t - 1000, unread: true) };
        var whatsNew = new[] { Release("r1", t - 2000, unread: true) };

        var (before, unreadBefore) = NotificationMerge.Build(null, social, 0, whatsNew, 0, Array.Empty<ActivityEntry>());
        Assert.Equal(3, unreadBefore);
        Assert.All(before, n => Assert.True(n.IsUnread));

        // The same list, re-merged with c1 individually marked: the center's row, the bell's count and the timeline's
        // pip all come out of THIS, so one write moves all three.
        string readIds = NotificationReadIds.Add("", "c1");
        var (after, unreadAfter) = NotificationMerge.Build(null, social, 0, whatsNew, 0, Array.Empty<ActivityEntry>(), readIds);

        Assert.Equal(2, unreadAfter);
        Assert.False(after.First(n => n.Id == "c1").IsUnread);
        Assert.True(after.First(n => n.Id == "c2").IsUnread);
        Assert.Equal(2, HomeTimelineMerge.Build(after).Unread);
        Assert.Equal(3, HomeTimelineMerge.Build(after).Total);
    }

    [Fact]
    public void TheReadSetIsIdempotent_Bounded_AndRefusesASeparator()
    {
        Assert.Equal("a", NotificationReadIds.Add("", "a"));
        Assert.Equal("a", NotificationReadIds.Add("a", "a"));            // idempotent — never grows
        Assert.Equal("a\nb", NotificationReadIds.Add("a", "b"));
        Assert.Equal("a", NotificationReadIds.Add("a", "b\nc"));         // refused, set unchanged
        Assert.Equal("a", NotificationReadIds.Add("a", ""));
        Assert.False(NotificationReadIds.Contains("ab", "a"));           // a prefix is not a member
        Assert.False(NotificationReadIds.Contains(null, "a"));

        string set = "";
        for (int i = 0; i < NotificationReadIds.Cap + 25; i++) set = NotificationReadIds.Add(set, "id" + i);
        var ids = NotificationReadIds.Parse(set);
        Assert.Equal(NotificationReadIds.Cap, ids.Count);
        Assert.Equal("id" + (NotificationReadIds.Cap + 24), ids[^1]);    // newest kept
        Assert.Equal("id25", ids[0]);                                    // oldest dropped
        Assert.True(NotificationReadIds.Contains(set, "id" + (NotificationReadIds.Cap + 24)));
        Assert.False(NotificationReadIds.Contains(set, "id0"));
    }
}
