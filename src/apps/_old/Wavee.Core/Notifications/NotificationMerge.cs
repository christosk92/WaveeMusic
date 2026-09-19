using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>The pure notification aggregation: merge the four category snapshots into one newest-first list + the total
/// unread count, applying the remote-feed last-seen watermarks. Engine-free so it is unit-testable on its own; the
/// <c>NotificationCenterBridge</c> is the thin reactive wrapper around it.</summary>
public static class NotificationMerge
{
    /// <param name="readIds">The per-item read set (<see cref="NotificationReadIds"/>) — the OTHER half of the remote
    /// feeds' local read state, written when a single row is marked seen anywhere in the app. Applied on top of the
    /// watermarks so both halves answer through this one merge and every surface reads the same <c>IsUnread</c>.</param>
    public static (IReadOnlyList<WaveeNotification> Items, int Unread) Build(
        AppUpdateNotification? update,
        IReadOnlyList<SocialNotification> social, long ganderSeenMs,
        IReadOnlyList<NewReleaseNotification> whatsNew, long whatsNewSeenMs,
        IReadOnlyList<ActivityEntry> activity,
        string? readIds = null)
    {
        var list = new List<WaveeNotification>(
            (update is null ? 0 : 1) + social.Count + whatsNew.Count + activity.Count);

        if (update is not null) list.Add(update);
        foreach (var s in social)
            list.Add(s with { IsUnread = s.IsUnread && s.Timestamp > ganderSeenMs && !NotificationReadIds.Contains(readIds, s.Id) });
        foreach (var n in whatsNew)
            list.Add(n with { IsUnread = n.IsUnread && n.Timestamp > whatsNewSeenMs && !NotificationReadIds.Contains(readIds, n.Id) });
        foreach (var e in activity) list.Add(new ActivityNotification(e));

        list.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));

        // Local activity remains visible (and may retain its unread dot inside the panel), but it is informational:
        // routine actions such as liking a song must not increase the attention-seeking bell badge.
        int unread = 0;
        foreach (var x in list)
            if (x.IsUnread && x.Category != NotificationCategory.Activity)
                unread++;
        return (list, unread);
    }
}
