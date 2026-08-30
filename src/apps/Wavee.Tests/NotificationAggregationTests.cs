using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The pure NotificationMerge: merge order (newest-first, update pinned), unread math against the last-seen watermarks,
// and category tagging. Engine-free — the bridge is just the reactive wrapper around this.
public class NotificationAggregationTests
{
    static SocialNotification Social(string id, long ts, bool isNew) =>
        new(id, ts, isNew, "title-" + id, "spotify:user:x", SocialActionType.NavigateWebview, null, Array.Empty<string>(), null);

    static NewReleaseNotification Release(string id, long ts, bool unread) =>
        new(id, ts, unread, NewReleaseKind.Album, "spotify:album:" + id, "Album " + id, null, "Artist", "ALBUM", false);

    static ActivityEntry Activity(long id, long ts, bool read) =>
        new(id, ActivityKind.Save, "spotify:track:" + id, null, null, ts, ActivityStatus.Done, read);

    [Fact]
    public void Build_SortsNewestFirst_WithUpdatePinnedTop()
    {
        var update = new AppUpdateNotification(long.MaxValue, true,
            AppUpdateSnapshot.Idle with { State = AppUpdateState.Available, TargetQuad = "1.2.3" });
        var (items, _) = NotificationMerge.Build(
            update,
            new[] { Social("s", 100, true) }, 0,
            new[] { Release("r", 300, true) }, 0,
            new[] { Activity(1, 200, false) });

        Assert.Equal(4, items.Count);
        Assert.IsType<AppUpdateNotification>(items[0]);         // pinned (MaxValue timestamp)
        Assert.Equal("spotify:album:r", ((NewReleaseNotification)items[1]).Uri);   // 300
        Assert.IsType<ActivityNotification>(items[2]);          // 200
        Assert.IsType<SocialNotification>(items[3]);            // 100
    }

    [Fact]
    public void Unread_Social_GatedByGanderLastSeen()
    {
        // isNew but at/under the watermark → read; isNew and above → unread.
        var (_, unreadLow) = NotificationMerge.Build(null,
            new[] { Social("a", 50, true) }, ganderSeenMs: 100,
            Array.Empty<NewReleaseNotification>(), 0, Array.Empty<ActivityEntry>());
        Assert.Equal(0, unreadLow);

        var (items, unreadHigh) = NotificationMerge.Build(null,
            new[] { Social("a", 150, true) }, ganderSeenMs: 100,
            Array.Empty<NewReleaseNotification>(), 0, Array.Empty<ActivityEntry>());
        Assert.Equal(1, unreadHigh);
        Assert.True(items[0].IsUnread);
    }

    [Fact]
    public void Unread_Social_ServerNotNew_NeverCounts()
    {
        var (_, unread) = NotificationMerge.Build(null,
            new[] { Social("a", 150, false) }, ganderSeenMs: 0,
            Array.Empty<NewReleaseNotification>(), 0, Array.Empty<ActivityEntry>());
        Assert.Equal(0, unread);   // server said not-new → never unread regardless of watermark
    }

    [Fact]
    public void Unread_WhatsNew_GatedByWhatsNewLastSeen()
    {
        var (_, unread) = NotificationMerge.Build(null,
            Array.Empty<SocialNotification>(), 0,
            new[] { Release("r", 500, true), Release("r2", 10, true) }, whatsNewSeenMs: 100,
            Array.Empty<ActivityEntry>());
        Assert.Equal(1, unread);   // only the one above the watermark
    }

    [Fact]
    public void Unread_Activity_RemainsUnreadInPanel_ButDoesNotCountTowardBellBadge()
    {
        var (items, unread) = NotificationMerge.Build(null,
            Array.Empty<SocialNotification>(), 0,
            Array.Empty<NewReleaseNotification>(), 0,
            new[] { Activity(1, 10, read: false), Activity(2, 20, read: true) });

        Assert.Equal(0, unread);
        Assert.True(Assert.IsType<ActivityNotification>(items[1]).IsUnread);
    }

    [Fact]
    public void Categories_AreTaggedCorrectly()
    {
        var (items, _) = NotificationMerge.Build(
            new AppUpdateNotification(long.MaxValue, true, AppUpdateSnapshot.Idle with
            {
                State = AppUpdateState.Failed,
                Failure = new AppUpdateFailure(AppUpdateFailureKind.Network, 0, "boom"),
            }),
            new[] { Social("s", 100, true) }, 0,
            new[] { Release("r", 90, true) }, 0,
            new[] { Activity(1, 80, false) });

        Assert.Equal(NotificationCategory.AppUpdate, items[0].Category);
        Assert.Contains(items, i => i.Category == NotificationCategory.Social);
        Assert.Contains(items, i => i.Category == NotificationCategory.NewRelease);
        Assert.Contains(items, i => i.Category == NotificationCategory.Activity);
    }
}
