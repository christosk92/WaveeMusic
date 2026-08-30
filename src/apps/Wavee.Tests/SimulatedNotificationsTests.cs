using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The "Send event" builders. Every assertion here covers something that fails SILENTLY in the real pipeline — a simulated
// event that classifies as the wrong topic, or arrives already-read, tests the wrong dial (or nothing) while looking fine.
public class SimulatedNotificationsTests
{
    static readonly SimSeed Real = new("spotify:album:abc", "Random Access Memories", "https://i.scdn.co/image/x", "Daft Punk");
    // A concert is seeded from an ARTIST, so its Name IS the act — that is how NotificationSimulator.ArtistSeed builds it.
    static readonly SimSeed Act = new("spotify:artist:abc", "Daft Punk", "https://i.scdn.co/image/a", "Daft Punk");
    static readonly SimSeed None = default;

    static (IReadOnlyList<WaveeNotification> Items, int Unread) Merge(
        WaveeNotification n, long ganderSeen = 0, long whatsNewSeen = 0)
        => NotificationMerge.Build(
            n as AppUpdateNotification,
            n is SocialNotification s ? [s] : Array.Empty<SocialNotification>(), ganderSeen,
            n is NewReleaseNotification r ? [r] : Array.Empty<NewReleaseNotification>(), whatsNewSeen,
            Array.Empty<ActivityEntry>());

    // ── invariant 1: the timestamp must beat both watermarks, or the row arrives already-read ────────────────────────

    [Fact]
    public void ATimestampBeatsBothWatermarks_EvenWhenTheyAreInTheFuture()
    {
        // Both watermarks are stamped to "now" whenever the panel is opened or Mark-all-read is pressed, so a plain
        // UtcNow can tie or lose. Build gates unread on STRICTLY greater.
        long now = 1_000_000;

        Assert.True(SimulatedNotifications.NextTimestamp(now, 0, 0) > 0);
        Assert.True(SimulatedNotifications.NextTimestamp(now, now, now) > now);            // same-millisecond press
        Assert.True(SimulatedNotifications.NextTimestamp(now, now + 5_000, 0) > now + 5_000);
        Assert.True(SimulatedNotifications.NextTimestamp(now, 0, now + 5_000) > now + 5_000);
    }

    [Fact]
    public void ANormalPress_UsesNowRatherThanDriftingIntoTheFuture()
    {
        long now = 1_000_000;
        Assert.Equal(now, SimulatedNotifications.NextTimestamp(now, now - 10, now - 20));
    }

    [Fact]
    public void ASimulatedRow_SurvivesTheRealMergeAsUnread()
    {
        // End-to-end through the merge the app actually uses, at the worst-case watermark (a panel opened this instant).
        long seen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long ts = SimulatedNotifications.NextTimestamp(seen, seen, seen);

        foreach (var n in new WaveeNotification[]
                 {
                     SimulatedNotifications.NewRelease(Real, episode: false, ts, 1),
                     SimulatedNotifications.NewRelease(Real, episode: true, ts, 2),
                     SimulatedNotifications.Concert(Real, ts, 3),
                     SimulatedNotifications.Follower(ts, 4),
                 })
        {
            var (items, _) = Merge(n, ganderSeen: seen, whatsNewSeen: seen);
            var row = Assert.Single(items);
            Assert.True(row.IsUnread, n.GetType().Name + " arrived already-read — it would never escalate");
        }
    }

    // ── invariant 2: unique ids, or Windows replaces the previous banner ────────────────────────────────────────────

    [Fact]
    public void SuccessivePresses_ProduceDistinctIds()
    {
        // The merge does not dedup, the panel keys rows on "ntf:" + Id, and the toast tag is "live:" + Id — a reused id
        // makes the second press look like it did nothing.
        var ids = new[]
        {
            SimulatedNotifications.NewRelease(Real, false, 1, 1).Id,
            SimulatedNotifications.NewRelease(Real, false, 2, 2).Id,
            SimulatedNotifications.NewRelease(Real, true, 3, 3).Id,
            SimulatedNotifications.Concert(Real, 4, 4).Id,
            SimulatedNotifications.Follower(5, 5).Id,
        };

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith(SimulatedNotifications.IdPrefix, id, StringComparison.Ordinal));
    }

    [Fact]
    public void TwoRowsFromSeparatePresses_BothSurviveTheMerge()
    {
        var a = SimulatedNotifications.Concert(Real, 10, 1);
        var b = SimulatedNotifications.Concert(Real, 11, 2);

        var (items, _) = NotificationMerge.Build(null, [a, b], 0,
            Array.Empty<NewReleaseNotification>(), 0, Array.Empty<ActivityEntry>());

        Assert.Equal(2, items.Count);
    }

    // ── invariant 3: the topic is DERIVED from content ──────────────────────────────────────────────────────────────

    [Fact]
    public void AConcertBuild_ClassifiesAsAConcert()
    {
        // This is what routes the event to the Concerts dial. It must come from the wire type / action target — never the
        // title, which is server-localized prose in the real feed.
        Assert.True(SpotifyUpdates.IsConcert(SimulatedNotifications.Concert(Real, 1, 1)));
        Assert.True(SpotifyUpdates.IsConcert(SimulatedNotifications.Concert(None, 1, 1)));
        Assert.True(SpotifyUpdates.IsConcertWireType(SimulatedNotifications.ConcertWireType));
    }

    [Fact]
    public void AFollowerBuild_IsNotAConcert()
    {
        // A social row with no concert markers lands on Followers. Getting this wrong silently tests the other dial.
        Assert.False(SpotifyUpdates.IsConcert(SimulatedNotifications.Follower(1, 1)));
    }

    [Fact]
    public void EpisodesAndAlbums_CarryTheKindThatSplitsTheirDials()
    {
        Assert.Equal(NewReleaseKind.Album, SimulatedNotifications.NewRelease(Real, episode: false, 1, 1).Kind);
        Assert.Equal(NewReleaseKind.Episode, SimulatedNotifications.NewRelease(Real, episode: true, 1, 1).Kind);
    }

    [Fact]
    public void TheUpdateBuild_IsAvailableNotFailed()
    {
        // ToastEscalator.Present yields an empty title for Failed, which never banners — that would read as a broken test.
        var u = SimulatedNotifications.AppUpdate("1.2.3", 1);

        Assert.Equal(AppUpdateState.Available, u.Snapshot.State);
        Assert.Equal("1.2.3", u.Snapshot.TargetQuad);
        Assert.True(u.IsUnread);
        Assert.Equal(NotificationCategory.AppUpdate, u.Category);
    }

    [Fact]
    public void AMissingVersion_StillProducesATitleableUpdate()
    {
        Assert.False(string.IsNullOrEmpty(SimulatedNotifications.AppUpdate(null, 1).Snapshot.TargetQuad));
    }

    // ── seeds and safety ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RealSeedData_IsCarriedThrough_SoTheRowIsClickable()
    {
        var r = SimulatedNotifications.NewRelease(Real, episode: false, 1, 1);

        Assert.Equal(Real.Uri, r.Uri);            // the click target — a placeholder would go nowhere
        Assert.Equal(Real.Name, r.Name);
        Assert.Equal(Real.ImageUrl, r.ImageUrl);
        Assert.Equal("Daft Punk", r.CreatorName);
        Assert.Contains("Daft Punk", SimulatedNotifications.Concert(Act, 1, 1).Title, StringComparison.Ordinal);
        Assert.Equal(Act.Uri, SimulatedNotifications.Concert(Act, 1, 1).ActionUri);
    }

    [Fact]
    public void AnEmptyLibrary_FallsBackToAMarkedPlaceholder_WithoutAFakeClickTarget()
    {
        var r = SimulatedNotifications.NewRelease(None, episode: false, 1, 1);
        var c = SimulatedNotifications.Concert(None, 1, 1);

        Assert.Contains("simulated", r.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Null(c.ActionUri);   // nowhere real to go, so it offers no navigation rather than a dead one
    }

    [Fact]
    public void TheActivityTarget_IsUnresolvable_SoUndoCanNeverTouchSomethingReal()
    {
        // The undo for a real save calls SetSaved(uri, false). A simulated entry must not be able to unsave a real album;
        // an unresolvable target is never in the saved set, so the inverse is a no-op.
        string uri = SimulatedNotifications.ActivityTargetUri(7);

        Assert.StartsWith("wavee:simulated:", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify:", uri, StringComparison.Ordinal);
        Assert.NotEqual(uri, SimulatedNotifications.ActivityTargetUri(8));
    }

    [Fact]
    public void ASimulatedActivityKind_IsNeverUndoable()
    {
        // PlaylistCreate is the kind the simulator records, precisely because IsUndoable excludes it — so no Undo button
        // is offered at all, and there is no inverse to get wrong.
        var e = new ActivityEntry(1, ActivityKind.PlaylistCreate, SimulatedNotifications.ActivityTargetUri(1),
            "Simulated activity", null, 1, ActivityStatus.Done, Read: false);

        Assert.False(e.IsUndoable);
    }
}
